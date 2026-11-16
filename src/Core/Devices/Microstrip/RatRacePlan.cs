using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>What a rat-race hybrid comes out to on one substrate: the derived numbers, and the plan the model
/// stamps and the PCell draws.</summary>
/// <param name="W0Meters">Width of the Z0 feeds.</param>
/// <param name="RingZ0">√2·Z0, the ring's impedance.</param>
/// <param name="RingWMeters">The ring's width.</param>
/// <param name="RingEeff">MLIN's dispersive εeff of the ring at F.</param>
/// <param name="QuarterWaveMeters">λg/4 of the ring at F.</param>
/// <param name="RadiusMeters">Centre-line radius of the ring's two half-turns; 0 when the ring has corners.</param>
/// <param name="FeedMeters">Length of each feed, junction to pin.</param>
public sealed record RatRaceDesign(
    double Z0, double FreqHz, double W0Meters, double RingZ0, double RingWMeters, double RingEeff,
    double QuarterWaveMeters, double RadiusMeters, double FeedMeters, PassivePlan Plan)
{
    /// <summary>How the ring turns: half-turns, or corners.</summary>
    public PlanCorner Corner { get; init; }

    /// <summary>With corners, the straight run down each end of the ring between its two corners; 0 for half-turns.</summary>
    public double SideMeters { get; init; }

    /// <summary>With corners, the length of ring each corner stands for at F (<see cref="PlanSection.LineLength"/>).</summary>
    public double CornerMeters { get; init; }

    /// <summary>Meandered: the turns of the 3λg/4 arm folded inside the ring; 0 for the plain ring.</summary>
    public int MeanderTurns { get; init; }

    /// <summary>Meandered: the folded arm's legs centre to centre; 0 for the plain ring.</summary>
    public double MeanderPitchMeters { get; init; }
}

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
///
/// <para><b>Corners.</b> With <see cref="PlanCorner.Miter"/> or <see cref="PlanCorner.Square"/> each half-turn is two 90°
/// corners (an MMITER at its usual chamfer, or MBEND's square corner) with a straight side between them, so the ring is a
/// rectangle. The spacings count each corner at its own electrical length at F, which <see cref="PlanSection.LineLength"/>
/// takes from the corner's model on the substrate, so the plan stays a pure function of the specs and the substrate:
/// 2·(corner) + side = λg/4 − W0, and the sides are that much shorter. At <see cref="PlanCorner.Curve"/> the plan is
/// the half-turns' to the last bit.</para>
///
/// <para><b>Meandered</b> (<c>meander</c> = k turns, odd; Eccleston and Ong for compact rings, He and Wang for a meandered
/// one). The three λg/4 arms make a C: 2 → 1 the upper run, 1 → 3 a <see cref="PlanRoute"/> U-turn of radius Y down the
/// right, 3 → 4 the lower run. The 3λg/4 arm closes it from the inside: a half-turn from tee 4's free end back into the
/// ring, a <see cref="PlanRoute"/> meander of k turns whose legs run between the two runs, and a half-turn out to tee 2.
/// Every turn of that arm has radius Y/(k + 2), so the legs sit 2Y/(k + 2) apart and from the runs, and the half-turns'
/// own electrical length at F comes out of the meander's θ. Y is the plain ring's radius for a λg/4 − 2·W0 half-turn,
/// so the ring is no taller than the plain one and about λg/4 narrower. Half-turns only: a cornered meander is refused.
/// The legs are separate lines in the model, uncoupled to each other and to the runs.</para>
/// </summary>
public static class RatRacePlan
{
    /// <summary>The part's pins, in terminal order: Σ, the two outputs, Δ.</summary>
    public static readonly IReadOnlyList<string> Pins = ["1", "2", "3", "4"];

    public static RatRaceDesign Build(double z0, double fHz, MicrostripSubstrate s, PlanCorner corner = PlanCorner.Curve,
        double meander = 0)
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
        if (meander != 0) return Meandered(z0, fHz, s, corner, meander, w0, ringZ0, wr, eeff, q);
        if (corner != PlanCorner.Curve) return Cornered(z0, fHz, s, corner, w0, ringZ0, wr, eeff, q);
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

