// A tokenizer for the `.PLX` / `.DSL` S-expression dialect
// (docs/sonnet-briefs/brief-PL2-component-library-breadth.md R-PL2-11).
//
// ── Why this is not PcbSexpr ──────────────────────────────────────────────────────────────────────
//
// It looks like the same grammar and is not. This dialect does two things the board format never
// does, and both of them are inside the ATOM, which is precisely the part a tokenizer cannot be
// parameterised over:
//
//   * commas inside coordinate atoms   — `(pt 0, -100)`
//   * unit words after numbers         — `(pinLength 300 mils)`
//
// Supporting them in PcbSexpr means changing the tokenizer that L4d's board reader depends on, to
// accommodate a foreign dialect's quirks, for no benefit to the format that reader actually serves.
// A separate tokenizer is cheaper than that risk — and small enough to be worth measuring rather than
// arguing about: this file is the measurement.
//
// The comma is treated as whitespace, so `(pt 0, -100)` yields atoms `0` and `-100`; a trailing unit
// word survives as its own atom and callers take the first numeric, so `(pinLength 300 mils)` reads
// 300 without the word ever needing to be enumerated.

using System.Globalization;
using System.Text;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>One node: a tag, its atoms, and its children.</summary>
public sealed class PlxNode
{
    public string Tag { get; init; } = "";

    /// <summary>The unparenthesised words following the tag, quotes already stripped.</summary>
    public List<string> Atoms { get; } = [];

    public List<PlxNode> Children { get; } = [];

    public PlxNode? First(string tag)
        => Children.FirstOrDefault(c => c.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<PlxNode> All(string tag)
        => Children.Where(c => c.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>This node's first atom, or <c>""</c>.</summary>
    public string Atom => Atoms.Count > 0 ? Atoms[0] : "";

    /// <summary>A child's first atom — <c>(width 6)</c> → <c>"6"</c>.</summary>
    public string AtomOf(string tag) => First(tag)?.Atom ?? "";

    /// <summary>
    /// A child's first NUMERIC atom, which is how a unit word is skipped without enumerating the
    /// units: <c>(pinLength 300 mils)</c> → <c>300</c>.
    /// </summary>
    public double NumberOf(string tag, double fallback = 0)
    {
        var node = First(tag);
        if (node is null) return fallback;
        foreach (var atom in node.Atoms)
            if (double.TryParse(atom, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return v;
        return fallback;
    }

    /// <summary>A <c>(pt x, y)</c> child as a pair. Absent reads as the origin, which every caller
    /// here guards against by checking the node exists first.</summary>
    public (double X, double Y) PointOf(string tag)
    {
        var node = First(tag);
        if (node is null) return (0, 0);
        var nums = node.Numbers();
        return nums.Count >= 2 ? (nums[0], nums[1]) : (0, 0);
    }

    /// <summary>Every numeric atom of this node, in order — the comma having already gone.</summary>
    public List<double> Numbers()
    {
        var result = new List<double>(Atoms.Count);
        foreach (var atom in Atoms)
            if (double.TryParse(atom, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                result.Add(v);
        return result;
    }
}

public static class ComponentPlxSexpr
{
    /// <summary>What <see cref="Parse"/> recovered.</summary>
    /// <param name="Banner">The first line's format banner, which is the ONLY difference between the
    /// two extensions (R-PL2-10) — kept so a message can name the format the user actually handed us.</param>
    public sealed record Result(string Banner, IReadOnlyList<PlxNode> Roots);

    /// <summary>How deep a nested expression may go before the file is treated as malformed. Bounds
    /// the recursion on a file that is not what it claims to be.</summary>
    private const int MaxDepth = 64;

    public static Result Parse(string text)
    {
        var roots = new List<PlxNode>();
        int i = 0;

        // The banner is a bare line before any parenthesis — not an S-expression, so it is taken off
        // the front rather than parsed.
        string banner = "";
        int firstParen = text.IndexOf('(');
        if (firstParen > 0)
        {
            banner = text[..firstParen].Trim();
            int nl = banner.IndexOf('\n');
            if (nl >= 0) banner = banner[..nl].TrimEnd('\r').Trim();
            i = firstParen;
        }

        while (i < text.Length)
        {
            while (i < text.Length && text[i] != '(') i++;
            if (i >= text.Length) break;
            var node = ParseNode(text, ref i, 0);
            if (node is not null) roots.Add(node);
        }

        return new Result(banner, roots);
    }

    private static PlxNode? ParseNode(string text, ref int i, int depth)
    {
        if (depth > MaxDepth || i >= text.Length || text[i] != '(') return null;
        i++;                                                            // past '('

        string tag = ReadAtom(text, ref i);
        var node = new PlxNode { Tag = tag };

        while (i < text.Length)
        {
            SkipSpace(text, ref i);
            if (i >= text.Length) break;

            if (text[i] == ')') { i++; break; }

            if (text[i] == '(')
            {
                var child = ParseNode(text, ref i, depth + 1);
                if (child is not null) node.Children.Add(child);
                continue;
            }

            string atom = ReadAtom(text, ref i);
            if (atom.Length > 0) node.Atoms.Add(atom);
        }

        return node;
    }

    /// <summary>One atom: a quoted string, or a run up to the next delimiter. <b>The comma is a
    /// delimiter</b>, which is the whole reason this file exists.</summary>
    private static string ReadAtom(string text, ref int i)
    {
        SkipSpace(text, ref i);
        if (i >= text.Length) return "";

        if (text[i] == '"')
        {
            i++;
            var sb = new StringBuilder();
            while (i < text.Length && text[i] != '"') sb.Append(text[i++]);
            if (i < text.Length) i++;                                   // past the closing quote
            return sb.ToString();
        }

        int start = i;
        while (i < text.Length && !IsDelimiter(text[i])) i++;
        return text[start..i];
    }

    private static bool IsDelimiter(char c)
        => char.IsWhiteSpace(c) || c is '(' or ')' or ',' or '"';

    private static void SkipSpace(string text, ref int i)
    {
        while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;
    }
}
