using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// MBend artwork (brief-L5a-pcell-contract-and-microstrip.md §3): two arms of width <c>W</c>
/// meeting at <c>Angle</c> degrees (measured CCW from arm 1's own +X direction, matching this
/// codebase's standing CCW-positive convention — see <c>LayoutArc</c>'s own doc comment), with the
/// outer corner cut when <c>Mitered</c>. R-pc-3: pin 1 at the origin, arm 1 (the input arm) along
/// +X. R-pc-18: mitered and unmitered are DISTINCT discontinuities — this file's own geometry
/// difference between the two (a real corner cut vs. none) is what backs that on the electrical
/// side (<c>MicrostripBendModel</c> in <c>src/Core/Devices/</c>) actually using different models.
///
/// <b>The outline is built DIRECTLY as one polygon, never as a union of two arm rectangles.</b>
/// Two rectangles unioned meet in an overlap whose boundary carries each rectangle's own leftover
/// corners, so an OBLIQUE bend rendered with a step on the outside and a spur on the inside —
/// visibly two overlapping stubs rather than one trace that turns. (The 45° case in the geometry
/// golden file used to come out as a self-overlapping TEN-vertex outline; it is a clean seven-vertex
/// chamfered bend now.) <see cref="BuildBendOutline"/> instead computes the two end caps, the real
/// OUTER corner (where the arms' outer edge lines cross) and the real INNER corner, and emits the
/// hexagon they define. At exactly 90° that hexagon is the same L the old extended-rectangle union
/// produced — the shipped right-angle geometry is unchanged, vertex for vertex — and at every other
/// angle it is the shape a bend is actually supposed to be.
///
/// <para>Building it directly also removes the boolean subtraction the miter used to need: the
/// chamfer is two vertices in place of one, so there is no <c>Difference</c> step that can silently
/// miss the geometry. That mattered — this file's own history records the miter cut being a complete
/// no-op twice, once because the arms only overlapped in a quarter of the corner square and once
/// because the corrective arm extension moved a cut point off the edge it was walking along.</para>
///
/// <b>The miter magnitude at 90° is the published fit and nothing else; at any other angle it is a
/// stated extrapolation.</b> Douville &amp; James 1978 fitted a RIGHT-ANGLE bend and says nothing
/// about oblique ones, and no equivalently-validated fit for arbitrary angles was found — so
/// <c>Optimal</c> at 90° is still exactly <see cref="MicrostripDiscontinuities.MiterCutLength"/> and
/// <c>Fifty</c> is still a 0.5·W per-edge leg. Away from 90°, <see cref="MiterLegDbu"/> holds the
/// FRACTION of the corner removed constant instead (the corner being the outer-to-inner diagonal),
/// which reduces to the published leg at 90° by construction, and every oblique bend reports through
/// <see cref="PCellResult.Diagnostics"/> that its chamfer is an engineering extrapolation rather
/// than the paper's number.
///
/// <para><b>This deliberately reverses R-L5h-7's original "report and stay unmitered."</b> That rule
/// was right that the formula must not be silently extrapolated and wrong about the remedy: leaving
/// an oblique corner square is what made the bend read as two merged stubs in the first place. The
/// no-silent-extrapolation half is kept exactly — the diagnostic fires on every oblique bend and
/// names both the angle and what the number is.</para>
/// </summary>
public static class MBendPCell
{
    public const string GeneratorId = "MBEND";

