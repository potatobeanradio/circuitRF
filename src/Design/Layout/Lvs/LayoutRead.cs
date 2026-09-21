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
    public static LvsNetlist Read(
        LayoutView view, string clayPath, string cellDir, Technology? tech,
        out LvsGeometry geometry,
        Func<string?, string, TechResolution>? resolveTechAt = null,
        IReadOnlySet<string>? schematicComponents = null,
        LvsHierarchyContext? hierarchy = null)
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
            var parameters = ParametersOf(subView);

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
                    // Brief 7's tier-0 anchor, carried and never obeyed here (R-lvs7-2b): every
                    // element of an array states the SAME one, which is exactly why it is a claim
                    // the comparison has to check for uniqueness rather than an identity.
                    AnchorId = inst.SchematicId ?? "",
                });
            }
        }

        foreach (var (_, entry) in unclassified.OrderBy(e => e.Key, StringComparer.Ordinal))
            notes.Add(LvsDiagnostics.UnclassifiedCell(entry.Name, entry.Count));

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

    /// <summary>What the artwork already states about this device's values. Brief 10 compares
    /// them; this only carries what a generated cell recorded when it drew itself.</summary>
    private static IReadOnlyDictionary<string, object?> ParametersOf(LayoutView? subView)
    {
        if (subView?.PCellOrigin is not { } origin || origin.Parameters.Count == 0)
            return EmptyParameters;

        var values = new Dictionary<string, object?>(origin.Parameters.Count, StringComparer.Ordinal);
        foreach (var (name, value) in origin.Parameters) values[name] = value.Kind switch
        {
            PCellValueKind.String => value.AsText(),
            PCellValueKind.Bool   => value.AsBool(),
            PCellValueKind.Int    => value.AsInt(),
            _                     => value.AsReal(),
        };
        return values;
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyParameters =
        new Dictionary<string, object?>(StringComparer.Ordinal);

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
    private sealed class NetTable(CopperPieces pieces)
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
