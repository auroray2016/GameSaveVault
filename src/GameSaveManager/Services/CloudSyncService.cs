using System.IO;
using System.Text.Json;
using System.Threading.Channels;

using GameSaveManager.Models;

namespace GameSaveManager.Services;

/// <summary>
/// 云同步：备份 zip 创建后自动上传到 WebDAV（坚果云/NAS/AList）。
/// 只增不覆盖、不双向删除——云端是异地保险副本。上传登记表 cloud-state.json 记录
/// 「本地文件 → 远端路径」；启动/保存设置时对账补传，程序关闭期间漏传的下次启动自动补上。
/// </summary>
public static class CloudSyncService
{
    private sealed class StateFile
    {
        public Dictionary<string, StateEntry> Uploaded { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>已知云端文件（内容本机已具备：本机上传的 / 已下载并入本机的）：远端相对路径 → 云端 Modified ticks。
        /// 云端 Modified 是「上传时刻」而非游戏存档时刻（启动对账会把旧包补传、上传本身也有延迟），
        /// 切换玩家判断「云端是否更新」必须跳过已知文件，否则会陷入
        /// 「误判云端更新 → 下载旧包刷新镜像 → 下次备份误判变化 → 再上传」的死循环。</summary>
        public Dictionary<string, long> KnownRemote { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>「不再自动下载」黑名单（key=游戏安全名/文件名，value=加入时刻）：
        /// 用户主动删除本机备份后，云端同名 zip 不再被自动收集下载回来（防复活）。
        /// 云端浏览窗手动下载 / 切换玩家选「云端最新」会解除登记。</summary>
        public Dictionary<string, DateTime> Dismissed { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>手动归属映射：云端目录段（游戏安全名）→ 本机游戏安全名。
        /// 用户在云端管理窗「归属到…」指派后，收集/核验按映射认领该目录（优先级最高，覆盖自动判断）。</summary>
        public Dictionary<string, string> Mapping { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StateEntry
    {
        public long Size { get; set; }
        public long Mtime { get; set; }
        public string Remote { get; set; } = "";
        public DateTime When { get; set; }
        /// <summary>用户已从云端删除该副本。登记表保留但 IsUploaded 返回 false，
        /// UI 显示「已删重传」；重新上传成功后清零。</summary>
        public bool Deleted { get; set; }
    }

    /// <summary>上传任务快照：Entry 在入队时从备份记录构建（含存档时间），上传成功后写进云端索引。</summary>
    private sealed record Job(string Local, string Game, string Profile, CloudIndex.Entry? Entry);

    private static readonly object _gate = new();
    private static StateFile _state = new();
    private static readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>自动上传连续失败次数（退避重试节奏 + 抑制重复 toast）；成功后清零。</summary>
    private static readonly Dictionary<string, int> _upFails = new(StringComparer.OrdinalIgnoreCase);
    private static Channel<Job>? _channel;
    private static bool _started;
    private static CancellationTokenSource? _stopCts;
    private static Task? _workerTask;

    /// <summary>云端文件索引缓存：云端目录段（游戏安全名）→ 该目录云端文件列表（供切换玩家时判断云端新旧）。</summary>
    private static readonly Dictionary<string, List<CloudRemoteFile>> _gameIndex =
        new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _indexAt;
    /// <summary>云端索引身份段缓存（Games：目录 → AppId+别名），收集/认领时判定跨设备目录归属。
    /// 启动时为空（首次拉到索引后填充）；为空时认领退化为默认名+手动映射。</summary>
    private static CloudIndex.Doc? _idDoc;
    /// <summary>正在自动收集的游戏，防止启动收集与手动触发并发重复。</summary>
    private static readonly HashSet<string> _collecting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>自动上传失败时通知 UI（后台线程触发）。</summary>
    public static event Action<string>? BackgroundFailed;
    /// <summary>上传登记表变化（worker 自动传完、手动传完）；UI 据此刷新 ☁ 标记。</summary>
    public static event Action? UploadStateChanged;

    /// <summary>云同步可用 = 已填齐 WebDAV 地址/账号/密码。
    /// 旧版全局开关 CloudEnabled 已从界面移除，不再作为门槛；自动上传由各游戏的 CloudAuto 决定。</summary>
    public static bool Configured
    {
        get
        {
            var s = Store.Settings;
            return !string.IsNullOrWhiteSpace(s.CloudUrl)
                && !string.IsNullOrWhiteSpace(s.CloudUser)
                && !string.IsNullOrWhiteSpace(CloudSecret.Unprotect(s.CloudSecret));
        }
    }

    public static void Start()
    {
        if (_started) return;
        _started = true;
        LoadState();
        MigrateGlobalCloud();
        _stopCts = new CancellationTokenSource();
        _channel = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        BackupService.BackupCreated += OnBackupCreated;
        _workerTask = Task.Run(WorkerLoop);
        Reconcile();
        _ = Task.Run(() => AutoCollectAllAsync(_stopCts.Token));   // 启动后自动收集（内部有延迟）
    }

    /// <summary>应用退出时优雅停机：通知后台循环停止、等待在途任务收尾（有界）、排空登记表写盘。
    /// 不取消正在进行的 HTTP（PUT 被强杀无意义，服务端按完整请求落盘）；超过时限直接放行退出。</summary>
    public static async Task StopAsync()
    {
        if (!_started) return;
        try
        {
            try { _stopCts?.Cancel(); } catch { }
            try { _channel?.Writer.TryComplete(); } catch { }
            // 启动收集的初始延迟/各步取消后自行结束；worker 在 channel Complete 后退出
            if (_workerTask != null)
            {
                var finished = Task.WhenAny(_workerTask, Task.Delay(5000));
                await finished.ConfigureAwait(false);
            }
            WaitStateSaved(3000);   // 等最后的登记表落盘
        }
        catch { /* 停机路径任何异常都不得阻断退出 */ }
        finally
        {
            try { BackupService.BackupCreated -= OnBackupCreated; } catch { }
        }
    }

    /// <summary>旧版全局开关一次性迁移：CloudEnabled 用户的全部游戏继承为按游戏自动云存档。</summary>
    internal static void MigrateGlobalCloud()
    {
        try
        {
            if (Store.Settings.CloudPerGameMigrated) return;
            // 本次启动没拿到可信配置（settings.json 读盘失败/恢复未果而回退出厂默认）时，
            // 迁移决策只停留在内存、绝不落盘：磁盘上很可能正是用户的富配置，落盘即"默认值覆写"事故。
            // 迁移位本次会话仍置位（避免会话内重复判定），下次正常启动再据真实配置迁移一次即可。
            bool canPersist = Store.SettingsConfidence != SettingsLoadConfidence.Defaulted;
            if (Store.Settings.CloudEnabled)
            {
                foreach (var g in Store.Games)
                    if (!g.Archived) g.CloudAuto = true;   // 归档条目不参与自动云同步
                Store.Save();
            }
            Store.Settings.CloudPerGameMigrated = true;
            if (canPersist) Store.SaveSettings();
        }
        catch { /* 迁移失败不影响启动，下次再试 */ }
    }

    /// <summary>设置窗口云表单的三态判定结果。</summary>
    internal enum CloudFormState
    {
        /// <summary>地址/账号/密码全空：用户要关闭云同步（旧密码密文也应一并清空）。</summary>
        Empty,
        /// <summary>填了一部分但没齐全：本次不启用，已填内容照存以便下次打开继续补全。</summary>
        Partial,
        /// <summary>地址 + 账号 + 密码齐全：云同步立即生效。</summary>
        Complete,
    }

    /// <summary>按"地址、账号、是否有可用密码（本次输入或旧密文解密成功）"判定云表单状态。
    /// 纯函数：设置窗口保存逻辑与冒烟测试共用同一套判定——半成品表单只影响云同步本身，
    /// 绝不能阻塞快捷键、备份策略等其他设置项的保存。</summary>
    internal static CloudFormState EvaluateCloudForm(string url, string user, bool hasSecret)
    {
        bool hasUrl = !string.IsNullOrWhiteSpace(url);
        bool hasUser = !string.IsNullOrWhiteSpace(user);
        if (!hasUrl && !hasUser && !hasSecret) return CloudFormState.Empty;
        if (hasUrl && hasUser && hasSecret) return CloudFormState.Complete;
        return CloudFormState.Partial;
    }

    /// <summary>某游戏是否自动上传（全局已配置且该游戏开启自动云存档）。</summary>
    internal static bool ShouldAutoUpload(GameEntry g) => Configured && g.CloudAuto;

    private static void OnBackupCreated(GameEntry g, BackupRecord b)
    {
        if (!ShouldAutoUpload(g) || string.IsNullOrEmpty(b.File)) return;
        Enqueue(b.File, g.DisplayName, b.Profile ?? "", CloudIndex.BuildFor(b));
    }

    /// <summary>扫描自动云存档游戏的备份，把未登记/已变化的加入上传队列。</summary>
    public static void Reconcile()
    {
        if (!Configured) return;
        foreach (var g in Store.Games)
        {
            if (g.Archived || !g.CloudAuto) continue;
            foreach (var b in g.Backups)
                if (!string.IsNullOrEmpty(b.File))
                    Enqueue(b.File, g.DisplayName, b.Profile ?? "", CloudIndex.BuildFor(b));
        }
    }

    /// <summary>对账自愈云端索引：把「本机已上传登记，但云端索引缺条目」的备份补写进 index.json
    /// （两机并发写索引互相覆盖丢条目时的兜底）。每次启动自动收集前跑一次；失败静默下次再试。</summary>
    private static async Task ReconcileIndexAsync()
    {
        if (!Configured) return;
        try
        {
            // 期望集合：CloudAuto 游戏中"已登记且登记仍有效"的备份 → 云端 rel → 索引条目
            var expected = new Dictionary<string, CloudIndex.Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in Store.Games)
            {
                if (g.Archived || !g.CloudAuto) continue;
                foreach (var b in g.Backups)
                {
                    if (string.IsNullOrEmpty(b.File)) continue;
                    lock (_gate)
                    {
                        if (!_state.Uploaded.TryGetValue(b.File, out var e)) continue;
                        if (e.Deleted) continue;   // 用户已从云端删除：不补索引、不补传
                        try
                        {
                            var fi = new FileInfo(b.File);
                            if (!fi.Exists || fi.Length != e.Size || fi.LastWriteTimeUtc.Ticks != e.Mtime) continue;
                        }
                        catch { continue; }
                        if (!string.IsNullOrEmpty(e.Remote))
                            expected[e.Remote] = CloudIndex.BuildFor(b);
                    }
                }
            }
            if (expected.Count == 0) return;

            using var client = BuildClient();
            var doc = await GetIndexAsync(client);
            if (doc == null) return;   // 云端索引不存在/损坏：不整表重建（可能他机索引正在写），等上传路径自己补
            int missing = 0;
            foreach (var (rel, e) in expected)
                if (!doc.Entries.ContainsKey(rel)) { CloudIndex.Upsert(doc, rel, e); missing++; }
            if (missing > 0)
                await PutIndexAsync(client, doc);
        }
        catch { /* 网络不可达等：下次启动再自愈 */ }
    }

    /// <summary>待上传数量（设置页展示用）。</summary>
    public static int PendingCount
    {
        get { lock (_gate) return _channel?.Reader.Count ?? 0; }
    }

    private static void Enqueue(string file, string game, string profile, CloudIndex.Entry? entry)
    {
        if (_channel == null) return;
        lock (_gate)
        {
            // 用户已从云端删除（按钮显「已删重传」）：任何自动路径都不得补传，只能由该按钮手动重传
            if (_state.Uploaded.TryGetValue(file, out var de) && de.Deleted) return;
            if (_inFlight.Contains(file)) return;
            if (IsRegisteredCurrent(file)) return;
            if (!_inFlight.Add(file)) return;
            _channel.Writer.TryWrite(new Job(file, game, profile, entry));
        }
    }

    /// <summary>自动上传闸门：登记表中 Deleted=true 的备份（用户已从云端删除，按钮显「已删重传」）
    /// 对一切自动/批量上传路径跳过；恢复上传的唯一入口是备份行的手动单份上传。</summary>
    internal static bool IsAutoUploadSkipped(string file)
    {
        lock (_gate)
            return _state.Uploaded.TryGetValue(file, out var e) && e.Deleted;
    }

    private static bool IsRegisteredCurrent(string file)
    {
        if (!_state.Uploaded.TryGetValue(file, out var e)) return false;
        if (e.Deleted) return false;
        try
        {
            var fi = new FileInfo(file);
            return fi.Exists && fi.Length == e.Size && fi.LastWriteTimeUtc.Ticks == e.Mtime;
        }
        catch { return false; }
    }

    private static async Task WorkerLoop()
    {
        var reader = _channel!.Reader;
        while (await reader.WaitToReadAsync())
        {
            // 一批用同一个 client（同一份设置），设置变更在下一批生效
            var batch = new List<Job>();
            while (batch.Count < 50 && reader.TryRead(out var job)) batch.Add(job);

            // 设置在入队后被关闭：这批不发送（保留未登记状态，重新开启后对账补传）
            if (!Configured)
            {
                ReleaseJobs(batch);
                await Task.Delay(TimeSpan.FromSeconds(30));
                continue;
            }

            WebDavClient? client = null;
            try { client = BuildClient(); }
            catch (Exception ex)
            {
                ReleaseJobs(batch);
                BackgroundFailed?.Invoke("云同步初始化失败：" + ex.Message);
                await Task.Delay(TimeSpan.FromMinutes(2));   // 配置错误时限速，避免每个备份都报
                continue;
            }

            foreach (var job in batch)
            {
                try
                {
                    // 排队期间用户已从云端删除（转为「已删重传」态）：放弃这次自动上传，等手动重传
                    if (IsAutoUploadSkipped(job.Local)) continue;
                    await UploadOne(client, job);
                    UploadStateChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    // 单个失败不阻塞其余；安排退避自动重试（30s→1m→2m…封顶 30 分钟），
                    // 不再只能等下次启动/保存设置对账。前 3 次失败提示，之后静默重试防 toast 轰炸。
                    int fails;
                    Job retryJob = job;
                    lock (_gate)
                    {
                        fails = _upFails.TryGetValue(job.Local, out var n) ? n + 1 : 1;
                        _upFails[job.Local] = fails;
                    }
                    if (fails <= 3)
                        BackgroundFailed?.Invoke($"云备份上传失败（{job.Game}）：{ex.Message}，将在稍后自动重试");
                    int delayMs = Math.Min(1800_000, 30_000 * (1 << Math.Min(fails - 1, 6)));
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(delayMs);
                            Enqueue(retryJob.Local, retryJob.Game, retryJob.Profile, retryJob.Entry);
                        }
                        catch { /* 进程退出等：本次重试放弃，下次启动 Reconcile 兜底 */ }
                    });
                }
                finally
                {
                    lock (_gate) _inFlight.Remove(job.Local);
                }
            }
        }
    }

