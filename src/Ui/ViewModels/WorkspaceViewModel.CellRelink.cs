using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Putting a broken cell reference back together (owner, 2026-09-04).
///
/// <para>Removing a reference leaves every instance that placed it reading <b>Not Found</b>, and until
/// now the only way back was to re-create the reference by hand and hope the alias was spelled the
/// same. <b>Re-reference Cell…</b> is the gesture that does it: it looks for the cell first — in this
/// workspace, in the ones open in other windows, in the recent list — and asks only when that fails.
/// The answer is then recorded as a reference like any other, so a schematic with eleven instances of
/// one broken cell is repaired once.</para>
/// </summary>
public partial class WorkspaceViewModel
{
    /// <inheritdoc/>
    public async Task ReReferenceCellAsync(SchematicDocument doc, EditableComponent comp)
    {
        var vm = doc.ViewModel;
        if (comp.CellRef is not { Length: > 0 } cellRef) return;

        // A cell reference is relative to the document's own folder, so an unsaved schematic has
        // nothing to write one against — and its instances read NotFound for that reason alone.
        if (vm.EditModel.SchematicDirectory is not { Length: > 0 } baseDir)
        {
            Messages.Warning("Save this schematic first — a cell reference is written relative to the "
                           + "folder the schematic is saved in, and this one has none yet.");
            return;
        }

        if (await FindOrAskForCellAsync(cellRef, comp.InstanceName, baseDir) is not { } target) return;

        if (RecordReferenceFor(cellRef, baseDir, target.Dir) is not { } outcome) return;

        int rewritten = vm.RelinkCellReferences(cellRef, outcome.NewRef);
        RefreshAfterReferenceChange();
        ReportRelink(target.Dir, outcome, rewritten, target.FoundBy);
    }

    /// <inheritdoc/>
    public async Task ReReferenceInstanceCellAsync(LayoutDocument doc, LayoutInstance instance)
    {
        if (doc.ActiveViewModel is not { } vm) return;
        if (instance.CellRef is not { Length: > 0 } cellRef) return;

        // The same rule as the schematic's, and for the same reason: a scratch layout has no folder
        // for a reference to be relative to, which is why every instance in it reads NotFound.
        if (vm.InstanceBaseDir is not { Length: > 0 } baseDir)
        {
            Messages.Warning("Save this layout first — a cell reference is written relative to the "
                           + "folder the layout is saved in, and this one has none yet.");
            return;
        }

        string name = Path.GetFileName(cellRef.Replace('\\', '/').TrimEnd('/'));
        if (await FindOrAskForCellAsync(cellRef, name, baseDir) is not { } target) return;

        if (RecordReferenceFor(cellRef, baseDir, target.Dir) is not { } outcome) return;

        int rewritten = vm.RelinkCellReferences(cellRef, outcome.NewRef);
        RefreshAfterReferenceChange();
        ReportRelink(target.Dir, outcome, rewritten, target.FoundBy);
    }

    /// <summary>Where the cell turned out to be, and how it was found.</summary>
    private readonly record struct RelinkTarget(string Dir, CellRefFoundBy FoundBy);

    /// <summary>
    /// The search, and the picker when it comes up empty — the half both editors share.
    /// Null means there is nothing more to do: the reference was not repairable, it already resolves
    /// (and the view has been refreshed), or the user cancelled.
    /// </summary>
    private async Task<RelinkTarget?> FindOrAskForCellAsync(string cellRef, string instanceName, string baseDir)
    {
        if (!CellReferenceRepair.IsRepairable(cellRef))
        {
            // A kit part or a wBond reads NotFound for reasons a folder cannot answer.
            Messages.Info($"'{instanceName}' does not reference a cell folder, so there is nothing to "
                        + "re-reference. Check the kit or the component's own File parameter.");
            return null;
        }

        var found = CellReferenceRepair.Find(cellRef, baseDir, RepairSearchRoots());

        if (found.FoundBy == CellRefFoundBy.AlreadyResolves)
        {
            // Nothing is broken any more; the document was showing a stale render. Say so rather than
            // silently doing nothing — the user is looking at a "Not Found" glyph as they read this.
            RefreshAfterReferenceChange();
            Messages.Info($"'{CellName(cellRef)}' resolves again — the view has been refreshed.");
            return null;
        }

        string? dir = found.CellDir ?? await AskForCellFolderAsync(cellRef);
        return dir is null ? null : new RelinkTarget(dir, found.FoundBy);
    }

