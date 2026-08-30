// ================================================================
//  SimulatedSourceNetworkMetricsTests.cs
//
//  R-stb-1: the stability/passivity metrics must be available for a SIMULATED source, not only a
//  Touchstone file. They were not — reported by the owner as "I see the correct controls with a
//  touchstone file, but not from a simulation npy file".
//
//  Root cause: a simulation run writes its S-parameters into a NAMED ANALYSIS GROUP ("SP1.S"), and
//  DataSet.Contains bare-resolves — bare resolution deliberately refuses analysis cubes ("Analysis
//  cubes are reachable only by qualification"). So `data.Contains("S")` was FALSE for every
//  simulated source, no SNP was built, and TraceRowViewModel.RebuildSignals skipped its entire
//  network-metric block on `if (entry.Snp is null) continue;`. The SAME bare-lookup mistake left
//  the per-port Z0 vector empty, silently discarding the reference impedance the maths depends on.
//
//  These tests build a run-shaped DataSet (cubes in a named group) rather than reading
//  circuitRF_demo/, which is gitignored and absent on a fresh clone.
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

public sealed class SimulatedSourceNetworkMetricsTests : IDisposable
{
    private readonly string _dir;

    public SimulatedSourceNetworkMetricsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crf-simsrc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- fixtures ------------------------------------------------------

