// ================================================================
//  NodePickerLabeledFilterTests.cs
//  Gate tests for brief node-picker-labeled-filter
//
//  T1  — Extract_LabeledNets_Collected
//  T2  — LabelNamedN1_IsLabeled
//  T3  — PinName_NotLabeled
//  T5  — Persistence_RoundTrips_LabeledNodes
//  T7  — Picker_FiltersToLabeled
//  T8  — Picker_EmptyWhenNoLabels_FilterOn
//  T9  — Picker_ShowAll_RevealsAll
//  T10 — Picker_AbsentProvenance_ShowsAll
//  T11 — SideCube_NotSelectable
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Schematic;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class NodePickerLabeledFilterTests
{
    // ── SchematicEditModel helpers ────────────────────────────────────────────

    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    private static EditableComponent Resistor(string name, double x, double y)
        => new() { InstanceName = name, Symbol = SymbolKind.Resistor, X = x, Y = y };

    private static EditableComponent MakePin(int num, string name, double x = 0, double y = 0)
    {
        var c = new EditableComponent { InstanceName = $"Pin{num}", Symbol = SymbolKind.Pin, X = x, Y = y };
        c.Parameters.Add(new EditableParameter { Name = "Num",      Expression = num.ToString() });
        c.Parameters.Add(new EditableParameter { Name = "Name",     Expression = name });
        c.Parameters.Add(new EditableParameter { Name = "Polarity", Expression = "" });
        return c;
    }

    // ── DataSet / UI helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Builds a DataSet with a V cube [node × harmonic] and optionally a
    /// __LabeledNodes side cube.  nodeLabels = null means no side cube.
    /// </summary>
    private static DataSet BuildHbDataSet(string[] nodeNames, string[]? labeledNodes)
    {
        int N = nodeNames.Length;
        int K = 2;                 // 3 harmonics (DC + 2)

        var nodeVals = Enumerable.Range(0, N).Select(i => (double)i).ToArray();
        var harmVals = Enumerable.Range(0, K + 1).Select(k => (double)k * 1e9).ToArray();
        var nodeAxis = new Axis("node",     nodeVals, "", nodeNames);
        var harmAxis = new Axis("harmonic", harmVals, "Hz");

        var data = new Complex[N * (K + 1)];
        for (int n = 0; n < N; n++)
            data[n * (K + 1)] = new Complex(n + 1, 0);   // DC component = node index + 1

        var ds = new DataSet();
        ds.Add("V", new DataCube(new[] { nodeAxis, harmAxis }, data));

        if (labeledNodes is not null && labeledNodes.Length > 0)
        {
            var lIdx = Enumerable.Range(0, labeledNodes.Length).Select(i => (double)i).ToArray();
            ds.Add("__LabeledNodes", new DataCube(
                [new Axis("label", lIdx, "", labeledNodes)],
                new double[labeledNodes.Length]));
        }
        else if (labeledNodes is not null)
        {
            // Present but empty — signals "schematic ran, user labeled nothing".
            ds.Add("__LabeledNodes", new DataCube(
                [new Axis("label", Array.Empty<double>(), "", Array.Empty<string>())],
                Array.Empty<double>()));
        }

        return ds;
    }

    private static async System.Threading.Tasks.Task<(string path, DataSourceLibraryViewModel lib)>
        ExportAndLoad(DataSet ds)
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf_nplf_{Guid.NewGuid():N}.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);
        return (path, lib);
    }

    private static Trace MakeCubeTrace(string sourcePath, string cubeName)
    {
        var snp   = new SNP(new[] { 1e9 }, 2);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.SourcePath = sourcePath;
        trace.CubeName   = cubeName;
        trace.Slice      = null;
        trace.Transform  = CubeTransform.None;
        return trace;
    }

    private static (TraceRowViewModel trvm, PlotInspectorViewModel pivm) BuildInspector(
        DataSourceLibraryViewModel lib, string sourcePath)
    {
        var trace = MakeCubeTrace(sourcePath, "V");
        var plot  = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(trace);

        var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
        inspector.RebuildAndNotify();

        return (inspector.Traces[0], inspector);
    }

    // ── T1: Extract_LabeledNets_Collected ────────────────────────────────────

    [Fact]
    public void T1_Extract_LabeledNets_Collected()
    {
        var model = new SchematicEditModel();
        // Wire (0,0)→(0,400): R1 port0 at (0,0), port1 at (0,400).
        model.Wires.Add(Wire((0, 0), (0, 400)));
        model.Components.Add(Resistor("R1", 0, 200));
        // User-placed label "Vout" on the wire.
        model.NetLabels.Add(new EditableNetLabel { X = 0, Y = 200, Name = "Vout" });

        // Separate isolated resistor → auto-named net.
        model.Components.Add(Resistor("R2", 400, 200));

        var result = NetExtractor.Extract(model);

        // "Vout" from a user label → in LabeledNets.
        Assert.Contains("Vout", result.TestBench.LabeledNets);

        // Auto-named net (e.g. "n1") → NOT in LabeledNets.
        foreach (var n in result.TestBench.LabeledNets)
            Assert.Equal("Vout", n);    // only "Vout" should be in the set
    }

    // ── T2: LabelNamedN1_IsLabeled ───────────────────────────────────────────

    [Fact]
    public void T2_LabelNamedN1_IsLabeled()
    {
        var model = new SchematicEditModel();
        model.Wires.Add(Wire((0, 0), (0, 400)));
        model.Components.Add(Resistor("R1", 0, 200));
        // User explicitly named their net "n1" — provenance, not pattern.
        model.NetLabels.Add(new EditableNetLabel { X = 0, Y = 200, Name = "n1" });

        var result = NetExtractor.Extract(model);

        Assert.Contains("n1", result.TestBench.LabeledNets);
    }

    // ── T3: PinName_NotLabeled ───────────────────────────────────────────────

    [Fact]
    public void T3_PinName_NotLabeled()
    {
        var model = new SchematicEditModel();
        // Pin port is at local (100,0); Pin at (-100,0) → port at world (0,0).
        model.Components.Add(MakePin(1, "rf_in", -100, 0));

        // Wire with a net label on the same net as the Pin.
        // Pin port is at (0,0), on the wire from (0,0) to (0,400). Pin overrides the label.
        model.Wires.Add(Wire((0, 0), (0, 400)));
        model.NetLabels.Add(new EditableNetLabel { X = 0, Y = 200, Name = "Vlabel" });

        // Ground at (400, 200) — port at (400, 0).
        model.Components.Add(new EditableComponent
            { InstanceName = "GND1", Symbol = SymbolKind.Ground, X = 400, Y = 200 });
        model.NetLabels.Add(new EditableNetLabel { X = 400, Y = 0, Name = "gnd_label" });

        var result = NetExtractor.Extract(model);

        // The Pin overrides the label on its net → "Vlabel" is NOT in LabeledNets.
        Assert.DoesNotContain("Vlabel", result.TestBench.LabeledNets);

        // Ground "0" overrides everything → "gnd_label" NOT in LabeledNets.
        Assert.DoesNotContain("gnd_label", result.TestBench.LabeledNets);

        // "0" itself is never a labeled net.
        Assert.DoesNotContain("0", result.TestBench.LabeledNets);
    }

    // ── T5: Persistence_RoundTrips_LabeledNodes ───────────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task T5_Persistence_RoundTrips_LabeledNodes()
    {
        var ds = BuildHbDataSet(
            ["Vin", "Vout", "n1", "n2"],
            ["Vin", "Vout"]);

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var entry = lib.Entries.FirstOrDefault();
            Assert.NotNull(entry);
            var loaded = entry!.Data;
            Assert.NotNull(loaded);

            Assert.True(loaded!.Contains("__LabeledNodes"),
                "__LabeledNodes must survive the .npy round-trip");

            var lblCube = loaded["__LabeledNodes"];
            Assert.True(lblCube.Axes.Count > 0);
            var labels = lblCube.Axes[0].Labels;
            Assert.NotNull(labels);
            Assert.Contains("Vin",  labels!);
            Assert.Contains("Vout", labels!);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── T7: Picker_FiltersToLabeled ───────────────────────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task T7_Picker_FiltersToLabeled()
    {
        // Node axis: Vin(0), n1(1), Vout(2), n2(3).  Labeled: Vin and Vout.
        var ds = BuildHbDataSet(
            ["Vin", "n1", "Vout", "n2"],
            ["Vin", "Vout"]);

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var (trvm, _) = BuildInspector(lib, path);

            // Find the "node" axis row.
            var nodeRow = trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "node");
            Assert.NotNull(nodeRow);

            // Filtered picker shows only labeled nodes.
            Assert.Equal(2, nodeRow!.PinOptions.Count);
            Assert.Contains("Vin",  nodeRow.PinOptions);
            Assert.Contains("Vout", nodeRow.PinOptions);
            Assert.DoesNotContain("n1", nodeRow.PinOptions);
            Assert.DoesNotContain("n2", nodeRow.PinOptions);

            // TruePinIndex for "Vout" (display index 1) must be 2 (true cube axis position).
            // Simulate user selecting display index 1 → Vout.
            nodeRow.PinIndex = 1;
            Assert.Equal(2, nodeRow.TruePinIndex);

            // TruePinIndex for "Vin" (display index 0) must be 0.
            nodeRow.PinIndex = 0;
            Assert.Equal(0, nodeRow.TruePinIndex);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── T8: Picker_EmptyWhenNoLabels_FilterOn ────────────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task T8_Picker_EmptyWhenNoLabels_FilterOn()
    {
        // Present-but-empty __LabeledNodes → schematic ran, user tagged nothing.
        var ds = BuildHbDataSet(["Vin", "Vout", "n1"], []);  // empty string[] = present-but-empty

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var (trvm, _) = BuildInspector(lib, path);

            // ShowAllNodes should be false (default; __LabeledNodes present).
            Assert.False(trvm.ShowAllNodes);

            var nodeRow = trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "node");
            Assert.NotNull(nodeRow);

            // Empty labeled set → no options shown.
            Assert.Empty(nodeRow!.PinOptions);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── T9: Picker_ShowAll_RevealsAll ─────────────────────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task T9_Picker_ShowAll_RevealsAll()
    {
        var ds = BuildHbDataSet(
            ["Vin", "n1", "Vout"],
            ["Vin", "Vout"]);

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var (trvm, _) = BuildInspector(lib, path);

            // Default: filtered.
            var nodeRow = trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "node");
            Assert.NotNull(nodeRow);
            Assert.Equal(2, nodeRow!.PinOptions.Count);

            // Toggle ShowAllNodes → all 3 nodes appear.
            trvm.ShowAllNodes = true;

            nodeRow = trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "node");
            Assert.NotNull(nodeRow);
            Assert.Equal(3, nodeRow!.PinOptions.Count);
            Assert.Contains("n1", nodeRow.PinOptions);

            // TruePinIndex should be 1:1 with display index when unfiltered.
            nodeRow.PinIndex = 1;   // "n1" at display index 1
            Assert.Equal(1, nodeRow.TruePinIndex);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── T10: Picker_AbsentProvenance_ShowsAll ─────────────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task T10_Picker_AbsentProvenance_ShowsAll()
    {
        // No __LabeledNodes cube (hand-written netlist or S-param file).
        var ds = BuildHbDataSet(["Vin", "Vout", "n1"], null);

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var (trvm, _) = BuildInspector(lib, path);

            // Absent provenance → ShowAllNodes defaults to true.
            Assert.True(trvm.ShowAllNodes);

            var nodeRow = trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "node");
            Assert.NotNull(nodeRow);

            // All nodes visible.
            Assert.Equal(3, nodeRow!.PinOptions.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── Picker_FiltersAfterSweep ─────────────────────────────────────────────
    //
    // After StackSweepAxis the V cube is [sweep, node, harmonic].
    // __LabeledNodes remains rank-1 [label].
    // The picker must still show only the labeled nodes (Vin, Vout), not n1/n2.

    private static DataSet BuildSweptHbDataSet(
        string[] nodeLabels, string[] labeledNodes, int sweepPts = 3)
    {
        // V cube: [node × harmonic] per sweep point; stacked below.
        var nodeAxis = new Axis("node",     Enumerable.Range(0, nodeLabels.Length).Select(i => (double)i).ToArray(),
                                "", nodeLabels);
        var harmAxis = new Axis("harmonic", new[] { 0.0, 1e9 }, "Hz");
        var vData    = new System.Numerics.Complex[nodeLabels.Length * 2];

        var sweepVals = Enumerable.Range(0, sweepPts).Select(i => (double)i).ToArray();
        var sweepAxis = new Axis("Pin", sweepVals);

        // Build per-point DataSets, then stack.
        var pts = new DataSet[sweepPts];
        for (int i = 0; i < sweepPts; i++)
        {
            var pt = new DataSet();
            pt.Add("V", new DataCube(new[] { nodeAxis, harmAxis }, vData));

            // Every point carries the same __LabeledNodes cube (sweep-invariant metadata).
            if (labeledNodes.Length > 0)
            {
                var lblIdx  = Enumerable.Range(0, labeledNodes.Length).Select(k => (double)k).ToArray();
                var lblAxis = new Axis("label", lblIdx, "", labeledNodes);
                pt.Add("__LabeledNodes", new DataCube(new[] { lblAxis }, new double[labeledNodes.Length]));
            }
            pts[i] = pt;
        }

        return DataSet.StackSweepAxis(sweepAxis, pts);
    }

    [Fact]
    public async System.Threading.Tasks.Task Picker_FiltersAfterSweep()
    {
        // Swept DataSet: V is [sweep=3, node=4, harmonic=2], __LabeledNodes is [label=2].
        // Only Vin and Vout are labeled; n1 and n2 are internal/auto-named.
        var ds = BuildSweptHbDataSet(
            nodeLabels:   ["Vin", "Vout", "n1", "n2"],
            labeledNodes: ["Vin", "Vout"]);

        // The V cube after stacking must be rank-3.
        Assert.Equal(3, ds["V"].Rank);
        Assert.Equal("Pin",  ds["V"].Axes[0].Name);
        Assert.Equal("node", ds["V"].Axes[1].Name);

        // __LabeledNodes must remain rank-1 (not swept).
        Assert.Equal(1, ds["__LabeledNodes"].Rank);
        Assert.Equal("label", ds["__LabeledNodes"].Axes[0].Name);

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            // Build inspector bound to the swept DataSet with V as the trace signal.
            var (trvm, _) = BuildInspector(lib, path);

            // Picker must filter to labeled nodes only.
            Assert.False(trvm.ShowAllNodes, "Default: filter ON when __LabeledNodes is present");

            var nodeRow = trvm.AxisRoles.FirstOrDefault(r => r.AxisName == "node");
            Assert.NotNull(nodeRow);
            Assert.Equal(2, nodeRow!.PinOptions.Count);
            Assert.Contains("Vin",  nodeRow.PinOptions);
            Assert.Contains("Vout", nodeRow.PinOptions);
            Assert.DoesNotContain("n1", nodeRow.PinOptions);
            Assert.DoesNotContain("n2", nodeRow.PinOptions);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── T11: SideCube_NotSelectable ──────────────────────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task T11_SideCube_NotSelectable()
    {
        var ds = BuildHbDataSet(["Vin", "Vout"], ["Vin", "Vout"]);

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var (trvm, _) = BuildInspector(lib, path);

            // __LabeledNodes must not appear in the signal combo.
            var signals = trvm.AvailableSignals;
            var metaEntry = signals.FirstOrDefault(s =>
                s.IsCubeBound && s.CubeName == "__LabeledNodes");
            Assert.Null(metaEntry);

            // But "V" (the actual signal cube) MUST appear.
            var vEntry = signals.FirstOrDefault(s =>
                s.IsCubeBound && s.CubeName == "V");
            Assert.NotNull(vEntry);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
