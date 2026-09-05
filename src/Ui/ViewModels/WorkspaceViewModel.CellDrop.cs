using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.Views.Dialogs;
using CircuitRF.Ui.Schematic;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Taking a cell — or a loose file — from ANOTHER workspace into this one (MW3).
///
/// <para><b>One flow, two gestures.</b> Dragging a cell from workspace A's Project Tree onto
/// workspace B's, and <c>File ▸ Add Cell to Workspace…</c>, ask the same question and run the same
/// code: the menu exists because a drag between two windows is awkward when one of them is not on
/// screen, not because it is a different operation. MW2 R-mw2-6 named two entry points for creating
/// an external reference and this is still two — the menu item IS the drag gesture, reached by
/// keyboard.</para>
///
/// <para><b>The modal is shown after the drag has returned</b> (R-mw3-6): showing one from inside a
/// drop handler, while the platform drag loop is still unwound, is how a drag-drop deadlock is
/// written. The view posts the call; nothing here runs on the drag's own stack.</para>
/// </summary>
public partial class WorkspaceViewModel
{
    /// <summary>
    /// File ▸ Add Cell to Workspace… — pick a cell folder anywhere on disk and take it in, by copy
    /// or by reference. The same prompt the drag gesture shows, and the same outcomes.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddCellToWorkspace))]
    private async Task AddCellToWorkspace(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null || CurrentWorkspacePath is null) return;

