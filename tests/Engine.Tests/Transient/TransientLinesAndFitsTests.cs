using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Transient;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Transient;

/// <summary>
/// brief-trn-4's gates: lossless lines and coupled pairs as delays, the lossy line's stop-and-report, wideband rational fits
/// of an S-parameter file (passivity enforced) and their long-run stability, and a tone source's harmonic terminations
/// (docs/design/transient.md §5.3-§5.4). Closed forms throughout, except the file, which is its own reference.
/// </summary>
public sealed class TransientLinesAndFitsTests(ITestOutputHelper output)
{
    private static (TransientRunResult Result, ElaboratedNetlist Nl) Run(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var p = TransientRunner.Resolve(tb.Analyses.OfType<TransientAnalysis>().Single(), nl.ResolvedGlobals);
        return (new TransientEngine(nl).Run(p), nl);
    }

    private static double[] Node(TransientRunResult r, string name)
    {
        int i = Array.IndexOf(r.NodeNames, name);
        Assert.True(i >= 0, $"node {name} is not recorded: {string.Join(", ", r.NodeNames)}");
        return [.. r.V.Select(v => v[i])];
    }

    private static string TestData(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata");
            if (Directory.Exists(cand)) return Path.Combine([cand, .. parts]);
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata not found above " + AppContext.BaseDirectory);
    }

    /// <summary>A ramp from 0 at t0 to 1 at t0 + rise, held: the source the line gates are driven with.</summary>
    private static double Ramp(double t, double t0, double rise) => t <= t0 ? 0 : t >= t0 + rise ? 1 : (t - t0) / rise;

    /// <summary>
    /// Gate 1 — a pulse into a 50 Ω line (τ = 1 ns) terminated in 100 Ω: the near end holds the incident half until 2τ after the
    /// edge, then rises by a third of it; the reflection's corner is an accepted step, at exactly 2τ after the source's.
    /// </summary>
    [Fact]
    public void Gate1_LosslessReflection_ArrivesAt2Tau_WithAThird()
    {
        var (r, nl) = Run("""
            V_Pulse:VS s 0 V1=0 V2=1 Delay=0.5n Rise=0.1n Fall=0.1n Width=20n
            R:RS s in R=50
            TLIN:TL1 in out Z=50 E=360 deg F=1 GHz
            R:RL out 0 R=100
            analysis TR1 type=tran Stop=4n Step=0.01n SaveSteps=yes
            """);
        Assert.Contains(nl.Notes, n => n.Contains("TL1") && n.Contains("stepped as its delay"));
        var line = Assert.Single(r.Lines);
        Assert.Equal(1e-9, line.DelaysS[0], 15);
        var vin = Node(r, "in");
        var vout = Node(r, "out");
        double worst = 0;
        for (int k = 0; k < r.Time.Length; k++)
        {
            double t = r.Time[k], inc = 0.5 * Ramp(t, 0.5e-9, 0.1e-9);
            double wantIn = inc + 0.5 * (1.0 / 3) * Ramp(t, 2.5e-9, 0.1e-9);
            double wantOut = (1 + 1.0 / 3) * 0.5 * Ramp(t, 1.5e-9, 0.1e-9);
            worst = Math.Max(worst, Math.Max(Math.Abs(vin[k] - wantIn), Math.Abs(vout[k] - wantOut)));
        }
        output.WriteLine($"largest departure from the closed form: {worst:G3} V");
        Assert.True(worst < 1e-6, $"worst {worst:G3}");
        int at = r.Time.Select((t, i) => (t, i)).First(z => z.t >= 3e-9).i;
        Assert.Equal(1.0 / 3, (vin[at] - 0.5) / 0.5, 6);
        // the reflection's two corners are steps, to the last few bits of 2.5 ns and 2.6 ns
        foreach (double c in new[] { 2.5e-9, 2.6e-9 })
            Assert.Contains(r.StepTime!, t => Math.Abs(t - c) <= 1e-21);
    }

