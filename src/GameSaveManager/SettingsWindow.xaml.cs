using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameSaveManager.Services;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
// WPF/WinForms 隐式 using 二义时，固定使用 WPF 类型
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace GameSaveManager;

public partial class SettingsWindow : Window
{
    public string DataDir => Store.BaseDir;
    private List<NameSourceDef> _sources = new();
    private List<AppIdSourceDef> _appIdSources = new();
    /// <summary>待保存的热键组合（""=禁用）；录入期间为 null 表示正在等待按键。</summary>
    private string? _hotKeyCombo;
    private bool _capturingHotKey;
    /// <summary>进入设置窗口时已保存的云应用密码密文（密码框留空表示不修改）。</summary>
    private string _cloudSecretStored = "";
    /// <summary>进入设置窗口时已保存的备份目录（用于检测「本次修改了目录」，记录旧根供迁移认领）。</summary>
    private string _origBackupDir = "";
    private bool _cloudBusy;
    /// <summary>保存成功后需要带给主窗口的补充提示（如云同步半成品未启用）；null = 普通"设置已保存"。</summary>
    public string? CloudNotice { get; private set; }

    public SettingsWindow()
    {
        InitializeComponent();
        DarkTitleBar.SetTheme(this, ThemeManager.IsDark);
        DataContext = this;
        AboutVersionText.Text = "版本 " + (System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "1.0.0");
        AboutBuildText.Text = "发布时间 " + BuildInfo.PublishTimeText;
        // 外观：选中当前模式（Checked 事件会比对 Mode，相同则不重复落盘）
        (ThemeManager.Mode switch
        {
            ThemeManager.Light => ThemeLightBtn,
            ThemeManager.LightSoft => ThemeLightSoftBtn,
            ThemeManager.Dark => ThemeDarkBtn,
            _ => ThemeSystemBtn,
        }).IsChecked = true;
        MaxKeepBox.Text = Store.Settings.MaxKeepPerGame.ToString();
        FullEveryBox.Text = Store.Settings.FullEveryN.ToString();
        IncrChk.IsChecked = Store.Settings.IncrementalEnabled;
        HotMinutesBox.Text = Math.Clamp(Store.Settings.HotSnapshotMinutes, 5, 120).ToString();
        StartupScanChk.IsChecked = Store.Settings.ScanOnStartup;
        _hotKeyCombo = Store.Settings.BackupHotKey ?? "";
        _origBackupDir = Store.Settings.BackupDir ?? "";
        UpdateBackupDirBox();
        UpdateIncrRow();
        UpdateHotKeyBox();
        LoadCloud();
        LoadSources();
        LoadAppIdSources();
    }

    // ────────────── 左侧导航分页 ──────────────

    /// <summary>切换右侧分页；InitializeComponent 解析导航项时页面字段可能尚未建立，需空值保护。</summary>
    private void OnNavCheck(object sender, RoutedEventArgs e)
    {
        if (PageGeneral == null || PageBackup == null || PageCloud == null
            || PageData == null || PageAbout == null) return;
        PageGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageBackup.Visibility = NavBackup.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageCloud.Visibility = NavCloud.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageData.Visibility = NavData.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ────────────── 外观主题 ──────────────

    /// <summary>点选主题模式：ThemeManager 即时换肤，随后静默落盘；与当前模式相同则跳过。</summary>
    private void OnThemeModeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        string mode = ThemeManager.Normalize(tag);
        if (mode == ThemeManager.Mode) return;
        ThemeManager.SetMode(mode);
        Store.SaveSettings();
    }

    // ────────────── 云同步（WebDAV） ──────────────

    private void LoadCloud()
    {
        var s = Store.Settings;
        CloudUrlBox.Text = s.CloudUrl;
        CloudUserBox.Text = s.CloudUser;
        CloudDirBox.Text = string.IsNullOrWhiteSpace(s.CloudDir) ? "GameSaveVault" : s.CloudDir;
        _cloudSecretStored = s.CloudSecret ?? "";
        CloudDelWithCloudChk.IsChecked = s.CloudDelWithCloud;
        DefaultPlayerNameBox.Text = s.DefaultPlayerName;
    }

    /// <summary>用对话框当前值构造 WebDAV 客户端（密码框留空则沿用已保存密码）。失败返回错误信息。</summary>
    private (WebDavClient? client, string? err) BuildCloudClient()
    {
        string url = CloudUrlBox.Text.Trim();
        string user = CloudUserBox.Text.Trim();
        string dir = (CloudDirBox.Text.Trim().Trim('/'));
        if (dir.Length == 0) dir = "GameSaveVault";
        string pass = CloudPassBox.Password;
        if (pass.Length == 0) pass = CloudSecret.Unprotect(_cloudSecretStored);
        if (string.IsNullOrWhiteSpace(url)) return (null, "请填写 WebDAV 服务器地址");
        if (string.IsNullOrWhiteSpace(user)) return (null, "请填写账号");
        if (string.IsNullOrEmpty(pass)) return (null, "请填写应用密码（坚果云需使用应用专用密码）");
        try { return (new WebDavClient(url, user, pass, dir), null); }
        catch (Exception ex) { return (null, ex.Message); }
    }

