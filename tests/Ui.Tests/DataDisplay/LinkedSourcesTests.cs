// ================================================================
//  LinkedSourcesTests.cs — brief-ddl-4: family and versus markers on a
//  bias plane (companion matching), a contour's frequency, the selected
//  trial and an HB drive sweep as link sources and consumers. One test
//  per gate.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class LinkedSourcesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"crf_ddl4_{Guid.NewGuid():N}");
    public LinkedSourcesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static readonly Guid M1 = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static AxisSliceConfig Pin(string axis, int index, string? link = null, string? name = null)
        => new() { AxisName = axis, Role = AxisRole.PinToIndex, Index = index, Link = link, LinkName = name };
    private static AxisSliceConfig X(string axis) => new() { AxisName = axis, Role = AxisRole.KeepAsX };
    private static AxisSliceConfig Fam(string axis) => new() { AxisName = axis, Role = AxisRole.FamilyIterate };

    private static TraceConfig Cube(string src, string cube, string expr, params AxisSliceConfig[] slice)
        => new() { SourcePath = src, CubeName = cube, Expression = expr, CubeSlice = [.. slice] };

    private static PlotContainerConfig PlotOf(PlotType type, double left, params TraceConfig[] traces)
        => new() { PlotType = type, Left = left, Top = 0, Width = 300, Height = 300, Traces = [.. traces] };

    private string WriteCdd(string name, params PlotContainerConfig[] plots)
    {
        var config = new DataDisplayConfig
        {
            FormatVersion = DataDisplayConfig.CurrentFormatVersion,
            Tabs = [new TabConfig { Name = "Tab 1", Plots = [.. plots] }],
        };
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, JsonSerializer.Serialize(config, DataDisplayJson.Options));
        return path;
    }

    private static async Task<DisplayWindowViewModel> OpenAsync(string cdd)
    {
        var w = new DisplayWindowViewModel();
        await w.LoadAllAsync(cdd);
        return w;
    }

    private static Trace TraceAt(DisplayWindowViewModel w, int plot, int trace = 0)
        => w.Tabs[0].DataDisplay.Plots[plot].PlotVM.Plot.Traces[trace];

    private static double[] Ys(Trace t) => [.. t.Points.Select(p => (double)p.Y)];

    /// <summary>Drawn points are single precision.</summary>
    private static void AssertClose(double[] expected, double[] actual)
        => Assert.Equal(expected, actual, (a, b) => Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Abs(a)));

    // ── Gates 1 and 2: the FET bias plane ─────────────────────────────────────────────────────────

    private static readonly double[] BiasVgs = [-1.0, -0.7, -0.38, -0.1];
    private static readonly double[] BiasVds = [0.0, 3.0, 6.1, 9.0];
    private const int NFreq = 5;

    /// <summary>
    /// Model data: <c>Y[point, freq, 2, 2]</c> (Re Y21 = 100·point + freq index), <c>vgs[point]</c>, <c>vds[point]</c>,
    /// <c>Idp[point]</c>, and a gridded I-V family <c>Id[vgsG, vdsG]</c> on a finer plane. Point 10 is (−0.38 V, 6.1 V).
    /// </summary>
    private string ModelData()
    {
        int np = BiasVgs.Length * BiasVds.Length;
        var point = new Axis("point", [.. Enumerable.Range(0, np).Select(i => (double)i)]);
        var freq  = new Axis("freq", [.. Enumerable.Range(0, NFreq).Select(i => 1e9 * (i + 1))], "Hz");
        var ii    = new Axis("i", [1, 2]);
        var jj    = new Axis("j", [1, 2]);
        var y = new Complex[np * NFreq * 4];
        for (int p = 0; p < np; p++)
            for (int f = 0; f < NFreq; f++)
                for (int k = 0; k < 4; k++)
                    y[((p * NFreq) + f) * 4 + k] = new Complex(100 * p + f + 0.1 * k, k);
        var ds = new DataSet();
        ds.Add("Y", new DataCube([point, freq, ii, jj], y));
        ds.Add("vgs", new DataCube([point], [.. Enumerable.Range(0, np).Select(p => BiasVgs[p / BiasVds.Length])]) { Unit = "V" });
        ds.Add("vds", new DataCube([point], [.. Enumerable.Range(0, np).Select(p => BiasVds[p % BiasVds.Length])]) { Unit = "V" });
        ds.Add("Idp", new DataCube([point], [.. Enumerable.Range(0, np).Select(p => 0.01 * p + 0.001)]));

        var vgsG = new Axis("vgs", [-1.0, -0.75, -0.5, -0.4, -0.25, 0.0], "V");
        var vdsG = new Axis("vds", [0.0, 2.0, 4.0, 6.0, 8.0, 10.0], "V");
        var id = new double[vgsG.Length * vdsG.Length];
        for (int a = 0; a < vgsG.Length; a++)
            for (int b = 0; b < vdsG.Length; b++) id[a * vdsG.Length + b] = (vgsG.Values[a] + 1.2) * vdsG.Values[b];
        ds.Add("Id", new DataCube([vgsG, vdsG], id));

        string path = Path.Combine(_dir, "model.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return path;
    }

    /// <summary>The S-parameter detail: <c>Re Y[@m1, :, 2, 1]</c> against frequency, stored at point 0.</summary>
    private static TraceConfig Detail(string npy)
    {
        var t = Cube(npy, "Y", "Y[@m1, :, 1, 0]", Pin("point", 0, M1.ToString("D"), "m1"), X("freq"), Pin("i", 1), Pin("j", 0));
        t.CubeTransform = CubeTransform.Real;
        return t;
    }

    private static double[] Y21Row(int point) => [.. Enumerable.Range(0, NFreq).Select(f => 100.0 * point + f + 0.1 * 2)];

    [Fact]
    public async Task Gate1_IvFamily_ToSParameters_TakesTheNearestBiasPoint_AndSaysWhich()
    {
        string npy = ModelData();
        // m1 on the Vgs = −0.4 V curve (curve 3) at Vds = 6 V.
        var family = Cube(npy, "Id", "Id[:, :]", Fam("vgs"), X("vds"));
        family.Markers.Add(new MarkerConfig { Id = M1.ToString("D"), Name = "m1", PositionStaticX = 6.0f, PositionStaticY = 3 });
        var w = await OpenAsync(WriteCdd("iv.cdd", PlotOf(PlotType.Rect, 0, family), PlotOf(PlotType.Rect, 320, Detail(npy))));

        var detail = TraceAt(w, 1);
        Assert.Equal(10, detail.Slice![0].Index);                    // (−0.38 V, 6.1 V) is the nearest to (−0.4, 6)
        AssertClose(Y21Row(10), Ys(detail));
        Assert.False(detail.LinkFrozen);
        Assert.Equal("point 10 (vds 6.1 V, vgs -380 mV; m1 at 6 V, -400 mV).", detail.LinkNote);
    }

    [Fact]
    public async Task Gate2_Versus_TheMarkersSampleIsThePoint_Exactly()
    {
        string npy = ModelData();
        var versus = Cube(npy, "Idp", "Idp", X("point"));
        versus.XSpec = "vds";
        versus.Markers.Add(new MarkerConfig { Id = M1.ToString("D"), Name = "m1" });
        var w = await OpenAsync(WriteCdd("versus.cdd", PlotOf(PlotType.Rect, 0, versus), PlotOf(PlotType.Rect, 320, Detail(npy))));

        var overview = TraceAt(w, 0);
        Assert.True(overview.PlaceMarkerAtSample(overview.Markers[0], 6));   // point 6: (−0.7 V, 6.1 V)
        w.ResolveLinks();

        var detail = TraceAt(w, 1);
        Assert.Equal(6, detail.Slice![0].Index);
        AssertClose(Y21Row(6), Ys(detail));
        Assert.Null(detail.LinkNote);

        // The marker also supplies the X operand's value, in its cube's unit (R-ddl4-2).
        var site = new LinkDocument.MarkerSite(0, w.Tabs[0].DataDisplay.Plots[0].PlotVM.Plot, overview, overview.Markers[0]);
        var value = MarkerLinkSource.Resolve(site, new LibraryDataSources(w.DataSourceLibrary));
        Assert.Contains(value.Coordinates, c => c.Axis == "vds" && c.Value == 6.1 && c.Unit == "V");
    }

    // ── Gate 3: a three-frequency loadpull's contours ─────────────────────────────────────────────

    private const int N = 7, NPin = 20;
    private static readonly double[] Freqs = [1.8e9, 2.0e9, 2.2e9];
    private static Complex Load(int g) => new(-0.6 + 0.2 * (g / N), -0.6 + 0.2 * (g % N));

    private string ThreeFrequencyLoadpull()
    {
        var freq = new Axis("freq", Freqs, "Hz");
        var grid = new Axis("gridPoint", [.. Enumerable.Range(0, N * N).Select(i => (double)i)]);
        var pin  = new Axis("pinStep", [.. Enumerable.Range(0, NPin).Select(p => -5.0 + 2 * p)], "dBm");
        double Pout(int f, Complex g, double p)
        {
            double gain = 15 - f - 8 * Math.Pow(Complex.Abs(g - new Complex(0.4, 0.3)), 2);
            double lin = Math.Pow(10, (p + gain) / 10), sat = Math.Pow(10, 3.6);
            return 10 * Math.Log10(lin / Math.Pow(1 + Math.Pow(lin / sat, 4), 0.25));
        }
        double[] Metric(Func<int, Complex, double, double> m)
        {
            var v = new double[Freqs.Length * N * N * NPin];
            for (int f = 0; f < Freqs.Length; f++)
                for (int g = 0; g < N * N; g++)
                    for (int p = 0; p < NPin; p++) v[(f * N * N + g) * NPin + p] = m(f, Load(g), pin.Values[p]);
            return v;
        }
        Complex[] Loads(Func<Complex, Complex> z)
            => [.. Enumerable.Range(0, Freqs.Length * N * N).Select(i => z(Load(i % (N * N))))];
        var ds = new DataSet();
        ds.Add("GammaLoad", new DataCube([freq, grid], Loads(g => g)));
        ds.Add("ZLoad",     new DataCube([freq, grid], Loads(g => 50 * (1 + g) / (1 - g))));
        ds.Add("PavlDbm",   new DataCube([freq, grid, pin], Metric((f, g, p) => p)));
        ds.Add("Pout_dBm",  new DataCube([freq, grid, pin], Metric(Pout)));
        ds.Add("Gt_dB",     new DataCube([freq, grid, pin], Metric((f, g, p) => Pout(f, g, p) - p)));
        // The overview: small-signal gain against frequency, on the loadpull's own frequency axis.
        ds.Add("Gss", new DataCube([freq], [15.0, 14.0, 13.0]));
        string path = Path.Combine(_dir, "lp3.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return path;
    }

    [Fact]
    public async Task Gate3_ContourFrequency_FollowsAGainMarker_AndRefitsOnRelease()
    {
        string npy = ThreeFrequencyLoadpull();
        var gain = Cube(npy, "Gss", "Gss", X("freq"));
        gain.Markers.Add(new MarkerConfig { Id = M1.ToString("D"), Name = "m1" });
        var contour = new TraceConfig
        {
            SourcePath = npy,
            ContourTrace = new ContourTraceConfig
            {
                MetricName = "Pout_dBm", FreqIndex = 0, FreqLink = M1.ToString("D"), FreqLinkName = "m1",
                LevelMode = ContourLevelMode.Count, LevelCount = 5,
            },
        };
        var w = await OpenAsync(WriteCdd("contour.cdd", PlotOf(PlotType.Rect, 0, gain), PlotOf(PlotType.Smith, 320, contour)));
        var overview = TraceAt(w, 0);
        var cd = TraceAt(w, 1).ContourData!;

        Assert.True(overview.PlaceMarkerAtSample(overview.Markers[0], 1));
        w.FollowLinks(released: true);
        Assert.Equal(1, cd.FreqIndex);
        Assert.True(cd.Grid is not null, cd.Status ?? TraceAt(w, 1).LinkNote ?? "no grid");

        // Dragged to the third: a contour refit waits for the release.
        Assert.True(overview.PlaceMarkerAtSample(overview.Markers[0], 2));
        w.FollowLinks(released: false);
        Assert.Equal(1, cd.FreqIndex);
        w.FollowLinks(released: true);
        Assert.Equal(2, cd.FreqIndex);
        Assert.Contains("Updates on release.", TraceAt(w, 1).LinkNote);

        // The link is saved with the contour.
        string saved = Path.Combine(_dir, "contour-saved.cdd");
        await w.SaveAllAsync(saved);
        var config = JsonSerializer.Deserialize<DataDisplayConfig>(File.ReadAllText(saved), DataDisplayJson.Options)!;
        var ct = config.Tabs[0].Plots[1].Traces[0].ContourTrace!;
        Assert.Equal((M1.ToString("D"), "m1", 2), (ct.FreqLink, ct.FreqLinkName, ct.FreqIndex));
    }

    // ── Gate 4: the selected trial ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gate4_SelectedTrial_DrivesAPin_AndFreezesWhenCleared()
    {
        const int Trials = 20, F = 4;
        var trial = new Axis("trial", [.. Enumerable.Range(1, Trials).Select(i => (double)i)]);
        var freq  = new Axis("freq", [.. Enumerable.Range(0, F).Select(i => 1e9 * (i + 1))], "Hz");
        var ds = new DataSet();
        ds.Add("Resp", new DataCube([trial, freq], [.. Enumerable.Range(0, Trials * F).Select(k => (double)k)]));
        string npy = Path.Combine(_dir, "mc.npy");
        DataSetExporter.Export(ds, npy, ExportFormat.Npy);

        var detail = Cube(npy, "Resp", "Resp[@selected, :]", Pin("trial", 0, LinkDocument.SelectionId, "selected"), X("freq"));
        var w = await OpenAsync(WriteCdd("mc.cdd", PlotOf(PlotType.Rect, 0, detail)));
        var t = TraceAt(w, 0);
        double[] Row(int trialNumber) => [.. Enumerable.Range(0, F).Select(f => (double)((trialNumber - 1) * F + f))];

        try
        {
            // What clicking the bar holding trial 12 does.
            TrialSelection.Shared.Select(npy, [12]);
            Assert.Equal(11, t.Slice![0].Index);
            Assert.Equal(Row(12), Ys(t));
            Assert.Null(t.LinkNote);

            TrialSelection.Shared.Select(npy, [17, 12, 15]);
            Assert.Equal(Row(12), Ys(t));
            Assert.Equal("3 trials selected; showing trial 12, the lowest.", t.LinkNote);

            TrialSelection.Shared.Clear(npy);
            Assert.Equal(Row(12), Ys(t));
            Assert.Equal("No trial is selected; showing trial 12, the last one selected.", t.LinkNote);
            Assert.False(t.LinkFrozen);
        }
        finally { TrialSelection.Shared.Clear(npy); }
    }

    // ── Gate 5: an HB drive sweep ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gate5_HbDrive_MeasurementsAndVoltagesShareTheSweepAxis_AndTheSpectrumStepsWithTheMarker()
    {
        // The shipped power amplifier example's HB sweep, run as the CLI runs it.
        string ws = Path.Combine(_dir, "hb");
        CopyDirectory(Path.Combine(RepoRoot(), "examples", "Harmonic Balance"), ws);
        string npy = Path.Combine(_dir, "pa.npy");
        var (exit, _, err) = CircuitRF.Ui.Tests.Optimization.OptCli.Run(
            "hb", Path.Combine(ws, "PowerAmplifier", "schematic", "PowerAmplifier.csch"), "-o", npy);
        Assert.True(exit == 0, err);

        // R-ddl4-6: the measurement cubes and the node voltages carry the same sweep axis — name, length, values.
        DataSet ds;
        using (var fs = File.OpenRead(npy)) ds = DataSetImporter.ImportNpy(fs);
        var sweep = ds["V"].Axes[0];
        var drive = ds["Pout_dBm"].Axes.Single();
        Assert.Equal((sweep.Name, sweep.Length, sweep.Unit), (drive.Name, drive.Length, drive.Unit));
        Assert.Equal(sweep.Values, drive.Values);

        // A marker on Pout against Pin drives V[@m1, node, :] by index.
        var pout = Cube(npy, "Pout_dBm", "Pout_dBm", X(sweep.Name));
        pout.Markers.Add(new MarkerConfig { Id = M1.ToString("D"), Name = "m1" });
        var spectrum = Cube(npy, "V", "V[@m1, 1, :]", Pin(sweep.Name, 0, M1.ToString("D"), "m1"), Pin("node", 1), X("harmonic"));
        spectrum.CubeTransform = CubeTransform.Mag;
        var w = await OpenAsync(WriteCdd("hb.cdd", PlotOf(PlotType.Rect, 0, pout), PlotOf(PlotType.Rect, 320, spectrum)));
        var overview = TraceAt(w, 0);
        var v = ds["V"];
        double[] Spectrum(int k) => [.. Enumerable.Range(0, v.Axes[2].Length).Select(h => v[k, 1, h].ComplexValue!.Value.Magnitude)];

        foreach (int k in new[] { 5, 20 })
        {
            Assert.True(overview.PlaceMarkerAtSample(overview.Markers[0], k));
            w.ResolveLinks();
            var t = TraceAt(w, 1);
            Assert.Equal(k, t.Slice![0].Index);
            AssertClose(Spectrum(k), Ys(t));
        }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "VERSION"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to), overwrite: true);
    }
}
