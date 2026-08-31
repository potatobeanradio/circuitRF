namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Local wire geometry helpers for 6d connection-state detection.
/// Determines "is this port/endpoint connected to something?" using only
/// geometric adjacency — NOT global net extraction (which is 6e).
/// No Avalonia types — headless-testable.
/// </summary>
public static class WireGeometry
{
    private const double SnapTol = 6.0;  // world units

    /// <summary>
    /// Normalizes a wire point list: deduplicates consecutive identical points,
    /// merges collinear same-axis interior runs into a single segment, and drops
    /// zero-length segments. The result is a clean alternating H/V orthogonal polyline.
    /// May return a list with fewer than 2 points when the input collapses to a single
    /// or zero distinct points — callers that need a valid wire should check Count ≥ 2.
    /// Framework-free; shared by wire draw, segment-move commit, and split-piece cleanup.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> NormalizePoints(
        IReadOnlyList<(double X, double Y)> pts)
    {
        if (pts.Count < 2) return pts;

        var result = new List<(double X, double Y)> { pts[0] };
        for (int i = 1; i < pts.Count; i++)
        {
            var prev = result[^1];
            var curr = pts[i];

            // Drop zero-length: curr coincides with prev.
            if (Math.Abs(curr.X - prev.X) < 1e-6 && Math.Abs(curr.Y - prev.Y) < 1e-6) continue;

            // Drop collinear interior: prev→curr and curr→next are on the same axis.
            if (i < pts.Count - 1)
            {
                var next = pts[i + 1];
                bool collinearH = Math.Abs(curr.Y - prev.Y) < 1e-6 && Math.Abs(next.Y - curr.Y) < 1e-6;
                bool collinearV = Math.Abs(curr.X - prev.X) < 1e-6 && Math.Abs(next.X - curr.X) < 1e-6;
                if (collinearH || collinearV) continue;
            }

            result.Add(curr);
        }

        return result;
    }

    /// <summary>
    /// Applies simple orthogonal routing to a two-point wire:
    /// returns the intermediate waypoint(s) needed to route from (x0,y0) to (x1,y1)
    /// with at most one 90° bend. Returns the full point list including endpoints.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> OrthogonalRoute(
        double x0, double y0, double x1, double y1)
    {
        // Prefer horizontal-first routing (bend at (x1, y0))
        if (Math.Abs(x1 - x0) < 1e-6) return [(x0, y0), (x1, y1)];   // vertical
        if (Math.Abs(y1 - y0) < 1e-6) return [(x0, y0), (x1, y1)];   // horizontal
        return [(x0, y0), (x1, y0), (x1, y1)];                         // L-shape: H then V
    }

    /// <summary>
    /// Re-draws a wire whose ONE or TWO endpoints have been carried to a new place by a moved
    /// component pin, keeping the shape the user drew.
    ///
    /// <para><b>Why this is not <see cref="OrthogonalRoute"/>.</b> The follow paths used to throw the
    /// whole polyline away and redraw it as a bare L between the two endpoints, which is a different
    /// wire wherever the original had more than one bend. Three things went wrong at once, all
    /// reported from real sheets (2026-08-30): a run the user placed on a row moved off it; a
    /// vertical run came back horizontal (and landed on top of an unrelated vertical, so the reader
    /// could no longer tell two nets apart); and — the serious one — <b>every mid-span tap on that
    /// wire was dropped</b>, because the L simply does not pass through where the T-junctions were.
    /// A capacitor pair tapping the middle of an inductor's wire silently left the net when the
    /// inductor was nudged one grid step.</para>
    ///
    /// <para><b>The rule instead: a moved endpoint deforms its own wire as little as the geometry
    /// allows.</b> An orthogonal polyline alternates H and V legs, so the delta at a moved end
    /// splits into a component ALONG that end's leg — absorbed by lengthening it, changing nothing
    /// else — and a component ACROSS it, which is passed to the one neighbouring vertex, where the
    /// next leg (perpendicular by construction) absorbs it as its own length. The propagation stops
    /// there; nothing past the second vertex ever moves, so bends, rows and columns survive and so
    /// does every tap that is not on the two legs that changed.</para>
    ///
    /// <para>When the neighbouring vertex is the far ENDPOINT, it is held by whatever is at the other
    /// end and cannot absorb anything — a plain two-point wire is exactly this case. Then a new
    /// elbow is inserted AT THE MOVED END, which leaves the original leg (and everything tapping it)
    /// exactly where it was. This is the vertical jog a user expects to see appear under a part they
    /// nudged off its row.</para>
    ///
    /// <para>Both endpoints moving by the same delta is a rigid translation, and is done as one.
    /// Anything this cannot express — a zero-length or non-orthogonal leg — falls back to
    /// <see cref="OrthogonalRoute"/>, i.e. to what shipped before.</para>
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> FollowEndpoints(
        IReadOnlyList<(double X, double Y)> orig,
        bool startMoved, double nsx, double nsy,
        bool endMoved,   double nex, double ney)
    {
        const double eps = 1e-6;
        if (orig.Count < 2 || (!startMoved && !endMoved)) return orig;

        if (startMoved && endMoved)
        {
            double dsx = nsx - orig[0].X,  dsy = nsy - orig[0].Y;
            double dex = nex - orig[^1].X, dey = ney - orig[^1].Y;
            if (Math.Abs(dsx - dex) < eps && Math.Abs(dsy - dey) < eps)
                return orig.Select(p => (p.X + dsx, p.Y + dsy)).ToList();

            var once = RubberBandEnd(orig, atStart: true, nsx, nsy);
            return RubberBandEnd(once, atStart: false, nex, ney);
        }

        return startMoved
            ? RubberBandEnd(orig, atStart: true,  nsx, nsy)
            : RubberBandEnd(orig, atStart: false, nex, ney);
    }

    /// <summary>
    /// One end of <paramref name="pts"/> moves to (nx,ny); the rest deforms as little as it can.
    /// See <see cref="FollowEndpoints"/> for the rule this implements.
    /// </summary>
    private static IReadOnlyList<(double X, double Y)> RubberBandEnd(
        IReadOnlyList<(double X, double Y)> pts, bool atStart, double nx, double ny)
    {
        const double eps = 1e-6;
        if (pts.Count < 2) return pts;

        int iEnd  = atStart ? 0 : pts.Count - 1;          // the endpoint that moves
        int iNext = atStart ? 1 : pts.Count - 2;          // its one neighbour
        var p0 = pts[iEnd];
        var p1 = pts[iNext];

        double dx = nx - p0.X, dy = ny - p0.Y;
        if (Math.Abs(dx) < eps && Math.Abs(dy) < eps) return pts;

        bool legH = Math.Abs(p1.Y - p0.Y) < eps;
        bool legV = Math.Abs(p1.X - p0.X) < eps;
        if (legH == legV)                                  // zero-length or diagonal — not our shape
            return OrthogonalRouteBetween(pts, atStart, nx, ny);

        var res = pts.ToList();
        res[iEnd] = (nx, ny);

        // The delta along the moved end's own leg just changes that leg's length.
        double across = legH ? dy : dx;
        if (Math.Abs(across) < eps) return res;

        // Across the leg: hand it to the neighbour, but only when the neighbour is an INTERIOR
        // vertex (the far endpoint is held) AND the leg past it is perpendicular, so taking the
        // shift only changes ITS length too.
        int iAfter = atStart ? 2 : pts.Count - 3;
        if (iAfter >= 0 && iAfter < pts.Count)
        {
            var p2 = pts[iAfter];
            bool nextPerp = legH
                ? Math.Abs(p2.X - p1.X) < eps            // H leg → neighbour leg must be V
                : Math.Abs(p2.Y - p1.Y) < eps;           // V leg → neighbour leg must be H
            if (nextPerp)
            {
                res[iNext] = legH ? (p1.X, p1.Y + dy) : (p1.X + dx, p1.Y);
                return res;
            }
        }

        // Neighbour cannot absorb it: elbow at the moved end, leaving the original leg on its row
        // (or column) — and with it every tap that leg carries.
        var elbow = legH ? (nx, p0.Y) : (p0.X, ny);
        res.Insert(atStart ? 1 : res.Count - 1, elbow);
        return res;
    }

    /// <summary>The pre-existing bare-L fallback, for a wire whose end leg this rule cannot read.</summary>
    private static IReadOnlyList<(double X, double Y)> OrthogonalRouteBetween(
        IReadOnlyList<(double X, double Y)> pts, bool atStart, double nx, double ny)
        => atStart
            ? OrthogonalRoute(nx, ny, pts[^1].X, pts[^1].Y)
            : OrthogonalRoute(pts[0].X, pts[0].Y, nx, ny);

    /// <summary>
    /// Returns true if (px,py) lies on any point or segment of the given wire,
    /// within SnapTol. Used for drag-to-connect checking (§4.2).
    /// </summary>
    public static bool PointOnWire(EditableWire wire, double px, double py, double tol = SnapTol)
    {
        var pts = wire.Points;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            if (SchematicGeometry.PointOnSegment(px, py, pts[i].X, pts[i].Y,
                                                  pts[i + 1].X, pts[i + 1].Y, tol))
                return true;
        }
        // Also check isolated endpoints (single-point wire)
        if (pts.Count == 1)
            return SchematicGeometry.CoincidentPoints(px, py, pts[0].X, pts[0].Y, tol);
        return false;
    }

    /// <summary>
    /// Returns true if two wires share an endpoint within SnapTol, AND there is a
    /// junction dot at that point in the edit model.
    /// Used to determine if a crossing is a real connection (§5.1).
    /// </summary>
    public static bool WiresConnectedAtPoint(
        EditableWire wa, EditableWire wb,
        SchematicEditModel model,
        double tol = SnapTol)
    {
        if (wa.Points.Count == 0 || wb.Points.Count == 0) return false;

        foreach (var (ax, ay) in GetEndpoints(wa))
        {
            foreach (var (bx, by) in GetEndpoints(wb))
            {
                if (SchematicGeometry.CoincidentPoints(ax, ay, bx, by, tol))
                {
                    // An endpoint coincidence is always a connection (no dot needed).
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Returns all component ports in the model that are geometrically coincident
    /// with a wire endpoint, used for building connection visualization.
    /// </summary>
    public static IReadOnlyList<(string CompId, int PortIdx)> FindConnectedPorts(
        SchematicEditModel model, double tol = SnapTol)
    {
        var result = new List<(string, int)>();

        foreach (var comp in model.Components)
        {
            for (int pi = 0; pi < comp.PortCount; pi++)
            {
                var (wx, wy) = comp.GetPortWorldCoord(pi);
                foreach (var wire in model.Wires)
                {
                    if (PointOnWire(wire, wx, wy, tol))
                    {
                        result.Add((comp.Id, pi));
                        break;
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Checks whether placing or moving a component at (cx,cy) with the given
    /// symbol/rotation/mirror would connect any of its ports to any wire.
    /// Returns true if at least one port snaps to a wire (drag-to-connect).
    /// </summary>
    public static bool AnyPortConnectsToWire(
        SchematicEditModel model,
        SymbolKind symbol, double cx, double cy,
        SymbolRotation rot, bool mirrorX,
        double tol = SnapTol)
    {
        var portDefs = SymbolPortDefs.For(symbol);
        foreach (var (_, lx, ly) in portDefs)
        {
            var (wx, wy) = SchematicGeometry.LocalToWorld(lx, ly, cx, cy, rot, mirrorX);
            foreach (var wire in model.Wires)
            {
                if (PointOnWire(wire, wx, wy, tol)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Attempts to merge two wire point lists end-to-end.
    /// Returns the merged, normalized point list if exactly one endpoint pair is
    /// coincident within <paramref name="tol"/>; returns null if no junction or
    /// if more than one endpoint pair matches (loop / ambiguous junction).
    /// </summary>
    /// <summary>
    /// If wires <paramref name="aPoints"/> and <paramref name="bPoints"/> are both straight and
    /// COLLINEAR (same horizontal or vertical line) with overlapping or abutting spans, returns the
    /// single merged wire spanning their union; otherwise null. This simplifies redundant overlapping
    /// wire away (no junction is created where collinear wires overlap).
    /// </summary>
    public static IReadOnlyList<(double X, double Y)>? TryMergeCollinearOverlap(
        IReadOnlyList<(double X, double Y)> aPoints,
        IReadOnlyList<(double X, double Y)> bPoints,
        double tol)
    {
        var a = NormalizePoints(aPoints);
        var b = NormalizePoints(bPoints);
        if (a.Count != 2 || b.Count != 2) return null;   // only straight wires

        bool aH = Math.Abs(a[0].Y - a[1].Y) < tol, aV = Math.Abs(a[0].X - a[1].X) < tol;
        bool bH = Math.Abs(b[0].Y - b[1].Y) < tol, bV = Math.Abs(b[0].X - b[1].X) < tol;

        if (aH && bH && Math.Abs(a[0].Y - b[0].Y) < tol)
        {
            double aLo = Math.Min(a[0].X, a[1].X), aHi = Math.Max(a[0].X, a[1].X);
            double bLo = Math.Min(b[0].X, b[1].X), bHi = Math.Max(b[0].X, b[1].X);
            if (bLo > aHi + tol || aLo > bHi + tol) return null;   // disjoint
            double y = a[0].Y;
            return [(Math.Min(aLo, bLo), y), (Math.Max(aHi, bHi), y)];
        }
        if (aV && bV && Math.Abs(a[0].X - b[0].X) < tol)
        {
            double aLo = Math.Min(a[0].Y, a[1].Y), aHi = Math.Max(a[0].Y, a[1].Y);
            double bLo = Math.Min(b[0].Y, b[1].Y), bHi = Math.Max(b[0].Y, b[1].Y);
            if (bLo > aHi + tol || aLo > bHi + tol) return null;
            double x = a[0].X;
            return [(x, Math.Min(aLo, bLo)), (x, Math.Max(aHi, bHi))];
        }
        return null;
    }

    public static IReadOnlyList<(double X, double Y)>? TryBuildMergedPoints(
        IReadOnlyList<(double X, double Y)> aPoints,
        IReadOnlyList<(double X, double Y)> bPoints,
        double tol)
    {
        if (aPoints.Count < 2 || bPoints.Count < 2) return null;

        bool aEndBStart   = SchematicGeometry.CoincidentPoints(aPoints[^1].X, aPoints[^1].Y, bPoints[0].X,  bPoints[0].Y,  tol);
        bool aEndBEnd     = SchematicGeometry.CoincidentPoints(aPoints[^1].X, aPoints[^1].Y, bPoints[^1].X, bPoints[^1].Y, tol);
        bool aStartBEnd   = SchematicGeometry.CoincidentPoints(aPoints[0].X,  aPoints[0].Y,  bPoints[^1].X, bPoints[^1].Y, tol);
        bool aStartBStart = SchematicGeometry.CoincidentPoints(aPoints[0].X,  aPoints[0].Y,  bPoints[0].X,  bPoints[0].Y,  tol);

        int matches = (aEndBStart ? 1 : 0) + (aEndBEnd ? 1 : 0) + (aStartBEnd ? 1 : 0) + (aStartBStart ? 1 : 0);
        if (matches != 1) return null;

        var merged = new List<(double X, double Y)>(aPoints.Count + bPoints.Count - 1);

        if (aEndBStart)
        {
            merged.AddRange(aPoints);
            for (int i = 1; i < bPoints.Count; i++) merged.Add(bPoints[i]);
        }
        else if (aEndBEnd)
        {
            merged.AddRange(aPoints);
            for (int i = bPoints.Count - 2; i >= 0; i--) merged.Add(bPoints[i]);
        }
        else if (aStartBEnd)
        {
            merged.AddRange(bPoints);
            for (int i = 1; i < aPoints.Count; i++) merged.Add(aPoints[i]);
        }
        else // aStartBStart
        {
            for (int i = aPoints.Count - 1; i >= 0; i--) merged.Add(aPoints[i]);
            for (int i = 1; i < bPoints.Count; i++) merged.Add(bPoints[i]);
        }

        var normalized = NormalizePoints(merged);
        return normalized.Count >= 2 ? normalized : null;
    }

    private static IEnumerable<(double X, double Y)> GetEndpoints(EditableWire wire)
    {
        if (wire.Points.Count > 0)
        {
            yield return wire.Points[0];
            if (wire.Points.Count > 1)
                yield return wire.Points[wire.Points.Count - 1];
        }
    }
}
