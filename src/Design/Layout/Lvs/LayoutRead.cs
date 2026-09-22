// A `.clay` plus its technology becomes an LvsNetlist — brief-lvs-3-layout-netlist.md,
// docs/design/lvs.md §4. Flat, one technology, no comparison.
//
//   LayoutView ──► FlattenTagged ──► LayerRegions ──► CopperPieces(+index)
//        │                                                 ▲
//        └─ instances ─► device tier ─► TerminalMap ─► PlacedPins ──┘
//                                                        │
//                                                        └──► LvsNetlist
//
// ── WHAT THIS FILE MAY NOT DO, AND IT IS SCANNED FOR (R-lvs3-7) ───────────────────────────────
//
// No Clipper call, no Union, no InflatePaths — geometry belongs to Extraction and Drc.
// No stackup via-span walk — DrcConnectivity's, and its GroundReach is why R-lvs3-6 needed no
//   second one here.
// No CellPins reimplementation and no LayoutInstanceTransform arithmetic done by hand — PlacedPins
//   performs the projection, and it is the ONLY one in the repository for CellPins' own reason.
// No second flatten.
// No write of any kind. LVS is read-only on `check`'s terms (R-aut4-6), so it runs on a read-only
//   tree and on a workspace another process has open.
//
// Each of those has exactly one implementation somewhere else, and the whole series is built on
// that being true (overview §0).
//
// ── NOTHING A SCHEMATIC SAYS REACHES A NET (R-lvs3-5b, overview §1a) ──────────────────────────
//
// This function takes no schematic. The pin projection is asked for in PinNaming.ArtworkOnly,
// which REFUSES a schematic-facing delegate rather than ignoring one, and the only schematic-shaped
// argument here is a set of component NAMES used for exactly one finding — R-lvs3-3d's dangling
// SchematicId — which touches no net, no terminal and no device identity. Get this wrong and LVS
// asks the artwork what the artwork says, gets the schematic's answer back, and passes every design
// with no symptom at all.

using System.IO;
using System.Linq;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout.Lvs;

/// <summary>The layout half of the comparison: artwork in, <see cref="LvsNetlist"/> out.</summary>
public static class LayoutRead
{
    /// <summary>
    /// Reads <paramref name="view"/> as a flat netlist of devices, terminals and nets.
    /// </summary>
    /// <param name="view">The root layout, as read.</param>
    /// <param name="clayPath">Where it was read from. An instance's <c>CellRef</c> is relative to
    /// the layout folder holding it, so with no path nothing resolves.</param>
    /// <param name="cellDir">The cell folder <paramref name="clayPath"/> is a view of — what the
    /// BOUNDARY pins are read against (this cell's own ports, in port order).</param>
    /// <param name="tech">The resolved root technology. Null means no stackup, so no conductors,
    /// no partition and no netlist.</param>
    /// <param name="resolveTechAt">How a sub-cell's own technology resolves, for the cross-technology
    /// reconciliation the flatten already performs. Null is the single-technology case.</param>
    /// <param name="schematicComponents">Component names the schematic has, used for R-lvs3-3d's
    /// dangling-<c>SchematicId</c> finding and for <b>nothing else</b>. Null skips that one check;
    /// supplying it cannot change a net, a terminal or a device.</param>
    public static LvsNetlist Read(
        LayoutView view, string clayPath, string cellDir, Technology? tech,
        Func<string?, string, TechResolution>? resolveTechAt = null,
        IReadOnlySet<string>? schematicComponents = null,
        LvsHierarchyContext? hierarchy = null)
        => Read(view, clayPath, cellDir, tech, out _, resolveTechAt, schematicComponents, hierarchy);

