using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Ui.Layout.PCells;

/// <summary>
/// MBend artwork (brief-L5a-pcell-contract-and-microstrip.md §3): two arms of width <c>W</c>
/// meeting at <c>Angle</c> degrees (measured CCW from arm 1's own +X direction, matching this
/// codebase's standing CCW-positive convention — see <c>LayoutArc</c>'s own doc comment), with the
/// outer corner cut when <c>Mitered</c>. R-pc-3: pin 1 at the origin, arm 1 (the input arm) along
/// +X. R-pc-18: mitered and unmitered are DISTINCT discontinuities — this file's own geometry
/// difference between the two (a real corner cut vs. none) is what backs that on the electrical
/// side (<c>MicrostripBendModel</c> in <c>src/Core/Devices/</c>) actually using different models.
///
/// <b>brief-L5-followups-3.md §3 (R-L5h-5/6/7) — the root cause, found by direct numeric
/// reconstruction, was neither hypothesis the brief itself led with.</b> The Douville-James FORMULA
/// (<see cref="MicrostripDiscontinuities.MiterCutLength"/>, <c>src/Core</c>, untouched by this fix)
/// already interprets <c>M</c> correctly as the fraction REMOVED (≈69% per leg at W/h=1, matching
/// R-L5h-5's own worked expectation) and already returns a PER-EDGE LEG length (not a diagonal —
/// R-L5h-5's "missing √2" hypothesis does not apply, because nothing here ever divides by √2: the
/// leg IS the quantity <see cref="BuildMiterCutTriangle"/> needs). The actual defect
/// (brief-L5-followups-2.md's own investigation already found this, but declined to fix it, citing
/// "needs visual verification") was that the two arms — each built independently by
/// <see cref="PCellGeometryHelpers.BuildArmRect"/>, stopping/starting exactly AT the nominal pivot
/// point — only overlap in a halfW×halfW QUARTER of the true W×W corner square, producing a
/// "stair-stepped" union boundary with no single sharp outer corner at all.
/// <see cref="BuildMiterCutTriangle"/>'s own corner computation (the intersection of the two arms'
/// OUTER EDGE LINES, extended) therefore lands on a point that is not actually ON the union's real
/// boundary, so the miter-cut polygon never overlaps anything and <c>LayoutBooleans.Difference</c>
/// correctly finds nothing to subtract — a complete geometric no-op, for EVERY miter magnitude, not
/// a magnitude error. <b>Fixed here</b> by extending each arm HALF a width past/before the pivot
/// along its own centerline (<c>arm1LenDbu</c>/<c>arm2Origin*</c> below) so the two arms' widths
/// form the full W×W overlap square the corner-intersection math already assumed — verified
/// numerically (not by eye) against the same worked example: at W/h=1 the three miter modes now
/// produce three genuinely distinct outlines, and the removed length matches the calculator oracle
/// (see <c>MBendMiterGeometryTests.cs</c> for the full comparison table).
///
/// <b>R-L5h-7's decision: the miter cut is restricted to an exact 90° bend, reported (never
/// silently extrapolated) otherwise.</b> Douville &amp; James's own fitted curve is a right-angle
/// formula (its citation states nothing about oblique bends), and the corner-square construction
/// above is itself a right-angle-specific geometric argument — extending it to an arbitrary
/// <c>Angle</c> would mean silently guessing a cut shape/length neither the formula nor the artwork
/// convention actually covers, which is exactly the "wrong numbers look plausible" trap R-L5h-7
/// warns against. <see cref="IsRightAngleBend"/> gates both <c>Fifty</c> and <c>Optimal</c> (the
/// SAME triangular-cut construction, so the SAME restriction applies to both); a non-90° bend with a
/// non-None Miter selected keeps its un-mitered (square-corner) geometry and reports why via
/// <see cref="PCellResult.Diagnostics"/> — the generator-level analogue of R13a's "disabled with a
/// stated reason," since a pure PCell generator has no interactive control to grey out directly.
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
        long stubLen = (long)Math.Round(PCellGeometryHelpers.StubLengthFactor * w, MidpointRounding.AwayFromZero);
        long halfW = w / 2;

        var signalLayer = SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);

        long cornerX = stubLen, cornerY = 0;
        double angleRad = angleDeg * Math.PI / 180.0;
        double d2X = Math.Cos(angleRad), d2Y = Math.Sin(angleRad);

        // Each arm extends HALF a width past/before the pivot corner along its OWN centerline, so
        // the two arms' widths form a full W×W overlap square there instead of a halfW×halfW
        // quarter — see this file's own class doc comment for why that quarter-overlap is what made
        // the miter cut a no-op. Arm1's far end and arm2's origin both move; PIN positions do not
        // (pin1 is always the origin; pin2 is still exactly stubLen from the corner along d2).
        long arm1LenDbu = stubLen + halfW;
        long arm2OriginX = (long)Math.Round(cornerX - d2X * halfW, MidpointRounding.AwayFromZero);
        long arm2OriginY = (long)Math.Round(cornerY - d2Y * halfW, MidpointRounding.AwayFromZero);
        long arm2LenDbu = stubLen + halfW;

        var arm1 = PCellGeometryHelpers.BuildArmRect(0, 0, 0.0, arm1LenDbu, w, signalLayer);
        var arm2 = PCellGeometryHelpers.BuildArmRect(arm2OriginX, arm2OriginY, angleDeg, arm2LenDbu, w, signalLayer);

        double pin2X = cornerX + stubLen * Math.Cos(angleRad);
        double pin2Y = cornerY + stubLen * Math.Sin(angleRad);
        long pin2XDbu = (long)Math.Round(pin2X, MidpointRounding.AwayFromZero);
        long pin2YDbu = (long)Math.Round(pin2Y, MidpointRounding.AwayFromZero);

        LayoutShape merged = PCellGeometryHelpers.UnionArms([arm1, arm2], signalLayer, technology);

        List<string>? diagnostics = null;
        bool straightThrough = Math.Abs(Math.Sin(angleRad)) <= 1e-9;
        if (miter != MicrostripBendMiter.None && !straightThrough)
        {
            if (IsRightAngleBend(angleRad))
            {
                var miterCut = BuildMiterCutTriangle(cornerX, cornerY, angleDeg, w, wMeters, miter, technology, layerSelection, signalLayer, dbuPerMicron);
                if (miterCut is not null)
                {
                    var diff = LayoutBooleans.Difference([merged, miterCut], technology);
                    if (diff.Shapes.Count > 0) merged = diff.Shapes[0];
                }
            }
            else
            {
                // R-L5h-7: Douville & James (Optimal) and the triangular corner-square construction
                // (Fifty, same geometry) are both right-angle-specific — never silently extrapolated
                // to an oblique bend. Reported once; the bend still generates, un-mitered.
                diagnostics = [$"MBend: Miter cut is only defined for a 90° bend (Angle={angleDeg:0.###}°, not a right angle) — generated without a corner cut."];
            }
        }

        var pins = new[]
        {
            new PCellPin("1", 0, 0, signalLayer, w, 180.0),
            new PCellPin("2", pin2XDbu, pin2YDbu, signalLayer, w, angleDeg),
        };

        // pcell-parameter-handles.md — three grips, and the placement of the two angle grips is the
        // whole UX question:
        //
        //   W      — the top edge of the input arm, the ordinary "widen this trace" gesture.
        //   Angle  — an ANGULAR grip at EACH pin. Grab either end of the bend and swing it; the end
        //            you did NOT grab holds its position. Both drive the same `Angle`.
        //
        //   Miter  — still no grip: an enumeration (None/Fifty/Optimal) wearing a Real's clothes,
        //            with no continuum for a drag to move along. It belongs in the parameter list.
        //
        // <b>The two angle grips are anchored on OPPOSITE points, and that asymmetry is forced by R4,
        // not chosen.</b> R4 fixes pin 1 at the cell origin with arm 1 along +X, so:
        //
        //   * PIN 2 swings about the PIVOT CORNER. That is its real geometric path — the arm rotates
        //     about the bend — so the anchor is both the correct measurement frame and an accurate
        //     hint arc. The anchor does not move with `Angle` at all, so pinning it is a no-op.
        //
        //   * PIN 1 CANNOT swing about the pivot, and this is where an earlier reading of the
        //     contract concluded a pin-1 grip was impossible. Pin 1 sits at (0,0) for every value of
        //     `Angle`; its bearing from the pivot is always 180°, so a grip anchored there is
        //     INVARIANT in the parameter — the host measures no movement and refuses the drag. That
        //     refusal is correct, and the conclusion drawn from it ("so pin 1 can't drive the angle")
        //     was not: anchoring on PIN 2 instead makes the same parameter perfectly measurable,
        //     because pin 2 is what moves. The relationship is exactly linear — the inscribed-angle
        //     construction gives bearing = Angle/2 off the reference — so the solve converges on its
        //     first iteration.
        //
        // Both angle grips therefore hold their anchor (R-pch-4b): the end you are not dragging keeps
        // its world position while the instance translates under it. Only pin 1's anchor actually
        // moves; pin 2's is set for uniformity, so a reader does not have to work out which of two
        // sibling grips is the special one.
        //
        // <b>Degenerate at Angle = 180°</b>, where the arms fold back and the two pins coincide — the
        // bearing is undefined there and the grip is unmeasurable. That is the already-diagnosed
        // straight-through case, not a new failure mode.
        var handles = new[]
        {
            new PCellHandle("W", arm1LenDbu / 2, 0, arm1LenDbu / 2, w / 2, AxisDeg: 90),

            // Pin 2, about the pivot. Reference +X because that is how `Angle` is measured
            // (BuildArmRect takes it from +X), so this grip's angular projection IS the parameter.
            new PCellHandle("Angle", cornerX, cornerY, pin2XDbu, pin2YDbu,
                AxisDeg: 0, Kind: PCellHandleKind.Angular, KeepAnchorFixed: true),

            // Pin 1, about pin 2. Reference 180° so the projection reads as a positive half-angle
            // over the whole usable range rather than wrapping through the normalisation cut.
            new PCellHandle("Angle", pin2XDbu, pin2YDbu, 0, 0,
                AxisDeg: 180, Kind: PCellHandleKind.Angular, KeepAnchorFixed: true),
        };

        return new PCellResult([merged], pins, diagnostics, Handles: handles);
    }

    /// <summary>R-L5h-7: true only for an EXACT right-angle bend — arm2's direction purely
    /// perpendicular to arm1's fixed +X direction (R-pc-3), independent of turn sign (covers both
    /// Angle=90 and Angle=-90/270).</summary>
    private static bool IsRightAngleBend(double angleRad) => Math.Abs(Math.Cos(angleRad)) <= 1e-9;

    /// <summary>
    /// The miter cut length, applied symmetrically along each arm's outer edge from the sharp
    /// outer corner: <c>Optimal</c> uses Douville &amp; James 1978's W/h-dependent optimum
    /// (<c>MicrostripDiscontinuities.MiterCutLength</c>, <c>src/Core/Devices/Microstrip/</c> —
    /// shared with the electrical model's own chamfer-geometry reasoning per R-pc-12/R7, though the
    /// L-C-L electrical model itself no longer consumes this length directly, see
    /// <c>MicrostripBendModel</c>'s own doc comment); <c>Fifty</c> uses a FIXED 50% chamfer
    /// (per-edge leg = 0.5·W, i.e. M/100=0.5 in the same diagonal-cut convention) — the artwork
    /// analogue of the Fifty electrical coefficients (brief-mtaper-mklopf.md §1A.3). Only ever
    /// called for a confirmed right-angle bend (<see cref="IsRightAngleBend"/>, checked by the
    /// caller) — see this file's own class doc comment for why that restriction exists.
    /// </summary>
    private static LayoutShape? BuildMiterCutTriangle(
        long cornerX, long cornerY, double angleDeg, long wDbu, double wMeters, MicrostripBendMiter miter,
        Technology? technology, PCellLayerSelection layerSelection, LayerKey signalLayer, int dbuPerMicron)
    {
        double angleRad = angleDeg * Math.PI / 180.0;
        double d1X = 1.0, d1Y = 0.0;
        double d2X = Math.Cos(angleRad), d2Y = Math.Sin(angleRad);

        double turnSign = Math.Sign(d1X * d2Y - d1Y * d2X);
        if (turnSign == 0) return null;

        // Outer side = right of travel for a CCW (left) turn, left of travel for a CW (right) turn.
        double n1X = turnSign > 0 ? d1Y : -d1Y, n1Y = turnSign > 0 ? -d1X : d1X;
        double n2X = turnSign > 0 ? d2Y : -d2Y, n2Y = turnSign > 0 ? -d2X : d2X;

        double halfW = wDbu / 2.0;
        double a1X = cornerX + n1X * halfW, a1Y = cornerY + n1Y * halfW; // point on arm1's outer edge line
        double a2X = cornerX + n2X * halfW, a2Y = cornerY + n2Y * halfW; // point on arm2's outer edge line

        // arm1's outer edge is always horizontal (d1 = (1,0) by R-pc-3) — solve the intersection
        // with arm2's outer edge line (a2 + s*d2) directly rather than a general 2-line solver.
        // With the caller's arms now forming a full W×W overlap square (this file's class doc
        // comment), this intersection point is the union's own real sharp outer corner.
        if (Math.Abs(d2Y) < 1e-12) return null; // degenerate (arm2 also horizontal)
        double s = (a1Y - a2Y) / d2Y;
        double outerX = a2X + s * d2X, outerY = a1Y; // = a2Y + s*d2Y by construction

        double mMeters;
        if (miter == MicrostripBendMiter.Fifty)
        {
            mMeters = 0.5 * wMeters; // fixed 50% chamfer, per-edge leg = 0.5*W
        }
        else
        {
            // Optimal: h = the resolved substrate height; falls back to the W/h→∞ asymptote of the
            // Douville-James formula (cut → 0.52·W) when no technology/substrate resolves, per §2
            // of the brief ("the geometry is still generatable" even with nothing resolved).
            var (substrate, _, _) = SubstrateResolver.ResolveElectrical(technology, layerSelection);
            mMeters = substrate is not null
                ? MicrostripDiscontinuities.MiterCutLength(wMeters, substrate.HeightMeters)
                : MicrostripDiscontinuities.MiterCutLengthAsymptotic(wMeters);
        }
        long mDbu = PCellUnits.MetresToDbu(mMeters, dbuPerMicron);
        if (mDbu <= 0) return null;

        // The two cut points walk back from the sharp outer corner ALONG EACH ARM'S OWN OUTER EDGE,
        // toward THAT ARM'S OWN PIN — never symmetrically in "-d" for both. Pin1 sits at the origin,
        // i.e. in the -d1 direction from the corner (cornerX = stubLen·d1), so cut1 = outer - d1·m is
        // correct as one leg. Pin2 sits FURTHER along +d2 from the corner (pin2 = corner + stubLen·d2,
        // unchanged by the arm-extension fix above), so walking toward pin2 from the corner is the
        // +d2 direction — cut2 = outer + d2·m, not outer - d2·m. Using -d2 here (as an earlier version
        // of this method did) walks OFF arm2's own outer edge entirely, into the region behind arm2's
        // now-extended origin (the exact bug the class doc comment's arm-extension fix exposed: with
        // arm2's origin shifted a half-width BEHIND the nominal corner, "-d2 from the corner" is no
        // longer inside arm2 at all, and the cut silently missed the real geometry a second time).
        long cut1X = (long)Math.Round(outerX - d1X * mDbu, MidpointRounding.AwayFromZero);
        long cut1Y = (long)Math.Round(outerY - d1Y * mDbu, MidpointRounding.AwayFromZero);
        long cut2X = (long)Math.Round(outerX + d2X * mDbu, MidpointRounding.AwayFromZero);
        long cut2Y = (long)Math.Round(outerY + d2Y * mDbu, MidpointRounding.AwayFromZero);
        long outerXDbu = (long)Math.Round(outerX, MidpointRounding.AwayFromZero);
        long outerYDbu = (long)Math.Round(outerY, MidpointRounding.AwayFromZero);

        return new PolygonShape
        {
            Layer = signalLayer,
            Xy = [outerXDbu, outerYDbu, cut1X, cut1Y, cut2X, cut2Y],
        };
    }
}
