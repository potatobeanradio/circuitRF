# Sonnet Brief — Project Tree: "Remove Cell" (Trash + workspace usage count + big warning)

Adds a **Remove Cell** context-menu item to cell nodes. Destructive, no in-app undo; moves the cell folder to the
system Trash/Recycle Bin (recoverable there). Requires a BIG warning dialog that tells the user **how many other
cells in the workspace currently use this cell**.

**Depends on:** `SystemTrash.TryMoveToTrash` from `brief-tree-trash-and-file-remove.md` (land that first).

## Part 1 — Cell-usage scanner (new, headless, testable)
`src/Ui/Schematic/CellUsageScanner.cs`:
```csharp
public static class CellUsageScanner
{
    /// <summary>Counts DISTINCT cells in the workspace (excluding the target itself) that contain at least one
    /// schematic component whose CellRef resolves to <paramref name="targetCellDir"/>. Used by Remove Cell's
    /// warning. Best-effort: unreadable schematics are skipped.</summary>
    public static int CountReferencingCells(string workspaceRootDir, string targetCellDir);
}
```
Algorithm:
- Normalize `targetCellDir` (`Path.GetFullPath`, trim trailing separators).
- Enumerate every cell folder under `workspaceRootDir` (a directory containing `CellFolder.CcellFileName`),
  recursively — mirror `WorkspaceScanner`'s "has `.ccell`" test; reuse `CellFolder` constants. Skip the target.
- For each such cell, for each `.csch` under its schematic sub-folder (`CellFolder.SubFolderPath(cellDir,
  ViewType.Schematic)`, `*.csch`): load via `SchematicPersistence.LoadFromFile` → `editModel` (its
  `SchematicDirectory` is the .csch's folder). For each `comp` with `comp.CellRef is not null`, resolve the
  referenced cell dir = `Path.GetFullPath(Path.Combine(schematicDir, comp.CellRef))` (CellRef points at the cell
  FOLDER, e.g. "../../cell3"). If it equals `targetCellDir` (OrdinalIgnoreCase), this cell references the target —
  count it once and move on to the next cell.
- Return the distinct count of referencing cells. Wrap per-file loads in try/catch (skip unreadable).

This is framework-free (UI-layer `SchematicPersistence`/`CellFolder` only) — no Avalonia. Testable headless.

## Part 2 — ITreeActions + WorkspaceViewModel
Add to `ITreeActions`:
```csharp
/// <summary>Remove a cell folder (moves to Trash). Big warning incl. workspace usage count; no in-app undo.</summary>
Task RemoveCellAsync(ProjectTreeNodeViewModel cellNode);
```
Implement in `WorkspaceViewModel`:
1. Guard: `CurrentWorkspacePath` must be set (need the workspace root). `var workspaceRoot =
   Path.GetDirectoryName(CurrentWorkspacePath)!;`
2. `int usedIn = CellUsageScanner.CountReferencingCells(workspaceRoot, cellNode.AbsolutePath);`
3. Build the BIG warning message:
   - Base: `"Remove cell '<name>'?\n\nThis moves the entire cell folder to the Trash/Recycle Bin. There is no
     in-app undo."`
   - If `usedIn == 1`: append `"\n\n⚠ This cell is used in 1 other cell. Removing it will break that reference."`
   - If `usedIn > 1`: append `$"\n\n⚠ This cell is used in {usedIn} cells. Removing it will break those
     references."`
   - If `usedIn == 0`: no usage line.
4. Show a confirm dialog (reuse `Views.Dialogs.SaveChangesDialog(message, saveLabel:"Remove Cell",
   dontSaveLabel:null, cancelLabel:"Cancel", title:"Remove Cell")`; `SaveChangesResult.Save` == confirmed). The
   message itself carries the BIG warning text; that satisfies the "big warning" requirement without a new dialog
   type. (The `title:` parameter is added by `brief-savechangesdialog-title.md` — land that too.)
5. On confirm: **close any open tabs/sessions under the cell dir** first (schematic tabs, symbol editors, the
   cell-parameter editor) — iterate `_openDocsByPath` for keys whose path is inside `cellNode.AbsolutePath`,
   close each via the existing close machinery (`_factory.CloseDockable`), and for `.csch` paths call
   `RetireSessionIfUnreferenced`. Also clear the Properties inspector if it's showing this cell.
6. `SystemTrash.TryMoveToTrash(cellNode.AbsolutePath, out var err)` → on success `Messages.Info($"Removed cell
   (moved to Trash): {cellNode.AbsolutePath}")`; on failure `Messages.Error($"Remove cell failed: {err}")` and
   stop (folder untouched).
7. `_factory.ProjectTreeTool?.Refresh();`

Don't attempt to rewrite/repair referencing cells — the warning informs the user; broken cell-refs already render
as a "Not Found" placeholder + elaboration skips the instance (existing behavior).

## Part 3 — VM command (`ProjectTreeItemViewModel.cs`)
```csharp
public IAsyncRelayCommand RemoveCellCommand { get; }  // → _actions?.RemoveCellAsync(this) ?? Task.CompletedTask,
                                                      //   CanExec: _actions is not null && IsCell
```

## Part 4 — View (`ProjectTreeView.axaml`)
Per the spec: **bottom of the cell's context menu, with a Separator above it.** Add at the very end of the
`<ContextMenu>`:
```xml
<Separator IsVisible="{Binding IsCell}"/>
<MenuItem Header="Remove Cell"
          Command="{Binding RemoveCellCommand}"
          IsVisible="{Binding IsCell}"/>
```
(Icon added in the icons brief.)

## Tests (`tests/Ui.Tests`, headless)
- **`CellUsageScanner_CountsDistinctReferencingCells`**: build a temp workspace with cellA, cellB, cellC where
  cellB and cellC each have a schematic with a component `CellRef` resolving to cellA → `CountReferencingCells(ws,
  cellA) == 2`. A cell referencing cellA from two instances in one schematic still counts that cell **once**.
- **`CellUsageScanner_Zero_WhenUnused`**: target cell referenced by nobody → 0.
- **`CellUsageScanner_ExcludesSelf`**: a cell that (somehow) references itself is not counted.

## Gate
Build 0W/0E; scanner tests green. Manually on macOS: right-click a cell used by 2 others → "Remove Cell" → dialog
shows "used in 2 cells" → confirm → cell folder in Trash, tree refreshes, open tabs for that cell closed. Cancel
leaves everything intact. Trash failure never deletes the folder.

## On completion
Note in `src/Ui/CLAUDE.md`: Remove Cell uses `CellUsageScanner.CountReferencingCells` for the warning and
`SystemTrash` for the (recoverable) removal; referencing cells are not auto-repaired (broken cell-refs already
degrade gracefully).
