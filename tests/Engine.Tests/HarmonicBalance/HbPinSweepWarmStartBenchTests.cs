using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Benchmark + correctness for warm-starting an HB Pin sweep from the previous converged spectrum
/// instead of cold-seeding each point from a fresh nonlinear-DC solve (the question raised about the
/// production ParametricSweepEngine path, which currently cold-starts every point).
///
/// Both paths re-elaborate per Pin (Pin is a global the P1Tone reads). The COLD path calls
/// <c>RunSinglePoint(warmStart: null)</c> → a full NonlinearDcEngine solve + zero-harmonic seed each
/// point (exactly what <c>HbEngine.Run</c> does in the sweep). The WARM path threads the previous
/// point's converged interface V as the seed → NO per-point DC solve, harmonics seeded from the real
/// neighbour. Asserts: (a) identical converged result (same physical root), (b) warm uses ≤ the Newton
/// iterations of cold and only ONE DC solve for the whole sweep.
/// </summary>
public class HbPinSweepWarmStartBenchTests(ITestOutputHelper output)
{
    // The user's reported netlist (HB1 only; the Pin sweep is driven manually below).
    private const string Netlist = @"
define MyFET (gate drain)
  parameters Periphery_mm=1
  Sv = -0.837
  Sc = 0.71
  TV0 = 4.268
  TC = 1.507
  th = 0.001
  a = 0.176
  g = 0.089
  lam = 0.0012
  B = 1130
  SDD:X1  gate  0  drain  0  I[1,0]=_v1/50  I[2,0]=Periphery_mm*(B*TC*tanh(_v2*a*(tanh(g*(TV0 - _v1 + _v2*th + Sc*ln(exp(-(Sv - _v1)/Sc) + 1)))+1))*ln(exp(-(2*TV0 - 2*_v1 +2*_v2*th + 2*Sc*ln(exp(-(Sv - _v1)/Sc) + 1))/TC) + 1) * (_v2*lam + 1))/2
end MyFET

Pin = 0
RFfreq = 2 GHz
VDD = 48

C:C1  Vin  n1  C=1 mF
L:L1  n2  n3  L=1 mH
L:L2  n4  Vout  L=1 mH
R:R2  n5  0  R=80 Ohm
C:C2  n5  n6  C=1 mF
P1Tone:P1  n1  0  Pavl=Pin dBm  Z=50 Ohm  Freq=RFfreq  Phase=0 deg  Z[0]=1 Ohm  Z[2]=30 Ohm
Vdc:V1  n2  0  Vdc=-3.05 V
Vdc:V2  VDD  0  Vdc=VDD V
MyFET:X1  n3  Vout  Periphery_mm=1
IProbe:Iout  Vout  n6
IProbe:Iin  Vin  n3
IProbe:IDC  VDD  n4
C:C3  Vout  0  C=0.3 pF

analysis HB1 type=hb Tone=""RFfreq"" ToneUnit=GHz MaxHarm=5 FFTOverSample=1 Tol=1e-6 DriveStepping=IfNecessary GuardHarmonic=0 Lambda=1 MaxIter=100
";

