using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// Copy and paste of a MIXED selection — bond wires and layout geometry together (wbond.md §6.7).
///
/// <para><b>An envelope around the two existing payloads, never a third representation of either.</b>
/// The wires travel as the same <see cref="WBondClipboard.Payload"/> a wires-only copy writes, and the
/// geometry as the same <see cref="LayoutFragment.Payload"/> the Layout Editor already writes. Nothing
/// here re-encodes a shape or a wire, so a mixed copy cannot drift from a single-kind one — and the
/// two single-kind paste paths keep working on their own markers, untouched.</para>
///
/// <para><b>The envelope is only used when the selection genuinely spans both.</b> A wires-only copy
/// writes the plain wBond payload and a geometry-only copy the plain layout fragment, so pasting into
/// another wBond editor, or into the Layout Editor, still works exactly as before. Wrapping
/// unconditionally would have made every copy unreadable by every existing paste path.</para>
///
/// <para>Marker-guarded on <c>DataFormat.Text</c> like every other clipboard payload in this codebase
/// — an in-process typed format writes nothing to NSPasteboard and crashes macOS drag-and-drop.</para>
/// </summary>
public static class WBondMixedClipboard
{
    /// <summary>Checked before anything else in the blob is trusted.</summary>
    public const string Marker = "circuitrf/wbond-mixed-clipboard-v1";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The two halves, either of which may be absent.</summary>
    public sealed class Payload
    {
        public string? Marker { get; set; }

        /// <summary>A serialized <see cref="WBondClipboard.Payload"/>, or null when no wire was selected.</summary>
        public string? Wires { get; set; }

        /// <summary>A serialized <see cref="LayoutFragment.Payload"/>, or null when no shape was selected.</summary>
        public string? Layout { get; set; }
    }

    /// <summary>
    /// Builds the clipboard text for whatever is selected.
    ///
    /// <para>Returns the PLAIN single-kind payload when only one kind is present, and the mixed
    /// envelope only when both are — so the common case stays readable by every existing paste path.
    /// Null when nothing at all is selected.</para>
    /// </summary>
    public static string? Compose(string? wiresJson, string? layoutJson)
    {
        bool hasWires = !string.IsNullOrWhiteSpace(wiresJson);
        bool hasLayout = !string.IsNullOrWhiteSpace(layoutJson);

        if (!hasWires && !hasLayout) return null;
        if (hasWires && !hasLayout) return wiresJson;
        if (!hasWires && hasLayout) return layoutJson;

        return JsonSerializer.Serialize(
            new Payload { Marker = Marker, Wires = wiresJson, Layout = layoutJson }, JsonOpts);
    }

    /// <summary>
    /// Reads a mixed envelope. Returns false for anything else — including a plain single-kind
    /// payload, which the caller then hands to its own parser.
    /// </summary>
    public static bool TryParse(string? text, out Payload payload)
    {
        payload = new Payload();
        if (string.IsNullOrWhiteSpace(text)) return false;

        try
        {
            // System.Text.Json ignores properties a type does not declare, so a foreign blob (a
            // schematic clipboard, a wires-only payload, arbitrary JSON) deserializes into an
            // all-default Payload whose Marker is null and fails the check below.
            var parsed = JsonSerializer.Deserialize<Payload>(text, JsonOpts);
            if (parsed?.Marker != Marker) return false;

            payload = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;   // not JSON at all: not ours, and not an error worth surfacing
        }
    }

    /// <summary>True when the clipboard holds a mixed payload — used to describe what a paste will do.</summary>
    public static bool IsMixed(string? text) => TryParse(text, out _);

    /// <summary>
    /// Splits clipboard text into the wire half and the layout half — <b>the one place any paste path
    /// turns "whatever is on the clipboard" into payloads it can try.</b>
    ///
    /// <para>A mixed envelope yields its two halves. Anything else yields the text UNCHANGED as both,
    /// because each single-kind parser already rejects what is not its own — so a plain layout
    /// fragment handed to the wire parser is simply refused, and vice versa. That is what makes
    /// "paste whatever I copied into whatever editor I am in" true without either editor knowing what
    /// the other can hold: each takes the part it understands and ignores the rest.</para>
    /// </summary>
    public static (string? Wires, string? Layout) Unwrap(string? text) =>
        TryParse(text, out var payload) ? (payload.Wires, payload.Layout) : (text, text);
}
