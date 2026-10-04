using System.Text;

namespace GameSaveManager.Services;

/// <summary>Valve KeyValue (VDF) 极简解析器，满足 libraryfolders.vdf / appmanifest_*.acf 的解析。</summary>
public static class Vdf
{
    public static Dictionary<string, object> Parse(string text)
    {
        int pos = 0;
        return ParseBlock(text, ref pos, true);
    }

    public static Dictionary<string, object> ParseFile(string path)
        => Parse(System.IO.File.ReadAllText(path));

    private static Dictionary<string, object> ParseBlock(string t, ref int pos, bool top)
    {
        var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (pos < t.Length)
        {
            SkipWs(t, ref pos);
            if (pos >= t.Length) break;
            char c = t[pos];
            if (c == '}')
            {
                pos++;
                if (!top) return dict;
                continue;
            }
            string key = ReadToken(t, ref pos);
            SkipWs(t, ref pos);
            if (pos < t.Length && t[pos] == '{')
            {
                pos++;
                dict[key] = ParseBlock(t, ref pos, false);
            }
            else
            {
                dict[key] = ReadToken(t, ref pos);
            }
        }
        return dict;
    }

    private static string ReadToken(string t, ref int pos)
    {
        SkipWs(t, ref pos);
        if (pos >= t.Length) return "";
        if (t[pos] == '"')
        {
            pos++;
            var sb = new StringBuilder();
            while (pos < t.Length && t[pos] != '"')
            {
                if (t[pos] == '\\' && pos + 1 < t.Length)
                {
                    pos++;
                    char n = t[pos];
                    sb.Append(n == 'n' ? '\n' : n == '\\' ? '\\' : n);
                    pos++;
                }
                else sb.Append(t[pos++]);
            }
            if (pos < t.Length) pos++;   // 跳过结束引号
            return sb.ToString();
        }
        int start = pos;
        while (pos < t.Length && !char.IsWhiteSpace(t[pos]) && t[pos] != '{' && t[pos] != '}')
            pos++;
        return t.Substring(start, pos - start);
    }

    private static void SkipWs(string t, ref int pos)
    {
        while (pos < t.Length && char.IsWhiteSpace(t[pos])) pos++;
    }

    public static string GetStr(Dictionary<string, object> dict, string key, string def = "")
        => dict.TryGetValue(key, out var v) && v is string s ? s : def;

    public static Dictionary<string, object>? GetDict(Dictionary<string, object> dict, string key)
        => dict.TryGetValue(key, out var v) && v is Dictionary<string, object> d ? d : null;
}
