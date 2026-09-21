// A short is a PATH — brief-lvs-8-findings.md R-lvs8-4, docs/design/lvs.md §6.5.
//
//   "VDD and VOUT are joined through a 0.2 mm neck of Metal1 at (1.204 mm, 3.881 mm)"
//
// not "VDD and VOUT are shorted". That sentence is the whole reason brief 2 kept the merge edges,
// and F4 of the proving board is the gate.
//
// ── R-lvs8-4a HAD TO BE WIDENED, AND HERE IS WHY ──────────────────────────────────────────────
//
// The brief says the answer is "a breadth-first walk over that spanning forest, and the shortest
// join sequence between the two pieces". That is necessary and it is NOT sufficient, and brief 2's
// own note already said so without drawing the conclusion: `DrcRegions.Components` unions two
// pieces of metal that meet on ONE LAYER before the union-find ever sees them, so a same-layer
// short leaves NO EDGE BEHIND. It is not a short join on the path — it is not on the path at all,
// because the two ends are one node.
//
// F4 is exactly that shape: 0.2 mm of top copper from the ground strip up to the input run. Walk
// the forest and the shortest sequence from the input's pad to a ground pad runs down a stitching
// via, across the pour and up another stitching via — three 0.6 mm barrels, every one of them
// correct artwork, and not one of them the fault. The spur is invisible to the forest.
//
// So the path is a walk over the forest AND, at every piece the walk passes through, the
// NARROWEST PLACE INSIDE THAT PIECE between where the walk entered and where it left. The narrowest
// step of the whole route is what the finding names. That is the brief's own sentence taken
// literally — "a 0.2 mm NECK OF METAL1", not a via — and it is the only reading under which its
// own gate can pass.
//
// ── THE MEASUREMENT IS AN OPENING, WHICH IS THE DRC's OWN MINIMUM-WIDTH TEST ──────────────────
//
// §8 says "no new geometry", and none is invented: the neck is found with the same erode-then-
// dilate that `DrcEngine.CheckMinWidth` has always used to answer "which part of this conductor is
// narrower than w", bisected on w until the two ends stop being connected. What comes back is the
// width, in DBU, and the piece of metal that is the bridge — never the whole net, because a marker
// covering a board-wide pour points at nothing (R-lvs8-4c).
//
// It is bounded on purpose: a piece past `CopperNeck.MaxVertices` is not measured at all and the
// walk reports its join instead. A report that took a minute to say where a short is would be one
// nobody waits for.
//
// The measurement itself is NOT in this folder. R-lvs3-7 says nothing under `Lvs/` unions
// geometry and a source scan holds it, so the erode-and-dilate lives with the copper, in
// `Extraction/CopperNeck.cs`, and what is here is the walk.

using System.Linq;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Extraction;

namespace CircuitRF.Design.Layout.Lvs;

/// <summary>
/// The narrowest place on the route between two pins that should not be connected — either a join
/// between two pieces or a neck inside one.
/// </summary>
/// <param name="WidthDbu">How wide the metal is there.</param>
/// <param name="X">DBU — <b>the coordinate the finding names</b>.</param>
/// <param name="Y">DBU.</param>
/// <param name="Layer">Which drawing layer it is on.</param>
/// <param name="Kind">A join between pieces, or a neck inside one (null).</param>
/// <param name="Rings">The marker: the join's own geometry, or the bridging metal.</param>
/// <param name="Bounds">Its bounding box.</param>
internal readonly record struct LvsShortStep(
    long WidthDbu, long X, long Y, LayerKey Layer, JoinKind? Kind,
    long[][] Rings, Bbox Bounds);