    [Fact]
    public void PinSweep_WarmStart_MatchesCold_AndUsesFewerSolves()
    {
        string path = Path.Combine(Path.GetTempPath(), $"warmstart_{Guid.NewGuid():N}.cnl");
        File.WriteAllText(path, Netlist);
        Library lib; TestBench tb;
        try { (lib, tb) = CnlReader.ReadFile(path); }
        finally { File.Delete(path); }

        var hba    = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        int pinIdx = tb.GlobalVariables.FindIndex(v => v.Name == "Pin");
        Assert.True(pinIdx >= 0, "Pin global not found");

        // Representative subrange of the user's 0..30 dBm sweep (kept converging + quick).
        double[] pins = Enumerable.Range(0, 11).Select(i => (double)(i * 2)).ToArray();   // 0..20 dBm
        var settings = new AnalysisSettings();   // defaults; HbConsoleDiagnostics = false (quiet)

        var cold = RunSweep(lib, tb, hba, pinIdx, pins, settings, warm: false);
        var warm = RunSweep(lib, tb, hba, pinIdx, pins, settings, warm: true);

        // (a) Same physical root at every point.
        double worst = 0;
        for (int i = 0; i < pins.Length; i++)
            worst = Math.Max(worst, MaxAbsDiff(cold.V[i], warm.V[i]));
        Assert.True(worst < 1e-3, $"cold/warm interface V diverge (max |Δ|={worst:E3} V) — different root?");

        output.WriteLine($"HB Pin sweep, {pins.Length} points ({pins[0]}..{pins[^1]} dBm):");
        output.WriteLine($"  COLD (DC-seed each point):   {cold.Iters,4} Newton iters, {cold.DcSolves} DC solves");
        output.WriteLine($"  WARM (previous-point seed):  {warm.Iters,4} Newton iters, {warm.DcSolves} DC solves");
        output.WriteLine($"  Newton iters {cold.Iters}→{warm.Iters} " +
                         $"({100.0 * (cold.Iters - warm.Iters) / cold.Iters:F0}% fewer); " +
                         $"DC solves {cold.DcSolves}→{warm.DcSolves}; max |ΔV|={worst:E2} V");

        // (b) Warm-start does no worse on Newton iterations and skips all but the first DC solve.
        Assert.True(warm.Iters <= cold.Iters,
            $"warm Newton iters ({warm.Iters}) should be ≤ cold ({cold.Iters})");
        Assert.Equal(pins.Length, cold.DcSolves);   // cold: a DC solve every point
        Assert.Equal(1,           warm.DcSolves);    // warm: one DC solve for the whole sweep
    }

    private readonly record struct SweepStats(int Iters, int DcSolves, Complex[][,] V);

    private static SweepStats RunSweep(
        Library lib, TestBench tb, HarmonicBalanceAnalysis hba, int pinIdx,
        double[] pins, AnalysisSettings settings, bool warm)
    {
        var orig = tb.GlobalVariables[pinIdx];
        int iters = 0, dcSolves = 0;
        var perPoint = new Complex[pins.Length][,];
        Complex[,]? prevV = null;
        try
        {
            for (int i = 0; i < pins.Length; i++)
            {
                tb.GlobalVariables[pinIdx] =
                    new Variable("Pin", pins[i].ToString("G17", CultureInfo.InvariantCulture), null);

                var netlist = new Elaborator(lib).Elaborate(tb);
                var p       = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);
                var eng     = new HbEngine(netlist, tb, settings);

                Complex[,]? seed = warm ? prevV : null;
                if (seed is null) dcSolves++;   // RunSinglePoint runs NonlinearDcEngine only when unseeded

                var sp = eng.RunSinglePoint(p, seed);
                Assert.True(sp.Converged, $"{(warm ? "warm" : "cold")} Pin={pins[i]} dBm did not converge");

                iters       += sp.Iterations;
                perPoint[i]  = sp.V;
                prevV        = sp.V;
            }
        }
        finally { tb.GlobalVariables[pinIdx] = orig; }
        return new SweepStats(iters, dcSolves, perPoint);
    }

    private static double MaxAbsDiff(Complex[,] a, Complex[,] b)
    {
        double m = 0;
        int n0 = a.GetLength(0), n1 = a.GetLength(1);
        for (int n = 0; n < n0; n++)
            for (int k = 0; k < n1; k++)
                m = Math.Max(m, (a[n, k] - b[n, k]).Magnitude);
        return m;
    }

    // The same netlist plus the Pin sweep directive, exercising the PRODUCTION path
    // (ParametricSweepEngine.Run) — which now warm-starts by default (HbSweepWarmStart).
    private const string NetlistWithSweep = Netlist + @"
