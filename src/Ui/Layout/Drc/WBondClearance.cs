// Wire-to-ARTWORK clearance — the half of the 3D predicate class that needs layout types
// (brief-wbond-wbd §M3). The pure wire-to-wire half is `CircuitRF.WBond.WireGeometry3D`, which stays
// in the framework-free project because it needs nothing but `Point3`.
//
// ── R-wbd-1: a wire point is NANOMETRES; a LayoutShape is DBU ────────────────────────────────────
//
// This bridge has already shipped broken twice — once in `WBondRenderer` (nm converted to microns
// instead of to DBU) and once in `DxfWireIo` (nm fed straight to a DBU-taking writer) — and both
// times it was invisible on every default document, because at the default 1,000 DBU/µm the two
// units coincide exactly and a missing conversion is indistinguishable from a correct one.
//
// <b>This file converts at exactly one crossing, and it converts the LAYOUT INTO NANOMETRES</b>
// rather than the wires into DBU. Wires are the only 3D thing here and the wire side is where the
// precision matters: a wire's z has no DBU equivalent at all, so pushing everything into DBU would
// need a second, invented convention for the vertical axis. `WBondSnap.ToNm` is the one function that
// does it, reused rather than re-derived.
//
// Every test of anything in this file runs at a NON-DEFAULT DbuPerMicron. A suite built only on the
// default cannot tell a correct conversion from a missing one.
//
// ── The z assumption, stated rather than left to be discovered ──────────────────────────────────
//
// Artwork on a conductor layer sits at that layer's own height in the stackup, and assuming z = 0 for
// everything is exactly the kind of silent wrongness this phase exists to avoid — a wire arching
// 20 mil over a pad would measure its clearance to a plane 60 mil below the pad's real surface.
// <see cref="WBondLayerHeights"/> resolves a height per drawing layer from the stackup; what it
// measures FROM is documented on that type, because "z = 0" has to mean something specific and the
// wire model's own z origin is the ground plane.

using Clipper2Lib;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Layout.Drc;

/// <summary>
/// The height, in nanometres above the wire model's own z origin, of each drawing layer's artwork.
///
/// <para><b>What z = 0 means.</b> A <c>Point3.Z</c> is measured from the ground plane the method of
/// images reflects in (wbond.md §3.2) — the model's z origin IS the ground reference. So a drawing
/// layer's height here is the height of its conductor's TOP SURFACE above the top surface of the
/// lowest ground-designated conductor in the stackup, which is the same reference
/// <c>SubstrateResolver</c> already resolves ground against. Getting this wrong is a 2%-scale trap
/// on a real stackup and a total misread on a thick board.</para>
///
/// <para><b>When the stackup cannot answer, every layer resolves to z = 0 and the run says so.</b>
/// That is the honest degradation: a technology with no stackup has not told us where its metal is,
/// and inventing heights would produce clearances that look authoritative and are fiction.</para>
/// </summary>
public sealed class WBondLayerHeights
{
    private readonly Dictionary<LayerKey, long> _zByLayer = [];

    private WBondLayerHeights(IReadOnlyList<string> diagnostics) => Diagnostics = diagnostics;

    public IReadOnlyList<string> Diagnostics { get; }

    /// <summary>True when the stackup gave a real answer — otherwise every layer is at z = 0.</summary>
    public bool Resolved { get; private init; }

    /// <summary>The artwork height for a drawing layer, in nanometres. Zero when unresolved.</summary>
    public long ZNmOf(LayerKey key) => _zByLayer.TryGetValue(key, out long z) ? z : 0L;