        IStorageFolder? startLocation = null;
        try { startLocation = await window.StorageProvider.TryGetFolderFromPathAsync(_lastWorkspaceParentDir); }
        catch { }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                  = "Add Cell to Workspace",
            AllowMultiple          = false,
            SuggestedStartLocation = startLocation,
        });
        if (folders.Count == 0) return;

        await AcceptCellFromOtherWorkspaceCoreAsync(
            Path.GetFullPath(folders[0].Path.LocalPath), destFolderDir: null);
    }

    private bool CanAddCellToWorkspace() => CurrentWorkspacePath is not null;

    /// <inheritdoc cref="Schematic.IHierarchyHost.CanReferenceExternalCell"/>
    public bool CanReferenceExternalCell => CurrentWorkspacePath is not null;

    /// <inheritdoc cref="Schematic.IHierarchyHost.ReferenceExternalCellAsync"/>
    ///
    /// <remarks>
    /// The cell picker's <b>Reference Cell…</b> button, and deliberately the SAME folder pick and the
    /// SAME prompt as <c>File ▸ Add Cell to Workspace…</c> — a third way to bring a cell in would be a
    /// third place for "reference or copy?", "bring its technology?" and the collision prompts to
    /// drift. Only the answer differs: this one reports where the cell ended up, so the picker's
    /// caller can go straight on and place it.
    /// </remarks>
    public async Task<string?> ReferenceExternalCellAsync()
    {
        var window = ResolveOwner(null);
        if (window is null || CurrentWorkspacePath is null) return null;

        IStorageFolder? startLocation = null;
        try { startLocation = await window.StorageProvider.TryGetFolderFromPathAsync(_lastWorkspaceParentDir); }
        catch { }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                  = "Reference Cell",
            AllowMultiple          = false,
            SuggestedStartLocation = startLocation,
        });
        if (folders.Count == 0) return null;

        return await AcceptCellFromOtherWorkspaceCoreAsync(
            Path.GetFullPath(folders[0].Path.LocalPath), destFolderDir: null);
    }

    // ── The shared flow ───────────────────────────────────────────────────────

    /// <summary>
    /// Takes <paramref name="sourceCellDir"/> — a cell folder belonging to some other workspace —
    /// into this one, asking first.
    /// </summary>
    /// <param name="destFolderDir">The folder the cell was dropped on; null means the workspace root.</param>
    public Task AcceptCellFromOtherWorkspaceAsync(string sourceCellDir, string? destFolderDir)
        => AcceptCellFromOtherWorkspaceCoreAsync(sourceCellDir, destFolderDir);

    /// <summary>
    /// The flow itself, reporting the ABSOLUTE cell folder the cell now occupies here — the source
    /// folder for a reference (it stays where it is), the new folder for a copy — or null when
    /// nothing was taken in.
    ///
    /// <para>The drag and the File-menu gesture both discard that answer; the cell picker's
    /// <b>Reference Cell…</b> button is the one caller that needs it, because its whole point is to
    /// place the cell it has just brought in. Split from the public entry point rather than widening
    /// it: <c>ITreeActions</c> exposes the void-shaped one and has no use for a return value.</para>
    /// </summary>
    internal async Task<string?> AcceptCellFromOtherWorkspaceCoreAsync(string sourceCellDir, string? destFolderDir)
    {
        if (CurrentWorkspacePath is null) return null;
        var window = ResolveOwner(null);
        if (window is null) return null;

        string myRoot = Path.GetDirectoryName(CurrentWorkspacePath)!;
        string source = Path.GetFullPath(sourceCellDir);

        if (!File.Exists(Path.Combine(source, CellFolder.CcellFileName)))
        {
            Messages.Error($"'{Path.GetFileName(source)}' is not a cell folder (no {CellFolder.CcellFileName}).");
            return null;
        }

        // R-mw3-4: a cell already in this workspace has nothing to add. The drop handler answers
        // DragDropEffects.None for that case so the gesture never starts; the MENU can still reach it,
        // and it says so rather than making a pointless copy of a cell beside itself.
        if (!WorkspaceRootFinder.IsOutside(source, myRoot))
        {
            Messages.Info($"'{Path.GetFileName(source)}' is already in this workspace. "
                        + "Use Duplicate Cell to make a copy of it here.");
            return null;
        }

        string dest = destFolderDir is null ? myRoot : Path.GetFullPath(destFolderDir);
        if (!Directory.Exists(dest) || WorkspaceRootFinder.IsOutside(dest, myRoot)) dest = myRoot;

        string? sourceRoot = WorkspaceRootFinder.WorkspaceDirOf(source);

        // The plan walks the cell's whole hierarchy — every reachable .csch and .clay, for the kit
        // scan and the technology comparison — and that is what a user used to wait for with a frozen
        // window before the dialog appeared (owner, 2026-09-05: ~60 s on a large cell). It now runs
        // OFF the UI thread while the dialog is already up.
        //
        // <b>Its own TechnologyCache, deliberately.</b> Every other cache this walk touches
        // (WorkspaceRootFinder, CellStat, CellSymbolResolver, CellLayoutResolver, PdkKitRegistry) is
        // lock-guarded and safe to share; TechnologyCache is a plain Dictionary with no gate, and the
        // UI thread holds the shared one. A private cache costs one extra .ctech read and removes the
        // race rather than papering over it.
        var pendingPlan = Task.Run(() =>
            CrossWorkspaceCellCopy.Plan(source, dest, myRoot, SubCellMode.Copy, cache: new TechnologyCache()));

        var dialog = new AddCellToWorkspaceDialog(
            Path.GetFileName(source),
            sourceRoot is null ? "(no workspace)" : FolderLeaf(sourceRoot),
            FolderLeaf(myRoot),
            // Answered from the top cell alone, so it is known before the walk finishes — and it is
            // the same answer the walk would give, not an approximation. See HasSubCells.
            hasSubCells: CrossWorkspaceCellCopy.HasSubCells(source, sourceRoot),
            sourceIsInAWorkspace: sourceRoot is not null,
            pendingPlan: pendingPlan);

        var choice = await dialog.ShowDialog<AddCellChoice?>(window);
        if (choice is null) return null;

        // Completed by construction: the dialog does not enable OK until it lands, so this neither
        // blocks nor re-walks. Awaited rather than assumed so a faulted plan surfaces as an error
        // here instead of as an exception inside the copy.
        CrossWorkspaceCellCopy.CellCopyPlan preview;
        try { preview = await pendingPlan; }
        catch (Exception ex) { Messages.Error($"Could not examine '{Path.GetFileName(source)}': {ex.Message}"); return null; }

        if (choice.Reference)
        {
            // §5C.2a/R47e applied at the workspace rather than at one layout: the referenced cell stays
            // where it is, so the layouts that will place it resolve their technology through the
            // workspace DEFAULT — making it the default is what turns the refusal into a permit.
            // Through R47f's confirmation, which is where the size of that decision is stated; a
            // cancel there leaves the reference uncreated rather than creating one that cannot be
            // placed.
            if (choice.BringTechnology && !await AdoptTechnologyForWorkspaceAsync(preview.Technology))
                return null;

            // The cell stays where it is, so where it is IS the answer.
            return ReferenceExternalCell(myRoot, sourceRoot!, source) ? source : null;
        }

        return await CopyExternalCellAsync(
            window, myRoot, sourceRoot, source, dest, choice.SubCells, choice.BringTechnology,
            // Reuse the plan already computed for the dialog when the user kept the mode it was
            // computed for — the walk it embodies is the expensive part, and re-doing it would put
            // the cost straight back, just after the dialog instead of before it.
            choice.SubCells == SubCellMode.Copy ? preview : null);
    }

    // ── Reference ─────────────────────────────────────────────────────────────

    /// <summary>
    /// References ONE cell where it lives.
    ///
    /// <para><b>One cell in, one row out</b> (owner, 2026-09-04). The alias is still what a
    /// <c>ws://</c> reference resolves through and is still created once per target workspace — that
    /// part of MW2 is unchanged — but an alias created BY this gesture is recorded
    /// <see cref="CwsWorkspaceRef.CellsOnly"/> and draws no row, and the CELL is listed instead. The
    /// previous behaviour listed the alias alone, so taking one cell from a colleague's project put
    /// their entire catalogue of cells in this workspace's tree, which is the opposite of what the
    /// gesture names.</para>
    /// </summary>
    /// <returns>True when the reference is recorded — the cell picker's Reference Cell… button then
    /// places it, so a failure must not read as a silent success.</returns>
    private bool ReferenceExternalCell(string myRoot, string sourceRoot, string sourceCellDir)
    {
        // R-mw3-7 / MW2 §2: the alias is created once and REUSED. Two aliases for one workspace would
        // make the same cell reachable under two names, and a rename repair would then have to guess.
        string? alias = ExistingAliasFor(myRoot, sourceRoot);

        if (alias is null)
        {
            alias = UniqueAlias(myRoot, FolderLeaf(sourceRoot));
            if (!AddReferencedWorkspace(
                    myRoot, alias, Path.Combine(sourceRoot, ".cws"), out string? error, cellsOnly: true))
            {
                Messages.Error(error!);
                return false;
            }
        }

        string cellRef = ExternalCellRef.RefFor(
            alias, Path.GetRelativePath(sourceRoot, sourceCellDir).Replace('\\', '/'));

        if (!AddReferencedCell(myRoot, cellRef, out string? cellError, out bool alreadyListed))
        {
            Messages.Error(cellError!);
            return false;
        }

        string cellName = Path.GetFileName(sourceCellDir);
        Messages.Success(alreadyListed
            ? $"'{cellName}' is already referenced here ({cellRef})."
            : $"'{cellName}' is now referenced from '{FolderLeaf(sourceRoot)}' and appears in the "
              + $"Project Tree ({cellRef}).");

        RefreshAfterReferenceChange();
        return true;
    }

    // ── Copy ──────────────────────────────────────────────────────────────────

    /// <returns>The absolute folder the copy landed in, or null when it was cancelled or failed.</returns>
    private async Task<string?> CopyExternalCellAsync(
        Window window, string myRoot, string? sourceRoot,
        string sourceCellDir, string destParentDir, SubCellMode mode, bool bringTechnology,
        CrossWorkspaceCellCopy.CellCopyPlan? alreadyPlanned = null)
    {
        var plan = (alreadyPlanned
                    ?? CrossWorkspaceCellCopy.Plan(sourceCellDir, destParentDir, myRoot, mode, cache: _techCache))
            with { BringTechnology = bringTechnology };

        // R-mw3-9: collisions are ASKED about, never auto-suffixed. `Amp_2` appearing in someone's
        // project without their say-so is worse than a second dialog.
        var resolved = await ResolveCollisionsAsync(window, plan.Folders);
        if (resolved is null) return null;
        plan = plan with { Folders = resolved, DestCellDir = resolved[0].DestDir };

        // The alias has to exist BEFORE the rewrite: ExternalCellRef.MakeCellRef reads the table, and
        // a rewrite run without it would silently emit the raw relative form R-mw2-5 forbids writing.
        if (plan.NeedsSourceAlias && sourceRoot is not null && ExistingAliasFor(myRoot, sourceRoot) is null)
        {
            string alias = UniqueAlias(myRoot, FolderLeaf(sourceRoot));
            if (!AddReferencedWorkspace(myRoot, alias, Path.Combine(sourceRoot, ".cws"), out string? error))
            {
                Messages.Error(error!);
                return null;
            }
        }

        string written;
        try { written = CrossWorkspaceCellCopy.Execute(plan); }
        catch (Exception ex) { Messages.Error($"Copy failed: {ex.Message}"); return null; }

        RefreshAfterReferenceChange();

        int extra = plan.Folders.Count - 1;
        Messages.Success(
            extra == 0 ? "Copied" : $"Copied, with {extra} sub-cell{(extra == 1 ? "" : "s")}", written);

        // R47g. Both answers are worth a line: what the copy is drawn with is not visible in the tree,
        // and the second case is the one where the shapes have just changed meaning on purpose.
        if (plan.TechnologyNeedsAnswer)
            Messages.Info(bringTechnology
                ? $"{plan.Technology.TechnologyDisplay} came with it; the copied layouts point at "
                  + "the copy in this workspace's tech/ folder."
                : "The copied layouts use this workspace's technology — their layer numbers are "
                  + "unchanged and now carry this workspace's meanings.");

        return written;
    }

    /// <summary>
    /// Asks for a new name for every planned folder that already exists, and re-points anything
    /// nested under a renamed one. Null when the user cancelled — a half-copied hierarchy is not a
    /// state worth producing.
    /// </summary>
    private async Task<List<CrossWorkspaceCellCopy.CopiedFolder>?> ResolveCollisionsAsync(
        Window window, IReadOnlyList<CrossWorkspaceCellCopy.CopiedFolder> folders)
    {
        var result  = folders.ToList();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < result.Count; i++)
        {
            string dest = result[i].DestDir;

            // Two sub-cells of the same name, reached from different corners of the source workspace,
            // want the same folder here. That is a collision like any other and is ASKED about — the
            // alternative is one of them silently overwriting the other on the way in.
            bool taken = !claimed.Add(dest);
            if (!taken && !Directory.Exists(dest) && !File.Exists(dest)) continue;

            string parent = Path.GetDirectoryName(dest)!;
            string name   = Path.GetFileName(dest);

            while (true)
            {
                var dlg = new InputNameDialog(
                    "Name Already Taken", $"'{name}' already exists here. New cell name:", name);
                var chosen = await dlg.ShowDialog<string?>(window);
                if (string.IsNullOrWhiteSpace(chosen)) return null;
                chosen = chosen.Trim();

                if (NameValidator.Validate(chosen) is { } reason)
                {
                    Messages.Error($"Invalid cell name: {reason}");
                    continue;
                }

                string candidate = Path.Combine(parent, chosen);
                if (Directory.Exists(candidate) || File.Exists(candidate) || claimed.Contains(candidate))
                {
                    name = chosen;
                    continue;
                }
                claimed.Add(candidate);

                // Anything the plan placed INSIDE the folder just renamed moves with it.
                string oldPrefix = dest + Path.DirectorySeparatorChar;
                for (int j = 0; j < result.Count; j++)
                {
                    if (j == i) { result[j] = result[j] with { DestDir = candidate }; continue; }
                    if (result[j].DestDir.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                        result[j] = result[j] with
                        { DestDir = Path.Combine(candidate, result[j].DestDir[oldPrefix.Length..]) };
                }
                break;
            }
        }

        return result;
    }

    // ── Loose files, and a .cws (MW3 §5) ──────────────────────────────────────

    /// <summary>
    /// A non-cell file dragged from another workspace's tree is COPIED into this one (R-mw3-11),
    /// with the same collision prompt and no Reference option — a loose <c>.s2p</c>, <c>.npy</c> or
    /// <c>.ctech</c> has no reference semantics in a <c>.cws</c>, and offering one would be a fourth
    /// path convention beside the three <c>DocumentFileRefs.RefBase</c> already carries.
    /// </summary>
    public async Task AcceptDroppedFileAsync(string sourceFile, string? destFolderDir)
    {
        if (CurrentWorkspacePath is null) return;
        var window = ResolveOwner(null);
        if (window is null || !File.Exists(sourceFile)) return;

        string myRoot = Path.GetDirectoryName(CurrentWorkspacePath)!;
        string dest   = destFolderDir is null ? myRoot : Path.GetFullPath(destFolderDir);
        if (!Directory.Exists(dest) || WorkspaceRootFinder.IsOutside(dest, myRoot)) dest = myRoot;

        string name = Path.GetFileName(sourceFile);
        string target = Path.Combine(dest, name);

        while (File.Exists(target) || Directory.Exists(target))
        {
            var dlg = new InputNameDialog(
                "Name Already Taken", $"'{name}' already exists here. New file name:", name);
            var chosen = await dlg.ShowDialog<string?>(window);
            if (string.IsNullOrWhiteSpace(chosen)) return;
            name   = chosen.Trim();
            target = Path.Combine(dest, name);
        }

        try { File.Copy(sourceFile, target); }
        catch (Exception ex) { Messages.Error($"Copy failed: {ex.Message}"); return; }

        _factory.ProjectTreeTool?.Refresh();
        Messages.Success("Copied", target);
    }

    /// <summary>
    /// A <c>.cws</c> dropped on a tree OPENS that workspace in a window of its own (R-mw3-12) and is
    /// never copied — copying a workspace into a workspace is not a thing.
    /// </summary>
    public static void OpenDroppedWorkspace(string cwsPath) => App.OpenWorkspaceInNewWindow(cwsPath);
}
