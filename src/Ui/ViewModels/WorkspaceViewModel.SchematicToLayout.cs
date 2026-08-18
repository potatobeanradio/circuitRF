using System;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.WBond;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// docs/sonnet-briefs/brief-L5-schematic-to-layout.md §2.1/§3A/§3B — the two "Design" menu commands:
/// **Update Layout from Schematic** (§2, the schematic→layout generator already wired) and
/// **Update Schematic from Layout** (§3A, its mechanical inverse). Both are symmetric in every way
/// the brief asks for: same file-targeting shape (create-if-absent, open, focus, leave a differently-
/// named primary alone and report it — R-L5-17/21), same change-report shape (capped at 20 instances,
/// overwritten-count always visible, silent when nothing changed — R-L5-13/14/22), one undoable action
/// each (R-L5-12/22), and — the one guardrail that is NOT negotiable — neither ever runs except from
/// an explicit user invocation (R-L5-23): no save hook, no open hook, no document-activation hook
/// anywhere near either method.
/// </summary>
public partial class WorkspaceViewModel
{
    // ── Update Layout from Schematic (§2/§2.1) ───────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(IsSchematicDocumentActive))]
    private void UpdateLayoutFromSchematic()
    {
        if (ResolveActiveDocumentForCommands() is SchematicDocument doc)
            RunLayoutUpdate(doc, onlyWBond: null);
    }

    /// <summary>
    /// The wBond parameter dialog's own <b>Update Layout</b> button (owner, 2026-08-17): the same command
    /// as far as the layout and the wires are concerned, but it touches <b>only this wBond</b> — every
    /// other component in the schematic is left exactly as the layout already has it.
    ///
    /// <para>Why the restriction is worth having its own entry point: a user editing a wBond's arrays
    /// wants to see the wires, not to have every instance in the layout re-resolved, re-placed and
    /// re-reported around them. The full command is one menu item away when that IS what they want.</para>
    ///
    /// <para>The document is found from the VIEW MODEL rather than from the active dockable: this is
    /// raised by a NON-MODAL dialog, so the user may well have clicked another tab since it opened, and
    /// "the active document" would then be the wrong schematic — or not a schematic at all.</para>
    /// </summary>
    internal void UpdateLayoutForWBond(SchematicViewModel schematic, EditableComponent comp)
    {
        var doc = _openDocsByPath.Values.OfType<SchematicDocument>().Concat(_scratchDocs)
            .FirstOrDefault(d => ReferenceEquals(d.ViewModel, schematic));

        if (doc is null)
        {
            Messages.Error("Update Layout: this schematic is no longer open.");
            return;
        }

        RunLayoutUpdate(doc, onlyWBond: comp);
    }

    /// <param name="onlyWBond">
    /// When non-null, ONLY this wBond component is written — the instance generator does not run at all.
    /// Null is the ordinary whole-schematic command.
    /// </param>
    private void RunLayoutUpdate(SchematicDocument doc, EditableComponent? onlyWBond)
    {
        if (doc.IsScratch)
        {
            Messages.Error("Update Layout from Schematic: save the schematic first — a scratch schematic has no cell to write into.");
            return;
        }

        string schematicPath = doc.FilePath!;
        string schematicDir  = Path.GetDirectoryName(schematicPath)!;
        string cellDir       = Path.GetDirectoryName(schematicDir)!;
        string schematicName = Path.GetFileNameWithoutExtension(schematicPath);
        string layoutDir     = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        string targetPath    = Path.Combine(layoutDir, schematicName + CellFolder.ViewExtension(ViewType.Layout));

        // R-L5-17: capture primacy BEFORE touching anything.
        var primaryBefore = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
        bool hadNoRealPrimary = primaryBefore.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent);
        string? primaryBeforeName = primaryBefore.ResolvedName;

        bool createdNewFile = !File.Exists(targetPath);
        if (createdNewFile)
        {
            Directory.CreateDirectory(layoutDir);
            var techRes = ResolveTechFor(null, targetPath);
            var seedModel = new LayoutView
            {
                DbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
                DisplayUnit  = techRes.Tech?.DefaultDisplayUnit ?? LayoutUnit.Um,
                SnapDbu      = techRes.Tech?.DefaultSnapDbu ?? 1000,
                AngleMode    = AngleMode.AnyAngle,
            };
            LayoutPersistence.SaveToFile(targetPath, seedModel);
            _factory.ProjectTreeTool?.Refresh();
        }

