// Typing a port's anchor on the row that prints it (owner, 2026-09-19).
//
// ── WHAT WAS WRONG ──────────────────────────────────────────────────────────────────────────────
//
// The Sources and Loads lists printed their anchor in a TextBlock. There was no other control
// anywhere in the window that named a refdes or a pin, so the answer to "how do I change what U1.VDD
// points at" was "edit the `.crail` by hand" — and pressing "+" produced a row reading `(no anchor)`
// with no way at all to give it one. The reported words were: what good is that to a user.
//
// ── WHY THE ROW'S OWN COLUMN, AND NOT A DIALOG OR A COMBO ───────────────────────────────────────
//
// Every other settable value on this window is an `InlineEditText` in the row's own grid: double
// click, type, Return commits, Escape reverts, LostFocus commits. An anchor is a value of that row
// exactly as its voltage is, so it gets the same control — a dialog for one field is a detour, and
// an editable ComboBox commits on every keystroke, which on these rows means a re-solve per
// character (`RailRfViewModel.OnRowEdited` starts the Fast loop on every committed edit).
//
// The CANDIDATES still have to be discoverable, because "U1.VDD" is not guessable — so they are on
// the field's tooltip, built from the board netlist's own pads and filtered to the rail's net. That
// is the same list the extractor will resolve the anchor against, read from the same place, so the
// tooltip cannot offer a pad the solve then cannot find.
//
// ── THE TWO FORMS STAY TWO FORMS ────────────────────────────────────────────────────────────────
//
// `RailPortAnchor` is a refdes-and-pin OR a coordinate, never both and never neither, and it refuses
// on its own account when that is broken. This parser keeps that: a comma means a coordinate (read
// through `RailLengthFormat.ParsePoint`, in the board's own display unit, which is what the row
// PRINTS), anything else is a refdes with an optional pin after the first dot, and empty is the
// unanchored row the "+" button makes — which still refuses, visibly, on the row's own flag.

using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// The anchor column of a source or load row: what it shows, what it accepts, and what its tooltip
/// offers.
/// </summary>
internal static class RailAnchorEntry
{
    /// <summary>What the row's anchor field shows. <b>Empty for an unanchored row</b>, so the
    /// field's watermark says what to type rather than the row reading "(no anchor)" as though that
    /// were a value.</summary>
    internal static string Text(RailPortAnchor anchor, RailLengthFormat format) =>
        anchor.IsPad || anchor.Point is not null ? anchor.Describe(format) : "";

    /// <summary>
    /// The anchor a typed string means, or null where it means nothing and the edit is rejected.
    /// </summary>
    /// <remarks>
    /// A rejected edit is not silently discarded: the row notifies anyway and the field snaps back
    /// to what is stored, which is the contract every other <c>InlineEditText</c> on this window
    /// already keeps (<see cref="RailSourceRowViewModel"/>'s <c>Commit</c> states why).
    /// </remarks>
    internal static RailPortAnchor? Parse(string? text, RailLengthFormat format)
    {
        string s = (text ?? "").Trim();

        // Clearing the field unanchors the row rather than being refused. The row then flags, which
        // is the state "+" creates and is a state a user has to be able to get back to.
        if (s.Length == 0) return new RailPortAnchor();

        if (s.Contains(','))
            return format.ParsePoint(s) is { } p ? new RailPortAnchor { Point = p } : null;

        int dot = s.IndexOf('.');
        if (dot < 0) return new RailPortAnchor { Refdes = s };

        string refdes = s[..dot].Trim();
        string pin    = s[(dot + 1)..].Trim();
        if (refdes.Length == 0) return null;

        return new RailPortAnchor { Refdes = refdes, Pin = pin.Length > 0 ? pin : null };
    }

    /// <summary>How many candidates a tooltip names before it stops listing them.</summary>
    private const int MaxOffered = 24;

    /// <summary>
    /// The field's tooltip — how to spell an anchor, and which ones this board actually offers.
    /// </summary>
    /// <param name="pads">The board netlist's pads, as the extractor will read them.</param>
    /// <param name="net">The rail's net, where it has one. Narrows the list to the pads that are
    /// ON this rail, which is the only useful half of a board's pad set.</param>
    /// <param name="format">The board's units, so the coordinate example is in the unit the row
    /// would print one in rather than in DBU.</param>
    internal static string Tip(IReadOnlyList<PlacedPin> pads, string? net, RailLengthFormat format)
    {
        string how =
            "Where this port sits on the board. Double-click to type it.\n\n"
          + "A REFDES AND A PIN — U1.VDD, or just BT1 where the part has one pin. A pin name that "
          + "reaches several pads (an IC's whole VDD field) resolves to all of them and ties into "
          + "one port, which is what the die sees.\n\n"
          + $"OR A COORDINATE, as a pair — {format.Point(26_500_000, 9_875_000)} — for a board with "
          + "no netlist to name a pad. It is read in the board's own unit, and a half that names "
          + "its own keeps it. One form or the other, never both.\n\n"
          + "Clear the field to unanchor the row.";

        var offered = pads
            .Where(p => p.Refdes is { Length: > 0 })
            .Where(p => net is not { Length: > 0 }
                     || string.Equals(p.Net, net, System.StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Pin is { Length: > 0 } pin ? $"{p.Refdes}.{pin}" : p.Refdes!)
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (offered.Count == 0)
            return how + "\n\nThis board's netlist names no pads"
                       + (net is { Length: > 0 } ? $" on '{net}'" : "")
                       + ", so an anchor here has to be a coordinate.";

        string list = string.Join(", ", offered.Take(MaxOffered));
        if (offered.Count > MaxOffered) list += $", … ({offered.Count} in all)";

        return how + $"\n\nOn {(net is { Length: > 0 } ? $"'{net}'" : "this board")} the netlist "
                   + $"names: {list}";
    }
}
