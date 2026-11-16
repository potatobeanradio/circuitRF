using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// MCRC, the coupled-resonator filter, through the engine. Each shape is built from a textbook design computed here (the
/// part stores geometry; synthesis is not the part's), and its response lands on the design: Cohn's J-inverter
/// parallel-coupled filter as Straight, folded as ZigZag and as Hairpin (Cristal and Frankel's hairpin, without the
/// arms' own coupling), Hong and Lancaster's coupling coefficients for the open loops, and the ring at one guided
/// wavelength. The bandstop and spurline notch where their closed forms put them, and the composite is its sections
/// wired by hand.
/// </summary>
public class CoupledResonatorSParamTests(ITestOutputHelper output)
{
    // RO4350B, 20 mil, lossless, so the passband ripple is the design's.
    private const string Sub = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=1e20 TanD=0";
    private static readonly MicrostripSubstrate Board = new(0.508e-3, 35e-6, 3.66, 1e20, 0.0);
    private const string Ports = """
        Port:P1 p1 0 Num=1 Z=50 Ohm
        Port:P2 p2 0 Num=2 Z=50 Ohm
        """;
    private const double F0 = 2.4e9, Z0 = 50.0;

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    // ══ The textbook designs ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Chebyshev lowpass prototype g0..g(n+1) for <paramref name="rippleDb"/> (G. L. Matthaei, L. Young and
    /// E. M. T. Jones' closed form).</summary>
    internal static double[] Chebyshev(int n, double rippleDb)
    {
        double beta = Math.Log(1.0 / Math.Tanh(rippleDb / 17.37)), gamma = Math.Sinh(beta / (2.0 * n));
        var g = new double[n + 2];
        g[0] = 1.0;
        double a(int k) => Math.Sin((2 * k - 1) * Math.PI / (2.0 * n));
        double b(int k) => gamma * gamma + Math.Pow(Math.Sin(k * Math.PI / n), 2);
        g[1] = 2.0 * a(1) / gamma;
        for (int k = 2; k <= n; k++) g[k] = 4.0 * a(k - 1) * a(k) / (b(k - 1) * g[k - 1]);
        g[n + 1] = n % 2 == 1 ? 1.0 : Math.Pow(1.0 / Math.Tanh(beta / 4.0), 2);
        return g;
    }

    private static readonly MicrostripValidityReporter Quiet = new("(test design)");

    private static KirschningJansenCoupled.Modes Modes(double w, double s)
        => KirschningJansenCoupled.Compute(F0, w, s, Board.HMeters, Board.TMeters, Board.EpsR, Quiet);

    /// <summary>The gap at which a pair of width <paramref name="w"/> has voltage coupling <paramref name="c"/> at F0,
    /// (Ze − Zo)/(Ze + Zo), by bisection over MCLIN's own modes.</summary>
    internal static double GapFor(double w, double c)
    {
        double lo = Math.Log(0.1 * Board.HMeters), hi = Math.Log(10.0 * Board.HMeters);
        for (int i = 0; i < 60; i++)
        {
            double mid = 0.5 * (lo + hi);
            var m = Modes(w, Math.Exp(mid));
            if ((m.Ze - m.Zo) / (m.Ze + m.Zo) > c) lo = mid; else hi = mid;
        }
        return PassivePlan.OnGrid(Math.Exp(0.5 * (lo + hi)));
    }

    internal static double W50 => PassivePlan.OnGrid(HammerstadJensen.SynthesizeWidth(Z0, Board.HMeters, Board.TMeters, Board.EpsR, Quiet));

    private static double LineEeff(double w)
        => new MicrostripLineModel(w, 0.0, Board.HMeters, Board.TMeters, Board.EpsR, Board.SigmaSPerM, Board.TanD, "t")
            .LineParameters(F0).Eeff;

    private static double OpenEnd(double w)
        => MicrostripOpenEnd.Extension(w, Board.HMeters, Board.EpsR,
            new MicrostripLineModel(w, 0.0, Board.HMeters, Board.TMeters, Board.EpsR, Board.SigmaSPerM, Board.TanD, "t")
                .LineParameters(0.0).EeffStatic);

