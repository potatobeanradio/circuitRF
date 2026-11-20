using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Synthesis.Filters;

/// <summary>
/// The designs MCRC's realisers produce (docs/design/filter-synthesis.md §4), each as the part's stored values (SI
/// lengths as doubles, names as strings, keyed as the part names them). Two routes:
/// <list type="bullet">
/// <item>Cohn's parallel-coupled filter (<see cref="Cohn"/>): J-inverters in closed form, each coupled section's gap from
/// MCLIN's own modes, for the edge-coupled and zig-zag chains.</item>
/// <item>Coupling coefficients and external Q (<see cref="FromCouplings"/>): each gap and the feed found by solving the
/// part's own circuit model, a pair's split and one resonator's loaded band, for hairpins and open loops. It takes the
/// coupling matrix as given, so a cross-coupled design hands it the k_ij its own synthesis finds.</item>
/// </list>
/// Every measurement is on the lossless twin of the substrate, as the textbook designs are: loss lowers the peaks but
/// barely moves them.
/// </summary>
public static class CoupledResonatorDesign
{
    private const double C0 = 299_792_458.0;

    // ══ Lines and their ends ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The width of a <paramref name="z0"/> line at <paramref name="fHz"/>, on the plan grid.</summary>
    public static double Width(double z0, double fHz, MicrostripSubstrate s)
        => PassivePlan.OnGrid(BranchLinePlan.Width(z0, fHz, s, "Coupled-resonator synthesis", "synthesis.mcrc.width-unreachable"));

    /// <summary>The unloaded Q of a resonator of a <paramref name="z0"/> line at <paramref name="fHz"/>: β/(2α), with α
    /// MLIN's own conductor and dielectric loss. Radiation and the open ends are left out, so a real resonator's Q is
    /// somewhat lower.</summary>
    public static double UnloadedQ(double z0, double fHz, MicrostripSubstrate s)
    {
        var p = BranchLinePlan.Line(Width(z0, fHz, s), s).LineParameters(fHz);
        return p.BetaRadPerM / (2.0 * p.AlphaNpPerM);
    }

    /// <summary>A line's εeff at <paramref name="fHz"/>.</summary>
    public static double LineEeff(double w, double fHz, MicrostripSubstrate s) => BranchLinePlan.Line(w, s).LineParameters(fHz).Eeff;

    /// <summary>One strip's open-end extension, as MCRC's free ends are modelled.</summary>
    public static double OpenEnd(double w, MicrostripSubstrate s)
        => MicrostripOpenEnd.Extension(w, s.HMeters, s.EpsR, BranchLinePlan.Line(w, s).LineParameters(0.0).EeffStatic);

    /// <summary>What one mitred corner of width <paramref name="w"/> adds to a resonator's centre-line length beyond its
    /// electrical length: its two arms, less its own phase as a length of the line (<see cref="PassivePlan"/>'s
    /// <c>LineLength</c>).</summary>
    public static double CornerExcess(double w, double fHz, MicrostripSubstrate s)
    {
        var bend = new PlanBend([1, 2], 0, 0, 0, w, 90.0, PlanBend.MiterFor(PlanCorner.Miter, w, s));
        var line = BranchLinePlan.Line(w, s).LineParameters(fHz);
        return 2.0 * bend.Arm - bend.LineLength(s, fHz, line.Z0, C0 / (4.0 * fHz * Math.Sqrt(line.Eeff)));
    }

    private static KirschningJansenCoupled.Modes Modes(double fHz, double w, double gap, MicrostripSubstrate s)
        => KirschningJansenCoupled.Compute(fHz, w, gap, s.HMeters, s.TMeters, s.EpsR,
            new MicrostripValidityReporter("(coupled-resonator synthesis, not reported)"));   // one each: realisers run side by side

    // ══ Cohn's parallel-coupled filter ════════════════════════════════════════════════════════════════════════════

    /// <summary>The gap at which a pair of width <paramref name="w"/> has voltage coupling <paramref name="c"/> at
    /// <paramref name="fHz"/>, (Ze − Zo)/(Ze + Zo), by bisection over MCLIN's own modes.</summary>
    public static double GapFor(double w, double c, double fHz, MicrostripSubstrate s)
        => Solve(gap => { var m = Modes(fHz, w, gap, s); return (m.Ze - m.Zo) / (m.Ze + m.Zo); }, c,
                 0.1 * s.HMeters, 10.0 * s.HMeters, log: true, $"a voltage coupling of {c:G3}");