    private static void ReleaseJobs(List<Job> batch)
    {
        lock (_gate) foreach (var j in batch) _inFlight.Remove(j.Local);
    }

    private static async Task UploadOne(WebDavClient client, Job job)
    {
        if (!File.Exists(job.Local))
        {
            // 本地备份已删除：云端作为异地副本保留；本地登记清掉
            lock (_gate) _state.Uploaded.Remove(job.Local);
            SaveState();
            return;
        }
        var fi = new FileInfo(job.Local);
        string profileSeg = string.IsNullOrEmpty(job.Profile) ? "_通用" : BackupService.SafeName(job.Profile);
        string rel = $"{BackupService.SafeName(job.Game)}/{profileSeg}/{Path.GetFileName(job.Local)}";
        string remote = await client.UploadAsync(job.Local, rel);
        lock (_gate)
        {
            _upFails.Remove(job.Local);   // 成功：退避计数清零
            // 重传场景：旧 Remote（已被删除的云端路径）的 KnownRemote 悬挂键一并清掉
            string? oldRemote = _state.Uploaded.TryGetValue(job.Local, out var oldE) ? oldE.Remote : null;
            _state.Uploaded[job.Local] = new StateEntry
            {
                Size = fi.Length,
                Mtime = fi.LastWriteTimeUtc.Ticks,
                Remote = remote,
                When = DateTime.Now,
                Deleted = false,
            };
            _state.KnownRemote[remote] = 0;   // 本机上传的内容天然是「已知」
            if (!string.IsNullOrEmpty(oldRemote)
                && !string.Equals(oldRemote, remote, StringComparison.OrdinalIgnoreCase))
                _state.KnownRemote.Remove(oldRemote);
            SaveState();
        }

        // 更新云端索引 index.json：把这份 zip 的存档时间带给云端列表（切换比较/云端浏览用）。
        // 先 GET 现有索引合并（保留其他机器写入的条目）再 PUT；必须用返回值 remote 作键——
        // 412 同名冲突时 UploadAsync 会加机器名后缀，最终路径与请求时不同。
        // 同时自报游戏身份（AppId + 全部名字安全化后并入别名表）——两台电脑名字不一致时
        // 凭 AppId/别名互相认领云端目录。失败仅通知不回滚登记：zip 已传成功，索引缺失由启动对账自愈。
        if (job.Entry != null)
        {
            try
            {
                var doc = await GetIndexAsync(client) ?? new CloudIndex.Doc();
                CloudIndex.Upsert(doc, remote, job.Entry);
                var game = Store.Games.FirstOrDefault(x =>
                    string.Equals(BackupService.SafeName(x.DisplayName), BackupService.SafeName(job.Game), StringComparison.OrdinalIgnoreCase));
                if (game != null)
                {
                    CloudIndex.UpsertIdentity(doc, remote.Split('/')[0], game.AppId, new[]
                    {
                        BackupService.SafeName(game.DisplayName),
                        BackupService.SafeName(game.NameCN ?? ""),
                        BackupService.SafeName(game.NameEN ?? ""),
                    });
                }
                await PutIndexAsync(client, doc);
            }
            catch (Exception ex)
            {
                BackgroundFailed?.Invoke($"云端索引更新失败（{job.Game}）：{ex.Message}");
            }
        }
    }

