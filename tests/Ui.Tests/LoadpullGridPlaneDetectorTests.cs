// ================================================================
//  LoadpullGridPlaneDetectorTests.cs
//  Gate tests for brief-dd-loadpull-contour-ux-round8 §4a — LoadpullRecognition.DetectGridPlane.
//
//  Both GammaLoad and ZLoad are always emitted for every loadpull (LoadpullEngine.
//  BuildLoadpullDataSet), so cube presence cannot tell a Γ-authored grid from an impedance-authored
//  one — the detector reads the GEOMETRY of GammaLoad instead (max VSWR vs a threshold).
//
//  Real-fixture measurements that picked/verified the 15.0 threshold (owner's heuristic, not a
//  hard fact of the data — see RESOLVED.md for the full record):
//    Ideal_GaN_FET_1p6_mm_1p8_GHz.spl (measured tuner data, Γ-swept)   → max VSWR ≈ 19.0  (Γ side)
//    Hero3/RLSweep.cnl + RLSweep.gam  (engine run, impedance-swept)    → max VSWR ≈ 2.6   (Z side)
//  Clean separation on both sides of 15.0. One secondary .spl fixture (ConvertedFile.spl, VSWR≈12.3)
//  sits under the threshold despite being Γ-measured — a known limitation of a geometric heuristic,
//  recorded rather than hidden; it is not one of the two fixtures this brief required.
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;
using RfCore.Loadpull;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class LoadpullGridPlaneDetectorTests
{
    // ---- Synthetic impedance-grid fixture (matches LoadpullEngine.BuildLoadpullDataSet's shape) --

    private static DataSet BuildSyntheticGrid(Complex[] gammaPoints)
    {
        int nG = gammaPoints.Length;
        var gridAxis = new Axis("gridPoint", Enumerable.Range(0, nG).Select(i => (double)i).ToArray());
        var pinAxis  = new Axis("pinStep",   new[] { 0.0 });

        var zLoad = gammaPoints.Select(g => 50.0 * (1 + g) / (1 - g)).ToArray();
        var pout  = new double[nG];
        var eff   = new double[nG];
        for (int i = 0; i < nG; i++) { pout[i] = 10.0; eff[i] = 40.0; }

        var ds = new DataSet();
        ds.Add("GammaLoad",  new DataCube(new[] { gridAxis }, gammaPoints));
        ds.Add("ZLoad",      new DataCube(new[] { gridAxis }, zLoad));
        ds.Add("Pout_dBm",   new DataCube(new[] { gridAxis, pinAxis }, pout));
        ds.Add("Efficiency", new DataCube(new[] { gridAxis, pinAxis }, eff));
        return ds;
    }

    // §4a: a low-VSWR (bounded, near-Z0) termination grid classifies as the impedance plane.
    [Fact]
    public void DetectGridPlane_LowVswrGrid_ReturnsZ()
    {
        // |Γ| up to ~0.026 (Z ≈ 50-53 Ω) — mirrors the real RLSweep.gam impedance-grid fixture
        // (measured max VSWR ≈ 2.6, well under the 15.0 threshold).
        var gammas = new[]
        {
            new Complex(0.0,   0.0),
            new Complex(0.026, 0.0),
            new Complex(0.0,   0.026),
            new Complex(-0.02, 0.015),
        };
        var ds   = BuildSyntheticGrid(gammas);
        var view = Assert.Single(LoadpullRecognition.FindLoadpullViews(ds));

        Assert.Equal(SurfacePlane.Z, LoadpullRecognition.DetectGridPlane(ds, view));
    }

    // §4a: a high-VSWR (near unit-circle) termination grid classifies as the Γ plane.
    [Fact]
    public void DetectGridPlane_HighVswrGrid_ReturnsGamma()
    {
        // max |Γ| = 0.9 → VSWR = 19 — mirrors the real Ideal_GaN_FET_1p6_mm_1p8_GHz.spl measurement
        // (measured max VSWR ≈ 19.0, clearly over the 15.0 threshold).
        var gammas = new[]
        {
            new Complex(0.0, 0.0),
            new Complex(0.5, 0.0),
            new Complex(0.9, 0.0),
            new Complex(0.0, 0.6),
        };
        var ds   = BuildSyntheticGrid(gammas);
        var view = Assert.Single(LoadpullRecognition.FindLoadpullViews(ds));

        Assert.Equal(SurfacePlane.Gamma, LoadpullRecognition.DetectGridPlane(ds, view));
    }

    // §4a: |Γ| == 1 exactly must not produce NaN/∞ — the point is clamped, not skipped, so it still
    // legitimately contributes to (rather than silently vanishing from) the max-VSWR measurement.
    [Fact]
    public void DetectGridPlane_UnitCircleGammaPoint_DoesNotProduceNaNOrInfinity()
    {
        var gammas = new[] { new Complex(1.0, 0.0), new Complex(0.1, 0.0) };
        var ds     = BuildSyntheticGrid(gammas);
        var view   = Assert.Single(LoadpullRecognition.FindLoadpullViews(ds));

        var plane = LoadpullRecognition.DetectGridPlane(ds, view);   // must not throw / must be finite-driven
        Assert.Equal(SurfacePlane.Gamma, plane);   // the clamped near-1 point still reads as a Γ grid
    }

    // §4a: a non-finite (NaN) Γ point is skipped rather than deciding the classification.
    [Fact]
    public void DetectGridPlane_NaNGammaPoint_IsSkipped()
    {
        var gammas = new[] { new Complex(double.NaN, double.NaN), new Complex(0.01, 0.0) };
        var ds     = BuildSyntheticGrid(gammas);
        var view   = Assert.Single(LoadpullRecognition.FindLoadpullViews(ds));

        Assert.Equal(SurfacePlane.Z, LoadpullRecognition.DetectGridPlane(ds, view));
    }

    // §4a: missing GammaLoad (should not happen per anchor 6, but must degrade safely) → impedance.
    [Fact]
    public void DetectGridPlane_MissingGammaLoad_DefaultsToZ()
    {
        var ds = new DataSet();
        var view = new LoadpullRecognition.LoadpullView(null);

        Assert.Equal(SurfacePlane.Z, LoadpullRecognition.DetectGridPlane(ds, view));
    }

    // §4a real fixture: a measured Γ-swept tuner grid (.spl) classifies as the Γ plane.
    [FixtureFact("testdata/spl_test_data", "ask the repo owner for these lab-measured .spl files — not committed to the repository")]
    public async Task DetectGridPlane_RealSplFixture_ClassifiesAsGamma()
    {
        var path = FindSplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl");
        if (path is null) return;

        var lib = new DataSourceLibraryViewModel();
        await lib.SelectDataSourceAsync(path);
        var ds = lib.SelectedEntry!.Data!;
        var view = Assert.Single(LoadpullRecognition.FindLoadpullViews(ds));

        Assert.Equal(SurfacePlane.Gamma, LoadpullRecognition.DetectGridPlane(ds, view));
    }

    private static string? FindSplFile(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = System.IO.Path.Combine(dir, "testdata", "spl_test_data", name);
            if (System.IO.File.Exists(cand)) return cand;
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        return null;
    }
}
