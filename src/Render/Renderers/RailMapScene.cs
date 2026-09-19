// What railRF's board panel DRAWS, as geometry — one pure function of a DC result
// (docs/sonnet-briefs/brief-railrf-8-board-view.md R-rail8-9 … R-rail8-13; railrf.md §2.4, §2.9,
// §11.6, §11.7).
//
// ── WHY IT IS HERE AND NOT IN src/Ui ───────────────────────────────────────────────────────────
//
// R-rail8-13: the window and a headless report draw with the SAME code, which is the reason RND-1
// put this project below the firewall. The scene computes; RailMapRenderer paints it and computes
// nothing; the OVERLAY in src/Ui holds the pointer handling and calls into both.
//
// ── EVERYTHING IS IN WORLD UNITS, WHICH ARE THE LAYOUT'S DATABASE UNITS ────────────────────────
//
// R-rail8-3, and it is the overlay contract's own rule. The PDN model measures lengths in METRES
// (PdnProvenance.CellSizeMetres, PdnClassification.EquivalentLengthMetres) and coordinates in DBU,
// so metres→DBU is the one conversion this feature has. It is done HERE, once, in MetresToDbu, and
// nowhere else — the wBond finding this rule comes from is that the nm↔DBU bridge fails SILENTLY at
// the 1000 DBU/µm default, so anything testing it needs geometry off BOTH axes and off a round
// number.
//
// ── THE LEGEND IS WORLD GEOMETRY, NOT SCREEN CHROME, AND THAT IS THE POINT ─────────────────────
//
// §11.6 trap 4: "a drop map is co-extensive with the copper, so this looks harmless — until a
// legend, a source marker or a flagged-via callout sits outside the copper's own bbox and Zoom to
// Fit cuts it off." A legend drawn in screen space could not be framed at all, and §11.7 point 1
// frames a clipboard page from what is PAINTED. So the plate has a world box, it sits below the
// map, and Bounds is the union of everything.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Engine.Pdn;
using Clipper2Lib;

namespace CircuitRF.Render;

/// <summary>Which of §11.3's four tabs a scene was built for.</summary>
public enum RailMapKind
{
    /// <summary>The artwork as drawn, with this rail's own copper outlined and its islands numbered.
    /// <b>No map.</b> It is the only tab that shows a rail which is three regions joined by a 20 mil
    /// neck, which is why it draws anything at all.</summary>
    Copper,

    /// <summary>§2.4's headline: the artwork coloured by node voltage on a cold-to-hot scale.</summary>
    Drop,

    /// <summary>|Z| over frequency. <b>Empty here by design</b> — brief 15 fills it, and brief 8
    /// creates the tab so that is a fill rather than a re-layout.</summary>
    Impedance,

    /// <summary>§2.9 rule 2's trace-versus-mesh classification. A REQUIREMENT, not a diagnostic.</summary>
    Class,
}

/// <summary>One square of a map, in DBU.</summary>
/// <param name="Layer">The drawing layer its copper is on — the map is clipped per layer.</param>
/// <param name="CentreX">DBU.</param>
/// <param name="CentreY">DBU.</param>
/// <param name="HalfSpanDbu">Half the tile's side.</param>
/// <param name="Value">
/// What the map reads there, <b>in whatever the scene's own ramp is in</b> — volts on the drop map
/// and decibels relative to one ohm on the |Z| map.
///
/// <para>Not two fields and not a unit beside it, because <see cref="RailMapScene.Normalise"/> is
/// the only thing that reads this and it is a ratio: the tile's place on the ramp. What the number
/// MEANS is said once, in <see cref="RailMapLegend.ColdLabel"/> and
/// <see cref="RailMapLegend.HotLabel"/>, which is where a reader looks.</para>
///
/// <para>The |Z| map is in dB and that is not cosmetic: a PDN impedance spans four decades, so a
/// linear ramp over it colours all but the top decade the same and the map says nothing about the
/// place a designer is looking at.</para>
/// </param>
public readonly record struct RailMapTile(
    LayerKey Layer, long CentreX, long CentreY, long HalfSpanDbu, double Value);

