// Framework-free snap + angle-mode math for interactive drawing (docs/design/layout-view.md §1.5 R5,
// §3.3 R10 — L1b brief). No SKPath / Avalonia types.

namespace CircuitRF.Render;

public static class LayoutSnapping
{
    /// <summary>Snaps a raw world coordinate to <paramref name="snapDbu"/> — <c>Math.Round(dbu / snap) * snap</c>.
    /// <paramref name="snapDbu"/> &lt;= 0 or <paramref name="suspend"/> means "no snapping" (raw coordinate,
    /// rounded to the nearest integer DBU since the model is integer-only — R1).</summary>
    public static long SnapValue(double raw, long snapDbu, bool suspend)
    {
        if (suspend || snapDbu <= 0) return (long)Math.Round(raw);
        return (long)(Math.Round(raw / snapDbu) * snapDbu);
    }

    public static (long X, long Y) SnapPoint(double x, double y, long snapDbu, bool suspend)
        => (SnapValue(x, snapDbu, suspend), SnapValue(y, snapDbu, suspend));

    /// <summary>
    /// Constrains a candidate point relative to the previous vertex per <paramref name="angleMode"/>,
    /// then snaps. <b>Snaps a single scalar distance along the constrained direction, never X and Y
    /// independently</b> — the brief describes "constrain, then snap, then re-check, preferring the
    /// axis-aligned result when the two fight"; snapping one distance along the already-chosen
    /// direction achieves the same "never emit an off-mode segment" guarantee by construction, with
    /// no fallback/re-check branch needed, because both axis components are always exact multiples
    /// of the same snapped magnitude (equal for a diagonal, one of them exactly zero for an axis
    /// direction) rather than independently-rounded coordinates that could disagree.
    /// <paramref name="angleMode"/> never applies to <c>Circle</c>/<c>RoundedRect</c> — callers only
    /// use this for <c>Polygon</c>/<c>Path</c> vertices.
    /// </summary>
    public static (long X, long Y) ConstrainAndSnap(
        long prevX, long prevY, double candX, double candY,
        AngleMode angleMode, long snapDbu, bool suspendSnap)
    {
        if (angleMode == AngleMode.AnyAngle)
            return SnapPoint(candX, candY, snapDbu, suspendSnap);

        double dx = candX - prevX, dy = candY - prevY;
        if (dx == 0 && dy == 0) return (prevX, prevY);

        if (angleMode == AngleMode.Manhattan)
        {
            // Keep the previous vertex exactly on the other axis (it is already on-grid, since
            // every placed vertex passes through this same snap) — never re-derive it from a
            // separately-rounded coordinate.
            if (Math.Abs(dx) >= Math.Abs(dy))
                return (SnapValue(candX, snapDbu, suspendSnap), prevY);
            return (prevX, SnapValue(candY, snapDbu, suspendSnap));
        }

        // Deg45: bucket into one of 8 directions (45-degree multiples).
        double angle = Math.Atan2(dy, dx);
        const double step = Math.PI / 4.0;
        double bucket = Math.Round(angle / step) * step;
        double ux = Math.Cos(bucket), uy = Math.Sin(bucket);
        double dist = Math.Sqrt(dx * dx + dy * dy);

        // Math.Round(ux)/Math.Round(uy) reliably yields exactly -1/0/1 for every one of the 8
        // buckets (|cos|/|sin| is either 0, 1, or 1/sqrt(2) ~ 0.707, which rounds to 1) — this is
        // the "one sign per axis" the diagonal and axis-aligned cases share.
        int signX = (int)Math.Round(ux);
        int signY = (int)Math.Round(uy);

        // The in-direction component length: for an axis bucket this is the full distance; for a
        // diagonal bucket it is dist/sqrt(2) on EACH axis — |ux| (=|uy| for a diagonal) already
        // captures exactly that scale factor.
        double magnitude = dist * Math.Max(Math.Abs(ux), Math.Abs(uy));

        if (suspendSnap || snapDbu <= 0)
        {
            long fx = prevX + signX * (long)Math.Round(magnitude);
            long fy = prevY + signY * (long)Math.Round(magnitude);
            return (fx, fy);
        }

        long snappedMag = (long)(Math.Round(magnitude / snapDbu) * snapDbu);
        return (prevX + signX * snappedMag, prevY + signY * snappedMag);
    }

    /// <summary>
    /// A <see cref="AngleMode.Deg45"/> constraint that a geometry-snap target composes with rather than
    /// overrides: the CURSOR's travel from the previous point picks one of the 8 directions, and the
    /// target only says how far along it the point goes — its projection onto that direction. On an
    /// axis direction that is the target's own coordinate on the free axis, exactly (the rule a
    /// Shift-constrained move drag already follows, R-dup-4); on a diagonal it is the nearest point of
    /// the diagonal, rounded to DBU with both components kept equal.
    /// </summary>
    public static (long X, long Y) ConstrainToTarget(
        long prevX, long prevY, double candX, double candY, long targetX, long targetY)
    {
        double dx = candX - prevX, dy = candY - prevY;
        if (dx == 0 && dy == 0) return (prevX, prevY);

        const double step = Math.PI / 4.0;
        double bucket = Math.Round(Math.Atan2(dy, dx) / step) * step;
        int signX = (int)Math.Round(Math.Cos(bucket));
        int signY = (int)Math.Round(Math.Sin(bucket));

        if (signY == 0) return (targetX, prevY);
        if (signX == 0) return (prevX, targetY);

        long m = (long)Math.Round(((targetX - prevX) * (double)signX + (targetY - prevY) * (double)signY) / 2.0);
        return (prevX + signX * m, prevY + signY * m);
    }
}