    /// <summary>
    /// Cohn's parallel-coupled half-wave filter (S. B. Cohn, IRE Trans. MTT 6(2), 1958,
    /// <see href="https://doi.org/10.1109/TMTT.1958.1124542">10.1109/TMTT.1958.1124542</see>): each coupled section an
    /// admittance inverter, J01·Z0 = √(π·FBW/(2·g0·g1)), J(k,k+1)·Z0 = π·FBW/(2√(g_k·g_(k+1))), its modes
    /// Ze, Zo = Z0(1 ± JZ0 + (JZ0)²). On one strip width, the Z0 line's, each section's gap is the one whose
    /// (Ze − Zo)/(Ze + Zo) is Cohn's, JZ0/(1 + (JZ0)²). Each section is λg/4 in its modes' mean εeff; each resonator is
    /// its two sections less the coupled open end at each of its ends, the mean of the even- and odd-mode extensions in
    /// that section's own modes; the resonators take their mean length. <paramref name="g"/> is g0 … g(N+1).
    /// </summary>
    public static (double W, double L, double[] S) Cohn(double[] g, double fbw, double fHz, double z0, MicrostripSubstrate s)
    {
        int n = g.Length - 2;
        double w = Width(z0, fHz, s);
        var gaps = new double[n + 2];
        var quarter = new double[n + 2];
        var end = new double[n + 2];
        for (int j = 1; j <= n + 1; j++)
        {
            double x = j == 1 ? Math.Sqrt(Math.PI * fbw / (2.0 * g[0] * g[1]))
                     : j == n + 1 ? Math.Sqrt(Math.PI * fbw / (2.0 * g[n] * g[n + 1]))
                     : Math.PI * fbw / (2.0 * Math.Sqrt(g[j - 1] * g[j]));
            gaps[j] = GapFor(w, x / (1.0 + x * x), fHz, s);
            var m = Modes(fHz, w, gaps[j], s);
            quarter[j] = C0 / (4.0 * fHz * Math.Sqrt((m.EeffE + m.EeffO) / 2.0));
            var m0 = Modes(0.0, w, gaps[j], s);
            end[j] = (MicrostripOpenEnd.Extension(w, s.HMeters, s.EpsR, m0.EeffE)
                    + MicrostripOpenEnd.Extension(w, s.HMeters, s.EpsR, m0.EeffO)) / 2.0;
        }
        double l = Enumerable.Range(1, n).Average(k => quarter[k] + quarter[k + 1] - end[k] - end[k + 1]);
        return (w, PassivePlan.OnGrid(l), gaps);
    }

    // ══ Coupling coefficients and external Q ══════════════════════════════════════════════════════════════════════

    /// <summary>The all-pole prototype's coupling matrix: k(i,i+1) = FBW/√(g_i·g_(i+1)), nothing else (J.-S. Hong and
    /// M. J. Lancaster, <i>Microstrip Filters for RF/Microwave Applications</i>, Wiley, 2001,
    /// <see href="https://doi.org/10.1002/0471221619">10.1002/0471221619</see>, §8.1).</summary>
    public static double[,] Couplings(double[] g, double fbw)
    {
        int n = g.Length - 2;
        var k = new double[n, n];
        for (int i = 1; i < n; i++) k[i - 1, i] = k[i, i - 1] = fbw / Math.Sqrt(g[i] * g[i + 1]);
        return k;
    }

    /// <summary>The external Q at either end, Qe = g0·g1/FBW (equal to g_N·g_(N+1)/FBW for these prototypes).</summary>
    public static double ExternalQ(double[] g, double fbw) => g[0] * g[1] / fbw;

