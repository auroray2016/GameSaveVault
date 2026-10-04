using Microsoft.Win32;
using System.Windows;
using GameSaveManager.Services;
using Application = System.Windows.Application;

namespace GameSaveManager;

/// <summary>
/// 外观主题管理：system（跟随系统）/ light（奶油白清新）/ dark（优化深色）。
/// 切换时替换 Application 资源中的主题字典，所有以 DynamicResource 引用的画笔即时生效，
/// 并同步各窗口原生标题栏（DWM 深色属性）。
/// </summary>
public static class ThemeManager
{
    public const string System = "system";
    public const string Light = "light";          // 清新浅色（奶油白暖调）
    public const string LightSoft = "lightsoft";  // 素雅浅色（冷白灰蓝调）
    public const string Dark = "dark";

    /// <summary>用户选择的模式（system/light/dark）。</summary>
    public static string Mode { get; private set; } = System;
    /// <summary>当前实际生效的主题（system 解析后为 light/dark）。</summary>
    public static string Actual { get; private set; } = Dark;
    public static bool IsDark => Actual == Dark;

    /// <summary>主题实际生效后触发（含 system 模式下跟随系统切换）：供主界面刷新主题图标/菜单勾选。</summary>
    public static event Action? ThemeChanged;

    private static bool _listening;

    /// <summary>启动早期（窗口创建前）调用：读取设置、应用主题、开始跟随系统。</summary>
    public static void Initialize()
    {
        Mode = Normalize(Store.Settings.Theme);
        StartSystemListener();
        Apply(Resolve(Mode));
    }

    /// <summary>设置中切换模式：更新设置并即时生效（持久化由调用方负责）。</summary>
    public static void SetMode(string mode)
    {
        Mode = Normalize(mode);
        Store.Settings.Theme = Mode;
        Apply(Resolve(Mode));
    }

    public static string Normalize(string? mode) =>
        mode switch { Light => Light, LightSoft => LightSoft, Dark => Dark, _ => System };

    private static string Resolve(string mode) =>
        mode == System ? GetSystemTheme() : mode;

    /// <summary>读取系统「应用深浅色」偏好：注册表 AppsUseLightTheme（0=深色，1=浅色；缺省按浅色）。</summary>
    public static string GetSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
                return value == 0 ? Dark : Light;
        }
        catch { /* 注册表不可读：按浅色处理 */ }
        return Light;
    }

    private static void Apply(string actual)
    {
        Actual = actual;

        // 替换 MergedDictionaries 中的活动主题字典（保留其它字典）。
        var merged = Application.Current.Resources.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            string src = merged[i].Source?.OriginalString ?? "";
            if (src.Contains("Themes/"))
                merged.RemoveAt(i);
        }
        string file = actual switch
        {
            Light => "Light",
            LightSoft => "LightSoft",
            _ => "Dark",
        };
        // 显式指定程序集：相对 URI 只按入口程序集解析，测试宿主/单文件场景下会指向错误程序集。
        merged.Insert(0, new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/GameSaveVault;component/Themes/{file}.xaml",
                UriKind.Absolute)
        });

        // 同步已存在窗口的原生标题栏深浅。
        bool dark = actual == Dark;
        foreach (Window window in Application.Current.Windows)
            DarkTitleBar.SetTheme(window, dark);

        ThemeChanged?.Invoke();
    }

    private static void StartSystemListener()
    {
        if (_listening) return;
        _listening = true;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.Invoke(() =>
            {
                if (Mode == System) Apply(GetSystemTheme());
            });
        };
    }
}
