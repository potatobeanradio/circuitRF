using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using RfCore.Data;
using RfCore.Loadpull;
using Xunit;

namespace RfCore.Tests.Loadpull;

/// <summary>
/// Brief LPW — wave-derived loadpull metrics, the <c>.cst</c> and <c>.satwave</c> readers, one dispatcher, and a chosen
/// compression reference. Every expected value comes from <see cref="LpwSyntheticDevice"/>'s analytic device, worked from
/// the brief's definitions; the readers never see it except through the committed fixtures in <c>testdata/lpw</c>.
/// </summary>
public sealed class LpwWaveLoadpullTests
{
    private static string Fixture(string name) => Path.Combine(LpwSyntheticDevice.FixtureDir(), name);

    private static double At(DataSet ds, string cube, int g, int p) => ds[cube].RealValues[g * ds[cube].Axes[^1].Length + p];

    [Fact]
    public void CommittedFixtures_AreTheSyntheticDevicesOutput()
    {
        if (Environment.GetEnvironmentVariable("LPW_WRITE_FIXTURES") == "1") LpwSyntheticDevice.WriteAll(LpwSyntheticDevice.FixtureDir());
        foreach (var (name, text) in LpwSyntheticDevice.All())
            Assert.True(File.ReadAllText(Fixture(name)) == text, $"{name} is not the device's output; regenerate with LPW_WRITE_FIXTURES=1");
    }

    // ── Gate 1: the derivation ───────────────────────────────────────────

    [Fact]
    public void Lpcwave_WithItsComputedColumnsRemoved_TheWavesReproduceThem()
    {
        string[] stated = ["Psource[dBm]", "PinWaves[dBm]", "PinDelWaves[dBm]", "PoutWaves[dBm]", "GainWavesTrd[dB]", "GainWavesPwr[dB]",
                           "PAEffWaves[%]", "OutEffWaves[%]", "|GinWaves@F0|", "PhiinWaves@F0[deg]"];
        string full = File.ReadAllText(Fixture("synthetic.lpcwave"));
        var withColumns = LpcwaveReader.ReadLpcwave(new StringReader(full));
        var wavesOnly = LpcwaveReader.ReadLpcwave(new StringReader(DropColumns(full, stated)));

        foreach (var (cube, tol) in new[] { ("PavlDbm", 0.01), ("PinDel_dBm", 0.01), ("Pout_dBm", 0.01), ("Gt_dB", 0.01), ("Gp_dB", 0.01),
                                            ("PAE", 0.01), ("Efficiency", 0.01), ("IRL_dB", 0.01) })
        {
            var a = withColumns[cube].RealValues; var b = wavesOnly[cube].RealValues;
            for (int i = 0; i < a.Length; i++)
            {
                Assert.Equal(double.IsNaN(a[i]), double.IsNaN(b[i]));
                if (!double.IsNaN(a[i])) Assert.True(Math.Abs(a[i] - b[i]) <= tol, $"{cube}[{i}]: stated {a[i]}, from the waves {b[i]}");
            }
        }
        // ΓS ≠ 0, so the available power is not the incident one.
        Assert.True(Math.Abs(At(wavesOnly, "PavlDbm", 0, 0) - At(wavesOnly, "PinInc_dBm", 0, 0)) > 0.1);
    }

    /// <summary>The text with the named columns taken out of the header and every data row.</summary>
    private static string DropColumns(string lpcwave, string[] drop)
    {
        var lines = lpcwave.Split("\r\n");
        int header = Array.FindIndex(lines, l => l.StartsWith("Point", StringComparison.Ordinal));
        var cols = lines[header].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var keep = Enumerable.Range(0, cols.Length).Where(i => !drop.Contains(cols[i])).ToArray();
        for (int k = 0; k < lines.Length; k++)
        {
            var t = lines[k].Trim();
            if (k == header) lines[k] = string.Join("  ", keep.Select(i => cols[i]));
            else if (k > header && t.Length > 0 && !t.StartsWith('!') && !t.StartsWith('#'))
            {
                var v = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                lines[k] = "  " + string.Join("  ", keep.Where(i => i >= 3).Select(i => v[i - 3]));
            }
        }
        return string.Join("\r\n", lines);
    }

