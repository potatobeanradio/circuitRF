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
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    private static Technology MakeTech() => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers = [new LayerDef { Key = LayerA, Name = "L1", Color = new CircuitRF.Design.Theming.Rgba(255, 0, 0), FillOpacity = 1.0, ZOrder = 0, Visible = true, Selectable = true }],
    };

    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    /// <summary>A technology fixture at the PRODUCTION-default <c>FillOpacity</c> (0.35, alpha≈89) —
    /// deliberately NOT <see cref="MakeTech"/>'s FillOpacity=1.0 fixture, which would make ghost and
    /// committed fills trivially/structurally identical post-fix and defeat the point of a comparative
    /// gate.</summary>
    private static Technology MakeRealisticTech() => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers = [new LayerDef { Key = LayerA, Name = "L1", Color = new CircuitRF.Design.Theming.Rgba(255, 0, 0), FillOpacity = 0.35, ZOrder = 0, Visible = true, Selectable = true }],
    };

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

    private static SKBitmap RenderPixels(LayoutView view, Technology tech, LayoutViewport vp, string? baseDir, bool forceMergeTier = false, LayoutOverlay? overlay = null, LayoutPathCache? pathCache = null)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = baseDir, ForceMergeTier = forceMergeTier, Overlay = overlay, PathCache = pathCache };
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        using var img = surface.Snapshot();
        return SKBitmap.FromImage(img);
    }

    private static bool IsRedFilled(SKBitmap bmp, int x, int y)
    {
        var c = bmp.GetPixel(x, y);
        return c.Red > c.Green + 30 && c.Red > c.Blue + 30;
    }

    /// <summary>Euclidean RGB distance — a contrast-against-background proxy for the R-dgf-1
    /// comparative gates below. Ignores alpha (both bitmaps are opaque render targets).</summary>
    private static double ColorDistance(SKColor a, SKColor b)
    {
        double dr = a.Red - b.Red, dg = a.Green - b.Green, db = a.Blue - b.Blue;
        return System.Math.Sqrt(dr * dr + dg * dg + db * db);
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

    // ── docs/sonnet-briefs/brief-drag-fill.md — the SAME defect, expressed as a live drag ────────────
    //
    // R-drag-1's diagnosis: DrawLayer substitutes Overlay.DragOverrides for the stored shape BEFORE
    // calling BuildShapePath (or the path cache, which is itself BuildShapePath-derived) — the exact
    // same builder R-fix-1 already normalizes winding in, for BOTH the individual and merge tiers. So a
    // shape being dragged should already be immune to this bug; these gates prove that directly rather
    // than assuming it from the code path alone.

    /// <summary>Gate 2: a shape rendered via a DragOverrides substitution is pixel-identical to the
    /// SAME shape committed directly — same fill colour, same FillOpacity, not background and not some
    /// other (e.g. outline-only or reduced-alpha) drag-specific rendering.</summary>
    [Fact]
    public void Dragged_SingleShape_IsPixelIdenticalToTheSameShapeCommittedDirectly()
    {
        var tech = MakeTech();
        var vp = new LayoutViewport(-50, -50, 1.0, 400, 400);

        var committedView = MakeView();
        committedView.Shapes.Add(SquareClockwise());
        using var committedPixels = RenderPixels(committedView, tech, vp, null);

        // A placeholder shape at index 0, fully replaced at render time by the DragOverrides entry —
        // mirrors exactly how LayoutEditorViewModel's live move-drag preview works (R-L1c-3): the
        // model itself (the placeholder) is never mutated mid-drag, only the overlay is.
        var draggedView = MakeView();
        draggedView.Shapes.Add(new RectShape { Layer = LayerA, X1 = 900, Y1 = 900, X2 = 901, Y2 = 901 });
        var overlay = new LayoutOverlay { DragOverrides = new Dictionary<int, LayoutShape> { [0] = SquareClockwise() } };
        using var draggedPixels = RenderPixels(draggedView, tech, vp, null, overlay: overlay);

        Assert.True(PixelsEqual(committedPixels, draggedPixels),
            "a dragged shape must render pixel-identical to the same shape committed directly");
    }

    /// <summary>docs/sonnet-briefs/brief-drag-fill-still-outline-only.md — the real bug the two tests
    /// above could not catch: neither ever set <see cref="LayoutRenderOptions.PathCache"/>, so both
    /// always exercised <c>DrawLayer</c>'s uncached <c>else</c> branch — never the
    /// <c>opts.PathCache is {} cache</c> branch <see cref="LayoutCanvas"/> ALWAYS uses in the real app
    /// (it constructs a live <see cref="LayoutPathCache"/> for every bound document). This test
    /// reproduces the real pipeline: render the shape once as COMMITTED first (seeding the cache at its
    /// index, exactly as the app always has by the time a user starts dragging something already on
    /// screen), then render again on the SAME cache instance with a <c>DragOverrides</c> entry translating
    /// the shape elsewhere. Before the fix, <c>LayoutPathCache.GetOrBuild</c> — keyed by index only, never
    /// comparing the shape argument against what it already has cached — returned the stale PRE-drag
    /// (RefX, RefY), so the fill (and its geometry stroke) kept painting at the ORIGINAL position while
    /// only the (uncached) accent selection outline tracked the cursor: "the ghost is still an outline
    /// during dragging."</summary>
    [Fact]
    public void Dragged_SingleShape_WithLivePathCache_FillFollowsTheDragNotTheStalePosition()
    {
        var tech = MakeTech();
        var vp = new LayoutViewport(-50, -50, 1.0, 400, 400);
        var cache = new LayoutPathCache();

        var view = MakeView();
        view.Shapes.Add(SquareClockwise()); // index 0, at (0,0)-(200,200)

        // Seed the cache exactly as the real app has by drag time: a first frame with no drag in
        // progress, drawn through the SAME cache instance the live drag will reuse.
        using (RenderPixels(view, tech, vp, null, pathCache: cache)) { }

        int origCx = (int)vp.WorldToScreenX(100), origCy = (int)vp.WorldToScreenY(100);   // original centre
        int dragCx = (int)vp.WorldToScreenX(250), dragCy = (int)vp.WorldToScreenY(250);   // dragged-to centre

        var dragged = SquareClockwise();
        LayoutGeometry.TranslateBy(dragged, 150, 150); // same shape, moved to (150,150)-(350,350)
        var overlay = new LayoutOverlay { DragOverrides = new Dictionary<int, LayoutShape> { [0] = dragged } };
        using var pixels = RenderPixels(view, tech, vp, null, overlay: overlay, pathCache: cache);

        Assert.True(IsRedFilled(pixels, dragCx, dragCy), "the fill must follow the drag to its new position, not stay behind");
        Assert.False(IsRedFilled(pixels, origCx, origCy), "the fill must NOT remain painted at the shape's stale, pre-drag position");
    }

    /// <summary>Gates 3/4: dragging a shape so it overlaps another same-layer shape with the OPPOSITE
    /// winding must keep the overlap filled — the R-fix-1 regression, expressed as a drag instead of a
    /// static instance/merge-tier render. Covers BOTH tiers via <paramref name="forceMergeTier"/>.</summary>
    [Theory]
    [InlineData(false)] // individual (per-shape) tier — the default for a 2-shape layer
    [InlineData(true)]  // forced merge tier — the SAME aggregate a large/dense layer would use
    public void Dragged_OppositeWindingOverlap_IsFilled_NotCancelled(bool forceMergeTier)
    {
        var view = MakeView();
        view.Shapes.Add(SquareClockwise());                                    // index 0, stationary
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 900, Y1 = 900, X2 = 901, Y2 = 901 }); // index 1, placeholder — its drag override lands overlapping index 0
        var tech = MakeTech();
        var vp = new LayoutViewport(-50, -50, 1.0, 400, 400);

        var overlay = new LayoutOverlay { DragOverrides = new Dictionary<int, LayoutShape> { [1] = SquareCounterClockwiseOverlapping() } };
        using var pixels = RenderPixels(view, tech, vp, null, forceMergeTier: forceMergeTier, overlay: overlay);
        var (ox, oy, ax, ay, bx, by) = Layout(vp);

        Assert.True(IsRedFilled(pixels, ox, oy), "overlap region must stay filled while the shape is being dragged");
        Assert.True(IsRedFilled(pixels, ax, ay), "shape A's own region must be filled during the drag");
        Assert.True(IsRedFilled(pixels, bx, by), "the dragged shape's own region must be filled");
    }

    // ── docs/sonnet-briefs/brief-drag-fill-reopened.md — the ghost's OWN fill was the real defect ────
    //
    // R-dgf-1: a "can't see it" report needs a COMPARATIVE gate, not a threshold gate — the retired
    // DrawingGhost_IsVisiblyFilled_NotOutlineOnly asked "is there any fill at all," which stayed true
    // at the old fixed alpha=60 (≈24% opacity) even though the owner could not see it next to a
    // committed shape. Replaced (not supplemented) by the gates below, which measure the ghost's fill
    // against the SAME shape committed on the SAME layer, the way the eye actually judges it.
    // (MakeRealisticTech/ColorDistance are declared once, alongside MakeTech/IsRedFilled above.)

    /// <summary>R-dgf-1's comparative gate: the drawing ghost's rendered fill, sampled well inside the
    /// shape (away from the dashed edge), must be within a stated fraction (90%) of the SAME shape
    /// committed on the SAME layer — measured as each fill's colour contrast against the canvas
    /// background. At the pre-fix hardcoded alpha=60 (vs. the committed shape's opacity-derived
    /// alpha≈89 at this fixture's FillOpacity=0.35), R-dgf-2's own measurement put this ratio at a
    /// consistent ~0.67-0.69 — comfortably below the 90% bar — which is exactly the gap the owner
    /// reported and a threshold gate could never catch.</summary>
    [Fact]
    public void DrawingGhost_FillMatchesCommittedFill_WithinStatedFraction()
    {
        var tech = MakeRealisticTech();
        var vp = new LayoutViewport(-50, -50, 1.0, 400, 400);
        int cx = (int)vp.WorldToScreenX(100), cy = (int)vp.WorldToScreenY(100); // well inside, away from the dashed edge

        var committedView = MakeView();
        committedView.Shapes.Add(SquareClockwise());
        using var committedPixels = RenderPixels(committedView, tech, vp, null);
        var committedColor = committedPixels.GetPixel(cx, cy);

        var ghostView = MakeView();
        var overlay = new LayoutOverlay { InProgressPrimitive = SquareClockwise() };
        using var ghostPixels = RenderPixels(ghostView, tech, vp, null, overlay: overlay);
        var ghostColor = ghostPixels.GetPixel(cx, cy);

        double committedContrast = ColorDistance(LayoutRenderTheme.Light.Background, committedColor);
        double ghostContrast = ColorDistance(LayoutRenderTheme.Light.Background, ghostColor);
        Assert.True(committedContrast > 5, "test fixture sanity: the committed shape must actually be visible against the background");

        double ratio = ghostContrast / committedContrast;
        Assert.True(ratio >= 0.9,
            $"the drawing ghost's fill contrast ({ghostContrast:F1}) must be within 90% of the committed shape's fill contrast ({committedContrast:F1}); ratio was {ratio:F3}");
    }

    /// <summary>Gate 4: the L1f paste-fragment preview shares <c>DrawGhostShape</c> with the L1b draw
    /// ghost, so it must get the identical treatment — proven independently here via
    /// <c>LayoutOverlay.PastePreview</c> rather than assumed from the shared call site.</summary>
    [Fact]
    public void PasteGhost_FillMatchesCommittedFill_WithinStatedFraction()
    {
        var tech = MakeRealisticTech();
        var vp = new LayoutViewport(-50, -50, 1.0, 400, 400);
        int cx = (int)vp.WorldToScreenX(100), cy = (int)vp.WorldToScreenY(100);

        var committedView = MakeView();
        committedView.Shapes.Add(SquareClockwise());
        using var committedPixels = RenderPixels(committedView, tech, vp, null);
        var committedColor = committedPixels.GetPixel(cx, cy);

        var pasteView = MakeView();
        var overlay = new LayoutOverlay { PastePreview = [SquareClockwise()] };
        using var pastePixels = RenderPixels(pasteView, tech, vp, null, overlay: overlay);
        var pasteColor = pastePixels.GetPixel(cx, cy);

        double committedContrast = ColorDistance(LayoutRenderTheme.Light.Background, committedColor);
        double pasteContrast = ColorDistance(LayoutRenderTheme.Light.Background, pasteColor);
        Assert.True(committedContrast > 5, "test fixture sanity: the committed shape must actually be visible against the background");

        double ratio = pasteContrast / committedContrast;
        Assert.True(ratio >= 0.9,
            $"the paste-fragment ghost's fill contrast ({pasteContrast:F1}) must be within 90% of the committed shape's fill contrast ({committedContrast:F1}); ratio was {ratio:F3}");
    }

    /// <summary>Gate 5: raising the ghost's fill toward the committed opacity must not quietly turn
    /// "make it visible" into "make it identical" — the dashed outline (unchanged by R-dgf-3) is the
    /// one thing that still marks a ghost as provisional. Scans a band of pixels straddling the top
    /// edge and asserts real on/off contrast variation along it: a dashed stroke alternates between a
    /// high-alpha (220) segment and a gap showing only the (much lower-alpha) fill beneath it, while a
    /// solid line would read as near-uniform contrast along its whole length.</summary>
    [Fact]
    public void DrawingGhost_EdgeShowsDashVariation_NotAUniformSolidStroke()
    {
        var tech = MakeRealisticTech();
        var vp = new LayoutViewport(-50, -50, 1.0, 400, 400);

        var ghostView = MakeView();
        var overlay = new LayoutOverlay { InProgressPrimitive = SquareClockwise() };
        using var ghostPixels = RenderPixels(ghostView, tech, vp, null, overlay: overlay);

        int yCenter = (int)System.Math.Round(vp.WorldToScreenY(200)); // the top edge, y=200 in world
        int xStart = (int)System.Math.Round(vp.WorldToScreenX(15));
        int xEnd = (int)System.Math.Round(vp.WorldToScreenX(185));

        double maxDist = 0, minDist = double.MaxValue;
        for (int x = xStart; x <= xEnd; x++)
            for (int y = yCenter - 2; y <= yCenter + 2; y++)
            {
                var c = ghostPixels.GetPixel(x, y);
                double d = ColorDistance(LayoutRenderTheme.Light.Background, c);
                maxDist = System.Math.Max(maxDist, d);
                minDist = System.Math.Min(minDist, d);
            }

        Assert.True(maxDist - minDist > 40,
            $"the ghost's edge must show real dash on/off variation (max={maxDist:F1}, min={minDist:F1}), not read as a solid outline");
    }
}
