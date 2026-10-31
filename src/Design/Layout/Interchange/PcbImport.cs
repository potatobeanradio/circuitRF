// Board import orchestrator (docs/sonnet-briefs/brief-L4d-kicad-pcb-import.md). The ONLY piece of the
// board stack that touches CellFolder / layer reconciliation / Technology / Messages — PcbReader itself
// stays pure tokens-and-geometry, exactly the split DxfImport and DxfReader already draw (R-L4d-0).
//
// Import only. There is no writer of this format in this phase and none planned as a stretch goal (§1):
// emitting a board file means authoring board-setup and design-rule state circuitRF has no opinion
// about, in a file that is then the user's to fabricate from. The outward handoff is already served by
// L4b's DXF writer, whose exact layer names and colours every board tool's graphics import reads.


using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Footprints;

namespace CircuitRF.Design.Layout.Interchange;

public static class PcbImport
{
    /// <param name="ViaEntries">The <see cref="StackupKind.Via"/> entries this import needs, one per
    /// distinct via span in the file, each binding a drawing layer that is also in
    /// <paramref name="LayersToAdd"/>. <b>Carried separately from <paramref name="Stackup"/> on
    /// purpose</b>: replacing a technology's whole stackup silently would be indefensible and both
    /// appliers refuse it when the destination already declares one, but a via entry is purely
    /// ADDITIVE — it declares a drill, it cannot invalidate a substrate — so it applies either way.
    /// Empty when the board has no vias, or when nothing states which conductors they join.</param>
    public sealed record ImportResult(
        bool Cancelled,
        IReadOnlyList<string> CreatedCellDirs,
        string? BoardCellDir,
        IReadOnlyList<LayerDef> LayersToAdd,
        Stackup? Stackup,
        IReadOnlyList<string> Messages,
        IReadOnlyList<StackupLayer> ViaEntries)
    {
        /// <summary>The technology every written <c>.clay</c> references (issue #34): the one this import wrote
        /// beside the cells (<see cref="ImportTechnologyTarget.New"/>), or the existing one it used. Null when
        /// nothing was imported.</summary>
        public string? TechPath { get; init; }

        /// <summary>That technology, as written or as read.</summary>
        public Technology? Technology { get; init; }

        /// <summary>The one sentence an import into a chosen technology refused with (R-gt-3's rule, applied to a
        /// board): the board's copper does not agree with that technology's stackup. Nothing was created.</summary>
        public string? Refusal { get; init; }
    }

    private static ImportResult Nothing(IReadOnlyList<string> messages)
        => new(true, [], null, [], null, messages, []);

    private static ImportResult Refused(List<string> messages, string refusal)
    {
        messages.Add(refusal);
        return Nothing(messages) with { Refusal = refusal };
    }

