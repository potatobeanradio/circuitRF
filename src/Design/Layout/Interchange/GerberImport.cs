// Gerber file-set import orchestrator (docs/sonnet-briefs/brief-L4g-gerber-import-orchestration.md).
//
// This is PcbImport to GerberReader/ExcellonReader's PcbReader: the only piece of the Gerber stack
// that touches CellFolder, layer reconciliation, Technology and Messages. Both readers deliberately
// know nothing about any of those, which is what makes them headlessly testable against fixtures with
// no workspace anywhere — and what leaves this file holding everything they refuse to know: which
// files in a folder are artwork at all, what layer each one is, which technology the result belongs
// to, where the cell lands, and what the user is told.
//
// R-L4g-0: this is ONE MORE CONSUMER of InterchangeStructure and the shared layer-mapping dialog. If
// a second reconciliation ever appears here, this file has gone wrong — the identity CASCADE below
// (R-L4g-5) decides what a file is; LayoutLayerMapping decides where that lands, exactly as it does
// for GDSII, DXF and board import.
//
// R-L4g-6 (§6): no menu item, no picker, no prompt. The entry point takes a RESOLVED list of file
// paths and returns a result; it asks no one anything except through the two callbacks it is handed —
// the shared layer dialog, and L4f's drill-format resolution. L4h is where a human is involved.


using Clipper2Lib;

using CircuitRF.Design.Cells;
using CircuitRF.Engine;

namespace CircuitRF.Design.Layout.Interchange;

public static class GerberImport
{
    /// <summary>What a drill-format prompt answered. A null <see cref="DrillFormatChoice"/> from the
    /// callback CANCELS the whole import (R-L4h-6); a non-null one with a null
    /// <see cref="Override"/> accepts the inference as it stands. Two states, said once, rather than a
    /// nullable-of-nullable nobody can read at the call site.</summary>
    /// <param name="ApplyToAll">Answer every REMAINING drill file the same way without asking again.
    /// A set's drill files come out of one exporter in one format, so being asked the same question
    /// once per file is repetition, not diligence — and the second dialog is the one a user answers
    /// without reading. A null <see cref="Override"/> carried this way accepts each later file's OWN
    /// inference rather than forcing this file's format onto it: the user confirmed the inference, and
    /// only what they actually CHANGED is worth propagating.</param>
    public sealed record DrillFormatChoice(DrillFormatOverride? Override, bool ApplyToAll = false);

    /// <summary>L4f's format resolution, as L4h will implement it: the file, what was inferred, and
    /// the cross-check against the artwork's own extent — the strongest single piece of evidence
    /// available, and free here because this is the only place that holds both readers' output.</summary>
    /// <param name="remainingFiles">How many further drill files would be asked this same question if
    /// the answer is not marked <see cref="DrillFormatChoice.ApplyToAll"/> — so the prompt can offer
    /// "apply to all" only when there is something to apply it to.</param>
    public delegate DrillFormatChoice? ResolveDrillFormat(
        string fileName, DrillFormatInference inferred, DrillExtentsCheck crossCheck, int remainingFiles);

    /// <summary>One artwork file's row of R-L4g-15's per-layer summary.</summary>
    public sealed record LayerReport(
        string FileName,
        string LayerName,
        GerberLayerRung Rung,
        int Flashes,
        int Strokes,
        int Regions,
        bool Composited,
        bool OrderGuessed)
    {
        public bool IdentityGuessed => Rung == GerberLayerRung.Heuristic;
    }

    public sealed record ImportResult(
        bool Cancelled,
        IReadOnlyList<string> CreatedCellDirs,
        string? CellDir,
        string? ImportDir,
        string? TechPath,
        Technology? Technology,
        IReadOnlyList<LayerReport> Layers,
        IReadOnlyList<string> DrillCandidates,
        IReadOnlyList<string> SkippedFiles,
        IReadOnlyList<string> Messages);

    private static ImportResult Nothing(IReadOnlyList<string> messages, IReadOnlyList<string>? candidates = null)
        => new(true, [], null, null, null, null, [], candidates ?? [], [], messages);

    /// <summary>
    /// Imports one Gerber file SET as a single flat cell plus a technology of its own.
    /// </summary>
    /// <param name="filePaths">The files to import — already resolved by the caller (L4h turns a
    /// folder or a single file into this list). Classification still runs over them, because the
    /// caller may hand over a whole folder's contents and R-L4g-1 decides by CONTENT what each one is.</param>
    /// <param name="parentDir">Where the import folder is created.</param>
    /// <param name="importName">What to call the import folder, its technology and its cell — normally
    /// the source folder's name, or the single file's base name.</param>
    /// <param name="destTech">The workspace's own technology. Read for rung 2 of the cascade and for
    /// the shared mapping dialog, and <b>never modified</b> (R-L4g-8).</param>
    /// <param name="resolveLayerMapping">The shared layer-mapping dialog, exactly as every other
    /// import takes it. Returning null aborts the whole import and creates nothing.</param>
    /// <param name="resolveDrillFormat">L4f's format prompt. Called only when the inference actually
    /// had to guess, or when the hits disagree with the artwork's extent.</param>
    /// <param name="control">Cancellation and progress, exactly as <c>EmRunService.Run</c> takes them
    /// — the ONE object rather than two parameters. Null (the CLI's case, and every existing test's)
    /// runs the import unobserved and uncancellable, which is what makes this an additive parameter.
    /// See <see cref="ImportUnobserved"/> for where the ticks are and where cancellation stops being
    /// answered.</param>
    public static ImportResult Import(
        IReadOnlyList<string> filePaths,
        string parentDir,
        string importName,
        Technology? destTech,
        int destDbuPerMicron,
        Func<IReadOnlyList<LayerMappingRow>, IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>?>? resolveLayerMapping = null,
        ResolveDrillFormat? resolveDrillFormat = null,
        RunControl? control = null)
    {
        var messages = new List<string>();
        try
        {
            return ImportUnobserved(filePaths, parentDir, importName, destTech, destDbuPerMicron,
                                    resolveLayerMapping, resolveDrillFormat, control, messages);
        }
        catch (OperationCanceledException)
        {
            // GRACEFUL, and "nothing was created" is literally true: every cancellation checkpoint
            // below is BEFORE step 10's ImportFolder.Create, so there is no half-written folder to
            // clean up here. What the import had already worked out is still reported — a cancelled
            // run that says nothing at all reads as a crash.
            messages.Add("Import cancelled — nothing was created.");
            return Nothing(messages);
        }
    }

