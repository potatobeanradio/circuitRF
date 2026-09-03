// Reads a board file's tokens into neutral LayoutShape/LayoutPin geometry (docs/sonnet-briefs/
// brief-L4d-kicad-pcb-import.md §§2-8). Like DxfReader, this file touches NOTHING but bytes and
// tokens — no CellFolder, no Technology, no Messages. PcbImport is the only piece that does.
//
// R-L4d-1 — dispatch on the tokens actually present; never branch on the version stamp, never refuse a
// file for its version. That is not a stylistic preference here, it is what four measured epochs of
// one real board force:
//
//   epoch      stroke width          arc parameterisation   fill flag        net reference
//   20171130   (width W)             (angle A)              absent           (net 7)  + a top-level table
//   20211014   (width W)             (mid x y)              (fill none|yes)  (net 7)  + a top-level table
//   20221018   (stroke (width W) …)  (mid x y)              (fill none|yes)  (net 7)  + a top-level table
//   20260206   (stroke (width W) …)  (mid x y)              (fill no|yes)    (net "GND"), NO table
//
// and the spellings are mixed WITHIN one file: at 20260206 every fp_line carries (stroke …) while
// gr_poly still carries a bare (width …). The layer table moves too — B.Cu is ordinal 31 through
// 20221018 and 2 at 20260206 — and at 20171130 a renamed layer's user name occupies the CANONICAL
// name slot, so a file may contain no string "F.Cu" at all. Everything therefore resolves through the
// file's own (layers …) table, and "is this copper" is the table's TYPE word, never a name or an
// ordinal range.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CircuitRF.Design.Layout.Interchange;

public static class PcbReader
{
    /// <summary>
    /// R-L4d-20: refuse before allocating, and name the number. Deliberately the SAME constant
    /// <c>LayoutFlatten</c> already establishes for the other unbounded-expansion path rather than a
    /// second number that can drift from it — a reader that dies partway through a large board leaves a
    /// half-imported layout and no explanation, which is the failure this exists to prevent.
    /// </summary>
    public const long EntityHardCeiling = LayoutFlatten.FlattenAllLevelsHardCeiling;

    /// <summary><paramref name="Refusal"/> non-null means nothing was read and nothing must be created
    /// (R-L4d-20's "before allocating", and a file that is not a board at all).</summary>
    public sealed record ReadResult(PcbBoard? Board, string? Refusal);

    /// <summary>The root tag every file of this format opens with.</summary>
    private const string RootTag = "kicad_pcb";

    /// <summary>
    /// The SOURCE layer name every via barrel is drawn on.
    ///
    /// <para>R-L4d-10 needs two DIFFERENT layers per via — the barrel on <see cref="LayoutShape.Layer"/>
    /// and the pad on <see cref="ViaShape.LandingLayer"/> — and this format's layer table has no drill
    /// layer in it at all: a via states only the copper span it connects. Putting the barrel on the
    /// span's own top copper would collapse the two fields onto one key and make the distinction
    /// unobservable, which is precisely the failure ViaShape's doc comment warns about. So the reader
    /// mints one synthetic source layer by this name and lets it go through the ordinary reconciliation
    /// like every other source layer — where it matches a technology's own drill layer by name with no
    /// authoring at all (the shipped PCB starter technology calls its drill layer exactly this).</para>
    /// </summary>
    public const string DrillLayerName = "Drill";

    // Entity-bearing tags, for the pre-parse census AND for the "what did we read" counter.
    private static readonly string[] CensusTags =
    [
        "segment", "arc", "via", "zone", "footprint", "module", "pad",
        "gr_line", "gr_rect", "gr_circle", "gr_arc", "gr_poly", "gr_curve", "bezier", "gr_text",
        "fp_line", "fp_rect", "fp_circle", "fp_arc", "fp_poly", "fp_curve", "fp_text",
        "filled_polygon",
    ];

    /// <summary>Top-level tags that carry no geometry and are not a mystery — reported nowhere,
    /// because naming them would bury the tokens a reader actually needs to see (§2's own "a file full
    /// of tokens we skip must not read as a file full of mysteries").</summary>
    private static readonly HashSet<string> IgnoredMetadata =
    [
        "version", "generator", "generator_version", "host", "general", "paper", "page", "title_block",
        "setup", "net_class", "uuid", "tstamp", "property", "descr", "tags", "attr", "model", "path",
        "sheetname", "sheetfile", "autoplace_cost90", "autoplace_cost180", "solder_mask_margin",
        "solder_paste_margin", "solder_paste_ratio", "clearance", "zone_connect", "thermal_width",
        "thermal_gap", "thermal_bridge_angle", "private_layers", "net_tie_pad_groups", "embedded_fonts",
        "embedded_files", "component_classes", "duplicate_pad_numbers_are_jumpers", "effects", "layer",
        "layers", "at", "locked", "placed", "tedit", "jumper_pad_groups", "libraries", "component_class",
        "units", "unit", "pins", "net_name", "hatch", "connect_pads", "min_thickness", "polygon",
        "filled_polygon", "keepout", "fill", "priority", "filled_areas_thickness",
    ];

    /// <summary>Entities we deliberately do not import, reported by type with a count (§5's own table).</summary>
    private static readonly HashSet<string> SkippedEntities =
    [
        "image", "dimension", "group", "target", "gr_bbox", "gr_text_box", "fp_text_box", "table",
        "tuning_pattern", "generated", "teardrop", "point",
    ];

    /// <summary>
    /// A footprint's own text is skipped ON PURPOSE, and the reason is R-L4d-15 rather than laziness.
    ///
    /// <para>An <c>fp_text</c> is the placement's reference designator and value — R3, 10k — not the
    /// library part's artwork. Importing it into the CELL would bake one placement's designator into the
    /// shared cell and mint a separate cell per placement, which is exactly the 400-copies-of-geometry
    /// outcome R-L4d-15 exists to prevent. Board-level <c>gr_text</c> is imported normally (§5); this is
    /// the one text case where importing costs more than it carries.</para>
    /// </summary>
    /// <summary>Reported once per pad that HAD a net the shared cell cannot carry — see the note in
    /// <c>ReadPad</c>. Stated rather than silent, because "the tracks know, the pads do not" is exactly
    /// the kind of thing a user needs told before wiring up ports.</summary>
    private const string PadNetDroppedReason =
        "pad whose net was not carried into the shared footprint cell (a pad's net belongs to the " +
        "PLACEMENT; the tracks reaching it still carry theirs)";

    private const string FootprintTextSkipReason =
        "footprint reference/value text (a per-PLACEMENT designator, not the shared cell's artwork)";

    /// <summary>
    /// Reads <paramref name="text"/> at a destination resolution of <paramref name="dbuPerMicron"/>.
    /// </summary>
    public static ReadResult Read(string text, int dbuPerMicron)
    {
        // ── R-L4d-20, first pass: count entities in the RAW TEXT, before a node tree exists ─────────
        long census = CountEntities(text);
        if (census > EntityHardCeiling)
            return new ReadResult(null,
                $"This board carries about {census:N0} entities, above the {EntityHardCeiling:N0} " +
                "import ceiling — nothing was imported. Crop the board in the originating tool and " +
                "export the region you intend to simulate.");

        var parsed = PcbSexpr.Parse(text);
        if (parsed.Root is null)
            return new ReadResult(null, "This file contains no S-expression at all — it is not a board file.");
        if (parsed.Root.Tag != RootTag)
            return new ReadResult(null,
                $"This file's root is ({parsed.Root.Tag} …), not ({RootTag} …) — it is not a board file.");

        var board = new PcbBoard();
        board.Diagnostics.AddRange(parsed.Diagnostics);
        board.Version = parsed.Root.ChildAtom("version");

        ReadLayerTable(parsed.Root, board);
        ReadGeneral(parsed.Root, board);
        ReadStackup(parsed.Root, board);
        var netNames = ReadNetTable(parsed.Root);
        double defaultViaDrillMm = parsed.Root.Child("setup")?.ChildNum("via_drill") ?? 0;

        var ctx = new Ctx(dbuPerMicron, netNames, board, defaultViaDrillMm);

        foreach (var node in parsed.Root.Nodes)
            ReadTopLevel(node, ctx);

        // Shapes produced = board-level shapes plus every shape a placement actually renders, i.e. the
        // cell's own shape count once per placement — the number a user compares against the file, not
        // the deduplicated cell total.
        board.ShapesProduced = board.Shapes.Count;
        foreach (var p in board.Placements)
            if (board.FootprintCells.TryGetValue(p.ContentKey, out var cell))
                board.ShapesProduced += cell.Shapes.Count;

        return new ReadResult(board, null);
    }

