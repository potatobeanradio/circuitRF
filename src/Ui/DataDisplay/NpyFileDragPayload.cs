using System;

namespace CircuitRF.Ui.DataDisplay;

/// <summary>
/// Payload carried by a data-file drag-and-drop from the Project Tree onto a Data Display plot
/// (R-dd-3) — "compare against that other run" as one motion, mirroring the existing
/// palette→schematic/palette→layout drag idiom. Serialized as a prefixed text string so the
/// payload travels on the native platform pasteboard — an in-process format leaves nothing on
/// NSPasteboard on macOS, causing an AppKit crash (see <c>PaletteDragPayload</c>'s own note).
/// </summary>
public sealed record NpyFileDragPayload(string AbsolutePath)
{
    private const string Prefix = "circuitrf-data-file:";

    /// <summary>Compact wire representation: <c>circuitrf-data-file:&lt;absolute-file-path&gt;</c>.</summary>
    public string Serialize() => $"{Prefix}{AbsolutePath}";

    /// <summary>
    /// Parses a string produced by <see cref="Serialize"/>. Returns false for null, empty,
    /// strings without the prefix, or an empty path — the foreign-text guard so a random text
    /// drop onto a plot is silently ignored.
    /// </summary>
    public static bool TryParse(string? s, out NpyFileDragPayload result)
    {
        result = default!;
        if (s is null || !s.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var path = s[Prefix.Length..];
        if (string.IsNullOrEmpty(path)) return false;
        result = new NpyFileDragPayload(path);
        return true;
    }
}
