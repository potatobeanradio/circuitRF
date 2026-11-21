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

/// <summary>The role of one arm of the normalised lowpass ladder, before the form's transformation.</summary>
public enum FilterArmKind
{
    /// <summary>A series element: an L in the lowpass prototype.</summary>
    Series,

    /// <summary>A shunt element: a C to ground in the lowpass prototype.</summary>
    Shunt,

    /// <summary>A parallel LC in the through path (a finite transmission zero).</summary>
    SeriesTank,

    /// <summary>A series LC to ground (a finite transmission zero).</summary>
    ShuntStub,
}

/// <summary>
/// One arm of a realised ladder: its prototype role and the indices of the
/// <see cref="FilterRealization.Elements"/> the form turned it into, in placement order.
/// </summary>
public sealed record FilterArm(FilterArmKind Kind, IReadOnlyList<int> Elements);

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

    /// <summary>
    /// The arms, source end first, each naming the elements it became. The drawing places by arm
    /// rather than rediscovering which elements belong together.
    /// </summary>
    public IReadOnlyList<FilterArm> Arms { get; init; } = [];

    /// <summary>Something the caller should say about this ladder, or empty.</summary>
    public string Note { get; init; } = "";

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
public static partial class FilterLadder
{
    /// <summary>
    /// The ladder for <paramref name="response"/> at <paramref name="order"/>, scaled to
    /// <paramref name="sourceOhms"/> and to the cutoff <paramref name="form"/> names.
    /// </summary>
    /// <param name="fcHz">Cutoff for lowpass and highpass.</param>
    /// <param name="f1Hz">
    /// Lower band edge for bandpass and bandstop: where the prototype's Ω = 1 lands — the edge of
    /// the passband at the ripple (3.01 dB for Butterworth), or of the stopband at Astop for
    /// inverse Chebyshev. On a bandstop it bounds the rejected band from outside.
    /// </param>
    /// <param name="f2Hz">Upper cutoff edge.</param>
    /// <param name="rippleDb">Read by Chebyshev. Ignored by the other all-pole families.</param>
    /// <param name="first">Which arm comes first. Parallel is the dual of series.</param>
    public static FilterLadderResult Synthesize(
        FilterResponse response, NetworkForm form, int order,
        double fcHz, double f1Hz, double f2Hz,
        double rippleDb, double sourceOhms, FilterFirstElement first, double astopDb = 40,
        EllipticType ellipticType = EllipticType.Auto)
    {
        if (order < 1)
            return Refuse($"A filter order is at least 1; got {order}.");
        if (!(sourceOhms > 0) || !double.IsFinite(sourceOhms))
            return Refuse($"The source resistance must be a positive number of ohms; got {sourceOhms:G6}.");

        // Butterworth / Chebyshev ladders use handbook g-values and never need the prototype
        // polynomials — Create's spectral factorisation overflows long before the sine / Chebyshev
        // recurrence does, so the closed form is the only path that stays open at high order.
        if (ClosedFormG(response, order, rippleDb) is { } closed)
            return BuildFromG(closed, response, form, order, fcHz, f1Hz, f2Hz, sourceOhms, first);

        if (SynthesizeWithZeros(response, order, rippleDb, astopDb, ellipticType,
                                form, fcHz, f1Hz, f2Hz, sourceOhms, first) is { } withZeros)
            return withZeros;

        FilterPrototype proto;
        try
        {
            proto = FilterPrototype.Create(response, order, rippleDb, astopDb);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException)
        {
            return Refuse(e.Message);
        }

        double[]? g = PrototypeG(proto);
        if (g is null)
            return Refuse(
                $"{response} order {order} did not produce a ladder with positive elements.");
        return BuildFromG(g, response, form, order, fcHz, f1Hz, f2Hz, sourceOhms, first);
    }

    /// <summary>Denormalises a g-vector into the requested form and terminations.</summary>
    private static FilterLadderResult BuildFromG(
        double[] g, FilterResponse response, NetworkForm form, int order,
        double fcHz, double f1Hz, double f2Hz, double sourceOhms, FilterFirstElement first)
    {
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
        // Chebyshev) it is the other canonical ladder, terminated in the reciprocal ratio.
        bool shuntFirst = first == FilterFirstElement.Parallel;
        // The dual exchanges impedance and admittance all the way along, the termination
        // included, so its load is the RECIPROCAL ratio. The DC mismatch 4r/(1+r)² is the same for
        // r and 1/r, which is how the wrong one went unnoticed: only the ripple edge shows it.
        if (shuntFirst) loadNorm = 1.0 / loadNorm;
        double rs = sourceOhms;
        double rl = loadNorm * sourceOhms;

        var elements = new List<FilterElement>();
        var arms = new List<FilterArm>();
        string node = "p1";
        var ids = new Ids();
        for (int k = 1; k <= order; k++)
        {
            bool shunt = shuntFirst ? k % 2 == 1 : k % 2 == 0;
            double gk = g[k];
            if (!(gk > 0) || !double.IsFinite(gk))
                return Refuse($"{response} order {order} produced a non-positive element.");
            int from = elements.Count;
            node = AddArm(elements, form, gk, shunt, rs, wEdge, w0, bw, node, ref ids);
            arms.Add(new FilterArm(shunt ? FilterArmKind.Shunt : FilterArmKind.Series,
                                   [.. Enumerable.Range(from, elements.Count - from)]));
        }

        return new FilterLadderResult
        {
            Realization = new FilterRealization
            {
                First = first,
                SourceOhms = rs,
                LoadOhms = rl,
                Elements = elements,
                Arms = arms,
                G = g,
                Cnl = ToCnl(elements, rs, rl),
            },
        };
    }