/// <summary>
/// One piece of copper the scene draws as an area: a classification region, or an island of the
/// rail on the <c>copper</c> tab.
/// </summary>
/// <param name="Layer">Its drawing layer.</param>
/// <param name="Copper">Its geometry, DBU.</param>
/// <param name="Bounds">Its extent, DBU.</param>
/// <param name="Class">What the fast model does with it (<see cref="PdnCopperClass.Trace"/> on a
/// copper-tab island, which means nothing there and is not drawn).</param>
/// <param name="Forced">True where the user overrode the inference — drawn differently, because
/// §2.9 rule 2 is about seeing what you overrode.</param>
/// <param name="Region">Its identity, so a click can write an override keyed the same way the
/// document keys one.</param>
/// <param name="Readout">What the hover says. On a class region this is brief 4's own reason
/// string, verbatim — R-rail4-3 wrote it to be read here.</param>
public sealed record RailMapRegion(
    LayerKey Layer,
    Paths64 Copper,
    Bbox Bounds,
    PdnCopperClass Class,
    bool Forced,
    PdnRegionRef Region,
    string Readout);

/// <summary>What one marker on the map is.</summary>
public enum RailMarkerKind
{
    /// <summary>A source at its resolved pad.</summary>
    Source,

    /// <summary>A load that draws current.</summary>
    Load,

    /// <summary>A port that draws nothing. <b>Drawn, never omitted</b> — R-rail5-10's rule applied to
    /// the picture: <i>not added</i> and <i>added with no current</i> must not look the same.</summary>
    Observation,

    /// <summary>A layer transition whose worst via is over its limit (brief 6).</summary>
    ViaFlag,
}

/// <summary>A marker at a resolved coordinate, DBU.</summary>
/// <param name="Kind">Which of the four.</param>
/// <param name="X">DBU.</param>
/// <param name="Y">DBU.</param>
/// <param name="Label">The short text drawn beside the glyph — <c>U1.VDD</c>, never a node number.</param>
/// <param name="Readout">The sentence the hover gives, which is the report's own.</param>
public sealed record RailMapMarker(RailMarkerKind Kind, long X, long Y, string Label, string Readout);

/// <summary>
/// The scale plate — <b>inside the picture</b> (R-rail8-9), in world units (see this file's header).
/// </summary>
/// <param name="Box">Where it sits, DBU.</param>
/// <param name="ColdValue">The value at the cold end of the ramp, in the tiles' own units.</param>
/// <param name="HotValue">The value at the hot end.</param>
/// <param name="Caption">What the plate is titled — it carries the model kind, because §2.9 rule 1
/// says every result says which model produced it and a picture copied out of the window is a
/// result.</param>
/// <param name="ColdLabel">The cold end AS THE PLATE PRINTS IT — "3.6812 V", "1.2 mΩ".</param>
/// <param name="HotLabel">The hot end, same.</param>
/// <remarks>
/// <b>The two labels are formatted here and not by the renderer</b>, which is R-rail8-13's own rule
/// applied to text: the scene is the pure function of the result and the renderer paints it and
/// decides nothing. It became load-bearing when the |Z| map arrived — the plate reads volts on one
/// tab and ohms on another, and a renderer that chose between them would be deciding what the
/// numbers are.
/// </remarks>
public sealed record RailMapLegend(
    Bbox Box, double ColdValue, double HotValue, string Caption,
    string ColdLabel, string HotLabel);

/// <summary>
/// Everything one board tab draws, as a pure function of the result. Nothing here is an Avalonia or
/// a Skia type; <see cref="RailMapRenderer"/> turns it into paint.
/// </summary>
public sealed class RailMapScene
{
    /// <summary>Which tab this was built for.</summary>
    public required RailMapKind Kind { get; init; }

    /// <summary>The drop map's squares, empty on every other tab.</summary>
    public IReadOnlyList<RailMapTile> Tiles { get; init; } = [];

    /// <summary>The copper each layer's tiles are clipped to, so the shading stops at the artwork's
    /// own edge rather than at the sampling grid's.</summary>
    public IReadOnlyDictionary<LayerKey, Paths64> Clip { get; init; } =
        new Dictionary<LayerKey, Paths64>();

    /// <summary>The class tab's regions, or the copper tab's islands.</summary>
    public IReadOnlyList<RailMapRegion> Regions { get; init; } = [];

