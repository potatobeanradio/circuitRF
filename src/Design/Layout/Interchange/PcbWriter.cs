// Board-format writer — tokens out, and nothing else. Like PcbReader (and like DxfWriter/GdsiiWriter
// relative to their own orchestrators) this file touches no CellFolder, no Messages and no editor
// state: PcbExport does the hierarchy walk and hands it a finished model.
//
// ── One epoch, deliberately, and it is NOT the newest ───────────────────────────────────────────────
//
// PcbReader must never branch on the version stamp, because files of every epoch are in circulation and
// arrive unbidden. A WRITER has the opposite problem: it picks one dialect and every reader downstream
// must accept it. This writes 20221018 (PcbLayerNaming.TargetVersion), which is
//   * late enough to be free of design rules and net classes — those left the board file at the
//     20211014 epoch, measured across four real files (src/Ui/RESOLVED.md), so nothing here has to
//     invent routing constraints circuitRF has no equivalent for; and
//   * early enough that every later release still opens it, whereas emitting the newest stamp would
//     exclude every older reader for no gain.
// Concretely that means: (stroke (width W)) not (width W), three-point (mid …) arcs not centre+angle,
// (fill yes|no), an ORDINAL net table, and the 0/31 copper ordinals — not the renumbered ones.
//
// ── What is deliberately NOT written ────────────────────────────────────────────────────────────────
//
// No design rules and no net classes. Not because circuitRF lacks rules — it has a full DrcRule model —
// but because the two models disagree in KIND: ours measures per-layer/region process geometry
// (MinWidth, MinSpacing, MinEnclosure, MinNotch, MinArea, Density over boolean-derived regions), while
// this format's are per-NET-CLASS routing constraints (clearance, trace width, via diameter) living in
// a sibling project file. Only MinWidth and MinSpacing have any counterpart at all, and circuitRF has
// no net-class concept to attach them to — so every rule would collapse onto one synthesised "Default"
// class that looks authoritative and is wrong for every net but the narrowest. What the technology
// holds and this format cannot carry is REPORTED instead.
//
// Also not written: pcbplotparams (plot/Gerber output configuration, all defaults), aux_axis_origin,
// and the tenting/covering flags. All optional; all would be invented.

using System.Globalization;
using System.Text;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>One footprint DEFINITION: cell-local artwork plus the pins that become its pads.</summary>
public sealed record PcbFootprintDef(string Name, IReadOnlyList<LayoutShape> Shapes, IReadOnlyList<LayoutPin> Pins);

/// <summary>One placement of a <see cref="PcbFootprintDef"/>.</summary>
public sealed record PcbFootprintPlacement(
    string DefName, long X, long Y, double RotationDegrees, bool MirrorX, string? Reference);

/// <summary>Everything <c>PcbWriter</c> needs, already resolved. Built by <c>PcbExport</c>.</summary>
public sealed class PcbExportModel
{
    public List<LayoutShape> BoardShapes { get; } = [];
    public Dictionary<string, PcbFootprintDef> Definitions { get; } = [];
    public List<PcbFootprintPlacement> Placements { get; } = [];
    public Technology? Tech { get; set; }
    public int DbuPerMicron { get; set; } = LayoutUnits.DefaultDbuPerMicron;
    public string BoardTitle { get; set; } = "";
}

/// <summary>Counters, and everything the write could not carry at full fidelity.</summary>
public sealed record PcbWriteSummary(
    int Segments, int Arcs, int Vias, int Zones, int Graphics, int Texts,
    int Footprints, int PadsFromPins, int PinsWithNoArtwork,
    int CubicsFlattened, int BitmapsSkipped, int HolesKeyholed, int UnnamedDrills,
    IReadOnlyList<string> UnmappedLayerNames,
    IReadOnlyList<string> Notes);

public static class PcbWriter
{
    /// <summary>Coordinates are decimal millimetres with NO exponent notation (§2). At the 1000 DBU/µm
    /// default one DBU is one nanometre, so six decimal places represent every DBU exactly and this
    /// format is lossless in both directions — the one interchange path here that is.</summary>
    private const string MmFormat = "0.######";

    public static PcbWriteSummary Write(TextWriter w, PcbExportModel model)
    {
        var layers = PcbLayerNaming.Assign(model.Tech);
        double dbuPerMm = model.DbuPerMicron * 1000.0;
        var ctx = new Ctx(w, layers, dbuPerMm);

        w.Write("(kicad_pcb (version ");
        w.Write(PcbLayerNaming.TargetVersion);
        w.WriteLine(") (generator circuitrf)");
        w.WriteLine();

        WriteGeneral(ctx, model);
        WriteLayerTable(ctx, layers);
        WriteSetup(ctx, model);
        var netOrdinals = WriteNetTable(ctx, model);
        ctx.NetOrdinals = netOrdinals;

        foreach (var placement in model.Placements)
            if (model.Definitions.TryGetValue(placement.DefName, out var def))
                WriteFootprint(ctx, def, placement);

        foreach (var shape in model.BoardShapes)
            WriteBoardShape(ctx, shape);

        w.WriteLine(")");

        if (model.Tech is { } tech)
        {
            int rules = tech.DrcRules.Count;
            if (rules > 0)
                ctx.Notes.Add(
                    $"This technology's {rules} DRC rule(s) were NOT written. This format's design rules are " +
                    "per-net-class routing constraints in a sibling project file, not per-layer process " +
                    "geometry — only minimum width and minimum spacing have any counterpart, and circuitRF " +
                    "has no net classes to attach them to. Set the rules you need on the receiving side.");
        }
        if (ctx.Zones > 0)
            ctx.Notes.Add(
                $"{ctx.Zones} copper region(s) were written as filled zones so they carry their net. " +
                "Refilling zones in the receiving tool re-derives their shape from the outline and its " +
                "clearances, which will differ from what circuitRF drew.");

        return new PcbWriteSummary(
            ctx.Segments, ctx.Arcs, ctx.Vias, ctx.Zones, ctx.Graphics, ctx.Texts,
            ctx.Footprints, ctx.PadsFromPins, ctx.PinsWithNoArtwork,
            ctx.CubicsFlattened, ctx.BitmapsSkipped, ctx.HolesKeyholed, ctx.UnnamedDrills,
            layers.UnmappedLayerNames, ctx.Notes);
    }

    // ── Context ─────────────────────────────────────────────────────────────────────────────────

