using GameSaveManager.Models;

namespace GameSaveManager.Services;

/// <summary>
/// 游玩会话统计的纯逻辑部分（无 UI/定时器/进程依赖，便于冒烟单测）。
/// 计时口径：游戏进程出现 → 消失为一个会话；轮询周期 3 秒，端点误差 ≤3 秒。
/// 安装本应用之前的历史无法追溯，总时长只统计闭合后的会话。
/// </summary>
public static class PlayTracker
{
    /// <summary>短于此长度的会话不计入（启动器闪退/更新程序/误触运行）。中断会话同样适用。</summary>
    public const int MinSessionSeconds = 20;

    /// <summary>每游戏保留的会话上限：超出时丢弃最旧的，并从总时长中扣除。</summary>
    public const int MaxSessions = 300;

    /// <summary>闭合一次会话并写入游戏条目。返回是否计入（过短会话被丢弃）。
    /// 中断会话（应用退出时游戏仍在运行）计入时长但不更新「最后关闭时间」。</summary>
    public static bool CloseSession(GameEntry g, DateTime start, DateTime end, string source,
        bool interrupted = false)
    {
        if (g == null || end < start) return false;
        long seconds = (long)(end - start).TotalSeconds;
        if (seconds < MinSessionSeconds) return false;

        g.PlaySeconds += seconds;
        g.Sessions.Add(new PlaySession
        {
            Start = start,
            End = end,
            Seconds = seconds,
            Source = string.IsNullOrEmpty(source) ? "monitor" : source,
            Interrupted = interrupted,
        });

        while (g.Sessions.Count > MaxSessions)
        {
            // 会话按开始时间先后追加；超出上限从最旧端淘汰（防御乱序：取最小 Start）
            int oldest = 0;
            for (int i = 1; i < g.Sessions.Count; i++)
                if (g.Sessions[i].Start < g.Sessions[oldest].Start) oldest = i;
            var old = g.Sessions[oldest];
            g.Sessions.RemoveAt(oldest);
            g.PlaySeconds = Math.Max(0, g.PlaySeconds - old.Seconds);
        }

        if (!interrupted) g.LastClosedAt = end;
        return true;
    }

    /// <summary>启动时清理崩溃遗留的未闭合会话（End == MinValue）。
    /// 正常退出时会话已在 OnClosed 结算；未闭合说明上次异常终止，结束时刻不可知，直接丢弃。</summary>
    public static int DropStaleOpenSessions(System.Collections.Generic.IEnumerable<GameEntry> games)
    {
        int dropped = 0;
        foreach (var g in games)
        {
            for (int i = g.Sessions.Count - 1; i >= 0; i--)
                if (g.Sessions[i].End == default) { g.Sessions.RemoveAt(i); dropped++; }
        }
        return dropped;
    }

    /// <summary>合并重复入库条目的游玩统计：关闭时间取较新；会话按开始时刻去重合并；
    /// 总时长按合并后会话重算（不能直接相加，避免重复计数）。</summary>
    public static void MergeStats(GameEntry keep, GameEntry drop)
    {
        if (drop.LastClosedAt > keep.LastClosedAt) keep.LastClosedAt = drop.LastClosedAt;
        var have = keep.Sessions.Select(s => s.Start).ToHashSet();
        foreach (var s in drop.Sessions)
            if (s.End != default && have.Add(s.Start)) keep.Sessions.Add(s);
        // 按开始时间排序，超限丢弃最旧
        keep.Sessions.Sort((a, b) => a.Start.CompareTo(b.Start));
        while (keep.Sessions.Count > MaxSessions) keep.Sessions.RemoveAt(0);
        keep.PlaySeconds = keep.Sessions.Sum(s => s.Seconds);
    }

    /// <summary>近 N 天内开始的会话总秒数。</summary>
    public static long SecondsSince(GameEntry g, DateTime since) =>
        g.Sessions.Where(s => s.Start >= since).Sum(s => s.Seconds);

    /// <summary>秒 → 人类可读时长：0「—」；&lt;60「不足 1 分钟」；&lt;1 小时「N 分钟」；
    /// &lt;1 天「N 小时 M 分」；再长「N 天 M 小时」。</summary>
    public static string FormatDuration(long seconds)
    {
        if (seconds <= 0) return "—";
        if (seconds < 60) return "不足 1 分钟";
        if (seconds < 3600) return $"{seconds / 60} 分钟";
        long hours = seconds / 3600;
        long mins = seconds % 3600 / 60;
        if (hours < 24) return mins > 0 ? $"{hours} 小时 {mins} 分" : $"{hours} 小时";
        long days = hours / 24;
        long remHours = hours % 24;
        return remHours > 0 ? $"{days} 天 {remHours} 小时" : $"{days} 天";
    }
}
