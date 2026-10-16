using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GameSaveManager.Models;

namespace GameSaveManager.Services;

/// <summary>备份回收区：所有用户/轮转删除的 zip 先移入 backups\_recycle\ 并登记 recycle.json，
/// 保留 15 天可还原，过期由启动清理与每日清理物理删除。
/// 设计要点：
/// ① 物理移动而非复制——同盘秒成、不占双份空间；跨盘（自定义备份根）退化为复制+删除。
/// ② 扁平存放（{guid}.zip）+ 索引保存原始相对路径/原文件名，避免同名冲突、保证增量链还原对位。
/// ③ 索引在 %APPDATA%（BaseDir），实体在备份根——备份根改到移动盘后索引仍随应用配置走。
/// ④ 全部方法线程安全（与备份服务闸串行调用为主，锁兜底索引读写）。</summary>
public static class RecycleService
{
    public const int RetentionDays = 15;
    public const string DirName = "_recycle";

    private static readonly object Gate = new();
    private static List<RecycleItem>? _items;
    private static bool _loaded;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    public static string RecycleDir => Path.Combine(Store.BackupRoot, DirName);
    public static string IndexFile => Path.Combine(Store.BaseDir, "recycle.json");

    // ── 索引读写 ──────────────────────────────────────────────

    private static List<RecycleItem> Items
    {
        get
        {
            if (_loaded) return _items!;
            _items = Load();
            _loaded = true;
            return _items;
        }
    }

    /// <summary>测试/备份根切换后强制下次重新读盘（正常流程无须调用）。</summary>
    public static void ReloadForTests()
    {
        lock (Gate) { _items = null; _loaded = false; }
    }

