// The board netlist, as the PDN extractors want it — railrf.md §2.2, §4.3.
//
// ── THE LINK THAT WAS NEVER DRAWN ──────────────────────────────────────────────────────────────
//
// PdnAttachments' own summary of PdnPad says "BoardNetlistRecord maps onto this directly", and until
// this file existed nothing performed that mapping. The consequence was invisible and total:
// RailBoardInputs.Pads was assigned NOWHERE in src/, so it was always empty, so
//
//   * every refdes anchor on a source or a load resolved to no copper and fell back to a refusal,
//     which is why the shipped Power Rail example's ports are all coordinates;
//   * PdnMountingLoopExtractor — the whole of brief 13's L_p + L_r − 2M + L_pad — answered "the
//     board netlist has no pad for it" for every part on every board, so EVERY mounting inductance
//     railRF has ever reported was a typed one, and RailMountingBasis.ComputedFromGeometry was a
//     value nothing could produce.
//
// Neither failed. Both degraded to the typed path, which is the same path a document with no netlist
// takes, so there was nothing on any report to say the netlist had been read and then dropped.
//
// ── IT IS A PROJECTION, NEVER A READER AND NEVER GEOMETRY ──────────────────────────────────────
//
// BoardNetlistFile's own governing rule (R-gi5-1) is that the netlist is EVIDENCE ABOUT THE ARTWORK
// and never geometry. This file inherits that rule intact: it creates no shape, moves no shape, and
// its coordinates are the netlist's own, already in DBU on the artwork's coordinate system because
// BoardNetlistFile.Read resolved the units and cross-checked them against the artwork's extent. What
// comes out is a projection of records that are already there.
//
// ── WHY A COMPONENT HOLE AND A SURFACE PAD ARE BOTH PADS, AND A VIA IS NOT ─────────────────────
//
// R-gi5-3 makes the distinction on FIELDS: a record carrying a component reference and a pin belongs
// to a part; a record carrying a net and no component reference is a via. A decoupling capacitor on
// a surface-mount land carries no drill at all, so HasHole is false for it and IsComponentHole is
// false too — which is why the test here is the component reference and the pin, NOT IsComponentHole.
// Using that property would have silently dropped every surface-mount part on the board, leaving
// only the through-hole ones, which on a modern board is close to none of them.

namespace CircuitRF.Design.Layout.Pdn;

using CircuitRF.Design.Layout.Interchange;

/// <summary>The board netlist's records, as the PDN side reads them.</summary>
public static class PdnBoardPads
{
    /// <summary>
    /// Every record that belongs to a part — its refdes, its pin, its net and where it is.
    /// </summary>
    /// <remarks>
    /// <b>A refused netlist contributes nothing.</b> <see cref="BoardNetlist.Refusal"/> non-null
    /// means nothing was read and nothing may be used, which is the contract that type states; a
    /// half-read netlist mislabels pads rather than failing, and a mislabelled pad is a part whose
    /// mounting loop is computed from a neighbour's via.
    /// </remarks>
    public static IReadOnlyList<PdnPad> PadsOf(BoardNetlist? netlist)
    {
        if (netlist is not { Refusal: null }) return [];

        var pads = new List<PdnPad>();
        foreach (var r in netlist.Records)
        {
            // The component reference AND the pin, rather than IsComponentHole — see this file's
            // header. A surface-mount land has no drill and is still this part's pad.
            if (r.Component is not { Length: > 0 } refdes || r.Pin is not { Length: > 0 } pin)
                continue;

            pads.Add(new PdnPad(refdes, pin, r.Net, r.X, r.Y, PdnPadSource.BoardNetlist));
        }

        return pads;
    }

    /// <summary>
    /// Every record that names a net, as a point the connectivity walk can be seeded from.
    /// </summary>
    /// <remarks>
    /// Vias included, and on purpose: a net point is how <c>PdnRailRegions</c> learns that a pour it
    /// reached is the rail's rather than the next net's, and a stitching via is frequently the only
    /// record standing on an inner-layer pour at all.
    /// </remarks>
    public static IReadOnlyList<PdnNetPoint> NetPointsOf(BoardNetlist? netlist)
    {
        if (netlist is not { Refusal: null }) return [];

        var points = new List<PdnNetPoint>();
        foreach (var r in netlist.Records)
            if (r.Net is { Length: > 0 } net)
                points.Add(new PdnNetPoint(net, r.X, r.Y));

        return points;
    }
}
