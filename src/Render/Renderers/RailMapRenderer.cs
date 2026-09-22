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
using CircuitRF.Design.RailRf;
using SkiaSharp;

namespace CircuitRF.Render;

/// <summary>
/// Where the legend plate's three labels land, and at what size (R-rail18-4) —
/// <see cref="RailMapRenderer.LayOutLabels"/> decides it, this carries the decision.
/// </summary>
/// <param name="TextSizePx">The size the labels are drawn at, device pixels. It tracks the PLATE,
/// which is world geometry, so it GROWS with the zoom — and it is never below
/// <see cref="RailMapRenderer.LegendFloorPx"/>, because text under that is not a legend
/// (R-rail21-3a). <see cref="RailMapRenderer.LegendSizePx"/> is the size the strings are measured
/// at.</param>
/// <param name="Cold">The minimum's box, device pixels.</param>
/// <param name="Caption">The caption's. Empty when <paramref name="CaptionDrawn"/> is false.</param>
/// <param name="Hot">The maximum's.</param>
/// <param name="Drawn">False where not even the two end labels fit at the floor — then NOTHING is
/// drawn, plate included (R-rail21-3b).</param>
/// <param name="CaptionDrawn">False where the caption was dropped to keep the end labels at the
/// floor.</param>
public readonly record struct RailMapLabelLayout(
    float TextSizePx, SKRect Cold, SKRect Caption, SKRect Hot, bool Drawn, bool CaptionDrawn);

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

    /// <summary>The size the legend's three strings are MEASURED at, device pixels — the reference
    /// the fit below is expressed as a ratio of, not a ceiling on what is drawn. See
    /// <see cref="LayOutLabels"/>: the plate is a world box and the text is in points, so on a small
    /// enough canvas the three labels do not fit at this size and are drawn smaller.</summary>
    public const float LegendSizePx = 11f;


    /// <summary>
    /// <b>The size legend text is never drawn below, device pixels (R-rail21-3a).</b>
    /// </summary>
    /// <remarks>
    /// <b>It is <see cref="LabelSizePx"/> on purpose, and that is the whole definition.</b> The
    /// marker callouts on this same picture are drawn at that size and are screen-fixed — a
    /// deliberate decision, so a callout on a backplane is the same size as one on a module. A
    /// legend smaller than the callouts beside it is not a legend, and pinning the floor to that
    /// number means there is ONE notion of "readable on this map" rather than two that drift.
    ///
    /// <para>R-rail18-4 fixed the three labels OVERLAPPING on a small canvas by shrinking them to
    /// fit. It did not fix their size, and it had no floor: on a real board's pane the caption came
    /// out around six pixels — a grey smear, with the numbers at the ends barely a pixel tall
    /// (2026-09-20). <b>An unreadable legend is strictly worse than no legend</b>: it occupies the
    /// space where the answer would go and looks like a rendering fault. So below this, text is
    /// DROPPED rather than scaled — see <see cref="LayOutLabels"/>.</para>
    /// </remarks>
    public const float LegendFloorPx = LabelSizePx;

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
    /// <param name="batchTiles">Whether to batch the drop map's tiles into one triangle list — see
    /// <c>RailLayoutOverlay.Draw</c>'s own note for the frame-rate measurement that decided it.</param>
    /// <param name="netPreview">The net highlighted in the pick list and not yet made a rail, or null
    /// for none. A draw argument for <paramref name="highlight"/>'s reason, and drawn in its own role
    /// so it cannot be mistaken for a committed rail (R-rail19-2b).</param>
    /// <param name="notFitted">Every part the table says is NOT FITTED, or null for none — <b>the
    /// state R-rail23-1's checkbox puts a row in, said on the board</b> (owner, 2026-09-21).
    ///
    /// <para><b>The footprint is NEVER removed, and that is the whole shape of this.</b> What is
    /// drawn under a part is its land pattern — copper, mask and silkscreen — and all of it is
    /// etched and printed on the board whether or not a component is soldered onto it; unmounting
    /// says what is fitted TO the geometry and never touches it (R-rail23-1c). So the land pattern
    /// stays and this marks it, exactly as the <c>Observation</c> marker is drawn rather than
    /// omitted: <i>not there</i> and <i>there and contributing nothing</i> must not look the
    /// same.</para>
    ///
    /// <para>A draw argument for <paramref name="highlight"/>'s reason — mount state is a property
    /// of the document, not of the solve, and folding it into <see cref="RailMapScene"/> would make
    /// one result produce a new scene on every tick of a checkbox.</para></param>
    public static void Draw(SKCanvas canvas, RailMapScene scene, LayoutViewport viewport, RailMapTheme theme,
                            IReadOnlySet<LayerKey>? hiddenLayers = null,
                            RailPartHighlight? highlight = null,
                            bool batchTiles = false,
                            RailNetPreview? netPreview = null,
                            IReadOnlyList<RailPartHighlight>? notFitted = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(theme);

        if (hiddenLayers is { Count: 0 }) hiddenLayers = null;

        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, (float)viewport.Width, (float)viewport.Height));
        try
        {
            DrawTiles(canvas, scene, viewport, theme, hiddenLayers, batchTiles);
            DrawRegions(canvas, scene, viewport, theme, hiddenLayers);

            // UNDER the markers, and under everything below them. This is AMBIENT state — it is up
            // for every unmounted row at once, nobody asked for it this frame, and there may be two
            // dozen of them. A source or a load resolved onto the same pad is an ANSWER and has to
            // stay on top of it; so does a selection, which is the one thing the user did ask for.
            DrawNotFitted(canvas, notFitted, viewport, theme);

            DrawMarkers(canvas, scene, viewport, theme);

            // AFTER the markers and BEFORE the legend. Over the markers because the selection is the
            // thing the user just asked to be shown and a port glyph sitting on the same pad would
            // otherwise cover it; under the legend because the legend is opaque chrome that must stay
            // readable (§11.7) and a mark drawn over it would be read as part of the plate.
            // UNDER the part highlight, because a part the user picked is a more specific answer
            // than the net a list row is merely resting on, and the two can cover the same pad.
            DrawNetPreview(canvas, netPreview, viewport, theme);
            DrawPartHighlight(canvas, highlight, viewport, theme);

            DrawLegend(canvas, scene, viewport, theme);
            DrawNote(canvas, scene, viewport, theme);
        }
        finally { canvas.Restore(); }
    }

    // ── the drop map ───────────────────────────────────────────────────────────────────────────

    private static void DrawTiles(SKCanvas canvas, RailMapScene scene, LayoutViewport vp, RailMapTheme theme,
                                  IReadOnlySet<LayerKey>? hiddenLayers, bool batchTiles)
    {
        if (scene.Tiles.Count == 0) return;

        var plan = PlanFor(scene, theme);
        var meshes = batchTiles ? MeshFor(plan) : null;

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

                if (meshes is not null && meshes.TryGetValue(group.Layer, out var mesh))
                {
                    DrawMesh(canvas, mesh, vp);
                    continue;
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

        /// <summary>The batched form, built on first use and only when a caller asked for it — see
        /// <see cref="MeshFor"/>. Null until then.
        ///
        /// <para><b>There is deliberately no "only worth it above N tiles" threshold.</b> One was
        /// written and removed: the two forms draw the same picture, so a threshold buys nothing —
        /// and it silently made this file's own equality gate VACUOUS, because every layer of the
        /// gate's fixture fell under it and the batched path the test names was never run.</para></summary>
        public IReadOnlyDictionary<LayerKey, TileMesh>? Meshes;
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

    // ── the batched map, and why the rectangles are issued as ONE operation ────────────────────
    //
    // ONE DRAW CALL PER LAYER INSTEAD OF ONE PER EXTRACTION CELL. The plan above removed the
    // per-frame REBUILD of the tile lists and the colour ramp; this removes the per-frame ISSUE,
    // which is the part that was actually costing the frame. On a four-layer sensor board with
    // Accuracy on, the drop map is 64,907 tiles and every one of them was a separate
    // canvas.DrawRect on a Metal-backed surface — on every frame, AND on every pointer move, since
    // LayoutCanvas.OnPointerMoved invalidates unconditionally. A `sample` of the running
    // application caught the render thread pegged at 100% with 86% of it inside one Skia function
    // under the canvas-playback chain and the GPU essentially idle: the cost was Skia's
    // PER-OPERATION CPU work, multiplied by 64,907 (owner, 2026-09-19 — panning at about one frame
    // a second, and the bare cursor no better).
    //
    // ── WHY VERTICES AND NOT AN IMAGE ─────────────────────────────────────────────────────────
    //
    // Pre-painting each layer into one image and blitting it is the obvious answer and it is WRONG
    // here, which is worth recording because it looks right and it measured pixel-perfect on the
    // board it was written against. A sheet has to assume the tiles lie on a lattice, and they do
    // not: RailMapScene's interpolated branch takes `half = step / 2` in INTEGER division, so on an
    // odd step every tile is one DBU narrower than its own spacing. The error is invisible per tile
    // and accumulates across a few hundred of them into a whole-tile shift — 0.53% of the picture
    // wrong by up to 225 levels on the very first fixture it was gated against.
    //
    // A triangle list assumes nothing. Each tile is two triangles at its OWN corners, so a tile that
    // does not abut its neighbour does not abut it here either, and the picture is the rectangles'
    // picture whatever the scene put in it. Built once in world coordinates and drawn under the
    // viewport's own matrix, so one mesh serves every pan and zoom.
    //
    // NOT ON BY DEFAULT, and that is the point of the argument. The report and the clipboard draw
    // through VectorPage to SVG and PDF, where the map has to stay VECTOR — vertices would flatten
    // to a triangle soup and an SVG of a drop map is a thing people zoom into. The window opts in;
    // every headless caller keeps the rectangles. Same split as `render --detail screen` versus
    // `--detail full`, and for the same reason.

    /// <summary>One layer's tiles as a triangle list, in world DBU relative to its own origin.</summary>
    /// <param name="Vertices">Two triangles per tile, each triangle flat-coloured because its three
    /// vertices carry the same colour.</param>
    /// <param name="OriginX">What the vertex coordinates are relative to. <b>Not a tidiness
    /// measure</b>: a vertex is a <c>float</c>, whose 24-bit mantissa stops being exact at about
    /// 1.7e7, and a board's DBU coordinates reach past that. Relative to the layer's own corner they
    /// do not.</param>
    /// <param name="OriginY">Same.</param>
    private sealed record TileMesh(SKVertices Vertices, long OriginX, long OriginY);

    private static IReadOnlyDictionary<LayerKey, TileMesh>? MeshFor(TilePlan plan)
    {
        if (plan.Meshes is { } built) return built;

        var meshes = new Dictionary<LayerKey, TileMesh>();
        foreach (var group in plan.Layers)
            if (BuildMesh(group) is { } mesh)
                meshes[group.Layer] = mesh;

        plan.Meshes = meshes;
        return meshes;
    }

    private static TileMesh? BuildMesh(TileLayerPlan group)
    {
        var tiles = group.Tiles;
        if (tiles.Length == 0) return null;

        long ox = long.MaxValue, oy = long.MaxValue;
        foreach (var t in tiles)
        {
            if (t.CentreX - t.HalfSpanDbu < ox) ox = t.CentreX - t.HalfSpanDbu;
            if (t.CentreY - t.HalfSpanDbu < oy) oy = t.CentreY - t.HalfSpanDbu;
        }

        var points  = new SKPoint[tiles.Length * 6];
        var colours = new SKColor[tiles.Length * 6];

        for (int i = 0, v = 0; i < tiles.Length; i++)
        {
            var t = tiles[i];
            float x0 = t.CentreX - t.HalfSpanDbu - ox, x1 = t.CentreX + t.HalfSpanDbu - ox;
            float y0 = t.CentreY - t.HalfSpanDbu - oy, y1 = t.CentreY + t.HalfSpanDbu - oy;
            var c = group.Colours[i];

            points[v + 0] = new SKPoint(x0, y0);
            points[v + 1] = new SKPoint(x1, y0);
            points[v + 2] = new SKPoint(x1, y1);
            points[v + 3] = new SKPoint(x0, y0);
            points[v + 4] = new SKPoint(x1, y1);
            points[v + 5] = new SKPoint(x0, y1);
            for (int k = 0; k < 6; k++) colours[v + k] = c;
            v += 6;
        }

        return new TileMesh(SKVertices.CreateCopy(SKVertexMode.Triangles, points, colours), ox, oy);
    }

    /// <summary>
    /// Draws a layer's mesh under the viewport's own transform.
    /// </summary>
    /// <remarks>
    /// The matrix is <see cref="RectOf"/>'s arithmetic expressed once instead of per tile: the scale
    /// is the zoom, Y is negated because the world is Y-up and the screen is Y-down, and the
    /// translation puts the mesh's origin where <see cref="LayoutViewport.WorldToScreenX"/> and
    /// <see cref="LayoutViewport.WorldToScreenY"/> put it. No culling — the whole mesh is one
    /// operation, and asking the GPU to discard what is off screen is cheaper than splitting it.
    /// </remarks>
    private static readonly SKPaint MeshPaint =
        new() { IsAntialias = false, Style = SKPaintStyle.Fill, Color = SKColors.White };

    private static void DrawMesh(SKCanvas canvas, TileMesh mesh, LayoutViewport vp)
    {
        canvas.Save();
        try
        {
            canvas.Translate((float)vp.WorldToScreenX(mesh.OriginX), (float)vp.WorldToScreenY(mesh.OriginY));
            canvas.Scale((float)vp.Zoom, -(float)vp.Zoom);

            // WHITE, AND IT IS NOT COSMETIC. SKBlendMode.Modulate MULTIPLIES the vertex colour by
            // the paint's, so handing it the shared tile paint — whose Color is whatever the last
            // rectangle set, and black on a frame that drew none — renders the entire map black
            // with its coverage pixel-for-pixel correct. That is a map that is wrong in the one way
            // a coverage check cannot see, which is why the gate compares COLOURS.
            canvas.DrawVertices(mesh.Vertices, SKBlendMode.Modulate, MeshPaint);
        }
        finally { canvas.Restore(); }
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

    /// <summary>How opaque the copper tab's two series sections are washed — light enough that the
    /// artwork under them is still what the tab shows.</summary>
    private const byte SectionWashAlpha = 56;

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
                // ── brief 25, R-rail25-4c: the two sections shade differently ────────────────
                //
                // Only where the rail HAS a series element — Section is null otherwise and the tab
                // draws the bare outline it always drew. A translucent wash rather than the class
                // tab's opaque fill: this tab's job is to show the artwork AS DRAWN with the rail
                // outlined over it, and painting over the copper would take that away to answer a
                // question that only exists on a rail with a series element in it.
                if (region.Section is { } section)
                {
                    fill.Color = (section == RailSection.Upstream
                                      ? theme.ClassTrace
                                      : theme.ClassSpreading).WithAlpha(SectionWashAlpha);
                    canvas.DrawPath(path, fill);
                }

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
                RailMarkerKind.Source     => theme.Source,
                RailMarkerKind.ViaFlag    => theme.ViaFlag,
                RailMarkerKind.Driven     => theme.Source,
                RailMarkerKind.MapExtreme => theme.LegendInk,
                _                         => theme.Load,
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
            else if (marker.Kind == RailMarkerKind.Driven)
            {
                // WHERE THE MAP IS MEASURED FROM — a disc inside a second, wider ring, so it reads
                // as "this one, of the ports you can already see" rather than as a fifth kind of
                // thing. The |Z| tab draws every declared port and until this one of them was the
                // origin of every number on the picture and looked like all the others.
                canvas.DrawCircle(x, y, MarkerRadiusPx, fill);
                canvas.DrawCircle(x, y, MarkerRadiusPx, ring);
                canvas.DrawCircle(x, y, MarkerRadiusPx * 2f, ring);
            }
            else if (marker.Kind == RailMarkerKind.MapExtreme)
            {
                // The ends of the ramp: a CROSS, which is a pointer at a place rather than a thing
                // on the board. A disc here would read as a port nobody declared.
                float r = MarkerRadiusPx * 1.4f;
                ring.Color = colour;
                canvas.DrawLine(x - r, y, x + r, y, ring);
                canvas.DrawLine(x, y - r, x, y + r, ring);
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
    /// <summary>
    /// The pick list's current net, outlined on the board — R-rail19-2's preview.
    /// </summary>
    /// <remarks>
    /// <b>Dashed, and in its own colour.</b> The copper tab draws a COMMITTED rail's islands as a
    /// solid 1.5 px stroke in <see cref="RailMapTheme.CopperHighlight"/>; this is the same geometry
    /// for a net nobody has committed, so it has to read differently at a glance or it says the
    /// opposite of what it means (R-rail19-2b).
    ///
    /// <para>A net the artwork gives no copper draws NOTHING — the caller learns there is none from
    /// the picture staying as it was, which is <see cref="DrawPartHighlight"/>'s own rule.</para>
    /// </remarks>
    private static void DrawNetPreview(
        SKCanvas canvas, RailNetPreview? preview, LayoutViewport vp, RailMapTheme theme)
    {
        if (preview is not { IsEmpty: false } net) return;

        using var dash = SKPathEffect.CreateDash([PreviewDashPx, PreviewDashPx], 0);
        using var stroke = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = PreviewStrokePx, Color = theme.NetPreview, PathEffect = dash,
        };

        foreach (var (_, paths) in net.Copper)
        {
            if (paths.Count == 0) continue;
            using var path = ToPath(paths, vp);
            canvas.DrawPath(path, stroke);
        }

        using var ink = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.NetPreview };
        using var font = Font(SkiaFonts.PlexSemiBold, LabelSizePx);
        float x = (float)vp.WorldToScreenX((net.Bounds.MinX + net.Bounds.MaxX) / 2);
        float y = (float)vp.WorldToScreenY(net.Bounds.MaxY);
        canvas.DrawText(net.Label, x, y - 4f, SKTextAlign.Center, font, ink);
    }

    /// <summary>The preview outline's stroke, device pixels — heavier than the copper tab's 1.5 px
    /// solid one, because it is drawn over a map rather than over bare copper.</summary>
    public const float PreviewStrokePx = 2f;

    /// <summary>Its dash period, device pixels.</summary>
    public const float PreviewDashPx = 4f;

    /// <summary>
    /// Every part the table says is not fitted, marked where it sits — <b>the land pattern is left
    /// exactly as it is and crossed</b>.
    /// </summary>
    /// <remarks>
    /// <b>A cross, dashed, and in a neutral colour</b>, which is three statements and each is load
    /// bearing. The CROSS is what a depopulated part is drawn as everywhere else in this trade and
    /// is legible at a glance over a colour ramp; DASHED separates it from the selection outline,
    /// which is solid and is the one mark the user asked for this frame; NEUTRAL keeps it from
    /// reading as an alarm, because a part deliberately left off a board is not a fault.
    ///
    /// <para><b>The label is the refdes and nothing else.</b> A sentence per part would be two dozen
    /// sentences over the copper on the shipped example alone; what the mark has to answer is
    /// <i>which</i> rows are off, and the table beside it says the rest.</para>
    ///
    /// <para><b>A part the board does not place draws nothing</b>, which is
    /// <see cref="RailPartHighlight.Outline"/>'s own rule — the caller learns there is no geometry
    /// from the picture staying as it was, never from a mark appearing at the origin.</para>
    /// </remarks>
    private static void DrawNotFitted(
        SKCanvas canvas, IReadOnlyList<RailPartHighlight>? parts, LayoutViewport vp, RailMapTheme theme)
    {
        if (parts is not { Count: > 0 }) return;

        using var dash = SKPathEffect.CreateDash([NotFittedDashPx, NotFittedDashPx], 0);
        using var stroke = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = NotFittedStrokePx, Color = theme.NotFitted, PathEffect = dash,
        };
        using var solid = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = NotFittedStrokePx, Color = theme.NotFitted,
        };
        using var ink = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.NotFitted };
        using var font = Font(SkiaFonts.PlexSemiBold, LabelSizePx);

        foreach (var part in parts)
        {
            var box = part.Outline;
            if (box.IsEmpty) continue;

            var rect = ScreenRect(box, vp);

            // The same floor the selection outline keeps, and for the same reason: an 0402 at fit
            // zoom is a few pixels, and a mark nobody can see has answered nothing.
            float padX = Math.Max(0f, MinHighlightPx - rect.Width) / 2f;
            float padY = Math.Max(0f, MinHighlightPx - rect.Height) / 2f;
            rect.Inflate(padX + HighlightInsetPx, padY + HighlightInsetPx);

            canvas.DrawRoundRect(rect, HighlightCornerPx, HighlightCornerPx, stroke);
            canvas.DrawLine(rect.Left, rect.Top, rect.Right, rect.Bottom, solid);
            canvas.DrawLine(rect.Left, rect.Bottom, rect.Right, rect.Top, solid);

            canvas.DrawText(part.Label, rect.MidX, rect.Top - 4f, SKTextAlign.Center, font, ink);
        }
    }

    /// <summary>The not-fitted mark's stroke, device pixels — lighter than the selection's, because
    /// it is ambient and there may be two dozen of them on one board.</summary>
    public const float NotFittedStrokePx = 1.5f;

    /// <summary>Its dash period, device pixels.</summary>
    public const float NotFittedDashPx = 3f;

    /// <summary>One world box as a screen rectangle, normalised — the y axis is flipped, so the
    /// corners cannot be assumed to come out in order.</summary>
    private static SKRect ScreenRect(Bbox box, LayoutViewport vp)
    {
        float x0 = (float)vp.WorldToScreenX(box.MinX), x1 = (float)vp.WorldToScreenX(box.MaxX);
        float y0 = (float)vp.WorldToScreenY(box.MinY), y1 = (float)vp.WorldToScreenY(box.MaxY);
        return new SKRect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
    }

    private static void DrawPartHighlight(
        SKCanvas canvas, RailPartHighlight? highlight, LayoutViewport vp, RailMapTheme theme)
    {
        if (highlight is not { } part) return;

        var box = part.Outline;
        if (box.IsEmpty) return;

        var rect = ScreenRect(box, vp);

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

        // ── NOTHING FITS AT THE FLOOR ⇒ NOTHING IS DRAWN (R-rail21-3b) ───────────────────────
        //
        // Decided BEFORE the plate, because a plate and a colour ramp with no numbers on either
        // end is not a scale — it is a coloured box sitting where the answer would be. That is the
        // "rendering fault" reading the floor exists to prevent, so the whole legend goes.
        var layout = LayOutLabels(legend, bar.Left, bar.Right, textBaseline,
                                  textBaseline - bar.Bottom);
        if (!layout.Drawn) return;

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

        using var font = Font(SkiaFonts.PlexRegular, layout.TextSizePx);
        using var ink = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.LegendInk };

        // The plate's two end labels are the SCENE's — volts on the drop tab, ohms on the |Z| tab.
        // A renderer that chose between them would be deciding what the numbers are, which is the
        // one thing R-rail8-13 says this file does not do.
        canvas.DrawText(legend.ColdLabel, barLeft, textBaseline, SKTextAlign.Left, font, ink);
        canvas.DrawText(legend.HotLabel, barRight, textBaseline, SKTextAlign.Right, font, ink);

        // The caption carries the model kind (§2.9 rule 1), so it is drawn wherever it can be drawn
        // READABLY — and dropped rather than shrunk where it cannot (R-rail21-3b). A caption at six
        // pixels names the model to nobody; what it does do is take the space the two end labels
        // need in order to stay at the floor.
        if (!layout.CaptionDrawn) return;

        using var caption = Font(SkiaFonts.PlexSemiBold, layout.TextSizePx);
        canvas.DrawText(legend.Caption, (barLeft + barRight) / 2, textBaseline, SKTextAlign.Center,
                        caption, ink);
    }

    /// <summary>
    /// The legend plate's screen geometry — <b>the one place it is computed</b>, so the drawing pass
    /// and anything asking where the labels land read the same rectangle.
    /// </summary>
    /// <param name="legend">The plate. Its <see cref="RailMapLegend.Box"/> is in DBU and supplies the
    /// ANCHOR — its top-left corner — and nothing else.</param>
    /// <param name="vp">World → screen.</param>
    /// <param name="plate">The whole plate, device pixels.</param>
    /// <param name="bar">The ramp's rectangle inside it.</param>
    /// <param name="baseline">Where the labels' baseline sits.</param>
    /// <returns>False where there is no room on the canvas for a readable plate, in which case it is
    /// not drawn at all.</returns>
    /// <remarks>
    /// <b>THE PLATE IS SCREEN-FIXED. It is chrome, like the marker callouts beside it</b> (owner,
    /// 2026-09-20). Its size was the world box mapped to screen, floored at a readable minimum —
    /// so it was constant while zoomed out and then grew without limit as the user zoomed in, and
    /// the two maps did it at different rates because they size their boxes off different content:
    /// <c>RailMapScene</c>'s |Z| pass unions a <c>MarkerReachDbu</c> square around every marker into
    /// its bounds and the drop pass does not, so the |Z| box is the larger and ran away first. That
    /// is what the report was — <i>the |Z| legend needs to zoom more like the Drop legend</i> —
    /// and equalising the two boxes would only have made them wrong together.
    ///
    /// <para><b>This does not reverse the 2026-09-19 decision that the TEXT tracks the plate</b>
    /// (see <see cref="LayOutLabels"/>, which still does). It removes that decision's premise. The
    /// argument there was that nothing about a plate which is <i>part of the picture</i> justifies
    /// its text being screen-fixed — true, and the answer is that the plate is not part of the
    /// picture. It names the colours; it is not one of them. The callouts on this same map have
    /// been screen-fixed by a deliberate decision since brief 18, so a legend that grows past them
    /// on the way in is the same map measuring itself two ways.</para>
    ///
    /// <para><b>The size is the minimum readable one, always</b> — the narrowest, shortest plate
    /// that holds this legend's own three strings at <see cref="LegendFloorPx"/>, which is
    /// <see cref="LabelSizePx"/>, which is what the callouts are drawn at. One notion of "readable
    /// on this map" rather than two that drift.</para>
    ///
    /// <para>Clamped to the viewport, so the answer to "there is no room" is eventually
    /// <see cref="LayOutLabels"/> drawing less rather than this drawing off-screen. The world box
    /// still frames in <c>RailMapScene.Bounds</c> and the drag still moves it in DBU, so Zoom to
    /// Fit keeps leaving room below the map and a plate the user has dragged stays where they put
    /// it.</para>
    /// </remarks>
    public static bool TryPlate(
        RailMapLegend legend, LayoutViewport vp, out SKRect plate, out SKRect bar, out float baseline)
    {
        ArgumentNullException.ThrowIfNull(legend);

        // THE ANCHOR, and the only thing read off the world box. Its top-left, because that is the
        // corner a drag moves and the corner the plate has always grown from.
        float x0 = (float)vp.WorldToScreenX(legend.Box.MinX);
        float y0 = (float)vp.WorldToScreenY(legend.Box.MaxY);

        plate = SKRect.Empty;
        bar = SKRect.Empty;
        baseline = 0;

        float h = MinPlateHeightPx;
        float w = MinPlateWidthPx(legend, h);

        if (vp.Width > 0)  w = Math.Min(w, (float)vp.Width);
        if (vp.Height > 0) h = Math.Min(h, (float)vp.Height);

        if (w < 2 || h < 2) return false;

        float left = x0, top = y0;
        if (vp.Width > 0 && left + w > vp.Width)   left = Math.Max(0f, (float)vp.Width - w);
        if (vp.Height > 0 && top + h > vp.Height)  top  = Math.Max(0f, (float)vp.Height - h);

        plate = new SKRect(left, top, left + w, top + h);

        float pad = h * PlatePadFraction;
        bar = new SKRect(left + pad, top + pad, left + w - pad, top + h * BarBottomFraction);
        baseline = top + h - pad;
        return true;
    }

    /// <summary>The plate's border inset, as a fraction of its height.</summary>
    private const float PlatePadFraction = 0.12f;

    /// <summary>Where the colour ramp stops, as a fraction of the plate's height.</summary>
    private const float BarBottomFraction = 0.5f;

    /// <summary>What is left for the labels, as a fraction of the plate's height — the ramp's
    /// bottom to the baseline. Derived from the two above so it cannot drift from them.</summary>
    private const float LabelBandFraction = 1f - PlatePadFraction - BarBottomFraction;

    /// <summary>The shortest plate whose label band is a floor-height line.</summary>
    public static float MinPlateHeightPx => LegendFloorPx / LabelBandFraction;

    /// <summary>
    /// The narrowest plate that holds all three labels at <see cref="LegendFloorPx"/> — the width
    /// <see cref="LayOutLabels"/> is about to ask for, computed from the same strings and the same
    /// spacing rather than from a second estimate of them.
    /// </summary>
    public static float MinPlateWidthPx(RailMapLegend legend, float plateHeightPx)
    {
        ArgumentNullException.ThrowIfNull(legend);

        using var regular  = Font(SkiaFonts.PlexRegular,  LegendFloorPx);
        using var semibold = Font(SkiaFonts.PlexSemiBold, LegendFloorPx);

        float perSide = Math.Max(regular.MeasureText(legend.ColdLabel),
                                 regular.MeasureText(legend.HotLabel))
                      + semibold.MeasureText(legend.Caption) / 2f
                      + LegendFloorPx * GapFraction;

        // Two pixels of slack, because the fit this has to satisfy measures the same strings at
        // LegendSizePx and scales down to the floor — a plate sized to the exact requirement lands
        // on the wrong side of it by rounding.
        return 2f * perSide + 2f * plateHeightPx * PlatePadFraction + 2f;
    }

    /// <summary>Clear space between neighbouring labels, as a fraction of the text size — labels
    /// that merely touch read as one word.</summary>
    private const float GapFraction = 0.6f;

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
    /// <para><b>WIDENING the box is not available</b> — it is world geometry and
    /// <see cref="RailMapScene.Bounds"/> frames it, so a renderer that widened the plate would draw
    /// outside what Zoom to Fit framed (§11.6 trap 4, which this legend's placement exists to
    /// satisfy); and widening it in the SCENE would make a world extent depend on a screen font
    /// size. So the text is fitted to the box.
    ///
    /// <para><b>But NOT below <see cref="LegendFloorPx"/>, which is R-rail21-3 and reverses half of
    /// what R-rail18-4 decided.</b> That brief shrank the three labels without a floor, on the
    /// grounds that small text is recoverable — the exports are vector and the window can be
    /// widened — while a missing model name is not. On a real board's pane that produced a caption
    /// of about six pixels and end labels barely a pixel tall: <i>"text is not readable in the
    /// scale, unless using heavy zoom"</i> (2026-09-20). An unreadable legend is strictly worse
    /// than no legend, because it occupies the space where the answer would go and reads as a
    /// rendering fault — and a six-pixel caption names the model to nobody, so what R-rail18-4
    /// protected was not actually being delivered.</para>
    ///
    /// <para>So, in order: draw all three at whatever size fits, never below the floor; where that
    /// cannot be done, DROP THE CAPTION and keep the two end labels at the floor — they are the
    /// scale, and a scale with no numbers is nothing; where even those will not fit, draw no legend
    /// at all. Never scaled below the floor at any step.</para>
    ///
    /// <para><b>And it scales without a CEILING</b>, which is the 2026-09-19 half and is unchanged:
    /// see the two constraints in the body. The floor and the ceiling are not in tension — the text
    /// tracks the plate upwards and stops tracking it downwards.</para>
    ///
    /// <param name="legend">The plate.</param>
    /// <param name="left">The bar's left edge, device pixels — where the cold label starts.</param>
    /// <param name="right">Its right edge — where the hot label ends.</param>
    /// <param name="baseline">The text baseline, device pixels.</param>
    /// <param name="bandHeightPx">How much of the plate is left under the ramp, device pixels — the
    /// second constraint on the size. Omitted, only the bar's width constrains it.</param>
    public static RailMapLabelLayout LayOutLabels(
        RailMapLegend legend, float left, float right, float baseline,
        float bandHeightPx = float.PositiveInfinity)
    {
        ArgumentNullException.ThrowIfNull(legend);

        using var regular = Font(SkiaFonts.PlexRegular, LegendSizePx);
        using var semibold = Font(SkiaFonts.PlexSemiBold, LegendSizePx);

        float wCold = regular.MeasureText(legend.ColdLabel);
        float wHot = regular.MeasureText(legend.HotLabel);
        float wCaption = semibold.MeasureText(legend.Caption);

        float available = Math.Max(0f, right - left);
        float centre = (left + right) / 2f;

        // Clear space between neighbouring labels, in the SAME units the widths are in, so it
        // scales with them — see GapFraction, which MinPlateWidthPx sizes the plate from.
        const float Gap = LegendSizePx * GapFraction;

        // Each side of the centred caption on its own. A total-width test is not enough — one long
        // end label and one short one fits by total and still runs into a centred caption.
        float perSide = Math.Max(wCold, wHot) + wCaption / 2f + Gap;

        // ── THE TEXT TRACKS THE PLATE, UPWARDS AS WELL AS DOWN (owner, 2026-09-19) ────────────
        //
        // This was `Math.Min(1f, …)`: the text shrank when the plate was too small for it and was
        // pinned at LegendSizePx otherwise. The plate is WORLD geometry, so zooming in grew the
        // border, the ramp and the gap between the labels while the labels themselves stopped at
        // 11 px — the further in, the smaller the legend read, and there was no zoom at which it
        // came back. Nothing about a plate that is part of the picture justifies its text being the
        // one thing in it that is screen-fixed.
        //
        // TWO CONSTRAINTS, AND THE SMALLER WINS. The first is the bar's width, as before. The
        // second is the LABEL BAND — what the plate has left under the ramp — without which a
        // legend whose three strings happen to be short ("0", "1", "") would size itself off a
        // width it cannot use and draw straight through the plate's own floor.
        float size = perSide > 0 ? LegendSizePx * (available / 2f / perSide) : LegendSizePx;
        if (bandHeightPx > 0 && size > bandHeightPx) size = bandHeightPx;

        // ── THE FLOOR, AND WHAT IS GIVEN UP TO STAY ABOVE IT (R-rail21-3a, R-rail21-3b) ───────
        //
        // Everything above this line is the fit; everything below is the refusal to go under the
        // floor. Note that the BAND is a hard ceiling in both branches — text taller than the strip
        // left under the ramp does not fit inside the plate at all, and a legend drawn over its own
        // border is the rendering fault again in a different spelling.
        // Half a pixel of slack, and then pinned AT the floor: TryPlate sizes the plate from these
        // same strings measured at the FLOOR while the fit above measures them at LegendSizePx and
        // scales, so a plate widened to fit exactly comes back a hundredth of a pixel short of its
        // own target. Without this, every legend the plate was widened for would drop its caption.
        if (size >= LegendFloorPx - 0.5f)
            return Labels(Math.Max(size, LegendFloorPx), left, right, centre, baseline,
                          wCold, wCaption, wHot, captionDrawn: true, drawn: true);

        // Half a pixel of slack: TryPlate derives the band from the plate's height by one route and
        // this compares it by another, and an exact-equality band was reported as 9.9999995 px.
        bool bandFits = !(bandHeightPx > 0) || bandHeightPx + 0.5f >= LegendFloorPx;

        // Without the caption the constraint is the two END labels and the gap between them, at the
        // floor. They ARE the scale; the caption is what the scale is of, and a reader looking at a
        // colour bar with two numbers on it has the part that cannot be reconstructed.
        float endsAtFloor = (wCold + wHot + Gap) * (LegendFloorPx / LegendSizePx);

        return bandFits && endsAtFloor <= available
            ? Labels(LegendFloorPx, left, right, centre, baseline,
                     wCold, wCaption, wHot, captionDrawn: false, drawn: true)
            : Labels(LegendFloorPx, left, right, centre, baseline,
                     wCold, wCaption, wHot, captionDrawn: false, drawn: false);
    }

    /// <summary>The three boxes at one size — the arithmetic <see cref="LayOutLabels"/> ends on,
    /// once for each of its three answers.</summary>
    private static RailMapLabelLayout Labels(
        float size, float left, float right, float centre, float baseline,
        float wCold, float wCaption, float wHot, bool captionDrawn, bool drawn)
    {
        float scale  = size / LegendSizePx;
        float ascent = size;                           // a conservative single-line box

        return new RailMapLabelLayout(
            size,
            new SKRect(left, baseline - ascent, left + wCold * scale, baseline),
            captionDrawn
                ? new SKRect(centre - wCaption * scale / 2f, baseline - ascent,
                             centre + wCaption * scale / 2f, baseline)
                : SKRect.Empty,
            new SKRect(right - wHot * scale, baseline - ascent, right, baseline),
            drawn,
            captionDrawn);
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
