# Sonnet Brief — Project Tree: "Save" context-menu item for dirty items

Add a **Save** context-menu item (with icon) to Project Tree nodes that are currently dirty. Hide it when the
node is clean. Behavior:
- **Cell node:** saves every dirty schematic and symbol in that cell (primary or not). The cell then becomes
  clean and its dirty indicator clears.  Menu displays as "Save Cell".
- **Individual `.csch` / `.csym` / `.cdd` node:** saves only that file.  Menu displays as "Save Schematic", "Save Symbol", "Save Data Display".

**Depends on** `brief-close-quit-save.md` for the `SaveDataDisplayDoc(dd, owner)` helper (the `.cdd` save path
reuses it). Land that brief first, or add that helper here.

## Dirty signal (host-resolved, evaluated at right-click)
Add to `ITreeActions` (`src/Ui/ViewModels/ProjectTree/ITreeActions.cs`):
```csharp
/// <summary>True when this node has unsaved work (drives the "Save" context item's visibility).</summary>
bool IsNodeDirty(ProjectTreeNodeViewModel node);
/// <summary>Save this node: a cell saves all its dirty schematics+symbols; a file saves just itself.</summary>
Task SaveNodeAsync(ProjectTreeNodeViewModel node);
```

Implement in `WorkspaceViewModel`. First refactor the existing dirty aggregation so it's reusable — extract the
boolean out of `RefreshCellDirty`:
```csharp
private bool IsCellDirty(string cellDir) =>
    _registry.AllDirtyPaths.Any(p => IsViewInCell(p, cellDir))
    || _openDocsByPath.Values.OfType<SymbolEditorDocument>().Any(d =>
           d.IsDirty && d.ViewModel.CurrentSymbolPath is { } sp && IsViewInCell(sp, cellDir));

private void RefreshCellDirty(string cellDir)
    => _factory.ProjectTreeTool?.SetCellDirty(cellDir, IsCellDirty(cellDir));   // same behavior, now via helper
```
Then:
```csharp
public bool IsNodeDirty(ProjectTreeNodeViewModel node)
{
    switch (node.Kind)
    {
        case NodeKind.Cell:
            return IsCellDirty(node.AbsolutePath);
        case NodeKind.ViewFile:
            var key = Path.GetFullPath(node.AbsolutePath);
            var ext = Path.GetExtension(key).ToLowerInvariant();
            if (ext == ".csch")
                return _registry.AllDirtyPaths.Any(p =>
                    string.Equals(Path.GetFullPath(p), key, StringComparison.OrdinalIgnoreCase));
            if (ext == ".csym")
                return _openDocsByPath.Values.OfType<SymbolEditorDocument>().Any(d =>
                    d.IsDirty && d.ViewModel.CurrentSymbolPath is { } sp
                    && string.Equals(Path.GetFullPath(sp), key, StringComparison.OrdinalIgnoreCase));
            return false;
        case NodeKind.DataDisplayFile:
            var ddKey = Path.GetFullPath(node.AbsolutePath);
            return _openDocsByPath.Values.OfType<DataDisplayDocument>().Any(d =>
                d.FilePath is { } fp
                && string.Equals(Path.GetFullPath(fp), ddKey, StringComparison.OrdinalIgnoreCase)
                && d.ViewModel.Window.HasUnsavedChanges());
        default:
            return false;
    }
}
```
(A cell's non-open `.csch` can't be dirty — only in-memory sessions are — so "all dirty schematics in the cell"
= open dirty registry sessions under the cell dir. Same for symbols.)

