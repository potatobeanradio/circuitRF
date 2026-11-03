using CircuitRF.Design.Layout.PCells;
using Clipper2Lib;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Schematic;
using static CircuitRF.Design.Layout.SchematicSync.SchematicLayoutPlanner;

namespace CircuitRF.Design.Layout.SchematicSync;

/// <summary>
/// brief-sdl-3 R-sdl3-1: the copper a run leaves crossing other copper it is not joined to. Connected placement
/// puts every part exactly where the schematic's lengths say (series rule S1), so a meander whose lengths make it
/// cross itself crosses itself, and a part placed against a joint can land on its neighbour. Nothing moves here;
/// each such contact is a <see cref="ReportSeverity.Warning"/>.
///
/// <para><b>What "different nets" means.</b> A part's copper is split into pieces (its connected copper per
/// layer), and a piece carries the nets of the part's pins that land on it — so a line's one strip carries both its
/// nets, a capacitor's two pads carry one each, and a coupled pair's two strips are two pieces. Two pieces of
/// different parts whose nets have none in common, and whose copper overlaps or comes within the partition's
/// hairline of each other (<see cref="LayerRegions"/>: copper closer than a micrometre is one piece), are reported.
/// A contact that only TOUCHES, with no area, between two parts that are both joined to one part between them is
/// not: the lines on a tee's arms, or either side of a bend, meet corner to corner at its junction, which is copper
/// both are joined to anyway.</para>
///
/// <para><b>Copper with no schematic link counts too</b> (SDL-4 relies on it): shapes drawn in the layout, and
/// placements no schematic part names. A drawn shape that states a net is on that net. Copper whose net is unknown
/// is reported when it JOINS two parts that share no net — a short — and not merely for touching one, which is what
/// a hand-drawn trace to a pin is for.</para>
///
/// <para>Candidates come from a <see cref="LayoutSpatialIndex"/> over the pieces' boxes, so the narrow phase runs
/// on neighbours only and never pairwise over every shape.</para>
/// </summary>
public static class PlacementOverlaps
{
    /// <summary>One contact. <paramref name="A"/>/<paramref name="B"/> are the parts, <paramref name="A"/> first in
    /// schematic-id order, with the nets of the copper that met. <paramref name="Crosses"/>: the copper overlaps with
    /// area (else it touches). <paramref name="Through"/> is set for a short through unlinked copper: what that copper
    /// is, said as a sentence's subject.</summary>
    public sealed record Overlap(string A, string NetsA, string B, string NetsB, bool Crosses, string? Through)
    {
        /// <summary><paramref name="B"/> is drawn copper on a stated net (the ground pour, say), not a part: it is
        /// described rather than named, and a click selects <paramref name="A"/> alone.</summary>
        public bool BIsDrawn { get; init; }

        public ReportLine ToReportLine() => new(A,
            Through is { } t
                ? $"{t} joins {A} (net {NetsA}) and {B} (net {NetsB}), which the schematic does not join."
                : BIsDrawn
                    ? $"{A} {(Crosses ? "overlaps" : "touches")} {B} (nets {NetsA}, {NetsB})."
                    : $"{A} {(Crosses ? "overlaps" : "touches")} {B} (nets {NetsA}, {NetsB}): the schematic's lengths " +
                      $"make these {(Crosses ? "cross" : "meet")}.",
            ReportSeverity.Warning) { Kind = ReportKind.Overlap, Ids = BIsDrawn ? [A] : [A, B] };
    }

    private sealed class Piece
    {
        public required int Owner;                    // instance index, or −1 for drawn copper
        public required string? Id;                   // the schematic id of a linked part
        public required LayerKey Layer;
        public required Paths64 Paths;
        public required Bbox Box;
        public IReadOnlyList<string>? Nets;           // null: unknown (no schematic link, or no pin on it)
        public Paths64? Inflated;
    }

