// railRF's board maps, painted. (brief-railrf-8-board-view.md R-rail8-9 … R-rail8-13;
// railrf.md §2.4, §2.9, §11.7 point 2.)
//
// ── THE RULE THIS FILE IS WRITTEN UNDER, AND IT IS FREE HERE AND EXPENSIVE LATER ───────────────
//
// §11.7 point 2, decided in the design note "rather than discovered in an export":
//
//     For every other copy the background is a BACKDROP, so honouring transparency is a matter of
//     not painting it; the stackup copy is the counter-example, and it is instructive — its
//     renderer uses the background colour as PAINT, to cut a drill hole, and simply not painting
//     the bore did not make it transparent, it showed the dielectric behind it. A hole had to be
//     cut in what was behind.
//
//     A railRF map must not acquire that shape: the shading is OPAQUE PAINT laid over the copper
//     and the legend carries its own scale, so NOTHING IN THE PICTURE DEPENDS ON THE PAGE'S
//     BACKGROUND BEING ANY PARTICULAR COLOUR.
//
// Concretely, for anyone extending this file: never read a background colour, never clear a region
// to one, never use SKBlendMode.Clear/DstOut/SrcOut, and never rely on an alpha to reveal what is
// underneath. Every colour RailMapTheme hands out is opaque, and the theme's own header says why.
//
// ── IT COMPUTES NOTHING ────────────────────────────────────────────────────────────────────────
//
// R-rail8-13: RailMapScene is the pure function of the result; this paints what that produced. The
// only arithmetic here is scene→screen, which is LayoutViewport's, and the ramp lookup, which is
// RailMapTheme.Ramp — shared with the legend so the plate and the map cannot come to disagree.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using SkiaSharp;

namespace CircuitRF.Render;

/// <summary>Paints a <see cref="RailMapScene"/>. Draws; decides nothing.</summary>
public static class RailMapRenderer
{
    /// <summary>A marker glyph's radius, in device pixels — fixed, so a callout on a backplane is the
    /// same size as one on a module (and see <see cref="RailMapScene.MarkerReachDbu"/>, which is the
    /// world allowance Zoom to Fit makes for it).</summary>
    public const float MarkerRadiusPx = 5f;

    /// <summary>Label point size, device pixels.</summary>
    public const float LabelSizePx = 10f;

    /// <summary>The legend's own text size, device pixels.</summary>
    public const float LegendSizePx = 11f;

    /// <summary>How many bands the legend's ramp is drawn in. Enough to read as continuous, few
    /// enough that two renders of the same scene are trivially identical.</summary>
    public const int LegendBands = 48;

