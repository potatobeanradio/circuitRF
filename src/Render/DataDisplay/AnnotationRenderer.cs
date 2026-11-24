using System;
using System.Collections.Generic;
using System.Globalization;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay;

public sealed record AnnotationPage(IReadOnlyList<CanvasAnnotation> Items, double Zoom = 1,
                                    double OffsetX = 0, double OffsetY = 0);

public static class AnnotationRenderer
{
    public static CanvasAnnotation Translate(CanvasAnnotation annotation, double dx, double dy) => annotation switch
    {
        CanvasText text => text with { X = text.X + dx, Y = text.Y + dy },
        CanvasLine line => line with { X = line.X + dx, Y = line.Y + dy, EndX = line.EndX + dx, EndY = line.EndY + dy },
        CanvasBox box => box with { X = box.X + dx, Y = box.Y + dy, EndX = box.EndX + dx, EndY = box.EndY + dy },
        CanvasOval oval => oval with { X = oval.X + dx, Y = oval.Y + dy, EndX = oval.EndX + dx, EndY = oval.EndY + dy },
        _ => annotation,
    };
    public sealed record TextLayout(IReadOnlyList<string> Lines, float Width, float Height, float Baseline, float LineHeight);

    public static TextLayout Layout(CanvasText text)
    {
        using var font = new SKFont(SkiaFonts.PlexRegular, (float)Math.Clamp(text.FontSize, 1, 200));
        using var fallback = new SKFont(SkiaFonts.DejaVuRegular, font.Size);
        float Measure(string value) => RendererText.MeasureTextWithFallback(value, font, fallback);
        var lines = new List<string>();
        var width = text.WrapWidth is > 0 and < double.PositiveInfinity ? (float)text.WrapWidth.Value : float.PositiveInfinity;
        foreach (var paragraph in text.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string pending = paragraph;
            while (pending.Length > 0 && Measure(pending) > width)
            {
                int fit = 0;
                int wordBreak = 0;
                var elements = StringInfo.GetTextElementEnumerator(pending);
                while (elements.MoveNext())
                {
                    int next = elements.ElementIndex + elements.GetTextElement().Length;
                    if (Measure(pending[..next]) > width && fit > 0) break;
                    fit = next;
                    if (char.IsWhiteSpace(pending[elements.ElementIndex])) wordBreak = elements.ElementIndex;
                }
                if (wordBreak > 0) fit = wordBreak;
                lines.Add(pending[..fit].TrimEnd());
                pending = pending[fit..].TrimStart();
            }
            if (pending.Length > 0 || paragraph.Length == 0) lines.Add(pending);
        }
        float measuredWidth = 0;
        foreach (var line in lines) measuredWidth = Math.Max(measuredWidth, Measure(line));
        var metrics = font.Metrics;
        float lineHeight = Math.Max(font.Size * 1.2f, metrics.Descent - metrics.Ascent + metrics.Leading);
        return new(lines, float.IsFinite(width) ? Math.Max(width, measuredWidth) : measuredWidth,
            Math.Max(1, lines.Count) * lineHeight, -metrics.Ascent, lineHeight);
    }

    public static SKRect Bounds(CanvasAnnotation annotation)
    {
        if (annotation is CanvasText text)
        {
            var layout = Layout(text);
            var matrix = SKMatrix.CreateRotationDegrees((int)text.Rotation);
            var bounds = matrix.MapRect(new SKRect(0, 0, layout.Width, layout.Height));
            bounds.Offset((float)text.X, (float)text.Y);
            return bounds;
        }
        var box = Coordinates(annotation);
        float padding = (float)Math.Clamp(annotation.LineWidth, 0.25, 40) / 2;
        if (annotation is CanvasLine { StartArrow: true } or CanvasLine { EndArrow: true })
            padding = Math.Max(8, padding * 8);
        box.Inflate(padding, padding);
        return box;
    }

    public static SKRect Coordinates(CanvasAnnotation annotation) => annotation switch
    {
        CanvasLine line => Rectangle(line.X, line.Y, line.EndX, line.EndY),
        CanvasBox box => Rectangle(box.X, box.Y, box.EndX, box.EndY),
        CanvasOval oval => Rectangle(oval.X, oval.Y, oval.EndX, oval.EndY),
        _ => SKRect.Empty,
    };

