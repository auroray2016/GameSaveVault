using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.ObjectModel;
using GameSaveManager.Models;

namespace GameSaveManager.Services;

/// <summary>全局设置（settings.json 持久化）。</summary>
public class AppSettings
{
    public string Theme { get; set; } = "system";         // 外观主题：system 跟随系统 / light 浅色 / dark 深色
    public int MaxKeepPerGame { get; set; } = 10;         // 每游戏保留份数
    public bool IncrementalEnabled { get; set; } = false; // 增量备份（仅打包变化文件），默认关闭，需要时在设置中开启
    public int FullEveryN { get; set; } = 5;              // 每 N 次增量后强制一次全量
    public bool ScanOnStartup { get; set; } = false;      // 启动时自动扫描 Steam 库
    public string BackupDir { get; set; } = "";           // 备份根目录（空 = 默认 数据目录\backups）
    /// <summary>上一个备份根目录（修改 BackupDir 时自动记录）。启动时若发现备份文件在当前根缺失
    /// 而在旧根仍在，提示用户「移动认领」到新目录；处理完成后清空。</summary>
    public string PrevBackupDir { get; set; } = "";
    /// <summary>删除备份时是否默认同时删除云端副本。每次删除有云端副本的备份时弹对话框，
    /// 此值作为 checkbox 默认勾选状态；用户每次操作后回写，下次沿用。</summary>
    public bool CloudDelWithCloud { get; set; } = false;
    /// <summary>默认卡槽名（新加入游戏的虚拟玩家名），设置中可修改。</summary>
    public string DefaultPlayerName { get; set; } = "玩家1";
    public int SortMode { get; set; } = 0;                // 列表排序：0=最近游玩 1=名称 2=最近添加
    public bool SortDesc { get; set; } = true;            // 方向：true=新→旧 / Z→A
    public DateTime RecentAddedSeenAt { get; set; } = DateTime.MinValue;  // 「最近添加」蓝点已读时间
    public double SidebarWidth { get; set; } = 310;       // 侧栏宽度（可拖动，240–520）
    public int CnNameSource { get; set; } = 0;            // 中文名快捷数据源：0=自动 1=小黑盒 2=Steam
    public List<NameSourceDef>? CustomNameSources { get; set; }  // 中文名数据源（内置覆盖 + 用户自定义），null 时由 GameNameService 填充默认
    public List<AppIdSourceDef>? CustomAppIdSources { get; set; } // AppId 搜索数据源（内置覆盖 + 用户自定义），null 时由 AppIdSourceService 填充默认
    public int HotSnapshotMinutes { get; set; } = 15;     // 运行中定时快照间隔分钟（每游戏独立开关，使用时 clamp 5–120）
    /// <summary>全局热备份快捷键，格式 "Ctrl+Shift+B"；空字符串=禁用。</summary>
    public string BackupHotKey { get; set; } = "Ctrl+Shift+B";

    // ── 云同步（WebDAV：坚果云 / Nextcloud / 群晖 / AList 等）──
    public bool CloudEnabled { get; set; } = false;        // 开启后新备份自动上传
    public string CloudUrl { get; set; } = "https://dav.jianguoyun.com/dav/";  // WebDAV 根地址
    public string CloudUser { get; set; } = "";           // 账号
    public string CloudSecret { get; set; } = "";         // 应用密码（DPAPI 加密后的 base64）
    public string CloudDir { get; set; } = "GameSaveVault";  // 云端根目录名（自动创建）
    /// <summary>一次性迁移：旧版全局开关 CloudEnabled 的用户，其全部游戏继承为按游戏自动云存档。</summary>
    public bool CloudPerGameMigrated { get; set; } = false;

    /// <summary>设置结构版本（当前 1）。新版每次保存都会写入；
    /// 旧版文件无此字段——配合默认壳指纹识别"读到了/被写入了出厂默认值"的事故文件。</summary>
    public int SettingsSchemaVersion { get; set; } = CurrentSchemaVersion;

    public const int CurrentSchemaVersion = 1;

    /// <summary>前向/后向兼容：保留本版本不认识的字段，新旧版本互写不丢配置。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>设置加载结果的可信程度。</summary>
public enum SettingsLoadConfidence
{
    /// <summary>正常读到磁盘文件并反序列化成功。</summary>
    Loaded,
    /// <summary>原文件丢失/损坏/为默认壳，已从内容丰富的快照恢复。</summary>
    Restored,
    /// <summary>没拿到任何可信配置，内存只是出厂默认值。</summary>
    Defaulted,
}

/// <summary>设置从快照恢复的具体原因（仅 Restored 时有意义），用于启动提示给出准确说法。</summary>
public enum SettingsRestoreCause
{
    /// <summary>本次启动未走快照恢复。</summary>
    None,
    /// <summary>磁盘文件是出厂默认壳（疑似被默认值覆写事故），已回滚到富快照。</summary>
    DefaultShellRollback,
    /// <summary>原文件存在但读取/解析失败（占用、损坏）。</summary>
    Corrupt,
    /// <summary>原文件丢失。</summary>
    Missing,
}

public static class Store
{
    /// <summary>测试可重定向数据目录（null = 正常 %APPDATA%）。</summary>
    public static string? HomeDirOverride;