    /// <summary>
    /// Where to look, most likely first — and the order is the whole of the tie-breaking rule, since
    /// <see cref="CellReferenceRepair"/> refuses to choose between two cells of the same name.
    /// This workspace comes first (a cell that moved INSIDE the project is the commonest break of
    /// all), then the workspaces open in other windows, then the ones this workspace already
    /// references, then the recent list — decreasingly good guesses, all of them cheap.
    /// </summary>
    private IReadOnlyList<string> RepairSearchRoots()
    {
        var roots = new List<string>();
        if (CurrentWorkspaceRoot is { Length: > 0 } mine) roots.Add(mine);
        roots.AddRange(OtherOpenWorkspaceRoots());

        if (CurrentWorkspaceRoot is { Length: > 0 } root)
        {
            try
            {
                var cws = WorkspacePersistence.LoadFromFile(Path.Combine(root, ".cws"));
                foreach (var entry in cws.ReferencedWorkspaces ?? [])
                    if (ExternalCellRef.WorkspaceRootForAlias(root, entry.Alias) is { } other)
                        roots.Add(other);
            }
            catch { /* a .cws we cannot read contributes no candidates, which is not an error here */ }
        }

        foreach (var (_, cwsPath) in GetRecentWorkspaces())
            if (Path.GetDirectoryName(cwsPath) is { Length: > 0 } dir)
                roots.Add(dir);

        return roots;
    }

