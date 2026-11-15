using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>What the equal-split divider comes out to on one substrate: the derived numbers, and the plan the
/// model stamps and the PCell draws.</summary>
/// <param name="W0Meters">Width of the Z0 line the three ports leave on.</param>
/// <param name="ArmZ0">√2·Z0, each quarter-wave arm's impedance.</param>
/// <param name="ArmWMeters">The arm's width.</param>
/// <param name="ArmEeff">MLIN's dispersive εeff of the arm at F.</param>
/// <param name="QuarterWaveMeters">λg/4 of the arm at F: the electrical length between the input junction's and
/// an output junction's reference planes.</param>
/// <param name="RingRadiusMeters">Centre-line radius of each arm's half-turn.</param>
/// <param name="FeedMeters">Length of the Z0 line from each junction to its port pin.</param>
/// <param name="IsolationOhms">2·Z0, the resistor that belongs across R2-R3.</param>
public sealed record WilkinsonDesign(
    double Z0, double FreqHz, double GapMeters,
    double W0Meters, double ArmZ0, double ArmWMeters, double ArmEeff, double QuarterWaveMeters,
    double RingRadiusMeters, double FeedMeters, double IsolationOhms, PassivePlan Plan);

/// <summary>
/// The equal-split two-way divider (E. J. Wilkinson, IRE Trans. MTT, 1960; D. M. Pozar, Microwave
/// Engineering, the Wilkinson section) planned as one microstrip ring. Pure: specs and a substrate in,
/// <see cref="WilkinsonDesign"/> out. <see cref="MicrostripPlanModel"/> stamps the plan and the PCell draws
/// it, so the circuit and the copper are the same pieces.
///
/// <para><b>The ring.</b> An input tee (pin 1 on its branch) splits into two arms of √2·Z0, each a straight run
/// on the tee and a 180° MCURVE, which come back facing each other across <c>Gap</c>. Each arm ends in an output
/// tee whose branch leaves outward to pin 2 or 3; the output tee's other through end is R2 or R3, the inner
/// edge of the gap. <c>Gap</c> is the span of the isolation resistor's pins, so an 0402 laid across R2-R3
/// lands with its pads on the two ends and closes the ring. The resistor is NOT part of this part: it is an
/// ordinary resistor of 2·Z0 on R2-R3, so EM can cut it out as a component port and LVS and a BOM see it.</para>
///
/// <para><b>The numbers.</b> Widths come from MLIN's own model at F (its dispersive Z0), λg/4 from MLIN's own
/// dispersive εeff at F. The arm length λg/4 is measured between the junctions' reference planes, which is
/// where <see cref="MicrostripTeeModel"/>'s junction network is referred: the input tee's arm line
/// (Gap/2 + W0/2) plus the curve's effective length π·(R − 0.2·W), which fixes R. Every port pin sits one W0
/// of Z0 line past its junction, so a port lands on a line and not in a junction's fringing field. All
/// dimensions are held on <see cref="PassivePlan.OnGrid"/>.</para>
///
/// <para>Stage 2 of docs/design/rf-passives.md adds the split ratio and section count; both change the arms'
/// impedances and lengths and add sections, which is why each arm is planned on its own.</para>
/// </summary>
public static class WilkinsonPlan
{
    /// <summary>The part's pins, in terminal order: the input, the two outputs, and the resistor's two ends.</summary>
    public static readonly IReadOnlyList<string> Pins = ["1", "2", "3", "R2", "R3"];

    public static WilkinsonDesign Build(double z0, double fHz, double gapMeters, MicrostripSubstrate s)
    {
        foreach (var (name, value) in new[] { ("Z0", z0), ("F", fHz), ("Gap", gapMeters) })
            if (!(value > 0))
                throw new ParameterRefusalException(Diagnostic.Create("device.wilkinson.spec-not-positive",
                    DiagnosticSeverity.Error, "Wilkinson divider: {name} = {value} must be positive.",
                    ("name", name), ("value", value)));

        double armZ0 = Math.Sqrt(2.0) * z0;
        double w0 = PassivePlan.OnGrid(Width(z0, fHz, s));
        double wa = PassivePlan.OnGrid(Width(armZ0, fHz, s));
        double gap = PassivePlan.OnGrid(gapMeters);
        double eeff = Line(wa, s).LineParameters(fHz).Eeff;
        double quarter = PlanarLineAngle.LengthMeters(eeff, Math.PI / 2.0, fHz);

        double feed = w0;
        double a = gap / 2.0 + w0;                      // input tee: centre to each arm's curve
        double straight = a - w0 / 2.0;                 // the line that arm carries before its curve
        double radius = PassivePlan.OnGrid(
            (quarter - straight) / Math.PI - MicrostripCurve.EffectiveRadiusOffsetPerWidth * wa);
        if (radius < wa)
            throw new ParameterRefusalException(Diagnostic.Create("device.wilkinson.ring-too-small",
                DiagnosticSeverity.Error,
                "Wilkinson divider: a quarter wave at F = {f} Hz is {quarter} m, which leaves a ring radius of {radius} m, "
              + "under the arm's own width {w} m. Lower F, or narrow Gap.",
                ("f", fHz), ("quarter", quarter), ("radius", radius), ("w", wa)));

        double l3 = feed + wa / 2.0;                    // every tee's branch: half the arm, then the feed
        double x1 = l3, x2 = x1 + 2.0 * radius;
        // Nodes: 1-5 the pins (1, 2, 3, R2, R3); 6/7 the input tee's top/bottom arm ends; 8/9 the curves' far ends.
        var sections = new PlanSection[]
        {
            new PlanTee(wa, wa, w0, a, a, l3, [6, 7, 1], x1, a, 270.0),
            new PlanCurve(wa, -180.0, radius, [6, 8], x1, a, 90.0),
            new PlanCurve(wa, 180.0, radius, [7, 9], x1, -a, 270.0),
            // The output tees: through arms of half the branch width, so the curve's end and R2/R3 are the
            // junction's own reference planes and carry no line.
            new PlanTee(wa, wa, w0, w0 / 2.0, w0 / 2.0, l3, [4, 8, 2], x2, gap / 2.0, 90.0),
            new PlanTee(wa, wa, w0, w0 / 2.0, w0 / 2.0, l3, [9, 5, 3], x2, -a, 90.0),
        };

        return new WilkinsonDesign(z0, fHz, gap, w0, armZ0, wa, eeff, quarter, radius, feed, 2.0 * z0,
            new PassivePlan(Pins, sections, NodeCount: 9));
    }

    private static MicrostripLineModel Line(double w, MicrostripSubstrate s)
        => new(w, 1.0, s.HMeters, s.TMeters, s.EpsR, s.SigmaSPerM, s.TanD, "(Wilkinson plan)", s.RoughnessMeters);

    /// <summary>The width whose MLIN Z0 at F is <paramref name="z"/>; a Z0 no width reaches is refused.</summary>
    private static double Width(double z, double fHz, MicrostripSubstrate s)
    {
        var reporter = new MicrostripValidityReporter("Wilkinson divider");
        double w = Planar.PlanarLineSynthesis.SynthesizeWidth(x => Line(x, s).LineParameters(fHz).Z0, z, s.HMeters,
            "MLIN", reporter);
        if (reporter.Drain() is [var unreachable, ..])
            throw new ParameterRefusalException(Diagnostic.Create("device.wilkinson.width-unreachable",
                DiagnosticSeverity.Error, "{message}", ("message", unreachable.Message)));
        return w;
    }
}
