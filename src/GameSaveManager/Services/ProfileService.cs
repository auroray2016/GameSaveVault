using System.IO;
using System.Text.Json;
using GameSaveManager.Models;

namespace GameSaveManager.Services;

/// <summary>
/// 玩家存档（一台电脑多人玩同一游戏，各自独立的存档与备份）。
/// 原理（zip 唯一事实源）：每位玩家的进度 = 他名下的备份 zip，不再维护镜像目录。
/// 切换 = 当前 live 存档打包给当前玩家（成功才准动 live；"没有变化"则跳过）
///        → 目标玩家最新 zip 还原覆盖 live（严格清理目标快照没有的新文件）。
/// 玩家列表 = 持久化的 Players 卡槽名单（创建/改名/删除玩家时维护；名下备份删光卡槽也在）。
/// 旧版数据没有该名单时，启动迁移 BackfillPlayers 从备份记录 Profile 去重回填；回填前临时回退推导。
/// 备份 zip 按玩家分目录存放（backups\&lt;游戏&gt;\&lt;玩家&gt;\），备份记录按玩家过滤展示。
/// 云同步游戏（检测到 Steam 云路径）禁用——云会把旧档同步回来覆盖本地切换。
/// </summary>
public static class ProfileService
{
    /// <summary>旧版镜像的 slot.json（迁移读一次即弃；新数据不再产生）。</summary>
    private class SlotManifest
    {
        public string Name { get; set; } = "";
        public DateTime Created { get; set; }
        public List<string> Paths { get; set; } = new();
    }

    /// <summary>profiles\{GUID}\game.json：玩家数据根的"身份证"。games.json 丢失后游戏以新 GUID 重新入库时，
    /// 靠它记录的游戏名候选把旧 GUID 下的玩家数据回迁给新条目（否则会永久失联）。</summary>
    private class RootMarker
    {
        public string Id { get; set; } = "";
        public string NameCN { get; set; } = "";
        public string NameEN { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string ActiveProfile { get; set; } = "";
    }

    private const string RootMarkerFile = "game.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>玩家数据根：备份根\_appdata\profiles（旧版镜像所在地；现仅存 game.json 身份证与迁移残留）。</summary>
    public static string ProfilesRoot =>
        Path.Combine(Store.BackupRoot, Store.AppSnapshotDir, "profiles");

    public static string RootFor(GameEntry g) =>
        Path.Combine(ProfilesRoot, g.Id);

    /// <summary>该游戏的全部玩家名：以持久化 Players 卡槽名单为准（保序去重）。
    /// 卡槽独立于备份存在——删光名下备份玩家仍在，只能右键卡槽「删除玩家」移除。
    /// 名单为空的旧版数据临时从备份记录 Profile 去重推导；二者皆空的非云游戏
    /// 返回虚拟「玩家1」（形式卡槽，不持久化，UI 与切换逻辑照常工作）。</summary>
    public static List<string> ListSlots(GameEntry g)
    {
        try
        {
            g.Players ??= new List<string>();
            if (g.Players.Count > 0)
                return g.Players.Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            var fromBackups = SlotsFromBackups(g);
            if (fromBackups.Count > 0) return fromBackups;
            // 无任何真实玩家：非云游戏补虚拟玩家1（云游戏返回空列表，玩家功能本就禁用）
            return IsDefaultPlayerApplicable(g)
                ? new List<string> { DefaultFirstPlayerName }
                : new List<string>();
        }
        catch { return new(); }
    }

    /// <summary>从备份记录 Profile 推导玩家名单（按最早备份时间排序≈创建顺序）。启动回填/找回时使用。</summary>
    private static List<string> SlotsFromBackups(GameEntry g)
        => g.Backups
            .Where(b => !string.IsNullOrEmpty(b.Profile))
            .GroupBy(b => b.Profile!, StringComparer.Ordinal)
            .Select(x => (Name: x.Key, First: x.Min(b => b.Time)))
            .OrderBy(x => x.First).ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Name)
            .ToList();

    /// <summary>把玩家名登记进卡槽名单（云端下载的他机新玩家、找回、镜像迁移等所有入口统一走这里）。</summary>
    public static void RegisterSlot(GameEntry g, string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return;
        g.Players ??= new List<string>();
        if (!g.Players.Contains(name, StringComparer.Ordinal)) g.Players.Add(name);
    }

    /// <summary>该玩家"游戏内最后一次保存"的时间：他名下所有备份 SavedAt（游戏真实保存时刻，
    /// 非打包/上传时刻）的最大值。只切换不玩游戏时该时间不会变；空玩家（无备份/空锚点）返回 null。</summary>
    public static DateTime? LastProgressSaved(GameEntry g, string slot)
    {
        try
        {
            // 时间轴口径：取该玩家全部备份中「游戏内真实保存时刻」SavedAt 的最大值。
            // 绝不能按打包时间 Time 取最新那条再读 SavedAt——还原前安全快照打包时间最新、
            // 内容却可能是旧进度（英灵神殿实测：22:49 的安全快照记着前一天 23:18 的 live），
            // 会让云端核对误报「云端有较新备份」。空锚点 SavedAt=MinValue 不参与。
            var savedTimes = g.Backups
                .Where(b => (b.Profile ?? "") == slot && b.SavedAt != DateTime.MinValue)
                .Select(b => (DateTime?)b.SavedAt)
                .ToList();
            if (savedTimes.Count > 0) return savedTimes.Max()!.Value;
            // 兜底：旧记录字段均未回填（启动迁移未跑/损坏）时现读 Time 最新一份的 manifest
            var latest = g.Backups.Where(b => (b.Profile ?? "") == slot)
                .OrderByDescending(b => b.Time).FirstOrDefault();
            if (latest == null) return null;
            var t = BackupService.SavedAtFromManifest(BackupService.ReadManifest(latest.File));
            return t == DateTime.MinValue ? null : t;
        }
        catch { return null; }
    }

    /// <summary>游戏当前 live 存档的最后保存时间（各存档目录内存档文件的最新修改时间）。
    /// 用于切换确认窗标注"当前玩家进度"——玩家切换后若继续游玩，live 时间会比他最新备份更新。</summary>
    public static DateTime? LiveProgressSaved(GameEntry g)
    {
        try
        {
            DateTime? max = null;
            foreach (var dir in EffectivePaths(g))
                foreach (var f in SafeFiles(dir))
                {
                    try
                    {
                        var t = File.GetLastWriteTime(f);
                        if (t.Year > 1980 && (max == null || t > max)) max = t;
                    }
                    catch { }
                }
            return max;
        }
        catch { return null; }
    }

