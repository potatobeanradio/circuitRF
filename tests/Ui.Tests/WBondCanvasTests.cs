using System;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// WB-C3's Avalonia half — the overlay renderer and the pointer controller.
/// </summary>
public class WBondCanvasTests
{
    private static WBondDesign Design(int wires = 4, int arrays = 1)
    {
        long loopNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var design = new WBondDesign();

        for (int a = 0; a < arrays; a++)
        {
            var array = new WireArray { Name = $"G{a + 1}" };
            for (int w = 0; w < wires; w++)
            {
                double y = a * 200 + w * 6;
                array.Wires.Add(LoopShape.CreateSeedWire(
                    Point3.Mils(0, y, 4), Point3.Mils(100, y, 1),
                    WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopHeightNm: loopNm));
            }
            design.Arrays.Add(array);
        }
        return design;
    }

    private static LayoutViewport Viewport() =>
        new() { Zoom = 0.5, PanX = -100, PanY = -100, Width = 800, Height = 600 };

    private static (SKSurface Surface, SKCanvas Canvas) Target()
    {
        var surface = SKSurface.Create(new SKImageInfo(800, 600));
        return (surface, surface.Canvas);
    }

    // ---------------------------------------------------------------- the overlay renderer

    /// <summary>Every wire and every vertex is drawn — the counts are the contract.</summary>
    [Fact]
    public void TheOverlay_DrawsEveryWireAndVertex()
    {
        var (surface, canvas) = Target();
        using (surface)
        {
            var design = Design(wires: 4);
            var result = WBondRenderer.Draw(canvas, design, Viewport(), WBondRenderTheme.Fallback);

            Assert.Equal(4, result.WiresDrawn);
            Assert.Equal(4 * 6, result.SegmentsDrawn);   // 7 points -> 6 segments
            Assert.Equal(4 * 7, result.DotsDrawn);
        }
    }

    /// <summary>
    /// <b>True-diameter mode has a 1 px floor.</b> Without it a 1 mil wire vanishes entirely at
    /// whole-package zoom, which is exactly when a user is most likely to be hunting for it.
    ///
    /// <para>The zoom figures here are <b>device pixels per DBU</b>, which is what
    /// <see cref="LayoutViewport"/> actually means — at the 1,000 DBU/µm default one DBU is one
    /// nanometre, so a 1 mil wire is 25,400 DBU across.</para>
    /// </summary>
    [Fact]
    public void TrueDiameterMode_NeverDrawsThinnerThanAPixel()
    {
        var wire = Design().AllWires().First();
        var theme = WBondRenderTheme.Fallback;

        // Zoomed far out: 25,400 DBU at 1e-6 px/DBU is 0.025 px — well under one pixel.
        var wayOut = new LayoutViewport { Zoom = 1e-6, Width = 800, Height = 600 };
        Assert.Equal(1.0f, WBondRenderer.StrokeWidth(wire, wayOut, theme, WireThicknessMode.TrueDiameter));

        // Zoomed in: it scales with the view, which is the whole point of the mode.
        var zoomedIn = new LayoutViewport { Zoom = 1e-3, Width = 800, Height = 600 };
        float wide = WBondRenderer.StrokeWidth(wire, zoomedIn, theme, WireThicknessMode.TrueDiameter);
        Assert.True(wide > 20.0f, $"A 1 mil wire at 1e-3 px/DBU should be ~25 px; got {wide}.");
    }

    /// <summary>
    /// <b>The DBU bridge is not free, and this is the test that proves it.</b> A wire point is stored
    /// in nanometres; the layout viewport works in the host layout's own database units. At the
    /// 1,000 DBU/µm default the two coincide exactly — so an overlay that simply passed nanometres
    /// through would pass every test written on a default layout and put every wire ten times out of
    /// place on a 100 DBU/µm one.
    /// </summary>
    [Fact]
    public void TrueDiameterMode_ScalesWithTheHostLayoutsResolution()
    {
        var wire = Design().AllWires().First();
        var theme = WBondRenderTheme.Fallback;
        var viewport = new LayoutViewport { Zoom = 1e-3, Width = 800, Height = 600 };

        float atDefault = WBondRenderer.StrokeWidth(wire, viewport, theme, WireThicknessMode.TrueDiameter, 1000);
        float atCoarse  = WBondRenderer.StrokeWidth(wire, viewport, theme, WireThicknessMode.TrueDiameter, 100);

        Assert.Equal(atDefault / 10.0f, atCoarse, 3);
    }

