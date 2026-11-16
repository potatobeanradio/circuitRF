using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>How a Schiffman phase shifter's reference line is laid: straight along +x, or folded back beside the C-section
/// on a 180° MCURVE (<see cref="Curve"/>) or two 90° corners (<see cref="Miter"/>, <see cref="Square"/>, as
/// <see cref="PlanCorner"/>'s), or <see cref="Meander"/>ed by <see cref="PlanRoute"/> so its legs are the C-section's
/// length and the two paths sit as one block, all four pins on its left edge.</summary>
public enum SchiffmanReference { Straight, Curve, Miter, Square, Meander }

/// <summary>What a Schiffman phase shifter comes out to on one substrate.</summary>
/// <param name="Rho">Ze/Zo of the C-section, Schiffman's ρ.</param>
/// <param name="ReferenceTurns">The reference line's electrical length at F over the C-section's, k: (ΔΦ + π)/(π/2).</param>
/// <param name="RippleDeg">The differential phase's equal ripple about <see cref="PhaseDeg"/>, ± degrees, over the band.</param>
/// <param name="ReferenceMeters">The reference line's length at F, its corners counted at their own electrical length.</param>
public sealed record SchiffmanDesign(
    double Z0, double FreqHz, double PhaseDeg, double Band, double Rho, double ReferenceTurns, double RippleDeg,
    double Ze, double Zo, double WMeters, double SMeters, double LengthMeters, KirschningJansenCoupled.Modes Modes,
    double W0Meters, double ReferenceMeters, SchiffmanReference Reference, PassivePlan Plan)
{
    /// <summary>Meander: the folded reference's legs centre to centre; 0 for the other forms.</summary>
    public double MeanderPitchMeters { get; init; }
}

/// <summary>
/// The Schiffman differential phase shifter (B. M. Schiffman, "A New Class of Broad-Band Microwave 90-Degree Phase
/// Shifters," IRE Trans. MTT, vol. 6, no. 2, pp. 232-237, April 1958): a C-section — an edge-coupled pair a quarter
/// wave long at F with its far ends joined — beside a uniform reference line, the two phases differing by ΔΦ nearly
/// flat over a band. Pure: Z0, F, the phase and the band plus a substrate in, <see cref="SchiffmanDesign"/> out.
///
/// <para><b>The C-section.</b> With Ze·Zo = Z0² the section is matched at every frequency (an all-pass), and its phase
/// is cos φ = (ρ − tan²θ)/(ρ + tan²θ), i.e. tan(φ/2) = tan θ/√ρ, ρ = Ze/Zo and θ its electrical length: π at the quarter
/// wave. The reference line is kθ, so the difference Δφ = kθ − φ is ΔΦ at F for k = 2(ΔΦ + π)/π (three quarter waves for
/// 90°). Δφ − ΔΦ is odd about F, so over the band f2/f1 = <c>Band</c> centred on F (θ1 = π/(1 + Band)) it ripples
/// equally when its value at the band edge is minus its peak inside: ρ is found by bisection on that, as Schiffman's
/// curves choose it, and the ripple is the readout's. <see cref="CoupledLineSynthesis"/> finds the pair; its length is
/// where its own model, strap included, is 180° at F (about λg/4 at the pair's mean εeff), and the reference line is k
/// quarter waves at its own εeff.</para>
///
/// <para><b>Pins.</b> 1 and 2 are the C-section's near ends (strip A's and strip B's), 3 and 4 the reference line's ends.
/// A short strap across the far ends, one strip width wide, joins them; it is an MLIN from end to end in the model.</para>
///
/// <para><b>Limit.</b> The design is TEM. In microstrip the odd mode is faster than the even mode, so the C-section is
/// not a quarter wave for both and its match and phase depart from Schiffman's curves; the strap adds a little series
/// inductance. Both move the differential phase off ΔΦ and widen the ripple, which the circuit model shows.</para>
/// </summary>
public static class SchiffmanPlan
{
    /// <summary>The part's pins, in terminal order: the C-section's two ends, then the reference line's.</summary>
    public static readonly IReadOnlyList<string> Pins = ["1", "2", "3", "4"];

