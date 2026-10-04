using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameSaveManager.Services;

/// <summary>中文名数据源选择（同步按钮快捷菜单）。</summary>
public enum CnNameSource
{
    Auto = 0,      // 自动：本地库 → 各启用源按优先级依次尝试
    Xiaoheihe = 1, // 仅小黑盒
    Steam = 2,     // 仅 Steam 两个源
}

/// <summary>
/// 中文名数据源定义（内置 + 用户自定义统一结构）。
/// URL 模板占位符：{appid}、{name}、{name_urlenc}；JSON 提取路径同样支持 {appid}（如 {appid}.data.name）。
/// </summary>
public class NameSourceDef
{
    public string Id { get; set; } = "";              // 唯一标识（内置源固定 id）
    public string DisplayName { get; set; } = "";
    public bool BuiltIn { get; set; }                 // 内置源不可删，技术字段随程序升级
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }                 // 越小越先尝试
    public string UrlTemplate { get; set; } = "";
    public string ExtractType { get; set; } = "json"; // "json" | "regex"
    public string ExtractExpr { get; set; } = "";     // JSON 路径（result.name）或正则（取捕获组 1）
    public string Referer { get; set; } = "";
    public string Encoding { get; set; } = "utf-8";   // "utf-8" | "gbk"
    public int TimeoutSec { get; set; } = 8;

    public bool IsJson => ExtractType == "json";
    public bool IsRegex => ExtractType == "regex";
}

/// <summary>中文名查询结果。</summary>
public record CnNameResult(string? Name, string Source, SteamApi.NameSyncState State, bool FromLocal);

/// <summary>
/// 游戏中文名多源服务：本地轻量库（cnnames.json）→ 可配置数据源（内置小黑盒/Steam + 用户自定义）。
/// 故障弹性：单源失败隔离跳过；会话健康度（连续失败 2 次自动跳过，成功恢复）；本地库是离线最终防线。
/// </summary>
public static class GameNameService
{
    private class DbEntry
    {
        public string Name { get; set; } = "";
        public string Src { get; set; } = "";
        public string Ts { get; set; } = "";
    }

    private static Dictionary<int, DbEntry>? _db;        // 用户库（可写，落盘 cnnames.json）
    private static Dictionary<int, DbEntry>? _defaultDb; // 预置库（随程序发布，只读兜底）
    private static readonly object DbLock = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>会话健康度：源 id → 连续失败次数（≥2 在自动模式跳过）。</summary>
    private static readonly Dictionary<string, int> FailCount = new();
    private const int FailThreshold = 2;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>读取用选项：预置库由外部脚本生成，键名大小写可能与 DbEntry 属性不一致。</summary>
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    public static string DbFile => Path.Combine(Store.BaseDir, "cnnames.json");

    /// <summary>预置中文名库（随程序发布在 exe 旁 Data 下，只读；用户库条目优先于它）。
    /// 单文件便携版中 AppContext.BaseDirectory 指向临时解压目录，须用 ProcessPath 定位 exe 所在目录。</summary>
    public static string DefaultDbFile
    {
        get
        {
            string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (exeDir != null)
            {
                string beside = Path.Combine(exeDir, "Data", "cnnames.default.json");
                if (File.Exists(beside)) return beside;
            }
            return Path.Combine(AppContext.BaseDirectory, "Data", "cnnames.default.json");
        }
    }

