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
/// Generates the self-consistent two-tone regression golden for Hero 5 by running the circuitRF HB
/// engine and writing the converged V and I_nl over the mixIndex axis to CSV.
///
/// Label: SELF-GENERATED REGRESSION — NOT independently validated against other simulators.
/// Run with:  dotnet test --filter GenerateHero5Golden
/// The test passes if every swept point converges, then writes the golden files.
/// </summary>
public class Hero5GoldenGenerator(ITestOutputHelper output)
{
    // Bounded golden sweep (the cnl sweeps further; two-tone is stiffer at high drive — keep the
    // regression in the well-converged low/mid-drive range; the owner can extend after a convergence
    // study). Generator and regression MUST use the same range.
    public const double GoldenStart = -20, GoldenStop = -8, GoldenStep = 4;

    internal static string Hero5Dir()
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

    [Fact]
    public void GenerateHero5Golden()
    {
        var dir       = Hero5Dir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero5.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);
        var hba       = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p         = HbEngine.Resolve(hba, netlist.ResolvedGlobals)
                        with { SweepStart = GoldenStart, SweepStop = GoldenStop, SweepStep = GoldenStep };

        output.WriteLine($"Hero 5: f1={p.ToneFreqsHz[0]/1e9:F4} f2={p.ToneFreqsHz[1]/1e9:F4} GHz, " +
                         $"MaxMixOrder={p.MaxMixOrder}, sweep {p.SweepStart}..{p.SweepStop} step {p.SweepStep} dBm");

        var sw = new ParametricSweepAnalysis("SW_auto", p.SweepVarName!, p.SweepValues().ToArray(), hba.Name);
        var ds = ParametricSweepEngine.Run(sw, lib, tb);
        int maxOrder = (int)Math.Round(ds["MetaMixOrder"].RealValues[0]);
        var grid     = new MixingGrid(maxOrder);

        int converged = (int)ds["Converged"].RealValues.Sum();
        int total     = ds["Converged"].Axes[0].Values.Length;
        output.WriteLine($"Convergence: {converged}/{total} sweep points converged.");
        Assert.True(converged == total,
            $"Some sweep points did not converge ({converged}/{total}). Cannot write golden.");

        string[] names = ds["V"].Axes[1].Labels!;
        int gateIdx  = Array.FindIndex(names, n => n.Contains("n_gate",  StringComparison.OrdinalIgnoreCase));
        int drainIdx = Array.FindIndex(names, n => n.Contains("n_drain", StringComparison.OrdinalIgnoreCase));
        Assert.True(gateIdx >= 0 && drainIdx >= 0);

        WriteGoldenV(dir, "hero5_self_V_n_gate.csv",   "n_gate",  "V (interface voltage)",    ds, grid, gateIdx);
        WriteGoldenV(dir, "hero5_self_V_n_drain.csv",  "n_drain", "V (interface voltage)",    ds, grid, drainIdx);
        WriteGoldenI(dir, "hero5_self_INl_n_gate.csv", "n_gate",  "I M1:g branch (device current, A)", ds, grid, "M1:g");
        WriteGoldenI(dir, "hero5_self_INl_n_drain.csv","n_drain", "I M1:d branch (device current, A)", ds, grid, "M1:d");

        output.WriteLine("Golden files written to " + dir);
    }

    // Write V golden (node-indexed cube, axes [sweep, node, mixIndex])
    private static void WriteGoldenV(string dir, string filename, string nodeName, string quantity,
        DataSet ds, MixingGrid grid, int nodeIdx)
    {
        WriteGoldenCore(dir, filename, nodeName, quantity, ds, grid,
            (m, si) => (Complex)ds["V"][si, nodeIdx, m]);
    }

    // Write I: branch-current golden (unified I cube [sweep, branch, mixIndex])
    private static void WriteGoldenI(string dir, string filename, string nodeName, string quantity,
        DataSet ds, MixingGrid grid, string branchLabel)
    {
        var iCube  = ds["I"];
        var labels = iCube.Axes[iCube.Rank - 2].Labels!;
        int brIdx  = Array.FindIndex(labels, l => l == branchLabel);
        WriteGoldenCore(dir, filename, nodeName, quantity, ds, grid,
            (m, si) => (Complex)iCube[si, brIdx, m]);
    }

    private static void WriteGoldenCore(string dir, string filename, string nodeName, string quantity,
        DataSet ds, MixingGrid grid, Func<int, int, Complex> getValue)
    {
        var path = Path.Combine(dir, filename);
        using var w = GoldenRegen.OpenWriter(path);   // no-op unless CIRCUITRF_REGENERATE_GOLDENS
        var tf = ds["ToneFreqs"].RealValues;
        double f1 = tf[0], f2 = tf[1];

        w.WriteLine("# SELF-GENERATED REGRESSION DATA — NOT INDEPENDENTLY VALIDATED");
        w.WriteLine("# Generated by circuitRF two-tone HB engine (Phase 4c).");
        w.WriteLine("# A future cross-check against other simulators with the identical SDD FET is owed.");
        w.WriteLine($"# Circuit: hero5.cnl  |  Node: {nodeName}  |  Quantity: {quantity}");
        w.WriteLine($"# f1 = {f1/1e9:F4} GHz, f2 = {f2/1e9:F4} GHz  |  MaxMixOrder = {grid.MaxMixOrder}");
        w.WriteLine("# Columns: k1; k2; freq_Hz; Pave_dBm; Re; Im");
        w.WriteLine("k1; k2; freq_Hz; Pave_dBm; Re; Im");

        var sweepVals = ds["Converged"].Axes[0].Values;
        var ci = CultureInfo.InvariantCulture;
        for (int si = 0; si < sweepVals.Length; si++)
        {
            double pIn = sweepVals[si];
            for (int m = 0; m < grid.MixCount; m++)
            {
                var (k1, k2) = grid.ToneOf(m);
                double freqHz = k1 * f1 + k2 * f2;
                Complex v = getValue(m, si);
                double im = m == 0 ? 0.0 : v.Imaginary;   // (0,0) DC is real
                w.WriteLine($"{k1.ToString(ci)}; {k2.ToString(ci)}; {freqHz.ToString(ci)}; " +
                            $"{pIn.ToString(ci)}; {v.Real.ToString("G8", ci)}; {im.ToString("G8", ci)}");
            }
        }
    }
}
