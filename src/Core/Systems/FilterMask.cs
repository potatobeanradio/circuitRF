using CircuitRF.Core.Matching;

namespace CircuitRF.Core.Systems;

/// <summary>
/// A passband / stopband mask, in Hz and dB, laid out as the LC Filter Designer's four edge boxes:
/// lowpass (A = Fp, B = Fs), highpass (A = Fs, B = Fp), bandpass (A = Fs1, B = Fp1, C = Fp2, D = Fs2)
/// and bandstop (A = Fp1, B = Fs1, C = Fs2, D = Fp2). A bandpass stop edge left at zero is no limit.
/// </summary>
public sealed record FilterMaskSpec(NetworkForm Form, double ApDb, double AsDb,
                                    double AHz, double BHz, double CHz, double DHz);

/// <summary>
/// One design the mask search settled on: the order, the stopband floor and band edges it was built
/// at (Fc for an edge form, F1/F2 for a band form), and both responses.
/// </summary>
public sealed record FilterMaskChoice(int Order, double AstopDb, EllipticType EllipticType,
                                      double FcHz, double F1Hz, double F2Hz,
                                      FilterNetwork Ideal, FilterLadderResult Ladder, double MissDb)
{
    /// <summary>True when the edges were moved off the given ones to balance the mask's two bands.</summary>
    public bool Balanced { get; init; }
}

/// <summary>
/// Whether a response stays inside a mask, and the smallest design that does. The closed-form order
/// (<see cref="FilterOrder.Minimum"/>) is where a search starts, never the answer on its own: a
/// formula that reads Ap where the ladder is built at its Ripple, or a family whose ladder has a
/// negative element at the requested floor, would otherwise hand back a filter outside its own mask.
/// </summary>
public static class FilterMask
{
    /// <summary>How far a response may sit outside the mask and still meet it, dB: numerical noise only.</summary>
    public const double ToleranceDb = 1e-6;

    /// <summary>
    /// The same, for a ladder netlist simulated as a circuit: its element values are written to a
    /// finite number of digits, which alone moves an edge sized with no margin by ~1e-5 dB.
    /// </summary>
    public const double LadderToleranceDb = 1e-3;

    /// <summary>The highest order the search tries.</summary>
    public const int MaxOrder = 31;

    /// <summary>
    /// How many orders past the first one whose exact ladder meets the mask the search keeps trying
    /// when only the caller's snapped check turns them away.
    /// </summary>
    public const int SnapOrderWindow = 6;

    /// <summary>
    /// The extra stopband depth, dB over As, the search builds a family with finite zeros at
    /// (inverse Chebyshev, elliptic). At a shallow floor those ladders can need a negative element;
    /// building deeper still meets an As floor, and is tried before a higher order.
    /// </summary>
    private static readonly double[] ExtraStopDb = [0, 1, 2, 3, 5, 7, 10, 15, 20, 30, 40];

    /// <summary>
    /// The frequencies a response is checked at: each band sampled densely (log-spaced), with its
    /// edges, out to a decade beyond the outermost edge. Pass and stop sets are kept apart.
    /// </summary>
    public static (double[] Pass, double[] Stop) CheckFrequencies(FilterMaskSpec m, int perBand = 400)
    {
        var pass = new List<double>();
        var stop = new List<double>();
        void Band(List<double> into, double lo, double hi)
        {
            if (!(lo > 0) || !(hi > lo) || !double.IsFinite(hi)) return;
            double r = Math.Log(hi / lo);
            for (int i = 0; i <= perBand; i++) into.Add(lo * Math.Exp(r * i / perBand));
        }
        switch (m.Form)
        {
            case NetworkForm.Lowpass:
                Band(pass, m.AHz / 1000, m.AHz);
                Band(stop, m.BHz, m.BHz * 10);
                break;
            case NetworkForm.Highpass:
                Band(stop, m.AHz / 10, m.AHz);
                Band(pass, m.BHz, m.BHz * 1000);
                break;
            case NetworkForm.Bandstop:
                Band(pass, m.AHz / 10, m.AHz);
                Band(stop, m.BHz, m.CHz);
                Band(pass, m.DHz, m.DHz * 10);
                break;
            default:
                if (m.AHz > 0) Band(stop, m.AHz / 10, m.AHz);
                Band(pass, m.BHz, m.CHz);
                if (m.DHz > 0) Band(stop, m.DHz, m.DHz * 10);
                break;
        }
        return (pass.ToArray(), stop.ToArray());
    }

