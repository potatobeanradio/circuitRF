// ================================================================
//  SvgSafeFill.cs  —  a translucent black fill that survives SVG export
//
//  Skia's SVG device omits `fill` when the colour is pure black (the SVG
//  default) and drops `fill-opacity` with it, so a 55 %-opaque black box
//  is written as a bare shape and every viewer paints it OPAQUE black.
//  One unit of grey in each channel is indistinguishable on screen and
//  is written with its paint and opacity. Measured: src/Ui/Diagnostics/
//  DocsPaintRemap.cs, which does the same for the docs' theme brushes.
// ================================================================

using SkiaSharp;

namespace CircuitRF.Render.DataDisplay;

internal static class SvgSafeFill
{
    /// <summary><paramref name="c"/>, or #010101 at its alpha when it is translucent pure black.</summary>
    public static SKColor Of(SKColor c)
        => c.Red == 0 && c.Green == 0 && c.Blue == 0 && c.Alpha is > 0 and < 255 ? new SKColor(1, 1, 1, c.Alpha) : c;
}
