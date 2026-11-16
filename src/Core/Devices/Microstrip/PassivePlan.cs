using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>
/// The substrate a planned passive is built on: MLIN's own six numbers, as the factory resolves them for
/// any microstrip part (injected from the technology's stackup at extraction, the factory's fallbacks
/// otherwise).
/// </summary>
public sealed record MicrostripSubstrate(
    double HMeters, double TMeters, double EpsR, double SigmaSPerM, double TanD, double RoughnessMeters = 0.0);

/// <summary>
/// One piece of a planned passive: an EXISTING microstrip part, placed. The model stamps it through that
/// part's own model and the PCell draws it through that part's own generator, so the two cannot describe
/// different copper (docs/design/rf-passives.md).
/// </summary>
/// <param name="Nodes">The plan node each of the part's pins joins, in the part's own terminal order. Nodes
/// 1..<see cref="PassivePlan.Pins"/>.Count are the passive's pins in order; the rest are internal.</param>
/// <param name="X">Where the part's pin 1 sits in the passive's frame (m). The part's own frame has its pin 1
/// at the origin (R-pc-3).</param>
/// <param name="Y">As <paramref name="X"/>.</param>
/// <param name="RotationDeg">The part's frame turned counter-clockwise about its pin 1, a multiple of 90.</param>
public abstract record PlanSection(int[] Nodes, double X, double Y, double RotationDeg)
{
    /// <summary>
    /// A two-pin section's electrical length at <paramref name="fHz"/>, as a length of the line it joins: its insertion
    /// phase between two lines of <paramref name="z"/>, from the part's own model stamped as a plan of this one section,
    /// over the line's phase per metre (π/2 over its λg/4 at F, <paramref name="quarterWave"/>). A plan shortens its
    /// straight runs by a corner's, so it stays a pure function of the specs and the substrate.
    /// </summary>
    public double LineLength(MicrostripSubstrate s, double fHz, double z, double quarterWave)
        => -new PassivePlan(["1", "2"], [this with { Nodes = [1, 2], X = 0.0, Y = 0.0, RotationDeg = 0.0 }], 2)
               .S21(s, fHz, z).Phase / (Math.PI / 2.0) * quarterWave;
}

/// <summary>An MTEE: W1/W2 the through arms, W3 the branch, and L1-L3 each arm from the junction centre to its
/// pin, as <c>MTeePCell</c> draws them and <see cref="MicrostripTeeModel"/> simulates them. Nodes: through_in,
/// through_out, branch.</summary>
public sealed record PlanTee(double W1, double W2, double W3, double L1, double L2, double L3,
    int[] Nodes, double X, double Y, double RotationDeg) : PlanSection(Nodes, X, Y, RotationDeg);

/// <summary>An MCROSS: arms 1-4 along the part's +x, +y, −x and −y, of widths W1-W4, and L1-L4 each arm from the junction
/// centre to its pin, as <c>MCrossPCell</c> draws them and <see cref="MicrostripCrossModel"/> simulates them. Unlike the
/// other sections it is posed at its centre, MCROSS's own origin (R-pc-3's symmetric junction), not at a pin. Nodes: arm 1
/// to arm 4. MCROSS keeps its junction only while each pair of arms reaches past the other pair: a pair ending at the
/// other's edges is, to it, a straight line with pins on its sides.</summary>
public sealed record PlanCross(int[] Nodes, double X, double Y, double RotationDeg,
    double W1, double W2, double W3, double W4, double L1, double L2, double L3, double L4)
    : PlanSection(Nodes, X, Y, RotationDeg);

/// <summary>An MCURVE: width, signed turn (degrees, counter-clockwise positive) and centre-line radius.</summary>
public sealed record PlanCurve(double W, double AngleDeg, double Radius,
    int[] Nodes, double X, double Y, double RotationDeg) : PlanSection(Nodes, X, Y, RotationDeg);

/// <summary>An MLIN: width and length, running along the part's +x from pin 1 to pin 2.</summary>
public sealed record PlanLine(int[] Nodes, double X, double Y, double RotationDeg, double W, double L) : PlanSection(Nodes, X, Y, RotationDeg);

/// <summary>An MGAP: the end-to-end gap <paramref name="S"/> between two line ends of width <paramref name="W"/>, pin 1
/// at the pose and pin 2 S on along the part's +x (<see cref="MicrostripGapModel"/>, which refuses S ≤ 0 and warns
/// outside its fit as MGAP does; <c>MGapPCell</c> draws no copper, only the two ends). No copper joins its nodes.</summary>
public sealed record PlanGap(int[] Nodes, double X, double Y, double RotationDeg, double W, double S)
    : PlanSection(Nodes, X, Y, RotationDeg);