analysis HB1_sweep_Pin type=parametric_sweep Var=Pin Start=0 Stop=10 Step=2 Inner=HB1
";

    [Fact]
    public void ProductionSweep_WarmStartDefault_MatchesColdStart()
    {
        string path = Path.Combine(Path.GetTempPath(), $"warmstart_prod_{Guid.NewGuid():N}.cnl");
        File.WriteAllText(path, NetlistWithSweep);
        Library lib; TestBench tb;
        try { (lib, tb) = CnlReader.ReadFile(path); }
        finally { File.Delete(path); }

        var sweep = tb.Analyses.OfType<ParametricSweepAnalysis>().First();

        // Default settings → warm-start ON; explicit OFF → cold DC seed each point.
        var warm = (RfCore.Data.DataSet)ParametricSweepEngine.Run(sweep, lib, tb, new AnalysisSettings());
        var cold = (RfCore.Data.DataSet)ParametricSweepEngine.Run(
            sweep, lib, tb, new AnalysisSettings { HbSweepWarmStart = false });

        // The wired production path must converge to the SAME physical root either way. Warm and cold
        // seeds stop at slightly different within-tolerance iterates (HB Tol=1e-6 on ‖F‖), so the V
        // cube — which includes volt-scale back-solved nodes — agrees to convergence-tolerance noise
        // (~1e-5 V here, ~1e-6 relative), NOT bit-identity. A different root would diverge by volts.
        var vW = warm["V"].ComplexValues;
        var vC = cold["V"].ComplexValues;
        Assert.Equal(vC.Length, vW.Length);
        double worst = 0;
        for (int i = 0; i < vC.Length; i++)
            worst = Math.Max(worst, (vW[i] - vC[i]).Magnitude);
        output.WriteLine($"Production sweep warm-vs-cold V cube: max |Δ| = {worst:E3} V");
        Assert.True(worst < 1e-3, $"warm/cold V cube diverge (max |Δ|={worst:E3} V) — different root?");
    }

    // ── Multi-tone (HB-P3 M3) ────────────────────────────────────────────────
    //
    // The two-tone and n-tone paths used to build their initial guess from NonlinearDcEngine.Run +
    // InitialGuess2D on EVERY call — RunTwoTone took no seed and RunMultiTone took no seed, so a
    // swept two-tone analysis cold-started every point while the single-tone one beside it had been
    // warm-starting since §11.1 was built. They now take the same seed, of their own [N, M] shape.

    private static string Hero5Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero5");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero5 not found");
    }

    private readonly record struct MultiToneSweep(int DcSolves, int Iterations, Complex[][,] V);

    /// <summary>Walks Pavl_dbm through a multi-tone HB analysis, warm-chaining or not.</summary>
    private static MultiToneSweep RunMultiToneSweep(string cnlFile, double[] levels, bool warm,
        int? maxIterAtIndex = null, int maxIter = 1)
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(Hero5Dir(), cnlFile));
        var hba  = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        int idx  = tb.GlobalVariables.FindIndex(v => v.Name == "Pavl_dbm");
        Assert.True(idx >= 0, "Pavl_dbm global not found");
        var orig = tb.GlobalVariables[idx];

        var perPoint = new Complex[levels.Length][,];
        Complex[,]? seed = null;
        int iterations = 0;

        NonlinearDcEngine.ResetRuns();
        try
        {
            for (int i = 0; i < levels.Length; i++)
            {
                tb.GlobalVariables[idx] = new Variable(
                    "Pavl_dbm", levels[i].ToString("G17", CultureInfo.InvariantCulture), orig.Unit);

                using var netlist = new Elaborator(lib).Elaborate(tb);
                var p = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);
                // DriveStepping off: this measures the WARM START, and a ramp firing on a starved
                // point would add DC solves and iterations that belong to a different mechanism.
                p = p with { DriveStepping = DcBiasSteppingMode.Never };
                if (maxIterAtIndex == i) p = p with { MaxIter = maxIter };

                var rr = new HbEngine(netlist, tb).Run(p, warm ? seed : null);
                iterations += rr.Trace!.Steps.Sum(st => st.Iterations);
                perPoint[i] = rr.InterfaceV!;
                seed = rr.Converged ? rr.InterfaceV : null;   // §11.1: reset the chain on a failure
            }
        }
        finally { tb.GlobalVariables[idx] = orig; }

        return new MultiToneSweep(NonlinearDcEngine.Runs, iterations, perPoint);
    }

    /// <summary>
    /// Warm vs cold on a two-tone and a three-tone Pin sweep: one DC seed for the whole sweep instead
    /// of one per point, fewer Newton iterations, and the same interface spectrum at every point.
    /// </summary>
    [Theory]
    [InlineData("hero5.cnl")]
    [InlineData("hero5_3tone.cnl")]
    public void MultiTonePinSweep_WarmStart_MatchesCold_AndUsesFewerSolves(string cnlFile)
    {
        // Deliberately a COMPRESSED span. Below compression a multi-tone point converges in three
        // Newton iterations from either seed, so a small-signal sweep shows the DC-solve saving and
        // nothing else — there is no iteration headroom there to save.
        double[] levels = [8, 10, 12, 14, 16, 18, 20, 22];

        var cold = RunMultiToneSweep(cnlFile, levels, warm: false);
        var warm = RunMultiToneSweep(cnlFile, levels, warm: true);

        double worst = 0, scale = 1.0;
        for (int i = 0; i < levels.Length; i++)
        {
            worst = Math.Max(worst, MaxAbsDiff(cold.V[i], warm.V[i]));
            for (int n = 0; n < cold.V[i].GetLength(0); n++)
                for (int m = 0; m < cold.V[i].GetLength(1); m++)
                    scale = Math.Max(scale, cold.V[i][n, m].Magnitude);
        }

        output.WriteLine($"{cnlFile}, {levels.Length} points ({levels[0]}..{levels[^1]} dBm):");
        output.WriteLine($"  COLD: {cold.Iterations,4} Newton iters, {cold.DcSolves} DC solves");
        output.WriteLine($"  WARM: {warm.Iterations,4} Newton iters, {warm.DcSolves} DC solves");
        output.WriteLine($"  max |ΔV| = {worst:E3} V ({worst / scale:E2} relative)");

        Assert.Equal(levels.Length, cold.DcSolves);   // cold: a DC seed every point
        Assert.Equal(1,             warm.DcSolves);   // warm: one for the whole sweep
        Assert.True(warm.Iterations < cold.Iterations,
            $"warm Newton iters ({warm.Iterations}) should be < cold ({cold.Iterations})");
        // Relative, for the reason the single-tone production-sweep test above gives: warm and cold
        // stop at different within-tolerance iterates, so they agree to convergence noise.
        Assert.True(worst / scale < 1e-6,
            $"warm and cold reach different spectra (max |Δ|={worst:E3} V, {worst / scale:E2} relative)");
    }

    /// <summary>
    /// The chain still resets on a non-converged point at every tone count — §11.1's rule, and the
    /// belt to the line search's braces. Starving ONE point (MaxIter=1) must make the NEXT point
    /// DC-seed itself rather than inherit a half-solved spectrum.
    /// </summary>
    [Fact]
    public void MultiToneChain_ResetsAfterANonConvergedPoint()
    {
        double[] levels = [-20, -18, -16, -14];

        var clean   = RunMultiToneSweep("hero5.cnl", levels, warm: true);
        var starved = RunMultiToneSweep("hero5.cnl", levels, warm: true, maxIterAtIndex: 1, maxIter: 1);

        output.WriteLine($"clean  : {clean.DcSolves} DC solves");
        output.WriteLine($"starved: {starved.DcSolves} DC solves (point 1 capped at MaxIter=1)");

        Assert.Equal(1, clean.DcSolves);
        // The first point seeds from DC; point 1 fails; point 2 must seed from DC again.
        Assert.Equal(2, starved.DcSolves);
    }
}