    /// <summary>
    /// Imports the whole board in <paramref name="stream"/> as real cell folders under
    /// <paramref name="parentDir"/>: one cell per distinct footprint definition (R-L4d-15) plus one
    /// board cell holding the tracks, vias, zone fills and an instance per placement.
    /// </summary>
    /// <param name="boardName">What to call the board's own cell — normally the file's base name.</param>
    /// <param name="resolveLayerMapping">The shared L1g layer-mapping dialog, exactly as
    /// <c>GdsiiImport</c>/<c>DxfImport</c> take it. Returning null aborts the whole import and creates
    /// nothing.</param>
    /// <param name="coalesceRasterFill">R-rf3-7, on <c>GerberImport.Import</c>'s terms and for the
    /// same reason — a board file converted from painted artwork carries the same tens of thousands
    /// of abutting strokes, and they are the same problem for the same consumers. True is the
    /// default; false gives the primitives exactly as authored.</param>
    /// <param name="target">Issue #34, on the Gerber import's terms (R-gt-1). Null or
    /// <see cref="ImportTechnologyTarget.New"/>: a technology of the board's own is WRITTEN beside the cells,
    /// <c>&lt;parentDir&gt;/&lt;boardName&gt;.ctech</c>, and every written <c>.clay</c> references it — so the
    /// layers a board brings survive the session. <paramref name="destTech"/> is its donor: its layers, rules and
    /// settings are copied, and its stackup is used only when the board states none. <b>Use</b> imports against an
    /// existing technology, references it, writes nothing to it, keeps a layer it lacks as unknown, and refuses
    /// when the board's copper count is not that technology's conductor count.</param>
    /// <param name="resolveMapping">The mapping dialog that also chooses the technology (R-gt-2). Given, it is
    /// asked instead of <paramref name="resolveLayerMapping"/>, on every import with layers; its answer's target
    /// overrides <paramref name="target"/>.</param>
    /// <param name="destTechPath">Where <paramref name="destTech"/> was read from, so a copied technology's
    /// material-library references can be re-pointed from its new folder. Null leaves them as written.</param>
    public static ImportResult Import(
        Stream stream,
        string parentDir,
        string boardName,
        Technology? destTech,
        int destDbuPerMicron,
        Func<IReadOnlyList<LayerMappingRow>, IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>?>? resolveLayerMapping = null,
        bool coalesceRasterFill = true,
        ImportTechnologyTarget? target = null,
        Func<ImportMappingRequest, ImportMappingAnswer?>? resolveMapping = null,
        string? destTechPath = null)
    {
        target ??= ImportTechnologyTarget.New;
        using var textReader = new StreamReader(stream, System.Text.Encoding.UTF8);
        string text = textReader.ReadToEnd();

        var read = PcbReader.Read(text, destDbuPerMicron);
        if (read.Refusal is { } refusal)
            return Nothing([refusal]);
        if (read.Board is null)
            return Nothing(["This file could not be read as a board."]);

        var board = read.Board;
        var messages = new List<string>(board.Diagnostics);
        if (board.Version is { Length: > 0 } version)
            // R-L4d-1: the version is REPORTED, never branched on and never a reason to refuse.
            messages.Add($"Board format epoch {version} — read by the tokens present, not by the version.");

        // ── Layers ──────────────────────────────────────────────────────────────────────────────
        var allNames = new List<string>();
        foreach (var s in board.Shapes) CollectNames(s, allNames);
        foreach (var cell in board.FootprintCells.Values)
            foreach (var s in cell.Shapes) CollectNames(s, allNames);
        allNames = [.. allNames.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

        var allShapes = board.Shapes.Select(s => s.Shape)
            .Concat(board.FootprintCells.Values.SelectMany(c => c.Shapes.Select(s => s.Shape)))
            .ToList();

        // The technology everything below is resolved against: the one this import USES, or the donor of the one
        // it writes. Re-proposing against another one re-stamps every shape's source key and reads nothing again
        // (R-gt-2), which is what the mapping dialog's Technology row does.
        Technology? useTech = null;
        if (target.UsePath is { } usePath && (useTech = GerberImport.LoadTarget(usePath, messages)) is null)
            return Nothing(messages);

        IReadOnlyList<LayerDef> sourceLayers = [];
        IReadOnlyDictionary<string, LayerKey> keyByName = new Dictionary<string, LayerKey>();
        IReadOnlyList<LayerMappingRow> ProposeFor(Technology? tech, bool use)
        {
            (sourceLayers, keyByName) = PcbLayerReconciliation.BuildSourceLayers(allNames, board.LayerTable, tech);
            foreach (var s in board.Shapes) s.Shape.Layer = KeyOf(keyByName, s.LayerName);
            foreach (var cell in board.FootprintCells.Values)
                foreach (var s in cell.Shapes) s.Shape.Layer = KeyOf(keyByName, s.LayerName);

            // §3, following L4b: an unmatched row defaults to "Add to technology" rather than Keep-as-unknown. A
            // board file's layer names are the author's deliberate intent, not an accident of a paste — which is
            // exactly the divergence L4b already justified for DXF. Not into a technology this import USES: nothing
            // is added to one of those (R-gt-4), so its unmatched rows stay unknown.
            var proposed = LayoutLayerMapping.Propose(allShapes, sourceLayers, tech, evenWithoutDestination: !use);
            return [.. proposed.Select(r => r.Match == LayerMatchKind.NoMatch && !use
                ? r with { Choice = new LayoutFragment.LayerReconciliationChoice(LayoutFragment.LayerReconciliationAction.AddToTechnology) }
                : r)];
        }
        var rows = ProposeFor(useTech ?? destTech, useTech is not null);
        int copperCount = board.LayerTable.Count(e => e.IsCopper);

        IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>? choices = null;
        if (resolveMapping is not null && rows.Count > 0)
        {
            // Asked on EVERY import with layers, as the Gerber import asks: which technology a board lands in is
            // a question about the whole board, not about one unmatched layer.
            var request = new ImportMappingRequest(rows, copperCount, target, tech => ProposeFor(tech ?? destTech, tech is not null));
            var answer = resolveMapping(request);
            if (answer is null) return Nothing(messages);
            if (answer.Target != target)
            {
                useTech = null;
                if (answer.Target.UsePath is { } chosen && (useTech = GerberImport.LoadTarget(chosen, messages)) is null)
                    return Nothing(messages);
                target = answer.Target;
            }
            // The dialog may have re-proposed against several technologies; put back the keys this one names.
            ProposeFor(useTech ?? destTech, useTech is not null);
            rows = answer.Rows;
            choices = LayoutLayerMapping.BuildChoices(rows);
        }
        else if (rows.Count > 0 && LayoutLayerMapping.RequiresConfirmation(rows) && resolveLayerMapping is not null)
        {
            choices = resolveLayerMapping(rows);
            if (choices is null) return Nothing(messages);
        }
        choices ??= LayoutLayerMapping.BuildChoices(rows);
        var mappingTech = useTech ?? destTech;
        if (rows.Count > 0) messages.Add(LayoutLayerMapping.SummarizeMapping(LayoutLayerMapping.Settle(rows, choices), mappingTech));

        // R-gt-3's rule for a board: into a technology that already has a stackup, the board's copper must be
        // that stackup's conductors. Checked before anything is written, so a refusal creates nothing.
        if (useTech is not null)
        {
            int conductors = useTech.Stackup.Layers.Count(l => l.Kind == StackupKind.Conductor);
            if (conductors > 0 && conductors != copperCount)
                return Refused(messages,
                    $"The board has {copperCount} copper layer(s) and '{useTech.Name}' has {conductors} conductor(s) in its " +
                    "stackup, so it cannot be imported into that technology. Nothing was imported.");
        }

        // ── Stackup, and the via entries its conductors make expressible ─────────────────────────
        // Built HERE rather than at the end, because the via spans are named against it and the vias
        // have to be moved onto their entries' drawing layers before the cells are written. Its own
        // messages still go out in their original place, below.
        var stackup = PcbStackupMapping.Build(
            board.Stackup, board.OverallThicknessMm, destDbuPerMicron,
            name => ResolveKey(keyByName, choices, name));

        // Which stackup is in force after this import (issue #34). A technology this import USES keeps its own. One it
        // writes keeps the donor's — R-L4d-6: nothing already declared is replaced — unless the donor's conductors are
        // not this board's copper, which is the wrong stackup rather than a different opinion of the right one (a
        // four-layer board in a two-layer workspace lost its inner copper that way); then the board's own is taken.
        // Naming a span against any other stackup would produce entries whose conductors do not exist, which
        // ViaSpanResolver reads back as no span at all.
        bool boardStatesStackup = stackup.Stackup is { Layers.Count: > 0 };
        int donorConductors = destTech?.Stackup.Layers.Count(l => l.Kind == StackupKind.Conductor) ?? 0;
        bool donorStackupKept = useTech is null && destTech is { Stackup.Layers.Count: > 0 }
                                && (!boardStatesStackup || donorConductors == copperCount);
        var effectiveStackup = useTech is not null ? useTech.Stackup
            : donorStackupKept ? destTech!.Stackup
            : stackup.Stackup;

        var spanByShape = new Dictionary<PcbImportedShape, PcbViaSpanMapping.SourceSpan>();
        foreach (var s in board.Shapes.Concat(board.FootprintCells.Values.SelectMany(c => c.Shapes)))
        {
            if (s.Shape is not ViaShape) continue;
            if (s.SpanFromName is not { Length: > 0 } from || s.SpanToName is not { Length: > 0 } to) continue;
            if (ResolveKey(keyByName, choices, from) is not { } fromKey) continue;
            if (ResolveKey(keyByName, choices, to) is not { } toKey) continue;
            spanByShape[s] = new PcbViaSpanMapping.SourceSpan(fromKey, toKey);
        }

        var viaSpans = PcbViaSpanMapping.Build(
            [.. spanByShape.Values.Distinct()],
            effectiveStackup,
            [.. sourceLayers.Select(l => l.Key).Concat(mappingTech?.Layers.Select(l => l.Key) ?? [])],
            destDbuPerMicron);

        // What to CALL a layer in a message — both the source key and whatever reconciliation mapped
        // it to, since a coalescing note names the layer the shapes ended up on.
        var layerNameByKey = new Dictionary<LayerKey, string>();
        foreach (var (name, key) in keyByName)
        {
            layerNameByKey.TryAdd(key, name);
            if (ResolveKey(keyByName, choices, name) is { } resolved) layerNameByKey.TryAdd(resolved, name);
        }

        // ── Footprint views, as read ────────────────────────────────────────────────────────────
        var cellsByKey = board.FootprintCells.Values.ToList();
        var layersToAdd = new List<LayerDef>();
        var addedKeys = new HashSet<LayerKey>();

        // A minted drill layer carries no shapes until the vias are moved onto it below, and a span's
        // own copper may carry none at all (an inner plane a blind via lands on, with no artwork on
        // this board). ApplyReconciliation only ever adds a layer some SHAPE was on, so neither would
        // otherwise reach the technology — and a via entry binding a layer the technology does not
        // declare resolves to nothing, which is the defect this whole path exists to close.
        foreach (var def in viaSpans.NewDrawingLayers)
            if (addedKeys.Add(def.Key)) layersToAdd.Add(def);
        foreach (var span in spanByShape.Values.Distinct())
            foreach (var key in new[] { span.From, span.To })
                if (!addedKeys.Contains(key) && sourceLayers.FirstOrDefault(l => l.Key == key) is { } def)
                {
                    addedKeys.Add(key);
                    layersToAdd.Add(def);
                }

        var readViews = new Dictionary<string, LayoutView>();
        foreach (var cell in cellsByKey)
        {
            var reconciled = LayoutFragment.ApplyReconciliation([.. cell.Shapes.Select(s => s.Shape)], sourceLayers, choices);
            foreach (var def in reconciled.LayersToAdd)
                if (addedKeys.Add(def.Key)) layersToAdd.Add(def);

            var view = new LayoutView { DbuPerMicron = destDbuPerMicron };
            view.Shapes.AddRange(reconciled.Shapes);
            ResolveViaLayers(cell.Shapes, reconciled.Shapes, keyByName, choices, spanByShape, viaSpans);
            foreach (var (pin, layerName) in cell.Pins)
            {
                if (ResolveKey(keyByName, choices, layerName) is { } key) pin.Layer = key;
                view.Pins.Add(pin);
            }
            readViews[cell.ContentKey] = view;
        }

        // ── The board cell's own shapes ─────────────────────────────────────────────────────────
        // Reconciled before the back-side decision below, so the layers only a track uses are among the
        // ones that decision looks the surface roles up in.
        var boardReconciled = LayoutFragment.ApplyReconciliation([.. board.Shapes.Select(s => s.Shape)], sourceLayers, choices);
        foreach (var def in boardReconciled.LayersToAdd)
            if (addedKeys.Add(def.Key)) layersToAdd.Add(def);

        var boardView = new LayoutView { DbuPerMicron = destDbuPerMicron };
        boardView.Shapes.AddRange(boardReconciled.Shapes);
        ResolveViaLayers(board.Shapes, boardReconciled.Shapes, keyByName, choices, spanByShape, viaSpans);

        // ── Which cells there are, and which face each placement is on (brief-bsp-4 R-bsp4-1) ──────
        // What the back-side decision looks the surface roles up in: the technology these cells will resolve.
        Technology? effective = useTech ?? (destTech is null && layersToAdd.Count == 0 ? null : new Technology
        {
            Name = destTech?.Name ?? boardName,
            Layers = [.. destTech?.Layers ?? [], .. layersToAdd.Where(l => destTech?.Layers.All(d => d.Key != l.Key) ?? true)],
            Stackup = effectiveStackup ?? new Stackup(),
        });
        var plan = PlanSides(board, cellsByKey, readViews, effective, layerNameByKey, messages);

        // ── Cell folders ────────────────────────────────────────────────────────────────────────
        // Two CONTENT-DISTINCT cells can share one library name — the same part number drawn two ways,
        // or one placed on both sides. The name mangler maps original → unique and is therefore keyed by
        // the original, so handing it duplicates silently collapses them: measured, a real board created
        // 58 cell folders of which only 22 were distinct, and 36 cells overwrote each other's .clay on
        // the way past. Uniquify BEFORE mangling so every content key gets its own folder.
        var proposedNames = new List<string>(plan.Cells.Count + 1);
        var seenNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in plan.Cells)
        {
            string baseName = cell.LibraryName;
            int n = seenNames.TryGetValue(baseName, out int prior) ? prior + 1 : 1;
            seenNames[baseName] = n;
            proposedNames.Add(n == 1 ? baseName : $"{baseName}#{n}");
        }
        proposedNames.Add(boardName);
        var cellNames = DxfNaming.NameCellsForImport(proposedNames);

        var createdDirs = new List<string>();
        var dirById = new Dictionary<string, string>();
        for (int i = 0; i < plan.Cells.Count; i++)
        {
            string dir = CellFolder.CreateCellFolder(parentDir, UniqueName(cellNames, proposedNames[i], i));
            dirById[plan.Cells[i].Id] = dir;
            createdDirs.Add(dir);
        }
        string boardDir = CellFolder.CreateCellFolder(parentDir, cellNames[boardName]);
        createdDirs.Add(boardDir);

        // The technology every written .clay references (issue #34): the one this import uses, or the one it writes
        // beside the cells once they are written (below).
        string techPath = useTech is not null ? target.UsePath! : Path.Combine(parentDir, boardName + ".ctech");

        // ── Write each footprint cell ───────────────────────────────────────────────────────────
        foreach (var cell in plan.Cells)
        {
            Coalesce(cell.View, mappingTech, destDbuPerMicron, coalesceRasterFill, messages, layerNameByKey);
            WriteCell(dirById[cell.Id], cell.View, techPath);
        }

        // ── Write the board cell ────────────────────────────────────────────────────────────────
        string boardLayoutDir = CellFolder.SubFolderPath(boardDir, ViewType.Layout);
        foreach (var placement in board.Placements)
        {
            var (id, bottom) = plan.TargetOf(placement);
            if (!dirById.TryGetValue(id, out var targetDir)) continue;
            boardView.Instances.Add(new LayoutInstance
            {
                CellRef = Path.GetRelativePath(boardLayoutDir, targetDir),
                X = placement.X,
                Y = placement.Y,
                // A back part placed as read has NO mirror, and that is measured rather than assumed — the
                // format's stored child geometry already carries the flip (every local Y negated) and its
                // child layers are already the back-side ones (PcbReader.ReadFootprint). Placed as the
                // bottom side of its FRONT form instead, that flip is circuitRF's mirror, which negates
                // local X: the two differ by a half turn, the same one PcbWriter adds on the way out.
                RotationDegrees = bottom ? LayoutAngle.Normalize(placement.RotationDegrees + 180.0) : placement.RotationDegrees,
                MirrorX = bottom,
                Side = bottom ? PlacementSide.Bottom : PlacementSide.Top,
                Mag = 1.0,
                // brief-footprint-4b R-fp4b-8a: the designator is the PLACEMENT's, and so is where its
                // author put it. RefDes rather than SchematicId — an imported board corresponds to no
                // schematic component in this workspace, and R-fp4b-1b is exactly that case. Its offset
                // turns with the placement, so the half turn plus mirror reflects its Y back.
                RefDes = placement.Reference,
                LabelDx = placement.LabelDx,
                LabelDy = bottom && placement.LabelDy is { } dy ? -dy : placement.LabelDy,
                LabelRotDeg = placement.LabelRotDeg,
            });
        }
        Coalesce(boardView, mappingTech, destDbuPerMicron, coalesceRasterFill, messages, layerNameByKey);
        WriteCell(boardDir, boardView, techPath);

        // ── The technology the cells reference (issue #34) ────────────────────────────────────
        // Before this, the layers a board brought lived only in a session's copy of the workspace technology,
        // while the .clay files were written naming them — so the next session drew every one of them as an
        // unnamed "L-n/0", and a workspace with no technology kept nothing at all.
        Technology tech;
        if (useTech is not null)
        {
            tech = useTech;
            var kept = rows.Where(r => r.Choice.Action == LayoutFragment.LayerReconciliationAction.KeepUnknown)
                           .Select(r => r.SourceName ?? $"{r.Source.Layer}/{r.Source.Datatype}").ToList();
            if (kept.Count > 0)
                messages.Add($"Kept as unknown layers, because '{useTech.Name}' has no layer for them and nothing is added " +
                             $"to a technology an import uses: {string.Join(", ", kept)}.");
            if (viaSpans.NewEntries.Count > 0)
                messages.Add($"{viaSpans.NewEntries.Count} via span(s) have no via entry in '{useTech.Name}' and none was added: " +
                             string.Join(", ", viaSpans.NewEntries.Select(e => e.Name)) + ".");
            messages.Add($"This import uses the technology '{useTech.Name}' ({Path.GetFileName(techPath)}): the layouts " +
                         "reference it, and nothing in it was changed.");
        }
        else
        {
            if (donorStackupKept && boardStatesStackup)
                messages.Add($"The technology keeps the stackup of '{destTech!.Name}', which has the board's {copperCount} " +
                             "copper layer(s); the board's own stackup was not applied. Compare them in the technology editor.");
            tech = BoardTechnology(boardName, destTech, destTechPath, techPath, layersToAdd,
                                   donorStackupKept || !boardStatesStackup ? null : stackup.Stackup, viaSpans.NewEntries,
                                   [boardView, .. plan.Cells.Select(c => c.View)], sourceLayers, board.LayerTable, messages);
            TechPersistence.SaveToFile(techPath, tech);
            messages.Add($"This import wrote its own technology, {Path.GetFileName(techPath)}, next to the cells, and left " +
                         "the workspace's technology untouched. A board describes its own layers and stackup, and grafting " +
                         "them onto a technology other cells share is a permanent cost for a temporary convenience.");
        }


        // ── Stackup ─────────────────────────────────────────────────────────────────────────────
        messages.AddRange(stackup.Messages);
        messages.AddRange(viaSpans.Messages);

        // ── What came in, and what did not ──────────────────────────────────────────────────────
        messages.Add(
            $"Imported {board.EntitiesRead:N0} entities as {board.ShapesProduced:N0} shape(s): " +
            $"{plan.Cells.Count:N0} footprint cell(s) placed {board.Placements.Count:N0} time(s), plus " +
            $"{boardView.Shapes.Count:N0} board-level shape(s).");

        foreach (var (what, count) in board.SkippedCounts.OrderByDescending(kv => kv.Value))
            messages.Add($"{count:N0} × {what} — not imported.");

        foreach (var (what, count) in board.DegradedCounts.OrderByDescending(kv => kv.Value))
            messages.Add($"{count:N0} × {what}.");

        // Gate 14: an unrecognized token is reported ONCE, by name, with a count — never per
        // occurrence, and never silently.
        foreach (var (token, count) in board.UnknownTokenCounts.OrderByDescending(kv => kv.Value))
            messages.Add($"Unrecognized token \"{token}\" ({count:N0} occurrence(s)) — skipped.");

        // R-L4d-19: the whole board comes in, unfiltered — cropping is an EDIT, and the editor already
        // has one. Say what comes next rather than making a file reader host a second selection UI.
        messages.Add(
            "The whole board was imported. A real board is not a MoM problem as a whole — select the " +
            "region you intend to simulate and crop it before setting up EM ports.");

        return new ImportResult(false, createdDirs, boardDir, layersToAdd, stackup.Stackup, messages, viaSpans.NewEntries)
        {
            TechPath = techPath,
            Technology = tech,
        };
    }

