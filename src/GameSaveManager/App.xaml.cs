using System.IO;
using System.Reflection;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using GameSaveManager.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace GameSaveManager;

/// <summary>深色标题栏：通过 DWM 属性让原生标题栏（含最小化/最大化/关闭按钮）跟随深色主题。
/// Windows 11 / Windows 10 较新版本支持；不支持时静默无效（标题栏保持系统默认）。</summary>
public static class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;   // Win11 22000 起；Win10 旧版曾用 19

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>在窗口的 SourceInitialized 之后（有 HWND）调用；也可对 Loaded 的窗口直接调用。</summary>
    public static void Enable(Window window) => SetTheme(window, dark: true);

    /// <summary>按当前主题设置原生标题栏深浅；窗口句柄尚未创建时自动挂到 SourceInitialized。</summary>
    public static void SetTheme(Window window, bool dark)
    {
        try
        {
            var helper = new WindowInteropHelper(window);
            IntPtr hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero)
            {
                window.SourceInitialized += (_, _) => Apply(new WindowInteropHelper(window).Handle, dark);
                return;
            }
            Apply(hwnd, dark);
        }
        catch { /* 旧系统无此 DWM 属性：忽略 */ }
    }

    private static void Apply(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }
}

/// <summary>发布时间：publish 时用 -p:BuildDate="yyyy-MM-dd HH:mm" 写入程序集 AssemblyMetadata；
/// 开发构建未写入时回退为主程序文件的最后修改时间。</summary>
internal static class BuildInfo
{
    public static string PublishTimeText
    {
        get
        {
            try
            {
                var meta = Assembly.GetExecutingAssembly()
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => a.Key == "BuildDate");
                if (!string.IsNullOrWhiteSpace(meta?.Value))
                    return meta.Value;
            }
            catch { /* 反射失败走文件时间回退 */ }

            try
            {
                string? path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
            }
            catch { /* 文件时间不可用 */ }

            return "未知";
        }
    }
}

public partial class App : Application
{
    // Local\ 前缀：按会话/用户隔离，不需要管理员权限，也不会被其他用户的同名对象干扰
    private const string SingleInstanceMutexName = @"Local\GameSaveVault.SingleInstance";
    private const string ActivateEventName = @"Local\GameSaveVault.Activate";
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        CleanupOldBundleExtractDirs();

        // 单实例：已有实例运行时，唤醒它的主窗口后本进程直接退出
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var ev))
                    using (ev) ev.Set();   // 通知已有实例把窗口提到前台
            }
            catch { /* 权限/对象消失等：静默退出即可 */ }
            Shutdown();
            return;
        }
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        StartActivateListener();

        // UI 线程未处理异常：弹窗提示并写日志，避免启动期 XAML 异常无声闪退。
        // 必须在 base.OnStartup（创建主窗口）之前注册。
        DispatcherUnhandledException += (_, args) =>
        {
            string dump = FlattenException(args.Exception);
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsm-crash.txt"), dump); } catch { }
            // 致命错误（OOM 等）吞掉只会让进程进入不可预测状态：交给运行时终止并留崩痕；
            // 其余普通异常弹窗后继续运行（保持原有容错行为）
            if (args.Exception is OutOfMemoryException or StackOverflowException or System.Threading.ThreadAbortException)
            {
                args.Handled = false;
                return;
            }
            args.Handled = true;
            MessageBox.Show($"发生错误：\n{dump}",
                "GameSaveVault", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        // 非 UI 线程/后台任务的未处理异常：UI 处理器接不到，至少写进崩溃日志便于排查
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsm-crash.txt"),
                    $"[{DateTime.Now:HH:mm:ss}] 非 UI 线程未处理异常（终止={args.IsTerminating}）：\n"
                    + FlattenException(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString() ?? "?")));
            }
            catch { }
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsm-crash.txt"),
                    $"[{DateTime.Now:HH:mm:ss}] 后台任务未观察异常：{FlattenException(args.Exception)}\n");
            }
            catch { }
            args.SetObserved();
        };

        // 窗口创建前先加载数据与外观主题，保证主窗口首帧即是正确主题。
        Store.Init();
        ThemeManager.Initialize();

        base.OnStartup(e);
    }

    /// <summary>监听第二个实例发出的激活信号：还原最小化并把主窗口提到前台。</summary>
    private void StartActivateListener()
    {
        var thread = new System.Threading.Thread(() =>
        {
            while (_activateEvent != null)
            {
                try
                {
                    if (!_activateEvent.WaitOne()) return;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var w = MainWindow;
                        if (w == null) return;
                        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                        w.Show();
                        w.Activate();
                        w.Topmost = true;
                        w.Topmost = false;
                    }));
                }
                catch { /* 进程退出时事件释放，监听线程结束 */ return; }
            }
        })
        { IsBackground = true, Name = "GSM-Activate" };
        thread.Start();
    }

    /// <summary>单文件发布时，.NET 运行时把框架 DLL（coreclr.dll、UIAutomationTypes.dll 等）
    /// 解压到 %TEMP%\.net\GameSaveVault\&lt;hash&gt;\；进程崩溃会残留旧目录。
    /// 启动时清理：当前进程的解压目录绝不清理（其中 UIAutomationTypes 等是 WPF
    /// 创建主窗口时才延迟加载的，误删会导致启动期 FileNotFoundException）；
    /// 且只清理 1 天前的目录，避免与其他刚启动的实例竞争。</summary>
    private static void CleanupOldBundleExtractDirs()
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), ".net", "GameSaveVault");
            if (!Directory.Exists(root)) return;

            // 单文件模式下 AppContext.BaseDirectory 指向当前进程的解压目录；
            // 非单文件时指向程序目录（不可能等于 root 的子目录，天然不会误判）。
            string? currentDir = null;
            try
            {
                currentDir = Path.GetFullPath(AppContext.BaseDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { }

            var cutoff = DateTime.Now.AddDays(-1);
            foreach (var dir in Directory.GetDirectories(root))
            {
                try
                {
                    string full = Path.GetFullPath(dir)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(full, currentDir, StringComparison.OrdinalIgnoreCase))
                        continue;                                  // 当前进程正在使用
                    if (Directory.GetLastWriteTime(full) > cutoff)
                        continue;                                  // 刚创建的（其他实例），不动
                    Directory.Delete(full, true);
                }
                catch { /* 被占用或无权限，下次启动再清 */ }
            }
        }
        catch { }
    }

    /// <summary>递归展开异常链（含 AggregateException 的所有内部异常），返回完整信息。</summary>
    private static string FlattenException(Exception ex, int depth = 0)
    {
        var sb = new System.Text.StringBuilder();
        string indent = new string(' ', depth * 2);
        sb.AppendLine($"{indent}[{ex.GetType().FullName}] {ex.Message}");
        if (!string.IsNullOrEmpty(ex.StackTrace))
            sb.AppendLine(ex.StackTrace);
        if (ex is System.AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions)
                sb.Append(FlattenException(inner, depth + 1));
        }
        else if (ex.InnerException != null)
        {
            sb.AppendLine($"{indent}---- InnerException ----");
            sb.Append(FlattenException(ex.InnerException, depth + 1));
        }
        return sb.ToString();
    }
}
