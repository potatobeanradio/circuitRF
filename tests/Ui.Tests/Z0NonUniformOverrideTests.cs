// ================================================================
//  Z0NonUniformOverrideTests.cs — brief-dd-z0-nonuniform-override gate tests
//
//  The rule this file pins, in full:
//    • Z0 Override OFF  ⇒ ABSOLUTELY NO renormalization. The trace renders the source's own data,
//      each port at its own reference impedance. Marker impedance is reported against that port's
//      own reference.
//    • Z0 Override ON   ⇒ every port of the trace is renormalized to the user's uniform Z0,
//      starting from the source's true per-port references (so a non-uniform source is accounted
//      for). Marker impedance is reported against the override Z0.
//
//  The regression: brief-dd-z0-renormalization.md §1/§2 made the renormalization UNCONDITIONAL.
//  A run whose Terms carry different impedances (Z=50 at port 1, Z=12 at port 2) was silently
//  re-referenced to a uniform port-1 50 Ω, turning a genuine −20 dB return loss into ~−4 dB.
//
//  Oracle: an ideal 50 Ω ↔ 12 Ω transformer. Referenced to its own ports it is S = [[0,1],[1,0]]
//  — a perfect match, |S11| = 0. Renormalized to a uniform 50 Ω it reads
//  |S11| = (50−12)/(50+12) = 0.6129 → −4.25 dB, which is exactly the reported symptom. So the two
//  behaviors are separated by the largest possible margin, with a hand-checkable expected value.
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

public sealed class Z0NonUniformOverrideTests : IDisposable
{
    private readonly string _dir;

    public Z0NonUniformOverrideTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crf-z0nu-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly Complex Port1Z0 = new(50, 0);
    private static readonly Complex Port2Z0 = new(12, 0);

    /// <summary>|Γ| a uniform-50 Ω renormalization produces at BOTH ports of the ideal transformer:
    /// (50−12)/(50+12). Hand-computed, not read back from the code under test.</summary>
    private const double UniformRenormGammaMag = (50.0 - 12.0) / (50.0 + 12.0);   // 0.612903…

    /// <summary>A simulated S-parameter run, shaped exactly like SParameterEngine's output (grouped
    /// "SP1.S" + per-port "SP1.Z0"): an ideal 50↔12 Ω transformer, perfectly matched at its own
    /// port references at every frequency.</summary>
    private static DataSet TransformerRun(string group = "SP1")
    {
        double[] freqs = [1e9, 2e9];
        var s = new Complex[freqs.Length * 4];
        for (int f = 0; f < freqs.Length; f++)
        {
            s[f * 4 + 0] = Complex.Zero;     // S11 — matched
            s[f * 4 + 1] = Complex.One;      // S12
            s[f * 4 + 2] = Complex.One;      // S21
            s[f * 4 + 3] = Complex.Zero;     // S22 — matched
        }

        var ds = new DataSet();
        ds.AddToGroup(group, "S", new DataCube(
            [new Axis("freq", freqs, "Hz"),
             new Axis("i", [1.0, 2.0], ""),
             new Axis("j", [1.0, 2.0], "")],
            s));
        ds.AddToGroup(group, "Z0", new DataCube(
            [new Axis("port", [1.0, 2.0], "")],
            [Port1Z0, Port2Z0]));
        return ds;
    }

    private string WriteNpy(DataSet ds, string name = "run.npy")
    {
        string p = Path.Combine(_dir, name);
        DataSetExporter.Export(ds, p, ExportFormat.Npy);
        return p;
    }

    private async Task<(DataSourceLibraryViewModel lib, string path)> LoadTransformerRunAsync()
    {
        string p = WriteNpy(TransformerRun());
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(p);
        await lib.SelectDataSourceAsync(p);
        return (lib, p);
    }

    private static TraceRowViewModel BuildRow(DataSourceLibraryViewModel lib, string path,
                                              PlotType plotType = PlotType.Smith)
    {
        var trace = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            SourcePath = path, CubeName = "SP1.S", Slice = null, Transform = CubeTransform.None,
        };
        var plot = new Plot(plotType, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
        inspector.RebuildAndNotify();
        return inspector.Traces[0];
    }

    /// <summary>Selects a picker item by label, guaranteeing OnSelectedSignalChanged actually runs
    /// even when the item is already the auto-selected default (a reference-equal re-assignment is a
    /// no-op under CommunityToolkit's generated setter). Mirrors Z0RenormalizationTests.</summary>
    private static void SelectSignal(TraceRowViewModel row, string label)
    {
        var target = row.AvailableSignals.First(x => x.Label == label);
        if (!ReferenceEquals(row.SelectedSignal, target)) { row.SelectedSignal = target; return; }
        row.SelectedSignal = row.AvailableSignals.First(x => x.Label != label);
        row.SelectedSignal = target;
    }