    /// <summary>
    /// The worst amount, dB, by which <paramref name="lossDb"/> (insertion loss, positive) leaves the
    /// mask: more than Ap anywhere in a passband, or less than As anywhere in a stopband. Zero or less
    /// meets it.
    /// </summary>
    public static double WorstMissDb(FilterMaskSpec m, Func<double, double> lossDb, int perBand = 400)
    {
        var (pass, stop) = MissParts(m, lossDb, perBand);
        double worst = Math.Max(pass, stop);
        return double.IsNegativeInfinity(worst) ? 0 : worst;
    }

    /// <summary>
    /// <see cref="WorstMissDb"/> split by band: the passband's worst miss and the stopband's, each
    /// negative infinity when the mask has no such band.
    /// </summary>
    public static (double Pass, double Stop) MissParts(FilterMaskSpec m, Func<double, double> lossDb, int perBand = 400)
    {
        var (pass, stop) = CheckFrequencies(m, perBand);
        double p = double.NegativeInfinity, s = double.NegativeInfinity;
        foreach (double f in pass) p = Math.Max(p, Clean(lossDb(f)) - m.ApDb);
        foreach (double f in stop) s = Math.Max(s, m.AsDb - Clean(lossDb(f)));
        return (p, s);

        static double Clean(double v) => double.IsNaN(v) ? double.PositiveInfinity : v;
    }

    /// <summary>
    /// The design edges with the passband widened by <paramref name="s"/> (narrowed below 1), the
    /// centre kept: Fc scaled for an edge form, the bandwidth about the geometric centre for a band
    /// form (a bandstop's stopband narrowing as its passbands widen).
    /// </summary>
    public static (double Fc, double F1, double F2) ScaleEdges(NetworkForm form, double fc, double f1, double f2, double s)
    {
        switch (form)
        {
            case NetworkForm.Lowpass:  return (fc * s, f1, f2);
            case NetworkForm.Highpass: return (fc / s, f1, f2);
            default:
                double f0 = Math.Sqrt(f1 * f2);
                double bw = (f2 - f1) * (form == NetworkForm.Bandstop ? 1 / s : s);
                double lo = (-bw + Math.Sqrt(bw * bw + 4 * f0 * f0)) / 2;
                return (fc, lo, lo + bw);
        }
    }

    /// <summary>
    /// The design edges, as a widening of the given ones, that balance the passband's miss against
    /// the stopband's: the response sits as far inside the mask as these settings allow. The
    /// passband miss falls as the passband widens and the stopband's rises, so a bisection finds it.
    /// </summary>
    private static (double S, FilterNetwork? Ideal, double Miss) Balance(
        Func<double, FilterNetwork?> create, FilterMaskSpec mask, CancellationToken ct)
    {
        double Gap(double s, out FilterNetwork? ideal, out double miss)
        {
            ideal = create(s);
            if (ideal is null) { miss = double.PositiveInfinity; return double.NaN; }
            var net = ideal;
            var (p, st) = MissParts(mask, f => IdealLossDb(net, f));
            miss = Math.Max(p, st);
            return st - p;
        }
        double lo = 0.25, hi = 4;
        double gLo = Gap(lo, out _, out _), gHi = Gap(hi, out _, out _);
        if (double.IsNaN(gLo) || double.IsNaN(gHi) || gLo > 0 || gHi < 0) return (1, null, double.PositiveInfinity);
        for (int i = 0; i < 40 && hi / lo > 1 + 1e-9; i++)
        {
            ct.ThrowIfCancellationRequested();
            double mid = Math.Sqrt(lo * hi);
            double g = Gap(mid, out _, out _);
            if (double.IsNaN(g)) return (1, null, double.PositiveInfinity);
            if (g < 0) lo = mid; else hi = mid;
        }
        // The narrower end: its stopband miss is the one that sits at or under the passband's.
        Gap(lo, out var best, out double bestMiss);
        Gap(hi, out var other, out double otherMiss);
        return otherMiss < bestMiss ? (hi, other, otherMiss) : (lo, best, bestMiss);
    }

