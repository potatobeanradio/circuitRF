using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// ── Phase L1b gates: drawing tools, snap/angle mode, undo, dirty/save ──
// docs/sonnet-briefs/brief-L1b-drawing-tools.md "Gate (acceptance)" items 2-12.

public class LayoutDrawingToolsTests
{
    private static LayoutView FreshModel(long snapDbu = 1000, AngleMode angleMode = AngleMode.AnyAngle) => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = snapDbu,
        AngleMode    = angleMode,
    };

    private static LayoutEditorViewModel MakeVm(LayoutView model) => new(model);

    // ── Gate 2: every tool produces the right primitive on the current layer ────

    [Fact]
    public void Rect_PressDragRelease_ProducesNormalizedRectShapeOnCurrentLayer()
    {
        var vm = MakeVm(FreshModel());
        vm.CurrentLayerKey = new LayerKey(2, 0);
        vm.ActiveTool = LayoutEditorViewModel.Tool.Rect;

        vm.OnPointerPressed(5000, 9000, KeyModifiers.None);
        vm.OnPointerMoved(1000, 2000, true, KeyModifiers.None);
        vm.OnPointerReleased(1000, 2000, KeyModifiers.None);

        var shape = Assert.Single(vm.Model.Shapes);
        var rect = Assert.IsType<RectShape>(shape);
        Assert.Equal(new LayerKey(2, 0), rect.Layer);
        Assert.Equal(1000, rect.X1); Assert.Equal(2000, rect.Y1);
        Assert.Equal(5000, rect.X2); Assert.Equal(9000, rect.Y2);
    }

    [Fact]
    public void RoundedRect_CornerRadius_ComesFromToolbarField_ClampedToHalfShorterSide()
    {
        var vm = MakeVm(FreshModel());
        vm.ActiveTool = LayoutEditorViewModel.Tool.RoundedRect;
        vm.CommitCornerRadiusText("50000"); // far larger than half the shape

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerMoved(10000, 4000, true, KeyModifiers.None); // 10000 x 4000 -> half of shorter side = 2000
        vm.OnPointerReleased(10000, 4000, KeyModifiers.None);

        var rr = Assert.IsType<RoundedRectShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(2000, rr.CornerRadius);
    }

    [Fact]
    public void Circle_PressIsCenter_DragDistanceIsRadius()
    {
        var vm = MakeVm(FreshModel());
        vm.ActiveTool = LayoutEditorViewModel.Tool.Circle;

        vm.OnPointerPressed(1000, 1000, KeyModifiers.None);
        vm.OnPointerMoved(1000, 6000, true, KeyModifiers.None); // straight up 5000 dbu
        vm.OnPointerReleased(1000, 6000, KeyModifiers.None);

        var circle = Assert.IsType<CircleShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(1000, circle.Cx); Assert.Equal(1000, circle.Cy);
        Assert.Equal(5000, circle.R);
    }

    [Fact]
    public void Polygon_MultiClick_DoubleClickCloses_ProducesClosedPolygon()
    {
        var vm = MakeVm(FreshModel());
        vm.ActiveTool = LayoutEditorViewModel.Tool.Polygon;

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerPressed(1000, 0, KeyModifiers.None);
        vm.OnPointerPressed(1000, 1000, KeyModifiers.None);
        vm.OnPointerPressed(1000, 1000, KeyModifiers.None, clickCount: 2); // double-click closes

        var poly = Assert.IsType<PolygonShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(new long[] { 0, 0, 1000, 0, 1000, 1000 }, poly.Xy);
    }

    [Fact]
    public void Polygon_EnterCloses_MinimumThreeVertices_ShorterAttemptDiscarded()
    {
        var vm = MakeVm(FreshModel());
        vm.ActiveTool = LayoutEditorViewModel.Tool.Polygon;

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerPressed(1000, 0, KeyModifiers.None);
        vm.OnKeyDown(Key.Enter, KeyModifiers.None); // only 2 points -- below the 3-vertex minimum

        Assert.Empty(vm.Model.Shapes);
    }

    [Fact]
    public void Path_MultiClickEnter_ProducesOpenPathWithToolbarWidthAndEndStyle()
    {
        var vm = MakeVm(FreshModel());
        vm.ActiveTool = LayoutEditorViewModel.Tool.Path;
        vm.CommitPathWidthText("5um");
        vm.CurrentPathEndStyle = PathEndStyle.Round;

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerPressed(1000, 0, KeyModifiers.None);
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);

        var path = Assert.IsType<PathShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(new long[] { 0, 0, 1000, 0 }, path.Xy);
        Assert.Equal(5000, path.Width);
        Assert.Equal(PathEndStyle.Round, path.End);
    }

    [Fact]
    public void Label_ClickThenType_ProducesLabelShapeWithToolbarHeight()
    {
        var vm = MakeVm(FreshModel());
        vm.ActiveTool = LayoutEditorViewModel.Tool.Label;
        vm.CommitLabelHeightText("8um");

        vm.OnPointerPressed(3000, 4000, KeyModifiers.None);
        vm.OnTextInput("P1");
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);

        var label = Assert.IsType<LabelShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal("P1", label.Text);
        Assert.Equal(3000, label.X); Assert.Equal(4000, label.Y);
        Assert.Equal(8000, label.Height);
        Assert.False(label.IsPort);
    }

    // ── Gate 3: one gesture, one undo entry ──────────────────────────────────

    [Fact]
    public void TwelveVertexPolygon_UndoesInOneCtrlZ_RedoRestoresIdentically()
    {
        var vm = MakeVm(FreshModel());
        vm.ActiveTool = LayoutEditorViewModel.Tool.Polygon;

        for (int i = 0; i < 12; i++)
            vm.OnPointerPressed(i * 100, (i % 2) * 500, KeyModifiers.None);
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);

        Assert.Single(vm.Model.Shapes);
        var before = LayoutPersistence.Serialize(vm.Model);

        Assert.True(vm.UndoRedo.CanUndo);
        vm.UndoRedo.Undo();
        Assert.Empty(vm.Model.Shapes);

        vm.UndoRedo.Redo();
        var after = LayoutPersistence.Serialize(vm.Model);
        Assert.Equal(before, after);
    }

    [Fact]
    public void TwelveVertexPolygon_IsExactlyOneUndoEntry()
    {
        var vm = MakeVm(FreshModel());
        vm.ActiveTool = LayoutEditorViewModel.Tool.Polygon;

        for (int i = 0; i < 12; i++)
            vm.OnPointerPressed(i * 100, (i % 2) * 500, KeyModifiers.None);
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);

        var poly = Assert.IsType<PolygonShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(24, poly.Xy.Length); // 12 vertices

        vm.UndoRedo.Undo();
        Assert.Empty(vm.Model.Shapes); // the entire 12-vertex polygon undone by one Undo
        Assert.False(vm.UndoRedo.CanUndo);
    }

    // ── Gate 4: undo restores list position ──────────────────────────────────

    [Fact]
    public void DrawABC_UndoCUndoB_RedoB_RestoresAtOriginalIndex()
    {
        var vm = MakeVm(FreshModel());
        vm.ActiveTool = LayoutEditorViewModel.Tool.Rect;

        void DrawRect(long x)
        {
            vm.OnPointerPressed(x, 0, KeyModifiers.None);
            vm.OnPointerMoved(x + 2000, 2000, true, KeyModifiers.None);
            vm.OnPointerReleased(x + 2000, 2000, KeyModifiers.None);
        }

        DrawRect(0);    // A -> index 0
        DrawRect(1000); // B -> index 1
        DrawRect(2000); // C -> index 2
        Assert.Equal(3, vm.Model.Shapes.Count);
        var originalB = vm.Model.Shapes[1];

        vm.UndoRedo.Undo(); // undo C
        vm.UndoRedo.Undo(); // undo B
        Assert.Single(vm.Model.Shapes);

        vm.UndoRedo.Redo(); // redo B
        Assert.Equal(2, vm.Model.Shapes.Count);
        Assert.Same(originalB, vm.Model.Shapes[1]); // back at index 1, not appended to index... (well, only 2 shapes, but index matches)
    }

    [Fact]
    public void DrawABC_UndoAll_RedoAll_PreservesOriginalOrder()
    {
        var vm = MakeVm(FreshModel());
        vm.ActiveTool = LayoutEditorViewModel.Tool.Rect;

        void DrawRect(long x)
        {
            vm.OnPointerPressed(x, 0, KeyModifiers.None);
            vm.OnPointerMoved(x + 2000, 2000, true, KeyModifiers.None);
            vm.OnPointerReleased(x + 2000, 2000, KeyModifiers.None);
        }

        DrawRect(0); DrawRect(1000); DrawRect(2000);
        var expected = vm.Model.Shapes.Select(s => ((RectShape)s).X1).ToArray();

        vm.UndoRedo.Undo(); vm.UndoRedo.Undo(); vm.UndoRedo.Undo();
        Assert.Empty(vm.Model.Shapes);

        vm.UndoRedo.Redo(); vm.UndoRedo.Redo(); vm.UndoRedo.Redo();
        var actual = vm.Model.Shapes.Select(s => ((RectShape)s).X1).ToArray();
        Assert.Equal(expected, actual);
    }

    // ── Gate 5: snap ──────────────────────────────────────────────────────────

    [Fact]
    public void Snap_OneMicronGrid_EveryPlacedVertexIsMultipleOf1000Dbu()
    {
        var vm = MakeVm(FreshModel(snapDbu: 1000));
        vm.ActiveTool = LayoutEditorViewModel.Tool.Polygon;

        vm.OnPointerPressed(123, 456, KeyModifiers.None);
        vm.OnPointerPressed(2789, 3111, KeyModifiers.None);
        vm.OnPointerPressed(5500, 500, KeyModifiers.None);
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);

        var poly = Assert.IsType<PolygonShape>(Assert.Single(vm.Model.Shapes));
        foreach (var v in poly.Xy)
            Assert.Equal(0, v % 1000);
    }

    /// <summary>R-dup-2: the grid-snap TOGGLE (F9) is what places raw coordinates now — Alt no longer
    /// suspends snap for a drawing tool any more than it does for a drag, since one key meaning
    /// "suspend snap" while drawing and "duplicate" while dragging is the split this round removed.</summary>
    [Fact]
    public void Snap_GridToggleOff_PlacesRawCoordinates_ForThatPoint()
    {
        var vm = MakeVm(FreshModel(snapDbu: 1000));
        vm.ActiveTool = LayoutEditorViewModel.Tool.Rect;
        vm.ToggleSnapDbuEnabled();

        vm.OnPointerPressed(123, 456, KeyModifiers.None);
        vm.OnPointerMoved(789, 1011, true, KeyModifiers.None);
        vm.OnPointerReleased(789, 1011, KeyModifiers.None);

        var rect = Assert.IsType<RectShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(123, rect.X1); Assert.Equal(456, rect.Y1);
        Assert.Equal(789, rect.X2); Assert.Equal(1011, rect.Y2);
    }

    /// <summary>The other half of R-dup-2, stated as its own test so the retirement is pinned rather
    /// than merely implied by the absence of the old one: Alt while DRAWING is now inert.</summary>
    [Fact]
    public void Snap_AltNoLongerSuspendsSnap_WhileDrawing()
    {
        var vm = MakeVm(FreshModel(snapDbu: 1000));
        vm.ActiveTool = LayoutEditorViewModel.Tool.Rect;

        vm.OnPointerPressed(123, 456, KeyModifiers.Alt);
        vm.OnPointerMoved(789, 1011, true, KeyModifiers.Alt);
        vm.OnPointerReleased(789, 1011, KeyModifiers.Alt);

        var rect = Assert.IsType<RectShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(0, rect.X1 % 1000); Assert.Equal(0, rect.Y1 % 1000);
        Assert.Equal(0, rect.X2 % 1000); Assert.Equal(0, rect.Y2 % 1000);
    }

    [Fact]
    public void Snap_ZeroSnapDbu_PlacesRawCoordinates()
    {
        var vm = MakeVm(FreshModel(snapDbu: 0));
        vm.ActiveTool = LayoutEditorViewModel.Tool.Rect;

        vm.OnPointerPressed(123, 456, KeyModifiers.None);
        vm.OnPointerMoved(789, 1011, true, KeyModifiers.None);
        vm.OnPointerReleased(789, 1011, KeyModifiers.None);

        var rect = Assert.IsType<RectShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(123, rect.X1); Assert.Equal(456, rect.Y1);
        Assert.Equal(789, rect.X2); Assert.Equal(1011, rect.Y2);
    }

    // ── Gate 6: angle mode ────────────────────────────────────────────────────

    [Fact]
    public void AngleMode_Manhattan_EveryPolygonSegmentIsAxisAligned()
    {
        var vm = MakeVm(FreshModel(snapDbu: 1000, angleMode: AngleMode.Manhattan));
        vm.ActiveTool = LayoutEditorViewModel.Tool.Polygon;

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerPressed(4123, 3877, KeyModifiers.None);   // not axis-aligned raw input
        vm.OnPointerPressed(1000, 9321, KeyModifiers.None);
        vm.OnPointerPressed(0, 0, KeyModifiers.None, clickCount: 2);

        var poly = Assert.IsType<PolygonShape>(Assert.Single(vm.Model.Shapes));
        int n = poly.Xy.Length / 2;
        // Only the segments actually drawn are angle-constrained -- the implicit closing edge
        // (last vertex back to the first) is not a drawn segment, so it is excluded here.
        for (int i = 0; i < n - 1; i++)
        {
            int j = i + 1;
            long dx = poly.Xy[2 * j] - poly.Xy[2 * i];
            long dy = poly.Xy[2 * j + 1] - poly.Xy[2 * i + 1];
            Assert.True(dx == 0 || dy == 0, $"segment {i}->{j} is not axis-aligned: dx={dx} dy={dy}");
        }
    }

    [Fact]
    public void AngleMode_Deg45_EverySegmentAngleIsMultipleOf45Degrees()
    {
        var vm = MakeVm(FreshModel(snapDbu: 1000, angleMode: AngleMode.Deg45));
        vm.ActiveTool = LayoutEditorViewModel.Tool.Polygon;

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerPressed(5137, 5412, KeyModifiers.None);   // near-diagonal raw input
        vm.OnPointerPressed(9000, 137, KeyModifiers.None);    // near-horizontal raw input
        vm.OnPointerPressed(0, 0, KeyModifiers.None, clickCount: 2);

        var poly = Assert.IsType<PolygonShape>(Assert.Single(vm.Model.Shapes));
        int n = poly.Xy.Length / 2;
        // Only the segments actually drawn are angle-constrained -- the implicit closing edge
        // (last vertex back to the first) is not a drawn segment, so it is excluded here.
        for (int i = 0; i < n - 1; i++)
        {
            int j = i + 1;
            long dx = poly.Xy[2 * j] - poly.Xy[2 * i];
            long dy = poly.Xy[2 * j + 1] - poly.Xy[2 * i + 1];
            if (dx == 0 && dy == 0) continue;
            double angleDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            double nearestMultiple = Math.Round(angleDeg / 45.0) * 45.0;
            Assert.True(Math.Abs(angleDeg - nearestMultiple) < 1e-6,
                $"segment {i}->{j} angle {angleDeg} is not a multiple of 45 degrees");
        }
    }

    [Fact]
    public void AngleMode_AnyAngle_ArbitrarySegmentSurvivesUnchanged()
    {
        var vm = MakeVm(FreshModel(snapDbu: 0, angleMode: AngleMode.AnyAngle));
        vm.ActiveTool = LayoutEditorViewModel.Tool.Polygon;

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerPressed(4123, 3877, KeyModifiers.None);
        vm.OnPointerPressed(9001, 137, KeyModifiers.None);
        vm.OnPointerPressed(0, 0, KeyModifiers.None, clickCount: 2);

        var poly = Assert.IsType<PolygonShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(new long[] { 0, 0, 4123, 3877, 9001, 137 }, poly.Xy);
    }

    // ── Gate 7: changing snap/angle mode moves no existing geometry ─────────

    [Fact]
    public void ChangingSnapOrAngleMode_LeavesSerializationByteIdentical()
    {
        var model = FreshModel();
        var vm = MakeVm(model);
        vm.ActiveTool = LayoutEditorViewModel.Tool.Rect;
        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerMoved(5000, 5000, true, KeyModifiers.None);
        vm.OnPointerReleased(5000, 5000, KeyModifiers.None);

        var before = LayoutPersistence.Serialize(model);

        vm.SnapDbu = 500;
        model.AngleMode = AngleMode.Manhattan;

        var after = LayoutPersistence.Serialize(model);

        // Only the SnapDbu/AngleMode tokens themselves may differ -- the Shapes array must be identical.
        var linesBefore = before.Split('\n').SkipWhile(l => !l.Contains("\"Shapes\"")).ToArray();
        var linesAfter  = after.Split('\n').SkipWhile(l => !l.Contains("\"Shapes\"")).ToArray();
        Assert.Equal(string.Join('\n', linesBefore), string.Join('\n', linesAfter));
    }

    // ── Gate 8: typed entry parses and reverts ───────────────────────────────

    [Fact]
    public void TypedEntry_ParsesUnitSuffixedText_ToExactDbu()
    {
        var vm = MakeVm(FreshModel());
        vm.ActiveTool = LayoutEditorViewModel.Tool.Path;
        vm.CommitPathWidthText("2.9mm");

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerPressed(1000, 0, KeyModifiers.None);
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);

        var path = Assert.IsType<PathShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(2_900_000, path.Width); // 2.9mm at 1nm resolution
    }

    [Fact]
    public void TypedEntry_InvalidText_RevertsWithoutThrowing()
    {
        var vm = MakeVm(FreshModel());
        vm.CommitPathWidthText("10um"); // establish a known-good baseline
        var exception = Record.Exception(() => vm.CommitPathWidthText("2.9 furlongs"));
        Assert.Null(exception);

        vm.ActiveTool = LayoutEditorViewModel.Tool.Path;
        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerPressed(1000, 0, KeyModifiers.None);
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);

        var path = Assert.IsType<PathShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(10_000, path.Width); // unchanged from the "10um" baseline
    }

    // ── Gate 9: typed rect commit ─────────────────────────────────────────────

    [Fact]
    public void TypedRectCommit_WidthAndHeight_ProduceExactDimensions_RegardlessOfPointer()
    {
        var vm = MakeVm(FreshModel());
        vm.ActiveTool = LayoutEditorViewModel.Tool.Rect;

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerMoved(999_999, 12, true, KeyModifiers.None); // wildly different from the typed size below
        Assert.True(vm.IsDrawingRect);

        vm.CommitDrawWidthText("2.9mm");
        vm.CommitDrawHeightText("20mm");
        vm.CommitTypedRect();

        var rect = Assert.IsType<RectShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(2_900_000, rect.X2 - rect.X1);
        Assert.Equal(20_000_000, rect.Y2 - rect.Y1);
        Assert.False(vm.IsDrawingRect); // gesture ended
    }

    // ── Gate 10: Escape and Backspace ─────────────────────────────────────────

    [Fact]
    public void Escape_MidPolygon_LeavesModelUntouched_ClearsOverlay()
    {
        var vm = MakeVm(FreshModel());
        vm.ActiveTool = LayoutEditorViewModel.Tool.Polygon;

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerPressed(1000, 0, KeyModifiers.None);
        Assert.NotNull(vm.Overlay.InProgressPrimitive);

        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.Empty(vm.Model.Shapes);
        Assert.Null(vm.Overlay.InProgressPrimitive);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void Backspace_DropsExactlyOneVertex()
    {
        var vm = MakeVm(FreshModel());
        vm.ActiveTool = LayoutEditorViewModel.Tool.Polygon;

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerPressed(1000, 0, KeyModifiers.None);
        vm.OnPointerPressed(2000, 0, KeyModifiers.None);

        vm.OnKeyDown(Key.Back, KeyModifiers.None);

        // Close now with only the 2 remaining vertices + one more to reach the 3-vertex minimum.
        vm.OnPointerPressed(0, 1000, KeyModifiers.None);
        vm.OnPointerPressed(0, 1000, KeyModifiers.None, clickCount: 2);

        var poly = Assert.IsType<PolygonShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(new long[] { 0, 0, 1000, 0, 0, 1000 }, poly.Xy); // vertex at (2000,0) was dropped
    }

    // ── Gate 11: current layer ────────────────────────────────────────────────

    [Fact]
    public void NoTechnology_OffersFallbackLayers_AllUsable()
    {
        var vm = MakeVm(FreshModel());
        Assert.Equal(4, vm.AvailableLayers.Count);
        Assert.Equal(new LayerKey(1, 0), vm.CurrentLayerKey);

        vm.ActiveTool = LayoutEditorViewModel.Tool.Rect;
        vm.CurrentLayerItem = vm.AvailableLayers[2]; // 3/0
        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerMoved(1000, 1000, true, KeyModifiers.None);
        vm.OnPointerReleased(1000, 1000, KeyModifiers.None);

        var rect = Assert.IsType<RectShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(new LayerKey(3, 0), rect.Layer);
    }

    [Fact]
    public void TechnologyChange_RemovingCurrentLayer_FallsBackToFirstLayer_NoThrow()
    {
        var vm = MakeVm(FreshModel());
        var tech = new Technology
        {
            Name = "T", Layers =
            {
                new LayerDef { Key = new LayerKey(5, 0), Name = "M1", ZOrder = 0 },
                new LayerDef { Key = new LayerKey(6, 0), Name = "M2", ZOrder = 1 },
            },
        };
        vm.ApplyTechResolution(new TechResolution(tech, "/t.ctech", TechResolutionSource.WorkspaceDefault, []));
        vm.CurrentLayerKey = new LayerKey(6, 0);
        vm.CurrentLayerItem = vm.AvailableLayers.First(l => l.Key == new LayerKey(6, 0));

        var techWithoutM2 = new Technology
        {
            Name = "T", Layers = { new LayerDef { Key = new LayerKey(5, 0), Name = "M1", ZOrder = 0 } },
        };
        var exception = Record.Exception(() =>
            vm.ApplyTechResolution(new TechResolution(techWithoutM2, "/t.ctech", TechResolutionSource.WorkspaceDefault, [])));

        Assert.Null(exception);
        Assert.Equal(new LayerKey(5, 0), vm.CurrentLayerKey);
    }

    // ── Gate 12: dirty and save ───────────────────────────────────────────────

    [Fact]
    public void Drawing_DirtiesDocument_UndoBackToSaved_ClearsDirty()
    {
        var model = FreshModel();
        var vm = MakeVm(model);
        var tmp = Path.GetTempFileName();
        try
        {
            vm.PerformSave(tmp); // establish a clean, saved baseline (empty layout)
            Assert.False(vm.IsDirty);

            vm.ActiveTool = LayoutEditorViewModel.Tool.Rect;
            vm.OnPointerPressed(0, 0, KeyModifiers.None);
            vm.OnPointerMoved(1000, 1000, true, KeyModifiers.None);
            vm.OnPointerReleased(1000, 1000, KeyModifiers.None);
            Assert.True(vm.IsDirty);

            vm.UndoRedo.Undo();
            Assert.False(vm.IsDirty); // back to the saved (empty) baseline
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void SaveAndReload_RoundTripsEveryDrawnShape()
    {
        var model = FreshModel();
        var vm = MakeVm(model);
        vm.ActiveTool = LayoutEditorViewModel.Tool.Rect;
        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerMoved(1000, 2000, true, KeyModifiers.None);
        vm.OnPointerReleased(1000, 2000, KeyModifiers.None);

        vm.ActiveTool = LayoutEditorViewModel.Tool.Circle;
        vm.OnPointerPressed(5000, 5000, KeyModifiers.None);
        vm.OnPointerMoved(5000, 6000, true, KeyModifiers.None);
        vm.OnPointerReleased(5000, 6000, KeyModifiers.None);

        var tmp = Path.GetTempFileName();
        try
        {
            vm.PerformSave(tmp);
            var reloaded = LayoutPersistence.LoadFromFile(tmp);
            Assert.Equal(2, reloaded.Shapes.Count);
            Assert.IsType<RectShape>(reloaded.Shapes[0]);
            Assert.IsType<CircleShape>(reloaded.Shapes[1]);
        }
        finally { File.Delete(tmp); }
    }

    // ── Regression: drawing must repaint the canvas / clear the empty-layout placeholder ──
    // Bug: the view's IsEmpty-bound placeholder (drawn on top of LayoutCanvas) never hid itself
    // after the first shape was drawn, because nothing raised PropertyChanged for IsEmpty/
    // ShapeCountText/InstanceCountText/ExtentText when Model.Changed fired -- the shape really was
    // in the model and really was rendered, just invisible underneath a stale placeholder.

    [Fact]
    public void DrawingAShape_RaisesPropertyChanged_ForIsEmptyAndMetadataBarCounts()
    {
        var vm = MakeVm(FreshModel());
        Assert.True(vm.IsEmpty);

        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is not null) raised.Add(e.PropertyName); };

        vm.ActiveTool = LayoutEditorViewModel.Tool.Rect;
        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerMoved(2000, 2000, true, KeyModifiers.None);
        vm.OnPointerReleased(2000, 2000, KeyModifiers.None);

        Assert.False(vm.IsEmpty);
        Assert.Contains(nameof(LayoutEditorViewModel.IsEmpty), raised);
        Assert.Contains(nameof(LayoutEditorViewModel.ShapeCountText), raised);
        Assert.Contains(nameof(LayoutEditorViewModel.InstanceCountText), raised);
        Assert.Contains(nameof(LayoutEditorViewModel.ExtentText), raised);
    }

    [Fact]
    public void UndoingTheOnlyShape_RaisesPropertyChanged_IsEmptyBecomesTrueAgain()
    {
        var vm = MakeVm(FreshModel());
        vm.ActiveTool = LayoutEditorViewModel.Tool.Rect;
        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerMoved(2000, 2000, true, KeyModifiers.None);
        vm.OnPointerReleased(2000, 2000, KeyModifiers.None);
        Assert.False(vm.IsEmpty);

        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is not null) raised.Add(e.PropertyName); };

        vm.UndoRedo.Undo();

        Assert.True(vm.IsEmpty);
        Assert.Contains(nameof(LayoutEditorViewModel.IsEmpty), raised);
    }
}
