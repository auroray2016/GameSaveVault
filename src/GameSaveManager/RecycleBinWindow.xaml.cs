using System;
using System.Linq;
using System.Windows;
using GameSaveManager.Models;
using GameSaveManager.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace GameSaveManager;

/// <summary>「最近删除」窗口（主界面 ⋯ 更多 → 最近删除）。
/// 列出 15 天内删除的备份，支持还原/单项永久删除/清空，到期项由 RecycleService 自动清理。</summary>
public partial class RecycleBinWindow : Window
{
    public RecycleBinWindow()
    {
        InitializeComponent();
        DarkTitleBar.SetTheme(this, ThemeManager.IsDark);
        Reload();
    }

    /// <summary>
    /// 游戏列吃掉列表剩余宽度：六列固定宽合计 630（玩家90+存档时间132+大小64+删除时间92+
    /// 保留期限96+操作156），再留 8px 余量；窄窗最小 150，保证「还原/永久删除」不被裁。
    /// </summary>
    private void OnListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double fixedCols = 90 + 132 + 64 + 92 + 96 + 156 + 8;
        ColGame.Width = Math.Max(150, e.NewSize.Width - fixedCols);
    }

    private void Reload()
    {
        var items = RecycleService.List();
        RecycleList.ItemsSource = items;
        EmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearBtn.IsEnabled = items.Count > 0;
        SummaryText.Text = items.Count == 0
            ? $"最近删除是空的：删除的备份会保留 {RecycleService.RetentionDays} 天，到期自动清除。"
            : $"共 {items.Count} 份最近删除的备份，占用 {BackupService.FormatBytes(items.Sum(x => x.Size))}；" +
              $"保留 {RecycleService.RetentionDays} 天，到期自动清除，也可以随时还原。";
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not RecycleItem item) return;
        // 还原前最后一次确认非必须（操作可逆、结果即回到列表），直接执行并给出结果说明
        try
        {
            var result = RecycleService.Restore(item.Id);
            if (!result.Ok)
            {
                MessageBox.Show(this, result.Message, "还原失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Reload();
            MessageBox.Show(this,
                $"《{item.GameName}》的备份已还原：{result.Message}。",
                "还原成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "还原失败：" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnPurge(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not RecycleItem item) return;
        var r = MessageBox.Show(this,
            $"永久删除《{item.GameName}》这份备份（{item.SizeText}）？\n" +
            "立即从磁盘清除，无法找回，也不能再还原。",
            "永久删除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;
        try
        {
            RecycleService.PurgeNow(item.Id);
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "删除失败：" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        int n = RecycleService.Count;
        if (n == 0) return;
        var r = MessageBox.Show(this,
            $"清空「最近删除」中的全部 {n} 份备份？\n" +
            "它们会立即从磁盘永久删除，无法找回，也不能再还原。\n\n" +
            "确定要清空吗？",
            "清空最近删除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;
        try
        {
            var (removed, failed) = RecycleService.ClearAll();
            Reload();
            if (failed > 0)
                MessageBox.Show(this,
                    $"已永久删除 {removed} 份；{failed} 份因文件被占用未能删除（请关闭同步盘/压缩软件后重试）。",
                    "部分失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "清空失败：" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();
}