    /// <summary>The compact ring: the λg/4 arms a C, the 3λg/4 arm meandered inside it (see the class remarks).</summary>
    private static RatRaceDesign Meandered(double z0, double fHz, MicrostripSubstrate s, PlanCorner corner, double meander,
        double w0, double ringZ0, double wr, double eeff, double q)
    {
        if (meander != Math.Round(meander) || meander < 1 || meander % 2 == 0 || corner != PlanCorner.Curve)
            throw new ParameterRefusalException(Diagnostic.Create("device.ratrace.meander", DiagnosticSeverity.Error,
                "Rat-race hybrid: a meandered ring takes an odd number of turns (1, 3, ...) and curved corners, not "
              + "Meander = {k} with Corner = {corner}.", ("k", meander), ("corner", corner.ToString())));
        int k = (int)meander;
        double half = PassivePlan.OnGrid(((q - 2.0 * w0) / Math.PI - MicrostripCurve.EffectiveRadiusOffsetPerWidth * wr)
                                         / (k + 2)), y = PassivePlan.OnGrid((k + 2) * half);
        if (!(half > wr))
            throw new ParameterRefusalException(Diagnostic.Create("device.ratrace.ring-too-small",
                DiagnosticSeverity.Error,
                "Rat-race hybrid: a quarter wave at F = {f} Hz is {quarter} m, which leaves the folded arm's {k} turns a "
              + "radius of {radius} m, no wider than the ring {w} m. Use fewer turns or lower F.",
                ("f", fHz), ("quarter", q), ("k", k), ("radius", half), ("w", wr)));

        // No fit check: the turns' half-turns add to about λg/4 − 2·W0, so the legs are about (λg/2 + 2·W0)/(k + 1), and
        // at k = 1 the right-hand turn's crest stays inside the U-turn by about 0.6·Y.
        double l3 = wr / 2.0 + w0, h = w0 / 2.0, xr = 2.0 * w0 + q;
        var turn = new PlanCurve(wr, -180.0, half, [], 0.0, 0.0, 0.0);
        double folded = 1.5 * Math.PI - 2.0 * turn.LineLength(s, fHz, ringZ0, q) / q * (Math.PI / 2.0);
        // Nodes: 1-4 the pins; 5/6 tee 2's left/right, 7/8 tee 1's; 9/10 tee 3's left/right, 11/12 tee 4's right/left;
        // 13/14 the folded arm's meander ends; the two routes' own from 15.
        var down = PlanRoute.Build(xr, y, 0.0, wr, Math.PI / 2.0, fHz, s, PlanRouteStyle.Meander, PlanCorner.Curve,
            turns: 1, pitch: 2.0 * y, side: -1, from: 8, to: 10, next: 15);
        var fold = PlanRoute.Build(0.0, -y + 2.0 * half, 0.0, wr, folded, fHz, s, PlanRouteStyle.Meander, PlanCorner.Curve,
            turns: k, pitch: 2.0 * half, side: 1, from: 13, to: 14, next: down.NextNode);
        PlanSection[] sections =
        [
            new PlanTee(wr, wr, w0, h, h, l3, [6, 5, 2], w0, y, 180.0),
            new PlanLine([6, 7], w0, y, 0.0, wr, q),
            new PlanTee(wr, wr, w0, h, h, l3, [8, 7, 1], xr, y, 180.0),
            .. down.Sections,
            new PlanTee(wr, wr, w0, h, h, l3, [9, 10, 3], w0 + q, -y, 0.0),
            new PlanLine([11, 9], w0, -y, 0.0, wr, q),
            new PlanTee(wr, wr, w0, h, h, l3, [12, 11, 4], 0.0, -y, 0.0),
            turn with { Nodes = [12, 13], X = 0.0, Y = -y, RotationDeg = 180.0 },
            .. fold.Sections,
            turn with { Nodes = [14, 5], X = 0.0, Y = k * half, RotationDeg = 180.0 },
        ];
        return new RatRaceDesign(z0, fHz, w0, ringZ0, wr, eeff, q, y, w0,
            new PassivePlan(Pins, sections, fold.NextNode - 1) { Warnings = [.. down.Warnings, .. fold.Warnings] })
        {
            MeanderTurns = k, MeanderPitchMeters = 2.0 * half,
        };
    }

    /// <summary>The ring with corners: the half-turns' runs and tees at ±Y, each end two corners and a side.</summary>
    private static RatRaceDesign Cornered(double z0, double fHz, MicrostripSubstrate s, PlanCorner corner,
        double w0, double ringZ0, double wr, double eeff, double q)
    {
        var bend = new PlanBend([], 0.0, 0.0, 0.0, wr, -90.0, PlanBend.MiterFor(corner, wr, s));
        double r = bend.Arm, length = bend.LineLength(s, fHz, ringZ0, q);
        double y = PassivePlan.OnGrid((q - w0 - 2.0 * length) / 2.0 + r), side = 2.0 * y - 2.0 * r;
        if (!(side > 0.0))
            throw new ParameterRefusalException(Diagnostic.Create("device.ratrace.ring-too-small",
                DiagnosticSeverity.Error,
                "Rat-race hybrid: a quarter wave at F = {f} Hz is {quarter} m, which leaves no straight side between the "
              + "ring's corners ({side} m). Lower F.", ("f", fHz), ("quarter", q), ("side", side)));

        double feed = w0, l3 = wr / 2.0 + feed, h = w0 / 2.0;
        double xr = 3.0 * w0 + 2.0 * q;
        // Nodes as the half-turns' (1-14); 15/16 the right end's joints, top first; 17/18 the left end's, bottom first.
        var sections = new PlanSection[]
        {
            new PlanTee(wr, wr, w0, h, h, l3, [6, 5, 2], w0, y, 180.0),
            new PlanLine([6, 7], w0, y, 0.0, wr, q),
            new PlanTee(wr, wr, w0, h, h, l3, [8, 7, 1], 2.0 * w0 + q, y, 180.0),
            new PlanLine([8, 9], 2.0 * w0 + q, y, 0.0, wr, q),
            new PlanTee(wr, wr, w0, h, h, l3, [10, 9, 3], xr, y, 180.0),
            new PlanBend([10, 15], xr, y, 0.0, wr, -90.0, bend.M),
            new PlanLine([15, 16], xr + r, y - r, 270.0, wr, side),
            new PlanBend([16, 11], xr + r, -y + r, 270.0, wr, -90.0, bend.M),
            new PlanLine([12, 11], xr - w0, -y, 0.0, wr, w0),
            new PlanTee(wr, wr, w0, h, h, l3, [13, 12, 4], w0 + 2.0 * q, -y, 0.0),
            new PlanLine([14, 13], 0.0, -y, 0.0, wr, 2.0 * q + w0),
            new PlanBend([14, 17], 0.0, -y, 180.0, wr, -90.0, bend.M),
            new PlanLine([17, 18], -r, -y + r, 90.0, wr, side),
            new PlanBend([18, 5], -r, y - r, 90.0, wr, -90.0, bend.M),
        };
        return new RatRaceDesign(z0, fHz, w0, ringZ0, wr, eeff, q, 0.0, feed, new PassivePlan(Pins, sections, NodeCount: 18))
        {
            Corner = corner, SideMeters = side, CornerMeters = length,
        };
    }
}
