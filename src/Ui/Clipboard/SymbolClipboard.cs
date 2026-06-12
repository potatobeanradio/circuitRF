using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Input.Platform;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Clipboard;

/// <summary>
/// System-clipboard helper for symbol editor selections.
/// Primary format: JSON text (cross-platform, round-trips perfectly).
/// Image formats (PDF/SVG/PNG) are not included in v1 — only JSON ships.
/// See SchematicClipboard for the multi-format pattern if image formats are added later.
/// </summary>
public static class SymbolClipboard
{
    // Prefix guard — any text that doesn't contain this marker is silently ignored on paste.
    private const string Marker = "circuitrf/symbol-clipboard-v1";

    private sealed class Payload
    {
        public string? Marker     { get; set; }
        public double  GridSize   { get; set; } = 100.0;
        public List<SymbolPrimitive> Primitives { get; set; } = [];
        public List<CsymPin>         Pins       { get; set; } = [];
    }

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Copies the given selection to the system clipboard as JSON text.
    /// No-op if both lists are empty.
    /// </summary>
    public static async Task CopyAsync(
        IClipboard clipboard,
        IReadOnlyList<SymbolPrimitive> primitives,
        IReadOnlyList<SymbolPin>       pins,
        double gridSize = 100.0)
    {
        if (primitives.Count == 0 && pins.Count == 0) return;

        var payload = new Payload
        {
            Marker     = Marker,
            GridSize   = gridSize,
            Primitives = [..primitives],
            Pins       = pins.Select(p => new CsymPin
            {
                LocalX    = p.LocalX,
                LocalY    = p.LocalY,
                PortIndex = p.PortIndex,
                Name      = p.Name,
            }).ToList(),
        };

        string json;
        try   { json = JsonSerializer.Serialize(payload, _opts); }
        catch { return; }

        try   { await clipboard.SetTextAsync(json); }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Tries to paste from the system clipboard.
    /// Returns null if the clipboard contains no recognized symbol JSON.
    /// Primitives are translated by (offsetX, offsetY); pins are P-snapped after offset.
    /// </summary>
    public static async Task<(List<SymbolPrimitive> Prims, List<SymbolPin> Pins, double GridSize)?> PasteAsync(
        IClipboard clipboard,
        double offsetX = 100.0, double offsetY = 100.0)
    {
        string? json;
        try   { json = await clipboard.TryGetTextAsync(); }
        catch { return null; }

        if (string.IsNullOrWhiteSpace(json)) return null;

        Payload? payload;
        try   { payload = JsonSerializer.Deserialize<Payload>(json, _opts); }
        catch { return null; }

        if (payload?.Marker != Marker) return null;
        if (payload.Primitives.Count == 0 && payload.Pins.Count == 0) return null;

        // Offset each primitive in-place — fresh objects from deserialization, safe to mutate.
        foreach (var p in payload.Primitives)
            SymbolGeometry.TranslateBy(p, offsetX, offsetY);

        // Offset and P-snap pins (pins always land on the connection grid P=100).
        static double PSnap(double v) => Math.Round(v / 100.0) * 100.0;
        var pins = payload.Pins
            .Select(p => new SymbolPin(PSnap(p.LocalX + offsetX), PSnap(p.LocalY + offsetY), p.PortIndex, p.Name))
            .ToList();

        return (payload.Primitives, pins, payload.GridSize);
    }
}
