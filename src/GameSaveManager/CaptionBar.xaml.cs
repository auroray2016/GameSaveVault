using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;

namespace GameSaveManager;

/// <summary>子窗口通用自绘标题栏。挂到任意窗口内容顶部（两行包装 Grid 的 Row 0）即可：
/// Loaded 时自动给宿主窗口挂与主窗口一致的 WindowChrome，同步图标/标题，
/// 按 <see cref="Window.ResizeMode"/> 隐藏不可用按钮（NoResize 只留关闭键），
/// 可缩放窗口最大化时给包装容器 6px 边距补偿、字形在 □/❐ 间切换（行为同主窗口）。</summary>
public partial class CaptionBar : System.Windows.Controls.UserControl
{
    private Window? _win;
    private bool _maxWired;

    public CaptionBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var win = Window.GetWindow(this);
        if (win == null || ReferenceEquals(_win, win)) return;
        _win = win;

        // 图标/标题：无 Icon 的窗口（如编辑游戏）只显示文字，不留空位
        WinIcon.Source = win.Icon;
        WinIcon.Visibility = win.Icon != null ? Visibility.Visible : Visibility.Collapsed;
        WinTitle.Text = win.Title;

        // 自绘 Chrome：参数与 MainWindow 完全一致（CaptionHeight 32、直角、关 Aero 按钮、1px 底框）
        WindowChrome.SetWindowChrome(win, new WindowChrome
        {
            CaptionHeight = 32,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        // NoResize：只能关闭；CanMinimize：无最大化；CanResize：三键齐全
        BtnMin.Visibility = win.ResizeMode == ResizeMode.NoResize ? Visibility.Collapsed : Visibility.Visible;
        BtnMax.Visibility = win.ResizeMode == ResizeMode.CanResize ? Visibility.Visible : Visibility.Collapsed;

        if (win.ResizeMode == ResizeMode.CanResize && !_maxWired)
        {
            win.StateChanged += OnStateChanged;
            _maxWired = true;
        }
        SyncMaximized();
    }

    private void OnStateChanged(object? sender, System.EventArgs e) => SyncMaximized();

    /// <summary>最大化时包装容器缩 6px 防屏幕边缘裁切（与主窗口 RootGrid 补偿一致）；
    /// Parent 即承载 CaptionBar 的两行包装 Grid。</summary>
    private void SyncMaximized()
    {
        if (_win == null) return;
        bool max = _win.WindowState == WindowState.Maximized;
        if (Parent is FrameworkElement host)
            host.Margin = max ? new Thickness(6) : new Thickness(0);
        GlyphMax.Text = max ? "\uE923" : "\uE922";
        BtnMax.ToolTip = max ? "向下还原" : "最大化";
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        if (_win != null) _win.WindowState = WindowState.Minimized;
    }

    private void OnToggleMax(object sender, RoutedEventArgs e)
    {
        if (_win == null) return;
        _win.WindowState = _win.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnClose(object sender, RoutedEventArgs e) => _win?.Close();
}