    private static TraceRowViewModel RowFor(DataSourceLibraryViewModel lib, string path,
                                            string element, PlotType plotType = PlotType.Smith)
    {
        var row = BuildRow(lib, path, plotType);
        row.SelectedGroup = "SP1";
        SelectSignal(row, element);
        return row;
    }

    // ---- The bug itself: Override off must not renormalize ------------------

    [Fact]
    public async Task CubeTrace_OverrideOff_RendersSourceDataUnrenormalized()
    {
        var (lib, p) = await LoadTransformerRunAsync();
        var row = RowFor(lib, p, "S(1,1)");

        Assert.False(row.Z0OverrideEnabled, "pre-condition: Override starts unchecked");
        Assert.True(row.Trace.SourceZ0IsUnusual, "pre-condition: the source is non-uniform");

        // The perfect match the source actually holds — NOT the 0.6129 a uniform-50 Ω
        // re-reference would produce.
        Assert.NotEmpty(row.Trace.Points);
        foreach (var pt in row.Trace.Points)
        {
            Assert.Equal(0.0, Math.Sqrt(pt.X * pt.X + pt.Y * pt.Y), precision: 9);
        }
    }

    [Fact]
    public async Task CubeTrace_OverrideOff_S22_AlsoUnrenormalized()
    {
        // The port whose reference is NOT the port-1 value is where the old code did most damage:
        // it re-referenced port 2 from 12 Ω to 50 Ω without being asked.
        var (lib, p) = await LoadTransformerRunAsync();
        var row = RowFor(lib, p, "S(2,2)");

        Assert.NotEmpty(row.Trace.Points);
        foreach (var pt in row.Trace.Points)
            Assert.Equal(0.0, Math.Sqrt(pt.X * pt.X + pt.Y * pt.Y), precision: 9);
    }

    [Fact]
    public async Task CubeTrace_OverrideOn_RenormalizesAllPortsFromPerPortSource()
    {
        var (lib, p) = await LoadTransformerRunAsync();
        var row = RowFor(lib, p, "S(1,1)");

        row.Z0OverrideEnabled = true;   // Z0 box is seeded at the source's port-1 value, 50 Ω

        Assert.Equal(Port1Z0, row.Trace.Z0);
        Assert.NotEmpty(row.Trace.Points);
        foreach (var pt in row.Trace.Points)
        {
            // (50−12)/(50+12) — the non-uniform port-2 reference IS accounted for; a renormalization
            // that ignored it would leave |S11| at 0.
            Assert.Equal(UniformRenormGammaMag, Math.Sqrt(pt.X * pt.X + pt.Y * pt.Y), precision: 6);
        }
    }

    [Fact]
    public async Task CubeTrace_OverrideOn_CustomZ0_MatchesRFNetworkSToS()
    {
        var (lib, p) = await LoadTransformerRunAsync();
        var row = RowFor(lib, p, "S(1,1)");

        row.Z0OverrideEnabled = true;
        row.Z0String = "75";
        Assert.Equal("", row.Z0ErrorText);

        var m = new NumFlat.Mat<Complex>(2, 2);
        m[0, 0] = Complex.Zero; m[0, 1] = Complex.One;
        m[1, 0] = Complex.One;  m[1, 1] = Complex.Zero;
        var expected = RFNetwork.SToS(m, [Port1Z0, Port2Z0],
                                      RFNetwork.Z0Array(new Complex(75, 0), 2))[0, 0];

        Assert.NotEmpty(row.Trace.Points);
        Assert.Equal(expected.Real,      row.Trace.Points[0].X, precision: 6);
        Assert.Equal(expected.Imaginary, row.Trace.Points[0].Y, precision: 6);
    }

    [Fact]
    public async Task CubeTrace_OverrideToggle_RoundTripsBackToSourceData()
    {
        var (lib, p) = await LoadTransformerRunAsync();
        var row = RowFor(lib, p, "S(1,1)");

        var before = row.Trace.Points.ToArray();

        row.Z0OverrideEnabled = true;
        row.Z0String = "75";
        Assert.NotEqual(before[0], row.Trace.Points[0]);

        row.Z0OverrideEnabled = false;   // unchecking must restore the raw source data, not keep 75 Ω
        Assert.Equal(before.Length, row.Trace.Points.Count);
        for (int i = 0; i < before.Length; i++)
            Assert.Equal(before[i], row.Trace.Points[i]);
    }