    /// <summary>
    /// Cohn's parallel-coupled half-wave filter (S. B. Cohn, IRE Trans. MTT 6(2), 1958): each coupled section an
    /// admittance inverter, J01·Z0 = √(π·FBW/(2·g0·g1)), J(k,k+1)·Z0 = π·FBW/(2√(g_k·g_(k+1))), Ze = Z0(1 + JZ0 + (JZ0)²),
    /// Zo = Z0(1 − JZ0 + (JZ0)²). On one strip width, each section's gap is the one whose (Ze − Zo)/(Ze + Zo) is Cohn's,
    /// JZ0/(1 + (JZ0)²); each section is λg/4 in its modes' mean εeff, and each resonator its two sections less an open end
    /// at each end. The resonators take their mean length.
    /// </summary>
    internal static (double W, double L, double[] S) Cohn(int n, double rippleDb, double fbw)
    {
        var g = Chebyshev(n, rippleDb);
        double w = W50;
        var s = new double[n + 2];
        var quarter = new double[n + 2];
        for (int j = 1; j <= n + 1; j++)
        {
            double x = j == 1 ? Math.Sqrt(Math.PI * fbw / (2.0 * g[0] * g[1]))
                     : j == n + 1 ? Math.Sqrt(Math.PI * fbw / (2.0 * g[n] * g[n + 1]))
                     : Math.PI * fbw / (2.0 * Math.Sqrt(g[j - 1] * g[j]));
            s[j] = GapFor(w, x / (1.0 + x * x));
            var m = Modes(w, s[j]);
            quarter[j] = 299_792_458.0 / (4.0 * F0 * Math.Sqrt((m.EeffE + m.EeffO) / 2.0));
        }
        double l = Enumerable.Range(1, n).Average(k => quarter[k] + quarter[k + 1]) - 2.0 * OpenEnd(w);
        return (w, PassivePlan.OnGrid(l), s);
    }

    /// <summary>What one mitred corner of width <paramref name="w"/> adds to a resonator's centre-line length beyond its
    /// electrical length: its two arms, less its own phase as a length of the line (<see cref="PlanBend.LineLength"/>).</summary>
    internal static double CornerExcess(double w)
    {
        var bend = new PlanBend([1, 2], 0, 0, 0, w, 90.0, PlanBend.MiterFor(PlanCorner.Miter, w, Board));
        double eeff = LineEeff(w);
        double z = new MicrostripLineModel(w, 0.0, Board.HMeters, Board.TMeters, Board.EpsR, Board.SigmaSPerM, Board.TanD, "t")
            .LineParameters(F0).Z0;
        double quarter = 299_792_458.0 / (4.0 * F0 * Math.Sqrt(eeff));
        return 2.0 * bend.Arm - bend.LineLength(Board, F0, z, quarter);
    }

    /// <summary>A spec as the factory reads one, from named values.</summary>
    internal static CoupledResonatorSpec Spec(params (string Key, object Value)[] values)
    {
        var d = new Dictionary<string, object>();
        foreach (var (k, v) in values) d[k] = v;   // a later value wins
        return CoupledResonatorSpec.Read(k => d.TryGetValue(k, out var v) && v is double x ? x : null,
                                         k => d.TryGetValue(k, out var v) && v is string t ? t : null);
    }

    internal static (string, object)[] Gaps(double[] s, int from, int to)
        => [.. Enumerable.Range(from, to - from + 1).Select(k => ($"S{k}", (object)s[k]))];

    // ══ Running a plan ═════════════════════════════════════════════════════════════════════════════════════════════

    private static ElaboratedNetlist AsPart(PassivePlan plan)
    {
        var nl = Elaborate($"{Ports}\nMLIN:X1 p1 p2 W=1e-3 L=1e-3 {Sub}");
        int at = nl.Components.FindIndex(c => c.InstancePath == "X1");
        var c = nl.Components[at];
        nl.Components[at] = new ElaboratedComponent(c.ComponentType, c.InstancePath, c.Nodes, c.Parameters,
            new MicrostripPlanModel(plan, Board, c.InstancePath));
        return nl;
    }

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static Complex[,] SAt(ElaboratedNetlist nl, double freqHz)
    {
        var s = SParameterEngine.Run(nl, [freqHz])["S"];
        var m = new Complex[2, 2];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                m[i, j] = (Complex)s[0, i, j];
        return m;
    }

