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
/// The microstrip ladder (MLAD) through the engine. It is its plan's sections and nothing else, so it matches the same
/// MLIN, MGAP, MTAPER, MTEE and MCROSS instances wired by hand; and each filter and matcher the list is meant to hold,
/// computed here from its closed form and written as a list, does what that closed form says at F.
/// </summary>
public class LadderSParamTests(ITestOutputHelper output)
{
    private const string Sub = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=5.8e7 TanD=0.0037";
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static double Db(Complex x) => 20.0 * Math.Log10(x.Magnitude);

    // ── Writing a list from a design ────────────────────────────────────────────────────────────────────

    private static double Width(double z, double f, MicrostripSubstrate s) => LadderPlan.Width(z, f, s);

    private static MicrostripLineParameters At(double w, double f, MicrostripSubstrate s)
        => new MicrostripLineModel(w, 0.0, s.HMeters, s.TMeters, s.EpsR, s.SigmaSPerM, s.TanD, "test").LineParameters(f);

    /// <summary>The length of a line of width <paramref name="w"/> that is <paramref name="theta"/> long at F.</summary>
    private static double Len(double w, double theta, double f, MicrostripSubstrate s)
        => PlanarLineAngle.LengthMeters(At(w, f, s), theta);

    /// <summary>An open stub's end extension (Kirschning, Jansen and Koster), which its drawn length leaves out.</summary>
    private static double OpenEnd(double w, MicrostripSubstrate s)
        => MicrostripOpenEnd.Extension(w, s.HMeters, s.EpsR, At(w, 0.0, s).EeffStatic);

    /// <summary>A line of <paramref name="z"/> ohms, <paramref name="theta"/> long at F.</summary>
    private static LadderLine Line(double z, double theta, double f, MicrostripSubstrate s)
    {
        double w = Width(z, f, s);
        return new LadderLine(w, Len(w, theta, f, s));
    }

    /// <summary>A stub of <paramref name="z"/> ohms per arm, <paramref name="theta"/> long at F; an open one drawn short
    /// by its end extension.</summary>
    private static LadderStub Stub(double z, double theta, double f, LadderStubEnd end, LadderStubSide side, MicrostripSubstrate s)
    {
        double w = Width(z, f, s);
        return new LadderStub(w, Len(w, theta, f, s) - (end == LadderStubEnd.Open ? OpenEnd(w, s) : 0.0), end, side);
    }

    /// <summary>Butterworth lowpass prototype values g1…gN (g0 = g(N+1) = 1): 2·sin((2k − 1)π/2N). Textbook, no DOI
    /// (D. M. Pozar, Microwave Engineering, 4th ed., §8.3).</summary>
    private static double[] Butterworth(int n) => [.. Enumerable.Range(1, n).Select(k => 2.0 * Math.Sin((2 * k - 1) * Math.PI / (2 * n)))];

    // ── Running one ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ladder as one instance on <paramref name="ports"/>: pins 1 and 2 on p1 and p2, every via pin on
    /// ground, then whatever <paramref name="tail"/> adds (a load).</summary>
    private static string Cnl(IReadOnlyList<LadderSection> list, string ports, string sub = Sub, double f = 2.4e9, string tail = "")
    {
        string grounds = string.Concat(Enumerable.Repeat(" 0", LadderPlan.Grounds(list)));
        return $"{ports}\nMLAD:X1 p1 p2{grounds} Sections=\"{LadderPlan.Format(list)}\" Z0=50 Ohm F={F(f)} {sub}\n{tail}";
    }

    private static string Ports(params double[] z)
        => string.Join("\n", z.Select((r, k) => $"Port:P{k + 1} p{k + 1} 0 Num={k + 1} Z={F(r)} Ohm"));