    /// <summary>A DataSet shaped like a real S-parameter run: cubes live in a NAMED group.</summary>
    private static DataSet GroupedRun(string group = "SP1", int nPorts = 2, Complex? z0 = null)
    {
        double[] freqs = [1e9, 2e9, 3e9];
        var s = new Complex[freqs.Length * nPorts * nPorts];
        for (int f = 0; f < freqs.Length; f++)
            for (int i = 0; i < nPorts; i++)
                for (int j = 0; j < nPorts; j++)
                    s[f * nPorts * nPorts + i * nPorts + j] =
                        i == j ? new Complex(0.5, -0.1) : new Complex(2.0, 0.3);

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

    // =========================================================================
    //  The lookup itself (RfCore) — the layer the bug actually lived in
    // =========================================================================

    [Fact]
    public void FindCubeSpec_GroupedRun_ResolvesQualified_WhereBareContainsFails()
    {
        var ds = GroupedRun("SP1");

        // This is the trap, pinned directly: bare resolution refuses an analysis cube.
        Assert.False(ds.Contains("S"));
        Assert.False(ds.Contains("Z0"));

        Assert.Equal("SP1.S",  DataSetBuilder.FindCubeSpec(ds, "S"));
        Assert.Equal("SP1.Z0", DataSetBuilder.FindCubeSpec(ds, "Z0"));
        Assert.Null(DataSetBuilder.FindCubeSpec(ds, "NoSuchCube"));
    }

    [Fact]
    public void FindCubeSpec_FlatTouchstoneShaped_StillResolvesBare()
    {
        // The Touchstone path always worked — it must keep working, unqualified.
        var snp = new SNP([1e9], [new NumFlat.Mat<Complex>(2, 2)],
                          MatrixType.S, MatrixFormat.RI, new Complex(50, 0));
        var ds = DataSetBuilder.FromSnp(snp);

        Assert.True(ds.Contains("S"));
        Assert.Equal("S",  DataSetBuilder.FindCubeSpec(ds, "S"));
        Assert.Equal("Z0", DataSetBuilder.FindCubeSpec(ds, "Z0"));
    }

    [Fact]
    public void ToSnp_QualifiedSpec_ReadsZ0FromTheSameGroup_NotSomeOtherAnalysis()
    {
        // Two analyses with DIFFERENT references: pairing SP1.S with SP2's Z0 would look right
        // and be wrong, which is worse than the 50 Ω fallback.
        var ds = GroupedRun("SP1", z0: new Complex(50, 0));
        var other = GroupedRun("SP2", z0: new Complex(75, 0));
        ds.AddToGroup("SP2", "S",  other.CubesIn("SP2")["S"]);
        ds.AddToGroup("SP2", "Z0", other.CubesIn("SP2")["Z0"]);

        Assert.Equal(50.0, DataSetBuilder.ToSnp(ds, "SP1.S").Z0.Real, 9);
        Assert.Equal(75.0, DataSetBuilder.ToSnp(ds, "SP2.S").Z0.Real, 9);
    }

    // =========================================================================
    //  End to end through the real loader — the owner's actual scenario
    // =========================================================================

    [Fact]
    public async Task GroupedRunNpy_ExposesANetworkView_WhileSnpStaysNullByDesign()
    {
        string p = WriteNpy(GroupedRun("SP1"));

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        var entry = Assert.Single(lib.Entries);

        // Snp MUST stay null: a grouped run goes through the cube path, which can carry a swept
        // axis an SNP cannot (brief-sparam-run-add-trace). The metrics get the narrow view instead.
        Assert.Null(entry.Snp);
        Assert.NotNull(entry.NetworkView);
        Assert.Equal(2, entry.NetworkView!.Ports);
        Assert.Equal(3, entry.NetworkView.Frequencies.Length);
    }

    [Fact]
    public async Task GroupedRunNpy_PopulatesPerPortZ0_NotAnEmptyVector()
    {
        // The second half of the same bug: an empty Z0PerPort silently drops the per-port
        // reference the stability/passivity maths renormalizes against.
        string p = WriteNpy(GroupedRun("SP1", nPorts: 3));

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        var entry = Assert.Single(lib.Entries);

        Assert.Equal(3, entry.Z0PerPort.Count);
        Assert.NotNull(entry.Z0Kind);
        Assert.All(entry.Z0PerPort, z => Assert.Equal(50.0, z.Real, 9));
    }

    [Fact]
    public async Task GroupedRunNpy_ComplexZ0_IsCarriedThrough_NotFlattenedAway()
    {
        string p = WriteNpy(GroupedRun("SP1", z0: new Complex(50, 10)));

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        var entry = Assert.Single(lib.Entries);

        Assert.NotNull(entry.NetworkView);
        Assert.Equal(Z0Kind.UniformComplex, entry.Z0Kind);
        Assert.True(entry.HasUnusualZ0);
        Assert.All(entry.Z0PerPort, z => Assert.Equal(10.0, z.Imaginary, 9));
    }

    [Fact]
    public async Task GroupedRunNpy_FourPort_BuildsAFourPortNetworkView_SoThePortSelectorsAppear()
    {
        // R-stb-9 / R-stb-3: the In/Out selectors only render above 2 ports, so an N-port
        // simulated source has to survive the whole path with its port count intact.
        string p = WriteNpy(GroupedRun("SP1", nPorts: 4));

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        var entry = Assert.Single(lib.Entries);

        Assert.NotNull(entry.NetworkView);
        Assert.Equal(4, entry.NetworkView!.Ports);
        Assert.Equal(4, entry.Z0PerPort.Count);
    }

    // =========================================================================
    //  The picker itself — the surface the owner reported missing
    // =========================================================================

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
    public async Task Picker_SimulatedRun_OffersTheNetworkMetrics_InItsAnalysisGroup()
    {
        string p = WriteNpy(GroupedRun("SP1"));
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var row = BuildRow(lib, p, "SP1.S", PlotType.Rect);
        row.SelectedGroup = "SP1";
        var labels = row.AvailableSignals.Select(x => x.Label).ToList();

        // Post-§4: one item per ordered (i,j) pair rather than a bare "S" quantity — the cube path
        // is still what serves them (S(1,1)/CubeName == "SP1.S").
        Assert.Contains("S(1,1)", labels);
        Assert.Contains("S(2,2)", labels);
        Assert.Contains("Load Stability µ",   labels);
        Assert.Contains("Source Stability µ'", labels);
        Assert.Contains("Rollett K",           labels);
        Assert.Contains("Passivity σmax",      labels);
        Assert.Contains("MaxGain",             labels);
    }

    [Fact]
    public async Task Picker_SimulatedRun_SelectingAMetric_BindsAndComputesRealValues()
    {
        string p = WriteNpy(GroupedRun("SP1"));
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var row = BuildRow(lib, p, "SP1.S", PlotType.Rect);
        row.SelectedGroup = "SP1";
        row.SelectedSignal = row.AvailableSignals.First(x => x.Label == "Load Stability µ");

        var trace = row.Trace;
        Assert.Equal(DerivedParameters.Mu, trace.Derived);
        Assert.Null(trace.CubeName);                        // derived traces take the network path
        Assert.False(trace.IsCubeBound);
        Assert.Equal(2, trace.Data.Ports);
        Assert.NotEmpty(trace.Points);                      // it actually produced a curve
    }

    [Fact]
    public async Task Picker_SweptSCube_OffersNoMetrics_RatherThanFlatteningAnAxisAway()
    {
        // A swept S cube (rank 4) cannot be an SNP. Offering metrics would mean silently plotting
        // one arbitrary sweep slice, so it must be refused outright.
        var ds = new DataSet();
        double[] sweep = [1, 2], freqs = [1e9, 2e9, 3e9];
        double[] ports = [1, 2];
        ds.AddToGroup("SP1", "S", new DataCube(
            [new Axis("Pin", sweep, "dBm"), new Axis("freq", freqs, "Hz"),
             new Axis("i", ports, ""), new Axis("j", ports, "")],
            new Complex[sweep.Length * freqs.Length * 4]));
        string p = WriteNpy(ds, "swept.npy");

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        Assert.False(NetworkMetrics.IsNetworkShaped(lib.Entries[0].Data!));
        Assert.Null(lib.Entries[0].NetworkView);

        var row = BuildRow(lib, p, "SP1.S", PlotType.Rect);
        row.SelectedGroup = "SP1";
        var labels = row.AvailableSignals.Select(x => x.Label).ToList();

        Assert.Contains("S", labels);                       // still fully usable as a cube
        Assert.DoesNotContain("Load Stability µ", labels);
    }

    [Fact]
    public async Task Picker_TouchstoneSource_OffersEachMetricExactlyOnce()
    {
        // The metric block must not double-fire now that there are two producers of it.
        string path = Path.Combine(_dir, "t.s2p");
        File.WriteAllText(path,
            "# GHz S MA R 50" + Environment.NewLine +
            "1.0 0.5 -10 2.0 90 0.05 20 0.4 -30" + Environment.NewLine +
            "2.0 0.5 -20 1.8 80 0.05 10 0.4 -40" + Environment.NewLine);

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);
        await lib.SelectDataSourceAsync(path);

        var trace = new Trace(lib.Entries[0].Snp!, MatrixType.S, 0, 0, DependentVarFormat.Db);
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
        inspector.RebuildAndNotify();
        var row = inspector.Traces[0];

        row.SelectedGroup = "S-Parameters";
        var labels = row.AvailableSignals.Select(x => x.Label).ToList();
        Assert.Equal(1, labels.Count(l => l == "Load Stability µ"));
        Assert.Equal(1, labels.Count(l => l == "Passivity σmax"));
    }