    /// <summary>
    /// R-rf3: a painted pour becomes the region it paints, in place, on one cell's shapes.
    ///
    /// <para><b>After <see cref="ResolveViaLayers"/>, and that is not an accident</b> — that method
    /// pairs <c>sources[i]</c> with <c>reconciled[i]</c> BY INDEX, and coalescing changes the length
    /// of the list. It is also why nothing here can touch a <see cref="ViaShape"/>: only
    /// <c>PathShape</c> is a candidate, so the vias whose layers were just settled are carried
    /// through untouched.</para>
    ///
    /// <para>The group key is layer, net, component and pin — everything a board file states about a
    /// track that makes two of them interchangeable. There is no polarity here: the format has no
    /// clear-polarity concept, so every shape is additive by construction.</para>
    /// </summary>
    private static void Coalesce(
        LayoutView view, Technology? destTech, int destDbuPerMicron, bool enabled,
        List<string> messages, IReadOnlyDictionary<LayerKey, string> layerNameByKey)
    {
        if (!enabled || view.Shapes.Count < LayoutRasterFillCoalesce.MinStrokesPerRegion) return;

        long tol = destTech is { DefaultFlattenTolDbu: > 0 } tech
            ? tech.DefaultFlattenTolDbu
            : LayoutRasterFillCoalesce.DefaultToleranceDbu(destDbuPerMicron);

        var candidates = new List<LayoutRasterFillCoalesce.Candidate>(view.Shapes.Count);
        foreach (var shape in view.Shapes)
        {
            if (shape is not PathShape) continue;
            candidates.Add(new LayoutRasterFillCoalesce.Candidate(
                shape,
                string.Join('\u0001', shape.Layer.Layer, shape.Layer.Datatype, shape.Net, shape.Component, shape.Pin),
                layerNameByKey.TryGetValue(shape.Layer, out string? n) ? n : $"layer {shape.Layer.Layer}/{shape.Layer.Datatype}"));
        }

        var plan = LayoutRasterFillCoalesce.Build(candidates, tol);
        if (plan.IsEmpty) return;

        var coalesced = plan.Apply(view.Shapes);
        view.Shapes.Clear();
        view.Shapes.AddRange(coalesced);
        foreach (string note in plan.Notes(destDbuPerMicron)) messages.Add(note);
    }

