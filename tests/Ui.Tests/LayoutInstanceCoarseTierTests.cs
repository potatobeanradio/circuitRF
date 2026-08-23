using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  The coarse tier (L2f) — the third and outermost of the compiled-instance draw tiers.
//
//  Stroke elision (L2e) stopped a dense sub-cell from STROKING an outline nobody could see, but it
//  still drew every primitive: zoom a via field out and Skia tessellates and merges tens of thousands
//  of mutually OVERLAPPING grown rectangles per chunk, per placement, to arrive at a solid block. On a
//  design placing one 156,816-via capacitor two dozen times that is 3.7 million rectangles a frame,
//  and it is all thrown away by the rasterizer.
//
//  The coarse tier notices when the answer is already known: a chunk whose grown primitives cover at
//  least as much area as the box holding them (CompiledChunk.CoverageAt) contributes ONE rect — its
//  own bounds — and all such chunks in a layer are batched into a single path. On a uniform field that
//  substitution is not an approximation: coverage is (grown side / pitch)^2, so coverage >= 1 is
//  exactly "adjacent grown primitives touch", and their union IS the bounding box.
//
//  These tests hold three things: it engages where it should and is invisible when it does; it does
//  NOT engage where the primitives are still separately visible; and the batching is real.
// ──────────────────────────────────────────────────────────────────────────────

