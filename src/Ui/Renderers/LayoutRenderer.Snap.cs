// Geometry snap — marker glyph rendering (docs/sonnet-briefs/brief-snap-distance-and-geometry-snap.md
// §2.5/R-snp-4). Partial-class extension of LayoutRenderer, kept in its own file per this codebase's
// convention for a concern that deserves its own home (mirrors LayoutRenderer.Instances.cs). Only the
// SINGLE top-priority candidate is ever drawn (LayoutOverlay.SnapMarker carries just one) — R-snp-9's
// cycling through coincident features is a click-time concern, not a rendering one.

using System.Collections.Generic;
using CircuitRF.Ui.Layout;
using SkiaSharp;

namespace CircuitRF.Ui.Renderers;

public static partial class LayoutRenderer
{
    /// <summary>Fixed screen-space glyph half-size — reads at any zoom, mirroring
    /// <see cref="HandleSizeDevicePixels"/>'s own "constant on screen" convention. Two bumps so far:
    /// the feature's original 7.0/1.5 pair went to 7.7/1.65 (~10%, "make the glyph more obvious"), and
    /// brief-snap-combobox-and-consistency.md R-cmb-6 asked for a FURTHER 10% on top of that — 8.47/1.815.
    /// This is the ONLY place either constant is defined; every glyph shape (diamond/square/X/triangle/
    /// circle/bowtie) below takes <c>half</c> as a parameter derived from it, so there was nothing
    /// duplicated to consolidate when this was checked (per R-cmb-6's own "if duplicated, consolidate"
    /// instruction).</summary>
    private const double SnapMarkerSizeDevicePixels = 8.47;
    private const double SnapMarkerStrokeDevicePixels = 1.815;

    /// <summary>How far to tint the marker color toward black/white for contrast against the canvas
    /// background (owner follow-up) — "slightly", not a full recolor.</summary>
    private const double SnapMarkerContrastTintAmount = 0.3;

    /// <summary>Resolves <paramref name="candidate"/>'s own source layer color (R-snp-4: cross-layer
    /// snapping is legible only because the marker tells the user WHICH layer it's about to snap to)
    /// via the same <c>layerMap.TryGetValue → FallbackPalette.For</c> chain every other consumer of a
    /// resolved layer already uses (<see cref="DrawGhostShape"/>, <see cref="DrawLayer"/>'s own
    /// per-layer loop) — never a second resolver. The resolved color is then tinted for contrast
    /// against <paramref name="theme"/>'s own background (owner follow-up): darker on a light canvas,
    /// lighter on a dark one, measured from the background's own actual luminance rather than assuming
    /// which of the two built-in theme presets is active — a custom/overridden background still gets
    /// the right tint direction.</summary>
    private static void DrawSnapMarker(SKCanvas canvas, SnapCandidate candidate, Dictionary<LayerKey, LayerDef>? layerMap, PathSpace ps, double scaleUm, LayoutRenderTheme theme)
    {
        LayerDef def = layerMap is not null && layerMap.TryGetValue(candidate.Layer, out var found)
            ? found
            : FallbackPalette.For(candidate.Layer);
        var color = TintForContrast(new SKColor(def.Color.R, def.Color.G, def.Color.B), theme.Background);

        float cx = ps.X(candidate.X), cy = ps.Y(candidate.Y);
        float half = DevicePixelsToPathSpace(scaleUm, SnapMarkerSizeDevicePixels) / 2f;
        float strokeWidth = DevicePixelsToPathSpace(scaleUm, SnapMarkerStrokeDevicePixels);

        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = strokeWidth, Color = color };

        switch (candidate.Kind)
        {
            case SnapFeatureKind.Pin:
                DrawSnapDiamond(canvas, cx, cy, half, stroke);
                break;
            case SnapFeatureKind.CornerEndpoint:
                canvas.DrawRect(new SKRect(cx - half, cy - half, cx + half, cy + half), stroke);
                break;
            case SnapFeatureKind.Intersection:
                DrawSnapX(canvas, cx, cy, half, stroke);
                break;
            case SnapFeatureKind.Midpoint:
                DrawSnapTriangle(canvas, cx, cy, half, stroke);
                break;
            case SnapFeatureKind.Centroid:
                canvas.DrawCircle(cx, cy, half, stroke);
                break;
            case SnapFeatureKind.Nearest:
                DrawSnapBowtie(canvas, cx, cy, half, stroke);
                break;
        }
    }

    private static void DrawSnapDiamond(SKCanvas canvas, float cx, float cy, float half, SKPaint paint)
    {
        using var path = new SKPath();
        path.MoveTo(cx, cy - half);
        path.LineTo(cx + half, cy);
        path.LineTo(cx, cy + half);
        path.LineTo(cx - half, cy);
        path.Close();
        canvas.DrawPath(path, paint);
    }

    private static void DrawSnapTriangle(SKCanvas canvas, float cx, float cy, float half, SKPaint paint)
    {
        using var path = new SKPath();
        path.MoveTo(cx, cy - half);
        path.LineTo(cx + half, cy + half);
        path.LineTo(cx - half, cy + half);
        path.Close();
        canvas.DrawPath(path, paint);
    }

    private static void DrawSnapX(SKCanvas canvas, float cx, float cy, float half, SKPaint paint)
    {
        canvas.DrawLine(cx - half, cy - half, cx + half, cy + half, paint);
        canvas.DrawLine(cx - half, cy + half, cx + half, cy - half, paint);
    }

    /// <summary>Two small triangles pointing at each other — visually distinct from the diamond
    /// (Pin) and the plain triangle (Midpoint) at a glance, and lowest priority (R-snp-5), so it only
    /// ever shows when nothing more specific is in range.</summary>
    private static void DrawSnapBowtie(SKCanvas canvas, float cx, float cy, float half, SKPaint paint)
    {
        using var left = new SKPath();
        left.MoveTo(cx - half, cy - half);
        left.LineTo(cx, cy);
        left.LineTo(cx - half, cy + half);
        left.Close();
        canvas.DrawPath(left, paint);

        using var right = new SKPath();
        right.MoveTo(cx + half, cy - half);
        right.LineTo(cx, cy);
        right.LineTo(cx + half, cy + half);
        right.Close();
        canvas.DrawPath(right, paint);
    }

    /// <summary>Standard perceptual-luminance weighting (Rec. 601) decides whether <paramref
    /// name="background"/> reads as light or dark, then blends <paramref name="color"/> toward
    /// black (light background) or white (dark background) by <see cref="SnapMarkerContrastTintAmount"/>
    /// — alpha is left untouched.</summary>
    private static SKColor TintForContrast(SKColor color, SKColor background)
    {
        double luminance = 0.299 * background.Red + 0.587 * background.Green + 0.114 * background.Blue;
        return luminance > 127.5
            ? Blend(color, 0, 0, 0, SnapMarkerContrastTintAmount)      // light background -> tint darker
            : Blend(color, 255, 255, 255, SnapMarkerContrastTintAmount); // dark background -> tint lighter
    }

    private static SKColor Blend(SKColor color, double towardR, double towardG, double towardB, double amount) => new SKColor(
        (byte)Math.Clamp(color.Red   + (towardR - color.Red)   * amount, 0, 255),
        (byte)Math.Clamp(color.Green + (towardG - color.Green) * amount, 0, 255),
        (byte)Math.Clamp(color.Blue  + (towardB - color.Blue)  * amount, 0, 255),
        color.Alpha);
}
