namespace CircuitRF.Ui.Layout.PCells;

/// <summary>Shared geometry construction helpers for the built-in microstrip PCell generators —
/// kept in one place so "how an arm rectangle is built" and "how junction arms are unioned into
/// one outline" (brief-L5a-pcell-contract-and-microstrip.md §3: "Junction geometry is a union, not
/// overlapping rectangles") are never reimplemented per component.</summary>
internal static class PCellGeometryHelpers
{
    /// <summary>How far each junction arm's drawn stub extends, as a multiple of that arm's own
    /// width — MTee/MCross/MBend declare no explicit arm-length parameter (only widths and, for
    /// MBend, an angle): the junction discontinuity itself is what these components model, and the
    /// physical line length on either side is a separate, user-placed MLIN. The stub exists purely
    /// so the junction reads as a real piece of artwork with pins genuinely at its own edge.</summary>
    internal const double StubLengthFactor = 2.5;

    /// <summary>
    /// Builds a straight rectangular arm of <paramref name="widthDbu"/> running from
    /// <paramref name="originX"/>,<paramref name="originY"/> for <paramref name="lengthDbu"/> in
    /// direction <paramref name="directionDeg"/> (0 = +X, 90 = +Y, CCW-positive — same sense as
    /// <c>LayoutArc</c>'s own convention). Axis-aligned cases return a <see cref="RectShape"/>
    /// (cheaper, and the common case); any other angle returns a 4-vertex <see cref="PolygonShape"/>.
    /// </summary>
    internal static LayoutShape BuildArmRect(long originX, long originY, double directionDeg,
        long lengthDbu, long widthDbu, LayerKey layer)
    {
        double rad = directionDeg * Math.PI / 180.0;
        double dx = Math.Cos(rad), dy = Math.Sin(rad);

        // Snap near-axis directions exactly (avoids float-noise producing a 4-vertex polygon for
        // what is visually and electrically an axis-aligned arm).
        bool alongX = Math.Abs(dy) < 1e-9;
        bool alongY = Math.Abs(dx) < 1e-9;
        long halfW = widthDbu / 2;

        if (alongX)
        {
            long x2 = originX + (long)Math.Round(dx * lengthDbu, MidpointRounding.AwayFromZero);
            long xMin = Math.Min(originX, x2), xMax = Math.Max(originX, x2);
            return new RectShape { Layer = layer, X1 = xMin, Y1 = originY - halfW, X2 = xMax, Y2 = originY + halfW };
        }
        if (alongY)
        {
            long y2 = originY + (long)Math.Round(dy * lengthDbu, MidpointRounding.AwayFromZero);
            long yMin = Math.Min(originY, y2), yMax = Math.Max(originY, y2);
            return new RectShape { Layer = layer, X1 = originX - halfW, Y1 = yMin, X2 = originX + halfW, Y2 = yMax };
        }

        double nx = -dy, ny = dx; // left-hand normal, unit length since (dx,dy) is unit length
        double endX = originX + dx * lengthDbu, endY = originY + dy * lengthDbu;

        long[] xy =
        [
            Round(originX + nx * halfW), Round(originY + ny * halfW),
            Round(endX + nx * halfW),    Round(endY + ny * halfW),
            Round(endX - nx * halfW),    Round(endY - ny * halfW),
            Round(originX - nx * halfW), Round(originY - ny * halfW),
        ];
        return new PolygonShape { Layer = layer, Xy = xy };
    }

    /// <summary>Unions a set of arm shapes into ONE outline (Clipper2, via <see cref="LayoutBooleans.Union"/>)
    /// — never overlapping rects, per the brief's own explicit instruction. All operands must
    /// already carry the resolved <paramref name="layer"/>. Returns the single merged outline
    /// (a <see cref="PolygonShape"/> or <see cref="CurveShape"/> — Clipper2's own output shape),
    /// or the sole operand unchanged if only one was given (union of one is a no-op, but still goes
    /// through the same call path so the layer/holes normalization stays uniform).</summary>
    internal static LayoutShape UnionArms(IReadOnlyList<LayoutShape> arms, LayerKey layer, Technology? tech)
    {
        if (arms.Count == 1) return arms[0];
        var result = LayoutBooleans.Union(arms, tech);
        // A well-formed set of overlapping/touching arms unions to exactly one region; if geometry
        // ever produced more than one (arms that don't actually touch — a caller bug, not a user
        // input this phase exposes), keep the largest by bounding-box area rather than silently
        // dropping data.
        if (result.Shapes.Count <= 1)
            return result.Shapes.Count == 1 ? result.Shapes[0] : arms[0];

        return result.Shapes
            .OrderByDescending(s =>
            {
                var bb = LayoutGeometry.BboxOf(s);
                return (bb.MaxX - bb.MinX) * (double)(bb.MaxY - bb.MinY);
            })
            .First();
    }

    private static long Round(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);
}