    // ── Gate 2: .cst ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("synthetic_f0.cst", 0)]
    [InlineData("synthetic_2f0.cst", 1)]
    [InlineData("synthetic_3f0.cst", 2)]
    public void Cst_ReadsEveryLoadAndDrive_WithEveryMetricAtItsAnalyticValue(string file, int harmonics)
    {
        var ds = LoadpullFiles.Read(Fixture(file));
        var loads = LpwSyntheticDevice.Loads;
        Assert.Equal(loads.Length, ds["GammaLoad"].Axes[0].Length);
        Assert.Equal(LpwSyntheticDevice.Drives.Max(), ds["Pout_dBm"].Axes[^1].Length);
        for (int g = 0; g < loads.Length; g++)
            Assert.True((ds["GammaLoad"].ComplexValues[g] - loads[g]).Magnitude < 1e-5, $"GammaLoad[{g}]");

        for (int g = 0; g < loads.Length; g++)
            for (int p = 0; p < LpwSyntheticDevice.Drives.Max(); p++)
            {
                if (p >= LpwSyntheticDevice.Drives[g])
                {
                    Assert.True(double.IsNaN(At(ds, "Pout_dBm", g, p)), $"load {g} drive {p} is padding");
                    continue;
                }
                var m = LpwSyntheticDevice.Expected(g, p);
                // A harmonic power that is not positive (the first drive's, where the load reflects more than it is sent) is
                // NaN on both sides: dBm of nothing.
                void Near(string cube, double want, double tol) =>
                    Assert.True(double.IsNaN(want) ? double.IsNaN(At(ds, cube, g, p)) : Math.Abs(At(ds, cube, g, p) - want) <= tol,
                                $"{cube} load {g} drive {p}: {At(ds, cube, g, p)} vs {want}");
                Near("PavlDbm", m.PavlDbm, 1e-3); Near("PinInc_dBm", m.PinIncDbm, 1e-3); Near("PinDel_dBm", m.PinDelDbm, 1e-3);
                Near("Pout_dBm", m.PoutDbm, 1e-3); Near("Pout_W", m.PoutW, 1e-5 * Math.Max(1, m.PoutW)); Near("Gt_dB", m.GtDb, 1e-3);
                Near("Gp_dB", m.GpDb, 1e-3); Near("Pdc_W", m.PdcW, 1e-4); Near("Efficiency", m.Efficiency, 1e-3); Near("PAE", m.Pae, 1e-3);
                Near("Zin_real", m.Zin.Real, 1e-3); Near("Zin_imag", m.Zin.Imaginary, 1e-3); Near("IRL_dB", m.IrlDb, 1e-3);
                Near("AMPM_deg", m.AmpmDeg, 1e-2);
                int np = ds["GammaLoadMeas"].Axes[^1].Length;
                Assert.True((ds["GammaLoadMeas"].ComplexValues[g * np + p] - m.GammaLoadMeas).Magnitude < 1e-5);
                for (int h = 2; h <= harmonics + 1; h++)
                {
                    var (pout, pdel) = LpwSyntheticDevice.ExpectedHarmonic(g, p, h);
                    Near(LoadpullWaves.HarmonicPoutName(h), pout, 1e-3);
                    Near(LoadpullWaves.HarmonicPinDelName(h), pdel, 1e-3);
                }
            }

        for (int h = 2; h <= 3; h++)
        {
            Assert.Equal(h <= harmonics + 1, ds.Contains(LoadpullWaves.HarmonicGammaName(h)));
            Assert.Equal(h <= harmonics + 1, ds.Contains(LoadpullWaves.HarmonicPoutName(h)));
        }
    }

    [Fact]
    public void Cst_HarmonicLoad_IsPowerWeighted_SoTheNoisyFirstDriveDoesNotMoveIt()
    {
        var ds = LoadpullFiles.Read(Fixture("synthetic_2f0.cst"));
        var want = LpwSyntheticDevice.HarmonicLoads[0];
        for (int g = 0; g < LpwSyntheticDevice.Loads.Length; g++)
        {
            Assert.True((ds["GammaLoad_2f0"].ComplexValues[g] - want).Magnitude < 1e-3, $"load {g}: {ds["GammaLoad_2f0"].ComplexValues[g]}");
            // The plain mean, by contrast, is pulled far off by the first drive's |Γ| = 3.
            var plain = Enumerable.Range(0, LpwSyntheticDevice.Drives[g])
                .Select(p => LpwSyntheticDevice.Waves(g, p, 2)).Select(w => w[2] / w[3]).Aggregate(Complex.Zero, (a, b) => a + b)
                / LpwSyntheticDevice.Drives[g];
            Assert.True((plain - want).Magnitude > 0.1);
        }
    }