    /// <summary>Sources, loads, observation ports and flagged vias.</summary>
    public IReadOnlyList<RailMapMarker> Markers { get; init; } = [];

    /// <summary>The scale plate, or null on a tab with no scale.</summary>
    public RailMapLegend? Legend { get; init; }

    /// <summary>A sentence drawn centred when there is nothing else to draw — an empty |Z| tab says
    /// so rather than looking like a broken drop map.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// Everything this scene paints, unioned — <b>what Zoom to Fit must frame</b>.
    /// </summary>
    /// <remarks>
    /// Deliberately computed from the UNCULLED content: a fit computed from what is currently on
    /// screen would depend on the viewport it is about to replace, and would not converge.
    /// </remarks>
    public Bbox Bounds { get; init; } = Bbox.Empty;

    /// <summary>The highest voltage the map holds — the cold end of the ramp.</summary>
    public double ColdValue { get; init; }

    /// <summary>The lowest — the hot end.</summary>
    public double HotValue { get; init; }

    /// <summary>
    /// Where <paramref name="v"/> sits on the ramp, 0 (cold) … 1 (hot). A flat field reads 0
    /// everywhere rather than dividing by zero, which is the honest picture of a rail with no drop
    /// on it.
    /// </summary>
    /// <remarks>
    /// <b>The span may be NEGATIVE and that is not a guard against a bad scene</b>: on the drop map
    /// the cold end is the HIGHEST voltage — the source — and on the |Z| map it is the LOWEST
    /// impedance, because hot means "worst" on both and worst is the other direction. Only the
    /// zero-span case is special; it is the flat field, and it is not an error.
    /// </remarks>
    public double Normalise(double v)
    {
        double span = ColdValue - HotValue;
        return span == 0 || double.IsNaN(span) ? 0 : Math.Clamp((ColdValue - v) / span, 0, 1);
    }

    /// <summary>A scene that draws nothing — what every tab shows before a solve.</summary>
    public static RailMapScene Empty(RailMapKind kind, string? note = null) =>
        new() { Kind = kind, Note = note };

    // ── The one unit conversion this feature has (R-rail8-3) ───────────────────────────────────

    /// <summary>DBU per metre, at the artwork's own resolution.</summary>
    public static double DbuPerMetre(int dbuPerMicron) => dbuPerMicron * 1e6;

    /// <summary>Metres → DBU, rounded. <b>The only place this conversion happens.</b></summary>
    public static long MetresToDbu(double metres, int dbuPerMicron) =>
        (long)Math.Round(metres * DbuPerMetre(dbuPerMicron));

    /// <summary>DBU → metres, the inverse of <see cref="MetresToDbu"/>.</summary>
    public static double DbuToMetres(long dbu, int dbuPerMicron) => dbu / DbuPerMetre(dbuPerMicron);

    // ── Sampling ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How many samples the drop map takes across the map's longer side where it has to interpolate.
    /// </summary>
    /// <remarks>
    /// <b>Fixed, so the picture is a function of the result and not of the window size.</b> The
    /// clipboard copy and brief 17's documentation figures both depend on rendering the same result
    /// twice giving the same bytes, and a viewport-dependent grid would break both.
    ///
    /// <para><b>The cost is O(samples × nodes)</b>, because each sample goes through
    /// <see cref="RailDcResult.VoltageAt"/> — which is a linear scan, by design, since R-rail5-2 puts
    /// the interpolation rule in exactly one place so the window and the headless report cannot
    /// differ about it. That is cheap in the regime this branch actually runs in (the fast model's
    /// graph is a few hundred junctions), and <see cref="Build"/> never enters it for a fine mesh —
    /// see the branch there.</para>
    /// </remarks>
    public const int SamplesAcross = 96;

