// Geometry snap — marker glyph rendering (docs/sonnet-briefs/brief-snap-distance-and-geometry-snap.md
// §2.5/R-snp-4). Partial-class extension of LayoutRenderer, kept in its own file per this codebase's
// convention for a concern that deserves its own home (mirrors LayoutRenderer.Instances.cs). Only the
// SINGLE top-priority candidate is ever drawn (LayoutOverlay.SnapMarker carries just one) — R-snp-9's
// cycling through coincident features is a click-time concern, not a rendering one.

using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace CircuitRF.Render;

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

    /// <summary>
    /// Draws the geometry-snap glyph ON TOP of a canvas that is otherwise finished — the second half
    /// of <see cref="LayoutRenderOptions.DeferSnapMarker"/>, which see for why this exists at all.
    ///
    /// <para>It rebuilds the same path-space transform <see cref="LayoutRenderer.Draw"/> used, from
    /// the same viewport and the same view, rather than being handed one: the glyph is a single
    /// stroked shape, so recomputing an origin and a matrix for it costs nothing measurable, and a
    /// transform passed across the seam is a second copy of the rule that decides where anything on
    /// this canvas lands. Nothing is drawn when there is no marker, no view, or no overlay — the
    /// ordinary case on every frame where the cursor is not near a snappable feature.</para>
    /// </summary>
    public static void DrawSnapMarkerOnTop(SKCanvas canvas, LayoutView? view, Technology? tech,
                                           LayoutViewport vp, LayoutRenderOptions opts)
    {
        if (canvas is null || view is null) return;
        if (opts.Overlay is not { } overlay || (overlay.SnapMarker is null && overlay.SnapTracking is null)) return;

        double centerX = vp.PanX + vp.Width  / (2.0 * vp.Zoom);
        double centerY = vp.PanY + vp.Height / (2.0 * vp.Zoom);
        var (originX, originY) = ComputeOrigin(centerX, centerY, vp.Width / vp.Zoom, vp.Height / vp.Zoom);

        double dbuToUm = 1.0 / Math.Max(1, view.DbuPerMicron);
        double scaleUm = vp.Zoom / dbuToUm;

        canvas.Save();
        try
        {
            canvas.ClipRect(SKRect.Create(0, 0, (float)vp.Width, (float)vp.Height));
            canvas.Concat(SKMatrix.CreateScaleTranslation(
                (float)scaleUm, (float)scaleUm,
                (float)((originX - vp.PanX) * vp.Zoom),
                (float)(vp.Height - (originY - vp.PanY) * vp.Zoom)));

            var ps = new PathSpace(originX, originY, dbuToUm);
            if (overlay.SnapTracking is { } tracking) DrawSnapTracking(canvas, tracking, ps, scaleUm, opts.Theme);
            if (overlay.SnapMarker is { } candidate)
                DrawSnapMarker(canvas, candidate, LayerCanvasContrast.DrawnLayerMap(tech, opts.Theme.Background), ps, scaleUm, opts.Theme);
        }
        finally
        {
            canvas.Restore();
        }
    }

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
        // A tracked point is drawn in tracking's own colour, as its line and its reference are: it is the line's answer,
        // not a feature of the layer it came from.
        var color = TintForContrast(candidate.Kind == SnapFeatureKind.Tracking ? TrackColour : new SKColor(def.Color.R, def.Color.G, def.Color.B),
                                    theme.Background);

        float cx = ps.X(candidate.X), cy = ps.Y(candidate.Y);
        float half = DevicePixelsToPathSpace(scaleUm, SnapMarkerSizeDevicePixels) / 2f;
        float strokeWidth = DevicePixelsToPathSpace(scaleUm, SnapMarkerStrokeDevicePixels);

        DrawSnapGlyph(canvas, candidate.Kind, cx, cy, half, strokeWidth, color);
    }

    /// <summary>Snap tracking's colour — its reference marks and its tracked glyph (the 3D view's, so the two editors agree).</summary>
    internal static readonly SKColor TrackColour = new(40, 200, 120);

    /// <summary>The colours of a line of constant y (along x) and of constant x (along y): the 3D view's x and y axes.</summary>
    private static readonly SKColor TrackAlongX = new(220, 60, 60), TrackAlongY = new(60, 170, 70);

    /// <summary>Snap tracking: each tracking line in force, dashed in its axis's colour from its reference to the tracked
    /// point — ending there, where the click will land — then a small plus at every reference, a fixed size on screen.</summary>
    private static void DrawSnapTracking(SKCanvas canvas, LayoutSnapTrackingOverlay tracking, PathSpace ps, double scaleUm, LayoutRenderTheme theme)
    {
        float px1 = DevicePixelsToPathSpace(scaleUm, 1);
        foreach (var (x0, y0, x1, y1, vertical) in tracking.Lines)
        {
            float ax = ps.X(x0), ay = ps.Y(y0), bx = ps.X(x1), by = ps.Y(y1);
            using var line = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f * px1,
                Color = TintForContrast(vertical ? TrackAlongY : TrackAlongX, theme.Background),
                PathEffect = SKPathEffect.CreateDash([5 * px1, 4 * px1], 0),
            };
            canvas.DrawLine(ax, ay, bx, by, line);
        }
        using var mark = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 * px1, StrokeCap = SKStrokeCap.Round,
            Color = TintForContrast(TrackColour, theme.Background),
        };
        float m = 5 * px1;
        foreach (var (x, y) in tracking.References)
        {
            float cx = ps.X(x), cy = ps.Y(y);
            canvas.DrawLine(cx - m, cy, cx + m, cy, mark);
            canvas.DrawLine(cx, cy - m, cx, cy + m, mark);
        }
    }

    /// <summary>
    /// Draw ONE snap glyph, at a stated centre, size, stroke and colour.
    ///
    /// <para>Split out of <see cref="DrawSnapMarker"/> so the user documentation can put the real
    /// glyph in its feature table instead of the word "diamond". <b>The point is that there is one
    /// implementation:</b> a doc that draws its own approximation of these six shapes goes wrong the
    /// first time one of them is redrawn, and goes wrong silently.</para>
    /// </summary>
    public static void DrawSnapGlyph(SKCanvas canvas, SnapFeatureKind kind,
                                     float cx, float cy, float half, float strokeWidth, SKColor color)
    {
        using var stroke = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = strokeWidth, Color = color,
        };

        switch (kind)
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
            // Snap tracking: a plus inside a circle — the reference mark (a plus) on the line it came from.
            case SnapFeatureKind.Tracking:
                canvas.DrawCircle(cx, cy, half, stroke);
                canvas.DrawLine(cx - half * 0.6f, cy, cx + half * 0.6f, cy, stroke);
                canvas.DrawLine(cx, cy - half * 0.6f, cx, cy + half * 0.6f, stroke);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind,
                    "A snap feature kind with no glyph would draw an empty cell in the docs table.");
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
    private static SKColor TintForContrast(SKColor color, SKColor background) =>
        TintForContrast(color, background, SnapMarkerContrastTintAmount);

    /// <summary>
    /// Push a layer colour AWAY from the canvas background so an annotation drawn in it reads as an
    /// annotation rather than as more of the same metal: darker on a light background, lighter on a
    /// dark one. Keyed off the background's own Rec. 601 luminance rather than off which built-in
    /// theme happens to be active, so a custom or overridden background tints the right way too.
    /// </summary>
    internal static SKColor TintForContrast(SKColor color, SKColor background, double amount)
    {
        double luminance = 0.299 * background.Red + 0.587 * background.Green + 0.114 * background.Blue;
        return luminance > 127.5
            ? Blend(color, 0, 0, 0, amount)        // light background -> tint darker
            : Blend(color, 255, 255, 255, amount); // dark background -> tint lighter
    }

    private static SKColor Blend(SKColor color, double towardR, double towardG, double towardB, double amount) => new SKColor(
        (byte)Math.Clamp(color.Red   + (towardR - color.Red)   * amount, 0, 255),
        (byte)Math.Clamp(color.Green + (towardG - color.Green) * amount, 0, 255),
        (byte)Math.Clamp(color.Blue  + (towardB - color.Blue)  * amount, 0, 255),
        color.Alpha);
}
