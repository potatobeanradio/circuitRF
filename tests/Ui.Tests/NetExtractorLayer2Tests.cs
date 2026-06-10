using System.Linq;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Layer 2 gate: net naming.
/// Ground → "0"; labeled net → label text; auto-names deterministically stable;
/// two-different-labels-on-one-net → conflict recorded (extraction still succeeds).
/// </summary>
public class NetExtractorLayer2Tests
{
    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    private static EditableComponent Resistor(string name, double x, double y)
        => new() { InstanceName = name, Symbol = SymbolKind.Resistor, X = x, Y = y };

    private static string NetOf(NetExtractor.ExtractionResult r, string name, int idx)
        => r.TestBench.Instances.First(i => i.InstanceName == name).NetBindings[idx];

    // ── Test 1: Ground pin → net "0" ─────────────────────────────────────────

    [Fact]
    public void GroundPin_NetIsZero()
    {
        var model = new SchematicEditModel();
        // Ground at (0,0): its single port is at local (0,0) → world (0,0).
        model.Components.Add(new EditableComponent
            { InstanceName = "GND1", Symbol = SymbolKind.Ground, X = 0, Y = 0 });
        // R1 at (0,200): port0=(0,0) — coincides with GND1.
        model.Components.Add(Resistor("R1", 0, 200));

        var result = NetExtractor.Extract(model);

        Assert.Equal("0", NetOf(result, "R1", 0));
    }

    // ── Test 2: labeled net takes the label text ─────────────────────────────

    [Fact]
    public void LabeledNet_TakesLabelName()
    {
        var model = new SchematicEditModel();
        // Wire (0,0)→(0,400). R1.port0=(0,0) sits on the left end.
        model.Wires.Add(Wire((0, 0), (0, 400)));
        model.Components.Add(Resistor("R1", 0, 200));
        // Label mid-segment — not on a vertex, so FindLabelNetKey uses segment scan.
        model.NetLabels.Add(new EditableNetLabel { X = 0, Y = 200, Name = "rf_in" });

        var result = NetExtractor.Extract(model);

        Assert.Equal("rf_in", NetOf(result, "R1", 0));
    }

    // ── Test 3: auto-names are deterministically stable ──────────────────────

    [Fact]
    public void AutoNames_StableAcrossReExtraction()
    {
        var model = new SchematicEditModel();
        // Three isolated resistors; no wires, no labels → all 6 pins get auto-names.
        model.Components.Add(Resistor("R1", 0,   200));
        model.Components.Add(Resistor("R2", 400, 200));
        model.Components.Add(Resistor("R3", 800, 200));

        var r1 = NetExtractor.Extract(model);
        var r2 = NetExtractor.Extract(model);

        // Same names on first extraction match second extraction.
        foreach (var inst in r1.TestBench.Instances)
        {
            var inst2 = r2.TestBench.Instances.First(i => i.InstanceName == inst.InstanceName);
            Assert.Equal(inst.NetBindings, inst2.NetBindings);
        }
    }

    // ── Test 4: auto-names follow component-list order ───────────────────────

    [Fact]
    public void AutoNames_OrderedByComponentListOrder()
    {
        var model = new SchematicEditModel();
        // R1 is first; its port0 should get n1, port1 gets n2.
        // R2 is second; its port0 (distinct position) gets n3, port1 gets n4.
        model.Components.Add(Resistor("R1", 0,   200));
        model.Components.Add(Resistor("R2", 400, 200));

        var result = NetExtractor.Extract(model);

        var r1p0 = NetOf(result, "R1", 0);
        var r1p1 = NetOf(result, "R1", 1);
        var r2p0 = NetOf(result, "R2", 0);
        var r2p1 = NetOf(result, "R2", 1);

        // All four nets are distinct (no wires connecting anything).
        var nets = new[] { r1p0, r1p1, r2p0, r2p1 };
        Assert.Equal(nets.Length, nets.Distinct().Count());

        // R1's nets come before R2's in the stable ordering.
        int r1p0Num = int.Parse(r1p0[1..]);  // strip leading 'n'
        int r2p0Num = int.Parse(r2p0[1..]);
        Assert.True(r1p0Num < r2p0Num, "R1 pins should get lower auto-names than R2 pins");
    }

    // ── Test 5: two different labels on one net → conflict recorded ──────────

    [Fact]
    public void TwoDifferentLabelsOnSameNet_ConflictRecorded()
    {
        var model = new SchematicEditModel();
        // One wire spanning two label positions.
        model.Wires.Add(Wire((0, 0), (0, 400)));
        // Two different labels placed mid-segment on the same wire.
        model.NetLabels.Add(new EditableNetLabel { X = 0, Y = 100, Name = "vdd" });
        model.NetLabels.Add(new EditableNetLabel { X = 0, Y = 300, Name = "gnd_bad" });

        var result = NetExtractor.Extract(model);

        // At least one conflict was recorded.
        Assert.NotEmpty(result.Conflicts);
        // The conflict message names both labels.
        var conflict = result.Conflicts[0];
        Assert.Contains("vdd",     conflict, StringComparison.Ordinal);
        Assert.Contains("gnd_bad", conflict, StringComparison.Ordinal);
        // Extraction still succeeds — TestBench was produced.
        Assert.NotNull(result.TestBench);
    }

    // ── Test 6: ground wins over a label on the same net ─────────────────────

    [Fact]
    public void GroundWinsOverLabel()
    {
        var model = new SchematicEditModel();
        // Ground at (0,0) and R1.port0=(0,0) both at same P-cell.
        model.Components.Add(new EditableComponent
            { InstanceName = "GND1", Symbol = SymbolKind.Ground, X = 0, Y = 0 });
        model.Components.Add(Resistor("R1", 0, 200));
        // Label placed on the same net — ground must win, no conflict for "0" vs label.
        model.NetLabels.Add(new EditableNetLabel { X = 0, Y = 0, Name = "gnd_label" });

        var result = NetExtractor.Extract(model);

        // The net is "0", not the label text.
        Assert.Equal("0", NetOf(result, "R1", 0));
        // No conflict — ground overriding a label is not a conflict.
        Assert.Empty(result.Conflicts);
    }
}