    /// <summary>One footprint cell to write: a definition as read, or the FRONT form of a back one.</summary>
    private sealed record PlannedCell(string Id, string LibraryName, LayoutView View);

    private sealed class SidePlan
    {
        public List<PlannedCell> Cells { get; } = [];
        public Dictionary<string, string> BottomTarget { get; } = [];   // content key → the cell its front form is

        public (string Id, bool Bottom) TargetOf(PcbPlacement p)
            => p.Back && BottomTarget.TryGetValue(p.ContentKey, out var id) ? (id, true) : (AsRead(p.ContentKey), false);

        public static string AsRead(string contentKey) => "read:" + contentKey;
    }

    /// <summary>
    /// brief-bsp-4 R-bsp4-1/R-bsp4-3: a back footprint becomes a <see cref="PlacementSide.Bottom"/>
    /// placement of its FRONT form — the baked flip undone (local Y negated back, each surface role's
    /// back layer swapped for its front layer) — and shares one cell with the same part on the front.
    ///
    /// <para><b>Decided on reconciled keys, against the destination technology's own surface roles,</b>
    /// because those are exactly what a bottom placement is drawn through (<see cref="SurfaceRoles.Swap"/>):
    /// a front layer is accepted only when swapping it back gives the very layer the old import put the
    /// shape on, so every land lands where, and on what, it always did. A layer that is not a surface
    /// role (inner copper, the drill layer) maps to itself on both faces.</para>
    ///
    /// <para><b>Anything that cannot be un-flipped stays as read</b> — its baked cell on the top, as
    /// every import did before — and is reported with the layer that stopped it. Never a half-mapped
    /// cell. With no technology at all (the GUI on a workspace that has none) there is nothing to find
    /// the bottom layers in, so every back part stays as read.</para>
    /// </summary>
    private static SidePlan PlanSides(
        PcbBoard board, IReadOnlyList<PcbFootprintCell> cells, IReadOnlyDictionary<string, LayoutView> readViews,
        Technology? effective,
        IReadOnlyDictionary<LayerKey, string> layerNameByKey, List<string> messages)
    {
        var plan = new SidePlan();
        var backKeys = board.Placements.Where(p => p.Back).Select(p => p.ContentKey).ToHashSet();
        var library = cells.ToDictionary(c => c.ContentKey, c => c.LibraryName);
        var fronts = new Dictionary<string, LayoutView>();

        string NameOf(LayerKey k) =>
            effective?.Layers.FirstOrDefault(l => l.Key == k)?.Name
            ?? (layerNameByKey.TryGetValue(k, out var n) ? n : $"layer {k.Layer}/{k.Datatype}");

        var keptAsRead = new List<string>();
        if (backKeys.Count > 0 && effective is not null)
        {
            var roles = SurfaceRoles.For(effective);
            foreach (string key in backKeys)
            {
                if (FrontForm(readViews[key], roles, out var stuck) is { } front) { fronts[key] = front; continue; }
                string refs = string.Join(", ", board.Placements.Where(p => p.Back && p.ContentKey == key && p.Reference is { Length: > 0 })
                                                                .Select(p => p.Reference));
                keptAsRead.Add(
                    $"Back-side footprint '{library[key]}'{(refs.Length > 0 ? $" ({refs})" : "")} was kept as its own flipped cell, " +
                    "placed on the top: " +
                    (stuck is { } k
                        ? $"'{NameOf(k)}' has no counterpart on the other face in '{effective.Name}', so its front form cannot be drawn."
                        : $"'{effective.Name}' has no bottom copper."));
            }
        }

        // The cells placed as read: every front placement's, and every back one that stays baked.
        var asRead = new HashSet<string>(board.Placements.Where(p => !p.Back || !fronts.ContainsKey(p.ContentKey)).Select(p => p.ContentKey));
        var asReadByContent = new Dictionary<string, string>();
        foreach (var cell in cells)
        {
            if (!asRead.Contains(cell.ContentKey)) continue;
            plan.Cells.Add(new PlannedCell(SidePlan.AsRead(cell.ContentKey), cell.LibraryName, readViews[cell.ContentKey]));
            asReadByContent.TryAdd(KeyLevelContent(cell.LibraryName, readViews[cell.ContentKey]), SidePlan.AsRead(cell.ContentKey));
        }

        int shared = 0;
        foreach (var cell in cells)
        {
            if (!fronts.TryGetValue(cell.ContentKey, out var front)) continue;
            string content = KeyLevelContent(cell.LibraryName, front);
            if (asReadByContent.TryGetValue(content, out string? id)) { shared++; }
            else
            {
                id = "front:" + cell.ContentKey;
                plan.Cells.Add(new PlannedCell(id, cell.LibraryName, front));
                asReadByContent[content] = id;
            }
            plan.BottomTarget[cell.ContentKey] = id;
        }

        int backPlacements = board.Placements.Count(p => p.Back);
        if (backPlacements == 0) return plan;
        int bottom = board.Placements.Count(p => p.Back && fronts.ContainsKey(p.ContentKey));
        string found = $"{backPlacements:N0} back-side placement(s) found";
        messages.Add(bottom == 0 ? found + "." :
            $"{found}; {bottom:N0} placed on the bottom as bottom-side placements of their footprint's front form " +
            $"({shared:N0} footprint(s) share one cell with the same part on the front).");
        if (effective is null)
            messages.Add(
                $"{backPlacements:N0} back-side placement(s) were kept as their own flipped cells, placed on the top: there is no " +
                "technology to find the bottom layers in, which a bottom-side placement is drawn on.");
        messages.AddRange(keptAsRead);
        return plan;
    }