    /// <summary>
    /// Hong and Lancaster's method (IEEE Trans. MTT 44(11), 1996,
    /// <see href="https://doi.org/10.1109/22.543968">10.1109/22.543968</see>, and the book), on the part's own circuit
    /// model with no EM: <paramref name="form"/> holds the shape's stored values (W, Shape, Profile, Feed and the shape's
    /// own Sa, Gap, Wc, Uc), <paramref name="k"/> the coupling matrix, <paramref name="qe"/> the external Q at each end.
    /// <list type="number">
    /// <item>One resonator, lightly fed by coupled strips 3·h off, is sized to resonate at <paramref name="fHz"/>.</item>
    /// <item>Each gap S(i+1) is the one at which two such resonators, lightly fed, split into f1 and f2 with
    /// (f2² − f1²)/(f2² + f1²) = k(i,i+1), by bisection on the gap.</item>
    /// <item>The feed is the one at which one resonator fed alike at both ends has Qe at each
    /// (<see cref="ExternalQOf"/>): the tap (Tapped) or the feed strips' gap S1 = S(N+1) (Coupled), by bisection.</item>
    /// <item>Each resonator is then sized for f0 where it sits: a coupled side moves a resonator's own frequency by the
    /// shift of the pair's mean, √(f1·f2), from the lone resonator's, and a feed by half the shift of a resonator fed at
    /// both ends, so the middle resonators of loops come out smaller than the end ones.</item>
    /// </list>
    /// An inline row realises adjacent couplings only. Open loops with <c>Arrangement</c> = <c>Cross</c> (N = 4) add
    /// k(1,4) across <c>Sx</c>, and measure the square's own pairs, 2-3 back to back (S3) and 1-4 across their openings
    /// (Sx), in the square itself, its other two loops shortened to 0.6·l (near 1.7·f0, out of the window) and its other
    /// gaps 5·h, so the split and the mean are the pair's own. Only |k| is realised: the square's geometry sets each
    /// sign. Any other non-zero k_ij further apart is refused. Returns the stored values: <paramref name="form"/>'s, plus
    /// N, L_k, the gaps (and Sx) and Tap. With <paramref name="em"/>, each gap and a tap are then read off EM curves
    /// measured around the circuit model's answer (§6b, <see cref="CouplingCurves"/>). <paramref name="cancel"/> stops it
    /// between circuit solves.
    /// </summary>
    public static Dictionary<string, object> FromCouplings(IReadOnlyDictionary<string, object> form, double[,] k, double qe,
        double fHz, MicrostripSubstrate s, ICouplingCurveSolver? em = null, CancellationToken cancel = default)
    {
        int n = k.GetLength(0);
        bool cross = form.GetValueOrDefault("Arrangement") is "Cross";
        for (int i = 0; i < n; i++)
            for (int j = i + 2; j < n; j++)
                if ((k[i, j] != 0.0 || k[j, i] != 0.0) && !(cross && i == 0 && j == n - 1))
                    throw Refusal("cross-coupling", "k({i},{j}) = {k} couples resonators that do not sit side by side; an "
                      + "inline row realises adjacent couplings only, and the cross-coupled square adds k(1,4) alone.",
                        ("i", i + 1), ("j", j + 1), ("k", (k[i, j] != 0.0 ? k[i, j] : k[j, i]).ToString("G3", System.Globalization.CultureInfo.InvariantCulture)));
        s = Lossless(s);
        double w = (double)form["W"], h = s.HMeters, light = 3.0 * h;
        bool tapped = form.TryGetValue("Feed", out var feed) && feed is "Tapped";
        PassivePlan Build(int count, double l, params (string Key, object Value)[] more) => Bench(form, s, count, l, more);
        (string, object)[] Light(int count) => [("Feed", "Coupled"), ("S1", light), ($"S{count + 1}", light)];

        // 1. One resonator at f0.
        double l = C0 / (2.0 * fHz * Math.Sqrt(LineEeff(w, fHz, s))), lone = 0.0;
        for (int i = 0; i < 8 && Math.Abs(lone / fHz - 1.0) > 1e-5; i++)
        {
            cancel.ThrowIfCancellationRequested();
            lone = Peaks(Build(1, l, Light(1)), s, 1, 0.5 * fHz, 1.5 * fHz)[0];
            l = PassivePlan.OnGrid(l * lone / fHz);
        }
        lone = Peaks(Build(1, l, Light(1)), s, 1, 0.5 * fHz, 1.5 * fHz)[0];

        // 2. The gaps from the pair's split: two in a row, or one of the cross square's own pairs in the square.
        string Bed(string key) => cross && key is "S3" or "Sx" ? key : "";
        PassivePlan PairPlan(string key, double gap) => Bed(key) == "" ? LightPair(form, l, gap, s)
            : Build(4, l, [.. Light(4), ("Arrangement", "Cross"), ("S2", 5.0 * h), ("S3", 5.0 * h), ("S4", 5.0 * h),
                ("Sx", 5.0 * h), (key, gap), .. Enumerable.Range(1, 4)
                    .Select(i => ($"L{i}", (object)((i is 1 or 4) == (key == "Sx") ? l : 0.6 * l)))]);
        var pairs = new Dictionary<(string, double), double[]>();
        double[] Pair(string key, double gap) => pairs.TryGetValue((Bed(key), gap), out var f) ? f
            : pairs[(Bed(key), gap)] = Peaks(PairPlan(key, gap), s, 2, 0.5 * lone, 1.5 * lone);
        static double Split(double[] f) => (f[1] * f[1] - f[0] * f[0]) / (f[1] * f[1] + f[0] * f[0]);
        var sides = Enumerable.Range(1, n - 1).Select(i => (I: i, J: i + 1, Key: $"S{i + 1}")).ToList();
        if (cross) sides.Add((1, n, "Sx"));
        var v = new Dictionary<string, object>(form) { ["N"] = (double)n };
        var onEm = new Dictionary<(string, double), double>();
        foreach (var (i, j, key) in sides)
        {
            string what = $"k({i},{j}) = {k[i - 1, j - 1]:G3}";
            double target = Math.Abs(k[i - 1, j - 1]);
            double gap = Solve(gap => Split(Pair(key, gap)), target, 0.1 * h, 5.0 * h, log: true, what, cancel);
            // EM: k(S) on the same bench through half, once and twice the circuit's gap.
            if (em is not null)
                gap = onEm.TryGetValue((Bed(key), gap), out var g) ? g : onEm[(Bed(key), gap)] = CouplingCurve.Through(
                    [Math.Max(0.1 * h, gap / 2.0), gap, Math.Min(5.0 * h, 2.0 * gap)], x =>
                    {
                        var f = Pair(key, PassivePlan.OnGrid(x));
                        return CouplingCurves.CouplingOf(em, PairPlan(key, PassivePlan.OnGrid(x)), s, Math.Sqrt(f[0] * f[1]));
                    }).Where(target, what);
            v[key] = PassivePlan.OnGrid(gap);
        }

        // 3. The feed from one resonator's group delay.
        // A feed so heavy that the resonance drowns in the direct path between the taps counts as Qe = 0.
        double Qe(PassivePlan one) => TryPeaks(one, s, 1, 0.5 * lone, 1.5 * lone) is [var f] ? ExternalQOf(one, s, f) : 0.0;
        (string, object)[] fed;
        if (tapped)
        {
            // The tap runs from the midpoint along a free stretch: a hairpin's end section, or a loop's side opposite
            // its opening and, in an odd row or the square, on round the corner along the outer side, the corner
            // skipped (an even row's last loop has its coupled side there).
            double tap, arm = new PlanBend([1, 2], 0, 0, 0, w, 90.0, PlanBend.MiterFor(PlanCorner.Miter, w, s)).Arm;
            // The tap along u in [lo, hi]; with EM, Qe(Tap) through it and a tenth of the range either side.
            double Fit(Func<double, double> tapOf, double lo, double hi)
            {
                string what = $"Qe = {qe:G3}";
                double u = Solve(x => Qe(Build(1, l, ("Tap", PassivePlan.OnGrid(tapOf(x))))), qe, lo, hi, log: false, what, cancel);
                if (em is null) return tapOf(u);
                double d = 0.1 * (hi - lo);
                return tapOf(CouplingCurve.Through([Math.Max(lo, u - d), u, Math.Min(hi, u + d)],
                    x => CouplingCurves.ExternalQOf(em, Build(1, l, ("Tap", PassivePlan.OnGrid(tapOf(x)))), s, lone))
                    .Where(qe, what));
            }
            if (form["Shape"] is "OpenLoop")
            {
                double side = (l + (double)form["Gap"]) / 4.0;
                double a = side / 2.0 - arm - w, b = side / 2.0 + arm + w;
                double c = n % 2 == 1 || cross ? 1.5 * side - arm - w : b;
                tap = Fit(u => u <= a ? u : u - a + b, w, a + c - b);
            }
            else
            {
                double centre = form.GetValueOrDefault("Profile") is "Sir" ? (double)form["Uc"] * l
                              : w + (double)form["Sa"] + 2.0 * arm;
                tap = Fit(t => t, centre / 2.0 + 0.6 * w, l / 2.0 - 0.6 * w);
            }
            v["Tap"] = PassivePlan.OnGrid(tap);
            fed = [("Tap", v["Tap"])];
        }
        else
        {
            double gap = PassivePlan.OnGrid(Solve(x => -Qe(Build(1, l, ("S1", PassivePlan.OnGrid(x)),
                ("S2", PassivePlan.OnGrid(x)))), -qe, 0.1 * h, 5.0 * h, log: true, $"Qe = {qe:G3}", cancel));
            v["S1"] = v[$"S{n + 1}"] = gap;
            fed = [("S1", gap), ("S2", gap)];
        }

        // 4. Each resonator sized for f0 where it sits.
        double byFeed = (Peaks(Build(1, l, fed), s, 1, 0.5 * fHz, 1.5 * fHz)[0] - lone) / 2.0;
        for (int i = 1; i <= n; i++)
        {
            double f = lone + (i == 1 ? byFeed : 0.0) + (i == n ? byFeed : 0.0);
            foreach (var (a, b, key) in sides)
                if (a == i || b == i) { var pair = Pair(key, (double)v[key]); f += Math.Sqrt(pair[0] * pair[1]) - lone; }
            v[$"L{i}"] = PassivePlan.OnGrid(l * f / fHz);
        }
        return v;
    }