    /// <summary>数据目录的环境变量入口（未设置时无任何影响；仅供便携/隔离演练使用，
    /// 注意 SpecialFolder.ApplicationData 走 SHGetKnownFolderPath 不吃 APPDATA 重写）。</summary>
    private const string HomeEnvVar = "GSM_VAULT_DIR";

    public static string BaseDir =>
        HomeDirOverride
        ?? (Environment.GetEnvironmentVariable(HomeEnvVar) is { Length: > 0 } envHome
            ? Path.GetFullPath(envHome)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameSaveVault"));

    public static string GamesFile => Path.Combine(BaseDir, "games.json");
    public static string SettingsFile => Path.Combine(BaseDir, "settings.json");
    /// <summary>云同步上传登记表（cloud-state.json）：记录每个本地 zip 是否已上传、对应远端路径。</summary>
    public static string CloudStateFile => Path.Combine(BaseDir, "cloud-state.json");
    public static string IconsDir => Path.Combine(BaseDir, "cache", "icons");
    public static string CoversDir => Path.Combine(BaseDir, "cache", "covers");
    /// <summary>完结馆竖版海报缓存目录（Steam library_600x900.jpg）。</summary>
    public static string PostersDir => Path.Combine(BaseDir, "cache", "posters");
    /// <summary>备份根目录：设置里可自定义（空 = 数据目录\backups）。设置加载前访问会回退默认值。</summary>
    public static string BackupRoot =>
        string.IsNullOrWhiteSpace(Settings.BackupDir)
            ? Path.Combine(BaseDir, "backups")
            : Settings.BackupDir;

    public static ObservableCollection<GameEntry> Games { get; private set; } = new();
    public static AppSettings Settings { get; private set; } = new();

    /// <summary>本次启动 games.json 解析失败时，损坏文件被隔离后的文件名（仅文件名，位于 BaseDir）。</summary>
    public static string? CorruptGamesFile { get; private set; }
    /// <summary>本次启动 settings.json 解析失败、已回退默认设置。</summary>
    public static bool CorruptSettings { get; private set; }
    /// <summary>自定义备份目录不可用（盘已拔/无权限），本次已回退到默认备份根。</summary>
    public static bool BackupRootFallback { get; private set; }
    /// <summary>本次启动从 _appdata 自动快照恢复成功的 games 快照文件名（仅展示用）。</summary>
    public static string? RestoredGamesSnapshot { get; private set; }
    /// <summary>本次启动从 _appdata 自动快照恢复成功的 settings 快照文件名（仅展示用）。</summary>
    public static string? RestoredSettingsSnapshot { get; private set; }
    /// <summary>本次启动设置加载的可信程度：正常加载 / 从快照恢复 / 仅拿到出厂默认值。
    /// Defaulted 时一切"启动即落盘"的动作都必须防备把用户真实配置覆盖成默认值。</summary>
    public static SettingsLoadConfidence SettingsConfidence { get; private set; } = SettingsLoadConfidence.Defaulted;
    /// <summary>本次启动从快照恢复设置的具体原因（None = 未恢复）。</summary>
    public static SettingsRestoreCause SettingsRestoreCause { get; private set; } = SettingsRestoreCause.None;
    /// <summary>本次启动是否处于"设置加载不可信"的危险会话：内存只有出厂默认值，而磁盘现存富配置
    /// （或磁盘现状根本读不出）。此时任何 SaveSettings 都必须拒绝，设置窗口也应阻止用户保存并要求重启。
    /// 注意：正常 Loaded 后用户主动把各项改回默认值不属此列，保存不得被误伤。</summary>
    internal static bool IsUnsafeDefaultSession
    {
        get
        {
            if (SettingsConfidence != SettingsLoadConfidence.Defaulted) return false;
            if (!File.Exists(SettingsFile)) return false;   // 首启/真空库：允许创建设置文件
            try
            {
                return !IsDefaultShell(
                    JsonSerializer.Deserialize<AppSettings>(ReadAllTextWithRetry(SettingsFile)));
            }
            catch
            {
                return true;   // 磁盘现状读不出时按最保守策略：宁可不存，不可覆盖
            }
        }
    }
    /// <summary>本次启动时 games.json 不存在（快照也没能恢复 → 真空库；用于提示"可从备份目录重建"）。</summary>
    public static bool GamesFileAbsent { get; private set; }

    /// <summary>程序自身数据快照目录（位于备份根内）：对 games.json/settings.json/rules.json 自动留底。</summary>
    public const string AppSnapshotDir = "_appdata";
    /// <summary>每种数据文件的固定副本槽数（{prefix}.1.json / {prefix}.2.json 交替原地覆写；
    /// 另一槽始终保留上一份完好副本，抗覆写中途中断）。</summary>
    private const int SnapshotSlots = 2;
    /// <summary>同一数据文件两次留底最小间隔：频繁 Save 时不覆写副本。</summary>
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Init()
    {
        CorruptGamesFile = null;
        CorruptSettings = false;
        BackupRootFallback = false;
        RestoredGamesSnapshot = null;
        RestoredSettingsSnapshot = null;
        SettingsConfidence = SettingsLoadConfidence.Defaulted;
        SettingsRestoreCause = SettingsRestoreCause.None;
        GamesFileAbsent = false;
        Directory.CreateDirectory(BaseDir);
        Directory.CreateDirectory(IconsDir);
        Directory.CreateDirectory(CoversDir);
        LoadSettings();         // 先读设置（BackupRoot 可能被自定义；损坏时也先尝试快照恢复）
        try
        {
            Directory.CreateDirectory(BackupRoot);
        }
        catch
        {
            // 自定义备份根位于已拔除的盘/无权限目录时不能让程序启动失败：
            // 本次回退默认目录，保证备份功能可用，设置页可再改回有效位置。
            if (!string.IsNullOrWhiteSpace(Settings.BackupDir))
            {
                BackupRootFallback = true;
                Settings.BackupDir = "";
                Directory.CreateDirectory(BackupRoot);
            }
            else throw;
        }
        Load();                 // 备份根已确定：games 损坏/丢失可从 _appdata 快照恢复
        // 启动留底（节流）：即使本次没有任何 Save，外部版本写入的数据也进快照
        SnapshotAppData("games.json");
        SnapshotAppData("settings.json");
        SnapshotAppData("rules.json");
        PruneStaleSavePaths();   // 清洗旧版误报存档路径（game.exe 通用名误扫等），有移除才落盘
    }

    /// <summary>启动清洗：移除旧版模糊匹配误报的存档路径（含本软件自身目录），有移除才落盘。</summary>
    private static void PruneStaleSavePaths()
    {
        try
        {
            int removed = Games.Sum(g => SaveLocator.PruneStalePaths(g));
            if (removed > 0) Save();
        }
        catch { /* 清洗失败不影响启动 */ }
    }

    public static void Load()
    {
        Games = new ObservableCollection<GameEntry>();
        if (!File.Exists(GamesFile))
        {
            // 索引被删/数据目录重建：首次使用无快照时正常空库；有快照则自动恢复
            GamesFileAbsent = true;
            TryRestoreGames();
            return;
        }
        try
        {
            var list = JsonSerializer.Deserialize<List<GameEntry>>(File.ReadAllText(GamesFile), JsonOpts);
            if (list != null)
                foreach (var g in list) Games.Add(g);
        }
        catch
        {
            // 索引损坏（磁盘错误/同步盘冲突/手改坏）：绝不能让一次 Save 把坏文件原地覆盖。
            // 先隔离改名保留现场；再尝试从 _appdata 自动快照恢复，恢复不了才以空库启动。
            try
            {
                string quarantined = $"games.corrupt.{DateTime.Now:yyyyMMdd-HHmmssfff}.json";
                File.Move(GamesFile, Path.Combine(BaseDir, quarantined), overwrite: true);
                CorruptGamesFile = quarantined;
            }
            catch { /* 隔离也失败时保持空库内存态，不阻断启动 */ }
            TryRestoreGames();
        }
    }

    /// <summary>从 _appdata 快照恢复游戏库（快照必须是合法 JSON 数组才采用）。</summary>
    private static void TryRestoreGames()
    {
        try
        {
            string? snap = PickSnapshot("games.json", JsonValueKind.Array);
            if (snap == null) return;
            File.Copy(snap, GamesFile, overwrite: true);
            var list = JsonSerializer.Deserialize<List<GameEntry>>(File.ReadAllText(GamesFile), JsonOpts);
            if (list == null) { File.Delete(GamesFile); return; }
            foreach (var g in list) Games.Add(g);
            RestoredGamesSnapshot = Path.GetFileName(snap);
        }
        catch
        {
            try { if (File.Exists(GamesFile) && CorruptGamesFile == null) File.Delete(GamesFile); } catch { }
        }
    }

    /// <summary>备份目录在运行中被重新指定后（如 AppData 被删、备份在自定义盘），空库可从新备份根的
    /// _appdata 快照补恢复：直接并入当前 Games 集合（不替换实例以保持 UI 绑定），返回恢复条目数。
    /// 库非空时绝不工作，避免覆盖已有数据。</summary>
    public static int TryRestoreEmptyLibrary()
    {
        if (Games.Count > 0) return 0;
        try
        {
            string? snap = PickSnapshot("games.json", JsonValueKind.Array);
            if (snap == null) return 0;
            var list = JsonSerializer.Deserialize<List<GameEntry>>(File.ReadAllText(snap), JsonOpts);
            if (list == null || list.Count == 0) return 0;
            foreach (var g in list) Games.Add(g);
            RestoredGamesSnapshot = Path.GetFileName(snap);
            Save();
            return list.Count;
        }
        catch { return 0; }
    }

    /// <summary>存盘失败时触发（参数为可读原因）。UI 可订阅提示用户"记录未保存"，
    /// 避免界面显示成功、重启后记录消失。</summary>
    public static event Action<string>? SaveFailed;

    /// <summary>原子写：先写临时文件再替换，避免崩溃/并发读到半写的 JSON。
    /// 临时名带 GUID：后台备份与 UI 保存并发时不会共用同一个 .tmp 互相截断。</summary>
    public static void AtomicWrite(string path, string content)
    {
        string tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, content);
        try
        {
            // 高并发保存时，目标文件可能被杀软实时扫描短暂占用（对方句柄未共享删除），
            // Move 覆盖会偶发 UnauthorizedAccessException：短退避重试数次即可。
            for (int i = 0; ; i++)
            {
                try { File.Move(tmp, path, overwrite: true); return; }
                catch (UnauthorizedAccessException) when (i < 4)
                {
                    Thread.Sleep(50 * (i + 1));
                }
            }
        }
        finally
        {
            // 重试耗尽抛出时清掉临时文件，避免目录残留 .tmp
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static readonly object SaveLock = new();

    public static void Save()
    {
        // 备份/玩家操作在后台线程、界面操作在 UI 线程，都可能调 Save：
        // 锁内做快照序列化，避免枚举 Games 时被另一线程增删而丢保存。
        lock (SaveLock)
        {
            try
            {
                string json = JsonSerializer.Serialize(Games.ToList(), JsonOpts);
                AtomicWrite(GamesFile, json);
                SnapshotAppData("games.json");
            }
            catch (Exception ex)
            {
                // 静默吞掉会让用户以为备份记录已保存（重启后丢失）：写崩溃日志 + 通知 UI 明示
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsm-crash.txt"),
                    $"[{DateTime.Now:HH:mm:ss}] Store.Save 失败：{ex}\n"); } catch { }
                try
                {
                    SaveFailed?.Invoke($"备份记录写入失败（{ex.GetType().Name}），重启后本次改动可能丢失，请检查磁盘空间/权限后重新操作一次。");
                }
                catch { /* 通知本身不能再抛 */ }
            }
        }
    }

