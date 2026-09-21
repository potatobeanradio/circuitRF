// WHERE EVERY PLACED PART'S TERMINALS ARE — brief-authored-board-1-layout-pads.md, promoted to the
// shared extraction by brief-lvs-2-shared-extraction.md R-lvs2-1.
//
// Was `PlacedPins.Of` in Layout/Pdn/. Same walk, same join, same answers; what it gained is
// PinNaming (R-lvs2-2) and a namespace that does not belong to one of its two readers.
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
// THREE OF THE FOUR WERE THIS FILE'S TO FIX AND THE FOURTH WAS NOT. Producing the pads is what the
// first three needed; nothing CONSUMED them to make a parts-table row, so a board with fifty-five
// placed footprints still opened with column headings over nothing. RailPartDiscovery (brief 26) is
// that consumer, and it is where the fourth degradation is closed — this file supplies its input.
//
// PdnBoardPads' governing rule is that the netlist is EVIDENCE ABOUT THE ARTWORK and never geometry.
// This file is the opposite and says so: its pads ARE geometry, measured off the board, and they
// carry PinSource.Artwork so every report that names one can say which claim it is reading
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

using CircuitRF.Design.Layout.Pdn;

namespace CircuitRF.Design.Layout.Extraction;

/// <summary>The artwork's own pads — what a board circuitRF drew already states.</summary>
public static class PlacedPins
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
    /// <param name="naming">Which claims may name a pin's net — R-lvs2-2a. Required and without a
    /// default, because the wrong one here has no symptom: see <see cref="PinNaming"/>.</param>
    /// <param name="portNamesOf">The ports of the component an instance's <c>SchematicId</c> names,
    /// IN PORT ORDER, or null where nothing can answer. R-ab1-4's join is against this list; with no
    /// answer, pads come out named by their pin name alone (R-ab1-4d), which is what an anchor
    /// written against a hand-placed part will spell.</param>
    /// <param name="notes">Appended with one sentence per instance that contributed nothing for a
    /// reason worth stating. <b>Returned, never posted</b> — this project's own rule.</param>
    /// <param name="portNetsOf">The nets those same ports bind, IN THE SAME ORDER — brief 2's
    /// R-ab2-1c, <c>Instance.NetBindings</c> verbatim. Supplied together with
    /// <paramref name="portNamesOf"/> by one builder, so the "same order" contract is held at one
    /// site rather than asserted at two.</param>
    /// <param name="stamped">The board's copper partitioned and joined to the names its shapes
    /// state — brief 2's R-ab2-2. Consulted only where the schematic did not answer, which is the
    /// owner's rule of 2026-09-20 in one line of code.</param>
    /// <param name="extents">Filled, when supplied, with each pad's own extent in DBU — the
    /// footprint pin's stated width. It is a side channel rather than a seventh member of
    /// <see cref="PlacedPin"/> because a netlist pad has no extent to state, and the one consumer is
    /// <c>PdnBoardDivergence</c>'s position comparison.</param>
    /// <param name="origins">Filled, when supplied, with one <see cref="PlacedPinOrigin"/> per
    /// returned pad, in the same order — R-lvs3-5. See that type for why it is a side channel and
    /// a list.</param>
    /// <param name="scope">Which placements to walk — R-lvs3-5b. The default is railRF's, which is
    /// the one that existed before LVS asked.</param>
    public static IReadOnlyList<PlacedPin> Of(
        LayoutView view, string? clayPath, Technology? tech,
        PinNaming naming,
        Func<string, IReadOnlyList<string>>? portNamesOf = null,
        List<string>? notes = null,
        Func<string, IReadOnlyList<string>>? portNetsOf = null,
        CopperPieces? stamped = null,
        IDictionary<PlacedPin, long>? extents = null,
        IList<PlacedPinOrigin>? origins = null,
        PlacementScope scope = PlacementScope.Designated)
    {
        ArgumentNullException.ThrowIfNull(view);

        // R-lvs2-2c. A parameter that is silently ignored in one mode is a parameter somebody will
        // supply and believe in — and believing in it here means LVS asking the artwork what the
        // artwork says and getting the SCHEMATIC's answer back, on every net of every design, with
        // nothing anywhere that looks wrong (overview §1a).
        if (naming == PinNaming.ArtworkOnly && (portNamesOf is not null || portNetsOf is not null))
            throw new ArgumentException(
                "PinNaming.ArtworkOnly reads the artwork and nothing else, so it takes no " +
                "schematic-facing delegate. Pass PinNaming.SchematicThenArtwork to let the " +
                "schematic answer, or drop the delegates.", nameof(naming));

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

        var pads = new List<PlacedPin>();

        for (int instIndex = 0; instIndex < view.Instances.Count; instIndex++)
        {
            var inst = view.Instances[instIndex];

            // R-ab1-1b. Null means THIS PLACEMENT HAS NO IDENTITY TO DRAW, and R-fp4b-8c is explicit
            // that it must not be handed a fabricated one. A pad keyed on a fabricated designator is
            // worse here than no pad at all, because an anchor would then resolve to it.
            //
            // R-lvs3-5b widened this and did not weaken it: PlacementScope.EveryPlacement still
            // fabricates nothing — the pad simply comes back with a null Refdes, which is a state
            // PlacedPin has always been able to say. See PlacementScope.
            string? refdes = inst.DisplayRefDes is { Length: > 0 } d ? d : null;
            if (refdes is null && scope == PlacementScope.Designated) continue;

            var res = CellLayoutResolver.Resolve(inst.CellRef, layoutDir);
            if (res is not { State: CellLayoutState.Resolved, View: { } subView })
            {
                // R-ab1-1c: the flatten's own sentence, not a second wording of it.
                notes?.Add(LayoutDesignFlatten.UnresolvedNote(inst.CellRef));
                continue;
            }

            var pins = CellPins.Resolve(subView, tech);
            if (pins.Count == 0) continue;

            IReadOnlyList<string> ports = Lookup(portNamesOf, inst.SchematicId);
            IReadOnlyList<string> nets  = Lookup(portNetsOf,  inst.SchematicId);

            var joined = JoinPinsToPorts(inst, refdes ?? UnnamedPlacement, pins, ports, nets.Count, notes);
            if (joined is null) continue;   // a partial name match — refused, never filled in

            // R-ab1-1d. An ARRAY placement produces one pad per pin per cell, and the refdes is the
            // same on all of them. That is correct and worth stating, because a via fence placed as a
            // 1xN array is the shape that makes it look wrong.
            int rows = Math.Max(1, inst.Rows), cols = Math.Max(1, inst.Cols);
            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            for (int i = 0; i < pins.Count; i++)
            {
                var (x, y) = LayoutInstanceTransform.TransformPoint(pins[i].X, pins[i].Y, inst, r, c);
                int port = joined[i];

                // The PORT's own spelling where it has one, so an anchor written against the
                // schematic resolves whatever case the artwork used; the PIN's where it does not,
                // because a nameless port has no name to give and `U1.1` is what an anchor written
                // against a generated chip land will spell (R-ab1-4d's reasoning, per port).
                string? name = port >= 0 && port < ports.Count && ports[port].Length > 0
                    ? ports[port]
                    : pins[i].Name is { Length: > 0 } pn ? pn : null;

                // THE RULE, IN ONE LINE (brief 2 §0): the schematic's own binding where a schematic
                // resolves, else the net stated on the copper this pad lands on — on its OWN layer
                // (R-ab2-2d). A pad on unnamed copper stays unnamed, which is representable and is
                // what a board with placement and no netlist produces today.
                // R-lvs2-2a spells the precedence at the call site. In ArtworkOnly `nets` is
                // empty by construction — the delegate is refused above — and the test is written
                // out anyway, because "empty by construction" is a property of two other lines.
                string? net = naming == PinNaming.SchematicThenArtwork
                              && port >= 0 && port < nets.Count
                              && nets[port] is { Length: > 0 } bound
                    ? bound
                    : stamped?.NameAt(x, y, pins[i].Layer);

                var pad = new PlacedPin(refdes, name, net, x, y, PinSource.Artwork);
                pads.Add(pad);
                if (extents is not null && pins[i].WidthDbu > 0) extents[pad] = pins[i].WidthDbu;
                origins?.Add(new PlacedPinOrigin(
                    instIndex, res.ResolvedCellDir!, PinKeyOf(pins, i), pins[i].Layer, r, c)
                {
                    WidthDbu = pins[i].WidthDbu,
                });
            }
        }

        return pads;
    }

    /// <summary>
    /// R-rail27-3b — <b>which land pattern each placed part sits on</b>, by designator.
    /// </summary>
    /// <remarks>
    /// <b>The board knows what the bill of materials would have said.</b> A discovered part row on a
    /// board with no BOM and no part library shows nothing in the footprint column — and that is the
    /// one column that would let somebody GROUP the rows they are about to assign a part number to.
    /// Each placed instance names its land-pattern cell, and that name is the token
    /// <c>FootprintTokens</c> already matches case codes out of.
    ///
    /// <para><b>The cell's NAME, not a resolution.</b> This is the last segment of the instance's
    /// <c>CellRef</c> — no file is opened, no pins are resolved, and an unresolvable reference still
    /// contributes its name, because the name is the whole answer here. <see cref="Of"/> resolves
    /// because it needs the pins; this does not.</para>
    ///
    /// <para><b>The ROOT's own placements</b>, exactly as <see cref="Of"/> walks them (R-ab1-1a):
    /// a land pattern nested three cells deep inside a module is that module's internal business.
    /// A placement with no designator to draw contributes nothing, for R-ab1-1b's reason.</para>
    /// </remarks>
    public static IReadOnlyDictionary<string, string> FootprintsOf(LayoutView? view)
    {
        var byRefdes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (view is null) return byRefdes;

        foreach (var inst in view.Instances)
        {
            if (inst.DisplayRefDes is not { Length: > 0 } refdes) continue;
            if (inst.CellRef is not { Length: > 0 } cellRef) continue;

            string name = cellRef.Split('/', '\\')[^1];
            if (name.Length > 0) byRefdes[refdes] = name;
        }

        return byRefdes;
    }

    /// <summary>
    /// Every net point the artwork itself states — one per pad carrying a net, plus every
    /// <see cref="ViaShape"/> in the ROOT's own shapes that carries one.
    /// </summary>
    /// <remarks>
    /// <b>Vias included, and not an oversight</b> — <c>PdnBoardPads.NetPointsOf</c>'s own note says
    /// why: a stitching via is frequently the only thing standing on an inner-layer pour, and a net
    /// point is how <c>Regions</c> learns that a pour it reached is the rail's. The root's
    /// vias are in <see cref="LayoutView.Shapes"/> and need no transform.
    ///
    /// <para><b>A via FOLLOWS ITS PIECE</b> (R-ab2-2e). Before brief 2 a via contributed a point
    /// only where somebody had stamped that very via, which on a stitching fence means stamping
    /// thirty of them; now naming the trace they land on names them all, which is what lets
    /// <c>Regions</c> recognise an inner-layer pour it reached. The via's OWN stamp still
    /// wins where it has one — it is the more specific statement.</para>
    /// </remarks>
    /// <param name="stamped">The partition, where one was built. Null leaves the pre-brief-2
    /// behaviour exactly as it was.</param>
    public static IReadOnlyList<PdnNetPoint> NetPointsOf(
        LayoutView view, IReadOnlyList<PlacedPin> pads, CopperPieces? stamped = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(pads);

        var points = new List<PdnNetPoint>();

        foreach (var pad in pads)
            if (pad.Net is { Length: > 0 } net)
                points.Add(new PdnNetPoint(net, pad.X, pad.Y));

        foreach (var shape in view.Shapes)
        {
            if (shape is not ViaShape via) continue;
            // Any layer, deliberately: a via IS the thing that joins them, so asking which one it is
            // on is the wrong question.
            string? net = via.Net is { Length: > 0 } own ? own : stamped?.NameAt(via.X, via.Y, null);
            if (net is { Length: > 0 }) points.Add(new PdnNetPoint(net, via.X, via.Y));
        }

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
    /// Which PORT each pin is, in <paramref name="pins"/>' order — <c>-1</c> where nothing answered,
    /// or <c>null</c> to contribute nothing at all.
    /// </summary>
    /// <remarks>
    /// <b>Indices rather than names</b> since brief 2: the pin's NAME and the pin's NET are two
    /// answers off one join, and computing the join twice is how the two would come to disagree
    /// about which pad is which port (<c>Instance.NetBindings</c> is in port order, so the net is
    /// simply <c>nets[port]</c>).
    /// </remarks>
    /// <param name="portCount">How many ports the component has, which is the greater of the two
    /// lists the caller supplied — a primitive declares no port NAMES and still binds a net per
    /// port, and the index branch below needs the count either way.</param>
    private static int[]? JoinPinsToPorts(
        LayoutInstance inst, string refdes, IReadOnlyList<LayoutPin> pins,
        IReadOnlyList<string> ports, int netCount, List<string>? notes)
    {
        int portCount = Math.Max(ports.Count, netCount);

        var idx = new int[pins.Count];
        Array.Fill(idx, -1);

        // R-ab1-4d. No SchematicId, so no ports to join to: pads come out named by their PIN NAME
        // alone. `U1.1` resolves and `U1.VDD` does not, and that is honest — nothing on that board
        // ever said VDD.
        if (portCount == 0) return idx;

        // R-ab1-4a. By NAME first — ordinal, case-insensitive, the comparison PdnMountingLoop already
        // uses for refdes and net.
        int hits = 0;
        for (int i = 0; i < pins.Count; i++)
        {
            for (int p = 0; p < ports.Count; p++)
                if (ports[p].Length > 0 &&
                    string.Equals(pins[i].Name, ports[p], StringComparison.OrdinalIgnoreCase))
                {
                    idx[i] = p;
                    hits++;
                    break;
                }
        }

        if (hits == pins.Count) return idx;

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
        if (portCount == pins.Count)
        {
            for (int i = 0; i < pins.Count; i++) idx[i] = i;
            return idx;
        }

        // The counts disagree, which R-fp3-5 refuses at placement and which a hand-placed instance
        // can still carry. Index is not available and no name matched, so the pads are named by their
        // own pins and the disagreement is stated rather than resolved.
        notes?.Add(
            $"{refdes} sits on a footprint with {pins.Count} pin(s) against {portCount} port(s) on " +
            "the component it names, so its pads are named after the footprint's own pins rather " +
            "than after the component's ports.");
        return idx;
    }

    /// <summary>The delegate's answer for one instance, or an empty list — never null.</summary>
    private static IReadOnlyList<string> Lookup(
        Func<string, IReadOnlyList<string>>? source, string? schematicId) =>
        schematicId is { Length: > 0 } id && source is not null ? source(id) ?? [] : [];

    /// <summary>What a placement with no designator is CALLED in a note. It is never written into
    /// a pad: <see cref="PlacedPin.Refdes"/> stays null, per R-fp4b-8c.</summary>
    private const string UnnamedPlacement = "(a placement with no designator)";

    /// <summary>
    /// How <c>TerminalMap</c> names layout pin <paramref name="index"/> — its own name, or
    /// <c>#n</c> for an unnamed one.
    /// </summary>
    /// <remarks>
    /// <b>THE spelling, and <c>TerminalMap</c> calls it rather than keeping a second copy.</b> A
    /// terminal's <c>LayoutPin</c> list holds these strings and LVS joins a terminal to a pad by
    /// comparing them, so two spellings of one key would match nothing at all — which reads as
    /// every device being open, on a board that is perfectly connected.
    /// </remarks>
    public static string PinKeyOf(IReadOnlyList<LayoutPin> pins, int index)
        => pins[index].Name.Length > 0 ? pins[index].Name : "#" + (index + 1);

    private static string Names(IReadOnlyList<LayoutPin> pins) =>
        string.Join(", ", pins.Select(p => p.Name is { Length: > 0 } n ? n : "(unnamed)"));
}
