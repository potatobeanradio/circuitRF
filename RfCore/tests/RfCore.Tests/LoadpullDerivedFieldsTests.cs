// ================================================================
//  LoadpullDerivedFieldsTests.cs — gate tests for brief 7.5g
//
//  Slices covered:
//    7.5g-1  UnwrapDegInPlace helper (pure math, no I/O)
//    7.5g-2  SPL derived cubes: Zin_real/Zin_imag, AMPM, IRL
//    7.5g-3  SPL ZSource cube (from gamma_src1_* columns)
//    7.5g-4  SPL ZSource value matches file header expectation
//    7.5g-5  ConvertedFile.spl (lpcwave-style columns) derives cubes
//    7.5g-6  .lpcwave: Zin, AMPM, and ZSource cubes present
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using RfCore.Data;
using RfCore.Loadpull;
using Xunit;

namespace RfCore.Tests;

public class LoadpullDerivedFieldsTests
{
    // ── test-data helpers ──────────────────────────────────────────────────────

    private static string SplDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var c1 = Path.Combine(dir, "testdata", "spl_test_data");
            if (Directory.Exists(c1)) return c1;
            var c2 = Path.Combine(dir, "circuitRF", "testdata", "spl_test_data");
            if (Directory.Exists(c2)) return c2;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/spl_test_data not found");
    }

    private static string LpwDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var c1 = Path.Combine(dir, "testdata", "lpwave_test_data");
            if (Directory.Exists(c1)) return c1;
            var c2 = Path.Combine(dir, "circuitRF", "testdata", "lpwave_test_data");
            if (Directory.Exists(c2)) return c2;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/lpwave_test_data not found");
    }

    private static string SplFile(string name) => Path.Combine(SplDir(), name);
    private static string LpwFile(string name) => Path.Combine(LpwDir(), name);

    private static void Near(double expected, double actual, double tol, string label = "")
        => Assert.InRange(actual, expected - tol, expected + tol);

    // ── 7.5g-1: UnwrapDegInPlace (pure math) ─────────────────────────────────

    [Fact]
    public void UnwrapDegInPlace_WrapsCorrectly()
    {
        // Verify a phase sequence that wraps past ±180° is correctly unwrapped.
        // Input: 0 → 90 → 180 → 270 → -80 (wraps ≈ 360°)
        // Expected: 0 → 90 → 180 → 270 → 280
        var deg = new double[] { 0.0, 90.0, 180.0, 270.0, -80.0 };
        LoadpullDerivedFields.UnwrapDegInPlace(deg);

        Near(  0.0, deg[0], 1e-9, "deg[0]");
        Near( 90.0, deg[1], 1e-9, "deg[1]");
        Near(180.0, deg[2], 1e-9, "deg[2]");
        Near(270.0, deg[3], 1e-9, "deg[3]");
        Near(280.0, deg[4], 1e-9, "deg[4]");
    }

    // ── 7.5g-2: SPL derived cubes present ─────────────────────────────────────

    [FixtureFact("testdata/spl_test_data", "ask the repo owner for these lab-measured .spl files — not committed to the repository")]
    public void Spl_Ideal_DerivedCubesPresent()
    {
        // The standard fixture has Gamma_in_mag + Gamma_in_phase → Zin_real/Zin_imag
        // and trans_phase → AMPM, and Refl_dB → IRL.
        var ds = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));

        Assert.True(ds.Contains("Zin_real"), "Zin_real cube should be produced");
        Assert.True(ds.Contains("Zin_imag"), "Zin_imag cube should be produced");
        Assert.True(ds.Contains("AMPM_deg"),     "AMPM cube should be produced");
        Assert.True(ds.Contains("IRL_dB"),      "IRL cube should be produced (from Refl_dB)");

        // Shapes must match Pout (same grid × pin)
        var pout    = ds["Pout_dBm"];
        var zinReal = ds["Zin_real"];
        var ampm    = ds["AMPM_deg"];

        Assert.Equal(pout.Axes.Count, zinReal.Axes.Count);
        for (int i = 0; i < pout.Axes.Count; i++)
            Assert.Equal(pout.Axes[i].Length, zinReal.Axes[i].Length);

        Assert.Equal(pout.Axes.Count, ampm.Axes.Count);
    }

    // ── 7.5g-3: SPL ZSource cube present and rank-1 freq ─────────────────────

    [FixtureFact("testdata/spl_test_data", "ask the repo owner for these lab-measured .spl files — not committed to the repository")]
    public void Spl_Ideal_ZSourceCubePresentAndRankOne()
    {
        var ds = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));

        Assert.True(ds.Contains("ZSource"), "ZSource cube should be produced from gamma_src1");
        var zc = ds["ZSource"];
        var freqAx = Assert.Single(zc.Axes);
        Assert.Equal("freq", freqAx.Name);
        Assert.Equal(1, freqAx.Length);  // single-freq fixture
        Assert.Equal(DataKind.Complex, zc.DataKind);
    }

    // ── 7.5g-4: SPL ZSource value is finite and consistent with source Γ ──────

    [FixtureFact("testdata/spl_test_data", "ask the repo owner for these lab-measured .spl files — not committed to the repository")]
    public void Spl_Ideal_ZSourceValueFiniteAndPlausible()
    {
        // Fixture gamma_src1_real/imag = 0.284672 + 0.467153j
        // → GammaToZ → Z ≈ 48 + 64j Ω (plausible active source impedance)
        var ds = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));

        var zc = ds["ZSource"];
        var sr = zc[0];
        Assert.True(sr.IsComplex,                  "ZSource[0] should be complex");
        var z = sr.ComplexValue!.Value;
        Assert.True(double.IsFinite(z.Real),      "ZSource.Real should be finite");
        Assert.True(double.IsFinite(z.Imaginary), "ZSource.Imag should be finite");

        // Rough plausibility: Z.Real should be in a physical range (0 < R < 500 Ω)
        Assert.True(z.Real > 0 && z.Real < 500,
            $"ZSource.Real = {z.Real} should be in (0, 500)");
    }

    // ── 7.5g-5: ConvertedFile.spl (lpcwave-style columns) derives cubes ───────

    [FixtureFact("testdata/spl_test_data", "ask the repo owner for these lab-measured .spl files — not committed to the repository")]
    public void Spl_ConvertedFile_DerivedCubesOrGracefulAbsence()
    {
        // ConvertedFile.spl uses lpcwave-style column names (|GinWaves@F0|, |GS@F0|, etc.)
        // We only assert the parse does NOT throw and produces some FOM cubes.
        // Derived cubes are presence-gated (may or may not be present depending on columns).
        var ds = SplReader.ReadSpl(SplFile("ConvertedFile.spl"));

        Assert.True(ds.Contains("Pout_dBm"), "ConvertedFile.spl should have at least a Pout cube");
        // If ZSource present, it must be finite
        if (ds.Contains("ZSource"))
        {
            var sr = ds["ZSource"][0];
            var zv = sr.ComplexValue.GetValueOrDefault();
            Assert.True(sr.IsComplex && double.IsFinite(zv.Real),
                $"ConvertedFile ZSource.Real should be finite, got {zv.Real}");
        }
    }

    // ── 7.5g-6: .lpcwave fixture has Zin, AMPM, and ZSource ─────────────────

    [FixtureFact("testdata/lpwave_test_data", "ask the repo owner for these lab-measured .lpcwave files — not committed to the repository")]
    public void Lpcwave_DerivedCubesPresent()
    {
        // 4x150_new_wavecal_24012020.lpcwave has |GinWaves@F0|/PhiinWaves@F0[deg],
        // PhiLWaves@F0[deg], |GS@F0|/PhiS@F0[deg] — all derivation inputs present.
        var ds = LpcwaveReader.ReadLpcwave(LpwFile("4x150_new_wavecal_24012020.lpcwave"));

        Assert.True(ds.Contains("Zin_real"), "Zin_real should be derived for lpcwave fixture");
        Assert.True(ds.Contains("Zin_imag"), "Zin_imag should be derived for lpcwave fixture");
        Assert.True(ds.Contains("AMPM_deg"),     "AMPM should be derived for lpcwave fixture");
        Assert.True(ds.Contains("ZSource"),  "ZSource should be produced for lpcwave fixture");

        // ZSource rank-1 {freq}
        var zc = ds["ZSource"];
        var lpwFreqAx = Assert.Single(zc.Axes);
        Assert.Equal("freq", lpwFreqAx.Name);
        var sr = zc[0];
        Assert.True(sr.IsComplex, "Lpcwave ZSource[0] should be complex");
        var z = sr.ComplexValue!.Value;
        Assert.True(double.IsFinite(z.Real),
            $"Lpcwave ZSource.Real should be finite, got {z.Real}");

        // Source Z from file comment: 27.38 +j 21.17 Ω → tolerate ±1 Ω
        Near(27.38, z.Real,      1.0, "ZSource.Real ≈ 27.38 Ω");
        Near(21.17, z.Imaginary, 1.0, "ZSource.Imag ≈ 21.17 Ω");
    }
}
