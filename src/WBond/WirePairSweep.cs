// The broad phase for wire-to-wire clearance (brief-wbond-wbd R-wbd-4).
//
// ── Why an all-pairs sweep is not an option ─────────────────────────────────────────────────────
//
// The owner's stated worst case is 600 wires. That is 179,700 unordered pairs before anything looks
// at a segment, and a wire is 6-7 points, so a naive all-pairs all-segments sweep is roughly seven
// million segment-pair distances. That is not catastrophic on its own — the physics kernel already
// does 12.9 M filament pairs in about half a second — but DRC is run REPEATEDLY while fixing
// violations, and paying a second per check is the difference between a tool people run and a tool
// people stop running.
//
// So the pairs are pruned first, on bounding boxes, exactly as the 2D DRC's own spacing sweep does.
// A wire's 3D box is cheap and rejects almost everything: bond wires are short and local, so on real
// geometry each wire has a handful of neighbours and the rest of the design is never examined.
//
// ── Why a uniform grid and not an R-tree ────────────────────────────────────────────────────────
//
// `LayoutSpatialIndex` (the layout editor's own R-tree) is the right structure for 10^5-10^6 shapes
// spread over a board. A wBond design is hundreds of wires clustered around one die, all of similar
// size — the case a uniform grid handles with a fraction of the code and no tree to keep fresh. The
// index is also rebuilt from scratch on every check rather than maintained incrementally, because a
// check already re-reads the whole design and a stale acceleration structure is a source of silently
// missed violations.
//
// ── Intersecting wires are a GEOMETRY error, not merely a tight clearance ───────────────────────
//
// A real design has no intersecting wires: two pieces of metal cannot occupy the same space, so a
// clearance at or below zero means the design itself is wrong, not that it is close to a limit.
// <see cref="FindTouching"/> is that check, and it is available whether or not a house states a
// clearance rule — an assembly house's rule file is not what makes overlapping metal invalid.

namespace CircuitRF.WBond;

/// <summary>One pair of wires that came within the queried distance of each other.</summary>
/// <param name="A">Index into the sweep's own wire list.</param>
/// <param name="B">Index into the sweep's own wire list, always greater than <paramref name="A"/>.</param>
/// <param name="ClearanceNm">Surface-to-surface distance. Zero when touching, negative when the
/// metal interpenetrates.</param>
public readonly record struct WirePair(int A, int B, double ClearanceNm);

/// <summary>What one sweep actually did — reported rather than claimed. See R-wbd-4.</summary>
/// <param name="Wires">How many wires were indexed.</param>
/// <param name="CandidatePairs">Pairs the broad phase produced — how much work the grid saved.</param>
/// <param name="TestedPairs">Pairs that survived the box test and had their segments measured.</param>
public readonly record struct WireSweepCounters(int Wires, int CandidatePairs, int TestedPairs)
{
    /// <summary>Every unordered pair there would have been without a broad phase.</summary>
    public long AllPairs => (long)Wires * (Wires - 1) / 2;
}

/// <summary>
/// Bounding-box-accelerated all-pairs clearance over a set of wires. Framework-free and stateless
/// between calls — build it, query it, drop it.
/// </summary>
public sealed class WirePairSweep
{
    private readonly IReadOnlyList<Wire> _wires;
    private readonly Bbox3[] _boxes;
    private readonly double _cell;
    private readonly Dictionary<(int, int), List<int>> _grid;

    /// <summary>Above this many cells per axis the grid is coarsened — a pathological aspect ratio
    /// must cost a slower sweep, never unbounded memory.</summary>
    private const int MaxCellsPerAxis = 512;