    /// <summary>
    /// The front form of a back footprint's baked cell: Y negated back (the writer's own
    /// <see cref="PcbWriter.FlipY"/>, so import undoes exactly what export does) and every layer on
    /// the other face. Null, with the layer that stopped it, when a layer has no counterpart whose own
    /// counterpart is that layer again — a bottom placement of the result would not land where the
    /// baked cell did. <paramref name="stuck"/> is null when the technology has no bottom copper.
    /// </summary>
    private static LayoutView? FrontForm(LayoutView baked, SurfaceRoles roles, out LayerKey? stuck)
    {
        stuck = null;
        if (!roles.HasBottomCopper) return null;

        LayerKey? Unswap(LayerKey k) => roles.Swap(k) is { } f && roles.Swap(f) == k ? f : null;

        var front = new LayoutView { DbuPerMicron = baked.DbuPerMicron };
        foreach (var shape in baked.Shapes)
        {
            if (Unswap(shape.Layer) is not { } layer) { stuck = shape.Layer; return null; }
            var c = PcbWriter.FlipY(shape);
            c.Layer = layer;
            if (c is ViaShape { LandingLayer: { } landing } via && roles.Swap(landing) is not null)
            {
                // A landing layer with no other side stays where it is on both faces — the bottom view's rule.
                if (Unswap(landing) is not { } l) { stuck = landing; return null; }
                via.LandingLayer = l;
            }
            front.Shapes.Add(c);
        }

        foreach (var pin in baked.Pins)
        {
            // A through-hole pad's pin is on the TOP copper whichever face its part is on — the reader
            // names a pad's first copper layer, and a through-hole pad lists the front first — so it stays
            // there, as the same part read on the front has it.
            LayerKey layer;
            if (pin.Layer == roles.Top.Copper) layer = pin.Layer;
            else if (Unswap(pin.Layer) is { } f) layer = f;
            else { stuck = pin.Layer; return null; }
            front.Pins.Add(new LayoutPin
            {
                Name = pin.Name, X = pin.X, Y = -pin.Y, WidthDbu = pin.WidthDbu,
                OutwardDeg = LayoutAngle.Normalize(-pin.OutwardDeg), Layer = layer,
            });
        }
        return front;
    }