    private static Complex[,] SAt(string cnl, double freqHz, int n)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var s = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), [freqHz])["S"];
        var m = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                m[i, j] = (Complex)s[0, i, j];
        return m;
    }

    private static Func<double, double> S21Db(string cnl) => f => Db(SAt(cnl, f, 2)[1, 0]);

    /// <summary>Where <paramref name="g"/> crosses <paramref name="level"/> between lo and hi, by bisection.</summary>
    private static double Crossing(Func<double, double> g, double lo, double hi, double level)
    {
        double glo = g(lo) - level;
        Assert.True(glo * (g(hi) - level) < 0.0, $"no crossing of {level} between {lo / 1e9} and {hi / 1e9} GHz");
        while (hi - lo > 1e3)
        {
            double mid = 0.5 * (lo + hi), gm = g(mid) - level;
            if (gm * glo > 0.0) { lo = mid; glo = gm; } else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>The minimum of <paramref name="g"/> between lo and hi, by golden section.</summary>
    private static double Minimum(Func<double, double> g, double lo, double hi)
    {
        double r = (Math.Sqrt(5.0) - 1.0) / 2.0, x1 = hi - r * (hi - lo), x2 = lo + r * (hi - lo), f1 = g(x1), f2 = g(x2);
        while (hi - lo > 1e3)
        {
            if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - r * (hi - lo); f1 = g(x1); }
            else { lo = x1; x1 = x2; f1 = f2; x2 = lo + r * (hi - lo); f2 = g(x2); }
        }
        return 0.5 * (lo + hi);
    }

    // ── The composite against its parts ─────────────────────────────────────────────────────────────────

    /// <summary>Every kind and option: a gap at pin 1, an open stub up, a line, a shorted stub down, a taper, an open
    /// cross, a line, a shorted cross.</summary>
    private static readonly LadderSection[] EveryKind =
    [
        new LadderGap(0.2e-3),
        new LadderStub(0.6e-3, 8e-3),
        new LadderLine(0.3e-3, 4.25e-3),
        new LadderStub(1.2e-3, 5e-3, LadderStubEnd.Short, LadderStubSide.Down),
        new LadderTaper(0.3e-3, 2.5e-3, 3e-3),
        new LadderStub(0.8e-3, 6e-3, LadderStubEnd.Open, LadderStubSide.Both),
        new LadderLine(2.5e-3, 1.5e-3),
        new LadderStub(0.5e-3, 7e-3, LadderStubEnd.Short, LadderStubSide.Both),
    ];

    /// <summary>The plan's own sections as ordinary instances at <paramref name="f"/>: the G pins on ground, an open end its
    /// capacitance there from the closed form on the line's own static and dispersive values.</summary>
    private static string ByHand(PassivePlan plan, double f)
    {
        string Node(int n) => n <= 2 ? $"p{n}" : n <= plan.Pins.Count ? "0" : $"i{n}";
        var b = new StringBuilder(Ports(50, 50)).AppendLine();
        int k = 0;
        foreach (var section in plan.Sections)
        {
            string nodes = string.Join(" ", section.Nodes.Select(Node));
            b.AppendLine(section switch
            {
                PlanTee t => $"MTEE:T{++k} {nodes} W1={F(t.W1)} W2={F(t.W2)} W3={F(t.W3)} L1={F(t.L1)} L2={F(t.L2)} L3={F(t.L3)} {Sub}",
                PlanCross x => $"MCROSS:X{++k} {nodes} W1={F(x.W1)} W2={F(x.W2)} W3={F(x.W3)} W4={F(x.W4)} "
                             + $"L1={F(x.L1)} L2={F(x.L2)} L3={F(x.L3)} L4={F(x.L4)} {Sub}",
                PlanLine l => $"MLIN:L{++k} {nodes} W={F(l.W)} L={F(l.L)} {Sub}",
                PlanGap g => $"MGAP:G{++k} {nodes} W={F(g.W)} S={F(g.S)} {Sub}",
                PlanTaper t => $"MTAPER:P{++k} {nodes} W1={F(t.W1)} W2={F(t.W2)} L={F(t.L)} {Sub}",
                PlanOpen o => $"C:C{++k} {nodes} 0 C={F(SingleEnd(o.W, f))}",
                _ => throw new InvalidOperationException(section.GetType().Name),
            });
        }
        return b.ToString();
    }

    private static double SingleEnd(double w, double f)
    {
        var p = At(w, f, Ro4350b);
        return MicrostripOpenEnd.Capacitance(OpenEnd(w, Ro4350b), p.Eeff, p.Z0);
    }

    [Theory]
    [InlineData(1.2e9)]
    [InlineData(4.8e9)]
    public void TheLadder_IsItsSectionsWiredByHand(double f)
    {
        var plan = LadderPlan.Build(EveryKind, 50.0, 2.4e9, Ro4350b).Plan;
        var (a, b) = (SAt(Cnl(EveryKind, Ports(50, 50)), f, 2), SAt(ByHand(plan, f), f, 2));
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                Assert.True((a[i, j] - b[i, j]).Magnitude < 1e-9, $"S{i + 1}{j + 1} at {f / 1e9} GHz: {a[i, j]} vs {b[i, j]}");
    }

    /// <summary>A DC block is one gap: the pins are separate copper, so there is no DC path, and it passes more as the
    /// frequency rises. A shorted stub's via pin is on the through line's copper.</summary>
    [Fact]
    public void AGap_IsADcBlock_AndAShortedStubIsOnTheLinesCopper()
    {
        var block = LadderPlan.Build("gap(S=0.1mm)", 50.0, 2.4e9, Ro4350b).Plan;
        Assert.Empty(new MicrostripPlanModel(block, Ro4350b, "X1").DcShortedTerminals);
        var cnl = Cnl(LadderPlan.Parse("gap(S=0.1mm)"), Ports(50, 50));
        Assert.True(Db(SAt(cnl, 10e9, 2)[1, 0]) > Db(SAt(cnl, 1e9, 2)[1, 0]) + 10.0);

        var stub = LadderPlan.Build("line(W=1mm, L=5mm); stub(W=1mm, L=5mm, end=short)", 50.0, 2.4e9, Ro4350b).Plan;
        Assert.Equal(new[] { (0, 1), (0, 2) }, new MicrostripPlanModel(stub, Ro4350b, "X1").DcShortedTerminals.Select(p => (p.A, p.B)));
    }

    // ── Lowpass ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The stepped-impedance lowpass from the Butterworth g-values (Pozar §8.6, textbook, no DOI): a shunt C is a low-Z
    /// line of βl = g·Zlow/R0, a series L a high-Z line of βl = g·R0/Zhigh, each at fc. N = 6, Zlow 15 Ω, Zhigh 120 Ω.
    /// The −3 dB point is fc to ±5 %; the steps between widths are not modelled, as the closed form leaves them out.
    /// </summary>
    [Fact]
    public void ASteppedImpedanceLowpass_CutsOffAtItsGValuesFc()
    {
        const double fc = 2.5e9, r0 = 50.0, zLow = 15.0, zHigh = 120.0;
        var g = Butterworth(6);
        var list = g.Select((gk, k) => (LadderSection)(k % 2 == 0
            ? Line(zLow, gk * zLow / r0, fc, Ro4350b)
            : Line(zHigh, gk * r0 / zHigh, fc, Ro4350b))).ToArray();
        double f3 = Crossing(S21Db(Cnl(list, Ports(50, 50))), 0.5 * fc, 1.5 * fc, -3.0);
        output.WriteLine($"{LadderPlan.Format(list)}\n-3 dB at {f3 / 1e9:F4} GHz ({(f3 / fc - 1) * 100:F2} %)");
        Assert.InRange(f3 / fc, 0.95, 1.05);
    }

    /// <summary>
    /// The open-stub lowpass, Richards and Kuroda's form of N = 3 Butterworth: series L 1, shunt C 2, series L 1 become
    /// series stubs of Z0 and a shunt stub of Z0/2, all λ/8 at fc (P. I. Richards,
    /// <a href="https://doi.org/10.1109/JRPROC.1948.233274">10.1109/JRPROC.1948.233274</a>); a unit element of Z0 at each
    /// end and Kuroda's identity (unit element Z1, series stub Z2 ≡ shunt stub Z1(Z1 + Z2)/Z2, unit element Z1 + Z2;
    /// textbook, no DOI) turn the series stubs into shunt ones: open stubs of 2·Z0, Z0/2 and 2·Z0, joined by unit elements
    /// of 2·Z0. The Z0/2 stub is drawn as two arms of Z0, a cross. The −3 dB point is fc to ±5 %.
    /// </summary>
    [Fact]
    public void AKurodaStubLowpass_CutsOffAtFc()
    {
        const double fc = 2.4e9, z0 = 50.0, eighth = Math.PI / 4.0;
        LadderSection[] list =
        [
            Stub(2 * z0, eighth, fc, LadderStubEnd.Open, LadderStubSide.Up, Ro4350b),
            Line(2 * z0, eighth, fc, Ro4350b),
            Stub(z0, eighth, fc, LadderStubEnd.Open, LadderStubSide.Both, Ro4350b),
            Line(2 * z0, eighth, fc, Ro4350b),
            Stub(2 * z0, eighth, fc, LadderStubEnd.Open, LadderStubSide.Up, Ro4350b),
        ];
        double f3 = Crossing(S21Db(Cnl(list, Ports(50, 50))), 0.5 * fc, 1.5 * fc, -3.0);
        output.WriteLine($"{LadderPlan.Format(list)}\n-3 dB at {f3 / 1e9:F4} GHz ({(f3 / fc - 1) * 100:F2} %)");
        Assert.InRange(f3 / fc, 0.95, 1.05);
    }

    /// <summary>
    /// The elliptic kind, a stepped-impedance lowpass with stubs: N = 3 Butterworth's series L's as 100 Ω lines and its
    /// shunt C as an open stub that is λ/4 at the zero f∞ = 1.3·fc and has the capacitor's admittance at fc,
    /// tan(πfc/2f∞)/Zs = ωc·C (textbook, no DOI). The transmission zero is at f∞ to ±5 %, and the passband passes: the
    /// closed form puts the stub at a point, and MTEE's own junction, whose branch inductance is negative for a stub
    /// wider than the line it stands on (Gupta, Garg and Chadha's fit), moves the zero about 3 % up on this board.
    /// </summary>
    [Fact]
    public void AnEllipticKind_PutsItsZeroWhereItsStubIsAQuarterWave()
    {
        const double fc = 2.4e9, r0 = 50.0, zHigh = 100.0, fInf = 1.3 * fc;
        var g = Butterworth(3);
        double zs = Math.Tan(Math.PI / 2.0 * fc / fInf) * r0 / g[1];
        double ws = Width(zs, fInf, Ro4350b);
        LadderSection[] list =
        [
            Line(zHigh, g[0] * r0 / zHigh, fc, Ro4350b),
            new LadderStub(ws, Len(ws, Math.PI / 2.0, fInf, Ro4350b) - OpenEnd(ws, Ro4350b)),
            Line(zHigh, g[2] * r0 / zHigh, fc, Ro4350b),
        ];
        var s21 = S21Db(Cnl(list, Ports(50, 50)));
        double zero = Minimum(s21, 1.1 * fc, 1.6 * fc);
        output.WriteLine($"{LadderPlan.Format(list)}\nzero at {zero / 1e9:F4} GHz ({(zero / fInf - 1) * 100:F2} %), "
                       + $"S21 {s21(zero):F1} dB; at fc/2 {s21(fc / 2):F2} dB");
        Assert.InRange(zero / fInf, 0.95, 1.05);
        Assert.True(s21(zero) < -30.0);
        Assert.True(s21(fc / 2) > -1.0);
    }

    // ── Bandpass, highpass, bandstop ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Quarter-wave shorted stubs joined by quarter-wave lines of Z0 (G. L. Matthaei,
    /// <a href="https://doi.org/10.1109/TMTT.1960.1124801">10.1109/TMTT.1960.1124801</a>), the stubs in the narrow-band
    /// form Z0n = π·Z0·Δ/(4·gn) (Pozar §8.8, textbook, no DOI).
    /// </summary>
    private static LadderSection[] ShortedStubFilter(double f0, double band, int n, Func<int, LadderStubSide> side)
    {
        var g = Butterworth(n);
        var list = new List<LadderSection>();
        for (int k = 0; k < n; k++)
        {
            if (k > 0) list.Add(Line(50.0, Math.PI / 2.0, f0, Ro4350b));
            double z = Math.PI * 50.0 * band / (4.0 * g[k]);
            // On both sides, two arms in parallel: each of twice the stub's impedance.
            list.Add(Stub(side(k) == LadderStubSide.Both ? 2.0 * z : z, Math.PI / 2.0, f0, LadderStubEnd.Short, side(k), Ro4350b));
        }
        return [.. list];
    }

    /// <summary>The shorted-stub bandpass, N = 3 Butterworth over Δ = 1, every stub a cross of two arms: the −3 dB
    /// band's centre is F to ±2 %.</summary>
    [Fact]
    public void AShortedStubBandpass_IsCentredOnF()
    {
        const double f0 = 2.4e9;
        var list = ShortedStubFilter(f0, 1.0, 3, _ => LadderStubSide.Both);
        Assert.Equal(6, LadderPlan.Grounds(list));
        var s21 = S21Db(Cnl(list, Ports(50, 50)));
        double lo = Crossing(s21, 0.3 * f0, f0, -3.0), hi = Crossing(s21, f0, 1.7 * f0, -3.0);
        output.WriteLine($"{LadderPlan.Format(list)}\n-3 dB {lo / 1e9:F4} to {hi / 1e9:F4} GHz, centre {(lo + hi) / 2e9:F4} GHz");
        Assert.InRange((lo + hi) / 2.0 / f0, 0.98, 1.02);
    }

    /// <summary>The shorted-stub highpass: the same structure made wide, Δ = 1.6 about f0 = 4.8 GHz, the outer stubs
    /// one-sided either way and the middle one a cross. Every stub shorts DC, so it stops low frequencies, and it passes
    /// from about 0.6·f0 until the stubs near a half wave at 2·f0 (the narrow-band form's band is narrower than Δ).</summary>
    [Fact]
    public void AShortedStubHighpass_StopsLowAndPassesHigh()
    {
        const double f0 = 4.8e9;
        var list = ShortedStubFilter(f0, 1.6, 3, k => k switch { 0 => LadderStubSide.Up, 1 => LadderStubSide.Both, _ => LadderStubSide.Down });
        var s21 = S21Db(Cnl(list, Ports(50, 50)));
        output.WriteLine(LadderPlan.Format(list));
        output.WriteLine(string.Join(", ", new[] { 0.1, 0.3, 0.6, 0.8, 1.0, 1.2, 1.4 }.Select(x => $"{x}·f0 {s21(x * f0):F2} dB")));
        Assert.True(s21(0.1 * f0) < -20.0 && s21(0.3 * f0) < -10.0, "the stopband");
        foreach (double x in new[] { 0.8, 1.0, 1.2 }) Assert.True(s21(x * f0) > -1.0, $"{x}·f0: {s21(x * f0):F2} dB");
    }

    /// <summary>The quarter-wave stub bandstop: open stubs λ/4 at F joined by λ/4 lines of Z0, the stubs
    /// Z0n = 4·Z0/(π·gn·Δ) (Pozar §8.8, textbook, no DOI), N = 3 Butterworth over Δ = 0.5. Its notch is at F.</summary>
    [Fact]
    public void AQuarterWaveStubBandstop_NotchesAtF()
    {
        const double f0 = 2.4e9, band = 0.5;
        var g = Butterworth(3);
        var list = new List<LadderSection>();
        for (int k = 0; k < 3; k++)
        {
            if (k > 0) list.Add(Line(50.0, Math.PI / 2.0, f0, Ro4350b));
            list.Add(Stub(4.0 * 50.0 / (Math.PI * g[k] * band), Math.PI / 2.0, f0, LadderStubEnd.Open, LadderStubSide.Up, Ro4350b));
        }
        var s21 = S21Db(Cnl(list, Ports(50, 50)));
        double notch = Minimum(s21, 0.8 * f0, 1.2 * f0);
        output.WriteLine($"{LadderPlan.Format(list)}\nnotch {notch / 1e9:F4} GHz ({(notch / f0 - 1) * 100:F2} %), {s21(notch):F1} dB");
        Assert.InRange(notch / f0, 0.99, 1.01);
        Assert.True(s21(notch) < -40.0);
    }

    /// <summary>
    /// The end-coupled (gap-coupled) bandpass (S. B. Cohn, <a href="https://doi.org/10.1109/JRPROC.1957.278389">
    /// 10.1109/JRPROC.1957.278389</a>): N = 3 Butterworth over Δ = 0.03 at 10 GHz on 25 mil alumina, where gaps reach the
    /// outer couplings. Inverters Z0·J1 = √(πΔ/2g1), Z0·Jk = πΔ/(2√(g(k−1)·gk)), Z0·J(N+1) = √(πΔ/2gN); each a series
    /// susceptance b = Z0J/(1 − (Z0J)²), a gap whose MGAP Cg is b/(ω0·Z0); resonator k is π − ½[atan 2bk + atan 2b(k+1)]
    /// long, less the length its ends' MGAP Cp stand for, atan(ω0·Cp·Z0) each (textbook, no DOI). The −3 dB band about
    /// the peak is centred on F to ±2 %.
    /// </summary>
    [Fact]
    public void AnEndCoupledBandpass_IsCentredOnF()
    {
        const double f0 = 10e9, band = 0.03, z0 = 50.0;
        const string alumina = "H=0.635e-3 T=5e-6 Er=9.8 Sigma=4.1e7 TanD=0.0001";
        var s = new MicrostripSubstrate(0.635e-3, 5e-6, 9.8, 4.1e7, 0.0001);
        double w0 = Width(z0, f0, s), w = 2.0 * Math.PI * f0;
        var g = Butterworth(3);
        double[] j = [Math.Sqrt(Math.PI * band / (2 * g[0])), Math.PI * band / (2 * Math.Sqrt(g[0] * g[1])),
                      Math.PI * band / (2 * Math.Sqrt(g[1] * g[2])), Math.Sqrt(Math.PI * band / (2 * g[2]))];
        double[] b = [.. j.Select(x => x / (1 - x * x))];
        var gaps = b.Select(bk =>
        {
            double cg = bk / (w * z0), lo = 2e-6, hi = 5e-3;   // Cg falls as S grows
            while (hi - lo > 1e-9)
            {
                double mid = 0.5 * (lo + hi);
                if (MicrostripGap.Capacitances(w0, mid, s.HMeters, s.EpsR).SeriesC > cg) lo = mid; else hi = mid;
            }
            return (S: 0.5 * (lo + hi), Cp: MicrostripGap.Capacitances(w0, 0.5 * (lo + hi), s.HMeters, s.EpsR).ShuntC);
        }).ToArray();
        double zLine = At(w0, f0, s).Z0;
        var list = new List<LadderSection> { new LadderGap(gaps[0].S) };
        for (int k = 0; k < 3; k++)
        {
            double theta = Math.PI - 0.5 * (Math.Atan(2 * b[k]) + Math.Atan(2 * b[k + 1]))
                         - Math.Atan(w * gaps[k].Cp * zLine) - Math.Atan(w * gaps[k + 1].Cp * zLine);
            list.Add(new LadderLine(w0, Len(w0, theta, f0, s)));
            list.Add(new LadderGap(gaps[k + 1].S));
        }
        var cnl = Cnl(list, Ports(50, 50), alumina, f0);
        var s21 = S21Db(cnl);
        double peak = Minimum(x => -s21(x), 0.95 * f0, 1.05 * f0);
        double top = s21(peak);
        double lo3 = Crossing(s21, 0.9 * f0, peak, top - 3.0), hi3 = Crossing(s21, peak, 1.1 * f0, top - 3.0);
        output.WriteLine($"{LadderPlan.Format(list)}\npeak {top:F2} dB; -3 dB {lo3 / 1e9:F4} to {hi3 / 1e9:F4} GHz, "
                       + $"centre {(lo3 + hi3) / 2e9:F4} GHz");
        Assert.InRange((lo3 + hi3) / 2.0 / f0, 0.98, 1.02);
    }



    // ── Matchers and transformers ───────────────────────────────────────────────────────────────────────

    /// <summary>The matchers' frequency, and a load of <paramref name="r"/> in series with the capacitor of reactance
    /// <paramref name="x"/> there, on pin 2.</summary>
    private const double Fm = 2e9;

    private static string Load(double r, double x) => $"R:RL p2 m R={F(r)} Ohm\nC:CL m 0 C={F(-1.0 / (2.0 * Math.PI * Fm * x))}";

    /// <summary>
    /// The single-stub shunt matcher (Pozar §5.2, textbook, no DOI), into 60 − j80 Ω: the stub d from the load, where
    /// t = tan βd = [XL + √(RL·((Z0 − RL)² + XL²)/Z0)]/(RL − Z0), and a shorted stub of tan βl = Y0/B with
    /// B = [RL²t − (Z0 − XL·t)(XL + Z0·t)]/(Z0·[RL² + (XL + Z0·t)²]). Into the complex load |S11| at F is under −30 dB.
    /// </summary>
    [Fact]
    public void ASingleStubMatcher_MatchesAComplexLoad()
    {
        const double z0 = 50.0, Rl = 60.0, Xl = -80.0;
        double t = (Xl + Math.Sqrt(Rl * ((z0 - Rl) * (z0 - Rl) + Xl * Xl) / z0)) / (Rl - z0);
        double bd = Math.Atan(t) < 0 ? Math.Atan(t) + Math.PI : Math.Atan(t);
        double bb = (Rl * Rl * t - (z0 - Xl * t) * (Xl + z0 * t)) / (z0 * (Rl * Rl + Math.Pow(Xl + z0 * t, 2)));
        double bl = Math.Atan(1.0 / (z0 * bb));
        if (bl < 0) bl += Math.PI;
        LadderSection[] list =
        [
            Stub(z0, bl, Fm, LadderStubEnd.Short, LadderStubSide.Up, Ro4350b),
            Line(z0, bd, Fm, Ro4350b),
        ];
        double s11 = Db(SAt(Cnl(list, Ports(50), f: Fm, tail: Load(Rl, Xl)), Fm, 1)[0, 0]);
        output.WriteLine($"{LadderPlan.Format(list)}\n|S11| at F {s11:F1} dB");
        Assert.True(s11 < -30.0, $"|S11| {s11:F1} dB");
    }

    /// <summary>
    /// The double-stub shunt matcher (Pozar §5.3, textbook, no DOI), into 40 − j30 Ω: open stubs λ/8 apart, the first at
    /// the load, of susceptances b1 = −bL + [1 − √((1 + t²)gL − gL²t²)]/t and b2 = [−√((1 + t²)gL − gL²t²) + gL]/(gL·t) in
    /// Y0, with t = tan βd = 1 and yL = gL + j·bL the load's admittance there: of the two solutions the one whose stubs are
    /// least steep in susceptance against length. What the closed form leaves out (the stubs' loss, a junction's
    /// capacitance) reaches the input about 1/gL times larger, so the stubs are 70 Ω, where MTEE's capacitance is least,
    /// each tan βl = b·Zs/Z0 long. Into the complex load |S11| at F is under −30 dB.
    /// </summary>
    [Fact]
    public void ADoubleStubMatcher_MatchesAComplexLoad()
    {
        const double z0 = 50.0, t = 1.0, zs = 70.0, Rl = 40.0, Xl = -30.0;
        var y = z0 / new Complex(Rl, Xl);
        double root = Math.Sqrt((1 + t * t) * y.Real - y.Real * y.Real * t * t);
        double b1 = -y.Imaginary + (1 - root) / t, b2 = (-root + y.Real) / (y.Real * t);
        static double Open(double bk) => Math.Atan(bk * zs / z0) is var a && a < 0 ? a + Math.PI : Math.Atan(bk * zs / z0);
        LadderSection[] list =
        [
            Stub(zs, Open(b2), Fm, LadderStubEnd.Open, LadderStubSide.Up, Ro4350b),
            Line(z0, Math.Atan(t), Fm, Ro4350b),
            Stub(zs, Open(b1), Fm, LadderStubEnd.Open, LadderStubSide.Down, Ro4350b),
        ];
        double s11 = Db(SAt(Cnl(list, Ports(50), f: Fm, tail: Load(Rl, Xl)), Fm, 1)[0, 0]);
        output.WriteLine($"b1 {b1:F3}, b2 {b2:F3}\n{LadderPlan.Format(list)}\n|S11| at F {s11:F1} dB");
        Assert.True(s11 < -30.0, $"|S11| {s11:F1} dB");
    }

    /// <summary>The three-section binomial quarter-wave transformer from 50 to 100 Ω, ln(Zn+1/Zn) = 2^−N·C(N, n)·ln(ZL/Z0)
    /// (Pozar §5.6, textbook, no DOI): each section λ/4 at F. Into 100 Ω, |S11| at F is under −40 dB.</summary>
    [Fact]
    public void ABinomialQuarterWaveTransformer_MatchesAtF()
    {
        const double f0 = 2.4e9, z0 = 50.0, zl = 100.0;
        int[] c = [1, 3, 3, 1];
        double z = z0;
        var list = new List<LadderSection>();
        for (int n = 0; n < 3; n++)
        {
            z *= Math.Exp(Math.Pow(2, -3) * c[n] * Math.Log(zl / z0));
            list.Add(Line(z, Math.PI / 2.0, f0, Ro4350b));
        }
        double s11 = Db(SAt(Cnl(list, Ports(z0, zl)), f0, 2)[0, 0]);
        output.WriteLine($"{LadderPlan.Format(list)}\n|S11| at F {s11:F1} dB");
        Assert.True(s11 < -40.0, $"|S11| {s11:F1} dB");
    }
}
