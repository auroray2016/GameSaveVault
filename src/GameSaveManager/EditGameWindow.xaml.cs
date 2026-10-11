using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using GameSaveManager.Models;
using GameSaveManager.Services;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace GameSaveManager;

/// <summary>
/// 编辑游戏信息：中英文名、AppId、游戏 exe、封面图（本地图片或按 AppId 从 Steam CDN 下载）、图标（本地图片或 exe 提取）。
/// 仅在点「保存」时写回 GameEntry；自定义图片复制到 cache 目录（custom_{gameId} 前缀），避免引用易失效的原始路径。
/// </summary>
public partial class EditGameWindow : Window
{
    private readonly GameEntry _g;
    private string? _pendingCover;   // 保存时写入 CoverFile
    private string? _pendingIcon;    // 保存时写入 IconFile

    public EditGameWindow(GameEntry g)
    {
        InitializeComponent();
        DarkTitleBar.SetTheme(this, ThemeManager.IsDark);
        _g = g;
        NameCnBox.Text = g.NameCN;
        NameEnBox.Text = g.NameEN;
        AppIdBox.Text = g.AppId > 0 ? g.AppId.ToString() : "";
        ExeText.Text = string.IsNullOrWhiteSpace(g.ExePath) ? "（未设置）" : g.ExePath;
        ShowPreview(CoverPreview, CoverEmpty, g.CoverFile);
        ShowPreview(IconPreview, IconEmpty, g.IconFile);
    }