    /// <summary>
    /// The same read, and <b>where everything it found IS</b> — <c>brief-lvs-8-findings.md</c>
    /// R-lvs8-2c.
    /// </summary>
    /// <param name="geometry">
    /// The artwork's own coordinates, in the netlist's index space. <b>A side channel and not a
    /// member of <see cref="LvsNetlist"/></b>: that type serves both sides and a schematic has no
    /// DBU to fill it with, which is R-lvs3-1a's whole point. See <see cref="LvsGeometry"/>.
    /// </param>
    /// <inheritdoc cref="Read(LayoutView, string, string, Technology, Func{string, string, TechResolution}, IReadOnlySet{string})"/>
    /// <param name="hierarchy">
    /// What a HIERARCHICAL run carries — <c>brief-lvs-9-hierarchy.md</c>. <b>Null is the flat
    /// reading, exactly as it has always been</b>: every sub-cell's copper in one partition, every
    /// placement a leaf, no descent and no ceiling of its own. Supplied, the placements that are
    /// cells in their own right are read as such, their internals leave this level's partition
    /// (R-lvs9-2d), and what it cost is counted.
    /// </param>
    /// <remarks>
    /// <b>Internal because two of what it takes are</b> — the hierarchy context a caller outside
    /// this assembly cannot have, and brief 13's already-resolved wBond wires. The public reading is
    /// the overload above, which is the whole netlist minus the report's side channel.
    /// </remarks>
    internal static LvsNetlist Read(
        LayoutView view, string clayPath, string cellDir, Technology? tech,
        out LvsGeometry geometry,
        Func<string?, string, TechResolution>? resolveTechAt = null,
        IReadOnlySet<string>? schematicComponents = null,
        LvsHierarchyContext? hierarchy = null,
        IReadOnlyList<AssemblyWBond>? wbonds = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        geometry = LvsGeometry.None;
        var padGeometry = new List<LvsPadGeometry>();

        var notes = new List<Diagnostic>();
        string document = Path.GetFileName(clayPath);

        // ── R-lvs3-3e: BEFORE anything is matched ──────────────────────────────────────────────
        //
        // Two placements sharing a designator makes tier 1 meaningless, so every finding derived
        // from it is misleading and a user would chase whichever of the two the walk reached
        // first. DesignatorPool already answers the question; nothing asked it at the right time.
        foreach (var dup in DesignatorPool.NamesIn(view)
                     .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
            notes.Add(LvsDiagnostics.DuplicateDesignator(dup.Key, dup.Count()));

        // How a coordinate and a layer are SPELLED, needed before the geometry side channel exists
        // — a contact reported in bare DBU on a board drawn in millimetres is a number nobody can
        // place.
        var layerNames = new Dictionary<LayerKey, string>();
        foreach (var layer in tech?.Layers ?? []) layerNames[layer.Key] = layer.Name;
        var naming = new LvsGeometryNaming(RailRf.RailLengthFormat.For(view), layerNames);

        // ── The copper ─────────────────────────────────────────────────────────────────────────
        var flat = LayoutDesignFlatten.FlattenTagged(view, cellDir, tech, resolveTechAt, null);

        // R-lvs3-2c, RailArtwork's rule: no netlist, and the note says so.
        if (flat.ExceedsCeiling)
        {
            notes.Add(LvsDiagnostics.OverFlattenCeiling(document, LayoutDesignFlatten.HardCeiling));
            return LvsNetlist.Nothing(notes);
        }

        foreach (string unresolved in flat.UnresolvedInstances)
            notes.Add(LvsDiagnostics.UnresolvedInstance(unresolved));
        foreach (string pending in flat.PendingCrossTechMappings.Keys.OrderBy(k => k, StringComparer.Ordinal))
            notes.Add(LvsDiagnostics.PendingCrossTechMapping(pending));

        string layoutDir = Path.GetDirectoryName(Path.GetFullPath(clayPath)) ?? "";

        // Every placement resolved ONCE and read three times — to classify it, to decide whether
        // its copper joins this partition, and to type its device. CellLayoutResolver caches, so a
        // second Resolve would be cheap; what three separate walks would cost is three chances for
        // the three answers to disagree about what a placement IS.
        var placed = new PlacedCell[view.Instances.Count];
        for (int i = 0; i < view.Instances.Count; i++)
        {
            var r = CellLayoutResolver.Resolve(view.Instances[i].CellRef, layoutDir);
            placed[i] = r.State == CellLayoutState.Resolved
                ? new PlacedCell(r.View, r.ResolvedCellDir)
                : new PlacedCell(null, null);
        }

        // ── R-lvs9-2c: which placements are cells of their own ─────────────────────────────────
        var modules = LayoutReadHierarchy.Classify(view, placed, hierarchy, notes);

        // The stamps are the ROOT's own shapes and never a sub-cell's (R-ab2-2a): a land pattern is
        // one cell shared by every placement of it, so a Net stamped inside a C0402's `.clay` would
        // put thirteen capacitors on one net.
        //
        // ── The pins (R-lvs3-5) ────────────────────────────────────────────────────────────────
        //
        // BEFORE the partition, which is R-lvs9-2a's doing: which of a module's copper is a
        // boundary PAD is answered by where its declared pins landed, and the projection that
        // answers it is this one. It takes no `stamped` argument for that reason and needs none —
        // LVS reads a pad's POSITION and never the net name PlacedPins would have put on it, and
        // asking for a name here would be one lookup per pad that nothing goes on to read.
        var origins = new List<PlacedPinOrigin>();
        var padNotes = new List<string>();
        var pads = PlacedPins.Of(
            view, clayPath, tech, PinNaming.ArtworkOnly,
            notes: padNotes, origins: origins, scope: PlacementScope.EveryPlacement);

        foreach (string note in padNotes) notes.Add(LvsDiagnostics.UnresolvedInstance(note));

        // R-lvs9-2d: a module read as a cell keeps its internals, and only its declared boundary
        // pads join this level's copper. With no hierarchy this is every shape the flatten made,
        // byte for byte as before.
        var copper = LayoutReadHierarchy.CopperFor(flat.Shapes, view, modules, pads, origins);
        var pieces = CopperPieces.Build(copper, tech, view.Shapes);

        // R-lvs9-5a2's third counter. CopperPieces unions once per call, by LayerRegions.Build's
        // own construction, so counting the calls IS counting the unions — and what the brief
        // guarantees is that there is one per distinct CELL rather than one per placement.
        if (tech is not null && copper.Count > 0 && hierarchy is not null) hierarchy.Counters.LayerUnions++;
        foreach (string refusal in pieces.Refusals)
            notes.Add(LvsDiagnostics.ContestedNetName(refusal));

        ReportGround(tech, pieces, notes);

        // ── R-lvs9-3: undeclared contact is reported, never absorbed ───────────────────────────
        LayoutReadHierarchy.ReportUndeclaredContact(
            view, flat.Shapes, pads, origins, modules, pieces, tech, hierarchy, notes, naming);

        // (instance, row, col, pin key) -> the pads that key names. A LIST because one terminal may
        // legitimately be several pads of one name — a bonded ground, a FET's two sources.
        var padsByPin = new Dictionary<(int Inst, int Row, int Col, string Pin), List<int>>(PinKeyComparer.Instance);
        for (int i = 0; i < origins.Count; i++)
        {
            var o = origins[i];
            var key = (o.Instance, o.Row, o.Col, o.PinKey);
            if (!padsByPin.TryGetValue(key, out var list)) padsByPin[key] = list = [];
            list.Add(i);
        }

        // ── The devices ────────────────────────────────────────────────────────────────────────
        var nets = new NetTable(pieces);
        var devices = new List<LvsDevice>();

        // R-lvs14-4a: which placements OWN copper, so tier-3 recognition can be kept out of it.
        // Filled by the walk that already knows which instance is a device, rather than by asking
        // the same question a second time somewhere else and getting a different answer.
        var devicePaths = new HashSet<string>(StringComparer.Ordinal);
        var saidOnce = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unclassified = new Dictionary<string, (string Name, int Count)>(StringComparer.OrdinalIgnoreCase);

        for (int instIndex = 0; instIndex < view.Instances.Count; instIndex++)
        {
            var inst = view.Instances[instIndex];
            var subView = placed[instIndex].View;
            string? resolvedDir = placed[instIndex].CellDir;

            if (!IsDevice(inst, resolvedDir, subView))
            {
                // R-lvs3-3c. No ports, no designator, real copper — reported ONCE PER CELL TYPE,
                // because a via fence is one cell placed forty times.
                if (resolvedDir is { Length: > 0 } dir && subView is not null && DrawsCopper(subView, tech))
                {
                    string name = new DirectoryInfo(dir).Name;
                    unclassified[dir] = (name, unclassified.TryGetValue(dir, out var prior) ? prior.Count + 1 : 1);
                }
                continue;   // R-lvs3-4c: an array of an interconnect cell is copper and no devices
            }

            if (schematicComponents is not null
                && inst.SchematicId is { Length: > 0 } sid
                && !schematicComponents.Contains(sid))
                notes.Add(LvsDiagnostics.DanglingSchematicId(LayoutDesignFlatten.PathOf(inst, instIndex), sid));

            var map = resolvedDir is { Length: > 0 } cellFolder
                ? TerminalMap.ResolveCell(cellFolder)
                : new TerminalMapResult([], TerminalMapOrigin.None, ["The placement's cell does not resolve."]);

            // R-lvs3-5a. Origin None makes the device UNMATCHABLE — emitted with no terminals,
            // reported, and never dropped: a device the comparison cannot handle must still appear
            // in the count, or the two sides disagree about how many parts there are for a reason
            // the report never gave.
            if (map.Origin == TerminalMapOrigin.None
                && resolvedDir is { Length: > 0 } unmapped
                && saidOnce.Add("no-terminal-map:" + unmapped))
                notes.Add(LvsDiagnostics.NoTerminalMap(
                    new DirectoryInfo(unmapped).Name, string.Join(" ", map.Notes)));

            // R-lvs1-3c, carried into the LVS report rather than restated: a map read positionally
            // is a GUESS, and R-lvs8-3's catalogue lists it because a comparison resting on one is
            // a comparison whose every finding may name the wrong terminal. Brief 1 authored the
            // sentence and `check` already prints it; giving it a second `lvs.` id here would be
            // one fault with two contracts. Once per cell TYPE, for UnclassifiedCell's reason.
            if (map.Origin == TerminalMapOrigin.ByOrder
                && resolvedDir is { Length: > 0 } positional
                && saidOnce.Add("by-order:" + positional))
                notes.Add(TerminalDiagnostics.DerivedByOrder(
                    new DirectoryInfo(positional).Name, map.Terminals.Count));

            var type = DeviceTypes.OfLayout(inst, resolvedDir, subView);
            var (parameters, parameterFacts) = ParametersOf(subView, resolvedDir);
            devicePaths.Add(LayoutDesignFlatten.PathOf(inst, instIndex));

            // R-lvs3-4a. An MxN array is MxN devices, each with the array element's own transformed
            // pins — and all of them carry the SAME designator (R-lvs3-4b), which is correct and is
            // exactly why the PATH is the identity.
            int rows = Math.Max(1, inst.Rows), cols = Math.Max(1, inst.Cols);
            bool isArray = rows > 1 || cols > 1;
            string basePath = LayoutDesignFlatten.PathOf(inst, instIndex);

            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                string path = isArray ? $"{basePath}[{r},{c}]" : basePath;
                int deviceIndex = devices.Count;

                var (x, y) = LayoutInstanceTransform.TransformPoint(0, 0, inst, r, c);
                var terminals = new List<LvsTerminal>(map.Terminals.Count);

                foreach (var terminal in map.Terminals.OrderBy(t => t.Port))
                {
                    int net = ResolveTerminal(
                        terminal, instIndex, r, c, path,
                        padsByPin, pads, origins, pieces, tech, nets, notes,
                        deviceIndex, terminals.Count, padGeometry, hierarchy);

                    terminals.Add(new LvsTerminal(terminal.Port, terminal.Name, net));
                    nets.Attach(net, deviceIndex, terminals.Count - 1);
                }

                // R-lvs9-2b. A module placement is ONE device whose terminals are its boundary
                // pins, and the descent that compares its INSIDE is the caller's — recorded here
                // because this is the walk that knows which placement is which.
                if (modules.TryGetValue(instIndex, out string? moduleDir))
                    hierarchy!.Modules.Add(new LvsModulePlacement(
                        path, moduleDir, LvsCellKey.Of(moduleDir, subView, tech), deviceIndex));

                devices.Add(new LvsDevice(
                    path, inst.DisplayRefDes ?? "", type, terminals, parameters,
                    new LvsProvenance(document, path, x, y))
                {
                    ParameterFacts = parameterFacts,

                    // Brief 7's tier-0 anchor, carried and never obeyed here (R-lvs7-2b): every
                    // element of an array states the SAME one, which is exactly why it is a claim
                    // the comparison has to check for uniqueness rather than an identity.
                    AnchorId = inst.SchematicId ?? "",
                });
            }
        }

        foreach (var (_, entry) in unclassified.OrderBy(e => e.Key, StringComparer.Ordinal))
            notes.Add(LvsDiagnostics.UnclassifiedCell(entry.Name, entry.Count));

        // ── Tier 3: devices read out of copper (brief 14) ──────────────────────────────────────
        //
        // AFTER every placement, and that is not merely tidiness. A recognised device may never
        // override an instance (R-lvs14-4a), so what it is allowed to look at is decided from the
        // set of placements that produced devices — which only exists once the walk above has
        // finished. It is OFF unless the run asked for it and the technology describes something.
        if (hierarchy is { Recognize: true })
            DeviceRecognition.Emit(
                tech,
                LayoutReadHierarchy.RecognizableCopper(flat.Shapes, view, placed, hierarchy, devicePaths),
                pieces, nets, devices, padGeometry, notes, naming, document);

        // ── The assembly's bond wires (brief 13) ───────────────────────────────────────────────
        //
        // AFTER every placement, so a foot landing on a die's bond pad reaches the net that pad is
        // already on. Null — which is every caller that is not a full run — leaves the reading
        // byte for byte as it was, and a design that places no wBond passes through untouched.
        if (wbonds is { Count: > 0 })
            AssemblyRead.Emit(
                wbonds, pieces, view.DbuPerMicron, document, nets, devices, padGeometry, notes, naming);

        // ── R-lvs9-5d: a pathological design costs a message, not a hang ───────────────────────
        //
        // DrcEngine's bargain, reused rather than re-derived — and it comes back with NO netlist,
        // for the same reason the flatten ceiling does: a confident half-comparison over a design
        // the run never finished reading is worse than a refusal that says the number.
        if (hierarchy is { } gate && devices.Count > gate.MaxDevices)
        {
            notes.Add(LvsDiagnostics.OverDeviceCeiling(document, devices.Count, gate.MaxDevices));
            return LvsNetlist.Nothing(notes);
        }

        // ── The boundary: this cell's own ports, in port order ─────────────────────────────────
        var boundary = BoundaryNetsOf(view, cellDir, tech, pieces, nets, hierarchy);

        nets.ReportLabelDisagreements(notes);

        // ── Where all of it IS (R-lvs8-2c) ─────────────────────────────────────────────────────
        var netOfPiece = new int[pieces.Count];
        for (int p = 0; p < pieces.Count; p++) netOfPiece[p] = nets.Existing(pieces.NetOfPiece(p));

        geometry = new LvsGeometry(
            pieces, padGeometry, netOfPiece, naming.Format, naming.LayerNames);

        return new LvsNetlist(devices, nets.Build(), boundary, notes);
    }

