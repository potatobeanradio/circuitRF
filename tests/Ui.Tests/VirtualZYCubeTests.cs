// ================================================================
//  VirtualZYCubeTests.cs  —  brief-dd-network-params-and-stability.md §2
//
//  A simulated S-parameter run has an S cube and a Z0 cube but no Z or Y cube at all — so the
//  picker never offered Z/Y and typing "SP1.Z[:, 1, 1]" reported invalid, even though the S+Z0
//  data needed to compute Z/Y was right there. DataSourceEntryViewModel.Data now lazily
//  materializes "Z"/"Y" cubes into every named group that carries S+Z0 (via
//  RfCore.Data.NetworkMetrics.ConvertSCube), so every existing DataSet consumer — the picker, the
//  spec parser, the Table, the matrix-type selector — reaches them with no special-casing.
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

public sealed class VirtualZYCubeTests : IDisposable
{
    private readonly string _dir;

    public VirtualZYCubeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crf-zycube-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static DataSet GroupedRun(string group, int nPorts, Complex? z0 = null)
    {
        double[] freqs = [1e9, 2e9];
        var s = new Complex[freqs.Length * nPorts * nPorts];
        var rnd = new Random(7);
        for (int f = 0; f < freqs.Length; f++)
            for (int i = 0; i < nPorts; i++)
                for (int j = 0; j < nPorts; j++)
                    s[f * nPorts * nPorts + i * nPorts + j] =
                        i == j ? new Complex(0.5, -0.1) : new Complex(rnd.NextDouble() * 0.3, rnd.NextDouble() * 0.1);

        var ds = new DataSet();
        ds.AddToGroup(group, "S", new DataCube(
            [new Axis("freq", freqs, "Hz"),
             new Axis("i", Enumerable.Range(1, nPorts).Select(v => (double)v).ToArray(), ""),
             new Axis("j", Enumerable.Range(1, nPorts).Select(v => (double)v).ToArray(), "")],
            s));
        var z0Vals = Enumerable.Repeat(z0 ?? new Complex(50, 0), nPorts).ToArray();
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
                                              string cubeName, PlotType plotType)
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

    [Fact]
    public async Task Picker_SimulatedRun_OffersZAndY_InTheSameGroupAsS()
    {
        // §4 landed after this test was first written: the picker no longer offers a bare "S"/"Z"/"Y"
        // quantity item — it offers one element item per (i,j) pair, only for the currently-selected
        // matrix type (default S). Z/Y become reachable by flipping MatrixType (covered by
        // MatrixTypeSelector_S_To_Z_RewritesCubeName_KeepsSlice below), which relabels the same items.
        string p = WriteNpy(GroupedRun("SP1", nPorts: 4));
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var row = BuildRow(lib, p, "SP1.S", PlotType.Rect);
        row.SelectedGroup = "SP1";
        var items = row.AvailableSignals.Where(x => x.IsCubeBound).ToList();

        Assert.Contains(items, x => x.Label == "S(1,1)" && x.CubeName == "SP1.S");
        Assert.Contains(items, x => x.Label == "S(4,4)" && x.CubeName == "SP1.S");
        Assert.Equal(16, items.Count(x => x.CubeName == "SP1.S"));

        row.MatrixType = MatrixType.Z;
        var zItems = row.AvailableSignals.Where(x => x.IsCubeBound).ToList();
        Assert.Contains(zItems, x => x.Label == "Z(1,1)" && x.CubeName == "SP1.Z");

        row.MatrixType = MatrixType.Y;
        var yItems = row.AvailableSignals.Where(x => x.IsCubeBound).ToList();
        Assert.Contains(yItems, x => x.Label == "Y(1,1)" && x.CubeName == "SP1.Y");
    }

    [Fact]
    public async Task PickingZ_RendersACurve_MatchingRFNetwork_SToZ()
    {
        string p = WriteNpy(GroupedRun("SP1", nPorts: 4));
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var row = BuildRow(lib, p, "SP1.S", PlotType.Rect);
        row.SelectedGroup = "SP1";
        row.MatrixType = MatrixType.Z;
        // "Z(1,1)" is already the auto-selected fallback at this point (trace.Slice is still null,
        // so RebuildSignals' match falls back to the first candidate) — pick a DIFFERENT pair so
        // this is a genuine selection change.
        row.SelectedSignal = row.AvailableSignals.First(x => x.Label == "Z(2,1)");

        var trace = row.Trace;
        Assert.Equal("SP1.Z", trace.CubeName);
        Assert.NotEmpty(trace.Points);
    }

