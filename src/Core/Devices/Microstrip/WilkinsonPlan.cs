using System.Numerics;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>One quarter-wave line of a divider, as the readout lists it: what it is, its impedance, its width and its
/// λg/4 at F.</summary>
public sealed record WilkinsonLine(string Name, double Z, double WMeters, double Eeff, double QuarterWaveMeters);

/// <summary>One arm of a ring with corners, as the readout lists it: the line it rises from the input tee and falls to its
/// output tee (each), the line across between its two corners, and the length of arm each corner stands for at F
/// (<see cref="PlanSection.LineLength"/>).</summary>
public sealed record RingArm(string Name, double RiseMeters, double AcrossMeters, double CornerMeters);

/// <summary>One isolation resistor: the two pins it goes across, and its value.</summary>
public sealed record WilkinsonResistor(string PinA, string PinB, double Ohms);

/// <summary>What the divider comes out to on one substrate: the derived numbers, and the plan the model stamps and
/// the PCell draws.</summary>
/// <param name="W0Meters">Width of the Z0 line the three ports leave on.</param>
/// <param name="ArmZ0">The ring's arm impedance: √2·Z0 for the equal split, the arm to port 2 for an unequal one, the
/// section next to the input for a multi-section one.</param>
/// <param name="ArmWMeters">That arm's width.</param>
/// <param name="ArmEeff">MLIN's dispersive εeff of that arm at F.</param>
/// <param name="QuarterWaveMeters">λg/4 of that arm at F: the electrical length between the input junction's and
/// an output junction's reference planes.</param>
/// <param name="RingRadiusMeters">Centre-line radius of each arm's half-turn; 0 when the ring has corners.</param>
/// <param name="FeedMeters">Length of the Z0 line from the input junction to pin 1 (and, with no output
/// transformer, from each output junction to pin 2 or 3).</param>
/// <param name="IsolationOhms">The resistor that belongs across R2-R3: 2·Z0 for the equal split.</param>
public sealed record WilkinsonDesign(
    double Z0, double FreqHz, double GapMeters,
    double W0Meters, double ArmZ0, double ArmWMeters, double ArmEeff, double QuarterWaveMeters,
    double RingRadiusMeters, double FeedMeters, double IsolationOhms, PassivePlan Plan)
{
    /// <summary>The amplitude split ratio: P3/P2 = K².</summary>
    public double K { get; init; } = 1.0;

    /// <summary>The number of quarter-wave sections, each with its own resistor.</summary>
    public int Sections { get; init; } = 1;

    /// <summary>f2/f1 of the design band (multi-section only).</summary>
    public double Band { get; init; } = WilkinsonPlan.DefaultBand;

    /// <summary>Every distinct quarter-wave line, as the readout lists them.</summary>
    public IReadOnlyList<WilkinsonLine> Lines { get; init; } = [];

    /// <summary>The isolation resistors, the one across R2-R3 first.</summary>
    public IReadOnlyList<WilkinsonResistor> Resistors { get; init; } = [];

    /// <summary>How the ring turns: half-turns, or corners.</summary>
    public PlanCorner Corner { get; init; }

    /// <summary>With corners, each arm's straight runs (one entry when the arms are alike); empty for half-turns.</summary>
    public IReadOnlyList<RingArm> Arms { get; init; } = [];
}

