using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// Two owner reports about dragging a microstrip's grips: the grips did not snap to nearby GEOMETRY
/// (only to the grid), and shortening an MKlopf's L by its grip could drag the taper through itself.
/// </summary>
public sealed class PCellGripSnapAndOverlapTests : IDisposable
{
    private readonly string _workspaceDir;

    public PCellGripSnapAndOverlapTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crf-grip-snap-" + Guid.NewGuid().ToString("N")[..8]);
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

    private LayoutEditorViewModel MakeVm(long snapDbu)
        => new(new LayoutView { DbuPerMicron = 1000, SnapDbu = snapDbu },
               Path.Combine(_workspaceDir, "Doc", "layout", "main.clay"));

    private LayoutEditorViewModel Place(
        string generatorId, IReadOnlyDictionary<string, PCellValue> parameters, long snapDbu)
    {
        var vm = MakeVm(snapDbu);
        string cellDir = GeneratedCellStore.GetOrCreate(
            _workspaceDir, generatorId, parameters, null, null, PCellLayerSelection.Default);

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

    private static IReadOnlyDictionary<string, PCellValue> ParametersOf(LayoutEditorViewModel vm)
    {
        var res = CellLayoutResolver.Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir);
        Assert.Equal(CellLayoutState.Resolved, res.State);
        return res.View!.PCellOrigin!.Parameters;
    }

    /// <summary>
    /// A whole drag as the canvas performs one. <c>snapTolDbu</c> is what arms the geometry-snap query
    /// at all — it defaults to 0 on the view model (meaning "no snap"), and the canvas computes it per
    /// call from the live zoom, so a test that leaves it out is testing the grid-only path.
    /// <para/>
    /// <paramref name="steps"/> matters for the overlap guard: a real pointer emits many small moves,
    /// and the guard stops the grip at the last move that did not fold. One giant jump straight past
    /// the boundary has no such intermediate value, so the grip holds its start value until the cursor
    /// comes back into valid territory.
    /// </summary>
    private static void Drag(LayoutEditorViewModel vm, PCellHandleMarker grip, long toX, long toY,
                             KeyModifiers mods = KeyModifiers.None, long tolDbu = 200_000,
                             long snapTolDbu = 50_000, int steps = 1)
    {
        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None,
                            hitTolDbu: tolDbu, snapTolDbu: snapTolDbu);

        for (int i = 1; i <= steps; i++)
        {
            long x = grip.X + (long)((toX - grip.X) * (double)i / steps);
            long y = grip.Y + (long)((toY - grip.Y) * (double)i / steps);
            vm.OnPointerMoved(x, y, leftDown: true, mods, hitTolDbu: tolDbu, snapTolDbu: snapTolDbu);
        }

