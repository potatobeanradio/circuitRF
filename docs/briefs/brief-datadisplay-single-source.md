# Sonnet Brief — Single-datasource Data Display (results combobox, remove library panel)

> **Open questions for the owner are at the bottom.** A few items below are marked **[ASSUMPTION]** —
> implement as written unless the owner says otherwise.

## Goal
Replace the multi-file Data Source Library panel with a **single selected datasource per Data Display
document**, chosen from a **combobox in the toolbar**. The combo lists, by name, every `<schematic>` under
the workspace `results/` dir that has a `run.npy`, plus every workspace Touchstone known file
(`.s1p….s24p`, `.snp`). Selecting one makes its file the document's datasource. Traces resolve their data
through a small indirection so a Data Display can also pull specific cross-schematic runs.

This is a real behavior change, taken under the **alpha no-back-compat** rule: bump the `.cdd`
`format_version` and reject older files (no migration).

---

## Core model: logical source references

Today `Trace.SourcePath` is an **absolute** path and the per-document `DataSourceLibrary` is keyed by
absolute `FilePath`. We keep that runtime machinery, but add a **logical reference** that is what gets
persisted and what the combobox drives.

Add **`Trace.SourceRef`** (string?, the *logical* reference; persisted) alongside the existing
**`Trace.SourcePath`** (absolute; runtime, unchanged meaning for all current consumers/matchers):

- `SourceRef == "run.npy"` (or null) → the **selected datasource** (the sentinel).
- `SourceRef == "<schematic>/run.npy"` → that **specific** results run, always
  (`<resultsRoot>/<schematic>/run.npy`), regardless of selection. (Cross-schematic; manual-edit only for now.)
- `SourceRef` rooted/absolute → used as-is (forward-compat / specific Touchstone).

**Resolver** (single source of truth — put on `DataSourceLibraryViewModel`):
```csharp
// Absolute path for a logical ref. Null when it's the sentinel and nothing is selected.
public string? ResolveAbs(string? sourceRef)
{
    if (string.IsNullOrEmpty(sourceRef) || sourceRef == DataSourceRef.Selected)   // "run.npy"
        return SelectedDataSourceAbs;                                             // set on selection
    if (Path.IsPathRooted(sourceRef)) return sourceRef;
    var root = ResultsRootProvider?.Invoke();
    return root is null ? null : Path.GetFullPath(Path.Combine(root, sourceRef)); // "<schematic>/run.npy"
}
```
Add `const string DataSourceRef.Selected = "run.npy";` somewhere shared (e.g. a tiny static class in
`CircuitRF.Ui.DataDisplay`).

**Runtime invariant:** whenever `SourceRef` or the selection changes, recompute
`SourcePath = ResolveAbs(SourceRef)` and lazily ensure that file is loaded into the library, then
re-resolve the trace. All existing absolute-path matchers (`TrySetCubeData`, `RebuildSignals`,
`OnLibraryChanged`, `RefreshSourceZ0`, `ReseedSliceIfCubeShapeChanged`) keep comparing against the
absolute `SourcePath` and need **no change**.

---

## 1. `DataSourceLibraryViewModel` — selected source, available list, lazy load

Repurpose this class as the document's **internal datasource cache + selection** (it's no longer a
user-facing panel). Add:

```csharp
// Workspace seams (set by DisplayWindowViewModel / WorkspaceViewModel).
public Func<string?>? ResultsRootProvider { get; set; }      // <workspaceRoot>/results or null
public Func<IReadOnlyList<string>>? KnownTouchstoneProvider { get; set; }  // abs paths of workspace .sNp known files

// The selected datasource.
public string? SelectedDataSourceRef { get; private set; }   // logical id persisted in .cdd
public string? SelectedDataSourceAbs { get; private set; }   // resolved abs (drives the sentinel)
public DataSourceEntryViewModel? SelectedEntry { get; private set; }  // loaded entry, or null until lazy-loaded

// The combobox feed (display name + logical id + abs + kind). Rebuilt on focus/run/new.
public ObservableCollection<DataSourceItem> AvailableDataSources { get; } = new();

public event EventHandler? SelectedDataSourceChanged;   // DataDisplay re-resolves all traces on this
```