    /// <summary>The bench k and Qe are measured on: <paramref name="count"/> resonators of <paramref name="form"/>'s shape,
    /// each <paramref name="l"/> long in a row, <paramref name="more"/>'s values on top.</summary>
    public static PassivePlan Bench(IReadOnlyDictionary<string, object> form, MicrostripSubstrate s, int count, double l,
        params (string Key, object Value)[] more)
    {
        var d = new Dictionary<string, object>(form) { ["N"] = (double)count, ["L"] = l, ["Arrangement"] = "Inline" };
        foreach (var (key, value) in more) d[key] = value;
        return CoupledResonatorPlan.Build(Spec(d), s);
    }

    /// <summary>Two resonators <paramref name="gap"/> apart, each lightly fed by a coupled strip 3·h off: the pair whose
    /// split gives k.</summary>
    public static PassivePlan LightPair(IReadOnlyDictionary<string, object> form, double l, double gap, MicrostripSubstrate s)
        => Bench(form, s, 2, l, ("Feed", "Coupled"), ("S1", 3.0 * s.HMeters), ("S3", 3.0 * s.HMeters), ("S2", gap));

    /// <summary>The spec a placed part with these stored values reads as.</summary>
    public static CoupledResonatorSpec Spec(IReadOnlyDictionary<string, object> v)
        => CoupledResonatorSpec.Read(key => v.TryGetValue(key, out var x) && x is double d ? d : null,
                                     key => v.TryGetValue(key, out var x) && x is string t ? t : null);

