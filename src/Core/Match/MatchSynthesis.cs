using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CircuitRF.Core.Matching;

/// <summary>What one synthesis pass produced, or why it could not.</summary>
public sealed class MatchSynthesisResult
{
    /// <summary>Non-null when the design cannot be synthesised. Everything else is then unset.</summary>
    public MatchRefusal? Refusal { get; init; }

    /// <summary>True when <see cref="Network"/> is usable.</summary>
    public bool Ok => Refusal is null && Network is not null;

    /// <summary>g[0] .. g[n+1] of the lowpass prototype.</summary>
    public double[] G { get; init; } = [];

    /// <summary>Band centre, rad/s.</summary>
    public double Omega0 { get; init; }

    /// <summary>Fractional bandwidth.</summary>
    public double W { get; init; }

    /// <summary>True when termination 1 pinned g1.</summary>
    public bool AnalysisIsTerm1 { get; init; }

    /// <summary>The Q the prototype was built to — <see cref="MatchDesign.QAdjust"/> when set.</summary>
    public double QAnalysis { get; init; }

    /// <summary>The analysis end's real Q, before any Q-adjust.</summary>
    public double QAnalysisActual { get; init; }

    /// <summary>The far-end Q the synthesis produced.</summary>
    public double QFarSynthesised { get; init; }

    /// <summary>The far termination's real Q.</summary>
    public double QFarActual { get; init; }

    /// <summary>The analysis end's port resistance — never rescaled by a transform.</summary>
    public double RAnalysis { get; init; }

    /// <summary>The far port resistance the synthesis produced. Not the requested one.</summary>
    public double RFarSynthesised { get; init; }

    /// <summary>The far termination's requested resistance.</summary>
    public double RFarTarget { get; init; }

    /// <summary>The product of N^2 the Norton transforms must reach (match.md §4.8).</summary>
    public double RequiredTransformRatio =>
        RFarSynthesised > 0 ? RFarTarget / RFarSynthesised : double.NaN;

    /// <summary>The basis ladder, Term1-first. <b>Without</b> the §4.5/§4.6 split elements.</summary>
    public MatchNetwork? Network { get; init; }

    /// <summary>True when neither end had a reactance and the equal-ripple prototype ran instead.</summary>
    public bool UsedRipplePrototype { get; init; }

    /// <summary>True when Q_far exceeds Q_actual by more than 2 % and §4.5's excess element is wanted.</summary>
    public bool NeedsExcessElement { get; init; }

    /// <summary>Structure hash of the basis ladder (match.md §7.3).</summary>
    public string BasisFingerprint { get; init; } = string.Empty;

    /// <summary>Anything the caller should be told but that is not a refusal.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Shorthand for a refusal-only result.</summary>
    public static MatchSynthesisResult Refuse(MatchRefusal refusal) => new() { Refusal = refusal };
}

/// <summary>
/// match.md §4: the lowpass prototypes, the bandpass transformation, absorption, and §4.5/§4.6's
/// end splits.
/// </summary>
public static class MatchSynthesis
{
    /// <summary>Below this ratio of Q_far to Q_actual there is nothing to add at the far end.</summary>
    public const double ExcessRatioThreshold = 1.02;

    /// <summary>Synthesises the basis ladder for a design.</summary>
    public static MatchSynthesisResult Synthesize(MatchDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);

