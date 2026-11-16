using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>What stands in for each quarter-wave arm of a lumped Wilkinson divider: a low-pass π (C, L, C) or T (L, C, L).</summary>
public enum LumpedArm { Pi, Tee }

/// <summary>The resistive pad's forms (MRPAD): the matched π, T and bridged-T attenuators, and the matched three-port star (Wye)
/// and delta splitters.</summary>
public enum ResistivePadForm { Pi, T, BridgedT, Wye, Delta }

/// <summary>One junction of a lumped plan's interconnect, at grid cell (<paramref name="Col"/>, <paramref name="Row"/>), row 0 at
/// the top, with the pins its outward arms end on: a direction (<see cref="LumpedPlan.E"/>, N, W, S) and a pin each.</summary>
public sealed record LumpedJunction(int Col, int Row, params (int Dir, string Pin)[] Out);

/// <summary>Two neighbouring junctions joined by a series site, <paramref name="PinA"/> on <paramref name="From"/>'s arm and
/// <paramref name="PinB"/> on <paramref name="To"/>'s; with neither pin, by a line, the two being one piece of copper.</summary>
public sealed record LumpedEdge(int From, int To, string? PinA = null, string? PinB = null);

/// <summary>A lumped network's layout: the part's engine token, a word for its symbol, the pins in terminal order (the ports,
/// then each series site's two pins, then the pins to ground), the junctions and the edges joining them.</summary>
public sealed record LumpedTopology(string Part, string Label, IReadOnlyList<string> Pins, IReadOnlyList<LumpedJunction> Junctions,
    IReadOnlyList<LumpedEdge> Edges);

/// <summary>What a lumped realisation comes out to on one substrate: its layout, the interconnect's width (the Z0 line's), each
/// arm's length from its junction's centre to its pin, the sites' span, and the plan, whose sites carry the values.</summary>
public sealed record LumpedDesign(LumpedTopology Topology, double WMeters, double ArmMeters, double SpanMeters, PassivePlan Plan);

/// <summary>
/// The lumped realisations of the spec-driven passives (docs/design/rf-passives.md, "Lumped realisations"), and the resistive
/// pad, which has no other. Every lumped element is a site (<see cref="PlanSite"/>): a pin pair, or a pin to ground, filled by an
/// ordinary R, L or C, an SMT part on a board and an ideal or a TFR, MIMCAP or SPIRAL part on a die. The part's own copper is
/// only the pads' interconnect, and its model is that copper.
///
/// <para><b>The values</b> are the closed forms at F. A quarter-wave line of Zc equals, at F only, a low-pass π of a series
/// L = Zc/ω between two shunt C = 1/(ω·Zc), a low-pass T of two L = Zc/ω about one C = 1/(ω·Zc), and (as a 3λ/4 line, −270°
/// ≡ +90°) the high-pass π of a series C = 1/(ω·Zc) between two shunt L = Zc/ω (D. M. Pozar, Microwave Engineering, a
/// textbook, no DOI). Shunt elements meeting at one node add. Wilkinson: each √2·Z0 arm a π or a T, and 2·Z0 across the
/// outputs. Branch-line: each arm a π (R. W. Vogel, IEEE Trans. MTT, 1992,
/// <see href="https://doi.org/10.1109/22.120097">10.1109/22.120097</see>). Rat-race: three π for the λ/4 spacings and the
/// high-pass π for the 3λ/4 one; at ports 2 and 4 a π's shunt C meets the high-pass π's shunt L, which resonate at F and are
/// left out. Coupled-line balun, as a lattice (W. Bakalski et al., IEEE MTT-S 2002,
/// <see href="https://doi.org/10.1109/MWSYM.2002.1011595">10.1109/MWSYM.2002.1011595</see>): L from 1 to 2 and C to ground at 2,
/// C from 1 to 3 and L to ground at 3, with √(L/C) = √(2·Z0·ZL) and ω²·L·C = 1, so the outputs are equal and in antiphase
/// for any equal loads and the input is matched with ZL on each. The resistive pad's forms are the textbook matched pads
/// and splitters (<see cref="ResistivePad"/>).</para>
///
/// <para><b>The layout.</b> Junctions on a square grid, each of the Z0 line's width W and each arm W from the junction's
/// centre to its pin: a junction with four arms is an MCROSS, three an MTEE, two in line an MLIN and two at a right angle an
/// MBEND's square corner with MLIN up to the arms' ends. Neighbouring junctions are 2·W + span apart, so the two pins of
/// every series site face each other across the site's span (<c>Gap</c>, or the <c>Footprint</c>'s land span), as MWPD's
/// R2-R3 do; a pin to ground is an outward arm, as MCBL's G2 and G3 are. Every dimension is on <see cref="PassivePlan.OnGrid"/>.
/// The interconnect is not absorbed into the values: it is a few W of line per site, small against the parts below about a
/// tenth of the line's quarter-wave frequency, and the model has it.</para>
/// </summary>
public static class LumpedPlan
{
    /// <summary>The grid's directions: +x, +y (up), −x, −y.</summary>
    public const int E = 0, N = 1, W = 2, S = 3;