    public static PCellResult Generate(
        IReadOnlyDictionary<string, PCellValue> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
        double wMeters = parameters.Real("W", 0.0);
        double angleDeg = parameters.Real("Angle", 90.0);
        // "Miter" (0=None, 1=Fifty, 2=Optimal — MicrostripBendMiter's own order); "Mitered" (0/1)
        // still read as a fallback for a hand-authored parameter set predating this brief.
        // Read through Real, not Int, so an Int-kinded 2 and the Real 2.0 every pre-contract-v2
        // parameter set carries decide the same mode — this is an ENUMERATION, and which kind the
        // caller happened to use is not something the geometry may depend on.
        var miter = parameters.ContainsKey("Miter")
            ? (MicrostripBendMiter)(int)Math.Round(parameters.Real("Miter", 0.0))
            : (parameters.Real("Mitered", 0.0) != 0.0 ? MicrostripBendMiter.Optimal : MicrostripBendMiter.None);

        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        long w = PCellUnits.MetresToDbu(wMeters, dbuPerMicron);
        long halfW = w / 2;

        var signalLayer = SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);

        double angleRad = angleDeg * Math.PI / 180.0;
        double d2X = Math.Cos(angleRad), d2Y = Math.Sin(angleRad);

        List<string>? diagnostics = null;
        bool straightThrough = Math.Abs(Math.Sin(angleRad)) <= 1e-9;

        // Each arm ends at the MODEL's reference plane: the line through the inner corner, square to
        // the arm, (W/2)·tan(θ/2) from the centre-line corner — at 90° the edges of the W×W corner
        // square. The L-C-L model has no length of its own, so an arm drawn past that plane is line
        // the schematic never simulated: the arms used to be 2.5·W each, about 4·W of unsimulated
        // line per bend once MLINs were abutted at the pins. A chamfer deep enough to run past the
        // plane moves the pin out to where the strip is full width again, as MMITER's does, so an
        // abutting MLIN never fills the cut back in. A straight-through "bend" has no corner and no
        // plane, and keeps the legacy stubs.
        double leg = straightThrough || miter == MicrostripBendMiter.None
            ? 0.0
            : MiterLegDbu(w, angleRad, wMeters, miter, technology, layerSelection, dbuPerMicron, ref diagnostics);
        double plane = w / 2.0 * Math.Tan(Math.Abs(angleRad) / 2.0);
        long stubLen = straightThrough
            ? (long)Math.Round(PCellGeometryHelpers.StubLengthFactor * w, MidpointRounding.AwayFromZero)
            : (long)Math.Round(Math.Max(plane, leg - plane), MidpointRounding.AwayFromZero);

        long cornerX = stubLen, cornerY = 0;
        double pin2X = cornerX + stubLen * d2X;
        double pin2Y = cornerY + stubLen * d2Y;
        long pin2XDbu = (long)Math.Round(pin2X, MidpointRounding.AwayFromZero);
        long pin2YDbu = (long)Math.Round(pin2Y, MidpointRounding.AwayFromZero);

        LayoutShape merged;
        if (straightThrough)
        {
            // Both arms collinear: there is no corner to build an outline around, and the two outer
            // edge lines are parallel so the intersection below is undefined. A union of the two arm
            // rectangles is the honest degenerate answer (a straight through-line at Angle=0; two
            // superimposed arms at Angle=180, which is the already-diagnosed folded case).
            var s1 = PCellGeometryHelpers.BuildArmRect(0, 0, 0.0, stubLen, w, signalLayer);
            var s2 = PCellGeometryHelpers.BuildArmRect(cornerX, cornerY, angleDeg, stubLen, w, signalLayer);
            merged = PCellGeometryHelpers.UnionArms([s1, s2], signalLayer, technology);
        }
        else
        {
            merged = BuildBendOutline(cornerX, cornerY, stubLen, halfW, angleRad, leg, signalLayer, ref diagnostics);
        }

        var pins = new[]
        {
            new PCellPin("1", 0, 0, signalLayer, w, 180.0),
            new PCellPin("2", pin2XDbu, pin2YDbu, signalLayer, w, angleDeg),
        };

