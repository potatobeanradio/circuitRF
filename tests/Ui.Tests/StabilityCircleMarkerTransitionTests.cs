// ================================================================
//  StabilityCircleMarkerTransitionTests.cs — brief-dd-stability-circle-marker-transition
//
//  Switching a trace from S(1,1) to a stability circle sent its marker to 0 Hz, where every lookup
//  read NaN. Two independent faults, both in the S-param → circle direction:
//
//   1. A CUBE marker's frequency lives in its POSITION — PlotControl deliberately never assigns
//      Marker.Freq for a cube trace (CubeMarkerIndex re-derives the sample on every read) and
//      markers are constructed with freq: 0.0. Nothing read it out before the cube binding was
//      torn down, so the frequency was simply lost. Fixed by Trace.CaptureMarkerFrequencies(),
//      called before the cube identity is cleared.
//   2. The Derived setter's own snap loop matched frequencies with
//      `f == m.Freq - 1e-6` — an exact float comparison against a SHIFTED value, so it never
//      matched and every marker fell through to `Data.Frequencies.Length - 1`. It also zeroed
//      PositionStatic first, so "nearest point on the circle" was measured from the origin and the
//      marker teleported. Now: nearest FREQUENCY sample, position kept, shortest move onto that
//      frequency's circle.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class StabilityCircleMarkerTransitionTests : IDisposable
{
    private readonly string _dir;

    public StabilityCircleMarkerTransitionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crf-stbmark-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly double[] Freqs = [1e9, 1.19e9, 1.38e9, 1.57e9, 1.76e9];

    /// <summary>A 4-port run whose 1→2 sub-network is potentially unstable, so the circles exist.</summary>
    private static DataSet GroupedRun(string group = "SP1", int nPorts = 4)
    {
        var s = new Complex[Freqs.Length * nPorts * nPorts];
        var rnd = new Random(5);
        for (int f = 0; f < Freqs.Length; f++)
            for (int i = 0; i < nPorts; i++)
                for (int j = 0; j < nPorts; j++)
                    s[f * nPorts * nPorts + i * nPorts + j] =
                        i == j ? Complex.FromPolarCoordinates(0.5 + 0.05 * f, -1.0 - 0.2 * f)
                               : new Complex(rnd.NextDouble() * 0.3, rnd.NextDouble() * 0.2);
        for (int f = 0; f < Freqs.Length; f++)
            s[f * nPorts * nPorts + 1 * nPorts + 0] = Complex.FromPolarCoordinates(3.0, 1.4 + 0.1 * f);

        var ds = new DataSet();
        ds.AddToGroup(group, "S", new DataCube(
            [new Axis("freq", Freqs, "Hz"),
             new Axis("i", Enumerable.Range(1, nPorts).Select(v => (double)v).ToArray(), ""),
             new Axis("j", Enumerable.Range(1, nPorts).Select(v => (double)v).ToArray(), "")],
            s));
        ds.AddToGroup(group, "Z0", new DataCube(
            [new Axis("port", Enumerable.Range(1, nPorts).Select(v => (double)v).ToArray(), "")],
            Enumerable.Repeat(new Complex(50, 0), nPorts).ToArray()));
        return ds;
    }

    private async Task<TraceRowViewModel> S11RowAsync()
    {
        string p = Path.Combine(_dir, "run.npy");
        DataSetExporter.Export(GroupedRun(), p, ExportFormat.Npy);
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var trace = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        { SourcePath = p, CubeName = "SP1.S", Slice = null, Transform = CubeTransform.None };
        var plot = new Plot(PlotType.Smith, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var insp = new PlotInspectorViewModel(plot, () => { }, lib);
        insp.RebuildAndNotify();
        var row = insp.Traces[0];
        row.SelectedGroup = row.AvailableGroups.First(g => g.Contains("SP1"));
        Select(row, "S(1,1)");
        return row;
    }

    private static void Select(TraceRowViewModel row, string label)
    {
        var target = row.AvailableSignals.First(x => x.Label == label);
        if (!ReferenceEquals(row.SelectedSignal, target)) { row.SelectedSignal = target; return; }
        row.SelectedSignal = row.AvailableSignals.First(x => x.Label != label);
        row.SelectedSignal = target;
    }

    /// <summary>A marker exactly as PlotControl builds one on a cube Smith trace: constructed with
    /// freq 0.0, carrying its identity purely in the snapped position.</summary>
    private static Marker AddCubeMarker(Trace t, int sample)
    {
        var m = new Marker(t, 0.0, isMulti: false, isDelta: false, index: 1, FreqUnit.GHz)
        { PositionStatic = t.Points[sample] };
        m.UseNormalizedImpedance = false;
        t.Markers.Add(m);
        return m;
    }

    private static double DistanceFromPerimeter(Trace t, System.Numerics.Vector2 p, int fi)
    {
        var c = t.StabilityCircleCentres[fi];
        double r = Math.Abs(t.StabilityCircleRadii[fi]);
        double d = Math.Sqrt((p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y));
        return Math.Abs(d - r);
    }

    // ---- The reported bug ---------------------------------------------------

    [Fact]
    public async Task CubeMarker_KeepsItsFrequency_AndLandsOnThatFrequencysCircle()
    {
        var row = await S11RowAsync();
        const int sample = 1;                       // 1.19 GHz — deliberately NOT the last sample
        var m   = AddCubeMarker(row.Trace, sample);
        Assert.Equal(0.0, m.Freq);                  // pre-condition: cube markers carry no frequency

        Select(row, "Source Stability Circles");

        var t = row.Trace;
        Assert.Equal(Freqs[sample], m.Freq);        // not 0, and not Freqs[^1]
        Assert.NotEmpty(t.StabilityCircleCentres);
        Assert.True(DistanceFromPerimeter(t, m.PositionStatic, sample) < 1e-5,
                    "the marker must sit on the circle for ITS OWN frequency");
    }

    [Fact]
    public async Task Marker_MovesToTheNearestPointOnThatCircle_NotTheOrigin()
    {
        var row = await S11RowAsync();
        const int sample = 1;
        var before = row.Trace.Points[sample];
        var m = AddCubeMarker(row.Trace, sample);

        Select(row, "Source Stability Circles");

        // The nearest point on a circle to an external point is the radial projection — computed
        // here from the circle geometry, not read back from the code under test.
        var t = row.Trace;
        var c = t.StabilityCircleCentres[sample];
        double r  = Math.Abs(t.StabilityCircleRadii[sample]);
        double dx = before.X - c.X, dy = before.Y - c.Y;
        double dc = Math.Sqrt(dx * dx + dy * dy);
        Assert.True(dc > 1e-9, "pre-condition: the old position is not the circle centre");

        Assert.Equal(c.X + dx / dc * r, m.PositionStatic.X, precision: 5);
        Assert.Equal(c.Y + dy / dc * r, m.PositionStatic.Y, precision: 5);
    }

    [Fact]
    public async Task MarkerBoxLines_ShowTheFrequency_AndNoNaN()
    {
        var row = await S11RowAsync();
        var m = AddCubeMarker(row.Trace, 1);

        Select(row, "Source Stability Circles");

        var lines = row.Trace.BuildMarkerBoxLines(m, FreqUnit.GHz).Select(l => l.Text).ToList();
        Assert.DoesNotContain(lines, l => l.Contains("NaN", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("1.19", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("freq=0 ", StringComparison.Ordinal));
    }

    // ---- The frequency match itself, at model level -------------------------

    [Fact]
    public void NetworkMarker_KeepsItsOwnFrequency_NotTheLastSample()
    {
        // Pins fault 2 directly: `f == m.Freq - 1e-6` never matched, so every marker was snapped to
        // the last frequency's circle while its box still claimed the original one.
        var snp = new SNP(Freqs, 2, MatrixType.S, MatrixFormat.MA, new Complex(50, 0));
        for (int f = 0; f < Freqs.Length; f++)
        {
            snp.Matrices[f][0, 0] = Complex.FromPolarCoordinates(0.5 + 0.05 * f, -1.0 - 0.2 * f);
            snp.Matrices[f][0, 1] = new Complex(0.05, 0.0);
            snp.Matrices[f][1, 0] = Complex.FromPolarCoordinates(3.0, 1.4 + 0.1 * f);
            snp.Matrices[f][1, 1] = Complex.FromPolarCoordinates(0.6, -0.7);
        }

        var t = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Complex);
        var m = new Marker(t, Freqs[1], isMulti: false, isDelta: false, index: 1)
        { Freq = Freqs[1], PositionStatic = new System.Numerics.Vector2(0.3f, 0.2f) };
        t.Markers.Add(m);

        t.Derived = DerivedParameters.LoadStabilityCircle;

        Assert.Equal(Freqs[1], m.Freq);
        Assert.NotEqual(Freqs[^1], m.Freq);
        Assert.True(DistanceFromPerimeter(t, m.PositionStatic, 1) < 1e-5);
    }

    // ---- CaptureMarkerFrequencies must not invent a frequency ---------------

    [Fact]
    public void NonFrequencyCubeAxis_LeavesTheFrequencyAlone()
    {
        // A power/parameter sweep has no frequency to carry over. Reading the sweep VALUE into Freq
        // would be worse than leaving it — it would silently claim e.g. 12 Hz for "Pin = 12 dBm".
        var snp = new SNP([1e9], 2, MatrixType.S, MatrixFormat.MA, new Complex(50, 0));
        var t = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Complex) { CubeName = "HB1.V" };
        t.SetCubeData([0, 6, 12], [new Complex(0.1, 0), new Complex(0.2, 0), new Complex(0.3, 0)],
                      null, "Pin", "dBm", PlotType.Smith, FreqUnit.GHz);

        var m = new Marker(t, 0.0, false, false, 1) { PositionStatic = new System.Numerics.Vector2(0.2f, 0f) };
        t.Markers.Add(m);

        t.CaptureMarkerFrequencies();

        Assert.Equal(0.0, m.Freq);
    }
}
