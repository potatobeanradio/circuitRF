using Avalonia.Input;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  AIMING A PART BEFORE YOU PUT IT DOWN.
//
//  Reported from the field, 2026-09-21: a footprint could not be turned once a part type had been
//  picked and the ghost was on the cursor — it had to be dropped, rotated and placed again. Both
//  halves of that report were true: the placement ghost carried no angle at all, and the editor's own
//  R branch is gated on the Select tool, which the Instance tool is not — so the key reached nothing
//  whichever way it was fixed.
//
//  What is pinned here is the GESTURE and its RESULT: the ghost turns, the committed instance
//  carries the angle it was turned to, one undo entry covers the whole placement (the rotation is
//  not an edit — nothing exists yet to edit), and Escape still takes the angle away with the
//  placement. The rendering is not asserted: LayoutInstanceTransform already reads Rot/MirrorX on
//  every path that draws one, and a pixel test here would be testing that instead.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class LayoutPlacementRotationTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);

    public LayoutPlacementRotationTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfPlaceRot_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir)) Directory.Delete(_workspaceDir, recursive: true);
    }

    /// <summary>
    /// <b>R turns the armed ghost, and the part lands at the angle it was turned to.</b>
    /// </summary>
    /// <remarks>
    /// A rectangular leaf cell rather than a square one, because the whole point of rotating a land
    /// pattern is that its two lands are not interchangeable — the ghost's own bounding box is what
    /// says the turn actually happened rather than merely being recorded.
    /// </remarks>
    [Fact]
    public void RotatingTheArmedGhost_TurnsIt_AndTheCommittedInstanceCarriesTheAngle()
    {
        var vm = MakeVm();

        vm.BeginInstancePlacement("../../Leaf");
        Assert.True(vm.IsInstancePlacementActive);
        Assert.Equal(0.0, vm.InstancePlacementRotationDegrees);

        vm.OnKeyDown(Key.R, KeyModifiers.None);
        Assert.Equal(90.0, vm.InstancePlacementRotationDegrees);

        // Shift+R is the other way, and R ADVANCES rather than snapping (R-L3d-11) — three more
        // counter-clockwise from 90 is 360, which normalizes to 0 on the instance itself.
        vm.OnKeyDown(Key.R, KeyModifiers.Shift);
        Assert.Equal(0.0, vm.InstancePlacementRotationDegrees);

        vm.OnKeyDown(Key.R, KeyModifiers.None);
        vm.OnPointerPressed(5000, 6000, KeyModifiers.None);

        var placed = Assert.Single(vm.Model.Instances);
        Assert.Equal(90.0, placed.RotationDegrees);
        Assert.Equal(LayoutRotation.R90, placed.Rot);
        Assert.False(placed.MirrorX);

        // ONE undo entry for the whole placement: the rotation was part of the gesture, not an edit.
        vm.UndoRedo.Undo();
        Assert.Empty(vm.Model.Instances);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    /// <summary>
    /// <b>M flips it, and the flip negates the angle as well as toggling the flag.</b>
    /// </summary>
    /// <remarks>
    /// An instance transform is mirror-then-rotate, so a world reflection gives
    /// <c>M ∘ Rot(θ) ∘ Mx^m = Rot(−θ) ∘ Mx^(m+1)</c> — <see cref="LayoutEditorViewModel.MirrorSelection"/>'s
    /// own derivation. Toggling the flag on its own silently mis-places every rotated placement,
    /// which is why the angle is asserted here and not just the flag.
    /// </remarks>
    [Fact]
    public void MirroringTheArmedGhost_NegatesTheAngleAsWellAsTogglingTheFlag()
    {
        var vm = MakeVm();

        vm.BeginInstancePlacement("../../Leaf");
        vm.OnKeyDown(Key.R, KeyModifiers.None);          // 90
        vm.OnKeyDown(Key.M, KeyModifiers.None);          // horizontal flip: Rot(-90), mirrored

        Assert.True(vm.InstancePlacementMirrorX);
        Assert.Equal(270.0, vm.InstancePlacementRotationDegrees);

        vm.OnPointerPressed(0, 0, KeyModifiers.None);

        var placed = Assert.Single(vm.Model.Instances);
        Assert.True(placed.MirrorX);
        Assert.Equal(270.0, placed.RotationDegrees);
    }

    /// <summary>
    /// <b>A fresh arming starts square, and Escape takes the angle away with the placement.</b>
    /// </summary>
    /// <remarks>
    /// The tool stays armed after a commit so a row of identical parts keeps the angle it was aimed
    /// at — that is the first assertion. Picking a DIFFERENT part is a new decision, and an angle
    /// carried into it would be a surprise with nothing on screen to explain it.
    /// </remarks>
    [Fact]
    public void TheAngleSurvivesTheNextPlacement_AndAFreshArmingStartsSquare()
    {
        var vm = MakeVm();

        vm.BeginInstancePlacement("../../Leaf");
        vm.OnKeyDown(Key.R, KeyModifiers.None);
        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerPressed(20000, 0, KeyModifiers.None);

        Assert.Equal(2, vm.Model.Instances.Count);
        Assert.All(vm.Model.Instances, i => Assert.Equal(90.0, i.RotationDegrees));

        vm.BeginInstancePlacement("../../Leaf");
        Assert.Equal(0.0, vm.InstancePlacementRotationDegrees);

        vm.OnKeyDown(Key.R, KeyModifiers.None);
        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.False(vm.IsInstancePlacementActive);
        Assert.Equal(0.0, vm.InstancePlacementRotationDegrees);
        Assert.Equal(2, vm.Model.Instances.Count);
    }

    // ── fixture ─────────────────────────────────────────────────────────────────────────────────

    private LayoutEditorViewModel MakeVm()
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, "Leaf");
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var leaf = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

        // Rectangular on purpose — see the first test's own note.
        leaf.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 4000, Y2 = 1000 });
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "main.clay"), leaf);

        string clayPath = Path.Combine(_workspaceDir, "Root", "layout", "root.clay");
        return new LayoutEditorViewModel(
            new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath);
    }
}
