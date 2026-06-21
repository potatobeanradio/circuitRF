# Brief 7.4g — Loadpull data-source entry point (make LP data selectable in the Data Display)

**Phase:** 7.4g (Data Display loadpull contours — the UI entry point; the wiring-it-together sub-gate).
**Design:** `docs/design/loadpull-contours.md` §3 (7.4g), §1.2 (ingest-first / measured≡simulated),
`results-dataset-layout.md` (grouped `run.npy`), `data-display.md` 7.2 (data-source library).
**Goal:** give the 7.4 contour stack a UI entry point. Today a user has **no way to get loadpull data into the
Data Display as a selectable source**, so the contour card (7.4e) has nothing to bind. Close that — for both
producers: **measured** `.spl`/`.lpcwave` test files, and **simulated** loadpull analysis results.

**The core finding (verified on disk):** the data-source library already supports loadpull *as a loader* —
`DataSourceLibraryViewModel` has `SourceKind.Spl`/`SourceKind.Lpcwave`, routes `.spl`/`.lpcwave` through the
7.4f readers (`SplReader.ReadSpl` / `LpcwaveReader.ReadLpcwave`) into a loadpull `DataSet` in
`LoadFileAsync`/`LoadSplAsync`/`LoadLpcwaveAsync`, and handles dedup/reload/restore. **The only gap is
discovery/listing:** `RefreshAvailableDataSources` enumerates just (a) sim-run `run.npy` dirs and (b) workspace
**known Touchstone** files (via `KnownTouchstoneProvider`). Loadpull files are loadable but never *offered* in
the `AvailableDataSources` combo, so the user can't pick one. **This brief is mostly listing/wiring, not new
machinery.**

**Consumes (verified on disk):**
- `DataSourceLibraryViewModel`: `AvailableDataSources` (`ObservableCollection<DataSourceItem>`),
  `RefreshAvailableDataSources()`, `ResultsRootProvider` (Func→results dir), `KnownTouchstoneProvider`
  (Func→IReadOnlyList<string> of known Touchstone paths), `SourceKind` (has `Npy`/`Touchstone`/`Spl`/
  `Lpcwave`), the extension helpers (`IsSplExtension`/`IsLpcwaveExtension` already recognize the formats),
  `SelectDataSourceAsync`, `LoadFileAsync`.
- `DataSourceItem(DisplayName, LogicalId, AbsolutePath, SourceKind)` — the combo item record.
- `DisplayWindowViewModel`: owns `DataSourceLibrary`; `RefreshAvailableDataSources()` pass-through;
  `GetResultsRootAction` (injected by `WorkspaceViewModel` → `<workspaceRoot>/results`);
  `SetLoadRunResultsAction` (folder picker scoped to results), `OpenFile`/import path.
- `WorkspaceViewModel` (`src/Ui/ViewModels/WorkspaceViewModel.cs`): the place that injects
  `GetResultsRootAction` and wires the known-file providers into the library — **the seam to generalize**
  (read it in context; it's where `KnownTouchstoneProvider` is assigned).
- `LoadpullEngine.BuildLoadpullDataSet` / `LoadpullResult` (engine side) + the run.npy results writer
  (`src/Ui/Schematic/RunResultsWriter.cs`) — for the simulated path (§2).

**Firewall:** UI/workspace wiring only; readers + DataSet are already RfCore. No new on-disk format.

---

## 1. Measured path — list `.spl`/`.lpcwave` as known files (the bulk of this brief)

The mechanism the owner hypothesized is exactly right and already half-built for Touchstone: a workspace
**"known file"** that appears in the source picker. Generalize it from Touchstone-only to **any supported data
file**, including loadpull.

### 1a. Generalize the known-file provider
- In `WorkspaceViewModel`, wherever `KnownTouchstoneProvider` is set, broaden the tracked set to include
  `.spl`/`.lpcwave` (and keep Touchstone). Either:
  - rename/extend the provider to a **known-data-file** provider returning all known external data files, OR
  - add a sibling `KnownLoadpullProvider` (Func→paths) on `DataSourceLibraryViewModel` and assign it alongside.
  Prefer the **single generalized provider** if the existing known-file tracking is just a path list — fewer
  moving parts. If Touchstone known-files come from a dedicated typed store, a sibling provider is the smaller
  change. Read the existing wiring and pick the lower-churn option; note the choice in a comment.
- The workspace's notion of "known files" (however it's persisted/scanned) must accept `.spl`/`.lpcwave`. If
  known files are tracked by extension allow-list, add the two extensions; if by a picker filter, add them
  there too (§1c).

### 1b. List them in `RefreshAvailableDataSources`
In `DataSourceLibraryViewModel.RefreshAvailableDataSources`, after the Touchstone block, add a loadpull block
(or fold into one loop over the generalized provider):
```csharp
// Workspace known loadpull files (.spl/.lpcwave), sorted by name.
var loadpull = KnownLoadpullProvider?.Invoke() ?? Array.Empty<string>();   // or the generalized provider
foreach (var p in loadpull.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
{
    var kind = IsSplExtension(Path.GetExtension(p)) ? SourceKind.Spl : SourceKind.Lpcwave;
    AvailableDataSources.Add(new DataSourceItem(Path.GetFileName(p), p, p, kind));
}
```
`LogicalId` = absolute path (rooted) so `ResolveAbs` returns it as-is and `SelectDataSourceAsync` →
`LoadFileAsync` routes it through the existing `.spl`/`.lpcwave` loaders. No new load code.