    /// <summary>
    /// Every contact in <paramref name="target"/> once <paramref name="plan"/> is applied (to a copy — the target is
    /// not touched). <paramref name="extraction"/> is the run's own, whose net bindings name each linked part's nets.
    /// </summary>
    public static IReadOnlyList<Overlap> Find(SchematicLayoutPlan plan, LayoutView target, string layoutBaseDir,
                                              Technology? technology, NetExtractor.ExtractionResult extraction)
    {
        if (technology is null) return [];
        var conductors = Conductors.Of(technology).SelectMany(c => c.DrawingLayers).ToHashSet();
        if (conductors.Count == 0) return [];

        // The layout as the run leaves it: instances cloned (the plan writes orientation links into them), shapes
        // shared (the plan only appends clones and removes by index).
        var view = new LayoutView { DbuPerMicron = target.DbuPerMicron, TechRef = target.TechRef };
        view.Instances.AddRange(target.Instances.Select(LayoutGeometry.Clone));
        view.Shapes.AddRange(target.Shapes);
        plan.ApplyTo(view);

        var bindings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var inst in extraction.TestBench.Instances) bindings.TryAdd(inst.InstanceName, TerminalNets.Of(inst));

        long reach = Math.Max(1, view.DbuPerMicron);   // the partition's hairline: closer than this is joined
        var pieces = new List<Piece>();
        var partNets = new Dictionary<int, HashSet<string>>();
        for (int i = 0; i < view.Instances.Count; i++) AddInstance(i, view.Instances[i]);
        AddDrawn();
        if (pieces.Count < 2) return [];

        // Broad phase: the layout's spatial index over the pieces' boxes.
        var boxes = pieces.Select(p => (LayoutShape)new RectShape
        {
            Layer = p.Layer, X1 = p.Box.MinX, Y1 = p.Box.MinY, X2 = p.Box.MaxX, Y2 = p.Box.MaxY,
        }).ToList();
        var index = new LayoutSpatialIndex();

        var pairs = new Dictionary<(int, int), (Piece A, Piece B, bool Crosses)>();
        var unknownContacts = new Dictionary<Piece, List<Piece>>(ReferenceEqualityComparer.Instance as IEqualityComparer<Piece>);
        for (int i = 0; i < pieces.Count; i++)
        {
            var p = pieces[i];
            var near = new Bbox(p.Box.MinX - reach, p.Box.MinY - reach, p.Box.MaxX + reach, p.Box.MaxY + reach);
            foreach (int j in index.QueryIntersecting(boxes, near))
            {
                if (j <= i) continue;
                var q = pieces[j];
                if (q.Layer != p.Layer || (q.Owner == p.Owner && p.Owner >= 0)) continue;
                bool pKnown = p.Nets is { Count: > 0 }, qKnown = q.Nets is { Count: > 0 };
                if (!pKnown && !qKnown) continue;
                if (pKnown && qKnown && p.Nets!.Intersect(q.Nets!, StringComparer.Ordinal).Any()) continue;
                if (!Contact(p, q, out bool crosses)) continue;

                if (pKnown && qKnown)
                {
                    if (!crosses && JoinedBetween(p, q)) continue;
                    var key = p.Owner < q.Owner ? (p.Owner, q.Owner) : (q.Owner, p.Owner);
                    pairs[key] = pairs.TryGetValue(key, out var had) ? had with { Crosses = had.Crosses || crosses } : (p, q, crosses);
                }
                else
                {
                    var (unknown, known) = pKnown ? (q, p) : (p, q);
                    if (!unknownContacts.TryGetValue(unknown, out var list)) unknownContacts[unknown] = list = [];
                    list.Add(known);
                }
            }
        }

        var found = new List<Overlap>();
        foreach (var (a, b, crosses) in pairs.Values)
        {
            if (a.Owner < 0 || b.Owner < 0)
            {
                var (part, drawn) = a.Owner < 0 ? (b, a) : (a, b);
                found.Add(new Overlap(part.Id!, NetText(part), DescribeDrawn(drawn), NetText(drawn), crosses, null) { BIsDrawn = true });
                continue;
            }
            var (first, second) = Ordered(a, b);
            found.Add(new Overlap(first.Id!, NetText(first), second.Id!, NetText(second), crosses, null));
        }
        foreach (var (unknown, touched) in unknownContacts)
        {
            var stranger = Strangers(touched);
            if (stranger is not { } s) continue;
            var (first, second) = Ordered(s.A, s.B);
            found.Add(new Overlap(first.Id!, NetText(first), second.Id!, NetText(second), true, Describe(unknown)));
        }
        return found.OrderBy(o => o.Through is null ? 0 : 1)
                    .ThenBy(o => o.A, Comparer<string>.Create(JointGraph.CompareIds))
                    .ThenBy(o => o.B, Comparer<string>.Create(JointGraph.CompareIds))
                    .ToList();

