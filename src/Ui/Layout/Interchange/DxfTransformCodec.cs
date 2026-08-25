// INSERT xscale/yscale/rotation <-> our MirrorX/rotation/Mag (docs/sonnet-briefs/brief-L4b-dxf-interchange.md
// R-L4b-2). "Mirror is a negative scale, not a flag" — the DXF analogue of L4a's STRANS trap, but with
// the OPPOSITE resolution: GDSII's STRANS reflects about the X-AXIS (negates Y) before rotating, a
// DIFFERENT axis than this codebase's own MirrorX convention (negate local X before rotating), which is
// why GdsiiTransformCodec needs the reflect-then-rotate-180 trick. DXF's INSERT transform order is
// SCALE then ROTATE then TRANSLATE (per the DXF spec) — a negative xscale negates local X BEFORE
// rotation, which is EXACTLY LayoutInstanceTransform.MirrorMagScale's own convention. The trap here is
// assuming DXF needs the same +180 correction GDSII did; it does not — the mapping is direct.
//
// L3d (R-L3d-8) DELETED this file's snapping, for the same reason GdsiiTransformCodec lost its own:
// INSERT's group 50 has always carried an arbitrary rotation, and it was our model that could not.
// The yscale mismatch report is UNRELATED and stays — a non-uniform INSERT is a genuine
// inexpressibility in our (Mag, MirrorX) model, and no amount of rotation freedom changes that.

namespace CircuitRF.Ui.Layout.Interchange;

public static class DxfTransformCodec
{
    /// <summary>Our (MirrorX, rotation in degrees, Mag) -> DXF INSERT's (xscale, yscale, rotation in
    /// degrees).</summary>
    public static (double XScale, double YScale, double RotationDegrees) ToDxf(bool mirrorX, double rotDegrees, double mag)
    {
        double xscale = mirrorX ? -mag : mag;
        return (xscale, mag, LayoutAngle.Normalize(rotDegrees));
    }

    /// <summary>
    /// DXF INSERT's (xscale, yscale, rotation in degrees) -> our (MirrorX, rotation in degrees, Mag).
    /// The rotation is exact — see this file's header. <paramref name="yScaleMismatch"/> is true when
    /// <c>|yscale| != |xscale|</c> — a genuinely non-uniform third-party INSERT our own (Mag, MirrorX)
    /// model cannot represent exactly; <c>Mag</c> is taken from <c>|xscale|</c> in that case and the
    /// mismatch is reported by the caller.
    /// </summary>
    public static (bool MirrorX, double RotDegrees, double Mag) FromDxf(
        double xscale, double yscale, double rotationDegrees, out bool yScaleMismatch)
    {
        bool mirrorX = xscale < 0;
        double mag = Math.Abs(xscale);
        yScaleMismatch = Math.Abs(Math.Abs(yscale) - mag) > 1e-9;

        return (mirrorX, LayoutAngle.Normalize(rotationDegrees), mag);
    }
}