    /// <summary>
    /// Gate 2 — a lossless coupled pair (Ze 75, Zo 33.3 Ω, even 90° and odd 80° at 1 GHz) driven at port 1, every port in 50 Ω:
    /// near- and far-end crosstalk equal the modal closed form (each mode a single line between its own modal terminations,
    /// reflected at both ends) within 1e-6.
    /// </summary>
    [Fact]
    public void Gate2_CoupledPair_CrosstalkIsTheModalClosedForm()
    {
        const double ze = 75, zo = 2500.0 / 75, tauE = 0.25e-9, tauO = 0.25e-9 * 80 / 90;
        var (r, _) = Run(string.Format(CultureInfo.InvariantCulture, """
            V_Pulse:VS s 0 V1=0 V2=1 Delay=0.1n Rise=0.05n Fall=0.05n Width=20n
            R:RS s p1 R=50
            CLIN:TC p1 p2 p3 p4 Ze={0:R} Ohm Zo={1:R} Ohm E=90 deg Eo=80 deg F=1 GHz
            R:R2 p2 0 R=50
            R:R3 p3 0 R=50
            R:R4 p4 0 R=50
            analysis TR1 type=tran Stop=0.85n Step=0.002n
            """, ze, zo));
        // each mode: Zc = Z/2 against 25 Ω at both ends, driven by half the source behind 25 Ω
        static (double Near, double Far) Mode(double t, double zc, double tau)
        {
            double a = zc / (zc + 25), g = (25 - zc) / (25 + zc);
            double s(double x) => 0.5 * Ramp(x, 0.1e-9, 0.05e-9);
            double near = a * s(t), far = 0;
            for (int k = 1; k < 8; k++) near += (1 + g) * Math.Pow(g, 2 * k - 1) * a * s(t - 2 * k * tau);
            for (int k = 0; k < 8; k++) far += (1 + g) * Math.Pow(g, 2 * k) * a * s(t - (2 * k + 1) * tau);
            return (near, far);
        }
        var v3 = Node(r, "p3");
        var v4 = Node(r, "p4");
        double worst = 0, peak3 = 0, peak4 = 0;
        for (int k = 0; k < r.Time.Length; k++)
        {
            var e = Mode(r.Time[k], ze / 2, tauE);
            var o = Mode(r.Time[k], zo / 2, tauO);
            double next = e.Near - o.Near, fext = e.Far - o.Far;
            peak3 = Math.Max(peak3, Math.Abs(next)); peak4 = Math.Max(peak4, Math.Abs(fext));
            worst = Math.Max(worst, Math.Max(Math.Abs(v3[k] - next), Math.Abs(v4[k] - fext)));
        }
        output.WriteLine($"NEXT peak {peak3:G4} V, FEXT peak {peak4:G4} V, largest departure {worst:G3} V");
        Assert.True(peak3 > 0.05 && peak4 > 0.01, "the crosstalk is not trivially zero");
        Assert.True(worst < 1e-6, $"worst {worst:G3}");
    }

    /// <summary>
    /// Gate 3, as brief TRN-4b left it — TRN-4 stopped and reported because no shipped lossy line was causal; with the losses'
    /// phase in the models (CausalLoss) a lossy microstrip runs as a delay and a rational residual within its band (here
    /// MaxStep 50 ps: up to 10 GHz), and is refused by name, with its error, where it is not (Step 10 ps sets the band to 50 GHz,
    /// where 25 mm of 0.8 mm FR4 is not within 1e-3). A lossless stripline runs as its delay alone.
    /// </summary>
    [Fact]
    public void Gate3_ALossyLine_RunsWithinItsBand_AndIsRefusedByNameBeyondIt()
    {
        const string bench = """
            V_Pulse:VS s 0 V1=0 V2=1 Delay=0.1n Rise=0.1n Fall=0.1n Width=2n
            R:RS s in R=50
            MLIN:TL1 in out W=1.5 mm L=25 mm H=0.8 mm T=35 um Er=4.4 TanD=0.02 Sigma=5.8e7
            R:RL out 0 R=50
            """;
        var (r10, _) = Run(bench + "\nanalysis TR1 type=tran Stop=2n Step=0.01n MaxStep=50p\n");
        var line = Assert.Single(r10.Lines);
        Assert.True(line.Order > 0 && line.Error <= TransientRealization.LineTolerance);

        var ex = Assert.Throws<TransientRefusedException>(() => Run(bench + "\nanalysis TR1 type=tran Stop=2n Step=0.01n\n"));
        Assert.Contains("TL1 (MLIN): this lossy or dispersive line cannot be realized in transient up to 50 GHz", ex.Message);
        Assert.Contains("S-parameter, harmonic balance and envelope analyses support it", ex.Message);

        var (r, _) = Run("""
            V_Pulse:VS s 0 V1=0 V2=1 Delay=0.1n Rise=0.1n Fall=0.1n Width=2n
            R:RS s in R=50
            SLIN:TL1 in out W=0.35 mm L=10 mm H1=0.25 mm H2=0.25 mm T=17 um Er=3.5 TanD=0 Sigma=1e30
            R:RL out 0 R=50
            analysis TR1 type=tran Stop=2n Step=0.01n
            """);
        var slin = Assert.Single(r.Lines);
        Assert.Equal(0, slin.Order);
        Assert.True(slin.Error < 1e-9);
    }