/// <summary>An MTAPER: width <paramref name="W1"/> at pin 1 to <paramref name="W2"/> at pin 2, linearly over
/// <paramref name="L"/> along the part's +x (<see cref="MicrostripTaperModel"/>, <c>MTaperPCell</c>).</summary>
public sealed record PlanTaper(int[] Nodes, double X, double Y, double RotationDeg, double W1, double W2, double L)
    : PlanSection(Nodes, X, Y, RotationDeg);

/// <summary>
/// An open end: the field fringing past the end of a strip of width <paramref name="W"/> with nothing attached
/// (<see cref="MicrostripOpenEnd"/>), stamped as its end capacitance to ground. One node: a line's end. Two nodes and
/// the gap <paramref name="S"/>: the ends of a coupled pair's two strips at one end of a <see cref="PlanCoupledLine"/>
/// (either strip first), each mode with its own end capacitance. It draws nothing; the pose only says where the end is.
/// A quarter-wave stub's open end has a near-zero self admittance at resonance and <see cref="LocalAdmittanceBlock"/>
/// does not pivot, so a plan numbers it first of its internal nodes, to be eliminated last (as MCBL numbers its own).
/// </summary>
public sealed record PlanOpen(int[] Nodes, double X, double Y, double RotationDeg, double W, double? S = null)
    : PlanSection(Nodes, X, Y, RotationDeg);

/// <summary>
/// A corner: an MMITER of miter fraction <paramref name="M"/> (<see cref="MicrostripMiterBendModel"/>) or, with no M, an
/// MBEND's square corner (<see cref="MicrostripBendModel"/>), turning <paramref name="AngleDeg"/> (counter-clockwise
/// positive) from the part's +x. Pin 1 is at the pose, the centre-line corner <see cref="Arm"/> along +x, and pin 2
/// <see cref="Arm"/> on from the corner along the new heading.
/// </summary>
public sealed record PlanBend(int[] Nodes, double X, double Y, double RotationDeg, double W, double AngleDeg, double? M)
    : PlanSection(Nodes, X, Y, RotationDeg)
{
    /// <summary>Pin to centre-line corner, where the part's own generator puts its pins: MMITER's pin offset, or the
    /// reference plane of MBEND's square corner, rounded to the 1 nm database unit as both generators round them.</summary>
    public double Arm
    {
        get
        {
            double w = Math.Round(W / 1e-9);
            double nm = M is { } m ? MicrostripMiterBend.PinOffset(w, AngleDeg, m) : MicrostripMiterBend.PlaneOffset(w, AngleDeg);
            return Math.Round(nm, MidpointRounding.AwayFromZero) * 1e-9;
        }
    }

    /// <summary>The miter a <paramref name="corner"/> corner of width <paramref name="w"/> takes: for
    /// <see cref="PlanCorner.Miter"/> MMITER's own default, the Douville-James optimum for W/h capped where its table
    /// ends; for <see cref="PlanCorner.Square"/> none, MBEND's square corner.</summary>
    public static double? MiterFor(PlanCorner corner, double w, MicrostripSubstrate s)
        => corner == PlanCorner.Miter
            ? Math.Min(MicrostripMiterBend.DouvilleJamesOptimum(w, s.HMeters), MicrostripMiterBend.MaxMiter(90.0))
            : null;
}

/// <summary>How a ring's corners are made (MWPD, MRRC). <see cref="Curve"/>: 180° MCURVE half-turns, as the parts always
/// were. <see cref="Miter"/> and <see cref="Square"/>: a rectangle of straight runs and four 90° corners
/// (<see cref="PlanBend"/>), an MMITER at the Douville-James chamfer or MBEND's square corner.</summary>
public enum PlanCorner { Curve, Miter, Square }

/// <summary>What a site holds: a resistor, an inductor, a capacitor, a via to ground, a jumper (a bond wire or an air
/// bridge joining two pieces of the part's copper), or the load the part drives (MCBL's balanced load, which is the
/// circuit's rather than the part's).</summary>
public enum PlanSiteRole { R, L, C, Via, Jumper, Load }

/// <summary>Series: the part goes across two of the passive's pins. Shunt: from one pin to ground.</summary>
public enum PlanSiteKind { Series, Shunt }

