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
/// candidate is judged by the ladder's own response against the mask, so what it returns meets it.
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
    public static FilterValueSearchResult? Run(
        FilterResponse response, NetworkForm form, int startOrder, int maxOrder,
        double fcHz, double f1Hz, double f2Hz, double rippleDb, double sourceOhms,
        FilterFirstElement first, EllipticType requestedType, FilterMaskSpec mask,
        IReadOnlyList<(FilterLibrarySeries? Inductors, FilterLibrarySeries? Capacitors)> pairs,
        IProgress<string>? progress, CancellationToken ct, out string why, out double closestMissDb)
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
        foreach (var choice in FilterMask.Candidates(response, form, startOrder, maxOrder, fcHz, f1Hz, f2Hz,
                                                     rippleDb, sourceOhms, first, requestedType, mask, ct))
        {
            if (firstOrder == 0) firstOrder = choice.Order;
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
                var found = Fit(real, lSeries, cSeries, mask, coarse, fine, ct, out double miss);
                closestMissDb = Math.Min(closestMissDb, miss);
                if (found is null) continue;
                var (used, picks) = found.Value;
                return new FilterValueSearchResult(choice, lSeries, cSeries, picks, used, miss);
            }
        }
        why = firstOrder == 0
            ? "No design meets the mask with an LC ladder even at exact values, so no choice of values can."
            : $"No choice of values from orders {firstOrder} to {lastOrder}"
              + (pairs.Count > 1 ? $", over {pairs.Count} pairings of inductor and capacitor series," : "")
              + " meets the mask"
              + (double.IsFinite(closestMissDb) ? $" (the closest misses by {closestMissDb:0.###} dB)." : ".")
              + " A finer series, a wider transition or a looser mask does.";
        return null;
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
        CancellationToken ct, out double missDb)
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
            var loss = FilterLadder.InsertionLossDb(used, real.SourceOhms, real.LoadOhms, grid.All);
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
