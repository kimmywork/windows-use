// Diff.cs — 期望串与实际读回串的逐字符比对 + 异常模式识别。
// 每种输入方案的成败判定都走这里，保证 A/B/C 三方案口径一致。

namespace WinUse.PoC.Input;

internal sealed class DiffResult
{
    public bool LengthEqual { get; set; }
    public bool ExactMatch { get; set; }

    /// <summary>首个不匹配下标；长度不同但前缀一致时取较短串长度。-1 表示完全一致。</summary>
    public int FirstMismatchIndex { get; set; } = -1;

    /// <summary>首个不匹配位置附近的期望串片段（便于报告定位）。</summary>
    public string ExpectedSnippet { get; set; } = "";

    /// <summary>首个不匹配位置附近的实际串片段。</summary>
    public string ActualSnippet { get; set; } = "";

    public List<string> Anomalies { get; set; } = new();

    public string Verdict => ExactMatch ? "OK" : "MISMATCH";
}

internal static class Diff
{
    private const int SnippetRadius = 6;

    /// <summary>逐 UTF-16 码元比对，并识别丢字/多字/乱序/问号替换/代理对断裂等典型异常。</summary>
    public static DiffResult Compare(string expected, string actual)
    {
        string exp = expected ?? string.Empty;
        string act = actual ?? string.Empty;

        var result = new DiffResult
        {
            LengthEqual = exp.Length == act.Length,
            ExactMatch = string.Equals(exp, act, StringComparison.Ordinal)
        };

        int common = Math.Min(exp.Length, act.Length);
        int first = -1;
        for (int i = 0; i < common; i++)
        {
            if (exp[i] != act[i])
            {
                first = i;
                break;
            }
        }
        if (first == -1 && exp.Length != act.Length)
        {
            first = common;
        }

        result.FirstMismatchIndex = first;
        if (first >= 0)
        {
            result.ExpectedSnippet = Snippet(exp, first);
            result.ActualSnippet = Snippet(act, first);
        }

        var anomalies = result.Anomalies;
        if (act.Length < exp.Length)
        {
            anomalies.Add($"LOST_CHARS(expected={exp.Length},actual={act.Length},lost={exp.Length - act.Length})");
            if (first == act.Length && first > 0)
            {
                anomalies.Add("TRUNCATED_PREFIX_INTACT");
            }
        }
        else if (act.Length > exp.Length)
        {
            anomalies.Add($"EXTRA_CHARS(expected={exp.Length},actual={act.Length},extra={act.Length - exp.Length})");
            if (exp.Length > 0 && act.StartsWith(exp, StringComparison.Ordinal))
            {
                anomalies.Add("EXTRA_TRAILING_CHARS");
            }
        }

        if (!result.ExactMatch && result.LengthEqual && SameMultiset(exp, act))
        {
            anomalies.Add("REORDERED");
        }

        if (act.Contains('?') && !exp.Contains('?'))
        {
            anomalies.Add("QUESTION_MARK_SUBSTITUTION");
        }
        if (act.Contains('�'))
        {
            anomalies.Add("REPLACEMENT_CHAR_U+FFFD");
        }
        if (act.IndexOf('\0') >= 0)
        {
            anomalies.Add("NUL_CHAR");
        }
        if (HasLoneSurrogate(act))
        {
            anomalies.Add("LONE_SURROGATE_DETECTED");
        }
        if (!result.ExactMatch && ContainsIdeographs(exp) && ContainsIdeographs(act) && result.LengthEqual)
        {
            anomalies.Add("CJK_CONTENT_DIFFERS");
        }
        if (!result.ExactMatch && IsPureAscii(exp) && !IsPureAscii(act))
        {
            anomalies.Add("ASCII_INPUT_BECAME_NON_ASCII");
        }

        return result;
    }

    /// <summary>是否有孤立代理项（UTF-16 代理对被拆散的直接证据）。</summary>
    public static bool HasLoneSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))
                {
                    return true;
                }
                i++;
            }
            else if (char.IsLowSurrogate(s[i]))
            {
                return true;
            }
        }
        return false;
    }

    private static bool SameMultiset(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var counts = new Dictionary<char, int>();
        foreach (char c in a)
        {
            counts[c] = counts.TryGetValue(c, out int v) ? v + 1 : 1;
        }
        foreach (char c in b)
        {
            if (!counts.TryGetValue(c, out int v) || v == 0) return false;
            counts[c] = v - 1;
        }
        return true;
    }

    private static bool ContainsIdeographs(string s)
    {
        foreach (char c in s)
        {
            int cp = char.ConvertToUtf32(s, s.IndexOf(c));
            if (cp >= 0x3400 && cp <= 0x9FFF) return true;
            if (cp >= 0xF900 && cp <= 0xFAFF) return true;
            if (cp >= 0x20000 && cp <= 0x2FA1F) return true;
        }
        return false;
    }

    private static bool IsPureAscii(string s)
    {
        foreach (char c in s)
        {
            if (c > 0x7F) return false;
        }
        return true;
    }

    private static string Snippet(string s, int center)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int start = Math.Max(0, center - SnippetRadius);
        int len = Math.Min(s.Length - start, SnippetRadius * 2 + 1);
        string core = s.Substring(start, len);
        return $"@{start}..{start + len - 1}:[{Escape(core)}]";
    }

    /// <summary>把不可见/易混字符转成可见转义，便于在报告与 JSON 里定位。</summary>
    public static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length * 2);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\0': sb.Append("\\0"); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c) || char.IsSurrogate(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("X4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>控制台/报告用的短预览：超长时截断并标注总长。</summary>
    public static string Preview(string s, int max = 24)
    {
        if (string.IsNullOrEmpty(s)) return "(空)";
        string esc = Escape(s);
        return esc.Length <= max ? esc : string.Concat(esc.AsSpan(0, max), $"…(共{s.Length}字符)");
    }
}