/// <summary>
/// The two-way Wilkinson divider (E. J. Wilkinson, IRE Trans. MTT, 1960; D. M. Pozar, Microwave Engineering, the
/// Wilkinson section) planned as one microstrip ring, with its two variants: an unequal split (L. I. Parad and
/// R. L. Moynihan, IEEE Trans. MTT, 1965) and a broadband multi-section divider (S. B. Cohn, IEEE Trans. MTT, 1968).
/// Pure: specs and a substrate in, <see cref="WilkinsonDesign"/> out. <see cref="MicrostripPlanModel"/> stamps the
/// plan and the PCell draws it, so the circuit and the copper are the same pieces.
///
/// <para><b>The ring.</b> An input tee (pin 1 on its branch) splits into two arms, each a straight run on the tee
/// and a 180° MCURVE, which come back facing each other across <c>Gap</c>. Each arm ends in an output tee whose
/// branch leaves outward to pin 2 or 3; the output tee's other through end is R2 or R3, the inner edge of the gap.
/// <c>Gap</c> is the span of the isolation resistor's pins, so an 0402 laid across R2-R3 lands with its pads on the
/// two ends and closes the ring. The resistor is NOT part of this part: it is an ordinary resistor on R2-R3, so EM
/// can cut it out as a component port and LVS and a BOM see it.</para>
///
/// <para><b>The numbers.</b> Widths come from MLIN's own model at F (its dispersive Z0), λg/4 from MLIN's own
/// dispersive εeff at F. An arm's length λg/4 is measured between the junctions' reference planes, which is
/// where <see cref="MicrostripTeeModel"/>'s junction network is referred: the input tee's arm line plus the curve's
/// effective length π·(R − 0.2·W), which fixes R. Every port pin sits one W0 of Z0 line past its junction, so a
/// port lands on a line and not in a junction's fringing field. All dimensions are held on
/// <see cref="PassivePlan.OnGrid"/>.</para>
///
/// <para><b>Unequal split, K ≠ 1.</b> The arms are Z0·√(K(1 + K²)) to port 2 and Z0·√((1 + K²)/K³) to port 3, which
/// leave port 2's end at K·Z0 and port 3's at Z0/K; a λg/4 transformer of Z0·√K or Z0/√K on each output tee's
/// branch brings it back to Z0, and the resistor is Z0·(K + 1/K). The two arms have different εeff, so the input
/// tee's two through lengths differ to make both λg/4 on one curve radius, and the gap sits off the input's axis.
/// At K = 1 the transformers are Z0 lines that change nothing, so the plan leaves them out and is the equal split's
/// to the last bit.</para>
///
/// <para><b>Corners.</b> With <see cref="PlanCorner.Miter"/> or <see cref="PlanCorner.Square"/> each arm is a rectangle's
/// half instead of a half-turn: it rises from the input tee, turns 90° (an MMITER at its usual chamfer, or MBEND's square
/// corner), runs across, turns again and falls to its output tee. Its λg/4 still counts every piece: the input tee's
/// line, the straight runs, and each corner's own electrical length at F, which <see cref="PlanSection.LineLength"/> takes
/// from the corner's model on the substrate, so the plan stays a pure function of the specs and the substrate. The runs
/// are that much shorter. The rectangle is as tall as the curved ring (the upper arm runs across at the half-turn's
/// top), so the arms stay a ring's diameter apart: the plan models no coupling between them. Both arms span the one
/// width between the tees; the arm needing more line rises and falls by half the difference on top, so the gap stays
/// on the input's axis even for K ≠ 1. The output tees, transformers and further
/// sections are the half-turns' own. At <see cref="PlanCorner.Curve"/> the plan is the half-turns' to the last bit.</para>
///
/// <para><b>Multi-section, N = 2 to 7.</b> The ring is the section next to the input; each further section is a
/// straight pair running on from the ring's output tees, the two lines parallel either side of the gap's centre
/// line, with a tee and a resistor's pin pair at every junction. Sections and resistors are numbered from the
/// outputs, as the 1968 paper numbers them: resistor 1 on R2-R3, resistor k on R2_k-R3_k. <see cref="BroadbandSections"/> gives the
/// impedances and resistors.</para>
/// </summary>
public static class WilkinsonPlan
{
    /// <summary>The most sections the plan builds: as many as Cohn's 1968 tables go to.</summary>
    public const int MaxSections = 7;

    /// <summary>The narrowest f2/f1 <see cref="ChebyshevSections"/> solves.</summary>
    public const double MinChebyshevBand = 1.1;

    /// <summary>f2/f1 when none is stated: an octave.</summary>
    public const double DefaultBand = 2.0;

    /// <summary>The equal-split divider's pins, in terminal order: the input, the two outputs, and the resistor's two
    /// ends.</summary>
    public static readonly IReadOnlyList<string> Pins = ["1", "2", "3", "R2", "R3"];

    /// <summary>The pins of a divider of <paramref name="sections"/> sections: the equal split's, then each further
    /// resistor's pair, R2_k and R3_k, k = 2 … N.</summary>
    public static IReadOnlyList<string> PinsFor(int sections)
        => sections <= 1 ? Pins
         : [.. Pins, .. Enumerable.Range(2, sections - 1).SelectMany(k => new[] { $"R2_{k}", $"R3_{k}" })];

    /// <summary>The section count a pin count belongs to (3 + 2·N pins); 1 for any count that is not one.</summary>
    public static int SectionsForPinCount(int pins)
        => pins > 5 && (pins - 3) % 2 == 0 && (pins - 3) / 2 <= MaxSections ? (pins - 3) / 2 : 1;