    private static double[] S21Db(ElaboratedNetlist nl, double[] f)
    {
        var s = SParameterEngine.Run(nl, f)["S"];
        return [.. Enumerable.Range(0, f.Length).Select(i => 20.0 * Math.Log10(((Complex)s[i, 1, 0]).Magnitude))];
    }

    /// <summary>The passband's ripple edges: the lowest and highest frequencies in [lo, hi] where |S21| is within
    /// <paramref name="rippleDb"/> of 0 dB, each refined by bisection.</summary>
    private static (double F1, double F2) RippleEdges(PassivePlan plan, double rippleDb, double lo, double hi)
    {
        var nl = AsPart(plan);
        var f = Enumerable.Range(0, 801).Select(i => lo + (hi - lo) * i / 800.0).ToArray();
        var db = S21Db(nl, f);
        bool In(double v) => v >= -rippleDb;
        int i1 = Array.FindIndex(db, In), i2 = Array.FindLastIndex(db, In);
        Assert.True(i1 > 0 && i2 < f.Length - 1, $"no passband inside {lo / 1e9}-{hi / 1e9} GHz");
        double Edge(double a, double b)   // a outside, b inside
        {
            for (int k = 0; k < 40; k++)
            {
                double m = 0.5 * (a + b);
                if (In(S21Db(nl, [m])[0])) b = m; else a = m;
            }
            return 0.5 * (a + b);
        }
        return (Edge(f[i1 - 1], f[i1]), Edge(f[i2 + 1], f[i2]));
    }

    [Theory]
    [InlineData("Straight")]
    [InlineData("ZigZag")]
    [InlineData("Hairpin")]
    public void AChainDesign_LandsOnItsBand(string shape)
    {
        const int n = 3;
        const double ripple = 0.5, fbw = 0.1;
        var (w, l, s) = Cohn(n, ripple, fbw);
        double sa = 2.0 * w;
        l += shape switch { "ZigZag" => CornerExcess(w), "Hairpin" => 2.0 * CornerExcess(w), _ => 0.0 };
        var spec = Spec([("N", (double)n), ("W", w), ("L", PassivePlan.OnGrid(l)), ("Shape", shape), ("Sa", sa), .. Gaps(s, 1, n + 1)]);
        var plan = CoupledResonatorPlan.Build(spec, Board);
        var (f1, f2) = RippleEdges(plan, ripple, 0.8 * F0, 1.2 * F0);
        double centre = (f1 + f2) / 2.0, bw = f2 - f1;
        output.WriteLine($"{shape}: W {w * 1e3:F4} L {l * 1e3:F4} S {string.Join(" ", s.Skip(1).Take(n + 1).Select(x => (x * 1e3).ToString("F4")))} mm; " +
                         $"band {f1 / 1e9:F4}-{f2 / 1e9:F4} GHz, centre {centre / F0 - 1:P2} off, bandwidth {bw / (fbw * F0) - 1:P1} off");
        Assert.InRange(centre / F0, 0.98, 1.02);
        Assert.InRange(bw / (fbw * F0), 0.85, 1.15);
    }

    // ══ Open loops: Hong and Lancaster's coupling coefficients ═══════════════════════════════════════════════════

    /// <summary>The frequencies of |S21|'s <paramref name="count"/> highest peaks in [lo, hi], each refined by golden
    /// section.</summary>
    private static double[] Peaks(PassivePlan plan, int count, double lo, double hi)
    {
        var nl = AsPart(plan);
        var f = Enumerable.Range(0, 601).Select(i => lo + (hi - lo) * i / 600.0).ToArray();
        var db = S21Db(nl, f);
        var tops = Enumerable.Range(1, f.Length - 2).Where(i => db[i] >= db[i - 1] && db[i] >= db[i + 1])
            .OrderByDescending(i => db[i]).Take(count).Order().ToList();
        Assert.Equal(count, tops.Count);
        return [.. tops.Select(i => Golden(x => S21Db(nl, [x])[0], f[i - 1], f[i + 1]))];
    }

    /// <summary>The maximum of <paramref name="y"/> over [a, b], by golden section.</summary>
    private static double Golden(Func<double, double> y, double a, double b)
    {
        double r = (Math.Sqrt(5.0) - 1.0) / 2.0, x1 = b - r * (b - a), x2 = a + r * (b - a), y1 = y(x1), y2 = y(x2);
        while (b - a > 1e3)
        {
            if (y1 > y2) { b = x2; x2 = x1; y2 = y1; x1 = b - r * (b - a); y1 = y(x1); }
            else { a = x1; x1 = x2; y1 = y2; x2 = a + r * (b - a); y2 = y(x2); }
        }
        return 0.5 * (a + b);
    }

