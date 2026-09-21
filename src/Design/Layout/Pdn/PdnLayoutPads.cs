// The BOARD ITSELF, as the PDN extractors want it — brief-authored-board-1-layout-pads.md.
//
// ── THE OTHER HALF OF PdnBoardPads, AND DELIBERATELY NOT THE SAME KIND OF KNOWLEDGE ────────────
//
// PdnBoardPads projects a companion file. This measures the artwork. Until it existed,
// RailBoardInputs.Pads had exactly one producer, so a board a user DREW — instances carrying
// designators, footprint cells carrying named pins, a Net field on every shape — opened with
// Pads = [] and degraded in four places at once: no pick list, every refdes anchor falling back to a
// coordinate, every mounting inductance a typed one, and no parts-table row. None of the four fails.
// All four degrade to the path a board with no companion files takes, which is why nobody noticed.
//
// PdnBoardPads' governing rule is that the netlist is EVIDENCE ABOUT THE ARTWORK and never geometry.
// This file is the opposite and says so: its pads ARE geometry, measured off the board, and they
// carry PdnPadSource.Artwork so every report that names one can say which claim it is reading
// (overview §1b). A pad set whose origin is not recoverable is the defect the series exists to fix.
//
// ── IT PERFORMS NO SECOND FLATTEN, PIN RESOLUTION OR TRANSFORM ─────────────────────────────────
//
// The projection already exists in two halves and the renderer performs it every frame
// (LayoutRenderer.Instances.cs:1341,1352):
//
//     var pins = CellPins.Resolve(subView, tech);
//     var (wx, wy) = LayoutInstanceTransform.TransformPoint(pin.X, pin.Y, inst, r, col);
//
// Those two functions and no others. CellPins exists precisely because three callers had grown their
// own copy of its two-branch answer and the branch that matters fires only on older cells; a fourth
// copy here would find nothing on exactly those cells, silently.
//
// ── THE ROOT'S OWN PLACEMENTS, NOT A RECURSIVE DESCENT ─────────────────────────────────────────
//
// R-ab1-1a, which is LayoutDesignFlatten's own rule for designators (R-fp4b-4b) and is right for the
// same reason: a pad belongs to the part that was PLACED on the board, and a land pattern nested
// three cells deep inside a module is that module's internal business until somebody places the
// module.

using System.IO;
using System.Linq;

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>The artwork's own pads — what a board circuitRF drew already states.</summary>
public static class PdnLayoutPads
{
    /// <summary>
    /// Every pad of every part placed on <paramref name="view"/> — its designator, the pin it is,
    /// and where the instance transform puts it.
    /// </summary>
    /// <param name="view">The board, as read. Its INSTANCES are walked; its own shapes are not pads.</param>
    /// <param name="clayPath">Where it was read from — an instance's <c>CellRef</c> is relative to the
    /// layout folder holding it, so with no path nothing resolves and nothing is contributed.</param>
    /// <param name="tech">The root stackup, which is what <see cref="CellPins"/> re-invokes a
    /// generator against for a cell written before pins were persisted.</param>
    /// <param name="portNamesOf">The ports of the component an instance's <c>SchematicId</c> names,
    /// IN PORT ORDER, or null where nothing can answer. R-ab1-4's join is against this list; with no
    /// answer, pads come out named by their pin name alone (R-ab1-4d), which is what an anchor
    /// written against a hand-placed part will spell.</param>
    /// <param name="notes">Appended with one sentence per instance that contributed nothing for a
    /// reason worth stating. <b>Returned, never posted</b> — this project's own rule.</param>
    /// <remarks>
    /// <b>Nets are not this brief's</b> (scope). Every pad here comes out with <c>Net</c> null, which
    /// is already a representable state and is exactly what a board with placement and no netlist
    /// produces today.
    /// </remarks>
    public static IReadOnlyList<PdnPad> PadsOf(
        LayoutView view, string? clayPath, Technology? tech,
        Func<string, IReadOnlyList<string>>? portNamesOf = null,
        List<string>? notes = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.Instances.Count == 0 || clayPath is not { Length: > 0 }) return [];