    /// <summary>The ideal response's insertion loss, dB, at <paramref name="hz"/>.</summary>
    public static double IdealLossDb(FilterNetwork ideal, double hz)
    {
        double mag = ideal.At(2 * Math.PI * hz).S21.Magnitude;
        return mag > 0 ? -20 * Math.Log10(mag) : double.PositiveInfinity;
    }

    /// <summary>
    /// The lowest order from <paramref name="startOrder"/> to <paramref name="maxOrder"/> whose ideal
    /// response meets <paramref name="mask"/> AND has an LC ladder (that <paramref name="accept"/>, when
    /// given, also takes: the caller's check of the ladder as it will be BUILT, its values snapped to a
    /// series); for a family with finite zeros, built at the shallowest floor from As down that does
    /// all of it. Null, with <paramref name="why"/>, when nothing in the range does.
    /// </summary>
    public static FilterMaskChoice? Find(
        FilterResponse response, NetworkForm form, int startOrder, int maxOrder,
        double fcHz, double f1Hz, double f2Hz, double rippleDb, double sourceOhms,
        FilterFirstElement first, EllipticType requestedType, FilterMaskSpec mask,
        Func<FilterMaskChoice, bool>? accept, CancellationToken ct, out string? why)
    {
        why = null;
        var state = new SearchState();
        int firstBuilt = 0;
        foreach (var choice in Enumerate(response, form, startOrder, maxOrder, fcHz, f1Hz, f2Hz, rippleDb,
                                         sourceOhms, first, requestedType, mask, state, ct))
        {
            // A ladder that meets the mask exactly but not once snapped is a series too coarse for
            // the mask; a few orders' more margin settles it either way, and the rest would only cost.
            if (firstBuilt > 0 && choice.Order > firstBuilt + SnapOrderWindow) break;
            if (accept is null || accept(choice)) return choice;
            state.LastRefusal = "Its values, snapped to the chosen series, leave the mask.";
            if (firstBuilt == 0) firstBuilt = choice.Order;
            // The re-centred edges are the snapped values' best chance at this order; a deeper floor
            // only spends the margin they need, so then the next order.
            if (choice.Balanced) state.SkipOrder = choice.Order;
        }
        double bestMiss = state.BestMiss;
        string? lastRefusal = state.LastRefusal;
        why = startOrder == maxOrder
            ? $"At order {startOrder} no {response} filter meets the mask with an LC ladder"
              + (double.IsFinite(bestMiss) && bestMiss > ToleranceDb ? $" (it misses by {bestMiss:0.###} dB)." : ".")
              + (lastRefusal is not null && !(bestMiss > ToleranceDb) ? " " + lastRefusal : "")
              + " A higher order, a wider transition or a looser mask does."
            : firstBuilt > 0
              ? $"From order {firstBuilt} to {Math.Min(maxOrder, firstBuilt + SnapOrderWindow)}, {response} ladders meet "
                + "the mask with exact values, but none does once its values are snapped to the chosen series. "
                + "Exact values, a finer series, a wider transition or a looser mask does."
            : $"No {response} filter up to order {maxOrder} meets the mask with an LC ladder. "
              + "A wider transition or a looser mask does.";
        return null;
    }

