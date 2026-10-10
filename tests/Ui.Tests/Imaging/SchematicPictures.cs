// Schematic pictures for the IM-7 gates, drawn in memory with SkiaSharp — anti-aliased, 2 px strokes, text in DejaVu
// Sans (a font circuitRF ships). brief-img-7-schematic-wires-and-regions.md §4. Never a committed file.

using System;
using CircuitRF.Design.Imaging;
using CircuitRF.Render;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Imaging;

internal static class SchematicPictures
{
    public const float Pen = 2;

    public static SKPaint Ink() => Pictures.Stroke(SKColors.Black, Pen);

    public static SKFont Font(float size = 16) => new(SkiaFonts.DejaVuRegular, size);

    /// <summary>Draws <paramref name="text"/> with its ink's left edge at <paramref name="left"/>.</summary>
    public static void Text(SKCanvas c, string text, float left, float baseline, float size = 16)
    {
        using var font = Font(size);
        using var paint = Pictures.Fill(SKColors.Black);
        font.MeasureText(text, out var bounds);
        c.DrawText(text, left - bounds.Left, baseline, SKTextAlign.Left, font, paint);
    }

    /// <summary>A ground: three bars, the widest at <paramref name="y"/>, centred on <paramref name="x"/>.</summary>
    public static void Ground(SKCanvas c, float x, float y)
    {
        using var pen = Ink();
        c.DrawLine(x - 14, y, x + 14, y, pen);
        c.DrawLine(x - 9, y + 6, x + 9, y + 6, pen);
        c.DrawLine(x - 4, y + 12, x + 4, y + 12, pen);
    }

    /// <summary>An L-network: an input wire, a series inductor of four humps, a T, a shunt capacitor to ground and an
    /// output wire — five wire segments, three symbols.</summary>
    public static void LNetwork(SKCanvas c)
    {
        using var pen = Ink();
        c.DrawLine(20, 80, 90, 80, pen);
        using (var humps = new SKPath())
        {
            humps.MoveTo(90, 80);
            for (int k = 0; k < 4; k++) humps.ArcTo(new SKRect(90 + 12 * k, 74, 102 + 12 * k, 86), 180, 180, false);
            c.DrawPath(humps, pen);
        }
        c.DrawLine(138, 80, 300, 80, pen);
        c.DrawLine(220, 80, 220, 140, pen);
        c.DrawLine(208, 140, 232, 140, pen);
        c.DrawLine(208, 148, 232, 148, pen);
        c.DrawLine(220, 148, 220, 200, pen);
        Ground(c, 220, 200);
    }

    public static RasterImage LNetwork(float skewDeg = 0) => Pictures.Draw(360, 260, c =>
    {
        if (skewDeg != 0) c.RotateDegrees(skewDeg, 180, 130);
        LNetwork(c);
    });

    /// <summary>A series capacitor between two long leads, its plates vertical.</summary>
    public static void SeriesCapacitor(SKCanvas c)
    {
        using var pen = Ink();
        c.DrawLine(20, 120, 150, 120, pen);
        c.DrawLine(150, 106, 150, 134, pen);
        c.DrawLine(158, 106, 158, 134, pen);
        c.DrawLine(158, 120, 300, 120, pen);
    }
}
