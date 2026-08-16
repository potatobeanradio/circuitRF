using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// WB-C3's two canvases: the wire overlay on the layout editor, snapping to real layout geometry,
/// and hierarchy descent.
/// </summary>
public class WBondOverlayTests
{
    private static WBondDesign Design(int wires = 3)
    {
        var profile = LoopProfile.BallBond(WBondUnits.ToNm(20.0, WBondUnit.Mil), points: 7);
        var design = new WBondDesign();
        design.Profiles.Add(profile);

        var array = new WireArray { Name = "G1", Profile = profile.Name };
        for (int w = 0; w < wires; w++)
            array.Wires.Add(profile.CreateWire(
                Point3.Mils(0, w * 6, 4), Point3.Mils(100, w * 6, 1),
                WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold"));

        design.Arrays.Add(array);
        return design;
    }

    private static LayoutView LayoutWithRect(long x1, long y1, long x2, long y2, int dbuPerMicron = 1000)
    {
        var view = new LayoutView { DbuPerMicron = dbuPerMicron };
        view.Shapes.Add(new RectShape { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Layer = new LayerKey(1, 0) });
        return view;
    }

    // ---------------------------------------------------------------- the unit bridge

    /// <summary>
    /// <b>The one conversion that is invisible until it is wrong.</b> A wire point is nanometres; a
    /// layout coordinate is that layout's own database units. They coincide EXACTLY at the 1,000
    /// DBU/µm default, so an implementation with no conversion at all passes on every default layout.
    /// </summary>
    [Theory]
    [InlineData(1000, 25400, 25400)]   // the default — identity, which is why the bug hides here
    [InlineData(100, 25400, 2540)]     // a coarser layout — a missing conversion is 10x out
    [InlineData(2000, 25400, 50800)]   // a finer one
    public void NanometresCrossIntoTheHostLayoutsOwnUnits(int dbuPerMicron, long nm, long expectedDbu)
    {
        Assert.Equal(expectedDbu, WBondSnap.ToDbu(nm, dbuPerMicron));
        Assert.Equal(nm, WBondSnap.ToNm(expectedDbu, dbuPerMicron));
    }

    /// <summary>
    /// The same wires draw on the same PIXELS whatever the host layout's resolution — a pixel oracle
    /// for the bridge above.
    ///
    /// <para>The fixture is chosen so it can actually fail: three wires 6 mil apart, framed so two of
    /// them are on the canvas. A missing conversion moves the coarse render's wire SPACING by 10x,
    /// which drops the second wire off the canvas entirely. Geometry sitting on x = 0 or y = 0 would
    /// have been invariant under the very scaling under test and proved nothing.</para>
    /// </summary>
    [Fact]
    public void TheOverlayDrawsAtTheSamePhysicalPlace_AtAnyLayoutResolution()
    {
        // 1 µm of world is 1,000 DBU on the fine layout and 100 on the coarse one, so a zoom in
        // px-per-DBU that differs by the same factor puts the two on identical pixels.
        var fine = Render(dbuPerMicron: 1000, zoom: 1e-3);
        var coarse = Render(dbuPerMicron: 100, zoom: 1e-2);

        Assert.NotEmpty(fine);
        Assert.Equal(fine, coarse);

        static List<(int X, int Y)> Render(int dbuPerMicron, double zoom)
        {
            using var surface = SKSurface.Create(new SKImageInfo(400, 300));
            surface.Canvas.Clear(SKColors.Black);

            // Framed in PHYSICAL terms — 20 px of margin and the first wire 100 px up from the bottom
            // — so both viewports show the same physical window at their own resolution.
            var viewport = new LayoutViewport
            {
                Zoom = zoom, PanX = -20.0 / zoom, PanY = -100.0 / zoom, Width = 400, Height = 300,
            };

            WBondRenderer.Draw(surface.Canvas, Design(3), viewport, WBondRenderTheme.Fallback,
                               dbuPerMicron: dbuPerMicron);
            surface.Canvas.Flush();

            using var image = surface.Snapshot();
            using var bitmap = SKBitmap.FromImage(image);

            var lit = new List<(int X, int Y)>();
            for (int y = 0; y < bitmap.Height; y += 2)
                for (int x = 0; x < bitmap.Width; x += 2)
                    if (bitmap.GetPixel(x, y).Red > 32) lit.Add((x, y));
            return lit;
        }
    }

    // ---------------------------------------------------------------- snapping (§6.6)

    /// <summary>A wire point lands on a real bond pad's corner — the whole reason snapping exists here.</summary>
    [Fact]
    public void AWirePoint_SnapsToALayoutRectsCorner()
    {
        var layout = LayoutWithRect(0, 0, 50_000, 50_000);

        // 300 nm off the (50000, 50000) corner, with a 1 mil tolerance.
        var result = WBondSnap.Snap(layout, tech: null, baseDir: "", 50_300, 49_700,
                                    WBondUnits.ToNm(1.0, WBondUnit.Mil));

        Assert.True(result.Snapped);
        Assert.Equal(50_000, result.XNm);
        Assert.Equal(50_000, result.YNm);
        Assert.Equal(SnapFeatureKind.CornerEndpoint, result.Kind);
    }

    /// <summary>Snapping never refuses a placement — out of range returns the raw point unchanged.</summary>
    [Fact]
    public void NothingInRange_ReturnsTheRawPoint()
    {
        var layout = LayoutWithRect(0, 0, 50_000, 50_000);
        var result = WBondSnap.Snap(layout, null, "", 900_000, 900_000, WBondUnits.ToNm(1.0, WBondUnit.Mil));

        Assert.False(result.Snapped);
        Assert.Equal(900_000, result.XNm);
        Assert.Equal(900_000, result.YNm);
    }

    /// <summary>With no layout at all (§10's third entry point) snapping is simply inert.</summary>
    [Fact]
    public void WithNoLayout_SnappingIsInert()
    {
        var result = WBondSnap.Snap(null, null, "", 1234, 5678, 1000);

        Assert.False(result.Snapped);
        Assert.Equal(1234, result.XNm);
        Assert.Equal(5678, result.YNm);
    }

    /// <summary>
    /// The snap runs against the layout's OWN units, so a coarse-resolution layout snaps to the same
    /// physical corner rather than to a point ten times away.
    /// </summary>
    [Fact]
    public void Snapping_HonoursTheLayoutsOwnResolution()
    {
        // 5,000 DBU at 100 DBU/µm is 50 µm = 50,000 nm — the same physical corner as the test above.
        var coarse = LayoutWithRect(0, 0, 5_000, 5_000, dbuPerMicron: 100);

        var result = WBondSnap.Snap(coarse, null, "", 50_300, 49_700, WBondUnits.ToNm(1.0, WBondUnit.Mil));

        Assert.True(result.Snapped);
        Assert.Equal(50_000, result.XNm);
        Assert.Equal(50_000, result.YNm);
    }

    // ---------------------------------------------------------------- descent (WB27)

    /// <summary>Descending one translated instance puts a world point into the sub-cell's own frame.</summary>
    [Fact]
    public void ToFrame_SubtractsTheInstancesOwnPlacement()
    {
        var instance = new LayoutInstance { CellRef = "Sub", X = 10_000, Y = 4_000 };
        var chain = new List<(LayoutInstance, int, int)> { (instance, 0, 0) };

        var (x, y) = WBondDescent.ToFrame(25_000, 9_000, chain, 1000);

        Assert.Equal(15_000, x, 3);
        Assert.Equal(5_000, y, 3);
    }

    /// <summary>Two levels compose, outermost first — which is what "descend" means.</summary>
    [Fact]
    public void ToFrame_ComposesTwoLevels()
    {
        var outer = new LayoutInstance { CellRef = "A", X = 10_000, Y = 0 };
        var inner = new LayoutInstance { CellRef = "B", X = 3_000, Y = 500 };
        var chain = new List<(LayoutInstance, int, int)> { (outer, 0, 0), (inner, 0, 0) };

        var (x, y) = WBondDescent.ToFrame(25_000, 2_000, chain, 1000);

        Assert.Equal(12_000, x, 3);
        Assert.Equal(1_500, y, 3);
    }

    /// <summary>At the base level there is no transform to apply, and the renderer is told so.</summary>
    [Fact]
    public void AtTheBaseLevel_ThereIsNoFrameTransform()
        => Assert.Null(WBondDescent.FrameTransform([], 1000));

    /// <summary>
    /// A push that did not record its instance leaves the chain incomplete, and an incomplete chain
    /// must REFUSE rather than compose a partial transform — wires at a silently wrong offset are
    /// worse than no wires, because judging a foot against the pad under it is the entire point.
    /// </summary>
    [Fact]
    public void AnIncompleteDescentChain_RefusesToPlace()
    {
        var doc = new LayoutDocument("base", new LayoutEditorViewModel(new LayoutView()));
        doc.PushIn(new LayoutEditorViewModel(new LayoutView()), "X1");   // no instance recorded

        Assert.Equal(1, doc.NavDepth);
        Assert.Empty(doc.DescentChain);
        Assert.False(doc.DescentChainIsComplete);
        Assert.False(WBondDescent.CanPlace(doc, new LayoutView(), new LayoutView()));
    }

    /// <summary>A complete chain places; the recorded instances come back in descent order.</summary>
    [Fact]
    public void ACompleteDescentChain_PlacesAndKeepsItsOrder()
    {
        var doc = new LayoutDocument("base", new LayoutEditorViewModel(new LayoutView()));
        var outer = new LayoutInstance { CellRef = "A", X = 1_000 };
        var inner = new LayoutInstance { CellRef = "B", X = 2_000 };

        doc.PushIn(new LayoutEditorViewModel(new LayoutView()), "A", outer);
        doc.PushIn(new LayoutEditorViewModel(new LayoutView()), "B", inner);

        Assert.True(doc.DescentChainIsComplete);
        Assert.Equal(["A", "B"], doc.DescentChain.Select(c => c.Instance.CellRef).ToArray());
        Assert.True(WBondDescent.CanPlace(doc, new LayoutView(), new LayoutView()));
    }

    /// <summary>A resolution change part-way down cannot be composed exactly, so it refuses.</summary>
    [Fact]
    public void ADescentAcrossAResolutionChange_RefusesToPlace()
    {
        var doc = new LayoutDocument("base", new LayoutEditorViewModel(new LayoutView()));
        doc.PushIn(new LayoutEditorViewModel(new LayoutView()), "A", new LayoutInstance { CellRef = "A" });

        Assert.False(WBondDescent.CanPlace(doc,
            new LayoutView { DbuPerMicron = 1000 },
            new LayoutView { DbuPerMicron = 100 }));
    }

    // ---------------------------------------------------------------- the overlay's own routing

    /// <summary>A press on a wire is consumed and selects it; the layout editor never sees it.</summary>
    [Fact]
    public void APressOnAWire_IsConsumed()
    {
        var vm = new WBondViewModel(Design());
        var overlay = new WBondLayoutOverlay(vm);

        var foot = vm.Design.AllWires().First().Points[0];
        bool consumed = overlay.OnPointerPressed(foot.X, foot.Y, WBondUnits.ToNm(3.0, WBondUnit.Mil),
                                                 KeyModifiers.None, clickCount: 2);

        Assert.True(consumed);
        Assert.Equal([0], vm.Selection.Wires);
    }

    /// <summary>
    /// A press on empty space clears the wire selection and starts a wire marquee rather than moving
    /// anything — the two are the same gesture, and only a HIT can ever begin a drag.
    ///
    /// <para>The layout editor is still live underneath: see
    /// <see cref="WithTheWireMarqueeOff_AnEmptyDragFallsThroughToTheLayoutEditor"/> for the toggle
    /// that hands this gesture back to it, and note that a press ON a wire is consumed either way.</para>
    /// </summary>
    [Fact]
    public void APressOnEmptySpace_ClearsTheSelection_AndMovesNothing()
    {
        var vm = new WBondViewModel(Design());
        var overlay = new WBondLayoutOverlay(vm);

        var foot = vm.Design.AllWires().First().Points[0];
        overlay.OnPointerPressed(foot.X, foot.Y, WBondUnits.ToNm(3.0, WBondUnit.Mil), KeyModifiers.None, 2);
        overlay.OnPointerReleased(foot.X, foot.Y);
        Assert.NotEmpty(vm.Selection.Wires);

        long far = WBondUnits.ToNm(9999, WBondUnit.Mil);
        overlay.OnPointerPressed(far, far, WBondUnits.ToNm(3.0, WBondUnit.Mil), KeyModifiers.None, 1);

        Assert.True(vm.Selection.IsEmpty);
        Assert.Equal(foot, vm.Design.AllWires().First().Points[0]);
    }

    /// <summary>
    /// At depth the wires are a locked reference (WB27): every gesture belongs to the layout editor,
    /// so a click on a wire selects nothing and is not consumed.
    /// </summary>
    [Fact]
    public void AtDepth_TheWiresAreNotSelectable()
    {
        var vm = new WBondViewModel(Design());
        var overlay = new WBondLayoutOverlay(vm)
        {
            DescentChain = [(new LayoutInstance { CellRef = "Sub" }, 0, 0)],
        };

        var foot = vm.Design.AllWires().First().Points[0];
        bool consumed = overlay.OnPointerPressed(foot.X, foot.Y, WBondUnits.ToNm(3.0, WBondUnit.Mil),
                                                 KeyModifiers.None, clickCount: 2);

        Assert.True(overlay.IsAtDepth);
        Assert.False(consumed);
        Assert.True(vm.Selection.IsEmpty);
    }

    /// <summary>A drag moves the wire and stays on the incremental path — never a mesh rebuild.</summary>
    [Fact]
    public void ADrag_MovesTheWire_ViaTheIncrementalPath()
    {
        var vm = new WBondViewModel(Design());
        var overlay = new WBondLayoutOverlay(vm) { SnapEnabled = false };

        var foot = vm.Design.AllWires().First().Points[0];
        long startY = foot.Y;
        int rebuilds = vm.RebuildCount;

        overlay.OnPointerPressed(foot.X, foot.Y, WBondUnits.ToNm(3.0, WBondUnit.Mil),
                                 KeyModifiers.None, clickCount: 2);

        long step = WBondUnits.ToNm(1.0, WBondUnit.Mil);
        for (int frame = 1; frame <= 5; frame++)
            overlay.OnPointerMoved(foot.X, foot.Y + frame * step, 0, leftButtonDown: true, KeyModifiers.None);

        overlay.OnPointerReleased(foot.X, foot.Y + 5 * step);

        // The TOTAL displacement is exact even though the first frames were below the drag threshold:
        // deltas are measured from the press point, not from the frame that first crossed it. (With a
        // 3 mil hit tolerance the threshold is 3 mil, so frames 1 and 2 are still a click.)
        Assert.Equal(startY + 5 * step, vm.Design.AllWires().First().Points[0].Y);
        Assert.Equal(rebuilds, vm.RebuildCount);
        Assert.True(vm.IncrementalUpdateCount >= 3,
                    $"the drag must run through the incremental path; {vm.IncrementalUpdateCount} updates");
    }

    /// <summary>Arrow keys are the overlay's only when it has a selection — otherwise they nudge the layout.</summary>
    [Fact]
    public void ArrowKeys_AreClaimedOnlyWhenAWireIsSelected()
    {
        var vm = new WBondViewModel(Design());
        var overlay = new WBondLayoutOverlay(vm);

        Assert.False(overlay.OnKeyDown(Key.Up, KeyModifiers.None));

        var foot = vm.Design.AllWires().First().Points[0];
        overlay.OnPointerPressed(foot.X, foot.Y, WBondUnits.ToNm(3.0, WBondUnit.Mil),
                                 KeyModifiers.None, clickCount: 2);
        long before = vm.Design.AllWires().First().Points[0].Y;

        Assert.True(overlay.OnKeyDown(Key.Up, KeyModifiers.None));
        Assert.Equal(before + WireEdits.DefaultNudgeNm, vm.Design.AllWires().First().Points[0].Y);
    }

    /// <summary>
    /// A right-to-left marquee is CROSSING and promotes to whole wires; left-to-right ENCLOSES and
    /// selects points. The direction comes from the hand, not from a mode the user had to set.
    /// </summary>
    [Fact]
    public void TheMarqueeDirection_DecidesCrossingVersusEnclose()
    {
        var vm = new WBondViewModel(Design());
        var overlay = new WBondLayoutOverlay(vm);

        long left = WBondUnits.ToNm(-10, WBondUnit.Mil);
        long right = WBondUnits.ToNm(60, WBondUnit.Mil);
        long low = WBondUnits.ToNm(-10, WBondUnit.Mil);
        long high = WBondUnits.ToNm(20, WBondUnit.Mil);

        Assert.True(overlay.OnPointerPressed(right, high, 0, KeyModifiers.None, 1));
        overlay.OnPointerMoved(left, low, 0, leftButtonDown: true, KeyModifiers.None);
        Assert.True(overlay.OnPointerReleased(left, low));
        Assert.NotEmpty(vm.Selection.Wires);
        Assert.Empty(vm.Selection.Points);

        Assert.True(overlay.OnPointerPressed(left, low, 0, KeyModifiers.None, 1));
        Assert.True(overlay.OnPointerReleased(right, high));
        Assert.Empty(vm.Selection.Wires);
        Assert.NotEmpty(vm.Selection.Points);
    }

    /// <summary>
    /// With the wire marquee off, a drag on empty space belongs to the layout editor again — the
    /// whole reason the toggle exists, since both marquees want the same gesture.
    /// </summary>
    [Fact]
    public void WithTheWireMarqueeOff_AnEmptyDragFallsThroughToTheLayoutEditor()
    {
        var vm = new WBondViewModel(Design());
        var overlay = new WBondLayoutOverlay(vm) { WireMarqueeEnabled = false };

        long far = WBondUnits.ToNm(9999, WBondUnit.Mil);
        Assert.False(overlay.OnPointerPressed(far, far, 0, KeyModifiers.None, 1));
        Assert.False(overlay.OnPointerMoved(far / 2, far / 2, 0, leftButtonDown: true, KeyModifiers.None));
        Assert.False(overlay.OnPointerReleased(far / 2, far / 2));
    }

    /// <summary>Hold-<c>w</c> promotes a single click to the whole wire, and releasing it stops promoting.</summary>
    [Fact]
    public void HoldingW_PromotesAClickToTheWholeWire_AndReleasingItStops()
    {
        var vm = new WBondViewModel(Design());
        var overlay = new WBondLayoutOverlay(vm);
        var foot = vm.Design.AllWires().First().Points[0];
        long tol = WBondUnits.ToNm(3.0, WBondUnit.Mil);

        overlay.OnKeyDown(Key.W, KeyModifiers.None);
        overlay.OnPointerPressed(foot.X, foot.Y, tol, KeyModifiers.None, clickCount: 1);
        overlay.OnPointerReleased(foot.X, foot.Y);
        Assert.Equal([0], vm.Selection.Wires);

        // The RELEASE is what narrows the selection here, not the press — a press on something already
        // selected picks it up so it can be dragged, and only a gesture that turns out to be a plain
        // click re-resolves (the click-through in WBondLayoutOverlay.EndDrag). Without releasing, the
        // whole-wire selection would still be standing, correctly.
        overlay.OnKeyUp(Key.W, KeyModifiers.None);
        overlay.OnPointerPressed(foot.X, foot.Y, tol, KeyModifiers.None, clickCount: 1);
        Assert.Equal([0], vm.Selection.Wires);

        overlay.OnPointerReleased(foot.X, foot.Y);
        Assert.Empty(vm.Selection.Wires);
        Assert.NotEmpty(vm.Selection.Points);
    }

    // ---------------------------------------------------------------- creation (§6.4)

    /// <summary>
    /// Click the start, click the end, and a wire exists — with the loop the profile generates, not
    /// a straight chord between the feet.
    /// </summary>
    [Fact]
    public void ClickClick_CreatesAWire_WithTheProfilesFullLoop()
    {
        var vm = new WBondViewModel(Design(1));
        var overlay = new WBondLayoutOverlay(vm) { WireDrawArmed = true, SnapEnabled = false };

        int before = vm.Design.AllWires().Count();
        long x0 = WBondUnits.ToNm(200, WBondUnit.Mil);
        long x1 = WBondUnits.ToNm(300, WBondUnit.Mil);

        Assert.True(overlay.OnPointerPressed(x0, 0, 0, KeyModifiers.None, 1));
        Assert.True(overlay.OnPointerMoved(x1, 0, 0, leftButtonDown: false, KeyModifiers.None));
        Assert.True(overlay.OnPointerPressed(x1, 0, 0, KeyModifiers.None, 1));

        var wires = vm.Design.AllWires().ToList();
        Assert.Equal(before + 1, wires.Count);

        var placed = wires[^1];
        Assert.Equal(x0, placed.Points[0].X);
        Assert.Equal(x1, placed.Points[^1].X);

        // The loop is what the profile generates: interior points rise above both feet.
        Assert.True(placed.Points.Count > 2);
        Assert.True(placed.Points.Skip(1).Take(placed.Points.Count - 2).Max(p => p.Z) > placed.Points[0].Z);
    }

    /// <summary>Shift constrains the second click to ortho (§6.4).</summary>
    [Fact]
    public void ShiftConstrainsThePlacementToOrtho()
    {
        var vm = new WBondViewModel(Design(1));
        var overlay = new WBondLayoutOverlay(vm) { WireDrawArmed = true, SnapEnabled = false };

        long x0 = WBondUnits.ToNm(200, WBondUnit.Mil);
        long y0 = WBondUnits.ToNm(50, WBondUnit.Mil);

        overlay.OnPointerPressed(x0, y0, 0, KeyModifiers.None, 1);

        // Mostly-horizontal move with a little y drift — Shift flattens it.
        long x1 = x0 + WBondUnits.ToNm(100, WBondUnit.Mil);
        long y1 = y0 + WBondUnits.ToNm(9, WBondUnit.Mil);
        overlay.OnPointerMoved(x1, y1, 0, leftButtonDown: false, KeyModifiers.Shift);
        overlay.OnPointerPressed(x1, y1, 0, KeyModifiers.Shift, 1);

        var placed = vm.Design.AllWires().Last();
        Assert.Equal(y0, placed.Points[^1].Y);
        Assert.Equal(x1, placed.Points[^1].X);
    }

    /// <summary>Escape abandons a half-placed wire, and nothing is added or left on the undo stack.</summary>
    [Fact]
    public void EscapeDuringPlacement_AddsNothing()
    {
        var vm = new WBondViewModel(Design(1));
        var overlay = new WBondLayoutOverlay(vm) { WireDrawArmed = true, SnapEnabled = false };

        int before = vm.Design.AllWires().Count();
        bool couldUndoBefore = vm.CanUndo;

        overlay.OnPointerPressed(WBondUnits.ToNm(200, WBondUnit.Mil), 0, 0, KeyModifiers.None, 1);
        Assert.True(overlay.OnKeyDown(Key.Escape, KeyModifiers.None));

        Assert.Equal(before, vm.Design.AllWires().Count());
        Assert.Equal(couldUndoBefore, vm.CanUndo);
    }

    /// <summary>
    /// A created wire joins an ARRAY. A wire in no array is drawn, is measured, and is absent from
    /// every published inductance — the reduction sums over arrays (§3.4), so it would go missing
    /// silently.
    /// </summary>
    [Fact]
    public void ACreatedWire_JoinsAnArrayBoundToItsProfile()
    {
        var vm = new WBondViewModel(Design(1));

        int index = vm.AddWire(Point3.Mils(0, 40, 0), Point3.Mils(100, 40, 0),
                               WBondDefaults.ShippedDiameterNm, WBondDefaults.ShippedMaterial);

        Assert.True(index >= 0);
        var array = vm.Design.Arrays.Single(a => a.Wires.Any(w => w.Points[0].Y == Point3.Mils(0, 40, 0).Y));
        Assert.Equal(vm.Design.Profiles[0].Name, array.Profile);
        Assert.Contains(vm.Readout.Rows, r => r.Name == array.Name);
    }

    /// <summary>Creation is structural — one rebuild, and undo removes the wire again.</summary>
    [Fact]
    public void CreatingAWire_IsStructural_AndUndoable()
    {
        var vm = new WBondViewModel(Design(1));
        int rebuilds = vm.RebuildCount;
        int before = vm.Design.AllWires().Count();

        vm.AddWire(Point3.Mils(0, 40, 0), Point3.Mils(100, 40, 0),
                   WBondDefaults.ShippedDiameterNm, WBondDefaults.ShippedMaterial);

        Assert.Equal(rebuilds + 1, vm.RebuildCount);
        Assert.Equal(before + 1, vm.Design.AllWires().Count());

        vm.Undo();
        Assert.Equal(before, vm.Design.AllWires().Count());
    }

    /// <summary>
    /// A design carrying only FREE wires has no profile to generate a loop from, so creation makes
    /// one rather than refusing. (An entirely empty design is not reachable here — <c>Validate</c>
    /// refuses a design with no arrays, because an empty array makes the array-basis inductance
    /// singular.)
    /// </summary>
    [Fact]
    public void CreatingIntoAProfilelessDesign_MakesAProfile()
    {
        var design = new WBondDesign();
        var free = new Wire { DiameterNm = WBondDefaults.ShippedDiameterNm, Material = "Gold" };
        free.Points.AddRange([Point3.Mils(0, 0, 0), Point3.Mils(50, 0, 5), Point3.Mils(100, 0, 0)]);
        design.Arrays.Add(new WireArray { Name = "G1", Wires = { free } });

        var vm = new WBondViewModel(design);
        Assert.Empty(vm.Design.Profiles);

        int index = vm.AddWire(Point3.Mils(0, 40, 0), Point3.Mils(100, 40, 0),
                               WBondDefaults.ShippedDiameterNm, WBondDefaults.ShippedMaterial,
                               pointsIfProfileCreated: 7);

        Assert.True(index >= 0);
        Assert.Single(vm.Design.Profiles);
        Assert.Equal(7, vm.Design.AllWires().Last().Points.Count);
    }

    /// <summary>An out-of-range stored point count is clamped, not trusted — two points is no loop at all.</summary>
    [Theory]
    [InlineData(null, WBondDefaults.ShippedPoints)]
    [InlineData(2, 3)]
    [InlineData(500, 101)]
    [InlineData(9, 9)]
    public void AStoredPointCount_IsClamped(int? stored, int expected)
        // The resolver's own clamp, not a copy of it here — reading WBondDefaults.Points instead
        // would test whatever this machine's preferences file happens to say.
        => Assert.Equal(expected, WBondDefaults.Clamp(stored));

    // ---------------------------------------------------------------- shared edit primitive

    /// <summary>
    /// A drag and a nudge are one implementation. Anything else is two chances to disagree about
    /// which points a selection actually moves, which is exactly the rule that can be wrong.
    /// </summary>
    [Fact]
    public void TranslateAndNudge_AgreeForTheSameDisplacement()
    {
        var dragged = Design(1);
        var nudged = Design(1);
        var selection = new WireSelection { Wires = { 0 } };

        WireEdits.Translate(dragged, selection, 3 * WireEdits.DefaultNudgeNm, 0, EditorView.Layout);
        WireEdits.Nudge(nudged, selection, 3, 0, WireEdits.DefaultNudgeNm, EditorView.Layout);

        Assert.Equal(nudged.AllWires().First().Points, dragged.AllWires().First().Points);
    }

    /// <summary>
    /// A gesture is ONE undo entry. Without this a single alt-drag would leave sixty of them and
    /// Ctrl+Z would walk back through the drag a frame at a time instead of undoing it.
    /// </summary>
    [Fact]
    public void AGesture_CollapsesToOneUndoEntry()
    {
        var vm = new WBondViewModel(Design(1));
        long start = vm.Design.AllWires().First().Points[0].Y;
        var selection = new WireSelection { Wires = { 0 } };
        vm.Selection = selection;

        vm.BeginGesture();
        for (int frame = 0; frame < 20; frame++) vm.NudgeSelection(0, 1, coarse: false, EditorView.Layout);
        vm.EndGesture();

        Assert.NotEqual(start, vm.Design.AllWires().First().Points[0].Y);

        vm.Undo();
        Assert.Equal(start, vm.Design.AllWires().First().Points[0].Y);
        Assert.False(vm.CanUndo);
    }
}
