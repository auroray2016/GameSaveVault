using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameSaveManager.Services;

/// <summary>AppId 搜索数据源定义（内置 + 用户自定义统一结构）。返回的是候选列表（名字 → AppId）。</summary>
public class AppIdSourceDef
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool BuiltIn { get; set; }
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public string UrlTemplate { get; set; } = "";   // 占位符 {name}、{name_urlenc}
    public string ListPath { get; set; } = "";       // 候选数组的 JSON 路径，如 data.results
    public string IdField { get; set; } = "id";      // 数组项中 AppId 字段（字符串或数字均可）
    public string NameField { get; set; } = "name";  // 数组项中名称字段
    public string ImageField { get; set; } = "";     // 数组项中封面字段（可空）
    public string Referer { get; set; } = "";
    public string Encoding { get; set; } = "utf-8";
    public int TimeoutSec { get; set; } = 10;

    public bool HasImage => !string.IsNullOrEmpty(ImageField);
}

/// <summary>AppId 搜索候选。Approximate=true 表示由中文前缀缩短近似命中，需用户核对。</summary>
public record AppIdCandidate(int AppId, string Name, string Image, string Source, bool Approximate = false);

/// <summary>
/// AppId 搜索多源服务：按游戏名搜索 Steam AppId 候选列表。
/// 故障弹性与中文名源一致：单源隔离、会话健康度（连续失败 2 次跳过）、首个有结果的源胜出。
/// 注：小黑盒「按名搜索」接口强制登录，无法匿名使用，故不内置；其免登录的 appid→详情 用于中文名源。
/// </summary>
public static class AppIdSourceService
{
    private static readonly Dictionary<string, int> FailCount = new();
    private const int FailThreshold = 2;

    public static List<AppIdSourceDef> BuiltInSources() => new()
    {
        // Steam 官方商店搜索：免 Referer、中英文均可、cc=hk 避开区域锁导致主游戏被过滤
        new AppIdSourceDef
        {
            Id = "steam_store", DisplayName = "Steam 官方商店搜索", BuiltIn = true, Priority = 5,
            UrlTemplate = "https://store.steampowered.com/api/storesearch/?term={name_urlenc}&l=schinese&cc=hk",
            ListPath = "items", IdField = "id", NameField = "name", ImageField = "tiny_image",
        },
        // 补充源：SteamTools 现要求带 Referer，否则一律 403
        new AppIdSourceDef
        {
            Id = "steamtools", DisplayName = "SteamTools（国内直连）", BuiltIn = true, Priority = 10,
            UrlTemplate = "https://steamtools.games/api/search?query={name_urlenc}",
            ListPath = "data.results", IdField = "id", NameField = "name", ImageField = "image",
            Referer = "https://steamtools.games/",
        },
    };

    public static List<AppIdSourceDef> GetSources()
    {
        var list = Store.Settings.CustomAppIdSources ?? new List<AppIdSourceDef>();
        var byId = list.Where(s => !string.IsNullOrEmpty(s.Id)).ToDictionary(s => s.Id, s => s);
        foreach (var b in BuiltInSources())
        {
            if (byId.TryGetValue(b.Id, out var ex))
            {
                ex.DisplayName = b.DisplayName; ex.BuiltIn = true;
                ex.UrlTemplate = b.UrlTemplate; ex.ListPath = b.ListPath;
                ex.IdField = b.IdField; ex.NameField = b.NameField; ex.ImageField = b.ImageField;
                ex.Referer = b.Referer;
            }
            else list.Add(b);
        }
        Store.Settings.CustomAppIdSources = list;
        return list.OrderBy(s => s.Priority).ToList();
    }

    public static void SaveSources(List<AppIdSourceDef> sources)
    {
        Store.Settings.CustomAppIdSources = sources;
        Store.SaveSettings();
        lock (FailCount) FailCount.Clear();
    }

    private static bool IsHealthy(string id)
    {
        lock (FailCount) return !(FailCount.TryGetValue(id, out var c) && c >= FailThreshold);
    }