        // R-L5-16: open it and make it the active document — no prompt, whether created or pre-existing.
        OpenOrActivateLayout(targetPath);
        var layoutVm = GetOrCreateLayoutSession(targetPath);

        // The INSTANCE half is skipped entirely for a wBond-only update: nothing else in the schematic
        // is re-resolved, so nothing else can move, be re-parameterised, or be reported.
        if (onlyWBond is null)
        {
            if (layoutVm.WorkspaceRootDir is not { Length: > 0 } workspaceRoot)
            {
                Messages.Error("Update Layout from Schematic: no workspace is open — generated PCell cells need a workspace to live in.");
                return;
            }

            var result = SchematicToLayoutGenerator.Run(
                doc.ViewModel.EditModel, layoutVm.Model, schematicDir, workspaceRoot, layoutDir,
                layoutVm.Technology, layoutVm.ResolvedTechPath, this);

            if (result.Command is not null)
                layoutVm.Execute(result.Command);

            ReportGenerationResult(result.Command, result.Lines, result.NoLayoutWarnings,
                result.AddedCount, result.UpdatedCount, result.UnchangedCount, result.RemovedCount,
                result.OverwrittenParameterCount, "Update Layout from Schematic");
        }

        SeedWBondSidecar(doc.ViewModel.EditModel, cellDir, schematicName, layoutVm, onlyWBond);

