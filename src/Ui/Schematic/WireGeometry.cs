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
