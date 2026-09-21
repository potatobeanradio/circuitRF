// Which drawn metal is electrically one net (docs/design/layout-view.md §9A.3).
//
// <b>This is the capability the net-aware rules were blocked on.</b> A real deck states a large
// share of its spacing rules twice — once for metal on the SAME net and once for DIFFERENT nets,
// with different values, because two pieces of one net may sit closer than two that could short.
// Without net identity a checker has to pick one of the two values: the same-net value passes
// genuine shorts, and the different-net value fails correct artwork. Neither is acceptable, which
// is why v1 read those rules and did not enforce them.
//
// <b>What this is NOT.</b> It is not LVS. It answers "which shapes are electrically joined to each
// other", from geometry and the stackup alone. It does not know what any net is CALLED, does not
// compare against a schematic, and does not extract devices. Those are separate questions and
// naming this class anything LVS-flavoured would invite the assumption that it answers them.

using Clipper2Lib;
using CircuitRF.Design.Layout.Extraction;

namespace CircuitRF.Design.Layout.Drc;

/// <summary>
/// One electrically-connected piece of metal: a region, the layer it sits on, and the net it
/// belongs to.
/// </summary>
/// <param name="Layer">The drawing layer this piece is on.</param>
/// <param name="Paths">Its geometry, Clipper2 form, DBU.</param>
/// <param name="Bounds">Bounding box, so a pairwise sweep can reject most pairs without work.</param>
/// <param name="Net">Net index. Two pieces with the same index are electrically the same net.</param>
internal sealed record DrcNetPiece(LayerKey Layer, Paths64 Paths, Bbox Bounds, int Net);

/// <summary>What joined two pieces — brief-lvs-2-shared-extraction.md R-lvs2-4a.</summary>
public enum JoinKind
{
    /// <summary>Two pieces of metal meeting on one drawing layer.</summary>
    SameLayerTouch,

    /// <summary>A via barrel bridging a piece on one conductor to a piece on another.</summary>
    Via,
}

/// <summary>
/// One edge of the spanning forest the net partition was built from — <b>WHY two pieces are one
/// net</b>, which is the only question a designer looking at a short actually has (R-lvs2-4).
/// </summary>
/// <remarks>
/// <b>Membership cannot answer it.</b> <see cref="DrcConnectivity.Extract(IReadOnlyDictionary{LayerKey, Paths64}, Technology)"/>
/// unions and renumbers, and what survives is which pieces share a number. That answers <i>are
/// these two pins one net</i> and nothing else; <i>"joined through a via at (1.204 mm, 3.881 mm)"</i>
/// is the whole difference between a report a user can act on and one they cannot.
///
/// <para><b>ONE EDGE PER UNION, NOT EVERY TOUCHING PAIR</b> (R-lvs2-4c). A spanning forest is all a
/// path walk needs, and recording every adjacency would make the structure quadratic in the thing
/// it exists to make cheap.</para>
/// </remarks>
/// <param name="PieceA">Index into the piece list <c>Extract</c> returned.</param>
/// <param name="PieceB">The other one.</param>
/// <param name="Kind">Measured from the two pieces, not from which loop produced the union.</param>
/// <param name="X">WHERE the join happens, DBU — the via's own position for a via, a point inside
/// the intersection for a same-layer touch (R-lvs2-4d). This is the coordinate brief 8 puts a
/// marker on.</param>
/// <param name="Y">DBU.</param>
/// <param name="Layer">The layer the join is observed on — the via's own drawing layer for a via.</param>
public readonly record struct PieceJoin(
    int PieceA, int PieceB, JoinKind Kind, long X, long Y, LayerKey Layer);

/// <summary>
/// Extracts net identity from flat geometry plus the technology's own stackup.
///
/// <para>Two pieces of metal are the same net when they touch on one layer, or when a via joins
/// them across layers. The stackup is what says which layers a via joins — that is exactly what
/// <see cref="StackupLayer.SpanFromLayer"/>/<see cref="StackupLayer.SpanToLayer"/> have carried
/// since the via primitive landed, unread until now.</para>
/// </summary>
internal static class DrcConnectivity
{
    /// <summary>
    /// Two regions count as joined when they overlap or share an edge. Same reasoning, and the same
    /// number, as the topological selections: Clipper2 reports an edge-only intersection as zero
    /// area, and a via landing exactly on a metal edge is a real connection.
    /// </summary>
    private const double TouchDilationDbu = 1.0;

    /// <summary>
    /// Builds the net partition.
    /// </summary>
    /// <param name="layerRegions">Per-layer unioned geometry, as the DRC run already built it.</param>
    /// <param name="tech">Supplies the stackup that says which layers a via joins.</param>
    /// <returns>
    /// Every connected piece, with a net index. Layers the stackup does not describe still yield
    /// pieces — each simply forms its own net, since nothing states what it connects to.
    /// </returns>
    public static IReadOnlyList<DrcNetPiece> Extract(
        IReadOnlyDictionary<LayerKey, Paths64> layerRegions,
        Technology tech) => Extract(layerRegions, tech, null);

