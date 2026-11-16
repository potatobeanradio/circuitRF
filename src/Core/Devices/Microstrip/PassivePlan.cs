using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

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
public abstract record PlanSection(int[] Nodes, double X, double Y, double RotationDeg);

/// <summary>An MTEE: W1/W2 the through arms, W3 the branch, and L1-L3 each arm from the junction centre to its
/// pin, as <c>MTeePCell</c> draws them and <see cref="MicrostripTeeModel"/> simulates them. Nodes: through_in,
/// through_out, branch.</summary>
public sealed record PlanTee(double W1, double W2, double W3, double L1, double L2, double L3,
    int[] Nodes, double X, double Y, double RotationDeg) : PlanSection(Nodes, X, Y, RotationDeg);

/// <summary>An MCURVE: width, signed turn (degrees, counter-clockwise positive) and centre-line radius.</summary>
public sealed record PlanCurve(double W, double AngleDeg, double Radius,
    int[] Nodes, double X, double Y, double RotationDeg) : PlanSection(Nodes, X, Y, RotationDeg);

/// <summary>An MLIN: width and length, running along the part's +x from pin 1 to pin 2.</summary>
public sealed record PlanLine(int[] Nodes, double X, double Y, double RotationDeg, double W, double L) : PlanSection(Nodes, X, Y, RotationDeg);

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

    /// <summary>
    /// The corner's electrical length at <paramref name="fHz"/>, as a length of the line it joins: its insertion phase
    /// between two lines of <paramref name="z"/>, from the part's own model stamped as a plan of this one section,
    /// over the line's phase per metre (π/2 over its λg/4 at F, <paramref name="quarterWave"/>). A plan shortens its
    /// straight runs by it, so it stays a pure function of the specs and the substrate.
    /// </summary>
    public double LineLength(MicrostripSubstrate s, double fHz, double z, double quarterWave)
    {
        var model = new MicrostripPlanModel(
            new PassivePlan(["1", "2"], [this with { Nodes = [1, 2], X = 0.0, Y = 0.0, RotationDeg = 0.0 }], 2), s, "(corner)");
        var y = new LocalAdmittanceBlock(2);
        model.Stamp(y, new ElaboratedComponent(nameof(MicrostripPlanModel), "(corner)", [1, 2],
            new Dictionary<string, Value>(), model), 2.0 * Math.PI * fHz);
        Complex s21 = -2.0 * z * y[1, 0] / ((1.0 + z * y[0, 0]) * (1.0 + z * y[1, 1]) - z * z * y[0, 1] * y[1, 0]);
        return -s21.Phase / (Math.PI / 2.0) * quarterWave;
    }
}

/// <summary>How a ring's corners are made (MWPD, MRRC). <see cref="Curve"/>: 180° MCURVE half-turns, as the parts always
/// were. <see cref="Miter"/> and <see cref="Square"/>: a rectangle of straight runs and four 90° corners
/// (<see cref="PlanBend"/>), an MMITER at the Douville-James chamfer or MBEND's square corner.</summary>
public enum PlanCorner { Curve, Miter, Square }

/// <summary>A planned passive: its pin names in terminal order, its sections, and how many plan nodes the
/// sections join (pins included).</summary>
public sealed record PassivePlan(IReadOnlyList<string> Pins, IReadOnlyList<PlanSection> Sections, int NodeCount)
{
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
/// the strips of one conductor are joined only by parts on the passive's pins, never inside it.
/// </summary>
public sealed record PlanCoupledLine(int[] Nodes, double X, double Y, double RotationDeg, double W, double S, double L,
    int Fingers = 2) : PlanSection(Nodes, X, Y, RotationDeg);

/// <summary>
/// A radial stub (MRADIAL, <see cref="MicrostripRadialStubModel"/>): a sector of <paramref name="AngleDeg"/> whose narrow
/// end is <paramref name="W"/> wide, open at <paramref name="Ro"/> from its apex, or with <paramref name="Sides"/> = 2 a
/// butterfly. Its one node is the shunt node, the middle of the narrow end, where the stub hangs off a tee's branch or
/// a line's end. In the part's own frame one sector fans along +x from its pin; a butterfly's second sector fans along
/// −x, and its pin faces −y.
/// </summary>
public sealed record PlanRadialStub(int[] Nodes, double X, double Y, double RotationDeg, double W, double Ro,
    double AngleDeg, int Sides = 1) : PlanSection(Nodes, X, Y, RotationDeg);
