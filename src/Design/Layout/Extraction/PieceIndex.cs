// The broad phase PieceAt never had — brief-lvs-2-shared-extraction.md R-lvs2-3.
//
// ── WHY IT WAS FREE AND IS NOT ANY MORE ────────────────────────────────────────────────────────
//
// PdnCopperPieces.PieceAt was a linear scan over every piece with a bbox rejection and then an
// exact Clipper2 containment test. At railRF's scale — one rail, tens of anchors, a partition of a
// few dozen pieces — it cost nothing measurable. LVS asks the same question once per PIN: 3,000
// parts at four pins each against a partition of thousands of pieces is the entire run, and the
// answer is the same answer either way.
//
// ── A UNIFORM GRID, NOT AN R-TREE (R-lvs2-3a) ──────────────────────────────────────────────────
//
// WirePairSweep's header is the precedent and its reasoning carries verbatim: LayoutSpatialIndex is
// the right structure for 10^5-10^6 shapes of wildly varying size spread over a board, and it is
// typed to LayoutShape lists besides. A grid is a fraction of the code and there is no tree to keep
// fresh. It is rebuilt from scratch per run and never maintained incrementally (R-lvs2-3c), for
// WirePairSweep's reason: a run already re-reads the whole design, and a stale acceleration
// structure is a source of silently missed answers.
//
// ── THE DEGENERATE CASE IS BOUNDED, AND HERE IT IS (R-lvs2-3b) ─────────────────────────────────
//
// A board-wide ground pour's bounding box covers every cell of the grid, so it is a candidate for
// every query. THAT IS FINE HERE, and only here, BECAUSE THE NUMBER OF SUCH PIECES IS SMALL — a
// handful of pours on a real board, each tested exactly, per query. Rather than smear one pour
// across a quarter of a million buckets, a piece spanning more than MaxCellsPerPiece cells is put
// on a single "spans the board" list that every query tests.
//
// IT IS NOT FINE IF SOMEBODY LATER REACHES FOR THE SAME GRID OVER SHAPES RATHER THAN PIECES. A
// board has one ground POUR and tens of thousands of ground SHAPES, and every one of them on the
// everywhere-list would be the linear scan back again, with a grid in front of it.
//
// ── THE EXACT ANSWER IS UNCHANGED (R-lvs2-3e) ──────────────────────────────────────────────────
//
// The grid narrows candidates; Regions.Contains still decides, and candidates are tested in
// ASCENDING PIECE ORDER so a query covered by pieces on several layers returns the same one the
// scan returned. A broad phase that changed which piece answers is a broad phase that changed the
// answer.

using CircuitRF.Design.Layout.Drc;

namespace CircuitRF.Design.Layout.Extraction;

/// <summary>What one indexed lookup actually cost — R-lvs2-3's gate, and WireSweepCounters' shape
/// for WireSweepCounters' reason: a structural property is asserted on a COUNTER, never on a
/// clock.</summary>
/// <param name="Pieces">How many pieces were indexed.</param>
/// <param name="Candidates">Pieces the grid offered — bucket plus the board-spanning list.</param>
/// <param name="ExactTests">Candidates that survived the bbox test and had Clipper run on them.
/// This is the number that must stay bounded as the partition grows.</param>
public readonly record struct PieceLookupCounters(int Pieces, int Candidates, int ExactTests);

/// <summary>
/// A uniform grid over piece bounding boxes. Build it, query it, drop it.
/// </summary>
internal sealed class PieceIndex
{
    /// <summary>A pathological aspect ratio must cost a slower query, never unbounded memory.</summary>
    private const int MaxCellsPerAxis = 512;

    /// <summary>Above this, a piece goes on <see cref="_spanning"/> instead of into every cell it
    /// covers — the board-wide pour of R-lvs2-3b.</summary>
    private const int MaxCellsPerPiece = 64;

    private readonly IReadOnlyList<DrcNetPiece> _pieces;
    private readonly Dictionary<(int, int), List<int>> _grid = [];
    private readonly List<int> _spanning = [];
    private readonly Bbox _extent;
    private readonly long _cell;