    [Fact]
    public async Task CubeOnlyNpy_WithNoSCube_StillLoadsAsACubeSource_WithNoSnp()
    {
        // Guard the other direction: an HB/DC run has no S cube and must NOT acquire a bogus SNP.
        var ds = new DataSet();
        ds.AddToGroup("HB1", "V", new DataCube(
            [new Axis("harmonic", [0, 1, 2], "")],
            [new Complex(1, 0), new Complex(0.5, 0), new Complex(0.1, 0)]));
        string p = WriteNpy(ds, "hb.npy");

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        var entry = Assert.Single(lib.Entries);

        Assert.Null(entry.Snp);
        Assert.Null(entry.NetworkView);        // no S cube at all — nothing to view as a network
        Assert.False(entry.IsBroken);          // cube-only is a normal source, not a failure
        Assert.NotNull(entry.Data);
    }

    // =========================================================================
    //  Surviving a RE-RUN — the metric traces must not be swept away as stale
    // =========================================================================

    /// <summary>
    /// Builds a plot holding one derived (network-metric) trace on a simulated source, wired to the
    /// library exactly as the real inspector is, and returns the inspector so a reload can be run
    /// through it.
    /// </summary>
    private static PlotInspectorViewModel BuildDerivedPlot(
        DataSourceLibraryViewModel lib, string path, DerivedParameters derived, PlotType plotType,
        out TraceRowViewModel row)
    {
        var trace = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            SourcePath = path, CubeName = "SP1.S", Slice = null, Transform = CubeTransform.None,
        };
        var plot = new Plot(plotType, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
        inspector.RebuildAndNotify();
        row = inspector.Traces[0];
        row.SelectedGroup = "SP1";
        row.SelectedSignal = row.AvailableSignals.First(x => x.Derived == derived);
        return inspector;
    }