    private static readonly int[] Dx = [1, 0, -1, 0], Dy = [0, 1, 0, -1];

    /// <summary>Wilkinson, π arms. Junctions: the input's two corners (1 and G1, joined by a line), output 2 (with G2) and
    /// output 3 (with G3); L1 and L2 the arms, R2-R3 the resistor.</summary>
    public static readonly LumpedTopology WilkinsonPi = new("MWPD", "π",
        ["1", "2", "3", "R2", "R3", "L1a", "L1b", "L2a", "L2b", "G1", "G2", "G3"],
        [new(0, 0, (W, "1")), new(1, 0, (E, "2"), (N, "G2")), new(1, 1, (E, "3"), (S, "G3")), new(0, 1, (W, "G1"))],
        [new(0, 1, "L1a", "L1b"), new(1, 2, "R2", "R3"), new(3, 2, "L2a", "L2b"), new(0, 3)]);

    /// <summary>Wilkinson, T arms: the input at the top left, each arm two L about a C to ground (G1, G2), the outputs at the
    /// right, R2-R3 between them.</summary>
    public static readonly LumpedTopology WilkinsonTee = new("MWPD", "T",
        ["1", "2", "3", "R2", "R3", "L1a", "L1b", "L2a", "L2b", "L3a", "L3b", "L4a", "L4b", "G1", "G2"],
        [new(0, 0, (W, "1")), new(1, 0, (N, "G1")), new(2, 0, (E, "2")), new(2, 1, (E, "3")), new(1, 1, (S, "G2")), new(0, 1)],
        [new(0, 1, "L1a", "L1b"), new(1, 2, "L2a", "L2b"), new(2, 3, "R2", "R3"), new(5, 4, "L3a", "L3b"),
         new(4, 3, "L4a", "L4b"), new(0, 5)]);

    /// <summary>Branch-line: the ports at the corners as MBLC's (1 and 2 the upper line, 4 and 3 the lower), a C to ground at
    /// each, L1 and L3 the series arms, L2 and L4 the shunt arms.</summary>
    public static readonly LumpedTopology BranchLineLayout = new("MBLC", "90°",
        ["1", "2", "3", "4", "L1a", "L1b", "L2a", "L2b", "L3a", "L3b", "L4a", "L4b", "G1", "G2", "G3", "G4"],
        [new(0, 0, (W, "1"), (N, "G1")), new(1, 0, (E, "2"), (N, "G2")), new(1, 1, (E, "3"), (S, "G3")),
         new(0, 1, (W, "4"), (S, "G4"))],
        [new(0, 1, "L1a", "L1b"), new(1, 2, "L2a", "L2b"), new(3, 2, "L3a", "L3b"), new(0, 3, "L4a", "L4b")]);

    /// <summary>Rat-race: the ring 2, 1, 3, 4 clockwise from the top left; L1-L3 the λ/4 spacings, C1 the 3λ/4 one, and a C to
    /// ground at 1 and at 3.</summary>
    public static readonly LumpedTopology RatRaceLayout = new("MRRC", "180°",
        ["1", "2", "3", "4", "L1a", "L1b", "L2a", "L2b", "L3a", "L3b", "C1a", "C1b", "G1", "G3"],
        [new(0, 0, (W, "2")), new(1, 0, (E, "1"), (N, "G1")), new(1, 1, (E, "3"), (S, "G3")), new(0, 1, (W, "4"))],
        [new(0, 1, "L1a", "L1b"), new(1, 2, "L2a", "L2b"), new(3, 2, "L3a", "L3b"), new(3, 0, "C1a", "C1b")]);

