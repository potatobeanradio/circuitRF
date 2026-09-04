// Two zoom-dependent rendering defects found on an imported board's Drill Map layer (2026-09-04) —
// see src/Ui/RESOLVED.md, "The drill map rendered differently at every zoom step".
//
//   1. The R-L2c-2 merge tier batched CLOSED PathShapes into one NonZero-filled path. A closed
//      centreline strokes to a ring whose winding nothing normalizes, so two of them cancelled —
//      geometry deleted, not coarsened. IsOpenCentreline already states this theorem for the elision
//      aggregate; the merge tier had never been given the guard.
//   2. The LOD substitutions (hairline elision, the mustOutline visibility floor) painted SOLID while
//      their un-substituted neighbours on the SAME layer painted at the layer's fill alpha, so one
//      layer rendered in two brightnesses and which half a shape fell in moved with the zoom.

using CircuitRF.Design.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

public class LayoutMergeTierRingTests
{
    private static readonly LayerKey LayerA = new(1, 0);

    private static Technology MakeTech(double fillOpacity = 0.35) => new()
    {
        Name = "T", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers =
        [
            new LayerDef
            {
                Key = LayerA, Name = "L1", Color = new CircuitRF.Design.Theming.Rgba(255, 128, 96),
                FillOpacity = fillOpacity, ZOrder = 0, Visible = true, Selectable = true,
            },
        ],
    };

    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    /// <summary>A closed rectangular centreline, wound CW or CCW as asked — the two orientations an
    /// importer emits interchangeably, and the pair that used to cancel once merged.</summary>
    private static PathShape ClosedRect(long x1, long y1, long x2, long y2, long width, bool clockwise)
    {
        long[] ccw = [x1, y1, x2, y1, x2, y2, x1, y2, x1, y1];
        if (!clockwise) return new PathShape { Layer = LayerA, Xy = ccw, Width = width, End = PathEndStyle.Flush };
        long[] cw = new long[ccw.Length];
        int n = ccw.Length / 2;
        for (int i = 0; i < n; i++) { cw[2 * i] = ccw[2 * (n - 1 - i)]; cw[2 * i + 1] = ccw[2 * (n - 1 - i) + 1]; }
        return new PathShape { Layer = LayerA, Xy = cw, Width = width, End = PathEndStyle.Flush };
    }