        string layoutDir = Path.GetDirectoryName(Path.GetFullPath(clayPath)) ?? "";

        // R-ab1-5d. The flatten's own outcomes gate the pads: a board over the ceiling comes back
        // with no artwork-derived pads and the note says so, rather than with a confident pad list
        // over geometry the run never saw. The predicate is LayoutDesignFlatten's, not a second one.
        if (LayoutDesignFlatten.ExceedsCeiling(view, layoutDir))
        {
            notes?.Add(
                $"'{Path.GetFileName(clayPath)}' holds more instance geometry than the flatten " +
                "ceiling allows, so no pad was read off its parts.");
            return [];
        }

        var pads = new List<PdnPad>();

        foreach (var inst in view.Instances)
        {
            // R-ab1-1b. Null means THIS PLACEMENT HAS NO IDENTITY TO DRAW, and R-fp4b-8c is explicit
            // that it must not be handed a fabricated one. A pad keyed on a fabricated designator is
            // worse here than no pad at all, because an anchor would then resolve to it.
            if (inst.DisplayRefDes is not { Length: > 0 } refdes) continue;

            var res = CellLayoutResolver.Resolve(inst.CellRef, layoutDir);
            if (res is not { State: CellLayoutState.Resolved, View: { } subView })
            {
                // R-ab1-1c: the flatten's own sentence, not a second wording of it.
                notes?.Add(LayoutDesignFlatten.UnresolvedNote(inst.CellRef));
                continue;
            }

            var pins = CellPins.Resolve(subView, tech);
            if (pins.Count == 0) continue;

            var names = JoinPinsToPorts(inst, refdes, pins, portNamesOf, notes);
            if (names is null) continue;   // a partial name match — refused, never filled in

            // R-ab1-1d. An ARRAY placement produces one pad per pin per cell, and the refdes is the
            // same on all of them. That is correct and worth stating, because a via fence placed as a
            // 1xN array is the shape that makes it look wrong.
            int rows = Math.Max(1, inst.Rows), cols = Math.Max(1, inst.Cols);
            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            for (int i = 0; i < pins.Count; i++)
            {
                var (x, y) = LayoutInstanceTransform.TransformPoint(pins[i].X, pins[i].Y, inst, r, c);
                pads.Add(new PdnPad(refdes, names[i], null, x, y, PdnPadSource.Artwork));
            }
        }