    /// <summary>
    /// Builds the scene for one tab.
    /// </summary>
    /// <param name="result">The rail's DC answer, or null before one exists.</param>
    /// <param name="kind">Which tab.</param>
    /// <param name="dbuPerMicron">The artwork's resolution — the only thing the metres↔DBU bridge
    /// needs.</param>
    /// <param name="plane">
    /// The plane pair's own answer (brief 15), or null where none has been computed.
    /// </param>
    /// <remarks>
    /// <b>The |Z| tab takes a SECOND result and that is not an inconsistency.</b> §4.1's shunt
    /// branch vanishes at ω = 0, so the DC run's netlist carries no cavity at all and the map is of
    /// a different extraction — at the frequency the map is at, meshed to λ/20 there. What the DC
    /// result still supplies is the copper to clip to and the markers to draw, which are the same
    /// artwork on every tab (R-rail8-12).
    /// </remarks>
    public static RailMapScene Build(
        RailDcResult? result, RailMapKind kind, int dbuPerMicron, PdnPlaneAnswer? plane = null)
    {
        if (kind == RailMapKind.Impedance)
            return BuildImpedance(result, plane, dbuPerMicron);

        if (result is null) return Empty(kind, "No result yet. Run the rail.");

        return kind switch
        {
            RailMapKind.Copper => BuildCopper(result),
            RailMapKind.Class  => BuildClass(result, dbuPerMicron),
            _                  => BuildDrop(result, dbuPerMicron),
        };
    }

    // ── copper: the rail's own islands, and nothing else ───────────────────────────────────────

    private static RailMapScene BuildCopper(RailDcResult result)
    {
        if (result.Regions is not { } set || set.Power.Count == 0)
            return Empty(RailMapKind.Copper,
                "This rail's extraction reported no copper of its own to outline.");

        var regions = new List<RailMapRegion>();
        var bounds = Bbox.Empty;

        foreach (var island in set.Power)
            foreach (var (layer, paths) in island.Copper)
            {
                if (paths.Count == 0) continue;
                var bb = BoundsOf(paths);
                bounds = bounds.Union(bb);
                regions.Add(new RailMapRegion(
                    layer, paths, bb, PdnCopperClass.Trace, Forced: false,
                    PdnCopperClassifier.RefOf(layer, paths),
                    $"Island {island.Index} of rail '{result.RailName}' on layer " +
                    $"{layer.Layer}/{layer.Datatype}. {set.IslandReport}"));
            }

        return new RailMapScene
        {
            Kind    = RailMapKind.Copper,
            Regions = regions,
            Bounds  = bounds,
        };
    }

    // ── class: §2.9 rule 2, drawn ──────────────────────────────────────────────────────────────

    private static RailMapScene BuildClass(RailDcResult result, int dbuPerMicron)
    {
        if (result.Classification.Count == 0)
            return Empty(RailMapKind.Class,
                "The accurate reading meshes every piece of copper, so it classifies none. Switch to " +
                "the fast model to see which copper the closed form priced.");

        var regions = new List<RailMapRegion>(result.Classification.Count);
        var bounds = Bbox.Empty;

        foreach (var c in result.Classification)
        {
            bounds = bounds.Union(c.Bounds);
            regions.Add(new RailMapRegion(
                c.Region.Layer, c.Copper, c.Bounds, c.Class, c.Forced, c.Region,
                (c.IsReference ? "Reference conductor. " : "") + c.Reason));
        }

        return new RailMapScene
        {
            Kind    = RailMapKind.Class,
            Regions = regions,
            Markers = MarkersOf(result),
            Bounds  = bounds.Union(MarkerBounds(result, dbuPerMicron)),
        };
    }

    // ── drop: the headline, and it is a picture ────────────────────────────────────────────────