    /// <summary>
    /// Walks the stackup top-to-bottom accumulating thickness, then re-references every conductor's
    /// top surface to the lowest ground-designated conductor's top surface.
    /// </summary>
    /// <remarks>
    /// Stackup thicknesses are stored in DBU at <see cref="LayoutUnits.DefaultDbuPerMicron"/> — not at
    /// the layout's own resolution. Neither <see cref="Technology"/> nor the `.ctech` carries a
    /// resolution, so that fixed convention is what <c>SubstrateResolver</c> already relies on and is
    /// settled rather than guessed. Using the layout's <c>DbuPerMicron</c> here instead would rescale
    /// every height by the ratio and be invisible on a default document.
    /// </remarks>
    public static WBondLayerHeights Resolve(Technology? tech)
    {
        var problems = new List<string>();

        if (tech is null || tech.Stackup.Layers.Count == 0)
        {
            problems.Add("The technology states no stackup, so every layer's artwork was measured at " +
                         "the ground plane (z = 0). Wire-to-layer clearances are not trustworthy until " +
                         "the stackup is filled in.");
            return new WBondLayerHeights(problems) { Resolved = false };
        }

        // Total thickness first, so heights can be accumulated downward and then read as heights
        // above the bottom of the stack.
        long total = 0;
        foreach (var sl in tech.Stackup.Layers)
            if (sl.Kind != StackupKind.Via) total += Math.Max(0, sl.ThicknessDbu);

        var heights = new WBondLayerHeights(problems) { Resolved = true };

        long cursorDbu = total;            // top of the stack, measured from its bottom
        long? groundTopDbu = null;
        var conductorTops = new List<(StackupLayer Layer, long TopDbu)>();

        foreach (var sl in tech.Stackup.Layers)
        {
            if (sl.Kind == StackupKind.Via) continue;    // a via has no z band of its own

            long topDbu = cursorDbu;
            cursorDbu -= Math.Max(0, sl.ThicknessDbu);

            if (sl.Kind != StackupKind.Conductor) continue;

            conductorTops.Add((sl, topDbu));

            // The LOWEST ground-designated conductor wins — walking top to bottom, the last one seen.
            if (sl.IsGroundReference) groundTopDbu = topDbu;
        }

        if (groundTopDbu is null)
        {
            problems.Add("No conductor in the stackup is marked as the ground reference, so wire " +
                         "heights were measured from the bottom of the stack. Mark the ground plane " +
                         "in the Technology Editor's Stackup tab.");
            groundTopDbu = 0;
        }

        foreach (var (layer, topDbu) in conductorTops)
        {
            long zNm = DbuToNm(topDbu - groundTopDbu.Value);
            foreach (var key in layer.DrawingLayers) heights._zByLayer[key] = zNm;
        }

        return heights;
    }

    /// <summary>Stackup DBU (always at the fixed default resolution) to nanometres.</summary>
    private static long DbuToNm(long dbu) =>
        (long)Math.Round(dbu * 1000.0 / LayoutUnits.DefaultDbuPerMicron, MidpointRounding.AwayFromZero);
}

/// <summary>
/// A set of 2D artwork edges lifted to a stated height and indexed for nearest-distance queries from
/// a wire.
///
/// <para><b>Coordinates are NANOMETRES throughout</b>, converted once at construction — see this
/// file's header for why the layout is converted into the wires' units rather than the reverse.</para>
///
/// <para>The index is a uniform XY grid, for the same reason <c>WirePairSweep</c> uses one: the
/// artwork near a bond wire is a handful of pads on one die, all of similar size. Without it, one
/// wire against a ground pour of a few thousand edges would be a few thousand segment distances, and
/// the check runs that per wire per rule.</para>
/// </summary>
public sealed class PlanarEdgeIndex
{
    private readonly struct Edge(long x0, long y0, long x1, long y1)
    {
        public readonly long X0 = x0, Y0 = y0, X1 = x1, Y1 = y1;
    }

    private readonly List<Edge> _edges = [];
    private readonly Dictionary<(int, int), List<int>> _grid = [];
    private readonly double _cell;
    private readonly long _zNm;

    // The occupied cell range, so a nearest-edge query knows when it has covered everything.
    private int _cellMinX = 0, _cellMaxX = 0, _cellMinY = 0, _cellMaxY = 0;

    private const int MaxCellsPerAxis = 512;

    private PlanarEdgeIndex(long zNm, double cell)
    {
        _zNm  = zNm;
        _cell = cell;
    }

    public int EdgeCount => _edges.Count;

    /// <summary>The height these edges were lifted to.</summary>
    public long ZNm => _zNm;

