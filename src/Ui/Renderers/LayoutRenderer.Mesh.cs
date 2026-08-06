// R-em-15 — the EM mesh overlay, copying LayoutRenderOptions.ShowPCellPins' contract EXACTLY:
// screen-space, live-resolved from the EmMeshReport, never layer geometry, never counted in
// LayoutFrameCounters, never reachable by any exporter, defaulting to false so every export /
// one-shot render draws no mesh by construction. The toggle default lives at the VM layer.
//
// **Why the mesh is drawn as an inset panel rather than "over" the artwork.** The mesh lives in the
// CROSS-SECTION plane (x across the line, y above the ground plane); the layout canvas shows the
// PLAN view. There is no coordinate mapping between them, so painting mesh segments onto plan-view
// artwork would be a picture of nothing. §10.5's mesh viewer is a cross-section viewer, and an inset
// panel is what that is on this canvas.
//
// R-em-16's counterpart: everything NUMERIC about the mesh (unknown count, per-conductor and
// per-interface counts, min/max cell, truncation half-extent, every note) is surfaced by the panel
// from EmMeshReport verbatim. This file draws the picture only.

using SkiaSharp;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Ui.Renderers;

public static partial class LayoutRenderer
{
    private const float MeshPanelMarginPx  = 12f;
    private const float MeshPanelPaddingPx = 10f;

    /// <summary>Fraction of the canvas height the inset cross-section panel occupies.</summary>
    private const float MeshPanelHeightFraction = 0.38f;

    private const float MeshSegmentStrokePx  = 1.4f;
    private const float MeshBoundaryTickPx   = 3.0f;