    private static SKBitmap Render(LayoutView view, Technology tech, LayoutViewport vp, LayoutRenderOptions opts)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        using var img = surface.Snapshot();
        return SKBitmap.FromImage(img);
    }

    private static int LitPixels(SKBitmap bmp, SKColor background)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (System.Math.Abs(c.Red - background.Red) > 6
                    || System.Math.Abs(c.Green - background.Green) > 6
                    || System.Math.Abs(c.Blue - background.Blue) > 6) n++;
            }
        return n;
    }

    // ── Defect 1 — the merge tier may not batch a closed centreline ─────────────────────────────

    [Fact]
    public void TwoNestedClosedPaths_OfOppositeWinding_SurviveTheMergeTier()
    {
        // Nested, so the outer one's ring encloses the inner one's — the arrangement a drill chart's
        // border and its cell rules form, and the one IsOpenCentreline records as the failure case.
        var view = MakeView();
        view.Shapes.Add(ClosedRect(10_000, 10_000, 190_000, 190_000, 4_000, clockwise: true));
        view.Shapes.Add(ClosedRect(60_000, 60_000, 140_000, 140_000, 4_000, clockwise: false));
        var tech = MakeTech();

        var vp = new LayoutViewport(0, 0, 400.0 / 200_000, 400, 400);
        var baseOpts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };

        using var individual = Render(view, tech, vp, baseOpts);
        using var merged = Render(view, tech, vp, baseOpts with { ForceMergeTier = true });

        var bg = individual.GetPixel(2, 2);
        int individualLit = LitPixels(individual, bg);
        int mergedLit = LitPixels(merged, bg);

        Assert.True(individualLit > 0, "the un-merged render drew nothing — the fixture is wrong, not the tier");

        // Merging is allowed to composite overlaps differently. It is NOT allowed to delete a ring or
        // flood its interior, which is what an unguarded NonZero batch does to this pair: the two rings
        // cancel and the figure either vanishes or fills solid.
        Assert.InRange(mergedLit, (int)(individualLit * 0.9), (int)(individualLit * 1.1));
    }

    [Fact]
    public void AClosedPath_KeepsItsHole_WhenTheLayerMerges()
    {
        // The single-ring case, stated on its own: a closed centreline's interior is a HOLE, and a
        // merged layer must not fill it. This is what "the table cells appear to be filled" was.
        var view = MakeView();
        view.Shapes.Add(ClosedRect(20_000, 20_000, 180_000, 180_000, 4_000, clockwise: true));
        // A second, unrelated closed ring elsewhere on the layer is enough to give the batch a partner.
        view.Shapes.Add(ClosedRect(60_000, 60_000, 140_000, 140_000, 4_000, clockwise: false));
        var tech = MakeTech();

        var vp = new LayoutViewport(0, 0, 400.0 / 200_000, 400, 400);
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, ForceMergeTier = true };
        using var bmp = Render(view, tech, vp, opts);

        var bg = bmp.GetPixel(2, 2);
        // Dead centre is inside BOTH rings' holes and touches neither wall.
        var centre = bmp.GetPixel(200, 200);
        Assert.True(
            System.Math.Abs(centre.Red - bg.Red) <= 6
            && System.Math.Abs(centre.Green - bg.Green) <= 6
            && System.Math.Abs(centre.Blue - bg.Blue) <= 6,
            $"the merged layer filled the rings' interior: centre {centre} vs background {bg}");
    }

    // ── Defect 2 — a substitution matches the frame's own outline decision ───────────────────────

    [Fact]
    public void WithOutlinesOff_AHairlineElidedPath_DoesNotOutshineAnUnelidedOne()
    {
        // Two paths on one layer, alike in every way except width: one under the 1-device-pixel
        // hairline threshold (so it is substituted by a widened fill), one over it (so it is not).
        // They must read as the same material. Before the fix the elided one was painted solid and its
        // neighbour at 35%, which is what made one drill chart render half bright and half ghosted.
        var view = MakeView();
        view.Shapes.Add(new PathShape { Layer = LayerA, Xy = [20_000, 140_000, 180_000, 140_000], Width = 300, End = PathEndStyle.Flush });
        view.Shapes.Add(new PathShape { Layer = LayerA, Xy = [20_000, 60_000, 180_000, 60_000], Width = 3_000, End = PathEndStyle.Flush });
        var tech = MakeTech();

        // 400 px over 200,000 DBU = 0.002 device px per DBU: the 300-DBU path is 0.6 px wide (elides),
        // the 3,000-DBU path is 6 px (does not). Outlines forced off for the whole frame.
        var vp = new LayoutViewport(0, 0, 400.0 / 200_000, 400, 400);
        var opts = new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false, OutlineVertexBudget = 1,
        };
        using var bmp = Render(view, tech, vp, opts);

        var bg = bmp.GetPixel(2, 2);
        var thin = bmp.GetPixel(200, 120);    // on the elided path
        var thick = bmp.GetPixel(200, 280);   // on the plain-filled path

        Assert.True(System.Math.Abs(thin.Red - bg.Red) > 6 || System.Math.Abs(thin.Green - bg.Green) > 6,
            "the hairline path did not draw at all — the fixture is wrong");

        // Same layer, same colour, same (absent) outline: the two must land within a few levels of one
        // another. Solid-vs-35% is a ~100-level gap on this palette.
        Assert.InRange(thin.Green, thick.Green - 24, thick.Green + 24);
        Assert.InRange(thin.Blue, thick.Blue - 24, thick.Blue + 24);
    }

    [Fact]
    public void WithOutlinesOn_TheElidedFill_IsStillSolid()
    {
        // The other half of the rule, and the reason this is not simply "stop painting solid": when the
        // frame IS outlining, the widened fill stands in for a fill PLUS a solid outline, so solid is
        // what reproduces it. This pins the behaviour the fix deliberately leaves alone.
        var view = MakeView();
        view.Shapes.Add(new PathShape { Layer = LayerA, Xy = [20_000, 140_000, 180_000, 140_000], Width = 300, End = PathEndStyle.Flush });
        view.Shapes.Add(new PathShape { Layer = LayerA, Xy = [20_000, 60_000, 180_000, 60_000], Width = 3_000, End = PathEndStyle.Flush });
        var tech = MakeTech();

        var vp = new LayoutViewport(0, 0, 400.0 / 200_000, 400, 400);
        var opts = new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false, OutlineVertexBudget = -1,   // always outline
        };
        using var bmp = Render(view, tech, vp, opts);

        var thin = bmp.GetPixel(200, 120);
        // The layer colour is (255,128,96) at full alpha; a 35% fill over white is far lighter.
        Assert.InRange(thin.Green, 118, 138);
        Assert.InRange(thin.Blue, 86, 106);
    }
}
