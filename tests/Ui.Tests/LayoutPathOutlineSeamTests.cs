using System.Collections.Generic;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

// ── L1 fix: Path outline internal seams (brief-L1-fix-path-seams-and-live-tech.md §1) ──
// GetFillPath emits one overlapping contour per segment plus a wedge per join — fine when FILLING
// (Winding composites the overlap once) but visible when hairline-STROKING the same path (every
// internal contour boundary draws). BuildPathOutline now runs the result through SKPath.Simplify
// before returning it; these tests pin that fix directly, without depending on a real Technology.

public class LayoutPathOutlineSeamTests
{
    private static Technology MakeTech(LayerKey key, SKColor color, double fillOpacity) => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers =
        [
            new LayerDef
            {
                Key = key, Name = "L1", Color = new CircuitRF.Ui.Theming.Rgba(color.Red, color.Green, color.Blue),
                FillOpacity = fillOpacity, ZOrder = 0, Visible = true, Selectable = true,
            },
        ],
    };

    private static (SKSurface Surface, LayoutRenderResult Result) Render(LayoutView view, Technology tech, LayoutViewport vp)
    {
        var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };
        var result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        return (surface, result);
    }

    // ── No internal seams at a bend ───────────────────────────────────────────

    [Fact]
    public void PathWithNinetyDegreeBend_ScanlineThroughTheJoint_HasExactlyTwoSilhouetteEdges()
    {
        var key = new LayerKey(1, 0);
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000, AngleMode = AngleMode.AnyAngle };
        // 3-vertex centerline: right, then a 90° bend upward. The bend/join sits at (100_000, 0).
        view.Shapes.Add(new PathShape
        {
            Layer = key,
            Xy = [0, 0, 100_000, 0, 100_000, 100_000],
            Width = 40_000,
            End = PathEndStyle.Flush,
        });
        // Low fill opacity so the (opaque) stroke color is unmistakably distinct from both the
        // faint fill tint and the background — never ambiguous with either.
        var tech = MakeTech(key, new SKColor(20, 20, 220), 0.12);

        var bb = LayoutGeometry.BboxOf(view.Shapes[0]);
        var vp = LayoutViewport.ZoomToFit(bb, 500, 500, 0.15);
        var (surface, _) = Render(view, tech, vp);

        // World Y = 0 is the horizontal segment's own centerline, which runs straight through the
        // joint into the vertical segment's footprint with no cap or gap — a single contiguous
        // interior span if (and only if) the outline has no internal seam.
        int screenY = (int)vp.WorldToScreenY(0);
        int edgeRuns = CountStrokeColorRuns(surface, screenY);

        Assert.Equal(2, edgeRuns); // exactly the left and right silhouette edges — no seam in between

        surface.Dispose();
    }

    private static bool IsOpaqueStrokeBlue(SKColor c) => c.Red < 100 && c.Green < 100 && c.Blue > 120;

    private static int CountStrokeColorRuns(SKSurface surface, int y)
    {
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        y = System.Math.Clamp(y, 0, bmp.Height - 1);

        int runs = 0;
        bool inRun = false;
        for (int x = 0; x < bmp.Width; x++)
        {
            bool matches = IsOpaqueStrokeBlue(bmp.GetPixel(x, y));
            if (matches && !inRun) { runs++; inRun = true; }
            else if (!matches) inRun = false;
        }
        return runs;
    }

    // ── Silhouette preserved: Simplify doesn't shrink or distort the outline ──

    [Fact]
    public void SimplifiedOutline_BoundsMatchTheUnsimplifiedGetFillPathResult()
    {
        var shape = new PathShape
        {
            Layer = new LayerKey(1, 0),
            Xy = [0, 0, 100_000, 0, 100_000, 100_000],
            Width = 40_000,
            End = PathEndStyle.Flush,
        };
        var ps = new LayoutRenderer.PathSpace(0, 0, 1.0);

        // The raw (pre-Simplify) outline, built the same way BuildPathOutline does internally —
        // a straight-line-only centerline, so a plain MoveTo/LineTo replicates it exactly.
        using var centerline = new SKPath();
        centerline.MoveTo(ps.X(shape.Xy[0]), ps.Y(shape.Xy[1]));
        for (int i = 1; i < shape.Xy.Length / 2; i++)
            centerline.LineTo(ps.X(shape.Xy[2 * i]), ps.Y(shape.Xy[2 * i + 1]));

        using var strokeForFill = new SKPaint
        {
            Style = SKPaintStyle.Stroke, StrokeWidth = ps.Len(shape.Width),
            StrokeCap = SKStrokeCap.Butt, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true,
        };
        using var rawOutline = new SKPath();
        strokeForFill.GetFillPath(centerline, rawOutline);

        using var simplifiedOutline = LayoutRenderer.BuildPathOutline(shape, ps)!;

        var rawBounds = rawOutline.Bounds;
        var simplifiedBounds = simplifiedOutline.Bounds;

        const float tol = 0.5f;
        Assert.True(System.Math.Abs(rawBounds.Left - simplifiedBounds.Left) <= tol
            && System.Math.Abs(rawBounds.Top - simplifiedBounds.Top) <= tol
            && System.Math.Abs(rawBounds.Right - simplifiedBounds.Right) <= tol
            && System.Math.Abs(rawBounds.Bottom - simplifiedBounds.Bottom) <= tol,
            $"raw={rawBounds} simplified={simplifiedBounds}");
    }

    // ── Degenerate input: never throws, never drops the shape (returns non-null) ─────────

    public static IEnumerable<object[]> DegenerateShapes()
    {
        yield return new object[] { "identical points", new PathShape { Layer = new LayerKey(1, 0), Xy = [5000, 5000, 5000, 5000], Width = 10_000 } };
        yield return new object[] { "zero width",        new PathShape { Layer = new LayerKey(1, 0), Xy = [0, 0, 100_000, 0], Width = 0 } };
        yield return new object[] { "identical + zero width", new PathShape { Layer = new LayerKey(1, 0), Xy = [5000, 5000, 5000, 5000], Width = 0 } };
    }

    [Theory]
    [MemberData(nameof(DegenerateShapes))]
    public void DegenerateInput_DoesNotThrow_ReturnsNonNullOutline(string name, PathShape shape)
    {
        var ps = new LayoutRenderer.PathSpace(0, 0, 1.0);
        SKPath? outline = null;
        var ex = Record.Exception(() => outline = LayoutRenderer.BuildPathOutline(shape, ps));

        Assert.Null(ex);
        Assert.True(outline is not null, $"{name}: outline must not be null (the shape must not be silently dropped)");
        outline?.Dispose();
    }
}
