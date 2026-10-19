using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using GameSaveManager.Models;

namespace GameSaveManager.Services;

public class BackupManifest
{
    public string Game { get; set; } = "";
    /// <summary>所属玩家（槽位名）；"" = 未启用玩家时的通用备份。</summary>
    public string Profile { get; set; } = "";
    public DateTime Time { get; set; }
    public List<string> Paths { get; set; } = new();
    public string Kind { get; set; } = "full";            // full | incr
    public string BaseFile { get; set; } = "";            // incr: 父 zip 文件名
    public List<ZipFileInfo> Files { get; set; } = new(); // 该快照的最终文件全集（zip entry 名 + 大小）
}

public class ZipFileInfo
{
    public string N { get; set; } = "";   // zip entry 名，如 p0/save/a.sav
    public long S { get; set; }           // 文件大小
    public long M { get; set; }           // LastWriteTimeUtc.Ticks（精确变更检测）
}

/// <summary>
/// 快照式备份/还原：整存档目录打包 zip（内含 manifest.json 记录原始路径），
/// 还原时先做安全快照，再解压覆盖回原路径。
/// 增量模式：与最近一次备份对比（路径+大小+修改时间），仅打包有变化的文件；
/// manifest.Files 记录该快照的最终文件全集，还原时沿「全量→增量…→目标」链依次解压覆盖。
/// </summary>
public static class BackupService
{
    /// <summary>UI 集合变更必须回到 UI 线程（列表绑定会崩溃）。</summary>
    private static void OnUi(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) a(); else d.Invoke(a);
    }

    // ── 每游戏操作闸：同一游戏的备份/还原/删除互斥，不同游戏互不阻塞 ──
    // 背景：自动备份已并行化（多游戏同时退出时 2 路并发），可能与手动备份/还原/
    // 删除撞在同一游戏的 zip 目录与清单上。闸保证同游戏串行；Restore 内部的
    // 「还原前安全快照」会再调 Create，因此闸必须可重入（同线程直接放行）。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _gameGates =
        new(StringComparer.Ordinal);

    [ThreadStatic]
    private static Stack<(string Id, SemaphoreSlim Gate)>? _zoneHeld;   // 当前线程持有的闸（Create/Restore 均为同步方法，线程内不会切换）

    /// <summary>进入该游戏的互斥区。返回 IDisposable，using 包住操作体；
    /// 同线程重入（Restore→安全快照 Create）直接放行不重复等待。</summary>
    private static IDisposable EnterGameZone(string gameId)
    {
        var gate = _gameGates.GetOrAdd(gameId, _ => new SemaphoreSlim(1, 1));
        _zoneHeld ??= new Stack<(string, SemaphoreSlim)>();
        if (_zoneHeld.Any(z => z.Id == gameId)) return new ZoneScope(null, null);
        gate.Wait();
        // Wait 成功后 Push 理论上不会抛（OOM 等极端情形），但一旦发生必须释放，
        // 否则该游戏的闸永远处于已占用状态，后续所有备份/还原永久阻塞
        try { _zoneHeld.Push((gameId, gate)); }
        catch { gate.Release(); throw; }
        return new ZoneScope(gate, _zoneHeld);
    }

    private sealed class ZoneScope(SemaphoreSlim? gate, Stack<(string, SemaphoreSlim)>? stack) : IDisposable
    {
        public void Dispose()
        {
            if (gate == null || stack == null) return;
            stack.Pop();
            gate.Release();
        }
    }

    /// <summary>游戏从库中移除后清理对应互斥闸，避免 _gameGates 只增不清。
    /// 仅在闸空闲（CurrentCount==1）时移除；仍有在途备份/还原持有时保留，待其自然过期。</summary>
    internal static void TryRemoveGameGate(string gameId)
    {
        if (_gameGates.TryGetValue(gameId, out var gate) && gate.CurrentCount == 1)
            _gameGates.TryRemove(gameId, out _);
    }

    /// <summary>把游戏名转成安全的文件/目录名（过滤 Windows 非法字符、控制字符，限长）。</summary>
    public static string SafeName(string name)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim().TrimEnd('.');
        if (name.Length > 60) name = name[..60].TrimEnd();
        return string.IsNullOrWhiteSpace(name) ? "game" : name;
    }

    /// <summary>该游戏的备份目录：backups\{游戏名}（未启用玩家时 zip 直接放这里）。</summary>
    public static string BackupDirFor(GameEntry g) => Path.Combine(Store.BackupRoot, SafeName(g.DisplayName));

    /// <summary>某个玩家的备份目录：backups\{游戏名}\{玩家名}（多玩家各自独立）。</summary>
    public static string PlayerBackupDir(GameEntry g, string player) =>
        Path.Combine(BackupDirFor(g), SafeName(player));

    /// <summary>
    /// 游戏改名后迁移备份目录：移动全部 zip、更新备份记录路径、同步 zip 内 manifest 的增量基链文件名。
    /// </summary>
    public static void MigrateBackupDir(GameEntry g, string oldDisplayName)
    {
        using var __zone = EnterGameZone(g.Id);
        string oldDir = Path.Combine(Store.BackupRoot, SafeName(oldDisplayName));
        string newDir = BackupDirFor(g);
        if (string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase)) return;
        if (!Directory.Exists(oldDir)) return;    // 没备份过 → 无需迁移
        Directory.CreateDirectory(newDir);
        string prefix = SafeName(g.DisplayName);
        var (map, errors) = MoveAndMap(oldDir, newDir, prefix, g);
        errors.AddRange(PatchChains(newDir, map));
        if (errors.Count == 0) { try { Directory.Delete(oldDir, recursive: true); } catch { } }
        Store.Save();
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"游戏「{g.DisplayName}」改名后，备份目录有 {errors.Count} 项未能迁移，未迁移的文件与记录保持原样，可稍后重试：\n"
                + string.Join("\n", errors.Take(10)));
    }

    /// <summary>启动时把旧版 {guid} 目录迁移到 {游戏名} 目录（文件改名 + 记录/manifest 同步）。</summary>
    public static void MigrateLegacyDirs()
    {
        bool changed = false;
        foreach (var g in Store.Games.ToList())
        {
            string legacy = Path.Combine(Store.BackupRoot, g.Id);
            if (!Directory.Exists(legacy)) continue;
            try
            {
                string newDir = BackupDirFor(g);
                Directory.CreateDirectory(newDir);
                var (map, errors) = MoveAndMap(legacy, newDir, SafeName(g.DisplayName), g);
                errors.AddRange(PatchChains(newDir, map));
                if (errors.Count == 0) { try { Directory.Delete(legacy, recursive: true); } catch { } }
                else
                {
                    // 启动迁移不能打断：留档日志，旧目录保留，下次启动重试
                    try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsm-crash.txt"),
                        $"[{DateTime.Now:HH:mm:ss}] 旧目录迁移 {g.Id} 部分失败：{string.Join("; ", errors.Take(5))}\n"); } catch { }
                }
                changed = true;
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsm-crash.txt"),
                    $"[{DateTime.Now:HH:mm:ss}] 旧目录迁移 {g.Id} 失败：{ex.Message}\n"); } catch { }
            }
        }
        if (changed) Store.Save();
    }

    /// <summary>把 oldDir 的 zip 移入 newDir（文件名加游戏名前缀），返回 旧文件名→新文件名 映射、
    /// 未移动成功的错误列表；只更新已确认移动到位的 g.Backups 记录。
    /// 移动失败的文件与记录保持原样（原路径仍有效），调用方不得删除旧目录、可下次重试。</summary>
    private static (Dictionary<string, string> Map, List<string> Errors) MoveAndMap(
        string oldDir, string newDir, string prefix, GameEntry g)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var f in Directory.GetFiles(oldDir))
        {
            string oldName = Path.GetFileName(f);
            // 提取原文件名尾部的时间戳，避免反复改名时前缀累积（A_B_时间戳.zip 越改越长）
            var mt = System.Text.RegularExpressions.Regex.Match(oldName, @"\d{4}-\d{2}-\d{2}_\d{6}(_\d+)?\.zip$");
            string tail = mt.Success ? mt.Value : oldName;
            string newName = prefix + "_" + tail;
            string dest = Path.Combine(newDir, newName);
            string stem = tail.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? tail[..^4] : tail;
            for (int n = 2; File.Exists(dest); n++)
                dest = Path.Combine(newDir, prefix + "_" + stem + $"_{n}.zip");
            try { File.Move(f, dest); }
            catch (Exception ex) { errors.Add($"{oldName}（{ex.Message}）"); continue; }
            string finalName = Path.GetFileName(dest);
            map[oldName] = finalName;
            foreach (var b in g.Backups)
            {
                if (string.Equals(Path.GetFileName(b.File), oldName, StringComparison.OrdinalIgnoreCase)
                    && b.File.StartsWith(oldDir, StringComparison.OrdinalIgnoreCase))
                {
                    b.File = dest;
                    if (b.BaseFile.Length > 0 && map.TryGetValue(b.BaseFile, out var nb))
                        b.BaseFile = nb;
                }
            }
        }

        // 玩家子目录：整体移动（玩家 zip 不带游戏名前缀，文件名不变，增量基链不受影响）
        foreach (var sub in Directory.GetDirectories(oldDir))
        {
            string subName = Path.GetFileName(sub);
            string destSub = Path.Combine(newDir, subName);
            try
            {
                if (Directory.Exists(destSub))
                    foreach (var f in Directory.GetFiles(sub))
                    {
                        string df = Path.Combine(destSub, Path.GetFileName(f));
                        if (!File.Exists(df)) File.Move(f, df);
                    }
                else
                    Directory.Move(sub, destSub);
            }
            catch (Exception ex) { errors.Add($"{subName} 子目录（{ex.Message}）"); }
        }
        // 更新玩家备份记录的目录前缀（File 在子目录内、文件名不变）：只改确认到位的
        foreach (var b in g.Backups)
        {
            if (string.IsNullOrEmpty(b.File) || !b.File.StartsWith(oldDir, StringComparison.OrdinalIgnoreCase))
                continue;
            string newPath = newDir + b.File.Substring(oldDir.Length);
            if (File.Exists(newPath)) b.File = newPath;
            else errors.Add($"{Path.GetFileName(b.File)}（记录对应文件未到位，保持原路径）");
        }
        return (map, errors);
    }

    /// <summary>按映射重写目录内每个 zip 的 manifest.BaseFile（增量链引用旧文件名 → 新文件名）。
    /// 采用"副本上改→原子替换"：磁盘满/杀软截断时原 zip 不会被写坏；返回失败包名列表。</summary>
    private static List<string> PatchChains(string dir, Dictionary<string, string> map)
    {
        var failed = new List<string>();
        if (map.Count == 0) return failed;
        foreach (var zipName in map.Values)
        {
            string zipPath = Path.Combine(dir, zipName);
            string tmp = zipPath + ".patch." + Guid.NewGuid().ToString("N");
            bool dirty = false;
            try
            {
                File.Copy(zipPath, tmp, overwrite: true);
                using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Update))
                {
                    var e = zip.GetEntry("manifest.json");
                    if (e != null)
                    {
                        BackupManifest? m;
                        using (var rd = new StreamReader(e.Open()))
                            m = JsonSerializer.Deserialize<BackupManifest>(rd.ReadToEnd());
                        if (m != null && m.BaseFile.Length > 0 && map.TryGetValue(m.BaseFile, out var nb))
                        {
                            m.BaseFile = nb;
                            e.Delete();
                            var ne = zip.CreateEntry("manifest.json");
                            using (var w = new StreamWriter(ne.Open()))
                                w.Write(JsonSerializer.Serialize(m, JsonOpts));
                            dirty = true;
                        }
                    }
                }   // 关闭后再替换，确保流已写完
                if (dirty) File.Move(tmp, zipPath, overwrite: true);
            }
            catch (Exception ex)
            {
                failed.Add($"{zipName}（{ex.GetType().Name}）");
                dirty = false;   // 替换也可能失败：临时副本必须清掉
            }
            finally
            {
                if (!dirty) { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
            }
        }
        return failed;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static BackupRecord Create(GameEntry g, string note, bool hot = false) => Create(g, note, hot, out _);

    /// <summary>备份创建并入库后触发（云同步据此自动上传；后台线程触发，订阅方注意切 UI 线程）。</summary>
    public static event Action<GameEntry, BackupRecord>? BackupCreated;

    /// <summary>创建备份。hot=true：游戏运行中热快照——跳过运行检测、强制独立全量、
    /// 被游戏占用的文件剔除出包和清单（skippedFiles 返回剔除数）。</summary>
    public static BackupRecord Create(GameEntry g, string note, bool hot, out int skippedFiles)
        => Create(g, note, hot, out skippedFiles, profile: null);

    /// <summary>
    /// 创建备份（完整参数版）。
    /// profile：备份归属玩家；null = 当前激活玩家（"" = 未启用玩家，通用备份）。
    /// scanDirs/manifestPaths：镜像迁移专用——从镜像 pN 目录打包，但 manifest.Paths 记录当年真实存档路径
    /// （还原时条目按 pN 落回真实路径），两个列表必须一一对应；null = 常规模式（扫描对象=备份目标路径）。
    /// </summary>
    public static BackupRecord Create(GameEntry g, string note, bool hot, out int skippedFiles,
        string? profile, List<string>? scanDirs = null, List<string>? manifestPaths = null)
    {
        using var __zone = EnterGameZone(g.Id);
        skippedFiles = 0;
        var paths = manifestPaths != null
            ? manifestPaths.Select(p => p).Distinct().ToList()
            : g.SavePaths
                .Where(p => !p.IsReg && System.IO.Directory.Exists(p.Path))
                .Select(p => p.Path).Distinct().ToList();
        if (paths.Count == 0)
            throw new InvalidOperationException("没有可备份的存档目录（路径都不存在）");

        // 打包来源目录：默认=备份目标路径本身；迁移模式=镜像 pN 目录（与 paths 按下标一一对应）
        var roots = scanDirs;
        if (roots != null && roots.Count != paths.Count)
            throw new InvalidOperationException("镜像目录与路径表数量不一致，无法打包");
        roots ??= paths;

        if (!hot) CheckGameRunning(g, "备份");

        // 归属玩家：显式 profile > ActiveProfile > 虚拟默认玩家1。
        // 新游戏 Players 空、ActiveProfile 空：第一份真实备份归属「玩家1」并在打包成功后实体化卡槽；
        // 镜像迁移模式（scanDirs）不参与虚拟——其 profile 由调用方显式指定。
        string profileKey;
        bool materializeDefault = false;
        if (profile != null) profileKey = profile;
        else if (!string.IsNullOrEmpty(g.ActiveProfile)) profileKey = g.ActiveProfile;
        else if (scanDirs == null && ProfileService.IsDefaultPlayerApplicable(g))
        {
            profileKey = ProfileService.DefaultFirstPlayerName;
            materializeDefault = true;
        }
        else profileKey = "";

        // ── 决定全量 or 增量（增量基准只在同一玩家的备份链内选，绝不跨玩家）──
        // 热快照强制独立全量：运行中文件随时可能再被写入，作为增量基点会造成链空洞
        var st = Store.Settings;
        BackupRecord? baseRec = null;
        var baseFiles = new Dictionary<string, ZipFileInfo>(StringComparer.OrdinalIgnoreCase);
        var myBackups = g.Backups.Where(b => (b.Profile ?? "") == profileKey).ToList();
        if (st.IncrementalEnabled && !hot && myBackups.Count > 0 &&
            CountIncrSinceFull(myBackups) < Math.Max(1, st.FullEveryN))
        {
            baseRec = myBackups[0];   // 列表新→旧插入
            var bm = TryReadManifest(baseRec.File);
            if (bm != null)
            {
                foreach (var f in bm.Files) baseFiles[f.N] = f;
            }
            else baseRec = null;      // 基础包丢失/损坏 → 退回全量
        }
        bool incremental = baseRec != null;

        // 启用玩家后，该玩家的 zip 放进 backups\{游戏}\{玩家}\ 子目录，各玩家互不干扰
        string gameDir = profileKey.Length > 0 ? PlayerBackupDir(g, profileKey) : BackupDirFor(g);
        Directory.CreateDirectory(gameDir);
        // 清理历史崩溃残留的 .part（只删 1 小时前的，避开可能正在进行的打包）
        try
        {
            foreach (var p in Directory.GetFiles(gameDir, "*.part"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(p) > TimeSpan.FromHours(1)) File.Delete(p);
        }
        catch { }
        var time = DateTime.Now;
        string stem = SafeName(g.DisplayName) + "_" + time.ToString("yyyy-MM-dd_HHmmss");
        string file = Path.Combine(gameDir, stem + ".zip");
        for (int n = 2; File.Exists(file); n++)   // 同秒多次备份时避免文件名冲突
            file = Path.Combine(gameDir, stem + $"_{n}.zip");

        // ── 扫描存档文件，对比基础快照找变化（大小 + 精确修改时间）──
        var changed = new List<(string Entry, string Src)>();
        var finalFiles = new List<ZipFileInfo>();
        for (int i = 0; i < roots.Count; i++)
        {
            string root = roots[i];
            foreach (var f in SafeFiles(root))
            {
                try
                {
                    if (hot)
                    {
                        // 运行中热快照：先确认文件当前可读（游戏可能独占锁定），
                        // 锁定的直接剔除，保证清单与实际打包集合一致、增量链无空洞
                        using var probe = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    }
                    var fi = new FileInfo(f);
                    string entry = $"p{i}/{Path.GetRelativePath(root, f).Replace('\\', '/')}";
                    var info = new ZipFileInfo
                    {
                        N = entry,
                        S = fi.Length,
                        M = fi.LastWriteTimeUtc.Ticks,
                    };
                    finalFiles.Add(info);
                    // 旧包无精确 mtime（M=0）→ 一律视为变化，宁多勿漏
                    bool unchanged = baseFiles.TryGetValue(entry, out var bi)
                        && bi.S == info.S && bi.M == info.M;
                    if (!incremental || !unchanged)
                        changed.Add((entry, f));
                }
                catch { skippedFiles++; /* 单个文件读不到则跳过（任何模式都计数上报，且本就不在清单内） */ }
            }
        }
        if (incremental && changed.Count == 0)
            throw new InvalidOperationException("存档自上次备份以来没有变化，无需备份");
        // 新游戏存档目录里还没有任何文件（游戏尚未真正产生存档）：不打空 zip、不实体化卡槽——
        // 形式上的玩家1保持虚拟，直到真实存档出现
        if (!incremental && myBackups.Count == 0 && finalFiles.Count == 0)
            throw new InvalidOperationException("游戏尚未产生存档文件，无需备份");
        // 全量备份同样跳过"零变化"：与该玩家最新备份的文件清单完全一致（无新增/修改/删除）时不打包，
        // 避免切换玩家/退出游戏等场景在全量周期点产生冗余 zip
        if (!incremental && myBackups.Count > 0)
        {
            var latestManifest = TryReadManifest(myBackups[0].File);
            if (latestManifest != null && SameSnapshot(latestManifest.Files, finalFiles))
                throw new InvalidOperationException("存档自上次备份以来没有变化，无需备份");
        }

        var manifest = new BackupManifest
        {
            Game = g.DisplayName,
            Profile = profileKey,
            Time = time,
            Paths = paths,
            Kind = incremental ? "incr" : "full",
            BaseFile = incremental ? Path.GetFileName(baseRec!.File) : "",
            Files = finalFiles,
        };
        // 打包瞬间失败的文件（游戏运行中锁定，或杀软/同步盘独占——非热模式也可能发生）
        // 要从清单剔除，保证 manifest.Files = zip 实际条目（否则还原会得到新旧混合档）。
        // manifest 在内容循环之后写入；先写 .part 临时文件、关闭后再 Move 到最终名：
        // 同步盘/WebDAV/本程序在最终路径看到的永远是完整 zip，不会抓到写一半的包。
        var failedPack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string partFile = file + ".part";
        long originalSize, packedSize;
        try
        {
            using (var fs = File.Create(partFile))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                // 存档内容：p{i}/相对路径（增量只含变化文件）
                foreach (var (entry, src) in changed)
                {
                    ZipArchiveEntry? ze = null;
                    try { ze = zip.CreateEntryFromFile(src, entry, CompressionLevel.Optimal); }
                    catch
                    {
                        ze?.Delete();                        // 移除半截 entry
                        failedPack.Add(entry);
                        skippedFiles++;
                    }
                }
                if (failedPack.Count > 0)
                    finalFiles.RemoveAll(f => failedPack.Contains(f.N));

                // manifest
                var mEntry = zip.CreateEntry("manifest.json");
                using (var w = new StreamWriter(mEntry.Open()))
                    w.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            }

            // ── 统计：快照逻辑总大小 + 实际打包的原始字节（重开 zip 读未压缩长度，准确）──
            originalSize = finalFiles.Sum(f => f.S);
            packedSize = originalSize;
            try
            {
                using var fs2 = File.OpenRead(partFile);
                using var zip2 = new ZipArchive(fs2, ZipArchiveMode.Read);
                packedSize = zip2.Entries
                    .Where(e => e.FullName != "manifest.json")
                    .Sum(e => e.Length);
            }
            catch { }

            File.Move(partFile, file);   // 同目录 Move 是元数据级原子操作
        }
        catch
        {
            try { if (File.Exists(partFile)) File.Delete(partFile); } catch { }
            throw;
        }

        var record = new BackupRecord
        {
            Time = time,
            Profile = profileKey,
            Note = note,
            File = file,
            Size = new FileInfo(file).Length,
            Kind = manifest.Kind,
            BaseFile = manifest.BaseFile,
            OriginalSize = originalSize,
            PackedSize = packedSize,
            Hot = hot,
            // 存档时间（游戏真实保存时刻）与空锚点标记：manifest.Files 此时已定稿（hot 剔除已完成）
            SavedAt = SavedAtFromManifest(manifest),
            EmptyAnchor = finalFiles.Count == 0,
        };
        // 首次真实备份：把虚拟玩家1实体化（名单 + 激活态），让 Prune/后续备份按真实卡槽工作
        if (materializeDefault)
        {
            ProfileService.RegisterSlot(g, ProfileService.DefaultFirstPlayerName);
            g.ActiveProfile = ProfileService.DefaultFirstPlayerName;
        }
        OnUi(() =>
        {
            g.Backups.Insert(0, record);
            Prune(g);
            g.Refresh();
            g.RefreshVisibleBackups();
            g.RefreshProfiles();   // 虚拟→实体化后筹码激活态/名单刷新
        });
        // 有归属玩家的备份：落激活标记（幂等；此时玩家有真实 zip，建目录写 game.json 合理）
        if (profileKey.Length > 0) ProfileService.WriteGameMarker(g);
        // 备份内容 == 当前 live：基线刷新为备份后状态，后续零备份玩家比较才不会用过时基线
        SetLiveBaseline(g);
        Store.Save();
        try { BackupCreated?.Invoke(g, record); } catch { /* 云同步异常不得影响备份主流程 */ }
        return record;
    }

    /// <summary>两份快照文件清单是否完全一致（条目数 + 每项大小/修改时间；旧清单 M=0 视为不一致，宁多勿漏）。</summary>
    private static bool SameSnapshot(List<ZipFileInfo> oldFiles, List<ZipFileInfo> newFiles)
    {
        if (oldFiles.Count != newFiles.Count || oldFiles.Count == 0) return false;
        var oldMap = oldFiles.ToDictionary(f => f.N, StringComparer.OrdinalIgnoreCase);
        foreach (var c in newFiles)
        {
            if (!oldMap.TryGetValue(c.N, out var o)) return false;      // 新增文件
            if (o.S != c.S || o.M != c.M || o.M == 0) return false;    // 内容/时间变化（旧包无 mtime 也算变化）
        }
        return true;
    }

    /// <summary>当前 live 存档是否已被该玩家最新一份备份完整覆盖（文件清单/大小/修改时间完全一致、无新增删除）。
    /// 切换/新建玩家前据此判断：已覆盖则跳过安全备份，直接切换，避免来回切换产生冗余 zip。</summary>
    public static bool IsSaveUpToDate(GameEntry g)
    {
        string profile = g.ActiveProfile ?? "";
        var latest = g.Backups.FirstOrDefault(b => (b.Profile ?? "") == profile);
        if (latest == null) return false;
        var manifest = TryReadManifest(latest.File);
        if (manifest == null) return false;   // 最新包损坏/丢失 → 不能假设已备份

        var paths = g.SavePaths
            .Where(p => !p.IsReg && Directory.Exists(p.Path))
            .Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var current = new List<ZipFileInfo>();
        bool scanError = false;
        for (int i = 0; i < paths.Count; i++)
        {
            foreach (var f in SafeFiles(paths[i]))
            {
                try
                {
                    var fi = new FileInfo(f);
                    current.Add(new ZipFileInfo
                    {
                        N = $"p{i}/{Path.GetRelativePath(paths[i], f).Replace('\\', '/')}",
                        S = fi.Length,
                        M = fi.LastWriteTimeUtc.Ticks,
                    });
                }
                catch { scanError = true; }   // 单个文件读不到 → 按"不确定"处理，宁备份勿漏
            }
        }
        return !scanError && SameSnapshot(manifest.Files, current);
    }

    /// <summary>该备份的增量链在本机是否完整（沿 BaseFile 能走到全量基础包，且 zip 文件都在）。
    /// 云端只同步下来部分增量包、基础包尚未下载完时返回 false——切换玩家应跳过它选更早的完整备份。</summary>
    public static bool IsRestorable(GameEntry g, BackupRecord r)
    {
        try
        {
            var cur = r;
            var guard = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                if (!File.Exists(cur.File)) return false;
                var m = TryReadManifest(cur.File);
                if (m == null) return false;
                if (m.Kind != "incr") return true;
                if (!guard.Add(Path.GetFileName(cur.File))) return false;
                var parent = g.Backups.FirstOrDefault(b =>
                    (b.Profile ?? "") == (cur.Profile ?? "") &&
                    Path.GetFileName(b.File).Equals(m.BaseFile, StringComparison.OrdinalIgnoreCase));
                if (parent == null) return false;
                cur = parent;
            }
        }
        catch { return false; }
    }

    /// <summary>建立 manifest 内 pN 下标 → 实际落盘根目录 的映射（还原/玩家切换专用）。
    /// targetPaths==null：每个下标落 manifest.Paths[i] 原路径。
    /// 否则：①与本机目录精确一致的路径直接对位（本机 zip 的常态）；
    /// ②云端/旧配置 zip 的外来路径先按"末级目录名相同"优先配对，残余的按顺序配对
    /// （容错跨机目录名不同）；但源/目标互为父子目录（层级错位，如 ...\Saved 对
    /// ...\Saved\SaveGames）一律报错中止——倒灌会形成 SaveGames\SaveGames 式自嵌套；
    /// ③配置/截图等不参与玩家切换的目录在字典中无映射，其 zip 条目跳过不落盘。
    private static Dictionary<int, string> BuildRestorePathMap(BackupManifest m, IReadOnlyList<string>? targetPaths)
    {
        var map = new Dictionary<int, string>();
        if (m.Paths == null || m.Paths.Count == 0) return map;
        if (targetPaths == null)
        {
            for (int i = 0; i < m.Paths.Count; i++) map[i] = m.Paths[i];
            return map;
        }
        static string Norm(string p)
        {
            try { return Path.GetFullPath(p.Trim()).TrimEnd('\\', '/'); }
            catch { return p.Trim().TrimEnd('\\', '/'); }
        }
        var targets = targetPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Norm)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // 源机"参与切换的目录集"口径必须与 EffectivePaths 一致：有名字像存档的就只认这些，
        // 一个都没有时兜底全部目录（manifest.Paths 本就只含非注册表目录）
        var saveLikeIdx = new List<int>();
        for (int i = 0; i < m.Paths.Count; i++)
            if (ProfileService.IsLikelySaveDir(m.Paths[i])) saveLikeIdx.Add(i);
        var effectiveSrc = new HashSet<int>(saveLikeIdx.Count > 0 ? saveLikeIdx
            : Enumerable.Range(0, m.Paths.Count));
        var usedTargets = new HashSet<int>();
        var foreignIdx = new List<int>();
        for (int i = 0; i < m.Paths.Count; i++)
        {
            string mp = Norm(m.Paths[i]);
            int exact = -1;
            for (int t = 0; t < targets.Count; t++)
                if (!usedTargets.Contains(t)
                    && string.Equals(targets[t], mp, StringComparison.OrdinalIgnoreCase)) { exact = t; break; }
            if (exact >= 0) { map[i] = targets[exact]; usedTargets.Add(exact); }
            else if (effectiveSrc.Contains(i)) foreignIdx.Add(i);
            // 其余（配置/截图等）不参与切换：无映射，条目跳过
        }
        var freeTargets = targets.Where((_, idx) => !usedTargets.Contains(idx)).ToList();

        // 配对策略：①末级目录名相同的优先精确对位（多目录时避免按下标错配）；
        // ②残余目录再按顺序配对，容错跨机目录名不同的合法场景（源机 pc_saves → 本机 pc_saves2）；
        // ③但只要某条配对的源/目标互为父子目录（层级错位，典型 ...\Saved ↔ ...\Saved\SaveGames），
        // 一律中止——把高一层整棵树（Config/Logs/SaveGames…）倒进低一层，落盘即成自嵌套。
        var remaining = new List<string>(freeTargets);
        var unmatched = new List<int>();
        foreach (int i in foreignIdx)
        {
            string src = Norm(m.Paths[i]);
            string leaf = Path.GetFileName(src.TrimEnd('\\', '/'));
            int hit = remaining.FindIndex(t =>
                string.Equals(Path.GetFileName(t.TrimEnd('\\', '/')), leaf, StringComparison.OrdinalIgnoreCase));
            if (hit >= 0) { map[i] = remaining[hit]; remaining.RemoveAt(hit); }
            else unmatched.Add(i);
        }
        // 层级安全闸：残余源目录与任何候选目标互为父子，都没有安全的落盘位置
        foreach (int i in unmatched)
        {
            var usrc = Norm(m.Paths[i]);
            string? levelTgt = remaining.FirstOrDefault(t =>
                SaveLocator.IsWithin(t, usrc) || SaveLocator.IsWithin(usrc, t));
            if (levelTgt != null)
                throw new InvalidOperationException(
                    "备份的存档目录与本机当前存档位置不在同一层级。为避免还原时产生" +
                    "存档目录同名自嵌套（如 SaveGames\\SaveGames），已中止还原。\n" +
                    $"备份记录的目录：{usrc}\n本机当前的目录：{levelTgt}\n" +
                    "请在「编辑游戏」的存档位置中改回与备份相同的层级后重试（高一层、低一层都不行，必须是同一目录层级）。");
        }
        if (unmatched.Count != remaining.Count)
            throw new InvalidOperationException(
                "备份记录的存档目录与本机当前存档位置无法一一对应。为避免把存档还原到错误目录，已中止切换。\n" +
                $"备份中有 {foreignIdx.Count} 个存档目录：{string.Join("、", foreignIdx.Select(i => m.Paths[i]))}\n" +
                $"本机当前识别到 {freeTargets.Count} 个：{string.Join("、", freeTargets)}\n" +
                "请在「编辑游戏」中核对存档位置后重试。");
        for (int k = 0; k < unmatched.Count; k++) map[unmatched[k]] = remaining[k];
        return map;
    }

    /// <summary>新增存档位置后：检查它与该游戏历史备份记录的目录是否互为父子（层级错位）。
    /// 有冲突返回可展示的警告文案（只提示不阻止），无冲突返回 null。
    /// 层级不一致的旧备份在还原时会被 BuildRestorePathMap/自嵌套预检拦截。</summary>
    public static string? CheckPathLevelConflict(GameEntry g, string newPathRaw)
    {
        try
        {
            string np = NormPath(newPathRaw);
            if (string.IsNullOrWhiteSpace(np)) return null;
            foreach (var b in g.Backups)
            {
                var m = TryReadManifest(b.File);
                if (m?.Paths == null) continue;
                foreach (var p in m.Paths)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    string op = NormPath(p);
                    if (string.Equals(op, np, StringComparison.OrdinalIgnoreCase)) continue;
                    bool oldHigher = SaveLocator.IsWithin(np, op);    // 新位置在旧位置内部 = 新位置更深
                    bool newHigher = SaveLocator.IsWithin(op, np);    // 旧位置在新位置内部 = 新位置更浅
                    if (!oldHigher && !newHigher) continue;
                    return "检测到新存档位置与该游戏已有备份的目录不在同一层级：\n\n" +
                           $"已有备份记录：{op}\n本次添加位置：{np}\n（新位置比备份目录{(oldHigher ? "深" : "浅")}了一层或多层）\n\n" +
                           "层级不一致时，旧备份无法直接还原（程序会拦截，避免产生 SaveGames\\SaveGames 式嵌套目录）。\n" +
                           "• 以后的新备份按新层级正常工作；\n" +
                           "• 若要还原旧备份，请临时把存档位置改回原来的层级，还原后再改回。";
                }
            }
        }
        catch { }
        return null;
    }

    /// <param name="purgeNewFiles">严格还原：删除快照中不存在的新文件（只清理快照目录范围内）。</param>
    /// <param name="targetPaths">还原落盘路径重映射（可空，玩家切换时传 EffectivePaths）。映射不是按下标硬对位，
    /// 而是：与 manifest.Paths 精确一致的本机路径直接对位；云端/旧配置的外来路径只与末级目录名
    /// 相同的本机目录配对，末级名不同（层级错位）或数量对不上都会明确报错中止；
    /// 配置/截图等不参与切换的目录不落盘。
    /// null = 按 manifest.Paths 原路径还原（直接点备份行还原的常态）。
    /// 无论哪种映射，条目会落成根目录同名自嵌套时一律在写盘前中止。</param>
    /// <param name="safetySnapshot">还原前是否给当前 live 打「还原前安全快照」（默认 true）。
    /// 玩家切换（SwitchTo）传 false：切走方的 live 已由 AnchorOnLeave 精确锚定——
    /// 命中已有备份时再打快照必然是内容重复的冗余 zip（卡赞 01:25 事故包的真正来源），
    /// 未命中时锚点包本身就是刚打好的「切换玩家」新包，同样不需要重复兜底。</param>
    public static void Restore(GameEntry g, BackupRecord r, bool purgeNewFiles = false,
        IReadOnlyList<string>? targetPaths = null, bool safetySnapshot = true, bool markFavorite = true)
    {
        using var __zone = EnterGameZone(g.Id);
        if (!File.Exists(r.File))
            throw new InvalidOperationException("备份文件已丢失");
        CheckGameRunning(g, "还原");

        // 安全快照：还原前先备份当前存档（当前无存档则跳过）。
        // 只忽略"没有变化/没有可备份目录"；磁盘满、IO 错误等真实失败必须中止还原——
        // 否则唯一的后悔药没建成，随后的覆盖与严格清理将无法回退。
        // 零备份的激活玩家若根本没玩过（live 与切入基线一致）：不打快照——
        // 否则会把上一任的进度错误归到她名下。
        // 玩家切换时整段跳过：切走方已在 SwitchTo/AnchorOnLeave 锚定，安全快照只会打出冗余包。
        bool activePlayerZero = !string.IsNullOrEmpty(g.ActiveProfile)
            && !g.Backups.Any(b => b.Profile == g.ActiveProfile);
        bool skipUntouched = activePlayerZero && LiveMatchesBaseline(g);
        try { if (safetySnapshot && HasAnySave(g) && !skipUntouched) Create(g, "还原前安全快照"); }
        catch (InvalidOperationException ex)
            when (ex.Message.Contains("没有变化") || ex.Message.Contains("没有可备份")) { }

        // ── 解析增量链：目标 → … → 全量，从旧到新依次应用 ──
        var chain = new List<(BackupManifest Manifest, string File)>();
        var cur = r;
        var guard = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            if (!guard.Add(Path.GetFileName(cur.File)))
                throw new InvalidOperationException("增量链出现循环引用");
            var m = ReadManifestOrThrow(cur.File);
            chain.Add((m, cur.File));
            if (m.Kind != "incr") break;
            var parent = g.Backups.FirstOrDefault(b =>
                (b.Profile ?? "") == (cur.Profile ?? "") &&
                Path.GetFileName(b.File).Equals(m.BaseFile, StringComparison.OrdinalIgnoreCase));
            if (parent == null)
                throw new InvalidOperationException($"增量链断裂：缺少基础备份 {m.BaseFile}");
            cur = parent;
        }
        chain.Reverse();

        // 落盘路径映射：在写任何文件之前为整条链逐包构建——映射对不上立即中止，live 保持原样
        var pathMaps = chain.Select(c => BuildRestorePathMap(c.Manifest, targetPaths)).ToList();

        // 目标快照的最终文件全集（旧包无 Files 则退化为解压链上全部条目）
        var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in chain[^1].Manifest.Files) want.Add(f.N);

        // 统一自嵌套预检（对所有映射生效，含 targetPaths==null 的原路径精确还原）：
        // 条目相对路径首段若与目标根末级目录同名，落盘必然形成「SaveGames\SaveGames」式目录——
        // 这是备份时存档位置层级配错产生的污染包，必须在写任何文件前中止，防止嵌套随还原/再备份扩散。
        // 只检最终快照仍保留的条目（want）：增量链中间包里的历史污染条目若已从快照消失则不再拦截。
        for (int ci = 0; ci < chain.Count; ci++)
        {
            var pm = pathMaps[ci];
            foreach (var f in chain[ci].Manifest.Files)
            {
                if (want.Count > 0 && !want.Contains(f.N)) continue;
                string n = f.N;
                int slash = n.IndexOf('/');
                if (slash < 2 || slash > 5) continue;
                if (!n.StartsWith("p", StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(n.Substring(1, slash - 1), out int pi)) continue;
                if (!pm.TryGetValue(pi, out var rootRaw)) continue;
                string rel = slash + 1 < n.Length ? n.Substring(slash + 1) : "";
                int seg = rel.IndexOf('/');
                string first = seg < 0 ? rel : rel.Substring(0, seg);
                string leaf = Path.GetFileName(rootRaw.TrimEnd('\\', '/'));
                if (first.Length > 0 && leaf.Length > 0
                    && first.Equals(leaf, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"备份「{Path.GetFileName(chain[ci].File)}」中含有与存档目录同名的子目录" +
                        $"（落盘会形成 {leaf}\\{leaf}\\… 的自嵌套结构）。这是存档位置层级配错时产生的备份，\n" +
                        "为避免嵌套目录扩散，已中止还原。请改选层级正确的备份，" +
                        "或在「编辑游戏」中把存档位置改回备份时的同一层级。");
            }
        }

        // 解压失败/越界条目：有任何一条失败都中止，绝不在混合档上继续严格清理
        var failed = new List<string>();
        int lastZipEntries = 0;   // 最终快照包内的实际存档条目数（判定"明确的空快照"用）

        // TOCTOU 复查：从入口检查到真正落盘之间隔着安全快照与链解析，
        // 用户可能在此期间启动了游戏；落盘前再查一次，避免覆盖运行中游戏刚写的存档
        CheckGameRunning(g, "还原");

        for (int ci = 0; ci < chain.Count; ci++)
        {
            var (m, zipFile) = chain[ci];
            var pathMap = pathMaps[ci];
            // manifest 里是精确到毫秒的原 mtime；zip 条目只有 2 秒精度，解包后须回写，
            // 否则还原后的 live 时间戳漂移会让下次备份把同一批文件误判为「有变化」
            var precise = m.Files.ToDictionary(f => f.N, f => f.M, StringComparer.OrdinalIgnoreCase);
            using var fs = File.OpenRead(zipFile);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            lastZipEntries = zip.Entries.Count(e => e.FullName != "manifest.json");
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName == "manifest.json") continue;
                if (want.Count > 0 && !want.Contains(entry.FullName)) continue;
                int slash = entry.FullName.IndexOf('/');
                if (slash < 2 || slash > 5) continue;   // 只处理 pN/ 前缀
                if (!entry.FullName.StartsWith("p", StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(entry.FullName.Substring(1, slash - 1), out var pi)) continue;
                if (pi < 0 || pi >= m.Paths.Count) continue;
                if (!pathMap.TryGetValue(pi, out var rootRaw)) continue;   // 不参与切换的目录（配置等）跳过
                string rel = entry.FullName.Substring(slash + 1);
                if (rel.Length == 0) continue;
                string root, target;
                try
                {
                    root = Path.GetFullPath(rootRaw);
                    target = Path.GetFullPath(Path.Combine(root, rel.Replace('/', '\\')));
                }
                catch (Exception ex) { failed.Add($"{entry.FullName}（路径非法：{ex.Message}）"); continue; }
                // Zip Slip 防护：损坏/被篡改的包条目不得写出快照目录之外
                if (!SaveLocator.IsWithin(target, root))
                {
                    failed.Add($"{entry.FullName}（越界路径，已跳过）");
                    continue;
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                    if (precise.TryGetValue(entry.FullName, out var ticks) && ticks > 0)
                        File.SetLastWriteTimeUtc(target, new DateTime(ticks));
                }
                catch (Exception ex) { failed.Add($"{entry.FullName}（{ex.GetType().Name}）"); }
            }
        }

        // 有文件没还原则存档已是新旧混合：明确报错，且绝不执行严格清理
        if (failed.Count > 0)
            throw new InvalidOperationException(
                $"有 {failed.Count} 个存档文件还原失败（游戏可能仍在运行、文件被占用或被杀毒软件拦截）。" +
                "未执行额外清理，请关闭相关程序后重试。还原前的存档已自动保留为「还原前安全快照」备份，可随时找回。失败条目：\n" +
                string.Join("\n", failed.Take(10)));

        // 严格还原：删除快照范围目录中不在目标文件全集内的文件（旧包无 Files 不做清理）。
        // 例外：清单为空且包内也无任何存档条目 = 明确的空快照（如全新玩家的锚点包），
        // 此时切换/还原过去 live 就该被清空，purge 照常执行。
        bool emptySnapshot = chain[^1].Manifest.Files.Count == 0 && lastZipEntries == 0;
        if (purgeNewFiles && (chain[^1].Manifest.Files.Count > 0 || emptySnapshot))
        {
            // 只允许清理"当前配置仍在使用"的存档目录：备份后用户改了位置、或目录已挪作他用时不得误删
            var currentRoots = new HashSet<string>(g.SavePaths
                .Where(p => !p.IsReg)
                .Select(p => { try { return Path.GetFullPath(p.Path); } catch { return null; } })
                .Where(p => p != null)!, StringComparer.OrdinalIgnoreCase);
            var purgeFailed = new List<string>();
            // 每个落盘目录用它在最新快照中实际对应的 pN 前缀（映射来自最新包，避免下标错位误删）
            foreach (var (pi, rootRaw) in pathMaps[^1])
            {
                string root;
                try { root = Path.GetFullPath(rootRaw); } catch { continue; }
                if (!currentRoots.Contains(root)) continue;
                purgeFailed.AddRange(PurgeExtras(root, $"p{pi}/", want));
            }
            if (purgeFailed.Count > 0)
                throw new InvalidOperationException(
                    $"存档已还原，但有 {purgeFailed.Count} 个快照之外的文件被占用无法清理（严格还原未完全生效）：\n" +
                    string.Join("\n", purgeFailed.Take(10)) +
                    "\n请关闭占用这些文件的程序后重新还原一次；还原前的存档已自动保留为「还原前安全快照」备份，可随时找回。");
        }

        // 还原完成：记录 live 基线指纹（SwitchTo 离开零备份玩家时据此判断她有没有玩过）
        SetLiveBaseline(g);
        // 手动还原成功 = 最明确的"认可动作"：心目中的指针指向还原目标（原因 restore）。
        // 玩家切换也走本方法，但显式传 markFavorite:false——切换不改变目标玩家自己的指针。
        if (markFavorite)
            ProfileService.MarkFavorite(g, r.Profile ?? "", r.FileJson, "restore");
    }

    // ── live 基线指纹（运行时，不持久化）──────────────────────
    /// <summary>计算当前 live 文件指纹：p{i}/相对路径 + 大小 + 精确 mtime，按路径排序。</summary>
    private static List<(string Rel, long Size, long Mtime)> LiveSignature(GameEntry g)
    {
        var sig = new List<(string Rel, long Size, long Mtime)>();
        var paths = g.SavePaths.Where(p => !p.IsReg).Select(p => p.Path).ToList();
        for (int i = 0; i < paths.Count; i++)
        {
            string root = paths[i];
            if (!Directory.Exists(root)) continue;
            foreach (var f in SafeFiles(root))
            {
                try
                {
                    var fi = new FileInfo(f);
                    sig.Add(($"p{i}/" + Path.GetRelativePath(root, f).Replace('\\', '/'),
                        fi.Length, fi.LastWriteTimeUtc.Ticks));
                }
                catch { }
            }
        }
        sig.Sort((a, b) => string.CompareOrdinal(a.Rel, b.Rel));
        return sig;
    }

    /// <summary>把当前 live 指纹存为基线（Restore 成功后调用）。</summary>
    public static void SetLiveBaseline(GameEntry g) => g.LiveBaseline = LiveSignature(g);

    /// <summary>只读判断：当前 live 存档是否与目标备份快照的文件全集完全一致
    /// （文件集/大小/精确 mtime，与备份去重同一口径）。供手动还原入口短路"重复还原同一备份"。
    /// 任何不确定（清单损坏/过旧、存档路径配置变化、读取异常）一律返回 false 走正常还原——
    /// 宁可多还原一次，也不可误跳过。</summary>
    public static bool IsLiveSameAsSnapshot(GameEntry g, BackupRecord r)
    {
        try
        {
            var m = TryReadManifest(r.File);
            if (m?.Files == null || m.Paths == null) return false;
            // 路径表必须与当前配置一一对应：m.Files 的 pN/ 前缀按备份时的非注册表路径顺序编号
            var curPaths = g.SavePaths.Where(p => !p.IsReg).Select(p => p.Path).ToList();
            var distinct = curPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (curPaths.Count != distinct.Count) return false;   // 配置含重复路径，索引口径不确定
            var curNorm = distinct.Select(NormPath).ToList();
            var manNorm = m.Paths.Select(NormPath).ToList();
            if (curNorm.Count != manNorm.Count) return false;
            for (int i = 0; i < curNorm.Count; i++)
                if (!string.Equals(curNorm[i], manNorm[i], StringComparison.OrdinalIgnoreCase))
                    return false;   // 存档位置配置改过，不短路

            var live = LiveSignature(g);
            var target = m.Files
                .Select(f => (Rel: f.N, Size: f.S, Mtime: f.M))
                .OrderBy(x => x.Rel, StringComparer.Ordinal)
                .ToList();
            if (live.Count != target.Count) return false;   // 文件数不同（有新增/删除）
            for (int i = 0; i < live.Count; i++)
                if (!string.Equals(live[i].Rel, target[i].Rel, StringComparison.Ordinal)
                    || live[i].Size != target[i].Size || live[i].Mtime != target[i].Mtime)
                    return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>跨机/跨目录放宽口径的内容一致性：不核对 manifest.Paths 与本机存档根目录，
    /// 只按"相对路径全集 + 大小 + 精确 mtime"比对。云端下载的 zip 记的是源机路径，
    /// Restore 经 targetPaths 重映射落盘并回写精确 mtime 后，根目录不同但内容同一——
    /// 严格版（IsLiveSameAsSnapshot）会因路径表不等而漏判，使云端存档还原后误报「新存档未备份」。
    /// 多存档目录中存在同名相对路径（归属有歧义）、清单为空或任何不确定 → 一律 false。</summary>
    public static bool IsLiveSameAsSnapshotContent(GameEntry g, BackupRecord r)
    {
        try
        {
            var m = TryReadManifest(r.File);
            if (m?.Files is not { Count: > 0 }) return false;
            var target = new Dictionary<string, (long S, long M)>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in m.Files)
            {
                int slash = f.N.IndexOf('/');
                if (slash <= 0) return false;   // 条目必须带 pN/ 前缀
                string rel = f.N[(slash + 1)..];
                // 源机多个存档目录下出现同名相对路径 → 脱离根目录后归属有歧义，放弃放宽判定
                if (rel.Length == 0 || target.ContainsKey(rel)) return false;
                target[rel] = (f.S, f.M);
            }
            var live = LiveSignature(g);
            if (live.Count != target.Count) return false;
            foreach (var (entry, size, mtime) in live)
            {
                string rel = entry[(entry.IndexOf('/') + 1)..];
                if (!target.TryGetValue(rel, out var v) || v.S != size || v.M != mtime) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>在该玩家名下查找内容与当前 live 完全一致的备份（严格口径或跨目录口径命中均可），
    /// 按打包时间新→旧返回第一份。切换玩家时据此识别「live 只是某份已有备份的还原件」
    /// （典型：刚手动还原到旧档），不再打内容重复的「切换玩家」zip；找不到返回 null。</summary>
    public static BackupRecord? FindSnapshotMatchingLive(GameEntry g, string profile)
    {
        foreach (var r in g.Backups
                     .Where(b => string.Equals(b.Profile ?? "", profile ?? "", StringComparison.Ordinal))
                     .OrderByDescending(b => b.Time))
        {
            if (!IsRestorable(g, r)) continue;
            try
            {
                if (IsLiveSameAsSnapshot(g, r) || IsLiveSameAsSnapshotContent(g, r)) return r;
            }
            catch { /* 单份读取失败不影响继续查找更早的包 */ }
        }
        return null;
    }

    private static string NormPath(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\', '/'); }
        catch { return p; }
    }

    /// <summary>当前玩家筹码的 live 核对结果（以"心目中的存档"指针包为基准的时间轴模型）。
    /// 📍live=当前存档最新游戏内保存时间；★=她认可的那份备份（指针包；失效则回退最新可还原包）的存档时间。
    /// Kind：nosave=存档目录空；nobackup=有 live 但没有任何可还原备份；
    /// same=📍=★（正停在她认可的档；具体措辞由 FavoriteWhy 决定：已还原到/已备份·手动/已备份·自动/一致）；
    /// changed=📍&gt;★（玩出了未备份进度）；older=★&gt;📍（当前进度早于心目中的档，点击行可还原回去；
    /// 库中更新的备份由备份列表的橙色感叹号逐行提示，不再在筹码上告警）；
    /// unknown=无法确定（调用方保留静态文案）。
    /// BackupLatest=★的存档时间；MatchedAt=same 时=★时间；FavoriteWhy=指针移动原因。</summary>
    public readonly record struct LiveStateInfo(string Kind, int LiveFiles, DateTime LiveLatest,
        DateTime BackupLatest, int BackupCount, DateTime MatchedAt = default,
        DateTime RestoredAt = default, string FavoriteWhy = "");

    /// <summary>live 与备份"同一时刻"判定容差：同机 NTFS 为精确 ticks，留 2 秒兼容 FAT/exFAT/跨卷拷贝。</summary>
    internal const long LiveTimeToleranceTicks = TimeSpan.TicksPerSecond * 2;

    public static LiveStateInfo GetLiveState(GameEntry g, string profile)
    {
        try
        {
            var live = LiveSignature(g);
            long liveMax = live.Count > 0 ? live.Max(x => x.Mtime) : 0;
            var mine = g.Backups.Where(b => (b.Profile ?? "") == (profile ?? "")).ToList();
            // ★ 心目中的档：指针包优先，丢失/损坏回退最新可还原包（why=fallback）
            var fav = ProfileService.ResolveFavorite(g, profile ?? "", out string why);
            long favTime = fav != null && fav.SavedAt != DateTime.MinValue
                ? fav.SavedAt.ToUniversalTime().Ticks : 0;
            DateTime favAt = favTime > 0 ? new DateTime(favTime, DateTimeKind.Utc).ToLocalTime() : default;
            if (live.Count == 0)
                return new LiveStateInfo("nosave", 0, default, favAt, mine.Count, default, default, why);
            if (fav == null)
                return new LiveStateInfo("nobackup", live.Count,
                    new DateTime(liveMax, DateTimeKind.Utc), default, mine.Count, default, default, why);
            // 指针是空锚点（SavedAt=MinValue）：live 有内容即"选定空档之后玩出了进度"
            string kind;
            DateTime matchedAt = default;
            if (favTime == 0)
                kind = "changed";
            else if (liveMax - favTime > LiveTimeToleranceTicks)
                kind = "changed";
            else if (favTime - liveMax > LiveTimeToleranceTicks)
                kind = "older";
            else { kind = "same"; matchedAt = favAt; }
            return new LiveStateInfo(kind, live.Count, new DateTime(liveMax, DateTimeKind.Utc),
                favAt, mine.Count, matchedAt, default, why);
        }
        catch { return new LiveStateInfo("unknown", 0, default, default, 0); }
    }

    /// <summary>给备份行打"新于当前 live"运行时标记（还原旧档后，更新的备份行挂橙色感叹号）。
    /// st=null（游玩中等不核对场景）时统一清标。判定口径：备份 SavedAt 比 live 最新存档时间新 2 秒以上；
    /// live 目录为空时凡有存档时间的备份都算新于当前。纯展示，不落盘。</summary>
    public static void ApplyNewerThanLiveFlags(IEnumerable<BackupRecord> rows, LiveStateInfo? st)
    {
        bool hasLive = st is { LiveFiles: > 0 } && st.Value.Kind != "nosave";
        DateTime liveLocal = st is { } sv && sv.LiveLatest != default
            ? sv.LiveLatest.ToLocalTime() : default;
        long liveTicks = hasLive ? liveLocal.ToUniversalTime().Ticks : 0;
        foreach (var b in rows)
        {
            bool newer = st != null && b.SavedAt != DateTime.MinValue
                && (!hasLive || b.SavedAt.ToUniversalTime().Ticks - liveTicks > LiveTimeToleranceTicks);
            b.IsNewerThanLive = newer;
            b.NewerThanLiveTip = newer
                ? $"该备份存档于 {b.SavedAt:yyyy-MM-dd HH:mm}\n" +
                  $"当前存档停在 {(hasLive ? liveLocal.ToString("yyyy-MM-dd HH:mm") : "（存档目录为空）")}\n" +
                  "此备份新于当前存档，还原它将进入更新的进度"
                : "";
        }
    }

    /// <summary>当前 live 是否与基线完全一致（文件集/大小/mtime 全等；基线未设返回 false）。</summary>
    public static bool LiveMatchesBaseline(GameEntry g)
    {
        var baseLine = g.LiveBaseline;
        if (baseLine == null) return false;
        var cur = LiveSignature(g);
        if (cur.Count != baseLine.Count) return false;
        for (int i = 0; i < cur.Count; i++)
            if (cur[i].Rel != baseLine[i].Rel || cur[i].Size != baseLine[i].Size
                || cur[i].Mtime != baseLine[i].Mtime) return false;
        return true;
    }

    private static List<string> PurgeExtras(string root, string prefix, HashSet<string> want)
    {
        var failed = new List<string>();
        if (!Directory.Exists(root)) return failed;
        foreach (var f in SafeFiles(root))
        {
            try
            {
                string entry = prefix + Path.GetRelativePath(root, f).Replace('\\', '/');
                if (!want.Contains(entry)) File.Delete(f);
            }
            catch (Exception ex) { failed.Add($"{Path.GetRelativePath(root, f)}（{ex.GetType().Name}）"); }
        }
        // 删除清空后遗留的空目录（自底向上，不动根本身）
        try
        {
            var subDirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.Length);
            foreach (var d in subDirs)
                if (!Directory.EnumerateFileSystemEntries(d).Any())
                    Directory.Delete(d);
        }
        catch { }
        return failed;
    }

    public static void Delete(GameEntry g, BackupRecord r, bool cloudRemoteDeleted = false)
    {
        using var __zone = EnterGameZone(g.Id);
        // 是同玩家其他增量备份的基础 → 删除会断链，拒绝（不同玩家目录不同，不构成依赖）
        var fileName = Path.GetFileName(r.File);
        int dependents = g.Backups.Count(b =>
            !ReferenceEquals(b, r) && (b.Profile ?? "") == (r.Profile ?? "") &&
            b.BaseFile.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (dependents > 0)
            throw new InvalidOperationException(
                $"该备份是 {dependents} 份增量备份的基础，请先删除依赖它的备份");
        // 不再物理删除：移入回收区（保留 15 天可还原）。移动失败（占用/权限/跨盘复制失败）必须中止：
        // 记录照摘会让 zip 成为永久扫不出来的残留（游戏仍在库，孤儿扫描跳过整个目录）
        if (File.Exists(r.File))
        {
            var moved = RecycleService.MoveIn(g, r, "manual", cloudRemoteDeleted);
            if (moved == null)
                throw new InvalidOperationException(
                    $"备份移入「最近删除」失败，记录已保留：{Path.GetFileName(r.File)}\n" +
                    "请关闭占用该文件的程序（同步盘、压缩软件等）后重试。");
        }
        g.Backups.Remove(r);
        // 云端仍有这份 zip 时记入「不再自动下载」黑名单：用户删了就是不要了，
        // 不能让自动收集反复下载回来（防复活）。未配置云同步时内部直接跳过。
        CloudSyncService.OnLocalBackupsDeleted(g, new[] { r.File });
        // 名下备份删光也不清 ActiveProfile：卡槽是独立实体（持久化 Players 名单），
        // 他仍是当前玩家，下一份备份继续归他；只有右键卡槽「删除玩家」才移除卡槽。
        Store.Save();
        g.Refresh();
        g.RefreshVisibleBackups();
        g.RefreshProfiles();
        ProfileService.WriteGameMarker(g);
    }

    // ── 玩家维度的备份管理（新建/改名/删除玩家时调用）──

    /// <summary>创建第一位玩家时：把游戏根目录下的既有备份（归属"通用"）认领为该玩家的备份。
    /// 移动根目录 zip 到玩家子目录，更新记录的 Profile 与 File 路径；返回认领份数。</summary>
    public static int AdoptBackupsForPlayer(GameEntry g, string player)
    {
        string root = BackupDirFor(g);
        string dest = PlayerBackupDir(g, player);
        var legacy = g.Backups.Where(b => (b.Profile ?? "") == "").ToList();
        if (legacy.Count == 0 && !Directory.Exists(root)) return 0;

        bool moved = false;
        int moveErrors = 0;
        if (Directory.Exists(root))
        {
            Directory.CreateDirectory(dest);
            // 只移动根目录直属 zip（玩家子目录里的不动）
            foreach (var f in Directory.GetFiles(root, "*.zip"))
            {
                string target = Path.Combine(dest, Path.GetFileName(f));
                for (int n = 2; File.Exists(target); n++)
                    target = Path.Combine(dest, Path.GetFileNameWithoutExtension(f) + $"_{n}.zip");
                try { File.Move(f, target); moved = true; }
                catch { moveErrors++; }   // 被占用：留在根目录，记录保持通用归属，下次切换重试
            }
        }
        // 只认领导确认已搬走的：源文件仍在说明本次没移动，保持原记录（原路径仍有效）
        foreach (var b in legacy)
        {
            if (!string.IsNullOrEmpty(b.File) && File.Exists(b.File)) continue;
            b.Profile = player;
            if (!string.IsNullOrEmpty(b.File))
            {
                string name = Path.GetFileName(b.File);
                // 优先按文件名在目标目录定位实际落点（可能加了 _n 后缀）
                var actual = Directory.Exists(dest)
                    ? Directory.GetFiles(dest, name + "*").OrderBy(p => p.Length).FirstOrDefault()
                    : null;
                b.File = actual ?? Path.Combine(dest, name);
            }
        }
        if (legacy.Count > 0 || moved) Store.Save();
        if (moveErrors > 0)
            throw new InvalidOperationException($"有 {moveErrors} 个备份文件被占用，未能归入新玩家目录；其余已正常认领，被占用的会在下次切换时重试。");
        return legacy.Count;
    }

    /// <summary>玩家改名：重命名其备份子目录并同步记录。物理移动全部成功后才改记录；
    /// 有文件被占用时不动任何记录直接报错（文件与卡槽都保留原名，稍后重试即可；
    /// 已合并到新目录的部分下次重试会继续搬剩余文件）。</summary>
    public static void RenamePlayerBackups(GameEntry g, string oldName, string newName)
    {
        string oldDir = PlayerBackupDir(g, oldName);
        string newDir = PlayerBackupDir(g, newName);
        var moveErrors = new List<string>();
        if (Directory.Exists(oldDir) && !string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(BackupDirFor(g));
            try
            {
                if (Directory.Exists(newDir))
                {
                    // 目标已存在（上次改名搬了一半）：逐文件合并
                    foreach (var f in Directory.GetFiles(oldDir))
                    {
                        string df = Path.Combine(newDir, Path.GetFileName(f));
                        try
                        {
                            if (File.Exists(df))
                                moveErrors.Add($"{Path.GetFileName(f)}（新名称目录下已有同名文件）");
                            else File.Move(f, df);
                        }
                        catch (Exception ex) { moveErrors.Add($"{Path.GetFileName(f)}（{ex.Message}）"); }
                    }
                    if (moveErrors.Count == 0)
                    {
                        try { Directory.Delete(oldDir); } catch { /* 空目录残留不影响记录一致性 */ }
                    }
                }
                else
                {
                    Directory.Move(oldDir, newDir);
                }
            }
            catch (Exception ex) { moveErrors.Add($"{oldName} 目录移动失败（{ex.Message}）"); }
        }
        if (moveErrors.Count > 0)
            throw new InvalidOperationException(
                $"玩家改名未完成：{oldName} 的备份目录有文件被占用或同名冲突，所有记录已保持原样。\n" +
                "请关闭同步盘/压缩软件等占用程序后重试改名。\n" +
                string.Join("\n", moveErrors.Take(10)));
        foreach (var b in g.Backups)
        {
            if ((b.Profile ?? "") == oldName)
            {
                b.Profile = newName;
                if (!string.IsNullOrEmpty(b.File) && b.File.StartsWith(oldDir, StringComparison.OrdinalIgnoreCase))
                {
                    string newFile = newDir + b.File.Substring(oldDir.Length);
                    // 只改指到确实存在的新位置（防御：移动整体成功但个别历史记录路径异常）
                    if (File.Exists(newFile)) b.File = newFile;
                }
            }
        }
        Store.Save();
    }

    /// <summary>删除玩家：其全部 zip 移入回收区（15 天内可连同玩家备份一起找回），
    /// 备份记录从库中摘除，返回移入份数。被占用移不走的文件保留记录并汇总报错。</summary>
    public static int DeletePlayerBackups(GameEntry g, string player)
    {
        using var __zone = EnterGameZone(g.Id);
        var recs = g.Backups.Where(b => (b.Profile ?? "") == player).ToList();
        string dir = PlayerBackupDir(g, player);
        // 逐个移入回收区：只有移走的（及本就不存在的僵尸记录）才允许移除记录；
        // 被占用的保留记录，否则 zip 残留且玩家卡槽已删，文件永远扫不回来
        var deletedRecs = new List<BackupRecord>();
        var failedFiles = new List<string>();
        foreach (var b in recs)
        {
            if (!File.Exists(b.File)) { deletedRecs.Add(b); continue; }   // 僵尸记录：直接摘
            var moved = RecycleService.MoveIn(g, b, "player");
            if (moved != null) deletedRecs.Add(b);
            else failedFiles.Add($"{Path.GetFileName(b.File)}（文件被占用）");
        }
        // 玩家子目录残余（空目录/非 zip 内容）清空；失败不影响记录口径
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { failedFiles.Add($"{dir}（{ex.GetType().Name}）"); }
        foreach (var b in deletedRecs) g.Backups.Remove(b);
        if (deletedRecs.Count > 0)
        {
            Store.Save();
            // 删除玩家：名下云端仍有的 zip 一并记入「不再自动下载」黑名单，
            // 否则自动收集会把他的云端备份下载回来、把他复活成新玩家
            CloudSyncService.OnLocalBackupsDeleted(g, deletedRecs.Select(b => b.File));
        }
        if (failedFiles.Count > 0)
            throw new InvalidOperationException(
                $"玩家「{player}」有 {failedFiles.Count} 项被占用未能删除，相关记录已保留（卡槽与其余备份已删除）。\n" +
                "请关闭同步盘/压缩软件等占用程序后，再次删除该玩家的残留备份。\n" +
                string.Join("\n", failedFiles.Take(10)));
        return deletedRecs.Count;
    }

    // ── 游戏移除 / 重新入库：备份数据的脱离、找回、残留清理 ──

    /// <summary>脱离存放的玩家镜像目录名：backups\{游戏}\_profiles（游戏被移除时镜像从 profiles\{GUID} 迁到这里）。</summary>
    public const string DetachedProfilesDir = "_profiles";

    /// <summary>该游戏在磁盘上的备份数据总占用（字节）：游戏备份目录 + profiles\{GUID} 玩家镜像。</summary>
    public static long TotalBytesOnDisk(GameEntry g) =>
        DirSize(BackupDirFor(g)) + DirSize(ProfileService.RootFor(g));

    /// <summary>移除游戏时选择"连备份一起删除"：全部 zip 移入回收区（15 天内可找回），
    /// 再清空目录残余与玩家镜像（games.json 记录由调用方删除）。</summary>
    public static void DeleteAllOnRemoval(GameEntry g)
    {
        using var __zone = EnterGameZone(g.Id);
        // 在库记录指向的 zip 逐个收入（带完整记录快照，还原即回到备份列表）
        RecycleService.MoveInMany(g, g.Backups.ToList(), "removal");
        // 目录内可能还有记录之外的散包（历史手动放入/异常残留）：按文件路径一并回收
        string backupDir = BackupDirFor(g);
        if (Directory.Exists(backupDir))
            RecycleService.MoveInOrphanFolder(backupDir, "removal", g.DisplayName);
        TryDeleteDir(backupDir);                    // 空目录/玩家镜像等非 zip 残余
        TryDeleteDir(ProfileService.RootFor(g));    // 切换玩家用的 live 镜像：非备份包，不进回收区
    }

    // ── 备份目录迁移认领：旧根里的 zip 移动到当前根 ──

    /// <summary>备份目录迁移认领的候选（扫描结果）。</summary>
    public sealed record ReclaimCandidate(GameEntry Game, BackupRecord Record, string OldAbs, string NewAbs);

    /// <summary>扫描「当前备份根下文件缺失、但在旧根 oldRoot 仍存在」的备份记录。
    /// 相对路径记录按 oldRoot+相对段对位；旧版绝对路径记录指向旧根内且文件仍在的也一并纳入
    ///（能正常还原但文件不在新根，属「留在旧目录」，迁移后改写为相对存储）。
    /// 返回（缺失总数, 可认领列表）。不修改任何状态，供 UI 询问后调用 ReclaimFromRoot。</summary>
    public static (int Missing, List<ReclaimCandidate> Claimable) ScanReclaimFromRoot(string oldRoot)
    {
        var claimable = new List<ReclaimCandidate>();
        int missing = 0;
        string oldTrim = NormalizeRoot(oldRoot);
        if (oldTrim.Length == 0 || !Directory.Exists(oldTrim)) return (0, claimable);
        string curTrim = NormalizeRoot(Store.BackupRoot);
        if (string.Equals(oldTrim, curTrim, StringComparison.OrdinalIgnoreCase)) return (0, claimable);

        foreach (var g in Store.Games)
            foreach (var b in g.Backups)
            {
                string stored = b.FileStored;
                if (string.IsNullOrEmpty(stored)) continue;
                if (Path.IsPathRooted(stored))
                {
                    // 旧版绝对路径记录：仅当指向旧根且文件仍在时才可对位迁移
                    //（文件已随旧目录丢失的死记录不在此处理，由刷新/黑名单兜底）
                    if (!stored.StartsWith(oldTrim, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(stored)) continue;
                    string absRel = stored.Substring(oldTrim.Length).TrimStart('\\', '/');
                    if (absRel.Length == 0) continue;
                    claimable.Add(new ReclaimCandidate(g, b, stored, Path.Combine(curTrim, absRel)));
                    continue;
                }
                if (File.Exists(b.File)) continue;          // 相对记录：当前根下文件在位 → 无需迁移
                missing++;
                string oldAbs = Path.Combine(oldTrim, stored);
                if (!File.Exists(oldAbs)) continue;
                claimable.Add(new ReclaimCandidate(g, b, oldAbs, Path.Combine(curTrim, stored)));
            }
        return (missing, claimable);
    }

    /// <summary>执行迁移：把 ScanReclaimFromRoot 给出的候选从旧根移动到当前根（对位移动，
    /// 重名加序号），旧版绝对路径记录改写为新位置（自动转为相对存储）。返回移动份数。</summary>
    public static int ReclaimFromRoot(IEnumerable<ReclaimCandidate> candidates)
    {
        int moved = 0;
        var touched = new HashSet<GameEntry>();
        foreach (var c in candidates)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(c.NewAbs)!);
                string target = c.NewAbs;
                for (int n = 2; File.Exists(target); n++)
                    target = Path.Combine(Path.GetDirectoryName(c.NewAbs)!,
                        Path.GetFileNameWithoutExtension(c.NewAbs) + $"_{n}.zip");
                File.Move(c.OldAbs, target);
                // 无论原记录是绝对还是相对存储，重名加了 _n 后缀时都必须回写实际落点，否则记录指向缺失文件
                if (!string.Equals(c.Record.File, target, StringComparison.OrdinalIgnoreCase))
                    c.Record.File = target;             // setter 自动转为相对路径存储
                moved++;
                touched.Add(c.Game);
            }
            catch { /* 单个失败不中断整批 */ }
        }
        if (moved > 0)
        {
            Store.Save();
            foreach (var g in touched) g.Refresh();
        }
        return moved;
    }

    private static string NormalizeRoot(string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root)) return "";
            return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return ""; }
    }


    /// <summary>
    /// 游戏重新入库后找回旧备份：枚举 backups\{游戏名} 下的 zip（含玩家子目录），按 zip 内 manifest
    /// 重建备份记录（时间/玩家归属/增量链）。返回找回份数。
    ///
    /// 认领判定：目录名 SafeName 匹配游戏任一显示名（DisplayName / NameCN / NameEN）就认领，
    /// 不做 manifest.Paths 与当前 SavePaths 的路径交集校验——因为 Steam 游戏卸载/重装、学习版换
    /// 安装位、Steam↔学习版互转时存档路径位置变化完全正常。游戏名匹配已是充分信号。
    ///
    /// 多候选目录名是为了覆盖：学习版时代中文名目录 → Steam 扫描时英文名反查前没匹配上 → 反查到
    /// 中文名后才匹配上 的时间差场景；以及首次备份时还没反查到中文名、用了英文名目录的情况。
    /// </summary>
    public static int ReclaimOrphanBackups(GameEntry g, Dictionary<string, string> slotNameByDir,
                                           HashSet<string>? claimedFiles = null)
    {
        if (g.Backups.Count > 0) return 0;   // 有记录说明该条目一直没离开过库

        // 收集所有可能的候选目录名（SafeName 后去重）
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(g.DisplayName)) candidates.Add(SafeName(g.DisplayName));
        if (!string.IsNullOrWhiteSpace(g.NameCN)) candidates.Add(SafeName(g.NameCN));
        if (!string.IsNullOrWhiteSpace(g.NameEN)) candidates.Add(SafeName(g.NameEN));

        claimedFiles ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<BackupRecord>();
        List<string>? newestPaths = null;
        DateTime newestTime = DateTime.MinValue;
        foreach (var leaf in candidates)
        {
            string dir = Path.Combine(Store.BackupRoot, leaf);
            if (!Directory.Exists(dir)) continue;

            foreach (var zip in Directory.EnumerateFiles(dir, "*.zip", SearchOption.AllDirectories))
            {
                if (claimedFiles.Contains(zip)) continue;
                string[] parts = Path.GetRelativePath(dir, zip)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Length > 1 && parts[0].Equals(DetachedProfilesDir, StringComparison.OrdinalIgnoreCase))
                    continue;   // 脱离存放的玩家镜像目录（镜像不是 zip，保险跳过）
                var m = TryReadManifest(zip);
                if (m == null) continue;
                if (m.Time > newestTime) { newestTime = m.Time; newestPaths = m.Paths; }

                string profile = "";
                if (parts.Length > 1)
                {
                    string sub = parts[0];
                    if (!string.IsNullOrEmpty(m.Profile) && SafeName(m.Profile).Equals(sub, StringComparison.OrdinalIgnoreCase))
                        profile = m.Profile;
                    else if (!slotNameByDir.TryGetValue(sub, out var realName))
                        profile = sub;
                    else profile = realName;
                }
                found.Add(new BackupRecord
                {
                    Time = m.Time,
                    Profile = profile,
                    Note = "找回的旧备份",
                    File = zip,
                    Size = new FileInfo(zip).Length,
                    Kind = m.Kind,
                    BaseFile = m.BaseFile,
                    SavedAt = SavedAtFromManifest(m),
                    EmptyAnchor = m.Files.Count == 0,
                });
                claimedFiles.Add(zip);
            }
        }
        if (found.Count == 0) return 0;
        foreach (var r in found.OrderBy(r => r.Time))
            g.Backups.Insert(0, r);
        // 条目没有任何存档路径时（典型：索引丢失后从备份重建），用最新备份清单里的原始路径预填，
        // 尊重手动移除名单；路径是否仍存在由列表状态自己反映。
        if (g.SavePaths.Count == 0 && newestPaths != null)
        {
            foreach (var p in newestPaths
                         .Where(p => !string.IsNullOrWhiteSpace(p))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                g.TryAddAutoSavePath(new SavePathEntry { Path = p, Source = "旧备份" });
            }
        }
        Store.Save();
        return found.Count;
    }

    /// <summary>把从云端下载的 zip（任意临时位置）认领进指定游戏的备份库：
    /// 移入正确目录（玩家子目录）、登记 BackupRecord；同时间+同大小视为重复直接丢弃临时文件。
    /// 返回新建的记录（重复时返回已有记录，null=清单损坏）。必须在 UI 线程或后台均可调用。</summary>
    public static BackupRecord? AdoptDownloadedZip(GameEntry g, string downloadedZip)
    {
        using var __zone = EnterGameZone(g.Id);
        var m = TryReadManifest(downloadedZip);
        if (m == null) throw new InvalidOperationException("下载的备份清单（manifest）损坏，无法导入");
        string profile = m.Profile ?? "";
        long srcLen = new FileInfo(downloadedZip).Length;

        // 僵尸记录（条目还在、zip 已从磁盘消失：用户手删过文件、备份盘换过、整目录迁移丢过）与本次
        // 下载是同一份备份（同时间+同玩家）时先摘掉——否则下面读它的 Length 会抛 FileNotFoundException
        // 直接把导入打断（用户只看到「下载失败」），且文件落回来后同一条备份会出现两条记录。
        // 集合改动走 UI 线程：Backups 是绑定中的 ObservableCollection。
        bool droppedZombie = false;
        OnUi(() =>
        {
            foreach (var z in g.Backups.Where(b =>
                         b.Time == m.Time && (b.Profile ?? "") == profile && !File.Exists(b.File)).ToList())
            {
                g.Backups.Remove(z);
                droppedZombie = true;
            }
        });

        BackupRecord? existing = g.Backups.FirstOrDefault(b =>
            b.Time == m.Time && (b.Profile ?? "") == profile
            && File.Exists(b.File) && new FileInfo(b.File).Length == srcLen);
        if (existing != null)
        {
            ProfileService.RegisterSlot(g, profile);   // 幂等：他机新玩家的重复包也保证卡槽在
            try { File.Delete(downloadedZip); } catch { }
            if (droppedZombie) Store.Save();
            return existing;
        }

        string dir = profile.Length > 0 ? PlayerBackupDir(g, profile) : BackupDirFor(g);
        Directory.CreateDirectory(dir);
        string dest = Path.Combine(dir, Path.GetFileName(downloadedZip));
        // 同名同大小物理文件已在目标目录（条目重建后云端补传/另一同名片断共享目录）：
        // 直接挂到该文件上，绝不改名成 _2——否则增量包的 BaseFile 指向原名会断链
        if (File.Exists(dest) && new FileInfo(dest).Length == new FileInfo(downloadedZip).Length)
        {
            try { File.Delete(downloadedZip); } catch { }
            var dupRec = new BackupRecord
            {
                Time = m.Time,
                Profile = profile,
                Note = "云端下载",
                File = dest,
                Size = new FileInfo(dest).Length,
                Kind = m.Kind,
                BaseFile = m.BaseFile,
                SavedAt = SavedAtFromManifest(m),
                EmptyAnchor = m.Files.Count == 0,
            };
            OnUi(() =>
            {
                int di = 0;
                while (di < g.Backups.Count && g.Backups[di].Time > dupRec.Time) di++;
                g.Backups.Insert(di, dupRec);
                g.Refresh();
                g.RefreshVisibleBackups();
            });
            ProfileService.RegisterSlot(g, profile);   // 他机新玩家：下载即建卡槽
            Store.Save();
            return dupRec;
        }
        for (int n = 2; File.Exists(dest); n++)
            dest = Path.Combine(dir, Path.GetFileNameWithoutExtension(downloadedZip) + $"_{n}.zip");
        File.Move(downloadedZip, dest);

        var rec = new BackupRecord
        {
            Time = m.Time,
            Profile = profile,
            Note = "云端下载",
            File = dest,
            Size = new FileInfo(dest).Length,
            Kind = m.Kind,
            BaseFile = m.BaseFile,
            SavedAt = SavedAtFromManifest(m),
            EmptyAnchor = m.Files.Count == 0,
        };
        OnUi(() =>
        {
            int idx = 0;
            while (idx < g.Backups.Count && g.Backups[idx].Time > rec.Time) idx++;
            g.Backups.Insert(idx, rec);
            g.Refresh();
            g.RefreshVisibleBackups();
        });
        ProfileService.RegisterSlot(g, profile);   // 他机新玩家：下载即建卡槽
        Store.Save();
        return rec;
    }

    /// <summary>空库重建（games.json 丢失/损坏且无快照可恢复时）：为每个含 zip 的残留备份目录
    /// 生成占位游戏条目（exe 未知＝未安装），并自动找回 zip 备份记录、玩家镜像、激活玩家与存档路径。
    /// 之后重新安装游戏再扫描，会按游戏名占位条目自动补全 exe/AppId/封面（不产生重复卡片）。
    /// 返回 (重建条目数, 备份总数, 玩家档案总数)。</summary>
    public static (int games, int backups, int slots) RebuildLibraryFromBackups(
        HashSet<string>? claimedFiles = null)
    {
        claimedFiles ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 先快照目录清单：认领后残留列表会变化
        var dirs = ListOrphans()
            .Where(o => !o.IsProfileRoot && o.ZipCount > 0)
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int games = 0, backups = 0, slots = 0;
        foreach (var o in dirs)
        {
            var g = new GameEntry { NameCN = o.Name };
            Store.Games.Add(g);
            var (n, s) = ProfileService.ReclaimAll(g, claimedFiles);
            if (n == 0 && s == 0)
            {
                // zip 清单全损坏等极端情况：没有任何可挂载数据的卡片不留
                Store.Games.Remove(g);
                continue;
            }
            g.RefreshProfiles();
            g.RefreshVisibleBackups();
            g.Refresh();
            games++; backups += n; slots += s;
        }
        if (games > 0) Store.Save();
        return (games, backups, slots);
    }

    /// <summary>残留备份信息（游戏已从库中移除但数据还留在磁盘上）。</summary>
    public sealed class OrphanBackupInfo
    {
        public string Name { get; init; } = "";
        public string Dir { get; init; } = "";
        /// <summary>true=profiles\{GUID} 玩家镜像残留（早期版本移除产生）；false=backups\{游戏名} 备份目录。</summary>
        public bool IsProfileRoot { get; init; }
        public int ZipCount { get; init; }
        public int SlotCount { get; init; }
        public long Size { get; init; }
        public DateTime LastWrite { get; init; }
        public string TypeText => IsProfileRoot ? "玩家镜像" : "游戏备份";
        public string DetailText => IsProfileRoot ? $"{SlotCount} 个玩家档案" : $"{ZipCount} 份备份";
        public string SizeText => FormatBytes(Size);
        public string LastWriteText => LastWrite == DateTime.MinValue ? "" : LastWrite.ToString("yyyy-MM-dd");
    }

    /// <summary>枚举磁盘上匹配不到任何在库游戏的残留：游戏名备份目录 + profiles 下的 GUID 镜像目录。</summary>
    public static List<OrphanBackupInfo> ListOrphans()
    {
        var result = new List<OrphanBackupInfo>();
        // 归档条目（删库留档的完结游戏）视同已移除：其残留备份仍要在「未关联备份」窗口可见可删
        var liveGames = Store.Games.Where(g => !g.Archived).ToList();
        var gameNames = liveGames
            .Select(g => SafeName(g.DisplayName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gameIds = liveGames.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 备份记录实际指向的顶层目录（游戏反查到中文名后，旧英文名目录仍承载在库备份，不得当残留删）
        var usedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in liveGames)
            foreach (var b in g.Backups)
            {
                try
                {
                    string top = Path.GetRelativePath(Store.BackupRoot, b.File)
                        .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .FirstOrDefault() ?? "";
                    if (top.Length > 0) usedRoots.Add(top);
                }
                catch { }
            }

        if (Directory.Exists(Store.BackupRoot))
        {
            foreach (var d in Directory.EnumerateDirectories(Store.BackupRoot))
            {
                string leaf = Path.GetFileName(d);
                if (leaf.Equals("profiles", StringComparison.OrdinalIgnoreCase)) continue;
                if (leaf.Equals(RecycleService.DirName, StringComparison.OrdinalIgnoreCase)) continue;  // 回收区
                if (leaf.Equals(Store.AppSnapshotDir, StringComparison.OrdinalIgnoreCase)) continue;   // 程序自身数据快照
                if (gameNames.Contains(leaf)) continue;    // 属于某在库游戏
                if (usedRoots.Contains(leaf)) continue;    // 被在库游戏的备份记录引用（历史英文名目录）
                result.Add(BuildOrphan(d, false));
            }
        }
        string profileRoot = ProfileService.ProfilesRoot;
        if (Directory.Exists(profileRoot))
        {
            foreach (var d in Directory.EnumerateDirectories(profileRoot))
            {
                if (gameIds.Contains(Path.GetFileName(d))) continue;
                result.Add(BuildOrphan(d, true));
            }
        }
        return result.OrderByDescending(x => x.Size).ToList();
    }

    private static OrphanBackupInfo BuildOrphan(string dir, bool isProfileRoot)
    {
        int zips = 0, slots = 0;
        long size = 0;
        var latest = DateTime.MinValue;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(f);
                    size += fi.Length;
                    if (fi.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)) zips++;
                    if (fi.LastWriteTime > latest) latest = fi.LastWriteTime;
                }
                catch { }
            }
            if (isProfileRoot) slots = Directory.GetDirectories(dir).Length;
        }
        catch { }
        // GUID 镜像根：优先显示 game.json 身份证里的真实游戏名（没有身份证的老残留才显示 GUID）
        string name = Path.GetFileName(dir);
        string? markerName = null;
        if (isProfileRoot)
            markerName = ProfileService.TryReadRootMarker(dir)?.DisplayName;
        if (!string.IsNullOrWhiteSpace(markerName)) name = markerName;
        return new OrphanBackupInfo
        {
            Name = name,
            Dir = dir,
            IsProfileRoot = isProfileRoot,
            ZipCount = zips,
            SlotCount = slots,
            Size = size,
            LastWrite = latest,
        };
    }

    /// <summary>删除一个残留目录（仅允许删除备份根之内的路径；UI 调用前须二次确认）。
    /// 目录内 zip 先进回收区（15 天可还原），其余内容递归清空。</summary>
    public static void DeleteOrphan(string dir)
    {
        string root = Path.GetFullPath(Store.BackupRoot).TrimEnd(Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能删除备份目录内的残留数据");
        // 回收区自身禁止从这里删（它不是任何游戏的残留目录，且批量回收时正被读写）
        if (full.StartsWith(
                Path.GetFullPath(RecycleService.RecycleDir).TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("「最近删除」里的数据请在最近删除窗口中清理");
        // 归档游戏的死记录带完整快照，优先按记录收入（还原能直接回列表）；其余散包由目录扫描兜底
        string prefix = full + Path.DirectorySeparatorChar;
        foreach (var g in Store.Games)
        {
            var recs = g.Backups.Where(b =>
            {
                if (string.IsNullOrEmpty(b.File)) return false;
                try { return Path.GetFullPath(b.File).StartsWith(prefix, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }).ToList();
            if (recs.Count > 0) RecycleService.MoveInMany(g, recs, "orphan");
        }
        RecycleService.MoveInOrphanFolder(full);
        Directory.Delete(full, recursive: true);
        PruneRecordsUnder(full);
    }

    /// <summary>残留目录删完后，清掉条目上指向该目录、文件已不存在的死备份记录。
    /// 主要场景：归档留档（删库留完结档案）游戏的备份被用户在「未关联备份」窗口删掉，
    /// 条目上的 Backups 不会自愈，复活后详情页会出现点不动的幽灵行。
    /// 同步摘掉指向死包的「心目中的存档」指针、写入云同步防复活黑名单并落盘。</summary>
    private static void PruneRecordsUnder(string deletedDir)
    {
        string prefix = deletedDir + Path.DirectorySeparatorChar;
        bool changed = false;
        foreach (var g in Store.Games)
        {
            var dead = g.Backups.Where(b =>
            {
                if (string.IsNullOrEmpty(b.File) || File.Exists(b.File)) return false;
                try { return Path.GetFullPath(b.File).StartsWith(prefix, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }).ToList();
            if (dead.Count == 0) continue;

            var deadFiles = dead.Select(b => b.File).ToList();
            foreach (var b in dead) g.Backups.Remove(b);
            // 指针包已随残留删除：即时摘掉 SwitchAnchors（ProfileService 也会自愈，这里避免复活瞬间错锚）
            if (g.SwitchAnchors is { Count: > 0 })
            {
                foreach (var key in g.SwitchAnchors
                    .Where(kv => deadFiles.Any(f => string.Equals(
                        BackupRecord.ResolveFile(kv.Value ?? ""), f, StringComparison.OrdinalIgnoreCase)))
                    .Select(kv => kv.Key).ToList())
                {
                    g.SwitchAnchors.Remove(key);
                    g.FavoriteWhy?.Remove(key);
                }
            }
            // 用户主动删残留=不再需要：云端同份 zip 进防自动下载黑名单，避免自动收集给复活游戏下回来
            CloudSyncService.OnLocalBackupsDeleted(g, deadFiles);
            g.RefreshVisibleBackups();
            g.RefreshProfiles();
            g.Refresh();
            changed = true;
        }
        if (changed) Store.Save();
    }

    public static string FormatBytes(long b)
    {
        double mb = b / 1024.0 / 1024.0;
        if (mb >= 1024) return $"{mb / 1024:F2} GB";
        if (mb >= 1) return $"{mb:F1} MB";
        return $"{b / 1024.0:F0} KB";
    }

    private static long DirSize(string dir)
    {
        long total = 0;
        foreach (var f in SafeFiles(dir))
            try { total += new FileInfo(f).Length; } catch { }
        return total;
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    public static void Prune(GameEntry g)
    {
        int max = Math.Max(1, Store.Settings.MaxKeepPerGame);
        string profile = g.ActiveProfile ?? "";
        // 只在当前玩家的备份范围内清理；其他玩家的备份不受保留份数限制影响
        var mine = g.Backups.Where(b => (b.Profile ?? "") == profile).ToList();
        // 保留份数规则：只有 🔒锁定 备份额外保留、不占配额；「心目中的存档」只是玩家认可的进度指针，
        // 不做特别保留——指针包被淘汰后 ResolveFavorite 会自动回退到最新可还原包，还原能力不受影响。
        // 只删除「没有后继增量引用」的备份，保证增量链始终完整。
        // 列表按新→旧插入，Last 即最旧（时间相同同秒创建时也比按 Time 排序可靠）
        while (mine.Count(b => !b.Locked) > max)
        {
            var referenced = mine
                .Where(b => b.BaseFile.Length > 0)
                .Select(b => b.BaseFile)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = mine
                .Where(b => !referenced.Contains(Path.GetFileName(b.File)))
                .Where(b => !b.Locked)   // 🔒锁定：不参与自动淘汰（手动删除照常）；心目中指针包照常淘汰
                .ToList();
            if (candidates.Count == 0) break;  // 未锁定且不在增量链上的候选没了 → 宁可超限也不删锁定项
            // 运行中热快照价值低、时效短 → 优先淘汰，尽量保住冷备份链（列表新→旧，Last 即最旧）
            var victim = candidates.LastOrDefault(b => b.Hot) ?? candidates.Last();
            if (ReferenceEquals(victim, mine[0])) break;      // 只能牺牲最新还原点 → 停止
            // 淘汰同样先进回收区（用户指定：15 天内可找回；还原后如想长期保留请手动锁定）
            if (File.Exists(victim.File))
            {
                var moved = RecycleService.MoveIn(g, victim, "prune");
                if (moved == null) break;   // 被占用/移动失败：保留记录并停止本轮（下轮再试）
            }
            g.Backups.Remove(victim);
            mine.Remove(victim);
        }
    }

    public static bool HasAnySave(GameEntry g) =>
        g.SavePaths.Any(p => !p.IsReg && System.IO.Directory.Exists(p.Path) &&
                             System.IO.Directory.EnumerateFileSystemEntries(p.Path).Any());

    public static bool IsGameRunning(GameEntry g)
    {
        if (string.IsNullOrEmpty(g.ProcessName)) return false;
        try
        {
            var procs = Process.GetProcessesByName(g.ProcessName);
            // GetProcessesByName 返回的每个 Process 对象都持有进程句柄，必须显式释放
            foreach (var p in procs) p.Dispose();
            return procs.Length > 0;
        }
        catch { return false; }
    }

    private static void CheckGameRunning(GameEntry g, string op)
    {
        if (IsGameRunning(g))
            throw new InvalidOperationException($"游戏正在运行，请先退出 {g.DisplayName} 再{op}");
    }

    /// <summary>从最新备份往前数连续增量的个数（用于到达阈值后强制全量）。调用方须传入同一玩家的备份。</summary>
    private static int CountIncrSinceFull(List<BackupRecord> recordsNewestFirst)
    {
        int n = 0;
        foreach (var b in recordsNewestFirst)
        {
            if (b.Kind != "incr") break;
            n++;
        }
        return n;
    }

    /// <summary>读 zip 内 manifest（损坏/不存在返回 null）。供云同步重建玩家镜像使用。</summary>
    public static BackupManifest? ReadManifest(string zipFile) => TryReadManifest(zipFile);

    private static BackupManifest? TryReadManifest(string zipFile)
    {
        try
        {
            using var fs = File.OpenRead(zipFile);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var e = zip.GetEntry("manifest.json");
            if (e == null) return null;
            using var rd = new StreamReader(e.Open());
            return JsonSerializer.Deserialize<BackupManifest>(rd.ReadToEnd());
        }
        catch { return null; }
    }

    /// <summary>从 manifest 计算「存档时间」：Files 中最大 M（游戏真实保存时刻，毫秒级 UTC ticks）转本地时间。
    /// 空文件全集（空锚点）或无有效时间返回 MinValue。增量包的 Files 是该快照最终文件全集，口径同样正确。</summary>
    public static DateTime SavedAtFromManifest(BackupManifest? m)
    {
        try
        {
            if (m == null || m.Files.Count == 0) return DateTime.MinValue;
            long maxTicks = m.Files.Max(f => f.M);
            if (maxTicks <= 0) return DateTime.MinValue;
            return new DateTime(maxTicks, DateTimeKind.Utc).ToLocalTime();
        }
        catch { return DateTime.MinValue; }
    }

    /// <summary>启动后台一次性回填：旧记录 SavedAt==MinValue 的从 zip manifest 补算存档时间
    /// （读不到/损坏的保持 MinValue，下次启动再试）。幂等；返回补算条数，有变更时落盘。</summary>
    public static int BackfillSavedAt()
    {
        int n = 0;
        try
        {
            foreach (var g in Store.Games)
            {
                bool dirty = false;
                foreach (var b in g.Backups)
                {
                    if (b.SavedAt != DateTime.MinValue) continue;
                    var m = TryReadManifest(b.File);
                    if (m == null) continue;
                    b.SavedAt = SavedAtFromManifest(m);
                    b.EmptyAnchor = m.Files.Count == 0;
                    dirty = true;
                    n++;
                }
                if (dirty) Store.Save();
            }
        }
        catch { /* 单条失败不影响其余；下次启动重试 */ }
        return n;
    }

    private static BackupManifest ReadManifestOrThrow(string zipFile)
    {
        var m = TryReadManifest(zipFile);
        if (m == null)
            throw new InvalidOperationException("备份包损坏（缺少 manifest）");
        return m;
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            string[] subDirs = Array.Empty<string>();
            try { subDirs = System.IO.Directory.GetDirectories(cur); } catch { }
            foreach (var d in subDirs) stack.Push(d);
            string[] files = Array.Empty<string>();
            try { files = System.IO.Directory.GetFiles(cur); } catch { }
            foreach (var f in files) yield return f;
        }
    }
}
