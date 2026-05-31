namespace CircuitRF.Core.Devices;

/// <summary>Two-port resistor. Phase 1: stub; Stamp implemented in Phase 2.</summary>
public sealed class ResistorModel : ComponentModel
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;
}
