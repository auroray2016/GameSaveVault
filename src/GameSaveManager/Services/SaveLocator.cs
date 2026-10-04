using System.Diagnostics;
using System.IO;
using System.Text;
using GameSaveManager.Models;

namespace GameSaveManager.Services;

/// <summary>
/// 存档位置探测引擎（六级漏斗的本地实现）：
/// 1. Steam userdata 云存档   2. Unity 引擎识别(app.info)
/// 3. Unreal 引擎识别         4. Godot 引擎识别
/// 5. 常见目录模板匹配        6. 游戏目录内 Save* 文件夹（学习版绿色存档）
/// </summary>
public static class SaveLocator
{
    public static List<SavePathEntry> Detect(
        string gameName, string exePath, string installDir,
        int appId, Dictionary<int, List<string>>? userdataMap)
    {
        var found = new List<SavePathEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, string source)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!seen.Add(path)) return;
            if (IsSelfOrBackup(path)) return;   // 绝不把本软件数据/备份目录当作存档
            if (!Directory.Exists(path)) return;
            found.Add(new SavePathEntry { Path = path, Source = source });
        }

        // 本软件自身目录与备份根目录：扫到等于把自己备份进备份，必须排除
        static bool IsSelfOrBackup(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                return IsWithin(full, Path.GetFullPath(Store.BaseDir))
                    || IsWithin(full, Path.GetFullPath(Store.BackupRoot));
            }
            catch { return false; }
        }

        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string localLow = Path.Combine(user, "AppData", "LocalLow");

        // ── 1. Steam 云存档（userdata/{steamid3}/{appid}/remote）──
        if (appId > 0 && userdataMap != null && userdataMap.TryGetValue(appId, out var remotes))
            foreach (var r in remotes) Add(r, "Steam云");

        // 名称变体：原始名 / 去副标题(冒号前) / 仅字母数字
        var names = NameVariants(gameName, installDir, exePath);

        // 厂商名（exe 版本信息），用于 My Games\{厂商}\{游戏} 这类两级结构
        string company = "";
        try
        {
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                company = FileVersionInfo.GetVersionInfo(exePath).CompanyName ?? "";
        }
        catch { }

        // 归一化名称（小写、去符号、尾部罗马数字→阿拉伯数字），用于模糊目录匹配
        var normNames = names.Select(NormalizeName)
            .Where(n => n.Length >= 3 && !StopWords.Contains(n)).Distinct().ToList();

        // ── 2. Unity：{exe}_Data/app.info 内明文记录 公司名+产品名 ──
        string exeDir = Path.GetDirectoryName(exePath) ?? installDir;
        string probeDir = Directory.Exists(installDir) ? installDir : exeDir;
        foreach (var dataDir in SteamScanner.SafeSubDirs(probeDir, 0))
        {
            if (!dataDir.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)) continue;
            string appInfo = Path.Combine(dataDir, "app.info");
            if (!File.Exists(appInfo)) continue;
            try
            {
                var lines = File.ReadAllLines(appInfo)
                    .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                if (lines.Count >= 2)
                {
                    string p = Path.Combine(localLow, lines[0], lines[1]);
                    if (Directory.Exists(p)) Add(p, "Unity");
                }
            }
            catch { }
            break;
        }

        // ── 3. Unreal：LOCALAPPDATA\{ProjectName}\Saved\SaveGames ──
        foreach (var n in names)
        {
            if (StopWords.Contains(NormalizeName(n))) continue;
            string wosp = new string(n.Where(char.IsLetterOrDigit).ToArray());
            if (wosp.Length < 3) continue;
            string p = Path.Combine(localApp, wosp, "Saved", "SaveGames");
            if (Directory.Exists(p)) Add(p, "Unreal");
            string p2 = Path.Combine(localApp, wosp, "Saved");
            if (Directory.Exists(p2)) Add(p2, "Unreal");
        }

        // ── 4. Godot：APPDATA\Godot\app_userdata\{游戏名} ──
        foreach (var n in names)
        {
            if (StopWords.Contains(NormalizeName(n))) continue;
            string p = Path.Combine(appData, "Godot", "app_userdata", n);
            if (Directory.Exists(p)) Add(p, "Godot");
        }

        // ── 5. 常见目录模板 ──
        foreach (var n in names)
        {
            if (StopWords.Contains(NormalizeName(n))) continue;   // game.exe / start.exe 等通用名不做模板
            Add(Path.Combine(docs, n), "模板");
            Add(Path.Combine(docs, "My Games", n), "模板");
            Add(Path.Combine(user, "Saved Games", n), "模板");
            Add(Path.Combine(appData, n), "模板");
            Add(Path.Combine(localApp, n), "模板");
        }

        // ── 5b. 厂商两级模板：My Games\{厂商}\{游戏名}（如 Runic Games\Torchlight 2）──
        if (!string.IsNullOrWhiteSpace(company))
        {
            foreach (var n in names)
            {
                if (StopWords.Contains(NormalizeName(n))) continue;
                Add(Path.Combine(docs, "My Games", company, n), "厂商");
                Add(Path.Combine(appData, company, n), "厂商");
                Add(Path.Combine(localApp, company, n), "厂商");
                Add(Path.Combine(localLow, company, n), "厂商");
            }
        }

        // ── 5c. 模糊目录匹配（浅层级、白名单根目录）──
        //   解决：罗马数字/阿拉伯数字差异（Torchlight II ↔ Torchlight 2）、
        //         厂商分组目录（My Games\Runic Games\Torchlight 2）、命名含特殊符号
        FuzzyMatchDir(docs, 1, normNames, Add);
        FuzzyMatchDir(Path.Combine(docs, "My Games"), 2, normNames, Add);
        FuzzyMatchDir(Path.Combine(user, "Saved Games"), 2, normNames, Add);
        FuzzyMatchDir(appData, 1, normNames, Add);
        FuzzyMatchDir(appData, 2, normNames, Add);
        FuzzyMatchDir(localApp, 2, normNames, Add);
        FuzzyMatchDir(localLow, 2, normNames, Add);

        // ── 6. 游戏目录内的 Save* 文件夹（学习版绿色存档）──
        try
        {
            foreach (var sub in SteamScanner.SafeSubDirs(probeDir, 0))
            {
                var fn = Path.GetFileName(sub);
                if (fn.Equals("save", StringComparison.OrdinalIgnoreCase) ||
                    fn.Equals("saves", StringComparison.OrdinalIgnoreCase) ||
                    fn.Equals("savegame", StringComparison.OrdinalIgnoreCase) ||
                    fn.Equals("savegames", StringComparison.OrdinalIgnoreCase))
                    Add(sub, "目录内");
            }
        }
        catch { }

        // ── 7. 本地规则库 rules.json（用户可编辑，离线生效）──
        try
        {
            foreach (var p in LocalRules.Match(normNames, installDir))
                Add(p, "规则");
        }
        catch { }

        return found;
    }

    /// <summary>从游戏名/安装目录名/exe产品名生成候选名称变体。</summary>
    public static List<string> NameVariants(string gameName, string installDir, string exePath)
    {
        var set = new List<string>();
        void Push(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            s = s.Trim();
            if (s.Length < 2) return;
            if (!set.Contains(s, StringComparer.OrdinalIgnoreCase)) set.Add(s);
        }

        Push(gameName);
        try
        {
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                var vi = FileVersionInfo.GetVersionInfo(exePath);
                Push(vi.ProductName);
                Push(Path.GetFileNameWithoutExtension(exePath));
            }
        }
        catch { }
        if (!string.IsNullOrEmpty(installDir)) Push(Path.GetFileName(installDir.TrimEnd('\\')));

        // 冒号前的主标题（"只狼：影逝二度" → "只狼"）
        int n = set.Count;
        for (int i = 0; i < n; i++)
        {
            var s = set[i];
            int idx = s.IndexOf(':');
            if (idx < 0) idx = s.IndexOf('：');
            if (idx > 1) Push(s.Substring(0, idx).Trim());
        }
        return set;
    }

    // ── 模糊目录匹配 ────────────────────────────────────────

    /// <summary>
    /// 在白名单根目录下做浅层级枚举（不递归全盘），用归一化名称匹配游戏存档目录。
    /// depth=1 只匹配根的直接子目录；depth=2 额外匹配两层（处理 厂商\游戏 分组）。
    /// </summary>
    private static void FuzzyMatchDir(string root, int depth, List<string> normNames,
        Action<string, string> add)
    {
        if (!Directory.Exists(root) || normNames.Count == 0) return;
        try
        {
            foreach (var d1 in SafeDirs(root))
            {
                if (NameMatches(Path.GetFileName(d1), normNames)) add(d1, "模糊匹配");
                if (depth < 2) continue;
                foreach (var d2 in SafeDirs(d1))
                {
                    if (NameMatches(Path.GetFileName(d2), normNames)) add(d2, "模糊匹配");
                }
            }
        }
        catch { }
    }

    private static string[] SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch { return Array.Empty<string>(); }
    }

    private static bool NameMatches(string folderName, List<string> normNames)
    {
        string d = NormalizeName(folderName);
        if (d.Length < 3 || StopWords.Contains(d)) return false;
        foreach (var n in normNames)
        {
            if (d == n) return true;
            // 前缀包含（如 "Torchlight 2 Saves"）：两侧都要求 ≥5 字符且非通用词，
            // 防止 game.exe/start.exe 这类通用启动器名把 game*、start* 目录全部扫中
            if (n.Length >= 5 && d.StartsWith(n)) return true;
            if (d.Length >= 5 && n.StartsWith(d)) return true;
        }
        return false;
    }

    /// <summary>判断 child 是否等于 parent 或位于其目录树内（大小写不敏感）。</summary>
    public static bool IsWithin(string child, string parent)
    {
        if (string.IsNullOrWhiteSpace(child) || string.IsNullOrWhiteSpace(parent)) return false;
        try
        {
            var c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// 清洗旧版误报的自动探测存档路径（game.exe 通用名曾导致 game* 目录被批量扫中）：
    /// ① 本软件数据目录/备份根目录内的路径一律移除；
    /// ② 来源为「模糊匹配/模板/厂商」的条目，按修复后的规则重判，不再命中则移除。
    /// 手动添加、Wiki、注册表及引擎精确规则（Steam云/Unity/Unreal/Godot/目录内/规则）不动。
    /// 返回移除的条目数。
    /// </summary>
    public static int PruneStalePaths(GameEntry g)
    {
        // 判定基准合并中英文双名（宽于探测时的单名输入），宁可漏删不可误删真实存档
        var names = new List<string>();
        names.AddRange(NameVariants(g.NameEN, g.InstallDir, g.ExePath));
        names.AddRange(NameVariants(g.NameCN, g.InstallDir, g.ExePath));
        var normNames = names.Select(NormalizeName)
            .Where(n => n.Length >= 3 && !StopWords.Contains(n)).Distinct().ToList();

        int removed = 0;
        for (int i = g.SavePaths.Count - 1; i >= 0; i--)
        {
            var p = g.SavePaths[i];
            if (p.IsReg || p.Source is "手动" or "Wiki" or "Steam云" or "Unity"
                or "Unreal" or "Godot" or "目录内" or "规则") continue;

            bool stale;
            if (IsWithin(p.Path, Store.BaseDir) || IsWithin(p.Path, Store.BackupRoot))
                stale = true;
            else if (p.Source == "模糊匹配")
                stale = !NameMatches(Path.GetFileName(p.Path.TrimEnd('\\', '/')), normNames);
            else // "模板" / "厂商"：末段必须精确等于某个有效游戏名变体
            {
                string leaf = NormalizeName(Path.GetFileName(p.Path.TrimEnd('\\', '/')));
                stale = StopWords.Contains(leaf) || !normNames.Contains(leaf);
            }

            if (stale)
            {
                var dead = g.SavePaths[i];
                g.SavePaths.RemoveAt(i);
                removed++;
                // 已判定为误报的路径记入忽略名单，防止后台 Wiki/自动探测再次加回
                if (!string.IsNullOrWhiteSpace(dead.Path))
                {
                    string norm = dead.Path.Trim().TrimEnd('\\', '/');
                    if (!g.IgnoredSavePaths.Any(x =>
                            string.Equals(x.Trim().TrimEnd('\\', '/'), norm, StringComparison.OrdinalIgnoreCase)))
                        g.IgnoredSavePaths.Add(norm);
                }
            }
        }
        return removed;
    }

    /// <summary>
    /// 通用启动器/安装包名词表（归一化小写形式）：学习版 exe 常叫 game.exe、start.exe、
    /// 开始游戏.exe 等，这些词不能作为游戏名去匹配目录，否则 AppData 下 game* 目录会全部误中。
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // 英文：通用启动器/安装器/运行库名
        "game", "games", "gamedata", "gameplay", "gameexe", "start", "starter", "startup",
        "startgame", "launch", "launcher", "gamelauncher", "ali213gamelauncher", "setup",
        "install", "installer", "play", "player", "playgame", "client", "steam", "steamclient",
        "unity", "unreal", "engine", "godot", "main", "app", "application", "run", "runner",
        "bin", "data", "exe", "demo", "trial", "crack", "crackfix", "update", "updater",
        "patch", "redist", "redistributable", "runtime", "directx", "vcredist", "dotnet",
        "common", "shared", "public", "default", "temp", "tmp", "cache", "log", "logs",
        "config", "configs", "save", "saves", "savegame", "savegames", "profile", "profiles",
        "user", "users", "files", "folder", "desktop", "documents", "downloads", "video",
        "videos", "screenshot", "screenshots", "music", "picture", "pictures", "backup", "backups",
        // 中文：学习版常见启动器/通用目录名
        "游戏", "开始游戏", "启动游戏", "启动", "开始", "安装", "程序", "游戏启动器",
        "中文版", "汉化版", "学习版", "绿色版", "补丁", "破解", "存档", "游戏存档", "备份",
    };

    /// <summary>
    /// 名称归一化：小写 + 尾部罗马数字转阿拉伯数字（要求数字前是分隔符，避免
    /// 把 "civ"、"matrix" 这类单词误转）+ 仅保留字母/数字/中文等字符。
    /// "Torchlight II" → "torchlight2"，与文件夹 "Torchlight 2" → "torchlight2" 对齐。
    /// </summary>
    public static string NormalizeName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        string lower = s.ToLowerInvariant();
        var m = RomanTailRegex.Match(lower);
        if (m.Success)
            lower = lower[..m.Groups[1].Index] + RomanMap[m.Groups[1].Value];
        var sb = new StringBuilder();
        foreach (char c in lower)
        {
            // 保留字母、数字、中文/日文等非 ASCII 字符
            if (char.IsLetterOrDigit(c) || (int)c > 127) sb.Append(c);
        }
        return sb.ToString();
    }

    // 分组1 = 数字前的分隔/起始位置，分组2 = 罗马数字（长的先匹配）
    private static readonly System.Text.RegularExpressions.Regex RomanTailRegex =
        new(@"(?<![a-z])(viii|vii|iii|ii|ix|iv|vi|x|v|i)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly Dictionary<string, string> RomanMap = new()
    {
        ["viii"]="8", ["vii"]="7", ["iii"]="3", ["ii"]="2", ["ix"]="9",
        ["iv"]="4", ["vi"]="6", ["x"]="10", ["v"]="5", ["i"]="1",
    };
}