        void AddInstance(int i, LayoutInstance inst)
        {
            if (CellLayoutResolver.Resolve(inst, layoutBaseDir, technology) is not
                    { State: CellLayoutState.Resolved, View: { } cell }) return;
            var shapes = LayoutFlatten.FlattenAllLevels(inst, layoutBaseDir, technology).Shapes
                                      .Where(s => conductors.Contains(s.Layer)).ToList();
            if (shapes.Count == 0) return;

            // The part's pins, where they land, with the net each is bound to — none for a part no schematic names.
            var pins = new List<(long X, long Y, LayerKey Layer, string Net)>();
            string? id = inst.SchematicId is { Length: > 0 } sid && bindings.TryGetValue(sid, out _) ? sid : null;
            if (id is not null && inst.Rows * inst.Cols == 1)
            {
                var nets = bindings[id];
                var cellPins = CellPins.Resolve(cell, technology);
                for (int k = 1; k <= nets.Count; k++)
                {
                    if (nets[k - 1] is not { Length: > 0 } net || SchematicLayoutOrientation.CellPinFor(k, cellPins) is not { } pin) continue;
                    var (x, y) = LayoutInstanceTransform.TransformPoint(pin.X, pin.Y, inst, 0, 0);
                    pins.Add((x, y, pin.Layer, net));
                }
                partNets[i] = pins.Select(p => p.Net).ToHashSet(StringComparer.Ordinal);
            }

            foreach (var group in shapes.GroupBy(s => s.Layer))
                foreach (var piece in Pieces(group))
                {
                    // A pin lands on a piece within the hairline: a bend's pin is rounded to the DBU, and so is its
                    // copper after the placement turns it, so the two can disagree by a fraction of one.
                    var grown = Clipper.InflatePaths(new Paths64 { piece[0] }, reach / 2.0, JoinType.Miter, EndType.Polygon);
                    var on = pins.Where(p => p.Layer == group.Key
                                             && grown.Any(g => Clipper.PointInPolygon(new Point64(p.X, p.Y), g)
                                                               != PointInPolygonResult.IsOutside))
                                 .Select(p => p.Net).Distinct(StringComparer.Ordinal).ToList();
                    pieces.Add(new Piece
                    {
                        Owner = i, Id = id ?? inst.DisplayRefDes, Layer = group.Key, Paths = piece, Box = BoxOf(piece),
                        Nets = id is null || on.Count == 0 ? null : on,
                    });
                }
        }

        void AddDrawn()
        {
            foreach (var group in view.Shapes.Where(s => conductors.Contains(s.Layer))
                                             .GroupBy(s => (s.Layer, Net: NetOfDrawn(s))))
                foreach (var piece in Pieces(group))
                    pieces.Add(new Piece
                    {
                        Owner = -1, Id = null, Layer = group.Key.Layer, Paths = piece, Box = BoxOf(piece),
                        Nets = group.Key.Net is { } net ? [net] : null,
                    });
        }

        // The ground the run draws — its pour and the copper tying a ground via to its pad — is on the ground net by
        // construction, whether or not the shape states it.
        static string? NetOfDrawn(LayoutShape s) =>
            s.Net is { Length: > 0 } n ? n
            : s.Generated is { } g && (g == GroundArtwork.PourTag || g.StartsWith(GroundArtwork.ViaTagPrefix, StringComparison.Ordinal)) ? "0"
            : null;

        string LayerText(LayerKey key) =>
            technology.Layers.FirstOrDefault(l => l.Key == key)?.Name is { Length: > 0 } n ? n : $"{key.Layer}/{key.Datatype}";

        string DescribeDrawn(Piece drawn) =>
            view.Shapes.Any(s => s.Layer == drawn.Layer && s.Generated == GroundArtwork.PourTag
                                 && Clipper.Intersect(drawn.Paths, LayoutClipper.ToClipperPaths(s, 1), LayoutClipper.Rule).Count > 0)
                ? $"the ground pour on {LayerText(drawn.Layer)}"
                : $"copper drawn on {LayerText(drawn.Layer)}";

