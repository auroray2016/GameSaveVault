using System.IO;
using GameSaveManager.Models;

namespace GameSaveManager.Services;

/// <summary>
/// 启动时一次性数据自修复（幂等，可重复执行）：
/// ① 学习版启动器 exe（开始游戏.exe / Ali213 Game Launcher 等）被当成游戏主程序的条目
///    → 在安装目录内重新定位真实游戏 exe，清空旧存档路径以便按正确名字重新探测；
/// ② 模拟器/主机工具（Ryujinx、Yuzu 等）误入库 → 从库中移除（磁盘备份保留）；
/// ③ 个别内部代号 exe（如 ZingangGame = 暗影火炬城）直接定名。
/// </summary>
public static class DataMigration
{
    /// <summary>exe 文件主名（小写）→ 直接定名（中文名, 英文名, 是否重置错误 appid/封面, 钉死的 appid）。
    /// 用于无版本信息、联网也难匹配的代号 exe；ResetId=true 用于曾被误配成其他游戏（如曲包/同名初代 appid）
    /// 的条目（每次启动重复断言，防止联网回填再次误配）；PinAppId&gt;0 时锁定为该 appid，回填不得覆盖。</summary>
    private static readonly Dictionary<string, (string Cn, string En, bool ResetId, int PinAppId)> CodeNames =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["zinganggame"] = ("暗影火炬城", "F.I.S.T.: Forged In Shadow Torch", false, 0),
        ["hman10"] = ("大富翁10", "Richman 10", false, 0),
        // Alan Wake 2 PC 端 Epic 独占，Steam 无此 appid；曾被误配为 Beat Saber 曲包（3274290）+ 错误封面
        ["alanwake2"] = ("心灵杀手2", "Alan Wake 2", true, 0),
        // LOTF2.exe 是 2023 年《堕落之主》（1501750），联网搜索会被同名 2014 初代（265300）精确命中，必须钉死
        ["lotf2"] = ("堕落之主", "Lords of the Fallen (2023)", true, 1501750),
    };

    public static List<string> FixScannedEntries()
    {
        var log = new List<string>();
        var remove = new List<GameEntry>();
        bool changed = false;

        foreach (var g in Store.Games.ToList())
        {
            if (g.IsSteam) continue;
            string exeName = Path.GetFileName(g.ExePath ?? "");

            // ② 模拟器/主机工具误入库
            if (FolderScanner.IsEmulatorExe(exeName))
            {
                remove.Add(g);
                log.Add($"移除模拟器/工具：{g.DisplayName}");
                continue;
            }

            // ① 启动器被当成主程序 → 安装目录内重新定位真实游戏 exe
            if (FolderScanner.IsLauncherExe(exeName) && !string.IsNullOrWhiteSpace(g.InstallDir))
            {
                FolderGameCandidate? best = null;
                try { best = FolderScanner.Scan(g.InstallDir).FirstOrDefault(); }
                catch { /* 目录不可读则跳过 */ }
                if (best != null &&
                    !string.Equals(best.Exe, g.ExePath, StringComparison.OrdinalIgnoreCase))
                {
                    g.ExePath = best.Exe;
                    if (IsLauncherName(g.NameEN) || string.IsNullOrWhiteSpace(g.NameEN))
                        g.NameEN = best.ProductName;   // 真实 exe 多无 ProductName → 回退 exe 名，联网反查再纠正
                    if (g.SavePaths.Count > 0) g.SavePaths.Clear();
                    log.Add($"修正游戏入口：{g.DisplayName} → {Path.GetFileName(best.Exe)}");
                    changed = true;
                }
            }

            // ③ 内部代号 exe 直接定名（在启动器重定位之后再判定，如 开始游戏.exe→hman10.exe=大富翁10）。
            //    ResetId 条目为防联网回填再次误配，每次启动重复断言名称/AppId；
            //    但只有"本次确实纠正了错误数据"时才清存档路径，否则用户手加的路径每次启动都会被抹掉，
            //    且启动流程不会重新探测（自动备份会因此永久找不到存档目录）。
            string exeStem = Path.GetFileNameWithoutExtension(g.ExePath ?? "").ToLowerInvariant();
            if (CodeNames.TryGetValue(exeStem, out var cn))
            {
                bool misIdentified = cn.ResetId
                    ? g.NameCN != cn.Cn || g.NameEN != cn.En || g.AppId != cn.PinAppId
                    : string.IsNullOrWhiteSpace(g.NameCN)
                      || string.IsNullOrWhiteSpace(g.NameEN)
                      || g.NameEN.Trim().Equals(exeStem, StringComparison.OrdinalIgnoreCase)
                      || IsLauncherName(g.NameEN);
                if (misIdentified)
                {
                    g.NameCN = cn.Cn;
                    g.NameEN = cn.En;
                    if (cn.ResetId)
                    {
                        g.AppId = cn.PinAppId;          // PinAppId=0（Epic 独占等无 Steam appid）则留空
                        g.AppIdLocked = cn.PinAppId > 0;
                        g.CoverFile = "";               // 旧封面是误配游戏的图
                    }
                    if (g.SavePaths.Count > 0) g.SavePaths.Clear();   // 旧路径是按错误身份误探的，重新探测
                    log.Add($"定名：{cn.Cn}");
                    changed = true;
                }
            }

            // ④ Steam 搜索失败时写入的占位中文名（"Steam 上的 XXX"）→ 清空，交由联网反查重新获取
            if (!string.IsNullOrEmpty(g.NameCN) && g.NameCN.Contains("Steam 上的"))
            {
                g.NameCN = "";
                log.Add($"清理占位中文名：{g.NameEN}");
                changed = true;
            }
        }

        foreach (var g in remove)
        {
            Store.Games.Remove(g);
            changed = true;
        }
        if (changed) Store.Save();
        return log;
    }

    private static bool IsLauncherName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var n = name.ToLowerInvariant();
        return n.Contains("ali213") || n.Contains("launcher")
            || n.Contains("开始游戏") || n.Contains("bootstrap");
    }
}