    /// <summary>
    /// Builds an index over a Clipper region's boundary, converting DBU to nanometres once.
    /// </summary>
    /// <param name="dbuPerMicron">The LAYOUT's own resolution — the R-wbd-1 crossing.</param>
    public static PlanarEdgeIndex Build(Paths64 region, int dbuPerMicron, long zNm)
    {
        ArgumentNullException.ThrowIfNull(region);

        var raw = new List<Edge>();
        long minX = long.MaxValue, minY = long.MaxValue, maxX = long.MinValue, maxY = long.MinValue;

        foreach (var path in region)
        {
            if (path.Count < 2) continue;
            for (int i = 0; i < path.Count; i++)
            {
                var a = path[i];
                var b = path[(i + 1) % path.Count];      // implicitly closed, like every ring here

                long ax = WBondSnap.ToNm(a.X, dbuPerMicron), ay = WBondSnap.ToNm(a.Y, dbuPerMicron);
                long bx = WBondSnap.ToNm(b.X, dbuPerMicron), by = WBondSnap.ToNm(b.Y, dbuPerMicron);

                raw.Add(new Edge(ax, ay, bx, by));
                minX = Math.Min(minX, Math.Min(ax, bx));
                minY = Math.Min(minY, Math.Min(ay, by));
                maxX = Math.Max(maxX, Math.Max(ax, bx));
                maxY = Math.Max(maxY, Math.Max(ay, by));
            }
        }

        double cell = 1.0;
        if (raw.Count > 0)
        {
            double span = Math.Max(maxX - minX, maxY - minY);
            // Aim for roughly one edge per cell, then floor the cell size so a huge extent cannot
            // produce an unbounded number of buckets.
            cell = Math.Max(1.0, span / Math.Max(1.0, Math.Sqrt(raw.Count)));
            cell = Math.Max(cell, span / MaxCellsPerAxis);
        }

        var index = new PlanarEdgeIndex(zNm, cell);
        bool first = true;

        foreach (var e in raw)
        {
            int i = index._edges.Count;
            index._edges.Add(e);

            int x0 = (int)Math.Floor(Math.Min(e.X0, e.X1) / cell);
            int x1 = (int)Math.Floor(Math.Max(e.X0, e.X1) / cell);
            int y0 = (int)Math.Floor(Math.Min(e.Y0, e.Y1) / cell);
            int y1 = (int)Math.Floor(Math.Max(e.Y0, e.Y1) / cell);

            if (first)
            {
                index._cellMinX = x0; index._cellMaxX = x1;
                index._cellMinY = y0; index._cellMaxY = y1;
                first = false;
            }
            else
            {
                index._cellMinX = Math.Min(index._cellMinX, x0);
                index._cellMaxX = Math.Max(index._cellMaxX, x1);
                index._cellMinY = Math.Min(index._cellMinY, y0);
                index._cellMaxY = Math.Max(index._cellMaxY, y1);
            }

            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                if (!index._grid.TryGetValue((x, y), out var bucket)) index._grid[(x, y)] = bucket = [];
                bucket.Add(i);
            }
        }

        return index;
    }

    /// <summary>
    /// Builds an index over a plain rectangle's boundary — used for the layout extent, which is what
    /// <c>dist_to_edge</c> measures against.
    /// </summary>
    public static PlanarEdgeIndex BuildRectangle(Bbox box, int dbuPerMicron, long zNm)
    {
        var ring = new Path64
        {
            new Point64(box.MinX, box.MinY),
            new Point64(box.MaxX, box.MinY),
            new Point64(box.MaxX, box.MaxY),
            new Point64(box.MinX, box.MaxY),
        };
        return Build([ring], dbuPerMicron, zNm);
    }

    /// <summary>
    /// Minimum distance from the wire's METAL SURFACE to the nearest indexed edge, in nanometres.
    /// <see cref="double.PositiveInfinity"/> when the index is empty.
    ///
    /// <para>Expands the search radially: only the cells within the best distance found so far are
    /// ever visited, so a wire in the middle of a large design pays for its own neighbourhood rather
    /// than for the design.</para>
    /// </summary>
    public double MinDistanceTo(Wire wire)
    {
        if (_edges.Count == 0 || wire.Points.Count < 2) return double.PositiveInfinity;

        var wireBox = WireGeometry3D.BboxOf(wire);

        int cx0 = (int)Math.Floor(wireBox.MinX / _cell);
        int cx1 = (int)Math.Floor(wireBox.MaxX / _cell);
        int cy0 = (int)Math.Floor(wireBox.MinY / _cell);
        int cy1 = (int)Math.Floor(wireBox.MaxY / _cell);

        // How many rings it takes to have covered the whole index from anywhere — the loop's
        // termination bound, so a wire far outside the artwork still finishes rather than widening
        // forever.
        int ringLimit = Math.Max(
            Math.Max(Math.Abs(cx0 - _cellMinX), Math.Abs(_cellMaxX - cx1)),
            Math.Max(Math.Abs(cy0 - _cellMinY), Math.Abs(_cellMaxY - cy1))) + 1;

        double best = double.PositiveInfinity;
        var visited = new HashSet<int>();

        for (int ring = 0; ring <= ringLimit; ring++)
        {
            // Nothing in this ring or beyond can be closer than this in XY, so once the best answer
            // is inside that bound the search is finished — which is what keeps a wire in the middle
            // of a large design paying for its own neighbourhood rather than for the design.
            if (ring > 1 && (ring - 1) * _cell > best) break;

            for (int x = cx0 - ring; x <= cx1 + ring; x++)
            for (int y = cy0 - ring; y <= cy1 + ring; y++)
            {
                // Only the perimeter of the widened box is new on rings after the first.
                if (ring > 0 && x > cx0 - ring && x < cx1 + ring && y > cy0 - ring && y < cy1 + ring) continue;
                if (!_grid.TryGetValue((x, y), out var bucket)) continue;

                foreach (int i in bucket)
                {
                    if (!visited.Add(i)) continue;
                    var e = _edges[i];
                    double d = WireGeometry3D.DistanceToPlanarSegment(wire, e.X0, e.Y0, e.X1, e.Y1, _zNm);
                    if (d < best) best = d;
                }
            }
        }

        return best;
    }
}
