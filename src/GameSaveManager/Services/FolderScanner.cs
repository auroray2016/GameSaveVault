using System.Diagnostics;
using System.IO;

namespace GameSaveManager.Services;

public record FolderGameCandidate(string Exe, string Dir, string ProductName);

/// <summary>
/// 学习版批量扫描：在根目录的 1–3 层子目录里找「每个目录最大的非工具 exe」作为游戏主程序。
/// 排除卸载器/运行库/各学习组启动器（开始游戏.exe、Ali213 Game Launcher 等），
/// 剔除位于其他候选目录内部的嵌套候选（如 root/Game/Editor）。
/// </summary>
public static class FolderScanner
{
    private static readonly string[] ExeBlacklist =
    {
        "unins", "setup", "install", "update", "patch", "crash", "dxsetup",
        "vcredist", "dxwebsetup", "oalinst", "xnafx", "dotnet", "report", "redist",
    };

    /// <summary>学习版/平台启动器 exe：本身不是游戏，不能作为游戏主程序入库。</summary>
    private static readonly string[] LauncherExeBlacklist =
    {
        "开始游戏", "启动游戏", "ali213", "launcher", "bootstrap", "prelaunch",
    };

    /// <summary>启动器类 ProductName（匹配到则跳过该 exe）。</summary>
    private static readonly string[] LauncherProductBlacklist =
    {
        "ali213", "game launcher", "gamelauncher",
    };

    /// <summary>模拟器/主机工具：不是游戏，扫描时跳过。</summary>
    private static readonly string[] EmulatorBlacklist =
    {
        "ryujinx", "yuzu", "suyu", "rpcs3", "cemu", "dolphin", "pcsx2", "retroarch",
        "xenia", "citra", "ppsspp", "duckstation", "melonds", "desmume", "vita3k",
        "snes9x", "bsnes", "aethersx2",
    };

    /// <summary>文件名是否为学习版/平台启动器 exe（开始游戏.exe、Ali213 Game Launcher 等）。</summary>
    public static bool IsLauncherExe(string fileName) =>
        LauncherExeBlacklist.Any(b => fileName.Contains(b, StringComparison.OrdinalIgnoreCase));

    /// <summary>文件名是否为模拟器/主机工具 exe（Ryujinx、Yuzu 等）。</summary>
    public static bool IsEmulatorExe(string fileName) =>
        EmulatorBlacklist.Any(b => fileName.Contains(b, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] DirBlacklist =
    {
        "redist", "_commonredist", "commonredist", "directx", "dotnet", "unins",
        "crash", "logs", "backup", "__macosx",
    };

    public static List<FolderGameCandidate> Scan(string root, int depth = 3)
    {
        var results = new List<FolderGameCandidate>();
        if (!Directory.Exists(root)) return results;

        var dirs = new List<string> { root };
        CollectDirs(root, 1, depth, dirs);
        foreach (var dir in dirs)
        {
            var best = BestExe(dir);
            if (best != null) results.Add(best);
        }

        // 嵌套剔除：候选目录若位于另一候选目录内部则丢弃（上层目录优先保留）
        var final = new List<FolderGameCandidate>();
        foreach (var c in results.OrderBy(r => r.Dir.Length))
        {
            if (final.Any(f => c.Dir.StartsWith(f.Dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                continue;
            final.Add(c);
        }
        return final;
    }

    private static void CollectDirs(string dir, int level, int maxDepth, List<string> acc)
    {
        if (level > maxDepth) return;
        string[] subs = Array.Empty<string>();
        try { subs = Directory.GetDirectories(dir); } catch { }
        foreach (var d in subs)
        {
            var name = Path.GetFileName(d) ?? "";
            if (DirBlacklist.Any(b => name.Contains(b, StringComparison.OrdinalIgnoreCase))) continue;
            acc.Add(d);
            CollectDirs(d, level + 1, maxDepth, acc);
        }
    }

    private static FolderGameCandidate? BestExe(string dir)
    {
        string[] files = Array.Empty<string>();
        try { files = Directory.GetFiles(dir, "*.exe"); } catch { }
        FolderGameCandidate? best = null;
        long bestSize = 200 * 1024;   // 最小阈值：过滤小工具/加载器
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            if (ExeBlacklist.Any(b => name.Contains(b, StringComparison.OrdinalIgnoreCase))) continue;
            if (IsLauncherExe(name)) continue;
            if (IsEmulatorExe(name)) continue;
            long size;
            string product = "";
            try
            {
                size = new FileInfo(f).Length;
                if (size < bestSize) continue;
                var vi = FileVersionInfo.GetVersionInfo(f);
                product = vi.ProductName ?? "";
            }
            catch { continue; }
            // ProductName 是启动器（如 Ali213 Game Launcher）→ 跳过，选同目录真实游戏 exe
            if (!string.IsNullOrWhiteSpace(product) &&
                LauncherProductBlacklist.Any(b => product.ToLowerInvariant().Contains(b)))
                continue;
            bestSize = size;
            best = new FolderGameCandidate(f, dir,
                string.IsNullOrWhiteSpace(product) ? Path.GetFileNameWithoutExtension(f) : product.Trim());
        }
        return best;
    }

    /// <summary>手动指定单个 exe → 候选（名称取版本资源 ProductName，缺失用文件名）。
    /// 用户显式指定，不做大小/启动器黑名单过滤。</summary>
    public static FolderGameCandidate CandidateFromExe(string exe)
    {
        string product = "";
        try { product = FileVersionInfo.GetVersionInfo(exe).ProductName ?? ""; }
        catch { }
        return new FolderGameCandidate(exe,
            Path.GetDirectoryName(exe) ?? "",
            string.IsNullOrWhiteSpace(product) ? Path.GetFileNameWithoutExtension(exe) : product.Trim());
    }
}