    private static (string, object)[] Loop(double w, double l, double gap, string arrangement = "Inline")
        => [("W", w), ("L", PassivePlan.OnGrid(l)), ("Shape", "OpenLoop"), ("Gap", gap), ("Arrangement", arrangement)];

    /// <summary>Bisection of a decreasing <paramref name="y"/> for <paramref name="target"/> over [lo, hi] (log space).</summary>
    private static double Solve(Func<double, double> y, double target, double lo, double hi)
    {
        double a = Math.Log(lo), b = Math.Log(hi);
        for (int i = 0; i < 30; i++)
        {
            double m = 0.5 * (a + b);
            if (y(Math.Exp(m)) > target) a = m; else b = m;
        }
        return PassivePlan.OnGrid(Math.Exp(0.5 * (a + b)));
    }

    /// <summary>
    /// Hong and Lancaster's method (IEEE Trans. MTT 44(12), 1996), on the model itself: the lowpass prototype's coupling
    /// coefficients M(k,k+1) = FBW/√(g_k·g_(k+1)) and external Q, Qe = g0·g1/FBW. Each gap is the one whose two loops,
    /// lightly fed, split into f1 and f2 with (f2² − f1²)/(f2² + f1²) = M; the tap the one at which one loop, tapped at
    /// both ends, has a 3 dB bandwidth of 2·f0/Qe. Each loop is then sized for f0 where it sits: a coupled side lowers a
    /// loop's own resonance by the shift of the pair's mean, √(f1·f2), from the lone loop's, and a tap by its own shift,
    /// so the middle loops, with two coupled sides, come out smaller than the end ones.
    /// </summary>
    internal static (double W, double[] L, double Gap, double[] S, double Tap) OpenLoopDesign(int n, double rippleDb, double fbw)
    {
        var g = Chebyshev(n, rippleDb);
        double w = W50, gap = 1e-3, light = 1e-3;
        double l = 299_792_458.0 / (2.0 * F0 * Math.Sqrt(LineEeff(w))) + 4.0 * CornerExcess(w);
        double Lone(double tap) => Peaks(CoupledResonatorPlan.Build(Spec([("N", 1.0), .. Loop(w, l, gap), ("Feed", "Tapped"),
            ("Tap", tap)]), Board), 1, 0.8 * F0, 1.2 * F0)[0];
        for (int i = 0; i < 4; i++) l *= Lone(light) / F0;
        double[] Pair(double s) => Peaks(CoupledResonatorPlan.Build(Spec([("N", 2.0), .. Loop(w, l, gap), ("Feed", "Tapped"),
            ("Tap", light), ("S2", s)]), Board), 2, 0.85 * F0, 1.15 * F0);
        double Split(double s) { var f = Pair(s); return (f[1] * f[1] - f[0] * f[0]) / (f[1] * f[1] + f[0] * f[0]); }
        var gaps = new double[n + 2];
        for (int k = 1; k < n; k++) gaps[k + 1] = Solve(Split, fbw / Math.Sqrt(g[k] * g[k + 1]), 0.1 * Board.HMeters, 4.0 * Board.HMeters);
        double Qe(double tap)
        {
            var one = CoupledResonatorPlan.Build(Spec([("N", 1.0), .. Loop(w, l, gap), ("Feed", "Tapped"), ("Tap", tap)]), Board);
            var (f1, f2) = RippleEdges(one, 3.0, 0.7 * F0, 1.3 * F0);
            return 2.0 * (f1 + f2) / 2.0 / (f2 - f1);
        }
        double tapAt = Solve(Qe, g[0] * g[1] / fbw, w, (l + gap) / 4.0 - w);

        double lone = Lone(light), byTap = Lone(tapAt) - lone;
        var lengths = new double[n];
        for (int k = 1; k <= n; k++)
        {
            double f = lone + (k == 1 || k == n ? byTap : 0.0);
            foreach (int j in new[] { k, k + 1 })   // gaps S_k and S_(k+1), where there is a loop beyond
                if (j >= 2 && j <= n) { var pair = Pair(gaps[j]); f += Math.Sqrt(pair[0] * pair[1]) - lone; }
            lengths[k - 1] = PassivePlan.OnGrid(l * f / F0);
        }
        return (w, lengths, gap, gaps, tapAt);
    }

