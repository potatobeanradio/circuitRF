using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// MCLA, the coupled array, through the engine: each form is MCLINN, MLIN and its open ends' capacitances wired by hand to
/// 1e-9; an interdigital filter designed from Matthaei's closed forms lands on its centre and its ripple band; a combline
/// filter's loading capacitors, from the textbook resonance condition, set its centre.
/// </summary>
public class CoupledArrayTests(ITestOutputHelper output)
{
    private const string Sub = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=5.8e7 TanD=0.0037";
    /// <summary>The same board without loss, for the filters: the closed forms are lossless.</summary>
    private const string Lossless = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=1e15 TanD=0";
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);
    private const double C0 = 299_792_458.0;

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

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

    /// <summary>|S21| in dB over <paramref name="freqs"/>, ports 1 and 2 only.</summary>
    internal static double[] S21Db(string cnl, double[] freqs)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var s = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), freqs)["S"];
        return [.. Enumerable.Range(0, freqs.Length).Select(i => 20.0 * Math.Log10(((Complex)s[i, 1, 0]).Magnitude))];
    }

    private static string Ports(int n) => string.Join("\n",
        Enumerable.Range(1, n).Select(k => $"Port:P{k} p{k} 0 Num={k} Z=50 Ohm"));

    /// <summary>The part's own line: every strip's width and gap stated, the words as written.</summary>
    private static string Line(CoupledArraySpec s, string nets, string sub = Sub)
        => $"MCLA:A1 {nets} N={s.N} {string.Join(" ", s.W.Select((w, k) => $"W[{k + 1}]={F(w)}"))} "
         + $"{string.Join(" ", s.S.Select((g, k) => $"S[{k + 1}]={F(g)}"))} "
         + $"{string.Join(" ", s.C.Select((c, k) => $"C[{k + 1}]={F(c)}"))} L={F(s.L)} Form={s.Form} Feed={s.Feed} "
         + (s.Ends is { } e ? $"Ends=\"{e}\" " : "") + $"Tap={F(s.Tap)} Wf={F(s.Wf)} {sub}";

    private static CoupledArraySpec Spec(CoupledArrayForm form, CoupledArrayFeed feed, int n, string? ends = null)
        => new([.. Enumerable.Range(0, n).Select(k => k == 1 ? 1.3e-3 : 1.0e-3)],
               [.. Enumerable.Range(0, n - 1).Select(k => k == 0 ? 0.35e-3 : 0.5e-3)], 16e-3, form, feed, ends, 3e-3, 1.1e-3,
               [.. Enumerable.Range(0, n).Select(k => (1.0 + 0.2 * k) * 1e-12)]);

    /// <summary>
    /// Every form (and a custom pattern of every end kind) is its plan's sections wired by hand as ordinary instances:
    /// each <see cref="PlanCoupledStrips"/> an MCLINN on the strips' cut nodes, each tap, fold and feed an MLIN, each
    /// open or loaded end MLIN's end capacitance as a C. Every pin a port, to 1e-9. The part is read from its netlist
    /// line, so Form, Feed and Ends pass the elaborator as words.
    /// </summary>
    [Theory]
    [InlineData(CoupledArrayForm.Interdigital, CoupledArrayFeed.Tapped, 3, null)]
    [InlineData(CoupledArrayForm.Interdigital, CoupledArrayFeed.Coupled, 4, null)]
    [InlineData(CoupledArrayForm.Combline, CoupledArrayFeed.Tapped, 3, null)]
    [InlineData(CoupledArrayForm.Combline, CoupledArrayFeed.Coupled, 4, null)]
    [InlineData(CoupledArrayForm.PseudoInterdigital, CoupledArrayFeed.Coupled, 6, null)]
    [InlineData(CoupledArrayForm.PseudoInterdigital, CoupledArrayFeed.Tapped, 4, null)]
    [InlineData(CoupledArrayForm.Custom, CoupledArrayFeed.Coupled, 4, "PS OC JO JP")]
    public void TheArray_IsItsSectionsWiredByHand(CoupledArrayForm form, CoupledArrayFeed feed, int n, string? ends)
    {
        var spec = Spec(form, feed, n, ends);
        var plan = CoupledArrayPlan.Build(spec, Ro4350b).Plan;
        int pins = plan.Pins.Count;
        string nets = string.Join(" ", Enumerable.Range(1, pins).Select(k => $"p{k}"));
        double worst = 0.0;
        foreach (double f in new[] { 1.1e9, 2.6e9, 4.3e9 })
        {
            var part = SAt($"{Ports(pins)}\n{Line(spec, nets)}", f, pins);
            var byHand = SAt(ByHand(plan, f), f, pins);
            for (int i = 0; i < pins; i++)
                for (int j = 0; j < pins; j++)
                    worst = Math.Max(worst, (part[i, j] - byHand[i, j]).Magnitude);
        }
        output.WriteLine($"{form}/{feed}, N = {n}: {pins} pins, {plan.Sections.Count} sections, worst |dS| {worst:G3}");
        Assert.True(worst < 1e-9, $"worst |dS| {worst:G3}");
    }

    private static string ByHand(PassivePlan plan, double f)
    {
        int pins = plan.Pins.Count;
        string Node(int n) => n <= pins ? $"p{n}" : $"i{n}";
        var b = new StringBuilder(Ports(pins)).AppendLine();
        int k = 0;
        foreach (var section in plan.Sections)
            b.AppendLine(section switch
            {
                PlanCoupledStrips m => $"MCLINN:K{++k} {string.Join(" ", m.ModelNodes.Select(Node))} N={m.W.Count} "
                    + string.Join(" ", m.W.Select((w, j) => $"W[{j + 1}]={F(w)}")) + " "
                    + string.Join(" ", m.S.Select((g, j) => $"S[{j + 1}]={F(g)}")) + $" L={F(m.L)} {Sub}",
                PlanLine l => $"MLIN:L{++k} {string.Join(" ", l.Nodes.Select(Node))} W={F(l.W)} L={F(l.L)} {Sub}",
                PlanOpen o => $"C:C{++k} {Node(o.Nodes[0])} 0 C={F(OpenEnd(o.W, f))}",
                _ => throw new InvalidOperationException(section.GetType().Name),
            });
        return b.ToString();
    }

    private static MicrostripLineModel Mlin(double w)
        => new(w, 0.0, Ro4350b.HMeters, Ro4350b.TMeters, Ro4350b.EpsR, Ro4350b.SigmaSPerM, Ro4350b.TanD, "test");

    private static double Extension(double w)
        => MicrostripOpenEnd.Extension(w, Ro4350b.HMeters, Ro4350b.EpsR, Mlin(w).LineParameters(0.0).EeffStatic);

    /// <summary>A strip end's capacitance at <paramref name="f"/>: the closed form's extension on MLIN's own line.</summary>
    private static double OpenEnd(double w, double f)
    {
        var p = Mlin(w).LineParameters(f);
        return MicrostripOpenEnd.Capacitance(Extension(w), p.Eeff, p.Z0);
    }

    // ── An interdigital filter from Matthaei's closed forms (CoupledArraySynthesis) ─────────────────────────────────

    /// <summary>A Matthaei interdigital design realised on <paramref name="sub"/> and run lossless: its 3 dB edges, and its
    /// ripple band, the outermost frequencies within the loss it actually ripples to between its first and last peaks.</summary>
    private (double Centre, double Band, double Ripple) Interdigital(MicrostripSubstrate sub, string lossless, double h)
    {
        const double f0 = 2.4e9, fbw = 0.2, ripple = 0.1, z0 = 50.0;
        var (ground, mutual) = CoupledArraySynthesis.Interdigital(LowpassPrototype.GValues(FilterResponse.Chebyshev, 3, ripple), fbw, h);
        var (w, s, section) = CoupledArraySynthesis.Fit(ground, mutual, z0, sub);
        double eeff = section.Modes.Eeff.Average();
        output.WriteLine($"Er {sub.EpsR}, h {h}: W {string.Join(", ", w.Select(x => (x * 1e3).ToString("F3")))} mm; "
                       + $"S {string.Join(", ", s.Select(x => (x * 1e3).ToString("F3")))} mm; eeff {eeff:F3}");
        var line = new MicrostripLineModel(w[2], 0.0, sub.HMeters, sub.TMeters, sub.EpsR, sub.SigmaSPerM, sub.TanD, "test");
        double l = C0 / Math.Sqrt(eeff) / (4 * f0)
                 - MicrostripOpenEnd.Extension(w[2], sub.HMeters, sub.EpsR, line.LineParameters(0.0).EeffStatic);
        var spec = new CoupledArraySpec(w, s, l, CoupledArrayForm.Interdigital, CoupledArrayFeed.Coupled, null, 0, 0,
                                        [.. Enumerable.Repeat(1e-12, 5)]);
        string nets = "p1 p2 " + string.Join(" ", Enumerable.Repeat("0", CoupledArrayPlan.Build(spec, sub).Plan.Pins.Count - 2));
        double[] freqs = [.. Enumerable.Range(0, 801).Select(i => f0 * (0.6 + 0.8 * i / 800))];
        var db = S21Db($"{Ports(2)}\n{Line(spec, nets, lossless)}", freqs);

        var (lo3, hi3) = Edges(freqs, db, -3.0);
        var peaks = Enumerable.Range(1, db.Length - 2).Where(i => db[i] >= db[i - 1] && db[i] > db[i + 1] && db[i] > -1.0).ToList();
        double realised = -Enumerable.Range(peaks[0], peaks[^1] - peaks[0] + 1).Min(i => db[i]);
        var (lo, hi) = Outermost(freqs, db, -Math.Max(realised, ripple) - 1e-3);
        output.WriteLine($"  3 dB {lo3 / 1e9:F4}-{hi3 / 1e9:F4} GHz; ripples to {realised:F3} dB, {lo / 1e9:F4}-{hi / 1e9:F4} GHz; "
                       + $"{peaks.Count} peaks");
        return ((lo3 + hi3) / 2 / f0, (hi - lo) / (fbw * f0), realised);
    }

    /// <summary>
    /// A three-resonator 0.1 dB Chebyshev interdigital filter, 20 % wide at 2.4 GHz, from Matthaei's closed forms: the
    /// g-values, then each line's admittance to ground and each adjacent pair's mutual (resonator scale h = 0.15), then
    /// the widths and gaps whose field-solved cross-section has them. Run lossless, as the closed forms are. In a
    /// homogeneous dielectric (εr = 1, so TEM) it is the design itself: centre within 0.2 %, ripple 0.1 dB, ripple band
    /// within 7 %. On 20 mil RO4350B its centre (the middle of the 3 dB band) lands within 3 % of 2.4 GHz and its ripple
    /// band within 20 % of the design's, measured at the ripple it actually has: about 0.6 dB, since microstrip's modes
    /// run at unequal velocities and the N-line model, quasi-static, keeps each at its own. The design is TEM; that
    /// difference is what the tolerances are for.
    /// </summary>
    [Fact]
    public void AnInterdigitalFilter_FromMatthaeisClosedForms_LandsOnItsCentreAndBand()
    {
        var air = Interdigital(new MicrostripSubstrate(0.508e-3, 35e-6, 1.0, 1e15, 0.0), "H=0.508e-3 T=35e-6 Er=1 Sigma=1e15 TanD=0", 0.15);
        Assert.InRange(air.Centre, 0.998, 1.002);
        Assert.InRange(air.Ripple, 0.08, 0.12);
        Assert.InRange(air.Band, 0.93, 1.07);

        var board = Interdigital(Ro4350b, Lossless, 0.15);
        Assert.InRange(board.Centre, 0.97, 1.03);
        Assert.InRange(board.Band, 0.8, 1.2);
    }

    /// <summary>
    /// Hong and Lancaster's pseudo-interdigital board (<a href="https://doi.org/10.1109/75.401073">10.1109/75.401073</a>,
    /// Fig. 2): three groups of interleaved hairpins between two feed strips on 1.27 mm of εr 10.8, strips 0.4 mm wide
    /// (the feeds 0.5 mm), 0.3 mm from the feeds, 1.0 mm apart in a group and 1.1 mm between groups, the walls 0.5 mm clear
    /// of the ends they pass, 26.5 mm overall (so 25.5 mm strips, less a wall and its clearance), run lossless. The paper's
    /// filter is 15 % wide at 1.1 GHz with an attenuation pole at the upper band edge; the plan's 3 dB band lands within
    /// 5 % of that centre and 20 % of that width, with the pole just above it.
    /// </summary>
    [Fact]
    public void HongAndLancastersBoard_PassesItsMeasuredBand()
    {
        const string board = "H=1.27e-3 T=17e-6 Er=10.8 Sigma=1e15 TanD=0";
        double[] w = [0.5e-3, .. Enumerable.Repeat(0.4e-3, 12), 0.5e-3];
        double[] s = [0.3e-3, 1e-3, 1e-3, 1e-3, 1.1e-3, 1e-3, 1e-3, 1e-3, 1.1e-3, 1e-3, 1e-3, 1e-3, 0.3e-3];
        var spec = new CoupledArraySpec(w, s, 25.5e-3, CoupledArrayForm.PseudoInterdigital, CoupledArrayFeed.Coupled, null,
                                        0, 0, [.. Enumerable.Repeat(1e-12, 14)], 0.5e-3);
        double[] freqs = [.. Enumerable.Range(0, 241).Select(i => 0.8e9 + 2.5e6 * i)];
        var db = S21Db($"{Ports(2)}\n{Line(spec, "p1 p2", board)}", freqs);
        var (lo3, hi3) = Edges(freqs, db, -3.0);
        output.WriteLine($"3 dB {lo3 / 1e9:F4}-{hi3 / 1e9:F4} GHz, best {db.Max():F3} dB");
        Assert.InRange((lo3 + hi3) / 2 / 1.1e9, 0.95, 1.05);
        Assert.InRange((hi3 - lo3) / (0.15 * 1.1e9), 0.8, 1.2);
        Assert.Contains(Enumerable.Range(0, freqs.Length), i => freqs[i] > hi3 && freqs[i] < hi3 + 0.1e9 && db[i] < -35.0);
    }

    /// <summary>The lowest and highest frequencies where |S21| is at least <paramref name="levelDb"/>.</summary>
    internal static (double Lo, double Hi) Outermost(double[] f, double[] db, double levelDb)
    {
        int lo = Array.FindIndex(db, x => x >= levelDb), hi = Array.FindLastIndex(db, x => x >= levelDb);
        return lo < 0 ? (0, 0) : (f[lo], f[hi]);
    }

    // ── A combline filter: the loading capacitors from the resonance condition ──────────────────────────────────────

    /// <summary>
    /// A three-resonator combline filter, tapped a quarter of the way up from the shorts, on 20 mil RO4350B (lossless):
    /// strips 1 mm wide and 1.2 mm apart, 45° long at 2 GHz on the middle strip's own line, each loaded at its open end by
    /// the textbook's resonance condition C = Y·cot θ0/ω0 (Matthaei, <i>Microwave J.</i> 1963, no DOI), taken on the
    /// array's susceptance with every other loaded end shorted, each mode at its own speed, less the end's own fringing,
    /// which the part models (<see cref="CoupledArraySynthesis.ComblineLoading"/>). Its centre, the middle of the 3 dB band, lands within 3 % of 2 GHz. The loading C is what sets it: every
    /// C 20 % larger moves it down by about 8 %, as the resonance condition says (δf/f = −δC/C / (1 + θ0/(sin θ0·cos θ0))
    /// at 45°).
    /// </summary>
    [Fact]
    public void AComblineFilter_ItsLoadingCapacitorsFromTheResonanceCondition_LandsOnItsCentre()
    {
        const double f0 = 2.0e9, theta0 = Math.PI / 4;
        double[] w = [1.0e-3, 1.0e-3, 1.0e-3], s = [1.2e-3, 1.2e-3];
        var (l, c, _) = CoupledArraySynthesis.ComblineLoading(MicrostripMultiLineModel.Solve(w, s, Ro4350b), w, theta0, f0, Ro4350b);
        output.WriteLine($"L {l * 1e3:F3} mm, C {string.Join(", ", c.Select(x => (x * 1e12).ToString("F3")))} pF");

        double Centre(double scale)
        {
            var spec = new CoupledArraySpec(w, s, l, CoupledArrayForm.Combline, CoupledArrayFeed.Tapped, null, l / 4, 1.1e-3,
                                            [.. c.Select(x => x * scale)]);
            var plan = CoupledArrayPlan.Build(spec, Ro4350b).Plan;
            var nets = new StringBuilder("p1 p2");
            var loads = new StringBuilder();
            foreach (var site in plan.Sites)
                if (site.Role == PlanSiteRole.Via) nets.Append(" 0");
                else
                {
                    nets.Append($" {site.PinA}");
                    loads.AppendLine($"C:C{site.PinA} {site.PinA} 0 C={F(site.Value!.Value)}");
                }
            double[] freqs = [.. Enumerable.Range(0, 401).Select(i => f0 * (0.6 + 0.8 * i / 400))];
            var db = S21Db($"{Ports(2)}\n{Line(spec, nets.ToString(), Lossless)}\n{loads}", freqs);
            var (lo3, hi3) = Edges(freqs, db, -3.0);
            output.WriteLine($"C x{scale}: 3 dB {lo3 / 1e9:F4}-{hi3 / 1e9:F4} GHz, best {db.Max():F3} dB");
            return (lo3 + hi3) / 2;
        }

        double centre = Centre(1.0), heavier = Centre(1.2);
        Assert.InRange(centre / f0, 0.97, 1.03);
        Assert.InRange(heavier / centre, 0.85, 0.95);
    }

    /// <summary>The outermost frequencies either side of the response's peak where |S21| is still at least
    /// <paramref name="levelDb"/>, walking out from the peak.</summary>
    internal static (double Lo, double Hi) Edges(double[] f, double[] db, double levelDb)
    {
        int peak = Array.IndexOf(db, db.Max()), lo = peak, hi = peak;
        while (lo > 0 && db[lo - 1] >= levelDb) lo--;
        while (hi < db.Length - 1 && db[hi + 1] >= levelDb) hi++;
        return (f[lo], f[hi]);
    }
}
