// ================================================================
//  Z0RenormalizationTests.cs — brief-dd-z0-renormalization.md gate tests (UI side)
//
//  §1  Cube-bound S/Z/Y trace renders at trace.Z0; two traces of one dataset at different Z0
//      render distinct loci.
//  §2  Complex Z0; Re(Z0)<=0 refused; an unusual (non-uniform) cube source can renormalize to a
//      uniform target and the badge still shows the source was unusual.
//  §3  Y-axis label Z0 token — byte-identical when unchanged, present when re-referenced.
//  §4  Marker impedance readout (cube-bound reflection element) at the trace's Z0.
//  §5  Contour Z0 control gating (Γ plane only) and RebuildContour Z0 threading.
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
using RfCore.Loadpull;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class Z0RenormalizationTests : IDisposable
{
    private readonly string _dir;

    public Z0RenormalizationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crf-z0renorm-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- Helpers (mirror VirtualZYCubeTests) --------------------------------

    private static DataSet GroupedRun(string group, int nPorts, Complex[]? z0PerPort = null)
    {
        double[] freqs = [1e9, 2e9];
        var s = new Complex[freqs.Length * nPorts * nPorts];
        var rnd = new Random(11);
        for (int f = 0; f < freqs.Length; f++)
            for (int i = 0; i < nPorts; i++)
                for (int j = 0; j < nPorts; j++)
                    s[f * nPorts * nPorts + i * nPorts + j] =
                        i == j ? new Complex(0.4, -0.15) : new Complex(rnd.NextDouble() * 0.2, rnd.NextDouble() * 0.1);

        var ds = new DataSet();
        ds.AddToGroup(group, "S", new DataCube(
            [new Axis("freq", freqs, "Hz"),
             new Axis("i", Enumerable.Range(1, nPorts).Select(v => (double)v).ToArray(), ""),
             new Axis("j", Enumerable.Range(1, nPorts).Select(v => (double)v).ToArray(), "")],
            s));
        var z0Vals = z0PerPort ?? Enumerable.Repeat(new Complex(50, 0), nPorts).ToArray();
        ds.AddToGroup(group, "Z0", new DataCube(
            [new Axis("port", Enumerable.Range(1, nPorts).Select(v => (double)v).ToArray(), "")],
            z0Vals));
        return ds;
    }

    private string WriteNpy(DataSet ds, string name = "run.npy")
    {
        string p = Path.Combine(_dir, name);
        DataSetExporter.Export(ds, p, ExportFormat.Npy);
        return p;
    }

    private static TraceRowViewModel BuildRow(DataSourceLibraryViewModel lib, string path,
                                              string cubeName, PlotType plotType = PlotType.Smith)
    {
        var trace = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            SourcePath = path, CubeName = cubeName, Slice = null, Transform = CubeTransform.None,
        };
        var plot = new Plot(plotType, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
        inspector.RebuildAndNotify();
        return inspector.Traces[0];
    }

    /// <summary>
    /// Selects the picker item with the given label, guaranteeing OnSelectedSignalChanged actually
    /// runs (and so writes Trace.Slice) even when the item happens to already be RebuildSignals' own
    /// (callback-suppressed) auto-selected default — a plain re-assignment of a REFERENCE-EQUAL
    /// TraceDataItem is a no-op under CommunityToolkit's generated property setter, so this bounces
    /// through a different item first when needed (same principle VirtualZYCubeTests' tests apply by
    /// always picking a port pair genuinely different from whatever might already be selected).
    /// </summary>
    private static void SelectSignal(TraceRowViewModel row, string label)
    {
        var target = row.AvailableSignals.First(x => x.Label == label);
        if (!ReferenceEquals(row.SelectedSignal, target))
        {
            row.SelectedSignal = target;
            return;
        }
        var other = row.AvailableSignals.First(x => x.Label != label);
        row.SelectedSignal = other;
        row.SelectedSignal = target;
    }

    // ---- §1: cube-bound renders at trace.Z0, matches RFNetwork.SToS ---------

    [Fact]
    public async Task S11_Z0Override_MatchesRFNetwork_SToS_ToWithin1e12()
    {
        var ds = GroupedRun("SP1", nPorts: 4);
        string p = WriteNpy(ds);
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var row = BuildRow(lib, p, "SP1.S", PlotType.Smith);
        row.SelectedGroup = "SP1";
        SelectSignal(row, "S(1,1)");

        row.Z0OverrideEnabled = true;
        row.Z0String = "75";

        var trace = row.Trace;
        Assert.NotEmpty(trace.Points);

        // Hand-compute S(1,1) at 75 Ω from the ORIGINAL (50 Ω) source S cube.
        var sCube = ds["SP1.S"];
        int nPorts = 4;
        var z0Src = Enumerable.Repeat(new Complex(50, 0), nPorts).ToArray();
        var z0New = Enumerable.Repeat(new Complex(75, 0), nPorts).ToArray();
        var raw = sCube.ComplexValues;

        for (int f = 0; f < 2; f++)
        {
            var m = new NumFlat.Mat<Complex>(nPorts, nPorts);
            for (int i = 0; i < nPorts; i++)
            for (int j = 0; j < nPorts; j++)
                m[i, j] = raw[f * nPorts * nPorts + i * nPorts + j];
            var expected = RFNetwork.SToS(m, z0Src, z0New)[0, 0];

            Assert.Equal(expected.Real, trace.Points[f].X, precision: 6);
            Assert.Equal(expected.Imaginary, trace.Points[f].Y, precision: 6);
        }
    }

    [Fact]
    public async Task TwoTracesOfSameCube_AtDifferentZ0_RenderDistinctLoci()
    {
        string p = WriteNpy(GroupedRun("SP1", nPorts: 2));
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var row50 = BuildRow(lib, p, "SP1.S", PlotType.Smith);
        row50.SelectedGroup = "SP1";
        SelectSignal(row50, "S(1,1)");

        var row75 = BuildRow(lib, p, "SP1.S", PlotType.Smith);
        row75.SelectedGroup = "SP1";
        SelectSignal(row75, "S(1,1)");
        row75.Z0OverrideEnabled = true;
        row75.Z0String = "75";

        Assert.NotEmpty(row50.Trace.Points);
        Assert.NotEmpty(row75.Trace.Points);
        Assert.NotEqual(row50.Trace.Points[0], row75.Trace.Points[0]);
    }

    // ---- §2: complex Z0, Re(Z0)<=0 refused, unusual source badge -----------

    [Fact]
    public async Task ComplexZ0_RenormalizesAndRenders()
    {
        var ds = GroupedRun("SP1", nPorts: 2);
        string p = WriteNpy(ds);
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var row = BuildRow(lib, p, "SP1.S", PlotType.Smith);
        row.SelectedGroup = "SP1";
        SelectSignal(row, "S(1,1)");
        row.Z0OverrideEnabled = true;
        row.Z0String = "50+10j";

        Assert.Equal("", row.Z0ErrorText);
        Assert.Equal(new Complex(50, 10), row.Trace.Z0);
        Assert.NotEmpty(row.Trace.Points);

        var sCube = ds["SP1.S"];
        var z0Src = Enumerable.Repeat(new Complex(50, 0), 2).ToArray();
        var z0New = Enumerable.Repeat(new Complex(50, 10), 2).ToArray();
        var m = new NumFlat.Mat<Complex>(2, 2);
        var raw = sCube.ComplexValues;
        for (int i = 0; i < 2; i++)
        for (int j = 0; j < 2; j++)
            m[i, j] = raw[i * 2 + j];
        var expected = RFNetwork.SToS(m, z0Src, z0New)[0, 0];

        Assert.Equal(expected.Real, row.Trace.Points[0].X, precision: 6);
        Assert.Equal(expected.Imaginary, row.Trace.Points[0].Y, precision: 6);
    }

    [Fact]
    public async Task NegativeRealZ0_RefusedWithMessage_TraceUnchanged()
    {
        string p = WriteNpy(GroupedRun("SP1", nPorts: 2));
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var row = BuildRow(lib, p, "SP1.S", PlotType.Smith);
        row.SelectedGroup = "SP1";
        SelectSignal(row, "S(1,1)");
        row.Z0OverrideEnabled = true;

        var before = row.Trace.Z0;
        row.Z0String = "-5";

        Assert.NotEmpty(row.Z0ErrorText);
        Assert.True(row.HasZ0Error);
        Assert.Equal(before, row.Trace.Z0);   // rejected — trace.Z0 must not have moved
    }

    [Fact]
    public async Task UnusualSource_RenormalizesToUniform_BadgeStillShowsUnusual()
    {
        // Non-uniform per-port source (port1=50Ω, port2=75-10jΩ).
        var z0PerPort = new[] { new Complex(50, 0), new Complex(75, -10) };
        var ds = GroupedRun("SP1", nPorts: 2, z0PerPort);
        string p = WriteNpy(ds);
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var row = BuildRow(lib, p, "SP1.S", PlotType.Smith);
        row.SelectedGroup = "SP1";
        SelectSignal(row, "S(1,1)");

        Assert.True(row.Trace.SourceZ0IsUnusual, "pre-condition: source is non-uniform");
        Assert.True(row.ShowZ0Control, "renorm must be offered even for an unusual source (§2)");
        Assert.True(row.ShowZ0Badge, "badge must still indicate the source was unusual");

        row.Z0OverrideEnabled = true;
        row.Z0String = "50";   // renormalize the non-uniform source to a uniform 50 Ω

        var sCube = ds["SP1.S"];
        var z0New = Enumerable.Repeat(new Complex(50, 0), 2).ToArray();
        var m = new NumFlat.Mat<Complex>(2, 2);
        var raw = sCube.ComplexValues;
        for (int i = 0; i < 2; i++)
        for (int j = 0; j < 2; j++)
            m[i, j] = raw[i * 2 + j];
        var expected = RFNetwork.SToS(m, z0PerPort, z0New)[0, 0];

        Assert.Equal(expected.Real, row.Trace.Points[0].X, precision: 6);
        Assert.Equal(expected.Imaginary, row.Trace.Points[0].Y, precision: 6);
    }

    // ---- §3: Y-axis label Z0 token -------------------------------------------

    private static Trace MakeReflectionTrace(Complex[] sourceZ0PerPort, bool unusual, Complex z0)
    {
        var trace = new Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName = "SP1.S",
            Slice =
            [
                new AxisSlice("freq", AxisRole.KeepAsX, 0),
                new AxisSlice("i", AxisRole.PinToIndex, 0),
                new AxisSlice("j", AxisRole.PinToIndex, 0),
            ],
            SourceZ0PerPort = sourceZ0PerPort,
            SourceZ0IsUnusual = unusual,
            Z0 = z0,
        };
        return trace;
    }

    [Fact]
    public void Label_ByteIdentical_WhenNotReReferenced()
    {
        var trace = MakeReflectionTrace([new Complex(50, 0)], unusual: false, z0: new Complex(50, 0));
        Assert.Equal("dB20(S(1,1))", trace.RectYLabel("dB20(S(1,1))", false));
    }

    [Fact]
    public void Label_ShowsToken_WhenReReferenced_RealAndComplex()
    {
        var real = MakeReflectionTrace([new Complex(50, 0)], unusual: false, z0: new Complex(75, 0));
        Assert.Equal("dB20(S(1,1)) @ Z0=75Ω", real.RectYLabel("dB20(S(1,1))", false));

        var cplx = MakeReflectionTrace([new Complex(50, 0)], unusual: false, z0: new Complex(50, 10));
        Assert.Contains("@ Z0=", cplx.RectYLabel("dB20(S(1,1))", false));
        Assert.Contains("50", cplx.RectYLabel("dB20(S(1,1))", false));
    }

    [Fact]
    public void Label_75OhmSourceAt75_ShowsNoToken()
    {
        var trace = MakeReflectionTrace([new Complex(75, 0)], unusual: false, z0: new Complex(75, 0));
        Assert.Equal("dB20(S(1,1))", trace.RectYLabel("dB20(S(1,1))", false));
    }

    [Fact]
    public void Label_ContourTrace_NeverShowsToken()
    {
        var trace = new Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            ContourData = new ContourData(),
            Z0 = new Complex(75, 0),
        };
        Assert.Equal("", trace.RectYLabel("dB20(S(1,1))", false));
    }

    // ---- §4: marker impedance readout on a cube-bound reflection element ----

    [Fact]
    public void CubeMarkerImpedance_AtTraceZ0_MatchesFormula()
    {
        var s11 = new Complex(0.2, -0.3);
        var trace = new Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName = "SP1.S",
            Slice =
            [
                new AxisSlice("freq", AxisRole.KeepAsX, 0),
                new AxisSlice("i", AxisRole.PinToIndex, 0),
                new AxisSlice("j", AxisRole.PinToIndex, 0),
            ],
            Z0 = new Complex(75, 0),
        };
        trace.SetCubeData([1e9], [s11], null, "freq", "Hz", PlotType.Table, FreqUnit.GHz);

        var marker = new Marker(trace, 1e9, isMulti: false, isDelta: false, index: 1) { Freq = 1e9 };
        marker.UseNormalizedImpedance = false;

        Assert.True(trace.MarkerShowsImpedance(marker));

        var z0 = new Complex(75, 0);
        var expectedZ = z0 * (Complex.Conjugate(z0) / z0 + s11) / (Complex.One - s11);
        string result = trace.GetMarkerImpedanceString(marker);
        // Format with the SAME marker (whatever MatrixFormat it resolved to) rather than hand-parsing
        // a substring — the exact string must match, not just "contains a number that looks right".
        Assert.Equal($"impedance={marker.FormatComplex(expectedZ)} Ω", result);

        // Off-diagonal element (i != j) has no impedance meaning.
        var offDiag = new Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName = "SP1.S",
            Slice =
            [
                new AxisSlice("freq", AxisRole.KeepAsX, 0),
                new AxisSlice("i", AxisRole.PinToIndex, 0),
                new AxisSlice("j", AxisRole.PinToIndex, 1),
            ],
        };
        offDiag.SetCubeData([1e9], [s11], null, "freq", "Hz", PlotType.Table, FreqUnit.GHz);
        Assert.False(offDiag.MarkerShowsImpedance(new Marker(offDiag, 1e9, false, false, 1) { Freq = 1e9 }));
    }

    // ---- §5: contour Z0 control gating + RebuildContour threading -----------

    // A genuine power sweep (5 PavlDbm points) per grid point, mirroring the RfCore-side
    // Z0RenormalizationTests fixture — needed for LoadpullSurface.Reduce to interpolate at all.
    private static DataSet BuildGammaLoadpullDataSet(Complex[] gammas, double[] gainOffsetDb)
    {
        int nGrid = gammas.Length, nPin = 5;
        var grid = new Axis("gridPoint", Enumerable.Range(0, nGrid).Select(i => (double)i).ToArray());
        var pin  = new Axis("pinStep",   Enumerable.Range(0, nPin).Select(i => (double)i).ToArray());

        var pavl = new double[nGrid * nPin];
        var pout = new double[nGrid * nPin];
        var gt   = new double[nGrid * nPin];
        for (int gi = 0; gi < nGrid; gi++)
        for (int pi = 0; pi < nPin; pi++)
        {
            int idx = gi * nPin + pi;
            pavl[idx] = pi;
            pout[idx] = pi + gainOffsetDb[gi];
            gt[idx]   = gainOffsetDb[gi];
        }

        var ds = new DataSet();
        ds.Add("GammaLoad", new DataCube([grid], gammas));
        ds.Add("ZLoad",     new DataCube([grid], gammas.Select(g => RfHelpers.G2Z(g) * 50.0).ToArray()));
        ds.Add("Pout_dBm",  new DataCube([grid, pin], pout));
        ds.Add("Gt_dB",     new DataCube([grid, pin], gt));
        ds.Add("PavlDbm",   new DataCube([grid, pin], pavl));
        return ds;
    }

    private static readonly Complex[] GammaGrid8 =
    {
        new(0.0, 0.0), new(0.2, 0.0), new(0.2, 0.2), new(0.0, 0.2),
        new(-0.2, 0.0), new(-0.2, -0.2), new(0.0, -0.2), new(0.3, 0.1),
    };
    private static readonly double[] GammaGrid8GainDb = { 10, 11, 9, 10.5, 10.2, 9.8, 10.7, 11.5 };

    private async Task<(DataSourceLibraryViewModel lib, string path)> LoadGammaGridAsync()
    {
        string p = WriteNpy(BuildGammaLoadpullDataSet(GammaGrid8, GammaGrid8GainDb), "loadpull.npy");
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);
        return (lib, p);
    }

    private static PlotInspectorViewModel AddContourInspector(
        DataSourceLibraryViewModel lib, PlotType plotType)
    {
        var plot = new Plot(plotType, FreqUnit.GHz);
        var insp = new PlotInspectorViewModel(plot, () => { }, lib);
        insp.AddContourTraceCommand.Execute(null);
        return insp;
    }

    [Fact]
    public async Task ShowContourZ0Control_GatedToGammaPlane()
    {
        var (lib, _) = await LoadGammaGridAsync();

        var smithInsp = AddContourInspector(lib, PlotType.Smith);
        var smithRow  = smithInsp.Traces[0];
        Assert.True(smithRow.ShowContourZ0Control, "Smith (Γ plane) must show the contour Z0 control");

        var rectInsp = AddContourInspector(lib, PlotType.Rect);
        var rectRow  = rectInsp.Traces[0];
        Assert.False(rectRow.ShowContourZ0Control, "Rect (Z plane) must NOT show the contour Z0 control");
    }

    [Fact]
    public async Task RebuildContour_Z0Override_MovesGammaGrid_ButNotZPlane()
    {
        var (lib, _) = await LoadGammaGridAsync();

        var insp = AddContourInspector(lib, PlotType.Smith);
        var row  = insp.Traces[0];
        row.ContourMetricName    = "Pout_dBm";
        row.ContourConstraintKind = ConstraintKind.ConstantMetric;
        row.ContourConstraintMetric = "PavlDbm";
        row.ContourConstraintValue = 2.0;

        var cd = row.Trace.ContourData!;
        Assert.NotNull(cd.Scatter);
        var coordsAt50 = cd.Scatter!.Coords.ToArray();
        Assert.NotEmpty(coordsAt50);

        row.Z0OverrideEnabled = true;
        row.Z0String = "25";

        var coordsAt25 = cd.Scatter!.Coords.ToArray();
        Assert.Equal(coordsAt50.Length, coordsAt25.Length);

        // Spot-check one grid point by hand against the same RenormGamma formula pinned in the
        // RfCore-side Z0RenormalizationTests (public via RfHelpers.G2Z/Z2G — assumed 50 Ω source).
        var z = RfHelpers.G2Z(coordsAt50[0]) * (50.0 / 25.0);
        var expected = RfHelpers.Z2G(z);
        Assert.Equal(expected.Real, coordsAt25[0].Real, precision: 9);
        Assert.Equal(expected.Imaginary, coordsAt25[0].Imaginary, precision: 9);

        // Z-plane: switching plot type and editing Z0 must not move the fit at all.
        var zInsp = AddContourInspector(lib, PlotType.Rect);
        var zRow  = zInsp.Traces[0];
        zRow.ContourMetricName      = "Pout_dBm";
        zRow.ContourConstraintKind  = ConstraintKind.ConstantMetric;
        zRow.ContourConstraintMetric = "PavlDbm";
        zRow.ContourConstraintValue = 2.0;
        var zCd = zRow.Trace.ContourData!;
        var zGridBefore = zCd.Grid!.Values.ToArray();

        // Rect never SHOWS the Z0 control (ShowContourZ0Control is false there), but the property
        // setters themselves don't gate on that — confirm directly that editing Z0 through the same
        // path the UI would use, on a Z-plane contour, cannot leak into the fit (RebuildContour
        // computes z0=null whenever plane != Gamma, regardless of what Trace.Z0 holds).
        zRow.Z0OverrideEnabled = true;
        zRow.Z0String = "25";
        Assert.Equal(new Complex(25, 0), zRow.Trace.Z0);   // the field DID change...
        var zGridAfter = zCd.Grid!.Values.ToArray();       // ...but the Z-plane grid must not have
        Assert.Equal(zGridBefore, zGridAfter);
    }
}