    /// <summary>The band the part opens with: 1.4:1, ρ = 2.35, a 7.9 dB pair, the widest a 90° part builds on the fallback
    /// 1.6 mm FR-4 (a 161 µm gap against its 160 µm limit). 90° needs ρ ≥ 9/4, 8.3 dB, however narrow the band.</summary>
    public const double DefaultBand = 1.4;

    /// <summary>The C-section's phase at electrical length <paramref name="theta"/> (radians), 0 to 2π over 0 to π.</summary>
    public static double CSectionPhase(double rho, double theta)
    {
        double p = 2.0 * Math.Atan(Math.Tan(theta) / Math.Sqrt(rho));
        return theta > Math.PI / 2.0 ? p + 2.0 * Math.PI : p;
    }

    /// <summary>ρ, k and the ripple (radians) of the equal-ripple design for ΔΦ = <paramref name="phaseRad"/> over
    /// <paramref name="band"/>.</summary>
    public static (double Rho, double K, double Ripple) EqualRipple(double phaseRad, double band)
    {
        double k = 2.0 * (phaseRad + Math.PI) / Math.PI, t1 = Math.PI / (1.0 + band);
        double Dev(double rho, double t) => k * t - CSectionPhase(rho, t) - phaseRad;
        double Peak(double rho)
        {
            double best = double.NegativeInfinity;
            for (int i = 0; i <= 2000; i++) best = Math.Max(best, Dev(rho, t1 + (Math.PI / 2.0 - t1) * i / 2000.0));
            return best;
        }
        // At ρ = (k/2)² Δφ is flat at F and the peak is 0; a larger ρ raises both the peak and the band edge's value.
        double lo = k * k / 4.0, hi = lo;
        while (Dev(hi, t1) + Peak(hi) < 0.0 && hi < 1e4) hi *= 1.5;
        for (int i = 0; i < 100; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (Dev(mid, t1) + Peak(mid) < 0.0) lo = mid; else hi = mid;
        }
        double r = 0.5 * (lo + hi);
        return (r, k, Peak(r));
    }