[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public sealed class LayoutInstanceCoarseTierTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);

    /// <summary>The tier off, for the reference render — see
    /// <c>LayoutRenderOptions.CoarseCoverageThreshold</c>: negative disables outright.</summary>
    private const double TierOff = -1;

    public LayoutInstanceCoarseTierTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfCoarseTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;
    }

    public void Dispose()
    {
        LayoutTextOutline.TestOverrideTypeface = null;
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir)) Directory.Delete(_workspaceDir, recursive: true);
    }

    // FillOpacity 1.0 for the same reason LayoutInstanceChunkCullingTests uses it: a translucent
    // layer composites N batched shapes differently from N separate ones, so a difference in
    // compositing could not be told apart from a difference in what was drawn.
    private static Technology MakeTech() => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers =
        [
            new LayerDef
            {
                Key = LayerA, Name = "L1", Color = new CircuitRF.Ui.Theming.Rgba(255, 0, 0),
                FillOpacity = 1.0, ZOrder = 0, Visible = true, Selectable = true,
            },
        ],
    };

    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    /// <summary>An N x N field of disjoint squares on a uniform pitch — the shape of the real case
    /// (a via array) and the geometry the coverage identity is exact for.</summary>
    private static void AddField(LayoutView v, int side, long pitch, long size)
    {
        for (int r = 0; r < side; r++)
        for (int c = 0; c < side; c++)
            v.Shapes.Add(new RectShape
            {
                Layer = LayerA,
                X1 = c * pitch, Y1 = r * pitch, X2 = c * pitch + size, Y2 = r * pitch + size,
            });
    }

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        var view = MakeView();
        populate(view);
        LayoutPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "main.clay"), view);
        return cellDir;
    }

    private LayoutView PlaceInstance(string cellDir)
    {
        var top = MakeView();
        top.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(_workspaceDir, cellDir),
            X = 0, Y = 0, Rot = LayoutRotation.R0, Mag = 1, Rows = 1, Cols = 1,
        });
        return top;
    }

    private static LayoutRenderResult Render(LayoutView view, Technology tech, LayoutViewport vp, string? baseDir,
                                             out SKColor[] pixels, double coarse = 0)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = baseDir,
            CoarseCoverageThreshold = coarse,
        };
        var result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        pixels = bmp.Pixels;
        return result;
    }

    // ── Where it engages, it must be invisible ─────────────────────────────────────────────────
    //
    // A 200 x 200 field of 0.42 um squares on a 1.26 um pitch — the real generated capacitor's own
    // numbers — viewed at a zoom where the hairline stroke each primitive is grown by is several
    // times the pitch. Every grown square then overlaps its neighbours in both axes, so the elided
    // tier's own output is already the solid block the coarse tier substitutes for it.

    [Fact]
    public void CoarseTier_WhereGrownPrimitivesOverlap_DrawsWhatTheElidedTierWouldHave()
    {
        var cellDir = CreateCell("Dense", v => AddField(v, 200, pitch: 1260, size: 420));
        var tech = MakeTech();
        var top = PlaceInstance(cellDir);

        // 252 um of field across 400 px: one device pixel is 0.63 um, half the pitch, so the
        // half-hairline each primitive grows by is already wider than the gap between two of them.
        var vp = new LayoutViewport(0, 0, 400.0 / 252_000, 400, 400);

        Render(top, tech, vp, _workspaceDir, out var withTier);
        Render(top, tech, vp, _workspaceDir, out var withoutTier, coarse: TierOff);

        // The assertion is about WHERE the two differ, not how much. Inside the field both tiers are
        // painting the same opaque colour over the same pixels and must agree exactly; along its outer
        // edge they are two different ways of antialiasing one boundary — the coarse tier's single
        // grown chunk-bounds rect against the elided tier's outermost grown primitive — and a soft
        // edge landing a fraction of a pixel differently is not a different picture. A tolerance on
        // the WHOLE frame would have accepted a wrong interior; this cannot.
        int interiorDiffering = 0, edgeDiffering = 0;
        int w = (int)vp.Width;
        for (int i = 0; i < withTier.Length; i++)
        {
            if (withTier[i] == withoutTier[i]) continue;
            int x = i % w, y = i / w;
            bool nearEdge = x <= EdgeMarginPx || x >= FieldRightPx - EdgeMarginPx
                            || y <= FieldTopPx + EdgeMarginPx || y >= (int)vp.Height - 1 - EdgeMarginPx;
            if (nearEdge) edgeDiffering++; else interiorDiffering++;
        }

        Assert.Equal(0, interiorDiffering);
        Assert.True(edgeDiffering > 0, "nothing differed anywhere — the tier did not engage, so this proves nothing.");
    }

    /// <summary>How far in from the field's own boundary a pixel stops counting as its antialiased
    /// edge. Two, because the boundary is one hairline stroke wide and can land off a pixel centre.
    /// </summary>
    private const int EdgeMarginPx = 2;

    /// <summary>Where the 200 x 200 field on a 1.26 um pitch ends on screen in the fixture above: the
    /// last primitive starts at 199 * 1.26 um and is 0.42 um wide, over 252 um of viewport in 400 px.
    /// </summary>
    private const int FieldRightPx = 400;

    /// <summary>…and its top edge, which is at the TOP of the canvas because layout Y is up while
    /// screen Y is down — the field starts at world (0,0), i.e. the canvas's bottom-left.</summary>
    private const int FieldTopPx = 0;

    // ── …and it must actually be doing something ───────────────────────────────────────────────
    //
    // Counters, not wall-clock (R-L2a-3, and the same rule LayoutInstanceChunkCullingTests follows).
    // Without the tier this layer issues one draw call per visible chunk; with it, the collapsed
    // chunks are one path and therefore one call for the whole layer.

    [Fact]
    public void CoarseTier_BatchesEveryCollapsedChunkIntoOneDrawCallPerLayer()
    {
        var cellDir = CreateCell("Dense", v => AddField(v, 200, pitch: 1260, size: 420));
        var tech = MakeTech();
        var top = PlaceInstance(cellDir);
        var vp = new LayoutViewport(0, 0, 400.0 / 252_000, 400, 400);

        var withTier = Render(top, tech, vp, _workspaceDir, out _);
        var withoutTier = Render(top, tech, vp, _workspaceDir, out _, coarse: TierOff);

        Assert.Equal(1, withTier.DrawCalls);
        Assert.True(withoutTier.DrawCalls > 100,
            $"the reference render issued {withoutTier.DrawCalls} calls — too few for this to be measuring the batch.");
    }

    // ── Where the primitives are still separately visible, it must NOT engage ──────────────────
    //
    // Two independent ways for that to be true, and both matter: zoomed in far enough that the gaps
    // are wide relative to the stroke (below), and a field sparse enough that they never close at
    // all (the test after it). Either way the render has to be EXACTLY what it was without the tier —
    // not close, since here there is a picture to lose: a grid of dots rather than a block.

    [Fact]
    public void CoarseTier_ZoomedInEnoughToSeeTheGaps_ChangesNoPixel()
    {
        var cellDir = CreateCell("Dense", v => AddField(v, 200, pitch: 1260, size: 420));
        var tech = MakeTech();
        var top = PlaceInstance(cellDir);

        // 25.2 um across 400 px — the pitch is now 20 device pixels and the squares read as dots.
        var vp = new LayoutViewport(0, 0, 400.0 / 25_200, 400, 400);

        Render(top, tech, vp, _workspaceDir, out var withTier);
        Render(top, tech, vp, _workspaceDir, out var withoutTier, coarse: TierOff);

        Assert.Equal(withoutTier, withTier);
    }

    // ── …and the size gate is the other half of that, for a reason coverage alone cannot cover ──
    //
    // Coverage is an AREA measure. Geometry that OVERLAPS itself can sum past its own bounding box
    // while leaving a large part of that box genuinely empty, and substituting the box then paints a
    // hole shut. That is exactly what the size gate is for: the coarse tier is only ever offered a
    // chunk the elision tier already reduced to grown bounding boxes — i.e. one whose primitives are
    // a few device pixels — and at that size there is no hole big enough to see. Take the gate away
    // and this fixture, whose primitives are fifty device pixels, fills a quarter of the frame.

    [Fact]
    public void CoarseTier_OnLargePrimitivesThatOverlapButLeaveAGap_ChangesNoPixel()
    {
        var cellDir = CreateCell("Clumped", v =>
        {
            // 40 heavily overlapping 50 x 50 um squares packed into the left eighth of the extent —
            // their areas sum past the whole bounding box on their own …
            for (int r = 0; r < 5; r++)
            for (int c = 0; c < 8; c++)
                v.Shapes.Add(new RectShape
                {
                    Layer = LayerA,
                    X1 = c * 6_250, Y1 = r * 37_500,
                    X2 = c * 6_250 + 50_000, Y2 = r * 37_500 + 50_000,
                });
            // … and one far away, which is all it takes to stretch the bounding box across a gap
            // five times wider than anything drawn in it.
            v.Shapes.Add(new RectShape { Layer = LayerA, X1 = 350_000, Y1 = 0, X2 = 400_000, Y2 = 50_000 });
        });
        var tech = MakeTech();
        var top = PlaceInstance(cellDir);

        // 1000 DBU per device pixel: every square is 50 px across, far outside the elision tier.
        var vp = new LayoutViewport(0, 0, 400.0 / 400_000, 400, 400);

        Render(top, tech, vp, _workspaceDir, out var withTier);
        Render(top, tech, vp, _workspaceDir, out var withoutTier, coarse: TierOff);

        Assert.Equal(withoutTier, withTier);
    }

    [Fact]
    public void CoarseTier_OnAFieldTooSparseForItsGrownPrimitivesToMeet_ChangesNoPixel()
    {
        // The one fixture that isolates the COVERAGE gate rather than the elision gate above it, and
        // the numbers have to be picked for that: 1 um squares on an 8 um pitch, at 1000 DBU per
        // device pixel. Each primitive is one device pixel, well inside the elision tier — so the
        // coarse tier is being asked, and only coverage can answer no. Grown by a half-hairline on
        // each side a square reaches 3 px against an 8 px pitch, so coverage is (3/8)^2 and the field
        // still reads as separated dots. Slacken the gate and this test paints them as a solid block.
        var cellDir = CreateCell("Sparse", v => AddField(v, 40, pitch: 8_000, size: 1_000));
        var tech = MakeTech();
        var top = PlaceInstance(cellDir);
        var vp = new LayoutViewport(0, 0, 400.0 / 400_000, 400, 400);

        Render(top, tech, vp, _workspaceDir, out var withTier);
        Render(top, tech, vp, _workspaceDir, out var withoutTier, coarse: TierOff);

        Assert.Equal(withoutTier, withTier);
    }
}