    public static void LoadSettings()
    {
        Settings = new AppSettings();
        SettingsConfidence = SettingsLoadConfidence.Defaulted;
        if (!File.Exists(SettingsFile))
        {
            TryRestoreSettings(SettingsRestoreCause.Missing);   // 首次使用无快照→默认；数据文件被删且有快照→恢复
            return;
        }
        string? raw;
        try
        {
            raw = ReadAllTextWithRetry(SettingsFile);
        }
        catch (Exception ex)
        {
            // 文件存在却读不出（杀软实时扫描占用、强杀后句柄未释放、虚拟化层瞬时空洞）：
            // 绝不能带着一份 new AppSettings() 继续往下走让启动迁移落盘——先试快照恢复。
            LogSettingsIo($"settings.json 读取失败，尝试快照恢复：{ex.GetType().Name}: {ex.Message}");
            CorruptSettings = true;
            TryRestoreSettings(SettingsRestoreCause.Corrupt);
            return;
        }
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(raw);
            if (s != null)
            {
                s.MaxKeepPerGame = Math.Clamp(s.MaxKeepPerGame, 1, 100);
                s.FullEveryN = Math.Clamp(s.FullEveryN, 1, 50);
                // 毒默认壳识别：文件是出厂默认值且不是新版自己写出的（无 schema 版本标记），
                // 同时存在内容丰富的历史快照——说明这是一次"默认值覆写"事故，立即回滚到富快照。
                if (IsDefaultShell(s)
                    && !raw.Contains("SettingsSchemaVersion", StringComparison.Ordinal)
                    && PickSnapshot("settings.json", JsonValueKind.Object, IsRichSettingsSnapshot) is { } rich)
                {
                    if (TryAdoptSettingsSnapshot(rich, out var rescued))
                    {
                        Settings = rescued;
                        SettingsConfidence = SettingsLoadConfidence.Restored;
                        SettingsRestoreCause = SettingsRestoreCause.DefaultShellRollback;
                        RestoredSettingsSnapshot = Path.GetFileName(rich);
                        LogSettingsIo($"settings.json 为默认壳，已自动回滚到富快照 {RestoredSettingsSnapshot}");
                        return;
                    }
                }
                Settings = s;
                SettingsConfidence = SettingsLoadConfidence.Loaded;
            }
        }
        catch
        {
            // 损坏则先尝试从 _appdata 快照恢复；恢复不了才用默认值。坏文件一律改名留档。
            CorruptSettings = true;
            try
            {
                File.Move(SettingsFile,
                    Path.Combine(BaseDir, $"settings.corrupt.{DateTime.Now:yyyyMMdd-HHmmssfff}.json"),
                    overwrite: true);
            }
            catch { }
            TryRestoreSettings(SettingsRestoreCause.Corrupt);
        }
    }

