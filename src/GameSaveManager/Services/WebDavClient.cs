using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace GameSaveManager.Services;

/// <summary>云端备份条目（PROPFIND 枚举结果）。</summary>
public sealed class CloudRemoteFile
{
    public string RelPath { get; init; } = "";   // 相对云端根：游戏/玩家/xxx.zip
    public string Game { get; init; } = "";
    public string Profile { get; init; } = "";   // "" = 未分玩家（_通用）
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public DateTime Modified { get; init; }      // 云端文件修改时间 = 上传时刻
    /// <summary>存档时间（游戏真实保存时刻，UTC）。由云端索引 index.json 关联填充；
    /// null = 索引无此条目（旧包/他机新包索引未写），展示与比较回退用 Modified。</summary>
    public DateTime? SaveAt { get; set; }
    /// <summary>排序/主显示时间：优先存档时间（用户关心的是"这是几点的游戏进度"）。</summary>
    public DateTime SortTimeUtc => SaveAt ?? Modified;
    public string MainTimeText => (SaveAt ?? Modified).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    /// <summary>副行小字：上传时刻；无存档时间时为空串（避免两行显示同一时间）。</summary>
    public string UploadTimeText => SaveAt == null ? "" : "上传于 " + Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string SizeText => BackupService.FormatBytes(Size);
    public string TimeText => Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string ProfileText => Profile.Length == 0 ? "通用" : Profile;
    /// <summary>备注显示文本：上传前本机同名备份上若有备注，加载时回填（无则空串）。</summary>
    public string NoteText { get; set; } = "";
}

/// <summary>应用密码的 DPAPI 保护（CurrentUser；不引 NuGet，直接 P/Invoke crypt32）。</summary>
public static class CloudSecret
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DATA_BLOB dataIn, string? szDescr, ref DATA_BLOB optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, uint flags, out DATA_BLOB dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr ppszDescr, ref DATA_BLOB optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, uint flags, out DATA_BLOB dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr ppszDescr, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, uint flags, out DATA_BLOB dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>应用专属熵：DPAPI 已绑定当前用户，再加熵可防止同用户下其他程序/恶意代码
    /// 直接复用本机凭据密文解密（密文仍只存本地，不跨机同步）。</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GameSaveVault.CloudSecret.v1");

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        byte[] bytes = Encoding.UTF8.GetBytes(plain);
        var blob = new DATA_BLOB { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
        var ent = new DATA_BLOB { cbData = Entropy.Length, pbData = Marshal.AllocHGlobal(Entropy.Length) };
        try
        {
            Marshal.Copy(bytes, 0, blob.pbData, bytes.Length);
            Marshal.Copy(Entropy, 0, ent.pbData, Entropy.Length);
            if (!CryptProtectData(ref blob, null, ref ent, IntPtr.Zero, IntPtr.Zero, 0, out var outBlob))
                throw new InvalidOperationException("凭据加密失败（DPAPI）");
            try { return Convert.ToBase64String(outBlob.pbData.ToBytes(outBlob.cbData)); }
            finally { LocalFree(outBlob.pbData); }
        }
        finally { Marshal.FreeHGlobal(blob.pbData); Marshal.FreeHGlobal(ent.pbData); }
    }

    public static string Unprotect(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return "";
        byte[] raw;
        try { raw = Convert.FromBase64String(encrypted); }
        catch { return ""; }
        var blob = new DATA_BLOB { cbData = raw.Length, pbData = Marshal.AllocHGlobal(raw.Length) };
        var ent = new DATA_BLOB { cbData = Entropy.Length, pbData = Marshal.AllocHGlobal(Entropy.Length) };
        try
        {
            Marshal.Copy(raw, 0, blob.pbData, raw.Length);
            Marshal.Copy(Entropy, 0, ent.pbData, Entropy.Length);
            // 先按新格式（带熵）解；解不开再按旧格式（无熵，升级前保存的密文）解，
            // 兼容期内旧密文照样可用，下次保存设置时自动重新加密
            if (!CryptUnprotectData(ref blob, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero, 0, out var outBlob))
            {
                Marshal.Copy(raw, 0, blob.pbData, raw.Length);
                if (!CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out outBlob))
                    return "";   // 换用户/换机后解不开：按"未配置密码"处理，由用户重填
            }
            try { return Encoding.UTF8.GetString(outBlob.pbData.ToBytes(outBlob.cbData)); }
            finally { LocalFree(outBlob.pbData); }
        }
        finally { Marshal.FreeHGlobal(blob.pbData); Marshal.FreeHGlobal(ent.pbData); }
    }

    private static byte[] ToBytes(this IntPtr ptr, int len)
    {
        var buf = new byte[len];
        Marshal.Copy(ptr, buf, 0, len);
        return buf;
    }
}

