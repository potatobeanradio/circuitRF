using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>What a delay line comes out to on one substrate: the derived numbers, the route and the plan the model
/// stamps and the PCell draws.</summary>
/// <param name="WMeters">The line's width, MLIN's Z0 at F.</param>
/// <param name="Eeff">MLIN's dispersive εeff at F.</param>
/// <param name="LineMeters">θ at F as a length of the line: the straight run's length.</param>
/// <param name="PitchMeters">The meander's legs centre to centre, as built (Pitch, or 6·W when none is given); 0 for the
/// other styles.</param>
public sealed record DelayLineDesign(
    double Z0, double FreqHz, double ThetaRad, double WMeters, double Eeff, double LineMeters, double PitchMeters,
    PlanRouteResult Route, PassivePlan Plan)
{
    /// <summary>The copper's length along its centre line: every leg, turn and corner as drawn.</summary>
    public double CentreLineMeters => Plan.Sections.Sum(section => section switch
    {
        PlanLine l => l.L,
        PlanCurve c => Math.Abs(c.AngleDeg) * Math.PI / 180.0 * c.Radius,
        PlanBend b => 2.0 * b.Arm,
        PlanCoupledStrips m => m.W.Count * m.L,
        _ => 0.0,
    });
}

/// <summary>
/// A length of line that is θ long at F (docs/design/rf-passives.md, "The delay line, MDLY"): a delay or phase line laid
/// straight, round one corner or as a meander, by <see cref="PlanRoute"/>. Pure: specs and a substrate in,
/// <see cref="DelayLineDesign"/> out; <see cref="MicrostripPlanModel"/> stamps the plan and the PCell draws it.
///
/// <para><b>Pins.</b> 1 the start, 2 the end. The run starts at pin 1 heading +x; a meander stacks its legs to the left of
/// that heading (Side = 1) or to the right (−1).</para>
///
/// <para><b>The width</b> is MLIN's for Z0 at F, and θ holds through every corner as the route counts it. With
/// <c>coupled</c> the meander's legs are one N-line section, so the model includes their coupling.</para>
/// </summary>
public static class DelayLinePlan
{
    /// <summary>The part's pins, in terminal order.</summary>
    public static readonly IReadOnlyList<string> Pins = ["1", "2"];

    public static DelayLineDesign Build(double z0, double fHz, double thetaRad, MicrostripSubstrate s,
        PlanRouteStyle style = PlanRouteStyle.Meander, PlanCorner corner = PlanCorner.Curve, double turns = 2,
        double pitch = 0.0, double side = 1, bool coupled = false)
    {
        foreach (var (name, value) in new[] { ("Z0", z0), ("F", fHz), ("θ", thetaRad) })
            if (!(value > 0))
                throw new ParameterRefusalException(Diagnostic.Create("device.delayline.spec-not-positive",
                    DiagnosticSeverity.Error, "Delay line: {name} = {value} must be positive.", ("name", name), ("value", value)));
        if (style == PlanRouteStyle.Meander && (turns != Math.Round(turns) || turns < 1))
            throw new ParameterRefusalException(Diagnostic.Create("device.delayline.turns", DiagnosticSeverity.Error,
                "Delay line: a meander takes a whole number of turns, 1 or more, not {turns}.", ("turns", turns)));

        double w = PassivePlan.OnGrid(BranchLinePlan.Width(z0, fHz, s, "Delay line", "device.delayline.width-unreachable"));
        double eeff = BranchLinePlan.Line(w, s).LineParameters(fHz).Eeff;
        // 6W unless given: a half-turn of 3W, where MCURVE's effective-radius fit starts, and legs 5W apart edge to edge.
        double p = style == PlanRouteStyle.Meander ? (pitch > 0.0 ? pitch : 6.0 * w) : 0.0;
        var route = PlanRoute.Build(0.0, 0.0, 0.0, w, thetaRad, fHz, s, style, corner, (int)turns, p, side < 0 ? -1 : 1,
            coupled: coupled);
        return new DelayLineDesign(z0, fHz, thetaRad, w, eeff, PlanarLineAngle.LengthMeters(eeff, thetaRad, fHz), p, route,
            new PassivePlan(Pins, route.Sections, route.NextNode - 1) { Warnings = route.Warnings });
    }
}
