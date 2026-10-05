using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Serialization;
using GameSaveManager.Services;

namespace GameSaveManager.Models;

public class GameEntry : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string NameCN { get; set; } = "";
    public string NameEN { get; set; } = "";
    public string Type { get; set; } = "Local";            // Steam | Local
    public int AppId { get; set; }                          // 0 = 无
    /// <summary>AppId 已人工/迁移钉死（如同名系列新作误配初代），联网回填不得覆盖。</summary>
    public bool AppIdLocked { get; set; }
    public string ExePath { get; set; } = "";
    public string InstallDir { get; set; } = "";
    public string IconFile { get; set; } = "";              // 本地图标 png 绝对路径
    public string CoverFile { get; set; } = "";             // 本地封面 jpg 绝对路径
    public DateTime LastPlayed { get; set; } = DateTime.MinValue;   // 最近「打开」时间（Steam manifest 在启动时写入；本应用启动时也记录），仅作回退/参考
    public DateTime AddedAt { get; set; } = DateTime.MinValue;      // 入库时间（旧数据无此字段，排序垫底）

    // ── 游玩统计（本应用进程监测，安装前的历史无法追溯）──────────────
    /// <summary>最后一次游戏进程关闭时刻（卡片时间/「最近游玩」排序的首选口径）。</summary>
    public DateTime LastClosedAt { get; set; } = DateTime.MinValue;
    /// <summary>累计游玩秒数（由会话记录累加，PlayTracker 统一维护）。</summary>
    public long PlaySeconds { get; set; }
    /// <summary>游玩会话明细（按开始时间先后；闭合后入库，最多保留 <see cref="PlayTracker.MaxSessions"/> 条）。</summary>
    public List<PlaySession> Sessions { get; set; } = new();

    private bool _isRunning;
    /// <summary>运行时状态：游戏进程当前存活（WatchTick 每 1 秒刷新，不持久化）。卡片显示「游玩中」。</summary>
    [JsonIgnore]
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(LastPlayedText));
            OnPropertyChanged(nameof(LastPlayedTip));
        }
    }

    private bool _autoBackup;
    /// <summary>自动备份开关（列表徽章/详情页开关绑定，切换需通知）。</summary>
    public bool AutoBackup
    {
        get => _autoBackup;
        set { _autoBackup = value; OnPropertyChanged(nameof(AutoBackup)); }
    }

    private bool _hotSnapshot;
    /// <summary>运行中定时快照开关（游戏运行期间每隔 N 分钟自动备份一次）。</summary>
    public bool HotSnapshot
    {
        get => _hotSnapshot;
        set { _hotSnapshot = value; OnPropertyChanged(nameof(HotSnapshot)); }
    }

    private bool _favorite;
    /// <summary>收藏置顶（列表排序用，切换后需刷新集合视图）。</summary>
    public bool Favorite
    {
        get => _favorite;
        set { _favorite = value; OnPropertyChanged(nameof(Favorite)); }
    }

    private bool _cloudAuto;
    /// <summary>自动云存档（按游戏）：新备份自动上传，并自动收集下载云端缺失备份；
    /// 下载只进备份列表，不覆盖 live。需全局云功能已配置。</summary>
    public bool CloudAuto
    {
        get => _cloudAuto;
        set { _cloudAuto = value; OnPropertyChanged(nameof(CloudAuto)); }
    }

    private bool _wikiFailed;
    /// <summary>运行时标记：最近一次 Wiki 存档规则补全因网络失败未拿到数据（详情页显示重试入口，
    /// 不持久化——重启后首次补全会重新给出结果）。</summary>
    public bool WikiFailed
    {
        get => _wikiFailed;
        set { if (_wikiFailed != value) { _wikiFailed = value; OnPropertyChanged(nameof(WikiFailed)); } }
    }

    /// <summary>最近一次还原/切换恢复到 live 的文件指纹（运行时，不持久化）：
    /// 零备份玩家被切走时判断她是否真玩过——live 与基线一致说明没玩，跳过打包。</summary>
    internal List<(string Rel, long Size, long Mtime)>? LiveBaseline { get; set; }

    public ObservableCollection<SavePathEntry> SavePaths { get; set; } = new();
    /// <summary>用户手动移除过的存档路径（规范化后持久化）。后台自动探测/Wiki 补全/去重合并
    /// 不得再把这些路径加回；只有用户手动「添加路径」或显式点「自动获取」才能解除。</summary>
    public List<string> IgnoredSavePaths { get; set; } = new();
    /// <summary>全部备份记录（含所有玩家）；详情页列表请用 VisibleBackups（按当前玩家过滤）。</summary>
    public ObservableCollection<BackupRecord> Backups { get; set; } = new();
    /// <summary>当前玩家可见的备份（详情页备份列表绑定它）。</summary>
    [JsonIgnore]
    public ObservableCollection<BackupRecord> VisibleBackups { get; set; } = new();

    // ── 玩家存档（同一游戏多人各玩各的，独立存档+独立备份）──
    public string ActiveProfile { get; set; } = "";          // 当前玩家名（"" = 未启用玩家，共用存档）
    /// <summary>玩家卡槽名单（持久化，创建顺序）。卡槽是独立实体：即使名下备份被全部删除，
    /// 玩家仍保留在列表里，只能通过右键卡槽「删除玩家」移除。旧数据启动时由 BackfillPlayers 回填。</summary>
    public List<string> Players { get; set; } = new();
    /// <summary>[已退役] 旧版还原标记（玩家→手动还原所到的存档时间），仅保留用于反序列化与一次性迁移；
    /// 启动迁移 BackfillFavorite 后清空，新逻辑一律以 SwitchAnchors（心目中的存档指针）+ FavoriteWhy 为准。</summary>
    public Dictionary<string, DateTime> RestoredMarks { get; set; } = new();

    /// <summary>心目中的存档指针：玩家名 → 她认可的那份备份的 FileJson（相对口径）。
    /// 由"认可动作"移动：手动还原→还原目标；手动备份（含热键/热备份）→新包；
    /// 退出游戏自动备份→新包（运行中热快照不移动）；切走玩家→锚定包；首位玩家建卡→初始包。
    /// 筹码 live 核对以指针包的存档时间为基准；切回她时还原指针包；Prune 自动淘汰跳过指针包。
    /// 指针包丢失/损坏时解析端回退她最新的可还原包。玩家改名/删除时同步维护。
    /// 历史命名 SwitchAnchors：属性/JSON 键名保持不变以兼容存量 games.json。</summary>
    public Dictionary<string, string> SwitchAnchors { get; set; } = new();

    /// <summary>指针移动原因，与 SwitchAnchors 同键：restore=手动还原；manual=手动备份（含热键/热备份）；
    /// auto=退出游戏自动备份；switch=切走/新建玩家时锚定；init=首位玩家初始承接；""=启动迁移兜底。
    /// 决定 live 与指针包一致时筹码的文案（已还原到 / 已备份·手动 / 已备份·自动）。</summary>
    public Dictionary<string, string> FavoriteWhy { get; set; } = new();
    public List<string> ProfilePaths { get; set; } = new();  // 旧版手动勾选目录（已废弃，保留仅为反序列化兼容；现全自动按存档位置识别）
    [JsonIgnore]
    public ObservableCollection<ProfileSlotVm> ProfileSlots { get; set; } = new();

    // ── 显示用计算属性 ──────────────────────────────
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(NameCN) ? NameEN : NameCN;
    public override string ToString() => DisplayName;
    [JsonIgnore]
    public string SubName => string.IsNullOrWhiteSpace(NameCN) ? "" : NameEN;
    [JsonIgnore]
    public string TypeBadge => Type == "Steam" ? "Steam" : "本地版";
    [JsonIgnore]
    public bool IsSteam => Type == "Steam";
    [JsonIgnore]
    public string Letter => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName.Substring(0, 1).ToUpper();
    [JsonIgnore]
    public bool HasIcon => !string.IsNullOrEmpty(IconFile) && File.Exists(IconFile);
    [JsonIgnore]
    public bool HasCover => !string.IsNullOrEmpty(CoverFile) && File.Exists(CoverFile);
    [JsonIgnore]
    public bool HasBackups => Backups.Count > 0;
    /// <summary>详情页备份区可见性：当前玩家有没有备份（未启用玩家时等同 HasBackups）。</summary>
    [JsonIgnore]
    public bool HasVisibleBackups => VisibleBackups.Count > 0;
    [JsonIgnore]
    public string BackupCountText => $"历史备份 {Backups.Count} 份";
    [JsonIgnore]
    public string ExeName => string.IsNullOrEmpty(ExePath) ? "" : Path.GetFileName(ExePath);
    [JsonIgnore]
    public string ProcessName => string.IsNullOrEmpty(ExePath) ? "" : Path.GetFileNameWithoutExtension(ExePath);

    [JsonIgnore]
    public string LastBackupText
    {
        get
        {
            var latest = Backups.Select(b => b.Time).DefaultIfEmpty(DateTime.MinValue).Max();
            return latest == DateTime.MinValue ? "未备份" : Store.TimeAgo(latest);
        }
    }

    /// <summary>排序/显示用的有效最近时间：最后关闭优先，无关闭记录时回退 Steam「打开」时间。</summary>
    [JsonIgnore]
    public DateTime LastActiveAt => LastClosedAt >= LastPlayed ? LastClosedAt : LastPlayed;

    /// <summary>卡片小字：运行中→「游玩中」；否则优先最后关闭时间，无记录回退 Steam 打开时间，全空→空串（隐藏）。</summary>
    [JsonIgnore]
    public string LastPlayedText
    {
        get
        {
            if (IsRunning) return "游玩中";
            if (LastClosedAt != DateTime.MinValue) return Store.TimeAgo(LastClosedAt);
            return LastPlayed == DateTime.MinValue ? "" : Store.TimeAgo(LastPlayed);
        }
    }

    /// <summary>卡片时间 ToolTip：标明口径（最后关闭 / Steam 最近打开）与绝对时刻。</summary>
    [JsonIgnore]
    public string LastPlayedTip
    {
        get
        {
            if (IsRunning) return "游戏运行中";
            if (LastClosedAt != DateTime.MinValue) return "最后关闭 " + LastClosedAt.ToString("yyyy-MM-dd HH:mm");
            return LastPlayed == DateTime.MinValue ? ""
                : "Steam 记录的启动时间 " + LastPlayed.ToString("yyyy-MM-dd HH:mm");
        }
    }

    /// <summary>游玩总时长（会话累计，不足 1 分钟显示「不足 1 分钟」，无记录「—」）。</summary>
    [JsonIgnore]
    public string TotalPlayText => PlayTracker.FormatDuration(PlaySeconds);
    /// <summary>入库日期文本（旧数据无 AddedAt → 未知）。</summary>
    [JsonIgnore]
    public string AddedAtText => AddedAt == DateTime.MinValue ? "未知（早期数据）" : AddedAt.ToString("yyyy-MM-dd");
    /// <summary>最后关闭时间绝对文本（无记录 → 未记录）。</summary>
    [JsonIgnore]
    public string LastClosedText => LastClosedAt == DateTime.MinValue ? "未记录" : LastClosedAt.ToString("yyyy-MM-dd HH:mm");
    /// <summary>会话次数文本。</summary>
    [JsonIgnore]
    public string SessionCountText => Sessions.Count == 0 ? "暂无游玩记录" : $"共 {Sessions.Count} 次会话";

    // ── 玩家槽位显示属性 ──
    [JsonIgnore]
    public bool HasCloudSave => SavePaths.Any(p => p.Source == "Steam云");
    [JsonIgnore]
    public string ActiveProfileText => string.IsNullOrEmpty(ActiveProfile) ? "未启用（共用存档）" : ActiveProfile;
    [JsonIgnore]
    public bool HasProfileSlots => ProfileSlots.Count > 0;
    /// <summary>当前玩家在界面上的名字：优先取激活筹码名；空 ActiveProfile 的虚拟玩家1 显示默认玩家名（玩家1）。</summary>
    [JsonIgnore]
    public string ActiveProfileDisplayName =>
        ProfileSlots.FirstOrDefault(v => v.IsActive)?.Name
        ?? (string.IsNullOrWhiteSpace(ActiveProfile) ? ProfileService.DefaultFirstPlayerName : ActiveProfile);
    /// <summary>备份区空列表提示文案：启用玩家后按玩家区分。</summary>
    [JsonIgnore]
    public string EmptyBackupHint =>
        HasProfileSlots
            ? $"玩家「{ActiveProfileDisplayName}」还没有备份，玩过之后点下方「立即备份」"
            : "暂无备份记录，点击下方「立即备份」创建第一份快照";
    /// <summary>备份区标题旁的归属提示。</summary>
    [JsonIgnore]
    public string BackupOwnerText =>
        HasProfileSlots ? $"玩家「{ActiveProfileDisplayName}」的备份 · {VisibleBackups.Count} 份" : $"{VisibleBackups.Count} 份备份";

    /// <summary>重新枚举玩家列表（备份记录 Profile 去重 + 当前激活玩家）。筹码按钮就地增删/更新激活态，
    /// 不整表重建——切换玩家时按钮位置保持不动，只有「当前」徽章跳转。</summary>
    public void RefreshProfiles()
    {
        var names = ProfileService.ListSlots(this);
        // 删除已不存在的玩家筹码
        foreach (var vm in ProfileSlots.Where(v => !names.Contains(v.Name)).ToList())
            ProfileSlots.Remove(vm);
        // 新增缺少的筹码（追加到末尾，与创建顺序一致）；已有筹码只更新激活态
        bool virtualDefaultActive = ProfileService.IsDefaultPlayerApplicable(this);
        foreach (var n in names)
        {
            var vm = ProfileSlots.FirstOrDefault(v => v.Name == n);
            if (vm == null)
            {
                vm = new ProfileSlotVm { Name = n };
                ProfileSlots.Add(vm);
            }
            // 虚拟玩家1（唯一卡槽、ActiveProfile 仍空）视为当前激活，显示「当前」徽章
            vm.IsActive = n == ActiveProfile
                || (virtualDefaultActive && n == ProfileService.DefaultFirstPlayerName
                    && string.IsNullOrEmpty(ActiveProfile));
            // 筹码第二行（零 IO 静态部分）：非当前玩家=最近备份时间；当前玩家=备份时间占位，
            // 其 live 真实状态（已备份/有新进度/游玩中）由 MainWindow 异步扫描后就地升级
            var mine = Backups.Where(b => (b.Profile ?? "") == n).ToList();
            var latest = mine.OrderByDescending(b => b.Time).FirstOrDefault();
            DateTime latestAt = latest is { SavedAt: var sa0 } && sa0 != DateTime.MinValue ? sa0
                : latest?.Time ?? DateTime.MinValue;
            string timeTxt = latestAt != DateTime.MinValue ? latestAt.ToString("MM-dd HH:mm") : "";
            string clickHint = "点击切换到该玩家；右键可重命名或删除";
            if (!vm.IsActive)
            {
                // 物理存档目录只属于当前玩家：其他筹码展示备份事实即可
                vm.SetStatus(timeTxt, "normal",
                    latestAt == DateTime.MinValue
                        ? $"玩家「{n}」还没有备份\n{clickHint}"
                        : $"最近备份 {timeTxt} · 共 {mine.Count} 份\n{clickHint}");
            }
            else if (latestAt == DateTime.MinValue)
            {
                vm.SetStatus("从未备份", "warn", "当前玩家还没有备份，玩过之后点下方「立即备份」");
            }
            else
            {
                vm.SetStatus(timeTxt, "normal", $"最近备份 {timeTxt} · 共 {mine.Count} 份\n正在核对当前进度…");
            }
        }
        OnPropertyChanged(nameof(ActiveProfileText));
        OnPropertyChanged(nameof(ActiveProfileDisplayName));
        OnPropertyChanged(nameof(HasProfileSlots));
        OnPropertyChanged(nameof(HasCloudSave));
        OnPropertyChanged(nameof(EmptyBackupHint));
        OnPropertyChanged(nameof(BackupOwnerText));
        RefreshVisibleBackups();
    }

    // ── 存档路径增删（统一入口：用户移除决定持久化，自动探测不得复活）──

    /// <summary>规范化存档路径用于比较：去空白、去尾部目录分隔符（大小写不敏感比较由调用处做）。</summary>
    private static string NormSavePath(string p) => (p ?? "").Trim().TrimEnd('\\', '/');

    private bool IsSavePathIgnored(string path)
    {
        string norm = NormSavePath(path);
        return IgnoredSavePaths.Any(x => string.Equals(NormSavePath(x), norm, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>后台自动探测/Wiki 补全/去重合并入列：去重，并跳过用户手动移除过的路径。返回是否新增。</summary>
    public bool TryAddAutoSavePath(SavePathEntry p)
    {
        if (p == null || string.IsNullOrWhiteSpace(p.Path)) return false;
        string norm = NormSavePath(p.Path);
        if (SavePaths.Any(x => string.Equals(NormSavePath(x.Path), norm, StringComparison.OrdinalIgnoreCase))) return false;
        if (IsSavePathIgnored(p.Path)) return false;
        SavePaths.Add(p);
        return true;
    }

    /// <summary>用户显式点「自动获取/刷新规则」：允许把曾移除的路径加回（同时解除忽略标记）。返回是否新增。</summary>
    public bool AcceptAutoSavePath(SavePathEntry p)
    {
        if (p == null || string.IsNullOrWhiteSpace(p.Path)) return false;
        string norm = NormSavePath(p.Path);
        IgnoredSavePaths.RemoveAll(x => string.Equals(NormSavePath(x), norm, StringComparison.OrdinalIgnoreCase));
        if (SavePaths.Any(x => string.Equals(NormSavePath(x.Path), norm, StringComparison.OrdinalIgnoreCase))) return false;
        SavePaths.Add(p);
        return true;
    }

    /// <summary>用户手动添加存档路径：解除该路径的忽略标记并去重入列（Source=手动）。</summary>
    public void AddManualSavePath(string path)
    {
        string norm = NormSavePath(path);
        IgnoredSavePaths.RemoveAll(x => string.Equals(NormSavePath(x), norm, StringComparison.OrdinalIgnoreCase));
        if (SavePaths.All(x => !string.Equals(NormSavePath(x.Path), norm, StringComparison.OrdinalIgnoreCase)))
            SavePaths.Add(new SavePathEntry { Path = path.Trim(), Source = "手动" });
    }

    /// <summary>用户手动移除存档路径：从列表删除并记入忽略名单，防止后台自动探测再次加回。</summary>
    public void RemoveSavePath(SavePathEntry p)
    {
        if (!SavePaths.Remove(p) || string.IsNullOrWhiteSpace(p.Path)) return;
        string norm = NormSavePath(p.Path);
        if (!IgnoredSavePaths.Any(x => string.Equals(NormSavePath(x), norm, StringComparison.OrdinalIgnoreCase)))
            IgnoredSavePaths.Add(norm);
    }

    /// <summary>按当前激活玩家重建备份列表视图（新建备份/切换玩家/删除后调用）。</summary>
    public void RefreshVisibleBackups()
    {
        var want = Backups
            .Where(b => (b.Profile ?? "") == (ActiveProfile ?? ""))
            .OrderByDescending(b => b.Time)
            .ToList();
        VisibleBackups.Clear();
        foreach (var r in want) VisibleBackups.Add(r);
        OnPropertyChanged(nameof(HasVisibleBackups));
        OnPropertyChanged(nameof(EmptyBackupHint));
        OnPropertyChanged(nameof(BackupOwnerText));
        OnPropertyChanged(nameof(HasBackups));
    }

    [JsonIgnore]
    public string TotalSizeText
    {
        get
        {
            double mb = Backups.Sum(b => b.Size) / 1024.0 / 1024.0;
            return mb >= 1 ? $"{mb:F1} MB" : $"{Backups.Sum(b => b.Size) / 1024.0:F0} KB";
        }
    }

    public void Refresh()   // 触发依赖计算属性的更新
    {
        OnPropertyChanged(nameof(LastBackupText));
        OnPropertyChanged(nameof(LastPlayedText));
        OnPropertyChanged(nameof(LastPlayedTip));
        OnPropertyChanged(nameof(TotalPlayText));
        OnPropertyChanged(nameof(TotalSizeText));
        OnPropertyChanged(nameof(HasBackups));
        OnPropertyChanged(nameof(BackupCountText));
        OnPropertyChanged(nameof(HasIcon));
        OnPropertyChanged(nameof(HasCover));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(SubName));
        OnPropertyChanged(nameof(Letter));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class SavePathEntry : INotifyPropertyChanged
{
    public string Path { get; set; } = "";
    public string Source { get; set; } = "手动";   // Steam云 / Unity / Unreal / Godot / 模板 / 目录内 / 手动
    public bool IsReg { get; set; } = false;

    [JsonIgnore]
    public bool Exists => IsReg ? true : System.IO.Directory.Exists(Path);
    [JsonIgnore]
    public string StatusText => IsReg ? "注册表" : (Exists ? "● 已找到" : "● 未找到");
    [JsonIgnore]
    public bool Missing => !IsReg && !Exists;

    public event PropertyChangedEventHandler? PropertyChanged;
    public void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class BackupRecord : INotifyPropertyChanged
{
    public DateTime Time { get; set; }
    /// <summary>所属玩家（槽位名）；"" = 未启用玩家时的通用备份。</summary>
    public string Profile { get; set; } = "";
    private string _note = "";
    public string Note
    {
        get => _note;
        set { _note = value; OnPropertyChanged(nameof(Note)); }
    }
    private string _file = "";
    /// <summary>zip 路径。持久化时 BackupRoot 下的文件存为相对路径（换备份目录/迁移数据后记录仍能跟随
    /// 新根解析，不再出现整批「缺失」幽灵记录）；根外的路径按原样存（兼容外部导入）。
    /// getter 恒返回解析后的绝对路径——全部调用方（备份/还原/云登记表键）无须感知存储格式。
    /// 旧版 games.json 里的绝对路径反序列化后照常工作，下次 Save 时自动转为相对路径。</summary>
    [JsonIgnore]
    public string File
    {
        get => ResolveFile(_file);
        set => _file = StorableFile(value);
    }

    /// <summary>JSON 持久化专用：直读直写原始存储值（相对路径或旧版绝对路径），不经解析。
    /// 对外键名仍为 "File"，与既有 games.json 双向兼容；不设此属性的话序列化会走上面的
    /// getter 把解析后的绝对路径写回盘，相对存储就永远落不了盘。
    /// setter：绝对路径 → 归一转换（旧版绝对记录加载即转相对）；相对路径 → 原样存储
    ///（已是存储格式，不能过 StorableFile——GetFullPath 会按当前目录解析把它毁掉）。</summary>
    [JsonPropertyName("File")]
    public string FileJson
    {
        get => _file;
        set => _file = value != null && Path.IsPathRooted(value) ? StorableFile(value) : value ?? "";
    }

    /// <summary>原始存储值（相对路径或旧版绝对路径；迁移认领/测试用）。</summary>
    internal string FileStored => _file;

    /// <summary>BackupRoot 下的路径 → 相对路径（保留 Windows 分隔符以保证 Path.Combine 逐字节还原）；
    /// 根外路径原样返回。</summary>
    internal static string StorableFile(string abs)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(abs)) return abs ?? "";
            string root = Path.GetFullPath(Store.BackupRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(abs)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string rel = Path.GetRelativePath(root, full);
            // 不在根下（根自身/逃逸 ../、跨盘）：存规范绝对路径（GetFullPath 归一，不带残留的 ..）
            if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal) ||
                Path.IsPathRooted(rel)) return full;
            return rel;
        }
        catch { return abs; }
    }

    /// <summary>存储值 → 绝对路径：已是绝对路径原样返回；相对路径拼接当前 BackupRoot。</summary>
    internal static string ResolveFile(string stored)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(stored)) return stored ?? "";
            if (Path.IsPathRooted(stored)) return stored;
            return Path.Combine(Store.BackupRoot, stored);
        }
        catch { return stored; }
    }

    public long Size { get; set; }              // zip 实际占用
    public long OriginalSize { get; set; }      // 快照存档逻辑总大小（未压缩）
    public long PackedSize { get; set; }        // 实际打包的原始字节（增量 = 变化部分）
    public string Kind { get; set; } = "";      // full | incr（"" 视为 full，兼容旧记录）
    public string BaseFile { get; set; } = "";  // 增量备份的父 zip 文件名
    /// <summary>运行中热快照（游戏进程存活时截取，可能不含最后一次游戏内保存之后的数据）。</summary>
    public bool Hot { get; set; } = false;
    private bool _locked;
    /// <summary>锁定：滚动清理（超过「每游戏保留份数」时的自动淘汰）跳过这份备份。
    /// 手动删除/删玩家不受此影响（仍会弹确认）。持久化在 games.json。</summary>
    public bool Locked
    {
        get => _locked;
        set { if (_locked != value) { _locked = value; OnPropertyChanged(nameof(Locked)); } }
    }
    /// <summary>存档时间：游戏内最后一次保存的时刻（zip 内 manifest.Files 最大 M 的本地时间）。
    /// 这是备份的核心属性——用户关心"这份备份是几点的游戏进度"，而非"几点打的包"。
    /// MinValue = 未记录（旧记录，启动后台回填 BackfillSavedAt）。</summary>
    public DateTime SavedAt { get; set; } = DateTime.MinValue;
    /// <summary>打包时存档文件全集为空（新玩家空锚点/游戏尚未产生存档）。
    /// 与 SavedAt==MinValue 组合才能区分"空锚点"与"旧记录未回填"。</summary>
    public bool EmptyAnchor { get; set; }

    private bool _cloudUploaded;
    /// <summary>运行时标记：该备份已上传云端（不持久化，由 CloudSyncService 按登记表刷新）。</summary>
    [JsonIgnore]
    public bool CloudUploaded
    {
        get => _cloudUploaded;
        set { if (_cloudUploaded != value) { _cloudUploaded = value; OnPropertyChanged(nameof(CloudUploaded)); } }
    }

    private bool _cloudDeleted;
    /// <summary>运行时标记：该备份曾上传但已被用户从云端删除（登记表 Deleted 位）。
    /// 为 true 时按钮显示「已删重传」，用户可一键重新上传。</summary>
    [JsonIgnore]
    public bool CloudDeleted
    {
        get => _cloudDeleted;
        set { if (_cloudDeleted != value) { _cloudDeleted = value; OnPropertyChanged(nameof(CloudDeleted)); } }
    }

    private bool _isNewerThanLive;
    /// <summary>运行时标记：这份备份的存档时间新于当前 live（还原到旧档后，更新的备份行在锁前挂橙色感叹号）。
    /// 不持久化，由主窗口每次 live 核对后按 SavedAt 与 live 最新存档时间比较回填。</summary>
    [JsonIgnore]
    public bool IsNewerThanLive
    {
        get => _isNewerThanLive;
        set { if (_isNewerThanLive != value) { _isNewerThanLive = value; OnPropertyChanged(nameof(IsNewerThanLive)); } }
    }

    private string _newerThanLiveTip = "";
    /// <summary>感叹号悬停说明（该备份存档时间 / 当前存档时间 / 新于当前提醒）；空串=无标记。</summary>
    [JsonIgnore]
    public string NewerThanLiveTip
    {
        get => _newerThanLiveTip;
        set { if (_newerThanLiveTip != value) { _newerThanLiveTip = value; OnPropertyChanged(nameof(NewerThanLiveTip)); } }
    }

    [JsonIgnore]
    public bool IsIncr => Kind == "incr";
    [JsonIgnore]
    public string KindText => IsIncr ? "增量" : "全量";
    [JsonIgnore]
    public string TimeText => Time.ToString("yyyy-MM-dd HH:mm");
    [JsonIgnore]
    public bool HasSavedAt => SavedAt != DateTime.MinValue;
    /// <summary>主行：「存档于 日期时间」（游戏真实保存时刻）；空锚点显示"尚无游戏存档"；旧记录未回填时回退备份时间。</summary>
    [JsonIgnore]
    public string SavedAtMainText => HasSavedAt ? "存档于 " + SavedAt.ToString("yyyy-MM-dd HH:mm")
        : (EmptyAnchor ? "尚无游戏存档" : "存档于 " + TimeText);
    /// <summary>副行小字：备份（打包）时刻；始终显示完整日期时间（不因与存档同日省略日期）。
    /// 无存档时间的记录为空串（UI 隐藏该行）。</summary>
    [JsonIgnore]
    public string BackupTimeText => !HasSavedAt ? ""
        : "备份于 " + Time.ToString("yyyy-MM-dd HH:mm");
    [JsonIgnore]
    public string SizeText
    {
        get
        {
            double mb = Size / 1024.0 / 1024.0;
            return mb >= 1 ? $"{mb:F1} MB" : $"{Size / 1024.0:F0} KB";
        }
    }

    /// <summary>备注列第二行：统一「存档大小（zip 实际大小） · 压缩率」，不显示原始大小。
    /// 压缩率 = 1 - 压缩包大小 / 包内文件未压缩总大小（全量 PackedSize=OriginalSize；增量仅含变化文件，
    /// 用 PackedSize 才与存档大小口径一致）；无统计数据的旧记录只显示存档大小。</summary>
    [JsonIgnore]
    public string SizeDetailText
    {
        get
        {
            long raw = PackedSize > 0 ? PackedSize : OriginalSize;
            if (raw > 0 && Size > 0)
            {
                int ratio = (int)Math.Round(100.0 * (1 - (double)Size / raw));
                ratio = Math.Clamp(ratio, 0, 100);
                return $"{SizeText} · 压缩率 {ratio}%";
            }
            return SizeText;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>玩家筹码视图模型（玩家名 + 是否当前激活 + 第二行 live/备份状态；激活态变化通知 UI）。
/// StatusKind：normal（灰/芯片内弱白）、good（已备份，青）、warn（未备份/有新进度，橙）。</summary>
public class ProfileSlotVm : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive != value) { _isActive = value; OnPropertyChanged(nameof(IsActive)); } }
    }

    private string _statusText = "";
    /// <summary>筹码第二行：短状态（≤6 字，如「✓ 已备份」「● 有新进度」「09-27 20:11」）；空串隐藏。</summary>
    public string StatusText
    {
        get => _statusText;
        private set { if (_statusText != value) { _statusText = value; OnPropertyChanged(nameof(StatusText)); } }
    }

    private string _statusKind = "normal";
    /// <summary>状态配色键：normal/good/warn，由 XAML 触发器映射到主题色。</summary>
    public string StatusKind
    {
        get => _statusKind;
        private set { if (_statusKind != value) { _statusKind = value; OnPropertyChanged(nameof(StatusKind)); } }
    }

    private string _statusTip = "";
    /// <summary>悬停完整说明（绝对时间/份数/live 最新更新），第二行只放短词防筹码过宽。</summary>
    public string StatusTip
    {
        get => _statusTip;
        private set { if (_statusTip != value) { _statusTip = value; OnPropertyChanged(nameof(StatusTip)); } }
    }

    private string _statusAction = "";
    /// <summary>状态行点击动作："" 不可点；restoreFavorite=还原她心目中的指针包；restoreCloud=下载云端最新并还原。</summary>
    public string StatusAction
    {
        get => _statusAction;
        private set
        {
            if (_statusAction != value)
            {
                _statusAction = value;
                OnPropertyChanged(nameof(StatusAction));
                OnPropertyChanged(nameof(StatusClickable));
            }
        }
    }

    private string _restoredTagText = "";
    /// <summary>主状态行下方的次要事实标记：最近一次手动还原所到的存档时间（如「↩ 已还原到 10-01 15:26」）。
    /// 与主状态并列展示——还旧档后主状态是可点的「本地有较新备份」，此标记保留「刚还原过」的痕迹。空串隐藏。</summary>
    public string RestoredTagText
    {
        get => _restoredTagText;
        private set
        {
            if (_restoredTagText != value)
            {
                _restoredTagText = value;
                OnPropertyChanged(nameof(RestoredTagText));
            }
        }
    }

    private string _restoredTagTip = "";
    /// <summary>还原次要标记的悬停说明。</summary>
    public string RestoredTagTip
    {
        get => _restoredTagTip;
        private set
        {
            if (_restoredTagTip != value)
            {
                _restoredTagTip = value;
                OnPropertyChanged(nameof(RestoredTagTip));
            }
        }
    }

    /// <summary>状态行是否可点（XAML 据此放手型/下划线/开放命中）。</summary>
    public bool StatusClickable => !string.IsNullOrEmpty(_statusAction);

    /// <summary>一次性更新第二行（RefreshProfiles 与异步 live/云端核对共用）。</summary>
    public void SetStatus(string text, string kind, string tip, string action = "")
    {
        StatusText = text;
        StatusKind = kind;
        StatusTip = tip;
        StatusAction = action;
    }

    /// <summary>设置主状态行下方的「已还原到 X」次要事实标记（空串=清除）。</summary>
    public void SetRestoredTag(string text, string tip = "")
    {
        RestoredTagText = text;
        RestoredTagTip = tip;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>一次游玩会话：从进程出现到进程消失。闭合后才计入总时长与列表。</summary>
public class PlaySession
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }                     // MinValue = 进行中/未闭合（正常不应持久化，崩溃遗留由 PlayTracker 清理）
    public long Seconds { get; set; }
    /// <summary>app = 经本应用启动（起点精确）；monitor = 轮询发现的外部/Steam 启动（起点误差 ≤3 秒）。</summary>
    public string Source { get; set; } = "monitor";
    /// <summary>中断会话：应用退出时游戏仍在运行，时长只计到应用退出，不更新「最后关闭时间」。</summary>
    public bool Interrupted { get; set; }

    [JsonIgnore]
    public string DurationText => PlayTracker.FormatDuration(Seconds);
    [JsonIgnore]
    public string StartText => Start.ToString("yyyy-MM-dd HH:mm");
    [JsonIgnore]
    public string RangeText => Start.ToString("MM-dd HH:mm") + " – " + End.ToString("HH:mm");
}