        vm.OnPointerReleased(toX, toY, mods);
    }

    /// <summary>Right edge of the placed MLIN's own artwork, in world DBU — where the "L" grip sits.</summary>
    private static long RightEdgeOf(LayoutEditorViewModel vm)
        => Grip(vm, "L", 1, 0).X;

    // ── Geometry snap on a grip drag ──────────────────────────────────────────

    /// <summary>
    /// The report: a grip would only ever land on the grid, so lining a trace end up with the pad it
    /// has to meet meant zooming in and eyeballing it. The target here sits DELIBERATELY off-grid
    /// (a 10 µm snap step against a corner at 3,333 µm), so landing on it exactly is only possible
    /// if geometry snap won — a grid-snapped drag can only reach a multiple of 10 µm.
    /// </summary>
    [Fact]
    public void GripDrag_LandsExactlyOnAnOffGridCorner_NotOnTheSnapGrid()
    {
        const long snap = 10_000;          // 10 µm
        const long targetX = 3_333_000;    // 3,333 µm — not a multiple of the snap step
        const long targetY = 0;

        var vm = Place("MLIN", SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0), snap);

        // A separate shape whose corner is the thing to snap to.
        vm.Model.Shapes.Add(new RectShape
        {
            Layer = new LayerKey(1, 0),
            X1 = targetX, Y1 = targetY, X2 = targetX + 500_000, Y2 = targetY + 500_000,
        });

        var grip = Grip(vm, "L", 1, 0);
        Assert.NotEqual(targetX, grip.X);

        // Land the cursor a little short of the corner so the snap has to pull it the rest of the way.
        Drag(vm, grip, targetX - 2_000, targetY);

        Assert.Equal(targetX, RightEdgeOf(vm));
        Assert.NotEqual(0, targetX % snap);   // non-vacuous: the target really is off-grid
    }

    /// <summary>
    /// With nothing to snap to, the grid still governs — geometry snap OVERRIDES grid snap, it does
    /// not replace it. Without this the first test could pass by the grip simply following the raw
    /// cursor.
    /// </summary>
    [Fact]
    public void GripDrag_WithNoGeometryNearby_StillLandsOnTheGrid()
    {
        const long snap = 10_000;
        var vm = Place("MLIN", SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0), snap);

        Drag(vm, Grip(vm, "L", 1, 0), 3_333_000, 0);

        Assert.Equal(0, RightEdgeOf(vm) % snap);
    }

    /// <summary>
    /// A grip must never attract to the artwork it is itself dragging: the instance regenerates every
    /// tick, so its own edge is a target that moves with the cursor. With the instance excluded and no
    /// other geometry present, the drag falls back to the grid — which is what the previous test
    /// asserts, and what this one proves is not an accident of there being nothing in range.
    /// </summary>
    [Fact]
    public void GripDrag_ExcludesItsOwnInstanceFromSnapCandidates()
    {
        const long snap = 10_000;
        var vm = Place("MLIN", SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0), snap);

        var grip = Grip(vm, "L", 1, 0);
        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 200_000, snapTolDbu: 50_000);
        vm.OnPointerMoved(grip.X + 3_000, 0, leftDown: true, KeyModifiers.None,
                          hitTolDbu: 200_000, snapTolDbu: 50_000);

        // Mid-drag, with the cursor a few DBU off the instance's own corner: nothing real is offered.
        Assert.False(vm.HasSnapTargetForTests,
            "a grip drag must not attract to the instance it is regenerating");

        vm.OnPointerReleased(grip.X + 3_000, 0, KeyModifiers.None);
    }

    /// <summary>R-dup-2: the geometry-snap TOGGLE is what turns the attraction off now. Alt used to,
    /// and no longer does — see the test below, which pins the retirement rather than leaving it to be
    /// inferred from this one's absence.</summary>
    [Fact]
    public void GripDrag_WithGeometrySnapToggledOff_IgnoresTheCornerEntirely()
    {
        const long snap = 10_000;
        const long targetX = 3_333_000;

        var vm = Place("MLIN", SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0), snap);
        vm.GeometrySnapEnabled = false;
        vm.Model.Shapes.Add(new RectShape
        {
            Layer = new LayerKey(1, 0),
            X1 = targetX, Y1 = 0, X2 = targetX + 500_000, Y2 = 500_000,
        });

        Drag(vm, Grip(vm, "L", 1, 0), targetX - 2_000, 0);

        Assert.NotEqual(targetX, RightEdgeOf(vm));
    }

    [Fact]
    public void GripDrag_WithAltHeld_StillSnapsToGeometry()
    {
        const long snap = 10_000;
        const long targetX = 3_333_000;

        var vm = Place("MLIN", SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0), snap);
        vm.Model.Shapes.Add(new RectShape
        {
            Layer = new LayerKey(1, 0),
            X1 = targetX, Y1 = 0, X2 = targetX + 500_000, Y2 = 500_000,
        });

        Drag(vm, Grip(vm, "L", 1, 0), targetX - 2_000, 0, mods: KeyModifiers.Alt);

        Assert.Equal(targetX, RightEdgeOf(vm));
    }

    // ── The MKlopf overlap guard ──────────────────────────────────────────────

    // A taper with a large lateral offset: shorten it far enough and the centreline has to turn
    // tighter than its own width can, so the outline crosses itself. Measured on this fixture, the
    // fold appears between L = 5 mm and L = 3 mm.
    private static Dictionary<string, PCellValue> OffsetTaper(double lMetres) => new(StringComparer.Ordinal)
    {
        ["Z1"]          = PCellValue.Real(50),
        ["Z2"]          = PCellValue.Real(100),
        ["GammaMax"]    = PCellValue.Real(0.05),
        ["L"]           = PCellValue.Real(lMetres),
        ["Offset"]      = PCellValue.Real(5e-3),
        ["SmoothSteps"] = PCellValue.Real(0),
    };

    private static bool FoldsThroughItself(PCellResult r)
        => r.Shapes.Any(s => LayoutSelfIntersection.Test(s, null));

    /// <summary>
    /// The fixture's own premise, asserted rather than assumed: a long taper is clean and a short one
    /// really does fold. Without this the guard test below could pass against geometry that never
    /// overlapped in the first place.
    /// </summary>
    [Fact]
    public void TheFixtureActuallyFolds_WhenLIsShortEnough()
    {
        Assert.False(FoldsThroughItself(
            MKlopfPCell.Generate(OffsetTaper(20e-3), null, PCellLayerSelection.Default)));
        Assert.True(FoldsThroughItself(
            MKlopfPCell.Generate(OffsetTaper(1e-3), null, PCellLayerSelection.Default)));
    }

    /// <summary>
    /// The report: dragging L short must not push the taper through itself. The grip stops at the
    /// last value that did not fold — the committed geometry is clean, and L is strictly shorter than
    /// it started (so the guard stopped the drag rather than refusing it outright).
    /// </summary>
    [Fact]
    public void DraggingLShort_StopsBeforeTheGeometryFolds()
    {
        var vm = Place("MKLOPF", OffsetTaper(20e-3), snapDbu: 1_000);

        // The overlap guard only runs on the LIVE preview path — a deferred drag regenerates once, on
        // release, and has no intermediate artwork to stop on. Deferral is decided by a measured 16 ms
        // budget, so under a full-solution run this test failed with the guard never getting a chance.
        // Put the budget out of reach: what is being pinned is the guard, not the machine's speed.
        vm.LivePreviewBudgetMs = 1e9;

        double startL = ParametersOf(vm)["L"].AsReal();
        var grip = Grip(vm, "L", 1, 0);

        // Drag the far end back past the origin — far shorter than any non-folding length — in the
        // many small steps a real pointer emits, so the guard has intermediate values to stop on.
        Drag(vm, grip, -2_000_000, grip.Y, tolDbu: 400_000, steps: 40);

        double endL = ParametersOf(vm)["L"].AsReal();
        Assert.True(endL < startL, $"the grip must still shorten the taper (L {startL} → {endL})");

        var final = MKlopfPCell.Generate(ParametersOf(vm), null, PCellLayerSelection.Default);
        Assert.False(FoldsThroughItself(final),
            $"a grip drag must not commit a self-overlapping outline (L={endL})");
    }

    /// <summary>
    /// Scope: the guard is on the DRAG, not on the parameter. A value typed into the Properties
    /// Inspector or the parameter dialog is a deliberate act on a named number and still goes
    /// through — this editor reports a bad parameter rather than forbidding one.
    /// </summary>
    [Fact]
    public void TypingAShortL_IsStillAllowedToOverlap()
    {
        var vm = Place("MKLOPF", OffsetTaper(20e-3), snapDbu: 1_000);

        var shortened = new Dictionary<string, PCellValue>(ParametersOf(vm), StringComparer.Ordinal)
        {
            ["L"] = PCellValue.Real(1e-3),
        };

        Assert.True(vm.EditInstancePCellParameters(0, shortened));
        Assert.Equal(1e-3, ParametersOf(vm)["L"].AsReal(), 12);

        // And it really is the overlapping geometry — the edit was not quietly clamped either.
        Assert.True(FoldsThroughItself(
            MKlopfPCell.Generate(ParametersOf(vm), null, PCellLayerSelection.Default)));
    }
}