    /// <summary>Coupled-line balun as a lattice: output 2, the input and output 3 in a row; L1 from 1 to 2, C1 from 1 to 3, and
    /// G2 and G3 to ground beside the outputs.</summary>
    public static readonly LumpedTopology LatticeBalunLayout = new("MCBL", "balun",
        ["1", "2", "3", "L1a", "L1b", "C1a", "C1b", "G2", "G3"],
        [new(0, 0, (W, "2"), (S, "G2")), new(1, 0, (N, "1")), new(2, 0, (E, "3"), (S, "G3"))],
        [new(1, 0, "L1a", "L1b"), new(1, 2, "C1a", "C1b")]);

    /// <summary>The resistive pad's forms. Their shunt resistors sit on a terminal: the π's on 1 and 2, the T's and bridged-T's
    /// on 3, the middle node; a splitter's 3 is its third port. So the bridged-T and the two splitters share one pin list.</summary>
    public static readonly LumpedTopology PadPi = new("MRPAD", "R",
        ["1", "2", "R1a", "R1b"],
        [new(0, 0, (W, "1")), new(1, 0, (E, "2"))],
        [new(0, 1, "R1a", "R1b")]);

    public static readonly LumpedTopology PadT = new("MRPAD", "R",
        ["1", "2", "3", "R1a", "R1b", "R2a", "R2b"],
        [new(0, 0, (W, "1")), new(1, 0, (S, "3")), new(2, 0, (E, "2"))],
        [new(0, 1, "R1a", "R1b"), new(1, 2, "R2a", "R2b")]);

    public static readonly LumpedTopology PadBridgedT = new("MRPAD", "R",
        ["1", "2", "3", "R1a", "R1b", "R2a", "R2b", "R3a", "R3b"],
        [new(0, 0, (W, "1")), new(1, 0, (E, "2")), new(1, 1), new(0, 1, (S, "3"))],
        [new(0, 3, "R1a", "R1b"), new(2, 1, "R2a", "R2b"), new(0, 1, "R3a", "R3b"), new(3, 2)]);

    public static readonly LumpedTopology PadWye = new("MRPAD", "R",
        ["1", "2", "3", "R1a", "R1b", "R2a", "R2b", "R3a", "R3b"],
        [new(0, 0, (W, "1")), new(1, 0), new(2, 0, (E, "2")), new(1, 1, (S, "3"))],
        [new(0, 1, "R1a", "R1b"), new(2, 1, "R2a", "R2b"), new(3, 1, "R3a", "R3b")]);

    public static readonly LumpedTopology PadDelta = new("MRPAD", "R",
        ["1", "2", "3", "R1a", "R1b", "R2a", "R2b", "R3a", "R3b"],
        [new(0, 0, (W, "1")), new(1, 0, (E, "2")), new(1, 1, (E, "3")), new(0, 1)],
        [new(0, 1, "R1a", "R1b"), new(1, 2, "R2a", "R2b"), new(2, 3, "R3a", "R3b"), new(0, 3)]);

    /// <summary>Every layout, the first of a part's with a pin count being the one its symbol draws for that count.</summary>
    public static readonly IReadOnlyList<LumpedTopology> All =
        [WilkinsonPi, WilkinsonTee, BranchLineLayout, RatRaceLayout, LatticeBalunLayout, PadPi, PadT, PadBridgedT, PadWye, PadDelta];

    /// <summary>
    /// The layout a part draws, from its own words (<paramref name="word"/> answers a parameter's text, or null): MWPD, MBLC,
    /// MRRC or MCBL with <c>Realization = Lumped</c> (MWPD's π or T by <c>Arm</c>), a resistive pad by its <c>Form</c>; null
    /// for anything else. What its pins and its symbol follow wherever its parameters are at hand, a placed part's rows or a
    /// generated cell's parameters: its pin count alone does not name it, since MWPD's T and its six-section ring both have
    /// fifteen pins (3 + 2·6).
    /// </summary>
    public static LumpedTopology? LayoutFor(string part, Func<string, string?> word)
    {
        bool Is<T>(string name, T value) where T : struct, Enum
            => Enum.TryParse(word(name)?.Trim(), ignoreCase: true, out T v) && v.Equals(value);
        if (part.Equals("MRPAD", StringComparison.OrdinalIgnoreCase))
            return PadTopology(Enum.TryParse(word("Form")?.Trim(), ignoreCase: true, out ResistivePadForm form) && Enum.IsDefined(form)
                ? form : ResistivePadForm.Pi);
        if (!Is("Realization", PlanRealization.Lumped)) return null;
        return part.ToUpperInvariant() switch
        {
            "MWPD" => Is("Arm", LumpedArm.Tee) ? WilkinsonTee : WilkinsonPi,
            "MBLC" => BranchLineLayout,
            "MRRC" => RatRaceLayout,
            "MCBL" => LatticeBalunLayout,
            _ => null,
        };
    }

