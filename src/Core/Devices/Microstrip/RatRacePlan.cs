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

    /// <summary>Which ring: the 3λg/4 arm as line, or as a coupled section.</summary>
    public RatRaceForm Form { get; init; }

    /// <summary>Coupled-line form: the section's asked even and odd modes, 2·Ze·Zo/(Ze − Zo) = √2·Z0.</summary>
    public (double Ze, double Zo) SectionModes { get; init; }

    /// <summary>Coupled-line form: the section's pair as drawn — strip width, gap and length — and its coupling, dB.</summary>
    public (double W, double S, double L, double CouplingDb) Section { get; init; }
}

/// <summary>Which rat-race (MRRC): the ring of line, or the compact ring whose 3λg/4 arm is a coupled section with its
/// diagonal far ends grounded (S. March, 1968).</summary>
public enum RatRaceForm { Ring, CoupledLine }

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

    /// <summary>The coupled-line form's pins: the four ports, then the section's grounded ends, G2 beside port 2's tee and
    /// G4 beside port 4's, for a via to ground each.</summary>
    public static readonly IReadOnlyList<string> CoupledLinePins = ["1", "2", "3", "4", "G2", "G4"];

    /// <summary>The coupling the coupled-line form's section opens with, dB.</summary>
    public const double DefaultSectionCoupling = 10.0;

    public static RatRaceDesign Build(double z0, double fHz, MicrostripSubstrate s, PlanCorner corner = PlanCorner.Curve,
        double meander = 0, RatRaceForm form = RatRaceForm.Ring, double coupling = DefaultSectionCoupling)
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
        if (form == RatRaceForm.CoupledLine)
        {
            if (meander != 0)
                throw new ParameterRefusalException(Diagnostic.Create("device.ratrace.coupled-line-meander",
                    DiagnosticSeverity.Error, "Rat-race hybrid: the coupled-line form has no 3λ/4 arm to meander; set Meander "
                  + "to 0."));
            return CoupledLine(z0, fHz, s, corner, coupling, w0, ringZ0, wr, eeff, q);
        }
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

    /// <summary>
    /// The coupled-line ring (S. March, "A Wideband Stripline Hybrid Ring," IEEE Trans. MTT, vol. 16, no. 6, p. 361, June
    /// 1968). A pair a quarter wave long with two diagonal far ends grounded is, at F, a 270° line: with ports 2 and 3 of
    /// the pair shorted, Y11 = −j(Ye + Yo)/2·cot θ and Y14 = j(Ye − Yo)/2·csc θ, a line of 2·Ze·Zo/(Ze − Zo) at θ = π/2,
    /// whose transfer has the 3λ/4 arm's sign. So it replaces that arm, at a third of its length, when 2·Ze·Zo/(Ze − Zo) =
    /// √2·Z0. That fixes one condition; the section's coupling c fixes the other: √(Ze·Zo) = √2·Z0·c/√(1 − c²), March's
    /// own section being the 3 dB one (√(Ze·Zo) = √2·Z0) and a weaker one a wider, lower-impedance pair that edge coupling
    /// builds. Its length is where its own model, the grounded ends in place, has Y11 = 0 at F, about λg/4 at the modes'
    /// mean εeff.
    ///
    /// <para><b>The layout.</b> The section lies along the bottom, strip A on top. Tee 4 sits on strip A's near end, its
    /// port running on to the left and the ring rising from its branch; tee 2 on strip B's far end, its port leaving down
    /// from its branch and the ring running on to the right. Strip A's far end (G2) and strip B's near end (G4) are the
    /// grounded ends: G2 inside the ring, with at least three port widths of room before the ring's right side, G4
    /// outside below port 4. The three λg/4 arms are runs and corners around the other three sides: 2 → 1 right and up to
    /// tee 1 on the right side (its port to the right), 1 → 3 up and across to tee 3 on the top (its port up), 3 → 4
    /// across and down into tee 4. Each arm counts its corner at its own electrical length at F
    /// (<see cref="PlanSection.LineLength"/>): an MCURVE quarter turn of 3W, or an MMITER or MBEND corner, by
    /// <c>Corner</c>. The square closes because the right side is the taller by the pair's pitch, which the top run's
    /// split between 1 → 3 and 3 → 4 takes up.</para>
    ///
    /// <para><b>What a board builds.</b> Whatever its coupling, the section's mutual admittance is fixed: 1/Zo − 1/Ze =
    /// 2/(√2·Z0). Edge coupling at the coupled-line model's narrowest gap (S/h = 0.1) reaches about 0.022 S on 20 mil
    /// RO4350B, 0.028 S on 25 mil alumina and 0.033 S on 100 µm GaAs, against the 0.028 S a 50 Ω ring needs: so on GaAs
    /// a 50 Ω ring builds (a 10 dB section of 227 µm strips 11 µm apart at 10 GHz), and on the board one of about 65 Ω and
    /// more.</para>
    ///
    /// <para><b>Refusals.</b> A section the board cannot build (<c>device.ratrace.coupled-line-unbuildable</c>, naming the
    /// admittance), a tee wider than the pair's strip plus two gaps (it would touch the grounded strip beside it), and an
    /// arm left with less than one ring width of straight line.</para>
    /// </summary>
    private static RatRaceDesign CoupledLine(double z0, double fHz, MicrostripSubstrate s, PlanCorner corner, double couplingDb,
        double w0, double ringZ0, double wr, double eeff, double q)
    {
        const string part = "Rat-race hybrid";
        if (!(couplingDb > 0))
            throw new ParameterRefusalException(Diagnostic.Create("device.ratrace.spec-not-positive",
                DiagnosticSeverity.Error, "Rat-race hybrid: Coupling = {c} must be positive.", ("c", couplingDb)));
        double c = Math.Pow(10.0, -couplingDb / 20.0), zm = ringZ0 * c / Math.Sqrt(1.0 - c * c), root = Math.Sqrt((1.0 + c) / (1.0 - c));
        double ze = zm * root, zo = zm / root;
        CoupledLineGeometry g;
        try { g = CoupledLineSynthesis.Synthesize(ze, zo, fHz, s, part); }
        catch (ParameterRefusalException ex)
        {
            // Whatever its coupling, the section's mutual admittance is fixed: 1/Zo − 1/Ze = 2/(√2·Z0). Say so, since a
            // weaker coupling only trades a narrower gap for wider strips.
            throw new ParameterRefusalException(Diagnostic.Create("device.ratrace.coupled-line-unbuildable",
                DiagnosticSeverity.Error, "Rat-race hybrid: the coupled-line ring's section needs 1/Zo − 1/Ze = √2/Z0 = {y} S "
              + "at any coupling, and this board's edge-coupled pair cannot give Ze = {ze} ohm, Zo = {zo} ohm ({why}). A "
              + "higher Z0, a higher-permittivity or thinner substrate, or another Coupling may reach it.",
                ("y", Math.Sqrt(2.0) / z0), ("ze", ze), ("zo", zo), ("why", ex.Message)));
        }
        double wc = PassivePlan.OnGrid(g.WMeters), gap = PassivePlan.OnGrid(g.SMeters), p = wc + gap;
        double lc = PassivePlan.OnGrid(NullLength(wc, gap, g.QuarterWaveMeters, fHz, s));
        if (Math.Max(w0, wr) >= wc + 2.0 * gap)
            throw new ParameterRefusalException(Diagnostic.Create("device.ratrace.coupled-line-tight", DiagnosticSeverity.Error,
                "Rat-race hybrid: the section's strips ({wc} m, {gap} m apart) are narrower than the tees ({wt} m) less two gaps, "
              + "so a tee would touch the grounded strip beside it. Ask for a weaker Coupling.",
                ("wc", wc), ("gap", gap), ("wt", Math.Max(w0, wr))));

        PlanSection Corner(int sign) => corner == PlanCorner.Curve
            ? new PlanCurve(wr, 90.0 * sign, PassivePlan.OnGrid(3.0 * wr), [], 0.0, 0.0, 0.0)
            : new PlanBend([], 0.0, 0.0, 0.0, wr, 90.0 * sign, PlanBend.MiterFor(corner, wr, s));
        var turn = Corner(1);
        double r = turn is PlanCurve tc ? tc.Radius : ((PlanBend)turn).Arm;
        double k = turn.LineLength(s, fHz, ringZ0, q);
        double m4 = Math.Max(wc, w0) / 2.0, h = PassivePlan.OnGrid(2.0 * w0);

        // 2 → 1: h, a corner, a1. 1 → 3: a2, a corner, b2. 3 → 4: b3, a corner, a3. The square closes on x (the left side
        // over tee 4's junction) and on y (the top run level for both sides), which leaves a2 free of h.
        double a1 = PassivePlan.OnGrid(q - h - k);
        double a2 = PassivePlan.OnGrid((q - k - lc - wr / 2.0 + p - w0 + m4) / 2.0);
        double b2 = PassivePlan.OnGrid(q - k - a2);
        double xr = lc + w0 + h + r, y1 = -p + r + a1, top = y1 + w0 + a2 + r;
        double x3 = xr - r - b2;
        double b3 = x3 - w0 - r + wr / 2.0, a3 = top - r - m4;     // so the left side lands on tee 4's branch
        foreach (var (name, len) in new[] { ("2 → 1", a1), ("1 → 3", Math.Min(a2, b2)), ("3 → 4", Math.Min(a3, b3)) })
            if (!(len >= wr))
                throw new ParameterRefusalException(Diagnostic.Create("device.ratrace.ring-too-small", DiagnosticSeverity.Error,
                    "Rat-race hybrid: in the coupled-line ring at F = {f} Hz the arm {arm} keeps only {len} m of straight line, "
                  + "under the ring's own width {w} m. Lower F, or ask for a weaker Coupling (a wider section).",
                    ("f", fHz), ("arm", name), ("len", len), ("w", wr)));

        double hh = w0 / 2.0, l3 = wr / 2.0 + w0;
        // Nodes: 1-4 the ports, 5 G2 (strip A's far end), 6 G4 (strip B's near end); 7 strip A's near end (tee 4), 8 strip
        // B's far end (tee 2); then around the ring from tee 2: 9 tee 2's ring side, 10/11 the first corner, 12/13 tee 1,
        // 14/15 the second corner, 16/17 tee 3, 18/19 the third corner, 20 tee 4's branch.
        PlanSection[] sections =
        [
            new PlanCoupledLine([7, 5, 6, 8], 0.0, 0.0, 0.0, wc, gap, lc),
            new PlanTee(wc, wr, w0, hh, hh, Math.Max(wc, wr) / 2.0 + w0, [8, 9, 2], lc, -p, 0.0),
            new PlanLine([9, 10], lc + w0, -p, 0.0, wr, h),
            turn with { Nodes = [10, 11], X = lc + w0 + h, Y = -p, RotationDeg = 0.0 },
            new PlanLine([11, 12], xr, -p + r, 90.0, wr, a1),
            new PlanTee(wr, wr, w0, hh, hh, l3, [12, 13, 1], xr, y1, 90.0),
            new PlanLine([13, 14], xr, y1 + w0, 90.0, wr, a2),
            turn with { Nodes = [14, 15], X = xr, Y = top - r, RotationDeg = 90.0 },
            new PlanLine([15, 16], xr - r, top, 180.0, wr, b2),
            new PlanTee(wr, wr, w0, hh, hh, l3, [16, 17, 3], x3, top, 180.0),
            new PlanLine([17, 18], x3 - w0, top, 180.0, wr, b3),
            turn with { Nodes = [18, 19], X = x3 - w0 - b3, Y = top, RotationDeg = 180.0 },
            new PlanLine([19, 20], -wr / 2.0, top - r, 270.0, wr, a3),
            new PlanTee(wc, w0, wr, wr / 2.0, wr / 2.0 + w0, m4, [7, 4, 20], 0.0, 0.0, 180.0),
        ];
        return new RatRaceDesign(z0, fHz, w0, ringZ0, wr, eeff, q, 0.0, w0,
            new PassivePlan(CoupledLinePins, sections, NodeCount: 20))
        {
            Corner = corner, CornerMeters = k, Form = RatRaceForm.CoupledLine, SectionModes = (ze, zo),
            Section = (wc, gap, lc, couplingDb),
        };
    }

    /// <summary>The section's length whose own model, strip A's far end and strip B's near end grounded, has no self
    /// admittance at F: Y11 = −j(Ye·cot θe + Yo·cot θo)/2 = 0, by bisection about the quarter wave at the mean εeff.</summary>
    private static double NullLength(double wc, double gap, double quarter, double fHz, MicrostripSubstrate s)
    {
        double ImY11(double l)
        {
            var plan = new PassivePlan(["a", "b", "ga", "gb"], [new PlanCoupledLine([1, 3, 4, 2], 0.0, 0.0, 0.0, wc, gap, l)], 4);
            var model = new MicrostripPlanModel(plan, s, "(coupled-line ring section)");
            var y = new LocalAdmittanceBlock(2);
            model.Stamp(y, new Elaboration.ElaboratedComponent(nameof(MicrostripPlanModel), "(section)", [1, 2, 0, 0],
                new Dictionary<string, Expressions.Value>(), model), 2.0 * Math.PI * fHz);
            return y[0, 0].Imaginary;
        }
        double lo = 0.8 * quarter, hi = 1.2 * quarter;
        if (!(ImY11(lo) < 0 && ImY11(hi) > 0)) return quarter;
        for (int i = 0; i < 60; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (ImY11(mid) < 0) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
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