    private static RailMapScene BuildDrop(RailDcResult result, int dbuPerMicron)
    {
        // Every cell of the rail's OWN copper (never the reference's — the map colours the rail),
        // grouped by drawing layer, each carrying the voltage the solve produced for its node.
        var byLayer = new Dictionary<LayerKey, List<(long X, long Y, double V)>>();
        foreach (var (node, cell) in result.Netlist.NodeCells)
        {
            if (cell.IsReference) continue;
            if (!result.NodeVoltages.TryGetValue(node, out double v)) continue;
            if (!byLayer.TryGetValue(cell.Layer, out var list)) byLayer[cell.Layer] = list = [];
            list.Add((cell.CentreX, cell.CentreY, v));
        }

        if (byLayer.Count == 0)
            return Empty(RailMapKind.Drop,
                "This rail's extraction bound no cell on its own copper, so there is no field to " +
                "colour.");

        var clip = ClipPaths(result);
        var mapBounds = Bbox.Empty;
        foreach (var list in byLayer.Values)
            foreach (var (x, y, _) in list)
                mapBounds = mapBounds.Union(new Bbox(x, y, x, y));
        foreach (var paths in clip.Values) mapBounds = mapBounds.Union(BoundsOf(paths));

        long step = GridStep(mapBounds);
        long cellPitch = MetresToDbu(result.Netlist.Provenance.CellSizeMetres, dbuPerMicron);

        var tiles = new List<RailMapTile>();
        foreach (var (layer, cells) in byLayer)
        {
            var layerBounds = clip.TryGetValue(layer, out var paths) && paths.Count > 0
                ? BoundsOf(paths)
                : BoundsOfPoints(cells);
            if (layerBounds.IsEmpty) continue;

            // THE ONE BRANCH, and it is not two arithmetics (R-rail5-2). VoltageAt's own contract is
            // that an exact hit on a cell centre answers THAT CELL'S value — so where the extraction's
            // cells are already at least as dense as the sampling grid, tiling them IS the sampled
            // answer, read straight off the field instead of rediscovered by a linear scan per sample.
            // The accurate reading's mesh is always in that regime; the fast reading's junctions are
            // irregular and sparse, and are interpolated.
            long gx = Math.Max(1, (layerBounds.MaxX - layerBounds.MinX) / step + 1);
            long gy = Math.Max(1, (layerBounds.MaxY - layerBounds.MinY) / step + 1);

            if (cellPitch > 0 && cells.Count >= gx * gy)
            {
                long half = Math.Max(1, cellPitch / 2);
                foreach (var (x, y, v) in cells) tiles.Add(new RailMapTile(layer, x, y, half, v));
            }
            else
            {
                long half = Math.Max(1, step / 2);
                for (long y = layerBounds.MinY + half; y <= layerBounds.MaxY + half; y += step)
                    for (long x = layerBounds.MinX + half; x <= layerBounds.MaxX + half; x += step)
                        if (result.VoltageAt(layer, isReference: false, x, y) is { } v)
                            tiles.Add(new RailMapTile(layer, x, y, half, v));
            }
        }

        double cold = double.NegativeInfinity, hot = double.PositiveInfinity;
        foreach (var t in tiles)
        {
            if (t.Value > cold) cold = t.Value;
            if (t.Value < hot) hot = t.Value;
        }
        if (tiles.Count == 0) { cold = 0; hot = 0; }

        var markers = MarkersOf(result);
        var bounds = mapBounds.Union(MarkerBounds(result, dbuPerMicron));
        var legend = LegendFor(
            bounds, cold, hot,
            $"{result.RailName} · {ModelName(result.Netlist.Provenance.ModelKind)}", Volts);

        return new RailMapScene
        {
            Kind       = RailMapKind.Drop,
            Tiles      = tiles,
            Clip       = clip,
            Markers    = markers,
            Legend     = legend,
            ColdValue  = cold,
            HotValue   = hot,
            Bounds     = legend is null ? bounds : bounds.Union(legend.Box),
        };
    }

    // ── |Z|: §2.4's other picture, and the tab brief 8 left empty ──────────────────────────────