        string NetText(Piece p) => p.Nets is { Count: > 0 } n ? string.Join("/", n) : "?";

        (Piece, Piece) Ordered(Piece a, Piece b) =>
            JointGraph.CompareIds(a.Id ?? "", b.Id ?? "") <= 0 ? (a, b) : (b, a);

        bool Contact(Piece p, Piece q, out bool crosses)
        {
            crosses = false;
            p.Inflated ??= Clipper.InflatePaths(p.Paths, reach / 2.0, JoinType.Miter, EndType.Polygon);
            q.Inflated ??= Clipper.InflatePaths(q.Paths, reach / 2.0, JoinType.Miter, EndType.Polygon);
            if (Area(Clipper.Intersect(p.Inflated, q.Inflated, LayoutClipper.Rule)) <= 0) return false;
            crosses = Area(Clipper.Intersect(p.Paths, q.Paths, LayoutClipper.Rule)) > 0;
            return true;
        }

        // Both joined to one part between them (a tee, a bend): a corner-to-corner meeting at its junction.
        bool JoinedBetween(Piece p, Piece q) =>
            partNets.Any(e => e.Key != p.Owner && e.Key != q.Owner
                              && e.Value.Overlaps(p.Nets!) && e.Value.Overlaps(q.Nets!));

        // The first two linked pieces an unknown-net piece reaches that are on no common net and not joined anyway.
        (Piece A, Piece B)? Strangers(List<Piece> touched)
        {
            var linked = touched.Where(t => t.Id is not null && t.Owner >= 0)
                                .OrderBy(t => t.Id ?? "", Comparer<string>.Create(JointGraph.CompareIds)).ToList();
            for (int a = 0; a < linked.Count; a++)
                for (int b = a + 1; b < linked.Count; b++)
                    if (linked[a].Owner != linked[b].Owner
                        && !linked[a].Nets!.Intersect(linked[b].Nets!, StringComparer.Ordinal).Any()
                        && !JoinedBetween(linked[a], linked[b]))
                        return (linked[a], linked[b]);
            return null;
        }

        string Describe(Piece unknown)
        {
            string layer = LayerText(unknown.Layer);
            if (unknown.Owner < 0) return $"Copper drawn on {layer} that no schematic part owns";
            var inst = view.Instances[unknown.Owner];
            string name = inst.DisplayRefDes ?? Path.GetFileNameWithoutExtension(inst.CellRef ?? "") ?? "a placement";
            return unknown.Id is not null && inst.SchematicId is { Length: > 0 } && bindings.ContainsKey(inst.SchematicId)
                ? $"Copper of {name} on {layer} that none of its pins lands on"
                : $"{name}, placed with no schematic part,";
        }
    }

    /// <summary>The connected copper of <paramref name="shapes"/>, all on one layer: each piece's outer ring first, then
    /// its holes.</summary>
    private static List<Paths64> Pieces(IEnumerable<LayoutShape> shapes)
    {
        var subject = new Paths64();
        foreach (var s in shapes) subject.AddRange(LayoutClipper.ToClipperPaths(s, Math.Max(1, LayoutFlattener.ResolveTolDbu(s, null))));
        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, subject, new Paths64(), tree, LayoutClipper.Rule);
        var result = new List<Paths64>();
        void Walk(PolyPath64 node)
        {
            for (int i = 0; i < node.Count; i++)
            {
                var child = (PolyPath64)node[i];
                if (child.Polygon is null) continue;
                var piece = new Paths64 { child.Polygon };
                for (int h = 0; h < child.Count; h++)
                {
                    var hole = (PolyPath64)child[h];
                    if (hole.Polygon is not null) piece.Add(hole.Polygon);
                    Walk(hole);   // an island inside a hole is a piece of its own
                }
                result.Add(piece);
            }
        }
        Walk(tree);
        return result;
    }

    private static Bbox BoxOf(Paths64 paths)
    {
        var r = Clipper.GetBounds(paths[0]);
        return new Bbox(r.left, r.top, r.right, r.bottom);   // Clipper's top is the LEAST y
    }

    private static double Area(Paths64 paths)
    {
        double a = 0;
        foreach (var p in paths) a += Clipper.Area(p);
        return a;
    }
}
