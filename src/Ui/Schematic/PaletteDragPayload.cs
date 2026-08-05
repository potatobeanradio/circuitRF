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
/// <param name="PCellGeneratorId">
/// Set when the tile places a PARAMETRIC CELL by generator id. A kit's cells are discovered at run
/// time and share the placeholder <see cref="SymbolKind"/> every kit tile uses, so the kind alone
/// cannot say WHICH cell was dragged — without the id a dropped vendor cell would place whatever
/// that placeholder means, which is nothing.
/// </param>
public sealed record PaletteDragPayload(
    SymbolKind Kind, int PortCount, string? CellDir = null, string? PCellGeneratorId = null)
{
    private const string Prefix      = "circuitrf-palette:";
    private const string PCellMarker = "@pcell:";
    private const string CellMarker  = "@cell:";

    /// <summary>
    /// Compact wire representation: <c>circuitrf-palette:Kind:PortCount</c>, then an optional
    /// <c>:@pcell:&lt;generatorId&gt;</c> and an optional <c>:@cell:&lt;path&gt;</c>.
    ///
    /// <para><b>BOTH tails can be present, and that is the point.</b> One palette tile is one PART;
    /// which VIEW gets placed is decided by what it was dropped on. So a tile for a part that has
    /// both a schematic symbol and a layout generator carries both routes, and dropping it on a
    /// schematic places the symbol while dropping it on a layout places the cell. Emitting only one
    /// would make the same tile work on one canvas and silently do nothing on the other.</para>
    ///
    /// <para>Both tails are MARKED, and the path is always LAST, because a cell folder is a path and
    /// may itself contain ':' — an unmarked tail could not be told apart from a generator id, and a
    /// path placed before the id would swallow it. A generator id may not contain ':'.</para>
    /// </summary>
    public string Serialize()
    {
        var text = $"{Prefix}{Kind}:{PortCount}";
        if (PCellGeneratorId is { Length: > 0 } gen) text += $":{PCellMarker}{gen}";
        if (CellDir is { Length: > 0 } dir)          text += $":{CellMarker}{dir}";
        return text;
    }

    /// <summary>
    /// Parses a string produced by <see cref="Serialize"/>. Returns false for null, empty, strings
    /// without the <c>circuitrf-palette:</c> prefix, or malformed payloads — this is the foreign-text
    /// guard so random drops onto a canvas are silently ignored.
    ///
    /// <para>An UNMARKED trailing path is still accepted: that is what an older payload looks like,
    /// and a drag begun before an update must not become unparseable mid-gesture.</para>
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
        string  tail     = c < 0 ? ""   : after[(c + 1)..];
        if (!int.TryParse(countText, out var portCount)) return false;

        string? generatorId = null;
        if (tail.StartsWith(PCellMarker, StringComparison.Ordinal))
        {
            string afterMarker = tail[PCellMarker.Length..];
            int sep = afterMarker.IndexOf(':');
            generatorId = sep < 0 ? afterMarker : afterMarker[..sep];
            tail        = sep < 0 ? ""          : afterMarker[(sep + 1)..];
        }

        // Marked, or the older unmarked form — both are a cell folder path.
        string? cellDir = tail.StartsWith(CellMarker, StringComparison.Ordinal)
            ? tail[CellMarker.Length..]
            : tail;

        result = new PaletteDragPayload(
            kind, portCount,
            string.IsNullOrWhiteSpace(cellDir)     ? null : cellDir,
            string.IsNullOrWhiteSpace(generatorId) ? null : generatorId);
        return true;
    }
}
