using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// R-pch-12 — GRIP-LOCK and grip hover.
///
/// <para>The owner's report: pressing on the corner of a PCell that has draggable grips sometimes
/// moved the whole instance and sometimes edited a parameter, with no way to tell in advance which
/// one a press would produce. Three separate things caused that — the grips only exist once the
/// instance is selected, they occupy the same pixels as the body with only an unmarked four-pixel
/// radius between them, and a grip whose sensitivity cannot be measured used to fall through to a
/// move after a dead-on click. Alt-at-press answers all three by force, and hover answers the
/// question before the press is made.</para>
///
/// <para>Everything here is driven through the real gesture entry points, and every "the grip won"
/// assertion is paired with the same press WITHOUT Alt, so a passing test is never just the grip
/// radius being generous.</para>
/// </summary>
public sealed class PCellGripLockTests : IDisposable
{
    private readonly string _workspaceDir;

    public PCellGripLockTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crf-grip-lock-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        File.WriteAllText(Path.Combine(_workspaceDir, ".cws"), "{}");
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir)) Directory.Delete(_workspaceDir, recursive: true);
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    /// <summary>Deliberately TIGHT — a real canvas's four device pixels is a few DBU at a sane zoom,
    /// and the whole subject here is a press that misses that radius.</summary>
    private const long HitTol = 20_000;

    /// <summary>Grip-lock's own radius, standing in for the canvas's 24 device pixels.</summary>
    private const long LockTol = 900_000;

    private LayoutEditorViewModel PlaceMlin(long snapDbu = 1000)
    {
        var vm = new LayoutEditorViewModel(
            new LayoutView { DbuPerMicron = 1000, SnapDbu = snapDbu },
            Path.Combine(_workspaceDir, "Doc", "layout", "main.clay"));

        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(
            _workspaceDir, "MLIN", defaults, null, null, PCellLayerSelection.Default);

        vm.Model.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0,
        });
        vm.SelectInstance(0);
        return vm;
    }

    private static PCellHandleMarker Grip(LayoutEditorViewModel vm, string label, double dx, double dy)
        => Assert.Single(vm.Overlay.PCellHandles, h =>
               h.Label == label && Math.Abs(h.AxisDx - dx) < 1e-6 && Math.Abs(h.AxisDy - dy) < 1e-6);

    private static double Distance(PCellHandleMarker h, long x, long y)
        => Math.Sqrt((double)(h.X - x) * (h.X - x) + (double)(h.Y - y) * (h.Y - y));

    private static IReadOnlyDictionary<string, PCellValue> ParametersOf(LayoutEditorViewModel vm)
    {
        var res = CellLayoutResolver.Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir);
        Assert.Equal(CellLayoutState.Resolved, res.State);
        return res.View!.PCellOrigin!.Parameters;
    }

    /// <summary>
    /// A press point that is comfortably OUTSIDE the ordinary grip radius, inside the instance's own
    /// artwork (so a plain press there lands on the body and moves it), and unambiguously nearest to
    /// the "L" grip.
    ///
    /// <para>Derived from the cell's own geometry rather than written as a constant: the offset is a
    /// fraction of the distance to the next-nearest grip, so the "nearest grip wins" claim the tests
    /// make is true by construction on any MLIN default, not by a number that happened to work
    /// once.</para>
    /// </summary>
    private static (long X, long Y, PCellHandleMarker Grip) InboardOfTheLengthGrip(LayoutEditorViewModel vm)
    {
        var grip = Grip(vm, "L", 1, 0);
        double nearestOther = vm.Overlay.PCellHandles
            .Where(h => h.X != grip.X || h.Y != grip.Y)
            .Min(h => Distance(h, grip.X, grip.Y));

        // Far enough out to miss the ordinary radius by a wide margin, near enough to stay inside the
        // LOCK radius (an MLIN's default length is 10 mm, so a plain fraction of the distance to the
        // next grip lands 2 mm away — outside the lock radius, where the correct behaviour is to do
        // nothing and every test below would be asserting the wrong thing).
        long off = Math.Min((long)(nearestOther * 0.4), LockTol / 2);
        Assert.True(off > HitTol * 3,
            $"the fixture must offer a press point well outside the ordinary grip radius (off={off})");
        Assert.True(off < nearestOther / 2,
            $"the press point must stay unambiguously nearest to the L grip (off={off})");
        return (grip.X - off, grip.Y, grip);
    }

    private void Press(LayoutEditorViewModel vm, long x, long y, KeyModifiers mods)
        => vm.OnPointerPressed(x, y, mods, hitTolDbu: HitTol, snapTolDbu: 0, gripLockTolDbu: LockTol);

    private void Move(LayoutEditorViewModel vm, long x, long y, bool leftDown, KeyModifiers mods,
                      long snapTolDbu = 0)
        => vm.OnPointerMoved(x, y, leftDown, mods, hitTolDbu: HitTol, pixelDbu: 0,
                             snapTolDbu: snapTolDbu, gripLockTolDbu: LockTol);

    // ── The gesture itself ────────────────────────────────────────────────────

    [Fact]
    public void AltPress_NearButNotOnAGrip_EditsTheParameter_AndNeverMovesTheInstance()
    {
        var vm = PlaceMlin();
        double lengthBefore = ParametersOf(vm).Real("L", 0);
        var (px, py, grip) = InboardOfTheLengthGrip(vm);

        Press(vm, px, py, KeyModifiers.Alt);
        Move(vm, grip.X + 500_000, grip.Y, leftDown: true, KeyModifiers.Alt);
        vm.OnPointerReleased(grip.X + 500_000, grip.Y, KeyModifiers.Alt);

        Assert.True(ParametersOf(vm).Real("L", 0) > lengthBefore, "the grip should have lengthened the line");
        Assert.Equal(0, vm.Model.Instances[0].X);
        Assert.Equal(0, vm.Model.Instances[0].Y);
    }

    /// <summary>
    /// The non-vacuity partner, and the whole reason grip-lock exists: the IDENTICAL press without Alt
    /// lands on the instance body and moves the cell. If this ever starts editing the parameter too,
    /// the test above is proving nothing.
    /// </summary>
    [Fact]
    public void ThatSamePressWithoutAlt_MovesTheInstance_AndLeavesTheParameterAlone()
    {
        var vm = PlaceMlin();
        double lengthBefore = ParametersOf(vm).Real("L", 0);
        var (px, py, grip) = InboardOfTheLengthGrip(vm);

        Press(vm, px, py, KeyModifiers.None);
        Move(vm, grip.X + 500_000, grip.Y, leftDown: true, KeyModifiers.None);
        vm.OnPointerReleased(grip.X + 500_000, grip.Y, KeyModifiers.None);

        Assert.NotEqual(0, vm.Model.Instances[0].X);
        Assert.Equal(lengthBefore, ParametersOf(vm).Real("L", 0), precision: 12);
    }

    /// <summary>
    /// Bounded, by the owner's call. Outside the lock radius the press does NOTHING — it does not fall
    /// through to a move, a marquee, or a deselect. "Alt means I am only talking to grips" is the
    /// promise; reaching across the canvas for a grip the user was not looking at is not part of it.
    /// </summary>
    [Fact]
    public void AltPress_BeyondTheLockRadius_DoesNothingAtAll()
    {
        var vm = PlaceMlin();
        double lengthBefore = ParametersOf(vm).Real("L", 0);
        var grip = Grip(vm, "L", 1, 0);
        long far = grip.X + LockTol * 4;

        Press(vm, far, 0, KeyModifiers.Alt);
        Move(vm, far + 500_000, 0, leftDown: true, KeyModifiers.Alt);
        vm.OnPointerReleased(far + 500_000, 0, KeyModifiers.Alt);

        Assert.Equal(lengthBefore, ParametersOf(vm).Real("L", 0), precision: 12);
        Assert.Equal(0, vm.Model.Instances[0].X);
        Assert.Single(vm.Overlay.PCellHandles, h => h.Label == "L" && h.AxisDx > 0);  // still selected
    }

    [Fact]
    public void AltPress_ClaimsTheNEARESTGrip_NotTheFirstDeclared()
    {
        var vm = PlaceMlin();
        var wide = Grip(vm, "W", 0, 1);          // the top edge — a different axis from "L"

        Press(vm, wide.X, wide.Y - 1, KeyModifiers.Alt);

        Assert.Equal("W", vm.Overlay.PCellHandles.Single(h => h.Active).Label);
        vm.OnPointerReleased(wide.X, wide.Y - 1, KeyModifiers.Alt);
    }

    // ── Alt is SPENT by the press (the owner's own condition on this design) ──

    /// <summary>
    /// The point of the whole carve-out. Alt everywhere else in this editor means "suspend snap"; if
    /// it kept that meaning inside a locked drag, then holding Alt to guarantee the grip would
    /// silently cost the geometry snapping the grip was grabbed in order to use — which is the exact
    /// workflow grip-lock was asked for.
    ///
    /// <para>The target corner sits deliberately off-grid (10 µm steps against a corner at 3,333 µm),
    /// so landing on it exactly is only possible if geometry snap actually ran.</para>
    /// </summary>
    [Fact]
    public void ALockedDrag_StillSnapsToGeometry_WithAltHeldThroughout()
    {
        const long snap = 10_000;
        const long targetX = 3_333_000;

        var vm = PlaceMlin(snap);
        vm.Model.Shapes.Add(new RectShape
        {
            Layer = new LayerKey(1, 0), X1 = targetX, Y1 = 0, X2 = targetX + 500_000, Y2 = 500_000,
        });

        var (px, py, grip) = InboardOfTheLengthGrip(vm);
        Press(vm, px, py, KeyModifiers.Alt);
        Move(vm, targetX - 2_000, grip.Y, leftDown: true, KeyModifiers.Alt, snapTolDbu: 50_000);
        vm.OnPointerReleased(targetX - 2_000, grip.Y, KeyModifiers.Alt);

        Assert.Equal(targetX, Grip(vm, "L", 1, 0).X);
        Assert.NotEqual(0, targetX % snap);      // non-vacuous: the target really is off-grid
    }

    /// <summary>
    /// The carve-out is scoped to the gesture Alt STARTED, and nothing wider: a drag begun without Alt
    /// still has Alt meaning suspend-snap, exactly as every other drag in this editor does.
    /// </summary>
    [Fact]
    public void AnUnlockedGripDrag_WithAltHeldDuringTheMove_StillSuspendsSnap()
    {
        const long snap = 10_000;
        const long targetX = 3_333_000;

        var vm = PlaceMlin(snap);
        vm.Model.Shapes.Add(new RectShape
        {
            Layer = new LayerKey(1, 0), X1 = targetX, Y1 = 0, X2 = targetX + 500_000, Y2 = 500_000,
        });

        var grip = Grip(vm, "L", 1, 0);
        Press(vm, grip.X, grip.Y, KeyModifiers.None);
        Move(vm, targetX - 2_000, grip.Y, leftDown: true, KeyModifiers.Alt, snapTolDbu: 50_000);
        vm.OnPointerReleased(targetX - 2_000, grip.Y, KeyModifiers.Alt);

        Assert.NotEqual(targetX, Grip(vm, "L", 1, 0).X);
    }

    [Fact]
    public void ALockedDrag_ReportsItselfAsLocked_AndAnOrdinaryOneDoesNot()
    {
        var vm = PlaceMlin();
        var (px, py, _) = InboardOfTheLengthGrip(vm);

        Press(vm, px, py, KeyModifiers.Alt);
        Assert.True(vm.PCellHandleDragIsLocked);
        vm.OnPointerReleased(px, py, KeyModifiers.Alt);

        var grip = Grip(vm, "L", 1, 0);
        Press(vm, grip.X, grip.Y, KeyModifiers.None);
        Assert.False(vm.PCellHandleDragIsLocked);
        vm.OnPointerReleased(grip.X, grip.Y, KeyModifiers.None);
    }

    // ── Alt's other meanings are untouched where there are no grips ───────────

    /// <summary>
    /// Grip-lock is gated on the selection actually HAVING grips, which is what keeps Alt+click
    /// overlap cycling, Alt-suspend-snap and Alt-scale-about-centre working everywhere else. With
    /// nothing selected there are no grips, so an Alt press is an ordinary press.
    /// </summary>
    [Fact]
    public void AltPress_WithNoGripsOnTheSelection_IsAnOrdinaryPress()
    {
        var vm = PlaceMlin();
        vm.DeselectAllCommand.Execute(null);
        vm.Model.Shapes.Add(new RectShape
        {
            Layer = new LayerKey(1, 0), X1 = 10_000_000, Y1 = 0, X2 = 11_000_000, Y2 = 1_000_000,
        });

        Press(vm, 10_500_000, 500_000, KeyModifiers.Alt);
        Move(vm, 10_500_000 + 400_000, 500_000, leftDown: true, KeyModifiers.Alt);
        vm.OnPointerReleased(10_500_000 + 400_000, 500_000, KeyModifiers.Alt);

        var rect = Assert.IsType<RectShape>(vm.Model.Shapes[0]);
        Assert.NotEqual(10_000_000, rect.X1);      // the ordinary Alt+drag moved it
    }

    // ── Hover: seeing which gesture you are about to get ─────────────────────

    [Fact]
    public void HoveringAGrip_MarksIt_AndReportsAnAxisCursor()
    {
        var vm = PlaceMlin();
        var grip = Grip(vm, "L", 1, 0);

        Move(vm, grip.X, grip.Y, leftDown: false, KeyModifiers.None);

        Assert.True(vm.HoveredPCellHandleIndex >= 0);
        Assert.Equal(PCellGripCursor.EastWest, vm.HoveredPCellHandleCursor);
        Assert.Single(vm.Overlay.PCellHandles, h => h.Hovered);
    }

    /// <summary>An MLIN's width grip travels across the line, not along it — so it must not report the
    /// same cursor as the length grip. Without this, "reports a cursor" could pass on a constant.</summary>
    [Fact]
    public void HoveringTheWidthGrip_ReportsTheOtherAxis()
    {
        var vm = PlaceMlin();
        var grip = Grip(vm, "W", 0, 1);

        Move(vm, grip.X, grip.Y, leftDown: false, KeyModifiers.None);

        Assert.Equal(PCellGripCursor.NorthSouth, vm.HoveredPCellHandleCursor);
    }

    [Fact]
    public void HoveringAwayFromEveryGrip_ClearsTheMark()
    {
        var vm = PlaceMlin();
        var grip = Grip(vm, "L", 1, 0);

        Move(vm, grip.X, grip.Y, leftDown: false, KeyModifiers.None);
        Move(vm, grip.X + LockTol * 4, grip.Y, leftDown: false, KeyModifiers.None);

        Assert.Equal(-1, vm.HoveredPCellHandleIndex);
        Assert.Equal(PCellGripCursor.None, vm.HoveredPCellHandleCursor);
        Assert.DoesNotContain(vm.Overlay.PCellHandles, h => h.Hovered);
    }

    /// <summary>
    /// The highlight has to promise exactly what the press will deliver: with Alt held, a press at
    /// this distance WOULD claim the grip (see the first test), so the hover at the same point must
    /// say so. Without Alt the same point highlights nothing, because the same point presses nothing.
    /// </summary>
    [Fact]
    public void HoverUnderAlt_UsesTheLockRadius_AndOtherwiseDoesNot()
    {
        var vm = PlaceMlin();
        var (px, py, _) = InboardOfTheLengthGrip(vm);

        Move(vm, px, py, leftDown: false, KeyModifiers.None);
        Assert.Equal(-1, vm.HoveredPCellHandleIndex);

        Move(vm, px, py, leftDown: false, KeyModifiers.Alt);
        Assert.True(vm.HoveredPCellHandleIndex >= 0);
    }

    [Fact]
    public void AltOverAPCellWithGrips_ArmsEveryGrip_AndReleasingItDisarms()
    {
        var vm = PlaceMlin();

        vm.SetGripLockArmed(true);
        Assert.True(vm.GripLockArmed);
        Assert.All(vm.Overlay.PCellHandles, h => Assert.True(h.Armed));

        vm.SetGripLockArmed(false);
        Assert.False(vm.GripLockArmed);
        Assert.All(vm.Overlay.PCellHandles, h => Assert.False(h.Armed));
    }

    /// <summary>Arming is refused when there is nothing to arm — otherwise the editor would claim a
    /// mode is on that the next press would not honour.</summary>
    [Fact]
    public void ArmingIsRefused_WhenTheSelectionHasNoGrips()
    {
        var vm = PlaceMlin();
        vm.DeselectAllCommand.Execute(null);

        vm.SetGripLockArmed(true);

        Assert.False(vm.GripLockArmed);
    }

    /// <summary>
    /// The held-key latch this editor has already been bitten by once (Space-to-pan): hold Alt, click
    /// a toolbar button, and the key-up never reaches the canvas. Left armed, every later press would
    /// claim a grip instead of moving the instance, with nothing on screen explaining it.
    /// </summary>
    [Fact]
    public void LosingFocus_DropsTheArmedLatch()
    {
        var vm = PlaceMlin();
        vm.SetGripLockArmed(true);

        vm.ClearGripLockArmed();

        Assert.False(vm.GripLockArmed);
        Assert.Equal(-1, vm.HoveredPCellHandleIndex);
    }

    [Fact]
    public void StartingAGripDrag_DropsTheArmedHighlight()
    {
        var vm = PlaceMlin();
        var (px, py, _) = InboardOfTheLengthGrip(vm);
        vm.SetGripLockArmed(true);

        Press(vm, px, py, KeyModifiers.Alt);

        Assert.False(vm.GripLockArmed);
        Assert.DoesNotContain(vm.Overlay.PCellHandles, h => h.Armed);
        vm.OnPointerReleased(px, py, KeyModifiers.Alt);
    }
}