    /// <summary>The resistive pad's layout with <paramref name="pins"/> pins, the π's for any count that is no form's. Unlike
    /// the four spec-driven parts' (<see cref="LayoutFor"/>), a pad's pin count does name its layout: it has no distributed
    /// form whose count could be the same, and the bridged-T and the splitters, nine pins each, share one pin list.</summary>
    public static LumpedTopology PadForPins(int pins)
        => All.FirstOrDefault(t => t.Part == "MRPAD" && t.Pins.Count == pins) ?? PadPi;

    /// <summary>The resistive pad's layout for <paramref name="form"/>.</summary>
    public static LumpedTopology PadTopology(ResistivePadForm form) => form switch
    {
        ResistivePadForm.T => PadT,
        ResistivePadForm.BridgedT => PadBridgedT,
        ResistivePadForm.Wye => PadWye,
        ResistivePadForm.Delta => PadDelta,
        _ => PadPi,
    };

    /// <summary>The lumped Wilkinson divider: each √2·Z0 arm a π or a T at F, and 2·Z0 on R2-R3. Equal split, one section.</summary>
    public static LumpedDesign Wilkinson(double z0, double fHz, double gap, MicrostripSubstrate s, LumpedArm arm,
        double k = 1.0, double n = 1.0)
    {
        Positive("wilkinson", "Wilkinson divider", ("Z0", z0), ("F", fHz), ("Gap", gap), ("K", k));
        if (k != 1.0 || n != 1.0)
            throw new ParameterRefusalException(Diagnostic.Create("device.wilkinson.lumped-equal-split", DiagnosticSeverity.Error,
                "Wilkinson divider: K = {k}, N = {n} with Realization = Lumped. The lumped divider is the equal split in one "
              + "section; set K = 1 and N = 1, or Realization = Distributed.", ("k", k), ("n", n)));
        double omega = 2.0 * Math.PI * fHz, zc = Math.Sqrt(2.0) * z0, l = zc / omega, c = 1.0 / (omega * zc);
        PlanSite[] sites = arm == LumpedArm.Pi
            ? [Series(PlanSiteRole.R, "R2", "R3", 2.0 * z0), Series(PlanSiteRole.L, "L1a", "L1b", l),
               Series(PlanSiteRole.L, "L2a", "L2b", l), Shunt(PlanSiteRole.C, "G1", 2.0 * c), Shunt(PlanSiteRole.C, "G2", c),
               Shunt(PlanSiteRole.C, "G3", c)]
            : [Series(PlanSiteRole.R, "R2", "R3", 2.0 * z0), Series(PlanSiteRole.L, "L1a", "L1b", l),
               Series(PlanSiteRole.L, "L2a", "L2b", l), Series(PlanSiteRole.L, "L3a", "L3b", l),
               Series(PlanSiteRole.L, "L4a", "L4b", l), Shunt(PlanSiteRole.C, "G1", c), Shunt(PlanSiteRole.C, "G2", c)];
        return Design(arm == LumpedArm.Pi ? WilkinsonPi : WilkinsonTee, sites, Width(z0, fHz, s, "Wilkinson divider",
            "device.wilkinson.width-unreachable"), gap);
    }

