// ================================================================
//  Z0ComplexPortMarkerTests.cs — a reflection marker reports the impedance the PORT sees
//
//  Owner-reported, 2026-08-31. Two ports at conjugate impedances (a Term at Z = 5+j100 driving
//  5−j100) are a perfect power-wave match, so S(1,1) plots at the Smith centre — correct. The
//  marker, however, read "impedance=50+j0 Ω" instead of the 5−j100 the port actually looks into.
//
//  The Kurokawa arithmetic was never wrong. Γ = 0 at a reference Z0 means Z = conj(Z0), and
//  Trace.FormatImpedance computes exactly that — but it was handed 50 Ω, because the trace's
//  SourceZ0PerPort array had been cleared out from under it.
//
//  ORDERING, not arithmetic: PlotInspectorViewModel.TrySetCubeData stamps the per-port references
//  on the trace, and TraceRowViewModel.RebuildSignals then cleared them for EVERY cube-bound trace.
//  RebuildSignals runs after the stamp on both paths that matter — the row VM's own constructor
//  (so: every .cdd load, plot-type switch, undo/redo and paste) and OnLibraryChanged's
//  RefreshDataSources (so: every re-run refresh). With the array null, Trace.MarkerReferenceZ0
//  falls through to the trace's own Z0, whose default is 50 Ω. Hence a correct plot with a wrong
//  readout, and hence a symptom that "fixed itself" the moment the user touched the picker.
//
//  ORACLE, hand-computed and reference-independent: for Γ = 0 at reference Z0, Z = conj(Z0)
//  exactly. So a 1-port whose reference is 5+j100 and whose S11 is 0 MUST read 5−j100 — no other
//  value is defensible, and the wrong answer (50) differs from it in both parts.
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

public sealed class Z0ComplexPortMarkerTests : IDisposable
{
    private readonly string _dir;

    public Z0ComplexPortMarkerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crf-z0cpx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly Complex PortZ0   = new(5, 100);    // the Term's own reference
    private static readonly Complex SeenByIt = new(5, -100);   // conj(PortZ0) — what Γ=0 means

    /// <summary>A 1-port S-parameter run shaped exactly like SParameterEngine's output (grouped
    /// "SP1.S" + per-port "SP1.Z0"): one port referenced to 5+j100, terminated in its conjugate, so
    /// S11 is identically zero.</summary>
    private static DataSet ConjugateMatchedRun()
    {
        var ds = new DataSet();
        ds.AddToGroup("SP1", "S", new DataCube(
            [new Axis("freq", [1e9], "Hz"), new Axis("i", [1.0], "port"), new Axis("j", [1.0], "port")],
            [Complex.Zero]));
        ds.AddToGroup("SP1", "Z0", new DataCube(
            [new Axis("port", [1.0], "port")], [PortZ0]));
        return ds;
    }

    private string WriteRun(string name = "Test.npy")
    {
        string p = Path.Combine(_dir, name);
        DataSetExporter.Export(ConjugateMatchedRun(), p, ExportFormat.Npy);
        return p;
    }

    private static Marker MarkerOn(Trace t) =>
        new(t, 1e9, isMulti: false, isDelta: false, index: 1)
        {
            Freq = 1e9, UseNormalizedImpedance = false, MatrixFormat = MatrixFormat.RI,
        };

    /// <summary>Asserts the marker readout names <paramref name="expected"/> — spelled through the
    /// marker's own formatter, so this pins the VALUE and never the formatting.</summary>
    private static void AssertReadsImpedance(Trace t, Complex expected)
    {
        var m = MarkerOn(t);
        Assert.True(t.MarkerShowsImpedance(m), "pre-condition: S(i,i) must offer an impedance readout");
        Assert.Equal($"impedance={m.FormatComplex(expected)} Ω", t.GetMarkerImpedanceString(m));
    }

    /// <summary>Builds the display the way a run does: library entry, one Smith plot, one S(1,1)
    /// trace on it.</summary>
    private async Task<DataDisplayDocumentViewModel> BuildDisplayAsync(string npyPath)
    {
        var vm = new DataDisplayDocumentViewModel();
        vm.Window.DataSourceLibrary.ResultsRootProvider = () => _dir;
        var lib = vm.Window.DataSourceLibrary;
        lib.RefreshAvailableDataSources();
        await lib.SelectDataSourceAsync(Path.GetFileName(npyPath));

        var container = vm.Window.DataDisplay!.Plots.First();
        container.Inspector.PlotType = PlotType.Smith;
        container.Inspector.AddTraceCommand.Execute(null);
        return vm;
    }

    // ---- The bug: the readout must survive the row VM being rebuilt --------------

