namespace CircuitRF.Core.Devices;

/// <summary>Two-port inductor. Phase 1: stub.</summary>
public sealed class InductorModel : ComponentModel
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;
}
