using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Compiled-cell chunking + stroke elision (the L2e "dense PCell" work).
//
//  A compiled sub-cell used to be ONE aggregate path per layer. Path CONSTRUCTION was therefore a
//  once-per-cell cost — which is what the compile cache was for and it worked — but RASTERIZATION
//  stayed proportional to the cell's TOTAL geometry at every zoom, because Skia walks every segment
//  of a path it is handed. Measured on a real design whose MIM capacitor carries 24,964 via
//  rects: 127 ms/frame at Zoom-to-Fit and still 43 ms/frame zoomed 256x in, with 640 vias on screen.
//
//  Two changes, and these tests hold both:
//    1. Chunking + culling — the layer is split into a grid, each chunk carrying its own bounds, and
//       a chunk outside the viewport is skipped. This must be PIXEL-IDENTICAL to drawing everything.
//    2. Stroke elision — a chunk whose largest primitive is under a few device pixels draws as one
//       solid grown fill rather than a fill pass plus a per-primitive outline pass. Stroking is where
//       the time went (82 ms of a 102 ms layer, tessellating outlines for ~100k segments) and at that
//       size the outline IS the whole visible shape, so the grown fill has to cover the same pixels.
// ──────────────────────────────────────────────────────────────────────────────

[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public sealed class LayoutInstanceChunkCullingTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);

    public LayoutInstanceChunkCullingTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfChunkTest_" + Guid.NewGuid().ToString("N")[..8]);
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

    // FillOpacity 1.0 on purpose: a partially transparent fill composites differently when N shapes
    // are batched into one path than when each is drawn separately, so a translucent layer could not
    // tell a culling bug apart from an ordinary alpha-compositing difference. Opaque + disjoint
    // geometry (below) makes the two tiers genuinely comparable, which is what gives the pixel
    // assertion its teeth.
    private static Technology MakeTech(double fillOpacity = 1.0) => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers = [new LayerDef { Key = LayerA, Name = "L1", Color = new CircuitRF.Design.Theming.Rgba(255, 0, 0), FillOpacity = fillOpacity, ZOrder = 0, Visible = true, Selectable = true }],
    };

    /// <summary>An N x N field of L-shaped polygons. Deliberately NOT rectangles: a grown bounding box
    /// is EXACTLY what a fill-plus-outline pass paints for an opaque axis-aligned rect — same pixels,
    /// same colour — so a rect field cannot tell the elided tier from the exact one no matter how far
    /// in you zoom, and a test built on one silently proves nothing. (Found by mutation: raising the
    /// engagement threshold a thousandfold changed not one pixel of a rect fixture.) An L has interior
    /// the bbox does not, and a partially transparent fill makes that interior a different colour from
    /// the outline, so the substitution becomes visible the moment it happens.</summary>
    private static void AddPolyField(LayoutView v, int side, long pitch = 1000, long size = 600)
    {
        long h = size / 3;
        for (int r = 0; r < side; r++)
        for (int c = 0; c < side; c++)
        {
            long x = c * pitch, y = r * pitch;
            v.Shapes.Add(new PolygonShape
            {
                Layer = LayerA,
                Xy = [x, y, x + size, y, x + size, y + h, x + h, y + h, x + h, y + size, x, y + size],
            });
        }
    }

    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    /// <summary>An N x N field of disjoint squares — the shape of the real problem case (a via array),
    /// and enough primitives that the compile splits it across several chunks rather than one.</summary>
    private static void AddField(LayoutView v, int side, long pitch = 1000, long size = 500)
    {
        for (int r = 0; r < side; r++)
        for (int c = 0; c < side; c++)
            v.Shapes.Add(new RectShape
            {
                Layer = LayerA,
                X1 = c * pitch, Y1 = r * pitch,
                X2 = c * pitch + size, Y2 = r * pitch + size,
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

    private static LayoutRenderResult Render(LayoutView view, Technology tech, LayoutViewport vp, string? baseDir,
                                             out byte[] pixels, double elision = 0)
    {
        var result = RenderCore(view, tech, vp, baseDir, elision, out var bmp);
        using (bmp) pixels = bmp.Bytes;
        return result;
    }

    /// <summary>Same render, handed back as normalized <see cref="SKColor"/>s rather than raw bytes.
    /// The raw buffer's channel ORDER is platform-dependent (Rgba8888 here, Bgra8888 elsewhere), which
    /// is fine for comparing two renders against each other but silently wrong for comparing a pixel
    /// against a named colour: the Light theme's background is #F6F6F4, whose red and blue differ, so
    /// reading it in the wrong order marks every background pixel as painted and any "is this pixel
    /// painted" test built on it passes no matter what the renderer did. That is not hypothetical —
    /// it is what the first version of the elision test below actually did.</summary>
    private static LayoutRenderResult RenderColors(LayoutView view, Technology tech, LayoutViewport vp,
                                                   string? baseDir, out SKColor[] pixels, double elision = 0)
    {
        var result = RenderCore(view, tech, vp, baseDir, elision, out var bmp);
        using (bmp) pixels = bmp.Pixels;
        return result;
    }

    private static LayoutRenderResult RenderCore(LayoutView view, Technology tech, LayoutViewport vp,
                                                 string? baseDir, double elision, out SKBitmap bmp)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = baseDir,
            StrokeElisionPixelThreshold = elision,
        };
        var result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        using var img = surface.Snapshot();
        bmp = SKBitmap.FromImage(img);
        return result;
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

    // ── Culling must never change a pixel ──────────────────────────────────────────────────────
    //
    // The reference is the SAME geometry drawn as top-level shapes, which has no chunking and no
    // culling of its own to go wrong — an independent path to the same picture, not a second call
    // into the code under test. 1,600 shapes stays under DefaultMergeShapeCountThreshold so the
    // top-level side draws per-shape, and the squares are 10 device pixels here so neither side's
    // sub-pixel LOD tier engages: what is compared is real geometry against real geometry.

    [Theory]
    [InlineData(0, 0, 40)]       // full extent — every chunk visible, nothing culled
    [InlineData(5, 5, 10)]       // zoomed in, interior — most chunks culled
    [InlineData(0, 0, 8)]        // zoomed in, hard against the origin corner
    [InlineData(32, 32, 12)]     // zoomed in, past the far corner
    public void ChunkCulling_IsPixelIdenticalToTheSameGeometryDrawnWithoutIt(double leftUm, double bottomUm, double spanUm)
    {
        const int Side = 40;
        var cellDir = CreateCell("Field", v => AddField(v, Side));
        var tech = MakeTech();

        var flat = MakeView();
        AddField(flat, Side);

        var vp = new LayoutViewport(leftUm * 1000, bottomUm * 1000, 800.0 / (spanUm * 1000), 800, 800);

        Render(PlaceInstance(cellDir), tech, vp, _workspaceDir, out var viaInstance, elision: -1);
        Render(flat, tech, vp, null, out var flattened, elision: -1);

        Assert.Equal(flattened, viaInstance);
    }

    // ── Culling must actually happen ───────────────────────────────────────────────────────────
    //
    // Counters, not wall-clock: this codebase's own established rule (R-L2a-3) is that a deterministic
    // work count is the per-commit gate and a millisecond is the diagnostic. Before chunking, a
    // compiled layer was one path and one draw call at EVERY zoom, so this ratio was flat at 1.0 —
    // which is precisely the defect. Disabling the culling query in DrawInstances turns this red.

    [Fact]
    public void ChunkCulling_ZoomedIn_IssuesFarFewerDrawCallsThanFullExtent()
    {
        var cellDir = CreateCell("Dense", v => AddField(v, 64));   // 4,096 primitives -> a 4x4 chunk grid
        var tech = MakeTech();
        var top = PlaceInstance(cellDir);

        var fullExtent = new LayoutViewport(0, 0, 800.0 / 64_000, 800, 800);
        var zoomedIn = new LayoutViewport(30_000, 30_000, 800.0 / 8_000, 800, 800);

        var wide = Render(top, tech, fullExtent, _workspaceDir, out _, elision: -1);
        var near = Render(top, tech, zoomedIn, _workspaceDir, out _, elision: -1);

        Assert.True(near.DrawCalls * 3 < wide.DrawCalls,
            $"zoomed-in view issued {near.DrawCalls} draw calls against {wide.DrawCalls} at full extent — " +
            "culling inside the compiled cell is not engaging");
    }

    // ── Culling must not clip at the viewport edge ─────────────────────────────────────────────
    //
    // A chunk whose bounds sit just outside the viewport can still paint into it, because the stroke
    // has width and antialiasing softens past that. Draw's margin-expanded query rect is what covers
    // it; this pins that the margin is carried into the per-chunk test too, rather than the chunk
    // bounds being compared against a bare viewport.

    [Fact]
    public void ChunkCulling_KeepsGeometryWhoseStrokeBleedsInFromJustOffScreen()
    {
        var cellDir = CreateCell("Edge", v => AddField(v, 40));
        var tech = MakeTech();

        // Aimed at a CHUNK boundary, not just any edge — with 1,600 primitives the compile builds a
        // 3x3 grid whose first column of chunks ends at x = 12,500 dbu (the right edge of the last
        // square whose CENTRE falls in that column). A viewport starting exactly there leaves that
        // chunk's bounds touching, not overlapping, so it is culled the moment the test rect is the
        // bare viewport — while its outline still paints a pixel back into column 0. That is the case
        // Draw's margin exists for, and without a boundary this precise the test cannot see it: chunk
        // bounds span hundreds of primitives, so an arbitrary edge almost always lands mid-chunk and
        // passes either way.
        var vp = new LayoutViewport(12_500, 10_000, 800.0 / 8_000, 800, 800);
        Render(PlaceInstance(cellDir), tech, vp, _workspaceDir, out var withInstance, elision: -1);
        Render(Flat(40), tech, vp, null, out var flattened, elision: -1);

        Assert.Equal(flattened, withInstance);

        LayoutView Flat(int side) { var v = MakeView(); AddField(v, side); return v; }
    }

    // ── Stroke elision must cover the same pixels the outline did ──────────────────────────────
    //
    // The tier exists to stop tessellating an outline that, at these sizes, IS the shape. So the test
    // is a FOOTPRINT comparison against the exact-geometry render of the identical frame: the painted
    // extent must not shrink or grow by more than a pixel. Nothing here asserts the two are
    // pixel-identical — they are not, and are not meant to be; the claim is that the substitution is
    // invisible at the size it engages at, and footprint is what would betray it.

    [Fact]
    public void StrokeElision_PaintsTheSameFootprintAsTheOutlineItReplaces()
    {
        var cellDir = CreateCell("Tiny", v => AddField(v, 64, pitch: 400, size: 120));
        var tech = MakeTech();
        var top = PlaceInstance(cellDir);

        // 64 * 400 dbu = 25.6 um across 800 px -> ~31 px/um, so a 0.12 um square is ~3.7 device
        // pixels: inside DefaultStrokeElisionDevicePixels, which is the regime under test.
        var vp = new LayoutViewport(0, 0, 800.0 / 25_600, 800, 800);

        var elidedResult = RenderColors(top, tech, vp, _workspaceDir, out var elided);
        var exactResult = RenderColors(top, tech, vp, _workspaceDir, out var exact, elision: -1);

        var (e, x) = (PaintedBounds(elided, 800, 800), PaintedBounds(exact, 800, 800));
        Assert.True(e.HasValue && x.HasValue, "both renders must paint something");
        Assert.True(Math.Abs(e!.Value.Left - x!.Value.Left) <= 1
                 && Math.Abs(e.Value.Top - x.Value.Top) <= 1
                 && Math.Abs(e.Value.Right - x.Value.Right) <= 1
                 && Math.Abs(e.Value.Bottom - x.Value.Bottom) <= 1,
            $"elided footprint {e} differs from exact footprint {x} by more than a pixel");

        // The OVERALL bbox is far too blunt on its own — it is set by the outermost squares of a
        // 64x64 field, so dropping the grow entirely (every square shrinking from ~5.7 to ~3.7 device
        // pixels, which is exactly the mistake this tier could make) moves it by one pixel and sails
        // through. Painted-pixel COUNT is what actually sees it: that same mistake costs well over
        // half the ink. Verified by mutation, not assumed.
        int inkElided = PaintedPixels(elided), inkExact = PaintedPixels(exact);
        Assert.True(Math.Abs(inkElided - inkExact) <= inkExact * 0.15,
            $"elided render painted {inkElided} pixels against the exact geometry's {inkExact} — " +
            "the grown fill is not covering what the outline it replaced covered");

        // And it must genuinely be the cheaper tier — one fill per chunk, not a fill AND a stroke.
        Assert.True(elidedResult.DrawCalls < exactResult.DrawCalls,
            $"elision issued {elidedResult.DrawCalls} draw calls, exact geometry {exactResult.DrawCalls}");
    }

    // ── ...and it must stay OFF wherever the detail is actually visible ────────────────────────
    //
    // The other half of the bargain, and the half a user would notice: zoom in and the real geometry
    // comes back, outline, fill opacity and all. Everything above tests the elided tier does no harm
    // where it engages; nothing above would notice it engaging where it must not, because those tests
    // disable it to isolate culling. Pinned as strict pixel identity against the exact-geometry
    // render at a zoom where the squares are ~10 device pixels — well clear of the threshold.

    [Theory]
    [InlineData(10)]     // L arms ~12 device px
    [InlineData(4)]      // ~30 device px
    public void StrokeElision_DoesNotEngageOnceGeometryIsBigEnoughToSee(double spanUm)
    {
        var cellDir = CreateCell("Big", v => AddPolyField(v, 40));
        var tech = MakeTech(fillOpacity: 0.5);
        var top = PlaceInstance(cellDir);
        var vp = new LayoutViewport(5_000, 5_000, 800.0 / (spanUm * 1000), 800, 800);

        Render(top, tech, vp, _workspaceDir, out var byDefault);
        Render(top, tech, vp, _workspaceDir, out var exact, elision: -1);

        Assert.Equal(exact, byDefault);
    }

    /// <summary>How many pixels the frame painted at all.</summary>
    private static int PaintedPixels(SKColor[] px)
    {
        var bg = LayoutRenderTheme.Light.Background;
        int n = 0;
        foreach (var c in px) if (c != bg) n++;
        return n;
    }

    /// <summary>Bounding box of every non-background pixel, or null if the frame is empty.</summary>
    private static (int Left, int Top, int Right, int Bottom)? PaintedBounds(SKColor[] px, int w, int h)
    {
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        var bg = LayoutRenderTheme.Light.Background;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (px[y * w + x] == bg) continue;
            if (x < l) l = x;
            if (x > r) r = x;
            if (y < t) t = y;
            if (y > b) b = y;
        }
        return r < 0 ? null : (l, t, r, b);
    }
}
