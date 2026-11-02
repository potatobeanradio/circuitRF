# Sonnet Brief — Phase 7.2b: data-source library (`file → DataSet`; .npy loads + plots via the SNP path)

**Design:** `docs/design/data-display.md` §2.2 + §7.2 "Design (RESOLVED)". **Depends on 7.2a** (FromSnp emits a
`Z0` cube; `ToSnp` reads it) — land 7.2a first. **This brief generalizes the source library to load BOTH
Touchstone and `.npy`, keeping the existing SNP trace/picker path 100% intact (regression-safe).** Because an
`.npy` that contains an `S` cube can expose a `ToSnp`-derived `SNP`, S-parameter `.npy` sources become pickable
and plottable through the **existing** machinery — **no trace rebinding, no picker rewrite here.** The
cube-native trace path (HB spectra / measurements, identity components, minimal labels) is **7.2c**. UI
indicator + Messages warning is **7.2e**.

## Current shape (what we generalize)
- `DisplayWindowViewModel` owns one `SnpLibrary` (`SnpLibraryViewModel`) and passes it to each `TabViewModel`
  → `DataDisplayViewModel` → `PlotInspectorViewModel` (`Library`). The picker builds `TraceDataItem`s from
  `Library.Entries`, each using `entry.Snp` (ports, matrix elements, derived params).
- `SnpLibraryViewModel.LoadFileAsync(path|IStorageFile)` reads Touchstone via `TouchstoneIO`, wraps in
  `SnpEntryViewModel { SNP Snp }`, dedups by path, handles broken/reload, computes shortest-unique `DisplayName`.
- `.cdd` traces reference sources by `TraceConfig.SourcePath`; load rebuilds the library from those paths
  (broken-entry fallback for missing files).

## Deliverables

### 1. `SnpEntryViewModel` — carry a unified `DataSet`, keep `SNP` for S-sources
- Add `public DataSet Data { get; private set; }` — the unified payload for every source.
- Keep `public SNP Snp { get; }` for the **existing SNP trace/picker path**. Populate per format:
  - **Touchstone:** `Snp` = the loaded SNP (as today); `Data` = `DataSetBuilder.FromSnp(Snp)`.
  - **`.npy`:** `Data` = `DataSetImporter.Import(path).DataSet`; **if `Data.Contains("S")`** →
    `Snp` = `DataSetBuilder.ToSnp(Data)` (uniform per 7.2a — the Z0 cube drives the reference). If the `.npy`
    has **no** `S` cube (pure HB/measurement) → `Snp` is **null/empty** (a cube-only source; not pickable until
    7.2c — see "Out of scope").
- `IsBroken`, `DisplayName`, and the reveal/copy/remove commands stay. Add a `SourceKind { Touchstone, Npy }`
  (or derive from extension) for routing.
- **Reload:** re-load by format and **refresh in place** so existing trace bindings (which hold `entry.Snp`)
  survive: Touchstone → `Snp.RefreshFrom(newSnp)` (as today) + `Data = FromSnp`; `.npy` → re-`Import` →
  rebuild `Data`, and if it has `S`, `Snp.RefreshFrom(ToSnp(Data))` (refresh the same SNP instance, don't
  replace the reference).

### 2. `SnpLibraryViewModel` — load `.npy` alongside Touchstone
- `LoadFileAsync(path)` / `LoadFileAsync(IStorageFile)`: **route by extension** — Touchstone extensions (the
  existing `_snpExtensions` set) → current path; **`.npy`** → `DataSetImporter.Import` → new `.npy` entry ctor.
  Dedup by full path (unchanged); broken-entry restore + `ReloadAsync` route by extension too.
- Add `.npy` to the accepted-extension check. `UpdateDisplayNames` is path-based — **unchanged** (works across
  mixed formats).
- `LibraryChanged` semantics unchanged (fires after load/reload/remove/restore).

### 3. File picker + `.cdd` source rebuild
- The Open/Import file dialog (`_openFileAction` / code-behind that calls `LoadFileAsync`): **add `.npy`** to
  the picker's file-type filters alongside Touchstone.
- `.cdd` load (`DataDisplayViewModel.LoadPlotContainerConfigAsync` source rebuild from `TraceConfig.SourcePath`):
  route each path by extension through the generalized `LoadFileAsync` (broken-entry fallback unchanged).
  Existing `.cdd` files reference only `.sNp` — alpha allows breaks, no migration.

### 4. Picker enumeration (verify — likely zero change)
The picker builds `TraceDataItem`s from `Library.Entries` via `entry.Snp`. Since `.npy`-with-`S` entries now
expose an `SNP`, they should appear in the picker **automatically**. **Verify** `PlotInspectorViewModel`'s
signal enumeration iterates **all** entries that have a non-empty `Snp` (Touchstone + `.npy`-with-S), and that
entries **without** an `Snp` (cube-only `.npy`) are simply skipped by the SNP picker (not crashed on). Make the
minimal change if the enumeration currently assumes Touchstone-only.

## Out of scope (explicitly — later briefs)
- **Cube-native trace path + picker** for non-S cubes (HB `V/I` spectra, measurements): **7.2c**. A pure-HB
  `.npy` loads as a source in 7.2b but is **not pickable** until 7.2c (no `S` cube → no SNP). This is the
  correct staging boundary, not a bug.
- **Trace identity components** (`source·analysis·cube·slice·transform`) + **minimal-label policy** (§2.7): 7.2c.
- **Non-uniform/complex Z0 indicator + Messages warning:** 7.2e.
- **Class rename** `Snp{Library,Entry}ViewModel` → `DataSource*`: defer to 7.2c (the rename churns many refs;
  doing it now would obscure this regression-safe diff). **Flag the naming debt** in a code comment: the class
  now holds a `DataSet` and loads `.npy`; the SNP is one (S-param) facet.
- Touching `RFNetwork`/the SNP trace compute (already correct).

## Gate (verify in the running app + tests where practical)
1. **Regression:** load a `.sNp` — picker, plotting, markers, Smith/Polar/Rect/Table, and `.cdd` save/reload
   behave **exactly as before** (no diff in Touchstone-sourced behavior).
2. **`.npy` S-param plots via the existing path:** load a real S-param run `.npy`
   (`results/<schematicKey>/<analysisName>.npy`); it appears in the library; pick `S(2,1)` → **dB Rect trace
   renders**; `S(1,1)` on a **Smith** plot; a **Table**; **markers** read correctly. Plot a `.sNp` S21 **and**
   the `.npy` S21 in the **same Rect plot** — **this is the 7.2 S-param gate.**
3. **Mixed library:** both formats coexist; `DisplayName` disambiguates across formats; reload of each refreshes
   in place without breaking existing traces.
4. **Cube-only `.npy`:** a pure-HB `.npy` (no `S` cube) loads as a library entry without error and is absent
   from the SNP picker (deferred to 7.2c) — no crash.
5. `.cdd` round-trip: a display with both a `.sNp`- and a `.npy`-sourced trace saves and reloads faithfully
   (sources rebuilt by extension; broken-entry fallback intact). Build 0W/0E; suite green.

## On completion
Update `src/Ui/CLAUDE.md` (source library now loads `.npy` via `DataSetImporter` + Touchstone via `FromSnp`;
each entry carries a `DataSet`; `.npy`-with-S exposes a `ToSnp` SNP for the existing picker). Tick 7.2b in
`data-display.md` §7.2 status; note that the S-param gate is met and 7.2c adds the cube-native path for non-S
cubes + identity components + labels + the class rename.