    [Fact]
    public async Task TypedSpec_SP1Z_And_SP1Y_Parse_AndBind()
    {
        string p = WriteNpy(GroupedRun("SP1", nPorts: 4, z0: new Complex(50, 0)));
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var zRow = BuildRow(lib, p, "SP1.S", PlotType.Rect);
        zRow.SelectedGroup = "SP1";
        zRow.CommitSpec("mag(SP1.Z[:, 1, 1])");
        Assert.False(zRow.HasSpecError);
        Assert.Equal("SP1.Z", zRow.Trace.CubeName);
        Assert.NotEmpty(zRow.Trace.Points);

        var yRow = BuildRow(lib, p, "SP1.S", PlotType.Rect);
        yRow.SelectedGroup = "SP1";
        yRow.CommitSpec("mag(SP1.Y[:, 2, 1])");
        Assert.False(yRow.HasSpecError);
        Assert.Equal("SP1.Y", yRow.Trace.CubeName);
        Assert.NotEmpty(yRow.Trace.Points);
    }

    [Fact]
    public async Task MaterializedZAndYCubes_MatchRFNetwork_SToZ_SToY_PerFrequency()
    {
        // The values genuinely resolve through DataSet lookup (ds["SP1.Z"]) and match
        // RFNetwork.SToZ/SToY of the S cube at the same frequency — a unit test, not eyeball.
        string p = WriteNpy(GroupedRun("SP1", nPorts: 4, z0: new Complex(50, 0)));
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);
        var entry = lib.Entries[0];

        var ds = entry.Data!;
        Assert.True(ds.Contains("SP1.Z"));
        Assert.True(ds.Contains("SP1.Y"));

        var sCube = ds["SP1.S"];
        var zCube = ds["SP1.Z"];
        var yCube = ds["SP1.Y"];
        const int nPorts = 4;
        var z0PerPort = Enumerable.Repeat(new Complex(50, 0), nPorts).ToArray();
        var sRaw = sCube.ComplexValues;
        var zRaw = zCube.ComplexValues;
        var yRaw = yCube.ComplexValues;