`DataSourceItem`: `record(string DisplayName, string LogicalId, string AbsolutePath, SourceKind Kind)`.
- Sim: `DisplayName = "<schematic>"`, `LogicalId = "<schematic>/run.npy"`, `AbsolutePath = <results>/<schematic>/run.npy`, `Kind = Npy`.
- Touchstone: `DisplayName = Path.GetFileName(p)`, `LogicalId = p` (abs), `AbsolutePath = p`, `Kind = Touchstone`.

Methods:
```csharp
// Enumerate WITHOUT loading any file (lazy). Sim: each results subdir containing run.npy.
// Touchstone: KnownTouchstoneProvider(). Stable order: sims by most-recent run.npy first (File.GetLastWriteTime),
// then Touchstone by name. No-op-safe when there's no workspace.
public void RefreshAvailableDataSources();

// Select by logical id: set SelectedDataSourceRef/Abs, lazy-load the entry into Entries (LoadFileAsync),
// set SelectedEntry, fire SelectedDataSourceChanged. Tolerates a missing/not-yet-existing file
// (SelectedEntry stays null → traces render <invalid>).
public Task SelectDataSourceAsync(string? logicalId);

// Most-recent <schematic>/run.npy LogicalId, or null. Used for new-display default.
public string? MostRecentRunRef();
```

**Lazy rule:** `RefreshAvailableDataSources` only stats directories/files — it must not import any
`.npy`/`.snp`. The only loads are: `SelectDataSourceAsync` (loads the selected file) and trace resolution
of a specific cross-schematic `SourceRef` (loads that one file on demand at config-load/select time).

Keep `Entries`, `LoadFileAsync`, `AddBrokenEntry`, `ReloadChangedAsync`, etc. — they remain the cache.
Remove nothing the loaders need; just stop exposing this as a panel.

---

## 2. Persistence (`Models/DataDisplayConfig.cs`)

- `DataDisplayConfig`: add `public string? SelectedDataSource { get; set; }` (document-level logical id).
- Bump `public const int CurrentFormatVersion = 2;`. `LoadAllAsync` already throws on mismatch — old v1
  `.cdd` files are rejected (alpha). Leave the clipboard/paste `FormatVersion` default at 1 (paste path
  checks `Plots.Count`, not version) so copy/paste is unaffected.
- `TraceConfig.SourcePath` now stores the **logical `SourceRef`**, not an absolute/relativized path.

`DisplayWindowViewModel.SaveAllAsync`: write `config.SelectedDataSource = DataSourceLibrary.SelectedDataSourceRef;`.
`LoadAllAsync`: after building tabs, call `await DataSourceLibrary.SelectDataSourceAsync(config.SelectedDataSource);`
**before** resolving traces (so the sentinel resolves). Include `SelectedDataSource` in
`BuildComparisonJson` so changing the datasource marks the document dirty.

---

## 3. `DataDisplayViewModel` — build/load traces via SourceRef

**`BuildTraceConfig`** (replace the configDir-relativization block): emit the logical ref.
```csharp
string? sourceRef = t.SourceRef
    ?? DeriveRef(t.SourcePath, library);   // fallback for traces created before SourceRef was set
// DeriveRef: == SelectedDataSourceAbs → "run.npy"; under resultsRoot → "<schematic>/run.npy"; else abs.
tc.SourcePath = sourceRef;
```
(`configDir` no longer used for source paths — drop that relativization for traces.)

**`LoadPlotContainerConfigAsync`** (per trace): treat `traceConfig.SourcePath` as the logical `SourceRef`.
```csharp
string? sref = traceConfig.SourcePath;                 // logical
string? abs  = Library?.ResolveAbs(sref);              // sentinel → selected; "<x>/run.npy" → results
if (abs is not null && File.Exists(abs))
    await Library!.LoadFileAsync(abs);                 // lazy: only the referenced file(s)
// ... build trace as today, but:
trace.SourceRef  = sref;
trace.SourcePath = abs;                                // may be null → renders <invalid>
```
Cube-bound / network-bound branching stays the same; just bind `SourcePath = abs` and keep `SourceRef`.
A null/missing `abs` must not throw — the trace loads with no points and shows `<invalid>` (cube) or a
broken network entry. (Confirm `TrySetCubeData` already clears points when the entry is absent — it does.)

