using System.Globalization;
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
/// The g-values are the continued fraction of the prototype's own input impedance, extracted by
/// <see cref="MatchPrototypes.Extract"/> — the same Cauer step the matching ladders use, read off
/// the polynomials <see cref="FilterPrototype"/> already built. Nothing here is a transcribed table.
/// A family with transmission zeros on the axis (inverse Chebyshev, elliptic) is refused: that
/// ladder is tanks, not one L or C per arm, and a continued fraction of an all-pole impedance is
/// the wrong network.
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
        double rippleDb, double sourceOhms, FilterFirstElement first)
    {
        if (order < 1)
            return Refuse($"A filter order is at least 1; got {order}.");
        if (!(sourceOhms > 0) || !double.IsFinite(sourceOhms))
            return Refuse($"The source resistance must be a positive number of ohms; got {sourceOhms:G6}.");
        if (response is FilterResponse.InvChebyshev or FilterResponse.Elliptic)
            return Refuse(
                $"{response} has transmission zeros on the axis, so its ladder is a set of tanks " +
                "rather than one element per arm. The ideal response is available; the ladder is not " +
                "part of this synthesis.");

        FilterPrototype proto;
        try
        {
            proto = FilterPrototype.Create(response, order, rippleDb);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException)
        {
            return Refuse(e.Message);
        }

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
}
