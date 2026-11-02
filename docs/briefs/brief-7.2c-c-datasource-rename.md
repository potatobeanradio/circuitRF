# Sonnet Brief — 7.2c-c: `Snp*` → `DataSource*` rename (mechanical, final 7.2c pass)

**Context.** Last of the three 7.2c briefs (cube traces + minimal labels already shipped). The data-source
library now loads Touchstone **and** `.npy` (each entry carries a `DataSet`), so the `Snp` naming is stale. This
is a **pure rename / no behavior change** pass across the now-stable surface. The "NAMING DEBT" headers in
`SnpLibraryViewModel.cs` / `SnpEntryViewModel.cs` call this out explicitly.

## Renames
**Types + files:**
- `SnpLibraryViewModel` → `DataSourceLibraryViewModel`
  (`…/ViewModels/SnpLibraryViewModel.cs` → `DataSourceLibraryViewModel.cs`)
- `SnpEntryViewModel` → `DataSourceEntryViewModel`
  (`…/ViewModels/SnpEntryViewModel.cs` → `DataSourceEntryViewModel.cs`)

**Members/fields/params that carry the old name** (rename for consistency; grep each and update all refs):
- `DisplayWindowViewModel.SnpLibrary` (public) → `DataSourceLibrary`. This is a visible property — update all
  consumers (`WorkspaceViewModel.RefreshOpenDataDisplaysAsync` calls `…Window.SnpLibrary.ReloadChangedAsync(...)`;
  `DataDisplayViewModel.Library`, `PlotContainerViewModel.Library`, `PlotInspectorViewModel._library` /
  `LibraryEntries`, `TraceRowViewModel` library refs, etc.).
- Local fields/params typed as the old class (`_library`, `library`, `SnpLibraryViewModel?` types) → retype to
  `DataSourceLibraryViewModel`. Keep the member names `Library`/`LibraryEntries`/`LibraryChanged` as-is (already
  source-agnostic); only the **type** changes there.
- Any `snp`/`Snp`-named locals that are really *entries* or *sources* (not an actual `SNP` network object) →
  rename to `entry`/`source`. **Do NOT** rename genuine `SNP` (the RfCore network type) or `entry.Snp` (the
  per-entry SNP accessor used by the legacy matrix picker) — those stay; they're the real network object.

**XAML:** update `x:Type`/`DataType`/`xmlns`-qualified references and any design-time `DesignInstance` to the new
class names (`PlotInspectorView.axaml`, library/data-source views, any `<Design.DataContext>`).

## Guardrails (don't over-reach)
- **No behavior, signature shape, or serialization changes.** `.cdd` / `.cws` field names are unaffected — this
  is in-memory VM naming only. Do not touch `TraceConfig.SourcePath`, cube fields, or any persisted JSON keys.
- Keep `SNP`, `entry.Snp`, `DataSetBuilder.ToSnp/FromSnp`, `.sNp`/Touchstone strings, and file-extension logic
  unchanged — the rename is the **library/entry VM** identity, not the network type or the Touchstone concept.
- Remove the now-resolved "NAMING DEBT" header comments in both renamed files.
- Update doc references in `src/Ui/CLAUDE.md` and any `docs/` that name the old VM classes.

## Method
Rename via IDE/symbol rename where possible; otherwise grep `SnpLibraryViewModel`, `SnpEntryViewModel`, and
`SnpLibrary` (the property) across `src/Ui` (and tests) and replace. Then a global build catches stragglers
(TreatWarningsAsErrors). Update the two filenames last.

## Tests
No new tests. Rename any existing test symbols/usages so the suite compiles, and confirm the existing Data
Display tests (cube traces 7.2c-a, minimal labels 7.2c-b, dirty-indicator, save/load) still pass unchanged —
that's the regression oracle for a no-behavior rename.

## Gate
Build 0W/0E (TreatWarningsAsErrors); full test suite green. Manual smoke: open a Data Display, load a `.npy` and
a Touchstone source, add a cube trace and a network trace, save+reload `.cdd` — all behave exactly as before.

## On completion
Note in `src/Ui/CLAUDE.md`: the data-source library VMs are `DataSourceLibraryViewModel` /
`DataSourceEntryViewModel` (was `Snp*`); `DisplayWindowViewModel.DataSourceLibrary` (was `SnpLibrary`). This
closes Phase 7.2c.
