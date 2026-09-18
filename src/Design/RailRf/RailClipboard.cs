// The TEXT flavour of a railRF copy (railrf.md §11.7; brief-railrf-9-copy.md R-rail9-4).
//
// ── WHY THERE IS A MARKER ──────────────────────────────────────────────────────────────────────
//
// §11.7: "The rail's own state as marker-guarded JSON, so a copy pastes back into another railRF
// document and a FOREIGN PASTE IS IGNORED RATHER THAN HALF-PARSED."
//
// Both halves matter, and the second is the one that costs something when it is missing. The system
// clipboard is one shared channel: the text sitting on it may be a layout fragment, a schematic
// fragment, a Data Display config, a line someone copied out of a terminal, or a .crail somebody
// pasted out of an editor. Every one of those is JSON-shaped often enough that a permissive reader
// gets PART of it and reports success — and a document that has been half-replaced by an unrelated
// payload is worse than one that refused, because nothing says so.
//
// LayoutFragment.Marker is the same guard for the same reason, and this file is deliberately its
// shape: one constant, one envelope, one TryDeserialize that returns false for absolutely everything
// that is not this.
//
// ── IT WRITES NO CLIPBOARD CODE EITHER ─────────────────────────────────────────────────────────
//
// R-rail9-1's rule applies here by construction: this is a string, and the string goes to
// PlotExporter.SetClipboardDataAsync like every other copy in this application. Nothing in
// src/Design may reference Avalonia at all.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace CircuitRF.Design.RailRf;

/// <summary>The marker-guarded JSON a railRF copy carries. Framework-free.</summary>
public static class RailClipboard
{
    /// <summary>
    /// The guard. Versioned in the same breath as the name, on <c>LayoutFragment.Marker</c>'s own
    /// terms — a reader that met a payload it could not understand would otherwise have to guess
    /// whether it was foreign or merely newer, and those want different answers.
    /// </summary>
    public const string Marker = "circuitrf/railrf-clipboard-v1";

    private const string MarkerField   = "marker";
    private const string DocumentField = "document";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// One railRF document, wrapped.
    /// </summary>
    /// <remarks>
    /// <b>Unvalidated, deliberately</b> — see <see cref="RailDocumentIo.SerializeUnvalidated"/>.
    /// R-rail9-5: a copy always writes, including from a document that is still being built, because
    /// a copy that wrote nothing would leave the previous one on the clipboard for the next paste to
    /// find.
    ///
    /// <para>The document is nested rather than having the marker added as a sibling key, so this
    /// envelope can never collide with a field <c>.crail</c> grows later, and so a reader can tell a
    /// clipboard payload from a bare <c>.crail</c> file by shape alone.</para>
    /// </remarks>
    public static string Serialize(RailDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var envelope = new JsonObject
        {
            [MarkerField]   = Marker,
            [DocumentField] = JsonNode.Parse(RailDocumentIo.SerializeUnvalidated(document)),
        };
        return envelope.ToJsonString(JsonOpts);
    }

    /// <summary>
    /// Marker-guarded parse. <b>Anything else is a clean false</b> — arbitrary text, a layout or
    /// schematic clipboard payload, a bare <c>.crail</c> file, truncated JSON, an envelope with a
    /// different marker, or a document from a newer circuitRF. Never an exception, never a partially
    /// populated document.
    /// </summary>
    public static bool TryDeserialize(string? text, out RailDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        try
        {
            if (JsonNode.Parse(text) is not JsonObject envelope) return false;
            if (envelope[MarkerField]?.GetValue<string>() != Marker) return false;
            if (envelope[DocumentField] is not JsonObject body) return false;

            document = RailDocumentIo.DeserializeUnvalidated(body.ToJsonString());
            return true;
        }
        catch
        {
            // Every failure mode of the three calls above is the same answer: this is not ours.
            return false;
        }
    }
}
