using System.Diagnostics;
using System.IO;
using System.Windows;
using GameSaveManager.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace GameSaveManager;

/// <summary>已移除游戏的残留备份/玩家镜像清理窗口（「备份目录」按钮打开）。</summary>
public partial class OrphanBackupsWindow : Window
{
    public OrphanBackupsWindow()
    {
        InitializeComponent();
        DarkTitleBar.SetTheme(this, ThemeManager.IsDark);
        Reload();
    }

    /// <summary>
    /// 名称列吃掉列表剩余宽度：四列固定宽合计 372（内容104+占用72+最近文件92+操作104），
    /// 再留 8px 边距余量；窄窗时最小 140，保证「打开/删除」按钮永不被裁。
    /// </summary>
    private void OnOrphanListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double fixedCols = 104 + 72 + 92 + 104 + 8;
        ColName.Width = System.Math.Max(140, e.NewSize.Width - fixedCols);
    }

    private void Reload()
    {
        var items = BackupService.ListOrphans();
        OrphanList.ItemsSource = items;
        RootBox.Text = Store.BackupRoot;
        EmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        long total = items.Sum(x => x.Size);
        SummaryText.Text = items.Count == 0
            ? "没有残留备份——磁盘上的所有备份都属于在库游戏。"
            : $"共 {items.Count} 项已移除游戏残留，占用 {BackupService.FormatBytes(total)}。" +
              "重新添加同名游戏时这些数据会自动找回；确认不再需要可逐项删除。";
    }

    private void OnOpenRoot(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Store.BackupRoot);
            Process.Start("explorer.exe", Store.BackupRoot);
        }
        catch { }
    }

    private void OnOpenItem(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not BackupService.OrphanBackupInfo o) return;
        try
        {
            Directory.CreateDirectory(o.Dir);
            Process.Start("explorer.exe", o.Dir);
        }
        catch { }
    }

    private void OnDeleteItem(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not BackupService.OrphanBackupInfo o) return;
        var r = MessageBox.Show(this,
            $"彻底删除「{o.Name}」的{o.TypeText}？\n{o.DetailText}，占用 {o.SizeText}。\n删除后无法恢复。",
            "确认删除残留", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;
        try
        {
            BackupService.DeleteOrphan(o.Dir);
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "删除失败：" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();
}