    /// <summary>
    /// Every design from <paramref name="startOrder"/> to <paramref name="maxOrder"/> whose ideal
    /// response meets the mask and whose ladder exists, lowest order first: at each order and floor,
    /// the edges as given, then the balanced edges (<see cref="FilterMaskChoice.Balanced"/>). Lazy,
    /// so a caller that stops early pays for nothing past it.
    /// </summary>
    public static IEnumerable<FilterMaskChoice> Candidates(
        FilterResponse response, NetworkForm form, int startOrder, int maxOrder,
        double fcHz, double f1Hz, double f2Hz, double rippleDb, double sourceOhms,
        FilterFirstElement first, EllipticType requestedType, FilterMaskSpec mask, CancellationToken ct) =>
        Enumerate(response, form, startOrder, maxOrder, fcHz, f1Hz, f2Hz, rippleDb, sourceOhms, first,
                  requestedType, mask, new SearchState(), ct);

    /// <summary>What a search learned on the way, and the order its caller wants it to leave.</summary>
    private sealed class SearchState
    {
        public string? LastRefusal;
        public double BestMiss = double.PositiveInfinity;
        public int SkipOrder;
    }

    private static IEnumerable<FilterMaskChoice> Enumerate(
        FilterResponse response, NetworkForm form, int startOrder, int maxOrder,
        double fcHz, double f1Hz, double f2Hz, double rippleDb, double sourceOhms,
        FilterFirstElement first, EllipticType requestedType, FilterMaskSpec mask,
        SearchState state, CancellationToken ct)
    {
        bool zeros = response is FilterResponse.InvChebyshev or FilterResponse.Elliptic;
        double[] extras = zeros ? ExtraStopDb : [0];
        for (int n = Math.Max(1, startOrder); n <= maxOrder; n++)
        {
            var type = response == FilterResponse.Elliptic ? EllipticTypes.Resolve(requestedType, n) : EllipticType.A;
            // No LC ladder exists at these orders: an even inverse Chebyshev, an even type-a elliptic.
            if (response == FilterResponse.InvChebyshev && (n & 1) == 0) continue;
            if (response == FilterResponse.Elliptic && type == EllipticType.A && (n & 1) == 0) continue;
            foreach (double extra in extras)
            {
                if (state.SkipOrder == n) break;
                double astop = mask.AsDb + extra;
                // The edges as given first; then the edges moved so the response sits as far inside
                // the mask as it can (Ap above the Ripple, or a deeper floor than As, leaves room to
                // widen or narrow the passband).
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (state.SkipOrder == n) break;
                    ct.ThrowIfCancellationRequested();
                    double fc = fcHz, f1 = f1Hz, f2 = f2Hz;
                    FilterNetwork? ideal;
                    double miss;
                    if (attempt == 0)
                    {
                        try
                        {
                            ideal = FilterNetwork.Create(response, form, n, fc, f1, f2, rippleDb, astop, 0, type);
                        }
                        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                        {
                            state.LastRefusal = e.Message;
                            continue;
                        }
                        var net0 = ideal;
                        miss = WorstMissDb(mask, f => IdealLossDb(net0, f));
                    }
                    else
                    {
                        var (sc, balanced, bMiss) = Balance(k =>
                        {
                            var (c, a, b) = ScaleEdges(form, fcHz, f1Hz, f2Hz, k);
                            try { return FilterNetwork.Create(response, form, n, c, a, b, rippleDb, astop, 0, type); }
                            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return null; }
                        }, mask, ct);
                        if (balanced is null) continue;
                        (fc, f1, f2) = ScaleEdges(form, fcHz, f1Hz, f2Hz, sc);
                        ideal = balanced;
                        miss = bMiss;
                    }
                    state.BestMiss = Math.Min(state.BestMiss, miss);
                    if (miss > ToleranceDb) continue;
                    FilterLadderResult ladder;
                    try
                    {
                        ladder = FilterLadder.Synthesize(response, form, n, fc, f1, f2, rippleDb, sourceOhms,
                                                         first, astop, type);
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                    {
                        state.LastRefusal = e.Message;
                        continue;
                    }
                    if (!ladder.Ok)
                    {
                        state.LastRefusal = ladder.Refusal?.Message;
                        continue;
                    }
                    yield return new FilterMaskChoice(n, astop, type, fc, f1, f2, ideal, ladder, miss)
                        { Balanced = attempt == 1 };
                }
            }
        }
    }
}
