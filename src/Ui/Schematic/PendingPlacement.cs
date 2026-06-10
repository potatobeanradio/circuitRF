namespace CircuitRF.Ui.Schematic;

/// <summary>
/// App-level armed-placement state: the component type + rotation currently being placed.
/// Null on <see cref="PlacementService"/> means nothing is armed.
/// </summary>
public sealed record PendingPlacement(
    SymbolKind Kind,
    int PortCount,
    SymbolRotation Rotation = SymbolRotation.R0);