    /// <param name="wires">The wires to index, in a stable order the caller can map back.</param>
    /// <param name="gapNm">
    /// The largest clearance any query will ask about. Boxes are grown by half of it, so a pair that
    /// could be within <paramref name="gapNm"/> is always a candidate. Passing a larger value than
    /// needed costs candidates, never correctness.
    /// </param>
    public WirePairSweep(IReadOnlyList<Wire> wires, double gapNm)
    {
        ArgumentNullException.ThrowIfNull(wires);
        _wires = wires;

        _boxes = new Bbox3[wires.Count];
        var world = Bbox3.Empty;
        double meanExtent = 0.0;

        for (int i = 0; i < wires.Count; i++)
        {
            // The box carries the wire's own radius plus half the query gap, so two boxes that touch
            // bound a pair that could be exactly `gapNm` apart. Half each, not the whole gap each —
            // the gap is between them, not around each.
            _boxes[i] = WireGeometry3D.MetalBboxOf(wires[i]).Expand(Math.Max(0.0, gapNm) / 2.0);
            if (_boxes[i].IsEmpty) continue;

            world = world.Union(_boxes[i]);
            meanExtent += Math.Max(_boxes[i].MaxX - _boxes[i].MinX, _boxes[i].MaxY - _boxes[i].MinY);
        }

        if (wires.Count > 0) meanExtent /= wires.Count;

        // A cell about the size of a wire is the right trade: smaller and a single wire is inserted
        // into many cells, larger and every cell holds most of the design.
        _cell = Math.Max(1.0, Math.Max(meanExtent, Math.Max(1.0, gapNm)));

        if (!world.IsEmpty)
        {
            double spanX = world.MaxX - world.MinX;
            double spanY = world.MaxY - world.MinY;
            double needed = Math.Max(spanX, spanY) / MaxCellsPerAxis;
            if (needed > _cell) _cell = needed;
        }

        _grid = new Dictionary<(int, int), List<int>>(wires.Count * 2);
        for (int i = 0; i < wires.Count; i++)
        {
            if (_boxes[i].IsEmpty) continue;
            foreach (var key in CellsOf(_boxes[i]))
            {
                if (!_grid.TryGetValue(key, out var bucket)) _grid[key] = bucket = [];
                bucket.Add(i);
            }
        }
    }

    /// <summary>What the most recent query cost.</summary>
    public WireSweepCounters Counters { get; private set; }

    /// <summary>
    /// Every pair whose surface-to-surface clearance is strictly below <paramref name="limitNm"/>,
    /// with the measured clearance.
    /// </summary>
    /// <param name="includePair">
    /// Optional filter applied BEFORE any geometry is measured — used to restrict a rule to the two
    /// wire sets it names, so a check over one array pair does not pay for the whole design.
    /// </param>
    public IReadOnlyList<WirePair> FindCloserThan(double limitNm, Func<int, int, bool>? includePair = null)
    {
        var results = new List<WirePair>();
        var seen = new HashSet<long>();
        int candidates = 0, tested = 0;

        foreach (var bucket in _grid.Values)
        {
            for (int bi = 0; bi < bucket.Count; bi++)
            for (int bj = bi + 1; bj < bucket.Count; bj++)
            {
                int a = bucket[bi], b = bucket[bj];
                if (a > b) (a, b) = (b, a);

                // A wire spans several cells, so the same pair reaches this point once per shared
                // cell. Dedup here rather than by intersecting cell lists: the pair count is small
                // by the time it gets here, and a hash set is simpler than the alternative.
                if (!seen.Add((long)a * _wires.Count + b)) continue;
                candidates++;

                if (includePair is not null && !includePair(a, b)) continue;
                if (!_boxes[a].WithinOf(_boxes[b], 0.0)) continue;

                tested++;
                double clearance = WireGeometry3D.Clearance(_wires[a], _wires[b]);
                if (clearance < limitNm) results.Add(new WirePair(a, b, clearance));
            }
        }

        Counters = new WireSweepCounters(_wires.Count, candidates, tested);

        // Deterministic order: two runs over unchanged geometry must produce identical lists, because
        // the panel's selection, the markers and every test depend on it.
        results.Sort(static (x, y) => x.A != y.A ? x.A.CompareTo(y.A) : x.B.CompareTo(y.B));
        return results;
    }

