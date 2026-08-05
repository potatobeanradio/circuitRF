using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// A capacitor whose value comes from a process and a geometry rather than from a number the user
/// typed — the area and sidewall components of a fabricated capacitor, with temperature
/// coefficients.
///
/// <code>
///   C = ( Cfixed + Cj·area + Cjsw·perimeter ) · (1 + TC1·ΔT + TC2·ΔT²)
/// </code>
///
/// <para><b>Why this is a separate type from <see cref="CapacitorModel"/> and not extra parameters
/// on it.</b> An ideal capacitor takes a capacitance; this one takes a process and a shape and
/// works out a capacitance. Merging them would put six parameters that mean nothing on every
/// capacitor ever placed, and would make <c>C</c> ambiguous — is it the value, or a parasitic to add
/// to the geometric term? Kept apart, both questions have one answer.</para>
///
/// <para><b>It is LINEAR, and that is the physics, not a simplification.</b> The area and sidewall
/// terms of a fabricated capacitor are fixed by geometry: the capacitance does not depend on the
/// voltage across it. A capacitance that DOES vary with bias is a junction, and a junction is a
/// diode — <see cref="DiodeModel"/> already carries the depletion charge and its derivative, in the
/// form harmonic balance needs. Making this one bias-dependent would put a second, worse copy of
/// that physics under a name that does not say so.</para>
///
/// <para><b>Everything is resolved at construction.</b> The ambient a device is evaluated at is
/// known at elaboration and nowhere else, so a model reading its parameters during
/// <see cref="Stamp"/> could not see it. A sweep re-elaborates every point, so this costs nothing.
/// </para>
/// </summary>
public sealed class SemiCapacitorModel : ComponentModel
{
    private readonly double _c;

    /// <param name="fixedCapacitance">A capacitance that is not geometric — a stated parasitic, or the whole value.</param>
    /// <param name="areaCapacitance">Capacitance per unit area (the card's <c>Cj</c>).</param>
    /// <param name="perimeterCapacitance">Capacitance per unit length of edge (the card's <c>Cjsw</c>).</param>
    /// <param name="deltaT">Device temperature minus the parameter set's own extraction temperature, in degrees.</param>
    public SemiCapacitorModel(
        double fixedCapacitance     = 0.0,
        double areaCapacitance      = 0.0,
        double perimeterCapacitance = 0.0,
        double area                 = 0.0,
        double perimeter            = 0.0,
        double tc1                  = 0.0,
        double tc2                  = 0.0,
        double deltaT               = 0.0)
        => _c = (fixedCapacitance
               + areaCapacitance      * area
               + perimeterCapacitance * perimeter)
              * Temperature.PolynomialScale(tc1, tc2, deltaT);

    /// <summary>The capacitance this instance resolved to, in farads. Surfaced because it is DERIVED —
    /// a user who typed a width and a length has no other way to see what capacitor they got.</summary>
    public double Capacitance => _c;

    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
        // jωC = 0 at DC → exact open, the same as the ideal capacitor.
        => mna.AddAdmittance(c.Nodes[0], c.Nodes[1], new Complex(0, omega * _c));
}