    /// <summary>手动「立即上传全部」（设置页）：直接逐份上传并返回结果，跳过队列等待。
    /// overrideClient 允许用对话框中尚未保存的配置上传；null 则按已保存设置构建。</summary>
    public static async Task<(int ok, int fail, List<string> errors)> UploadAllAsync(
        WebDavClient? overrideClient = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        int ok = 0, fail = 0;
        var errors = new List<string>();
        WebDavClient client;
        try { client = overrideClient ?? BuildClient(); }
        catch (Exception ex) { return (0, 0, new List<string> { ex.Message }); }

        var jobs = new List<Job>();
        foreach (var g in Store.Games)
        {
            if (g.Archived) continue;   // 批量上传只传在库游戏
            foreach (var b in g.Backups)
                if (!string.IsNullOrEmpty(b.File))
                    jobs.Add(new Job(b.File, g.DisplayName, b.Profile ?? "", CloudIndex.BuildFor(b)));
        }

        int i = 0;
        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            bool mine = false;
            lock (_gate)
            {
                // 用户已从云端删除（显「已删重传」）的记录：批量上传也不复活，只能逐份手动重传
                if (IsAutoUploadSkipped(job.Local)) continue;
                if (IsRegisteredCurrent(job.Local)) continue;
                if (!_inFlight.Add(job.Local)) continue;   // 后台队列正在传：不重复
                mine = true;
            }
            try
            {
                progress?.Report($"正在上传 {++i}/{jobs.Count}：{job.Game}");
                await UploadOne(client, job);
                ok++;
            }
            catch (Exception ex)
            {
                fail++;
                if (errors.Count < 5) errors.Add($"{job.Game}：{ex.Message}");
            }
            finally { lock (_gate) if (mine) _inFlight.Remove(job.Local); }
        }
        return (ok, fail, errors);
    }

    // ── 自动收集下载（云端 → 本机备份列表；不碰 live）──

    /// <summary>收集结束汇报 UI：新增备份数、新玩家数、首个错误（null=无错误）。</summary>
    public static event Action<(int backups, int players, string? error)>? CollectFinished;