    /// <summary>The live case — the row VM is reconstructed on a .cdd load, a plot-type switch, an
    /// undo and a paste, and each of those used to blank the port references.</summary>
    [Fact]
    public async Task ReflectionMarker_AfterRowVmRebuild_StillReportsThePortsOwnReference()
    {
        var vm    = await BuildDisplayAsync(WriteRun());
        var plot  = vm.Window.DataDisplay!.Plots.First();
        var trace = plot.Inspector.Traces[0].Trace;

        Assert.Equal(PortZ0, trace.MarkerZ0);
        AssertReadsImpedance(trace, SeenByIt);

        // A plot-type switch reconstructs every trace card (PlotInspectorViewModel.RebuildTraces) —
        // the same reconstruction a .cdd load, an undo and a paste perform. It must not disturb the
        // reference the readout is taken against.
        plot.Inspector.PlotType = PlotType.Polar;

        var rebuilt = plot.Inspector.Traces[0].Trace;
        Assert.NotNull(rebuilt.SourceZ0PerPort);
        Assert.Equal(PortZ0, rebuilt.MarkerZ0);
        AssertReadsImpedance(rebuilt, SeenByIt);
    }

    /// <summary>The owner's own reproduction, end to end: save the display, reopen it, and read the
    /// marker. Before the fix this reported 50+j0 while the point sat at the Smith centre.</summary>
    [Fact]
    public async Task ReflectionMarker_AfterCddReload_StillReportsThePortsOwnReference()
    {
        string npy = WriteRun();
        var vm     = await BuildDisplayAsync(npy);

        string cdd = Path.Combine(_dir, "Test.cdd");
        await vm.Window.SaveAllAsync(cdd, 0, 0, 0, 0);

        var reloaded = new DataDisplayDocumentViewModel();
        reloaded.Window.DataSourceLibrary.ResultsRootProvider = () => _dir;
        await reloaded.Window.LoadAllAsync(cdd);

        var trace = reloaded.Window.DataDisplay!.Plots.First().Inspector.Traces[0].Trace;

        // The plot itself is unchanged either way — a conjugate match is at the centre whatever the
        // reference, which is exactly why this bug was invisible on the chart.
        Assert.Equal(0.0, Math.Sqrt(trace.Points[0].X * trace.Points[0].X +
                                    trace.Points[0].Y * trace.Points[0].Y), precision: 9);

        Assert.Equal(PortZ0, trace.MarkerZ0);
        AssertReadsImpedance(trace, SeenByIt);
    }

    /// <summary>With Override off, Trace.Z0 is documented as a read-only MIRROR of the source's
    /// port-1 reference — so a .cdd that persisted a stale 50 Ω must not survive the reload. If it
    /// did, ticking Override would renormalize to a value the user never typed.</summary>
    [Fact]
    public async Task Z0Box_AfterCddReload_MirrorsThePortsOwnReference()
    {
        string npy = WriteRun();
        var vm     = await BuildDisplayAsync(npy);

        string cdd = Path.Combine(_dir, "Test.cdd");
        await vm.Window.SaveAllAsync(cdd, 0, 0, 0, 0);

        var reloaded = new DataDisplayDocumentViewModel();
        reloaded.Window.DataSourceLibrary.ResultsRootProvider = () => _dir;
        await reloaded.Window.LoadAllAsync(cdd);

        var row = reloaded.Window.DataDisplay!.Plots.First().Inspector.Traces[0];
        Assert.False(row.Z0OverrideEnabled, "pre-condition: Override starts unchecked");
        Assert.Equal(PortZ0, row.Trace.Z0);
    }

    // ---- The reference the stamp comes from -------------------------------------

    /// <summary>The per-port array is re-stamped from the group's own Z0 cube, and a complex
    /// reference counts as "unusual" so the provenance badge still fires.</summary>
    [Fact]
    public async Task CubeTrace_CarriesPerPortZ0AndMarksItUnusual()
    {
        var vm    = await BuildDisplayAsync(WriteRun());
        var row   = vm.Window.DataDisplay!.Plots.First().Inspector.Traces[0];

        Assert.Equal([PortZ0], row.Trace.SourceZ0PerPort!);
        Assert.True(row.Trace.SourceZ0IsUnusual, "a complex reference is not UniformReal");
        Assert.True(row.IsCubeNetworkParamTrace);
    }

    /// <summary>Sanity anchor for the arithmetic itself, independent of any view model: Γ = 0 at a
    /// complex reference is the conjugate impedance, and a purely real reference still reads back
    /// unchanged (so the fix cannot have skewed the ordinary 50 Ω case).</summary>
    [Theory]
    [InlineData(5.0, 100.0)]
    [InlineData(50.0, 0.0)]
    [InlineData(75.0, -20.0)]
    public void GammaZero_ReadsBackAsTheConjugateReference(double r, double x)
    {
        var z0  = new Complex(r, x);
        var snp = new SNP([1e9], 1, MatrixType.S, MatrixFormat.RI, z0);
        snp.Matrices[0][0, 0] = Complex.Zero;

        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Complex)
        {
            SourceZ0PerPort   = [z0],
            SourceZ0IsUnusual = x != 0.0,
        };

        Assert.Equal(z0, trace.MarkerZ0);
        AssertReadsImpedance(trace, Complex.Conjugate(z0));
    }
}
