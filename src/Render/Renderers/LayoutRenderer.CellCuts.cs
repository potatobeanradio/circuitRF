// brief-icp-7 — the open .cem's cell cuts on the layout (R-icp7-3) and the solver's geometry preview (R-icp7-6). Both are pushed by
// the .cem from ICP-2's cached build and drawn in path space, above the committed geometry and below the port glyphs; only the
// interactive canvas sets them.

using SkiaSharp;

namespace CircuitRF.Render;

public static partial class LayoutRenderer
{
    /// <summary>How far apart a cut's hatch lines are, in device pixels.</summary>
    internal const float CutHatchSpacingDevicePixels = 7f;

    /// <summary>A pin glyph's size, in device pixels.</summary>
    internal const float CutPinGlyphDevicePixels = 9f;

    /// <summary>R-icp7-6 — the layout dimmed under a wash of the background, then each shape the extractor is handed, filled in its
    /// layer's colour and outlined, so the metal a run solves reads at full strength and what it does not is faint behind it.</summary>
    private static void DrawSolverGeometry(SKCanvas canvas, IReadOnlyList<LayoutShape> shapes, Dictionary<LayerKey, LayerDef>? layerMap,
                                           LayoutRenderTheme theme, PathSpace ps, LayoutViewport vp, double scaleUm, LayoutFrameCounters counters)
    {
        var visible = NormalizedRect(ps.X((long)Math.Floor(vp.VisibleMinX)), ps.Y((long)Math.Floor(vp.VisibleMinY)),
                                     ps.X((long)Math.Ceiling(vp.VisibleMaxX)), ps.Y((long)Math.Ceiling(vp.VisibleMaxY)));
        using (var wash = new SKPaint { Style = SKPaintStyle.Fill, Color = theme.Background.WithAlpha(165) })
            canvas.DrawRect(visible, wash);
        counters.DrawCalls++;
        float stroke = DevicePixelsToPathSpace(scaleUm, 1f);
        foreach (var shape in shapes)
        {
            if (shape is LabelShape or BitmapShape) continue;
            using var path = BuildShapePath(shape, ps);
            if (path is null) continue;
            var c = layerMap is not null && layerMap.TryGetValue(shape.Layer, out var def)
                ? new SKColor(def.Color.R, def.Color.G, def.Color.B) : theme.RulerAnnotationLine;
            byte alpha = layerMap is not null && layerMap.TryGetValue(shape.Layer, out var d2) ? (byte)Math.Clamp(d2.FillOpacity * 255 * 1.6, 60, 230) : (byte)140;
            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = c.WithAlpha(alpha) };
            using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, Color = c };
            canvas.DrawPath(path, fill);
            canvas.DrawPath(path, line);
            counters.DrawCalls += 2;
        }
    }

    /// <summary>R-icp7-3 — each cut instance's left-out region hatched in the warning colour with a dashed edge (its box dashed
    /// longer, so a box reads as a box), and, with the ports shown, each pin's verdict glyph at its face.</summary>
    private static void DrawCutMarks(SKCanvas canvas, IReadOnlyList<LayoutCutMark> marks, LayoutRenderTheme theme, PathSpace ps,
                                     double scaleUm, bool pins, LayoutFrameCounters counters)
    {
        float stroke = DevicePixelsToPathSpace(scaleUm, 1.25f);
        float spacing = DevicePixelsToPathSpace(scaleUm, CutHatchSpacingDevicePixels);
        var color = theme.Warning;
        using var hatch = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, Color = color.WithAlpha(150),
        };
        foreach (var mark in marks)
        {
            using var region = new SKPath { FillType = SKPathFillType.EvenOdd };
            foreach (var poly in mark.Outline)
            {
                if (poly.Xy.Length < 6) continue;
                region.MoveTo(ps.X(poly.Xy[0]), ps.Y(poly.Xy[1]));
                for (int i = 2; i + 1 < poly.Xy.Length; i += 2) region.LineTo(ps.X(poly.Xy[i]), ps.Y(poly.Xy[i + 1]));
                region.Close();
            }
            if (region.IsEmpty)
            {
                // An instance left out whole leaves nothing: only its cross is drawn.
                if (pins) foreach (var pin in mark.Pins) DrawCutPin(canvas, pin, theme, ps, scaleUm);
                continue;
            }

            // Hatch: diagonal lines across the region's bounds, clipped to it. Drawn as lines, not a path effect, so a vector
            // export of a canvas capture keeps them.
            var b = region.Bounds;
            canvas.Save();
            canvas.ClipPath(region, SKClipOperation.Intersect, antialias: true);
            float span = b.Width + b.Height;
            for (float t = 0; t <= span; t += spacing)
                canvas.DrawLine(b.Left + t - b.Height, b.Bottom, b.Left + t, b.Top, hatch);
            canvas.Restore();

            float dash = DevicePixelsToPathSpace(scaleUm, mark.Box ? 9f : 4f);
            using var edge = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, Color = color,
                PathEffect = SKPathEffect.CreateDash([dash, dash * 0.6f], 0),
            };
            canvas.DrawPath(region, edge);
            counters.DrawCalls += 2;

            if (!pins) continue;
            foreach (var pin in mark.Pins) DrawCutPin(canvas, pin, theme, ps, scaleUm);
        }
    }

    /// <summary>One pin's glyph: a ground mark, a join line to the pin it touches, a warning triangle, a ring, or a cross. A routed
    /// pin draws nothing here: its port's own arrow stands at the face.</summary>
    private static void DrawCutPin(SKCanvas canvas, LayoutCutPinMark pin, LayoutRenderTheme theme, PathSpace ps, double scaleUm)
    {
        if (pin.Kind == LayoutCutPinKind.Routed) return;
        float s = DevicePixelsToPathSpace(scaleUm, CutPinGlyphDevicePixels);
        float x = ps.X(pin.X), y = ps.Y(pin.Y);
        var ink = pin.Kind is LayoutCutPinKind.Floating or LayoutCutPinKind.LeftOut ? theme.Warning
                : TintForContrast(theme.Selection, theme.Background, PortMarkerContrastTintAmount);
        using var line = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = DevicePixelsToPathSpace(scaleUm, 1.6f), Color = ink,
            StrokeCap = SKStrokeCap.Round,
        };
        switch (pin.Kind)
        {
            case LayoutCutPinKind.Grounded:
                // The ground symbol: a stem and three bars narrowing downward (screen down is path +y).
                canvas.DrawLine(x, y - s * 0.6f, x, y, line);
                for (int k = 0; k < 3; k++)
                {
                    float w = s * (0.9f - 0.3f * k), yy = y + k * s * 0.3f;
                    canvas.DrawLine(x - w / 2, yy, x + w / 2, yy, line);
                }
                break;
            case LayoutCutPinKind.Abutting:
                if (pin.ToX is { } tx && pin.ToY is { } ty)
                {
                    float x2 = ps.X(tx), y2 = ps.Y(ty);
                    canvas.DrawLine(x, y, x2, y2, line);
                    canvas.DrawCircle(x2, y2, s * 0.25f, line);
                }
                canvas.DrawCircle(x, y, s * 0.25f, line);
                break;
            case LayoutCutPinKind.Floating:
                using (var tri = new SKPath())
                {
                    tri.MoveTo(x, y - s * 0.6f);
                    tri.LineTo(x + s * 0.55f, y + s * 0.4f);
                    tri.LineTo(x - s * 0.55f, y + s * 0.4f);
                    tri.Close();
                    canvas.DrawPath(tri, line);
                    canvas.DrawLine(x, y - s * 0.25f, x, y + s * 0.08f, line);
                    canvas.DrawPoint(x, y + s * 0.25f, line);
                }
                break;
            case LayoutCutPinKind.Return:
                canvas.DrawCircle(x, y, s * 0.3f, line);
                break;
            case LayoutCutPinKind.LeftOut:
                canvas.DrawLine(x - s * 0.35f, y - s * 0.35f, x + s * 0.35f, y + s * 0.35f, line);
                canvas.DrawLine(x - s * 0.35f, y + s * 0.35f, x + s * 0.35f, y - s * 0.35f, line);
                break;
        }
    }
}
