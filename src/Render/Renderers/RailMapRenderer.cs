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

/// <summary>
/// Where the legend plate's three labels land, and at what size (R-rail18-4) —
/// <see cref="RailMapRenderer.LayOutLabels"/> decides it, this carries the decision.
/// </summary>
/// <param name="TextSizePx">The size all three are drawn at, device pixels. At or below
/// <see cref="RailMapRenderer.LegendSizePx"/> — the plate never enlarges its own text.</param>
/// <param name="Cold">The minimum's box, device pixels.</param>
/// <param name="Caption">The caption's.</param>
/// <param name="Hot">The maximum's.</param>
public readonly record struct RailMapLabelLayout(
    float TextSizePx, SKRect Cold, SKRect Caption, SKRect Hot);

/// <summary>Paints a <see cref="RailMapScene"/>. Draws; decides nothing.</summary>
public static class RailMapRenderer
{
    /// <summary>A marker glyph's radius, in device pixels — fixed, so a callout on a backplane is the
    /// same size as one on a module (and see <see cref="RailMapScene.MarkerReachDbu"/>, which is the
    /// world allowance Zoom to Fit makes for it).</summary>
    public const float MarkerRadiusPx = 5f;

    /// <summary>Label point size, device pixels.</summary>
    public const float LabelSizePx = 10f;

    /// <summary>The legend's own text size, device pixels — <b>at full size.</b> See
    /// <see cref="LayOutLabels"/>: the plate is a world box and the text is in points, so on a small
    /// enough canvas the three labels do not fit at this size and are drawn smaller.</summary>
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
                    paint.Color = theme.Ramp(scene.Normalise(tile.Value));
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

        if (!TryPlate(legend, vp, out var plate, out var bar, out float textBaseline)) return;

        float x0 = plate.Left, x1 = plate.Right, y0 = plate.Top, y1 = plate.Bottom;