    /// <summary>
    /// Draws <paramref name="scene"/> into the canvas the layout was just drawn into, in the same
    /// Skia lease.
    /// </summary>
    /// <param name="canvas">The canvas. Clipped to the viewport before anything is painted, because
    /// nothing else clips this pass — the layout underneath is culled before it is drawn and a map
    /// tile is not, so an off-screen tile would paint straight across whatever is docked beside the
    /// board panel (the wBond overlay's own finding).</param>
    /// <param name="scene">What to draw.</param>
    /// <param name="viewport">World → screen.</param>
    /// <param name="theme">railRF's own colours, projected from the active theme.</param>
    public static void Draw(SKCanvas canvas, RailMapScene scene, LayoutViewport viewport, RailMapTheme theme)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(theme);

        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, (float)viewport.Width, (float)viewport.Height));
        try
        {
            DrawTiles(canvas, scene, viewport, theme);
            DrawRegions(canvas, scene, viewport, theme);
            DrawMarkers(canvas, scene, viewport, theme);
            DrawLegend(canvas, scene, viewport, theme);
            DrawNote(canvas, scene, viewport, theme);
        }
        finally { canvas.Restore(); }
    }

    // ── the drop map ───────────────────────────────────────────────────────────────────────────

    private static void DrawTiles(SKCanvas canvas, RailMapScene scene, LayoutViewport vp, RailMapTheme theme)
    {
        if (scene.Tiles.Count == 0) return;

        // One pass per drawing layer, because the clip is per layer: the shading has to stop at the
        // artwork's own edge and not at the sampling grid's, and a grid cell straddling the edge of a
        // trace would otherwise paint copper that is not there.
        foreach (var group in GroupByLayer(scene))
        {
            canvas.Save();
            try
            {
                if (scene.Clip.TryGetValue(group.Key, out var paths) && paths.Count > 0)
                {
                    using var clip = ToPath(paths, vp);
                    canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
                }

                using var paint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
                foreach (var tile in group.Value)
                {
                    paint.Color = theme.Ramp(scene.Normalise(tile.ValueV));
                    canvas.DrawRect(RectOf(tile, vp), paint);
                }
            }
            finally { canvas.Restore(); }
        }
    }

    private static List<KeyValuePair<LayerKey, List<RailMapTile>>> GroupByLayer(RailMapScene scene)
    {
        var byLayer = new Dictionary<LayerKey, List<RailMapTile>>();
        var order = new List<LayerKey>();
        foreach (var tile in scene.Tiles)
        {
            if (!byLayer.TryGetValue(tile.Layer, out var list))
            {
                byLayer[tile.Layer] = list = [];
                order.Add(tile.Layer);
            }
            list.Add(tile);
        }

        // Emission order is the scene's own, not a dictionary's: two renders of one scene have to be
        // byte-identical (brief 9's clipboard gate and brief 17's figures both depend on it).
        return [.. order.Select(k => new KeyValuePair<LayerKey, List<RailMapTile>>(k, byLayer[k]))];
    }

    private static SKRect RectOf(RailMapTile tile, LayoutViewport vp)
    {
        float x0 = (float)vp.WorldToScreenX(tile.CentreX - tile.HalfSpanDbu);
        float x1 = (float)vp.WorldToScreenX(tile.CentreX + tile.HalfSpanDbu);
        float y0 = (float)vp.WorldToScreenY(tile.CentreY + tile.HalfSpanDbu);   // world Y is up
        float y1 = (float)vp.WorldToScreenY(tile.CentreY - tile.HalfSpanDbu);
        return new SKRect(x0, y0, x1, y1);
    }

    // ── the class tab, and the copper tab's islands ────────────────────────────────────────────

    private static void DrawRegions(SKCanvas canvas, RailMapScene scene, LayoutViewport vp, RailMapTheme theme)
    {
        if (scene.Regions.Count == 0) return;

        bool outlineOnly = scene.Kind == RailMapKind.Copper;

        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f,
        };

        foreach (var region in scene.Regions)
        {
            using var path = ToPath(region.Copper, vp);

            if (outlineOnly)
            {
                stroke.Color = theme.CopperHighlight;
                stroke.StrokeWidth = 1.5f;
                canvas.DrawPath(path, stroke);
                continue;
            }

            fill.Color = region.Class == PdnCopperClass.Trace ? theme.ClassTrace : theme.ClassSpreading;
            canvas.DrawPath(path, fill);

            // A FORCED region draws differently from an inferred one — §2.9 rule 2 is about the user
            // being able to see what they overrode, and a fill that looked the same either way would
            // make the override invisible, which is the failure this whole tab exists to prevent.
            stroke.Color = region.Forced ? theme.ClassForced : theme.LegendInk;
            stroke.StrokeWidth = region.Forced ? 3f : 1f;
            canvas.DrawPath(path, stroke);
        }
    }

    // ── markers ───────────────────────────────────────────────────────────────────────────────

    private static void DrawMarkers(SKCanvas canvas, RailMapScene scene, LayoutViewport vp, RailMapTheme theme)
    {
        if (scene.Markers.Count == 0) return;

        using var font = Font(SkiaFonts.PlexSemiBold, LabelSizePx);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f };

        foreach (var marker in scene.Markers)
        {
            float x = (float)vp.WorldToScreenX(marker.X);
            float y = (float)vp.WorldToScreenY(marker.Y);

            var colour = marker.Kind switch
            {
                RailMarkerKind.Source  => theme.Source,
                RailMarkerKind.ViaFlag => theme.ViaFlag,
                _                      => theme.Load,
            };

            fill.Color = colour;
            ring.Color = theme.LegendInk;

            if (marker.Kind == RailMarkerKind.Observation)
            {
                // A port that draws nothing is a RING rather than a disc: it is on the board and it
                // is not a load, and the two must not look the same (R-rail5-10, seen as a picture).
                ring.Color = colour;
                canvas.DrawCircle(x, y, MarkerRadiusPx, ring);
            }
            else
            {
                canvas.DrawCircle(x, y, MarkerRadiusPx, fill);
                canvas.DrawCircle(x, y, MarkerRadiusPx, ring);
            }

            fill.Color = theme.LegendInk;
            canvas.DrawText(marker.Label, x + MarkerRadiusPx + 3f, y + LabelSizePx * 0.35f,
                            SKTextAlign.Left, font, fill);
        }
    }

    // ── the legend, inside the picture ────────────────────────────────────────────────────────

    private static void DrawLegend(SKCanvas canvas, RailMapScene scene, LayoutViewport vp, RailMapTheme theme)
    {
        if (scene.Legend is not { } legend) return;

        float x0 = (float)vp.WorldToScreenX(legend.Box.MinX);
        float x1 = (float)vp.WorldToScreenX(legend.Box.MaxX);
        float y0 = (float)vp.WorldToScreenY(legend.Box.MaxY);
        float y1 = (float)vp.WorldToScreenY(legend.Box.MinY);
        if (x1 - x0 < 2 || y1 - y0 < 2) return;

        var plate = new SKRect(x0, y0, x1, y1);

        // OPAQUE, and this is the one line of this file the §11.7 rule is actually about.
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.LegendBackground };
        canvas.DrawRect(plate, fill);

        using var stroke = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = theme.LegendInk,
        };
        canvas.DrawRect(plate, stroke);

        float pad = (y1 - y0) * 0.12f;
        float barTop = y0 + pad;
        float barBottom = y0 + (y1 - y0) * 0.5f;
        float barLeft = x0 + pad;
        float barRight = x1 - pad;

        using var band = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        for (int i = 0; i < LegendBands; i++)
        {
            float a = barLeft + (barRight - barLeft) * i / LegendBands;
            float b = barLeft + (barRight - barLeft) * (i + 1) / LegendBands;
            band.Color = theme.Ramp((i + 0.5) / LegendBands);
            canvas.DrawRect(new SKRect(a, barTop, b + 0.5f, barBottom), band);
        }

        using var font = Font(SkiaFonts.PlexRegular, LegendSizePx);
        using var ink = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.LegendInk };
        float textBaseline = y1 - pad;

        canvas.DrawText(Volts(legend.ColdValue), barLeft, textBaseline, SKTextAlign.Left, font, ink);
        canvas.DrawText(Volts(legend.HotValue), barRight, textBaseline, SKTextAlign.Right, font, ink);

        // The caption carries the model kind (§2.9 rule 1: every result says which model produced it,
        // and a picture pasted into a document is a result that has left the window behind).
        using var caption = Font(SkiaFonts.PlexSemiBold, LegendSizePx);
        canvas.DrawText(legend.Caption, (barLeft + barRight) / 2, textBaseline, SKTextAlign.Center,
                        caption, ink);
    }

    private static string Volts(double v) =>
        Math.Abs(v) >= 1.0 ? $"{v:0.####} V" : $"{v * 1e3:0.###} mV";

    private static void DrawNote(SKCanvas canvas, RailMapScene scene, LayoutViewport vp, RailMapTheme theme)
    {
        if (scene.Note is not { Length: > 0 } note) return;
        if (scene.Tiles.Count > 0 || scene.Regions.Count > 0) return;

        using var font = Font(SkiaFonts.PlexRegular, LegendSizePx + 1);
        using var ink = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.LegendInk };
        canvas.DrawText(note, (float)(vp.Width / 2), (float)(vp.Height / 2), SKTextAlign.Center, font, ink);
    }

    /// <summary>
    /// Every font this renderer draws with, in GREYSCALE antialiasing.
    /// </summary>
    /// <remarks>
    /// <b>Not Skia's default, and the reason is §11.7.</b> Subpixel (LCD) antialiasing spreads a
    /// glyph's coverage unevenly across the red, green and blue channels, on the assumption that the
    /// pixels it lands on are a particular physical stripe order. This picture does not stay on a
    /// screen: §11.7 copies it out as PDF, SVG and a bitmap that may be rescaled, and the same
    /// renderer draws the headless report. A glyph carrying one display's subpixel layout into a page
    /// is a label that reads differently depending on where it is looked at — which is the same class
    /// of defect as a map that depends on the page's background, and it is free to avoid here.
    /// </remarks>
    private static SKFont Font(SKTypeface typeface, float size) =>
        new(typeface, size) { Edging = SKFontEdging.Antialias, Subpixel = false };

    // ── geometry ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Clipper polygons → one screen-space <see cref="SKPath"/>.
    /// </summary>
    /// <remarks>
    /// Converted at the SCREEN, never in world coordinates: a board coordinate in DBU routinely
    /// exceeds what a <c>float</c> holds exactly (a 200 mm board at the 1000 DBU/µm default is
    /// 2 × 10<sup>8</sup> DBU), and an <c>SKPath</c> is float throughout. The viewport transform is
    /// done in <c>double</c> first, so what reaches Skia is a pixel coordinate.
    /// </remarks>
    private static SKPath ToPath(Clipper2Lib.Paths64 paths, LayoutViewport vp)
    {
        var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        foreach (var ring in paths)
        {
            if (ring.Count < 3) continue;
            for (int i = 0; i < ring.Count; i++)
            {
                float x = (float)vp.WorldToScreenX(ring[i].X);
                float y = (float)vp.WorldToScreenY(ring[i].Y);
                if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
            }
            path.Close();
        }
        return path;
    }
}