internal static class LvsShortPath
{
    /// <summary>
    /// The narrowest step of the shortest route from <paramref name="from"/> to
    /// <paramref name="to"/>, or null where the two pieces are not joined at all.
    /// </summary>
    /// <param name="geometry">The partition and its spanning forest.</param>
    /// <param name="from">The piece one net's pin sits on.</param>
    /// <param name="fromX">That pin's own coordinate — where the route starts inside the piece.</param>
    /// <param name="fromY">DBU.</param>
    /// <param name="to">The piece the other net's pin sits on.</param>
    /// <param name="toX">DBU.</param>
    /// <param name="toY">DBU.</param>
    public static LvsShortStep? Narrowest(
        LvsGeometry geometry,
        int from, long fromX, long fromY,
        int to, long toX, long toY)
    {
        if (from < 0 || to < 0) return null;

        var route = Route(geometry, from, to);
        if (route is null) return null;

        LvsShortStep? best = null;
        void Offer(LvsShortStep? step)
        {
            if (step is not { } s) return;
            if (best is not { } b || s.WidthDbu < b.WidthDbu) best = s;
        }

        // Every join the walk crossed. The constriction of a join is the NARROWER of the two
        // pieces it bridges, which for a via is its barrel — measured rather than assumed, the same
        // choice `DrcConnectivity.JoinOf` makes about the kind.
        foreach (var (join, a, b) in route.Joins)
        {
            int narrow = MinExtent(geometry, a) <= MinExtent(geometry, b) ? a : b;
            Offer(new LvsShortStep(
                MinExtent(geometry, narrow), join.X, join.Y, join.Layer, join.Kind,
                geometry.RingsOfPiece(narrow), geometry.BoundsOfPiece(narrow)));
        }

        // And the narrowest place inside each piece, between where the route entered it and where
        // it left — the half the spanning forest cannot see.
        for (int i = 0; i < route.Pieces.Count; i++)
        {
            var (ax, ay) = i == 0 ? (fromX, fromY) : (route.Joins[i - 1].Join.X, route.Joins[i - 1].Join.Y);
            var (bx, by) = i == route.Pieces.Count - 1
                ? (toX, toY)
                : (route.Joins[i].Join.X, route.Joins[i].Join.Y);
            Offer(Neck(geometry, route.Pieces[i], ax, ay, bx, by));
        }

        return best;
    }

    // ── The walk (R-lvs8-4a) ─────────────────────────────────────────────────────────────────

    private sealed record Walk(List<int> Pieces, List<(PieceJoin Join, int A, int B)> Joins);

    /// <summary>Breadth-first over the spanning forest: the shortest join sequence between two
    /// pieces, or null where nothing joins them.</summary>
    private static Walk? Route(LvsGeometry geometry, int from, int to)
    {
        if (from == to) return new Walk([from], []);

        var adjacency = new Dictionary<int, List<(int To, int Join)>>();
        var joins = geometry.Pieces.Joins;
        for (int j = 0; j < joins.Count; j++)
        {
            (adjacency.TryGetValue(joins[j].PieceA, out var a) ? a : adjacency[joins[j].PieceA] = [])
                .Add((joins[j].PieceB, j));
            (adjacency.TryGetValue(joins[j].PieceB, out var b) ? b : adjacency[joins[j].PieceB] = [])
                .Add((joins[j].PieceA, j));
        }

        var cameFrom = new Dictionary<int, (int Piece, int Join)> { [from] = (-1, -1) };
        var queue = new Queue<int>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            int at = queue.Dequeue();
            if (at == to) break;
            if (!adjacency.TryGetValue(at, out var next)) continue;

            // Ascending, so two routes of equal length resolve to one answer on every run
            // (R-lvs8-1b is about the report; this is what makes the report reproducible).
            foreach (var (peer, join) in next.OrderBy(e => e.To).ThenBy(e => e.Join))
                if (cameFrom.TryAdd(peer, (at, join))) queue.Enqueue(peer);
        }

        if (!cameFrom.ContainsKey(to)) return null;

        var pieces = new List<int>();
        var crossed = new List<(PieceJoin, int, int)>();
        for (int at = to; at >= 0; at = cameFrom[at].Piece)
        {
            pieces.Add(at);
            var (prior, join) = cameFrom[at];
            if (prior >= 0) crossed.Add((joins[join], prior, at));
        }

        pieces.Reverse();
        crossed.Reverse();
        return new Walk(pieces, crossed);
    }

    /// <summary>A piece's narrow dimension — what it can carry at its tightest, bounded above.</summary>
    private static long MinExtent(LvsGeometry geometry, int piece)
    {
        var b = geometry.BoundsOfPiece(piece);
        return b.IsEmpty ? 0 : Math.Max(1, Math.Min(b.MaxX - b.MinX, b.MaxY - b.MinY));
    }

    // ── The neck (R-lvs8-4b) ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The narrowest place inside one piece between two points on it — <b>asked of
    /// <see cref="CopperNeck"/>, because R-lvs3-7 says geometry is Extraction's and means it</b>.
    /// </summary>
    private static LvsShortStep? Neck(
        LvsGeometry geometry, int piece, long ax, long ay, long bx, long by)
    {
        // A tenth of a micron: fine enough that a 0.2 mm spur reports as 0.2 mm, coarse enough
        // that the bisection is a dozen tests rather than two dozen.
        long tolerance = Math.Max(1, geometry.Format.DbuPerMicron / 10);
        if (CopperNeck.Measure(geometry.Pieces, piece, ax, ay, bx, by, tolerance) is not { } reading)
            return null;

        var box = reading.Bounds;
        return new LvsShortStep(
            reading.WidthDbu, (box.MinX + box.MaxX) / 2, (box.MinY + box.MaxY) / 2,
            geometry.Pieces.LayerOfPiece(piece), null, reading.Rings, box);
    }
}