**Re-resolve on selection change** — add to `DataDisplayViewModel` a handler subscribed to
`Library.SelectedDataSourceChanged`:
```csharp
private async void OnSelectedDataSourceChanged(object? s, EventArgs e)
{
    foreach (var c in _plots)
      foreach (var t in c.PlotVM.Plot.Traces)
        if (string.IsNullOrEmpty(t.SourceRef) || t.SourceRef == DataSourceRef.Selected)
            t.SourcePath = Library!.ResolveAbs(t.SourceRef);   // re-point sentinel traces
    // Then rebuild every trace's data + redraw (mirror RebuildAndNotify across containers),
    // and refresh open inspectors' signal lists.
    RaiseContentChanged();
}
```
Cross-schematic traces (`SourceRef` = `"<x>/run.npy"`) are intentionally untouched.

---

## 4. Trace picker uses the selected datasource only

`TraceRowViewModel.RebuildSignals` currently enumerates **all** `_parent.LibraryEntries`. Change it to
enumerate **only** `_parent.Library.SelectedEntry` (network items if it has an SNP; cube items from its
DataSet). When the user picks a signal (`OnSelectedSignalChanged`), set the trace to the sentinel:
```csharp
_trace.SourceRef  = DataSourceRef.Selected;     // "run.npy"
_trace.SourcePath = _parent.Library.SelectedDataSourceAbs;
```
Everything else in the card (axis roles, cube slice, transforms) is unchanged. If `SelectedEntry` is null
the picker is empty and `CanAddTrace` is false — fine. A cross-schematic trace, when its card is opened,
shows the **selected** source's signals (per spec); picking re-points it to the selected source.

`PlotInspectorViewModel.AddTrace` seed paths: when seeding from the library, seed from
`Library.SelectedEntry` (network seed if it has an SNP, else its first plottable cube), and stamp
`SourceRef = DataSourceRef.Selected`.

---

## 5. Toolbar combobox + remove the panel (`Views/DataDisplay/DataDisplayView.axaml`)

- **Combobox**: add immediately to the right of the existing **Load Dataset** button
  (`LoadRunResultsCommand`). Bind `ItemsSource="{Binding ViewModel.Window.DataSourceLibrary.AvailableDataSources}"`,
  `SelectedItem` two-way to a new `DisplayWindowViewModel.SelectedDataSourceItem` (which calls
  `DataSourceLibrary.SelectDataSourceAsync(item.LogicalId)` on change), `DisplayMemberBinding` =
  `DisplayName`. Width ~200, with a tooltip showing the resolved path.
- **Remove the library panel**: change the main content `Grid ColumnDefinitions="180,4,*"` →
  single column `*`; delete the `<v:DataSourceLibraryView .../>` and its `<GridSplitter/>`. The tabs +
  inspector grid becomes the whole content area.
- Delete `Views/DataDisplay/DataSourceLibraryView.axaml` + `.axaml.cs`. Keep
  `DataSourceLibraryViewModel` (now the internal cache/selection — no longer panel-bound).
- **Focus refresh**: in `DataDisplayView.axaml.cs`, on the control's `GotFocus`/`AttachedToVisualTree`
  (or `DataDisplayDocument` activation), call `ViewModel.Window.DataSourceLibrary.RefreshAvailableDataSources()`.

`DisplayWindowViewModel` additions: `SelectedDataSourceItem` (the bound combo selection) +
`RefreshAvailableDataSources()` pass-through; wire `DataSourceLibrary.ResultsRootProvider` /
`KnownTouchstoneProvider` from the workspace seams (next section).

---

## 6. Workspace glue (`WorkspaceViewModel.cs`) — locate & extend

The Data Display already receives `GetResultsRootAction` (→ `<workspaceRoot>/results`),
`SetLoadRunResultsAction`, and `OpenFileAsNewDisplay`. At each `new DataDisplayDocument(...)` /
`DisplayWindowViewModel` wiring site:

1. Set `DataSourceLibrary.ResultsRootProvider` from the existing results-root accessor.
2. Set `DataSourceLibrary.KnownTouchstoneProvider` from the workspace's **known-files** collection,
   filtered to Touchstone extensions (`.s1p….s24p`, `.snp`). **[QUESTION 1 — confirm the source.]**