        // R-L5-17: primacy — new file with no prior real primary becomes primary automatically
        // (CellFolder's own SoleFile branch handles the common case for free); only the ambiguous
        // multi-file/no-named-primary case needs an explicit .ccell write. A differently-named primary
        // is left untouched and reported.
        if (createdNewFile)
        {
            if (hadNoRealPrimary)
            {
                var afterCreate = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
                if (afterCreate.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent))
                    SetPrimaryLayout(cellDir, Path.GetFileName(targetPath));
            }
            else
            {
                Messages.Warning($"'{primaryBeforeName}' remains this cell's primary layout — '{Path.GetFileName(targetPath)}' was written but not made primary.");
            }
        }
    }

    /// <summary>
    /// wbond.md §9.5/WB41 — the wBond half of Update Layout from Schematic.
    ///
    /// <para>A wBond has no layout view to place, so the generator above emits nothing for it (WB23: no
    /// wire enters a <c>.clay</c>). Its wires are the CELL's own <c>.wBond</c> sidecar instead, which is
    /// the same file <see cref="WBondCell"/> already loads for any cell that has one — so writing it
    /// here is what makes both halves of the owner's report work: the wires appear after Update Layout
    /// from Schematic, and they appear again every later time that <c>.clay</c> is opened.</para>
    ///
    /// <para><b>The session has to be told, not just the disk.</b> The layout document is already open
    /// by this point — created moments ago by the branch above, or long since — and a session built
    /// before the sidecar existed has no wire overlay on it. Attaching here is what puts the wires on
    /// screen now rather than on the next reopen.</para>
    /// </summary>
    private void SeedWBondSidecar(SchematicEditModel model, string cellDir, string cellName,
                                  LayoutEditorViewModel layoutVm, EditableComponent? only = null)
    {
        // The LIVE design when this cell's layout is already open with wires on it. Passing it is what
        // makes the merge correct rather than merely visible: an open editor holds its own design object
        // and writes it back on save, so merging through the file would change nothing on screen and be
        // overwritten moments later (owner, 2026-08-17, with the workspace attached — the `.wBond` held
        // G1 and G2 while the layout showed only G1).
        var seeded = WBondCellSeeding.Seed(model, cellDir, cellName, only, layoutVm.WireDesign);
        if (seeded.Outcome == WBondCellSeeding.Outcome.NoWBond) return;

        // Before the messages, so a repaint is not waiting behind a message sink.
        if (seeded.LiveDesignChanged) layoutVm.NotifyWireDesignChangedExternally();

        foreach (string line in seeded.Messages)
        {
            // The written file rides along on the success line so the Messages pane's own reveal
            // affordance points at it — the same shape every other "wrote a file" report here uses.
            // A MERGE is an ordinary success too (an array the schematic added arrived in the layout,
            // and nothing already drawn was touched); the lines it emits about what could NOT be
            // resolved are the ones that read as warnings, and they are worded to say so.
            if (seeded.Outcome is WBondCellSeeding.Outcome.Created or WBondCellSeeding.Outcome.Merged)
                Messages.Success(line, seeded.Path);
            else Messages.Warning(line);
        }

        if (!seeded.HasSidecar) return;

        // Already attached (this cell was opened after the sidecar existed) — nothing to do, and
        // re-attaching would replace the live editor the user may have unsaved wire edits in.
        if (layoutVm.WireDesign is not null) return;

        if (WBondCell.TryAttach(layoutVm, layoutVm.CurrentLayoutPath, m => Messages.Warning(m)))
        {
            layoutVm.AssemblyRules = ResolveWorkspaceAssemblyRules(layoutVm.CurrentLayoutPath!);
            _factory.WBondProfileTool?.SetActiveLayout(layoutVm);
            _factory.WBondInductanceTool?.SetActiveLayout(layoutVm);
        }

        // §10.1's two panels, shown the FIRST time a cell's wires reach its layout — and only then
        // (owner, 2026-08-17). Someone who has just generated wires has no reason to know two panels
        // exist, and the layout toolbar's own P/A buttons are how they are reached from then on. A
        // re-run deliberately leaves the arrangement exactly as the user has since set it: nothing is
        // more irritating than a command that re-opens a panel you closed on purpose.
        //
        // ARRANGED, not merely opened, the first time this installation ever needs them (owner,
        // 2026-08-17) — see ShowWBondPanels, which transcribes the owner's own placement for the two
        // of them and leaves everything else in the workspace alone.
        if (seeded.Outcome == WBondCellSeeding.Outcome.Created) ShowWBondPanels();

        _factory.ProjectTreeTool?.Refresh();
    }

    // ── Update Schematic from Layout (§3A) ───────────────────────────────────────────────────────

    /// <summary>
    /// Set by the shell's code-behind to the same "generate a symbol?" prompt the schematic canvas
    /// already shows (<c>SchematicView.ShowAutoGenPromptAsync</c>) — a dialog needs a Window, which this
    /// view model deliberately does not have. Null headless, where nothing is generated and the empty
    /// placeholder is reported instead of being left to speak for itself.
    /// </summary>
    public Func<string, Task<bool>>? AutoGenSymbolPrompt { get; set; }

    [RelayCommand(CanExecute = nameof(IsLayoutDocumentActive))]
    private async Task UpdateSchematicFromLayout()
    {
        if (ResolveActiveDocumentForCommands() is not LayoutDocument doc) return;

        if (doc.FilePath is not { Length: > 0 } layoutPath)
        {
            Messages.Error("Update Schematic from Layout: save the layout first — a scratch layout has no cell to write into.");
            return;
        }

        string layoutDir     = Path.GetDirectoryName(layoutPath)!;
        string cellDir       = Path.GetDirectoryName(layoutDir)!;
        string layoutName    = Path.GetFileNameWithoutExtension(layoutPath);
        string schematicDir  = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        string targetPath    = Path.Combine(schematicDir, layoutName + CellFolder.ViewExtension(ViewType.Schematic));

        var primaryBefore = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
        bool hadNoRealPrimary = primaryBefore.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent);
        string? primaryBeforeName = primaryBefore.ResolvedName;

        bool createdNewFile = !File.Exists(targetPath);
        if (createdNewFile)
        {
            Directory.CreateDirectory(schematicDir);
            SchematicPersistence.SaveToFile(targetPath, new SchematicEditModel(), cellName: Path.GetFileName(cellDir));
            _factory.ProjectTreeTool?.Refresh();
        }

        OpenOrActivateSchematic(targetPath);
        var schematicVm = GetOrCreateSession(targetPath);

        var layoutVm = GetOrCreateLayoutSession(layoutPath);
        var result = LayoutToSchematicGenerator.Run(layoutVm.Model, schematicVm.EditModel, layoutDir, layoutVm.Technology);

        if (result.Command is not null)
            schematicVm.Execute(result.Command);

        ReportGenerationResult(result.Command, result.Lines, [],
            result.CreatedCount, result.UpdatedCount, result.UnchangedCount, 0,
            result.OverwrittenParameterCount, "Update Schematic from Layout", overwrittenNoun: "schematic edits");

        await OfferSymbolsForPlacedCells(result.CellsWithoutSymbols);

        // §9.6/WB42 — the WIRE layer, which this command had no knowledge of at all (owner,
        // 2026-08-17: "if I change loop height in layout editor, the component in the schematic is not
        // updated. Even if I use Update Schematic from Layout"). Separate from the instance walk above
        // because it is a separate layer: LayoutToSchematicGenerator reads LayoutInstances, and no wire
        // is ever one of those (WB23 — no wire enters a .clay).
        //
        // The LIVE design, not the file on disk, so an unsaved wire edit reconciles too — matching the
        // instance half, which reads layoutVm.Model rather than re-reading the .clay.
        var wires = WBondSchematicReconcile.Run(schematicVm.EditModel, layoutVm.WireDesign);
        if (wires.Command is not null) schematicVm.Execute(wires.Command);

        foreach (string line in wires.Messages)
        {
            if (wires.ArraysMoved) Messages.Warning(line);
            else Messages.Success(line);
        }

        if (createdNewFile)
        {
            if (hadNoRealPrimary)
            {
                var afterCreate = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
                if (afterCreate.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent))
                    SetPrimarySchematic(cellDir, Path.GetFileName(targetPath));
            }
            else
            {
                Messages.Warning($"'{primaryBeforeName}' remains this cell's primary schematic — '{Path.GetFileName(targetPath)}' was written but not made primary.");
            }
        }
    }

    /// <summary>
    /// Owner, 2026-08-17: an instance this command placed "does not render the pins" — because the cell
    /// it names has no symbol, so it draws as a bare placeholder.
    ///
    /// <para><b>This is the offer the Library palette already makes, arriving at the same cell by a
    /// different route.</b> Dropping a symbol-less cell from the palette asks "a symbol for X has not
    /// been created. Do you want one to be auto-generated?"; the same cell reaching the schematic
    /// through Update Schematic from Layout used to place silently and leave the user with a blank box.
    /// Asked ONCE for the whole run rather than per cell — this command places many instances at a time,
    /// and a prompt per cell would be a queue of dialogs where the palette has a single click.</para>
    ///
    /// <para><b>The pin count comes from <see cref="CellPortCount"/>, not from <c>.ccell NumPorts</c>
    /// alone.</b> Nothing derives that field, so a cell whose schematic the user drew with N pins — and
    /// whose cell editor they never opened — declares zero, and the fixed fallback of 2 would generate a
    /// two-pin symbol for it. Reading the cell's own schematic is what makes the generated symbol match
    /// the cell it stands for.</para>
    /// </summary>
    internal async Task OfferSymbolsForPlacedCells(IReadOnlyList<string> cellDirs)
    {
        if (cellDirs.Count == 0) return;

        var names = cellDirs.Select(d => Path.GetFileName(d)).ToList();
        string subject = names.Count == 1
            ? $"A symbol for \"{names[0]}\" has not been created"
            : $"{names.Count} cells placed from this layout have no symbol ({string.Join(", ", names)})";

        // Declined, or no host window to ask through (headless). Either way the instances ARE on the
        // schematic and the user has to be told why they look empty — the whole defect being fixed here
        // is that this case said nothing at all.
        bool generate = AutoGenSymbolPrompt is { } prompt
                     && await prompt($"{subject}. Do you want one to be auto-generated? Without a symbol " +
                                     "the instance draws as an empty box with no pins.");
        if (!generate)
        {
            Messages.Warning(names.Count == 1
                ? $"\"{names[0]}\" was placed without a symbol — it draws as an empty box with no pins until one exists."
                : $"{names.Count} instances were placed without symbols — they draw as empty boxes with no pins until symbols exist.");
            return;
        }

        foreach (string cellDir in cellDirs)
        {
            try
            {
                string cellName = Path.GetFileName(cellDir);
                var sym = AutoSymbolGenerator.Generate(cellName, CellPortCount.Resolve(cellDir));
                string symDir = CellFolder.SubFolderPath(cellDir, ViewType.Symbol);
                Directory.CreateDirectory(symDir);
                string path = Path.Combine(symDir, cellName + CellFolder.ViewExtension(ViewType.Symbol));
                SymbolPersistence.SaveToFile(path, sym);

                // Through the SAME handler the palette's own auto-generation calls, not a second copy
                // of it: the placed component was already drawn against the absence of this file, so it
                // keeps rendering the placeholder until the resolver cache is dropped and every open
                // schematic rebuilds. Getting that wrong looks exactly like the symbol not being there.
                OnCellSymbolAutoGenerated(cellDir);
                Messages.Success($"Generated a {sym.Pins.Count}-pin symbol for \"{cellName}\".", path);
            }
            catch (Exception ex)
            {
                Messages.Error($"Could not generate a symbol for \"{Path.GetFileName(cellDir)}\": {ex.Message}");
            }
        }
    }

    // ── Shared file-primacy helpers (mirrors MakePrimary's own .ccell write) ────────────────────────

    private void SetPrimaryLayout(string cellDir, string fileName) => SetPrimary(cellDir, ccell => ccell.PrimaryLayout = fileName);
    private void SetPrimarySchematic(string cellDir, string fileName) => SetPrimary(cellDir, ccell => ccell.PrimarySchematic = fileName);

    private void SetPrimary(string cellDir, Action<CcellFile> apply)
    {
        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        if (!File.Exists(ccellPath)) return;
        try
        {
            var ccell = CellPersistence.LoadFromFile(ccellPath);
            apply(ccell);
            CellPersistence.SaveToFile(ccellPath, ccell);
            _factory.ProjectTreeTool?.Refresh();
        }
        catch (Exception ex)
        {
            Messages.Error($"Could not set primary view: {ex.Message}");
        }
    }

    // ── Shared change report (R-L5-13/14/22) ─────────────────────────────────────────────────────

    /// <summary>
    /// R-L5-13: one line per INSTANCE (grouped — an instance's several changed parameters share one
    /// group), capped at <paramref name="cap"/> instances, with a trailing summary that keeps the
    /// overwritten-parameter count visible even when details are truncated. R-L5-14: posts NOTHING
    /// (not even a summary) when <paramref name="command"/> is null AND there is nothing else to say.
    /// Shared verbatim by both directions (R-L5-22: "same cap, same silence, same single undoable
    /// action").
    /// </summary>
    private void ReportGenerationResult(
        Commands.IUiCommand? command,
        IReadOnlyList<SchematicToLayoutGenerator.ReportLine> lines,
        IReadOnlyList<string> extraWarnings,
        int addedOrCreated, int updated, int unchanged, int removed, int overwrittenParamCount,
        string commandLabel, int cap = 20, string overwrittenNoun = "layout edits")
    {
        foreach (var w in extraWarnings) Messages.Warning(w);

        if (command is null)
            return; // R-L5-14 — nothing changed, say nothing (the extra warnings above, if any, are a
                     // separate, persistent concern and are not silenced by "nothing changed this run").

        var byInstance = lines
            .GroupBy(l => l.InstanceName)
            .ToList();

        int shown = Math.Min(cap, byInstance.Count);
        for (int i = 0; i < shown; i++)
            foreach (var line in byInstance[i])
                Messages.Post(line.Severity == SchematicToLayoutGenerator.ReportSeverity.Warning ? MessageLevel.Warning : MessageLevel.Info, line.Text);

        if (byInstance.Count > cap)
        {
            int remaining = byInstance.Count - cap;
            Messages.Info($"…and {remaining} more instance(s) updated. {overwrittenParamCount} had {overwrittenNoun} overwritten.");
        }

        string removedSuffix = removed > 0 ? $", {removed} no longer in the schematic" : "";
        Messages.Success($"{commandLabel}: {addedOrCreated} added, {updated} updated, {unchanged} unchanged{removedSuffix}.");
    }
}
