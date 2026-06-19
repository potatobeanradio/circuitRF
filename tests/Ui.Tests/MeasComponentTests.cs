// ================================================================
//  MeasComponentTests.cs  —  Gate tests for the MEAS Library component
//  (brief-meas-component.md §Part 2)
//
//  1. Meas_NoInstanceEmitted          — MEAS is skipped from tb.Instances
//  2. Meas_RowsBecomeMeasurements     — MEAS rows route to tb.Measurements
//  3. Meas_DuplicateName_FirstKept    — duplicate name → first kept, conflict logged
//  4. Meas_InsideCell_Ignored         — MEAS inside a sub-cell → warning, not attached
//  5. Meas_RoundTrip_Csch             — MEAS component survives .csch round-trip
// ================================================================

using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for the MEAS annotation component.
/// MEAS rows route to TestBench.Measurements (top-level only), never emitted as Instances.
/// </summary>
public class MeasComponentTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static EditableComponent Meas(params (string Name, string Expression)[] rows)
    {
        var c = new EditableComponent { Symbol = SymbolKind.Meas, InstanceName = "MEAS1" };
        foreach (var (name, expr) in rows)
            c.Parameters.Add(new EditableParameter { Name = name, Expression = expr });
        return c;
    }

    private static EditableComponent Meas(string instanceName, params (string Name, string Expression)[] rows)
    {
        var c = new EditableComponent { Symbol = SymbolKind.Meas, InstanceName = instanceName };
        foreach (var (name, expr) in rows)
            c.Parameters.Add(new EditableParameter { Name = name, Expression = expr });
        return c;
    }

    private static EditableComponent Resistor(string name, double cx, double cy, string r)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Resistor, X = cx, Y = cy };
        c.Parameters.Add(new EditableParameter { Name = "R", Expression = r });
        return c;
    }

    private static EditableComponent Ground(double cx, double cy)
        => new() { Symbol = SymbolKind.Ground, X = cx, Y = cy };

    private static EditableComponent Pin(int num, double cx, double cy)
    {
        var c = new EditableComponent { Symbol = SymbolKind.Pin, X = cx, Y = cy };
        c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
        return c;
    }

    // ── Test 1: Meas_NoInstanceEmitted ───────────────────────────────────────

    /// <summary>
    /// A MEAS component must never appear in TestBench.Instances — only in Measurements.
    /// A co-placed Resistor IS emitted normally to confirm the general path is unaffected.
    /// </summary>
    [Fact]
    public void Meas_NoInstanceEmitted()
    {
        var model = new SchematicEditModel();
        model.Components.Add(Meas(("Pout", "SP1.V[0,0]")));
        model.Components.Add(Resistor("R1", 0, 400, "50"));
        model.Components.Add(Ground(0, 600));

        var result = NetExtractor.Extract(model);

        // No MEAS-backed instance.
        Assert.DoesNotContain(result.TestBench.Instances,
            i => i.Reference.Equals("MEAS", System.StringComparison.OrdinalIgnoreCase));

        // R1 IS emitted.
        Assert.Contains(result.TestBench.Instances, i => i.InstanceName == "R1");

        // Pout appears as a measurement, not an instance.
        Assert.Single(result.TestBench.Measurements);
        Assert.Equal("Pout", result.TestBench.Measurements[0].Name);
    }

    // ── Test 2: Meas_RowsBecomeMeasurements ──────────────────────────────────

    /// <summary>
    /// Two MEAS rows Pout and PAE must appear in tb.Measurements in declaration order,
    /// with expressions preserved verbatim.
    /// </summary>
    [Fact]
    public void Meas_RowsBecomeMeasurements()
    {
        var model = new SchematicEditModel();
        model.Components.Add(Meas(
            ("Pout", "10*log10(abs(HB1.V[0,0])^2/50)"),
            ("PAE",  "(Pout - Pin) / Pdc * 100")));

        var result = NetExtractor.Extract(model);

        var meas = result.TestBench.Measurements;
        Assert.Equal(2, meas.Count);
        Assert.Equal("Pout", meas[0].Name);
        Assert.Equal("10*log10(abs(HB1.V[0,0])^2/50)", meas[0].Expression);
        Assert.Equal("PAE",  meas[1].Name);
        Assert.Equal("(Pout - Pin) / Pdc * 100", meas[1].Expression);
    }

    // ── Test 3: Meas_DuplicateName_FirstKept ─────────────────────────────────

    /// <summary>
    /// When two rows share the same name (within the same or across MEAS components),
    /// the first definition is kept, the second is dropped, and a conflict is reported.
    /// </summary>
    [Fact]
    public void Meas_DuplicateName_FirstKept()
    {
        var model = new SchematicEditModel();
        model.Components.Add(Meas("MEAS1", ("Gain", "SP1.S[0,1]"), ("Gain", "SP1.S[1,0]")));

        var result = NetExtractor.Extract(model);

        // Only one Gain measurement (the first).
        var meas = result.TestBench.Measurements;
        Assert.Single(meas);
        Assert.Equal("Gain",        meas[0].Name);
        Assert.Equal("SP1.S[0,1]",  meas[0].Expression);

        // A conflict message must be reported.
        Assert.Contains(result.Conflicts, c => c.Contains("'Gain'") && c.Contains("more than once"));
    }

    // ── Test 4: Meas_InsideCell_Ignored ──────────────────────────────────────

    /// <summary>
    /// A MEAS inside a sub-cell must NOT route its rows to the cell's design model;
    /// a conflict warning must be raised.  The top-level tb.Measurements remains empty.
    /// </summary>
    [Fact]
    public void Meas_InsideCell_Ignored()
    {
        var sub = new SchematicEditModel();
        sub.Components.Add(Pin(1, 0, 200));
        sub.Components.Add(Pin(2, 0, 600));
        sub.Components.Add(Meas(("Pout", "HB1.V[0,0]")));
        sub.Wires.Add(MakeWire((0, 0), (0, 200)));
        sub.Wires.Add(MakeWire((0, 400), (0, 600)));

        var top = new SchematicEditModel();
        var cellComp = new EditableComponent
            { InstanceName = "U1", CellRef = "SubCell", X = 0, Y = 200 };
        top.Components.Add(cellComp);

        var resolver = new StubCellResolver(new()
        {
            ["SubCell"] = new CellResolution("SubCell", sub, []),
        });

        var result = NetExtractor.Extract(top, cells: resolver);

        // Top testbench has no measurements.
        Assert.Empty(result.TestBench.Measurements);

        // A warning about MEAS inside a cell must be present.
        Assert.Contains(result.Conflicts, c =>
            c.Contains("MEAS") && c.Contains("cell") && c.Contains("ignored"));
    }

    // ── Test 5: Meas_RoundTrip_Csch ──────────────────────────────────────────

    /// <summary>
    /// A MEAS component with two rows must survive a .csch JSON round-trip:
    /// SymbolKind.Meas, instance name, and parameter rows all preserved.
    /// </summary>
    [Fact]
    public void Meas_RoundTrip_Csch()
    {
        var model = new SchematicEditModel();
        model.Components.Add(Meas("MEAS1",
            ("Gain", "dB(SP1.S[0,1])"),
            ("NF",   "SP1.NF[0]")));

        string json = SchematicPersistence.Serialize(model);
        var (loaded, _, _) = SchematicPersistence.Deserialize(json);

        var measComps = loaded.Components
            .Where(c => c.Symbol == SymbolKind.Meas)
            .ToList();
        Assert.Single(measComps);

        var comp = measComps[0];
        Assert.Equal("MEAS1", comp.InstanceName);
        Assert.Equal(2, comp.Parameters.Count);
        Assert.Equal("Gain",        comp.Parameters[0].Name);
        Assert.Equal("dB(SP1.S[0,1])",comp.Parameters[0].Expression);
        Assert.Equal("NF",          comp.Parameters[1].Name);
        Assert.Equal("SP1.NF[0]",   comp.Parameters[1].Expression);
    }

    // ── Wire / resolver helpers ───────────────────────────────────────────────

    private static EditableWire MakeWire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    private sealed class StubCellResolver : ICellResolver
    {
        private readonly System.Collections.Generic.Dictionary<string, CellResolution> _map;

        public StubCellResolver(System.Collections.Generic.Dictionary<string, CellResolution> map)
            => _map = map;

        public CellResolution? Resolve(EditableComponent comp, SchematicEditModel _)
            => comp.CellRef is not null && _map.TryGetValue(comp.CellRef, out var r) ? r : null;
    }
}
