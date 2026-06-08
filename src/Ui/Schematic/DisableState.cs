namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Per-instance disable state for a placed component (§7.2).
/// Honored by net extraction (6e) to bridge the component as open or short.
/// The renderer shows the appropriate glyph; the engine sees no disabled component.
/// </summary>
public enum DisableState
{
    None,   // normal
    Open,   // component omitted from netlist (open circuit)
    Short,  // component's ports merged into one net (short circuit)
}
