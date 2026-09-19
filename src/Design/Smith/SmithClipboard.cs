// The TEXT flavour of a Smith Chart copy (docs/design/smith-chart.md §6; brief-smith-1-document.md
// R-smith1-6).
//
// ── WHY THERE IS A MARKER ──────────────────────────────────────────────────────────────────────
//
// The guard is not tidiness. The system clipboard is one shared channel, and the text sitting on it
// may be a layout fragment, a schematic fragment, a Data Display config, a line somebody copied out
// of a terminal, or a .crail pasted out of an editor. Every one of those is JSON-shaped often enough
// that a permissive reader gets PART of it and reports success — and a document half-replaced by an
// unrelated payload is worse than one that refused, because nothing says so.
//
// RailClipboard and LayoutFragment.Marker are the same guard for the same reason, and this file is
// deliberately their shape: one constant, one envelope, one TryDeserialize that returns false for
// absolutely everything that is not this.
//
// ── AND IT WRITES NO CLIPBOARD CODE ────────────────────────────────────────────────────────────
//
// This is a string. The string goes to PlotExporter.SetClipboardDataAsync like every other copy in
// this application (overview §0: one clipboard path). Nothing in src/Design may reference Avalonia
// at all.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace CircuitRF.Design.Smith;

/// <summary>The marker-guarded JSON a Smith Chart copy carries. Framework-free.</summary>
public static class SmithClipboard
{
    /// <summary>
    /// The guard. Versioned in the same breath as the name, on <c>LayoutFragment.Marker</c>'s own
    /// terms — a reader that met a payload it could not understand would otherwise have to guess
    /// whether it was FOREIGN or merely NEWER, and those two want different answers.
    /// </summary>
    public const string Marker = "circuitrf/smith-clipboard-v1";

    private const string MarkerField = "marker";
    private const string DesignField = "design";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// One Smith design, wrapped.
    /// </summary>
    /// <remarks>
    /// <b>Unvalidated, deliberately</b> — see <see cref="SmithDesignIo.SerializeUnvalidated"/>. A
    /// copy always writes, including from a design that is still being built, because a copy that
    /// wrote nothing would leave the previous one on the clipboard for the next paste to find.
    ///
    /// <para>The design is NESTED rather than having the marker added as a sibling key, so this
    /// envelope can never collide with a field `.csmith` grows later, and so a reader can tell a
    /// clipboard payload from a bare `.csmith` file by shape alone.</para>
    /// </remarks>
    public static string Serialize(SmithDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);

        var envelope = new JsonObject
        {
            [MarkerField] = Marker,
            [DesignField] = JsonNode.Parse(SmithDesignIo.SerializeUnvalidated(design)),
        };
        return envelope.ToJsonString(JsonOpts);
    }

    /// <summary>
    /// Marker-guarded parse. <b>Anything else is a clean false</b> — arbitrary text, a layout,
    /// schematic or railRF clipboard payload, a Data Display config, a bare `.csmith` file,
    /// truncated JSON, an envelope with a different marker, or a design from a newer circuitRF.
    /// Never an exception, never a partially populated design.
    /// </summary>
    public static bool TryDeserialize(string? text, out SmithDesign? design)
    {
        design = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        try
        {
            if (JsonNode.Parse(text) is not JsonObject envelope) return false;
            if (envelope[MarkerField]?.GetValue<string>() != Marker) return false;
            if (envelope[DesignField] is not JsonObject body) return false;

            design = SmithDesignIo.DeserializeUnvalidated(body.ToJsonString());
            return true;
        }
        catch
        {
            // Every failure mode of the three calls above is the same answer: this is not ours.
            return false;
        }
    }
}