    /// <summary><see cref="PcbReader.ContentKeyOf(PcbFootprintCell)"/> on reconciled keys — how a back
    /// footprint's front form is recognised as the same part already read on the front.</summary>
    private static string KeyLevelContent(string libraryName, LayoutView view)
    {
        static string K(LayerKey k) => $"{k.Layer}/{k.Datatype}";
        return PcbReader.ContentKeyOf(libraryName,
            view.Shapes.Select(s => (K(s.Layer) + (s is ViaShape { LandingLayer: { } l } ? ">" + K(l) : ""), s)),
            view.Pins.Select(p => (p, K(p.Layer))));
    }

    private static string UniqueName(IReadOnlyDictionary<string, string> names, string proposed, int index)
        => names.TryGetValue(proposed, out var name) ? name : $"footprint_{index}";

    /// <summary>Every SOURCE layer name one imported shape refers to. A via refers to three — its own
    /// barrel layer, its pad's copper, and the two conductors its span joins — and all of them must
    /// reach reconciliation, because a span conductor with no artwork on this board still has to end up
    /// as a real layer for the via entry naming it to resolve.</summary>
    private static void CollectNames(PcbImportedShape s, List<string> into)
    {
        into.Add(s.LayerName);
        if (s.LandingLayerName is { Length: > 0 } landing) into.Add(landing);
        if (s.SpanFromName is { Length: > 0 } from) into.Add(from);
        if (s.SpanToName is { Length: > 0 } to) into.Add(to);
    }