    private static void ShowPreview(System.Windows.Controls.Image img,
        System.Windows.Controls.TextBlock empty, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                // 走文件流而非 UriSource：路径含 # 等 URI 保留字符时 Uri 会截断路径导致加载失败
                using var fs = File.OpenRead(path);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // 解码完即释放文件句柄
                bmp.StreamSource = fs;
                bmp.EndInit();
                img.Source = bmp;
                empty.Visibility = Visibility.Collapsed;
                return;
            }
            catch { /* 格式不支持时显示占位 */ }
        }
        img.Source = null;
        empty.Visibility = Visibility.Visible;
    }

    /// <summary>推测游戏目录：当前填写的 exe → 已存 exe → 安装目录，作为浏览对话框初始位置。</summary>
    private string? GuessGameDir()
    {
        foreach (var p in new[] { ExeText.Text, _g.ExePath, _g.InstallDir })
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try
            {
                string full = Path.GetFullPath(p);   // 占位文本等非法路径会抛异常，跳过
                if (File.Exists(full)) return Path.GetDirectoryName(full);
                if (Directory.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    private void OnBrowseExe(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择游戏 exe",
            Filter = "程序|*.exe|所有文件|*.*",
            InitialDirectory = GuessGameDir(),
        };
        if (dlg.ShowDialog(this) != true) return;
        ExeText.Text = dlg.FileName;
    }

    private async void OnDownloadCover(object s, RoutedEventArgs e)
    {
        if (!int.TryParse(AppIdBox.Text.Trim(), out int appId) || appId <= 0)
        {
            ErrText.Text = "请先填写有效的 Steam AppId 再下载封面";
            ErrText.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            string cover = Store.CoverFileFor(appId);
            bool ok = await SteamApi.DownloadCover(appId, cover);
            if (!ok)
            {
                ErrText.Text = "封面下载失败（检查网络或 AppId 是否正确）";
                ErrText.Visibility = Visibility.Visible;
                return;
            }
            _pendingCover = cover;
            ShowPreview(CoverPreview, CoverEmpty, cover);
            ErrText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ErrText.Text = "下载失败：" + ex.Message;
            ErrText.Visibility = Visibility.Visible;
        }
    }

    private void OnBrowseCover(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择封面图片",
            Filter = "图片|*.jpg;*.jpeg;*.png;*.webp;*.bmp",
            InitialDirectory = GuessGameDir(),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            // 有 AppId 时优先与官方下载封面同款命名（{appId}.jpg）；被另一条目共用或学习版才用 custom_{gameId} 兜底
            int coverAppId = CurrentAppId();
            _pendingCover = coverAppId > 0
                ? SavePickedCover(dlg.FileName, coverAppId)
                : CopyToCache(dlg.FileName, Store.CoversDir, "custom_" + _g.Id);
            ShowPreview(CoverPreview, CoverEmpty, _pendingCover);
            ErrText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ErrText.Text = "复制图片失败：" + ex.Message;
            ErrText.Visibility = Visibility.Visible;
        }
    }

    private void OnBrowseIcon(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择图标图片或游戏 exe",
            Filter = "图片/程序|*.png;*.jpg;*.jpeg;*.ico;*.bmp;*.exe",
            InitialDirectory = GuessGameDir(),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (dlg.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                string dest = Path.Combine(Store.IconsDir, "custom_" + _g.Id + ".png");
                string? png = IconExtractor.ExtractToPng(dlg.FileName, dest);
                if (png == null)
                {
                    ErrText.Text = "无法从该 exe 提取图标";
                    ErrText.Visibility = Visibility.Visible;
                    return;
                }
                _pendingIcon = png;
            }
            else
            {
                _pendingIcon = CopyToCache(dlg.FileName, Store.IconsDir, "custom_" + _g.Id);
            }
            ShowPreview(IconPreview, IconEmpty, _pendingIcon);
            ErrText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ErrText.Text = "处理图标失败：" + ex.Message;
            ErrText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>复制到 cache 目录并清理同前缀旧文件（前缀不带扩展名）。</summary>
    private static string CopyToCache(string src, string dir, string prefix)
    {
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir, prefix + ".*"))
            File.Delete(old);
        string dest = Path.Combine(dir, prefix + Path.GetExtension(src).ToLowerInvariant());
        File.Copy(src, dest, overwrite: true);
        return dest;
    }

    /// <summary>把本地图片以「官方下载封面」同款命名落盘：cache\covers\{appId}.jpg。
    /// jpg/jpeg 直接复制（无损）；png/webp/bmp 等经 WIC 转码为 JPEG（质量 90），保证文件与命名都是真 jpg。
    /// 调用前须确认 {appId}.jpg 没被另一条目引用（双装共用），否则会覆盖另一条目的封面。</summary>
    private static string SaveCoverCanonical(string src, int appId)
    {
        string dest = Store.CoverFileFor(appId);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        string ext = Path.GetExtension(src).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg")
        {
            File.Copy(src, dest, overwrite: true);
            return dest;
        }
        DataMigration.EncodeJpeg(src, dest);   // 文件流解码，兼容含 # 的路径
        return dest;
    }

    /// <summary>选手动封面的落盘目标选择：该 AppId 的官方缓存位没被别的条目占用 → {appId}.jpg（与官方同款命名，
    /// 重删再添可零网络认领）；已被另一条目引用（双装/同 AppId 两条目）→ 退回 custom_{gameId}.* 私有位，
    /// 换图绝不覆盖另一条目的封面。</summary>
    private string SavePickedCover(string src, int appId)
    {
        string canonical = Store.CoverFileFor(appId);
        return Store.IsImageReferencedByOthers(canonical, _g.Id)
            ? CopyToCache(src, Store.CoversDir, "custom_" + _g.Id)
            : SaveCoverCanonical(src, appId);
    }

    /// <summary>AppId 输入框当前值；无效/为空返回 0。</summary>
    private int CurrentAppId() =>
        int.TryParse(AppIdBox.Text.Trim(), out int a) && a > 0 ? a : 0;

    private async void OnSearchAppId(object s, RoutedEventArgs e)
    {
        string name = NameEnBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrText.Text = "请先输入英文名称再查询";
            ErrText.Visibility = Visibility.Visible;
            return;
        }

        ErrText.Text = "正在查询 AppId 数据源…";
            ErrText.Visibility = Visibility.Visible;
            BtnSave.IsEnabled = false;

        try
        {
            // 通道1：steamtools.games（国内可达，直接返回 appid+名称+封面 URL）
            var stResults = await AppIdSourceService.SearchAsync(name);

            // 也用安装目录名做候选
            string dir = _g.InstallDir ?? "";
            string dirName = System.IO.Path.GetFileName(dir.TrimEnd('\\'));
            if (!string.IsNullOrWhiteSpace(dirName) && !dirName.Equals(name, StringComparison.OrdinalIgnoreCase))
                stResults.AddRange(await AppIdSourceService.SearchAsync(dirName));

            // 去重（按 AppId）
            stResults = stResults
                .GroupBy(r => r.AppId)
                .Select(g => g.First())
                .ToList();

            if (stResults.Count == 0)
            {
                // 通道2：PCGamingWiki（可拿到存档路径但可能无 appid）
                ErrText.Text = "游戏数据源无结果，正在查 PCGamingWiki…";
                var wikiResults = await WikiService.SearchAsync(name);
                if (!string.IsNullOrWhiteSpace(dirName) && !dirName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    wikiResults.AddRange(await WikiService.SearchAsync(dirName));
                wikiResults = wikiResults
                    .GroupBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                if (wikiResults.Count == 0)
                {
                    ErrText.Text = "未查到结果，请手动填写 AppId";
                    return;
                }
                ShowPickWindow(wikiResults.Select(r => new PickChoice(
                    r.Title, AppIdSourceService.CleanEnglishName(r.Title), r.AppId, "game", false)).ToList(),
                    "PCGamingWiki");
                return;
            }

            if (stResults.Count == 1)
            {
                var r = stResults[0];
                AppIdBox.Text = r.AppId.ToString();
                // 自动下载封面
                string cover = Store.CoverFileFor(r.AppId);
                if (await SteamApi.DownloadCover(r.AppId, cover))
                {
                    _pendingCover = cover;
                    ShowPreview(CoverPreview, CoverEmpty, cover);
                }
                // 用净化后的英文名替换（接口名可能带「（中文名）」后缀，不能混进英文名框）
                string enClean = AppIdSourceService.CleanEnglishName(r.Name);
                if (!string.IsNullOrWhiteSpace(enClean))
                    NameEnBox.Text = enClean;
                ErrText.Text = (r.Approximate ? "⚠ 近似匹配，请核对（" : "已匹配（") +
                    $"{r.Source}）：{r.Name}（AppId {r.AppId}）";
                ErrText.Visibility = Visibility.Visible;
                return;
            }

            // 多条结果：分类打徽章（主游戏/季票/DLC/试玩），主游戏排前；近似命中加 ≈
            var allNames = stResults.Select(r => r.Name).ToList();
            var choices = stResults.Select(r => new PickChoice(
                r.Name, AppIdSourceService.CleanEnglishName(r.Name), r.AppId,
                AppIdSourceService.ClassifyResultKind(r.Name, allNames), r.Approximate)).ToList();
            choices = choices.OrderBy(c => KindRank(c.Kind)).ToList();
            ShowPickWindow(choices,
                stResults[0].Source + (stResults.Any(r => r.Approximate) ? "（近似匹配，请核对）" : ""));
        }
        catch (Exception ex)
        {
            ErrText.Text = "查询失败：" + ex.Message;
            ErrText.Visibility = Visibility.Visible;
        }
        finally
        {
            BtnSave.IsEnabled = true;
        }
    }

    /// <summary>选择窗条目：Display 完整显示名；EnName 净化后英文名（空则不回填）；Kind 类型徽章键。</summary>
    private record PickChoice(string Display, string EnName, int AppId, string Kind, bool Approx);

    /// <summary>类型排序权重：主游戏在前，试玩在末。</summary>
    private static int KindRank(string kind) => kind switch
    {
        "game" => 0, "season" => 1, "dlc" => 2, "bundle" => 3, "demo" => 4, _ => 5,
    };

    /// <summary>类型徽章：(文字, 底色RGB, 浅字=true白字/false深字)。</summary>
    private static (string Text, byte R, byte G, byte B, bool Light)? BadgeOf(string kind) => kind switch
    {
        "season" => ("季票", 0x8B, 0x5C, 0xF6, true),
        "dlc" => ("DLC", 0xFB, 0xBF, 0x24, false),
        "bundle" => ("捆绑包", 0xFB, 0xBF, 0x24, false),
        "demo" => ("试玩", 0xF8, 0x71, 0x71, false),
        _ => null,
    };

    /// <summary>弹选择窗口让用户从多条结果中选一条（深色标题栏/深色按钮、类型徽章、列表可滚动）。</summary>
    private void ShowPickWindow(List<PickChoice> items, string source)
    {
        var pickWin = new Window
        {
            Title = "选择匹配结果",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = FindResource("Panel") as System.Windows.Media.Brush,
            Foreground = FindResource("TextPrimary") as System.Windows.Media.Brush,
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            UseLayoutRounding = true,
        };
        DarkTitleBar.SetTheme(pickWin, ThemeManager.IsDark);

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var prompt = new TextBlock
        {
            Text = $"{source} 查到 {items.Count} 条结果，请选择：",
            FontSize = 13, TextWrapping = TextWrapping.Wrap,
            Foreground = FindResource("TextSecondary") as System.Windows.Media.Brush,
            Margin = new Thickness(0, 0, 0, 10),
        };
        Grid.SetRow(prompt, 0);
        root.Children.Add(prompt);

        var listHost = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = listHost, MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var status = new TextBlock
        {
            FontSize = 12,
            Foreground = FindResource("TextSecondary") as System.Windows.Media.Brush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var cancel = new System.Windows.Controls.Button
        {
            Content = "取消", Width = 84, Style = FindResource("Btn") as Style,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, IsCancel = true,
        };
        var bottom = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(status, 0);
        Grid.SetColumn(cancel, 1);
        bottom.Children.Add(status);
        bottom.Children.Add(cancel);
        Grid.SetRow(bottom, 2);
        root.Children.Add(bottom);

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int col = 0;
            if (BadgeOf(item.Kind) is { } bi)
            {
                var badge = new Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(bi.R, bi.G, bi.B)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 1, 6, 2),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = bi.Text, FontSize = 10.5, FontWeight = FontWeights.Bold,
                        Foreground = new System.Windows.Media.SolidColorBrush(
                            bi.Light ? System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF)
                                     : System.Windows.Media.Color.FromRgb(0x1A, 0x1D, 0x25)),
                    },
                };
                Grid.SetColumn(badge, 0);
                content.Children.Add(badge);
                col = 1;
            }
            var nameText = new TextBlock
            {
                Text = (item.Approx ? "≈ " : "") + $"{item.Display}（AppId {item.AppId}）",
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(nameText, col);
            content.Children.Add(nameText);

            var btn = new System.Windows.Controls.Button
            {
                Content = content, Tag = i, Style = FindResource("Btn") as Style,
                Margin = new Thickness(0, 3, 0, 3),
                Padding = new Thickness(10, 7, 10, 7),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            };
            btn.Click += async (_s, _e) =>
            {
                int idx = (int)((System.Windows.Controls.Button)_s).Tag;
                var choice = items[idx];
                listHost.IsEnabled = false;
                cancel.IsEnabled = false;
                status.Text = "正在下载封面…";
                AppIdBox.Text = choice.AppId.ToString();
                if (!string.IsNullOrWhiteSpace(choice.EnName))
                    NameEnBox.Text = choice.EnName;
                ErrText.Text = $"已选择：{choice.Display}（AppId {choice.AppId}）";
                ErrText.Visibility = Visibility.Visible;
                // 自动下载封面
                string cover = Store.CoverFileFor(choice.AppId);
                if (await SteamApi.DownloadCover(choice.AppId, cover))
                {
                    _pendingCover = cover;
                    ShowPreview(CoverPreview, CoverEmpty, cover);
                }
                // 等待期间窗口可能已被用户取消关闭，此时不能再设置 DialogResult
                if (!pickWin.IsLoaded) return;
                pickWin.DialogResult = true;
                pickWin.Close();
            };
            listHost.Children.Add(btn);
        }

        pickWin.Content = root;
        // 结果窗已弹出：陈旧的「正在查询…」状态提前收起（旧实现要等关窗后才收）
        ErrText.Visibility = Visibility.Collapsed;
        pickWin.ShowDialog();
    }

    /// <summary>按钮触发：按当前数据源（默认自动）获取正式中文名。</summary>
    private async void OnSyncChineseName(object s, RoutedEventArgs e)
    {
        if (!int.TryParse(AppIdBox.Text.Trim(), out int appId) || appId <= 0)
        {
            ErrText.Text = "请先填写有效的 Steam AppId";
            ErrText.Visibility = Visibility.Visible;
            return;
        }
        var source = (CnNameSource)Store.Settings.CnNameSource;
        await DoSyncChineseName(appId, source);
    }

    /// <summary>实际执行同步（指定数据源）。</summary>
    private async Task DoSyncChineseName(int appId, CnNameSource source)
    {
        BtnSyncCn.IsEnabled = false;
        BtnSyncSrc.IsEnabled = false;
        BtnSyncCn.Content = "获取中…";
        var srcLabel = source switch { CnNameSource.Xiaoheihe => "小黑盒", CnNameSource.Steam => "Steam", _ => "自动" };
        ErrText.Text = $"正在获取中文名（{srcLabel}）…";
        ErrText.Visibility = Visibility.Visible;
        try
        {
            var r = await GameNameService.FetchAsync(appId, source);
            if (!string.IsNullOrWhiteSpace(r.Name))
            {
                NameCnBox.Text = r.Name;
                ErrText.Text = $"中文名已同步（来源：{r.Source}）：{r.Name}";
            }
            else if (r.State == SteamApi.NameSyncState.RateLimited)
            {
                ErrText.Text = "触发 Steam 限流保护，请等待约 1 分钟后再试";
            }
            else if (r.State == SteamApi.NameSyncState.NoLocalizedName)
            {
                ErrText.Text = "Steam 未提供该游戏的官方中文名（发行商未本地化），可在中文名输入框手动填写后保存";
            }
            else if (r.State == SteamApi.NameSyncState.NoData)
            {
                ErrText.Text = $"各数据源均无 AppId（{appId}）的中文名：可能 AppId 有误或游戏太冷门，可手动填写中文名";
            }
            else
            {
                ErrText.Text = "所有数据源均连接失败：小黑盒连不上请检查网络，Steam 源需加速器覆盖本程序流量";
            }
        }
        catch (Exception ex)
        {
            ErrText.Text = "获取失败：" + ex.Message;
        }
        finally
        {
            ErrText.Visibility = Visibility.Visible;
            BtnSyncCn.Content = "同步中文名";
            BtnSyncCn.IsEnabled = true;
            BtnSyncSrc.IsEnabled = true;
        }
    }

    /// <summary>数据源选择菜单：自动 / 小黑盒（国内直连）/ Steam（需加速器）。</summary>
    private void OnSyncSourceMenu(object s, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        int cur = Store.Settings.CnNameSource;
        foreach (var (label, val, tip) in new (string, int, string)[]
        {
            ("自动（推荐）", 0, "本地库 → 小黑盒 → Steam 依次尝试"),
            ("小黑盒（国内直连）", 1, "无需加速器，卡赞等锁区游戏也能取中文名"),
            ("Steam 商店", 2, "官方名，需加速器覆盖本程序流量"),
        })
        {
            var mi = new MenuItem { Header = (cur == val ? "✓ " : "   ") + label, ToolTip = tip };
            mi.Click += async (_, _) =>
            {
                Store.Settings.CnNameSource = val;
                Store.SaveSettings();
                if (int.TryParse(AppIdBox.Text.Trim(), out int appId) && appId > 0)
                    await DoSyncChineseName(appId, (CnNameSource)val);
            };
            menu.Items.Add(mi);
        }
        menu.PlacementTarget = BtnSyncSrc;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnSave(object s, RoutedEventArgs e)
    {
        string appidText = AppIdBox.Text.Trim();
        int appId = 0;
        if (appidText.Length > 0 && (!int.TryParse(appidText, out appId) || appId < 0))
        {
            ErrText.Text = "AppId 必须是数字（0 表示未匹配）";
            ErrText.Visibility = Visibility.Visible;
            return;
        }

        // 中英文名不能同时为空：否则备份目录退化为共享名 "game"，
        // 多款无名游戏会共用一个备份目录，移除其中一款时会删掉其他游戏的全部备份。
        if (string.IsNullOrWhiteSpace(NameCnBox.Text) && string.IsNullOrWhiteSpace(NameEnBox.Text))
        {
            ErrText.Text = "中文名和英文名至少填写一个";
            ErrText.Visibility = Visibility.Visible;
            return;
        }

        string exe = ExeText.Text.Trim();
        if (exe == "（未设置）") exe = "";
        if (exe.Length > 0 && !File.Exists(exe))
        {
            ErrText.Text = "游戏 exe 文件不存在";
            ErrText.Visibility = Visibility.Visible;
            return;
        }

        string oldDisplay = _g.DisplayName;   // 改名前记录（备份目录按游戏名命名，需迁移）
        _g.NameCN = NameCnBox.Text.Trim();
        _g.NameEN = NameEnBox.Text.Trim();
        _g.AppId = appId;
        if (exe.Length > 0)
        {
            _g.ExePath = exe;
            _g.InstallDir = Path.GetDirectoryName(exe) ?? "";
        }
        if (_pendingCover != null)
        {
            // 落盘前按最终 AppId 规整封面命名：
            //  ① 选图时无 AppId（存成 custom_*），保存时补上了 → 转为 {appId}.jpg；
            //  ② 选图后改过 AppId（已存成旧 {oldId}.jpg）→ 挪到新 {appId}.jpg，删旧文件。
            // 新 AppId 缓存位已被另一条目占用（双装）→ 退回 custom_{gameId} 私有位，不覆盖共用图；
            // 旧文件若仍被别的条目引用则保留，只解绑本条目。
            string pendingName = Path.GetFileName(_pendingCover);
            string canonical = appId > 0 ? Store.CoverFileFor(appId) : "";
            bool needFix = appId > 0 && File.Exists(_pendingCover)
                && !string.Equals(_pendingCover, canonical, StringComparison.OrdinalIgnoreCase)
                && (pendingName.StartsWith("custom_", StringComparison.OrdinalIgnoreCase)
                    || pendingName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
            if (needFix)
            {
                try
                {
                    bool newSlotShared = File.Exists(canonical)
                        && Store.IsImageReferencedByOthers(canonical, _g.Id);
                    string moved;
                    if (newSlotShared)
                        // pending 已是按本条目命名的 custom_ 私有图 → 原地保留（CopyToCache 会先删同前缀文件，自复制必抛）
                        moved = pendingName.StartsWith("custom_", StringComparison.OrdinalIgnoreCase)
                            ? _pendingCover
                            : CopyToCache(_pendingCover, Store.CoversDir, "custom_" + _g.Id);
                    else
                        moved = SaveCoverCanonical(_pendingCover, appId);
                    if (!string.Equals(moved, _pendingCover, StringComparison.OrdinalIgnoreCase)
                        && !Store.IsImageReferencedByOthers(_pendingCover, _g.Id))
                        File.Delete(_pendingCover);
                    _pendingCover = moved;
                }
                catch { /* 规整失败保留原 pending，不阻塞保存 */ }
            }
            _g.CoverFile = _pendingCover;
        }
        if (_pendingIcon != null) _g.IconFile = _pendingIcon;
        _g.Refresh();
        // 手动填写的中文名也写入本地库（含 CJK 才入库，避免英文占位），今后扫描/重装直接复用
        if (_g.AppId > 0 && !string.IsNullOrWhiteSpace(_g.NameCN) && _g.NameCN.Any(c => c >= 0x4E00 && c <= 0x9FFF))
            GameNameService.Remember(_g.AppId, _g.NameCN, "手动");
        if (BackupService.SafeName(oldDisplay) != BackupService.SafeName(_g.DisplayName))
            BackupService.MigrateBackupDir(_g, oldDisplay);
        // 改名后目录名可能正好对上移除前保留的备份：找回旧备份与玩家档案
        try
        {
            var (reBak, reSlot) = ProfileService.ReclaimAll(_g);
            if (reBak + reSlot > 0) { _g.RefreshProfiles(); _g.RefreshVisibleBackups(); _g.Refresh(); }
        }
        catch { }
        DialogResult = true;
    }
}