    /// <summary>
    /// §2.4's <i>"|Z| across the whole plane at a chosen frequency"</i>, over the same artwork and
    /// through the same painter as the drop map.
    /// </summary>
    /// <remarks>
    /// <b>The ramp is logarithmic and the drop map's is not</b> — see <see cref="RailMapTile.Value"/>.
    /// A PDN impedance field routinely spans four decades between the driven port and a resonance,
    /// and a linear ramp over that colours everything but the top decade the same.
    ///
    /// <para>The MARKERS are what make this picture answer the question §2.4 asks. "A mode whose
    /// maximum sits on the load pin field is a problem; the same mode with its maximum in a corner
    /// is not" — a map with no load callout on it cannot distinguish those, so the callouts are on
    /// the |Z| tab for the same reason they are on the drop tab and not as decoration.</para>
    /// </remarks>
    private static RailMapScene BuildImpedance(
        RailDcResult? result, PdnPlaneAnswer? plane, int dbuPerMicron)
    {
        if (plane is null)
            return Empty(RailMapKind.Impedance,
                "No |Z| map yet. It is the plane pair's own answer and it is a separate run — " +
                "find the plane resonances, at the frequency you want the map at.");

        if (plane.Refusal is { } refusal)
            return Empty(RailMapKind.Impedance, refusal);

        if (plane.ImpedanceMap.Count == 0)
            return Empty(RailMapKind.Impedance,
                "This plane pair has modes and no impedance map: nothing drove one. " +
                string.Join(" ", plane.Notes));

        long half = Math.Max(1, MetresToDbu(plane.CellSizeMetres, dbuPerMicron) / 2);

        var tiles = new List<RailMapTile>(plane.ImpedanceMap.Count);
        var mapBounds = Bbox.Empty;
        double cold = double.PositiveInfinity, hot = double.NegativeInfinity;

        foreach (var cell in plane.ImpedanceMap)
        {
            // A cell reading zero or a non-finite ohm has no place on a logarithmic ramp, and it is
            // dropped rather than clamped: a tile at an invented value is a tile a reader believes.
            double db = DecibelOhms(cell.OhmsMagnitude);
            if (!double.IsFinite(db)) continue;

            tiles.Add(new RailMapTile(cell.Cell.Layer, cell.Cell.CentreX, cell.Cell.CentreY, half, db));
            mapBounds = mapBounds.Union(
                new Bbox(cell.Cell.CentreX - half, cell.Cell.CentreY - half,
                         cell.Cell.CentreX + half, cell.Cell.CentreY + half));

            if (db < cold) cold = db;
            if (db > hot) hot = db;
        }

        if (tiles.Count == 0)
            return Empty(RailMapKind.Impedance,
                "Every cell of this impedance map read zero ohms, which is not a field. " +
                string.Join(" ", plane.Notes));

        // COLD is the LOWEST impedance here and the HIGHEST voltage on the drop map, because hot
        // means "worst" on both — see Normalise.
        var clip = result is null ? [] : ClipPaths(result);
        foreach (var paths in clip.Values) mapBounds = mapBounds.Union(BoundsOf(paths));

        var markers = result is null ? [] : MarkersOf(result);
        var bounds = result is null ? mapBounds : mapBounds.Union(MarkerBounds(result, dbuPerMicron));

        string rail = result?.RailName ?? "";
        string model = result is null ? "" : " · " + ModelName(result.Netlist.Provenance.ModelKind);
        string driven = plane.MapPortName.Length > 0 ? $" from {plane.MapPortName}" : "";

        var legend = LegendFor(
            bounds, cold, hot,
            $"{rail}{model} · |Z| at {PdnMask.Hertz(plane.MapFrequencyHz)}{driven}",
            db => Ohms(OhmsFromDecibels(db)));

        return new RailMapScene
        {
            Kind      = RailMapKind.Impedance,
            Tiles     = tiles,
            Clip      = clip,
            Markers   = markers,
            Legend    = legend,
            ColdValue = cold,
            HotValue  = hot,
            Bounds    = legend is null ? bounds : bounds.Union(legend.Box),
        };
    }

    /// <summary>The rail's copper, per drawing layer — what the shading is clipped to.</summary>
    private static Dictionary<LayerKey, Paths64> ClipPaths(RailDcResult result)
    {
        var clip = new Dictionary<LayerKey, Paths64>();
        if (result.Regions is not { } set) return clip;

        foreach (var island in set.Power)
            foreach (var (layer, paths) in island.Copper)
            {
                if (paths.Count == 0) continue;
                if (!clip.TryGetValue(layer, out var merged)) clip[layer] = merged = [];
                foreach (var p in paths) merged.Add(p);
            }

        return clip;
    }