    [Fact]
    public void AnOpenLoopDesign_LandsOnItsBand()
    {
        const int n = 3;
        const double ripple = 0.5, fbw = 0.05;
        var (w, l, gap, s, tap) = OpenLoopDesign(n, ripple, fbw);
        var plan = CoupledResonatorPlan.Build(Spec([("N", (double)n), .. Loop(w, l[0], gap), .. Lengths(l), ("Feed", "Tapped"),
            ("Tap", tap), .. Gaps(s, 2, n)]), Board);
        var (f1, f2) = RippleEdges(plan, ripple, 0.85 * F0, 1.15 * F0);
        double centre = (f1 + f2) / 2.0, bw = f2 - f1;
        output.WriteLine($"OpenLoop: W {w * 1e3:F4} L {string.Join(" ", l.Select(x => (x * 1e3).ToString("F4")))} Gap {gap * 1e3} " +
                         $"S {s[2] * 1e3:F4} {s[3] * 1e3:F4} Tap {tap * 1e3:F4} mm; " +
                         $"band {f1 / 1e9:F4}-{f2 / 1e9:F4} GHz, centre {centre / F0 - 1:P2} off, bandwidth {bw / (fbw * F0) - 1:P1} off");
        Assert.InRange(centre / F0, 0.98, 1.02);
        Assert.InRange(bw / (fbw * F0), 0.85, 1.15);
    }

    private static (string, object)[] Lengths(double[] l) => [.. l.Select((x, k) => ($"L{k + 1}", (object)x))];

    /// <summary>The four loops in Hong and Lancaster's square: 1 and 4 cross-coupled across their openings, 2 and 3 back
    /// to back. The cross coupling against the main path puts a transmission zero either side of the passband, where an
    /// all-pole response falls off monotonically.</summary>
    [Fact]
    public void TheCrossCoupledLoops_HaveAZeroEitherSide()
    {
        var (w, l, gap, s, tap) = OpenLoopDesign(3, 0.5, 0.05);
        // Loops 1 and 4 as the inline design's end loops, 2 and 3 as its middle one; the cross coupling weaker.
        var plan = CoupledResonatorPlan.Build(Spec([("N", 4.0), .. Loop(w, l[0], gap, "Cross"), ("L2", l[1]), ("L3", l[1]),
            ("Feed", "Tapped"), ("Tap", tap), ("S2", s[2]), ("S3", s[2]), ("S4", s[2]), ("Sx", 3.0 * s[2])]), Board);
        var nl = AsPart(plan);
        var f = Enumerable.Range(0, 801).Select(i => F0 * (0.8 + 0.4 * i / 800.0)).ToArray();
        var db = S21Db(nl, f);
        double pass = f[Array.IndexOf(db, db.Max())];
        var dips = Enumerable.Range(1, f.Length - 2).Where(i => db[i] < db[i - 1] && db[i] < db[i + 1] && db[i] < -20.0)
            .Select(i => f[i]).ToList();
        output.WriteLine($"zeros at {string.Join(", ", dips.Select(x => (x / 1e9).ToString("F4")))} GHz; passband peak {pass / 1e9:F4} GHz, {db.Max():F2} dB");
        Assert.True(db.Max() > -1.0, $"no passband: |S21| peaks at {db.Max()} dB");
        Assert.Contains(dips, x => x < pass);
        Assert.Contains(dips, x => x > pass);
    }

    // ══ Ring, bandstop, spurline ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>A ring one guided wavelength round at F0 (in its line's εeff), fed lightly across a gap either side,
    /// passes at its first resonance there.</summary>
    [Fact]
    public void ARingOneWavelengthRound_PassesAtF0()
    {
        double w = W50, l = 299_792_458.0 / (F0 * Math.Sqrt(LineEeff(w)));
        var plan = CoupledResonatorPlan.Build(Spec([("W", w), ("L", PassivePlan.OnGrid(l)), ("Shape", "Ring"),
            ("N", 1.0), ("S1", 0.2e-3), ("S2", 0.2e-3)]), Board);
        double peak = Peaks(plan, 1, 0.9 * F0, 1.1 * F0)[0];
        output.WriteLine($"Ring: L {l * 1e3:F3} mm, peak {peak / 1e9:F4} GHz ({peak / F0 - 1:P2})");
        Assert.InRange(peak / F0, 0.98, 1.02);
    }

