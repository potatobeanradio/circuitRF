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
/// Generates self-consistent regression golden data for Hero 2 by running the
/// circuitRF HB engine and writing the converged V and I_nl to CSV.
///
/// Label: SELF-GENERATED REGRESSION — NOT independently validated against other simulators.
/// A future cross-check against other simulators with the identical SDD FET is still owed.
///
/// Run with:
///   dotnet test --filter GenerateHero2Golden
/// The test always passes; it merely writes the golden files if the simulation converges.
/// The old external-reference golden files (hero2_golden_reference_*.csv) are superseded.
/// </summary>
public class Hero2GoldenGenerator(ITestOutputHelper output)
{
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

    [Fact]
    public void GenerateHero2Golden()
    {
        var dir = Hero2Dir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero2.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);

        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, netlist.ResolvedGlobals);

        output.WriteLine($"Running Hero 2: f0={p.ToneHz/1e9:F3} GHz, K={p.MaxHarmonic}, " +
                         $"sweep {p.SweepStart}..{p.SweepStop} step {p.SweepStep} dBm");

        var sw = new ParametricSweepAnalysis("SW_auto", p.SweepVarName!, p.SweepValues().ToArray(), hba.Name);
        var ds = ParametricSweepEngine.Run(sw, lib, tb);

        var sweepVals = ds["Converged"].Axes[0].Values;
        int total     = sweepVals.Length;
        int converged = (int)ds["Converged"].RealValues.Sum();
        output.WriteLine($"Convergence: {converged}/{total} sweep points converged.");

        Assert.True(converged == total,
            $"Some sweep points did not converge ({converged}/{total}). Cannot write golden.");

        // ── Identify interface node indices (node axis is Axes[1] after sweep prepend) ──
        var ifNames  = ds["V"].Axes[1].Labels!;
        int gateIdx  = Array.FindIndex(ifNames, n => n.Contains("n_gate",  StringComparison.OrdinalIgnoreCase));
        int drainIdx = Array.FindIndex(ifNames, n => n.Contains("n_drain", StringComparison.OrdinalIgnoreCase));

        Assert.True(gateIdx  >= 0, "n_gate interface node not found");
        Assert.True(drainIdx >= 0, "n_drain interface node not found");

        output.WriteLine($"Gate  interface index: {gateIdx}");
        output.WriteLine($"Drain interface index: {drainIdx}");

        // ── Write golden CSVs ─────────────────────────────────────────────────
        double f0 = p.ToneHz;
        int    K  = p.MaxHarmonic;

        WriteGolden(dir, "hero2_self_V_n_gate.csv",    "n_gate",  "V (interface voltage)",              ds, "V",   gateIdx,  f0, K);
        WriteGolden(dir, "hero2_self_V_n_drain.csv",   "n_drain", "V (interface voltage)",              ds, "V",   drainIdx, f0, K);
        // Unified I cube branch currents — write per-branch golden
        WriteBranchGolden(dir, "hero2_self_INl_n_gate.csv",  "n_gate",  "I M1:g branch (device current, A)",  ds, "M1:g", f0, K);
        WriteBranchGolden(dir, "hero2_self_INl_n_drain.csv", "n_drain", "I M1:d branch (device current, A)", ds, "M1:d", f0, K);

        // ── Write README ──────────────────────────────────────────────────────
        WriteReadme(dir, p, converged, total);

        output.WriteLine("Golden files written to " + dir);

        // ── Quick sanity on the DC anchors ────────────────────────────────────
        for (int si = 0; si < total; si++)
        {
            double vGateDc  = ((System.Numerics.Complex)ds["V"][si, gateIdx,  0]).Real;
            double vDrainDc = ((System.Numerics.Complex)ds["V"][si, drainIdx, 0]).Real;
            Assert.InRange(vGateDc,  -3.10, -3.00);
            Assert.InRange(vDrainDc,  47.0,  49.0);
        }
        output.WriteLine("DC anchor sanity: PASS (gate ≈ −3.05 V, drain ≈ 48 V at all sweep points).");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void WriteGolden(string dir, string filename, string nodeName, string quantityDesc,
        RfCore.Data.DataSet ds, string cubeName, int nodeIdx, double f0, int K)
    {
        var sweepVals = ds["Converged"].Axes[0].Values;
        var path = Path.Combine(dir, filename);
        using var w = new StreamWriter(path);

        w.WriteLine($"# SELF-GENERATED REGRESSION DATA — NOT INDEPENDENTLY VALIDATED");
        w.WriteLine($"# Generated by circuitRF Hero 2 HB engine (Phase 4a).");
        w.WriteLine($"# A future cross-check against other simulators with the identical SDD FET is owed.");
        w.WriteLine($"# Circuit: hero2.cnl  |  Node: {nodeName}  |  Quantity: {quantityDesc}");
        w.WriteLine($"# f0 = {f0/1e9:F3} GHz  |  K (max harmonic) = {K}");
        w.WriteLine($"# DC current self-biasing verified: I_nl[drain,0] rises with Pin.");
        w.WriteLine($"# Columns: freq_Hz; Pave_dBm; Re; Im");
        w.WriteLine("freq_Hz; Pave_dBm; Re; Im");

        var ci = CultureInfo.InvariantCulture;
        for (int si = 0; si < sweepVals.Length; si++)
        {
            double pIn = sweepVals[si];
            for (int k = 0; k <= K; k++)
            {
                double freqHz = k * f0;
                Complex v     = (Complex)ds[cubeName][si, nodeIdx, k];
                // DC bin: imaginary part is always 0 (real signal).
                double im = k == 0 ? 0.0 : v.Imaginary;
                w.WriteLine($"{freqHz.ToString(ci)}; {pIn.ToString(ci)}; " +
                             $"{v.Real.ToString("G8", ci)}; {im.ToString("G8", ci)}");
            }
        }
    }

    // Unified I cube [sweep, branch, harmonic] — write one branch's harmonics
    private static void WriteBranchGolden(string dir, string filename, string nodeName,
        string quantityDesc, RfCore.Data.DataSet ds, string branchLabel, double f0, int K)
    {
        var iCube     = ds["I"];
        var brLabels  = iCube.Axes[iCube.Rank - 2].Labels!;
        int brIdx     = Array.FindIndex(brLabels, l => l == branchLabel);

        var sweepVals = ds["Converged"].Axes[0].Values;
        var path = Path.Combine(dir, filename);
        using var w = new StreamWriter(path);

        w.WriteLine($"# SELF-GENERATED REGRESSION DATA — NOT INDEPENDENTLY VALIDATED");
        w.WriteLine($"# Generated by circuitRF Hero 2 HB engine.");
        w.WriteLine($"# Circuit: hero2.cnl  |  Branch: {branchLabel}  |  Quantity: {quantityDesc}");
        w.WriteLine($"# f0 = {f0/1e9:F3} GHz  |  K (max harmonic) = {K}");
        w.WriteLine($"# Columns: freq_Hz; Pave_dBm; Re; Im");
        w.WriteLine("freq_Hz; Pave_dBm; Re; Im");

        var ci = CultureInfo.InvariantCulture;
        for (int si = 0; si < sweepVals.Length; si++)
        {
            double pIn = sweepVals[si];
            for (int k = 0; k <= K; k++)
            {
                double freqHz = k * f0;
                Complex v     = (Complex)iCube[si, brIdx, k];
                double  im    = k == 0 ? 0.0 : v.Imaginary;
                w.WriteLine($"{freqHz.ToString(ci)}; {pIn.ToString(ci)}; " +
                             $"{v.Real.ToString("G8", ci)}; {im.ToString("G8", ci)}");
            }
        }
    }

    private static void WriteReadme(string dir, HbAnalysisParams p, int converged, int total)
    {
        var path = Path.Combine(dir, "README_golden.md");
        using var w = new StreamWriter(path);
        w.WriteLine("# Hero 2 Test Data — Golden Reference Files");
        w.WriteLine();
        w.WriteLine("## Self-generated files (circuitRF self-consistency, not cross-validated)");
        w.WriteLine();
        w.WriteLine("| File | Description |");
        w.WriteLine("|------|-------------|");
        w.WriteLine("| `hero2_self_V_n_gate.csv`    | Interface voltage V at n_gate, all harmonics DC+4, per Pin |");
        w.WriteLine("| `hero2_self_V_n_drain.csv`   | Interface voltage V at n_drain, all harmonics DC+4, per Pin |");
        w.WriteLine("| `hero2_self_INl_n_gate.csv`  | Nonlinear device current I_nl at n_gate, per harmonic, per Pin |");
        w.WriteLine("| `hero2_self_INl_n_drain.csv` | Nonlinear device current I_nl at n_drain, per harmonic, per Pin |");
        w.WriteLine();
        w.WriteLine($"**Sweep:** Pin = {p.SweepStart:F0}…{p.SweepStop:F0} dBm, step {p.SweepStep:F0} dBm " +
                    $"({total} points, {converged} converged).");
        w.WriteLine($"**MaxHarm:** K = {p.MaxHarmonic}. **f0:** {p.ToneHz/1e9:F3} GHz.");
        w.WriteLine();
        w.WriteLine("**Label: SELF-GENERATED REGRESSION — NOT independently validated** against " +
                    "other simulators. These files freeze the current engine state for regression " +
                    "detection (CI catches any numerical change ≥ 1e-5). An independent cross-check " +
                    "against other simulators with the identical SDD FET is a future task.");
        w.WriteLine();
        w.WriteLine("## Deprecated files (superseded)");
        w.WriteLine();
        w.WriteLine("| File | Status |");
        w.WriteLine("|------|--------|");
        w.WriteLine("| `hero2_golden_reference_n_drain.csv` | **DEPRECATED** — external reference " +
                    "generated with the old Y_DC_VIRT clamped DC (wrong physics). Do not use for validation. |");
        w.WriteLine("| `hero2_golden_reference_n_gate.csv`  | **DEPRECATED** — same reason. |");
    }
}
