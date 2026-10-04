using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameSaveManager.Models;

namespace GameSaveManager.Services;

/// <summary>一条从 PCGamingWiki 解析出的原始位置（未还原机器相关变量，便于跨机器缓存）。</summary>
public class WikiEntry
{
    public string Type { get; set; } = "saves";   // saves | config | registry
    public string Raw { get; set; } = "";
}

/// <summary>
/// PCGamingWiki 社区存档规则库（设计漏斗第 3 级）。
/// 通过 MediaWiki API 取页面 wikitext，花括号深度解析 {{Game data/saves|config|registry|Windows|...}}，
/// 还原 {{p|变量}}、&lt;path-to-game&gt;、&lt;Steam-folder&gt;、&lt;user-id&gt; 等模板变量。
/// 结果按游戏缓存原始模板到 wiki-cache.json，离线可用。
/// </summary>
public static class WikiService
{
    private const string Api = "https://www.pcgamingwiki.com/w/api.php";

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        // 部分环境 TLS 1.2 默认未协商
        SslProtocols = System.Security.Authentication.SslProtocols.Tls12,
    })
    { Timeout = TimeSpan.FromSeconds(20) };

    static WikiService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("GameSaveVault/0.1 (local save manager; contact: local)");
    }

    /// <summary>Wiki 请求节流：PCGamingWiki 对启动突发并发会 429（静默丢条目、本周期不重试）；
    /// 全局限 2 并发 + 200ms 间隔，429/超时退避重试最多 3 次。</summary>
    private static readonly SemaphoreSlim _httpGate = new(2);
    private static long _lastRequestTicks;

    private static async Task<string?> WikiGetStringAsync(string url)
    {
        for (int attempt = 0; ; attempt++)
        {
            await _httpGate.WaitAsync();
            try
            {
                long last = Interlocked.Read(ref _lastRequestTicks);
                if (last > 0)
                {
                    int wait = 200 - (int)((DateTime.UtcNow.Ticks - last) / TimeSpan.TicksPerMillisecond);
                    if (wait > 0) await Task.Delay(wait);
                }
                Interlocked.Exchange(ref _lastRequestTicks, DateTime.UtcNow.Ticks);
                return await Http.GetStringAsync(url);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (attempt >= 2) return null;                 // 429/超时：退避后重试
                await Task.Delay(1500 * (attempt + 1));
            }
            finally { _httpGate.Release(); }
        }
    }

    private static string CacheFile => Path.Combine(Store.BaseDir, "wiki-cache.json");

    /// <summary>缓存条目：原始模板 + 抓取时间（TTL 30 天）+ 页面标题 / Steam appid。</summary>
    private class WikiCacheItem
    {
        public DateTime FetchedAt { get; set; } = DateTime.Now;
        public string Title { get; set; } = "";
        public int AppId { get; set; }
        public List<WikiEntry> Entries { get; set; } = new();
    }

    /// <summary>Wiki 查询结果（供学习版联网匹配复用）。</summary>
    public class WikiLookup
    {
        public string Title { get; set; } = "";
        public int AppId { get; set; }
        public List<WikiEntry> Entries { get; set; } = new();
    }

    private static Dictionary<string, WikiCacheItem>? _cache;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

    // ── 公开入口 ──────────────────────────────────────────

    /// <summary>
    /// 查 Wiki 并还原为可备份的存档/注册表路径。
    /// 返回的条目已附加到 game.SavePaths（调用方负责 UI 线程封送/去重）。
    /// 缓存 30 天有效；forceRefresh=true 时强制联网更新（离线回退过期缓存）。
    /// </summary>
    public static async Task<List<SavePathEntry>> EnrichAsync(
        GameEntry g, string? steamPath, string? steamUserId, bool forceRefresh = false)
    {
        var result = new List<SavePathEntry>();
        var item = await GetItemAsync(g.AppId > 0 ? $"app:{g.AppId}" : null, g.NameEN, forceRefresh);

        // 网络失败信号（详情页「Wiki 获取失败·重试」入口依据）：仅在缓存也无数据、
        // 且本次联网确实出错时置位；拿到数据（含过期缓存）一律视为成功。
        LastEnrichFailed = item == null && _fetchErrored;

        if (item == null || item.Entries.Count == 0) return result;

        var ctx = new ResolveCtx
        {
            InstallDir = g.InstallDir ?? "",
            SteamPath = steamPath ?? "",
            UserId = steamUserId ?? "",
            AppId = g.AppId,
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in item.Entries)
        {
            foreach (var raw in SplitCells(e.Raw))
            {
                bool reg = e.Type == "registry";
                string? resolved = Resolve(raw, reg, ctx);
                if (string.IsNullOrWhiteSpace(resolved)) continue;
                if (!seen.Add(reg ? "reg:" + resolved : resolved)) continue;
                result.Add(new SavePathEntry
                {
                    Path = resolved,
                    IsReg = reg,
                    Source = "Wiki",
                });
            }
        }
        return result;
    }

    /// <summary>最近一次 EnrichAsync 是否因网络失败未拿到数据（无缓存可用）。
    /// WikiService 有 Gate 串行保护，调用方在 await 之后立即读取即为本游戏的结果。</summary>
    public static bool LastEnrichFailed { get; private set; }

    private static bool _fetchErrored;   // FetchAsync 最近一次是否抛异常（网络层失败）

    /// <summary>按英文名查 Wiki（学习版联网匹配用）：返回页面标题、Steam appid、存档条目。</summary>
    public static async Task<WikiLookup?> LookupByNameAsync(string nameEN, bool forceRefresh = false)
    {
        var item = await GetItemAsync(null, nameEN, forceRefresh);
        if (item == null) return null;
        return new WikiLookup { Title = item.Title, AppId = item.AppId, Entries = item.Entries };
    }

    /// <summary>取缓存条目：优先 app: 键 → name: 键；过期/强制时联网，失败回退过期缓存（离线可用）。</summary>
    private static async Task<WikiCacheItem?> GetItemAsync(string? appKey, string nameEN, bool forceRefresh)
    {
        if (string.IsNullOrWhiteSpace(nameEN)) return null;
        string nameKey = "name:" + nameEN.Trim().ToLowerInvariant();
        var cache = LoadCache();
        bool Fresh(WikiCacheItem c) => DateTime.Now - c.FetchedAt < CacheTtl;

        if (!forceRefresh)
        {
            if (appKey != null && cache.TryGetValue(appKey, out var a) && Fresh(a)) return a;
            if (cache.TryGetValue(nameKey, out var n) && Fresh(n)) return n;
        }

        var fetched = await FetchAsync(nameEN);
        if (fetched != null && fetched.Entries.Count > 0)
        {
            SaveCache(nameKey, fetched);
            if (fetched.AppId > 0) SaveCache($"app:{fetched.AppId}", fetched);
            return fetched;
        }
        // 联网失败 / 无数据：回退过期缓存
        if (appKey != null && cache.TryGetValue(appKey, out var oa)) return oa;
        return cache.TryGetValue(nameKey, out var on) ? on : null;
    }

    // ── 抓取 + 解析 ───────────────────────────────────────

    private static async Task<WikiCacheItem?> FetchAsync(string gameNameEN)
    {
        if (string.IsNullOrWhiteSpace(gameNameEN)) return null;
        _fetchErrored = false;
        try
        {
            // redirects=1：搜索结果常是重定向页（如 The Witcher 3 → The Witcher 3: Wild Hunt），
            // 不跟随的话拿到的 wikitext 只有 #REDIRECT 一行，解析不到存档表
            string url = Api + "?action=query&generator=search&gsrlimit=3&redirects=1"
                + "&gsrsearch=" + Uri.EscapeDataString(gameNameEN)
                + "&prop=revisions&rvprop=content&rvslots=main&format=json";
            var body = await WikiGetStringAsync(url);
            if (body == null) return null;
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("query", out var q) ||
                !q.TryGetProperty("pages", out var pages)) return null;

            // pages 是以 pageid 为键的 JSON 对象，不是数组
            // 先收集所有有数据的页面，再优先选标题完全相等的（防 "Elden Ring" 搜到 "Elden Ring Nightreign"）
            WikiCacheItem? best = null;
            bool bestExact = false;
            foreach (var prop in pages.EnumerateObject())
            {
                var page = prop.Value;
                if (!page.TryGetProperty("revisions", out var revs) || revs.GetArrayLength() == 0) continue;
                var slot = revs[0].GetProperty("slots").GetProperty("main");
                if (!slot.TryGetProperty("*", out var contentEl)) continue;
                string text = contentEl.GetString() ?? "";
                var parsed = ParseWikitext(text);
                if (parsed.Count == 0) continue;
                string title = page.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var item = new WikiCacheItem
                {
                    FetchedAt = DateTime.Now,
                    Title = title,
                    AppId = ExtractAppId(text),
                    Entries = parsed,
                };
                bool exact = title.Equals(gameNameEN, StringComparison.OrdinalIgnoreCase);
                if (exact && !bestExact) { best = item; bestExact = true; }
                else if (best == null) best = item;
            }
            return best;
        }
        catch (Exception) { _fetchErrored = true; /* 网络失败静默，后续手动 */ }
        return null;
    }

    /// <summary>
    /// 从 wikitext 提取 Steam appid。优先解析 infobox 模板参数 |steam appid = NNNN
    ///（PCGamingWiki 现代页面主要用此形式存放 appid），无则回退到商店链接众数投票。
    /// </summary>
    public static int ExtractAppId(string wikitext)
    {
        // 优先：infobox 模板参数 |steam appid = NNNN
        var paramMatch = Regex.Match(wikitext,
            @"\|\s*steam\s+appid\s*=\s*(\d{2,})\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (paramMatch.Success && int.TryParse(paramMatch.Groups[1].Value, out var pid) && pid > 1000)
            return pid;

        // 回退：商店链接众数投票（兼容 /app/123、/news/app/123）
        var matches = Regex.Matches(wikitext,
            @"store\.steampowered(?:\.com)?/(?:[a-z]+/)*app/(\d+)", RegexOptions.IgnoreCase);
        var counts = new Dictionary<int, int>();
        foreach (Match m in matches)
        {
            if (int.TryParse(m.Groups[1].Value, out var id) && id > 1000)
                counts[id] = counts.TryGetValue(id, out var c) ? c + 1 : 1;
        }
        return counts.Count == 0 ? 0 : counts.OrderByDescending(kv => kv.Value).First().Key;
    }

    /// <summary>搜索 Wiki 返回多条结果（供编辑窗口用户选择）。</summary>
    public class WikiSearchResult
    {
        public string Title { get; set; } = "";
        public int AppId { get; set; }
    }

    public static async Task<List<WikiSearchResult>> SearchAsync(string nameEN)
    {
        var results = new List<WikiSearchResult>();
        if (string.IsNullOrWhiteSpace(nameEN)) return results;
        try
        {
            string url = Api + "?action=query&generator=search&gsrlimit=8&redirects=1"
                + "&gsrsearch=" + Uri.EscapeDataString(nameEN)
                + "&prop=revisions&rvprop=content&rvslots=main&format=json";
            var body = await WikiGetStringAsync(url);
            if (body == null) return results;
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("query", out var q) ||
                !q.TryGetProperty("pages", out var pages)) return results;

            foreach (var prop in pages.EnumerateObject())
            {
                var page = prop.Value;
                if (!page.TryGetProperty("revisions", out var revs) || revs.GetArrayLength() == 0) continue;
                var slot = revs[0].GetProperty("slots").GetProperty("main");
                if (!slot.TryGetProperty("*", out var contentEl)) continue;
                string text = contentEl.GetString() ?? "";
                string title = page.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                int aid = ExtractAppId(text);
                // 只保留有 appid 或有存档模板的页面
                if (aid > 0 || ParseWikitext(text).Count > 0)
                    results.Add(new WikiSearchResult { Title = title, AppId = aid });
            }
        }
        catch { }
        return results;
    }

    /// <summary>从 wikitext 提取 Windows 平台的 saves / config / registry 模板原文。</summary>
    public static List<WikiEntry> ParseWikitext(string text)
    {
        var list = new List<WikiEntry>();
        string[] markers = { "{{game data/saves|", "{{game data/config|", "{{game data/registry|" };
        foreach (var marker in markers)
        {
            string type = marker.Contains("registry") ? "registry"
                        : marker.Contains("config") ? "config" : "saves";
            int idx = 0;
            while (true)
            {
                int start = text.ToLowerInvariant().IndexOf(marker, idx, StringComparison.Ordinal);
                if (start < 0) break;
                string? inner = ExtractTemplate(text, start);
                idx = start + marker.Length;
                if (inner == null) continue;

                // inner 形如 "game data/saves|Windows|...内容..."（首个参数是模板名），
                // 嵌套模板里的 | 由深度感知拆分跳过
                var args = SplitTopLevel(inner);
                if (args.Count < 3) continue;
                string platform = args[1];
                if (!platform.Contains("Windows", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new WikiEntry { Type = type, Raw = string.Join("|", args.Skip(2)) });
            }
        }
        return list;
    }

    /// <summary>从 "{{" 起始位置按花括号深度取到匹配 "}}"，返回内部字符串。</summary>
    private static string? ExtractTemplate(string text, int openStart)
    {
        int depth = 0, i = openStart;
        for (; i + 1 < text.Length; i++)
        {
            if (text[i] == '{' && text[i + 1] == '{') { depth++; i++; }
            else if (text[i] == '}' && text[i + 1] == '}')
            {
                depth--; i++;
                if (depth == 0)
                    return text.Substring(openStart + 2, i - openStart - 3);
            }
        }
        return null;
    }

    /// <summary>按顶层 | 拆分参数（跳过嵌套 {{}}、[[]] 内的 |）。</summary>
    private static List<string> SplitTopLevel(string s)
    {
        var result = new List<string>();
        int brace = 0, bracket = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '{') brace++;
            else if (c == '}') brace = Math.Max(0, brace - 1);
            else if (c == '[') bracket++;
            else if (c == ']') bracket = Math.Max(0, bracket - 1);
            else if (c == '|' && brace == 0 && bracket == 0)
            {
                result.Add(s.Substring(start, i - start));
                start = i + 1;
            }
        }
        result.Add(s.Substring(start));
        return result.Select(x => x.Trim()).ToList();
    }

    // ── 变量还原 ──────────────────────────────────────────

    private class ResolveCtx
    {
        public string InstallDir = "";
        public string SteamPath = "";
        public string UserId = "";
        public int AppId;
    }

    private static IEnumerable<string> SplitCells(string raw)
    {
        // 单元格内多行/多系统路径用 <br> 或 <br /> 分隔
        return Regex.Split(raw, @"<br\s*/?>")
            .Select(s => s.Trim()).Where(s => s.Length > 0);
    }

    private static string? Resolve(string raw, bool reg, ResolveCtx ctx)
    {
        string s = raw;

        // 注释、引用脚注
        s = Regex.Replace(s, @"<!--.*?-->", "", RegexOptions.Singleline);
        s = Regex.Replace(s, @"<ref\b[^>]*/?>.*?</ref>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<ref\b[^>]*/>", "", RegexOptions.IgnoreCase);

        // 已知 PCGW 占位符（HTML 标签清洗之前先替换）
        string instDir = ctx.InstallDir.TrimEnd('\\') + "\\";
        string steamDir = ctx.SteamPath.TrimEnd('\\') + "\\";
        s = Regex.Replace(s, @"<path-to-game>/??", _ => instDir.Replace("$", "$$"), RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<steam-folder>/??", _ => steamDir.Replace("$", "$$"), RegexOptions.IgnoreCase);
        if (ctx.UserId.Length > 0)
            s = Regex.Replace(s, @"<(user-id|steam-id3|steam user id|steamid3)>", ctx.UserId, RegexOptions.IgnoreCase);
        if (ctx.AppId > 0)
            s = Regex.Replace(s, @"<(gameid|steam-appid|app-id|appid)>", ctx.AppId.ToString(), RegexOptions.IgnoreCase);

        // {{p|变量}} 路径模板
        bool nonWindows = false;
        s = Regex.Replace(s, @"\{\{\s*[pP]\s*\|([^{}]*?)\}\}", m =>
        {
            var v = PVar(m.Groups[1].Value.Trim(), ctx);
            if (v == null) { nonWindows = true; return ""; }
            return v;
        });
        if (nonWindows) return null;

        // 其余嵌套模板（脚注/提示）整段移除
        s = Regex.Replace(s, @"\{\{[^{}]*\}\}", "");

        // 维基链接 [[X|显示文字]] → 显示文字
        s = Regex.Replace(s, @"\[\[[^|\]]*\|([^\]]*)\]\]", "$1");
        s = Regex.Replace(s, @"\[\[([^\]]*)\]\]", "$1");

        // 残留 HTML 标签（<code> <sup> 等）
        s = Regex.Replace(s, @"</?[a-z][^>]*>", "", RegexOptions.IgnoreCase);

        // 还有未识别的 <占位符> → 无法可靠还原，丢弃
        if (Regex.IsMatch(s, @"<[a-z0-9 \-]+>", RegexOptions.IgnoreCase)) return null;

        s = System.Net.WebUtility.HtmlDecode(s);
        try { s = Environment.ExpandEnvironmentVariables(s); } catch { }

        // 路径规范化（注册表键也用 \，斜杠一并归一）
        s = s.Replace('/', '\\');
        s = Regex.Replace(s, @"\\{2,}", @"\");
        s = s.Trim().Trim('"').Trim();

        bool looksLikeReg = Regex.IsMatch(s, @"^(HKEY_(CURRENT_USER|LOCAL_MACHINE|USERS|CLASSES_ROOT)|HK(CU|LM|CR|U))\\",
            RegexOptions.IgnoreCase);
        if (looksLikeReg) return s.TrimEnd('\\');   // 注册表键
        if (reg) return null;                        // registry 行但未解析出 HKEY → 丢弃

        if (s.Length < 4 || !s.Contains('\\') || s.StartsWith("\\")) return null;
        if (!Regex.IsMatch(s, @"^[a-zA-Z]:\\")) return null;   // 文件路径必须是盘符绝对路径

        // 末段像文件（local_settings.txt 等）：MVP 备份以目录为单位，文件型跳过
        string lastSeg = s.Split('\\').Last();
        if (Regex.IsMatch(lastSeg, @"\.[a-zA-Z0-9]{1,5}$")) return null;

        return s.TrimEnd('\\');
    }

    /// <summary>还原 {{p|xxx}}；非 Windows 变量返回 null（用于跳过该行）。</summary>
    private static string? PVar(string arg, ResolveCtx ctx)
    {
        string a = arg.ToLowerInvariant().Replace("/", "\\").Trim('\\');
        string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return a switch
        {
            "userprofile" => Profile,
            "userprofile\\documents" or "documents" or "mydocuments"
                => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "appdata" or "roamingappdata"
                => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "localappdata"
                => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "localappdatalow"
                => Path.Combine(Profile, "AppData", "LocalLow"),
            "public" => Environment.ExpandEnvironmentVariables("%PUBLIC%"),
            "windir" => Environment.ExpandEnvironmentVariables("%WINDIR%"),
            "programfiles" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "programfilesx86" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "savedgames" or "userprofile\\saved games"
                => Path.Combine(Profile, "Saved Games"),
            "steam" or "steamfolder" => ctx.SteamPath.TrimEnd('\\'),
            _ when a.Contains("osx") || a.Contains("linux") || a.Contains("home") => null,
            _ => Environment.ExpandEnvironmentVariables("%" + arg.ToUpperInvariant().Replace("\\", "_") + "%")
        };
    }

    // ── 缓存 ──────────────────────────────────────────────

    private static Dictionary<string, WikiCacheItem> LoadCache()
    {
        if (_cache != null) return _cache;
        _cache = new Dictionary<string, WikiCacheItem>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(CacheFile))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, WikiCacheItem>>(
                    File.ReadAllText(CacheFile));
                if (d != null) _cache = new Dictionary<string, WikiCacheItem>(d, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { /* 旧格式（裸数组）/ 损坏则重建 */ }
        return _cache;
    }

    private static void SaveCache(string key, WikiCacheItem item)
    {
        var cache = LoadCache();
        cache[key] = item;
        try
        {
            Directory.CreateDirectory(Store.BaseDir);
            string json = JsonSerializer.Serialize(cache, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            string tmp = CacheFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, CacheFile, overwrite: true);
        }
        catch { }
    }
}