    /// <summary>The frequency of |S21|'s deepest null in [lo, hi].</summary>
    private static double Null(PassivePlan plan, double lo, double hi)
    {
        var nl = AsPart(plan);
        var f = Enumerable.Range(0, 401).Select(i => lo + (hi - lo) * i / 400.0).ToArray();
        var db = S21Db(nl, f);
        int i = Array.IndexOf(db, db.Min());
        return Golden(x => -S21Db(nl, [x])[0], f[Math.Max(0, i - 1)], f[Math.Min(f.Length - 1, i + 1)]);
    }

    /// <summary>A bandstop resonator, half a guided wavelength at F0 less its two open ends and with its corners' excess
    /// added (its closed form), coupled over its first half to the through line, notches the line at F0.</summary>
    [Theory]
    [InlineData("ZigZag")]
    [InlineData("Hairpin")]
    public void ABandstopResonator_NotchesWhereItIsHalfAWavelength(string shape)
    {
        double w = W50, sa = 2.0 * w;
        double l = 299_792_458.0 / (2.0 * F0 * Math.Sqrt(LineEeff(w))) - 2.0 * OpenEnd(w)
                 + (shape == "Hairpin" ? 2.0 : 1.0) * CornerExcess(w);
        var plan = CoupledResonatorPlan.Build(Spec([("N", 1.0), ("W", w), ("L", PassivePlan.OnGrid(l)), ("Shape", shape),
            ("Form", "Bandstop"), ("Sa", sa), ("S1", 0.3e-3)]), Board);
        double notch = Null(plan, 0.85 * F0, 1.15 * F0);
        output.WriteLine($"Bandstop {shape}: L {l * 1e3:F3} mm, notch {notch / 1e9:F4} GHz ({notch / F0 - 1:P2})");
        Assert.InRange(notch / F0, 0.98, 1.02);
    }

    /// <summary>
    /// A spurline notches where its spur is a quarter wavelength in the coupled pair's odd mode (R. N. Bates, IEE J.
    /// Microwaves, Optics and Acoustics 1(6), 1977): with the through strip and the spur joined at the near end and the
    /// spur open at the far end, the pair's transfer admittance is 2/(j(Ze·sin θe − Zo·tan θo·cos θe)), which vanishes
    /// at θo = π/2. Its closed form here: L plus the spur's open end, a quarter wave in the odd mode's εeff at the notch.
    /// </summary>
    [Fact]
    public void ASpurline_NotchesWhereItsSpurIsAnOddModeQuarterWave()
    {
        double w = 0.6e-3, s = 0.2e-3, l = 18e-3;
        var plan = CoupledResonatorPlan.Build(Spec([("N", 1.0), ("W", w), ("L", l), ("Form", "Spurline"), ("S1", s)]), Board);
        double notch = Null(plan, 1.5e9, 4.5e9);
        double closed = 2.5e9;
        for (int i = 0; i < 20; i++)
        {
            var m = KirschningJansenCoupled.Compute(closed, w, s, Board.HMeters, Board.TMeters, Board.EpsR, Quiet);
            closed = 299_792_458.0 / (4.0 * (l + OpenEnd(w)) * Math.Sqrt(m.EeffO));
        }
        output.WriteLine($"Spurline: notch {notch / 1e9:F4} GHz, closed form {closed / 1e9:F4} GHz ({notch / closed - 1:P2})");
        Assert.InRange(notch / closed, 0.98, 1.02);
    }

    // ══ The composite is its sections ════════════════════════════════════════════════════════════════════════════

