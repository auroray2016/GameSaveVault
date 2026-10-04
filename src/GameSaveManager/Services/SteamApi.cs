using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameSaveManager.Services;

/// <summary>
/// Steam 免费公开接口：中文名称获取（商店页 og:title 优先，appdetails JSON 兜底）+ 封面图下载（CDN）。
/// 中文名获取遵循“最少请求”：缓存命中不联网；未命中时商店页 1 次请求，失败兜底 JSON 1 次，不重试循环；
/// 内置节流（串行 + 350ms 间隔），成功结果进程内缓存，429 限流自动冷却 30 秒。
/// </summary>
public static class SteamApi
{
    private static HttpClient _http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        // 重建 handler 会重新读取系统代理设置：覆盖「先开应用、后开加速器」时
        // 静态 HttpClient 一直走旧（无代理）配置导致的超时。
        var h = new HttpClient(new HttpClientHandler
        {
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
        })
        { Timeout = TimeSpan.FromSeconds(8) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        return h;
    }

    /// <summary>网络失败时重建 HttpClient（刷新系统代理与坏连接）。</summary>
    private static void ResetHttpClient()
    {
        var old = _http;
        _http = CreateHttp();
        try { old.Dispose(); } catch { }
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _nextAllowed = DateTime.MinValue;
    private static readonly Dictionary<int, string> CnNameCache = new();

    /// <summary>中文名同步结果状态。</summary>
    public enum NameSyncState
    {
        Success,        // 获取成功
        RateLimited,    // 被 Steam 限流（HTTP 429）
        NoData,         // Steam 返回 success=false：AppId 无数据（AppId 有误 / 区域锁区 / 已下架）
        NoLocalizedName,// Steam 无该游戏的官方中文名（发行商未本地化，仅媒体/社区译名）
        NetworkError,   // 网络失败（商店页与接口均连不上）
    }

    /// <summary>是否含中日韩文字（用于判定 Steam 返回的名称是否为真正的中文本地化名）。</summary>
    private static bool HasCjk(string s) =>
        s.Any(c => c >= 0x4E00 && c <= 0x9FFF || c >= 0x3400 && c <= 0x4DBF);

    /// <summary>获取游戏中文名；失败返回 null（保留英文名）。FromCache=true 表示命中进程内缓存（未联网）。</summary>
    public static async Task<(string? Name, NameSyncState State, bool FromCache)> FetchChineseName(int appId)
    {
        if (appId <= 0) return (null, NameSyncState.NoData, false);
        await Gate.WaitAsync();
        try
        {
            lock (CnNameCache)
            {
                if (CnNameCache.TryGetValue(appId, out var hit)) return (hit, NameSyncState.Success, true);
            }

            var wait = _nextAllowed - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            _nextAllowed = DateTime.UtcNow.AddMilliseconds(350);

            // ① 商店页面（与浏览器访问同一 URL，CDN 缓存友好、区域限制宽松）：一次请求，只取 og:title
            bool pageReached = false;
            try
            {
                var html = await _http.GetStringAsync(
                    $"https://store.steampowered.com/app/{appId}/?l=schinese&cc=hk");
                pageReached = true;
                var m = Regex.Match(html, "<meta property=\"og:title\" content=\"([^\"]+)\"");
                if (m.Success)
                {
                    var s = WebUtility.HtmlDecode(m.Groups[1].Value.Trim());
                    if (!string.IsNullOrWhiteSpace(s) && HasCjk(s))
                    {
                        lock (CnNameCache) { CnNameCache[appId] = s; }
                        return (s, NameSyncState.Success, false);
                    }
                    // 英文名（发行商未注册中文名）：不直接返回，落到 JSON 接口复核
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _nextAllowed = DateTime.UtcNow.AddSeconds(30);
                return (null, NameSyncState.RateLimited, false);
            }
            catch { /* 网络失败，走兜底接口 */ }

            // ② 兜底：轻量 JSON 接口（cc=hk 绕过区域锁），最多再请求一次（不重试、不循环）
            ResetHttpClient();
            try
            {
                using var doc = JsonDocument.Parse(await _http.GetStringAsync(
                    $"https://store.steampowered.com/api/appdetails?appids={appId}&l=schinese&cc=hk"));
                if (doc.RootElement.TryGetProperty(appId.ToString(), out var node) &&
                    node.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True &&
                    node.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    var s = name.GetString();
                    if (!string.IsNullOrWhiteSpace(s) && HasCjk(s))
                    {
                        lock (CnNameCache) { CnNameCache[appId] = s!; }
                        return (s, NameSyncState.Success, false);
                    }
                    // l=schinese 仍返回英文名：发行商未在 Steam 注册中文商店名
                    return (null, NameSyncState.NoLocalizedName, false);
                }
                return (null, NameSyncState.NoData, false);   // 商店页/接口均无此游戏数据：锁区/下架/AppId 错误
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _nextAllowed = DateTime.UtcNow.AddSeconds(30);
                return (null, NameSyncState.RateLimited, false);
            }
            catch
            {
                // 页面可达但无 og:title（如年龄验证页）且接口也连不上 → 视为无数据；页面都打不开 → 网络问题
                return (null, pageReached ? NameSyncState.NoData : NameSyncState.NetworkError, false);
            }
        }
        catch { /* 其他异常，交由末尾统一返回 */ }
        finally { Gate.Release(); }
        return (null, NameSyncState.NetworkError, false);
    }

    /// <summary>下载官方头图到本地缓存。</summary>
    public static async Task<bool> DownloadCover(int appId, string saveTo)
    {
        if (appId <= 0) return false;
        try
        {
            var urls = new[]
            {
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg",
                $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg",
            };
            foreach (var url in urls)
            {
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    try
                    {
                        var bytes = await _http.GetByteArrayAsync(url);
                        if (bytes.Length > 1000)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(saveTo)!);
                            await File.WriteAllBytesAsync(saveTo, bytes);
                            return true;
                        }
                    }
                    catch when (attempt == 1)
                    {
                        ResetHttpClient();   // 同步中文名同款优化：刷新代理后重试一次
                        await Task.Delay(400);
                    }
                    catch { }
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>store 搜索熔断：国内无加速器时 store.steampowered.com 直连超时，
    /// 一次失败后短时间内不再逐个关键词重试（搜索失败与「无结果」均为 null，冷却只影响网络失败场景）。</summary>
    private static DateTime _searchCooldownUntil = DateTime.MinValue;

    /// <summary>按英文名反查 appid（学习版联网匹配用，MVP 保留接口）。</summary>
    public static async Task<(int AppId, string Name)?> SearchByName(string term)
    {
        if (DateTime.UtcNow < _searchCooldownUntil) return null;
        try
        {
            var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&cc=CN&l=schinese";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array &&
                items.GetArrayLength() > 0)
            {
                var first = items[0];
                int id = first.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0;
                string name = first.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (id > 0) return (id, name);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _searchCooldownUntil = DateTime.UtcNow.AddMinutes(3);   // 连接失败/超时：store 不可达，冷却
        }
        catch { }
        return null;
    }

    /// <summary>在大小写转换边界插空格：TheDevilInMe → The Devil In Me；ELDENRING 全大写不变；ABCdef → AB Cdef。</summary>
    public static string InsertSpacesAtCaseBoundaries(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (i > 0)
            {
                char prev = s[i - 1];
                // 小→大边界：aB → a B
                if (char.IsLower(prev) && char.IsUpper(c))
                    sb.Append(' ');
                // 大+大→小边界：ABc → A Bc（首字母缩写后接单词）
                else if (i + 1 < s.Length && char.IsUpper(prev) && char.IsUpper(c) && char.IsLower(s[i + 1]))
                    sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>下载 steamtools 返回的封面图 URL 到本地（备用，主流程仍走 Steam CDN header.jpg）。</summary>
    public static async Task<bool> DownloadImage(string url, string saveTo)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(url);
            if (bytes.Length > 1000)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(saveTo)!);
                await File.WriteAllBytesAsync(saveTo, bytes);
                return true;
            }
        }
        catch { }
        return false;
    }
}