    private static LayerKey KeyOf(IReadOnlyDictionary<string, LayerKey> keyByName, string name)
        => name.Length > 0 && keyByName.TryGetValue(name, out var key) ? key : default;

    /// <summary>The reconciled destination key for a SOURCE layer name, or null when nothing maps.
    /// <see cref="LayoutFragment.ApplyReconciliation"/> does exactly this for a shape's own
    /// <see cref="LayoutShape.Layer"/>; a via's landing layer and a pin's layer are not shape layers, so
    /// they are resolved here through the same choices rather than left at the synthetic key.</summary>
    private static LayerKey? ResolveKey(
        IReadOnlyDictionary<string, LayerKey> keyByName,
        IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>? choices,
        string name)
    {
        if (name.Length == 0 || !keyByName.TryGetValue(name, out var source)) return null;
        if (choices is not null && choices.TryGetValue(source, out var choice)
            && choice.Action == LayoutFragment.LayerReconciliationAction.MapToExisting
            && choice.MapTarget is { } target)
            return target;
        return source;
    }

    /// <summary>
    /// A via's two layers, both settled AFTER reconciliation.
    ///
    /// <para>R-L4d-10: <see cref="LayoutShape.Layer"/> is the via's BARREL and
    /// <see cref="ViaShape.LandingLayer"/> is its PAD. <see cref="ViaShape"/>'s own doc comment states
    /// the consequence of getting it backwards in as many words — a plausible-looking export with
    /// copper where the hole should be.</para>
    ///
    /// <para>The barrel moves off the generic drill layer onto the one bound to the via entry for its
    /// own SPAN, because the drawing layer is what selects the entry and therefore what states the
    /// span (R-via-3, <see cref="ViaSpanResolver"/>). A via whose span could not be expressed keeps
    /// the drill layer it was read onto — the pre-existing behaviour, still reachable and still
    /// reported, which is what lets an import land in a technology the user does not want changed.</para>
    /// </summary>
    private static void ResolveViaLayers(
        IReadOnlyList<PcbImportedShape> sources,
        IReadOnlyList<LayoutShape> reconciled,
        IReadOnlyDictionary<string, LayerKey> keyByName,
        IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>? choices,
        IReadOnlyDictionary<PcbImportedShape, PcbViaSpanMapping.SourceSpan> spanByShape,
        PcbViaSpanMapping.Result viaSpans)
    {
        for (int i = 0; i < sources.Count && i < reconciled.Count; i++)
        {
            if (reconciled[i] is not ViaShape via) continue;

            if (sources[i].LandingLayerName is { Length: > 0 } name)
                via.LandingLayer = ResolveKey(keyByName, choices, name);

            if (spanByShape.TryGetValue(sources[i], out var span)
                && viaSpans.BarrelLayerBySpan.TryGetValue(span, out var barrel))
                via.Layer = barrel;
        }
    }

