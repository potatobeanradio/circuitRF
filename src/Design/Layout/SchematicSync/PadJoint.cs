using System.Runtime.CompilerServices;

namespace CircuitRF.Design.Layout.SchematicSync;

/// <summary>
/// brief-sdl-2 D2 (series decision D-joint): where on a footprint pad a line ends — the middle of the pad's OUTER
/// edge, so a line's L is the exposed line and the circuit simulation's length is the layout's.
///
/// <para>Found from the pad's own copper: the shape on the pin's layer that holds the pin point, measured along the
/// pin's effective outward direction (<see cref="PadFacing"/>, D1). The joint point is the pin point plus that
/// direction times the copper's reach along it — for a pad centred on its pin, half the pad's extent along the
/// direction. A pin with no copper under it joins at its own point, and <see cref="Point.OnCopper"/> says so, so the
/// caller can report it.</para>
///
/// <para>Computed once per resolved cell and cached on the cell's <see cref="LayoutView"/> REFERENCE, as
/// <see cref="CellPins"/> caches pins: an edit or a re-read produces a new reference, which is simply a miss.</para>
/// </summary>
public static class PadJoint
{
    /// <summary>One pin: <paramref name="Pin"/> as the cell stores it, and <paramref name="Joint"/> — a pin at the
    /// outer-edge midpoint, facing the effective direction, with the stored pin's name, width and layer — which is
    /// what a chain joins.</summary>
    public sealed record Point(LayoutPin Pin, LayoutPin Joint, bool OnCopper)
    {
        /// <summary>The outline of the copper the joint was measured on, in the cell's coordinates; null off copper.</summary>
        public long[]? Ring { get; init; }
    }

    /// <summary>Every pin of a cell, in the cell's own order. <paramref name="SinglePad"/>: the part's pads all sit
    /// on their centroid, so their stored directions were kept (D1).</summary>
    public sealed record Set(IReadOnlyList<Point> Points, bool SinglePad)
    {
        /// <summary>The joint of <paramref name="pin"/>, one of the cell's pins (by reference, else by name and point).</summary>
        public Point? For(LayoutPin pin) =>
            Points.FirstOrDefault(p => ReferenceEquals(p.Pin, pin))
            ?? Points.FirstOrDefault(p => p.Pin.Name == pin.Name && p.Pin.X == pin.X && p.Pin.Y == pin.Y);
    }

    private static readonly ConditionalWeakTable<LayoutView, Set> Cache = new();
    private static readonly ConditionalWeakTable<LayoutView, Set> StoredCache = new();

    /// <summary>The joint points of <paramref name="cell"/>'s pins. <paramref name="effectiveFacing"/>: apply D1's
    /// facing rule — for a footprint, whose file may store the sign backwards. Off, each pin keeps the direction its
    /// cell gives it: an imported part was written with D1's direction already, and a placed cell's pins face the way
    /// its author drew them, which "away from the centroid" need not be.</summary>
    public static Set Of(LayoutView cell, Technology? technology, bool effectiveFacing = true)
    {
        var cache = effectiveFacing ? Cache : StoredCache;
        if (cache.TryGetValue(cell, out var cached)) return cached;

        var pins = CellPins.Resolve(cell, technology);
        var facing = effectiveFacing ? PadFacing.Effective(pins)
                                     : new PadFacing.Result(pins.Select(p => LayoutAngle.Normalize(p.OutwardDeg)).ToList(), false);
        var points = new List<Point>(pins.Count);
        for (int i = 0; i < pins.Count; i++)
        {
            var pin = pins[i];
            double deg = facing.Deg[i];
            var (ux, uy) = LayoutAngle.CosSin(deg);
            var (reach, ring) = Reach(cell, pin, ux, uy);
            double r = reach ?? 0;
            points.Add(new Point(pin, new LayoutPin
            {
                Name = pin.Name,
                X = (long)Math.Round(pin.X + ux * r),
                Y = (long)Math.Round(pin.Y + uy * r),
                WidthDbu = pin.WidthDbu,
                OutwardDeg = deg,
                Layer = pin.Layer,
            }, reach is not null) { Ring = ring });
        }

        var set = new Set(points, pins.Count > 0 && facing.Degenerate);
        cache.AddOrUpdate(cell, set);
        return set;
    }

    /// <summary>How far the copper holding the pin point reaches from it along (ux, uy): the largest projection of any
    /// of its vertices, which is exact for the convex outlines pads are. Null when no copper on the pin's layer holds
    /// the point.</summary>
    private static (double? Reach, long[]? Ring) Reach(LayoutView cell, LayoutPin pin, double ux, double uy)
    {
        double? best = null;
        long[]? bestRing = null;
        foreach (var shape in cell.Shapes)
        {
            if (shape.Layer != pin.Layer) continue;
            if (shape is not (RectShape or PolygonShape or CircleShape or RoundedRectShape or CurveShape)) continue;
            var bb = LayoutGeometry.BboxOf(shape);
            if (pin.X < bb.MinX || pin.X > bb.MaxX || pin.Y < bb.MinY || pin.Y > bb.MaxY) continue;
            long[] ring = LayoutFlattener.Flatten(shape, 1)[0];
            if (!Inside(ring, pin.X, pin.Y)) continue;
            double reach = double.NegativeInfinity;
            for (int k = 0; k + 1 < ring.Length; k += 2)
                reach = Math.Max(reach, (ring[k] - pin.X) * ux + (ring[k + 1] - pin.Y) * uy);
            if (best is null || reach > best) (best, bestRing) = (reach, ring);
        }
        return (best, bestRing);
    }

    /// <summary>Inside or on the boundary of a closed ring of flat x,y pairs (even-odd, with the edge counted in).</summary>
    private static bool Inside(long[] ring, long x, long y)
    {
        int n = ring.Length / 2;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            long xi = ring[2 * i], yi = ring[2 * i + 1], xj = ring[2 * j], yj = ring[2 * j + 1];
            // On the edge: collinear and within the segment's box.
            long cross = (xj - xi) * (y - yi) - (yj - yi) * (x - xi);
            if (cross == 0 && x >= Math.Min(xi, xj) && x <= Math.Max(xi, xj) && y >= Math.Min(yi, yj) && y <= Math.Max(yi, yj))
                return true;
            if ((yi > y) != (yj > y) && x < (double)(xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }
}
