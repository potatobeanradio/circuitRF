// Test-only stand-in for L2c's not-yet-built R8b merge tier (docs/design/layout-view.md §2.3 R8b,
// §5.3 item 2) — used ONLY to produce comparable timing numbers for the L2a baseline table's R8b
// crossover measurement (docs/sonnet-briefs/brief-L2a-performance-harness.md §3/§5). This file changes
// nothing in src/Ui/Renderers/LayoutRenderer.cs and implements no production behavior — L2a's own
// guardrail (§6) forbids building the real merge tier now.
//
// Where the real per-shape ("darkening") path draws N shapes as N separate `canvas.DrawPath(fill)`
// calls (so same-layer overlap composites darker, §2.3 R8a), this stand-in collects every shape's
// fill path into ONE merged SKPath per layer and draws it with a single `canvas.DrawPath` call — the
// same "one layer, ~two draw calls" shape L2c's real merge tier will eventually produce. It
// deliberately reuses LayoutRenderer's own internal PathSpace/BuildPathOutline (the exact geometry the
// real renderer builds for a PathShape) and duplicates the short, simple per-primitive-kind switch for
// everything else — Rect/Polygon/RoundedRect/Circle/Curve/Via — rather than trying to make
// LayoutRenderer.BuildShapePath itself reachable from a test assembly, which the brief's "only
// production change is counters" guardrail does not allow.

using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.LayoutPerf;

internal static class MergedPathBenchmarkRenderer
{
    /// <summary>Draws <paramref name="view"/> exactly like <see cref="LayoutRenderer.Draw"/> does for
    /// the background/grid, but every layer's shapes collapse into ONE fill path + one stroke path —
    /// two draw calls per visible layer, regardless of shape count. Labels and bitmaps are excluded
    /// from the merge (labels are text draws, never batchable into a fill path; bitmaps render outside
    /// the per-layer loop entirely, R-bmp-2) — same population <c>DrawLayer</c> actually merges.</summary>
    public static void Draw(SKCanvas canvas, LayoutView view, Technology? tech, LayoutViewport vp, LayoutRenderTheme theme)
    {
        canvas.Save();
        try
        {
            var clipRect = SKRect.Create(0, 0, (float)vp.Width, (float)vp.Height);
            canvas.ClipRect(clipRect);
            using (var bg = new SKPaint { Style = SKPaintStyle.Fill, Color = theme.Background })
                canvas.DrawRect(clipRect, bg);

            if (vp.Width < 1 || vp.Height < 1 || vp.Zoom <= 0) return;

            var byLayer = new Dictionary<LayerKey, List<LayoutShape>>();
            foreach (var shape in view.Shapes)
            {
                if (shape is BitmapShape or LabelShape) continue;
                if (!byLayer.TryGetValue(shape.Layer, out var list))
                    byLayer[shape.Layer] = list = [];
                list.Add(shape);
            }

            var layerMap = tech?.Layers.ToDictionary(l => l.Key);
            var resolved = new List<(LayerDef Def, List<LayoutShape> Shapes)>(byLayer.Count);
            foreach (var (key, shapes) in byLayer)
            {
                var def = layerMap is not null && layerMap.TryGetValue(key, out var found) ? found : FallbackPalette.For(key);
                resolved.Add((def, shapes));
            }
            resolved.Sort(static (a, b) => a.Def.ZOrder.CompareTo(b.Def.ZOrder));

            double centerX = vp.PanX + vp.Width / (2.0 * vp.Zoom);
            double centerY = vp.PanY + vp.Height / (2.0 * vp.Zoom);
            var (originX, originY) = LayoutRenderer.ComputeOrigin(centerX, centerY, vp.Width / vp.Zoom, vp.Height / vp.Zoom);
            double dbuToUm = 1.0 / Math.Max(1, view.DbuPerMicron);
            var ps = new LayoutRenderer.PathSpace(originX, originY, dbuToUm);

            double scaleUm = vp.Zoom / dbuToUm;
            double transX = (originX - vp.PanX) * vp.Zoom;
            double transY = vp.Height - (originY - vp.PanY) * vp.Zoom;
            var matrix = SKMatrix.CreateScaleTranslation((float)scaleUm, (float)scaleUm, (float)transX, (float)transY);

            canvas.Save();
            try
            {
                canvas.Concat(in matrix);
                foreach (var (def, shapes) in resolved)
                {
                    if (!def.Visible) continue;
                    DrawMergedLayer(canvas, def, shapes, ps, scaleUm);
                }
            }
            finally { canvas.Restore(); }
        }
        finally { canvas.Restore(); }
    }

    private static void DrawMergedLayer(SKCanvas canvas, LayerDef def, List<LayoutShape> shapes, LayoutRenderer.PathSpace ps, double scaleUm)
    {
        var color = new SKColor(def.Color.R, def.Color.G, def.Color.B);
        byte fillAlpha = (byte)Math.Clamp(Math.Round(def.FillOpacity * 255.0), 0, 255);

        using var merged = new SKPath();
        foreach (var shape in shapes)
        {
            using var p = BuildBenchmarkShapePath(shape, ps);
            if (p is null || p.IsEmpty) continue;
            merged.AddPath(p);
        }
        if (merged.IsEmpty) return;

        using var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color.WithAlpha(fillAlpha) };
        using var strokePaint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)(2.0 / Math.Max(scaleUm, 1e-12)),
            Color = color.WithAlpha(255),
        };

        canvas.DrawPath(merged, fillPaint);   // draw call 1 of 2 for this layer
        canvas.DrawPath(merged, strokePaint); // draw call 2 of 2 for this layer
    }

    /// <summary>Mirrors <c>LayoutRenderer.BuildShapePath</c>'s switch — see this file's header for why
    /// it is a duplicate rather than a shared call.</summary>
    private static SKPath? BuildBenchmarkShapePath(LayoutShape shape, LayoutRenderer.PathSpace ps)
    {
        if (shape is PathShape trace) return LayoutRenderer.BuildPathOutline(trace, ps);

        var path = new SKPath();
        switch (shape)
        {
            case RectShape r:
                path.AddRect(NormalizedRect(ps.X(r.X1), ps.Y(r.Y1), ps.X(r.X2), ps.Y(r.Y2)));
                break;
            case PolygonShape p:
                AddRing(path, p.Xy, ps);
                break;
            case RoundedRectShape rr:
            {
                var rect = NormalizedRect(ps.X(rr.X1), ps.Y(rr.Y1), ps.X(rr.X2), ps.Y(rr.Y2));
                float radius = ps.Len(rr.CornerRadius);
                path.AddRoundRect(rect, radius, radius);
                break;
            }
            case CircleShape c:
                path.AddCircle(ps.X(c.Cx), ps.Y(c.Cy), ps.Len(c.R));
                break;
            case CurveShape curve:
                AddRing(path, curve.Xy, ps); // benchmark approximation: straight ring, not edge-accurate — cost proxy only
                break;
            case ViaShape via:
                path.AddCircle(ps.X(via.X), ps.Y(via.Y), ps.Len(via.PadSize / 2.0));
                break;
            default:
                path.Dispose();
                return null;
        }
        return path;
    }

    private static void AddRing(SKPath path, long[] xy, LayoutRenderer.PathSpace ps)
    {
        int n = xy.Length / 2;
        if (n < 2) return;
        path.MoveTo(ps.X(xy[0]), ps.Y(xy[1]));
        for (int i = 1; i < n; i++) path.LineTo(ps.X(xy[2 * i]), ps.Y(xy[2 * i + 1]));
        path.Close();
    }

    private static SKRect NormalizedRect(float x1, float y1, float x2, float y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
}