        if (!(design.F1 > 0) || !(design.F2 > design.F1))
            return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                MatchRefusalKind.InvalidTermination,
                "The band must satisfy 0 < F1 < F2.",
                null, ("f1", design.F1), ("f2", design.F2)));

        foreach (var (t, which) in (ReadOnlySpan<(Termination, int)>)[(design.Term1, 1), (design.Term2, 2)])
        {
            if (!(t.R > 0))
                return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                    MatchRefusalKind.InvalidTermination,
                    $"Termination {which} has a non-positive resistance.", which, ("r", t.R)));
            if (t.Kind != ReactanceKind.None && !(t.Value > 0))
                return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                    MatchRefusalKind.InvalidTermination,
                    $"Termination {which}'s {t.Kind} must be positive; " +
                    "a short or an open is expressed as Kind = None, not as a magic value.",
                    which, ("value", t.Value)));
        }

        var valid = MatchOrders.ValidOrders(design.Term1, design.Term2);
        if (!valid.Contains(design.Order))
            return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                MatchRefusalKind.InvalidOrder,
                $"Order {design.Order} cannot absorb both ends: with a " +
                (design.Term1.Topology == design.Term2.Topology ? "like" : "mixed") +
                $" termination pair the ladder's arms alternate, so only {string.Join(", ", valid)} fit.",
                null, ("order", design.Order), ("minValid", valid[0]), ("maxValid", valid[^1])));

        double om0 = design.Omega0, w = design.W;
        int n = design.Order;

        double q1 = design.Term1.QAt(om0), q2 = design.Term2.QAt(om0);
        bool anaIsTerm1 = design.AnalysisEnd switch
        {
            AnalysisEndChoice.Term1 => true,
            AnalysisEndChoice.Term2 => false,
            _ => q1 >= q2,
        };
        Termination ana = anaIsTerm1 ? design.Term1 : design.Term2;
        Termination far = anaIsTerm1 ? design.Term2 : design.Term1;
        double qAnaActual = anaIsTerm1 ? q1 : q2;
        double qFarActual = anaIsTerm1 ? q2 : q1;
        double qAna = design.QAdjust > 0 ? design.QAdjust : qAnaActual;

        var notes = new List<string>();
        bool ripple = qAna <= 0.0
            || (design.Response == ResponseShape.ChebyshevTwoEnded && qFarActual <= 0.0);
        if (ripple && design.Response != ResponseShape.ChebyshevFano)
            notes.Add(
                $"{design.Response} needs a reactance to prescribe; with a purely resistive end the " +
                $"equal-ripple prototype at {design.RippleDb:0.###} dB ran instead.");

        double[]? g;
        double familyMaxQFar = 0.0;
        if (ripple)
        {
            g = RippleG(n, design.RippleDb);
        }
        else if (design.Response == ResponseShape.ChebyshevFano)
        {
            g = FanoG(n, qAna, w);
            if (g is null)
                return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                    MatchRefusalKind.NoRealRoot,
                    $"No real Fano root at order {n} for an analysis-end Q of {qAna:0.####}: " +
                    "there is no Chebyshev/Fano solution at this order for this termination.",
                    anaIsTerm1 ? 1 : 2, ("order", n), ("q", qAna), ("w", w)));
        }
        else if (design.Response == ResponseShape.ChebyshevTwoEnded)
        {
            g = TwoEndedG(n, qAna, qFarActual, w);
        }
        else
        {
            var search = MatchPrototypes.Search(
                design.Response, n, qAna * w,
                candidate =>
                {
                    var built = Build(candidate, n, ana, far, design, anaIsTerm1);
                    bool feasible = qFarActual <= 0.0 || built.QFar >= qFarActual;
                    double score = MatchResponse.WorstReturnLossDb(built.Network, design.F1, design.F2);
                    return new PrototypeEvaluation(feasible, built.QFar, score);
                });
            familyMaxQFar = search.MaxQFar;
            g = search.G;
            if (g is null)
                return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                    MatchRefusalKind.ResponseInfeasible,
                    $"{design.Response} cannot absorb termination {(anaIsTerm1 ? 2 : 1)} at order {n} — " +
                    $"its far-end Q reaches only {familyMaxQFar:0.###} against the " +
                    $"{qFarActual:0.###} needed.",
                    anaIsTerm1 ? 2 : 1,
                    ("order", n), ("maxQFar", familyMaxQFar), ("qRequired", qFarActual)));
        }

        if (g is null || g.Any(v => !double.IsFinite(v) || v <= 0.0))
            return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                MatchRefusalKind.NoRealRoot,
                $"The {design.Response} prototype at order {n} produced no positive g-vector for an " +
                $"analysis-end Q of {qAna:0.####}.",
                anaIsTerm1 ? 1 : 2, ("order", n), ("q", qAna)));

        var b = Build(g, n, ana, far, design, anaIsTerm1);

        if (qFarActual > 0.0 && b.QFar < qFarActual * (1.0 - 1e-9))
            return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                MatchRefusalKind.FarEndNotAbsorbable,
                $"Termination {(anaIsTerm1 ? 2 : 1)} is not absorbable at order {n}: the synthesis " +
                $"reaches Q_far = {b.QFar:0.####} against the termination's own {qFarActual:0.####} " +
                $"(ratio {b.QFar / qFarActual:0.###}). Try another order, another response, the other " +
                "analysis end, or a Q-adjusted solution.",
                anaIsTerm1 ? 2 : 1,
                ("qFar", b.QFar), ("qActual", qFarActual), ("ratio", b.QFar / qFarActual)));

        bool needsExcess = qFarActual > 0.0 && b.QFar / qFarActual > ExcessRatioThreshold;

        return new MatchSynthesisResult
        {
            G = g,
            Omega0 = om0,
            W = w,
            AnalysisIsTerm1 = anaIsTerm1,
            QAnalysis = qAna,
            QAnalysisActual = qAnaActual,
            QFarSynthesised = b.QFar,
            QFarActual = qFarActual,
            RAnalysis = ana.R,
            RFarSynthesised = b.RFar,
            RFarTarget = far.R,
            Network = b.Network,
            UsedRipplePrototype = ripple,
            NeedsExcessElement = needsExcess,
            BasisFingerprint = Fingerprint(g, b.Network),
            Notes = notes,
        };
    }

    /// <summary>
    /// match.md §4.3's singly-prescribed Chebyshev closed form, with the Fano root. Returns null when
    /// P_n(c) has no real root — <b>a value, not an exception</b>.
    /// </summary>
    public static double[]? FanoG(int n, double q, double w)
    {
        if (n < MatchOrders.MinOrder || n > MatchOrders.MaxOrder) return null;
        if (!(q > 0) || !(w > 0)) return null;

        double th = Math.PI / (2 * n);
        double qw = q * w;
        double c = 2.0 / qw * Math.Sin(th);
        double c2 = c * c, c4 = c2 * c2;

        double r;
        if (n == 2)
        {
            r = 0.5;
        }
        else
        {
            double[] p = n switch
            {
                3 => [16, 16, 3 + 12 * c2, -3 - 4 * c2],
                4 => [16, 8, 2 + 8 * c2, -1 - 2 * c2],
                5 => [256, 512, 16 * (21 + 20 * c2), 16 * (5 + 12 * c2),
                      5 * (1 + 12 * c2 + 16 * c4), -(5 + 20 * c2 + 16 * c4)],
                _ => [1024, 1536, 256 * (3 + 4 * c2), 128 * (1 + 3 * c2),
                      4 * (3 + 32 * c2 + 48 * c4), -2 * (3 + 16 * c2 + 16 * c4)],
            };
            // "The first real root", made reproducible: sorted ASCENDING and taking the first that
            // yields a real d = sqrt(c^2/4 + r). Ordering has to be pinned to something, because a
            // root finder's own order is an implementation detail and a different member of the
            // family is a different (and generally worse) design, not an error anyone would see.
            var real = MatchPoly.Roots(p)
                .Where(z => Math.Abs(z.Imaginary) < 1e-12 * Math.Max(1.0, z.Magnitude))
                .Select(z => z.Real)
                .Where(x => c2 / 4 + x >= 0)
                .OrderBy(x => x)
                .ToList();
            if (real.Count == 0) return null;
            r = real[0];
        }

        double d = Math.Sqrt(c2 / 4 + r);
        double sinhA = d + c / 2;
        double bigD = sinhA / (Math.Sin(th) / qw) - 1.0;
        if (!(bigD > 0)) return null;

        var g = new double[n + 2];
        g[0] = 1.0;
        g[1] = qw;
        for (int j = 2; j <= n; j++)
        {
            int z = j - 1;
            double cz2 = Math.Cos(z * th) * Math.Cos(z * th);
            double sz2 = Math.Sin(z * th) * Math.Sin(z * th);
            double k2 = (sz2 * cz2 + (cz2 + bigD * bigD * sz2) * Math.Sin(th) * Math.Sin(th) / (qw * qw))
                        / (Math.Sin((2 * z - 1) * th) * Math.Sin((2 * z + 1) * th));
            g[j] = 1.0 / (g[j - 1] * k2);
        }
        g[n + 1] = qw / (bigD * g[n]);
        return g;
    }

    /// <summary>
    /// match.md §4.3's doubly-prescribed Chebyshev form. <b>Not a ripple setting and not a lesser
    /// prototype</b>: both end Q's are inputs, so the far end absorbs exactly and §4.5's excess
    /// element is never needed. What it gives up is Fano optimality.
    /// </summary>
    public static double[]? TwoEndedG(int n, double qAna, double qFar, double w)
    {
        if (n < MatchOrders.MinOrder || n > MatchOrders.MaxOrder) return null;
        if (!(qAna > 0) || !(qFar > 0) || !(w > 0)) return null;

        double th = Math.PI / (2 * n);
        double x = (1.0 / (qFar * w) + 1.0 / (qAna * w)) * Math.Sin(th);
        double y = (1.0 / (qFar * w) - 1.0 / (qAna * w)) * Math.Sin(th);

        var g = new double[n + 2];
        g[0] = 1.0;
        g[1] = 2.0 * Math.Sin(th) / (x - y);
        for (int r = 1; r <= n - 1; r++)
        {
            double denom = g[r] * (x * x + y * y + Math.Sin(2 * r * th) * Math.Sin(2 * r * th)
                                   - 2.0 * x * y * Math.Cos(2 * r * th));
            g[r + 1] = 4.0 * Math.Sin((2 * r - 1) * th) * Math.Sin((2 * r + 1) * th) / denom;
        }
        g[n + 1] = 2.0 * Math.Sin(th) / ((x + y) * g[n]);
        return g;
    }

    /// <summary>
    /// match.md §4.3's real-to-real prototype: the standard equal-ripple table, used when neither end
    /// has a reactance to prescribe and the component is a plain bandpass transformer.
    /// </summary>
    public static double[] RippleG(int n, double rippleDb)
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
    /// Decomposes the end arms' absorbed elements into the terminations' own values plus the real
    /// added elements of match.md §4.5 (CFano/LFano, far end) and §4.6 (CDetune/LDetune, analysis end).
    /// </summary>
    /// <remarks>
    /// <b>This runs LAST, after every Norton transform, and that is a deliberate departure from the
    /// brief's ordering.</b> Two reasons, both measured:
    /// <list type="number">
    /// <item>An extra element inside the basis list breaks the strict (L,C) x (shunt,series)
    /// alternation that match.md §4.7's pair-discovery offsets assume — the gap-3 move would then
    /// swap a shunt element past a series one, which is a different circuit, not a re-ordering.</item>
    /// <item>Applied at the end, the split is exact by construction: the far port has reached its
    /// target resistance, so the kept value comes out equal to the termination's own to machine
    /// precision. Applied to the basis it is only equal after the transforms have scaled it.</item>
    /// </list>
    /// The arm total is unchanged either way, so the response is unchanged — which is asserted.
    /// </remarks>
    public static MatchNetwork WithEndSplits(MatchNetwork network, MatchSynthesisResult result, MatchDesign design)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(design);

        var net = network.Clone();
        double om0 = result.Omega0;

        // Far end first: inserting at the analysis end shifts no far-end index, but not vice versa.
        foreach (int end in (ReadOnlySpan<int>)[result.AnalysisIsTerm1 ? 2 : 1, result.AnalysisIsTerm1 ? 1 : 2])
        {
            bool isAnalysis = end == (result.AnalysisIsTerm1 ? 1 : 2);
            Termination term = end == 1 ? design.Term1 : design.Term2;
            if (!term.HasReactance) continue;

            int idx = net.Elements.FindIndex(e => e.AbsorbedEnd == end);
            if (idx < 0) continue;

            var absorbed = net.Elements[idx];
            double rEnd = end == 1 ? net.R1 : net.R2;
            double ceqTotal = absorbed.Type == ElementType.C
                ? absorbed.Value
                : 1.0 / (om0 * om0 * absorbed.Value);
            double qSynth = MatchQ.Q(term.Topology, rEnd, ceqTotal, om0);
            double qActual = term.QAt(om0);
            if (qActual <= 0 || qSynth / qActual <= ExcessRatioThreshold) continue;

            var (ceqKept, ceqAdded) = MatchQ.SplitExcess(
                term.Topology, ceqTotal, qSynth, qActual, rEnd, om0);
            if (!(ceqKept > 0) || !(ceqAdded > 0)) continue;

            absorbed.Value = MatchQ.FromCeq(term.Kind, ceqKept, om0);
            var added = new MatchElement
            {
                Name = (term.Kind == ReactanceKind.C ? "C" : "L") + (isAnalysis ? "Detune" : "Fano"),
                Type = absorbed.Type,
                IsShunt = absorbed.IsShunt,
                Value = MatchQ.FromCeq(term.Kind, ceqAdded, om0),
                IsExcess = !isAnalysis,
                IsDetune = isAnalysis,
            };
            // Outward of the absorbed element, so it reads as "ours, next to theirs". Same
            // orientation, so it lands on the same nets (shunt) or in the same chain (series).
            net.Elements.Insert(end == 1 ? idx : idx + 1, added);
        }

        return net;
    }

    /// <summary>
    /// A short stable hash of the basis ladder's STRUCTURE — element count, the per-element
    /// type/orientation sequence, and the g-values to 6 significant figures (match.md §7.3).
    /// </summary>
    public static string Fingerprint(double[] g, MatchNetwork network)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(network);

        var sb = new StringBuilder();
        sb.Append(network.Elements.Count).Append('|');
        foreach (var e in network.Elements)
            sb.Append(e.Type == ElementType.L ? 'L' : 'C').Append(e.IsShunt ? 'p' : 's');
        sb.Append('|');
        foreach (double v in g)
            sb.Append(v.ToString("G6", CultureInfo.InvariantCulture)).Append(',');

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>
    /// match.md §4.4: frequency-scale, impedance-scale, resonate — and mark what the terminations
    /// supply. Built from the analysis end and reversed so index 0 is always the Term1 side.
    /// </summary>
    private static (MatchNetwork Network, double RFar, double QFar) Build(
        double[] g, int n, Termination ana, Termination far, MatchDesign design, bool anaIsTerm1)
    {
        double om0 = design.Omega0, w = design.W, rAna = ana.R;

        var s = new bool[n + 2];
        s[0] = s[1] = ana.Topology == TerminationTopology.Series;
        for (int j = 2; j <= n; j++) s[j] = !s[j - 1];
        s[n + 1] = s[n];

        // Arms in ANALYSIS-END order; arm 0 is the analysis end, arm n-1 the far end.
        var arms = new List<(bool IsSeries, double L, double C)>(n);
        for (int j = 1; j <= n; j++)
        {
            double gFreq = g[j] / (w * om0);
            double gImp = s[j] ? gFreq * rAna : gFreq / rAna;
            double l, c;
            if (s[j]) { l = gImp; c = 1.0 / (om0 * om0 * l); }
            else { c = gImp; l = 1.0 / (om0 * om0 * c); }
            arms.Add((s[j], l, c));
        }

        double rFar = s[n + 1] ? rAna / g[n + 1] : g[n + 1] * rAna;
        double qFar = g[n] * g[n + 1] / w;
        if (s[n + 1]) qFar = 1.0 / qFar;

        // Which element each end supplies. Marked in ANALYSIS order, before the reversal.
        int anaEnd = anaIsTerm1 ? 1 : 2, farEnd = anaIsTerm1 ? 2 : 1;
        var absorbedByArm = new (int End, ElementType Type)?[n];
        if (ana.AbsorbedType is { } at) absorbedByArm[0] = (anaEnd, at);
        if (far.AbsorbedType is { } ft) absorbedByArm[n - 1] = (farEnd, ft);

        // Reverse by ARM, not by element: the (L, C) order inside an arm is what keeps the finished
        // list strictly alternating in type, which match.md §4.7's move offsets depend on.
        var order = Enumerable.Range(0, n).ToList();
        if (!anaIsTerm1) order.Reverse();

        var net = new MatchNetwork
        {
            R1 = anaIsTerm1 ? rAna : rFar,
            R2 = anaIsTerm1 ? rFar : rAna,
        };
        int nl = 0, nc = 0;
        foreach (int armIndex in order)
        {
            var arm = arms[armIndex];
            var absorbed = absorbedByArm[armIndex];
            net.Elements.Add(new MatchElement
            {
                Name = $"L{++nl}",
                Type = ElementType.L,
                IsShunt = !arm.IsSeries,
                Value = arm.L,
                ArmIndex = armIndex,
                AbsorbedEnd = absorbed is { Type: ElementType.L } a1 ? a1.End : 0,
            });
            net.Elements.Add(new MatchElement
            {
                Name = $"C{++nc}",
                Type = ElementType.C,
                IsShunt = !arm.IsSeries,
                Value = arm.C,
                ArmIndex = armIndex,
                AbsorbedEnd = absorbed is { Type: ElementType.C } a2 ? a2.End : 0,
            });
        }

        return (net, rFar, qFar);
    }
}
