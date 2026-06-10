using System;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Payload carried by a palette tile system drag-and-drop operation.
/// Serialized as a prefixed text string so the payload travels on the native platform pasteboard —
/// an in-process format leaves nothing on NSPasteboard on macOS, causing an AppKit crash.
/// </summary>
public sealed record PaletteDragPayload(SymbolKind Kind, int PortCount)
{
    private const string Prefix = "circuitrf-palette:";

    /// <summary>Compact wire representation: <c>circuitrf-palette:Kind:PortCount</c>.</summary>
    public string Serialize() => $"{Prefix}{Kind}:{PortCount}";

    /// <summary>
    /// Parses a string produced by <see cref="Serialize"/>. Returns false for null, empty,
    /// strings without the <c>circuitrf-palette:</c> prefix, or malformed payloads — this is
    /// the foreign-text guard so random drops onto the canvas are silently ignored.
    /// </summary>
    public static bool TryParse(string? s, out PaletteDragPayload result)
    {
        result = default!;
        if (s is null || !s.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var rest = s.AsSpan(Prefix.Length);
        int sep = rest.LastIndexOf(':');
        if (sep < 0) return false;
        var kindSpan  = rest[..sep];
        var countSpan = rest[(sep + 1)..];
        if (!Enum.TryParse<SymbolKind>(kindSpan.ToString(), out var kind)) return false;
        if (!int.TryParse(countSpan, out var portCount)) return false;
        result = new PaletteDragPayload(kind, portCount);
        return true;
    }
}
