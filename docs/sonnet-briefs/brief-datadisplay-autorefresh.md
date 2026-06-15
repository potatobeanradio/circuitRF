# Sonnet Brief — Data displays auto-refresh with newly-generated .npy after a run

**Goal:** After a simulation runs and writes `.npy` results, any **open Data Display already showing those
results** updates automatically — the user should not click "refresh." Only the **changed** data reloads;
unrelated/old data is left untouched.

## Seam (confirmed)
`WorkspaceViewModel.RunAnalysis` Step 3 Success → `RunResultsWriter.WriteResults(...)` writes
`<baseDir>/results/<schematicKey>/<analysis>.npy` (clearing stale `.npy` first). A Data Display loads `.npy`
files into its `SnpLibraryViewModel` (`DisplayWindowViewModel.SnpLibrary`); each `SnpEntryViewModel.FilePath` is
the source path. `SnpLibraryViewModel.ReloadAsync(entry)` already re-reads a file **in place** (preserving the
`SNP`/`DataSet` instance identity so bound traces survive) and fires `LibraryChanged`, which
`PlotInspectorViewModel.OnLibraryChanged` turns into a trace-path rebuild + `Autoscale` + `PlotNeedsRedraw`. So
the auto-update reduces to: *after writing, reload exactly the library entries whose path matches a written
`.npy`.*

## Changes

### 1. `RunResultsWriter.WriteResults` returns the written paths
Change its return type from `void` to `IReadOnlyList<string>` (absolute `.npy` paths actually written). Collect
each `Path.Combine(dir, Sanitize(r.Name) + ".npy")` in the write loop; return that list. Return an **empty list**
on every early-out (no results, collision skip, exception). Update the existing call site in `RunAnalysis` and
any `RunResultsWriter` tests to accept the return value.

### 2. `SnpLibraryViewModel.ReloadChangedAsync` (new)
```csharp
/// <summary>Reload only the entries whose source file matches one of <paramref name="changedAbsPaths"/>
/// (used after a run regenerates .npy results). Files that don't exist are skipped (no missing-file prompt).
/// Fires LibraryChanged per reloaded entry so open inspectors rebuild + redraw the affected traces only.</summary>
public async Task ReloadChangedAsync(IReadOnlyCollection<string> changedAbsPaths)
{
    if (changedAbsPaths.Count == 0) return;
    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var p in changedAbsPaths) set.Add(Path.GetFullPath(p));

    foreach (var entry in Entries.ToList())   // snapshot: ReloadAsync may mutate state
    {
        if (entry.FilePath is not string fp) continue;
        if (!set.Contains(Path.GetFullPath(fp))) continue;
        if (!File.Exists(fp)) continue;        // never trigger the FindMissingFileAsync prompt during auto-refresh
        await ReloadAsync(entry);              // in-place refresh + LibraryChanged
    }
}
```
(Reuses the existing `ReloadAsync`; do not duplicate its load logic. `ReloadAsync` already routes by extension
and preserves SNP identity.)

### 3. `WorkspaceViewModel.RunAnalysis` — refresh open displays after writing
In the `RunStatus.Success` branch, capture the written paths and refresh:
```csharp
case RunStatus.Success:
    Messages.Success(result.StatusMessage);
    var written = RunResultsWriter.WriteResults(
        baseDir, RunResultsWriter.SchematicKey(activeDoc.FilePath, activeDoc.Id),
        RunResultsWriter.OwnerIdentity(activeDoc.FilePath, activeDoc.Id),
        result.Results, Messages);
    await RefreshOpenDataDisplaysAsync(written);
    break;
```
Add the helper (runs on the UI thread — `RunAnalysis` resumes on the UI thread after its `await Task.Run`, and
this touches VM/ObservableCollection state):
```csharp
private async Task RefreshOpenDataDisplaysAsync(IReadOnlyList<string> changedPaths)
{
    if (changedPaths.Count == 0) return;
    var displays = _openDocsByPath.Values.OfType<DataDisplayDocument>()
        .Concat(_scratchDataDisplays);
    foreach (var dd in displays)
        await dd.ViewModel.Window.SnpLibrary.ReloadChangedAsync(changedPaths);
}
```
`DataDisplayDocument.ViewModel` is `DataDisplayDocumentViewModel`; its `.Window` is the
`DisplayWindowViewModel` exposing `SnpLibrary` (same accessors already used in `OpenOrActivateDataDisplayCoreAsync`).

## Scope / non-goals (state clearly on completion)
- This refreshes displays that **already reference** the regenerated `.npy` paths (the reported bug: re-running
  doesn't update an open display without a manual refresh). It does **not** auto-add a brand-new results file to
  a display that never loaded it — that's a separate "auto-add" feature, out of scope.
- Only changed paths reload; entries for other files are untouched (requirement: "don't refresh old data").
- No `.cdd`/`.npy` format-version bump (the `.npy` format is self-describing; `WriteResults` already overwrites
  in place).

## Tests
- **`SnpLibraryViewModel_ReloadChanged_OnlyMatching`** (DataDisplay VM tests, headless): load two `.npy` entries
  A and B; rewrite A's file on disk with different data; call `ReloadChangedAsync([A.path])`; assert A's `Data`
  reflects the new content and `LibraryChanged` fired, while B is untouched and not reloaded. A path not present
  in the library is a no-op.
- **`RunResultsWriter_ReturnsWrittenPaths`**: write two analyses → returned list contains both
  `results/<key>/<name>.npy` absolute paths; a collision skip returns empty.

## Gate
Build 0W/0E; tests green. Manually: open a Data Display with a trace bound to a results `.npy`; re-run the
analysis; the plotted trace updates to the new data with no manual refresh. A second open display bound to a
*different* results file does not change. Re-running an analysis whose `.npy` no display references is a silent
no-op (no error).

## On completion
Note in `src/Ui/CLAUDE.md`: after a run, `RunAnalysis` reloads only the changed `.npy` paths in every open Data
Display via `SnpLibraryViewModel.ReloadChangedAsync` (in-place reload preserves trace bindings; `LibraryChanged`
drives the inspector rebuild/redraw); brand-new files are not auto-added to displays.