    /// <summary>
    /// The import proper. Split out only so <see cref="Import"/> can own the one
    /// <see cref="OperationCanceledException"/> catch: <see cref="RunControl.Tick"/> and
    /// <see cref="RunControl.TickStage"/> answer cancellation by THROWING, and a checkpoint spelled
    /// out at every one of the dozen work boundaries below would be a dozen places to forget one.
    ///
    /// <para><b>Where progress is reported.</b> One monotone stage bar over
    /// <c>artwork + drill + 1</c> units, because the measured cost is overwhelmingly the per-artwork-file
    /// read (1.5 s of a 1.9 s import on a real 20-layer board) and a bar that only moves between
    /// phases would sit still through all of it. The label is renamed THROUGH the tick
    /// (<see cref="RunControl.TickStage"/>'s <c>nextLabel</c>) rather than through
    /// <see cref="RunControl.BeginStage"/>, which would reset the sub-counter and send the bar
    /// backwards every time a phase changed.</para>
    ///
    /// <para><b>Where cancellation stops being answered: step 10.</b> Everything before it is
    /// reading and arithmetic in memory, so stopping there creates nothing. From
    /// <c>ImportFolder.Create</c> onward the import is writing a folder, a technology and a cell —
    /// about 150 ms of the run — and a stop landing in the middle of that would leave exactly the
    /// half-written import R-L4g-14 exists to make impossible. So it runs to the end.</para>
    /// </summary>
    private static ImportResult ImportUnobserved(
        IReadOnlyList<string> filePaths,
        string parentDir,
        string importName,
        Technology? destTech,
        int destDbuPerMicron,
        Func<IReadOnlyList<LayerMappingRow>, IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>?>? resolveLayerMapping,
        ResolveDrillFormat? resolveDrillFormat,
        RunControl? control,
        List<string> messages)
    {
        // ── 1. What is in the set at all (R-L4g-1) ──────────────────────────────────────────────
        // Indeterminate: the classifier reads every candidate file's CONTENT (R-L4g-1 decides by
        // content, never by extension), so on a folder of any size this is real work — but its own
        // denominator is the file count, which is not worth a second bar for the one pass that is
        // always the cheapest thing here.
        control?.BeginStage("looking at what the folder holds");

        // Deduplicated first: this entry point is public and takes any list, and every per-file map
        // below is keyed on the path. One repeated path is a duplicate key, which is an exception out
        // of the middle of an import rather than a message about it.
        var classified = filePaths
            .DistinctBy(Path.GetFullPath, StringComparer.OrdinalIgnoreCase)
            .Select(GerberFileClassifier.Classify)
            .ToList();
        var artworkFiles = classified.Where(c => c.Kind == GerberFileKind.Artwork).ToList();
        var drillFiles = classified.Where(c => c.Kind == GerberFileKind.Drill).ToList();
        var jobFiles = classified.Where(c => c.Kind == GerberFileKind.JobFile).ToList();
        var skipped = classified.Where(c => c.Kind == GerberFileKind.Other).ToList();

        // R-L4g-2: a folder scan that silently ignores half of what it found is the same failure as a
        // reader that silently ignores a token, and it is more alarming because the user can see the
        // files sitting there.
        var skippedNames = skipped.Select(s => s.FileName).ToList();
        foreach (var file in skipped)
            messages.Add($"Skipped {file.FileName} — {file.Why}.");

        var drillCandidates = GerberFileClassifier.FindSiblingDrillCandidates(
            [.. classified.Where(c => c.Kind != GerberFileKind.Other).Select(c => c.Path)]);

        if (artworkFiles.Count == 0 && drillFiles.Count == 0)
        {
            messages.Add("None of the files given hold Gerber artwork or drill data, so nothing was imported.");
            return Nothing(messages, drillCandidates);
        }

        // ONE counted stage from here to the end, and the only BeginStage after this point — every
        // later phase renames the label THROUGH the tick, which is what keeps the bar monotone. The
        // +1 is step 10's write, ticked when the cell is on disk.
        control?.BeginStage("reading the artwork", artworkFiles.Count + drillFiles.Count + 1);

        // ── 2. The job file (R-L4g-5 rung 0) ────────────────────────────────────────────────────
        GerberJobFile.JobFileContents? job = null;
        var jobFunctionByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (jobFiles.Count > 0)
        {
            try
            {
                job = GerberJobFile.Read(File.ReadAllText(jobFiles[0].Path));
            }
            catch (IOException ex)
            {
                messages.Add($"{jobFiles[0].FileName} could not be read ({ex.GetType().Name}); the import fell back to each file's own attributes.");
            }

            if (job is null)
                messages.Add($"{jobFiles[0].FileName} is not readable as a job file; the import fell back to each file's own attributes.");
            else
            {
                messages.AddRange(job.Diagnostics);
                foreach (var entry in job.Files)
                    if (entry.FileFunction is { Length: > 0 } fn)
                        jobFunctionByName[Path.GetFileName(entry.Path)] = fn;
                messages.Add(
                    $"{jobFiles[0].FileName} names {job.Files.Count} file(s) as part of this board" +
                    (job.LayerNumber is { } n ? $" and declares {n} copper layer(s)" : "") + ".");
            }
            if (jobFiles.Count > 1)
                messages.Add($"{jobFiles.Count} job files were given; {jobFiles[0].FileName} was used and the rest ignored.");
        }

        // ── 3. Read the artwork ─────────────────────────────────────────────────────────────────
        var reads = new List<(GerberFileClass File, GerberReadResult Read)>();
        foreach (var file in artworkFiles)
        {
            // Ticked on ENTRY, naming the file about to be read, so the counter is "which of the N
            // files is being worked on" and every exit from this body — read, IOException, refusal —
            // advances it. A tick at the bottom would have to be repeated before each `continue`,
            // and the one that got forgotten would leave the bar permanently short of its own end.
            control?.TickStage(1, $"reading {file.FileName}");

            GerberReadResult read;
            try
            {
                using var stream = File.OpenRead(file.Path);
                using var text = new StreamReader(stream, System.Text.Encoding.UTF8);
                read = GerberReader.Read(text, destDbuPerMicron);
            }
            catch (IOException ex)
            {
                messages.Add($"{file.FileName} could not be read ({ex.GetType().Name}) and was not imported.");
                continue;
            }

            if (read.Refusal is { } refusal)
            {
                // A refusal is per FILE, never per set: the other layers are still real artwork, and an
                // import that threw away four good files because the fifth declared a negative image
                // would be strictly less useful than one that says which file it dropped.
                messages.Add($"{file.FileName}: {refusal}");
                continue;
            }
            reads.Add((file, read));
        }

        if (reads.Count == 0 && drillFiles.Count == 0)
        {
            messages.Add("No artwork could be read, so nothing was imported.");
            return Nothing(messages, drillCandidates);
        }

        // ── 4. The identity cascade (R-L4g-5) ───────────────────────────────────────────────────
        var identities = new List<GerberLayerIdentity>(reads.Count);
        foreach (var (file, read) in reads)
        {
            jobFunctionByName.TryGetValue(file.FileName, out string? jobFunction);
            identities.Add(GerberLayerCascade.Identify(file.Path, read, jobFunction, destTech));
        }

        // ── 5. Drill files: read, and mint a drill layer for each (R-L4f, R-L4g-4) ──────────────
        var artworkForExtent = new List<GerberImportedShape>();
        foreach (var (_, read) in reads) artworkForExtent.AddRange(read.Shapes);
        var artworkExtent = DrillViaPairing.ArtworkExtents(artworkForExtent);

        var drills = new List<(GerberFileClass File, ExcellonReadResult Read, GerberLayerIdentity Identity)>();
        var drillNames = new List<string>();
        DrillFormatChoice? standingFormat = null;   // set once the user says "apply to all"
        for (int drillIndex = 0; drillIndex < drillFiles.Count; drillIndex++)
        {
            var file = drillFiles[drillIndex];
            control?.TickStage(1, $"reading {file.FileName}");

            ExcellonReadResult read;
            try
            {
                using var stream = File.OpenRead(file.Path);
                read = ExcellonReader.Read(stream, destDbuPerMicron);
            }
            catch (IOException ex)
            {
                messages.Add($"{file.FileName} could not be read ({ex.GetType().Name}) and was not imported.");
                continue;
            }

            if (read.Refusal is { } refusal)
            {
                messages.Add($"{file.FileName}: {refusal}");
                continue;
            }

            // R-L4h-6's prompt, asked from here because this is the only place that holds both the
            // inference and the artwork it can be checked against.
            // R-L4h-6: the prompt appears only when L4f's INFERENCE is uncertain. A cross-check that
            // disagrees while the file DECLARED its own format is not format uncertainty — it means the
            // drill file and the artwork do not belong to the same board, and that is a message, not a
            // question the user can answer with two dropdowns. The cross-check still travels to the
            // prompt as evidence whenever the prompt does appear.
            var crossCheck = ExcellonReader.CrossCheckExtents(read, artworkExtent);

            // R-L4f-1's EVIDENCE SOURCE 5, used as evidence rather than only printed. It is the
            // strongest source on the ladder and it is free here, because this is the only place that
            // holds both readers' output: hits that do not land inside the artwork's own bounding box
            // mean the inference is wrong, whatever the tool table implied. So where the file left
            // something for us to guess AND the guess disagrees with the artwork, the alternatives to
            // that guess are tried and one that agrees is taken — only ever flipping what was guessed,
            // never something the file declared.
            //
            // The case this exists for is not hypothetical: one real drill file declares no units, no
            // format and no LZ/TZ word, and its tool table settles the unit (inch) while nothing at all
            // settles the suppression. Defaulted to leading-suppressed, its 751 holes land in a strip
            // 1/400th the size of the board and 175 of them fall outside it entirely; read
            // trailing-suppressed, every one lands on the board.
            if (read.Format.RequiredAGuess && !crossCheck.Agrees)
            {
                foreach (var candidate in GuessAlternatives(read.Format))
                {
                    ExcellonReadResult retry;
                    try
                    {
                        using var retryStream = File.OpenRead(file.Path);
                        retry = ExcellonReader.Read(retryStream, destDbuPerMicron, candidate);
                    }
                    catch (IOException) { break; }

                    if (retry.Refusal is not null || (retry.Hits.Count == 0 && retry.Slots.Count == 0)) continue;
                    var retryCheck = ExcellonReader.CrossCheckExtents(retry, artworkExtent);
                    if (!retryCheck.Agrees) continue;

                    messages.Add(
                        $"{file.FileName}: read as {read.Format}, {crossCheck.HitsOutside:N0} of " +
                        $"{crossCheck.HitCount:N0} hole(s) fell outside the artwork. The file declares " +
                        $"neither, so it was settled against the artwork instead, as {retry.Format} — " +
                        "under which every hole lands on the board.");
                    read = retry;
                    crossCheck = retryCheck;
                    break;
                }
            }

            if (read.Format.RequiredAGuess && (resolveDrillFormat is not null || standingFormat is not null))
            {
                var choice = standingFormat
                             ?? resolveDrillFormat!(file.FileName, read.Format, crossCheck,
                                                    drillFiles.Count - drillIndex - 1);
                if (choice is null)
                {
                    messages.Add("The drill format was not settled, so nothing was imported.");
                    return Nothing(messages, drillCandidates);
                }
                if (choice.ApplyToAll && standingFormat is null)
                {
                    standingFormat = choice;
                    messages.Add(
                        choice.Override is null
                            ? $"{file.FileName}: the inferred drill format was accepted for this file and " +
                              "every remaining drill file in the set, each read as its own inference."
                            : $"{file.FileName}: the drill format stated here was applied to every " +
                              "remaining drill file in the set as well.");
                }
                if (choice.Override is { } overrides)
                {
                    using var stream = File.OpenRead(file.Path);
                    read = ExcellonReader.Read(stream, destDbuPerMicron, overrides);
                    if (read.Refusal is { } reread)
                    {
                        messages.Add($"{file.FileName}: {reread}");
                        continue;
                    }
                    crossCheck = ExcellonReader.CrossCheckExtents(read, artworkExtent);
                }
            }

            messages.Add($"{file.FileName}: {read.Format}. {string.Join(" ", read.Format.Evidence)}");
            if (!crossCheck.Agrees) messages.Add($"{file.FileName}: {crossCheck.Report}");

            bool? plated = read.Plated ?? ExcellonReader.PlatingFromFileName(file.Path);
            string layerName = plated == false ? "Drill (non-plated)" : "Drill";
            if (drillNames.Contains(layerName, StringComparer.OrdinalIgnoreCase))
                layerName = $"{layerName} ({Path.GetFileNameWithoutExtension(file.Path)})";
            drillNames.Add(layerName);

            drills.Add((file, read,
                GerberLayerCascade.IdentifyDrill(file.Path, read.FileFunction, destTech, layerName)));
        }

        // R-L4g-4: an artwork set with no drill data imports, and says so — it is a perfectly ordinary
        // thing to want, and it must not read as a failure.
        if (drills.Count == 0)
        {
            messages.Add(
                "No drill data was read, so no vias were reconstructed. Any via in this board is present " +
                "as its copper pad only." +
                (drillCandidates.Count > 0
                    ? $" {drillCandidates.Count} drill file(s) next to this folder look like they belong to it — they were NOT imported."
                    : ""));

            // R-L4f-3's refusal, said at the moment it matters. A set whose drill data is emitted in
            // the BINARY (EIA-coded) form never reaches the drill reader at all — the classifier sees
            // bytes that are not text and skips the file as a sibling — so the import succeeds and the
            // board simply has no holes. "Skipped, not text" is true and tells the user nothing they
            // can act on; this names the one cause they can do something about, and only when the set
            // actually came out with no drill data.
            int notText = skipped.Count(s => s.Why.Contains("not text", StringComparison.Ordinal));
            if (notText > 0)
                messages.Add(
                    $"{notText} file(s) in this set are not text and were skipped. A drill file emitted " +
                    "in the BINARY (EIA-coded) form looks exactly like that — circuitRF reads only the " +
                    "ASCII/Excellon form. If this board's holes are missing, re-export the drill data as " +
                    "ASCII and import it with the set.");
        }

        // ── 6. Layer order (R-L4g-10) ───────────────────────────────────────────────────────────
        control?.SetStageLabel("working out the layer stack");
        var conductors = identities.Where(i => i.IsConductor).ToList();
        var guessedOrder = conductors.Where(c => c.CopperIndex is null).ToList();
        var copperTopToBottom = conductors
            .OrderBy(c => c.CopperIndex ?? SideRank(c.Side))
            .ThenBy(InnerRank)                                  // "Inner 2" before "Inner 10"
            .ThenBy(c => c.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (conductors.Count > 0 && guessedOrder.Count == 0)
            messages.Add(
                $"Copper stack order was DECLARED for all {conductors.Count} copper layer(s): " +
                string.Join(", ", copperTopToBottom.Select(c => c.LayerName)) + ", top to bottom.");
        else if (guessedOrder.Count > 0)
            // A silently wrong stack order produces a simulation that runs cleanly and answers a
            // different question (L4d's R-L4d-5), which is why the guess must never be
            // indistinguishable from the declaration.
            messages.Add(
                $"Copper stack order was GUESSED for {guessedOrder.Count} of {conductors.Count} copper layer(s) — " +
                string.Join(", ", copperTopToBottom.Where(guessedOrder.Contains).Select(g => g.LayerName)) +
                " — because neither the job file nor %TF.FileFunction ranked them. The order used, top to " +
                "bottom, is: " + string.Join(", ", copperTopToBottom.Select(c => c.LayerName)) + ".");

        // ── 7. Source layers, and the one reconciliation (R-L4g-0, R-L4g-7, R-L4g-11) ───────────
        var allIdentities = new List<GerberLayerIdentity>(identities);
        allIdentities.AddRange(drills.Select(d => d.Identity));

        var (sourceLayers, keyByFile) = BuildSourceLayers(allIdentities, copperTopToBottom, destTech, messages);

        foreach (var ((_, read), identity) in reads.Zip(identities))
            foreach (var shape in read.Shapes)
                shape.Shape.Layer = keyByFile[identity.FilePath];

        var allShapes = reads.SelectMany(r => r.Read.Shapes.Select(s => s.Shape)).ToList();
        var rows = LayoutLayerMapping.Propose(allShapes, sourceLayers, destTech);

        // R-L4g-6: an unmatched row defaults to "Add to technology", following L4b's and L4d's own
        // divergence from the paste path — a file set's layer names are the author's deliberate intent,
        // not an accident of a paste.
        rows = [.. rows.Select(r => r.Match == LayerMatchKind.NoMatch
            ? r with { Choice = new LayoutFragment.LayerReconciliationChoice(LayoutFragment.LayerReconciliationAction.AddToTechnology) }
            : r)];

        // The dialog is rung 4 and ONLY rung 4: whatever rungs 0-3 identified is settled, and asking
        // about it would make an exactly-identified set (gates 6 and 7) interrupt for nothing. What is
        // left is genuinely unidentified, and there is nothing else that can answer for it.
        var unidentified = identities.Where(i => i.Rung == GerberLayerRung.Unidentified).ToList();
        IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>? choices = null;
        if (unidentified.Count > 0 && rows.Count > 0 && resolveLayerMapping is not null)
        {
            choices = resolveLayerMapping(rows);
            if (choices is null)
            {
                messages.Add("Layer mapping was cancelled, so nothing was imported.");
                return Nothing(messages, drillCandidates);
            }
        }
        choices ??= LayoutLayerMapping.BuildChoices(rows);
        if (rows.Count > 0) messages.Add("Layers: " + LayoutLayerMapping.SummarizeMapping(rows, destTech));

        var reconciled = LayoutFragment.ApplyReconciliation(allShapes, sourceLayers, choices);
        var artwork = new List<GerberImportedShape>(allShapes.Count);
        // Which of those shapes came out of a COMPOSITED read, by reference — the only shapes a via
        // pad may be carved out of. Two files can land on one layer, so "everything on this layer" is
        // not the same set and would re-polygonise a neighbour's untouched artwork.
        var compositedShapes = new HashSet<LayoutShape>(ReferenceEqualityComparer.Instance);
        {
            int at = 0;
            foreach (var (_, read) in reads)
                foreach (var imported in read.Shapes)
                {
                    var shape = reconciled.Shapes[at++];
                    artwork.Add(imported with { Shape = shape });
                    if (read.Composited) compositedShapes.Add(shape);
                }
        }

        // The FINAL key each file's layer landed on — what the new technology must define, and what a
        // via's barrel and landing must name.
        var finalKeyByFile = allIdentities.ToDictionary(
            i => i.FilePath,
            i => ResolveKey(keyByFile[i.FilePath], choices));

        // R-L4g-12: %TO.C and %TO.P ride onto the shapes, and NOTHING here builds hierarchy from them.
        //
        // Component membership CAN be declared — %TO.C names a component reference and %TO.P its pad,
        // so grouping pads by component is a lookup rather than the threshold-driven clustering it
        // would otherwise be. The conclusion is still no, on three grounds that survive that:
        //   * There is no cell DEFINITION to build. Two placements of the same part carry different
        //     references and their geometry is in absolute board coordinates, so recovering a shared
        //     cell plus two transforms means INFERRING the transform — the clustering problem again by
        //     another name.
        //   * It would break the round trip. L4c's writer emits no %TO.C, so hierarchy built from it
        //     could not survive a re-export and L4h's byte-identity gate would fail on the first cycle.
        //   * It is not needed. The pads are on the right layers with the right nets either way.
        // This is the site where footprint inference would go. Do not add it here.
        foreach (var imported in artwork)
        {
            imported.Shape.Component = imported.Component;
            imported.Shape.Pin = imported.Pin;
        }

        // ── 8. Vias (R-L4f) ─────────────────────────────────────────────────────────────────────
        control?.SetStageLabel("reconstructing vias");
        var copperKeys = copperTopToBottom.Select(c => finalKeyByFile[c.FilePath]).ToList();
        var copperPaths = copperTopToBottom.Select(c => c.FilePath).ToHashSet(StringComparer.Ordinal);
        int compositedCopper = reads.Count(r => r.Read.Composited && copperPaths.Contains(r.File.Path));
        IReadOnlyList<GerberImportedShape> remaining = artwork;
        var drillShapes = new List<LayoutShape>();
        int vias = 0, declaredVias = 0, unpairedHoles = 0, componentHoles = 0, slots = 0, tools = 0, hits = 0;

        // The pads a composited COPPER layer painted before its pour swallowed them, on the layer key
        // that file actually landed on. Copper only: a hole through a solder mask is not a via, and
        // offering mask openings as pads would pair holes to them wherever the copper was composited
        // away. Every pad is already inside the copper in `remaining`, so anything that claims one owes
        // that layer the same disc back — which is what CarveClaimedPads below does.
        IReadOnlyList<GerberImportedShape> compositedPads =
        [
            .. reads
                .Where(r => r.Read.CompositedFlashes.Count > 0 && copperPaths.Contains(r.File.Path))
                .SelectMany(r => r.Read.CompositedFlashes.Select(f =>
                {
                    var onLayer = (CircleShape)LayoutGeometry.Clone(f.Shape);
                    onLayer.Layer = finalKeyByFile[r.File.Path];
                    return f with { Shape = onLayer };
                })),
        ];
        var carved = new List<CircleShape>();

        foreach (var (file, read, identity) in drills)
        {
            var drillKey = finalKeyByFile[identity.FilePath];
            var span = DrillViaPairing.MapSpan(read.Span, drillKey, copperKeys);
            var paired = DrillViaPairing.Pair(
                remaining, read, span.Barrel, span.Landing, destDbuPerMicron, compositedCopper,
                compositedPads, copperKeys);

            if (paired.Refusal is { } refusal)
            {
                messages.Add($"{file.FileName}: {refusal}");
                continue;
            }

            drillShapes.AddRange(paired.AllShapes);
            remaining = paired.RemainingArtwork;
            compositedPads = paired.RemainingCompositedPads;
            carved.AddRange(paired.CarvedPads);
            vias += paired.Vias.Count;
            declaredVias += paired.DeclaredVias;
            unpairedHoles += paired.UnpairedHoles.Count;
            componentHoles += paired.ComponentHoles.Count;
            slots += paired.Slots.Count;
            tools += read.Tools.Count;
            hits += read.Hits.Count;

            messages.Add($"{file.FileName}: {read.Tools.Count} tool(s), {read.Hits.Count} hit(s) → {paired.Vias.Count} via(s). {span.Note}");
            foreach (var diagnostic in paired.Diagnostics) messages.Add($"{file.FileName}: {diagnostic}");
            foreach (var diagnostic in read.Diagnostics) messages.Add($"{file.FileName}: {diagnostic}");
            if (!read.ToolDiametersExact)
                messages.Add($"{file.FileName}: at least one tool diameter did not land on a whole DBU and was rounded.");
        }

        if (carved.Count > 0)
        {
            int before = remaining.Count;
            remaining = CarveClaimedPads(remaining, carved, compositedShapes, destDbuPerMicron);
            messages.Add(
                $"{carved.Count:N0} via pad(s) were cut back out of the pour they had been composited " +
                "into, so the copper is not counted twice — each one is now the pad of a via object " +
                "instead of part of the surrounding copper. The layer's copper is unchanged: every pad " +
                "cut lay wholly inside it, and the via puts the same disc back." +
                (remaining.Count != before ? $" ({before:N0} → {remaining.Count:N0} artwork shape(s).)" : ""));
        }

        // ── 9. The technology this import mints (R-L4g-8, R-L4g-9) ──────────────────────────────
        control?.SetStageLabel("building the technology");
        var tech = BuildTechnology(importName, allIdentities, copperTopToBottom, finalKeyByFile, destTech, sourceLayers);

        var stackup = GerberStackupMapping.Build(
            job?.MaterialStackup, job?.BoardThicknessMm, job?.LayerNumber, copperKeys, destDbuPerMicron);
        if (stackup.Stackup is not null) tech.Stackup = stackup.Stackup;
        messages.AddRange(stackup.Messages);

        // A drill layer is DECLARED by the file set (a drill file was actually read), and a
        // StackupKind.Via entry is what marks the drawing layer it landed on as one — it carries no
        // substrate value and nothing simulates it, so it is added in both branches of R-L4g-9. Without
        // it a bare, unpaired hole re-exports as copper on the drill layer instead of as a drill hit.
        //
        // Its SPAN is named from the stackup's own conductor entries when there are any. A through
        // hole goes from the topmost conductor to the bottommost, which is what the drill files here
        // are read as (no set declared a span), and naming it is what stops the technology validator
        // reporting a via that spans nothing on every import that HAS a job file. A set without one
        // has no conductor entries to name, and inventing two would be a substrate invented under
        // another name — that import already says, in words, that the technology is incomplete.
        var conductorEntries = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        foreach (var (_, _, identity) in drills)
            tech.Stackup.Layers.Add(new StackupLayer
            {
                Kind = StackupKind.Via,
                Name = identity.LayerName,
                DrawingLayers = [finalKeyByFile[identity.FilePath]],
                Fill = ViaFillKind.Plated,
                SpanFromLayer = conductorEntries.Count > 0 ? conductorEntries[0].Name : null,
                SpanToLayer = conductorEntries.Count > 0 ? conductorEntries[^1].Name : null,
            });

        // ── 10. Write (R-L4g-13) — nothing is created before this point ─────────────────────────
        //
        // THE LAST CANCELLATION CHECKPOINT, and the stage's last unit, in one call — TickStage checks
        // the token and advances the counter, so the bar reaches its own end here and NOTHING after
        // this line touches `control` at all.
        //
        // That is the point of doing it here rather than after the write. Past this line the import
        // is creating a folder, a technology and a cell, and a token check landing between any two of
        // those would throw with the cell already on disk — turning R-L4g-14's "nothing was created"
        // into a lie told by the cancellation path itself. The write is ~150 ms on a real 20-layer
        // board, so running it to the end costs a cancelling user nothing they would notice, and a
        // full bar labelled "writing the cell" for those 150 ms is the honest reading of it.
        control?.TickStage(1, "writing the cell");
        string importDir = ImportFolder.Create(parentDir, importName);
        string folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(importDir));
        string cellDir;
        string techPath;
        try
        {
            techPath = Path.Combine(importDir, folderName + ".ctech");
            tech.Name = folderName;
            TechPersistence.SaveToFile(techPath, tech);

            cellDir = CellFolder.CreateCellFolder(importDir, folderName);
            var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);

            // R-L4g-12: ONE cell, ONE LayoutView, NO sub-cells. Gerber has no hierarchy; the only
            // construct that resembles one is step-and-repeat, which is panelization and which L4e
            // already flattens (its R-L4e-15). No LayoutInstance is created here, ever.
            var view = new LayoutView
            {
                DbuPerMicron = destDbuPerMicron,
                TechRef = Path.GetRelativePath(layoutDir, techPath),
            };
            view.Shapes.AddRange(remaining.Select(s => s.Shape));
            view.Shapes.AddRange(drillShapes);

            string fileName = folderName + ".clay";
            LayoutPersistence.SaveToFile(Path.Combine(layoutDir, fileName), view);

            string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
            var ccell = CellPersistence.LoadFromFile(ccellPath);
            ccell.PrimaryLayout = fileName;
            CellPersistence.SaveToFile(ccellPath, ccell);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // R-L4g-14: "nothing was created" has to stay literally true on every exit path.
            ImportFolder.RemoveIfEmpty(importDir);
            messages.Add($"The import could not be written ({ex.Message}), so nothing was created.");
            return Nothing(messages, drillCandidates);
        }