    private sealed class Ctx(TextWriter w, PcbLayerNaming.Result layers, double dbuPerMm)
    {
        public TextWriter W { get; } = w;
        public PcbLayerNaming.Result Layers { get; } = layers;
        public double DbuPerMm { get; } = dbuPerMm;
        public IReadOnlyDictionary<string, int> NetOrdinals { get; set; } = new Dictionary<string, int>();
        public List<string> Notes { get; } = [];

        public int Segments, Arcs, Vias, Zones, Graphics, Texts;
        public int Footprints, PadsFromPins, PinsWithNoArtwork;
        public int CubicsFlattened, BitmapsSkipped, HolesKeyholed, UnnamedDrills;

        /// <summary>DBU → millimetres. A length, so no sign flip.</summary>
        public string Mm(long dbu) => (dbu / DbuPerMm).ToString(MmFormat, CultureInfo.InvariantCulture);

        /// <summary>DBU → millimetres for a Y coordinate. <b>The one and only sign flip on the way
        /// out</b>, mirroring <see cref="PcbUnits.Y"/> on the way in.</summary>
        public string My(long dbu) => (-dbu / DbuPerMm).ToString(MmFormat, CultureInfo.InvariantCulture);

        public string Deg(double d) => d.ToString(MmFormat, CultureInfo.InvariantCulture);

        public string? LayerName(LayerKey key)
            => Layers.RowByKey.TryGetValue(key, out var row) ? row.Name : PcbLayerNaming.FallbackName;

        public bool IsCopper(LayerKey key)
            => Layers.RowByKey.TryGetValue(key, out var row) && row.IsCopper;

        public int NetOf(string? net)
            => net is { Length: > 0 } n && NetOrdinals.TryGetValue(n, out int ordinal) ? ordinal : 0;
    }

    // ── Header sections ─────────────────────────────────────────────────────────────────────────

    private static void WriteGeneral(Ctx ctx, PcbExportModel model)
    {
        // The board's overall thickness is the stackup's own sum when it has one — never invented.
        long total = 0;
        foreach (var layer in model.Tech?.Stackup.Layers ?? [])
            if (layer.Kind is StackupKind.Conductor or StackupKind.Dielectric) total += layer.ThicknessDbu;

        ctx.W.WriteLine("  (general");
        if (total > 0) ctx.W.WriteLine($"    (thickness {ctx.Mm(total)})");
        ctx.W.WriteLine("  )");
        ctx.W.WriteLine();
        ctx.W.WriteLine("  (paper \"A4\")");
        if (model.BoardTitle.Length > 0)
            ctx.W.WriteLine($"  (title_block (title {Quote(model.BoardTitle)}))");
        ctx.W.WriteLine();
    }

    private static void WriteLayerTable(Ctx ctx, PcbLayerNaming.Result layers)
    {
        ctx.W.WriteLine("  (layers");
        foreach (var row in layers.Table)
            ctx.W.WriteLine($"    ({row.Ordinal} {Quote(row.Name)} {row.Type})");

        // Edge.Cuts is always declared even when nothing is drawn on it: a board file with no outline
        // layer at all reads as malformed to some importers, and declaring an empty layer costs one row.
        if (!layers.Table.Any(r => r.Name == "Edge.Cuts"))
            ctx.W.WriteLine("    (44 \"Edge.Cuts\" user)");
        ctx.W.WriteLine("  )");
        ctx.W.WriteLine();
    }

