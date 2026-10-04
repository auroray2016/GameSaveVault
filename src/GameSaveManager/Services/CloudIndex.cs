using System.Text.Json;
using GameSaveManager.Models;

namespace GameSaveManager.Services;

/// <summary>
/// 云端索引：云端根目录一个 index.json，记录所有已上传 zip 的关键字段（核心是存档时间）。
/// PROPFIND 枚举 + 1 次 GET 索引即可拿到每份 zip 的「游戏真实保存时刻」，无需下载 zip。
/// 云端目录因此保持干净：只有备份 zip + 这一个索引文件。
/// 两机并发"读-改-写"索引可能互相覆盖丢条目——可自愈：对账（Reconcile）时从本机上传
/// 登记表补写缺失条目；孤儿条目（zip 已删）在 Link 时自然不被引用，删除时同步清理。
/// </summary>
public static class CloudIndex
{
    public const string FileName = "index.json";

    /// <summary>单个 zip 的索引条目。键 = zip 的云端相对路径（与 WebDavClient.UploadAsync
    /// 返回值一致——412 同名冲突加机器名后缀后是最终路径，不能假设等于请求时的 relPath）。</summary>
    public sealed class Entry
    {
        public long T { get; set; }          // 存档时间 UTC ticks（0 = 无存档/空锚点/未知）
        public string P { get; set; } = "";  // 玩家名（冗余，诊断用；目录结构已含归属）
        public string N { get; set; } = "";  // 备份备注
        public string K { get; set; } = "";  // full | incr
        public DateTime U { get; set; }      // 上传时刻
    }

    public sealed class Doc
    {
        public int V { get; set; } = 1;
        public Dictionary<string, Entry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>游戏身份元数据（跨设备互认）：键 = 云端目录名（游戏安全名）。
        /// 每台设备上传时自报本机 AppId 与全部名字（安全化后），形成别名沉淀——
        /// 两台电脑名字不一致（纠正时机不同/手写差异）时，凭 AppId 或任一别名互相认领目录。</summary>
        public Dictionary<string, GameMeta> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>云端某游戏目录的身份记录：AppId（0=非 Steam/未知）+ 别名集合（安全化后的名字）。</summary>
    public sealed class GameMeta
    {
        public int A { get; set; }
        public List<string>? Al { get; set; }
    }

    /// <summary>别名集合上限：防止长期改名/多设备把索引撑大；超限时挤掉最旧的别名。</summary>
    public const int MaxAliases = 12;

    /// <summary>上传方自报身份：把游戏的 AppId 与全部名字（安全化后）并入云端索引该目录的记录。
    /// 幂等合并：别名保序追加去重。AppId 冲突保护：该目录段已登记另一个正 AppId 时（两款不同
    /// Steam 游戏的显示名安全化后撞名），既不覆盖 AppId 也不并入本次别名——否则别名污染会让
    /// CloudDirsFor 把两款游戏互相认领、下载串档（历史已污染的目录可用云端管理的「归属到…」纠正）。</summary>
    public static void UpsertIdentity(Doc doc, string gameKey, int appId, IEnumerable<string> names)
    {
        if (string.IsNullOrWhiteSpace(gameKey)) return;
        if (!doc.Games.TryGetValue(gameKey, out var meta)) meta = new GameMeta();
        bool appIdConflict = appId > 0 && meta.A > 0 && meta.A != appId;
        if (appId > 0 && meta.A == 0) meta.A = appId;   // 只在无 AppId 时首次落定；冲突不覆盖
        var list = meta.Al ?? new List<string>();
        if (!appIdConflict)
        {
            foreach (var n in names)
            {
                if (string.IsNullOrWhiteSpace(n)) continue;
                var safe = n.Trim();
                if (list.Contains(safe, StringComparer.OrdinalIgnoreCase)) continue;
                list.Add(safe);
            }
            while (list.Count > MaxAliases) list.RemoveAt(0);
        }
        meta.Al = list;
        doc.Games[gameKey] = meta;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>从本地备份记录构建索引条目（入队时快照；之后改备注不回写，可接受——核心是存档时间）。</summary>
    public static Entry BuildFor(BackupRecord r) => new()
    {
        T = r.SavedAt == DateTime.MinValue ? 0 : r.SavedAt.ToUniversalTime().Ticks,
        P = r.Profile ?? "",
        N = r.Note ?? "",
        K = r.Kind ?? "",
        U = DateTime.UtcNow,
    };

    public static string Serialize(Doc doc) => JsonSerializer.Serialize(doc, JsonOpts);

    /// <summary>解析索引；损坏/解析失败返回 null。调用方按空索引处理，但不得立即覆盖 PUT——
    /// 避免把其他机器写入的完好索引清掉（只在有本地条目要 upsert 时才写回）。</summary>
    public static Doc? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var doc = JsonSerializer.Deserialize<Doc>(json);
            if (doc == null) return null;
            doc.Entries ??= new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            doc.Games ??= new Dictionary<string, GameMeta>(StringComparer.OrdinalIgnoreCase);   // 旧版索引无身份段
            return doc;
        }
        catch { return null; }
    }

    public static void Upsert(Doc doc, string rel, Entry e) => doc.Entries[rel] = e;

    public static void Remove(Doc doc, string rel) => doc.Entries.Remove(rel);

    /// <summary>把索引条目关联到 PROPFIND 枚举出的 zip 列表（就地填 CloudRemoteFile.SaveAt）。
    /// 纯函数：索引里没有的 zip（旧包/他机新包索引还没写上）SaveAt 保持 null，由调用方回退上传时间。</summary>
    public static void Link(IEnumerable<CloudRemoteFile> files, Doc? doc)
    {
        if (doc == null) return;
        foreach (var f in files)
        {
            if (!doc.Entries.TryGetValue(f.RelPath, out var e)) continue;
            if (e.T > 0)
                f.SaveAt = new DateTime(e.T, DateTimeKind.Utc);
        }
    }
}