    // ---- Marker impedance readout ------------------------------------------

    [Fact]
    public async Task CubeMarkerImpedance_OverrideOff_ReportsAgainstThatPortsOwnZ0()
    {
        var (lib, p) = await LoadTransformerRunAsync();
        var row = RowFor(lib, p, "S(2,2)", PlotType.Table);

        var trace  = row.Trace;
        var marker = new Marker(trace, 1e9, isMulti: false, isDelta: false, index: 1) { Freq = 1e9 };
        marker.UseNormalizedImpedance = false;

        Assert.True(trace.MarkerShowsImpedance(marker));
        // Port 2's own reference — the readout must NOT come back as 50 Ω (the port-1 mirror the
        // Z0 box shows) nor as anything derived from a renormalization that never happened.
        Assert.Equal(Port2Z0, trace.MarkerZ0);

        // S22 = 0 at its own reference ⇒ Z = 12 Ω exactly.
        Assert.Equal($"impedance={marker.FormatComplex(Port2Z0)} Ω",
                     trace.GetMarkerImpedanceString(marker));
    }

    [Fact]
    public async Task CubeMarkerImpedance_OverrideOn_ReportsAgainstOverrideZ0()
    {
        var (lib, p) = await LoadTransformerRunAsync();
        var row = RowFor(lib, p, "S(2,2)", PlotType.Table);

        row.Z0OverrideEnabled = true;
        row.Z0String = "50";

        var trace  = row.Trace;
        var marker = new Marker(trace, 1e9, isMulti: false, isDelta: false, index: 1) { Freq = 1e9 };
        marker.UseNormalizedImpedance = false;

        Assert.Equal(new Complex(50, 0), trace.MarkerZ0);

        // With every port at 50 Ω, port 2 of a 50↔12 transformer looks like 12 Ω through the
        // (now visible) mismatch: Γ = −(50−12)/(50+12) at port 2 ⇒ Z = 50·(1+Γ)/(1−Γ) = 12 Ω.
        // The IMPEDANCE is a physical quantity and must be reference-independent — the same 12 Ω
        // this port reads with Override off, only now arrived at the long way round.
        string result = trace.GetMarkerImpedanceString(marker);
        Assert.Equal($"impedance={marker.FormatComplex(Port2Z0)} Ω", result);
    }

    // ---- Network (Touchstone/SNP) path ------------------------------------

    [Fact]
    public void NetworkTrace_OverrideOff_RendersSourceDataUnrenormalized()
    {
        var snp = new SNP([1e9], 2, MatrixType.S, MatrixFormat.MA, Port1Z0);
        snp.Matrices[0][0, 0] = Complex.Zero;
        snp.Matrices[0][0, 1] = Complex.One;
        snp.Matrices[0][1, 0] = Complex.One;
        snp.Matrices[0][1, 1] = Complex.Zero;

        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Complex)
        {
            SourceZ0PerPort   = [Port1Z0, Port2Z0],
            SourceZ0IsUnusual = true,
        };
        trace.BuildPath(PlotType.Smith, FreqUnit.GHz);

        Assert.Single(trace.Points);
        Assert.Equal(0.0, Math.Sqrt(trace.Points[0].X * trace.Points[0].X +
                                    trace.Points[0].Y * trace.Points[0].Y), precision: 6);

        // Same trace with Override on renormalizes to the uniform trace Z0 (50 Ω).
        trace.Z0OverrideEnabled = true;
        trace.BuildPath(PlotType.Smith, FreqUnit.GHz);
        Assert.Equal(UniformRenormGammaMag,
                     Math.Sqrt(trace.Points[0].X * trace.Points[0].X +
                               trace.Points[0].Y * trace.Points[0].Y), precision: 6);
    }

    // ---- Persistence --------------------------------------------------------

    [Fact]
    public void OverrideFlag_PersistsToTraceConfig()
    {
        var snp   = new SNP([1e9], 2, MatrixType.S, MatrixFormat.MA, Port1Z0);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db) { SourcePath = "/x.s2p" };

        Assert.False(DataDisplayViewModel.BuildTraceConfig(trace, configDir: "").Z0Override);

        trace.Z0OverrideEnabled = true;
        trace.Z0 = new Complex(75, 0);
        var tc = DataDisplayViewModel.BuildTraceConfig(trace, configDir: "");
        Assert.True(tc.Z0Override);
        Assert.Equal("75", tc.Z0);

        // A copied trace carries the flag (Duplicate Trace / plot copy must not silently drop it).
        var copy = new Trace(trace);
        Assert.True(copy.Z0OverrideEnabled);
    }
}