    public static WilkinsonDesign Build(double z0, double fHz, double gapMeters, MicrostripSubstrate s,
        double k = 1.0, double n = 1.0, double band = DefaultBand, PlanCorner corner = PlanCorner.Curve)
    {
        foreach (var (name, value) in new[] { ("Z0", z0), ("F", fHz), ("Gap", gapMeters), ("K", k) })
            if (!(value > 0))
                throw new ParameterRefusalException(Diagnostic.Create("device.wilkinson.spec-not-positive",
                    DiagnosticSeverity.Error, "Wilkinson divider: {name} = {value} must be positive.",
                    ("name", name), ("value", value)));
        if (!(n >= 1 && n <= MaxSections && n == Math.Round(n)))
            throw new ParameterRefusalException(Diagnostic.Create("device.wilkinson.sections-unsupported",
                DiagnosticSeverity.Error, "Wilkinson divider: N = {n} sections; the plan builds 1 to {max}.",
                ("n", n), ("max", MaxSections)));
        int sections = (int)n;
        if (sections > 1 && k != 1.0)
            throw new ParameterRefusalException(Diagnostic.Create("device.wilkinson.unequal-multisection",
                DiagnosticSeverity.Error,
                "Wilkinson divider: K = {k} with N = {n}. The multi-section design is the equal split only; set K = 1, "
              + "or N = 1 for an unequal split.", ("k", k), ("n", sections)));
        if (sections > 1 && !(band > 1.0))
            throw new ParameterRefusalException(Diagnostic.Create("device.wilkinson.band-not-above-one",
                DiagnosticSeverity.Error,
                "Wilkinson divider: Band = {band} is f2/f1, the band's upper edge over its lower, and must be above 1.",
                ("band", band)));

        // The lines' impedances. Equal split: arms of √2·Z0 and 2·Z0 across them. K: the unequal split's arms, output
        // transformers and resistor. N: the broadband sections and resistors, numbered from the outputs.
        bool unequal = k != 1.0;
        double z2 = z0 * Math.Sqrt(k * (1.0 + k * k)), z3 = z0 * Math.Sqrt((1.0 + k * k) / (k * k * k));
        double[] zs = [z2], rs = [z0 * (k + 1.0 / k)];
        if (sections > 1)
        {
            var (zn, rn) = BroadbandSections(sections, band);
            zs = [.. zn.Select(z => z * z0)];
            rs = [.. rn.Select(r => r * z0)];
            z2 = z3 = zs[^1];
        }

        double feed = PassivePlan.OnGrid(Width(z0, fHz, s, 1.0));
        double w0 = feed;
        var arm2 = Quarter(unequal ? "arm to 2" : sections > 1 ? $"section {sections} (the ring)" : "arms", z2, fHz, s, k);
        var arm3 = unequal ? Quarter("arm to 3", z3, fHz, s, k) : arm2;
        double wa2 = arm2.WMeters, wa3 = arm3.WMeters;
        double gap = PassivePlan.OnGrid(gapMeters);
        var lines = new List<WilkinsonLine>();
        var ladder = new WilkinsonLine[sections];                  // ladder[m] is section m; [sections] is the ring
        for (int m = 1; m < sections; m++) lines.Add(ladder[m] = Quarter($"section {m}", zs[m - 1], fHz, s, k));
        lines.Add(arm2);
        if (unequal) lines.Add(arm3);
        WilkinsonLine? t2 = null, t3 = null;
        if (unequal)
        {
            lines.Add(t2 = Quarter("transformer at 2", z0 * Math.Sqrt(k), fHz, s, k));
            lines.Add(t3 = Quarter("transformer at 3", z0 / Math.Sqrt(k), fHz, s, k));
        }

        // The ring's output tees: the branch's width and length, and the through line either side of the branch: lr
        // towards the resistor's pin, lv towards the arm that arrives. With no further section the branch is the
        // output (a W0 feed, or the transformer); with one it is the next section, and every resistor pin sits one
        // half of the widest line beyond the gap's edge from the pair's centre line.
        double axis = sections > 1 ? Math.Max(w0, ladder.Skip(1).Max(l => l!.WMeters)) / 2.0 : 0.0;
        (double Wb, double L3, double Lr, double Lv) Junction(WilkinsonLine arm, WilkinsonLine? transformer) =>
            sections > 1 ? (ladder[sections - 1].WMeters, PassivePlan.OnGrid(arm.WMeters / 2.0 + ladder[sections - 1].QuarterWaveMeters),
                            axis, ladder[sections - 1].WMeters / 2.0)
          : transformer is { } t ? (t.WMeters, PassivePlan.OnGrid(arm.WMeters / 2.0 + t.QuarterWaveMeters), t.WMeters / 2.0, t.WMeters / 2.0)
          : (w0, feed + arm.WMeters / 2.0, w0 / 2.0, w0 / 2.0);
        var j2 = Junction(arm2, t2);
        var j3 = Junction(arm3, t3);

        // The arms. Each ends where its output tee's through line ends (pos above the gap's centre); the two must be
        // λg/4 on one curve radius, so the input tee's two through lengths differ by d — nothing, for equal arms —
        // and the gap's centre sits at gy, below the input's axis by half of what that leaves over.
        double posT = gap / 2.0 + (j2.Lr + j2.Lv), posB = gap / 2.0 + (j3.Lr + j3.Lv);
        const double c = MicrostripCurve.EffectiveRadiusOffsetPerWidth;
        double d = (arm2.QuarterWaveMeters - arm3.QuarterWaveMeters) - Math.PI * c * (wa2 - wa3);
        bool curved = corner == PlanCorner.Curve;
        double yc = curved ? PassivePlan.OnGrid((posT - posB - d) / 2.0) : 0.0;
        double a = posT - yc, b = posB + yc, gy = -yc;
        if (Math.Min(a, b) < w0 / 2.0)
            throw new ParameterRefusalException(Diagnostic.Create("device.wilkinson.split-unrealisable",
                DiagnosticSeverity.Error,
                "Wilkinson divider: at K = {k} the two arms' quarter waves differ by more than the ring can take up "
              + "on this board. Choose K nearer 1, or widen Gap.", ("k", k)));

        double radius = 0.0, across = 0.0;
        (double Rise, double Across, double Corner, double Arm, double? M) upper = default, lower = default;
        if (curved)
        {
            double straight = a - w0 / 2.0;             // the line the port-2 arm carries before its curve
            radius = PassivePlan.OnGrid(
                (arm2.QuarterWaveMeters - straight) / Math.PI - c * wa2);
            if (radius < Math.Max(wa2, wa3))
                throw new ParameterRefusalException(Diagnostic.Create("device.wilkinson.ring-too-small",
                    DiagnosticSeverity.Error,
                    "Wilkinson divider: a quarter wave at F = {f} Hz is {quarter} m, which leaves a ring radius of {radius} m, "
                  + "under the arm's own width {w} m. Lower F, or narrow Gap.",
                    ("f", fHz), ("quarter", arm2.QuarterWaveMeters), ("radius", radius), ("w", Math.Max(wa2, wa3))));
        }
        else
        {
            // Each arm closes at the width X = λg/4 − (the tee's line) − 2·(a corner's length) + 2·Arm between the tees
            // when it rises by nothing. The rectangle keeps the curved ring's height: the upper arm runs across where the
            // half-turn's top is, so the two arms are a ring's diameter apart rather than a gap's, since the plan models no
            // coupling between them. Both arms rise by that, span the narrower width it leaves, and the other arm rises
            // and falls by half the difference on top.
            (double X, double Corner, double Arm, double? M) Closing(WilkinsonLine arm, double pos)
            {
                var bend = new PlanBend([], 0.0, 0.0, 0.0, arm.WMeters, -90.0, PlanBend.MiterFor(corner, arm.WMeters, s));
                double length = bend.LineLength(s, fHz, arm.Z, arm.QuarterWaveMeters);
                return (arm.QuarterWaveMeters - (pos - w0 / 2.0) - 2.0 * length + 2.0 * bend.Arm, length, bend.Arm, bend.M);
            }
            var (t, u) = (Closing(arm2, a), Closing(arm3, b));
            // The curved plan of the same specs, whose refusals therefore hold here too.
            var ring = Build(z0, fHz, gapMeters, s, k, n, band);
            double crest = ((PlanTee)ring.Plan.Sections[0]).L1 + ring.RingRadiusMeters;   // the half-turn's top, centre line
            double rise = Math.Max(0.0, PassivePlan.OnGrid(crest - a - t.Arm));
            double flat = PassivePlan.OnGrid(Math.Min(t.X, u.X));
            across = flat - 2.0 * rise;
            upper = (rise + PassivePlan.OnGrid((t.X - flat) / 2.0), across - 2.0 * t.Arm, t.Corner, t.Arm, t.M);
            lower = (rise + PassivePlan.OnGrid((u.X - flat) / 2.0), across - 2.0 * u.Arm, u.Corner, u.Arm, u.M);
            if (Math.Min(upper.Across, lower.Across) <= 0.0)
                throw new ParameterRefusalException(Diagnostic.Create("device.wilkinson.ring-too-small",
                    DiagnosticSeverity.Error,
                    "Wilkinson divider: a quarter wave at F = {f} Hz is {quarter} m, which leaves no straight run between "
                  + "the ring's corners ({run} m). Lower F, or narrow Gap.",
                    ("f", fHz), ("quarter", arm2.QuarterWaveMeters), ("run", Math.Min(upper.Across, lower.Across))));
        }

        double l3 = feed + Math.Max(wa2, wa3) / 2.0;    // the input tee's branch: half the wider arm, then the feed
        double x1 = l3, x2 = curved ? x1 + 2.0 * radius : x1 + across;
        // Nodes: 1..P the pins (1, 2, 3, R2, R3, then R2_k/R3_k); P+1/P+2 the input tee's top/bottom arm ends; P+3/P+4
        // the arms' far ends; then the ring's output tees' outgoing ends (N > 1), the corners' joints, and each further
        // section's start, top and bottom.
        var pins = PinsFor(sections);
        int p = pins.Count, next = p + 5;
        int RTop(int m) => 4 + 2 * (m - 1);
        int RBottom(int m) => 5 + 2 * (m - 1);
        int out2 = sections > 1 ? next++ : 2, out3 = sections > 1 ? next++ : 3;
        var plan = new List<PlanSection> { new PlanTee(wa2, wa3, w0, a, b, l3, [p + 1, p + 2, 1], x1, a, 270.0) };
        if (curved)
        {
            plan.Add(new PlanCurve(wa2, -180.0, radius, [p + 1, p + 3], x1, a, 90.0));
            plan.Add(new PlanCurve(wa3, 180.0, radius, [p + 2, p + 4], x1, -b, 270.0));
        }
        else
        {
            // Up (or down) from the input tee, a corner, across, a corner, and down (or up) to the output tee; a rise of
            // nothing is no line at all.
            void Arm(int from, int to, double y0, double sign, double w, (double Rise, double Across, double Corner,
                double Arm, double? M) arm)
            {
                double up = sign > 0 ? 90.0 : 270.0, turn = -90.0 * sign, v = arm.Rise, r = arm.Arm;
                int rise = v > 0.0 ? next++ : from, c1 = next++, c2 = next++, fall = v > 0.0 ? next++ : to;
                if (v > 0.0) plan.Add(new PlanLine([from, rise], x1, y0, up, w, v));
                plan.Add(new PlanBend([rise, c1], x1, y0 + sign * v, up, w, turn, arm.M));
                plan.Add(new PlanLine([c1, c2], x1 + r, y0 + sign * (v + r), 0.0, w, arm.Across));
                plan.Add(new PlanBend([c2, fall], x1 + r + arm.Across, y0 + sign * (v + r), 0.0, w, turn, arm.M));
                if (v > 0.0) plan.Add(new PlanLine([fall, to], x2, y0 + sign * v, (up + 180.0) % 360.0, w, v));
            }
            Arm(p + 1, p + 3, a, 1.0, wa2, upper);
            Arm(p + 2, p + 4, -b, -1.0, wa3, lower);
        }
        // The ring's output tees, their through lines spanning the branch: the arm's end and the resistor's pin are the
        // junction's own reference planes when lr = lv = half the branch's width.
        plan.Add(new PlanTee(wa2, wa2, j2.Wb, j2.Lr, j2.Lv, j2.L3, [RTop(sections), p + 3, out2], x2, gy + gap / 2.0, 90.0));
        plan.Add(new PlanTee(wa3, wa3, j3.Wb, j3.Lv, j3.Lr, j3.L3, [p + 4, RBottom(sections), out3], x2, -b, 90.0));

        // The further sections, from the ring outwards: section m runs on the previous junction's outgoing arm, and
        // ends in a tee whose branch reaches the gap's edge, R2_m (R2 for m = 1) above and R3_m below. The tee below
        // is the one above turned half round, so its through runs the other way.
        double bx = x2 + j2.L3, yTop = gy + gap / 2.0 + axis, yBottom = gy - gap / 2.0 - axis;
        for (int m = sections - 1; m >= 1; m--)
        {
            double wIn = ladder[m].WMeters, wOut = m > 1 ? ladder[m - 1].WMeters : w0;
            double l1 = wIn / 2.0, l2 = PassivePlan.OnGrid(wIn / 2.0 + (m > 1 ? ladder[m - 1].QuarterWaveMeters : feed));
            int top = m > 1 ? next++ : 2, bottom = m > 1 ? next++ : 3;
            plan.Add(new PlanTee(wIn, wOut, wIn, l1, l2, axis, [out2, top, RTop(m)], bx, yTop, 0.0));
            plan.Add(new PlanTee(wOut, wIn, wIn, l2, l1, axis, [bottom, out3, RBottom(m)], bx + l1 + l2, yBottom, 180.0));
            bx += l1 + l2;
            out2 = top;
            out3 = bottom;
        }

        var resistors = rs.Select((r, i) => new WilkinsonResistor(pins[3 + 2 * i], pins[4 + 2 * i], r)).ToList();
        return new WilkinsonDesign(z0, fHz, gap, w0, z2, wa2, arm2.Eeff, arm2.QuarterWaveMeters, radius, feed, rs[0],
            new PassivePlan(pins, plan, NodeCount: next - 1)
            {
                // Every resistor's pins are Gap apart across the gap's centre line, so each site spans Gap.
                Sites = [.. resistors.Select(r => new PlanSite(PlanSiteRole.R, PlanSiteKind.Series, r.PinA, r.PinB, gap, r.Ohms))],
            })
        {
            K = k, Sections = sections, Band = band, Lines = lines, Resistors = resistors, Corner = corner,
            Arms = curved ? [] : unequal
                ? [new(arm2.Name, upper.Rise, upper.Across, upper.Corner), new(arm3.Name, lower.Rise, lower.Across, lower.Corner)]
                : [new(arm2.Name, upper.Rise, upper.Across, upper.Corner)],
        };
    }