    /// <summary>
    /// The same partition, and <b>the spanning forest it was built from</b> — R-lvs2-4b.
    /// </summary>
    /// <param name="joins">Filled with one record per successful union.</param>
    /// <remarks>
    /// <b>An additive OVERLOAD, not a change to the existing return.</b> The DRC does not ask for
    /// the joins and pays nothing for them: locating a join costs a Clipper intersection that the
    /// two-argument form never performs.
    /// </remarks>
    public static IReadOnlyList<DrcNetPiece> Extract(
        IReadOnlyDictionary<LayerKey, Paths64> layerRegions,
        Technology tech,
        out IReadOnlyList<PieceJoin> joins)
    {
        var found = new List<PieceJoin>();
        var pieces = Extract(layerRegions, tech, found);
        joins = found;
        return pieces;
    }

    private static IReadOnlyList<DrcNetPiece> Extract(
        IReadOnlyDictionary<LayerKey, Paths64> layerRegions,
        Technology tech,
        List<PieceJoin>? joins)
    {
        // ── Every connected piece on every layer, before any via is considered ──────────────────
        var pieces = new List<(LayerKey Layer, Paths64 Paths, Bbox Bounds)>();
        var byLayer = new Dictionary<LayerKey, List<int>>();

        foreach (var (layer, region) in layerRegions)
        {
            foreach (var component in DrcRegions.Components(region))
            {
                if (!byLayer.TryGetValue(layer, out var list)) byLayer[layer] = list = [];
                list.Add(pieces.Count);
                pieces.Add((layer, component, DrcRegions.BoundsOf(component)));
            }
        }

        if (pieces.Count == 0) return [];

        var uf = new UnionFind(pieces.Count);
        var meets = new Dictionary<int, Paths64>();

        // ── Vias join pieces across layers ──────────────────────────────────────────────────────
        // A via's own geometry is the bridge: a piece on the layer below and a piece on the layer
        // above are one net when BOTH touch the same via. Testing "does the via touch each side"
        // rather than "do the two sides overlap each other" is what makes a staircase of offset
        // metal connect correctly — the two metal pieces need never overlap one another.
        // Ordered top to bottom, which is what Stackup.Layers is (R-em-3) — so the conductors a via
        // passes THROUGH are the ones between its two span ends in this list. The enumeration is
        // Conductors.Of's, written once (R-lvs2-1); duplicates are preserved because the order is
        // read by INDEX and collapsing them would move every index after the collapse.
        var stackupConductors = Conductors.Of(tech);
        var conductorLayers = new Dictionary<string, IReadOnlyList<LayerKey>>(StringComparer.Ordinal);
        var conductorOrder = new List<string>(stackupConductors.Count);
        foreach (var c in stackupConductors)
        {
            conductorLayers[c.StackupName] = c.DrawingLayers;
            conductorOrder.Add(c.StackupName);
        }

        foreach (var via in tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Via))
        {
            if (via.SpanFromLayer is not { Length: > 0 } from ||
                via.SpanToLayer is not { Length: > 0 } to) continue;
            if (!conductorLayers.ContainsKey(from) || !conductorLayers.ContainsKey(to)) continue;

            // ── EVERY CONDUCTOR THE BARREL PASSES, NOT ONLY ITS TWO ENDS ────────────────────────
            //
            // A plated barrel shorts every layer it passes through that has copper at that point,
            // and the ANTI-PAD is how the artwork says which those are: copper right up to the
            // barrel is a connection, a clearance round it is not. Joining only the span's two ends
            // read a four-layer board with a power plane on an inner layer as though the plane were
            // not on the board — the plane came back a galvanically separate island, carrying no
            // current and contributing nothing, with the picture showing it plainly connected.
            //
            // It could not fail loudly, either: Regions reports islands as ORDINARY on
            // imported artwork ("the copper stops at every pad"), so the count went up by one and
            // read as the thing that note is about.
            int a = conductorOrder.IndexOf(from), b = conductorOrder.IndexOf(to);
            var spanned = conductorOrder
                .GetRange(Math.Min(a, b), Math.Abs(a - b) + 1)
                .Select(name => conductorLayers[name])
                .ToList();

            foreach (var viaLayer in via.DrawingLayers)
            {
                if (!byLayer.TryGetValue(viaLayer, out var viaPieces)) continue;

                foreach (int v in viaPieces)
                {
                    var touched = new List<int>();
                    foreach (var layers in spanned)
                        if (FirstTouching(pieces, byLayer, layers, pieces[v], out var meet) is { } hit)
                        {
                            touched.Add(hit);
                            if (joins is not null) meets[hit] = meet;
                        }

                    // A via touching only one conductor is a real, common state mid-edit — it
                    // connects nothing yet. It is not an error here; a rule about it is a rule's
                    // business. Unchanged: what widened is WHICH conductors are candidates.
                    if (touched.Count < 2) continue;

                    foreach (int hit in touched)
                    {
                        // R-lvs2-4c: the record is written only where the union actually MERGED
                        // two sets, so what is retained is a spanning forest rather than every
                        // adjacency.
                        if (!uf.Union(v, hit)) continue;
                        joins?.Add(JoinOf(pieces, v, hit, meets.GetValueOrDefault(hit)));
                    }
                }
            }
        }

