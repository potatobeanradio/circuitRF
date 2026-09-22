// Naming the columns of a placement table that has no header row.
//
// ── WHY THIS EXISTS ────────────────────────────────────────────────────────────────────────────
//
// PlacementFile has refused a headerless file since it was written, and the refusal is right:
// "column order is not a standard — a positional reading would put the rotation in the Y column
// silently". What the refusal then said was "Name them with --columns, or state --from to read it
// as something else", and a designer met that sentence in the GUI (field report, 2026-09-22). There
// is no --columns in a window. He had a perfectly ordinary headerless placement dump from his own
// CAD tool and the only route railRF offered him was a command-line flag.
//
// So the flag grew a control, and this file is the part of it that is not a view: what the columns
// PROBABLY are, and the canonical name for each role.
//
// ── AN INFERENCE AND A DECLARATION MUST NEVER READ THE SAME ────────────────────────────────────
//
// That is the GI series' standing rule 2 and the whole reason this returns EVIDENCE beside the
// roles. Nothing here reads a file: it proposes an assignment, states what it read that on, and the
// dialog shows the proposal over the file's own first rows so a wrong guess is visible before it is
// accepted. A guess applied silently here is the same catastrophe the refusal was written against —
// a rotation in the Y column produces a board whose parts are all in a stripe, which is not
// obviously wrong on a board nobody has seen.
//
// ── THE ONE THING IT WILL NOT GUESS ────────────────────────────────────────────────────────────
//
// THE UNITS. A headerless dump declares none, and mm and mils differ by a factor of 25.4: a file
// read at the wrong one still lands every part inside a plausible-looking box. PlacementFile
// already defaults to mm and says so as Defaulted; what this adds is that the dialog asks, because
// the person who exported the file knows and railRF cannot.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>What one column of a headerless placement table is.</summary>
public enum PlacementColumnRole
{
    /// <summary>Not read. The default for every column nothing recognised — a column left out is a
    /// column that contributes nothing, which is always safe; a column assigned wrongly is not.</summary>
    Ignore,

    /// <summary>The reference designator. Required.</summary>
    Refdes,

    /// <summary>The X coordinate. Required.</summary>
    X,

    /// <summary>The Y coordinate. Required.</summary>
    Y,

    /// <summary>The rotation, in degrees.</summary>
    Rotation,

    /// <summary>Which side of the board the part is on.</summary>
    Side,

    /// <summary>Whether the part is mirrored.</summary>
    Mirror,

    /// <summary>The land pattern.</summary>
    Footprint,
}

/// <summary>
/// A proposed column assignment and what it was read on.
/// </summary>
/// <param name="Roles">One role per column of the table, in column order.</param>
/// <param name="Evidence">One sentence per column that was assigned, saying what the values in it
/// looked like. <b>It is the whole point of the type</b> — see this file's header.</param>
public sealed record PlacementColumnInference(
    IReadOnlyList<PlacementColumnRole> Roles,
    IReadOnlyList<string> Evidence)
{
    /// <summary>True when the three columns a placement table cannot be read without were all
    /// found. A proposal that is short of one is still shown — with the gap named — because the
    /// user can fill it in, and a dialog that refused to open would leave them where the refusal
    /// did.</summary>
    public bool IsComplete =>
        Roles.Contains(PlacementColumnRole.Refdes)
        && Roles.Contains(PlacementColumnRole.X)
        && Roles.Contains(PlacementColumnRole.Y);
}