    [Fact]
    public void Cst_IsRefused_ForAMissingHarmonicRow_AndForAMeasurementTypeItDoesNotRead()
    {
        // Block 2, drive 3: its 2f0 row taken out.
        var lines = LpwSyntheticDevice.Cst(1).Split("\r\n").ToList();
        int begin = lines.Select((l, i) => (l, i)).Where(x => x.l == "#BEGIN").ElementAt(1).i;
        lines.RemoveAt(begin + 1 + 2 * 2 + 1);
        var ex = Assert.Throws<FormatException>(() => CstReader.Read(new StringReader(string.Join("\r\n", lines))));
        Assert.Contains("block 2, drive 3", ex.Message);

        string sourcePull = LpwSyntheticDevice.Cst(0).Replace("#MEASUREMENT TYPE: Power Sweep", "#MEASUREMENT TYPE: Source Pull");
        ex = Assert.Throws<FormatException>(() => CstReader.Read(new StringReader(sourcePull)));
        Assert.Contains("'Source Pull'", ex.Message);
    }

    // ── .satwave and the dispatcher ──────────────────────────────────────

    [Fact]
    public void Satwave_ReadsAsAOnePointLoadpull_ThroughTheLpcwaveColumns()
    {
        var sat = LoadpullFiles.Read(Fixture("synthetic.satwave"));
        var lpc = LoadpullFiles.Read(Fixture("synthetic.lpcwave"));
        Assert.Single(sat["GammaLoad"].ComplexValues);
        Assert.True((sat["GammaLoad"].ComplexValues[0] - LpwSyntheticDevice.Loads[0]).Magnitude < 1e-3);
        Assert.True(sat.Contains("ZSource"));
        foreach (var cube in new[] { "PavlDbm", "Pout_dBm", "Gt_dB", "PAE", "PinInc_dBm", "AMPM_deg" })
            for (int p = 0; p < LpwSyntheticDevice.Drives[0]; p++)
                Assert.Equal(At(lpc, cube, 0, p), At(sat, cube, 0, p), 6);
    }