    /// <summary>The stackup, and ONLY the stackup — see this file's header for why no rules are
    /// written. Omitted entirely when the technology has none, rather than fabricated (the export-side
    /// counterpart of R-L4d-6).</summary>
    private static void WriteSetup(Ctx ctx, PcbExportModel model)
    {
        var stack = model.Tech?.Stackup.Layers;
        if (stack is null || stack.Count == 0) return;

        ctx.W.WriteLine("  (setup");
        ctx.W.WriteLine("    (stackup");
        int dielectric = 0;
        foreach (var layer in stack)
        {
            switch (layer.Kind)
            {
                case StackupKind.Conductor:
                {
                    // Named by the layer table's own copper name where one is bound, so a round trip
                    // through PcbReader re-binds the stackup entry to the same artwork.
                    string name = layer.DrawingLayers.Count > 0 && ctx.LayerName(layer.DrawingLayers[0]) is { } n
                        ? n : layer.Name;
                    ctx.W.WriteLine($"      (layer {Quote(name)} (type \"copper\") (thickness {ctx.Mm(layer.ThicknessDbu)}))");
                    break;
                }
                case StackupKind.Dielectric:
                {
                    dielectric++;
                    var sb = new StringBuilder();
                    sb.Append($"      (layer \"dielectric {dielectric}\" (type \"core\") (thickness {ctx.Mm(layer.ThicknessDbu)})");
                    sb.Append($" (epsilon_r {layer.Epsr.ToString(MmFormat, CultureInfo.InvariantCulture)})");
                    sb.Append($" (loss_tangent {layer.TanD.ToString("0.########", CultureInfo.InvariantCulture)})");
                    sb.Append(')');
                    ctx.W.WriteLine(sb.ToString());
                    break;
                }
                // StackupKind.Via has no counterpart in a stackup section — a via's own geometry
                // carries its size and drill, and the fill model is a circuitRF process parameter.
            }
        }
        ctx.W.WriteLine("    )");
        ctx.W.WriteLine("  )");
        ctx.W.WriteLine();
    }

    /// <summary>
    /// The ordinal → name table. <b>Net 0 must be declared and must be the empty name</b> — it is the
    /// unassigned net, and every entity that belongs to nothing references it.
    /// </summary>
    private static Dictionary<string, int> WriteNetTable(Ctx ctx, PcbExportModel model)
    {
        var names = new List<string>();
        void Collect(IEnumerable<LayoutShape> shapes)
        {
            foreach (var s in shapes)
                if (s.Net is { Length: > 0 } n && !names.Contains(n, StringComparer.Ordinal)) names.Add(n);
        }
        Collect(model.BoardShapes);
        foreach (var def in model.Definitions.Values) Collect(def.Shapes);

        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        ctx.W.WriteLine("  (net 0 \"\")");
        for (int i = 0; i < names.Count; i++)
        {
            ordinals[names[i]] = i + 1;
            ctx.W.WriteLine($"  (net {i + 1} {Quote(names[i])})");
        }
        ctx.W.WriteLine();
        return ordinals;
    }

    // ── Board-level geometry ────────────────────────────────────────────────────────────────────

    private static void WriteBoardShape(Ctx ctx, LayoutShape shape)
    {
        string? layer = ctx.LayerName(shape.Layer);
        if (layer is null) return;

        switch (shape)
        {
            case BitmapShape:
                ctx.BitmapsSkipped++;
                return;

            case LabelShape label:
                WriteText(ctx, label, layer);
                return;

            case ViaShape via:
                WriteVia(ctx, via);
                return;

            case PathShape path when ctx.IsCopper(shape.Layer):
                WriteTrack(ctx, path, layer);
                return;

            case PathShape path:
                WriteStrokedGraphic(ctx, path, layer);
                return;

            default:
                // Every remaining kind is a FILLED region. On copper it becomes a zone (which carries a
                // net); elsewhere a filled graphic.
                if (ctx.IsCopper(shape.Layer)) WriteZone(ctx, shape, layer);
                else WriteFilledGraphic(ctx, shape, layer, prefix: "gr");
                return;
        }
    }

    /// <summary>A copper centreline becomes <c>segment</c>s and <c>arc</c>s — one per edge, because
    /// each carries its own two endpoints in this format.</summary>
    private static void WriteTrack(Ctx ctx, PathShape path, string layer)
    {
        int net = ctx.NetOf(path.Net);
        foreach (var edge in EnumerateEdges(ctx, path))
        {
            if (edge.Mid is { } mid)
            {
                ctx.W.WriteLine(
                    $"  (arc (start {ctx.Mm(edge.X0)} {ctx.My(edge.Y0)}) (mid {ctx.Mm(mid.X)} {ctx.My(mid.Y)}) " +
                    $"(end {ctx.Mm(edge.X1)} {ctx.My(edge.Y1)}) (width {ctx.Mm(path.Width)}) " +
                    $"(layer {Quote(layer)}) (net {net}))");
                ctx.Arcs++;
            }
            else
            {
                ctx.W.WriteLine(
                    $"  (segment (start {ctx.Mm(edge.X0)} {ctx.My(edge.Y0)}) (end {ctx.Mm(edge.X1)} {ctx.My(edge.Y1)}) " +
                    $"(width {ctx.Mm(path.Width)}) (layer {Quote(layer)}) (net {net}))");
                ctx.Segments++;
            }
        }
    }

    private static void WriteStrokedGraphic(Ctx ctx, PathShape path, string layer, string prefix = "gr", string indent = "  ")
    {
        foreach (var edge in EnumerateEdges(ctx, path))
        {
            string stroke = $"(stroke (width {ctx.Mm(Math.Max(path.Width, 1))}) (type solid))";
            if (edge.Mid is { } mid)
                ctx.W.WriteLine(
                    $"{indent}({prefix}_arc (start {ctx.Mm(edge.X0)} {ctx.My(edge.Y0)}) (mid {ctx.Mm(mid.X)} {ctx.My(mid.Y)}) " +
                    $"(end {ctx.Mm(edge.X1)} {ctx.My(edge.Y1)}) {stroke} (layer {Quote(layer)}))");
            else
                ctx.W.WriteLine(
                    $"{indent}({prefix}_line (start {ctx.Mm(edge.X0)} {ctx.My(edge.Y0)}) (end {ctx.Mm(edge.X1)} {ctx.My(edge.Y1)}) " +
                    $"{stroke} (layer {Quote(layer)}))");
            ctx.Graphics++;
        }
    }

    private readonly record struct Edge(long X0, long Y0, long X1, long Y1, (long X, long Y)? Mid);

    /// <summary>
    /// One <see cref="Edge"/> per drawn edge of a path. An Arc edge yields its own midpoint (this
    /// format's three-point arc form); a Cubic is SAMPLED into line segments and counted, because the
    /// format has no cubic on a track at all — reported rather than silently approximated.
    /// </summary>
    private static IEnumerable<Edge> EnumerateEdges(Ctx ctx, PathShape path)
    {
        int n = path.Xy.Length / 2;
        for (int i = 0; i + 1 < n; i++)
        {
            long x0 = path.Xy[2 * i], y0 = path.Xy[2 * i + 1];
            long x1 = path.Xy[2 * i + 2], y1 = path.Xy[2 * i + 3];
            var edge = path.Edges is { } edges && i < edges.Count ? edges[i] : null;

            if (edge?.Kind == EdgeKind.Arc && edge.Bulge != 0)
            {
                var arc = LayoutArc.FromBulge(x0, y0, x1, y1, edge.Bulge);
                if (arc.R > 0)
                {
                    double mid = arc.StartAngle + arc.Sweep / 2.0;
                    yield return new Edge(x0, y0, x1, y1, (
                        (long)Math.Round(arc.Cx + arc.R * Math.Cos(mid), MidpointRounding.AwayFromZero),
                        (long)Math.Round(arc.Cy + arc.R * Math.Sin(mid), MidpointRounding.AwayFromZero)));
                    continue;
                }
            }

            if (edge?.Kind == EdgeKind.Cubic)
            {
                ctx.CubicsFlattened++;
                const int steps = 16;
                long px = x0, py = y0;
                for (int s = 1; s <= steps; s++)
                {
                    double t = (double)s / steps, u = 1 - t;
                    double bx = u * u * u * x0 + 3 * u * u * t * edge.C1X + 3 * u * t * t * edge.C2X + t * t * t * x1;
                    double by = u * u * u * y0 + 3 * u * u * t * edge.C1Y + 3 * u * t * t * edge.C2Y + t * t * t * y1;
                    long qx = s == steps ? x1 : (long)Math.Round(bx, MidpointRounding.AwayFromZero);
                    long qy = s == steps ? y1 : (long)Math.Round(by, MidpointRounding.AwayFromZero);
                    yield return new Edge(px, py, qx, qy, null);
                    px = qx; py = qy;
                }
                continue;
            }

            yield return new Edge(x0, y0, x1, y1, null);
        }
    }

    /// <summary>
    /// A filled copper region becomes a zone whose outline IS the region and whose
    /// <c>filled_polygon</c> is the same points — so it carries its net, which a copper GRAPHIC does
    /// not (checked: no top-level graphic in any measured real board carries one). The cost is stated
    /// in the summary rather than hidden: refilling zones on the receiving side re-derives the copper
    /// from this outline and its clearances.
    /// </summary>
    private static void WriteZone(Ctx ctx, LayoutShape shape, string layer)
    {
        var ring = OuterRingWithHoles(ctx, shape);
        if (ring.Length < 6) return;

        int net = ctx.NetOf(shape.Net);
        ctx.W.WriteLine("  (zone");
        ctx.W.WriteLine($"    (net {net})");
        ctx.W.WriteLine($"    (layer {Quote(layer)})");
        ctx.W.WriteLine("    (hatch edge 0.5)");
        ctx.W.WriteLine("    (fill yes (thermal_gap 0.2) (thermal_bridge_width 0.2))");
        ctx.W.WriteLine("    (polygon");
        WritePts(ctx, ring, "      ");
        ctx.W.WriteLine("    )");
        ctx.W.WriteLine("    (filled_polygon");
        ctx.W.WriteLine($"      (layer {Quote(layer)})");
        WritePts(ctx, ring, "      ");
        ctx.W.WriteLine("    )");
        ctx.W.WriteLine("  )");
        ctx.Zones++;
    }

    private static void WriteFilledGraphic(Ctx ctx, LayoutShape shape, string layer, string prefix, string indent = "  ")
    {
        var ring = OuterRingWithHoles(ctx, shape);
        if (ring.Length < 6) return;
        ctx.W.WriteLine($"{indent}({prefix}_poly");
        WritePts(ctx, ring, indent + "  ");
        ctx.W.WriteLine($"{indent}  (stroke (width 0) (type solid))");
        ctx.W.WriteLine($"{indent}  (fill yes)");
        ctx.W.WriteLine($"{indent}  (layer {Quote(layer)})");
        ctx.W.WriteLine($"{indent})");
        ctx.Graphics++;
    }

    private static void WritePts(Ctx ctx, long[] ring, string indent)
    {
        ctx.W.Write(indent);
        ctx.W.Write("(pts");
        for (int i = 0; i + 1 < ring.Length; i += 2)
        {
            if (i % 12 == 0 && i > 0) { ctx.W.WriteLine(); ctx.W.Write(indent); ctx.W.Write("  "); }
            ctx.W.Write($" (xy {ctx.Mm(ring[i])} {ctx.My(ring[i + 1])})");
        }
        ctx.W.WriteLine(")");
    }

    /// <summary>
    /// A filled region as ONE closed ring, with any holes bridged in by a zero-width slit.
    ///
    /// <para>That is not a workaround here — it is what this format natively does. L4d measured it: in
    /// a real filled zone the vertices that repeat bound sub-loops of NEGATIVE signed area reached by a
    /// doubled edge, i.e. a hole cut into a single self-touching outline. So writing a keyholed ring
    /// produces exactly the construction the originating tool produces itself, and reading it back
    /// through <c>PcbReader</c> round-trips. (<c>GdsiiWriter</c> carries the same ~20-line bridge for
    /// GDSII's own version of this constraint; kept separate rather than widening that audited file's
    /// visibility, following <c>DxfNaming</c>'s stated precedent.)</para>
    /// </summary>
    /// <param name="omitDrill">
    /// When set, the one inner ring that IS this pad's drilled hole is dropped rather than keyholed —
    /// it is written as <c>(drill …)</c> and re-punched by the reader, so carrying it in the outline as
    /// well would cut it twice and leave a keyhole slit across the copper.
    ///
    /// <para><b>Only that one ring.</b> A pad's other holes are real copper features — a custom pad
    /// built from unfilled circle primitives is a pad full of annuli — and dropping them along with the
    /// drill silently fills them in. Measured on a real board: three custom pads lost every hole they
    /// had, not just the drilled one.</para>
    /// </param>
    private static long[] OuterRingWithHoles(Ctx ctx, LayoutShape shape, ViaShape? omitDrill = null)
    {
        IReadOnlyList<long[]> rings;
        try { rings = LayoutFlattener.Flatten(shape, ResolveTol(ctx, shape)); }
        catch (ArgumentOutOfRangeException) { return []; }
        if (rings.Count == 0) return [];

        var inner = rings.Skip(1).Where(r => !IsDrillRing(r, omitDrill)).ToList();
        if (inner.Count == 0) return rings[0];

        ctx.HolesKeyholed += inner.Count;
        return Keyhole(rings[0], inner);
    }

    /// <summary>True when <paramref name="ring"/> is the hole <paramref name="drill"/> punched — judged
    /// by its CENTROID sitting on the drill, which holds for a round hole and for a slot alike, and
    /// which no other feature of a pad shares by accident.</summary>
    private static bool IsDrillRing(long[] ring, ViaShape? drill)
    {
        if (drill is null || ring.Length < 6) return false;
        double cx = 0, cy = 0;
        for (int i = 0; i < ring.Length; i += 2) { cx += ring[i]; cy += ring[i + 1]; }
        cx /= ring.Length / 2.0; cy /= ring.Length / 2.0;
        double tol = Math.Max(drill.DrillSize / 2.0, 1);
        return Math.Abs(cx - drill.X) <= tol && Math.Abs(cy - drill.Y) <= tol;
    }

    private static long ResolveTol(Ctx ctx, LayoutShape shape)
        => LayoutFlattener.OwnTolDbu(shape) ?? Math.Max(1, (long)(ctx.DbuPerMm / 1000));

    private static long[] Keyhole(long[] outer, List<long[]> holes)
    {
        var combined = new List<long>(outer);
        foreach (var hole in holes)
        {
            int combinedPoints = combined.Count / 2;
            int holePoints = hole.Length / 2;
            if (holePoints == 0) continue;
            int bestOuterIdx = 0, bestHoleIdx = 0;
            double bestDistSq = double.MaxValue;

            for (int oi = 0; oi < combinedPoints; oi++)
            {
                double ox = combined[oi * 2], oy = combined[oi * 2 + 1];
                for (int hi = 0; hi < holePoints; hi++)
                {
                    double dx = ox - hole[hi * 2], dy = oy - hole[hi * 2 + 1];
                    double distSq = dx * dx + dy * dy;
                    if (distSq < bestDistSq) { bestDistSq = distSq; bestOuterIdx = oi; bestHoleIdx = hi; }
                }
            }

            long bridgeX = combined[bestOuterIdx * 2], bridgeY = combined[bestOuterIdx * 2 + 1];
            var insertion = new List<long>();
            for (int k = 0; k <= holePoints; k++)
            {
                int idx = (bestHoleIdx + k) % holePoints;
                insertion.Add(hole[idx * 2]);
                insertion.Add(hole[idx * 2 + 1]);
            }
            insertion.Add(bridgeX);
            insertion.Add(bridgeY);

            combined.InsertRange((bestOuterIdx + 1) * 2, insertion);
        }
        return [.. combined];
    }

    private static void WriteVia(Ctx ctx, ViaShape via)
    {
        // R-L4d-10 in reverse: LandingLayer is the PAD's copper, and it is what names the span. The
        // barrel's own layer is a circuitRF drill layer with no counterpart here — the hole is the
        // (drill …) value, not a layer.
        string from = via.LandingLayer is { } landing ? ctx.LayerName(landing) ?? "F.Cu" : "F.Cu";
        string to = OppositeCopper(ctx, from);
        ctx.W.WriteLine(
            $"  (via (at {ctx.Mm(via.X)} {ctx.My(via.Y)}) (size {ctx.Mm(via.PadSize)}) " +
            $"(drill {ctx.Mm(via.DrillSize)}) (layers {Quote(from)} {Quote(to)}) (net {ctx.NetOf(via.Net)}))");
        ctx.Vias++;
    }

    private static string OppositeCopper(Ctx ctx, string from)
    {
        var copper = ctx.Layers.Table.Where(r => r.IsCopper).ToList();
        if (copper.Count < 2) return from == "F.Cu" ? "B.Cu" : "F.Cu";
        var first = copper.MinBy(r => r.Ordinal)!;
        var last = copper.MaxBy(r => r.Ordinal)!;
        return from == first.Name ? last.Name : first.Name;
    }

    private static void WriteText(Ctx ctx, LabelShape label, string layer, string prefix = "gr", string indent = "  ")
    {
        double degrees = label.RotationDegrees;
        ctx.W.WriteLine(
            $"{indent}({prefix}_text {Quote(label.Text)} (at {ctx.Mm(label.X)} {ctx.My(label.Y)} {ctx.Deg(degrees)}) " +
            $"(layer {Quote(layer)}) (effects (font (size {ctx.Mm(label.Height)} {ctx.Mm(label.Height)}) " +
            $"(thickness {ctx.Mm(Math.Max(label.Height / 8, 1))})){JustifyClause(label)}))");
        ctx.Texts++;
    }

    /// <summary>
    /// The <c>(justify …)</c> this format needs to place the string where circuitRF draws it — the
    /// exact inverse of <c>PcbReader.ReadJustification</c>. <b>Omitting it is not neutral</b>: this
    /// format's unstated default is centred on both axes, so a label carrying circuitRF's own default
    /// anchor (left, baseline) must SAY <c>left bottom</c> or it comes back half its width displaced.
    /// A label whose anchor already is this format's default emits nothing, which keeps the common
    /// case's output unchanged.
    /// </summary>
    private static string JustifyClause(LabelShape label)
    {
        var words = new List<string>(2);
        switch (label.HAlign ?? LabelHAlign.Left)
        {
            case LabelHAlign.Left:  words.Add("left"); break;
            case LabelHAlign.Right: words.Add("right"); break;
        }
        switch (label.VAlign ?? LabelVAlign.Baseline)
        {
            case LabelVAlign.Top: words.Add("top"); break;
            // Baseline is not expressible; bottom is the nearest thing this format has, and the
            // difference is one descender on a string that usually has none.
            case LabelVAlign.Baseline:
            case LabelVAlign.Bottom: words.Add("bottom"); break;
        }
        return words.Count == 0 ? "" : $" (justify {string.Join(' ', words)})";
    }

    // ── Footprints ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One placement becomes one footprint.
    ///
    /// <para><b>The mirror is BAKED, not transformed</b> — L4d measured that this format stores a
    /// back-side footprint's child geometry already flipped (every local Y negated) with its child
    /// layers already rewritten to their back-side counterparts. Emitting our own <c>MirrorX</c> as a
    /// transform instead would produce a footprint that flips a second time when the receiving tool
    /// applies its own side convention.</para>
    ///
    /// <para><b>A pad's angle is written ABSOLUTE</b> (placement + local), because that is how the
    /// format stores it — also measured, on a part placed at 0° and 180° whose pad angles read 0 and
    /// 180 over byte-identical pad POSITIONS.</para>
    /// </summary>
    private static void WriteFootprint(Ctx ctx, PcbFootprintDef def, PcbFootprintPlacement placement)
    {
        bool flip = placement.MirrorX;
        string side = flip ? "B.Cu" : "F.Cu";

        ctx.W.WriteLine($"  (footprint {Quote("circuitrf:" + def.Name)}");
        ctx.W.WriteLine($"    (layer {Quote(side)})");
        ctx.W.WriteLine($"    (at {ctx.Mm(placement.X)} {ctx.My(placement.Y)} {ctx.Deg(placement.RotationDegrees)})");
        if (placement.Reference is { Length: > 0 } reference)
            ctx.W.WriteLine($"    (property \"Reference\" {Quote(reference)})");

        // Each pin claims the copper shape it sits inside; that shape becomes its PAD, and everything
        // left over is footprint artwork. Without this a pad would either be invented from the pin's
        // bare width (losing the real artwork) or duplicated (pad AND graphic on the same copper).
        var claimed = new HashSet<LayoutShape>();
        foreach (var pin in def.Pins)
        {
            var artwork = ClaimArtworkFor(ctx, def, pin, claimed);
            // A through-hole pad is TWO shapes at one coordinate in circuitRF's model — the copper and
            // the drilled barrel (ViaShape) — and one pad in this format. Claim both, or the drill is
            // silently dropped and every through-hole part exports as surface-mount.
            var drill = ClaimDrillFor(def, pin, claimed);
            // A through-hole pad is ONE pad spanning several copper layers here and N congruent shapes
            // in circuitRF's model — claim the copies rather than writing them again as graphics, and
            // remember WHICH layers they were on, because that is the pad's real span.
            var span = new List<LayerKey> { pin.Layer };
            if (drill is not null && artwork is not null and not ViaShape)
                span.AddRange(ClaimCongruentCopperOnOtherLayers(ctx, def, artwork, claimed));
            // An oval drill's SLOT is a stroke on the barrel's own layer, alongside it — claim it so it
            // is not written again as graphics, and hand it over so the drill can be written oval.
            var slot = ClaimSlotFor(def, drill, claimed);
            WritePad(ctx, pin, artwork, drill, placement, flip, span, slot);
        }

        foreach (var shape in def.Shapes)
        {
            if (claimed.Contains(shape)) continue;
            WriteFootprintShape(ctx, shape, flip, placement);
        }

        ctx.W.WriteLine("  )");
        ctx.Footprints++;
    }

    /// <summary>
    /// The smallest unclaimed COPPER shape on the pin's own layer whose bounding box contains it —
    /// smallest, so a pad inside a pour claims the pad and not the pour.
    ///
    /// <para><b>A <see cref="ViaShape"/> is only ever the fallback</b>, never a competitor. It is the
    /// drilled barrel, and <see cref="ClaimDrillFor"/> claims it separately in the very next step; the
    /// only case where it is legitimately a pin's ARTWORK is a bare via with no copper shape around it
    /// at all (a via-as-pad). Letting it compete on area is how a real pad gets displaced by its own
    /// hole: a barrel is smaller than the pad it sits in, so it wins the smallest-area test, the pin
    /// exports as a plain circle of the barrel's diameter, and the real 5 x 10 mm outline is then
    /// written a second time as unclaimed footprint GRAPHICS — the same copper twice, in two different
    /// shapes. (Latent until the import stopped inflating a footprint drill's PadSize to the pad's long
    /// dimension, which had been keeping the barrel's box the larger of the two by accident.)</para>
    /// </summary>
    private static LayoutShape? ClaimArtworkFor(Ctx ctx, PcbFootprintDef def, LayoutPin pin, HashSet<LayoutShape> claimed)
    {
        LayoutShape? best = null;
        long bestArea = long.MaxValue;
        LayoutShape? bareVia = null;
        long bareViaArea = long.MaxValue;

        foreach (var shape in def.Shapes)
        {
            if (shape is LabelShape or BitmapShape) continue;
            if (claimed.Contains(shape)) continue;

            var box = LayoutGeometry.BboxOf(shape);
            if (box.IsEmpty || !box.Contains(pin.X, pin.Y)) continue;
            long area = (box.MaxX - box.MinX) * (box.MaxY - box.MinY);

            if (shape is ViaShape)
            {
                if (area < bareViaArea) { bareViaArea = area; bareVia = shape; }
                continue;
            }
            if (shape.Layer != pin.Layer) continue;
            if (area < bestArea) { bestArea = area; best = shape; }
        }

        best ??= bareVia;
        if (best is not null) claimed.Add(best);
        else ctx.PinsWithNoArtwork++;
        return best;
    }

    /// <summary>
    /// Claims the pad's congruent copies on the OTHER copper layers, once a through-hole pad has been
    /// decided. A through-hole pad is one pad on <c>*.Cu</c> in this format and N congruent shapes in
    /// circuitRF's model — one per copper layer it occupies — so the shapes on the layers the pad
    /// already covers are not leftover artwork. Left unclaimed they are written again as footprint
    /// graphics, which puts a second, identical copper region on every inner and back layer.
    /// </summary>
    private static List<LayerKey> ClaimCongruentCopperOnOtherLayers(
        Ctx ctx, PcbFootprintDef def, LayoutShape artwork, HashSet<LayoutShape> claimed)
    {
        var also = new List<LayerKey>();
        var type = artwork.GetType();
        var box = LayoutGeometry.BboxOf(artwork);
        if (box.IsEmpty) return also;

        foreach (var shape in def.Shapes)
        {
            if (claimed.Contains(shape) || shape.GetType() != type) continue;
            if (shape.Layer == artwork.Layer || !ctx.IsCopper(shape.Layer)) continue;
            if (LayoutGeometry.BboxOf(shape) != box) continue;
            claimed.Add(shape);
            also.Add(shape.Layer);
        }
        return also;
    }

    /// <summary>A <see cref="ViaShape"/> centred on the pin — the drilled half of a through-hole pad.</summary>
    private static ViaShape? ClaimDrillFor(PcbFootprintDef def, LayoutPin pin, HashSet<LayoutShape> claimed)
    {
        foreach (var shape in def.Shapes)
        {
            if (shape is not ViaShape via || claimed.Contains(via)) continue;
            if (Math.Abs(via.X - pin.X) > via.PadSize / 2 || Math.Abs(via.Y - pin.Y) > via.PadSize / 2) continue;
            claimed.Add(via);
            return via;
        }
        return null;
    }

    /// <summary>The two-point stroke on the barrel's own layer, centred on it — an oval drill's slot,
    /// which the reader draws alongside the <see cref="ViaShape"/> because that primitive carries only
    /// one diameter. Null when the hole is round.</summary>
    private static PathShape? ClaimSlotFor(PcbFootprintDef def, ViaShape? drill, HashSet<LayoutShape> claimed)
    {
        if (drill is null) return null;
        foreach (var shape in def.Shapes)
        {
            if (shape is not PathShape p || claimed.Contains(p)) continue;
            if (p.Layer != drill.Layer || p.Xy.Length != 4 || p.Width != drill.DrillSize) continue;
            if ((p.Xy[0] + p.Xy[2]) / 2 != drill.X || (p.Xy[1] + p.Xy[3]) / 2 != drill.Y) continue;
            claimed.Add(p);
            return p;
        }
        return null;
    }

    private static void WritePad(
        Ctx ctx, LayoutPin pin, LayoutShape? artwork, ViaShape? drill, PcbFootprintPlacement placement, bool flip,
        IReadOnlyList<LayerKey> copperSpan, PathShape? slot = null)
    {
        // The pad sits where its ARTWORK is, not where the pin marker is. The pin names the connection;
        // the copper is the geometry, and re-centring it on the marker moves real copper.
        var centre = CentreOf(artwork) ?? (pin.X, pin.Y);
        long px = centre.X;

        // ── A drilled pad's `at` is the HOLE, and the copper's displacement is the drill's (offset) ──
        //
        // The reader puts a pad's shape at `at + offset` and its hole at `at` — that is this format's
        // own convention, measured from plotted Gerber. So the anchor written here is the HOLE, and
        // anything between it and the copper centre has to go back out as the offset. Writing the
        // copper's centre as `at` with a bare `(drill …)` instead moved the hole onto the copper centre,
        // which drifts the whole pad by the offset on every round trip.
        var anchor = drill is not null ? (X: drill.X, Y: drill.Y) : centre;
        long offX = centre.X - anchor.X, offY = centre.Y - anchor.Y;
        long y = flip ? -anchor.Y : anchor.Y;
        // The mask opening belongs to the side the PAD's copper is on, which is not the same question as
        // whether the PLACEMENT is mirrored. An import bakes a back-side footprint's flip into the cell
        // (its child layers are already the back-side ones, so MirrorX is false), and pairing that
        // footprint's B.Cu pad with F.Mask opens the solder mask on the wrong face of the board.
        string layers = ctx.LayerName(pin.Layer) is { } pinLayer
            ? Quote(flip ? PcbLayerNaming.FlipSide(pinLayer) : pinLayer) + " " +
              Quote(MaskSideFor(flip ? PcbLayerNaming.FlipSide(pinLayer) : pinLayer))
            : "\"*.Cu\" \"*.Mask\"";
        double angle = placement.RotationDegrees;   // absolute, per the measurement above
        int net = ctx.NetOf(artwork?.Net ?? null);

        string at = $"(at {ctx.Mm(anchor.X)} {ctx.My(y)} {ctx.Deg(angle)})";
        string kind = drill is null ? "smd" : "thru_hole";
        // (drill oval W H) when the hole is a slot: W is the barrel, H the barrel plus the stroke's own
        // span, since the reader recovers the span as |W - H|. Without this a slot came back round —
        // right width, but a hole where the file has a slot.
        string drillSize = drill is null ? "" : ctx.Mm(drill.DrillSize);
        if (slot is not null)
        {
            double sdx = slot.Xy[2] - slot.Xy[0], sdy = slot.Xy[3] - slot.Xy[1];
            long span = (long)Math.Round(Math.Sqrt(sdx * sdx + sdy * sdy), MidpointRounding.AwayFromZero);
            long across = drill!.DrillSize, along = drill.DrillSize + span;

            // W is the slot's X extent and H its Y extent, IN THE PAD'S OWN FRAME — so which of the two
            // gets the span depends on which way the slot runs. Writing it X-major unconditionally turns
            // every vertical slot on its side, which is a hole in the wrong place, not a rounding.
            bool alongY = Math.Abs(sdy) > Math.Abs(sdx);
            drillSize = alongY
                ? $"oval {ctx.Mm(across)} {ctx.Mm(along)}"
                : $"oval {ctx.Mm(along)} {ctx.Mm(across)}";

            // This format can only run a slot along one of the pad's own axes. A slot at any other
            // angle needs the PAD turned to meet it, which would turn its copper too — so it is snapped
            // and said, rather than silently rotated.
            double minor = Math.Min(Math.Abs(sdx), Math.Abs(sdy));
            if (minor > Math.Max(Math.Abs(sdx), Math.Abs(sdy)) * 0.01)
                ctx.Notes.Add(
                    "A slotted hole runs at an angle to its own pad, which this format cannot state — " +
                    "it was written along the nearer of the pad's two axes.");
        }
        string hole = drill is null ? ""
            : offX == 0 && offY == 0
                ? $" (drill {drillSize})"
                : $" (drill {drillSize} (offset {ctx.Mm(offX)} {ctx.My(flip ? -offY : offY)}))";
        string padLayers = drill is null ? layers : ThroughPadLayers(ctx, copperSpan, layers);

        switch (artwork)
        {
            case RectShape r:
                ctx.W.WriteLine(
                    $"    (pad {Quote(pin.Name)} {kind} rect {at} (size {ctx.Mm(r.X2 - r.X1)} {ctx.Mm(r.Y2 - r.Y1)}){hole} " +
                    $"(layers {padLayers}) (net {net}))");
                break;

            case RoundedRectShape rr:
            {
                long w = rr.X2 - rr.X1, h = rr.Y2 - rr.Y1;
                double ratio = Math.Min(w, h) > 0 ? rr.CornerRadius / (double)Math.Min(w, h) : 0.25;
                ctx.W.WriteLine(
                    $"    (pad {Quote(pin.Name)} {kind} roundrect {at} (size {ctx.Mm(w)} {ctx.Mm(h)}) " +
                    $"(roundrect_rratio {ratio.ToString(MmFormat, CultureInfo.InvariantCulture)}){hole} " +
                    $"(layers {padLayers}) (net {net}))");
                break;
            }

            case CircleShape c:
                ctx.W.WriteLine(
                    $"    (pad {Quote(pin.Name)} {kind} circle {at} (size {ctx.Mm(c.R * 2)} {ctx.Mm(c.R * 2)}){hole} " +
                    $"(layers {padLayers}) (net {net}))");
                break;

            // A two-point round-capped stroke IS this format's `oval` pad — a stadium of the stroke's
            // width, `size.x - size.y` long along its own axis. Without this case it fell to the
            // catch-all below, whose OuterRingWithHoles has no ring to give for a stroked path, and from
            // there to the default: a plain CIRCLE of the pin's declared width, silently replacing the
            // pad's real outline. Measured on a real board, that hit 23 pads — every oval one, plus
            // every custom pad whose claimed primitive was a line.
            case PathShape p when p.Xy.Length == 4 && (p.Edges is null || p.Edges.Count == 0)
                                  && p.End == PathEndStyle.Round && p.Width > 0:
            {
                double ddx = p.Xy[2] - p.Xy[0], ddy = p.Xy[3] - p.Xy[1];
                long seg = (long)Math.Round(Math.Sqrt(ddx * ddx + ddy * ddy), MidpointRounding.AwayFromZero);
                // The reader recovers the stroke from `length = |size.x - size.y|`, so the long axis is
                // the segment PLUS one width (the two round caps together).
                double local = seg == 0 ? 0.0 : Math.Atan2(ddy, ddx) * 180.0 / Math.PI;
                if (flip) local = -local;                 // the cell's Y is negated on the way out
                string ovalAt = $"(at {ctx.Mm(anchor.X)} {ctx.My(y)} {ctx.Deg(LayoutAngle.Normalize(angle + local))})";
                ctx.W.WriteLine(
                    $"    (pad {Quote(pin.Name)} {kind} oval {ovalAt} " +
                    $"(size {ctx.Mm(seg + p.Width)} {ctx.Mm(p.Width)}){hole} (layers {padLayers}) (net {net}))");
                break;
            }

            case ViaShape v:
                // The pin's only artwork IS the barrel — pad and hole in one shape.
                ctx.W.WriteLine(
                    $"    (pad {Quote(pin.Name)} thru_hole circle {at} (size {ctx.Mm(v.PadSize)} {ctx.Mm(v.PadSize)}) " +
                    $"(drill {ctx.Mm(v.DrillSize)}) (layers \"*.Cu\" \"*.Mask\") (net {net}))");
                break;

            case not null:
            {
                // Anything else keeps its exact outline as a custom pad's own primitive — the same
                // graphics tokens §5 reads, which is why no second geometry path is needed here either.
                var ring = OuterRingWithHoles(ctx, artwork, omitDrill: drill);
                if (ring.Length < 6) goto default;
                // ── The anchor is a formality here, and must be sized like one ───────────────────
                //
                // A custom pad's copper is its ANCHOR shape unioned with its primitives. The whole
                // outline is already in the primitive below, so the anchor contributes nothing this
                // pad should have — but the format requires a (size …), so it gets the smallest one
                // that still parses. Writing the PIN's declared width here instead put a real square
                // of invented copper at the centre of every custom pad, which a reader that honours
                // the anchor (this repo's own, since 2026-08-25) reads straight back in.
                string anchorSize = ctx.Mm(1);
                ctx.W.WriteLine($"    (pad {Quote(pin.Name)} {kind} custom {at} (size {anchorSize} {anchorSize}){hole}");
                ctx.W.WriteLine($"      (layers {padLayers}) (net {net})");
                ctx.W.WriteLine("      (options (clearance outline) (anchor rect))");
                ctx.W.WriteLine("      (primitives");
                ctx.W.WriteLine("        (gr_poly");
                WritePts(ctx, Recentre(ring, px, centre.Y, flip), "          ");
                ctx.W.WriteLine("          (width 0) (fill yes)");
                ctx.W.WriteLine("        )");
                ctx.W.WriteLine("      )");
                ctx.W.WriteLine("    )");
                break;
            }

            default:
                // No artwork claimed this pin — write the connection point at the width the pin states,
                // which is all the model holds. Counted, so the summary can say how many. A `goto
                // default` from the catch-all above lands here too, which is why this counts rather
                // than only the null case: an outline this format could not carry is the same loss to
                // the user as no outline at all, and it used to be silent.
                if (artwork is not null) ctx.PinsWithNoArtwork++;
                ctx.W.WriteLine(
                    $"    (pad {Quote(pin.Name)} {kind} circle {at} " +
                    $"(size {ctx.Mm(Math.Max(pin.WidthDbu, 1))} {ctx.Mm(Math.Max(pin.WidthDbu, 1))}){hole} " +
                    $"(layers {padLayers}) (net {net}))");
                break;
        }
        ctx.PadsFromPins++;
    }

    /// <summary>
    /// The <c>(layers …)</c> list for a DRILLED pad. <c>*.Cu</c> means every copper layer, and it is
    /// only right when the pad's copper really is on every one of them.
    ///
    /// <para>Writing it unconditionally put copper on layers the design left bare: a non-plated hole
    /// whose only copper was a front-side aperture came back with the same artwork repeated on both
    /// inner layers and the back, which is a short, not a rendering artefact. The span comes from the
    /// congruent copies the pad itself claimed, so it is measured from the design rather than assumed
    /// from the fact that there is a hole.</para>
    /// </summary>
    private static string ThroughPadLayers(Ctx ctx, IReadOnlyList<LayerKey> span, string singleLayerFallback)
    {
        int copperInTech = ctx.Layers.Table.Count(r => r.IsCopper);
        var distinct = span.Distinct().ToList();
        if (copperInTech > 0 && distinct.Count >= copperInTech) return "\"*.Cu\" \"*.Mask\"";
        if (distinct.Count <= 1) return singleLayerFallback;

        var names = distinct.Select(k => ctx.LayerName(k)).Where(n => n is not null).Select(n => n!).ToList();
        if (names.Count == 0) return singleLayerFallback;
        var masks = names.Select(MaskSideFor).Distinct();
        return string.Join(' ', names.Concat(masks).Select(Quote));
    }

    /// <summary>The solder-mask layer facing the same side of the board as <paramref name="copperLayer"/>.
    /// A layer this format does not name by side (an inner copper layer, or a name we did not mint)
    /// gets the front mask, which is what a surface pad on it would need if it had one.</summary>
    private static string MaskSideFor(string copperLayer)
        => copperLayer.StartsWith("B.", StringComparison.OrdinalIgnoreCase) ? "B.Mask" : "F.Mask";

    /// <summary>A custom pad's primitives are relative to the PAD, not the footprint.</summary>
    private static long[] Recentre(long[] ring, long px, long py, bool flip)
    {
        var moved = new long[ring.Length];
        for (int i = 0; i + 1 < ring.Length; i += 2)
        {
            moved[i] = ring[i] - px;
            moved[i + 1] = (flip ? -ring[i + 1] : ring[i + 1]) - (flip ? -py : py);
        }
        return moved;
    }

    private static void WriteFootprintShape(
        Ctx ctx, LayoutShape shape, bool flip, PcbFootprintPlacement placement)
    {
        // A via inside a cell that no pin claimed is still a real drilled hole — a mounting hole, a
        // thermal via. This format has no free-standing via inside a footprint, but it has an UNNAMED
        // pad, which is exactly that. Dropping it silently would remove holes from the board.
        if (shape is ViaShape via)
        {
            long vy = flip ? -via.Y : via.Y;
            ctx.W.WriteLine(
                $"    (pad \"\" thru_hole circle (at {ctx.Mm(via.X)} {ctx.My(vy)} {ctx.Deg(placement.RotationDegrees)}) " +
                $"(size {ctx.Mm(via.PadSize)} {ctx.Mm(via.PadSize)}) (drill {ctx.Mm(via.DrillSize)}) " +
                $"(layers \"*.Cu\" \"*.Mask\") (net {ctx.NetOf(via.Net)}))");
            ctx.UnnamedDrills++;
            return;
        }

        string? layer = ctx.LayerName(shape.Layer);
        if (layer is null) return;
        if (flip) layer = PcbLayerNaming.FlipSide(layer);

        var emitted = flip ? FlipY(shape) : shape;

        switch (emitted)
        {
            case BitmapShape: ctx.BitmapsSkipped++; return;
            case LabelShape label: WriteText(ctx, label, layer, prefix: "fp", indent: "    "); return;
            case PathShape path: WriteStrokedGraphic(ctx, path, layer, prefix: "fp", indent: "    "); return;
            default: WriteFilledGraphic(ctx, emitted, layer, prefix: "fp", indent: "    "); return;
        }
    }

    /// <summary>The centre of a shape's own bounding box, or null when it has none.</summary>
    private static (long X, long Y)? CentreOf(LayoutShape? shape)
    {
        if (shape is null) return null;
        var b = LayoutGeometry.BboxOf(shape);
        return b.IsEmpty ? null : ((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2);
    }

    /// <summary>Bakes an instance's mirror into a cell-local shape, per the measurement in
    /// <see cref="WriteFootprint"/>. Negating Y reverses an arc's sense, so every bulge flips sign —
    /// <c>LayoutFlatten.FlipBulgeSigns</c> owns that rule and is reused rather than re-derived.</summary>
    private static LayoutShape FlipY(LayoutShape shape)
    {
        var clone = LayoutGeometry.Clone(shape);
        LayoutCoordinateWalk.Transform(clone, LayoutCoordinateTransform.AxisIndependent(v => v, v => -v, v => v));
        LayoutFlatten.FlipBulgeSigns(clone);
        return clone;
    }

    /// <summary>Strings are double-quoted UTF-8 with backslash escapes (§2).</summary>
    private static string Quote(string s)
        => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