### 1c. "Add known file" affordance accepts loadpull
The existing import / "add known file" UX (the button wired to `ImportCommand` / the workspace's add-known-file
command) must offer `.spl`/`.lpcwave` in its file-picker filter. The library already recognizes the extensions;
this is just adding filter entries and ensuring the chosen path lands in the known-file store that the provider
(§1a) reads.

**Gate (measured):** register a test `.spl` (e.g. `testdata/spl_test_data/Ideal_GaN_FET_1p6_mm_1p8_GHz.spl`)
as a workspace known file → it appears in the Data Display source picker → selecting it loads the loadpull
DataSet (`SelectedEntry` non-null, no exception) → a 7.4e contour trace binds and draws. A `.lpcwave` known
file does the same.

---

## 2. Simulated path — LP analysis result becomes a selectable source

The cleanest design: an LP analysis writes its result **the same way every other analysis does** — as a grouped
`run.npy` under `<workspaceRoot>/results/<schematic>/run.npy` — so `RefreshAvailableDataSources`'s existing
run-scan lists it **for free** (no new listing code; §1's mechanism is only for measured files).

- **Confirm first (instrument before building):** does the loadpull analysis already serialize through the
  run.npy results path (`RunResultsWriter`/the results-dataset layout)? Read `RunResultsWriter.cs` and the LP
  analysis run flow. If an LP run already produces a `run.npy` containing the loadpull FOM cubes
  (`{gridPoint,pinStep}` + `GammaLoad`), then **this path is already done** — the source already appears in the
  picker, and the only work is verifying a contour binds to it. State that finding explicitly.
- **If it does NOT yet round-trip through run.npy:** wire `LoadpullEngine`'s result (`BuildLoadpullDataSet` →
  the loadpull `DataSet`) into the same results-writer path used by DC/S-param/HB so it lands as
  `results/<schematic>/run.npy`. This is the bulk of the simulated-path work if needed. Keep the DataSet shape
  identical to what the 7.4f readers produce (decision §1.2: measured ≡ simulated downstream) — same FOM cube
  names, same axes — so the contour card can't tell them apart.

**Gate (simulated):** run a loadpull analysis on a schematic → its result appears as a selectable source in the
Data Display (same combo, via the run.npy scan) → a 7.4e contour binds and draws. No origin-specific
affordance distinguishes it from the measured `.spl` case.

---

## 3. Slice plan (compile-and-test-gated)
- **7.4g-1 — measured listing.** Generalize the known-file provider (§1a) + the `RefreshAvailableDataSources`
  loadpull block (§1b) + picker filter (§1c). Gate: a known `.spl`/`.lpcwave` appears in `AvailableDataSources`
  and selects/loads (assert `SelectedEntry` is the loadpull entry).
- **7.4g-2 — simulated path.** Confirm/wire LP result → run.npy (§2). Gate: an LP run appears as a source.
  (If already wired, this slice is a verification test only — say so.)
- **7.4g-3 — end-to-end with 7.4e.** With a selected loadpull source (measured AND simulated), author a contour
  trace and confirm it draws. Gate: the design's headline — both producer paths reach a drawn contour, with no
  origin-specific UI.

## 4. Constraints / gotchas
- **Don't reinvent loading.** The library already loads `.spl`/`.lpcwave`; this brief only makes them
  *discoverable*. Resist adding a parallel load path.
- **LogicalId convention:** measured loadpull known files use the **absolute path** as LogicalId (like
  Touchstone known files), so `ResolveAbs`/persistence behave identically. Sim runs keep the
  `<schematic>/run.npy` relative LogicalId (unchanged).
- **Persistence (`.cdd`):** a contour trace bound to a loadpull source persists its `SelectedDataSource` like
  any other; confirm a saved display with a loadpull source + contour trace round-trips (depends on 7.4e
  serialization landing — coordinate).
- **measured ≡ simulated (decision §1.2):** no origin badge, no separate picker section heading that leaks
  "this is measured vs simulated." They're just data sources.
- **Instrument-first:** for §2, READ `RunResultsWriter` + the LP run flow and report what's actually there
  before writing new serialization — the simulated path may already work end-to-end.
- **Firewall / TreatWarningsAsErrors-clean.** UI + workspace wiring only.

## 5. Tests
- `DataSourceLibraryViewModel`: with a stub `KnownLoadpullProvider`/generalized provider returning a test
  `.spl` path, `RefreshAvailableDataSources` includes it with `SourceKind.Spl`; `SelectDataSourceAsync` on its
  LogicalId loads the loadpull entry. Same for `.lpcwave`.
- Simulated: a test that an LP-result run.npy (real or synthesized to the loadpull DataSet shape) is listed by
  the run scan and loads.
- End-to-end (7.4g-3) is owner-verified visually (contour draws from each source).

## 6. Out of scope
- The contour card itself (7.4e) — this brief only feeds it data.
- Auto-watching the workspace for new files (no FileSystemWatcher — manual/on-focus refresh, per the existing
  workspace convention).
- Any new on-disk format (surfaces stay derived; run.npy + external `.spl`/`.lpcwave` are the only carriers).
- AppSettings/defaults UI (7.4e deferral).
