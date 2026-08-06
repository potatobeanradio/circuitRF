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

namespace CircuitRF.Ui.Layout.Drc;

/// <summary>
/// One electrically-connected piece of metal: a region, the layer it sits on, and the net it
/// belongs to.
/// </summary>
/// <param name="Layer">The drawing layer this piece is on.</param>
/// <param name="Paths">Its geometry, Clipper2 form, DBU.</param>
/// <param name="Bounds">Bounding box, so a pairwise sweep can reject most pairs without work.</param>
/// <param name="Net">Net index. Two pieces with the same index are electrically the same net.</param>
internal sealed record DrcNetPiece(LayerKey Layer, Paths64 Paths, Bbox Bounds, int Net);

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
        Technology tech)
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

        // ── Vias join pieces across layers ──────────────────────────────────────────────────────
        // A via's own geometry is the bridge: a piece on the layer below and a piece on the layer
        // above are one net when BOTH touch the same via. Testing "does the via touch each side"
        // rather than "do the two sides overlap each other" is what makes a staircase of offset
        // metal connect correctly — the two metal pieces need never overlap one another.
        var conductorLayers = new Dictionary<string, List<LayerKey>>(StringComparer.Ordinal);
        foreach (var sl in tech.Stackup.Layers)
            if (sl.Kind == StackupKind.Conductor && sl.Name.Length > 0)
                conductorLayers[sl.Name] = sl.DrawingLayers;

        foreach (var via in tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Via))
        {
            if (via.SpanFromLayer is not { Length: > 0 } from ||
                via.SpanToLayer is not { Length: > 0 } to) continue;
            if (!conductorLayers.TryGetValue(from, out var fromLayers) ||
                !conductorLayers.TryGetValue(to, out var toLayers)) continue;

            foreach (var viaLayer in via.DrawingLayers)
            {
                if (!byLayer.TryGetValue(viaLayer, out var viaPieces)) continue;

                foreach (int v in viaPieces)
                {
                    int? below = FirstTouching(pieces, byLayer, fromLayers, pieces[v]);
                    int? above = FirstTouching(pieces, byLayer, toLayers, pieces[v]);

                    // A via touching only one side is a real, common state mid-edit — it connects
                    // nothing yet. It is not an error here; a rule about it is a rule's business.
                    if (below is null || above is null) continue;

                    uf.Union(v, below.Value);
                    uf.Union(v, above.Value);
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
        (LayerKey Layer, Paths64 Paths, Bbox Bounds) probe)
    {
        var grown = Clipper.InflatePaths(probe.Paths, TouchDilationDbu, JoinType.Miter, EndType.Polygon, 2.0);
        var probeBounds = DrcRegions.Grow(probe.Bounds, (long)Math.Ceiling(TouchDilationDbu));

        foreach (var layer in layers)
        {
            if (!byLayer.TryGetValue(layer, out var candidates)) continue;

            foreach (int i in candidates)
            {
                if (!probeBounds.Intersects(pieces[i].Bounds)) continue;   // cheap rejection first

                var meet = Clipper.BooleanOp(ClipType.Intersection, grown, pieces[i].Paths, LayoutClipper.Rule);
                if (meet.Count > 0) return i;
            }
        }

        return null;
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

        public void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            if (_size[ra] < _size[rb]) (ra, rb) = (rb, ra);
            _parent[rb] = ra;
            _size[ra] += _size[rb];
        }
    }
}