    public static SchiffmanDesign Build(double z0, double fHz, double phaseDeg, double band, MicrostripSubstrate s,
        SchiffmanReference reference = SchiffmanReference.Straight)
    {
        foreach (var (name, value) in new[] { ("Z0", z0), ("F", fHz) })
            if (!(value > 0))
                throw new ParameterRefusalException(Diagnostic.Create("device.schiffman.spec-not-positive",
                    DiagnosticSeverity.Error, "Schiffman phase shifter: {name} = {value} must be positive.",
                    ("name", name), ("value", value)));
        if (!(phaseDeg > 0.0 && phaseDeg < 180.0))
            throw new ParameterRefusalException(Diagnostic.Create("device.schiffman.phase-invalid",
                DiagnosticSeverity.Error, "Schiffman phase shifter: Phase = {p}°; it is between 0 and 180°.", ("p", phaseDeg)));
        if (!(band > 1.0 && band < 5.0))
            throw new ParameterRefusalException(Diagnostic.Create("device.schiffman.band-invalid",
                DiagnosticSeverity.Error, "Schiffman phase shifter: Band = {b}; it is f2/f1, above 1 and below 5.", ("b", band)));

        const string part = "Schiffman phase shifter";
        var (rho, k, ripple) = EqualRipple(phaseDeg * Math.PI / 180.0, band);
        double ze = z0 * Math.Sqrt(rho), zo = z0 / Math.Sqrt(rho);
        var g = CoupledLineSynthesis.Synthesize(ze, zo, fHz, s, part);
        double w = PassivePlan.OnGrid(g.WMeters), gap = PassivePlan.OnGrid(g.SMeters);
        double p = w + gap, l = PassivePlan.OnGrid(HalfTurnLength(w, gap, g.QuarterWaveMeters, z0, fHz, s));
        double w0 = PassivePlan.OnGrid(BranchLinePlan.Width(z0, fHz, s, part, "device.schiffman.width-unreachable"));
        var line = BranchLinePlan.Line(w0, s).LineParameters(fHz);
        double q0 = PlanarLineAngle.LengthMeters(line.Eeff, Math.PI / 2.0, fHz);
        double lref = k * q0;
        // The reference line runs below the C-section, three of its widths clear of strip B.
        double y3 = PassivePlan.OnGrid(-p - w / 2.0 - 3.0 * w0 - w0 / 2.0);

        // Nodes: 1-4 the pins; 5/6 the C-section's far ends (A, B); 7-10 the reference line's joints.
        var sections = new List<PlanSection>
        {
            new PlanCoupledLine([1, 5, 2, 6], 0.0, 0.0, 0.0, w, gap, l),
            new PlanLine([5, 6], l + w / 2.0, w / 2.0, 270.0, w, p + w),
        };
        if (reference == SchiffmanReference.Meander)
        {
            var route = Meandered(phaseDeg * Math.PI / 180.0 + Math.PI, l, w0, y3, fHz, s);
            sections.AddRange(route.Sections);
            return new SchiffmanDesign(z0, fHz, phaseDeg, band, rho, k, ripple * 180.0 / Math.PI, ze, zo, w, gap, l,
                CoupledLineCouplerPlan.Modes(w, gap, l, fHz, s), w0, lref, reference,
                new PassivePlan(Pins, sections, NodeCount: route.NextNode - 1) { Warnings = route.Warnings })
            {
                MeanderPitchMeters = ((PlanCurve)route.Sections[1]).Radius * 2.0,
            };
        }
        sections.AddRange(Reference(reference, lref, w0, y3, fHz, s, line.Z0, q0, part));
        return new SchiffmanDesign(z0, fHz, phaseDeg, band, rho, k, ripple * 180.0 / Math.PI, ze, zo, w, gap, l,
            CoupledLineCouplerPlan.Modes(w, gap, l, fHz, s), w0, lref, reference,
            new PassivePlan(Pins, sections, NodeCount: 10));
    }

    /// <summary>
    /// The meandered reference: a <see cref="PlanRoute"/> meander of θ = ΔΦ + π from pin 3 heading +x, one turn of curved
    /// half-turn stacked downward, so it ends on pin 4 heading back to the left edge under pin 3. Its pitch is set by
    /// bisection so each leg is the C-section's length <paramref name="l"/>, and the reference lies under the section as
    /// one block; when even six widths' pitch leaves legs shorter than that, six widths it is, clear of the route's
    /// coupling warning, and the legs are what θ leaves. The route holds θ through the turn.
    /// </summary>
    private static PlanRouteResult Meandered(double theta, double l, double w0, double y, double fHz, MicrostripSubstrate s)
    {
        PlanRouteResult Route(double pitch) => PlanRoute.Build(0.0, y, 0.0, w0, theta, fHz, s, PlanRouteStyle.Meander,
            PlanCorner.Curve, turns: 1, pitch: pitch, side: -1, from: 3, to: 4, next: 7);
        double Leg(double pitch)
        {
            try { return ((PlanLine)Route(pitch).Sections[0]).L; }
            catch (ParameterRefusalException) { return double.NegativeInfinity; }
        }
        double lo = PassivePlan.OnGrid(6.0 * w0);
        if (Leg(lo) <= l) return Route(lo);
        double hi = lo;
        while (Leg(hi) > l) hi *= 2.0;
        for (int i = 0; i < 50; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (Leg(mid) > l) lo = mid; else hi = mid;
        }
        return Route(PassivePlan.OnGrid(hi));
    }