    /// <summary>The plan's own sections as ordinary instances at <paramref name="f"/>: MLIN, MCLIN, MMITER, MCURVE, MTEE
    /// and MGAP, and an open end as its capacitance there.</summary>
    private static ElaboratedNetlist ByHand(PassivePlan plan, double f)
    {
        string Node(int n) => n <= 2 ? $"p{n}" : $"i{n}";
        var b = new StringBuilder(Ports).AppendLine();
        int k = 0;
        foreach (var section in plan.Sections)
        {
            string nodes = string.Join(" ", section.Nodes.Select(Node));
            b.AppendLine(section switch
            {
                PlanTee t => $"MTEE:T{++k} {nodes} W1={F(t.W1)} W2={F(t.W2)} W3={F(t.W3)} L1={F(t.L1)} L2={F(t.L2)} L3={F(t.L3)} {Sub}",
                PlanLine l => $"MLIN:L{++k} {nodes} W={F(l.W)} L={F(l.L)} {Sub}",
                PlanCoupledLine c => $"MCLIN:M{++k} {nodes} W={F(c.W)} S={F(c.S)} L={F(c.L)} {Sub}",
                PlanCurve c => $"MCURVE:C{++k} {nodes} W={F(c.W)} Angle={F(c.AngleDeg)} deg Radius={F(c.Radius)} {Sub}",
                PlanBend { M: { } m } bend => $"MMITER:B{++k} {nodes} W={F(bend.W)} Angle={F(bend.AngleDeg)} deg M={F(m)} {Sub}",
                PlanGap g => $"MGAP:G{++k} {nodes} W={F(g.W)} S={F(g.S)} {Sub}",
                PlanOpen o => $"C:C{++k} {nodes} 0 C={F(EndCapacitance(o.W, f))}",
                _ => throw new InvalidOperationException(section.GetType().Name),
            });
        }
        return Elaborate(b.ToString());
    }

    private static double EndCapacitance(double w, double f)
    {
        var p = new MicrostripLineModel(w, 0.0, Board.HMeters, Board.TMeters, Board.EpsR, Board.SigmaSPerM, Board.TanD, "t")
            .LineParameters(f);
        return MicrostripOpenEnd.Capacitance(OpenEnd(w), p.Eeff, p.Z0);
    }

    /// <summary>One filter of each shape and form, on the textbook design's numbers.</summary>
    internal static CoupledResonatorSpec Sample(string which)
    {
        var (w, l, s) = Cohn(3, 0.5, 0.1);
        (string, object)[] common = [("N", 3.0), ("W", w), ("L", l), ("Sa", 2.0 * w), .. Gaps(s, 1, 4)];
        return which switch
        {
            "Straight" => Spec(common),
            "ZigZag" => Spec([.. common, ("Shape", "ZigZag")]),
            "HairpinSirTapped" => Spec([.. common, ("Shape", "Hairpin"), ("Profile", "SIR"), ("Wc", 0.5e-3), ("Uc", 0.4),
                ("Feed", "Tapped"), ("Tap", 9e-3)]),
            "OpenLoop" => Spec([.. common, ("Shape", "OpenLoop"), ("Gap", 1e-3)]),
            "Cross" => Spec([.. common, ("N", 4.0), ("S5", s[4]), ("Shape", "OpenLoop"), ("Arrangement", "Cross"),
                ("Gap", 1e-3), ("Sx", 1e-3), ("Feed", "Tapped"), ("Tap", 3e-3)]),
            "Ring" => Spec([("N", 1.0), ("W", w), ("L", 2.0 * l), ("Shape", "Ring"), ("S1", 0.2e-3), ("S2", 0.2e-3)]),
            "Bandstop" => Spec([.. common, ("Shape", "ZigZag"), ("Form", "Bandstop")]),
            _ => Spec([("N", 2.0), ("W", 0.6e-3), ("L", 18e-3), ("Form", "Spurline"), ("S1", 0.2e-3), ("S2", 0.3e-3)]),
        };
    }

    [Theory]
    [InlineData("Straight")]
    [InlineData("ZigZag")]
    [InlineData("HairpinSirTapped")]
    [InlineData("OpenLoop")]
    [InlineData("Cross")]
    [InlineData("Ring")]
    [InlineData("Bandstop")]
    [InlineData("Spurline")]
    public void TheFilter_IsItsSectionsWiredByHand(string which)
    {
        var plan = CoupledResonatorPlan.Build(Sample(which), Board);
        foreach (double f in new[] { 1.3e9, 2.25e9, 3.7e9 })
        {
            var (a, b) = (SAt(AsPart(plan), f), SAt(ByHand(plan, f), f));
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    Assert.True((a[i, j] - b[i, j]).Magnitude < 1e-9, $"S{i + 1}{j + 1} at {f / 1e9} GHz: {a[i, j]} vs {b[i, j]}");
        }
    }
}
