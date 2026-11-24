// ================================================================
//  LoadpullOffGridLinkTests.cs — brief-ddl-3: a detail plot and a summary
//  table read a loadpull at a marker anywhere on the Γ plane. One test
//  per gate, on an analytic 7×7 loadpull so a read between loads has a
//  truth to be compared with.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using CircuitRF.Cli;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;
using RfCore.Export;
using RfCore.Loadpull;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class LoadpullOffGridLinkTests : IDisposable
{
    private const int N = 7, NPin = 36;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"crf_ddl3_{Guid.NewGuid():N}");
    public LoadpullOffGridLinkTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static readonly double[] Pins = [.. Enumerable.Range(0, NPin).Select(p => -5.0 + p)];
    private static Complex Load(int g) => new(-0.6 + 0.2 * (g / N), -0.6 + 0.2 * (g % N));
    private static int Node(int i, int k) => i * N + k;

    private static double Pout(Complex g, double pin)
    {
        double gain = 15 - 8 * Math.Pow(Complex.Abs(g - new Complex(0.5, 0.4)), 2);
        double psat = 40 - 10 * Math.Pow(Complex.Abs(g - new Complex(0.6, 0.0)), 2);
        double lin = Math.Pow(10, (pin + gain) / 10), sat = Math.Pow(10, psat / 10);
        return 10 * Math.Log10(lin / Math.Pow(1 + Math.Pow(lin / sat, 4), 0.25));
    }

    private string Run()
    {
        var grid = new Axis("gridPoint", [.. Enumerable.Range(0, N * N).Select(i => (double)i)]);
        var pin  = new Axis("pinStep", Pins, "dBm");
        double[] Cube(Func<Complex, double, double> f)
        {
            var v = new double[N * N * NPin];
            for (int g = 0; g < N * N; g++) for (int p = 0; p < NPin; p++) v[g * NPin + p] = f(Load(g), Pins[p]);
            return v;
        }
        var ds = new DataSet();
        ds.Add("GammaLoad",  new DataCube([grid], [.. Enumerable.Range(0, N * N).Select(Load)]));
        ds.Add("ZLoad",      new DataCube([grid], [.. Enumerable.Range(0, N * N).Select(g => 50 * (1 + Load(g)) / (1 - Load(g)))]));
        ds.Add("PavlDbm",    new DataCube([grid, pin], Cube((g, p) => p)));
        ds.Add("Pout_dBm",   new DataCube([grid, pin], Cube(Pout)));
        ds.Add("Gt_dB",      new DataCube([grid, pin], Cube((g, p) => Pout(g, p) - p)));
        ds.Add("Efficiency", new DataCube([grid, pin], Cube((g, p) => 100 * Math.Pow(10, Pout(g, p) / 10) / 1000 / 20)));
        ds.Add("PAE",        new DataCube([grid, pin], Cube((g, p) => 100 * (Math.Pow(10, Pout(g, p) / 10) - Math.Pow(10, p / 10)) / 1000 / 20)));
        string path = Path.Combine(_dir, "lp.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return path;
    }

    private static readonly Guid M1 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>m1 on the GammaLoad Smith trace at <paramref name="at"/> — free or bound — and <c>Pout_dBm[@m1, :]</c>.</summary>
    private string Cdd(string npy, Complex at, bool free, TableReadMode read = TableReadMode.Interp, bool table = false)
    {
        var loads = new TraceConfig { SourcePath = npy, CubeName = "GammaLoad", Expression = "GammaLoad",
            CubeSlice = [new AxisSliceConfig { AxisName = "gridPoint", Role = AxisRole.KeepAsX }] };
        loads.Markers.Add(new MarkerConfig { Id = M1.ToString("D"), Name = "m1", FreePosition = free,
                                             PositionStaticX = (float)at.Real, PositionStaticY = (float)at.Imaginary });
        var detail = new TraceConfig { SourcePath = npy, CubeName = "Pout_dBm", Expression = "Pout_dBm[@m1, :]",
            CubeSlice = [new AxisSliceConfig { AxisName = "gridPoint", Role = AxisRole.PinToIndex, Link = M1.ToString("D"), LinkName = "m1" },
                         new AxisSliceConfig { AxisName = "pinStep", Role = AxisRole.KeepAsX }] };
        var plots = new List<PlotContainerConfig>
        {
            new() { PlotType = PlotType.Smith, Width = 300, Height = 300, Traces = [loads] },
            new() { PlotType = PlotType.Rect, Left = 320, Width = 400, Height = 300, TableReadMode = read, Traces = [detail] },
        };
        if (table)
            plots.Add(new PlotContainerConfig
            {
                PlotType = PlotType.Table, Top = 320, Width = 500, Height = 120, TableReadMode = read,
                TableOptimum = TableOptimum.AtMarker, TableMarkerLink = M1.ToString("D"), TableMarkerLinkName = "m1",
                Traces = [.. new[] { "Pout_dBm", "Gt_dB", "PAE" }.Select(m => new TraceConfig
                {
                    SourcePath = DataSourceRef.Selected, SummaryColumn = new SummaryColumnConfig { MetricName = m, Header = m },
                }).Append(new TraceConfig { SourcePath = DataSourceRef.Selected,
                                            SummaryColumn = new SummaryColumnConfig { Kind = SummaryColumnKind.Zload, Header = "ZL" } })],
            });
        var config = new DataDisplayConfig
        {
            FormatVersion = DataDisplayConfig.CurrentFormatVersion, SelectedDataSource = npy,
            Tabs = [new TabConfig { Plots = plots }],
        };
        string path = Path.Combine(_dir, $"lp{Guid.NewGuid():N}.cdd");
        File.WriteAllText(path, JsonSerializer.Serialize(config, DataDisplayJson.Options));
        return path;
    }

    private static async Task<DisplayWindowViewModel> OpenAsync(string cdd)
    {
        var w = new DisplayWindowViewModel();
        await w.LoadAllAsync(cdd);
        return w;
    }

    private static Trace Detail(DisplayWindowViewModel w) => w.Tabs[0].DataDisplay.Plots[1].PlotVM.Plot.Traces[0];

    /// <summary>A measured drive-up, linearly interpolated at <paramref name="x"/> dBm of available power.</summary>
    private static double Measured(Complex load, double x)
    {
        int j = Math.Clamp((int)Math.Floor(x - Pins[0]), 0, NPin - 2);
        double t = (x - Pins[j]) / (Pins[j + 1] - Pins[j]);
        return Pout(load, Pins[j]) * (1 - t) + Pout(load, Pins[j + 1]) * t;
    }

    private static IEnumerable<(double X, double Y)> Curve(Trace t)
        => t.CubeXValues!.Zip(t.CubeReal!).Where(p => p.First >= Pins[0] && p.First <= Pins[^1]);

    /// <summary>Gate 1: at a load the synthesised drive-up is the measured one; between four it is the truth, and lies
    /// between its neighbours.</summary>
    [Fact]
    public async Task Gate1_AKnownSurface()
    {
        string npy = Run();
        var node = Load(Node(2, 3));
        var atNode = Detail(await OpenAsync(Cdd(npy, node, free: true)));
        Assert.True(atNode.IsSynthesized);
        Assert.NotEmpty(Curve(atNode));
        Assert.All(Curve(atNode), p => Assert.InRange(p.Y - Measured(node, p.X), -0.05, 0.05));

        var centre = new Complex(-0.3, -0.3);
        var between = Detail(await OpenAsync(Cdd(npy, centre, free: true)));
        Complex[] around = [new(-0.4, -0.4), new(-0.4, -0.2), new(-0.2, -0.4), new(-0.2, -0.2)];
        Assert.All(Curve(between), p =>
        {
            Assert.InRange(p.Y - Pout(centre, p.X), -0.2, 0.2);
            var n = around.Select(a => Measured(a, p.X)).ToList();
            Assert.InRange(p.Y, n.Min() - 1e-9, n.Max() + 1e-9);
        });
        Assert.Equal($"Pout_dBm @ m1 (interpolated, Γ {Complex.Abs(centre):0.##}∠{centre.Phase * 180 / Math.PI:0}°)",
                     TraceLabeler.QuantityFor(between));
        Assert.Equal(LineType.Dashed, between.DrawnLineType);
    }

    /// <summary>Gate 2: a marker snapped to a load draws that load's measured curve, solid, with no "interpolated".</summary>
    [Fact]
    public async Task Gate2_SnappedIsMeasured()
    {
        int node = Node(4, 1);
        var t = Detail(await OpenAsync(Cdd(Run(), Load(node), free: false)));
        Assert.False(t.IsSynthesized);
        Assert.Equal(node, t.Slice![0].Index);
        Assert.Equal(Pins.Select(p => Pout(Load(node), p)), t.CubeReal!, (a, b) => Math.Abs(a - b) < 1e-9);
        Assert.Equal(LineType.Solid, t.DrawnLineType);
        Assert.DoesNotContain("interpolated", TraceLabeler.QuantityFor(t));
    }

    /// <summary>Gate 3: read Nearest, a free marker draws the nearest load's measured curve and the card names it.</summary>
    [Fact]
    public async Task Gate3_NearestNamesTheNode()
    {
        var t = Detail(await OpenAsync(Cdd(Run(), new Complex(-0.27, -0.31), free: true, TableReadMode.Nearest)));
        int node = Node(2, 1);                                  // Γ = −0.2 − j0.4
        Assert.False(t.IsSynthesized);
        Assert.Equal(node, t.Slice![0].Index);
        Assert.Contains("gridPoint 15", t.LinkNote);
    }

    /// <summary>Gate 4: beyond the outermost loads the curve is an extrapolation, dashed, and the card says so.</summary>
    [Fact]
    public async Task Gate4_OutsideTheHullIsExtrapolated()
    {
        var t = Detail(await OpenAsync(Cdd(Run(), new Complex(0.75, 0.0), free: true)));
        Assert.True(t.IsSynthesized);
        Assert.Equal(LineType.Dashed, t.DrawnLineType);
        Assert.Contains("Extrapolated: m1 is 0.15 (|ΔΓ|) outside the measured loads.", t.LinkNote);
    }

    /// <summary>Gate 5: At marker, every column is the surface read at the marker's load, in both read modes.</summary>
    [Theory]
    [InlineData(TableReadMode.Interp)]
    [InlineData(TableReadMode.Nearest)]
    public async Task Gate5_TheTableAtTheMarker(TableReadMode read)
    {
        string npy = Run();
        var at = new Complex(-0.3, -0.3);
        var w = await OpenAsync(Cdd(npy, at, free: true, read, table: true));
        var table = w.Tabs[0].DataDisplay.Plots[2].PlotVM.Plot;
        // The marker's coordinate as it holds it (a float position), which is what the table reads at.
        var z = table.TableMarkerPosition!.Z;
        Assert.True(Complex.Abs(table.TableMarkerPosition.Gamma - at) < 1e-6);

        var surface = new LoadpullSurface(PlotSourceFile.Load(npy).Data!);
        var constraint = ConstraintSpec.AtCompression(3.0);
        foreach (var t in table.Traces.Where(x => x.SummaryColumn!.Kind == SummaryColumnKind.Metric))
            Assert.Equal(surface.MetricAtCoord(0, t.SummaryColumn!.MetricName, z, constraint, SurfacePlane.Z,
                                               nearest: read == TableReadMode.Nearest),
                         t.SummaryColumn!.CellsReal![0], 9);
        var zl = table.Traces.Single(x => x.SummaryColumn!.Kind == SummaryColumnKind.Zload).SummaryColumn!.CellsComplex![0];
        Assert.Equal(z, zl);
    }

    /// <summary>Gate 6: a `.cdd` with a free m1 draws the same synthesised curve headlessly as in the window.</summary>
    [Fact]
    public async Task Gate6_HeadlessDrawsTheSameModel()
    {
        string cdd = Cdd(Run(), new Complex(-0.3, -0.3), free: true);
        var window = Detail(await OpenAsync(cdd));

        var config = JsonSerializer.Deserialize<DataDisplayConfig>(File.ReadAllText(cdd), DataDisplayJson.Options)!;
        var (sources, refusal) = CddSources.Bind(cdd, config, config.Tabs, [0], []);
        Assert.Null(refusal);
        var plots = config.Tabs[0].Plots.Select(pc => PlotConfigLoader.LoadPlot(pc, sources!)).ToList();
        LinkResolve.Resolve(new LinkDocument([new LinkDocument.Tab("Tab 1", plots)]), sources!);
        var headless = plots[1].Traces[0];

        Assert.True(headless.IsSynthesized);
        Assert.Equal(window.CubeXValues, headless.CubeXValues);
        Assert.Equal(window.CubeReal, headless.CubeReal);
    }
}
