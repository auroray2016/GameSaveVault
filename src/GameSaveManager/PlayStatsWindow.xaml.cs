using System.Windows;
using System.Windows.Controls;
using GameSaveManager.Models;
using GameSaveManager.Services;

namespace GameSaveManager;

/// <summary>「📊 游玩详情」：导入日期 / 最后关闭时间 / 总时长 / 近 7·30 天 / 会话明细。只读统计。</summary>
public partial class PlayStatsWindow : Window
{
    public PlayStatsWindow(GameEntry g)
    {
        InitializeComponent();
        DarkTitleBar.SetTheme(this, ThemeManager.IsDark);
        Title = $"游玩详情 · {g.DisplayName}";
        DataContext = BuildVm(g);
    }

    private static PlayStatsVm BuildVm(GameEntry g)
    {
        var sessions = g.Sessions.OrderByDescending(s => s.Start).ToList();
        DateTime now = DateTime.Now;
        long sec7 = PlayTracker.SecondsSince(g, now.AddDays(-7));
        long sec30 = PlayTracker.SecondsSince(g, now.AddDays(-30));

        // 「最后关闭」卡片：主行绝对时刻，次行说明。
        // 已记录退出→真实退出时间+相对时间；只监测到启动→Steam 启动时间并注明；都没有→未记录
        string lastClose, lastCloseHint;
        if (g.LastClosedAt != DateTime.MinValue)
        {
            lastClose = $"{g.LastClosedAt:yyyy-MM-dd HH:mm}";
            lastCloseHint = Store.TimeAgo(g.LastClosedAt);
        }
        else if (g.LastPlayed != DateTime.MinValue)
        {
            lastClose = $"{g.LastPlayed:yyyy-MM-dd HH:mm}";
            lastCloseHint = "Steam 记录的启动时间，玩过一次后更新为退出时间";
        }
        else
        {
            lastClose = "未记录";
            lastCloseHint = "";
        }

        return new PlayStatsVm
        {
            Title = g.DisplayName,
            Sub = string.IsNullOrWhiteSpace(g.SubName) ? g.TypeBadge : $"{g.TypeBadge} · {g.SubName}",
            Added = g.AddedAtText,
            LastClose = lastClose,
            LastCloseHint = lastCloseHint,
            LastCloseHintVisible = string.IsNullOrEmpty(lastCloseHint)
                ? Visibility.Collapsed : Visibility.Visible,
            Total = PlayTracker.FormatDuration(g.PlaySeconds),
            Average = sessions.Count > 0
                ? PlayTracker.FormatDuration(g.PlaySeconds / sessions.Count)
                : "—",
            Count = sessions.Count > 0 ? $"/ {sessions.Count} 次" : "",
            Week7 = PlayTracker.FormatDuration(sec7),
            Week30 = PlayTracker.FormatDuration(sec30),
            CountHeader = sessions.Count > 0 ? $"游玩记录（{sessions.Count} 次，最新在前）" : "游玩记录",
            EmptyHintVisible = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed,
            Sessions = sessions,
        };
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

/// <summary>弹窗一次性视图模型（打开弹窗时计算，不做变更通知）。</summary>
public sealed class PlayStatsVm
{
    public string Title { get; init; } = "";
    public string Sub { get; init; } = "";
    public string Added { get; init; } = "";
    public string LastClose { get; init; } = "";
    public string LastCloseHint { get; init; } = "";
    public Visibility LastCloseHintVisible { get; init; }
    public string Total { get; init; } = "";
    public string Average { get; init; } = "";
    public string Count { get; init; } = "";
    public string Week7 { get; init; } = "";
    public string Week30 { get; init; } = "";
    public string CountHeader { get; init; } = "";
    public Visibility EmptyHintVisible { get; init; }
    public System.Collections.Generic.List<PlaySession> Sessions { get; init; } = new();
}
