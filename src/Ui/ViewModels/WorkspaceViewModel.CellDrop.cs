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

        await AcceptCellFromOtherWorkspaceAsync(
            Path.GetFullPath(folders[0].Path.LocalPath), destFolderDir: null);
    }

    private bool CanAddCellToWorkspace() => CurrentWorkspacePath is not null;

    // ── The shared flow ───────────────────────────────────────────────────────

    /// <summary>
    /// Takes <paramref name="sourceCellDir"/> — a cell folder belonging to some other workspace —
    /// into this one, asking first.
    /// </summary>
    /// <param name="destFolderDir">The folder the cell was dropped on; null means the workspace root.</param>
    public async Task AcceptCellFromOtherWorkspaceAsync(string sourceCellDir, string? destFolderDir)
    {
        if (CurrentWorkspacePath is null) return;
        var window = ResolveOwner(null);
        if (window is null) return;

        string myRoot = Path.GetDirectoryName(CurrentWorkspacePath)!;
        string source = Path.GetFullPath(sourceCellDir);

        if (!File.Exists(Path.Combine(source, CellFolder.CcellFileName)))
        {
            Messages.Error($"'{Path.GetFileName(source)}' is not a cell folder (no {CellFolder.CcellFileName}).");
            return;
        }

        // R-mw3-4: a cell already in this workspace has nothing to add. The drop handler answers
        // DragDropEffects.None for that case so the gesture never starts; the MENU can still reach it,
        // and it says so rather than making a pointless copy of a cell beside itself.
        if (!WorkspaceRootFinder.IsOutside(source, myRoot))
        {
            Messages.Info($"'{Path.GetFileName(source)}' is already in this workspace. "
                        + "Use Duplicate Cell to make a copy of it here.");
            return;
        }

        string dest = destFolderDir is null ? myRoot : Path.GetFullPath(destFolderDir);
        if (!Directory.Exists(dest) || WorkspaceRootFinder.IsOutside(dest, myRoot)) dest = myRoot;

        string? sourceRoot = WorkspaceRootFinder.WorkspaceDirOf(source);

        // Every reason Reference might be unavailable, in one sentence — the dialog disables the
        // option and shows it, rather than letting the user pick a mode that then fails.
        string? refusal = ReferenceRefusal(myRoot, sourceRoot, source);

        var preview = CrossWorkspaceCellCopy.Plan(source, dest, myRoot, SubCellMode.Copy);

        var dialog = new AddCellToWorkspaceDialog(
            Path.GetFileName(source),
            sourceRoot is null ? "(no workspace)" : FolderLeaf(sourceRoot),
            FolderLeaf(myRoot),
            refusal,
            preview.UnimportedKits,
            hasSubCells: preview.Folders.Count > 1);

        var choice = await dialog.ShowDialog<AddCellChoice?>(window);
        if (choice is null) return;

        if (choice.Reference)
        {
            ReferenceExternalCell(myRoot, sourceRoot!, source);
            return;
        }

        await CopyExternalCellAsync(window, myRoot, sourceRoot, source, dest, choice.SubCells);
    }

    /// <summary>
    /// Why this cell cannot be referenced where it is, or null when it can be.
    ///
    /// <para>Both MW2 gates are asked, and a refusal from either stands: the workspace-level one
    /// because this gesture CREATES the alias <c>File ▸ Reference Workspace…</c> would have refused
    /// to create, and the cell-level one because the cell's own layout may deviate from its
    /// workspace default. Being no looser than the deliberate gesture is the point — a drag must not
    /// be a way around a refusal.</para>
    /// </summary>
    private string? ReferenceRefusal(string myRoot, string? sourceRoot, string sourceCellDir)
    {
        if (sourceRoot is null)
            return $"'{Path.GetFileName(sourceCellDir)}' is not inside a workspace, so there is nothing "
                 + "to reference it through — a ws:// reference names a workspace. It can be copied in.";

        var workspaceCheck = ExternalWorkspaceGate.CheckWorkspaceTechnology(myRoot, sourceRoot, _techCache);
        if (!workspaceCheck.Permitted) return workspaceCheck.Refusal;

        var cellCheck = ExternalWorkspaceGate.CheckCellTechnology(null, myRoot, sourceCellDir, _techCache);
        return cellCheck.Permitted ? null : cellCheck.Refusal;
    }

    // ── Reference ─────────────────────────────────────────────────────────────

    private void ReferenceExternalCell(string myRoot, string sourceRoot, string sourceCellDir)
    {
        // R-mw3-7 / MW2 §2: the alias is created once and REUSED. Two aliases for one workspace would
        // make the same cell reachable under two names, and a rename repair would then have to guess.
        string? alias = ExistingAliasFor(myRoot, sourceRoot);
        bool    added = false;

        if (alias is null)
        {
            alias = UniqueAlias(myRoot, FolderLeaf(sourceRoot));
            if (!AddReferencedWorkspace(myRoot, alias, Path.Combine(sourceRoot, ".cws"), out string? error))
            {
                Messages.Error(error!);
                return;
            }
            added = true;
        }

        string cellRef = ExternalCellRef.RefFor(
            alias, Path.GetRelativePath(sourceRoot, sourceCellDir).Replace('\\', '/'));

        Messages.Success(added
            ? $"'{FolderLeaf(sourceRoot)}' is now referenced as \"{alias}\"; place '{Path.GetFileName(sourceCellDir)}' "
              + $"from the Project Tree ({cellRef})."
            : $"'{Path.GetFileName(sourceCellDir)}' is already reachable through the existing reference "
              + $"\"{alias}\" ({cellRef}).");

        _factory.ProjectTreeTool?.Refresh();
    }

    // ── Copy ──────────────────────────────────────────────────────────────────

    private async Task CopyExternalCellAsync(
        Window window, string myRoot, string? sourceRoot,
        string sourceCellDir, string destParentDir, SubCellMode mode)
    {
        var plan = CrossWorkspaceCellCopy.Plan(sourceCellDir, destParentDir, myRoot, mode);

        // R-mw3-9: collisions are ASKED about, never auto-suffixed. `Amp_2` appearing in someone's
        // project without their say-so is worse than a second dialog.
        var resolved = await ResolveCollisionsAsync(window, plan.Folders);
        if (resolved is null) return;
        plan = plan with { Folders = resolved, DestCellDir = resolved[0].DestDir };

        // The alias has to exist BEFORE the rewrite: ExternalCellRef.MakeCellRef reads the table, and
        // a rewrite run without it would silently emit the raw relative form R-mw2-5 forbids writing.
        if (plan.NeedsSourceAlias && sourceRoot is not null && ExistingAliasFor(myRoot, sourceRoot) is null)
        {
            string alias = UniqueAlias(myRoot, FolderLeaf(sourceRoot));
            if (!AddReferencedWorkspace(myRoot, alias, Path.Combine(sourceRoot, ".cws"), out string? error))
            {
                Messages.Error(error!);
                return;
            }
        }

        string written;
        try { written = CrossWorkspaceCellCopy.Execute(plan); }
        catch (Exception ex) { Messages.Error($"Copy failed: {ex.Message}"); return; }

        CellSymbolResolver.InvalidateAll();
        _factory.ProjectTreeTool?.Refresh();

        int extra = plan.Folders.Count - 1;
        Messages.Success(
            extra == 0 ? "Copied" : $"Copied, with {extra} sub-cell{(extra == 1 ? "" : "s")}", written);
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
