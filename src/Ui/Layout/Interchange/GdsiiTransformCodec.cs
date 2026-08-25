// STRANS/ANGLE ↔ our own MirrorX/rotation conversion — the single place §2.1 item 4's "reflect before
// rotate" order-of-operations bug would hide.
//
// GDSII's STRANS bit 15 reflects about the X-AXIS (negates Y) before rotation. Our own
// LayoutInstanceTransform's MirrorX negates local X (not Y) before rotation — a DIFFERENT axis, by
// construction of this codebase's own convention (see LayoutInstanceTransform.MirrorMagScale). Since
// negate-Y ≡ rotate-180° ∘ negate-X (Rot(180°)·diag(-1,1) = diag(1,-1)), a GDSII (reflect, angle) pair
// maps onto our (MirrorX, angle) as (MirrorX = reflect, angle = angle + 180° when reflect, else angle
// unchanged) — both directions verified algebraically against LayoutInstanceTransform.TransformPoint's
// own rotation math before writing a line of reader/writer code, and pinned by
// LayoutGdsiiTransformTests's all-8-combination pixel comparison (gate 5).
//
// L3d (R-L3d-8) DELETED this file's snapping. GDSII's ANGLE has always carried an arbitrary angle;
// before L3d it was OUR model that could not, so every non-cardinal third-party angle was rounded to
// a multiple of 90° and the discarded remainder reported as a loss. An instance now carries a real
// angle, so there is nothing to discard and nothing to report — the mapping is exact in both
// directions. LabelShape.Rotation is still genuinely four-way, so the TEXT path in GdsiiReader does
// its own snapping and keeps its own diagnostic; that limitation is real and stays visible.

namespace CircuitRF.Ui.Layout.Interchange;

public static class GdsiiTransformCodec
{
    /// <summary>Our (MirrorX, rotation in degrees) → GDSII's (reflect, angle in degrees).</summary>
    public static (bool Reflect, double AngleDegrees) ToGdsii(bool mirrorX, double rotDegrees)
    {
        double baseDeg = LayoutAngle.Normalize(rotDegrees);
        return mirrorX ? (true, LayoutAngle.Normalize(baseDeg - 180.0)) : (false, baseDeg);
    }

    /// <summary>GDSII's (reflect, angle in degrees) → our (MirrorX, rotation in degrees). Exact — see
    /// this file's header for why there is no longer a snap or a loss report.</summary>
    public static (bool MirrorX, double RotDegrees) FromGdsii(bool reflect, double angleDegrees)
    {
        double effective = reflect
            ? LayoutAngle.Normalize(angleDegrees + 180.0)
            : LayoutAngle.Normalize(angleDegrees);
        return (reflect, effective);
    }
}