    /// <summary>
    /// The broadband divider of <paramref name="sections"/> = 2 to 7 sections (S. B. Cohn, IEEE Trans. MTT, 1968),
    /// normalised to Z0 = 1: the impedances and the resistors, both from the outputs inward. Above 3 it is
    /// <see cref="ChebyshevSections"/>, the same two conditions solved for any N; 2 and 3 keep the bisection they were
    /// first planned with, so their numbers stay bit for bit.
    ///
    /// <para><b>Even mode</b> (both outputs driven alike; no current in any resistor): each half is a cascade of quarter
    /// waves from the input's half, 2, to an output, 1. It is made an equal-ripple (Chebyshev) transformer over the band
    /// f1..f2 centred on F, symmetric so that z_k·z_{N+1−k} = 2: its reflection is zero where cos θ = cos θm · x_j, x_j
    /// the zeros of the N-th Chebyshev polynomial and θm = π/(1 + f2/f1) the band edge's electrical length. With the
    /// symmetry there is one unknown, z_1, found by bisection where the reflection's real part crosses zero at the first
    /// zero.</para>
    ///
    /// <para><b>Odd mode</b> (outputs driven in antiphase; the input junction a virtual ground and every resistor
    /// halved to ground): each half is a short, then the sections from the input end with a conductance 2/R_k at every
    /// junction. The resistors make the output's admittance exactly 1 at the even mode's own zeros — at θ1, which by the
    /// lines' symmetry also holds at π − θ1, and for N = 3 at the centre — so at those frequencies every port is matched
    /// and the outputs are isolated. N equations in the N conductances, solved by Newton's method.</para>
    /// </summary>
    public static (double[] Z, double[] R) BroadbandSections(int sections, double band)
    {
        if (sections > 3) return ChebyshevSections(sections, band);
        double thetaM = Math.PI / (1.0 + band);
        double theta1 = Math.Acos(Math.Cos(thetaM) * (sections == 2 ? Math.Sqrt(0.5) : Math.Sqrt(3.0) / 2.0));
        double[] Even(double z1) => sections == 2 ? [z1, 2.0 / z1] : [z1, Math.Sqrt(2.0), 2.0 / z1];

        double lo = 1.0, hi = Math.Sqrt(2.0);
        for (int i = 0; i < 200; i++)
        {
            double mid = 0.5 * (lo + hi);
            var zin = InputImpedance(Even(mid), theta1);
            if (((zin - 2.0) / (zin + 2.0)).Real > 0.0) lo = mid; else hi = mid;
        }
        double[] z = Even(0.5 * (lo + hi));

        double[] Residual(double[] g)
        {
            var y = OddAdmittance(z, g, theta1) - 1.0;
            return sections == 2 ? [y.Real, y.Imaginary] : [y.Real, y.Imaginary, (OddAdmittance(z, g, Math.PI / 2.0) - 1.0).Real];
        }
        double[] gs = [.. Enumerable.Repeat(1.0, sections)];
        for (int iteration = 0; iteration < 50; iteration++)
        {
            var f = Residual(gs);
            var jac = new double[sections, sections];
            for (int j = 0; j < sections; j++)
            {
                double[] step = [.. gs];
                step[j] += 1e-7;
                var fj = Residual(step);
                for (int i = 0; i < sections; i++) jac[i, j] = (fj[i] - f[i]) / 1e-7;
            }
            var dx = Solve(jac, [.. f.Select(v => -v)]);
            for (int i = 0; i < sections; i++) gs[i] += dx[i];
            if (dx.Max(Math.Abs) < 1e-14) break;
        }
        return (z, [.. gs.Select(g => 2.0 / g)]);
    }