    private static void Record(string id, bool ok)
    {
        lock (FailCount)
        {
            if (ok) FailCount[id] = 0;
            else FailCount[id] = (FailCount.TryGetValue(id, out var c) ? c : 0) + 1;
        }
    }

    /// <summary>按游戏名搜索候选；自动模式按优先级尝试，首个有结果的源胜出。
    /// 0 条时依次回退：① 无空格英文名大写边界插空格 ② 中文查询逐字缩短前缀（近似命中打标记）。</summary>
    public static async Task<List<AppIdCandidate>> SearchAsync(string query)
    {
        var r = await SearchOnceAsync(query);
        if (r.Count > 0) return r;

        // 回退1：无空格英文名 → 大写边界插空格
        if (!query.Contains(' ') && query.Length > 4)
        {
            string spaced = SteamApi.InsertSpacesAtCaseBoundaries(query);
            if (spaced != query)
            {
                r = await SearchOnceAsync(spaced);
                if (r.Count > 0) return r;
            }
        }

        // 回退2：中文查询 0 条 → 逐字缩短前缀（官方名带标点，缺冒号会匹配不到）
        foreach (var prefix in PrefixCandidates(query))
        {
            var pr = await SearchOnceAsync(prefix);
            if (pr.Count > 0)
                return pr.Select(c => c with { Approximate = true }).ToList();
        }
        return r;
    }

    /// <summary>中文前缀缩短候选序列：逐字去尾（最短保留 2 字），去重。如「黑神话悟空」→ 黑神话悟、黑神话。</summary>
    internal static List<string> PrefixCandidates(string query)
    {
        var list = new List<string>();
        if (!HasCjk(query)) return list;
        for (int len = query.Length - 1; len >= 2; len--)
        {
            string p = query[..len].TrimEnd();
            if (p.Length >= 2 && !list.Contains(p, StringComparer.Ordinal)) list.Add(p);
        }
        return list;
    }

    /// <summary>是否含中日韩文字。</summary>
    private static bool HasCjk(string s) =>
        s.Any(c => c >= 0x4E00 && c <= 0x9FFF || c >= 0x3400 && c <= 0x4DBF);

