using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A selection holding BOTH bond wires and layout primitives drags as one thing (owner, 2026-08-27:
/// with both selected only one of them moved).
///
/// <para>wbond.md §6.3 makes holding both at once the point — "select the pads and the wires landing
/// on them" is one gesture — but only the half that owned the PRESS ever heard about the drag. The
/// canvas mediates, as it already does for the companion marquee, pushing whichever half's delta is
/// authoritative into the other; these tests drive the two view models directly, which is exactly
/// what the canvas does between them.</para>
///
/// <para><b>One delta, from one snap decision.</b> Re-deriving it on the far side is how the two
/// halves of a selection end up a step apart, so <c>CompanionMoveTo</c> takes an ABSOLUTE delta and
/// applies it verbatim.</para>
/// </summary>
public class WBondCompanionMoveTests
{
    private static long Mil(double v) => WBondUnits.ToNm(v, WBondUnit.Mil);

    private static WBondDesign WireDesign()
    {
        var design = new WBondDesign();
        var array = new WireArray { Name = "G1" };
        array.Wires.Add(LoopShape.CreateSeedWire(
            new Point3(0, 0, 0), new Point3(Mil(30), 0, 0),
            WBondDefaults.DiameterNm, WBondDefaults.Material, Mil(20), WBondDefaults.Points));
        design.Arrays.Add(array);
        return design;
    }

    /// <summary>A layout with one rectangle, the wire overlay over it, and both halves selected.</summary>
    private static (LayoutEditorViewModel Vm, WBondViewModel Wires, WBondLayoutOverlay Overlay, LayoutView Model)
        Fixture()
    {
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        model.Shapes.Add(new RectShape
        {
            Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000,
        });

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        var wires = new WBondViewModel(WireDesign());
        var overlay = new WBondLayoutOverlay(wires, frameBudgetMs: 1e9)
        {
            SnapEnabled = false,
            GridPitchNm = 0,
            ReferenceLayout = model,
        };
        wires.SelectAllWires();

        return (vm, wires, overlay, model);
    }

    private static long ShapeX(LayoutView model) => ((RectShape)model.Shapes[0]).X1;
    private static long ShapeY(LayoutView model) => ((RectShape)model.Shapes[0]).Y1;
    private static Point3 WireFoot(WBondViewModel wires) => wires.Design.AllWires().First().Points[0];

    // ── The LAYOUT drives; the wires follow ──────────────────────────────────────────────────────

    [Fact]
    public void WhenTheLayoutOwnsTheDrag_TheWiresComeAlong()
    {
        var (vm, wires, overlay, model) = Fixture();
        var footBefore = WireFoot(wires);

        // The canvas's own sequence: the overlay declines the press, so its half is armed to follow.
        overlay.BeginCompanionMove();
        overlay.CompanionMoveTo(3_000, -2_000);          // the layout's delta, in DBU
        overlay.CommitCompanionMove();

        Assert.Equal(footBefore.X + WBondSnap.ToNm(3_000, model.DbuPerMicron), WireFoot(wires).X);
        Assert.Equal(footBefore.Y + WBondSnap.ToNm(-2_000, model.DbuPerMicron), WireFoot(wires).Y);
        _ = vm;
    }

    [Fact]
    public void TheDeltaIsABSOLUTE_SoFramesDoNotAccumulate()
    {
        // The whole reason it is absolute: the driving half reports its total travel every frame, and
        // a receiver that added each report would move several times as far as the hand did.
        var (_, wires, overlay, model) = Fixture();
        var before = WireFoot(wires);

        overlay.BeginCompanionMove();
        overlay.CompanionMoveTo(1_000, 0);
        overlay.CompanionMoveTo(2_000, 0);
        overlay.CompanionMoveTo(3_000, 0);
        overlay.CommitCompanionMove();

        Assert.Equal(before.X + WBondSnap.ToNm(3_000, model.DbuPerMicron), WireFoot(wires).X);
    }

    [Fact]
    public void ACompanionMoveThatNeverMoved_LeavesNoUndoEntry()
    {
        // Armed on every press the overlay declines — including a plain click, which must leave
        // nothing behind. The gesture is opened lazily, on the first frame that actually moves.
        var (_, wires, overlay, _) = Fixture();

        overlay.BeginCompanionMove();
        overlay.CommitCompanionMove();

        Assert.False(wires.CanUndo);
    }

    [Fact]
    public void CancellingACompanionMove_PutsTheWiresBack()
    {
        var (_, wires, overlay, _) = Fixture();
        var before = WireFoot(wires);

        overlay.BeginCompanionMove();
        overlay.CompanionMoveTo(3_000, 4_000);
        overlay.CancelCompanionMove();

        Assert.Equal(before, WireFoot(wires));
    }

    [Fact]
    public void WithNoWiresSelected_ACompanionMoveDoesNothing()
    {
        var (_, wires, overlay, _) = Fixture();
        wires.Selection = new WireSelection();
        var before = WireFoot(wires);

        overlay.BeginCompanionMove();
        overlay.CompanionMoveTo(3_000, 0);
        overlay.CommitCompanionMove();

        Assert.Equal(before, WireFoot(wires));
    }

    // ── The OVERLAY drives; the primitives follow ────────────────────────────────────────────────

    [Fact]
    public void WhenTheOverlayOwnsTheDrag_ThePrimitivesComeAlong()
    {
        var (vm, _, _, model) = Fixture();
        long x = ShapeX(model), y = ShapeY(model);

        vm.BeginCompanionMove();
        vm.CompanionMoveTo(3_000, -2_000);
        vm.CommitCompanionMove();

        Assert.Equal(x + 3_000, ShapeX(model));
        Assert.Equal(y - 2_000, ShapeY(model));
    }

    [Fact]
    public void TheLayoutsCompanionMove_IsOneUndoEntryAcrossEveryChannel()
    {
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 });
        model.Rulers.Add(new RulerAnnotation
        {
            X1 = 0, Y1 = 5_000, X2 = 20_000, Y2 = 5_000,
            SizeMode = RulerSizeMode.Scaled, TextHeightDbu = 1_000,
        });

        // Select All takes every channel at once, which is the mixed selection this is about.
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);
        Assert.Single(vm.SelectedIndices);
        Assert.Single(vm.SelectedRulerIndices);

        vm.BeginCompanionMove();
        vm.CompanionMoveTo(2_000, 0);
        vm.CommitCompanionMove();

        Assert.Equal(2_000, ((RectShape)model.Shapes[0]).X1);
        Assert.Equal(2_000, model.Rulers[0].X1);

        vm.UndoCommand.Execute(null);
        Assert.Equal(0, ((RectShape)model.Shapes[0]).X1);
        Assert.Equal(0, model.Rulers[0].X1);
    }

    [Fact]
    public void ALayoutCompanionMoveThatNeverMoved_LeavesNoUndoEntry()
    {
        var (vm, _, _, _) = Fixture();

        vm.BeginCompanionMove();
        vm.CommitCompanionMove();

        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void CancellingTheLayoutsCompanionMove_LeavesThePrimitivesWhereTheyWere()
    {
        var (vm, _, _, model) = Fixture();
        long x = ShapeX(model);

        vm.BeginCompanionMove();
        vm.CompanionMoveTo(3_000, 0);
        vm.CancelCompanionMove();

        Assert.Equal(x, ShapeX(model));
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void WithNothingSelectedInTheLayout_ACompanionMoveDoesNothing()
    {
        var (vm, _, _, model) = Fixture();
        vm.DeselectAllCommand.Execute(null);
        long x = ShapeX(model);

        vm.BeginCompanionMove();
        vm.CompanionMoveTo(3_000, 0);
        vm.CommitCompanionMove();

        Assert.Equal(x, ShapeX(model));
    }

    // ── The delta the canvas actually forwards ───────────────────────────────────────────────────

    [Fact]
    public void AnOverlayDrag_ReportsItsDeltaInTheLayoutsOwnUnits()
    {
        var (_, wires, overlay, _) = Fixture();
        var foot = WireFoot(wires);

        Assert.Null(overlay.CompanionDragDelta);   // nothing in flight

        overlay.OnPointerPressed(foot.X, foot.Y, Mil(1), Avalonia.Input.KeyModifiers.None, 1);
        overlay.OnPointerMoved(foot.X + Mil(10), foot.Y, Mil(1),
                               leftButtonDown: true, Avalonia.Input.KeyModifiers.None);

        var delta = overlay.CompanionDragDelta;
        Assert.NotNull(delta);
        Assert.Equal(WBondSnap.ToDbu(Mil(10), 1000), delta!.Value.Dx);
        Assert.Equal(0, delta.Value.Dy);
    }

    [Fact]
    public void AnAltSTRETCH_ReportsNoDelta_BecauseItTranslatesNothing()
    {
        // It scales one wire's span about its far foot. A pad following a "delta" from that would be
        // moved by a gesture that never moved anything.
        var (_, wires, overlay, _) = Fixture();
        var outFoot = wires.Design.AllWires().First().Points[^1];

        overlay.OnPointerPressed(outFoot.X, outFoot.Y, Mil(1), Avalonia.Input.KeyModifiers.Alt, 1);
        overlay.OnPointerMoved(outFoot.X + Mil(7), outFoot.Y, Mil(1),
                               leftButtonDown: true, Avalonia.Input.KeyModifiers.Alt);

        Assert.Null(overlay.CompanionDragDelta);
    }

    // ── One gesture, ONE Ctrl+Z ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Owner, 2026-08-27: undoing a mixed drag took two undos. The two halves land on two different
    /// histories and there is no stack to merge them onto — what makes them one edit is a shared
    /// <c>EditSequence</c> stamp, which <c>UndoLast</c> drains.
    ///
    /// <para>The fixture attaches the wire design to the LAYOUT session, which is what puts both
    /// histories behind one <c>UndoLast</c>/<c>RedoLast</c> in the first place.</para>
    /// </summary>
    private static (LayoutEditorViewModel Vm, LayoutView Model) AttachedFixture()
    {
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.AttachWireDesign(WireDesign(), Path.Combine(Path.GetTempPath(), "companion.wBond"));
        vm.SelectAllCommand.Execute(null);
        vm.WireEditor!.SelectAllWires();
        return (vm, model);
    }

    [Fact]
    public void AMixedDrag_IsONEUndo()
    {
        var (vm, model) = AttachedFixture();
        var overlay = vm.WireOverlay!;
        var wires = vm.WireEditor!;

        long shapeX = ShapeX(model);
        var foot = WireFoot(wires);

        // Exactly what LayoutCanvas.OnPointerReleased does: both halves commit inside ONE group.
        vm.BeginCompanionMove();
        vm.CompanionMoveTo(3_000, 0);
        overlay.BeginCompanionMove();
        overlay.CompanionMoveTo(3_000, 0);
        using (CircuitRF.Ui.Commands.EditSequence.Group())
        {
            overlay.CommitCompanionMove();
            vm.CommitCompanionMove();
        }

        Assert.Equal(shapeX + 3_000, ShapeX(model));
        Assert.NotEqual(foot.X, WireFoot(wires).X);

        // ONE undo puts BOTH back.
        Assert.True(vm.CanUndoLast);
        vm.UndoLast();

        Assert.Equal(shapeX, ShapeX(model));
        Assert.Equal(foot.X, WireFoot(wires).X);
    }

    [Fact]
    public void AMixedDrag_IsONERedo()
    {
        var (vm, model) = AttachedFixture();
        var overlay = vm.WireOverlay!;
        var wires = vm.WireEditor!;

        long shapeX = ShapeX(model);
        var foot = WireFoot(wires);

        vm.BeginCompanionMove();
        vm.CompanionMoveTo(3_000, 0);
        overlay.BeginCompanionMove();
        overlay.CompanionMoveTo(3_000, 0);
        using (CircuitRF.Ui.Commands.EditSequence.Group())
        {
            overlay.CommitCompanionMove();
            vm.CommitCompanionMove();
        }

        vm.UndoLast();
        Assert.True(vm.CanRedoLast);
        vm.RedoLast();

        Assert.Equal(shapeX + 3_000, ShapeX(model));
        Assert.NotEqual(foot.X, WireFoot(wires).X);
    }

    [Fact]
    public void WithoutTheGroup_ItTakesTwo_WhichIsTheBug()
    {
        // The control, and the reason the group is load-bearing rather than decorative: the same two
        // commits recorded under two stamps still need two undos.
        var (vm, model) = AttachedFixture();
        var overlay = vm.WireOverlay!;

        long shapeX = ShapeX(model);
        var foot = WireFoot(vm.WireEditor!);

        vm.BeginCompanionMove();
        vm.CompanionMoveTo(3_000, 0);
        overlay.BeginCompanionMove();
        overlay.CompanionMoveTo(3_000, 0);
        overlay.CommitCompanionMove();      // no group…
        vm.CommitCompanionMove();           // …so two stamps

        vm.UndoLast();
        bool bothBack = ShapeX(model) == shapeX && WireFoot(vm.WireEditor!).X == foot.X;
        Assert.False(bothBack);

        vm.UndoLast();
        Assert.Equal(shapeX, ShapeX(model));
        Assert.Equal(foot.X, WireFoot(vm.WireEditor!).X);
    }

    [Fact]
    public void TwoUNRELATEDEdits_StillTakeTwoUndos()
    {
        // The stamp is unique per edit outside a group, so draining it can never swallow a
        // neighbouring edit — which is the risk this shape has to be checked against.
        var (vm, model) = AttachedFixture();
        var overlay = vm.WireOverlay!;

        long shapeX = ShapeX(model);
        var foot = WireFoot(vm.WireEditor!);

        using (CircuitRF.Ui.Commands.EditSequence.Group())
        {
            vm.BeginCompanionMove();
            vm.CompanionMoveTo(3_000, 0);
            vm.CommitCompanionMove();
        }
        using (CircuitRF.Ui.Commands.EditSequence.Group())
        {
            overlay.BeginCompanionMove();
            overlay.CompanionMoveTo(0, 4_000);
            overlay.CommitCompanionMove();
        }

        vm.UndoLast();                                   // the wire edit, on its own
        Assert.Equal(foot.Y, WireFoot(vm.WireEditor!).Y);
        Assert.Equal(shapeX + 3_000, ShapeX(model));     // the shape is still moved

        vm.UndoLast();
        Assert.Equal(shapeX, ShapeX(model));
    }

    [Fact]
    public void ANestedGroup_KeepsTheOuterStamp()
    {
        // So a caller never has to know whether it is already inside one.
        long outer, inner;
        using (CircuitRF.Ui.Commands.EditSequence.Group())
        {
            outer = CircuitRF.Ui.Commands.EditSequence.Next();
            using (CircuitRF.Ui.Commands.EditSequence.Group())
                inner = CircuitRF.Ui.Commands.EditSequence.Next();

            // …and the outer group is still open after the inner one closes.
            Assert.Equal(outer, CircuitRF.Ui.Commands.EditSequence.Next());
        }

        Assert.Equal(outer, inner);
        Assert.NotEqual(outer, CircuitRF.Ui.Commands.EditSequence.Next());
    }

    // ── A plain click means "just this", on BOTH sides of the seam ───────────────────────────────

    /// <summary>
    /// Owner, 2026-08-27: a ruler dragged over a wire point took the point with it. The two selections
    /// are deliberately independent (§6.3), so a wire selected earlier is still live when a plain
    /// click lands on a ruler — invisible before the companion move, and one unmodified click moving
    /// something the user never touched afterwards.
    ///
    /// <para>Nothing is cleared, so §6.3 is untouched: the companion simply refuses to follow a press
    /// that RESOLVED a new selection. Only a press that picks up an EXISTING one says "the whole
    /// selection travels".</para>
    /// </summary>
    [Fact]
    public void APressThatResolvedANewSelection_DoesNotDragTheOtherHalf()
    {
        var (_, wires, overlay, _) = Fixture();
        var before = WireFoot(wires);

        overlay.CompanionPressResolvedNewSelection = true;   // the layout just newly selected a ruler
        overlay.BeginCompanionMove();
        overlay.CompanionMoveTo(3_000, 0);
        overlay.CommitCompanionMove();

        Assert.Equal(before, WireFoot(wires));
    }

    [Fact]
    public void APressThatPickedUpAnEXISTINGSelection_StillDragsBothHalves()
    {
        // The control: the gate must not simply switch the companion move off.
        var (_, wires, overlay, model) = Fixture();
        var before = WireFoot(wires);

        overlay.CompanionPressResolvedNewSelection = false;
        overlay.BeginCompanionMove();
        overlay.CompanionMoveTo(3_000, 0);
        overlay.CommitCompanionMove();

        Assert.Equal(before.X + WBondSnap.ToNm(3_000, model.DbuPerMicron), WireFoot(wires).X);
    }

    [Fact]
    public void TheSameGateGuardsThePrimitives()
    {
        var (vm, _, _, model) = Fixture();
        long x = ShapeX(model);

        vm.CompanionPressResolvedNewSelection = true;    // the overlay just newly selected a wire
        vm.BeginCompanionMove();
        vm.CompanionMoveTo(3_000, 0);
        vm.CommitCompanionMove();

        Assert.Equal(x, ShapeX(model));
    }

    [Fact]
    public void TheLayoutReports_WhetherItsPressResolvedANewSelection()
    {
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        model.Rulers.Add(new RulerAnnotation
        {
            X1 = 0, Y1 = 0, X2 = 20_000, Y2 = 0,
            SizeMode = RulerSizeMode.Scaled, TextHeightDbu = 1_000,
        });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        // First press on the ruler: a NEW selection.
        vm.OnPointerPressed(10_000, 0, Avalonia.Input.KeyModifiers.None, 1, 500);
        Assert.True(vm.LastPressResolvedNewSelection);
        vm.OnPointerReleased(10_000, 0, Avalonia.Input.KeyModifiers.None);

        // Pressing it again picks the SAME selection up — which is the statement "drag all of it".
        vm.OnPointerPressed(10_000, 0, Avalonia.Input.KeyModifiers.None, 1, 500);
        Assert.False(vm.LastPressResolvedNewSelection);
    }

    [Fact]
    public void APressOnEmptySpace_CountsAsResolvingANewSelection()
    {
        var (vm, _, _, _) = Fixture();

        vm.OnPointerPressed(900_000, 900_000, Avalonia.Input.KeyModifiers.None, 1, 40);
        Assert.True(vm.LastPressResolvedNewSelection);
    }
}
