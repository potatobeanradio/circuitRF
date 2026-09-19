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

/// <summary>
/// An empty tab's own sentence, wrapped — <see cref="RailMapRenderer.LayOutNote"/> decides it, this
/// carries the decision.
/// </summary>
/// <param name="TextSizePx">The size the lines are drawn at, device pixels. At or below
/// <see cref="RailMapRenderer.NoteSizePx"/> — a note never enlarges itself.</param>
/// <param name="Lines">The note's lines, in order. Empty where there is no room for any.</param>
/// <param name="Box">The block they occupy, device pixels.</param>
public sealed record RailMapNoteLayout(float TextSizePx, IReadOnlyList<string> Lines, SKRect Box);

/// <summary>Paints a <see cref="RailMapScene"/>. Draws; decides nothing.</summary>
public static class RailMapRenderer
{
    /// <summary>A marker glyph's radius, in device pixels — fixed, so a callout on a backplane is the
    /// same size as one on a module (and see <see cref="RailMapScene.MarkerReachDbu"/>, which is the
    /// world allowance Zoom to Fit makes for it).</summary>
    public const float MarkerRadiusPx = 5f;

    /// <summary>Label point size, device pixels.</summary>
    public const float LabelSizePx = 10f;

    /// <summary>The selected part's outline, device pixels. <see cref="MinHighlightPx"/> is what keeps
    /// an 0402 findable at whole-board zoom — which is the zoom "where is C7" is asked at.</summary>
    public const float MinHighlightPx = 22f;

    public const float HighlightStrokePx = 2f;
    public const float HighlightInsetPx = 3f;
    public const float HighlightCornerPx = 3f;
    public const float HighlightPadRadiusPx = 2.5f;

    /// <summary>The legend's own text size, device pixels — <b>at full size.</b> See
    /// <see cref="LayOutLabels"/>: the plate is a world box and the text is in points, so on a small
    /// enough canvas the three labels do not fit at this size and are drawn smaller.</summary>
    public const float LegendSizePx = 11f;


    /// <summary>What an empty tab's sentence is drawn at, device pixels, before it is wrapped.</summary>
    public const float NoteSizePx = LegendSizePx + 1;

    /// <summary>How small that sentence may be shrunk before it simply wraps to more lines.</summary>
    public const float NoteFloorPx = 7f;

    /// <summary>What the note keeps clear of the panel's four edges, device pixels.</summary>
    public const float NoteMarginPx = 12f;