    /// <summary>
    /// The same divider for any N ≥ 2, by the same two conditions as <see cref="BroadbandSections"/>. Even mode: the
    /// ⌊N/2⌋ impedances next to the output (the rest follow from z_k·z_(N+1−k) = 2, and an odd N's middle one is √2) by
    /// Gauss–Newton on the reflection at the ⌊N/2⌋ Chebyshev zeros below π/2, cos θ_j = cos θm · cos((2j − 1)π/(2N));
    /// with the symmetry a zero there is one real condition, and the reflection then has every zero of the N-th
    /// Chebyshev polynomial, so the transformer is equal-ripple over the band. Odd mode: the N conductances by Newton's
    /// method, the half's admittance exactly 1 at those zeros (and at π/2 for an odd N).
    ///
    /// <para>Both are continued from f2/f1 = 10, where they converge from a plain start (impedances evenly spaced in
    /// log, every resistor 4), to the band asked for in steps of 5 %, each step starting from the last one's answer: a
    /// direct start fails for N ≥ 4 in narrow bands, where the zeros crowd together near π/2. Below f2/f1 = 1.1 the
    /// odd mode's equations become dependent (seven sections lose the solution at 1.05), so a narrower band is refused,
    /// as is any the solve does not reach or that would need a resistor that is not positive.</para>
    /// </summary>
    public static (double[] Z, double[] R) ChebyshevSections(int sections, double band)
    {
        if (!(band >= MinChebyshevBand))
            throw new ParameterRefusalException(Diagnostic.Create("device.wilkinson.sections-unsolved",
                DiagnosticSeverity.Error,
                "Wilkinson divider: Band = {band} is narrower than {n} sections are solved for (f2/f1 of {min} and up). "
              + "Widen Band, or use fewer sections.", ("band", band), ("n", sections), ("min", MinChebyshevBand)));
        int half = sections / 2;
        double[] u = [.. Enumerable.Range(0, half).Select(k => Math.Log(2.0) * (k + 0.5) / sections)];
        double[] g = [.. Enumerable.Repeat(0.5, sections)];
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(Math.Log(band / 10.0)) / 0.05));
        double residual = double.NaN;
        double[] z = [];
        for (int step = 0; step <= steps; step++)
        {
            double at = step == 0 ? 10.0 : 10.0 * Math.Pow(band / 10.0, (double)step / steps);
            double thetaM = Math.PI / (1.0 + at);
            double[] zeros = [.. Enumerable.Range(1, half)
                .Select(j => Math.Acos(Math.Cos(thetaM) * Math.Cos((2 * j - 1) * Math.PI / (2.0 * sections))))];
            double[] Even(double[] v) => [.. v.Select(Math.Exp), .. sections % 2 == 1 ? new[] { Math.Sqrt(2.0) } : [],
                                          .. v.Reverse().Select(x => 2.0 / Math.Exp(x))];
            double[] Mismatch(double[] v) => [.. zeros.SelectMany(t =>
            {
                var zin = InputImpedance(Even(v), t);
                var r = (zin - 2.0) / (zin + 2.0);
                return new[] { r.Real, r.Imaginary };
            })];
            u = GaussNewton(Mismatch, u);
            z = Even(u);
            var zf = z;
            double[] Odd(double[] c) => [.. zeros.SelectMany(t =>
            {
                var y = OddAdmittance(zf, c, t) - 1.0;
                return new[] { y.Real, y.Imaginary };
            }), .. sections % 2 == 1 ? new[] { (OddAdmittance(zf, c, Math.PI / 2.0) - 1.0).Real } : []];
            g = GaussNewton(Odd, g);
            residual = Math.Max(Mismatch(u).Max(Math.Abs), Odd(g).Max(Math.Abs));
        }
        if (!(residual < 1e-9) || g.Any(x => !(x > 0.0)))
            throw new ParameterRefusalException(Diagnostic.Create("device.wilkinson.sections-unsolved",
                DiagnosticSeverity.Error,
                "Wilkinson divider: {n} sections over Band = {band} have no solution with positive resistors. Widen Band, "
              + "or use fewer sections.", ("n", sections), ("band", band)));
        return (z, [.. g.Select(x => 2.0 / x)]);
    }

    /// <summary>Newton's method from <paramref name="x"/> on <paramref name="f"/>'s residuals, with a forward-difference
    /// Jacobian; Gauss–Newton when there are more residuals than unknowns and they are consistent.</summary>
    private static double[] GaussNewton(Func<double[], double[]> f, double[] x)
    {
        x = [.. x];
        int n = x.Length;
        for (int iteration = 0; iteration < 50; iteration++)
        {
            var r = f(x);
            var jac = new double[r.Length, n];
            for (int j = 0; j < n; j++)
            {
                double[] step = [.. x];
                step[j] += 1e-7;
                var rj = f(step);
                for (int i = 0; i < r.Length; i++) jac[i, j] = (rj[i] - r[i]) / 1e-7;
            }
            double[] dx;
            if (r.Length == n) dx = Solve(jac, [.. r.Select(v => -v)]);
            else
            {
                // The normal equations: only for the even mode's few unknowns, whose condition squaring them can take.
                var normal = new double[n, n];
                var rhs = new double[n];
                for (int a = 0; a < n; a++)
                {
                    for (int b = 0; b < n; b++)
                        for (int i = 0; i < r.Length; i++) normal[a, b] += jac[i, a] * jac[i, b];
                    for (int i = 0; i < r.Length; i++) rhs[a] -= jac[i, a] * r[i];
                }
                dx = Solve(normal, rhs);
            }
            for (int i = 0; i < n; i++) x[i] += dx[i];
            if (dx.Max(Math.Abs) < 1e-13) break;
        }
        return x;
    }

    /// <summary>The impedance at the input end of quarter-wave-at-F sections listed from the load (1) inward, each
    /// θ long.</summary>
    private static Complex InputImpedance(double[] fromLoad, double theta)
    {
        Complex zl = 1.0, j = Complex.ImaginaryOne;
        double t = Math.Tan(theta);
        foreach (double z in fromLoad) zl = z * (zl + j * z * t) / (z + j * zl * t);
        return zl;
    }

    /// <summary>The odd-mode half's admittance at the output: a short at the input end, then the sections from the input
    /// outward (<paramref name="fromOutputs"/> reversed), each followed by its conductance (<paramref name="g"/>, also
    /// from the outputs).</summary>
    private static Complex OddAdmittance(double[] fromOutputs, double[] g, double theta)
    {
        var j = Complex.ImaginaryOne;
        Complex? y = null;
        double t = Math.Tan(theta);
        for (int k = fromOutputs.Length - 1; k >= 0; k--)
        {
            double y0 = 1.0 / fromOutputs[k];
            y = y is { } v ? y0 * (v + j * y0 * t) / (y0 + j * v * t) : -j * y0 / t;
            y += g[k];
        }
        return y!.Value;
    }

    /// <summary>Gaussian elimination with partial pivoting, for the Newton steps (up to 7×7).</summary>
    private static double[] Solve(double[,] a, double[] rhs)
    {
        int n = rhs.Length;
        for (int col = 0; col < n; col++)
        {
            int pivot = Enumerable.Range(col, n - col).MaxBy(r => Math.Abs(a[r, col]));
            for (int q = 0; q < n; q++) (a[col, q], a[pivot, q]) = (a[pivot, q], a[col, q]);
            (rhs[col], rhs[pivot]) = (rhs[pivot], rhs[col]);
            for (int r = col + 1; r < n; r++)
            {
                double factor = a[r, col] / a[col, col];
                for (int q = col; q < n; q++) a[r, q] -= factor * a[col, q];
                rhs[r] -= factor * rhs[col];
            }
        }
        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double sum = rhs[r];
            for (int q = r + 1; q < n; q++) sum -= a[r, q] * x[q];
            x[r] = sum / a[r, r];
        }
        return x;
    }

    /// <summary>A quarter-wave line of impedance <paramref name="z"/>: MLIN's width at F on the 2 nm grid, its εeff
    /// there, and λg/4.</summary>
    private static WilkinsonLine Quarter(string name, double z, double fHz, MicrostripSubstrate s, double k)
    {
        double w = PassivePlan.OnGrid(Width(z, fHz, s, k));
        double eeff = Line(w, s).LineParameters(fHz).Eeff;
        return new WilkinsonLine(name, z, w, eeff, PlanarLineAngle.LengthMeters(eeff, Math.PI / 2.0, fHz));
    }

    private static MicrostripLineModel Line(double w, MicrostripSubstrate s)
        => new(w, 1.0, s.HMeters, s.TMeters, s.EpsR, s.SigmaSPerM, s.TanD, "(Wilkinson plan)", s.RoughnessMeters);

    /// <summary>The width whose MLIN Z0 at F is <paramref name="z"/>; a Z0 no width reaches is refused — as a split
    /// the board cannot make when K ≠ 1 asked for it.</summary>
    private static double Width(double z, double fHz, MicrostripSubstrate s, double k)
    {
        var reporter = new MicrostripValidityReporter("Wilkinson divider");
        double w = Planar.PlanarLineSynthesis.SynthesizeWidth(x => Line(x, s).LineParameters(fHz).Z0, z, s.HMeters,
            "MLIN", reporter);
        if (reporter.Drain() is [var unreachable, ..])
            throw new ParameterRefusalException(k != 1.0
                ? Diagnostic.Create("device.wilkinson.split-unrealisable", DiagnosticSeverity.Error,
                    "Wilkinson divider: K = {k} needs a {z} Ω line, which no width reaches on this board ({message}). "
                  + "Choose K nearer 1.", ("k", k), ("z", Math.Round(z, 1)), ("message", unreachable.Message))
                : Diagnostic.Create("device.wilkinson.width-unreachable",
                    DiagnosticSeverity.Error, "{message}", ("message", unreachable.Message)));
        return w;
    }
}