    /// <summary>Cheap raw-text scan for <c>(tag</c> occurrences. Deliberately not a parse: R-L4d-20's
    /// whole point is to answer "is this too big" before a node tree is built.</summary>
    internal static long CountEntities(string text)
    {
        long count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '(') continue;
            int j = i + 1;
            while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
            int start = j;
            while (j < text.Length && !char.IsWhiteSpace(text[j]) && text[j] != '(' && text[j] != ')') j++;
            if (j == start) continue;
            var span = text.AsSpan(start, j - start);
            foreach (var tag in CensusTags)
                if (span.SequenceEqual(tag)) { count++; break; }
        }
        return count;
    }

    // ── Context ─────────────────────────────────────────────────────────────────────────────────

    private sealed class Ctx(int dbuPerMicron, IReadOnlyDictionary<int, string> netNames, PcbBoard board, double defaultViaDrillMm)
    {
        public int Dbu { get; } = dbuPerMicron;
        public PcbBoard Board { get; } = board;
        public double DefaultViaDrillMm { get; } = defaultViaDrillMm;
        private readonly IReadOnlyDictionary<int, string> _netNames = netNames;

        /// <summary>
        /// R-L4d-18: <see cref="LayoutShape.Net"/> is the net NAME, never the ordinal, and net 0 — the
        /// unassigned net — leaves it <b>null, not ""</b>.
        ///
        /// <para>Both spellings are live. Through the 20221018 epoch a <c>(net 7)</c> ordinal indexes a
        /// top-level <c>(net 7 "VDD")</c> table (and a pad writes <c>(net 7 "VDD")</c> inline, carrying
        /// both). At 20260206 the ordinal table is GONE and every reference is <c>(net "VDD")</c>
        /// directly — so "numeric means ordinal, quoted means name" is the dispatch, not the version.</para>
        /// </summary>
        public string? NetOf(PcbNode node)
        {
            var net = node.Child("net");
            if (net is null) return null;

            string? first = net.Atom(0);
            if (first is null) return null;

            if (int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ordinal))
            {
                if (ordinal == 0) return null;                       // the unassigned net
                if (net.Atom(1) is { Length: > 0 } inline) return inline;
                return _netNames.TryGetValue(ordinal, out var name) && name.Length > 0 ? name : null;
            }

            return first.Length == 0 ? null : first;
        }

        public void Unknown(string tag) => Bump(Board.UnknownTokenCounts, tag);

        /// <summary>Not imported at all.</summary>
        public void Skipped(string what) => Bump(Board.SkippedCounts, what);

        /// <summary>Imported, but not at full fidelity — see <see cref="PcbBoard.DegradedCounts"/>.</summary>
        public void Degraded(string what) => Bump(Board.DegradedCounts, what);

        private static void Bump(Dictionary<string, int> counter, string key)
            => counter[key] = counter.TryGetValue(key, out int n) ? n + 1 : 1;
    }

    // ── Header sections ─────────────────────────────────────────────────────────────────────────

    private static void ReadLayerTable(PcbNode root, PcbBoard board)
    {
        var layers = root.Child("layers");
        if (layers is null) { board.Diagnostics.Add("This file declares no (layers …) table."); return; }

        foreach (var row in layers.Nodes)
        {
            // A row's TAG is its ordinal: (0 "F.Cu" signal "top_layer").
            if (!int.TryParse(row.Tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ordinal)) continue;
            string canonical = row.Atom(0) ?? "";
            string type = row.Atom(1) ?? "user";
            string? user = row.Atom(2);
            if (canonical.Length == 0) continue;
            board.LayerTable.Add(new PcbLayerTableEntry(ordinal, canonical, type, user));
        }
    }

    private static void ReadGeneral(PcbNode root, PcbBoard board)
        => board.OverallThicknessMm = root.Child("general")?.ChildNum("thickness");

    private static void ReadStackup(PcbNode root, PcbBoard board)
    {
        var stackup = root.Child("setup")?.Child("stackup");
        if (stackup is null) return;   // R-L4d-6 — absent stays absent, and PcbImport says so

        var entries = new List<PcbStackupEntry>();
        foreach (var layer in stackup.Children("layer"))
        {
            string name = layer.Atom(0) ?? "";
            string type = layer.ChildAtom("type") ?? "";
            entries.Add(new PcbStackupEntry(
                name, type,
                layer.ChildNum("thickness"),
                layer.ChildNum("epsilon_r"),
                layer.ChildNum("loss_tangent")));
        }
        board.Stackup = entries;
    }

    private static Dictionary<int, string> ReadNetTable(PcbNode root)
    {
        var names = new Dictionary<int, string>();
        foreach (var net in root.Children("net"))
            if (net.Atom(0) is { } a && int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                names[n] = net.Atom(1) ?? "";
        return names;
    }

    // ── Layer specs and wildcards ───────────────────────────────────────────────────────────────

    /// <summary>
    /// R-L4d-16: expands one entry of a pad's <c>layers</c> list against the board's OWN table.
    ///
    /// <para>A hard-coded list cannot do this. At the 20171130 epoch the copper layers in a measured
    /// file are named <c>top_layer</c> and <c>bottom_layer</c> — neither ends in ".Cu" — so matching a
    /// name suffix finds nothing, and matching an ordinal range breaks at 20260206 where B.Cu moved from
    /// 31 to 2. The table's TYPE word is the only stable answer.</para>
    /// </summary>
    internal static List<PcbLayerTableEntry> ExpandLayerSpec(string spec, IReadOnlyList<PcbLayerTableEntry> table)
    {
        if (spec == "*") return [.. table];

        if (spec.StartsWith("*.", StringComparison.Ordinal))
        {
            string suffix = spec[1..];                                   // ".Cu", ".Mask", ".Paste"
            if (suffix == ".Cu") return [.. table.Where(e => e.IsCopper)];
            return [.. table.Where(e => e.CanonicalName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))];
        }

        if (spec.StartsWith("F&B.", StringComparison.Ordinal))
        {
            string suffix = "." + spec["F&B.".Length..];                 // "F&B.Cu" -> ".Cu"
            var pool = suffix == ".Cu"
                ? table.Where(e => e.IsCopper).ToList()
                : table.Where(e => e.CanonicalName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (pool.Count == 0) return [];
            var outer = new List<PcbLayerTableEntry> { pool.MinBy(e => e.Ordinal)! };
            var last = pool.MaxBy(e => e.Ordinal)!;
            if (!ReferenceEquals(outer[0], last)) outer.Add(last);
            return outer;
        }

        var exact = table.FirstOrDefault(e =>
            string.Equals(e.CanonicalName, spec, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.UserName, spec, StringComparison.OrdinalIgnoreCase));
        return exact is null ? [] : [exact];
    }

    // ── Top-level dispatch ──────────────────────────────────────────────────────────────────────

    private static void ReadTopLevel(PcbNode node, Ctx ctx)
    {
        switch (node.Tag)
        {
            case "net": return;                                   // the ordinal table, read already
            case "gr_line" or "gr_rect" or "gr_circle" or "gr_arc" or "gr_poly" or "gr_curve" or "bezier":
                ctx.Board.EntitiesRead++;
                ReadGraphic(node, ctx, ctx.Board.Shapes, originX: 0, originY: 0);
                return;
            case "gr_text":
                ctx.Board.EntitiesRead++;
                ReadText(node, ctx, ctx.Board.Shapes);
                return;
            case "segment":
                ctx.Board.EntitiesRead++;
                ReadSegment(node, ctx, ctx.Board.Shapes);
                return;
            case "arc":
                ctx.Board.EntitiesRead++;
                ReadTrackArc(node, ctx, ctx.Board.Shapes);
                return;
            case "via":
                ctx.Board.EntitiesRead++;
                ReadVia(node, ctx, ctx.Board.Shapes);
                return;
            case "zone":
                ctx.Board.EntitiesRead++;
                ReadZone(node, ctx);
                return;
            case "footprint" or "module":
                ctx.Board.EntitiesRead++;
                ReadFootprint(node, ctx);
                return;
        }

        if (SkippedEntities.Contains(node.Tag)) { ctx.Skipped(node.Tag); return; }
        if (IgnoredMetadata.Contains(node.Tag)) return;
        ctx.Unknown(node.Tag);
    }

    // ── Geometry ────────────────────────────────────────────────────────────────────────────────

    private static long Lx(PcbNode? at, Ctx ctx, long originX)
        => at?.Num(0) is { } v ? originX + PcbUnits.X(v, ctx.Dbu) : originX;

    private static long Ly(PcbNode? at, Ctx ctx, long originY)
        => at?.Num(1) is { } v ? originY + PcbUnits.Y(v, ctx.Dbu) : originY;

    /// <summary>R-L4d-1: <c>(width W)</c> and <c>(stroke (width W) …)</c> are the same quantity in two
    /// spellings, and BOTH occur in the 20260206 epoch — in the same file.</summary>
    private static long StrokeWidth(PcbNode node, Ctx ctx)
    {
        double? mm = node.ChildNum("width") ?? node.Child("stroke")?.ChildNum("width");
        return mm is { } w && w > 0 ? PcbUnits.Length(w, ctx.Dbu) : 0;
    }

    /// <summary>
    /// R-L4d-9, the highest-consequence silent error in this phase: an unfilled outline must NEVER
    /// become a filled region.
    ///
    /// <para>An ABSENT fill token reads as unfilled, and that is a deliberate one-way bias rather than
    /// a faithful reading of every epoch. At 20171130 a <c>gr_poly</c> states no fill at all and IS
    /// filled; at 20211014/20221018 the spellings are <c>none</c>/<c>yes</c>; at 20260206 they are
    /// <c>no</c>/<c>yes</c>. Guessing "filled" for the silent case would invent a copper pour that is
    /// not on the board and would then be meshed and simulated; guessing "unfilled" loses an outline's
    /// interior, which is visible, recoverable, and reported by count.</para>
    /// </summary>
    private static bool IsFilled(PcbNode node)
        => node.Child("fill") is { } fill && (fill.Atom(0) is "yes" or "solid" or "true");

    private static bool StatesFill(PcbNode node) => node.Child("fill") is not null;

    private static string LayerNameOf(PcbNode node) => node.ChildAtom("layer") ?? "";

    private static void ReadGraphic(PcbNode node, Ctx ctx, List<PcbImportedShape> into, long originX, long originY)
    {
        string kind = node.Tag.StartsWith("gr_", StringComparison.Ordinal) ? node.Tag[3..]
                    : node.Tag.StartsWith("fp_", StringComparison.Ordinal) ? node.Tag[3..]
                    : node.Tag == "bezier" ? "curve" : node.Tag;

        string layer = LayerNameOf(node);
        long width = StrokeWidth(node, ctx);
        string? net = ctx.NetOf(node);

        switch (kind)
        {
            case "line":
            {
                var s = node.Child("start"); var e = node.Child("end");
                if (s is null || e is null) return;
                Add(into, new PathShape
                {
                    Xy = [Lx(s, ctx, originX), Ly(s, ctx, originY), Lx(e, ctx, originX), Ly(e, ctx, originY)],
                    Width = width, End = PathEndStyle.Round, Net = net,
                }, layer);
                return;
            }
            case "rect":
            {
                var s = node.Child("start"); var e = node.Child("end");
                if (s is null || e is null) return;
                long x1 = Lx(s, ctx, originX), y1 = Ly(s, ctx, originY);
                long x2 = Lx(e, ctx, originX), y2 = Ly(e, ctx, originY);
                if (IsFilled(node))
                {
                    Add(into, new RectShape
                    {
                        X1 = Math.Min(x1, x2), Y1 = Math.Min(y1, y2),
                        X2 = Math.Max(x1, x2), Y2 = Math.Max(y1, y2), Net = net,
                    }, layer);
                }
                else
                {
                    // R-L4d-9 — the four edges as a stroked, closed path. Never a RectShape.
                    Add(into, new PathShape
                    {
                        Xy = [x1, y1, x2, y1, x2, y2, x1, y2, x1, y1],
                        Width = width, End = PathEndStyle.Round, Net = net,
                    }, layer);
                    if (!StatesFill(node)) ctx.Degraded("rect with no fill flag (imported as an outline)");
                }
                return;
            }
            case "circle":
            {
                var c = node.Child("center") ?? node.Child("centre"); var e = node.Child("end");
                if (c is null || e is null) return;
                long cx = Lx(c, ctx, originX), cy = Ly(c, ctx, originY);
                long ex = Lx(e, ctx, originX), ey = Ly(e, ctx, originY);
                long r = (long)Math.Round(Math.Sqrt((double)(ex - cx) * (ex - cx) + (double)(ey - cy) * (ey - cy)),
                    MidpointRounding.AwayFromZero);
                if (r <= 0) return;
                if (IsFilled(node))
                {
                    Add(into, new CircleShape { Cx = cx, Cy = cy, R = r, Net = net }, layer);
                }
                else
                {
                    // An annulus of the stroke's own width, expressed as a CLOSED circular centerline
                    // with Width — two 180-degree Arc edges, which is exact. §5's table words this as a
                    // CurveShape with an inner ring, but a Curve's Holes are by contract flat vertex
                    // lists (never their own edge list), so that spelling would have to FLATTEN the
                    // inner ring to a polygon and would make the unfilled circle the one outline in the
                    // table that is approximated. The unfilled rect and unfilled polygon above are both
                    // stroked paths; this keeps all three the same kind of object and exact.
                    Add(into, new PathShape
                    {
                        Xy = [cx + r, cy, cx - r, cy, cx + r, cy],
                        Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 1.0 },
                                 new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 1.0 }],
                        Width = width, End = PathEndStyle.Round, Net = net,
                    }, layer);
                }
                return;
            }
            case "arc":
            {
                if (TryArcPath(node, ctx, originX, originY, width, net) is { } arc)
                    Add(into, arc, layer);
                return;
            }
            case "poly":
            {
                var pts = ReadPoints(node.Child("pts"), ctx, originX, originY);
                if (pts.Count < 6) return;
                if (IsFilled(node))
                {
                    Add(into, new PolygonShape { Xy = [.. pts], Net = net }, layer);
                }
                else
                {
                    var closed = new List<long>(pts) { pts[0], pts[1] };
                    Add(into, new PathShape
                    {
                        Xy = [.. closed], Width = width, End = PathEndStyle.Round, Net = net,
                    }, layer);
                    if (!StatesFill(node)) ctx.Degraded("polygon with no fill flag (imported as an outline)");
                }
                return;
            }
            case "curve":
            {
                // (gr_curve (pts (xy p0)(xy c1)(xy c2)(xy p3))) — one cubic, control points inline.
                var pts = ReadPoints(node.Child("pts"), ctx, originX, originY);
                if (pts.Count < 8) return;
                var edge = new LayoutEdge
                {
                    Kind = EdgeKind.Cubic,
                    C1X = pts[2], C1Y = pts[3],
                    C2X = pts[4], C2Y = pts[5],
                };
                Add(into, new PathShape
                {
                    Xy = [pts[0], pts[1], pts[6], pts[7]], Edges = [edge],
                    Width = width, End = PathEndStyle.Round, Net = net,
                }, layer);
                return;
            }
            default:
                ctx.Unknown(node.Tag);
                return;
        }
    }

    /// <summary>
    /// Both arc spellings, distinguished by which token is PRESENT (R-L4d-1) — never by the file's
    /// version.
    ///
    /// <para><b>Three-point form</b> <c>(start)(mid)(end)</c>: the sweep's sign comes from the cross
    /// product of (mid − start) and (end − mid), computed in the already-Y-flipped frame so the sign is
    /// the one our own bulge convention means.</para>
    ///
    /// <para><b>Centre form</b> <c>(start CX CY)(end SX SY)(angle A)</c>: <c>start</c> is the CENTRE and
    /// <c>end</c> is the arc's first point — not a chord, which is why a reader that assumes the
    /// three-point layout draws a straight line through the middle of a rounded silkscreen. A is swept
    /// COUNTER-clockwise in the source's raw Y-down frame, which is the opposite sense from a placement
    /// angle in the same file. That is measured, not assumed: 17 arcs that appear in both the 20171130
    /// and 20211014 renderings of one board were converted under each sign and checked against the
    /// newer file's own <c>(mid …)</c> — 13 agreed with counter-clockwise and 0 with clockwise. Flipping
    /// Y negates it, so the sweep this method returns is −A.</para>
    /// </summary>
    private static PathShape? TryArcPath(PcbNode node, Ctx ctx, long originX, long originY, long width, string? net)
    {
        var start = node.Child("start"); var end = node.Child("end");
        if (start is null || end is null) return null;

        var mid = node.Child("mid");
        if (mid is not null)
        {
            long x0 = Lx(start, ctx, originX), y0 = Ly(start, ctx, originY);
            long xm = Lx(mid, ctx, originX), ym = Ly(mid, ctx, originY);
            long x1 = Lx(end, ctx, originX), y1 = Ly(end, ctx, originY);
            double? bulge = BulgeFromThreePoints(x0, y0, xm, ym, x1, y1);
            if (bulge is null)
                return new PathShape { Xy = [x0, y0, x1, y1], Width = width, End = PathEndStyle.Round, Net = net };
            return new PathShape
            {
                Xy = [x0, y0, x1, y1],
                Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = bulge.Value }],
                Width = width, End = PathEndStyle.Round, Net = net,
            };
        }

        var angle = node.Child("angle");
        if (angle?.Num(0) is not { } degrees) return null;

        long cx = Lx(start, ctx, originX), cy = Ly(start, ctx, originY);
        long px = Lx(end, ctx, originX), py = Ly(end, ctx, originY);
        double sweep = -degrees * Math.PI / 180.0;            // CCW in the source's Y-down frame
        double dx = px - cx, dy = py - cy;
        double ex = cx + dx * Math.Cos(sweep) - dy * Math.Sin(sweep);
        double ey = cy + dx * Math.Sin(sweep) + dy * Math.Cos(sweep);

        return new PathShape
        {
            Xy = [px, py,
                  (long)Math.Round(ex, MidpointRounding.AwayFromZero),
                  (long)Math.Round(ey, MidpointRounding.AwayFromZero)],
            Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = LayoutArc.ToBulge(sweep) }],
            Width = width, End = PathEndStyle.Round, Net = net,
        };
    }

    /// <summary>
    /// Signed bulge for the circular arc through three points, or null when they are collinear.
    /// <see cref="LayoutArc.ToBulge"/> owns the sweep→bulge half of the convention — this method never
    /// re-derives <c>tan(sweep/4)</c> itself (§5's own "reuse rather than deriving a second time").
    /// </summary>
    internal static double? BulgeFromThreePoints(long x0, long y0, long xm, long ym, long x1, long y1)
    {
        double ax = xm - x0, ay = ym - y0, bx = x1 - xm, by = y1 - ym;
        double cross = ax * by - ay * bx;
        if (Math.Abs(cross) < 1e-9) return null;

        // Circumcentre of the three points.
        double d = 2.0 * ((double)x0 * (ym - y1) + (double)xm * (y1 - y0) + (double)x1 * (y0 - ym));
        if (Math.Abs(d) < 1e-9) return null;
        double sq0 = (double)x0 * x0 + (double)y0 * y0;
        double sqm = (double)xm * xm + (double)ym * ym;
        double sq1 = (double)x1 * x1 + (double)y1 * y1;
        double ux = (sq0 * (ym - y1) + sqm * (y1 - y0) + sq1 * (y0 - ym)) / d;
        double uy = (sq0 * (x1 - xm) + sqm * (x0 - x1) + sq1 * (xm - x0)) / d;

        double a0 = Math.Atan2(y0 - uy, x0 - ux);
        double a1 = Math.Atan2(y1 - uy, x1 - ux);
        double sweep = a1 - a0;
        while (sweep <= -Math.PI) sweep += 2 * Math.PI;
        while (sweep > Math.PI) sweep -= 2 * Math.PI;
        // A sweep past 180 degrees has the same chord and the same |sweep| as its complement — the
        // cross product is what says which side the middle point is on, so it, not the wrap, decides.
        if (cross > 0 && sweep < 0) sweep += 2 * Math.PI;
        if (cross < 0 && sweep > 0) sweep -= 2 * Math.PI;
        return LayoutArc.ToBulge(sweep);
    }

    private static List<long> ReadPoints(PcbNode? pts, Ctx ctx, long originX, long originY)
    {
        var result = new List<long>();
        if (pts is null) return result;
        foreach (var xy in pts.Nodes)
        {
            if (xy.Tag != "xy") continue;
            if (xy.Num(0) is not { } x || xy.Num(1) is not { } y) continue;
            result.Add(originX + PcbUnits.X(x, ctx.Dbu));
            result.Add(originY + PcbUnits.Y(y, ctx.Dbu));
        }
        return result;
    }

    private static void ReadText(PcbNode node, Ctx ctx, List<PcbImportedShape> into)
    {
        string text = node.Atom(0) ?? "";
        var at = node.Child("at");
        if (at is null) return;
        double heightMm = node.Child("effects")?.Child("font")?.ChildNum("size", 1) ?? 1.0;
        double degrees = at.Num(2) ?? 0;


        var (hAlign, vAlign) = ReadJustification(node, ctx);

        Add(into, new LabelShape
        {
            X = Lx(at, ctx, 0), Y = Ly(at, ctx, 0),
            Text = text,
            Height = PcbUnits.Length(heightMm, ctx.Dbu),
            RotationDegrees = LayoutAngle.Normalize(PcbUnits.Angle(degrees)),
            IsPort = false,          // §5: never a port — a board's text is annotation, not connectivity
            HAlign = hAlign, VAlign = vAlign,
        }, LayerNameOf(node));
    }

    /// <summary>
    /// <c>(effects (justify …))</c> → the anchor <see cref="LabelShape.X"/>/<see cref="LabelShape.Y"/>
    /// name. <b>This format's default is CENTRED on both axes, not left-of-baseline</b>, so a reader
    /// that leaves the fields null puts every unjustified string half its own width to the right of
    /// where the board says it goes — and, for the very common <c>(justify left top)</c>, a full cap
    /// height above its own row. Measured on a stackup table drawn on a user layer: 119 strings, every
    /// one of them a row out of place.
    ///
    /// <para>The Y flip does NOT swap top and bottom. <c>top</c> means "the text hangs below this
    /// point" in the source's Y-down frame; after the flip the point is still the text's own top edge,
    /// which is what <see cref="LabelVAlign.Top"/> means in layout's Y-up frame. Only the anchor's
    /// COORDINATE moves, never which corner of the string it names.</para>
    ///
    /// <para><c>mirror</c> is a back-side rendering flag, not an anchor, and layout has no mirrored
    /// text — reported once, per R-L4d-1's rule that a degraded import is stated rather than silent.</para>
    /// </summary>
    private static (LabelHAlign?, LabelVAlign?) ReadJustification(PcbNode node, Ctx ctx)
    {
        var justify = node.Child("effects")?.Child("justify");
        var words = new HashSet<string>(justify?.Atoms ?? [], StringComparer.OrdinalIgnoreCase);

        var h = words.Contains("left")  ? LabelHAlign.Left
              : words.Contains("right") ? LabelHAlign.Right
              : LabelHAlign.Center;

        // ── `mirror` reverses the text's own x axis, which swaps which END of the string is at the
        // anchor ──────────────────────────────────────────────────────────────────────────────────
        //
        // Back-side text is stored mirrored so it reads correctly with the board turned over; in the
        // file's own front-view coordinates its glyphs run BACKWARD from the anchor. Layout has no
        // mirrored text and reversed glyphs would be worse than none, so the glyphs come in forwards —
        // but the string must still OCCUPY the side of the anchor the board put it on, and that side is
        // the reflected one. Owner report, 2026-08-25: a `(justify left mirror)` annotation at 225 deg
        // rendered on the wrong side of its own anchor, which is this swap, missing.
        //
        // Only the horizontal half swaps: a board flip is an X mirror, so top and bottom are untouched.
        if (words.Contains("mirror"))
        {
            h = h switch { LabelHAlign.Left => LabelHAlign.Right, LabelHAlign.Right => LabelHAlign.Left, _ => h };
            ctx.Degraded(
                "mirrored text (imported unmirrored — the glyphs read forwards, and the anchor keeps " +
                "the side of the string the board put there)");
        }
        var v = words.Contains("top")    ? LabelVAlign.Top
              : words.Contains("bottom") ? LabelVAlign.Bottom
              : LabelVAlign.Middle;
        return (h, v);
    }

    private static void ReadSegment(PcbNode node, Ctx ctx, List<PcbImportedShape> into)
    {
        var s = node.Child("start"); var e = node.Child("end");
        if (s is null || e is null) return;
        Add(into, new PathShape
        {
            Xy = [Lx(s, ctx, 0), Ly(s, ctx, 0), Lx(e, ctx, 0), Ly(e, ctx, 0)],
            Width = node.ChildNum("width") is { } w ? PcbUnits.Length(w, ctx.Dbu) : 0,
            End = PathEndStyle.Round,
            Net = ctx.NetOf(node),
        }, LayerNameOf(node));
    }

    private static void ReadTrackArc(PcbNode node, Ctx ctx, List<PcbImportedShape> into)
    {
        long width = node.ChildNum("width") is { } w ? PcbUnits.Length(w, ctx.Dbu) : 0;
        if (TryArcPath(node, ctx, 0, 0, width, ctx.NetOf(node)) is { } arc)
            Add(into, arc, LayerNameOf(node));
    }

    /// <summary>
    /// R-L4d-10, and read <see cref="ViaShape"/>'s own doc comment before touching either field:
    /// <see cref="LayoutShape.Layer"/> is the BARREL (the drill), <see cref="ViaShape.LandingLayer"/> is
    /// the PAD. Backwards, this "produces a GDSII/DXF export that looks plausible and puts copper where
    /// the hole should be".
    /// </summary>
    private static void ReadVia(PcbNode node, Ctx ctx, List<PcbImportedShape> into)
    {
        var at = node.Child("at");
        if (at is null) return;

        double sizeMm = node.ChildNum("size") ?? 0;
        // The drill is OPTIONAL — a 20171130-epoch file states it once, in (setup (via_drill …)), and
        // never on the via itself. Falling back to 0 would export a via with no hole in it.
        double drillMm = node.ChildNum("drill") ?? ctx.DefaultViaDrillMm;

        var span = node.Child("layers");
        var specs = span is null ? [] : span.Atoms.ToList();
        string padLayer = specs.Count > 0 ? specs[0] : "";

        bool through = true;
        if (specs.Count >= 2)
        {
            var copper = ctx.Board.LayerTable.Where(e => e.IsCopper).ToList();
            var from = copper.FirstOrDefault(e => Matches(e, specs[0]));
            var to = copper.FirstOrDefault(e => Matches(e, specs[1]));
            through = from is not null && to is not null
                      && from.Ordinal == copper.Min(e => e.Ordinal)
                      && to.Ordinal == copper.Max(e => e.Ordinal);
            padLayer = specs[0];
        }
        // The kind word is a bare atom in every epoch measured — but tolerate the childless-list
        // spelling too rather than silently treating a blind via as a through one (R-L4d-1's own habit).
        if (IsFlagged(node, "blind") || IsFlagged(node, "micro") || IsFlagged(node, "buried")) through = false;

        if (!through)
            // R-L4d-10: the model carries ONE landing layer, so a blind/buried via cannot be expressed.
            // Report it by count, naming where it was put, rather than pretending otherwise.
            ctx.Degraded($"blind/buried via placed on its top span layer \"{padLayer}\" only " +
                        "(the model carries one landing layer)");

        Add(into, new ViaShape
        {
            X = Lx(at, ctx, 0), Y = Ly(at, ctx, 0),
            PadSize = PcbUnits.Length(sizeMm, ctx.Dbu),
            DrillSize = PcbUnits.Length(drillMm, ctx.Dbu),
            LandingLayer = null,                 // set by PcbImport once the layer keys are reconciled
            Net = ctx.NetOf(node),
        }, DrillLayerName, padLayerName: padLayer);
    }

    private static bool IsFlagged(PcbNode node, string flag)
        => node.HasAtom(flag) || node.Child(flag) is not null;

    private static bool Matches(PcbLayerTableEntry e, string spec)
        => string.Equals(e.CanonicalName, spec, StringComparison.OrdinalIgnoreCase)
        || string.Equals(e.UserName, spec, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// R-L4d-11/12/13. The outline is the author's REQUEST; the <c>filled_polygon</c> nodes are the
    /// copper that exists, and for EM only the copper exists.
    ///
    /// <para><b>Islands and holes, as measured (R-L4d-14).</b> Every <c>filled_polygon</c> is ONE
    /// positively-oriented outline. A hole inside it is not a second ring: it is cut into that single
    /// outline by a zero-width slit, so the outline runs into the hole, around it the other way, and
    /// back out along a coincident edge. That is measured, not inferred — on a real ground pour the
    /// vertices that repeat bound sub-loops of NEGATIVE signed area (−103.713 mm² and −14.559 mm²,
    /// against a whole-outline area of +10288.285 mm²), and the vertex before the first occurrence
    /// equals the vertex after the second, which is the slit's doubled edge. Islands are separate
    /// <c>filled_polygon</c> nodes at the 20260206 epoch (3 of them) and are merged into ONE node at
    /// 20171130/20211014/20221018 (one 15,410-point outline, 192 repeated vertices) — by the same slit
    /// construction.</para>
    ///
    /// <para>So the fill imports VERBATIM as one <see cref="PolygonShape"/> per node, with no holes and
    /// no matching: the slit outline is exactly the copper the originating tool draws. Nothing here
    /// needs <c>PolygonShape.Holes</c> and nothing needs per-polygon hole matching.</para>
    /// </summary>
    private static void ReadZone(PcbNode node, Ctx ctx)
    {
        string? net = ctx.NetOf(node);

        if (node.Child("keepout") is not null)
        {
            ctx.Skipped("keepout zone (not copper)");
            return;
        }

        var fills = node.Children("filled_polygon").ToList();
        if (fills.Count == 0)
        {
            // R-L4d-12 — never fall back to the outline. It includes every area the fill would have
            // cleared around pads and neighbouring nets, so importing it as copper shorts the board.
            string layer = LayerNameOf(node);
            ctx.Skipped($"unfilled zone on net \"{net ?? "(none)"}\", layer \"{layer}\" " +
                        "(fill the board and re-import — the outline is NOT imported as copper)");
            return;
        }

        foreach (var fill in fills)
        {
            var pts = ReadPoints(fill.Child("pts"), ctx, 0, 0);
            if (pts.Count < 6) continue;
            // A filled_polygon states its own layer from the 20211014 epoch on; before that it inherits
            // the zone's.
            string layer = fill.ChildAtom("layer") ?? LayerNameOf(node);
            Add(ctx.Board.Shapes, new PolygonShape { Xy = [.. pts], Net = net }, layer);
        }
    }

    // ── Footprints ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-L4d-15: one cell per distinct footprint DEFINITION, N instances.
    ///
    /// <para><b>There is no mirror to compose, and that is a measured refutation of the brief's own
    /// premise.</b> §7 expects a back-layer footprint to be "a mirror combined with that angle", to be
    /// reconciled against <c>LayoutInstanceTransform</c>'s convention. It is not. Comparing a board's
    /// back-side placement against the same part's library original, the flip is already BAKED into the
    /// stored child data: every local Y is negated (X untouched) and every child layer is rewritten to
    /// its back-side counterpart (F.SilkS → B.SilkS, F.Cu → B.Cu). A pad polygon reads
    /// (1,0)(0.5,0.75)(−0.5,0.75)(−0.5,−0.75)(0.5,−0.75) in the library and
    /// (1,0)(0.5,−0.75)(−0.5,−0.75)(−0.5,0.75)(0.5,0.75) on the board — y → −y, exactly. Applying our
    /// own <c>MirrorX</c> on top of that would flip the whole board a second time.</para>
    ///
    /// <para><b>Positions are footprint-local; a pad's ANGLE is absolute.</b> Also measured: the same
    /// library part placed at 0° and at 180° stores byte-identical child coordinates (checked across six
    /// parts and four distinct angles) but stores pad angles of 0 and 180 respectively. A cell keyed on
    /// the absolute angle would therefore mint a second cell per placement angle and defeat the whole
    /// point of R-L4d-15 — so the pad's LOCAL angle (stored − footprint) is what goes in the cell.</para>
    /// </summary>
    private static void ReadFootprint(PcbNode node, Ctx ctx)
    {
        string library = node.Atom(0) ?? "footprint";
        var at = node.Child("at");
        long x = Lx(at, ctx, 0), y = Ly(at, ctx, 0);
        double placementDegrees = PcbUnits.Angle(at?.Num(2) ?? 0);

        var cell = new PcbFootprintCell { LibraryName = library };

        foreach (var child in node.Nodes)
        {
            switch (child.Tag)
            {
                case "fp_line" or "fp_rect" or "fp_circle" or "fp_arc" or "fp_poly" or "fp_curve":
                    ctx.Board.EntitiesRead++;
                    ReadGraphic(child, ctx, cell.Shapes, 0, 0);
                    break;
                case "pad":
                    ctx.Board.EntitiesRead++;
                    ReadPad(child, ctx, cell, placementDegrees);
                    break;
                case "fp_text":
                    ctx.Skipped(FootprintTextSkipReason);
                    break;
                default:
                    if (SkippedEntities.Contains(child.Tag)) ctx.Skipped(child.Tag);
                    else if (!IgnoredMetadata.Contains(child.Tag)) ctx.Unknown(child.Tag);
                    break;
            }
        }

        cell.ContentKey = ContentKeyOf(cell);
        ctx.Board.FootprintCells.TryAdd(cell.ContentKey, cell);
        ctx.Board.Placements.Add(new PcbPlacement(
            cell.ContentKey, x, y, placementDegrees, ReferenceOf(node)));
    }

    /// <summary>The part's reference designator, whichever spelling the epoch uses — a
    /// <c>(property "Reference" "R3")</c> from 20211014 on, an <c>(fp_text reference "R3" …)</c>
    /// before that. Used only to name the cell folder readably; nothing depends on it.</summary>
    private static string? ReferenceOf(PcbNode node)
    {
        foreach (var p in node.Children("property"))
            if (p.Atom(0) == "Reference") return p.Atom(1);
        foreach (var t in node.Children("fp_text"))
            if (t.Atom(0) == "reference") return t.Atom(1);
        return null;
    }

    private static void ReadPad(PcbNode node, Ctx ctx, PcbFootprintCell cell, double placementDegrees)
    {
        string number = node.Atom(0) ?? "";
        string type = node.Atom(1) ?? "";
        string shape = node.Atom(2) ?? "";

        var at = node.Child("at");
        long ax = Lx(at, ctx, 0), ay = Ly(at, ctx, 0);
        // The stored pad angle is ABSOLUTE (measured — see ReadFootprint). Subtract the placement to get
        // the cell-local one, so every placement of a part yields the SAME cell.
        double padDegrees = LayoutAngle.Normalize(PcbUnits.Angle(at?.Num(2) ?? 0) - placementDegrees);

        // ── (drill … (offset …)) moves the COPPER, not the hole ─────────────────────────────────
        //
        // The pad's (at …) is where the HOLE goes; the pad's shape sits at (at + offset), with the
        // offset turned by the pad's own orientation. Measured from the originating tool's own plot:
        // three trapezoid pads carrying (offset 0 +/-1.905) plot their copper 1.905 mm off the
        // footprint origin while their drill stays on it.
        //
        // This used to be reported as "not expressible — the drill is placed at the pad centre", which
        // had it backwards: circuitRF carries the hole and the copper as SEPARATE shapes, so an offset
        // hole is not merely expressible, it is the natural representation. What was wrong was putting
        // the copper on the hole.
        long px = ax, py = ay;
        if (node.Child("drill")?.Child("offset") is { } off && off.Num(0) is { } offX && off.Num(1) is { } offY)
        {
            double ox = PcbUnits.Length(offX, ctx.Dbu), oy = -(double)PcbUnits.Length(offY, ctx.Dbu);
            var (cosP, sinP) = LayoutAngle.CosSin(LayoutAngle.Normalize(padDegrees));
            px = ax + (long)Math.Round(ox * cosP - oy * sinP, MidpointRounding.AwayFromZero);
            py = ay + (long)Math.Round(ox * sinP + oy * cosP, MidpointRounding.AwayFromZero);
        }

        var size = node.Child("size");
        long sx = size?.Num(0) is { } w ? PcbUnits.Length(w, ctx.Dbu) : 0;
        long sy = size?.Num(1) is { } h ? PcbUnits.Length(h, ctx.Dbu) : 0;
        string? net = ctx.NetOf(node);

        var hole = BuildHoleShape(node, type, ax, ay, padDegrees, ctx);

        var specs = node.Child("layers")?.Atoms.ToList() ?? [];
        var expanded = new List<PcbLayerTableEntry>();
        var nonCopperEntries = new List<PcbLayerTableEntry>();
        foreach (var spec in specs)
            foreach (var entry in ExpandLayerSpec(spec, ctx.Board.LayerTable))
                if (entry.IsCopper) { if (!expanded.Contains(entry)) expanded.Add(entry); }
                else if (!nonCopperEntries.Contains(entry)) nonCopperEntries.Add(entry);

        if (nonCopperEntries.Count > 0 && expanded.Count > 0)
            // A mask or paste aperture ON A PAD THAT ALSO HAS COPPER is the copper EXPANDED by a margin
            // this reader does not read, so copying the pad's own outline onto those layers would invent
            // artwork that is not on the board — the same class of error R-L4d-9 forbids for a copper
            // pour. Reported, not silent.
            ctx.Skipped("pad aperture on a non-copper layer (mask/paste openings are not generated)");

        if (expanded.Count == 0 && specs.Count > 0 && nonCopperEntries.Count == 0)
            ctx.Skipped($"pad on layer(s) \"{string.Join(' ', specs)}\" that this board's own layer table does not declare");

        foreach (var entry in expanded)
        {
            // R-L4d-15, and this is the subtle half of it: a pad's NET is a property of the PLACEMENT,
            // not of the library part's artwork — R3 and R4 are the same resistor footprint touching
            // different nets. Carrying it into the shared cell would make every placement's cell
            // content-distinct and mint one cell per placement, which is exactly the
            // 400-copies-of-geometry outcome R-L4d-15 exists to prevent (measured: a real 63-part board
            // produced 57 "distinct" cells for 21 actual library parts). circuitRF's layout model has
            // nowhere to put per-placement pad connectivity, so the pad carries none — the tracks that
            // reach it still carry theirs, which is what EM port setup and DRC's NetScope actually read.
            foreach (var padShape in BuildPadShapes(node, shape, px, py, sx, sy, padDegrees, net: null, ctx))
                foreach (var drilled in Drill(padShape, hole))
                    Add(cell.Shapes, drilled, entry.CanonicalName);
        }
        if (net is not null && expanded.Count > 0) ctx.Degraded(PadNetDroppedReason);

        // ── A pad with NO copper anywhere ────────────────────────────────────────────────────────
        //
        // The margin argument above does not apply here: there is no copper to expand from, so the pad
        // IS the aperture and its own outline is exactly what the board carries. Dropping it produced a
        // cell holding nothing but a courtyard — owner report, 2026-08-25: "the ChamfnRRect and Circ
        // cells are empty. Is there supposed to be something there?" There was: a solder-mask opening
        // and a stencil aperture respectively, both of them real board content.
        if (expanded.Count == 0 && nonCopperEntries.Count > 0)
        {
            foreach (var entry in nonCopperEntries)
                foreach (var padShape in BuildPadShapes(node, shape, px, py, sx, sy, padDegrees, net: null, ctx))
                    foreach (var drilled in Drill(padShape, hole))
                        Add(cell.Shapes, drilled, entry.CanonicalName);
            ctx.Degraded(
                "pad with no copper on any layer (imported as the mask/paste aperture it is, at the " +
                "pad's own outline — this format states no margin to expand)");
        }

        // R-L4d-17: pads populate the cell's pin list — what makes merely-IMPORTED artwork carry
        // connectivity, and what lets the EM port picker select a board's own connection points.
        if (expanded.Count > 0)
        {
            // Facing = the pad's long axis; width = its extent ACROSS that. The format states no port
            // direction, so this is the one fact the geometry itself supplies rather than an invention.
            bool longIsX = sx >= sy;
            cell.Pins.Add(new PcbImportedPin(new LayoutPin
            {
                Name = number,
                X = ax, Y = ay,
                WidthDbu = longIsX ? sy : sx,
                OutwardDeg = LayoutAngle.Normalize(padDegrees + (longIsX ? 0 : 90)),
                Layer = default,                       // reconciled by PcbImport
            }, expanded[0].CanonicalName));
        }

        if (hole is null) return;

        // ── The barrel record ───────────────────────────────────────────────────────────────────
        //
        // The HOLE itself is already visible: it was subtracted from the pad's copper above, which is
        // both what the board physically is and what the originating tool draws — metal with the hole
        // missing from it. This ViaShape is the record that a hole exists, for export and DRC; its pad
        // and drill are deliberately the same size, because the pad's copper is its own shape and this
        // must not invent a second, round one on top of it.
        //
        // Owner report, 2026-08-25: "the TrapH renders as a large elongated hole in [the originating tool],
        // but in
        // circuitRF it doesn't look like a hole with missing metal." It did not, and the cause was here:
        // circuitRF renders a via as an ANNULUS — pad filled, barrel punched out — so a via whose pad
        // and drill are the same size cancels to nothing and the hole became invisible the moment this
        // stopped inventing an oversized landing disc. Subtracting the hole from the real copper is the
        // fix that is also correct, rather than restoring a disc that was never there.
        long holeAcross = hole switch
        {
            CircleShape c => c.R * 2,
            PathShape p => p.Width,
            _ => 0,
        };
        if (holeAcross <= 0) return;

        // A slot's own outline, on the drill layer, so the hole reads as a slot even where there is no
        // copper to have removed it from. A round hole needs no such marker — the ViaShape IS it.
        if (hole is PathShape slot)
        {
            Add(cell.Shapes, new PathShape { Xy = [.. slot.Xy], Width = slot.Width, End = slot.End }, DrillLayerName);
            ctx.Degraded(
                "oval pad drill (drawn as the slot it is; the via primitive itself carries only the " +
                "slot's width, so an export writes a round hole of that width)");
        }

        Add(cell.Shapes, new ViaShape
        {
            X = ax, Y = ay,                   // the HOLE is at the pad's own (at …); px/py carry its offset copper
            PadSize = holeAcross,
            DrillSize = holeAcross,
            Net = null,                       // placement data — see the note on the pad artwork above
        }, DrillLayerName);
    }

    /// <summary>
    /// The drilled hole's own outline in cell-local coordinates, or null when the pad has none — a
    /// circle for a plain drill, a stadium for <c>(drill oval W H)</c>.
    ///
    /// <para><c>(drill oval W H)</c> puts a WORD at atom 0, and <see cref="PcbNode.Num"/> counts every
    /// atom rather than only numeric ones — so reading from index 0 returned null for <c>"oval"</c> and
    /// H was never read at all. The <c>Max(0, W)</c> that followed equalled W and looked correct.</para>
    /// </summary>
    private static LayoutShape? BuildHoleShape(PcbNode node, string type, long ax, long ay, double padDegrees, Ctx ctx)
    {
        var drill = node.Child("drill");
        if (drill is null) return null;
        if (type is not ("thru_hole" or "np_thru_hole")) return null;

        bool oval = drill.HasAtom("oval");
        int firstNum = oval ? 1 : 0;
        double d0 = drill.Num(firstNum) ?? 0;
        double d1 = drill.Num(firstNum + 1) ?? d0;

        // A slot's BARREL is its NARROW dimension — the hole is that wide everywhere along its length,
        // and it is the dimension a fab reads. This used to take the larger, which is both the wrong
        // shape and too wide across the slot.
        bool isSlot = oval && Math.Abs(d0 - d1) > 1e-9;
        double acrossMm = isSlot ? Math.Min(d0, d1) : Math.Max(d0, d1);
        if (acrossMm <= 0) return null;
        long across = PcbUnits.Length(acrossMm, ctx.Dbu);

        if (!isSlot) return new CircleShape { Cx = ax, Cy = ay, R = across / 2 };

        long span = PcbUnits.Length(Math.Abs(d0 - d1), ctx.Dbu);
        double along = padDegrees + (d0 >= d1 ? 0 : 90);
        var (cosS, sinS) = LayoutAngle.CosSin(LayoutAngle.Normalize(along));
        long hx = (long)Math.Round(span / 2.0 * cosS, MidpointRounding.AwayFromZero);
        long hy = (long)Math.Round(span / 2.0 * sinS, MidpointRounding.AwayFromZero);
        return new PathShape
        {
            Xy = [ax - hx, ay - hy, ax + hx, ay + hy],
            Width = across, End = PathEndStyle.Round,
        };
    }

    /// <summary>
    /// <paramref name="pad"/> with <paramref name="hole"/> taken out of it — a through-hole pad's copper
    /// is an annulus, not a disc, because the hole really is drilled through it. Returns the pad
    /// unchanged when there is no hole, and never returns nothing for a hole that swallows the pad
    /// (an over-drill is the file's own business, and an empty result would silently delete the pad).
    /// </summary>
    private static IReadOnlyList<LayoutShape> Drill(LayoutShape pad, LayoutShape? hole)
    {
        if (hole is null) return [pad];
        var cut = LayoutBooleans.Difference([pad, hole], tech: null).Shapes;
        return cut.Count == 0 ? [pad] : cut;
    }

    /// <summary>§5/R-L4d-16's pad table. A <c>custom</c> pad's <c>(primitives …)</c> are the same
    /// graphic tokens as §5, so they go through <see cref="ReadGraphic"/> — never a second reader.</summary>
    private static List<LayoutShape> BuildPadShapes(
        PcbNode node, string shape, long px, long py, long sx, long sy, double degrees, string? net, Ctx ctx)
    {
        var one = BuildPadShape(node, shape, px, py, sx, sy, degrees, net, ctx, out var extra);
        var result = new List<LayoutShape>(1 + (extra?.Count ?? 0));
        if (one is not null) result.Add(one);
        if (extra is not null) result.AddRange(extra);
        return result;
    }

    private static LayoutShape? BuildPadShape(
        PcbNode node, string shape, long px, long py, long sx, long sy, double degrees, string? net, Ctx ctx,
        out List<LayoutShape>? extra)
    {
        extra = null;
        bool rotated = !LayoutAngle.TryCardinal(LayoutAngle.Normalize(degrees), out var cardinal) || cardinal is not LayoutRotation.R0;
        bool swap = LayoutAngle.TryCardinal(LayoutAngle.Normalize(degrees), out var c2) && c2 is LayoutRotation.R90 or LayoutRotation.R270;

        switch (shape)
        {
            case "circle":
                return new CircleShape { Cx = px, Cy = py, R = Math.Max(sx, sy) / 2, Net = net };

            case "rect":
            {
                if (!rotated)
                    return new RectShape { X1 = px - sx / 2, Y1 = py - sy / 2, X2 = px + sx / 2, Y2 = py + sy / 2, Net = net };
                if (swap)
                    return new RectShape { X1 = px - sy / 2, Y1 = py - sx / 2, X2 = px + sy / 2, Y2 = py + sx / 2, Net = net };
                // A pad carrying its own non-cardinal angle cannot be an axis-aligned RectShape (§5's
                // own "or PolygonShape when the pad carries its own angle" — and the same reason
                // LayoutCoordinateWalk refuses a Rect under a rotating transform, L3d).
                return RotatedRect(px, py, sx, sy, degrees, net);
            }

            case "oval":
            {
                long width = Math.Min(sx, sy);
                long length = Math.Abs(sx - sy);
                if (length == 0)
                    return new CircleShape { Cx = px, Cy = py, R = width / 2, Net = net };
                double along = (sx >= sy ? 0 : 90) + degrees;
                double rad = along * Math.PI / 180.0;
                long hx = (long)Math.Round(length / 2.0 * Math.Cos(rad), MidpointRounding.AwayFromZero);
                long hy = (long)Math.Round(length / 2.0 * Math.Sin(rad), MidpointRounding.AwayFromZero);
                return new PathShape
                {
                    Xy = [px - hx, py - hy, px + hx, py + hy],
                    Width = width, End = PathEndStyle.Round, Net = net,
                };
            }

            case "roundrect":
            {
                double ratio = node.ChildNum("roundrect_rratio") ?? 0.25;
                long radius = (long)Math.Round(ratio * Math.Min(sx, sy), MidpointRounding.AwayFromZero);

                // (chamfer …) names corners that are CUT rather than rounded, at
                // chamfer_ratio x the pad's SHORT side. A rounded rectangle rounds all four equally and
                // is axis-aligned by type, so neither a chamfer nor a non-cardinal angle fits in one —
                // both build the general boundary instead, straight edges plus quarter-circle arc edges,
                // which is exactly what the originating tool plots.
                var chamferNode = node.Child("chamfer");
                long chamfer = chamferNode is null ? 0
                    : (long)Math.Round((node.ChildNum("chamfer_ratio") ?? 0.0) * Math.Min(sx, sy), MidpointRounding.AwayFromZero);

                if (chamferNode is not null || (rotated && !swap && radius > 0))
                    return ChamferedRoundRect(px, py, sx, sy, radius, chamfer, chamferNode, degrees, net);

                if (!rotated)
                    return new RoundedRectShape { X1 = px - sx / 2, Y1 = py - sy / 2, X2 = px + sx / 2, Y2 = py + sy / 2, CornerRadius = radius, Net = net };
                if (swap)
                    return new RoundedRectShape { X1 = px - sy / 2, Y1 = py - sx / 2, X2 = px + sy / 2, Y2 = py + sx / 2, CornerRadius = radius, Net = net };
                return RotatedRect(px, py, sx, sy, degrees, net);
            }

            case "trapezoid":
            {
                // ── (rect_delta DX DY): DX moves the Y extents, DY the X extents ─────────────────
                //
                // Crossed on purpose, and it is the format's own convention rather than a typo: DX
                // lengthens one vertical side and shortens the other by the same amount, so the pad
                // TAPERS along Y; DY does the same to the horizontal sides. Each therefore reaches
                // BEYOND the nominal (size …) on one side and inside it on the other — a trapezoid pad
                // legitimately overhangs its own size box, which is what it is for.
                //
                // <b>Measured from the originating tool's own plot, at the FLASH.</b> The sign on the
                // near pair had been inverted, which slopes both ends the same way: a parallelogram
                // instead of a taper, with the same area and the same bounding box on one axis, so
                // nothing but the shape itself shows it (owner report, 2026-08-25, comparing renderings:
                // "the trapezoid renderings look different"). Un-rotating two plotted flashes of known
                // pads gives the pad-local corners directly, which is the evidence here — an aperture
                // definition alone is not, because matching apertures to pads is a guess:
                //
                //   (size 5 10) (rect_delta 0.635 0) at 33 deg
                //       flash (120.7994,-98.8212) ... about (120,-93)  ->  local (-2.5,-5.3175) ...
                //   (size 5 10) (rect_delta 0 0.635) at 33 deg
                //       flash (112.8602,-98.7279) ... about (112.5,-93) ->  local (-2.8175,-5.0) ...
                var delta = node.Child("rect_delta");
                long dx = delta?.Num(0) is { } a ? PcbUnits.Length(a, ctx.Dbu) : 0;
                long dy = delta?.Num(1) is { } b ? PcbUnits.Length(b, ctx.Dbu) : 0;
                long hxr = sx / 2, hyr = sy / 2;
                var pts = new (double X, double Y)[]
                {
                    (-hxr - dy / 2.0, -hyr - dx / 2.0),
                    ( hxr + dy / 2.0, -hyr + dx / 2.0),
                    ( hxr - dy / 2.0,  hyr - dx / 2.0),
                    (-hxr + dy / 2.0,  hyr + dx / 2.0),
                };
                return new PolygonShape { Xy = RotateAbout(pts, px, py, degrees), Net = net };
            }

            case "custom":
            {
                // A custom pad is its ANCHOR shape UNION every one of its primitives — all of them, not
                // the first. Owner report, 2026-08-25: importing only primitive[0] left a lone arc
                // sticking out past the courtyard where the file had a five-piece pad, which reads as a
                // geometry bug rather than as the drop it was; and dropping the anchor removed the pad's
                // own body, so a "custom" pad frequently arrived with no copper under its pin at all.
                var built = new List<PcbImportedShape>();
                var strokes = new List<LayoutShape>();
                foreach (var primitive in node.Child("primitives")?.Nodes ?? [])
                {
                    int before = built.Count;
                    ReadGraphic(primitive, ctx, built, px, py);

                    // ── A primitive's own (width …) counts, even when it is FILLED ───────────────
                    //
                    // The originating tool draws a filled primitive as fill PLUS a pen stroke of that
                    // width along its boundary, so the copper reaches half a width past the outline.
                    // Measured against its own plotted Gerber: our pad was short by exactly 0.1 mm on
                    // the side a (width 0.2) filled triangle bounds. Expressed as the stroke it is —
                    // a closed PathShape on the primitive's own boundary — so the union below produces
                    // fill-plus-stroke with round joins, rather than a second inflate implementation.
                    long penWidth = StrokeWidth(primitive, ctx);
                    if (penWidth <= 0) continue;
                    for (int i = before; i < built.Count; i++)
                    {
                        if (built[i].Shape is PathShape) continue;      // already a stroke
                        long tol = LayoutFlattener.ResolveTolDbu(built[i].Shape, null);
                        foreach (var ring in LayoutFlattener.Flatten(built[i].Shape, tol))
                        {
                            if (ring.Length < 6) continue;
                            var closed = new long[ring.Length + 2];
                            Array.Copy(ring, closed, ring.Length);
                            closed[^2] = ring[0]; closed[^1] = ring[1];
                            strokes.Add(new PathShape { Xy = closed, Width = penWidth, End = PathEndStyle.Round });
                        }
                    }
                }

                // (options … (anchor rect|circle)) names the shape the primitives are added to, sized by
                // the pad's own (size …). Circle is this format's default when the option is absent.
                var anchor = node.Child("options")?.ChildAtom("anchor") ?? "circle";
                LayoutShape? body = string.Equals(anchor, "rect", StringComparison.OrdinalIgnoreCase)
                    ? BuildPadShape(node, "rect", px, py, sx, sy, degrees, net, ctx, out _)
                    : sx > 0
                        ? new CircleShape { Cx = px, Cy = py, R = sx / 2, Net = net }
                        : null;

                var pieces = new List<LayoutShape>(built.Count + strokes.Count + 1);
                if (body is not null) pieces.Add(body);
                foreach (var b in built) { b.Shape.Net = net; pieces.Add(b.Shape); }
                foreach (var st in strokes) { st.Net = net; pieces.Add(st); }
                if (pieces.Count == 0) return null;

                // ── ONE pad, not a pile of overlapping pieces ────────────────────────────────────
                //
                // "Anchor union primitives" is the format's own definition, and a UNION is what it
                // means: the originating tool plots a custom pad as a single aperture outline, with no
                // internal edges. Handing the pieces through separately draws every one of those
                // internal edges — owner report, 2026-08-25, comparing the two renderings side by side:
                // "CircnCustom renders as some strange boolean." It is also the honest model, since a
                // pad is one copper region and nothing downstream (DRC, EM, export) should have to
                // rediscover that.
                //
                // The union is polygonal, so a circular primitive arrives flattened at the default
                // tolerance rather than as a CircleShape. That is the same trade the tool's own plot
                // makes, and it is the representation, not the area, that changes. Disjoint primitives
                // stay disjoint — the union simply returns each piece.
                if (pieces.Count > 1)
                {
                    var merged = LayoutBooleans.Union(pieces, tech: null).Shapes;
                    if (merged.Count > 0)
                    {
                        foreach (var m in merged) m.Net = net;
                        if (merged.Count > 1) extra = [.. merged.Skip(1)];
                        return merged[0];
                    }
                }

                if (pieces.Count > 1) extra = [.. pieces.Skip(1)];
                return pieces[0];
            }

            default:
                ctx.Skipped($"pad shape \"{shape}\"");
                return null;
        }
    }

    /// <summary>tan(90 deg / 4) — one counter-clockwise quarter-circle corner in
    /// <see cref="LayoutArc"/>'s <c>bulge = tan(sweep/4)</c> convention. The same constant
    /// <see cref="LayoutRotationPromotion"/> uses to turn a rounded rectangle into a curve, and for the
    /// same reason.</summary>
    private const double QuarterTurnBulge = 0.41421356237309503;

    /// <summary>
    /// The general rounded/chamfered rectangle boundary: straight runs along the four sides, and at each
    /// corner either a straight CHAMFER, a quarter-circle ARC, or a sharp point.
    ///
    /// <para><b>The corner names are in the layout's own Y-UP frame</b> — <c>top_left</c> is
    /// (-x, +y) — which is not a coincidence to be re-derived per format: a Gerber plotted by the
    /// originating tool states aperture coordinates Y-up too, and its chamfered apertures put the cut on
    /// exactly the corner the pad names. Verified on two pads with disjoint chamfer sets.</para>
    ///
    /// <para>Traversed counter-clockwise so every corner arc is a POSITIVE quarter-turn bulge; a filled
    /// boundary does not care about winding, but the bulge sign and the winding must agree.</para>
    /// </summary>
    private static CurveShape ChamferedRoundRect(
        long px, long py, long sx, long sy, long radius, long chamfer, PcbNode? chamferNode,
        double degrees, string? net)
    {
        var named = new HashSet<string>(chamferNode?.Atoms ?? [], StringComparer.OrdinalIgnoreCase);
        double hx = sx / 2.0, hy = sy / 2.0;

        // Neither cut may eat more than half a side, and a chamfer replaces the rounding on its own
        // corner rather than stacking with it.
        double r = Math.Max(0, Math.Min(radius, Math.Min(hx, hy)));
        double c = Math.Max(0, Math.Min(chamfer, Math.Min(hx, hy)));

        // Counter-clockwise from the bottom-left corner. Cut = how far the corner is trimmed;
        // IsChamfer = whether the trim is a straight edge (true) or a quarter-circle (false).
        (double X, double Y, string Name)[] corners =
        [
            (-hx, -hy, "bottom_left"), (hx, -hy, "bottom_right"), (hx, hy, "top_right"), (-hx, hy, "top_left"),
        ];

        var xy = new List<double>(16);
        var edges = new List<LayoutEdge>(8);
        for (int i = 0; i < 4; i++)
        {
            var (cx, cy, name) = corners[i];
            bool isChamfer = named.Contains(name);
            double cut = isChamfer ? c : r;

            // The two directions the boundary leaves this corner along, as unit steps toward its
            // neighbours — which is all that is needed to place the trim points on either side.
            var (px0, py0, _) = corners[(i + 3) % 4];
            var (px1, py1, _) = corners[(i + 1) % 4];
            double inX = Math.Sign(px0 - cx), inY = Math.Sign(py0 - cy);
            double outX = Math.Sign(px1 - cx), outY = Math.Sign(py1 - cy);

            if (cut <= 0)
            {
                xy.Add(cx); xy.Add(cy);
                edges.Add(new LayoutEdge { Kind = EdgeKind.Line });
                continue;
            }

            xy.Add(cx + inX * cut); xy.Add(cy + inY * cut);
            edges.Add(isChamfer
                ? new LayoutEdge { Kind = EdgeKind.Line }
                : new LayoutEdge { Kind = EdgeKind.Arc, Bulge = QuarterTurnBulge });
            xy.Add(cx + outX * cut); xy.Add(cy + outY * cut);
            edges.Add(new LayoutEdge { Kind = EdgeKind.Line });
        }

        var local = new (double X, double Y)[xy.Count / 2];
        for (int i = 0; i < local.Length; i++) local[i] = (xy[2 * i], xy[2 * i + 1]);
        return new CurveShape { Xy = RotateAbout(local, px, py, degrees), Edges = edges, Net = net };
    }

    private static PolygonShape RotatedRect(long px, long py, long sx, long sy, double degrees, string? net)
    {
        double hx = sx / 2.0, hy = sy / 2.0;
        var pts = new (double X, double Y)[] { (-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy) };
        return new PolygonShape { Xy = RotateAbout(pts, px, py, degrees), Net = net };
    }

    private static long[] RotateAbout((double X, double Y)[] local, long px, long py, double degrees)
    {
        var (cos, sin) = LayoutAngle.CosSin(LayoutAngle.Normalize(degrees));
        var xy = new long[local.Length * 2];
        for (int i = 0; i < local.Length; i++)
        {
            xy[2 * i] = px + (long)Math.Round(local[i].X * cos - local[i].Y * sin, MidpointRounding.AwayFromZero);
            xy[2 * i + 1] = py + (long)Math.Round(local[i].X * sin + local[i].Y * cos, MidpointRounding.AwayFromZero);
        }
        return xy;
    }

    // ── Content addressing (R-L4d-15) ───────────────────────────────────────────────────────────

    /// <summary>
    /// A stable hash of a footprint's cell-local content. Two placements of one library part hash the
    /// same and therefore share one cell — which is what keeps a board of 400 identical parts at 400
    /// instances rather than 400 copies of geometry.
    ///
    /// <para>The library NAME is part of the key, so two genuinely different parts that happen to
    /// coincide geometrically stay separate cells; the placement is not, because the geometry hashed
    /// here has already had it removed.</para>
    /// </summary>
    internal static string ContentKeyOf(PcbFootprintCell cell)
    {
        var sb = new StringBuilder();
        sb.Append(cell.LibraryName).Append('\n');
        foreach (var s in cell.Shapes)
        {
            sb.Append(s.LayerName).Append('|').Append(s.Shape.GetType().Name).Append('|').Append(s.Shape.Net ?? "").Append('|');
            AppendGeometry(sb, s.Shape);
            sb.Append('\n');
        }
        foreach (var (p, layerName) in cell.Pins)
            sb.Append("pin|").Append(p.Name).Append('|').Append(layerName).Append('|')
              .Append(p.X).Append(',').Append(p.Y)
              .Append('|').Append(p.WidthDbu).Append('|')
              .Append(p.OutwardDeg.ToString("R", CultureInfo.InvariantCulture)).Append('\n');

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    private static void AppendGeometry(StringBuilder sb, LayoutShape shape)
    {
        switch (shape)
        {
            case RectShape r: sb.Append(r.X1).Append(',').Append(r.Y1).Append(',').Append(r.X2).Append(',').Append(r.Y2); break;
            case RoundedRectShape rr: sb.Append(rr.X1).Append(',').Append(rr.Y1).Append(',').Append(rr.X2).Append(',').Append(rr.Y2).Append(',').Append(rr.CornerRadius); break;
            case CircleShape c: sb.Append(c.Cx).Append(',').Append(c.Cy).Append(',').Append(c.R); break;
            case ViaShape v: sb.Append(v.X).Append(',').Append(v.Y).Append(',').Append(v.PadSize).Append(',').Append(v.DrillSize); break;
            case PolygonShape p: AppendXy(sb, p.Xy); break;
            case CurveShape cu: AppendXy(sb, cu.Xy); AppendEdges(sb, cu.Edges); break;
            case PathShape pa: AppendXy(sb, pa.Xy); sb.Append('w').Append(pa.Width).Append((int)pa.End); AppendEdges(sb, pa.Edges); break;
            case LabelShape l: sb.Append(l.X).Append(',').Append(l.Y).Append(',').Append(l.Height).Append(',').Append(l.Text); break;
        }
    }

    private static void AppendXy(StringBuilder sb, long[] xy)
    {
        foreach (long v in xy) sb.Append(v).Append(',');
    }

    private static void AppendEdges(StringBuilder sb, List<LayoutEdge>? edges)
    {
        if (edges is null) return;
        foreach (var e in edges)
            sb.Append((int)e.Kind).Append(':').Append(e.Bulge.ToString("R", CultureInfo.InvariantCulture))
              .Append(':').Append(e.C1X).Append(',').Append(e.C1Y).Append(',').Append(e.C2X).Append(',').Append(e.C2Y).Append(';');
    }

    // ── Emit ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Records a shape against its SOURCE layer name. A via additionally records the name of
    /// its landing (pad) layer, which <c>PcbImport</c> resolves once the keys are known.</summary>
    private static void Add(List<PcbImportedShape> into, LayoutShape shape, string layerName, string? padLayerName = null)
        => into.Add(new PcbImportedShape(shape, layerName, padLayerName));
}
