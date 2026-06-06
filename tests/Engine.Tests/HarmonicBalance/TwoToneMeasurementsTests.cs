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
/// Phase 4c Step 7 — IMD measurement selectors (<see cref="TwoToneMeasurements"/>).
///
/// Verifies the selectors invert the locked mixIndex enumeration correctly and read the §6.3
/// product table off a real Hero-5 two-tone result:
///   - Tone(k₁,k₂) returns the stored phasor for a retained rep, and the conjugate of the retained
///     partner for a non-retained rep (e.g. (−1,2) = conj of (1,−2));
///   - product frequencies map correctly (carriers → f₁/f₂, IM3 → 2f₁−f₂);
///   - IM3/IM2/IM5 are below the carrier (negative dBc) at this drive.
/// </summary>
public class TwoToneMeasurementsTests(ITestOutputHelper output)
{
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

    private static DataSet RunHero5(double pavlStop)
    {
        var dir       = Hero5Dir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dir, "hero5.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);
        var hba       = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p         = HbEngine.Resolve(hba, netlist.ResolvedGlobals) with { SweepStop = pavlStop };
        return new HbEngine(netlist, tb).Run(p);
    }

    [Fact]
    public void Selectors_InvertMixIndex_AndReadImdTable()
    {
        var ds = RunHero5(-14.0);                // a few low/mid-drive points
        var sweepVals = ds["Converged"].Axes[0].Values;
        int last = sweepVals.Length - 1;
        int maxOrder = (int)Math.Round(ds["MetaMixOrder"].RealValues[0]);
        var grid = new MixingGrid(maxOrder);
        var tf = ds["ToneFreqs"].RealValues;
        double f1 = tf[0], f2 = tf[1];

        string[] nodeNames = ds["V"].Axes[0].Labels!;
        int drainIdx = Array.FindIndex(nodeNames, s => s.Contains("drain", StringComparison.OrdinalIgnoreCase));
        Assert.True(drainIdx >= 0);
        string drain = nodeNames[drainIdx];

        // ── Tone() on a RETAINED rep equals the raw stored phasor. ──
        var direct = (Complex)ds["V"][drainIdx, grid.IndexOf(2, -1), last];
        Assert.Equal(direct, TwoToneMeasurements.Tone(ds, last, drain, 2, -1));

        // ── Tone() on a NON-RETAINED rep uses the conjugate of its retained partner. ──
        // (−1,2) is not in the half-plane; its conjugate (1,−2) is.
        Assert.Equal(-1, grid.IndexOf(-1, 2));
        Assert.True(grid.IndexOf(1, -2) >= 0);
        var conjPartner = Complex.Conjugate((Complex)ds["V"][drainIdx, grid.IndexOf(1, -2), last]);
        var viaSelector = TwoToneMeasurements.Tone(ds, last, drain, -1, 2);
        Assert.Equal(conjPartner.Real,      viaSelector.Real,      1e-12);
        Assert.Equal(conjPartner.Imaginary, viaSelector.Imaginary, 1e-12);

        // ── Product frequencies map correctly. ──
        Assert.Equal(f1,          TwoToneMeasurements.FrequencyOf(ds, 1, 0), 1.0);
        Assert.Equal(f2,          TwoToneMeasurements.FrequencyOf(ds, 0, 1), 1.0);
        Assert.Equal(2 * f1 - f2, TwoToneMeasurements.FrequencyOf(ds, 2, -1), 1.0);
        Assert.Equal(2 * f2 - f1, TwoToneMeasurements.FrequencyOf(ds, -1, 2), 1.0);
        Assert.Equal(3 * f1 - 2 * f2, TwoToneMeasurements.FrequencyOf(ds, 3, -2), 1.0);

        // ── IMD levels (dBc), §6.3 products on the drain. ──
        double carrierDbm = TwoToneMeasurements.PoutDbm(ds, last, drain, 1, 0);
        double im3LoDbc = TwoToneMeasurements.ImDbc(ds, last, drain, 2, -1, 1, 0);   // 2f1-f2 vs f1
        double im3HiDbc = TwoToneMeasurements.ImDbc(ds, last, drain, -1, 2, 0, 1);   // 2f2-f1 vs f2
        double im2Dbc   = TwoToneMeasurements.ImDbc(ds, last, drain, 1, -1, 1, 0);   // f2-f1 baseband
        double im5LoDbc = TwoToneMeasurements.ImDbc(ds, last, drain, 3, -2, 1, 0);   // 3f1-2f2

        output.WriteLine($"Hero5 @ Pavl={sweepVals[last]:F0} dBm — carrier Pout={carrierDbm:F2} dBm");
        output.WriteLine($"  IM3 lo (2f1-f2) = {im3LoDbc:F1} dBc   IM3 hi (2f2-f1) = {im3HiDbc:F1} dBc");
        output.WriteLine($"  IM2 (f2-f1)     = {im2Dbc:F1} dBc");
        output.WriteLine($"  IM5 lo (3f1-2f2)= {im5LoDbc:F1} dBc");

        // Intermod products sit below the carrier.
        Assert.True(im3LoDbc < 0, $"IM3 lo not below carrier: {im3LoDbc:F2} dBc");
        Assert.True(im3HiDbc < 0, $"IM3 hi not below carrier: {im3HiDbc:F2} dBc");
        Assert.True(im5LoDbc < 0, $"IM5 lo not below carrier: {im5LoDbc:F2} dBc");
        // IM3 sidebands are roughly symmetric (equal tones, near-symmetric terminations).
        Assert.True(Math.Abs(im3LoDbc - im3HiDbc) < 6.0,
            $"IM3 sidebands wildly asymmetric: lo={im3LoDbc:F2}, hi={im3HiDbc:F2} dBc");
    }
}
