using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Matching;

namespace CircuitRF.Core.Systems;

/// <summary>Which end of the ladder the first arm is. Parallel is a shunt arm.</summary>
public enum FilterFirstElement
{
    /// <summary>The first arm is in series with the through path.</summary>
    Series,

    /// <summary>The first arm is a shunt to ground.</summary>
    Parallel,
}

/// <summary>
/// One arm of a synthesised ladder, in base SI. A bandpass or bandstop arm is two elements that
/// share a role: a series resonator, a shunt resonator, a parallel pair in the through path, or a
/// series pair to ground.
/// </summary>
public sealed record FilterElement(
    string Name,
    bool Inductor,
    bool Shunt,
    double Value,
    string NodeA,
    string NodeB)
{
    /// <summary>Henries or farads, whichever <see cref="Inductor"/> says.</summary>
    public string Component => Inductor ? "L" : "C";
}

/// <summary>Why a specification did not become a ladder. The ideal response is unaffected.</summary>
public sealed record FilterLadderRefusal(string Message);

/// <summary>
/// One doubly-terminated ladder for a specification: the elements, the load the synthesis actually
/// produced, and the netlist those elements are.
/// </summary>
public sealed class FilterRealization
{
    /// <summary>Series or parallel first arm.</summary>
    public FilterFirstElement First { get; init; }

    /// <summary>The source resistance the elements were scaled to, ohms.</summary>
    public double SourceOhms { get; init; }

    /// <summary>
    /// The load the prototype's own termination ratio produces, ohms. An even-order Chebyshev does
    /// not come out equal to the source; that ratio is the family's, not a rounding.
    /// </summary>
    public double LoadOhms { get; init; }

    /// <summary>The arms, source end first.</summary>
    public IReadOnlyList<FilterElement> Elements { get; init; } = [];

    /// <summary>The normalised g-values, source resistance first and load ratio last.</summary>
    public IReadOnlyList<double> G { get; init; } = [];

    /// <summary>The ladder as a <c>.cnl</c> fragment: two Terms and the elements between them.</summary>
    public string Cnl { get; init; } = "";
}

/// <summary>What <see cref="FilterLadder.Synthesize"/> returns. One of the two is always set.</summary>
public sealed class FilterLadderResult
{
    /// <summary>The ladder, when the family has an all-pole realisation.</summary>
    public FilterRealization? Realization { get; init; }

    /// <summary>Why there is no ladder. The ideal <see cref="FilterNetwork"/> still answers.</summary>
    public FilterLadderRefusal? Refusal { get; init; }

    /// <summary>True when <see cref="Realization"/> is set.</summary>
    public bool Ok => Realization is not null;
}

/// <summary>
/// The doubly-terminated LC ladder of an all-pole filter prototype.
/// </summary>
/// <remarks>
/// The g-values of an all-pole family are the continued fraction of the prototype's own input
/// impedance, extracted by <see cref="MatchPrototypes.Extract"/>. Inverse Chebyshev and elliptic
/// have zeros on the axis, so those ladders are tanks: each finite zero is removed as a parallel
/// LC in the through path or a series LC to ground, after a partial removal at infinity when the
/// zero is not yet a pole of the reactance. An even order of either family keeps a finite floor
/// at infinite frequency, which no LC ladder can, and is refused in favour of an odd order.
///
/// <para>Denormalisation is the handbook's. With <c>R</c> the source resistance and <c>ω</c> the
/// cutoff: a lowpass series arm is <c>L = g R / ω</c> and a shunt arm is <c>C = g / (R ω)</c>.
/// Highpass exchanges each inductor for a capacitor of <c>1/(g R ω)</c> and each capacitor for an
/// inductor of <c>R /(g ω)</c>. Bandpass replaces each arm by a resonator at the geometric centre.
/// Bandstop replaces each arm by the dual resonator.</para>
/// </remarks>
public static class FilterLadder
{
    /// <summary>
    /// The ladder for <paramref name="response"/> at <paramref name="order"/>, scaled to
    /// <paramref name="sourceOhms"/> and to the cutoff <paramref name="form"/> names.
    /// </summary>
    /// <param name="fcHz">Cutoff for lowpass and highpass.</param>
    /// <param name="f1Hz">Lower cutoff edge for bandpass (passband) and bandstop (stopband).</param>
    /// <param name="f2Hz">Upper cutoff edge.</param>
    /// <param name="rippleDb">Read by Chebyshev. Ignored by the other all-pole families.</param>
    /// <param name="first">Which arm comes first. Parallel is the dual of series.</param>
    public static FilterLadderResult Synthesize(
        FilterResponse response, NetworkForm form, int order,
        double fcHz, double f1Hz, double f2Hz,
        double rippleDb, double sourceOhms, FilterFirstElement first, double astopDb = 40)
    {
        if (order < 1)
            return Refuse($"A filter order is at least 1; got {order}.");
        if (!(sourceOhms > 0) || !double.IsFinite(sourceOhms))
            return Refuse($"The source resistance must be a positive number of ohms; got {sourceOhms:G6}.");

        FilterPrototype proto;
        try
        {
            proto = FilterPrototype.Create(response, order, rippleDb, astopDb);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException)
        {
            return Refuse(e.Message);
        }

        if (proto.P.Length > 1)
            return SynthesizeWithZeros(proto, form, fcHz, f1Hz, f2Hz, sourceOhms, first);

        double[]? g = PrototypeG(proto);
        if (g is null)
            return Refuse(
                $"{response} order {order} did not produce a ladder with positive elements.");

        // Extract's last entry is a resistance ratio after an even number of removals and a
        // conductance ratio after an odd number — each removal swaps impedance for admittance.
        // Reading an odd ladder as a resistance inverts the load.
        double raw = g[^1];
        double loadNorm = order % 2 == 0 ? raw : 1.0 / raw;
        if (!(loadNorm > 0) || !double.IsFinite(loadNorm))
            return Refuse($"{response} order {order} produced a non-positive load.");

        if (!Cutoff(form, fcHz, f1Hz, f2Hz, out double wEdge, out double w0, out double bw, out string? why))
            return Refuse(why!);

        // Parallel-first is the same g-vector with the arms swapped: an element that was a series
        // inductor becomes a shunt capacitor of the same normalised value. For a symmetric prototype
        // that is the dual and the response is unchanged. For an asymmetric one (an even-order
        // Chebyshev) it is the other canonical ladder, and its load is still the prototype's ratio.
        bool shuntFirst = first == FilterFirstElement.Parallel;
        double rs = sourceOhms;
        double rl = loadNorm * sourceOhms;
        IReadOnlyList<double> used = g;

        var elements = new List<FilterElement>();
        string node = "p1";
        var ids = new Ids();
        for (int k = 1; k <= order; k++)
        {
            bool shunt = shuntFirst ? k % 2 == 1 : k % 2 == 0;
            double gk = used[k];
            if (!(gk > 0) || !double.IsFinite(gk))
                return Refuse($"{response} order {order} produced a non-positive element.");
            node = AddArm(elements, form, gk, shunt, rs, wEdge, w0, bw, node, ref ids);
        }

        string cnl = ToCnl(elements, rs, rl);
        return new FilterLadderResult
        {
            Realization = new FilterRealization
            {
                First = first,
                SourceOhms = rs,
                LoadOhms = rl,
                Elements = elements,
                G = used,
                Cnl = cnl,
            },
        };
    }