    /// <summary>The picker, when the search found nothing (or found two equally good answers).</summary>
    private async Task<string?> AskForCellFolderAsync(string cellRef)
    {
        var window = ResolveOwner(null);
        if (window is null) return null;

        IStorageFolder? start = null;
        try { start = await window.StorageProvider.TryGetFolderFromPathAsync(_lastWorkspaceParentDir); }
        catch { }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                  = $"Locate Cell '{CellName(cellRef)}'",
            AllowMultiple          = false,
            SuggestedStartLocation = start,
        });
        if (folders.Count == 0) return null;

        string picked = Path.GetFullPath(folders[0].Path.LocalPath);
        if (!File.Exists(Path.Combine(picked, CellFolder.CcellFileName)))
        {
            Messages.Error($"'{Path.GetFileName(picked)}' is not a cell folder "
                         + $"(no {CellFolder.CcellFileName}). Pick the cell's own folder.");
            return null;
        }
        return picked;
    }

    /// <summary>What was recorded for the repaired reference, and what the document should now say.</summary>
    private readonly record struct RelinkOutcome(string NewRef, string? Alias, string? OtherRoot);

    /// <summary>
    /// Records whatever reference <paramref name="targetDir"/> needs — an alias, a Project Tree row —
    /// and hands back the reference the document should carry. Null when the <c>.cws</c> could not be
    /// written (the refusal has already been said).
    ///
    /// <para><b>The old alias is asked for by name first.</b> When the cell is where it always was and
    /// only the <c>.cws</c> entry went missing, re-declaring the SAME alias makes the reference the
    /// document already holds resolve again — no document edit, no undo entry, and nothing for a
    /// colleague to review. A different name would rewrite every instance for no reason.</para>
    /// </summary>
    private RelinkOutcome? RecordReferenceFor(string oldRef, string baseDir, string targetDir)
    {
        if (CurrentWorkspacePath is null) return null;
        string myRoot = Path.GetDirectoryName(CurrentWorkspacePath)!;

        string? otherRoot = WorkspaceRootFinder.IsOutside(targetDir, myRoot)
            ? WorkspaceRootFinder.WorkspaceDirOf(targetDir)
            : null;

        string? alias = null;
        if (otherRoot is not null)
        {
            alias = ExistingAliasFor(myRoot, otherRoot);
            if (alias is null)
            {
                // The reference's own alias, when it is free — see the note above.
                string wanted = ExternalCellRef.TryParse(oldRef, out string a, out _) && a.Length > 0
                    ? a : FolderLeaf(otherRoot);

                if (!AddReferencedWorkspace(
                        myRoot, wanted, Path.Combine(otherRoot, ".cws"), out string? error, cellsOnly: true))
                {
                    wanted = UniqueAlias(myRoot, FolderLeaf(otherRoot));
                    if (!AddReferencedWorkspace(
                            myRoot, wanted, Path.Combine(otherRoot, ".cws"), out error, cellsOnly: true))
                    {
                        Messages.Error(error!);
                        return null;
                    }
                }
                alias = wanted;
            }

            // The cell earns its own row in the Project Tree, exactly as a cell referenced through
            // File ▸ Add Cell to Workspace… does — this IS that reference, arrived at from the other end.
            if (!AddReferencedCell(
                    myRoot,
                    ExternalCellRef.RefFor(alias, Path.GetRelativePath(otherRoot, targetDir).Replace('\\', '/')),
                    out string? cellError, out _))
            {
                Messages.Error(cellError!);
                return null;
            }
        }

        // Read AFTER the alias exists: MakeCellRef reads the alias table, and without the entry it
        // would emit the raw ../.. form R-mw2-5 forbids writing.
        WorkspaceRootFinder.InvalidateCache();
        return new RelinkOutcome(ExternalCellRef.MakeCellRef(baseDir, targetDir), alias, otherRoot);
    }

    /// <summary>What happened, in one sentence — including the case where the stored reference was
    /// already right and only the <c>.cws</c> entry had gone missing.</summary>
    private void ReportRelink(string targetDir, RelinkOutcome outcome, int rewritten, CellRefFoundBy foundBy)
    {
        string cell  = Path.GetFileName(targetDir);
        string where = outcome.OtherRoot is null ? "this workspace" : $"'{FolderLeaf(outcome.OtherRoot)}'";

        if (rewritten == 0)
        {
            Messages.Success(
                $"'{cell}' is referenced again from {where}"
              + (outcome.Alias is null ? "." : $" as \"{outcome.Alias}\".")
              + " The instances that place it resolve as they did.");
            return;
        }

        Messages.Success(
            $"{rewritten} instance{(rewritten == 1 ? "" : "s")} of '{cell}' "
          + $"{(rewritten == 1 ? "now points" : "now point")} at {where}"
          + (outcome.Alias is null ? "" : $" (\"{outcome.Alias}\")")
          + (foundBy == CellRefFoundBy.UniqueName ? " — the cell had moved." : "."));
    }

    /// <summary>The cell's own name as the broken reference spells it — for a message, when there is
    /// no folder on disk to take one from.</summary>
    private static string CellName(string cellRef)
    {
        string trimmed = cellRef.Replace('\\', '/').TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
    }

    /// <summary>
    /// Everything that has to happen after this workspace's references change — one method, called by
    /// every gesture that adds, promotes or removes one.
    ///
    /// <para><b>The rebuild is the part that was missing</b> (owner, 2026-09-04: re-adding a reference
    /// left the instances drawing "Not Found" until one of them was dragged). A schematic's render
    /// model carries each component's resolution state, computed when the model is BUILT — so
    /// dropping the resolver caches fixes what the next resolution answers and changes nothing on
    /// screen. The drag was doing the real work: it was an edit, and an edit rebuilds the model.</para>
    /// </summary>
    private void RefreshAfterReferenceChange()
    {
        // The alias table and the cell/symbol caches all key off references that have just changed.
        WorkspaceRootFinder.InvalidateCache();
        CellSymbolResolver.InvalidateAll();
        CellLayoutResolver.InvalidateAll();

        _factory.ProjectTreeTool?.Refresh();
        RebuildOpenSchematics();
        RepaintOpenLayouts();
    }

    /// <summary>Nudges every open layout frame to re-resolve its instances — the same broadcast the
    /// live-view seam makes, which is safe to over-send (a repaint that finds nothing changed routes
    /// to MarkInstancesDirty, not a shape rebuild).</summary>
    private void RepaintOpenLayouts()
    {
        foreach (var doc in _openDocsByPath.Values.OfType<LayoutDocument>().Concat(_scratchLayouts))
            foreach (var (session, _) in doc.NavFrames)
                session.Model.NotifyChanged(LayoutChangeInfo.InstancesOnly);
    }
}