```csharp
public async Task SaveNodeAsync(ProjectTreeNodeViewModel node)
{
    var owner = ResolveOwner(null);
    if (owner is null) return;

    switch (node.Kind)
    {
        case NodeKind.Cell:
            await SaveCellViewsAsync(node.AbsolutePath, owner);
            break;
        case NodeKind.ViewFile:
            var ext = Path.GetExtension(node.AbsolutePath).ToLowerInvariant();
            if (ext == ".csch")      SaveSchematicByPath(node.AbsolutePath);
            else if (ext == ".csym") await SaveSymbolByPathAsync(node.AbsolutePath, owner);
            break;
        case NodeKind.DataDisplayFile:
            await SaveDataDisplayByPathAsync(node.AbsolutePath, owner);
            break;
    }

    // Refresh .cws open-doc snapshot silently (parity with single-doc save).
    if (CurrentWorkspacePath is not null) WriteWorkspaceFile(CurrentWorkspacePath, silent: true);
}

private void SaveSchematicByPath(string absPath)
{
    var key = Path.GetFullPath(absPath);
    if (!_registry.TryGet(key, out var vm) || vm is null || !vm.UndoRedo.IsModified) return;
    try
    {
        SchematicPersistence.SaveToFile(key, vm.EditModel, Path.GetFileNameWithoutExtension(key));
        NotifySessionSaved(key);                 // clears dirty + refreshes cell indicator
        Messages.Success("Saved", key);
    }
    catch (Exception ex) { Messages.Error($"Failed to save '{key}': {ex.Message}"); }
}

private async Task SaveSymbolByPathAsync(string absPath, Window owner)
{
    var key = Path.GetFullPath(absPath);
    var doc = _openDocsByPath.Values.OfType<SymbolEditorDocument>().FirstOrDefault(d =>
        d.ViewModel.CurrentSymbolPath is { } sp
        && string.Equals(Path.GetFullPath(sp), key, StringComparison.OrdinalIgnoreCase));
    if (doc is { IsDirty: true }) await SaveMaterializedSymbolDoc(doc, owner);   // existing helper
}

private async Task SaveDataDisplayByPathAsync(string absPath, Window owner)
{
    var key = Path.GetFullPath(absPath);
    var doc = _openDocsByPath.Values.OfType<DataDisplayDocument>().FirstOrDefault(d =>
        d.FilePath is { } fp && string.Equals(Path.GetFullPath(fp), key, StringComparison.OrdinalIgnoreCase));
    if (doc is not null && doc.ViewModel.Window.HasUnsavedChanges())
        await SaveDataDisplayDoc(doc, owner);    // from brief-close-quit-save.md
}

private async Task SaveCellViewsAsync(string cellDir, Window owner)
{
    foreach (var p in _registry.AllDirtyPaths.Where(p => IsViewInCell(p, cellDir)).ToList())
        SaveSchematicByPath(p);
    foreach (var doc in _openDocsByPath.Values.OfType<SymbolEditorDocument>()
                 .Where(d => d.IsDirty && d.ViewModel.CurrentSymbolPath is { } sp && IsViewInCell(sp, cellDir))
                 .ToList())
        await SaveMaterializedSymbolDoc(doc, owner);
    RefreshCellDirty(cellDir);                   // cell becomes clean → indicator updates
}
```
(`NotifySessionSaved` already calls `UpdateCellDirtyForSession` → `RefreshCellDirty`, so saving a schematic also
updates the owning cell's indicator.)

## VM (`ProjectTreeItemViewModel.cs`)
```csharp
/// <summary>True when this node has unsaved work — drives the "Save" context item.
/// Resolved through the host so it reflects live dirty state when the menu opens.</summary>
public bool IsSaveable => _actions?.IsNodeDirty(this) ?? false;

public IAsyncRelayCommand SaveCommand { get; }   // ctor: new AsyncRelayCommand(() => _actions?.SaveNodeAsync(this) ?? Task.CompletedTask)
```

## View (`ProjectTreeView.axaml`)
Add as the FIRST item in the `<ContextMenu>` (above "Open"), so the save action leads for a dirty item:
```xml
<MenuItem Header="Save"
          Command="{Binding SaveCommand}"
          IsVisible="{Binding IsSaveable}">
    <MenuItem.Icon><mi:MaterialIcon Kind="ContentSave" Width="14" Height="14"/></MenuItem.Icon>
</MenuItem>
```

**Menu-open freshness:** `IsSaveable` is a plain getter resolved through the host, so it reflects current dirty
state each time the context menu's items are realized on open. Verify this in a quick manual test (open the menu
on a clean file → no Save; edit it → reopen the menu → Save appears). If a given Avalonia 12 build caches the
binding across opens, add a `ContextMenu` `Opening` handler in `ProjectTreeView.axaml.cs` that re-reads the
node's dirtiness (its `DataContext` is the `ProjectTreeNodeViewModel`) and toggles the Save item's `IsVisible`
imperatively — keep that as the fallback only if needed.

## Tests (`tests/Ui.Tests`, headless where possible)
- **`IsNodeDirty_*`**: with a registered dirty `.csch` session → `IsNodeDirty` true for that view-file node and
  for its owning cell node; after `NotifySessionSaved`, both false. Dirty open `.csym` editor → true for the
  `.csym` node and its cell; clean → false.
- **`SaveCellViewsAsync_SavesAllDirtyViews`**: a cell with two dirty schematic sessions + one dirty symbol editor
  → after save, all three are written to disk and `IsCellDirty(cellDir) == false`.

## Gate
Build 0W/0E; tests green. Manually: right-click a clean file/cell → no "Save"; dirty a schematic → its file node
and its cell both offer "Save"; "Save" on the cell writes every dirty view in it and clears the cell dot; "Save"
on an individual `.csch`/`.csym`/`.cdd` writes only that file.

## On completion
Note in `src/Ui/CLAUDE.md`: the Project Tree "Save" context item is host-resolved via
`ITreeActions.IsNodeDirty`/`SaveNodeAsync`; a cell saves all its open dirty schematic sessions + symbol editors;
file nodes save only themselves; dirty state reuses the registry/symbol-editor/data-display dirty signals.