public static class PlacementColumns
{
    /// <summary>
    /// The canonical header name for each role — one of the aliases <see cref="PlacementFile"/>
    /// already matches, so a list built here reads exactly as a file that had named its own
    /// columns that way.
    /// </summary>
    /// <remarks>
    /// <b><see cref="PlacementColumnRole.Ignore"/> is the empty string on purpose.</b>
    /// <c>DelimitedTables.IndexOf</c> skips a header cell of zero length, so an ignored column
    /// cannot be matched by any role — which is stronger than inventing a placeholder name that
    /// might one day collide with a real alias.
    /// </remarks>
    public static string HeaderFor(PlacementColumnRole role) => role switch
    {
        PlacementColumnRole.Refdes    => "refdes",
        PlacementColumnRole.X         => "x",
        PlacementColumnRole.Y         => "y",
        PlacementColumnRole.Rotation  => "rotation",
        PlacementColumnRole.Side      => "side",
        PlacementColumnRole.Mirror    => "mirror",
        PlacementColumnRole.Footprint => "footprint",
        _                             => "",
    };

    /// <summary>What a role is called on the dialog.</summary>
    public static string LabelFor(PlacementColumnRole role) => role switch
    {
        PlacementColumnRole.Refdes    => "Reference",
        PlacementColumnRole.X         => "X",
        PlacementColumnRole.Y         => "Y",
        PlacementColumnRole.Rotation  => "Rotation",
        PlacementColumnRole.Side      => "Side",
        PlacementColumnRole.Mirror    => "Mirror",
        PlacementColumnRole.Footprint => "Footprint",
        _                             => "(not read)",
    };

    /// <summary>The roles in the order the dialog offers them.</summary>
    public static readonly PlacementColumnRole[] All =
    [
        PlacementColumnRole.Ignore,
        PlacementColumnRole.Refdes,
        PlacementColumnRole.X,
        PlacementColumnRole.Y,
        PlacementColumnRole.Rotation,
        PlacementColumnRole.Side,
        PlacementColumnRole.Mirror,
        PlacementColumnRole.Footprint,
    ];

    /// <summary>The column names to hand <see cref="PlacementFile.Read"/>'s <c>columns</c>
    /// parameter, one per column and in column order.</summary>
    public static IReadOnlyList<string> ToColumns(IReadOnlyList<PlacementColumnRole> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return [.. roles.Select(HeaderFor)];
    }

    /// <summary>How many rows the inference looks at. Enough to tell a coordinate column from a
    /// rotation column, few enough that a 50,000-row dump is not scanned in full to fill a dialog.</summary>
    private const int SampleRows = 64;

    /// <summary>How many of a column's sampled values must fit a pattern before it is proposed.
    /// Not all of them: a real dump carries a blank rotation on a part that has none and a stray
    /// comment row nobody stripped.</summary>
    private const double Majority = 0.8;