        // pcell-parameter-handles.md — three grips:
        //
        //   W      — the top edge of the input arm, the ordinary "widen this trace" gesture.
        //   Angle  — an ANGULAR grip at EACH pin, and they are MIRROR IMAGES: grab either end and swing
        //            its arm about the corner; the other arm keeps its place AND its heading, so the
        //            trace attached there stays attached. Both drive the same `Angle`.
        //
        //   Miter  — still no grip: an enumeration (None/Fifty/Optimal) wearing a Real's clothes,
        //            with no continuum for a drag to move along. It belongs in the parameter list.
        //
        //   * PIN 2 swings about the corner, which does not move with `Angle`, and arm 1 stays exactly
        //     where it is with no help from the host.
        //   * PIN 1 sits at the cell origin heading +X for every `Angle` (R4), so holding arm 2 still
        //     instead is a ROTATION of the instance, which no generator can express. The grip holds the
        //     corner in place (KeepAnchorFixed) and pin 2's outward direction with it (AnchorDirDeg);
        //     the host turns the instance by the angle's change and arm 1 swings the other way. Its
        //     reference axis turns with pin 2's direction, so the bearing it reads is -Angle.
        //
        // <b>Degenerate at Angle = 180°</b>, where the arms fold back onto each other — the
        // already-diagnosed straight-through case, not a new failure mode.
        var handles = new[]
        {
            new PCellHandle("W", stubLen / 2, 0, stubLen / 2, w / 2, AxisDeg: 90,
                Quantity: PCellHandleQuantity.Length),

            // Pin 2, about the pivot. Reference +X because that is how `Angle` is measured
            // (BuildArmRect takes it from +X), so this grip's angular projection IS the parameter.
            new PCellHandle("Angle", cornerX, cornerY, pin2XDbu, pin2YDbu,
                AxisDeg: 0, Kind: PCellHandleKind.Angular, KeepAnchorFixed: true,
                Quantity: PCellHandleQuantity.Angle),

            // Pin 1, about the corner, holding arm 2's place and heading.
            new PCellHandle("Angle", cornerX, cornerY, 0, 0,
                AxisDeg: 180.0 + angleDeg, Kind: PCellHandleKind.Angular, KeepAnchorFixed: true,
                Quantity: PCellHandleQuantity.Angle, AnchorDirDeg: angleDeg),
        };

