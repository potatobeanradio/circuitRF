using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Referencing a cell in ANOTHER workspace (MW2). The reference form itself lives in
/// <see cref="ExternalCellRef"/>; this is the deliberate way one comes into existence.
///
/// <para><b>Two entry points and no more</b> (R-mw2-6): this command, and MW3's drag-drop gesture,
/// which creates the alias if it does not exist yet and reuses it when it does. The palette and every
/// existing placement path stay workspace-scoped — an external reference is an act, never something a
/// user arrives at by not noticing.</para>
/// </summary>
public partial class WorkspaceViewModel
{
    /// <summary>
    /// File ▸ Reference Workspace… — pick another workspace's <c>.cws</c>, name the alias, and record
    /// it in this workspace's own <c>.cws</c>. Its cells then appear in the Project Tree beside the
    /// referenced libraries and place exactly like any other tree cell.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReferenceWorkspace))]
    private async Task ReferenceWorkspace(Window? owner)
    {
        var window = ResolveOwner(owner);
        if (window is null || CurrentWorkspacePath is null) return;
        string myRoot = Path.GetDirectoryName(CurrentWorkspacePath)!;

        IStorageFolder? startLocation = null;
        try { startLocation = await window.StorageProvider.TryGetFolderFromPathAsync(_lastWorkspaceParentDir); }
        catch { }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                  = "Reference Workspace",
            AllowMultiple          = false,
            SuggestedStartLocation = startLocation,
        });
        if (folders.Count == 0) return;

        string otherRoot = Path.GetFullPath(folders[0].Path.LocalPath);
        string otherCws  = Path.Combine(otherRoot, ".cws");

        if (!File.Exists(otherCws))
        {
            Messages.Error("That folder is not a circuitRF workspace (no .cws found).");
            return;
        }

        if (string.Equals(Path.GetFullPath(myRoot), otherRoot, StringComparison.OrdinalIgnoreCase))
        {
            Messages.Warning("A workspace already contains its own cells; it cannot reference itself.");
            return;
        }

        // §3, said BEFORE anything is written but NOT a refusal. Creating the reference writes one
        // alias and draws nothing; the hazard — a layout's whole instance hierarchy compiled against
        // one layer table — arrives when a cell is PLACED, and the placement gate is asked there with
        // both real documents in hand. Refusing on the two workspaces' DEFAULT technologies instead
        // would block a workspace that holds several of them, and a purely schematic reference, which
        // §3 exempts outright.
        string? techWarning =
            ExternalWorkspaceGate.WorkspaceTechnologyWarning(myRoot, otherRoot, _techCache);

        // Already referenced — including by the per-cell gesture, whose alias is recorded CellsOnly
        // and draws no row of its own. That is exactly the workspace this command is being run on, so
        // it PROMOTES the existing alias rather than refusing or adding a second name for one target:
        // a ws:// reference already written through it goes on resolving, unchanged.
        if (ExistingAliasFor(myRoot, otherRoot) is { } existingAlias)
        {
            if (!ShowReferencedWorkspace(myRoot, existingAlias, out string? promoteError))
            {
                Messages.Error(promoteError!);
                return;
            }
            Messages.Success($"'{FolderLeaf(otherRoot)}' is referenced as \"{existingAlias}\"; "
                           + "its cells now appear in the Project Tree.");
            if (techWarning is not null) Messages.Warning(techWarning);
            RefreshAfterReferenceChange();
            return;
        }

        string suggested = UniqueAlias(myRoot, FolderLeaf(otherRoot));
        var dialog = new Views.Dialogs.InputNameDialog(
            "Reference Workspace", "Name this reference:", suggested);
        var alias = await dialog.ShowDialog<string?>(window);
        if (string.IsNullOrWhiteSpace(alias)) return;
        alias = alias.Trim();

        if (alias.Contains('/') || alias.Contains('\\'))
        {
            // The alias runs to the first separator in ws://alias/… , so one inside it would silently
            // truncate every reference written through it.
            Messages.Error("A workspace reference name cannot contain '/' or '\\'.");
            return;
        }

        if (!AddReferencedWorkspace(myRoot, alias, otherCws, out string? error))
        {
            Messages.Error(error!);
            return;
        }

        Messages.Success($"'{FolderLeaf(otherRoot)}' is now referenced as \"{alias}\". "
                       + "Its cells appear in the Project Tree.");
        if (techWarning is not null) Messages.Warning(techWarning);
        RefreshAfterReferenceChange();
    }

    private bool CanReferenceWorkspace() => CurrentWorkspacePath is not null;

    /// <summary>
    /// Records one alias in <paramref name="workspaceRoot"/>'s <c>.cws</c> — the ONE place a
    /// cross-workspace path is written (R-mw2-4), which is what turns "relocating the other project
    /// breaks every document that referenced it" into a one-line repair.
    ///
    /// <para>Shared with MW3's drag-drop gesture, which reuses an existing alias rather than adding a
    /// second one for the same workspace: two aliases for one target would make the same cell reachable
    /// under two names, and a rename repair would then have to guess which.</para>
    /// </summary>
    /// <param name="cellsOnly">
    /// True when the alias is being created only so that ONE CELL can be addressed through it (MW3's
    /// per-cell reference). Such an entry is recorded exactly as any other — a <c>ws://</c> reference
    /// cannot tell the difference — but the Project Tree does not render it as a workspace, because
    /// referencing one cell must not list the other project's whole catalogue.
    /// </param>
    internal static bool AddReferencedWorkspace(
        string workspaceRoot, string alias, string otherCwsPath, out string? error,
        bool cellsOnly = false)
    {
        error = null;
        string cwsPath = Path.Combine(workspaceRoot, ".cws");

        CwsFile cws;
        try { cws = WorkspacePersistence.LoadFromFile(cwsPath); }
        catch (Exception ex) { error = $"Could not read this workspace's .cws: {ex.Message}"; return false; }

        cws.ReferencedWorkspaces ??= [];

        string stored = Schematic.WorkspaceRefs.ToStoredRef(otherCwsPath, workspaceRoot);
        string otherRoot = Path.GetFullPath(Path.GetDirectoryName(otherCwsPath)!);

        // Already referenced under some name — reuse it rather than adding a second alias.
        foreach (var existing in cws.ReferencedWorkspaces)
        {
            string existingRoot;
            try
            {
                existingRoot = Path.GetFullPath(Path.GetDirectoryName(
                    Schematic.WorkspaceRefs.Resolve(existing.Path, workspaceRoot))!);
            }
            catch { continue; }

            if (string.Equals(existingRoot, otherRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(existing.Alias, alias, StringComparison.OrdinalIgnoreCase))
                    error = $"That workspace is already referenced as \"{existing.Alias}\".";
                return error is null;
            }

            if (string.Equals(existing.Alias, alias, StringComparison.OrdinalIgnoreCase))
            {
                error = $"\"{alias}\" already names a different referenced workspace.";
                return false;
            }
        }

        cws.ReferencedWorkspaces.Add(
            new CwsWorkspaceRef { Alias = alias, Path = stored, CellsOnly = cellsOnly });

        // SL2 R-sl2-6: one of the two write sites that has something better to say than silence. An
        // alias the user has just typed is not convenience state about a session — it is the whole
        // gesture, and it would be gone at the next open — so this one reads the choke point's
        // answer and refuses out loud rather than appearing to succeed.
        try
        {
            if (!WorkspacePersistence.SaveToFileAtomic(cwsPath, cws))
            {
                error = $"'{Path.GetFileName(Path.GetDirectoryName(cwsPath))}' is read-only on this " +
                        "machine, so the reference could not be recorded in its .cws.";
                return false;
            }
        }
        catch (Exception ex) { error = $"Could not write this workspace's .cws: {ex.Message}"; return false; }

        // The alias table is memoised per workspace root and is asked per cell instance per render,
        // so a newly-recorded alias resolves to nothing until the memo is dropped.
        WorkspaceRootFinder.InvalidateCache();
        return true;
    }

    /// <summary>The alias an existing reference to <paramref name="otherWorkspaceRoot"/> already
    /// carries, or null when it is not referenced yet. MW3's gesture asks this before adding.</summary>
    internal static string? ExistingAliasFor(string workspaceRoot, string otherWorkspaceRoot)
    {
        try
        {
            string target = Path.GetFullPath(otherWorkspaceRoot);
            var cws = WorkspacePersistence.LoadFromFile(Path.Combine(workspaceRoot, ".cws"));
            foreach (var entry in cws.ReferencedWorkspaces ?? [])
            {
                if (ExternalCellRef.WorkspaceRootForAlias(workspaceRoot, entry.Alias) is not { } root) continue;
                if (string.Equals(Path.GetFullPath(root), target, StringComparison.OrdinalIgnoreCase))
                    return entry.Alias;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Clears an alias's <see cref="CwsWorkspaceRef.CellsOnly"/> flag, so the referenced workspace
    /// renders as its own sub-tree of cells. Returns false only when the <c>.cws</c> could not be
    /// read or written — an alias that is already visible is success with nothing to do.
    /// </summary>
    internal static bool ShowReferencedWorkspace(string workspaceRoot, string alias, out string? error)
    {
        error = null;
        string cwsPath = Path.Combine(workspaceRoot, ".cws");

        CwsFile cws;
        try { cws = WorkspacePersistence.LoadFromFile(cwsPath); }
        catch (Exception ex) { error = $"Could not read this workspace's .cws: {ex.Message}"; return false; }

        var entry = (cws.ReferencedWorkspaces ?? []).FirstOrDefault(
            r => string.Equals(r.Alias, alias, StringComparison.OrdinalIgnoreCase));
        if (entry is null || !entry.CellsOnly) return true;

        entry.CellsOnly = false;
        return SaveCws(cwsPath, cws, out error);
    }

    /// <summary>
    /// Records ONE cell of a referenced workspace in this workspace's <c>.cws</c> — the row the
    /// Project Tree draws at its root (§ referenced cells). <paramref name="cellRef"/> is the
    /// <c>ws://alias/…</c> form; the alias it names must already be recorded.
    ///
    /// <para>Adding a reference that is already listed is success, not an error: the gesture is
    /// idempotent, and a user who drags the same cell across twice has not done anything wrong.
    /// <paramref name="alreadyListed"/> says which of the two happened so the caller can say so.</para>
    /// </summary>
    internal static bool AddReferencedCell(
        string workspaceRoot, string cellRef, out string? error, out bool alreadyListed)
    {
        error = null;
        alreadyListed = false;
        string cwsPath = Path.Combine(workspaceRoot, ".cws");

        CwsFile cws;
        try { cws = WorkspacePersistence.LoadFromFile(cwsPath); }
        catch (Exception ex) { error = $"Could not read this workspace's .cws: {ex.Message}"; return false; }

        cws.ReferencedCells ??= [];
        if (cws.ReferencedCells.Any(r => string.Equals(r, cellRef, StringComparison.OrdinalIgnoreCase)))
        {
            alreadyListed = true;
            return true;
        }

        cws.ReferencedCells.Add(cellRef);
        return SaveCws(cwsPath, cws, out error);
    }

    /// <summary>
    /// Drops one cell reference, and the alias behind it when that alias was created for this cell
    /// alone and nothing else still needs it. Returns false when the entry was not there.
    /// </summary>
    internal static bool RemoveReferencedCell(
        string workspaceRoot, string cellRef, out string? error, out string? removedAlias)
    {
        error = null;
        removedAlias = null;
        string cwsPath = Path.Combine(workspaceRoot, ".cws");

        CwsFile cws;
        try { cws = WorkspacePersistence.LoadFromFile(cwsPath); }
        catch (Exception ex) { error = $"Could not read this workspace's .cws: {ex.Message}"; return false; }

        int removed = cws.ReferencedCells?.RemoveAll(
            r => string.Equals(r, cellRef, StringComparison.OrdinalIgnoreCase)) ?? 0;
        if (removed == 0) { error = "That cell is not referenced here."; return false; }

        // The alias goes too, whenever it was created for the referenced cells (CellsOnly) and no
        // OTHER referenced cell still addresses it.
        //
        // <b>Instances that place the cell do NOT keep it alive</b> (owner, 2026-09-04: "I removed my
        // reference from the Project tree, but the layout instance still resolves"). Keeping the alias
        // for their sake was a defensible instinct and it was wrong twice over: the removal
        // confirmation has already SAID those references will stop resolving, so keeping them working
        // makes the app do the opposite of what it just promised; and it made the outcome depend on
        // whether a document happened to be saved — an unsaved schematic counted zero, so removing the
        // same reference broke the instances in one document and not in another. Removing a reference
        // now means the same thing every time, the dialog's warning is the whole of the trade, and
        // Re-reference Cell… is the way back.
        if (ExternalCellRef.TryParse(cellRef, out string alias, out _)
            && (cws.ReferencedWorkspaces ?? []).FirstOrDefault(
                   r => string.Equals(r.Alias, alias, StringComparison.OrdinalIgnoreCase)) is { CellsOnly: true }
            && !(cws.ReferencedCells ?? []).Any(
                   r => ExternalCellRef.TryParse(r, out string a, out _)
                        && string.Equals(a, alias, StringComparison.OrdinalIgnoreCase)))
        {
            cws.ReferencedWorkspaces!.RemoveAll(
                r => string.Equals(r.Alias, alias, StringComparison.OrdinalIgnoreCase));
            removedAlias = alias;
        }

        if (!SaveCws(cwsPath, cws, out error)) return false;

        // The alias table a ws:// reference resolves through is memoised, and this rewrite changes it.
        WorkspaceRootFinder.InvalidateCache();
        return true;
    }

    /// <summary>
    /// SL2 R-sl2-6's write, in one place: a read-only workspace refuses OUT LOUD rather than
    /// appearing to succeed and losing the edit at the next open.
    /// </summary>
    private static bool SaveCws(string cwsPath, CwsFile cws, out string? error)
    {
        error = null;
        try
        {
            if (!WorkspacePersistence.SaveToFileAtomic(cwsPath, cws))
            {
                error = $"'{Path.GetFileName(Path.GetDirectoryName(cwsPath))}' is read-only on this " +
                        "machine, so the reference could not be recorded in its .cws.";
                return false;
            }
        }
        catch (Exception ex) { error = $"Could not write this workspace's .cws: {ex.Message}"; return false; }
        return true;
    }

    private static string UniqueAlias(string workspaceRoot, string suggested)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var cws = WorkspacePersistence.LoadFromFile(Path.Combine(workspaceRoot, ".cws"));
            foreach (var e in cws.ReferencedWorkspaces ?? []) taken.Add(e.Alias);
        }
        catch { }

        if (!taken.Contains(suggested)) return suggested;
        for (int i = 2; ; i++)
            if (!taken.Contains($"{suggested}{i}")) return $"{suggested}{i}";
    }

    private static string FolderLeaf(string dir) =>
        Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}