    /// <summary>
    /// The plate, placed BELOW the map in world units and sized from the map's own extent.
    /// </summary>
    /// <remarks>
    /// Sized as a fraction of the content rather than in millimetres so the same arithmetic frames a
    /// 10 mm module and a 200 mm backplane, and so <see cref="Bounds"/> is scale-free. Below rather
    /// than inside, because a plate laid over the copper would hide the part of the map a reader is
    /// most likely to be looking at — and §11.6 trap 4 is precisely about content that sits outside
    /// the copper's own bbox being framed rather than cut off.
    /// </remarks>
    private static RailMapLegend? LegendFor(
        Bbox content, double cold, double hot, string caption,
        Func<double, string> label)
    {
        if (content.IsEmpty) return null;

        long w = Math.Max(1, content.MaxX - content.MinX);
        long h = Math.Max(1, content.MaxY - content.MinY);

        // Sized off the LONGER side, not off each axis. A supply trace is routinely 30 mm long and
        // 0.4 mm wide, and a plate 7.5 % of 0.4 mm tall is 30 µm — unreadable, and small enough to sit
        // inside the margin Zoom to Fit already leaves for an extreme aspect ratio, which would make
        // §11.6 trap 4 undetectable on exactly the geometry it is most likely to bite.
        long span = Math.Max(w, h);
        long plateW = Math.Max(1, (long)(w * LegendWidthFraction));
        long plateH = Math.Max(1, (long)(span * LegendHeightFraction));
        long gap = Math.Max(1, (long)(span * LegendGapFraction));

        var box = new Bbox(
            content.MinX,
            content.MinY - gap - plateH,
            content.MinX + plateW,
            content.MinY - gap);

        return new RailMapLegend(box, cold, hot, caption, label(cold), label(hot));
    }

    /// <summary>What the plate calls the model that produced a result (§2.9 rule 1).</summary>
    private static string ModelName(PdnModelKind kind) =>
        kind == PdnModelKind.Accurate ? "Accurate model" : "Fast model";

    /// <summary>Volts, as the drop map's plate and the board readout both print them.</summary>
    public static string Volts(double v) =>
        Math.Abs(v) >= 1.0 ? $"{v:0.####} V" : $"{v * 1e3:0.###} mV";

    /// <summary>
    /// Ohms over the six decades a PDN map actually covers, as the |Z| plate prints them.
    /// </summary>
    /// <remarks>
    /// <b>Engineering prefixes rather than a fixed unit</b>: the same map routinely holds 800 µΩ at
    /// the driven port and 40 Ω on a resonance, and a plate reading "0.0008" at one end says nothing
    /// a reader can use.
    /// </remarks>
    public static string Ohms(double ohms) =>
        !double.IsFinite(ohms) ? "—"
        : Math.Abs(ohms) >= 1e3 ? $"{ohms / 1e3:0.###} kΩ"
        : Math.Abs(ohms) >= 1.0 ? $"{ohms:0.###} Ω"
        : Math.Abs(ohms) >= 1e-3 ? $"{ohms * 1e3:0.###} mΩ"
        : $"{ohms * 1e6:0.###} µΩ";

    /// <summary>
    /// Decibels relative to one ohm, which is what an impedance TILE carries — see
    /// <see cref="RailMapTile.Value"/> for why the |Z| ramp is logarithmic and the drop ramp is not.
    /// </summary>
    public static double DecibelOhms(double ohms) =>
        ohms > 0 ? 20.0 * Math.Log10(ohms) : double.NegativeInfinity;

    /// <summary>The inverse, for a readout that has a tile and wants the ohms back.</summary>
    public static double OhmsFromDecibels(double db) => Math.Pow(10.0, db / 20.0);

    /// <summary>The plate's width, as a fraction of the map's own.</summary>
    public const double LegendWidthFraction = 0.40;

    /// <summary>Its height, as a fraction of the map's LONGER side — see <see cref="LegendFor"/>.</summary>
    public const double LegendHeightFraction = 0.075;

    /// <summary>The gap between the map's lower edge and the plate, on the same basis.</summary>
    public const double LegendGapFraction = 0.03;

    // ── markers ────────────────────────────────────────────────────────────────────────────────