    /// <summary>读文本文件：遇到 IO 占用（杀软扫描/句柄释放窗口）时短退避重试，不把瞬时故障当损坏。</summary>
    private static string ReadAllTextWithRetry(string path, int attempts = 4)
    {
        for (int i = 0; ; i++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (i < attempts - 1) { Thread.Sleep(100 * (i + 1)); }
        }
    }

    /// <summary>判断一份设置是否为"出厂默认壳"：所有用户可配置项都保持默认值。
    /// 用来区分"用户真的没配置过"和"加载失败后 new 出来的默认值事故"。</summary>
    internal static bool IsDefaultShell(AppSettings? s)
    {
        if (s is null) return true;
        return string.IsNullOrEmpty(s.CloudUser)
            && string.IsNullOrEmpty(s.CloudSecret)
            && !s.CloudEnabled
            && s.BackupHotKey == "Ctrl+Shift+B"
            && s.DefaultPlayerName == "玩家1"
            && string.IsNullOrEmpty(s.BackupDir)
            && s.Theme == "system"
            && (s.CustomNameSources is null || s.CustomNameSources.Count == 0)
            && (s.CustomAppIdSources is null || s.CustomAppIdSources.Count == 0);
    }

    /// <summary>快照内容校验：settings 快照除了 JSON 结构合法，还必须不是默认壳（毒快照不得作为恢复候选）。</summary>
    private static bool IsRichSettingsSnapshot(string text)
    {
        try { return !IsDefaultShell(JsonSerializer.Deserialize<AppSettings>(text)); }
        catch { return false; }
    }

