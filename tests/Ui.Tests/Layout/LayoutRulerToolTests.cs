using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// docs/design/layout-view.md §9B.5/§9B.6 — placing, snapping, selecting, dragging, deleting and
/// clearing rulers. Gates 6, 7, 8 and 11.
/// </summary>
public class LayoutRulerToolTests : System.IDisposable
{
    public LayoutRulerToolTests() => LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;

    public void Dispose()
    {
        LayoutTextOutline.TestOverrideTypeface = null;
        System.GC.SuppressFinalize(this);
    }

    private static readonly LayerKey Metal = new(1, 0);

    private static LayoutView Model(long snapDbu = 0) => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit = LayoutUnit.Um,
        SnapDbu = snapDbu,
    };

    private static LayoutEditorViewModel RulerVm(LayoutView model) =>
        new(model) { ActiveTool = LayoutEditorViewModel.Tool.Ruler };

    /// <summary>Two clicks, at the same tolerances the canvas passes.</summary>
    private static void Place(LayoutEditorViewModel vm, double x1, double y1, double x2, double y2,
                              KeyModifiers secondMods = KeyModifiers.None, long snapTolDbu = 0)
    {
        vm.OnPointerPressed(x1, y1, KeyModifiers.None, 1, 40, 1, snapTolDbu);
        vm.OnPointerMoved(x2, y2, leftDown: false, secondMods, 40, 1, snapTolDbu);
        vm.OnPointerPressed(x2, y2, secondMods, 1, 40, 1, snapTolDbu);
    }

    // ── §9B.5: two-click placement ────────────────────────────────────────────────────────────────

    [Fact]
    public void TwoClicks_CommitOneRuler_AndTheToolStaysArmed()
    {
        var model = Model();
        var vm = RulerVm(model);

        Place(vm, 0, 0, 3_000, 4_000);

        var r = Assert.Single(model.Rulers);
        Assert.Equal(0, r.X1);
        Assert.Equal(3_000, r.X2);
        Assert.Equal(4_000, r.Y2);
        Assert.Equal(5_000, r.DistanceDbu);

        // R-rul-4: Fixed, 11 pt.
        Assert.Equal(RulerSizeMode.Fixed, r.SizeMode);
        Assert.Equal(11.0, r.TextSizePt);

        // R-rul-8: the tool stays armed for the next one.
        Assert.Equal(LayoutEditorViewModel.Tool.Ruler, vm.ActiveTool);
        Place(vm, 0, 0, 1_000, 0);
        Assert.Equal(2, model.Rulers.Count);
    }

    [Fact]
    public void CoincidentEndpoints_AreDiscarded_NotCommitted()
    {
        var model = Model();
        var vm = RulerVm(model);
        Place(vm, 1_234, 5_678, 1_234, 5_678);
        Assert.Empty(model.Rulers);
    }

    [Fact]
    public void LivePreview_ShowsTheWholeRuler_BeforeTheSecondClick()
    {
        var model = Model();
        var vm = RulerVm(model);

        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 40, 1);
        vm.OnPointerMoved(2_000, 0, leftDown: false, KeyModifiers.None, 40, 1);

        var preview = vm.Overlay.RulerPreview;
        Assert.NotNull(preview);
        Assert.Equal(2_000, preview!.X2);
        // R-rul-8: "the number is visible BEFORE committing."
        Assert.Equal(2_000, preview.DistanceDbu);
        Assert.Empty(model.Rulers);
    }

    // ── D arms the tool (owner, 2026-08-27) ───────────────────────────────────────────────────────

    [Fact]
    public void D_ArmsTheRulerTool_FromAnyTool()
    {
        var model = Model();
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        vm.OnKeyDown(Key.D, KeyModifiers.None);
        Assert.Equal(LayoutEditorViewModel.Tool.Ruler, vm.ActiveTool);

        // …and from another drawing tool, without a detour through Select.
        vm.ActiveTool = LayoutEditorViewModel.Tool.Rect;
        vm.OnKeyDown(Key.D, KeyModifiers.None);
        Assert.Equal(LayoutEditorViewModel.Tool.Ruler, vm.ActiveTool);
    }

    [Fact]
    public void D_WithAModifier_DoesNotArmIt()
    {
        // Ctrl/Cmd+D is Duplicate and Alt arms a duplicate drag — neither may be stolen.
        foreach (var mods in new[] { KeyModifiers.Control, KeyModifiers.Meta, KeyModifiers.Alt })
        {
            var vm = new LayoutEditorViewModel(Model()) { ActiveTool = LayoutEditorViewModel.Tool.Select };
            vm.OnKeyDown(Key.D, mods);
            Assert.Equal(LayoutEditorViewModel.Tool.Select, vm.ActiveTool);
        }
    }

    [Fact]
    public void D_IsAnOrdinaryCharacterWhileALabelIsBeingTyped()
    {
        var model = Model();
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Label };
        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 40, 1);
        Assert.True(vm.IsTypingLabel);

        vm.OnKeyDown(Key.D, KeyModifiers.None);
        Assert.Equal(LayoutEditorViewModel.Tool.Label, vm.ActiveTool);
        Assert.True(vm.IsTypingLabel);
    }

    [Fact]
    public void D_DoesNotAbandonADragInProgress()
    {
        var model = Model();
        model.Shapes.Add(new RectShape { Layer = Metal, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40);
        vm.OnPointerReleased(500, 500, KeyModifiers.None);

        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(900, 500, leftDown: true, KeyModifiers.None, 40);
        vm.OnKeyDown(Key.D, KeyModifiers.None);

        Assert.Equal(LayoutEditorViewModel.Tool.Select, vm.ActiveTool);
        vm.OnPointerReleased(900, 500, KeyModifiers.None);
        Assert.Equal(400, ((RectShape)model.Shapes[0]).X1);   // the move still committed
    }

    // ── Gate 8: Escape ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Escape_MidPlacement_DisarmsToSelect_WithNothingCommitted()
    {
        var model = Model();
        var vm = RulerVm(model);

        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 40, 1);
        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.Equal(LayoutEditorViewModel.Tool.Select, vm.ActiveTool);
        Assert.Empty(model.Rulers);
        Assert.Null(vm.Overlay.RulerPreview);
    }

    // ── Gate 6: snap ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GeometrySnap_LandsExactlyOnACorner_AndTheReadoutIsExact()
    {
        var model = Model();
        // Two rects whose facing corners are exactly 4,000 DBU apart.
        model.Shapes.Add(new RectShape { Layer = Metal, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 });
        model.Shapes.Add(new RectShape { Layer = Metal, X1 = 5_000, Y1 = 0, X2 = 6_000, Y2 = 1_000 });

        var vm = RulerVm(model);
        vm.GeometrySnapEnabled = true;

        // Both clicks land NEAR the facing corners, never on them.
        Place(vm, 1_017, 13, 4_988, -21, snapTolDbu: 200);

        var r = Assert.Single(model.Rulers);
        Assert.Equal(1_000, r.X1); Assert.Equal(0, r.Y1);
        Assert.Equal(5_000, r.X2); Assert.Equal(0, r.Y2);
        Assert.Equal(4_000, r.DistanceDbu);
    }

    [Fact]
    public void Shift_LocksTheSecondEndpointTo45Degrees_EvenInAManhattanDocument()
    {
        // R-rul-10: NOT the document's own AngleMode — a Manhattan document is a statement about
        // manufacturable ARTWORK, and the diagonal gap between two Manhattan traces is exactly the
        // measurement you most want to take.
        var model = Model();
        model.AngleMode = AngleMode.Manhattan;
        var vm = RulerVm(model);

        Place(vm, 0, 0, 1_000, 940, secondMods: KeyModifiers.Shift);

        var r = Assert.Single(model.Rulers);
        Assert.Equal(System.Math.Abs(r.X2 - r.X1), System.Math.Abs(r.Y2 - r.Y1));
        Assert.True(r.X2 > 0 && r.Y2 > 0);
    }

    [Fact]
    public void Shift_LocksToTheAxis_WhenTheMoveIsPredominantlyHorizontal()
    {
        var model = Model();
        var vm = RulerVm(model);
        Place(vm, 0, 0, 4_000, 120, secondMods: KeyModifiers.Shift);

        var r = Assert.Single(model.Rulers);
        Assert.Equal(0, r.Y2);
        // LayoutSnapping.ConstrainAndSnap — the SAME helper a Path vertex uses — preserves the drag's
        // own LENGTH along the chosen direction rather than projecting onto it, so the X lands at
        // hypot(4000, 120), not at 4000. Asserted as the helper actually behaves; a ruler must not
        // acquire its own second constraint rule.
        Assert.Equal(4_002, r.X2);
    }

    [Fact]
    public void GeometrySnap_OutranksTheShiftConstraint()
    {
        // R-rul-10: "a snapped endpoint is a stronger statement of intent than a held modifier."
        var model = Model();
        model.Shapes.Add(new RectShape { Layer = Metal, X1 = 3_000, Y1 = 2_000, X2 = 4_000, Y2 = 3_000 });

        var vm = RulerVm(model);
        vm.GeometrySnapEnabled = true;

        // Shift alone would flatten this to (4000, 0) or a 45 deg point; the corner at (3000, 2000)
        // is in tolerance and wins.
        Place(vm, 0, 0, 3_030, 1_980, secondMods: KeyModifiers.Shift, snapTolDbu: 200);

        var r = Assert.Single(model.Rulers);
        Assert.Equal(3_000, r.X2);
        Assert.Equal(2_000, r.Y2);
    }

    // ── Gate 11: selection ────────────────────────────────────────────────────────────────────────

    private static LayoutEditorViewModel WithOneRuler(out LayoutView model, out RulerAnnotation ruler)
    {
        model = Model();
        ruler = new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 0, TextHeightDbu = 500 };
        model.Rulers.Add(ruler);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.NoteZoomPxPerDbu(0.01);
        return vm;
    }

    [Fact]
    public void ClickingTheLine_SelectsTheRuler()
    {
        var vm = WithOneRuler(out _, out _);
        vm.OnPointerPressed(5_000, 0, KeyModifiers.None, 1, 40, 0.01);
        vm.OnPointerReleased(5_000, 0, KeyModifiers.None);

        Assert.Equal([0], vm.SelectedRulerIndices);
        Assert.Empty(vm.SelectedIndices);
    }

    [Fact]
    public void ClickingTheReadoutText_SelectsTheRuler()
    {
        // R-rul-11: "clicking the number selects the ruler, which is the affordance a user reaches
        // for first." The readout sits above the midpoint; ask the RENDERER where it is rather than
        // guessing, which is the point of the shared measurement.
        var vm = WithOneRuler(out var model, out var ruler);
        var textBb = LayoutRenderer.MeasureRulerTextWorldBbox(ruler, model.DisplayUnit, model.DbuPerMicron, 0.01);
        Assert.False(textBb.IsEmpty);

        long cx = (textBb.MinX + textBb.MaxX) / 2, cy = (textBb.MinY + textBb.MaxY) / 2;
        Assert.True(cy > 0, "the readout must sit off the line, not on it");

        vm.OnPointerPressed(cx, cy, KeyModifiers.None, 1, 4, 0.01);
        vm.OnPointerReleased(cx, cy, KeyModifiers.None);

        Assert.Equal([0], vm.SelectedRulerIndices);
    }

    [Fact]
    public void ARulerOverATrace_IsSelectedInPreferenceToTheTrace()
    {
        var model = Model();
        model.Shapes.Add(new RectShape { Layer = Metal, X1 = 0, Y1 = -1_000, X2 = 10_000, Y2 = 1_000 });
        model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 0, TextHeightDbu = 500 });

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.NoteZoomPxPerDbu(0.01);
        vm.OnPointerPressed(5_000, 0, KeyModifiers.None, 1, 40, 0.01);
        vm.OnPointerReleased(5_000, 0, KeyModifiers.None);

        Assert.Equal([0], vm.SelectedRulerIndices);
        Assert.Empty(vm.SelectedIndices);
    }

    [Fact]
    public void MarqueeEnclosure_SelectsTheRuler_AndCrossingOnlyDoesNot()
    {
        var model = Model();
        model.Rulers.Add(new RulerAnnotation { X1 = 1_000, Y1 = 1_000, X2 = 2_000, Y2 = 2_000, TextHeightDbu = 200 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.NoteZoomPxPerDbu(0.01);

        // Left-to-right = enclose. The whole line is inside.
        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 40, 0.01);
        vm.OnPointerMoved(5_000, 5_000, leftDown: true, KeyModifiers.None, 40);
        vm.OnPointerReleased(5_000, 5_000, KeyModifiers.None);
        Assert.Equal([0], vm.SelectedRulerIndices);

        // Left-to-right again, but only partially covering it — enclose must refuse.
        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 40, 0.01);
        vm.OnPointerMoved(1_500, 1_500, leftDown: true, KeyModifiers.None, 40);
        vm.OnPointerReleased(1_500, 1_500, KeyModifiers.None);
        Assert.Empty(vm.SelectedRulerIndices);
    }

    [Fact]
    public void EndpointHandles_RenderOnlyForASingleRulerSelection()
    {
        var model = Model();
        model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 0, TextHeightDbu = 500 });
        model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 5_000, X2 = 10_000, Y2 = 5_000, TextHeightDbu = 500 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.NoteZoomPxPerDbu(0.01);

        vm.OnPointerPressed(5_000, 0, KeyModifiers.None, 1, 40, 0.01);
        vm.OnPointerReleased(5_000, 0, KeyModifiers.None);
        Assert.True(vm.Overlay.ShowRulerEndpointHandles);

        vm.OnPointerPressed(5_000, 5_000, KeyModifiers.Shift, 1, 40, 0.01);
        vm.OnPointerReleased(5_000, 5_000, KeyModifiers.Shift);
        Assert.Equal(2, vm.SelectedRulerIndices.Count);
        Assert.False(vm.Overlay.ShowRulerEndpointHandles);
    }

    [Fact]
    public void SelectAll_IncludesRulers_SoCtrlADeleteLeavesNothingBehind()
    {
        var model = Model();
        model.Shapes.Add(new RectShape { Layer = Metal, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 });
        model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 5_000, X2 = 4_000, Y2 = 5_000 });

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        Assert.Single(vm.SelectedIndices);
        Assert.Single(vm.SelectedRulerIndices);

        vm.OnKeyDown(Key.Delete, KeyModifiers.None);
        Assert.Empty(model.Shapes);
        Assert.Empty(model.Rulers);

        // Still ONE undo entry across both kinds.
        vm.UndoCommand.Execute(null);
        Assert.Single(model.Shapes);
        Assert.Single(model.Rulers);
    }

    // ── Cycling through what is under a ruler (owner, 2026-08-27) ─────────────────────────────────

    /// <summary>A ruler lying along the middle of a trace, plus the view model holding both.</summary>
    private static LayoutEditorViewModel RulerOverTrace(out LayoutView model)
    {
        model = Model();
        model.Shapes.Add(new RectShape { Layer = Metal, X1 = 0, Y1 = -1_000, X2 = 10_000, Y2 = 1_000 });
        model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 0, TextHeightDbu = 500 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.NoteZoomPxPerDbu(0.01);
        return vm;
    }

    private static void ClickAt(LayoutEditorViewModel vm, long x, long y)
    {
        vm.OnPointerPressed(x, y, KeyModifiers.None, 1, 40, 0.01);
        vm.OnPointerReleased(x, y, KeyModifiers.None);
    }

    [Fact]
    public void ClickingAnAlreadySelectedRuler_CyclesDownToTheGeometryUnderIt()
    {
        var vm = RulerOverTrace(out _);

        // First click takes the ruler — it paints above the trace (R-rul-11).
        ClickAt(vm, 5_000, 0);
        Assert.Equal([0], vm.SelectedRulerIndices);
        Assert.Empty(vm.SelectedIndices);

        // Second click at the same point walks DOWN the stack to the trace.
        ClickAt(vm, 5_000, 0);
        Assert.Empty(vm.SelectedRulerIndices);
        Assert.Equal([0], vm.SelectedIndices);

        // …and a third wraps back to the ruler, exactly as overlapping shapes have always done.
        ClickAt(vm, 5_000, 0);
        Assert.Equal([0], vm.SelectedRulerIndices);
        Assert.Empty(vm.SelectedIndices);
    }

    [Fact]
    public void TheCycleReachesAPlacedCellUnderARuler_Too()
    {
        // "cell instance or primitive etc." — an instance is reached the same way, still only when no
        // SHAPE was hit (L3a's own rule, unchanged).
        var model = Model();
        model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 0, TextHeightDbu = 500 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.NoteZoomPxPerDbu(0.01);

        // No instance to resolve in a scratch document, so the honest assertion is the STACK the
        // press builds — which is what the cycle walks.
        var picks = vm.PickStackForTest(5_000, 0, 40);
        Assert.Equal(LayoutPickKind.Ruler, Assert.Single(picks).Kind);

        model.Shapes.Add(new RectShape { Layer = Metal, X1 = 0, Y1 = -1_000, X2 = 10_000, Y2 = 1_000 });
        picks = vm.PickStackForTest(5_000, 0, 40);
        Assert.Equal(2, picks.Count);
        Assert.Equal(LayoutPickKind.Ruler, picks[0].Kind);
        Assert.Equal(LayoutPickKind.Shape, picks[1].Kind);
    }

    [Fact]
    public void SelectingAnInstance_ClearsTheRulerSelection_JustAsSelectingAShapeDoes()
    {
        // Owner report, 2026-08-27: cycling from a ruler down to a placed cell left the ruler selected
        // too, while cycling to a plain shape did not. Nothing to do with PCells — SetSelection had
        // been taught about the third channel and SetInstanceSelection, its mirror, had not.
        var model = Model();
        model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 0, TextHeightDbu = 500 });
        model.Instances.Add(new LayoutInstance { CellRef = "somewhere" });
        var vm = new LayoutEditorViewModel(model);

        vm.SelectRulers([0]);
        Assert.Equal([0], vm.SelectedRulerIndices);

        vm.SelectInstance(0);
        Assert.Equal([0], vm.SelectedInstanceIndices);
        Assert.Empty(vm.SelectedRulerIndices);

        // …and the reverse direction, which already worked, still does.
        vm.SelectRulers([0]);
        Assert.Equal([0], vm.SelectedRulerIndices);
        Assert.Empty(vm.SelectedInstanceIndices);
    }

    [Fact]
    public void TheCycleWrapsIndefinitely_NotJustOnce()
    {
        // The second defect the same report turned up: SetRulerSelection and SetInstanceSelection both
        // cleared the cycle cache when they cleared a shape selection — destroying the very stack being
        // walked. Three clicks looked right (ruler, shape, ruler); the FOURTH rebuilt the stack and sat
        // on the ruler forever. Six clicks is the shortest run that catches it.
        var vm = RulerOverTrace(out _);

        for (int pass = 0; pass < 3; pass++)
        {
            ClickAt(vm, 5_000, 0);
            Assert.Equal([0], vm.SelectedRulerIndices);
            Assert.Empty(vm.SelectedIndices);

            ClickAt(vm, 5_000, 0);
            Assert.Empty(vm.SelectedRulerIndices);
            Assert.Equal([0], vm.SelectedIndices);
        }
    }

    [Fact]
    public void TheStatusLine_SaysWhereInTheStackYouAre()
    {
        var vm = RulerOverTrace(out _);

        ClickAt(vm, 5_000, 0);
        Assert.EndsWith("1 of 2", vm.SelectionStatusText);

        ClickAt(vm, 5_000, 0);
        Assert.EndsWith("2 of 2", vm.SelectionStatusText);
    }

    [Fact]
    public void WithNoRulers_TheCycleIsExactlyWhatItAlwaysWas()
    {
        // Two overlapping shapes on one layer: the small one is reachable, and a repeat click walks
        // between them. Nothing about the pick stack changed this.
        var model = Model();
        model.Shapes.Add(new RectShape { Layer = Metal, X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        model.Shapes.Add(new RectShape { Layer = Metal, X1 = 4_000, Y1 = 4_000, X2 = 6_000, Y2 = 6_000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        ClickAt(vm, 5_000, 5_000);
        int first = Assert.Single(vm.SelectedIndices);
        ClickAt(vm, 5_000, 5_000);
        int second = Assert.Single(vm.SelectedIndices);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void DraggingAnAlreadySelectedRuler_StillMovesIt_RatherThanCycling()
    {
        // The cycle advances on a press, and the press also begins a move drag — so a press-and-DRAG
        // on a selected ruler must still move something, not leave the user having silently changed
        // what is selected and moved nothing. (It moves whatever the press selected, which is the
        // same contract a stack of overlapping shapes has always had.)
        var vm = RulerOverTrace(out var model);
        ClickAt(vm, 5_000, 0);
        Assert.Equal([0], vm.SelectedRulerIndices);

        vm.OnPointerPressed(5_000, 0, KeyModifiers.None, 1, 40, 0.01);
        vm.OnPointerMoved(5_000, 3_000, leftDown: true, KeyModifiers.None, 40);
        vm.OnPointerReleased(5_000, 3_000, KeyModifiers.None);

        // The press cycled to the trace, so the trace is what moved — and it did move.
        Assert.Equal([0], vm.SelectedIndices);
        Assert.Equal(2_000, ((RectShape)model.Shapes[0]).Y1);
    }

    [Fact]
    public void AnEndpointGrab_StillBeatsTheCycle()
    {
        // §9B.6: an endpoint is a HANDLE. It must not be turned into a cycle step, or the one gesture
        // that re-measures a ruler would become unreachable the moment anything lay underneath it.
        var vm = RulerOverTrace(out var model);
        ClickAt(vm, 5_000, 0);
        Assert.Equal([0], vm.SelectedRulerIndices);

        vm.OnPointerPressed(10_000, 0, KeyModifiers.None, 1, 40, 0.01);
        vm.OnPointerMoved(20_000, 0, leftDown: true, KeyModifiers.None, 40);
        vm.OnPointerReleased(20_000, 0, KeyModifiers.None);

        Assert.Equal([0], vm.SelectedRulerIndices);      // still the ruler, not cycled to the trace
        Assert.Equal(20_000, model.Rulers[0].X2);        // and the endpoint moved
    }

    // ── Gate 7: undo/redo ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Place_IsExactlyOneUndoEntry()
    {
        var model = Model();
        var vm = RulerVm(model);
        Place(vm, 0, 0, 1_000, 0);
        Assert.Single(model.Rulers);

        vm.UndoCommand.Execute(null);
        Assert.Empty(model.Rulers);
        vm.RedoCommand.Execute(null);
        Assert.Single(model.Rulers);
    }

    [Fact]
    public void MoveDrag_MovesTheWholeRuler_AsOneUndoEntry()
    {
        var vm = WithOneRuler(out var model, out _);
        vm.OnPointerPressed(5_000, 0, KeyModifiers.None, 1, 40, 0.01);
        vm.OnPointerReleased(5_000, 0, KeyModifiers.None);

        vm.OnPointerPressed(5_000, 0, KeyModifiers.None, 1, 40, 0.01);
        vm.OnPointerMoved(5_000, 2_000, leftDown: true, KeyModifiers.None, 40);
        vm.OnPointerReleased(5_000, 2_000, KeyModifiers.None);

        Assert.Equal(2_000, model.Rulers[0].Y1);
        Assert.Equal(2_000, model.Rulers[0].Y2);

        vm.UndoCommand.Execute(null);
        Assert.Equal(0, model.Rulers[0].Y1);
        Assert.Equal(0, model.Rulers[0].Y2);
    }

    [Fact]
    public void DraggingOneEndpoint_MovesOnlyThatEndpoint_AndReMeasures()
    {
        var vm = WithOneRuler(out var model, out _);
        vm.OnPointerPressed(5_000, 0, KeyModifiers.None, 1, 40, 0.01);
        vm.OnPointerReleased(5_000, 0, KeyModifiers.None);
        Assert.True(vm.Overlay.ShowRulerEndpointHandles);

        // Grab the SECOND endpoint and pull it out to 20,000.
        vm.OnPointerPressed(10_000, 0, KeyModifiers.None, 1, 40, 0.01);
        vm.OnPointerMoved(20_000, 0, leftDown: true, KeyModifiers.None, 40);

        // The live preview re-measures before anything is committed.
        Assert.NotNull(vm.Overlay.RulerDragOverrides);
        Assert.Equal(20_000, vm.Overlay.RulerDragOverrides![0].DistanceDbu);
        Assert.Equal(10_000, model.Rulers[0].X2);   // the model is untouched mid-drag

        vm.OnPointerReleased(20_000, 0, KeyModifiers.None);
        Assert.Equal(0, model.Rulers[0].X1);        // the OTHER endpoint did not move
        Assert.Equal(20_000, model.Rulers[0].X2);
        Assert.Equal(20_000, model.Rulers[0].DistanceDbu);

        vm.UndoCommand.Execute(null);
        Assert.Equal(10_000, model.Rulers[0].X2);
    }

    [Fact]
    public void Delete_RemovesSelectedRulers_AsOneUndoEntry()
    {
        var vm = WithOneRuler(out var model, out _);
        vm.OnPointerPressed(5_000, 0, KeyModifiers.None, 1, 40, 0.01);
        vm.OnPointerReleased(5_000, 0, KeyModifiers.None);

        vm.OnKeyDown(Key.Delete, KeyModifiers.None);
        Assert.Empty(model.Rulers);

        vm.UndoCommand.Execute(null);
        Assert.Single(model.Rulers);
    }

    // ── R-rul-13: Ctrl+K ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClearAllRulers_RemovesEveryOne_AndOneUndoRestoresThemAll()
    {
        var model = Model();
        for (int i = 0; i < 5; i++)
            model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = i * 1_000, X2 = 1_000 * (i + 1), Y2 = i * 1_000 });

        var vm = new LayoutEditorViewModel(model);
        Assert.True(vm.ClearAllRulersAvailability.CanExecute);

        vm.ClearAllRulers();
        Assert.Empty(model.Rulers);

        vm.UndoCommand.Execute(null);
        Assert.Equal(5, model.Rulers.Count);
        for (int i = 0; i < 5; i++) Assert.Equal(1_000 * (i + 1), model.Rulers[i].X2);
    }

    [Fact]
    public void ClearAllRulers_IsDisabledWithAReason_AtZeroRulers()
    {
        var vm = new LayoutEditorViewModel(Model());
        var avail = vm.ClearAllRulersAvailability;
        Assert.False(avail.CanExecute);
        Assert.False(string.IsNullOrWhiteSpace(avail.DisabledReason));

        vm.ClearAllRulers();   // a no-op, not an empty undo entry
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    // ── R-rul-12: the context menu finds one ──────────────────────────────────────────────────────

    [Fact]
    public void FindRulerForContextMenu_FindsTheRulerUnderTheClick()
    {
        var vm = WithOneRuler(out _, out _);
        Assert.Equal(0, vm.FindRulerForContextMenu(5_000, 0, 40));
        Assert.Null(vm.FindRulerForContextMenu(5_000, 90_000, 40));
    }

    // ── R-rul-11a: multi-selection editing, ONE undo entry ────────────────────────────────────────

    [Fact]
    public void TenRulers_OneTextSize_OneUndoEntry()
    {
        var model = Model();
        for (int i = 0; i < 10; i++)
            model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = i * 1_000, X2 = 5_000, Y2 = i * 1_000, TextSizePt = 11.0 });

        var vm = new LayoutEditorViewModel(model);
        vm.SelectRulers(Enumerable.Range(0, model.Rulers.Count));

        vm.ApplyToEachRuler<double>("Ruler Text Size", r => r.TextSizePt, (r, v) => r.TextSizePt = v, 16.0);
        Assert.All(model.Rulers, r => Assert.Equal(16.0, r.TextSizePt));

        vm.UndoCommand.Execute(null);
        Assert.All(model.Rulers, r => Assert.Equal(11.0, r.TextSizePt));
    }

    [Fact]
    public void ApplyToEachRuler_PushesNothing_WhenNothingActuallyChanges()
    {
        var model = Model();
        model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 0, TextSizePt = 11.0 });
        var vm = new LayoutEditorViewModel(model);
        vm.SelectRulers(Enumerable.Range(0, model.Rulers.Count));

        vm.ApplyToEachRuler<double>("Ruler Text Size", r => r.TextSizePt, (r, v) => r.TextSizePt = v, 11.0);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }
}
