using System;
using System.IO;
using System.Numerics;
using RfCore.Data;
using RfCore.Loadpull;
using Xunit;

namespace CircuitRF.Engine.Tests.Loadpull;

/// <summary>
/// Gate 7.4f-1: SplReader reads all 4 spl_test_data files into the
/// canonical loadpull DataSet shape.
/// </summary>
public class SplReaderTests
{
    private static string SplDataDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "spl_test_data");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/spl_test_data not found");
    }

    private static void Near(double expected, double actual, double tol, string label = "")
        => Assert.InRange(actual, expected - tol, expected + tol);

    // ── Canonical axis names ──────────────────────────────────────────────────

    [Fact]
    public void SplReader_HarmonicaRf_1p8GHz_GridPinCounts()
    {
        var path = Path.Combine(SplDataDir(), "Ideal_GaN_FET_1p6_mm_1p8_GHz.spl");
        var ds   = SplReader.ReadSpl(path);

        var pout = ds["Pout_dBm"];
        Assert.Equal("gridPoint", pout.Axis(0).Name);
        Assert.Equal("pinStep",   pout.Axis(1).Name);
        Assert.Equal(145, pout.Axis(0).Length);
        Assert.Equal(70,  pout.Axis(1).Length);
    }

    [Fact]
    public void SplReader_HarmonicaRf_1p8GHz_GammaLoadShape()
    {
        var path = Path.Combine(SplDataDir(), "Ideal_GaN_FET_1p6_mm_1p8_GHz.spl");
        var ds   = SplReader.ReadSpl(path);

        var gl = ds["GammaLoad"];
        Assert.Equal(1, gl.Rank);
        Assert.Equal("gridPoint", gl.Axis(0).Name);
        Assert.Equal(145, gl.Axis(0).Length);
        Assert.Equal(DataKind.Complex, gl.DataKind);
    }

    [Fact]
    public void SplReader_HarmonicaRf_1p8GHz_FirstGammaIsZero()
    {
        // Grid point 0 in the file has gamma_ld1 = 0+0j (50 Ω match)
        var path = Path.Combine(SplDataDir(), "Ideal_GaN_FET_1p6_mm_1p8_GHz.spl");
        var ds   = SplReader.ReadSpl(path);

        var gl   = ds["GammaLoad"].ComplexValues;
        var g0   = gl[0];
        Near(0.0, g0.Magnitude, 1e-6, "|Γ₀|");
    }

    [Fact]
    public void SplReader_HarmonicaRf_1p8GHz_PoutUnitsAreWatts()
    {
        // At grid point 0, pin step 0: Pin_avail = -20 dBm → Pout_dBm ≈ -7.57
        // In watts: 10^(-7.57/10)/1000 ≈ 1.75e-4 W
        var path = Path.Combine(SplDataDir(), "Ideal_GaN_FET_1p6_mm_1p8_GHz.spl");
        var ds   = SplReader.ReadSpl(path);

        double poutDbm = -7.569553; // from file row 1
        double poutW   = Math.Pow(10.0, poutDbm / 10.0) / 1000.0;

        var pout = ds["Pout_W"].RealValues;
        Near(poutW, pout[0], 1e-7, "Pout[0,0] W");
    }

    [Fact]
    public void SplReader_HarmonicaRf_1p8GHz_DEIsLinear()
    {
        // Eff_%% ≈ 0.004638 → linear = 0.004638/100 = 4.638e-5
        var path = Path.Combine(SplDataDir(), "Ideal_GaN_FET_1p6_mm_1p8_GHz.spl");
        var ds   = SplReader.ReadSpl(path);

        var de = ds["Efficiency"].RealValues;
        Near(0.004638, de[0], 1e-6, "Efficiency[0,0] %");
    }

    [Fact]
    public void SplReader_HarmonicaRf_1p8GHz_PavlDbmAxisValues()
    {
        // pinStep axis values = Pin_avail_dBm; first step ≈ -20 dBm
        var path = Path.Combine(SplDataDir(), "Ideal_GaN_FET_1p6_mm_1p8_GHz.spl");
        var ds   = SplReader.ReadSpl(path);

        var pinAxis = ds["PavlDbm"].Axis(1).Values;
        Near(-20.0, pinAxis[0], 0.1, "pinStep[0] dBm");
    }

    [Fact]
    public void SplReader_HarmonicaRf_1p8GHz_CanonicalCubesPresent()
    {
        var path = Path.Combine(SplDataDir(), "Ideal_GaN_FET_1p6_mm_1p8_GHz.spl");
        var ds   = SplReader.ReadSpl(path);

        foreach (var name in new[] { "Pout_dBm", "Pout_W", "Gt_dB", "Gp_dB", "Efficiency", "PAE", "PavlDbm",
                                     "GammaLoad", "ZLoad" })
            Assert.True(ds.Contains(name), $"Missing cube: {name}");
    }

    [Fact]
    public void SplReader_HarmonicaRf_1p8GHz_GtIsPositive()
    {
        // Transducer gain should be positive (PA gain ~12-15 dB)
        var path = Path.Combine(SplDataDir(), "Ideal_GaN_FET_1p6_mm_1p8_GHz.spl");
        var ds   = SplReader.ReadSpl(path);

        var gt = ds["Gt_dB"].RealValues;
        Assert.True(gt[0] > 5.0, $"Gt[0,0] = {gt[0]} dB — expected > 5 dB");
    }

    // ── 3-frequency file ──────────────────────────────────────────────────────

    [Fact]
    public void SplReader_HarmonicaRf_3Freq_FreqAxisPresent()
    {
        var path = Path.Combine(SplDataDir(), "GaN_FET_1p6_mm_3_Freq.spl");
        var ds   = SplReader.ReadSpl(path);

        var pout = ds["Pout_dBm"];
        Assert.Equal(3, pout.Rank);
        Assert.Equal("freq",      pout.Axis(0).Name);
        Assert.Equal("gridPoint", pout.Axis(1).Name);
        Assert.Equal("pinStep",   pout.Axis(2).Name);
    }

    [Fact]
    public void SplReader_HarmonicaRf_3Freq_GridPinCounts()
    {
        var path = Path.Combine(SplDataDir(), "GaN_FET_1p6_mm_3_Freq.spl");
        var ds   = SplReader.ReadSpl(path);

        var pout = ds["Pout_dBm"];
        Assert.Equal(3,   pout.Axis(0).Length);
        Assert.Equal(145, pout.Axis(1).Length);
        Assert.Equal(70,  pout.Axis(2).Length);
    }

    // ── lpcwave-derived .spl dialect ─────────────────────────────────────────

    [Fact]
    public void SplReader_LpcwaveDerived_GridPinCounts()
    {
        var path = Path.Combine(SplDataDir(), "ConvertedFile.spl");
        var ds   = SplReader.ReadSpl(path);

        var pout = ds["Pout_dBm"];
        Assert.Equal("gridPoint", pout.Axis(0).Name);
        Assert.Equal("pinStep",   pout.Axis(1).Name);
        // 38 grid points × 13 pin steps (from header "2.4 38 13")
        Assert.Equal(38, pout.Axis(0).Length);
        Assert.Equal(13, pout.Axis(1).Length);
    }

    [Fact]
    public void SplReader_LpcwaveDerived_FirstGamma()
    {
        // First grid point: gamma_ld1 = 0+0.5j → |Γ| = 0.5, phase = 90°
        var path = Path.Combine(SplDataDir(), "ConvertedFile.spl");
        var ds   = SplReader.ReadSpl(path);

        var gl = ds["GammaLoad"].ComplexValues;
        Near(0.5, gl[0].Magnitude,  0.01, "|Γ₀|");
        Near(90.0, gl[0].Phase * 180.0 / Math.PI, 2.0, "∠Γ₀ (deg)");
    }

    [Fact]
    public void SplReader_LpcwaveDerived_PoutUnitsAreWatts()
    {
        // PoutWaves[dBm] ≈ 16.72 dBm at first point/step → W
        var path = Path.Combine(SplDataDir(), "ConvertedFile.spl");
        var ds   = SplReader.ReadSpl(path);

        double poutDbm = 16.72; // from file
        double poutW   = Math.Pow(10.0, poutDbm / 10.0) / 1000.0;

        var pout = ds["Pout_W"].RealValues;
        Near(poutW, pout[0], 0.002, "Pout[0,0] W");
    }

    [Fact]
    public void SplReader_LpcwaveDerived_CanonicalCubesPresent()
    {
        var path = Path.Combine(SplDataDir(), "ConvertedFile.spl");
        var ds   = SplReader.ReadSpl(path);

        foreach (var name in new[] { "Pout_dBm", "Gt_dB", "Gp_dB", "Efficiency", "PAE", "GammaLoad", "ZLoad" })
            Assert.True(ds.Contains(name), $"Missing cube: {name}");
    }

    // ── TestOut.spl ───────────────────────────────────────────────────────────

    [Fact]
    public void SplReader_TestOut_ParsesWithoutThrowing()
    {
        var path = Path.Combine(SplDataDir(), "TestOut.spl");
        var ds   = SplReader.ReadSpl(path);
        Assert.True(ds.Contains("GammaLoad"), "GammaLoad missing");
    }
}
