using Avalonia.Input;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Payload carried by a palette tile system drag-and-drop operation.
/// The canvas checks for <see cref="Format"/> and ignores all other drag types.
/// </summary>
public sealed record PaletteDragPayload(SymbolKind Kind, int PortCount)
{
    /// <summary>circuitRF-specific in-process DnD format. Foreign drags don't carry this format.</summary>
    public static readonly DataFormat<PaletteDragPayload> Format =
        DataFormat.CreateInProcessFormat<PaletteDragPayload>("circuitrf/palette-item");
}
