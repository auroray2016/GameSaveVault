using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

using GameSaveManager.Models;
using GameSaveManager.Services;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using System.Windows.Data;
using MessageBox = System.Windows.MessageBox;
using Clipboard = System.Windows.Clipboard;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace GameSaveManager;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private string? _steamPath;
    private Dictionary<int, List<string>> _userdataMap = new();
    private GameEntry? _selectedGame;
    private string _filter = "all";
    private string _keyword = "";
    private readonly HashSet<string> _wasRunning = new();
    /// <summary>本应用用 exe 启动的游戏进程（gameId → PID/进程名/启动时刻）：自动备份优先认 PID，
    /// 防同名进程误判；Steam 协议启动拿不到游戏 PID，仍走进程名兜底。</summary>
    private readonly Dictionary<string, (int Pid, string Name, DateTime StartedAt)> _launched = new();
    /// <summary>进行中的游玩会话（gameId → 起点/来源）：WatchTick 对所有游戏跟踪运行态翻转，
    /// 进程消失即闭合，统计最后关闭时间/时长/会话明细，与自动备份开关无关。</summary>
    private readonly Dictionary<string, (DateTime Start, string Source)> _playOpen = new();
    /// <summary>自动备份进行中的游戏 Id（防同一游戏重复触发）。</summary>
    private readonly HashSet<string> _autoBusy = new(StringComparer.Ordinal);
    /// <summary>自动备份并发上限：多游戏同时退出时最多 2 路打包（存档盘 IO 密集，再多收益小）。</summary>
    private static readonly SemaphoreSlim AutoBackupGate = new(2, 2);
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.4) };
    private bool _suppressAutoToast;
    private DateTime _lastAutoBackup = DateTime.MinValue;
    /// <summary>运行中定时快照的上次时间（按游戏 Id）。</summary>
    private readonly Dictionary<string, DateTime> _lastHotSnapshot = new();
    private int _sortMode = 0;         // 0=最近游玩（默认） 1=名称 2=最近添加
    private bool _sortDirDesc = true;  // true=新→旧 / Z→A
    /// <summary>本次运行已被认领的旧 zip（同名游戏重新入库时防止重复认领）。</summary>
    private readonly HashSet<string> _reclaimedZipPaths = new(StringComparer.OrdinalIgnoreCase);

    public GameEntry? SelectedGame
    {
        get => _selectedGame;
        set
        {
            _selectedGame = value;
            OnPropertyChanged(nameof(SelectedGame));
            ShowPoster = value == null;   // 无选中（启动/删除游戏/筛选隐藏）→ 海报；有选中 → 详情
        }
    }

    private bool _showPoster;
    /// <summary>true 时右侧显示海报（无选中游戏）。常规下由 SelectedGame 派生；点列表空白处可强制显示。
    /// 用平移而非 Opacity/Visibility 切换，规避列表滚动时的渲染树移除崩溃。</summary>
    public bool ShowPoster
    {
        get => _showPoster;
        set
        {
            _showPoster = value;
            OnPropertyChanged(nameof(ShowPoster));
            if (PosterTransform != null)
                PosterTransform.X = value ? 0 : 9999;
            if (EmptyState != null)
                EmptyState.IsHitTestVisible = value;
        }
    }

    private string _policyText = "";
    public string PolicyText
    {
        get => _policyText;
        set { _policyText = value; OnPropertyChanged(nameof(PolicyText)); }
    }

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.SetTheme(this, ThemeManager.IsDark);
        ThemeManager.ThemeChanged += UpdateThemeButton;
        UpdateThemeButton();
        Closed += (_, _) => ThemeManager.ThemeChanged -= UpdateThemeButton;
        // Store.Init 已在 App.OnStartup 中完成（窗口创建前）；此处重复调用会导致整轮加载/快照重跑
        // 存盘失败必须让用户看见（否则界面显示成功、重启后记录消失）
        Store.SaveFailed += msg => Dispatcher.BeginInvoke(() => ShowToast(msg, true));
        // 云同步：备份创建后自动上传 WebDAV；失败仅 toast 提示，不打断任何操作
        CloudSyncService.BackgroundFailed += msg => Dispatcher.BeginInvoke(() => ShowToast(msg, true));
        CloudSyncService.UploadStateChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (SelectedGame is { } sg) CloudSyncService.RefreshCloudFlags(sg);
        });
        CloudSyncService.CollectFinished += r => Dispatcher.BeginInvoke(() =>
        {
            if (r.backups > 0 || r.players > 0)
                ShowToast("云同步：云端收集到 " + r.backups + " 份新备份" + (r.players > 0 ? $"、{r.players} 位新玩家" : ""));
            else if (r.error != null) ShowToast("云同步失败：" + r.error, true);
        });
        CloudSyncService.Start();
        GamesList.ItemsSource = Store.Games;
        Store.Games.CollectionChanged += (_, _) => UpdateSideFooters();
        // 清理上次崩溃遗留的未闭合游玩会话（正常退出已在 OnClosed 结算）
        PlayTracker.DropStaleOpenSessions(Store.Games);
        // 启动即应用筛选与「收藏置顶」排序：XAML 中 FiltAll 的 IsChecked=True 在 InitializeComponent
        // 期间触发 OnFilterChanged 时，FiltSteam/FiltLocal 字段尚未赋值被守卫跳过，导致首次打开列表
        // 无排序（收藏游戏不置顶，需手动点筛选按钮才正常）。
        _sortMode = Math.Clamp(Store.Settings.SortMode, 0, 2);
        _sortDirDesc = Store.Settings.SortDesc;
        // 恢复排序 chip（IsChecked=True 触发 OnSortChipChanged → ApplyFilter）
        (_sortMode switch { 1 => RSortName, 2 => RSortAdded, _ => RSortPlayed }).IsChecked = true;
        UpdateSortArrow();
        SideCol.Width = new GridLength(Math.Clamp(Store.Settings.SidebarWidth, 240, 520));
        UpdateRecentAddedDot();
        ApplyFilter();
        ShowPoster = true;   // 启动不选中任何游戏：右侧直接显示海报
        _toastTimer.Tick += (_, _) => HideToast();
        UpdatePolicyText();

        _watchTimer.Tick += WatchTick;
        _watchTimer.Start();

        Loaded += async (_, _) =>
        {
            UpdateStatusBar();
            BackupService.MigrateLegacyDirs();   // 旧版 {guid} 备份目录 → {游戏名} 目录（幂等）
            ProfileService.MigrateLegacyProfileRoot();   // 旧版 profiles\ → _appdata\profiles\（幂等）
            var mirrorFails = ProfileService.MigrateMirrorBackups();   // 旧版玩家镜像 pN → 补打成玩家 zip（幂等；失败保留重试）
            foreach (var f in mirrorFails) Debug.WriteLine("[镜像迁移失败] " + f);
            ProfileService.MigrateLegacyBackupsToPlayers();   // 已建玩家的游戏：历史通用备份认领给玩家（幂等）
            ProfileService.BackfillMarkers();   // 玩家数据根补写 game.json 身份证，空壳根清理（幂等）
            ReclaimRemovedGameData(silent: false);   // 移除后重新入库的游戏：找回旧备份与玩家档案（幂等）
            bool rebuiltFromBackups = await TryOfferRebuildFromBackupsAsync();   // 索引丢失/损坏且无快照：询问是否从备份目录重建卡片
            if (Store.Games.Count == 0)
            {
                await ScanSteamAsync(firstRun: true);
                // 索引损坏后空库首扫：扫到的游戏可能正是旧 zip 的属主，补一次合并/找回
                MergeDuplicateEntries();
            }
            else
            {
                int merged = MergeDuplicateEntries();   // 卸载换目录重装 / 同目录多 exe / Steam↔学习版 重复条目合并
                if (merged > 0)
                    CollectionViewSource.GetDefaultView(Store.Games).Refresh();
                var fixLog = DataMigration.FixScannedEntries();   // 启动器/模拟器误入库自修复（幂等）
                if (fixLog.Count > 0)
                {
                    CollectionViewSource.GetDefaultView(Store.Games).Refresh();
                    ShowToast($"已自动修正 {fixLog.Count} 处启动器/模拟器误入库问题");
                }
                // 玩家1卡槽为 UI 虚拟显示（ListSlots 回退），入库零磁盘副作用，无需启动迁移
                _ = BackfillWikiAsync();   // 已有库：Wiki 规则回填/过期更新
                _ = BackfillLocalMatchAsync();   // 学习版联网反查封面/中文名
                if (Store.Settings.ScanOnStartup)
                    await ScanSteamAsync(false);   // 增量扫描，只新增不重复
            }
            NotifyDataIssues(rebuiltFromBackups);   // 索引损坏/设置损坏/备份盘掉线提示（仅本次启动有问题时）
            // 持久化玩家卡槽名单回填（镜像迁移/认领/找回/重建/合并都可能产生具名备份，故放在全部迁移之后；幂等）
            ProfileService.BackfillPlayers();
            // 实体版遗留的零备份「玩家1」清回虚拟态（先于认领）
            ProfileService.DematerializeEmptyDefaultPlayers();
            // 旧版通用备份（Profile 空）认领：无玩家则建玩家1，有玩家认领给激活玩家；零备份新游戏保持虚拟
            _ = ProfileService.MaterializeLegacyDefaultPlayers();
            // 旧备份记录补算存档时间（幂等；读 zip manifest，后台跑不阻塞 UI）
            await Task.Run(() => BackupService.BackfillSavedAt());
            if (SelectedGame is { } sg0)
            {
                sg0.RefreshVisibleBackups();   // 重建集合让列表/筹码的时间文本按新字段刷新
                sg0.RefreshProfiles();
            }
            // 备份目录迁移检查（上次改过备份目录且旧目录仍有备份 → 询问「移动认领」）：
            // 放在全部迁移/回填之后，ApplicationIdle 优先级不挤占启动
            _ = Dispatcher.BeginInvoke(new Action(CheckBackupMigration), DispatcherPriority.ApplicationIdle);
        };
    }

    /// <summary>启动数据异常提示：索引损坏/设置损坏/自定义备份盘不可用已回退（均不阻断启动）。
    /// rebuiltFromBackups=true 时游戏卡片已根据备份重建，措辞不得再提示"重新添加游戏"。</summary>
    private void NotifyDataIssues(bool rebuiltFromBackups = false)
    {
        var msgs = new List<string>();
        if (rebuiltFromBackups)
        {
            string quar = Store.CorruptGamesFile != null
                ? $"原损坏文件已改名为 {Store.CorruptGamesFile} 留档（位于 {Store.BaseDir}）。\n"
                : "";
            msgs.Add("游戏库索引已丢失/损坏，本次启动已根据备份目录重建游戏卡片（备份记录与玩家档案均已找回）。\n" +
                     quar + "重装游戏后扫描会自动补全 exe、AppId 与封面。");
        }
        else if (Store.RestoredGamesSnapshot != null)
            msgs.Add($"游戏库索引 games.json {(Store.CorruptGamesFile != null ? "已损坏" : "丢失")}，" +
                     $"已自动从程序数据快照恢复（{Store.RestoredGamesSnapshot}，位于备份目录\\{Store.AppSnapshotDir}）。\n" +
                     (Store.CorruptGamesFile != null
                         ? $"原损坏文件已改名为 {Store.CorruptGamesFile} 留档（位于 {Store.BaseDir}）。\n\n"
                         : "") +
                     "磁盘上的备份 zip 不受影响，玩家档案与备份记录均已随索引恢复。");
        else if (Store.CorruptGamesFile != null)
            msgs.Add($"游戏库索引 games.json 已损坏，原文件已改名为\n{Store.CorruptGamesFile}\n" +
                     $"（位于 {Store.BaseDir}）留档。\n\n" +
                     "磁盘上的备份 zip 不受影响：重新添加游戏后会自动找回旧备份。");
        if (Store.SettingsRestoreCause != SettingsRestoreCause.None
            && Store.RestoredSettingsSnapshot != null)
        {
            string what = Store.SettingsRestoreCause switch
            {
                SettingsRestoreCause.DefaultShellRollback =>
                    "检测到设置被出厂默认内容覆盖（云账号、快捷键等曾丢失）",
                SettingsRestoreCause.Corrupt =>
                    "设置文件无法正常读取（文件损坏或被其他程序占用）",
                SettingsRestoreCause.Missing => "设置文件丢失",
                _ => "设置文件异常",
            };
            msgs.Add($"{what}，已自动从程序数据快照恢复（{Store.RestoredSettingsSnapshot}），请核对云同步与快捷键等设置。");
        }
        else if (Store.CorruptSettings)
            msgs.Add("设置文件 settings.json 已损坏，本次已恢复默认设置，原文件已改名留档。");
        if (Store.BackupRootFallback)
            msgs.Add("自定义的备份存放目录当前不可访问（盘已拔除或无权限），本次备份已临时存回默认目录。\n" +
                     "盘恢复后可在「设置 → 数据」重新指定。");
        if (msgs.Count > 0)
            MessageBox.Show(this, string.Join("\n\n", msgs), "数据恢复提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 某些启动方式（无前台激活权限）会把主窗口沉到桌面图标层之下，表现为"窗口不可见"。
        // 置顶脉冲把窗口提到正常层顶部（SetWindowPos 不抢焦点，只影响本次启动）。
        Topmost = true;
        Topmost = false;
        RegisterBackupHotKey();
    }

    protected override void OnClosed(EventArgs e)
    {
        // 先停定时器/后台枚举，避免退出过程中还有 Tick 触碰正在关闭的 UI 与数据
        try { _watchTimer.Stop(); } catch { }
        try { _toastTimer.Stop(); } catch { }
        try { _dbUpdateCts?.Cancel(); } catch { }
        FlushPlaySessionsOnExit();
        // 云同步优雅停机：channel 收口、在途上传有界等待、登记表同步落盘（丢线程池防 UI 续体死锁）
        try { Task.Run(CloudSyncService.StopAsync).Wait(TimeSpan.FromSeconds(8)); } catch { }
        GlobalHotKeyService.Unregister();
        base.OnClosed(e);
    }

    /// <summary>应用退出时结算仍在进行的游玩会话：时长计到退出时刻（标中断），
    /// 但不更新「最后关闭时间」——游戏可能仍在运行，并非真正关闭。</summary>
    private void FlushPlaySessionsOnExit()
    {
        if (_playOpen.Count == 0) return;
        DateTime now = DateTime.Now;
        foreach (var (id, ps) in _playOpen.ToList())
        {
            var g = Store.Games.FirstOrDefault(x => x.Id == id);
            if (g != null) PlayTracker.CloseSession(g, ps.Start, now, ps.Source, interrupted: true);
        }
        _playOpen.Clear();
        try { Store.Save(); } catch { /* 退出路径静默 */ }
    }

    /// <returns>true=已注册或已禁用（空字符串）；false=格式无效或被系统拒绝（可能与其他软件冲突）。</returns>
    private bool RegisterBackupHotKey()
    {
        // 与其他软件热键冲突时 RegisterHotKey 会失败，不影响主程序运行
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            return GlobalHotKeyService.Register(hwnd, Store.Settings.BackupHotKey, OnGlobalBackupHotKey);
        }
        catch { return false; }
    }

    private bool _hotKeyBacking;

    /// <summary>全局快捷键：静默热备份——不弹任何对话框，只用 toast 反馈结果。</summary>
    private async void OnGlobalBackupHotKey()
    {
        if (_hotKeyBacking || _busyDepth > 0) return;   // 进行中/忙：忽略，不排队
        var g = PickHotKeyBackupTarget();
        if (g == null)
        {
            ShowToast("没有正在运行的游戏，请先在主窗口选中要备份的游戏");
            return;
        }
        _hotKeyBacking = true;
        bool hot = BackupService.IsGameRunning(g);
        SetBusy(true);
        try
        {
            BackupRecord r;
            int skipped = 0;
            r = await Task.Run(() => BackupService.Create(g, "热键备份", hot, out skipped));
            RefreshAfterChange(g);
            ShowToast(hot && skipped > 0
                ? $"✓「{g.DisplayName}」备份完成 {r.SizeText}（跳过 {skipped} 个占用文件）"
                : $"✓「{g.DisplayName}」备份完成 {r.SizeText}");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("没有变化"))
        {
            ShowToast($"「{g.DisplayName}」存档无变化，无需备份");
        }
        catch (Exception ex)
        {
            ShowToast($"「{g.DisplayName}」热键备份失败：{ex.Message}", true);
        }
        finally
        {
            SetBusy(false);
            _hotKeyBacking = false;
        }
    }

    /// <summary>热键备份目标：优先正在运行的游戏（用户在游戏里按键）；
    /// 多个同时运行时取当前选中的，否则取第一个；都没运行则备份当前选中的游戏。</summary>
    private GameEntry? PickHotKeyBackupTarget()
    {
        var running = Store.Games
            .Where(x => x.ExePath.Length > 0 && BackupService.IsGameRunning(x)).ToList();
        if (running.Count > 0)
            return SelectedGame != null && running.Contains(SelectedGame) ? SelectedGame : running[0];
        return SelectedGame;
    }

    private void UpdatePolicyText()
    {
        PolicyText = $"保留策略：每游戏最多 {Store.Settings.MaxKeepPerGame} 份"
            + (Store.Settings.IncrementalEnabled ? $" · 增量备份（每 {Store.Settings.FullEveryN} 次做一次全量）" : " · 全量快照");
    }

    /// <summary>启动后对已入库游戏做 Wiki 补全（缓存 30 天，过期自动静默更新）。</summary>
    private static async Task BackfillWikiAsync()
    {
        await Task.Delay(1500);
        foreach (var g in Store.Games.ToList())
            await EnrichWikiAsync(g);
    }

    /// <summary>启动后对学习版游戏做联网反查（Wiki 提 appid → 封面/中文名），静默。
    /// 未匹配过的直接查；已匹配但没封面的（可能 appid 不准）强制刷新 Wiki 纠正。
    /// 并发 4 路（国内环境下 Steam storesearch 不可达，单路 12s 超时，串行走不完）。</summary>
    private async Task BackfillLocalMatchAsync()
    {
        await Task.Delay(3000);
        var toFix = Store.Games
            .Where(x => !x.IsSteam && (x.AppId == 0 || !x.HasCover
                        || string.IsNullOrWhiteSpace(x.NameCN) || IsJunkName(x.NameEN)))
            .OrderBy(x => x.AppId == 0)   // 先纠正已有 appid 但没封面/名字不对的
            .ToList();
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(toFix.Select(async g =>
        {
            await gate.WaitAsync();
            try { await MatchLocalGameAsync(g, silent: true, forceWiki: g.AppId > 0); }
            finally { gate.Release(); }
        }));
    }

    /// <summary>
    /// 合并重复入库的同游戏条目（启动时幂等执行）。两种情况算重复：
    /// ① 同安装根下多个 exe（游戏 exe 与启动器并存，旧逻辑）；
    /// ② 卸载后换目录重装 / Steam↔学习版互转：同 AppId 但安装目录不同，且恰有一条 exe 仍有效
    ///    （两条都有效视为真·双装，保留）。
    /// 保留历史备份多的旧条目，新条目的有效 exe/存档路径/备份/玩家镜像并入后移除。
    /// </summary>
    private int MergeDuplicateEntries()
    {
        int mergedTotal = 0;
        bool changed;
        do
        {
            changed = false;
            foreach (var grp in Store.Games.Where(x => x.AppId > 0)
                         .GroupBy(x => x.AppId).Where(g => g.Count() > 1).ToList())
            {
                var members = grp.ToList();
                GameEntry? a = null, b = null;
                for (int i = 0; i < members.Count && a == null; i++)
                for (int j = i + 1; j < members.Count; j++)
                {
                    var x = members[i];
                    var y = members[j];
                    bool sameRoot = SameInstallRoot(x.InstallDir, y.InstallDir);
                    bool reinstall = !sameRoot && (ExeAlive(x) ^ ExeAlive(y));
                    if (sameRoot || reinstall) { a = x; b = y; break; }
                }
                if (a == null || b == null) continue;

                var (keep, drop) = ChooseKeeper(a, b);
                Store.MergeGame(keep, drop);
                Store.Games.Remove(drop);
                mergedTotal++;
                changed = true;
                break;   // 集合已变，重新分组
            }
        } while (changed);

        if (mergedTotal > 0) Store.Save();
        return mergedTotal;
    }

    private static bool ExeAlive(GameEntry g) =>
        !string.IsNullOrWhiteSpace(g.ExePath) && File.Exists(g.ExePath);

    /// <summary>两条重复记录中保留谁：历史备份多者优先（旧条目挂着备份与玩家镜像），
    /// 其次非启动器、收藏、存档路径多、入库更早（GUID 与 profiles 目录延续）。</summary>
    private static (GameEntry keep, GameEntry drop) ChooseKeeper(GameEntry a, GameEntry b)
    {
        int Score(GameEntry g) =>
            g.Backups.Count * 100
            + (IsLauncherEntry(g) ? 0 : 1000)
            + (g.Favorite ? 200 : 0)
            + g.SavePaths.Count * 10
            + (g.ProfileSlots.Count > 0 ? 50 : 0);
        int sa = Score(a), sb = Score(b);
        if (sa == sb)
            return a.AddedAt <= b.AddedAt ? (a, b) : (b, a);
        return sa > sb ? (a, b) : (b, a);
    }

    /// <summary>两个安装目录是否同根（一方是另一方的前缀，如 D:\Game 与 D:\Game\bin\x64）。</summary>
    private static bool SameInstallRoot(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        a = a.TrimEnd('\\', '/').ToLowerInvariant();
        b = b.TrimEnd('\\', '/').ToLowerInvariant();
        return a == b || a.StartsWith(b + "\\") || b.StartsWith(a + "\\");
    }

    /// <summary>入口是否为启动器/引导程序（与游戏 exe 同目录的附加 exe）。</summary>
    private static bool IsLauncherEntry(GameEntry g)
    {
        var exe = Path.GetFileNameWithoutExtension(g.ExePath ?? "").ToLowerInvariant();
        return exe.Contains("launcher") || exe.Contains("bootstrap") || exe.Contains("prelaunch")
            || exe.Contains("ali213") || exe.Contains("开始游戏") || exe.Contains("启动游戏");
    }

    // ── 已移除游戏数据的找回（重新入库时镜像回迁 + zip 记录重建） ──

    /// <summary>启动时：为所有在库游戏找回移除前保留的旧备份/玩家档案（幂等；有找回才提示）。</summary>
    private void ReclaimRemovedGameData(bool silent)
    {
        int games = 0, backups = 0, profiles = 0;
        foreach (var g in Store.Games.ToList())
        {
            var (n, slots) = ReclaimGameData(g);
            if (n == 0 && slots == 0) continue;
            games++; backups += n; profiles += slots;
        }
        if (games == 0) return;
        Store.Save();
        CollectionViewSource.GetDefaultView(Store.Games).Refresh();
        if (silent) return;
        ShowToast(profiles > 0
            ? $"已找回 {games} 款游戏移除前的数据：{backups} 份备份、{profiles} 个玩家档案"
            : $"已找回 {games} 款游戏移除前的旧备份（共 {backups} 份）");
    }

    /// <summary>索引丢失/损坏且快照也没恢复时：发现备份目录里有游戏备份，询问是否一键重建卡片。
    /// 用户选「否」时落盘一份空索引，避免以后每次启动都追问。</summary>
    private async Task<bool> TryOfferRebuildFromBackupsAsync()
    {
        if (Store.Games.Count > 0) return false;
        if (Store.CorruptGamesFile == null && !Store.GamesFileAbsent) return false;
        List<BackupService.OrphanBackupInfo> candidates;
        try
        {
            candidates = BackupService.ListOrphans()
                .Where(o => !o.IsProfileRoot && o.ZipCount > 0).ToList();
        }
        catch { return false; }
        if (candidates.Count == 0) return false;

        string why = Store.GamesFileAbsent ? "已丢失" : "已损坏";
        var r = MessageBox.Show(this,
            $"检测到备份目录中有 {candidates.Count} 款游戏的存档备份，但游戏库索引{why}。\n\n" +
            "是否立即根据备份重建游戏卡片？\n" +
            "• 备份记录、玩家档案、存档位置会自动找回\n" +
            "• 卡片先标记为未安装；重装游戏后扫描会自动补全 exe、AppId 与封面\n\n" +
            "选「否」保持空库（备份不会被删除，随时可在备份管理中查看）。",
            "从备份重建游戏库", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes)
        {
            Store.Save();   // 落盘空数组：用户已知情，以后不再追问
            return false;
        }

        SetBusy(true);
        try
        {
            BusyText.Text = "正在从备份重建游戏库…";
            await Task.Delay(60);   // 让忙碌遮罩先绘制
            // RebuildLibraryFromBackups 内部会增删已绑定到界面的 Store.Games，必须留在 UI 线程执行
            var (games, backups, slots) =
                BackupService.RebuildLibraryFromBackups(_reclaimedZipPaths);
            if (games == 0)
            {
                ShowToast("备份目录中没有可读取的备份清单，未重建卡片", true);
                return false;
            }
            ApplyFilter();
            ShowToast(slots > 0
                ? $"已从备份重建 {games} 款游戏：找回 {backups} 份备份、{slots} 个玩家档案"
                : $"已从备份重建 {games} 款游戏，找回 {backups} 份备份");
            return true;
        }
        catch (Exception ex)
        {
            ShowToast("重建失败：" + ex.Message, true);
            return false;
        }
        finally { SetBusy(false); }
    }

    /// <summary>找"空库重建"产生的占位卡：无 exe、挂着旧备份、游戏名候选与给定名一致。</summary>
    private static GameEntry? FindRebuiltPlaceholder(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string key = BackupService.SafeName(name);
        return Store.Games.FirstOrDefault(x =>
            !x.IsSteam && string.IsNullOrWhiteSpace(x.ExePath) && x.Backups.Count > 0 &&
            (BackupService.SafeName(x.NameCN) == key || BackupService.SafeName(x.NameEN) == key ||
             BackupService.SafeName(x.DisplayName) == key));
    }

    /// <summary>单个游戏的数据找回：玩家镜像按游戏名回迁到新 GUID + 按 manifest 重建备份记录。
    /// 返回 (找回备份数, 玩家数)；不覆盖 live 存档，只恢复备份/档案关联。</summary>
    private (int backups, int slots) ReclaimGameData(GameEntry g)
    {
        try
        {
            var (n, slots) = ProfileService.ReclaimAll(g, _reclaimedZipPaths);
            // 全新游戏：不建任何玩家数据，ListSlots 会虚拟显示玩家1
            g.RefreshProfiles();
            if (slots == 0 && n == 0) return (0, 0);
            g.RefreshVisibleBackups();
            g.Refresh();
            return (n, slots);
        }
        catch { return (0, 0); }
    }

    // ── Steam 扫描 ──────────────────────────────────────
    private async void OnScanSteam(object s, RoutedEventArgs e) => await ScanSteamAsync(false);

    private async Task ScanSteamAsync(bool firstRun)
    {
        SetBusy(true);
        try
        {
            var (newGames, msg, lpChanged) = await Task.Run(ScanSteamCore);
            _steamPath ??= SteamScanner.FindSteamPath();
            if (_steamPath != null)
                _userdataMap = SteamScanner.ScanUserdataSaves(_steamPath);
            foreach (var g in newGames) Store.Games.Add(g);
            // 可能是移除后重新扫描入库：找回旧备份与玩家档案
            int reBak = 0, reSlot = 0;
            foreach (var g in newGames) { var (b, s) = ReclaimGameData(g); reBak += b; reSlot += s; }
            if (newGames.Count > 0 || lpChanged) Store.Save();   // 最近游玩时间变化也需持久化
            if (newGames.Count > 0 || lpChanged) ApplyFilter();   // 刷新排序
            if (newGames.Count > 0)
            {
                UpdateRecentAddedDot();
                RevealGame(newGames[0]);   // 定位到新导入的第一款
            }
            string reclaimMsg = reBak + reSlot > 0
                ? $"；已找回 {reBak} 份旧备份" + (reSlot > 0 ? $"、{reSlot} 个玩家档案" : "")
                : "";
            ShowToast((firstRun && newGames.Count > 0
                ? $"首次扫描完成：发现 {newGames.Count} 款 Steam 游戏"
                : msg) + reclaimMsg);
            _ = EnrichSteamGamesSafeAsync();
        }
        catch (Exception ex)
        {
            ShowToast("扫描失败：" + ex.Message, true);
        }
        finally { SetBusy(false); }
    }

    /// <summary>封面/中文名补全是 fire-and-forget 后台任务，内部各步失败本有计数提示，
    /// 但顶层（线程池抛异常、Dispatcher 封送失败等）不能无人接：写崩溃日志并 toast，
    /// 否则任务无声中断、部分游戏永远停在无封面/无中文名状态且无任何线索。</summary>
    private async Task EnrichSteamGamesSafeAsync()
    {
        try { await EnrichSteamGamesAsync(); }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsm-crash.txt"),
                    $"[{DateTime.Now:HH:mm:ss}] 扫描后信息补全失败：{ex}\n");
            }
            catch { }
            await Dispatcher.BeginInvoke(() => ShowToast("游戏信息补全中断：" + ex.Message, true));
        }
    }

    private (List<GameEntry> games, string msg, bool lpChanged) ScanSteamCore()
    {
        var steamPath = SteamScanner.FindSteamPath();
        if (steamPath == null) return (new List<GameEntry>(), "未找到 Steam，请确认已安装", false);
        var userdataMap = SteamScanner.ScanUserdataSaves(steamPath);
        _userdataMap = userdataMap;
        var installed = SteamScanner.ScanInstalled(steamPath);
        // 过滤系统组件 / 运行库（非真实游戏）
        var skipNames = new[] { "redistributables", "steamworks", "steam runtime", "proton", "steam linux" };
        installed = installed.Where(gi =>
            !skipNames.Any(n => gi.Name.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
        var added = new List<GameEntry>();
        bool lpChanged = false;
        int scanErrors = 0;
        string? firstScanError = null;
        foreach (var gi in installed)
        {
            // 单个游戏出错（怪标题含非法路径字符、目录无权限等）只跳过这一款，不得中止整轮扫描
            try
            {
            // 同 AppId 已在库即视为同一游戏（含学习版条目：Steam↔学习版互转、重复扫描不再产生第二张卡片）
            var existing = Store.Games.FirstOrDefault(g => g.AppId == gi.AppId && gi.AppId > 0);
            if (existing != null)
            {
                // 已存在：仅刷新最近游玩时间（manifest 变化时保持排序准确）
                if (existing.LastPlayed != gi.LastPlayed)
                {
                    existing.LastPlayed = gi.LastPlayed;
                    lpChanged = true;
                }
                // 卸载重装 / 换库目录后：旧 exe 路径已失效而扫描到有效新位置 → 更新位置并补探存档路径
                if (!string.IsNullOrWhiteSpace(gi.ExePath) && File.Exists(gi.ExePath)
                    && (string.IsNullOrWhiteSpace(existing.ExePath) || !File.Exists(existing.ExePath))
                    && !existing.ExePath.Equals(gi.ExePath, StringComparison.OrdinalIgnoreCase))
                {
                    existing.ExePath = gi.ExePath;
                    existing.InstallDir = gi.InstallDir;
                    var extraPaths = SaveLocator.Detect(gi.Name, gi.ExePath, gi.InstallDir, gi.AppId, userdataMap);
                    // existing 已在界面上：SavePaths/VisibleBackups 是绑定集合，必须回 UI 线程改
                    Dispatcher.Invoke(() =>
                    {
                        foreach (var p in extraPaths) existing.TryAddAutoSavePath(p);   // 尊重用户移除决定
                        existing.Refresh();
                    });
                    lpChanged = true;
                }
                continue;
            }
            // 空库重建留下的占位卡（无 exe、挂旧备份、名字相符）：就地补全为 Steam 条目，不产生第二张卡
            var placeholder = FindRebuiltPlaceholder(gi.Name);
            if (placeholder != null)
            {
                placeholder.AppId = gi.AppId;
                placeholder.Type = "Steam";
                // 占位名就是英文名（备份目录是英文名）时不能留在 NameCN：中文名反查只在 NameCN 为空时执行，
                // 否则该卡永远显示英文。清空后 DisplayName 回退 NameEN，反查成功即补回中文名。
                if (BackupService.SafeName(placeholder.NameCN) == BackupService.SafeName(gi.Name))
                    placeholder.NameCN = "";
                placeholder.NameEN = gi.Name;
                placeholder.ExePath = gi.ExePath;
                placeholder.InstallDir = gi.InstallDir;
                string phCover = Store.CoverFileFor(gi.AppId);
                if (File.Exists(phCover)) placeholder.CoverFile = phCover;
                if (placeholder.LastPlayed == default) placeholder.LastPlayed = gi.LastPlayed;
                var phPaths = SaveLocator.Detect(gi.Name, gi.ExePath, gi.InstallDir, gi.AppId, userdataMap);
                Dispatcher.Invoke(() =>
                {
                    foreach (var p in phPaths) placeholder.TryAddAutoSavePath(p);
                    placeholder.RefreshVisibleBackups();
                    placeholder.Refresh();
                });
                lpChanged = true;
                continue;
            }
            var cover = Store.CoverFileFor(gi.AppId);
            var g = new GameEntry
            {
                NameCN = "",
                NameEN = gi.Name,
                Type = "Steam",
                AppId = gi.AppId,
                ExePath = gi.ExePath,
                InstallDir = gi.InstallDir,
                CoverFile = File.Exists(cover) ? cover : "",
                LastPlayed = gi.LastPlayed,
                AddedAt = DateTime.Now,
            };
            // 新卡尚未加入 Store.Games、界面没有它的绑定视图，这里在后台线程 Add 是安全的
            foreach (var p in SaveLocator.Detect(gi.Name, gi.ExePath, gi.InstallDir, gi.AppId, userdataMap))
                g.SavePaths.Add(p);
            added.Add(g);
            }
            catch (Exception ex)
            {
                scanErrors++;
                firstScanError ??= $"{gi.Name}: {ex.Message}";
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsm-crash.txt"),
                    $"[{DateTime.Now:HH:mm:ss}] 扫描 {gi.Name} 失败：{ex}\n"); } catch { }
            }
        }
        string doneMsg = $"扫描完成：新增 {added.Count} 款 Steam 游戏";
        if (scanErrors > 0) doneMsg += $"（{scanErrors} 款处理失败已跳过：{firstScanError}）";
        return (added, doneMsg, lpChanged);
    }

    /// <summary>后台补全：封面图下载 + 中文名获取（节流，不阻塞 UI）。</summary>
    private async Task EnrichSteamGamesAsync()
    {
        int coverFails = 0;   // 封面下载失败数（结束后一次性提示，不再静默）
        foreach (var g in Store.Games.Where(g => g.IsSteam && g.AppId > 0).ToList())
        {
            if (!g.HasCover)
            {
                string cover = Store.CoverFileFor(g.AppId);
                bool ok = await SteamApi.DownloadCover(g.AppId, cover);
                if (ok)
                    await Dispatcher.BeginInvoke(() => { g.CoverFile = cover; g.Refresh(); });
                else
                    coverFails++;
            }
            if (string.IsNullOrWhiteSpace(g.NameCN))
            {
                var r = await GameNameService.FetchAsync(g.AppId);
                if (r.Name != null)
                {
                    GameEntry? wikiTarget = null;
                    await Dispatcher.BeginInvoke(() =>
                    {
                        // 中文名揭晓后才能匹配上"空库重建"的中文名占位卡：并入占位卡（保留其备份/玩家），
                        // 删掉刚扫出的新 Steam 卡，避免同一游戏两张卡片
                        var twin = FindRebuiltPlaceholder(r.Name);
                        if (twin != null)
                        {
                            string oldTwinDisplay = twin.DisplayName;
                            Store.MergeGame(twin, g);   // exe/安装目录/存档路径/备份并集/玩家镜像并入
                            Store.Games.Remove(g);
                            twin.AppId = g.AppId;
                            twin.Type = "Steam";
                            twin.NameEN = g.NameEN;
                            twin.NameCN = r.Name;
                            if (!string.IsNullOrEmpty(g.CoverFile)) twin.CoverFile = g.CoverFile;
                            BackupService.MigrateBackupDir(twin, oldTwinDisplay);
                            twin.RefreshProfiles();
                            twin.RefreshVisibleBackups();
                            twin.Refresh();
                            Store.Save();
                            wikiTarget = twin;
                        }
                        else
                        {
                            string oldDisplay = g.DisplayName;
                            g.NameCN = r.Name;
                            g.Refresh();
                            // 反查前可能用过 Steam 英文名备份 → 备份目录名随 DisplayName 变了，
                            // 必须先迁移旧目录里的 zip，再补找回（找回是从新目录找）。
                            BackupService.MigrateBackupDir(g, oldDisplay);
                            Store.Save();
                            ReclaimGameData(g);
                        }
                    });
                    if (wikiTarget != null) { await EnrichWikiAsync(wikiTarget); continue; }
                }
            }
            await EnrichWikiAsync(g);
        }
        if (coverFails > 0)
            await Dispatcher.BeginInvoke(() => ShowToast(
                $"{coverFails} 款游戏的封面下载失败（网络原因），可稍后在「✎ 编辑」里重试", true));
    }

    /// <summary>
    /// 从 PCGamingWiki 补全存档/注册表位置（缓存 30 天；forceRefresh 强制联网）。
    /// 新增路径附加到游戏上，已存在的去重。返回新增路径条数；
    /// 网络失败时置 g.WikiFailed（详情页显示「点击重试」），成功/无数据则清除。
    /// </summary>
    private static async Task<int> EnrichWikiAsync(GameEntry g, bool forceRefresh = false)
    {
        try
        {
            string? sp = SteamScanner.FindSteamPath();
            string? uid = sp != null ? SteamScanner.GetSteamUserId(sp) : null;
            var found = await WikiService.EnrichAsync(g, sp, uid, forceRefresh);
            bool failed = WikiService.LastEnrichFailed;
            int added = 0;
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                g.WikiFailed = failed;
                foreach (var p in found)
                {
                    // 静默补全不得加回用户手动移除过的路径；用户显式「自动获取/重试」(forceRefresh) 才允许
                    bool ok = forceRefresh ? g.AcceptAutoSavePath(p) : g.TryAddAutoSavePath(p);
                    if (ok) added++;
                }
                if (added > 0) { Store.Save(); g.Refresh(); }
            });
            return added;
        }
        catch { /* Wiki 失败不影响主流程 */ }
        return 0;
    }

    // ── 手动添加游戏 ────────────────────────────────────
    private async void OnAddGame(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择游戏主程序 exe",
            Filter = "可执行文件 (*.exe)|*.exe",
        };
        if (dlg.ShowDialog(this) != true) return;

        string exe = dlg.FileName;
        string dir = Path.GetDirectoryName(exe) ?? "";

        // 同一 exe / 同一安装目录已在库 → 不重复入库，直接定位（卸载换目录重装的合并在联网匹配 AppId 后处理）
        var dup = Store.Games.FirstOrDefault(x =>
            !string.IsNullOrWhiteSpace(x.ExePath) && x.ExePath.Equals(exe, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(x.InstallDir) && dir.Equals(x.InstallDir, StringComparison.OrdinalIgnoreCase)));
        if (dup != null)
        {
            RevealGame(dup);
            ShowToast($"「{dup.DisplayName}」已在游戏库中，已为你定位");
            return;
        }

        string nameEN;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exe);
            nameEN = !string.IsNullOrWhiteSpace(vi.ProductName) ? vi.ProductName
                   : Path.GetFileNameWithoutExtension(exe);
        }
        catch { nameEN = Path.GetFileNameWithoutExtension(exe); }

        var g = new GameEntry { Type = "Local", NameEN = nameEN, ExePath = exe, InstallDir = dir, AddedAt = DateTime.Now };
        var paths = await Task.Run(() => SaveLocator.Detect(nameEN, exe, dir, 0, null));
        foreach (var p in paths) g.SavePaths.Add(p);

        var icon = await Task.Run(() => IconExtractor.ExtractToPng(exe, Store.IconFileFor(g.Id)));
        if (icon != null) g.IconFile = icon;

        Store.Games.Add(g);
        var (reBak, reSlot) = ReclaimGameData(g);   // 移除后重新添加：找回旧备份与玩家档案
        Store.Save();
        UpdateRecentAddedDot();
        RevealGame(g);
        string reclaimMsg = reBak + reSlot > 0
            ? $"；已找回 {reBak} 份旧备份" + (reSlot > 0 ? $"、{reSlot} 个玩家档案" : "")
            : "";
        ShowToast((paths.Count > 0
            ? $"已添加「{g.DisplayName}」，自动识别到 {paths.Count} 个存档位置"
            : $"已添加「{g.DisplayName}」，未自动识别到存档位置，请手动添加路径") + reclaimMsg);
        _ = MatchLocalGameAsync(g);
        _ = EnrichWikiAsync(g);
    }

    /// <summary>
    /// 学习版联网反查 Steam appid → 封面 + 中文名 + Wiki 存档路径。
    /// 主通道：PCGamingWiki 页面里的 Steam 商店链接（国内可达）；备用：Steam storesearch（海外）。
    /// 只接受与搜索词高置信匹配的结果，避免乱配；占位 ProductName（如 BootstrapPackagedGame）会用安装目录名。
    /// </summary>
    private async Task MatchLocalGameAsync(GameEntry g, bool silent = false, bool forceWiki = false)
    {
        try
        {
            // 钉死的条目（如同名新作被锁定 appid）只补封面/中文名/存档，不做 appid 反查
            int wikiAppId = 0;
            string wikiTitle = "";
            int searchAppId = 0;
            string searchTitle = "";
            if (!g.AppIdLocked)
            {
                var terms = SearchTerms(g);

                // 通道 1：PCGamingWiki 页面里的 Steam 链接（国内可达，置信度最高）
                // 用多个候选词依次试；目录/exe 名来源优先——NameEN 可能本身就是上次误配来的名字
                // （如被误配成 Desolate City），NameEN 自我确认会阻断正确词条的纠正
                foreach (var (term, _) in terms.Where(t => t.FromFile))
                {
                    var wiki = await WikiService.LookupByNameAsync(term, forceWiki);
                    if (wiki is { AppId: > 0 } && IsConfident(term, wiki.Title))
                    { wikiAppId = wiki.AppId; wikiTitle = wiki.Title; break; }
                }
                if (wikiAppId == 0)
                foreach (var (term, _) in terms.Where(t => !t.FromFile))
                {
                    var wiki = await WikiService.LookupByNameAsync(term, forceWiki);
                    if (wiki is { AppId: > 0 } && IsConfident(term, wiki.Title))
                    { wikiAppId = wiki.AppId; wikiTitle = wiki.Title; break; }
                }

                // 通道 2：Steam storesearch（海外可达时备用；封面下载成功才算有效，防首条结果乱配）
                if (wikiAppId == 0)
                {
                    // 与通道 1 相同：目录/exe 来源优先
                    var ordered = terms.Where(t => t.FromFile).Concat(terms.Where(t => !t.FromFile));
                    foreach (var (term, _) in ordered)
                    {
                        var hit = await SteamApi.SearchByName(term);
                        if (hit != null && IsConfident(term, hit.Value.Name) &&
                            await SteamApi.DownloadCover(hit.Value.AppId, Store.CoverFileFor(hit.Value.AppId)))
                        { searchAppId = hit.Value.AppId; searchTitle = hit.Value.Name; break; }
                    }
                }
            }

            // 定 appid：未匹配过时优先 Wiki；已有值时仅 Wiki 高可信结果可纠正
            int appId; string matchedTitle;
            if (g.AppId > 0)
            {
                if (wikiAppId > 0 && wikiAppId != g.AppId) { appId = wikiAppId; matchedTitle = wikiTitle; }
                else { appId = g.AppId; matchedTitle = g.NameEN; }
            }
            else
            {
                appId = wikiAppId > 0 ? wikiAppId : searchAppId;
                matchedTitle = wikiAppId > 0 ? wikiTitle : searchTitle;
            }
            if (appId == 0) return;

            // ── 以下为网络操作，后台线程完成后再封送 UI ──
            string coverPath = Store.CoverFileFor(appId);
            bool coverOk = g.HasCover || await SteamApi.DownloadCover(appId, coverPath);
            var cnResult = await GameNameService.FetchAsync(appId);   // 自动源：本地库→小黑盒→Steam
            var cn = cnResult.Name;

            string? newName = null;
            List<SavePathEntry>? extraPaths = null;
            bool junkName = IsJunkName(g.NameEN);
            // Wiki 纠正了原有错误 appid（如被误配成 DLC/音乐包）→ 以匹配名为准改名并按名字重探；
            // 首次匹配成功（原 AppId==0，名字可能只是 exe 文件名）同样以匹配名为准，否则按错误旧名探存档
            bool appidCorrected = wikiAppId > 0 && g.AppId > 0 && wikiAppId != g.AppId;
            bool rename = junkName || appidCorrected || g.AppId == 0;
            // 名字是占位/启动器名/被误配 → 用匹配名纠正并重探测；存档路径为空（如手动修正过 exe）→ 也重探测
            if (!string.IsNullOrWhiteSpace(matchedTitle) && (rename || g.SavePaths.Count == 0))
            {
                if (rename) newName = matchedTitle;
                extraPaths = SaveLocator.Detect(rename ? matchedTitle : g.NameEN, g.ExePath, g.InstallDir, appId, _userdataMap);
            }

            bool mergedAway = false;
            await Dispatcher.InvokeAsync(() =>
            {
                // 同 AppId 的另一条记录：同安装根（游戏 exe 与启动器并存）或跨目录重装
                // （卸载后换位置重装，恰一条 exe 仍有效；两份都在视为双装，不处理）
                var sibling = Store.Games.FirstOrDefault(o => !ReferenceEquals(o, g)
                    && o.AppId == appId
                    && (SameInstallRoot(o.InstallDir, g.InstallDir) || (ExeAlive(o) ^ ExeAlive(g))));
                if (sibling != null && SameInstallRoot(sibling.InstallDir, g.InstallDir)
                    && IsLauncherEntry(g) && !IsLauncherEntry(sibling))
                {
                    // 新条目是启动器、旧条目是游戏 → 直接丢弃新条目
                    Store.Games.Remove(g);
                    Store.Save();
                    mergedAway = true;
                    return;
                }

                bool changed = false;
                if (g.AppId != appId) { g.AppId = appId; changed = true; }
                if (newName != null && g.NameEN != newName) { g.NameEN = newName; changed = true; }
                if (extraPaths != null)
                    foreach (var p in extraPaths)
                        if (g.TryAddAutoSavePath(p)) changed = true;   // 静默联网纠正：跳过用户移除过的路径
                if (coverOk && !g.HasCover) { g.CoverFile = coverPath; changed = true; }
                string oldDisplay = g.DisplayName;
                if (!string.IsNullOrWhiteSpace(cn) && string.IsNullOrWhiteSpace(g.NameCN))
                { g.NameCN = cn!; changed = true; }
                // 改名（EN/CN）→ 备份目录名可能变了：先迁移旧目录 zip，再补旧数据找回
                if (changed) BackupService.MigrateBackupDir(g, oldDisplay);
                var (reBak, reSlot) = ReclaimGameData(g);
                if (changed || reBak + reSlot > 0)
                {
                    g.Refresh();
                    Store.Save();
                    if (!silent)
                        ShowToast(reBak + reSlot > 0
                            ? $"已匹配「{g.DisplayName}」，找回 {reBak} 份旧备份" + (reSlot > 0 ? $"、{reSlot} 个玩家档案" : "")
                            : $"已联网匹配「{g.DisplayName}」");
                }

                // 卸载重装 / 多 exe 入口去重：历史备份与玩家镜像挂在旧条目上，默认旧条目保留；
                // 仅当旧条目是启动器而本条是游戏本体时反过来保留本条
                if (sibling != null)
                {
                    if (!IsLauncherEntry(sibling) || IsLauncherEntry(g))
                    {
                        Store.MergeGame(sibling, g);
                        Store.Games.Remove(g);
                        mergedAway = true;
                        if (!silent) ShowToast($"已把重复条目合并到「{sibling.DisplayName}」（卸载重装/多启动入口）");
                    }
                    else
                    {
                        Store.MergeGame(g, sibling);
                        Store.Games.Remove(sibling);
                    }
                    Store.Save();
                }

                // 空库重建的占位卡（无 exe、挂旧备份、AppId=0）走 AppId 合并匹配不上：
                // 学习版重装后手动添加 exe，反查揭晓名字后按名并入占位卡，避免同一游戏两张卡片。
                if (sibling == null && !mergedAway)
                {
                    string? twinName = !string.IsNullOrWhiteSpace(cn) ? cn : matchedTitle;
                    var rebuiltTwin = FindRebuiltPlaceholder(twinName);
                    if (rebuiltTwin != null)
                    {
                        string oldTwinDisplay = rebuiltTwin.DisplayName;
                        Store.MergeGame(rebuiltTwin, g);   // 有效 exe/安装目录/存档路径/备份并集/图标/玩家镜像并入
                        Store.Games.Remove(g);
                        rebuiltTwin.AppId = appId;
                        if (!string.IsNullOrWhiteSpace(newName)) rebuiltTwin.NameEN = newName;
                        if (!string.IsNullOrWhiteSpace(cn) && string.IsNullOrWhiteSpace(rebuiltTwin.NameCN))
                            rebuiltTwin.NameCN = cn!;
                        if (coverOk && !rebuiltTwin.HasCover) rebuiltTwin.CoverFile = coverPath;
                        BackupService.MigrateBackupDir(rebuiltTwin, oldTwinDisplay);
                        rebuiltTwin.RefreshProfiles();
                        rebuiltTwin.RefreshVisibleBackups();
                        rebuiltTwin.Refresh();
                        Store.Save();
                        mergedAway = true;
                        if (!silent)
                            ShowToast($"已并入重建卡片「{rebuiltTwin.DisplayName}」，旧备份与玩家档案保留");
                    }
                }
            });

            // 已作为重复条目并入旧记录：后续不再给它补 Wiki
            if (mergedAway || !Store.Games.Contains(g)) return;

            // appid 确定后按 app: 键再解析一次 Wiki 路径（Lookup 已写入缓存）
            await EnrichWikiAsync(g);
        }
        catch { /* 匹配失败不影响使用，可稍后手动「更新信息」重试 */ }
    }

    /// <summary>占位/无效 ProductName 判定（Unreal 等引擎默认名）。</summary>
    private static bool IsJunkName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var n = SaveLocator.NormalizeName(name);
        string[] junk = { "bootstrappackagedgame", "gamename", "productname", "mygame",
                          "newproject", "projectname", "unity", "unreal" };
        return n.Length < 2 || junk.Contains(n);
    }

    /// <summary>学习版搜索词：占位名时回退到安装目录名。</summary>
    private static string BestSearchTerm(GameEntry g)
    {
        if (!IsJunkName(g.NameEN)) return g.NameEN;
        if (!string.IsNullOrWhiteSpace(g.InstallDir))
        {
            var dir = Path.GetFileName(g.InstallDir.TrimEnd('\\', '/'));
            if (!string.IsNullOrWhiteSpace(dir)) return dir;
        }
        return g.NameEN;
    }

    /// <summary>
    /// 联网匹配候选词（按优先级）：安装目录名/exe 名（FromFile=true，游戏本体文件名，最高信号）
    /// → 英文名（NameEN 可能是上次误配来的 DLC/曲包名，放低优先级并禁止逐词去尾）。
    /// 中文 ProductName（如「巫师3：狂猎」）不适合搜英文 Wiki/Steam，直接跳过。
    /// 每个原始词附带规范化变体（DaysGone→Days Gone、Octopath_Traveler→Octopath Traveler）。
    /// </summary>
    private static List<(string Term, bool FromFile)> SearchTerms(GameEntry g)
    {
        var list = new List<(string Term, bool FromFile)>();
        // dirTerm=安装目录/exe 名：结构上是「游戏名(+版本/DLC 后缀)」，允许逐词去尾兜底；
        // NameEN 可能是误配来的名（如 DLC/曲包名），只做规范化、不截断，避免越截越像另一个游戏
        void Add(string? s, bool dirTerm = false)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            s = s.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(s, "[a-zA-Z]{2}")) return;
            // 无元音的缩略/代号（DGPCM23、TPH）在 Wiki/Steam 都搜不到，直接跳过
            if (!System.Text.RegularExpressions.Regex.IsMatch(s, "[aeiouAEIOU]")) return;
            // 纯通用词目录（如 EnhancedEdition）会误配到任意「XX Enhanced Edition」游戏，跳过
            if (IsGenericTerm(s)) return;
            // 注意：原始串与前一源（如 NameEN）重复时不能整体 return——本源（目录/exe）可能还带着
            // 去尾变体（如 Little_Nightmares_II_Enhanced → "Little Nightmares II"），漏生成就永远搜不到
            if (!list.Any(x => x.Term.Equals(s, StringComparison.OrdinalIgnoreCase))) list.Add((s, dirTerm));
            foreach (var v in SearchTermVariants(s, dirTerm))
                if (!IsGenericTerm(v) && !list.Any(x => x.Term.Equals(v, StringComparison.OrdinalIgnoreCase)))
                    list.Add((v, dirTerm));
        }
        Add(g.NameEN);
        if (!string.IsNullOrWhiteSpace(g.InstallDir))
            Add(Path.GetFileName(g.InstallDir.TrimEnd('\\', '/')), dirTerm: true);
        if (!string.IsNullOrWhiteSpace(g.ExePath))
            Add(Path.GetFileNameWithoutExtension(g.ExePath), dirTerm: true);
        if (list.Count == 0) list.Add((BestSearchTerm(g), false));
        return list;
    }

    /// <summary>发布目录常见通用词（版本/打包用语），纯由它们组成的候选词不具游戏区分度，禁用。</summary>
    private static readonly HashSet<string> GenericTermWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "enhanced", "edition", "version", "remastered", "remake", "definitive", "complete",
        "update", "updated", "upgrade", "patch", "crack", "cracked", "repack", "release",
        "setup", "install", "installer", "redist", "redistributable", "bin", "binaries",
        "binary", "data", "game", "games", "new", "old", "temp", "tmp", "backup", "full",
        "final", "build", "codex", "dodi", "fitgirl", "goldberg",
    };

    /// <summary>候选词是否纯由通用词组成（如 "Enhanced Edition"、"Setup"）——≤3 词时才有意义。</summary>
    private static bool IsGenericTerm(string s)
    {
        var toks = System.Text.RegularExpressions.Regex
            .Split(s, @"[^a-zA-Z0-9]+")
            .Where(t => t.Length > 0)
            .ToArray();
        if (toks.Length == 0 || toks.Length > 3) return false;
        return toks.All(t => GenericTermWords.Contains(t));
    }

    /// <summary>
    /// 搜索词规范化变体：去版本号尾巴（Kena...v1.04）→ 驼峰/数字边界插空格（DaysGone→Days Gone、
    /// richman10→richman 10）→ ._-: 转空格（Octopath_Traveler→Octopath Traveler）；
    /// allowTailDrop：规范化后 ≥4 个词时逐词去尾（目录名混入 DLC/版本词兜底，
    /// 如 Two Point Hospital Culture Shock → … → Two Point Hospital）。
    /// </summary>
    private static IEnumerable<string> SearchTermVariants(string s, bool allowTailDrop)
    {
        static string Rx(string i, string p, string r, bool ignoreCase = false) =>
            System.Text.RegularExpressions.Regex.Replace(i, p, r,
                ignoreCase ? System.Text.RegularExpressions.RegexOptions.IgnoreCase
                           : System.Text.RegularExpressions.RegexOptions.None);
        string t = Rx(s, @"[._\s-]*v?\d+(\.\d+)+[a-z0-9]*\s*$", "", ignoreCase: true);
        t = Rx(t, @"[._\s-]*(win64|win32|win)?[._\s-]*shipping\s*$", "", ignoreCase: true);
        t = Rx(t, @"([a-z0-9])([A-Z])", "$1 $2");   // 驼峰边界（区分大小写，勿开 IgnoreCase）
        t = Rx(t, @"([a-zA-Z])(\d)", "$1 $2");
        t = Rx(t, @"[._\-:]+", " ");
        t = Rx(t, @"\s+", " ").Trim();
        if (t.Length > 1 && !t.Equals(s, StringComparison.OrdinalIgnoreCase)) yield return t;
        if (allowTailDrop)
        {
            static bool IsNumToken(string w)
            {
                if (w.All(char.IsDigit)) return true;
                return w.ToLowerInvariant() is "i" or "ii" or "iii" or "iv" or "v" or "vi"
                    or "vii" or "viii" or "ix" or "x" or "xi" or "xii" or "xiii" or "xiv" or "xv";
            }
            var toks = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // 长词优先（break 即停），2 词仅在更长词全部落空后兜底（如 FATAL_FRAME_MOBW → "fatal frame"）
            for (int n = toks.Length - 1; n >= 2; n--)
            {
                // 即将丢掉的词是数字/罗马数字（续代标记，如 Alan Wake 2、Little Nightmares II）
                // → 停在更长的词上，否则会误配同名初代（Alan Wake 2 → Alan Wake 2012）
                if (IsNumToken(toks[n])) break;
                yield return string.Join(' ', toks.Take(n));
            }
        }
    }

    /// <summary>匹配置信度：归一化后名称相等，或标题包含搜索词（防止搜索第一条结果乱配）。
    /// 单词搜索词（如 Snes9x/Wrath）不允许包含命中——结果可能只是标题的一部分
    /// （如 RetroArch - Snes9x DLC、Shadows on the Vatican ...: Wrath）；多词才允许。
    /// 仅允许「标题包含搜索词」方向（简称→全名，如 Kena → Kena: Bridge of Spirits）；
    /// 反向「搜索词包含标题」会把串了 DLC/曲包后缀的误配长名（如 Beat Saber … Alan Walker
    /// → Beat Saber）当成命中，目录名后缀场景由 SearchTerms 逐词去尾变体覆盖。</summary>
    private static bool IsConfident(string term, string title)
    {
        var a = SaveLocator.NormalizeName(term);
        var b = SaveLocator.NormalizeName(title);
        if (a.Length < 3 || b.Length < 3) return false;
        if (a == b) return true;
        if (!b.Contains(a)) return false;   // 只接受标题比搜索词更全（标题包含词）
        int tokens = term.Split(new[] { ' ', '-', ':', '_', '/', '\\' },
            StringSplitOptions.RemoveEmptyEntries).Length;
        return tokens >= 2;
    }

    // ── 扫描文件夹（批量导入学习版）─────────────────────
    private async void OnScanFolder(object s, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择游戏根目录（自动扫描其下 1–2 层子目录的游戏 exe）",
            ShowNewFolderButton = false,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        SetBusy(true);
        int added = 0;
        int reBak = 0, reSlot = 0;   // 重新入库找回统计
        GameEntry? firstAdded = null;
        try
        {
            var cands = await Task.Run(() => FolderScanner.Scan(dlg.SelectedPath));
            foreach (var c in cands)
            {
                // 已入库（相同 exe 或相同安装目录）则跳过
                if (Store.Games.Any(g => g.ExePath.Equals(c.Exe, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(g.InstallDir) && c.Dir.Equals(g.InstallDir, StringComparison.OrdinalIgnoreCase))))
                    continue;

                var g = new GameEntry
                {
                    Type = "Local",
                    NameEN = c.ProductName,
                    ExePath = c.Exe,
                    InstallDir = c.Dir,
                    AddedAt = DateTime.Now,
                };
                var paths = await Task.Run(() => SaveLocator.Detect(g.NameEN, c.Exe, c.Dir, 0, null));
                foreach (var p in paths) g.SavePaths.Add(p);
                var icon = await Task.Run(() => IconExtractor.ExtractToPng(c.Exe, Store.IconFileFor(g.Id)));
                if (icon != null) g.IconFile = icon;

                Store.Games.Add(g);
                var (rb, rs) = ReclaimGameData(g);   // 移除后重新扫到：找回旧备份与玩家档案
                reBak += rb; reSlot += rs;
                added++;
                firstAdded ??= g;
                _ = MatchLocalGameAsync(g);
                _ = EnrichWikiAsync(g);
            }
            if (added > 0) { Store.Save(); UpdateRecentAddedDot(); RevealGame(firstAdded!); }
            string reclaimMsg = reBak + reSlot > 0
                ? $"；已找回 {reBak} 份旧备份" + (reSlot > 0 ? $"、{reSlot} 个玩家档案" : "")
                : "";
            ShowToast((added > 0
                ? $"扫描完成：新增 {added} 款游戏"
                : $"扫描完成：没有发现新游戏（已忽略工具/卸载器等）") + reclaimMsg);
            if (added > 0)
                CollectionViewSource.GetDefaultView(Store.Games).Refresh();
        }
        catch (Exception ex)
        {
            ShowToast("扫描失败：" + ex.Message, true);
        }
        finally { SetBusy(false); }
    }

    // ── 列表/筛选/搜索 ──────────────────────────────────
    /// <summary>点击列表空白处显示海报，并阻止 ListBox 取消选中。</summary>
    private void OnGamesListPreviewMouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject dep) return;
        // 滚动条/滑块同样位于 ListBox 模板内部，必须放行：
        // 否则点住拖动滚动条会被下面 e.Handled = true 吞掉，滚动条失效。
        if (FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(dep) != null) return;
        // 行内交互按钮（收藏星标）：不改变海报/详情状态，也不吞事件，交由按钮自身处理。
        // 否则在"无选中+海报"态（首次启动/删除游戏后）点星标：海报被强制收起，而 Button
        // 吞掉鼠标事件使 ListBox 不产生选中，SelectedGame 仍为 null，右侧残留无名空详情。
        if (FindAncestor<System.Windows.Controls.Button>(dep) != null) return;
        var item = ItemsControl.ContainerFromElement(GamesList, dep) as ListBoxItem;
        if (item == null)
        {
            e.Handled = true;
            ShowPoster = true;
        }
        else
        {
            // 点到游戏项：即使该项已选中（SelectionChanged 不触发）也要收起海报，露出详情
            ShowPoster = false;
        }
    }

    private void OnGameSelected(object s, SelectionChangedEventArgs e)
    {
        _suppressAutoToast = true;
        SelectedGame = GamesList.SelectedItem as GameEntry;   // 海报/详情的显隐由 SelectedGame 派生
        SelectedGame?.RefreshProfiles();
        RefreshActiveSlotLiveStateAsync(SelectedGame);
        // 切换游戏即刷新云状态：先按登记表即时点亮（登记表落盘，重启后保留上次状态），
        // 再后台用 1 轮列表请求对账该游戏全部备份（每游戏 10 秒防抖，失败静默）
        if (SelectedGame is { } gSel && CloudSyncService.Configured)
        {
            CloudSyncService.RefreshCloudFlags(gSel);
            _ = VerifyGameCloudOnSwitchAsync(gSel);
        }
        Dispatcher.BeginInvoke(new Action(() => _suppressAutoToast = false), DispatcherPriority.Loaded);
    }

    /// <summary>游戏切换后批量核验的防抖表：同一游戏 10 秒内只核验一次（快速划列表不刷请求）。</summary>
    private readonly Dictionary<string, DateTime> _gameCloudCheckedAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>切换游戏后批量核验该游戏云端状态（失败静默保持登记表现状）。</summary>
    private async Task VerifyGameCloudOnSwitchAsync(GameEntry g)
    {
        if (_gameCloudCheckedAt.TryGetValue(g.DisplayName, out var last) &&
            (DateTime.Now - last).TotalSeconds < 10) return;
        _gameCloudCheckedAt[g.DisplayName] = DateTime.Now;
        try { await CloudSyncService.VerifyGameCloudStatusAsync(g); }
        catch { /* 断网/服务器抖动：保持登记表现状，不打扰 */ }
    }

    private void OnSearchChanged(object s, TextChangedEventArgs e) => ApplyFilter();

    private void OnClearSearch(object s, RoutedEventArgs e) => SearchBox.Clear();

    /// <summary>列表项星标：切换收藏并刷新排序（收藏置顶）。</summary>
    private void OnToggleFavorite(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is not GameEntry g) return;
        g.Favorite = !g.Favorite;
        Store.Save();
        CollectionViewSource.GetDefaultView(Store.Games).Refresh();
    }

    /// <summary>双击列表项运行游戏（双击星标等按钮时不触发）。</summary>
    private void OnListItemDoubleClick(object s, RoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d &&
            FindAncestor<System.Windows.Controls.Button>(d) != null) return;
        if (SelectedGame is GameEntry g) LaunchGame(g);
    }

    /// <summary>点击详情页头部（封面/名称整块）：在左侧列表选中并滚动到当前游戏。
    /// 操作按钮点击时 ButtonBase 已将 MouseLeftButtonUp 标记 Handled，不会冒泡到这里。</summary>
    private void OnLocateInList(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is GameEntry g)
            RevealGame(g);
    }

    /// <summary>沿可视化/逻辑树向上查找指定类型的祖先。</summary>
    private static T? FindAncestor<T>(DependencyObject o) where T : DependencyObject
    {
        while (o != null)
        {
            if (o is T t) return t;
            o = System.Windows.Media.VisualTreeHelper.GetParent(o)
                ?? LogicalTreeHelper.GetParent(o);
        }
        return null;
    }

    private void OnFilterChanged(object s, RoutedEventArgs e)
    {
        if (FiltAll == null || FiltSteam == null || FiltLocal == null) return;   // XAML 初始化期间触发
        _filter = FiltSteam.IsChecked == true ? "steam" : FiltLocal.IsChecked == true ? "local" : "all";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (SearchBox != null) _keyword = SearchBox.Text.Trim().ToLower();
        ICollectionView view = CollectionViewSource.GetDefaultView(Store.Games);
        view.Filter = o =>
        {
            if (o is not GameEntry g) return false;
            if (_filter == "steam" && !g.IsSteam) return false;
            if (_filter == "local" && g.IsSteam) return false;
            if (_keyword.Length == 0) return true;
            return g.DisplayName.ToLower().Contains(_keyword) || g.NameEN.ToLower().Contains(_keyword);
        };
        // 排序：收藏置顶为第一优先级；CustomSort 与 SortDescriptions 互斥，全部逻辑集中在此
        view.SortDescriptions.Clear();
        if (view is ListCollectionView lcv)
        {
            lcv.CustomSort = Comparer<GameEntry>.Create((a, b) =>
            {
                if (a.Favorite != b.Favorite) return a.Favorite ? -1 : 1;
                int r;
                if (_sortMode == 0)
                {
                    // 最近游玩：按最后关闭时间（无记录回退 Steam 打开时间）；无数据恒垫底，不随方向翻转
                    DateTime aPlay = a.LastActiveAt, bPlay = b.LastActiveAt;
                    bool aHas = aPlay > DateTime.MinValue, bHas = bPlay > DateTime.MinValue;
                    if (aHas != bHas) return aHas ? -1 : 1;
                    r = aPlay.CompareTo(bPlay);
                    if (_sortDirDesc) r = -r;
                    if (r == 0) r = string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture);
                }
                else if (_sortMode == 2)
                {
                    // 最近添加：入库时间新→旧，旧数据（无 AddedAt）恒垫底
                    bool aHas = a.AddedAt > DateTime.MinValue, bHas = b.AddedAt > DateTime.MinValue;
                    if (aHas != bHas) return aHas ? -1 : 1;
                    r = a.AddedAt.CompareTo(b.AddedAt);
                    if (_sortDirDesc) r = -r;
                    if (r == 0) r = string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture);
                }
                else
                {
                    r = string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture);
                    if (_sortDirDesc) r = -r;
                }
                return r;
            });
        }
        GamesList.ItemsSource = view;
        UpdateSideFooters();
    }

    /// <summary>导入后定位：被筛选隐藏时切回"全部"，选中并滚动到该项，避免新游戏沉没在列表底部。</summary>
    private void RevealGame(GameEntry g)
    {
        if (!GamesList.Items.Contains(g) && FiltAll != null)
            FiltAll.IsChecked = true;   // 触发 OnFilterChanged → ApplyFilter
        GamesList.SelectedItem = g;
        GamesList.ScrollIntoView(g);
        GamesList.UpdateLayout();
        GamesList.ScrollIntoView(g);    // 布局后再滚一次，防虚拟化偏差
    }

    /// <summary>侧栏拖动结束：持久化宽度。</summary>
    private void OnSidebarResized(object s, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        Store.Settings.SidebarWidth = SideCol.Width.Value;
        Store.SaveSettings();
    }

    /// <summary>排序 chip 切换（最近游玩/名称/最近添加）。</summary>
    private void OnSortChipChanged(object s, RoutedEventArgs e)
    {
        if (GamesList == null) return;   // XAML 初始化期间触发
        _sortMode = RSortName.IsChecked == true ? 1 : RSortAdded.IsChecked == true ? 2 : 0;
        Store.Settings.SortMode = _sortMode;
        if (_sortMode == 2)
        {
            // 查看过最近添加 → 蓝点已读
            Store.Settings.RecentAddedSeenAt = DateTime.Now;
            UpdateRecentAddedDot();
        }
        Store.SaveSettings();
        UpdateSortArrow();
        ApplyFilter();
    }

    /// <summary>近 7 天内有新导入且未查看时，显示「最近添加」chip 上的小蓝点。</summary>
    private void UpdateRecentAddedDot()
    {
        if (RecentAddedDot == null) return;
        bool hasNew = Store.Games.Any(g => g.AddedAt > Store.Settings.RecentAddedSeenAt
                                        && g.AddedAt > DateTime.Now.AddDays(-7));
        RecentAddedDot.Visibility = hasNew ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>在已选中的排序 chip 上再按下：RadioButton 不会触发 Checked，这里直接翻转升降序。
    /// 用 Preview 按下事件——此时 IsChecked 还是点击前状态，可据此区分“选方式”与“换方向”。</summary>
    private void OnSortChipReclick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var rb = (System.Windows.Controls.RadioButton)sender;
        if (rb.IsChecked != true) return;   // 未选中 chip：正常走 Checked 切换排序方式
        _sortDirDesc = !_sortDirDesc;
        Store.Settings.SortDesc = _sortDirDesc;
        Store.SaveSettings();
        UpdateSortArrow();
        ApplyFilter();
    }

    /// <summary>当前排序 chip 上显示方向箭头（↓降序 / ↑升序），其余两个隐藏。</summary>
    private void UpdateSortArrow()
    {
        if (AddedDir == null || PlayedDir == null || NameDir == null) return;
        string arrow = _sortDirDesc ? "↓" : "↑";
        AddedDir.Text = arrow; PlayedDir.Text = arrow; NameDir.Text = arrow;
        AddedDir.Visibility = _sortMode == 2 ? Visibility.Visible : Visibility.Collapsed;
        PlayedDir.Visibility = _sortMode == 0 ? Visibility.Visible : Visibility.Collapsed;
        NameDir.Visibility = _sortMode == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 列表卡片悬停效果（纯 2D：放大上浮 + rotateY 近似倾斜 + 金属扫光） ──────
    /// <summary>rotateY ±6° 的 2D 近似参数：光标移到边缘时卡片横向收缩的最大比例。
    /// 真 rotateY(6°) 的均匀收窄只有 cos6°≈0.5%，真正的立体感来自透视近大远小，
    /// 在本卡宽（约 280px、perspective≈1000）下边缘差约 2~3%；取 2.2% 内收配合
    /// 受压侧方向暗角，观感与 6° 透视一致，开销仅一个 ScaleTransform。</summary>
    private const double CardTiltMaxCompress = 0.022;

    /// <summary>鼠标追踪的最小时间片（ms）：高轮询鼠标下避免无意义的过密属性写入。</summary>
    private const int CardMoveThrottleMs = 8;

    /// <summary>每张卡解析一次的特效元素缓存（存在 CardRoot.Tag 上）。
    /// 模板名称范围解析不便宜，绝不能在 MouseMove 里每次 FindName 六个名字。</summary>
    private sealed class CardFxState
    {
        public Border Face = null!;
        public ScaleTransform Tilt = null!;
        public Border Glow = null!;
        public RadialGradientBrush GlowBrush = null!;
        public Border Shade = null!;
        public LinearGradientBrush ShadeBrush = null!;
        public double GlowRadius;
        public double KnownWidth = -1;
        public int LastMoveTick;
        public bool GlowLit;

        /// <summary>view.Refresh() 拆除容器时名称范围已销毁，FindName 返回 null/抛异常，
        /// 此时卡片马上丢弃，返回 null 即可，不做任何复位。</summary>
        public static CardFxState? TryCreate(FrameworkElement root)
        {
            try
            {
                if (root.FindName("CardFace") is not Border face) return null;
                if (root.FindName("CardTilt") is not ScaleTransform tilt) return null;
                if (root.FindName("GlowLayer") is not Border glow) return null;
                if (root.FindName("GlowBrush") is not RadialGradientBrush glowBrush) return null;
                if (root.FindName("TiltShade") is not Border shade) return null;
                if (root.FindName("TiltShadeBrush") is not LinearGradientBrush shadeBrush) return null;
                return new CardFxState
                {
                    Face = face, Tilt = tilt, Glow = glow, GlowBrush = glowBrush,
                    Shade = shade, ShadeBrush = shadeBrush,
                };
            }
            catch { return null; }
        }
    }

    private static CardFxState? GetCardFx(FrameworkElement root)
    {
        if (root.Tag is CardFxState existing) return existing;
        var created = CardFxState.TryCreate(root);
        if (created != null) root.Tag = created;
        return created;
    }

    private void OnCardMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not FrameworkElement root) return;
        // 悬停卡片的 ListBoxItem 提到最高 ZIndex：放大的边缘与向下投影要盖在
        // 相邻卡片之上，否则按列表顺序渲染时下方阴影会被下一张不透明卡片裁掉
        if (FindAncestor<ListBoxItem>(root) is { } hoverItem && System.Windows.Controls.Panel.GetZIndex(hoverItem) != 1)
            System.Windows.Controls.Panel.SetZIndex(hoverItem, 1);

        CardFxState? fx = GetCardFx(root);
        if (fx is null || fx.Face.ActualWidth <= 0 || fx.Face.ActualHeight <= 0) return;

        // 时间片节流：只跳过中间帧，ZIndex 等逻辑在上面已先处理
        int tick = Environment.TickCount;
        if (tick - fx.LastMoveTick < CardMoveThrottleMs) return;
        fx.LastMoveTick = tick;

        // 归一化坐标直接相对卡片本体（CardFace），不再需要任何 padding 反算
        System.Windows.Point q = e.GetPosition(fx.Face);
        double nx = Math.Clamp(q.X / fx.Face.ActualWidth, 0, 1);
        double ny = Math.Clamp(q.Y / fx.Face.ActualHeight, 0, 1);

        // ── rotateY 近似：光标侧「向内下压」，远侧边缘锚定不动（读作翘起） ──
        double away = Math.Abs(nx * 2.0 - 1.0);   // 卡中缝=0，左右边缘=1
        // 离开复位动画可能还在跑：停掉动画才能即时写值，保证跟手
        fx.Tilt.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        fx.Face.BeginAnimation(UIElement.RenderTransformOriginProperty, null);
        fx.Tilt.ScaleX = 1.0 - CardTiltMaxCompress * away;
        // 原点取远离光标的对侧（nx=0 光标在左 → 锚定右缘 1.0；nx=1 → 锚定左缘 0），
        // 随 nx 连续插值，过中缝不跳变
        fx.Face.RenderTransformOrigin = new System.Windows.Point(1.0 - nx, 0.5);

        // ── 受压侧方向暗角：暗边始终在光标一侧，浓度随偏离中缝增大 ──
        fx.Shade.BeginAnimation(UIElement.OpacityProperty, null);
        fx.Shade.Opacity = 0.55 * away;           // 端色 alpha≈13%，全开实际压暗约 7%
        if (nx <= 0.5)
        {
            fx.ShadeBrush.StartPoint = new System.Windows.Point(0, 0);
            fx.ShadeBrush.EndPoint = new System.Windows.Point(1, 0);
        }
        else
        {
            fx.ShadeBrush.StartPoint = new System.Windows.Point(1, 0);
            fx.ShadeBrush.EndPoint = new System.Windows.Point(0, 0);
        }

        // ── 金属扫光：绝对像素正圆，圆心跟随鼠标；半径随卡宽变化时重取一次 ──
        if (!fx.KnownWidth.Equals(fx.Face.ActualWidth))
        {
            fx.KnownWidth = fx.Face.ActualWidth;
            fx.GlowRadius = Math.Max(110.0, fx.Face.ActualWidth * 0.45);
            fx.GlowBrush.RadiusX = fx.GlowRadius;
            fx.GlowBrush.RadiusY = fx.GlowRadius;
        }
        fx.GlowBrush.Center = q;
        fx.GlowBrush.GradientOrigin = q;

        // 扫光淡入只在本次悬停的第一次有效移动触发：每次移动都重启会让动画反复
        // 从当前值起跑、永远到不了 1，光晕长期偏暗且抖动。圆心更新本身是即时的。
        if (!fx.GlowLit)
        {
            fx.GlowLit = true;
            fx.Glow.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }
    }

    private void OnCardMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not FrameworkElement root) return;
        // 离开时把 ListBoxItem 的 ZIndex 复位，恢复按列表顺序渲染
        if (FindAncestor<ListBoxItem>(root) is { } leaveItem && System.Windows.Controls.Panel.GetZIndex(leaveItem) != 0)
            System.Windows.Controls.Panel.SetZIndex(leaveItem, 0);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // 放大/上浮/阴影：直接对命名对象复位（不经二次名称解析，容器拆除时也安全）
        ScaleTransform? scale; TranslateTransform? lift; DropShadowEffect? shadow;
        try
        {
            scale = root.FindName("CardScale") as ScaleTransform;
            lift = root.FindName("CardLift") as TranslateTransform;
            shadow = root.FindName("CardShadow") as DropShadowEffect;
        }
        catch { return; }
        if (scale != null)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, ResetAnim(1, ease));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, ResetAnim(1, ease));
        }
        lift?.BeginAnimation(TranslateTransform.YProperty, ResetAnim(0, ease));
        if (shadow != null)
        {
            shadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, ResetAnim(0, ease));
            shadow.BeginAnimation(DropShadowEffect.ShadowDepthProperty, ResetAnim(0, ease));
            shadow.BeginAnimation(DropShadowEffect.OpacityProperty, ResetAnim(0.05, ease));
        }

        // 倾斜/暗角/扫光复位：未触发过 MouseMove 的卡没有缓存状态，全部本就是初值，跳过
        if (root.Tag is CardFxState fx)
        {
            // ScaleX 与变换原点同时回弹：边缘沿直线平滑收回，不会出现单侧先缩后放的抖动
            fx.Tilt.BeginAnimation(ScaleTransform.ScaleXProperty, ResetAnim(1, ease));
            fx.Face.BeginAnimation(UIElement.RenderTransformOriginProperty,
                new PointAnimation(new System.Windows.Point(0.5, 0.5), TimeSpan.FromMilliseconds(380))
                { EasingFunction = ease });
            fx.Shade.BeginAnimation(UIElement.OpacityProperty, ResetAnim(0, ease));
            fx.GlowLit = false;   // 清除「已亮起」标记，下次进入重新淡入一次
            fx.Glow.BeginAnimation(UIElement.OpacityProperty, ResetAnim(0, ease));
        }
    }

    private static DoubleAnimation ResetAnim(double to, CubicEase ease)
        => new(to, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease };

    private void UpdateSideFooters()
    {
        ICollectionView view = CollectionViewSource.GetDefaultView(Store.Games);
        int visible = 0;
        foreach (var item in view) visible++;
        SideCount.Text = $"共 {visible} 款游戏";
        long totalBytes = Store.Games.Sum(g => g.Backups.Sum(b => b.Size));
        double gb = totalBytes / 1024.0 / 1024.0 / 1024.0;
        double mb = totalBytes / 1024.0 / 1024.0;
        SideSize.Text = gb >= 1 ? $"备份占用 {gb:F1} GB" : $"备份占用 {mb:F1} MB";
    }

    // ── 存档路径操作 ────────────────────────────────────
    private void OnAddSavePath(object s, RoutedEventArgs e)
    {
        if (SelectedGame is not GameEntry g) return;
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = $"选择「{g.DisplayName}」的存档文件夹",
            ShowNewFolderButton = false,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        if (g.SavePaths.Any(p => p.Path.Equals(dlg.SelectedPath, StringComparison.OrdinalIgnoreCase)))
        {
            ShowToast("该路径已存在", true);
            return;
        }
        g.AddManualSavePath(dlg.SelectedPath);   // 手动添加同时解除"曾移除"标记
        Store.Save();
        ShowToast("已添加存档路径");
        // 与历史备份层级不一致时只提示不阻止（新层级的新备份照常工作，旧备份还原会被拦截）
        string? levelWarn = BackupService.CheckPathLevelConflict(g, dlg.SelectedPath);
        if (levelWarn != null)
            MessageBox.Show(levelWarn, "存档位置层级提示", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnOpenSavePath(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is not SavePathEntry p) return;
        if (Directory.Exists(p.Path))
            Process.Start("explorer.exe", p.Path);
        else
            ShowToast("目录不存在（游戏可能还没生成存档）", true);
    }

    private void OnRemoveSavePath(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is not SavePathEntry p) return;
        if (SelectedGame is GameEntry g)
        {
            g.RemoveSavePath(p);   // 记入忽略名单：后台自动探测/Wiki 补全不会再加回（手动添加或点「自动获取」可恢复）
            Store.Save();
            ShowToast("已移除该存档位置，以后不会再自动添加");
        }
    }

    // ── 备份/还原 ───────────────────────────────────────
    private async void OnBackupNow(object s, RoutedEventArgs e)
    {
        if (SelectedGame is not GameEntry g) return;
        bool hot = BackupService.IsGameRunning(g);
        if (hot && MessageBox.Show(
                $"「{g.DisplayName}」正在运行：将备份存档目录中" +
                "「最近一次游戏内保存」的进度，被游戏占用的文件会自动跳过。\n\n继续备份？",
                "运行中热备份", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.Yes) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            BackupRecord r;
            int skipped = 0;
            if (hot)
                r = await Task.Run(() => BackupService.Create(g, "手动热备份", true, out skipped));
            else
                r = await Task.Run(() => BackupService.Create(g, "手动备份"));
            RefreshAfterChange(g);
            ShowToast(hot && skipped > 0
                ? $"备份完成「{r.Note}」 {r.SizeText}（跳过 {skipped} 个占用文件）"
                : $"备份完成「{r.Note}」 {r.SizeText}");
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, true);
        }
        finally { SetBusy(false); }
    }

    private async void OnRestoreBackup(object s, RoutedEventArgs e)
    {
        if (SelectedGame is GameEntry g &&
            (s as FrameworkElement)?.DataContext is BackupRecord r)
            await RestoreAsync(g, r);
    }

    private async void OnRestoreLatest(object s, RoutedEventArgs e)
    {
        if (SelectedGame is not GameEntry g) return;
        // 只在当前玩家的备份里找最近一份（VisibleBackups 已按新→旧，取首条即可）
        var r = g.VisibleBackups.OrderByDescending(b => b.Time).FirstOrDefault();
        if (r == null)
        {
            ShowToast(g.HasProfileSlots
                ? $"玩家「{g.ActiveProfile}」还没有备份记录"
                : "暂无备份记录", true);
            return;
        }
        await RestoreAsync(g, r);
    }

    private async Task RestoreAsync(GameEntry g, BackupRecord r)
    {
        // 短路：当前进度与目标备份完全一致时不弹框、不建快照、不重复解压
        // 云端下载包路径表是源机的，严格比对必不中，补内容口径（相对路径+大小+mtime 全等）
        bool alreadySame;
        try
        {
            alreadySame = await Task.Run(() =>
                BackupService.IsLiveSameAsSnapshot(g, r) || BackupService.IsLiveSameAsSnapshotContent(g, r));
        }
        catch { alreadySame = false; }
        if (alreadySame)
        {
            ShowToast("当前进度与这份备份完全一致，无需还原");
            return;
        }
        var (confirm, purge) = ConfirmRestore.Show(this, g.DisplayName, r);
        if (!confirm) return;
        SetBusy(true);
        try
        {
            await Task.Run(() => BackupService.Restore(g, r, purge));
            // Restore 已落还原标记（📍=💾 时显示「存档已还原」，新备份产生自动解除），这里只需落盘
            try { Store.Save(); } catch { }
            RefreshAfterChange(g);
            ShowToast(purge ? "严格还原完成（已清理快照外的新文件）" : "还原完成");
        }
        catch (Exception ex)
        {
            ShowToast("还原失败：" + ex.Message, true);
        }
        finally { SetBusy(false); }
    }

    /// <summary>玩家筹码第二行状态点击：
    /// 「本地有较新备份」→ 直接还原本机最新一份（仍弹还原确认框）；
    /// 「云端有较新备份」→ 确认后只把云端最新下载入库（带百分比，不自动还原）；
    /// 入库后状态自然变成「本地有较新备份」，想还原再点那一行。</summary>
    private async void OnSlotStatusClick(object sender, RoutedEventArgs e)
    {
        if (SelectedGame is not GameEntry g) return;
        if ((sender as FrameworkElement)?.DataContext is not ProfileSlotVm vm) return;
        if (_busyDepth > 0 || string.IsNullOrEmpty(vm.StatusAction)) return;
        string profile = vm.Name;

        if (vm.StatusAction == "restoreLocal")
        {
            var r = LatestRestorable(g, profile);
            if (r == null) { ShowToast("没有找到可还原的备份", true); return; }
            await RestoreAsync(g, r);
            return;
        }

        if (vm.StatusAction == "restoreCloud")
        {
            var pick = MessageBox.Show(this,
                $"云端有玩家「{profile}」更新的存档。\n\n" +
                "「是」= 下载云端最新存档到本机（只下载，不会改动当前存档）\n" +
                "下载完成后这行会变成「本地有较新备份」，想应用再点击还原\n" +
                "「否」= 暂不下载",
                "下载云端存档", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (pick != MessageBoxResult.Yes) return;
            SetBusy(true);
            try
            {
                BusyText.Text = "正在查询云端存档…";
                var progress = new Progress<double>(p =>
                    BusyText.Text = $"正在下载云端存档… {p * 100:0}%");
                var res = await CloudSyncService.PreparePlayerFromCloud(g, profile, default, progress);
                if (!res.HasRemote || res.Record == null)
                {
                    ShowToast("云端没有找到可下载的存档", true);
                    return;
                }
                RefreshAfterChange(g);   // 新下载的备份先进列表，筹码状态即时重核（云端项消失，转本地较新）
                ShowToast(res.WasAlreadyLocal
                    ? "这份云端存档本机已经有了，无需重复下载"
                    : "云端存档已下载到本机（未还原），需要时点「本地有较新备份」");
            }
            catch (Exception ex)
            {
                ShowToast("下载云端存档失败：" + ex.Message, true);
                return;
            }
            finally { SetBusy(false); }
        }
    }

    /// <summary>该玩家名下打包时间最新、链路完整可还原的备份。</summary>
    private static BackupRecord? LatestRestorable(GameEntry g, string profile)
        => g.Backups.Where(b => (b.Profile ?? "") == profile)
            .OrderByDescending(b => b.Time)
            .FirstOrDefault(b => BackupService.IsRestorable(g, b));

    private async void OnDeleteBackup(object s, RoutedEventArgs e)
    {
        if (SelectedGame is not GameEntry g ||
            (s as FrameworkElement)?.DataContext is not BackupRecord r) return;
        if (_busyDepth > 0) return;

        // 云端处理决策：未配置云同步 / 纯本机备份（从未上云）直接删，不打扰；
        // 有云端副本时弹对话框，checkbox 默认勾选状态来自上次用户选择（持久化）
        bool delCloud = false, cloudCopy = false;
        if (CloudSyncService.Configured && CloudSyncService.HasKnownRemoteCopy(g, r.File))
        {
            cloudCopy = true;
            var choice = new CloudDeleteDialog(this, g.DisplayName, r.SavedAtMainText, r.SizeText)
            { Owner = this };
            if (choice.ShowDialog() != true) return;                 // 取消
            delCloud = choice.DeleteCloudToo;
            // 用户勾选状态写回设置，下次沿用（不再有"记住选择就不再问"——每次都弹）
            Store.Settings.CloudDelWithCloud = delCloud;
            Store.SaveSettings();
        }

        SetBusy(true);
        try
        {
            BackupService.Delete(g, r);                                  // 本地删除 + 黑名单兜底
            int cloudDeleted = 0;
            if (delCloud)
                cloudDeleted = await CloudSyncService.DeleteRemoteCopiesAsync(g, r.File);
            RefreshAfterChange(g);
            ShowToast(delCloud && cloudDeleted > 0
                ? $"已删除该备份（云端副本已同步删除）"
                : cloudCopy
                    ? "已删除该备份（云端副本已保留，可随时在 ☁ 云端备份中查看）"
                    : "已删除该备份");
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, true);
        }
        finally { SetBusy(false); }
    }

    /// <summary>详情页「Wiki 获取失败 · 点击重试」：强制联网重取该游戏的 Wiki 存档规则。</summary>
    private async void OnWikiRetry(object s, RoutedEventArgs e)
    {
        if (SelectedGame is not GameEntry g || _busyDepth > 0) return;
        SetBusy(true);
        try
        {
            int added = await EnrichWikiAsync(g, forceRefresh: true);
            ShowToast(added > 0
                ? $"Wiki 规则已更新，新增 {added} 条存档路径"
                : g.WikiFailed
                    ? "Wiki 仍获取失败：请检查网络后稍后再试"
                    : "Wiki 无更新（该游戏规则已是最新）");
        }
        finally { SetBusy(false); }
    }

    /// <summary>启动后检查备份目录迁移：上次改过备份目录（PrevBackupDir 有值）且旧目录里
    /// 仍有当前记录指向的 zip → 询问用户「移动认领」到新目录（保留记录与增量链）。</summary>
    private async void CheckBackupMigration()
    {
        try
        {
            string prev = Store.Settings.PrevBackupDir;
            if (string.IsNullOrWhiteSpace(prev)) return;
            var (missing, claimable) = await Task.Run(() => BackupService.ScanReclaimFromRoot(prev));
            if (claimable.Count == 0)
            {
                if (missing == 0)   // 旧目录已无可迁移内容（用户手动处理过）：清掉迁移标记
                { Store.Settings.PrevBackupDir = ""; Store.SaveSettings(); }
                return;
            }
            var r = MessageBox.Show(this,
                $"备份目录已改为：\n{Store.BackupRoot}\n\n有 {claimable.Count} 份备份文件仍留在旧目录：\n{prev}\n\n移动到新目录？（保留全部备份记录与增量链）",
                "备份目录迁移", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (r != MessageBoxResult.Yes) return;
            int moved = await Task.Run(() => BackupService.ReclaimFromRoot(claimable));
            Store.Settings.PrevBackupDir = "";
            Store.SaveSettings();
            foreach (var game in claimable.Select(c => c.Game).Distinct())
                RefreshAfterChange(game);
            ShowToast($"已从旧目录认领 {moved} 份备份");
        }
        catch { /* 迁移检查失败不影响启动，下次再试 */ }
    }

    // ── 玩家存档（多人各玩各的：独立存档 + 独立备份）──────
    private async void OnNewProfile(object s, RoutedEventArgs e)
    {
        if (SelectedGame is not GameEntry g) return;
        if (ProfileService.DisabledReason(g) is string cloudReason) { ShowToast(cloudReason, true); return; }
        string prompt = "给新玩家起个名字（如：小红）：\n\n· 当前玩家的进度会先自动打包一份备份，再给新玩家留一份起点备份\n· 创建后请启动游戏，在主菜单选择【新游戏】，即可开始这位玩家自己的进度\n  注意：不要点「继续游戏」，否则会接着上一位玩家的存档玩";
        var typed = Prompt.Show(this, prompt, $"玩家{g.ProfileSlots.Count + 1}");
        if (typed == null) return;
        string name = typed.Trim();
        SetBusy(true);
        try
        {
            await Task.Run(() => ProfileService.CreatePlayer(g, name));
            g.RefreshProfiles();
            RefreshAfterChange(g);
            ShowToast($"玩家「{g.ActiveProfile}」已创建——现在启动游戏，在主菜单选「新游戏」开始他的进度");
        }
        catch (Exception ex) { ShowToast(ex.Message, true); }
        finally { SetBusy(false); }
    }

    /// <summary>点击玩家筹码：切换到该玩家（确认后自动回存当前玩家进度 → 覆盖为目标玩家存档）。</summary>
    private async void OnSwitchPlayer(object s, RoutedEventArgs e)
    {
        if (SelectedGame is not GameEntry g) return;
        if ((s as FrameworkElement)?.DataContext is not ProfileSlotVm vm) return;
        if (vm.IsActive) { ShowToast($"当前就是玩家「{vm.Name}」"); return; }
        int targetBackups = g.Backups.Count(b => (b.Profile ?? "") == vm.Name);
        // 关键信息：目标玩家"游戏内最后一次保存"的时间——取他最新备份 zip 清单里存档文件的修改时间
        // （游戏真实保存时刻，非备份时刻），只切换不玩游戏时它不会变，用户据此判断切回去的进度停在哪一刻
        var savedAt = ProfileService.LastProgressSaved(g, vm.Name);
        // 玩家档案以备份 zip 的形式呈现：名称一行、时间一行
        string recTime = savedAt is DateTime st
            ? $"    游戏最后保存：{st:yyyy-MM-dd HH:mm}（{Store.TimeAgo(st)}）\n"
            : "    游戏内尚无存档（全新进度，进游戏后请选「新游戏」）\n";
        string recBak = targetBackups > 0
            ? $"    名下备份记录：{targetBackups} 份\n"
            : "    名下备份记录：暂无\n";
        string curName = string.IsNullOrEmpty(g.ActiveProfile) ? "当前" : g.ActiveProfile;
        // 当前玩家的进度也标注游戏保存时间：读 live 存档（他若切换后继续玩过，live 比他的最新备份更新）
        string curTime = ProfileService.LiveProgressSaved(g) is DateTime ct
            ? $"（游戏保存于 {ct:yyyy-MM-dd HH:mm}）" : "";
        var confirm = MessageBox.Show(this,
            $"切换到玩家「{vm.Name}」？\n\n" +
            $"玩家「{curName}」的当前进度{curTime}会先自动打包一份他的备份（可在备份记录中查看），" +
            $"随后游戏存档替换为「{vm.Name}」最新备份的进度：\n\n" +
            $"    玩家「{vm.Name}」的最新备份\n" + recTime + recBak + "\n" +
            "说明：玩家的进度就保存在他的备份 zip 里——切走时自动打包当前进度，\n" +
            "切入时恢复他最新的备份。来回切换、不玩就切不会产生多余的备份。\n\n" +
            $"切换后，下方备份记录只显示「{vm.Name}」的备份。",
            "确认切换玩家", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            // 自动云存档游戏：先查云端该玩家是否有比本机更新的进度（另一台电脑刚玩过的情形）。
            // 同口径比较：云端 zip 的存档时间（云端索引 index.json 提供）vs 本机该玩家最新备份的存档时间——
            // 都是"游戏内真实保存时刻"，不再用「上传时间 vs 存档时间」错位比较。
            // 查询失败不阻断切换，静默按本机版本走。
            bool useCloud = false;
            if (g.CloudAuto)
            {
                try
                {
                    var remoteIdx = await CloudSyncService.FetchGameIndexAsync(g);
                    var newestCloud = remoteIdx
                        .Where(f => (f.Profile ?? "") == BackupService.SafeName(vm.Name))
                        .OrderByDescending(f => f.SortTimeUtc).FirstOrDefault();
                    // 无索引条目的旧包回退旧规则：已知文件（本机上传/下载过）不提示，防死循环
                    bool known = newestCloud != null && CloudSyncService.IsRemoteKnown(newestCloud.RelPath);
                    if (CloudSyncService.ShouldPromptCloudNewer(savedAt, newestCloud, known))
                    {
                        string cloudLine = newestCloud!.SaveAt is DateTime cs
                            ? $"    云端版本：存档于 {cs.ToLocalTime():yyyy-MM-dd HH:mm}（上传于 {newestCloud.Modified.ToLocalTime():yyyy-MM-dd HH:mm}）\n"
                            : $"    云端版本（上传于）：{newestCloud.Modified.ToLocalTime():yyyy-MM-dd HH:mm}\n";
                        string localLine = savedAt == null
                            ? "    本机版本：全新进度，游戏内尚无存档\n"
                            : $"    本机版本：{savedAt:yyyy-MM-dd HH:mm}（{Store.TimeAgo(savedAt.Value)}）\n";
                        var pick = MessageBox.Show(this,
                            $"检测到云端有玩家「{vm.Name}」可能更新的存档：\n\n" +
                            cloudLine + localLine + "\n" +
                            "「是」= 先下载云端最新版本再切换（推荐）\n" +
                            "「否」= 使用本机现有版本切换",
                            "云端版本更新", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
                        useCloud = pick == MessageBoxResult.Yes;
                    }
                }
                catch { /* 网络不可达等：按本机版本切换 */ }
            }

            if (useCloud)
            {
                BusyText.Text = "正在查询云端存档…";
                var progress = new Progress<double>(p =>
                    BusyText.Text = $"正在下载玩家「{vm.Name}」的云端存档… {p * 100:0}%");
                var dl = await CloudSyncService.PreparePlayerFromCloud(g, vm.Name, default, progress);
                ShowToast(dl.WasAlreadyLocal
                    ? $"玩家「{vm.Name}」的云端最新存档本机已存在，直接使用"
                    : $"已下载玩家「{vm.Name}」的云端最新存档");
            }
            await Task.Run(() => ProfileService.SwitchTo(g, vm.Name));
            g.RefreshProfiles();
            RefreshAfterChange(g);
            var at = ProfileService.LastProgressSaved(g, vm.Name);
            ShowToast(at is DateTime rt
                ? $"已切换到玩家「{vm.Name}」，游戏进度是他 {rt:MM-dd HH:mm} 在游戏内保存的版本，未丢失"
                : $"已切换到玩家「{vm.Name}」，还是全新进度，进游戏请选「新游戏」");
        }
        catch (Exception ex) { ShowToast(ex.Message, true); }
        finally { SetBusy(false); }
    }

    /// <summary>从右键菜单项取被操作的玩家筹码 VM（ContextMenu 不在可视化树，DataContext 不继承，须经 PlacementTarget）。</summary>
    private static ProfileSlotVm? SlotVmFromMenuItem(object s)
    {
        if (s is not MenuItem mi) return null;
        var cm = mi.Parent as ContextMenu;
        return (cm?.PlacementTarget as FrameworkElement)?.DataContext as ProfileSlotVm;
    }

    /// <summary>右键筹码 → 重命名玩家（备份归属与玩家备份子目录同步改名）。</summary>
    private async void OnRenamePlayer(object s, RoutedEventArgs e)
    {
        if (SelectedGame is not GameEntry g) return;
        if (SlotVmFromMenuItem(s) is not ProfileSlotVm vm) return;
        var name = Prompt.Show(this,
            $"把玩家「{vm.Name}」改名为：\n（他的存档和备份都会跟着改名，进度不受影响）", vm.Name);
        if (name == null) return;
        name = name.Trim();
        SetBusy(true);
        try
        {
            // 整目录移动 + 清单写盘丢后台（备份多时 IO 明显），UI 集合刷新留在 UI 线程
            await Task.Run(() => ProfileService.RenamePlayer(g, vm.Name, name));
            g.RefreshProfiles();
            RefreshAfterChange(g);
            ShowToast($"玩家已改名为「{name}」");
        }
        catch (Exception ex) { ShowToast(ex.Message, true); }
        finally { SetBusy(false); }
    }

    /// <summary>右键筹码 → 删除玩家（含其全部备份；删的是当前玩家则先切走）。</summary>
    private async void OnDeletePlayer(object s, RoutedEventArgs e)
    {
        if (SelectedGame is not GameEntry g) return;
        if (SlotVmFromMenuItem(s) is not ProfileSlotVm vm) return;
        int nBak = g.Backups.Count(b => (b.Profile ?? "") == vm.Name);
        var others = ProfileService.ListSlots(g).Where(n => !n.Equals(vm.Name, StringComparison.Ordinal)).ToList();
        string activeNote = vm.IsActive
            ? (others.Count > 0
                ? $"· 他的进度当前正在游戏目录中，删除后会自动切换到玩家「{others[0]}」；\n"
                : "· 他的进度当前正在游戏目录中，删除后游戏回到「未分玩家」状态（游戏目录里的存档文件保留）；\n")
            : "";
        var confirm = MessageBox.Show(this,
            $"删除玩家「{vm.Name}」？\n\n" +
            $"· 将删除他的全部 {nBak} 份备份（他的进度就存在这些备份里）；\n" +
            activeNote +
            "· 其他玩家的存档与备份不受影响；\n" +
            "此操作不可恢复！",
            "确认删除玩家", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        // 云端副本处理：与单份备份删除一致——只有用户显式选择才动云端。
        // 注：稍后 SwitchTo 可能给被删玩家补打一份当前进度备份，若其被自动上云，
        // 会由云端收集时的「本地缺失」黑名单机制兜底，不在此处理。
        var cloudFiles = new List<string>();
        if (CloudSyncService.Configured)
            cloudFiles = g.Backups
                .Where(b => (b.Profile ?? "") == vm.Name && CloudSyncService.HasKnownRemoteCopy(g, b.File))
                .Select(b => b.File).ToList();
        bool delCloud = false;
        if (cloudFiles.Count > 0)
        {
            var firstRec = g.Backups.First(b => b.File == cloudFiles[0]);
            var choice = new CloudDeleteDialog(this, g.DisplayName, firstRec.SavedAtMainText, firstRec.SizeText, cloudFiles.Count);
            if (choice.ShowDialog() != true) return;   // 取消：整个删除流程中止
            delCloud = choice.DeleteCloudToo;
            Store.Settings.CloudDelWithCloud = delCloud;
            Store.SaveSettings();
        }

        SetBusy(true);
        try
        {
            // 删的是当前玩家且还有别人：先切换（当前进度自动打包给被删者 → 覆盖为别人的存档），再删除
            if (vm.IsActive && others.Count > 0)
                await Task.Run(() => ProfileService.SwitchTo(g, others[0]));
            int deleted = 0;
            await Task.Run(() => deleted = ProfileService.DeletePlayer(g, vm.Name));
            int cloudDeleted = 0;
            if (delCloud)
                foreach (var f in cloudFiles)
                    cloudDeleted += await CloudSyncService.DeleteRemoteCopiesAsync(g, f);
            g.RefreshProfiles();
            RefreshAfterChange(g);
            ShowToast(delCloud && cloudDeleted > 0
                ? $"玩家「{vm.Name}」已删除（含 {deleted} 份备份，云端副本已同步清理）"
                : $"玩家「{vm.Name}」已删除（含 {deleted} 份备份）");
        }
        catch (Exception ex) { ShowToast(ex.Message, true); }
        finally { SetBusy(false); }
    }

    // ── 备份备注 ────────────────────────────────────────
    private void OnEditBackupNote(object s, RoutedEventArgs e)
    {
        if (SelectedGame is not GameEntry g) return;
        if ((s as FrameworkElement)?.DataContext is not BackupRecord r) return;
        var note = Prompt.Show(this, "给这份备份改个名字\n（如：最终BOSS前、二周目开局）", r.Note);
        if (note == null) return;
        note = note.Trim();
        r.Note = note.Length == 0 ? "手动备份" : note;
        Store.Save();
        ShowToast("备份名称已更新");
    }

    /// <summary>切换备份锁定：锁定后自动滚动清理不删这份（手动删除不受影响）。</summary>
    private void OnToggleLock(object s, RoutedEventArgs e)
    {
        if (SelectedGame is not GameEntry g) return;
        if ((s as FrameworkElement)?.DataContext is not BackupRecord r) return;
        r.Locked = !r.Locked;
        Store.Save();
        ShowToast(r.Locked ? "已锁定：自动清理时会保留这份备份" : "已解锁");
    }

    // ── 外观主题（右上角小图标按钮）─────────────────────────
    private void OnThemeBtnClick(object s, RoutedEventArgs e)
    {
        UpdateThemeChips();
        ThemePopup.IsOpen = !ThemePopup.IsOpen;
    }

    private void OnThemePopupChecked(object s, RoutedEventArgs e)
    {
        if (s is not System.Windows.Controls.RadioButton { Tag: string tag }) return;
        string mode = ThemeManager.Normalize(tag);
        if (mode == ThemeManager.Mode) return;
        ThemeManager.SetMode(mode);
        Store.SaveSettings();
        ThemePopup.IsOpen = false;
    }

    /// <summary>刷新主题按钮图标与提示（主题实际生效后由 ThemeManager.ThemeChanged 驱动）。</summary>
    private void UpdateThemeButton()
    {
        ThemeIcon.Text = ThemeManager.IsDark ? "🌙" : "☀";
        string modeName = ThemeManager.Mode switch
        {
            ThemeManager.Light => "清新浅色",
            ThemeManager.LightSoft => "素雅浅色",
            ThemeManager.Dark => "深色",
            _ => $"跟随系统（当前{(ThemeManager.IsDark ? "深色" : "浅色")}）",
        };
        ThemeBtn.ToolTip = $"外观主题：{modeName}";
        UpdateThemeChips();
    }

    private void UpdateThemeChips()
    {
        ThemeChipSystem.IsChecked = ThemeManager.Mode == ThemeManager.System;
        ThemeChipLight.IsChecked = ThemeManager.Mode == ThemeManager.Light;
        ThemeChipLightSoft.IsChecked = ThemeManager.Mode == ThemeManager.LightSoft;
        ThemeChipDark.IsChecked = ThemeManager.Mode == ThemeManager.Dark;
    }

    // ── 设置 ────────────────────────────────────────────
    private void OnOpenSettings(object s, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            UpdatePolicyText();
            bool hotKeyOk = RegisterBackupHotKey();   // 快捷键可能已修改/清除，重新注册
            CloudSyncService.Reconcile();             // 云同步配置可能变更：补传未登记的备份
            // AppData 被删、备份在自定义盘：重新指定备份目录后从该盘 _appdata 快照补恢复空库
            int restored = Store.TryRestoreEmptyLibrary();
            if (restored > 0)
            {
                // 该自定义盘可能从未被新版程序指过：先把盘上旧版 backups\profiles 迁到 _appdata\profiles（幂等）
                ProfileService.MigrateLegacyProfileRoot();
                ReclaimRemovedGameData(silent: true);
                ApplyFilter();
                ShowToast($"已从备份目录的程序数据快照恢复 {restored} 款游戏");
            }
            else if (!hotKeyOk && !string.IsNullOrWhiteSpace(Store.Settings.BackupHotKey))
                ShowToast("设置已保存，但全局快捷键注册失败（可能与其他软件冲突），请更换组合键", true);
            else ShowToast(string.IsNullOrEmpty(dlg.CloudNotice) ? "设置已保存" : dlg.CloudNotice);
        }
    }

    /// <summary>本次「更新数据库」的取消源（停止按钮触发）；null=没有更新在跑。</summary>
    private CancellationTokenSource? _dbUpdateCts;

    /// <summary>工具栏「更新数据库」：先预取中文名（连接不畅会自动/手动中止），再刷新 Wiki 规则（并发 4 路）。</summary>
    private async void OnRefreshAllWiki(object s, RoutedEventArgs e)
    {
        SetBusy(true);
        _dbUpdateCts = new CancellationTokenSource();
        var ct = _dbUpdateCts.Token;
        StopDbBtn.Visibility = Visibility.Visible;
        var games = Store.Games.ToList();
        try
        {
            // ① 先批量预取中文名到本地库（小黑盒，国内直连免加速器；连续网络失败会自动中止）
            BusyText.Text = "正在下载中文名…";
            var progress = new Progress<string>(msg => BusyText.Text = msg);
            var (ok, cached, _, stopped, reason) = await GameNameService.PrefetchXiaoheiheAsync(
                games.Where(g => g.AppId > 0).Select(g => g.AppId), progress, ct);
            if (stopped)
            {
                ShowToast(reason ?? "已停止", true);
                return;
            }
            // 预取到的中文名即时反映到列表
            bool nameChanged = false;
            foreach (var g in games)
            {
                if (g.AppId > 0 && string.IsNullOrWhiteSpace(g.NameCN) &&
                    GameNameService.TryGetLocal(g.AppId, out var cn))
                {
                    g.NameCN = cn; g.Refresh(); nameChanged = true;
                }
            }
            if (nameChanged) Store.Save();

            // ② 再刷新 Wiki 存档规则库（外部并发 4，实际出速受 WikiService 内部 2 并发 + 200ms 间隔约束）
            BusyText.Text = "Wiki 更新中…";
            int wikiDone = 0, wikiTotal = games.Count;
            using var gate = new SemaphoreSlim(4);
            var wikiTask = Task.WhenAll(games.Select(async g =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    await EnrichWikiAsync(g, forceRefresh: true);
                    ((IProgress<string>)progress).Report($"Wiki 更新中（{Interlocked.Increment(ref wikiDone)}/{wikiTotal}）…");
                }
                finally { gate.Release(); }
            }));
            // 停止时立即结束等待；在途请求按各自超时自然收尾，不再阻塞界面
            await Task.WhenAny(wikiTask, Task.Delay(Timeout.Infinite, ct));
            if (ct.IsCancellationRequested)
            {
                ShowToast("已停止更新（已获取的数据已保存）", true);
                return;
            }
            ShowToast($"数据库已更新：中文名新增 {ok} 个（已缓存 {cached}），Wiki 规则 {games.Count} 款");
        }
        catch (OperationCanceledException) { ShowToast("已停止更新（已获取的数据已保存）", true); }
        catch (Exception ex) { ShowToast("更新失败：" + ex.Message, true); }
        finally
        {
            StopDbBtn.Visibility = Visibility.Collapsed;
            _dbUpdateCts?.Dispose();
            _dbUpdateCts = null;
            BusyText.Text = "处理中…"; SetBusy(false);
        }
    }

    /// <summary>「停止更新」：取消中文名预取与 Wiki 刷新等待。</summary>
    private void OnStopDbUpdate(object s, RoutedEventArgs e)
    {
        if (_dbUpdateCts == null) return;
        BusyText.Text = "正在停止…";
        StopDbBtn.IsEnabled = false;
        _dbUpdateCts.Cancel();
        StopDbBtn.IsEnabled = true;
    }

    /// <summary>详情页「▶ 运行游戏」。</summary>
    private void OnRunGame(object s, RoutedEventArgs e)
    {
        if (SelectedGame is GameEntry g) LaunchGame(g);
    }

    /// <summary>启动游戏：Steam 游戏优先走 steam://rungameid 协议（计时/覆盖层/更新），失败或学习版用 exe。</summary>
    private void LaunchGame(GameEntry g)
    {
        if (g.IsSteam && g.AppId > 0)
        {
            try
            {
                Process.Start(new ProcessStartInfo($"steam://rungameid/{g.AppId}") { UseShellExecute = true });
                RecordPlay(g);
                ShowToast($"已通过 Steam 启动「{g.DisplayName}」");
                return;
            }
            catch { /* Steam 未安装/协议不可用 → 退回 exe */ }
        }
        if (string.IsNullOrWhiteSpace(g.ExePath) || !File.Exists(g.ExePath))
        {
            ShowToast("未设置游戏 exe 或文件不存在，请通过「✎ 编辑」定位", true);
            return;
        }
        try
        {
            var p = Process.Start(new ProcessStartInfo(g.ExePath)
            {
                WorkingDirectory = Path.GetDirectoryName(g.ExePath) ?? "",
                UseShellExecute = true,
            });
            // 记录 PID：自动备份只认这个进程（同名进程/同目录其他程序不再误触发退出备份）
            if (p != null) _launched[g.Id] = (p.Id, g.ProcessName, DateTime.Now);
            RecordPlay(g);
            ShowToast($"已启动「{g.DisplayName}」");
        }
        catch (Exception ex) { ShowToast("启动失败：" + ex.Message, true); }
    }

    /// <summary>启动成功后记录游玩时间（「最近游玩」排序的数据来源；Steam manifest 扫描只覆盖
    /// 已安装的 Steam 游戏，学习版/收藏游戏此前永远没有该数据，排序看起来不生效），并即时重排。</summary>
    private void RecordPlay(GameEntry g)
    {
        g.LastPlayed = DateTime.Now;
        Store.Save();
        RefreshAfterChange(g);
    }

    /// <summary>详情页「✎ 编辑」：编辑名称/封面/图标/AppId/exe。</summary>
    private void OnEditGame(object s, RoutedEventArgs e)
    {
        if (SelectedGame is not GameEntry g) return;
        var dlg = new EditGameWindow(g) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            Store.Save();
            RefreshAfterChange(g);
            ShowToast("游戏信息已更新");
        }
    }

    /// <summary>详情页「📊 游玩详情」：导入日期 / 游玩总时长 / 最后关闭时间 / 会话记录。</summary>
    private void OnShowPlayStats(object s, RoutedEventArgs e)
    {
        if (SelectedGame is not GameEntry g) return;
        new PlayStatsWindow(g) { Owner = this }.ShowDialog();
    }

    private void OnAutoToggled(object s, RoutedEventArgs e)
    {
        if (SelectedGame is GameEntry g)
        {
            Store.Save();
            UpdateStatusBar();
            if (!_suppressAutoToast)
                ShowToast(g.AutoBackup ? "已开启：游戏退出后自动备份" : "已关闭自动备份",
                    anchor: (System.Windows.FrameworkElement)s);
        }
    }

    private void OnHotToggled(object s, RoutedEventArgs e)
    {
        if (SelectedGame is GameEntry g)
        {
            Store.Save();
            UpdateStatusBar();
            if (!_suppressAutoToast)
                ShowToast(g.HotSnapshot
                    ? $"已开启：游戏运行中每 {Math.Clamp(Store.Settings.HotSnapshotMinutes, 5, 120)} 分钟自动快照"
                    : "已关闭运行中定时快照",
                    anchor: (System.Windows.FrameworkElement)s);
        }
    }

    /// <summary>每游戏「☁ 自动云存档」开关：开启需云功能已配置；开启后立即收集一次云端备份。</summary>
    private void OnCloudAutoToggled(object s, RoutedEventArgs e)
    {
        if (SelectedGame is not GameEntry g) return;
        if (g.CloudAuto && !CloudSyncService.Configured)
        {
            g.CloudAuto = false;   // 回退开关（会再触发一次本事件，走关闭分支），温和提示先配置（非错误样式）
            ShowToast("云同步还没配置完整，补全后就能开启自动云存档啦（设置 → 云同步）",
                anchor: (System.Windows.FrameworkElement)s);
            return;
        }
        Store.Save();
        if (!_suppressAutoToast)
            ShowToast(g.CloudAuto ? "已开启自动云存档，正在检查云端备份…" : "已关闭自动云存档",
                anchor: (System.Windows.FrameworkElement)s);
        if (g.CloudAuto)
        {
            CloudSyncService.Reconcile();
            _ = CollectGameWithToast(g);
        }
    }

    /// <summary>收集单游戏云端备份并刷新界面（开启自动云存档时）。</summary>
    private async Task CollectGameWithToast(GameEntry g)
    {
        SetBusy(true);
        try
        {
            var (b, p) = await CloudSyncService.CollectGameAsync(g);
            if (b > 0 || p > 0)
            {
                RefreshAfterChange(g);
                ShowToast($"云端收集完成：新增 {b} 份备份" + (p > 0 ? $"、{p} 位玩家" : ""));
            }
        }
        catch (Exception ex) { ShowToast("云端收集失败：" + ex.Message, true); }
        finally { SetBusy(false); }
    }

    /// <summary>备份行「上传」：把该份 zip 手动传到云盘。</summary>
    private async void OnUploadBackup(object s, RoutedEventArgs e)
    {
        if (SelectedGame is not GameEntry g) return;
        if ((s as FrameworkElement)?.DataContext is not BackupRecord r) return;
        SetBusy(true);
        try
        {
            await CloudSyncService.UploadSingleAsync(g, r);
            ShowToast("已上传到云盘：" + r.TimeText);
        }
        catch (Exception ex) { ShowToast(ex.Message, true); }
        finally { SetBusy(false); }
    }

    /// <summary>行点击云端存在性核验的防抖表：同一份备份 10 秒内不重复发 PROPFIND。</summary>
    private readonly Dictionary<string, DateTime> _cloudCheckedAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>点击备份记录行：冗余精确核验这份备份在云端是否还在
    /// （他机/网页端可能已删除），结果联动「已传 / 已删重传」状态。未配置云同步时不请求。</summary>
    private async void OnBackupRowClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SelectedGame is not GameEntry g) return;
        if ((sender as FrameworkElement)?.DataContext is not BackupRecord r || string.IsNullOrEmpty(r.File)) return;
        if (!CloudSyncService.Configured) return;
        // 防抖：同一文件 10 秒内只核验一次（选中、点空白都会触发，避免频繁网络请求）
        if (_cloudCheckedAt.TryGetValue(r.File, out var last) && (DateTime.Now - last).TotalSeconds < 10) return;
        _cloudCheckedAt[r.File] = DateTime.Now;
        try { await CloudSyncService.VerifyCloudExistsAsync(g, r); }
        catch { /* 核验失败（断网/服务器抖动）保持现状，不打扰 */ }
    }

    /// <summary>工具栏「☁ 云存档」：打开云端备份浏览窗；未配置则引导去设置。</summary>
    private void OnOpenCloudBrowser(object s, RoutedEventArgs e)
    {
        if (!CloudSyncService.Configured)
        {
            if (MessageBox.Show(this, "云同步尚未配置。是否打开「设置 → 云同步」配置账号（坚果云需应用专用密码）？",
                    "云存档", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                OnOpenSettings(s, e);
            return;
        }
        WebDavClient client;
        try { client = CloudSyncService.NewClient(); }
        catch (Exception ex) { ShowToast(ex.Message, true); return; }
        new CloudBrowserWindow(client) { Owner = this }.ShowDialog();
    }

    private void RefreshAfterChange(GameEntry g)
    {
        g.Refresh();
        CloudSyncService.RefreshCloudFlags(g);
        g.RefreshVisibleBackups();
        ICollectionView view = CollectionViewSource.GetDefaultView(Store.Games);
        view.Refresh();
        GamesList.ItemsSource = view;
        UpdateSideFooters();
        UpdateStatusBar();
        RefreshActiveSlotLiveStateAsync(g);
    }

    private readonly HashSet<string> _liveStateBusy = new();

    /// <summary>后台核对当前玩家 live 存档与其最新备份，异步升级筹码第二行状态。
    /// RefreshProfiles 保持零 IO 契约（静态文案它给），真实状态在这里补；同一游戏同时只跑一轮。</summary>
    private async void RefreshActiveSlotLiveStateAsync(GameEntry? g)
    {
        if (g is null) return;
        var vm0 = g.ProfileSlots.FirstOrDefault(v => v.IsActive);
        if (vm0 is null || !_liveStateBusy.Add(g.Id)) return;
        string profile = vm0.Name;
        try
        {
            var st = await Task.Run(() => BackupService.GetLiveState(g, profile));
            bool running = g.IsRunning;
            await Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(g, SelectedGame)) return;   // 已切到别的游戏：结果作废
                var cur = g.ProfileSlots.FirstOrDefault(v => v.IsActive);
                if (cur is null || cur.Name != profile) return;  // 玩家已切换：结果作废
                ApplyLiveState(cur, st, running);
            });
            // ☁ 云端核对：已配置且游戏没在玩时，查该玩家云端最新备份的存档时间是否比本机新。
            // 严格事实 ☁>💾 才覆盖本地状态；离线/超时/未配置一律静默，只显示本地事实。
            if (!running && CloudSyncService.Configured)
            {
                try
                {
                    var remoteIdx = await Task.Run(async () => await CloudSyncService.FetchGameIndexAsync(g));
                    var newestCloud = remoteIdx
                        .Where(f => (f.Profile ?? "") == BackupService.SafeName(profile))
                        .OrderByDescending(f => f.SortTimeUtc).FirstOrDefault();
                    bool known = newestCloud != null && CloudSyncService.IsRemoteKnown(newestCloud.RelPath);
                    var localSaved = ProfileService.LastProgressSaved(g, profile);
                    if (newestCloud != null &&
                        CloudSyncService.ShouldPromptCloudNewer(localSaved, newestCloud, known))
                    {
                        string cloudTime = newestCloud.SaveAt is DateTime cs
                            ? cs.ToLocalTime().ToString("MM-dd HH:mm")
                            : newestCloud.Modified.ToLocalTime().ToString("MM-dd HH:mm");
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (!ReferenceEquals(g, SelectedGame)) return;
                            var cur = g.ProfileSlots.FirstOrDefault(v => v.IsActive);
                            if (cur is null || cur.Name != profile) return;
                            cur.SetStatus("云端有较新备份", "info",
                                $"云端有存档于 {cloudTime} 的备份，比本机新\n点击这行只下载到本机（不自动还原）",
                                "restoreCloud");
                        });
                    }
                }
                catch { /* 网络不可达等：保持本地状态 */ }
            }
        }
        catch { }
        finally { _liveStateBusy.Remove(g.Id); }
    }

    private static void ApplyLiveState(ProfileSlotVm vm, BackupService.LiveStateInfo st, bool running)
    {
        string bt = st.BackupLatest != default ? st.BackupLatest.ToString("MM-dd HH:mm") : "";
        string lt = st.LiveLatest != default ? st.LiveLatest.ToLocalTime().ToString("MM-dd HH:mm") : "";
        string mt = st.MatchedAt != default ? st.MatchedAt.ToString("MM-dd HH:mm") : "";
        if (running)
        {
            vm.SetStatus("游玩中", "normal", "游戏正在运行\n退出游戏后自动核对并更新备份状态");
            return;
        }
        switch (st.Kind)
        {
            case "nosave":
                vm.SetStatus("游戏无存档", "normal", "当前玩家的存档目录为空\n游戏内存档后即会出现存档");
                break;
            case "nobackup":
                vm.SetStatus("从未备份", "warn",
                    $"当前有 {st.LiveFiles} 个存档文件，还没有备份\n点下方「立即备份」保存进度");
                break;
            case "localnewer":
                vm.SetStatus("本地有较新备份", "info",
                    $"本机有存档时间 {bt} 的备份，比当前存档新\n点击这行直接还原到那份备份",
                    "restoreLocal");
                break;
            case "same":
                vm.SetStatus("存档备份一致", "good",
                    $"当前存档与最近备份一致\n存档时间 {bt}");
                break;
            case "restored":
                vm.SetStatus("存档已还原", "good",
                    $"当前存档是还原的备份\n存档时间 {mt}");
                break;
            case "changed":
                vm.SetStatus("新存档未备份", "warn",
                    $"当前进度有未备份的变化\n最新存档 {lt}　最近备份 {bt}\n点下方「立即备份」保存");
                break;
            // unknown：保留 RefreshProfiles 给的静态备份时间，不乱标状态
        }
    }

    // ── 游戏/目录 ──────────────────────────────────────
    private async void OnRemoveGame(object s, RoutedEventArgs e)
    {
        if (SelectedGame is not GameEntry g) return;
        long bytes = 0;
        try { bytes = await Task.Run(() => BackupService.TotalBytesOnDisk(g)); } catch { }
        string sizeLine = bytes > 0
            ? $"该游戏的备份与玩家档案共占用 {BackupService.FormatBytes(bytes)}。\n\n"
            : "";
        var confirm = MessageBox.Show(this,
            $"从库中移除「{g.DisplayName}」？\n\n{sizeLine}" +
            "「是」= 仅从库中移除，备份和玩家档案保留在磁盘上，以后重新添加该游戏会自动找回；\n" +
            "「否」= 连备份和玩家档案一起彻底删除（不可恢复）；\n" +
            "「取消」= 不做任何操作。",
            "确认移除", MessageBoxButton.YesNoCancel, MessageBoxImage.Question,
            MessageBoxResult.Yes);
        if (confirm == MessageBoxResult.Cancel) return;
        bool purge = confirm == MessageBoxResult.No;
        SetBusy(true);
        try
        {
            // 磁盘重活（整目录移动/递归删除，备份多的时候会卡 UI）丢到后台线程
            try
            {
                string removedId = g.Id;
                await Task.Run(() =>
                {
                    if (purge)
                        BackupService.DeleteAllOnRemoval(g);
                    else
                        ProfileService.DetachOnRemoval(g);   // 玩家镜像脱离 GUID，按游戏名保留
                    BackupService.TryRemoveGameGate(removedId);   // 闸只增不清：游戏移除后回收
                });
            }
            catch (Exception ex) { ShowToast("清理备份数据时出错：" + ex.Message, true); }
            // 图标按 GUID 缓存，移除后再无引用，一并清理；封面按 AppId 共享保留
            try { if (!string.IsNullOrEmpty(g.IconFile) && File.Exists(g.IconFile)) File.Delete(g.IconFile); } catch { }
            _playOpen.Remove(g.Id);   // 条目删除：进行中的游玩会话不再结算
            Store.Games.Remove(g);
            Store.Save();
            SelectedGame = null;
            GamesList.SelectedItem = null;
            UpdateSideFooters();
            ShowToast(purge
                ? $"已移除「{g.DisplayName}」并删除其全部备份数据"
                : $"已移除「{g.DisplayName}」，备份已保留，重新添加游戏即可找回");
        }
        finally { SetBusy(false); }
    }

    private void OnOpenBackupDir(object s, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Store.BackupRoot);
        new OrphanBackupsWindow { Owner = this }.ShowDialog();
    }

    /// <summary>详情页「刷新规则」：强制联网更新 Wiki 存档规则，并重跑本地探测（含 rules.json）。</summary>
    private async void OnRefreshRules(object s, RoutedEventArgs e)
    {
        if (SelectedGame is not GameEntry g) return;
        SetBusy(true);
        try
        {
            await EnrichWikiAsync(g, forceRefresh: true);
            var detected = await Task.Run(() =>
                SaveLocator.Detect(g.NameEN, g.ExePath, g.InstallDir, g.AppId, _userdataMap));
            bool changed = false;
            foreach (var p in detected)
                if (g.AcceptAutoSavePath(p)) changed = true;   // 用户显式点「自动获取」：允许加回曾移除的路径
            if (changed) { Store.Save(); g.Refresh(); }
            ShowToast(changed ? "存档规则已刷新，已补充新识别的位置" : "存档规则已刷新，没有发现新的位置");
        }
        catch (Exception ex) { ShowToast("刷新失败：" + ex.Message, true); }
        finally { SetBusy(false); }
    }

    // ── 自动备份监视（轮询进程退出）+ 运行中定时快照 ───────
    private void WatchTick(object? s, EventArgs e)
    {
        // 手动备份/还原/玩家操作/扫描进行中：本轮跳过，避免与后台任务并发改集合、互写 zip
        if (_busyDepth > 0) return;
        foreach (var g in Store.Games.Where(g => g.ExePath.Length > 0).ToList())
        {
            bool running = CheckGameRunningState(g);
            if (running)
            {
                if (!_playOpen.ContainsKey(g.Id))
                {
                    // 本应用 exe 启动的用精确启动时刻；Steam 协议/外部启动以首次轮询发现为起点（≤3s 误差）
                    bool fromApp = _launched.TryGetValue(g.Id, out var li0);
                    _playOpen[g.Id] = (fromApp ? li0.StartedAt : DateTime.Now, fromApp ? "app" : "monitor");
                }
                if (!g.IsRunning)
                {
                    g.IsRunning = true;
                    if (ReferenceEquals(g, SelectedGame)) RefreshActiveSlotLiveStateAsync(g);  // 筹码→「游玩中」
                }
                if (g.AutoBackup) _wasRunning.Add(g.Id);
                // 运行中定时快照（每游戏独立开关；间隔为全局设置）
                if (g.HotSnapshot && DueForHotSnapshot(g))
                {
                    _lastHotSnapshot[g.Id] = DateTime.Now;   // 先记时间：失败/无变化也不至于每 3 秒重试
                    _ = RunGameBackupAsync(g, "运行中快照", hot: true);
                }
                continue;
            }
            if (g.IsRunning)
            {
                g.IsRunning = false;
                if (ReferenceEquals(g, SelectedGame)) RefreshActiveSlotLiveStateAsync(g);  // 退出后立即重新核对
            }
            // 会话闭合（与自动备份无关）：最后关闭时间 / 时长 / 会话明细入库并重排
            if (_playOpen.Remove(g.Id, out var ps)
                && PlayTracker.CloseSession(g, ps.Start, DateTime.Now, ps.Source))
            {
                Store.Save();
                RefreshAfterChange(g);
            }
            if (g.AutoBackup && _wasRunning.Remove(g.Id))
            {
                _lastHotSnapshot.Remove(g.Id);   // 会话结束，下次启动重新计时
                AppendAutoLog($"「{g.DisplayName}」检测到游戏退出，触发自动备份");
                _ = RunGameBackupAsync(g, "已退出，自动备份", hot: false);
            }
        }
        UpdateStatusBar();
    }

    /// <summary>游戏运行状态检测：本应用用 exe 启动过的游戏优先认记录的 PID（同名进程不再误判），
    /// PID 已死即视为退出并清除记录；Steam 协议启动 / 外部启动回退进程名匹配。</summary>
    private bool CheckGameRunningState(GameEntry g)
    {
        if (_launched.TryGetValue(g.Id, out var li))
        {
            if (IsPidAlive(li)) return true;
            _launched.Remove(g.Id);
            return false;
        }
        return BackupService.IsGameRunning(g);
    }

    /// <summary>PID 是否仍是当初启动的那个进程（进程已死或 PID 被其他新进程复用均视为退出）。</summary>
    private static bool IsPidAlive((int Pid, string Name, DateTime StartedAt) li)
    {
        try
        {
            using var p = Process.GetProcessById(li.Pid);
            return !p.HasExited && p.StartTime >= li.StartedAt.AddSeconds(-5);
        }
        catch { return false; }   // 进程不存在 / 已退出
    }

    /// <summary>自动备份 / 运行中快照：后台打包，最多 2 款游戏并发（AutoBackupGate），同一游戏串行
    /// （_autoBusy + BackupService 每游戏闸）。不再占全局忙碌锁——长备份不再阻塞其他游戏的退出备份，
    /// 与手动操作的互斥由每游戏闸保证。完成/失败回 UI 线程提示。</summary>
    private async Task RunGameBackupAsync(GameEntry g, string note, bool hot)
    {
        if (!_autoBusy.Add(g.Id)) return;   // 该游戏已有自动备份在跑
        try { await AutoBackupGate.WaitAsync(); }
        catch { _autoBusy.Remove(g.Id); return; }
        AppendAutoLog($"「{g.DisplayName}」开始 {note}（{(hot ? "运行中快照" : "退出备份")}）");
        try
        {
            var r = await Task.Run(() => BackupService.Create(g, note, hot));
            if (!hot) _lastAutoBackup = DateTime.Now;
            AppendAutoLog($"「{g.DisplayName}」完成 {note}：{r.File}（{r.SizeText}）");
            await Dispatcher.InvokeAsync(() =>
            {
                RefreshAfterChange(g);
                ShowToast($"「{g.DisplayName}」{note}完成 {r.SizeText}");
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("没有变化"))
        {
            // 无新存档写入：运行中快照静默跳过（时间戳已更新）；退出备份提示一声（原行为）
            AppendAutoLog($"「{g.DisplayName}」跳过 {note}：存档无变化");
            if (!hot)
                await Dispatcher.InvokeAsync(() => ShowToast($"「{g.DisplayName}」存档无变化，跳过自动备份"));
        }
        catch (Exception ex)
        {
            AppendAutoLog($"「{g.DisplayName}」失败 {note}：{ex.GetType().Name}：{ex.Message}");
            await Dispatcher.InvokeAsync(() => ShowToast($"「{g.DisplayName}」{note}失败：{ex.Message}", true));
        }
        finally
        {
            AutoBackupGate.Release();
            _autoBusy.Remove(g.Id);
        }
    }

    /// <summary>自动备份运行日志（logs\auto-backup.log 追加一行）：触发/成功/跳过/失败全记录，
    /// 出问题可回溯；Toast 转瞬即逝，这里落盘为准。写失败静默（日志不能反噬备份流程）。</summary>
    private static void AppendAutoLog(string msg)
    {
        try
        {
            string dir = Path.Combine(Store.BaseDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "auto-backup.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {msg}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>该游戏是否到了运行中定时快照的时间（间隔 clamp 5–120 分钟）。</summary>
    private bool DueForHotSnapshot(GameEntry g)
    {
        int min = Math.Clamp(Store.Settings.HotSnapshotMinutes, 5, 120);
        return !_lastHotSnapshot.TryGetValue(g.Id, out var t) ||
               (DateTime.Now - t).TotalMinutes >= min;
    }

    // ── UI 辅助 ─────────────────────────────────────────
    private int _busyDepth;

    /// <summary>忙碌态：显示提示并禁用整个主界面，防止备份/还原/扫描/玩家操作期间
    /// 再次点击按钮造成重入（并发写 zip、并发改集合、备份中删目录等）。计数式，支持嵌套配对。</summary>
    private void SetBusy(bool busy)
    {
        _busyDepth += busy ? 1 : -1;
        if (_busyDepth < 0) _busyDepth = 0;
        bool on = _busyDepth > 0;
        RootGrid.IsEnabled = !on;
        BusyText.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowToast(string msg, bool error = false, System.Windows.FrameworkElement? anchor = null)
    {
        var errBrush = (System.Windows.Media.Brush)FindResource("ToastErrorText");
        var okBrush = (System.Windows.Media.Brush)FindResource("Accent");
        var errText = (System.Windows.Media.Brush)FindResource("ToastErrorText");
        var okText = (System.Windows.Media.Brush)FindResource("ToastText");

        if (anchor != null)
        {
            // 锚定浮层：紧贴被点控件下方，按钮本身始终可见可点
            ToastPopupText.Text = msg;
            ToastPopupCard.BorderBrush = error ? errBrush : okBrush;
            ToastPopupText.Foreground = error ? errText : okText;
            ToastPopupCard.BeginAnimation(OpacityProperty, null);
            ToastPopupCard.Opacity = 1;
            ToastPopup.PlacementTarget = anchor;
            ToastPopup.IsOpen = true;
            Toast.Opacity = 0;   // 避免与左下角常驻提示同时出现两条
        }
        else
        {
            ToastText.Text = msg;
            Toast.BorderBrush = error ? errBrush : okBrush;
            // Toast 是深色半透明底，错误态文字也要用深底专用的亮红（不能用浅底语义的 Red）
            ToastText.Foreground = error ? errText : okText;
            Toast.BeginAnimation(OpacityProperty, null);
            Toast.Opacity = 1;
            ToastPopup.IsOpen = false;
        }
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        Toast.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
        if (ToastPopup.IsOpen)
        {
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) => ToastPopup.IsOpen = false;
            ToastPopupCard.BeginAnimation(OpacityProperty, fade);
        }
    }

    private void UpdateStatusBar()
    {
        // 左侧：还没自动备份过时留空，不显示任何提示；备份过后只报最近一次时间
        StatusLeft.Text = _lastAutoBackup == DateTime.MinValue
            ? ""
            : $"最近一次自动备份：{_lastAutoBackup:MM-dd HH:mm}";
        int watching = Store.Games.Count(g => g.AutoBackup);
        StatusRight.Text = $"已开启自动备份：{watching} 个游戏 · 备份位置 {Store.BackupRoot}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>极简输入弹窗（改名用）。颜色在 Show 时从当前主题读取，切换主题后新弹窗即时正确。</summary>
public static class Prompt
{
    private static System.Windows.Media.Brush Res(string key) =>
        (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource(key);

    public static string? Show(Window owner, string title, string initial)
    {
        var tb = new System.Windows.Controls.TextBox
        {
            Text = initial,
            FontSize = 14,
            Margin = new Thickness(20, 14, 20, 0),
            Padding = new Thickness(8, 6, 8, 6),
            Background = Res("Bg"),
            Foreground = Res("TextPrimary"),
            CaretBrush = Res("TextPrimary"),
            BorderBrush = Res("Accent"),
        };
        var ok = new System.Windows.Controls.Button { Content = "确定", Width = 80, Height = 30, IsDefault = true };
        var cancel = new System.Windows.Controls.Button { Content = "取消", Width = 80, Height = 30, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var panel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 20, 16),
        };
        panel.Children.Add(ok);
        panel.Children.Add(cancel);
        var root = new StackPanel();
        root.Children.Add(new TextBlock
        {
            Text = title,
            Margin = new Thickness(20, 16, 20, 0),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("TextSecondary"),
        });
        root.Children.Add(tb);
        root.Children.Add(panel);
        var w = new Window
        {
            Title = "GameSaveVault",
            Owner = owner,
            Width = 440,
            MinHeight = 180,
            SizeToContent = SizeToContent.Height,   // 高度随说明文字自适应，避免按钮被裁出窗口
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = Res("Panel"),
            Content = root,
        };
        ok.Click += (_, _) => w.DialogResult = true;
        return w.ShowDialog() == true ? tb.Text : null;
    }
}

/// <summary>还原确认弹窗（含「严格还原」选项）。深色标题栏 + 应用统一按钮/文字资源，
/// 与 CloudDeleteDialog 同一套观感；「还原」为主操作用 BtnPrimary。</summary>
public static class ConfirmRestore
{
    public static (bool Ok, bool Purge) Show(Window owner, string gameName, BackupRecord r)
    {
        bool purge = false;

        var w = new Window
        {
            Title = "确认还原",
            Owner = owner,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)App_res("Panel"),
        };
        DarkTitleBar.SetTheme(w, ThemeManager.IsDark);

        var root = new StackPanel();
        root.Children.Add(new TextBlock
        {
            Text = "确认还原",
            Margin = new Thickness(20, 16, 20, 0),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)App_res("TextPrimary"),
        });
        root.Children.Add(new TextBlock
        {
            Text = $"将「{gameName}」还原到 {r.TimeText} 的{r.KindText}备份？",
            Margin = new Thickness(20, 8, 20, 0),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)App_res("TextSecondary"),
        });
        root.Children.Add(new TextBlock
        {
            Text = $"备注：{r.Note}\n还原前会自动快照当前存档。",
            Margin = new Thickness(20, 10, 20, 0),
            FontSize = 12.5,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)App_res("TextSecondary"),
        });
        // 安全快照 = 上次还原前的进度：点明"撤销上次还原"语义，避免误以为在重复还原
        if (r.Note == "还原前安全快照")
            root.Children.Add(new TextBlock
            {
                Text = "这是上次还原前自动保存的进度，还原它相当于撤销上次还原。",
                Margin = new Thickness(20, 8, 20, 0),
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)App_res("Accent"),
            });
        var cb = new System.Windows.Controls.CheckBox
        {
            Content = "严格还原：同时删除备份中不存在的新文件",
            FontSize = 12.5,
            Margin = new Thickness(20, 12, 20, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = (System.Windows.Media.Brush)App_res("TextPrimary"),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        cb.Checked += (_, _) => purge = true;
        cb.Unchecked += (_, _) => purge = false;
        root.Children.Add(cb);

        var ok = new System.Windows.Controls.Button
        {
            Content = "还原", Height = 30,
            Style = (Style)App_res("BtnPrimary"), IsDefault = true,
        };
        var cancel = new System.Windows.Controls.Button
        {
            Content = "取消", Height = 30,
            Style = (Style)App_res("Btn"), IsCancel = true,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var panel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 20, 16),
        };
        panel.Children.Add(ok);
        panel.Children.Add(cancel);
        root.Children.Add(panel);
        w.Content = root;

        ok.Click += (_, _) => w.DialogResult = true;
        var okResult = w.ShowDialog() == true;
        return (okResult, purge);
    }

    /// <summary>取应用级资源（App.xaml 已合并主题字典，键必然存在）。</summary>
    private static object App_res(string key) => System.Windows.Application.Current.FindResource(key);
}

/// <summary>删除本地备份时的云端副本处理弹窗：一个「同时删除云端副本」可选项，
/// 勾选状态默认沿用上次用户选择（Store.Settings.CloudDelWithCloud）；
/// 用户每次操作后勾选状态回写设置，下次默认沿用，但每次都会弹对话框。</summary>
public class CloudDeleteDialog : Window
{
    public bool DeleteCloudToo { get; private set; }
    private readonly System.Windows.Controls.CheckBox _withCloud;

    public CloudDeleteDialog(Window owner, string game, string time, string size, int count = 1)
    {
        Title = "删除备份";
        DarkTitleBar.SetTheme(this, ThemeManager.IsDark);
        Owner = owner;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (System.Windows.Media.Brush)TryFindResource("Panel")!;

        var root = new StackPanel();
        root.Children.Add(new TextBlock
        {
            Text = count == 1 ? "确认删除这份备份？" : $"确认删除这 {count} 份备份？",
            Margin = new Thickness(20, 16, 20, 0),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)TryFindResource("TextPrimary")!,
        });
        root.Children.Add(new TextBlock
        {
            Text = count == 1 ? $"{game} · {time} · {size}" : $"{game} · 共 {count} 份备份",
            Margin = new Thickness(20, 8, 20, 0),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)TryFindResource("TextSecondary")!,
        });
        root.Children.Add(new TextBlock
        {
            Text = "· 不勾选下方选项：仅删除本地，云端副本保留，之后可在「☁ 云端备份」里查看或还原\n· 勾选下方选项：本地和云端副本一起删除（云端删除不可恢复）",
            Margin = new Thickness(20, 12, 20, 0),
            FontSize = 12.5,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)TryFindResource("TextSecondary")!,
        });
        _withCloud = new System.Windows.Controls.CheckBox
        {
            Content = "同时删除云端副本",
            FontSize = 13,
            Margin = new Thickness(20, 14, 20, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsChecked = Store.Settings?.CloudDelWithCloud == true,
            Foreground = (System.Windows.Media.Brush)TryFindResource("TextPrimary")!,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        root.Children.Add(_withCloud);

        var del = new System.Windows.Controls.Button { Content = "删除", Height = 30, Style = (Style)TryFindResource("Btn")!, IsDefault = true };
        var cancel = new System.Windows.Controls.Button { Content = "取消", Height = 30, Style = (Style)TryFindResource("Btn")!, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var panel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 20, 16),
        };
        panel.Children.Add(del);
        panel.Children.Add(cancel);
        root.Children.Add(panel);

        Content = root;
        del.Click += (_, _) => { DeleteCloudToo = _withCloud.IsChecked == true; DialogResult = true; };
    }
}