        return new PCellResult([merged], pins, diagnostics, Handles: handles);
    }

    /// <summary>R-L5h-7's own predicate, kept: an EXACT right-angle bend is the one case where the
    /// miter magnitude comes straight from a published, fitted formula rather than from the
    /// generalisation below. Independent of turn sign (covers both Angle=90 and Angle=-90/270).</summary>
    private static bool IsRightAngleBend(double angleRad) => Math.Abs(Math.Cos(angleRad)) <= 1e-9;

    /// <summary>
    /// The bend outline, built DIRECTLY as one polygon for any angle — never as a union of two arm
    /// rectangles.
    ///
    /// <para><b>This is the fix for "a non-90° bend looks like two stubs merged together."</b> Two
    /// rectangles unioned meet in a lens-shaped overlap whose boundary carries each rectangle's own
    /// leftover corners, so an oblique bend renders with a step on the outside and a spur on the
    /// inside — visibly two overlapping stubs rather than one trace that turns. The right shape is a
    /// hexagon: the two end caps, the OUTER corner (where the two outer edge lines meet), and the
    /// INNER corner (where the two inner edge lines meet). At exactly 90° that hexagon is the same L
    /// the extended-rectangle union used to produce, so the shipped right-angle geometry is
    /// unchanged; at every other angle it is the shape a bend is actually supposed to be.</para>
    ///
    /// <para>Building the outline directly also removes the boolean difference the miter cut used to
    /// need: the chamfer is now just two vertices in place of one, so there is no subtract step that
    /// can silently miss the geometry (the exact failure this file's own history records twice).</para>
    /// </summary>
    private static LayoutShape BuildBendOutline(
        long cornerX, long cornerY, long stubLen, long halfW, double angleRad, double leg,
        LayerKey signalLayer, ref List<string>? diagnostics)
    {
        double d1X = 1.0, d1Y = 0.0;
        var (d2X, d2Y, n1X, n1Y, n2X, n2Y, outerX, outerY, innerX, innerY) = Corners(cornerX, cornerY, halfW, angleRad);

        double pin1X = 0, pin1Y = 0;
        // Rounded as the pin itself is, so the pin sits on the midpoint of its own end cap at any angle (brief-sdl-1:
        // round the cap from the exact point and the pin of an oblique bend lands a fraction of a DBU outside its copper).
        double pin2X = Math.Round(cornerX + stubLen * d2X, MidpointRounding.AwayFromZero);
        double pin2Y = Math.Round(cornerY + stubLen * d2Y, MidpointRounding.AwayFromZero);

        // End caps, perpendicular to each arm's own direction at its own pin.
        double a1X = pin1X + n1X * halfW, a1Y = pin1Y + n1Y * halfW;   // pin1, outer side
        double b1X = pin1X - n1X * halfW, b1Y = pin1Y - n1Y * halfW;   // pin1, inner side
        // Pin 2's cap is offset by one whole-DBU vector either way from the rounded pin, so the pin is its exact midpoint.
        double cap2X = Math.Round(n2X * halfW, MidpointRounding.AwayFromZero), cap2Y = Math.Round(n2Y * halfW, MidpointRounding.AwayFromZero);
        double a2X = pin2X + cap2X, a2Y = pin2Y + cap2Y;   // pin2, outer side
        double b2X = pin2X - cap2X, b2Y = pin2Y - cap2Y;   // pin2, inner side

        // Never cut past the end of either outer edge — that would fold the outline through itself.
        // The arms are sized so the chamfer fits, so this only guards rounding.
        double avail = Math.Min(Math.Sqrt((outerX - a1X) * (outerX - a1X) + (outerY - a1Y) * (outerY - a1Y)),
                                Math.Sqrt((outerX - a2X) * (outerX - a2X) + (outerY - a2Y) * (outerY - a2Y)));
        if (leg > avail + 1.0)
        {
            leg = avail;
            (diagnostics ??= []).Add(
                "MBend: the miter chamfer was limited by the arm length available — the corner cut " +
                "is shorter than the miter rule asks for.");
        }
        leg = Math.Min(leg, avail);

        // The arms end at the reference planes, so the inner corner IS both inner end-cap corners at
        // every angle, and a chamfer that ends exactly at a pin meets that pin's outer corner. The
        // END-CAP corners are the exact ones — square to their arm, through the pin — so a computed
        // corner or chamfer point within one DBU of one of them is dropped, never the cap corner: the
        // arm length is a whole number of DBU, and keeping the computed point instead would tilt the
        // cap by a nanometre and hand the mesher an oblique edge to staircase.
        static bool Near(double ax, double ay, double bx, double by)
            => Math.Abs(Math.Round(ax) - Math.Round(bx)) <= 1 && Math.Abs(Math.Round(ay) - Math.Round(by)) <= 1;

        var xy = new List<long>(16);
        void Add(double x, double y)
        {
            xy.Add((long)Math.Round(x, MidpointRounding.AwayFromZero));
            xy.Add((long)Math.Round(y, MidpointRounding.AwayFromZero));
        }

        Add(a1X, a1Y);
        if (leg > 0)
        {
            double c1X = outerX - d1X * leg, c1Y = outerY - d1Y * leg;
            double c2X = outerX + d2X * leg, c2Y = outerY + d2Y * leg;
            if (!Near(c1X, c1Y, a1X, a1Y)) Add(c1X, c1Y);
            if (!Near(c2X, c2Y, a2X, a2Y)) Add(c2X, c2Y);
        }
        else
        {
            Add(outerX, outerY);
        }
        Add(a2X, a2Y);
        // Pin 2's corners are rotated and so rounded; pin 1's lie on the axes and are exact. Where an
        // arm ends at its plane all three inner points are one. Pin 2's is kept unless it IS pin 1's: dropping it
        // for a neighbour a DBU away moved pin 2's cap off pin 2, and LVS read the pin as on no copper (brief-sdl-1).
        if (Math.Round(b2X) != b1X || Math.Round(b2Y) != b1Y) Add(b2X, b2Y);
        if (!Near(innerX, innerY, b2X, b2Y) && !Near(innerX, innerY, b1X, b1Y)) Add(innerX, innerY);
        Add(b1X, b1Y);

        return new PolygonShape { Layer = signalLayer, Xy = [.. xy] };
    }

    /// <summary>Arm 2's direction, both arms' outward normals (outer side = right of travel for a CCW
    /// turn, left for a CW one), and the outer and inner corners — where each pair of parallel-offset
    /// edge lines crosses — for a bend whose centre-line corner is at (<paramref name="cornerX"/>,
    /// <paramref name="cornerY"/>) and whose arm 1 runs along +X.</summary>
    private static (double D2X, double D2Y, double N1X, double N1Y, double N2X, double N2Y,
                    double OuterX, double OuterY, double InnerX, double InnerY)
        Corners(double cornerX, double cornerY, double halfW, double angleRad)
    {
        double d1X = 1.0, d1Y = 0.0;
        double d2X = Math.Cos(angleRad), d2Y = Math.Sin(angleRad);
        double turnSign = Math.Sign(d1X * d2Y - d1Y * d2X);
        double n1X = turnSign > 0 ? d1Y : -d1Y, n1Y = turnSign > 0 ? -d1X : d1X;
        double n2X = turnSign > 0 ? d2Y : -d2Y, n2Y = turnSign > 0 ? -d2X : d2X;
        var (outerX, outerY) = LineIntersect(
            cornerX + n1X * halfW, cornerY + n1Y * halfW, d1X, d1Y,
            cornerX + n2X * halfW, cornerY + n2Y * halfW, d2X, d2Y);
        var (innerX, innerY) = LineIntersect(
            cornerX - n1X * halfW, cornerY - n1Y * halfW, d1X, d1Y,
            cornerX - n2X * halfW, cornerY - n2Y * halfW, d2X, d2Y);
        return (d2X, d2Y, n1X, n1Y, n2X, n2Y, outerX, outerY, innerX, innerY);
    }

    /// <summary>
    /// The chamfer leg, measured along each outer edge back from the sharp outer corner.
    ///
    /// <para><b>At exactly 90° this is the published number and nothing else</b>: <c>Optimal</c> is
    /// Douville &amp; James 1978's W/h-dependent optimum (<c>MicrostripDiscontinuities.MiterCutLength</c>,
    /// <c>src/Core/Devices/Microstrip/</c>, untouched), <c>Fifty</c> is the fixed per-edge leg of
    /// 0.5·W. Neither changes.</para>
    ///
    /// <para><b>At any other angle there is no published optimum to use, and this says so rather than
    /// pretending otherwise.</b> Douville &amp; James fitted a RIGHT-ANGLE bend; the paper states
    /// nothing about oblique ones, and no equivalently-validated fit for arbitrary angles was found.
    /// What ships instead is the standard engineering generalisation — hold the FRACTION of the corner
    /// removed constant. The corner is the segment from the outer corner O to the inner corner I; the
    /// 90° leg corresponds to some fraction f of |O−I| removed along the bisector, so the oblique leg
    /// is whatever reproduces that same f on this bend's own geometry. It reduces to the published leg
    /// exactly at 90° (by construction — f is derived FROM it), it degrades sensibly toward a shallow
    /// bend (less corner to remove, so a longer, gentler chamfer), and it is reported as an
    /// extrapolation through <see cref="PCellResult.Diagnostics"/> so nobody reads an oblique bend's
    /// chamfer as carrying the paper's accuracy.</para>
    ///
    /// <para>The leg is the corner's alone: the arms are sized afterwards so that it fits (see
    /// <see cref="Generate"/>), which is what lets a shallow bend take its full chamfer instead of a
    /// clamped one.</para>
    /// </summary>
    private static double MiterLegDbu(
        long wDbu, double angleRad, double wMeters, MicrostripBendMiter miter,
        Technology? technology, PCellLayerSelection layerSelection, int dbuPerMicron,
        ref List<string>? diagnostics)
    {
        double leg = 0;
        if (wDbu <= 0) return 0;

        // The 90°-equivalent leg, in DBU — the published quantity, whatever the actual angle is.
        double leg90Meters;
        if (miter == MicrostripBendMiter.Fifty)
        {
            leg90Meters = 0.5 * wMeters;
        }
        else
        {
            // Optimal: h = the resolved substrate height; falls back to the W/h→∞ asymptote of the
            // Douville-James formula (cut → 0.52·W) when no technology/substrate resolves, per §2 of
            // brief-L5a ("the geometry is still generatable" even with nothing resolved).
            var (substrate, _, _) = SubstrateResolver.ResolveElectrical(technology, layerSelection);
            leg90Meters = substrate is not null
                ? MicrostripDiscontinuities.MiterCutLength(wMeters, substrate.HeightMeters)
                : MicrostripDiscontinuities.MiterCutLengthAsymptotic(wMeters);
        }
        double leg90 = PCellUnits.MetresToDbu(leg90Meters, dbuPerMicron);
        if (leg90 <= 0) return 0;

        if (IsRightAngleBend(angleRad))
        {
            leg = leg90;
        }
        else
        {
            // f: the fraction of the 90° corner diagonal (W·√2) the published leg removes along the
            // bisector. A 90° corner's bisector depth for leg m is m/√2, so f = (m/√2)/(W·√2) = m/(2W).
            double f = leg90 / (2.0 * wDbu);

            // The leg is a property of the corner alone, so it is measured at a corner on the origin
            // before the arms are sized to fit it.
            var (_, _, _, _, _, _, outerX, outerY, innerX, innerY) = Corners(0, 0, wDbu / 2, angleRad);
            double d1X = 1.0, d1Y = 0.0;
            double diagX = innerX - outerX, diagY = innerY - outerY;
            double diagLen = Math.Sqrt(diagX * diagX + diagY * diagY);
            if (diagLen <= 0) return 0;
            double bx = diagX / diagLen, by = diagY / diagLen;

            // Both outer edges leave O at the same angle to the bisector, so either dot product is
            // the same cos(half-angle); take arm 1's.
            double cosHalf = -d1X * bx + -d1Y * by;
            if (cosHalf <= 1e-9) return 0;

            leg = f * diagLen / cosHalf;

            (diagnostics ??= []).Add(
                $"MBend: no published optimum-miter fit exists for a {angleRad * 180.0 / Math.PI:0.###}° bend " +
                "(Douville & James 1978 is a right-angle formula) — the corner is chamfered by the same " +
                "FRACTION of the corner that formula removes at 90°, which is an engineering " +
                "extrapolation, not a validated fit.");
        }

        return Math.Max(leg, 0);
    }

    /// <summary>Intersection of two lines given as point + direction. The caller guarantees they are
    /// not parallel (the straight-through case is handled before this is ever reached).</summary>
    private static (double X, double Y) LineIntersect(
        double p1X, double p1Y, double d1X, double d1Y,
        double p2X, double p2Y, double d2X, double d2Y)
    {
        double denom = d1X * d2Y - d1Y * d2X;
        double t = ((p2X - p1X) * d2Y - (p2Y - p1Y) * d2X) / denom;
        return (p1X + t * d1X, p1Y + t * d1Y);
    }
}
