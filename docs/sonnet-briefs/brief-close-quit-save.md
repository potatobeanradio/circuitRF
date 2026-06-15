# Sonnet Brief — Close/Quit save pipeline: crash on orphaned-only dirty + dirty Data Display slips through

Two close/quit bugs in `src/Ui/ViewModels/WorkspaceViewModel.cs` (`PromptSaveBeforeClose`, `HasAnyDirtyWork`,
`ConfirmCloseDockable`) and `src/Ui/Views/WorkspaceWindow.axaml.cs` (`OnClosing`).

## Bug #2 (CRASH) — `firstId` indexes an empty list
**Stack:** `ArgumentOutOfRangeException` at `List`1.get_Item` ← `PromptSaveBeforeClose` ← `WorkspaceWindow.OnClosing`.

**Root cause (confirmed).** In `PromptSaveBeforeClose`, after computing `total`, the message builds `firstId` with an
unguarded final branch:
```csharp
string firstId = dirtyScratch.Count > 0       ? dirtyScratch[0].Id
               : dirtyScratchSymbols.Count > 0 ? dirtyScratchSymbols[0].Id
               : dirtyMat.Count > 0            ? dirtyMat[0].Id
               :                                 dirtyMatSymbols[0].Id;   // ← no guard
```
When the **only** dirty work is an orphaned dirty session (`dirtyOrphanedSessions.Count > 0` while all four
document lists are empty — e.g. push into a sub-cell, edit, pop back out; or close a dirty tab with "Don't Save"),
`total > 0` but every document list is empty, so `dirtyMatSymbols[0]` throws. Because `OnClosing` is `async void`,
the throw surfaces via `Task.ThrowAsync` as an unhandled exception → app crash (matches the trace).

**Fix.** Make `firstId` guard every branch and cover the orphaned-session case; only show the named-doc message
when a name is actually available:
```csharp
string? firstId =
      dirtyScratch.Count          > 0 ? dirtyScratch[0].Id
    : dirtyScratchSymbols.Count   > 0 ? dirtyScratchSymbols[0].Id
    : dirtyMat.Count              > 0 ? dirtyMat[0].Id
    : dirtyMatSymbols.Count       > 0 ? dirtyMatSymbols[0].Id
    : dirtyMatDisplays.Count      > 0 ? dirtyMatDisplays[0].Id           // added in Bug #1 below
    : dirtyScratchDisplays.Count  > 0 ? dirtyScratchDisplays[0].Id       // added in Bug #1 below
    : dirtyOrphanedSessions.Count > 0 ? Path.GetFileNameWithoutExtension(dirtyOrphanedSessions[0])
    : null;

string msg = (total == 1 && firstId is not null)
    ? $"Save '{firstId}' before {context}?"
    : $"You have {total} unsaved document(s). Save before {context}?";
```

**Also harden `OnClosing`** (defense-in-depth) so a future throw in the prompt can't kill the app mid-quit. Wrap
the await in try/catch; on error keep the window open and surface the message:
```csharp
protected override async void OnClosing(WindowClosingEventArgs e)
{
    base.OnClosing(e);
    if (_closingConfirmed) return;
    if (_vm is null || !_vm.HasAnyDirtyWork()) return;
    e.Cancel = true;
    try
    {
        if (await _vm.PromptSaveBeforeClose(this, "closing"))
        {
            _vm.OnCleanExit();
            _closingConfirmed = true;
            Close();
        }
    }
    catch (Exception ex)
    {
        _vm.Messages.Error($"Couldn't complete close/save: {ex.Message}"); // window stays open (no _closingConfirmed)
    }
}
```

## Bug #1 (DATA LOSS) — dirty Data Display slips through on quit
**Root cause (confirmed).** `DataDisplayDocumentViewModel.IsDirty` is an `[ObservableProperty]` that is **never
set** — nothing propagates `DisplayWindowViewModel.HasUnsavedChanges()` into it. So the app never treats a `.cdd`
as dirty: it's absent from `HasAnyDirtyWork()`, from `PromptSaveBeforeClose`, and from `ConfirmCloseDockable`.
Quitting with an edited data display silently discards the edits. (Schematics/symbols/orphaned sessions are
covered; data displays are the gap.)

Use the live `DisplayWindowViewModel.HasUnsavedChanges()` as the dirty signal (it compares a baseline captured at
construction/save, so a brand-new untouched display reports clean — exactly what we want). Do **not** rely on the
never-set `IsDirty` field.

**Fix A — `HasAnyDirtyWork()`:** add data displays.
```csharp
|| _scratchDataDisplays.Any(d => d.ViewModel.Window.HasUnsavedChanges())
|| _openDocsByPath.Values.OfType<DataDisplayDocument>().Any(d => d.ViewModel.Window.HasUnsavedChanges())
```

**Fix B — `PromptSaveBeforeClose`:** collect, count, and save dirty data displays.
```csharp
var dirtyScratchDisplays = _scratchDataDisplays
    .Where(d => d.ViewModel.Window.HasUnsavedChanges()).ToList();
