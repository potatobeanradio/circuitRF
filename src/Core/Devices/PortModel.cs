namespace CircuitRF.Core.Devices;

/// <summary>
/// RF port / termination — two nodes (signal + reference).
/// Carries Num (port number) and Z (reference impedance).
/// Phase 1: stub.
/// </summary>
public sealed class PortModel : ComponentModel
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;
}

/// <summary>Alias used in .cnl as "Term".</summary>
public sealed class TermModel : ComponentModel
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;
}
