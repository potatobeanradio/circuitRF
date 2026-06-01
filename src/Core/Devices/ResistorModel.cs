using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>Two-terminal resistor. Group 1: AddAdmittance(a, b, 1/R).</summary>
public sealed class ResistorModel : ComponentModel
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        double r = c.Parameters["R"].AsReal();
        if (r <= 0)
            throw new InvalidOperationException(
                $"{c.InstancePath}: R must be positive, got {r}");
        mna.AddAdmittance(c.Nodes[0], c.Nodes[1], new Complex(1.0 / r, 0));
    }
}