3. **New display default:** after the seams are set, `RefreshAvailableDataSources()` then
   `await SelectDataSourceAsync(MostRecentRunRef())` (most-recent `run.npy` by `File.GetLastWriteTime`).
   Do this in the New-Data-Display creation path (File menu) so a fresh `.cdd` opens on the latest run.
4. **Run-completed refresh:** the post-run path already reloads changed `.npy` into open displays
   (`Library.ReloadChangedAsync`). Extend it to also call `RefreshAvailableDataSources()` on each open
   Data Display, and if the **selected** datasource's `run.npy` was among the changed paths, reload it +
   re-resolve (fire `SelectedDataSourceChanged`).

**Load Dataset button [ASSUMPTION]:** keep it; repurpose to "pick any `.npy`/`.snp` from disk → add a
transient `DataSourceItem` (LogicalId = its absolute path) and select it." **[QUESTION 3.]**

---

## 7. Lazy-loading contract (must hold)
- Building/refreshing the combo list never imports a `.npy`/`.snp` (stat only).
- Files load only when: (a) a datasource is selected, or (b) a trace's specific cross-schematic
  `SourceRef` is resolved at config-load time. Nothing pre-loads all of `results/`.

---

## Tests
1. **Resolve_Sentinel:** `ResolveAbs("run.npy")` == `SelectedDataSourceAbs`; `ResolveAbs(null)` same.
2. **Resolve_CrossSchematic:** `ResolveAbs("ampB/run.npy")` == `<results>/ampB/run.npy` regardless of selection.
3. **Enumerate_NoLoad:** `RefreshAvailableDataSources` lists results subdirs with `run.npy` + known Touchstone,
   and `Entries` stays empty (nothing imported).
4. **Select_LazyLoads:** `SelectDataSourceAsync("ampA/run.npy")` loads exactly that file; `SelectedEntry` set.
5. **MostRecent:** with two `run.npy` of different `LastWriteTime`, `MostRecentRunRef()` picks the newer.
6. **Persist_RoundTrip:** save → `.cdd` has `SelectedDataSource` + trace `SourcePath=="run.npy"` for
   selected-bound traces and `"<schematic>/run.npy"` for cross-schematic; load (v2) restores both; v1 file rejected.
7. **SwitchBreaksTraces:** select source A (trace plots), switch to source B lacking that cube → trace
   re-renders `<invalid>` (no exception), `SourceRef` still `"run.npy"`.
8. **CrossSchematicStable:** a trace with `SourceRef="ampB/run.npy"` keeps rendering from ampB after the
   selected datasource is switched to ampA.
9. **PickerUsesSelected:** with source A selected, the trace card's signal list comes from A only; picking
   a signal sets `SourceRef="run.npy"`.

## Gate (manual)
New Data Display opens on the most-recent run. Toolbar combo lists schematics + Touchstone files; the
library panel is gone and the canvas uses the full width. Switching the combo re-points sentinel traces
(some go `<invalid>`). Editing a `.cdd` by hand to set a trace `SourcePath` to `"<other>/run.npy>"` renders
that other run's data alongside the selected one. Running a sim refreshes the combo and the selected data.

---

## Open questions for the owner
1. **Known Touchstone files source.** Where should the combo pull Touchstone files from — the workspace's
   tracked **Known Files** (the same collection the Project Tree shows), or a recursive scan of the
   workspace dir for `*.s?p`/`*.snp`? I assumed the former (the workspace known-files list, filtered to
   Touchstone extensions). Which collection/property is it?
2. **Persisted selected-datasource token.** I'm persisting `SelectedDataSource` as the logical id:
   `"<schematic>/run.npy"` for sims, absolute path for a Touchstone file; trace sentinel is literally
   `"run.npy"`. Good, or do you want a different token (e.g. bare `<schematic>`)?
3. **Load Dataset button.** Keep it as "pick any file from disk → becomes the selected datasource", or
   remove it now that the combo exists?
4. **Scope.** Selected datasource is **per-document** (one combo drives all tabs in the `.cdd`), matching
   the per-document library. Confirm that's what you want (vs per-tab).
