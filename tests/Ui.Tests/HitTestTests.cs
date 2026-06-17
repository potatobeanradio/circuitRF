using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

public class HitTestTests
{
    private static (SchematicEditModel Edit, SchematicModel Render, SchematicSpatialIndex Index) BuildSmall()
    {
        var edit = new SchematicEditModel();
        edit.Components.Add(new EditableComponent
        {
            InstanceName = "R1",
            Symbol       = SymbolKind.Resistor,
            X            = 0,
            Y            = 0,
        });
        edit.Components.Add(new EditableComponent
        {
            InstanceName = "C1",
            Symbol       = SymbolKind.Capacitor,
            X            = 600,
            Y            = 0,
        });
        var (render, index) = edit.BuildRenderModel();
        return (edit, render, index);
    }

    [Fact]
    public void HitComponent_Center_ReturnsComponent()
    {
        var (edit, render, index) = BuildSmall();
        var hit = SchematicHitTest.Test(edit, render, index, 0, 0);
        Assert.Equal(SchematicHitTest.HitKind.Component, hit.Kind);
        Assert.Equal(edit.Components[0].Id, hit.Id);
    }

    [Fact]
    public void HitComponent_FarAway_ReturnsNone()
    {
        var (edit, render, index) = BuildSmall();
        var hit = SchematicHitTest.Test(edit, render, index, 99_999, 99_999);
        Assert.Equal(SchematicHitTest.HitKind.None, hit.Kind);
    }

    [Fact]
    public void HitComponent_SecondComponent_ReturnsCorrectId()
    {
        var (edit, render, index) = BuildSmall();
        var hit = SchematicHitTest.Test(edit, render, index, 600, 0);
        Assert.Equal(SchematicHitTest.HitKind.Component, hit.Kind);
        Assert.Equal(edit.Components[1].Id, hit.Id);
    }

    [Fact]
    public void RectHit_BothComponents_ReturnsBoth()
    {
        var (edit, render, index) = BuildSmall();
        var hits = SchematicHitTest.TestRect(edit, render, index, -500, -500, 1200, 500);
        var ids   = hits.Select(h => h.Id).ToHashSet();
        Assert.Contains(edit.Components[0].Id, ids);
        Assert.Contains(edit.Components[1].Id, ids);
    }

    [Fact]
    public void RectHit_NarrowRect_ReturnsOnlyFirst()
    {
        var (edit, render, index) = BuildSmall();
        var hits = SchematicHitTest.TestRect(edit, render, index, -300, -300, 200, 300);
        Assert.Single(hits);
        Assert.Equal(edit.Components[0].Id, hits[0].Id);
    }

    [Fact]
    public void NearestPort_FindsPort_WithinTolerance()
    {
        var (edit, render, index) = BuildSmall();
        // Resistor is vertical: port 0 is at local (0,-200) → world (0,-200) at R0
        var (found, _, _, px, py) = SchematicHitTest.NearestPort(edit, 0, -200, 20);
        Assert.True(found);
        Assert.InRange(py, -210.0, -190.0);
    }

    // ── includeLabels tests ───────────────────────────────────────────────────

    // Resistor at (0,0): type label "R" (1 char).
    // Canonical geometry: baselineX=-155, baselineY=280, band=[204,305.6], textRight≈-106.5.
    // Click (-130, 255) is inside that zone.

    [Fact]
    public void LabelClick_IncludeLabelsTrue_ReturnsLabelHit()
    {
        var (edit, render, index) = BuildSmall();
        var hit = SchematicHitTest.Test(edit, render, index, -130, 255, includeLabels: true);
        Assert.Equal(SchematicHitTest.HitKind.ComponentType, hit.Kind);
        Assert.Equal(edit.Components[0].Id, hit.Id);
    }

    [Fact]
    public void LabelClick_IncludeLabelsFalse_ReturnsNone()
    {
        var (edit, render, index) = BuildSmall();
        // Point below the glyph but NOT in the label band (which starts at y≈204) — uses old
        // approximate position to confirm label exclusion is reliable regardless of click point.
        var hit = SchematicHitTest.Test(edit, render, index, -130, 170, includeLabels: false);
        Assert.NotEqual(SchematicHitTest.HitKind.ComponentType, hit.Kind);
        Assert.NotEqual(SchematicHitTest.HitKind.ComponentName, hit.Kind);
        Assert.NotEqual(SchematicHitTest.HitKind.ComponentParam, hit.Kind);
    }