    /// <summary>The lumped 3 dB branch-line hybrid: each arm a π at F, Z0/√2 the series arms and Z0 the shunt arms, so each
    /// port's C to ground is the two arms' ends together, (1 + √2)/(ω·Z0). One section.</summary>
    public static LumpedDesign BranchLine(double z0, double fHz, double gap, MicrostripSubstrate s, double sections = 1.0)
    {
        Positive("branchline", "Branch-line coupler", ("Z0", z0), ("F", fHz), ("Gap", gap));
        if (sections != 1.0)
            throw new ParameterRefusalException(Diagnostic.Create("device.branchline.lumped-one-section", DiagnosticSeverity.Error,
                "Branch-line coupler: N = {n} with Realization = Lumped. The lumped coupler is one section; set N = 1, or "
              + "Realization = Distributed.", ("n", sections)));
        double omega = 2.0 * Math.PI * fHz, ls = z0 / Math.Sqrt(2.0) / omega, lp = z0 / omega;
        double c = (1.0 + Math.Sqrt(2.0)) / (omega * z0);
        return Design(BranchLineLayout, [Series(PlanSiteRole.L, "L1a", "L1b", ls), Series(PlanSiteRole.L, "L2a", "L2b", lp),
                Series(PlanSiteRole.L, "L3a", "L3b", ls), Series(PlanSiteRole.L, "L4a", "L4b", lp),
                Shunt(PlanSiteRole.C, "G1", c), Shunt(PlanSiteRole.C, "G2", c), Shunt(PlanSiteRole.C, "G3", c),
                Shunt(PlanSiteRole.C, "G4", c)],
            Width(z0, fHz, s, "Branch-line coupler", "device.branchline.width-unreachable"), gap);
    }

    /// <summary>The lumped rat-race: a ring of √2·Z0, three low-pass π (L = √2·Z0/ω) for the λ/4 spacings 2-1, 1-3 and 3-4,
    /// the high-pass π's series C = 1/(ω·√2·Z0) for the 3λ/4 one, 4-2, and 2·C to ground at 1 and at 3.</summary>
    public static LumpedDesign RatRace(double z0, double fHz, double gap, MicrostripSubstrate s)
    {
        Positive("ratrace", "Rat-race hybrid", ("Z0", z0), ("F", fHz), ("Gap", gap));
        double omega = 2.0 * Math.PI * fHz, zr = Math.Sqrt(2.0) * z0, l = zr / omega, c = 1.0 / (omega * zr);
        return Design(RatRaceLayout, [Series(PlanSiteRole.L, "L1a", "L1b", l), Series(PlanSiteRole.L, "L2a", "L2b", l),
                Series(PlanSiteRole.L, "L3a", "L3b", l), Series(PlanSiteRole.C, "C1a", "C1b", c),
                Shunt(PlanSiteRole.C, "G1", 2.0 * c), Shunt(PlanSiteRole.C, "G3", 2.0 * c)],
            Width(z0, fHz, s, "Rat-race hybrid", "device.ratrace.width-unreachable"), gap);
    }

    /// <summary>The lattice balun: √(L/C) = √(2·Z0·ZL), ZL on each output, at ω²·L·C = 1.</summary>
    public static LumpedDesign LatticeBalun(double z0, double zl, double fHz, double gap, MicrostripSubstrate s)
    {
        Positive("marchand", "Marchand balun", ("Z0", z0), ("ZL", zl), ("F", fHz), ("Gap", gap));
        double omega = 2.0 * Math.PI * fHz, x = Math.Sqrt(2.0 * z0 * zl);
        return Design(LatticeBalunLayout, [Series(PlanSiteRole.L, "L1a", "L1b", x / omega), Series(PlanSiteRole.C, "C1a", "C1b",
                1.0 / (omega * x)), Shunt(PlanSiteRole.C, "G2", 1.0 / (omega * x)), Shunt(PlanSiteRole.L, "G3", x / omega)],
            Width(z0, fHz, s, "Marchand balun", "device.marchand.width-unreachable"), gap);
    }

