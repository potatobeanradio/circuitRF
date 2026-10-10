// Symbols for the IM-8 gates, each drawn by the test in the convention it is named for — never by src/Render, so the
// gate does not compare the renderer with itself. brief-img-8-schematic-symbols.md §4. Anti-aliased, 2 px strokes.
//
// Every drawing is made in its kind's built-in frame (a two-terminal part upright with pin 1 on top, a line across, a
// terminal's wire to the right, a ground's lead up), centred on the origin, with its wires out to ±100; the picture
// turns it about the centre, so 90° here is R90 there.

using System;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Schematic.Recognition;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Imaging;

internal static class SymbolPictures
{
    public const int Size = 240;

    /// <summary>A picture of <paramref name="draw"/> turned by <paramref name="deg"/> about the centre — and then, when
    /// asked, the whole picture mirrored left to right.</summary>
    public static RasterImage Picture(Action<SKCanvas, SKPaint> draw, float deg = 0, bool mirror = false) =>
        Pictures.Draw(Size, Size, c =>
        {
            c.Translate(Size / 2f, Size / 2f);
            if (mirror) c.Scale(-1, 1);
            c.RotateDegrees(deg);
            using var pen = SchematicPictures.Ink();
            draw(c, pen);
        });

    /// <summary>The symbols the picture's reading names.</summary>
    public static SchematicSymbolsRead Read(RasterImage img) => SchematicSymbols.Read(SchematicImageReading.Read(img));

    public static void Upright(SKCanvas c, SKPaint pen, float end)
    {
        c.DrawLine(0, -100, 0, -end, pen);
        c.DrawLine(0, end, 0, 100, pen);
    }

    public static void Across(SKCanvas c, SKPaint pen, float end)
    {
        c.DrawLine(-100, 0, -end, 0, pen);
        c.DrawLine(end, 0, 100, 0, pen);
    }

    /// <summary>A US resistor: a zig-zag of eight strokes.</summary>
    public static void UsResistor(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 24);
        using var p = new SKPath();
        p.MoveTo(0, -24);
        p.LineTo(6, -21);
        for (int k = 0; k < 7; k++) p.LineTo(k % 2 == 0 ? -6 : 6, -15 + 6 * k);
        p.LineTo(0, 24);
        c.DrawPath(p, pen);
    }

    /// <summary>An IEC resistor: a hollow rectangle.</summary>
    public static void IecResistor(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 24);
        c.DrawRect(-10, -24, 20, 48, pen);
    }

    /// <summary>A coil of four humps bulging to +x.</summary>
    public static void Coil(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 24);
        using var p = new SKPath();
        p.MoveTo(0, -24);
        for (int k = 0; k < 4; k++) p.ArcTo(new SKRect(-6, -24 + 12 * k, 6, -12 + 12 * k), 270, 180, false);
        c.DrawPath(p, pen);
    }

    /// <summary>An IEC inductor: a filled rectangle.</summary>
    public static void IecInductor(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 21);
        using var fill = Pictures.Fill(SKColors.Black);
        c.DrawRect(-7, -21, 14, 42, fill);
    }

    /// <summary>A capacitor of two flat plates.</summary>
    public static void FlatCapacitor(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 4);
        c.DrawLine(-14, -4, 14, -4, pen);
        c.DrawLine(-14, 4, 14, 4, pen);
    }

    /// <summary>A capacitor of a flat plate and a curved one bowing toward it.</summary>
    public static void CurvedCapacitor(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 4);
        c.DrawLine(-14, -4, 14, -4, pen);
        using var p = new SKPath();
        p.AddArc(new SKRect(-26, 4, 26, 56), 240, 60);
        c.DrawPath(p, pen);
    }

    /// <summary>A ground of three bars under its lead.</summary>
    public static void Ground(SKCanvas c, SKPaint pen)
    {
        c.DrawLine(0, -100, 0, -6, pen);
        c.DrawLine(-14, -6, 14, -6, pen);
        c.DrawLine(-9, 0, 9, 0, pen);
        c.DrawLine(-4, 6, 4, 6, pen);
    }

    /// <summary>A terminal: an open circle on the end of a wire coming from the right.</summary>
    public static void Terminal(SKCanvas c, SKPaint pen)
    {
        c.DrawCircle(0, 0, 8, pen);
        c.DrawLine(8, 0, 100, 0, pen);
    }

    /// <summary>A transmission line: a box with a line along it.</summary>
    public static void BoxLine(SKCanvas c, SKPaint pen)
    {
        Across(c, pen, 30);
        c.DrawRect(-30, -9, 60, 18, pen);
        c.DrawLine(-22, 0, 22, 0, pen);
    }

    /// <summary>Two parallel bars across the leads joined at their ends — a drawn rectangle, not a capacitor.</summary>
    public static void JoinedBars(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 4);
        c.DrawRect(-14, -4, 28, 8, pen);
    }

    /// <summary>An n-channel FET with no arrow: a gate wire from the left onto an unbroken channel bar standing out past
    /// its drain and source arms, the arms out to wires going up and down.</summary>
    public static void Fet(SKCanvas c, SKPaint pen)
    {
        c.DrawLine(-100, 0, -5, 0, pen);
        c.DrawLine(-5, -15, -5, 15, pen);
        c.DrawLine(-5, -6, 6, -6, pen);
        c.DrawLine(6, -6, 6, -100, pen);
        c.DrawLine(-5, 6, 6, 6, pen);
        c.DrawLine(6, 6, 6, 100, pen);
    }

    /// <summary>An irregular closed outline between two leads, no side of it near an axis — no symbol anyone draws.</summary>
    public static void Blob(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 12);
        using var p = new SKPath();
        p.MoveTo(0, -12);
        foreach (var (x, y) in new[] { (9, -8), (12, 2), (6, 10), (0, 12), (-8, 9), (-12, -1), (-7, -9) }) p.LineTo(x, y);
        p.Close();
        c.DrawPath(p, pen);
    }

    /// <summary>A diode, anode on top: a filled triangle pointing down at its bar.</summary>
    public static void Diode(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 9);
        using var fill = Pictures.Fill(SKColors.Black);
        using var tri = new SKPath();
        tri.MoveTo(-11, -9);
        tri.LineTo(11, -9);
        tri.LineTo(0, 9);
        tri.Close();
        c.DrawPath(tri, fill);
        c.DrawLine(-11, 9, 11, 9, pen);
    }
}
