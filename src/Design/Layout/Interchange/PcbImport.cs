// Board import orchestrator (docs/sonnet-briefs/brief-L4d-kicad-pcb-import.md). The ONLY piece of the
// board stack that touches CellFolder / layer reconciliation / Technology / Messages — PcbReader itself
// stays pure tokens-and-geometry, exactly the split DxfImport and DxfReader already draw (R-L4d-0).
//
// Import only. There is no writer of this format in this phase and none planned as a stretch goal (§1):
// emitting a board file means authoring board-setup and design-rule state circuitRF has no opinion
// about, in a file that is then the user's to fabricate from. The outward handoff is already served by
// L4b's DXF writer, whose exact layer names and colours every board tool's graphics import reads.


using CircuitRF.Design.Cells;

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
        IReadOnlyList<StackupLayer> ViaEntries);

    private static ImportResult Nothing(IReadOnlyList<string> messages)
        => new(true, [], null, [], null, messages, []);

    /// <summary>
    /// Imports the whole board in <paramref name="stream"/> as real cell folders under
    /// <paramref name="parentDir"/>: one cell per distinct footprint definition (R-L4d-15) plus one
    /// board cell holding the tracks, vias, zone fills and an instance per placement.
    /// </summary>
    /// <param name="boardName">What to call the board's own cell — normally the file's base name.</param>
    /// <param name="resolveLayerMapping">The shared L1g layer-mapping dialog, exactly as
    /// <c>GdsiiImport</c>/<c>DxfImport</c> take it. Returning null aborts the whole import and creates
    /// nothing.</param>
    public static ImportResult Import(
        Stream stream,
        string parentDir,
        string boardName,
        Technology? destTech,
        int destDbuPerMicron,
        Func<IReadOnlyList<LayerMappingRow>, IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>?>? resolveLayerMapping = null)
    {
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

        var (sourceLayers, keyByName) = PcbLayerReconciliation.BuildSourceLayers(allNames, board.LayerTable, destTech);
        foreach (var s in board.Shapes) s.Shape.Layer = KeyOf(keyByName, s.LayerName);
        foreach (var cell in board.FootprintCells.Values)
        {
            foreach (var s in cell.Shapes) s.Shape.Layer = KeyOf(keyByName, s.LayerName);
        }

        var allShapes = board.Shapes.Select(s => s.Shape)
            .Concat(board.FootprintCells.Values.SelectMany(c => c.Shapes.Select(s => s.Shape)))
            .ToList();
        var rows = LayoutLayerMapping.Propose(allShapes, sourceLayers, destTech);

        // §3, following L4b: an unmatched row defaults to "Add to technology" rather than
        // Keep-as-unknown. A board file's layer names are the author's deliberate intent, not an
        // accident of a paste — which is exactly the divergence L4b already justified for DXF, and the
        // same reason applies verbatim here.
        rows = [.. rows.Select(r => r.Match == LayerMatchKind.NoMatch
            ? r with { Choice = new LayoutFragment.LayerReconciliationChoice(LayoutFragment.LayerReconciliationAction.AddToTechnology) }
            : r)];

        IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>? choices = null;
        if (rows.Count > 0 && LayoutLayerMapping.RequiresConfirmation(rows) && resolveLayerMapping is not null)
        {
            choices = resolveLayerMapping(rows);
            if (choices is null) return Nothing(messages);
        }
        choices ??= LayoutLayerMapping.BuildChoices(rows);
        if (rows.Count > 0) messages.Add(LayoutLayerMapping.SummarizeMapping(rows, destTech));

        // ── Stackup, and the via entries its conductors make expressible ─────────────────────────
        // Built HERE rather than at the end, because the via spans are named against it and the vias
        // have to be moved onto their entries' drawing layers before the cells are written. Its own
        // messages still go out in their original place, below.
        var stackup = PcbStackupMapping.Build(
            board.Stackup, board.OverallThicknessMm, destDbuPerMicron,
            name => ResolveKey(keyByName, choices, name));

        // Which stackup is actually in force after this import: the destination's whenever it declares
        // one, because neither applier ever replaces a stackup that is already there. Naming a span
        // against the stackup that is about to be REFUSED would produce entries whose conductors do not
        // exist, which ViaSpanResolver reads back as no span at all.
        var effectiveStackup = destTech is { } dt && dt.Stackup.Layers.Count > 0 ? dt.Stackup : stackup.Stackup;

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
            [.. sourceLayers.Select(l => l.Key).Concat(destTech?.Layers.Select(l => l.Key) ?? [])]);

        // ── Cell folders ────────────────────────────────────────────────────────────────────────
        var cellsByKey = board.FootprintCells.Values.ToList();

        // Two CONTENT-DISTINCT cells can share one library name — the same part number drawn two ways,
        // or one placed on both sides. The name mangler maps original → unique and is therefore keyed by
        // the original, so handing it duplicates silently collapses them: measured, a real board created
        // 58 cell folders of which only 22 were distinct, and 36 cells overwrote each other's .clay on
        // the way past. Uniquify BEFORE mangling so every content key gets its own folder.
        var proposedNames = new List<string>(cellsByKey.Count + 1);
        var seenNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in cellsByKey)
        {
            string baseName = cell.LibraryName;
            int n = seenNames.TryGetValue(baseName, out int prior) ? prior + 1 : 1;
            seenNames[baseName] = n;
            proposedNames.Add(n == 1 ? baseName : $"{baseName}#{n}");
        }
        proposedNames.Add(boardName);
        var cellNames = DxfNaming.NameCellsForImport(proposedNames);

        var createdDirs = new List<string>();
        var dirByContentKey = new Dictionary<string, string>();
        for (int i = 0; i < cellsByKey.Count; i++)
        {
            string dir = CellFolder.CreateCellFolder(parentDir, UniqueName(cellNames, proposedNames[i], i));
            dirByContentKey[cellsByKey[i].ContentKey] = dir;
            createdDirs.Add(dir);
        }
        string boardDir = CellFolder.CreateCellFolder(parentDir, cellNames[boardName]);
        createdDirs.Add(boardDir);

        // ── Write each footprint cell ───────────────────────────────────────────────────────────
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

            WriteCell(dirByContentKey[cell.ContentKey], view);
        }

        // ── Write the board cell ────────────────────────────────────────────────────────────────
        var boardReconciled = LayoutFragment.ApplyReconciliation([.. board.Shapes.Select(s => s.Shape)], sourceLayers, choices);
        foreach (var def in boardReconciled.LayersToAdd)
            if (addedKeys.Add(def.Key)) layersToAdd.Add(def);

        var boardView = new LayoutView { DbuPerMicron = destDbuPerMicron };
        boardView.Shapes.AddRange(boardReconciled.Shapes);
        ResolveViaLayers(board.Shapes, boardReconciled.Shapes, keyByName, choices, spanByShape, viaSpans);

        string boardLayoutDir = CellFolder.SubFolderPath(boardDir, ViewType.Layout);
        foreach (var placement in board.Placements)
        {
            if (!dirByContentKey.TryGetValue(placement.ContentKey, out var targetDir)) continue;
            boardView.Instances.Add(new LayoutInstance
            {
                CellRef = Path.GetRelativePath(boardLayoutDir, targetDir),
                X = placement.X,
                Y = placement.Y,
                RotationDegrees = placement.RotationDegrees,
                // R-L4d-15/§7: NO mirror, and that is measured rather than assumed — a back-layer
                // footprint's stored child geometry already carries the flip (every local Y negated)
                // and its child layers are already the back-side ones. See PcbReader.ReadFootprint.
                MirrorX = false,
                Mag = 1.0,
            });
        }
        WriteCell(boardDir, boardView);

        // ── Stackup ─────────────────────────────────────────────────────────────────────────────
        messages.AddRange(stackup.Messages);
        messages.AddRange(viaSpans.Messages);

        // ── What came in, and what did not ──────────────────────────────────────────────────────
        messages.Add(
            $"Imported {board.EntitiesRead:N0} entities as {board.ShapesProduced:N0} shape(s): " +
            $"{cellsByKey.Count:N0} footprint cell(s) placed {board.Placements.Count:N0} time(s), plus " +
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

        return new ImportResult(false, createdDirs, boardDir, layersToAdd, stackup.Stackup, messages, viaSpans.NewEntries);
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

    private static void WriteCell(string cellDir, LayoutView view)
    {
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        string cellName = Path.GetFileName(Path.TrimEndingDirectorySeparator(cellDir));
        string fileName = cellName + ".clay";
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, fileName), view);

        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimaryLayout = fileName;
        CellPersistence.SaveToFile(ccellPath, ccell);
    }
}
