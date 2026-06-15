# Sonnet Brief — Project Tree: move-to-Trash helper + "Remove" for files / Data Displays / results

Adds a cross-platform "move to system Trash/Recycle Bin" helper and wires **Remove** context-menu items for the
non-cell removable tree nodes. (Remove **Cell** is a separate brief — it depends on the helper built here.)

Owner preference (stated): removals move items to the **system Trash/Recycle Bin** (recoverable later), not a
hard delete. Dialogs confirm and state there is **no in-app undo** (the OS Trash is the recovery path).

## Part 1 — `SystemTrash` helper (new file, UI layer)
`src/Ui/Schematic/SystemTrash.cs` — framework-free except `System.Diagnostics`/`RuntimeInformation`:
```csharp
public static class SystemTrash
{
    /// <summary>Moves a file or directory to the OS Trash/Recycle Bin. Returns true on success.
    /// Never hard-deletes: if the OS trash path fails, returns false (caller reports, does NOT delete).</summary>
    public static bool TryMoveToTrash(string path, out string? error) { … }
}
```
Per-OS implementation (shell out; capture exit code + stderr):
- **macOS:** `osascript -e 'tell application "Finder" to delete POSIX file "<abs>"'` (moves to Trash; works for
  files and folders). Escape embedded quotes in `<abs>`.
- **Windows:** prefer `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile/DeleteDirectory(path,
  UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)` (add `<UseWindowsForms>`-free pkg ref
  `Microsoft.VisualBasic` only if not already resolvable; it ships with the Windows SDK targeting pack). Guard
  the call in a `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)` branch so non-Windows builds don't touch it.
- **Linux:** `gio trash "<abs>"`. If `gio` is absent (exit ≠ 0 / not found), return false with a clear error
  (do NOT fall back to `rm`).

Return false + error on any failure; the dev machine is macOS so the macOS path must be solid. **Flag to owner:**
Linux relies on `gio` (GNOME); other DEs may need `trash-put`/`kioclient` — note as a follow-up, not blocking.

## Part 2 — ITreeActions + WorkspaceViewModel
Add to `ITreeActions` (`src/Ui/ViewModels/ProjectTree/ITreeActions.cs`):
```csharp
/// <summary>Remove a .cdd Data Display file (moves to Trash). Confirms; no usage check.</summary>
void RemoveDataDisplay(ProjectTreeNodeViewModel node);
/// <summary>Remove a removable file/dir (.csch, .csym, results dir/subdir, .npy) — moves to Trash. Confirms.</summary>
void RemoveFile(ProjectTreeNodeViewModel node);
```
Implement both in `WorkspaceViewModel` (async-capable; use `ResolveOwner(null)` for the dialog owner). Shared
steps for each:
1. Build a confirm dialog (reuse `Views.Dialogs.SaveChangesDialog(message, saveLabel:"Remove",
   dontSaveLabel:null, cancelLabel:"Cancel", title:<see below>)`; treat `SaveChangesResult.Save` == confirmed —
   same 2-button pattern already used by `ShowAutoGenPromptAsync`). Pass `title:"Remove Data Display"` for the
   Data Display path and `title:"Remove"` for the file/dir path. (The `title:` parameter is added by
   `brief-savechangesdialog-title.md` — land that brief too; without it the dialog title is wrong/misleading.)
   - Data Display message: `"Remove Data Display '<name>'?\n\nThis moves the file to the Trash/Recycle Bin.
     There is no in-app undo."`
   - File/dir message: `"Remove '<name>'?\n\nThis moves it to the Trash/Recycle Bin. There is no in-app undo."`
2. On confirm: **close any open tab(s) referencing the path** first. For a file: if `_openDocsByPath` contains
   the abs path, close that dockable (`_factory.CloseDockable(dockable)` then it flows through `OnDockableClosed`
   cleanup) and `RetireSessionIfUnreferenced` for `.csch`. For a directory (results dir): close any open doc
   whose path is *under* the directory. (Use the existing `_openDocsByPath` map + close machinery; don't
   hand-roll.)
