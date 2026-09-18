// What one part-reference CELL of a companion table means, which is not always one refdes
// (docs/sonnet-briefs/brief-railrf-2-companion-readers.md R-rail2-14 item 2).
//
// A purchasing document is written GROUPED: one row per part number, with every reference that uses
// it in a single cell — "C1-C9", "C1,C2,C3", "C1 C2 C3". That is the normal shape of the document
// §2.2 describes, not an edge case. A reader that takes the cell verbatim looks up a refdes called
// "C1-C9", matches nothing, and reports nine unresolved parts — which reads as a broken board rather
// than as a reader that cannot spell.
//
// SO A CELL IS A SET, AND ONE REFERENCE IS THE ONE-ELEMENT CASE. That is the whole idea, and the
// reason it is allowed now: the alternative is a propagating change through brief 3's pad resolution,
// brief 7's parts table and brief 16's matcher.
//
// WHAT IT WILL NOT DO: expand a range whose two ends are not the same letters over ascending
// numbers. "C1-R9" is not a range, "C10-C2" is not a range, and neither is turned into one — an
// expansion that guesses is a set of references that are NOT on the board, and those are worse than
// an unexpanded cell because they look real.

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>What one reference cell expanded to, and whether it was a range or a list at all.</summary>
/// <param name="Refdes">Every reference the cell names, in the order it named them, deduplicated.</param>
/// <param name="Expanded">True where the cell held more than one reference — the count a reader
/// reports, because a grouped document and a flat one are worth telling apart.</param>
/// <param name="Unexpanded">A fragment that looked like a range and was NOT expanded, with the
/// reason. Reported, never dropped: a cell this reader could not read is the case where a user's own
/// eyes are the fix.</param>
public sealed record RefdesSet(
    IReadOnlyList<string> Refdes,
    bool Expanded,
    IReadOnlyList<string> Unexpanded)
{
    public static readonly RefdesSet Empty = new([], false, []);
}

public static class RefdesCell
{
    /// <summary>The most references one range may expand to. A cell reading <c>C1-C99999</c> is a
    /// typo, not a board, and the ceiling is what stops a typo becoming a hundred thousand rows.
    /// Well above any real grouped row: a dense decoupling bank is tens, not thousands.</summary>
    public const int MaxRangeExpansion = 2000;

    /// <summary>The separators between references in one cell. A comma and a semicolon are the
    /// written ones; whitespace is the one an export produces when it joins with a space.</summary>
    private static readonly char[] Separators = [',', ';', ' ', '\t'];

    /// <summary>
    /// Every reference <paramref name="cell"/> names.
    ///
    /// <para>A plain reference returns itself. <c>C1,C2</c> and <c>C1 C2</c> return both.
    /// <c>C1-C9</c> and <c>C1-9</c> return the nine. Anything that looks like a range and is not one
    /// is returned VERBATIM and named in <see cref="RefdesSet.Unexpanded"/> — never silently dropped
    /// and never guessed at.</para>
    /// </summary>
    public static RefdesSet Parse(string? cell)
    {
        if (cell is null || cell.Trim().Length == 0) return RefdesSet.Empty;

        var refdes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unexpanded = new List<string>();
        int pieces = 0;

        foreach (string piece in cell.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            string item = piece.Trim();
            if (item.Length == 0) continue;
            pieces++;

            if (item.Contains('-', StringComparison.Ordinal) ||
                item.Contains('–', StringComparison.Ordinal))    // an en dash, which a spreadsheet writes
            {
                if (TryExpandRange(item, out var range))
                {
                    foreach (string r in range) if (seen.Add(r)) refdes.Add(r);
                    continue;
                }

                // Not a range this reader can be sure of. Kept as written AND reported, because a
                // reference containing a hyphen is a legal reference on some boards.
                unexpanded.Add(item);
            }

            if (seen.Add(item)) refdes.Add(item);
        }

        return new RefdesSet(refdes, refdes.Count > 1 || pieces > 1, unexpanded);
    }

    /// <summary>
    /// <c>C1-C9</c> or <c>C1-9</c> → the nine references. False where the two ends do not share a
    /// letter prefix, where either end is not letters-then-digits, where the range descends, or where
    /// it would exceed <see cref="MaxRangeExpansion"/>.
    ///
    /// <para>The leading-zero spelling is preserved from the LOW end — <c>C001-C003</c> is C001, C002,
    /// C003 and not C1, C2, C3, because the board's silkscreen is what a refdes has to match.</para>
    /// </summary>
    private static bool TryExpandRange(string item, out IReadOnlyList<string> expanded)
    {
        expanded = [];

        string normalized = item.Replace('–', '-');
        int cut = normalized.IndexOf('-', StringComparison.Ordinal);
        if (cut <= 0 || cut == normalized.Length - 1) return false;
        if (normalized.IndexOf('-', cut + 1) >= 0) return false;      // two hyphens: not a range

        string lo = normalized[..cut].Trim(), hi = normalized[(cut + 1)..].Trim();
        if (!Split(lo, out string prefix, out string loDigits)) return false;
        if (prefix.Length == 0) return false;

        string hiDigits;
        if (Split(hi, out string hiPrefix, out hiDigits))
        {
            // "C1-9" — the high end may omit the prefix, which is how a spreadsheet's own fill
            // writes it. A DIFFERENT prefix is not a range and is never treated as one.
            if (hiPrefix.Length > 0 && !hiPrefix.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        }
        else return false;

        if (!int.TryParse(loDigits, out int first) || !int.TryParse(hiDigits, out int last)) return false;
        if (last < first || last - first + 1 > MaxRangeExpansion) return false;

        int width = loDigits.Length > 1 && loDigits[0] == '0' ? loDigits.Length : 0;
        var list = new List<string>(last - first + 1);
        for (int n = first; n <= last; n++)
            list.Add(prefix + (width > 0 ? n.ToString().PadLeft(width, '0') : n.ToString()));

        expanded = list;
        return true;
    }

    /// <summary>Splits <c>C12</c> into <c>C</c> and <c>12</c>. False where the text is not letters
    /// followed by digits with nothing after them.</summary>
    private static bool Split(string text, out string prefix, out string digits)
    {
        prefix = "";
        digits = "";
        if (text.Length == 0) return false;

        int i = 0;
        while (i < text.Length && char.IsLetter(text[i])) i++;
        prefix = text[..i];

        int d = i;
        while (d < text.Length && char.IsAsciiDigit(text[d])) d++;
        if (d != text.Length || d == i) return false;

        digits = text[i..d];
        return true;
    }
}