/// <summary>
/// A place for one lumped part (docs/design/rf-passives.md, "Sites"): an ordinary part on the passive's pins, never copper
/// of the passive's own. Metadata only: no section reads it, so a plan's model and artwork are the same with or without
/// its sites.
/// </summary>
/// <param name="PinA">The pin the part's first terminal lands on.</param>
/// <param name="PinB">The pin its second terminal lands on; null for a shunt site, whose second terminal is ground.</param>
/// <param name="SpanMeters">Pin to pin, where the plan sets it (MWPD's and MCBL's <c>Gap</c>, MIDC's finger pitch); null
/// for a shunt site, whose part brings its own land.</param>
/// <param name="Value">What the design wants there, in SI (Ω, H, F); null where the part is a short (a via, a jumper).</param>
public sealed record PlanSite(PlanSiteRole Role, PlanSiteKind Kind, string PinA, string? PinB, double? SpanMeters,
    double? Value)
{
    /// <summary>How a report names the site: its pins, <c>R2-R3</c>, or the one pin of a shunt site.</summary>
    public string Name => PinB is null ? PinA : $"{PinA}-{PinB}";
}

/// <summary>A planned passive: its pin names in terminal order, its sections, and how many plan nodes the
/// sections join (pins included).</summary>
public sealed record PassivePlan(IReadOnlyList<string> Pins, IReadOnlyList<PlanSection> Sections, int NodeCount)
{
    /// <summary>The lumped parts the plan expects on its pins (<see cref="PlanSite"/>). Empty for a part with none.</summary>
    public IReadOnlyList<PlanSite> Sites { get; init; } = [];

    /// <summary>What the plan knows its model leaves out (a route's uncoupled legs, <see cref="PlanRoute"/>): reported by
    /// <see cref="MicrostripPlanModel"/> with its sections' own warnings.</summary>
    public IReadOnlyList<Diagnostic> Warnings { get; init; } = [];

    /// <summary>S21 of a two-pin plan at <paramref name="fHz"/> between terminations of <paramref name="z"/>, from
    /// <see cref="MicrostripPlanModel"/>: what a run's or a corner's insertion phase is read from.</summary>
    public Complex S21(MicrostripSubstrate s, double fHz, double z)
    {
        var model = new MicrostripPlanModel(this, s, "(corner)");
        var y = new LocalAdmittanceBlock(2);
        model.Stamp(y, new ElaboratedComponent(nameof(MicrostripPlanModel), "(corner)", [1, 2],
            new Dictionary<string, Value>(), model), 2.0 * Math.PI * fHz);
        return -2.0 * z * y[1, 0] / ((1.0 + z * y[0, 0]) * (1.0 + z * y[1, 1]) - z * z * y[0, 1] * y[1, 0]);
    }

    /// <summary>
    /// A length on the 2 nm grid. Every dimension a plan places is held on it, so that a half of any of them
    /// is a whole nanometre: the layout database's unit (1 nm), where the generator rounds each part's own
    /// pins. Two pieces that meet in the plan then meet to the last unit in the artwork instead of a rounding
    /// apart, which LVS reads as open.
    /// </summary>
    public static double OnGrid(double meters) => Math.Round(meters / 2e-9, MidpointRounding.AwayFromZero) * 2e-9;
}

/// <summary>
/// <see cref="Fingers"/> edge-coupled strips of width W, gap S and length L, running from x = 0 to +X, strip k centred
/// k·(W + S) below the first, as <c>MCLIN</c>'s artwork places its two. Nodes: each strip's near (x = 0) end, then its
/// far end, top strip first — for two strips exactly MCLIN's order (A near, A far, B near, B far), and the section is
/// an MCLIN (<see cref="MicrostripCoupledLineModel"/>). With more, alternate strips are the two conductors of an
/// interdigitated coupler, modelled from the same pair's modes by Ou's equations (<see cref="InterdigitatedLineModel"/>);
/// the strips of one conductor are joined only by parts on the passive's pins, never inside it. Strips of their own
/// widths and gaps, or a section with every coupling modelled, are <see cref="PlanCoupledStrips"/>.
///
/// <para><b>Wiggly edges</b> (two strips, <paramref name="OddLengthFactor"/> above 1): the facing edges are a saw-tooth
/// of <see cref="WigglePitch"/> and peak-to-peak <see cref="WiggleDepth"/>, so the odd mode, whose current runs along
/// them, travels <paramref name="OddLengthFactor"/> times the straight length (A. Podell, 1970). S is then the gap
/// across the teeth; strip B sits <see cref="WiggleGapOffset"/> below strip A's mean edge.</para>
/// </summary>
public sealed record PlanCoupledLine(int[] Nodes, double X, double Y, double RotationDeg, double W, double S, double L,
    int Fingers = 2, double OddLengthFactor = 1.0) : PlanSection(Nodes, X, Y, RotationDeg)
{
    /// <summary>True when the facing edges are a saw-tooth.</summary>
    public bool Wiggly => OddLengthFactor > 1.0;

    /// <summary>The saw-tooth's period: about one strip width, a whole number of teeth along L.</summary>
    public double WigglePitch => L / Math.Max(1.0, Math.Round(L / W));

    /// <summary>The saw-tooth's peak-to-peak depth: each half-tooth is k times its run, k the odd-length factor.</summary>
    public double WiggleDepth => WigglePitch / 2.0 * Math.Sqrt(Math.Max(0.0, OddLengthFactor * OddLengthFactor - 1.0));

    /// <summary>Strip B's mean edge below strip A's: S across the slanted teeth, S·k measured straight down.</summary>
    public double WiggleGapOffset => S * Math.Max(1.0, OddLengthFactor);
}