3. `SystemTrash.TryMoveToTrash(node.AbsolutePath, out var err)`. On success → `Messages.Info($"Removed (moved to
   Trash): {node.AbsolutePath}")` (or `Messages.Success`); on failure → `Messages.Error($"Remove failed: {err}")`
   and stop.
4. `_factory.ProjectTreeTool?.Refresh()`.

`RemoveDataDisplay` and `RemoveFile` share almost everything — factor a private
`RemoveNodeToTrashAsync(node, friendlyKind)` and have both call it (Data Display just passes a tailored label).

## Part 3 — VM commands (`ProjectTreeItemViewModel.cs`)
Add visibility helpers + commands:
```csharp
public bool IsDataDisplayFile => Kind == NodeKind.DataDisplayFile;

/// <summary>Removable via Trash: .csch/.csym view files, results dirs (UserFolder), and .npy/other files.
/// NOTE: results dirs/.npy are scanned as UserFolder/OtherFile (no dedicated NodeKind) — see ProjectTreeNode.</summary>
public bool IsRemovableFile =>
    (Kind == NodeKind.ViewFile && Path.GetExtension(AbsolutePath).ToLowerInvariant() is ".csch" or ".csym")
    || Kind == NodeKind.OtherFile        // includes .npy and misc files under results dirs
    || Kind == NodeKind.UserFolder;      // includes a results directory and its subdirectories

public IRelayCommand RemoveDataDisplayCommand { get; }  // → _actions?.RemoveDataDisplay(this), CanExec IsDataDisplayFile
public IRelayCommand RemoveFileCommand       { get; }  // → _actions?.RemoveFile(this),        CanExec IsRemovableFile
```
**Important caveat to encode:** `ProjectTreeNode` has **no dedicated NodeKind** for a results directory or `.npy`
file — the scanner classifies `.npy` as `OtherFile` and any results folder as `UserFolder`. So `IsRemovableFile`
keys on `OtherFile`/`UserFolder` (plus `.csch`/`.csym` ViewFiles). This intentionally allows removing ordinary
user folders/files too, which is reasonable for "Remove". Do **not** make Remove visible on `Cell`,
`CellViewFolder`, `Workspace`, `Library*`, `KnownFile*`, or `ColorThemeFile` nodes.

## Part 4 — View (`ProjectTreeView.axaml`)
Add to the `<ContextMenu>`:
```xml
<MenuItem Header="Remove Data Display"
          Command="{Binding RemoveDataDisplayCommand}"
          IsVisible="{Binding IsDataDisplayFile}"/>
<MenuItem Header="Remove"
          Command="{Binding RemoveFileCommand}"
          IsVisible="{Binding IsRemovableFile}"/>
```
Place these near the bottom of the menu (above any cell-specific items added later). Icons come in the separate
icons brief.

## Tests
- **`tests/Ui.Tests`** (headless where possible): `SystemTrash.TryMoveToTrash` on a temp file on the current OS
  → returns true and the file no longer exists at the original path (don't assert it's *in* Trash — OS-specific).
  Skip/conditionally-run on unsupported CI OSes.
- VM helper unit tests: `IsRemovableFile`/`IsDataDisplayFile`/`IsOpenableFile` true/false for representative
  node kinds + extensions.

## Gate
Build 0W/0E. Manually on macOS: right-click a `.cdd` → "Remove Data Display" → confirm → file in Trash, tree
refreshes, open tab (if any) closed. Right-click a `.csch`/`.npy`/results folder → "Remove" → same. A failed
trash never deletes the file.

## On completion
Note in `src/Ui/CLAUDE.md`: removals route through `SystemTrash.TryMoveToTrash` (OS Trash/Recycle, recoverable;
never hard-delete on failure); `.npy`/results live under `OtherFile`/`UserFolder` node kinds (no dedicated kind).
