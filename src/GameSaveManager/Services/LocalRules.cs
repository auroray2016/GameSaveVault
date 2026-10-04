using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameSaveManager.Services;

/// <summary>rules.json 中的一条本地存档规则（用户可编辑，离线生效）。</summary>
public class SaveRule
{
    /// <summary>正则，匹配归一化后的游戏名（小写、去符号、罗马数字已转阿拉伯）。</summary>
    public string Match { get; set; } = "";
    /// <summary>候选存档路径，支持变量（见 LocalRules.Expand 注释）。</summary>
    public List<string> Paths { get; set; } = new();
    public string Note { get; set; } = "";
}

/// <summary>
/// 本地存档规则库 rules.json：首次运行生成种子（内置常见游戏），用户可自行增改，文件保存后自动重载。
/// 弥补 PCGamingWiki 覆盖不到 / 离线场景，尤其学习版绿色存档。
/// 路径变量：{docs} 我的文档  {home} 用户目录  {appdata} Roaming  {localappdata} Local
///          {locallow} LocalLow  {savedgames} 已保存的游戏  {install} 游戏安装目录  {steam} Steam 目录
/// </summary>
public static class LocalRules
{
    private static List<SaveRule>? _rules;
    private static string _loadedFile = "";
    private static DateTime _loadedWriteTime = DateTime.MinValue;

    public static string RulesFile => Path.Combine(Store.BaseDir, "rules.json");

    public static List<SaveRule> Load()
    {
        try
        {
            // 文件被外部编辑（写入时间变化）即重载；路径变化（测试重定向）也重载
            bool upToDate = _rules != null && _loadedFile == RulesFile && File.Exists(RulesFile)
                && File.GetLastWriteTime(RulesFile) == _loadedWriteTime;
            if (upToDate) return _rules!;

            _rules = new List<SaveRule>();
            if (!File.Exists(RulesFile)) Seed();
            if (File.Exists(RulesFile))
            {
                var d = JsonSerializer.Deserialize<List<SaveRule>>(File.ReadAllText(RulesFile));
                if (d != null)
                    _rules = d.Where(r => !string.IsNullOrWhiteSpace(r.Match)).ToList();
            }
            _loadedFile = RulesFile;
            _loadedWriteTime = File.Exists(RulesFile) ? File.GetLastWriteTime(RulesFile) : DateTime.Now;
        }
        catch { _rules ??= new List<SaveRule>(); }
        return _rules;
    }

    /// <summary>对归一化游戏名列表应用规则，返回展开后的候选路径（是否存在由调用方判断）。</summary>
    public static IEnumerable<string> Match(IEnumerable<string> normNames, string installDir)
    {
        var names = normNames.ToList();
        foreach (var rule in Load())
        {
            bool hit;
            try { hit = names.Any(n => Regex.IsMatch(n, rule.Match, RegexOptions.IgnoreCase)); }
            catch { continue; }   // 非法正则跳过，不影响其他规则
            if (!hit) continue;
            foreach (var p in rule.Paths)
            {
                var expanded = Expand(p, installDir);
                if (!string.IsNullOrWhiteSpace(expanded)) yield return expanded;
            }
        }
    }

    /// <summary>展开路径变量（大小写不敏感）。</summary>
    public static string Expand(string tpl, string installDir)
    {
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return tpl
            .Replace("{docs}", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), StringComparison.OrdinalIgnoreCase)
            .Replace("{home}", user, StringComparison.OrdinalIgnoreCase)
            .Replace("{appdata}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase)
            .Replace("{localappdata}", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase)
            .Replace("{locallow}", Path.Combine(user, "AppData", "LocalLow"), StringComparison.OrdinalIgnoreCase)
            .Replace("{savedgames}", Path.Combine(user, "Saved Games"), StringComparison.OrdinalIgnoreCase)
            .Replace("{install}", installDir ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{steam}", SteamScanner.FindSteamPath() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    private static void Seed()
    {
        var seed = new List<object>
        {
            new
            {
                _comment = "本地存档规则。Match=正则（匹配归一化英文名：小写、无符号、罗马数字转阿拉伯，如 torchlight2）。"
                         + "Paths 支持变量 {docs}我的文档 {home}用户目录 {appdata}Roaming {localappdata}Local {locallow}LocalLow "
                         + "{savedgames}已保存的游戏 {install}游戏安装目录 {steam}Steam目录。改完保存即生效，无需重启。",
            },
            new SaveRule { Match = "witcher3", Note = "巫师3：狂猎", Paths = new() { @"{docs}\The Witcher 3" } },
            new SaveRule { Match = "cyberpunk2077", Note = "赛博朋克2077", Paths = new() { @"{home}\Saved Games\CD Projekt Red\Cyberpunk 2077" } },
            new SaveRule { Match = "eldenring", Note = "艾尔登法环（学习版常见位置）", Paths = new() { @"{appdata}\EldenRing" } },
            new SaveRule { Match = "^b1$|blackmyth", Note = "黑神话：悟空", Paths = new() { @"{localappdata}\b1" } },
            new SaveRule { Match = "torchlight2", Note = "火炬之光2", Paths = new() { @"{docs}\My Games\runic games\torchlight 2" } },
            new SaveRule { Match = "baldursgate3|bg3", Note = "博德之门3", Paths = new() { @"{localappdata}\Larian Studios\Baldur's Gate 3" } },
            new SaveRule { Match = "skyrimspecialedition|skyrimse", Note = "上古卷轴5 特别版", Paths = new() { @"{docs}\My Games\Skyrim Special Edition" } },
            new SaveRule { Match = "fallout4", Note = "辐射4", Paths = new() { @"{docs}\My Games\Fallout4" } },
        };
        try
        {
            Directory.CreateDirectory(Store.BaseDir);
            File.WriteAllText(RulesFile, JsonSerializer.Serialize(seed, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }
        catch { /* 种子写入失败不影响主流程 */ }
    }
}
