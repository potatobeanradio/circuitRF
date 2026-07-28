// INSERT xscale/yscale/rotation <-> our MirrorX/Rot/Mag (docs/sonnet-briefs/brief-L4b-dxf-interchange.md
// R-L4b-2). "Mirror is a negative scale, not a flag" — the DXF analogue of L4a's STRANS trap, but with
// the OPPOSITE resolution: GDSII's STRANS reflects about the X-AXIS (negates Y) before rotating, a
// DIFFERENT axis than this codebase's own MirrorX convention (negate local X before rotating), which is
// why GdsiiTransformCodec needs the reflect-then-rotate-180 trick. DXF's INSERT transform order is
// SCALE then ROTATE then TRANSLATE (per the DXF spec) — a negative xscale negates local X BEFORE
// rotation, which is EXACTLY LayoutInstanceTransform.MirrorMagScale's own convention. The trap here is
// assuming DXF needs the same +180 correction GDSII did; it does not — the mapping is direct.

namespace CircuitRF.Ui.Layout.Interchange;

public static class DxfTransformCodec
{
    /// <summary>Our (MirrorX, Rot, Mag) -> DXF INSERT's (xscale, yscale, rotation in degrees).</summary>
    public static (double XScale, double YScale, double RotationDegrees) ToDxf(bool mirrorX, LayoutRotation rot, double mag)
    {
        double xscale = mirrorX ? -mag : mag;
        return (xscale, mag, DegreesOf(rot));
    }

    /// <summary>
    /// DXF INSERT's (xscale, yscale, rotation in degrees) -> our (MirrorX, Rot, Mag). ANGLE is snapped
    /// to the nearest multiple of 90 degrees (our model only stores a discrete 4-way rotation) —
    /// <paramref name="snappedDeltaDegrees"/> reports how much was discarded (0 for any angle our own
    /// writer ever emits). <paramref name="yScaleMismatch"/> is true when <c>|yscale| != |xscale|</c> —
    /// a genuinely non-uniform third-party INSERT our own (Mag, MirrorX) model cannot represent exactly;
    /// <c>Mag</c> is taken from <c>|xscale|</c> in that case and the mismatch is reported by the caller.
    /// </summary>
    public static (bool MirrorX, LayoutRotation Rot, double Mag) FromDxf(
        double xscale, double yscale, double rotationDegrees,
        out double snappedDeltaDegrees, out bool yScaleMismatch)
    {
        bool mirrorX = xscale < 0;
        double mag = Math.Abs(xscale);
        yScaleMismatch = Math.Abs(Math.Abs(yscale) - mag) > 1e-9;

        var rot = SnapToRotation(Normalize(rotationDegrees), out snappedDeltaDegrees);
        return (mirrorX, rot, mag);
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
