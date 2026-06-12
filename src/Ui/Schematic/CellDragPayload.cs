using System;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Payload carried by a cell drag-and-drop operation from the Project Tree.
/// Serialized as a prefixed text string so the payload travels on the native platform pasteboard —
/// an in-process format leaves nothing on NSPasteboard on macOS, causing an AppKit crash.
/// </summary>
public sealed record CellDragPayload(string CellAbsPath)
{
    private const string Prefix = "circuitrf-cell:";

    /// <summary>Compact wire representation: <c>circuitrf-cell:&lt;absolute-cell-folder-path&gt;</c>.</summary>
    public string Serialize() => $"{Prefix}{CellAbsPath}";

    /// <summary>
    /// Parses a string produced by <see cref="Serialize"/>. Returns false for null, empty,
    /// strings without the <c>circuitrf-cell:</c> prefix, or empty paths — this is
    /// the foreign-text guard so random drops onto the canvas are silently ignored.
    /// </summary>
    public static bool TryParse(string? s, out CellDragPayload result)
    {
        result = default!;
        if (s is null || !s.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var path = s[Prefix.Length..];
        if (string.IsNullOrEmpty(path)) return false;
        result = new CellDragPayload(path);
        return true;
    }
}