    [Fact]
    public void GlyphClick_IncludeLabelsFalse_ReturnsComponent()
    {
        var (edit, render, index) = BuildSmall();
        var hit = SchematicHitTest.Test(edit, render, index, 0, 0, includeLabels: false);
        Assert.Equal(SchematicHitTest.HitKind.Component, hit.Kind);
        Assert.Equal(edit.Components[0].Id, hit.Id);
    }

    // ── TestStack tests ───────────────────────────────────────────────────────

    [Fact]
    public void TestStack_TwoOverlappingGlyphs_ReturnsTopFirst()
    {
        // Two components at the same location; higher index = topmost.
        var edit = new SchematicEditModel();
        edit.Components.Add(new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 0, Y = 0 });
        edit.Components.Add(new EditableComponent { InstanceName = "R2", Symbol = SymbolKind.Resistor, X = 0, Y = 0 });
        var (render, idx) = edit.BuildRenderModel();

        var stack = SchematicHitTest.TestStack(edit, render, idx, 0, 0);

        Assert.Equal(2, stack.Count);
        Assert.Equal(SchematicHitTest.HitKind.Component, stack[0].Kind);
        Assert.Equal(edit.Components[1].Id, stack[0].Id);  // highest index first
        Assert.Equal(SchematicHitTest.HitKind.Component, stack[1].Kind);
        Assert.Equal(edit.Components[0].Id, stack[1].Id);
    }

    [Fact]
    public void TestStack_WireUnderComponent_BothInStack()
    {
        // Resistor at (0,0); horizontal wire passing through its glyph body.
        var edit = new SchematicEditModel();
        edit.Components.Add(new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 0, Y = 0 });
        var wire = new EditableWire();
        wire.Points.Add((-500, 0));
        wire.Points.Add((500, 0));
        edit.Wires.Add(wire);
        var (render, idx) = edit.BuildRenderModel();

        // Click dead centre of the resistor glyph (which the wire passes through).
        var stack = SchematicHitTest.TestStack(edit, render, idx, 0, 0);

        Assert.True(stack.Count >= 2);
        Assert.Equal(SchematicHitTest.HitKind.Component,   stack[0].Kind);
        Assert.Equal(edit.Components[0].Id,                 stack[0].Id);
        Assert.Equal(SchematicHitTest.HitKind.WireSegment, stack[1].Kind);
        Assert.Equal(wire.Id,                               stack[1].Id);
    }

    [Fact]
    public void TestStack_TwoOverlappingWires_TwoEntries()
    {
        // Two horizontal wires both covering x=0; click at (0,0).
        var edit = new SchematicEditModel();
        var w1 = new EditableWire(); w1.Points.Add((-500, 0)); w1.Points.Add((500, 0)); edit.Wires.Add(w1);
        var w2 = new EditableWire(); w2.Points.Add((-500, 0)); w2.Points.Add((500, 0)); edit.Wires.Add(w2);
        var (render, idx) = edit.BuildRenderModel();

        var stack = SchematicHitTest.TestStack(edit, render, idx, 0, 0);

        Assert.Equal(2, stack.Count);
        Assert.All(stack, h => Assert.Equal(SchematicHitTest.HitKind.WireSegment, h.Kind));
        var ids = stack.Select(h => h.Id).ToHashSet();
        Assert.Contains(w1.Id, ids);
        Assert.Contains(w2.Id, ids);
    }

    [Fact]
    public void TestStack_FirstEntryMatchesTest_ForComponent()
    {
        var (edit, render, index) = BuildSmall();
        var singleHit = SchematicHitTest.Test(edit, render, index, 0, 0, includeLabels: false);
        var stack     = SchematicHitTest.TestStack(edit, render, index, 0, 0);

        Assert.NotEmpty(stack);
        Assert.Equal(singleHit.Kind,     stack[0].Kind);
        Assert.Equal(singleHit.Id,       stack[0].Id);
        Assert.Equal(singleHit.SubIndex, stack[0].SubIndex);
    }

    [Fact]
    public void TestStack_FirstEntryMatchesTest_ForWireSegment()
    {
        // A lone horizontal wire, click mid-segment.
        var edit = new SchematicEditModel();
        var wire = new EditableWire();
        wire.Points.Add((-500, 0));
        wire.Points.Add((500, 0));
        edit.Wires.Add(wire);
        var (render, idx) = edit.BuildRenderModel();

        var singleHit = SchematicHitTest.Test(edit, render, idx, 0, 0, includeLabels: false);
        var stack     = SchematicHitTest.TestStack(edit, render, idx, 0, 0);

        Assert.NotEmpty(stack);
        Assert.Equal(singleHit.Kind,     stack[0].Kind);
        Assert.Equal(singleHit.Id,       stack[0].Id);
        Assert.Equal(singleHit.SubIndex, stack[0].SubIndex);
    }

    // ── Cycle logic tests (PickClickThrough / CurrentSelectionIndexInStack) ───

    private static SchematicViewModel BuildVm(SchematicEditModel edit)
        => new SchematicViewModel(edit);

    [Fact]
    public void Cycle_FirstClick_SelectsTopmost()
    {
        var edit = new SchematicEditModel();
        edit.Components.Add(new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 0, Y = 0 });
        edit.Components.Add(new EditableComponent { InstanceName = "R2", Symbol = SymbolKind.Resistor, X = 0, Y = 0 });
        var (render, idx) = edit.BuildRenderModel();
        var vm = BuildVm(edit);

        var stack = SchematicHitTest.TestStack(edit, render, idx, 0, 0);
        var hit   = vm.PickClickThrough(stack, shift: false);

        Assert.Equal(stack[0].Id, hit.Id);
    }

    [Fact]
    public void Cycle_SecondClick_AdvancesToNext()
    {
        var edit = new SchematicEditModel();
        edit.Components.Add(new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 0, Y = 0 });
        edit.Components.Add(new EditableComponent { InstanceName = "R2", Symbol = SymbolKind.Resistor, X = 0, Y = 0 });
        var (render, idx) = edit.BuildRenderModel();
        var vm = BuildVm(edit);

        var stack = SchematicHitTest.TestStack(edit, render, idx, 0, 0);

        // Select topmost first.
        vm.Selection.SelectOne(stack[0].Id);
        var hit = vm.PickClickThrough(stack, shift: false);

        Assert.Equal(stack[1].Id, hit.Id);
    }

    [Fact]
    public void Cycle_LastEntryWrapsToTop()
    {
        var edit = new SchematicEditModel();
        edit.Components.Add(new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 0, Y = 0 });
        edit.Components.Add(new EditableComponent { InstanceName = "R2", Symbol = SymbolKind.Resistor, X = 0, Y = 0 });
        var (render, idx) = edit.BuildRenderModel();
        var vm = BuildVm(edit);

        var stack = SchematicHitTest.TestStack(edit, render, idx, 0, 0);

        // Select the bottommost entry (last in stack).
        vm.Selection.SelectOne(stack[^1].Id);
        var hit = vm.PickClickThrough(stack, shift: false);

        Assert.Equal(stack[0].Id, hit.Id);
    }

    [Fact]
    public void Cycle_MultiSelection_ResetsToTopmost()
    {
        var edit = new SchematicEditModel();
        edit.Components.Add(new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 0, Y = 0 });
        edit.Components.Add(new EditableComponent { InstanceName = "R2", Symbol = SymbolKind.Resistor, X = 0, Y = 0 });
        var (render, idx) = edit.BuildRenderModel();
        var vm = BuildVm(edit);

        var stack = SchematicHitTest.TestStack(edit, render, idx, 0, 0);

        // Select both — multi-selection → cur = -1 → topmost.
        vm.Selection.SelectOne(stack[0].Id);
        vm.Selection.Add(stack[1].Id);
        var hit = vm.PickClickThrough(stack, shift: false);

        Assert.Equal(stack[0].Id, hit.Id);
    }

    [Fact]
    public void Cycle_ShiftClick_AlwaysReturnsTopmost()
    {
        var edit = new SchematicEditModel();
        edit.Components.Add(new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 0, Y = 0 });
        edit.Components.Add(new EditableComponent { InstanceName = "R2", Symbol = SymbolKind.Resistor, X = 0, Y = 0 });
        var (render, idx) = edit.BuildRenderModel();
        var vm = BuildVm(edit);

        var stack = SchematicHitTest.TestStack(edit, render, idx, 0, 0);

        // Even with a non-topmost selection, shift returns topmost.
        vm.Selection.SelectOne(stack[1].Id);
        var hit = vm.PickClickThrough(stack, shift: true);

        Assert.Equal(stack[0].Id, hit.Id);
    }

    [Fact]
    public void Cycle_EmptyStack_ReturnsNoneHit()
    {
        var edit = new SchematicEditModel();
        var (render, idx) = edit.BuildRenderModel();
        var vm = BuildVm(edit);

        var stack = SchematicHitTest.TestStack(edit, render, idx, 99999, 99999);
        var hit   = vm.PickClickThrough(stack, shift: false);

        Assert.Equal(SchematicHitTest.HitKind.None, hit.Kind);
    }

    // ── Suppressed-label hit-test tests ──────────────────────────────────────
    // LabelStartOffY=134, LabelRowHeight=72 → row0 centerY=170, row1 centerY=242.
    // textLeft = comp.X - 165 → click at x ≈ comp.X - 130 is inside the zone.

    [Fact]
    public void Ground_NoTypeHit()
    {
        var edit = new SchematicEditModel();
        edit.Components.Add(new EditableComponent
        {
            InstanceName = "GND1",
            Symbol       = SymbolKind.Ground,
            X            = 0,
            Y            = 0,
        });
        var (render, idx) = edit.BuildRenderModel();

        // Click at the type-label row position for a Ground component.
        var hit = SchematicHitTest.Test(edit, render, idx, -130, 170, includeLabels: true);
        Assert.NotEqual(SchematicHitTest.HitKind.ComponentType, hit.Kind);
    }

    [Fact]
    public void SuppressedTypeLabel_NoTypeHit()
    {
        var edit = new SchematicEditModel();
        edit.Components.Add(new EditableComponent
        {
            InstanceName   = "R1",
            Symbol         = SymbolKind.Resistor,
            X              = 0,
            Y              = 0,
            ShowTypeLabel  = false,
            ShowInstanceName = true,
        });
        var (render, idx) = edit.BuildRenderModel();

        // With ShowTypeLabel=false → no ComponentType hit at the canonical row-0 position.
        // Row 0 canonical band: baseline y=280, band [204,305.6], baseX=-155.
        var suppressed = SchematicHitTest.Test(edit, render, idx, -130, 255, includeLabels: true);
        Assert.NotEqual(SchematicHitTest.HitKind.ComponentType, suppressed.Kind);

        // Regression guard: ShowTypeLabel=true → ComponentType IS returned at same position.
        edit.Components[0].ShowTypeLabel = true;
        var (render2, idx2) = edit.BuildRenderModel();
        var visible = SchematicHitTest.Test(edit, render2, idx2, -130, 255, includeLabels: true);
        Assert.Equal(SchematicHitTest.HitKind.ComponentType, visible.Kind);
    }

    [Fact]
    public void SuppressedInstanceName_NoNameHit()
    {
        var edit = new SchematicEditModel();
        var comp = new EditableComponent
        {
            InstanceName     = "R1",
            Symbol           = SymbolKind.Resistor,
            X                = 0,
            Y                = 0,
            ShowTypeLabel    = true,
            ShowInstanceName = false,
        };
        // Add a shown param so we can verify param-row slot is unaffected.
        comp.Parameters.Add(new EditableParameter
        {
            Name            = "R",
            Expression      = "50",
            Unit            = "Ohm",
            ShowOnSchematic = true,
        });
        edit.Components.Add(comp);
        var (render, idx) = edit.BuildRenderModel();

        // Row 1 (instance name) suppressed → no ComponentName hit at the row-1 canonical position.
        // Row 1 canonical band: baseline y=352, band [276,377.6].
        var nameHit = SchematicHitTest.Test(edit, render, idx, -100, 327, includeLabels: true);
        Assert.NotEqual(SchematicHitTest.HitKind.ComponentName, nameHit.Kind);

        // Param row is row 2 (after suppressed row 1).
        // Row 2 baseline y = 280 + 2*72 = 424, band [348,449.6], center ≈ 399.
        // textRight = -155 + 10*38.5 + 10 = 240 → x=-100 is inside.
        var paramHit = SchematicHitTest.Test(edit, render, idx, -100, 399, includeLabels: true);
        Assert.Equal(SchematicHitTest.HitKind.ComponentParam, paramHit.Kind);
        Assert.Equal(comp.Id, paramHit.Id);
    }
}
