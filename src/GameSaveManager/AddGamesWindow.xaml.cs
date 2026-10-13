using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using GameSaveManager.Services;
using Forms = System.Windows.Forms;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace GameSaveManager;

/// <summary>添加游戏向导：文件夹批量扫描 + 手动指定 exe 统一在一个窗口里勾选入库。
/// 窗口只负责给出待入库 exe 清单（<see cref="SelectedPicks"/>），入库动作由 MainWindow 执行。</summary>
public partial class AddGamesWindow : Window
{
    /// <summary>点「添加所选」后：勾选且不在库的候选。</summary>
    public List<FolderGameCandidate> SelectedPicks { get; private set; } = new();

    private readonly ObservableCollection<AddCandidateVm> _items = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private bool _scanning;

    public AddGamesWindow()
    {
        InitializeComponent();
        // 自绘标题栏外仍有 1px DWM 边框，按主题染深浅（与其余子窗口一致）
        DarkTitleBar.SetTheme(this, ThemeManager.IsDark);
        CandidateList.ItemsSource = _items;
        UpdateSummary();
    }

    /// <summary>相同 exe / 相同安装目录是否已在游戏库（已归档留档的不算：允许重新勾选入库，触发档案复活）。</summary>
    private static bool IsInLibrary(FolderGameCandidate c) =>
        Store.Games.Any(g => !g.Archived &&
            (!string.IsNullOrWhiteSpace(g.ExePath) && g.ExePath.Equals(c.Exe, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(g.InstallDir) && c.Dir.Equals(g.InstallDir, StringComparison.OrdinalIgnoreCase))));

    private void AddCandidates(IEnumerable<FolderGameCandidate> candidates)
    {
        int added = 0, dup = 0;
        foreach (var c in candidates)
        {
            if (!_seen.Add(c.Exe)) { dup++; continue; }
            bool inLib = IsInLibrary(c);
            var vm = new AddCandidateVm(c, inLib);
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AddCandidateVm.Selected)) UpdateSummary(); };
            _items.Add(vm);
            if (inLib) dup++; else added++;
        }
        UpdateSummary(added, dup);
    }

    private async void OnPickFolder(object sender, RoutedEventArgs e)
    {
        if (_scanning) return;
        using var dlg = new Forms.FolderBrowserDialog
        {
            Description = "选择游戏根目录（自动扫描其下 1–3 层子目录的游戏 exe）",
            ShowNewFolderButton = false,
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog() != Forms.DialogResult.OK) return;

        _scanning = true;
        BtnPickFolder.IsEnabled = false;
        BtnPickExe.IsEnabled = false;
        StatusText.Text = "正在扫描「" + dlg.SelectedPath + "」…";
        try
        {
            var found = await Task.Run(() => FolderScanner.Scan(dlg.SelectedPath));
            if (found.Count == 0)
            {
                StatusText.Text = "这个目录里没扫到游戏主程序，可换个上层目录，或用「手动指定 exe」。";
                return;
            }
            AddCandidates(found);
        }
        catch (Exception ex)
        {
            StatusText.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            _scanning = false;
            BtnPickFolder.IsEnabled = true;
            BtnPickExe.IsEnabled = true;
        }
    }

    private void OnPickExe(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择游戏主程序 exe（可多选）",
            Filter = "可执行文件 (*.exe)|*.exe",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        AddCandidates(dlg.FileNames.Select(FolderScanner.CandidateFromExe));
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        SelectedPicks = _items.Where(v => v.Selected && !v.InLibrary)
            .Select(v => v.Candidate).ToList();
        if (SelectedPicks.Count == 0)
        {
            StatusText.Text = "还没有勾选要添加的游戏。";
            return;
        }
        DialogResult = true;
    }

    private void UpdateSummary(int? justAdded = null, int? justSkipped = null)
    {
        int newCount = _items.Count(v => !v.InLibrary);
        int libCount = _items.Count - newCount;
        int picked = _items.Count(v => v.Selected);
        BtnConfirm.Content = picked > 0 ? $"添加所选 ({picked})" : "添加所选";
        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_items.Count == 0) return;
        string text = $"共 {_items.Count} 个候选";
        if (libCount > 0) text += $"，{libCount} 个已在库（自动跳过）";
        if (justAdded is > 0) text += $"；本次新增 {justAdded} 个";
        StatusText.Text = text + "。";
    }
}

/// <summary>向导列表行：候选 + 是否在库 + 勾选态。在库行不可勾；勾选变化通知窗口刷新底部计数。</summary>
public sealed class AddCandidateVm : INotifyPropertyChanged
{
    public FolderGameCandidate Candidate { get; }
    public string DisplayName => Candidate.ProductName;
    public string Exe => Candidate.Exe;
    public bool InLibrary { get; }
    public bool CanSelect => !InLibrary;
    public Visibility InLibraryVis => InLibrary ? Visibility.Visible : Visibility.Collapsed;

    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            OnPropertyChanged(nameof(Selected));
        }
    }

    public AddCandidateVm(FolderGameCandidate candidate, bool inLibrary)
    {
        Candidate = candidate;
        InLibrary = inLibrary;
        _selected = !inLibrary;   // 新游戏默认勾选
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
