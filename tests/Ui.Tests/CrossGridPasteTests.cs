using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Layer 6 — cross-grid paste (§5): when P_src != P_dst, connection points are snapped to P_dst,
/// a warning is posted, intra-group coincidences are preserved, and the snap is one undoable action.
/// Same-grid paste is unchanged.
/// </summary>
public class CrossGridPasteTests
{
    private const double Eps = 1e-9;

    // Simple IMessageSink that records warnings for assertions.
    private sealed class CaptureSink : IMessageSink
    {
        public readonly List<string> Warnings = new();
        public void Post(MessageLevel level, string text, string? filePath = null)
        {
            if (level == MessageLevel.Warning) Warnings.Add(text);
        }
        public void Clear() => Warnings.Clear();
    }

    private static (SchematicEditModel Model, UndoRedoStack Undo, SchematicViewModel Vm) MakeDst(
        double dstGrid = 100.0)
    {
        var m  = new SchematicEditModel { GridSize = dstGrid, GridSnap = true };
        var vm = new SchematicViewModel(m);
        return (m, vm.UndoRedo, vm);
    }

    // ── Same-grid paste (unchanged behaviour) ────────────────────────────────

    [Fact]
    public void SameGrid_Paste_NoSnapNoWarning()
    {
        var (model, undo, vm) = MakeDst(100.0);
        var sink = new CaptureSink();

        var comp = new EditableComponent
            { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 300, Y = 0 };
        var wire = new EditableWire();
        wire.Points.AddRange([(300.0, 0.0), (500.0, 0.0)]);

        vm.Execute(new SchematicPasteCommand(
            model, [comp], [wire], [],
            sourceGridSize: 100.0, messageSink: sink));

        // No warning — same grid.
        Assert.Empty(sink.Warnings);
        // Positions unchanged.
        Assert.Equal(300.0, model.Components[0].X, Eps);
        Assert.Equal(500.0, model.Wires[0].Points[1].X, Eps);
    }

    // ── Cross-grid paste: component origins snapped to P_dst ─────────────────

    [Fact]
    public void CrossGrid_ComponentOrigins_SnappedToPDst()
    {
        // P_src = 50: component at (150, 100). P_dst = 100: should snap to (200, 100).
        var (model, _, vm) = MakeDst(100.0);
        var comp = new EditableComponent
            { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 150, Y = 100 };

        vm.Execute(new SchematicPasteCommand(
            model, [comp], [], [],
            sourceGridSize: 50.0));

        Assert.Equal(200.0, model.Components[0].X, Eps);
        Assert.Equal(100.0, model.Components[0].Y, Eps);
    }

    [Fact]
    public void CrossGrid_WireVertices_SnappedToPDst()
    {
        // Wire from (50,0) to (150,0) on P_src=50. On P_dst=100: (0,0)→(200,0) or (100,0).
        // Math.Round(50/100)=0 → x=0; Math.Round(150/100)=2 → x=200.
        var (model, _, vm) = MakeDst(100.0);
        var wire = new EditableWire();
        wire.Points.AddRange([(50.0, 0.0), (150.0, 0.0)]);

        vm.Execute(new SchematicPasteCommand(
            model, [], [wire], [],
            sourceGridSize: 50.0));

        var pts = model.Wires[0].Points;
        Assert.Equal(0.0,   pts[0].X, Eps);
        Assert.Equal(200.0, pts[1].X, Eps);
        Assert.Equal(0.0,   pts[0].Y, Eps);
        Assert.Equal(0.0,   pts[1].Y, Eps);
    }

    // ── Cross-grid paste: warning posted ─────────────────────────────────────

    [Fact]
    public void CrossGrid_Warning_Posted()
    {
        var (model, _, vm) = MakeDst(100.0);
        var sink = new CaptureSink();
        var comp = new EditableComponent
            { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 150, Y = 0 };

        vm.Execute(new SchematicPasteCommand(
            model, [comp], [], [],
            sourceGridSize: 50.0, messageSink: sink));

        Assert.Single(sink.Warnings);
        Assert.Contains("50", sink.Warnings[0]);   // mentions source grid
        Assert.Contains("100", sink.Warnings[0]);  // mentions destination grid
    }

    // ── Cross-grid paste: intra-group coincidence preserved ──────────────────

    [Fact]
    public void CrossGrid_IntraGroupCoincidence_Preserved()
    {
        // Two components at (150, 0) on P_src=50 — they are coincident.
        // After snap to P_dst=100: both map to (200, 0) — still coincident.
        var (model, _, vm) = MakeDst(100.0);
        var c1 = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 150, Y = 0 };
        var c2 = new EditableComponent { InstanceName = "R2", Symbol = SymbolKind.Resistor, X = 150, Y = 0 };

        vm.Execute(new SchematicPasteCommand(
            model, [c1, c2], [], [],
            sourceGridSize: 50.0));

        // Both land at the same snapped coordinate.
        Assert.Equal(model.Components[0].X, model.Components[1].X, Eps);
        Assert.Equal(model.Components[0].Y, model.Components[1].Y, Eps);
    }

    // ── Cross-grid paste: one undoable action ────────────────────────────────

    [Fact]
    public void CrossGrid_Paste_IsOneUndo()
    {
        var (model, undo, vm) = MakeDst(100.0);
        var comp = new EditableComponent
            { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 150, Y = 0 };
        // Use exact P_src=50 multiples that map to DISTINCT P_dst=100 cells:
        // (100,0) → round(1.0)*100 = 100; (300,0) → round(3.0)*100 = 300 — not degenerate after snap.
        var wire = new EditableWire();
        wire.Points.AddRange([(100.0, 0.0), (300.0, 0.0)]);

        vm.Execute(new SchematicPasteCommand(
            model, [comp], [wire], [],
            sourceGridSize: 50.0));

        Assert.Single(model.Components);
        Assert.Single(model.Wires);
        Assert.Equal(100.0, model.Wires[0].Points[0].X, Eps);
        Assert.Equal(300.0, model.Wires[0].Points[1].X, Eps);

        undo.Undo();   // one undo removes both component and wire

        Assert.Empty(model.Components);
        Assert.Empty(model.Wires);

        undo.Redo();   // redo restores them (snapped positions)
        Assert.Single(model.Components);
        Assert.Equal(200.0, model.Components[0].X, Eps);  // 150 → round(1.5)*100 = 200
        Assert.Single(model.Wires);
        Assert.Equal(100.0, model.Wires[0].Points[0].X, Eps);
    }

    // ── SourceGridSize round-trips through SerializeSelection ────────────────

    [Fact]
    public void SerializeSelection_EmbedsSrcGrid_DeserializeReturnsIt()
    {
        var comp = new EditableComponent
            { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 200, Y = 0 };
        string json = SchematicPersistence.SerializeSelection([comp], [], [], sourceGridSize: 50.0);

        var (_, _, _, srcGrid) = SchematicPersistence.DeserializeSelection(json);
        Assert.Equal(50.0, srcGrid, Eps);
    }
}