    /// <summary>
    /// <b>Thin mode scales with zoom too</b> (owner, 2026-08-16: "as I zoom in, the wire segment and
    /// wire vertex is supposed to render bigger").
    ///
    /// <para>This test used to assert the opposite — that the mode "ignores zoom entirely, which is
    /// what makes it the safe default" — and the half of that which was really load-bearing is the
    /// FLOOR, not the constancy: a 1 mil wire must still be visible at whole-package zoom. It keeps
    /// the floor and drops the ceiling. What separates the two modes now is WIDTH, not whether the
    /// wire grows: thin is a fixed fraction of the real diameter, Ø is the real diameter.</para>
    /// </summary>
    [Fact]
    public void ThinMode_ScalesWithZoomButNeverBelowItsFloor()
    {
        var wire = Design().AllWires().First();
        var theme = WBondRenderTheme.Fallback;

        float zoomedOut = WBondRenderer.StrokeWidth(wire, new LayoutViewport { Zoom = 1e-9 }, theme, WireThicknessMode.Thin);
        float middling  = WBondRenderer.StrokeWidth(wire, new LayoutViewport { Zoom = 1e-3 }, theme, WireThicknessMode.Thin);
        float zoomedIn  = WBondRenderer.StrokeWidth(wire, new LayoutViewport { Zoom = 1e-1 }, theme, WireThicknessMode.Thin);

        Assert.Equal(theme.LineWidthPx, zoomedOut);        // the floor — a sub-pixel wire stays visible
        Assert.True(zoomedIn > middling, "Thin mode stopped growing with zoom.");
        Assert.True(middling >= theme.LineWidthPx);

        // …and it is genuinely THINNER than true diameter at the same zoom, or the toggle says nothing.
        float trueDiameter = WBondRenderer.StrokeWidth(wire, new LayoutViewport { Zoom = 1e-1 }, theme, WireThicknessMode.TrueDiameter);
        Assert.True(zoomedIn < trueDiameter);
    }

    /// <summary>
    /// The profile view draws ONE curve per array plus the BAND for its bound members, and individual
    /// curves for free wires — the clutter answer (§6.2 idea 3). 200 bound wires must not become 200
    /// curves; they must not become ZERO curves either.
    ///
    /// <para><b>The representative curve is the half that was missing, and its absence was visible as
    /// a blank view.</b> The band is a min/max envelope: with one bound member — or with several that
    /// momentarily share a shape, which is exactly what the quality ladder produces when it collapses
    /// them onto their chords mid-drag — min equals max at every sample and the band is a zero-area
    /// path that fills nothing. With the bound members also skipped, the array rendered as nothing at
    /// all. That is the owner's "the profile view sometimes disappears while dragging".</para>
    /// </summary>
    [Fact]
    public void TheProfileView_DrawsOneCurvePerArrayPlusTheBand_NotEveryMember()
    {
        var (surface, canvas) = Target();
        using (surface)
        {
            var design = Design(wires: 20);

            var result = WBondRenderer.DrawProfile(
                canvas, design, WBondRenderTheme.Fallback,
                span => (float)(span / 1000.0), z => (float)(600 - z / 1000.0));

            // All 20 share one shape and project onto one curve: one representative, not 20 and not 0.
            Assert.Equal(1, result.WiresDrawn);

            // Reshape two and they appear alongside the representative — presence is a function of
            // GEOMETRY, which since 2026-08-18 is the only thing it can be a function of.
            WireEdits.ScaleHeightAboutChord(design.Arrays[0].Wires[3], 1.8);
            WireEdits.ScaleHeightAboutChord(design.Arrays[0].Wires[7], 0.4);

            var after = WBondRenderer.DrawProfile(
                canvas, design, WBondRenderTheme.Fallback,
                span => (float)(span / 1000.0), z => (float)(600 - z / 1000.0));

            Assert.Equal(3, after.WiresDrawn);
        }
    }

    /// <summary>
    /// A ONE-WIRE array — the shipped default document — puts pixels on the profile canvas.
    ///
    /// <para>The pixel oracle is the point: the counter above says a curve was emitted, but the bug
    /// this guards was a path that was emitted and filled nothing. Only a rendered surface can tell
    /// those apart, and a single bound wire is the smallest case that produces a degenerate band.</para>
    /// </summary>
    [Fact]
    public void TheProfileView_OfASingleBoundWire_ActuallyMarksTheSurface()
    {
        var (surface, canvas) = Target();
        using (surface)
        {
            canvas.Clear(SKColors.Black);

            using var before = surface.Snapshot();
            using var beforeBitmap = SKBitmap.FromImage(before);
            int beforeLit = CountLitPixels(beforeBitmap);

            WBondRenderer.DrawProfile(
                canvas, Design(wires: 1), WBondRenderTheme.Fallback,
                span => (float)(span / 1000.0), z => (float)(600 - z / 1000.0));
            canvas.Flush();

            using var after = surface.Snapshot();
            using var afterBitmap = SKBitmap.FromImage(after);

            Assert.True(CountLitPixels(afterBitmap) > beforeLit + 50,
                        "A single bound wire must still draw a profile curve.");
        }
    }