    /// <summary>
    /// Proposes a role for each column of <paramref name="table"/>, reading the VALUES — there is
    /// no header to read, which is the whole situation.
    /// </summary>
    /// <remarks>
    /// <b>Nothing here is applied.</b> The result is a proposal the dialog renders over the file's
    /// own first rows; see this file's header for why that separation is the design and not a
    /// nicety.
    ///
    /// <para><b>The order the roles are claimed in is the order of how RECOGNISABLE they are</b>,
    /// and each claim removes its column from what the next one may take. A side column holds two
    /// or three distinct tokens and nothing else does; a rotation holds angles; the coordinates are
    /// what is left that is numeric. Running it the other way round — coordinates first, by
    /// numeric-ness — takes the rotation column as X on any file whose first numeric column is the
    /// angle, which is the exact failure the original refusal names.</para>
    /// </remarks>
    /// <param name="firstDataRow">The row the data starts at. A headerless file is 0; the parameter
    /// exists so a caller that has stripped a preamble does not have to rebuild the table.</param>
    public static PlacementColumnInference Infer(DelimitedTable table, int firstDataRow = 0)
    {
        ArgumentNullException.ThrowIfNull(table);

        int columns = 0;
        var samples = new List<List<string>>();
        for (int r = firstDataRow; r < table.Rows.Count && samples.Count < SampleRows; r++)
        {
            var fields = table.Rows[r].Fields;
            if (fields.Count == 0) continue;
            columns = Math.Max(columns, fields.Count);
            samples.Add([.. fields.Select(f => (f ?? "").Trim().Trim('"'))]);
        }

        var roles = new PlacementColumnRole[columns];
        var evidence = new List<string>();
        if (columns == 0) return new PlacementColumnInference(roles, evidence);

        var taken = new bool[columns];
        var values = new List<string>[columns];
        for (int c = 0; c < columns; c++)
            values[c] = [.. samples.Select(s => c < s.Count ? s[c] : "").Where(v => v.Length > 0)];

        void Claim(int c, PlacementColumnRole role, string why)
        {
            roles[c] = role;
            taken[c] = true;
            evidence.Add($"Column {c + 1} read as {LabelFor(role)} — {why}.");
        }

        // ── SIDE, first: two or three distinct tokens from a fixed vocabulary ──────────────────
        for (int c = 0; c < columns && !roles.Contains(PlacementColumnRole.Side); c++)
        {
            if (taken[c] || values[c].Count == 0) continue;
            if (Fraction(values[c], IsSideToken) < Majority) continue;
            Claim(c, PlacementColumnRole.Side,
                  $"every value is one of {Distinct(values[c])}, which is a board side and nothing else");
        }

        // ── MIRROR, on the same footing: a two-valued flag column ──────────────────────────────
        for (int c = 0; c < columns && !roles.Contains(PlacementColumnRole.Mirror); c++)
        {
            if (taken[c] || values[c].Count == 0) continue;
            if (Fraction(values[c], IsMirrorToken) < Majority) continue;
            if (Distinct(values[c]).Length == 0) continue;
            Claim(c, PlacementColumnRole.Mirror,
                  $"every value is one of {Distinct(values[c])}, which reads as a mirror flag");
        }

        // ── REFERENCE: letters then digits, and mostly distinct ────────────────────────────────
        //
        // The second half matters. A footprint column is also letters-and-digits ("0402", "SOT23")
        // and would otherwise be a perfectly good reference designator — but a board repeats a land
        // pattern dozens of times and repeats a designator never, so the DISTINCTNESS is what
        // separates them, and it is a property of the board rather than of anyone's naming.
        int bestRefdes = -1;
        double bestRefdesScore = 0;
        for (int c = 0; c < columns; c++)
        {
            if (taken[c] || values[c].Count == 0) continue;
            double looksLike = Fraction(values[c], IsDesignator);
            if (looksLike < Majority) continue;

            double distinct = values[c].Distinct(StringComparer.OrdinalIgnoreCase).Count()
                              / (double)values[c].Count;
            double score = looksLike * distinct;
            if (score > bestRefdesScore) { bestRefdesScore = score; bestRefdes = c; }
        }
        if (bestRefdes >= 0)
            Claim(bestRefdes, PlacementColumnRole.Refdes,
                  "its values are letters followed by digits and are almost all distinct, which a "
                + "land pattern name is not");

        // ── ROTATION: numeric, inside ±360, and clustered ──────────────────────────────────────
        //
        // BEFORE the coordinates, and that ordering is the point. A rotation column is numeric, and
        // a coordinate rule that simply took the first two numeric columns would take it — which is
        // the "puts the rotation in the Y column silently" the refusal this replaces was written
        // against. An angle is bounded and repeats; a coordinate is neither.
        for (int c = 0; c < columns && !roles.Contains(PlacementColumnRole.Rotation); c++)
        {
            if (taken[c] || values[c].Count < 2) continue;
            if (Fraction(values[c], IsNumber) < Majority) continue;

            var numbers = values[c].Select(TryNumber).OfType<double>().ToList();
            if (numbers.Count == 0) continue;
            if (numbers.Any(v => v < -360.0 || v > 360.0)) continue;

            int distinct = numbers.Select(v => Math.Round(v, 3)).Distinct().Count();
            if (distinct > 24) continue;   // an angle column on a real board is a handful of values

            Claim(c, PlacementColumnRole.Rotation,
                  $"its values are numbers within ±360 taking only {distinct} distinct value(s), "
                + "which is an angle rather than a coordinate");
        }

        // ── X and Y: the two remaining numeric columns with the widest spread ──────────────────
        //
        // LEFT TO RIGHT, because every placement format in circulation writes X before Y and
        // nothing in the values themselves separates them — a board is not reliably wider than it
        // is tall. That is a convention rather than a measurement, and the evidence says so: it is
        // the one assignment a reader is most likely to need to swap.
        var numericLeft = new List<int>();
        for (int c = 0; c < columns; c++)
        {
            if (taken[c] || values[c].Count == 0) continue;
            if (Fraction(values[c], IsNumber) >= Majority) numericLeft.Add(c);
        }

        if (numericLeft.Count >= 2)
        {
            var bySpread = numericLeft
                .Select(c => (Column: c, Spread: SpreadOf(values[c])))
                .OrderByDescending(t => t.Spread)
                .Take(2)
                .OrderBy(t => t.Column)
                .ToList();

            Claim(bySpread[0].Column, PlacementColumnRole.X,
                  "it is one of the two numeric columns with the widest spread, and it is the "
                + "left-hand one — every placement format writes X first, which is a convention and "
                + "not a measurement, so check it");
            Claim(bySpread[1].Column, PlacementColumnRole.Y,
                  "it is the other of the two numeric columns with the widest spread");
        }

        // ── FOOTPRINT: what is left that is text and repeats ───────────────────────────────────
        for (int c = 0; c < columns && !roles.Contains(PlacementColumnRole.Footprint); c++)
        {
            if (taken[c] || values[c].Count == 0) continue;
            if (Fraction(values[c], IsNumber) >= Majority) continue;
            Claim(c, PlacementColumnRole.Footprint,
                  "it is text, it repeats, and nothing else claimed it — which is what a land "
                + "pattern name looks like");
        }

        return new PlacementColumnInference(roles, evidence);
    }