var dirtyMatDisplays = _openDocsByPath.Values.OfType<DataDisplayDocument>()
    .Where(d => d.ViewModel.Window.HasUnsavedChanges()).ToList();
```
Add `dirtyScratchDisplays.Count + dirtyMatDisplays.Count` to `total`, include them in the `firstId` chain (see
Bug #2), and in the **Save** branch save each (materialized + scratch):
```csharp
foreach (var dd in dirtyMatDisplays)     await SaveDataDisplayDoc(dd, owner);
foreach (var dd in dirtyScratchDisplays) await SaveDataDisplayDoc(dd, owner);
```

**Fix C — new helper `SaveDataDisplayDoc`** (saves materialized in place; scratch via a `.cdd` picker, then
tracks + materializes — mirrors the schematic/symbol save-as pattern):
```csharp
private async Task<bool> SaveDataDisplayDoc(DataDisplayDocument dd, Window owner)
{
    var window = dd.ViewModel.Window;
    if (dd.FilePath is { } path)
    {
        await window.SaveAllAsync(path);            // writes + CaptureBaseline ⇒ HasUnsavedChanges() == false
        Messages.Success("Saved", path);
        return true;
    }
    var result = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
    {
        Title = "Save Data Display", SuggestedFileName = dd.Id, DefaultExtension = "cdd",
        FileTypeChoices = [ new FilePickerFileType("circuitRF Data Display") { Patterns = ["*.cdd"] } ],
    });
    if (result is null) return false;               // cancelled
    var picked = Path.GetFullPath(result.Path.LocalPath);
    await window.SaveAllAsync(picked);
    _scratchDataDisplays.Remove(dd);
    dd.Materialize(picked);
    _openDocsByPath[picked] = dd;
    Messages.Success("Saved", picked);
    return true;
}
```
(`DisplayWindowViewModel.SaveAllAsync(path, …)` already exists; window-geometry params default to 0 — fine here.
`DataDisplayDocument.Materialize(path)` sets `FilePath` and clears its `ViewModel.IsDirty`.)

**Fix D — `ConfirmCloseDockable`:** add a per-tab branch so closing a single dirty `.cdd` tab also prompts.
```csharp
if (dockable is DataDisplayDocument ddDoc && ddDoc.ViewModel.Window.HasUnsavedChanges())
{
    var dlg = new Views.Dialogs.SaveChangesDialog(
        $"Save '{ddDoc.Id}' before closing?", title: "Unsaved Changes");
    await dlg.ShowDialog(window);
    return dlg.Result switch
    {
        SaveChangesResult.Cancel   => false,
        SaveChangesResult.DontSave => true,
        SaveChangesResult.Save     => await SaveDataDisplayDoc(ddDoc, window),
        _                          => false,
    };
}
```

**Out of scope / owner note:** wiring `DataDisplayDocumentViewModel.IsDirty` (and the tab-title bullet) to live
edits would need a dirty-changed signal from `DisplayWindowViewModel` (its `HasUnsavedChanges()` is a polled
comparison with no change event). Not required to fix the data loss — the close/quit pipeline now consults
`HasUnsavedChanges()` directly. Flag if you want the live tab bullet too (separate change).

## Tests (`tests/Ui.Tests`)
1. **`PromptSaveBeforeClose_OrphanedOnly_NoCrash`** (Bug #2): set up a registry with one dirty, unreferenced
   session and no dirty docs; call `PromptSaveBeforeClose` (inject a stub dialog result of `DontSave` so it
   returns without UI) → it returns without throwing. Without the fix this throws `ArgumentOutOfRangeException`.
2. **`HasAnyDirtyWork_IncludesDataDisplays`** (Bug #1): an open `DataDisplayDocument` whose `Window` reports
   `HasUnsavedChanges() == true` → `HasAnyDirtyWork()` returns true; clean display → false.
3. If practical, **`SaveDataDisplayDoc_Materialized_Writes`**: a materialized dirty display → after
   `SaveDataDisplayDoc`, the `.cdd` exists and `HasUnsavedChanges() == false`.

## Gate
Build 0W/0E; tests green. Manually on macOS: (a) push into a sub-cell, edit, pop out, quit via File menu → prompt
appears (no crash) and offers to save the orphaned cell. (b) Open/edit a data display, quit → prompt now includes
it and saving writes the `.cdd`; "Don't Save" discards intentionally; "Cancel" keeps the app open.

## On completion
Note in `src/Ui/CLAUDE.md`: the close/quit dirty pipeline now covers Data Displays via
`DisplayWindowViewModel.HasUnsavedChanges()` (the `DataDisplayDocumentViewModel.IsDirty` field is not wired to
live edits); `PromptSaveBeforeClose`'s message no longer indexes an empty list when only orphaned sessions are
dirty; `OnClosing` guards the prompt against exceptions.
