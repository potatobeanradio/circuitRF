using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Two-terminal capacitor. Group 1: AddAdmittance(a, b, jωC).
/// At DC (ω = 0): admittance = 0 = exact open circuit.
/// </summary>
public sealed class CapacitorModel : ComponentModel
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        double cap = c.Parameters["C"].AsReal();
        // jωC = 0 at DC → exact open; non-zero at AC.
        mna.AddAdmittance(c.Nodes[0], c.Nodes[1], new Complex(0, omega * cap));
    }
}