    /// <summary>
    /// Draws the cross-section mesh as a screen-space inset. Takes no
    /// <see cref="LayoutFrameCounters"/> — deliberately: R-em-15 requires the overlay to contribute
    /// to no geometry count, and not having the parameter makes that true by construction rather
    /// than by remembering not to increment it.
    /// </summary>
    internal static void DrawEmMeshOverlay(
        SKCanvas canvas, EmMeshReport report, LayoutRenderTheme theme, double viewportW, double viewportH)
    {
        var segs = report.Mesh.Segments;
        if (segs.Count == 0 || viewportW < 60 || viewportH < 60) return;

        // Extent of everything that will be drawn, INCLUDING the truncated interface tails — hiding
        // truncation is exactly what R-mom-10 warns against ("the one place kernel A can be quietly
        // wrong"), so the picture is fitted to show it rather than cropped to the conductors.
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (var s in segs)
        {
            minX = Math.Min(minX, Math.Min(s.A.X, s.B.X));
            maxX = Math.Max(maxX, Math.Max(s.A.X, s.B.X));
            minY = Math.Min(minY, Math.Min(s.A.Y, s.B.Y));
            maxY = Math.Max(maxY, Math.Max(s.A.Y, s.B.Y));
        }
        double spanX = maxX - minX, spanY = maxY - minY;
        if (!(spanX > 0) && !(spanY > 0)) return;

        float panelH = (float)Math.Min(viewportH * MeshPanelHeightFraction, viewportH - 2 * MeshPanelMarginPx);
        float panelW = (float)(viewportW - 2 * MeshPanelMarginPx);
        if (panelH < 40 || panelW < 60) return;

        var panel = SKRect.Create(
            MeshPanelMarginPx,
            (float)viewportH - MeshPanelMarginPx - panelH,
            panelW, panelH);

        canvas.Save();
        try
        {
            canvas.ClipRect(panel);

            using (var back = new SKPaint { Color = theme.Background.WithAlpha(225), IsAntialias = false })
                canvas.DrawRect(panel, back);
            using (var edge = new SKPaint
            {
                Color = theme.EmMeshTruncation.WithAlpha(140), IsStroke = true,
                StrokeWidth = 1f, IsAntialias = true,
            })
                canvas.DrawRect(panel, edge);

            var inner = SKRect.Create(
                panel.Left + MeshPanelPaddingPx, panel.Top + MeshPanelPaddingPx,
                panel.Width - 2 * MeshPanelPaddingPx, panel.Height - 2 * MeshPanelPaddingPx);
            if (inner.Width < 10 || inner.Height < 10) return;

            // Uniform scale — an anisotropic fit would make a 35 µm metal thickness on a 1.6 mm
            // substrate look like something it is not, and thickness is exactly what the edge
            // grading is about (R-mom-8's own reference-length finding).
            double sx = spanX > 0 ? inner.Width  / spanX : double.PositiveInfinity;
            double sy = spanY > 0 ? inner.Height / spanY : double.PositiveInfinity;
            double scale = Math.Min(sx, sy);
            if (!double.IsFinite(scale) || scale <= 0) return;

            double cx = 0.5 * (minX + maxX), cy = 0.5 * (minY + maxY);
            float MapX(double x) => (float)(inner.MidX + (x - cx) * scale);
            float MapY(double y) => (float)(inner.MidY - (y - cy) * scale);   // y is UP in the cross-section

            using var conductorPaint = new SKPaint
            {
                Color = theme.EmMeshConductor, IsStroke = true,
                StrokeWidth = MeshSegmentStrokePx, IsAntialias = true, StrokeCap = SKStrokeCap.Butt,
            };
            using var interfacePaint = new SKPaint
            {
                Color = theme.EmMeshInterface, IsStroke = true,
                StrokeWidth = MeshSegmentStrokePx, IsAntialias = true, StrokeCap = SKStrokeCap.Butt,
            };
            using var boundaryPaint = new SKPaint
            {
                Color = theme.EmMeshTruncation, IsStroke = true,
                StrokeWidth = 1f, IsAntialias = true,
            };

            // Conductor and dielectric-interface segments in visibly different styles — they are
            // different unknowns (free vs. bound charge) and a user reading a mesh needs to see which.
            foreach (var s in segs)
            {
                var paint = s.Kind == EmSegmentKind.Conductor ? conductorPaint : interfacePaint;
                canvas.DrawLine(MapX(s.A.X), MapY(s.A.Y), MapX(s.B.X), MapY(s.B.Y), paint);
            }

            // The CELL BOUNDARIES, because the whole point is to see the edge grading. Every segment
            // endpoint is one; drawing a short perpendicular tick at each shows the geometric
            // progression that R-mom-8 describes far more legibly than the segments alone.
            foreach (var s in segs)
            {
                double dx = s.B.X - s.A.X, dy = s.B.Y - s.A.Y;
                double n = Math.Sqrt(dx * dx + dy * dy);
                if (n <= 0) continue;
                float px = (float)(-dy / n) * MeshBoundaryTickPx;
                float py = (float)( dx / n) * MeshBoundaryTickPx;
                float ax = MapX(s.A.X), ay = MapY(s.A.Y);
                canvas.DrawLine(ax - px, ay + py, ax + px, ay - py, boundaryPaint);
            }

            // A LOCATOR BOX around each conductor. On a true-scale panel that spans the 20-substrate-
            // height truncation (R-mom-10 requires truncation to be visible), a 2.9 mm strip 35 µm
            // thick is a handful of pixels — physically correct and useless to look at. The box does
            // not misstate the geometry: it is drawn AT the conductor's own bounds, only widened to
            // a legible minimum, so it says "the conductor is here" without redrawing it bigger.
            var byConductor = new Dictionary<int, (double MinX, double MinY, double MaxX, double MaxY)>();
            foreach (var s in segs)
            {
                if (s.Kind != EmSegmentKind.Conductor || s.ConductorIndex < 0) continue;
                if (!byConductor.TryGetValue(s.ConductorIndex, out var b))
                    b = (double.PositiveInfinity, double.PositiveInfinity,
                         double.NegativeInfinity, double.NegativeInfinity);
                byConductor[s.ConductorIndex] = (
                    Math.Min(b.MinX, Math.Min(s.A.X, s.B.X)), Math.Min(b.MinY, Math.Min(s.A.Y, s.B.Y)),
                    Math.Max(b.MaxX, Math.Max(s.A.X, s.B.X)), Math.Max(b.MaxY, Math.Max(s.A.Y, s.B.Y)));
            }

            using (var locator = new SKPaint
            {
                Color = theme.EmMeshConductor, IsStroke = true,
                StrokeWidth = MeshSegmentStrokePx, IsAntialias = true,
            })
            {
                const float minPx = 5f;
                foreach (var b in byConductor.Values)
                {
                    float l = MapX(b.MinX), r = MapX(b.MaxX);
                    float t = MapY(b.MaxY), bt = MapY(b.MinY);   // y is up, so MaxY maps to the top
                    if (r - l < minPx) { float m = 0.5f * (l + r); l = m - minPx / 2; r = m + minPx / 2; }
                    if (bt - t < minPx) { float m = 0.5f * (t + bt); t = m - minPx / 2; bt = m + minPx / 2; }
                    canvas.DrawRect(SKRect.Create(l - 2, t - 2, (r - l) + 4, (bt - t) + 4), locator);
                }
            }

            // The truncation extent, or a clear indication that it runs off-panel. A viewer that
            // hides truncation defeats the reporting the engine already does (R-mom-10).
            if (report.TruncationHalfExtent > 0)
            {
                using var dash = new SKPaint
                {
                    Color = theme.EmMeshTruncation, IsStroke = true, StrokeWidth = 1f,
                    IsAntialias = true, PathEffect = SKPathEffect.CreateDash([4f, 4f], 0),
                };
                float leftX  = MapX(minX);
                float rightX = MapX(maxX);
                canvas.DrawLine(leftX,  inner.Top, leftX,  inner.Bottom, dash);
                canvas.DrawLine(rightX, inner.Top, rightX, inner.Bottom, dash);
            }
        }
        finally
        {
            canvas.Restore();
        }
    }
}