    /// <summary>
    /// Every pair the broad phase cannot rule out, WITHOUT measuring any geometry — for a caller that
    /// evaluates something other than clearance on each pair (an assembly rule's whole predicate, for
    /// instance) and only wants the pair count pruned.
    /// </summary>
    /// <param name="xyOnly">
    /// Ignore z in the box test. <b>Required for a foot-pitch rule, and getting it wrong is a silent
    /// miss.</b> Two wires stacked vertically are far apart in 3D and directly on top of each other in
    /// plan, so a 3D box test would prune exactly the pair a pitch rule exists to catch.
    /// </param>
    public IReadOnlyList<(int A, int B)> CandidatePairs(bool xyOnly = false)
    {
        var results = new List<(int, int)>();
        var seen = new HashSet<long>();
        int candidates = 0;

        foreach (var bucket in _grid.Values)
        {
            for (int bi = 0; bi < bucket.Count; bi++)
            for (int bj = bi + 1; bj < bucket.Count; bj++)
            {
                int a = bucket[bi], b = bucket[bj];
                if (a > b) (a, b) = (b, a);
                if (!seen.Add((long)a * _wires.Count + b)) continue;

                candidates++;
                bool near = xyOnly
                    ? WithinXy(_boxes[a], _boxes[b])
                    : _boxes[a].WithinOf(_boxes[b], 0.0);

                if (near) results.Add((a, b));
            }
        }

        Counters = new WireSweepCounters(_wires.Count, candidates, results.Count);
        results.Sort(static (x, y) => x.Item1 != y.Item1 ? x.Item1.CompareTo(y.Item1) : x.Item2.CompareTo(y.Item2));
        return results;
    }

    private static bool WithinXy(in Bbox3 a, in Bbox3 b) =>
        a.MinX <= b.MaxX && b.MinX <= a.MaxX &&
        a.MinY <= b.MaxY && b.MinY <= a.MaxY;

    /// <summary>
    /// The minimum clearance between one wire and every other wire the broad phase says could be
    /// near it. <see cref="double.PositiveInfinity"/> when nothing is in range.
    /// </summary>
    public double MinClearanceFrom(int index, Func<int, bool>? includeOther = null)
    {
        if (index < 0 || index >= _wires.Count || _boxes[index].IsEmpty) return double.PositiveInfinity;

        double best = double.PositiveInfinity;
        var seen = new HashSet<int>();

        foreach (var key in CellsOf(_boxes[index]))
        {
            if (!_grid.TryGetValue(key, out var bucket)) continue;
            foreach (int other in bucket)
            {
                if (other == index || !seen.Add(other)) continue;
                if (includeOther is not null && !includeOther(other)) continue;
                if (!_boxes[index].WithinOf(_boxes[other], 0.0)) continue;

                double d = WireGeometry3D.Clearance(_wires[index], _wires[other]);
                if (d < best) best = d;
            }
        }
        return best;
    }

    /// <summary>
    /// Every pair of wires whose metal touches or interpenetrates — a geometry error rather than a
    /// clearance shortfall, and true regardless of what any assembly house states.
    ///
    /// <para><b>Contact is <c>clearance &#x2264; 0</c>, so the query limit has to be strictly
    /// positive.</b> <see cref="FindCloserThan"/> reports <c>clearance &lt; limit</c>, so passing 0
    /// finds interpenetration but silently skips two wires that touch EXACTLY — which is not an
    /// exotic case: an array laid out on a pitch equal to its own wire diameter is drawn that way on
    /// purpose. <paramref name="toleranceNm"/> is the width of that boundary, and the caller
    /// (<c>WBondBuiltInRules.TouchToleranceNm</c>) is where the choice of a picometre is argued.</para>
    ///
    /// <para>Build the sweep with <c>gapNm</c> at least <paramref name="toleranceNm"/> so the broad
    /// phase cannot prune a pair the narrow phase would have reported.</para>
    /// </summary>
    public IReadOnlyList<WirePair> FindTouching(double toleranceNm) =>
        FindCloserThan(Math.Max(double.Epsilon, toleranceNm));

    private IEnumerable<(int, int)> CellsOf(Bbox3 box)
    {
        int x0 = (int)Math.Floor(box.MinX / _cell);
        int x1 = (int)Math.Floor(box.MaxX / _cell);
        int y0 = (int)Math.Floor(box.MinY / _cell);
        int y1 = (int)Math.Floor(box.MaxY / _cell);

        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
            yield return (x, y);
    }
}