    /// <summary>
    /// A derived trace on a SIMULATED source must survive re-running the analysis. It did not: a
    /// simulated run has no <c>Snp</c> by design, so its metric traces bind to the entry's
    /// <c>NetworkView</c> — and <c>PlotInspectorViewModel.OnLibraryChanged</c> built its "still in
    /// the library" set from <c>entry.Snp</c> alone. Every derived trace on a simulated source was
    /// therefore classified stale and DELETED on the first LibraryChanged after a run.
    /// </summary>
    [Theory]
    [InlineData(DerivedParameters.MaxGain,              PlotType.Rect)]
    [InlineData(DerivedParameters.Mu,                   PlotType.Rect)]
    [InlineData(DerivedParameters.Passivity,            PlotType.Rect)]
    [InlineData(DerivedParameters.LoadStabilityCircle,  PlotType.Smith)]
    [InlineData(DerivedParameters.SourceStabilityCircle, PlotType.Smith)]
    public async Task DerivedTraceOnSimulatedSource_SurvivesARerun(
        DerivedParameters derived, PlotType plotType)
    {
        string p = WriteNpy(GroupedRun("SP1"));
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var inspector = BuildDerivedPlot(lib, p, derived, plotType, out var row);
        var trace = row.Trace;
        Assert.Equal(derived, trace.Derived);
        Assert.True(HasGeometry(trace), "trace drew nothing before the re-run");

        // Re-run: the analysis overwrites its own .npy at the same path, then the workspace
        // reloads exactly the changed files (WorkspaceViewModel.RefreshOpenDataDisplaysAsync).
        DataSetExporter.Export(GroupedRun("SP1"), p, ExportFormat.Npy);
        await lib.ReloadChangedAsync([p]);

        Assert.Single(inspector.Traces);
        Assert.Equal(derived, inspector.Traces[0].Trace.Derived);
        Assert.True(HasGeometry(inspector.Traces[0].Trace), "trace drew nothing after the re-run");
    }

