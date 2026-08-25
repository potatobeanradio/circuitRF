// S-expression tokenizer/parser for the board interchange format (docs/sonnet-briefs/
// brief-L4d-kicad-pcb-import.md §2). Pure text-and-tokens: this file knows nothing about layers,
// shapes, or editor state — exactly the split DxfReader/DxfImport already draw (R-L4d-0).
//
// Deliberately iterative rather than recursive. A board file's nesting is shallow (a zone's
// filled_polygon is about six levels down), but a truncated or malformed file can present an
// arbitrarily long run of '(' and a recursive-descent parser turns that into a StackOverflow — an
// uncatchable process kill, not a diagnostic. §2's own rule is "never refuse a file for its version";
// dying on one is worse.

using System.Globalization;
using System.Text;

namespace CircuitRF.Ui.Layout.Interchange;

/// <summary>
/// One parenthesised list. <see cref="Tag"/> is its first atom (<c>segment</c>, <c>gr_line</c>, …);
/// <see cref="Items"/> preserves the original order of everything after it, atoms and sub-lists
/// interleaved, because this format positions some operands (<c>(pad "1" thru_hole circle …)</c>) and
/// names others.
/// </summary>
public sealed class PcbNode
{
    public string Tag { get; init; } = "";

    /// <summary>Atoms and child <see cref="PcbNode"/>s in file order. A string element is an atom
    /// (already unquoted and unescaped); a <see cref="PcbNode"/> element is a sub-list.</summary>
    public List<object> Items { get; } = [];

    /// <summary><paramref name="index"/>-th ATOM after the tag, or null when there are fewer.
    /// Counts atoms only — sub-lists never shift an atom's index, which is what makes
    /// <c>(pad "1" thru_hole circle (at …) …)</c> readable as atoms 0/1/2 regardless of what follows.</summary>
    public string? Atom(int index)
    {
        int seen = 0;
        foreach (var item in Items)
            if (item is string s && seen++ == index) return s;
        return null;
    }

    /// <summary>Every atom after the tag, in order.</summary>
    public IEnumerable<string> Atoms
    {
        get
        {
            foreach (var item in Items)
                if (item is string s) yield return s;
        }
    }

    /// <summary>Every direct child list, in order.</summary>
    public IEnumerable<PcbNode> Nodes
    {
        get
        {
            foreach (var item in Items)
                if (item is PcbNode n) yield return n;
        }
    }

    /// <summary>Direct child lists whose tag is <paramref name="tag"/>.</summary>
    public IEnumerable<PcbNode> Children(string tag)
    {
        foreach (var item in Items)
            if (item is PcbNode n && n.Tag == tag) yield return n;
    }

    /// <summary>The FIRST direct child list tagged <paramref name="tag"/>, or null.</summary>
    public PcbNode? Child(string tag)
    {
        foreach (var item in Items)
            if (item is PcbNode n && n.Tag == tag) return n;
        return null;
    }

    /// <summary>True when a bare atom equal to <paramref name="atom"/> appears after the tag — how
    /// this format spells a positional flag (<c>(via blind …)</c>, <c>(options clearance anchor)</c>).</summary>
    public bool HasAtom(string atom)
    {
        foreach (var item in Items)
            if (item is string s && s == atom) return true;
        return false;
    }

    /// <summary><paramref name="index"/>-th atom parsed as a decimal number, or null when absent or
    /// unparseable. Invariant culture always: the format is decimal-point, never locale-dependent —
    /// reading it under a comma-decimal culture silently truncates every coordinate to its integer part.</summary>
    public double? Num(int index)
        => Atom(index) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : null;

    /// <summary>First child tagged <paramref name="tag"/>, its <paramref name="index"/>-th atom as a
    /// number. The single most common read in the reader (<c>(width 0.2)</c>, <c>(size 1.6)</c>).</summary>
    public double? ChildNum(string tag, int index = 0) => Child(tag)?.Num(index);

    /// <summary>First child tagged <paramref name="tag"/>, its <paramref name="index"/>-th atom.</summary>
    public string? ChildAtom(string tag, int index = 0) => Child(tag)?.Atom(index);

    public override string ToString() => $"({Tag} …{Items.Count})";
}

public static class PcbSexpr
{
    /// <summary>What <see cref="Parse"/> produced: the root list plus anything that was wrong with the
    /// text. A file that ends mid-list still yields every list that DID close — §2's "never refuse a
    /// file" applied to the parser itself: a truncated download imports what it has and says so.</summary>
    public sealed record ParseResult(PcbNode? Root, IReadOnlyList<string> Diagnostics);

    /// <summary>
    /// Parses <paramref name="text"/> into a node tree. Tokens are unquoted lowercase words; strings
    /// are double-quoted with backslash escapes; everything else is whitespace.
    /// </summary>
    public static ParseResult Parse(string text)
    {
        var diagnostics = new List<string>();
        var stack = new List<PcbNode>();
        PcbNode? root = null;
        int i = 0, n = text.Length;
        var sb = new StringBuilder();

        while (i < n)
        {
            char c = text[i];

            if (c == '(')
            {
                i++;
                // The tag is the first token after '(' — read it here rather than as a generic atom so
                // a node is never constructed tagless (which would make every Child()/Tag read a
                // special case downstream).
                while (i < n && char.IsWhiteSpace(text[i])) i++;
                int tagStart = i;
                while (i < n && !char.IsWhiteSpace(text[i]) && text[i] != '(' && text[i] != ')') i++;
                var node = new PcbNode { Tag = text[tagStart..i] };

                if (stack.Count > 0) stack[^1].Items.Add(node);
                else if (root is null) root = node;
                else diagnostics.Add("More than one top-level list in the file — only the first was read.");
                stack.Add(node);
                continue;
            }

            if (c == ')')
            {
                i++;
                if (stack.Count == 0)
                {
                    diagnostics.Add("Unbalanced ')' in the file — ignored.");
                    continue;
                }
                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            if (c == '"')
            {
                i++;
                sb.Clear();
                bool closed = false;
                while (i < n)
                {
                    if (text[i] == '\\' && i + 1 < n) { sb.Append(text[i + 1]); i += 2; continue; }
                    if (text[i] == '"') { i++; closed = true; break; }
                    sb.Append(text[i]); i++;
                }
                if (!closed) diagnostics.Add("A quoted string is unterminated at end of file — read to the end.");
                if (stack.Count > 0) stack[^1].Items.Add(sb.ToString());
                continue;
            }

            if (char.IsWhiteSpace(c)) { i++; continue; }

            int start = i;
            while (i < n && !char.IsWhiteSpace(text[i]) && text[i] != '(' && text[i] != ')' && text[i] != '"') i++;
            if (stack.Count > 0) stack[^1].Items.Add(text[start..i]);
            else diagnostics.Add($"Atom \"{text[start..i]}\" outside any list — ignored.");
        }

        if (stack.Count > 0)
            diagnostics.Add($"The file ends inside {stack.Count} unclosed list(s) — everything that closed was read.");

        return new ParseResult(root, diagnostics);
    }
}