    /// <summary>启动后延迟收集全部自动云存档游戏（不阻塞 UI）。stopCt 在应用退出时取消，
    /// 取消后静默结束、不发结束 toast（窗口已关）。</summary>
    private static async Task AutoCollectAllAsync(CancellationToken stopCt = default)
    {
        if (!Configured) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(12), stopCt);   // 避开启动繁忙
        }
        catch (OperationCanceledException) { return; }
        await ReconcileIndexAsync();   // 先自愈云端索引（补他机覆盖丢失的条目），再收集
        int newBackups = 0, newPlayers = 0;
        string? firstErr = null;
        foreach (var g in Store.Games.ToList())
        {
            if (stopCt.IsCancellationRequested) return;
            if (g.Archived || !g.CloudAuto) continue;
            try
            {
                var (b, p) = await CollectGameAsync(g, ct: stopCt);
                newBackups += b; newPlayers += p;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { firstErr ??= ex.Message; }
        }
        if (!stopCt.IsCancellationRequested)
            CollectFinished?.Invoke((newBackups, newPlayers, firstErr));
    }

    /// <summary>收集单个游戏：枚举该游戏认领的**全部**云端目录（手动映射/AppId/别名/默认名，
    /// 跨设备名字不一致时旧目录也能被发现），下载本机缺的 zip 并导入；
    /// 云端新玩家的 zip 直接落库即完成该玩家建档（zip 即玩家档案）。返回 (实际新增备份, 新玩家)。
    /// client 由外部提供时不释放（设置页测试上传复用同一连接）。</summary>
    public static async Task<(int backups, int players)> CollectGameAsync(
        GameEntry g, WebDavClient? client = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // 去重键用稳定的 g.Id：游戏改名后 SafeName(显示名) 变化，旧逻辑下改名瞬间可
        // 并发跑两个收集；同名游戏则可能错误地互相挡掉
        string collectKey = g.Id;
        lock (_gate)
        {
            if (!_collecting.Add(collectKey)) return (0, 0);
        }
        WebDavClient own = client ?? BuildClient();
        try
        {
            // 云端索引 1 次小 GET：存档时间关联 + 游戏身份段（AppId/别名，拉到后认领更准、顺带孤儿墓碑清扫）
            var doc = await GetIndexAsync(own, ct);
            UpdateIdentityCache(doc);
            int backups = 0, players = 0;
            string dir = Path.Combine(Store.BaseDir, "cache", "cloud");
            Directory.CreateDirectory(dir);

            foreach (string cloudDir in CloudDirsFor(g))
            {
                ct.ThrowIfCancellationRequested();
                var remote = await own.ListAsync(cloudDir, progress, ct);
                try { CloudIndex.Link(remote, doc); } catch { }   // 读不到索引按无索引处理（SaveAt=null 回退上传时间）
                lock (_gate)
                {
                    _gameIndex[cloudDir] = remote;
                    _indexAt = DateTime.Now;
                }
                GcDismissed(cloudDir, remote);   // 顺手回收该目录段云端已删除的墓碑，防黑名单无限增长

                var wanted = PlanDownloads(g, remote, cloudDir);
                // 本机已有同名文件的云端条目 = 内容已知；其余下载成功后逐一登记
                var wantedRel = wanted.Select(f => f.RelPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var f in remote)
                    if (!wantedRel.Contains(f.RelPath)) MarkRemoteKnown(f.RelPath, f.Modified);

                foreach (var f in wanted)
                {
                    ct.ThrowIfCancellationRequested();
                    string tmp = Path.Combine(dir, f.Name);
                    for (int n = 2; File.Exists(tmp); n++)   // 同批/残留同名：临时加序号
                        tmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(f.Name) + $"_{n}.zip");

                    // 下载/导入失败（含 zip 校验失败）时必须删掉残留 tmp，否则每次收集都堆积垃圾
                    bool importDone = false;
                    try
                    {
                        progress?.Report("下载：" + f.Name);
                        await own.DownloadAsync(f.RelPath, tmp, ct);

                        // zip 即玩家档案：云端新玩家的 zip 直接落库（归属 manifest.Profile）即完成建档
                        string profile = BackupService.ReadManifest(tmp)?.Profile ?? "";
                        bool newPlayer = profile.Length > 0 && !ProfileService.ListSlots(g).Contains(profile);
                        int before = g.Backups.Count;
                        var adopted = BackupService.AdoptDownloadedZip(g, tmp);
                        importDone = true;
                        if (g.Backups.Count > before) backups++;
                        if (newPlayer) players++;
                        MarkRemoteKnown(f.RelPath, f.Modified);   // 已并入本机（备份记录）→ 内容已知
                        // 登记为「云端已存」：备份行禁用回传，杜绝下载包原样上传撞 412 造成云端重复副本
                        if (adopted != null) MarkAdoptedFromCloud(adopted.File, f.RelPath);
                    }
                    finally
                    {
                        if (!importDone) { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
                    }
                }
            }
            if (backups > 0 || players > 0)
            {
                // ProfileSlots 是 UI 绑定的 ObservableCollection：后台收集线程不得直接改，
                // 必须封送到 UI 线程（与 BackupService.OnUi 同口径）
                OnUi(() => g.RefreshProfiles());
                UploadStateChanged?.Invoke();
            }
            return (backups, players);
        }
        finally
        {
            lock (_gate) _collecting.Remove(collectKey);
            if (client == null) own.Dispose();
        }
    }

    /// <summary>后台线程改 UI 绑定集合时封送到 UI 线程；无 Application（冒烟/无界面）时直接执行。</summary>
    private static void OnUi(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) a();
        else d.Invoke(a);
    }

    /// <summary>按文件名预筛：本机已有同名 zip 的不下载（精确判重由 Adopt 的时间+大小兜底）；
    /// 用户主动删除过的（不再下载黑名单）也不下载，避免云端副本反复复活。
    /// 墓碑按「云端目录段/文件名」匹配（同一游戏可能有多个候选目录）。</summary>
    internal static List<CloudRemoteFile> PlanDownloads(GameEntry g, List<CloudRemoteFile> remote, string cloudDir)
    {
        var localNames = g.Backups
            .Where(b => !string.IsNullOrEmpty(b.File))
            .Select(b => Path.GetFileName(b.File))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return remote.Where(f => !localNames.Contains(f.Name) && !IsDismissed(cloudDir, f.Name)).ToList();
    }

    // ── 跨设备目录认领（AppId → 别名 → 手动映射 → 默认名）──

    /// <summary>本机游戏在云端可能的目录段集合（按认领优先级，去重保序）：
    /// ① 手动映射（云端管理窗「归属到…」，用户明确指派，覆盖自动判断）
    /// ② AppId 相同的索引记录目录（Steam 游戏最强身份）
    /// ③ 别名命中（索引记录别名含本机任一名字——两台电脑名字不一致时互认）
    /// ④ 默认名（本机显示名安全化）。识别身份依赖云端索引缓存（拉过索引才有 AppId/别名）。</summary>
    internal static List<string> CloudDirsFor(GameEntry g)
    {
        string def = BackupService.SafeName(g.DisplayName);
        var dirs = new List<string>();
        void Add(string d)
        {
            if (!string.IsNullOrWhiteSpace(d) && !dirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                dirs.Add(d);
        }
        var myNames = new List<string> { def, BackupService.SafeName(g.NameCN ?? ""), BackupService.SafeName(g.NameEN ?? "") }
            .Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool defBlocked = false;
        lock (_gate)
        {
            // ① 手动映射：值 = 本机游戏安全名（用户明确指派，不受 AppId 闸限制）
            foreach (var kv in _state.Mapping)
                if (string.Equals(kv.Value, def, StringComparison.OrdinalIgnoreCase)) Add(kv.Key);
            // ② ③ 索引身份段
            var games = _idDoc?.Games;
            if (games != null)
            {
                if (g.AppId > 0)
                    foreach (var kv in games)
                        if (kv.Value.A == g.AppId) Add(kv.Key);
                foreach (var kv in games)
                {
                    if (dirs.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)) continue;
                    var al = kv.Value.Al;
                    if (al == null || !al.Any(a => myNames.Contains(a))) continue;
                    // 串档闸：别名撞名但该云端目录登记的是另一个 Steam 游戏（AppId 都为正且不等）
                    // → 只是显示名安全化后撞名，不认领；AppId 未知（0）的一方保持原认领行为
                    if (g.AppId > 0 && kv.Value.A > 0 && kv.Value.A != g.AppId) continue;
                    Add(kv.Key);
                }
                // ④ 默认名同样要过闸：两款不同 Steam 游戏显示名完全同名时，默认目录已登记在别人
                // AppId 名下，本机不得把它当自己默认目录下载（手动映射仍可显式覆盖）
                if (g.AppId > 0 && games.TryGetValue(def, out var defMeta)
                    && defMeta.A > 0 && defMeta.A != g.AppId)
                    defBlocked = true;
            }
        }
        if (!defBlocked) Add(def);
        return dirs;
    }

    /// <summary>云端索引身份段并入缓存；每次拉到新索引后顺带跑孤儿墓碑清扫
    /// （此时候选目录段已含 AppId/别名命中段，清扫不会误删仍有效的墓碑）。</summary>
    internal static void UpdateIdentityCache(CloudIndex.Doc? doc)
    {
        if (doc == null) return;
        lock (_gate)
        {
            _idDoc = doc;
            SweepOrphanDismissed();
        }
    }

    /// <summary>孤儿云端目录判定：给定云端实际存在的目录段集合，返回没有任何本机游戏认领
    /// （手动映射/AppId/别名/默认名均不命中）的目录——云端管理窗「未匹配」按钮据此列出待匹配项。</summary>
    internal static List<string> FindUnclaimedDirs(IEnumerable<string> cloudDirs)
    {
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in Store.Games)
            foreach (var d in CloudDirsFor(g)) claimed.Add(d);
        return cloudDirs
            .Where(d => !claimed.Contains(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>手动归属：把云端目录指派给本机游戏（gameKey 为空 = 解除）。立即落盘。</summary>
    public static void SetMapping(string cloudDir, string? gameKey)
    {
        if (string.IsNullOrWhiteSpace(cloudDir)) return;
        lock (_gate)
        {
            bool changed = false;
            if (string.IsNullOrWhiteSpace(gameKey))
                changed = _state.Mapping.Remove(cloudDir);
            else if (!string.Equals(_state.Mapping.TryGetValue(cloudDir, out var cur) ? cur : null, gameKey, StringComparison.OrdinalIgnoreCase))
            {
                // 一个云端目录只归一个本机游戏；同一本机游戏可关联多个云端目录
                _state.Mapping[cloudDir] = gameKey!;
                changed = true;
            }
            if (changed) SaveState();
        }
    }

    /// <summary>云端目录当前被手动指派给的本机游戏安全名（未指派返回 null）。</summary>
    public static string? GetMapping(string cloudDir)
    {
        lock (_gate) return _state.Mapping.TryGetValue(cloudDir, out var v) ? v : null;
    }

    /// <summary>黑名单登记键：游戏名安全化 + 文件名（不带云端目录段，玩家段路径差异不影响匹配）。</summary>
    internal static string DismissKey(string gameKey, string fileName) => gameKey + "/" + fileName;

    /// <summary>该云端文件是否被用户「删除且不再自动下载」。</summary>
    internal static bool IsDismissed(string gameKey, string fileName)
    {
        lock (_gate) return _state.Dismissed.ContainsKey(DismissKey(gameKey, fileName));
    }

    /// <summary>用户主动删除本机备份后调用：云端仍存在的同名文件记入「不再自动下载」黑名单并落盘，
    /// 避免自动收集（启动对账/切换玩家云端检查）反复把它下载回来。只登记云端已知的文件
    /// （曾上传登记 / 曾下载或上传时记过 KnownRemote），纯本机备份不登记以免黑名单无谓膨胀。
    /// 未配置云同步时不做任何事。files 为被删记录的本地 zip 路径集合。
    /// 墓碑键 = 「云端目录段/文件名」：跨设备目录可能不止一个（AppId/别名/映射认领的旧目录），
    /// 从 KnownRemote 反查文件实际所在段；查不到时回退本机默认名段。</summary>
    public static void OnLocalBackupsDeleted(GameEntry g, IEnumerable<string> files)
    {
        if (!Configured) return;
        bool changed = false;
        lock (_gate)
        {
            foreach (string file in files)
            {
                if (string.IsNullOrEmpty(file)) continue;
                string fileName = Path.GetFileName(file);
                if (fileName.Length == 0) continue;
                foreach (var seg in RemoteSegsForLocked(g, file))
                {
                    _state.Dismissed[DismissKey(seg, fileName)] = DateTime.Now;
                    changed = true;
                }
            }
            if (changed) SaveState();
        }
    }

    /// <summary>本地删除弹窗用：云端是否仍有该文件的已知副本（曾上传登记或云端清单见过）。
    /// 纯本机备份（从未上云）返回 false，调用方无须询问。</summary>
    public static bool HasKnownRemoteCopy(GameEntry g, string localFile)
    {
        if (!Configured || string.IsNullOrEmpty(localFile)) return false;
        lock (_gate) return RemoteSegsForLocked(g, localFile).Count > 0;
    }

    /// <summary>按用户选择把云端副本真删掉：对每个已知目录段发 DELETE、清云端索引条目、
    /// 清「不再下载」黑名单、上传登记置 Deleted（对账不再补传）。返回成功删除的目录段数。
    /// 单段失败不影响其余段；全部失败返回 0（调用方按「云端保留」口径提示，黑名单兜底已由
    /// OnLocalBackupsDeleted 建立，不会复活）。</summary>
    public static async Task<int> DeleteRemoteCopiesAsync(GameEntry g, string localFile, CancellationToken ct = default)
    {
        if (!Configured || string.IsNullOrEmpty(localFile)) return 0;
        string fileName = Path.GetFileName(localFile);
        if (fileName.Length == 0) return 0;

        // 收集云端完整路径（KnownRemote key 本身就是完整相对路径，含玩家子目录）
        var candidateDirs = CloudDirsFor(g);
        List<string> remotePaths;
        List<string> segs;  // 仍需段名用于 Dismissed / Uploaded 清理
        lock (_gate)
        {
            segs = RemoteSegsForLocked(g, localFile);
            if (segs.Count == 0) return 0;
            // KnownRemote 命中：key 本身就是完整云端路径（游戏/玩家/文件.zip）
            remotePaths = _state.KnownRemote.Keys
                .Where(k => k.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase)
                            && candidateDirs.Contains(k.Split('/')[0], StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Uploaded 回退：用登记的 Remote 完整路径
            if (remotePaths.Count == 0
                && _state.Uploaded.TryGetValue(localFile, out var up) && !up.Deleted
                && !string.IsNullOrEmpty(up.Remote))
                remotePaths.Add(up.Remote);
        }

        using var client = BuildClient();
        // 逐段记录成功：只能清成功段的索引条目/墓碑；失败段保留原状，下次操作可重试。
        // 旧逻辑 ok>0 就清所有段，部分失败时失败段的副本失去登记、随后被对账补传"复活"。
        var okPaths = new List<string>(remotePaths.Count);
        var failPaths = new List<string>();
        foreach (var path in remotePaths)
        {
            try { await client.DeleteAsync(path, ct); okPaths.Add(path); }
            catch { failPaths.Add(path); }
        }
        if (okPaths.Count == 0) return 0;
        try
        {
            var doc = await GetIndexAsync(client, ct);
            if (doc != null)
            {
                bool changed = false;
                foreach (var path in okPaths)
                    if (doc.Entries.Remove(path)) changed = true;
                if (changed) await PutIndexAsync(client, doc, ct);
            }
        }
        catch { /* 索引清理失败无害：zip 已不存在，条目成孤儿不会被引用 */ }
        lock (_gate)
        {
            // 墓碑键是「游戏段/文件名」，一个段下可能有多个玩家子目录副本。
            // 只有该段下所有副本都删成功才移除墓碑；段内还有失败副本时保留，防自动收集复活
            var failSegs = failPaths.Select(p => p.Split('/')[0])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var p in okPaths)
            {
                string seg = p.Split('/')[0];
                if (!failSegs.Contains(seg))
                    _state.Dismissed.Remove(DismissKey(seg, fileName));
            }
            // 所有候选副本都删掉才置 Deleted（对账不再补传）；仍有失败时保持 false，用户可再次删除
            if (failPaths.Count == 0 && _state.Uploaded.TryGetValue(localFile, out var up)) up.Deleted = true;
            SaveState();
        }
        return okPaths.Count;
    }

    /// <summary>反查本地 zip 所在的云端目录段（调用方须持 _gate）。
    /// KnownRemote 命中的段 ∩ 该游戏的候选目录段；上传登记仍在但反查为空时回退登记的段 / 默认段。</summary>
    private static List<string> RemoteSegsForLocked(GameEntry g, string localFile)
    {
        var segs = new List<string>();
        string fileName = Path.GetFileName(localFile);
        if (fileName.Length == 0) return segs;
        var candidateDirs = CloudDirsFor(g);
        segs.AddRange(_state.KnownRemote.Keys
            .Where(k => k.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Split('/')[0])
            .Where(s => candidateDirs.Contains(s, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        if (segs.Count > 0) return segs;
        bool known = _state.Uploaded.TryGetValue(localFile, out var up) && !up.Deleted;
        if (!known) return segs;
        // 上传登记仍在：登记的 Remote 首段即云端实际段；缺失时回退默认段
        string seg2 = up!.Remote?.Split('/')[0] ?? "";
        if (string.IsNullOrEmpty(seg2) || !candidateDirs.Contains(seg2, StringComparer.OrdinalIgnoreCase))
            seg2 = BackupService.SafeName(g.DisplayName);
        segs.Add(seg2);
        return segs;
    }

    /// <summary>解除「不再自动下载」登记（云端浏览窗手动下载 / 切换玩家选「云端最新」时调用：
    /// 用户明确要这份，恢复其自动收集资格）。</summary>
    public static void UndismissCloudBackup(string gameKey, string fileName)
    {
        lock (_gate)
        {
            if (_state.Dismissed.Remove(DismissKey(gameKey, fileName))) SaveState();
        }
    }

    /// <summary>回收区还原时调用：删除时游戏可能已不在库、拿不到目录段映射，
    /// 故按文件名清除该文件在所有目录段下的「不再下载」墓碑（键格式 seg/fileName）。</summary>
    public static void UndismissByFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return;
        lock (_gate)
        {
            var dead = _state.Dismissed.Keys
                .Where(k => k.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (dead.Count == 0) return;
            foreach (var k in dead) _state.Dismissed.Remove(k);
            SaveState();
        }
    }

    /// <summary>孤儿墓碑清扫：游戏被移出库或改名后，其墓碑键的目录前缀已无主，
    /// 永远不会再被该游戏的云端清单 GC 命中（GcDismissed 只按在库游戏的候选目录段回收），
    /// 启动/每次拉到新索引后按「在库游戏候选目录段 ∪ 手动映射键」清掉无主墓碑，防止黑名单无限增长。
    /// 返回清除条数。</summary>
    internal static int SweepOrphanDismissed()
    {
        lock (_gate)
        {
            if (_state.Dismissed.Count == 0) return 0;
            var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in Store.Games)
                foreach (var d in CloudDirsFor(g)) alive.Add(d);
            foreach (var k in _state.Mapping.Keys) alive.Add(k);
            var dead = _state.Dismissed.Keys
                .Where(k => !alive.Contains(k.Substring(0, k.IndexOf('/'))))
                .ToList();
            foreach (var k in dead) _state.Dismissed.Remove(k);
            if (dead.Count > 0) SaveState();
            return dead.Count;
        }
    }

    /// <summary>墓碑垃圾回收：对照该云端目录段当前文件清单，把云端已不存在的墓碑移除并落盘
    /// （云端都没有了就无从复活，登记失去意义），防止黑名单随删除操作无限增长。
    /// gameKey 参数 = 云端目录段（同一游戏可能有多个认领目录，各自对照各自清单）。
    /// 在收集下载与切换游戏批量核验拿到最新云端清单后调用。返回清除条数。</summary>
    internal static int GcDismissed(string gameKey, List<CloudRemoteFile> remote)
    {
        if (remote == null || _state.Dismissed.Count == 0) return 0;
        var names = remote.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> dead;
        lock (_gate)
        {
            dead = _state.Dismissed.Keys
                .Where(k => k.StartsWith(gameKey + "/", StringComparison.OrdinalIgnoreCase) &&
                            !names.Contains(k.Substring(gameKey.Length + 1)))
                .ToList();
            foreach (var k in dead) _state.Dismissed.Remove(k);
            if (dead.Count > 0) SaveState();
        }
        return dead.Count;
    }

    // ── 手动单份上传 / ☁ 标记查询 / 云端索引 ──

    /// <summary>手动上传单份备份（备份行「上传」按钮）。成功后该记录 ☁ 标记点亮。</summary>
    public static async Task UploadSingleAsync(GameEntry g, BackupRecord b, CancellationToken ct = default)
    {
        if (!Configured)
            throw new InvalidOperationException("云功能未开启：请先在「设置 → 云同步」中配置并测试连接");
        if (!File.Exists(b.File)) throw new InvalidOperationException("备份文件不存在，无法上传");
        // 防御闸：云端已存（本机上传成功过 / 云端下载入库登记过）且文件未变 → 静默 no-op。
        // UI 按钮此时本就禁用；「已删重传」态 IsUploaded=false 不挡，文件被改动后大小/mtime 不符也不挡。
        if (IsUploaded(b.File)) return;
        using var client = BuildClient();
        await UploadOne(client, new Job(b.File, g.DisplayName, b.Profile ?? "", CloudIndex.BuildFor(b)));
        b.CloudUploaded = true;
        b.CloudDeleted = false;
        UploadStateChanged?.Invoke();
    }

    /// <summary>该本地 zip 是否已上传（有登记且大小/时间未变化）。</summary>
    public static bool IsUploaded(string file)
    {
        lock (_gate) return IsRegisteredCurrent(file);
    }

    /// <summary>按登记表刷新某游戏全部备份的 ☁ 运行时标记（含已删重传态）。</summary>
    public static void RefreshCloudFlags(GameEntry g)
    {
        foreach (var b in g.Backups)
        {
            b.CloudUploaded = IsUploaded(b.File);
            b.CloudDeleted = IsRemoteDeleted(b.File);
        }
    }

    /// <summary>该本地备份是否被标记为「云端已删除」（登记表 Deleted 位）。</summary>
    public static bool IsRemoteDeleted(string file)
    {
        lock (_gate)
        {
            if (!_state.Uploaded.TryGetValue(file, out var e)) return false;
            return e.Deleted;
        }
    }

    /// <summary>反查：给定云端相对路径，找对应的本地备份文件路径（找不到返回 null）。
    /// 用于云端浏览窗删除后定位本地备份记录。</summary>
    public static string? FindLocalByRemote(string relPath)
    {
        if (string.IsNullOrEmpty(relPath)) return null;
        lock (_gate)
        {
            foreach (var kv in _state.Uploaded)
                if (string.Equals(kv.Value.Remote, relPath, StringComparison.OrdinalIgnoreCase))
                    return kv.Key;
        }
        return null;
    }

    /// <summary>标记某本地备份的云端副本已被用户删除：登记表置 Deleted 位、清 KnownRemote，
    /// 对应备份记录切到「已删重传」态。持久化（重启后仍显示已删）。</summary>
    public static void MarkCloudDeleted(string localFile)
    {
        if (string.IsNullOrEmpty(localFile)) return;
        string? remote = null;
        lock (_gate)
        {
            if (_state.Uploaded.TryGetValue(localFile, out var e))
            {
                e.Deleted = true;
                remote = e.Remote;
                if (!string.IsNullOrEmpty(remote)) _state.KnownRemote.Remove(remote);
                SaveState();
            }
            // 云端副本被删：「不再下载」墓碑一并清除（云端都没有了，防复活失去意义），防黑名单无限增长。
            // remote 首段即游戏安全名，与墓碑键前缀同源
            string gk = remote?.Split('/')[0] ?? "";
            if (gk.Length > 0 && _state.Dismissed.Remove(DismissKey(gk, Path.GetFileName(localFile))))
                SaveState();
        }
        var b = Store.Games.SelectMany(g => g.Backups).FirstOrDefault(x => x.File == localFile);
        if (b != null) { b.CloudDeleted = true; b.CloudUploaded = false; }
        UploadStateChanged?.Invoke();
    }

    /// <summary>实时核验单份备份的云端存在性（用户点击备份行时触发，作为冗余精确校验）。
    /// 跨设备认领的全部候选目录逐一查（命中即停）；云端仍在 → 清删除标记、置已传；
    /// 云端已无 → 标记已删重传。返回云端是否存在。</summary>
    public static async Task<bool> VerifyCloudExistsAsync(GameEntry g, BackupRecord b, CancellationToken ct = default)
    {
        if (!Configured || string.IsNullOrEmpty(b.File)) return false;
        string fileName = Path.GetFileName(b.File);
        using var client = BuildClient();
        CloudRemoteFile? hit = null;
        foreach (string cloudDir in CloudDirsFor(g))
        {
            var remote = await client.ListAsync(cloudDir, null, ct);
            // 按文件名匹配（remote 路径可能有机器名后缀，文件名是稳定标识）
            hit = remote.FirstOrDefault(f => string.Equals(f.Name, fileName, StringComparison.OrdinalIgnoreCase));
            if (hit != null) break;
        }
        if (hit != null)
        {
            lock (_gate)
            {
                if (_state.Uploaded.TryGetValue(b.File, out var e))
                {
                    e.Deleted = false;
                    e.Remote = hit.RelPath;
                    e.Size = new FileInfo(b.File).Length;
                    e.Mtime = new FileInfo(b.File).LastWriteTimeUtc.Ticks;
                }
                else
                {
                    // 存量下载包（旧版未写登记表）：云端确有同名 → 补登记，持久禁用手动回传
                    try
                    {
                        var fi = new FileInfo(b.File);
                        if (fi.Exists)
                            _state.Uploaded[b.File] = new StateEntry
                            {
                                Size = fi.Length,
                                Mtime = fi.LastWriteTimeUtc.Ticks,
                                Remote = hit.RelPath,
                                When = DateTime.Now,
                                Deleted = false,
                            };
                    }
                    catch { }
                }
                _state.KnownRemote[hit.RelPath] = 0;
                SaveState();
            }
            b.CloudDeleted = false;
            b.CloudUploaded = true;
        }
        else
        {
            MarkCloudDeleted(b.File);
        }
        UploadStateChanged?.Invoke();
        return hit != null;
    }

    /// <summary>切换游戏时的批量云端核验：一次列出该游戏云目录（1 轮 PROPFIND），
    /// 按文件名对账该游戏全部本地备份的云状态，结果写回登记表持久化（重启后保留）。
    /// 云端在 → 已传；云端没有但登记过 → 已删重传；从未上传 → 保持未上传。
    /// 失败静默（断网/服务器抖动保持登记表现状，不打扰）。</summary>
    public static async Task VerifyGameCloudStatusAsync(GameEntry g, CancellationToken ct = default)
    {
        if (!Configured || g.Backups.Count == 0) return;
        List<CloudRemoteFile> remote;
        try { remote = await FetchGameIndexAsync(g, ct); }   // 内部已按候选目录段逐段 GC 墓碑
        catch { return; }
        bool changed = false;
        lock (_gate)
        {
            foreach (var b in g.Backups)
            {
                if (string.IsNullOrEmpty(b.File)) continue;
                var hit = remote.FirstOrDefault(f =>
                    string.Equals(f.Name, Path.GetFileName(b.File), StringComparison.OrdinalIgnoreCase));
                if (hit != null)
                {
                    if (_state.Uploaded.TryGetValue(b.File, out var e))
                    {
                        if (e.Deleted) { e.Deleted = false; changed = true; }
                        e.Remote = hit.RelPath;
                        try
                        {
                            var fi = new FileInfo(b.File);
                            e.Size = fi.Length;
                            e.Mtime = fi.LastWriteTimeUtc.Ticks;
                        }
                        catch { }
                    }
                    else
                    {
                        // 存量数据：旧版下载入库的包只在运行时标过「已存」、没写登记表（重启回退可点）。
                        // 云端确有同名文件（412 保护保证同名即同一份）→ 补登记，持久禁用手动回传。
                        try
                        {
                            var fi = new FileInfo(b.File);
                            if (fi.Exists)
                            {
                                _state.Uploaded[b.File] = new StateEntry
                                {
                                    Size = fi.Length,
                                    Mtime = fi.LastWriteTimeUtc.Ticks,
                                    Remote = hit.RelPath,
                                    When = DateTime.Now,
                                    Deleted = false,
                                };
                                changed = true;
                            }
                        }
                        catch { }
                    }
                    if (!_state.KnownRemote.ContainsKey(hit.RelPath))
                    {
                        _state.KnownRemote[hit.RelPath] = 0;
                        changed = true;
                    }
                    b.CloudDeleted = false;
                    b.CloudUploaded = true;
                }
                else if (_state.Uploaded.TryGetValue(b.File, out var miss))
                {
                    // 云端没有但登记过：按已被删处理（与单条核验 VerifyCloudExistsAsync 语义一致）
                    if (!miss.Deleted)
                    {
                        miss.Deleted = true;
                        if (!string.IsNullOrEmpty(miss.Remote)) _state.KnownRemote.Remove(miss.Remote);
                        changed = true;
                    }
                    b.CloudDeleted = true;
                    b.CloudUploaded = false;
                }
                else
                {
                    // 从未上传过的记录：保持「上传云端」初态
                    b.CloudDeleted = false;
                    b.CloudUploaded = false;
                }
            }
            if (changed) SaveState();
        }
        UploadStateChanged?.Invoke();
    }

    /// <summary>实时拉取并缓存该游戏**全部候选目录**（跨设备认领）的云端文件列表（合并返回，
    /// 切换玩家前检查云端新旧 / 批量核验云状态用）。每段清单单独缓存并顺手 GC 墓碑。</summary>
    public static async Task<List<CloudRemoteFile>> FetchGameIndexAsync(GameEntry g, CancellationToken ct = default)
    {
        using var client = BuildClient();
        var doc = await GetIndexAsync(client, ct);
        UpdateIdentityCache(doc);
        var merged = new List<CloudRemoteFile>();
        foreach (string cloudDir in CloudDirsFor(g))
        {
            var remote = await client.ListAsync(cloudDir, null, ct);
            try { CloudIndex.Link(remote, doc); } catch { }   // 读不到索引按无索引处理
            lock (_gate)
            {
                _gameIndex[cloudDir] = remote;
                _indexAt = DateTime.Now;
            }
            GcDismissed(cloudDir, remote);
            merged.AddRange(remote);
        }
        return merged;
    }

    /// <summary>缓存中某玩家最新的云端文件（跨该游戏全部候选目录合并；无缓存返回 null）。</summary>
    public static CloudRemoteFile? CachedNewest(GameEntry g, string profile)
    {
        lock (_gate)
        {
            // 云端 Profile 段是 SafeName 后的玩家名，比较时须同样安全化
            string key = BackupService.SafeName(profile ?? "");
            CloudRemoteFile? best = null;
            foreach (string cloudDir in CloudDirsFor(g))
            {
                if (!_gameIndex.TryGetValue(cloudDir, out var list)) continue;
                var cand = list.Where(f => (f.Profile ?? "") == key)
                    .OrderByDescending(f => f.Modified).FirstOrDefault();
                if (cand != null && (best == null || cand.Modified > best.Modified)) best = cand;
            }
            return best;
        }
    }

    // ── 云端索引 index.json 读写 ──

    /// <summary>云端索引相对路径（云端根目录下）。</summary>
    internal static string IndexRel => CloudIndex.FileName;

    internal static async Task<CloudIndex.Doc?> GetIndexAsync(WebDavClient client, CancellationToken ct = default)
        => CloudIndex.Deserialize(await client.DownloadTextAsync(IndexRel, ct));

    internal static async Task PutIndexAsync(WebDavClient client, CloudIndex.Doc doc, CancellationToken ct = default)
        => await client.UploadTextAsync(IndexRel, CloudIndex.Serialize(doc));

    // ── 切换玩家「云端是否可能更新」判定 ──

    /// <summary>两机文件系统时钟偏差防护：云端存档时间比本机新超过该值才算"云端更新"。</summary>
    internal static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(1);

    /// <summary>切换玩家「云端是否可能更新」判定（纯函数，可单测）。
    /// 同口径比较：云端 zip 的存档时间（云端索引提供）vs 本机该玩家最新备份的存档时间——
    /// 两者都是"游戏内真实保存时刻"，替换旧版「上传时间 > 存档时间」的错位比较
    /// （别的机器昨天玩的进度今天才上传，旧逻辑会误判云端更新）。
    /// 云端无索引条目（旧包/索引缺失）时回退：已知文件（本机上传/下载过）不提示防死循环，
    /// 真正外来的新文件才用上传时间粗判。</summary>
    internal static bool ShouldPromptCloudNewer(DateTime? localSavedAt, CloudRemoteFile? newestCloud, bool isRemoteKnown)
    {
        if (newestCloud == null) return false;
        if (newestCloud.SaveAt is DateTime cloudSaved)
            return localSavedAt == null ||
                   cloudSaved > localSavedAt.Value.ToUniversalTime() + ClockTolerance;
        return !isRemoteKnown &&
               (localSavedAt == null ||
                newestCloud.Modified > localSavedAt.Value.ToUniversalTime() + ClockTolerance);
    }

    /// <summary>按已保存设置新建一个客户端（云端浏览窗/手动操作使用，调用方负责 Dispose）。</summary>
    public static WebDavClient NewClient() => BuildClient();

    /// <summary>下载某玩家云端最新备份并导入本机备份库（只下载入库，不自动还原；入库后卡槽会显示
    /// 「本地有较新备份」，玩家想还原再点一下）。跨该游戏全部候选目录（跨设备认领）找该玩家最新一份。
    /// 下载前先预检本机是否已持有同一备份（文件名/存档时间+大小），已持有则零下载直接完成登记。
    /// progress 按 0~1 回报下载字节进度（预检命中或云端无文件时不产生下载）。</summary>
    public static async Task<PrepareCloudResult> PreparePlayerFromCloud(GameEntry g, string profile,
        CancellationToken ct = default, IProgress<double>? progress = null)
    {
        var result = new PrepareCloudResult();
        using var client = BuildClient();
        string key = BackupService.SafeName(profile ?? "");
        CloudRemoteFile? newest = null;
        foreach (string cloudDir in CloudDirsFor(g))
        {
            var remote = await client.ListAsync(cloudDir, null, ct);
            var cand = remote.Where(f => (f.Profile ?? "") == key)
                .OrderByDescending(f => f.Modified).FirstOrDefault();
            if (cand != null && (newest == null || cand.Modified > newest.Modified)) newest = cand;
        }
        if (newest == null) return result;
        result.HasRemote = true;

        // 下载前预检：本机已持有同一份（同名文件，或存档时间+大小一致）就不白下载
        var have = FindLocalForRemote(g, profile ?? "", newest);
        if (have != null)
        {
            MarkRemoteKnown(newest.RelPath, newest.Modified);
            MarkAdoptedFromCloud(have.File, newest.RelPath);
            UndismissCloudBackup(newest.RelPath.Split('/')[0], newest.Name);
            result.Record = have;
            result.WasAlreadyLocal = true;
            return result;
        }

        string dir = Path.Combine(Store.BaseDir, "cache", "cloud");
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, newest.Name);
        for (int n = 2; File.Exists(tmp); n++)
            tmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(newest.Name) + $"_{n}.zip");
        try
        {
            await client.DownloadAsync(newest.RelPath, tmp, ct, progress);
            var adopted = BackupService.AdoptDownloadedZip(g, tmp);   // 落库为该玩家最新备份
            MarkRemoteKnown(newest.RelPath, newest.Modified);   // 已下载并入 → 内容已知
            if (adopted != null) MarkAdoptedFromCloud(adopted.File, newest.RelPath);  // 禁回传，防云端重复副本
            // 用户明确要这份：按其实际所在云端目录段解除「不再下载」登记
            UndismissCloudBackup(newest.RelPath.Split('/')[0], newest.Name);
            result.Record = adopted;
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        return result;
    }

    /// <summary>PreparePlayerFromCloud 的结果：HasRemote=云端是否存在该玩家备份；
    /// WasAlreadyLocal=本机已持有、本次零下载；Record=对应的本机备份记录（可能为 null）。</summary>
    public sealed class PrepareCloudResult
    {
        public bool HasRemote { get; set; }
        public bool WasAlreadyLocal { get; set; }
        public BackupRecord? Record { get; set; }
    }

    /// <summary>下载前预检：云端这份备份本机是否已入库。
    /// ①同玩家名下同名 zip（文件名是稳定标识，412 机器名后缀的原名场景也走名字精确匹配）；
    /// ②云端索引存档时间与本地 SavedAt 一致（2 秒容差）且 zip 字节数相同——内容同一。</summary>
    private static BackupRecord? FindLocalForRemote(GameEntry g, string profile, CloudRemoteFile f)
    {
        long remoteTicks = f.SaveAt?.Ticks ?? 0;
        long remoteSize = f.Size;
        foreach (var b in g.Backups)
        {
            if ((b.Profile ?? "") != profile || string.IsNullOrEmpty(b.File) || !File.Exists(b.File)) continue;
            if (string.Equals(Path.GetFileName(b.File), f.Name, StringComparison.OrdinalIgnoreCase))
                return b;
            if (remoteTicks > 0 && remoteSize > 0
                && b.SavedAt != DateTime.MinValue && b.Size == remoteSize
                && Math.Abs(b.SavedAt.ToUniversalTime().Ticks - remoteTicks) <= BackupService.LiveTimeToleranceTicks)
                return b;
        }
        return null;
    }

    private static WebDavClient BuildClient()
    {
        var s = Store.Settings;
        string pass = CloudSecret.Unprotect(s.CloudSecret);
        if (string.IsNullOrWhiteSpace(s.CloudUrl) || string.IsNullOrWhiteSpace(s.CloudUser) || string.IsNullOrEmpty(pass))
            throw new InvalidOperationException("云同步未配置完整（地址/账号/应用密码）");
        return new WebDavClient(s.CloudUrl, s.CloudUser, pass, s.CloudDir);
    }

    // ── cloud-state.json 持久化 ──

    internal static void LoadState()
    {
        WaitStateSaved();   // 读盘前等异步写链排空，避免读到上一份状态（冒烟/重启语义一致）
        try
        {
            if (File.Exists(Store.CloudStateFile))
            {
                var loaded = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(Store.CloudStateFile));
                if (loaded != null)
                {
                    loaded.KnownRemote ??= new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    loaded.Dismissed ??= new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
                    loaded.Mapping ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    // 旧版登记表没有 KnownRemote 字段：已上传文件的内容本机天然具备，据此播种
                    foreach (var e in loaded.Uploaded.Values)
                        if (!string.IsNullOrEmpty(e.Remote) && !loaded.KnownRemote.ContainsKey(e.Remote))
                            loaded.KnownRemote[e.Remote] = 0;
                    _state = loaded;
                }
            }
            else
            {
                // 文件不存在（首次运行 / 切换沙箱）：必须显式重置为空表，否则会沿用上一份内存表，
                // 之后首次 SaveState 会把旧记录写进新位置造成跨库污染
                _state = new StateFile();
            }
        }
        catch (Exception ex)
        {
            // 损坏的登记表不能直接丢内存重置——首次 SaveState 会把好记录覆盖没。
            // 先隔离成 .corrupt 文件留档，再以空表启动（对账/收集会重新建立登记）。
            try
            {
                if (File.Exists(Store.CloudStateFile))
                {
                    string quar = Store.CloudStateFile + ".corrupt." + DateTime.Now.ToString("yyyyMMddHHmmss");
                    File.Move(Store.CloudStateFile, quar);
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsm-crash.txt"),
                        $"[{DateTime.Now:HH:mm:ss}] cloud-state.json 损坏已隔离到 {quar}：{ex.Message}\n");
                }
            }
            catch { }
            _state = new StateFile();
        }
    }

    /// <summary>登记远端文件为本机已知内容（已上传或已下载并入本机），随登记表持久化。</summary>
    internal static void MarkRemoteKnown(string relPath, DateTime cloudModified = default)
    {
        if (string.IsNullOrEmpty(relPath)) return;
        lock (_gate)
        {
            _state.KnownRemote[relPath] = cloudModified == default ? 0 : cloudModified.Ticks;
            SaveState();
        }
    }

    /// <summary>该远端文件的内容是否本机已具备（已上传或已下载过）。切换玩家的「云端更新」判断用。</summary>
    public static bool IsRemoteKnown(string relPath)
    {
        if (string.IsNullOrEmpty(relPath)) return true;
        lock (_gate) return _state.KnownRemote.ContainsKey(relPath);
    }

    /// <summary>云端 zip 下载并入本机备份库后登记：这份本地文件的内容云端已存在（源路径 remoteRel）。
    /// 效果：备份行立即切「云端已存」禁用手动回传，自动对账也跳过——刚下载的包原样 PUT 回去只会
    /// 撞 412 同名保护、自动加机器名后缀造出内容相同的云端重复副本。以后云端原件被删（他机/网页端），
    /// 行核验 VerifyCloudExistsAsync 会把它转成「已删重传」，用户仍可手动传回，不堵死。</summary>
    public static void MarkAdoptedFromCloud(string localFile, string remoteRel)
    {
        if (string.IsNullOrEmpty(localFile) || string.IsNullOrEmpty(remoteRel)) return;
        lock (_gate)
        {
            try
            {
                var fi = new FileInfo(localFile);
                if (!fi.Exists) return;
                _state.Uploaded[localFile] = new StateEntry
                {
                    Size = fi.Length,
                    Mtime = fi.LastWriteTimeUtc.Ticks,
                    Remote = remoteRel,
                    When = DateTime.Now,
                    Deleted = false,
                };
                _state.KnownRemote[remoteRel] = 0;
                SaveState();
            }
            catch { /* 登记失败不致命：下次云端核验会按文件名重新对账 */ }
        }
        UploadStateChanged?.Invoke();
    }

    /// <summary>串行落盘链：锁内只抓快照（微秒级），写盘在后台排队执行，
    /// 多次连续保存自然合并为"最后一次内容"。写顺序由链条保证，不会后写覆盖先写。</summary>
    private static Task _saveChain = Task.CompletedTask;
    private static readonly object _saveChainGate = new();

    /// <summary>持久化登记表。调用点均持 _gate：锁内序列化成字符串（Monitor 重入安全），
    /// 文件 IO 排到后台串行执行——慢盘/同步盘卡顿不再堵死所有登记查询。
    /// GUID 临时名原子替换（旧版固定 .part 在并发保存时会互相截断）。</summary>
    private static void SaveState()
    {
        string json;
        string target = Store.CloudStateFile;   // 入队时捕获路径：执行时沙箱/主库可能已切换
        lock (_gate)
            json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
        lock (_saveChainGate)
            _saveChain = _saveChain.ContinueWith(_ =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    Store.AtomicWrite(target, json);
                }
                catch (Exception ex)
                {
                    // 登记丢失会影响防回传/防复活判定，不能静默：写崩溃日志便于排查
                    try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsm-crash.txt"),
                        $"[{DateTime.Now:HH:mm:ss}] cloud-state 保存失败：{ex.Message}\n"); } catch { }
                }
            }, TaskScheduler.Default);
    }

    /// <summary>等待已排队的登记表写盘全部完成（优雅停机时调用，最多等 timeout）。</summary>
    internal static void WaitStateSaved(int timeoutMs = 3000)
    {
        Task t;
        lock (_saveChainGate) t = _saveChain;
        try { t.Wait(timeoutMs); } catch { }
    }
}