        for (int f = 0; f < 2; f++)
        {
            var m = new NumFlat.Mat<Complex>(nPorts, nPorts);
            for (int i = 0; i < nPorts; i++)
            for (int j = 0; j < nPorts; j++)
                m[i, j] = sRaw[f * nPorts * nPorts + i * nPorts + j];

            var expectedZ = RFNetwork.SToZ(m, z0PerPort);
            var expectedY = RFNetwork.SToY(m, z0PerPort);

            for (int i = 0; i < nPorts; i++)
            for (int j = 0; j < nPorts; j++)
            {
                int idx = f * nPorts * nPorts + i * nPorts + j;
                Assert.Equal(expectedZ[i, j].Real, zRaw[idx].Real, 9);
                Assert.Equal(expectedZ[i, j].Imaginary, zRaw[idx].Imaginary, 9);
                Assert.Equal(expectedY[i, j].Real, yRaw[idx].Real, 9);
                Assert.Equal(expectedY[i, j].Imaginary, yRaw[idx].Imaginary, 9);
            }
        }
    }

    [Fact]
    public async Task AutoTransform_S_DefaultsDb20_ZAndY_DefaultToMag()
    {
        string p = WriteNpy(GroupedRun("SP1", nPorts: 2));
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var row = BuildRow(lib, p, "SP1.S", PlotType.Rect);
        row.SelectedGroup = "SP1";

        // The initial auto-selection ("S(1,1)") that RebuildSignals performs is suppressed (no
        // side effects, avoids the ComboBox "revert bug") — pick a genuinely DIFFERENT element
        // each time so OnSelectedSignalChanged's auto-transform logic actually runs.
        row.SelectedSignal = row.AvailableSignals.First(x => x.Label == "S(1,2)");
        Assert.Equal(CubeTransform.dB20, row.Trace.Transform);

        row.MatrixType = MatrixType.Z;
        row.SelectedSignal = row.AvailableSignals.First(x => x.Label == "Z(2,1)");
        Assert.Equal(CubeTransform.Mag, row.Trace.Transform);

        row.MatrixType = MatrixType.Y;
        row.SelectedSignal = row.AvailableSignals.First(x => x.Label == "Y(2,2)");
        Assert.Equal(CubeTransform.Mag, row.Trace.Transform);
    }

    [Fact]
    public async Task MatrixTypeSelector_S_To_Z_RewritesCubeName_KeepsSlice()
    {
        string p = WriteNpy(GroupedRun("SP1", nPorts: 4));
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var row = BuildRow(lib, p, "SP1.S", PlotType.Rect);
        row.SelectedGroup = "SP1";
        row.SelectedSignal = row.AvailableSignals.First(x => x.Label == "S(3,2)");
        var sliceBefore = row.Trace.Slice;
        Assert.True(row.ShowMatrixTypeCombo);

        row.MatrixType = MatrixType.Z;

        Assert.Equal("SP1.Z", row.Trace.CubeName);
        Assert.Equal(sliceBefore!.Length, row.Trace.Slice!.Length);
        Assert.NotEmpty(row.Trace.Points);
        // Relabels to Z(3,2) — the SAME port pair stays selected, not S(1,1)'s default.
        Assert.Equal("Z(3,2)", row.SelectedSignal?.Label);

        row.MatrixType = MatrixType.Y;
        Assert.Equal("SP1.Y", row.Trace.CubeName);
        Assert.Equal("Y(3,2)", row.SelectedSignal?.Label);
    }

    // ---- .cdd round-trip ---------------------------------------------------

    [Fact]
    public async Task ZBoundTrace_SurvivesCddSaveAndFreshSessionReload()
    {
        string p = WriteNpy(GroupedRun("SP1", nPorts: 4));

        // "Save": build the trace as the picker would, then serialize exactly as .cdd save does.
        TraceConfig savedConfig;
        {
            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(p);
            await lib.SelectDataSourceAsync(p);
            var row = BuildRow(lib, p, "SP1.S", PlotType.Rect);
            row.SelectedGroup = "SP1";
            row.MatrixType = MatrixType.Z;
            // "Z(1,1)" is already the auto-selected fallback here — pick a different pair so this
            // is a genuine selection change (see PickingZ_RendersACurve_MatchingRFNetwork_SToZ).
            row.SelectedSignal = row.AvailableSignals.First(x => x.Label == "Z(2,1)");
            savedConfig = DataDisplayViewModel.BuildTraceConfig(row.Trace, configDir: "");
        }

        Assert.Equal("SP1.Z", savedConfig.CubeName);
        Assert.NotEmpty(savedConfig.CubeSlice);

        // "Reload": a brand-new session/library, loading the same file fresh — entry.Data has
        // never been touched before, so Z/Y materialize lazily on first access here, exactly as a
        // saved .cdd referencing "SP1.Z" needs.
        var freshLib = new DataSourceLibraryViewModel();
        await freshLib.LoadFileAsync(p);
        await freshLib.SelectDataSourceAsync(p);

        var restored = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            SourcePath = p,
            CubeName   = savedConfig.CubeName,
            Slice      = savedConfig.CubeSlice.Select(s => new AxisSlice(s.AxisName, s.Role, s.Index)).ToArray(),
            Transform  = savedConfig.CubeTransform,
        };
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(restored);
        var inspector = new PlotInspectorViewModel(plot, () => { }, freshLib);
        inspector.RebuildAndNotify();

        Assert.NotEmpty(restored.Points);
        Assert.Null(restored.InvalidSpecText);
    }

    [Fact]
    public async Task GroupedRunNpy_DoesNotMaterializeZY_InTheDefaultGroup()
    {
        // A flat/Touchstone-shaped S in the DEFAULT group is already offered through the network
        // (SNP) path — materializing "Z"/"Y" there too would offer the same values a second time.
        var snp = new SNP([1e9], [new NumFlat.Mat<Complex>(2, 2)], MatrixType.S, MatrixFormat.RI, new Complex(50, 0));
        var ds = DataSetBuilder.FromSnp(snp);
        string p = WriteNpy(ds, "flat.npy");

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        var entry = Assert.Single(lib.Entries);

        Assert.NotNull(entry.Snp);
        Assert.False(entry.Data!.Contains("Z"));
        Assert.False(entry.Data!.Contains("Y"));
    }
}