    // ── the tests, each one a property of the VALUES and never of a name ───────────────────────

    private static double Fraction(IReadOnlyList<string> values, Func<string, bool> test) =>
        values.Count == 0 ? 0 : values.Count(test) / (double)values.Count;

    private static string Distinct(IReadOnlyList<string> values) =>
        string.Join(", ",
            values.Select(v => v.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order()
                  .Take(4).Select(v => $"'{v}'"));

    private static bool IsSideToken(string v) => v.ToLowerInvariant() switch
    {
        "t" or "b" or "top" or "bottom" or "top side" or "bottom side"
            or "topside" or "bottomside" or "front" or "back" or "1" or "2" => true,
        _ => false,
    };

    private static bool IsMirrorToken(string v) => v.ToLowerInvariant() switch
    {
        "0" or "1" or "y" or "n" or "yes" or "no" or "true" or "false"
            or "mirror" or "nomirror" or "normal" or "mirrored" => true,
        _ => false,
    };

    /// <summary>Letters then digits, with an optional trailing letter — <c>C12</c>, <c>R7A</c>,
    /// <c>U101</c>. Deliberately NOT "contains a digit": that takes a land pattern.</summary>
    private static bool IsDesignator(string v)
    {
        int i = 0;
        while (i < v.Length && char.IsLetter(v[i])) i++;
        if (i == 0 || i > 4 || i >= v.Length) return false;

        int digits = 0;
        while (i < v.Length && char.IsAsciiDigit(v[i])) { i++; digits++; }
        if (digits == 0) return false;

        return i == v.Length || (i == v.Length - 1 && char.IsLetter(v[^1]));
    }

    private static bool IsNumber(string v) => TryNumber(v) is not null;

    private static double? TryNumber(string v) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;

    private static double SpreadOf(IReadOnlyList<string> values)
    {
        var numbers = values.Select(TryNumber).OfType<double>().ToList();
        return numbers.Count == 0 ? 0 : numbers.Max() - numbers.Min();
    }
}