    /// <summary>
    /// The resistive pad (MRPAD): Z0 at every port, and for the attenuators the loss A in dB, K = 10^(A/20). The textbook
    /// matched forms (no DOI): π, a series Z0·(K² − 1)/(2K) and Z0·(K + 1)/(K − 1) to ground on 1 and on 2; T, two series
    /// Z0·(K − 1)/(K + 1) and 2·Z0·K/(K² − 1) to ground on 3; bridged-T, two series Z0, Z0·(K − 1) across them and Z0/(K − 1)
    /// to ground on 3. The splitters are matched three-ports with a fixed loss, 20·log10(2) = 6.02 dB between any two ports,
    /// and take no A: the star's three arms are Z0/3 each, the delta's three sides Z0 (its Δ-Y equal).
    /// </summary>
    public static LumpedDesign ResistivePad(ResistivePadForm form, double z0, double attenuationDb, double gap, MicrostripSubstrate s)
    {
        bool splitter = form is ResistivePadForm.Wye or ResistivePadForm.Delta;
        Positive("attenuator", "Resistive pad", ("Z0", z0), ("Gap", gap), ("Attenuation", splitter ? 1.0 : attenuationDb));
        double k = Math.Pow(10.0, attenuationDb / 20.0);
        PlanSite[] sites = form switch
        {
            ResistivePadForm.T => [Series(PlanSiteRole.R, "R1a", "R1b", z0 * (k - 1.0) / (k + 1.0)),
                Series(PlanSiteRole.R, "R2a", "R2b", z0 * (k - 1.0) / (k + 1.0)), Shunt(PlanSiteRole.R, "3", 2.0 * z0 * k / (k * k - 1.0))],
            ResistivePadForm.BridgedT => [Series(PlanSiteRole.R, "R1a", "R1b", z0), Series(PlanSiteRole.R, "R2a", "R2b", z0),
                Series(PlanSiteRole.R, "R3a", "R3b", z0 * (k - 1.0)), Shunt(PlanSiteRole.R, "3", z0 / (k - 1.0))],
            ResistivePadForm.Wye => [Series(PlanSiteRole.R, "R1a", "R1b", z0 / 3.0), Series(PlanSiteRole.R, "R2a", "R2b", z0 / 3.0),
                Series(PlanSiteRole.R, "R3a", "R3b", z0 / 3.0)],
            ResistivePadForm.Delta => [Series(PlanSiteRole.R, "R1a", "R1b", z0), Series(PlanSiteRole.R, "R2a", "R2b", z0),
                Series(PlanSiteRole.R, "R3a", "R3b", z0)],
            _ => [Series(PlanSiteRole.R, "R1a", "R1b", z0 * (k * k - 1.0) / (2.0 * k)),
                Shunt(PlanSiteRole.R, "1", z0 * (k + 1.0) / (k - 1.0)), Shunt(PlanSiteRole.R, "2", z0 * (k + 1.0) / (k - 1.0))],
        };
        // ponytail: the interconnect's width is MLIN's at 1 MHz, quasi-static, since a pad has no frequency of its own.
        return Design(PadTopology(form), sites, Width(z0, 1e6, s, "Resistive pad", "device.attenuator.width-unreachable"), gap);
    }