    // ── Ground (R-lvs3-6) ────────────────────────────────────────────────────────────────────

    private static void ReportGround(Technology? tech, CopperPieces pieces, List<Diagnostic> notes)
    {
        var ground = pieces.Ground;

        // R-lvs3-6d. A warning naming the flag, with ground terminals then read as ordinary opens.
        // Not an error: a die with no backside metal, grounded only through bondwires to a package,
        // is a real and correct design.
        if (ground.ReferenceName is null
            && tech is not null
            && tech.Stackup.Layers.Any(l => l.Kind == StackupKind.Conductor))
            notes.Add(LvsDiagnostics.NoGroundReferenceConductor());

        // R-lvs3-6e, unconditional — including on a perfectly clean run. See the diagnostic.
        if (ground.ReliesOnUndrawnMetal)
            notes.Add(LvsDiagnostics.GroundReferenceUndrawn(ground.ReferenceName!, ground.ViasReached));
    }

    // ── Terminals (R-lvs3-5) ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Which net one terminal is on — the lookup R-lvs3-5c specifies, once per LAYOUT PIN and
    /// always on the pin's own layer.
    /// </summary>
    private static int ResolveTerminal(
        Terminal terminal, int instIndex, int row, int col, string path,
        Dictionary<(int, int, int, string), List<int>> padsByPin,
        IReadOnlyList<PlacedPin> pads, IReadOnlyList<PlacedPinOrigin> origins,
        CopperPieces pieces, Technology? tech, NetTable nets, List<Diagnostic> notes,
        int deviceIndex, int terminalIndex, List<LvsPadGeometry> padGeometry,
        LvsHierarchyContext? hierarchy)
    {
        var reached = new List<int>();

        foreach (string pinKey in terminal.LayoutPins)
        {
            if (!padsByPin.TryGetValue((instIndex, row, col, pinKey), out var padIndices)) continue;

            foreach (int p in padIndices)
            {
                // R-ab2-2d: ON THE PIN'S OWN LAYER. A pad takes the name — and the identity — of
                // the piece it lands on, and asking any-layer would have a top pad answer with the
                // net of whatever sits under it on the bottom.
                // R-lvs9-5a: ONE query per pin, and here it is.
                if (hierarchy is not null) hierarchy.Counters.PinQueries++;
                int index = pieces.IndexAt(pads[p].X, pads[p].Y, origins[p].Layer);
                int piece = index < 0 ? -1 : pieces.NetOfPiece(index);

                // Recorded EITHER WAY, including where it landed on nothing: a pad on no copper is
                // exactly the finding `lvs.pin.no-copper` is, and it needs a marker like any other.
                padGeometry.Add(new LvsPadGeometry(
                    deviceIndex, terminalIndex, pads[p].X, pads[p].Y,
                    origins[p].Layer, origins[p].WidthDbu, index));

                if (piece < 0)
                {
                    // R-lvs3-5c. The finding NAMES THE LAYER, so the answer is "your technology
                    // does not call that layer a conductor" rather than "your pad is not
                    // connected" — two very different fixes that look identical from here.
                    notes.Add(LvsDiagnostics.PinOnNoCopper(
                        path, pinKey, origins[p].Layer, LayerNameOf(tech, origins[p].Layer)));
                    continue;
                }

                int net = nets.Of(piece);
                if (!reached.Contains(net)) reached.Add(net);
            }
        }

        // R-lvs3-5d. Several pads, several nets: a finding, and NOT a silent choice of one.
        if (reached.Count > 1)
        {
            notes.Add(LvsDiagnostics.TerminalSplitAcrossNets(
                path, terminal.Name.Length > 0 ? terminal.Name : "port " + terminal.Port, reached.Count));
            return nets.Open();
        }

        // A pin that landed on nothing is an OPEN, and an open is a net of its own — a topological
        // fact, not a sentinel every reader would have to special-case.
        return reached.Count == 1 ? reached[0] : nets.Open();
    }

