// A stroked path's outline is built by SkPathOps, which always answers in EVEN-ODD — and every
// batching tier in this renderer folds that answer into a default WINDING SKPath, which copies
// contours and not the fill rule. See LayoutRenderer.AsWinding for the whole story; the short
// version is that an open centreline which touches itself strokes to a ring just as much as a
// closed one does, and IsRingGeometry only ever guarded the closed case.
//
// The owner reported it as the P, R, 4 and 9 of an imported board's drill map coming out solid
// through a PLACEMENT of the cell while the same artwork drawn top-level was correct (2026-09-04),
// and the mirrored B on its bottom assembly layer doing the same. Instance-only is a symptom of
// WHEN each tier batches, not of two defects: CompileCell always merges, DrawLayer's merge tier
// waits for MergeShapeCountThreshold candidates to be on screen.

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public sealed class LayoutOutlineFillRuleTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);
    private const int Size = 400;

    public LayoutOutlineFillRuleTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfFillRule_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static Technology MakeTech() => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers =
        [
            new LayerDef
            {
                Key = LayerA, Name = "L1", Color = new CircuitRF.Design.Theming.Rgba(255, 0, 0),
                FillOpacity = 1.0, ZOrder = 0, Visible = true, Selectable = true,
            },
        ],
    };

    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    /// <summary>A stroked capital P, written the way a CAM tool writes one: ONE stroke that runs up
    /// the stem, round the bowl and back onto the stem. Its centreline is OPEN — start and end are
    /// different points, so <c>IsRingGeometry</c> is false — but it touches itself, so the bowl's
    /// counter is a genuine HOLE in the outline. That hole is the whole test.</summary>
    private static PathShape LetterP() => new()
    {
        Layer = LayerA,
        Xy =
        [
            40_000, 20_000,     // foot of the stem
            40_000, 180_000,    // top of the stem
            150_000, 180_000,   // across the top of the bowl
            150_000, 110_000,   // down the right of the bowl
            40_000, 110_000,    // back onto the stem, closing the bowl
        ],
        Width = 12_000, End = PathEndStyle.Round,
    };

    /// <summary>The centre of the bowl's counter, in DBU — well clear of the 12,000-wide stroke.</summary>
    private const long CounterX = 95_000, CounterY = 145_000;

    private static void Glyph(LayoutView v)
    {
        v.Shapes.Add(LetterP());
        // Ordinary material elsewhere on the same layer, so the aggregate this is folded into is
        // never a single-contour path by accident.
        v.Shapes.Add(new PolygonShape { Layer = LayerA, Xy = [170_000, 20_000, 195_000, 20_000, 195_000, 60_000, 170_000, 60_000] });
    }

    // ── The defect at its source, with no renderer around it ──────────────────────────────────────

    [Fact]
    public void AStrokedOutline_FillsTheSameWhenFoldedIntoANonZeroAggregate()
    {
        var ps = new LayoutRenderer.PathSpace(0, 0, 1.0 / 1000.0);
        using var outline = LayoutRenderer.BuildPathOutline(LetterP(), ps);
        Assert.NotNull(outline);
        Assert.False(outline!.IsEmpty);

        // What every batching tier does to it: AddPath copies the contours into a path that keeps
        // its OWN (default, non-zero) fill rule.
        using var aggregate = new SKPath();
        aggregate.AddPath(outline);
        Assert.Equal(SKPathFillType.Winding, aggregate.FillType);

        Assert.Equal(RasterOf(outline), RasterOf(aggregate));
    }

    /// <summary>Rasterizes a path over its own bounds and returns the lit-pixel count — the only
    /// quantity that matters here, and one that changes by thousands when a counter fills.</summary>
    private static int RasterOf(SKPath path)
    {
        var b = path.Bounds;
        const int N = 128;
        using var surface = SKSurface.Create(new SKImageInfo(N, N));
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.Scale(Math.Min(N / b.Width, N / b.Height));
        surface.Canvas.Translate(-b.Left, -b.Top);
        surface.Canvas.DrawPath(path, new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Red, IsAntialias = false });
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        int lit = 0;
        foreach (var c in bmp.Pixels) if (c.Red > c.Blue + 8) lit++;
        return lit;
    }

    // ── The same thing through each tier that batches ─────────────────────────────────────────────

    [Fact]
    public void APlacedGlyph_KeepsItsCounter()
    {
        // A compiled instance chunk ALWAYS merges, which is why this reached the owner through a
        // placement and not through the flat file.
        var cellDir = CreateCell("glyph", Glyph);
        var placed = MakeView();
        placed.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(_workspaceDir, cellDir),
            X = 0, Y = 0, Rot = LayoutRotation.R0, Mag = 1, Rows = 1, Cols = 1, PitchX = 0, PitchY = 0,
        });

        AssertCounterIsBackground(placed, _workspaceDir, opts => opts);
    }

    [Fact]
    public void TheTopLevelMergeTier_KeepsItToo()
    {
        // Same defect, same fix, the other tier — a layer past MergeShapeCountThreshold sends this
        // glyph into a shared non-zero path exactly as CompileCell does.
        var flat = MakeView();
        Glyph(flat);
        AssertCounterIsBackground(flat, null, opts => opts with { ForceMergeTier = true });
    }

    [Fact]
    public void APlacementRendersWhatTheCellRenders()
    {
        // The standing oracle for this renderer, applied to a glyph: a placement of a cell must
        // render what the cell itself renders.
        var cellDir = CreateCell("glyph", Glyph);
        var placed = MakeView();
        placed.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(_workspaceDir, cellDir),
            X = 0, Y = 0, Rot = LayoutRotation.R0, Mag = 1, Rows = 1, Cols = 1, PitchX = 0, PitchY = 0,
        });
        var flat = MakeView();
        Glyph(flat);

        var vp = new LayoutViewport(0, 0, Size / 200_000.0, Size, Size);
        var flatPx = Render(flat, vp, null, o => o);
        var placedPx = Render(placed, vp, _workspaceDir, o => o);

        var bg = flatPx[0];
        int flatLit = Lit(flatPx, bg), placedLit = Lit(placedPx, bg);
        Assert.True(flatLit > 0, "the top-level render drew nothing — the fixture is wrong");
        // A filled counter is ~4,000 pixels of this 160,000-pixel frame against ~11,000 lit; the
        // band here is the antialiasing difference between one batched fill and several.
        Assert.InRange(placedLit, (int)(flatLit * 0.95), (int)(flatLit * 1.05));
    }

    private void AssertCounterIsBackground(LayoutView view, string? baseDir,
                                           Func<LayoutRenderOptions, LayoutRenderOptions> tune)
    {
        var vp = new LayoutViewport(0, 0, Size / 200_000.0, Size, Size);
        var px = Render(view, vp, baseDir, tune);
        var bg = px[0];
        int cx = (int)(CounterX * vp.Zoom), cy = (int)(Size - CounterY * vp.Zoom);
        var centre = px[cy * Size + cx];
        Assert.True(
            Math.Abs(centre.Red - bg.Red) <= 6 && Math.Abs(centre.Green - bg.Green) <= 6
            && Math.Abs(centre.Blue - bg.Blue) <= 6,
            $"the letter's counter filled in: centre {centre} vs background {bg}");
    }

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        var view = MakeView();
        populate(view);
        LayoutPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "main.clay"), view);
        return cellDir;
    }

    private static SKColor[] Render(LayoutView view, LayoutViewport vp, string? baseDir,
                                    Func<LayoutRenderOptions, LayoutRenderOptions> tune)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        LayoutRenderer.Draw(surface.Canvas, view, MakeTech(), vp, tune(new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = baseDir,
        }));
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.Pixels;
    }

    private static int Lit(SKColor[] px, SKColor background)
    {
        int n = 0;
        foreach (var c in px)
            if (Math.Abs(c.Red - background.Red) > 6 || Math.Abs(c.Green - background.Green) > 6
                || Math.Abs(c.Blue - background.Blue) > 6) n++;
        return n;
    }
}
