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
    string? Refdes, string? Pin, string? Net, long X, long Y, PinSource Source)
{
    /// <summary>
    /// The drawing layer the pad's LAND is on, where that is known — R-rail34-1.
    /// </summary>
    /// <remarks>
    /// <b>A refdes anchor seeds this layer and no other.</b> A pad is a coordinate, and anything else
    /// under it on another layer — a 3v3 pour under a VDD pad — is a different net that seeding
    /// every layer would make one rail with it. Filled by <c>RailArtwork.PadsFor</c> from the
    /// placement's own <see cref="PlacedPinOrigin.Layer"/>, the place <c>PdnNetPoint.Layer</c> has
    /// it from; a board NETLIST pad has no layer to state and stays null, which seeds every layer as
    /// every pad always did. A through-hole pad needs nothing more: its barrel joins the other
    /// layers, and the connectivity walk follows it.
    /// <para>Init-only, so every existing construction compiles and means what it meant.</para>
    /// </remarks>
    public LayerKey? Layer { get; init; }
}

/// <summary>
/// Which placements <see cref="PlacedPins.Of"/> walks — <c>brief-lvs-3-layout-netlist.md</c> R-lvs3-5b.
/// </summary>
public enum PlacementScope
{
    /// <summary>
    /// Only placements that carry a designator. <b>railRF's rule</b> (R-ab1-1b): a pad keyed on a
    /// fabricated designator is worse than no pad at all, because an anchor would then resolve to
    /// it, and R-fp4b-8c is explicit that a placement with no identity to draw must not be handed
    /// one.
    /// </summary>
    Designated,

    /// <summary>
    /// Every placement, designator or not. <b>LVS keys a device on its PATH and not on a
    /// designator</b>, so a device cell placed without one — a kit part whose <c>.ccell</c>
    /// declares ports, which is R-lvs3-3a's last clause and the whole reason a user-authored PDK
    /// needs no registration — is an ordinary unnamed device there and must still contribute its
    /// terminals. Such a pad comes back with a null <see cref="PlacedPin.Refdes"/>, which the type
    /// has always been able to say.
    /// </summary>
    EveryPlacement,
}

/// <summary>
/// Where one <see cref="PlacedPin"/> came from — R-lvs3-5.
/// </summary>
/// <remarks>
/// <b>A side channel, for the same reason <c>extents</c> is one.</b> A pad projected from a board
/// NETLIST has none of this: there is no instance, no cell folder and no array cell, so a netlist
/// pad would have to carry four members it can never fill. LVS is the one reader that needs them,
/// and it needs all four at once — the instance to classify the device, the cell folder to resolve
/// its terminal map, the pin key to join a terminal's <c>LayoutPin</c> list to a pad, and the layer
/// because a pin lands on the piece under it ON ITS OWN LAYER (R-ab2-2d).
///
/// <para><b>Filled in lockstep</b>: entry <c>i</c> describes the returned pad <c>i</c>. A LIST
/// rather than a dictionary keyed on the pad, because two cells of one array can put two pads at
/// the same place with the same name and a dictionary would silently keep one.</para>
/// </remarks>
/// <param name="Instance">Index into the walked view's own <c>Instances</c>.</param>
/// <param name="CellDir">The resolved cell folder the pad's land pattern came from.</param>
/// <param name="PinKey">How <c>TerminalMap</c> names this pin — its own name, or <c>#n</c> for an
/// unnamed one. <b>The map's spelling, not a second one</b>: a terminal's <c>LayoutPin</c> list is
/// matched against this string.</param>
/// <param name="Layer">The layout pin's own drawing layer.</param>
/// <param name="Row">Which array cell, 0-based. Zero for a plain placement.</param>
/// <param name="Col">Which array cell, 0-based. Zero for a plain placement.</param>
public readonly record struct PlacedPinOrigin(
    int Instance, string CellDir, string PinKey, LayerKey Layer, int Row, int Col)
{
    /// <summary>
    /// The footprint pin's own stated width, DBU — <b>the extent a marker is drawn at</b>
    /// (<c>brief-lvs-8-findings.md</c> R-lvs8-2c). Zero where the pattern states none.
    /// </summary>
    /// <remarks>
    /// <b>Init-only, so every existing construction still compiles and still means what it
    /// meant.</b> It is the same number the <c>extents</c> side channel carries; it is repeated
    /// here because that dictionary is keyed on the pad VALUE and two cells of one array can put
    /// two identical pads at two places, which is the case this list exists to keep apart.
    /// </remarks>
    public long WidthDbu { get; init; }
}

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