    /// <summary>
    /// The g-vector <c>[g0=1, g1..gn, gLoad]</c> of an all-pole prototype, or null when the
    /// continued fraction has no positive-element realisation.
    /// </summary>
    public static double[]? PrototypeG(FilterPrototype proto)
    {
        ArgumentNullException.ThrowIfNull(proto);
        if (proto.P.Length != 1) return null;

        double[] f = Scale(proto.F, proto.Alpha);
        foreach (double sign in (double[])[1.0, -1.0])
        {
            double[] a = MatchPoly.Trim(MatchPoly.Sub(proto.E, Scale(f, sign)));
            double[] b = MatchPoly.Trim(MatchPoly.Add(proto.E, Scale(f, sign)));
            double[] top, bot;
            if (a.Length == b.Length + 1) { top = a; bot = b; }
            else if (b.Length == a.Length + 1) { top = b; bot = a; }
            else continue;

            double[]? g = MatchPrototypes.Extract(top, bot, proto.Order);
            if (g is not null) return g;
        }
        return null;
    }

    private static bool Cutoff(NetworkForm form, double fcHz, double f1Hz, double f2Hz,
                               out double wEdge, out double w0, out double bw, out string? why)
    {
        wEdge = w0 = bw = 0;
        why = null;
        if (form is NetworkForm.Bandpass or NetworkForm.Bandstop)
        {
            if (!(f1Hz > 0) || !(f2Hz > f1Hz) || !double.IsFinite(f2Hz))
            {
                why = $"A {form.ToString().ToLowerInvariant()} filter needs 0 < F1 < F2; " +
                      $"got F1 = {f1Hz:G6} Hz, F2 = {f2Hz:G6} Hz.";
                return false;
            }
            double w1 = 2 * Math.PI * f1Hz, w2 = 2 * Math.PI * f2Hz;
            w0 = Math.Sqrt(w1 * w2);
            bw = w2 - w1;
            wEdge = bw;
            return true;
        }
        if (!(fcHz > 0) || !double.IsFinite(fcHz))
        {
            why = $"A lowpass or highpass filter needs a positive cutoff; got Fc = {fcHz:G6} Hz.";
            return false;
        }
        wEdge = 2 * Math.PI * fcHz;
        return true;
    }

    /// <summary>Adds one prototype arm and returns the through-path node on its far side.</summary>
    private struct Ids { public int Node, L, C; }

