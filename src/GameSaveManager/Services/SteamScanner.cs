using Microsoft.Win32;
using System.IO;
using GameSaveManager.Models;

namespace GameSaveManager.Services;

public class SteamGameInfo
{
    public int AppId;
    public string Name = "";
    public string InstallDir = "";
    public string ExePath = "";
    public DateTime LastPlayed;
}

public static class SteamScanner
{
    /// <summary>通过注册表定位 Steam 安装目录。</summary>
    public static string? FindSteamPath()
    {
        foreach (var (root, key) in new[]
        {
            (RegistryHive.CurrentUser, @"Software\Valve\Steam"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam"),
        })
        {
            try
            {
                using var k = RegistryKey.OpenBaseKey(root, RegistryView.Registry64).OpenSubKey(key);
                var v = (k?.GetValue("SteamPath") ?? k?.GetValue("InstallPath")) as string;
                if (!string.IsNullOrEmpty(v))
                    return v.Replace('/', '\\');
            }
            catch { }
        }
        return null;
    }

    /// <summary>解析 libraryfolders.vdf 获取所有游戏库目录。</summary>
    public static List<string> GetLibraries(string steamPath)
    {
        var result = new List<string>();
        string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdfPath))
            {
                var root = Vdf.ParseFile(vdfPath);
                if (root["libraryfolders"] is Dictionary<string, object> folders)
                {
                    foreach (var item in folders.Values.OfType<Dictionary<string, object>>())
                    {
                        var p = Vdf.GetStr(item, "path");
                        if (!string.IsNullOrEmpty(p)) result.Add(p);
                    }
                }
            }
        }
        catch { }
        if (!result.Contains(steamPath)) result.Insert(0, steamPath);
        return result;
    }

    /// <summary>扫描所有已安装 Steam 游戏（appmanifest_*.acf）。</summary>
    public static List<SteamGameInfo> ScanInstalled(string steamPath)
    {
        var games = new List<SteamGameInfo>();
        foreach (var lib in GetLibraries(steamPath))
        {
            string appsDir = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(appsDir)) continue;
            foreach (var manifest in Directory.GetFiles(appsDir, "appmanifest_*.acf"))
            {
                try
                {
                    var root = Vdf.ParseFile(manifest);
                    if (!root.TryGetValue("AppState", out var s) || s is not Dictionary<string, object> app)
                        continue;
                    var info = new SteamGameInfo
                    {
                        AppId = int.TryParse(Vdf.GetStr(app, "appid"), out var id) ? id : 0,
                        Name = Vdf.GetStr(app, "name"),
                        LastPlayed = long.TryParse(Vdf.GetStr(app, "LastPlayed"), out var ep) && ep > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(ep).LocalDateTime : DateTime.MinValue,
                    };
                    info.InstallDir = Path.Combine(lib, "steamapps", "common", Vdf.GetStr(app, "installdir"));
                    if (info.AppId > 0 && Directory.Exists(info.InstallDir))
                    {
                        info.ExePath = GuessMainExe(info.InstallDir);
                        games.Add(info);
                    }
                }
                catch { }
            }
        }
        return games.DistinctBy(g => g.AppId).ToList();
    }

    /// <summary>猜测主程序 exe：优先取目录里体积最大的 exe（两层内）。</summary>
    public static string GuessMainExe(string installDir)
    {
        try
        {
            var blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "uninstall", "UnityCrashHandler64", "UnityCrashHandler32", "crashhandler64",
                "dxsetup", "dxwebsetup", "vcredist", "dotNetFx", "oalinst", "Launcher"
            };
            var candidates = new List<(string path, long size)>();
            foreach (var depth1 in new[] { installDir }.Concat(SafeSubDirs(installDir, 1)))
            {
                foreach (var f in SafeFiles(depth1))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (blacklist.Any(b => name.StartsWith(b, StringComparison.OrdinalIgnoreCase))) continue;
                    if (name.EndsWith("Setup", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith("Redist", StringComparison.OrdinalIgnoreCase)) continue;
                    try { candidates.Add((f, new FileInfo(f).Length)); } catch { }
                }
            }
            return candidates.OrderByDescending(c => c.size).FirstOrDefault().path ?? "";
        }
        catch { return ""; }
    }

    /// <summary>扫描 userdata/{steamid3}/{appid}/remote —— Steam 云存档目录（正版存档必中点）。</summary>
    public static Dictionary<int, List<string>> ScanUserdataSaves(string steamPath)
    {
        var map = new Dictionary<int, List<string>>();
        string userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) return map;
        try
        {
            // 两层遍历：userdata/{steamid3}/{appid}/remote
            foreach (var userDir in Directory.GetDirectories(userdata))
            {
                foreach (var appDir in SafeSubDirs(userDir, 0))
                {
                    if (!int.TryParse(Path.GetFileName(appDir), out var appId)) continue;
                    string remote = Path.Combine(appDir, "remote");
                    if (!Directory.Exists(remote)) continue;
                    if (!map.TryGetValue(appId, out var list))
                        map[appId] = list = new List<string>();
                    if (!list.Contains(remote)) list.Add(remote);
                }
            }
        }
        catch { }
        return map;
    }

    public static string[] SafeSubDirs(string dir, int depth)
    {
        try { return Directory.GetDirectories(dir); } catch { return Array.Empty<string>(); }
    }

    /// <summary>取第一个数字命名的 userdata 子目录（SteamID3），用于还原 &lt;user-id&gt; 变量。</summary>
    public static string? GetSteamUserId(string steamPath)
    {
        try
        {
            string userdata = Path.Combine(steamPath, "userdata");
            if (!Directory.Exists(userdata)) return null;
            return Directory.GetDirectories(userdata)
                .Select(Path.GetFileName)
                .FirstOrDefault(n => n != null && n.All(char.IsDigit));
        }
        catch { return null; }
    }

    public static string[] SafeFiles(string dir)
    {
        try { return Directory.GetFiles(dir, "*.exe"); } catch { return Array.Empty<string>(); }
    }
}