    /// <summary>从 _appdata 快照恢复设置（优先内容丰富的快照；恢复后执行取值钳制）。
    /// 任何失败都保留现场、不再删除 settings.json，给下次启动留重试机会。</summary>
    private static void TryRestoreSettings(SettingsRestoreCause cause)
    {
        try
        {
            // 首选"富快照"（含云账号/自定义快捷键等非默认值）；全是默认壳时才退而求其次取任意合法快照
            string? snap = PickSnapshot("settings.json", JsonValueKind.Object, IsRichSettingsSnapshot)
                          ?? PickSnapshot("settings.json", JsonValueKind.Object);
            if (snap == null) return;
            if (!TryAdoptSettingsSnapshot(snap, out var s)) return;
            Settings = s;
            bool rich = !IsDefaultShell(s);
            SettingsConfidence = rich ? SettingsLoadConfidence.Restored
                                      : SettingsLoadConfidence.Defaulted;
            // 只恢复到一份默认壳时不弹"已恢复"（没有真实配置被找回），原因也不展示
            if (rich) SettingsRestoreCause = cause;
            RestoredSettingsSnapshot = Path.GetFileName(snap);
        }
        catch (Exception ex)
        {
            LogSettingsIo($"从快照恢复 settings.json 失败（现场已保留）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>把指定快照复制为正式 settings.json 并解析钳制；失败返回 false，不删除任何文件。</summary>
    private static bool TryAdoptSettingsSnapshot(string snap, out AppSettings result)
    {
        result = new AppSettings();
        try
        {
            File.Copy(snap, SettingsFile, overwrite: true);
            var s = JsonSerializer.Deserialize<AppSettings>(ReadAllTextWithRetry(SettingsFile));
            if (s is null) return false;
            s.MaxKeepPerGame = Math.Clamp(s.MaxKeepPerGame, 1, 100);
            s.FullEveryN = Math.Clamp(s.FullEveryN, 1, 50);
            result = s;
            return true;
        }
        catch (Exception ex)
        {
            LogSettingsIo($"采用快照 {Path.GetFileName(snap)} 失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void LogSettingsIo(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsm-crash.txt"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { }
    }

    public static void SaveSettings()
    {
        // 与 Save() 共用同一把锁：UI 线程保存设置与后台线程保存库存并发时，
        // 原子写/快照不留交错窗口（两类文件互不相同，串行化代价可忽略）。
        lock (SaveLock)
        {
            try
            {
                string json = JsonSerializer.Serialize(Settings, JsonOpts);
                // 防呆：危险会话（本次启动没拿到可信配置 + 磁盘是富配置/现状不可读）时拒绝一切覆写，
                // 与内存内容是不是默认壳无关——异常会话里任何代码路径的保存都不能覆盖磁盘上的真实配置。
                // 正常 Loaded 会话中用户主动清空/改回默认值不受此限制。
                if (IsUnsafeDefaultSession)
                {
                    string guard = Path.Combine(BaseDir,
                        $"settings.guard.{DateTime.Now:yyyyMMdd-HHmmssfff}.json");
                    try { AtomicWrite(guard, json); } catch { }
                    LogSettingsIo($"危险会话中拒绝保存设置，本次内容隔离到 {Path.GetFileName(guard)}");
                    try
                    {
                        SaveFailed?.Invoke("检测到本次启动设置加载异常，已阻止本次保存，请重启程序后再修改设置。");
                    }
                    catch { /* 通知本身不能再抛 */ }
                    return;
                }
                AtomicWrite(SettingsFile, json);
                SnapshotAppData("settings.json");
            }
            catch (Exception ex)
            {
                LogSettingsIo($"SaveSettings 失败：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // ── 程序自身数据快照（_appdata）─────────────────────────

    private static string SnapshotDirPath => Path.Combine(BackupRoot, AppSnapshotDir);

    /// <summary>把数据文件原地留底到 _appdata 双槽固定副本（{prefix}.1.json / {prefix}.2.json）。
    /// 全程只"原地覆写"固定文件：不新建时间戳文件、不删除旧文件——避免安全软件的文件防护
    /// （如 360 删除防护/恢复区）把删除或替换动作截留进回收站，导致条目持续堆积。
    /// 两槽交替覆写，另一槽恒为上一份完好副本；存量历史时间戳快照不删不增，仅作更旧兜底。
    /// 任何 IO 异常都吞掉：自我备份绝不能影响正常流程。force=true 跳过节流（测试用）。</summary>
    public static void SnapshotAppData(string fileName, bool force = false)
    {
        try
        {
            string src = Path.Combine(BaseDir, fileName);
            if (!File.Exists(src)) return;
            Directory.CreateDirectory(SnapshotDirPath);
            string prefix = Path.GetFileNameWithoutExtension(fileName);

            if (!force && LatestSnapshotTime(prefix) is { } latest && DateTime.Now - latest < SnapshotInterval)
                return;

            // 缺槽优先建槽；两槽都在则覆写 mtime 较旧者（较新槽保留上一份完好副本）
            string slot = PickOverwriteSlot(prefix);
            OverwriteInPlace(slot, File.ReadAllText(src));

            // 读回校验：覆写中途断电/磁盘错误会留下半截 JSON。坏槽不删除（删除同样会被文件防护
            // 截留进回收站），PickSnapshot 恢复时自动跳过；下一轮先覆写另一槽、再下一轮翻新本槽。
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(slot));
            }
            catch (JsonException) { /* 本槽作废，另一槽完好 */ }
        }
        catch { /* 自我备份失败静默 */ }
    }

    /// <summary>双槽固定副本路径：{prefix}.1.json / {prefix}.2.json。</summary>
    private static string SlotPath(string prefix, int slot) =>
        Path.Combine(SnapshotDirPath, $"{prefix}.{slot}.json");

    /// <summary>选本次覆写的槽位：缺失的槽优先（先把两槽建满），都在则取 mtime 较旧者。</summary>
    private static string PickOverwriteSlot(string prefix)
    {
        string s1 = SlotPath(prefix, 1);
        string s2 = SlotPath(prefix, 2);
        if (!File.Exists(s1)) return s1;
        if (!File.Exists(s2)) return s2;
        return File.GetLastWriteTime(s1) <= File.GetLastWriteTime(s2) ? s1 : s2;
    }

    /// <summary>全部副本（双槽 + 存量历史时间戳快照）中最新的留底时刻；节流判定用。
    /// 双槽看 LastWriteTime（原地覆写时刻即留底时刻），历史快照沿用文件名里的时间戳。</summary>
    private static DateTime? LatestSnapshotTime(string prefix)
    {
        try
        {
            if (!Directory.Exists(SnapshotDirPath)) return null;
            DateTime? latest = null;
            for (int i = 1; i <= SnapshotSlots; i++)
            {
                string f = SlotPath(prefix, i);
                if (File.Exists(f))
                {
                    var t = File.GetLastWriteTime(f);
                    if (latest == null || t > latest) latest = t;
                }
            }
            foreach (var f in Directory.EnumerateFiles(SnapshotDirPath, prefix + "_*.json"))
            {
                if (TryGetSnapshotTime(f, prefix, out var t) && (latest == null || t > latest))
                    latest = t;
            }
            return latest;
        }
        catch { return null; }
    }

    /// <summary>原地截断覆写固定文件（FileMode.Create + 强刷落盘）：
    /// 不经过 File.Delete / File.Move 替换，文件防护类驱动不会产生回收站条目。</summary>
    private static void OverwriteInPlace(string path, string content)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(flushToDisk: true);
    }

    /// <summary>从快照文件名解析创建时刻（{prefix}_yyyyMMdd_HHmmssfff，可带 _seq 后缀）。
    /// 不依赖文件 LastWriteTime（File.Copy 会继承源文件时间）。</summary>
    private static bool TryGetSnapshotTime(string filePath, string prefix, out DateTime time)
    {
        time = default;
        try
        {
            string name = Path.GetFileNameWithoutExtension(filePath);
            if (name.Length < prefix.Length + 1 + 18 || !name.StartsWith(prefix + "_", StringComparison.Ordinal))
                return false;
            return DateTime.TryParseExact(name.Substring(prefix.Length + 1, 18),
                "yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
        }
        catch { return false; }
    }

    /// <summary>从新到旧挑第一个 JSON 结构合法（期望数组/对象）且通过额外内容校验的副本，
    /// 返回完整路径；没有返回 null。候选顺序：双槽固定副本（mtime 新→旧）→ 存量历史时间戳
    /// 快照（文件名新→旧）。半截损坏的槽在此处被 JSON 解析自然跳过。
    /// contentCheck 可用于排除"合法但内容是默认壳"的毒快照。</summary>
    private static string? PickSnapshot(string fileName, JsonValueKind expectedKind,
        Func<string, bool>? contentCheck = null)
    {
        try
        {
            string dir = SnapshotDirPath;
            if (!Directory.Exists(dir)) return null;
            string prefix = Path.GetFileNameWithoutExtension(fileName);

            var slots = new List<string>();
            for (int i = 1; i <= SnapshotSlots; i++)
            {
                string f = SlotPath(prefix, i);
                if (File.Exists(f)) slots.Add(f);
            }
            foreach (var f in slots.OrderByDescending(File.GetLastWriteTime))
            {
                if (IsValidSnapshot(f)) return f;
            }
            // 历史时间戳快照不再新增也不删除，作为更旧兜底
            foreach (var f in Directory.EnumerateFiles(dir, prefix + "_*.json")
                         .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (IsValidSnapshot(f)) return f;
            }
        }
        catch { }
        return null;

        bool IsValidSnapshot(string f)
        {
            try
            {
                string text = File.ReadAllText(f);
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.ValueKind == expectedKind
                    && (contentCheck is null || contentCheck(text));
            }
            catch { return false; /* 该副本损坏，继续找更早的 */ }
        }
    }

    public static string IconFileFor(string id) => Path.Combine(IconsDir, id + ".png");
    public static string CoverFileFor(int appId) => Path.Combine(CoversDir, appId + ".jpg");
    /// <summary>完结馆竖版海报本地缓存路径（按 AppId；不存在时由 SteamApi.DownloadPoster 拉取）。</summary>
    public static string PosterFileFor(int appId) => Path.Combine(PostersDir, appId + ".jpg");
    /// <summary>用户手动换的完结馆海报（按游戏 Id；与官方 {appId}.jpg 区分，便于「恢复官方」）。</summary>
    public static string PosterCustomFileFor(string gameId) => Path.Combine(PostersDir, "custom_" + gameId + ".jpg");
    /// <summary>删游戏时为手换海报保留的按 AppId 存档：重新添加同一游戏时自动认领，无需联网重下。</summary>
    public static string PosterCustomAppFileFor(int appId) => Path.Combine(PostersDir, "custom_app_" + appId + ".jpg");

    /// <summary>本地图片缓存是否为有效图（与下载器口径一致：>1KB，防 0 字节/错误页占位）。</summary>
    public static bool IsUsableImage(string? file)
    {
        try { return !string.IsNullOrEmpty(file) && File.Exists(file) && new FileInfo(file).Length > 1000; }
        catch { return false; }
    }

    /// <summary>缓存图片（横封/竖海报）是否被「除指定条目外」的其他条目引用（含归档留档条目）。
    /// 改 AppId 换封面删旧 {oldAppId}.jpg 前必须先问：双装/多条目共用同一 AppId 缓存图时，
    /// 删了会让另一条目封面变空白。</summary>
    public static bool IsImageReferencedByOthers(string? file, string exceptGameId)
    {
        if (string.IsNullOrEmpty(file)) return false;
        return Games.Any(g => !string.Equals(g.Id, exceptGameId, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(g.CoverFile, file, StringComparison.OrdinalIgnoreCase)
                || string.Equals(g.PosterFile, file, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// 按 AppId 认领磁盘上已有的横封/竖海报/手换海报（纯本地、零网络）。
    /// 场景：删游戏后重新添加、空库重建、扫描重复入库——条目是新建的、字段为空，
    /// 但 cache 里的老图还在；直接指认，避免慢网/断网下重复下载或封面空白。
    /// 优先级：手换海报保留档(custom_app_) ＞ 官方竖海报 ＞ 横封面。返回是否改动了条目。
    /// </summary>
    public static bool AdoptCachedArtwork(GameEntry g)
    {
        if (g.AppId <= 0) return false;
        bool changed = false;

        if (!IsUsableImage(g.CoverFile))
        {
            string cover = CoverFileFor(g.AppId);
            if (IsUsableImage(cover)) { g.CoverFile = cover; changed = true; }
        }

        // 用户已手换（字段指向 custom_ 且文件在）就不碰；否则认领 custom_app 保留档，再认领官方竖图
        bool ownCustom = !string.IsNullOrEmpty(g.PosterFile)
            && Path.GetFileName(g.PosterFile).StartsWith("custom_", StringComparison.OrdinalIgnoreCase)
            && IsUsableImage(g.PosterFile);
        if (!ownCustom && !IsUsableImage(g.PosterFile))
        {
            string customKeep = PosterCustomAppFileFor(g.AppId);
            string official = PosterFileFor(g.AppId);
            if (IsUsableImage(customKeep)) { g.PosterFile = customKeep; changed = true; }
            else if (IsUsableImage(official)) { g.PosterFile = official; changed = true; }
        }

        if (changed) g.Refresh();
        return changed;
    }

    public static string TimeAgo(DateTime t)
    {
        var span = DateTime.Now - t;
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时前";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays} 天前";
        return t.ToString("yyyy-MM-dd");
    }

    /// <summary>
    /// 游戏卸载后换目录重装、被重复入库时合并两条记录：保留 <paramref name="keep"/>
    /// （通常是较早入库、挂着历史备份/收藏/设置的旧条目），把 <paramref name="drop"/>
    /// （新扫到、exe 路径有效的新条目）的安装位置、存档路径、备份、图标、玩家镜像并入。
    /// 调用方负责把 drop 从 <see cref="Games"/> 移除。返回并入的存档路径数。
    /// </summary>
    public static int MergeGame(GameEntry keep, GameEntry drop, bool adoptExe = true)
    {
        int pathsAdded = 0;

        // 1) exe / 安装目录：仅当旧条目位置已失效而新位置有效时才采用（两份都在=真·双装，不动）
        if (adoptExe && !string.IsNullOrWhiteSpace(drop.ExePath) && File.Exists(drop.ExePath)
            && (string.IsNullOrWhiteSpace(keep.ExePath) || !File.Exists(keep.ExePath)))
        {
            keep.ExePath = drop.ExePath;
            if (!string.IsNullOrWhiteSpace(drop.InstallDir)) keep.InstallDir = drop.InstallDir;
        }
        else if (string.IsNullOrWhiteSpace(keep.InstallDir) && !string.IsNullOrWhiteSpace(drop.InstallDir))
        {
            keep.InstallDir = drop.InstallDir;
        }

        // 2) 存档路径合并（TryAddAutoSavePath 尊重旧条目的手动移除名单）
        foreach (var p in drop.SavePaths.ToList())
            if (keep.TryAddAutoSavePath(p)) pathsAdded++;

        // 3) 备份记录按 zip 全路径并集（新条目可能已抢先认领到部分旧备份）
        var have = keep.Backups.Select(b => b.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var b in drop.Backups.ToList())
            if (!string.IsNullOrWhiteSpace(b.File) && have.Add(b.File))
                keep.Backups.Add(b);

        // 4) 最近游玩时间取较新
        if (drop.LastPlayed > keep.LastPlayed) keep.LastPlayed = drop.LastPlayed;

        // 4b) 游玩统计（关闭时间/会话去重合并/总时长按合并后会话重算）
        PlayTracker.MergeStats(keep, drop);

        // 5) 当前玩家：旧条目未启用而新条目已选定时继承
        if (string.IsNullOrWhiteSpace(keep.ActiveProfile) && !string.IsNullOrWhiteSpace(drop.ActiveProfile))
            keep.ActiveProfile = drop.ActiveProfile;

        // 5b) 玩家卡槽名单合并（drop 的玩家并入 keep，保序去重；旧数据名单缺失时从已合并备份补登）
        foreach (var n in drop.Players ?? new List<string>())
            ProfileService.RegisterSlot(keep, n);
        foreach (var b in keep.Backups)
            ProfileService.RegisterSlot(keep, b.Profile);

        // 6) 图标：旧条目无图标而新条目有时复制到旧条目 Id 下
        if (string.IsNullOrWhiteSpace(keep.IconFile) && !string.IsNullOrWhiteSpace(drop.IconFile)
            && File.Exists(drop.IconFile))
        {
            try
            {
                string dst = IconFileFor(keep.Id);
                File.Copy(drop.IconFile, dst, overwrite: true);
                keep.IconFile = dst;
            }
            catch { /* 图标复制失败不影响合并 */ }
        }

        // 7) 玩家镜像目录 profiles\{dropId} → profiles\{keepId}
        try { ProfileService.MergeRoots(keep, drop); } catch { }

        // 8) 完结档案：keep 未完结而 drop 已完结（归档通关卡撞上同游戏新条目）→ 继承通关日期与海报
        if (keep.CompletedAt == DateTime.MinValue && drop.CompletedAt != DateTime.MinValue)
        {
            keep.CompletedAt = drop.CompletedAt;
            if (!string.IsNullOrEmpty(drop.PosterFile) && File.Exists(drop.PosterFile))
            {
                try
                {
                    string dst = Path.GetFileName(drop.PosterFile).StartsWith("custom_", StringComparison.OrdinalIgnoreCase)
                        ? PosterCustomFileFor(keep.Id)
                        : PosterFileFor(keep.AppId);
                    if (!string.Equals(dst, drop.PosterFile, StringComparison.OrdinalIgnoreCase))
                        File.Copy(drop.PosterFile, dst, overwrite: true);
                    keep.PosterFile = dst;
                }
                catch { /* 海报复制失败：兜底链还能走封面/图标，不阻断合并 */ }
            }
        }

        keep.RefreshProfiles();
        keep.RefreshVisibleBackups();
        keep.Refresh();
        return pathsAdded;
    }
}