    private static string AddArm(List<FilterElement> elements, NetworkForm form, double gk,
                                 bool shunt, double rs, double wEdge, double w0, double bw,
                                 string node, ref Ids ids)
    {
        // Lowpass / highpass: one element. The prototype gk is an inductor when the arm is series
        // and a capacitor when it is shunt; highpass exchanges them.
        if (form is NetworkForm.Lowpass or NetworkForm.Highpass)
        {
            bool inductor = form == NetworkForm.Lowpass ? !shunt : shunt;
            double value = inductor
                ? (form == NetworkForm.Lowpass ? gk * rs / wEdge : rs / (gk * wEdge))
                : (form == NetworkForm.Lowpass ? gk / (rs * wEdge) : 1.0 / (gk * rs * wEdge));
            return PlaceOne(elements, inductor, shunt, value, node, ref ids);
        }

        // Bandpass: a series arm becomes a series resonator, a shunt arm a parallel resonator,
        // both resonant at ω0. L = g R / BW for a series arm, C = g / (R BW) for a shunt arm,
        // and the partner is 1/(ω0² times that).
        if (form == NetworkForm.Bandpass)
        {
            if (!shunt)
            {
                double l = gk * rs / bw;
                double c = bw / (w0 * w0 * gk * rs);
                string mid = PlaceOne(elements, inductor: true, shunt: false, l, node, ref ids);
                return PlaceOne(elements, inductor: false, shunt: false, c, mid, ref ids);
            }
            double cs = gk / (rs * bw);
            double ls = rs * bw / (w0 * w0 * gk);
            PlaceOne(elements, inductor: false, shunt: true, cs, node, ref ids);
            PlaceOne(elements, inductor: true, shunt: true, ls, node, ref ids);
            return node;
        }

        // Bandstop: s → BW·s / (s² + ω0²). A series inductor becomes a parallel LC in the through
        // path; a shunt capacitor becomes a series LC from the through node to ground. Both elements
        // of that stub are marked shunt so the load stays on the through path.
        if (!shunt)
        {
            double c = 1.0 / (rs * gk * bw);
            double l = rs * gk * bw / (w0 * w0);
            string far = Next(ref ids);
            PlacePair(elements, node, far, l, c, ref ids);
            return far;
        }
        double lsh = rs / (gk * bw);
        double csh = gk * bw / (rs * w0 * w0);
        string stub = Next(ref ids);
        elements.Add(El(true, node, stub, lsh, true, ref ids));
        elements.Add(El(false, stub, "0", csh, true, ref ids));
        return node;
    }

    private static string PlaceOne(List<FilterElement> elements, bool inductor, bool shunt,
                                   double value, string node, ref Ids ids)
    {
        if (shunt)
        {
            elements.Add(El(inductor, node, "0", value, true, ref ids));
            return node;
        }
        string far = Next(ref ids);
        elements.Add(El(inductor, node, far, value, false, ref ids));
        return far;
    }

    /// <summary>A parallel LC between two through-path nodes. Both elements share the two nodes.</summary>
    private static void PlacePair(List<FilterElement> elements, string a, string b,
                                  double henries, double farads, ref Ids ids)
    {
        elements.Add(El(true, a, b, henries, false, ref ids));
        elements.Add(El(false, a, b, farads, false, ref ids));
    }

    private static FilterElement El(bool inductor, string nodeA, string nodeB, double value,
                                    bool shunt, ref Ids ids)
    {
        int n = inductor ? ++ids.L : ++ids.C;
        return new FilterElement(inductor ? $"L{n}" : $"C{n}", inductor, shunt, value, nodeA, nodeB);
    }

    private static string Next(ref Ids ids) => $"n{++ids.Node}";