    /// <summary>
    /// The trace surviving is not enough — the CARD must still be pointed at the metric. It was not:
    /// <c>RebuildSignals</c> re-found a trace's entry with <c>e.Snp == _trace.Data</c>, which never
    /// matches for a simulated source (no Snp), so the selection fell back to the first signal in
    /// the group and Max Gain silently became S(1,1) on the rebuild every re-run triggers.
    /// </summary>
    [Theory]
    [InlineData(DerivedParameters.MaxGain,             PlotType.Rect)]
    [InlineData(DerivedParameters.Mu,                  PlotType.Rect)]
    [InlineData(DerivedParameters.Passivity,           PlotType.Rect)]
    [InlineData(DerivedParameters.LoadStabilityCircle, PlotType.Smith)]
    public async Task DerivedTraceOnSimulatedSource_KeepsItsCardSelection_AcrossARerun(
        DerivedParameters derived, PlotType plotType)
    {
        string p = WriteNpy(GroupedRun("SP1"));
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var inspector = BuildDerivedPlot(lib, p, derived, plotType, out var row);

        DataSetExporter.Export(GroupedRun("SP1"), p, ExportFormat.Npy);
        await lib.ReloadChangedAsync([p]);

        row = Assert.Single(inspector.Traces);
        Assert.Equal(derived, row.Trace.Derived);                       // the trace itself
        Assert.Equal(derived, row.SelectedSignal?.Derived);             // and the card's picker
        Assert.False(row.Trace.IsCubeBound);                            // never re-pointed at S(i,j)
    }

    /// <summary>An ordinary S(i,j) trace on the same source must be unaffected by that lookup change.</summary>
    [Fact]
    public async Task CubeBoundTraceOnSimulatedSource_KeepsItsCardSelection_AcrossARerun()
    {
        string p = WriteNpy(GroupedRun("SP1"));
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);

        var trace = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            SourcePath = p, CubeName = "SP1.S", Slice = null, Transform = CubeTransform.None,
        };
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
        inspector.RebuildAndNotify();
        var row = inspector.Traces[0];
        row.SelectedGroup  = "SP1";
        row.SelectedSignal = row.AvailableSignals.First(x => x.Label == "S(2,1)");

        DataSetExporter.Export(GroupedRun("SP1"), p, ExportFormat.Npy);
        await lib.ReloadChangedAsync([p]);

        var after = Assert.Single(inspector.Traces);
        Assert.True(after.Trace.IsCubeBound);
        Assert.Equal("S(2,1)", after.SelectedSignal?.Label);
    }

    /// <summary>A rect metric draws into Points; a stability circle draws into the circle lists.</summary>
    private static bool HasGeometry(Trace t) =>
        t.IsStabilityCircle ? t.StabilityCircleCentres.Count > 0 : t.Points.Count > 0;

    /// <summary>
    /// The binding itself, at the layer the deletion read: a reload must not hand out a NEW
    /// NetworkView instance, for exactly the reason <c>RefreshTouchstone</c> preserves the SNP's
    /// identity — a live trace holds the old one and would silently keep drawing stale data even
    /// once the stale-sweep stopped removing it.
    /// </summary>
    [Fact]
    public async Task ReloadingASimulatedSource_KeepsTheNetworkViewInstance()
    {
        string p = WriteNpy(GroupedRun("SP1"));
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        var entry = Assert.Single(lib.Entries);

        var before = entry.NetworkView;
        Assert.NotNull(before);
        double firstFreqBefore = before!.Frequencies[0];

        // A re-run with a DIFFERENT frequency grid, so "same instance" cannot be confused with
        // "nothing was refreshed".
        var rerun = GroupedRun("SP1");
        var sCube = rerun.CubesIn("SP1")["S"];
        var shifted = new DataSet();
        shifted.AddToGroup("SP1", "S", new DataCube(
            [new Axis("freq", [4e9, 5e9, 6e9], "Hz"), sCube.Axes[1], sCube.Axes[2]],
            sCube.ComplexValues));
        shifted.AddToGroup("SP1", "Z0", rerun.CubesIn("SP1")["Z0"]);
        DataSetExporter.Export(shifted, p, ExportFormat.Npy);
        await lib.ReloadChangedAsync([p]);

        Assert.Same(before, entry.NetworkView);            // identity preserved for live traces
        Assert.Equal(1e9, firstFreqBefore, 6);
        Assert.Equal(4e9, entry.NetworkView!.Frequencies[0], 6);   // and it really was refreshed
    }
}
