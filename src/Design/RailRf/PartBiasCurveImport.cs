// A capacitance-versus-DC-bias curve for ONE library row, from a supplier's characteristic-data
// export or from pasted text (owner request, 2026-09-23). Reading the table — its delimiter, header,
// units and preamble — is CapacitanceVoltageTable's, shared with the schematic's NonlinearC editor.
// What is here is what a library ROW makes possible to check, and the thinning.
//
// ── A CURVE AT THE WRONG SCALE IS REFUSED, NOT IMPORTED ─────────────────────────────────────────
//
// Derating only ever LOWERS capacitance, and never by ten at a curve's first point. So a curve whose
// first point is more than ten times the marked value, or under a tenth of it, is a unit error or
// another part's curve, and nothing is imported. Inside that band a first point more than a quarter
// off the marked value is imported and SAID — tolerance and the measurement's own AC level move it,
// and a reader should look.
//
// ── A CURVE IS THINNED TO WHAT LINEAR INTERPOLATION NEEDS ───────────────────────────────────────
//
// RailDerating reads a curve by linear interpolation, and a supplier's export samples a smooth curve
// every few tens of millivolts. Every point within ThinningTolerance of the straight line through the
// points kept either side of it is dropped (Ramer-Douglas-Peucker, measured vertically — which is the
// error interpolation makes at that bias), so a 201-row export becomes a curve somebody can read in
// the editor, and no bias reads further than that from the supplier's own number. The report says
// how many points were read and how many kept.

using CircuitRF.Design.Interchange;

namespace CircuitRF.Design.RailRf;

/// <summary>What a bias-curve import read. <see cref="Points"/> is empty where nothing may be
/// applied, and <see cref="Notes"/> then leads with why.</summary>
/// <param name="PointsRead">How many the source gave, before thinning.</param>
public sealed record PartBiasCurveImportResult(
    IReadOnlyList<PartBiasPoint> Points,
    int PointsRead,
    IReadOnlyList<string> Notes)
{
    public bool HasCurve => Points.Count > 0;
}

public static class PartBiasCurveImport
{
    /// <summary>How far, relative to the curve's largest capacitance, a dropped point may sit from
    /// the line the kept points draw through it.</summary>
    public const double ThinningTolerance = 0.002;

    /// <summary>Reads <paramref name="text"/> as a bias curve for <paramref name="row"/>.</summary>
    /// <param name="sourceName">The file name, or null for pasted text. A file name that names the
    /// part counts as the source naming it.</param>
    public static PartBiasCurveImportResult Read(string text, PartLibraryRow row, string? sourceName)
    {
        ArgumentNullException.ThrowIfNull(row);
        var table = CapacitanceVoltageTable.Read(text, sourceName);
        var notes = table.Notes.ToList();
        if (table.Samples.Count == 0) return new([], 0, notes);

        var read = table.Samples.Select(s => new PartBiasPoint(s.Volts, s.Farads)).ToList();

        // A ceramic's curve is symmetric in bias. Where both signs are given the positive half is the
        // curve; where only negative bias is, it is mirrored.
        if (read.Any(p => p.BiasVolts < 0))
        {
            if (read.Any(p => p.BiasVolts >= 0))
            {
                int negative = read.RemoveAll(p => p.BiasVolts < 0);
                notes.Add($"{negative} negative-bias point(s) were left out; a ceramic's curve is " +
                          "symmetric and the positive half was used.");
            }
            else
            {
                read = [.. read.Select(p => p with { BiasVolts = -p.BiasVolts })
                               .OrderBy(p => p.BiasVolts)];
                notes.Add("Every bias was negative; the curve was read at the magnitude of each.");
            }
        }

        // ── against the row ─────────────────────────────────────────────────────────────────
        if (row.CapacitanceFarads is { } marked && marked > 0)
        {
            double first = read[0].CapacitanceFarads, ratio = first / marked;
            string at = $"{RailDeratedCapacitance.Farads(first)} at {read[0].BiasVolts:0.###} V";
            if (ratio > 10 || ratio < 0.1)
            {
                notes.Insert(0, $"The curve's first point is {at} and {row.PartNumber} is marked " +
                                $"{RailDeratedCapacitance.Farads(marked)} — {ratio:0.###}× — so nothing " +
                                "was imported. That is a unit error or another part's curve.");
                return new([], read.Count, notes);
            }
            if (Math.Abs(ratio - 1) > 0.25)
                notes.Add($"The curve's first point is {at} and the row is marked " +
                          $"{RailDeratedCapacitance.Farads(marked)} ({ratio:P0}). Check that this is " +
                          $"{row.PartNumber}'s curve.");
        }

        if (row.PartNumber.Length > 0)
        {
            var named = Tokens(sourceName).Concat(table.Preamble.SelectMany(Tokens)).ToList();
            if (named.Count > 0 && !named.Any(t => SamePart(t, row.PartNumber)))
                notes.Add($"Nothing in {sourceName ?? "the pasted text"} names {row.PartNumber}. " +
                          "Check that this is its curve.");
        }

        double top = read[^1].BiasVolts;
        if (row.VoltageRatingV is { } rating && top < rating)
            notes.Add($"The curve stops at {top:0.###} V, below the part's {rating:0.###} V rating. A " +
                      "rail above it is derated at the last point, which is optimistic.");

        return new(Thin(read, ThinningTolerance), read.Count, notes);
    }

    /// <summary>Ramer-Douglas-Peucker on capacitance, measured vertically, relative to the curve's
    /// largest capacitance. The first and last points are always kept.</summary>
    internal static List<PartBiasPoint> Thin(IReadOnlyList<PartBiasPoint> points, double tolerance)
    {
        if (points.Count <= 2) return [.. points];
        double allowed = tolerance * points.Max(p => p.CapacitanceFarads);
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;

        var spans = new Stack<(int Lo, int Hi)>();
        spans.Push((0, points.Count - 1));
        while (spans.Count > 0)
        {
            var (lo, hi) = spans.Pop();
            if (hi - lo < 2) continue;
            var a = points[lo];
            var b = points[hi];
            double span = b.BiasVolts - a.BiasVolts;
            int worst = -1;
            double worstError = allowed;
            for (int i = lo + 1; i < hi; i++)
            {
                double t = span > 0 ? (points[i].BiasVolts - a.BiasVolts) / span : 0;
                double line = a.CapacitanceFarads + t * (b.CapacitanceFarads - a.CapacitanceFarads);
                double error = Math.Abs(points[i].CapacitanceFarads - line);
                if (error > worstError) { worstError = error; worst = i; }
            }
            if (worst < 0) continue;
            keep[worst] = true;
            spans.Push((lo, worst));
            spans.Push((worst, hi));
        }
        return [.. points.Where((_, i) => keep[i])];
    }

    private static IEnumerable<string> Tokens(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split([' ', '\t', ',', ';', '_', '.', '|', '#', '"'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The same part, or one of the two with its packaging code trimmed — the rule
    /// <see cref="PartLibraryTableImport"/> matches rows by.</summary>
    private static bool SamePart(string token, string partNumber) =>
        string.Equals(token, partNumber, StringComparison.OrdinalIgnoreCase)
        || (Math.Min(token.Length, partNumber.Length) >= PartLibraryTableImport.MinimumPrefixLength
            && (token.StartsWith(partNumber, StringComparison.OrdinalIgnoreCase)
                || partNumber.StartsWith(token, StringComparison.OrdinalIgnoreCase)));
}