    private static MicrostripSubstrate Lossless(MicrostripSubstrate s)
        => s with { TanD = 0.0, SigmaSPerM = 1e20, RoughnessMeters = 0.0 };

    // ══ Measuring a plan on its own model ═════════════════════════════════════════════════════════════════════════

    /// <summary>S21 of a two-pin plan between <paramref name="z0"/> ports, the model built once.</summary>
    public static Func<double, Complex> S21(PassivePlan plan, MicrostripSubstrate s, double z0 = 50.0)
    {
        var model = new MicrostripPlanModel(plan, s, "(coupled-resonator synthesis)");
        var at = new ElaboratedComponent(nameof(MicrostripPlanModel), "(coupled-resonator synthesis)", [1, 2],
            new Dictionary<string, Value>(), model);
        return f =>
        {
            var y = new LocalAdmittanceBlock(2);
            model.Stamp(y, at, 2.0 * Math.PI * f);
            return -2.0 * z0 * y[1, 0] / ((1.0 + z0 * y[0, 0]) * (1.0 + z0 * y[1, 1]) - z0 * z0 * y[0, 1] * y[1, 0]);
        };
    }

    /// <summary>The frequencies of |S21|'s <paramref name="count"/> highest peaks in [lo, hi], each refined by golden
    /// section.</summary>
    public static double[] Peaks(PassivePlan plan, MicrostripSubstrate s, int count, double lo, double hi)
        => TryPeaks(plan, s, count, lo, hi) is { } f && f.Length == count ? f
            : throw Refusal("out-of-reach", "the circuit model shows fewer than the {count} resonances looked for between "
                + "{lo} and {hi} Hz.", ("count", count), ("lo", lo), ("hi", hi));