    /// <summary>
    /// The C-section's length whose own model — the MCLIN and its strap, stamped as a plan — is 180° at F, by bisection
    /// about the quarter wave at the modes' mean εeff: at F the difference is then exactly ΔΦ on the board, the strap's
    /// inductance and the modes' unequal velocities taken up in the length.
    /// </summary>
    private static double HalfTurnLength(double w, double gap, double quarter, double z0, double fHz, MicrostripSubstrate s)
    {
        double ImS21(double l)
        {
            var plan = new PassivePlan(["1", "2"], [new PlanCoupledLine([1, 3, 2, 4], 0.0, 0.0, 0.0, w, gap, l),
                new PlanLine([3, 4], l, 0.0, 270.0, w, w + gap + w)], 4);
            var model = new MicrostripPlanModel(plan, s, "(C-section)");
            var y = new LocalAdmittanceBlock(2);
            model.Stamp(y, new Elaboration.ElaboratedComponent(nameof(MicrostripPlanModel), "(C-section)", [1, 2],
                new Dictionary<string, Expressions.Value>(), model), 2.0 * Math.PI * fHz);
            var s21 = -2.0 * z0 * y[1, 0] / ((1.0 + z0 * y[0, 0]) * (1.0 + z0 * y[1, 1]) - z0 * z0 * y[0, 1] * y[1, 0]);
            return s21.Imaginary;
        }
        // Below the length the phase is short of 180° (Im S21 < 0), above it past (Im S21 > 0).
        double lo = 0.7 * quarter, hi = 1.3 * quarter;
        if (!(ImS21(lo) < 0 && ImS21(hi) > 0)) return quarter;
        for (int i = 0; i < 60; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (ImS21(mid) < 0) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>
    /// The reference line from pin 3 at (0, <paramref name="y"/>) to pin 4, <paramref name="length"/> long at F: straight
    /// along +x, or folded back below itself, six widths lower, on a half-turn or two corners, each counted at its own
    /// electrical length. The one place a reference route is made, so a meandered route is one more case here.
    /// </summary>
    private static IEnumerable<PlanSection> Reference(SchiffmanReference form, double length, double w0, double y,
        double fHz, MicrostripSubstrate s, double z, double q0, string part)
    {
        if (form == SchiffmanReference.Straight)
            return [new PlanLine([3, 4], 0.0, y, 0.0, w0, PassivePlan.OnGrid(length))];
        double drop = PassivePlan.OnGrid(6.0 * w0);
        if (form == SchiffmanReference.Curve)
        {
            double radius = drop / 2.0;
            double run = PassivePlan.OnGrid((length - Math.PI * (radius - MicrostripCurve.EffectiveRadiusOffsetPerWidth * w0)) / 2.0);
            Refuse(run, part);
            return
            [
                new PlanLine([3, 7], 0.0, y, 0.0, w0, run),
                new PlanCurve(w0, -180.0, radius, [7, 8], run, y, 0.0),
                new PlanLine([8, 4], run, y - drop, 180.0, w0, run),
            ];
        }
        var bend = new PlanBend([], 0.0, 0.0, 0.0, w0, -90.0, PlanBend.MiterFor(form == SchiffmanReference.Miter
            ? PlanCorner.Miter : PlanCorner.Square, w0, s));
        double r = bend.Arm, side = drop - 2.0 * r, corner = bend.LineLength(s, fHz, z, q0);
        double a = PassivePlan.OnGrid((length - side - 2.0 * corner) / 2.0);
        Refuse(a, part);
        return
        [
            new PlanLine([3, 7], 0.0, y, 0.0, w0, a),
            new PlanBend([7, 8], a, y, 0.0, w0, -90.0, bend.M),
            new PlanLine([8, 9], a + r, y - r, 270.0, w0, side),
            new PlanBend([9, 10], a + r, y - r - side, 270.0, w0, -90.0, bend.M),
            new PlanLine([10, 4], a, y - drop, 180.0, w0, a),
        ];
    }

    private static void Refuse(double run, string part)
    {
        if (!(run > 0))
            throw new ParameterRefusalException(Diagnostic.Create("device.schiffman.reference-too-short",
                DiagnosticSeverity.Error, "{part}: the reference line is too short at this F to fold; lay it straight.",
                ("part", part)));
    }
}