/// <summary>
/// 极简 WebDAV 客户端（坚果云 / Nextcloud / 群晖 / AList）：PROPFIND/MKCOL/PUT/GET/DELETE。
/// 走系统代理；上传/枚举/下载各自带瞬时故障退避重试。
/// </summary>
public sealed class WebDavClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseNoSlash;     // 服务器根（不含尾斜杠），如 https://dav.jianguoyun.com/dav
    private readonly string _root;            // 云端备份目录，如 GameSaveVault

    // 全局共享底层 handler/连接池：WebDavClient 实例随每次云操作频繁创建（收集/删除/浏览…），
    // 旧实现每实例一个 HttpClientHandler，既浪费套接字又让 TLS/连接复用失效。
    // 凭据按实例不同（账号/应用密码可能修改），故认证头挂到每个 HttpRequestMessage 上而非默认头。
    private static readonly HttpClientHandler SharedHandler =
        new() { UseProxy = true, AutomaticDecompression = DecompressionMethods.All };
    private static readonly HttpClient SharedHttp = new(SharedHandler, disposeHandler: false)
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    private readonly AuthenticationHeaderValue? _auth;

    public WebDavClient(string url, string user, string password, string rootDir)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("WebDAV 服务器地址为空");
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != "http" && baseUri.Scheme != "https"))
            throw new InvalidOperationException("WebDAV 地址格式无效，应以 http:// 或 https:// 开头");
        _baseNoSlash = baseUri.ToString().TrimEnd('/');
        _root = string.IsNullOrWhiteSpace(rootDir) ? "GameSaveVault" : rootDir.Trim().Trim('/');
        _http = SharedHttp;
        if (!string.IsNullOrEmpty(user))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
            _auth = new AuthenticationHeaderValue("Basic", token);
        }
    }

    internal string Url(string relPath)
    {
        var segs = (_root + "/" + relPath.TrimStart('/'))
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        return _baseNoSlash + "/" + string.Join('/', segs);
    }

    private HttpRequestMessage Request(HttpMethod method, string relPath)
    {
        var req = new HttpRequestMessage(method, Url(relPath));
        if (_auth != null) req.Headers.Authorization = _auth;
        // UA 首次设置后 ParseAdd 会重复追加；幂等保护
        if (!SharedHttp.DefaultRequestHeaders.UserAgent.Any())
            SharedHttp.DefaultRequestHeaders.UserAgent.ParseAdd("GameSaveVault/1.0");
        return req;
    }

    /// <summary>测试连接：对服务器根做 PROPFIND Depth:0。</summary>
    public async Task<(bool ok, string msg)> TestAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await SendRetryAsync(() =>
            {
                var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), _baseNoSlash + "/");
                if (_auth != null) req.Headers.Authorization = _auth;   // 不走 Request() 工厂也必须带认证，否则必 401
                req.Headers.Add("Depth", "0");
                req.Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml");
                return req;
            }, ct);
            if ((int)resp.StatusCode == 207) return (true, "连接成功");
            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                return (false, $"认证失败（{(int)resp.StatusCode}）：请检查账号与应用密码（坚果云需用独立应用密码，非登录密码）");
            return (false, $"服务器返回 {(int)resp.StatusCode} {resp.StatusCode}");
        }
        catch (Exception ex) { return (false, "连接失败：" + ex.Message); }
    }

    private const string PropFindBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<D:propfind xmlns:D=\"DAV:\"><D:prop>" +
        "<D:resourcetype/><D:getcontentlength/><D:getlastmodified/>" +
        "</D:prop></D:propfind>";

    /// <summary>递归创建目录（逐级 MKCOL；201 创建/405 已存在都算成功）。</summary>
    public async Task EnsureDirAsync(string relDir, CancellationToken ct = default)
    {
        var segs = relDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string acc = "";
        foreach (var seg in segs)
        {
            acc = acc.Length == 0 ? seg : acc + "/" + seg;
            using var resp = await SendRetryAsync(() => Request(new HttpMethod("MKCOL"), acc), ct);
            int code = (int)resp.StatusCode;
            if (code == 201 || code == 405 || code == 204) continue;
            if (code == 401 || code == 403) throw new InvalidOperationException($"WebDAV 认证失败或无权限（{code}），请检查账号/应用密码");
            throw new InvalidOperationException($"创建云端目录「{acc}」失败：{code} {resp.StatusCode}");
        }
    }

    /// <summary>上传文件（PUT）。仅当远端不存在时写入（If-None-Match: *），保证只增不覆盖。
    /// 冲突（412）时先 HEAD 比字节数：一样大按「同内容已在云端」认领（响应丢失重试不造重复副本）；
    /// 确实撞名才加机器名后缀，再撞追加按文件身份算出的稳定短哈希。</summary>
    public async Task<string> UploadAsync(string localPath, string relPath, CancellationToken ct = default)
    {
        string parent = string.Join('/', relPath.Split('/')[..^1]);
        await EnsureDirAsync(parent, ct);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(30));   // 大文件放宽超时
            // 工厂：每次重试都重新打开 FileStream（已发出的流不能复位）
            using var resp = await SendRetryAsync(() =>
            {
                var req = Request(HttpMethod.Put, relPath);
                // 通配符必须用静态 Any（构造函数只接受带引号的字符串，直接 new("*") 会抛 FormatException）
                req.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
                req.Content = new StreamContent(File.OpenRead(localPath));
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                return req;
            }, cts.Token, longBody: true);
            int code = (int)resp.StatusCode;
            if (code is 200 or 201 or 204) return relPath;
            if (code == 412)
            {
                // 远端已有同名文件。先用 HEAD 比字节数：一样大就按「同一内容已在云端」认领
                // （典型场景：上次上传服务端其实写成功但响应丢失，重试不该再造重复副本）。
                long localLen = new FileInfo(localPath).Length;
                long? remoteLen = await HeadSizeAsync(relPath, ct);
                if (remoteLen is long rl && rl == localLen) return relPath;
                // 真·不同内容撞名（两台电脑同一秒备份）：第一次加机器名后缀；
                // 再撞（同名机器/系统克隆）追加按文件身份算出的稳定短哈希
                string name = Path.GetFileNameWithoutExtension(relPath);
                string suffix = SafeSeg(Environment.MachineName)
                    + (attempt >= 1 ? "_" + StableFileSuffix(localPath) : "");
                relPath = parent + $"/{name}_{suffix}.zip";
                continue;
            }
            if (code == 401 || code == 403) throw new InvalidOperationException($"云端认证失败（{code}），请在设置中检查账号/应用密码");
            if (code == 507) throw new InvalidOperationException("云端空间不足（507），请清理网盘或升级容量");
            throw new InvalidOperationException($"上传失败：{code} {resp.StatusCode}");
        }
        throw new InvalidOperationException("上传失败：云端持续返回 412 冲突（已尝试机器名/短哈希后缀），请稍后在云端管理中查看");
    }

    /// <summary>HEAD 取远端文件字节数（412 冲突时辨认"同内容响应丢失" vs "真撞名"）。
    /// 不支持 HEAD 或出错返回 null，调用方按真冲突处理（改名重传，宁重复不覆盖）。</summary>
    private async Task<long?> HeadSizeAsync(string relPath, CancellationToken ct)
    {
        try
        {
            using var resp = await SendRetryAsync(() => Request(HttpMethod.Head, relPath), ct);
            if (!resp.IsSuccessStatusCode) return null;
            return resp.Content.Headers.ContentLength;
        }
        catch { return null; }
    }

    /// <summary>按 机器名+文件大小+修改时间 计算稳定短哈希：同一台机器、同一个文件
    /// 每次算出的后缀相同（重试时收敛到同一云端名→HEAD 认领），不同内容则后缀不同（真正错开）。</summary>
    private static string StableFileSuffix(string localPath)
    {
        try
        {
            var fi = new FileInfo(localPath);
            string seed = $"{Environment.MachineName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(seed));
            return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        }
        catch { return SafeSeg(Environment.MachineName) + "_x"; }
    }

    /// <summary>上传小文本（云端索引 index.json 等派生数据）。直接覆盖写（不加 If-None-Match），
    /// 不走 UploadAsync 的 412 同名改名逻辑——索引是"最后写入者生效"的数据，不是要保护的孤本。</summary>
    public async Task UploadTextAsync(string relPath, string content, CancellationToken ct = default)
    {
        string parent = string.Join('/', relPath.Split('/')[..^1]);
        await EnsureDirAsync(parent, ct);
        using var resp = await SendRetryAsync(() =>
        {
            var req = Request(HttpMethod.Put, relPath);
            req.Content = new StringContent(content, Encoding.UTF8, "application/json");
            return req;
        }, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"上传失败：{(int)resp.StatusCode} {resp.StatusCode}");
    }

    /// <summary>下载小文本（云端索引）。404 返回 null（索引尚未建立）。</summary>
    public async Task<string?> DownloadTextAsync(string relPath, CancellationToken ct = default)
    {
        using var resp = await SendRetryAsync(() => Request(HttpMethod.Get, relPath), ct);
        int code = (int)resp.StatusCode;
        if (code == 404) return null;
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"下载失败：{code} {resp.ReasonPhrase}");
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>下载到本地目标路径（先 .part 再原子改名）。progress 报告 0~1 的下载比例。</summary>
    public async Task DownloadAsync(string relPath, string localPath,
        CancellationToken ct = default, IProgress<double>? progress = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(30));
        using var resp = await SendRetryAsync(() => Request(HttpMethod.Get, relPath), cts.Token, longBody: true);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"下载失败：{(int)resp.StatusCode} {resp.ReasonPhrase}");
        string part = localPath + ".part";
        try
        {
            long? total = resp.Content.Headers.ContentLength;
            using (var src = await resp.Content.ReadAsStreamAsync(ct))
            using (var dst = File.Create(part))
            {
                // 手动分块拷贝以便按字节数回报进度；按整百分点节流，避免回调淹没 UI
                var buffer = new byte[81920];
                long read = 0;
                int lastPct = -1;
                while (true)
                {
                    int n = await src.ReadAsync(buffer, ct);
                    if (n == 0) break;
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total is long t && t > 0)
                    {
                        int pct = (int)Math.Min(100, read * 100 / t);
                        if (pct != lastPct) { lastPct = pct; progress?.Report(pct / 100.0); }
                    }
                }
                // 字节数校验：代理/服务器截断时 Content-Length 与实发不一致，绝不能把半截包落库
                if (total is long expected && expected > 0 && read != expected)
                    throw new InvalidOperationException($"下载不完整：收到 {read} 字节，应为 {expected} 字节，请重试");
            }
            // zip 完整性校验：能打开中央目录才算有效备份（防代理错误页/半截数据被当备份导入）
            try
            {
                using var probe = ZipFile.OpenRead(part);
                _ = probe.Entries.Count;
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidOperationException("下载文件已损坏（zip 校验失败），请重试：" + ex.Message);
            }
            File.Move(part, localPath, overwrite: true);
            progress?.Report(1.0);
        }
        finally { try { if (File.Exists(part)) File.Delete(part); } catch { } }
    }

    /// <summary>删除云端文件（2xx/404 均视为成功）。</summary>
    public async Task DeleteAsync(string relPath, CancellationToken ct = default)
    {
        using var resp = await SendRetryAsync(() => Request(HttpMethod.Delete, relPath), ct);
        int code = (int)resp.StatusCode;
        if (code is 401 or 403) throw new InvalidOperationException($"删除被拒绝（{code}）：认证失败或无权限");
        if (!(resp.IsSuccessStatusCode || code == 404))
            throw new InvalidOperationException($"删除失败：{code} {resp.StatusCode}");
    }

    /// <summary>递归枚举云端根下所有 zip（Depth:1 逐层 PROPFIND）。</summary>
    public async Task<List<CloudRemoteFile>> ListAllAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new List<CloudRemoteFile>();
        // relDir 一律使用相对云端根的路径（"" = 根），与 Upload/Download/Delete 的 relPath 语义一致
        await WalkAsync("", result, progress, ct, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>枚举云端某个子目录下的全部 zip（relDir 相对云端根，如「游戏名」）。
    /// 子目录不存在（游戏从未上传）返回空列表。</summary>
    public async Task<List<CloudRemoteFile>> ListAsync(
        string relDir, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new List<CloudRemoteFile>();
        string start = string.IsNullOrWhiteSpace(relDir) ? "" : relDir.Trim().Trim('/');
        await WalkAsync(start, result, progress, ct, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return result;
    }

    private async Task WalkAsync(string relDir, List<CloudRemoteFile> result,
        IProgress<string>? progress, CancellationToken ct, HashSet<string> visited)
    {
        // 防环：部分服务器对 collection 做别名/符号链接，逐层 PROPFIND 可能回到已访问目录
        if (!visited.Add(relDir)) return;
        // relDir 为相对云端根的路径（"" = 根）；进度展示时根层显示根目录名
        progress?.Report(relDir.Length == 0 ? _root : relDir);
        using var resp = await SendRetryAsync(() =>
        {
            var req = Request(new HttpMethod("PROPFIND"), relDir);
            req.Headers.Add("Depth", "1");
            req.Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml");
            return req;
        }, ct);
        if ((int)resp.StatusCode != 207)
        {
            if ((int)resp.StatusCode == 404) return;   // 根目录尚未创建 = 云端为空
            throw new InvalidOperationException($"枚举云端文件失败：{(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        var xml = await resp.Content.ReadAsStringAsync(ct);
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex) { throw new InvalidOperationException("云端返回的文件列表无法解析：" + ex.Message); }

        var responses = doc.Descendants().Where(e => e.Name.LocalName == "response").ToList();
        // 根层（relDir 为空）自身条目名是根目录名，其余层是当前目录末段
        string selfName = relDir.Length == 0 ? _root : relDir.Split('/')[^1];
        foreach (var r in responses)
        {
            string href = Uri.UnescapeDataString(
                r.Elements().FirstOrDefault(e => e.Name.LocalName == "href")?.Value.Trim() ?? "");
            // 一个 response 可有多段 propstat（200 成功属性 + 404 不支持属性）。
            // 必须取 status=200 那段的 prop，否则可能读到 404 空值，把目录误判成空文件/时间错误。
            var propstats = r.Elements().Where(e => e.Name.LocalName == "propstat").ToList();
            var goodPropStat = propstats.FirstOrDefault(ps =>
                ps.Elements().FirstOrDefault(s => s.Name.LocalName == "status")?.Value.Contains(" 200") == true)
                ?? propstats.FirstOrDefault();
            var prop = goodPropStat?.Elements().FirstOrDefault(e => e.Name.LocalName == "prop")
                ?? r.Descendants().FirstOrDefault(e => e.Name.LocalName == "prop");   // 非标准服务器兜底
            bool isCollection = prop != null &&
                prop.Descendants().Any(d => d.Name.LocalName == "collection");
            long size = 0;
            long.TryParse(prop?.Elements().FirstOrDefault(e => e.Name.LocalName == "getcontentlength")?.Value,
                out size);
            DateTime modified = ParseWebDate(
                prop?.Elements().FirstOrDefault(e => e.Name.LocalName == "getlastmodified")?.Value);

            // 末段即子项名（href 可能是绝对/相对/带尾斜杠）
            var segs = href.Split('?')[0].TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length == 0) continue;
            string name = segs[^1];
            if (isCollection)
            {
                if (name == selfName) continue;   // 自身
                string child = relDir.Length == 0 ? name : relDir + "/" + name;
                await WalkAsync(child, result, progress, ct, visited);
            }
            else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var parts = relDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
                // relDir = 游戏/玩家 → parts[0]=游戏 parts[1]=玩家
                string game = parts.Length > 0 ? parts[0] : "（未知游戏）";
                string profile = parts.Length > 1 && parts[1] != "_通用" ? parts[1] : "";
                string rel = relDir.Length == 0 ? name : relDir + "/" + name;
                result.Add(new CloudRemoteFile
                {
                    RelPath = rel,
                    Game = game,
                    Profile = profile,
                    Name = name,
                    Size = size,
                    Modified = modified,
                });
            }
        }
    }

    private static DateTime ParseWebDate(string? s)
    {
        if (DateTimeOffset.TryParseExact(s, "R", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return dto.UtcDateTime;
        // 非 RFC1123 格式：按 UTC 解析并显式标注 Utc（TryParse 可能返回 Unspecified，
        // 后续与云端索引的 UTC ticks 直接比较会错位 8 小时）
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return default;
    }

    /// <summary>机器名/玩家名等片段的文件名安全化（与本地 SafeName 规则保持一致的兜底）。</summary>
    private static string SafeSeg(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    // HttpClient 为进程级共享实例（连接池复用），using 语义保留但不释放共享对象
    public void Dispose() { }

    private async Task<HttpResponseMessage> SendRetryAsync(
        Func<HttpRequestMessage> factory, CancellationToken ct, bool longBody = false)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var req = factory();
            try
            {
                var resp = await _http.SendAsync(req,
                    longBody ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct);
                int code = (int)resp.StatusCode;
                if ((code == 429 || code >= 500) && attempt < 2)
                {
                    resp.Dispose();
                    await Task.Delay(600 * (1 << attempt), ct);
                    continue;
                }
                return resp;
            }
            // 注意括号：attempt 判断要罩住整个异常类型析取（旧代码 && 优先级导致
            // HttpRequestException 在最后一轮仍被吞掉重试逻辑之外）；调用方主动取消不重试
            catch (Exception ex) when (attempt < 2 && !ct.IsCancellationRequested
                && (ex is HttpRequestException or TaskCanceledException))
            {
                lastError = ex;
                await Task.Delay(600 * (1 << attempt), ct);
            }
        }
        throw lastError ?? new InvalidOperationException("WebDAV 请求失败（重试已耗尽）");
    }
}