    /// <summary>The elements as a <c>.cnl</c> fragment terminated in <paramref name="rs"/> and
    /// <paramref name="rl"/>.</summary>
    public static string ToCnl(IReadOnlyList<FilterElement> elements, double rs, double rl)
    {
        // Names and nodes were assigned with one counter, so an element and a node can share a
        // number. That is fine: a node is `n3` and an element is `L3`, and the netlist reader
        // does not confuse them. What is NOT fine is two elements with one name. El() uses the
        // same counter as Next(), so names are unique because the counter only grows.
        var sb = new StringBuilder();
        sb.Append("Term:T1  p1 0  Num=1 Z=").AppendLine(Num(rs));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in elements)
        {
            string name = e.Name;
            if (!seen.Add(name)) name += "_" + seen.Count;
            sb.Append(e.Component).Append(':').Append(name).Append("  ")
              .Append(e.NodeA).Append(' ').Append(e.NodeB).Append("  ")
              .Append(e.Component).Append('=').AppendLine(Num(e.Value));
        }
        // The through path ends at the last series element's far node, or at p1 when every arm
        // was a shunt. The last node that is not ground and not an internal stub is the load.
        string load = LoadNode(elements);
        sb.Append("Term:T2  ").Append(load).Append(" 0  Num=2 Z=").AppendLine(Num(rl));
        return sb.ToString();
    }

    /// <summary>
    /// The through-path node the load sits on: the far node of the last series element, else the
    /// source node when the ladder is a single shunt.
    /// </summary>
    public static string LoadNode(IReadOnlyList<FilterElement> elements)
    {
        string node = "p1";
        foreach (var e in elements)
        {
            if (e.Shunt) continue;
            if (e.NodeA == node) node = e.NodeB;
        }
        return node;
    }

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static double[] Scale(double[] a, double k)
    {
        var r = new double[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = a[i] * k;
        return r;
    }

    private static FilterLadderResult Refuse(string message) =>
        new() { Refusal = new FilterLadderRefusal(message) };

    // ── Finite transmission zeros (inverse Chebyshev, elliptic) ──────────────

    /// <summary>One arm of the normalised (1 rad/s, 1 Ω) lowpass ladder.</summary>
    private readonly record struct NormArm(NormKind Kind, double L, double C);

    private enum NormKind { SeriesL, ShuntC, SeriesC, ShuntL, SeriesParallel, ShuntSeries }

    /// <summary>
    /// A ladder whose transmission zeros sit on the jω axis. The open-circuit impedance of the
    /// lossless two-port is a reactance, built from the even and odd parts of <c>E</c> and
    /// <c>F</c>, and each finite zero is removed as a tank: a parallel LC in the through path or
    /// a series LC to ground. Poles at infinity come off as a single L or C, which is the Cauer
    /// step the all-pole families already use.
    /// </summary>
    private static FilterLadderResult SynthesizeWithZeros(
        FilterPrototype proto, NetworkForm form, double fcHz, double f1Hz, double f2Hz,
        double sourceOhms, FilterFirstElement first)
    {
        if (!Cutoff(form, fcHz, f1Hz, f2Hz, out double wEdge, out double w0, out double bw, out string? why))
            return Refuse(why!);

        // An even order keeps |S21| finite at infinite frequency. Every LC ladder is an open
        // there, so the two cannot be the same network.
        if (proto.P.Length == proto.E.Length &&
            Math.Abs(proto.Beta * proto.P[0] / proto.E[0]) > 1e-8)
            return Refuse(
                $"{proto.Response} of even order keeps a finite floor at infinite frequency, and a " +
                "ladder of inductors and capacitors is an open there. An odd order is the ladder.");

        double[] f = Scale(proto.F, proto.Alpha);
        var zeros = StopZeros(proto.P);
        bool wantShunt = first == FilterFirstElement.Parallel;

        List<NormArm>? tight = null, fit = null;
        double best = double.PositiveInfinity, bestFit = double.PositiveInfinity;
        foreach (var (n, d) in Reactances(proto.E, f))
        {
            if (!TryExtract(n, d, zeros, out var got)) continue;
            // A Cauer step on a coefficient that only exists because two polynomials almost cancelled
            // leaves an element near 1e-16. On a lowpass it is invisible. On a bandstop that element
            // is inverted, and 1e-16 becomes a farad.
            got.RemoveAll(a => a.L < 1e-8 && a.C < 1e-8);
            if (got.Count == 0 || !ZerosLand(got, zeros)) continue;
            double err = ZinError(got, proto);
            if (!double.IsFinite(err)) continue;
            if (err < best) { best = err; tight = got; }
            bool startsShunt = got[0].Kind is NormKind.ShuntC or NormKind.ShuntL or NormKind.ShuntSeries;
            if (startsShunt == wantShunt && err < bestFit) { bestFit = err; fit = got; }
        }
        // The end the user asked for, when that extraction is the same impedance. Otherwise the
        // tightest one, which is the other end of the same ladder.
        List<NormArm>? arms = fit is not null && bestFit <= Math.Max(1e-4, best * 50) ? fit : tight;
        if (arms is not null && Math.Min(best, bestFit) > 1e-2) arms = null;
        if (arms is null)
            return Refuse((proto.Order & 1) == 0
                ? $"{proto.Response} of even order keeps a finite floor at infinite frequency, and a " +
                  "ladder of inductors and capacitors is an open there. An odd order is the ladder."
                : $"{proto.Response} order {proto.Order} did not produce a ladder with positive elements.");

        var elements = new List<FilterElement>();
        string node = "p1";
        var ids = new Ids();
        foreach (var arm in arms)
            node = PlaceNorm(elements, arm, form, sourceOhms, wEdge, w0, bw, node, ref ids);

        // The reactance is the open-circuit impedance of a network terminated in 1 Ω, so the load
        // is the source resistance. An even-order elliptic can want a different ratio; the plotted
        // ladder says which load it was built for, and the ideal block still takes the typed pair.
        return new FilterLadderResult
        {
            Realization = new FilterRealization
            {
                First = first,
                SourceOhms = sourceOhms,
                LoadOhms = sourceOhms,
                Elements = elements,
                G = [],
                Cnl = ToCnl(elements, sourceOhms, sourceOhms),
            },
        };
    }

    /// <summary>Positive stopband zeros, one per conjugate pair, ascending.</summary>
    private static double[] StopZeros(double[] p)
    {
        var found = new List<double>();
        foreach (var r in MatchPoly.Roots(p))
        {
            if (Math.Abs(r.Real) > 1e-6 * (1.0 + r.Magnitude)) continue;
            double w = Math.Abs(r.Imaginary);
            if (w < 1e-6) continue;
            if (found.Any(z => Math.Abs(z - w) < 1e-5 * (1 + w))) continue;
            found.Add(w);
        }
        found.Sort();
        return [.. found];
    }

    /// <summary>The four reactance candidates: both even/odd splits, and <c>F</c> with either sign.</summary>
    private static IEnumerable<(double[] N, double[] D)> Reactances(double[] e, double[] f)
    {
        foreach (double sign in (double[])[1.0, -1.0])
        {
            var fs = Scale(f, sign);
            Split(e, out var ee, out var eo);
            Split(fs, out var fe, out var fo);
            var a = MatchPoly.Trim(MatchPoly.Add(ee, fe));
            var b = MatchPoly.Trim(MatchPoly.Sub(eo, fo));
            var c = MatchPoly.Trim(MatchPoly.Add(eo, fo));
            var d = MatchPoly.Trim(MatchPoly.Sub(ee, fe));
            yield return (a, b);
            yield return (b, a);
            yield return (c, d);
            yield return (d, c);
        }
    }

    private static void Split(double[] p, out double[] even, out double[] odd)
    {
        even = new double[p.Length];
        odd = new double[p.Length];
        int deg = p.Length - 1;
        for (int i = 0; i < p.Length; i++)
        {
            if (((deg - i) & 1) == 0) even[i] = p[i];
            else odd[i] = p[i];
        }
    }

    private static bool TryExtract(double[] n0, double[] d0, double[] zeros, out List<NormArm> arms)
    {
        arms = [];
        return Recur(n0, d0, [.. zeros], arms, 0);
    }

    /// <summary>
    /// One extraction step. A finite zero that is already a pole of the reactance comes off whole,
    /// as a parallel LC in the through path or a series LC to ground. A zero that is not yet a pole
    /// is brought there by a partial removal at infinity — a series L or a shunt C small enough
    /// that the remainder is zero at that frequency — and the next step takes the tank. With no
    /// zeros left, the rest is the ordinary Cauer step.
    /// </summary>
    private static bool Recur(double[] n, double[] d, List<double> left, List<NormArm> arms, int depth)
    {
        if (depth > 48) return false;
        Normalise(ref n, ref d);
        n = MatchPoly.Trim(n);
        d = MatchPoly.Trim(d);
        if (Degree(n) <= 0 && Degree(d) <= 0)
            return left.Count == 0 && arms.Count > 0 && AllPositive(arms);

        for (int i = 0; i < left.Count; i++)
        {
            double w = left[i], wz2 = w * w;
            if (Divides(d, wz2, out var q) && Tank(n, q, w) is { } series)
            {
                var removed = MatchPoly.Sub(n, Scale(MulByS(q), 1.0 / series.C));
                if (Divides(removed, wz2, out var n2) && Take(n2, q, left, i, series, arms, depth))
                    return true;
            }
            if (Divides(n, wz2, out q) && Tank(d, q, w) is { } asSeriesForm)
            {
                // Tank() answers the parallel-tank reading of the residue. A pole of the admittance
                // is the dual: a series LC to ground, L and C exchanged.
                double k = 1.0 / asSeriesForm.C;
                var removed = MatchPoly.Sub(d, Scale(MulByS(q), k));
                var shunt = new NormArm(NormKind.ShuntSeries, asSeriesForm.C, asSeriesForm.L);
                if (Divides(removed, wz2, out var d2) && Take(q, d2, left, i, shunt, arms, depth))
                    return true;
            }
        }

        int dn = Degree(n), dd = Degree(d);
        if (left.Count == 0)
        {
            if (dn == dd + 1)
            {
                double l = n[0] / d[0];
                if (!(l > 0) || !double.IsFinite(l)) return false;
                arms.Add(new NormArm(NormKind.SeriesL, l, 0));
                return Recur(MatchPoly.Sub(n, Scale(MulByS(d), l)), d, left, arms, depth + 1)
                       || Drop(arms);
            }
            if (dd == dn + 1)
            {
                double c = d[0] / n[0];
                if (!(c > 0) || !double.IsFinite(c)) return false;
                arms.Add(new NormArm(NormKind.ShuntC, 0, c));
                return Recur(n, MatchPoly.Sub(d, Scale(MulByS(n), c)), left, arms, depth + 1)
                       || Drop(arms);
            }
            return false;
        }

        // Partial removal at infinity, toward one remaining zero. Several zeros can accept a
        // positive partial; each is tried, and the first that finishes the ladder wins.
        if (dn == dd + 1 && n[0] / d[0] is var lFull && lFull > 0 && double.IsFinite(lFull))
        {
            for (int i = 0; i < left.Count; i++)
            {
                if (Partial(n, d, left[i]) is not { } l || !(l > 1e-9) || l >= lFull * 0.999) continue;
                arms.Add(new NormArm(NormKind.SeriesL, l, 0));
                if (Recur(MatchPoly.Sub(n, Scale(MulByS(d), l)), d, left, arms, depth + 1)) return true;
                if (!Drop(arms)) return false;
            }
        }
        if (dd == dn + 1 && d[0] / n[0] is var cFull && cFull > 0 && double.IsFinite(cFull))
        {
            for (int i = 0; i < left.Count; i++)
            {
                var y = AdmittanceAt(n, d, left[i]);
                if (y is null) continue;
                double c = y.Value.Imaginary / left[i];
                if (!(c > 1e-9) || c >= cFull * 0.999) continue;
                arms.Add(new NormArm(NormKind.ShuntC, 0, c));
                if (Recur(n, MatchPoly.Sub(d, Scale(MulByS(n), c)), left, arms, depth + 1)) return true;
                if (!Drop(arms)) return false;
            }
        }
        return false;
    }

    private static bool Take(double[] n, double[] d, List<double> left, int index, NormArm arm,
                             List<NormArm> arms, int depth)
    {
        var next = new List<double>(left);
        next.RemoveAt(index);
        arms.Add(arm);
        return Recur(n, d, next, arms, depth + 1) || Drop(arms);
    }

    private static bool Drop(List<NormArm> arms)
    {
        if (arms.Count == 0) return false;
        arms.RemoveAt(arms.Count - 1);
        return false;
    }

    /// <summary>Series inductance that puts a zero of the remainder at <paramref name="w"/>.</summary>
    private static double? Partial(double[] n, double[] d, double w)
    {
        var z = ImpedanceAt(n, d, w);
        if (z is null) return null;
        if (Math.Abs(z.Value.Real) > 1e-3 * (1.0 + z.Value.Magnitude)) return null;
        return z.Value.Imaginary / w;
    }

    private static Complex? ImpedanceAt(double[] n, double[] d, double w)
    {
        var den = MatchPoly.Eval(d, new Complex(0, w));
        if (den.Magnitude < 1e-14) return null;
        return MatchPoly.Eval(n, new Complex(0, w)) / den;
    }

    private static Complex? AdmittanceAt(double[] n, double[] d, double w)
    {
        var num = MatchPoly.Eval(n, new Complex(0, w));
        if (num.Magnitude < 1e-14) return null;
        return MatchPoly.Eval(d, new Complex(0, w)) / num;
    }

    private static void Normalise(ref double[] n, ref double[] d)
    {
        double m = 0;
        foreach (double c in n) m = Math.Max(m, Math.Abs(c));
        foreach (double c in d) m = Math.Max(m, Math.Abs(c));
        if (m == 0 || Math.Abs(m - 1.0) < 1e-15) return;
        n = Scale(n, 1.0 / m);
        d = Scale(d, 1.0 / m);
    }

    /// <summary>A parallel tank <c>z = (s/C)/(s²+ω²)</c> when <paramref name="numer"/>/<c>(s²+ω²)·quot</c>
    /// has a real positive residue. <see cref="NormArm.L"/> and <see cref="NormArm.C"/> are that tank.</summary>
    private static NormArm? Tank(double[] numer, double[] quot, double w)
    {
        var nj = MatchPoly.Eval(numer, new Complex(0, w));
        var qj = MatchPoly.Eval(quot, new Complex(0, w));
        var denom = new Complex(0, w) * qj;
        if (denom.Magnitude < 1e-14) return null;
        var k = nj / denom;
        if (Math.Abs(k.Imaginary) > 1e-4 * (1.0 + Math.Abs(k.Real))) return null;
        if (!(k.Real > 0) || !double.IsFinite(k.Real)) return null;
        double c = 1.0 / k.Real;
        double l = 1.0 / (w * w * c);
        if (!(l > 0) || !(c > 0)) return null;
        return new NormArm(NormKind.SeriesParallel, l, c);
    }

    /// <summary>
    /// How far the ladder's input impedance, terminated in 1 Ω, sits from the prototype's. The
    /// prototype impedance is <c>(E ± αF)/(E ∓ αF)</c>; both signs are tried and the closer one
    /// is the score. A wrong extraction has the zeros and still fails this.
    /// </summary>
    private static double ZinError(List<NormArm> arms, FilterPrototype proto)
    {
        double[] f = Scale(proto.F, proto.Alpha);
        double best = double.PositiveInfinity;
        foreach (double sign in (double[])[1.0, -1.0])
        {
            var num = MatchPoly.Add(proto.E, Scale(f, sign));
            var den = MatchPoly.Sub(proto.E, Scale(f, sign));
            double err = 0;
            foreach (double w in (double[])[0.2, 0.5, 0.9, 1.4, 2.5])
            {
                var s = new Complex(0, w);
                var got = Drive(arms, s);
                var want = MatchPoly.Eval(num, s) / MatchPoly.Eval(den, s);
                double scale = Math.Max(1e-6, want.Magnitude);
                err = Math.Max(err, (got - want).Magnitude / scale);
            }
            if (err < best) best = err;
        }
        return best;
    }

    /// <summary>Input impedance of a normalised ladder terminated in 1 Ω, from the source end.</summary>
    private static Complex Drive(List<NormArm> arms, Complex s)
    {
        Complex z = Complex.One;
        for (int i = arms.Count - 1; i >= 0; i--)
        {
            var a = arms[i];
            switch (a.Kind)
            {
                case NormKind.SeriesL:
                    z += s * a.L;
                    break;
                case NormKind.SeriesC:
                    z += 1.0 / (s * a.C);
                    break;
                case NormKind.ShuntC:
                    z = 1.0 / (s * a.C + 1.0 / z);
                    break;
                case NormKind.ShuntL:
                    z = 1.0 / (1.0 / (s * a.L) + 1.0 / z);
                    break;
                case NormKind.SeriesParallel:
                    z += (s * a.L) / (1.0 + s * s * a.L * a.C);
                    break;
                default:
                    var series = s * a.L + 1.0 / (s * a.C);
                    z = 1.0 / (1.0 / series + 1.0 / z);
                    break;
            }
        }
        return z;
    }

    /// <summary>Every finite zero is a tank resonance, and there is no extra tank.</summary>
    private static bool ZerosLand(List<NormArm> arms, double[] zeros)
    {
        var got = new List<double>();
        foreach (var a in arms)
        {
            if (a.Kind is not (NormKind.SeriesParallel or NormKind.ShuntSeries)) continue;
            if (!(a.L > 0) || !(a.C > 0)) return false;
            got.Add(1.0 / Math.Sqrt(a.L * a.C));
        }
        if (got.Count != zeros.Length) return false;
        got.Sort();
        for (int i = 0; i < got.Count; i++)
            if (Math.Abs(got[i] - zeros[i]) > 1e-3 * zeros[i]) return false;
        return true;
    }

    private static bool AllPositive(List<NormArm> arms) =>
        arms.All(a => (a.L == 0 || a.L > 0) && (a.C == 0 || a.C > 0) && double.IsFinite(a.L + a.C));

    private static int Degree(double[] p) => p.Length <= 1 && (p.Length == 0 || Math.Abs(p[0]) < 1e-12) ? -1 : p.Length - 1;

    private static bool Divides(double[] poly, double wz2, out double[] quot)
    {
        var (q, r) = Div(MatchPoly.Trim(poly), [1.0, 0.0, wz2]);
        quot = q;
        double scale = 0;
        foreach (double c in poly) scale = Math.Max(scale, Math.Abs(c));
        double limit = 1e-4 * Math.Max(1.0, scale);
        foreach (double c in r)
            if (Math.Abs(c) > limit)
            {
                double w = Math.Sqrt(wz2);
                var at = MatchPoly.Eval(poly, new Complex(0, w));
                int deg = Math.Max(Degree(poly), 0);
                double term = Math.Max(1.0, scale) * Math.Pow(Math.Max(w, 1.0), deg);
                return q.Length > 0 && at.Magnitude < 1e-5 * term;
            }
        return q.Length > 0;
    }

    private static (double[] Quot, double[] Rem) Div(double[] num, double[] den)
    {
        num = MatchPoly.Trim(num);
        den = MatchPoly.Trim(den);
        if (den.Length == 0 || Math.Abs(den[0]) < 1e-18) return ([0.0], num);
        if (num.Length < den.Length) return ([0.0], num);
        var r = (double[])num.Clone();
        var q = new double[num.Length - den.Length + 1];
        for (int i = 0; i < q.Length; i++)
        {
            double coef = r[i] / den[0];
            q[i] = coef;
            for (int j = 0; j < den.Length; j++) r[i + j] -= coef * den[j];
        }
        int rem = den.Length - 1;
        var rest = rem <= 0 ? new[] { 0.0 } : r[^rem..];
        return (MatchPoly.Trim(q), MatchPoly.Trim(rest));
    }

    private static double[] MulByS(double[] p) => [.. p, 0.0];

    private static string PlaceNorm(List<FilterElement> elements, NormArm arm, NetworkForm form,
                                    double rs, double wEdge, double w0, double bw, string node, ref Ids ids)
    {
        // Highpass inverts every element first (L → 1/L as a capacitor, C → 1/C as an inductor),
        // after which the lowpass denormalisation is the right one.
        if (form == NetworkForm.Highpass) arm = Invert(arm);

        switch (arm.Kind)
        {
            case NormKind.SeriesL:
                return PlaceScaled(elements, inductor: true, shunt: false, arm.L, form, rs, wEdge, w0, bw, node, ref ids);
            case NormKind.SeriesC:
                return PlaceScaled(elements, inductor: false, shunt: false, arm.C, form, rs, wEdge, w0, bw, node, ref ids);
            case NormKind.ShuntC:
                return PlaceScaled(elements, inductor: false, shunt: true, arm.C, form, rs, wEdge, w0, bw, node, ref ids);
            case NormKind.ShuntL:
                return PlaceScaled(elements, inductor: true, shunt: true, arm.L, form, rs, wEdge, w0, bw, node, ref ids);
            case NormKind.SeriesParallel:
            {
                // The two elements stay in parallel, between the same two through-path nodes. A
                // bandpass or bandstop replaces each element by its own resonator and the resonators
                // stay in parallel with each other.
                string far = Next(ref ids);
                if (form is NetworkForm.Lowpass or NetworkForm.Highpass)
                {
                    PlacePair(elements, node, far, arm.L * rs / wEdge, arm.C / (rs * wEdge), ref ids);
                    return far;
                }
                PlaceBranch(elements, inductor: true, arm.L, form, rs, w0, bw, node, far, ref ids);
                PlaceBranch(elements, inductor: false, arm.C, form, rs, w0, bw, node, far, ref ids);
                return far;
            }
            default:
            {
                // Series LC to ground. The through path does not move. Same substitution, and the
                // two resonators stay in series down the stub.
                if (form is NetworkForm.Lowpass or NetworkForm.Highpass)
                {
                    double l = arm.L * rs / wEdge;
                    double c = arm.C / (rs * wEdge);
                    string stub = Next(ref ids);
                    elements.Add(El(true, node, stub, l, true, ref ids));
                    elements.Add(El(false, stub, "0", c, true, ref ids));
                    return node;
                }
                string mid = Next(ref ids);
                PlaceBranch(elements, true, arm.L, form, rs, w0, bw, node, mid, ref ids, stub: true);
                PlaceBranch(elements, false, arm.C, form, rs, w0, bw, mid, "0", ref ids, stub: true);
                return node;
            }
        }
    }

    private static NormArm Invert(NormArm arm) => arm.Kind switch
    {
        NormKind.SeriesL      => new(NormKind.SeriesC, 0, 1.0 / arm.L),
        NormKind.SeriesC      => new(NormKind.SeriesL, 1.0 / arm.C, 0),
        NormKind.ShuntC       => new(NormKind.ShuntL, 1.0 / arm.C, 0),
        NormKind.ShuntL       => new(NormKind.ShuntC, 0, 1.0 / arm.L),
        NormKind.SeriesParallel => new(NormKind.SeriesParallel, 1.0 / arm.C, 1.0 / arm.L),
        _                     => new(NormKind.ShuntSeries, 1.0 / arm.C, 1.0 / arm.L),
    };

    /// <summary>
    /// One normalised series element, after the lowpass-to-form substitution, connected between
    /// <paramref name="a"/> and <paramref name="b"/>. A bandpass inductor is a series resonator, a
    /// bandpass capacitor a parallel resonator; bandstop exchanges those. <paramref name="stub"/>
    /// marks every element shunt so a tank to ground does not pull the load node off the through path.
    /// </summary>
    private static void PlaceBranch(List<FilterElement> elements, bool inductor, double g,
                                    NetworkForm form, double rs, double w0, double bw,
                                    string a, string b, ref Ids ids, bool stub = false)
    {
        if (form == NetworkForm.Bandpass)
        {
            if (inductor)
            {
                double l = g * rs / bw;
                double c = bw / (w0 * w0 * g * rs);
                string mid = Next(ref ids);
                elements.Add(El(true, a, mid, l, stub, ref ids));
                elements.Add(El(false, mid, b, c, stub, ref ids));
                return;
            }
            double cp = g / (rs * bw);
            double lp = rs * bw / (w0 * w0 * g);
            elements.Add(El(true, a, b, lp, stub, ref ids));
            elements.Add(El(false, a, b, cp, stub, ref ids));
            return;
        }

        if (inductor)
        {
            double c = 1.0 / (rs * g * bw);
            double l = rs * g * bw / (w0 * w0);
            elements.Add(El(true, a, b, l, stub, ref ids));
            elements.Add(El(false, a, b, c, stub, ref ids));
            return;
        }
        double ls = rs / (g * bw);
        double cs = g * bw / (rs * w0 * w0);
        string priv = Next(ref ids);
        elements.Add(El(true, a, priv, ls, stub, ref ids));
        elements.Add(El(false, priv, b, cs, stub, ref ids));
    }

    /// <summary>One normalised L or C, denormalised for the form and placed.</summary>
    private static string PlaceScaled(List<FilterElement> elements, bool inductor, bool shunt, double g,
                                      NetworkForm form, double rs, double wEdge, double w0, double bw,
                                      string node, ref Ids ids)
    {
        if (form is NetworkForm.Lowpass or NetworkForm.Highpass)
        {
            double value = inductor ? g * rs / wEdge : g / (rs * wEdge);
            return PlaceOne(elements, inductor, shunt, value, node, ref ids);
        }
        if (form == NetworkForm.Bandpass)
        {
            if (inductor)
            {
                double l = g * rs / bw;
                double c = bw / (w0 * w0 * g * rs);
                if (!shunt)
                {
                    string after = PlaceOne(elements, true, false, l, node, ref ids);
                    return PlaceOne(elements, false, false, c, after, ref ids);
                }
                PlaceOne(elements, true, true, l, node, ref ids);
                PlaceOne(elements, false, true, c, node, ref ids);
                return node;
            }
            double cs = g / (rs * bw);
            double ls = rs * bw / (w0 * w0 * g);
            if (!shunt)
            {
                // A series capacitor becomes a parallel resonator in the through path.
                string far = Next(ref ids);
                PlacePair(elements, node, far, ls, cs, ref ids);
                return far;
            }
            PlaceOne(elements, false, true, cs, node, ref ids);
            PlaceOne(elements, true, true, ls, node, ref ids);
            return node;
        }

        // Bandstop.
        if (inductor)
        {
            double c = 1.0 / (rs * g * bw);
            double l = rs * g * bw / (w0 * w0);
            if (!shunt)
            {
                string far = Next(ref ids);
                PlacePair(elements, node, far, l, c, ref ids);
                return far;
            }
            string stub = Next(ref ids);
            elements.Add(El(true, node, stub, l, true, ref ids));
            elements.Add(El(false, stub, "0", c, true, ref ids));
            return node;
        }
        double lsh = rs / (g * bw);
        double csh = g * bw / (rs * w0 * w0);
        if (!shunt)
        {
            string far = Next(ref ids);
            PlacePair(elements, node, far, lsh, csh, ref ids);
            return far;
        }
        string mid = Next(ref ids);
        elements.Add(El(true, node, mid, lsh, true, ref ids));
        elements.Add(El(false, mid, "0", csh, true, ref ids));
        return node;
    }
}