    /// <summary>该游戏是否可用玩家功能（有云存档则禁用）。</summary>
    public static string? DisabledReason(GameEntry g)
    {
        if (g.HasCloudSave)
            return "该游戏使用 Steam 云同步，多个玩家的存档会被云档覆盖，玩家存档已禁用。如需多人各玩各的，请先在 Steam 中关闭该游戏的云同步。";
        return null;
    }

    /// <summary>
    /// 参与玩家切换的存档目录：自动从「存档位置」中识别
    /// （路径含 save/savedata/savegame/remote 的目录；配置/截图/注册表不参与）。
    /// 一个都识别不到时兜底使用全部非注册表目录，保证功能可用。
    /// </summary>
    public static List<string> EffectivePaths(GameEntry g)
    {
        var dirs = g.SavePaths
            .Where(p => !p.IsReg && !string.IsNullOrWhiteSpace(p.Path))
            .Select(p => p.Path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var chosen = dirs.Where(IsLikelySaveDir).ToList();
        if (chosen.Count == 0)
            chosen = dirs;   // 目录名不规范时兜底：全部存档位置都参与
        return chosen.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>智能默认判定：目录名或路径片段像存档目录。</summary>
    public static bool IsLikelySaveDir(string path)
    {
        string p = path.ToLowerInvariant().Replace('/', '\\');
        string leaf = Path.GetFileName(p.TrimEnd('\\'));
        return leaf.Contains("save") || p.EndsWith("\\remote") || p.Contains("\\remote\\")
               || leaf.Contains("savedata") || leaf.Contains("savedgame");
    }

    /// <summary>默认卡槽名：新加入的游戏只在 UI 上显示一个「形式上的玩家」——
    /// 不建目录、不写 game.json、不打空备份；只有该玩家第一次真实备份时才实体化。
    /// 名字可在设置中修改（Store.Settings.DefaultPlayerName），未初始化时回退 "玩家1"。</summary>
    public static string DefaultFirstPlayerName =>
        string.IsNullOrWhiteSpace(Store.Settings?.DefaultPlayerName) ? "玩家1"
        : Store.Settings.DefaultPlayerName;

    /// <summary>游戏当前是否处于「只有虚拟玩家1」状态：无真实 Players 名单、无具名备份、非云存档。
    /// 此时 ListSlots 返回虚拟玩家1，磁盘零副作用。</summary>
    public static bool IsDefaultPlayerApplicable(GameEntry g)
    {
        if (g == null || g.HasCloudSave) return false;
        if (g.Players != null && g.Players.Any(n => !string.IsNullOrWhiteSpace(n))) return false;
        if (g.Backups.Any(b => !string.IsNullOrEmpty(b.Profile))) return false;
        return true;
    }

    /// <summary>旧版通用备份（Profile 空）认领（启动时/测试用，幂等）：
    /// · 有真实玩家：认领给激活玩家（不在名单则首位）；
    /// · 无真实玩家（Players 空、亦无具名备份）：补建玩家1后认领给他。
    /// 零备份新游戏不受影响——保持虚拟显示、磁盘零副作用。返回涉及游戏数。</summary>
    public static int MaterializeLegacyDefaultPlayers()
    {
        int n = 0;
        foreach (var g in Store.Games)
        {
            if (g.HasCloudSave) continue;
            if (!g.Backups.Any(b => string.IsNullOrEmpty(b.Profile))) continue;
            var real = (g.Players ?? new())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal).ToList();
            if (real.Count == 0) real = SlotsFromBackups(g);
            string target;
            if (real.Count > 0)
            {
                target = !string.IsNullOrEmpty(g.ActiveProfile) && real.Contains(g.ActiveProfile)
                    ? g.ActiveProfile : real[0];
            }
            else
            {
                RegisterSlot(g, DefaultFirstPlayerName);
                g.ActiveProfile = DefaultFirstPlayerName;
                target = DefaultFirstPlayerName;
            }
            try { BackupService.AdoptBackupsForPlayer(g, target); } catch { }
            try { WriteGameMarker(g); } catch { }
            n++;
        }
        if (n > 0) Store.Save();
        return n;
    }

    /// <summary>旧名兼容：通用备份认领（统一逻辑见 MaterializeLegacyDefaultPlayers）。</summary>
    public static void MigrateLegacyBackupsToPlayers() => MaterializeLegacyDefaultPlayers();

    /// <summary>实体版遗留回迁（启动时，幂等）：名单只有「玩家1」且她名下没有任何备份
    /// （亦无其他具名备份）——这是上一版「加入游戏即建玩家1」写下的虚假实体，
    /// 清回虚拟态：Players 清空、激活态清空、删 game.json（空目录连目录删）。
    /// 用户显式命名/改名产生的卡槽名字不同，不受影响。</summary>
    public static int DematerializeEmptyDefaultPlayers()
    {
        int n = 0;
        foreach (var g in Store.Games)
        {
            if (g.HasCloudSave) continue;
            bool onlyDefault = g.Players is { Count: 1 }
                && g.Players[0] == DefaultFirstPlayerName
                && g.ActiveProfile == DefaultFirstPlayerName;
            if (!onlyDefault) continue;
            // 她（或任何人）有真实备份记录：保留实体
            if (g.Backups.Any(b => !string.IsNullOrEmpty(b.Profile))) continue;
            g.Players.Clear();
            g.ActiveProfile = "";
            try { TryClearRootMarker(g); } catch { }
            n++;
        }
        if (n > 0) Store.Save();
        return n;
    }

    /// <summary>新建玩家（用户点「＋ 新玩家」）：
    /// · 虚拟玩家1仍在（尚无真实玩家）：新名字直接承接默认卡槽位置——旧通用备份先认领给他，
    ///   当前进度打包（与认领来的快照一致则零变化跳过，不产生重复包）；
    /// · 已有真实玩家：先把当前玩家的进度打包（等同切走），再登记新玩家。
    /// 不打空锚点；显式玩家是真实卡槽，写 game.json；他的真实备份由 Create 产生。</summary>
    public static void CreatePlayer(GameEntry g, string name)
    {
        name = (name ?? "").Trim();
        ValidateName(name);
        EnsureUsable(g);
        var paths = EffectivePaths(g);
        if (paths.Count == 0)
            throw new InvalidOperationException("没有识别到可切换的存档目录，请先在「存档位置」中确认游戏的存档路径");
        // ListSlots 含虚拟占位：新名字不能与已显示筹码冲突；
        // 例外：虚拟状态下显式命名「玩家1」= 直接给默认卡槽命名，承接不冲突
        bool virtualState = IsDefaultPlayerApplicable(g);
        if (ListSlots(g).Contains(name)
            && !(virtualState && name == DefaultFirstPlayerName))
            throw new InvalidOperationException($"玩家「{name}」已存在，请换一个名字");

        if (virtualState)
        {
            // 首位显式玩家承接虚拟玩家1：先认领旧通用备份（这样零变化检查才有基准），
            // 再把当前进度打包归他并立为心目中的档；无存档/无变化则不产生 zip（无变化时指针落认领来的最新包）
            try { BackupService.AdoptBackupsForPlayer(g, name); } catch { }
            try { BackupAsFavorite(g, "初始备份", hot: false, "init", out _, profile: name); }
            catch (InvalidOperationException ex)
                when (ex.Message.Contains("没有变化") || ex.Message.Contains("尚未产生存档")) { }
        }
        else if (!string.IsNullOrEmpty(g.ActiveProfile))
        {
            // 常规：先把当前玩家进度保住（等同切走：live 命中已有备份则只移指针不打重复 zip）
            AnchorOnLeave(g, g.ActiveProfile);
        }

        RegisterSlot(g, name);
        g.ActiveProfile = name;
        WriteGameMarker(g);   // 显式玩家实体化：记录激活态（虚拟玩家1不写，他没人命名）
        Store.Save();
    }

    /// <summary>切换到指定玩家：当前进度先打包给当前玩家（zip 唯一事实源；打包失败绝不动 live，进度不丢），
    /// 再把目标玩家最新备份还原覆盖 live（严格清理：目标快照没有的新文件一并删除）。</summary>
    public static void SwitchTo(GameEntry g, string name)
    {
        EnsureUsable(g);
        if (!ListSlots(g).Contains(name))
            throw new InvalidOperationException($"玩家「{name}」不存在");

        // 1) 离开当前玩家：给她的 live 落切换锚点（zip 唯一事实源；打包失败绝不动 live，进度不丢）
        if (!string.IsNullOrEmpty(g.ActiveProfile))
        {
            // 零备份玩家：她若没玩过（live 与切入时恢复的基线一致），不打包——
            // 否则会把上一任的进度错误归到她名下；玩过则基线不匹配，正常打包保进度
            bool leavingZeroBackup = !g.Backups.Any(b => b.Profile == g.ActiveProfile);
            bool skipAsUntouched = leavingZeroBackup && BackupService.LiveMatchesBaseline(g);
            if (!skipAsUntouched)
                AnchorOnLeave(g, g.ActiveProfile);
        }

        // 2) 目标玩家的锚点包覆盖 live（无锚点/锚点失效→最新一份链路完整的备份；
        //    Restore 内部有"还原前安全快照"兜底）。
        //    云端增量包已下载、全量基础包还没到齐时跳过它选更早的完整备份，而不是让还原炸链。
        //    落盘路径重映射到本机当前存档目录：本机 zip 的 manifest.Paths 本就是本机路径，
        //    云端下载的 zip 记的是源机路径，必须落到本机目录才能生效。
        var target = PickSwitchTarget(g, name);
        if (target == null)
            throw new InvalidOperationException(
                $"玩家「{name}」名下还没有可还原的完整备份（云端基础包可能尚未下载完成，请稍后再试）");
        // safetySnapshot=false：切走方的 live 已在上面 AnchorOnLeave 锚定，
        // Restore 内再打安全快照只会制造内容重复的冗余 zip（锚点命中时尤甚）
        BackupService.Restore(g, target, purgeNewFiles: true,
            targetPaths: EffectivePaths(g), safetySnapshot: false, markFavorite: false);

        g.ActiveProfile = name;
        // 切换不产生新的"认可动作"：Restore 已按 markFavorite:false 调用，切入后指针保持目标玩家原值
        WriteGameMarker(g);
        Store.Save();
    }

    /// <summary>以"认可动作"名义备份：成功→心目中的指针移到新包；报「没有变化」→ live 未变，
    /// 指针已在承载这份 live 的包上时什么都不改（保留她原来的认可原因，例如刚手动备份后退出游戏
    /// 无变化，不该被改写成 auto）；指针缺失或指向别处时才兜底移到最新可还原包；随后原异常照常抛出。
    /// why：manual=手动备份/热键，auto=退出自动备份，init=首位玩家初始承接。</summary>
    public static BackupRecord BackupAsFavorite(GameEntry g, string note, bool hot, string why,
        out int skippedFiles, string? profile = null)
    {
        string player = profile ?? g.ActiveProfile ?? "";
        try
        {
            var rec = BackupService.Create(g, note, hot, out skippedFiles, profile: profile);
            MarkFavorite(g, rec.Profile ?? player, rec.FileJson, why);
            return rec;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("没有变化"))
        {
            skippedFiles = 0;
            var latest = g.Backups
                .Where(b => (b.Profile ?? "") == player)
                .OrderByDescending(b => b.Time)
                .FirstOrDefault(b => BackupService.IsRestorable(g, b));
            if (latest != null)
            {
                string? curFav = g.SwitchAnchors != null
                    && g.SwitchAnchors.TryGetValue(player, out var cf) ? cf : null;
                bool alreadyHere = !string.IsNullOrEmpty(curFav)
                    && string.Equals(BackupRecord.ResolveFile(curFav!),
                                     BackupRecord.ResolveFile(latest.FileJson),
                                     StringComparison.OrdinalIgnoreCase);
                // 指针已落在这份包上：无新认可动作，保留原 why；悬空/指别处才兜底落指针
                if (!alreadyHere) MarkFavorite(g, player, latest.FileJson, why);
            }
            throw;
        }
    }

    /// <summary>切走玩家时把她的当前 live 锚定到一份备份（= 移动她的心目中指针，why=switch）：
    /// live 与名下某份已有备份内容完全一致（刚手动还原旧档的典型场景）→ 只记指针、不打内容重复的 zip；
    /// 否则打一份「切换玩家」新备份并指向它；报「没有变化」→ 指向她最新的可还原包（与旧回退口径一致）。</summary>
    public static void AnchorOnLeave(GameEntry g, string leaving)
    {
        var matched = BackupService.FindSnapshotMatchingLive(g, leaving);
        if (matched != null)
        {
            MarkFavorite(g, leaving, matched.FileJson, "switch");
            return;
        }
        try
        {
            var rec = BackupService.Create(g, "切换玩家");
            MarkFavorite(g, leaving, rec.FileJson, "switch");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("没有变化"))
        {
            var latest = g.Backups
                .Where(b => (b.Profile ?? "") == leaving)
                .OrderByDescending(b => b.Time)
                .FirstOrDefault(b => BackupService.IsRestorable(g, b));
            if (latest != null) MarkFavorite(g, leaving, latest.FileJson, "switch");
        }
    }

    /// <summary>解析玩家当前"心目中的存档"：指针在库且链路完整→它；指针缺失/指向的包被删或损坏
    /// →回退她最新的可还原完整包（why 输出 fallback，文案不再按还原/备份口径细分）；
    /// 一份都没有→null。切回玩家与筹码 live 核对共用此解析。</summary>
    public static BackupRecord? ResolveFavorite(GameEntry g, string name, out string why)
    {
        name ??= "";
        why = g.FavoriteWhy?.TryGetValue(name, out var w) == true ? w : "";
        var mine = g.Backups.Where(b => (b.Profile ?? "") == name).ToList();
        if (g.SwitchAnchors != null
            && g.SwitchAnchors.TryGetValue(name, out var favFile)
            && !string.IsNullOrEmpty(favFile))
        {
            var fav = mine.FirstOrDefault(b =>
                string.Equals(b.FileJson, favFile, StringComparison.OrdinalIgnoreCase));
            if (fav != null && BackupService.IsRestorable(g, fav)) return fav;
        }
        var latest = mine.OrderByDescending(b => b.Time).FirstOrDefault(b => BackupService.IsRestorable(g, b));
        if (latest != null && string.IsNullOrEmpty(why)) why = "fallback";
        return latest;
    }

    /// <summary>选切入玩家要还原的备份 = 她当前心目中的存档（指针优先，失效回退最新可还原包）。</summary>
    private static BackupRecord? PickSwitchTarget(GameEntry g, string name)
        => ResolveFavorite(g, name, out _);

    /// <summary>移动"心目中的存档"指针：记录玩家认可的备份（FileJson 相对口径）与认可原因。</summary>
    public static void MarkFavorite(GameEntry g, string player, string fileJson, string why)
    {
        player ??= "";
        g.SwitchAnchors ??= new Dictionary<string, string>(StringComparer.Ordinal);
        g.SwitchAnchors[player] = fileJson;
        g.FavoriteWhy ??= new Dictionary<string, string>(StringComparer.Ordinal);
        g.FavoriteWhy[player] = why ?? "";
    }

    /// <summary>重命名玩家：改卡槽名单、备份归属与玩家备份子目录、同步激活态。</summary>
    public static void RenamePlayer(GameEntry g, string oldName, string newName)
    {
        newName = (newName ?? "").Trim();
        ValidateName(newName);
        if (!ListSlots(g).Contains(oldName))
            throw new InvalidOperationException($"玩家「{oldName}」不存在");
        if (ListSlots(g).Contains(newName))
            throw new InvalidOperationException($"玩家「{newName}」已存在，请换一个名字");

        // 虚拟玩家1改名：他没有备份、不在名单——以新名实体化（显式改名=真实卡槽，写 game.json）
        if (IsDefaultPlayerApplicable(g) && oldName == DefaultFirstPlayerName)
        {
            RegisterSlot(g, newName);
            g.ActiveProfile = newName;
            WriteGameMarker(g);
            Store.Save();
            return;
        }

        BackupService.RenamePlayerBackups(g, oldName, newName);
        // 卡槽名单原位改名（保持创建顺序）
        if (g.Players != null)
        {
            int idx = g.Players.FindIndex(n => string.Equals(n, oldName, StringComparison.Ordinal));
            if (idx >= 0) g.Players[idx] = newName;
        }
        if (g.ActiveProfile == oldName) g.ActiveProfile = newName;
        // 心目中的指针随玩家改名：备份目录已移动，按 zip 文件名把指针值重映射到新记录；原因随之搬运
        if (g.SwitchAnchors != null && g.SwitchAnchors.TryGetValue(oldName, out var anchorRel))
        {
            g.SwitchAnchors.Remove(oldName);
            string why = g.FavoriteWhy?.TryGetValue(oldName, out var w) == true ? w : "";
            g.FavoriteWhy?.Remove(oldName);
            string leaf = Path.GetFileName(anchorRel);
            var remapped = g.Backups
                .Where(b => (b.Profile ?? "") == newName)
                .FirstOrDefault(b => string.Equals(Path.GetFileName(b.FileJson), leaf, StringComparison.OrdinalIgnoreCase));
            if (remapped != null) MarkFavorite(g, newName, remapped.FileJson, why);
        }
        else
        {
            g.FavoriteWhy?.Remove(oldName);
        }
        WriteGameMarker(g);
        Store.Save();
    }

    /// <summary>删除玩家（唯一能移除卡槽的入口）：删除其全部备份 zip 与记录、从卡槽名单移除
    /// （不碰其他玩家；删的是当前玩家则回到"未启用"态）。返回删除的备份份数。</summary>
    public static int DeletePlayer(GameEntry g, string name)
    {
        // 虚拟玩家1不能删：删掉后 ListSlots 又会虚拟出同样的卡槽。引导用户改名或新建玩家
        if (IsDefaultPlayerApplicable(g) && name == DefaultFirstPlayerName)
            throw new InvalidOperationException(
                $"「{DefaultFirstPlayerName}」是默认玩家，不能删除；可右键玩家名字改名，或点「＋ 新玩家」后切换");
        int backups = BackupService.DeletePlayerBackups(g, name);
        g.Players?.RemoveAll(n => string.Equals(n, name, StringComparison.Ordinal));
        g.SwitchAnchors?.Remove(name);
        g.FavoriteWhy?.Remove(name);
        if (g.ActiveProfile == name) g.ActiveProfile = "";
        // 还有其他真实玩家：刷新激活标记；删光了：游戏回到虚拟玩家1态——清掉 marker，
        // 磁盘不留空标记（RootFor 下若有镜像等其他内容则保留目录、只删 game.json）
        if (g.Players != null && g.Players.Count > 0) WriteGameMarker(g);
        else TryClearRootMarker(g);
        Store.Save();
        return backups;
    }

    /// <summary>删除 RootFor 下的 game.json；目录随之清空则连目录一起删除（不存在则 no-op）。</summary>
    private static void TryClearRootMarker(GameEntry g)
    {
        try
        {
            string root = RootFor(g);
            if (!Directory.Exists(root)) return;
            string mf = Path.Combine(root, RootMarkerFile);
            if (File.Exists(mf)) File.Delete(mf);
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch { }
    }

    // ── 启动迁移（均幂等）────────────────────────────────

    /// <summary>启动迁移：旧版玩家镜像根 backups\profiles → backups\_appdata\profiles（一次性，幂等）。
    /// 逐个 GUID 目录整体移动（含 game.json 身份证）；目标已存在同名根时逐文件合并。</summary>
    public static void MigrateLegacyProfileRoot()
    {
        try
        {
            string oldRoot = Path.Combine(Store.BackupRoot, "profiles");
            if (!Directory.Exists(oldRoot)) return;
            if (oldRoot.Equals(ProfilesRoot, StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(ProfilesRoot);
            foreach (var d in Directory.GetDirectories(oldRoot))
            {
                string target = Path.Combine(ProfilesRoot, Path.GetFileName(d));
                try
                {
                    if (Directory.Exists(target)) MergeMove(d, target);
                    else Directory.Move(d, target);
                }
                catch { /* 占用则留待下次启动再迁 */ }
            }
            foreach (var f in Directory.GetFiles(oldRoot))
            {
                try { File.Move(f, Path.Combine(ProfilesRoot, Path.GetFileName(f)), overwrite: true); }
                catch { }
            }
            try { if (!Directory.EnumerateFileSystemEntries(oldRoot).Any()) Directory.Delete(oldRoot); } catch { }
        }
        catch { }
    }

    /// <summary>启动迁移：持久化玩家卡槽名单回填（幂等）。
    /// 旧版 games.json 没有 Players 字段——从备份记录 Profile 去重（按最早备份时间排序）补齐；
    /// 名单中已有但备份已删光的玩家保留（这正是卡槽独立于备份的语义）；清理空白/重复项。</summary>
    public static void BackfillPlayers()
    {
        bool dirty = false;
        foreach (var g in Store.Games)
        {
            try
            {
                g.Players ??= new List<string>();
                int before = g.Players.Count;
                var have = new HashSet<string>(g.Players, StringComparer.Ordinal);
                // 备份里有、名单里没有的：按创建顺序（最早备份时间）追加
                foreach (var n in SlotsFromBackups(g))
                    if (have.Add(n)) g.Players.Add(n);
                // 清理空白与重复（保序）
                var cleaned = g.Players
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (cleaned.Count != g.Players.Count) g.Players = cleaned;
                if (g.Players.Count != before) dirty = true;
            }
            catch { }
        }
        if (dirty) Store.Save();
    }

    /// <summary>启动迁移：为每个玩家建立"心目中的存档"指针，并清空已退役的 RestoredMarks（幂等）。
    /// · 指针已有效（在库、链路完整）→ 保留；缺原因登记则补 ""（不覆盖已知原因）；
    /// · 有旧版还原标记：在她现存可还原备份中找存档时间 ±2 秒内、打包最新的一份 → 原因 restore；
    ///   找不到不建指针（旧档包已不在，绝不用最新包冒充她的选择）；
    /// · 无标记且无有效指针：指针=最新可还原包、原因 ""（纯兜底，让筹码立刻有比较基准）。
    /// 共用存档（玩家名 ""）同样处理；全部处理完清空该游戏的 RestoredMarks。</summary>
    public static void BackfillFavorite()
    {
        bool dirty = false;
        foreach (var g in Store.Games)
        {
            try
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var n in g.Players ?? new List<string>())
                    if (!string.IsNullOrEmpty(n)) names.Add(n);
                foreach (var b in g.Backups) names.Add(b.Profile ?? "");
                if (!string.IsNullOrEmpty(g.ActiveProfile)) names.Add(g.ActiveProfile);
                if (g.RestoredMarks != null)
                    foreach (var k in g.RestoredMarks.Keys) names.Add(k ?? "");

                foreach (var name in names)
                {
                    var mine = g.Backups.Where(b => (b.Profile ?? "") == name).ToList();
                    bool validPointer = g.SwitchAnchors != null
                        && g.SwitchAnchors.TryGetValue(name, out var ptr)
                        && !string.IsNullOrEmpty(ptr)
                        && mine.Any(b => string.Equals(b.FileJson, ptr, StringComparison.OrdinalIgnoreCase)
                                        && BackupService.IsRestorable(g, b));
                    if (validPointer)
                    {
                        if (g.FavoriteWhy == null || !g.FavoriteWhy.ContainsKey(name))
                        {
                            g.FavoriteWhy ??= new Dictionary<string, string>(StringComparer.Ordinal);
                            g.FavoriteWhy[name] = "";   // 上一版本写入的锚点：原因未知，按"备份一致"口径展示
                            dirty = true;
                        }
                    }
                    else if (g.RestoredMarks != null
                             && g.RestoredMarks.TryGetValue(name, out var restoredAt)
                             && restoredAt != default)
                    {
                        long target = restoredAt.ToUniversalTime().Ticks;
                        var hit = mine.Where(b => BackupService.IsRestorable(g, b)
                                                  && b.SavedAt != DateTime.MinValue)
                            .Where(b => Math.Abs(b.SavedAt.ToUniversalTime().Ticks - target)
                                        <= BackupService.LiveTimeToleranceTicks)
                            .OrderByDescending(b => b.Time)
                            .FirstOrDefault();
                        if (hit != null) { MarkFavorite(g, name, hit.FileJson, "restore"); dirty = true; }
                        else { g.SwitchAnchors?.Remove(name); g.FavoriteWhy?.Remove(name); }
                    }
                    else
                    {
                        var latest = mine.OrderByDescending(b => b.Time)
                            .FirstOrDefault(b => BackupService.IsRestorable(g, b));
                        if (latest != null) { MarkFavorite(g, name, latest.FileJson, ""); dirty = true; }
                    }
                }

                if (g.RestoredMarks != null && g.RestoredMarks.Count > 0)
                {
                    g.RestoredMarks.Clear();
                    dirty = true;
                }
            }
            catch { }
        }
        if (dirty) Store.Save();
    }

    /// <summary>启动迁移：旧版玩家镜像（profiles\{GUID}\{玩家}\pN）补打成该玩家的 zip，
    /// 成功（或最新备份已覆盖该内容）后删除镜像；任何失败保留镜像，下次启动重试。幂等。
    /// 返回失败清单（"游戏 / 玩家：原因"），调用方自行记日志。</summary>
    public static List<string> MigrateMirrorBackups()
    {
        var failures = new List<string>();
        foreach (var g in Store.Games.ToList())
            failures.AddRange(MigrateGameMirrors(g));
        return failures;
    }

    /// <summary>迁移单个游戏下的全部旧版玩家镜像（重新入库找回时也会调用，把回迁的镜像立刻补打成 zip）。</summary>
    private static List<string> MigrateGameMirrors(GameEntry g)
    {
        var failures = new List<string>();
        try
        {
            string root = RootFor(g);
            if (!Directory.Exists(root)) return failures;
            foreach (var slotDir in Directory.GetDirectories(root).ToList())
            {
                string name = Path.GetFileName(slotDir);
                try { MigrateOneSlot(g, name, slotDir); }
                catch (Exception ex) { failures.Add($"{g.DisplayName} / {name}：{ex.Message}"); }
            }
        }
        catch (Exception ex) { failures.Add($"{g.DisplayName}：{ex.Message}"); }
        return failures;
    }

    /// <summary>迁移单个玩家镜像：pN 子目录打包为该玩家的 zip（manifest.Paths 记当年真实存档路径），
    /// 完成后删除整个镜像目录。镜像里没有任何 pN（空壳玩家）直接清理。</summary>
    private static void MigrateOneSlot(GameEntry g, string name, string slotDir)
    {
        var pnDirs = Directory.GetDirectories(slotDir)
            .Where(d =>
            {
                string n = Path.GetFileName(d);
                return n.Length > 1 && n[0] == 'p' && int.TryParse(n[1..], out _);
            })
            .OrderBy(d => int.Parse(Path.GetFileName(d)[1..]))
            .ToList();
        if (pnDirs.Count == 0)
        {
            try { Directory.Delete(slotDir, true); }   // 空壳玩家：无数据可保
            catch { }
            return;
        }
        // slot.json.Paths 记录当年 pN 对应的真实存档路径——还原时条目必须落回这些路径
        var slotPaths = ReadSlotManifest(slotDir)?.Paths;
        if (slotPaths == null || slotPaths.Count == 0)
            throw new InvalidOperationException("镜像清单（slot.json）缺失或损坏");
        if (pnDirs.Count > slotPaths.Count)
            throw new InvalidOperationException("镜像目录多于路径表，为防数据丢失保留待查");

        // pN 与路径表按下标一一对应；缺失的 pN（历史遗留）当作空目录
        var scan = new List<string>();
        for (int i = 0; i < slotPaths.Count; i++)
        {
            string pdir = Path.Combine(slotDir, $"p{i}");
            scan.Add(Directory.Exists(pdir) ? pdir : "");
        }
        try
        {
            BackupService.Create(g, "镜像迁移", hot: false, out _,
                profile: name, scanDirs: scan, manifestPaths: slotPaths);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("没有变化"))
        {
            // 该玩家最新 zip 已覆盖镜像内容 → 直接进入删除步骤
        }
        try { Directory.Delete(slotDir, true); }
        catch (Exception ex) { throw new InvalidOperationException("镜像目录删除失败：" + ex.Message); }
    }

    /// <summary>启动时为所有启用了玩家的条目补写/刷新 game.json 身份证（幂等；兼容改名）。
    /// zip 时代玩家数据根里只有这张身份证（旧镜像已在 MigrateMirrorBackups 阶段转成 zip 并清理）。
    /// 从未启用玩家、却残留空壳根的条目才清理掉它。</summary>
    public static void BackfillMarkers()
    {
        foreach (var g in Store.Games)
        {
            try
            {
                string root = RootFor(g);
                if (ListSlots(g).Count > 0)
                {
                    WriteGameMarker(g);   // 根不存在会创建
                    continue;
                }
                if (Directory.Exists(root) && !Directory.GetDirectories(root).Any())
                {
                    TryDeleteMarker(root);
                    try { Directory.Delete(root); } catch { }
                }
            }
            catch { }
        }
    }

    // ── 玩家数据根身份证 game.json ───────────────────────

    /// <summary>把游戏身份写入 profiles\{Id}\game.json（根不存在则创建；吞掉 IO 异常）。
    /// zip 时代该根只存这张身份证：games.json 丢失后重添同名游戏时，靠它把激活玩家恢复回来。</summary>
    public static void WriteGameMarker(GameEntry g)
    {
        try
        {
            string root = RootFor(g);
            var m = new RootMarker
            {
                Id = g.Id,
                NameCN = g.NameCN ?? "",
                NameEN = g.NameEN ?? "",
                DisplayName = g.DisplayName ?? "",
                ActiveProfile = g.ActiveProfile ?? "",
            };
            string json = JsonSerializer.Serialize(m, JsonOpts);
            string marker = Path.Combine(root, RootMarkerFile);
            // 内容与现状逐字相同就完全不碰文件：启动回填（BackfillMarkers）对全部游戏幂等，
            // 避免每次启动无意义覆写——装有文件防护类安全软件（如 360）时，原子替换会把
            // 旧身份证截留进回收站，造成每次启动堆积一批条目。
            if (File.Exists(marker))
            {
                try { if (File.ReadAllText(marker) == json) return; }
                catch { /* 文件被占用/读不出：走下面的原子写翻新 */ }
            }
            Directory.CreateDirectory(root);
            // 原子写：写到一半崩溃会导致 game.json 损坏、重建库时激活玩家无法自动恢复
            Store.AtomicWrite(marker, json);
        }
        catch { }
    }

    /// <summary>读 profiles\{Id}\game.json；损坏/不存在返回 null。供残留列表显示真实游戏名。</summary>
    public static RootMarkerInfo? TryReadRootMarker(string profileRootDir)
    {
        try
        {
            string f = Path.Combine(profileRootDir, RootMarkerFile);
            if (!File.Exists(f)) return null;
            var m = JsonSerializer.Deserialize<RootMarker>(File.ReadAllText(f));
            if (m == null) return null;
            return new RootMarkerInfo(m.Id ?? "", m.NameCN ?? "", m.NameEN ?? "",
                m.DisplayName ?? "", m.ActiveProfile ?? "");
        }
        catch { return null; }
    }

    /// <summary>玩家数据根身份证的只读视图（跨服务使用，字段不可变）。</summary>
    public sealed record RootMarkerInfo(string Id, string NameCN, string NameEN, string DisplayName, string ActiveProfile);

    private static void TryDeleteMarker(string root)
    {
        try { File.Delete(Path.Combine(root, RootMarkerFile)); } catch { }
    }

    // ── 游戏移除 / 重新入库：历史数据脱离 GUID、按游戏名回迁 ──

    /// <summary>脱离时记录激活玩家的标记文件名（位于 backups\{游戏}\_profiles\ 下）。</summary>
    private const string ActiveMarkerFile = "_active.txt";

    /// <summary>移除游戏（选择保留数据）时调用：删除随库删除的 GUID 身份证根
    /// （zip 时代玩家数据只在 backups\{游戏名}\ 下按名存放的 zip 里，天然脱离 GUID）；
    /// 仅当存在旧版残留镜像、或有激活玩家要记录时，才把数据/标记放进 backups\{游戏名}\_profiles\。</summary>
    public static void DetachOnRemoval(GameEntry g)
    {
        string src = RootFor(g);
        string? dst = null;
        if (Directory.Exists(src))
        {
            foreach (var slot in Directory.GetDirectories(src))
            {
                dst ??= Path.Combine(BackupService.BackupDirFor(g), BackupService.DetachedProfilesDir);
                Directory.CreateDirectory(dst);
                string target = Path.Combine(dst, Path.GetFileName(slot));
                try
                {
                    if (Directory.Exists(target)) MergeMove(slot, target);   // 同名玩家：较新状态覆盖
                    else Directory.Move(slot, target);
                }
                catch { /* 个别文件占用则跳过，残留随空目录清理 */ }
            }
            TryDeleteMarker(src);   // 身份证随条目脱离，不残留在即将删除的 GUID 根里
            try { if (!Directory.EnumerateFileSystemEntries(src).Any()) Directory.Delete(src); } catch { }
        }
        // 激活玩家记录在按游戏名存放的标记里，重新入库同名游戏时据此恢复（无激活玩家则不留垃圾）
        if (!string.IsNullOrEmpty(g.ActiveProfile))
        {
            dst ??= Path.Combine(BackupService.BackupDirFor(g), BackupService.DetachedProfilesDir);
            Directory.CreateDirectory(dst);
            try { File.WriteAllText(Path.Combine(dst, ActiveMarkerFile), g.ActiveProfile); } catch { }
        }
    }

    /// <summary>游戏重新入库：把 backups\{游戏名}\_profiles\ 的历史镜像数据回迁到 profiles\{新GUID}\。
    /// 同时处理 games.json 丢失场景：扫描其他无人认领的 GUID 根，凭 game.json 里的游戏名匹配回迁
    /// （zip 时代这些根里通常只剩 game.json 身份证，但激活玩家仍要靠它恢复）。
    /// 返回是否找回了历史数据；activeProfile 为移除前/丢失前正在玩的玩家（可能为空，由调用方校验）。</summary>
    public static bool ReclaimOnAdd(GameEntry g, out string activeProfile)
    {
        activeProfile = "";
        string dst = RootFor(g);
        bool reclaimed = false;
        int movedSlots = 0;

        // 1) 应用内「仅移除」产生的 _profiles 脱离目录（按游戏名目录定位）
        string src = Path.Combine(BackupService.BackupDirFor(g), BackupService.DetachedProfilesDir);
        if (Directory.Exists(src))
        {
            Directory.CreateDirectory(dst);
            foreach (var slot in Directory.GetDirectories(src))
            {
                string target = Path.Combine(dst, Path.GetFileName(slot));
                try
                {
                    if (Directory.Exists(target)) MergeMove(slot, target);
                    else Directory.Move(slot, target);
                    movedSlots++;
                }
                catch { }
            }
            try
            {
                string marker = Path.Combine(src, ActiveMarkerFile);
                if (File.Exists(marker))
                {
                    activeProfile = File.ReadAllText(marker).Trim();
                    File.Delete(marker);
                }
            }
            catch { }
            try { if (!Directory.EnumerateFileSystemEntries(src).Any()) Directory.Delete(src); } catch { }
            if (movedSlots > 0) reclaimed = true;
        }

        // 2) games.json 丢失/损坏后重扫：旧 GUID 根仍在 profiles\ 下，凭 game.json 游戏名候选回迁
        var (markerActive, orphanSlots, markerMatched) = ReclaimOrphanRoots(g, dst);
        if (orphanSlots > 0 || markerMatched) reclaimed = true;
        if (string.IsNullOrEmpty(activeProfile)) activeProfile = markerActive;

        if (reclaimed && Directory.Exists(dst)) WriteGameMarker(g);
        return reclaimed;
    }

    /// <summary>扫描 profiles\ 下不属于任何在库游戏的 GUID 根，game.json 名字候选命中 g 即并入 dst。
    /// 返回 (旧身份证记录的激活玩家, 实际回迁的数据目录数, 是否命中过身份证)；绝不触碰在库其他条目自己的根。</summary>
    private static (string active, int movedSlots, bool markerMatched) ReclaimOrphanRoots(GameEntry g, string dst)
    {
        string profileRoot = ProfilesRoot;
        if (!Directory.Exists(profileRoot)) return ("", 0, false);

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCandidate(candidates, g.DisplayName);
        AddCandidate(candidates, g.NameCN);
        AddCandidate(candidates, g.NameEN);
        if (candidates.Count == 0) return ("", 0, false);

        // 在库条目（含本次的 g）自己的根一律不碰
        var liveIds = Store.Games.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string recoveredActive = "";
        int movedSlots = 0;
        bool markerMatched = false;

        foreach (var root in Directory.EnumerateDirectories(profileRoot))
        {
            string id = Path.GetFileName(root);
            if (liveIds.Contains(id)) continue;
            var info = TryReadRootMarker(root);
            if (info == null) continue;
            var names = new[] { info.DisplayName, info.NameCN, info.NameEN };
            if (!names.Any(n => !string.IsNullOrWhiteSpace(n)
                               && candidates.Contains(BackupService.SafeName(n)))) continue;

            markerMatched = true;   // 即使旧根里只剩身份证（zip 时代常态），也要恢复激活玩家并清根
            Directory.CreateDirectory(dst);
            foreach (var slot in Directory.GetDirectories(root))
            {
                string target = Path.Combine(dst, Path.GetFileName(slot));
                try
                {
                    if (Directory.Exists(target)) MergeMove(slot, target);
                    else Directory.Move(slot, target);
                    movedSlots++;
                }
                catch { }
            }
            if (string.IsNullOrEmpty(recoveredActive)) recoveredActive = info.ActiveProfile;
            TryDeleteMarker(root);
            try { if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root); } catch { }
        }
        return (recoveredActive, movedSlots, markerMatched);
    }

    private static void AddCandidate(HashSet<string> set, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name)) set.Add(BackupService.SafeName(name));
    }

    /// <summary>重新入库找回的组合入口：历史玩家数据回迁 + zip 备份记录重建。
    /// 返回 (找回备份数, 玩家数)；找回数据时恢复移除前的激活玩家。claimedFiles 防同名碰撞重复认领。</summary>
    public static (int backups, int slots) ReclaimAll(GameEntry g, HashSet<string>? claimedFiles = null)
    {
        // 返回值是"本次新找回"的数据量（在库游戏重复调用必须为 0，避免启动 toast 噪声）
        var slotsBefore = new HashSet<string>(ListSlots(g), StringComparer.Ordinal);
        int backupsBefore = g.Backups.Count;

        ReclaimOnAdd(g, out string active);   // 旧版镜像/身份证/脱离标记（zip 时代通常只恢复激活玩家）
        // 先认领游戏名目录下既有的 zip（认领要求条目无备份记录，必须在镜像补打之前）
        int n = BackupService.ReclaimOrphanBackups(g,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), claimedFiles);
        // 回迁进来的旧版玩家镜像补打成 zip（zip 唯一事实源，记录由 Create 直接入库）；
        // 失败保留镜像，启动时 MigrateMirrorBackups 重试
        MigrateGameMirrors(g);
        int migrated = g.Backups.Count - backupsBefore - n;
        if (migrated < 0) migrated = 0;

        var slotsAfter = ListSlots(g);   // 玩家 = 备份记录 Profile 去重（找回的记录已按 manifest.Profile 归属）
        // 找回的备份（孤儿 zip + 镜像迁移新包）归属玩家合并进持久化卡槽名单，防止删光备份后卡槽丢失
        foreach (var s in SlotsFromBackups(g)) RegisterSlot(g, s);
        slotsAfter = ListSlots(g);
        int newSlots = slotsAfter.Count(s => !slotsBefore.Contains(s));
        if (newSlots > 0 || n > 0 || migrated > 0)
        {
            // 激活玩家：优先身份证/脱离标记；缺失时取最新备份归属的玩家（≈移除前最后在玩的人）
            if (string.IsNullOrEmpty(active) || !slotsAfter.Contains(active))
            {
                active = g.Backups
                    .Where(b => !string.IsNullOrEmpty(b.Profile))
                    .OrderByDescending(b => b.Time)
                    .Select(b => b.Profile!).FirstOrDefault() ?? "";
            }
            if (!string.IsNullOrEmpty(active)) g.ActiveProfile = active;
            WriteGameMarker(g);   // 重新入库的条目补身份证（根不存在会创建），供下次丢索引恢复
        }
        return (n + migrated, newSlots);
    }

    /// <summary>合并重复入库条目：把 profiles\{dropId} 下的历史数据并入 profiles\{keepId}
    /// （同名目录逐文件合并），并入后尽力清空 drop 的根。</summary>
    public static void MergeRoots(GameEntry keep, GameEntry drop)
    {
        string src = RootFor(drop);
        if (!Directory.Exists(src)) return;
        string dst = RootFor(keep);
        Directory.CreateDirectory(dst);
        foreach (var slot in Directory.GetDirectories(src))
        {
            string target = Path.Combine(dst, Path.GetFileName(slot));
            try
            {
                if (Directory.Exists(target)) MergeMove(slot, target);
                else Directory.Move(slot, target);
            }
            catch { }
        }
        TryDeleteMarker(src);
        try { if (!Directory.EnumerateFileSystemEntries(src).Any()) Directory.Delete(src); } catch { }
        WriteGameMarker(keep);
    }

    /// <summary>把 src 目录内容合并移动进已存在的 dst（同名目录冲突时逐文件覆盖移动）。</summary>
    private static void MergeMove(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(dst, Path.GetRelativePath(src, f));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // 必须单步原子替换：先删目标再 Move，若 Move 失败（占用/权限）目标会永久丢失。
                File.Move(f, target, overwrite: true);
            }
            catch { }
        }
        try { Directory.Delete(src, true); } catch { }
    }

    // ── 内部 ────────────────────────────────────────────

    private static void EnsureUsable(GameEntry g)
    {
        string? reason = DisabledReason(g);
        if (reason != null) throw new InvalidOperationException(reason);
        if (BackupService.IsGameRunning(g))
            throw new InvalidOperationException($"游戏正在运行，请先退出 {g.DisplayName} 再操作玩家存档");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("玩家名不能为空");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new InvalidOperationException("玩家名含有不能用于文件名的字符");
        if (name.Length > 40)
            throw new InvalidOperationException("玩家名最长 40 个字符");
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            string[] subDirs = Array.Empty<string>();
            try { subDirs = Directory.GetDirectories(cur); } catch { }
            foreach (var d in subDirs) stack.Push(d);
            string[] files = Array.Empty<string>();
            try { files = Directory.GetFiles(cur); } catch { }
            foreach (var f in files) yield return f;
        }
    }

    /// <summary>读旧版镜像的 slot.json（迁移专用；损坏/不存在返回 null）。</summary>
    private static SlotManifest? ReadSlotManifest(string slotDir)
    {
        try
        {
            string f = Path.Combine(slotDir, "slot.json");
            if (!File.Exists(f)) return null;
            return JsonSerializer.Deserialize<SlotManifest>(File.ReadAllText(f));
        }
        catch { return null; }
    }
}
