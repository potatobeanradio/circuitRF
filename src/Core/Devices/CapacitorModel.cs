namespace CircuitRF.Core.Devices;

/// <summary>Two-port capacitor. Phase 1: stub.</summary>
public sealed class CapacitorModel : ComponentModel
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;
}
