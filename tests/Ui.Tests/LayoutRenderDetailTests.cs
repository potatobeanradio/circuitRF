// The render-tolerance detail tier (src/Ui/Renderers/LayoutRenderDetail.cs) — the LOD that engages on
// geometry FINER than the screen, as opposed to geometry SMALLER than the screen (LayoutLodMergeTests).
//
// Owner report, 2026-09-04: panning and zooming an imported Gerber with every layer visible was slow
// with the whole design in view, and the board was an illegible smear there. Both were the same cause: the
// file carries far more vertices than any zoom level can show, and the per-layer outline stroke over
// that geometry was 227 ms of a 250 ms frame while painting solid colour over the layers beneath it.
//
// These gates assert the STRUCTURE (which vertices survive, which shapes are outlined, what the cache
// rebuilds), never a frame time — a timing assertion here would measure the machine.

using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

public class LayoutRenderDetailTests
{
    private static readonly LayerKey LayerA = new(1, 0);

    private static Technology MakeTech() => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers =
        [
            new LayerDef
            {
                Key = LayerA, Name = "L1", Color = new CircuitRF.Design.Theming.Rgba(255, 0, 0),
                FillOpacity = 0.5, ZOrder = 0, Visible = true, Selectable = true,
            },
        ],
    };

    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    /// <summary>A closed ring of <paramref name="n"/> vertices — the shape of every flattened arc an
    /// interchange importer produces, and the shape the tier exists for.</summary>
    private static long[] Ring(int n, long radius, long cx = 0, long cy = 0)
    {
        var xy = new long[n * 2];
        for (int i = 0; i < n; i++)
        {
            double a = 2 * System.Math.PI * i / n;
            xy[2 * i]     = cx + (long)(radius * System.Math.Cos(a));
            xy[2 * i + 1] = cy + (long)(radius * System.Math.Sin(a));
        }
        return xy;
    }

    private static byte[] RenderBytes(LayoutView view, Technology tech, LayoutViewport vp, LayoutRenderOptions opts)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.Bytes;
    }

    // ── The tolerance ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Tolerance_IsStableAcrossAZoomOctave_AndNeverExceedsTheRequestedBudget()
    {
        // A decimated path is cached; recomputing the tolerance straight from the zoom would rebuild
        // every contour on every frame of a zoom gesture, costing more than the tier saves.
        const double budgetPx = 0.5;
        long at1 = LayoutRenderDetail.ToleranceDbu(budgetPx, 1e-4);

        // Anywhere inside the same octave of zoom the answer must not move…
        Assert.Equal(at1, LayoutRenderDetail.ToleranceDbu(budgetPx, 1.2e-4));
        Assert.Equal(at1, LayoutRenderDetail.ToleranceDbu(budgetPx, 7e-5));

        // …and one octave finer must halve it, not something arbitrary.
        Assert.Equal(at1 / 2, LayoutRenderDetail.ToleranceDbu(budgetPx, 2e-4));

        // The bucket rounds DOWN, so the error stays inside the budget it was asked for.
        Assert.True(at1 <= budgetPx / 1e-4);
    }

    [Fact]
    public void Tolerance_IsZero_WhenDisabledOrFinerThanOneDbu()
    {
        Assert.Equal(0, LayoutRenderDetail.ToleranceDbu(-1, 1e-4));       // caller disabled the tier
        Assert.Equal(0, LayoutRenderDetail.ToleranceDbu(0.5, 0));         // degenerate zoom
        Assert.Equal(0, LayoutRenderDetail.ToleranceDbu(0.5, 10.0));      // 20 px per DBU: nothing to drop
    }

    // ── Decimation itself ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Decimate_KeepsTheLastVertex_SoAClosedContourStaysClosed()
    {
        // THE REGRESSION THIS FILE EXISTS FOR. A closed contour is stored as an implicitly-closed
        // vertex list. An earlier draft dropped a trailing vertex that sat near the one before it,
        // which moved the closing edge — and every round glyph on the imported board's drill chart
        // (D, O, 0, ':' — the closed ones, and only those) disappeared from the frame.
        var ring = Ring(64, radius: 10_000);
        var thinned = LayoutRenderDetail.Decimate(ring, tolDbu: 4_000, minKeep: 3);

        Assert.True(thinned.Length < ring.Length);
        Assert.Equal(ring[0], thinned[0]);
        Assert.Equal(ring[1], thinned[1]);
        Assert.Equal(ring[^2], thinned[^2]);
        Assert.Equal(ring[^1], thinned[^1]);
    }

    [Fact]
    public void Decimate_LeavesASmallContourReferenceIdentical()
    {
        // The hard floor: below it the walk costs more than it saves, and — the reason it is a floor
        // rather than a knob — every ordinary authored primitive stays bit-for-bit what it was, so the
        // tier can only ever engage on machine-generated geometry.
        var square = new long[] { 0, 0, 1000, 0, 1000, 1000, 0, 1000 };
        Assert.Same(square, LayoutRenderDetail.Decimate(square, tolDbu: 100_000, minKeep: 3));
    }

    [Fact]
    public void Decimate_ReturnsTheOriginal_RatherThanCollapsingAContourToNothing()
    {
        // A whole small ring collapsing to a line is the one way this tier could DELETE geometry
        // instead of simplifying it: the tolerance is larger than the entire shape.
        var ring = Ring(64, radius: 100);
        Assert.Same(ring, LayoutRenderDetail.Decimate(ring, tolDbu: 1_000_000, minKeep: 3));
    }

    [Fact]
    public void Decimate_MovesNoVertex_AndDropsOnlyOnesWithinTheTolerance()
    {
        // The error bound the default is chosen against, stated as two separate facts: every surviving
        // vertex is a STORED vertex (nothing is interpolated or snapped), and every dropped one was
        // within the tolerance of the survivor that precedes it.
        const long tol = 2_000;
        var ring = Ring(512, radius: 50_000);
        var thinned = LayoutRenderDetail.Decimate(ring, tolDbu: tol, minKeep: 3);
        Assert.True(thinned.Length < ring.Length);

        var source = Enumerable.Range(0, ring.Length / 2)
                               .Select(i => (ring[2 * i], ring[2 * i + 1])).ToHashSet();
        var kept   = Enumerable.Range(0, thinned.Length / 2)
                               .Select(i => (thinned[2 * i], thinned[2 * i + 1])).ToList();
        Assert.All(kept, k => Assert.Contains(k, source));

        // Walk the source in order: each vertex is either kept, or within tol of the last kept one.
        int k2 = 0;
        long lx = thinned[0], ly = thinned[1];
        for (int i = 0; i < ring.Length / 2; i++)
        {
            long x = ring[2 * i], y = ring[2 * i + 1];
            if (k2 < kept.Count && kept[k2] == (x, y)) { lx = x; ly = y; k2++; continue; }
            Assert.True(System.Math.Abs(x - lx) < tol && System.Math.Abs(y - ly) < tol,
                        $"vertex {i} was dropped but is further than {tol} from the last kept vertex");
        }
        Assert.Equal(kept.Count, k2);
    }

    // ── The outline decision — ONE per frame ─────────────────────────────────────────────────────
    //
    // Owner, 2026-09-04: with a single layer showing, zooming in and out gave different shapes their
    // outline at different zoom levels, and the editor read as malfunctioning. Every gate below is
    // about the decision being UNIFORM and STABLE, not about it being cheap.

    private static LayoutView BoardOf(int layers, int shapesPerLayer, int vertsPerShape)
    {
        var view = MakeView();
        for (int l = 0; l < layers; l++)
            for (int i = 0; i < shapesPerLayer; i++)
                view.Shapes.Add(new PolygonShape
                {
                    Layer = new LayerKey(l + 1, 0),
                    Xy = Ring(vertsPerShape, radius: 400_000, cx: i * 1_000_000, cy: l * 1_000_000),
                });
        view.NotifyChanged(LayoutChangeInfo.Full);
        return view;
    }

    private static Technology TechFor(int layers, params int[] visible)
    {
        var vis = visible.ToHashSet();
        return new Technology
        {
            Name = "T", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
            Layers = Enumerable.Range(0, layers).Select(l => new LayerDef
            {
                Key = new LayerKey(l + 1, 0), Name = $"L{l}",
                Color = new CircuitRF.Design.Theming.Rgba(255, 0, 0),
                FillOpacity = 0.5, ZOrder = l, Visible = vis.Contains(l), Selectable = true,
            }).ToList(),
        };
    }

    [Fact]
    public void OneLayerVisible_IsOutlined_AtEveryZoom()
    {
        // The owner's own rule, and it is not a special case anywhere in the code — one layer's
        // geometry simply does not reach the budget, so the answer is the same at every zoom.
        var view = BoardOf(layers: 8, shapesPerLayer: 40, vertsPerShape: 64);
        var tech = TechFor(8, visible: 0);
        const long budget = LayoutRenderer.DefaultOutlineVertexBudget;

        foreach (double zoom in new[] { 1e-7, 1e-6, 1e-5, 1e-4 })
            Assert.True(
                LayoutRenderDetail.CanAffordOutlines(view, tech, new LayoutViewport(0, 0, zoom, 800, 600), budget),
                $"a single visible layer must stay outlined at zoom {zoom}");
    }

    [Fact]
    public void EveryLayerVisible_DropsOutlines_UntilTheViewportClosesIn()
    {
        // …and the transition is MONOTONE in zoom: once outlines are on, zooming further in can never
        // take them away again. A user zooming in watches detail arrive and stay.
        var view = BoardOf(layers: 20, shapesPerLayer: 60, vertsPerShape: 256);
        var tech = TechFor(20, visible: Enumerable.Range(0, 20).ToArray());
        const long budget = LayoutRenderer.DefaultOutlineVertexBudget;

        bool wasOn = false;
        foreach (double zoom in new[] { 1e-8, 1e-7, 1e-6, 1e-5, 1e-4, 1e-3 })
        {
            bool on = LayoutRenderDetail.CanAffordOutlines(view, tech, new LayoutViewport(0, 0, zoom, 800, 600), budget);
            if (wasOn) Assert.True(on, $"outlines came back off at zoom {zoom} — the flip must be monotone");
            wasOn = on;
        }
        Assert.True(wasOn, "zoomed far enough in, outlines must be affordable");

        Assert.False(LayoutRenderDetail.CanAffordOutlines(view, tech, new LayoutViewport(0, 0, 1e-8, 800, 600), budget));
    }

    [Fact]
    public void TheDecisionDoesNotDependOnWhereTheViewportIs()
    {
        // THE GATE THIS REDESIGN EXISTS FOR. The honest cost measure is the vertex count actually on
        // screen — and using it would trade per-shape popping for per-PAN popping, which is the same
        // complaint from the user's side. So the answer is a function of the visible layers and the
        // zoom only. This holds even over a deliberately lopsided design, where a viewport-based
        // measure would swing hard between the crowded end and the empty one.
        var view = MakeView();
        for (int i = 0; i < 400; i++)                                    // everything crowded into one corner
            view.Shapes.Add(new PolygonShape { Layer = new LayerKey(1, 0), Xy = Ring(256, 200_000, cx: i * 500_000) });
        view.Shapes.Add(new PolygonShape { Layer = new LayerKey(1, 0), Xy = Ring(32, 200_000, cx: 900_000_000) });
        view.NotifyChanged(LayoutChangeInfo.Full);
        var tech = TechFor(1, visible: 0);

        const double zoom = 1e-6;
        bool atOrigin = LayoutRenderDetail.CanAffordOutlines(view, tech, new LayoutViewport(0, 0, zoom, 800, 600), 50_000);
        foreach (long panX in new long[] { 50_000_000, 400_000_000, 890_000_000 })
            Assert.Equal(atOrigin, LayoutRenderDetail.CanAffordOutlines(
                view, tech, new LayoutViewport(panX, 0, zoom, 800, 600), 50_000));
    }

    [Fact]
    public void HidingLayersBringsOutlinesBack_WithoutMovingTheViewport()
    {
        var view = BoardOf(layers: 20, shapesPerLayer: 60, vertsPerShape: 256);
        var vp = new LayoutViewport(0, 0, 1e-7, 800, 600);
        const long budget = LayoutRenderer.DefaultOutlineVertexBudget;

        Assert.False(LayoutRenderDetail.CanAffordOutlines(view, TechFor(20, Enumerable.Range(0, 20).ToArray()), vp, budget));
        Assert.True(LayoutRenderDetail.CanAffordOutlines(view, TechFor(20, 0, 1), vp, budget));
    }

    [Fact]
    public void ANegativeBudgetAlwaysOutlines()
    {
        var view = BoardOf(layers: 20, shapesPerLayer: 60, vertsPerShape: 256);
        var tech = TechFor(20, Enumerable.Range(0, 20).ToArray());
        Assert.True(LayoutRenderDetail.CanAffordOutlines(view, tech, new LayoutViewport(0, 0, 1e-9, 800, 600), -1));
    }

    // ── End to end, through the renderer ─────────────────────────────────────────────────────────

    private static LayoutView OverResolvedPour()
    {
        var view = MakeView();
        view.Shapes.Add(new PolygonShape { Layer = LayerA, Xy = Ring(4096, radius: 20_000_000) });
        return view;
    }

    [Fact]
    public void ANegativeThreshold_ReproducesTheUndecimatedFrameExactly()
    {
        // The escape hatch every export uses (LayoutClipboard.ExportOptions) and the way a test pins
        // the exact geometry the tier has to be measured against.
        var view = OverResolvedPour();
        var tech = MakeTech();
        var vp = new LayoutViewport(-25_000_000, -25_000_000, 400.0 / 50_000_000, 400, 400);

        var a = RenderBytes(view, tech, vp, new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, DetailPixelThreshold = -1 });
        var b = RenderBytes(view, tech, vp, new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, DetailPixelThreshold = -1 });
        Assert.Equal(a, b);   // and the render is deterministic, which is what makes the diffs below mean anything
    }

    [Fact]
    public void ZoomedOut_TheTierChangesTheFrame_AndZoomedIn_ItDoesNot()
    {
        // The whole contract in one gate: the detail budget engages only where the screen cannot show
        // the geometry, and gets out of the way — to the pixel — where it can.
        var view = OverResolvedPour();
        var tech = MakeTech();

        var far = new LayoutViewport(-25_000_000, -25_000_000, 400.0 / 50_000_000, 400, 400);
        Assert.NotEqual(
            RenderBytes(view, tech, far, new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, DetailPixelThreshold = -1 }),
            RenderBytes(view, tech, far, new LayoutRenderOptions { Theme = LayoutRenderTheme.Light }));

        // Close enough that the 4,096 vertices of a 20 mm ring are further apart than the budget.
        var near = new LayoutViewport(-100_000, -100_000, 400.0 / 200_000, 400, 400);
        Assert.Equal(
            RenderBytes(view, tech, near, new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, DetailPixelThreshold = -1 }),
            RenderBytes(view, tech, near, new LayoutRenderOptions { Theme = LayoutRenderTheme.Light }));
    }

    [Fact]
    public void TheCachedPathIsRebuiltWhenTheToleranceChanges()
    {
        // The one failure mode a decimation tier can have: a shape that stays coarse after zooming in,
        // because the path cached for the far view outlived the zoom level it was thinned for. Driven
        // through the real cache — the same instance across both frames, as LayoutCanvas keeps one for
        // the life of a document.
        var view = OverResolvedPour();
        var tech = MakeTech();
        var shared = new LayoutPathCache(1000);

        var far  = new LayoutViewport(-25_000_000, -25_000_000, 400.0 / 50_000_000, 400, 400);
        var near = new LayoutViewport(-100_000, -100_000, 400.0 / 200_000, 400, 400);

        RenderBytes(view, tech, far, new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, PathCache = shared });
        var warmedThenZoomed = RenderBytes(view, tech, near, new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, PathCache = shared });

        var exactNear = RenderBytes(view, tech, near, new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, DetailPixelThreshold = -1 });
        Assert.Equal(exactNear, warmedThenZoomed);
    }

    [Fact]
    public void AnOverResolvedShapeIsStillDrawn_NeverDropped()
    {
        // Same guarantee R-L2c-1 makes for the sub-pixel tier: less detail, never less geometry.
        var view = OverResolvedPour();
        var tech = MakeTech();
        var vp = new LayoutViewport(-25_000_000, -25_000_000, 400.0 / 50_000_000, 400, 400);

        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        var result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, new LayoutRenderOptions { Theme = LayoutRenderTheme.Light });
        Assert.Equal(1, result.ShapesDrawn);

        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        var centre = bmp.GetPixel(200, 200);
        Assert.True(centre.Red > centre.Green + 30, $"the pour must still be filled at the centre, got {centre}");
    }

    // ── The visibility floor, which the frame-wide decision never reaches ─────────────────────────

    private static Technology TechOneLayer(double fillOpacity = 0.5) => new()
    {
        Name = "T", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers = [new LayerDef
        {
            Key = LayerA, Name = "L1", Color = new CircuitRF.Design.Theming.Rgba(255, 0, 0),
            FillOpacity = fillOpacity, ZOrder = 0, Visible = true, Selectable = true,
        }],
    };

    private static int RedPixels(LayoutView view, Technology tech, LayoutViewport vp, long budget)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp,
            new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, OutlineVertexBudget = budget });
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red > c.Green + 20 && c.Red > c.Blue + 20) n++;
            }
        return n;
    }

    [Fact]
    public void ASubPixelShapeRendersTheSame_WhetherOrNotTheFrameOutlines()
    {
        // THE REGRESSION. A sub-pixel shape's fill is drawn at the layer's (partial) opacity and it is
        // the opaque pass that makes it read at all — so when the frame stopped outlining, every
        // decimal point and colon on the imported board's drill charts went with it and "0.2" rendered
        // as "0 2". Sub-pixel IS under the visibility floor by definition; the frame-wide decision must
        // not reach it.
        var view = MakeView();
        for (int i = 0; i < 40; i++)
            view.Shapes.Add(new RectShape { Layer = LayerA, X1 = i * 400_000, Y1 = 0, X2 = i * 400_000 + 200, Y2 = 200 });
        view.NotifyChanged(LayoutChangeInfo.Full);

        var tech = TechOneLayer();
        var vp = new LayoutViewport(-200_000, -8_000_000, 400.0 / 16_000_000, 400, 400);

        int outlined  = RedPixels(view, tech, vp, -1);
        int notOutlined = RedPixels(view, tech, vp, 1);   // budget of 1 vertex: the frame cannot outline
        Assert.True(outlined > 0, "the sub-pixel marks must be visible at all");
        Assert.Equal(outlined, notOutlined);
    }

    [Fact]
    public void AHairlineClosedPathKeepsItsOutline_WhenTheFrameDropsOutlines()
    {
        // The other half of the floor, and a separate mechanism: a closed hairline centreline strokes
        // to a RING, so it cannot go through the batched widened-fill tier (one shape's hole is
        // cancelled by another's winding). Its fill is a ring nothing can see, so the outline IS the
        // shape — which is how the round glyphs, and only the round ones, disappeared.
        var view = MakeView();
        view.Shapes.Add(new PathShape { Layer = LayerA, Width = 200, End = PathEndStyle.Round, Xy = Ring(24, radius: 300_000) });
        view.NotifyChanged(LayoutChangeInfo.Full);

        var tech = TechOneLayer();
        var vp = new LayoutViewport(-1_000_000, -1_000_000, 400.0 / 2_000_000, 400, 400);

        Assert.True(RedPixels(view, tech, vp, 1) > 0, "a hairline ring must survive a frame that is not outlining");
    }

    [Fact]
    public void AnOrdinaryShapeDoesLoseItsOutline_SoTheTierIsDoingSomething()
    {
        // The control for the two gates above: a shape whose fill the viewer can plainly see IS
        // subject to the frame-wide decision, or the floor would just be "always outline".
        var view = MakeView();
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 1_000_000, Y2 = 1_000_000 });
        view.NotifyChanged(LayoutChangeInfo.Full);

        var tech = TechOneLayer(fillOpacity: 0.25);
        var vp = new LayoutViewport(-200_000, -200_000, 400.0 / 1_400_000, 400, 400);

        Assert.NotEqual(RedPixels(view, tech, vp, -1), RedPixels(view, tech, vp, 1));
    }

    // ── Placed cells: the same two decisions, one level down ─────────────────────────────────────
    //
    // Owner, 2026-09-04: apply the whole scheme to instances too. Measured on the imported board PLACED
    // as a cell rather than drawn as top-level shapes — identical geometry, and before this it cost
    // 226 ms a frame against the flat version's 18 ms, because neither the frame-wide outline
    // decision nor the decimation tolerance reached compiled cell geometry, and the outline BUDGET
    // could not even see it.

    private sealed class CellFixture : IDisposable
    {
        public readonly string WorkspaceDir;
        public readonly string CellRef;

        public CellFixture(Action<LayoutView> populate)
        {
            WorkspaceDir = Path.Combine(Path.GetTempPath(), "crfDetailInst_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(WorkspaceDir);
            CellLayoutResolver.InvalidateUnder(WorkspaceDir);

            var cellDir = CellFolder.CreateCellFolder(WorkspaceDir, "Cell");
            var view = MakeView();
            populate(view);
            LayoutPersistence.SaveToFile(
                Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "main.clay"), view);
            CellRef = Path.GetRelativePath(WorkspaceDir, cellDir);
        }

        public LayoutView Place()
        {
            var top = MakeView();
            top.Instances.Add(new LayoutInstance
            {
                CellRef = CellRef, X = 0, Y = 0, Rot = LayoutRotation.R0, Mag = 1, Rows = 1, Cols = 1,
            });
            top.NotifyChanged(LayoutChangeInfo.Full);
            return top;
        }

        public void Dispose()
        {
            CellLayoutResolver.InvalidateUnder(WorkspaceDir);
            if (Directory.Exists(WorkspaceDir)) Directory.Delete(WorkspaceDir, recursive: true);
        }
    }

    private static byte[] RenderInstance(CellFixture fx, LayoutView top, Technology tech,
                                         LayoutViewport vp, LayoutRenderOptions opts)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        LayoutRenderer.Draw(surface.Canvas, top, tech, vp, opts with { BaseDir = fx.WorkspaceDir });
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.Bytes;
    }

    [Fact]
    public void TheFirstFrameOfAPlacedCellLooksLikeTheSecond()
    {
        // THE ORDERING BUG, and it is only reachable through instances. The outline decision reads the
        // spatial index's EXTENT to work out how much of the design is on screen, and it was the
        // INSTANCE query that put the placements into that index — which used to run after the layer
        // loop. So on a document whose geometry is all in placed cells (a schematic-generated layout
        // has no top-level shapes at all) the first frame asked before anything had answered, got
        // "empty", and decided differently from every frame after it: a visible flicker on open.
        using var fx = new CellFixture(v =>
        {
            for (int i = 0; i < 400; i++)
                v.Shapes.Add(new PolygonShape { Layer = LayerA, Xy = Ring(256, 200_000, cx: (i % 20) * 500_000, cy: (i / 20) * 500_000) });
        });

        var tech = TechOneLayer();
        var vp = new LayoutViewport(-500_000, -500_000, 400.0 / 11_000_000, 400, 400);
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light };

        // A FRESH top-level view each time, so each render is genuinely somebody's first frame.
        var first  = RenderInstance(fx, fx.Place(), tech, vp, opts);
        var second = RenderInstance(fx, fx.Place(), tech, vp, opts);
        var third  = RenderInstance(fx, fx.Place(), tech, vp, opts);

        Assert.Equal(second, first);
        Assert.Equal(third, second);
    }

    [Fact]
    public void APlacedCellIsCountedByTheOutlineBudget()
    {
        // Without this the budget was blind to exactly the design shape where it mattered most: a
        // layout with no top-level shapes counted zero and always "afforded" outlines.
        using var fx = new CellFixture(v =>
        {
            for (int i = 0; i < 600; i++)
                v.Shapes.Add(new PolygonShape { Layer = LayerA, Xy = Ring(256, 200_000, cx: (i % 25) * 500_000, cy: (i / 25) * 500_000) });
        });

        var top = fx.Place();
        var tech = TechOneLayer();
        var vp = new LayoutViewport(-500_000, -500_000, 400.0 / 13_000_000, 400, 400);

        Assert.Empty(top.Shapes);   // the point: nothing at the top level to count
        Assert.False(LayoutRenderDetail.CanAffordOutlines(top, tech, vp, 50_000, fx.WorkspaceDir));
        Assert.True(LayoutRenderDetail.CanAffordOutlines(top, tech, vp, 50_000_000, fx.WorkspaceDir));
    }

    [Fact]
    public void APlacedCellIsDecimated_AndANegativeThresholdStillPinsExactGeometry()
    {
        using var fx = new CellFixture(v =>
        {
            for (int i = 0; i < 200; i++)
                v.Shapes.Add(new PolygonShape { Layer = LayerA, Xy = Ring(2048, 400_000, cx: (i % 20) * 1_000_000, cy: (i / 20) * 1_000_000) });
        });

        var tech = TechOneLayer();
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light };

        // Far out, where the cell carries far more vertices than the frame can show.
        var far = new LayoutViewport(-1_000_000, -1_000_000, 400.0 / 22_000_000, 400, 400);
        Assert.NotEqual(
            RenderInstance(fx, fx.Place(), tech, far, opts with { DetailPixelThreshold = -1 }),
            RenderInstance(fx, fx.Place(), tech, far, opts));

        // Close in, where it does not — the tier gets out of the way to the pixel, through the
        // compiled-cell cache exactly as it does through the per-shape one.
        //
        // 600,000 DBU across the frame, not 900,000, and the difference is the WHOLE point of picking
        // this number by arithmetic rather than by eye. Decimation compares CHEBYSHEV distance, so on
        // a circle the tightest spacing is not the arc step (1,227 DBU here) but its diagonal
        // projection, 1,227/sqrt(2) = 868. At 900,000 across the tolerance buckets to 1,024 — under
        // the arc step, over the diagonal one — so the ring still thins on its 45-degree runs and only
        // there. 600,000 buckets to 512, safely under both.
        var near = new LayoutViewport(-300_000, -300_000, 400.0 / 600_000, 400, 400);
        Assert.Equal(
            RenderInstance(fx, fx.Place(), tech, near, opts with { DetailPixelThreshold = -1 }),
            RenderInstance(fx, fx.Place(), tech, near, opts));
    }

    [Fact]
    public void ZoomingBackOutReusesTheCompileItAlreadyPaidFor()
    {
        // The compiled-cell cache keeps a small RING of tolerances rather than one slot, so a zoom
        // gesture does not recompile the whole cell each way. Asserted through PathsConstructed —
        // a counter, not a clock: returning to a tolerance already compiled must build no paths.
        using var fx = new CellFixture(v =>
        {
            for (int i = 0; i < 100; i++)
                v.Shapes.Add(new PolygonShape { Layer = LayerA, Xy = Ring(512, 300_000, cx: (i % 10) * 800_000, cy: (i / 10) * 800_000) });
        });

        var tech = TechOneLayer();
        var top = fx.Place();
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, BaseDir = fx.WorkspaceDir };

        var far  = new LayoutViewport(-500_000, -500_000, 400.0 / 9_000_000, 400, 400);
        var near = far.WithZoomAnchoredAt(far.Zoom * 16, 200, 200);

        LayoutRenderResult Frame(LayoutViewport vp)
        {
            using var surface = SKSurface.Create(new SKImageInfo(400, 400));
            return LayoutRenderer.Draw(surface.Canvas, top, tech, vp, opts);
        }

        Frame(far);                                   // compiles at the far tolerance
        Assert.True(Frame(near).PathsConstructed > 0, "a new tolerance must compile");
        Assert.Equal(0, Frame(far).PathsConstructed); // …and coming back must not
    }
}
