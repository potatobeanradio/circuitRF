// STRANS/ANGLE ↔ our own MirrorX/Rot conversion — the single place §2.1 item 4's "reflect before
// rotate" order-of-operations bug would hide.
//
// GDSII's STRANS bit 15 reflects about the X-AXIS (negates Y) before rotation. Our own
// LayoutInstanceTransform's MirrorX negates local X (not Y) before rotation — a DIFFERENT axis, by
// construction of this codebase's own convention (see LayoutInstanceTransform.MirrorMagScale). Since
// negate-Y ≡ rotate-180° ∘ negate-X (Rot(180°)·diag(-1,1) = diag(1,-1)), a GDSII (reflect, angle) pair
// maps onto our (MirrorX, Rot) as (MirrorX = reflect, Rot = angle + 180° when reflect, else angle
// unchanged) — both directions verified algebraically against LayoutInstanceTransform.TransformPoint's
// own rotation table before writing a line of reader/writer code, and pinned by
// LayoutGdsiiTransformTests's all-8-combination pixel comparison (gate 5).

namespace CircuitRF.Ui.Layout.Interchange;

public static class GdsiiTransformCodec
{
    /// <summary>Our (MirrorX, Rot) → GDSII's (reflect, angle in degrees).</summary>
    public static (bool Reflect, double AngleDegrees) ToGdsii(bool mirrorX, LayoutRotation rot)
    {
        double baseDeg = DegreesOf(rot);
        return mirrorX ? (true, Normalize(baseDeg - 180.0)) : (false, baseDeg);
    }

    /// <summary>
    /// GDSII's (reflect, angle in degrees) → our (MirrorX, Rot). ANGLE is snapped to the nearest
    /// multiple of 90° — our model only stores a discrete 4-way rotation — and
    /// <paramref name="snappedDeltaDegrees"/> reports how much was discarded (0 for any angle our own
    /// writer ever emits, since it only ever writes exact multiples of 90°; non-zero only for a
    /// third-party file with a genuinely arbitrary ANGLE).
    /// </summary>
    public static (bool MirrorX, LayoutRotation Rot) FromGdsii(
        bool reflect, double angleDegrees, out double snappedDeltaDegrees)
    {
        double effective = reflect ? Normalize(angleDegrees + 180.0) : Normalize(angleDegrees);
        var rot = SnapToRotation(effective, out snappedDeltaDegrees);
        return (reflect, rot);
    }

    private static double DegreesOf(LayoutRotation r) => r switch
    {
        LayoutRotation.R90 => 90.0,
        LayoutRotation.R180 => 180.0,
        LayoutRotation.R270 => 270.0,
        _ => 0.0,
    };

    private static double Normalize(double deg)
    {
        deg %= 360.0;
        if (deg < 0) deg += 360.0;
        return deg;
    }

    private static LayoutRotation SnapToRotation(double deg, out double deltaDegrees)
    {
        int nearestQuadrant = (int)Math.Round(deg / 90.0) % 4;
        if (nearestQuadrant < 0) nearestQuadrant += 4;
        double nearestDeg = nearestQuadrant * 90.0;
        double diff = Math.Abs(deg - nearestDeg);
        deltaDegrees = Math.Min(diff, 360.0 - diff);
        return nearestQuadrant switch
        {
            1 => LayoutRotation.R90,
            2 => LayoutRotation.R180,
            3 => LayoutRotation.R270,
            _ => LayoutRotation.R0,
        };
    }
}