    // ── 内置数据源（技术字段随程序升级；用户可禁用/调优先级，不可删） ──
    public static List<NameSourceDef> BuiltInSources() => new()
    {
        new NameSourceDef
        {
            Id = "xiaoheihe", DisplayName = "小黑盒（国内直连）", BuiltIn = true, Priority = 10,
            UrlTemplate = "https://api.xiaoheihe.cn/game/web/get_game_detail/?appid={appid}",
            ExtractType = "json", ExtractExpr = "result.name",
            Referer = "https://www.xiaoheihe.cn/",
        },
        new NameSourceDef
        {
            Id = "steam_page", DisplayName = "Steam 商店页", BuiltIn = true, Priority = 20,
            UrlTemplate = "https://store.steampowered.com/app/{appid}/?l=schinese&cc=hk",
            ExtractType = "regex", ExtractExpr = "<meta property=\"og:title\" content=\"([^\"]+)\"",
        },
        new NameSourceDef
        {
            Id = "steam_api", DisplayName = "Steam 数据接口", BuiltIn = true, Priority = 30,
            UrlTemplate = "https://store.steampowered.com/api/appdetails?appids={appid}&l=schinese&cc=hk",
            ExtractType = "json", ExtractExpr = "{appid}.data.name",
        },
    };

    /// <summary>合并内置源与用户配置：内置源技术字段以代码为准（保留启用/优先级），自定义源全保留。</summary>
    public static List<NameSourceDef> GetSources()
    {
        var list = Store.Settings.CustomNameSources ?? new List<NameSourceDef>();
        var byId = list.Where(s => !string.IsNullOrEmpty(s.Id)).ToDictionary(s => s.Id, s => s);
        foreach (var b in BuiltInSources())
        {
            if (byId.TryGetValue(b.Id, out var ex))
            {
                // 保留用户的启用状态与优先级，技术字段随升级更新
                ex.DisplayName = b.DisplayName; ex.BuiltIn = true;
                ex.UrlTemplate = b.UrlTemplate; ex.ExtractType = b.ExtractType;
                ex.ExtractExpr = b.ExtractExpr; ex.Referer = b.Referer;
            }
            else list.Add(b);
        }
        Store.Settings.CustomNameSources = list;
        return list.OrderBy(s => s.Priority).ToList();
    }

    public static void SaveSources(List<NameSourceDef> sources)
    {
        Store.Settings.CustomNameSources = sources;
        Store.SaveSettings();
        lock (FailCount) FailCount.Clear();   // 配置变更后健康度重置
    }

    private static Dictionary<int, DbEntry> Db
    {
        get
        {
            if (_db == null)
            {
                _db = new Dictionary<int, DbEntry>();
                try
                {
                    if (File.Exists(DbFile))
                        _db = JsonSerializer.Deserialize<Dictionary<int, DbEntry>>(File.ReadAllText(DbFile), ReadOpts)
                               ?? new Dictionary<int, DbEntry>();
                }
                catch { _db = new Dictionary<int, DbEntry>(); }
            }
            return _db;
        }
    }

    /// <summary>预置库（随程序发布，只读兜底；任何写操作都只进用户库，绝不回写此库）。</summary>
    private static Dictionary<int, DbEntry> DefaultDb
    {
        get
        {
            if (_defaultDb == null)
            {
                _defaultDb = new Dictionary<int, DbEntry>();
                try
                {
                    if (File.Exists(DefaultDbFile))
                        _defaultDb = JsonSerializer.Deserialize<Dictionary<int, DbEntry>>(
                                         File.ReadAllText(DefaultDbFile), ReadOpts)
                                     ?? new Dictionary<int, DbEntry>();
                }
                catch { _defaultDb = new Dictionary<int, DbEntry>(); }
            }
            return _defaultDb;
        }
    }

    /// <summary>本地库查询（零网络）：用户库优先，预置库兜底。</summary>
    public static bool TryGetLocal(int appId, out string name)
    {
        lock (DbLock)
        {
            if (appId > 0)
            {
                if (Db.TryGetValue(appId, out var u) && !string.IsNullOrWhiteSpace(u.Name))
                { name = u.Name; return true; }
                if (DefaultDb.TryGetValue(appId, out var d) && !string.IsNullOrWhiteSpace(d.Name))
                { name = d.Name; return true; }
            }
        }
        name = "";
        return false;
    }