    private static SKRect Rectangle(double x, double y, double endX, double endY)
        => new((float)Math.Min(x, endX), (float)Math.Min(y, endY), (float)Math.Max(x, endX), (float)Math.Max(y, endY));

    public static void Draw(SKCanvas canvas, CanvasAnnotation annotation, SKColor defaultColor)
    {
        var color = SKColor.TryParse(annotation.Color, out var chosen) ? chosen : defaultColor;
        using var paint = new SKPaint { IsAntialias = true, Color = color };
        if (annotation is CanvasText text)
        {
            var layout = Layout(text);
            using var font = new SKFont(SkiaFonts.PlexRegular, (float)Math.Clamp(text.FontSize, 1, 200));
            using var fallback = new SKFont(SkiaFonts.DejaVuRegular, font.Size);
            canvas.Save();
            canvas.Translate((float)text.X, (float)text.Y);
            canvas.RotateDegrees((int)text.Rotation);
            for (int index = 0; index < layout.Lines.Count; index++)
                RendererText.DrawLeftTextWithFallback(canvas, layout.Lines[index], 0, layout.Baseline + index * layout.LineHeight,
                    font, fallback, paint);
            canvas.Restore();
            return;
        }

        using var path = new SKPath();
        double arrowSize = 0;
        CanvasLineStyle style;
        switch (annotation)
        {
            case CanvasLine line:
                double length = double.Hypot(line.EndX - line.X, line.EndY - line.Y);
                int headCount = (line.StartArrow ? 1 : 0) + (line.EndArrow ? 1 : 0);
                arrowSize = headCount == 0 ? 0 : Math.Min(length / headCount, Math.Max(8, line.LineWidth * 4));
                double dx = length == 0 ? 0 : (line.EndX - line.X) / length;
                double dy = length == 0 ? 0 : (line.EndY - line.Y) / length;
                double startTrim = line.StartArrow ? arrowSize : 0;
                double endTrim = line.EndArrow ? arrowSize : 0;
                path.MoveTo((float)(line.X + dx * startTrim), (float)(line.Y + dy * startTrim));
                path.LineTo((float)(line.EndX - dx * endTrim), (float)(line.EndY - dy * endTrim));
                style = line.Style;
                break;
            case CanvasBox box:
                path.AddRect(Coordinates(box));
                style = box.Style;
                break;
            case CanvasOval oval:
                path.AddOval(Coordinates(oval));
                style = oval.Style;
                break;
            default: return;
        }

        if (annotation is CanvasBox or CanvasOval && SKColor.TryParse(annotation.FillColor, out var fill))
        {
            paint.Color = fill;
            canvas.DrawPath(path, paint);
        }
        paint.Color = color;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = (float)Math.Clamp(annotation.LineWidth, 0.25, 40);
        using var dash = style == CanvasLineStyle.Dashed
            ? SKPathEffect.CreateDash([paint.StrokeWidth * 3, paint.StrokeWidth * 2], 0) : null;
        paint.PathEffect = dash;
        using var outline = new SKPath();
        paint.GetFillPath(path, outline);
        paint.PathEffect = null;
        paint.Style = SKPaintStyle.Fill;
        if (annotation is CanvasLine arrow)
        {
            if (arrow.StartArrow) AddArrow(arrow.EndX, arrow.EndY, arrow.X, arrow.Y);
            if (arrow.EndArrow) AddArrow(arrow.X, arrow.Y, arrow.EndX, arrow.EndY);
        }
        canvas.DrawPath(outline, paint);

        void AddArrow(double fromX, double fromY, double tipX, double tipY)
        {
            double length = double.Hypot(tipX - fromX, tipY - fromY);
            if (length == 0) return;
            double size = arrowSize;
            double dx = (tipX - fromX) / length;
            double dy = (tipY - fromY) / length;
            using var head = new SKPath();
            head.MoveTo((float)tipX, (float)tipY);
            head.LineTo((float)(tipX - dx * size - dy * size * 0.45), (float)(tipY - dy * size + dx * size * 0.45));
            head.LineTo((float)(tipX - dx * size + dy * size * 0.45), (float)(tipY - dy * size - dx * size * 0.45));
            head.Close();
            using var combined = new SKPath();
            if (outline.Op(head, SKPathOp.Union, combined))
            {
                outline.Reset();
                outline.AddPath(combined);
            }
            else outline.AddPath(head);
        }
    }
}