    private void SetCloudStatus(string msg, bool error = false, bool hint = false)
    {
        CloudStatusText.Text = msg;
        // error=红色（真正的操作失败）；hint=中性灰（信息没填全的温馨提醒，不是出错）；默认=强调色成功态
        CloudStatusText.Foreground = (Brush)FindResource(error ? "Red" : hint ? "TextSecondary" : "Accent2");
    }

    /// <summary>云同步信息未填全时的温和提示（不当作错误，不标红、不加 ✗）。</summary>
    private void CloudConfigHint(string? missing)
        => SetCloudStatus("云同步还没配置完整" + (string.IsNullOrEmpty(missing) ? "" : "：" + missing)
            + "。补全服务器地址、账号和应用密码后即可正常使用，未配置不影响本机备份。", hint: true);

    private async void OnCloudTest(object sender, RoutedEventArgs e)
    {
        if (_cloudBusy) return;
        var (client, err) = BuildCloudClient();
        if (client == null) { CloudConfigHint(err); return; }
        _cloudBusy = true; CloudTestBtn.IsEnabled = false;
        SetCloudStatus("正在连接…");
        try
        {
            var (ok, msg) = await client.TestAsync();
            SetCloudStatus((ok ? "✓ " : "✗ ") + msg, !ok);
        }
        catch (Exception ex) { SetCloudStatus("✗ " + ex.Message, true); }
        finally { _cloudBusy = false; CloudTestBtn.IsEnabled = true; }
    }

    private async void OnCloudUploadAll(object sender, RoutedEventArgs e)
    {
        if (_cloudBusy) return;
        var (client, err) = BuildCloudClient();
        if (client == null) { CloudConfigHint(err); return; }
        _cloudBusy = true;
        CloudTestBtn.IsEnabled = CloudUploadBtn.IsEnabled = CloudBrowseBtn.IsEnabled = false;
        SetCloudStatus("正在上传全部未同步备份…");
        try
        {
            var progress = new Progress<string>(m => SetCloudStatus(m));
            var (ok, fail, errors) = await CloudSyncService.UploadAllAsync(client, progress);
            if (fail == 0) SetCloudStatus($"✓ 上传完成，新上传 {ok} 份");
            else SetCloudStatus($"完成：成功 {ok} 份，失败 {fail} 份。" + string.Join("；", errors), true);
        }
        catch (Exception ex) { SetCloudStatus("✗ " + ex.Message, true); }
        finally
        {
            _cloudBusy = false;
            CloudTestBtn.IsEnabled = CloudUploadBtn.IsEnabled = CloudBrowseBtn.IsEnabled = true;
        }
    }

    private void OnCloudBrowse(object sender, RoutedEventArgs e)
    {
        if (_cloudBusy) return;   // 防重入：双击/焦点回车连开两个浏览窗
        var (client, err) = BuildCloudClient();
        if (client == null) { CloudConfigHint(err); return; }
        _cloudBusy = true;
        CloudTestBtn.IsEnabled = CloudUploadBtn.IsEnabled = CloudBrowseBtn.IsEnabled = false;
        try
        {
            new CloudBrowserWindow(client) { Owner = this }.ShowDialog();
        }
        finally
        {
            _cloudBusy = false;
            CloudTestBtn.IsEnabled = CloudUploadBtn.IsEnabled = CloudBrowseBtn.IsEnabled = true;
        }
    }

