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
                       + "Its cells appear in the Project Tree under Referenced Workspaces.");
        if (techWarning is not null) Messages.Warning(techWarning);
        _factory.ProjectTreeTool?.Refresh();
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
    internal static bool AddReferencedWorkspace(
        string workspaceRoot, string alias, string otherCwsPath, out string? error)
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

        cws.ReferencedWorkspaces.Add(new CwsWorkspaceRef { Alias = alias, Path = stored });

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