        // OPAQUE, and this is the one line of this file the §11.7 rule is actually about.
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.LegendBackground };
        canvas.DrawRect(plate, fill);

        using var stroke = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = theme.LegendInk,
        };
        canvas.DrawRect(plate, stroke);

        float barTop = bar.Top, barBottom = bar.Bottom, barLeft = bar.Left, barRight = bar.Right;

        using var band = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        for (int i = 0; i < LegendBands; i++)
        {
            float a = barLeft + (barRight - barLeft) * i / LegendBands;
            float b = barLeft + (barRight - barLeft) * (i + 1) / LegendBands;
            band.Color = theme.Ramp((i + 0.5) / LegendBands);
            canvas.DrawRect(new SKRect(a, barTop, b + 0.5f, barBottom), band);
        }

        var layout = LayOutLabels(legend, barLeft, barRight, textBaseline);

        using var font = Font(SkiaFonts.PlexRegular, layout.TextSizePx);
        using var ink = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.LegendInk };

        // The plate's two end labels are the SCENE's — volts on the drop tab, ohms on the |Z| tab.
        // A renderer that chose between them would be deciding what the numbers are, which is the
        // one thing R-rail8-13 says this file does not do.
        canvas.DrawText(legend.ColdLabel, barLeft, textBaseline, SKTextAlign.Left, font, ink);
        canvas.DrawText(legend.HotLabel, barRight, textBaseline, SKTextAlign.Right, font, ink);

        // The caption carries the model kind (§2.9 rule 1: every result says which model produced it,
        // and a picture pasted into a document is a result that has left the window behind). It is
        // therefore ALWAYS drawn — see LayOutLabels.
        using var caption = Font(SkiaFonts.PlexSemiBold, layout.TextSizePx);
        canvas.DrawText(legend.Caption, (barLeft + barRight) / 2, textBaseline, SKTextAlign.Center,
                        caption, ink);
    }

    /// <summary>
    /// The legend plate's screen geometry — <b>the one place it is computed</b>, so the drawing pass
    /// and anything asking where the labels land read the same rectangle.
    /// </summary>
    /// <param name="legend">The plate, whose <see cref="RailMapLegend.Box"/> is in DBU.</param>
    /// <param name="vp">World → screen.</param>
    /// <param name="plate">The whole plate, device pixels.</param>
    /// <param name="bar">The ramp's rectangle inside it.</param>
    /// <param name="baseline">Where the labels' baseline sits.</param>
    /// <returns>False where the plate has collapsed to nothing on screen, in which case it is not
    /// drawn at all.</returns>
    public static bool TryPlate(
        RailMapLegend legend, LayoutViewport vp, out SKRect plate, out SKRect bar, out float baseline)
    {
        ArgumentNullException.ThrowIfNull(legend);

        float x0 = (float)vp.WorldToScreenX(legend.Box.MinX);
        float x1 = (float)vp.WorldToScreenX(legend.Box.MaxX);
        float y0 = (float)vp.WorldToScreenY(legend.Box.MaxY);
        float y1 = (float)vp.WorldToScreenY(legend.Box.MinY);

        plate = new SKRect(x0, y0, x1, y1);
        bar = SKRect.Empty;
        baseline = 0;

        if (x1 - x0 < 2 || y1 - y0 < 2) return false;

        float pad = (y1 - y0) * 0.12f;
        bar = new SKRect(x0 + pad, y0 + pad, x1 - pad, y0 + (y1 - y0) * 0.5f);
        baseline = y1 - pad;
        return true;
    }

    /// <summary>
    /// Where the plate's three labels go, and how big they are — <b>the one place that decides it</b>,
    /// so the window, <c>RailReportPage</c> and <c>RailGraphicExport</c> cannot lay them out three
    /// ways.
    /// </summary>
    /// <remarks>
    /// <b>R-rail18-4. <c>RailMapLegend.Box</c> is a <see cref="Bbox"/> in DBU and this text is in
    /// POINTS</b>, so the plate shrinks with the canvas and the text does not: below roughly a 600 px
    /// canvas the minimum, the caption and the maximum ran into each other — in the window, and in
    /// every <c>.svg</c> and <c>.pdf</c> copied out of it.
    ///
    /// <para><b>Of the three possible answers the fix is SHRINK THE TEXT TO FIT THE BOX</b>, and the
    /// reason is that the other two give up something that is not theirs to give. WIDENING the box
    /// cannot be done here — the box is world geometry and <see cref="RailMapScene.Bounds"/> frames
    /// it, so a renderer that widened the plate would draw outside what Zoom to Fit framed (§11.6
    /// trap 4, which this legend's placement exists to satisfy); and widening it in the SCENE would
    /// make a world extent depend on a screen font size. DROPPING THE CAPTION is the one that looks
    /// cheapest and is not available at all: the caption carries the MODEL KIND, and §2.9 rule 1
    /// says every result names the model that produced it — a picture copied out of the window is a
    /// result that has left the window behind, so a plate without it is a fast answer that cannot be
    /// told from an accurate one. <c>RailZMapTests</c> asserts exactly that, and it is what rejected
    /// the threshold this was first written with.</para>
    ///
    /// <para>So all three labels always appear and the text scales without a floor. Small text is
    /// recoverable — the exports are vector and the window can be widened; overlapping text is not,
    /// and a missing model name is not either.</para>
    ///
    /// <param name="legend">The plate.</param>
    /// <param name="left">The bar's left edge, device pixels — where the cold label starts.</param>
    /// <param name="right">Its right edge — where the hot label ends.</param>
    /// <param name="baseline">The text baseline, device pixels.</param>
    public static RailMapLabelLayout LayOutLabels(
        RailMapLegend legend, float left, float right, float baseline)
    {
        ArgumentNullException.ThrowIfNull(legend);

        using var regular = Font(SkiaFonts.PlexRegular, LegendSizePx);
        using var semibold = Font(SkiaFonts.PlexSemiBold, LegendSizePx);

        float wCold = regular.MeasureText(legend.ColdLabel);
        float wHot = regular.MeasureText(legend.HotLabel);
        float wCaption = semibold.MeasureText(legend.Caption);

        float available = Math.Max(0f, right - left);
        float centre = (left + right) / 2f;

        // Clear space between neighbouring labels, in the SAME units the widths are in, so it scales
        // with them: labels that merely touch read as one word.
        const float Gap = LegendSizePx * 0.6f;

        // Each side of the centred caption on its own. A total-width test is not enough — one long
        // end label and one short one fits by total and still runs into a centred caption.
        float perSide = Math.Max(wCold, wHot) + wCaption / 2f + Gap;
        float scale = perSide > 0 ? Math.Min(1f, available / 2f / perSide) : 1f;

        float size = LegendSizePx * scale;
        float ascent = size;                           // a conservative single-line box

        return new RailMapLabelLayout(
            size,
            new SKRect(left, baseline - ascent, left + wCold * scale, baseline),
            new SKRect(centre - wCaption * scale / 2f, baseline - ascent,
                       centre + wCaption * scale / 2f, baseline),
            new SKRect(right - wHot * scale, baseline - ascent, right, baseline));
    }

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