    private static double[] TryPeaks(PassivePlan plan, MicrostripSubstrate s, int count, double lo, double hi)
    {
        var s21 = S21(plan, s);
        double Db(double x) => 20.0 * Math.Log10(s21(x).Magnitude);
        var f = Enumerable.Range(0, 601).Select(i => lo + (hi - lo) * i / 600.0).ToArray();
        var y = f.Select(Db).ToArray();
        return [.. Enumerable.Range(1, f.Length - 2).Where(i => y[i] >= y[i - 1] && y[i] >= y[i + 1])
            .OrderByDescending(i => y[i]).Take(count).Order().Select(i => Golden(Db, f[i - 1], f[i + 1]))];
    }

    /// <summary>
    /// The external Q at each end of one resonator fed alike at both, from S21's group delay τ at its resonance
    /// <paramref name="f0Hz"/>: lossless and doubly loaded, S21 ≈ 1/(1 + j·Qe·(f − f0)/f0) there, so Qe = ω0·τ (after
    /// J. B. Ness, IEEE Trans. MTT 46(4), 1998, <see href="https://doi.org/10.1109/22.664135">10.1109/22.664135</see>).
    /// A local measure, so a tapped resonator's direct path between its taps, which keeps |S21| high far below the
    /// resonance, does not enter it as it would a 3 dB band.
    /// </summary>
    public static double ExternalQOf(PassivePlan one, MicrostripSubstrate s, double f0Hz)
    {
        var s21 = S21(one, s);
        double d = 1e-4 * f0Hz, turn = (s21(f0Hz + d) / s21(f0Hz - d)).Phase;
        return -f0Hz * turn / (2.0 * d);
    }

    /// <summary>The maximum of <paramref name="y"/> over [a, b], by golden section.</summary>
    public static double Golden(Func<double, double> y, double a, double b)
    {
        double r = (Math.Sqrt(5.0) - 1.0) / 2.0, x1 = b - r * (b - a), x2 = a + r * (b - a), y1 = y(x1), y2 = y(x2);
        while (b - a > 1e-7 * b)
        {
            if (y1 > y2) { b = x2; x2 = x1; y2 = y1; x1 = b - r * (b - a); y1 = y(x1); }
            else { a = x1; x1 = x2; y1 = y2; x2 = a + r * (b - a); y2 = y(x2); }
        }
        return 0.5 * (a + b);
    }

    /// <summary>Where a decreasing <paramref name="y"/> meets <paramref name="target"/> in [lo, hi], by bisection
    /// (in log space with <paramref name="log"/>) until the bracket is under half the plan's grid
    /// (<see cref="PassivePlan.OnGrid"/>) and 1e-7 of the range, each halving one circuit solve; refused when the answer sits
    /// at an end, <paramref name="what"/> out of the range's reach.</summary>
    private static double Solve(Func<double, double> y, double target, double lo, double hi, bool log, string what,
        CancellationToken cancel = default)
    {
        double a = log ? Math.Log(lo) : lo, b = log ? Math.Log(hi) : hi;
        double X(double t) => log ? Math.Exp(t) : t;
        for (int i = 0; i < 30 && X(b) - X(a) > Math.Min(1e-9, 1e-7 * hi); i++)
        {
            cancel.ThrowIfCancellationRequested();
            double m = 0.5 * (a + b);
            if (y(X(m)) > target) a = m; else b = m;
        }
        double x = X(0.5 * (a + b));
        if (Math.Abs(x - lo) < 1e-6 * hi || Math.Abs(x - hi) < 1e-6 * hi)
            throw Refusal("out-of-reach", "{what} is out of reach between {lo} and {hi} on this substrate.",
                ("what", what), ("lo", Length(lo)), ("hi", Length(hi)));
        return x;
    }

    /// <summary>A length for a message: µm below a millimetre, mm above.</summary>
    private static string Length(double m)
        => m < 1e-3 ? $"{LadderSynthesis.Fmt(m * 1e6)} µm" : $"{LadderSynthesis.Fmt(m * 1e3)} mm";

    internal static ParameterRefusalException Refusal(string code, string message, params (string, object?)[] args)
        => new(Diagnostic.Create("synthesis.mcrc." + code, DiagnosticSeverity.Error,
            "Coupled-resonator synthesis: " + message, args));
}
