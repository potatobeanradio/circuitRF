using System.Diagnostics;
using CircuitRF.Ui.Schematic;
using SkiaSharp;

namespace CircuitRF.Ui.Renderers;


/// <summary>
/// Separable Skia renderer for the schematic canvas.
/// Accepts a Skia canvas + viewport parameters + model; draws everything.
/// No Avalonia types — keeps renderer re-skinnable.
/// </summary>
public static class SchematicRenderer
{
    private const double LodThreshold        = 6.0;
    private const double SimplifiedThreshold = 22.0;
    private const double MinGridSpacingPx    = 4.0;
    private const double CoarseGridRatio     = 10.0;

    private const float DotHalfSize = 5f;
    private const float PortBoxHalf = 10f;
    private const float ConnDotHalf = 4f;

    public static void Draw(
        SKCanvas canvas,
        (double W, double H) canvasSize,
        SchematicModel? model,
        SchematicSpatialIndex? index,
        double panX, double panY, double zoom,
        SchematicRenderTheme theme,
        long previousFrameTicks = 0,
        bool showFps = true,
        SchematicOverlay? overlay = null,
        bool useTransparentBackground = false,
        bool excludeGrid = false)
    {
        var sw = Stopwatch.StartNew();

        canvas.Clear(useTransparentBackground ? SKColors.Transparent : theme.Background);

        if (model is null)
        {
            DrawFpsOverlay(canvas, canvasSize, previousFrameTicks, theme, showFps);
            Volatile.Write(ref LastFrameTicks, sw.ElapsedTicks);
            return;
        }

        if (!excludeGrid)
            DrawGrid(canvas, canvasSize, model.GridSize, panX, panY, zoom, theme);

        double vpMinX = panX;
        double vpMinY = panY;
        double vpMaxX = panX + canvasSize.W / zoom;
        double vpMaxY = panY + canvasSize.H / zoom;

        var visComps = new HashSet<int>();
        var visWires = new HashSet<int>();

        if (index is not null)
            index.QueryViewport(vpMinX, vpMinY, vpMaxX, vpMaxY, visComps, visWires);
        else
        {
            for (int i = 0; i < model.Components.Count; i++) visComps.Add(i);
            for (int i = 0; i < model.Wires.Count;      i++) visWires.Add(i);
        }

        double compPixW    = zoom * 300.0;
        bool isLod         = compPixW < LodThreshold;
        bool isSimplified  = compPixW < SimplifiedThreshold;

        using var wirePaint      = new SKPaint { IsAntialias = true,  Style = SKPaintStyle.Stroke,
                                                  StrokeWidth = (float)Math.Max(1.0, zoom * 4),    Color = theme.Wire,
                                                  StrokeJoin  = SKStrokeJoin.Miter };
        using var bodyPaint      = new SKPaint { IsAntialias = true,  Style = SKPaintStyle.Stroke,
                                                  StrokeWidth = (float)Math.Max(1.0, zoom * 3),    Color = theme.ComponentBody };
        using var lodPaint       = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill,   Color = theme.LodRect };
        using var dotPaint       = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill,   Color = theme.ConnectionDot };
        using var unconnPaint    = new SKPaint { IsAntialias = true,  Style = SKPaintStyle.Stroke,
                                                  StrokeWidth = (float)Math.Max(1.0, zoom * 2),    Color = theme.Warning };
        using var connPinPaint   = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill,   Color = theme.ConnectedPin };
        using var warnFillPaint  = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill,   Color = theme.Warning.WithAlpha(60) };
        using var warnStrokePaint= new SKPaint { IsAntialias = true,  Style = SKPaintStyle.Stroke,
                                                  StrokeWidth = (float)Math.Max(1.5, zoom * 2.5),  Color = theme.Warning };
        using var textFont       = new SKFont(SkiaFonts.PlexRegular, (float)Math.Max(zoom * 70, 4.0));
        using var textPaint      = new SKPaint { IsAntialias = true,  Color = theme.Label };
        using var netLabelFont   = new SKFont(SkiaFonts.PlexItalic,  (float)Math.Max(zoom * 65, 4.0));
        using var netLabelPaint  = new SKPaint { IsAntialias = true,  Color = theme.NetLabelText };

        // ── Wires ─────────────────────────────────────────────────────────────
        float unconnEndHalf = (float)Math.Clamp(zoom * PortBoxHalf, 3.0, 8.0);
        var wireDragPts = overlay?.WireDragPoints;

        // Reused path object — Rewind() resets it without reallocation.
        using var wirePath = new SKPath();

        foreach (int wi in visWires)
        {
            var w = model.Wires[wi];

            // During drag, use overridden point list if available; skip BB cull for moved wires.
            IReadOnlyList<(double X, double Y)> pts;
            if (wireDragPts is not null && wireDragPts.TryGetValue(w.Id, out var overridePts))
                pts = overridePts;
            else
            {
                if (!BbIntersects(w.BbMinX, w.BbMinY, w.BbMaxX, w.BbMaxY, vpMinX, vpMinY, vpMaxX, vpMaxY))
                    continue;
                pts = w.Points;
            }

            // Draw as a single connected path so miter joins close the corners cleanly.
            if (pts.Count >= 2)
            {
                wirePath.Rewind();
                var (ax0, ay0) = ToPixel(pts[0].X, pts[0].Y, panX, panY, zoom);
                wirePath.MoveTo(ax0, ay0);
                for (int pi = 1; pi < pts.Count; pi++)
                {
                    var (ax, ay) = ToPixel(pts[pi].X, pts[pi].Y, panX, panY, zoom);
                    wirePath.LineTo(ax, ay);
                }
                canvas.DrawPath(wirePath, wirePaint);
            }

            // Unconnected endpoint squares — use model connectivity (deferred to drag-end).
            // Guard on pts.Count >= 2 symmetrically for both endpoints.
            if (!isSimplified && !isLod && pts.Count >= 2)
            {
                if (!w.StartConnected)
                {
                    var (ex, ey) = ToPixel(pts[0].X, pts[0].Y, panX, panY, zoom);
                    canvas.DrawRect(SKRect.Create(ex - unconnEndHalf, ey - unconnEndHalf,
                        unconnEndHalf * 2, unconnEndHalf * 2), unconnPaint);
                }
                if (!w.EndConnected)
                {
                    var (ex, ey) = ToPixel(pts[^1].X, pts[^1].Y, panX, panY, zoom);
                    canvas.DrawRect(SKRect.Create(ex - unconnEndHalf, ey - unconnEndHalf,
                        unconnEndHalf * 2, unconnEndHalf * 2), unconnPaint);
                }
            }
        }

        // ── Components ────────────────────────────────────────────────────────
        var compDragPos = overlay?.ComponentDragPositions;

        foreach (int ci in visComps)
        {
            var c = model.Components[ci];

            // During drag: use override position for moved components.
            double cx = c.X, cy = c.Y;
            if (compDragPos is not null && compDragPos.TryGetValue(c.Id, out var dragPos))
            {
                (cx, cy) = dragPos;
                // Approximate BB at new position; skip culling for moved items in the viewport.
                const double hb = 400.0;
                if (!BbIntersects(cx - hb, cy - hb, cx + hb, cy + hb, vpMinX, vpMinY, vpMaxX, vpMaxY))
                    continue;
            }
            else if (!BbIntersects(c.BbMinX, c.BbMinY, c.BbMaxX, c.BbMaxY, vpMinX, vpMinY, vpMaxX, vpMaxY))
                continue;

            var (cpx, cpy) = ToPixel(cx, cy, panX, panY, zoom);

            if (isLod)
            {
                float hw = (float)(compPixW * 0.35f);
                float hh = (float)(zoom * 100 * 0.35f);
                canvas.DrawRect(SKRect.Create(cpx - hw, cpy - hh, hw * 2, hh * 2), lodPaint);
                continue;
            }

            DrawSymbolLines(canvas, c, cx, cy, panX, panY, zoom, bodyPaint);
            DrawVariadicPortLeads(canvas, c, cx, cy, panX, panY, zoom, bodyPaint);

            // DisableState overlay (drawn on top of body)
            if (c.DisableState != DisableState.None && !isLod)
                DrawDisableOverlay(canvas, c, cx, cy, panX, panY, zoom, warnStrokePaint, warnFillPaint);

            if (!isSimplified)
            {
                DrawPortMarkers(canvas, c, cx, cy, panX, panY, zoom, unconnPaint, connPinPaint);
                (double DX, double DY)? lblDrag = null;
                if (overlay?.LabelDragOffsets is { } ldo && ldo.TryGetValue(c.Id, out var ld))
                    lblDrag = ld;
                DrawLabels(canvas, c, cpx, cpy, zoom, textFont, textPaint, lblDrag);
            }
        }

        // ── Connection dots ───────────────────────────────────────────────────
        if (!isLod)
        {
            // During a drag the overlay carries live dots recomputed from the moving geometry;
            // use them so junction dots follow instead of lagging at their pre-drag positions.
            var dotsToDraw = overlay?.ConnectionDotsOverride ?? model.ConnectionDots;
            float dotHalf = (float)Math.Clamp(zoom * DotHalfSize, 2.0, 6.0);
            foreach (var dot in dotsToDraw)
            {
                if (dot.X < vpMinX - 20 || dot.X > vpMaxX + 20 ||
                    dot.Y < vpMinY - 20 || dot.Y > vpMaxY + 20) continue;
                var (dx, dy) = ToPixel(dot.X, dot.Y, panX, panY, zoom);
                canvas.DrawRect(SKRect.Create(dx - dotHalf, dy - dotHalf, dotHalf * 2, dotHalf * 2), dotPaint);
            }
        }

        // ── Net labels ────────────────────────────────────────────────────────
        if (!isLod && !isSimplified && netLabelFont.Size >= 4f)
        {
            foreach (var lbl in model.NetLabels)
            {
                if (lbl.X < vpMinX - 200 || lbl.X > vpMaxX + 200 ||
                    lbl.Y < vpMinY - 50  || lbl.Y > vpMaxY + 50) continue;
                var (lx, ly) = ToPixel(lbl.X, lbl.Y, panX, panY, zoom);
                canvas.DrawText(lbl.Name, lx, ly, SKTextAlign.Left, netLabelFont, netLabelPaint);
            }
        }

        // ── 6d overlay ────────────────────────────────────────────────────────
        if (overlay is not null)
            DrawOverlay(canvas, canvasSize, model, overlay, panX, panY, zoom, theme, isLod);

        sw.Stop();
        Volatile.Write(ref LastFrameTicks, sw.ElapsedTicks);
        DrawFpsOverlay(canvas, canvasSize, sw.ElapsedTicks, theme, showFps);
    }

    // ── Symbol lines ─────────────────────────────────────────────────────────

    private static void DrawSymbolLines(
        SKCanvas canvas, SchematicComponent c,
        double compX, double compY,          // explicit world position (may differ during drag)
        double panX, double panY, double zoom,
        SKPaint paint)
    {
        float[] segs = SchematicSymbols.For(c.Symbol);
        for (int i = 0; i < segs.Length; i += 4)
        {
            var (ax, ay) = LocalToPixel(segs[i],     segs[i + 1], compX, compY, c.Rotation, c.MirrorX, panX, panY, zoom);
            var (bx, by) = LocalToPixel(segs[i + 2], segs[i + 3], compX, compY, c.Rotation, c.MirrorX, panX, panY, zoom);
            canvas.DrawLine(ax, ay, bx, by, paint);
        }
    }

    // ── Variadic port lead stubs (ZPort, Sdd) ────────────────────────────────

    // Draws stub lines from each port tip to the component body edge.
    // ZPort body: left edge x=−70, right edge x=+70.
    // Sdd body:   left edge x=−80, right edge x=+80.
    // Ports at LocalX<0 are left ports; ports at LocalX>0 are right ports.
    private static void DrawVariadicPortLeads(
        SKCanvas canvas, SchematicComponent c,
        double compX, double compY,
        double panX, double panY, double zoom,
        SKPaint paint)
    {
        if (c.Symbol is not (SymbolKind.ZPort or SymbolKind.Sdd)) return;
        float bodyEdge = c.Symbol == SymbolKind.ZPort ? 70f : 80f;
        foreach (var port in c.Ports)
        {
            float innerX = port.LocalX < 0f ? -bodyEdge : bodyEdge;
            var (ax, ay) = LocalToPixel(port.LocalX, port.LocalY, compX, compY, c.Rotation, c.MirrorX, panX, panY, zoom);
            var (bx, by) = LocalToPixel(innerX,      port.LocalY, compX, compY, c.Rotation, c.MirrorX, panX, panY, zoom);
            canvas.DrawLine(ax, ay, bx, by, paint);
        }
    }

    // ── Port markers ─────────────────────────────────────────────────────────

    private static void DrawPortMarkers(
        SKCanvas canvas, SchematicComponent c,
        double compX, double compY,          // explicit world position (may differ during drag)
        double panX, double panY, double zoom,
        SKPaint unconnPaint, SKPaint connPaint)
    {
        float boxHalf  = (float)Math.Clamp(zoom * PortBoxHalf, 3.0, 8.0);
        float connHalf = (float)Math.Clamp(zoom * ConnDotHalf, 2.0, 5.0);

        foreach (var port in c.Ports)
        {
            var (px, py) = LocalToPixel(port.LocalX, port.LocalY, compX, compY, c.Rotation, c.MirrorX, panX, panY, zoom);

            if (port.State == PortConnectionState.Unconnected)
                canvas.DrawRect(SKRect.Create(px - boxHalf, py - boxHalf, boxHalf * 2, boxHalf * 2), unconnPaint);
            else
                canvas.DrawRect(SKRect.Create(px - connHalf, py - connHalf, connHalf * 2, connHalf * 2), connPaint);
        }
    }

    // ── Labels (left-aligned; order: type, name, params) ─────────────────────

    private static void DrawLabels(
        SKCanvas canvas, SchematicComponent c,
        float cpx, float cpy, double zoom,
        SKFont font, SKPaint paint,
        (double DX, double DY)? dragDelta = null)
    {
        float textSize = font.Size;
        if (textSize < 4f) return;

        // Cap screen-space offsets so labels stay visible when the component center is on-screen.
        // Without capping, at zoom > ~2 the world-space offsets push labels off the canvas.
        float labelX   = cpx - (float)Math.Min(zoom * 155, 160.0);
        float startY   = cpy + (float)Math.Min(zoom * 120, 150.0) + textSize;
        float lineStep = textSize + 2f;

        for (int i = 0; i < c.Labels.Count; i++)
        {
            string label = c.Labels[i];
            if (string.IsNullOrEmpty(label)) continue;
            var (oDx, oDy) = i < c.LabelOffsets.Count ? c.LabelOffsets[i] : (0.0, 0.0);
            if (dragDelta is { } dd) { oDx += dd.DX; oDy += dd.DY; }
            float lx = labelX + (float)(oDx * zoom);
            float ly = startY + i * lineStep + (float)(oDy * zoom);
            canvas.DrawText(label, lx, ly, SKTextAlign.Left, font, paint);
        }
    }

    // ── DisableState overlay ──────────────────────────────────────────────────

    private static void DrawDisableOverlay(
        SKCanvas canvas, SchematicComponent c,
        double compX, double compY,          // explicit world position (may differ during drag)
        double panX, double panY, double zoom,
        SKPaint stroke, SKPaint hatchFill)
    {
        // Offset the baked glyph BB by the drag delta
        double dx = compX - c.X, dy = compY - c.Y;
        float pad = (float)(zoom * 18);
        var (ax, ay) = ToPixel(c.GlyphBbMinX + dx, c.GlyphBbMinY + dy, panX, panY, zoom);
        var (bx, by) = ToPixel(c.GlyphBbMaxX + dx, c.GlyphBbMaxY + dy, panX, panY, zoom);
        var rect = new SKRect(ax - pad, ay - pad, bx + pad, by + pad);

        canvas.DrawRect(rect, stroke);

        if (c.DisableState == DisableState.Open)
        {
            // X from corner to corner
            canvas.DrawLine(rect.Left, rect.Top, rect.Right, rect.Bottom, stroke);
            canvas.DrawLine(rect.Right, rect.Top, rect.Left, rect.Bottom, stroke);
        }
        else if (c.DisableState == DisableState.Short)
        {
            // Diagonal hatch fill
            canvas.Save();
            canvas.ClipRect(rect);
            float step = Math.Max(6f, (float)(zoom * 25));
            float h    = rect.Height;
            for (float d = rect.Left - h; d < rect.Right + h; d += step)
                canvas.DrawLine(d, rect.Top, d + h, rect.Bottom, hatchFill);
            canvas.Restore();
        }
    }

    // ── Overlay (selection, wire preview, ghost, rubber-band) ────────────────

    private static void DrawOverlay(
        SKCanvas canvas,
        (double W, double H) canvasSize,
        SchematicModel model,
        SchematicOverlay overlay,
        double panX, double panY, double zoom,
        SchematicRenderTheme theme,
        bool isLod)
    {
        if (!isLod && (overlay.SelectedComponentIds.Count > 0 || overlay.SelectedWireIds.Count > 0))
        {
            using var selStroke = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(1.0, zoom * 3),
                Color       = theme.SelectionBox,
                PathEffect  = SKPathEffect.CreateDash([6f, 4f], 0f),
            };
            using var selFill = new SKPaint
            {
                IsAntialias = false, Style = SKPaintStyle.Fill,
                Color       = theme.SelectionFill,
            };

            foreach (var comp in model.Components)
            {
                if (!overlay.SelectedComponentIds.Contains(comp.Id)) continue;
                // Glyph BB from render model; shift by drag delta when a drag is active.
                double bbMinX = comp.GlyphBbMinX, bbMinY = comp.GlyphBbMinY;
                double bbMaxX = comp.GlyphBbMaxX, bbMaxY = comp.GlyphBbMaxY;
                if (overlay.ComponentDragPositions is not null &&
                    overlay.ComponentDragPositions.TryGetValue(comp.Id, out var dragPos))
                {
                    double ddx = dragPos.X - comp.X, ddy = dragPos.Y - comp.Y;
                    bbMinX += ddx; bbMinY += ddy;
                    bbMaxX += ddx; bbMaxY += ddy;
                }
                // 2px fixed pad gives a thin outline exactly around the hit boundary.
                const float pad = 2f;
                var (ax, ay) = ToPixel(bbMinX, bbMinY, panX, panY, zoom);
                var (bx, by) = ToPixel(bbMaxX, bbMaxY, panX, panY, zoom);
                var rect = SKRect.Create(ax - pad, ay - pad, bx - ax + pad * 2, by - ay + pad * 2);
                canvas.DrawRect(rect, selFill);
                canvas.DrawRect(rect, selStroke);
            }

            using var wSel = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(6.0, zoom * 14),
                Color       = theme.SelectionBox,
                StrokeJoin  = SKStrokeJoin.Miter,
            };
            foreach (var wire in model.Wires)
            {
                if (!overlay.SelectedWireIds.Contains(wire.Id)) continue;
                // Use drag-override points while a drag is active so the highlight tracks movement.
                var pts = (overlay.WireDragPoints is not null &&
                           overlay.WireDragPoints.TryGetValue(wire.Id, out var dp))
                    ? dp : wire.Points;
                if (pts.Count < 2) continue;
                using var selPath = new SKPath();
                var (sx0, sy0) = ToPixel(pts[0].X, pts[0].Y, panX, panY, zoom);
                selPath.MoveTo(sx0, sy0);
                for (int pi = 1; pi < pts.Count; pi++)
                {
                    var (sx, sy) = ToPixel(pts[pi].X, pts[pi].Y, panX, panY, zoom);
                    selPath.LineTo(sx, sy);
                }
                canvas.DrawPath(selPath, wSel);
            }
        }

        // Selected wire segments (per-segment clicks — highlight each selected segment)
        var selSegs = overlay.SelectedWireSegments;
        if (selSegs.Count > 0 && !isLod)
        {
            using var segSelPaint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(6.0, zoom * 14),
                Color       = theme.SelectionBox,
                StrokeJoin  = SKStrokeJoin.Miter,
            };
            foreach (var segSel in selSegs)
            {
                SchematicWire? segWire = null;
                foreach (var w in model.Wires)
                    if (w.Id == segSel.WireId) { segWire = w; break; }
                if (segWire is null) continue;

                var segPts = (overlay.WireDragPoints is not null &&
                              overlay.WireDragPoints.TryGetValue(segSel.WireId, out var sdp))
                    ? sdp : segWire.Points;
                if (segSel.SegmentIndex >= segPts.Count - 1) continue;

                var (sax, say) = ToPixel(segPts[segSel.SegmentIndex].X,     segPts[segSel.SegmentIndex].Y,     panX, panY, zoom);
                var (sbx, sby) = ToPixel(segPts[segSel.SegmentIndex + 1].X, segPts[segSel.SegmentIndex + 1].Y, panX, panY, zoom);
                canvas.DrawLine(sax, say, sbx, sby, segSelPaint);
            }
        }

        // Wire preview
        if (overlay.WirePreview is { Count: >= 2 } pts2)
        {
            using var previewPaint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(1.0, zoom * 4),
                Color       = theme.WirePreview,
                PathEffect  = SKPathEffect.CreateDash([8f, 4f], 0f),
            };
            for (int i = 0; i < pts2.Count - 1; i++)
            {
                var (ax, ay) = ToPixel(pts2[i].X,     pts2[i].Y,     panX, panY, zoom);
                var (bx, by) = ToPixel(pts2[i + 1].X, pts2[i + 1].Y, panX, panY, zoom);
                canvas.DrawLine(ax, ay, bx, by, previewPaint);
            }
        }

        // Placement ghost
        if (overlay.Ghost is { } ghost && !isLod)
        {
            using var ghostPaint = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(1.0, zoom * 3),
                Color       = theme.GhostBody,
                PathEffect  = SKPathEffect.CreateDash([6f, 3f], 0f),
            };
            float[] segs = SchematicSymbols.For(ghost.Symbol);
            for (int i = 0; i < segs.Length; i += 4)
            {
                var (ax, ay) = LocalToPixel(segs[i],     segs[i + 1],
                    ghost.X, ghost.Y, ghost.Rotation, ghost.MirrorX, panX, panY, zoom);
                var (bx, by) = LocalToPixel(segs[i + 2], segs[i + 3],
                    ghost.X, ghost.Y, ghost.Rotation, ghost.MirrorX, panX, panY, zoom);
                canvas.DrawLine(ax, ay, bx, by, ghostPaint);
            }
        }

        // Rubber-band — solid outline for window (L→R), dashed for crossing (R→L)
        if (overlay.RubberBand is { } rb)
        {
            using var rbFill   = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill,   Color = theme.RubberBandFill };
            using var rbStroke = new SKPaint { IsAntialias = true,  Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = theme.RubberBandStroke };
            if (overlay.RubberBandCrossing)
                rbStroke.PathEffect = SKPathEffect.CreateDash([6f, 4f], 0f);
            var (ax, ay) = ToPixel(rb.X,         rb.Y,         panX, panY, zoom);
            var (bx, by) = ToPixel(rb.X + rb.W,  rb.Y + rb.H,  panX, panY, zoom);
            var rect = SKRect.Create(ax, ay, bx - ax, by - ay);
            canvas.DrawRect(rect, rbFill);
            canvas.DrawRect(rect, rbStroke);
        }
    }

    // ── Grid ─────────────────────────────────────────────────────────────────

    private static void DrawGrid(
        SKCanvas canvas, (double W, double H) size,
        double gridWorld, double panX, double panY, double zoom,
        SchematicRenderTheme theme)
    {
        double finePx = zoom * gridWorld;
        if (finePx < MinGridSpacingPx) return;

        double spacingWorld;
        byte gridAlpha;
        if (finePx < MinGridSpacingPx * 3)
        {
            spacingWorld = gridWorld * CoarseGridRatio;
            gridAlpha    = 45;
        }
        else
        {
            spacingWorld = gridWorld;
            gridAlpha    = 35;
        }

        double spacingPx = zoom * spacingWorld;
        if (spacingPx < MinGridSpacingPx) return;

        using var paint = new SKPaint
        {
            IsAntialias = false, Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f, Color = theme.Grid.WithAlpha(gridAlpha),
        };

        double firstWorldX = Math.Ceiling(panX / spacingWorld) * spacingWorld;
        double startPx     = (firstWorldX - panX) * zoom;
        int    vCount      = (int)Math.Ceiling((size.W - startPx) / spacingPx) + 2;
        for (int i = 0; i < vCount && i < 600; i++)
        {
            float px = (float)(startPx + i * spacingPx);
            if (px < -1 || px > size.W + 1) continue;
            canvas.DrawLine(px, 0f, px, (float)size.H, paint);
        }

        double firstWorldY = Math.Ceiling(panY / spacingWorld) * spacingWorld;
        double startPy     = (firstWorldY - panY) * zoom;
        int    hCount      = (int)Math.Ceiling((size.H - startPy) / spacingPx) + 2;
        for (int j = 0; j < hCount && j < 600; j++)
        {
            float py = (float)(startPy + j * spacingPx);
            if (py < -1 || py > size.H + 1) continue;
            canvas.DrawLine(0f, py, (float)size.W, py, paint);
        }
    }

    // ── FPS overlay ───────────────────────────────────────────────────────────

    private static void DrawFpsOverlay(
        SKCanvas canvas, (double W, double H) size,
        long frameTicks, SchematicRenderTheme theme, bool show)
    {
        if (!show || frameTicks <= 0) return;

        double ms  = frameTicks * 1000.0 / Stopwatch.Frequency;
        double fps = ms > 0 ? 1000.0 / ms : 0;
        string text = $"{ms:F1} ms  ({fps:F0} fps)";

        using var bgPaint   = new SKPaint { Color = new SKColor(0, 0, 0, 120), IsAntialias = false };
        using var textPaint = new SKPaint { Color = new SKColor(220, 220, 60),  IsAntialias = true };
        using var font      = new SKFont(SkiaFonts.PlexRegular, 11f);

        float textW = font.MeasureText(text);
        float x     = (float)size.W - textW - 14f;
        float y     = 16f;

        canvas.DrawRect(SKRect.Create(x - 4f, y - 12f, textW + 10f, 16f), bgPaint);
        canvas.DrawText(text, x, y, SKTextAlign.Left, font, textPaint);
    }

    // ── Transform helpers ─────────────────────────────────────────────────────

    private static (float X, float Y) ToPixel(double wx, double wy, double panX, double panY, double zoom)
        => ((float)((wx - panX) * zoom), (float)((wy - panY) * zoom));

    private static (float X, float Y) LocalToPixel(
        float lx, float ly,
        double compX, double compY,
        SymbolRotation rot, bool mirrorX,
        double panX, double panY, double zoom)
    {
        float mlx = mirrorX ? -lx : lx;
        (double rx, double ry) = rot switch
        {
            SymbolRotation.R90  => (-(double)ly,  (double)mlx),
            SymbolRotation.R180 => (-(double)mlx, -(double)ly),
            SymbolRotation.R270 => ((double)ly,   -(double)mlx),
            _                   => ((double)mlx,  (double)ly),
        };
        return ((float)((compX + rx - panX) * zoom),
                (float)((compY + ry - panY) * zoom));
    }

    private static bool BbIntersects(
        double minX, double minY, double maxX, double maxY,
        double vpMinX, double vpMinY, double vpMaxX, double vpMaxY)
        => maxX >= vpMinX && minX <= vpMaxX && maxY >= vpMinY && minY <= vpMaxY;

    public static long LastFrameTicks;
}