    private static List<RailMapMarker> MarkersOf(RailDcResult result)
    {
        var markers = new List<RailMapMarker>();

        // Sources: the netlist's own source branches carry the cell they were stamped at, which is
        // the pad the anchor resolved to. The SHARE comes from the result's own source list, matched
        // by refdes — a number the geometry decided (§2.2) and the one worth reading on the board.
        foreach (var origin in result.Netlist.Origins)
        {
            if (origin.Kind != PdnOriginKind.SourceBranch) continue;
            if ((origin.From ?? origin.To) is not { } cell) continue;

            var share = result.Sources.FirstOrDefault(
                s => origin.Refdes is { Length: > 0 } r &&
                     s.Name.StartsWith(r, StringComparison.OrdinalIgnoreCase));

            string label = origin.Refdes ?? share?.Name ?? "source";
            string readout = share is null
                ? origin.Description
                : $"{share.Name}: delivering {share.CurrentA * 1e3:0.###} mA, " +
                  $"{share.ShareOfTotal:P0} of this rail's total" +
                  (share.OpenCircuitVoltageV is { } v ? $", from {v:0.####} V open circuit" : "");

            markers.Add(new RailMapMarker(RailMarkerKind.Source, cell.CentreX, cell.CentreY, label, readout));
        }

        // Loads and observation ports, from the port bindings — every one of them, including the ones
        // that draw nothing (R-rail5-10).
        foreach (var port in result.Netlist.Ports)
        {
            var cell = FirstPowerCell(port.Cells);
            if (cell is not { } c) continue;

            var drop = result.Ports.FirstOrDefault(p => p.Index == port.Index);
            markers.Add(new RailMapMarker(
                port.DcCurrentA is null ? RailMarkerKind.Observation : RailMarkerKind.Load,
                c.CentreX, c.CentreY, port.Name, drop?.Describe() ?? port.Name));
        }

        // Flagged via transitions (brief 6) — on the WORST via of the field, which is what the flag
        // is on. Never the mean, and never the field's centroid: the one nearest the load routinely
        // carries several times its share, and pointing at the middle of the group would point at
        // copper that is fine.
        foreach (var flag in result.ViaCheck.Flags)
        {
            if (flag.Transition.Worst is not { } worst) continue;
            markers.Add(new RailMapMarker(
                RailMarkerKind.ViaFlag, worst.Barrel.X, worst.Barrel.Y,
                $"{flag.Transition.Count} vias", flag.Describe()));
        }

        return markers;
    }

    private static PdnCellRef? FirstPowerCell(IReadOnlyList<PdnCellRef> cells)
    {
        foreach (var c in cells) if (!c.IsReference) return c;
        return cells.Count > 0 ? cells[0] : null;
    }

    /// <summary>
    /// Where the markers reach — <b>separately from the map</b>, because that is the whole of §11.6
    /// trap 4: a callout outside the copper's own bbox is exactly what Zoom to Fit cuts off.
    /// </summary>
    private static Bbox MarkerBounds(RailDcResult result, int dbuPerMicron)
    {
        var bb = Bbox.Empty;
        foreach (var m in MarkersOf(result))
        {
            long r = MarkerReachDbu(dbuPerMicron);
            bb = bb.Union(new Bbox(m.X - r, m.Y - r, m.X + r, m.Y + r));
        }
        return bb;
    }

    /// <summary>
    /// How far a marker's glyph and label reach from its own coordinate, in DBU — <b>half a
    /// millimetre</b>, which is the one place in this file a physical size appears.
    /// </summary>
    /// <remarks>
    /// A marker is drawn at a fixed SCREEN size, so its world reach depends on the zoom and there is
    /// no exact answer. This is the allowance <see cref="Bounds"/> makes for it, and it is a real
    /// length rather than a fraction of the board because a callout on a 200 mm backplane is the same
    /// size on screen as one on a 10 mm module.
    /// </remarks>
    public static long MarkerReachDbu(int dbuPerMicron) => MetresToDbu(0.5e-3, dbuPerMicron);

    // ── small geometry helpers ─────────────────────────────────────────────────────────────────

    private static long GridStep(Bbox bounds)
    {
        long span = Math.Max(bounds.MaxX - bounds.MinX, bounds.MaxY - bounds.MinY);
        return Math.Max(1, span / SamplesAcross);
    }

    private static Bbox BoundsOf(Paths64 paths)
    {
        var bb = Bbox.Empty;
        foreach (var ring in paths)
            foreach (var pt in ring)
                bb = bb.Union(new Bbox(pt.X, pt.Y, pt.X, pt.Y));
        return bb;
    }

    private static Bbox BoundsOfPoints(List<(long X, long Y, double V)> cells)
    {
        var bb = Bbox.Empty;
        foreach (var (x, y, _) in cells) bb = bb.Union(new Bbox(x, y, x, y));
        return bb;
    }
}
