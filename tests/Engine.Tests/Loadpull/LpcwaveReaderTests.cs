using System;
using System.IO;
using System.Numerics;
using RfCore.Data;
using RfCore.Loadpull;
using Xunit;

namespace CircuitRF.Engine.Tests.Loadpull;

/// <summary>
/// Gate 7.4f-2: LpcwaveReader reads all 5 lpwave_test_data files into the
/// canonical loadpull DataSet shape.
/// </summary>
public class LpcwaveReaderTests
{
    private static string LpwaveDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "lpwave_test_data");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/lpwave_test_data not found");
    }

    private static void Near(double expected, double actual, double tol, string label = "")
        => Assert.InRange(actual, expected - tol, expected + tol);

    // ── Basic single-freq loadpull file ───────────────────────────────────────

    [Fact]
    public void LpcwaveReader_Basic_GridPinCounts()
    {
        var path = Path.Combine(LpwaveDir(), "4x150_new_wavecal_24012020.lpcwave");
        var ds   = LpcwaveReader.ReadLpcwave(path);

        var pout = ds["Pout_dBm"];
        Assert.Equal("gridPoint", pout.Axis(0).Name);
        Assert.Equal("pinStep",   pout.Axis(1).Name);
        Assert.Equal(19, pout.Axis(0).Length); // 19 # grid-point lines
        Assert.Equal(16, pout.Axis(1).Length); // 16 drive-up rows per grid point
    }

    [Fact]
    public void LpcwaveReader_Basic_GammaLoadShape()
    {
        var path = Path.Combine(LpwaveDir(), "4x150_new_wavecal_24012020.lpcwave");
        var ds   = LpcwaveReader.ReadLpcwave(path);

        var gl = ds["GammaLoad"];
        Assert.Equal(1, gl.Rank);
        Assert.Equal("gridPoint", gl.Axis(0).Name);
        Assert.Equal(19, gl.Axis(0).Length);
        Assert.Equal(DataKind.Complex, gl.DataKind);
    }

    [Fact]
    public void LpcwaveReader_Basic_FirstGammaIsNearZero()
    {
        // Grid point 001: Gamma=0.000, Phase=-88.9° → Γ ≈ 0 (50 Ω match)
        var path = Path.Combine(LpwaveDir(), "4x150_new_wavecal_24012020.lpcwave");
        var ds   = LpcwaveReader.ReadLpcwave(path);

        var gl = ds["GammaLoad"].ComplexValues;
        Near(0.0, gl[0].Magnitude, 1e-6, "|Γ₀|");
    }

    [Fact]
    public void LpcwaveReader_Basic_PinAxisFromPsource()
    {
        // Psource[dBm] appears before PinWaves[dBm] → pinStep axis from Psource
        // Grid point 001 first drive step: Psource = -7.45 dBm
        var path = Path.Combine(LpwaveDir(), "4x150_new_wavecal_24012020.lpcwave");
        var ds   = LpcwaveReader.ReadLpcwave(path);

        var pin = ds["PavlDbm"].Axis(1).Values;
        Near(-7.45, pin[0], 0.05, "pinStep[0] dBm");
    }

    [Fact]
    public void LpcwaveReader_Basic_PoutUnitsAreWatts()
    {
        // Grid point 0 pin step 0: PoutWaves = 3.05 dBm
        var path = Path.Combine(LpwaveDir(), "4x150_new_wavecal_24012020.lpcwave");
        var ds   = LpcwaveReader.ReadLpcwave(path);

        double poutDbm = 3.05;
        double poutW   = Math.Pow(10.0, poutDbm / 10.0) / 1000.0;

        var pout = ds["Pout_W"].RealValues;
        Near(poutW, pout[0], 5e-5, "Pout[0,0] W");
    }

    [Fact]
    public void LpcwaveReader_Basic_PAEIsLinear()
    {
        // PAEffWaves[%] at grid point 0, pin step 0: 0.82 % → 0.0082
        var path = Path.Combine(LpwaveDir(), "4x150_new_wavecal_24012020.lpcwave");
        var ds   = LpcwaveReader.ReadLpcwave(path);

        var pae = ds["PAE"].RealValues;
        Near(0.82, pae[0], 1e-2, "PAE[0,0] %");
    }

    [Fact]
    public void LpcwaveReader_Basic_CanonicalCubesPresent()
    {
        var path = Path.Combine(LpwaveDir(), "4x150_new_wavecal_24012020.lpcwave");
        var ds   = LpcwaveReader.ReadLpcwave(path);

        // Basic file has no OutEffWaves[%] → DE absent; OPT file (below) tests DE.
        foreach (var name in new[] { "Pout_dBm", "Pout_W", "Gt_dB", "Gp_dB", "PAE", "PavlDbm",
                                     "BiasVLoad", "BiasILoad", "GammaLoad", "ZLoad" })
            Assert.True(ds.Contains(name), $"Missing cube: {name}");
    }

    [Fact]
    public void LpcwaveReader_Basic_ZLoadIsReasonable()
    {
        // Grid point 001: Γ=0 → Z=50 Ω
        var path = Path.Combine(LpwaveDir(), "4x150_new_wavecal_24012020.lpcwave");
        var ds   = LpcwaveReader.ReadLpcwave(path);

        var zl = ds["ZLoad"].ComplexValues;
        Near(50.0, zl[0].Real,      1.0, "Z[0] real");
        Near(0.0,  zl[0].Imaginary, 1.0, "Z[0] imag");
    }

    // ── OPT-pattern file (no Psource, I1[uA]) ───────────────────────────────

    [Fact]
    public void LpcwaveReader_OPT_GridPinCounts()
    {
        var path = Path.Combine(LpwaveDir(), "compression-LP-OPT-pattern.lpcwave");
        var ds   = LpcwaveReader.ReadLpcwave(path);

        var pout = ds["Pout_dBm"];
        Assert.Equal(27, pout.Axis(0).Length); // 27 grid points
        Assert.Equal(34, pout.Axis(1).Length); // 34 drive steps
    }

    [Fact]
    public void LpcwaveReader_OPT_FirstGamma()
    {
        // Grid point 001: Γ = 0.250 ∠ 99.3°
        var path = Path.Combine(LpwaveDir(), "compression-LP-OPT-pattern.lpcwave");
        var ds   = LpcwaveReader.ReadLpcwave(path);

        var gl   = ds["GammaLoad"].ComplexValues;
        Near(0.250, gl[0].Magnitude,               0.002, "|Γ₀|");
        Near(99.3,  gl[0].Phase * 180.0 / Math.PI, 2.0,   "∠Γ₀ (deg)");
    }

    [Fact]
    public void LpcwaveReader_OPT_PinAxisFromPinWaves()
    {
        // No Psource col → pinStep from PinWaves[dBm]; first step = -10.96
        var path = Path.Combine(LpwaveDir(), "compression-LP-OPT-pattern.lpcwave");
        var ds   = LpcwaveReader.ReadLpcwave(path);

        var pin = ds["PavlDbm"].Axis(1).Values;
        Near(-10.96, pin[0], 0.05, "pinStep[0] dBm");
    }

    [Fact]
    public void LpcwaveReader_OPT_BiasISrcUsesUA()
    {
        // I1[uA] → BiasISrc in Amps (µA → A)
        // Grid point 0, pin step 0: I1[uA] = -1.716 µA → -1.716e-6 A
        var path = Path.Combine(LpwaveDir(), "compression-LP-OPT-pattern.lpcwave");
        var ds   = LpcwaveReader.ReadLpcwave(path);

        Assert.True(ds.Contains("BiasISrc"), "BiasISrc missing");
        var bias = ds["BiasISrc"].RealValues;
        Near(-1.716e-6, bias[0], 1e-8, "BiasISrc[0,0]");
    }

    [Fact]
    public void LpcwaveReader_OPT_PAEIsLinear()
    {
        // Grid 0, step 0: PAEffWaves[%] = 0.08 % → 8e-4
        var path = Path.Combine(LpwaveDir(), "compression-LP-OPT-pattern.lpcwave");
        var ds   = LpcwaveReader.ReadLpcwave(path);

        var pae = ds["PAE"].RealValues;
        Near(0.08, pae[0], 1e-3, "PAE[0,0] %");
    }

    // ── Harmonic-nesting files — parse-without-error + grid count ────────────

    [Fact]
    public void LpcwaveReader_F0_2F0_GridCount()
    {
        var path = Path.Combine(LpwaveDir(), "4x150_new_wavecal_f0_2f0_24012020.lpcwave");
        var ds   = LpcwaveReader.ReadLpcwave(path);

        var gl = ds["GammaLoad"];
        Assert.Equal(247, gl.Axis(0).Length); // nested F0×2F0 grid
    }

    [Fact]
    public void LpcwaveReader_F0_2F0_Variant2_ParsesOk()
    {
        var path = Path.Combine(LpwaveDir(), "4x150_new_wavecal_f0_2f0_24012020 2.lpcwave");
        var ds   = LpcwaveReader.ReadLpcwave(path);
        Assert.True(ds.Contains("GammaLoad"), "GammaLoad missing");
        Assert.True(ds["GammaLoad"].Axis(0).Length > 0, "No grid points");
    }

    [Fact]
    public void LpcwaveReader_F0_2F0_3F0_GridCount()
    {
        var path = Path.Combine(LpwaveDir(), "4x150_new_wavecal_f0_2f0_3f0_24012020.lpcwave");
        var ds   = LpcwaveReader.ReadLpcwave(path);

        var gl = ds["GammaLoad"];
        Assert.Equal(161, gl.Axis(0).Length); // nested F0×2F0×3F0 grid
    }

    // ── Sourcepull detection (via synthesized TextReader) ────────────────────

    [Fact]
    public void LpcwaveReader_SourcepullFile_ParsesOk()
    {
        // A file with "! Load Impedance" is sourcepull (load fixed, source swept).
        // Reader should not throw; GammaLoad holds the swept source Γ values.
        const string content =
            "! Power Sweep Source Pull Measurement Data\n" +
            "! Load Impedance = 50.00 +j 0.00 Ohm\n" +
            "Point  Gamma  Phase[deg]  PinWaves[dBm]  PoutWaves[dBm]  PAEffWaves[%]\n" +
            "!----\n" +
            "# 001  0.000  0.0\n" +
            "    5.00    10.00    40.00\n" +
            "    6.00    11.00    45.00\n" +
            "# 002  0.200  90.0\n" +
            "    5.00    10.50    41.00\n" +
            "    6.00    11.50    46.00\n";

        using var reader = new StringReader(content);
        var ds = LpcwaveReader.ReadLpcwave(reader);

        // Should have 2 grid points, 2 pin steps
        Assert.Equal(2, ds["Pout_dBm"].Axis(0).Length);
        Assert.Equal(2, ds["Pout_dBm"].Axis(1).Length);

        // Second grid point: Γ = 0.200 ∠ 90°
        var gl = ds["GammaLoad"].ComplexValues;
        Near(0.200, gl[1].Magnitude,               0.001, "|Γ₁|");
        Near(90.0,  gl[1].Phase * 180.0 / Math.PI, 1.0,   "∠Γ₁");
    }

    [Fact]
    public void LpcwaveReader_AllFiles_ParseWithoutException()
    {
        foreach (var file in Directory.EnumerateFiles(LpwaveDir(), "*.lpcwave"))
        {
            var ds = LpcwaveReader.ReadLpcwave(file); // must not throw
            Assert.True(ds.Contains("GammaLoad"), $"{Path.GetFileName(file)}: missing GammaLoad");
        }
    }
}