    /// <summary>
    /// Handbook g-values for families that have them. Null when the family needs the continued
    /// fraction of the prototype (Bessel, Gaussian) or has transmission zeros.
    /// </summary>
    public static double[]? ClosedFormG(FilterResponse response, int order, double rippleDb)
    {
        if (order < 1) return null;
        return response switch
        {
            FilterResponse.Butterworth => ButterworthG(order),
            FilterResponse.Chebyshev   => ChebyshevG(order, rippleDb > 0 ? rippleDb : 0.1),
            _                          => null,
        };
    }

    /// <summary><c>g_k = 2 sin((2k−1)π / 2n)</c>, unit terminations.</summary>
    public static double[] ButterworthG(int n)
    {
        var g = new double[n + 2];
        g[0] = 1.0;
        for (int k = 1; k <= n; k++)
            g[k] = 2.0 * Math.Sin((2.0 * k - 1.0) * Math.PI / (2.0 * n));
        g[n + 1] = 1.0;
        return g;
    }

    /// <summary>
    /// Equal-ripple doubly-terminated Chebyshev g-values (same recurrence as
    /// <see cref="MatchSynthesis.RippleG"/>, without that method's match-order ceiling).
    /// </summary>
    public static double[] ChebyshevG(int n, double rippleDb)
    {
        double lar = rippleDb > 0 ? rippleDb : 0.1;
        double bb = Math.Log(1.0 / Math.Tanh(lar / 17.37));
        double gamma = Math.Sinh(bb / (2 * n));
        double th = Math.PI / (2 * n);

        var a = new double[n + 1];
        var b = new double[n + 1];
        for (int k = 1; k <= n; k++)
        {
            a[k] = Math.Sin((2 * k - 1) * th);
            b[k] = gamma * gamma + Math.Sin(k * Math.PI / n) * Math.Sin(k * Math.PI / n);
        }

        var g = new double[n + 2];
        g[0] = 1.0;
        g[1] = 2.0 * a[1] / gamma;
        for (int k = 2; k <= n; k++) g[k] = 4.0 * a[k - 1] * a[k] / (b[k - 1] * g[k - 1]);
        double coth = 1.0 / Math.Tanh(bb / 4.0);
        g[n + 1] = n % 2 == 0 ? coth * coth : 1.0;
        return g;
    }

    /// <summary>
    /// The g-vector <c>[g0=1, g1..gn, gLoad]</c> of an all-pole prototype, or null when the
    /// continued fraction has no positive-element realisation.
    /// </summary>
    public static double[]? PrototypeG(FilterPrototype proto)
    {
        ArgumentNullException.ThrowIfNull(proto);
        if (proto.P.Length != 1) return null;

        // Prefer the closed form when the family has one — same numbers the design doc checks
        // Butterworth against, and stable past the double-precision Cauer wall.
        if (ClosedFormG(proto.Response, proto.Order, RippleDbOf(proto)) is { } closed)
            return closed;

        double[] f = Scale(proto.F, proto.Alpha);
        foreach (double sign in (double[])[1.0, -1.0])
        {
            double[] a = MatchPoly.Trim(MatchPoly.Sub(proto.E, Scale(f, sign)));
            double[] b = MatchPoly.Trim(MatchPoly.Add(proto.E, Scale(f, sign)));
            double[] top, bot;
            if (a.Length == b.Length + 1) { top = a; bot = b; }
            else if (b.Length == a.Length + 1) { top = b; bot = a; }
            else continue;

            // Renormalise before each removal so Bessel / Gaussian coefficient sprawl does not
            // flip a leading-ratio sign mid-ladder. Double-double is the next fallback when even
            // that loses the leading ratio around order 8+.
            double[]? g = MatchPrototypes.ExtractScaled(top, bot, proto.Order)
                       ?? FilterLadderHighPrecision.Extract(top, bot, proto.Order);
            if (g is not null) return g;
        }
        return null;
    }

    /// <summary>
    /// Passband ripple in dB recovered from the prototype's α/β for Chebyshev; unused otherwise.
    /// </summary>
    private static double RippleDbOf(FilterPrototype proto)
    {
        if (proto.Response is not FilterResponse.Chebyshev || !(proto.Beta > 0)) return 0.1;
        double eps = Math.Abs(proto.Alpha / proto.Beta);
        if (!(eps > 0) || !double.IsFinite(eps)) return 0.1;
        return 10.0 * Math.Log10(1.0 + eps * eps);
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