    /// <summary>Rendering actually puts pixels on the surface — a smoke test against a silent no-op.</summary>
    [Fact]
    public void TheOverlay_ActuallyMarksTheSurface()
    {
        var (surface, canvas) = Target();
        using (surface)
        {
            canvas.Clear(SKColors.Black);

            using var before = surface.Snapshot();
            using var beforeBitmap = SKBitmap.FromImage(before);
            int beforeLit = CountLitPixels(beforeBitmap);

            WBondRenderer.Draw(canvas, Design(wires: 3), Viewport(), WBondRenderTheme.Fallback);
            canvas.Flush();

            using var after = surface.Snapshot();
            using var afterBitmap = SKBitmap.FromImage(after);
            int afterLit = CountLitPixels(afterBitmap);

            Assert.True(afterLit > beforeLit + 100,
                $"Drawing 3 wires should light up the surface; {beforeLit} -> {afterLit} lit pixels.");
        }
    }

    private static int CountLitPixels(SKBitmap bitmap)
    {
        int lit = 0;
        for (int y = 0; y < bitmap.Height; y += 2)
            for (int x = 0; x < bitmap.Width; x += 2)
                if (bitmap.GetPixel(x, y).Red > 32) lit++;
        return lit;
    }

    // ---------------------------------------------------------------- the pointer controller

    /// <summary>Modifiers win over click count, so `g` gives the group on the first click.</summary>
    [Theory]
    [InlineData(WBondModifiers.None, 1, SelectionScope.Element)]
    [InlineData(WBondModifiers.None, 2, SelectionScope.Wire)]
    [InlineData(WBondModifiers.None, 3, SelectionScope.Array)]
    [InlineData(WBondModifiers.WholeWire, 1, SelectionScope.Wire)]
    [InlineData(WBondModifiers.WholeGroup, 1, SelectionScope.Array)]
    [InlineData(WBondModifiers.WholeGroup, 2, SelectionScope.Array)]
    public void ThePromotionRule_PrefersModifiersOverClickCount(
        WBondModifiers modifiers, int clicks, SelectionScope expected)
    {
        Assert.Equal(expected, WBondPointerController.ScopeFor(modifiers, clicks));
    }

    /// <summary>A click on a wire selects it; a click on empty space clears the selection.</summary>
    [Fact]
    public void AClick_SelectsAndAClickAway_Clears()
    {
        var vm = new WBondViewModel(Design(wires: 3));
        var controller = new WBondPointerController(vm);

        var foot = vm.Design.AllWires().First().Points[0];
        controller.Press(foot.X, foot.Y, WBondUnits.ToNm(3.0, WBondUnit.Mil),
                         WBondModifiers.WholeWire, view: EditorView.Layout);

        Assert.Equal([0], vm.Selection.Wires);

        controller.Press(WBondUnits.ToNm(9999, WBondUnit.Mil), WBondUnits.ToNm(9999, WBondUnit.Mil),
                         WBondUnits.ToNm(3.0, WBondUnit.Mil), WBondModifiers.None);

        Assert.True(vm.Selection.IsEmpty);
    }

    /// <summary>Shift-clicking empty space keeps what was selected, rather than clearing it.</summary>
    [Fact]
    public void ShiftClickingEmptySpace_KeepsTheSelection()
    {
        var vm = new WBondViewModel(Design(wires: 3));
        var controller = new WBondPointerController(vm);

        var foot = vm.Design.AllWires().First().Points[0];
        controller.Press(foot.X, foot.Y, WBondUnits.ToNm(3.0, WBondUnit.Mil), WBondModifiers.WholeWire);
        Assert.Single(vm.Selection.Wires);

        controller.Press(WBondUnits.ToNm(9999, WBondUnit.Mil), WBondUnits.ToNm(9999, WBondUnit.Mil),
                         WBondUnits.ToNm(3.0, WBondUnit.Mil), WBondModifiers.Shift);

        Assert.Single(vm.Selection.Wires);
    }

