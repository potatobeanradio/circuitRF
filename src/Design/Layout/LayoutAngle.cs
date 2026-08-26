// The ONE place a placement angle is normalized, tested for cardinality, and turned into (cos, sin)
// — brief-L3d-arbitrary-angle-instances.md R-L3d-1/R-L3d-2. Framework-free.
//
// R-L3d-1's convention, stated once here so nothing re-derives it: DEGREES, counter-clockwise, in
// the layout's own Y-up DBU frame — the same convention and the same sense LabelShape.PortDirection
// already documents (R0 = +x-hat, R90 = +y-hat). Radians exist only inside CosSin.
//
// WHY CosSin EXISTS AT ALL, and why it is not just Math.Cos/Math.Sin: Math.Cos(Math.PI / 2) is
// 6.123e-17, not 0. Feeding that through LayoutInstanceTransform would perturb EVERY existing
// cardinal placement in EVERY existing design by a fraction of a DBU — invisible on screen, fatal to
// R-L3d-2's requirement that the four cardinal angles reproduce the pre-L3d rotation tables exactly.
// The cardinal cases are therefore returned as exact literals, and the transcendental path runs only
// for an angle that genuinely needs it.

namespace CircuitRF.Design.Layout;

public static class LayoutAngle
{
    /// <summary>Folds any angle into <c>[0, 360)</c>. A non-finite input (a corrupt <c>.clay</c>, a
    /// division that produced NaN) becomes 0 rather than propagating NaN into every coordinate the
    /// transform touches — a placement at an unreadable angle is a placement at 0, not a layout full
    /// of NaN.</summary>
    public static double Normalize(double deg)
    {
        if (!double.IsFinite(deg)) return 0.0;
        deg %= 360.0;
        if (deg < 0) deg += 360.0;
        return deg == 0 ? 0.0 : deg;   // also collapses -0.0 to +0.0
    }

    /// <summary>The angle a <see cref="LayoutRotation"/> names, in this file's convention.</summary>
    public static double OfCardinal(LayoutRotation rot) => rot switch
    {
        LayoutRotation.R90  => 90.0,
        LayoutRotation.R180 => 180.0,
        LayoutRotation.R270 => 270.0,
        _                   => 0.0,
    };

    /// <summary>
    /// True when <paramref name="deg"/> is EXACTLY one of the four cardinals (compare normalized).
    /// Deliberately exact rather than tolerant: "cardinal" decides whether a placement serializes as
    /// the legacy <c>Rot</c> enum alone (R-L3d-4), so a tolerance would silently round a deliberate
    /// 89.999 deg placement to 90 deg on save. An angle a user or a composition actually produces —
    /// 90, 30+60, four 90 deg advances from 30 — lands exactly.
    /// </summary>
    public static bool TryCardinal(double deg, out LayoutRotation rot)
    {
        switch (Normalize(deg))
        {
            case 0.0:   rot = LayoutRotation.R0;   return true;
            case 90.0:  rot = LayoutRotation.R90;  return true;
            case 180.0: rot = LayoutRotation.R180; return true;
            case 270.0: rot = LayoutRotation.R270; return true;
            default:    rot = LayoutRotation.R0;   return false;
        }
    }

    /// <summary>Nearest cardinal, ties going to the larger angle. Used for the legacy <c>Rot</c>
    /// companion field (R-L3d-4) and at the two boundaries that genuinely cannot carry a real angle:
    /// a flattened label's <see cref="LabelShape.Rotation"/> and a carried port direction
    /// (R-L3d-6, R-L3d-12). Never used to decide what to STORE for an instance.</summary>
    public static LayoutRotation NearestCardinal(double deg)
    {
        int q = (int)Math.Round(Normalize(deg) / 90.0, MidpointRounding.AwayFromZero) % 4;
        return q switch
        {
            1 => LayoutRotation.R90,
            2 => LayoutRotation.R180,
            3 => LayoutRotation.R270,
            _ => LayoutRotation.R0,
        };
    }

    /// <summary>How far <paramref name="deg"/> is from its nearest cardinal, in degrees (0..45].
    /// Reported when a boundary snaps, never used to decide whether to snap.</summary>
    public static double DistanceToNearestCardinal(double deg)
    {
        double n = Normalize(deg);
        double nearest = OfCardinal(NearestCardinal(n));
        double diff = Math.Abs(n - nearest);
        return Math.Min(diff, 360.0 - diff);
    }

    /// <summary>(cos, sin) of <paramref name="deg"/>, EXACT at the four cardinals — see this file's
    /// header for why that exactness is load-bearing rather than cosmetic.</summary>
    public static (double Cos, double Sin) CosSin(double deg) => Normalize(deg) switch
    {
        0.0   => (1.0, 0.0),
        90.0  => (0.0, 1.0),
        180.0 => (-1.0, 0.0),
        270.0 => (0.0, -1.0),
        var d => (Math.Cos(d * Math.PI / 180.0), Math.Sin(d * Math.PI / 180.0)),
    };
}