        var result = new List<DrcNetPiece>(pieces.Count);
        foreach (var (layer, paths, bounds) in pieces)
            result.Add(new DrcNetPiece(layer, paths, bounds, 0));

        // Renumber to dense, ascending net indices so the numbers are stable and readable rather
        // than being whatever the union-find happened to leave as a root.
        var netOf = new Dictionary<int, int>();
        for (int i = 0; i < result.Count; i++)
        {
            int root = uf.Find(i);
            if (!netOf.TryGetValue(root, out int net))
            {
                net = netOf.Count;
                netOf[root] = net;
            }
            result[i] = result[i] with { Net = net };
        }

        return result;
    }

    /// <summary>The first piece on any of <paramref name="layers"/> that touches
    /// <paramref name="probe"/>, or null.</summary>
    private static int? FirstTouching(
        List<(LayerKey Layer, Paths64 Paths, Bbox Bounds)> pieces,
        Dictionary<LayerKey, List<int>> byLayer,
        IReadOnlyList<LayerKey> layers,
        (LayerKey Layer, Paths64 Paths, Bbox Bounds) probe,
        out Paths64 meet)
    {
        meet = [];
        var grown = Clipper.InflatePaths(probe.Paths, TouchDilationDbu, JoinType.Miter, EndType.Polygon, 2.0);
        var probeBounds = DrcRegions.Grow(probe.Bounds, (long)Math.Ceiling(TouchDilationDbu));

        foreach (var layer in layers)
        {
            if (!byLayer.TryGetValue(layer, out var candidates)) continue;

            foreach (int i in candidates)
            {
                if (!probeBounds.Intersects(pieces[i].Bounds)) continue;   // cheap rejection first

                var hit = Clipper.BooleanOp(ClipType.Intersection, grown, pieces[i].Paths, LayoutClipper.Rule);
                if (hit.Count > 0) { meet = hit; return i; }
            }
        }

        return null;
    }

    /// <summary>
    /// Where a join happens and what kind it is — R-lvs2-4d.
    /// </summary>
    /// <remarks>
    /// <b>The kind is MEASURED from the two pieces, not assumed from the loop.</b> Today every
    /// union this walk performs bridges a via barrel to a conductor, so every join it records is a
    /// <see cref="JoinKind.Via"/>: two pieces of metal meeting on ONE layer were already unioned
    /// into a single component by <see cref="DrcRegions.Components"/> before the union-find saw
    /// them, so there is no same-layer edge left to record. Classifying by measurement rather than
    /// by provenance means the answer stays right when a caller unions on its own account — brief
    /// 9's boundary stitching is that caller.
    /// </remarks>
    private static PieceJoin JoinOf(
        List<(LayerKey Layer, Paths64 Paths, Bbox Bounds)> pieces, int via, int hit, Paths64? meet)
    {
        bool sameLayer = pieces[via].Layer == pieces[hit].Layer;

        // A via's OWN position, which is the centre of its barrel — the thing a user is being asked
        // to look at. For a same-layer touch there is no such thing, so it is a point inside the
        // intersection: "joined through a 0.2 mm neck of Metal1 at (1.204 mm, 3.881 mm)".
        var box = sameLayer && meet is { Count: > 0 } ? DrcRegions.BoundsOf(meet) : pieces[via].Bounds;
        long x = box.IsEmpty ? 0 : (box.MinX + box.MaxX) / 2;
        long y = box.IsEmpty ? 0 : (box.MinY + box.MaxY) / 2;

        return new PieceJoin(
            via, hit, sameLayer ? JoinKind.SameLayerTouch : JoinKind.Via, x, y, pieces[via].Layer);
    }

    /// <summary>
    /// Union-find with path compression and union by size.
    ///
    /// <para>Local rather than shared: the schematic side's own connectivity uses a different
    /// keying (integer grid cells, exact coincidence) and unifying them would couple two
    /// correctness-critical mechanisms that happen to share an algorithm and nothing else.</para>
    /// </summary>
    private sealed class UnionFind
    {
        private readonly int[] _parent;
        private readonly int[] _size;

        public UnionFind(int n)
        {
            _parent = new int[n];
            _size = new int[n];
            for (int i = 0; i < n; i++) { _parent[i] = i; _size[i] = 1; }
        }

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];   // path halving
                x = _parent[x];
            }
            return x;
        }

        /// <summary>True where the two sets were distinct and have now been merged — which is what
        /// makes the retained edges a spanning forest (R-lvs2-4c).</summary>
        public bool Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return false;
            if (_size[ra] < _size[rb]) (ra, rb) = (rb, ra);
            _parent[rb] = ra;
            _size[ra] += _size[rb];
            return true;
        }
    }
}
