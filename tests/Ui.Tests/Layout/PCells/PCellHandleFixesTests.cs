using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// The owner-reported round on parameter grips: the grips going stale whenever the artwork under
/// them moves or is regenerated, the drag readout printing a raw SI number, and a snapped drag
/// committing a value that is not on the snap grid.
///
/// <para>Every one of these is driven through the real gesture / real render path rather than
/// against the solver, because in every case the solver was already right — what was wrong was the
/// layer between it and what the user sees.</para>
/// </summary>
public sealed class PCellHandleFixesTests : IDisposable
{
    private const long OneMilDbu = 25_400;   // at the 1000 DBU/µm default, 1 mil = 25.4 µm

    private readonly string _workspaceDir;

    public PCellHandleFixesTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crf-pcell-fix-" + Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>One MLIN instance, selected, on a layout whose snap step is 1 mil — the owner's own
    /// reported setup, and the one that makes a grid violation visible at all (a whole number of mils
    /// is not a whole number of anything in the metric-derived DBU the parameter converts through).</summary>
    private LayoutEditorViewModel PlaceMlin(long snapDbu = OneMilDbu)
    {
        var vm = new LayoutEditorViewModel(
            new LayoutView { DbuPerMicron = 1000, SnapDbu = snapDbu },
            Path.Combine(_workspaceDir, "Doc", "layout", "main.clay"));
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(
            _workspaceDir, "MLIN", defaults, null, null, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), Mag = 1.0,
        });
        vm.SelectInstance(0);
        return vm;
    }

    private static IReadOnlyDictionary<string, PCellValue> ParametersOf(LayoutEditorViewModel vm)
        => CellLayoutResolver.Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir)
                             .View!.PCellOrigin!.Parameters;

    /// <summary>A value in SI metres, expressed in mil — the unit the snap step is a whole number
    /// of, so "is it on the grid" is just "is this a whole number".</summary>
    private static double Mils(double metres) => metres / 25.4e-6;

    /// <summary>Drags grip <paramref name="index"/> twenty snap steps along its own travel
    /// direction, as the canvas would: press, one move, release.</summary>
    private static void DragGripAlongItsAxis(LayoutEditorViewModel vm, int index, long steps = 20,
                                             KeyModifiers modifiers = KeyModifiers.None)
    {
        var g = vm.Overlay.PCellHandles[index];
        long toX = g.X + (long)(g.AxisDx * steps * vm.SnapDbu);
        long toY = g.Y + (long)(g.AxisDy * steps * vm.SnapDbu);
        vm.OnPointerPressed(g.X, g.Y, modifiers, hitTolDbu: 20_000);
        vm.OnPointerMoved(toX, toY, leftDown: true, modifiers, hitTolDbu: 20_000);
        vm.OnPointerReleased(toX, toY, modifiers);
    }

    // ── The grips must track the artwork, wherever it is being drawn ─────────────────────────

    [Fact]
    public void DraggingTheWholeInstance_MovesItsGripsLive_NotOnlyOnRelease()
    {
        // The grips were placed against Model.Instances[i] — the COMMITTED position — so during an
        // ordinary move drag they sat on the artwork's old location and snapped across on release.
        var vm = PlaceMlin();
        long before = vm.Overlay.PCellHandles[0].X;

        vm.OnPointerPressed(5_000_000, 0, KeyModifiers.None, hitTolDbu: 1_000);       // on the body
        vm.OnPointerMoved(7_000_000, 0, leftDown: true, KeyModifiers.None, hitTolDbu: 1_000);

        long during = vm.Overlay.PCellHandles[0].X;
        Assert.True(during > before,
            $"the grip should have followed the live drag, but stayed at {during}");

        vm.OnPointerReleased(7_000_000, 0, KeyModifiers.None);
        Assert.Equal(during, vm.Overlay.PCellHandles[0].X);   // and release moves it no further
    }

    [Fact]
    public void CommittingAParameterFromThePropertiesInspector_RefreshesTheGrips()
    {
        // A parameter commit re-points the instance at a DIFFERENT generated cell (copy-on-write) at
        // the SAME index, so the selection-index list never changes — which is what the overlay
        // rebuild used to be gated on. The grips went on being drawn for the cell the instance no
        // longer referenced.
        var vm = PlaceMlin();
        long lengthGripX = vm.Overlay.PCellHandles.First(h => h.Label == "L" && h.AxisDx > 0).X;

        var current = ParametersOf(vm);
        var edited = new Dictionary<string, PCellValue>(current)
        {
            ["L"] = PCellValue.Real(current.Real("L") * 2.0),
        };
        Assert.True(vm.EditInstancePCellParameters(0, edited));

        long after = vm.Overlay.PCellHandles.First(h => h.Label == "L" && h.AxisDx > 0).X;
        Assert.True(after > lengthGripX,
            $"the length grip should have moved out with the doubled cell, but is still at {after}");
    }

    // ── The Properties Inspector, live, from EVERY grip ──────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryMlinGrip_MovesItsOwnParameterRow_MidDrag(int gripIndex)
    {
        // Three of MLIN's four grips pin their anchor, which puts an entry in InstanceDragOverrides —
        // and SingleSelectedInstance blanked the whole panel whenever that dictionary was non-empty.
        // So exactly the one grip whose anchor sits at the cell origin updated, and the other three
        // silently did not: "only some of the grippers update the parameters."
        var vm = PlaceMlin();
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);

        var rows = props.PCellParamRows!;
        var before = Enumerable.Range(0, rows.Count).ToDictionary(i => rows[i].Name, i => rows[i].ValueText);

        var g = vm.Overlay.PCellHandles[gripIndex];
        long toX = g.X + (long)(g.AxisDx * 20 * vm.SnapDbu);
        long toY = g.Y + (long)(g.AxisDy * 20 * vm.SnapDbu);
        vm.OnPointerPressed(g.X, g.Y, KeyModifiers.None, hitTolDbu: 20_000);
        vm.OnPointerMoved(toX, toY, leftDown: true, KeyModifiers.None, hitTolDbu: 20_000);

        string driven = g.Label;
        Assert.NotEqual(before[driven], rows.Single(r => r.Name == driven).ValueText);
        // ...and only that one: a one-axis grip moves exactly one row.
        foreach (var r in rows.Where(r => r.Name != driven))
            Assert.Equal(before[r.Name], r.ValueText);
    }

    // ── The readout, in units a person reads ─────────────────────────────────────────────────

    [Fact]
    public void TheDragReadout_ShowsALengthInTheDocumentsOwnDisplayUnit()
    {
        var vm = PlaceMlin();
        vm.DisplayUnit = LayoutUnit.Mil;

        var g = vm.Overlay.PCellHandles.First(h => h.Label == "W" && h.AxisDy > 0);
        vm.OnPointerPressed(g.X, g.Y, KeyModifiers.None, hitTolDbu: 20_000);
        vm.OnPointerMoved(g.X, g.Y + 20 * vm.SnapDbu, leftDown: true, KeyModifiers.None, hitTolDbu: 20_000);

        // It used to read "W = 0.0039116" — a bare SI-metres number with no unit at all.
        Assert.StartsWith("W = ", vm.DrawReadoutText);
        Assert.EndsWith(" mil", vm.DrawReadoutText);
        Assert.DoesNotContain("0.00", vm.DrawReadoutText);
    }

    [Fact]
    public void TheDragReadout_ShowsAnAngleInDegrees_NotAsALength()
    {
        var vm = new LayoutEditorViewModel(
            new LayoutView { DbuPerMicron = 1000, SnapDbu = OneMilDbu, DisplayUnit = LayoutUnit.Mil },
            Path.Combine(_workspaceDir, "Doc", "layout", "main.clay"));
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.MBend, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(
            _workspaceDir, "MBEND", defaults, null, null, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), Mag = 1.0,
        });
        vm.SelectInstance(0);

        var g = vm.Overlay.PCellHandles.First(h => h.Label == "Angle");
        vm.OnPointerPressed(g.X, g.Y, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerMoved(g.X + 500_000, g.Y - 500_000, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);

        Assert.Contains("°", vm.DrawReadoutText);
        Assert.DoesNotContain("mil", vm.DrawReadoutText);
    }

    // ── The snap grid means the PARAMETER, not just the cursor ───────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ADragUnder1MilSnapping_CommitsAValueOnTheMilGrid(int gripIndex)
    {
        // The reported failure: a width dragged to 468 mil came back as 468.00006 mil. The CURSOR was
        // snapped all along; the solved parameter was not, so it landed wherever convergence stopped
        // — and that value is what a schematic push-back, and any later export, then carries.
        var vm = PlaceMlin();
        string driven = vm.Overlay.PCellHandles[gripIndex].Label;

        DragGripAlongItsAxis(vm, gripIndex);

        double mils = Mils(ParametersOf(vm).Real(driven));
        Assert.Equal(Math.Round(mils), mils, 6);
    }

    [Fact]
    public void TurningTheGridOff_SuspendsItForTheParameterJustAsItDoesForTheCursor()
    {
        // The parameter lattice has to answer to the same control the cursor does, or "snapping off"
        // would only be half true. R-dup-2 moved that control from Alt (which now duplicates) to the
        // grid-snap toggle; the claim being tested is unchanged.
        var vm = PlaceMlin();
        var g = vm.Overlay.PCellHandles.First(h => h.Label == "W" && h.AxisDy > 0);

        long toY = g.Y + 20 * OneMilDbu + OneMilDbu / 2;
        vm.ToggleSnapDbuEnabled();
        vm.OnPointerPressed(g.X, g.Y, KeyModifiers.None, hitTolDbu: 20_000);
        vm.OnPointerMoved(g.X, toY, leftDown: true, KeyModifiers.None, hitTolDbu: 20_000);
        vm.OnPointerReleased(g.X, toY, KeyModifiers.None);

        double mils = Mils(ParametersOf(vm).Real("W"));
        Assert.NotEqual(Math.Round(mils), mils, 6);
    }

    [Fact]
    public void WithSnappingOff_TheParameterIsNotQuantizedEither()
    {
        var vm = PlaceMlin(snapDbu: 0);
        var g = vm.Overlay.PCellHandles.First(h => h.Label == "W" && h.AxisDy > 0);

        long toY = g.Y + 20 * OneMilDbu + OneMilDbu / 2;
        vm.OnPointerPressed(g.X, g.Y, KeyModifiers.None, hitTolDbu: 20_000);
        vm.OnPointerMoved(g.X, toY, leftDown: true, KeyModifiers.None, hitTolDbu: 20_000);
        vm.OnPointerReleased(g.X, toY, KeyModifiers.None);

        double mils = Mils(ParametersOf(vm).Real("W"));
        Assert.NotEqual(Math.Round(mils), mils, 6);
    }

    [Fact]
    public void AnImpedanceGrip_IsNeverRoundedOntoTheLengthGrid()
    {
        // MKlopf's edge grips drive Z1/Z2. Snapping an impedance onto a distance lattice would be
        // arithmetic with no meaning behind it, so the quantizer is declared-quantity-gated.
        var vm = new LayoutEditorViewModel(
            new LayoutView { DbuPerMicron = 1000, SnapDbu = OneMilDbu },
            Path.Combine(_workspaceDir, "Doc", "layout", "main.clay"));
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mklopf, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(
            _workspaceDir, "MKLOPF", defaults, null, null, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), Mag = 1.0,
        });
        vm.SelectInstance(0);

        int idx = vm.Overlay.PCellHandles.ToList().FindIndex(h => h.Label == "Z1");
        double before = ParametersOf(vm).Real("Z1");
        DragGripAlongItsAxis(vm, idx, steps: 10);
        double after = ParametersOf(vm).Real("Z1");

        Assert.NotEqual(before, after);
        // An impedance in ohms has no business being a multiple of 25.4 µm expressed as a number.
        Assert.NotEqual(Math.Round(after / 25.4e-6), after / 25.4e-6, 6);
    }

    // ── The selection highlight follows the regenerated artwork ──────────────────────────────

    [Fact]
    public void TheSelectionOutline_TracksTheLiveGripDragPreview_NotTheCellStillOnDisk()
    {
        // The outline was measured from CellHierarchy.InstanceBbox — i.e. from the cell on disk —
        // so it kept the pre-drag size while the artwork inside it grew.
        var vm = PlaceMlin();
        var inst = vm.Model.Instances[0];
        var committed = CellHierarchy.InstanceBbox(inst, vm.InstanceBaseDir);

        // A preview twice as long as the committed cell, exactly as a live L drag produces.
        var ghost = new LayoutView { DbuPerMicron = 1000 };
        ghost.Shapes.Add(new RectShape
        {
            Layer = new LayerKey(1, 0),
            X1 = committed.MinX, Y1 = committed.MinY,
            X2 = committed.MaxX * 2, Y2 = committed.MaxY,
        });

        var previewBbox = CellHierarchy.InstanceBboxOfView(ghost, inst, vm.InstanceBaseDir);
        Assert.True(previewBbox.MaxX > committed.MaxX);

        var overlay = new LayoutOverlay
        {
            SelectedInstanceIndices = [0],
            PCellHandlePreview = (0, ghost),
        };

        // Frame the region BEYOND the committed cell, where only a preview-sized outline can paint.
        var vp = new LayoutViewport(committed.MinX - 200_000, committed.MinY - 200_000, 0.00002, 500, 300);
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        LayoutRenderer.Draw(surface.Canvas, vm.Model, tech: null, vp,
            new LayoutRenderOptions
            {
                Theme = LayoutRenderTheme.Light, ShowGrid = false,
                Overlay = overlay, BaseDir = vm.InstanceBaseDir,
            });

        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        bool accentPastCommitted = false;
        int startX = (int)vp.WorldToScreenX(committed.MaxX) + 4;
        for (int x = Math.Max(0, startX); x < bmp.Width && !accentPastCommitted; x++)
            for (int y = 0; y < bmp.Height; y++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Blue > c.Red + 20 && c.Blue > c.Green + 10) { accentPastCommitted = true; break; }
            }

        Assert.True(accentPastCommitted,
            "the selection outline should have grown with the preview, not stayed on the committed cell");
    }

    // ── A pinned two-axis grip keeps BOTH axes ───────────────────────────────────────────────

    [Fact]
    public void MKlopfsNearMiddleGrip_DrivesLengthAndOffsetTogether_DespiteHoldingItsAnchor()
    {
        // AsCrossHandle used to drop KeepAnchorFixed, so a pinned grip's cross axis was measured
        // against a frame that does not move and read as dead — silently dropped, with the primary
        // axis still working. The near-end middle grip is exactly that shape.
        var vm = new LayoutEditorViewModel(
            new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 },
            Path.Combine(_workspaceDir, "Doc", "layout", "main.clay"));
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mklopf, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(
            _workspaceDir, "MKLOPF", defaults, null, null, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), Mag = 1.0,
        });
        vm.SelectInstance(0);

        var before = ParametersOf(vm);
        // The NEAR middle grip: two-axis, and the one whose anchor is the far end rather than pin 1.
        var g = Assert.Single(vm.Overlay.PCellHandles, h => h.HasCrossAxis && h.AnchorX != 0);

        vm.OnPointerPressed(g.X, g.Y, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerMoved(g.X - 2_000_000, g.Y - 1_000_000, leftDown: true, KeyModifiers.None, hitTolDbu: 200_000);
        vm.OnPointerReleased(g.X - 2_000_000, g.Y - 1_000_000, KeyModifiers.None);

        var after = ParametersOf(vm);
        Assert.NotEqual(before.Real("L"), after.Real("L"));
        Assert.NotEqual(before.Real("Offset"), after.Real("Offset"));
    }
}