    /// <summary>
    /// The plan of <paramref name="t"/>: every junction W = <paramref name="w"/> wide with each arm <paramref name="a"/> from its
    /// centre to its pin, junctions 2a + <paramref name="span"/> apart, so each series site's pins face each other across the
    /// span. Four arms are an MCROSS, three an MTEE, two in line an MLIN, two at a right angle an MBEND square corner with MLIN
    /// on to the arms' ends; an edge with no site is an MLIN of the span between the two arms' ends.
    /// </summary>
    public static PassivePlan Layout(LumpedTopology t, IReadOnlyList<PlanSite> sites, double w, double a, double span)
    {
        double u = 2.0 * a + span;
        int next = t.Pins.Count;
        int Pin(string name) => t.Pins.ToList().IndexOf(name) + 1;
        var arm = new Dictionary<(int J, int Dir), int>();
        var sections = new List<PlanSection>();
        (double X, double Y) At(int j, int d, double r) => (t.Junctions[j].Col * u + r * Dx[d], -t.Junctions[j].Row * u + r * Dy[d]);

        for (int j = 0; j < t.Junctions.Count; j++)
            foreach (var (d, pin) in t.Junctions[j].Out) arm[(j, d)] = Pin(pin);
        foreach (var e in t.Edges)
        {
            var (f, g) = (t.Junctions[e.From], t.Junctions[e.To]);
            int d = (g.Col - f.Col, g.Row - f.Row) switch { (1, 0) => E, (-1, 0) => W, (0, 1) => S, (0, -1) => N,
                _ => throw new ArgumentException($"{t.Part} {t.Label}: junctions {e.From} and {e.To} are not neighbours.") };
            int from = arm[(e.From, d)] = e.PinA is { } pa ? Pin(pa) : ++next;
            int to = arm[(e.To, (d + 2) % 4)] = e.PinB is { } pb ? Pin(pb) : ++next;
            if (e.PinA is null)
            {
                var (x, y) = At(e.From, d, a);
                sections.Add(new PlanLine([from, to], x, y, 90.0 * d, w, span));
            }
        }

        for (int j = 0; j < t.Junctions.Count; j++)
        {
            int[] dirs = [.. Enumerable.Range(0, 4).Where(d => arm.ContainsKey((j, d)))];
            int Node(int d) => arm[(j, d)];
            var (cx, cy) = At(j, E, 0.0);
            switch (dirs.Length)
            {
                case 4:
                    sections.Add(new PlanCross([Node(E), Node(N), Node(W), Node(S)], cx, cy, 0.0, w, w, w, w, a, a, a, a));
                    break;
                case 3:
                {
                    // The branch is opposite the missing arm; turned q quarters, MTEE's own branch (−y) points along it.
                    int b = (Enumerable.Range(0, 4).Single(d => !dirs.Contains(d)) + 2) % 4, q = (b + 1) % 4, into = (q + 2) % 4;
                    var (x, y) = At(j, into, a);
                    sections.Add(new PlanTee(w, w, w, a, a, a, [Node(into), Node(q), Node(b)], x, y, 90.0 * q));
                    break;
                }
                case 2 when dirs[1] - dirs[0] == 2:
                {
                    var (x, y) = At(j, dirs[0], a);
                    sections.Add(new PlanLine([Node(dirs[0]), Node(dirs[1])], x, y, 90.0 * dirs[1], w, 2.0 * a));
                    break;
                }
                case 2:
                {
                    // A corner: MBEND from arm d1 round to arm d2, then MLIN out to each arm's end.
                    int d1 = dirs[0], d2 = dirs[1], h = (d1 + 2) % 4;
                    var bend = new PlanBend([], 0.0, 0.0, 0.0, w, d2 == (h + 1) % 4 ? 90.0 : -90.0, null);
                    double r = bend.Arm;
                    int p1 = ++next, p2 = ++next;
                    var (bx, by) = At(j, d1, r);
                    sections.Add(bend with { Nodes = [p1, p2], X = bx, Y = by, RotationDeg = 90.0 * h });
                    var (x1, y1) = At(j, d1, a);
                    sections.Add(new PlanLine([Node(d1), p1], x1, y1, 90.0 * h, w, a - r));
                    var (x2, y2) = At(j, d2, r);
                    sections.Add(new PlanLine([p2, Node(d2)], x2, y2, 90.0 * d2, w, a - r));
                    break;
                }
                default:
                    throw new ArgumentException($"{t.Part} {t.Label}: junction {j} has {dirs.Length} arm(s).");
            }
        }
        return new PassivePlan(t.Pins, sections, next) { Sites = sites };
    }

    /// <summary>The design on <paramref name="t"/>: arms one interconnect width long, every series site <paramref name="gap"/>
    /// across, all on the 2 nm grid.</summary>
    private static LumpedDesign Design(LumpedTopology t, IReadOnlyList<PlanSite> sites, double w, double gap)
    {
        double width = PassivePlan.OnGrid(w), span = PassivePlan.OnGrid(gap);
        var spanned = sites.Select(x => x.Kind == PlanSiteKind.Series ? x with { SpanMeters = span } : x).ToList();
        return new LumpedDesign(t, width, width, span, Layout(t, spanned, width, width, span));
    }

    private static PlanSite Series(PlanSiteRole role, string a, string b, double value) => new(role, PlanSiteKind.Series, a, b, null, value);

    private static PlanSite Shunt(PlanSiteRole role, string pin, double value) => new(role, PlanSiteKind.Shunt, pin, null, null, value);

    private static double Width(double z0, double fHz, MicrostripSubstrate s, string part, string code)
        => BranchLinePlan.Width(z0, fHz, s, part, code);

    private static void Positive(string topology, string part, params (string Name, double Value)[] specs)
    {
        foreach (var (name, value) in specs)
            if (!(value > 0))
                throw new ParameterRefusalException(Diagnostic.Create($"device.{topology}.spec-not-positive",
                    DiagnosticSeverity.Error, "{part}: {name} = {value} must be positive.", ("part", part), ("name", name),
                    ("value", value)));
    }
}