        // ── 11. What the user is told (R-L4g-15, -16, -17) ──────────────────────────────────────
        var layerReports = new List<LayerReport>(reads.Count);
        foreach (var ((file, read), identity) in reads.Zip(identities))
        {
            bool orderGuessed = identity.IsConductor && identity.CopperIndex is null;
            layerReports.Add(new LayerReport(
                file.FileName, identity.LayerName, identity.Rung,
                read.FlashCount, read.StrokeCount, read.RegionCount, read.Composited, orderGuessed));

            messages.Add(
                $"{file.FileName} → {identity.LayerName} ({GerberLayerCascade.Describe(identity.Rung)}): " +
                $"{read.FlashCount:N0} flash(es), {read.StrokeCount:N0} stroke(s), {read.RegionCount:N0} region(s)" +
                (read.StepRepeatFactor > 1 ? $", multiplied {read.StepRepeatFactor}× by step-and-repeat" : "") +
                (read.Composited ? " — COMPOSITED for polarity, so its individual shape identities are gone" : "") +
                ".");
            if (read.Composited && read.CompositeReason is { Length: > 0 } reason)
                messages.Add($"{file.FileName}: {reason}");

            // R-L4g-16: the stroke count is ACTIONABLE, not decorative. A pour that arrived as N
            // parallel strokes is correct artwork that is neither editable copper nor meshable, and the
            // fix already exists in the editor. Name the layer, the count and the action.
            if (read.StrokeCount >= VectorFillStrokeThreshold)
                messages.Add(
                    $"{identity.LayerName} arrived as {read.StrokeCount:N0} separate strokes — that is a " +
                    "vector-filled pour, which is correct artwork but is neither editable copper nor " +
                    "meshable. Select them on that layer and use the editor's Merge action to turn them " +
                    "into one region before setting up EM ports.");

            foreach (var (command, count) in read.UnknownCommandCounts.OrderByDescending(kv => kv.Value))
                messages.Add($"{file.FileName}: unrecognized command \"{command}\" ({count:N0} occurrence(s)) — skipped.");
            foreach (var (what, count) in read.SkippedConstructCounts.OrderByDescending(kv => kv.Value))
                messages.Add($"{file.FileName}: {count:N0} × {what}.");
            foreach (var diagnostic in read.Diagnostics) messages.Add($"{file.FileName}: {diagnostic}");
            if (!read.CoordinatesExact)
                messages.Add(
                    $"{file.FileName}: the declared format's unit is not a whole number of DBU, so coordinates " +
                    $"were rounded — worst case {read.WorstCaseRoundingErrorDbu:0.###} DBU.");
        }

