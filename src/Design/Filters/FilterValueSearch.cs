using System.Numerics;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;

namespace CircuitRF.Design.Filters;

/// <summary>
/// What the search for a passing design settled on: the design (order, floor, edges, exact ladder),
/// the series its values come from, the value each element is built with, and how far inside the
/// mask the built ladder sits.
/// </summary>
public sealed record FilterValueSearchResult(
    FilterMaskChoice Choice,
    FilterLibrarySeries? Inductors,
    FilterLibrarySeries? Capacitors,
    IReadOnlyDictionary<string, (FilterLibrarySeries Series, FilterPart Part)> Picks,
    IReadOnlyList<FilterElement> Used,
    double MissDb);

/// <summary>
/// When a ladder meets its mask with exact values but not once snapped to a series or a library,
/// looks for one that does: through the designs the mask search offers (order, floor, re-centred
/// edges), through every pairing of an inductor series with a capacitor series it is given, and
/// through each element's neighbouring values in its series rather than only the nearest. Each
/// candidate is judged by the ladder's own response against the mask, a library part as its own
/// model's impedance when the caller supplies it, so what it returns meets it as placed.
/// </summary>
public static class FilterValueSearch
{
    /// <summary>How many values either side of the nearest one an element may move to.</summary>
    public const int Neighbours = 3;

    /// <summary>How many orders past the first one with a ladder the search tries.</summary>
    public const int OrderWindow = 6;

    /// <summary>How many of an order's designs (floors, edges) the search fits values to.</summary>
    public const int CandidatesPerOrder = 4;

    /// <summary>Sweeps of element-by-element improvement before a value set is given up on.</summary>
    private const int MaxSweeps = 8;

