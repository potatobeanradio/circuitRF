using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Measurements;

/// <summary>
/// Phase 5-4 gate: measurement evaluator on Hero 2 (single-tone GaN PA).
///
/// Verifies that cube-algebra measurement expressions evaluated by
/// <see cref="MeasurementEvaluator"/> produce the same Pout_dBm values as
/// the manually-computed reference in Hero2Tests.SimpleSweep, to ≤ 0.001 dB.
///
/// Measurement expression under test (single-tone, n_drain at harm index 1, All sweep):
///   Pout_dBm = 10*log10(real(0.5*HB1.V("n_drain",1,All)*conj(-1*HB1.INl("n_drain",1,All)))*1000)
///
/// The real() wrapper extracts Re(V·conj(I)/2) — actual RF power — matching the manual
/// reference exactly (within floating-point round-trip).
/// </summary>
public class Hero2MeasurementTests(ITestOutputHelper output)
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
    public void Pout_dBm_MatchesManualReference()
    {
        var dir       = Hero2Dir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero2.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);

        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        // Keep sweep short (−20..−10 dBm) for test speed; all points should converge
        var p   = HbEngine.Resolve(hba, netlist.ResolvedGlobals) with { SweepStop = -10.0 };
        var ds  = new HbEngine(netlist, tb).Run(p);

        // ── Verify all points converged ───────────────────────────────────────
        var sweepVals = ds["Converged"].Axes[0].Values;
        int nSweep = sweepVals.Length;
        for (int si = 0; si < nSweep; si++)
            Assert.True((double)ds["Converged"][si] > 0.5,
                $"HB not converged at Pin={sweepVals[si]:F1} dBm");

        // ── Manual reference ──────────────────────────────────────────────────
        string[] nodeNames = ds["V"].Axes[0].Labels!;
        int drainIdx = Array.FindIndex(nodeNames,
            n => n.Contains("n_drain", StringComparison.OrdinalIgnoreCase));
        Assert.True(drainIdx >= 0, "n_drain not found in V cube labels");

        var refPout = new double[nSweep];
        for (int si = 0; si < nSweep; si++)
        {
            var vout = (Complex)ds["V"][drainIdx, 1, si];
            var iout = -(Complex)ds["I:M1:d"][1, si];   // current OUT of port = -(current INTO FET)
            double pW = 0.5 * (vout * Complex.Conjugate(iout)).Real;
            refPout[si] = 10.0 * Math.Log10(pW * 1000.0);
        }

        // ── Measurement evaluator ─────────────────────────────────────────────
        // I("M1:d", harm, sweepSlice) — branch-current accessor, no node axis
        tb.Measurements.Clear();
        tb.Measurements.Add(new Measurement("Pout_dBm",
            "10*log10(real(0.5*HB1.V(\"n_drain\",1,All)*conj(-1*HB1.I(\"M1:d\",1,All)))*1000)"));

        DataSet dsSet = ds;   // implicit HbRunResult → DataSet for the results dict
        var results = new Dictionary<string, DataSet> { ["HB1"] = dsSet };
        var me = new MeasurementEvaluator(tb, netlist, results);
        me.EvaluateInto(ds);

        // ── Gate assertions ───────────────────────────────────────────────────
        Assert.True(ds.Contains("Pout_dBm"), "DataSet missing 'Pout_dBm' after measurement eval");
        var poutCube = ds["Pout_dBm"];
        Assert.Equal(1, poutCube.Rank);
        Assert.Equal(nSweep, poutCube.Axes[0].Length);
        Assert.Equal(DataKind.Real, poutCube.DataKind);

        double[] meas = poutCube.RealValues;
        for (int si = 0; si < nSweep; si++)
        {
            double ref_  = refPout[si];
            double got   = meas[si];
            output.WriteLine($"Pin={sweepVals[si]:F1} dBm  ref={ref_:F4} dBm  meas={got:F4} dBm  Δ={got - ref_:F6} dB");
            Assert.Equal(ref_, got, 1e-3);   // ≤ 0.001 dB tolerance
        }
    }
}
