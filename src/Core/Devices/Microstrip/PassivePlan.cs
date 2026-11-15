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