    /// <summary>
    /// The first design, lowest order first, whose ladder built from the series in
    /// <paramref name="pairs"/> meets <paramref name="mask"/>. Null with <paramref name="why"/> when
    /// none does; <paramref name="closestMissDb"/> then says how near the best one came.
    /// </summary>
    /// <param name="loadOhms">When given, only designs whose ladder ends in this load (<see cref="FilterMask.Find"/>).</param>
    public static FilterValueSearchResult? Run(
        FilterResponse response, NetworkForm form, int startOrder, int maxOrder,
        double fcHz, double f1Hz, double f2Hz, double rippleDb, double sourceOhms,
        FilterFirstElement first, EllipticType requestedType, FilterMaskSpec mask,
        IReadOnlyList<(FilterLibrarySeries? Inductors, FilterLibrarySeries? Capacitors)> pairs,
        Func<FilterPart, IReadOnlyList<double>, Complex[]?>? partImpedance,
        IProgress<string>? progress, CancellationToken ct, out string why, out double closestMissDb,
        double? loadOhms = null)
    {
        why = "";
        closestMissDb = double.PositiveInfinity;
        if (pairs.Count == 0)
        {
            why = "There is no series to choose values from.";
            return null;
        }
        var coarse = Grid(mask, 24);
        var fine = Grid(mask, 400);
        int firstOrder = 0;
        int lastOrder = 0, perOrder = 0;
        string shortlistNote = "";
        foreach (var choice in FilterMask.Candidates(response, form, startOrder, maxOrder, fcHz, f1Hz, f2Hz,
                                                     rippleDb, sourceOhms, first, requestedType, mask, ct, loadOhms))
        {
            if (firstOrder == 0)
            {
                firstOrder = choice.Order;
                // Many series: fit the few whose parts, as modelled, come nearest this ladder's elements.
                if (pairs.Count > MaxPairs && choice.Ladder.Realization is { } first0)
                    pairs = Shortlist(pairs, first0, mask, partImpedance, ct, out shortlistNote);
            }
            if (choice.Order > firstOrder + OrderWindow) break;
            if (choice.Order != lastOrder) perOrder = 0;
            lastOrder = choice.Order;
            // The shallowest floors first; past a few, a deeper floor only spends margin.
            if (++perOrder > CandidatesPerOrder) continue;
            if (choice.Ladder.Realization is not { } real) continue;
            foreach (var (lSeries, cSeries) in pairs)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Order {choice.Order}" +
                                 (choice.AstopDb > mask.AsDb + 1e-9 ? $", built for {choice.AstopDb:0.#} dB" : "") +
                                 (pairs.Count > 1 ? $", {lSeries?.Label ?? "exact"} with {cSeries?.Label ?? "exact"}" : ""));
                var found = Fit(real, lSeries, cSeries, mask, coarse, fine, partImpedance, ct, out double miss);
                closestMissDb = Math.Min(closestMissDb, miss);
                if (found is null) continue;
                var (used, picks) = found.Value;
                return new FilterValueSearchResult(choice, lSeries, cSeries, picks, used, miss);
            }
        }
        why = firstOrder == 0
            ? "No design meets the mask with an LC ladder even at exact values, so no choice of values can."
            : $"No choice of values from orders {firstOrder} to {lastOrder}"
              + (pairs.Count > 1 ? $", over {pairs.Count} pairings of inductor and capacitor series{shortlistNote}," : "")
              + " meets the mask"
              + (double.IsFinite(closestMissDb) ? $" (the closest misses by {closestMissDb:0.###} dB)." : ".")
              + " A finer series, a wider transition or a looser mask does.";
        return null;
    }

    /// <summary>The most pairings of series the search fits values over; more are shortlisted first.</summary>
    public const int MaxPairs = 10;

    /// <summary>How many inductor series, and how many capacitor series, a shortlist keeps.</summary>
    public const int ShortlistPerKind = 3;

    /// <summary>
    /// The pairings worth fitting when there are too many: the first pairing given (the one chosen),
    /// then the <see cref="ShortlistPerKind"/> inductor and capacitor series whose nearest parts, as
    /// modelled at the passband's centre, come closest to the ideal elements of <paramref name="real"/>.
    /// A series with no part within a factor of two of some element is last.
    /// </summary>
    private static IReadOnlyList<(FilterLibrarySeries? Inductors, FilterLibrarySeries? Capacitors)> Shortlist(
        IReadOnlyList<(FilterLibrarySeries? Inductors, FilterLibrarySeries? Capacitors)> pairs, FilterRealization real,
        FilterMaskSpec mask, Func<FilterPart, IReadOnlyList<double>, Complex[]?>? partImpedance, CancellationToken ct,
        out string note)
    {
        var (pass, _) = FilterMask.CheckFrequencies(mask, 24);
        double f0 = pass.Length > 0 ? Math.Sqrt(pass.Min() * pass.Max()) : 1e9;
        double w0 = 2 * Math.PI * f0;
        double Score(FilterLibrarySeries series)
        {
            double total = 0;
            int count = 0;
            foreach (var e in real.Elements.Where(e => e.Inductor == series.Inductors))
            {
                ct.ThrowIfCancellationRequested();
                var part = FilterValues.Nearest(e.Value, series.Parts);
                if (part is null || Math.Abs(Math.Log(part.Value / e.Value)) > Math.Log(2)) { total += 10; count++; continue; }
                Complex ideal = e.Inductor ? new Complex(0, w0 * e.Value) : new Complex(0, -1 / (w0 * e.Value));
                Complex actual = partImpedance is not null && part.Vendor is not null && partImpedance(part, [f0]) is { } z
                    ? z[0]
                    : e.Inductor ? new Complex(0, w0 * part.Value) : new Complex(0, -1 / (w0 * part.Value));
                total += Complex.Abs(Complex.Log(actual / ideal));
                count++;
            }
            return count == 0 ? 0 : total / count;
        }
        List<FilterLibrarySeries?> Best(IEnumerable<FilterLibrarySeries?> list) =>
            list.Distinct().OrderBy(x => x is null ? 0 : Score(x)).Take(ShortlistPerKind).ToList();
        var ls = Best(pairs.Select(p => p.Inductors));
        var cs = Best(pairs.Select(p => p.Capacitors));
        var result = new List<(FilterLibrarySeries?, FilterLibrarySeries?)> { pairs[0] };
        foreach (var l in ls)
            foreach (var c in cs)
                if (!(l == pairs[0].Inductors && c == pairs[0].Capacitors)) result.Add((l, c));
        int nl = pairs.Select(p => p.Inductors).Distinct().Count(), nc = pairs.Select(p => p.Capacitors).Distinct().Count();
        note = $" (the chosen pair, then the {ls.Count} of {nl} inductor and {cs.Count} of {nc} capacitor series nearest the ideal elements)";
        return result;
    }

    /// <summary>The mask's pass and stop frequencies at <paramref name="perBand"/> points a band.</summary>
    private static (double[] Pass, double[] Stop, double[] All) Grid(FilterMaskSpec mask, int perBand)
    {
        var (pass, stop) = FilterMask.CheckFrequencies(mask, perBand);
        return (pass, stop, [.. pass, .. stop]);
    }

    /// <summary>
    /// The values from the two series that put this ladder inside the mask, by coordinate descent:
    /// every element starts at its nearest value, then each in turn takes whichever of its
    /// neighbours lowers the worst miss most, until nothing improves or the mask is met.
    /// </summary>
    private static (List<FilterElement> Used, Dictionary<string, (FilterLibrarySeries, FilterPart)> Picks)? Fit(
        FilterRealization real, FilterLibrarySeries? lSeries, FilterLibrarySeries? cSeries, FilterMaskSpec mask,
        (double[] Pass, double[] Stop, double[] All) coarse, (double[] Pass, double[] Stop, double[] All) fine,
        Func<FilterPart, IReadOnlyList<double>, Complex[]?>? partImpedance, CancellationToken ct, out double missDb)
    {
        var elements = real.Elements;
        int n = elements.Count;
        var options = new List<(FilterLibrarySeries? Series, FilterPart? Part, double Value)>[n];
        for (int k = 0; k < n; k++)
        {
            var e = elements[k];
            var series = e.Inductor ? lSeries : cSeries;
            options[k] = series is null ? [(null, null, e.Value)] : Around(series, e.Value);
        }
        var pick = new int[n];
        var used = elements.ToList();
        void Set(int k, int j) { pick[k] = j; used[k] = elements[k] with { Value = options[k][j].Value }; }
        for (int k = 0; k < n; k++) Set(k, 0);

        // The worst miss is what decides, but it is flat wherever one frequency dominates; the mean
        // excess over the whole grid breaks those ties and pulls the rest of the response in too.
        (double Worst, double Score) Judge((double[] Pass, double[] Stop, double[] All) grid)
        {
            // A library part as its own model's impedance, where one is given: what is placed is judged.
            Complex[]?[]? z = null;
            if (partImpedance is not null)
            {
                z = new Complex[]?[n];
                for (int k = 0; k < n; k++)
                    if (options[k][pick[k]].Part is { Vendor: not null } part) z[k] = partImpedance(part, grid.All);
            }
            var loss = FilterLadder.InsertionLossDb(used, real.SourceOhms, real.LoadOhms, grid.All, z);
            double worst = double.NegativeInfinity, excess = 0;
            for (int i = 0; i < loss.Length; i++)
            {
                double m = i < grid.Pass.Length ? Clean(loss[i]) - mask.ApDb : mask.AsDb - Clean(loss[i]);
                worst = Math.Max(worst, m);
                if (m > 0) excess += Math.Min(m, 1e3);
            }
            if (double.IsNegativeInfinity(worst)) worst = 0;
            return (worst, Math.Max(worst, 0) + 0.25 * excess / Math.Max(1, loss.Length));
        }
        double Miss((double[] Pass, double[] Stop, double[] All) grid) => Judge(grid).Worst;

        var (miss, score) = Judge(coarse);
        bool Met() => miss <= FilterMask.LadderToleranceDb;
        for (int sweep = 0; sweep < MaxSweeps && !Met(); sweep++)
        {
            bool improved = false;
            // One element at a time, to any of its values.
            for (int k = 0; k < n && !Met(); k++)
            {
                ct.ThrowIfCancellationRequested();
                int keep = pick[k], best = keep;
                (double W, double S) bestJ = (miss, score);
                for (int j = 0; j < options[k].Count; j++)
                {
                    if (j == keep) continue;
                    Set(k, j);
                    var jj = Judge(coarse);
                    if (jj.Score < bestJ.S - 1e-12) { bestJ = jj; best = j; }
                }
                Set(k, best);
                if (best != keep) { (miss, score) = bestJ; improved = true; }
            }
            if (improved || Met()) continue;
            // Stuck: two neighbouring elements together, which is how a resonance or an impedance
            // level moves without one element alone getting worse first.
            for (int k = 0; k + 1 < n && !Met(); k++)
            {
                ct.ThrowIfCancellationRequested();
                int keepA = pick[k], keepB = pick[k + 1], bestA = keepA, bestB = keepB;
                (double W, double S) bestJ = (miss, score);
                for (int a = 0; a < options[k].Count; a++)
                for (int b = 0; b < options[k + 1].Count; b++)
                {
                    if (a == keepA && b == keepB) continue;
                    Set(k, a);
                    Set(k + 1, b);
                    var jj = Judge(coarse);
                    if (jj.Score < bestJ.S - 1e-12) { bestJ = jj; bestA = a; bestB = b; }
                }
                Set(k, bestA);
                Set(k + 1, bestB);
                if (bestA != keepA || bestB != keepB) { (miss, score) = bestJ; improved = true; }
            }
            if (!improved) break;
        }
        missDb = miss;
        if (!Met()) return null;
        // A coarse pass is confirmed on the full grid, the one the Designer itself checks against.
        missDb = Miss(fine);
        if (missDb > FilterMask.LadderToleranceDb) return null;
        var picks = new Dictionary<string, (FilterLibrarySeries, FilterPart)>(StringComparer.Ordinal);
        for (int k = 0; k < n; k++)
            if (options[k][pick[k]] is { Series: { } s, Part: { } p })
                picks[elements[k].Name] = (s, p);
        return (used, picks);

        static double Clean(double v) => double.IsNaN(v) ? double.PositiveInfinity : v;
    }

    /// <summary>
    /// The series' values around <paramref name="exact"/>: the nearest first, then up to
    /// <see cref="Neighbours"/> distinct values either side. One part per value.
    /// </summary>
    private static List<(FilterLibrarySeries? Series, FilterPart? Part, double Value)> Around(
        FilterLibrarySeries series, double exact)
    {
        var parts = series.Parts.Where(p => p.Value > 0)
                                .GroupBy(p => p.Value).Select(g => g.First())
                                .OrderBy(p => p.Value).ToList();
        if (parts.Count == 0) return [(null, null, exact)];
        int nearest = 0;
        double bestRatio = double.PositiveInfinity;
        for (int i = 0; i < parts.Count; i++)
        {
            double r = Math.Abs(Math.Log(parts[i].Value / exact));
            if (r < bestRatio) { bestRatio = r; nearest = i; }
        }
        var list = new List<(FilterLibrarySeries?, FilterPart?, double)> { (series, parts[nearest], parts[nearest].Value) };
        for (int d = 1; d <= Neighbours; d++)
        {
            if (nearest - d >= 0) list.Add((series, parts[nearest - d], parts[nearest - d].Value));
            if (nearest + d < parts.Count) list.Add((series, parts[nearest + d], parts[nearest + d].Value));
        }
        return list;
    }
}
