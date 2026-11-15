using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>What a rat-race hybrid comes out to on one substrate: the derived numbers, and the plan the model
/// stamps and the PCell draws.</summary>
/// <param name="W0Meters">Width of the Z0 feeds.</param>
/// <param name="RingZ0">√2·Z0, the ring's impedance.</param>
/// <param name="RingWMeters">The ring's width.</param>
/// <param name="RingEeff">MLIN's dispersive εeff of the ring at F.</param>
/// <param name="QuarterWaveMeters">λg/4 of the ring at F.</param>
/// <param name="RadiusMeters">Centre-line radius of the ring's two half-turns.</param>
/// <param name="FeedMeters">Length of each feed, junction to pin.</param>
public sealed record RatRaceDesign(
    double Z0, double FreqHz, double W0Meters, double RingZ0, double RingWMeters, double RingEeff,
    double QuarterWaveMeters, double RadiusMeters, double FeedMeters, PassivePlan Plan);

/// <summary>
/// The rat-race (180°) hybrid planned as one microstrip ring (D. M. Pozar, Microwave Engineering, the ring
/// hybrid; J. Reed and G. J. Wheeler, IRE Trans. MTT, 1956, for the even/odd analysis). Pure: specs and a
/// substrate in, <see cref="RatRaceDesign"/> out; <see cref="MicrostripPlanModel"/> stamps the plan and the
/// PCell draws it.
///
/// <para><b>Pins.</b> 1 is the sum (Σ) port and 4 the difference (Δ) port; 2 and 3 are the outputs. Driven at 1,
/// 2 and 3 are in phase and 4 is isolated; driven at 4, 2 and 3 are 180° apart and 1 is isolated.</para>
///
/// <para><b>The ring.</b> √2·Z0, 1.5 λg around, the pins in ring order 2, 1, 3, 4 spaced λg/4, λg/4, λg/4 and
/// 3λg/4. Widths are MLIN's at F, λg/4 MLIN's own dispersive εeff at F, and every spacing is measured between
/// the reference planes of the MTEEs on the ring, as <see cref="WilkinsonPlan"/>'s arms are. The ring is drawn
/// as two straight runs joined by two 180° MCURVEs: tees 2, 1 and 3 on the upper run with their branches up,
/// tee 4 on the lower run with its branch down. The lower run, one tee against three, carries the two extra
/// junction widths the upper run takes up, which fixes the half-turns' radius: π·(R − 0.2·W) = λg/4 − W0.
/// Each pin sits one W0 of Z0 line past its junction, in the tee's branch.</para>
/// </summary>
public static class RatRacePlan
{
    /// <summary>The part's pins, in terminal order: Σ, the two outputs, Δ.</summary>
    public static readonly IReadOnlyList<string> Pins = ["1", "2", "3", "4"];

    public static RatRaceDesign Build(double z0, double fHz, MicrostripSubstrate s)
    {
        foreach (var (name, value) in new[] { ("Z0", z0), ("F", fHz) })
            if (!(value > 0))
                throw new ParameterRefusalException(Diagnostic.Create("device.ratrace.spec-not-positive",
                    DiagnosticSeverity.Error, "Rat-race hybrid: {name} = {value} must be positive.",
                    ("name", name), ("value", value)));

        const string widthCode = "device.ratrace.width-unreachable";
        double ringZ0 = Math.Sqrt(2.0) * z0;
        double w0 = PassivePlan.OnGrid(BranchLinePlan.Width(z0, fHz, s, "Rat-race hybrid", widthCode));
        double wr = PassivePlan.OnGrid(BranchLinePlan.Width(ringZ0, fHz, s, "Rat-race hybrid", widthCode));
        double eeff = BranchLinePlan.Line(wr, s).LineParameters(fHz).Eeff;
        double q = PassivePlan.OnGrid(PlanarLineAngle.LengthMeters(eeff, Math.PI / 2.0, fHz));
        double radius = PassivePlan.OnGrid((q - w0) / Math.PI - MicrostripCurve.EffectiveRadiusOffsetPerWidth * wr);
        if (radius < wr)
            throw new ParameterRefusalException(Diagnostic.Create("device.ratrace.ring-too-small",
                DiagnosticSeverity.Error,
                "Rat-race hybrid: a quarter wave at F = {f} Hz is {quarter} m, which leaves a half-turn radius of "
              + "{radius} m, under the ring's own width {w} m. Lower F.",
                ("f", fHz), ("quarter", q), ("radius", radius), ("w", wr)));

        double feed = w0, l3 = wr / 2.0 + feed, h = w0 / 2.0;
        double xr = 3.0 * w0 + 2.0 * q;                    // where the upper run meets the right half-turn
        // Nodes: 1-4 the pins; 5-10 the upper run left to right (2's left/right, 1's, 3's); 11 the right half-turn's
        // lower end; 12/13 tee 4's right/left; 14 the left half-turn's lower end.
        var sections = new PlanSection[]
        {
            new PlanTee(wr, wr, w0, h, h, l3, [6, 5, 2], w0, radius, 180.0),
            new PlanLine([6, 7], w0, radius, 0.0, wr, q),
            new PlanTee(wr, wr, w0, h, h, l3, [8, 7, 1], 2.0 * w0 + q, radius, 180.0),
            new PlanLine([8, 9], 2.0 * w0 + q, radius, 0.0, wr, q),
            new PlanTee(wr, wr, w0, h, h, l3, [10, 9, 3], xr, radius, 180.0),
            new PlanCurve(wr, -180.0, radius, [10, 11], xr, radius, 0.0),
            // 3 to 4: the half-turn and one W0 of line. 4 to 2: λg/2 + W0 of line and the other half-turn.
            new PlanLine([12, 11], xr - w0, -radius, 0.0, wr, w0),
            new PlanTee(wr, wr, w0, h, h, l3, [13, 12, 4], w0 + 2.0 * q, -radius, 0.0),
            new PlanLine([14, 13], 0.0, -radius, 0.0, wr, 2.0 * q + w0),
            new PlanCurve(wr, -180.0, radius, [14, 5], 0.0, -radius, 180.0),
        };
        return new RatRaceDesign(z0, fHz, w0, ringZ0, wr, eeff, q, radius, feed,
            new PassivePlan(Pins, sections, NodeCount: 14));
    }
}
