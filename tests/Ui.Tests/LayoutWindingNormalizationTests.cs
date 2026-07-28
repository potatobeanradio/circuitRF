using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate 2 (docs/sonnet-briefs/brief-layout-testing-fixes.md, item 1/R-fix-1) — "Group into Cell" (and any
/// other batched-fill path) rendered an instance as if its two overlapping same-layer polygons were
/// XOR'd. Root cause: <c>LayoutRenderer.BuildShapePath</c>'s outer ring is never normalized to a
/// consistent winding direction — two overlapping outer contours with OPPOSITE vertex order cancel under
/// Skia's default Winding fill rule when merged into one batched <c>SKPath</c> (the instance-compiled
/// aggregate in <c>CompileCell</c>, and the L2c LOD/merge-tier aggregate in <c>DrawLayer</c> — the SAME
/// per-shape path builder feeds both, so the fix (and this test) covers both by construction). A single
/// shape drawn on its own (the individual, non-aggregated tier) never shows this — each shape composites
/// independently regardless of its own winding — which is why the cell's own view looked correct while
/// the instance did not.
/// </summary>
public sealed class LayoutWindingNormalizationTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);

    public LayoutWindingNormalizationTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfWindingTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateAll();
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    private static Technology MakeTech() => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers = [new LayerDef { Key = LayerA, Name = "L1", Color = new CircuitRF.Ui.Theming.Rgba(255, 0, 0), FillOpacity = 1.0, ZOrder = 0, Visible = true, Selectable = true }],
    };

    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    /// <summary>A 200x200 square traversed CLOCKWISE in DBU (Y-up) space — signed area is negative.</summary>
    private static PolygonShape SquareClockwise() => new()
    {
        Layer = LayerA,
        Xy = [0, 0, 0, 200, 200, 200, 200, 0],
    };

    /// <summary>A 200x200 square, offset to overlap the one above in a (100,100)-(200,200) region,
    /// traversed COUNTER-CLOCKWISE in DBU space — signed area is positive, the OPPOSITE handedness.</summary>
    private static PolygonShape SquareCounterClockwiseOverlapping() => new()
    {
        Layer = LayerA,
        Xy = [100, 100, 300, 100, 300, 300, 100, 300],
    };

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = MakeView();
        populate(view);
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "main.clay"), view);
        return cellDir;
    }

    private static SKBitmap RenderPixels(LayoutView view, Technology tech, LayoutViewport vp, string? baseDir, bool forceMergeTier = false)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = baseDir, ForceMergeTier = forceMergeTier };
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        using var img = surface.Snapshot();
        return SKBitmap.FromImage(img);
    }

    private static bool IsRedFilled(SKBitmap bmp, int x, int y)
    {
        var c = bmp.GetPixel(x, y);
        return c.Red > c.Green + 30 && c.Red > c.Blue + 30;
    }

    private static bool PixelsEqual(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
        return true;
    }

    private static (int OverlapX, int OverlapY, int OnlyAX, int OnlyAY, int OnlyBX, int OnlyBY) Layout(LayoutViewport vp)
    {
        int overlapX = (int)vp.WorldToScreenX(150), overlapY = (int)vp.WorldToScreenY(150); // inside BOTH squares
        int onlyAX = (int)vp.WorldToScreenX(50), onlyAY = (int)vp.WorldToScreenY(50);       // inside A only
        int onlyBX = (int)vp.WorldToScreenX(250), onlyBY = (int)vp.WorldToScreenY(250);     // inside B only
        return (overlapX, overlapY, onlyAX, onlyAY, onlyBX, onlyBY);
    }

    // ── Control: drawn directly, individually — never had the bug (each shape composites alone) ──────

    [Fact]
    public void DrawnDirectly_Individual_OppositeWindingOverlap_IsFilled_NotCancelled()
    {
        var view = MakeView();
        view.Shapes.Add(SquareClockwise());
        view.Shapes.Add(SquareCounterClockwiseOverlapping());
        var tech = MakeTech();
        var vp = new LayoutViewport(-50, -50, 1.0, 400, 400);

        using var pixels = RenderPixels(view, tech, vp, null);
        var (ox, oy, ax, ay, bx, by) = Layout(vp);

        Assert.True(IsRedFilled(pixels, ox, oy), "overlap region must be filled");
        Assert.True(IsRedFilled(pixels, ax, ay), "shape A's own region must be filled");
        Assert.True(IsRedFilled(pixels, bx, by), "shape B's own region must be filled");
    }

    /// <summary>A 200x200 axis-aligned rect covering the SAME footprint as <see cref="SquareClockwise"/>
    /// — Skia's own <c>AddRect</c> primitive has its own fixed internal winding, independent of any
    /// vertex order in our data, so normalizing ONLY the hand-built Polygon/Curve rings against each
    /// OTHER is not sufficient — they must also end up consistent with Rect/RoundedRect/Circle's own
    /// winding, or a Polygon-over-Rect overlap on the same layer could still cancel.</summary>
    private static RectShape RectSameFootprintAsSquareClockwise() => new() { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 200, Y2 = 200 };

    [Fact]
    public void AsInstance_RectAndOppositelyWoundPolygonOverlap_IsFilled_NotCancelled()
    {
        CreateCell("Leaf", v =>
        {
            v.Shapes.Add(RectSameFootprintAsSquareClockwise());
            v.Shapes.Add(SquareCounterClockwiseOverlapping());
        });
        var tech = MakeTech();
        var vp = new LayoutViewport(-50, -50, 1.0, 400, 400);

        var instanceView = MakeView();
        instanceView.Instances.Add(new LayoutInstance { CellRef = "Leaf", X = 0, Y = 0, Mag = 1.0 });
        using var pixels = RenderPixels(instanceView, tech, vp, _workspaceDir);
        var (ox, oy, ax, ay, bx, by) = Layout(vp);

        Assert.True(IsRedFilled(pixels, ox, oy), "Rect/Polygon overlap must be filled when rendered as an instance");
        Assert.True(IsRedFilled(pixels, ax, ay));
        Assert.True(IsRedFilled(pixels, bx, by));
    }

    // ── Gate 2a: the reported bug — as a compiled instance (LayoutRenderer.Instances.cs CompileCell) ──

    [Fact]
    public void AsInstance_OppositeWindingOverlap_IsFilled_NotCancelled()
    {
        CreateCell("Leaf", v =>
        {
            v.Shapes.Add(SquareClockwise());
            v.Shapes.Add(SquareCounterClockwiseOverlapping());
        });
        var tech = MakeTech();
        var vp = new LayoutViewport(-50, -50, 1.0, 400, 400);

        var instanceView = MakeView();
        instanceView.Instances.Add(new LayoutInstance { CellRef = "Leaf", X = 0, Y = 0, Mag = 1.0 });
        using var pixels = RenderPixels(instanceView, tech, vp, _workspaceDir);
        var (ox, oy, ax, ay, bx, by) = Layout(vp);

        Assert.True(IsRedFilled(pixels, ox, oy), "overlap region must be filled when rendered as an instance");
        Assert.True(IsRedFilled(pixels, ax, ay));
        Assert.True(IsRedFilled(pixels, bx, by));
    }

    // ── Gate 2b: the SAME cancellation must be reachable via the L2c LOD/merge aggregate ────────────

    [Fact]
    public void LodMergeAggregate_OppositeWindingOverlap_IsFilled_NotCancelled()
    {
        var view = MakeView();
        view.Shapes.Add(SquareClockwise());
        view.Shapes.Add(SquareCounterClockwiseOverlapping());
        var tech = MakeTech();
        var vp = new LayoutViewport(-50, -50, 1.0, 400, 400);

        // Only 2 shapes — force the merge tier explicitly rather than relying on the 2,000-shape default
        // threshold, so this test exercises the SAME aggregate DrawLayer's merge tier uses without
        // needing a slow, large synthetic layout.
        using var pixels = RenderPixels(view, tech, vp, null, forceMergeTier: true);
        var (ox, oy, ax, ay, bx, by) = Layout(vp);

        Assert.True(IsRedFilled(pixels, ox, oy), "overlap region must be filled under the forced merge tier");
        Assert.True(IsRedFilled(pixels, ax, ay));
        Assert.True(IsRedFilled(pixels, bx, by));
    }

    // ── Gate 2: "renders identically whether drawn directly or as an instance" ─────────────────────

    [Fact]
    public void InstanceRender_And_DirectRender_And_MergeTierRender_AreAllPixelIdentical()
    {
        var directView = MakeView();
        directView.Shapes.Add(SquareClockwise());
        directView.Shapes.Add(SquareCounterClockwiseOverlapping());

        CreateCell("Leaf", v =>
        {
            v.Shapes.Add(SquareClockwise());
            v.Shapes.Add(SquareCounterClockwiseOverlapping());
        });
        var instanceView = MakeView();
        instanceView.Instances.Add(new LayoutInstance { CellRef = "Leaf", X = 0, Y = 0, Mag = 1.0 });

        var tech = MakeTech();
        var vp = new LayoutViewport(-50, -50, 1.0, 400, 400);

        using var directPixels = RenderPixels(directView, tech, vp, null);
        using var instancePixels = RenderPixels(instanceView, tech, vp, _workspaceDir);
        using var mergePixels = RenderPixels(directView, tech, vp, null, forceMergeTier: true);

        Assert.True(PixelsEqual(directPixels, instancePixels), "instance render must match direct render");
        Assert.True(PixelsEqual(directPixels, mergePixels), "merge-tier render must match direct render");
    }
}
