// What hangs on the mesh — railrf.md §4.3, at ω = 0
// (docs/sonnet-briefs/brief-railrf-3-mesh-extractor.md R-rail3-11 … R-rail3-13).
//
// EVERY ONE OF THESE IS AN ORDINARY ComponentModel. No new device type exists anywhere in this
// series (overview §1f) — which is a scope statement as much as a convenience: a new device type
// would need a golden-reference test and a factory registration, and railRF needs neither.
//
//   A capacitor        bridges nothing at DC   present in the netlist, contributing no DC path
//   A source           its own pad's cells     ResistorModel (the R of the R-L) + a DC voltage branch
//   A series part      the largest terms after ResistorModel at its on-resistance / DCR.
//                      the source                ELEMENTS, NEVER ANNOTATIONS.
//   A load port        across power and ref     a current injection + a PortModel for observation
//   A regulator        two solves               never two branches in one mesh (R-rail3-13)
//
// ── TWO RULES WITH TEETH ───────────────────────────────────────────────────────────────────────
//
// R-rail3-12. MORE THAN ONE SOURCE IS MORE THAN ONE BRANCH AND NOTHING ELSE. §9: "Two supplies
// feeding one net do not share in proportion to anything a designer can see; the copper decides, and
// on a compact board it decides badly. … a second source stamped at the wrong node produces A
// PLAUSIBLE NUMBER, NOT AN ERROR." So there is no special case for a second source anywhere in this
// folder, and brief 5 gates it by superposition, which is arithmetic rather than opinion.
//
// R-rail3-13. A REGULATOR IS NEVER TWO BRANCHES IN ONE MESH. §9 calls the alternative "the modelling
// mistake this arrangement exists to make impossible". The extractor extracts ONE RAIL: it takes the
// rail name, it produces that rail's netlist, and it has NO CONCEPT OF A SECOND RAIL AT ALL. Brief 5
// runs it once per rail in RailOrder's order. Nothing in this file may grow a second rail's name.

using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>
/// A part that bridges two points of the rail and, at DC, is a resistance — the protection FET at
/// its on-resistance, the ferrite at its DCR, a zero-ohm link at its own.
///
/// <para><b>This is what makes an imported board electrically continuous.</b> §2.8: on imported
/// artwork the copper stops at every pad, so the board is not continuous until the user has said
/// what bridges each gap. A series part IS that statement, and at DC it is the largest term after
/// the source — 350 mΩ for a protection FET against 165 mΩ for 50 mm of thin inner copper.</para>
/// </summary>
/// <param name="Refdes">The part. Named on every breakdown row it appears in.</param>
/// <param name="A">One end.</param>
/// <param name="B">The other.</param>
/// <param name="ResistanceOhms">Its DC resistance.</param>
/// <param name="Basis">Where that number came from, for the provenance — "the part library's
/// on-resistance", "typed on the parts table".</param>
public sealed record PdnSeriesElement(
    string Refdes, RailPortAnchor A, RailPortAnchor B, double ResistanceOhms, string Basis);

/// <summary>
/// A part that bridges the rail to its reference — a decoupling capacitor.
///
/// <para><b>It is in the netlist and it carries no DC path</b> (R-rail3-4). That is not an omission
/// to be tidied up later: a capacitor bridges nothing at DC, which is correct and occasionally
/// surprising, and a capacitor QUIETLY LEFT OUT would make brief 14's netlist a different netlist
/// from this one.</para>
/// </summary>
/// <param name="Refdes">The part.</param>
/// <param name="Anchor">Its pad on the rail.</param>
/// <param name="CapacitanceFarads">The capacitance the part library resolved, or null where the
/// library has no row — counted and reported, never defaulted.</param>
public sealed record PdnShuntPart(string Refdes, RailPortAnchor Anchor, double? CapacitanceFarads);

/// <summary>Resolving an anchor to the pads it names.</summary>
public static class PdnAttachments
{
    /// <summary>
    /// Every pad <paramref name="anchor"/> names.
    ///
    /// <para><b>A pin FIELD is the point.</b> <c>U1.VDD</c> where <c>VDD</c> reaches six pads
    /// resolves to six pads and railRF ties them into one port, because that is what the die sees
    /// (§2.2). A refdes with no pin resolves to every pad of that part; a refdes with a pin resolves
    /// by pin name first and, where nothing matches, by NET at that refdes — which is how a pin
    /// field is spelled when a netlist numbers its pads and the user knows only the rail's
    /// name.</para>
    ///
    /// <para>A coordinate anchor resolves to itself. It is the FALLBACK rather than the spelling —
    /// see <see cref="RailPortAnchor"/> for why — and nothing here prefers one silently: the
    /// document already refused an anchor carrying both.</para>
    /// </summary>
    public static IReadOnlyList<(long X, long Y)> Resolve(
        RailPortAnchor anchor, IReadOnlyList<PlacedPin> pads) =>
        [.. ResolveLands(anchor, pads).Select(l => (l.X, l.Y))];

    /// <summary>
    /// <see cref="Resolve"/>, with the layer each point's copper is on where that is known — what the
    /// region walk SEEDS from (R-rail34-1, R-rail34-2).
    /// </summary>
    /// <remarks>
    /// A pad gives its land's layer (<see cref="PlacedPin.Layer"/>), a coordinate the layer its anchor
    /// states (<see cref="RailPortAnchor.Layer"/>); either may be null, and a null seeds every copper
    /// layer at the point but the reference, which is what every anchor did before.
    /// </remarks>
    public static IReadOnlyList<(long X, long Y, LayerKey? Layer)> ResolveLands(
        RailPortAnchor anchor, IReadOnlyList<PlacedPin> pads)
    {
        if (anchor.Point is { } xy) return [(xy.X, xy.Y, anchor.Layer)];
        if (anchor.Refdes is not { Length: > 0 } refdes) return [];

        var byPin = new List<(long X, long Y, LayerKey? Layer)>();
        var byNet = new List<(long X, long Y, LayerKey? Layer)>();

        foreach (var pad in pads)
        {
            if (!string.Equals(pad.Refdes, refdes, StringComparison.OrdinalIgnoreCase)) continue;

            if (anchor.Pin is not { Length: > 0 } pin) { byPin.Add((pad.X, pad.Y, pad.Layer)); continue; }

            if (string.Equals(pad.Pin, pin, StringComparison.OrdinalIgnoreCase))
                byPin.Add((pad.X, pad.Y, pad.Layer));
            else if (string.Equals(pad.Net, pin, StringComparison.OrdinalIgnoreCase))
                byNet.Add((pad.X, pad.Y, pad.Layer));
        }

        return byPin.Count > 0 ? byPin : byNet;
    }

    /// <summary>The refusal for an anchor that names no copper, or null. <b>It names what would
    /// answer it</b> — the house spelling `convert` and `em` already set.</summary>
    public static string? RefusalForUnresolved(
        string where, RailPortAnchor anchor, int padCount, RailLengthFormat? format = null) =>
        padCount > 0
            ? null
            : anchor.IsPad
                ? $"{where} names {anchor.Describe(format)}, and no pad of that reference is on this " +
                  "board. Check the reference against the board netlist, or give a coordinate " +
                  "instead."
                : $"{where} names {anchor.Describe(format)}, which is not on the rail's copper. Move " +
                  "it onto the rail, or give the refdes and pin the pad is under.";
}
