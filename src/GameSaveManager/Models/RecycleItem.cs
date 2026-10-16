using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GameSaveManager.Models;

/// <summary>回收区中的一份已删除备份（recycle.json 索引项）。
/// zip 实体扁平存放在 backups\_recycle\{Id}.zip，原始目录结构保存在 OrigRel，还原时逐字节归位。</summary>
public class RecycleItem
{
    /// <summary>回收项 Id，同时是 _recycle 内的物理文件名（{Id}.zip）。</summary>
    public string Id { get; set; } = "";
    /// <summary>删除时所属游戏 Id（游戏可能已被移除，还原时允许按游戏名兜底匹配）。</summary>
    public string GameId { get; set; } = "";
    /// <summary>删除时的游戏显示名（窗口展示 + 游戏重新入库后的兜底匹配）。</summary>
    public string GameName { get; set; } = "";
    /// <summary>所属玩家槽位（""=通用备份）。</summary>
    public string Profile { get; set; } = "";
    /// <summary>原始存放位置（相对 BackupRoot；旧版根外绝对路径记录则存绝对路径），还原目标。</summary>
    public string OrigRel { get; set; } = "";
    /// <summary>原 zip 文件名（保留增量备份 BaseFile 的对位关系，还原必须用原名）。</summary>
    public string OrigFileName { get; set; } = "";
    /// <summary>删除前整条 BackupRecord 的 JSON 快照：还原时完整恢复时间/备注/锁定/增量链等。
    /// 孤儿目录/无记录文件为空——这类项只还原文件，由未关联备份扫描重新挂载。</summary>
    public string RecordJson { get; set; } = "";
    public long Size { get; set; }
    /// <summary>备份打包时间（展示用，与 RecordJson 冗余，避免每行反序列化）。</summary>
    public DateTime Time { get; set; }
    /// <summary>游戏内最后保存时刻（展示用）。</summary>
    public DateTime SavedAt { get; set; }
    public string Note { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool Hot { get; set; }
    /// <summary>删除来源：manual 手动单份 | player 删除玩家 | removal 移除游戏 | prune 自动轮转 | orphan 残留清理。</summary>
    public string Source { get; set; } = "manual";
    /// <summary>删除时云端副本也被一并删除（仅提示：还原只恢复本机，云端需重新上传）。</summary>
    public bool RemoteAlsoDeleted { get; set; }
    public DateTime DeletedAt { get; set; }
    public DateTime ExpireAt { get; set; }

    // ── 展示辅助（不持久化）──
    [JsonIgnore] public string SizeText => BackupServiceText(Size);
    [JsonIgnore]
    public string TimeText => Time == default ? "" : Time.ToString("yyyy-MM-dd HH:mm");
    [JsonIgnore]
    public string SavedAtText => SavedAt == default ? "" : SavedAt.ToString("yyyy-MM-dd HH:mm");
    [JsonIgnore]
    public string DeletedAtText => DeletedAt == default ? "" : DeletedAt.ToString("MM-dd HH:mm");
    [JsonIgnore]
    public string ProfileText => string.IsNullOrEmpty(Profile) ? "通用" : Profile;
    [JsonIgnore]
    public string KindText => Kind == "incr" ? "增量" : "全量";
    [JsonIgnore]
    public string SourceText => Source switch
    {
        "player" => "删除玩家",
        "removal" => "移除游戏",
        "prune" => "自动淘汰",
        "orphan" => "残留清理",
        _ => "手动删除",
    };
    /// <summary>剩余保留天数（向上取整：今天删的显示 15 天）；已过期显示 0。</summary>
    [JsonIgnore]
    public int DaysLeft => Math.Max(0, (int)Math.Ceiling((ExpireAt - DateTime.Now).TotalDays));
    [JsonIgnore]
    public string DaysLeftText => DaysLeft <= 0 ? "待清除" : $"{DaysLeft} 天后清除";
    /// <summary>剩余 ≤3 天：列表里用警示色提醒。</summary>
    [JsonIgnore]
    public bool DaysLeftUrgent => DaysLeft > 0 && DaysLeft <= 3;
    /// <summary>首行副文案：来源 · 全量/增量 · 备注（有的话）。</summary>
    [JsonIgnore]
    public string SubText
    {
        get
        {
            var parts = new List<string> { SourceText, KindText };
            if (!string.IsNullOrEmpty(Note) && Note != "手动备份") parts.Add(Note);
            return string.Join(" · ", parts);
        }
    }

    private static string BackupServiceText(long b) => Services.BackupService.FormatBytes(b);
}