    /// <summary>仅去掉含中文的括号组（全角/半角），其余保留（供分类前缀比较）。</summary>
    private static string StripCjkParens(string raw)
    {
        string s = Regex.Replace(raw, @"（[^（）]*）", m => HasCjk(m.Value) ? " " : m.Value);
        s = Regex.Replace(s, @"\([^()]*\)", m => HasCjk(m.Value) ? " " : m.Value);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>从接口返回名中净化出纯英文名：① 去掉含中文的括号组 ② 截掉括号外的中文后缀
    /// （DLC 中文名等）；如 "Wo Long: Fallen Dynasty （卧龙：苍天陨落）" → "Wo Long: Fallen Dynasty"，
    /// "Wo Long: Fallen Dynasty 风起荆襄" → "Wo Long: Fallen Dynasty"。
    /// 不含英文字母（全中文名或空）时返回空字符串（调用方不应回填英文名框）。</summary>
    internal static string CleanEnglishName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = StripCjkParens(raw);
        for (int i = 1; i < s.Length; i++)
        {
            // 首个落在边界（前一字符非中文）的中文字符：其后视为中文后缀，截断
            if (IsCjk(s[i]) && !IsCjk(s[i - 1])) { s = s[..i]; break; }
        }
        s = s.Trim();
        return s.Any(c => c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z') ? s : "";
    }

    private static bool IsCjk(char c) =>
        c >= 0x4E00 && c <= 0x9FFF || c >= 0x3400 && c <= 0x4DBF;

    /// <summary>按名称判定结果种类：game（主游戏/版本）/season（季票）/dlc（附加内容）/
    /// bundle（捆绑包）/demo（试玩体验）。关键词优先；其次若本名称是另一条结果名的扩展前缀，
    /// 视为附加内容（如「主游戏名 + DLC 中文名」）。</summary>
    internal static string ClassifyResultKind(string name, IEnumerable<string> allNames)
    {
        string lower = name.ToLowerInvariant();
        if (Regex.IsMatch(lower, @"demo|playtest|beta|trial|alpha|α|体验版|试玩|测试")) return "demo";
        if (lower.Contains("season pass") || name.Contains("季票")) return "season";
        if (lower.Contains("bundle") || name.Contains("捆绑包")) return "bundle";
        if (Regex.IsMatch(lower, @"\bdlc\b|add-?on|expansion|map pack|资料片|升级包")) return "dlc";
        // 版本名（完全版/年度版/豪华版等）仍算主游戏，先于前缀判定
        if (Regex.IsMatch(lower,
            @"complete|game of the year|\bgoty\b|deluxe|gold|premium|standard|ultimate|definitive|" +
            "年度|完全|豪华|终极|黄金|标准")) return "game";

        string normSelf = StripCjkParens(name);
        foreach (var other in allNames)
        {
            if (string.Equals(other, name, StringComparison.Ordinal)) continue;
            string normOther = StripCjkParens(other);
            if (normOther.Length >= 4 && normSelf.Length > normOther.Length &&
                normSelf.StartsWith(normOther, StringComparison.OrdinalIgnoreCase) &&
                !char.IsLetterOrDigit(normSelf[normOther.Length])) return "dlc";
        }
        return "game";
    }

    private static async Task<List<AppIdCandidate>> SearchOnceAsync(string query)
    {
        foreach (var src in GetSources().Where(s => s.Enabled && IsHealthy(s.Id)))
        {
            var items = await QuerySource(src, query);
            Record(src.Id, items != null);
            if (items is { Count: > 0 })
                return items;
        }
        return new List<AppIdCandidate>();
    }

    /// <summary>用指定源发一次请求并提取候选列表；网络/解析失败返回 null（区别于"成功但无结果"的空列表）。</summary>
    public static async Task<List<AppIdCandidate>?> QuerySource(AppIdSourceDef src, string query)
    {
        try
        {
            string url = src.UrlTemplate
                .Replace("{name_urlenc}", Uri.EscapeDataString(query))
                .Replace("{name}", query);
            using var http = GameNameService.NewClient(src.TimeoutSec, src.Referer);
            byte[] bytes = await http.GetByteArrayAsync(url);
            Encoding enc;
            if (src.Encoding.Equals("gbk", StringComparison.OrdinalIgnoreCase))
            {
                try { enc = Encoding.GetEncoding("GB18030"); }
                catch { enc = Encoding.UTF8; }
            }
            else enc = Encoding.UTF8;
            string body = enc.GetString(bytes);

            using var doc = JsonDocument.Parse(body);
            var arrEl = GameNameService.NavigateJson(doc.RootElement, src.ListPath);
            if (arrEl == null || arrEl.Value.ValueKind != JsonValueKind.Array)
                return new List<AppIdCandidate>();   // 成功响应但结构里没有数组 = 无结果

            var list = new List<AppIdCandidate>();
            foreach (var item in arrEl.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                int appId = 0;
                if (item.TryGetProperty(src.IdField, out var idEl))
                {
                    if (idEl.ValueKind == JsonValueKind.Number) appId = idEl.GetInt32();
                    else if (idEl.ValueKind == JsonValueKind.String) int.TryParse(idEl.GetString(), out appId);
                }
                if (appId <= 0) continue;
                string name = item.TryGetProperty(src.NameField, out var nEl) && nEl.ValueKind == JsonValueKind.String
                    ? WebUtility.HtmlDecode(nEl.GetString() ?? "") : "";
                string img = "";
                if (!string.IsNullOrEmpty(src.ImageField) &&
                    item.TryGetProperty(src.ImageField, out var imgEl) && imgEl.ValueKind == JsonValueKind.String)
                    img = imgEl.GetString() ?? "";
                list.Add(new AppIdCandidate(appId, name.Trim(), img, src.DisplayName));
            }
            return list;
        }
        catch { return null; }
    }
}