    /// <summary>HTTP 明文地址实时风险提示（HTTPS/空地址不显示）。</summary>
    private void OnCloudUrlChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        bool insecure = CloudUrlBox.Text.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        HttpWarnText.Visibility = insecure ? Visibility.Visible : Visibility.Collapsed;
    }

    // ────────────── 全局热备份快捷键 ──────────────

    private const string DefaultHotKey = "Ctrl+Shift+B";

    private void UpdateHotKeyBox()
    {
        HotKeyBox.Text = string.IsNullOrEmpty(_hotKeyCombo) ? "（已禁用）" : _hotKeyCombo;
    }

    private void OnHotKeyGotFocus(object s, RoutedEventArgs e)
    {
        _capturingHotKey = true;
        HotKeyBox.Text = "请按下组合键…";
    }

    private void OnHotKeyLostFocus(object s, RoutedEventArgs e)
    {
        // 未录入新组合：还原当前值
        if (_capturingHotKey)
        {
            _capturingHotKey = false;
            UpdateHotKeyBox();
        }
    }

    private void OnHotKeyPreviewKeyDown(object s, KeyEventArgs e)
    {
        if (!_capturingHotKey) return;
        e.Handled = true;   // 只读框：吞掉所有按键，避免焦点移动/铃声
        // Esc 放弃录入；Backspace/Delete 清除（禁用热键）
        if (e.Key == Key.Escape) { MoveFocusFromHotKey(); return; }
        if (e.Key is Key.Back or Key.Delete)
        {
            _hotKeyCombo = "";
            _capturingHotKey = false;
            UpdateHotKeyBox();
            MoveFocusFromHotKey();
            return;
        }
        // 单独按修饰键不确认，等待真正的主键
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System) return;
        if (GlobalHotKeyService.TryFormat(e.Key, Keyboard.Modifiers, out string combo))
        {
            _hotKeyCombo = combo;
            _capturingHotKey = false;
            HotKeyBox.Text = combo;
            MoveFocusFromHotKey();
        }
        // 不合法的主键（无修饰键/不支持的键）：留在录入态继续等
    }

    private void OnHotKeyClear(object s, RoutedEventArgs e)
    {
        _hotKeyCombo = "";
        _capturingHotKey = false;
        UpdateHotKeyBox();
    }

    private void OnHotKeyDefault(object s, RoutedEventArgs e)
    {
        _hotKeyCombo = DefaultHotKey;
        _capturingHotKey = false;
        UpdateHotKeyBox();
    }

    private void MoveFocusFromHotKey() =>
        HotKeyClearBtn.Focus();

    // ────────────── 中文名数据源管理 ──────────────

    private void LoadSources()
    {
        _sources = GameNameService.GetSources();
        SourceList.ItemsSource = null;
        SourceList.ItemsSource = _sources;
        if (_sources.Count > 0) SourceList.SelectedIndex = 0;
    }

    private NameSourceDef? SelectedSource => SourceList.SelectedItem as NameSourceDef;

    private void OnAddSource(object s, RoutedEventArgs e)
    {
        var def = new NameSourceDef
        {
            Id = "custom_" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "新数据源",
            Enabled = true,
            Priority = (_sources.Count + 1) * 10,
            UrlTemplate = "https://example.com/api/game?appid={appid}",
            ExtractType = "json",
            ExtractExpr = "result.name",
            TimeoutSec = 8,
        };
        if (EditSourceDialog(def) != true) return;
        _sources.Add(def);
        SaveAndReload();
        SourceList.SelectedItem = def;
    }

    private void OnEditSource(object s, RoutedEventArgs e)
    {
        var def = SelectedSource;
        if (def == null) return;
        if (def.BuiltIn)
        {
            MessageBox.Show(this, "内置数据源不可修改（程序升级时会自动更新）。\n可取消勾选禁用它，再用「添加」创建自定义源。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (EditSourceDialog(def) == true) SaveAndReload();
    }

    private void OnDeleteSource(object s, RoutedEventArgs e)
    {
        var def = SelectedSource;
        if (def == null) return;
        if (def.BuiltIn)
        {
            MessageBox.Show(this, "内置数据源不可删除，取消勾选即可禁用。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"删除数据源「{def.DisplayName}」？", "确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _sources.Remove(def);
        SaveAndReload();
    }

    private void OnMoveSourceUp(object s, RoutedEventArgs e)
    {
        int i = SourceList.SelectedIndex;
        if (i <= 0) return;
        (_sources[i - 1], _sources[i]) = (_sources[i], _sources[i - 1]);
        ReassignPriorities();
        SaveAndReload();
        SourceList.SelectedIndex = i - 1;
    }

    private void OnMoveSourceDown(object s, RoutedEventArgs e)
    {
        int i = SourceList.SelectedIndex;
        if (i < 0 || i >= _sources.Count - 1) return;
        (_sources[i + 1], _sources[i]) = (_sources[i], _sources[i + 1]);
        ReassignPriorities();
        SaveAndReload();
        SourceList.SelectedIndex = i + 1;
    }

    private void ReassignPriorities()
    {
        for (int i = 0; i < _sources.Count; i++) _sources[i].Priority = (i + 1) * 10;
    }

    private void SaveAndReload()
    {
        ReassignPriorities();
        GameNameService.SaveSources(_sources);
        LoadSources();
    }

    /// <summary>数据源编辑对话框（代码构建），带在线测试；返回 true 表示确认修改。</summary>
    private bool? EditSourceDialog(NameSourceDef def)
    {
        var win = new Window
        {
            Title = "编辑数据源", Width = 560, Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (Brush)FindResource("Bg"),
        };
        var nameBox = MakeBox(def.DisplayName, 300);
        var urlBox = MakeBox(def.UrlTemplate, 440);
        var exprBox = MakeBox(def.ExtractExpr, 440);
        var refBox = MakeBox(def.Referer, 440);
        var timeoutBox = MakeBox(def.TimeoutSec.ToString(), 60);
        var testAppIdBox = MakeBox("2680010", 100);
        var rbJson = new RadioButton { Content = "JSON 路径", IsChecked = def.ExtractType == "json", Foreground = (Brush)FindResource("TextPrimary") };
        var rbRegex = new RadioButton { Content = "正则（取第 1 个括号）", IsChecked = def.ExtractType == "regex", Foreground = (Brush)FindResource("TextPrimary"), Margin = new Thickness(12, 0, 0, 0) };
        var encBox = new ComboBox { Width = 100, Height = 28 };
        encBox.Items.Add("utf-8"); encBox.Items.Add("gbk");
        encBox.SelectedItem = def.Encoding;
        var resultText = new TextBlock
        {
            Text = "", FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextSecondary"), Margin = new Thickness(0, 8, 0, 0),
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(MakeLabel("显示名称"));
        panel.Children.Add(nameBox);
        panel.Children.Add(MakeLabel("URL 模板（{appid} 会被替换为 Steam AppId）"));
        panel.Children.Add(urlBox);
        var typePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        typePanel.Children.Add(MakeLabelInline("提取方式："));
        typePanel.Children.Add(rbJson);
        typePanel.Children.Add(rbRegex);
        panel.Children.Add(typePanel);
        panel.Children.Add(MakeLabel("提取表达式（JSON 如 result.name；正则如 <title>([^<]+)</title>）"));
        panel.Children.Add(exprBox);
        var optPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        optPanel.Children.Add(MakeLabelInline("Referer："));
        optPanel.Children.Add(refBox);
        panel.Children.Add(optPanel);
        var opt2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        opt2.Children.Add(MakeLabelInline("编码："));
        opt2.Children.Add(encBox);
        opt2.Children.Add(MakeLabelInline("超时(秒)："));
        opt2.Children.Add(timeoutBox);
        panel.Children.Add(opt2);
        panel.Children.Add(new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = (Brush)FindResource("BorderBrush"), Margin = new Thickness(0, 14, 0, 0) });
        panel.Children.Add(MakeLabel("测试（输入一个 AppId 实际请求验证）"));
        var testRow = new StackPanel { Orientation = Orientation.Horizontal };
        testRow.Children.Add(testAppIdBox);
        var testBtn = new Button { Content = "测试", Style = (Style)FindResource("BtnSm"), Margin = new Thickness(8, 0, 0, 0) };
        testRow.Children.Add(testBtn);
        panel.Children.Add(testRow);
        panel.Children.Add(resultText);
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var okBtn = new Button { Content = "确定", Style = (Style)FindResource("BtnPrimary"), Width = 80, IsDefault = true };
        var cancelBtn = new Button { Content = "取消", Style = (Style)FindResource("Btn"), Width = 80, Margin = new Thickness(10, 0, 0, 0), IsCancel = true };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);
        panel.Children.Add(btnRow);
        win.Content = panel;

        testBtn.Click += async (_, _) =>
        {
            if (!int.TryParse(testAppIdBox.Text.Trim(), out int appId) || appId <= 0)
            {
                resultText.Text = "请输入有效的数字 AppId";
                resultText.Foreground = (Brush)FindResource("Red");
                return;
            }
            var probe = CollectFromDialog();
            testBtn.IsEnabled = false;
            resultText.Foreground = (Brush)FindResource("TextSecondary");
            resultText.Text = "请求中…";
            var (name, state) = await GameNameService.QuerySource(probe, appId);
            testBtn.IsEnabled = true;
            if (!string.IsNullOrWhiteSpace(name))
            {
                resultText.Foreground = (Brush)FindResource("Accent2");
                resultText.Text = $"✓ 提取成功：{name}";
            }
            else
            {
                resultText.Foreground = (Brush)FindResource("Red");
                resultText.Text = state switch
                {
                    SteamApi.NameSyncState.NetworkError => "✗ 网络错误：连接失败或超时（检查 URL/网络/是否需要代理）",
                    SteamApi.NameSyncState.RateLimited => "✗ 被限流（HTTP 429），稍后再试",
                    SteamApi.NameSyncState.NoLocalizedName => "△ 请求成功但提取到的不是中文名（检查表达式或该游戏无中文名）",
                    _ => "△ 请求成功但未提取到内容（检查提取表达式是否正确）",
                };
            }
        };

        NameSourceDef CollectFromDialog() => new()
        {
            Id = def.Id, BuiltIn = def.BuiltIn,
            DisplayName = nameBox.Text.Trim(),
            Enabled = def.Enabled,
            UrlTemplate = urlBox.Text.Trim(),
            ExtractType = rbJson.IsChecked == true ? "json" : "regex",
            ExtractExpr = exprBox.Text.Trim(),
            Referer = refBox.Text.Trim(),
            Encoding = (string?)encBox.SelectedItem ?? "utf-8",
            TimeoutSec = int.TryParse(timeoutBox.Text.Trim(), out int t) ? Math.Clamp(t, 3, 30) : 8,
            Priority = def.Priority,
        };

        bool? accepted = null;
        okBtn.Click += (_, _) =>
        {
            var d = CollectFromDialog();
            if (string.IsNullOrWhiteSpace(d.DisplayName) || string.IsNullOrWhiteSpace(d.UrlTemplate) ||
                string.IsNullOrWhiteSpace(d.ExtractExpr) || !d.UrlTemplate.Contains("{appid}"))
            {
                MessageBox.Show(win, "请填写名称、含 {appid} 的 URL 模板和提取表达式。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            def.DisplayName = d.DisplayName;
            def.UrlTemplate = d.UrlTemplate;
            def.ExtractType = d.ExtractType;
            def.ExtractExpr = d.ExtractExpr;
            def.Referer = d.Referer;
            def.Encoding = d.Encoding;
            def.TimeoutSec = d.TimeoutSec;
            accepted = true;
            win.DialogResult = true;
        };
        win.ShowDialog();
        return accepted;
    }

    private TextBlock MakeLabel(string text) => new()
    {
        Text = text, FontSize = 12, Foreground = (Brush)FindResource("TextSecondary"),
        Margin = new Thickness(0, 10, 0, 4), TextWrapping = TextWrapping.Wrap,
    };
    private TextBlock MakeLabelInline(string text) => new()
    {
        Text = text, FontSize = 12.5, Foreground = (Brush)FindResource("TextPrimary"),
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
    };
    private System.Windows.Controls.TextBox MakeBox(string text, double width) => new()
    {
        Text = text, Width = width, Height = 30, FontSize = 12.5, Padding = new Thickness(8, 0, 8, 0),
        VerticalContentAlignment = VerticalAlignment.Center,
        Background = (Brush)FindResource("Panel2"), Foreground = (Brush)FindResource("TextPrimary"),
        CaretBrush = (Brush)FindResource("TextPrimary"),
        BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(1),
    };

    // ────────────── AppId 搜索数据源管理 ──────────────

    private void LoadAppIdSources()
    {
        _appIdSources = AppIdSourceService.GetSources();
        AppIdSourceList.ItemsSource = null;
        AppIdSourceList.ItemsSource = _appIdSources;
        if (_appIdSources.Count > 0) AppIdSourceList.SelectedIndex = 0;
    }

    private AppIdSourceDef? SelectedAppIdSource => AppIdSourceList.SelectedItem as AppIdSourceDef;

    private void OnAddAppIdSource(object s, RoutedEventArgs e)
    {
        var def = new AppIdSourceDef
        {
            Id = "custom_" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "新 AppId 源",
            Enabled = true,
            Priority = (_appIdSources.Count + 1) * 10,
            UrlTemplate = "https://example.com/api/search?query={name_urlenc}",
            ListPath = "data.results",
            IdField = "id",
            NameField = "name",
            ImageField = "",
            TimeoutSec = 10,
        };
        if (EditAppIdSourceDialog(def) != true) return;
        _appIdSources.Add(def);
        SaveAppIdAndReload();
        AppIdSourceList.SelectedItem = def;
    }

    private void OnEditAppIdSource(object s, RoutedEventArgs e)
    {
        var def = SelectedAppIdSource;
        if (def == null) return;
        if (def.BuiltIn)
        {
            MessageBox.Show(this, "内置数据源不可修改（程序升级时自动更新）。\n可取消勾选禁用，再用「添加」创建自定义源。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (EditAppIdSourceDialog(def) == true) SaveAppIdAndReload();
    }

    /// <summary>「测试连接」：内置源不可编辑，但同样需要能验证连接是否正常。</summary>
    private void OnTestAppIdSource(object s, RoutedEventArgs e)
    {
        var def = SelectedAppIdSource;
        if (def == null)
        {
            MessageBox.Show(this, "请先选中一个数据源。", "测试连接", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var win = new Window
        {
            Title = "测试连接 · " + def.DisplayName, Width = 480,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            Background = (Brush)FindResource("Bg"),
        };
        var nameBox = MakeBox("Witcher", 300);
        var resultText = new TextBlock
        {
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextSecondary"), Margin = new Thickness(0, 10, 0, 0),
            Text = "输入一个游戏名，点测试实际搜索一次。",
        };
        var testBtn = new Button { Content = "测试", Style = (Style)FindResource("BtnSm") };
        var closeBtn = new Button
        {
            Content = "关闭", Style = (Style)FindResource("Btn"), Width = 80,
            Margin = new Thickness(10, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true,
        };
        var testRow = new StackPanel { Orientation = Orientation.Horizontal };
        testRow.Children.Add(nameBox); testRow.Children.Add(testBtn);
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        btnRow.Children.Add(closeBtn);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(MakeLabel("游戏名（中英文均可）"));
        panel.Children.Add(testRow);
        panel.Children.Add(resultText);
        panel.Children.Add(btnRow);
        win.Content = panel;

        testBtn.Click += async (_, _) =>
        {
            string q = nameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(q))
            {
                resultText.Foreground = (Brush)FindResource("Red");
                resultText.Text = "请输入游戏名";
                return;
            }
            testBtn.IsEnabled = false;
            resultText.Foreground = (Brush)FindResource("TextSecondary");
            resultText.Text = "搜索中…";
            var items = await AppIdSourceService.QuerySource(def, q);
            testBtn.IsEnabled = true;
            if (items == null)
            {
                resultText.Foreground = (Brush)FindResource("Red");
                resultText.Text = "✗ 连接异常：请求失败或超时（网络不通或被拦截）";
            }
            else if (items.Count == 0)
            {
                resultText.Foreground = (Brush)FindResource("Red");
                resultText.Text = "△ 连接正常、服务器有应答，但没有返回候选（换个游戏名再试）";
            }
            else
            {
                resultText.Foreground = (Brush)FindResource("Accent2");
                resultText.Text = "✓ 连接正常，返回 " + items.Count + " 条：" +
                    string.Join("；", items.Take(3).Select(c => $"{c.Name}(AppId {c.AppId})"));
            }
        };
        win.ShowDialog();
    }

    private void OnDeleteAppIdSource(object s, RoutedEventArgs e)
    {
        var def = SelectedAppIdSource;
        if (def == null) return;
        if (def.BuiltIn)
        {
            MessageBox.Show(this, "内置数据源不可删除，取消勾选即可禁用。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"删除数据源「{def.DisplayName}」？", "确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _appIdSources.Remove(def);
        SaveAppIdAndReload();
    }

    private void OnMoveAppIdSourceUp(object s, RoutedEventArgs e)
    {
        int i = AppIdSourceList.SelectedIndex;
        if (i <= 0) return;
        (_appIdSources[i - 1], _appIdSources[i]) = (_appIdSources[i], _appIdSources[i - 1]);
        SaveAppIdAndReload();
        AppIdSourceList.SelectedIndex = i - 1;
    }

    private void OnMoveAppIdSourceDown(object s, RoutedEventArgs e)
    {
        int i = AppIdSourceList.SelectedIndex;
        if (i < 0 || i >= _appIdSources.Count - 1) return;
        (_appIdSources[i + 1], _appIdSources[i]) = (_appIdSources[i], _appIdSources[i + 1]);
        SaveAppIdAndReload();
        AppIdSourceList.SelectedIndex = i + 1;
    }

    private void SaveAppIdAndReload()
    {
        for (int i = 0; i < _appIdSources.Count; i++) _appIdSources[i].Priority = (i + 1) * 10;
        AppIdSourceService.SaveSources(_appIdSources);
        LoadAppIdSources();
    }

    /// <summary>AppId 源编辑对话框（列表型 JSON 提取），带在线测试。</summary>
    private bool? EditAppIdSourceDialog(AppIdSourceDef def)
    {
        var win = new Window
        {
            Title = "编辑 AppId 数据源", Width = 560, Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (Brush)FindResource("Bg"),
        };
        var nameBox = MakeBox(def.DisplayName, 440);
        var urlBox = MakeBox(def.UrlTemplate, 440);
        var listBox = MakeBox(def.ListPath, 440);
        var idBox = MakeBox(def.IdField, 140);
        var nameFieldBox = MakeBox(def.NameField, 140);
        var imgBox = MakeBox(def.ImageField, 140);
        var refBox = MakeBox(def.Referer, 440);
        var timeoutBox = MakeBox(def.TimeoutSec.ToString(), 60);
        var testNameBox = MakeBox("khazan", 200);
        var encBox = new ComboBox { Width = 100, Height = 28 };
        encBox.Items.Add("utf-8"); encBox.Items.Add("gbk");
        encBox.SelectedItem = def.Encoding;
        var resultText = new TextBlock
        {
            Text = "", FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextSecondary"), Margin = new Thickness(0, 8, 0, 0),
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(MakeLabel("显示名称"));
        panel.Children.Add(nameBox);
        panel.Children.Add(MakeLabel("URL 模板（{name_urlenc} = URL 编码后的游戏名）"));
        panel.Children.Add(urlBox);
        panel.Children.Add(MakeLabel("候选列表的 JSON 路径（指向数组，如 data.results）"));
        panel.Children.Add(listBox);
        var fieldRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        fieldRow.Children.Add(new StackPanel { Children = { MakeLabelInline("AppId 字段"), idBox } });
        fieldRow.Children.Add(new StackPanel { Margin = new Thickness(12, 0, 0, 0), Children = { MakeLabelInline("名称字段"), nameFieldBox } });
        fieldRow.Children.Add(new StackPanel { Margin = new Thickness(12, 0, 0, 0), Children = { MakeLabelInline("封面字段(可空)"), imgBox } });
        panel.Children.Add(fieldRow);
        var optPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        optPanel.Children.Add(MakeLabelInline("Referer："));
        optPanel.Children.Add(refBox);
        panel.Children.Add(optPanel);
        var opt2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        opt2.Children.Add(MakeLabelInline("编码："));
        opt2.Children.Add(encBox);
        opt2.Children.Add(MakeLabelInline("超时(秒)："));
        opt2.Children.Add(timeoutBox);
        panel.Children.Add(opt2);
        panel.Children.Add(new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = (Brush)FindResource("BorderBrush"), Margin = new Thickness(0, 14, 0, 0) });
        panel.Children.Add(MakeLabel("测试（输入一个游戏名实际搜索）"));
        var testRow = new StackPanel { Orientation = Orientation.Horizontal };
        testRow.Children.Add(testNameBox);
        var testBtn = new Button { Content = "测试", Style = (Style)FindResource("BtnSm"), Margin = new Thickness(8, 0, 0, 0) };
        testRow.Children.Add(testBtn);
        panel.Children.Add(testRow);
        panel.Children.Add(resultText);
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var okBtn = new Button { Content = "确定", Style = (Style)FindResource("BtnPrimary"), Width = 80, IsDefault = true };
        var cancelBtn = new Button { Content = "取消", Style = (Style)FindResource("Btn"), Width = 80, Margin = new Thickness(10, 0, 0, 0), IsCancel = true };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);
        panel.Children.Add(btnRow);
        win.Content = panel;

        testBtn.Click += async (_, _) =>
        {
            string q = testNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(q))
            {
                resultText.Foreground = (Brush)FindResource("Red");
                resultText.Text = "请输入游戏名";
                return;
            }
            var probe = CollectFromDialog();
            testBtn.IsEnabled = false;
            resultText.Foreground = (Brush)FindResource("TextSecondary");
            resultText.Text = "搜索中…";
            var items = await AppIdSourceService.QuerySource(probe, q);
            testBtn.IsEnabled = true;
            if (items == null)
            {
                resultText.Foreground = (Brush)FindResource("Red");
                resultText.Text = "✗ 网络错误：连接失败或超时（检查 URL/网络）";
            }
            else if (items.Count == 0)
            {
                resultText.Foreground = (Brush)FindResource("Red");
                resultText.Text = "△ 请求成功但未解析到候选（检查列表路径/字段名）";
            }
            else
            {
                resultText.Foreground = (Brush)FindResource("Accent2");
                resultText.Text = "✓ 解析到 " + items.Count + " 条：" +
                    string.Join("；", items.Take(3).Select(c => $"{c.Name}(AppId {c.AppId})"));
            }
        };

        AppIdSourceDef CollectFromDialog() => new()
        {
            Id = def.Id, BuiltIn = def.BuiltIn, Enabled = def.Enabled, Priority = def.Priority,
            DisplayName = nameBox.Text.Trim(),
            UrlTemplate = urlBox.Text.Trim(),
            ListPath = listBox.Text.Trim(),
            IdField = idBox.Text.Trim(),
            NameField = nameFieldBox.Text.Trim(),
            ImageField = imgBox.Text.Trim(),
            Referer = refBox.Text.Trim(),
            Encoding = (string?)encBox.SelectedItem ?? "utf-8",
            TimeoutSec = int.TryParse(timeoutBox.Text.Trim(), out int t) ? Math.Clamp(t, 3, 30) : 10,
        };

        bool? accepted = null;
        okBtn.Click += (_, _) =>
        {
            var d = CollectFromDialog();
            if (string.IsNullOrWhiteSpace(d.DisplayName) || string.IsNullOrWhiteSpace(d.UrlTemplate) ||
                !d.UrlTemplate.Contains("{name") || string.IsNullOrWhiteSpace(d.ListPath) ||
                string.IsNullOrWhiteSpace(d.IdField) || string.IsNullOrWhiteSpace(d.NameField))
            {
                MessageBox.Show(win, "请填写名称、含 {name_urlenc} 的 URL、列表路径、AppId 字段与名称字段。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            def.DisplayName = d.DisplayName;
            def.UrlTemplate = d.UrlTemplate;
            def.ListPath = d.ListPath;
            def.IdField = d.IdField;
            def.NameField = d.NameField;
            def.ImageField = d.ImageField;
            def.Referer = d.Referer;
            def.Encoding = d.Encoding;
            def.TimeoutSec = d.TimeoutSec;
            accepted = true;
            win.DialogResult = true;
        };
        win.ShowDialog();
        return accepted;
    }

    private void UpdateBackupDirBox()
    {
        // 空 = 默认位置：显示占位提示；非空显示自定义路径
        BackupDirBox.Text = string.IsNullOrWhiteSpace(Store.Settings.BackupDir) ? "" : Store.Settings.BackupDir;
    }

    private void OnBrowseBackupDir(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择备份存放目录",
            InitialDirectory = Directory.Exists(Store.BackupRoot) ? Store.BackupRoot : Store.BaseDir,
        };
        if (dlg.ShowDialog(this) != true) return;
        BackupDirBox.Text = dlg.FolderName;
    }

    private void OnResetBackupDir(object s, RoutedEventArgs e) => BackupDirBox.Text = "";

    private void OnIncrToggled(object s, RoutedEventArgs e) => UpdateIncrRow();

    private void UpdateIncrRow()
    {
        FullEveryRow.Opacity = IncrChk.IsChecked == true ? 1 : 0.4;
        FullEveryBox.IsEnabled = IncrChk.IsChecked == true;
    }

    private void OnOpenDataDir(object s, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Store.BaseDir);
        Process.Start("explorer.exe", Store.BaseDir);
    }

    private void OnSave(object s, RoutedEventArgs e)
    {
        CloudNotice = null;
        // 本次启动设置加载不可信（读到默认值、磁盘却是富配置）：任何保存都可能覆盖用户原配置，
        // 弹明确对话框阻止，要求重启后再改（不能用易被忽略的 toast）。
        if (Store.IsUnsafeDefaultSession)
        {
            MessageBox.Show(this,
                "本次启动未能正常读取程序设置，为避免覆盖你原有的配置，本次不能保存修改。\n" +
                "请关闭设置窗口并重启程序，确认云同步、快捷键等设置正常后再修改。",
                "设置暂不可保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string err = "";
        int maxKeep = ParseInt(MaxKeepBox.Text, 1, 100, ref err);
        int fullEvery = ParseInt(FullEveryBox.Text, 1, 50, ref err);
        int hotMinutes = ParseInt(HotMinutesBox.Text, 5, 120, ref err);
        if (err.Length > 0)
        {
            ErrText.Text = err;
            ErrText.Visibility = Visibility.Visible;
            return;
        }

        Store.Settings.MaxKeepPerGame = maxKeep;
        Store.Settings.IncrementalEnabled = IncrChk.IsChecked == true;
        Store.Settings.FullEveryN = fullEvery;
        Store.Settings.ScanOnStartup = StartupScanChk.IsChecked == true;
        Store.Settings.HotSnapshotMinutes = hotMinutes;
        // 全局热键：空=禁用；非空必须能解析（录入器本身已保证，此处兜底）
        string combo = (_hotKeyCombo ?? "").Trim();
        if (combo.Length > 0)
        {
            var (mods, _, ok) = GlobalHotKeyService.Parse(combo);
            if (!ok || mods == 0)
            {
                ErrText.Text = "快捷键格式无效，须为「Ctrl/Alt/Shift/Win + 字母/数字/F1-F12」，如 Ctrl+Shift+B";
                ErrText.Visibility = Visibility.Visible;
                return;
            }
        }
        Store.Settings.BackupHotKey = combo;

        // 备份目录：空 = 默认；非空必须可创建
        string backupDir = BackupDirBox.Text.Trim();
        if (backupDir.Length > 0)
        {
            try { Directory.CreateDirectory(backupDir); }
            catch (Exception ex)
            {
                ErrText.Text = "备份目录无法创建：" + ex.Message;
                ErrText.Visibility = Visibility.Visible;
                return;
            }
        }
        Store.Settings.BackupDir = backupDir;
        // 本次改了备份目录：记录旧根，下次启动 CheckBackupMigration 会检查旧目录里
        // 仍有记录指向的备份并询问「移动认领」（新目录中已有的记录不受影响）
        if (backupDir != _origBackupDir && _origBackupDir.Length > 0)
            Store.Settings.PrevBackupDir = _origBackupDir;

        // 云同步（WebDAV）：无总开关。三态处理——齐全→立即启用；半成品→不启用但已填内容照存，
        // 下次打开回显继续补；全空→关闭并清掉旧密码密文。云表单状态绝不阻塞其他设置项保存。
        string cloudUrl = CloudUrlBox.Text.Trim();
        string cloudUser = CloudUserBox.Text.Trim();
        string cloudDir = CloudDirBox.Text.Trim().Trim('/');
        if (cloudDir.Length == 0) cloudDir = "GameSaveVault";
        string typedPass = CloudPassBox.Password;
        bool hasSecret = typedPass.Length > 0 || CloudSecret.Unprotect(_cloudSecretStored).Length > 0;
        var cloudState = CloudSyncService.EvaluateCloudForm(cloudUrl, cloudUser, hasSecret);
        Store.Settings.CloudEnabled = cloudState == CloudSyncService.CloudFormState.Complete;
        Store.Settings.CloudUrl = cloudUrl;
        Store.Settings.CloudUser = cloudUser;
        Store.Settings.CloudDir = cloudDir;
        Store.Settings.CloudDelWithCloud = CloudDelWithCloudChk.IsChecked == true;
        var dname = DefaultPlayerNameBox.Text.Trim();
        if (dname.Length > 0) Store.Settings.DefaultPlayerName = dname;
        if (typedPass.Length > 0) Store.Settings.CloudSecret = CloudSecret.Protect(typedPass);
        if (cloudState == CloudSyncService.CloudFormState.Empty) Store.Settings.CloudSecret = "";
        if (cloudState == CloudSyncService.CloudFormState.Partial)
            CloudNotice = "云同步信息还没填全，本次未启用（其他设置已保存）；补全服务器地址、账号和应用密码后即生效";

        // 数据源勾选状态随「保存」持久化（增删改已即时保存）
        GameNameService.SaveSources(_sources);
        AppIdSourceService.SaveSources(_appIdSources);
        Store.SaveSettings();
        DialogResult = true;
    }

    private static int ParseInt(string text, int min, int max, ref string err)
    {
        if (!int.TryParse(text.Trim(), out int v) || v < min || v > max)
        {
            err = $"请输入 {min}–{max} 之间的整数";
            return min;   // 出错时返回合法值（err 会拦截保存）
        }
        return v;
    }
}
