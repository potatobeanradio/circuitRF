// ================================================================
//  PortPairSpelling.cs — Name(i, j) in a trace reads as Name[:, …, i, j]
//
//  A measure line reads a port matrix by port number as SP1.S(2, 1), and a measurement that holds one —
//  DUT_1 = SP1.S(1:2, 1:2) — as DUT_1(2, 1). A trace is written in the bracket form, so the same spelling
//  typed into a trace card or `plot --trace` found no cube. This turns it into the bracket form before
//  either trace parser reads it: the two numbers go to the i and j axes, ':' to every other axis in the
//  cube's own order, and the port numbers are then read exactly as a typed S[:, 2, 1] is.
// ================================================================

using System.Linq;
using System.Text;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

/// <summary>Rewrites <c>Name(i, j)</c> on a port-matrix cube to <c>Name[:, …, i, j]</c>.</summary>
public static class PortPairSpelling
{
    /// <summary>
    /// <paramref name="text"/> with every <c>Name(i, j)</c> — Name a cube of <paramref name="ds"/> with port axes
    /// <c>i</c> and <c>j</c>, i and j whole numbers — written in the bracket form. Anything else, quoted text
    /// included, is left as it is.
    /// </summary>
    public static string Rewrite(string text, DataSet ds)
    {
        if (text.IndexOf('(') < 0) return text;
        var sb = new StringBuilder(text.Length + 16);
        int pos = 0;
        while (pos < text.Length)
        {
            char c = text[pos];
            if (c == '"')
            {
                int close = text.IndexOf('"', pos + 1);
                int end = close < 0 ? text.Length : close + 1;
                sb.Append(text, pos, end - pos);
                pos = end;
                continue;
            }
            if (!IsIdentStart(c) || (pos > 0 && IsIdent(text[pos - 1])))
            {
                sb.Append(c);
                pos++;
                continue;
            }
            int nameEnd = pos;
            while (nameEnd < text.Length && IsIdent(text[nameEnd])) nameEnd++;
            string name = text[pos..nameEnd];
            if (nameEnd < text.Length && text[nameEnd] == '(' && TryPair(text, nameEnd, out int i, out int j, out int after)
                && ds.Contains(name) && ds[name] is var cube
                && cube.Axes.Any(a => a.Name == "i") && cube.Axes.Any(a => a.Name == "j"))
            {
                sb.Append(name).Append('[')
                  .Append(string.Join(", ", cube.Axes.Select(a => a.Name switch { "i" => i.ToString(), "j" => j.ToString(), _ => ":" })))
                  .Append(']');
                pos = after;
                continue;
            }
            sb.Append(name);
            pos = nameEnd;
        }
        return sb.ToString();
    }

    /// <summary>"(2, 1)" at <paramref name="open"/>: two whole numbers and the position after the ')'.</summary>
    private static bool TryPair(string text, int open, out int i, out int j, out int after)
    {
        i = j = 0;
        after = open;
        int close = text.IndexOf(')', open + 1);
        if (close < 0) return false;
        var parts = text[(open + 1)..close].Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out i) || !int.TryParse(parts[1].Trim(), out j))
            return false;
        after = close + 1;
        return true;
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';
}