    [Fact]
    public void LoadpullFiles_ReadsAnUnknownExtensionByContent()
    {
        string dir = Directory.CreateTempSubdirectory("lpw").FullName;
        try
        {
            foreach (var name in new[] { "synthetic_2f0.cst", "synthetic.lpcwave", "synthetic.satwave" })
            {
                string renamed = Path.Combine(dir, name + ".txt");
                File.Copy(Fixture(name), renamed);
                Assert.Equal(LoadpullFiles.FormatOfExtension(Path.GetExtension(name)), LoadpullFiles.Classify(renamed));
                Assert.Equal(LoadpullFiles.Read(Fixture(name))["Pout_dBm"].RealValues, LoadpullFiles.Read(renamed)["Pout_dBm"].RealValues);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── Gate 4: the compression reference ────────────────────────────────

    /// <summary>Pout at <paramref name="c"/> dB below a gain's peak, by hand: the peak drive, then linear interpolation in
    /// compression over the drives after it.</summary>
    private static double PoutAtCompression(int g, double c, Func<LpwSyntheticDevice.Metrics, double> gain, bool fromSmallSignal)
    {
        var m = Enumerable.Range(0, LpwSyntheticDevice.Drives[g]).Select(p => LpwSyntheticDevice.Expected(g, p)).ToArray();
        int ci = fromSmallSignal ? 0 : Array.IndexOf(m, m.MaxBy(gain)!);
        for (int k = ci + 1; k < m.Length; k++)
        {
            double d0 = gain(m[ci]) - gain(m[k - 1]), d1 = gain(m[ci]) - gain(m[k]);
            if (d1 >= c) return m[k - 1].PoutDbm + (c - d0) / (d1 - d0) * (m[k].PoutDbm - m[k - 1].PoutDbm);
        }
        return double.NaN;
    }

    [Fact]
    public void Compression_OfGpAndOfGt_DifferAndEachMatchesItsHandComputedPoint()
    {
        var surface = new LoadpullSurface(LoadpullFiles.Read(Fixture("synthetic_f0.cst")));
        foreach (var (reference, gain) in new (string, Func<LpwSyntheticDevice.Metrics, double>)[]
                 { (CompressionGain.Gt, m => m.GtDb), (CompressionGain.Gp, m => m.GpDb) })
            foreach (var type in new[] { CompressionType.Gmax, CompressionType.Gss })
            {
                var r = surface.Reduce(0, "Pout_dBm", ConstraintSpec.AtCompression(1, reference, type), SurfacePlane.Gamma);
                // A load whose drive-up never compresses that far is left out, on both sides.
                var reached = Enumerable.Range(0, LpwSyntheticDevice.Loads.Length)
                    .Where(g => !double.IsNaN(PoutAtCompression(g, 1, gain, type == CompressionType.Gss))).ToArray();
                Assert.Equal(reached, r.UsedGridIndices);
                Assert.NotEmpty(reached);
                for (int i = 0; i < r.Values.Length; i++)
                {
                    double want = PoutAtCompression(r.UsedGridIndices[i], 1, gain, type == CompressionType.Gss);
                    Assert.True(Math.Abs(r.Values[i] - want) < 0.01, $"{reference} {type} load {i}: {r.Values[i]} vs {want}");
                }
            }
        // Load 0 reaches 1 dB on both gains, at different drives: Gt peaks a drive earlier and falls faster.
        var gt = surface.Reduce(0, "Pout_dBm", ConstraintSpec.AtCompression(1, CompressionGain.Gt, CompressionType.Gmax), SurfacePlane.Gamma);
        var gp = surface.Reduce(0, "Pout_dBm", ConstraintSpec.AtCompression(1, CompressionGain.Gp, CompressionType.Gmax), SurfacePlane.Gamma);
        Assert.True(Math.Abs(gt.Values[0] - gp.Values[0]) > 0.3, $"Gt and Gp compress at different drives: {gt.Values[0]} vs {gp.Values[0]}");
    }

    [Fact]
    public void Fit_IsCachedPerCompressionReference_SoSwitchingRefits()
    {
        // Six loads are needed for a fit: the 3-load fixture twice over, the copy's loads moved.
        var ds = LoadpullFiles.Read(Fixture("synthetic_f0.cst"));
        var surface = new LoadpullSurface(Doubled(ds));
        var gt = surface.Fit(0, "Pout_dBm", ConstraintSpec.AtCompression(0.5, CompressionGain.Gt, CompressionType.Gmax), SurfacePlane.Gamma);
        var gp = surface.Fit(0, "Pout_dBm", ConstraintSpec.AtCompression(0.5, CompressionGain.Gp, CompressionType.Gmax), SurfacePlane.Gamma);
        var gss = surface.Fit(0, "Pout_dBm", ConstraintSpec.AtCompression(0.5, CompressionGain.Gt, CompressionType.Gss), SurfacePlane.Gamma);
        Assert.NotNull(gt); Assert.NotNull(gp); Assert.NotNull(gss);
        Assert.NotEqual(gt!.Rbf.NodeValues.ToArray(), gp!.Rbf.NodeValues.ToArray());
        Assert.NotEqual(gt.Rbf.NodeValues.ToArray(), gss!.Rbf.NodeValues.ToArray());
        Assert.Same(gt, surface.Fit(0, "Pout_dBm", ConstraintSpec.AtCompression(0.5, CompressionGain.Gt, CompressionType.Gmax), SurfacePlane.Gamma));
    }

    [Fact]
    public void ConstraintReach_SaysHowFarTheLoadsGo_SoTheCardCanSayWhyNothingIsDrawn()
    {
        var surface = new LoadpullSurface(Doubled(LoadpullFiles.Read(Fixture("synthetic_f0.cst"))));
        var beyond = surface.ConstraintReach(0, "Pout_dBm", ConstraintSpec.AtCompression(20));
        Assert.Equal((0, 6, 6), (beyond.Reached, beyond.Loads, beyond.Needed));
        Assert.InRange(beyond.Hi, 0.5, 20);
        // Just inside the window, every load the window counts gives a value and a surface fits.
        var inside = ConstraintSpec.AtCompression(beyond.Hi - 0.01);
        Assert.True(surface.ConstraintReach(0, "Pout_dBm", inside).Reached >= 6);
        Assert.NotNull(surface.Fit(0, "Pout_dBm", inside, SurfacePlane.Gamma));

        double pinDel = LpwSyntheticDevice.Expected(0, 4).PinDelDbm + 40;
        var high = surface.ConstraintReach(0, "Pout_dBm", ConstraintSpec.AtConstantMetric("PinDel_dBm", pinDel));
        Assert.Equal(0, high.Reached);
        Assert.True(high.Lo < high.Hi && high.Hi < pinDel);
    }

    [Fact]
    public void AFillGrid_IsWholeUnderItsFootprintCircle_SoTheFillReachesTheClippedEdge()
    {
        var surface = new LoadpullSurface(Doubled(LoadpullFiles.Read(Fixture("synthetic_f0.cst"))));
        var fit = surface.Fit(0, "Pout_dBm", ConstraintSpec.AtCompression(0.5), SurfacePlane.Gamma)!;
        var g = surface.Resample(fit, new ViewBox(-1, 1, -1, 1), 80, toFootprintEdge: true);
        double r = Assert.NotNull(g.ClipRadius);
        int n = g.XSpace.Length, crossed = 0;
        for (int yi = 0; yi < n - 1; yi++)
            for (int xi = 0; xi < n - 1; xi++)
            {
                var corners = new[] { (xi, yi), (xi + 1, yi), (xi + 1, yi + 1), (xi, yi + 1) };
                var inside = corners.Select(c => Math.Sqrt(g.XSpace[c.Item1] * g.XSpace[c.Item1] + g.YSpace[c.Item2] * g.YSpace[c.Item2]) <= r).ToArray();
                if (!inside.Contains(true)) continue;   // wholly outside the circle: clipped away anyway
                crossed++;
                Assert.All(corners, c => Assert.False(double.IsNaN(g.Values[c.Item2 * n + c.Item1]), $"cell {xi},{yi}"));
            }
        Assert.True(crossed > 0);
        // The iso-line grid keeps the footprint mask: nothing evaluated beyond it.
        Assert.Null(surface.Resample(fit, new ViewBox(-1, 1, -1, 1), 80).ClipRadius);
    }

    /// <summary>The loadpull with every load repeated once at a slightly moved Γ — six loads, enough for a fit.</summary>
    private static DataSet Doubled(DataSet ds)
    {
        var o = new DataSet();
        int n = ds["GammaLoad"].Axes[0].Length;
        var grid = new Axis("gridPoint", Enumerable.Range(0, 2 * n).Select(i => (double)i).ToArray());
        foreach (var (name, cube) in ds.CubesIn(""))
        {
            if (name.StartsWith("__") || cube.Axes.Count == 0 || cube.Axes[0].Name != "gridPoint") { o.Add(name, cube); continue; }
            var axes = new[] { grid }.Concat(cube.Axes.Skip(1)).ToArray();
            if (name is "GammaLoad" or "ZLoad")
            {
                var v = cube.ComplexValues;
                var moved = name == "GammaLoad" ? v.Select(g => g * 0.8 + new Complex(0.05, -0.05)).ToArray() : v;
                o.Add(name, new DataCube(axes, v.Concat(moved).ToArray()));
            }
            else if (cube.DataKind == DataKind.Complex) o.Add(name, new DataCube(axes, cube.ComplexValues.Concat(cube.ComplexValues).ToArray()));
            else o.Add(name, new DataCube(axes, cube.RealValues.Concat(cube.RealValues.Select(x => x + 0.01)).ToArray()));
        }
        return o;
    }

    // ── Gate 5: constant delivered input ─────────────────────────────────

    [Fact]
    public void Pout_AtConstantDeliveredInput_IsTheDevicesPoutAtThatInput()
    {
        // Γin depends on the drive alone, so every load takes the same delivered input at drive 4: held at that level,
        // the contour's scatter is each load's own Pout at drive 4.
        double pinDel = LpwSyntheticDevice.Expected(0, 4).PinDelDbm;
        var surface = new LoadpullSurface(LoadpullFiles.Read(Fixture("synthetic_2f0.cst")));
        var r = surface.Reduce(0, "Pout_dBm", ConstraintSpec.AtConstantMetric("PinDel_dBm", pinDel), SurfacePlane.Gamma);
        Assert.Equal(LpwSyntheticDevice.Loads.Length, r.Values.Length);
        for (int i = 0; i < r.Values.Length; i++)
            Assert.True(Math.Abs(r.Values[i] - LpwSyntheticDevice.Expected(r.UsedGridIndices[i], 4).PoutDbm) < 1e-3);
    }
}