        var guessedIdentities = identities.Where(i => i.IsGuess).ToList();
        messages.Add(
            $"Imported {reads.Count} artwork file(s) as {layerReports.Count} layer(s) and " +
            $"{remaining.Count + drillShapes.Count:N0} shape(s): " +
            $"{identities.Count(i => i.Rung <= GerberLayerRung.TechnologySuffix)} layer(s) identified exactly, " +
            $"{guessedIdentities.Count} guessed from the file name, " +
            $"{unidentified.Count} mapped by hand.");

        // R-L4h-8: name the PERMANENT, BY-TYPE losses here, once, at the moment they happen — not only
        // in a brief. A user who exports a design, re-imports it and finds their rounded rectangles are
        // now polygons has to be told; and it must be said as a property of the FORMAT, because none of
        // it is something a better reader would recover. The import cannot know which of these the
        // source design actually used (the files carry no such types to lose), so it names the class
        // and only when the class applies — a set with no regions in it says nothing about regions.
        int regionsRead = reads.Sum(r => r.Read.RegionCount);
        var losses = new List<string>();
        if (regionsRead > 0)
            losses.Add(
                $"{regionsRead:N0} filled region(s) arrived as POLYGONS — the format has no rectangle, " +
                "rounded-rectangle or curve type, and a path drawn with a square or extended end style " +
                "is written as its outline, so any of those in the design that produced these files is " +
                "a polygon here");
        if (reads.Any(r => r.Read.Composited))
            losses.Add(
                "a layer that painted in clear polarity was COMPOSITED, so its individual shape " +
                "identities and its per-object net names are gone (the geometry is exact)");
        losses.Add(
            "text is not a type this format carries, so a label in the source design is polygon " +
            "outlines here and cannot become a label again");
        losses.Add(drills.Count > 0
            ? $"a via is written as a copper flash PLUS a drill hit, and the two were rejoined into " +
              $"{vias:N0} via(s) because the drill data came with the artwork — a flash whose hole was " +
              "not in the set stays a plain pad"
            : "a via is written as a copper flash PLUS a drill hit; with no drill file in this set, " +
              "every via in the source design is here as its copper pad alone");
        messages.Add(
            "What this format cannot carry back, permanently — this is the format's limit, not the " +
            "reader's: " + string.Join("; ", losses) + ".");

