using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// M4–M6 of brief-pcell-parameter-handles.md, driven through the REAL gesture entry points
/// (<c>OnPointerPressed</c>/<c>Moved</c>/<c>Released</c>) against a real generated cell on disk —
/// not against the solver in isolation, which has its own tests.
///
/// <para>What these exist to catch is the layer between the two: that a grip is found where the
/// instance transform actually puts it, that a drag commits through the copy-on-write path and not
/// past it, and that nothing is written to disk until the pointer comes up.</para>
/// </summary>
public sealed class PCellParameterHandleDragTests : IDisposable
{
    private readonly string _workspaceDir;

    public PCellParameterHandleDragTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crf-pcell-handle-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        File.WriteAllText(Path.Combine(_workspaceDir, ".cws"), "{}");
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir)) Directory.Delete(_workspaceDir, recursive: true);
    }

    // ── Fixture ──────────────────────────────────────────────────────────────────────────────

    private LayoutEditorViewModel MakeVm()
        => new(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 },
               Path.Combine(_workspaceDir, "Doc", "layout", "main.clay"));

    /// <summary>Places one MLIN instance and selects it — the state every drag below starts from.</summary>
    private (LayoutEditorViewModel Vm, string CellRef) PlaceMlin(
        LayoutRotation rot = LayoutRotation.R0, bool mirrorX = false, double mag = 1.0,
        long x = 0, long y = 0)
    {
        var vm = MakeVm();
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(
            _workspaceDir, "MLIN", defaults, null, null, PCellLayerSelection.Default);
        string cellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir);

        vm.Model.Instances.Add(new LayoutInstance
        {
            CellRef = cellRef, X = x, Y = y, Mag = mag, Rot = rot, MirrorX = mirrorX,
        });
        vm.SelectInstance(0);
        return (vm, cellRef);
    }

    private static IReadOnlyDictionary<string, PCellValue> ParametersOf(LayoutEditorViewModel vm, int index)
    {
        var res = CellLayoutResolver.Resolve(vm.Model.Instances[index].CellRef, vm.InstanceBaseDir);
        Assert.Equal(CellLayoutState.Resolved, res.State);
        return res.View!.PCellOrigin!.Parameters;
    }

    /// <summary>
    /// The FIRST grip carrying this label, in declaration order. MLIN declares one grip per edge, so
    /// two share each label — first() is the right edge for "L" and the top edge for "W", which is
    /// exactly the grip every test below this line was written against when there was only one of
    /// each. Kept deliberately, so the pre-existing tests keep testing what they always tested; the
    /// edge-specific behaviour has its own tests rather than being smuggled into these.
    /// </summary>
    private static PCellHandleMarker GripFor(LayoutEditorViewModel vm, string label)
        => vm.Overlay.PCellHandles.First(h => h.Label == label);

    /// <summary>A grip picked by its own travel direction — the only thing that tells two grips
    /// sharing a label apart.</summary>
    private static PCellHandleMarker EdgeGrip(LayoutEditorViewModel vm, string label, double dx, double dy)
        => Assert.Single(vm.Overlay.PCellHandles, h =>
               h.Label == label && Math.Abs(h.AxisDx - dx) < 1e-6 && Math.Abs(h.AxisDy - dy) < 1e-6);

    /// <summary>A whole drag, as the canvas performs one: press on the grip, move, release.</summary>
    private static void Drag(LayoutEditorViewModel vm, PCellHandleMarker grip, long toX, long toY,
                             long tolDbu = 200_000)
    {
        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: tolDbu);
        vm.OnPointerMoved(toX, toY, leftDown: true, KeyModifiers.None, hitTolDbu: tolDbu);
        vm.OnPointerReleased(toX, toY, KeyModifiers.None);
    }

    // ── Resolution and drawing ───────────────────────────────────────────────────────────────

    [Fact]
    public void ASelectedMlinInstance_ShowsItsDeclaredGrips()
    {
        var (vm, _) = PlaceMlin();

        Assert.Equal(4, vm.Overlay.PCellHandles.Count);   // one per edge midpoint
        Assert.Equal(2, vm.Overlay.PCellHandles.Count(h => h.Label == "L"));
        Assert.Equal(2, vm.Overlay.PCellHandles.Count(h => h.Label == "W"));
    }

    [Fact]
    public void AnUnselectedInstance_ShowsNoGrips()
    {
        var (vm, _) = PlaceMlin();
        vm.DeselectAllCommand.Execute(null);

        Assert.Empty(vm.Overlay.PCellHandles);
    }

    [Fact]
    public void AMultiSelection_ShowsNoGrips_MirroringL1dsOwnRule()
    {
        var (vm, cellRef) = PlaceMlin();
        vm.Model.Instances.Add(new LayoutInstance { CellRef = cellRef, X = 50_000_000, Y = 0, Mag = 1.0 });
        vm.SelectAllCommand.Execute(null);

        Assert.Empty(vm.Overlay.PCellHandles);
    }

    [Fact]
    public void GripsAreDrawnOnTheBasePlacementOnly_NotOncePerArrayCell()
    {
        // 50x50 would otherwise be 5,000 grips all driving the same two values.
        var (vm, _) = PlaceMlin();
        vm.Model.Instances[0].Rows = 50;
        vm.Model.Instances[0].Cols = 50;
        vm.Model.Instances[0].PitchX = 5_000_000;
        vm.Model.Instances[0].PitchY = 5_000_000;
        vm.SelectInstance(0);

        Assert.Equal(4, vm.Overlay.PCellHandles.Count);
    }

    // ── Gate 2: round trip ───────────────────────────────────────────────────────────────────

    [Fact]
    public void DraggingTheLengthGrip_CommitsTheParameterThatPutsItThere()
    {
        var (vm, _) = PlaceMlin();
        var grip = GripFor(vm, "L");

        Drag(vm, grip, toX: 6_000_000, toY: 0);

        // The property everything rests on: regenerate at the committed value and the grip is back
        // under where the drag left it.
        var after = GripFor(vm, "L");
        Assert.InRange(after.X, 6_000_000 - 2_000, 6_000_000 + 2_000);
        Assert.Equal(0.006, ParametersOf(vm, 0).Real("L"), 6);
    }

    [Fact]
    public void DraggingTheWidthGrip_EditsWidthAndLeavesLengthAlone()
    {
        var (vm, _) = PlaceMlin();
        double lengthBefore = ParametersOf(vm, 0).Real("L");
        var grip = GripFor(vm, "W");

        // The top-edge grip is anchored on the BOTTOM edge (y = -1450 um for the default 2.9 mm) and
        // that edge holds its world position, so dragging the top edge to y = 400 um gives a width of
        // 400 + 1450 = 1850 um. Under the pre-edge-grip declaration this same drag read as 800 um
        // (a half-width grip anchored on the centreline) — the number changed because the anchor did.
        Drag(vm, grip, toX: grip.X, toY: 400_000);

        var p = ParametersOf(vm, 0);
        Assert.Equal(0.00185, p.Real("W"), 7);
        Assert.Equal(lengthBefore, p.Real("L"), 9);
    }

    // ── Gate 3: the transform ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The gate that catches R-pch-8 being done backwards. Every rotation and mirror, plus a
    /// magnified instance: the drag is expressed in WORLD coordinates and must land the same
    /// parameter value regardless of how the instance is placed.
    /// </summary>
    [Theory]
    [InlineData(LayoutRotation.R0,   false, 1.0)]
    [InlineData(LayoutRotation.R90,  false, 1.0)]
    [InlineData(LayoutRotation.R180, false, 1.0)]
    [InlineData(LayoutRotation.R270, false, 1.0)]
    [InlineData(LayoutRotation.R0,   true,  1.0)]
    [InlineData(LayoutRotation.R90,  true,  1.0)]
    [InlineData(LayoutRotation.R180, true,  1.0)]
    [InlineData(LayoutRotation.R270, true,  1.0)]
    [InlineData(LayoutRotation.R0,   false, 2.0)]
    [InlineData(LayoutRotation.R90,  true,  2.0)]
    public void ATransformedInstance_DragsToTheSameParameterValue(
        LayoutRotation rot, bool mirrorX, double mag)
    {
        var (vm, _) = PlaceMlin(rot, mirrorX, mag);
        var grip = GripFor(vm, "L");
        var inst = vm.Model.Instances[0];

        // Aim at the world position the cell-local point (6 mm, 0) actually occupies under this
        // instance's own transform — the same question the user answers with their eyes.
        var (targetX, targetY) = LayoutInstanceTransform.TransformPoint(6_000_000, 0, inst, 0, 0);
        Drag(vm, grip, targetX, targetY, tolDbu: 400_000);

        // With Mag = 2 the same world travel is HALF the cell-local travel — the factor-of-two that
        // projecting in world space instead would silently get wrong.
        Assert.Equal(0.006, ParametersOf(vm, 0).Real("L"), 6);
    }

    // ── Gates 4, 6, 7: copy-on-write, undo, read-only ────────────────────────────────────────

    [Fact]
    public void DraggingOneInstance_LeavesASiblingSharingTheSameCellUntouched()
    {
        var (vm, cellRef) = PlaceMlin();
        vm.Model.Instances.Add(new LayoutInstance { CellRef = cellRef, X = 50_000_000, Y = 0, Mag = 1.0 });
        vm.SelectInstance(0);

        Drag(vm, GripFor(vm, "L"), toX: 6_000_000, toY: 0);

        Assert.NotEqual(cellRef, vm.Model.Instances[0].CellRef);
        Assert.Equal(cellRef, vm.Model.Instances[1].CellRef);
    }

    [Fact]
    public void ADragOfManyPointerMoves_IsExactlyOneUndoEntry()
    {
        var (vm, cellRef) = PlaceMlin();
        var grip = GripFor(vm, "L");

        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 200_000);
        for (int i = 1; i <= 40; i++)
            vm.OnPointerMoved(3_000_000 + i * 50_000, 0, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerReleased(5_000_000, 0, KeyModifiers.None);

        Assert.NotEqual(cellRef, vm.Model.Instances[0].CellRef);
        vm.UndoCommand.Execute(null);
        Assert.Equal(cellRef, vm.Model.Instances[0].CellRef);
        Assert.False(vm.UndoRedo.CanUndo);   // one entry, not forty
    }

    [Fact]
    public void TheOriginalGeneratedCell_IsNeverModifiedByADrag()
    {
        var (vm, cellRef) = PlaceMlin();
        string clay = Directory.GetFiles(
            Path.Combine(vm.InstanceBaseDir, cellRef), "*.clay", SearchOption.AllDirectories).Single();
        string before = File.ReadAllText(clay);

        Drag(vm, GripFor(vm, "L"), toX: 6_000_000, toY: 0);

        Assert.Equal(before, File.ReadAllText(clay));
    }

    [Fact]
    public void EscapeMidDrag_CommitsNothing_AndPushesNoUndoEntry()
    {
        var (vm, cellRef) = PlaceMlin();
        var grip = GripFor(vm, "L");

        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerMoved(6_000_000, 0, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.Equal(cellRef, vm.Model.Instances[0].CellRef);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void PressingAGripWithoutMoving_IsNotAnEdit()
    {
        var (vm, cellRef) = PlaceMlin();
        var grip = GripFor(vm, "L");

        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerReleased(grip.X, grip.Y, KeyModifiers.None);

        Assert.Equal(cellRef, vm.Model.Instances[0].CellRef);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    // ── Gate 5: no disk during a drag ────────────────────────────────────────────────────────

    [Fact]
    public void NoGeneratedCellIsWrittenUntilTheDragCommits()
    {
        var (vm, _) = PlaceMlin();
        var grip = GripFor(vm, "L");

        // Counted under THIS test's own workspace: a process-wide counter would be perturbed by
        // any other test creating a cell in parallel, quietly turning this into "nothing anywhere
        // wrote a cell" — which is not the claim.
        int before = GeneratedCellStore.CellsWrittenUnder(_workspaceDir);
        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 200_000);
        for (int i = 1; i <= 50; i++)
            vm.OnPointerMoved(3_000_000 + i * 40_000, 0, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);

        // Fifty distinct parameter values previewed, and not one folder on disk. A drag that wrote
        // per move would leave fifty orphaned cells behind — there is no collection for them.
        Assert.Equal(before, GeneratedCellStore.CellsWrittenUnder(_workspaceDir));

        vm.OnPointerReleased(5_000_000, 0, KeyModifiers.None);
        Assert.Equal(before + 1, GeneratedCellStore.CellsWrittenUnder(_workspaceDir));
    }

    // ── The grip wins the press ──────────────────────────────────────────────────────────────

    [Fact]
    public void PressingAGrip_DoesNotMoveTheInstance()
    {
        // R-pch-8's ordering. If the grip check ran after the body-move check, this drag would
        // translate the whole instance instead of editing a parameter — and there is no way for a
        // user to work around a gesture that does the wrong thing.
        var (vm, _) = PlaceMlin();
        long xBefore = vm.Model.Instances[0].X;

        Drag(vm, GripFor(vm, "L"), toX: 6_000_000, toY: 0);

        Assert.Equal(xBefore, vm.Model.Instances[0].X);
    }

    [Fact]
    public void AGripWinsThePress_EvenOverAnotherInstanceSittingUnderIt()
    {
        // The consequence of R-pch-8's ordering, pinned deliberately rather than left to be
        // rediscovered: a grip belongs to the SELECTED instance and is drawn on top, so pressing it
        // grabs the grip rather than selecting whatever is beneath. Exactly how an L1d handle already
        // behaves, and the reason MklopfLayoutEntryModeTests had to move one of its fixtures.
        var (vm, cellRef) = PlaceMlin();
        var grip = GripFor(vm, "L");

        // A second instance placed exactly under instance 0's length grip.
        vm.Model.Instances.Add(new LayoutInstance { CellRef = cellRef, X = grip.X, Y = grip.Y, Mag = 1.0 });

        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerMoved(6_000_000, 0, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerReleased(6_000_000, 0, KeyModifiers.None);

        // Instance 0 was edited; the selection never moved to instance 1.
        Assert.NotEqual(cellRef, vm.Model.Instances[0].CellRef);
        Assert.Equal(cellRef, vm.Model.Instances[1].CellRef);
        Assert.Equal(0, Assert.Single(vm.SelectedInstanceIndices));
    }

    [Fact]
    public void PressingAwayFromAnyGrip_StillMovesTheInstanceNormally()
    {
        var (vm, _) = PlaceMlin();

        // Inside the trace but well clear of either grip.
        vm.OnPointerPressed(1_500_000, 0, KeyModifiers.None, hitTolDbu: 100_000);
        vm.OnPointerMoved(1_500_000 + 4_000_000, 0, leftDown: true, KeyModifiers.None, hitTolDbu: 100_000);
        vm.OnPointerReleased(1_500_000 + 4_000_000, 0, KeyModifiers.None);

        Assert.Equal(4_000_000, vm.Model.Instances[0].X);
    }

    // ── Live preview ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MidDrag_TheOverlayCarriesRegeneratedArtworkForThatInstance()
    {
        var (vm, _) = PlaceMlin();
        var grip = GripFor(vm, "L");

        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerMoved(6_000_000, 0, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);

        var preview = vm.Overlay.PCellHandlePreview;
        Assert.NotNull(preview);
        Assert.Equal(0, preview!.Value.InstanceIndex);
        Assert.NotEmpty(preview.Value.GhostView.Shapes);

        var bbox = LayoutGeometry.BboxOf(preview.Value.GhostView.Shapes[0]);
        Assert.InRange(bbox.MaxX, 6_000_000 - 2_000, 6_000_000 + 2_000);
    }

    [Fact]
    public void MidDrag_TheReadoutNamesTheParameterAndItsValue()
    {
        var (vm, _) = PlaceMlin();
        var grip = GripFor(vm, "L");

        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerMoved(6_000_000, 0, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);

        Assert.StartsWith("L =", vm.DrawReadoutText);
    }

    [Fact]
    public void AfterTheDrag_TheReadoutIsCleared()
    {
        var (vm, _) = PlaceMlin();
        Drag(vm, GripFor(vm, "L"), toX: 6_000_000, toY: 0);

        Assert.Equal("", vm.DrawReadoutText);
    }

    // ── Snap ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>R-dup-2 moved the "suspend it" half from Alt to the grid-snap toggle (F9); the claim
    /// itself — that the PARAMETER answers to the world-space snap, not just the cursor — is
    /// unchanged, and is still tested against its own unsnapped control.</summary>
    [Fact]
    public void TheDragSnapsInWorldSpace_AndTheGridToggleSuspendsIt()
    {
        var (vm, _) = PlaceMlin();
        vm.SnapDbu = 1_000_000;   // 1 mm

        var grip = GripFor(vm, "L");
        Drag(vm, grip, toX: 6_400_000, toY: 0);            // snaps back to 6 mm
        double snapped = ParametersOf(vm, 0).Real("L");

        var (vm2, _) = PlaceMlin();
        vm2.SnapDbu = 1_000_000;
        vm2.ToggleSnapDbuEnabled();
        var grip2 = GripFor(vm2, "L");
        vm2.OnPointerPressed(grip2.X, grip2.Y, KeyModifiers.None, hitTolDbu: 200_000);
        vm2.OnPointerMoved(6_400_000, 0, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);
        vm2.OnPointerReleased(6_400_000, 0, KeyModifiers.None);
        double unsnapped = ParametersOf(vm2, 0).Real("L");

        Assert.Equal(0.006, snapped, 6);
        Assert.Equal(0.0064, unsnapped, 6);
    }

    // ── Gate 10: degradation ─────────────────────────────────────────────────────────────────

    [Fact]
    public void MTee_ShowsAGripPerArmWidth_AndDraggingOneEditsOnlyThatWidth()
    {
        var vm = MakeVm();
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.MTee, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(
            _workspaceDir, "MTEE", defaults, null, null, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), Mag = 1.0,
        });
        vm.SelectInstance(0);

        // Six: one at each corner of the metal (two per arm end cap).
        Assert.Equal(6, vm.Overlay.PCellHandles.Count);

        double w2Before = ParametersOf(vm, 0).Real("W2");
        double w3Before = ParametersOf(vm, 0).Real("W3");
        var w1Grip = GripFor(vm, "W1");

        // Widen arm 1 to a half-width of 400 um. Its own stub length is a multiple of its width, so
        // the grip travels in x as well — the projection ignores that, which is what makes a
        // one-degree-of-freedom grip work on a cell whose geometry moves under it.
        Drag(vm, w1Grip, toX: w1Grip.X, toY: 400_000, tolDbu: 400_000);

        var after = ParametersOf(vm, 0);
        Assert.Equal(0.0008, after.Real("W1"), 6);
        Assert.Equal(w2Before, after.Real("W2"), 9);
        Assert.Equal(w3Before, after.Real("W3"), 9);
    }

    // ── R-pch-4a: the two-axis grip ──────────────────────────────────────────────────────────

    private (LayoutEditorViewModel Vm, string CellRef) PlaceMklopf()
    {
        var vm = MakeVm();
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mklopf, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(
            _workspaceDir, "MKLOPF", defaults, null, null, PCellLayerSelection.Default);
        string cellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir);
        vm.Model.Instances.Add(new LayoutInstance { CellRef = cellRef, Mag = 1.0 });
        vm.SelectInstance(0);
        return (vm, cellRef);
    }

    /// <summary>
    /// MKlopf's FAR middle grip — the two-axis one, identified by its anchor being pin 1 (the cell
    /// origin, so (0,0) in world for an instance placed at the origin under any rotation). The NEAR
    /// middle grip is the mirror image and anchors on the far end instead, so the two are told apart
    /// by their anchor rather than by list position or by a screen direction that rotation changes.
    /// </summary>
    private static PCellHandleMarker MklopfFarGrip(LayoutEditorViewModel vm)
        => Assert.Single(vm.Overlay.PCellHandles, h => h.HasCrossAxis && h is { AnchorX: 0, AnchorY: 0 });

    [Fact]
    public void MKlopf_ShowsThreeGripsPerEnd_AndTheMiddleOneAdvertisesBothAxes()
    {
        var (vm, _) = PlaceMklopf();

        Assert.Equal(6, vm.Overlay.PCellHandles.Count);
        // Exactly the two middle grips hint both directions; the four edge grips drive one impedance
        // each and must not advertise a second axis they do not have.
        Assert.Equal(2, vm.Overlay.PCellHandles.Count(h => h.HasCrossAxis));
        Assert.True(MklopfFarGrip(vm).HasCrossAxis, "the renderer needs to know to hint both directions");
    }

    [Fact]
    public void DraggingTheMKlopfGripDiagonally_EditsLengthAndOffsetTogether()
    {
        var (vm, _) = PlaceMklopf();
        var before = ParametersOf(vm, 0);
        var grip = MklopfFarGrip(vm);

        // One drag, both axes: further along +X is a longer taper, further along +Y is more offset.
        Drag(vm, grip, toX: 8_000_000, toY: 1_500_000, tolDbu: 2_000_000);

        var after = ParametersOf(vm, 0);
        Assert.Equal(0.008, after.Real("L"), 5);
        Assert.Equal(0.0015, after.Real("Offset"), 5);
        Assert.NotEqual(before.Real("L"), after.Real("L"));
        Assert.NotEqual(before.Real("Offset"), after.Real("Offset"));
    }

    [Fact]
    public void ATwoAxisDrag_IsStillExactlyOneUndoEntry()
    {
        // Both axes commit together. Committing them separately would make a single drag two undo
        // steps, which is not what the user did.
        var (vm, cellRef) = PlaceMklopf();
        var grip = MklopfFarGrip(vm);

        Drag(vm, grip, toX: 8_000_000, toY: 1_500_000, tolDbu: 2_000_000);
        Assert.NotEqual(cellRef, vm.Model.Instances[0].CellRef);

        vm.UndoRedo.Undo();
        Assert.Equal(cellRef, vm.Model.Instances[0].CellRef);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void DraggingTheMKlopfGrip_LeavesPin1WhereItWas()
    {
        // The near end stays fixed and the taper stretches from it — what dragging a far corner
        // ought to do, and what the generator's own origin convention already guarantees.
        var (vm, _) = PlaceMklopf();
        var grip = MklopfFarGrip(vm);

        Drag(vm, grip, toX: 8_000_000, toY: 1_500_000, tolDbu: 2_000_000);

        var res = CellLayoutResolver.Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir);
        var pin1 = res.View!.Pins.Single(p => p.Name == "1");
        Assert.Equal(0, pin1.X);
        Assert.Equal(0, pin1.Y);
        // ...and the instance itself never moved either.
        Assert.Equal(0, vm.Model.Instances[0].X);
        Assert.Equal(0, vm.Model.Instances[0].Y);
    }

    [Fact]
    public void TheLiveGhostOfADiagonalMKlopfDrag_CarriesBothAxes_WhenItIsLiveAtAll()
    {
        // MKlopf is the real cell the two-axis grip exists for, and it IS fast enough to preview
        // live — measured ~1.2 ms per generate, ~4 ms for a first move (two solves plus the ghost)
        // against the 16 ms budget. But whether the budget trips is a property of the MACHINE, not
        // of the code, so this asserts the conditional: if a live ghost was produced, it carries
        // both axes. The unconditional version of this claim is covered by the synthetic two-axis
        // generator in PCellHandleDegradationTests, which cannot be slow.
        var (vm, _) = PlaceMklopf();
        var grip = MklopfFarGrip(vm);

        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 2_000_000);
        vm.OnPointerMoved(25_000_000, 1_500_000, leftDown: true, KeyModifiers.None, hitTolDbu: 2_000_000);

        // The readout carries both in EITHER mode — that part is not timing-dependent.
        Assert.Contains("L =", vm.DrawReadoutText);
        Assert.Contains("Offset =", vm.DrawReadoutText);

        if (vm.Overlay.PCellHandlePreview is not { } preview) return;   // deferred under load

        var bbox = LayoutGeometry.BboxOf(preview.GhostView.Shapes[0]);
        Assert.InRange(bbox.MaxX, 24_900_000, 25_100_000);   // stretched to the cursor's x...
        Assert.True(bbox.MaxY > 1_000_000,
            $"the centreline should have swung off axis, but the ghost tops out at {bbox.MaxY}");
    }

    [Fact]
    public void ATwoAxisDrag_ReadsOutBothParameters()
    {
        var (vm, _) = PlaceMklopf();
        var grip = MklopfFarGrip(vm);

        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 2_000_000);
        vm.OnPointerMoved(8_000_000, 1_500_000, leftDown: true, KeyModifiers.None, hitTolDbu: 2_000_000);

        Assert.Contains("L =", vm.DrawReadoutText);
        Assert.Contains("Offset =", vm.DrawReadoutText);
    }

    [Fact]
    public void ATwoAxisGripUnderARotatedInstance_StillSplitsTheDragCorrectly()
    {
        // The decomposition happens in CELL space, after the inverse transform — so which screen
        // direction is "along" and which is "across" follows the instance's own placement without
        // the generator, or this code, knowing anything about it.
        var (vm, _) = PlaceMklopf();
        vm.Model.Instances[0].Rot = LayoutRotation.R90;
        vm.SelectInstance(0);

        var grip = MklopfFarGrip(vm);
        var inst = vm.Model.Instances[0];
        var (tx, ty) = LayoutInstanceTransform.TransformPoint(8_000_000, 1_500_000, inst, 0, 0);

        Drag(vm, grip, tx, ty, tolDbu: 2_000_000);

        var after = ParametersOf(vm, 0);
        Assert.Equal(0.008, after.Real("L"), 5);
        Assert.Equal(0.0015, after.Real("Offset"), 5);
    }

    [Fact]
    public void AnInstanceOfANonPCellCell_ShowsNoGrips()
    {
        var vm = MakeVm();
        string cellDir = Path.Combine(_workspaceDir, "Plain");
        CellFolder.CreateCellFolder(_workspaceDir, "Plain");
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        LayoutPersistence.SaveToFile(
            Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "Plain.clay"), view);

        vm.Model.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), Mag = 1.0,
        });
        vm.SelectInstance(0);

        Assert.Empty(vm.Overlay.PCellHandles);
    }
    // ── R-pch-4b: an edge grip holds the OPPOSITE edge still ─────────────────────────────────

    [Fact]
    public void DraggingTheLeftEdge_GrowsTheLineLeftwards_LeavingTheRightEdgeWhereItWas()
    {
        // The headline of the whole pinned-anchor rule. Without it a generator's inability to move its
        // own origin (R4 pins pin 1 at (0,0)) makes a left-edge drag grow the line to the RIGHT — the
        // opposite of the gesture. The right edge's world position is the thing to assert, because it
        // is what the user is watching.
        var (vm, _) = PlaceMlin();
        long rightBefore = EdgeGrip(vm, "L", 1, 0).X;

        Drag(vm, EdgeGrip(vm, "L", -1, 0), toX: -2_000_000, toY: 0);

        Assert.InRange(EdgeGrip(vm, "L", 1, 0).X, rightBefore - 2_000, rightBefore + 2_000);
        Assert.InRange(EdgeGrip(vm, "L", -1, 0).X, -2_002_000, -1_998_000);

        // The length really did grow — the instance moved, it did not merely slide.
        double lenUm = ParametersOf(vm, 0).Real("L") * 1e6;
        Assert.InRange(lenUm, 11_998, 12_002);   // 10 mm default + 2 mm of new length
    }

    [Fact]
    public void DraggingTheBottomEdge_WidensDownwards_LeavingTheTopEdgeWhereItWas()
    {
        var (vm, _) = PlaceMlin();
        long topBefore = EdgeGrip(vm, "W", 0, 1).Y;

        Drag(vm, EdgeGrip(vm, "W", 0, -1), toX: 1_500_000, toY: -600_000);

        Assert.InRange(EdgeGrip(vm, "W", 0, 1).Y, topBefore - 2_000, topBefore + 2_000);
        Assert.InRange(EdgeGrip(vm, "W", 0, -1).Y, -602_000, -598_000);
    }

    [Fact]
    public void ADraggedEdge_IsStillExactlyOneUndoEntry_EvenThoughTheInstanceAlsoMoves()
    {
        // The parameter edit and the anchor-pinning translate ride in ONE ReplaceInstanceCommand. Two
        // commands would let Undo put the geometry back while leaving the instance moved.
        var (vm, cellRef) = PlaceMlin();
        long xBefore = vm.Model.Instances[0].X;

        Drag(vm, EdgeGrip(vm, "L", -1, 0), toX: -2_000_000, toY: 0);
        Assert.NotEqual(xBefore, vm.Model.Instances[0].X);   // it really did translate

        vm.UndoCommand.Execute(null);

        Assert.Equal(cellRef, vm.Model.Instances[0].CellRef);
        Assert.Equal(xBefore, vm.Model.Instances[0].X);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void ARotatedInstance_PinsTheAnchorInWORLDSpace_NotCellSpace()
    {
        // At R90 the cell's +X is world +Y, so a "left edge" drag runs downward on screen. The edge
        // that must hold still is the one the user can see holding still — a cell-space pin would
        // hold the wrong one and would look correct only at R0.
        var (vm, _) = PlaceMlin(rot: LayoutRotation.R90);
        var far = EdgeGrip(vm, "L", 0, 1);      // cell +X maps to world +Y
        long farBefore = far.Y;

        Drag(vm, EdgeGrip(vm, "L", 0, -1), toX: 0, toY: -2_000_000);

        Assert.InRange(EdgeGrip(vm, "L", 0, 1).Y, farBefore - 2_000, farBefore + 2_000);
    }

    // ── MBend's angle grip ───────────────────────────────────────────────────────────────────

    [Fact]
    public void MBendsAngleGrip_SwingsAboutThePivot_AndCommitsTheAngle()
    {
        var vm = MakeVm();
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.MBend, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(
            _workspaceDir, "MBEND", defaults, null, null, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), Mag = 1.0,
        });
        vm.SelectInstance(0);

        var grip = GripFor(vm, "Angle");
        Assert.True(grip.IsAngular);

        // Swing the free end from 90 degrees round to 45: same radius, on the diagonal.
        double r = Math.Sqrt((double)(grip.X - grip.AnchorX) * (grip.X - grip.AnchorX)
                           + (double)(grip.Y - grip.AnchorY) * (grip.Y - grip.AnchorY));
        long tx = grip.AnchorX + (long)Math.Round(r * Math.Cos(Math.PI / 4));
        long ty = grip.AnchorY + (long)Math.Round(r * Math.Sin(Math.PI / 4));

        Drag(vm, grip, toX: tx, toY: ty, tolDbu: 400_000);

        Assert.Equal(45.0, ParametersOf(vm, 0).Real("Angle"), precision: 0);
    }
    // ── Either end of an MBend swings it ─────────────────────────────────────────────────────

    private (LayoutEditorViewModel Vm, string CellRef) PlaceMBend()
    {
        var vm = MakeVm();
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.MBend, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(
            _workspaceDir, "MBEND", defaults, null, null, PCellLayerSelection.Default);
        string cellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir);
        vm.Model.Instances.Add(new LayoutInstance { CellRef = cellRef, Mag = 1.0 });
        vm.SelectInstance(0);
        return (vm, cellRef);
    }

    /// <summary>
    /// MBend's PIN-1 angle grip. Identified by sitting on the instance's own origin, which is exact
    /// and meaningful rather than incidental: R4 puts pin 1 at the cell origin, so this holds at any
    /// placement and after the instance has been translated by a pinned-anchor drag.
    /// </summary>
    private static PCellHandleMarker Pin1AngleGrip(LayoutEditorViewModel vm)
    {
        var inst = vm.Model.Instances[0];
        return Assert.Single(vm.Overlay.PCellHandles,
            h => h.Label == "Angle" && h.X == inst.X && h.Y == inst.Y);
    }

    /// <summary>MBend's PIN-2 angle grip — the other one.</summary>
    private static PCellHandleMarker Pin2AngleGrip(LayoutEditorViewModel vm)
    {
        var pin1 = Pin1AngleGrip(vm);
        return Assert.Single(vm.Overlay.PCellHandles,
            h => h.Label == "Angle" && (h.X != pin1.X || h.Y != pin1.Y));
    }

    [Fact]
    public void MBendDeclaresAnAngleGripAtBothPins_EachAnchoredOnWhatTheOtherOneHolds()
    {
        var (vm, _) = PlaceMBend();

        var angleGrips = vm.Overlay.PCellHandles.Where(h => h.Label == "Angle").ToList();
        Assert.Equal(2, angleGrips.Count);
        Assert.All(angleGrips, g => Assert.True(g.IsAngular));

        // Pin 2 swings about the PIVOT; pin 1 swings about PIN 2. Pin 1 cannot swing about the pivot
        // — its bearing from there is 180 degrees for every value of Angle — so anchoring it on the
        // moving end is what makes the same parameter measurable from that side.
        var atPin1 = Pin1AngleGrip(vm);
        Assert.NotEqual((0L, 0L), (atPin1.AnchorX, atPin1.AnchorY));
        Assert.Equal((Pin2AngleGrip(vm).X, Pin2AngleGrip(vm).Y), (atPin1.AnchorX, atPin1.AnchorY));
    }

    [Fact]
    public void DraggingMBendsPin1_ChangesTheAngle_AndLeavesPin2WhereItWas()
    {
        var (vm, _) = PlaceMBend();

        var pin1 = Pin1AngleGrip(vm);
        long pin2X = pin1.AnchorX, pin2Y = pin1.AnchorY;

        // Swing pin 1 to a new bearing about pin 2, at roughly its current distance.
        double r = Math.Sqrt((double)(pin1.X - pin2X) * (pin1.X - pin2X)
                           + (double)(pin1.Y - pin2Y) * (pin1.Y - pin2Y));
        double toBearing = Math.Atan2(pin1.Y - pin2Y, (double)(pin1.X - pin2X)) + (20.0 * Math.PI / 180.0);
        long tx = pin2X + (long)Math.Round(r * Math.Cos(toBearing));
        long ty = pin2Y + (long)Math.Round(r * Math.Sin(toBearing));

        double angleBefore = ParametersOf(vm, 0).Real("Angle");
        Drag(vm, pin1, toX: tx, toY: ty, tolDbu: 400_000);
        double angleAfter = ParametersOf(vm, 0).Real("Angle");

        Assert.NotEqual(angleBefore, angleAfter, precision: 1);

        // The end the user did NOT grab holds its world position — that is the whole point of
        // anchoring the grip there, and it is what a pivot-anchored grip could not have given.
        var pin2After = Pin2AngleGrip(vm);
        Assert.InRange(pin2After.X, pin2X - 3_000, pin2X + 3_000);
        Assert.InRange(pin2After.Y, pin2Y - 3_000, pin2Y + 3_000);
    }

    [Fact]
    public void BothMBendAngleGrips_DriveTheSameParameter_InTheSameDirection()
    {
        // Two grips, one parameter — the same rule MLIN's paired edge grips already follow. Asserted
        // because "drag either end" is only true if both ends agree about which way is bigger.
        var (vmA, _) = PlaceMBend();
        var pin2 = Pin2AngleGrip(vmA);
        double rA = Math.Sqrt((double)(pin2.X - pin2.AnchorX) * (pin2.X - pin2.AnchorX)
                            + (double)(pin2.Y - pin2.AnchorY) * (pin2.Y - pin2.AnchorY));
        double bearingA = Math.Atan2(pin2.Y - pin2.AnchorY, (double)(pin2.X - pin2.AnchorX)) + (15.0 * Math.PI / 180.0);
        Drag(vmA, pin2, pin2.AnchorX + (long)Math.Round(rA * Math.Cos(bearingA)),
                        pin2.AnchorY + (long)Math.Round(rA * Math.Sin(bearingA)), tolDbu: 400_000);
        double fromPin2 = ParametersOf(vmA, 0).Real("Angle");

        var (vmB, _) = PlaceMBend();
        var pin1 = Pin1AngleGrip(vmB);
        double rB = Math.Sqrt((double)(pin1.X - pin1.AnchorX) * (pin1.X - pin1.AnchorX)
                            + (double)(pin1.Y - pin1.AnchorY) * (pin1.Y - pin1.AnchorY));
        // Pin 1's bearing runs at HALF the parameter's rate, so the same 15 degrees of Angle is
        // 7.5 degrees of grip travel. Deriving it rather than hardcoding is the point: the test would
        // still pass if the relationship changed, and would fail if the two grips disagreed on sign.
        double bearingB = Math.Atan2(pin1.Y - pin1.AnchorY, (double)(pin1.X - pin1.AnchorX)) + (7.5 * Math.PI / 180.0);
        Drag(vmB, pin1, pin1.AnchorX + (long)Math.Round(rB * Math.Cos(bearingB)),
                        pin1.AnchorY + (long)Math.Round(rB * Math.Sin(bearingB)), tolDbu: 400_000);
        double fromPin1 = ParametersOf(vmB, 0).Real("Angle");

        Assert.Equal(fromPin2, fromPin1, precision: 0);
    }

    // ── The Properties Inspector follows the drag ────────────────────────────────────────────

    [Fact]
    public void ThePropertiesInspector_ShowsTheParameterMovingWhileTheGripIsDragged()
    {
        var (vm, _) = PlaceMlin();
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        vm.SelectInstance(0);

        var lengthRow = Assert.Single(props.PCellParamRows!, r => r.Name == "L");
        string before = lengthRow.ValueText;

        var grip = GripFor(vm, "L");
        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerMoved(6_000_000, 0, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);

        // MID-DRAG: the panel already reads the value the drag has reached, not the one it started
        // from, and the fields are not typeable while it does (R-L1j-2, widened to grip drags).
        Assert.NotEqual(before, lengthRow.ValueText);
        Assert.False(props.IsEditingEnabled);

        vm.OnPointerReleased(6_000_000, 0, KeyModifiers.None);

        // AFTER RELEASE: the committed value, and the same text — the live reading was the solver's
        // own answer, so it cannot disagree with what was committed.
        Assert.False(string.IsNullOrEmpty(lengthRow.ValueText));
        Assert.True(props.IsEditingEnabled);
        Assert.Equal(0.006, ParametersOf(vm, 0).Real("L"), 6);
    }

    [Fact]
    public void ADragOnOneInstance_NeverMovesThePanelShowingADifferentOne()
    {
        // The live values are keyed to the dragged instance's own index. Without that check a drag
        // would bleed into any panel that happened to be open, which is the kind of bug that only
        // shows up with two instances on screen.
        var (vm, cellRef) = PlaceMlin();
        vm.Model.Instances.Add(new LayoutInstance { CellRef = cellRef, X = 60_000_000, Y = 0, Mag = 1.0 });

        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        vm.SelectInstance(0);
        var grip = GripFor(vm, "L");

        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerMoved(6_000_000, 0, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);

        Assert.Equal(0, vm.PCellHandleDragInstanceIndex);
        Assert.NotNull(vm.PCellHandleDragParameters);

        vm.OnPointerReleased(6_000_000, 0, KeyModifiers.None);
        Assert.Null(vm.PCellHandleDragParameters);   // cleared on release, so nothing lingers
    }
}
