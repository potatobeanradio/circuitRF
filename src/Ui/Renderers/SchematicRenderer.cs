using System.Diagnostics;
using CircuitRF.Ui.Schematic;
using SkiaSharp;

namespace CircuitRF.Ui.Renderers;

/// <summary>
/// Separable Skia renderer for the schematic canvas (§3.3 ui-architecture.md).
/// Accepts a Skia canvas + viewport parameters + model; draws everything.
/// No Avalonia types — keeps renderer re-skinnable.
///
/// Performance strategy:
///   - Viewport virtualization: spatial index prunes invisible items.
///   - LOD: component pixel width &lt; LodThreshold → tiny filled rect only.
///   - Simplified: &lt; SimplifiedThreshold → body outline, no text.
///   - Full: body + port markers + labels.
///   - Grid: adaptive density (coarse-only when fine grid &lt; MinGridSpacingPx).
/// </summary>
public static class SchematicRenderer
{
    // LOD thresholds in pixels (based on standard 300-unit-wide component @ current zoom)
    private const double LodThreshold        = 6.0;   // below → tiny rect
    private const double SimplifiedThreshold = 22.0;  // below → body only (no text)
    private const double MinGridSpacingPx    = 4.0;   // below → skip grid entirely
    private const double CoarseGridRatio     = 10.0;  // coarse = fine × this

    // Dot size for connection junctions
    private const float DotHalfSize  = 5f;
    // Unconnected port box half-size
    private const float PortBoxHalf  = 10f;