        if (guessedIdentities.Count > 0)
            messages.Add(
                "GUESSED from the file name, so check them: " +
                string.Join(", ", guessedIdentities.Select(g => $"{g.FileName} → {g.LayerName}")) + ".");

        if (drills.Count > 0)
            messages.Add(
                $"Drill: {tools:N0} tool(s), {hits:N0} hit(s) → {vias:N0} via(s) ({declaredVias:N0} declared as vias " +
                $"by the files themselves), {unpairedHoles:N0} unpaired hole(s), {componentHoles:N0} component hole(s), " +
                $"{slots:N0} slot(s).");

        if (skippedNames.Count > 0)
            messages.Add($"{skippedNames.Count} file(s) in the set were not artwork or drill data: {string.Join(", ", skippedNames)}.");

        if (drillCandidates.Count > 0)
            messages.Add(
                $"{drillCandidates.Count} drill file(s) sit one folder away and share this board's file names: " +
                string.Join(", ", drillCandidates.Select(Path.GetFileName)) +
                ". They were NOT imported — import them with this set if they belong to it.");

        messages.Add(
            $"This import wrote its own technology, {folderName}.ctech, next to the cell, and left the " +
            "workspace's technology untouched. A Gerber set describes a whole board's drawing layers, and " +
            "grafting them onto a technology other cells share is a permanent cost for a temporary " +
            "convenience.");

