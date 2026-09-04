using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  The raster tier — the fourth compiled-instance draw tier, and the only one whose cost is set by
//  the PIXELS a placement occupies rather than by the geometry behind them.
//
//  The other three (chunk culling, stroke elision, the coarse tier) all key on a chunk's LARGEST
//  PRIMITIVE being a few device pixels. That is the right question for a dense field of tiny
//  identical primitives and the wrong question for an imported board: a few thousand large polygons,
//  any one of which spans a good fraction of the cell, never bring a chunk under those thresholds at
//  any zoom where the board is still on screen. An owner-reported 10x10 array of one such board
//  rendered at 1,871 ms per frame at Zoom-to-Fit — 100 placements x ~19 ms — which starves every
//  other visual in the window, since an application has one compositor.
//
//  So: rasterize the cell once at the placement's exact device scale and blit it per placement.
//  These tests hold that it engages where it pays, that it does NOT engage where it would not, that
//  it disappears entirely on the paths that must have exact vector geometry, and that its cache
//  cannot serve stale pixels.
// ──────────────────────────────────────────────────────────────────────────────

[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public sealed class LayoutInstanceRasterTierTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);

    /// <summary>Tier off, for the exact-geometry reference render — see
    /// <c>LayoutRenderOptions.InstanceRasterMaxDevicePixels</c>: negative disables outright.</summary>
    private const double TierOff = -1;

    public LayoutInstanceRasterTierTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfRasterTest_" + Guid.NewGuid().ToString("N")[..8]);
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

    // FillOpacity 1.0 for the same reason the coarse-tier and chunk-culling tests use it: a
    // translucent layer composites differently through an offscreen surface than straight onto the
    // canvas, so a compositing difference could not be told apart from a difference in what was drawn.
    private static Technology MakeTech(byte r = 255, byte g = 0, byte b = 0) => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers =
        [
            new LayerDef
            {
                Key = LayerA, Name = "L1", Color = new CircuitRF.Design.Theming.Rgba(r, g, b),
                FillOpacity = 1.0, ZOrder = 0, Visible = true, Selectable = true,
            },
        ],
    };

    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    /// <summary>A side x side field of disjoint squares. <c>side</c> is what decides whether the cell
    /// clears the tier's primitive floor, which is the gate these tests turn on and off.</summary>
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

    /// <summary>The cell placed as a rows x cols array on <paramref name="pitch"/> DBU.</summary>
    private LayoutView PlaceArray(string cellDir, int rows, int cols, long pitch)
    {
        var top = MakeView();
        top.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(_workspaceDir, cellDir),
            X = 0, Y = 0, Rot = LayoutRotation.R0, Mag = 1,
            Rows = rows, Cols = cols, PitchX = pitch, PitchY = pitch,
        });
        return top;
    }

    private LayoutRenderResult Render(LayoutView view, Technology tech, LayoutViewport vp,
                                      out SKColor[] pixels, double raster = 0, double detail = 0)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = _workspaceDir,
            InstanceRasterMaxDevicePixels = raster,
            DetailPixelThreshold = detail,
        };
        var result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        pixels = bmp.Pixels;
        return result;
    }

    /// <summary>A 20 x 20 field (400 primitives, past the tier's floor) spanning 20 um, arrayed 2 x 2
    /// on a 40 um pitch and framed whole in 400 px — so a placement is ~100 device pixels, well under
    /// the size ceiling, and all four are on screen.</summary>
    private (LayoutView Top, Technology Tech, LayoutViewport Vp) DenseArray(int rows = 2, int cols = 2)
    {
        var cellDir = CreateCell("Dense", v => AddField(v, 20, pitch: 1000, size: 500));
        return (PlaceArray(cellDir, rows, cols, pitch: 40_000), MakeTech(),
                new LayoutViewport(-5_000, -5_000, 400.0 / 90_000, 400, 400));
    }

    // ── It engages, and collapses the per-placement cost ───────────────────────────────────────
    //
    // Asserted on the DRAW-CALL COUNTER rather than on wall-clock: the counter is what states the
    // structural property — "one more placement costs one more blit, not one more cell's worth of
    // geometry" — and it says so on any machine under any load. The rasterization behind those blits
    // is counted separately (InstanceRastersBuilt) precisely so this reads as one number.

    [Fact]
    public void RasterTier_OnAnArrayOfADenseCell_CostsOneDrawCallPerPlacement()
    {
        var (top, tech, vp) = DenseArray();

        var withTier    = Render(top, tech, vp, out _);
        var withoutTier = Render(top, tech, vp, out _, raster: TierOff);

        Assert.Equal(4, withTier.InstancesDrawn);
        Assert.Equal(4, withoutTier.InstancesDrawn);

        Assert.Equal(1, withTier.InstanceRastersBuilt);   // the cell, once, for all four placements
        Assert.Equal(4, withTier.DrawCalls);              // and one blit each
        Assert.Equal(0, withoutTier.InstanceRastersBuilt);
        Assert.True(withoutTier.DrawCalls > withTier.DrawCalls,
            $"the tier saved nothing: {withoutTier.DrawCalls} draw calls without it, {withTier.DrawCalls} with.");
    }

    // ── The scaling law, which is the actual claim ─────────────────────────────────────────────
    //
    // The 10x10 array in the owner's report was slow because each of the 100 placements cost the
    // cell's whole geometry. What this tier changes is the SLOPE: growing the array must add one draw
    // call per placement and nothing else. Comparing two array sizes measures exactly that slope, and
    // does it without depending on how many chunks this particular cell happens to compile into.

    [Theory]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    public void RasterTier_GrowingTheArray_AddsExactlyOneDrawCallPerAddedPlacement(int smallSide, int largeSide)
    {
        var cellDir = CreateCell("Dense", v => AddField(v, 20, pitch: 1000, size: 500));
        var tech = MakeTech();
        // One viewport for both, framing the larger array whole so every placement of either is on
        // screen — a placement culled out of one render and not the other would measure the culling.
        var vp = new LayoutViewport(-5_000, -5_000, 400.0 / (largeSide * 40_000 + 10_000), 400, 400);

        var small = Render(PlaceArray(cellDir, smallSide, smallSide, 40_000), tech, vp, out _);
        var large = Render(PlaceArray(cellDir, largeSide, largeSide, 40_000), tech, vp, out _);

        int added = largeSide * largeSide - smallSide * smallSide;
        Assert.Equal(smallSide * smallSide, small.InstancesDrawn);
        Assert.Equal(largeSide * largeSide, large.InstancesDrawn);
        Assert.Equal(added, large.DrawCalls - small.DrawCalls);

        // …and the second render rasterized nothing: same cell, same scale, same colours, so it blits
        // the image the first one built. This is what makes a pan free.
        Assert.Equal(0, large.InstanceRastersBuilt);
    }

    // ── …and what it draws is the same picture ─────────────────────────────────────────────────
    //
    // Not pixel equality, and deliberately so: the blit lands at a fractional device offset per
    // placement, so every antialiased edge inside the cell may resolve up to a pixel differently.
    // That is the tier's stated trade and it is the same kind the other three make. What must hold is
    // that the cell is in the same PLACE at the same SIZE with the same COLOUR — so this compares the
    // painted footprint, which a wrong scale, a wrong origin or a dropped layer would all break.

    [Fact]
    public void RasterTier_PaintsTheSameFootprintTheGeometryPathDoes()
    {
        var (top, tech, vp) = DenseArray();

        Render(top, tech, vp, out var withTier);
        Render(top, tech, vp, out var withoutTier, raster: TierOff);

        var bg = withoutTier[0];
        int onlyWith = 0, onlyWithout = 0, painted = 0;
        for (int i = 0; i < withTier.Length; i++)
        {
            bool a = withTier[i] != bg, b = withoutTier[i] != bg;
            if (a && b) painted++;
            else if (a) onlyWith++;
            else if (b) onlyWithout++;
        }

        Assert.True(painted > 1000, $"nothing was painted either way ({painted} px) — this proves nothing.");
        // Disagreement is confined to the antialiased boundary of the field, which is a thin fraction
        // of a footprint that is thousands of pixels of solid colour.
        Assert.True(onlyWith + onlyWithout < painted / 10,
            $"the two renders disagree about {onlyWith + onlyWithout} px against {painted} painted — "
            + "that is a different picture, not a resampled edge.");
    }

    // ── Where it must NOT engage ───────────────────────────────────────────────────────────────

    [Fact]
    public void RasterTier_WithOnlyOnePlacement_DoesNotEngage()
    {
        var cellDir = CreateCell("Dense", v => AddField(v, 20, pitch: 1000, size: 500));
        var top  = PlaceArray(cellDir, rows: 1, cols: 1, pitch: 40_000);
        var tech = MakeTech();
        var vp   = new LayoutViewport(-5_000, -5_000, 400.0 / 90_000, 400, 400);

        // Rasterizing for a single placement is the same drawing work plus a surface and a blit, so
        // the tier declines — byte-identical to it being switched off.
        Render(top, tech, vp, out var withTier);
        Render(top, tech, vp, out var withoutTier, raster: TierOff);
        Assert.Equal(withoutTier, withTier);
    }

    [Fact]
    public void RasterTier_OnACheapCell_DoesNotEngage()
    {
        // 9 primitives — under the floor. The geometry path is already a handful of draw calls, and
        // keeping cheap cells on it is what stops this tier perturbing small-array pixel comparisons.
        var cellDir = CreateCell("Cheap", v => AddField(v, 3, pitch: 1000, size: 500));
        var top  = PlaceArray(cellDir, rows: 2, cols: 2, pitch: 40_000);
        var tech = MakeTech();
        var vp   = new LayoutViewport(-5_000, -5_000, 400.0 / 90_000, 400, 400);

        Render(top, tech, vp, out var withTier);
        Render(top, tech, vp, out var withoutTier, raster: TierOff);
        Assert.Equal(withoutTier, withTier);
    }

    [Fact]
    public void RasterTier_WhenAPlacementIsLargerThanTheCeiling_DoesNotEngage()
    {
        var (top, tech, _) = DenseArray();

        // Zoomed until one 20 um placement is ~4,000 device pixels — past the ceiling, and the zoom at
        // which per-chunk culling has real work to do again because only part of the cell is on screen.
        var vp = new LayoutViewport(0, 0, 4000.0 / 20_000, 400, 400);

        Render(top, tech, vp, out var withTier);
        Render(top, tech, vp, out var withoutTier, raster: TierOff);
        Assert.Equal(withoutTier, withTier);
    }

    // ── The export path ────────────────────────────────────────────────────────────────────────
    //
    // Every export sets DetailPixelThreshold negative — this renderer's established "the whole
    // level-of-detail system is off" knob. An export writes the cell's vector geometry into a PDF or
    // SVG canvas and must never write a bitmap of it, so the raster tier has to fall to that one knob
    // rather than to a second flag each export path would have to remember separately.

    [Fact]
    public void RasterTier_WithLevelOfDetailOff_DoesNotEngage()
    {
        var (top, tech, vp) = DenseArray();

        var export = Render(top, tech, vp, out var exported, detail: -1);
        Render(top, tech, vp, out var reference, raster: TierOff, detail: -1);

        Assert.Equal(reference, exported);
        Assert.True(export.DrawCalls > 4,
            "the export drew one call per placement — that is the raster tier, which must not reach an export.");
    }

    // ── The cache cannot serve stale pixels ────────────────────────────────────────────────────
    //
    // The snapshot lives on the compiled cell, which outlives any one frame and is shared by every
    // canvas. Keyed on scale alone it would blit yesterday's colours after a technology edit or a
    // theme switch — so the key carries every layer's colour, opacity, fill pattern and visibility
    // too. This renders the SAME view twice through the same compiled cell, changing only the colour.

    [Fact]
    public void RasterTier_AfterALayerColourChanges_RebuildsRatherThanBlittingStalePixels()
    {
        var (top, _, vp) = DenseArray();

        Render(top, MakeTech(255, 0, 0), vp, out var red);
        Render(top, MakeTech(0, 0, 255), vp, out var blue);

        var bg = red[0];
        int redPainted = red.Count(p => p != bg);
        Assert.True(redPainted > 1000, "nothing was painted — this proves nothing.");

        // Every painted pixel must have moved; a stale blit would have left them all identical.
        Assert.True(red.Where((p, i) => p != bg && blue[i] == p).Count() < redPainted / 100,
            "the second render reused the first's pixels — the raster cache is not keyed on colour.");
    }

    [Fact]
    public void RasterTier_AfterALayerIsHidden_RebuildsRatherThanBlittingStalePixels()
    {
        var (top, tech, vp) = DenseArray();

        Render(top, tech, vp, out var shown);

        var hidden = MakeTech();
        hidden.Layers[0].Visible = false;
        Render(top, hidden, vp, out var afterHiding);

        var bg = shown[0];
        Assert.True(shown.Count(p => p != bg) > 1000, "nothing was painted — this proves nothing.");
        Assert.All(afterHiding, p => Assert.Equal(bg, p));
    }
}