    public static void Draw(
        SKCanvas canvas,
        (double W, double H) canvasSize,
        SchematicModel? model,
        SchematicSpatialIndex? index,
        double panX, double panY, double zoom,
        SchematicRenderTheme theme,
        long previousFrameTicks = 0,
        bool showFps = true)
    {
        var sw = Stopwatch.StartNew();

        // ── Background ────────────────────────────────────────────────────────
        canvas.Clear(theme.Background);

        if (model is null)
        {
            DrawFpsOverlay(canvas, canvasSize, previousFrameTicks, theme, showFps);
            Volatile.Write(ref LastFrameTicks, sw.ElapsedTicks);
            return;
        }

        // ── Grid ──────────────────────────────────────────────────────────────
        DrawGrid(canvas, canvasSize, model.GridSize, panX, panY, zoom, theme);

        // ── Spatial index query ───────────────────────────────────────────────
        double vpMinX = panX;
        double vpMinY = panY;
        double vpMaxX = panX + canvasSize.W / zoom;
        double vpMaxY = panY + canvasSize.H / zoom;

        var visComps  = new HashSet<int>();
        var visWires  = new HashSet<int>();

        if (index is not null)
            index.QueryViewport(vpMinX, vpMinY, vpMaxX, vpMaxY, visComps, visWires);
        else
        {
            for (int i = 0; i < model.Components.Count; i++) visComps.Add(i);
            for (int i = 0; i < model.Wires.Count;      i++) visWires.Add(i);
        }

        // LOD decision: component standard width = 300 world units (body + leads)
        double compPixW = zoom * 300.0;
        bool isLod        = compPixW < LodThreshold;
        bool isSimplified = compPixW < SimplifiedThreshold;

        using var wirePaint      = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke,
                                                  StrokeWidth = (float)Math.Max(1.0, zoom * 4), Color = theme.Wire };
        using var bodyPaint      = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke,
                                                  StrokeWidth = (float)Math.Max(1.0, zoom * 3), Color = theme.ComponentBody };
        using var lodPaint       = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill, Color = theme.LodRect };
        using var dotPaint       = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill, Color = theme.ConnectionDot };
        using var unconnPaint    = new SKPaint { IsAntialias = true,  Style = SKPaintStyle.Stroke,
                                                  StrokeWidth = (float)Math.Max(1.0, zoom * 2), Color = theme.UnconnectedPort };
        using var textFont       = new SKFont(SkiaFonts.PlexRegular, (float)Math.Clamp(zoom * 70, 7.0, 18.0));
        using var textPaint      = new SKPaint { IsAntialias = true, Color = theme.Label };
        using var subTextFont    = new SKFont(SkiaFonts.PlexLight,   (float)Math.Clamp(zoom * 60, 6.0, 14.0));

        // ── Wires ─────────────────────────────────────────────────────────────
        foreach (int wi in visWires)
        {
            var w = model.Wires[wi];
            if (!BbIntersects(w.BbMinX, w.BbMinY, w.BbMaxX, w.BbMaxY, vpMinX, vpMinY, vpMaxX, vpMaxY))
                continue;

            var pts = w.Points;
            for (int pi = 0; pi < pts.Count - 1; pi++)
            {
                var (ax, ay) = ToPixel(pts[pi].X,     pts[pi].Y,     panX, panY, zoom);
                var (bx, by) = ToPixel(pts[pi + 1].X, pts[pi + 1].Y, panX, panY, zoom);
                canvas.DrawLine(ax, ay, bx, by, wirePaint);
            }
        }

        // ── Components ────────────────────────────────────────────────────────
        foreach (int ci in visComps)
        {
            var c = model.Components[ci];
            if (!BbIntersects(c.BbMinX, c.BbMinY, c.BbMaxX, c.BbMaxY, vpMinX, vpMinY, vpMaxX, vpMaxY))
                continue;

            var (cpx, cpy) = ToPixel(c.X, c.Y, panX, panY, zoom);

            if (isLod)
            {
                // Tiny filled rect — fast LOD
                float hw = (float)(compPixW * 0.35f);
                float hh = (float)(zoom * 100 * 0.35f);
                canvas.DrawRect(SKRect.Create(cpx - hw, cpy - hh, hw * 2, hh * 2), lodPaint);
                continue;
            }

            // Draw symbol lines
            DrawSymbolLines(canvas, c, panX, panY, zoom, bodyPaint);

            if (!isSimplified)
            {
                // Port markers
                DrawPortMarkers(canvas, c, panX, panY, zoom, unconnPaint, dotPaint);

                // Labels
                DrawLabels(canvas, c, cpx, cpy, zoom, textFont, subTextFont, textPaint);
            }
        }

        // ── Connection dots ───────────────────────────────────────────────────
        if (!isLod)
        {
            float dotHalf = (float)Math.Clamp(zoom * DotHalfSize, 2.0, 6.0);
            foreach (var dot in model.ConnectionDots)
            {
                if (dot.X < vpMinX - 20 || dot.X > vpMaxX + 20 ||
                    dot.Y < vpMinY - 20 || dot.Y > vpMaxY + 20) continue;
                var (dx, dy) = ToPixel(dot.X, dot.Y, panX, panY, zoom);
                canvas.DrawRect(SKRect.Create(dx - dotHalf, dy - dotHalf, dotHalf * 2, dotHalf * 2), dotPaint);
            }
        }

        sw.Stop();
        Volatile.Write(ref LastFrameTicks, sw.ElapsedTicks);

        DrawFpsOverlay(canvas, canvasSize, sw.ElapsedTicks, theme, showFps);
    }

    // ── Symbol line drawing ───────────────────────────────────────────────────

    private static void DrawSymbolLines(
        SKCanvas canvas, SchematicComponent c,
        double panX, double panY, double zoom,
        SKPaint paint)
    {
        float[] segs = SchematicSymbols.For(c.Symbol);
        for (int i = 0; i < segs.Length; i += 4)
        {
            var (ax, ay) = LocalToPixel(segs[i],     segs[i + 1], c.X, c.Y, c.Rotation, c.MirrorX, panX, panY, zoom);
            var (bx, by) = LocalToPixel(segs[i + 2], segs[i + 3], c.X, c.Y, c.Rotation, c.MirrorX, panX, panY, zoom);
            canvas.DrawLine(ax, ay, bx, by, paint);
        }
    }

    // ── Port markers ─────────────────────────────────────────────────────────

    private static void DrawPortMarkers(
        SKCanvas canvas, SchematicComponent c,
        double panX, double panY, double zoom,
        SKPaint unconnPaint, SKPaint dotPaint)
    {
        float boxHalf = (float)Math.Clamp(zoom * PortBoxHalf, 3.0, 8.0);

        foreach (var port in c.Ports)
        {
            var (px, py) = LocalToPixel(port.LocalX, port.LocalY, c.X, c.Y, c.Rotation, c.MirrorX, panX, panY, zoom);

            if (port.State == PortConnectionState.Unconnected)
            {
                // §4.3 — red box for unconnected port
                canvas.DrawRect(SKRect.Create(px - boxHalf, py - boxHalf, boxHalf * 2, boxHalf * 2), unconnPaint);
            }
            // Connected ports: the connection dot is on the wire, not the port itself
        }
    }

    // ── Labels ────────────────────────────────────────────────────────────────

    private static void DrawLabels(
        SKCanvas canvas, SchematicComponent c,
        float cpx, float cpy, double zoom,
        SKFont fontA, SKFont fontB, SKPaint paint)
    {
        float textSize = fontA.Size;
        if (textSize < 7f) return;

        float offsetY = (float)(zoom * 120 + textSize);  // below component body

        if (c.LabelA is { } la && la.Length > 0)
        {
            canvas.DrawText(la, cpx, cpy + offsetY, SKTextAlign.Center, fontA, paint);
        }

        if (c.LabelB is { } lb && lb.Length > 0)
        {
            canvas.DrawText(lb, cpx, cpy + offsetY + textSize + 2f, SKTextAlign.Center, fontB, paint);
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

        // Choose which grid level(s) to draw
        double spacingWorld;
        byte gridAlpha;
        if (finePx < MinGridSpacingPx * 3)
        {
            spacingWorld = gridWorld * CoarseGridRatio;
            gridAlpha = 45;
        }
        else
        {
            spacingWorld = gridWorld;
            gridAlpha = 35;
        }

        double spacingPx = zoom * spacingWorld;
        if (spacingPx < MinGridSpacingPx) return;

        using var paint = new SKPaint
        {
            IsAntialias = false,
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            Color       = theme.Grid.WithAlpha(gridAlpha),
        };

        // Vertical lines
        double firstWorldX = Math.Ceiling(panX / spacingWorld) * spacingWorld;
        double startPx     = (firstWorldX - panX) * zoom;
        int    vCount      = (int)Math.Ceiling((size.W - startPx) / spacingPx) + 2;
        for (int i = 0; i < vCount && i < 600; i++)
        {
            float px = (float)(startPx + i * spacingPx);
            if (px < -1 || px > size.W + 1) continue;
            canvas.DrawLine(px, 0f, px, (float)size.H, paint);
        }

        // Horizontal lines
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

        double ms = frameTicks * 1000.0 / Stopwatch.Frequency;
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

    /// <summary>World → Skia pixel.</summary>
    private static (float X, float Y) ToPixel(double wx, double wy, double panX, double panY, double zoom)
        => ((float)((wx - panX) * zoom), (float)((wy - panY) * zoom));

    /// <summary>Component local → Skia pixel (applies rotation, then world→pixel).</summary>
    private static (float X, float Y) LocalToPixel(
        float lx, float ly,
        double compX, double compY,
        SymbolRotation rot, bool mirrorX,
        double panX, double panY, double zoom)
    {
        // Apply horizontal mirror first
        float mlx = mirrorX ? -lx : lx;

        // Apply rotation (2D, Y-down schematic coords)
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
        => maxX >= vpMinX && minX <= vpMaxX &&
           maxY >= vpMinY && minY <= vpMaxY;

    /// <summary>Last frame render time (ticks). Written by this renderer; read by the view timer.</summary>
    public static long LastFrameTicks;
}
