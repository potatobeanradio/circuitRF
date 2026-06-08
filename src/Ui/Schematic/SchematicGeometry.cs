namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Coordinate transform helpers shared between the edit model and the renderer.
/// All transforms are in world units (100 units = 1 grid square).
/// No Avalonia or Skia types — usable in headless tests.
/// </summary>
public static class SchematicGeometry
{
    /// <summary>
    /// Transform a component-local point to world coordinates,
    /// applying the horizontal mirror then the rotation.
    /// </summary>
    public static (double X, double Y) LocalToWorld(
        float lx, float ly,
        double compX, double compY,
        SymbolRotation rot, bool mirrorX)
    {
        float mlx = mirrorX ? -lx : lx;

        (double rx, double ry) = rot switch
        {
            SymbolRotation.R90  => (-(double)ly,  (double)mlx),
            SymbolRotation.R180 => (-(double)mlx, -(double)ly),
            SymbolRotation.R270 => ((double)ly,   -(double)mlx),
            _                   => ((double)mlx,  (double)ly),   // R0
        };

        return (compX + rx, compY + ry);
    }

    /// <summary>True if point (px,py) lies on the segment from (ax,ay) to (bx,by), within tolerance.</summary>
    public static bool PointOnSegment(
        double px, double py,
        double ax, double ay, double bx, double by,
        double tolerance = 8.0)
    {
        double dx = bx - ax, dy = by - ay;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-10) return DistanceSq(px, py, ax, ay) <= tolerance * tolerance;

        double t = ((px - ax) * dx + (py - ay) * dy) / lenSq;
        t = Math.Clamp(t, 0.0, 1.0);
        double cx = ax + t * dx, cy = ay + t * dy;
        return DistanceSq(px, py, cx, cy) <= tolerance * tolerance;
    }

    /// <summary>
    /// True if (px,py) lies on the <em>open interior</em> of segment (ax,ay)-(bx,by):
    /// on the segment within tolerance, but not coincident with either endpoint.
    /// This is the T-junction test (§5.1) — a wire endpoint landing strictly mid-span
    /// on another wire's segment, as opposed to meeting it at a shared vertex.
    /// </summary>
    public static bool PointOnSegmentInterior(
        double px, double py,
        double ax, double ay, double bx, double by,
        double tolerance = 8.0)
    {
        if (!PointOnSegment(px, py, ax, ay, bx, by, tolerance)) return false;
        // Exclude the segment's own endpoints — a vertex coincidence is not a T-junction.
        if (CoincidentPoints(px, py, ax, ay, tolerance)) return false;
        if (CoincidentPoints(px, py, bx, by, tolerance)) return false;
        return true;
    }

    /// <summary>Squared distance between two points.</summary>
    public static double DistanceSq(double ax, double ay, double bx, double by)
    {
        double dx = ax - bx, dy = ay - by;
        return dx * dx + dy * dy;
    }

    /// <summary>True if two points are within tolerance.</summary>
    public static bool CoincidentPoints(double ax, double ay, double bx, double by, double tolerance = 5.0)
        => DistanceSq(ax, ay, bx, by) <= tolerance * tolerance;

    /// <summary>
    /// True if segment (ax,ay)-(bx,by) overlaps the axis-aligned rect [rMinX,rMaxX]×[rMinY,rMaxY].
    /// Exact for orthogonal (horizontal/vertical) segments; conservative for diagonals.
    /// </summary>
    public static bool SegmentIntersectsRect(
        double ax, double ay, double bx, double by,
        double rMinX, double rMinY, double rMaxX, double rMaxY)
    {
        double sMinX = Math.Min(ax, bx), sMaxX = Math.Max(ax, bx);
        double sMinY = Math.Min(ay, by), sMaxY = Math.Max(ay, by);
        return sMaxX >= rMinX && sMinX <= rMaxX && sMaxY >= rMinY && sMinY <= rMaxY;
    }
}