    private static List<RecycleItem> Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(IndexFile))
                {
                    var list = JsonSerializer.Deserialize<List<RecycleItem>>(
                        File.ReadAllText(IndexFile), JsonOpts);
                    if (list != null) return list;
                }
            }
            catch { /* 索引损坏不致命：当空回收区处理，实体由 PurgeOrphanFiles 自愈 */ }
            return new List<RecycleItem>();
        }
    }

    private static void Save()
    {
        // 调用方须持锁
        try
        {
            Directory.CreateDirectory(Store.BaseDir);
            string tmp = IndexFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, JsonOpts));
            File.Move(tmp, IndexFile, overwrite: true);
        }
        catch { /* 索引写失败不反噬删除主流程；下次删除会再次尝试落盘 */ }
    }

    /// <summary>回收区全部条目（新→旧删除顺序的副本）。</summary>
    public static List<RecycleItem> List()
    {
        lock (Gate)
            return Items.OrderByDescending(x => x.DeletedAt).ThenByDescending(x => x.Time).ToList();
    }

    public static int Count
    {
        get { lock (Gate) return Items.Count; }
    }

    public static long TotalBytes
    {
        get { lock (Gate) return Items.Sum(x => x.Size); }
    }

    // ── 移入 ──────────────────────────────────────────────────

    /// <summary>把一份在库备份的 zip 移入回收区（调用方负责之后从 g.Backups 摘记录/写云端黑名单/Store.Save）。
    /// 文件不存在（僵尸记录清理）返回 null——无实体可回收，不产生空项。</summary>
    public static RecycleItem? MoveIn(GameEntry g, BackupRecord r, string source, bool remoteAlsoDeleted = false)
    {
        string abs;
        string stored;
        try
        {
            abs = r.File;
            stored = r.FileStored;
        }
        catch { return null; }
        if (string.IsNullOrEmpty(abs) || !File.Exists(abs)) return null;

        string origRel = BuildOrigRel(abs, stored);
        var item = NewItem(Path.GetFileName(abs), origRel, source);
        item.GameId = g.Id ?? "";
        item.GameName = g.DisplayName ?? "";
        item.Profile = r.Profile ?? "";
        item.RecordJson = JsonSerializer.Serialize(r);
        item.Size = SafeLen(abs);
        item.Time = r.Time;
        item.SavedAt = r.SavedAt;
        item.Note = r.Note ?? "";
        item.Kind = r.Kind ?? "";
        item.Hot = r.Hot;
        item.RemoteAlsoDeleted = remoteAlsoDeleted;
        return CommitMoveIn(abs, item) ? item : null;
    }

    /// <summary>批量移入（删玩家/移除游戏）；只收文件仍在盘上的，返回成功移入的条目。</summary>
    public static List<RecycleItem> MoveInMany(GameEntry g, IEnumerable<BackupRecord> records, string source)
    {
        var moved = new List<RecycleItem>();
        foreach (var r in records.ToList())
        {
            var item = MoveIn(g, r, source);
            if (item != null) moved.Add(item);
        }
        return moved;
    }

    /// <summary>把一个备份根内目录下的全部 zip（含玩家子目录）逐个收入回收区，
    /// 保留相对备份根的完整路径（游戏名\玩家\xxx.zip），还原后未关联备份扫描可重新发现。
    /// source：orphan=未关联备份窗口删除；removal=移除游戏时连备份删除的目录内无记录散包。
    /// 返回成功移入的 zip 数。调用方随后照常递归删除目录残余（玩家镜像等非 zip 内容）。</summary>
    public static int MoveInOrphanFolder(string dir, string source = "orphan", string gameNameOverride = "")
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
        string root;
        try { root = Path.GetFullPath(Store.BackupRoot); }
        catch { return 0; }
        string fullDir = Path.GetFullPath(dir);
        if (!IsUnderRoot(fullDir, root)) return 0;   // 只收备份根之内的残留（与 DeleteOrphan 白名单一致）

        string gameName = fullDir
            .Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault() ?? "";
        int n = 0;
        foreach (var zip in Directory.EnumerateFiles(fullDir, "*.zip", SearchOption.AllDirectories).ToList())
        {
            string rel = Path.GetRelativePath(root, zip);
            string profile = "";
            var segs = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segs.Length >= 3) profile = segs[1];   // 游戏名\玩家\xxx.zip
            var item = NewItem(Path.GetFileName(zip), rel.Replace('\\', '/'), source);
            item.GameName = string.IsNullOrEmpty(gameNameOverride) ? gameName : gameNameOverride;
            item.Profile = profile;
            item.Size = SafeLen(zip);
            item.Time = File.GetLastWriteTime(zip);
            if (CommitMoveIn(zip, item)) n++;
        }
        return n;
    }

    private static RecycleItem NewItem(string origFileName, string origRel, string source)
    {
        var now = DateTime.Now;
        return new RecycleItem
        {
            Id = Guid.NewGuid().ToString("N"),
            OrigFileName = origFileName,
            OrigRel = origRel,
            Source = source,
            DeletedAt = now,
            ExpireAt = now.AddDays(RetentionDays),
        };
    }

    private static bool CommitMoveIn(string srcAbs, RecycleItem item)
    {
        try
        {
            Directory.CreateDirectory(RecycleDir);
            string dst = Path.Combine(RecycleDir, item.Id + ".zip");
            MoveFile(srcAbs, dst);
            lock (Gate)
            {
                Items.Add(item);
                Save();
            }
            return true;
        }
        catch
        {
            // 移动失败（占用/权限）：不登记、不摘原记录，由调用方按"文件仍在"语义中止删除
            return false;
        }
    }

    // ── 还原 ──────────────────────────────────────────────────

    public sealed record RestoreResult(bool Ok, bool GameFound, string FinalPath, string Message);

    /// <summary>还原一份回收项：zip 归位原路径；游戏在库则把备份记录写回 games.json。
    /// 游戏已不在库时只归位文件，由「未关联备份」窗口重新挂载。</summary>
    public static RestoreResult Restore(string itemId)
    {
        RecycleItem? item;
        lock (Gate) item = Items.FirstOrDefault(x => x.Id == itemId);
        if (item == null) return new RestoreResult(false, false, "", "回收项不存在（可能已过期清除）");

        string stored = Path.Combine(RecycleDir, item.Id + ".zip");
        if (!File.Exists(stored))
        {
            // 实体已丢：清掉死索引
            lock (Gate) { Items.Remove(item); Save(); }
            return new RestoreResult(false, false, "", "回收区中的备份文件已丢失");
        }

        // 目标路径：相对记录按当前备份根拼；根外绝对记录原样归位
        string target = Path.IsPathRooted(item.OrigRel)
            ? item.OrigRel
            : Path.Combine(Store.BackupRoot, item.OrigRel.Replace('/', Path.DirectorySeparatorChar));
        // 目标文件名始终用原始名（OrigRel 末段即原名；正常路径二者一致）
        try
        {
            string? parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            if (File.Exists(target))
                target = NextFreeName(target);   // 同秒重备/手动放过同名文件：加 _还原N 后缀，绝不覆盖
            MoveFile(stored, target);
        }
        catch (Exception ex)
        {
            return new RestoreResult(false, false, "", $"文件归位失败：{ex.Message}");
        }

        // 撤销删除时写的云端「不再下载」墓碑，恢复自动收集/对账资格
        try { CloudSyncService.UndismissByFileName(item.OrigFileName); } catch { }

        // 在库游戏：写回备份记录
        var g = FindGame(item);
        bool recordRestored = false;
        if (g != null && !string.IsNullOrEmpty(item.RecordJson))
        {
            try
            {
                var rec = JsonSerializer.Deserialize<BackupRecord>(item.RecordJson, JsonOpts);
                if (rec != null)
                {
                    rec.File = target;
                    bool dup = g.Backups.Any(b =>
                        string.Equals(b.File, target, StringComparison.OrdinalIgnoreCase));
                    if (!dup)
                    {
                        // 备份列表按新→旧存放，按时间插回正确位置（与新建/认领路径同口径）
                        int idx = 0;
                        while (idx < g.Backups.Count && g.Backups[idx].Time > rec.Time) idx++;
                        g.Backups.Insert(idx, rec);
                        Store.Save();
                        try
                        {
                            g.Refresh();
                            g.RefreshVisibleBackups();
                            g.RefreshProfiles();
                            ProfileService.WriteGameMarker(g);
                        }
                        catch { /* 展示层刷新失败不影响还原事实 */ }
                        recordRestored = true;
                    }
                }
            }
            catch { /* 快照损坏：文件已归位，仍可由未关联/重新扫描找回 */ }
        }

        lock (Gate)
        {
            Items.Remove(item);
            Save();
        }

        string msg = g != null
            ? recordRestored
                ? "已还原到备份列表"
                : "文件已归位（记录未写回，可在备份列表刷新后查看）"
            : "游戏不在库中，文件已归位；重新添加同名游戏后会自动找回";
        return new RestoreResult(true, g != null, target, msg);
    }

    /// <summary>按 Id 找游戏：先精确 Id，再按游戏名兜底（游戏删除后重新入库会换 GUID）。</summary>
    private static GameEntry? FindGame(RecycleItem item)
    {
        var g = Store.Games.FirstOrDefault(x => x.Id == item.GameId);
        if (g != null) return g;
        return Store.Games.FirstOrDefault(x =>
            string.Equals(x.DisplayName, item.GameName, StringComparison.OrdinalIgnoreCase));
    }

    private static string NextFreeName(string path)
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int n = 1; ; n++)
        {
            string candidate = Path.Combine(dir, $"{name}_还原{n}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    // ── 永久删除 / 清空 / 过期清理 ────────────────────────────

    /// <summary>立即物理删除单项。文件占用时抛异常、索引保留。</summary>
    public static void PurgeNow(string itemId)
    {
        RecycleItem? item;
        lock (Gate) item = Items.FirstOrDefault(x => x.Id == itemId);
        if (item == null) return;
        string stored = Path.Combine(RecycleDir, item.Id + ".zip");
        if (File.Exists(stored)) File.Delete(stored);   // 占用抛错，由调用方提示
        lock (Gate) { Items.Remove(item); Save(); }
    }

    /// <summary>清空回收区：单项失败不中断，返回失败数（占用文件保留索引）。</summary>
    public static (int Removed, int Failed) ClearAll()
    {
        List<RecycleItem> snapshot;
        lock (Gate) snapshot = Items.ToList();
        int removed = 0, failed = 0;
        var gone = new List<RecycleItem>();
        foreach (var item in snapshot)
        {
            string stored = Path.Combine(RecycleDir, item.Id + ".zip");
            try
            {
                if (File.Exists(stored)) File.Delete(stored);
                gone.Add(item);
                removed++;
            }
            catch { failed++; }
        }
        lock (Gate)
        {
            foreach (var item in gone) Items.Remove(item);
            Save();
        }
        return (removed, failed);
    }

    /// <summary>清除过期项；同时自愈"实体在但索引丢"的孤儿 zip（移动流程崩溃残留）。
    /// 应用启动执行一次，之后每天由监控时钟补一次（长期不重启的机器）。</summary>
    public static int PurgeExpired()
    {
        int purged = 0;
        var now = DateTime.Now;
        lock (Gate)
        {
            var expired = Items.Where(x => x.ExpireAt <= now).ToList();
            var keep = new List<RecycleItem>();
            foreach (var item in expired)
            {
                try
                {
                    string stored = Path.Combine(RecycleDir, item.Id + ".zip");
                    if (File.Exists(stored)) File.Delete(stored);
                    purged++;
                }
                catch
                {
                    keep.Add(item);   // 占用：本轮保留，明天再试
                }
            }
            if (expired.Count > 0)
            {
                Items.RemoveAll(x => expired.Contains(x) && !keep.Contains(x));
                Save();
            }
            PurgeOrphanFilesLocked();
        }
        return purged;
    }

    /// <summary>实体在盘、索引无记录的 zip（极少见：移动成功后落索引前崩溃）→ 直接清理。调用方持锁。</summary>
    private static void PurgeOrphanFilesLocked()
    {
        try
        {
            if (!Directory.Exists(RecycleDir)) return;
            var indexed = Items.Select(x => x.Id + ".zip")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(RecycleDir, "*.zip"))
            {
                if (indexed.Contains(Path.GetFileName(f))) continue;
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    // ── 文件/路径工具 ─────────────────────────────────────────

    /// <summary>原 zip 相对备份根的路径：优先用记录的存储值（相对口径原样保留），
    /// 根外绝对路径原样返回；兜底从绝对路径实时计算。统一用 '/' 存入索引。</summary>
    private static string BuildOrigRel(string abs, string stored)
    {
        if (!string.IsNullOrEmpty(stored) && !Path.IsPathRooted(stored))
            return stored.Replace('\\', '/');
        try
        {
            string root = Path.GetFullPath(Store.BackupRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(abs)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string rel = Path.GetRelativePath(root, full);
            if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
                return full;   // 根外（外部导入路径）：存绝对
            return rel.Replace('\\', '/');
        }
        catch { return abs; }
    }

    private static bool IsUnderRoot(string full, string root) =>
        full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static long SafeLen(string file)
    {
        try { return new FileInfo(file).Length; }
        catch { return 0; }
    }

    /// <summary>同盘原子改名；跨盘（备份根在其它卷）退化为复制+删除。</summary>
    private static void MoveFile(string src, string dst)
    {
        try { File.Move(src, dst, overwrite: false); }
        catch (IOException) when (!SameVolume(src, dst))
        {
            File.Copy(src, dst, overwrite: false);
            File.Delete(src);
        }
    }

    private static bool SameVolume(string a, string b)
    {
        try { return string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b),
            StringComparison.OrdinalIgnoreCase); }
        catch { return true; }   // 判断失败按同盘重试原异常
    }
}