    /// <summary>Baseline-to-baseline, as a multiple of the text size.</summary>
    public const float NoteLineSpacingPx = 1.35f;

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
    /// <param name="hiddenLayers">Drawing layers the technology says are not visible, or null for
    /// none. <b>A map over artwork that is not drawn is a map of nothing a reader can see</b>: turning
    /// a layer off in the <c>.ctech</c> takes its copper off the picture, and the shading that was laid
    /// over that copper has to go with it (owner, 2026-09-19). Applied at the DRAW, never in
    /// <see cref="RailMapScene"/> — the scene is the pure function of the RESULT (R-rail8-13) and
    /// visibility is a property of the technology the frame is being drawn with, so folding it into the
    /// scene would make one result produce two scenes.</param>
    /// <param name="highlight">The part selected in the parts table, or null for none. <b>A draw
    /// argument for the same reason <paramref name="hiddenLayers"/> is</b> — a selection is not a
    /// result, so folding it into <see cref="RailMapScene"/> would make one solve produce a new
    /// scene on every click. See <see cref="RailPartHighlight"/>.</param>
    public static void Draw(SKCanvas canvas, RailMapScene scene, LayoutViewport viewport, RailMapTheme theme,
                            IReadOnlySet<LayerKey>? hiddenLayers = null,
                            RailPartHighlight? highlight = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(theme);

        if (hiddenLayers is { Count: 0 }) hiddenLayers = null;

        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, (float)viewport.Width, (float)viewport.Height));
        try
        {
            DrawTiles(canvas, scene, viewport, theme, hiddenLayers);
            DrawRegions(canvas, scene, viewport, theme, hiddenLayers);
            DrawMarkers(canvas, scene, viewport, theme);

            // AFTER the markers and BEFORE the legend. Over the markers because the selection is the
            // thing the user just asked to be shown and a port glyph sitting on the same pad would
            // otherwise cover it; under the legend because the legend is opaque chrome that must stay
            // readable (§11.7) and a mark drawn over it would be read as part of the plate.
            DrawPartHighlight(canvas, highlight, viewport, theme);

            DrawLegend(canvas, scene, viewport, theme);
            DrawNote(canvas, scene, viewport, theme);
        }
        finally { canvas.Restore(); }
    }

    // ── the drop map ───────────────────────────────────────────────────────────────────────────

    private static void DrawTiles(SKCanvas canvas, RailMapScene scene, LayoutViewport vp, RailMapTheme theme,
                                  IReadOnlySet<LayerKey>? hiddenLayers)
    {
        if (scene.Tiles.Count == 0) return;

        var plan = PlanFor(scene, theme);

        // What is actually on screen, in world DBU, widened by one tile so a tile straddling the
        // edge still paints its visible half. Clipped-away tiles cost nothing to skip and a canvas
        // call each to draw, which is the whole of the zoomed-in case.
        double vminX = vp.VisibleMinX, vmaxX = vp.VisibleMaxX;
        double vminY = vp.VisibleMinY, vmaxY = vp.VisibleMaxY;

        // One pass per drawing layer, because the clip is per layer: the shading has to stop at the
        // artwork's own edge and not at the sampling grid's, and a grid cell straddling the edge of a
        // trace would otherwise paint copper that is not there.
        using var paint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };

        foreach (var group in plan.Layers)
        {
            if (hiddenLayers?.Contains(group.Layer) == true) continue;

            canvas.Save();
            try
            {
                if (scene.Clip.TryGetValue(group.Layer, out var paths) && paths.Count > 0)
                {
                    using var clip = ToPath(paths, vp);
                    canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
                }

                var tiles = group.Tiles;
                var colours = group.Colours;
                for (int i = 0; i < tiles.Length; i++)
                {
                    var tile = tiles[i];
                    long half = tile.HalfSpanDbu;
                    if (tile.CentreX + half < vminX || tile.CentreX - half > vmaxX) continue;
                    if (tile.CentreY + half < vminY || tile.CentreY - half > vmaxY) continue;

                    paint.Color = colours[i];
                    canvas.DrawRect(RectOf(tile, vp), paint);
                }
            }
            finally { canvas.Restore(); }
        }
    }

    // ── the per-scene draw plan, and why a cache is here at all ────────────────────────────────
    //
    // A drop map is one rect per extraction cell, and on the accurate reading that is the MESH —
    // tens to hundreds of thousands of them. Every frame of a pan used to rebuild a dictionary of
    // lists holding every tile, and to re-evaluate the colour ramp for every tile, before drawing
    // any of them: work that is a pure function of the scene and the theme, repeated at the frame
    // rate. On a real board with the drop map on, that is the whole of the reported "panning and
    // zoom frame rate is terribly slow", and with Accuracy on as well it is under one frame a
    // second (owner, 2026-09-19).
    //
    // NOTHING HERE CHANGES A PIXEL. The grouping is the same grouping in the same emission order,
    // the colour is the same colour, and the rects are still produced by RectOf off the live
    // viewport — so the two-renders-of-one-scene identity brief 9's clipboard gate and brief 17's
    // figures depend on is untouched. What is removed is only the repetition.
    //
    // Keyed on the scene by REFERENCE, in a weak table: a scene is immutable and is replaced
    // wholesale whenever the result changes (RailLayoutOverlay.Invalidate drops it), so a live scene
    // is exactly the right lifetime and a dead one must not be held. The theme is stored alongside
    // and the plan recomputed when it changes, which is a theme switch and not a frame.
    private sealed record TileLayerPlan(LayerKey Layer, RailMapTile[] Tiles, SKColor[] Colours);

    private sealed class TilePlan
    {
        public required RailMapTheme Theme { get; init; }
        public required IReadOnlyList<TileLayerPlan> Layers { get; init; }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RailMapScene, TilePlan> Plans = new();

    private static TilePlan PlanFor(RailMapScene scene, RailMapTheme theme)
    {
        if (Plans.TryGetValue(scene, out var cached) && ReferenceEquals(cached.Theme, theme))
            return cached;

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
        var layers = new List<TileLayerPlan>(order.Count);
        foreach (var key in order)
        {
            var tiles = byLayer[key].ToArray();
            var colours = new SKColor[tiles.Length];
            for (int i = 0; i < tiles.Length; i++)
                colours[i] = theme.Ramp(scene.Normalise(tiles[i].Value));
            layers.Add(new TileLayerPlan(key, tiles, colours));
        }

        var plan = new TilePlan { Theme = theme, Layers = layers };
        Plans.AddOrUpdate(scene, plan);
        return plan;
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

    private static void DrawRegions(SKCanvas canvas, RailMapScene scene, LayoutViewport vp, RailMapTheme theme,
                                    IReadOnlySet<LayerKey>? hiddenLayers)
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
            if (hiddenLayers?.Contains(region.Layer) == true) continue;

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

    // ── the selected part ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The outline and pad dots for the part selected in the parts table.
    /// </summary>
    /// <remarks>
    /// <b>Drawn in its own colour rather than the layout editor's selection colour.</b> This mark sits
    /// on top of a drop map whose entire palette is a cold-to-hot ramp; a mark whose colour falls
    /// inside that ramp is invisible exactly where the map is interesting. See
    /// <see cref="ColorRole.RailPartSelection"/>.
    ///
    /// <para>A part the board does not place produces an EMPTY outline and nothing is drawn — the
    /// caller decides whether to offer the mark, and it learns there is none from the picture staying
    /// as it was rather than from a box appearing at the origin.</para>
    /// </remarks>
    private static void DrawPartHighlight(
        SKCanvas canvas, RailPartHighlight? highlight, LayoutViewport vp, RailMapTheme theme)
    {
        if (highlight is not { } part) return;

        var box = part.Outline;
        if (box.IsEmpty) return;

        float x0 = (float)vp.WorldToScreenX(box.MinX), x1 = (float)vp.WorldToScreenX(box.MaxX);
        float y0 = (float)vp.WorldToScreenY(box.MinY), y1 = (float)vp.WorldToScreenY(box.MaxY);
        var rect = new SKRect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));

        // A part zoomed out to nothing is still findable: the outline never shrinks below a size a
        // user can see, because "where is C7" is asked most often from a view of the whole board.
        float padX = Math.Max(0f, MinHighlightPx - rect.Width) / 2f;
        float padY = Math.Max(0f, MinHighlightPx - rect.Height) / 2f;
        rect.Inflate(padX + HighlightInsetPx, padY + HighlightInsetPx);

        using var stroke = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = HighlightStrokePx, Color = theme.PartSelection,
        };
        canvas.DrawRoundRect(rect, HighlightCornerPx, HighlightCornerPx, stroke);

        using var dot = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.PartSelection,
        };
        foreach (var (px, py) in part.Pads)
            canvas.DrawCircle((float)vp.WorldToScreenX(px), (float)vp.WorldToScreenY(py),
                              HighlightPadRadiusPx, dot);

        using var font = Font(SkiaFonts.PlexSemiBold, LabelSizePx);
        canvas.DrawText(part.Label, rect.MidX, rect.Top - 4f, SKTextAlign.Center, font, dot);
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

    /// <summary>
    /// Where an empty tab's sentence lands — <b>wrapped to the panel it is in, and never one line</b>.
    /// </summary>
    /// <remarks>
    /// The note is a SENTENCE and not a label: the |Z| tab's runs to twenty words, because what it
    /// has to say is which OTHER run produces that picture. Drawn as a single centred line it was cut
    /// off at both panel edges, and the half a reader needs — the instruction at the end — was the
    /// half that went. So it wraps, and the block is centred as a block.
    ///
    /// <para>Shrinking comes SECOND and only where wrapping alone cannot fit it: unlike the legend's
    /// plate (<see cref="LayOutLabels"/>, whose box is world geometry and cannot be widened), a note
    /// has the whole panel and more lines are free. The floor exists because text below about 7 px is
    /// not readable on any of the three targets this renderer draws — at that point the honest
    /// picture is a note that overflows a pane nobody could read it in anyway, not a smaller one.</para>
    /// </remarks>
    /// <param name="note">The sentence. Split on whitespace, so an embedded newline is a word break.</param>
    /// <param name="width">The viewport's width, device pixels.</param>
    /// <param name="height">Its height.</param>
    public static RailMapNoteLayout LayOutNote(string note, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(note);

        float available = (float)width - 2 * NoteMarginPx;
        float room = (float)height - 2 * NoteMarginPx;

        string[] words = note.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || available <= 0 || room <= 0)
            return new RailMapNoteLayout(NoteSizePx, [], SKRect.Empty);

        float size = NoteSizePx;
        List<string> lines;
        float widest;
        while (true)
        {
            using var font = Font(SkiaFonts.PlexRegular, size);
            lines = Wrap(words, font, available, out widest);

            if ((widest <= available && lines.Count * size * NoteLineSpacingPx <= room)
                || size <= NoteFloorPx)
                break;

            // A fixed ladder rather than a computed ratio: two renders of one scene have to be byte
            // identical (R-rail8-13's own gate), and the ratio would depend on which of the two
            // constraints bound.
            size = Math.Max(NoteFloorPx, size - 0.5f);
        }

        float lineHeight = size * NoteLineSpacingPx;
        float block = lines.Count * lineHeight;
        float centre = (float)width / 2f;
        float top = (float)height / 2f - block / 2f;

        return new RailMapNoteLayout(
            size, lines, new SKRect(centre - widest / 2f, top, centre + widest / 2f, top + block));
    }

    /// <summary>Greedy word wrap at <paramref name="font"/>'s own measurement — no second guess at a
    /// string's width, which is what let the legend's labels disagree with themselves.</summary>
    private static List<string> Wrap(string[] words, SKFont font, float available, out float widest)
    {
        var lines = new List<string>();
        widest = 0f;
        string line = "";

        foreach (string word in words)
        {
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && font.MeasureText(candidate) > available)
            {
                lines.Add(line);
                widest = Math.Max(widest, font.MeasureText(line));
                line = word;
            }
            else line = candidate;
        }

        if (line.Length > 0)
        {
            lines.Add(line);
            widest = Math.Max(widest, font.MeasureText(line));
        }

        return lines;
    }

    private static void DrawNote(SKCanvas canvas, RailMapScene scene, LayoutViewport vp, RailMapTheme theme)
    {
        if (scene.Note is not { Length: > 0 } note) return;
        if (scene.Tiles.Count > 0 || scene.Regions.Count > 0) return;

        var layout = LayOutNote(note, vp.Width, vp.Height);
        if (layout.Lines.Count == 0) return;

        using var font = Font(SkiaFonts.PlexRegular, layout.TextSizePx);
        using var ink = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.LegendInk };

        float centre = (float)(vp.Width / 2);
        float lineHeight = layout.TextSizePx * NoteLineSpacingPx;

        for (int i = 0; i < layout.Lines.Count; i++)
            canvas.DrawText(layout.Lines[i], centre, layout.Box.Top + i * lineHeight + layout.TextSizePx,
                            SKTextAlign.Center, font, ink);
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