    /// <summary>
    /// The technology a board import writes for itself (issue #34): the donor's, copied — its layers, rules and
    /// settings — under the board's name, with every layer the cells draw on defined, the board's stackup when it
    /// states one (the donor's otherwise), and the via entries its spans need. The donor itself is never touched.
    /// </summary>
    private static Technology BoardTechnology(
        string boardName, Technology? donor, string? donorPath, string techPath,
        IReadOnlyList<LayerDef> layersToAdd, Stackup? boardStackup, IReadOnlyList<StackupLayer> viaEntries,
        IReadOnlyList<LayoutView> views, IReadOnlyList<LayerDef> sourceLayers,
        IReadOnlyList<PcbLayerTableEntry> layerTable, List<string> messages)
    {
        // A layer the board adds is called what the board's own editor calls it — the layer table's user name
        // ("F.Silkscreen", "User.Drawings") rather than the token its geometry references ("F.SilkS", "Dwgs.User").
        // The token stays the export alias. Matching (above) still sees the token, so a technology whose layer is
        // literally called "F.SilkS" is still matched with no authoring.
        var shownName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in layerTable)
            if (e.UserName is { Length: > 0 } user) shownName.TryAdd(e.CanonicalName, user);
        void Add(LayerDef def, Technology into)
        {
            if (into.Layers.Any(l => l.Key == def.Key)) return;
            into.Layers.Add(shownName.TryGetValue(def.Name, out var shown) ? Copy(def, shown) : def);
        }

        var tech = donor is null ? new Technology() : TechPersistence.Clone(donor);
        tech.Name = boardName;

        // A copied technology's material libraries are named relative to ITS folder; re-point them from this one, or
        // the written technology refuses to load. Where the donor was read from is the caller's to say; failing that,
        // the loader recorded where each library actually was. A shipped library is left as named: saving writes it
        // beside the new file (TechPersistence.SaveToFile).
        if (tech.MaterialLibraries is { Count: > 0 } libs && donor is not null)
        {
            string? Located(string reference)
            {
                if (donorPath is not null) return Path.GetFullPath(MaterialLibraries.ResolvePath(donorPath, reference));
                string file = Path.GetFileName(reference);
                return donor.ResolvedLibraryPaths.FirstOrDefault(p => Path.IsPathFullyQualified(p) && File.Exists(p)
                    && string.Equals(Path.GetFileName(p), file, StringComparison.OrdinalIgnoreCase));
            }
            tech.MaterialLibraries = [.. libs.Select(r => Located(r) is { } abs && File.Exists(abs)
                ? MaterialLibraries.RelativeReference(techPath, abs) : r)];
        }

        foreach (var def in layersToAdd) Add(def, tech);

        // Every layer a written cell draws on is defined, whatever the mapping said — a layer kept "unknown" in a
        // technology the import itself writes would be the same nameless L-n/0 this whole path exists to end.
        var used = new HashSet<LayerKey>();
        foreach (var view in views)
        {
            foreach (var shape in view.Shapes)
            {
                used.Add(shape.Layer);
                if (shape is ViaShape { LandingLayer: { } landing }) used.Add(landing);
            }
            foreach (var pin in view.Pins) used.Add(pin.Layer);
        }
        foreach (var key in used.OrderBy(k => k.Layer).ThenBy(k => k.Datatype))
            if (sourceLayers.FirstOrDefault(l => l.Key == key) is { } def) Add(def, tech);

        if (boardStackup is not null)
        {
            if (donor is { Stackup.Layers.Count: > 0 })
            {
                // The donor's boundary conditions, which the board file cannot state, are kept.
                boardStackup.Top = donor.Stackup.Top;
                boardStackup.Bottom = donor.Stackup.Bottom;
                int conductors = donor.Stackup.Layers.Count(l => l.Kind == StackupKind.Conductor);
                messages.Add($"The technology takes the board's own stackup: '{donor.Name}' has {conductors} conductor(s) and " +
                             "the board has a different number of copper layers. That technology was not changed.");
            }
            tech.Stackup = boardStackup;
        }

        // Keyed on the DRAWING LAYER, which is what selects an entry (R-via-3): two entries claiming one layer is the
        // state ViaSpanResolver cannot resolve deterministically.
        foreach (var entry in viaEntries)
            if (!tech.Stackup.Layers.Any(l => l.Kind == StackupKind.Via
                                              && l.DrawingLayers.Any(k => entry.DrawingLayers.Contains(k))))
                tech.Stackup.Layers.Add(entry);
        return tech;
    }

    private static LayerDef Copy(LayerDef d, string name) => new()
    {
        Key = d.Key, Name = name, Color = d.Color, FillOpacity = d.FillOpacity, ZOrder = d.ZOrder, Visible = d.Visible,
        Selectable = d.Selectable, Purpose = d.Purpose, FillPattern = d.FillPattern, Interchange = d.Interchange,
        SheetResistanceOhmPerSq = d.SheetResistanceOhmPerSq,
    };

    private static void WriteCell(string cellDir, LayoutView view, string techPath)
    {
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        view.TechRef = CircuitRF.Core.RefPath.ToStored(Path.GetRelativePath(layoutDir, techPath));
        string cellName = Path.GetFileName(Path.TrimEndingDirectorySeparator(cellDir));
        string fileName = cellName + ".clay";
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, fileName), view);

        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimaryLayout = fileName;
        CellPersistence.SaveToFile(ccellPath, ccell);
    }
}
