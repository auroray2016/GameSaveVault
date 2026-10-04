using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GameSaveManager.Models;
using GameSaveManager.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace GameSaveManager;

/// <summary>云端备份浏览窗：枚举 WebDAV上的 zip，按游戏筛选，下载导入本机游戏库或删除云端副本。</summary>
public partial class CloudBrowserWindow : Window
{
    private const string AllGames = "全部游戏";

    private readonly WebDavClient _client;
    private bool _busy;
    private bool _loading;

    private List<CloudRemoteFile> _all = new();
    private ListCollectionView? _view;

    public CloudBrowserWindow(WebDavClient client)
    {
        _client = client;
        InitializeComponent();
        DarkTitleBar.SetTheme(this, ThemeManager.IsDark);
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _busy = true;
        _loading = true;
        RefreshBtn.IsEnabled = DownloadBtn.IsEnabled = DeleteBtn.IsEnabled = MapBtn.IsEnabled = false;
        ShowIndeterminate("正在读取云端文件列表…");
        try
        {
            // 读取云端文件时同时把索引身份段并入缓存（AppId/别名认领更准）
            try { CloudSyncService.UpdateIdentityCache(await CloudSyncService.GetIndexAsync(_client)); } catch { }
            string lastDir = "";
            var progress = new Progress<string>(d =>
            {
                if (d != lastDir) { lastDir = d; StatusText.Text = "正在读取：" + Shorten(d); }
            });
            var files = await _client.ListAllAsync(progress);
            // 云端索引关联存档时间（1 次小 GET，读不到按无索引处理）；排序按存档时间优先
            try { CloudIndex.Link(files, await CloudSyncService.GetIndexAsync(_client)); } catch { }
            files.Sort((a, b) => b.SortTimeUtc.CompareTo(a.SortTimeUtc));
            // 备注回填：按 zip 文件名找本机备份，上传前本地有备注就直接显示该备注（无则留空）
            var localNotes = Store.Games
                .SelectMany(g => g.Backups)
                .GroupBy(b => Path.GetFileName(b.File), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(gr => gr.Key,
                    gr => gr.Select(b => b.Note).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "",
                    StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
                if (localNotes.TryGetValue(f.Name, out var note)) f.NoteText = note;
            _all = files;

            // 游戏筛选下拉：全部游戏 + 云端出现过的游戏段（去重排序）
            string? keep = (GameFilter.SelectedItem as string);
            GameFilter.Items.Clear();
            GameFilter.Items.Add(AllGames);
            foreach (var gm in files.Select(f => f.Game).Distinct()
                         .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                GameFilter.Items.Add(gm);
            GameFilter.SelectedItem = keep != null && GameFilter.Items.Contains(keep) ? keep : AllGames;

            _view = new ListCollectionView(_all) { Filter = FilterItem };
            FileList.ItemsSource = _view;
            UpdateCountText();
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取失败：" + ex.Message;
        }
        finally
        {
            _loading = false;
            _busy = false;
            BusyBar.Visibility = Visibility.Collapsed;
            RefreshBtn.IsEnabled = DownloadBtn.IsEnabled = DeleteBtn.IsEnabled = true;
            MapBtn.IsEnabled = true;   // 「未匹配」不依赖行选择，读取完成即可点
        }
    }

    private bool FilterItem(object obj)
    {
        if (GameFilter.SelectedItem is not string sel || sel == AllGames) return true;
        return obj is CloudRemoteFile f && f.Game == sel;
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _view?.Refresh();
        UpdateCountText();
    }

    /// <summary>状态栏数量文案：筛选时显示「可见/总数」。</summary>
    private void UpdateCountText()
    {
        if (_all.Count == 0)
        {
            StatusText.Text = "云端还没有备份（开启自动上传或点「立即上传全部」）";
            return;
        }
        int shown = _view?.Count ?? _all.Count;
        StatusText.Text = shown == _all.Count
            ? $"共 {_all.Count} 份云端备份"
            : $"显示 {shown} 份 / 共 {_all.Count} 份";
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (!_busy) await LoadAsync();
    }

    private CloudRemoteFile? Selected => FileList.SelectedItem as CloudRemoteFile;

    /// <summary>「未匹配」：自动对比本地与云端——列出云端存在但没有任何本机游戏认领的孤儿目录，
    /// 用户逐个指派归属；指派后立即收集该目录，把数据收回来。</summary>
    private async void OnShowUnmatched(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        RefreshBtn.IsEnabled = DownloadBtn.IsEnabled = DeleteBtn.IsEnabled = MapBtn.IsEnabled = false;
        ShowIndeterminate("正在对比本地与云端存档…");
        try
        {
            string lastDir = "";
            var progress = new Progress<string>(d =>
            {
                if (d != lastDir) { lastDir = d; StatusText.Text = "正在读取：" + Shorten(d); }
            });
            var files = await _client.ListAllAsync(progress);
            // 最新索引并入缓存（AppId/别名），保证认领判定是最新状态
            try { CloudSyncService.UpdateIdentityCache(await CloudSyncService.GetIndexAsync(_client)); } catch { }

            // 云端目录段（含备份份数）→ 过滤出无任何本机游戏认领的孤儿
            var groups = files.GroupBy(f => f.Game, StringComparer.OrdinalIgnoreCase)
                .Select(gr => (dir: gr.Key, count: gr.Count()))
                .OrderBy(x => x.dir, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var orphans = CloudSyncService.FindUnclaimedDirs(groups.Select(x => x.dir))
                .Select(d => (dir: d, count: groups.First(x => x.dir == d).count))
                .ToList();

            if (orphans.Count == 0)
            {
                StatusText.Text = "✓ 云端所有存档目录都已匹配到本机游戏，没有未匹配项";
                return;
            }

            // 弹窗是模态等待：先收起进度条并明确提示，状态栏不能停留在枚举目录进度
            BusyBar.Visibility = Visibility.Collapsed;
            StatusText.Text = $"请在弹窗中指派 {orphans.Count} 个未匹配目录的归属…";
            // 弹出孤儿清单让用户指派；返回最终完成的 (云端目录, 本机游戏)
            var matched = ShowOrphanDialog(orphans);
            foreach (var (dir, game) in matched)
            {
                ShowIndeterminate($"正在收集「{dir}」→「{game.DisplayName}」…");
                try
                {
                    await CloudSyncService.CollectGameAsync(game, _client);
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"「{dir}」已归属，但收集失败（稍后自动重试）：{ex.Message}";
                }
            }
            BusyBar.Visibility = Visibility.Collapsed;
            StatusText.Text = matched.Count > 0
                ? $"✓ 匹配完成：{matched.Count} 个云端目录已归属并收集"
                : "未指派任何归属";
        }
        catch (Exception ex)
        {
            StatusText.Text = "对比失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            BusyBar.Visibility = Visibility.Collapsed;
            RefreshBtn.IsEnabled = DownloadBtn.IsEnabled = DeleteBtn.IsEnabled = MapBtn.IsEnabled = true;
        }
    }

    /// <summary>孤儿云端目录匹配窗（代码构建，复用本窗深色样式）：每行一个目录 + 备份份数 +
    /// 本机游戏下拉 + 「归属」按钮。归属立即落盘映射，允许改归属或撤销；完成后返回最终归属 (目录, 游戏)。</summary>
    private List<(string dir, GameEntry game)> ShowOrphanDialog(
        List<(string dir, int count)> orphans)
    {
        const string placeholder = "请选择游戏";
        var gameNames = Store.Games.Select(g => g.DisplayName)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        var rowsHost = new StackPanel();
        var done = new List<(string dir, GameEntry game)>();
        foreach (var (dir, count) in orphans)
        {
            // 本行当前已归属的游戏（null=尚未归属）；支持改归属与撤销反悔
            GameEntry? assigned = null;

            var combo = new System.Windows.Controls.ComboBox
            {
                Width = 210, Height = 30, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                Style = (Style)FindResource("Combo"),
            };
            combo.Items.Add(placeholder);
            foreach (var n in gameNames) combo.Items.Add(n);
            combo.SelectedIndex = 0;

            var assignBtn = new System.Windows.Controls.Button
            {
                Content = "归属", Width = 60, Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("BtnPrimary"),
            };
            // 撤销链接放在名称列右侧（而不是新增按钮列），保证「归属」按钮始终与「完成」右缘对齐
            var undoLink = new TextBlock
            {
                Text = "撤销", FontSize = 12, Cursor = System.Windows.Input.Cursors.Hand,
                Foreground = (System.Windows.Media.Brush)FindResource("Accent"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            var label = new TextBlock
            {
                Text = $"{dir}（{count} 份备份）",
                ToolTip = dir, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            // 名称列内部：目录名（*） + 撤销链接（Auto）
            var nameCell = new Grid();
            nameCell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            nameCell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(label, 0); Grid.SetColumn(undoLink, 1);
            nameCell.Children.Add(label); nameCell.Children.Add(undoLink);

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(nameCell, 0); Grid.SetColumn(combo, 1); Grid.SetColumn(assignBtn, 2);
            grid.Children.Add(nameCell); grid.Children.Add(combo); grid.Children.Add(assignBtn);
            rowsHost.Children.Add(grid);

            assignBtn.Click += (_, _) =>
            {
                if (combo.SelectedItem is not string name || name == placeholder)
                {
                    MessageBox.Show(this, "请先选择一个本机游戏", "归属", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var game = Store.Games.FirstOrDefault(g => g.DisplayName == name);
                if (game == null) return;
                if (assigned != null && assigned.DisplayName == game.DisplayName) return;  // 已归属同一游戏
                if (assigned != null) done.RemoveAll(x => x.dir == dir);                  // 改归属：先撤旧记录
                CloudSyncService.SetMapping(dir, BackupService.SafeName(game.DisplayName));
                assigned = game;
                done.Add((dir, game));
                assignBtn.Content = "改归属";
                undoLink.Visibility = Visibility.Visible;
                label.Text = $"{dir}（{count} 份备份）→ {game.DisplayName}";
            };

            undoLink.MouseLeftButtonUp += (_, _) =>
            {
                CloudSyncService.SetMapping(dir, null);
                done.RemoveAll(x => x.dir == dir);
                assigned = null;
                combo.SelectedIndex = 0;
                assignBtn.Content = "归属";
                undoLink.Visibility = Visibility.Collapsed;
                label.Text = $"{dir}（{count} 份备份）";
            };
        }

        var info = new TextBlock
        {
            Text = $"发现 {orphans.Count} 个云端存档目录没能自动匹配到本机游戏。\n" +
                   "选择它实际属于哪个游戏，点「归属」（映射立即生效，可改归属或撤销；关闭后自动收集）：",
            Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary"),
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
        };
        var closeBtn = new System.Windows.Controls.Button
        {
            Content = "完成", Width = 90, HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            IsDefault = true, IsCancel = true, Style = (Style)FindResource("BtnPrimary"),
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(info);
        panel.Children.Add(rowsHost);
        panel.Children.Add(closeBtn);
        var dlg = new Window
        {
            Title = $"未匹配的云端存档（{orphans.Count}）", Owner = this,
            Width = 620, SizeToContent = SizeToContent.Height, MaxHeight = 620,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("Panel"),
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            UseLayoutRounding = true,
        };
        DarkTitleBar.SetTheme(dlg, ThemeManager.IsDark);
        closeBtn.Click += (_, _) => dlg.DialogResult = true;
        dlg.Content = panel;
        dlg.ShowDialog();
        return done;
    }

    /// <summary>云端目录段 → 本机候选游戏：① 手动映射 ② 三名字直配 ③ 认领链（AppId/别名）。
    /// 返回多条＝本机有同名/同身份的重复条目（典型：同一款游戏的 Steam 版与原版并存），归属有歧义，
    /// 必须由用户确认——静默取第一条会把备份导进另一个游戏。</summary>
    internal static List<GameEntry> FindOwnerCandidates(string cloudSeg)
    {
        var mapped = CloudSyncService.GetMapping(cloudSeg);
        if (mapped != null)
        {
            var byMap = Store.Games.Where(g =>
                string.Equals(BackupService.SafeName(g.DisplayName), mapped, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byMap.Count > 0) return byMap;
        }
        var byName = Store.Games.Where(g =>
            SafeEquals(cloudSeg, g.DisplayName) || SafeEquals(cloudSeg, g.NameCN) || SafeEquals(cloudSeg, g.NameEN)).ToList();
        if (byName.Count > 0) return byName;
        return Store.Games.Where(g =>
            CloudSyncService.CloudDirsFor(g).Contains(cloudSeg, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>候选里已经持有这份云端文件（存在同名备份记录）的那一个；不唯一（0 或 ≥2）返回 null。</summary>
    internal static GameEntry? PreferExistingOwner(List<GameEntry> candidates, string remoteFileName)
    {
        var owners = candidates.Where(g => g.Backups.Any(b =>
            string.Equals(Path.GetFileName(b.File), remoteFileName, StringComparison.OrdinalIgnoreCase))).ToList();
        return owners.Count == 1 ? owners[0] : null;
    }

    /// <summary>同名歧义选择窗：库里有多个同名条目、云端目录无法确定归属时让用户指定目标游戏，
    /// 并提醒清理重复条目（选错会把备份导进另一个游戏，所以这里不猜）。取消返回 null。</summary>
    private GameEntry? PickOwnerGame(List<GameEntry> candidates, CloudRemoteFile item)
    {
        const string placeholder = "请选择目标游戏";
        string Caption(GameEntry g) => $"{g.DisplayName}（{g.NameEN}）· {g.Backups.Count} 份备份";

        var combo = new System.Windows.Controls.ComboBox
        {
            Width = 400, Height = 30, Style = (Style)FindResource("Combo"),
        };
        combo.Items.Add(placeholder);
        foreach (var g in candidates) combo.Items.Add(Caption(g));
        combo.SelectedIndex = 0;

        var info = new TextBlock
        {
            Text = $"本机游戏库里有 {candidates.Count} 个同名条目，云端目录「{item.Game}」无法确定属于哪一个。\n" +
                   "请选对目标游戏——选错会把这份备份导进另一个游戏；建议在游戏列表里删掉重复的那一个。\n\n" +
                   $"即将导入：{item.Name}",
            Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary"),
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
        };
        var okBtn = new System.Windows.Controls.Button
        {
            Content = "导入到该游戏", MinWidth = 110, Padding = new Thickness(12, 0, 12, 0),
            IsDefault = true, Style = (Style)FindResource("BtnPrimary"),
        };
        var cancelBtn = new System.Windows.Controls.Button
        {
            Content = "取消", MinWidth = 80, Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(8, 0, 0, 0), IsCancel = true, Style = (Style)FindResource("Btn"),
        };
        var btns = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        btns.Children.Add(okBtn);
        btns.Children.Add(cancelBtn);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(info);
        panel.Children.Add(combo);
        panel.Children.Add(btns);

        GameEntry? chosen = null;
        var dlg = new Window
        {
            Title = "选择这份备份属于哪个游戏", Owner = this,
            Width = 460, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("Panel"),
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            UseLayoutRounding = true,
        };
        DarkTitleBar.SetTheme(dlg, ThemeManager.IsDark);
        okBtn.Click += (_, _) =>
        {
            int i = combo.SelectedIndex - 1;
            if (i < 0)
            {
                MessageBox.Show(dlg, "请先选择一个目标游戏", "选择备份归属",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            chosen = candidates[i];
            dlg.DialogResult = true;
        };
        cancelBtn.Click += (_, _) => dlg.DialogResult = false;
        dlg.Content = panel;
        dlg.ShowDialog();
        return chosen;
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null || _busy) return;

        // 归属判定：手动映射 → 三名字直配 → AppId/别名认领。落到多条＝库里有同名条目（归属有歧义），
        // 先看哪个条目已经持有这份云端文件，仍不唯一就让用户确认，绝不静默挑第一条。
        var candidates = FindOwnerCandidates(item.Game);
        if (candidates.Count == 0)
        {
            MessageBox.Show(this,
                $"本机游戏库中没有「{item.Game}」。\n可点左下「归属到…」手动指派给某个本机游戏，或先添加该游戏后再下载。",
                "无法导入", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var game = candidates.Count == 1
            ? candidates[0]
            : PreferExistingOwner(candidates, item.Name) ?? PickOwnerGame(candidates, item);
        if (game == null) return;

        if (MessageBox.Show(this,
                $"将云端备份下载并导入到游戏「{game.DisplayName}」？\n{item.Name}\n{item.SizeText} · {item.MainTimeText}" +
                (item.UploadTimeText.Length > 0 ? $"（{item.UploadTimeText}）" : ""),
                "下载云端备份", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            != MessageBoxResult.OK) return;

        _busy = true;
        RefreshBtn.IsEnabled = DownloadBtn.IsEnabled = DeleteBtn.IsEnabled = MapBtn.IsEnabled = false;
        BusyBar.Visibility = Visibility.Visible;
        BusyBar.IsIndeterminate = false;
        BusyBar.Value = 0;
        StatusText.Text = "正在下载：" + item.Name;
        string tmp = Path.Combine(Store.BaseDir, "cache", "cloud", item.Name);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
            // 同名临时残留先清，避免 .part 之外的脏文件
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

            var dlProgress = new Progress<double>(f =>
            {
                BusyBar.Value = f;
                StatusText.Text = $"正在下载：{item.Name}（{f:P0}）";
            });
            await _client.DownloadAsync(item.RelPath, tmp, progress: dlProgress);

            StatusText.Text = "正在导入…";
            int before = game.Backups.Count;
            BackupRecord? adopted = null;
            await Task.Run(() => adopted = BackupService.AdoptDownloadedZip(game, tmp));
            bool added = game.Backups.Count > before;
            // 这份内容云端本就存在：登记「云端已存」，备份行禁用上传，避免原样回传撞 412 造重复副本
            if (adopted != null) CloudSyncService.MarkAdoptedFromCloud(adopted.File, item.RelPath);
            // 用户手动点名下载：解除「不再自动下载」登记（按该文件实际所在的云端目录段），恢复自动收集资格
            CloudSyncService.UndismissCloudBackup(item.Game, item.Name);
            StatusText.Text = added
                ? $"✓ 已导入「{game.DisplayName}」：{item.Name}"
                : $"本机已有这份备份，未重复导入：{item.Name}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "下载失败：" + ex.Message;
        }
        finally
        {
            // Adopt 成功会把 tmp 移走/删掉；下载损坏/导入抛错时残留 tmp 必须在这里兜底清理
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            _busy = false;
            BusyBar.Visibility = Visibility.Collapsed;
            RefreshBtn.IsEnabled = DownloadBtn.IsEnabled = DeleteBtn.IsEnabled = true;
            MapBtn.IsEnabled = true;
        }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item == null || _busy) return;
        if (MessageBox.Show(this,
                $"确认删除云端备份？\n{item.Game} / {item.ProfileText} / {item.Name}\n\n删除仅影响云端副本，本机备份不受影响，且不影响其他电脑已下载的备份。",
                "删除云端备份", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            != MessageBoxResult.Yes) return;

        _busy = true;
        RefreshBtn.IsEnabled = DownloadBtn.IsEnabled = DeleteBtn.IsEnabled = MapBtn.IsEnabled = false;
        try
        {
            await _client.DeleteAsync(item.RelPath);
            // 同步清理云端索引条目（失败静默：孤儿条目无害，Link 时 zip 已不存在不会被引用）
            try
            {
                var doc = await CloudSyncService.GetIndexAsync(_client);
                if (doc != null && doc.Entries.ContainsKey(item.RelPath))
                {
                    CloudIndex.Remove(doc, item.RelPath);
                    await CloudSyncService.PutIndexAsync(_client, doc);
                }
            }
            catch { }
            // 若删的是本机曾上传的副本：本地备份记录切到「已删重传」态（登记表置 Deleted，防对账补传）
            try
            {
                var localFile = CloudSyncService.FindLocalByRemote(item.RelPath);
                if (!string.IsNullOrEmpty(localFile)) CloudSyncService.MarkCloudDeleted(localFile);
            }
            catch { }
            StatusText.Text = "✓ 已删除：" + item.Name;
            // 删除已成功，之后的列表刷新失败不能报「删除失败」（旧逻辑的误报）
            try { await LoadAsync(); }
            catch (Exception lex) { StatusText.Text = "✓ 已删除（列表刷新失败，可稍后重开窗口）：" + lex.Message; }
        }
        catch (Exception ex)
        {
            StatusText.Text = "删除失败：" + ex.Message;
            _busy = false;
            RefreshBtn.IsEnabled = DownloadBtn.IsEnabled = DeleteBtn.IsEnabled = true;
            MapBtn.IsEnabled = true;
        }
    }

    private static bool SafeEquals(string remoteSegment, string? localName)
    {
        if (string.IsNullOrEmpty(localName)) return false;
        return string.Equals(remoteSegment, BackupService.SafeName(localName), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>枚举目录可能是很长的云端路径，状态栏只显示末两段。</summary>
    private static string Shorten(string dir)
    {
        var parts = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? dir : string.Join("/", parts[^2], parts[^1]);
    }

    private void ShowIndeterminate(string text)
    {
        StatusText.Text = text;
        BusyBar.Visibility = Visibility.Visible;
        BusyBar.IsIndeterminate = true;
    }
}
