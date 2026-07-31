using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Layout Editor rotate (owner request, 2026-07-30): toolbar buttons + R / Shift+R, same controls
//  and sense as the Schematic Editor.
//
//  THE semantic difference, owner's explicit call: a multi-selection turns as ONE RIGID BODY. The
//  Schematic Editor rotates each selected component about its own origin, which is right for a
//  connectivity diagram; layout is physical artwork, where the relative positions of the selected
//  shapes ARE the design.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class LayoutRotateTests : IDisposable
{
    private readonly string _dir;

    public LayoutRotateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crfRotate_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        CellLayoutResolver.InvalidateUnder(_dir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_dir);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static LayoutView NewView() =>
        new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private static RectShape Rect(long x1, long y1, long x2, long y2) =>
        new() { Layer = new LayerKey(1, 0), X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    private static Bbox BboxOfAll(LayoutView v)
    {
        var b = Bbox.Empty;
        foreach (var s in v.Shapes) b = b.Union(LayoutGeometry.BboxOf(s));
        return b;
    }

    // ── The rigid-body rule ──────────────────────────────────────────────────

    [Fact]
    public void MultiSelection_RotatesAsOneRigidBody_RelativeLayoutPreserved()
    {
        var view = NewView();
        // Two clearly different shapes, far apart: a wide bar and a tall bar.
        view.Shapes.Add(Rect(0, 0, 8000, 1000));         // wide, at the origin
        view.Shapes.Add(Rect(20000, 0, 21000, 6000));    // tall, well to the right
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        var beforeA = LayoutGeometry.BboxOf(view.Shapes[0]);
        var beforeB = LayoutGeometry.BboxOf(view.Shapes[1]);
        var beforeAll = BboxOfAll(view);

        vm.SelectAllCommand.Execute(null);
        vm.RotateSelection(clockwise: false);

        var afterA = LayoutGeometry.BboxOf(view.Shapes[0]);
        var afterB = LayoutGeometry.BboxOf(view.Shapes[1]);

        // Each shape's own extent transposes (a 90° turn swaps width and height)…
        Assert.Equal(beforeA.MaxX - beforeA.MinX, afterA.MaxY - afterA.MinY);
        Assert.Equal(beforeA.MaxY - beforeA.MinY, afterA.MaxX - afterA.MinX);

        // …and — the point of the rule — the two stay the same distance apart, now along the
        // perpendicular axis. Rotating each about its OWN centre would leave them both where they
        // were, which is exactly the behaviour this test exists to forbid.
        long beforeSeparationX = beforeB.MinX - beforeA.MinX;
        long afterSeparationY  = afterB.MinY - afterA.MinY;
        Assert.Equal(beforeSeparationX, afterSeparationY);

        // The selection as a whole stays put (its extent transposes about its own centre).
        var afterAll = BboxOfAll(view);
        Assert.Equal(beforeAll.MaxX - beforeAll.MinX, afterAll.MaxY - afterAll.MinY);
        Assert.Equal(beforeAll.MaxY - beforeAll.MinY, afterAll.MaxX - afterAll.MinX);
    }

    [Fact]
    public void FourRotations_ReturnToTheStartingGeometry()
    {
        var view = NewView();
        view.Shapes.Add(Rect(1000, 2000, 9000, 4000));
        view.Shapes.Add(Rect(12000, 2000, 13000, 8000));
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        var before = LayoutPersistence.Serialize(view);

        vm.SelectAllCommand.Execute(null);
        for (int i = 0; i < 4; i++) vm.RotateSelection(clockwise: false);

        // Exactness matters: a rotation that rounded per-vertex would drift and never close.
        Assert.Equal(before, LayoutPersistence.Serialize(view));
    }

    [Fact]
    public void ClockwiseAndCounterClockwise_AreInverses()
    {
        var view = NewView();
        view.Shapes.Add(Rect(0, 0, 5000, 2000));
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        var before = LayoutPersistence.Serialize(view);

        vm.SelectAllCommand.Execute(null);
        vm.RotateSelection(clockwise: false);
        vm.RotateSelection(clockwise: true);

        Assert.Equal(before, LayoutPersistence.Serialize(view));
    }

    // ── Undo, and one gesture = one entry ────────────────────────────────────

    [Fact]
    public void Rotate_IsOneUndoEntry_ForTheWholeSelection()
    {
        var view = NewView();
        view.Shapes.Add(Rect(0, 0, 4000, 1000));
        view.Shapes.Add(Rect(6000, 0, 7000, 3000));
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        var before = LayoutPersistence.Serialize(view);

        vm.SelectAllCommand.Execute(null);
        vm.RotateSelection();

        Assert.NotEqual(before, LayoutPersistence.Serialize(view));

        vm.UndoCommand.Execute(null);
        Assert.Equal(before, LayoutPersistence.Serialize(view));   // ONE undo, not one per shape
    }

    // ── Per-shape details ────────────────────────────────────────────────────

    [Fact]
    public void RectCorners_StayNormalised_NeverInsideOut()
    {
        var view = NewView();
        view.Shapes.Add(Rect(0, 0, 5000, 2000));
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        vm.SelectAllCommand.Execute(null);
        vm.RotateSelection();

        var r = Assert.IsType<RectShape>(view.Shapes[0]);
        Assert.True(r.X1 <= r.X2, $"X1({r.X1}) must not exceed X2({r.X2})");
        Assert.True(r.Y1 <= r.Y2, $"Y1({r.Y1}) must not exceed Y2({r.Y2})");
    }

    [Fact]
    public void CircleRadius_IsUnchanged_RotationHasNoMagnitudeFactor()
    {
        var view = NewView();
        view.Shapes.Add(new CircleShape { Layer = new LayerKey(1, 0), Cx = 3000, Cy = 4000, R = 1234 });
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        vm.SelectAllCommand.Execute(null);
        vm.RotateSelection();

        Assert.Equal(1234, Assert.IsType<CircleShape>(view.Shapes[0]).R);
    }

    [Fact]
    public void ArcBulge_IsUnchanged_OnlyAMirrorFlipsIt()
    {
        var view = NewView();
        var curve = new CurveShape
        {
            Layer = new LayerKey(1, 0),
            Xy    = [0, 0, 4000, 0, 4000, 3000],
            Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.4142 },
                     new LayoutEdge { Kind = EdgeKind.Line },
                     new LayoutEdge { Kind = EdgeKind.Line }],
        };
        view.Shapes.Add(curve);
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        vm.SelectAllCommand.Execute(null);
        vm.RotateSelection();

        // Bulge is tan(sweep/4), measured against the edge's own chord — a rotation carries it
        // unchanged. Flipping it here would silently invert the curve's handedness.
        Assert.Equal(0.4142, Assert.IsType<CurveShape>(view.Shapes[0]).Edges![0].Bulge);
    }

    [Fact]
    public void LabelGlyphs_TurnWithTheGeometry_NotJustTheAnchor()
    {
        var view = NewView();
        view.Shapes.Add(new LabelShape
        {
            Layer = new LayerKey(1, 0), Text = "N1", Height = 5000,
            X = 1000, Y = 2000, Rotation = LayoutRotation.R0,
        });
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        vm.SelectAllCommand.Execute(null);
        vm.RotateSelection(clockwise: false);

        Assert.Equal(LayoutRotation.R90, Assert.IsType<LabelShape>(view.Shapes[0]).Rotation);
    }

    // ── Availability ─────────────────────────────────────────────────────────

    [Fact]
    public void Rotate_IsDisabledWithAReason_WhenNothingIsSelected()
    {
        var vm = new LayoutEditorViewModel(NewView()) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        var availability = vm.RotateAvailability;
        Assert.False(availability.CanExecute);
        Assert.False(string.IsNullOrWhiteSpace(availability.DisabledReason));
    }

    [Fact]
    public void EmptySelection_Rotate_IsANoOp_AndPushesNoUndoEntry()
    {
        var view = NewView();
        view.Shapes.Add(Rect(0, 0, 1000, 1000));
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        var before = LayoutPersistence.Serialize(view);
        vm.RotateSelection();                       // nothing selected

        Assert.Equal(before, LayoutPersistence.Serialize(view));
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    // ── Mirror ───────────────────────────────────────────────────────────────

    [Fact]
    public void MirrorHorizontal_ReversesLeftToRightOrder_ButKeepsSpacing()
    {
        var view = NewView();
        view.Shapes.Add(Rect(0, 0, 1000, 1000));          // left
        view.Shapes.Add(Rect(9000, 0, 10000, 1000));      // right
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        var beforeAll = BboxOfAll(view);

        vm.SelectAllCommand.Execute(null);
        vm.MirrorSelection(horizontal: true);

        var a = LayoutGeometry.BboxOf(view.Shapes[0]);
        var b = LayoutGeometry.BboxOf(view.Shapes[1]);

        // Rigid body again: the pair swaps ends but keeps its separation and overall extent.
        Assert.True(a.MinX > b.MinX, "the left shape must end up on the right");
        Assert.Equal(beforeAll, BboxOfAll(view));
    }

    [Fact]
    public void MirrorTwice_IsIdentity()
    {
        var view = NewView();
        view.Shapes.Add(Rect(1000, 2000, 6000, 3000));
        view.Shapes.Add(Rect(8000, 2000, 9000, 7000));
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        var before = LayoutPersistence.Serialize(view);

        vm.SelectAllCommand.Execute(null);
        vm.MirrorSelection(horizontal: true);
        vm.MirrorSelection(horizontal: true);

        Assert.Equal(before, LayoutPersistence.Serialize(view));
    }

    [Fact]
    public void MirrorVertical_FlipsTopToBottom()
    {
        var view = NewView();
        view.Shapes.Add(Rect(0, 0, 1000, 1000));          // bottom
        view.Shapes.Add(Rect(0, 9000, 1000, 10000));      // top
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        var beforeAll = BboxOfAll(view);

        vm.SelectAllCommand.Execute(null);
        vm.MirrorSelection(horizontal: false);

        var a = LayoutGeometry.BboxOf(view.Shapes[0]);
        var b = LayoutGeometry.BboxOf(view.Shapes[1]);

        Assert.True(a.MinY > b.MinY, "the bottom shape must end up on top");
        Assert.Equal(beforeAll, BboxOfAll(view));
    }

    [Fact]
    public void Mirror_FlipsArcBulge_UnlikeRotation()
    {
        var view = NewView();
        view.Shapes.Add(new CurveShape
        {
            Layer = new LayerKey(1, 0),
            Xy    = [0, 0, 4000, 0, 4000, 3000],
            Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.4142 },
                     new LayoutEdge { Kind = EdgeKind.Line },
                     new LayoutEdge { Kind = EdgeKind.Line }],
        });
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        vm.SelectAllCommand.Execute(null);
        vm.MirrorSelection(horizontal: true);

        // A reflection reverses which side of the chord the arc bulges toward. Failing to flip is
        // SILENT — the shape still draws, just with its curvature inverted.
        Assert.Equal(-0.4142, Assert.IsType<CurveShape>(view.Shapes[0]).Edges![0].Bulge);
    }

    [Fact]
    public void Mirror_IsOneUndoEntry()
    {
        var view = NewView();
        view.Shapes.Add(Rect(0, 0, 2000, 1000));
        view.Shapes.Add(Rect(5000, 0, 6000, 4000));
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        var before = LayoutPersistence.Serialize(view);

        vm.SelectAllCommand.Execute(null);
        vm.MirrorSelection();

        Assert.NotEqual(before, LayoutPersistence.Serialize(view));
        vm.UndoCommand.Execute(null);
        Assert.Equal(before, LayoutPersistence.Serialize(view));
    }

    [Fact]
    public void Mirror_IsDisabledWithAReason_WhenNothingIsSelected()
    {
        var vm = new LayoutEditorViewModel(NewView()) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Assert.False(vm.MirrorAvailability.CanExecute);
        Assert.False(string.IsNullOrWhiteSpace(vm.MirrorAvailability.DisabledReason));
    }
}