    private const string FileBench = """
        V_Pulse:VS s 0 V1=0 V2=1 Delay=0.1n Rise=0.2n Fall=0.2n Width=0
        R:RS s in R=50
        SnP:F1 in out 0 NumPorts=2 File="{0}"
        R:RL out 0 R=50
        analysis TR1 type=tran Stop={1} Step={2} MaxStep=50p RelTol={3}
        """;

    /// <summary>The transform of the output over the source's incident wave (Vs/2): S21 as the run saw it.</summary>
    private static Complex Transfer(TransientRunResult r, double hz)
    {
        var vs = Node(r, "s"); var vo = Node(r, "out");
        Complex a = 0, b = 0;
        for (int k = 0; k < r.Time.Length; k++)
        {
            var e = Complex.FromPolarCoordinates(1, -2 * Math.PI * hz * r.Time[k]);
            a += 0.5 * vs[k] * e; b += vo[k] * e;
        }
        return b / a;
    }

    /// <summary>
    /// Gate 4 — a committed band-pass .s2p (1 GHz, 20 %, testdata/transient/bandpass_1ghz.py) driven by a 0.4 ns pulse: across the
    /// passband the output's transform equals the file's S21 within the fit's reported error, its DC extrapolation is said; and a
    /// copy whose transmission is 2 % too large is made passive, the change reported.
    /// </summary>
    [Fact]
    public void Gate4_SnP_TransformIsS21_AndANonPassiveFileIsMadePassive()
    {
        string file = TestData("transient", "bandpass_1ghz.s2p");
        var (r, nl) = Run(string.Format(CultureInfo.InvariantCulture, FileBench, file, "60n", "5p", "1e-7"));
        var fit = Assert.Single(r.Fits);
        output.WriteLine($"fit: order {fit.Order}, error {fit.RelativeError:G3}, σmax {fit.SigmaBefore:G8} → {fit.SigmaAfter:G8}, change {fit.Perturbation:G3}");
        Assert.True(fit.RelativeError <= 1e-3);
        Assert.Contains(nl.Notes, n => n.Contains("F1") && n.Contains("DC is extrapolated"));
        var snp = TouchstoneCache.Get(file);
        double d2 = 0, s2 = 0;
        for (int k = 0; k < snp.FrequencyCount; k++)
        {
            double f = snp.Frequencies[k];
            if (f < 0.8e9 || f > 1.2e9) continue;
            var s21 = snp.Matrices[k][1, 0];
            var h = Transfer(r, f);
            d2 += (h - s21).Magnitude * (h - s21).Magnitude; s2 += s21.Magnitude * s21.Magnitude;
        }
        double rel = Math.Sqrt(d2 / s2);
        output.WriteLine($"transform against the file's S21, 0.8-1.2 GHz: {rel:G3} (relative RMS), {r.AcceptedSteps} steps");
        // The fit is within 3e-7 of the file; what is left is the integration's own error, which falls with RelTol (4.1e-3 at
        // 1e-5, 2.3e-4 at 1e-7, 1.1e-4 at 1e-9, measured) — so the bound is the fit's error plus that, not the fit's alone.
        Assert.True(rel <= fit.RelativeError + 5e-4, $"{rel:G3} against the fit's {fit.RelativeError:G3}");

        // the same file with S21 and S12 2 % too large: active, made passive, the change reported
        string active = Path.Combine(Path.GetTempPath(), $"trn4-active-{Guid.NewGuid():N}.s2p");
        try
        {
            File.WriteAllLines(active, File.ReadAllLines(file).Select(l =>
            {
                if (l.StartsWith('!') || l.StartsWith('#')) return l;
                var c = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 3; i <= 6; i++) c[i] = (1.02 * double.Parse(c[i], CultureInfo.InvariantCulture)).ToString("R", CultureInfo.InvariantCulture);
                return string.Join(' ', c);
            }));
            var (ra, nla) = Run(string.Format(CultureInfo.InvariantCulture, FileBench, active, "60n", "5p", "1e-5"));
            var fa = Assert.Single(ra.Fits);
            output.WriteLine($"active copy: σmax {fa.SigmaBefore:G8} → {fa.SigmaAfter:G10}, change {fa.Perturbation:G3}");
            Assert.True(fa.SigmaBefore > 1.015);
            Assert.True(fa.SigmaAfter <= 1 + RationalPassivity.Slack);
            Assert.True(fa.Perturbation > 0);
            Assert.Contains(nla.Notes, n => n.Contains("F1") && n.Contains("made passive"));
        }
        finally { File.Delete(active); }
    }

    /// <summary>
    /// Gate 5 — gate 4's bench with the non-passive copy, made passive, run for 10⁴ of its fit's slowest time constant: the
    /// output rings down and never grows back.
    /// </summary>
    [Fact]
    public void Gate5_TheEnforcedFit_StaysBoundedOver1e4TimeConstants()
    {
        string file = TestData("transient", "bandpass_1ghz.s2p");
        string active = Path.Combine(Path.GetTempPath(), $"trn4-active-{Guid.NewGuid():N}.s2p");
        try
        {
            File.WriteAllLines(active, File.ReadAllLines(file).Select(l =>
            {
                if (l.StartsWith('!') || l.StartsWith('#')) return l;
                var c = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 3; i <= 6; i++) c[i] = (1.02 * double.Parse(c[i], CultureInfo.InvariantCulture)).ToString("R", CultureInfo.InvariantCulture);
                return string.Join(' ', c);
            }));
            var (probe, _) = Run(string.Format(CultureInfo.InvariantCulture, FileBench, active, "1n", "10p", "1e-3"));
            double slow = Assert.Single(probe.Fits).SlowestS;
            double stop = 1e4 * slow;
            output.WriteLine($"slowest time constant {slow:G4} s; running {stop:G4} s");
            var (r, _) = Run(string.Format(CultureInfo.InvariantCulture, FileBench, active, stop.ToString("R", CultureInfo.InvariantCulture),
                                           (stop / 4000).ToString("R", CultureInfo.InvariantCulture), "1e-3"));
            var vo = Node(r, "out");
            double early = vo.Take(vo.Length / 10).Max(Math.Abs);
            double late = vo.Skip(vo.Length / 2).Max(Math.Abs);
            output.WriteLine($"peak in the first tenth {early:G4} V, largest in the second half {late:G4} V, {r.AcceptedSteps} steps");
            Assert.True(early > 1e-3);
            Assert.True(late <= 1e-9 * early, $"late {late:G3} against early {early:G3}");
        }
        finally { File.Delete(active); }
    }

    /// <summary>
    /// R-trn4-6 — a P1Tone (0 dBm, 50 Ω) whose second band presents 10 Ω: its drive is kept and its terminations fitted, so a
    /// 1 mA current injected at 2 GHz sees 50 Ω ∥ 10 Ω while the 1 GHz drive sees 50 Ω, each within 1e-3 of the closed form.
    /// </summary>
    [Theory]
    [InlineData(10.0)]
    [InlineData(50.0)]
    public void ToneSourceHarmonicTerminations_AreRealized(double z2)
    {
        const int N = 400;
        var (r, nl) = Run($$"""
            P1Tone:P1 a 0 Num=1 Pavl=0 Z=50 Z[2]={{z2:R}} Freq=1 GHz
            R:RL a 0 R=50
            I_1Tone:I2 a 0 Freq=2 GHz I=1m
            analysis TR1 type=tran Stop=10n Step={{1e-9 / N:R}} MaxStep=25p RelTol=1e-6
            """);
        foreach (var n in nl.Notes) output.WriteLine(n);
        if (z2 != 50) Assert.Contains(r.Fits, f => f.Instance == "P1" && f.What == "harmonic terminations");
        Assert.DoesNotContain(nl.Components, c => c.InstancePath == "P1.Z");   // put back after the run
        var va = Node(r, "a");
        int start = r.Time.Length - 1 - N;
        Complex Bin(int k)
        {
            Complex x = 0;
            for (int s = 0; s < N; s++) x += va[start + s] * Complex.FromPolarCoordinates(1, -2 * Math.PI * k * s / N);
            return 2 * x / N;
        }
        double v1 = Bin(1).Magnitude, v2 = Bin(2).Magnitude;
        double want1 = Math.Sqrt(8 * 50 * 1e-3) * 0.5, want2 = 1e-3 * (50.0 * z2 / (50 + z2));
        output.WriteLine($"|V(1 GHz)| {v1:G6} V (closed form {want1:G6}), |V(2 GHz)| {v2:G6} V (closed form {want2:G6})");
        Assert.True(Math.Abs(v1 / want1 - 1) < 1e-3, $"{v1} against {want1}");
        Assert.True(Math.Abs(v2 / want2 - 1) < 1e-3, $"{v2} against {want2}");
    }
}