    public PieceIndex(IReadOnlyList<DrcNetPiece> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        _pieces = pieces;

        var extent = Bbox.Empty;
        var spans = new List<long>(pieces.Count);
        foreach (var p in pieces)
        {
            if (p.Bounds.IsEmpty) continue;
            extent = extent.Union(p.Bounds);
            spans.Add(Math.Max(p.Bounds.MaxX - p.Bounds.MinX, p.Bounds.MaxY - p.Bounds.MinY));
        }

        _extent = extent;
        if (spans.Count == 0) { _cell = 1; return; }

        // R-lvs2-3d. THE MEDIAN, and it is not configurable. A cell about the size of a typical
        // piece is the right trade — smaller and one piece is inserted into many cells, larger and
        // one cell holds most of the board — and the MEDIAN rather than the mean because one
        // board-wide pour drags a mean up by orders of magnitude and would coarsen the grid into
        // uselessness on exactly the boards that need it. A knob here is a knob nobody can set
        // correctly.
        spans.Sort();
        _cell = Math.Max(1, spans[spans.Count / 2]);

        long span = Math.Max(_extent.MaxX - _extent.MinX, _extent.MaxY - _extent.MinY);
        long needed = span / MaxCellsPerAxis + 1;
        if (needed > _cell) _cell = needed;

        for (int i = 0; i < pieces.Count; i++)
        {
            if (pieces[i].Bounds.IsEmpty) continue;

            var (x0, y0, x1, y1) = CellsOf(pieces[i].Bounds);
            long cells = (x1 - x0 + 1) * (y1 - y0 + 1);
            if (cells > MaxCellsPerPiece) { _spanning.Add(i); continue; }

            for (int cx = (int)x0; cx <= x1; cx++)
            for (int cy = (int)y0; cy <= y1; cy++)
            {
                if (!_grid.TryGetValue((cx, cy), out var bucket)) _grid[(cx, cy)] = bucket = [];
                bucket.Add(i);          // ascending i, by construction of this loop
            }
        }
    }

    /// <summary>What the most recent query cost.</summary>
    public PieceLookupCounters Counters { get; private set; }

    /// <summary>How many pieces span so much of the board that every query tests them — the
    /// handful of pours R-lvs2-3b is about, exposed so a gate can assert it stays a handful.</summary>
    public int SpanningPieces => _spanning.Count;

    /// <summary>
    /// The net index of the piece covering (<paramref name="x"/>, <paramref name="y"/>) on
    /// <paramref name="layer"/>, or <c>-1</c>.
    /// </summary>
    /// <param name="layer">Null searches every layer, which is what a via wants — a via IS the
    /// thing that joins them, so restricting it to one is asking the wrong question.</param>
    public int PieceAt(long x, long y, LayerKey? layer)
    {
        int candidates = 0, exact = 0, answer = -1;

        // Outside the whole partition's extent there is no bucket and no piece can contain the
        // point — only the board-spanning list is walked, and its bbox test rejects every entry.
        List<int>? bucket = null;
        if (_extent.Contains(x, y))
            _grid.TryGetValue(((int)CellOf(x, _extent.MinX), (int)CellOf(y, _extent.MinY)), out bucket);

        // Merged ascending, so the FIRST piece that contains the point is the same one the linear
        // scan would have returned (R-lvs2-3e).
        int a = 0, b = 0;
        while (true)
        {
            int i;
            bool fromBucket = bucket is not null && a < bucket.Count;
            bool fromSpan = b < _spanning.Count;
            if (fromBucket && fromSpan) i = bucket![a] < _spanning[b] ? bucket[a++] : _spanning[b++];
            else if (fromBucket) i = bucket![a++];
            else if (fromSpan) i = _spanning[b++];
            else break;

            candidates++;
            var piece = _pieces[i];
            if (layer is { } only && piece.Layer != only) continue;
            if (!piece.Bounds.Contains(x, y)) continue;

            exact++;
            if (Regions.Contains(piece.Paths, x, y)) { answer = piece.Net; break; }
        }

        Counters = new PieceLookupCounters(_pieces.Count, candidates, exact);
        return answer;
    }

    /// <summary>Floor division — <c>long</c>'s own truncates toward zero, which puts the cells
    /// either side of the origin into one.</summary>
    private long CellOf(long v, long origin)
    {
        long n = v - origin, q = n / _cell;
        return n % _cell != 0 && n < 0 ? q - 1 : q;
    }

    private (long X0, long Y0, long X1, long Y1) CellsOf(Bbox b) =>
        (CellOf(b.MinX, _extent.MinX), CellOf(b.MinY, _extent.MinY),
         CellOf(b.MaxX, _extent.MinX), CellOf(b.MaxY, _extent.MinY));
}