        // R-L4g-17, unchanged from L4d's R-L4d-19: the whole set comes in, unfiltered, because cropping
        // is an EDIT and the editor already has one.
        messages.Add(
            "The whole file set was imported. A real board is not a MoM problem as a whole — crop the " +
            "region you intend to simulate before setting up EM ports.");

        return new ImportResult(
            false, [cellDir], cellDir, importDir, techPath, tech,
            layerReports, drillCandidates, skippedNames, messages);
    }

    /// <summary>The other readings of a drill file that the file itself did not rule out — one
    /// override per thing that was GUESSED, and their combination. Ordered cheapest-mistake first:
    /// zero suppression is the unknown a headerless file most often leaves open and the one that moves
    /// a coordinate by orders of magnitude, so it is tried before the unit.</summary>
    private static IEnumerable<DrillFormatOverride> GuessAlternatives(DrillFormatInference inferred)
    {
        bool unitGuessed = inferred.UnitEvidence
            is DrillFormatEvidence.ToolDiameters or DrillFormatEvidence.Defaulted;
        bool zeroGuessed = !inferred.DecimalCoordinates
            && inferred.ZeroOmissionEvidence == DrillFormatEvidence.Defaulted;

        var otherZero = inferred.ZeroOmission == GerberZeroOmission.Leading
            ? GerberZeroOmission.Trailing : GerberZeroOmission.Leading;
        var otherUnit = inferred.Unit == GerberUnit.Inches
            ? GerberUnit.Millimetres : GerberUnit.Inches;

        if (zeroGuessed) yield return new DrillFormatOverride(ZeroOmission: otherZero);
        if (unitGuessed) yield return new DrillFormatOverride(Unit: otherUnit);
        if (unitGuessed && zeroGuessed) yield return new DrillFormatOverride(Unit: otherUnit, ZeroOmission: otherZero);
    }

    /// <summary>Above this, a layer's strokes are a vector-filled pour rather than a handful of traces
    /// — the point at which R-L4g-16's Merge advice is worth giving rather than noise. A real pour is
    /// thousands of strokes; a hand-drawn layer is a few dozen.</summary>
    private const int VectorFillStrokeThreshold = 200;

    /// <summary>Top first, bottom last, everything unranked in between — the ONLY ordering available to
    /// a set that declares none, and the reason R-L4g-10 insists such a set says so by name.</summary>
    /// <summary>
    /// Cuts the pads a via claimed out of the composited copper they were merged into.
    ///
    /// <para>Restricted to <paramref name="compositedShapes"/> BY REFERENCE, not by layer: two files
    /// can land on one layer, and a boolean over everything on that layer would re-polygonise a
    /// neighbour's untouched artwork into the pour — a silent change to shapes nothing asked about.
    /// Only the polygons that compositing itself produced are rebuilt.</para>
    ///
    /// <para>One boolean per LAYER, not one per pad. A board of this shape has a thousand-odd vias and
    /// a pour carrying hundreds of thousands of vertices; a difference per pad would be a thousand
    /// passes over all of it.</para>
    /// </summary>
    private static IReadOnlyList<GerberImportedShape> CarveClaimedPads(
        IReadOnlyList<GerberImportedShape> artwork,
        IReadOnlyList<CircleShape> pads,
        HashSet<LayoutShape> compositedShapes,
        int dbuPerMicron)
    {
        var padsByLayer = pads.GroupBy(p => p.Layer).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<GerberImportedShape?>(artwork.Count);
        var rebuilt = new Dictionary<LayerKey, List<GerberImportedShape>>();
        var slotAt = new Dictionary<LayerKey, int>();

        // Everything untouched keeps its place and its identity; each carved layer's shapes come out
        // and its rebuilt ones go back in where the FIRST of them stood, so the layer's artwork does
        // not migrate to the end of the cell.
        foreach (var imported in artwork)
        {
            if (!compositedShapes.Contains(imported.Shape) ||
                !padsByLayer.ContainsKey(imported.Shape.Layer))
            {
                result.Add(imported);
                continue;
            }

            var layer = imported.Shape.Layer;
            if (!rebuilt.TryGetValue(layer, out var replacement))
            {
                rebuilt[layer] = replacement = [];
                slotAt[layer] = result.Count;
                result.Add(null);
            }
            replacement.Add(imported);
        }

        // Descending, so splicing one layer cannot move another layer's slot out from under it.
        foreach (var layer in rebuilt.Keys.OrderByDescending(k => slotAt[k]))
        {
            var group = rebuilt[layer];

            var copper = new Paths64();
            foreach (var imported in group)
                copper.AddRange(LayoutClipper.ToClipperPaths(imported.Shape, GerberPrimitives.CircleTolDbu));

            var discs = new Paths64();
            foreach (var pad in padsByLayer[layer])
                discs.AddRange(LayoutClipper.ToClipperPaths(pad, GerberPrimitives.CircleTolDbu));

            var tree = new PolyTree64();
            Clipper.BooleanOp(ClipType.Difference,
                              Clipper.Union(copper, LayoutClipper.Rule),
                              Clipper.Union(discs, LayoutClipper.Rule), tree, LayoutClipper.Rule);

            var carvedShapes = LayoutClipper.FromClipperTree(tree, layer, group[0].Shape.Net);
            result.RemoveAt(slotAt[layer]);
            result.InsertRange(slotAt[layer],
                carvedShapes.Select(sh => (GerberImportedShape?)new GerberImportedShape(sh, null, null, null)));
        }

        return [.. result.Where(r => r is not null).Select(r => r!)];
    }

    /// <summary>Where a guessed inner layer sits among the other guessed inner layers. Every one of
    /// them shares a single <see cref="SideRank"/>, so without this they fall back to file name — which
    /// orders "Inner 10" before "Inner 2" whenever the names are what distinguishes them. It is NOT a
    /// <c>CopperIndex</c>: the number came from a file NAME, and the import's own report must go on
    /// calling this stack order a guess.</summary>
    private static int InnerRank(GerberLayerIdentity identity)
    {
        const string prefix = "Inner ";
        return identity.LayerName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(identity.LayerName.AsSpan(prefix.Length), out int n)
            ? n
            : int.MaxValue;
    }

    private static int SideRank(string? side) => side switch
    {
        "Top" => 0,
        "Bot" => int.MaxValue - 1,
        _ => int.MaxValue / 2,
    };

    /// <summary>
    /// The synthetic "source layers" <see cref="LayoutLayerMapping.Propose"/> expects — one per FILE,
    /// which is the unit of layer identity in this format.
    ///
    /// <para>A destination layer the cascade matched donates its own key and name, so <c>Propose</c>
    /// sees a high-confidence match rather than a fresh unknown; everything else gets a minted key that
    /// collides with nothing in the destination technology.</para>
    ///
    /// <para><b>R-L4g-7: the source extension is recorded as the minted layer's
    /// <c>GerberSuffix</c>, unconditionally</b> — including on a donated layer that already carried a
    /// different one. This is not decoration. Without it a re-export names its files from a synthetic
    /// fallback suffix instead of the names the import read, and L4h's byte-identity gate cannot pass;
    /// L4d measured the equivalent omission on a real board and every layer landed on a general drawing
    /// layer, turning tracks into graphics.</para>
    /// </summary>
    private static (List<LayerDef> SourceLayers, Dictionary<string, LayerKey> KeyByFile) BuildSourceLayers(
        IReadOnlyList<GerberLayerIdentity> identities,
        IReadOnlyList<GerberLayerIdentity> copperTopToBottom,
        Technology? destTech,
        List<string> messages)
    {
        var sourceLayers = new List<LayerDef>(identities.Count);
        var keyByFile = new Dictionary<string, LayerKey>(StringComparer.Ordinal);
        var used = new HashSet<LayerKey>(destTech?.Layers.Select(l => l.Key) ?? []);
        var taken = new HashSet<LayerKey>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int next = 1;

        foreach (var identity in identities)
        {
            LayerKey key;
            if (identity.DestLayer is { } donated && taken.Add(donated))
            {
                key = donated;
            }
            else
            {
                if (identity.DestLayer is not null)
                    messages.Add(
                        $"{identity.FileName} resolved to the same technology layer as an earlier file; it was " +
                        "given a layer of its own so the two files' artwork does not merge.");
                while (used.Contains(new LayerKey(next, 0)) || taken.Contains(new LayerKey(next, 0))) next++;
                key = new LayerKey(next, 0);
                taken.Add(key);
                next++;
            }

            string name = identity.LayerName;
            if (!names.Add(name))
            {
                name = $"{identity.LayerName} ({Path.GetFileNameWithoutExtension(identity.FilePath)})";
                names.Add(name);
            }

            var donor = destTech?.Layers.FirstOrDefault(l => l.Key == key);
            int rank = IndexOfFile(copperTopToBottom, identity.FilePath);
            int zOrder = rank >= 0 ? rank * 10 : 1000 + sourceLayers.Count;

            sourceLayers.Add(new LayerDef
            {
                Key = key,
                Name = name,
                // R-L4g-11: FallbackPalette, the same deterministic gap-fill the renderer, DXF import
                // and board import already use — NEVER a `G04 Layer_Color=` comment. Those are one
                // tool's private annotation, they are not portable, and honouring them would make two
                // imports of the same board look different depending on who generated the files.
                // Deterministic in the key, so two imports of one set produce the same colours.
                Color = donor?.Color ?? FallbackPalette.For(key).Color,
                FillOpacity = donor?.FillOpacity ?? 0.35,
                ZOrder = zOrder,
                Purpose = identity.Purpose,
                Interchange = new InterchangeMapping(
                    donor?.Interchange?.GdsiiLayer,
                    donor?.Interchange?.GdsiiDatatype,
                    donor?.Interchange?.DxfLayerName,
                    identity.Extension is { Length: > 0 } ext ? ext : donor?.Interchange?.GerberSuffix,
                    identity.FileFunction ?? donor?.Interchange?.GerberFileFunction,
                    donor?.Interchange?.PcbLayerName),
            });
            keyByFile[identity.FilePath] = key;
        }

        return (sourceLayers, keyByFile);
    }

    private static int IndexOfFile(IReadOnlyList<GerberLayerIdentity> list, string filePath)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i].FilePath, filePath, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>R-L4g-8: a NEW technology, written into the import folder, pointed at by the new
    /// <c>.clay</c> — never a graft onto the workspace's own. The divergence from board import is about
    /// WHAT IS BEING GRAFTED ONTO WHAT: a board file's per-layer permittivity and loss tangent make a
    /// live override worth having, whereas a Gerber set brings a whole board's worth of drawing layers
    /// into a file possibly shared by every cell in the workspace and quite possibly describing an
    /// entirely different process. A file rather than a live override for the same reason in reverse:
    /// this technology has no prior state to preserve and nothing in it is a pending edit to something
    /// the user already had.</summary>
    private static Technology BuildTechnology(
        string name,
        IReadOnlyList<GerberLayerIdentity> identities,
        IReadOnlyList<GerberLayerIdentity> copperTopToBottom,
        IReadOnlyDictionary<string, LayerKey> finalKeyByFile,
        Technology? destTech,
        IReadOnlyList<LayerDef> sourceLayers)
    {
        var tech = new Technology { Name = name };
        var seen = new HashSet<LayerKey>();

        foreach (var identity in identities)
        {
            var key = finalKeyByFile[identity.FilePath];
            if (!seen.Add(key)) continue;

            // Where a shape landed on a DESTINATION layer (the cascade matched one, or the dialog
            // mapped one), that layer's own definition is COPIED — never referenced and never mutated,
            // because the workspace technology must come out of this untouched (gate 10).
            var donor = destTech?.Layers.FirstOrDefault(l => l.Key == key);
            var source = sourceLayers.FirstOrDefault(l => l.Key == key);

            tech.Layers.Add(new LayerDef
            {
                Key = key,
                Name = donor?.Name ?? source?.Name ?? identity.LayerName,
                Color = donor?.Color ?? source?.Color ?? FallbackPalette.For(key).Color,
                FillOpacity = donor?.FillOpacity ?? 0.35,
                ZOrder = source?.ZOrder ?? 0,
                Visible = true,
                Selectable = true,
                Purpose = identity.Purpose,
                FillPattern = donor?.FillPattern,
                // The source extension, always — see BuildSourceLayers' own note on R-L4g-7.
                Interchange = source?.Interchange ?? new InterchangeMapping(
                    null, null, null, identity.Extension, identity.FileFunction),
            });
        }

        tech.Layers.Sort((a, b) => a.ZOrder.CompareTo(b.ZOrder));
        _ = copperTopToBottom;
        return tech;
    }

    /// <summary>The reconciled destination key for a source key — the same projection
    /// <see cref="LayoutFragment.ApplyReconciliation"/> applies to a shape's own layer, applied here to
    /// the things that are not shape layers (a via's barrel and landing, and the technology's own layer
    /// table). Mirrors <c>PcbImport.ResolveKey</c> exactly.</summary>
    private static LayerKey ResolveKey(
        LayerKey source, IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>? choices)
    {
        if (choices is not null && choices.TryGetValue(source, out var choice)
            && choice.Action == LayoutFragment.LayerReconciliationAction.MapToExisting
            && choice.MapTarget is { } target)
            return target;
        return source;
    }
}