    /// <summary>
    /// <b>The marquee's direction comes from the hand, not a mode.</b> Right → left is crossing and
    /// catches whole wires; left → right is enclose.
    /// </summary>
    [Fact]
    public void TheMarqueeDirection_ComesFromThePressAndRelease()
    {
        var vm = new WBondViewModel(Design(wires: 3));
        var controller = new WBondPointerController(vm);

        long left = WBondUnits.ToNm(-10, WBondUnit.Mil);
        long right = WBondUnits.ToNm(60, WBondUnit.Mil);
        long low = WBondUnits.ToNm(-10, WBondUnit.Mil);
        long high = WBondUnits.ToNm(20, WBondUnit.Mil);

        // Drag right -> left: crossing, so partly-covered wires come back WHOLE.
        controller.Press(right, high, 0, WBondModifiers.None);
        controller.Marquee(left, low, WBondModifiers.None);
        Assert.NotEmpty(vm.Selection.Wires);
        Assert.Empty(vm.Selection.Points);

        // Drag left -> right over the same box: enclose, so they come back as points.
        controller.Press(left, low, 0, WBondModifiers.None);
        controller.Marquee(right, high, WBondModifiers.None);
        Assert.Empty(vm.Selection.Wires);
        Assert.NotEmpty(vm.Selection.Points);
    }

    /// <summary>
    /// A drag frame commits through the incremental path and never rebuilds.
    ///
    /// <para><b>The ladder is made inert with an unreachable frame budget</b>, and it has to be: this
    /// looks like a pure counter assertion but is not. The <c>QualityLadder</c> is fed measured
    /// wall-clock, so under a full-solution run a frame overruns 16.7 ms, the ladder drops to
    /// <c>FreezeAndSnap</c>, <c>DragFrame</c> stops calling <c>CommitPointMove</c> — and
    /// <c>IncrementalUpdateCount</c> stops rising while nothing at all is wrong. Observed failing that
    /// way, 2026-08-16. The invariant this test exists for (a point move must not take the structural
    /// path) has nothing to do with how busy the machine is, so the timing is removed rather than the
    /// test being tagged out of the routine gate.</para>
    /// </summary>
    [Fact]
    public void ADragFrame_UsesTheIncrementalPath()
    {
        var vm = new WBondViewModel(Design(wires: 4));
        var controller = new WBondPointerController(vm, frameBudgetMs: 1e9);

        vm.Selection = new WireSelection { Wires = { 0 } };
        int rebuilds = vm.RebuildCount;

        controller.BeginDrag();
        for (int frame = 0; frame < 10; frame++)
        {
            controller.DragFrame(moving =>
            {
                var wires = vm.Design.AllWires().ToList();
                foreach (int i in moving)
                {
                    var p = wires[i].Points[3];
                    wires[i].Points[3] = p with { Z = p.Z + 1000 };
                }
            });
        }
        controller.EndDrag();

        Assert.Equal(rebuilds, vm.RebuildCount);
        Assert.True(vm.IncrementalUpdateCount >= 10);
    }

    /// <summary>Every drag begins at the top rung and its readout starts non-provisional.</summary>
    [Fact]
    public void ADrag_BeginsExactAndNonProvisional()
    {
        var vm = new WBondViewModel(Design());
        var controller = new WBondPointerController(vm);

        controller.BeginDrag();
        Assert.Equal(DragQuality.Exact, controller.Quality);
        Assert.False(controller.ReadoutIsProvisional);
        Assert.True(controller.IsDragging);

        controller.EndDrag();
        Assert.False(controller.IsDragging);
    }

    /// <summary>A drag frame outside a drag is a caller error and says so.</summary>
    [Fact]
    public void ADragFrameOutsideADrag_IsRefused()
    {
        var vm = new WBondViewModel(Design());
        var controller = new WBondPointerController(vm);
        vm.Selection = new WireSelection { Wires = { 0 } };

        var ex = Assert.Throws<InvalidOperationException>(() => controller.DragFrame(_ => { }));
        Assert.Contains("BeginDrag", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>EndDrag always recomputes exactly</b>, even if the drag never degraded — a caller must not
    /// have to know which rungs were used.
    /// </summary>
    [Fact]
    public void EndDrag_AlwaysRecomputesTheFinalAnswer()
    {
        var vm = new WBondViewModel(Design(wires: 3));
        var controller = new WBondPointerController(vm);
        vm.Selection = new WireSelection { Wires = { 0 } };

        controller.BeginDrag();
        int before = vm.IncrementalUpdateCount;
        controller.EndDrag();

        Assert.True(vm.IncrementalUpdateCount > before,
            "EndDrag must publish a final, non-provisional answer.");
    }
}
