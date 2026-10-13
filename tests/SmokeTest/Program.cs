// 冒烟测试：验证 VDF 解析、Unity/模板存档识别、Steam 扫描、PCGamingWiki 补全（本机真实环境）
global using System.IO;
global using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using GameSaveManager.Models;
using GameSaveManager.Services;
using GameSaveManager;

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"[PASS] {name}"); }
    else { fail++; Console.WriteLine($"[FAIL] {name}  {detail}"); }
}

// ── 1. VDF 解析（含转义路径、嵌套、未引号数字）──
var vdf = Vdf.Parse("""
"libraryfolders"
{
    "0"
    {
        "path"        "D:\\Steam"
        "apps"
        {
            "1245620"    "123456"
            "550"        "789"
        }
    }
    "1"
    {
        "path"        "E:\\SteamLibrary"
        "apps"
        {
        }
    }
}
""");
var folders = Vdf.GetDict(vdf, "libraryfolders");
Check("VDF: 顶层键", folders != null);
var lib0 = Vdf.GetDict(folders!, "0");
Check("VDF: path 转义", Vdf.GetStr(lib0!, "path") == "D:\\Steam", Vdf.GetStr(lib0!, "path"));
var apps = Vdf.GetDict(lib0!, "apps");
Check("VDF: apps 嵌套", apps != null && apps.ContainsKey("1245620"));

// ── 全局数据沙箱（必须早于任何 Store 写盘）──
// 教训：各区块会把 Store.HomeDirOverride 复位为 null 来模拟默认环境，复位窗口里
// 一旦发生 Store.Save/BackupService.Create，就会直接写真实 %APPDATA%\GameSaveVault，
// 曾把用户 91 游戏的 games.json 整个覆写。用环境变量给所有 null 窗口兜底到临时沙箱。
string temp = Path.Combine(Path.GetTempPath(), "gsm_test_" + Guid.NewGuid().ToString("N")[..8]);
string sandboxHome = Path.Combine(temp, "vault_sandbox");
Environment.SetEnvironmentVariable("GSM_VAULT_DIR", sandboxHome);
Check("沙箱: GSM_VAULT_DIR 已兜底，未显式重定向时数据目录落在临时沙箱",
    Store.BaseDir == sandboxHome, Store.BaseDir);
// 真实库指纹：结尾必须原样未动（零污染守卫）
string realVaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameSaveVault");
string realGamesFile = Path.Combine(realVaultDir, "games.json");
DateTime realGamesMtime = File.Exists(realGamesFile) ? File.GetLastWriteTime(realGamesFile) : default;
string realSettingsFile = Path.Combine(realVaultDir, "settings.json");
bool realSettingsExisted = File.Exists(realSettingsFile);
DateTime realSettingsMtime = realSettingsExisted ? File.GetLastWriteTime(realSettingsFile) : default;
string realSettingsHash = realSettingsExisted ? FileFingerprint(realSettingsFile) : "";
// 开测前置：真实 settings.json 若已是"旧版默认壳"事故状态，直接失败——绝不能带毒开测、
// 更不能让任何后续 Store 调用把它当成正常库
Check("沙箱前置: 真实 settings.json 存在且非旧版默认壳（事故状态请先恢复再跑冒烟）",
    !realSettingsExisted || !IsLegacyDefaultSettingsShell(realSettingsFile),
    "真实库 settings.json 为默认壳，疑似设置丢失事故，停止冒烟");
string realBackupRoot = Path.Combine(realVaultDir, "backups");
var realBackupDirsBefore = Directory.Exists(realBackupRoot)
    ? Directory.GetDirectories(realBackupRoot).Select(p => Path.GetFileName(p)!).ToHashSet()
    : new HashSet<string>();
// 真实库根目录的"事故残留"基线：防呆隔离文件 settings.guard.* / 损坏留档 settings.corrupt.*
// （防呆只拦覆写不动 settings.json 指纹，所以要单独盯这类新残留）
string[] RealVaultStrayFiles() => Directory.Exists(realVaultDir)
    ? Directory.GetFiles(realVaultDir)
        .Where(p => p.Contains("settings.guard.", StringComparison.Ordinal)
                 || p.Contains("settings.corrupt.", StringComparison.Ordinal))
        .Select(p => Path.GetFileName(p)!).OrderBy(n => n, StringComparer.Ordinal).ToArray()
    : Array.Empty<string>();
var realStrayBefore = RealVaultStrayFiles().ToHashSet();

// ── 2. Unity 引擎识别（构造临时目录结构）──
string gameDir = Path.Combine(temp, "MyGame");
string dataDir = Path.Combine(gameDir, "MyGame_Data");
Directory.CreateDirectory(dataDir);
File.WriteAllText(Path.Combine(dataDir, "app.info"), "TestStudio\nMyGame\n");
string unitySave = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    "AppData", "LocalLow", "TestStudio", "MyGame");
Directory.CreateDirectory(unitySave);
File.WriteAllText(Path.Combine(unitySave, "save0.sav"), "demo");

var entries = SaveLocator.Detect("MyGame", Path.Combine(gameDir, "MyGame.exe"), gameDir, 0, null);
Check("Unity: app.info 定位 LocalLow", entries.Any(e => e.Path == unitySave),
    string.Join(" | ", entries.Select(e => e.Path)));
Check("Unity: 来源标记", entries.FirstOrDefault(e => e.Path == unitySave)?.Source == "Unity");

// ── 3. 目录内 Save* 文件夹（学习版绿色存档）──
string portableSave = Path.Combine(gameDir, "Save");
Directory.CreateDirectory(portableSave);
var entries2 = SaveLocator.Detect("MyGame", Path.Combine(gameDir, "MyGame.exe"), gameDir, 0, null);
Check("目录内 Save*", entries2.Any(e => e.Path == portableSave));

// ── 4. Steam userdata 模拟 ──
string fakeSteam = Path.Combine(temp, "steam");
string remote = Path.Combine(fakeSteam, "userdata", "103945820", "413150", "remote");
Directory.CreateDirectory(remote);
var map = SteamScanner.ScanUserdataSaves(fakeSteam);
Check("userdata: remote 扫描", map.TryGetValue(413150, out var l) && l[0] == remote);

// ── 5. 名称变体 ──
var names = SaveLocator.NameVariants("只狼：影逝二度", "", "");
Check("名称变体: 冒号拆分", names.Contains("只狼"), string.Join(" | ", names));

// ── 5b. 名称归一化（罗马数字 / 符号）──
Check("归一化: Torchlight II → torchlight2",
    SaveLocator.NormalizeName("Torchlight II") == "torchlight2", SaveLocator.NormalizeName("Torchlight II"));
Check("归一化: Torchlight 2 对齐",
    SaveLocator.NormalizeName("Torchlight 2") == "torchlight2");
Check("归一化: Civilization VI → civilization6",
    SaveLocator.NormalizeName("Sid Meier's Civilization VI") == "sidmeierscivilization6",
    SaveLocator.NormalizeName("Sid Meier's Civilization VI"));
Check("归一化: 单词 civ 不被误转",
    SaveLocator.NormalizeName("Civ") == "civ", SaveLocator.NormalizeName("Civ"));
Check("归一化: matrix 不被误转",
    SaveLocator.NormalizeName("Matrix") == "matrix", SaveLocator.NormalizeName("Matrix"));

// ── 5c. 厂商两级 + 罗马数字模糊匹配（只读：检测真实 My Games 目录，不创建任何文件）──
{
    var tl2Fuzzy = SaveLocator.Detect("Torchlight II",
        Path.Combine(temp, "TorchlightII.exe"), temp, 0, null);
    bool hit = tl2Fuzzy.Any(e => e.Path.Replace('\\', '/').ToLowerInvariant()
        .EndsWith("my games/runic games/torchlight 2/save") ||
        e.Path.Replace('\\', '/').ToLowerInvariant()
        .EndsWith("my games/runic games/torchlight 2"));
    if (Directory.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games")))
        Check("模糊匹配: Runic Games\\Torchlight 2 命中（真实目录）", hit,
            string.Join(" | ", tl2Fuzzy.Select(e => e.Path)));
    else
        Console.WriteLine("[INFO] 无 My Games 目录，跳过模糊匹配只读验证");
}

// ── 7. PCGamingWiki：wikitext 解析（离线，合成数据）──
string wikiSample = """
{{Game data|
{{Game data/saves|Windows|{{p|userprofile\Documents}}\My Games\Runic Games\Torchlight 2\save\}}
{{Game data/saves|OS X|{{p|osxhome}}/Library/Application Support/Runic Games/Torchlight 2/save/}}
{{Game data/config|Windows|{{p|localappdata}}\FooEngine\Config\}}
{{Game data/registry|Windows|{{Note|1}}HKEY_CURRENT_USER\Software\Foo Studio\Foo Game<ref>see notes</ref>}}
}}
""";
var parsed = WikiService.ParseWikitext(wikiSample);
Check("Wiki: 仅提取 Windows 三行（过滤 OS X）", parsed.Count == 3,
    $"count={parsed.Count}: {string.Join(",", parsed.Select(p => p.Type))}");
Check("Wiki: saves 类型", parsed.Any(p => p.Type == "saves" && p.Raw.Contains("Torchlight 2")));
Check("Wiki: registry 类型", parsed.Any(p => p.Type == "registry" && p.Raw.Contains("HKEY_CURRENT_USER")));
Check("Wiki: config 类型", parsed.Any(p => p.Type == "config" && p.Raw.Contains("FooEngine")));

// ── 8. PCGamingWiki：真机端到端（Torchlight II，需联网，失败只告警不判挂）──
try
{
    var fake = new GameEntry
    {
        AppId = 200710,
        NameEN = "Torchlight II",
        Type = "Steam",
        InstallDir = @"d:\steam\steamapps\common\Torchlight II",
    };
    var wikiPaths = await WikiService.EnrichAsync(fake, @"d:\steam", "404004501");
    foreach (var p in wikiPaths)
        Console.WriteLine($"       [Wiki/{(p.IsReg ? "注册表" : p.Source)}] {p.Path}");
    bool tl2Wiki = wikiPaths.Any(p =>
        p.Path.Replace('\\', '/').ToLowerInvariant().Contains("my games/runic games/torchlight 2/save"));
    Check("Wiki: 端到端命中 Torchlight 2 存档", tl2Wiki,
        "若离线会失败，属网络环境限制；解析逻辑已由第 7 组覆盖");

    // 缓存文件应已写入（离线可用）
    Check("Wiki: 缓存落盘", File.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GameSaveVault", "wiki-cache.json")));
}
catch (Exception ex)
{
    Console.WriteLine($"[WARN] Wiki 端到端测试异常（网络？）: {ex.Message}");
}

// ── 9. 本机真实 Steam 扫描（只读，不要求结果）──
var steamPath = SteamScanner.FindSteamPath();
if (steamPath != null)
{
    Console.WriteLine($"[INFO] 本机 Steam: {steamPath}");
    var libs = SteamScanner.GetLibraries(steamPath);
    Console.WriteLine($"[INFO] 游戏库: {string.Join(" | ", libs)}");
    string appsDir = Path.Combine(steamPath, "steamapps");
    var manifests = Directory.GetFiles(appsDir, "appmanifest_*.acf");
    if (manifests.Length > 0)
    {
        var root = Vdf.ParseFile(manifests[0]);
        Console.WriteLine($"[INFO] {Path.GetFileName(manifests[0])} 顶层键: {string.Join(",", root.Keys)}");
        if (root["AppState"] is Dictionary<string, object> app)
        {
            Console.WriteLine($"[INFO] AppState 键数: {app.Count}, appid={Vdf.GetStr(app, "appid")}, installdir={Vdf.GetStr(app, "installdir")}, name={Vdf.GetStr(app, "name")}");
            string testDir = System.IO.Path.Combine(steamPath, "steamapps", "common",
                Vdf.GetStr(app, "installdir"));
            Console.WriteLine($"[INFO] InstallDir 候选: {testDir} 存在={System.IO.Directory.Exists(testDir)}");
        }
        else
        {
            Console.WriteLine($"[INFO] AppState 类型: {root["AppState"]?.GetType().FullName}");
        }
    }
    var games = SteamScanner.ScanInstalled(steamPath);
    Console.WriteLine($"[INFO] 本机 Steam: {steamPath}");
    Console.WriteLine($"[INFO] 已安装 Steam 游戏: {games.Count} 款");
    foreach (var g in games.Take(5))
        Console.WriteLine($"       - {g.AppId} {g.Name}  exe={(g.ExePath.Length > 0 ? Path.GetFileName(g.ExePath) : "?")}");

    // 真实游戏存档定位验证：Torchlight II
    var tl2Real = games.FirstOrDefault(g => g.Name.Contains("Torchlight", StringComparison.OrdinalIgnoreCase));
    if (tl2Real != null)
    {
        var company = "";
        try { company = System.Diagnostics.FileVersionInfo.GetVersionInfo(tl2Real.ExePath).CompanyName ?? ""; } catch { }
        Console.WriteLine($"[INFO] Torchlight II exe 厂商: [{company}]");
        var detected = SaveLocator.Detect(tl2Real.Name, tl2Real.ExePath, tl2Real.InstallDir,
            tl2Real.AppId, SteamScanner.ScanUserdataSaves(steamPath));
        if (detected.Count == 0)
            Console.WriteLine("[INFO] Torchlight II: 未识别到存档位置（本机可能没玩过）");
        foreach (var p in detected)
            Console.WriteLine($"       [{p.Source}] {p.Path}");
    }
    var saves = SteamScanner.ScanUserdataSaves(steamPath);
    Console.WriteLine($"[INFO] userdata 云存档: 覆盖 {saves.Count} 款游戏");
}
else
{
    Console.WriteLine("[INFO] 本机未安装 Steam，跳过真实扫描");
}

// ── 10. 增量备份全流程（数据目录重定向到临时目录）──
{
    // 备份原设置，测试后恢复
    int svMax = Store.Settings.MaxKeepPerGame;
    bool svIncr = Store.Settings.IncrementalEnabled;
    int svN = Store.Settings.FullEveryN;
    string? svHome = Store.HomeDirOverride;

    string vault = Path.Combine(temp, "vault");
    Store.HomeDirOverride = vault;
    Store.Init();
    Check("增量: 出厂默认关闭（Store.Init 新环境）", !Store.Settings.IncrementalEnabled,
        Store.Settings.IncrementalEnabled.ToString());
    Store.Settings.IncrementalEnabled = true;
    Store.Settings.MaxKeepPerGame = 10;
    Store.Settings.FullEveryN = 5;

    System.IO.Compression.ZipArchive OpenZip(BackupRecord r)
    {
        var fs = File.OpenRead(r.File);
        return new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read);
    }
    BackupManifest ManifestOf(BackupRecord r)
    {
        using var zip = OpenZip(r);
        using var rd = new StreamReader(zip.GetEntry("manifest.json")!.Open());
        return System.Text.Json.JsonSerializer.Deserialize<BackupManifest>(rd.ReadToEnd())!;
    }
    List<string> EntryNames(BackupRecord r)
    {
        using var zip = OpenZip(r);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    // 10.1 全量 → 增量
    string saveDir = Path.Combine(temp, "incr_saves");
    Directory.CreateDirectory(Path.Combine(saveDir, "sub"));
    File.WriteAllText(Path.Combine(saveDir, "a.txt"), "A1");
    File.WriteAllText(Path.Combine(saveDir, "b.txt"), "B1");
    File.WriteAllText(Path.Combine(saveDir, "sub", "c.txt"), "C1");

    var gInc = new GameEntry { NameEN = "TestIncr" };
    gInc.SavePaths.Add(new SavePathEntry { Path = saveDir });

    var b1 = BackupService.Create(gInc, "全量");
    Check("增量: 首次为全量", b1.Kind == "full" && b1.BaseFile == "", b1.Kind);
    Check("增量: 全量包含嵌套目录文件", EntryNames(b1).Contains("p0/sub/c.txt"),
        string.Join(",", EntryNames(b1)));

    File.WriteAllText(Path.Combine(saveDir, "a.txt"), "A22");                     // 改（不同长度：mtime 粒度下同长度+同 ticks 会被误判无变化）
    File.WriteAllText(Path.Combine(saveDir, "d.txt"), "D1");                       // 增
    var b2 = BackupService.Create(gInc, "增量1");
    Check("增量: 类型与基础引用", b2.Kind == "incr" &&
        b2.BaseFile == Path.GetFileName(b1.File), $"{b2.Kind}|{b2.BaseFile}");
    var b2Entries = EntryNames(b2);
    Check("增量: 只打包变化文件（a/d，不含 b/c）",
        b2Entries.Contains("p0/a.txt") && b2Entries.Contains("p0/d.txt") &&
        !b2Entries.Contains("p0/b.txt") && !b2Entries.Contains("p0/sub/c.txt"),
        string.Join(",", b2Entries));
    Check("增量: manifest.Files 是快照全集", ManifestOf(b2).Files.Count == 4,
        ManifestOf(b2).Files.Count.ToString());
    Check("统计: 增量打包原始字节统计", b2.OriginalSize > 0 && b2.PackedSize < b2.OriginalSize,
        $"{b2.OriginalSize}/{b2.PackedSize}");
    Check("统计: 备注第二行含存档大小与压缩率、不再显示原始大小",
        b2.SizeDetailText.Contains(b2.SizeText) && b2.SizeDetailText.Contains("压缩率")
        && !b2.SizeDetailText.Contains("原始") && !b2.SizeDetailText.Contains("省"),
        b2.SizeDetailText);
    Check("统计: 全量第二行同样含存档大小",
        b1.OriginalSize > 0 && b1.PackedSize == b1.OriginalSize
        && b1.SizeDetailText.Contains(b1.SizeText) && b1.SizeDetailText.Contains("压缩率"),
        b1.SizeDetailText);
    // 压缩率口径：1 - 压缩包/包内未压缩字节（全量回退 OriginalSize；增量用 PackedSize），异常时 Clamp
    var statFull = new BackupRecord { Kind = "full", OriginalSize = 1000, Size = 300 };
    Check("统计: 全量压缩率=1-压缩包/原始(70%)", statFull.SizeDetailText.Contains("压缩率 70%"),
        statFull.SizeDetailText);
    var statFullBad = new BackupRecord { Kind = "full", OriginalSize = 1000, Size = 1200 };
    Check("统计: 压缩包大于原始时压缩率 Clamp 0%", statFullBad.SizeDetailText.Contains("压缩率 0%"),
        statFullBad.SizeDetailText);
    var statIncr = new BackupRecord { Kind = "incr", OriginalSize = 5000, PackedSize = 1000, Size = 400 };
    Check("统计: 增量按包内字节(PackedSize)算压缩率(60%)",
        statIncr.SizeDetailText.Contains("压缩率 60%") && statIncr.SizeDetailText.StartsWith(statIncr.SizeText),
        statIncr.SizeDetailText);
    var statOld = new BackupRecord { Kind = "full", Size = 2 * 1024 * 1024 };
    Check("统计: 无统计的旧记录第二行只显示存档大小",
        statOld.SizeDetailText == "2.0 MB" && !statOld.SizeDetailText.Contains("压缩率"),
        statOld.SizeDetailText);

    // 10.2 无变化 → 拒绝备份
    bool rejected = false;
    try { BackupService.Create(gInc, "无变化"); }
    catch (InvalidOperationException ex) { rejected = ex.Message.Contains("没有变化"); }
    Check("增量: 无变化时拒绝备份", rejected);

    // 10.3 三级链还原（还原前还会先做安全快照，链更长也要能走通）
    File.WriteAllText(Path.Combine(saveDir, "a.txt"), "A333");                     // 破坏现场
    File.Delete(Path.Combine(saveDir, "d.txt"));
    BackupService.Restore(gInc, b2);
    Check("增量: 链式还原内容正确",
        File.ReadAllText(Path.Combine(saveDir, "a.txt")) == "A22" &&
        File.ReadAllText(Path.Combine(saveDir, "d.txt")) == "D1" &&
        File.ReadAllText(Path.Combine(saveDir, "b.txt")) == "B1");

    File.WriteAllText(Path.Combine(saveDir, "a.txt"), "A4444");
    File.WriteAllText(Path.Combine(saveDir, "sub", "c.txt"), "C22");
    var b3 = BackupService.Create(gInc, "增量2");   // 基础 = 刚插入的安全快照
    File.WriteAllText(Path.Combine(saveDir, "a.txt"), "A55555");
    BackupService.Restore(gInc, b3);
    Check("增量: 跨安全快照的长链还原",
        File.ReadAllText(Path.Combine(saveDir, "a.txt")) == "A4444" &&
        File.ReadAllText(Path.Combine(saveDir, "sub", "c.txt")) == "C22" &&
        File.Exists(Path.Combine(saveDir, "d.txt")));

    // 10.4 删除保护：基础备份被依赖时拒绝删除
    bool blocked = false;
    try { BackupService.Delete(gInc, b1); }
    catch (InvalidOperationException) { blocked = true; }
    Check("增量: 删除基础备份被拒绝", blocked);
    var tip = gInc.Backups[0];        // 还原(b3)产生的安全快照，当前链尾
    BackupService.Delete(gInc, tip);
    BackupService.Delete(gInc, b3);   // 依赖解除后可删
    Check("增量: 链尾删除成功", !gInc.Backups.Contains(b3) && !gInc.Backups.Contains(tip));

    // 10.4b 严格还原：清理快照外新增文件（b1 全量快照只有 a/b/sub\c，没有 d）
    File.WriteAllText(Path.Combine(saveDir, "e.txt"), "E");
    Directory.CreateDirectory(Path.Combine(saveDir, "extra"));
    File.WriteAllText(Path.Combine(saveDir, "extra", "n.txt"), "N");
    BackupService.Restore(gInc, b1, purgeNewFiles: true);
    Check("严格还原: 新文件被清理、旧内容还原",
        !File.Exists(Path.Combine(saveDir, "d.txt")) &&
        !File.Exists(Path.Combine(saveDir, "e.txt")) &&
        !File.Exists(Path.Combine(saveDir, "extra", "n.txt")) &&
        File.ReadAllText(Path.Combine(saveDir, "a.txt")) == "A1" &&
        File.ReadAllText(Path.Combine(saveDir, "sub", "c.txt")) == "C1");
    Check("严格还原: 空目录已清理", !Directory.Exists(Path.Combine(saveDir, "extra")));

    // 10.5 全量周期：每 N 次增量后强制全量
    Store.Settings.FullEveryN = 2;
    string saveDir2 = Path.Combine(temp, "cycle_saves");
    Directory.CreateDirectory(saveDir2);
    var g2 = new GameEntry { NameEN = "TestCycle" };
    g2.SavePaths.Add(new SavePathEntry { Path = saveDir2 });
    File.WriteAllText(Path.Combine(saveDir2, "x.txt"), "X1");
    var c1 = BackupService.Create(g2, "全量");
    File.WriteAllText(Path.Combine(saveDir2, "x.txt"), "X22");
    var c2 = BackupService.Create(g2, "i1");
    File.WriteAllText(Path.Combine(saveDir2, "x.txt"), "X333");
    var c3 = BackupService.Create(g2, "i2");
    File.WriteAllText(Path.Combine(saveDir2, "x.txt"), "X4444");
    var c4 = BackupService.Create(g2, "应全量");
    Check("增量: 满 N 次后强制全量", c2.Kind == "incr" && c3.Kind == "incr" && c4.Kind == "full",
        $"{c2.Kind},{c3.Kind},{c4.Kind}");
    BackupService.Restore(g2, c3);
    Check("增量: 2 级链还原", File.ReadAllText(Path.Combine(saveDir2, "x.txt")) == "X333");

    // 10.6 保留份数清理：无依赖的旧备份先删、链上节点保留。
    // 注意：上面刚 Restore(c3)，c3 已成"心目中的档"指针包（why=restore）→ 与🔒并列豁免、不占配额；
    // 此时可删候选只剩最新点 c4（永不牺牲）→ 宁超上限也一份不删，c3 与整条链都保住
    Store.Settings.MaxKeepPerGame = 3;
    BackupService.Prune(g2);   // 4 份：c4(最新,full) c3(★指针,incr) c2/c1(链上被引用) → 无可删候选
    Check("保留: 心目中指针包豁免 Prune（超配额也保住 c3），增量链不断",
        g2.Backups.Count == 4 && g2.Backups.Contains(c3)
        && g2.Backups.Any(b => b.Kind == "incr") && g2.Backups.Any(b => b.Kind == "full"),
        string.Join(",", g2.Backups.Select(b => b.Kind)));
    // 解除指针（模拟她认可了更新的 c4）后再 Prune：上限压到 2，未保护 3 份超限 → 旧节点可被淘汰
    ProfileService.MarkFavorite(g2, c4.Profile ?? "", c4.FileJson, "manual");
    Store.Settings.MaxKeepPerGame = 2;
    BackupService.Prune(g2);   // 未保护 3 份 > 2：候选 {c3 无引用}→ 删 c3
    Check("保留: 指针移走后旧备份可正常淘汰（3 份）且最新点存活",
        g2.Backups.Count == 3 && g2.Backups.Contains(c4) && !g2.Backups.Contains(c3)
        && g2.Backups.Any(b => b.Kind == "full"),
        string.Join(",", g2.Backups.Select(b => b.Kind)));
    BackupService.Restore(g2, g2.Backups[0]);   // 剩余链仍可还原
    Check("保留: 清理后链仍可还原", File.ReadAllText(Path.Combine(saveDir2, "x.txt")).StartsWith("X"));

    // 10.7 关闭增量 → 全部全量
    Store.Settings.IncrementalEnabled = false;
    File.WriteAllText(Path.Combine(saveDir2, "x.txt"), "X9");
    var c5 = BackupService.Create(g2, "关增量");
    Check("增量: 关闭后全部全量", c5.Kind == "full");

    // 还原环境
    Store.Settings.MaxKeepPerGame = svMax;
    Store.Settings.IncrementalEnabled = svIncr;
    Store.Settings.FullEveryN = svN;
    Store.HomeDirOverride = svHome;
}

// ── 11. 扫描文件夹：exe 候选识别 ──
{
    string froot = Path.Combine(temp, "games_root");
    Directory.CreateDirectory(Path.Combine(froot, "Stardew Valley"));
    Directory.CreateDirectory(Path.Combine(froot, "Stardew Valley", "Editor"));
    Directory.CreateDirectory(Path.Combine(froot, "MyGame2"));
    Directory.CreateDirectory(Path.Combine(froot, "redist"));
    Directory.CreateDirectory(Path.Combine(froot, "Deep", "Pack"));
    File.WriteAllText(Path.Combine(froot, "Stardew Valley", "Stardew Valley.exe"), new string('a', 300_000));
    File.WriteAllText(Path.Combine(froot, "Stardew Valley", "unins000.exe"), "x");
    File.WriteAllText(Path.Combine(froot, "Stardew Valley", "Editor", "Editor.exe"), new string('a', 250_000));
    File.WriteAllText(Path.Combine(froot, "MyGame2", "MyGame2.exe"), new string('a', 400_000));
    File.WriteAllText(Path.Combine(froot, "MyGame2", "MyGame2.exe.config"), "x");
    File.WriteAllText(Path.Combine(froot, "redist", "vcredist_x64.exe"), new string('a', 500_000));
    File.WriteAllText(Path.Combine(froot, "Deep", "Pack", "game.exe"), new string('a', 350_000));
    File.WriteAllText(Path.Combine(froot, "Tiny.exe"), "x");   // <200KB 小工具 → 排除

    var cands = FolderScanner.Scan(froot);
    Check("扫描: 识别 3 款游戏", cands.Count == 3,
        string.Join(" | ", cands.Select(c => c.Exe)));
    Check("扫描: 排除卸载器/运行库/嵌套/小工具",
        cands.Any(c => c.ProductName == "Stardew Valley") &&
        cands.Any(c => c.ProductName == "MyGame2") &&
        cands.Any(c => c.Exe.EndsWith("game.exe", StringComparison.OrdinalIgnoreCase)) &&
        !cands.Any(c => c.ProductName.Contains("Editor")) &&
        !cands.Any(c => c.Exe.Contains("vcredist")) &&
        !cands.Any(c => c.Exe.Contains("Tiny")),
        string.Join(" | ", cands.Select(c => c.Exe)));
    Check("扫描: 重复扫描结果稳定", FolderScanner.Scan(froot).Count == cands.Count);
}

// ── 12. 本地规则库 rules.json + Wiki appid 提取 ──
{
    string vault2 = Path.Combine(temp, "vault2");
    Store.HomeDirOverride = vault2;
    Directory.CreateDirectory(vault2);

    // 首次加载自动生成种子规则
    var seeded = LocalRules.Load();
    Check("规则: 首次运行生成种子规则", seeded.Count >= 8, seeded.Count.ToString());
    Check("规则: rules.json 落盘", File.Exists(LocalRules.RulesFile));

    // 变量展开
    string expanded = LocalRules.Expand(@"{docs}\MyGame", "");
    Check("规则: {docs} 变量展开",
        expanded == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MyGame"),
        expanded);

    // 自定义规则：指向真实存在的目录，Detect 应识别为「规则」来源
    string saveDir = Path.Combine(temp, "rule_saves");
    Directory.CreateDirectory(saveDir);
    string installDir = Path.Combine(temp, "fake_game_install");
    Directory.CreateDirectory(installDir);
    File.WriteAllText(Path.Combine(LocalRules.RulesFile), System.Text.Json.JsonSerializer.Serialize(new List<SaveRule>
    {
        new() { Match = "fakerulegame", Paths = new() { saveDir } },
        new() { Match = "[bad(regex", Paths = new() { @"Z:\nope" } },   // 非法正则不得拖垮加载
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    File.SetLastWriteTimeUtc(LocalRules.RulesFile, DateTime.UtcNow.AddMinutes(1));   // 确保热重载判定触发（同秒写入 mtime 可能不变）

    var hits = SaveLocator.Detect("FakeRuleGame", Path.Combine(installDir, "game.exe"),
        installDir, 0, null);
    Check("规则: Detect 命中自定义规则路径",
        hits.Any(p => p.Path.Equals(saveDir, StringComparison.OrdinalIgnoreCase)),
        string.Join(",", hits.Select(p => p.Source)));
    Check("规则: 命中来源标记为「规则」",
        hits.Any(p => p.Source == "规则" && p.Path.Equals(saveDir, StringComparison.OrdinalIgnoreCase)));

    // 不存在的规则路径不会被加入
    Check("规则: 不存在路径自动忽略", !hits.Any(p => p.Path == @"Z:\nope"));

    // Wiki wikitext 提取 Steam appid
    string wiki = "foo {{Infobox game}} [https://store.steampowered.com/app/292030/The_Witcher_3_Wild_Hunt Steam] bar";
    Check("Wiki: 从商店链接提取 appid", WikiService.ExtractAppId(wiki) == 292030,
        WikiService.ExtractAppId(wiki).ToString());
    Check("Wiki: 无链接返回 0", WikiService.ExtractAppId("no store link here") == 0);
    // /news/app/ 形式 + 主游戏 appid 出现最多（DLC 链接干扰）
    string wiki2 = "{{x|https://store.steampowered.com/news/app/292030/view/1}} "
                 + "{{x|https://store.steampowered.com/news/app/292030/view/2}} "
                 + "dlc [https://store.steampowered.com/app/9999999/DLC dlc] "
                 + "[https://store.steampowered.com/app/292030/]";
    Check("Wiki: appid 取出现最多且兼容 news 链接", WikiService.ExtractAppId(wiki2) == 292030,
          WikiService.ExtractAppId(wiki2).ToString());

    // infobox 模板参数 |steam appid = NNNN（现代 PCGW 页面主要形式）
    string wiki3 = "|steam appid  = 1245620\n|steam appid side = 2778580\n|gogcom id = 123";
    Check("Wiki: infobox |steam appid 参数优先", WikiService.ExtractAppId(wiki3) == 1245620,
        WikiService.ExtractAppId(wiki3).ToString());
    // infobox 参数 + 商店链接混合：参数优先
    string wiki4 = "|steam appid  = 1245620\n[https://store.steampowered.com/app/9999999/DLC]";
    Check("Wiki: infobox 参数优先于商店链接", WikiService.ExtractAppId(wiki4) == 1245620,
        WikiService.ExtractAppId(wiki4).ToString());

    // 名称归一化置信度场景（Torchlight II → torchlight2）
    Check("规则: 归一化匹配罗马数字", SaveLocator.NormalizeName("Torchlight II") == "torchlight2");

    Store.HomeDirOverride = null;
}

// ── 13. 备份命名（游戏名目录/文件）+ 改名迁移 + 旧 guid 目录迁移 ──
{
    string vault3 = Path.Combine(temp, "vault3");
    Store.HomeDirOverride = vault3;
    Directory.CreateDirectory(vault3);

    Check("备份命名: SafeName 替换非法字符", BackupService.SafeName("a:b*c?d") == "a_b_c_d",
        BackupService.SafeName("a:b*c?d"));
    Check("备份命名: SafeName 空名回退", BackupService.SafeName("   ") == "game");

    string saveDir3 = Path.Combine(temp, "naming_saves");
    Directory.CreateDirectory(saveDir3);
    File.WriteAllText(Path.Combine(saveDir3, "a.txt"), "V1");

    var gN = new GameEntry { NameCN = "测试:游戏<1>" };   // 故意含非法字符
    gN.SavePaths.Add(new SavePathEntry { Path = saveDir3 });
    Store.Games.Add(gN);   // MigrateLegacyDirs 只处理库集合内的游戏

    var bN = BackupService.Create(gN, "命名测试");
    // 新语义：备份默认归属虚拟玩家1，目录为 备份根\{游戏名}\玩家1
    string expectDir = BackupService.PlayerBackupDir(gN, "玩家1");
    Check("备份命名: 备份目录按游戏名+玩家1", Path.GetDirectoryName(bN.File) == expectDir,
        Path.GetDirectoryName(bN.File) ?? "");
    Check("备份命名: zip 文件名含游戏名",
        Path.GetFileName(bN.File).StartsWith(BackupService.SafeName(gN.DisplayName) + "_"),
        Path.GetFileName(bN.File));

    // 改名 → 目录迁移（记录路径同步，旧目录删除）
    string oldDisplay = gN.DisplayName;
    gN.NameCN = "新名字";
    gN.Refresh();
    BackupService.MigrateBackupDir(gN, oldDisplay);
    string newDir = Path.Combine(Store.BackupRoot, BackupService.SafeName("新名字"));
    Check("备份迁移: 改名后新目录有 zip 且旧目录删除",
        Directory.Exists(newDir) && File.Exists(bN.File) &&
        !Directory.Exists(Path.Combine(Store.BackupRoot, BackupService.SafeName(oldDisplay))),
        File.Exists(bN.File) ? "ok" : "zip missing");

    // 旧 guid 目录迁移：手工构造 legacy 布局再迁移
    string legacy = Path.Combine(Store.BackupRoot, gN.Id);
    Directory.CreateDirectory(legacy);
    string legacyZip = Path.Combine(legacy, "2025-01-01_000000.zip");
    File.Move(bN.File, legacyZip);
    bN.File = legacyZip;
    BackupService.MigrateLegacyDirs();
    string expectZip = Path.Combine(newDir, BackupService.SafeName(gN.DisplayName) + "_2025-01-01_000000.zip");
    Check("备份迁移: 旧 guid 目录迁回游戏名目录",
        File.Exists(expectZip) && !Directory.Exists(legacy),
        Directory.Exists(legacy) ? "legacy still exists" : "ok");
    Check("备份迁移: 记录指向新位置且文件存在",
        bN.File == expectZip && File.Exists(bN.File), bN.File);

    Store.Games.Remove(gN);   // 清理，避免污染其他块
    Store.HomeDirOverride = null;
}

// ── 14. 真实 games.json 反序列化：Backups 集合是否正确加载 ──
{
    string realJson = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameSaveVault", "games.json");
    if (File.Exists(realJson))
    {
        var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var list = JsonSerializer.Deserialize<List<GameEntry>>(File.ReadAllText(realJson), opts);
        Check("真实加载: games.json 反序列化成功", list != null && list.Count > 0,
            list == null ? "null" : $"count={list.Count}");
        if (list != null)
        {
            int withBak = list.Count(g => g.Backups.Count > 0);
            Check("真实加载: 有备份记录的游戏数 > 0", withBak > 0, $"withBackups={withBak}");
            // find Witcher3（按英文名匹配更稳定；中文名可能被同步为官方名，如"巫师 3"含空格）
            var w = list.FirstOrDefault(g => g.NameEN.Contains("Witcher", StringComparison.OrdinalIgnoreCase))
                 ?? list.FirstOrDefault(g => g.NameCN.Contains("巫师"));
            if (w != null)
            {
                Check("真实加载: 巫师3 有备份记录", w.Backups.Count >= 1,
                    $"count={w.Backups.Count} file={w.Backups.FirstOrDefault()?.File ?? "null"}");
            }
            else
            {
                Check("真实加载: 巫师3 存在", false, "not found");
            }
        }
    }
    else
    {
        Check("真实加载: games.json 存在", false, "file not found");
    }
}

// ── 14. SteamTools 搜索词插空格优化 ──
{
    Check("插空格: TheDevilInMe", SteamApi.InsertSpacesAtCaseBoundaries("TheDevilInMe") == "The Devil In Me",
        SteamApi.InsertSpacesAtCaseBoundaries("TheDevilInMe"));
    Check("插空格: ELDENRING 全大写不变", SteamApi.InsertSpacesAtCaseBoundaries("ELDENRING") == "ELDENRING",
        SteamApi.InsertSpacesAtCaseBoundaries("ELDENRING"));
    Check("插空格: EldenRing", SteamApi.InsertSpacesAtCaseBoundaries("EldenRing") == "Elden Ring",
        SteamApi.InsertSpacesAtCaseBoundaries("EldenRing"));
    Check("插空格: ABCdef → AB Cdef", SteamApi.InsertSpacesAtCaseBoundaries("ABCdef") == "AB Cdef",
        SteamApi.InsertSpacesAtCaseBoundaries("ABCdef"));
    Check("插空格: 已有空格不变", SteamApi.InsertSpacesAtCaseBoundaries("Elden Ring") == "Elden Ring",
        SteamApi.InsertSpacesAtCaseBoundaries("Elden Ring"));
    Check("插空格: 短词不变", SteamApi.InsertSpacesAtCaseBoundaries("AB") == "AB",
        SteamApi.InsertSpacesAtCaseBoundaries("AB"));
}

// ── 15. 图标提取：结果不应残留大块透明画布（jumbo 图标坑回归测试）──
{
    string realJson2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameSaveVault", "games.json");
    string? testExe = null;
    if (File.Exists(realJson2))
    {
        var jsonOpts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var games2 = JsonSerializer.Deserialize<List<GameEntry>>(File.ReadAllText(realJson2), jsonOpts);
        testExe = games2?.Select(g => g.ExePath).FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p) && File.Exists(p));
    }
    if (testExe == null)
    {
        Check("图标提取: 找到可测试的 exe", true, "无可用 exe，跳过");
    }
    else
    {
        // 遍历最多 12 个真实 exe（第一个为必测项），验证裁剪对每个都生效
        var exes = new List<string> { testExe };
        try
        {
            var jsonOpts2 = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            var allGames = JsonSerializer.Deserialize<List<GameEntry>>(File.ReadAllText(realJson2), jsonOpts2);
            if (allGames != null)
                exes.AddRange(allGames
                    .Select(g => g.ExePath)
                    .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p) && !exes.Contains(p))
                    .Take(11));
        }
        catch { }
        int idx = 0;
        foreach (var exe in exes)
        {
            idx++;
            string iconOut = Path.Combine(temp, $"icon-extract-test-{idx}.png");
            string? r = IconExtractor.ExtractToPng(exe, iconOut);
            Check($"图标提取[{idx}] {Path.GetFileName(exe)}: 成功", r != null && File.Exists(iconOut), "");
            if (r == null) continue;
            using var fs = File.OpenRead(iconOut);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var bmp = decoder.Frames[0];
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            var bgra = new FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    if (px[row + x * 4 + 3] > 8)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            bool fullBleed = minX == 0 && minY == 0 && maxX == w - 1 && maxY == h - 1;
            Check($"图标提取[{idx}] {Path.GetFileName(exe)}: 裁剪后内容铺满画布", fullBleed,
                fullBleed ? $"{w}x{h} 全铺满" : $"{w}x{h} 内容 x[{minX},{maxX}] y[{minY},{maxY}]");
        }
    }
}

// ── 16. 玩家存档：多玩家存档/备份隔离全流程 ──
{
    string vault4 = Path.Combine(temp, "vault4");
    Store.HomeDirOverride = vault4;
    Directory.CreateDirectory(vault4);
    Store.Init();

    string pSave = Path.Combine(temp, "player_saves", "saves");
    Directory.CreateDirectory(pSave);
    string aFile = Path.Combine(pSave, "a.txt");
    File.WriteAllText(aFile, "开局");

    var gP = new GameEntry { NameCN = "玩家测试游戏" };
    gP.SavePaths.Add(new SavePathEntry { Path = pSave });

    // 启用玩家前已有一份历史备份（旧数据：Profile=""）
    // 显式 profile:"" 模拟玩家机制启用前的通用备份（否则 Create 会虚拟实体化玩家1）
    var oldBak = BackupService.Create(gP, "启用前备份", hot: false, out _, profile: "");
    Check("玩家: 启用前备份归属通用", oldBak.Profile == "", oldBak.Profile);

    // 第一位玩家：当前进度成为他的存档，历史备份认领给他
    ProfileService.CreatePlayer(gP, "小明");
    gP.RefreshProfiles();
    Check("玩家: 首位玩家承接卡槽、历史备份已认领",
        gP.Players.Count == 1 && oldBak.Profile == "小明",
        $"players={gP.Players.Count},adopted={oldBak.Profile}");
    Check("玩家: 激活玩家为小明", gP.ActiveProfile == "小明", gP.ActiveProfile);
    Check("玩家: 历史备份归属改为小明且可见",
        oldBak.Profile == "小明" && gP.VisibleBackups.Count == 1,
        $"profile={oldBak.Profile},visible={gP.VisibleBackups.Count}");
    Check("玩家: 认领后 zip 移入玩家子目录",
        Path.GetDirectoryName(oldBak.File) == BackupService.PlayerBackupDir(gP, "小明") && File.Exists(oldBak.File),
        Path.GetDirectoryName(oldBak.File) ?? "");

    // 小明玩了一段再备份（内容有变化才会生成增量）
    File.WriteAllText(aFile, "小明v2");
    var mingBak = BackupService.Create(gP, "小明的进度");
    Check("玩家: 新备份归属小明并放进玩家子目录",
        mingBak.Profile == "小明" &&
        Path.GetDirectoryName(mingBak.File) == BackupService.PlayerBackupDir(gP, "小明"),
        $"profile={mingBak.Profile},dir={Path.GetDirectoryName(mingBak.File)}");

    // 第二位玩家：登记激活，不打空锚点（第一份真实存档时才产生备份）
    ProfileService.CreatePlayer(gP, "小红");
    gP.RefreshProfiles();
    Check("玩家: 新玩家小红激活、名下暂无备份（不打空锚点）",
        gP.ActiveProfile == "小红" && gP.VisibleBackups.Count == 0
        && gP.Players.Count == 2,
        $"active={gP.ActiveProfile},visible={gP.VisibleBackups.Count}");

    File.WriteAllText(aFile, "小红v1");
    var hongBak = BackupService.Create(gP, "小红的进度");
    gP.RefreshVisibleBackups();
    Check("玩家: 小红的备份独立归属、总数 3 份",
        hongBak.Profile == "小红" && gP.VisibleBackups.Count == 1 && gP.Backups.Count == 3,
        $"total={gP.Backups.Count},visible={gP.VisibleBackups.Count}");
    Check("玩家: 刚备份完最新备份即覆盖当前进度", BackupService.IsSaveUpToDate(gP),
        BackupService.IsSaveUpToDate(gP).ToString());

    // 切回小明：切走时给小红自动打包"切换玩家"备份（zip 唯一事实源），live 恢复为小明的内容。
    // 小红当前进度（小红v1）已在最新备份里 → 不产生冗余包
    ProfileService.SwitchTo(gP, "小明");
    gP.RefreshProfiles();
    Check("玩家: 切换后 live 存档恢复为目标玩家内容",
        File.ReadAllText(aFile) == "小明v2", File.ReadAllText(aFile));
    Check("玩家: 无进度变化切走不打冗余包（小红仍 1 份）",
        gP.Backups.Count(b => b.Profile == "小红") == 1,
        $"小红={gP.Backups.Count(b => b.Profile == "小红")}");
    Check("玩家: 切换后备份列表只含小明的 2 份",
        gP.VisibleBackups.Count == 2 && gP.VisibleBackups.All(b => b.Profile == "小明"),
        $"visible={gP.VisibleBackups.Count},profiles={string.Join(",", gP.VisibleBackups.Select(b => b.Profile))}");

    // 重命名玩家：备份归属与玩家备份子目录同步改名
    ProfileService.RenamePlayer(gP, "小明", "大明");
    gP.RefreshProfiles();
    Check("玩家: 改名后激活态与备份归属同步",
        gP.ActiveProfile == "大明" && gP.Backups.Count(b => b.Profile == "大明") == 2
        && Directory.Exists(BackupService.PlayerBackupDir(gP, "大明"))
        && !Directory.Exists(BackupService.PlayerBackupDir(gP, "小明")),
        $"active={gP.ActiveProfile}");

    // 无新进度时来回切换：当前进度已被最新备份覆盖 → 不产生冗余备份
    ProfileService.SwitchTo(gP, "小红");
    gP.RefreshProfiles();
    Check("玩家: 无进度变化切换不产生冗余备份（大明 2 份、小红 1 份）",
        gP.Backups.Count(b => b.Profile == "大明") == 2 && gP.Backups.Count(b => b.Profile == "小红") == 1,
        $"大明={gP.Backups.Count(b => b.Profile == "大明")},小红={gP.Backups.Count(b => b.Profile == "小红")}");
    Check("玩家: 切换后 live 为小红的进度",
        File.ReadAllText(aFile) == "小红v1", File.ReadAllText(aFile));

    // 小红玩出未备份的新进度后切走：进度自动打包进她的"切换玩家"备份，绝不丢档
    File.WriteAllText(aFile, "小红v2");
    ProfileService.SwitchTo(gP, "大明");
    gP.RefreshProfiles();
    Check("玩家: 切走时未备份进度自动打包（小红 2 份，live 恢复大明进度）",
        gP.Backups.Count(b => b.Profile == "小红") == 2 && File.ReadAllText(aFile) == "小明v2"
        && gP.Backups.Any(b => b.Profile == "小红" && b.Note == "切换玩家"),
        $"小红={gP.Backups.Count(b => b.Profile == "小红")},live={File.ReadAllText(aFile)}");

    // 切回小红：live 恢复为她最新备份（切换包）的内容，备份份数不变
    ProfileService.SwitchTo(gP, "小红");
    gP.RefreshProfiles();
    Check("玩家: 切回小红 live = 她的切换备份进度（小红v2，备份仍 2 份）",
        File.ReadAllText(aFile) == "小红v2" && gP.Backups.Count(b => b.Profile == "小红") == 2,
        $"live={File.ReadAllText(aFile)},小红={gP.Backups.Count(b => b.Profile == "小红")}");

    // 新行为：玩家是独立实体——名下备份被手动删光后卡槽仍保留（只能右键卡槽删除玩家）；他仍是当前玩家
    foreach (var r in gP.Backups.Where(b => b.Profile == "小红").OrderByDescending(b => b.Time).ToList())
        BackupService.Delete(gP, r);
    gP.RefreshProfiles();
    Check("玩家: 备份删光后卡槽仍保留",
        gP.ProfileSlots.Any(v => v.Name == "小红"), string.Join(",", gP.ProfileSlots.Select(v => v.Name)));
    Check("玩家: ListSlots 仍含该玩家且激活态不清空",
        ProfileService.ListSlots(gP).Contains("小红") && gP.ActiveProfile == "小红",
        $"active={gP.ActiveProfile}");
    Check("玩家: 备份列表为空且空提示指向该玩家",
        gP.VisibleBackups.Count == 0 && gP.EmptyBackupHint.Contains("小红"),
        $"visible={gP.VisibleBackups.Count},hint={gP.EmptyBackupHint}");

    // 他仍是当前玩家：玩出新进度后立即备份，新备份重新归到他名下
    File.WriteAllText(aFile, "小红v3");
    BackupService.Create(gP, "重新备份");
    gP.RefreshProfiles();
    Check("玩家: 当前玩家重新备份归到其名下",
        gP.Backups.Count(b => b.Profile == "小红") == 1 && gP.VisibleBackups.Count == 1,
        $"小红={gP.Backups.Count(b => b.Profile == "小红")},visible={gP.VisibleBackups.Count}");

    // 零备份的非激活玩家：卡槽保留，但切换被拒绝（名下没有可还原的备份）
    ProfileService.CreatePlayer(gP, "小刚");        // 切走小红（无变化跳过），小刚无锚点；active=小刚
    ProfileService.SwitchTo(gP, "大明");             // 小刚无新进度→不打包；live 恢复大明，active=大明
    gP.RefreshProfiles();
    Check("玩家: 零备份非激活玩家卡槽仍保留", ProfileService.ListSlots(gP).Contains("小刚"));
    bool noRestore = false;
    try { ProfileService.SwitchTo(gP, "小刚"); }
    catch (InvalidOperationException) { noRestore = true; }
    Check("玩家: 切换到零备份玩家被拒绝（无可还原备份）", noRestore);
    Check("玩家: 切换被拒后 live 与激活态不变", gP.ActiveProfile == "大明" && File.ReadAllText(aFile) == "小明v2");

    // 删除玩家（右键卡槽）是移除卡槽的唯一途径：零备份也可删，其他人与备份不受影响
    int gangCount = gP.Backups.Count(b => b.Profile == "小刚");
    int delCount = ProfileService.DeletePlayer(gP, "小刚");
    gP.RefreshProfiles();
    Check("玩家: 删除玩家移除卡槽（零备份亦删），其他人卡槽都在",
        delCount == gangCount && gangCount == 0
        && !ProfileService.ListSlots(gP).Contains("小刚")
        && ProfileService.ListSlots(gP).Contains("大明")
        && ProfileService.ListSlots(gP).Contains("小红")
        && gP.ActiveProfile == "大明"
        && !Directory.Exists(BackupService.PlayerBackupDir(gP, "小刚")),
        $"del={delCount},slots={string.Join(",", ProfileService.ListSlots(gP))},active={gP.ActiveProfile}");
    Check("玩家: 删除后剩余备份 = 大明 2 + 小红 1", gP.Backups.Count == 3,
        $"total={gP.Backups.Count}");

    // 启动迁移：旧版玩家镜像（pN 目录 + slot.json）补打成该玩家的 zip，随后通用备份认领给玩家
    string pSave2 = Path.Combine(temp, "player_saves2", "saves");
    Directory.CreateDirectory(pSave2);
    File.WriteAllText(Path.Combine(pSave2, "a.txt"), "小刚开局");
    var gP2 = new GameEntry { NameCN = "迁移测试游戏" };
    gP2.SavePaths.Add(new SavePathEntry { Path = pSave2 });
    Store.Games.Add(gP2);
    // 玩家机制启用前的通用备份：显式 profile:""（否则会自动归虚拟玩家1）
    var legacyBak = BackupService.Create(gP2, "旧备份", hot: false, out _, profile: "");
    string migSlotP0 = Path.Combine(ProfileService.RootFor(gP2), "小刚", "p0");
    Directory.CreateDirectory(migSlotP0);
    File.WriteAllText(Path.Combine(migSlotP0, "a.txt"), "小刚的镜像进度");
    File.WriteAllText(Path.Combine(ProfileService.RootFor(gP2), "小刚", "slot.json"),
        "{\"Name\":\"小刚\",\"Created\":\"2026-01-01T00:00:00\",\"Paths\":[\""
        + pSave2.Replace("\\", "\\\\") + "\"]}");
    var migFails = ProfileService.MigrateMirrorBackups();
    Check("镜像迁移: 镜像补打成玩家 zip 并删除镜像目录",
        migFails.Count == 0 && !Directory.Exists(Path.Combine(ProfileService.RootFor(gP2), "小刚"))
        && gP2.Backups.Any(b => b.Profile == "小刚" && b.Note == "镜像迁移"),
        string.Join(";", migFails));
    Check("镜像迁移: 幂等（再跑一次无失败）",
        ProfileService.MigrateMirrorBackups().Count == 0);
    var migBak = gP2.Backups.First(b => b.Profile == "小刚" && b.Note == "镜像迁移");
    File.WriteAllText(Path.Combine(pSave2, "a.txt"), "被覆盖");
    BackupService.Restore(gP2, migBak, purgeNewFiles: true);
    Check("镜像迁移: 迁移包按 slot.json 的真实存档路径还原",
        File.ReadAllText(Path.Combine(pSave2, "a.txt")) == "小刚的镜像进度",
        File.ReadAllText(Path.Combine(pSave2, "a.txt")));
    gP2.ActiveProfile = "小刚";
    ProfileService.MigrateLegacyBackupsToPlayers();
    Check("玩家: 启动迁移把通用备份认领给已有玩家",
        legacyBak.Profile == "小刚" && File.Exists(legacyBak.File)
        && Path.GetDirectoryName(legacyBak.File) == BackupService.PlayerBackupDir(gP2, "小刚"),
        $"profile={legacyBak.Profile},dir={Path.GetDirectoryName(legacyBak.File)}");

    // ── 17. 存档路径：用户手动移除后，后台自动探测/Wiki 补全不得复活 ──
    {
        var gS = new GameEntry { NameCN = "路径测试游戏" };
        string bad = Path.Combine(temp, "wrong_save_dir");
        string good = Path.Combine(temp, "good_save_dir");
        Directory.CreateDirectory(bad); Directory.CreateDirectory(good);
        gS.TryAddAutoSavePath(new SavePathEntry { Path = bad, Source = "模板" });
        gS.TryAddAutoSavePath(new SavePathEntry { Path = good, Source = "模板" });
        Check("路径: 自动探测两条路径入列", gS.SavePaths.Count == 2, $"n={gS.SavePaths.Count}");

        // 用户移除错误路径
        var badEntry = gS.SavePaths.First(p => p.Path.Equals(bad, StringComparison.OrdinalIgnoreCase));
        gS.RemoveSavePath(badEntry);
        Check("路径: 移除后记入忽略名单",
            gS.SavePaths.Count == 1 && gS.IgnoredSavePaths.Count == 1,
            $"n={gS.SavePaths.Count},ignored={gS.IgnoredSavePaths.Count}");

        // 后台自动探测再次发现该路径（带尾部分隔符差异）→ 不得加回
        bool revived = gS.TryAddAutoSavePath(new SavePathEntry { Path = bad + "\\", Source = "Wiki" });
        Check("路径: 后台自动探测不加回已移除路径（尾斜杠归一化）",
            !revived && gS.SavePaths.Count == 1, $"revived={revived},n={gS.SavePaths.Count}");

        // 忽略名单随 games.json 持久化：序列化→反序列化后仍生效
        var gS2 = JsonSerializer.Deserialize<GameEntry>(JsonSerializer.Serialize(gS))!;
        bool revived2 = gS2.TryAddAutoSavePath(new SavePathEntry { Path = bad, Source = "Wiki" });
        Check("路径: 忽略名单持久化后仍生效", !revived2 && gS2.SavePaths.Count == 1, $"revived={revived2}");

        // 用户手动添加同一路径 → 解除忽略，以"手动"源入列
        gS.AddManualSavePath(bad);
        Check("路径: 手动添加解除忽略并以手动源入列",
            gS.SavePaths.Count == 2 && gS.IgnoredSavePaths.Count == 0
            && gS.SavePaths.Any(p => p.Path.Equals(bad, StringComparison.OrdinalIgnoreCase) && p.Source == "手动"),
            $"n={gS.SavePaths.Count},ignored={gS.IgnoredSavePaths.Count}");

        // 显式点「自动获取/刷新规则」→ 允许加回曾移除的路径并解除忽略
        var gS3 = new GameEntry { NameCN = "路径测试游戏3" };
        gS3.TryAddAutoSavePath(new SavePathEntry { Path = bad, Source = "模板" });
        gS3.RemoveSavePath(gS3.SavePaths[0]);
        bool accepted = gS3.AcceptAutoSavePath(new SavePathEntry { Path = bad, Source = "Wiki" });
        Check("路径: 显式自动获取可加回曾移除路径并解除忽略",
            accepted && gS3.SavePaths.Count == 1 && gS3.IgnoredSavePaths.Count == 0,
            $"accepted={accepted},n={gS3.SavePaths.Count},ignored={gS3.IgnoredSavePaths.Count}");
    }

    // ── 18. 移除游戏后的数据管理：保留可找回 / 彻底删除 / 残留清理 ──
    {
        // 一局游戏：玩家 + 备份齐全
        string rmSave = Path.Combine(temp, "rm_game_saves");
        Directory.CreateDirectory(rmSave);
        File.WriteAllText(Path.Combine(rmSave, "save1.dat"), "小明的进度");
        var gR = new GameEntry { NameCN = "移除测试游戏" };
        gR.SavePaths.Add(new SavePathEntry { Path = rmSave });
        Store.Games.Add(gR);
        ProfileService.CreatePlayer(gR, "小明");
        File.WriteAllText(Path.Combine(rmSave, "save1.dat"), "小明玩过一段");   // 制造变化
        BackupService.Create(gR, "小明的备份");
        string oldId = gR.Id;
        string rmGameDir = BackupService.BackupDirFor(gR);
        string oldProfileRoot = ProfileService.RootFor(gR);
        Check("移除: 备份与玩家身份证已生成",
            Directory.Exists(rmGameDir) && Directory.Exists(oldProfileRoot) && gR.Backups.Count == 2
            && File.Exists(Path.Combine(oldProfileRoot, "game.json")),
            $"dir={Directory.Exists(rmGameDir)},profiles={Directory.Exists(oldProfileRoot)},n={gR.Backups.Count}");

        // 移除（保留）：zip 本就按游戏名存放、天然脱离 GUID；GUID 身份证根删除，激活玩家记进 _active.txt
        ProfileService.DetachOnRemoval(gR);
        Store.Games.Remove(gR); Store.Save();
        string detachedActive = Path.Combine(rmGameDir, "_profiles", "_active.txt");
        Check("移除: 保留移除后 zip 留在游戏名目录、GUID 根清理、激活玩家已记录",
            !Directory.Exists(oldProfileRoot)
            && Directory.GetFiles(rmGameDir, "*.zip", SearchOption.AllDirectories).Length == 2
            && Directory.Exists(Path.Combine(rmGameDir, "小明"))
            && File.Exists(detachedActive) && File.ReadAllText(detachedActive) == "小明",
            "detach");
        Check("移除: 残留清理页列出该游戏目录（含 zip）",
            BackupService.ListOrphans().Any(o => !o.IsProfileRoot
                && o.Name == BackupService.SafeName("移除测试游戏") && o.ZipCount >= 1),
            "orphan list");

        // 重新添加（全新 GUID）同名游戏 → 自动找回 zip 与玩家，激活玩家按标记恢复
        var gR2 = new GameEntry { NameCN = "移除测试游戏" };
        gR2.SavePaths.Add(new SavePathEntry { Path = rmSave });
        Store.Games.Add(gR2);
        var (reBak, reSlot) = ProfileService.ReclaimAll(gR2,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Check("移除: 重新添加找回全部 zip（新 GUID 身份证）与激活玩家",
            gR2.Id != oldId && reSlot == 1 && reBak == 2 && gR2.ActiveProfile == "小明"
            && File.Exists(Path.Combine(ProfileService.RootFor(gR2), "game.json"))
            && !Directory.Exists(Path.Combine(rmGameDir, "_profiles")),
            $"n={reSlot},active={gR2.ActiveProfile},bak={reBak}");
        Check("移除: 重新添加按 manifest 找回旧备份且归属玩家",
            gR2.Backups.Count == 2 && gR2.Backups.All(b => b.Profile == "小明"),
            $"n={gR2.Backups.Count},profiles=[{string.Join(",", gR2.Backups.Select(b => b.Profile))}]");
        Check("移除: 找回后该目录不再列为残留",
            !BackupService.ListOrphans().Any(o => o.Name == BackupService.SafeName("移除测试游戏")),
            "still orphan");
        // 找回幂等：再来一次不重复
        var (reBak2, _) = ProfileService.ReclaimAll(gR2, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Check("移除: 找回过程幂等不重复", reBak2 == 0 && gR2.Backups.Count == reBak, $"again={reBak2}");

        // 游戏名 SafeName 匹配是充分信号，就算存档路径完全不同也应该认领
        // （Steam 卸载/重装、学习版换安装位、Steam↔学习版互转都是这种场景）
        string save2 = Path.Combine(temp, "other_saves");
        Directory.CreateDirectory(save2);
        var gSame = new GameEntry { NameCN = "移除测试游戏" };   // 同 SafeName
        gSame.SavePaths.Add(new SavePathEntry { Path = save2 }); // 完全不同的存档路径
        int sameClaim = BackupService.ReclaimOrphanBackups(gSame, new(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Check("移除: 同名游戏不管存档路径是否相交都认领（Steam卸载/重装场景）",
            sameClaim >= 1, $"claim={sameClaim}");

        // 第二局：彻底删除
        string delSave = Path.Combine(temp, "del_game_saves");
        Directory.CreateDirectory(delSave);
        File.WriteAllText(Path.Combine(delSave, "x.sav"), "x");
        var gD = new GameEntry { NameEN = "DeleteMeGame" };
        gD.SavePaths.Add(new SavePathEntry { Path = delSave });
        Store.Games.Add(gD);
        BackupService.Create(gD, "bye");
        ProfileService.CreatePlayer(gD, "小刚");
        string dDir = BackupService.BackupDirFor(gD), dRoot = ProfileService.RootFor(gD);
        BackupService.DeleteAllOnRemoval(gD);
        Store.Games.Remove(gD); Store.Save();
        Check("移除: 彻底删除后 zip 与玩家镜像目录都消失",
            !Directory.Exists(dDir) && !Directory.Exists(dRoot), "deleted");

        // 早期版本遗留的 GUID 镜像目录可被列出和删除
        string ghostRoot = Path.Combine(ProfileService.ProfilesRoot, "ghostid0001");
        Directory.CreateDirectory(Path.Combine(ghostRoot, "玩家A"));
        var ghost = BackupService.ListOrphans()
            .FirstOrDefault(o => o.IsProfileRoot && o.Name == "ghostid0001");
        Check("移除: GUID 玩家镜像残留可列出（玩家数正确）",
            ghost != null && ghost.SlotCount == 1, "ghost list");
        BackupService.DeleteOrphan(ghostRoot);
        Check("移除: 残留删除成功", !Directory.Exists(ghostRoot), "ghost del");
        // 删除防护：备份根之外的路径不允许删
        bool guarded = false;
        try { BackupService.DeleteOrphan(temp); }
        catch (InvalidOperationException) { guarded = true; }
        Check("移除: 删除残留限制在备份根之内", guarded, "guard");
    }

    // ── 19. 鲁棒性：索引/设置损坏隔离、备份盘掉线回退、卸载换目录重装的条目合并 ──
    {
        // 19.1 games.json 损坏：改名隔离 + 空库启动 + 磁盘 zip 原样保留
        string vault5 = Path.Combine(temp, "vault5");
        Directory.CreateDirectory(vault5);
        string bdir5 = Path.Combine(vault5, "backups", BackupService.SafeName("损坏测试游戏"));
        Directory.CreateDirectory(bdir5);
        string zipKeep = Path.Combine(bdir5, "old.zip");
        File.WriteAllText(zipKeep, "zip-presence-marker");
        File.WriteAllText(Path.Combine(vault5, "games.json"), "{ 这不是合法JSON ");
        Store.HomeDirOverride = vault5;
        Store.Init();
        Check("鲁棒: 损坏索引以空库启动", Store.Games.Count == 0, $"n={Store.Games.Count}");
        Check("鲁棒: 损坏索引被改名隔离、原位不再有坏文件",
            Store.CorruptGamesFile != null
            && File.Exists(Path.Combine(vault5, Store.CorruptGamesFile!))
            && !File.Exists(Path.Combine(vault5, "games.json")),
            Store.CorruptGamesFile ?? "");
        Store.Save();   // 落盘新空库，不得动到已隔离的坏文件
        Check("鲁棒: 空库落盘成功且磁盘 zip 与隔离文件都保留",
            File.Exists(zipKeep) && File.Exists(Path.Combine(vault5, "games.json"))
            && File.Exists(Path.Combine(vault5, Store.CorruptGamesFile!)), "save");

        // 19.2 settings.json 损坏 → 默认设置，坏文件隔离
        string vault6 = Path.Combine(temp, "vault6");
        Directory.CreateDirectory(vault6);
        File.WriteAllText(Path.Combine(vault6, "settings.json"), "{ broken");
        Store.HomeDirOverride = vault6;
        Store.Init();
        Check("鲁棒: 损坏设置回退默认并标记",
            Store.CorruptSettings && Store.Settings.MaxKeepPerGame == 10
            && !Store.Settings.IncrementalEnabled && Store.Settings.BackupDir == "",
            $"flag={Store.CorruptSettings}");

        // 19.3 自定义备份根不可用（非法路径模拟盘掉线/无权限）→ 回退默认备份根，启动不崩
        string vault7 = Path.Combine(temp, "vault7");
        Directory.CreateDirectory(vault7);
        string badSettings = JsonSerializer.Serialize(new { BackupDir = Path.Combine(vault7, "bad|dir") });
        File.WriteAllText(Path.Combine(vault7, "settings.json"), badSettings);
        Store.HomeDirOverride = vault7;
        Store.Init();
        Check("鲁棒: 备份根不可用时回退默认目录且标记",
            Store.BackupRootFallback && Store.Settings.BackupDir == ""
            && Directory.Exists(Path.Combine(vault7, "backups")),
            $"flag={Store.BackupRootFallback},root={Store.BackupRoot}");

        // 19.4 卸载后换目录重装：旧条目（exe 已失效 + 收藏 + 历史备份）并入新条目信息
        string vault8 = Path.Combine(temp, "vault8");
        Store.HomeDirOverride = vault8;
        Store.Init();
        string deadExe = Path.Combine(temp, "old_install", "game.exe");
        string newExe = Path.Combine(temp, "new_install", "game.exe");
        Directory.CreateDirectory(Path.Combine(temp, "new_install"));
        File.WriteAllText(newExe, "");   // 新位置 exe 存在
        string oldSave = Path.Combine(temp, "old_saves");
        string newSave = Path.Combine(temp, "new_saves");
        Directory.CreateDirectory(oldSave); Directory.CreateDirectory(newSave);

        var oldEntry = new GameEntry
        {
            NameCN = "重装测试游戏", AppId = 777001, Type = "Local", Favorite = true,
            ExePath = deadExe, InstallDir = Path.Combine(temp, "old_install"),
            AddedAt = DateTime.Now.AddDays(-30),
        };
        oldEntry.SavePaths.Add(new SavePathEntry { Path = oldSave, Source = "手动" });
        oldEntry.Backups.Add(new BackupRecord { File = Path.Combine(temp, "old.zip"), Size = 10 });
        var newEntry = new GameEntry
        {
            NameEN = "Reinstall Game", AppId = 777001, Type = "Local",
            ExePath = newExe, InstallDir = Path.Combine(temp, "new_install"),
            AddedAt = DateTime.Now, ActiveProfile = "小明",
        };
        newEntry.SavePaths.Add(new SavePathEntry { Path = newSave, Source = "Wiki" });
        // 新条目抢先认领到一份备份（不同 zip）
        newEntry.Backups.Add(new BackupRecord { File = Path.Combine(temp, "new.zip"), Size = 20 });
        // 新条目带玩家镜像
        Directory.CreateDirectory(Path.Combine(ProfileService.RootFor(newEntry), "小明"));

        int added = Store.MergeGame(oldEntry, newEntry);
        Check("鲁棒: 合并后存档路径并入（旧 1 条 + 新 1 条）",
            added == 1 && oldEntry.SavePaths.Count == 2
            && oldEntry.SavePaths.Any(p => p.Path.Equals(newSave, StringComparison.OrdinalIgnoreCase)),
            $"added={added},n={oldEntry.SavePaths.Count}");
        Check("鲁棒: 旧 exe 失效时采用重装后的新位置",
            oldEntry.ExePath.Equals(newExe, StringComparison.OrdinalIgnoreCase)
            && oldEntry.InstallDir.Equals(Path.Combine(temp, "new_install"), StringComparison.OrdinalIgnoreCase),
            oldEntry.ExePath);
        Check("鲁棒: 历史备份并集、收藏保留、激活玩家继承",
            oldEntry.Backups.Count == 2 && oldEntry.Favorite && oldEntry.ActiveProfile == "小明",
            $"n={oldEntry.Backups.Count},fav={oldEntry.Favorite},active={oldEntry.ActiveProfile}");
        Check("鲁棒: 新条目玩家镜像并入旧 GUID 目录",
            Directory.Exists(Path.Combine(ProfileService.RootFor(oldEntry), "小明"))
            && !Directory.Exists(ProfileService.RootFor(newEntry)), "profiles");

        // 19.5 两份 exe 都有效（真·双装）：合并不得抢走旧位置
        string exeB = Path.Combine(temp, "dual_b", "game.exe");
        Directory.CreateDirectory(Path.Combine(temp, "dual_b"));
        File.WriteAllText(exeB, "");
        var dualOld = new GameEntry { AppId = 777002, ExePath = newExe, InstallDir = Path.Combine(temp, "new_install") };
        var dualNew = new GameEntry { AppId = 777002, ExePath = exeB, InstallDir = Path.Combine(temp, "dual_b") };
        Store.MergeGame(dualOld, dualNew);
        Check("鲁棒: 两处 exe 都在时保留原 exe 位置（不武断改判）",
            dualOld.ExePath.Equals(newExe, StringComparison.OrdinalIgnoreCase), dualOld.ExePath);
    }

    // ── 20. 玩家镜像身份证 game.json：写入/改名刷新/丢索引后按名回迁/防误抢 ──
    {
        string vault9 = Path.Combine(temp, "vault9");
        Store.HomeDirOverride = vault9;
        Store.Init();
        string mkSave = Path.Combine(temp, "marker_saves");
        Directory.CreateDirectory(mkSave);
        File.WriteAllText(Path.Combine(mkSave, "save.dat"), "v1");

        var gM = new GameEntry { NameCN = "身份证测试游戏" };
        gM.SavePaths.Add(new SavePathEntry { Path = mkSave });
        Store.Games.Add(gM);
        ProfileService.CreatePlayer(gM, "小明");
        string oldRoot = ProfileService.RootFor(gM);
        string markerFile = Path.Combine(oldRoot, "game.json");
        Check("身份证: 建玩家后镜像根写入 game.json",
            File.Exists(markerFile) && File.ReadAllText(markerFile).Contains(gM.Id),
            markerFile);
        gM.NameCN = "身份证改名游戏";
        ProfileService.BackfillMarkers();
        Check("身份证: 启动回填在改名后刷新身份证内容（幂等）",
            File.Exists(markerFile) && File.ReadAllText(markerFile).Contains("身份证改名游戏"),
            "backfill");
        // 内容不变时回填/重写完全不碰文件（文件防护类安全软件会把原子替换截留进回收站）
        DateTime markerWrite = File.GetLastWriteTime(markerFile);
        Thread.Sleep(30);
        ProfileService.BackfillMarkers();
        ProfileService.WriteGameMarker(gM);
        Check("身份证: 内容未变时回填/重写零覆写（文件时间不变）",
            File.GetLastWriteTime(markerFile) == markerWrite, "rewritten");
        File.WriteAllText(Path.Combine(mkSave, "save.dat"), "v2-小明玩过");
        BackupService.MigrateBackupDir(gM, "身份证测试游戏");   // 改名流程：zip 目录随游戏名迁移
        var zipBak = BackupService.Create(gM, "小明的备份");

        // 残留列表里带身份证的 GUID 根显示真实游戏名（此时还在库，不应列为残留；先模拟丢索引）
        Store.Games.Remove(gM);   // 注意：不走 DetachOnRemoval，模拟 games.json 整个丢失
        var orphanBefore = BackupService.ListOrphans()
            .FirstOrDefault(o => o.IsProfileRoot && o.Dir.Equals(oldRoot, StringComparison.OrdinalIgnoreCase));
        Check("身份证: 丢索引后 GUID 根列为残留且显示游戏名而非 GUID",
            orphanBefore != null && orphanBefore.Name == "身份证改名游戏" && orphanBefore.SlotCount == 0,
            orphanBefore?.Name ?? "(未列出)");

        // 以全新 GUID 重新入库（重扫/重添），同名 → zip 按 manifest 归属找回，身份证回迁、激活玩家恢复
        var gM2 = new GameEntry { NameCN = "身份证改名游戏" };
        gM2.SavePaths.Add(new SavePathEntry { Path = mkSave });
        Store.Games.Add(gM2);
        var (reBak, reSlot) = ProfileService.ReclaimAll(gM2);
        string newRoot = ProfileService.RootFor(gM2);
        Check("身份证: 丢索引重添后旧 GUID 身份证根清理、新根建立",
            reSlot == 1 && File.Exists(Path.Combine(newRoot, "game.json")) && !Directory.Exists(oldRoot),
            $"slot={reSlot}");
        Check("身份证: 回迁后新根携带新身份证，记录的是新 GUID",
            File.ReadAllText(Path.Combine(newRoot, "game.json")).Contains(gM2.Id)
            && !File.ReadAllText(Path.Combine(newRoot, "game.json")).Contains(gM.Id),
            "new marker");
        Check("身份证: 激活玩家随身份证恢复、zip 备份按玩家归属找回",
            gM2.ActiveProfile == "小明" && reBak == 2
            && gM2.Backups.All(b => b.Profile == "小明") && File.Exists(zipBak.File),
            $"active={gM2.ActiveProfile},bak={reBak}");

        // 防误抢：在库其他游戏的身份证根绝不能被 ReclaimAll 搬走
        string otherSave = Path.Combine(temp, "other_marker_saves");
        Directory.CreateDirectory(otherSave);
        var gA = new GameEntry { NameCN = "路人甲游戏" };
        gA.SavePaths.Add(new SavePathEntry { Path = otherSave });
        Store.Games.Add(gA);
        ProfileService.CreatePlayer(gA, "甲");
        string aRoot = ProfileService.RootFor(gA);
        var gC = new GameEntry { NameCN = "完全不相干游戏" };
        gC.SavePaths.Add(new SavePathEntry { Path = otherSave });
        Store.Games.Add(gC);
        ProfileService.ReclaimAll(gC);
        Check("身份证: 找回时不触碰其他在库游戏的身份证根",
            File.Exists(Path.Combine(aRoot, "game.json")), "甲的身份证丢失!");

        // 老数据兼容：无 game.json 的旧 GUID 根不被误认领，仍按 GUID 列残留
        string legacy = Path.Combine(ProfileService.ProfilesRoot, "legacyguid001");
        Directory.CreateDirectory(Path.Combine(legacy, "老玩家"));
        ProfileService.ReclaimAll(gC);
        var legacyOrphan = BackupService.ListOrphans()
            .FirstOrDefault(o => o.IsProfileRoot && o.Dir.Equals(legacy, StringComparison.OrdinalIgnoreCase));
        Check("身份证: 无身份证的旧 GUID 根不被误认领，仍按 GUID 列残留",
            Directory.Exists(Path.Combine(legacy, "老玩家")) && legacyOrphan != null
            && legacyOrphan.Name == "legacyguid001",
            legacyOrphan?.Name ?? "(已丢失)");
    }

    // ── 21. 程序自身数据快照 _appdata：双槽原地留底/损坏与丢失恢复/节流/历史快照兜底/自定义盘补恢复 ──
    {
        string vault10 = Path.Combine(temp, "vault10");
        Store.HomeDirOverride = vault10;
        Store.Init();
        var gS = new GameEntry { NameCN = "自留底测试游戏", AppId = 424242 };
        Store.Games.Add(gS);
        Store.Save();
        string snapDir = Path.Combine(vault10, "backups", Store.AppSnapshotDir);
        string[] GamesSlots() => new[] { "games.1.json", "games.2.json" }
            .Select(n => Path.Combine(snapDir, n)).Where(File.Exists).ToArray();
        Check("自留底: Save 后在 backups\\_appdata 生成 games 双槽副本（先建槽1）", GamesSlots().Length == 1,
            $"n={GamesSlots().Length},dir={Directory.Exists(snapDir)}");
        Check("自留底: _appdata 不被列为残留",
            !BackupService.ListOrphans().Any(o => o.Dir.Equals(snapDir, StringComparison.OrdinalIgnoreCase)),
            "listed");
        // 节流：10 分钟内的非强制留底不新增也不覆写
        string slot1 = GamesSlots()[0];
        DateTime slot1Write = File.GetLastWriteTime(slot1);
        Thread.Sleep(30);
        Store.SnapshotAppData("games.json");
        Store.SnapshotAppData("games.json");
        Check("自留底: 10 分钟节流期内不新增也不覆写副本",
            GamesSlots().Length == 1 && File.GetLastWriteTime(slot1) == slot1Write, "throttle");
        // 节流以槽位 LastWriteTime 为准：早 5 分钟仍节流
        File.SetLastWriteTime(slot1, DateTime.Now.AddMinutes(-5));
        Store.SnapshotAppData("games.json");
        Check("自留底: 槽位时间早 5 分钟时节流仍生效（不建槽2）",
            GamesSlots().Length == 1, "fake-fresh");
        // 槽位时间早 15 分钟：缺槽优先 → 建槽2，槽1原样保留
        File.SetLastWriteTime(slot1, DateTime.Now.AddMinutes(-15));
        Store.SnapshotAppData("games.json");
        Check("自留底: 超过节流间隔且缺槽时建第 2 槽（旧槽保留）",
            GamesSlots().Length == 2, "fake-stale");
        // 两槽建满后：只原地覆写较旧槽，文件数恒为 2，且不再产生旧版时间戳快照（全程零删除）
        for (int i = 0; i < 22; i++) Store.SnapshotAppData("games.json", force: true);
        var slotsNow = GamesSlots();
        bool anyLegacy = Directory.GetFiles(snapDir, "games_*.json").Length > 0;
        Check("自留底: 双槽建满后文件数恒为 2 且不再产生时间戳快照（零删除）",
            slotsNow.Length == 2 && !anyLegacy,
            $"n={slotsNow.Length},legacy={anyLegacy}");
        Check("自留底: 两个槽内容都是当前游戏库 JSON",
            slotsNow.All(f => File.ReadAllText(f).Contains("自留底测试游戏")), "slot-content");
        // 半截槽恢复：较新槽被写坏 → games.json 损坏后 Init 自动跳过坏槽、从完好槽恢复
        string newerSlot = slotsNow.OrderByDescending(File.GetLastWriteTime).First();
        File.WriteAllText(newerSlot, "{broken-slot");
        File.WriteAllText(Path.Combine(vault10, "games.json"), "{broken");
        Store.Init();
        Check("自留底: games.json 损坏且新槽半写时自动从完好槽恢复",
            Store.Games.Count == 1 && Store.Games[0].AppId == 424242
            && Store.RestoredGamesSnapshot != null && Store.CorruptGamesFile != null
            && File.Exists(Path.Combine(vault10, Store.CorruptGamesFile!)),
            $"n={Store.Games.Count},snap={Store.RestoredGamesSnapshot}");
        // 历史时间戳快照兜底：双槽都不在时，存量旧版快照仍可恢复
        File.Delete(Path.Combine(snapDir, "games.1.json"));
        File.Delete(Path.Combine(snapDir, "games.2.json"));
        string legacySnap = Path.Combine(snapDir,
            "games_" + DateTime.Now.AddMinutes(-30).ToString("yyyyMMdd_HHmmssfff") + ".json");
        File.Copy(Path.Combine(vault10, "games.json"), legacySnap);
        File.WriteAllText(Path.Combine(vault10, "games.json"), "{broken-again");
        Store.Init();
        Check("自留底: 双槽缺失时从存量时间戳快照兜底恢复",
            Store.Games.Count == 1 && Store.Games[0].AppId == 424242
            && Store.RestoredGamesSnapshot == Path.GetFileName(legacySnap),
            $"snap={Store.RestoredGamesSnapshot}");

        // 丢失恢复：删掉 games.json 后重新 Init → 从快照恢复（首次使用无快照则空库，不误伤）
        File.Delete(Path.Combine(vault10, "games.json"));
        Store.Init();
        Check("自留底: games.json 丢失后自动从快照恢复",
            Store.Games.Count == 1 && Store.RestoredGamesSnapshot != null,
            $"n={Store.Games.Count}");

        // 设置留底与损坏恢复
        Store.Settings.MaxKeepPerGame = 33;
        Store.SaveSettings();
        Check("自留底: SaveSettings 生成 settings 双槽副本",
            File.Exists(Path.Combine(snapDir, "settings.1.json"))
            || File.Exists(Path.Combine(snapDir, "settings.2.json")), "no settings snap");
        File.WriteAllText(Path.Combine(vault10, "settings.json"), "***坏的***");
        Store.Init();
        Check("自留底: settings.json 损坏后恢复自定义值",
            Store.Settings.MaxKeepPerGame == 33 && Store.RestoredSettingsSnapshot != null
            && Store.CorruptSettings,
            $"max={Store.Settings.MaxKeepPerGame},snap={Store.RestoredSettingsSnapshot}");

        // 全新环境：没有任何快照时空库启动、不报错
        string vault10b = Path.Combine(temp, "vault10b");
        Store.HomeDirOverride = vault10b;
        Store.Init();
        Check("自留底: 首次使用无快照时空库正常启动",
            Store.Games.Count == 0 && Store.RestoredGamesSnapshot == null
            && !Directory.Exists(Path.Combine(vault10b, "games.json")), "fresh");

        // 旧版玩家镜像根 backups\profiles 一次性迁到 _appdata\profiles
        string oldProfRoot = Path.Combine(Store.BackupRoot, "profiles");
        string oldGuidDir = Path.Combine(oldProfRoot, "migrid000000000000000000000001");
        Directory.CreateDirectory(Path.Combine(oldGuidDir, "小明"));
        File.WriteAllText(Path.Combine(oldGuidDir, "game.json"), """{"Id":"migrid000000000000000000000001","DisplayName":"迁移根游戏"}""");
        ProfileService.MigrateLegacyProfileRoot();
        string newGuidDir = Path.Combine(ProfileService.ProfilesRoot, "migrid000000000000000000000001");
        Check("自留底: 旧 profiles 根整体迁入 _appdata\\profiles（含身份证与玩家目录）",
            Directory.Exists(Path.Combine(newGuidDir, "小明"))
            && File.Exists(Path.Combine(newGuidDir, "game.json"))
            && !Directory.Exists(oldProfRoot),
            $"new={Directory.Exists(newGuidDir)},old={Directory.Exists(oldProfRoot)}");
        ProfileService.MigrateLegacyProfileRoot();   // 幂等：旧根已不存在时不报错
        Check("自留底: 镜像根迁移幂等", Directory.Exists(newGuidDir), "idempotent");

        // AppData 被删、备份在自定义盘：重指备份目录后 TryRestoreEmptyLibrary 补恢复
        string vault11 = Path.Combine(temp, "vault11");
        string customBak = Path.Combine(temp, "custom_bak_root");
        Store.HomeDirOverride = vault11;
        Store.Init();
        Store.Settings.BackupDir = customBak;
        Store.SaveSettings();
        Store.Games.Add(new GameEntry { NameCN = "自定义盘游戏" });
        Store.Save();
        string customSnap = Path.Combine(customBak, Store.AppSnapshotDir);
        Check("自留底: 自定义备份根下生成双槽副本",
            File.Exists(Path.Combine(customSnap, "games.1.json"))
            || File.Exists(Path.Combine(customSnap, "games.2.json")), "no custom snap");
        // 模拟 AppData 目录被清空后重启（设置也丢了，默认根里没有快照）→ 空库
        File.Delete(Path.Combine(vault11, "games.json"));
        File.Delete(Path.Combine(vault11, "settings.json"));
        Store.Init();
        Check("自留底: 数据全丢且未指回自定义盘时空库", Store.Games.Count == 0, $"n={Store.Games.Count}");
        // 设置里重新指回自定义备份目录 → 立即从该盘快照恢复
        Store.Settings.BackupDir = customBak;
        Directory.CreateDirectory(customBak);
        int restored = Store.TryRestoreEmptyLibrary();
        Check("自留底: 重指自定义备份目录后空库从该盘快照恢复",
            restored == 1 && Store.Games.Count == 1 && File.Exists(Path.Combine(vault11, "games.json")),
            $"restored={restored},n={Store.Games.Count}");
        Check("自留底: 库非空时补恢复绝不覆盖现有数据",
            Store.TryRestoreEmptyLibrary() == 0, "overwrite!");
    }

    // ── 22. 从备份重建：manifest 路径回填 / 空库一键重建（备份+玩家+激活玩家）/ 不误报找回 ──
    {
        string vault12 = Path.Combine(temp, "vault12");
        Store.HomeDirOverride = vault12;
        Store.Init();
        string rbSave = Path.Combine(temp, "rebuild_saves");
        Directory.CreateDirectory(rbSave);
        File.WriteAllText(Path.Combine(rbSave, "save1.dat"), "x");

        var gR = new GameEntry { NameCN = "重建测试游戏" };
        gR.SavePaths.Add(new SavePathEntry { Path = rbSave });
        Store.Games.Add(gR);
        ProfileService.CreatePlayer(gR, "小明");   // 锚点 zip（内容 x）
        File.WriteAllText(Path.Combine(rbSave, "save1.dat"), "x2");
        var rbZip = BackupService.Create(gR, "重建前的备份");   // 第二份 zip（有变化才打包）
        string oldProfRoot = ProfileService.RootFor(gR);

        // 回归防护：普通在库游戏每次启动 ReclaimAll 都应返回 0（镜像本来就在位不算找回）
        var (fakeBak, fakeSlot) = ProfileService.ReclaimAll(gR);
        Check("重建: 在库游戏的 ReclaimAll 不误报找回（无启动 toast 噪声）",
            fakeBak == 0 && fakeSlot == 0 && gR.ActiveProfile == "小明",
            $"bak={fakeBak},slot={fakeSlot}");

        // 模拟 games.json 整个丢失且快照也不可用（例如手工删过 _appdata）：zip 与玩家镜像还在
        Store.Games.Remove(gR);
        File.Delete(Path.Combine(vault12, "games.json"));
        // 双槽副本 + 存量时间戳快照都清掉，模拟 _appdata 被手工清空
        string appSnap12 = Path.Combine(vault12, "backups", Store.AppSnapshotDir);
        foreach (var slot in new[] { "games.1.json", "games.2.json" })
            try { File.Delete(Path.Combine(appSnap12, slot)); } catch { }
        foreach (var f in Directory.GetFiles(appSnap12, "games_*.json"))
            File.Delete(f);
        Store.Init();
        Check("重建: 索引与快照皆无时空库启动并标记 GamesFileAbsent",
            Store.Games.Count == 0 && Store.GamesFileAbsent && File.Exists(rbZip.File),
            $"n={Store.Games.Count},absent={Store.GamesFileAbsent}");
        var orphansBefore = BackupService.ListOrphans()
            .Where(o => o.ZipCount > 0 && !o.IsProfileRoot).Select(o => o.Name).ToList();
        Check("重建: 残留列表能看到待重建的游戏备份目录",
            orphansBefore.Contains("重建测试游戏"), string.Join(",", orphansBefore));

        var (games, baks, slots) = BackupService.RebuildLibraryFromBackups();
        Check("重建: 一键重建出 1 张卡片并找回 2 份备份、1 个玩家",
            games == 1 && baks == 2 && slots == 1 && Store.Games.Count == 1,
            $"g={games},b={baks},s={slots},lib={Store.Games.Count}");
        var rebuilt = Store.Games[0];
        string newProfRoot = ProfileService.RootFor(rebuilt);
        Check("重建: 卡片挂着旧 zip、存档路径由 manifest 回填、身份证迁到新 GUID",
            rebuilt.Backups.Count == 2 && rebuilt.Backups.All(b => File.Exists(b.File))
            && rebuilt.SavePaths.Any(p => string.Equals(p.Path, rbSave, StringComparison.OrdinalIgnoreCase))
            && rebuilt.SavePaths[0].Source == "旧备份"
            && File.Exists(Path.Combine(newProfRoot, "game.json"))
            && !Directory.Exists(oldProfRoot),
            $"paths={rebuilt.SavePaths.Count}");
        Check("重建: 激活玩家随身份证恢复、镜像残留全部清零",
            rebuilt.ActiveProfile == "小明"
            && !BackupService.ListOrphans().Any(o => o.IsProfileRoot),
            $"active={rebuilt.ActiveProfile}");
        Check("重建: 新卡片无 exe（未安装态），等待扫描补全",
            string.IsNullOrWhiteSpace(rebuilt.ExePath), rebuilt.ExePath);

        // 路径回填单独验证：全新同名条目（无玩家）也能从最新 manifest 拿回路径
        string rb2Save = Path.Combine(temp, "rebuild_saves2");
        Directory.CreateDirectory(rb2Save);
        var gPf = new GameEntry { NameCN = "纯路径回填游戏" };
        gPf.SavePaths.Add(new SavePathEntry { Path = rb2Save });
        Store.Games.Add(gPf);
        File.WriteAllText(Path.Combine(rb2Save, "a.sav"), "y");
        BackupService.Create(gPf, "一份备份");
        Store.Games.Remove(gPf);
        var gPf2 = new GameEntry { NameCN = "纯路径回填游戏" };
        Store.Games.Add(gPf2);
        int reN = BackupService.ReclaimOrphanBackups(gPf2, new Dictionary<string, string>());
        Check("重建: 无存档路径的新条目找回备份时自动回填路径（尊重移除名单）",
            reN == 1 && gPf2.SavePaths.Any(p =>
                string.Equals(p.Path, rb2Save, StringComparison.OrdinalIgnoreCase)),
            $"n={reN},paths={gPf2.SavePaths.Count}");
    }

    // ── 23. 自定义备份盘重指：旧版 profiles\ 迁移到 _appdata\profiles 后按身份证回迁（幂等）──
    {
        string vault13 = Path.Combine(temp, "vault13");
        Store.HomeDirOverride = vault13;
        Store.Init();

        // 场景：AppData 被删，备份一直在自定义盘，且盘上是旧版裸 profiles\ 结构（从未被新版指过）
        string disk = Path.Combine(temp, "v13disk");
        Store.Settings.BackupDir = disk;
        Store.SaveSettings();

        var gGhost = new GameEntry { NameCN = "自定义盘游戏" };
        string v13Live = Path.Combine(temp, "v13_live_saves");   // slot.json 记录的当年真实存档路径（不存在亦可）
        string legacyRoot = Path.Combine(disk, "profiles", gGhost.Id);
        string xwSlot = Path.Combine(legacyRoot, "小王");
        Directory.CreateDirectory(Path.Combine(xwSlot, "p0"));
        File.WriteAllText(Path.Combine(xwSlot, "p0", "save.dat"), "v");
        File.WriteAllText(Path.Combine(xwSlot, "slot.json"),
            "{\"Name\":\"小王\",\"Created\":\"2026-01-01T00:00:00\",\"Paths\":[\""
            + v13Live.Replace("\\", "\\\\") + "\"]}");
        File.WriteAllText(Path.Combine(legacyRoot, "game.json"),
            "{\"Id\":\"" + gGhost.Id + "\",\"NameCN\":\"自定义盘游戏\",\"NameEN\":\"\"," +
            "\"DisplayName\":\"自定义盘游戏\",\"ActiveProfile\":\"小王\"}");

        // 重指后先迁移旧根（设置窗口回调顺序），再由 Reclaim 按名回迁
        ProfileService.MigrateLegacyProfileRoot();
        string movedRoot = Path.Combine(disk, Store.AppSnapshotDir, "profiles", gGhost.Id);
        Check("自定义盘: 旧版 profiles\\{GUID} 迁入 _appdata\\profiles",
            Directory.Exists(Path.Combine(movedRoot, "小王")) && !Directory.Exists(Path.Combine(disk, "profiles")),
            movedRoot);
        ProfileService.MigrateLegacyProfileRoot();
        Check("自定义盘: 旧根迁移幂等（重复执行不报错不重复）",
            Directory.Exists(Path.Combine(movedRoot, "小王")), "idempotent");

        var gBack = new GameEntry { NameCN = "自定义盘游戏" };
        Store.Games.Add(gBack);
        var (bakN, slotN) = ProfileService.ReclaimAll(gBack);
        Check("自定义盘: 回迁到新 GUID、旧镜像补打成 zip、激活玩家恢复",
            slotN == 1 && bakN == 1 && gBack.ActiveProfile == "小王"
            && Directory.GetFiles(BackupService.PlayerBackupDir(gBack, "小王"), "*.zip").Length == 1
            && !Directory.Exists(Path.Combine(ProfileService.RootFor(gBack), "小王"))
            && File.Exists(Path.Combine(ProfileService.RootFor(gBack), "game.json"))
            && !Directory.Exists(movedRoot),
            $"bak={bakN},slot={slotN},active={gBack.ActiveProfile}");
        Check("自定义盘: _appdata 不出现在残留备份列表",
            !BackupService.ListOrphans().Any(o => o.Name == Store.AppSnapshotDir), "orphan leak");

        Store.Settings.BackupDir = "";
    }

    Store.HomeDirOverride = null;

    // ── 24. 数据目录环境变量入口 GSM_VAULT_DIR（便携/隔离演练接缝；未设置时零影响）──
    {
        string envVault = Path.Combine(temp, "vault_env");
        Environment.SetEnvironmentVariable("GSM_VAULT_DIR", envVault);
        Check("环境变量: 设置 GSM_VAULT_DIR 后 BaseDir 重定向（HomeDirOverride 优先）",
            Store.BaseDir == envVault, Store.BaseDir);
        // 空洞窗口（env="" 且 override=null 时 BaseDir 指向真实 %APPDATA%）的沙箱文件指纹：
        // 窗口内任何后台写盘都可能落进真实库，窗口前后指纹必须完全一致
        string sandboxGames = Path.Combine(sandboxHome, "games.json");
        string sandboxSettings = Path.Combine(sandboxHome, "settings.json");
        string gamesFpBefore = FileFingerprint(sandboxGames);
        string settingsFpBefore = FileFingerprint(sandboxSettings);
        Environment.SetEnvironmentVariable("GSM_VAULT_DIR", "");
        string? got = Store.BaseDir;   // 空白视同未设置
        Check("环境变量: GSM_VAULT_DIR 为空时回退默认 %APPDATA%",
            got == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameSaveVault"),
            got);
        Check("环境变量: 空洞窗口内沙箱 games.json/settings.json 零写盘（真实库同样不受影响）",
            FileFingerprint(sandboxGames) == gamesFpBefore && FileFingerprint(sandboxSettings) == settingsFpBefore,
            $"games:{gamesFpBefore}->{FileFingerprint(sandboxGames)} settings:{settingsFpBefore}->{FileFingerprint(sandboxSettings)}");
        // 恢复全局沙箱兜底（不能留 null：之后的区块在 HomeDirOverride 复位窗口里会写真实库）
        Environment.SetEnvironmentVariable("GSM_VAULT_DIR", sandboxHome);
    }

    // ── 25. Beta 审计修复：迁移幂等 / 并发保存 / 严格还原边界 ──
    {
        string vault14 = Path.Combine(temp, "vault14");
        Store.HomeDirOverride = vault14;
        Store.Init();

        // 25.1 ResetId 代号条目：首次纠正可清路径，之后启动不得再清空用户路径（M1）
        var gAw = new GameEntry { Type = "Local", ExePath = @"D:\games\AlanWake2\AlanWake2.exe",
            NameCN = "", NameEN = "", AppId = 3274290 };
        gAw.SavePaths.Add(new SavePathEntry { Path = @"C:\Users\tester\Documents\手动存档", Source = "手动" });
        Store.Games.Add(gAw);
        DataMigration.FixScannedEntries();
        Check("迁移: 代号条目首次被纠正（名称/AppId/清空误探路径）",
            gAw.NameCN == "心灵杀手2" && gAw.NameEN == "Alan Wake 2" && gAw.AppId == 0
            && gAw.SavePaths.Count == 0,
            $"{gAw.NameCN}|{gAw.NameEN}|app={gAw.AppId}|paths={gAw.SavePaths.Count}");
        // 模拟用户重新补好路径（含手动来源）
        gAw.SavePaths.Add(new SavePathEntry { Path = @"C:\AW2\saves", Source = "手动" });
        DataMigration.FixScannedEntries();
        Check("迁移: 已纠正条目再次启动不再清空存档路径（幂等）",
            gAw.SavePaths.Count == 1 && gAw.SavePaths[0].Path == @"C:\AW2\saves",
            $"paths={gAw.SavePaths.Count}");

        // lotf2：钉死 appid 后再次执行，手动路径同样保留（AppIdLocked 是首次纠正时持久化的）
        var gL = new GameEntry { Type = "Local", ExePath = @"D:\LOTF2\LOTF2.exe",
            NameCN = "堕落之主", NameEN = "Lords of the Fallen (2023)", AppId = 1501750, AppIdLocked = true };
        gL.SavePaths.Add(new SavePathEntry { Path = @"C:\LOTF2\save" });
        Store.Games.Add(gL);
        DataMigration.FixScannedEntries();
        Check("迁移: 钉死 appid 条目二次启动保留路径并锁定 AppId",
            gL.AppId == 1501750 && gL.AppIdLocked && gL.SavePaths.Count == 1,
            $"app={gL.AppId},locked={gL.AppIdLocked},paths={gL.SavePaths.Count}");

        // 25.2 多线程并发 Store.Save：文件必须始终是合法 JSON，且无 .tmp 残留（H1）
        var saveTasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 25; i++) Store.Save();
        })).ToArray();
        Task.WaitAll(saveTasks);
        bool jsonOk = true; int savedCount = -1;
        try
        {
            var list = JsonSerializer.Deserialize<List<GameEntry>>(File.ReadAllText(Store.GamesFile));
            savedCount = list?.Count ?? -1;
        }
        catch { jsonOk = false; }
        Check("并发: 16 线程并发 Save 后 games.json 仍为合法 JSON",
            jsonOk && savedCount == Store.Games.Count, $"jsonOk={jsonOk},n={savedCount}");
        Check("并发: 目录无 .tmp 临时文件残留",
            !Directory.GetFiles(Store.BaseDir, "*.tmp*").Any(),
            string.Join(",", Directory.GetFiles(Store.BaseDir, "*.tmp*")));

        // 25.3 严格还原只清理当前仍在配置内的快照目录（H2）
        string dirA = Path.Combine(temp, "h2_dirA");
        string dirB = Path.Combine(temp, "h2_dirB");
        Directory.CreateDirectory(dirA);
        File.WriteAllText(Path.Combine(dirA, "keep.txt"), "v1");
        var gH = new GameEntry { NameEN = "H2Purge" };
        gH.SavePaths.Add(new SavePathEntry { Path = dirA });
        var bH = BackupService.Create(gH, "全量");
        // 备份后：A 中多出"快照外新文件"；用户把存档位置改到 B，B 中有无关重要文件
        File.WriteAllText(Path.Combine(dirA, "extra.txt"), "new");
        Directory.CreateDirectory(dirB);
        File.WriteAllText(Path.Combine(dirB, "important.txt"), "mine");
        gH.SavePaths.Clear();
        gH.SavePaths.Add(new SavePathEntry { Path = dirB });
        BackupService.Restore(gH, bH, purgeNewFiles: true);
        Check("严格还原: 不再配置的旧目录 A 中额外文件不被删除",
            File.Exists(Path.Combine(dirA, "extra.txt")), "extra.txt was purged");
        Check("严格还原: 当前无关目录 B 完全不受影响",
            File.Exists(Path.Combine(dirB, "important.txt")), "important.txt lost");
        Check("严格还原: 快照内文件正常还原",
            File.Exists(Path.Combine(dirA, "keep.txt")), "keep.txt missing");

        // 25.4 手动封面命名归一化：custom_{gameId}.* → 官方下载同款 {appId}.jpg
        Directory.CreateDirectory(Store.CoversDir);
        var gCover = new GameEntry { NameCN = "封面迁移测试", AppId = 555001 };
        string oldCover = Path.Combine(Store.CoversDir, "custom_" + gCover.Id + ".jpg");
        File.WriteAllBytes(oldCover, new byte[] { 1, 2, 3, 4, 5 });
        gCover.CoverFile = oldCover;
        Store.Games.Add(gCover);
        string orphan = Path.Combine(Store.CoversDir, "custom_orphan.jpg");
        File.WriteAllBytes(orphan, new byte[] { 9 });
        var gNoApp = new GameEntry { NameCN = "学习版无AppId" };
        string keepCover = Path.Combine(Store.CoversDir, "custom_" + gNoApp.Id + ".png");
        File.WriteAllBytes(keepCover, new byte[] { 7, 8 });
        gNoApp.CoverFile = keepCover;
        Store.Games.Add(gNoApp);
        Store.Save();

        var coverLog = DataMigration.NormalizeCustomCovers();
        string canonicalCover = Store.CoverFileFor(555001);
        Check("迁移: 手动封面改名为官方同款 {appId}.jpg 且内容一致",
            File.Exists(canonicalCover)
            && File.ReadAllBytes(canonicalCover).SequenceEqual(new byte[] { 1, 2, 3, 4, 5 })
            && gCover.CoverFile == canonicalCover, $"ref={gCover.CoverFile}");
        Check("迁移: 旧 custom_ 封面文件已删除", !File.Exists(oldCover), oldCover);
        Check("迁移: 无引用的 custom_ 孤儿被清理", !File.Exists(orphan), "orphan kept");
        Check("迁移: AppId=0 的学习版封面保留原名",
            File.Exists(keepCover) && gNoApp.CoverFile == keepCover, gNoApp.CoverFile);
        var coverLog2 = DataMigration.NormalizeCustomCovers();
        Check("迁移: 封面归一化幂等（二次执行零改动）", coverLog2.Count == 0, $"again={coverLog2.Count}");

        Store.HomeDirOverride = null;
    }

    // ── 25.5 归档残留删除联动 + 共用封面保护 + # 路径转码 ──
    {
        string vaultArc = Path.Combine(temp, "vaultArc");
        Store.HomeDirOverride = vaultArc;
        Store.Init();

        // 归档留档游戏：备份目录在「未关联备份」里被删时，条目上的死记录/指针必须同步清掉
        string arcSaves = Path.Combine(temp, "arc_saves");
        Directory.CreateDirectory(arcSaves);
        File.WriteAllText(Path.Combine(arcSaves, "s.sav"), "arc");
        var gArc = new GameEntry { NameCN = "归档删档测试" };
        gArc.SavePaths.Add(new SavePathEntry { Path = arcSaves });
        Store.Games.Add(gArc);
        // 先有一份通用备份，再建玩家：玩家会认领这份备份（顺序反了会撞「没有变化」）
        var bArc = BackupService.Create(gArc, "归档前的备份");
        ProfileService.CreatePlayer(gArc, "豪子");
        gArc.SwitchAnchors["豪子"] = bArc.FileJson;
        gArc.FavoriteWhy["豪子"] = "auto";
        string arcBackupDir = BackupService.BackupDirFor(gArc);
        gArc.Archived = true;
        gArc.CompletedAt = new DateTime(2026, 9, 1);
        Store.Save();
        Check("归档: 归档游戏备份目录在未关联备份窗口可见",
            BackupService.ListOrphans().Any(o => o.Dir.Equals(arcBackupDir, StringComparison.OrdinalIgnoreCase)),
            arcBackupDir);
        BackupService.DeleteOrphan(arcBackupDir);
        Check("归档: 残留删除后条目死备份记录一并清除", gArc.Backups.Count == 0, $"n={gArc.Backups.Count}");
        Check("归档: 可见备份列表同步清空（复活后不出幽灵行）",
            gArc.VisibleBackups.Count == 0, $"n={gArc.VisibleBackups.Count}");
        Check("归档: 指向死包的心目中指针一并摘除",
            !gArc.SwitchAnchors.ContainsKey("豪子") && !gArc.FavoriteWhy.ContainsKey("豪子"), "anchor kept");
        var reloaded = JsonSerializer.Deserialize<List<GameEntry>>(File.ReadAllText(Path.Combine(vaultArc, "games.json")))!;
        var arcReloaded = reloaded.First(g => g.NameCN == "归档删档测试");
        Check("归档: 死记录清除已落盘", arcReloaded.Backups.Count == 0
            && !arcReloaded.SwitchAnchors.ContainsKey("豪子"), $"{arcReloaded.Backups.Count}");

        // 对照：在库游戏的记录不受其他残留目录删除影响
        string liveSaves = Path.Combine(temp, "live_saves");
        Directory.CreateDirectory(liveSaves);
        File.WriteAllText(Path.Combine(liveSaves, "l.sav"), "live");
        var gLive = new GameEntry { NameCN = "在库游戏别误伤" };
        gLive.SavePaths.Add(new SavePathEntry { Path = liveSaves });
        Store.Games.Add(gLive);
        var bLive = BackupService.Create(gLive, "在库备份");
        string orphanDir = Path.Combine(Store.BackupRoot, BackupService.SafeName("无主残留游戏"));
        Directory.CreateDirectory(orphanDir);
        File.WriteAllText(Path.Combine(orphanDir, "old.zip"), "z");
        BackupService.DeleteOrphan(orphanDir);
        Check("归档: 删无主残留不误伤在库游戏的备份记录与文件",
            gLive.Backups.Count == 1 && File.Exists(bLive.File) && !Directory.Exists(orphanDir), "");

        // 共用缓存图片保护：双装/同 AppId 两条目共享 {appId}.jpg
        var gShareA = new GameEntry { NameCN = "双装甲", AppId = 7001 };
        var gShareB = new GameEntry { NameCN = "双装甲副本", AppId = 7001 };
        Directory.CreateDirectory(Store.CoversDir);
        string shared7001 = Store.CoverFileFor(7001);
        File.WriteAllBytes(shared7001, new byte[2000]);
        gShareA.CoverFile = shared7001;
        gShareB.CoverFile = shared7001;
        Store.Games.Add(gShareA);
        Store.Games.Add(gShareB);
        Check("共用封面: A 视角 7001.jpg 正被 B 引用",
            Store.IsImageReferencedByOthers(shared7001, gShareA.Id), "");
        gShareB.CoverFile = "";
        gShareB.PosterFile = shared7001;
        Check("共用封面: 海报字段引用也算被引用",
            Store.IsImageReferencedByOthers(shared7001, gShareA.Id), "");
        gShareB.PosterFile = "";
        // 此刻只有 A 引用：排除 A 后无人引用（可安全删图）；不排除 A 时仍判定被引用
        Check("共用封面: 解绑后引用判定准确（排除属主即无人引用）",
            !Store.IsImageReferencedByOthers(shared7001, gShareA.Id)
            && Store.IsImageReferencedByOthers(shared7001, "不排除任何人"), "");
        // 双装场景下归一化迁移：B 的 custom_ 私有图不得改名覆盖 A 正用的 7001.jpg
        gShareB.CoverFile = shared7001;
        string customB = Path.Combine(Store.CoversDir, "custom_" + gShareB.Id + ".jpg");
        File.WriteAllBytes(customB, new byte[] { 7, 7, 7 });
        gShareB.CoverFile = customB;
        var shareLog = DataMigration.NormalizeCustomCovers();
        Check("共用封面: 迁移跳过被另一条目占用的 {appId}.jpg，私有图保留",
            File.ReadAllBytes(shared7001).SequenceEqual(new byte[2000])
            && gShareB.CoverFile == customB && File.Exists(customB),
            $"ref={gShareB.CoverFile},log={shareLog.Count}");

        // 含 # 路径的图片转 JPEG（UriSource 会截断路径，必须走文件流）
        string hashDir = Path.Combine(temp, "图#集 2026");
        Directory.CreateDirectory(hashDir);
        string pngInHash = Path.Combine(hashDir, "cover #1.png");
        File.WriteAllBytes(pngInHash, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        string jpgOut = Path.Combine(hashDir, "out.jpg");
        DataMigration.EncodeJpeg(pngInHash, jpgOut);
        byte[] jpgHead = File.Exists(jpgOut) ? File.ReadAllBytes(jpgOut).Take(2).ToArray() : new byte[2];
        Check("转码: 含 # 路径的 PNG 可转成真 JPEG（FF D8 文件头）",
            new FileInfo(jpgOut).Length > 100 && jpgHead[0] == 0xFF && jpgHead[1] == 0xD8,
            $"len={(File.Exists(jpgOut) ? new FileInfo(jpgOut).Length : 0)}");
        // 后台 STA 包装可正常执行（当前为幂等态，零改动返回）
        var asyncLog = DataMigration.NormalizeCustomCoversAsync().GetAwaiter().GetResult();
        Check("迁移: 后台 STA 异步包装执行成功（幂等态零改动）", asyncLog.Count == 0, $"n={asyncLog.Count}");

        Store.HomeDirOverride = null;
    }

    // ── 26. 运行中热备份与定时快照 ──
    {
        string vault15 = Path.Combine(temp, "vault15");
        Store.HomeDirOverride = vault15;
        Store.Init();
        Store.Settings.IncrementalEnabled = true;
        Store.Settings.MaxKeepPerGame = 3;   // 供 Prune 用例
        Store.Settings.FullEveryN = 5;

        string hotDir = Path.Combine(temp, "hot_saves");
        Directory.CreateDirectory(hotDir);
        File.WriteAllText(Path.Combine(hotDir, "s1.txt"), "v1");
        File.WriteAllText(Path.Combine(hotDir, "s2.txt"), "v1");

        var gHot = new GameEntry { NameEN = "HotGame" };
        gHot.SavePaths.Add(new SavePathEntry { Path = hotDir });

        BackupManifest ManifestOf26(BackupRecord r)
        {
            using var z = System.IO.Compression.ZipFile.OpenRead(r.File);
            using var rd = new StreamReader(z.GetEntry("manifest.json")!.Open());
            return System.Text.Json.JsonSerializer.Deserialize<BackupManifest>(rd.ReadToEnd())!;
        }

        // 26.1 运行中（ExePath 指向测试进程自身 exe → ProcessName=SmokeTest）普通备份仍被拦截
        gHot.ExePath = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;
        bool blocked = false;
        try { BackupService.Create(gHot, "手动备份"); }
        catch (InvalidOperationException ex) { blocked = ex.Message.Contains("游戏正在运行"); }
        Check("热备份: 运行中普通备份仍被拦截", blocked, "no exception or wrong message");

        // 26.2 热备份成功：独立全量 + Hot 标记，无占用文件时 skipped=0
        var bh = BackupService.Create(gHot, "运行中快照", hot: true, out int sk0);
        Check("热备份: 成功且为独立全量", bh.Kind == "full" && bh.BaseFile == "" && bh.Hot,
            $"{bh.Kind}|hot={bh.Hot}|base={bh.BaseFile}");
        Check("热备份: 无占用文件时 skipped=0", sk0 == 0, sk0.ToString());

        // 26.3 独占锁定的文件被剔除：zip 内容与 manifest.Files 均不含该文件
        BackupRecord bh2;
        int skipped26;
        using (var lockFs = new FileStream(Path.Combine(hotDir, "s2.txt"),
                   FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            File.WriteAllText(Path.Combine(hotDir, "s1.txt"), "v22");   // 保证有变化内容
            bh2 = BackupService.Create(gHot, "运行中快照", hot: true, out skipped26);
            List<string> zipNames26;
            using (var z = System.IO.Compression.ZipFile.OpenRead(bh2.File))
                zipNames26 = z.Entries.Select(e => e.FullName).ToList();
            Check("热备份: 占用文件被剔除出包与清单并计数", skipped26 == 1 &&
                !zipNames26.Contains("p0/s2.txt") && !ManifestOf26(bh2).Files.Any(f => f.N == "p0/s2.txt"),
                $"skipped={skipped26}, entries={string.Join(",", zipNames26)}");
            Check("热备份: 未占用文件正常打包",
                zipNames26.Contains("p0/s1.txt") && ManifestOf26(bh2).Files.Any(f => f.N == "p0/s1.txt"),
                string.Join(",", zipNames26));
        }
        // （以下用例必须在文件锁释放后执行，模拟游戏正常退出后的磁盘状态）
        var oldestHot = bh;   // bh/bh2 均为热快照，bh 最旧（Prune 用例引用）

        // 26.4 退出游戏后基于热快照做冷增量：链完整（s2 重新入包，s3 新增）
        gHot.ExePath = "";
        File.WriteAllText(Path.Combine(hotDir, "s3.txt"), "v3");
        var bc = BackupService.Create(gHot, "自动备份（游戏退出）");
        Check("热备份: 退出后冷增量以最新热快照为基",
            bc.Kind == "incr" && bc.BaseFile == Path.GetFileName(bh2.File),
            $"{bc.Kind}|{bc.BaseFile}");

        // 26.5 热快照无变化 → "没有变化"（定时快照静默路径依赖此异常）
        bool noChange = false;
        try { BackupService.Create(gHot, "运行中快照", hot: true, out _); }
        catch (InvalidOperationException ex) { noChange = ex.Message.Contains("没有变化"); }
        Check("热备份: 无变化时提示无需备份", noChange, "no exception or wrong message");

        // 26.6 Prune 超限时优先淘汰最旧热快照（MaxKeep=3：[bc2, bc, bh2, bh] → 删 bh）
        File.WriteAllText(Path.Combine(hotDir, "s4.txt"), "v4");
        var bc2 = BackupService.Create(gHot, "手动备份");
        Check("Prune: 保留数超限时优先淘汰最旧热快照",
            gHot.Backups.Count == 3 && !gHot.Backups.Contains(oldestHot) &&
            !File.Exists(oldestHot.File) && gHot.Backups.Contains(bh2) && gHot.Backups.Contains(bc),
            $"n={gHot.Backups.Count}, oldestHotFileGone={!File.Exists(oldestHot.File)}");

        Store.HomeDirOverride = null;
    }

    // ── 27. 玩家"游戏内最后保存时间"：只切换不开游戏时间不变 / 真正游玩保存才推进 / 空镜像 null / 改名保留 / Created 稳定 ──
    {
        string vault27 = Path.Combine(temp, "vault27");
        Store.HomeDirOverride = vault27;
        Store.Init();

        string capDir = Path.Combine(temp, "cap_saves");
        Directory.CreateDirectory(capDir);
        string capFile = Path.Combine(capDir, "s.txt");
        File.WriteAllText(capFile, "p1-start");
        DateTime tP1Start = File.GetLastWriteTime(capFile);
        var gCap = new GameEntry { NameCN = "保存时间测试游戏" };
        gCap.SavePaths.Add(new SavePathEntry { Path = capDir });

        ProfileService.CreatePlayer(gCap, "玩家1");
        gCap.RefreshProfiles();
        Check("保存时间: 新建玩家的保存时间=存档文件修改时间",
            ProfileService.LastProgressSaved(gCap, "玩家1") == tP1Start,
            $"got={ProfileService.LastProgressSaved(gCap, "玩家1")},want={tP1Start}");
        Check("保存时间: live 当前进度时间=游戏目录内存档文件修改时间",
            ProfileService.LiveProgressSaved(gCap) == tP1Start,
            $"got={ProfileService.LiveProgressSaved(gCap)},want={tP1Start}");

        // 不开游戏，隔一会儿分出玩家2：玩家1的保存时间不得变动（用户恐慌场景的核心断言）
        Thread.Sleep(50);
        ProfileService.CreatePlayer(gCap, "玩家2");
        gCap.RefreshProfiles();
        Check("保存时间: 空切换（不玩游戏）不改变玩家1的游戏保存时间",
            ProfileService.LastProgressSaved(gCap, "玩家1") == tP1Start,
            $"got={ProfileService.LastProgressSaved(gCap, "玩家1")},want={tP1Start}");
        Check("保存时间: 玩家2尚未游玩备份，保存时间为空（虚拟承接不产生记录）",
            ProfileService.LastProgressSaved(gCap, "玩家2") == null,
            $"got={ProfileService.LastProgressSaved(gCap, "玩家2")}");

        // 玩家2真正在游戏里保存了一次（文件被游戏重写）→ 切回玩家1
        Thread.Sleep(50);
        File.WriteAllText(capFile, "p2-played");
        DateTime tP2Saved = File.GetLastWriteTime(capFile);
        Check("保存时间: 当前玩家游玩保存后 live 时间立即推进（无需切换）",
            ProfileService.LiveProgressSaved(gCap) == tP2Saved,
            $"got={ProfileService.LiveProgressSaved(gCap)},want={tP2Saved}");
        ProfileService.SwitchTo(gCap, "玩家1");
        gCap.RefreshProfiles();
        Check("保存时间: 切回玩家1后 live 已恢复为他的版本（live 时间回到他的保存时间）",
            ProfileService.LiveProgressSaved(gCap) == tP1Start,
            $"got={ProfileService.LiveProgressSaved(gCap)},want={tP1Start}");
        Check("保存时间: 玩家2游玩保存后，他的保存时间推进到新文件时间",
            ProfileService.LastProgressSaved(gCap, "玩家2") == tP2Saved,
            $"got={ProfileService.LastProgressSaved(gCap, "玩家2")},want={tP2Saved}");
        Check("保存时间: 切回玩家1后他的保存时间仍是最初时间（live 已恢复为他的档）",
            ProfileService.LastProgressSaved(gCap, "玩家1") == tP1Start,
            $"got={ProfileService.LastProgressSaved(gCap, "玩家1")},want={tP1Start}");

        // 连续空切换（用户隔几分钟点来切去）：双方保存时间都纹丝不动
        Thread.Sleep(50);
        ProfileService.SwitchTo(gCap, "玩家2");
        Thread.Sleep(50);
        ProfileService.SwitchTo(gCap, "玩家1");
        gCap.RefreshProfiles();
        Check("保存时间: 连续空切换后玩家1/玩家2保存时间均不变",
            ProfileService.LastProgressSaved(gCap, "玩家1") == tP1Start
            && ProfileService.LastProgressSaved(gCap, "玩家2") == tP2Saved,
            $"p1={ProfileService.LastProgressSaved(gCap, "玩家1")},p2={ProfileService.LastProgressSaved(gCap, "玩家2")}");

        // 玩家1接着玩：保存后再切走，他的保存时间才推进
        Thread.Sleep(50);
        File.WriteAllText(capFile, "p1-again");
        DateTime tP1Again = File.GetLastWriteTime(capFile);
        ProfileService.SwitchTo(gCap, "玩家2");
        gCap.RefreshProfiles();
        Check("保存时间: 玩家1再次游玩保存后，切走时他的保存时间推进",
            ProfileService.LastProgressSaved(gCap, "玩家1") == tP1Again,
            $"got={ProfileService.LastProgressSaved(gCap, "玩家1")},want={tP1Again}");

        // 玩家列表顺序由各自最早备份时间决定（≈创建顺序），多次切换后筹码顺序不漂移
        ProfileService.SwitchTo(gCap, "玩家1");
        Thread.Sleep(20);
        ProfileService.SwitchTo(gCap, "玩家2");
        gCap.RefreshProfiles();
        Check("保存时间: 多次切换后玩家列表顺序稳定",
            gCap.ProfileSlots.Count == 2 && gCap.ProfileSlots[0].Name == "玩家1"
            && gCap.ProfileSlots[1].Name == "玩家2",
            string.Join(",", gCap.ProfileSlots.Select(v => v.Name)));

        // 改名：备份归属与玩家子目录同步改名，保存时间保留
        ProfileService.RenamePlayer(gCap, "玩家2", "玩家二");
        gCap.RefreshProfiles();
        Check("保存时间: 改名后保存时间保留",
            ProfileService.LastProgressSaved(gCap, "玩家二") == tP2Saved
            && ProfileService.LastProgressSaved(gCap, "玩家2") is null,
            $"got={ProfileService.LastProgressSaved(gCap, "玩家二")}");

        // 全新玩家、存档目录为空（游戏还没生成过存档）→ null（UI 显示"全新进度"）
        string emptyDir = Path.Combine(temp, "cap_empty_saves");
        Directory.CreateDirectory(emptyDir);
        var gEmpty = new GameEntry { NameCN = "空存档测试游戏" };
        gEmpty.SavePaths.Add(new SavePathEntry { Path = emptyDir });
        ProfileService.CreatePlayer(gEmpty, "新手");
        Check("保存时间: 空存档镜像返回 null（全新进度）",
            ProfileService.LastProgressSaved(gEmpty, "新手") is null,
            ProfileService.LastProgressSaved(gEmpty, "新手")?.ToString() ?? "null");
        gEmpty.RefreshProfiles();
        Check("保存时间: 空镜像游戏刷新后筹码仍正常列出",
            gEmpty.ProfileSlots.Count == 1 && gEmpty.ProfileSlots[0].Name == "新手",
            $"{gEmpty.ProfileSlots.Count} chips");

        Store.HomeDirOverride = null;
    }

    // ── 28. 全局热备份快捷键：组合键解析 / 规范格式化 / 安全约束 / 设置默认值 ──
    {
        // Parse：默认组合
        var (m1, v1, ok1) = GlobalHotKeyService.Parse("Ctrl+Shift+B");
        Check("热键: 默认 Ctrl+Shift+B 解析", ok1 && m1 == 0x0006 && v1 == 0x42, $"ok={ok1},m={m1},v={v1}");
        // Parse：F 键 + Alt
        var (m2, v2, ok2) = GlobalHotKeyService.Parse("Alt+F5");
        Check("热键: Alt+F5 解析为虚拟键码", ok2 && m2 == 0x0001 && v2 == 0x74, $"ok={ok2},m={m2},v={v2}");
        // Parse：数字
        var (m3, v3, ok3) = GlobalHotKeyService.Parse("Ctrl+5");
        Check("热键: Ctrl+5 解析", ok3 && m3 == 0x0002 && v3 == (uint)'5', $"ok={ok3},m={m3},v={v3}");
        // Parse：空/无效
        Check("热键: 空字符串解析失败", GlobalHotKeyService.Parse("").ok == false, "");
        Check("热键: null 解析失败", GlobalHotKeyService.Parse(null).ok == false, "");
        Check("热键: 非法主键 Ctrl+Xyz 解析失败", GlobalHotKeyService.Parse("Ctrl+Xyz").ok == false, "");
        // Parse：大小写/多余空格容错
        var (m4, v4, ok4) = GlobalHotKeyService.Parse(" ctrl + b ");
        Check("热键: 大小写与空格容错", ok4 && m4 == 0x0002 && v4 == 0x42, $"ok={ok4},m={m4},v={v4}");

        // TryFormat：WPF 按键规范化
        Check("热键: TryFormat 规范顺序 Ctrl+Alt+Shift+Win+B",
            GlobalHotKeyService.TryFormat(Key.B,
                ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows,
                out string cf) && cf == "Ctrl+Alt+Shift+Win+B", cf);
        Check("热键: TryFormat 无修饰键拒绝",
            !GlobalHotKeyService.TryFormat(Key.B, ModifierKeys.None, out _), "");
        Check("热键: TryFormat F 键",
            GlobalHotKeyService.TryFormat(Key.F12, ModifierKeys.Alt, out string f12) && f12 == "Alt+F12", f12);
        Check("热键: TryFormat 数字键 D3",
            GlobalHotKeyService.TryFormat(Key.D3, ModifierKeys.Control, out string d3) && d3 == "Ctrl+3", d3);
        Check("热键: TryFormat 拒绝非字母数字F键（空格）",
            !GlobalHotKeyService.TryFormat(Key.Space, ModifierKeys.Control, out _), "");

        // 设置默认值存在且可注册解析
        var def = new AppSettings();
        var (dm, dv, dok) = GlobalHotKeyService.Parse(def.BackupHotKey);
        Check("热键: 设置默认快捷键可解析且带修饰键",
            !string.IsNullOrEmpty(def.BackupHotKey) && dok && dm != 0 && dv != 0,
            def.BackupHotKey);
    }

    // ── 29. 云同步（WebDAV）：DPAPI 凭据 / 默认配置 / 地址校验 / URL 转义 / 原子写入 / 云端下载认领 ──
    {
        Store.HomeDirOverride = Path.Combine(temp, "vault_cloud");
        Store.Init();

        // 29.1 DPAPI 往返（含中文/表情）
        string plain = "应用密码-pa#ss/😀";
        string enc = CloudSecret.Protect(plain);
        Check("云凭据: DPAPI 加密往返", enc != plain && CloudSecret.Unprotect(enc) == plain, enc);
        Check("云凭据: 空密码得空串", CloudSecret.Protect("") == "" && CloudSecret.Unprotect("") == "");
        Check("云凭据: 脏密文解不开返回空串", CloudSecret.Unprotect("@@not-base64@@") == "");

        // 29.2 默认配置
        var cs = Store.Settings;
        Check("云配置: 默认关闭", cs.CloudEnabled == false);
        Check("云配置: 默认坚果云地址", cs.CloudUrl == "https://dav.jianguoyun.com/dav/", cs.CloudUrl);
        Check("云配置: 默认云端目录", cs.CloudDir == "GameSaveVault", cs.CloudDir);
        Check("云配置: 登记文件位于数据目录",
            Store.CloudStateFile == Path.Combine(Store.BaseDir, "cloud-state.json"), Store.CloudStateFile);

        // 29.3 构造校验
        bool badUrl = false;
        try { _ = new WebDavClient("ftp://dav.example.com/dav", "u", "p", "d"); }
        catch (InvalidOperationException) { badUrl = true; }
        Check("WebDAV: 非 http(s) 地址拒绝", badUrl);
        bool emptyUrl = false;
        try { _ = new WebDavClient("  ", "u", "p", "d"); }
        catch (InvalidOperationException) { emptyUrl = true; }
        Check("WebDAV: 空地址拒绝", emptyUrl);
        var client = new WebDavClient("https://dav.jianguoyun.com/dav/", "user@example.com", "pw", "GameSaveVault");

        // 29.4 URL 拼接：逐段转义、斜杠收敛、保留路径分隔符
        string u1 = client.Url("/游戏 A/玩家#1/存档 2025.zip");
        Check("WebDAV: 中文/空格/# 逐段转义",
            u1 == "https://dav.jianguoyun.com/dav/GameSaveVault/" +
                  Uri.EscapeDataString("游戏 A") + "/" + Uri.EscapeDataString("玩家#1") + "/" +
                  Uri.EscapeDataString("存档 2025.zip"), u1);
        string u2 = client.Url("a//b/");
        Check("WebDAV: 多余斜杠与首尾斜杠收敛",
            u2 == "https://dav.jianguoyun.com/dav/GameSaveVault/a/b", u2);
        Check("WebDAV: 转义后可完整还原",
            Uri.UnescapeDataString(u1) == "https://dav.jianguoyun.com/dav/GameSaveVault/游戏 A/玩家#1/存档 2025.zip",
            Uri.UnescapeDataString(u1));
        var client2 = new WebDavClient("http://nas.local:5005/dav/", "u", "p", "  /My Vault/ ");
        Check("WebDAV: 自定义根目录去首尾斜杠",
            client2.Url("g/x.zip") == "http://nas.local:5005/dav/My%20Vault/g/x.zip", client2.Url("g/x.zip"));

        // 29.5 备份原子写入：无同名 .part 残留 + zip 可读 manifest + 旧 .part 清理
        string cloudSaveDir = Path.Combine(temp, "cloud_saves");
        Directory.CreateDirectory(cloudSaveDir);
        File.WriteAllText(Path.Combine(cloudSaveDir, "save1.txt"), "S1");
        var gCloud = new GameEntry { NameEN = "CloudGame" };
        gCloud.SavePaths.Add(new SavePathEntry { Path = cloudSaveDir });
        var cb1 = BackupService.Create(gCloud, "云备份1");
        bool manifestOk1;
        using (var z = System.IO.Compression.ZipFile.OpenRead(cb1.File))
            manifestOk1 = z.GetEntry("manifest.json") != null;
        Check("原子写入: 备份可正常读出 manifest", manifestOk1 && File.Exists(cb1.File));
        Check("原子写入: 无同名 .part 残留", !File.Exists(cb1.File + ".part"));

        string cbDir = Path.GetDirectoryName(cb1.File)!;
        string stalePart = Path.Combine(cbDir, "crash_old.zip.part");
        File.WriteAllText(stalePart, "x");
        File.SetLastWriteTimeUtc(stalePart, DateTime.UtcNow.AddHours(-2));
        string freshPart = Path.Combine(cbDir, "inflight.zip.part");
        File.WriteAllText(freshPart, "x");   // 刚产生：不得被误删
        File.WriteAllText(Path.Combine(cloudSaveDir, "save1.txt"), "S22");
        var cb2 = BackupService.Create(gCloud, "云备份2");
        Check("原子写入: 清理 1 小时前的 .part 残留", !File.Exists(stalePart));
        Check("原子写入: 新 .part 不被误删", File.Exists(freshPart));
        Check("原子写入: 第二份备份完整无 .part", File.Exists(cb2.File) && !File.Exists(cb2.File + ".part"));
        File.Delete(freshPart);

        // 29.6 云端下载认领：导入 / 重复判重删临时文件 / 损坏包拒绝
        var gOther = new GameEntry { NameEN = "CloudTarget" };
        string dl1 = Path.Combine(temp, "download1.zip");
        File.Copy(cb2.File, dl1);
        var cloudAdopted = BackupService.AdoptDownloadedZip(gOther, dl1);
        Check("云端导入: 记录登记且临时文件移入备份库",
            cloudAdopted != null && cloudAdopted.Note == "云端下载" && cloudAdopted.Profile == "玩家1" &&
            File.Exists(cloudAdopted.File) && !File.Exists(dl1) && gOther.Backups.Count == 1,
            $"note={cloudAdopted?.Note},count={gOther.Backups.Count}");

        string dl2 = Path.Combine(temp, "download2.zip");
        File.Copy(cb2.File, dl2);
        var cloudAdopted2 = BackupService.AdoptDownloadedZip(gOther, dl2);
        Check("云端导入: 同时间+同大小判重并丢弃临时文件",
            ReferenceEquals(cloudAdopted2, cloudAdopted) && gOther.Backups.Count == 1 && !File.Exists(dl2));

        string badZip = Path.Combine(temp, "broken.zip");
        File.WriteAllText(badZip, "this is not a zip");
        bool adoptBad = false;
        try { BackupService.AdoptDownloadedZip(gOther, badZip); }
        catch (InvalidOperationException) { adoptBad = true; }
        Check("云端导入: 损坏 zip 报错", adoptBad && File.Exists(badZip));

        Store.HomeDirOverride = null;
    }

    // ── 30. 按游戏自动云存档：开关持久化 / 旧设置迁移 / 云端预筛 / 云建档与重映射 / 切换玩家 zip 唯一事实源 ──
    {
        // 30.1 CloudAuto 持久化；CloudUploaded 仅运行时标记、不落盘
        {
            Store.HomeDirOverride = Path.Combine(temp, "vault_ca1");
            Store.Init();
            string caSaveDir = Path.Combine(temp, "ca_saves");
            Directory.CreateDirectory(caSaveDir);
            File.WriteAllText(Path.Combine(caSaveDir, "s.txt"), "v1");
            var gCA = new GameEntry { NameCN = "云开关游戏" };
            gCA.SavePaths.Add(new SavePathEntry { Path = caSaveDir });
            Store.Games.Add(gCA);
            var bCA = BackupService.Create(gCA, "一份备份");
            gCA.CloudAuto = true;
            bCA.CloudUploaded = true;
            Store.Save();
            Store.Init();   // 重新加载验证持久化边界
            var gCAR = Store.Games.First(x => x.NameCN == "云开关游戏");
            Check("云开关: CloudAuto 随库存持久化", gCAR.CloudAuto);
            Check("云开关: CloudUploaded 不持久化（重载为 false）",
                gCAR.Backups.All(b => !b.CloudUploaded), "应全部 false");
            Check("云开关: 新游戏默认关闭", new GameEntry().CloudAuto == false);
            Store.HomeDirOverride = null;
        }

        // 30.2 旧版全局开关一次性迁移
        {
            Store.HomeDirOverride = Path.Combine(temp, "vault_ca_m1");
            Store.Init();
            var gM1 = new GameEntry { NameEN = "Mig1" };
            Store.Games.Add(gM1);
            Store.Save();
            // 默认未开云：只落迁移位，游戏不自动
            CloudSyncService.MigrateGlobalCloud();
            Check("云迁移: 未开云时迁移位落地、游戏不自动",
                Store.Settings.CloudPerGameMigrated && !gM1.CloudAuto);
            // 幂等：迁移完成后再手动开全局也不回补
            Store.Settings.CloudEnabled = true;
            Store.SaveSettings();
            CloudSyncService.MigrateGlobalCloud();
            Check("云迁移: 已迁移后重复执行无副作用", !gM1.CloudAuto);

            // 旧用户场景：CloudEnabled=true 且迁移位缺失
            Store.HomeDirOverride = Path.Combine(temp, "vault_ca_m2");
            Store.Init();
            var gM2 = new GameEntry { NameEN = "Mig2" };
            Store.Games.Add(gM2);
            Store.Settings.CloudEnabled = true;
            Store.Save();
            Store.SaveSettings();
            CloudSyncService.MigrateGlobalCloud();
            Check("云迁移: 旧全局开启用户全部游戏继承自动",
                gM2.CloudAuto && Store.Settings.CloudPerGameMigrated);
            Store.HomeDirOverride = null;
        }

        // 30.2b 加载不可信（Defaulted）时迁移只落内存，绝不主动创/写 settings.json
        {
            Store.HomeDirOverride = Path.Combine(temp, "vault_ca_m3");
            Store.Init();
            Check("云迁移: 全新空库首启置信度为 Defaulted",
                Store.SettingsConfidence == SettingsLoadConfidence.Defaulted,
                Store.SettingsConfidence.ToString());
            CloudSyncService.MigrateGlobalCloud();
            Check("云迁移: Defaulted 时迁移位置内存但 settings.json 不落盘",
                Store.Settings.CloudPerGameMigrated && !File.Exists(Store.SettingsFile),
                $"migrated={Store.Settings.CloudPerGameMigrated}, fileExists={File.Exists(Store.SettingsFile)}");
            Store.HomeDirOverride = null;
        }

        // 30.2c 事故场景复现：磁盘是富配置、本次启动回退默认（Defaulted）——
        // MigrateGlobalCloud 不覆写，Store.SaveSettings 防呆拦截并隔离默认内容
        {
            string vaultM4 = Path.Combine(temp, "vault_ca_m4");
            Store.HomeDirOverride = vaultM4;
            Store.Init();
            Store.Settings.DefaultPlayerName = "富配置用户";
            Store.Settings.BackupHotKey = "Ctrl+F5";
            Store.Settings.CloudUser = "rich@example.com";
            Store.SaveSettings();
            Store.Init();   // 重新加载：磁盘富配置、置信度 Loaded
            Check("防呆前置: 富配置正常加载为 Loaded",
                Store.SettingsConfidence == SettingsLoadConfidence.Loaded
                && Store.Settings.BackupHotKey == "Ctrl+F5",
                Store.SettingsConfidence.ToString());
            // 模拟读盘瞬时失败后的内存状态：置信度 Defaulted + 一份出厂默认设置
            typeof(Store).GetProperty(nameof(Store.SettingsConfidence))!
                .SetValue(null, SettingsLoadConfidence.Defaulted);
            typeof(Store).GetProperty(nameof(Store.Settings))!
                .SetValue(null, new AppSettings());
            string richBefore = FileFingerprint(Store.SettingsFile);
            string? guardNotice = null;
            Store.SaveFailed += msg => guardNotice = msg;
            CloudSyncService.MigrateGlobalCloud();
            Store.SaveSettings();
            Check("云迁移: Defaulted 时磁盘富配置指纹不变",
                FileFingerprint(Store.SettingsFile) == richBefore);
            Check("防呆: SaveSettings 拒绝覆写并触发用户提示", guardNotice != null, guardNotice ?? "");
            var diskAfter = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Store.SettingsFile));
            Check("防呆: 磁盘仍是富配置（快捷键/玩家名/云账号）",
                diskAfter!.BackupHotKey == "Ctrl+F5"
                && diskAfter.DefaultPlayerName == "富配置用户"
                && diskAfter.CloudUser == "rich@example.com");
            Check("防呆: 默认内容已隔离到 settings.guard.*.json",
                Directory.GetFiles(vaultM4, "settings.guard.*.json").Length == 1);
            Store.HomeDirOverride = null;
        }

        // 30.2d 启动自愈：磁盘 settings.json 已是"旧版默认壳"+ 历史富快照 → Init 自动回滚并标记原因
        {
            Store.HomeDirOverride = Path.Combine(temp, "vault_ca_m5");
            Store.Init();
            Store.Settings.BackupHotKey = "Ctrl+F9";
            Store.Settings.DefaultPlayerName = "回滚用户";
            Store.Settings.CloudUser = "rollback@example.com";
            Store.SaveSettings();
            Store.SnapshotAppData("settings.json", force: true);   // 确保富快照落盘（绕开 10 分钟节流）
            // 用"无版本字段的出厂默认壳"覆写正式文件（事故指纹）
            var shellObj = JsonSerializer.SerializeToNode(new AppSettings(), new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            })!.AsObject();
            shellObj.Remove("SettingsSchemaVersion");
            File.WriteAllText(Store.SettingsFile, shellObj.ToJsonString());
            Store.Init();
            Check("自动回滚: 默认壳事故后重启置信度为 Restored",
                Store.SettingsConfidence == SettingsLoadConfidence.Restored,
                Store.SettingsConfidence.ToString());
            Check("自动回滚: 恢复原因标记为 DefaultShellRollback",
                Store.SettingsRestoreCause == SettingsRestoreCause.DefaultShellRollback,
                Store.SettingsRestoreCause.ToString());
            Check("自动回滚: 富配置已找回（快捷键/玩家名/云账号）",
                Store.Settings.BackupHotKey == "Ctrl+F9"
                && Store.Settings.DefaultPlayerName == "回滚用户"
                && Store.Settings.CloudUser == "rollback@example.com");
            Check("自动回滚: 记录了恢复来源快照名", Store.RestoredSettingsSnapshot != null);
            Store.HomeDirOverride = null;
        }

        // 30.2e 云表单三态判定（纯函数）：半成品只影响云同步，不得阻塞其他设置项
        {
            Check("云表单: 地址/账号/密码全空=Empty",
                CloudSyncService.EvaluateCloudForm("", "", false) == CloudSyncService.CloudFormState.Empty);
            Check("云表单: 地址+账号+密码齐全=Complete",
                CloudSyncService.EvaluateCloudForm("https://dav.example.com/dav/", "u", true)
                    == CloudSyncService.CloudFormState.Complete);
            Check("云表单: 只填地址=Partial",
                CloudSyncService.EvaluateCloudForm("https://dav.example.com/dav/", "", false)
                    == CloudSyncService.CloudFormState.Partial);
            Check("云表单: 账号+密码但缺地址=Partial",
                CloudSyncService.EvaluateCloudForm("", "u", true)
                    == CloudSyncService.CloudFormState.Partial);
            Check("云表单: 地址+账号但缺密码=Partial",
                CloudSyncService.EvaluateCloudForm("https://dav.example.com/dav/", "u", false)
                    == CloudSyncService.CloudFormState.Partial);
            Check("云表单: 只剩旧密文=Partial",
                CloudSyncService.EvaluateCloudForm("", "", true)
                    == CloudSyncService.CloudFormState.Partial);
        }

        // 30.2f 三态落盘语义：半成品不拦其他字段；全空清密文；Loaded 可改回默认；危险会话仍拦截
        {
            string vaultF = Path.Combine(temp, "vault_ca_f");
            Store.HomeDirOverride = vaultF;
            Store.Init();
            Check("危险会话: 全新空库（无 settings.json）不是危险会话", !Store.IsUnsafeDefaultSession);

            // 首启保存半成品云配置 + 同时修改其他设置项（快捷键）：都必须落盘
            Store.Settings.CloudEnabled = false;
            Store.Settings.CloudUrl = "https://dav.example.com/dav/";
            Store.Settings.CloudUser = "half";
            Store.Settings.CloudSecret = "";
            Store.Settings.BackupHotKey = "Ctrl+F7";
            Store.SaveSettings();
            Store.Init();
            Check("云表单落盘: 半成品不启用云同步", !Store.Settings.CloudEnabled);
            Check("云表单落盘: 半成品的地址/账号保留待下次补全",
                Store.Settings.CloudUrl == "https://dav.example.com/dav/" && Store.Settings.CloudUser == "half");
            Check("云表单落盘: 同一次保存里其他设置项（快捷键）正常落盘",
                Store.Settings.BackupHotKey == "Ctrl+F7");
            Check("危险会话: 正常加载后不是危险会话", !Store.IsUnsafeDefaultSession);

            // Loaded 会话中用户有权把所有项改回出厂默认（防呆不得误伤）
            typeof(Store).GetProperty(nameof(Store.Settings))!.SetValue(null, new AppSettings());
            Store.SaveSettings();
            var diskDefault = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Store.SettingsFile))!;
            Check("防呆边界: Loaded 会话可把设置改回出厂默认（带 schema 版本）",
                Store.IsDefaultShell(diskDefault)
                && diskDefault.SettingsSchemaVersion == AppSettings.CurrentSchemaVersion);

            // 全空表单=关闭云同步：旧密文/账号/地址必须清空
            Store.Init();
            Store.Settings.CloudEnabled = false;
            Store.Settings.CloudUrl = "";
            Store.Settings.CloudUser = "";
            Store.Settings.CloudSecret = "";
            Store.SaveSettings();
            Store.Init();
            Check("云表单落盘: 全空关闭后 CloudEnabled=false 且密文/账号/地址清空",
                !Store.Settings.CloudEnabled && Store.Settings.CloudSecret == ""
                && Store.Settings.CloudUser == "" && Store.Settings.CloudUrl == "");

            // 危险会话：磁盘富配置 + 本次启动回退 Defaulted → 任何保存都拒绝
            Store.Settings.BackupHotKey = "Ctrl+F8";
            Store.Settings.CloudUser = "rich@example.com";
            Store.Settings.CloudSecret = "protected-secret";
            Store.SaveSettings();
            string richFp = FileFingerprint(Store.SettingsFile);
            typeof(Store).GetProperty(nameof(Store.SettingsConfidence))!
                .SetValue(null, SettingsLoadConfidence.Defaulted);
            Check("危险会话: 富配置磁盘+Defaulted 判定为危险", Store.IsUnsafeDefaultSession);
            // 即使用户在异常会话里把内存改成非默认内容，也一样拦截
            typeof(Store).GetProperty(nameof(Store.Settings))!
                .SetValue(null, new AppSettings { BackupHotKey = "Ctrl+F9", CloudUser = "new@example.com" });
            Store.SaveSettings();
            Check("危险会话: 富配置磁盘指纹不变", FileFingerprint(Store.SettingsFile) == richFp);
            var diskStillRich = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Store.SettingsFile))!;
            Check("危险会话: 磁盘仍是原富配置",
                diskStillRich.BackupHotKey == "Ctrl+F8" && diskStillRich.CloudUser == "rich@example.com");
            Store.HomeDirOverride = null;
        }

        // 30.3 PlanDownloads：按本机已有文件名预筛云端清单
        {
            Store.HomeDirOverride = Path.Combine(temp, "vault_plan");
            Store.Init();
            string plSaveDir = Path.Combine(temp, "plan_saves");
            Directory.CreateDirectory(plSaveDir);
            File.WriteAllText(Path.Combine(plSaveDir, "f.txt"), "1");
            var gPL = new GameEntry { NameEN = "PlanGame" };
            gPL.SavePaths.Add(new SavePathEntry { Path = plSaveDir });
            var bPL1 = BackupService.Create(gPL, "b1");
            File.WriteAllText(Path.Combine(plSaveDir, "f.txt"), "2");
            var bPL2 = BackupService.Create(gPL, "b2");
            var remotePL = new List<CloudRemoteFile>
            {
                new() { Name = Path.GetFileName(bPL1.File), Game = "PlanGame" },  // 本机已有
                new() { Name = "cloud-only.zip", Game = "PlanGame" },             // 需下载
                new() { Name = Path.GetFileName(bPL2.File), Game = "PlanGame" },  // 本机已有
            };
            var plan = CloudSyncService.PlanDownloads(gPL, remotePL, "PlanGame");
            Check("云预筛: 只保留本机缺的一份",
                plan.Count == 1 && plan[0].Name == "cloud-only.zip", $"count={plan.Count}");
            Check("云预筛: 空云端返回空",
                CloudSyncService.PlanDownloads(gPL, new List<CloudRemoteFile>(), "PlanGame").Count == 0);
            var gPLEmpty = new GameEntry { NameEN = "PlanEmpty" };
            Check("云预筛: 空库全部需要",
                CloudSyncService.PlanDownloads(gPLEmpty, remotePL, "PlanGame").Count == 3);
            Store.HomeDirOverride = null;
        }

        // 30.4 云端 zip 落库即建档：AdoptDownloadedZip 导入新玩家 zip → 玩家进列表 → 切换恢复云端版本
        // （zip 唯一事实源后不再重建镜像；跨机器路径差异由 SwitchTo 的 targetPaths 重映射兜底）
        {
            Store.HomeDirOverride = Path.Combine(temp, "vault_cloudpc");
            Store.Init();

            // 造云端 zip：源机小明玩过后备份（manifest.Profile=小明，manifest.Paths=源机路径）
            string pcSrcDir = Path.Combine(temp, "pc_saves");
            Directory.CreateDirectory(pcSrcDir);
            File.WriteAllText(Path.Combine(pcSrcDir, "s.txt"), "开局");
            var gPcSrc = new GameEntry { NameCN = "云玩家源游戏" };
            gPcSrc.SavePaths.Add(new SavePathEntry { Path = pcSrcDir });
            ProfileService.CreatePlayer(gPcSrc, "小明");
            gPcSrc.RefreshProfiles();
            File.WriteAllText(Path.Combine(pcSrcDir, "s.txt"), "小明云端版");
            var mingCloudBak = BackupService.Create(gPcSrc, "小明进度");
            // 备份后源机 live 继续变化：若路径重映射失效，切到小明会把源机文件覆盖回云端版本（可检测）
            File.WriteAllText(Path.Combine(pcSrcDir, "s.txt"), "源机后续变化");

            // "另一台电脑"：同游戏的新条目，live 目录不同（模拟源机路径在本机不存在也可以，这里共存以校验不串目录）
            string pcDstDir = Path.Combine(temp, "pc_saves2");
            Directory.CreateDirectory(pcDstDir);
            File.WriteAllText(Path.Combine(pcDstDir, "s.txt"), "别的游戏目录");
            var gPcDst = new GameEntry { NameCN = "云玩家源游戏" };
            gPcDst.SavePaths.Add(new SavePathEntry { Path = pcDstDir });
            Store.Games.Add(gPcDst);

            // 云端同步是 add-only 全量枚举：全量锚点 + 增量包都会下载到本机（增量链必须完整才能还原）。
            // 下载临时文件名与云端一致（真机即如此），同库重传时直接挂上已存在的物理文件、不产生 _2 断链副本
            string dlDir = Path.Combine(temp, "dl_cloud");
            Directory.CreateDirectory(dlDir);
            var mingChain = gPcSrc.Backups.Where(b => b.Profile == "小明")
                .OrderBy(b => b.Time).ToList();
            BackupRecord? adoptedMing = null;
            foreach (var b in mingChain)
            {
                string dl = Path.Combine(dlDir, Path.GetFileName(b.File));
                File.Copy(b.File, dl);
                adoptedMing = BackupService.AdoptDownloadedZip(gPcDst, dl);
            }
            Check("云建档: 新玩家 zip 落库后玩家进列表且未激活",
                adoptedMing != null && adoptedMing.Profile == "小明" && adoptedMing.Note == "云端下载"
                && ProfileService.ListSlots(gPcDst).Contains("小明") && gPcDst.ActiveProfile == "",
                $"active={gPcDst.ActiveProfile}");
            Check("云建档: zip 挂到玩家备份子目录（临时文件不再存在）",
                adoptedMing != null && File.Exists(adoptedMing.File)
                && Path.GetDirectoryName(adoptedMing.File) == BackupService.PlayerBackupDir(gPcDst, "小明")
                && !Directory.EnumerateFiles(dlDir, "*.zip").Any(),
                adoptedMing?.File ?? "");

            // 重复导入同一云端 zip（同时间+同大小+同玩家）：判重返回已有记录、不新增
            string dlMing2 = Path.Combine(temp, "dl_ming2.zip");
            File.Copy(mingCloudBak.File, dlMing2);
            var adoptedDup = BackupService.AdoptDownloadedZip(gPcDst, dlMing2);
            Check("云建档: 重复导入判重不新增记录",
                adoptedDup == adoptedMing && gPcDst.Backups.Count(b => b.Profile == "小明") == mingChain.Count,
                $"count={gPcDst.Backups.Count(b => b.Profile == "小明")}");

            // 切换到云端来的玩家：live 恢复为云端 zip 内容，且落到本机存档目录（重映射）、不串到源机路径
            ProfileService.SwitchTo(gPcDst, "小明");
            gPcDst.RefreshProfiles();
            Check("云建档: 切换后本机 live 为云端版本",
                File.ReadAllText(Path.Combine(pcDstDir, "s.txt")) == "小明云端版",
                File.ReadAllText(Path.Combine(pcDstDir, "s.txt")));
            Check("云建档: 源机路径不被写入（重映射生效）",
                File.ReadAllText(Path.Combine(pcSrcDir, "s.txt")) == "源机后续变化",
                File.ReadAllText(Path.Combine(pcSrcDir, "s.txt")));
            Store.HomeDirOverride = null;
        }

        // 30.6 切换玩家（zip 唯一事实源）：切走时无论云同步开关都为当前玩家打包「切换玩家」备份，
        // 切入恢复目标玩家最新备份；没有变化则不打冗余包（备份=状态，切换=恢复状态）
        {
            Store.HomeDirOverride = Path.Combine(temp, "vault_switch");
            Store.Init();
            string swSaveDir = Path.Combine(temp, "sw_saves");
            Directory.CreateDirectory(swSaveDir);
            string swFile = Path.Combine(swSaveDir, "s.txt");
            File.WriteAllText(swFile, "开局");
            var gSW = new GameEntry { NameCN = "切换云游戏" };
            gSW.SavePaths.Add(new SavePathEntry { Path = swSaveDir });
            gSW.CloudAuto = true;
            ProfileService.CreatePlayer(gSW, "小明");   // 首玩家承接：当前「开局」打包归他
            gSW.RefreshProfiles();
            ProfileService.CreatePlayer(gSW, "小红");   // 小明无变化→切走不打包；小红无锚点、名下 0 份
            gSW.RefreshProfiles();

            // 小红玩出未备份进度后切走：切走打包保护进度，应多一份小红的「切换玩家」备份
            File.WriteAllText(swFile, "小红未备份");
            int hongBefore = gSW.Backups.Count(b => b.Profile == "小红");
            ProfileService.SwitchTo(gSW, "小明");
            gSW.RefreshProfiles();
            Check("云切换: 当前玩家未备份进度切走自动打包（小红 +1，note=切换玩家）",
                gSW.Backups.Count(b => b.Profile == "小红") == hongBefore + 1
                && gSW.Backups.Count(b => b.Profile == "小红" && b.Note == "切换玩家") == 1,
                $"小红={gSW.Backups.Count(b => b.Profile == "小红")}");
            Check("云切换: live 恢复为小明的开局",
                File.ReadAllText(swFile) == "开局", File.ReadAllText(swFile));

            // 小明没玩就切回小红：不产生备份；live=小红未备份
            int mingNow = gSW.Backups.Count(b => b.Profile == "小明");
            ProfileService.SwitchTo(gSW, "小红");
            gSW.RefreshProfiles();
            Check("云切换: 无进度变化不产生备份（小明份数不变）",
                gSW.Backups.Count(b => b.Profile == "小明") == mingNow,
                $"mingNow={mingNow}");
            Check("云切换: 切回小红 live 是她未备份的进度",
                File.ReadAllText(swFile) == "小红未备份", File.ReadAllText(swFile));

            // 关闭云同步后切换：数据保护与云开关解耦，仍为当前玩家打包
            File.WriteAllText(swFile, "小红再玩");
            gSW.CloudAuto = false;
            int hongNow2 = gSW.Backups.Count(b => b.Profile == "小红");
            ProfileService.SwitchTo(gSW, "小明");
            gSW.RefreshProfiles();
            Check("云切换: 关闭云同步后切换仍打包保护进度（小红 +1）",
                gSW.Backups.Count(b => b.Profile == "小红") == hongNow2 + 1,
                $"小红={gSW.Backups.Count(b => b.Profile == "小红")}");
            Check("云切换: 关闭云同步后 live 仍恢复为小明进度",
                File.ReadAllText(swFile) == "开局", File.ReadAllText(swFile));
            Store.HomeDirOverride = null;
        }

        // 30.7 时间戳精度与已知云端文件：防「云端上传时刻 ≠ 游戏存档时刻」切换死循环
        // （旧 zip 条目只有 2 秒精度，刷新/还原后毫秒截断曾被误判为文件变化 → 反复打包上传）
        {
            Store.HomeDirOverride = Path.Combine(temp, "vault_precise");
            Store.Init();
            string pzDir = Path.Combine(temp, "precise_saves");
            Directory.CreateDirectory(pzDir);
            string pzFile = Path.Combine(pzDir, "s.sav");
            File.WriteAllText(pzFile, "内容A");
            File.SetLastWriteTimeUtc(pzFile, new DateTime(2026, 9, 10, 15, 46, 44, 163, DateTimeKind.Utc));  // 带毫秒
            var gPZ = new GameEntry { NameCN = "精度游戏" };
            gPZ.SavePaths.Add(new SavePathEntry { Path = pzDir });
            ProfileService.CreatePlayer(gPZ, "甲");   // 锚点 zip：内容=内容A、毫秒 mtime
            gPZ.RefreshProfiles();
            var pzBak = gPZ.Backups.Where(b => b.Profile == "甲")
                .OrderByDescending(b => b.Time).First();
            long pzTicks = File.GetLastWriteTimeUtc(pzFile).Ticks;
            Check("时间精度: manifest 记录毫秒级 mtime",
                pzBak != null && BackupService.ReadManifest(pzBak.File)!.Files
                    .First(f => f.N.EndsWith("s.sav")).M == pzTicks);

            // 还原备份：zip 条目时间被截断到 2 秒，还原后必须按 manifest 回写毫秒
            File.WriteAllText(pzFile, "内容B");
            BackupService.Restore(gPZ, pzBak!);
            Check("时间精度: 还原后 live mtime 毫秒级还原",
                File.ReadAllText(pzFile) == "内容A" && File.GetLastWriteTimeUtc(pzFile).Ticks == pzTicks,
                $"ticks={File.GetLastWriteTimeUtc(pzFile).Ticks} vs {pzTicks}");

            // 切换玩家路径（Restore 带 targetPaths 重映射）：同样必须毫秒级回写，
            // 否则切换后 2 秒截断被误判为文件变化 → 反复打包上传（死循环根源）
            ProfileService.CreatePlayer(gPZ, "乙");   // 甲无变化→切走不打包；乙登记无锚点
            gPZ.RefreshProfiles();
            ProfileService.SwitchTo(gPZ, "甲");        // 乙无变化→不打包；恢复甲最新备份
            gPZ.RefreshProfiles();
            Check("时间精度: 切换玩家后 live mtime 毫秒级还原",
                File.ReadAllText(pzFile) == "内容A" && File.GetLastWriteTimeUtc(pzFile).Ticks == pzTicks,
                $"ticks={File.GetLastWriteTimeUtc(pzFile).Ticks} vs {pzTicks}");

            // 已知云端文件：未登记→未知；登记→已知；随 cloud-state.json 持久化
            string relPZ = BackupService.SafeName(gPZ.DisplayName) + "/_通用/x.zip";
            Check("已知云端: 未登记视为未知", !CloudSyncService.IsRemoteKnown(relPZ));
            CloudSyncService.MarkRemoteKnown(relPZ);
            Check("已知云端: 登记后视为已知", CloudSyncService.IsRemoteKnown(relPZ));
            CloudSyncService.WaitStateSaved();
            Check("已知云端: 随 cloud-state.json 持久化",
                System.Text.Json.JsonDocument.Parse(File.ReadAllText(Store.CloudStateFile))
                    .RootElement.GetProperty("KnownRemote").EnumerateObject().Any(p => p.Name == relPZ));
            // 旧版登记表（只有 Uploaded.Remote）升级后自动播种为已知
            CloudSyncService.WaitStateSaved();   // 防排队写盘覆盖下面手工构造的旧表
            File.WriteAllText(Store.CloudStateFile,
                "{\"Uploaded\":{\"k\":{\"Size\":1,\"Mtime\":0,\"Remote\":\"旧游戏/_通用/旧包.zip\",\"When\":\"2026-09-16T22:00:00\"}}}");
            CloudSyncService.LoadState();
            Check("已知云端: 旧登记表 Remote 自动播种", CloudSyncService.IsRemoteKnown("旧游戏/_通用/旧包.zip"));

            // 云端下载入库防回传：登记前备份行是「上传云端」可点态；登记后必须立即「云端已存」禁用
            Check("下载防回传: 登记前 IsUploaded=false（手动按钮可点）",
                !CloudSyncService.IsUploaded(pzBak!.File), "");
            CloudSyncService.MarkAdoptedFromCloud(pzBak.File, relPZ);
            Check("下载防回传: 登记后 IsUploaded=true（手动按钮禁用，不会撞 412 造重复副本）",
                CloudSyncService.IsUploaded(pzBak.File), "");
            Check("下载防回传: 反查云端路径命中本地文件（云端浏览窗删原件联动要用）",
                CloudSyncService.FindLocalByRemote(relPZ) == pzBak.File,
                CloudSyncService.FindLocalByRemote(relPZ) ?? "<null>");
            CloudSyncService.RefreshCloudFlags(gPZ);
            Check("下载防回传: 运行时标记 CloudUploaded=true 且非「已删重传」态",
                pzBak.CloudUploaded && !pzBak.CloudDeleted, "");
            CloudSyncService.WaitStateSaved();
            Check("下载防回传: 登记随 cloud-state.json 持久化（重启后按钮仍禁用）",
                System.Text.Json.JsonDocument.Parse(File.ReadAllText(Store.CloudStateFile))
                    .RootElement.GetProperty("Uploaded").EnumerateObject()
                    .Any(p => p.Name == pzBak.File && p.Value.GetProperty("Remote").GetString() == relPZ), "");
            CloudSyncService.LoadState();
            Check("下载防回传: 重载登记表后仍认定已上传",
                CloudSyncService.IsUploaded(pzBak.File), "");
            Store.HomeDirOverride = null;
        }
    }

    // ── 31. 类型徽章更名本地版 / WebDAV 上传请求构造回归（FormatException）──
    {
        // 31.1 类型徽章文案
        var gTb = new GameEntry();
        Check("徽章: 非 Steam 游戏显示本地版", gTb.TypeBadge == "本地版", gTb.TypeBadge);
        gTb.Type = "Steam";
        Check("徽章: Steam 游戏显示 Steam", gTb.TypeBadge == "Steam", gTb.TypeBadge);

        // 31.2 回归：UploadAsync 此前在构造 If-None-Match 时 new EntityTagHeaderValue("*")
        // 抛 FormatException，任何服务器都到不了。用本机裸 TCP 假服务器应答 201 验证请求真正发出
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        async Task HandleOne(System.Net.Sockets.NetworkStream stream)
        {
            using var reader = new StreamReader(stream, System.Text.Encoding.ASCII);
            var headers = new List<string>();
            string? line;
            while ((line = await reader.ReadLineAsync()) != null && line.Length > 0) headers.Add(line);
            int contentLen = 0;
            string? clLine = headers.FirstOrDefault(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            if (clLine != null) int.TryParse(clLine.Split(':')[1], out contentLen);
            if (headers.Any(x => x.StartsWith("Expect:", StringComparison.OrdinalIgnoreCase)))
            {
                var cont = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                await stream.WriteAsync(cont);
            }
            if (contentLen > 0)
            {
                var body = new char[contentLen];
                int got = 0;
                while (got < contentLen)
                {
                    int n = await reader.ReadAsync(body, got, contentLen - got);
                    if (n == 0) break;
                    got += n;
                }
            }
            var resp = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 201 Created\r\nContent-Length: 0\r\n\r\n");
            await stream.WriteAsync(resp);
            await stream.FlushAsync();
        }

        var server = Task.Run(async () =>
        {
            for (int i = 0; i < 3; i++)   // MKCOL g → MKCOL g/p → PUT
            {
                using var c = await listener.AcceptTcpClientAsync();
                await HandleOne(c.GetStream());
            }
        });

        string wcFile = Path.Combine(temp, "wc_up.txt");
        File.WriteAllText(wcFile, "hello");
        var wc = new WebDavClient($"http://127.0.0.1:{port}/", "u", "p", "root");
        bool uploadOk = false;
        string? upErr = null;
        try
        {
            string rel = await wc.UploadAsync(wcFile, "g/p/f.zip");
            uploadOk = rel == "g/p/f.zip";
        }
        catch (Exception ex) { upErr = ex.Message; }
        Check("WebDAV: 上传请求可正常构造并发到服务器（回归 FormatException）", uploadOk, upErr ?? "");
        await server;
        listener.Stop();
    }

    // ── 32. 云浏览枚举回归：根段被拼两次（…/GameSaveVault/GameSaveVault → 404 → 空列表）──
    {
        var l2 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l2.Start();
        int port2 = ((System.Net.IPEndPoint)l2.LocalEndpoint).Port;
        const string date = "Wed, 16 Sep 2026 11:00:00 GMT";

        // 按请求路径给出对应的 207 multistatus（WalkAsync 只看元素 LocalName）
        string BodyFor(string path)
        {
            string Item(string href, bool col, long size = 0) =>
                "<d:response><d:href>" + href + "</d:href><d:propstat><d:prop>" +
                "<d:resourcetype>" + (col ? "<d:collection/>" : "") + "</d:resourcetype>" +
                "<d:getcontentlength>" + size + "</d:getcontentlength>" +
                "<d:getlastmodified>" + date + "</d:getlastmodified>" +
                "</d:prop></d:propstat></d:response>";
            string Wrap(params string[] items) =>
                "<d:multistatus xmlns:d=\"DAV:\">" + string.Join("", items) + "</d:multistatus>";

            if (path == "/GameSaveVault")
                return Wrap(
                    Item("/GameSaveVault/", true),
                    Item("/GameSaveVault/G1", true));
            if (path == "/GameSaveVault/G1")
                return Wrap(
                    Item("/GameSaveVault/G1/", true),
                    Item("/GameSaveVault/G1/P1", true),
                    Item("/GameSaveVault/G1/_通用", true));
            if (path == "/GameSaveVault/G1/P1")
                return Wrap(
                    Item("/GameSaveVault/G1/P1/", true),
                    Item("/GameSaveVault/G1/P1/G1_2026.zip", false, 123));
            if (path == "/GameSaveVault/G1/_通用")
                return Wrap(
                    Item("/GameSaveVault/G1/_通用/", true),
                    Item("/GameSaveVault/G1/_通用/G1_t.zip", false, 45));
            return Wrap(Item(path.TrimEnd('/') + "/", true));
        }

        async Task Serve2(System.Net.Sockets.NetworkStream stream)
        {
            using var reader = new StreamReader(stream, System.Text.Encoding.ASCII);
            string? reqLine = await reader.ReadLineAsync();
            var headers = new List<string>();
            string? hl;
            while ((hl = await reader.ReadLineAsync()) != null && hl.Length > 0) headers.Add(hl);
            int contentLen = 0;
            string? clLine = headers.FirstOrDefault(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            if (clLine != null) int.TryParse(clLine.Split(':')[1], out contentLen);
            if (headers.Any(x => x.StartsWith("Expect:", StringComparison.OrdinalIgnoreCase)))
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"));
            if (contentLen > 0)
            {
                var buf = new char[contentLen];
                int got = 0;
                while (got < contentLen)
                {
                    int n = await reader.ReadAsync(buf, got, contentLen - got);
                    if (n == 0) break;
                    got += n;
                }
            }
            string rawPath = reqLine?.Split(' ').ElementAtOrDefault(1) ?? "/";
            // 请求路径中文段已被 EscapeDataString 编码，先解码再路由
            string path = Uri.UnescapeDataString(rawPath);
            string bodyText = BodyFor(path);
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                "HTTP/1.1 207 Multi-Status\r\nContent-Type: application/xml\r\nContent-Length: "
                + System.Text.Encoding.UTF8.GetByteCount(bodyText) + "\r\n\r\n" + bodyText);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }

        // ListAllAsync 发 4 个 PROPFIND；随后 ListAsync("G1") 发 3 个，共 7 个连接
        var server2 = Task.Run(async () =>
        {
            for (int i = 0; i < 7; i++)
            {
                using var c = await l2.AcceptTcpClientAsync();
                await Serve2(c.GetStream());
            }
        });

        var wc2 = new WebDavClient($"http://127.0.0.1:{port2}/", "u", "p", "GameSaveVault");
        List<CloudRemoteFile> got2 = await wc2.ListAllAsync();

        Check("云浏览: 枚举到 2 份 zip（回归根段拼两次→404→空列表）", got2.Count == 2,
            "count=" + got2.Count);
        var fP1 = got2.FirstOrDefault(f => f.Profile == "P1");
        var fCommon = got2.FirstOrDefault(f => f.Profile.Length == 0);
        Check("云浏览: 分玩家备份 游戏/玩家/大小/RelPath 正确",
            fP1 != null && fP1.Game == "G1" && fP1.Size == 123
            && fP1.Name == "G1_2026.zip"
            && fP1.RelPath == "G1/P1/G1_2026.zip",
            fP1 == null ? "缺失" : fP1.RelPath);
        Check("云浏览: _通用 备份 Profile 为空且 RelPath 正确",
            fCommon != null && fCommon.Game == "G1" && fCommon.Size == 45
            && fCommon.RelPath == "G1/_通用/G1_t.zip",
            fCommon == null ? "缺失" : fCommon.RelPath);
        // RelPath 必须能直接拼出单根 URL（下载/删除都靠它）
        Check("云浏览: RelPath 拼 URL 只有一个根段",
            wc2.Url("G1/P1/G1_2026.zip") ==
            $"http://127.0.0.1:{port2}/GameSaveVault/G1/P1/G1_2026.zip",
            wc2.Url("G1/P1/G1_2026.zip"));

        var one2 = await wc2.ListAsync("G1");
        Check("云浏览: ListAsync(游戏段) 返回该游戏 2 份备份", one2.Count == 2,
            "count=" + one2.Count);

        await server2;
        l2.Stop();
    }
}

// ── 33. 存档时间（SavedAt）：一等公民字段 / 空锚点 / 云端索引 / 切换比较 / 启动回填 ──
{
    Store.HomeDirOverride = Path.Combine(temp, "vault_savedat");
    Store.Init();

    // 33.1 Create 写入存档时间（取 manifest 内存档文件最大修改时刻，而非备份打包时刻）
    string sdDir = Path.Combine(temp, "savedat_saves");
    Directory.CreateDirectory(sdDir);
    string sdFile = Path.Combine(sdDir, "save.dat");
    DateTime sdT1 = new DateTime(2026, 9, 17, 14, 30, 0, DateTimeKind.Local);
    File.WriteAllText(sdFile, "v1");
    File.SetLastWriteTimeUtc(sdFile, sdT1.ToUniversalTime());
    var gSd = new GameEntry { NameEN = "SavedAtGame" };
    gSd.SavePaths.Add(new SavePathEntry { Path = sdDir });
    var rSd = BackupService.Create(gSd, "存档时间测试");
    Check("存档时间: Create 写入 SavedAt（非 MinValue）", rSd.SavedAt != DateTime.MinValue,
        rSd.SavedAt.ToString("yyyy-MM-dd HH:mm:ss"));
    Check("存档时间: 与 manifest 推导值一致",
        rSd.SavedAt == BackupService.SavedAtFromManifest(BackupService.ReadManifest(rSd.File)));
    Check("存档时间: 等于存档文件修改时间（±3s zip 精度容差）",
        Math.Abs((rSd.SavedAt - sdT1).TotalSeconds) <= 3,
        $"saved={rSd.SavedAt:HH:mm:ss},file={sdT1:HH:mm:ss}");
    Check("存档时间: LastProgressSaved 走字段优先路径",
        ProfileService.LastProgressSaved(gSd, "玩家1") == rSd.SavedAt);
    Check("存档时间: 主行显示「存档于 yyyy-MM-dd HH:mm」",
        rSd.HasSavedAt && rSd.SavedAtMainText == "存档于 " + rSd.SavedAt.ToString("yyyy-MM-dd HH:mm"),
        rSd.SavedAtMainText);
    string wantBakText = "备份于 " + rSd.Time.ToString("yyyy-MM-dd HH:mm");
    Check("存档时间: 副行始终显示完整日期时间（同日也不省略日期）",
        rSd.BackupTimeText == wantBakText, rSd.BackupTimeText);

    // 改动存档再备份：第二份存档时间更新；LastProgressSaved 取最新一份
    File.WriteAllText(sdFile, "v2");
    DateTime sdT2 = sdT1.AddHours(2);
    File.SetLastWriteTimeUtc(sdFile, sdT2.ToUniversalTime());
    var rSd2 = BackupService.Create(gSd, "第二份");
    Check("存档时间: 第二份备份存档时间随之更新",
        rSd2.SavedAt > rSd.SavedAt && Math.Abs((rSd2.SavedAt - sdT2).TotalSeconds) <= 3,
        $"{rSd2.SavedAt:HH:mm:ss} vs {sdT2:HH:mm:ss}");
    Check("存档时间: LastProgressSaved 取最新一份的存档时间",
        ProfileService.LastProgressSaved(gSd, "玩家1") == rSd2.SavedAt);

    // 33.2 空目录不再打空包：Create 拒绝（形式卡槽保持虚拟）；旧版 EmptyAnchor 记录仍正确显示
    string eaDir = Path.Combine(temp, "savedat_empty");
    Directory.CreateDirectory(eaDir);   // 空目录
    var gEa = new GameEntry { NameEN = "EmptyAnchorGame" };
    gEa.SavePaths.Add(new SavePathEntry { Path = eaDir });
    bool noEmptyZip = false;
    try { BackupService.Create(gEa, "空备份"); }
    catch (InvalidOperationException ex) { noEmptyZip = ex.Message.Contains("尚未产生存档"); }
    Check("空目录: 不产生空 zip、不实体化卡槽",
        noEmptyZip && gEa.Backups.Count == 0 && gEa.Players.Count == 0);
    // 旧版数据中的 EmptyAnchor 记录（持久化兼容）：显示行为不变
    var rEa = new BackupRecord { EmptyAnchor = true, Time = DateTime.Now };
    Check("空锚点: SavedAt 为 MinValue", rEa.SavedAt == DateTime.MinValue);
    Check("空锚点: LastProgressSaved 返回 null",
        ProfileService.LastProgressSaved(gEa, "") is null);
    Check("空锚点: 主行显示「尚无游戏存档」", rEa.SavedAtMainText == "尚无游戏存档", rEa.SavedAtMainText);
    Check("空锚点: 副行留空（避免与主行重复）", rEa.BackupTimeText == "", rEa.BackupTimeText);
    Check("空锚点: TimeText 仍可回退显示备份时间", rEa.TimeText.Length > 0);

    // 旧记录（无 SavedAt 且非空锚点）：主行回退备份时间但仍带「存档于」前缀，与新记录排版一致
    var rLegacy = new BackupRecord { Time = new DateTime(2026, 5, 1, 9, 8, 0) };
    Check("存档时间: 旧记录主行回退也带「存档于」前缀",
        rLegacy.SavedAtMainText == "存档于 2026-05-01 09:08", rLegacy.SavedAtMainText);

    Store.HomeDirOverride = null;

    // 33.3 CloudIndex 纯函数：BuildFor / 序列化往返 / 损坏容错 / Upsert / Remove / Link
    DateTime savedLocal = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Local);
    var eFull = CloudIndex.BuildFor(new BackupRecord
    { SavedAt = savedLocal, Profile = "小明", Note = "中文备注", Kind = "full", Time = DateTime.Now });
    Check("索引: BuildFor 编存档时间 UTC ticks 与各字段",
        eFull.T == savedLocal.ToUniversalTime().Ticks && eFull.T > 0
        && eFull.P == "小明" && eFull.N == "中文备注" && eFull.K == "full");
    Check("索引: MinValue 存档时间编为 T=0",
        CloudIndex.BuildFor(new BackupRecord()).T == 0);

    var docIdx = new CloudIndex.Doc();
    CloudIndex.Upsert(docIdx, "G1/小明/a.zip", eFull);
    CloudIndex.Upsert(docIdx, "G1/_通用/b.zip", new CloudIndex.Entry { T = 0 });
    var back = CloudIndex.Deserialize(CloudIndex.Serialize(docIdx));
    Check("索引: 序列化往返条目数与中文备注",
        back != null && back.Entries.Count == 2
        && back.Entries["G1/小明/a.zip"].N == "中文备注"
        && back.Entries["G1/小明/a.zip"].T == eFull.T,
        back?.Entries.Count.ToString() ?? "null");
    CloudIndex.Upsert(docIdx, "g1/小明/A.ZIP", eFull);
    Check("索引: 键大小写不敏感（同键覆盖不新增）",
        docIdx.Entries.Count == 2
        && ReferenceEquals(docIdx.Entries["G1/小明/a.zip"], docIdx.Entries["g1/小明/A.ZIP"]));
    CloudIndex.Remove(docIdx, "G1/_通用/b.zip");
    Check("索引: Remove 删除条目", docIdx.Entries.Count == 1);
    CloudIndex.Remove(docIdx, "不存在的键");   // 不得抛异常
    Check("索引: Remove 不存在键安全", docIdx.Entries.Count == 1);

    Check("索引: 损坏 JSON 返回 null", CloudIndex.Deserialize("{bad json") == null);
    Check("索引: 空串 / null 返回 null",
        CloudIndex.Deserialize("") == null && CloudIndex.Deserialize(null) == null);
    var bare = CloudIndex.Deserialize("{}");
    Check("索引: 缺 Entries 字段重建为空字典",
        bare != null && bare.Entries.Count == 0);

    // Link：索引条目按 RelPath 关联到 PROPFIND 枚举结果（T>0 填 SaveAt；T=0 / 键缺失不填）
    long tLink = DateTime.UtcNow.AddHours(-3).Ticks;
    var docL = new CloudIndex.Doc();
    CloudIndex.Upsert(docL, "G/P1/new.zip", new CloudIndex.Entry { T = tLink });
    CloudIndex.Upsert(docL, "G/P1/empty.zip", new CloudIndex.Entry { T = 0 });
    var filesL = new List<CloudRemoteFile>
    {
        new() { RelPath = "G/P1/new.zip", Modified = DateTime.UtcNow },
        new() { RelPath = "G/P1/empty.zip", Modified = DateTime.UtcNow },
        new() { RelPath = "G/P1/missing.zip", Modified = DateTime.UtcNow },
    };
    CloudIndex.Link(filesL, docL);
    Check("索引: Link 按 RelPath 填 SaveAt（UTC，可还原本地时刻）",
        filesL[0].SaveAt == new DateTime(tLink, DateTimeKind.Utc)
        && filesL[0].SaveAt!.Value.ToLocalTime() == new DateTime(tLink, DateTimeKind.Utc).ToLocalTime());
    Check("索引: T=0 不填 SaveAt", filesL[1].SaveAt == null);
    Check("索引: 索引缺失的 zip 不填 SaveAt", filesL[2].SaveAt == null);
    CloudIndex.Link(filesL, null);   // 空 doc：不抛

    // 33.4 ShouldPromptCloudNewer：比较本机存档时间 vs 云端存档时间（1 分钟时钟容差）
    DateTime locSav = DateTime.Now.AddHours(-1);   // 本机存档时间（本地时刻）
    DateTime utcSav = locSav.ToUniversalTime();
    var cloudNewer = new CloudRemoteFile { RelPath = "G/P/n.zip", Modified = utcSav.AddHours(2), SaveAt = utcSav.AddHours(2) };
    var cloudOlder = new CloudRemoteFile { RelPath = "G/P/o.zip", Modified = utcSav.AddHours(-5), SaveAt = utcSav.AddHours(-5) };
    var cloudClose = new CloudRemoteFile { RelPath = "G/P/c.zip", Modified = utcSav.AddSeconds(30), SaveAt = utcSav.AddSeconds(30) };
    var cloudNoIdx = new CloudRemoteFile { RelPath = "G/P/x.zip", Modified = utcSav.AddMinutes(30) };   // 索引缺失（旧包）
    Check("切换比较: 云端存档更新 → 弹窗",
        CloudSyncService.ShouldPromptCloudNewer(locSav, cloudNewer, true));
    Check("切换比较: 云端存档更旧 → 不弹",
        !CloudSyncService.ShouldPromptCloudNewer(locSav, cloudOlder, false));
    Check("切换比较: 相差 30 秒（容差内）→ 不弹",
        !CloudSyncService.ShouldPromptCloudNewer(locSav, cloudClose, false));
    Check("切换比较: 本机无存档 → 弹窗",
        CloudSyncService.ShouldPromptCloudNewer(null, cloudNewer, false));
    Check("切换比较: 无索引回退上传时间 + 未知文件 → 弹窗",
        CloudSyncService.ShouldPromptCloudNewer(locSav, cloudNoIdx, false));
    Check("切换比较: 无索引 + 已知文件（防死循环）→ 不弹",
        !CloudSyncService.ShouldPromptCloudNewer(locSav, cloudNoIdx, true));
    Check("切换比较: 云端没有备份 → 不弹",
        !CloudSyncService.ShouldPromptCloudNewer(locSav, null, false));

    // 33.5 启动回填：旧记录 SavedAt==MinValue 从 manifest 补算；损坏/缺失包不误补；落盘持久化
    Store.HomeDirOverride = Path.Combine(temp, "vault_savedat2");
    Store.Init();
    string bfDir = Path.Combine(temp, "savedat_bf");
    Directory.CreateDirectory(bfDir);
    File.WriteAllText(Path.Combine(bfDir, "s.txt"), "bf");
    var gBf = new GameEntry { NameEN = "BackfillGame" };
    gBf.SavePaths.Add(new SavePathEntry { Path = bfDir });
    Store.Games.Add(gBf);
    var rBf = BackupService.Create(gBf, "回填测试");
    Check("回填: 新记录本身已有存档时间", rBf.SavedAt != DateTime.MinValue);
    DateTime wantSav = rBf.SavedAt;
    rBf.SavedAt = DateTime.MinValue;   // 模拟旧版本遗留记录
    int bfN = BackupService.BackfillSavedAt();
    Check("回填: 补算条数 ≥ 1", bfN >= 1, "n=" + bfN);
    Check("回填: 恢复为 manifest 存档时间", rBf.SavedAt == wantSav,
        $"{rBf.SavedAt:yyyy-MM-dd HH:mm:ss} vs {wantSav:yyyy-MM-dd HH:mm:ss}");
    var rFake = new BackupRecord { File = Path.Combine(temp, "幽灵.zip"), Time = DateTime.Now, Profile = "假玩家" };
    gBf.Backups.Add(rFake);
    int bfN2 = BackupService.BackfillSavedAt();
    Check("回填: 读不到 manifest 的记录保持 MinValue 且不计数",
        rFake.SavedAt == DateTime.MinValue && bfN2 == 0, "n=" + bfN2);
    Store.Init();   // 重新加载 games.json，验证落盘
    var rAfter = Store.Games.SelectMany(g => g.Backups).FirstOrDefault(b => b.Note == "回填测试");
    Check("回填: 持久化（重 Init 后 SavedAt 仍在）",
        rAfter != null && rAfter.SavedAt == wantSav,
        rAfter == null ? "记录丢失" : rAfter.SavedAt.ToString("yyyy-MM-dd HH:mm:ss"));

    Store.HomeDirOverride = null;

    // 33.6 已删重传状态机：云端删除后本地显「已删重传」，对账不补传，重传成功恢复「已传」
    {
        Store.HomeDirOverride = Path.Combine(temp, "vault_savedat3");
        Store.Init();
        string ruDir = Path.Combine(temp, "reup_saves");
        Directory.CreateDirectory(ruDir);
        File.WriteAllText(Path.Combine(ruDir, "s.bin"), "reup");
        var gRu = new GameEntry { NameEN = "ReupGame" };
        gRu.SavePaths.Add(new SavePathEntry { Path = ruDir });
        Store.Games.Add(gRu);
        var bRu = BackupService.Create(gRu, "重传测试");
        string ghostRel = "ReupGame/_通用/ghost.zip";
        // 伪造「曾上传、已从云端删除」的登记表（Deleted=true）；用序列化避免路径反斜杠转义问题
        var stateObj = new
        {
            Uploaded = new Dictionary<string, object>
            {
                [bRu.File] = new
                {
                    Size = new FileInfo(bRu.File).Length,
                    Mtime = new FileInfo(bRu.File).LastWriteTimeUtc.Ticks,
                    Remote = ghostRel,
                    When = new DateTime(2026, 9, 17, 20, 0, 0),
                    Deleted = true,
                },
            },
        };
        CloudSyncService.WaitStateSaved();
        File.WriteAllText(Store.CloudStateFile, JsonSerializer.Serialize(stateObj));
        CloudSyncService.LoadState();
        Check("已删重传: Deleted 登记 → IsUploaded=false（对账/自动上传不补传）",
            !CloudSyncService.IsUploaded(bRu.File));
        Check("已删重传: IsRemoteDeleted=true 且可反查本地文件",
            CloudSyncService.IsRemoteDeleted(bRu.File)
            && CloudSyncService.FindLocalByRemote(ghostRel) == bRu.File);
        CloudSyncService.RefreshCloudFlags(gRu);
        Check("已删重传: 备份记录显示已删重传态（非已传）",
            bRu.CloudDeleted && !bRu.CloudUploaded);
        Check("已删重传: 自动/批量上传闸门命中（对账、新备份事件、立即上传全部都跳过）",
            CloudSyncService.IsAutoUploadSkipped(bRu.File));
        Check("已删重传: 未知云端路径反查返回 null",
            CloudSyncService.FindLocalByRemote("No/Such/x.zip") == null);

        // 裸 TCP 假服务器（单 keep-alive 连接，二进制安全）：MKCOL×2 → PUT zip → GET index.json(404) → PUT index.json
        var l3 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l3.Start();
        int port3 = ((System.Net.IPEndPoint)l3.LocalEndpoint).Port;
        string? indexBody = null;
        async Task Serve3(System.Net.Sockets.NetworkStream stream)
        {
            while (true)
            {
                // 请求行
                var lineBuf = new MemoryStream();
                int prev = -1, ch;
                while ((ch = stream.ReadByte()) >= 0)
                {
                    if (prev == '\r' && ch == '\n') { lineBuf.SetLength(Math.Max(0, lineBuf.Length - 1)); break; }
                    lineBuf.WriteByte((byte)ch);
                    prev = ch;
                }
                if (ch < 0) return;   // 客户端关闭连接 → 服务结束
                string reqLine3 = System.Text.Encoding.ASCII.GetString(lineBuf.ToArray()).Trim();
                if (reqLine3.Length == 0) return;
                var parts = reqLine3.Split(' ');
                string method = parts[0];
                string path3 = Uri.UnescapeDataString(parts.ElementAtOrDefault(1) ?? "/");
                // 其余 header
                int contentLen3 = 0;
                var hdrStream = new MemoryStream();
                int p = -1, c2;
                while ((c2 = stream.ReadByte()) >= 0)
                {
                    if (p == '\r' && c2 == '\n')
                    {
                        hdrStream.SetLength(Math.Max(0, hdrStream.Length - 1));
                        string hl = System.Text.Encoding.ASCII.GetString(hdrStream.ToArray());
                        if (hl.Length == 0) break;
                        if (hl.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(hl.Split(':')[1], out contentLen3);
                        hdrStream.SetLength(0);
                        p = -1;
                        continue;
                    }
                    hdrStream.WriteByte((byte)c2);
                    p = c2;
                }
                // body（zip 二进制：按字节数读完即丢弃）
                var body3 = new byte[contentLen3];
                int got3 = 0;
                while (got3 < contentLen3)
                {
                    int n3 = stream.Read(body3, got3, contentLen3 - got3);
                    if (n3 == 0) break;
                    got3 += n3;
                }
                string resp;
                if (method == "MKCOL")
                    resp = "HTTP/1.1 201 Created\r\nContent-Length: 0\r\n\r\n";
                else if (method == "GET" && path3.EndsWith("/index.json"))
                    resp = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n";
                else if (method == "PUT" && path3.EndsWith("/index.json"))
                {
                    indexBody = System.Text.Encoding.UTF8.GetString(body3);
                    resp = "HTTP/1.1 201 Created\r\nContent-Length: 0\r\n\r\n";
                }
                else
                    resp = "HTTP/1.1 201 Created\r\nContent-Length: 0\r\n\r\n";
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(resp));
                await stream.FlushAsync();
            }
        }
        var serveDone = new TaskCompletionSource();
        var server3 = Task.Run(async () =>
        {
            try
            {
                using var c3 = await l3.AcceptTcpClientAsync();
                await Serve3(c3.GetStream());
            }
            catch { }
            serveDone.TrySetResult();
        });

        Store.Settings.CloudEnabled = true;
        Store.Settings.CloudUrl = $"http://127.0.0.1:{port3}/";
        Store.Settings.CloudUser = "u";
        Store.Settings.CloudSecret = CloudSecret.Protect("p");
        Store.SaveSettings();

        await CloudSyncService.UploadSingleAsync(gRu, bRu);
        Check("已删重传: 重传后 IsUploaded=true、Deleted 清零",
            CloudSyncService.IsUploaded(bRu.File) && !CloudSyncService.IsRemoteDeleted(bRu.File));
        Check("已删重传: 重传后自动上传闸门解除",
            !CloudSyncService.IsAutoUploadSkipped(bRu.File));
        Check("已删重传: 备份记录恢复已传态",
            bRu.CloudUploaded && !bRu.CloudDeleted);
        string expectRel = BackupService.SafeName(gRu.DisplayName) + "/" + ProfileService.DefaultFirstPlayerName + "/" + Path.GetFileName(bRu.File);
        Check("已删重传: Remote 更新为实际上传路径",
            CloudSyncService.FindLocalByRemote(expectRel) == bRu.File,
            CloudSyncService.FindLocalByRemote(expectRel) ?? "null");
        Check("已删重传: KnownRemote 重新登记（切换防死循环恢复）",
            CloudSyncService.IsRemoteKnown(expectRel));
        var idxDoc = CloudIndex.Deserialize(indexBody);
        Check("已删重传: 重传写回云端索引且含存档时间",
            idxDoc != null && idxDoc.Entries.ContainsKey(expectRel) && idxDoc.Entries[expectRel].T > 0,
            indexBody == null ? "未收到 index.json PUT" : "条目缺失");
        CloudSyncService.WaitStateSaved();
        string savedState = File.ReadAllText(Store.CloudStateFile);
        Check("已删重传: 登记表落盘 Deleted=false", savedState.Contains("\"Deleted\": false"),
            savedState.Replace("\r", "").Replace("\n", " ").Replace("  ", " "));
        Check("已删重传: 登记表不含 Deleted=true", !savedState.Contains("\"Deleted\": true"));
        Check("已删重传: 旧云端路径已清除", !savedState.Contains("ghost.zip"));

        await Task.WhenAny(serveDone.Task, Task.Delay(5000));
        l3.Stop();
        Store.HomeDirOverride = null;
    }
}

// ── 34. 玩家卡槽独立于备份：删光备份卡槽保留，只能右键删除玩家；名单持久化与旧数据回填 ──
{
    Store.HomeDirOverride = Path.Combine(temp, "vault_slots");
    Store.Init();
    string slDir = Path.Combine(temp, "slots_saves");
    Directory.CreateDirectory(slDir);
    File.WriteAllText(Path.Combine(slDir, "s.dat"), "开局");
    var gSl = new GameEntry { NameEN = "SlotGame" };
    gSl.SavePaths.Add(new SavePathEntry { Path = slDir });
    Store.Games.Add(gSl);

    ProfileService.CreatePlayer(gSl, "甲");
    gSl.RefreshProfiles();
    Check("卡槽: 新建玩家进入持久化名单", gSl.Players.SequenceEqual(new[] { "甲" }),
        string.Join(",", gSl.Players));
    Check("卡槽: 筹码与 HasProfileSlots 同步",
        gSl.HasProfileSlots && gSl.ProfileSlots.Any(v => v.Name == "甲" && v.IsActive));

    // 删光甲的全部备份（模拟旧 bug：此前卡槽会随之消失）
    foreach (var r in gSl.Backups.Where(b => b.Profile == "甲").ToList())
        BackupService.Delete(gSl, r);
    gSl.RefreshProfiles();
    Check("卡槽: 备份删光后卡槽保留且仍为当前玩家",
        ProfileService.ListSlots(gSl).SequenceEqual(new[] { "甲" })
        && gSl.ActiveProfile == "甲" && gSl.HasProfileSlots
        && gSl.VisibleBackups.Count == 0,
        $"slots={string.Join(",", ProfileService.ListSlots(gSl))},active={gSl.ActiveProfile}");

    // 名单持久化：重载 games.json 后卡槽仍在（核心回归）
    Store.Init();
    var gSl2 = Store.Games.First(g => g.NameEN == "SlotGame");
    Check("卡槽: 重启重载后零备份玩家卡槽仍在",
        gSl2.Players.SequenceEqual(new[] { "甲" }) && gSl2.ActiveProfile == "甲",
        $"players={string.Join(",", gSl2.Players)},active={gSl2.ActiveProfile}");

    // 右键删除玩家是移除卡槽的唯一途径（零备份也能删）
    int delN = ProfileService.DeletePlayer(gSl2, "甲");
    Check("卡槽: DeletePlayer 移除零备份卡槽",
        delN == 0 && gSl2.Players.Count == 0 && gSl2.ActiveProfile == "");

    // BackfillPlayers：旧版数据无 Players 字段，从备份归属按创建顺序回填
    string bf2Dir = Path.Combine(temp, "slots_bf_saves");
    Directory.CreateDirectory(bf2Dir);
    File.WriteAllText(Path.Combine(bf2Dir, "s.dat"), "x");
    var gOld = new GameEntry { NameEN = "OldSlotsGame" };
    gOld.SavePaths.Add(new SavePathEntry { Path = bf2Dir });
    Store.Games.Add(gOld);
    ProfileService.CreatePlayer(gOld, "先到");
    ProfileService.CreatePlayer(gOld, "后到");
    // 后到真实玩过并备份（新语义下零备份玩家不产生任何记录，无法被备份推导）
    File.WriteAllText(Path.Combine(bf2Dir, "s.dat"), "后到玩过");
    BackupService.Create(gOld, "后到的进度");
    gOld.Players.Clear();   // 模拟旧版 games.json：备份有归属、名单缺失
    Check("卡槽: 无名单时 ListSlots 临时回退备份推导",
        ProfileService.ListSlots(gOld).SequenceEqual(new[] { "先到", "后到" }));
    ProfileService.BackfillPlayers();
    Check("卡槽: 回填按创建顺序补齐名单",
        gOld.Players.SequenceEqual(new[] { "先到", "后到" }), string.Join(",", gOld.Players));
    // 模拟「备份已删光但名单已持久化」的玩家：回填不得让他复活或丢失
    foreach (var r in gOld.Backups.Where(b => b.Profile == "后到").ToList())
        BackupService.Delete(gOld, r);
    ProfileService.BackfillPlayers();
    Check("卡槽: 回填幂等且保留零备份玩家",
        gOld.Players.SequenceEqual(new[] { "先到", "后到" }), string.Join(",", gOld.Players));
    ProfileService.BackfillPlayers();   // 再跑一次
    Check("卡槽: 回填重复执行无副作用", gOld.Players.SequenceEqual(new[] { "先到", "后到" }));

    // 反序列化兼容：旧 JSON 完全没有 Players 字段
    string legacyJson = """{"NameEN":"LegacyJsonGame","ActiveProfile":"独行","Backups":[]}""";
    var gLegacy = JsonSerializer.Deserialize<GameEntry>(legacyJson)!;
    Check("卡槽: 旧 JSON（无 Players）反序列化不炸且名单为空",
        gLegacy.Players != null && gLegacy.Players.Count == 0 && gLegacy.ActiveProfile == "独行");

    Store.HomeDirOverride = null;
}

// ── 35. 切换游戏批量核验云状态：1 轮列表请求对账全部记录；结果落盘，重启（重 LoadState）后保留 ──
{
    Store.HomeDirOverride = Path.Combine(temp, "vault_verify");
    Store.Init();
    string vvDir = Path.Combine(temp, "verify_saves");
    Directory.CreateDirectory(vvDir);
    File.WriteAllText(Path.Combine(vvDir, "s.dat"), "v");
    var gVv = new GameEntry { NameEN = "VerifyGame" };
    gVv.SavePaths.Add(new SavePathEntry { Path = vvDir });
    Store.Games.Add(gVv);
    var bOn = BackupService.Create(gVv, "云端在");
    File.WriteAllText(Path.Combine(vvDir, "s.dat"), "v2");   // 改动存档，绕过「无变化」守卫
    var bGone = BackupService.Create(gVv, "云端没了");
    File.WriteAllText(Path.Combine(vvDir, "s.dat"), "v3");
    var bNever = BackupService.Create(gVv, "从未上传");

    // 伪造登记表：bOn / bGone 曾上传（云端实际只剩 bOn，bGone 将由核验翻成已删）；bNever 无登记
    var vvState = new
    {
        Uploaded = new Dictionary<string, object>
        {
            [bOn.File] = new
            {
                Size = new FileInfo(bOn.File).Length,
                Mtime = new FileInfo(bOn.File).LastWriteTimeUtc.Ticks,
                Remote = "VerifyGame/_通用/" + Path.GetFileName(bOn.File),
                When = new DateTime(2026, 9, 18, 12, 0, 0),
                Deleted = false,
            },
            [bGone.File] = new
            {
                Size = new FileInfo(bGone.File).Length,
                Mtime = new FileInfo(bGone.File).LastWriteTimeUtc.Ticks,
                Remote = "VerifyGame/_通用/" + Path.GetFileName(bGone.File),
                When = new DateTime(2026, 9, 18, 12, 0, 0),
                Deleted = false,
            },
        },
    };
    CloudSyncService.WaitStateSaved();
    File.WriteAllText(Store.CloudStateFile, JsonSerializer.Serialize(vvState));
    CloudSyncService.LoadState();

    // 裸 TCP 假服务器（单 keep-alive 连接，二进制安全）：
    // PROPFIND /VerifyGame → 207 multistatus（仅 bOn 一个 zip）；其余 → 404
    var l5 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    l5.Start();
    int port5 = ((System.Net.IPEndPoint)l5.LocalEndpoint).Port;
    string listName = Path.GetFileName(bOn.File);
    string multistatus =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
        "<D:multistatus xmlns:D=\"DAV:\">" +
        "<D:response><D:href>/VerifyGame/</D:href><D:propstat><D:prop>" +
        "<D:resourcetype><D:collection/></D:resourcetype></D:prop>" +
        "<D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>" +
        "<D:response><D:href>/VerifyGame/" + listName + "</D:href><D:propstat><D:prop>" +
        "<D:resourcetype/><D:getcontentlength>123</D:getcontentlength>" +
        "<D:getlastmodified>Thu, 01 Jan 2026 00:00:00 GMT</D:getlastmodified></D:prop>" +
        "<D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>" +
        "</D:multistatus>";
    byte[] listBody = System.Text.Encoding.UTF8.GetBytes(multistatus);
    async Task Serve5(System.Net.Sockets.NetworkStream stream)
    {
        while (true)
        {
            // 请求行
            var lineBuf5 = new MemoryStream();
            int prev5 = -1, ch5;
            while ((ch5 = stream.ReadByte()) >= 0)
            {
                if (prev5 == '\r' && ch5 == '\n') { lineBuf5.SetLength(Math.Max(0, lineBuf5.Length - 1)); break; }
                lineBuf5.WriteByte((byte)ch5);
                prev5 = ch5;
            }
            if (ch5 < 0) return;
            string reqLine5 = System.Text.Encoding.ASCII.GetString(lineBuf5.ToArray()).Trim();
            if (reqLine5.Length == 0) return;
            var parts5 = reqLine5.Split(' ');
            string method5 = parts5[0];
            string path5 = Uri.UnescapeDataString(parts5.ElementAtOrDefault(1) ?? "/");
            // 其余 header
            int contentLen5 = 0;
            var hdr5 = new MemoryStream();
            int p5 = -1, c5b;
            while ((c5b = stream.ReadByte()) >= 0)
            {
                if (p5 == '\r' && c5b == '\n')
                {
                    hdr5.SetLength(Math.Max(0, hdr5.Length - 1));
                    string hl = System.Text.Encoding.ASCII.GetString(hdr5.ToArray());
                    if (hl.Length == 0) break;
                    if (hl.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(hl.Split(':')[1], out contentLen5);
                    hdr5.SetLength(0);
                    p5 = -1;
                    continue;
                }
                hdr5.WriteByte((byte)c5b);
                p5 = c5b;
            }
            // body（PROPFIND 查询 XML：按字节数读完即丢弃）
            var body5 = new byte[contentLen5];
            int got5 = 0;
            while (got5 < contentLen5)
            {
                int n5 = stream.Read(body5, got5, contentLen5 - got5);
                if (n5 == 0) break;
                got5 += n5;
            }
            if (method5 == "PROPFIND" && path5.EndsWith("/VerifyGame"))
            {
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                    "HTTP/1.1 207 Multi-Status\r\nContent-Type: application/xml\r\nContent-Length: " +
                    listBody.Length + "\r\n\r\n"));
                await stream.WriteAsync(listBody);
            }
            else
            {
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                    "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n"));
            }
            await stream.FlushAsync();
        }
    }
    var serve5Done = new TaskCompletionSource();
    var server5 = Task.Run(async () =>
    {
        try { using var c5 = await l5.AcceptTcpClientAsync(); await Serve5(c5.GetStream()); }
        catch { }
        serve5Done.TrySetResult();
    });

    Store.Settings.CloudEnabled = true;
    Store.Settings.CloudUrl = $"http://127.0.0.1:{port5}/";
    Store.Settings.CloudUser = "u";
    Store.Settings.CloudSecret = CloudSecret.Protect("p");
    Store.SaveSettings();

    await CloudSyncService.VerifyGameCloudStatusAsync(gVv);
    Check("云核验: 登记过且云端在 → 已传", bOn.CloudUploaded && !bOn.CloudDeleted);
    Check("云核验: 登记过但云端无 → 已删重传", bGone.CloudDeleted && !bGone.CloudUploaded);
    Check("云核验: 从未上传 → 保持未上传", !bNever.CloudUploaded && !bNever.CloudDeleted);
    Check("云核验: Remote 对账为实际云端路径",
        CloudSyncService.FindLocalByRemote("VerifyGame/" + listName) == bOn.File,
        CloudSyncService.FindLocalByRemote("VerifyGame/" + listName) ?? "null");

    // 落盘与重启保留：重 LoadState 后已删/已传态仍在（切换游戏即时点亮的数据源）
    CloudSyncService.LoadState();
    CloudSyncService.RefreshCloudFlags(gVv);
    Check("云核验: 结果落盘，重启（重 LoadState）后状态保留",
        bGone.CloudDeleted && !bGone.CloudUploaded && bOn.CloudUploaded);

    await Task.WhenAny(serve5Done.Task, Task.Delay(5000));
    l5.Stop();
    Store.HomeDirOverride = null;
}

// ── 36. 云端备份防复活：本地删除的云端文件进「不再下载」黑名单，自动收集不再下载回来 ──
{
    Store.HomeDirOverride = Path.Combine(temp, "vault_dismiss");
    Store.Init();
    string dsDir = Path.Combine(temp, "dismiss_saves");
    Directory.CreateDirectory(dsDir);
    File.WriteAllText(Path.Combine(dsDir, "s.dat"), "1");
    var gDs = new GameEntry { NameEN = "DismissGame" };
    gDs.SavePaths.Add(new SavePathEntry { Path = dsDir });
    Store.Games.Add(gDs);
    var bD1 = BackupService.Create(gDs, "云端有");
    File.WriteAllText(Path.Combine(dsDir, "s.dat"), "2");   // 改动存档，绕过「无变化」守卫
    var bD2 = BackupService.Create(gDs, "纯本地");

    // 配置云同步（不连网：后续只走本地判定与下载计划，无真实请求）
    Store.Settings.CloudEnabled = true;
    Store.Settings.CloudUrl = "http://127.0.0.1:9/";
    Store.Settings.CloudUser = "u";
    Store.Settings.CloudSecret = CloudSecret.Protect("p");
    Store.SaveSettings();

    // 伪造登记表：bD1 曾上传（KnownRemote 记了云端路径）；bD2 从未上云
    string d1Name = Path.GetFileName(bD1.File);
    var dsState = new
    {
        Uploaded = new Dictionary<string, object>
        {
            [bD1.File] = new
            {
                Size = new FileInfo(bD1.File).Length,
                Mtime = new FileInfo(bD1.File).LastWriteTimeUtc.Ticks,
                Remote = "DismissGame/_通用/" + d1Name,
                When = new DateTime(2026, 9, 19, 12, 0, 0),
                Deleted = false,
            },
        },
        KnownRemote = new Dictionary<string, long>
        {
            ["DismissGame/_通用/" + d1Name] = 0L,
        },
    };
    CloudSyncService.WaitStateSaved();
    File.WriteAllText(Store.CloudStateFile, JsonSerializer.Serialize(dsState));
    CloudSyncService.LoadState();

    // 基线：本机已有同名文件的云端条目不进下载计划
    var remoteAll = new List<CloudRemoteFile>
    {
        new() { Name = d1Name, RelPath = "DismissGame/_通用/" + d1Name },
        new() { Name = Path.GetFileName(bD2.File), RelPath = "DismissGame/_通用/" + Path.GetFileName(bD2.File) },
    };
    Check("防复活: 基线——本机已有同名文件不下载", CloudSyncService.PlanDownloads(gDs, remoteAll, "DismissGame").Count == 0);

    // 删除 bD1（云端已知）→ 登记黑名单；bD2（从未上云）→ 不登记
    BackupService.Delete(gDs, bD1);
    Check("防复活: 删除云端已知的本地备份 → 登记不再下载",
        CloudSyncService.IsDismissed("DismissGame", d1Name));
    Check("防复活: 删除从未上云的备份 → 不误登记",
        !CloudSyncService.IsDismissed("DismissGame", Path.GetFileName(bD2.File)));

    // 模拟自动收集对账：云端还有 bD1 的 zip，但不再进入下载计划
    var remotePlan = new List<CloudRemoteFile>
    {
        new() { Name = d1Name, RelPath = "DismissGame/_通用/" + d1Name },
    };
    Check("防复活: 已删备份不再进入下载计划（不再反复复活）",
        CloudSyncService.PlanDownloads(gDs, remotePlan, "DismissGame").Count == 0);

    // 黑名单持久化：重 LoadState（模拟重启）后仍生效
    CloudSyncService.LoadState();
    Check("防复活: 黑名单落盘，重启后仍生效",
        CloudSyncService.IsDismissed("DismissGame", d1Name) &&
        CloudSyncService.PlanDownloads(gDs, remotePlan, "DismissGame").Count == 0);

    // 手动下载解除登记（云端浏览窗/切换玩家选云端最新的路径），恢复自动收集资格
    CloudSyncService.UndismissCloudBackup("DismissGame", d1Name);
    Check("防复活: 手动下载解除登记，恢复自动收集",
        CloudSyncService.PlanDownloads(gDs, remotePlan, "DismissGame").Count == 1);

    // 删除玩家：名下云端已知的备份一并进黑名单（防止被云收集复活成新玩家）
    Thread.Sleep(1100);   // 拉开与 bD1 的时间戳（备份文件名精确到秒，同秒会生成同名 zip 干扰墓碑键）
    File.WriteAllText(Path.Combine(dsDir, "s.dat"), "3");
    var bD3 = BackupService.Create(gDs, "乙的备份");
    bD3.Profile = "乙";
    CloudSyncService.MarkRemoteKnown("DismissGame/乙/" + Path.GetFileName(bD3.File));
    int dn = BackupService.DeletePlayerBackups(gDs, "乙");
    Check("防复活: 删除玩家 → 名下云端已知的备份也登记不再下载",
        dn == 1 && CloudSyncService.IsDismissed("DismissGame", Path.GetFileName(bD3.File)));

    // 墓碑回收 A：云端浏览窗删除云端副本（MarkCloudDeleted）→ 对应墓碑一并清除
    CloudSyncService.OnLocalBackupsDeleted(gDs, new[] { bD1.File });   // 重新登记（前面手动下载用例已解除）
    Check("墓碑回收: 前置——bD1 墓碑已登记", CloudSyncService.IsDismissed("DismissGame", d1Name));
    CloudSyncService.MarkCloudDeleted(bD1.File);
    Check("墓碑回收: 云端副本也被删除后墓碑清除", !CloudSyncService.IsDismissed("DismissGame", d1Name));
    CloudSyncService.LoadState();
    Check("墓碑回收: 清除结果落盘（重启不复活）",
        !CloudSyncService.IsDismissed("DismissGame", d1Name));

    // 墓碑回收 B：对照云端清单 GC——云端已无的墓碑清除、云端仍在的保留
    CloudSyncService.MarkRemoteKnown("DismissGame/_通用/stay.zip");
    CloudSyncService.MarkRemoteKnown("DismissGame/_通用/gone.zip");
    CloudSyncService.OnLocalBackupsDeleted(gDs, new[] { @"X:\anywhere\stay.zip" });
    CloudSyncService.OnLocalBackupsDeleted(gDs, new[] { @"X:\anywhere\gone.zip" });
    Check("墓碑回收: 前置——gone/stay 两块墓碑都在",
        CloudSyncService.IsDismissed("DismissGame", "gone.zip") &&
        CloudSyncService.IsDismissed("DismissGame", "stay.zip"));
    var remoteGc = new List<CloudRemoteFile>
    {
        new() { Name = "stay.zip", RelPath = "DismissGame/_通用/stay.zip" },
    };
    int gcN = CloudSyncService.GcDismissed("DismissGame", remoteGc);
    Check("墓碑回收: 云端已无的墓碑被清除",
        gcN >= 1 && !CloudSyncService.IsDismissed("DismissGame", "gone.zip"), "n=" + gcN);
    Check("墓碑回收: 云端仍在的墓碑保留",
        CloudSyncService.IsDismissed("DismissGame", "stay.zip"));
    CloudSyncService.LoadState();
    Check("墓碑回收: GC 结果落盘",
        !CloudSyncService.IsDismissed("DismissGame", "gone.zip") &&
        CloudSyncService.IsDismissed("DismissGame", "stay.zip"));

    // 墓碑回收 C：孤儿清扫——不在库游戏（已移除/改名）的墓碑启动时清除，在库游戏的保留
    var gGhost = new GameEntry { NameEN = "GhostGame" };   // 不加入 Store.Games，模拟已移出库的游戏
    CloudSyncService.MarkRemoteKnown("GhostGame/_通用/orphanX.zip");
    CloudSyncService.OnLocalBackupsDeleted(gGhost, new[] { @"X:\anywhere\orphanX.zip" });
    Check("墓碑回收: 前置——孤儿墓碑已登记",
        CloudSyncService.IsDismissed("GhostGame", "orphanX.zip"));
    int swept = CloudSyncService.SweepOrphanDismissed();
    Check("墓碑回收: 孤儿清扫——不在库游戏的墓碑被清",
        swept >= 1 && !CloudSyncService.IsDismissed("GhostGame", "orphanX.zip"), "n=" + swept);
    Check("墓碑回收: 孤儿清扫——在库游戏的墓碑保留",
        CloudSyncService.IsDismissed("DismissGame", "stay.zip"));
    CloudSyncService.LoadState();
    Check("墓碑回收: 孤儿清扫结果落盘",
        !CloudSyncService.IsDismissed("GhostGame", "orphanX.zip") &&
        CloudSyncService.IsDismissed("DismissGame", "stay.zip"));

    Store.HomeDirOverride = null;
}

// ── 37. 跨设备云端身份互认：AppId/别名/手动映射认领云端目录，多目录收集、索引自报、跨目录墓碑 ──
{
    Store.HomeDirOverride = Path.Combine(temp, "vault_identity");
    Store.Init();

    // 37.0 造一份真实备份 zip，充当「另一台电脑上传到其中文名目录」的云端备份
    string srcDir = Path.Combine(temp, "identity_src");
    Directory.CreateDirectory(srcDir);
    File.WriteAllText(Path.Combine(srcDir, "s.dat"), "src");
    var gSrc = new GameEntry { NameEN = "SourceGame", ActiveProfile = "甲" };
    gSrc.SavePaths.Add(new SavePathEntry { Path = srcDir });
    Store.Games.Add(gSrc);
    var bSrc = BackupService.Create(gSrc, "他机备份");
    byte[] zipBytes = File.ReadAllBytes(bSrc.File);
    string cloudZipName = Path.GetFileName(bSrc.File);

    // 37.1 本机游戏：显示名=英文名（模拟 B 机没跑过中文名纠正），AppId 与另一台相同
    string idDir = Path.Combine(temp, "identity_saves");
    Directory.CreateDirectory(idDir);
    File.WriteAllText(Path.Combine(idDir, "s.dat"), "1");
    var gId = new GameEntry { NameEN = "OnlyEnName", AppId = 55500 };
    gId.SavePaths.Add(new SavePathEntry { Path = idDir });
    Store.Games.Add(gId);
    // 别名认领用：另一游戏的中文名命中 ShadowRealm 的别名表
    var gAlias = new GameEntry { NameCN = "OldSpellName" };
    Store.Games.Add(gAlias);

    // 云端索引（另一台电脑写的）：ShadowRealm 目录自报 AppId 55500 + 别名
    string indexJson = JsonSerializer.Serialize(new
    {
        V = 1,
        Entries = new Dictionary<string, object>(),
        Games = new Dictionary<string, object>
        {
            ["ShadowRealm"] = new { A = 55500, Al = new[] { "ShadowRealm", "OldSpellName" } },
            ["OnlyEnName"] = new { A = 0, Al = new[] { "OnlyEnName" } },
        },
    });
    string indexBody37 = indexJson;   // 服务器 GET /index.json 返回内容（PUT 后不更新——自报断言用捕获的 PUT body）

    // 假服务器：PROPFIND /ShadowRealm → 207 含 zip；GET zip → zip 字节；GET index.json → 200；
    // 其余 PROPFIND → 404（空目录）；PUT/MKCOL → 201（PUT index.json 捕获 body 供自报断言）
    var l6 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    l6.Start();
    int port6 = ((System.Net.IPEndPoint)l6.LocalEndpoint).Port;
    string? putIndexBody = null;
    string multistatus6 =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
        "<D:multistatus xmlns:D=\"DAV:\">" +
        "<D:response><D:href>/ShadowRealm/</D:href><D:propstat><D:prop>" +
        "<D:resourcetype><D:collection/></D:resourcetype></D:prop>" +
        "<D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>" +
        "<D:response><D:href>/ShadowRealm/甲/" + cloudZipName + "</D:href><D:propstat><D:prop>" +
        "<D:resourcetype/><D:getcontentlength>" + zipBytes.Length + "</D:getcontentlength>" +
        "<D:getlastmodified>Thu, 01 Jan 2026 00:00:00 GMT</D:getlastmodified></D:prop>" +
        "<D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>" +
        "</D:multistatus>";
    byte[] ms6 = System.Text.Encoding.UTF8.GetBytes(multistatus6);
    byte[] idx6 = System.Text.Encoding.UTF8.GetBytes(indexBody37);
    async Task Serve6(System.Net.Sockets.NetworkStream stream)
    {
        while (true)
        {
            var lineBuf = new MemoryStream();
            int prev = -1, ch;
            while ((ch = stream.ReadByte()) >= 0)
            {
                if (prev == '\r' && ch == '\n') { lineBuf.SetLength(Math.Max(0, lineBuf.Length - 1)); break; }
                lineBuf.WriteByte((byte)ch);
                prev = ch;
            }
            if (ch < 0) return;
            string reqLine = System.Text.Encoding.ASCII.GetString(lineBuf.ToArray()).Trim();
            if (reqLine.Length == 0) return;
            var parts = reqLine.Split(' ');
            string method = parts[0];
            string path6 = Uri.UnescapeDataString(parts.ElementAtOrDefault(1) ?? "/");
            int contentLen = 0;
            var hdr = new MemoryStream();
            int p = -1, cb;
            while ((cb = stream.ReadByte()) >= 0)
            {
                if (p == '\r' && cb == '\n')
                {
                    hdr.SetLength(Math.Max(0, hdr.Length - 1));
                    string hl = System.Text.Encoding.ASCII.GetString(hdr.ToArray());
                    if (hl.Length == 0) break;
                    if (hl.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(hl.Split(':')[1], out contentLen);
                    hdr.SetLength(0);
                    p = -1;
                    continue;
                }
                hdr.WriteByte((byte)cb);
                p = cb;
            }
            var body = new byte[contentLen];
            int got = 0;
            while (got < contentLen)
            {
                int n = stream.Read(body, got, contentLen - got);
                if (n == 0) break;
                got += n;
            }
            byte[] respBody;
            string head;
            if (method == "PROPFIND" && path6.EndsWith("/ShadowRealm"))
            {
                head = "HTTP/1.1 207 Multi-Status\r\nContent-Type: application/xml\r\nContent-Length: " + ms6.Length + "\r\n\r\n";
                respBody = ms6;
            }
            else if (method == "GET" && path6.EndsWith("/index.json"))
            {
                head = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + idx6.Length + "\r\n\r\n";
                respBody = idx6;
            }
            else if (method == "GET" && path6.EndsWith("/" + cloudZipName))
            {
                head = "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: " + zipBytes.Length + "\r\n\r\n";
                respBody = zipBytes;
            }
            else if (method == "PUT" && path6.EndsWith("/index.json"))
            {
                putIndexBody = System.Text.Encoding.UTF8.GetString(body);
                head = "HTTP/1.1 201 Created\r\nContent-Length: 0\r\n\r\n";
                respBody = Array.Empty<byte>();
            }
            else if (method == "PROPFIND")
            {
                head = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n";
                respBody = Array.Empty<byte>();
            }
            else
            {
                head = "HTTP/1.1 201 Created\r\nContent-Length: 0\r\n\r\n";
                respBody = Array.Empty<byte>();
            }
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(head));
            if (respBody.Length > 0) await stream.WriteAsync(respBody);
            await stream.FlushAsync();
        }
    }
    var serve6Done = new TaskCompletionSource();
    var server6 = Task.Run(async () =>
    {
        try
        {
            // Accept 循环：37 节连续多个云方法各自新建客户端（多条 TCP 连接），逐条接待
            while (true)
            {
                var c6 = await l6.AcceptTcpClientAsync();
                _ = Task.Run(async () => { try { using (c6) await Serve6(c6.GetStream()); } catch { } });
            }
        }
        catch { }
    });

    Store.Settings.CloudEnabled = true;
    Store.Settings.CloudUrl = $"http://127.0.0.1:{port6}/";
    Store.Settings.CloudUser = "u";
    Store.Settings.CloudSecret = CloudSecret.Protect("p");
    Store.SaveSettings();

    // 37.2 拉索引：第一次候选只有默认名（身份缓存空）→ 列 OnlyEnName → 404 空清单；索引已并入缓存
    await CloudSyncService.FetchGameIndexAsync(gId);
    Check("互认: 首次拉索引并入身份缓存", CloudSyncService.CloudDirsFor(gId).Count >= 2);

    // 37.3 AppId 认领：索引记录 AppId 相同 → 目录进入候选集合，且排在默认名之前
    var dirs37 = CloudSyncService.CloudDirsFor(gId);
    Check("互认: AppId 命中他机目录", dirs37.Contains("ShadowRealm") && dirs37.Contains("OnlyEnName"),
        string.Join(",", dirs37));
    Check("互认: 认领顺序——AppId 命中段排在默认名前", dirs37[0] == "ShadowRealm");

    // 37.4 别名认领：另一游戏的中文名在索引别名表里 → 同样命中该目录
    Check("互认: 别名命中他机目录", CloudSyncService.CloudDirsFor(gAlias).Contains("ShadowRealm"));

    // 37.5 多目录收集：默认名目录 404（空），他机中文名目录的 zip 被下载并落库建档
    var (nb, np) = await CloudSyncService.CollectGameAsync(gId);
    var rCloud = gId.Backups.FirstOrDefault(b => Path.GetFileName(b.File) == cloudZipName);
    Check("互认: 收集到他机目录的备份", nb == 1 && rCloud != null, $"nb={nb}");
    Check("互认: 云端新玩家随 zip 建档", np == 1 && rCloud!.Profile == "甲", $"np={np}");

    // 37.6 跨目录墓碑：删除收集来的备份 → 墓碑登记到实际云端段（ShadowRealm）而非默认段
    BackupService.Delete(gId, rCloud!);
    Check("互认: 删除后墓碑登记到实际云端目录段", CloudSyncService.IsDismissed("ShadowRealm", cloudZipName));
    Check("互认: 默认目录段无此墓碑", !CloudSyncService.IsDismissed("OnlyEnName", cloudZipName));
    CloudSyncService.SweepOrphanDismissed();
    Check("互认: 孤儿清扫不误删候选段的墓碑", CloudSyncService.IsDismissed("ShadowRealm", cloudZipName));

    // 37.7 上传自报身份：手动上传本机备份 → PUT index.json 的 Games 段带 AppId 与全部名字
    File.WriteAllText(Path.Combine(idDir, "s.dat"), "2");
    var bId = BackupService.Create(gId, "本机备份");
    await CloudSyncService.UploadSingleAsync(gId, bId);
    Check("互认: 上传后 ☁ 已传", CloudSyncService.IsUploaded(bId.File));
    Check("互认: 索引自报 AppId", putIndexBody != null && putIndexBody.Contains("\"A\":55500"),
        putIndexBody?.Replace("\r", "").Replace("\n", "") ?? "null");
    Check("互认: 索引自报别名沉淀", putIndexBody != null && putIndexBody.Contains("OnlyEnName"));

    // 37.8 手动映射：优先级最高；落盘持久化；解除后失效
    CloudSyncService.SetMapping("ThirdDir", BackupService.SafeName(gId.DisplayName));
    var dirsMapped = CloudSyncService.CloudDirsFor(gId);
    Check("互认: 手动映射加入候选且优先级最高", dirsMapped[0] == "ThirdDir", string.Join(",", dirsMapped));
    CloudSyncService.LoadState();
    Check("互认: 映射落盘持久化", CloudSyncService.GetMapping("ThirdDir") == "OnlyEnName",
        CloudSyncService.GetMapping("ThirdDir") ?? "null");
    // 孤儿目录判定：已被认领（映射/AppId/默认名）的目录不在未匹配清单，陌生目录在
    var unclaimed = CloudSyncService.FindUnclaimedDirs(new[] { "ThirdDir", "ShadowRealm", "OnlyEnName", "TotalGhost" });
    Check("互认: 未匹配清单只含真正的孤儿目录",
        unclaimed.Count == 1 && unclaimed[0] == "TotalGhost", string.Join(",", unclaimed));
    CloudSyncService.SetMapping("ThirdDir", null);
    Check("互认: 解除映射后该目录重新成为孤儿",
        CloudSyncService.FindUnclaimedDirs(new[] { "ThirdDir" }).Contains("ThirdDir"));
    Check("互认: 解除映射后失效", CloudSyncService.GetMapping("ThirdDir") == null &&
        !CloudSyncService.CloudDirsFor(gId).Contains("ThirdDir"));

    await Task.WhenAny(serve6Done.Task, Task.Delay(5000));
    l6.Stop();
    Store.HomeDirOverride = null;
}

// ── 38. AppId 搜索源：Steam 官方优先 / SteamTools 补充带 Referer / 中文前缀缩短回退 ──
{
    Store.HomeDirOverride = Path.Combine(temp, "vault_appidsrc");
    Store.Init();

    var srcs38 = AppIdSourceService.GetSources();
    Check("AppId源: 内置两源且 Steam 官方优先",
        srcs38.Count >= 2 && srcs38[0].Id == "steam_store" && srcs38[1].Id == "steamtools",
        string.Join(",", srcs38.Select(s => s.Id)));
    var storeSrc38 = srcs38.First(s => s.Id == "steam_store");
    Check("AppId源: 官方源走 storesearch 且 cc=hk",
        storeSrc38.UrlTemplate.Contains("storesearch") && storeSrc38.UrlTemplate.Contains("cc=hk"),
        storeSrc38.UrlTemplate);
    Check("AppId源: 官方源列表路径 items / 封面 tiny_image",
        storeSrc38.ListPath == "items" && storeSrc38.ImageField == "tiny_image");
    var toolsSrc38 = srcs38.First(s => s.Id == "steamtools");
    Check("AppId源: SteamTools 补 Referer（否则 403）",
        toolsSrc38.Referer == "https://steamtools.games/", toolsSrc38.Referer);

    // 内置源技术字段合并覆盖：模拟用户 settings 里残留旧的空 Referer/旧 URL，合并后应被内置值覆盖
    Store.Settings.CustomAppIdSources = new List<AppIdSourceDef>
    {
        new() { Id = "steamtools", DisplayName = "旧", UrlTemplate = "https://old/x",
                ListPath = "old", IdField = "x", NameField = "x", Referer = "" },
    };
    Store.SaveSettings();
    var merged38 = AppIdSourceService.GetSources();
    var toolsMerged38 = merged38.First(s => s.Id == "steamtools");
    Check("AppId源: 旧缓存的技术字段被内置值覆盖",
        toolsMerged38.UrlTemplate.Contains("steamtools.games") &&
        toolsMerged38.Referer == "https://steamtools.games/", toolsMerged38.UrlTemplate);

    // 中文前缀缩短：逐字去尾（最短保留 2 字）
    var pre38 = AppIdSourceService.PrefixCandidates("黑神话悟空");
    Check("AppId源: 中文前缀序列 黑神话悟/黑神话/黑神",
        pre38.Count == 3 && pre38[0] == "黑神话悟" && pre38[1] == "黑神话" && pre38[2] == "黑神",
        string.Join(",", pre38));
    Check("AppId源: 带标点名同样缩短",
        AppIdSourceService.PrefixCandidates("黑神话：悟空").Contains("黑神话："));
    Check("AppId源: 英文查询不产生前缀序列",
        AppIdSourceService.PrefixCandidates("Witcher").Count == 0);
    Check("AppId源: 单字查询不产生前缀序列",
        AppIdSourceService.PrefixCandidates("黑").Count == 0);

    // 名称净化：接口返回名中的中文括号/中文后缀不能进英文名框
    Check("净化: 全角中文括号去除", AppIdSourceService.CleanEnglishName(
        "Wo Long: Fallen Dynasty （卧龙：苍天陨落）") == "Wo Long: Fallen Dynasty");
    Check("净化: 括号外中文 DLC 后缀截断", AppIdSourceService.CleanEnglishName(
        "Wo Long: Fallen Dynasty 风起荆襄") == "Wo Long: Fallen Dynasty");
    Check("净化: 半角中文括号去除", AppIdSourceService.CleanEnglishName(
        "Game (中文名)") == "Game");
    Check("净化: 英文括号保留", AppIdSourceService.CleanEnglishName(
        "Game (Special)") == "Game (Special)");
    Check("净化: 纯英文名原样", AppIdSourceService.CleanEnglishName(
        "The Witcher 3") == "The Witcher 3");
    Check("净化: 纯中文名返回空", AppIdSourceService.CleanEnglishName(
        "黑神话：悟空") == "");

    // 分类：主游戏 / 季票 / 前缀扩展 DLC / 试玩；版本名仍算主游戏
    var wo38 = new List<string>
    {
        "Wo Long: Fallen Dynasty （卧龙：苍天陨落）",
        "Wo Long: Fallen Dynasty 季票",
        "Wo Long: Fallen Dynasty 风起荆襄",
        "Wo Long: Fallen Dynasty Complete Edition Demo",
    };
    Check("分类: 主游戏", AppIdSourceService.ClassifyResultKind(wo38[0], wo38) == "game");
    Check("分类: 季票", AppIdSourceService.ClassifyResultKind(wo38[1], wo38) == "season");
    Check("分类: 前缀扩展为 DLC", AppIdSourceService.ClassifyResultKind(wo38[2], wo38) == "dlc");
    Check("分类: Demo 优先", AppIdSourceService.ClassifyResultKind(wo38[3], wo38) == "demo");
    Check("分类: 版本名仍算主游戏", AppIdSourceService.ClassifyResultKind(
        "The Witcher 3: Wild Hunt - Complete Edition",
        new[] { "The Witcher 3: Wild Hunt", "The Witcher 3: Wild Hunt - Complete Edition" }) == "game");

    Store.HomeDirOverride = null;
}

// ── 39. 备份记录相对路径存储 + 目录迁移认领 + 云端删除设置 ──
{
    Store.HomeDirOverride = Path.Combine(temp, "vault_relpath");
    Store.Init();

    string root39 = Path.GetFullPath(Store.BackupRoot);

    // 39.1 StorableFile / ResolveFile 往返
    string zip39 = Path.Combine(root39, "MyGame", "MyGame_2026-01-01_120000.zip");
    string rel39 = BackupRecord.StorableFile(zip39);
    Check("相对路径: 根下绝对→相对存储", rel39 == "MyGame\\MyGame_2026-01-01_120000.zip", rel39);
    Check("相对路径: 解析还原逐字节一致", BackupRecord.ResolveFile(rel39) == zip39,
        BackupRecord.ResolveFile(rel39));

    string out39 = Path.Combine(temp, "outside", "x.zip");
    Check("相对路径: 根外路径存绝对", BackupRecord.StorableFile(out39) == out39,
        BackupRecord.StorableFile(out39));
    Check("相对路径: 根目录自身按绝对处理", BackupRecord.StorableFile(root39) == root39,
        BackupRecord.StorableFile(root39));
    string escInput39 = Path.Combine(root39, "..", "evil.zip");
    string escFull39 = Path.GetFullPath(escInput39);
    Check("相对路径: 逃逸根目录按绝对处理",
        BackupRecord.StorableFile(escInput39) == escFull39 &&
        !BackupRecord.StorableFile(escInput39).Contains(".."));

    // 39.2 记录 File setter/getter + JSON 往返（games.json 键名 "File" 双向兼容）
    var rec39 = new BackupRecord { Time = DateTime.Now };
    rec39.File = zip39;
    Check("相对路径: setter 转相对存储", rec39.FileStored == rel39, rec39.FileStored);
    Check("相对路径: getter 还原绝对", rec39.File == zip39, rec39.File);
    string json39 = JsonSerializer.Serialize(rec39);
    Check("相对路径: 序列化写相对不写绝对",
        json39.Contains("MyGame\\\\MyGame") && !json39.Contains(zip39), json39);
    var back39 = JsonSerializer.Deserialize<BackupRecord>(json39);
    Check("相对路径: 反序列化还原绝对与存储值",
        back39 != null && back39.File == zip39 && back39.FileStored == rel39);

    // 39.3 旧版绝对路径 JSON 兼容：反序列化即归一为相对存储
    var oldRec39 = JsonSerializer.Deserialize<BackupRecord>(
        JsonSerializer.Serialize(new { Time = DateTime.Now, File = zip39 }))!;
    Check("相对路径: 旧版绝对 JSON getter 得绝对", oldRec39.File == zip39, oldRec39.File);
    Check("相对路径: 旧记录加载即归一为相对", oldRec39.FileStored == rel39, oldRec39.FileStored);

    // 39.4 目录迁移认领：相对记录与旧版绝对记录均可对位
    string saveDir39 = Path.Combine(temp, "mig_saves");
    Directory.CreateDirectory(saveDir39);
    File.WriteAllText(Path.Combine(saveDir39, "s.txt"), "1");
    var g39 = new GameEntry { NameCN = "迁移认领游戏" };
    g39.SavePaths.Add(new SavePathEntry { Path = saveDir39 });
    Store.Games.Add(g39);
    var b39a = BackupService.Create(g39, "甲");
    File.WriteAllText(Path.Combine(saveDir39, "s.txt"), "22");
    var b39b = BackupService.Create(g39, "乙");
    Check("迁移: 新建记录为相对存储",
        !Path.IsPathRooted(b39a.FileStored) && !Path.IsPathRooted(b39b.FileStored) &&
        b39a.File == Path.Combine(root39, b39a.FileStored));

    // 模拟用户改备份目录：切到空的新根 → 全部记录解析缺失
    string newRoot39 = Path.Combine(temp, "vault_relpath_newroot");
    Directory.CreateDirectory(newRoot39);
    Store.Settings.BackupDir = newRoot39;
    Store.SaveSettings();
    Check("迁移: 切根后记录缺失", !File.Exists(b39a.File) && !File.Exists(b39b.File));

    // 旧版绝对路径记录：指向旧根内真实存在的 zip（新根之外 → 按绝对存储）
    string legacyZip39 = Path.Combine(root39, BackupService.SafeName(g39.DisplayName), "legacy_abs.zip");
    File.Copy(Path.Combine(root39, b39a.FileStored), legacyZip39);
    var recAbs39 = new BackupRecord { Time = DateTime.Now, Note = "旧版绝对" };
    recAbs39.File = legacyZip39;
    g39.Backups.Add(recAbs39);
    Check("迁移: 旧根路径按绝对存储", Path.IsPathRooted(recAbs39.FileStored), recAbs39.FileStored);

    var (missing39, claim39) = BackupService.ScanReclaimFromRoot(root39);
    Check("迁移: 相对记录缺失计 2", missing39 == 2, missing39.ToString());
    Check("迁移: 3 条均可认领（含旧绝对记录）", claim39.Count == 3, claim39.Count.ToString());
    Check("迁移: 候选目标位于新根",
        claim39.All(c => c.NewAbs.StartsWith(newRoot39, StringComparison.OrdinalIgnoreCase)),
        string.Join(" | ", claim39.Select(c => c.NewAbs)));

    int moved39 = BackupService.ReclaimFromRoot(claim39);
    Check("迁移: 移动 3 份", moved39 == 3, moved39.ToString());
    Check("迁移: 相对记录解析到新根且文件在位",
        File.Exists(b39a.File) && File.Exists(b39b.File) &&
        b39a.File.StartsWith(newRoot39, StringComparison.OrdinalIgnoreCase));
    Check("迁移: 旧版绝对记录改写为相对存储", !Path.IsPathRooted(recAbs39.FileStored),
        recAbs39.FileStored);
    Check("迁移: 改写后解析回新根位置",
        recAbs39.File == Path.Combine(newRoot39, recAbs39.FileStored) && File.Exists(recAbs39.File));
    Check("迁移: 旧根遗留文件已清空", !File.Exists(legacyZip39));

    // 39.5 云端删除设置默认值
    Check("云删: CloudDelWithCloud 默认不删云端", !new AppSettings().CloudDelWithCloud);
    Check("云删: PrevBackupDir 默认空", new AppSettings().PrevBackupDir == "");

    // 39.6 默认卡槽名称可设置
    var appS = new AppSettings();
    appS.DefaultPlayerName = "我的玩家";
    Store.HomeDirOverride = Path.Combine(temp, "vault_dpn");
    Store.Init();
    Store.Settings.DefaultPlayerName = "我的玩家";
    Check("卡槽名: DefaultFirstPlayerName 读取设置值",
        ProfileService.DefaultFirstPlayerName == "我的玩家",
        $"got={ProfileService.DefaultFirstPlayerName}");
    var gDpn = new GameEntry { NameCN = "默认名测试" };
    Store.Games.Add(gDpn);
    Check("卡槽名: 新游戏虚拟卡槽显示设置的名称",
        ProfileService.ListSlots(gDpn).SequenceEqual(new[] { "我的玩家" }));
    Store.HomeDirOverride = null;
}

// ── 40. 备份锁定防滚动删除 + 默认玩家1 ──
{
    Store.HomeDirOverride = Path.Combine(temp, "vault_lock");
    Store.Init();

    // 40.1 锁定的备份不被滚动清理
    string lockSave = Path.Combine(temp, "lock_saves");
    Directory.CreateDirectory(lockSave);
    var glock = new GameEntry { NameCN = "锁定测试游戏" };
    glock.SavePaths.Add(new SavePathEntry { Path = lockSave });
    Store.Games.Add(glock);
    Store.Settings.MaxKeepPerGame = 10;   // 建备份期间保持高配额（Create 末尾会调 Prune），建完再降到 2

    File.WriteAllText(Path.Combine(lockSave, "s.txt"), "1");
    var lk1 = BackupService.Create(glock, "第一份");
    File.WriteAllText(Path.Combine(lockSave, "s.txt"), "22");
    var lk2 = BackupService.Create(glock, "第二份");
    File.WriteAllText(Path.Combine(lockSave, "s.txt"), "333");
    var lk3 = BackupService.Create(glock, "第三份");
    File.WriteAllText(Path.Combine(lockSave, "s.txt"), "4444");
    var lk4 = BackupService.Create(glock, "第四份");
    Check("锁定: 初始 4 份",
        glock.Backups.Count == 4 && glock.Backups.Contains(lk2), glock.Backups.Count.ToString());

    // 锁定最旧的第一份（列表新→旧，lk1 是最旧候选）
    lk1.Locked = true;
    Store.Settings.MaxKeepPerGame = 2;
    BackupService.Prune(glock);
    Check("锁定: 清理后保留 3 份（2 常规 + 1 锁定）",
        glock.Backups.Count == 3, glock.Backups.Count.ToString());
    Check("锁定: 锁定的最旧备份仍在",
        glock.Backups.Contains(lk1) && File.Exists(lk1.File), lk1.FileStored);
    Check("锁定: 被淘汰的是未锁定的旧项（lk2）",
        glock.Backups.Contains(lk3) && glock.Backups.Contains(lk4) &&
        !glock.Backups.Contains(lk2));

    // 全部锁定 → 一份都不能再删
    foreach (var b in glock.Backups) b.Locked = true;
    BackupService.Prune(glock);
    Check("锁定: 全部锁定时不淘汰", glock.Backups.Count == 3, glock.Backups.Count.ToString());

    // 解锁后再清理 → 回到保留份数
    foreach (var b in glock.Backups) b.Locked = false;
    BackupService.Prune(glock);
    Check("锁定: 解锁后正常清理到 2 份",
        glock.Backups.Count == 2, glock.Backups.Count.ToString());

    // 40.2 形式玩家1：新游戏入库零磁盘副作用，第一份真实存档才实体化
    string freshSave = Path.Combine(temp, "fresh_saves");
    Directory.CreateDirectory(freshSave);
    var gfresh = new GameEntry { NameCN = "新游戏" };
    gfresh.SavePaths.Add(new SavePathEntry { Path = freshSave });
    Store.Games.Add(gfresh);
    Check("形式卡槽: 入库 Players 空、ActiveProfile 空",
        gfresh.Players.Count == 0 && gfresh.ActiveProfile == "");
    Check("形式卡槽: ListSlots 虚拟返回玩家1",
        ProfileService.ListSlots(gfresh).SequenceEqual(new[] { "玩家1" }));
    gfresh.RefreshProfiles();
    Check("形式卡槽: 筹码显示玩家1且为激活态",
        gfresh.ProfileSlots.Count == 1 && gfresh.ProfileSlots[0].Name == "玩家1"
        && gfresh.ProfileSlots[0].IsActive);
    // 磁盘零副作用：备份目录与 profiles\{id} 都不存在
    Check("形式卡槽: 备份目录未建", !Directory.Exists(BackupService.BackupDirFor(gfresh)));
    Check("形式卡槽: profiles 身份证目录未建",
        !Directory.Exists(Path.Combine(Store.BackupRoot, "_appdata", "profiles", gfresh.Id)));
    // 空存档目录不打包
    bool threwEmpty40 = false;
    try { BackupService.Create(gfresh, "空备份"); }
    catch (InvalidOperationException ex) { threwEmpty40 = ex.Message.Contains("尚未产生存档"); }
    Check("形式卡槽: 空目录不产生备份", threwEmpty40);

    // 真实存档出现 → 玩家1实体化
    File.WriteAllText(Path.Combine(freshSave, "a.sav"), "data");
    var freshBak = BackupService.Create(gfresh, "第一份存档");
    Check("形式卡槽: 首次备份后 Players=[玩家1] 且激活",
        gfresh.Players.Count == 1 && gfresh.Players[0] == "玩家1"
        && gfresh.ActiveProfile == "玩家1");
    Check("形式卡槽: 备份归属玩家1、目录与 game.json 已落盘",
        freshBak.Profile == "玩家1"
        && File.Exists(Path.Combine(ProfileService.RootFor(gfresh), "game.json"))
        && Directory.Exists(BackupService.PlayerBackupDir(gfresh, "玩家1")));

    // 40.3 第一位显式玩家承接虚拟卡槽位置（不产生多余的玩家1）
    string namedSave = Path.Combine(temp, "named_saves");
    Directory.CreateDirectory(namedSave);
    File.WriteAllText(Path.Combine(namedSave, "n.sav"), "x");
    var gnamed = new GameEntry { NameCN = "命名游戏" };
    gnamed.SavePaths.Add(new SavePathEntry { Path = namedSave });
    Store.Games.Add(gnamed);
    ProfileService.CreatePlayer(gnamed, "小明");
    Check("形式卡槽: 首位命名玩家直接占位（Players=[小明]，不多出玩家1）",
        gnamed.Players.Count == 1 && gnamed.Players[0] == "小明"
        && gnamed.ActiveProfile == "小明"
        && gnamed.Backups.All(b => b.Profile == "小明"));

    // 40.4 旧版通用备份（Profile 空、zip 在游戏通用备份根）启动迁移认领给玩家1
    var gleg = new GameEntry { NameCN = "老游戏" };
    string legacyRoot = BackupService.BackupDirFor(gleg);
    Directory.CreateDirectory(legacyRoot);
    string legacyZip = Path.Combine(legacyRoot, "legacy_old.zip");
    File.Copy(freshBak.File, legacyZip);
    gleg.Backups.Add(new BackupRecord
    {
        File = legacyZip, Profile = "", Time = DateTime.Now.AddDays(-2),
        Size = new FileInfo(legacyZip).Length
    });
    Store.Games.Add(gleg);
    int nLeg40 = ProfileService.MaterializeLegacyDefaultPlayers();
    Check("形式卡槽: 旧通用备份迁移数 1", nLeg40 == 1, nLeg40.ToString());
    Check("形式卡槽: 老游戏 Players=[玩家1]、备份已认领",
        gleg.Players.Count == 1 && gleg.ActiveProfile == "玩家1"
        && gleg.Backups.All(b => b.Profile == "玩家1")
        && Directory.Exists(BackupService.PlayerBackupDir(gleg, "玩家1")));
    // 幂等：再跑一次不重复迁移
    Check("形式卡槽: 旧迁移幂等", ProfileService.MaterializeLegacyDefaultPlayers() == 0);

    // 40.4b 实体版遗留：零备份的虚假玩家1启动时回迁虚拟态
    var gdem = new GameEntry { NameCN = "回迁测试游戏" };
    gdem.Players.Add("玩家1");
    gdem.ActiveProfile = "玩家1";
    Store.Games.Add(gdem);
    Check("形式卡槽: 零备份虚假玩家1回迁数 1",
        ProfileService.DematerializeEmptyDefaultPlayers() == 1);
    Check("形式卡槽: 回迁后 Players/激活态为空、ListSlots 仍虚拟显示玩家1",
        gdem.Players.Count == 0 && gdem.ActiveProfile == ""
        && ProfileService.ListSlots(gdem).SequenceEqual(new[] { "玩家1" }));
    Check("形式卡槽: 回迁幂等", ProfileService.DematerializeEmptyDefaultPlayers() == 0);

    // 40.5 云存档游戏：不虚拟玩家1
    var gcloud40 = new GameEntry { NameCN = "云存档游戏" };
    gcloud40.SavePaths.Add(new SavePathEntry { Path = freshSave, Source = "Steam云" });
    Store.Games.Add(gcloud40);
    Check("形式卡槽: 云游戏不虚拟",
        !ProfileService.IsDefaultPlayerApplicable(gcloud40)
        && ProfileService.ListSlots(gcloud40).Count == 0
        && gcloud40.Players.Count == 0);

    // 40.6 虚拟玩家1改名 → 实体化；删除该玩家 → 回到虚拟态；虚拟玩家1禁止删除
    var gv40 = new GameEntry { NameCN = "虚拟操作游戏" };
    Store.Games.Add(gv40);
    ProfileService.RenamePlayer(gv40, "玩家1", "小明");
    Check("形式卡槽: 虚拟玩家1改名后 Players=[小明]、game.json 已落盘",
        gv40.Players.Count == 1 && gv40.Players[0] == "小明"
        && gv40.ActiveProfile == "小明"
        && File.Exists(Path.Combine(ProfileService.RootFor(gv40), "game.json")));  // 显式改名=真实卡槽
    int delBak40 = ProfileService.DeletePlayer(gv40, "小明");
    Check("形式卡槽: 删光玩家回到虚拟态（Players 空、ListSlots 又回玩家1、目录不留）",
        delBak40 == 0 && gv40.Players.Count == 0 && gv40.ActiveProfile == ""
        && ProfileService.ListSlots(gv40).SequenceEqual(new[] { "玩家1" })
        && !Directory.Exists(ProfileService.RootFor(gv40)));
    bool threwDel40 = false;
    try { ProfileService.DeletePlayer(gv40, "玩家1"); }
    catch (InvalidOperationException ex) { threwDel40 = ex.Message.Contains("不能删除"); }
    Check("形式卡槽: 虚拟玩家1禁止删除", threwDel40);

    // 锁定标记持久化：序列化含 Locked 字段，反序列化还原
    string lockJson40 = JsonSerializer.Serialize(new BackupRecord { Locked = true });
    Check("锁定: Locked 持久化字段存在", lockJson40.Contains("\"Locked\":true"));
    Check("锁定: 反序列化还原 true",
        JsonSerializer.Deserialize<BackupRecord>(lockJson40)!.Locked);

    Store.HomeDirOverride = null;
}

// ── 41. 卡片悬停效果：自定义像素着色器已彻底移除（改用 WPF 3D 真 rotateY 透视 + 径向渐变柔光）──
//     自定义 ps_2_0 的 ImplicitInput 取不到元素内容，会把整张卡片渲染成纯黑；
//     因此改为不依赖着色器的原生实现（旋转走 Viewport3D + Viewport2DVisual3D），
//     这里守住"着色器不得回归"。
{
    var asm = typeof(Store).Assembly;
    Check("卡片: 自定义着色器 CardTiltEffect 已移除",
        asm.GetType("GameSaveManager.Effects.CardTiltEffect") == null);
    Check("死代码: FlagWaveEffect 仍已移除",
        asm.GetType("GameSaveManager.Effects.FlagWaveEffect") == null);
    // rotateY 透视翻转走 WPF 3D：Viewport2DVisual3D 把卡片本体当 3D 表面，
    // AxisAngleRotation3D.Angle 驱动角度、必须是可动画的 double 依赖属性，
    // 否则鼠标移出时的平滑复位（第 5 条）无法实现
    var ang = System.Windows.Media.Media3D.AxisAngleRotation3D.AngleProperty;
    Check("卡片: AxisAngleRotation3D.Angle 为可动画的 double 依赖属性",
        ang != null && ang.PropertyType == typeof(double)
        && ang.OwnerType == typeof(System.Windows.Media.Media3D.AxisAngleRotation3D));
    Check("卡片: 3D 表面宿主 Viewport2DVisual3D 存在（rotateY 透视的唯一 WPF 路径）",
        typeof(System.Windows.Media.Media3D.Viewport2DVisual3D) != null);
    Check("卡片: WPF 无 PlaneProjection（.NET 8 不存在该类型，故只能走 3D）",
        typeof(System.Windows.Media.Media3D.Viewport2DVisual3D).Assembly
            .GetType("System.Windows.Media.PlaneProjection") == null);
}

// ── 42. 外观主题：Normalize / 设置持久化 / SetMode 切换与主题字典替换 ──
{
    Check("主题: Normalize(light)=light", ThemeManager.Normalize("light") == ThemeManager.Light);
          Check("主题: Normalize(dark)=dark", ThemeManager.Normalize("dark") == ThemeManager.Dark);
          Check("主题: Normalize(lightsoft)=lightsoft", ThemeManager.Normalize("lightsoft") == ThemeManager.LightSoft);
          Check("主题: Normalize(null/空/乱码)=system",
        ThemeManager.Normalize(null) == ThemeManager.System
        && ThemeManager.Normalize("") == ThemeManager.System
        && ThemeManager.Normalize("neon") == ThemeManager.System);

    string vaultT = Path.Combine(temp, "vaultTheme");
    Store.HomeDirOverride = vaultT;
    Store.Init();
    Check("主题: 全新数据目录默认 system", Store.Settings.Theme == ThemeManager.System, Store.Settings.Theme);
    Store.Settings.Theme = ThemeManager.Dark;
    Store.SaveSettings();
    Store.HomeDirOverride = vaultT;
    Store.Init();
    Check("主题: Theme 随 settings.json 持久化", Store.Settings.Theme == ThemeManager.Dark, Store.Settings.Theme);

    // ThemeManager.Apply 访问 Application.Windows（线程亲和）：Application 建在专用 STA 泵线程上，
    // 测试工作线程调用 SetMode 必须封送到该线程。
    static void SetThemeModeOnAppThread(string mode)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess()) d.Invoke(() => ThemeManager.SetMode(mode));
        else ThemeManager.SetMode(mode);
    }

    // Apply 依赖 Application.Current 的资源容器：测试宿主中没有，显式创建一次。
    // 必须建在专用 STA 线程上并永久泵 Dispatcher——后续 OnUi 的 Dispatcher.Invoke
    //（云端下载入库等改动 ObservableCollection 的路径）在其它线程调用，需要有人泵消息，否则永久死锁。
    bool appOk = false;
    try
    {
        if (System.Windows.Application.Current == null)
        {
            var appReady = new ManualResetEventSlim(false);
            var appThread = new Thread(() =>
            {
                _ = new System.Windows.Application();
                appReady.Set();
                System.Windows.Threading.Dispatcher.Run();
            });
            appThread.SetApartmentState(ApartmentState.STA);
            appThread.IsBackground = true;
            appThread.Start();
            appReady.Wait(5000);
        }
        appOk = System.Windows.Application.Current != null;
    }
    catch (Exception ex) { Check("主题: WPF Application 可创建", false, ex.Message); }
    Check("主题: WPF Application 可创建", appOk);

    if (appOk)
    {
        var merged = System.Windows.Application.Current!.Resources.MergedDictionaries;
        SetThemeModeOnAppThread(ThemeManager.Light);
        bool lightApplied = ThemeManager.Mode == ThemeManager.Light
            && ThemeManager.Actual == ThemeManager.Light
            && !ThemeManager.IsDark
            && Store.Settings.Theme == ThemeManager.Light
            && merged.Cast<System.Windows.ResourceDictionary>()
                .Any(d => d.Source?.OriginalString.Contains("Themes/Light.xaml") == true);
        Check("主题: SetMode(light) 应用浅色字典", lightApplied);

        SetThemeModeOnAppThread(ThemeManager.Dark);
        bool darkApplied = ThemeManager.Mode == ThemeManager.Dark
            && ThemeManager.IsDark
            && Store.Settings.Theme == ThemeManager.Dark
            && merged.Cast<System.Windows.ResourceDictionary>()
                .Any(d => d.Source?.OriginalString.Contains("Themes/Dark.xaml") == true)
            && merged.Cast<System.Windows.ResourceDictionary>()
                .Count(d => d.Source?.OriginalString.Contains("Themes/") == true) == 1;
        Check("主题: SetMode(dark) 应用深色且旧字典已移除", darkApplied);

        // 素雅浅色（lightsoft）：必须加载 LightSoft.xaml 且不残留其它 Themes/ 字典
        SetThemeModeOnAppThread(ThemeManager.LightSoft);
        bool lightSoftApplied = merged.Cast<System.Windows.ResourceDictionary>()
            .Any(d => d.Source?.OriginalString.Contains("Themes/LightSoft.xaml") == true)
            && merged.Cast<System.Windows.ResourceDictionary>()
                .Count(d => d.Source?.OriginalString.Contains("Themes/") == true) == 1;
        Check("主题: SetMode(lightsoft) 应用素雅浅色字典", lightSoftApplied);

        // system 模式：Actual 必须与当前系统主题解析一致
        SetThemeModeOnAppThread(ThemeManager.System);
        string sysActual = ThemeManager.GetSystemTheme();
        Check("主题: SetMode(system) Mode=system 且 Actual 跟随系统",
            ThemeManager.Mode == ThemeManager.System && ThemeManager.Actual == sysActual,
            $"actual={ThemeManager.Actual} sys={sysActual}");

        // ThemeChanged 事件：主界面主题图标/菜单勾选依赖它刷新，每次 Apply 必须触发
        int themeChangedCount = 0;
        ThemeManager.ThemeChanged += () => themeChangedCount++;
        SetThemeModeOnAppThread(ThemeManager.Light);
        SetThemeModeOnAppThread(ThemeManager.Dark);
        SetThemeModeOnAppThread(ThemeManager.System);
        Check("主题: ThemeChanged 随 SetMode 逐次触发", themeChangedCount == 3, $"count={themeChangedCount}");
        Check("主题: ThemeChanged 后 Mode 落盘为 system", Store.Settings.Theme == ThemeManager.System, Store.Settings.Theme);
    }

    Store.HomeDirOverride = null;
}

// ── 43. 云端导入归属与僵尸记录：同名条目归属不静默取第一条 / zip 已丢失的记录不打断导入 ──
{
    string vault43 = Path.Combine(temp, "vault43");
    Store.HomeDirOverride = vault43;
    Store.Init();

    string save43 = Path.Combine(temp, "saves43");
    Directory.CreateDirectory(save43);
    File.WriteAllText(Path.Combine(save43, "slot.dat"), "SAVE-A");

    // 同一款游戏两条同名条目（典型：Steam 版 + 原版并存——正是下载导入失败的现场）
    var dup1 = new GameEntry { NameCN = "英灵神殿", NameEN = "Valheim" };
    dup1.SavePaths.Add(new SavePathEntry { Path = save43 });
    var dup2 = new GameEntry { NameCN = "英灵神殿", NameEN = "Valheim: 英灵神殿" };
    var solo = new GameEntry { NameCN = "只此一款" };
    Store.Games.Add(dup1);
    Store.Games.Add(dup2);
    Store.Games.Add(solo);

    var cands43 = CloudBrowserWindow.FindOwnerCandidates("英灵神殿");
    Check("云端归属: 同名条目返回全部候选（不再静默取第一条）", cands43.Count == 2, $"count={cands43.Count}");
    Check("云端归属: 唯一名字仍只返回一条候选",
        CloudBrowserWindow.FindOwnerCandidates("只此一款").Count == 1);

    // 僵尸记录：备份条目还在、zip 已被手动删除（换盘/手删文件后的残留），再从云端重新导入同一份
    var zombie43 = BackupService.Create(dup1, "本地备份");
    string zombieZip43 = zombie43.File;
    string download43 = Path.Combine(temp, "download43.zip");
    File.Copy(zombieZip43, download43);
    File.Delete(zombieZip43);
    Check("僵尸记录: 前置条件成立（条目在、zip 已不在磁盘）",
        dup1.Backups.Count == 1 && !File.Exists(dup1.Backups[0].File));

    Check("云端归属: 已持有该文件的候选被优先认出",
        ReferenceEquals(CloudBrowserWindow.PreferExistingOwner(cands43, Path.GetFileName(zombieZip43)), dup1));

    bool adoptThrew43 = false;
    BackupRecord? adopted43 = null;
    try { adopted43 = BackupService.AdoptDownloadedZip(dup1, download43); }
    catch (Exception ex) { adoptThrew43 = true; Console.WriteLine("  [诊断] 僵尸记录导入抛异常: " + ex.Message); }
    Check("僵尸记录: 重新导入同一份备份不抛异常", !adoptThrew43 && adopted43 != null);
    Check("僵尸记录: 导入后不残留重复条目且文件已落回",
        dup1.Backups.Count == 1 && File.Exists(dup1.Backups[0].File), $"count={dup1.Backups.Count}");

    // 另一条分支：与本次下载不同源的僵尸记录（时间不同）不得被误删
    var g43b = new GameEntry { NameCN = "另一个游戏" };
    g43b.SavePaths.Add(new SavePathEntry { Path = save43 });
    Store.Games.Add(g43b);
    var live43b = BackupService.Create(g43b, "旧备份");
    // 合成一条"文件已丢失、时间与本次下载不同"的残留记录（换盘/手删文件后的典型残留）
    var fake43b = new BackupRecord
    {
        Time = live43b.Time.AddMinutes(-30),
        Profile = live43b.Profile,
        File = Path.Combine(Path.GetDirectoryName(live43b.File)!, "早已丢失的备份.zip"),
        Kind = "full",
    };
    g43b.Backups.Add(fake43b);
    string download43b = Path.Combine(temp, "download43b.zip");
    File.Copy(live43b.File, download43b);
    bool threw43b = false;
    try { BackupService.AdoptDownloadedZip(g43b, download43b); } catch (Exception) { threw43b = true; }
    Check("僵尸记录: 与下载件不同源时保留不误删、且新件正常入库",
        !threw43b && g43b.Backups.Count == 2 && g43b.Backups.Contains(fake43b) && !File.Exists(fake43b.File),
        $"count={g43b.Backups.Count}");

    Store.HomeDirOverride = null;
}

// ── 44. 卡片悬停特效：纯 2D 合成层（无 Viewport3D）+ 徽章动态显隐 ──
{
    string xaml44 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameSaveManager", "MainWindow.xaml"));
    string xamlText44 = File.Exists(xaml44) ? File.ReadAllText(xaml44) : "";
    Check("MainWindow.xaml 源文件可读取", xamlText44.Length > 0, xaml44);
    // 性能回归守卫：3D 方案每张卡分配宿主纹理，且 Collapsed 徽章撑大渲染边界曾导致图标横压。
    // 只认真实 XAML 元素标签，避免命中说明性注释
    // 性能回归守卫：主列表卡曾用 3D 方案（每张卡分配宿主纹理），后改为纯 2D。
    // 注意：守卫范围仅限 GameListItem 模板切片——完结馆海报卡（CompletedCard）刻意使用 Viewport2DVisual3D。
    int listTplStart44 = xamlText44.IndexOf("<DataTemplate x:Key=\"GameListItem\"", StringComparison.Ordinal);
    string listTpl44 = listTplStart44 < 0 ? "" : xamlText44.Substring(listTplStart44,
        xamlText44.IndexOf("</DataTemplate>", listTplStart44, StringComparison.Ordinal) - listTplStart44);
    Check("主列表卡模板不再使用 Viewport3D", listTpl44.Length > 0 && !listTpl44.Contains("<Viewport3D"), "");
    Check("主列表卡模板不再使用 Viewport2DVisual3D", listTpl44.Length > 0 && !listTpl44.Contains("<Viewport2DVisual3D"), "");
    // 纯 2D 三件套：放大上浮层 / ScaleX 倾斜执行器 / 方向暗角
    bool hasLift44 = xamlText44.Contains("x:Name=\"LiftHost\"") && xamlText44.Contains("x:Name=\"CardScale\"");
    bool hasTilt44 = xamlText44.Contains("x:Name=\"CardTilt\"");
    bool hasShade44 = xamlText44.Contains("x:Name=\"TiltShade\"") && xamlText44.Contains("x:Name=\"TiltShadeBrush\"");
    Check("纯 2D 特效层齐全（LiftHost+CardTilt+方向暗角）", hasLift44 && hasTilt44 && hasShade44,
        $"lift={hasLift44},tilt={hasTilt44},shade={hasShade44}");
    // 「自动」徽章：全模板一处 + Visibility 动态显隐（不占位）；星标是 Auto 列末尾元素，
    // 徽章折叠时列从左侧收缩，星标 X 不变。守卫同时禁止退回 Opacity 恒占位写法
    int badgeCount44 = System.Text.RegularExpressions.Regex.Matches(xamlText44, "Text=\"自动\"").Count;
    Check("「自动」徽章全模板仅一处（曾图标角与星标旁重复渲染）", badgeCount44 == 1, $"count={badgeCount44}");
    bool badgeDynamic44 = xamlText44.Contains(
        "Visibility=\"{Binding AutoBackup, Converter={StaticResource BoolToVis}}\"");
    Check("徽章以 Visibility 绑定 AutoBackup 动态显隐", badgeDynamic44, $"dynamic={badgeDynamic44}");
    bool badgeOpacity44 = System.Text.RegularExpressions.Regex.IsMatch(xamlText44,
        "Value=\"True\">\\s*<Setter Property=\"Opacity\" Value=\"1\"");
    Check("徽章不再使用 Opacity 恒占位", !badgeOpacity44, $"opacity={badgeOpacity44}");

    // 回归守卫：首次启动（无选中+海报态）点星标，PreviewMouseDown 不得强制收起海报——
    // 行内按钮点击必须在识别到 Button 祖先时放行，否则 SelectedGame 仍为 null 而海报已隐藏，残留无名空详情
    string cs44 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameSaveManager", "MainWindow.xaml.cs"));
    string csText44 = File.Exists(cs44) ? File.ReadAllText(cs44) : "";
    Check("MainWindow.xaml.cs 源文件可读取", csText44.Length > 0, cs44);
    int handlerPos44 = csText44.IndexOf("OnGamesListPreviewMouseDown", StringComparison.Ordinal);
    int itemPos44 = csText44.IndexOf("ContainerFromElement(GamesList", handlerPos44, StringComparison.Ordinal);
    int buttonGuardPos44 = csText44.IndexOf(
        "FindAncestor<System.Windows.Controls.Button>(dep)", handlerPos44, StringComparison.Ordinal);
    Check("点星标不收起海报（Button 祖先放行守卫先于命中项判定）",
        handlerPos44 >= 0 && buttonGuardPos44 >= 0 && buttonGuardPos44 < itemPos44,
        $"handler={handlerPos44},guard={buttonGuardPos44},item={itemPos44}");
}

// ── 45. 游玩统计：最后关闭时间/会话累计（PlayTracker 纯逻辑）+ 详情入口守卫 ──
{
    DateTime t0 = new(2026, 9, 1, 10, 0, 0);
    var g45 = new GameEntry();

    // 短会话（<20 秒，启动器闪退/误触）不计入
    bool short45 = PlayTracker.CloseSession(g45, t0, t0.AddSeconds(19), "app");
    Check("短于 20 秒的会话被丢弃", !short45 && g45.Sessions.Count == 0 && g45.PlaySeconds == 0,
        $"accepted={short45},n={g45.Sessions.Count},sec={g45.PlaySeconds}");

    // 正常闭合：总时长/最后关闭/明细全部落账
    bool ok45 = PlayTracker.CloseSession(g45, t0, t0.AddMinutes(72), "app");
    Check("正常会话闭合后写入时长与最后关闭时间",
        ok45 && g45.PlaySeconds == 72 * 60 && g45.LastClosedAt == t0.AddMinutes(72) && g45.Sessions.Count == 1,
        $"ok={ok45},sec={g45.PlaySeconds},closed={g45.LastClosedAt:O},n={g45.Sessions.Count}");

    // 中断会话（应用退出时游戏仍在跑）：计入时长，但不更新「最后关闭时间」
    DateTime closedBefore45 = g45.LastClosedAt;
    bool inter45 = PlayTracker.CloseSession(g45, t0.AddDays(1), t0.AddDays(1).AddMinutes(30), "monitor", interrupted: true);
    Check("中断会话计入时长但不更新最后关闭时间",
        inter45 && g45.PlaySeconds == (72 + 30) * 60 && g45.LastClosedAt == closedBefore45 &&
        g45.Sessions.Count == 2 && g45.Sessions[1].Interrupted,
        $"ok={inter45},sec={g45.PlaySeconds},n={g45.Sessions.Count}");

    // 卡片文本口径：运行中→游玩中；无关闭记录→回退 Steam 打开时间；有关闭记录→关闭时间
    var g45b = new GameEntry();
    bool runningText45 = g45b.LastPlayedText == "";
    g45b.IsRunning = true;
    runningText45 &= g45b.LastPlayedText == "游玩中";
    g45b.IsRunning = false;
    g45b.LastPlayed = t0;
    runningText45 &= g45b.LastPlayedText == Store.TimeAgo(t0) && g45b.LastPlayedTip.StartsWith("Steam");
    g45b.LastClosedAt = t0.AddHours(2);
    runningText45 &= g45b.LastPlayedText == Store.TimeAgo(t0.AddHours(2)) && g45b.LastPlayedTip.StartsWith("最后关闭");
    Check("卡片时间文本口径（游玩中/回退Steam/关闭时间）", runningText45, "");

    // 有效最近时间：关闭优先于打开
    Check("LastActiveAt 取关闭与打开的较新者",
        g45b.LastActiveAt == t0.AddHours(2) && new GameEntry().LastActiveAt == DateTime.MinValue, "");

    // 近 N 天统计（窗口起点晚于第 1 条会话，只应纳入次日那条 30 分钟的中断会话）
    long week45 = PlayTracker.SecondsSince(g45, t0.AddMinutes(10));
    Check("SecondsSince 按会话开始时刻统计窗口内时长", week45 == 30 * 60, $"week={week45}");

    // 上限 300：超出丢弃最旧并从总时长扣除（插 301 条 60 秒会话，已有 2 条 → 最终 300 条）
    var g45c = new GameEntry();
    for (int i = 0; i < PlayTracker.MaxSessions + 1; i++)
        PlayTracker.CloseSession(g45c, t0.AddMinutes(i), t0.AddMinutes(i).AddSeconds(60), "monitor");
    long sum45 = g45c.Sessions.Sum(s => s.Seconds);
    Check($"会话上限 {PlayTracker.MaxSessions}：截断最旧且总时长同步扣除",
        g45c.Sessions.Count == PlayTracker.MaxSessions && g45c.PlaySeconds == sum45 && sum45 == PlayTracker.MaxSessions * 60,
        $"n={g45c.Sessions.Count},sec={g45c.PlaySeconds},sum={sum45}");

    // 崩溃遗留的未闭合会话：启动清理
    g45c.Sessions.Add(new PlaySession { Start = t0, End = default });
    int dropped45 = PlayTracker.DropStaleOpenSessions(new[] { g45c });
    Check("崩溃遗留的未闭合会话在启动时被清理",
        dropped45 == 1 && g45c.Sessions.Count == PlayTracker.MaxSessions && g45c.Sessions.All(s => s.End != default),
        $"dropped={dropped45},n={g45c.Sessions.Count}");

    // 重复入库合并：会话按 Start 去重，总时长重算而非相加
    var keep45 = new GameEntry();
    PlayTracker.CloseSession(keep45, t0, t0.AddMinutes(10), "app");
    var drop45 = new GameEntry { LastClosedAt = t0.AddMinutes(90) };
    PlayTracker.CloseSession(drop45, t0, t0.AddMinutes(10), "monitor");        // 与 keep 同开始时刻 → 去重
    PlayTracker.CloseSession(drop45, t0.AddMinutes(30), t0.AddMinutes(90), "monitor");
    PlayTracker.MergeStats(keep45, drop45);
    Check("MergeStats 会话去重合并、关闭时间取较新、总时长重算",
        keep45.Sessions.Count == 2 && keep45.PlaySeconds == 10 * 60 + 60 * 60 &&
        keep45.LastClosedAt == t0.AddMinutes(90),
        $"n={keep45.Sessions.Count},sec={keep45.PlaySeconds}");

    // 时长格式化
    bool fmt45 = PlayTracker.FormatDuration(0) == "—"
        && PlayTracker.FormatDuration(40) == "不足 1 分钟"
        && PlayTracker.FormatDuration(300) == "5 分钟"
        && PlayTracker.FormatDuration(3900) == "1 小时 5 分"
        && PlayTracker.FormatDuration(90000) == "1 天 1 小时";
    Check("FormatDuration 各档文案", fmt45,
        $"'{PlayTracker.FormatDuration(3900)}' '{PlayTracker.FormatDuration(90000)}'");

    // XAML 守卫：详情页存在「游玩详情」入口
    string xaml45 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameSaveManager", "MainWindow.xaml"));
    string xamlText45 = File.Exists(xaml45) ? File.ReadAllText(xaml45) : "";
    Check("详情标题栏存在「游玩详情」按钮并接线 OnShowPlayStats",
        xamlText45.Contains("游玩详情") && xamlText45.Contains("Click=\"OnShowPlayStats\""), "");

    // 界面文案：玩家用语，不得出现编程术语
    string statsXamlPath45 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameSaveManager", "PlayStatsWindow.xaml"));
    string statsXaml45 = File.Exists(statsXamlPath45) ? File.ReadAllText(statsXamlPath45) : "";
    Check("游玩详情界面无编程术语（会话/进程监测/本应用启动）",
        statsXaml45.Length > 0
        && !statsXaml45.Contains("会话记录") && !statsXaml45.Contains("会话数")
        && !statsXaml45.Contains("进程监测") && !statsXaml45.Contains("本应用启动")
        && !statsXaml45.Contains("本应用记录以来"), "");
    Check("游玩详情使用玩家用语（游玩记录/游玩次数）",
        statsXaml45.Contains("游玩记录") && statsXaml45.Contains("游玩次数"), "");
    // 来源徽章（本应用启动/进程监测）整列删除；Steam 独立行删除并并入最后关闭卡片
    Check("来源徽章已删除（不再绑定 SourceText）",
        !statsXaml45.Contains("SourceText"), "");
    Check("Steam 独立提示行已删除（并入最后关闭卡片）",
        !statsXaml45.Contains("Steam 记录的最近打开") && !statsXaml45.Contains("SteamRef"), "");
    // 底部口径提示（玩家用语）必须存在，旧的轮询技术口径不得复活；
    // 提示与关闭按钮必须分列（各自独占 Grid 列，防同格叠放遮挡回归）
    Check("底部玩家口径提示存在且旧技术口径已移除",
        statsXaml45.Contains("游玩数据仅统计本程序开启后的数据。")
        && !statsXaml45.Contains("端点误差") && !statsXaml45.Contains("历史无法追溯"), "");
    // 防遮挡关键结构：关闭按钮必须独占第 2 列（Grid.Column=1），不得与文字同居一个单元格
    int notePos45 = statsXaml45.IndexOf("游玩数据仅统计本程序开启后的数据。", StringComparison.Ordinal);
    int closePos45 = statsXaml45.IndexOf("Click=\"OnClose\"", notePos45, StringComparison.Ordinal);
    Check("底部提示与关闭按钮分列布局（防遮挡）",
        notePos45 >= 0 && closePos45 > notePos45
        && statsXaml45.Substring(notePos45, closePos45 - notePos45).Contains("Grid.Column=\"1\""), "");

    // 弹窗标题栏必须跟随主题（漏调会在深色主题下出现白色原生标题栏）。
    // 注意 code-behind 是双扩展名 PlayStatsWindow.xaml.cs，不能用 ChangeExtension
    string statsCsPath45 = statsXamlPath45 + ".cs";
    string statsCs45 = File.Exists(statsCsPath45) ? File.ReadAllText(statsCsPath45) : "";
    Check("游玩详情弹窗标题栏跟随主题（DarkTitleBar.SetTheme）",
        statsCs45.Contains("DarkTitleBar.SetTheme(this, ThemeManager.IsDark)"), "");

    // 实例化即解析 BAML 内全部 StaticResource：引用 MainWindow 窗口级资源（Badge/BadgeText）
    // 会在此抛 XamlParseException——曾因此资源缺失在 Measure 周期反复抛出导致栈溢出硬崩溃。
    // WPF 窗口须在 STA 线程构造。
    Exception? dlgErr45 = null;
    var sta45 = new Thread(() =>
    {
        try
        {
            // 复用前面区块创建的裸 Application（AppDomain 内不能 new 第二个），
            // 从 App.xaml 源码解析全局资源（Btn/BtnSm/BoolToVis 等）合并进去
            if (System.Windows.Application.Current!.Resources.MergedDictionaries
                    .OfType<System.Windows.ResourceDictionary>().All(d => d["BtnSm"] == null))
            {
                string appXamlPath45 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                    "..", "..", "..", "..", "..", "src", "GameSaveManager", "App.xaml"));
                // 剥掉 <Application.Resources> 与内层 <ResourceDictionary> 两层标签，只取条目
                string inner45 = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(appXamlPath45),
                    @"<Application\.Resources>\s*<ResourceDictionary[^>]*>(.*?)</ResourceDictionary>\s*</Application\.Resources>",
                    System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
                // 主题字典走相对 Pack URI，XamlReader 无 base uri 无法解析；主题已由前置区块合并，剔除
                inner45 = System.Text.RegularExpressions.Regex.Replace(inner45,
                    @"<ResourceDictionary\.MergedDictionaries>.*?</ResourceDictionary\.MergedDictionaries>",
                    "", System.Text.RegularExpressions.RegexOptions.Singleline);
                string asm45 = typeof(BoolToVis).Assembly.GetName().Name ?? "GameSaveVault";
                string dictXaml45 =
                    "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"" +
                    " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"" +
                    $" xmlns:local=\"clr-namespace:GameSaveManager;assembly={asm45}\">" +
                    inner45 + "</ResourceDictionary>";
                var rd45 = (System.Windows.ResourceDictionary)
                    System.Windows.Markup.XamlReader.Parse(dictXaml45);
                System.Windows.Application.Current.Resources.MergedDictionaries.Add(rd45);
            }
            // 放入正常+中断两条会话，强制 ItemTemplate 实例化（空列表不会加载模板）
            PlayTracker.CloseSession(g45b, t0.AddDays(-2), t0.AddDays(-2).AddMinutes(40), "app");
            PlayTracker.CloseSession(g45b, t0.AddDays(-1), t0.AddDays(-1).AddMinutes(25), "monitor", interrupted: true);
            // 必须 Show + 推一帧布局：ItemTemplate 内的 StaticResource 延迟到
            // 列表项 Measure 时才解析（真实崩溃点），仅 new 窗口抓不到
            var win45 = new PlayStatsWindow(g45b);
            win45.Show();
            win45.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            win45.Close();
        }
        catch (Exception ex) { dlgErr45 = ex; }
    });
    sta45.SetApartmentState(ApartmentState.STA);
    sta45.Start();
    sta45.Join();
    Check("PlayStatsWindow 可实例化（全部 StaticResource 可解析）",
        dlgErr45 == null, dlgErr45?.GetBaseException().Message ?? "");

    // ── 设置窗口：左侧导航分页改造回归守卫 ──
    string settingsXamlPath46 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameSaveManager", "SettingsWindow.xaml"));
    string settingsXaml46 = File.Exists(settingsXamlPath46) ? File.ReadAllText(settingsXamlPath46) : "";
    Check("设置窗口含 5 个导航项（通用/备份/云同步/数据/关于）",
        settingsXaml46.Length > 0
        && settingsXaml46.Contains("x:Name=\"NavGeneral\"")
        && settingsXaml46.Contains("x:Name=\"NavBackup\"")
        && settingsXaml46.Contains("x:Name=\"NavCloud\"")
        && settingsXaml46.Contains("x:Name=\"NavData\"")
        && settingsXaml46.Contains("x:Name=\"NavAbout\"")
        && settingsXaml46.Contains("Checked=\"OnNavCheck\""), "");
    Check("设置窗口含 5 个分页容器",
        settingsXaml46.Contains("x:Name=\"PageGeneral\"")
        && settingsXaml46.Contains("x:Name=\"PageBackup\"")
        && settingsXaml46.Contains("x:Name=\"PageCloud\"")
        && settingsXaml46.Contains("x:Name=\"PageData\"")
        && settingsXaml46.Contains("x:Name=\"PageAbout\""), "");
    Check("设置窗口默认显示通用页，其余分页折叠",
        settingsXaml46.Contains("IsChecked=\"True\"")
        && settingsXaml46.Contains("x:Name=\"PageBackup\" Visibility=\"Collapsed\"")
        && settingsXaml46.Contains("x:Name=\"PageCloud\" Visibility=\"Collapsed\"")
        && settingsXaml46.Contains("x:Name=\"PageData\" Visibility=\"Collapsed\"")
        && settingsXaml46.Contains("x:Name=\"PageAbout\" Visibility=\"Collapsed\""), "");
    // 云同步总开关已按用户要求移除（语义误导：实为总闸却写成自动上传）；旧文案不得复活
    Check("云同步总开关已移除（CloudEnabledChk 与误导文案不得存在）",
        !settingsXaml46.Contains("CloudEnabledChk")
        && !settingsXaml46.Contains("新备份自动上传到云盘"), "");
    // 云同步独立成第三页，导航顺序必须正确
    int navB = settingsXaml46.IndexOf("x:Name=\"NavBackup\"", StringComparison.Ordinal);
    int navC = settingsXaml46.IndexOf("x:Name=\"NavCloud\"", StringComparison.Ordinal);
    int navD = settingsXaml46.IndexOf("x:Name=\"NavData\"", StringComparison.Ordinal);
    Check("云同步为第三项导航（备份 → 云同步 → 数据）",
        navB >= 0 && navC > navB && navD > navC, "");
    // 旧版全部控件必须原样保留（改名/漏搬会直接破坏功能）
    string[] mustKeep46 = {
        "x:Name=\"StartupScanChk\"", "ThemeSystemBtn", "ThemeLightBtn", "ThemeLightSoftBtn", "ThemeDarkBtn",
        "x:Name=\"BackupDirBox\"", "x:Name=\"MaxKeepBox\"", "x:Name=\"IncrChk\"", "x:Name=\"FullEveryBox\"",
        "x:Name=\"FullEveryRow\"", "x:Name=\"HotMinutesBox\"", "x:Name=\"HotMinutesRow\"",
        "x:Name=\"HotKeyBox\"", "x:Name=\"HotKeyClearBtn\"", "x:Name=\"DefaultPlayerNameBox\"",
        "x:Name=\"CloudUrlBox\"", "x:Name=\"CloudUserBox\"",
        "x:Name=\"CloudPassBox\"", "x:Name=\"CloudDirBox\"", "x:Name=\"CloudDelWithCloudChk\"",
        "x:Name=\"CloudTestBtn\"", "x:Name=\"CloudUploadBtn\"", "x:Name=\"CloudBrowseBtn\"",
        "x:Name=\"CloudStatusText\"", "x:Name=\"SourceList\"", "x:Name=\"AppIdSourceList\"",
        "x:Name=\"ErrText\"", "x:Name=\"AboutVersionText\""
    };
    Check("设置窗口旧控件全部保留（28 项命名控件，总开关除外）",
        mustKeep46.All(n => settingsXaml46.Contains(n)),
        string.Join(",", mustKeep46.Where(n => !settingsXaml46.Contains(n))));

    // 真实构造 + Show 强制布局，并逐一切页验证可见性（BAML/StaticResource/导航逻辑一次覆盖）
    Exception? settingsErr46 = null;
    var sta46 = new Thread(() =>
    {
        try
        {
            var w46 = new SettingsWindow();
            w46.Show();
            w46.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            if (w46.PageGeneral.Visibility != System.Windows.Visibility.Visible
                || w46.PageBackup.Visibility != System.Windows.Visibility.Collapsed
                || w46.PageCloud.Visibility != System.Windows.Visibility.Collapsed
                || w46.PageData.Visibility != System.Windows.Visibility.Collapsed
                || w46.PageAbout.Visibility != System.Windows.Visibility.Collapsed)
                throw new InvalidOperationException("初始分页状态错误：应只显示通用页");
            w46.NavBackup.IsChecked = true;
            w46.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            if (w46.PageBackup.Visibility != System.Windows.Visibility.Visible || w46.PageGeneral.Visibility != System.Windows.Visibility.Collapsed)
                throw new InvalidOperationException("切到备份页失败");
            w46.NavCloud.IsChecked = true;
            w46.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            if (w46.PageCloud.Visibility != System.Windows.Visibility.Visible || w46.PageBackup.Visibility != System.Windows.Visibility.Collapsed)
                throw new InvalidOperationException("切到云同步页失败");
            w46.NavData.IsChecked = true;
            w46.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            if (w46.PageData.Visibility != System.Windows.Visibility.Visible || w46.PageCloud.Visibility != System.Windows.Visibility.Collapsed)
                throw new InvalidOperationException("切到数据页失败");
            w46.NavAbout.IsChecked = true;
            w46.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            if (w46.PageAbout.Visibility != System.Windows.Visibility.Visible || w46.PageData.Visibility != System.Windows.Visibility.Collapsed)
                throw new InvalidOperationException("切到关于页失败");
            if (string.IsNullOrWhiteSpace(w46.AboutVersionText.Text))
                throw new InvalidOperationException("关于页版本号未注入");
            w46.NavGeneral.IsChecked = true;
            if (w46.PageGeneral.Visibility != System.Windows.Visibility.Visible || w46.PageAbout.Visibility != System.Windows.Visibility.Collapsed)
                throw new InvalidOperationException("切回通用页失败");
            w46.Close();
        }
        catch (Exception ex) { settingsErr46 = ex; }
    });
    sta46.SetApartmentState(ApartmentState.STA);
    sta46.Start();
    sta46.Join();
    Check("SettingsWindow 可实例化且五个分页切换正常",
        settingsErr46 == null, settingsErr46?.GetBaseException().Message ?? "");

    // 云同步门槛守卫：Configured 不得再依赖已移除的 CloudEnabled 总开关
    string cloudSvcPath46 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameSaveManager", "Services", "CloudSyncService.cs"));
    string cloudSvc46 = File.Exists(cloudSvcPath46) ? File.ReadAllText(cloudSvcPath46) : "";
    int cfgStart46 = cloudSvc46.IndexOf("public static bool Configured", StringComparison.Ordinal);
    int cfgEnd46 = cloudSvc46.IndexOf("public static void Start", StringComparison.Ordinal);
    string cfgBody46 = cfgStart46 >= 0 && cfgEnd46 > cfgStart46
        ? cloudSvc46.Substring(cfgStart46, cfgEnd46 - cfgStart46) : "";
    Check("云同步可用判定不再包含 CloudEnabled 总开关",
        cfgBody46.Length > 0 && !cfgBody46.Contains("CloudEnabled"), "");

    // ===== 区块 47：浅色主题提示文字对比度（WCAG AA：正常文字 ≥4.5，Toast 深底 ≥7） =====
    string themesDir47 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameSaveManager", "Themes"));
    static string ThemeColor47(string xaml, string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(xaml,
            @"x:Key=""" + System.Text.RegularExpressions.Regex.Escape(key) + @""" Color=""(#[0-9A-Fa-f]{6})""");
        return m.Success ? m.Groups[1].Value : "";
    }
    static double Lum47(string hex)
    {
        byte r = Convert.ToByte(hex.Substring(1, 2), 16);
        byte g = Convert.ToByte(hex.Substring(3, 2), 16);
        byte b = Convert.ToByte(hex.Substring(5, 2), 16);
        static double Chan(byte v)
        {
            double c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Chan(r) + 0.7152 * Chan(g) + 0.0722 * Chan(b);
    }
    static double Ratio47(string fg, string bg)
    {
        double l1 = Lum47(fg), l2 = Lum47(bg);
        double hi = Math.Max(l1, l2), lo = Math.Min(l1, l2);
        return (hi + 0.05) / (lo + 0.05);
    }
    // Toast 实际底色 = ToastBg（F0 alpha）叠主题 Bg，预算为固定值（改主题时需同步）
    var themeSpec47 = new (string File, string Bg, string ToastBg)[]
    {
        ("Light.xaml",     "#F3EFE6", "#433E36"),
        ("LightSoft.xaml", "#F4F6F8", "#383D46"),
    };
    foreach (var (file, bg, toastBg) in themeSpec47)
    {
        string x47 = File.ReadAllText(Path.Combine(themesDir47, file));
        Check($"{file} 含 ToastText/ToastErrorText 语义色",
            ThemeColor47(x47, "ToastText").Length > 0 && ThemeColor47(x47, "ToastErrorText").Length > 0, "");
        // 浅底上的文字/提示色 ≥4.5
        foreach (string key in new[] { "TextSecondary", "Red", "SteamText", "BuiltInTagText", "Accent2" })
        {
            string col = ThemeColor47(x47, key);
            double ratio = col.Length > 0 ? Ratio47(col, bg) : 0;
            Check($"{file} {key} 白底对比度 ≥4.5（实测 {ratio:0.0}）", ratio >= 4.5, "");
        }
        // Toast 深底上的浅色文字 ≥7（大字号提示放宽也要 ≥4.5，这里取 7 留余量）
        double toastRatio = Ratio47(ThemeColor47(x47, "ToastText"), toastBg);
        Check($"{file} ToastText 深底对比度 ≥7（实测 {toastRatio:0.0}）", toastRatio >= 7, "");
        double errRatio = Ratio47(ThemeColor47(x47, "ToastErrorText"), toastBg);
        Check($"{file} ToastErrorText 深底对比度 ≥4.5（实测 {errRatio:0.0}）", errRatio >= 4.5, "");
    }
    string darkXaml47 = File.ReadAllText(Path.Combine(themesDir47, "Dark.xaml"));
    Check("Dark.xaml 同样定义 ToastText/ToastErrorText",
        ThemeColor47(darkXaml47, "ToastText").Length > 0
        && ThemeColor47(darkXaml47, "ToastErrorText").Length > 0, "");

    // 完结纪念卡：三套主题必须各自定义全套 Memorial* 配色（缺键会导致某主题下卡面透明/黑字）
    // 同时支持 <SolidColorBrush x:Key Color=".."/> 与 <Color x:Key>..</Color> 两种写法
    string MemVal47(string xaml, string key)
    {
        var m1 = System.Text.RegularExpressions.Regex.Match(xaml,
            @"x:Key=""" + System.Text.RegularExpressions.Regex.Escape(key) + @"""\s+Color=""(#[0-9A-Fa-f]{6})""");
        if (m1.Success) return m1.Groups[1].Value;
        var m2 = System.Text.RegularExpressions.Regex.Match(xaml,
            @"x:Key=""" + System.Text.RegularExpressions.Regex.Escape(key) + @"""\s*>(#[0-9A-Fa-f]{6})<");
        return m2.Success ? m2.Groups[1].Value : "";
    }
    string[] memKeys47 = { "MemorialBgTop", "MemorialBgBottom", "MemorialGoldHi", "MemorialGoldLo",
        "MemorialBorder", "MemorialTitle", "MemorialMuted", "MemorialValue", "MemorialSince", "MemorialDivider" };
    foreach (string memFile in new[] { "Dark.xaml", "Light.xaml", "LightSoft.xaml" })
    {
        string mx47 = File.ReadAllText(Path.Combine(themesDir47, memFile));
        int missing47 = memKeys47.Count(k => !mx47.Contains("x:Key=\"" + k + "\""));
        Check($"{memFile} 定义纪念卡全套 Memorial* 配色（缺 {missing47} 个）", missing47 == 0, "");
        if (memFile != "Dark.xaml")
        {
            // 浅色主题：浅底深字。数值 14px 按普通文本 ≥4.5；日期金色渐变深端 ≥3（23px 粗体大字）
            string memBg47 = MemVal47(mx47, "MemorialBgTop");
            double valRatio47 = Ratio47(MemVal47(mx47, "MemorialValue"), memBg47);
            double dateRatio47 = Ratio47(MemVal47(mx47, "MemorialGoldLo"), memBg47);
            Check($"{memFile} 纪念卡数值/日期在浅底上可读（数值 {valRatio47:0.0}≥4.5, 日期 {dateRatio47:0.0}≥3）",
                valRatio47 >= 4.5 && dateRatio47 >= 3, "");
        }
    }
    // Toast 绑定与错误态切换守卫
    string mainXaml47 = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameSaveManager", "MainWindow.xaml")));
    Check("纪念卡 XAML 全部引用主题色（无硬编码金色/底色）",
        mainXaml47.Contains("DynamicResource MemorialGoldHi}")
        && mainXaml47.Contains("DynamicResource MemorialBorder}")
        && !mainXaml47.Contains("#EBDDAA") && !mainXaml47.Contains("#1E212B"), "");
    Check("Toast 文字绑定 ToastText 语义色（不再用 TextPrimary）",
        mainXaml47.Contains("x:Name=\"ToastText\"") && mainXaml47.Contains("Foreground=\"{DynamicResource ToastText}\""), "");
    // Toast 必须在左下角（居中会压住详情区底部「退出游戏时备份」等按钮），且有 MaxWidth+换行兜底
    int toastTag47 = mainXaml47.IndexOf("x:Name=\"Toast\"", StringComparison.Ordinal);
    string toastOpen47 = toastTag47 >= 0 ? mainXaml47.Substring(toastTag47, Math.Min(700, mainXaml47.Length - toastTag47)) : "";
    Check("Toast 位于左下角（Left/Bottom），不再居中遮挡按钮",
        toastOpen47.Contains("HorizontalAlignment=\"Left\"")
        && toastOpen47.Contains("VerticalAlignment=\"Bottom\"")
        && !toastOpen47.Contains("HorizontalAlignment=\"Center\""), "");
    Check("Toast 限宽并换行，防止长提示伸回详情区",
        toastOpen47.Contains("MaxWidth=") && toastOpen47.Contains("TextWrapping=\"Wrap\""), "");
    // 锚定式提示浮层：紧贴被点按钮下方（Popup Placement=Bottom），不遮挡按钮
    Check("存在锚定式提示浮层 ToastPopup（Popup/Bottom/透明/不接收点击）",
        mainXaml47.Contains("x:Name=\"ToastPopup\"")
        && mainXaml47.Contains("Placement=\"Bottom\"")
        && mainXaml47.Contains("AllowsTransparency=\"True\"")
        && mainXaml47.Contains("x:Name=\"ToastPopupCard\"")
        && mainXaml47.Contains("x:Name=\"ToastPopupText\""), "");
    string mainCs47 = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameSaveManager", "MainWindow.xaml.cs")));
    Check("ShowToast 错误态切换 ToastErrorText 文字色",
        mainCs47.Contains("ToastErrorText") && mainCs47.Contains("ToastText.Foreground"), "");
    Check("ShowToast 支持 anchor 锚定参数并驱动 ToastPopup",
        mainCs47.Contains("FrameworkElement? anchor")
        && mainCs47.Contains("ToastPopup.PlacementTarget = anchor"), "");
    // 三个操作栏开关的反馈必须锚定到被点控件（sender），提示出现在按钮下方
    string autoHandler47 = mainCs47.Substring(
        mainCs47.IndexOf("private void OnAutoToggled", StringComparison.Ordinal), 400);
    string hotHandler47 = mainCs47.Substring(
        mainCs47.IndexOf("private void OnHotToggled", StringComparison.Ordinal), 500);
    string cloudHandler47 = mainCs47.Substring(
        mainCs47.IndexOf("private void OnCloudAutoToggled", StringComparison.Ordinal), 900);
    Check("退出游戏时备份/定时快照/自动云存档 三个开关提示锚定按钮下方",
        autoHandler47.Contains("anchor:") && hotHandler47.Contains("anchor:")
        && cloudHandler47.Split("ShowToast").Length >= 3
        && System.Text.RegularExpressions.Regex.Matches(cloudHandler47, "anchor:").Count >= 2, "");

    // ===== 区块 48：手动还原前"当前进度与目标备份一致"短路，防重复还原/冗余快照 =====
    string rd48 = Path.Combine(temp, "restore_dedup");
    Directory.CreateDirectory(rd48);
    File.WriteAllText(Path.Combine(rd48, "s.txt"), "v1");
    var g48 = new GameEntry { NameEN = "TestRestoreDedup48" };
    g48.SavePaths.Add(new SavePathEntry { Path = rd48 });
    var r1v48 = BackupService.Create(g48, "第一版");
    File.WriteAllText(Path.Combine(rd48, "s.txt"), "v2222222");
    var r2v48 = BackupService.Create(g48, "第二版");

    Check("还原短路: 当前进度==第二版备份 → 判定一致",
        BackupService.IsLiveSameAsSnapshot(g48, r2v48), "");
    Check("还原短路: 当前进度!=第一版备份 → 判定不一致",
        !BackupService.IsLiveSameAsSnapshot(g48, r1v48), "");

    // 还原到第一版：此刻 live=第二版且与最新备份 r2 相同，安全快照被去重跳过，条数不变
    int nBefore48 = g48.Backups.Count;
    BackupService.Restore(g48, r1v48);
    Check("还原短路: 还原后内容正确",
        File.ReadAllText(Path.Combine(rd48, "s.txt")) == "v1", "");
    Check("还原短路: 第一次还原（live 与最新备份相同）不新增快照",
        g48.Backups.Count == nBefore48, g48.Backups.Count.ToString());
    Check("还原短路: 还原后与目标备份判定一致（精确 mtime 已回写）",
        BackupService.IsLiveSameAsSnapshot(g48, r1v48), "");

    // 证明前置短路是唯一防线：若不拦截再还原同一备份，旧逻辑确实会多出一条安全快照
    BackupService.Restore(g48, r1v48);
    Check("还原短路: 不拦截时重复还原会产生冗余快照（说明守卫必要）",
        g48.Backups.Count == nBefore48 + 1, g48.Backups.Count.ToString());

    // 各种差异必须判定为不一致（不能误跳过真实需要的还原）
    File.WriteAllText(Path.Combine(rd48, "s.txt"), "v3333333333");
    Check("还原短路: 内容改动后判定不一致",
        !BackupService.IsLiveSameAsSnapshot(g48, r1v48), "");
    File.WriteAllText(Path.Combine(rd48, "s.txt"), "v1");
    File.SetLastWriteTimeUtc(Path.Combine(rd48, "s.txt"),
        File.GetLastWriteTimeUtc(Path.Combine(rd48, "s.txt")).AddSeconds(7));
    Check("还原短路: 仅修改时间不同也判定不一致",
        !BackupService.IsLiveSameAsSnapshot(g48, r1v48), "");

    // 重新精确还原回到与 r1 完全一致的干净状态，再隔离测试"新增文件/路径变化"
    BackupService.Restore(g48, r1v48);
    Check("还原短路: 干净状态下恢复判定一致",
        BackupService.IsLiveSameAsSnapshot(g48, r1v48), "");
    File.WriteAllText(Path.Combine(rd48, "new.txt"), "x");
    Check("还原短路: 新增文件判定不一致",
        !BackupService.IsLiveSameAsSnapshot(g48, r1v48), "");
    File.Delete(Path.Combine(rd48, "new.txt"));
    Check("还原短路: 删掉多余文件后恢复一致",
        BackupService.IsLiveSameAsSnapshot(g48, r1v48), "");
    g48.SavePaths.Add(new SavePathEntry { Path = Path.Combine(temp, "no_such_dir_48") });
    Check("还原短路: 存档路径配置变化时保守放行（false，不误跳过）",
        !BackupService.IsLiveSameAsSnapshot(g48, r1v48), "");
    g48.SavePaths.RemoveAt(g48.SavePaths.Count - 1);
    Check("还原短路: 路径配置恢复后重新判定一致",
        BackupService.IsLiveSameAsSnapshot(g48, r1v48), "");

    // UI 接线守卫：比对必须发生在确认框之前；一致时给"无需还原"提示
    string mainCs48 = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameSaveManager", "MainWindow.xaml.cs")));
    int idxSame48 = mainCs48.IndexOf("BackupService.IsLiveSameAsSnapshot(g, r)", StringComparison.Ordinal);
    int idxConfirm48 = mainCs48.IndexOf("ConfirmRestore.Show(this", StringComparison.Ordinal);
    Check("还原短路: RestoreAsync 中比对在确认框之前",
        idxSame48 > 0 && idxSame48 < idxConfirm48, $"{idxSame48}/{idxConfirm48}");
    Check("还原短路: 一致时提示「无需还原」且直接返回",
        mainCs48.Contains("当前进度与这份备份完全一致，无需还原"), "");
    Check("还原短路: 安全快照在确认框点明「撤销上次还原」语义",
        mainCs48.Contains("r.Note == \"还原前安全快照\"")
        && mainCs48.Contains("还原它相当于撤销上次还原"), "");

    // ===== 区块 49：玩家筹码第二行 live/备份动态状态 =====
    string ls49 = Path.Combine(temp, "live_state");
    Directory.CreateDirectory(ls49);
    File.WriteAllText(Path.Combine(ls49, "save.dat"), "abc");
    var g49 = new GameEntry { NameEN = "TestLiveState49" };
    g49.SavePaths.Add(new SavePathEntry { Path = ls49 });

    // 有 live、零备份 → nobackup；live 为空 → nosave
    var stNoBak49 = BackupService.GetLiveState(g49, ProfileService.DefaultFirstPlayerName);
    Check("筹码状态: 有存档无备份 → nobackup", stNoBak49.Kind == "nobackup" && stNoBak49.LiveFiles == 1,
        stNoBak49.Kind);
    File.Delete(Path.Combine(ls49, "save.dat"));
    Check("筹码状态: 存档目录空 → nosave", BackupService.GetLiveState(g49, "x").Kind == "nosave", "");

    // 备份后 live 与最新备份一致 → same；改动 → changed
    // 各版本显式拉开游戏内存档时间（模拟真实跨分钟/小时的游玩），避开同机 2 秒判定容差
    File.WriteAllText(Path.Combine(ls49, "save.dat"), "abc");
    File.SetLastWriteTime(Path.Combine(ls49, "save.dat"), DateTime.Now.AddMinutes(-3));
    var bak49 = BackupService.Create(g49, "首备");
    string prof49 = g49.ActiveProfile;
    Check("筹码状态: 刚备份完 → same", BackupService.GetLiveState(g49, prof49).Kind == "same", "");
    File.WriteAllText(Path.Combine(ls49, "save.dat"), "abc-CHANGED-longer");
    File.SetLastWriteTime(Path.Combine(ls49, "save.dat"), DateTime.Now.AddMinutes(-2));
    var stChanged49 = BackupService.GetLiveState(g49, prof49);
    Check("筹码状态: 玩过没备份 → changed 且带 live 最新时间",
        stChanged49.Kind == "changed" && stChanged49.LiveLatest != default
        && stChanged49.BackupCount == 1, stChanged49.Kind);

    // 心目中的档时间轴模型：A=首备(tA)，B=更新的备份(tB)。还原回旧档 A → 指针移到 A（why=restore），
    // live==A → same；比 A 更新的备份 B 改由备份列表橙色感叹号逐行提示，筹码不再告警
    var bakB49 = BackupService.Create(g49, "新进度备份");
    Check("筹码状态: 两份备份齐备（还原去重不产生冗余快照）", g49.Backups.Count == 2,
        g49.Backups.Count.ToString());
    Check("筹码状态: 备份后 live==最新备份 → same（无指针时回退最新可还原包）",
        BackupService.GetLiveState(g49, prof49).Kind == "same", "");
    BackupService.Restore(g49, bak49);
    var stRestored49 = BackupService.GetLiveState(g49, prof49);
    Check("筹码状态: 还原旧档 → same + 指针=旧档 + why=restore（认可动作移动指针）",
        stRestored49.Kind == "same" && stRestored49.FavoriteWhy == "restore"
        && stRestored49.MatchedAt == bak49.SavedAt && stRestored49.BackupCount >= 2
        && g49.SwitchAnchors.TryGetValue(prof49, out var favA49) && favA49 == bak49.FileJson,
        $"{stRestored49.Kind}/why={stRestored49.FavoriteWhy}/📍={stRestored49.MatchedAt:HH:mm:ss}");
    // 先还原到最新备份 B（此刻 live=tB 干净）：指针=B、why=restore、MatchedAt=tB
    BackupService.Restore(g49, bakB49);
    var stBack49 = BackupService.GetLiveState(g49, prof49);
    Check("筹码状态: 还原最新备份 → same 且 MatchedAt=该备份存档时间",
        stBack49.Kind == "same" && stBack49.MatchedAt == bakB49.SavedAt
        && stBack49.FavoriteWhy == "restore",
        $"{stBack49.Kind}/{stBack49.MatchedAt:HH:mm:ss}/{bakB49.SavedAt:HH:mm:ss}");
    // 还原后再玩（新内容、存档时间设为 -1 分钟，比 B 新但不污染未来时间）→ changed；
    // 走手动备份入口（BackupAsFavorite）→ 指针移到新包 why=manual → same
    File.WriteAllText(Path.Combine(ls49, "save.dat"), "post-restore-fun");
    File.SetLastWriteTime(Path.Combine(ls49, "save.dat"), DateTime.Now.AddMinutes(-1));
    Check("筹码状态: 还原后再玩 → changed（指针停在 B，不掩盖新进度）",
        BackupService.GetLiveState(g49, prof49).Kind == "changed", "");
    var bakC49 = ProfileService.BackupAsFavorite(g49, "再玩后的备份", false, "manual", out _);
    Check("筹码状态: 手动备份移动指针 → same + why=manual + MatchedAt=新包",
        BackupService.GetLiveState(g49, prof49).Kind == "same"
        && g49.FavoriteWhy!.TryGetValue(prof49, out var why49) && why49 == "manual"
        && g49.SwitchAnchors[prof49] == bakC49.FileJson, "");
    // 幂等：再回首备 A，live=A → 稳定 same+restore；比 A 新的 B/C 由行感叹号提示（下方区块 59 专测打标）
    File.WriteAllText(Path.Combine(ls49, "save.dat"), "abc");
    File.SetLastWriteTime(Path.Combine(ls49, "save.dat"), DateTime.Now.AddMinutes(-3));
    BackupService.Restore(g49, bak49);
    Check("筹码状态: 再次还原回首备 → same+restore（指针幂等落 A）",
        BackupService.GetLiveState(g49, prof49) is var stAgain49
        && stAgain49.Kind == "same" && stAgain49.FavoriteWhy == "restore"
        && g49.SwitchAnchors[prof49] == bak49.FileJson, "");
    // 收尾还原到最新备份，供后续 JSON/跨机用例使用
    BackupService.Restore(g49, g49.Backups.OrderByDescending(b => b.SavedAt).First());

    // 心目中的档指针 JSON 持久化往返（重启后仍认得她认可的那份与原因）
    var markJson49 = System.Text.Json.JsonSerializer.Serialize(g49);
    var g49Round = System.Text.Json.JsonSerializer.Deserialize<GameEntry>(markJson49)!;
    Check("筹码状态: 指针+原因随 games.json 持久化往返（字段不丢）",
        g49Round.SwitchAnchors != null && g49Round.FavoriteWhy != null
        && g49Round.SwitchAnchors.TryGetValue(prof49, out var favRound49)
        && favRound49 == g49.Backups.OrderByDescending(b => b.SavedAt).First().FileJson
        && g49Round.FavoriteWhy.TryGetValue(prof49, out var whyRound49) && whyRound49 == "restore",
        "指针或原因往返丢失");
    g49Round.FavoriteWhy!["x"] = "manual";
    var g49Round2 = System.Text.Json.JsonSerializer.Deserialize<GameEntry>(
        System.Text.Json.JsonSerializer.Serialize(g49Round))!;
    Check("筹码状态: FavoriteWhy 值往返一致",
        g49Round2.FavoriteWhy!.ContainsKey("x") && g49Round2.FavoriteWhy["x"] == "manual", "");

    // 回归（英灵神殿真实事故）：还原前安全快照打包时间最新（22:49），内容却是旧进度（前一天 23:18）；
    // 另有一份打包更早、内容更新的备份。LastProgressSaved 必须按 SavedAt 取 max，不能按 Time 取最新。
    var gPar49 = new GameEntry { NameEN = "TestParadox49" };
    DateTime oldProgress49 = DateTime.Now.AddDays(-1);   // 安全快照记着的旧 live
    DateTime newProgress49 = DateTime.Now.AddHours(-2);  // 真正的最新进度（打包时间更早）
    gPar49.Backups.Add(new BackupRecord { Profile = "豪子49p", Time = DateTime.Now.AddMinutes(-10), SavedAt = newProgress49, File = "newer_content.zip" });
    gPar49.Backups.Add(new BackupRecord { Profile = "豪子49p", Time = DateTime.Now, SavedAt = oldProgress49, File = "safety_snapshot.zip" });
    var lps49 = ProfileService.LastProgressSaved(gPar49, "豪子49p");
    Check("云端核对: LastProgressSaved 按 SavedAt 取 max（不被打包最新的安全快照旧进度骗）",
        lps49.HasValue && Math.Abs((lps49.Value - newProgress49).TotalSeconds) < 1,
        lps49?.ToString("HH:mm:ss") ?? "<null>");
    // 云端最新包与本机最新进度同刻（就是本机上传的那份）→ 不得提示云端更新（旧口径会误报）
    var cloudSame49 = new CloudRemoteFile
    {
        RelPath = "英灵神殿/豪子49p/x.zip", Name = "x.zip", Profile = "豪子49p",
        Modified = DateTime.UtcNow, SaveAt = newProgress49.ToUniversalTime(),
    };
    Check("云端核对: ☁=💾 同刻即便存在打包更新的旧内容安全快照也不报云端新（英灵神殿事故回归）",
        !CloudSyncService.ShouldPromptCloudNewer(lps49, cloudSame49, false),
        lps49?.ToUniversalTime().ToString("HH:mm:ss") + " vs " + cloudSame49.SaveAt?.ToString("HH:mm:ss"));

    // 跨机云下载还原（用户实测场景）：异地机器的存档时间更新（模拟另一台电脑刚玩过）。
    // 时间轴模型只比游戏内存档时间：云端包 SavedAt 最大 → 还原后 📍=💾=云端时间 + 还原标记 → restored，
    // 与 manifest.Paths 是源机路径、安全快照打包时间最新都无关
    string srcLive49b = Path.Combine(temp, "src_pc_live_49b");
    Directory.CreateDirectory(srcLive49b);
    File.WriteAllText(Path.Combine(srcLive49b, "save.dat"), "cloud-progress-v1");
    File.SetLastWriteTime(Path.Combine(srcLive49b, "save.dat"), DateTime.Now.AddMinutes(30));  // 异地新进度
    var gSrc49b = new GameEntry { NameEN = "TestCloudSrc49b" };
    gSrc49b.SavePaths.Add(new SavePathEntry { Path = srcLive49b });
    ProfileService.CreatePlayer(gSrc49b, "云游49b");          // 初始备份即云端包
    string dl49b = Path.Combine(temp, "dl49b.zip");
    File.Copy(gSrc49b.Backups.OrderByDescending(b => b.Time).First().File, dl49b);

    string dstLive49b = Path.Combine(temp, "dst_pc_live_49b");
    Directory.CreateDirectory(dstLive49b);
    File.WriteAllText(Path.Combine(dstLive49b, "save.dat"), "local-v1");
    var gDst49b = new GameEntry { NameEN = "TestCloudDst49b" };
    gDst49b.SavePaths.Add(new SavePathEntry { Path = dstLive49b });
    ProfileService.CreatePlayer(gDst49b, "云游49b");          // 本机初始备份=local-v1
    File.WriteAllText(Path.Combine(dstLive49b, "save.dat"), "local-v2-changed");  // 还原前有新进度→安全快照必建
    var adopted49b = BackupService.AdoptDownloadedZip(gDst49b, dl49b)!;
    System.Threading.Thread.Sleep(1200);   // 打包时间戳精确到秒：保证还原前安全快照严格最新打包
    Check("跨机云还原: 云端包已归属同名玩家", (adopted49b.Profile ?? "") == "云游49b", adopted49b.Profile ?? "");
    BackupService.Restore(gDst49b, adopted49b, purgeNewFiles: true,
        targetPaths: ProfileService.EffectivePaths(gDst49b));
    Check("跨机云还原: 还原前安全快照已建立",
        gDst49b.Backups.Any(b => b.Note == "还原前安全快照"), "");
    var stCloud49b = BackupService.GetLiveState(gDst49b, "云游49b");
    Check("跨机云还原: 还原异地最新包 → same+restore（指针=云端包，按存档时间不看打包时间/源机路径）",
        stCloud49b.Kind == "same" && stCloud49b.FavoriteWhy == "restore"
        && stCloud49b.MatchedAt != default, stCloud49b.Kind);
    Check("跨机云还原: 严格口径因源机路径漏判、内容口径命中",
        !BackupService.IsLiveSameAsSnapshot(gDst49b, adopted49b)
        && BackupService.IsLiveSameAsSnapshotContent(gDst49b, adopted49b), "");
    File.WriteAllText(Path.Combine(dstLive49b, "save.dat"), "cloud-progress-v1-TAMPERED");
    Check("跨机内容口径: 内容变化后放宽判定同样判否",
        !BackupService.IsLiveSameAsSnapshotContent(gDst49b, adopted49b), "");
    Check("跨机云还原: 篡改后 live 时间落后心目中的云端档 → older（点击行可还原指针包）",
        BackupService.GetLiveState(gDst49b, "云游49b").Kind == "older", "");

    // 用户实测：非严格还原后多出旧残留文件（本地世界/自动备份目录），残留不新于被还原进度 → same
    string srcLive49c = Path.Combine(temp, "src_pc_live_49c");
    Directory.CreateDirectory(srcLive49c);
    File.WriteAllText(Path.Combine(srcLive49c, "cloud.dat"), "CLOUD-NEW");
    File.SetLastWriteTime(Path.Combine(srcLive49c, "cloud.dat"), DateTime.Now.AddMinutes(40));
    var gSrc49c = new GameEntry { NameEN = "TestCloudSrc49c" };
    gSrc49c.SavePaths.Add(new SavePathEntry { Path = srcLive49c });
    ProfileService.CreatePlayer(gSrc49c, "云游49c");
    string dl49c = Path.Combine(temp, "dl49c.zip");
    File.Copy(gSrc49c.Backups.OrderByDescending(b => b.Time).First().File, dl49c);

    string dstLive49c = Path.Combine(temp, "dst_pc_live_49c");
    Directory.CreateDirectory(dstLive49c);
    File.WriteAllText(Path.Combine(dstLive49c, "old_local.dat"), "OLD-WORLD");  // 本地旧进度（残留候选）
    var gDst49c = new GameEntry { NameEN = "TestCloudDst49c" };
    gDst49c.SavePaths.Add(new SavePathEntry { Path = dstLive49c });
    ProfileService.CreatePlayer(gDst49c, "云游49c");
    File.WriteAllText(Path.Combine(dstLive49c, "old_local.dat"), "OLD-WORLD-V2");  // 还原前动过→安全快照必建
    var adopted49c = BackupService.AdoptDownloadedZip(gDst49c, dl49c)!;
    BackupService.Restore(gDst49c, adopted49c, purgeNewFiles: false,   // 非严格：old_local.dat 残留
        targetPaths: ProfileService.EffectivePaths(gDst49c));
    var filesAfter49c = Directory.GetFiles(dstLive49c).Select(Path.GetFileName).OrderBy(x => x).ToList();
    Check("非严格云还原: 云端文件落盘且旧残留保留",
        filesAfter49c.Contains("cloud.dat") && filesAfter49c.Contains("old_local.dat"),
        string.Join(",", filesAfter49c));
    Check("非严格云还原: 残留不新于云端进度 → same（英灵神殿 18 残留文件场景）",
        BackupService.GetLiveState(gDst49c, "云游49c").Kind == "same",
        BackupService.GetLiveState(gDst49c, "云游49c").Kind);

    // 反向场景：还原前本地残留就比云端包新（本地玩得更远却还原了旧云端档，非严格）。
    // 新模型下指针=云端包(+40)、live(+50) 更新 → changed（如实提示未备份新进度）；
    // 「新进度已被备份兜住」改由 LastProgressSaved/安全快照事实保证（筹码不再替库粉饰）
    string dstLive49d = Path.Combine(temp, "dst_pc_live_49d");
    Directory.CreateDirectory(dstLive49d);
    File.WriteAllText(Path.Combine(dstLive49d, "old_local.dat"), "OLD-WORLD");
    var gDst49d = new GameEntry { NameEN = "TestCloudDst49d" };
    gDst49d.SavePaths.Add(new SavePathEntry { Path = dstLive49d });
    ProfileService.CreatePlayer(gDst49d, "云游49c");
    File.WriteAllText(Path.Combine(dstLive49d, "old_local.dat"), "OLD-WORLD-FURTHER");  // 比初始备份新→快照必建
    File.SetLastWriteTime(Path.Combine(dstLive49d, "old_local.dat"), DateTime.Now.AddMinutes(50)); // 本地走得更远
    string dl49d = Path.Combine(temp, "dl49d.zip");   // Adopt 会消费下载文件，需另复制一份
    File.Copy(gSrc49c.Backups.OrderByDescending(b => b.Time).First().File, dl49d);
    var adopted49d = BackupService.AdoptDownloadedZip(gDst49d, dl49d)!;
    BackupService.Restore(gDst49d, adopted49d, purgeNewFiles: false,
        targetPaths: ProfileService.EffectivePaths(gDst49d));
    var st49d = BackupService.GetLiveState(gDst49d, "云游49c");
    Check("非严格云还原: 本地残留新于云端档 → changed（指针停在云端包，如实提示有未备份新进度）",
        st49d.Kind == "changed", st49d.Kind);
    Check("非严格云还原: 本地新进度由还原前安全快照兜住（LastProgressSaved=+50，云端文件也在目录）",
        File.Exists(Path.Combine(dstLive49d, "cloud.dat"))
        && gDst49d.Backups.Any(b => b.Note == "还原前安全快照"
            && Math.Abs((b.SavedAt - DateTime.Now.AddMinutes(50)).TotalSeconds) < 2)
        && ProfileService.LastProgressSaved(gDst49d, "云游49c") is DateTime lps49d
        && Math.Abs((lps49d - DateTime.Now.AddMinutes(50)).TotalSeconds) < 2,
        st49d.Kind);

    // VM 接线：RefreshProfiles 后当前筹码存在且有静态状态；短词上限 7 字防撑宽
    g49.RefreshProfiles();
    var activeVm49 = g49.ProfileSlots.FirstOrDefault(v => v.IsActive);
    Check("筹码状态: 当前筹码存在且静态文案非空",
        activeVm49 != null && activeVm49.StatusText.Length > 0 && activeVm49.StatusKind.Length > 0,
        activeVm49?.StatusText ?? "<null>");
    string[] shortStatus49 = { "存档备份一致", "存档已还原", "存档已备份", "新存档未备份", "游玩中", "从未备份",
        "游戏无存档", "当前进度较旧", "云端有较新备份" };
    Check("筹码状态: 所有动态短词 ≤7 个字（防筹码过长/截断）",
        shortStatus49.All(t => t.Length <= 7), string.Join("/", shortStatus49.Select(t => t.Length)));
    var otherVm49 = g49.ProfileSlots.FirstOrDefault(v => !v.IsActive);
    Check("筹码状态: 非当前筹码（若有）只显示备份时间、不标 live 状态",
        otherVm49 == null || otherVm49.StatusKind == "normal", otherVm49?.StatusKind ?? "无其他筹码");
    // 可点链接的 VM 契约：初始不可点；SetStatus 带 action 后放开
    Check("筹码状态: 状态动作初始为空、不可点",
        !activeVm49!.StatusClickable && string.IsNullOrEmpty(activeVm49.StatusAction), "");
    activeVm49.SetStatus("当前进度较旧", "info", "tip", "restoreFavorite");
    Check("筹码状态: restoreFavorite 动作 → 可点；清空动作复位",
        activeVm49.StatusClickable && activeVm49.StatusAction == "restoreFavorite", "");
    activeVm49.SetStatus("x", "normal", "tip");
    Check("筹码状态: 清空动作后恢复不可点", !activeVm49.StatusClickable, "");

    // XAML 守卫：绑定新字段、限宽省略号、链接态点击/手型/下划线、旧 SavedAtText 不得残留
    string mainXaml49 = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameSaveManager", "MainWindow.xaml")));
    string chipBlock49 = mainXaml49.Substring(
        mainXaml49.IndexOf("DataType=\"models:ProfileSlotVm\"", StringComparison.Ordinal),
        Math.Min(12000, mainXaml49.Length - mainXaml49.IndexOf("DataType=\"models:ProfileSlotVm\"", StringComparison.Ordinal)));
    Check("筹码状态: XAML 绑定 StatusText/StatusKind/ToolTip=StatusTip",
        chipBlock49.Contains("Text=\"{Binding StatusText}\"")
        && chipBlock49.Contains("Binding StatusKind")
        && chipBlock49.Contains("ToolTip=\"{Binding StatusTip}\""),
        $"{chipBlock49.IndexOf("StatusText")}/{chipBlock49.IndexOf("StatusKind")}/{chipBlock49.IndexOf("StatusTip")}");
    Check("筹码状态: 第二行限宽省略号、筹码整体限宽（防过长/显示不全）",
        chipBlock49.Contains("TextTrimming=\"CharacterEllipsis\"")
        && chipBlock49.Contains("MaxWidth=\"144\"")
        && chipBlock49.Contains("MaxWidth=\"168\""), "");
    Check("筹码状态: good/warn/info 三状态色（含蓝底上的浅绿浅琥珀浅蓝）",
        chipBlock49.Contains("Value=\"good\"") && chipBlock49.Contains("Value=\"warn\"")
        && chipBlock49.Contains("Value=\"info\"")
        && chipBlock49.Contains("#8FE0B4") && chipBlock49.Contains("#FFD27D")
        && chipBlock49.Contains("#AECAFF"), "");
    Check("筹码状态: 两个可点链接（点击事件/命中开关/手型/下划线/双 action）",
        chipBlock49.Contains("Click=\"OnSlotStatusClick\"")
        && chipBlock49.Contains("IsHitTestVisible=\"{Binding StatusClickable}\"")
        && chipBlock49.Contains("Cursor=\"Hand\"")
        && chipBlock49.Contains("Value=\"Underline\"")
        && chipBlock49.Contains("Value=\"restoreFavorite\"")
        && chipBlock49.Contains("Value=\"restoreCloud\""), "");
    Check("筹码状态: 旧 SavedAtText 绑定已移除",
        !mainXaml49.Contains("SavedAtText"), "");

    // 代码守卫：异步核对接在选中/变更/运行翻转三处；零 IO 契约（RefreshProfiles 不调 GetLiveState）
    string mainCs49 = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameSaveManager", "MainWindow.xaml.cs")));
    Check("筹码状态: 选中游戏/变更后/启停翻转均触发异步核对",
        mainCs49.Contains("RefreshActiveSlotLiveStateAsync(SelectedGame)")
        && System.Text.RegularExpressions.Regex.Matches(mainCs49, "RefreshActiveSlotLiveStateAsync\\(g\\)").Count >= 3,
        "");
    Check("筹码状态: 状态词九件套齐全（含当前进度较旧/云端有较新备份）",
        new[] { "存档备份一致", "存档已还原", "存档已备份", "新存档未备份", "游玩中", "从未备份",
            "游戏无存档", "当前进度较旧", "云端有较新备份" }
            .All(t => mainCs49.Contains(t)), "");
    Check("筹码状态: same 态按 why 分四种措辞（restore/manual/auto/其他）",
        mainCs49.Contains("case \"restore\":") && mainCs49.Contains("当前存档就是你手动还原选择的那份")
        && mainCs49.Contains("当前存档与手动备份一致")
        && mainCs49.Contains("当前存档与退出游戏时的自动备份一致")
        && mainCs49.Contains("当前存档与心目中的备份一致"), "");
    Check("筹码状态: 云端核对接线（已配置才查/同口径比较/覆盖为 restoreCloud/离线静默）",
        mainCs49.Contains("CloudSyncService.Configured")
        && mainCs49.Contains("CloudSyncService.ShouldPromptCloudNewer(localSaved, newestCloud, known)")
        && mainCs49.Contains("\"restoreCloud\"")
        && mainCs49.Contains("case \"older\""), "");
    Check("筹码状态: 状态行点击接线（心目中的档一键还原/云端只下载不自动还原/百分比进度）",
        mainCs49.Contains("private async void OnSlotStatusClick")
        && mainCs49.Contains("PreparePlayerFromCloud(g, profile, default, progress)")
        && mainCs49.Contains("正在下载云端存档… {p * 100:0}%")
        && mainCs49.Contains("ProfileService.ResolveFavorite(g, profile, out _)")
        && mainCs49.Contains("下载云端存档")
        // 云端下载分支内不得再调 RestoreAsync（restoreFavorite 分支的调用在方法前半段，这里按分支切片验证）
        && mainCs49.Substring(mainCs49.IndexOf("if (vm.StatusAction == \"restoreCloud\")"))
               .IndexOf("RestoreAsync", StringComparison.Ordinal) < 0, "");
    // 下载防回传接线守卫：三个下载入口都登记、手动单份上传有防御闸、核验补存量登记
    string cloudCs49 = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameSaveManager", "Services", "CloudSyncService.cs")));
    Check("云端下载: 下载前预检 + 结果三态 + 入库存为本地较新（不再显示云端较新）",
        cloudCs49.Contains("private static BackupRecord? FindLocalForRemote(GameEntry g, string profile, CloudRemoteFile f)")
        && cloudCs49.Contains("result.WasAlreadyLocal = true")
        && cloudCs49.Contains("public sealed class PrepareCloudResult")
        && cloudCs49.Contains("IProgress<double>? progress = null")
        && mainCs49.Contains("res.WasAlreadyLocal"), "");
    string browserCs49 = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameSaveManager", "CloudBrowserWindow.xaml.cs")));
    // 三个下载入口：自动收集 + PreparePlayerFromCloud 在 CloudSyncService（2 处登记），浏览窗手动下载 1 处
    Check("下载防回传: 三入口登记 + 手动上传防御闸 + 存量核验补登记接线齐全",
        cloudCs49.Contains("public static void MarkAdoptedFromCloud(string localFile, string remoteRel)")
        && new[] { "MarkAdoptedFromCloud(adopted.File, f.RelPath)",
                   "MarkAdoptedFromCloud(adopted.File, newest.RelPath)" }.All(s => cloudCs49.Contains(s))
        && browserCs49.Contains("CloudSyncService.MarkAdoptedFromCloud(adopted.File, item.RelPath)")
        && cloudCs49.Contains("if (IsUploaded(b.File)) return;")
        && cloudCs49.Contains("旧版下载入库的包")
        && cloudCs49.Contains("存量下载包"),
        cloudCs49.IndexOf("MarkAdoptedFromCloud").ToString());
    string svcCs49 = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameSaveManager", "Services", "BackupService.cs")));
    string profCs49 = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameSaveManager", "Services", "ProfileService.cs")));
    Check("筹码状态: Restore 认可落指针（why=restore）、筹码以指针为基准、older 分支与行感叹号打标齐全",
        svcCs49.Contains("ProfileService.MarkFavorite(g, r.Profile ?? \"\", r.FileJson, \"restore\")")
        && svcCs49.Contains("\"older\"")
        && svcCs49.Contains("public static void ApplyNewerThanLiveFlags"), "");
    Check("筹码状态: SwitchTo 的 Restore 不写指针（markFavorite:false），指针只由认可动作移动",
        profCs49.Contains("markFavorite: false")
        && profCs49.Contains("public static BackupRecord? ResolveFavorite")
        && profCs49.Contains("public static void BackfillFavorite"), "");
    Check("状态栏: 玩家措辞（最近一次自动备份/已开启自动备份 N 个游戏/备份位置）",
        mainCs49.Contains("最近一次自动备份")
        && mainCs49.Contains("已开启自动备份：") && mainCs49.Contains("个游戏 · 备份位置"), "");
    Check("状态栏: 未备份前左侧留空、旧技术措辞已移除",
        mainCs49.Contains("StatusLeft.Text = _lastAutoBackup == DateTime.MinValue")
        && mainCs49.Contains("? \"\"")
        && !mainCs49.Contains("等待游戏退出触发") && !mainCs49.Contains("监控中"), "");
    string gameEntryCs49 = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameSaveManager", "Models", "GameEntry.cs")));
    Check("筹码状态: RefreshProfiles 保持零 IO（不直接调 GetLiveState）",
        !gameEntryCs49.Contains("GetLiveState"), "");
    Check("筹码状态: GetLiveState 六分支齐全（nosave/nobackup/same/older/changed/unknown）",
        new[] { "nosave", "nobackup", "same", "older", "changed", "unknown" }
            .All(t => svcCs49.Contains(t))
        && !svcCs49.Contains("\"localnewer\"") && !svcCs49.Contains("\"restored\""), "");

    // ===== 区块 50：备份行紧凑化（A 方案：只减间距，结构/按钮/文案一字不动）=====
    // 行模板整块：从 ItemsControl 起点截到操作栏前，覆盖 Grid Margin 到最后一个按钮
    int rowStart50 = mainXaml49.IndexOf("ItemsSource=\"{Binding VisibleBackups}\"", StringComparison.Ordinal);
    int rowEnd50 = mainXaml49.IndexOf("<!-- 操作栏（同分区底部） -->", rowStart50, StringComparison.Ordinal);
    string rowBlock50 = mainXaml49.Substring(rowStart50, rowEnd50 - rowStart50);
    Check("紧凑备份行(A): 行上下距 8→3",
        rowBlock50.Contains("<Grid Margin=\"16,3\""), "");
    Check("紧凑备份行(A): 双行结构原样保留（185 时间列 + 存档于/备份于双行）",
        rowBlock50.Contains("Width=\"185\"") && rowBlock50.Contains("{Binding SavedAtMainText}")
        && rowBlock50.Contains("{Binding BackupTimeText}"), "");
    Check("备份行: 改名/还原/删除三个文字按钮原样",
        rowBlock50.Contains("Content=\"改名\"") && rowBlock50.Contains("Content=\"还原\"")
        && rowBlock50.Contains("Content=\"删除\""), "");
    // 云按钮：与锁同款矢量图标（灰云/绿云带勾）+ 文字三态，不再用 ☁ emoji 字形
    Check("备份行云: 矢量云 Path（已存=Green 云带勾）+ 上传云端/云端已存/已删重传文字，无 ☁ emoji",
        rowBlock50.Contains("OnUploadBackup") && rowBlock50.Contains("M19.35 10.04")
        && rowBlock50.Contains("Value=\"上传云端\"") && rowBlock50.Contains("Value=\"云端已存\"")
        && rowBlock50.Contains("Value=\"已删重传\"")
        && !rowBlock50.Contains("☁"), "");
    // 行底极淡分隔线：RowDivider 资源 + 行模板底边线
    Check("备份行: 每行底有极淡分隔线（RowDivider，0,0,0,1）",
        rowBlock50.Contains("RowDivider") && rowBlock50.Contains("BorderThickness=\"0,0,0,1\""), "");
    foreach (var themeFile50 in new[] { "Dark.xaml", "Light.xaml", "LightSoft.xaml" })
    {
        string themeText50 = File.ReadAllText(Path.Combine(themesDir47, themeFile50));
        Check($"{themeFile50}: 定义行分隔线资源 RowDivider（须弱于 BorderBrush）",
            themeText50.Contains("x:Key=\"RowDivider\""), "");
    }
    // 存档位置分区：标题/路径行留白与玩家分区一致
    int pathSec50 = mainXaml49.IndexOf("<!-- 存档位置 -->", StringComparison.Ordinal);
    string pathBlock50 = mainXaml49.Substring(pathSec50, Math.Min(2600, mainXaml49.Length - pathSec50));
    Check("存档位置分区: 标题 16,4,16,7、路径行 16,5（对齐下方分区），旧 16,12/16,9 已移除",
        pathBlock50.Contains("Margin=\"16,4,16,7\"") && pathBlock50.Contains("Margin=\"16,5\"")
        && !pathBlock50.Contains("Margin=\"16,12\"") && !pathBlock50.Contains("Margin=\"16,9\""), "");
    Check("游戏卡片: 行高 62（卡片本体 56 与图标齐高），旧 68 已移除",
        mainXaml49.Contains("CardRoot\" Height=\"62\"") && !mainXaml49.Contains("CardRoot\" Height=\"68\""), "");
    Check("紧凑备份行(A): 未引入合行方案的绑定与图标按钮",
        !mainXaml49.Contains("TimesCompactText") && !mainXaml49.Contains("TimesTipText")
        && !mainXaml49.Contains("Content=\"✎\"") && !mainXaml49.Contains("Content=\"🗑\""), "");
    Check("紧凑备份行(A): 模型层未残留短格式时间属性",
        !gameEntryCs49.Contains("SavedAtShortText") && !gameEntryCs49.Contains("BackupShortText"), "");
    // 锁标记用矢量 Path 实心涂色（WPF 不支持彩色 emoji 字形）：未锁灰、锁定金
    int lockBtn50 = rowBlock50.IndexOf("OnToggleLock", StringComparison.Ordinal);
    string lockBlock50 = rowBlock50.Substring(lockBtn50, Math.Min(1600, rowBlock50.Length - lockBtn50));
    Check("备份行锁: 矢量 Path 实心锁，未锁灰/锁定金（GoldLock），不再用 emoji",
        lockBlock50.Contains("<Path") && lockBlock50.Contains("GoldLock")
        && lockBlock50.Contains("Viewbox") && !lockBlock50.Contains("Segoe UI Emoji")
        && !lockBlock50.Contains("🔒"), "");
    int lockTag50 = rowBlock50.IndexOf("OnToggleLock", StringComparison.Ordinal);
    string lockBtnTag50 = rowBlock50.Substring(Math.Max(0, lockTag50 - 120), 120);
    Check("备份行锁: 按钮本体必须保留 BtnSm（不得退回系统默认白底按钮）",
        lockBtnTag50.Contains("BtnSm"), "");
    // 整分区纵向留白收紧（这些串在全文唯一，直接全文匹配）
    Check("紧凑分区(A): 标题/筹码行/列表头/操作栏留白已收紧",
        mainXaml49.Contains("Margin=\"16,4,16,2\"") && mainXaml49.Contains("Margin=\"16,3,16,-2.5\"")
        && mainXaml49.Contains("Margin=\"16,7,16,1\"") && mainXaml49.Contains("Padding=\"16,8\""), "");
    Check("紧凑分区(A): 主按钮尺寸未动（Padding 24,8）",
        mainXaml49.Contains("Padding=\"24,8\""), "");

    // 新导入游戏：ActiveProfile 为空的虚拟玩家1，备份归属/空提示不得出现空括号「」
    string dirNew50 = Path.Combine(Path.GetTempPath(), "gsm_smoke_newgame_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dirNew50);
    try
    {
        var gNew50 = new GameEntry { NameEN = "Smoke Newly Imported 50" };
        gNew50.SavePaths.Add(new SavePathEntry { Path = dirNew50 });
        gNew50.RefreshProfiles();
        Check("新导入游戏: 激活玩家显示名=默认玩家名（空 ActiveProfile 不直接上界面）",
            gNew50.ActiveProfile == ""
            && gNew50.ActiveProfileDisplayName == ProfileService.DefaultFirstPlayerName,
            $"active='{gNew50.ActiveProfile}',display='{gNew50.ActiveProfileDisplayName}'");
        Check("新导入游戏: 备份归属提示含玩家名、0 份、无空括号",
            gNew50.BackupOwnerText.Contains($"「{ProfileService.DefaultFirstPlayerName}」")
            && gNew50.BackupOwnerText.Contains("0 份")
            && !gNew50.BackupOwnerText.Contains("「」"), gNew50.BackupOwnerText);
        Check("新导入游戏: 空备份提示含玩家名、无空括号",
            gNew50.EmptyBackupHint.Contains($"「{ProfileService.DefaultFirstPlayerName}」")
            && !gNew50.EmptyBackupHint.Contains("「」"), gNew50.EmptyBackupHint);
    }
    finally { try { Directory.Delete(dirNew50, true); } catch { } }

    // ── 术语统一：界面只说「玩家/玩家存档」，不得出现卡槽/多卡槽/多玩家存档 ──
    string srcRoot50 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameSaveManager"));
    string settingsXaml50 = File.ReadAllText(Path.Combine(srcRoot50, "SettingsWindow.xaml"));
    int iGen50 = settingsXaml50.IndexOf("x:Name=\"PageGeneral\"", StringComparison.Ordinal);
    int iBak50 = settingsXaml50.IndexOf("x:Name=\"PageBackup\"", StringComparison.Ordinal);
    string genPage50 = settingsXaml50.Substring(iGen50, iBak50 - iGen50);
    string bakPage50 = settingsXaml50.Substring(iBak50, settingsXaml50.IndexOf("x:Name=\"PageCloud\"", StringComparison.Ordinal) - iBak50);
    Check("设置: 「默认玩家名称」输入框已移到通用页玩家小节",
        genPage50.Contains("DefaultPlayerNameBox") && genPage50.Contains("Text=\"玩家\"")
        && genPage50.Contains("默认玩家名称"), "");
    Check("设置: 备份页不再含默认玩家名称项与旧「默认卡槽名称」",
        !bakPage50.Contains("DefaultPlayerNameBox") && !settingsXaml50.Contains("默认卡槽名称"), "");
    Check("设置: 关于页功能介绍用「多个玩家各自独立存档」，旧「多卡槽」已移除",
        settingsXaml50.Contains("多个玩家各自独立存档、互不影响") && !settingsXaml50.Contains("多卡槽"), "");
    Check("详情页: Steam 云禁用条=玩家存档已禁用，提示用「多个玩家的存档」",
        mainXaml49.Contains("玩家存档已禁用（Steam 云同步游戏）")
        && mainXaml49.Contains("多个玩家的存档会被云档互相覆盖，因此禁用了玩家存档")
        && !mainXaml49.Contains("多玩家存档已禁用") && !mainXaml49.Contains("禁用玩家功能"), "");
    string profileSvc50 = File.ReadAllText(Path.Combine(srcRoot50, "Services", "ProfileService.cs"));
    Check("ProfileService: 用户文案与界面统一（无 默认卡槽/右键筹码 字样）",
        profileSvc50.Contains("玩家存档已禁用")
        && profileSvc50.Contains("是默认玩家，不能删除；可右键玩家名字改名")
        && !profileSvc50.Contains("玩家1是默认卡槽") && !profileSvc50.Contains("右键筹码改名"), "");
}

// ── 52. 卡槽云端下载：下载前预检（本机已有则零下载）、新包带百分比下载、入库即登记防回传、重复调用幂等 ──
{
    Store.HomeDirOverride = Path.Combine(temp, "vault_predl");
    Store.Init();

    // 先造备份（此时云未配置，不会触发任何上传）
    string dlDirA = Path.Combine(temp, "predl_saves_a");
    Directory.CreateDirectory(dlDirA);
    File.WriteAllText(Path.Combine(dlDirA, "s.dat"), "jia");
    var gDl = new GameEntry { NameEN = "PreDlGame", ActiveProfile = "甲" };
    gDl.SavePaths.Add(new SavePathEntry { Path = dlDirA });
    Store.Games.Add(gDl);
    var bDlA = BackupService.Create(gDl, "甲本机备份");
    byte[] zipA52 = File.ReadAllBytes(bDlA.File);
    string nameA52 = Path.GetFileName(bDlA.File);

    string dlSrcB = Path.Combine(temp, "predl_src_b");
    Directory.CreateDirectory(dlSrcB);
    File.WriteAllText(Path.Combine(dlSrcB, "s.dat"), "yi");
    var gDlSrc = new GameEntry { NameEN = "PreDlSrcB", ActiveProfile = "乙" };
    gDlSrc.SavePaths.Add(new SavePathEntry { Path = dlSrcB });
    Store.Games.Add(gDlSrc);
    var bDlBSrc = BackupService.Create(gDlSrc, "乙他机备份");
    byte[] zipB52 = File.ReadAllBytes(bDlBSrc.File);
    string nameB52 = Path.GetFileName(bDlBSrc.File);
    Store.Games.Remove(gDlSrc);   // 只当 zip 来源，不属本机库游戏

    // 假云：PROPFIND 两级目录（游戏段列玩家文件夹、玩家段列 zip）；GET zip 计数；index.json 404；其余 404/201
    var l52 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    l52.Start();
    int port52 = ((System.Net.IPEndPoint)l52.LocalEndpoint).Port;
    int getA52 = 0, getB52 = 0;
    // 真实布局两级：游戏目录列玩家文件夹；玩家目录列 zip
    string DirXml52(params string[] lines) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<D:multistatus xmlns:D=\"DAV:\">" +
        string.Concat(lines) + "</D:multistatus>";
    string Coll52(string href) =>
        "<D:response><D:href>" + href + "</D:href><D:propstat><D:prop>" +
        "<D:resourcetype><D:collection/></D:resourcetype></D:prop>" +
        "<D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>";
    string File52(string href, long len, string mod) =>
        "<D:response><D:href>" + href + "</D:href><D:propstat><D:prop>" +
        "<D:resourcetype/><D:getcontentlength>" + len + "</D:getcontentlength>" +
        "<D:getlastmodified>" + mod + "</D:getlastmodified></D:prop>" +
        "<D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>";
    byte[] msRoot52 = System.Text.Encoding.UTF8.GetBytes(DirXml52(
        Coll52("/PreDlGame/"), Coll52("/PreDlGame/甲/"), Coll52("/PreDlGame/乙/")));
    byte[] msA52b = System.Text.Encoding.UTF8.GetBytes(DirXml52(
        Coll52("/PreDlGame/甲/"),
        File52("/PreDlGame/甲/" + nameA52, zipA52.Length, "Thu, 01 Jan 2026 00:00:00 GMT")));
    byte[] msB52b = System.Text.Encoding.UTF8.GetBytes(DirXml52(
        Coll52("/PreDlGame/乙/"),
        File52("/PreDlGame/乙/" + nameB52, zipB52.Length, "Thu, 02 Jan 2026 00:00:00 GMT")));
    async Task Serve52(System.Net.Sockets.NetworkStream stream)
    {
        while (true)
        {
            var lb52 = new MemoryStream(); int prev52 = -1, ch52;
            while ((ch52 = stream.ReadByte()) >= 0)
            {
                if (prev52 == '\r' && ch52 == '\n') { lb52.SetLength(Math.Max(0, lb52.Length - 1)); break; }
                lb52.WriteByte((byte)ch52); prev52 = ch52;
            }
            if (ch52 < 0) return;
            string req52 = System.Text.Encoding.ASCII.GetString(lb52.ToArray()).Trim();
            if (req52.Length == 0) return;
            var rp52 = req52.Split(' ');
            string method52 = rp52[0], path52 = Uri.UnescapeDataString(rp52.ElementAtOrDefault(1) ?? "/");
            int clen52 = 0; var hb52 = new MemoryStream(); int pp52 = -1, cb52;
            while ((cb52 = stream.ReadByte()) >= 0)
            {
                if (pp52 == '\r' && cb52 == '\n')
                {
                    hb52.SetLength(Math.Max(0, hb52.Length - 1));
                    string hl52 = System.Text.Encoding.ASCII.GetString(hb52.ToArray());
                    if (hl52.Length == 0) break;
                    if (hl52.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(hl52.Split(':')[1], out clen52);
                    hb52.SetLength(0); pp52 = -1; continue;
                }
                hb52.WriteByte((byte)cb52); pp52 = cb52;
            }
            var body52 = new byte[clen52]; int got52 = 0;
            while (got52 < clen52) { int n52 = stream.Read(body52, got52, clen52 - got52); if (n52 == 0) break; got52 += n52; }
            byte[] respBody52; string head52;
            if (method52 == "PROPFIND" && path52.EndsWith("/PreDlGame/甲"))
            {
                head52 = "HTTP/1.1 207 Multi-Status\r\nContent-Type: application/xml\r\nContent-Length: " + msA52b.Length + "\r\n\r\n";
                respBody52 = msA52b;
            }
            else if (method52 == "PROPFIND" && path52.EndsWith("/PreDlGame/乙"))
            {
                head52 = "HTTP/1.1 207 Multi-Status\r\nContent-Type: application/xml\r\nContent-Length: " + msB52b.Length + "\r\n\r\n";
                respBody52 = msB52b;
            }
            else if (method52 == "PROPFIND" && path52.EndsWith("/PreDlGame"))
            {
                head52 = "HTTP/1.1 207 Multi-Status\r\nContent-Type: application/xml\r\nContent-Length: " + msRoot52.Length + "\r\n\r\n";
                respBody52 = msRoot52;
            }
            else if (method52 == "GET" && path52.EndsWith("/" + nameA52))
            {
                Interlocked.Increment(ref getA52);
                head52 = "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: " + zipA52.Length + "\r\n\r\n";
                respBody52 = zipA52;
            }
            else if (method52 == "GET" && path52.EndsWith("/" + nameB52))
            {
                Interlocked.Increment(ref getB52);
                head52 = "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: " + zipB52.Length + "\r\n\r\n";
                respBody52 = zipB52;
            }
            else if (method52 == "PROPFIND")
            {
                head52 = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n";
                respBody52 = Array.Empty<byte>();
            }
            else
            {
                head52 = "HTTP/1.1 201 Created\r\nContent-Length: 0\r\n\r\n";
                respBody52 = Array.Empty<byte>();
            }
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(head52));
            if (respBody52.Length > 0) await stream.WriteAsync(respBody52);
            await stream.FlushAsync();
        }
    }
    var srv52 = Task.Run(async () =>
    {
        try
        {
            while (true)
            {
                var c52 = await l52.AcceptTcpClientAsync();
                _ = Task.Run(async () => { try { using (c52) await Serve52(c52.GetStream()); } catch { } });
            }
        }
        catch { }
    });

    Store.Settings.CloudEnabled = true;
    Store.Settings.CloudUrl = $"http://127.0.0.1:{port52}/";
    Store.Settings.CloudUser = "u";
    Store.Settings.CloudSecret = CloudSecret.Protect("p");
    Store.SaveSettings();

    string relA52 = "PreDlGame/甲/" + nameA52;
    string relB52 = "PreDlGame/乙/" + nameB52;
    int backupsBefore52 = gDl.Backups.Count;

    // 甲：云端那份本机已有同名备份 → 预检命中，零下载
    var resA52 = await CloudSyncService.PreparePlayerFromCloud(gDl, "甲");
    Check("卡槽云下载: 本机已有时零下载（预检命中，GET 计数为 0）",
        resA52.HasRemote && resA52.WasAlreadyLocal && getA52 == 0,
        $"has={resA52.HasRemote},dup={resA52.WasAlreadyLocal},get={getA52}");
    Check("卡槽云下载: 预检命中返回的就是本机那份记录、备份数不增",
        resA52.Record != null && resA52.Record.File == bDlA.File
        && gDl.Backups.Count == backupsBefore52, "");
    Check("卡槽云下载: 预检命中也登记防回传（云端路径反查到本地）",
        CloudSyncService.IsUploaded(bDlA.File)
        && CloudSyncService.FindLocalByRemote(relA52) == bDlA.File, "");

    // 乙：本机没有 → 真正下载，带百分比
    var prog52 = new CollectProgress52();
    var resB52 = await CloudSyncService.PreparePlayerFromCloud(gDl, "乙", default, prog52);
    Check("卡槽云下载: 新玩家备份真实下载一次（GET=1）且不是已存在态",
        resB52.HasRemote && !resB52.WasAlreadyLocal && getB52 == 1,
        $"has={resB52.HasRemote},dup={resB52.WasAlreadyLocal},get={getB52}");
    Check("卡槽云下载: 下载回报了百分比进度且最终到 100%",
        prog52.Values.Count > 0 && prog52.Values.Max() >= 0.99,
        prog52.Values.Count == 0 ? "无回报" : string.Join(",", prog52.Values));
    Check("卡槽云下载: 入库为乙的「云端下载」备份、文件落盘、卡槽建档",
        resB52.Record != null && (resB52.Record.Profile ?? "") == "乙"
        && resB52.Record.Note == "云端下载" && File.Exists(resB52.Record.File)
        && ProfileService.ListSlots(gDl).Contains("乙"), "");
    string? adoptedFile52 = resB52.Record?.File;
    Check("卡槽云下载: 下载入库即登记防回传（手动/自动都不回传）",
        adoptedFile52 != null
        && CloudSyncService.IsUploaded(adoptedFile52)
        && CloudSyncService.FindLocalByRemote(relB52) == adoptedFile52, "");

    // 幂等：再来一次预检命中，不再下载
    var resB2_52 = await CloudSyncService.PreparePlayerFromCloud(gDl, "乙");
    Check("卡槽云下载: 重复请求幂等——已存在态且 GET 仍为 1、记录是同一份",
        resB2_52.WasAlreadyLocal && getB52 == 1
        && resB2_52.Record != null && resB2_52.Record.File == adoptedFile52,
        $"dup={resB2_52.WasAlreadyLocal},get={getB52}");

    l52.Stop();
    Store.HomeDirOverride = null;
}

// ── 53. 批次A：还原路径错位必须中止、云端包源机路径正确落盘、打包失败剔除清单、删除被占用文件保留记录 ──
{
    string srcRoot53 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameSaveManager"));
    string svcText53 = File.ReadAllText(Path.Combine(srcRoot53, "Services", "BackupService.cs"));
    Check("批次A 静态守卫: BuildRestorePathMap 存在且 PurgeExtras 返回失败列表",
        svcText53.Contains("BuildRestorePathMap") && svcText53.Contains("private static List<string> PurgeExtras"), "");
    Check("批次A 静态守卫: 打包失败不再只限 hot 剔除（failedHot 旧逻辑已消失）",
        !svcText53.Contains("failedHot"), "");
    Check("批次A 静态守卫: 删除 zip 失败时保留记录（文案含 记录已保留）",
        svcText53.Contains("记录已保留"), "");

    // 53.1 备份含两个存档目录（saves + remote），本机只剩一个 → 还原必须中止，live 一个字节都不能动
    Store.HomeDirOverride = Path.Combine(temp, "vault53a");
    Store.Init();
    string dirSave53 = Path.Combine(temp, "g53a", "saves");
    string dirRemote53 = Path.Combine(temp, "g53a", "remote");
    Directory.CreateDirectory(dirSave53);
    Directory.CreateDirectory(dirRemote53);
    File.WriteAllText(Path.Combine(dirSave53, "s.dat"), "SAVE-53");
    File.WriteAllText(Path.Combine(dirRemote53, "r.dat"), "REMOTE-53");
    var g53 = new GameEntry { NameCN = "错位游戏53" };
    g53.SavePaths.Add(new SavePathEntry { Path = dirSave53 });
    g53.SavePaths.Add(new SavePathEntry { Path = dirRemote53 });
    ProfileService.CreatePlayer(g53, "甲");
    var rec53 = g53.Backups[0];
    // 制造"另一个存档目录已不在本机"的局面，并在 live 放额外文件 + 改动内容
    Directory.Delete(dirRemote53, true);
    File.WriteAllText(Path.Combine(dirSave53, "extra53.txt"), "EXTRA");
    File.WriteAllText(Path.Combine(dirSave53, "s.dat"), "LIVE-CHANGED-53");
    string Snapshot53(string d) => string.Join("|", Directory.GetFiles(d).OrderBy(f => f)
        .Select(f => Path.GetFileName(f) + ":" + File.ReadAllText(f)));
    string liveBefore53 = Snapshot53(dirSave53);
    int countBefore53 = g53.Backups.Count;
    bool threw53 = false;
    try
    {
        BackupService.Restore(g53, rec53, purgeNewFiles: true,
            targetPaths: ProfileService.EffectivePaths(g53));
    }
    catch (InvalidOperationException ex) { threw53 = ex.Message.Contains("一一对应"); }
    Check("批次A: 存档目录数量对不上时还原中止（报一一对应）", threw53, "未抛异常或文案不符");
    Check("批次A: 中止后 live 文件内容/额外文件原样未动（没解压、没 PurgeExtras）",
        Snapshot53(dirSave53) == liveBefore53, Snapshot53(dirSave53));
    Check("批次A: 中止前安全快照已建（唯一改动：多一份快照记录）",
        g53.Backups.Count == countBefore53 + 1
        && g53.Backups.Any(b => b.Note == "还原前安全快照"),
        $"count={g53.Backups.Count}");

    // 53.2 云端包：源机 [saves, config]，本机只有 saves → 存档落到本机 saves、config 不落盘、purge 生效
    string srcSave53 = Path.Combine(temp, "g53src", "saves");
    string srcCfg53 = Path.Combine(temp, "g53src", "config");
    Directory.CreateDirectory(srcSave53);
    Directory.CreateDirectory(srcCfg53);
    File.WriteAllText(Path.Combine(srcSave53, "a.dat"), "CLOUD-SAVE-53");
    File.WriteAllText(Path.Combine(srcCfg53, "c.ini"), "CLOUD-CFG-53");
    var gSrc53 = new GameEntry { NameEN = "Test53Src" };
    gSrc53.SavePaths.Add(new SavePathEntry { Path = srcSave53 });
    gSrc53.SavePaths.Add(new SavePathEntry { Path = srcCfg53 });
    ProfileService.CreatePlayer(gSrc53, "甲");
    string dl53 = Path.Combine(temp, "dl53.zip");
    File.Copy(gSrc53.Backups[0].File, dl53);

    Store.HomeDirOverride = Path.Combine(temp, "vault53b");
    Store.Init();
    string dstSave53 = Path.Combine(temp, "g53dst", "saves");
    Directory.CreateDirectory(dstSave53);
    File.WriteAllText(Path.Combine(dstSave53, "a.dat"), "OLD-LOCAL");
    File.WriteAllText(Path.Combine(dstSave53, "junk53.tmp"), "JUNK");
    var gDst53 = new GameEntry { NameEN = "Test53Dst" };
    gDst53.SavePaths.Add(new SavePathEntry { Path = dstSave53 });
    ProfileService.CreatePlayer(gDst53, "甲");
    var adopted53 = BackupService.AdoptDownloadedZip(gDst53, dl53)!;
    BackupService.Restore(gDst53, adopted53, purgeNewFiles: true,
        targetPaths: ProfileService.EffectivePaths(gDst53));
    Check("批次A: 云端源机 saves 正确落盘本机 saves",
        File.ReadAllText(Path.Combine(dstSave53, "a.dat")) == "CLOUD-SAVE-53",
        File.ReadAllText(Path.Combine(dstSave53, "a.dat")));
    Check("批次A: config 不参与切换——本机没有冒出 config 目录、配置内容无处落盘",
        Directory.GetDirectories(Path.Combine(temp, "g53dst"))
            .All(d => string.Equals(Path.GetFileName(d), "saves", StringComparison.OrdinalIgnoreCase))
        && Directory.GetFiles(Path.Combine(temp, "g53dst"), "*", SearchOption.AllDirectories)
            .All(f => !File.ReadAllText(f).Contains("CLOUD-CFG-53")), "");
    Check("批次A: 映射目录上的 purge 仍然生效（junk 被清）",
        !File.Exists(Path.Combine(dstSave53, "junk53.tmp")), "");

    // 53.3 删除被占用的 zip：抛错且记录保留
    Store.HomeDirOverride = Path.Combine(temp, "vault53c");
    Store.Init();
    string occDir53 = Path.Combine(temp, "g53occ", "saves");
    Directory.CreateDirectory(occDir53);
    File.WriteAllText(Path.Combine(occDir53, "s.dat"), "OCC");
    var gOcc53 = new GameEntry { NameCN = "占用游戏53" };
    gOcc53.SavePaths.Add(new SavePathEntry { Path = occDir53 });
    ProfileService.CreatePlayer(gOcc53, "甲");
    var recOcc53 = gOcc53.Backups[0];
    bool delThrew53 = false;
    using (var lock53 = new FileStream(recOcc53.File, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        try { BackupService.Delete(gOcc53, recOcc53); }
        catch (InvalidOperationException) { delThrew53 = true; }
    }
    Check("批次A: zip 被占用时 Delete 抛 InvalidOperationException", delThrew53, "");
    Check("批次A: 删除失败记录保留、文件仍在",
        gOcc53.Backups.Contains(recOcc53) && File.Exists(recOcc53.File), "");

    // 53.4 非热备份打包瞬间失败：文件必须剔除出 manifest.Files（清单=实包，防新旧混合档）
    Store.HomeDirOverride = Path.Combine(temp, "vault53d");
    Store.Init();
    string lockDir53 = Path.Combine(temp, "g53lock", "saves");
    Directory.CreateDirectory(lockDir53);
    File.WriteAllText(Path.Combine(lockDir53, "good.dat"), "GOOD");
    string lf53 = Path.Combine(lockDir53, "locked.dat");
    File.WriteAllText(lf53, "LOCKED");
    var gLock53 = new GameEntry { NameCN = "打包占用游戏53", ActiveProfile = "甲" };
    gLock53.SavePaths.Add(new SavePathEntry { Path = lockDir53 });
    BackupRecord? recLock53;
    using (var fsLock53 = new FileStream(lf53, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        recLock53 = BackupService.Create(gLock53, "非热占用", hot: false, out int skipped53, profile: "甲");
        Check("批次A: 非热备份打包失败计入 skippedFiles", skipped53 >= 1, $"skipped={skipped53}");
    }
    var man53 = BackupService.ReadManifest(recLock53!.File)!;
    Check("批次A: 打包失败文件已从 manifest.Files 剔除（只剩 good）",
        man53.Files.Count == 1 && man53.Files[0].N.EndsWith("good.dat"),
        string.Join(",", man53.Files.Select(f => f.N)));
    using (var z53 = System.IO.Compression.ZipFile.OpenRead(recLock53.File))
    {
        var contentEntries53 = z53.Entries.Where(e => e.FullName != "manifest.json").ToList();
        Check("批次A: zip 实包条目与清单一致（无半截 locked 条目）",
            contentEntries53.Count == 1 && contentEntries53[0].FullName.EndsWith("good.dat"),
            string.Join(",", contentEntries53.Select(e => e.FullName)));
    }
    Store.HomeDirOverride = null;
}

// ── 54. 批次B：AppId串档闸、412 HEAD认领/改名、PROPFIND 200段、下载损坏校验、删除部分失败保留、state隔离、DPAPI熵 ──
{
    // 54.0 DPAPI 熵：往返一致且密文非明文
    string dp54 = CloudSecret.Protect("pw-甲54");
    Check("批次B: CloudSecret 带熵加密往返一致", dp54 != "pw-甲54" && CloudSecret.Unprotect(dp54) == "pw-甲54", "");

    // 身份缓存测试会触发孤儿清扫落盘，必须先切沙箱
    Store.HomeDirOverride = Path.Combine(temp, "vault54");
    Store.Init();
    CloudSyncService.LoadState();
    Store.Settings.CloudUrl = $"http://127.0.0.1:0/";
    Store.Settings.CloudUser = "u";
    Store.Settings.CloudSecret = CloudSecret.Protect("p");

    // 54.1 串档闸：同一云端目录先登记 AppId=100，之后 AppId=200 撞名上报不得覆盖/污染别名
    var docId54 = new CloudIndex.Doc();
    CloudIndex.UpsertIdentity(docId54, "MagicDir54", 100, new[] { "magicdir54" });
    CloudIndex.UpsertIdentity(docId54, "MagicDir54", 200, new[] { "intruder54" });
    Check("批次B: UpsertIdentity AppId 冲突时不覆盖、不并入侵者别名",
        docId54.Games["MagicDir54"].A == 100
        && !docId54.Games["MagicDir54"].Al!.Contains("intruder54"), "");
    CloudSyncService.UpdateIdentityCache(docId54);
    var gSteamA54 = new GameEntry { NameEN = "MagicDir54", AppId = 100 };
    var gSteamB54 = new GameEntry { NameEN = "MagicDir54", AppId = 200 };
    var gNonSteam54 = new GameEntry { NameEN = "MagicDir54", AppId = 0 };
    Check("批次B: 真主 AppId 相同 → 认领云端目录",
        CloudSyncService.CloudDirsFor(gSteamA54).Contains("MagicDir54"), "");
    Check("批次B: 别名撞名但 AppId 冲突 → 不认领（防云串档）",
        !CloudSyncService.CloudDirsFor(gSteamB54).Contains("MagicDir54"), "");
    Check("批次B: 非 Steam 游戏（AppId=0）撞名仍按别名认领（保持旧行为）",
        CloudSyncService.CloudDirsFor(gNonSteam54).Contains("MagicDir54"), "");

    // ── 假 WebDAV 服务器：MKCOL/PUT(412 剧本)/HEAD/GET(坏zip)/DELETE(乙首败后成)/PROPFIND(双propstat) ──
    var putPaths54 = new List<string>();
    int yiFailLeft54 = 4;   // 乙段持续 500（扛过 SendRetryAsync 的 3 次重试），第二次删除才恢复
    var l54 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    l54.Start();
    int port54 = ((System.Net.IPEndPoint)l54.LocalEndpoint).Port;
    async Task Serve54(System.Net.Sockets.TcpClient c54)
    {
        try
        {
            using var st54 = c54.GetStream();
            // 字节级读头：不能用 StreamReader——它会把同包到达的请求体吞进内部缓冲，
            // 之后按 Content-Length 直接读流会永久阻塞
            var headBytes54 = new List<byte>(512);
            int last4_54 = 0; // 按位记录最近 4 字节是否依次为 \r \n \r \n
            var one54 = new byte[1];
            while (true)
            {
                if (await st54.ReadAsync(one54.AsMemory()) <= 0) return;
                int rb54 = one54[0];
                headBytes54.Add((byte)rb54);
                last4_54 = rb54 switch
                {
                    '\r' => (last4_54 is 0 or 2) ? last4_54 + 1 : 0,
                    '\n' => (last4_54 is 1 or 3) ? last4_54 + 1 : 0,
                    _ => 0,
                };
                if (last4_54 == 4) break;
            }
            string headText54 = Encoding.ASCII.GetString(headBytes54.ToArray());
            var headLines54 = headText54.Split("\r\n");
            var rp54 = headLines54[0].Split(' ');
            string method54 = rp54[0], rawPath54 = rp54[1];
            int len54 = 0;
            for (int li54 = 1; li54 < headLines54.Length; li54++)
                if (headLines54[li54].StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(headLines54[li54].Substring(15).Trim(), out len54);
            var bodyBuf54 = new byte[len54];
            int got54 = 0;
            while (got54 < len54)
            {
                int n54 = await st54.ReadAsync(bodyBuf54.AsMemory(got54));
                if (n54 <= 0) break;
                got54 += n54;
            }
            string path54 = Uri.UnescapeDataString(rawPath54.Split('?')[0]);
            byte[] resp54;
            if (method54 == "MKCOL")
                resp54 = Encoding.ASCII.GetBytes("HTTP/1.1 201 Created\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            else if (method54 == "PUT" && path54.EndsWith("claim54.zip", StringComparison.OrdinalIgnoreCase))
            {
                lock (putPaths54) putPaths54.Add(path54);
                resp54 = Encoding.ASCII.GetBytes("HTTP/1.1 412 Precondition Failed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            }
            else if (method54 == "PUT" && path54.Contains("diff2_54"))
            {
                lock (putPaths54) putPaths54.Add(path54);
                bool hashName = path54.Contains("_" + Environment.MachineName.ToLowerInvariant() + "_", StringComparison.OrdinalIgnoreCase)
                    && System.Text.RegularExpressions.Regex.IsMatch(path54, @"_[0-9a-f]{8}\.zip$");
                resp54 = Encoding.ASCII.GetBytes(hashName
                    ? "HTTP/1.1 201 Created\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : "HTTP/1.1 412 Precondition Failed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            }
            else if (method54 == "PUT" && path54.Contains("diff_54"))
            {
                lock (putPaths54) putPaths54.Add(path54);
                bool machineName = path54.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
                resp54 = Encoding.ASCII.GetBytes(machineName
                    ? "HTTP/1.1 201 Created\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : "HTTP/1.1 412 Precondition Failed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            }
            else if (method54 == "PUT")
                resp54 = Encoding.ASCII.GetBytes("HTTP/1.1 201 Created\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            else if (method54 == "HEAD" && path54.EndsWith("claim54.zip", StringComparison.OrdinalIgnoreCase))
                resp54 = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\n");
            else if (method54 == "HEAD" && path54.EndsWith("diff_54.zip", StringComparison.OrdinalIgnoreCase))
                resp54 = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 999\r\nConnection: close\r\n\r\n");
            else if (method54 == "HEAD")
                resp54 = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            else if (method54 == "GET" && path54.EndsWith("bad54.zip", StringComparison.OrdinalIgnoreCase))
                resp54 = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 9\r\nConnection: close\r\n\r\nNOT-A-ZIP");
            else if (method54 == "DELETE")
            {
                bool yi = path54.Contains("/%E4%B9%99/") || path54.Contains("/乙/");
                bool failNow;
                lock (putPaths54) { failNow = yi && yiFailLeft54 > 0; if (yi) yiFailLeft54--; }
                resp54 = Encoding.ASCII.GetBytes(failNow
                    ? "HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            }
            else if (method54 == "PROPFIND" && path54.Contains("T54List"))
            {
                string xml54 = "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                    + "<D:multistatus xmlns:D=\"DAV:\">"
                    + "<D:response><D:href>/GameSaveVault/T54List/%E7%94%B2/</D:href>"
                    + "<D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop>"
                    + "<D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>"
                    + "<D:response><D:href>/GameSaveVault/T54List/%E7%94%B2/one54.zip</D:href>"
                    + "<D:propstat><D:prop><D:resourcetype/><D:getcontentlength>999</D:getcontentlength>"
                    + "<D:getlastmodified>Wed, 01 Jan 2020 00:00:00 GMT</D:getlastmodified></D:prop>"
                    + "<D:status>HTTP/1.1 404 Not Found</D:status></D:propstat>"
                    + "<D:propstat><D:prop><D:resourcetype/><D:getcontentlength>123</D:getcontentlength>"
                    + "<D:getlastmodified>Wed, 21 Oct 2015 07:28:00 GMT</D:getlastmodified></D:prop>"
                    + "<D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>"
                    + "</D:multistatus>";
                var xb54 = Encoding.UTF8.GetBytes(xml54);
                resp54 = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 207 Multi-Status\r\nContent-Type: application/xml; charset=utf-8\r\nContent-Length: {xb54.Length}\r\nConnection: close\r\n\r\n")
                    .Concat(xb54).ToArray();
            }
            else
                resp54 = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await st54.WriteAsync(resp54);
            await st54.FlushAsync();
        }
        catch { }
        finally { try { c54.Dispose(); } catch { } }
    }
    var srvTask54 = Task.Run(async () =>
    {
        while (true)
        {
            var cc54 = await l54.AcceptTcpClientAsync();
            _ = Task.Run(() => Serve54(cc54));
        }
    });

    Store.HomeDirOverride = Path.Combine(temp, "vault54");
    Store.Init();
    Store.Settings.CloudUrl = $"http://127.0.0.1:{port54}/";
    Store.Settings.CloudUser = "u";
    Store.Settings.CloudSecret = CloudSecret.Protect("p");
    Store.SaveSettings();

    using (var client54 = new WebDavClient($"http://127.0.0.1:{port54}/", "u", "p", "GameSaveVault"))
    {
        // 54.2 412 同内容：HEAD 字节数一致 → 认领原名，只 PUT 一次，不造重复副本
        string fClaim54 = Path.Combine(temp, "claim54.zip");
        File.WriteAllBytes(fClaim54, new byte[100]);
        int putsBefore54;
        lock (putPaths54) putsBefore54 = putPaths54.Count;
        string retClaim54 = await client54.UploadAsync(fClaim54, "T54/甲/claim54.zip");
        int putsAfterClaim54;
        lock (putPaths54) putsAfterClaim54 = putPaths54.Count;
        Check("批次B: 412 且 HEAD 同字节 → 认领原路径、零改名重传",
            retClaim54 == "T54/甲/claim54.zip" && putsAfterClaim54 == putsBefore54 + 1, retClaim54);

        // 54.3 412 真撞名：改名机器名后缀后传上
        string fDiff54 = Path.Combine(temp, "diff_54.zip");
        File.WriteAllBytes(fDiff54, new byte[200]);
        int beforeDiff54;
        lock (putPaths54) beforeDiff54 = putPaths54.Count;
        string retDiff54 = await client54.UploadAsync(fDiff54, "T54/甲/diff_54.zip");
        int afterDiff54;
        lock (putPaths54) afterDiff54 = putPaths54.Count;
        Check("批次B: 412 字节不同 → 加机器名后缀重传成功（2 次 PUT）",
            afterDiff54 == beforeDiff54 + 2
            && retDiff54 != "T54/甲/diff_54.zip"
            && retDiff54.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            && retDiff54.EndsWith(".zip"), retDiff54);

        // 54.4 同名机器也撞：第二轮追加稳定短哈希（3 次 PUT）
        string fDiff2_54 = Path.Combine(temp, "diff2_54.zip");
        File.WriteAllBytes(fDiff2_54, new byte[300]);
        int beforeDiff2_54;
        lock (putPaths54) beforeDiff2_54 = putPaths54.Count;
        string retDiff2_54 = await client54.UploadAsync(fDiff2_54, "T54/甲/diff2_54.zip");
        int afterDiff2_54;
        lock (putPaths54) afterDiff2_54 = putPaths54.Count;
        Check("批次B: 机器名后缀仍 412 → 追加 8 位短哈希后传上（3 次 PUT）",
            afterDiff2_54 == beforeDiff2_54 + 3
            && System.Text.RegularExpressions.Regex.IsMatch(retDiff2_54,
                Regex.Escape(Environment.MachineName) + @"_[0-9a-f]{8}\.zip$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase), retDiff2_54);

        // 54.5 PROPFIND：404 propstat 在前也必须取 200 段的 size/时间
        var list54 = await client54.ListAsync("T54List/甲");
        Check("批次B: PROPFIND 选 status=200 的 propstat（size=123、时间取 2015 段）",
            list54.Count == 1 && list54[0].Name == "one54.zip" && list54[0].Size == 123
            && list54[0].Modified.Year == 2015 && list54[0].Modified.Kind == DateTimeKind.Utc,
            list54.Count == 0 ? "空" : $"{list54[0].Size}/{list54[0].Modified:O}");

        // 54.6 下载到半截/非 zip：字节虽完整但中央目录校验失败 → 抛错、目标与 .part 都不留
        string dlBad54 = Path.Combine(temp, "bad54_dl.zip");
        bool badThrew54 = false;
        try { await client54.DownloadAsync("T54List/甲/bad54.zip", dlBad54); }
        catch (InvalidOperationException) { badThrew54 = true; }
        Check("批次B: 非 zip 下载抛错且目标文件/.part 均已清理",
            badThrew54 && !File.Exists(dlBad54) && !File.Exists(dlBad54 + ".part"), "");

        // 54.7 删除部分失败：甲成乙败 → 返回 1、墓碑保留、登记不置 Deleted；再删一次全成。
        // 注意：生产链路本地 zip 与云端同名，本地文件名必须就是 a54.zip（段反查按文件名匹配）
        var gDel54 = new GameEntry { NameCN = "删段54" };
        string seg54 = BackupService.SafeName(gDel54.DisplayName);
        string localDel54 = Path.Combine(temp, "a54.zip");
        File.WriteAllBytes(localDel54, new byte[10]);
        CloudSyncService.MarkRemoteKnown($"{seg54}/甲/a54.zip");
        CloudSyncService.MarkRemoteKnown($"{seg54}/乙/a54.zip");
        CloudSyncService.MarkAdoptedFromCloud(localDel54, $"{seg54}/甲/a54.zip");
        CloudSyncService.OnLocalBackupsDeleted(gDel54, new[] { localDel54 });
        int del1_54 = await CloudSyncService.DeleteRemoteCopiesAsync(gDel54, localDel54);
        Check("批次B: 两段删一成一败 → 返回成功数 1", del1_54 == 1, $"ok={del1_54}");
        Check("批次B: 部分失败时登记不置 Deleted、段墓碑保留（防副本复活/可重试）",
            !CloudSyncService.IsRemoteDeleted(localDel54)
            && CloudSyncService.IsDismissed(seg54, "a54.zip"), "");
        int del2_54 = await CloudSyncService.DeleteRemoteCopiesAsync(gDel54, localDel54);
        CloudSyncService.WaitStateSaved();
        Check("批次B: 重试后两段全删 → 返回 2、Deleted=true、墓碑清除",
            del2_54 == 2 && CloudSyncService.IsRemoteDeleted(localDel54)
            && !CloudSyncService.IsDismissed(seg54, "a54.zip"), $"ok={del2_54}");
    }
    l54.Stop();
    Store.HomeDirOverride = null;

    // 54.8 cloud-state.json 损坏：隔离 .corrupt、空表启动、不阻断后续重新落盘
    Store.HomeDirOverride = Path.Combine(temp, "vault54c");
    Store.Init();
    File.WriteAllText(Store.CloudStateFile, "{ broken json !!! ");
    CloudSyncService.LoadState();
    bool quarantined54 = Directory.Exists(Store.BaseDir)
        && Directory.GetFiles(Store.BaseDir, "cloud-state.json.corrupt.*").Length == 1;
    Check("批次B: 损坏登记表被隔离为 .corrupt 且内存按空表启动",
        quarantined54 && !CloudSyncService.IsRemoteKnown("any/zip54"), "");
    CloudSyncService.MarkRemoteKnown("reborn54/甲/x.zip");
    CloudSyncService.WaitStateSaved();
    Check("批次B: 隔离后登记功能恢复（新表正常落盘可读）",
        File.Exists(Store.CloudStateFile)
        && File.ReadAllText(Store.CloudStateFile).Contains("reborn54", StringComparison.Ordinal), "");
    Store.HomeDirOverride = null;
}

// ===== 批次 C：生命周期/资源/UI 加固 =====
{
    string? lastGateErr55 = null;

    // 55.1 游戏移除后互斥闸回收：移除后再备份不报错（闸按需重建）、同游戏串行仍可用
    Store.HomeDirOverride = Path.Combine(temp, "vault55");
    Store.Init();
    string saveDir55 = Path.Combine(temp, "gate55_saves");
    Directory.CreateDirectory(saveDir55);
    File.WriteAllText(Path.Combine(saveDir55, "a.txt"), "G1");
    var gGate55 = new GameEntry { NameEN = "Gate55" };
    gGate55.SavePaths.Add(new SavePathEntry { Path = saveDir55 });
    var bg1_55 = BackupService.Create(gGate55, "闸前备份");
    BackupService.TryRemoveGameGate(gGate55.Id);
    File.WriteAllText(Path.Combine(saveDir55, "a.txt"), "G22");
    bool gateRecreated55 = false;
    try
    {
        var bg2_55 = BackupService.Create(gGate55, "闸回收后备份");
        gateRecreated55 = bg2_55 != null && File.Exists(bg2_55.File)
            && bg1_55.File != bg2_55.File;
    }
    catch (Exception ex55) { lastGateErr55 = ex55.Message; }
    Check("批次C: 移除游戏后互斥闸可回收，重建后同游戏备份照常串行工作",
        gateRecreated55, lastGateErr55 ?? "");

    // 55.2 CloudSyncService 优雅停机：Start→Stop 在有界时间内结束且可幂等重复调用
    Store.Settings.CloudUrl = "http://127.0.0.1:1/";   // 无游戏 CloudAuto，启动期不发请求
    Store.Settings.CloudUser = "u";
    Store.Settings.CloudSecret = CloudSecret.Protect("p");
    Store.SaveSettings();
    CloudSyncService.Start();
    var stop1_55 = CloudSyncService.StopAsync();
    bool stop1Ok55 = stop1_55.Wait(TimeSpan.FromSeconds(10));
    var stop2_55 = CloudSyncService.StopAsync();
    bool stop2Ok55 = stop2_55.Wait(TimeSpan.FromSeconds(10));
    Check("批次C: CloudSyncService.StopAsync 有界返回且重复调用幂等不卡",
        stop1Ok55 && stop2Ok55, $"1={stop1Ok55},2={stop2Ok55}");

    Store.HomeDirOverride = null;

    // 55.3 源码守卫：本轮生命周期/资源加固的关键实现必须在位（防回归被静默回退）
    string repoRoot55 = AppContext.BaseDirectory;
    for (int i55 = 0; i55 < 8 && !File.Exists(Path.Combine(repoRoot55, "GameSaveManager.sln")); i55++)
        repoRoot55 = Path.GetDirectoryName(repoRoot55) ?? "";
    string Src55(string rel) => File.ReadAllText(Path.Combine(repoRoot55, "src", "GameSaveManager", rel));
    string appCs55 = Src55("App.xaml.cs");
    Check("批次C: 单实例命名 Mutex + 激活已有窗口事件在位",
        appCs55.Contains("SingleInstanceMutexName", StringComparison.Ordinal)
        && appCs55.Contains("EventWaitHandle.TryOpenExisting", StringComparison.Ordinal)
        && appCs55.Contains("StartActivateListener", StringComparison.Ordinal), "");
    Check("批次C: DispatcherUnhandledException 致命错误不再吞掉、非 UI 异常落盘",
        appCs55.Contains("args.Handled = false", StringComparison.Ordinal)
        && appCs55.Contains("AppDomain.CurrentDomain.UnhandledException", StringComparison.Ordinal)
        && appCs55.Contains("UnobservedTaskException", StringComparison.Ordinal), "");
    string davCs55 = Src55(Path.Combine("Services", "WebDavClient.cs"));
    Check("批次C: WebDavClient 共享静态 HttpClient 且实例 Dispose 不再释放共享连接",
        davCs55.Contains("static readonly HttpClient", StringComparison.Ordinal)
        && davCs55.Contains("public void Dispose()", StringComparison.Ordinal), "");
    string backupCs55 = Src55(Path.Combine("Services", "BackupService.cs"));
    Check("批次C: 还原落盘前 TOCTOU 二次运行检测在位",
        backupCs55.Contains("TOCTOU 复查", StringComparison.Ordinal), "");
    string cloudCs55 = Src55(Path.Combine("Services", "CloudSyncService.cs"));
    Check("批次C: 云同步停机令牌/worker 引用与有界等待在位",
        cloudCs55.Contains("public static async Task StopAsync()", StringComparison.Ordinal)
        && cloudCs55.Contains("_channel?.Writer.TryComplete()", StringComparison.Ordinal), "");
    string winCs55 = Src55("MainWindow.xaml.cs");
    Check("批次C: 退出时停定时器/取消枚举/云同步有界停机，重 IO 操作已后台化",
        winCs55.Contains("CloudSyncService.StopAsync", StringComparison.Ordinal)
        && winCs55.Contains("Task.Run(() => ProfileService.RenamePlayer", StringComparison.Ordinal)
        && winCs55.Contains("BackupService.TryRemoveGameGate", StringComparison.Ordinal), "");

    // 55.4 测试连接必须携带 Basic 认证头（回归：共享 HttpClient 后 TestAsync 漏挂 Authorization → 必 401）
    var l56 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    l56.Start();
    int port56 = ((System.Net.IPEndPoint)l56.LocalEndpoint).Port;
    string expectAuth56 = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("u56:p56"));
    _ = Task.Run(async () =>
    {
        for (int n56 = 0; n56 < 6; n56++)
        {
            System.Net.Sockets.TcpClient cc56;
            try { cc56 = await l56.AcceptTcpClientAsync(); }
            catch { return; }
            using (cc56)
            using (var st56 = cc56.GetStream())
            {
            var head56 = new List<byte>();
            var tail56 = new Queue<byte>();
            bool headerDone56 = false;
            var one56 = new byte[1];
            while (!headerDone56)
            {
                int read56 = await st56.ReadAsync(one56.AsMemory());
                if (read56 <= 0) break;
                byte b56 = one56[0];
                head56.Add(b56);
                tail56.Enqueue(b56);
                if (tail56.Count > 4) tail56.Dequeue();
                if (tail56.Count == 4 && tail56.SequenceEqual(new byte[] { 13, 10, 13, 10 }))
                    headerDone56 = true;
            }
            string headerText56 = Encoding.ASCII.GetString(head56.ToArray());
            // 读完请求体再响应：未读 body 直接 close 可能被 RST，客户端会误判连接失败并重试
            int contentLen56 = 0;
            foreach (var hl56 in headerText56.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                if (hl56.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(hl56[(hl56.IndexOf(':') + 1)..].Trim(), out contentLen56);
            int bodyRead56 = 0;
            var bodyBuf56 = new byte[4096];
            while (bodyRead56 < contentLen56)
            {
                int r56 = await st56.ReadAsync(bodyBuf56.AsMemory(0,
                    Math.Min(bodyBuf56.Length, contentLen56 - bodyRead56)));
                if (r56 <= 0) break;
                bodyRead56 += r56;
            }
            bool authed56 = ("\r\n" + headerText56).Contains("\r\nAuthorization: " + expectAuth56 + "\r\n",
                StringComparison.OrdinalIgnoreCase);
            string resp56 = authed56
                ? "HTTP/1.1 207 Multi-Status\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                : "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            var rb56 = Encoding.ASCII.GetBytes(resp56);
            await st56.WriteAsync(rb56.AsMemory());
            await st56.FlushAsync();
            }
        }
        try { l56.Stop(); } catch { }
    });

    var clientOk56 = new WebDavClient($"http://127.0.0.1:{port56}/dav", "u56", "p56", "GameSaveVault");
    var (okConn56, msgConn56) = await clientOk56.TestAsync();
    Check("批次C: 测试连接携带正确 Basic 认证 → 207 连接成功（不再误报失败）",
        okConn56 && msgConn56 == "连接成功", msgConn56);
    var clientBad56 = new WebDavClient($"http://127.0.0.1:{port56}/dav", "u56", "wrong56", "GameSaveVault");
    var (okBad56, msgBad56) = await clientBad56.TestAsync();
    Check("批次C: 测试连接密码错误 → 401 认证失败友好提示",
        !okBad56 && msgBad56.StartsWith("认证失败", StringComparison.Ordinal), msgBad56);
}

// 55.5 关于页发布时间：开发构建无 BuildDate 元数据时回退为主程序文件时间，必须可解析
{
    var buildText = BuildInfo.PublishTimeText;
    var okBuildFmt = DateTime.TryParseExact(buildText, "yyyy-MM-dd HH:mm",
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out _);
    Check("关于: 发布时间形如 yyyy-MM-dd HH:mm（元数据或文件时间回退）", okBuildFmt, buildText);
}

// ── 56. 还原路径层级守卫（卡赞 SaveGames\SaveGames 自嵌套事故回归）──
{
    Store.HomeDirOverride = Path.Combine(temp, "vault56");
    Store.Init();

    // 56.1 备份根比当前配置高一层（...\Saved → ...\Saved\SaveGames）：还原必须在写盘前中止
    string hiSaved56 = Path.Combine(temp, "khazan56", "Saved");
    string hiSg56 = Path.Combine(hiSaved56, "SaveGames");
    Directory.CreateDirectory(hiSg56);
    File.WriteAllText(Path.Combine(hiSg56, "save00.sav"), "SAVE-V1");
    Directory.CreateDirectory(Path.Combine(hiSaved56, "Config"));
    File.WriteAllText(Path.Combine(hiSaved56, "Config", "Engine.ini"), "[cfg]");
    Directory.CreateDirectory(Path.Combine(hiSaved56, "Logs"));
    File.WriteAllText(Path.Combine(hiSaved56, "Logs", "game.log"), "log");
    var gHi56 = new GameEntry { NameEN = "KhazanLevel56" };
    gHi56.SavePaths.Add(new SavePathEntry { Path = hiSaved56 });
    var bakHi56 = BackupService.Create(gHi56, "高一层备份");
    gHi56.SavePaths.Clear();   // 用户把存档位置从 Saved 改成其下的 SaveGames（事故操作）
    gHi56.SavePaths.Add(new SavePathEntry { Path = hiSg56 });
    File.WriteAllText(Path.Combine(hiSg56, "marker_untouched.ini"), "keep");
    bool hiBlocked56 = false; string hiMsg56 = "";
    try
    {
        BackupService.Restore(gHi56, bakHi56, purgeNewFiles: true,
            targetPaths: ProfileService.EffectivePaths(gHi56));
    }
    catch (InvalidOperationException ex) { hiBlocked56 = ex.Message.Contains("层级"); hiMsg56 = ex.Message; }
    Check("层级守卫: 备份目录比当前配置高一层时还原被中止（卡赞事故复现）",
        hiBlocked56, hiMsg56);
    Check("层级守卫: 中止后未产生 SaveGames 嵌套且 live 原文件零改写",
        !Directory.Exists(Path.Combine(hiSg56, "SaveGames"))
        && File.ReadAllText(Path.Combine(hiSg56, "marker_untouched.ini")) == "keep"
        && File.ReadAllText(Path.Combine(hiSg56, "save00.sav")) == "SAVE-V1", "");

    // 56.2 末级目录名相同、不同目录树（跨机云档/同层级迁移）：配对成功、正常落盘、无嵌套
    string srcSg56 = Path.Combine(temp, "pcA56", "SaveGames");
    string dstSg56 = Path.Combine(temp, "pcB56", "SaveGames");
    Directory.CreateDirectory(srcSg56);
    Directory.CreateDirectory(dstSg56);
    File.WriteAllText(Path.Combine(srcSg56, "save.dat"), "CROSS-V1");
    var gCross56 = new GameEntry { NameEN = "CrossLevel56" };
    gCross56.SavePaths.Add(new SavePathEntry { Path = srcSg56 });
    var bakCross56 = BackupService.Create(gCross56, "源机包");
    BackupService.Restore(gCross56, bakCross56, purgeNewFiles: true,
        targetPaths: new List<string> { dstSg56 });
    Check("层级守卫: 末级同名跨目录（跨机同层级）还原成功",
        File.Exists(Path.Combine(dstSg56, "save.dat"))
        && File.ReadAllText(Path.Combine(dstSg56, "save.dat")) == "CROSS-V1", "");
    Check("层级守卫: 跨目录落盘不含同名自嵌套子目录",
        !Directory.Exists(Path.Combine(dstSg56, "SaveGames")), "");

    // 56.2b 末级名不同但互不父子（跨机目录名差异，云建档 pc_saves→pc_saves2）：顺序配对仍容错
    string srcFlat56 = Path.Combine(temp, "pcC56", "pc_saves");
    string dstFlat56 = Path.Combine(temp, "pcD56", "pc_saves2");
    Directory.CreateDirectory(srcFlat56);
    Directory.CreateDirectory(dstFlat56);
    File.WriteAllText(Path.Combine(srcFlat56, "s.txt"), "FLAT-V1");
    var gFlat56 = new GameEntry { NameEN = "FlatLevel56" };
    gFlat56.SavePaths.Add(new SavePathEntry { Path = srcFlat56 });
    var bakFlat56 = BackupService.Create(gFlat56, "异名包");
    BackupService.Restore(gFlat56, bakFlat56, purgeNewFiles: true,
        targetPaths: new List<string> { dstFlat56 });
    Check("层级守卫: 末级名不同但非父子（跨机异名）仍可还原",
        File.Exists(Path.Combine(dstFlat56, "s.txt"))
        && File.ReadAllText(Path.Combine(dstFlat56, "s.txt")) == "FLAT-V1", "");

    // 56.3 污染包（包内自带与根同名子目录）即便原路径精确还原也必须在写任何文件前拦截
    string pollRoot56 = Path.Combine(temp, "poll56", "SaveGames");
    Directory.CreateDirectory(Path.Combine(pollRoot56, "SaveGames"));
    File.WriteAllText(Path.Combine(pollRoot56, "top.sav"), "TOP");
    File.WriteAllText(Path.Combine(pollRoot56, "SaveGames", "inner.sav"), "INNER");
    var gPoll56 = new GameEntry { NameEN = "Polluted56" };
    gPoll56.SavePaths.Add(new SavePathEntry { Path = pollRoot56 });
    var bakPoll56 = BackupService.Create(gPoll56, "污染包");
    Directory.Delete(pollRoot56, true);
    bool pollBlocked56 = false; string pollMsg56 = "";
    try { BackupService.Restore(gPoll56, bakPoll56); }
    catch (InvalidOperationException ex)
    {
        pollBlocked56 = ex.Message.Contains("自嵌套");
        pollMsg56 = ex.Message;
    }
    Check("层级守卫: 含同名子目录的污染包原路径还原也被拦截", pollBlocked56, pollMsg56);
    Check("层级守卫: 污染包拦截后零落盘（目录未被重建）",
        !Directory.Exists(pollRoot56), "");

    // 56.4 存档位置层级提示：同路径/异树同层级不警告，深一层或浅一层才警告
    string warnRoot56 = Path.Combine(temp, "warn56", "Saved");
    Directory.CreateDirectory(warnRoot56);
    File.WriteAllText(Path.Combine(warnRoot56, "w.sav"), "W");
    var gWarn56 = new GameEntry { NameEN = "WarnLevel56" };
    gWarn56.SavePaths.Add(new SavePathEntry { Path = warnRoot56 });
    BackupService.Create(gWarn56, "唯一包");
    Check("层级提示: 新位置与备份同路径不警告",
        BackupService.CheckPathLevelConflict(gWarn56, warnRoot56) == null, "");
    Check("层级提示: 新位置深一层给出警告",
        BackupService.CheckPathLevelConflict(gWarn56, Path.Combine(warnRoot56, "SaveGames"))?.Contains("层级") == true, "");
    Check("层级提示: 新位置浅一层给出警告",
        BackupService.CheckPathLevelConflict(gWarn56, Path.Combine(temp, "warn56"))?.Contains("层级") == true, "");
    Check("层级提示: 不同目录树的同层级路径不警告",
        BackupService.CheckPathLevelConflict(gWarn56, Path.Combine(temp, "other56", "Saved")) == null, "");

    // ── 57. 心目中的档指针：还旧档落 same+restore+次要标记；内部 Create 不动指针；UI/XAML/轮询提速接线守卫 ──
    {
        string ls57 = Path.Combine(temp, "dual_fact57");
        Directory.CreateDirectory(ls57);
        File.WriteAllText(Path.Combine(ls57, "save.dat"), "OLD-v1");
        File.SetLastWriteTime(Path.Combine(ls57, "save.dat"), DateTime.Now.AddMinutes(-30));
        var g57 = new GameEntry { NameEN = "DualFact57" };
        g57.SavePaths.Add(new SavePathEntry { Path = ls57 });
        var bakA57 = BackupService.Create(g57, "旧进度A");
        File.WriteAllText(Path.Combine(ls57, "save.dat"), "NEW-v2-longer");
        File.SetLastWriteTime(Path.Combine(ls57, "save.dat"), DateTime.Now.AddMinutes(-20));
        var bakB57 = BackupService.Create(g57, "新进度B");
        string prof57 = g57.ActiveProfile;
        // 还原到旧档 A：指针移到 A（why=restore），live==A → same，次要标记数据齐备
        BackupService.Restore(g57, bakA57);
        var stOld57 = BackupService.GetLiveState(g57, prof57);
        Check("心目中的档: 还旧档 → same + 指针=A + why=restore + MatchedAt=A",
            stOld57.Kind == "same" && stOld57.FavoriteWhy == "restore"
            && stOld57.MatchedAt == bakA57.SavedAt
            && g57.SwitchAnchors[prof57] == bakA57.FileJson,
            $"{stOld57.Kind}/{stOld57.FavoriteWhy}/{stOld57.MatchedAt:HH:mm:ss}/{bakA57.SavedAt:HH:mm:ss}");
        // 无备份的路人玩家：nobackup（结构兼容位 RestoredAt 保持 default，不炸）
        Check("心目中的档: 无备份玩家 → nobackup 且兼容位 RestoredAt=default",
            BackupService.GetLiveState(g57, "路人57") is var stNone57
            && stNone57.Kind == "nobackup" && stNone57.RestoredAt == default, "");
        // 还原到最新档 B：same + restore + MatchedAt=B
        BackupService.Restore(g57, bakB57);
        var stNew57 = BackupService.GetLiveState(g57, prof57);
        Check("心目中的档: 还最新档 → same + restore + MatchedAt=新备份存档时刻",
            stNew57.Kind == "same" && stNew57.FavoriteWhy == "restore"
            && stNew57.MatchedAt == bakB57.SavedAt, stNew57.Kind);
        // 内部 Create（热快照/安全快照口径）不移动指针：新包 C 后指针仍停 B（restore），筹码如实报 changed
        File.WriteAllText(Path.Combine(ls57, "save.dat"), "NEW-v3-even-longer-content");
        File.SetLastWriteTime(Path.Combine(ls57, "save.dat"), DateTime.Now.AddMinutes(-10));
        BackupService.Create(g57, "再玩后的备份C");
        Check("心目中的档: 内部 Create 后指针不移动 → changed 且 why 仍为 restore",
            BackupService.GetLiveState(g57, prof57) is var stC57
            && stC57.Kind == "changed" && stC57.FavoriteWhy == "restore"
            && g57.SwitchAnchors[prof57] == bakB57.FileJson, "");

        // 接线守卫：VM 暴露次要标记属性
        string srcRoot57 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "GameSaveManager"));
        string gameEntryCs57 = File.ReadAllText(Path.Combine(srcRoot57, "Models", "GameEntry.cs"));
        Check("双事实: ProfileSlotVm 暴露 RestoredTagText/SetRestoredTag",
            gameEntryCs57.Contains("public string RestoredTagText")
            && gameEntryCs57.Contains("public void SetRestoredTag"), "");
        string mainCs57 = File.ReadAllText(Path.Combine(srcRoot57, "MainWindow.xaml.cs"));
        Check("心目中的档: same+restore 分支按 why 落「↩ 已还原到」标记",
            mainCs57.Contains("st.FavoriteWhy") && mainCs57.Contains("↩ 已还原到"), "");
        string xaml57 = File.ReadAllText(Path.Combine(srcRoot57, "MainWindow.xaml"));
        Check("双事实: XAML 绑定 RestoredTagText 且空值折叠",
            xaml57.Contains("{Binding RestoredTagText}")
            && xaml57.Contains("Visibility=\"{Binding RestoredTagText, Converter={StaticResource BlankToCollapsed}}\""), "");
        // 玩家分区头部：标题与两段说明同行——短解释 + 窄窗口省略号兜底，绝不互相压住
        // 注意必须锚定标题元素本身（同名文字在上方注释里也出现过一次）
        int hdrStart57 = xaml57.IndexOf(">玩家存档与备份</TextBlock>");
        string hdr57 = xaml57.Substring(hdrStart57,
            xaml57.IndexOf("</Grid>", hdrStart57) - hdrStart57);
        Check("双事实: 头部解释用玩家口吻短文案（旧长句已移除）",
            hdr57.Contains("存档＝当前进度，备份＝可还原副本")
            && !xaml57.Contains("存档＝游戏里的当前进度"), "");
        Check("双事实: 头部解释与保留策略均有省略号兜底、PolicyText 悬停可看全文",
            System.Text.RegularExpressions.Regex.Matches(hdr57, "TextTrimming=\"CharacterEllipsis\"").Count >= 2
            && hdr57.Contains("ToolTip=\"{Binding DataContext.PolicyText"), "");
        // 次要标记配色：未选中 Accent2，蓝底选中筹码上改浅绿（与状态行 good 态同色）
        int tagStart57 = xaml57.IndexOf("Text=\"{Binding RestoredTagText}\"");
        string tagBlock57 = xaml57.Substring(tagStart57,
            xaml57.IndexOf("</TextBlock>", tagStart57) - tagStart57);
        Check("双事实: 次要标记未选中走 Accent2、蓝底选中态浅绿 #8FE0B4",
            tagBlock57.Contains("Value=\"{DynamicResource Accent2}\"")
            && tagBlock57.Contains("Binding IsActive")
            && tagBlock57.Contains("#8FE0B4"), "");
        // 游玩状态提速：轮询 3s→1s；每轮只枚举一次全部进程（替代每游戏一次枚举）
        Check("游玩提速: 监视轮询间隔 1 秒",
            mainCs57.Contains("_watchTimer = new() { Interval = TimeSpan.FromSeconds(1) }"), "");
        Check("游玩提速: 每轮只快照一次全部进程名",
            mainCs57.Contains("HashSet<string> runningNames;")
            && mainCs57.Contains("await Task.Run")
            && mainCs57.Contains("foreach (var p in Process.GetProcesses())"), "");
    }

    // ── 58. 切换玩家锚点：还原旧档后切换不打重复 zip、切回仍停旧档；新进度照打包；锚点失效回退；改名带锚点 ──
    {
        string ls58 = Path.Combine(temp, "switch_anchor58");
        Directory.CreateDirectory(ls58);
        string f58 = Path.Combine(ls58, "save.dat");
        var g58 = new GameEntry { NameEN = "SwitchAnchor58" };
        g58.SavePaths.Add(new SavePathEntry { Path = ls58 });

        // 甲：旧档 A、新档 B（时间拉开；注意 CreatePlayer 首位玩家会自带「初始备份」=当前 live）
        File.WriteAllText(f58, "甲旧档内容");
        File.SetLastWriteTime(f58, DateTime.Now.AddMinutes(-30));
        ProfileService.CreatePlayer(g58, "甲58");
        var bakA58 = g58.Backups.Where(b => b.Profile == "甲58").OrderBy(b => b.Time).First();
        File.WriteAllText(f58, "甲新档内容-更长");
        File.SetLastWriteTime(f58, DateTime.Now.AddMinutes(-20));
        var bakB58 = BackupService.Create(g58, "甲新进度");
        // 乙玩家（CreatePlayer 会先给甲打包，live=B 零变化被吞）与她的备份
        ProfileService.CreatePlayer(g58, "乙58");
        File.WriteAllText(f58, "乙的专属进度");
        File.SetLastWriteTime(f58, DateTime.Now.AddMinutes(-15));
        var bakC58 = BackupService.Create(g58, "乙进度");
        int Cnt58(string p) => g58.Backups.Count(b => b.Profile == p);

        // 切到甲：乙 live 与她最新包一致 → 锚定 C，零新增；甲无锚点 → 还原最新 B
        ProfileService.SwitchTo(g58, "甲58");
        Check("切换锚点: 无变化切走只锚定不打包（乙仍 1 份）",
            Cnt58("乙58") == 1 && File.ReadAllText(f58) == "甲新档内容-更长",
            $"乙={Cnt58("乙58")},live={File.ReadAllText(f58)}");

        // 甲手动还原到旧档 A（复现卡赞场景；同口径下安全快照因无变化跳过，甲仍 2 份）
        BackupService.Restore(g58, bakA58);
        Check("切换锚点: 手动还原旧档后 live=A 且份数未膨胀",
            File.ReadAllText(f58) == "甲旧档内容" && Cnt58("甲58") == 2,
            $"live={File.ReadAllText(f58)},甲={Cnt58("甲58")}");

        // 关键：切到乙。旧逻辑会把 live(A) 当"新变化"再打一份冗余 zip；新逻辑命中已有 A → 只锚定
        // （AnchorOnLeave 命中 A；SwitchTo 的 Restore 已关闭安全快照，不会再从 Restore 内部冒出重复包）
        ProfileService.SwitchTo(g58, "乙58");
        Check("切换锚点: 还原旧档后切走零冗余 zip（甲仍 2 份），锚点指向 A",
            Cnt58("甲58") == 2
            && g58.SwitchAnchors.TryGetValue("甲58", out var anchA58)
            && anchA58 == bakA58.FileJson
            && File.ReadAllText(f58) == "乙的专属进度",
            $"甲={Cnt58("甲58")},锚点={(g58.SwitchAnchors.TryGetValue("甲58", out var v58) ? v58 : "<无>")}");

        // 切回甲：锚点 A 生效（旧逻辑会选 Time 最新的 B，把玩家带回新档）
        ProfileService.SwitchTo(g58, "甲58");
        Check("切换锚点: 切回来仍停在手动选择的旧档 A（不被最新包 B 带回）",
            File.ReadAllText(f58) == "甲旧档内容" && Cnt58("甲58") == 2 && Cnt58("乙58") == 1,
            $"live={File.ReadAllText(f58)},甲={Cnt58("甲58")},乙={Cnt58("乙58")}");

        // 甲玩出任何备份都没有的全新进度后切走：照常打包保进度，锚点指向新包
        File.WriteAllText(f58, "甲玩出的全新结局内容");
        File.SetLastWriteTime(f58, DateTime.Now.AddMinutes(-10));
        ProfileService.SwitchTo(g58, "乙58");
        var newPack58 = g58.Backups.Where(b => b.Profile == "甲58" && b.Note == "切换玩家")
            .OrderByDescending(b => b.Time).FirstOrDefault();
        Check("切换锚点: 真有新进度切走仍自动打包（甲 3 份）并锚定新包",
            Cnt58("甲58") == 3 && newPack58 != null
            && g58.SwitchAnchors.TryGetValue("甲58", out var anchD58) && anchD58 == newPack58.FileJson,
            $"甲={Cnt58("甲58")}");
        ProfileService.SwitchTo(g58, "甲58");
        Check("切换锚点: 切回甲恢复她的全新进度",
            File.ReadAllText(f58) == "甲玩出的全新结局内容", File.ReadAllText(f58));

        // 锚点失效（zip 被 Prune 淘汰/损坏）：回退最新可还原包，不抛异常
        g58.SwitchAnchors["甲58"] = "第一狂战士：不存在_1999.zip";
        ProfileService.SwitchTo(g58, "乙58");
        ProfileService.SwitchTo(g58, "甲58");
        Check("切换锚点: 锚点包失效回退最新可还原包（live=甲全新进度）",
            File.ReadAllText(f58) == "甲玩出的全新结局内容", File.ReadAllText(f58));

        // 改名带锚点：键改名、值按 zip 文件名重映射
        g58.SwitchAnchors["乙58"] = bakC58.FileJson;
        ProfileService.RenamePlayer(g58, "乙58", "丙58");
        Check("切换锚点: 玩家改名后锚点随迁（旧键消失、指向新记录）",
            !g58.SwitchAnchors.ContainsKey("乙58")
            && g58.SwitchAnchors.TryGetValue("丙58", out var anchC58)
            && anchC58 == g58.Backups.First(b => b.Profile == "丙58").FileJson, "");

        // JSON 往返：锚点随 games.json 持久化
        var g58Round = System.Text.Json.JsonSerializer.Deserialize<GameEntry>(
            System.Text.Json.JsonSerializer.Serialize(g58))!;
        Check("切换锚点: SwitchAnchors 随 JSON 持久化往返",
            g58Round.SwitchAnchors != null
            && g58Round.SwitchAnchors.TryGetValue("甲58", out var ar58) && ar58.Length > 0
            && g58Round.SwitchAnchors.ContainsKey("丙58"), "");

        // 静态接线守卫
        string srcRoot58 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "GameSaveManager"));
        string profCs58 = File.ReadAllText(Path.Combine(srcRoot58, "Services", "ProfileService.cs"));
        Check("切换锚点: SwitchTo 经 AnchorOnLeave/PickSwitchTarget 走锚点",
            profCs58.Contains("BackupService.FindSnapshotMatchingLive(g, leaving)")
            && profCs58.Contains("var target = PickSwitchTarget(g, name)"), "");
        string bkCs58 = File.ReadAllText(Path.Combine(srcRoot58, "Services", "BackupService.cs"));
        Check("切换锚点: 玩家切换的 Restore 关闭安全快照（冗余包真正源头）",
            bkCs58.Contains("bool safetySnapshot = true")
            && profCs58.Contains("targetPaths: EffectivePaths(g), safetySnapshot: false"), "");
        string entryCs58 = File.ReadAllText(Path.Combine(srcRoot58, "Models", "GameEntry.cs"));
        Check("切换锚点: GameEntry 持久化 SwitchAnchors",
            entryCs58.Contains("public Dictionary<string, string> SwitchAnchors"), "");
    }

    // ── 59. 心目中的存档：认可动作移指针（init/manual/auto/switch/restore）、筹码以指针为基准、
    //       行感叹号打标、Prune 豁免指针包、悬空回退、改名/删除随迁、RestoredMarks 启动迁移 ──
    {
        string vault59 = Path.Combine(temp, "vault59");
        Store.HomeDirOverride = vault59;
        Store.Init();
        int svMax59 = Store.Settings.MaxKeepPerGame;
        bool svInc59 = Store.Settings.IncrementalEnabled;
        Store.Settings.IncrementalEnabled = false;
        Store.Settings.MaxKeepPerGame = 10;

        // 59.A 主游戏：指针随五种认可动作移动
        string ls59 = Path.Combine(temp, "fav_live59");
        Directory.CreateDirectory(ls59);
        string f59 = Path.Combine(ls59, "save.dat");
        var g59 = new GameEntry { NameEN = "Favorite59" };
        g59.SavePaths.Add(new SavePathEntry { Path = ls59 });
        const string P59 = "主角59";

        File.WriteAllText(f59, "fav-v1");
        File.SetLastWriteTime(f59, DateTime.Now.AddMinutes(-30));
        ProfileService.CreatePlayer(g59, P59);
        var z1_59 = g59.Backups.OrderBy(b => b.Time).First();
        Check("心目中的档: 首位玩家建卡 → 指针=初始包 why=init，筹码 same",
            g59.SwitchAnchors.TryGetValue(P59, out var a1_59) && a1_59 == z1_59.FileJson
            && g59.FavoriteWhy.TryGetValue(P59, out var w1_59) && w1_59 == "init"
            && BackupService.GetLiveState(g59, P59).Kind == "same", "");

        File.WriteAllText(f59, "fav-v2-longer");
        File.SetLastWriteTime(f59, DateTime.Now.AddMinutes(-20));
        var z2_59 = ProfileService.BackupAsFavorite(g59, "手动备份", false, "manual", out _);
        Check("心目中的档: 手动备份 → 指针移到新包 why=manual，same",
            g59.SwitchAnchors[P59] == z2_59.FileJson && g59.FavoriteWhy[P59] == "manual"
            && BackupService.GetLiveState(g59, P59).Kind == "same", "");

        bool noChange59 = false;
        try { ProfileService.BackupAsFavorite(g59, "没变化的手动备份", false, "manual", out _); }
        catch (InvalidOperationException ex) { noChange59 = ex.Message.Contains("没有变化"); }
        Check("心目中的档: 手动备份报无变化 → 异常照抛且指针落最新可还原包（不悬空）",
            noChange59 && g59.SwitchAnchors[P59] == z2_59.FileJson
            && g59.FavoriteWhy[P59] == "manual", "");

        // 英灵神殿真实场景：刚手动备份（why=manual），退出游戏自动备份发现无变化（why=auto）
        // → 没有新认可动作，指针与 why 都保持 manual，不被改写成 auto
        bool noChangeAuto59 = false;
        try { ProfileService.BackupAsFavorite(g59, "已退出，自动备份", false, "auto", out _); }
        catch (InvalidOperationException ex) { noChangeAuto59 = ex.Message.Contains("没有变化"); }
        Check("心目中的档: 无变化且指针已在该包 → 保留原 why（手动备份后退出不被改写成 auto）",
            noChangeAuto59 && g59.SwitchAnchors[P59] == z2_59.FileJson
            && g59.FavoriteWhy[P59] == "manual", "");

        // 指针指在旧包（如她刚还原了旧档、live 却与最新包一致）时无变化 → 仍兜底回到最新可还原包
        ProfileService.MarkFavorite(g59, P59, z1_59.FileJson, "restore");
        bool noChangeFallback59 = false;
        try { ProfileService.BackupAsFavorite(g59, "已退出，自动备份", false, "auto", out _); }
        catch (InvalidOperationException ex) { noChangeFallback59 = ex.Message.Contains("没有变化"); }
        Check("心目中的档: 无变化但指针指在旧包 → 兜底回到最新可还原包 why=auto",
            noChangeFallback59 && g59.SwitchAnchors[P59] == z2_59.FileJson
            && g59.FavoriteWhy[P59] == "auto", "");

        File.WriteAllText(f59, "fav-v3-even-longer");
        File.SetLastWriteTime(f59, DateTime.Now.AddMinutes(-10));
        var z3_59 = ProfileService.BackupAsFavorite(g59, "退出游戏自动备份", false, "auto", out _);
        Check("心目中的档: 退出自动备份 → 指针移到新包 why=auto，same",
            g59.SwitchAnchors[P59] == z3_59.FileJson && g59.FavoriteWhy[P59] == "auto"
            && BackupService.GetLiveState(g59, P59).Kind == "same", "");

        // 运行中热快照走内部 Create 口径：不移动指针
        File.WriteAllText(f59, "fav-v3b-hotshot");
        File.SetLastWriteTime(f59, DateTime.Now.AddMinutes(-9));
        var zHot59 = BackupService.Create(g59, "运行中热快照", hot: true, out int skippedHot59);
        Check("心目中的档: 运行中热快照成功但指针不动（仍 why=auto 停在退出备份包）",
            zHot59.Hot && g59.SwitchAnchors[P59] == z3_59.FileJson
            && g59.FavoriteWhy[P59] == "auto", $"skipped={skippedHot59}");

        // 手动还原首包 → 指针回到旧档 why=restore、same；同次核对接行感叹号
        BackupService.Restore(g59, z1_59);
        var stFav59 = BackupService.GetLiveState(g59, P59);
        Check("心目中的档: 手动还原 → 指针=还原目标 why=restore，live==指针 same",
            stFav59.Kind == "same" && stFav59.FavoriteWhy == "restore"
            && stFav59.MatchedAt == z1_59.SavedAt && g59.SwitchAnchors[P59] == z1_59.FileJson, "");
        BackupService.ApplyNewerThanLiveFlags(g59.Backups, stFav59);
        BackupRecord Row59(string fj) => g59.Backups.First(b => b.FileJson == fj);
        Check("心目中的档: 行感叹号——指针包不亮、更新的备份亮，提示含新于当前存档",
            !Row59(z1_59.FileJson).IsNewerThanLive && Row59(z1_59.FileJson).NewerThanLiveTip == ""
            && Row59(z2_59.FileJson).IsNewerThanLive && Row59(z3_59.FileJson).IsNewerThanLive
            && Row59(zHot59.FileJson).IsNewerThanLive
            && Row59(z3_59.FileJson).NewerThanLiveTip.Contains("新于当前存档"), "");
        BackupService.ApplyNewerThanLiveFlags(g59.Backups, null);
        Check("心目中的档: st=null（游玩中）统一清标",
            g59.Backups.All(b => !b.IsNewerThanLive && b.NewerThanLiveTip == ""), "");
        File.Delete(f59);
        var stEmpty59 = BackupService.GetLiveState(g59, P59);
        BackupService.ApplyNewerThanLiveFlags(g59.Backups, stEmpty59);
        Check("心目中的档: live 为空 → nosave，全部行亮感叹号且提示存档目录为空",
            stEmpty59.Kind == "nosave" && Row59(z1_59.FileJson).IsNewerThanLive
            && Row59(z2_59.FileJson).NewerThanLiveTip.Contains("存档目录为空"), stEmpty59.Kind);

        // live 比指针旧 → older；玩出更新进度 → changed
        File.WriteAllText(f59, "fav-v1");
        File.SetLastWriteTime(f59, DateTime.Now.AddMinutes(-40));
        Check("心目中的档: live 早于指针包 → older + BackupLatest=指针时间",
            BackupService.GetLiveState(g59, P59) is var stOlder59
            && stOlder59.Kind == "older"
            && Math.Abs((stOlder59.BackupLatest - z1_59.SavedAt).TotalSeconds) < 2, stOlder59.Kind);
        File.WriteAllText(f59, "fav-v4-brand-new");
        File.SetLastWriteTime(f59, DateTime.Now.AddMinutes(-5));
        Check("心目中的档: live 新于指针包 → changed",
            BackupService.GetLiveState(g59, P59).Kind == "changed", "");

        // 切走锚定 why=switch；切回还原指针包承载的内容
        ProfileService.CreatePlayer(g59, "伙伴59");
        Check("心目中的档: 新建第二名玩家 → 离开者指针 why=switch",
            g59.FavoriteWhy.TryGetValue(P59, out var whyLeave59) && whyLeave59 == "switch", "");
        ProfileService.SwitchTo(g59, P59);
        Check("心目中的档: 切回主角还原她指针包承载的内容",
            File.ReadAllText(f59) == "fav-v4-brand-new", File.ReadAllText(f59));

        // 59.B 无指针/悬空指针：ResolveFavorite 回退最新可还原包
        string fbLive59 = Path.Combine(temp, "fav_fallback59");
        Directory.CreateDirectory(fbLive59);
        string fbf59 = Path.Combine(fbLive59, "save.dat");
        var gFb59 = new GameEntry { NameEN = "FavFallback59" };
        gFb59.SavePaths.Add(new SavePathEntry { Path = fbLive59 });
        foreach (var (txt, mins) in new[] { ("fb-v1", -30), ("fb-v2-long", -20), ("fb-v3-longer", -10) })
        {
            File.WriteAllText(fbf59, txt);
            File.SetLastWriteTime(fbf59, DateTime.Now.AddMinutes(mins));
            BackupService.Create(gFb59, txt);
        }
        string profFb59 = gFb59.ActiveProfile;
        var latestFb59 = gFb59.Backups.Where(b => BackupService.IsRestorable(gFb59, b))
            .OrderByDescending(b => b.Time).First();
        var favFb59 = ProfileService.ResolveFavorite(gFb59, profFb59, out var whyFb59);
        Check("心目中的档: 无指针 → 回退最新可还原包 + why=fallback",
            favFb59 != null && favFb59.FileJson == latestFb59.FileJson && whyFb59 == "fallback",
            whyFb59 ?? "<null>");
        gFb59.SwitchAnchors[profFb59] = "不存在的幽灵包_1999.zip";
        var favDangle59 = ProfileService.ResolveFavorite(gFb59, profFb59, out _);
        Check("心目中的档: 悬空指针不抛异常、回退最新可还原包",
            favDangle59 != null && favDangle59.FileJson == latestFb59.FileJson, "");

        // 59.C Prune：指针包与锁并列豁免、不占保留配额
        Store.Settings.MaxKeepPerGame = 3;
        string prLive59 = Path.Combine(temp, "fav_prune59");
        Directory.CreateDirectory(prLive59);
        string prf59 = Path.Combine(prLive59, "save.dat");
        var gPr59 = new GameEntry { NameEN = "FavPrune59" };
        gPr59.SavePaths.Add(new SavePathEntry { Path = prLive59 });
        File.WriteAllText(prf59, "pr-v1");
        File.SetLastWriteTime(prf59, DateTime.Now.AddMinutes(-40));
        ProfileService.CreatePlayer(gPr59, "留档59");
        var pz1_59 = gPr59.Backups.Single(b => b.Profile == "留档59");
        BackupRecord? pz2_59 = null;
        int idxPr59 = 2;
        foreach (var (txt, mins) in new[] {
            ("pr-v2-long", -30), ("pr-v3-longer", -20), ("pr-v4-longerr", -15), ("pr-v5-longerrr", -10) })
        {
            File.WriteAllText(prf59, txt);
            File.SetLastWriteTime(prf59, DateTime.Now.AddMinutes(mins));
            var recPr = BackupService.Create(gPr59, "第" + idxPr59 + "份");
            if (idxPr59 == 2) pz2_59 = recPr;
            idxPr59++;
        }
        var pzLast59 = gPr59.Backups.OrderByDescending(b => b.Time).First();
        Check("心目中的档: Prune 超配额淘汰旧包但豁免指针包（z1 存活、z2 淘汰、总数 4）",
            gPr59.Backups.Count == 4
            && gPr59.Backups.Any(b => b.FileJson == pz1_59.FileJson) && File.Exists(pz1_59.File)
            && !gPr59.Backups.Any(b => b.FileJson == pz2_59!.FileJson) && !File.Exists(pz2_59!.File)
            && gPr59.Backups.Any(b => b.FileJson == pzLast59.FileJson),
            $"n={gPr59.Backups.Count}");

        // 59.D 启动迁移 BackfillFavorite：旧还原标记 ±2s 命中→restore；找不到→二次回填最新；幂等
        GameEntry NewMigGame59(string en, string liveDir, string file,
            params (string txt, int mins)[] snaps)
        {
            Directory.CreateDirectory(liveDir);
            var gg = new GameEntry { NameEN = en };
            gg.SavePaths.Add(new SavePathEntry { Path = liveDir });
            foreach (var (txt, mins) in snaps)
            {
                File.WriteAllText(file, txt);
                File.SetLastWriteTime(file, DateTime.Now.AddMinutes(mins));
                BackupService.Create(gg, txt);
            }
            return gg;
        }
        string profMig59 = ProfileService.DefaultFirstPlayerName;
        var gMA59 = NewMigGame59("FavMigA59", Path.Combine(temp, "fav_migA59"),
            Path.Combine(temp, "fav_migA59", "save.dat"), ("ma-v1", -30));
        var bMA59 = gMA59.Backups.Single();
        gMA59.RestoredMarks[profMig59] = bMA59.SavedAt;
        var gMB59 = NewMigGame59("FavMigB59", Path.Combine(temp, "fav_migB59"),
            Path.Combine(temp, "fav_migB59", "save.dat"), ("mb-v1", -30), ("mb-v2-long", -20));
        var bMB2_59 = gMB59.Backups.OrderByDescending(b => b.Time).First();
        gMB59.RestoredMarks[profMig59] = DateTime.Now.AddDays(-9);   // 库里没有对应包
        var gMC59 = NewMigGame59("FavMigC59", Path.Combine(temp, "fav_migC59"),
            Path.Combine(temp, "fav_migC59", "save.dat"), ("mc-v1", -30));
        var bMC59 = gMC59.Backups.Single();
        var gMD59 = NewMigGame59("FavMigD59", Path.Combine(temp, "fav_migD59"),
            Path.Combine(temp, "fav_migD59", "save.dat"), ("md-v1", -30), ("md-v2-long", -20));
        var dOld59 = gMD59.Backups.OrderBy(b => b.Time).First();
        gMD59.SwitchAnchors[profMig59] = dOld59.FileJson;   // 有效指针但缺 why
        Store.Games.Add(gMA59); Store.Games.Add(gMB59);
        Store.Games.Add(gMC59); Store.Games.Add(gMD59);
        Store.Save();
        ProfileService.BackfillFavorite();
        ProfileService.BackfillFavorite();
        Check("心目中的档: 迁移①旧还原标记命中现存包 → 指针=该包 why=restore，旧字段清空",
            gMA59.SwitchAnchors.TryGetValue(profMig59, out var anMA59) && anMA59 == bMA59.FileJson
            && gMA59.FavoriteWhy[profMig59] == "restore" && gMA59.RestoredMarks.Count == 0, "");
        Check("心目中的档: 迁移②标记找不到对应包 → 二遍回填最新包 why=空，不拿新包冒认还原",
            gMB59.SwitchAnchors.TryGetValue(profMig59, out var anMB59) && anMB59 == bMB2_59.FileJson
            && gMB59.FavoriteWhy[profMig59] == "" && gMB59.RestoredMarks.Count == 0, "");
        Check("心目中的档: 迁移③无标记无指针 → 最新可还原包 why=空",
            gMC59.SwitchAnchors[profMig59] == bMC59.FileJson && gMC59.FavoriteWhy[profMig59] == "", "");
        Check("心目中的档: 迁移④有效指针保留、只补 why=空（不改指她的选择）",
            gMD59.SwitchAnchors[profMig59] == dOld59.FileJson && gMD59.FavoriteWhy[profMig59] == "", "");
        string gamesFile59 = Path.Combine(vault59, "games.json");
        string hashMig2 = FileFingerprint(gamesFile59);
        ProfileService.BackfillFavorite();
        Check("心目中的档: 迁移第三遍无副作用（不再落盘，全部 RestoredMarks 已空）",
            FileFingerprint(gamesFile59) == hashMig2
            && Store.Games.All(g => g.RestoredMarks == null || g.RestoredMarks.Count == 0), "");

        // 59.E 改名 why 随迁、删玩家清两字典
        string rnLive59 = Path.Combine(temp, "fav_rename59");
        Directory.CreateDirectory(rnLive59);
        string rnf59 = Path.Combine(rnLive59, "save.dat");
        var gRn59 = new GameEntry { NameEN = "FavRename59" };
        gRn59.SavePaths.Add(new SavePathEntry { Path = rnLive59 });
        File.WriteAllText(rnf59, "rn-甲1");
        File.SetLastWriteTime(rnf59, DateTime.Now.AddMinutes(-30));
        ProfileService.CreatePlayer(gRn59, "甲59r");
        ProfileService.CreatePlayer(gRn59, "乙59r");
        File.WriteAllText(rnf59, "rn-乙1-long");
        File.SetLastWriteTime(rnf59, DateTime.Now.AddMinutes(-20));
        var bYi59 = BackupService.Create(gRn59, "乙进度");
        ProfileService.MarkFavorite(gRn59, "乙59r", bYi59.FileJson, "manual");
        ProfileService.RenamePlayer(gRn59, "乙59r", "丙59r");
        Check("心目中的档: 改名 → 指针与 why 随迁、旧键清空",
            !gRn59.SwitchAnchors.ContainsKey("乙59r") && !gRn59.FavoriteWhy.ContainsKey("乙59r")
            && gRn59.SwitchAnchors.TryGetValue("丙59r", out var anC59)
            && anC59 == gRn59.Backups.First(b => b.Profile == "丙59r").FileJson
            && gRn59.FavoriteWhy["丙59r"] == "manual", "");
        ProfileService.DeletePlayer(gRn59, "丙59r");
        Check("心目中的档: 删玩家 → 指针与 why 一并移除",
            !gRn59.SwitchAnchors.ContainsKey("丙59r")
            && !gRn59.FavoriteWhy.ContainsKey("丙59r"), "");

        // 59.F 静态接线守卫
        string srcRoot59 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "GameSaveManager"));
        string entryCs59 = File.ReadAllText(Path.Combine(srcRoot59, "Models", "GameEntry.cs"));
        Check("心目中的档: GameEntry 持久化 FavoriteWhy、行标记运行时属性 IsNewerThanLive",
            entryCs59.Contains("public Dictionary<string, string> FavoriteWhy")
            && entryCs59.Contains("public bool IsNewerThanLive"), "");
        string xaml59 = File.ReadAllText(Path.Combine(srcRoot59, "MainWindow.xaml"));
        Check("心目中的档: 备份行橙色感叹号（锁按钮前/绑定打标/Orange 填充/Material 图形）",
            xaml59.Contains("IsNewerThanLive, Converter={StaticResource BoolToVis}")
            && xaml59.Contains("ToolTip=\"{Binding NewerThanLiveTip}\"")
            && xaml59.Contains("Fill=\"{DynamicResource Orange}\"")
            && xaml59.Contains("M12 2C6.48 2"), "");
        string profCs59 = File.ReadAllText(Path.Combine(srcRoot59, "Services", "ProfileService.cs"));
        Check("心目中的档: 认可动作写入口/锚定/解析/迁移公开接线齐全",
            profCs59.Contains("public static BackupRecord BackupAsFavorite")
            && profCs59.Contains("public static void MarkFavorite")
            && profCs59.Contains("public static void AnchorOnLeave")
            && profCs59.Contains("public static void BackfillFavorite")
            && profCs59.Contains("MarkFavorite(g, leaving, matched.FileJson, \"switch\")"), "");
        string svcCs59 = File.ReadAllText(Path.Combine(srcRoot59, "Services", "BackupService.cs"));
        Check("心目中的档: Prune 豁免指针包 + 行感叹号打标函数在 BackupService",
            svcCs59.Contains("favFiles")
            && svcCs59.Contains("b.Locked || favFiles.Contains(b.File)")
            && svcCs59.Contains("public static void ApplyNewerThanLiveFlags"), "");
        string mainCs59 = File.ReadAllText(Path.Combine(srcRoot59, "MainWindow.xaml.cs"));
        Check("心目中的档: 筹码核对后同步打标 + older 点击还原指针包 + 文案接线",
            mainCs59.Contains("BackupService.ApplyNewerThanLiveFlags(g.VisibleBackups, running ? null : st)")
            && mainCs59.Contains("\"restoreFavorite\"")
            && mainCs59.Contains("ProfileService.ResolveFavorite(g, profile, out _)")
            && mainCs59.Contains("心目中的档"), "");

        Store.Settings.MaxKeepPerGame = svMax59;
        Store.Settings.IncrementalEnabled = svInc59;
    }

    // ── 60. 完结馆：完结字段/派生文案/海报判定与持久化往返，入口/覆盖层/海报卡/日历/操作栏接线守卫 ──
    {
        string vault60 = Path.Combine(temp, "vault60");
        Store.HomeDirOverride = vault60;
        Store.Init();

        var g60 = new GameEntry { NameEN = "HallOfFame60" };
        Check("完结馆: 新游戏默认未完结且派生文案/月份键全空",
            !g60.IsCompleted && !g60.HasPoster
            && g60.CompletedDateText == "" && g60.CompletedShortText == ""
            && g60.CompletedFullText == "" && g60.CompletedMonthKey == "", "");

        g60.CompletedAt = new DateTime(2026, 9, 15);
        Check("完结馆: 标记后日期文案与月份键正确（2026/09/15、9/15、2026年9月15日完结、2026-09）",
            g60.IsCompleted
            && g60.CompletedDateText == "2026/09/15"
            && g60.CompletedShortText == "9/15"
            && g60.CompletedFullText == "2026年9月15日完结"
            && g60.CompletedMonthKey == "2026-09", "");

        // 海报：空→占位；指向真实文件→有海报；null 容错
        Check("完结馆: PosterFile 空时无海报走首字占位", !g60.HasPoster, "");
        string poster60 = Path.Combine(temp, "p60.jpg");
        File.WriteAllBytes(poster60, new byte[] { 1, 2, 3 });
        g60.PosterFile = poster60;
        Check("完结馆: PosterFile 指向真实文件时 HasPoster 为真", g60.HasPoster, "");
        g60.PosterFile = null!;
        Check("完结馆: PosterFile 赋 null 容错为空串不崩", g60.PosterFile == "" && !g60.HasPoster, "");
        g60.PosterFile = poster60;

        Store.Games.Add(g60);
        Store.Save();
        var disk60 = System.Text.Json.JsonSerializer.Deserialize<List<GameEntry>>(
            File.ReadAllText(Store.GamesFile))!;
        var g60b = disk60.Single(x => x.NameEN == "HallOfFame60");
        Check("完结馆: CompletedAt/PosterFile 随 games.json 落盘往返",
            g60b.CompletedAt == new DateTime(2026, 9, 15) && g60b.PosterFile == poster60
            && g60b.IsCompleted && g60b.CompletedMonthKey == "2026-09", "");

        // 取消完结：MinValue 落盘后仍为未完结
        g60.CompletedAt = DateTime.MinValue;
        Store.Save();
        var g60c = System.Text.Json.JsonSerializer.Deserialize<List<GameEntry>>(
            File.ReadAllText(Store.GamesFile))!.Single(x => x.NameEN == "HallOfFame60");
        Check("完结馆: 取消完结（MinValue）落盘后未完结、月份键清空",
            !g60c.IsCompleted && g60c.CompletedMonthKey == "", "");

        Check("完结馆: PosterFileFor 按 AppId 落 cache/posters 目录",
            Store.PosterFileFor(123).EndsWith(Path.Combine("cache", "posters", "123.jpg")), "");
        Check("完结馆: 自定义海报按 custom_{gameId}.jpg 落 posters 目录",
            Store.PosterCustomFileFor("abc").EndsWith(Path.Combine("cache", "posters", "custom_abc.jpg")), "");

        // 展示兜底链：竖海报 → 横封面 → 图标 → 空（首字占位）
        string icon60 = Path.Combine(temp, "i60.png");
        string cover60 = Path.Combine(temp, "c60.jpg");
        File.WriteAllBytes(icon60, new byte[] { 1 });
        File.WriteAllBytes(cover60, new byte[] { 1 });
        g60.PosterFile = "";
        g60.CoverFile = "";
        g60.IconFile = icon60;
        Check("兜底链: 无海报无封面时用图标",
            g60.PosterDisplayFile == icon60 && g60.HasPosterDisplay && !g60.HasCustomPoster, "");
        g60.CoverFile = cover60;
        Check("兜底链: 封面优先于图标", g60.PosterDisplayFile == cover60, "");
        string custom60 = Path.Combine(temp, "custom_" + g60.Id + ".jpg");
        File.WriteAllBytes(custom60, new byte[] { 1 });
        g60.PosterFile = custom60;
        Check("兜底链: 自定义竖海报最优先且可识别",
            g60.PosterDisplayFile == custom60 && g60.HasCustomPoster, "");
        g60.PosterFile = poster60;
        Check("兜底链: 官方竖海报不算自定义", !g60.HasCustomPoster, "");
        g60.PosterFile = ""; g60.CoverFile = ""; g60.IconFile = "";
        Check("兜底链: 三图皆空→空串显示首字占位",
            g60.PosterDisplayFile == "" && !g60.HasPosterDisplay, "");

        // ── 静态接线守卫 ──
        string srcRoot60 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "GameSaveManager"));
        string entryCs60 = File.ReadAllText(Path.Combine(srcRoot60, "Models", "GameEntry.cs"));
        Check("完结馆: GameEntry 持久化 CompletedAt/PosterFile 并暴露月份键与日期文案",
            entryCs60.Contains("public DateTime CompletedAt")
            && entryCs60.Contains("public string PosterFile")
            && entryCs60.Contains("public bool IsCompleted")
            && entryCs60.Contains("public bool HasPoster")
            && entryCs60.Contains("CompletedMonthKey"), "");
        string storeCs60 = File.ReadAllText(Path.Combine(srcRoot60, "Services", "Store.cs"));
        Check("完结馆: Store 暴露海报缓存目录与按 AppId 路径",
            storeCs60.Contains("Path.Combine(BaseDir, \"cache\", \"posters\")")
            && storeCs60.Contains("public static string PosterFileFor(int appId)"), "");
        string steamCs60 = File.ReadAllText(Path.Combine(srcRoot60, "Services", "SteamApi.cs"));
        Check("完结馆: SteamApi 可下载竖版海报 library_600x900.jpg",
            steamCs60.Contains("public static Task<bool> DownloadPoster(int appId, string saveTo)")
            && steamCs60.Contains("\"library_600x900.jpg\""), "");
        Check("海报: 新游戏哈希图床兜底（GetItems 免 key 取 library_capsule/header，四主机轮试）",
            steamCs60.Contains("IStoreBrowseService/GetItems/v1/")
            && steamCs60.Contains("FetchStoreAssetsAsync")
            && steamCs60.Contains("include_assets")
            && steamCs60.Contains("\"library_capsule\"")
            && steamCs60.Contains("store_item_assets")
            && steamCs60.Contains("shared.cloudflare.steamstatic.com")
            && steamCs60.Contains("shared.akamai.steamstatic.com")
            && steamCs60.Contains("steamcdn-a.akamaihd.net"), "");

        string xaml60 = File.ReadAllText(Path.Combine(srcRoot60, "MainWindow.xaml"));
        Check("完结馆: 工具栏入口按钮（纯文字，按用户要求不显示数量徽章）",
            xaml60.Contains("<Button x:Name=\"BtnCompleted\"")
            && xaml60.Contains("Click=\"OnOpenCompleted\"")
            && xaml60.Contains("Content=\"🏆 完结馆\"")
            && !xaml60.Contains("CompletedCountBadge")
            && !xaml60.Contains("CompletedCountText"), "");
        Check("完结馆: 覆盖层视图/返回/副标题/空状态/卡片宿主齐全",
            xaml60.Contains("<Border x:Name=\"CompletedView\" Grid.Row=\"2\"")
            && xaml60.Contains("x:Name=\"BtnCompletedBack\"")
            && xaml60.Contains("Click=\"OnCloseCompleted\"")
            && xaml60.Contains("x:Name=\"CompletedSummary\"")
            && xaml60.Contains("x:Name=\"CompletedHost\"")
            && xaml60.Contains("x:Name=\"CompletedEmpty\""), "");
        Check("完结馆: 按月份/按年份/全部分组 chip 绑定同一切换事件",
            xaml60.Contains("x:Name=\"CompletedGroupMonth\"")
            && xaml60.Contains("Content=\"按月份\"")
            && xaml60.Contains("x:Name=\"CompletedGroupYear\"")
            && xaml60.Contains("Content=\"按年份\"")
            && xaml60.Contains("x:Name=\"CompletedGroupAll\"")
            && xaml60.Contains("Checked=\"OnCompletedModeChanged\""), "");
        // 卡片切片锚定 DataTemplate 标签，避开上方同名注释
        int cardStart60 = xaml60.IndexOf("<DataTemplate x:Key=\"CompletedCard\"");
        string card60 = cardStart60 < 0
            ? ""
            : xaml60.Substring(cardStart60,
                xaml60.IndexOf("</DataTemplate>", cardStart60) - cardStart60);
        Check("完结馆: 竖版 200x300 海报卡（真3D透视倾斜/单击进回顾/右键改日期+取消/完结日期绑定/卡距12）",
            cardStart60 >= 0
            && card60.Contains("Width=\"200\"") && card60.Contains("Height=\"300\"")
            && card60.Contains("Margin=\"0,0,12,12\"")
            && card60.Contains("<Viewport2DVisual3D")
            && card60.Contains("<PerspectiveCamera")
            && card60.Contains("<AmbientLight")   // 无灯时 VisualHost 在部分 GPU 上纯黑（已实测复现）
            && card60.Contains("x:Name=\"PcRotX\"")
            && card60.Contains("x:Name=\"PcRotY\"")
            && card60.Contains("MouseMove=\"OnPosterTiltMove\"")
            && card60.Contains("MouseEnter=\"OnPosterTiltEnter\"")
            && card60.Contains("MouseLeave=\"OnPosterTiltLeave\"")
            && card60.Contains("x:Name=\"PcShadeX\"")
            && card60.Contains("x:Name=\"PcShadeY\"")
            && card60.Contains("x:Name=\"PcGlow\"")
            && !card60.Contains("CornerRadius")   // 全直角
            && card60.Contains("MouseLeftButtonUp=\"OnCompletedCardClick\"")
            && card60.Contains("Click=\"OnCompletedEditDate\"")
            && card60.Contains("Click=\"OnCompletedUnmark\"")
            && card60.Contains("{Binding CompletedDateText}"), "");
        Check("完结馆: 馆内「完结回顾」详情层（返回/运行/改日期/取消/在列表查看 + 游玩统计 + 会话列表）",
            xaml60.Contains("x:Name=\"CompletedDetail\"")
            && xaml60.Contains("Click=\"OnCompletedDetailBack\"")
            && xaml60.Contains("Click=\"OnCompletedDetailRun\"")
            && xaml60.Contains("Click=\"OnCompletedDetailEditDate\"")
            && xaml60.Contains("Click=\"OnCompletedDetailUnmark\"")
            && xaml60.Contains("Click=\"OnCompletedDetailLocate\"")
            && xaml60.Contains("x:Name=\"CdStatCount\"")
            && xaml60.Contains("x:Name=\"CdStatWeek7\"")
            && xaml60.Contains("x:Name=\"CdSessionList\"")
            && xaml60.Contains("{Binding TotalPlayText}")
            && xaml60.Contains("{Binding CompletedFullText}")
            && xaml60.Contains("{Binding AddedAtText}")
            // 头部右侧完结纪念卡（填满右半空白）：完结日大字/星期/通关用时/完结至今
            && xaml60.Contains("x:Name=\"CdMemorialDate\"")
            && xaml60.Contains("x:Name=\"CdMemorialSpan\"")
            && xaml60.Contains("x:Name=\"CdMemorialSince\"")
            && xaml60.Contains("完结纪念")
            // 统计单行 6 格、记录区内部限高滚动，整页不超屏
            && xaml60.Contains("<UniformGrid Columns=\"6\"")
            && xaml60.Contains("MaxHeight=\"168\""), "");
        Check("完结馆: 日历 Popup（选日期生效 + 回到今天）",
            xaml60.Contains("x:Name=\"CompletedDatePopup\"")
            && xaml60.Contains("x:Name=\"CompletedCalendar\"")
            && xaml60.Contains("SelectedDatesChanged=\"OnCompletedDateChanged\"")
            && xaml60.Contains("x:Name=\"CompletedTodayBtn\"")
            && xaml60.Contains("Click=\"OnCompletedTodayClick\""), "");
        Check("完结馆: 详情操作栏「完结撒花」与绿色「已完结」两态按钮",
            xaml60.Contains("x:Name=\"BtnMarkCompleted\"")
            && xaml60.Contains("Content=\"🏆 完结撒花\"")
            && !xaml60.Contains("标记完结")   // 玩家用语已改名，旧文案不得残留
            && xaml60.Contains("Click=\"OnMarkCompleted\"")
            && xaml60.Contains("x:Name=\"BtnCompletedDone\"")
            && xaml60.Contains("Click=\"OnCompletedDoneClick\"")
            && xaml60.Contains("{Binding CompletedShortText}"), "");
        // 详情横幅底部恒叠黑色压暗遮罩，标题/副标题必须固定浅色 + 黑色包围光晕（浅色主题下深字压暗底会看不清）
        int heroTitle60 = xaml60.IndexOf("Text=\"{Binding DisplayName}\" FontSize=\"22\"", StringComparison.Ordinal);
        string heroTitleBlock60 = heroTitle60 >= 0 ? xaml60.Substring(heroTitle60, 750) : "";
        Check("详情横幅: 黑色压暗遮罩 + 标题固定浅色 + 标题/副标题黑色包围光晕",
            xaml60.Contains("#EE000000")
            && heroTitleBlock60.Contains("Foreground=\"#F5F7FA\"")
            && heroTitleBlock60.Contains("Foreground=\"#C3C9D5\"")
            && !heroTitleBlock60.Contains("DynamicResource TextPrimary")
            && !heroTitleBlock60.Contains("DynamicResource TextSecondary")
            // 光晕：标题 Blur5/Depth0，副标题 Blur4/Depth0（零偏移=四周包围，非投影）
            && xaml60.Contains("BlurRadius=\"5\" ShadowDepth=\"0\"")
            && xaml60.Contains("BlurRadius=\"4\" ShadowDepth=\"0\""), "");
        // ── 全界面「文字显示不全」修复守卫（8 项布局兜底，防止回退成固定宽硬切） ──
        // 1) 详情横幅按钮行必须 WrapPanel（窄窗口换行，末尾按钮不被挤出）
        int heroBtns60 = xaml60.IndexOf("Content=\"▶ 运行游戏\"", StringComparison.Ordinal);
        string heroWrap60 = heroBtns60 >= 0 ? xaml60.Substring(Math.Max(0, heroBtns60 - 900), 900) : "";
        Check("横幅按钮行: WrapPanel 自动换行", heroWrap60.Contains("<WrapPanel"), "");
        // 2) 备份行按钮 MinWidth（不再固定 Width 硬切字）
        Check("备份行: 旧固定宽度按钮已全部改为 MinWidth",
            xaml60.Contains("<Button MinWidth=\"92\"")
            && xaml60.Contains("Content=\"改名\" MinWidth=\"46\"")
            && xaml60.Contains("Content=\"还原\" MinWidth=\"46\"")
            && xaml60.Contains("Content=\"删除\" MinWidth=\"46\"")
            && !xaml60.Contains("<Button Width=\"92\"")
            && !xaml60.Contains("Content=\"改名\" Width=\"46\""), "");
        // 3) 玩家筹码名字 MaxWidth+省略号+悬停全文
        Check("玩家筹码: 名字超长省略号 + ToolTip",
            xaml60.Contains("Text=\"{Binding Name}\" VerticalAlignment=\"Center\"")
            && xaml60.Contains("MaxWidth=\"108\" TextTrimming=\"CharacterEllipsis\""), "");
        // 6) 完结统计备份格省略号兜底
        Check("完结详情: 存档备份统计省略号兜底",
            xaml60.Contains("x:Name=\"CdStatBackups\"") && xaml60.Substring(xaml60.IndexOf("x:Name=\"CdStatBackups\"", StringComparison.Ordinal), 300).Contains("TextTrimming=\"CharacterEllipsis\""), "");
        // 7) 侧栏底部统计两列防重叠
        Check("侧栏底部: 计数/容量两列布局带省略号",
            xaml60.Contains("x:Name=\"SideCount\"") && xaml60.Contains("x:Name=\"SideSize\"")
            && xaml60.Substring(xaml60.IndexOf("x:Name=\"SideSize\"", StringComparison.Ordinal) - 200, 260).Contains("TextTrimming=\"CharacterEllipsis\""), "");
        // 8) Wiki 重试按钮与添加路径同行排列（旧硬编码 104 让位已删除）
        Check("存档位置: Wiki重试/添加路径同行不重叠",
            !xaml60.Contains("Margin=\"0,0,104,0\"") && xaml60.Contains("Content=\"⚠ Wiki 获取失败 · 点击重试\""), "");
        // 4) 云浏览：玩家/大小/时间单元格省略号，窄列加宽
        string cloudXaml60 = File.ReadAllText(Path.Combine(srcRoot60, "CloudBrowserWindow.xaml"));
        Check("云浏览: 玩家/大小/时间省略号兜底 + 玩家列加宽到 96",
            cloudXaml60.Contains("x:Key=\"CProfile\"") && cloudXaml60.Contains("Header=\"玩家\" Width=\"96\"")
            && cloudXaml60.Contains("Header=\"大小\" Width=\"82\"")
            && cloudXaml60.Substring(cloudXaml60.IndexOf("x:Key=\"CProfile\"", StringComparison.Ordinal), 180).Contains("TextTrimming=\"CharacterEllipsis\""), "");
        // 5) 未关联备份：内容/占用/最近文件列改为省略号 CellTemplate（不再 DisplayMemberBinding 硬切）
        string orphanXaml60 = File.ReadAllText(Path.Combine(srcRoot60, "OrphanBackupsWindow.xaml"));
        Check("未关联备份: 三列省略号 CellTemplate（无 DisplayMemberBinding 硬切）",
            !orphanXaml60.Contains("DisplayMemberBinding")
            && orphanXaml60.Contains("Text=\"{Binding DetailText}\"")
            && orphanXaml60.Contains("Text=\"{Binding SizeText}\""), "");
        // 5b) 未关联备份：五列固定总宽曾达 710 > 窄窗可用宽，操作列被裁；现首列弹性（SizeChanged 分配）
        string orphanCs60 = File.ReadAllText(Path.Combine(srcRoot60, "OrphanBackupsWindow.xaml.cs"));
        Check("未关联备份: 名称列弹性伸缩（SizeChanged），操作列窄窗不被裁",
            orphanXaml60.Contains("x:Name=\"ColName\"") && orphanXaml60.Contains("SizeChanged=\"OnOrphanListSizeChanged\"")
            && orphanXaml60.Contains("Header=\"操作\" Width=\"104\"")
            && !orphanXaml60.Contains("Header=\"操作\" Width=\"140\"")
            && orphanCs60.Contains("OnOrphanListSizeChanged"), "");
        // 完结回顾顶栏：右列 5 按钮在窄窗/宽侧栏下会挤出，改 WrapPanel 折行
        int cdWrap60 = xaml60.IndexOf("<WrapPanel Grid.Column=\"1\" HorizontalAlignment=\"Right\"", StringComparison.Ordinal);
        string cdBar60 = cdWrap60 >= 0 ? xaml60.Substring(cdWrap60, 1800) : "";
        Check("完结回顾顶栏: 按钮组 WrapPanel 自动折行",
            cdWrap60 >= 0 && cdBar60.Contains("x:Name=\"BtnCdLocate\"")
            && cdBar60.Contains("x:Name=\"BtnCdDeleteArchive\""), "");
        // 6b) 游玩详情：总时长/平均/近7/近30/导入日期数值省略号兜底
        string statsXaml60 = File.ReadAllText(Path.Combine(srcRoot60, "PlayStatsWindow.xaml"));
        Check("游玩详情: 六个数值格省略号兜底",
            new[] { "Text=\"{Binding Added}\"", "Text=\"{Binding Total}\"", "Text=\"{Binding Average}\"",
                    "Text=\"{Binding Week7}\"", "Text=\"{Binding Week30}\"" }
            .All(a => { int q = statsXaml60.IndexOf(a, StringComparison.Ordinal); return q >= 0 && statsXaml60.Substring(q, 200).Contains("TextTrimming=\"CharacterEllipsis\""); }), "");
        // 设置窗口：所有长说明文字必须自动换行（右侧有按钮列时不换行就会被切，备份目录那行曾被「浏览」截断）
        string settingsXaml60 = File.ReadAllText(Path.Combine(srcRoot60, "SettingsWindow.xaml"));
        string[] settingsHints60 = {
            "留空使用默认位置；更改后新备份存入新目录，已有备份仍在原位置、可正常还原",
            "每次打开软件时增量扫描，只新增不重复",
            "选择后立即生效并自动保存；「跟随系统」会在 Windows 深浅色切换时自动联动",
            "超出后自动删除最旧的备份（增量链不会被破坏）",
            "与上次备份对比，只打包有变化的文件，大幅节省空间",
            "防止增量链过长，全量是链的起点",
            "游戏库 games.json · 备份 backups\\ · 图标封面缓存 cache\\"
        };
        Check("设置窗口: 全部长说明文字自动换行（不被按钮/输入框截断）",
            settingsHints60.All(h => { int q = settingsXaml60.IndexOf(h, StringComparison.Ordinal); return q >= 0 && settingsXaml60.Substring(q, h.Length + 220).Contains("TextWrapping=\"Wrap\""); }), "");
        // 深色底上标题若漏写 Foreground 会回落成系统黑字（已踩过）：三个标题必须显式浅色
        bool TitleHasPrimary(string anchor)
        {
            int p = xaml60.IndexOf(anchor, StringComparison.Ordinal);
            return p >= 0 && xaml60.Substring(p, 160).Contains("{DynamicResource TextPrimary}");
        }
        Check("完结馆: 「完结馆/完结回顾/空状态」三个标题显式浅色（防黑字）",
            TitleHasPrimary("Text=\"完结馆\"")
            && TitleHasPrimary("Text=\"完结回顾\"")
            && TitleHasPrimary("Text=\"还没有完结的游戏\""), "");

        string mainCs60 = File.ReadAllText(Path.Combine(srcRoot60, "MainWindow.xaml.cs"));
        Check("完结馆/封面: 启动时执行 custom_ 封面 → {appId}.jpg 归一化迁移（后台 STA 不卡启动）",
            mainCs60.Contains("await DataMigration.NormalizeCustomCoversAsync()"), "");
        string editCs60 = File.ReadAllText(Path.Combine(srcRoot60, "EditGameWindow.xaml.cs"));
        Check("封面: 编辑窗手动传图按官方命名落盘（{appId}.jpg，非 jpg 转码 JPEG）",
            editCs60.Contains("SaveCoverCanonical")
            && editCs60.Contains("Store.CoverFileFor(appId)")
            && editCs60.Contains("DataMigration.EncodeJpeg")
            // 无 AppId 才允许退回 custom_ 兜底
            && editCs60.Contains(": CopyToCache(dlg.FileName, Store.CoversDir, \"custom_\" + _g.Id)"), "");
        // 共用封面保护：改 AppId/换图删旧缓存前必须过引用检查；本地图片一律文件流解码（# 路径安全）
        Check("封面: 删旧 {appId}.jpg 前检查其他条目引用（双装不互删封面）",
            editCs60.Contains("Store.IsImageReferencedByOthers"), "");
        Check("封面: 编辑窗不再用 UriSource 加载本地图片（含 # 路径会截断）",
            !editCs60.Contains("UriSource = new Uri"), "");
        Check("封面: 主窗口海报转码也不走 UriSource",
            !mainCs60.Contains("UriSource = new Uri"), "");
        string backupCs60 = File.ReadAllText(Path.Combine(srcRoot60, "Services", "BackupService.cs"));
        Check("未关联备份: 删残留后同步清条目死备份记录（含归档条目）",
            backupCs60.Contains("PruneRecordsUnder(full)")
            && backupCs60.Contains("CloudSyncService.OnLocalBackupsDeleted(g, deadFiles)"), "");
        string steamApiCs62 = File.ReadAllText(Path.Combine(srcRoot60, "Services", "SteamApi.cs"));
        Check("Steam: 商店资产查询带 10 分钟负缓存，老游戏不重复打必失败请求",
            steamApiCs62.Contains("StoreAssetsFailedAt")
            && steamApiCs62.Contains("StoreAssetsFailTtlMinutes"), "");
        string migCs60 = File.ReadAllText(Path.Combine(srcRoot60, "Services", "DataMigration.cs"));
        Check("封面: 归一化迁移实现（改名+转码+孤儿清理+幂等保存）",
            migCs60.Contains("public static List<string> NormalizeCustomCovers()")
            && migCs60.Contains("Store.CoverFileFor(g.AppId)")
            && migCs60.Contains("Directory.GetFiles(Store.CoversDir, \"custom_*\")"), "");
        Check("封面: 归一化迁移跳过被另一条目占用的 {appId}.jpg，转码走文件流，后台 STA 执行",
            migCs60.Contains("Store.IsImageReferencedByOthers(dest, g.Id)")
            && migCs60.Contains("bmp.StreamSource = fs")
            && !migCs60.Contains("UriSource = new Uri")
            && migCs60.Contains("NormalizeCustomCoversAsync"), "");
        Check("完结馆: 十个事件处理器全部接线",
            mainCs60.Contains("OnOpenCompleted") && mainCs60.Contains("OnCloseCompleted")
            && mainCs60.Contains("OnCompletedModeChanged") && mainCs60.Contains("OnCompletedCardClick")
            && mainCs60.Contains("OnCompletedEditDate") && mainCs60.Contains("OnCompletedUnmark")
            && mainCs60.Contains("OnCompletedDateChanged") && mainCs60.Contains("OnCompletedTodayClick")
            && mainCs60.Contains("OnMarkCompleted") && mainCs60.Contains("OnCompletedDoneClick"), "");
        Check("完结馆: 按年份分组只分年不拆月，年组标题带款数",
            mainCs60.Contains("CompletedGroupYear.IsChecked == true")
            && mainCs60.Contains("MakeCompletedYearGroupHeader(year.Key, list.Count)"), "");

        // ── 侧栏「已完结」快捷筛选 + 完结游戏列表沉底 ──
        Check("侧栏: 筛选行含「已完结」chip（无奖杯图标，与全部/Steam/本地版同组互斥）",
            xaml60.Contains("x:Name=\"FiltCompleted\"") && xaml60.Contains("Content=\"已完结\"")
            && !xaml60.Contains("Content=\"🏆 已完结\"")
            && xaml60.Contains("GroupName=\"ft\""), "");
        Check("筛选: completed 过滤只留已通关；归档仍先剔除",
            mainCs60.Contains("if (_filter == \"completed\" && !g.IsCompleted) return false;"), "");
        Check("排序: 已完结游戏沉底（仅次于收藏置顶），标记/取消完结即时 ApplyFilter 重排",
            mainCs60.Contains("if (a.IsCompleted != b.IsCompleted) return a.IsCompleted ? 1 : -1;"), "");
        // 卡片名字旁淡金小奖杯：矢量金杯（非 emoji）、绑 IsCompleted、淡金色取 MemorialGoldLo
        Check("列表卡片: 已完结游戏名字旁淡金矢量小奖杯（非 emoji，随主题变色）",
            xaml60.Contains("Visibility=\"{Binding IsCompleted, Converter={StaticResource BoolToVis}}\"")
            && xaml60.Contains("<SolidColorBrush Color=\"{DynamicResource MemorialGoldLo}\"/>")
            && xaml60.Contains("ToolTip=\"已完结\""), "");
        // 悬停性能：阴影模糊半径不得逐帧动画（每帧 CPU 重新高斯模糊 = 悬停掉帧主因）；
        // 半径瞬时定终值 + 只动画 Opacity + Performance 偏置 + 鼠标节流对齐 60Hz
        Check("悬停性能: 卡片阴影 BlurRadius 不做逐帧动画（瞬时终值+只淡 Opacity+Performance）",
            xaml60.Contains("x:Name=\"CardShadow\"") && xaml60.Contains("RenderingBias=\"Performance\"")
            && xaml60.Contains("Storyboard.TargetProperty=\"(DropShadowEffect.BlurRadius)\"")
            && xaml60.Contains("To=\"8\" Duration=\"0:0:0\""), "");
        Check("悬停性能: 鼠标节流 16ms（60Hz），阴影离开时延迟归零避免逐帧模糊",
            mainCs60.Contains("CardMoveThrottleMs = 16")
            && mainCs60.Contains("BeginTime = TimeSpan.FromMilliseconds(380)"), "");

        // ── 删除游戏保留完结档案（归档/复活/删档案）──────────────────────
        string entryCsArc = File.ReadAllText(Path.Combine(srcRoot60, "Models", "GameEntry.cs"));
        Check("归档: GameEntry.Archived 属性（带通知）",
            entryCsArc.Contains("public bool Archived")
            && entryCsArc.Contains("OnPropertyChanged(nameof(Archived))"), "");
        Check("归档: 已完结游戏删除时只归档不真删；未完结走原真删分支",
            mainCs60.Contains("bool keepArchive = g.IsCompleted")
            && mainCs60.Contains("g.Archived = true")
            && mainCs60.Contains("if (keepArchive)")
            // 真删 Store.Games.Remove(g) 仍保留在未完结分支
            && mainCs60.Contains("Store.Games.Remove(g);"), "");
        Check("归档: 列表过滤/进程监控/热键/Wiki回填/找回/统计均跳过归档条目",
            mainCs60.Contains("if (g.Archived) return false;")
            && mainCs60.Contains("Where(g => !g.Archived && g.ExePath.Length > 0)")
            && mainCs60.Contains("!x.Archived && x.ExePath.Length > 0")
            && mainCs60.Contains("if (g.Archived) continue;")
            && mainCs60.Contains("Store.Games.Any(g => !g.Archived)")
            && mainCs60.Contains("Store.Games.Where(g => !g.Archived).Sum"), "");
        Check("归档: Steam 扫描与添加向导命中归档条目时就地复活",
            mainCs60.Contains("if (existing.Archived)")
            && mainCs60.Contains("private async Task ReviveArchivedAsync(GameEntry g, FolderGameCandidate c)")
            && mainCs60.Contains("g.Archived = false"), "");
        string addWinCsArc = File.ReadAllText(Path.Combine(srcRoot60, "AddGamesWindow.xaml.cs"));
        Check("归档: 向导在库判定排除归档，允许重新勾选触发复活",
            addWinCsArc.Contains("Store.Games.Any(g => !g.Archived"), "");
        Check("归档: 完结馆卡片「已移出」角标 + 仅归档可见的「彻底删除档案」入口",
            xaml60.Contains("Text=\"已移出\"")
            && xaml60.Contains("Visibility=\"{Binding Archived, Converter={StaticResource BoolToVis}}\"")
            && xaml60.Contains("Click=\"OnCompletedDeleteArchive\"")
            && xaml60.Contains("Click=\"OnCompletedDetailDeleteArchive\"")
            && xaml60.Contains("BtnCdDeleteArchive"), "");
        Check("归档: DeleteCompletedArchive 二次确认后真删条目与自定义海报（官方共享缓存不删）",
            mainCs60.Contains("private void DeleteCompletedArchive(GameEntry g)")
            && mainCs60.Contains("彻底删除档案")
            && mainCs60.Contains("if (g.HasCustomPoster && File.Exists(g.PosterFile)) File.Delete(g.PosterFile)"), "");
        string storeCsArc = File.ReadAllText(Path.Combine(srcRoot60, "Services", "Store.cs"));
        Check("归档: MergeGame 合并时继承已删条目的完结日期与海报",
            storeCsArc.Contains("keep.CompletedAt == DateTime.MinValue && drop.CompletedAt != DateTime.MinValue")
            && storeCsArc.Contains("PosterCustomFileFor(keep.Id)"), "");
        string backupCsArc = File.ReadAllText(Path.Combine(srcRoot60, "Services", "BackupService.cs"));
        Check("归档: 未关联备份枚举把归档条目视同已移除（残留备份可见可删）",
            backupCsArc.Contains("Where(g => !g.Archived)"), "");
        string cloudCsArc = File.ReadAllText(Path.Combine(srcRoot60, "Services", "CloudSyncService.cs"));
        Check("归档: 自动云同步运营循环（对账/收集/批量上传/迁移开启）跳过归档",
            cloudCsArc.Contains("if (g.Archived || !g.CloudAuto) continue;")
            && cloudCsArc.Contains("if (!g.Archived) g.CloudAuto = true"), "");

        // ── 图片缓存复用：删游戏再添加零网络认领老图 ─────────────────────
        string steamApiCsArt = File.ReadAllText(Path.Combine(srcRoot60, "Services", "SteamApi.cs"));
        Check("图片复用: 下载器对有效本地缓存零网络短路（>1KB 判定，先于 7 主机轮试）",
            steamApiCsArt.Contains("if (File.Exists(saveTo) && new FileInfo(saveTo).Length > 1000) return true;"), "");
        Check("图片复用: Store 提供按 AppId 认领横封/竖海报/手换保留档的统一入口",
            storeCsArc.Contains("public static string PosterCustomAppFileFor(int appId)")
            && storeCsArc.Contains("public static bool IsUsableImage(string? file)")
            && storeCsArc.Contains("public static bool AdoptCachedArtwork(GameEntry g)")
            && storeCsArc.Contains("PosterCustomAppFileFor(g.AppId)")
            // 优先级：手换保留档 ＞ 官方竖图 ＞ 横封
            && storeCsArc.IndexOf("PosterCustomAppFileFor", StringComparison.Ordinal)
               < storeCsArc.IndexOf("PosterFileFor(g.AppId)", StringComparison.Ordinal), "");
        Check("图片复用: 四个入库/补图入口全部先认领再联网",
            mainCs60.Contains("await Dispatcher.InvokeAsync(() => Store.AdoptCachedArtwork(g))")
            && mainCs60.Contains("if (Store.AdoptCachedArtwork(g)) { Store.Save(); BuildCompletedHostIfOpen(); return; }")
            && mainCs60.Contains("Store.AdoptCachedArtwork(g)"), "");
        Check("图片复用: 真删游戏时手换海报改名 custom_app_{appId}.jpg 留档；换海报清旧档防孤儿",
            mainCs60.Contains("File.Move(g.PosterFile, keepPoster)")
            && mainCs60.Contains("PosterCustomAppFileFor(g.AppId)"), "");
        Check("完结馆: 月份分组构建/Esc 两级返回/馆内数量副标题/后台补海报/更新数据库补海报",
            mainCs60.Contains("BuildCompletedHost")
            && mainCs60.Contains("MakeCompletedMonthHeader")
            && mainCs60.Contains("g.CompletedAt.Year")
            && mainCs60.Contains("Key.Escape")
            && mainCs60.Contains("CloseCompletedDetail")
            && !mainCs60.Contains("UpdateCompletedCount")
            && mainCs60.Contains("SteamApi.DownloadPoster(g.AppId, path)")
            && mainCs60.Contains("g.IsCompleted && !g.HasPoster"), "");
        Check("海报: 启动补海报不限 Steam（手动入库 Local 游戏同样补，自定义海报不覆盖）",
            mainCs60.Contains("BackfillCompletedPostersAsync")
            && mainCs60.Contains("g.AppId > 0 && g.IsCompleted && !g.HasPoster && !g.HasCustomPoster")
            && mainCs60.Contains("BackfillCompletedPostersAsync(delayed: true)"), "");
        Check("完结馆: 点卡片进馆内回顾详情（不跳主列表）+ 五个详情操作 + 统计填充",
            mainCs60.Contains("OpenCompletedDetail(g)")
            && mainCs60.Contains("private void OpenCompletedDetail(GameEntry g)")
            && mainCs60.Contains("FillCompletedDetailStats")
            && mainCs60.Contains("PlayTracker.SecondsSince(g, DateTime.Now.AddDays(-7))")
            && mainCs60.Contains("OnCompletedDetailBack")
            && mainCs60.Contains("OnCompletedDetailRun")
            && mainCs60.Contains("OnCompletedDetailEditDate")
            && mainCs60.Contains("OnCompletedDetailUnmark")
            && mainCs60.Contains("OnCompletedDetailLocate")
            && mainCs60.Contains("LaunchGame(g)"), "");
        Check("完结馆: 悬停特效 ZIndex 同时兼容 ListBoxItem 与海报墙 ContentControl",
            mainCs60.Contains("FindAncestor<ContentControl>(root)")
            && mainCs60.Contains("private static FrameworkElement? CardZHost(FrameworkElement root)"), "");
        Check("完结馆: 海报卡真3D透视倾斜驱动（rotateX/rotateY/Z抬升/双轴暗角）",
            mainCs60.Contains("OnPosterTiltMove")
            && mainCs60.Contains("OnPosterTiltEnter")
            && mainCs60.Contains("OnPosterTiltLeave")
            && mainCs60.Contains("AxisAngleRotation3D")
            && mainCs60.Contains("PosterTiltMaxAngle")
            && mainCs60.Contains("TranslateTransform3D.OffsetZProperty")
            // 上下倾斜方向：鼠标靠上(ny小)→ ay 负 → 上边缘后仰（用户验收过的手感，防再被写反）
            && mainCs60.Contains("double ay = (ny - 0.5) * 2.0;"), "");

        // ── 海报自定义/兜底链 + 工具栏精简 ──
        Check("完结馆: 海报兜底链与自定义判定派生属性",
            entryCs60.Contains("public string PosterDisplayFile")
            && entryCs60.Contains("public bool HasPosterDisplay")
            && entryCs60.Contains("public bool HasCustomPoster"), "");
        Check("完结馆: 卡片/详情海报绑定走兜底链（不再直绑 PosterFile）+ 换海报入口",
            xaml60.Contains("{Binding PosterDisplayFile}")
            && !xaml60.Contains("{Binding PosterFile}")
            && xaml60.Contains("OnCompletedChangePoster")
            && xaml60.Contains("OnCompletedResetPoster")
            && xaml60.Contains("OnCompletedDetailChangePoster"), "");
        Check("完结馆: 换海报/恢复官方实现；自动补海报不得覆盖自定义",
            mainCs60.Contains("PickAndSaveCustomPoster")
            && mainCs60.Contains("ResetCustomPoster")
            && mainCs60.Contains("Store.PosterCustomFileFor(g.Id)")
            && mainCs60.Contains("g.AppId <= 0 || g.HasCustomPoster"), "");

        // ── UU 风沉浸式深色（仅详情/完结馆/完结回顾局部；三套主题必须同名键，防浅色白屏）──
        string uuDark60 = File.ReadAllText(Path.Combine(srcRoot60, "Themes", "Dark.xaml"));
        string uuLight60 = File.ReadAllText(Path.Combine(srcRoot60, "Themes", "Light.xaml"));
        string uuSoft60 = File.ReadAllText(Path.Combine(srcRoot60, "Themes", "LightSoft.xaml"));
        string uuScopePath60 = Path.Combine(srcRoot60, "Themes", "ImmersiveScope.xaml");
        string uuScope60 = File.Exists(uuScopePath60) ? File.ReadAllText(uuScopePath60) : "";
        string uuApp60 = File.ReadAllText(Path.Combine(srcRoot60, "App.xaml"));
        Check("沉浸式: Dark 定义深夜蓝黑底+青绿点睛（UuBackdropBrush / #13E2C8）",
            uuDark60.Contains("x:Key=\"UuBackdropBrush\"")
            && uuDark60.Contains("#13E2C8")
            && uuDark60.Contains("x:Key=\"UuRunBrush\"")
            && uuDark60.Contains("x:Key=\"UuGlowTealBrush\""), "");
        Check("沉浸式: Light/LightSoft 同名 Uu* 键齐全（切换主题不抛缺键白屏）",
            uuLight60.Contains("x:Key=\"UuBackdropBrush\"")
            && uuLight60.Contains("x:Key=\"UuRunBrush\"")
            && uuSoft60.Contains("x:Key=\"UuBackdropBrush\"")
            && uuSoft60.Contains("x:Key=\"UuRunBrush\""), "");
        Check("沉浸式: 艺术底透明度键三套主题齐全（深色1/浅色0）+ 压暗遮罩键",
            uuDark60.Contains("x:Key=\"UuArtOpacity\">1")
            && uuLight60.Contains("x:Key=\"UuArtOpacity\">0")
            && uuSoft60.Contains("x:Key=\"UuArtOpacity\">0")
            && uuDark60.Contains("x:Key=\"UuArtVeil\"")
            && uuLight60.Contains("x:Key=\"UuArtVeil\"")
            && uuSoft60.Contains("x:Key=\"UuArtVeil\""), "");
        Check("沉浸式: UU 玻璃规格——背景遮罩只压 60%（#99），卡片半透（Panel alpha<=#8C）",
            uuDark60.Contains("<Color x:Key=\"UuArtVeilC\">#990A1020</Color>")
            && uuDark60.Contains("LinearGradientBrush x:Key=\"UuArtVeil\"")
            && uuDark60.Contains("<GradientStop Color=\"#F50A1020\" Offset=\"0\"/>")
            && uuDark60.Contains("<Color x:Key=\"UuPanelC\">#8C172244</Color>")
            && uuDark60.Contains("<Color x:Key=\"UuPanel2C\">#661E2A52</Color>"), "");
        Check("沉浸式: 浅色运行按钮为纯色（UuRunBrush 两 stop 同色=原 AccentPrimary）",
            uuLight60.Split(new[]{"x:Key=\"UuRunBrush\""}, StringSplitOptions.None)[1].Contains("<GradientStop Color=\"#5690EE\" Offset=\"0\"/>")
            && uuLight60.Split(new[]{"x:Key=\"UuRunBrush\""}, StringSplitOptions.None)[1].Contains("<GradientStop Color=\"#5690EE\" Offset=\"1\"/>")
            && uuSoft60.Split(new[]{"x:Key=\"UuRunBrush\""}, StringSplitOptions.None)[1].Contains("<GradientStop Color=\"#4C86E6\" Offset=\"1\"/>"), "");
        Check("沉浸式: ImmersiveScope 只覆盖表面/文字/行态；品牌色不覆盖（筹码/Chip 保持全局宝蓝），列表选中卡面走淡青玻璃",
            uuScope60.Contains("x:Key=\"Panel\"")
            && uuScope60.Contains("x:Key=\"BorderBrush\"")
            && uuScope60.Contains("x:Key=\"TextPrimary\"")
            && uuScope60.Contains("x:Key=\"CardSelectedBg\"")
            && !uuScope60.Contains("x:Key=\"Accent\"")
            && !uuScope60.Contains("x:Key=\"AccentPrimary\"")
            && !uuScope60.Contains("UuTealC")
            && !uuScope60.Contains("x:Key=\"ItemSelectedBg\""), "");
        Check("沉浸式: ImmersiveScope 改为运行时按主题动态挂载（XAML 不得静态合并，防浅色串色）",
            !xaml60.Contains("Source=\"Themes/ImmersiveScope.xaml\"")
            && xaml60.Contains("Loaded=\"OnImmersiveRootLoaded\"")
            && xaml60.Contains("x:Name=\"DetailImmersiveRoot\""), "");
        string winCs60 = File.ReadAllText(Path.Combine(srcRoot60, "MainWindow.xaml.cs"));
        Check("沉浸式: code-behind 仅深色挂载覆盖字典，沉浸根 Loaded/Unloaded 对称登记，切主题统一刷新",
            winCs60.Contains("component/Themes/ImmersiveScope.xaml")
            && winCs60.Contains("ThemeManager.IsDark")
            && winCs60.Contains("ThemeManager.ThemeChanged += RefreshImmersiveScopes")
            && winCs60.Contains("private void ApplyImmersiveScope(FrameworkElement root)")
            && winCs60.Contains("_immersiveRoots")
            && winCs60.Contains("OnImmersiveRootUnloaded"), "");
        Check("沉浸式: 详情/完结馆/完结回顾三处接入沉浸底，运行键走青绿样式",
            xaml60.Contains("x:Name=\"CompletedView\" Grid.Row=\"2\" Background=\"{DynamicResource UuHallBackdropBrush}\"")
            && xaml60.Contains("x:Name=\"CompletedDetail\" Grid.Row=\"1\" Background=\"{DynamicResource UuBackdropBrush}\"")
            && xaml60.Contains("Background=\"{DynamicResource UuBackdropBrush}\"")
            && xaml60.Contains("Style=\"{StaticResource BtnUuTeal}\"")
            && xaml60.Contains("x:Name=\"BtnRun\"")
            && xaml60.Contains("x:Name=\"BtnCdRun\""), "");
        Check("沉浸式: 壁纸两处（全窗口通铺+完结回顾详情整层覆盖），收藏墙不得内嵌；UuArtOpacity 四处受控",
            xaml60.Split(new[]{"Source=\"/GameSaveVault;component/Assets/immersive_wallpaper.png\""}, StringSplitOptions.None).Length == 3
            && xaml60.Split(new[]{"Opacity=\"{DynamicResource UuArtOpacity}\""}, StringSplitOptions.None).Length == 5
            && !xaml60.Contains("BlurEffect")
            && !xaml60.Contains("ScaleX=\"1.06\"")
            && xaml60.Contains("Background=\"{DynamicResource UuArtVeil}\""), "");
        Check("沉浸式: 完结馆收藏墙全透（专用 UuHallBackdropBrush），完结回顾详情深色不透明渐变（防收藏墙海报透出）",
            uuDark60.Contains("LinearGradientBrush x:Key=\"UuBackdropBrush\"")
            && uuDark60.Contains("<SolidColorBrush x:Key=\"UuHallBackdropBrush\" Color=\"#00000000\"/>")
            && uuLight60.Contains("LinearGradientBrush x:Key=\"UuHallBackdropBrush\"")
            && uuSoft60.Contains("LinearGradientBrush x:Key=\"UuHallBackdropBrush\""), "");
        Check("沉浸式: ToolTip 使用专用不透明 ToolTipBg/ToolTipBorder，三主题均不透明且 ImmersiveScope 不得覆盖（馆内提示不玻璃化）",
            xaml60.Contains("Background=\"{DynamicResource ToolTipBg}\"")
            && xaml60.Contains("BorderBrush=\"{DynamicResource ToolTipBorder}\"")
            && uuDark60.Contains("<SolidColorBrush x:Key=\"ToolTipBg\" Color=\"#20242E\"/>")
            && uuDark60.Contains("<SolidColorBrush x:Key=\"ToolTipBorder\" Color=\"#2A2F3B\"/>")
            && uuLight60.Contains("<SolidColorBrush x:Key=\"ToolTipBg\" Color=\"#F4F0E8\"/>")
            && uuSoft60.Contains("<SolidColorBrush x:Key=\"ToolTipBg\" Color=\"#EEF1F5\"/>")
            && !uuScope60.Contains("ToolTipBg")
            && !uuScope60.Contains("ToolTipBorder"), "");
        Check("沉浸式: 完结馆全透底色防透出——主体 Grid 命名 MainBody，打开/关闭完结馆对称 Hidden/Visible（保留滚动位置）",
            xaml60.Contains("<Grid x:Name=\"MainBody\" Grid.Row=\"2\" ClipToBounds=\"True\">")
            && winCs60.Contains("MainBody.Visibility = Visibility.Hidden;")
            && winCs60.Contains("MainBody.Visibility = Visibility.Visible;"), "");
        Check("沉浸式: 壁纸通铺到标题栏与状态栏——RootGrid 跨四行单一艺术层，侧栏/空态/三栏底色深色全透、浅色不透明",
            xaml60.Contains("Grid.RowSpan=\"4\" Opacity=\"{DynamicResource UuArtOpacity}\"")
            && !xaml60.Contains("Grid.ColumnSpan=\"4\" Opacity=\"{DynamicResource UuArtOpacity}\"")
            && xaml60.Contains("Background=\"{DynamicResource UuArtVeilFlat}\"")
            && xaml60.Contains("Background=\"{DynamicResource UuArtVeilTop}\"")
            && xaml60.Contains("x:Name=\"SideBar\" Background=\"{DynamicResource SideBarBg}\"")
            && xaml60.Contains("x:Name=\"EmptyState\" Background=\"{DynamicResource EmptyStateBg}\"")
            && xaml60.Contains("x:Name=\"DetailImmersiveRoot\"")
            && xaml60.Contains("VerticalScrollBarVisibility=\"Auto\" Background=\"Transparent\"")
            && uuDark60.Contains("<SolidColorBrush x:Key=\"SideBarBg\" Color=\"#00000000\"/>")
            && uuDark60.Contains("<SolidColorBrush x:Key=\"EmptyStateBg\" Color=\"#00000000\"/>")
            && uuDark60.Contains("<SolidColorBrush x:Key=\"ChromeBarBg\" Color=\"#00000000\"/>")
            && uuDark60.Contains("<SolidColorBrush x:Key=\"ChromeBarBorder\" Color=\"#00000000\"/>")
            && uuDark60.Contains("LinearGradientBrush x:Key=\"UuBackdropBrush\"")
            && uuDark60.Contains("<SolidColorBrush x:Key=\"UuHallBackdropBrush\" Color=\"#00000000\"/>")
            && uuLight60.Contains("LinearGradientBrush x:Key=\"UuBackdropBrush\"")
            && uuSoft60.Contains("LinearGradientBrush x:Key=\"UuBackdropBrush\"")
            && uuDark60.Contains("x:Key=\"UuArtVeilFlat\"")
            && uuDark60.Contains("x:Key=\"UuArtVeilTop\"")
            && uuLight60.Contains("<SolidColorBrush x:Key=\"SideBarBg\" Color=\"#FFFFFFFF\"/>")
            && uuLight60.Contains("<SolidColorBrush x:Key=\"EmptyStateBg\" Color=\"#FFF3EFE6\"/>")
            && uuLight60.Contains("<SolidColorBrush x:Key=\"ChromeBarBg\" Color=\"#FFFFFFFF\"/>")
            && uuLight60.Contains("<SolidColorBrush x:Key=\"ChromeBarBorder\" Color=\"#FFE2DAC9\"/>")
            && uuLight60.Contains("x:Key=\"UuArtVeilFlat\"")
            && uuSoft60.Contains("<SolidColorBrush x:Key=\"SideBarBg\" Color=\"#FFFFFFFF\"/>")
            && uuSoft60.Contains("<SolidColorBrush x:Key=\"EmptyStateBg\" Color=\"#FFF4F6F8\"/>")
            && uuSoft60.Contains("<SolidColorBrush x:Key=\"ChromeBarBorder\" Color=\"#FFDDE2E9\"/>")
            && uuSoft60.Contains("x:Key=\"UuArtVeilTop\""), "");
        Check("沉浸式: WindowChrome 自绘标题栏（系统按钮关闭，三颗自绘按钮跟随主题），32+52+28 行高，最大化防裁切；CaptionBtn 样式已提升 App.xaml 全局",
            xaml60.Contains("<WindowChrome CaptionHeight=\"32\"")
            && xaml60.Contains("GlassFrameThickness=\"0,0,0,1\"")
            && xaml60.Contains("UseAeroCaptionButtons=\"False\"")
            && xaml60.Contains("x:Name=\"CaptionMinBtn\"")
            && xaml60.Contains("x:Name=\"CaptionMaxBtn\"")
            && xaml60.Contains("x:Name=\"CaptionCloseBtn\"")
            && xaml60.Contains("x:Name=\"CaptionMaxGlyph\"")
            && xaml60.Contains("Click=\"OnCaptionMinimize\"")
            && xaml60.Contains("Click=\"OnCaptionToggleMax\"")
            && xaml60.Contains("Click=\"OnCaptionClose\"")
            && uuApp60.Contains("<Style x:Key=\"CaptionBtn\" TargetType=\"Button\">")
            && uuApp60.Contains("<Style x:Key=\"CaptionCloseBtn\" TargetType=\"Button\" BasedOn=\"{StaticResource CaptionBtn}\">")
            && uuApp60.Contains("WindowChrome.IsHitTestVisibleInChrome\" Value=\"True\"")
            && uuApp60.Contains("Value=\"{DynamicResource CaptionPressedBg}\"")
            && uuApp60.Contains("Value=\"{DynamicResource CaptionCloseHoverBg}\"")
            && !xaml60.Contains("<Style x:Key=\"CaptionBtn\"")
            && uuDark60.Contains("<SolidColorBrush x:Key=\"CaptionGlyph\" Color=\"#F0FFFFFF\"/>")
            && uuDark60.Contains("<SolidColorBrush x:Key=\"CaptionHoverBg\" Color=\"#2EFFFFFF\"/>")
            && uuDark60.Contains("<SolidColorBrush x:Key=\"CaptionPressedBg\" Color=\"#40FFFFFF\"/>")
            && uuDark60.Contains("<SolidColorBrush x:Key=\"CaptionCloseHoverBg\" Color=\"#BFE81123\"/>")
            && uuLight60.Contains("<SolidColorBrush x:Key=\"CaptionGlyph\" Color=\"#CC1A1A1A\"/>")
            && uuLight60.Contains("<SolidColorBrush x:Key=\"CaptionPressedBg\" Color=\"#26000000\"/>")
            && uuSoft60.Contains("<SolidColorBrush x:Key=\"CaptionHoverBg\" Color=\"#1A000000\"/>")
            && xaml60.Contains("<RowDefinition Height=\"32\"/>")
            && xaml60.Contains("<RowDefinition Height=\"52\"/>")
            && xaml60.Contains("<RowDefinition Height=\"28\"/>")
            && xaml60.Contains("Text=\"GameSaveVault\" FontSize=\"12\"")
            && xaml60.Contains("Background=\"{DynamicResource UuChromeScrim}\"")
            && uuDark60.Contains("<LinearGradientBrush x:Key=\"UuChromeScrim\"")
            && uuDark60.Contains("<GradientStop Color=\"#A60A1020\" Offset=\"0\"/>")
            && winCs60.Contains("WindowState.Maximized ? new Thickness(6)")
            && winCs60.Contains("OnCaptionToggleMax")
            && winCs60.Contains("\"\\uE923\""), "");

        // ── 子窗口统一自绘标题栏（方案 B：6 个常用大窗与主窗口同款 CaptionBar） ──
        string capBarXaml60 = File.ReadAllText(Path.Combine(srcRoot60, "CaptionBar.xaml"));
        string capBarCs60 = File.ReadAllText(Path.Combine(srcRoot60, "CaptionBar.xaml.cs"));
        Check("子窗口统一标题栏: 通用 CaptionBar 与主窗口同款（32px、Panel 同色底+1px 分隔线、MDL2 三键、直角 Chrome、最大化补偿、NoResize 自适应）",
            capBarXaml60.Contains("Height=\"32\"")
            && capBarXaml60.Contains("Background=\"{DynamicResource Panel}\"")
            && capBarXaml60.Contains("BorderBrush=\"{DynamicResource BorderBrush}\"")
            && capBarXaml60.Contains("BorderThickness=\"0,0,0,1\"")
            && capBarXaml60.Contains("x:Name=\"BtnMin\"")
            && capBarXaml60.Contains("x:Name=\"BtnMax\"")
            && capBarXaml60.Contains("x:Name=\"BtnClose\"")
            && capBarXaml60.Contains("x:Name=\"GlyphMax\"")
            && capBarXaml60.Contains("Style=\"{StaticResource CaptionBtn}\"")
            && capBarXaml60.Contains("Style=\"{StaticResource CaptionCloseBtn}\"")
            && capBarXaml60.Contains("&#xE921;")
            && capBarXaml60.Contains("&#xE922;")
            && capBarXaml60.Contains("&#xE8BB;")
            && capBarCs60.Contains("WindowChrome.SetWindowChrome")
            && capBarCs60.Contains("CaptionHeight = 32")
            && capBarCs60.Contains("GlassFrameThickness = new Thickness(0, 0, 0, 1)")
            && capBarCs60.Contains("CornerRadius = new CornerRadius(0)")
            && capBarCs60.Contains("UseAeroCaptionButtons = false")
            && capBarCs60.Contains("ResizeMode.NoResize ? Visibility.Collapsed")
            && capBarCs60.Contains("ResizeMode.CanResize ? Visibility.Visible")
            && capBarCs60.Contains("WindowState.Maximized")
            && capBarCs60.Contains("new Thickness(6)")
            && capBarCs60.Contains("\"\\uE923\"")
            && capBarCs60.Contains("_win?.Close()"), "");
        string[] capWindows60 =
        {
            "AddGamesWindow.xaml", "SettingsWindow.xaml", "CloudBrowserWindow.xaml",
            "EditGameWindow.xaml", "OrphanBackupsWindow.xaml", "PlayStatsWindow.xaml"
        };
        foreach (string capFile in capWindows60)
        {
            string capText = File.ReadAllText(Path.Combine(srcRoot60, capFile));
            Check($"子窗口统一标题栏: {capFile} 两行包装并在 Row 0 挂 CaptionBar（不再使用系统原生标题栏）",
                capText.Contains("xmlns:local=\"clr-namespace:GameSaveManager\"")
                && capText.Contains("<local:CaptionBar Grid.Row=\"0\"/>")
                && !capText.Contains("<WindowChrome.WindowChrome>"), capFile);
        }
        string editGameXaml60 = File.ReadAllText(Path.Combine(srcRoot60, "EditGameWindow.xaml"));
        Check("子窗口统一标题栏: 编辑游戏为 NoResize 固定窗（CaptionBar 只显关闭键），补窗口图标；内容超高时行 1 为 * + ScrollViewer 兜底，底部按钮不被裁切",
            editGameXaml60.Contains("ResizeMode=\"NoResize\"")
            && editGameXaml60.Contains("Icon=\"Assets/app.ico\"")
            && editGameXaml60.Contains("<ScrollViewer Grid.Row=\"1\" VerticalScrollBarVisibility=\"Auto\">")
            && editGameXaml60.Contains("<StackPanel Margin=\"24,20,24,20\">")
            && editGameXaml60.Contains("<RowDefinition Height=\"*\"/>"), "");
        string addGamesCs60 = File.ReadAllText(Path.Combine(srcRoot60, "AddGamesWindow.xaml.cs"));
        Check("子窗口统一标题栏: 添加游戏窗口按主题染 DWM 边框（回归守卫：曾漏调导致深色下白色原生标题栏）",
            addGamesCs60.Contains("DarkTitleBar.SetTheme(this, ThemeManager.IsDark)"), "");
        Check("沉浸式: 未选游戏时空态不得透出详情鬼影（DetailHost Content=null 折叠，紫色占位块不渲染）",
            xaml60.Contains("<Trigger Property=\"Content\" Value=\"{x:Null}\">")
            && xaml60.Contains("<Setter Property=\"Visibility\" Value=\"Collapsed\"/>"), "");
        Check("沉浸式: 工具栏六颗按钮统一 40% 半透明玻璃样式（含扫描 Steam 不再蓝底；深色透壁纸，浅色=常态不透明零变化，全局 Btn 不被污染）",
            uuApp60.Contains("<Style x:Key=\"BtnTool\" TargetType=\"Button\">")
            && uuApp60.Contains("Value=\"{DynamicResource ToolBtnBg}\"")
            && uuApp60.Contains("Value=\"{DynamicResource ToolBtnHoverBg}\"")
            && !xaml60.Contains("BtnToolPrimary")
            && !uuApp60.Contains("BtnToolPrimary")
            && !uuDark60.Contains("ToolAccentPrimary")
            && !uuDark60.Contains("ToolAccentHover")
            && !uuLight60.Contains("ToolAccentPrimary")
            && !uuSoft60.Contains("ToolAccentPrimary")
            && (new System.Text.RegularExpressions.Regex(
                "Style=\"\\{StaticResource BtnTool\\}\"").Matches(xaml60).Count == 6)
            && uuDark60.Contains("<SolidColorBrush x:Key=\"ToolBtnBg\" Color=\"#661E2A52\"/>")
            && uuDark60.Contains("<SolidColorBrush x:Key=\"ToolBtnBorder\" Color=\"#33FFFFFF\"/>")
            && uuDark60.Contains("<SolidColorBrush x:Key=\"ToolBtnHoverBg\" Color=\"#9932426E\"/>")
            && uuLight60.Contains("<SolidColorBrush x:Key=\"ToolBtnBg\" Color=\"#F4F0E8\"/>")
            && uuSoft60.Contains("<SolidColorBrush x:Key=\"ToolBtnBg\" Color=\"#EEF1F5\"/>"), "");
        Check("沉浸式: 全局主题底色未被改动（只允许局部换肤，Bg 仍是炭黑；侧栏白只来自 SideBarBg 浅色键）",
            uuDark60.Contains("<SolidColorBrush x:Key=\"Bg\" Color=\"#12141A\"/>")
            && uuDark60.Contains("<SolidColorBrush x:Key=\"Panel\" Color=\"#1A1D25\"/>"), "");
        Check("沉浸式: 统一壁纸 PNG 已作为 Resource 编入程序集",
            File.Exists(Path.Combine(srcRoot60, "Assets", "immersive_wallpaper.png"))
            && File.ReadAllText(Path.Combine(srcRoot60, "GameSaveManager.csproj")).Contains("<Resource Include=\"Assets\\immersive_wallpaper.png\" />"), "");
        Check("沉浸式: 完结回顾运行按钮保持原 BtnSm（浅色零变化），仅横幅运行键用 BtnUuTeal",
            xaml60.Contains("x:Name=\"BtnCdRun\" Style=\"{StaticResource BtnSm}\"")
            && xaml60.Split(new[]{"BtnUuTeal"}, StringSplitOptions.None).Length == 2, "");
        Check("沉浸式: App.xaml 定义 BtnUuTeal（青绿渐变+辉光，浅色回退常态宝蓝）",
            uuApp60.Contains("x:Key=\"BtnUuTeal\"")
            && uuApp60.Contains("DynamicResource UuRunBrush")
            && uuApp60.Contains("DynamicResource UuTealGlowColor"), "");

        // 工具栏：扫描文件夹 + 手动添加 合并为一个向导；低频项收进「⋯ 更多」
        string addWinXaml60 = Path.Combine(srcRoot60, "AddGamesWindow.xaml");
        string addWinCs60 = Path.Combine(srcRoot60, "AddGamesWindow.xaml.cs");
        Check("工具栏: 添加游戏统一向导窗口（文件夹扫描 + 手动 exe + 勾选清单）",
            File.Exists(addWinXaml60) && File.Exists(addWinCs60)
            && File.ReadAllText(addWinCs60).Contains("FolderScanner.Scan(")
            && File.ReadAllText(addWinCs60).Contains("FolderScanner.CandidateFromExe")
            && File.ReadAllText(addWinCs60).Contains("public List<FolderGameCandidate> SelectedPicks")
            && File.ReadAllText(addWinXaml60).Contains("CandidateList"), "");
        Check("工具栏: 只剩单一添加入口 OnAddGameWizard，旧按钮与旧处理器已删除",
            xaml60.Contains("Click=\"OnAddGameWizard\"")
            && mainCs60.Contains("private async void OnAddGameWizard")
            && mainCs60.Contains("AddLocalGameEntryAsync")
            && !xaml60.Contains("OnScanFolder")
            && !mainCs60.Contains("private async void OnScanFolder")
            && !mainCs60.Contains("private async void OnAddGame(")
            && !xaml60.Contains("扫描文件夹")
            && !xaml60.Contains("📂 备份管理")
            && !xaml60.Contains("🌐 更新数据库"), "");
        Check("工具栏: 「⋯更多」收纳未关联备份/更新存档规则；界面不出现「孤儿」外号",
            xaml60.Contains("OnMoreMenuClick")
            && mainCs60.Contains("private void OnMoreMenuClick")
            && mainCs60.Contains("🗂 未关联备份")
            && !xaml60.Contains("孤儿")
            && File.ReadAllText(Path.Combine(srcRoot60, "OrphanBackupsWindow.xaml"))
                .Contains("Title=\"未关联备份 · GameSaveVault\""), "");
    }

    Store.HomeDirOverride = null;
}

// ===== 零污染守卫：全程不得写真实库（games.json/settings.json 指纹不变、备份根不新增目录）=====
Check("沙箱守卫: 冒烟全程未改写真实 %APPDATA% 的 games.json",
    !File.Exists(realGamesFile) || File.GetLastWriteTime(realGamesFile) == realGamesMtime,
    $"before={realGamesMtime:HH:mm:ss}, after={File.GetLastWriteTime(realGamesFile):HH:mm:ss}");
Check("沙箱守卫: 冒烟全程未改写真实 %APPDATA% 的 settings.json",
    !realSettingsExisted
        ? !File.Exists(realSettingsFile)
        : File.GetLastWriteTime(realSettingsFile) == realSettingsMtime
          && FileFingerprint(realSettingsFile) == realSettingsHash,
    $"before={realSettingsMtime:HH:mm:ss}/{realSettingsHash[..12]}, "
    + $"after={(File.Exists(realSettingsFile) ? File.GetLastWriteTime(realSettingsFile).ToString("HH:mm:ss") + "/" + FileFingerprint(realSettingsFile)[..12] : "missing")}");
var realBackupDirsAfter = Directory.Exists(realBackupRoot)
    ? Directory.GetDirectories(realBackupRoot).Select(p => Path.GetFileName(p)!).ToHashSet()
    : new HashSet<string>();
var leaked = realBackupDirsAfter.Except(realBackupDirsBefore).ToList();
Check("沙箱守卫: 真实备份根未新增冒烟测试目录",
    leaked.Count == 0, string.Join(",", leaked));
var strayAfter = RealVaultStrayFiles().ToHashSet();
var strayLeaked = strayAfter.Except(realStrayBefore).ToList();
Check("沙箱守卫: 真实库根未新增 settings.guard/settings.corrupt 残留",
    strayLeaked.Count == 0, string.Join(",", strayLeaked));

// ===== 性能修复守卫：启动/后台重活不得回到 UI 线程（点筛选按钮鼠标迟缓回归防护）=====
string perfRoot61 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string perfMain61 = File.ReadAllText(Path.Combine(perfRoot61, "src", "GameSaveManager", "MainWindow.xaml.cs"));
string perfWiki61 = File.ReadAllText(Path.Combine(perfRoot61, "src", "GameSaveManager", "Services", "WikiService.cs"));
string perfXaml61 = File.ReadAllText(Path.Combine(perfRoot61, "src", "GameSaveManager", "MainWindow.xaml"));

// WikiService 正则必须全部预编译（静态缓存槽位不足会在 90 款回填时反复重编译冻 UI）
Check("性能守卫: WikiService 正则已预编译（Rx* 字段 + RegexOptions.Compiled）",
    perfWiki61.Contains("RxHtmlComment") && perfWiki61.Contains("RegexOptions.Compiled"), "");
int bareRegex61 = perfWiki61.Split('\n')
    .Count(l => l.Contains("new Regex(") && !l.Contains("RegexOptions.Compiled"));
Check("性能守卫: WikiService 无未编译的裸 new Regex", bareRegex61 == 0, $"bare={bareRegex61}");
Check("性能守卫: WikiService 不使用静态 Regex 方法（模式串无法预编译）",
    !perfWiki61.Contains("Regex.Replace(") && !perfWiki61.Contains("Regex.Match(")
    && !perfWiki61.Contains("Regex.IsMatch(") && !perfWiki61.Contains("Regex.Split("), "");

// 每秒进程枚举必须在后台线程，且有重入保护（Process.GetProcesses 系统繁忙时可冻 UI 1 秒+）
int watchPos61 = perfMain61.IndexOf("private async void WatchTick", StringComparison.Ordinal);
string watchBody61 = watchPos61 >= 0 ? perfMain61.Substring(watchPos61, 3000) : "";
Check("性能守卫: WatchTick 进程枚举放 Task.Run 后台执行",
    watchBody61.Contains("await Task.Run") && watchBody61.Contains("Process.GetProcesses()"), "");
Check("性能守卫: WatchTick 有 _watchTickBusy 重入保护",
    watchBody61.Contains("_watchTickBusy") && watchBody61.Contains("Interlocked.Exchange"), "");

// 联网解析链必须脱离 UI 同步上下文
int enrichPos61 = perfMain61.IndexOf("EnrichWikiAsync(GameEntry g", StringComparison.Ordinal);
string enrichBody61 = enrichPos61 >= 0 ? perfMain61.Substring(enrichPos61, 2500) : "";
Check("性能守卫: EnrichWikiAsync 联网解析在线程池（Task.Run + ConfigureAwait）",
    enrichBody61.Contains("Task.Run(() => WikiService.EnrichAsync")
    && enrichBody61.Contains("ConfigureAwait(false)"), "");
int matchPos61 = perfMain61.IndexOf("private async Task MatchLocalGameAsync", StringComparison.Ordinal);
string matchBody61 = matchPos61 >= 0 ? perfMain61.Substring(matchPos61, 1500) : "";
Check("性能守卫: MatchLocalGameAsync 开头强制脱离 UI 同步上下文",
    matchBody61.Contains("await Task.Run(() => { }).ConfigureAwait(false)"), "");

// Popup/ToolTip 冷启动预热必须保留（透明分层 HWND 首开同步卡 UI 1 秒+）
Check("性能守卫: 启动预热主题 Popup 与 ToolTip",
    perfMain61.Contains("ThemePopup.IsOpen = true;") && perfMain61.Contains("warmTip.IsOpen = true;"), "");

// ToolTip 全局样式必须轻量：无 Effect（软件光栅阴影在空转帧里持续吃 CPU）
int tipStylePos61 = perfXaml61.IndexOf("<Style TargetType=\"ToolTip\">", StringComparison.Ordinal);
int tipStyleEnd61 = tipStylePos61 >= 0 ? perfXaml61.IndexOf("</Style>", tipStylePos61, StringComparison.Ordinal) : -1;
string tipStyle61 = tipStyleEnd61 > tipStylePos61
    ? perfXaml61.Substring(tipStylePos61, tipStyleEnd61 - tipStylePos61) : "";
Check("性能守卫: XAML 含全局轻量 ToolTip 样式且样式内无 Effect",
    tipStyle61.Length > 0 && !tipStyle61.Contains("Effect"), "");

// 临时帧诊断插桩必须清理干净：订阅 CompositionTarget.Rendering 会强制渲染循环永不休眠
Check("性能守卫: DIAG 帧探针已全部清理（Rendering 订阅会自污染渲染循环）",
    !perfMain61.Contains("CompositionTarget.Rendering") && !perfMain61.Contains("DIAG")
    && !perfMain61.Contains("gsm_framelog") && !perfMain61.Contains("gsm_animclock"), "");

// 游戏列表必须开虚拟化+容器回收：卡片模板重（DropShadow/高质量图标/Storyboard），
// 89 款全量实例化会让切筛选 chip 冻几秒；固定行高 62 与虚拟化完全兼容
int glPos61 = perfXaml61.IndexOf("<ListBox x:Name=\"GamesList\"", StringComparison.Ordinal);
string glHead61 = glPos61 >= 0 ? perfXaml61.Substring(glPos61, 400) : "";
Check("性能守卫: 游戏列表开启虚拟化与容器回收（切筛选不卡）",
    glHead61.Contains("VirtualizingStackPanel.IsVirtualizing=\"True\"")
    && glHead61.Contains("VirtualizingStackPanel.VirtualizationMode=\"Recycling\"")
    && !glHead61.Contains("IsVirtualizing=\"False\""), "");

// 主题弹窗 4 个小 chip 保留外圈描边（含选中态蓝边）；要去描边的是其锚点🌙大按钮
int chipSmPos61 = perfXaml61.IndexOf("<Style x:Key=\"ChipSm\"", StringComparison.Ordinal);
int chipSmEnd61 = chipSmPos61 >= 0 ? perfXaml61.IndexOf("</Style>", chipSmPos61, StringComparison.Ordinal) : -1;
string chipSm61 = chipSmEnd61 > chipSmPos61 ? perfXaml61.Substring(chipSmPos61, chipSmEnd61 - chipSmPos61) : "";
Check("视觉守卫: 主题弹窗 ChipSm 保留描边与选中蓝边",
    chipSm61.Contains("BorderBrush=\"{DynamicResource BorderBrush}\"")
    && chipSm61.Contains("Property=\"BorderBrush\" Value=\"{DynamicResource Accent}\""), "");
// 🌙大按钮本身无描边：默认透明，悬停也不出现蓝边（只出悬停底色）
int themeBtnPos61 = perfXaml61.IndexOf("<Button x:Name=\"ThemeBtn\"", StringComparison.Ordinal);
int themeBtnTplEnd61 = themeBtnPos61 >= 0 ? perfXaml61.IndexOf("</Button.Template>", themeBtnPos61, StringComparison.Ordinal) : -1;
string themeBtnTpl61 = themeBtnTplEnd61 > themeBtnPos61
    ? perfXaml61.Substring(themeBtnPos61, themeBtnTplEnd61 - themeBtnPos61) : "";
Check("视觉守卫: 🌙主题大按钮无描边（默认与悬停均无边框）",
    themeBtnTpl61.Contains("BorderBrush=\"Transparent\"")
    && !themeBtnTpl61.Contains("Value=\"{DynamicResource Accent}\""), "");
// 主题弹窗外圈大边框去掉（包住 4 个 chip 的那圈），chip 自身描边保留
int popupBodyPos61 = perfXaml61.IndexOf("<Border x:Name=\"ThemePopupBody\"", StringComparison.Ordinal);
string popupBodyHead61 = popupBodyPos61 >= 0 ? perfXaml61.Substring(popupBodyPos61, 220) : "";
Check("视觉守卫: 主题弹窗外圈无描边（仅面板底色）",
    popupBodyHead61.Contains("BorderThickness=\"0\"")
    && !popupBodyHead61.Contains("BorderBrush="), "");

// 浅色主题横幅可读性：封面底部恒叠 #EE 黑遮罩（与主题无关的暗底），横幅按钮/徽章/已完结字
// 必须用固定浅色，不得绑 TextSecondary 等为白底设计的主题色，否则浅色主题下压黑底发糊
string perfApp61 = File.ReadAllText(Path.Combine(perfRoot61, "src", "GameSaveManager", "App.xaml"));
Check("视觉守卫: App.xaml 定义横幅暗底专用 BtnSmOnHero（固定浅色玻璃样式）",
    perfApp61.Contains("x:Key=\"BtnSmOnHero\"")
    && perfApp61.Contains("Foreground\" Value=\"#EDF1F8\"")
    && perfApp61.Contains("BorderBrush\" Value=\"#5CFFFFFF\""), "");
Check("视觉守卫: 横幅定义 HeroBadge/HeroBadgeText 玻璃徽章",
    perfXaml61.Contains("x:Key=\"HeroBadge\"") && perfXaml61.Contains("x:Key=\"HeroBadgeText\"")
    && perfXaml61.Contains("Foreground\" Value=\"#F5F7FA\""), "");
int heroStart61 = perfXaml61.IndexOf("<WrapPanel Margin=\"0,8,0,0\"", StringComparison.Ordinal);
int heroWrapEnd61 = heroStart61 >= 0 ? perfXaml61.IndexOf("</WrapPanel>", heroStart61, StringComparison.Ordinal) : -1;
string heroBar61 = heroWrapEnd61 > heroStart61
    ? perfXaml61.Substring(heroStart61, heroWrapEnd61 - heroStart61) : "";
Check("视觉守卫: 横幅操作按钮全部使用 BtnSmOnHero（暗底固定浅色）",
    heroBar61.Contains("Style=\"{StaticResource BtnSmOnHero}\"")
    && !heroBar61.Contains("Style=\"{StaticResource BtnSm}\""), "");
// 间距等距：旧的 8/10 特殊左边距必须全部消失，2 徽章+5 按钮统一右距 6（末项除外，共 7 处）
Check("视觉守卫: 横幅徽章/按钮间距统一 6px（无 8/10 参差）",
    !heroBar61.Contains("Margin=\"8,0,0,6\"")
    && !heroBar61.Contains("Margin=\"10,0")
    && heroBar61.Split([ "Margin=\"0,0,6,6\"" ], StringSplitOptions.None).Length >= 8,
    (heroBar61.Split([ "Margin=\"0,0,6,6\"" ], StringSplitOptions.None).Length - 1).ToString());
Check("视觉守卫: 横幅徽章使用 HeroBadge 且已完结字为固定亮绿",
    heroBar61.Contains("Style=\"{StaticResource HeroBadge}\"")
    && !heroBar61.Contains("Style=\"{StaticResource Badge}\"")
    && heroBar61.Contains("Foreground=\"#4ADE80\""), "");
Check("视觉守卫: 「自动获取」已改名「刷新路径」",
    perfXaml61.Contains("Content=\"刷新路径\"")
    && !perfXaml61.Contains("Content=\"自动获取\""), "");

Console.WriteLine($"\n结果: {pass} 通过, {fail} 失败");
try { Directory.Delete(temp, true); } catch { }
Environment.Exit(fail == 0 ? 0 : 1);

/// <summary>文件指纹：mtime + SHA256；不存在返回 "absent"。用于零污染守卫。</summary>
static string FileFingerprint(string path)
{
    if (!File.Exists(path)) return "absent";
    byte[] hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path));
    return File.GetLastWriteTime(path).Ticks + ":" + Convert.ToHexString(hash);
}

/// <summary>真实库 settings.json 是否为"旧版默认壳"（无 schema 版本标记且内容全默认）——
/// 正是"默认值覆写事故"的指纹，出现即应停止冒烟并恢复。</summary>
static bool IsLegacyDefaultSettingsShell(string path)
{
    try
    {
        string text = File.ReadAllText(path);
        if (text.Contains("SettingsSchemaVersion", StringComparison.Ordinal)) return false;
        return Store.IsDefaultShell(JsonSerializer.Deserialize<AppSettings>(text));
    }
    catch { return false; }
}

/// <summary>同步收集下载进度回报（不依赖 UI 同步上下文，await 返回后立即可读）。</summary>
sealed class CollectProgress52 : IProgress<double>
{
    public List<double> Values { get; } = new();
    public void Report(double value) { lock (Values) Values.Add(value); }
}