        return pads;
    }

    /// <summary>
    /// Every net point the artwork itself states — one per pad carrying a net, plus every
    /// <see cref="ViaShape"/> in the ROOT's own shapes that carries one.
    /// </summary>
    /// <remarks>
    /// <b>Vias included, and not an oversight</b> — <c>PdnBoardPads.NetPointsOf</c>'s own note says
    /// why: a stitching via is frequently the only thing standing on an inner-layer pour, and a net
    /// point is how <c>PdnRailRegions</c> learns that a pour it reached is the rail's. The root's
    /// vias are in <see cref="LayoutView.Shapes"/> and need no transform.
    ///
    /// <para>Until brief 2 names them, an artwork pad's <c>Net</c> is null, so on most boards this
    /// returns the vias alone. That is the honest answer and not an empty one.</para>
    /// </remarks>
    public static IReadOnlyList<PdnNetPoint> NetPointsOf(LayoutView view, IReadOnlyList<PdnPad> pads)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(pads);

        var points = new List<PdnNetPoint>();

        foreach (var pad in pads)
            if (pad.Net is { Length: > 0 } net)
                points.Add(new PdnNetPoint(net, pad.X, pad.Y));

        foreach (var shape in view.Shapes)
            if (shape is ViaShape via && via.Net is { Length: > 0 } viaNet)
                points.Add(new PdnNetPoint(viaNet, via.X, via.Y));

        return points;
    }

    // ── R-ab1-4: which pad is which pin ──────────────────────────────────────────────────────────
    //
    // Footprint R-fp3-5 guarantees pad count equals port count and says NOTHING about order. Join
    // those wrong and pad 1 gets the reference net and pad 2 gets the rail: the part still bridges
    // the two nets, still appears in the netlist, still contributes its capacitance — and
    // PdnMountingLoop's powerPad/returnPad swap, which on a symmetric 0402 is a mirror and on an
    // asymmetric part is a wrong inductance that looks entirely normal (overview §1c).

    /// <summary>
    /// The name each pin's pad takes, in <paramref name="pins"/>' order, or <c>null</c> to contribute
    /// nothing at all.
    /// </summary>
    private static string?[]? JoinPinsToPorts(
        LayoutInstance inst, string refdes, IReadOnlyList<LayoutPin> pins,
        Func<string, IReadOnlyList<string>>? portNamesOf, List<string>? notes)
    {
        IReadOnlyList<string> ports =
            inst.SchematicId is { Length: > 0 } id && portNamesOf is not null
                ? portNamesOf(id) ?? []
                : [];

        // R-ab1-4d. No SchematicId, so no ports to join to: pads come out named by their PIN NAME
        // alone. `U1.1` resolves and `U1.VDD` does not, and that is honest — nothing on that board
        // ever said VDD.
        if (ports.Count == 0) return ByPinName(pins);

        // R-ab1-4a. By NAME first — ordinal, case-insensitive, the comparison PdnMountingLoop already
        // uses for refdes and net.
        var matched = new string?[pins.Count];
        int hits = 0;
        for (int i = 0; i < pins.Count; i++)
        {
            foreach (string port in ports)
                if (port.Length > 0 &&
                    string.Equals(pins[i].Name, port, StringComparison.OrdinalIgnoreCase))
                {
                    matched[i] = port;   // the PORT's own spelling, so an anchor written against the
                    hits++;              // schematic resolves whatever case the artwork used
                    break;
                }
        }

        if (hits == pins.Count) return matched;

        // R-ab1-4c. A PARTIAL name match is a REFUSAL, never a fill-in. Two pins named A and K
        // against ports named A and anode: one matches, one does not, and completing it by index is a
        // guess about the pair that did not match. This is the Excellon-suppression class of decision
        // — the two readings differ by a swap that nothing downstream can detect.
        if (hits > 0)
        {
            notes?.Add(
                $"{refdes}: {hits} of its {pins.Count} footprint pin(s) match a port by name and the " +
                $"rest do not, so which pad is which port is a guess. Its pins are " +
                $"[{Names(pins)}] and its ports are [{string.Join(", ", ports)}]. No pad was read " +
                "for it — name the footprint's pins after the component's ports, or give the port " +
                "names the footprint already uses.");
            return null;
        }

        // R-ab1-4b. By INDEX where no name matches, and only when EVERY name fails — pad i is port i.
        // Safe precisely because R-fp3-5 has already refused any instance whose counts differ; a
        // generated chip land (pins "1" and "2" against unnamed ports) takes this branch and it is
        // the common case.
        if (ports.Count == pins.Count)
        {
            for (int i = 0; i < pins.Count; i++) matched[i] = ports[i].Length > 0 ? ports[i] : null;
            return matched;
        }

        // The counts disagree, which R-fp3-5 refuses at placement and which a hand-placed instance
        // can still carry. Index is not available and no name matched, so the pads are named by their
        // own pins and the disagreement is stated rather than resolved.
        notes?.Add(
            $"{refdes} sits on a footprint with {pins.Count} pin(s) against {ports.Count} port(s) on " +
            "the component it names, so its pads are named after the footprint's own pins rather " +
            "than after the component's ports.");
        return ByPinName(pins);
    }

    private static string?[] ByPinName(IReadOnlyList<LayoutPin> pins)
    {
        var names = new string?[pins.Count];
        for (int i = 0; i < pins.Count; i++)
            names[i] = pins[i].Name is { Length: > 0 } n ? n : null;
        return names;
    }

    private static string Names(IReadOnlyList<LayoutPin> pins) =>
        string.Join(", ", pins.Select(p => p.Name is { Length: > 0 } n ? n : "(unnamed)"));
}
