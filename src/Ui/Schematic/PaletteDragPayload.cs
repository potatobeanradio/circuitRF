using System;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Payload carried by a palette tile system drag-and-drop operation.
/// Serialized as a prefixed text string so the payload travels on the native platform pasteboard —
/// an in-process format leaves nothing on NSPasteboard on macOS, causing an AppKit crash.
/// </summary>
/// <param name="CellDir">
/// Absolute cell folder for an entry contributed by an imported kit — the cell whose symbol was
/// installed for it. Null for every built-in entry.
///
/// <para>Carrying this is what makes a DRAGGED kit part place the same component a CLICKED one
/// does. Without it the drop sees only the placeholder <see cref="SymbolKind"/> every kit part
/// shares, and places a generic component with a generic glyph — the two entry points silently
/// disagreeing about what the same tile means.</para>
/// </param>
public sealed record PaletteDragPayload(SymbolKind Kind, int PortCount, string? CellDir = null)
{
    private const string Prefix = "circuitrf-palette:";

    /// <summary>
    /// Compact wire representation: <c>circuitrf-palette:Kind:PortCount</c>, with an optional
    /// <c>:CellDir</c> tail. The tail is LAST so a payload written without one still parses, and a
    /// path containing ':' survives (everything after the third separator is the path).
    /// </summary>
    public string Serialize() => CellDir is { Length: > 0 }
        ? $"{Prefix}{Kind}:{PortCount}:{CellDir}"
        : $"{Prefix}{Kind}:{PortCount}";

    /// <summary>
    /// Parses a string produced by <see cref="Serialize"/>. Returns false for null, empty,
    /// strings without the <c>circuitrf-palette:</c> prefix, or malformed payloads — this is
    /// the foreign-text guard so random drops onto the canvas are silently ignored.
    /// </summary>
    public static bool TryParse(string? s, out PaletteDragPayload result)
    {
        result = default!;
        if (s is null || !s.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        string rest = s[Prefix.Length..];

        int k = rest.IndexOf(':');
        if (k < 0) return false;
        if (!Enum.TryParse<SymbolKind>(rest[..k], out var kind)) return false;

        string after = rest[(k + 1)..];
        int c = after.IndexOf(':');

        string countText = c < 0 ? after : after[..c];
        string? cellDir  = c < 0 ? null  : after[(c + 1)..];
        if (!int.TryParse(countText, out var portCount)) return false;

        result = new PaletteDragPayload(kind, portCount,
                                        string.IsNullOrWhiteSpace(cellDir) ? null : cellDir);
        return true;
    }
}
