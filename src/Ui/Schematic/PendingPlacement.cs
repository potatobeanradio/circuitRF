namespace CircuitRF.Ui.Schematic;

/// <summary>
/// App-level armed-placement state: the component type + rotation currently being placed.
/// Null on <see cref="PlacementService"/> means nothing is armed.
/// </summary>
public sealed record PendingPlacement(
    SymbolKind Kind,
    int PortCount,
    SymbolRotation Rotation = SymbolRotation.R0,
    /// <summary>
    /// Set when the armed entry came from an imported kit rather than the built-in library.
    /// Null for every built-in placement, which is therefore completely unaffected by it.
    /// </summary>
    PdkPartRef? Pdk = null);