    /// <summary>命中条目是否来自用户库（用于区分来源标签：本地库 / 预置库）。</summary>
    private static bool IsInUserDb(int appId)
    {
        lock (DbLock) return appId > 0 && Db.TryGetValue(appId, out var e) && !string.IsNullOrWhiteSpace(e.Name);
    }

    /// <summary>写入本地库（任何来源获取到的、或用户手动填写的中文名，均持久化复用）。</summary>
    public static void Remember(int appId, string name, string src)
    {
        if (appId <= 0 || string.IsNullOrWhiteSpace(name)) return;
        lock (DbLock)
        {
            Db[appId] = new DbEntry { Name = name.Trim(), Src = src, Ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
            SaveDb();
        }
    }

    /// <summary>本地库一次性落盘（批量预取结束时调用，避免逐条写文件）。</summary>
    private static void SaveDb()
    {
        try
        {
            string tmp = DbFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Db, JsonOpts));
            File.Move(tmp, DbFile, overwrite: true);
        }
        catch { /* 写失败不影响使用 */ }
    }

    /// <summary>批量预取中文名到本地库（"更新数据库"时调用）：仅走小黑盒（国内直连、免加速器），
    /// 跳过本地已有的；串行 + 350ms 节流防限流；结束一次性落盘。
    /// 连接不畅时：连续 5 次网络级失败自动中止（服务正常应答但无中文名不计）；也支持取消令牌手动停止。
    /// 返回 (成功, 已有跳过, 失败/无数据, 是否中止, 中止原因)。</summary>
    public static async Task<(int Ok, int Cached, int Fail, bool Stopped, string? Reason)> PrefetchXiaoheiheAsync(
        IEnumerable<int> appIds, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        const int maxConsecutiveNetFail = 5;
        const int maxConcurrency = 4;   // 小黑盒是国内 CDN，4 路并发安全；旧版串行+350ms/条 太慢
        var all = appIds.Where(id => id > 0).Distinct().ToList();
        var xhh = GetSources().FirstOrDefault(s => s.Id == "xiaoheihe");
        var todo = all.Where(id => !TryGetLocal(id, out _)).ToList();
        int cached = all.Count - todo.Count;
        if (xhh == null || !xhh.Enabled || todo.Count == 0)
            return (0, cached, todo.Count, false, null);

        int ok = 0, fail = 0, done = 0, netStreak = 0;
        int total = todo.Count;
        string? reason = null;
        var streakLock = new object();
        await Gate.WaitAsync(ct);
        try
        {
            using var workers = new SemaphoreSlim(maxConcurrency);
            using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tasks = todo.Select(appId => Task.Run(async () =>
            {
                await workers.WaitAsync(stopCts.Token);
                try
                {
                    var (name, state) = await QuerySource(xhh, appId);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        lock (DbLock)
                            Db[appId] = new DbEntry { Name = name!.Trim(), Src = "小黑盒", Ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
                        Interlocked.Increment(ref ok);
                        lock (streakLock) netStreak = 0;
                    }
                    else
                    {
                        Interlocked.Increment(ref fail);
                        lock (streakLock)
                        {
                            if (state == SteamApi.NameSyncState.NetworkError || state == SteamApi.NameSyncState.RateLimited)
                                netStreak++;
                            else
                                netStreak = 0;   // 服务正常应答但无中文名：不是连接问题，不累计
                            if (netStreak >= maxConsecutiveNetFail) stopCts.Cancel();
                        }
                    }
                    progress?.Report($"正在下载中文名（{Interlocked.Increment(ref done)}/{total}）…");
                }
                finally { workers.Release(); }
            }, stopCts.Token)).ToArray();

            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 自动中止（非手动）：落到下面的统一收尾
                reason = $"连接不畅：连续 {maxConsecutiveNetFail} 次请求失败，已自动中止（已获取的名称已保存，可稍后重试）";
            }
            lock (DbLock) SaveDb();
        }
        catch (OperationCanceledException)
        {
            // 手动停止：已获取的名称同样落盘保存
            lock (DbLock) SaveDb();
            reason ??= "已手动停止（已获取的名称已保存）";
        }
        finally { Gate.Release(); }
        return (ok, cached, fail, reason != null, reason);
    }

    /// <summary>按指定数据源查询中文名；Auto 模式依次尝试本地库→各启用源（跳过不健康源）。</summary>
    public static async Task<CnNameResult> FetchAsync(int appId, CnNameSource source = CnNameSource.Auto)
    {
        if (appId <= 0) return new CnNameResult(null, "", SteamApi.NameSyncState.NoData, false);
        if (TryGetLocal(appId, out var local))
            return new CnNameResult(local, IsInUserDb(appId) ? "本地库" : "预置库", SteamApi.NameSyncState.Success, true);

        await Gate.WaitAsync();
        try
        {
            if (TryGetLocal(appId, out local))
                return new CnNameResult(local, IsInUserDb(appId) ? "本地库" : "预置库", SteamApi.NameSyncState.Success, true);

            // 快捷菜单的强制单源映射；手动指定时忽略健康度
            IEnumerable<NameSourceDef> candidates;
            if (source == CnNameSource.Xiaoheihe)
                candidates = GetSources().Where(s => s.Id == "xiaoheihe");
            else if (source == CnNameSource.Steam)
                candidates = GetSources().Where(s => s.Id is "steam_page" or "steam_api");
            else
                candidates = GetSources().Where(s => s.Enabled && IsHealthy(s.Id));

            var lastState = SteamApi.NameSyncState.NoData;
            foreach (var src in candidates)
            {
                var (name, state) = await QuerySource(src, appId);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    lock (FailCount) FailCount[src.Id] = 0;
                    Remember(appId, name!, src.DisplayName);
                    return new CnNameResult(name, src.DisplayName, state, false);
                }
                lastState = state;
                lock (FailCount) FailCount[src.Id] = (FailCount.TryGetValue(src.Id, out var c) ? c : 0) + 1;
            }
            return new CnNameResult(null, "", lastState, false);
        }
        finally { Gate.Release(); }
    }

    private static bool IsHealthy(string id)
    {
        lock (FailCount) return !(FailCount.TryGetValue(id, out var c) && c >= FailThreshold);
    }

    /// <summary>是否含中日韩文字（提取结果必须含中文才算有效中文名）。</summary>
    private static bool HasCjk(string s) =>
        s.Any(c => c >= 0x4E00 && c <= 0x9FFF || c >= 0x3400 && c <= 0x4DBF);

    /// <summary>构造 HttpClient：连接阶段并发竞速尝试全部解析地址（IPv6 黑洞时 IPv4 立即胜出，
    /// 规避「冷启动首个请求卡满超时」）；浏览器 UA + 可选 Referer。</summary>
    internal static HttpClient NewClient(int timeoutSec, string? referer = null)
    {
        static async ValueTask<Stream> Connect(SocketsHttpConnectionContext ctx, CancellationToken ct)
        {
            IPAddress[] addrs;
            try { addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct).ConfigureAwait(false); }
            catch { addrs = Array.Empty<IPAddress>(); }
            if (addrs.Length == 0) throw new HttpRequestException("DNS 解析失败");

            using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pending = addrs.Select(ip =>
            {
                var sock = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                return TryConnect(sock, ip, ctx.DnsEndPoint.Port, raceCts.Token);
            }).ToList();

            Socket? winner = null;
            Exception? lastErr = null;
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);
                if (done.Result is { } sock) { winner = sock; break; }
                lastErr ??= done.Exception?.InnerException;
            }
            if (winner == null) throw new HttpRequestException("所有地址均连接失败", lastErr);
            raceCts.Cancel();   // 终止其余在途尝试（catch 中会 Dispose 掉）
            return new NetworkStream(winner, ownsSocket: true);
        }

        static async Task<Socket?> TryConnect(Socket sock, IPAddress ip, int port, CancellationToken ct)
        {
            try { await sock.ConnectAsync(ip, port, ct).ConfigureAwait(false); return sock; }
            catch { sock.Dispose(); return null; }
        }

        var http = new HttpClient(new SocketsHttpHandler { ConnectCallback = Connect })
        { Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 3, 30)) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        if (!string.IsNullOrWhiteSpace(referer))
            http.DefaultRequestHeaders.Referrer = new Uri(referer);
        return http;
    }

    /// <summary>用指定源定义发一次请求并提取中文名（任何异常隔离为 NetworkError，不抛出）。</summary>
    public static async Task<(string? Name, SteamApi.NameSyncState State)> QuerySource(NameSourceDef src, int appId)
    {
        try
        {
            string url = src.UrlTemplate
                .Replace("{appid}", appId.ToString())
                .Replace("{name_urlenc}", "");
            using var http = NewClient(src.TimeoutSec, src.Referer);
            byte[] bytes = await http.GetByteArrayAsync(url);
            Encoding enc;
            if (src.Encoding.Equals("gbk", StringComparison.OrdinalIgnoreCase))
            {
                try { enc = Encoding.GetEncoding("GB18030"); }   // 需 CodePages 包支持，缺失时回退 UTF-8
                catch { enc = Encoding.UTF8; }
            }
            else enc = Encoding.UTF8;
            string body = enc.GetString(bytes);

            string? extracted = src.ExtractType == "regex"
                ? ExtractRegex(body, src.ExtractExpr)
                : ExtractJsonPath(body, src.ExtractExpr.Replace("{appid}", appId.ToString()));

            if (string.IsNullOrWhiteSpace(extracted))
                return (null, SteamApi.NameSyncState.NoData);
            extracted = WebUtility.HtmlDecode(extracted).Trim();
            // 去除 HTML 标签残留（正则源可能取到带标签片段）
            extracted = Regex.Replace(extracted, "<[^>]+>", "").Trim();
            return HasCjk(extracted)
                ? (extracted, SteamApi.NameSyncState.Success)
                : (null, SteamApi.NameSyncState.NoLocalizedName);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return (null, SteamApi.NameSyncState.RateLimited);
        }
        catch { return (null, SteamApi.NameSyncState.NetworkError); }
    }

    private static string? ExtractRegex(string body, string pattern)
    {
        var m = Regex.Match(body, pattern, RegexOptions.IgnoreCase);
        return m.Success && m.Groups.Count > 1 ? m.Groups[1].Value : null;
    }

    /// <summary>极简 JSON 路径导航：a.b[0].c 形式；导航失败返回 null。单值与列表提取共用。</summary>
    internal static JsonElement? NavigateJson(JsonElement root, string path)
    {
        try
        {
            JsonElement cur = root;
            foreach (var seg in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                string key = seg;
                int bracket = seg.IndexOf('[');
                int idx = -1;
                if (bracket >= 0)
                {
                    key = seg[..bracket];
                    int end = seg.IndexOf(']', bracket);
                    if (end > bracket) int.TryParse(seg.Substring(bracket + 1, end - bracket - 1), out idx);
                }
                if (key.Length > 0)
                {
                    if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(key, out cur))
                        return null;
                }
                if (idx >= 0)
                {
                    if (cur.ValueKind != JsonValueKind.Array || idx >= cur.GetArrayLength()) return null;
                    cur = cur[idx];
                }
            }
            return cur;
        }
        catch { return null; }
    }

    /// <summary>极简 JSON 路径：a.b[0].c 形式；返回第一个匹配的字符串值。</summary>
    private static string? ExtractJsonPath(string json, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var el = NavigateJson(doc.RootElement, path);
            if (el == null) return null;
            return el.Value.ValueKind == JsonValueKind.String ? el.Value.GetString() : el.Value.GetRawText();
        }
        catch { return null; }
    }
}
