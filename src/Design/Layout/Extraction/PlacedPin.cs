// One terminal of one placed part, located on the board — brief-lvs-2-shared-extraction.md R-lvs2-1.
//
// Was `PdnPad`/`PdnPadSource`/`PdnPadSummary` in Layout/Pdn/PdnAttachments.cs. It is the SAME
// record: a renamed, promoted copy of what railRF has always produced, now in the namespace LVS
// reads copper through as well. Nothing about its meaning changed — a pad of a footprint and a
// terminal of a device are one object seen from two sides, and the neutral name is the one both
// readers can say.
//
// THE OLD NAMES DO NOT SURVIVE AS ALIASES (R-lvs2-1d). No [Obsolete] forwarder, no using alias: a
// type with two names is two types as far as a later reader is concerned, and this repository has
// already paid for that with three copies of a version number.

namespace CircuitRF.Design.Layout.Extraction;

/// <summary>
/// Which kind of knowledge a <see cref="PlacedPin"/> is — R-ab1-2a, extended by R-lvs2-2d.
///
/// <para><b>A netlist can disagree with the board and a projection of the board cannot.</b> That is
/// the whole distinction (railRF overview §1b): <c>PdnBoardPads</c> reads a companion file that is
/// EVIDENCE ABOUT the artwork, while <see cref="PlacedPins"/> measures the artwork itself. The two
/// have different failure modes — a netlist goes stale against a board somebody edited afterwards,
/// and a layout cannot, but a layout can be missing a net name where a netlist never is — so every
/// report that names a pin is entitled to say which it is reading.</para>
/// </summary>
public enum PinSource
{
    /// <summary>Stated by the board netlist — an <c>.ipc</c> the board tool wrote.</summary>
    BoardNetlist,

    /// <summary>Computed from the artwork: a placement's designator, its footprint cell's pins, and
    /// the instance transform. <b>An LVS layout pin is always this one.</b></summary>
    Artwork,

    /// <summary>
    /// Read off a schematic — R-lvs2-2d. Nothing in THIS brief produces one; it exists so brief 4's
    /// schematic-side netlist can carry its terminals in the same type without lying about where
    /// they came from.
    /// </summary>
    Schematic,
}

/// <summary>
/// One pad of the board, as a companion file knows it. <c>BoardNetlistRecord</c> maps onto this
/// directly and so does a placement file joined to a footprint.
/// </summary>
/// <param name="Refdes">The component reference — <c>U1</c>, <c>BT1</c>.</param>
/// <param name="Pin">The pin, as the netlist or the footprint names it.</param>
/// <param name="Net">The net it is on, where the file said.</param>
/// <param name="X">DBU, on the artwork's own coordinate system.</param>
/// <param name="Y">DBU.</param>
/// <param name="Source">Which kind of knowledge this is. <b>Required, positional, and deliberately
/// without a default</b> (R-ab1-2b, kept for its reason at R-lvs2-2d): adding it broke every
/// construction site, which is the point — each one has to state which claim it is making, and a
/// default would let a new site drift in unmarked.</param>
public readonly record struct PlacedPin(
    string? Refdes, string? Pin, string? Net, long X, long Y, PinSource Source);

/// <summary>How a pad set reads on a status strip and on a provenance banner — R-ab1-6c.</summary>
/// <remarks>
/// <b>Said once, here</b>, because the window's strip and the verb's banner report the same board and
/// two spellings of one count is the divergence nobody notices until they are compared. The
/// three-way spelling is R-ab1-3b made visible: a netlist naming eleven of thirteen parts reads
/// "eleven from the board netlist, two from the artwork", and that sentence is the only way a user
/// finds out their netlist is two parts stale.
/// </remarks>
public static class PlacedPinSummary
{
    /// <summary>The sentence, or empty where there are no pads at all.</summary>
    public static string Describe(IReadOnlyList<PlacedPin> pads)
    {
        ArgumentNullException.ThrowIfNull(pads);
        if (pads.Count == 0) return "";

        int netlist = 0;
        foreach (var p in pads) if (p.Source == PinSource.BoardNetlist) netlist++;
        int artwork = pads.Count - netlist;

        string count = $"{pads.Count} pad{(pads.Count == 1 ? "" : "s")}";
        return artwork == 0 ? $"{count}, from the board netlist"
             : netlist == 0 ? $"{count}, from the artwork"
             : $"{count}: {netlist} from the board netlist, {artwork} from the artwork";
    }
}