/// <summary>
/// A radial stub (MRADIAL, <see cref="MicrostripRadialStubModel"/>): a sector of <paramref name="AngleDeg"/> whose narrow
/// end is <paramref name="W"/> wide, open at <paramref name="Ro"/> from its apex, or with <paramref name="Sides"/> = 2 a
/// butterfly. Its one node is the shunt node, the middle of the narrow end, where the stub hangs off a tee's branch or
/// a line's end. In the part's own frame one sector fans along +x from its pin; a butterfly's second sector fans along
/// −x, and its pin faces −y.
/// </summary>
public sealed record PlanRadialStub(int[] Nodes, double X, double Y, double RotationDeg, double W, double Ro,
    double AngleDeg, int Sides = 1) : PlanSection(Nodes, X, Y, RotationDeg);

/// <summary>
/// N edge-coupled strips, each of its own width (<paramref name="W"/>, top strip first) with a gap of its own below each
/// but the last (<paramref name="S"/>, N − 1 of them), all of length L, running from x = 0 to +X and stacked downward
/// from the first as <see cref="PlanCoupledLine"/>'s are. The general N-strip section: the N-line model stamps it
/// (<see cref="MicrostripMultiLineModel"/>, MCLINN's), every strip coupled to every other through the cross-section's
/// own C and L, so a strip couples to both neighbours at once and to the strips beyond them. A plan that wants that for
/// two strips, or for an interdigitated set, asks by placing this rather than a <see cref="PlanCoupledLine"/>.
///
/// <para>Nodes as <see cref="PlanCoupledLine"/>'s: each strip's near (x = 0) end, then its far end, top strip first.
/// The model's pins are the near ends 1..N, then the far ends N + 1..2N (<see cref="ModelNodes"/>), so strip k's
/// nodes 2k − 1 and 2k are its pins k and N + k.</para>
/// </summary>
public sealed record PlanCoupledStrips(int[] Nodes, double X, double Y, double RotationDeg, IReadOnlyList<double> W,
    IReadOnlyList<double> S, double L) : PlanSection(Nodes, X, Y, RotationDeg)
{
    /// <summary><see cref="PlanSection.Nodes"/> in the model's pin order: every near end, then every far end.</summary>
    public int[] ModelNodes => [.. Enumerable.Range(0, W.Count).Select(k => Nodes[2 * k]),
                                .. Enumerable.Range(0, W.Count).Select(k => Nodes[2 * k + 1])];
}

/// <summary>A broadside pair's cross-section, as BCLIN (<see cref="Stripline"/>) or MBCLIN takes it, and the two
/// conductors the strips are drawn on. <see cref="Ha"/> is NaN on a microstrip pair, <see cref="Hd"/> NaN on a stripline
/// (and on a microstrip pair whose top strip is the surface).</summary>
public sealed record BroadsideStack(double Hs, double Hb, double Ha, double Hd, double Ta, double Tb, double EpsR,
    double SigmaSPerM, double TanD, bool Stripline, string? LayerA = null, string? LayerB = null);

/// <summary>
/// A broadside-coupled pair, strip A on one conductor and strip B on the next one down, centre to centre
/// <paramref name="O"/> apart sideways: BCLIN on a stripline stack, MBCLIN otherwise
/// (<see cref="StriplineBroadsideCoupledModel"/>, <see cref="MicrostripBroadsideCoupledModel"/>), drawn by their own
/// generator. Nodes in MCLIN's order: A near, A far, B near, B far.
/// </summary>
public sealed record PlanBroadsideLine(int[] Nodes, double X, double Y, double RotationDeg, double W, double O, double L,
    BroadsideStack Stack) : PlanSection(Nodes, X, Y, RotationDeg);
