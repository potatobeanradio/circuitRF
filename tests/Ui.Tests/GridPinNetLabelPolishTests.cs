using Avalonia.Input;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// B11 — F5 label-move SnapMode tri-state
/// B12 — Pin/Term Num auto-assign on inline type-change
/// B13 — Empty net-label text removes the label
/// </summary>
public class GridPinNetLabelPolishTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static SchematicViewModel MakeVm()
    {
        var em = new SchematicEditModel();
        return new SchematicViewModel(em);
    }

    // ── B11: SnapMode tri-state ───────────────────────────────────────────────

    [Fact]
    public void CycleSnapMode_OrderIsP_p_None_P()
    {
        var vm = MakeVm();
        // Default is FineGrid (p)
        Assert.Equal(SnapMode.FineGrid, vm.SnapMode);
        vm.CycleSnapMode(); Assert.Equal(SnapMode.None,          vm.SnapMode);
        vm.CycleSnapMode(); Assert.Equal(SnapMode.ConnectionGrid, vm.SnapMode);
        vm.CycleSnapMode(); Assert.Equal(SnapMode.FineGrid,       vm.SnapMode);
    }

    [Fact]
    public void LabelDelta_ConnectionGrid_SnapsToGridSize()
    {
        var vm = MakeVm();
        vm.SnapMode = SnapMode.ConnectionGrid;

        // Enter MoveLabels so _moveLabelRefX/Y are set — drive directly via BeginMoveLabels
        // then simulate the move via reflected field. Instead, we test the effect through
        // CommitMoveLabels by placing a component and invoking the full F5 flow.
        var em = vm.EditModel;
        var comp = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 0, Y = 0 };
        em.Components.Add(comp);
        em.NotifyChanged();

        // Start a MoveLabels op; click sets ref point at (0,0), move to (150,0) which is
        // 1.5 × GridSize (100) → should round to nearest multiple = 100.
        vm.BeginMoveLabels();
        // Inject the selection so WaitFirstClick fires; we call press directly.
        vm.Selection.SelectOne(comp.Id);
        vm.BeginMoveLabels();                              // re-enter with selection → WaitFirstClick
        vm.OnPointerPressed(0, 0, default);               // sets ref point

        // Move to (155, 73) — ConnectionGrid: round to nearest 100 → (200, 100)
        vm.OnPointerMoved(155, 73, leftDown: false, modifiers: default);

        double gridP = em.GridSize; // 100
        var overlay = vm.Overlay;
        if (overlay.LabelDragOffsets is { } offsets && offsets.TryGetValue(comp.Id, out var off))
        {
            Assert.Equal(Math.Round(155.0 / gridP) * gridP, off.DX, 1);
            Assert.Equal(Math.Round(73.0  / gridP) * gridP, off.DY, 1);
        }
        else
        {
            Assert.Fail("LabelDragOffsets not set during MoveLabels move phase");
        }
    }

    [Fact]
    public void LabelDelta_FineGrid_SnapsToAuthorGridSize()
    {
        var vm = MakeVm();
        vm.SnapMode = SnapMode.FineGrid;

        var em = vm.EditModel;
        var comp = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 0, Y = 0 };
        em.Components.Add(comp);
        em.NotifyChanged();

        vm.Selection.SelectOne(comp.Id);
        vm.BeginMoveLabels();
        vm.OnPointerPressed(0, 0, default);

        // Move to (17, 9) — FineGrid (p=5): round to nearest 5 → (15, 10)
        vm.OnPointerMoved(17, 9, leftDown: false, modifiers: default);

        double gridP = em.AuthorGridSize; // 5
        var overlay = vm.Overlay;
        if (overlay.LabelDragOffsets is { } offsets && offsets.TryGetValue(comp.Id, out var off))
        {
            Assert.Equal(Math.Round(17.0 / gridP) * gridP, off.DX, 1);
            Assert.Equal(Math.Round(9.0  / gridP) * gridP, off.DY, 1);
        }
        else
        {
            Assert.Fail("LabelDragOffsets not set during MoveLabels move phase");
        }
    }

    [Fact]
    public void LabelDelta_None_IsUnsnapped()
    {
        var vm = MakeVm();
        vm.SnapMode = SnapMode.None;

        var em = vm.EditModel;
        var comp = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 0, Y = 0 };
        em.Components.Add(comp);
        em.NotifyChanged();

        vm.Selection.SelectOne(comp.Id);
        vm.BeginMoveLabels();
        vm.OnPointerPressed(0, 0, default);

        // Move to an odd value that only aligns with exact position
        vm.OnPointerMoved(13.7, 6.3, leftDown: false, modifiers: default);

        var overlay = vm.Overlay;
        if (overlay.LabelDragOffsets is { } offsets && offsets.TryGetValue(comp.Id, out var off))
        {
            Assert.Equal(13.7, off.DX, 3);
            Assert.Equal(6.3,  off.DY, 3);
        }
        else
        {
            Assert.Fail("LabelDragOffsets not set during MoveLabels move phase");
        }
    }

    // ── B12: Pin/Term Num auto-assign on inline type-change ───────────────────

    [Fact]
    public void CommitInlineEdit_TypeChange_TwoExistingPins_NewPinGetsNum3()
    {
        var vm = MakeVm();
        var em = vm.EditModel;

        // Place two pins (Num 1 and 2) via CommitPlacement
        vm.CommitPlacement(SymbolKind.Pin, 0, SymbolRotation.R0, 0,   0);
        vm.CommitPlacement(SymbolKind.Pin, 0, SymbolRotation.R0, 100, 0);

        // Place a generic component to be type-changed to Pin
        var src = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 200, Y = 0 };
        em.Components.Add(src);
        em.NotifyChanged();

        // Trigger inline type edit → "PIN"
        var hit = new SchematicHitTest.HitResult(SchematicHitTest.HitKind.ComponentType, src.Id);
        vm.BeginInlineEditForHit(hit, 0, 0);
        vm.InlineEditValue = "PIN";
        vm.CommitInlineEdit();

        // The new Pin should have Num = 3 (existing pins are named "Pin1" and "Pin2")
        var newPin = em.Components.First(c => c.Symbol == SymbolKind.Pin
                                          && c.InstanceName != "Pin1"
                                          && c.InstanceName != "Pin2");
        var numParam = newPin.Parameters.FirstOrDefault(p => p.Name == "Num");
        Assert.NotNull(numParam);
        Assert.Equal("3", numParam.Expression);
    }

    [Fact]
    public void CommitInlineEdit_TypeChange_TwoExistingTerms_NewTermGetsNextNum()
    {
        var vm = MakeVm();
        var em = vm.EditModel;

        vm.CommitPlacement(SymbolKind.Term, 0, SymbolRotation.R0, 0,   0);
        vm.CommitPlacement(SymbolKind.Term, 0, SymbolRotation.R0, 100, 0);

        var src = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 200, Y = 0 };
        em.Components.Add(src);
        em.NotifyChanged();

        var hit = new SchematicHitTest.HitResult(SchematicHitTest.HitKind.ComponentType, src.Id);
        vm.BeginInlineEditForHit(hit, 0, 0);
        vm.InlineEditValue = "TERM";
        vm.CommitInlineEdit();

        // Existing terms are named "Term1" and "Term2" (prefix = "Term")
        var newTerm = em.Components.First(c => c.Symbol == SymbolKind.Term
                                           && c.InstanceName != "Term1"
                                           && c.InstanceName != "Term2");
        var numParam = newTerm.Parameters.FirstOrDefault(p => p.Name == "Num");
        Assert.NotNull(numParam);
        Assert.Equal("3", numParam.Expression);
    }

    // ── B13: Empty net-label text removes the label ───────────────────────────

    [Fact]
    public void CommitInlineEdit_EmptyNetLabel_RemovesExistingLabel()
    {
        var vm = MakeVm();
        var em = vm.EditModel;

        // Place a wire and a net label
        var wire = new EditableWire();
        wire.Points.Add((0,   0));
        wire.Points.Add((200, 0));
        em.Wires.Add(wire);

        var lbl = new EditableNetLabel { Name = "VDD", X = 100, Y = -20 };
        em.NetLabels.Add(lbl);
        em.NotifyChanged();

        Assert.Single(em.NetLabels);

        // Open inline edit for the existing label
        vm.BeginWireNodeLabelEdit(wire.Id, 100, 0, 0, 0);
        Assert.True(vm.IsInlineEditing);

        // Clear the text and commit
        vm.InlineEditValue = "";
        vm.CommitInlineEdit();

        Assert.Empty(em.NetLabels);
    }

    [Fact]
    public void CommitInlineEdit_EmptyNetLabel_IsUndoable()
    {
        var vm = MakeVm();
        var em = vm.EditModel;

        var wire = new EditableWire();
        wire.Points.Add((0,   0));
        wire.Points.Add((200, 0));
        em.Wires.Add(wire);

        var lbl = new EditableNetLabel { Name = "VDD", X = 100, Y = -20 };
        em.NetLabels.Add(lbl);
        em.NotifyChanged();

        vm.BeginWireNodeLabelEdit(wire.Id, 100, 0, 0, 0);
        vm.InlineEditValue = "";
        vm.CommitInlineEdit();

        Assert.Empty(em.NetLabels);

        vm.UndoRedo.Undo();

        Assert.Single(em.NetLabels);
        Assert.Equal("VDD", em.NetLabels[0].Name);
    }

    [Fact]
    public void CommitInlineEdit_EmptyNetLabel_WhenNoLabel_IsNoOp()
    {
        var vm = MakeVm();
        var em = vm.EditModel;

        var wire = new EditableWire();
        wire.Points.Add((0,   0));
        wire.Points.Add((200, 0));
        em.Wires.Add(wire);
        em.NotifyChanged();

        // Click on a wire node with no existing label
        vm.BeginWireNodeLabelEdit(wire.Id, 100, 0, 0, 0);
        vm.InlineEditValue = "";
        vm.CommitInlineEdit();

        Assert.Empty(em.NetLabels);
    }
}
