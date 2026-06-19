using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// CI regression test for Hero 2 (single-tone GaN HEMT PA, 2 GHz).
/// Compares V and I_nl against the self-generated golden files in testdata/Hero2/.
///
/// Noise-floor rule (from the brief): components with |value| &lt; 1e-5 (Re or Im) are
/// numerical noise and pass by default — only signal-bearing bins are validated.
///
/// Also asserts physics anchors:
///   - DC gate ≈ −3.05 V, DC drain ≈ 48 V at every sweep point.
///   - Gate harmonics k≥1 ≈ 0 (linear gate, no significant harmonic generation).
///   - DC drain current I_nl[drain,0] rises monotonically with Pin (self-biasing).
/// </summary>
public class Hero2RegressionTests(ITestOutputHelper output)
{
    /// <summary>Noise floor: values below this magnitude are numerical noise — skip comparison.</summary>
    private const double NoiseFloor = 1e-5;

    private static string Hero2Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero2");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero2 not found");
    }

    // ── Golden CSV loader ────────────────────────────────────────────────────

    private record GoldenEntry(double FreqHz, double Pave_dBm, double Re, double Im);

    private static List<GoldenEntry> LoadGolden(string path)
    {
        var entries = new List<GoldenEntry>();
        var ci = CultureInfo.InvariantCulture;
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.StartsWith('#') || line.StartsWith("freq")) continue;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(';');
            if (parts.Length < 4) continue;
            entries.Add(new GoldenEntry(
                double.Parse(parts[0].Trim(), ci),
                double.Parse(parts[1].Trim(), ci),
                double.Parse(parts[2].Trim(), ci),
                double.Parse(parts[3].Trim(), ci)));
        }
        return entries;
    }

    // ── Regression gate ─────────────────────────────────────────────────────

    [Fact]
    public void Hero2_V_MatchesSelfGeneratedGolden()
    {
        var dir = Hero2Dir();
        var drainGolden = LoadGolden(Path.Combine(dir, "hero2_self_V_n_drain.csv"));
        var gateGolden  = LoadGolden(Path.Combine(dir, "hero2_self_V_n_gate.csv"));

        Assert.NotEmpty(drainGolden);
        Assert.NotEmpty(gateGolden);

        RunAndCompare(dir, drainGolden, gateGolden, null, null, "V", compareV: true);
    }

    [Fact]
    public void Hero2_INl_MatchesSelfGeneratedGolden()
    {
        var dir = Hero2Dir();
        var drainGolden = LoadGolden(Path.Combine(dir, "hero2_self_INl_n_drain.csv"));
        var gateGolden  = LoadGolden(Path.Combine(dir, "hero2_self_INl_n_gate.csv"));

        Assert.NotEmpty(drainGolden);
        Assert.NotEmpty(gateGolden);

        RunAndCompare(dir, null, null, drainGolden, gateGolden, "INl", compareV: false);
    }

    [Fact]
    public void Hero2_PhysicsAnchors()
    {
        var dir = Hero2Dir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero2.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);

        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, netlist.ResolvedGlobals);
        var sw  = new ParametricSweepAnalysis("SW_auto", p.SweepVarName!, p.SweepValues().ToArray(), hba.Name);
        var ds  = ParametricSweepEngine.Run(sw, lib, tb);

        string[] ifNames = ds["V"].Axes[1].Labels!;
        int gateIdx  = Array.FindIndex(ifNames, n => n.Contains("n_gate",  StringComparison.OrdinalIgnoreCase));
        int drainIdx = Array.FindIndex(ifNames, n => n.Contains("n_drain", StringComparison.OrdinalIgnoreCase));

        Assert.True(gateIdx  >= 0, "n_gate interface node not found");
        Assert.True(drainIdx >= 0, "n_drain interface node not found");

        var sweepVals = ds["Converged"].Axes[0].Values;

        // Locate drain branch in unified I cube [sweep, branch, harmonic].
        var iCube     = ds["I"];
        var iLabels   = iCube.Axes[iCube.Rank - 2].Labels!;
        int drainBrIdx = Array.FindIndex(iLabels, l => l == "M1:d" || l == "M1:1");
        Assert.True(drainBrIdx >= 0, "Unified I cube missing M1 drain branch");

        // ── All sweep points converged ────────────────────────────────────────
        int nonConv = ds["Converged"].RealValues.Count(v => v < 0.5);
        Assert.True(nonConv == 0, $"{nonConv} sweep points did not converge.");

        output.WriteLine($"All {sweepVals.Length} sweep points converged.");

        // ── DC voltage anchors (held by bias network via regularization) ──────
        for (int si = 0; si < sweepVals.Length; si++)
        {
            double pin    = sweepVals[si];
            double vGate  = ((Complex)ds["V"][si, gateIdx,  0]).Real;
            double vDrain = ((Complex)ds["V"][si, drainIdx, 0]).Real;
            Assert.InRange(vGate,  -3.10, -3.00); // ≈ −3.05 V
            Assert.InRange(vDrain,  47.0,  49.0); // ≈  48 V
            output.WriteLine($"Pin={pin,5:F1} dBm:  V_gate={vGate:F4} V  V_drain={vDrain:F4} V  " +
                             $"I_nl_drain={((Complex)iCube[si, drainBrIdx, 0]).Real*1e3:F2} mA");
        }

        // ── Gate harmonics k≥2 are near zero (linear gate, no harmonic generation) ──
        // k=1 is the drive fundamental at the gate node — physically nonzero and increasing
        // with Pin. Only k≥2 harmonics should be negligible for this linear-gate topology.
        int K = p.MaxHarmonic;
        for (int si = 0; si < sweepVals.Length; si++)
        for (int k = 2; k <= K; k++)
        {
            double vGateKMag = ((Complex)ds["V"][si, gateIdx, k]).Magnitude;
            // Gate harmonics k≥2 should be tiny — loose bound (0.1 V) catches gross errors.
            Assert.True(vGateKMag < 0.1,
                $"Gate harmonic k={k} at Pin={sweepVals[si]:F1} dBm: " +
                $"|V_gate[k={k}]| = {vGateKMag:E3} V ≥ 0.1 V (unexpectedly large).");
        }
        output.WriteLine($"Gate harmonic check (k=2..{K} < 0.1 V): PASS.");

        // ── DC drain current rises with Pin (self-biasing) ────────────────────
        double prevI = double.MinValue;
        for (int si = 0; si < sweepVals.Length; si++)
        {
            double iDrain = ((Complex)iCube[si, drainBrIdx, 0]).Real;
            Assert.True(iDrain >= prevI - 0.1e-3,  // allow 0.1 mA noise
                $"DC drain current did not increase at Pin={sweepVals[si]:F1} dBm: " +
                $"I_nl[drain,0]={iDrain*1e3:F2} mA, prev={prevI*1e3:F2} mA.");
            prevI = iDrain;
        }
        output.WriteLine($"Self-biasing check: I_nl[drain,0] rises with Pin. PASS.");
    }

    // ── Compare helper (shared by V and INl tests) ───────────────────────────

    private void RunAndCompare(string dir,
        List<GoldenEntry>? drainGoldenV, List<GoldenEntry>? gateGoldenV,
        List<GoldenEntry>? drainGoldenI, List<GoldenEntry>? gateGoldenI,
        string quantityLabel, bool compareV)
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero2.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);

        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, netlist.ResolvedGlobals);
        var sw  = new ParametricSweepAnalysis("SW_auto", p.SweepVarName!, p.SweepValues().ToArray(), hba.Name);
        var ds  = ParametricSweepEngine.Run(sw, lib, tb);

        string[] ifNames = ds["V"].Axes[1].Labels!;
        int gateIdx  = Array.FindIndex(ifNames, n => n.Contains("n_gate",  StringComparison.OrdinalIgnoreCase));
        int drainIdx = Array.FindIndex(ifNames, n => n.Contains("n_drain", StringComparison.OrdinalIgnoreCase));

        Assert.True(gateIdx  >= 0, "n_gate not in interface");
        Assert.True(drainIdx >= 0, "n_drain not in interface");

        var drainGolden = compareV ? drainGoldenV! : drainGoldenI!;
        var gateGolden  = compareV ? gateGoldenV!  : gateGoldenI!;

        // Locate drain and gate branches in unified I cube [sweep, branch, harmonic].
        var iCube = ds["I"];
        var iBrLabels = iCube.Axes[iCube.Rank - 2].Labels!;
        int iDrainBrIdx = Array.FindIndex(iBrLabels, l => l == "M1:d" || l == "M1:1");
        int iGateBrIdx  = Array.FindIndex(iBrLabels, l => l == "M1:g" || l == "M1:0");

        var sweepVals = ds["Converged"].Axes[0].Values;
        double f0 = p.ToneHz;
        int    K  = p.MaxHarmonic;
        int nChecked = 0;
        int nFail    = 0;
        var failMsgs = new List<string>();

        for (int si = 0; si < sweepVals.Length; si++)
        {
            double pin = sweepVals[si];
            for (int k = 0; k <= K; k++)
            {
                double freqHz = k * f0;

                foreach (var (nodeLabel, nodeIdx, branchIdx, golden) in new[]
                {
                    // V path uses node-indexed cube; I path uses unified I cube with branch index.
                    ("drain", drainIdx, iDrainBrIdx, drainGolden),
                    ("gate",  gateIdx,  iGateBrIdx,  gateGolden),
                })
                {
                    var entry = golden.FirstOrDefault(e =>
                        Math.Abs(e.Pave_dBm - pin) < 0.05 &&
                        Math.Abs(e.FreqHz - freqHz) < 1e6);

                    if (entry is null) continue;

                    Complex sim = compareV
                        ? (Complex)ds["V"][si, nodeIdx,  k]
                        : (Complex)iCube   [si, branchIdx, k];
                    double simRe = sim.Real;
                    double simIm = k == 0 ? 0.0 : sim.Imaginary;

                    // Apply noise-floor rule: skip comparison for near-zero components.
                    bool checkRe = Math.Abs(entry.Re) >= NoiseFloor;
                    bool checkIm = Math.Abs(entry.Im) >= NoiseFloor;

                    if (!checkRe && !checkIm) continue;  // both golden components are noise

                    nChecked++;

                    double reDiff = checkRe ? Math.Abs(simRe - entry.Re) : 0;
                    double imDiff = checkIm ? Math.Abs(simIm - entry.Im) : 0;

                    // Tolerance: match to 1e-4 relative or 1e-6 absolute on each component.
                    double reTol = checkRe ? Math.Max(1e-6, Math.Abs(entry.Re) * 1e-4) : 0;
                    double imTol = checkIm ? Math.Max(1e-6, Math.Abs(entry.Im) * 1e-4) : 0;

                    bool pass = reDiff <= reTol && imDiff <= imTol;
                    if (!pass)
                    {
                        nFail++;
                        failMsgs.Add(
                            $"  {nodeLabel} k={k} Pin={pin:F1}: " +
                            $"sim=({simRe:G6},{simIm:G6}) golden=({entry.Re:G6},{entry.Im:G6}) " +
                            $"reDiff={reDiff:E2} imDiff={imDiff:E2} " +
                            $"(tols: {reTol:E1},{imTol:E1})");
                    }
                    else
                    {
                        output.WriteLine(
                            $"  {nodeLabel} k={k} Pin={pin:F1}: OK " +
                            $"sim=({simRe:G6},{simIm:G6}) golden=({entry.Re:G6},{entry.Im:G6}) " +
                            $"Δ=({reDiff:E2},{imDiff:E2})");
                    }
                }
            }
        }

        output.WriteLine($"\n[{quantityLabel}] Checked {nChecked} signal-bearing bins. {nFail} failures.");

        if (nFail > 0)
        {
            output.WriteLine("FAILURES:");
            foreach (var msg in failMsgs) output.WriteLine(msg);
            Assert.Fail($"{nFail}/{nChecked} signal-bearing {quantityLabel} bins exceeded tolerance. " +
                        $"See output for details.");
        }

        Assert.True(nChecked > 0,
            $"No signal-bearing {quantityLabel} bins found — check interface node mapping.");
    }

}