    /// <summary>This cell's own ports, in port order — R-lvs3-1's <c>BoundaryNets</c>.</summary>
    /// <remarks>
    /// <b>Through the terminal map, never positionally</b> (R-lvs4-4b). The root's own pins need no
    /// transform: they are already in the root's frame, which is the frame the partition is in.
    /// </remarks>
    private static IReadOnlyList<int> BoundaryNetsOf(
        LayoutView view, string cellDir, Technology? tech, CopperPieces pieces, NetTable nets,
        LvsHierarchyContext? hierarchy = null)
    {
        var map = TerminalMap.ResolveCell(cellDir);
        if (map.Origin == TerminalMapOrigin.None || map.Terminals.Count == 0) return [];

        var pins = CellPins.Resolve(view, tech);
        var byKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < pins.Count; i++) byKey[PlacedPins.PinKeyOf(pins, i)] = i;

        var boundary = new List<int>(map.Terminals.Count);
        foreach (var terminal in map.Terminals.OrderBy(t => t.Port))
        {
            int net = -1;
            foreach (string pinKey in terminal.LayoutPins)
            {
                if (!byKey.TryGetValue(pinKey, out int i)) continue;
                if (hierarchy is not null) hierarchy.Counters.PinQueries++;
                int piece = pieces.PieceAt(pins[i].X, pins[i].Y, pins[i].Layer);
                if (piece >= 0) { net = nets.Of(piece); break; }
            }
            boundary.Add(net >= 0 ? net : nets.Open());
        }
        return boundary;
    }

    // ── Which instances are devices (R-lvs3-3a) ──────────────────────────────────────────────

    /// <summary>
    /// R-lvs3-3a's five clauses. Anything else is <b>interconnect</b>: its copper joins the
    /// partition and it contributes no device.
    /// </summary>
    /// <remarks>
    /// <b>The last clause is what makes a user-authored PDK need no registration</b> (R-lvs3-3b).
    /// A kit part installed by <c>PdkPartInstaller</c> lands as an ordinary cell folder with a
    /// symbol, a layout and a <c>.ccell</c>, so it is a device for the same reason every other
    /// cell folder is. There is no device table to maintain per kit and nothing for a kit author
    /// to get wrong.
    /// </remarks>
    internal static bool IsDevice(LayoutInstance inst, string? resolvedCellDir, LayoutView? resolvedView)
    {
        if (inst.SchematicId is { Length: > 0 }) return true;
        if (inst.DisplayRefDes is { Length: > 0 }) return true;
        if (LayoutPartKind.Of(inst) is not null) return true;
        if (DeviceTypes.TryGetSymbolKind(resolvedView?.PCellOrigin?.GeneratorId, out _)) return true;

        if (resolvedCellDir is not { Length: > 0 } dir) return false;
        var ccell = ReadCcell(dir);
        return ccell is { NumPorts: > 0 } || ccell?.ExternalProvider is { Length: > 0 };
    }

    /// <summary>Whether a cell draws anything on a conductor layer — R-lvs3-3c's "real copper".</summary>
    private static bool DrawsCopper(LayoutView subView, Technology? tech)
    {
        if (tech is null) return false;
        var conductor = Conductors.Of(tech).SelectMany(c => c.DrawingLayers).ToHashSet();
        return conductor.Count > 0 && subView.Shapes.Any(s => conductor.Contains(s.Layer));
    }

    /// <summary>
    /// <b>What the artwork itself states about this device's values</b>, and what each of those
    /// values is — brief 10 compares them (R-lvs10-2a), this only carries what the CELL already
    /// says.
    /// </summary>
    /// <remarks>
    /// <b>Two sources, and both of them are the cell's own.</b> A generated cell records the
    /// resolved SI parameters it drew from (<c>PCellOrigin.Parameters</c>) and which of them it
    /// DERIVED from its own geometry rather than read; a cell folder may additionally DECLARE
    /// parameters in its <c>.ccell</c>, whose defaults are the value that cell is for —
    /// <c>R0402-294R</c> is a 294 Ω 0402 and every placement of it is one.
    ///
    /// <para><b>What is deliberately NOT a source is the PLACEMENT.</b> A layout instance carries no
    /// parameter overrides, so a cell's claim is true of every placement of it — which is exactly the
    /// property R-lvs10-2a requires and the reason a cell that declares no default claims nothing
    /// rather than claiming zero.</para>
    ///
    /// <para>The declared default is an EXPRESSION with a unit beside it, and it is resolved through
    /// the one expression engine with the unit applied — never by parsing the number out of it.
    /// A default that does not evaluate (it names a variable this cell has no scope for) claims
    /// nothing, which is the same answer as declaring none.</para>
    /// </remarks>
    private static (IReadOnlyDictionary<string, object?> Values,
                    IReadOnlyDictionary<string, LvsParameterFact> Facts)
        ParametersOf(LayoutView? subView, string? cellDir)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var facts  = new Dictionary<string, LvsParameterFact>(StringComparer.Ordinal);

        foreach (var declared in (cellDir is { Length: > 0 } dir ? ReadCcell(dir) : null)?.Parameters ?? [])
        {
            if (declared.Name is not { Length: > 0 } name) continue;
            if (Declared(declared) is not { } value) continue;
            values[name] = value;
            facts[name] = new LvsParameterFact(declared.Dimension);
        }

        // The generator's own snapshot WINS where both say something: a `.ccell` default is what
        // the cell is for and the snapshot is what was actually drawn.
        if (subView?.PCellOrigin is { } origin)
            foreach (var (name, value) in origin.Parameters)
            {
                values[name] = value.Kind switch
                {
                    PCellValueKind.String => value.AsText(),
                    PCellValueKind.Bool   => value.AsBool(),
                    PCellValueKind.Int    => value.AsInt(),
                    _                     => value.AsReal(),
                };
                facts[name] = new LvsParameterFact(
                    facts.TryGetValue(name, out var prior) ? prior.Dimension : UnitDimension.None,
                    origin.IsComputed(name), origin.IsUnread(name));
            }

        return values.Count == 0
            ? (EmptyParameters, EmptyFacts)
            : (values, facts);
    }

    /// <summary>
    /// One declared parameter's default, resolved to SI — or null where it declares no default or
    /// the default does not evaluate on its own.
    /// </summary>
    private static object? Declared(CcellParameter declared)
    {
        if (string.IsNullOrWhiteSpace(declared.DefaultExpression)) return null;
        try
        {
            string unit = UnitNormalizer.ToEngineUnit(declared.Unit);
            var value = new Evaluator().Eval(
                declared.DefaultExpression, new Scope("ccell"), unit.Length > 0 ? unit : null);
            return value.Kind switch
            {
                ValueKind.Real    => value.AsReal(),
                ValueKind.Complex => value.AsComplex(),
                ValueKind.Bool    => value.AsBool(),
                ValueKind.String  => value.AsString(),
                _                 => null,
            };
        }
        catch (Exception)
        {
            // A default naming something this cell has no scope for is not a finding: it is a cell
            // that states nothing about this parameter, which R-lvs10-2b already answers.
            return null;
        }
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyParameters =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, LvsParameterFact> EmptyFacts =
        new Dictionary<string, LvsParameterFact>(StringComparer.Ordinal);

    private static CcellFile? ReadCcell(string cellDir)
    {
        try
        {
            string path = Path.Combine(cellDir, CellFolder.CcellFileName);
            return File.Exists(path) ? CellPersistence.LoadFromFile(path) : null;
        }
        catch (Exception) { return null; }   // an unreadable cell is reported on its own account
    }

    private static string LayerNameOf(Technology? tech, LayerKey layer) =>
        tech?.Layers.FirstOrDefault(l => l.Key == layer)?.Name is { Length: > 0 } name
            ? name
            : "(undeclared)";

    /// <summary>Ordinal for the numbers, ordinal-ignore-case for the pin name — the comparison
    /// <c>TerminalMap</c> matches a <c>LayoutPin</c> entry with.</summary>
    private sealed class PinKeyComparer : IEqualityComparer<(int Inst, int Row, int Col, string Pin)>
    {
        public static readonly PinKeyComparer Instance = new();

        public bool Equals((int Inst, int Row, int Col, string Pin) a, (int Inst, int Row, int Col, string Pin) b)
            => a.Inst == b.Inst && a.Row == b.Row && a.Col == b.Col
               && string.Equals(a.Pin, b.Pin, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((int Inst, int Row, int Col, string Pin) k)
            => HashCode.Combine(k.Inst, k.Row, k.Col, StringComparer.OrdinalIgnoreCase.GetHashCode(k.Pin));
    }

    /// <summary>
    /// Partition nets in, <see cref="LvsNet"/> indices out — and the one place ground becomes one
    /// net.
    /// </summary>
    /// <remarks>
    /// <b>The merge happens HERE and not in the partition</b> (R-lvs3-6a). Two backside vias on an
    /// MMIC reach the same undrawn metal and are therefore one net, but <c>DrcConnectivity</c>
    /// deliberately does not union them: the DRC's net-aware rules and railRF's island count are
    /// answers about DRAWN copper and must not move because LVS asked a different question.
    /// </remarks>
    /// <summary>
    /// <b>Internal rather than private</b> so brief 13's <c>AssemblyRead</c> can put a bond wire's
    /// feet on this level's nets. A wire foot is located by the same point-in-piece lookup a pad is,
    /// so it must arrive at the SAME net table — a second one would give one design two numbering
    /// schemes and two answers to "is this wire on the input net".
    /// </summary>
    internal sealed class NetTable(CopperPieces pieces)
    {
        private readonly Dictionary<int, int> _ofPartition = [];
        private readonly List<string?> _labels = [];
        private readonly List<List<(int Device, int Terminal)>> _pins = [];
        private readonly SortedSet<string> _groundLabels = new(StringComparer.Ordinal);
        private int _ground = -1;

        /// <summary>The LVS net a partition net belongs to.</summary>
        public int Of(int partitionNet)
        {
            if (pieces.IsGround(partitionNet))
            {
                if (_ground < 0) _ground = Add("0");
                if (pieces.NameOfNet(partitionNet) is { Length: > 0 } stated) _groundLabels.Add(stated);
                _ofPartition[partitionNet] = _ground;
                return _ground;
            }

            if (_ofPartition.TryGetValue(partitionNet, out int existing)) return existing;
            return _ofPartition[partitionNet] = Add(pieces.NameOfNet(partitionNet));
        }

        /// <summary>A net with one pin on it and no name — what a pin that landed on nothing is.</summary>
        public int Open() => Add(null);

        /// <summary>
        /// The net a partition net already belongs to, or <c>-1</c> where nothing put it on one —
        /// <b>which is R-lvs8-5c's floating copper</b>. Asks; never creates.
        /// </summary>
        /// <remarks>
        /// The ground clause has to be here rather than in the caller: a piece of the undrawn
        /// reference's own network that no terminal happened to touch is still net 0, and calling
        /// it floating would put a warning on every stitching via of a correct MMIC.
        /// </remarks>
        public int Existing(int partitionNet)
        {
            if (partitionNet < 0) return -1;
            if (_ofPartition.TryGetValue(partitionNet, out int existing)) return existing;
            return pieces.IsGround(partitionNet) ? _ground : -1;
        }

        public void Attach(int net, int device, int terminal) => _pins[net].Add((device, terminal));

        /// <summary>R-lvs3-5e. A stamped name is read, reported and never obeyed.</summary>
        public void ReportLabelDisagreements(List<Diagnostic> notes)
        {
            foreach (string stated in _groundLabels)
                if (!string.Equals(stated, "0", StringComparison.Ordinal))
                    notes.Add(LvsDiagnostics.NetLabelDisagrees("0", stated));
        }

        public IReadOnlyList<LvsNet> Build() =>
            [.. Enumerable.Range(0, _labels.Count).Select(i => new LvsNet(i, _labels[i], _pins[i]))];

        private int Add(string? label)
        {
            _labels.Add(label);
            _pins.Add([]);
            return _labels.Count - 1;
        }
    }
}
