# Brief: Data Exporter (GUI export to .npy / .mat / tab-delimited / Touchstone)

Design: `docs/design/data-exporter-ui.md` (read it first — this brief is the build order).
Related: `docs/design/data-export.md` (RfCore writer API), `docs/design/results-dataset-layout.md`
(grouped `run.npy`). Stack/rules: .NET 10, Avalonia 12, `TreatWarningsAsErrors=true` (capture
nullable-property reads into locals). RfCore references **no** Avalonia (firewall — keep the new
RfCore code framework-free). Build must end **0W/0E**; add gate tests; report total test count.

Goal: a modal **Data Exporter** window that exports a selected `results/<schematic>/run.npy` to
`.npy`, `.mat`, tab-delimited text, or Touchstone, opened from (a) a new Data Display toolbar button
and (b) a new **File → Export…** menu item. Plus: fix the stale `MatWriter`.

Work in this order.

---

## Part A — RfCore (no Avalonia)

### A1. `RfCore/src/Export/ExportFormat.cs` — add `Tsv`
Add `Tsv` to the enum (after `Npy`).

### A2. `RfCore/src/Export/DataSetExporter.cs` — dispatch Tsv
In the `switch (format)` add:
```csharp
case ExportFormat.Tsv:
    TsvWriter.Write(path, workingDs, opts);
    break;
```
(`TsvWriter` takes no payload.)

### A3. `RfCore/src/Export/DataSetSubset.cs` (new) — group subset
```csharp
using System.Collections.Generic;
using RfCore.Data;
namespace RfCore.Export;

/// <summary>Builds a new DataSet containing only the named groups' cubes (cubes are
/// immutable — shallow-copied via AddToGroup). Unknown group names are skipped.</summary>
public static class DataSetSubset
{
    public static DataSet SelectGroups(DataSet ds, IEnumerable<string> groups)
    {
        var outp = new DataSet();
        foreach (var g in groups)
        {
            if (!ds.ContainsGroup(g)) continue;
            foreach (var kvp in ds.CubesIn(g))
                outp.AddToGroup(g, kvp.Key, kvp.Value);
        }
        return outp;
    }
}
```

### A4. `RfCore/src/Export/TsvWriter.cs` (new) — tab-delimited, long format
One section per cube, across all groups. For each `group` in `ds.Groups`, each `(name, cube)` in
`ds.CubesIn(group)`:
- Section header line: `# {group}.{name}` (omit `group.` when `group == DataSet.DefaultGroup`).
- Column header row (tab-separated): one column per axis as `{axis.Name}[{axis.Unit}]` (omit `[]`
  when unit empty); then `value` for real cubes, or `re`\t`im` for complex.
- One data row per cube element = Cartesian product of axis indices (row-major, matching
  `cube.ComplexValues`/`RealValues` flat order). Each row lists, per axis, the axis **label** when
  `axis.Labels != null` else `axis.Values[idx]` (InvariantCulture, `"G17"`), then the datum
  (`Real`\t`Imaginary` for complex, value for real).
- Blank line between sections.
Use `\t` separators, `\n` newlines, `StreamWriter` (UTF-8). Iterate indices with an odometer over
`cube.Axes[d].Length`. Rank-0 (scalar) cube → header `value`/`re`,`im` + one data row, no axis columns.

### A5. `RfCore/src/Export/MatWriter.cs` — rewrite for grouped DataSet (currently stale)
It iterates `ds.Cubes` (default group only) → writes nothing for grouped runs. Mirror `NpyWriter`'s
grouped traversal:
- Bump `internal const int FormatVersion = 2;`.
- Replace the `foreach (var kvp in ds.Cubes)` block with:
  ```csharp
  foreach (var group in ds.Groups)
  {
      // default group → datasets directly under /dataset; named group → /dataset/<group>/
      H5Group target, axesTarget;
      if (group == DataSet.DefaultGroup) { target = datasetGroup; axesTarget = axesGroup; }
      else
      {
          target = new H5Group(); datasetGroup[EscapeName(group)] = target;
          axesTarget = new H5Group(); axesGroup[EscapeName(group)] = axesTarget;
      }
      foreach (var kvp in ds.CubesIn(group))
      {
          string h = EscapeName(kvp.Key);
          target[h] = MakeCubeDataset(kvp.Value);
          var cag = new H5Group(); axesTarget[h] = cag;
          cag["axes.json"] = new string[] { BuildAxesJson(kvp.Value) };
      }
  }
  ```
  (`datasetGroup["__axes__"] = axesGroup;` stays; per-group axes nest under it.)
- Add an ordered group-name string dataset: `datasetGroup["groups"] = ds.Groups.ToArray();`.
- Leave `MakeShapedArray`, `ComplexEntry`, `BuildLinearNetworkGroup`, `MakeCubeDataset`,
  `BuildAxesJson`, `EscapeName` unchanged. Linear-network path untouched (UI never enables it).
- Note: HDF5 group key collisions — a cube literally named the same as a group is not expected in
  run results; no extra uniquification needed (unlike npy's flat field bag).

### A6. `RfCore/src/Export/TouchstoneExporter.cs` (new) — Touchstone slicing + renorm + write
Framework-free. Public surface roughly:
```csharp
public sealed record TouchstoneExportOptions(
    double Z0Ohms, int Digits, char DigitFormat /*'f'|'g'|'e'*/, MatrixFormat MatrixFormat);

public enum TouchstoneExportStatus { Ok, NoSCube, NameCollision }

public sealed record TouchstoneExportResult(
    TouchstoneExportStatus Status, IReadOnlyList<string> WrittenPaths,
    IReadOnlyList<string> CollidingNames, Z0Kind SourceZ0Kind, bool Renormalized);
```
Two helpers:
1. **Inspect** (for the VM to drive the UI): given `ds` + analysis `group`, return the S cube's
   non-`freq`/`i`/`j` axes (name, unit, values, labels) and the `Z0Kind`
   (`DataSetBuilder.ClassifyZ0(ds.CubesIn(group)["Z0"])`; if no `Z0` cube assume `UniformReal`).
   The S cube is `ds.CubesIn(group)["S"]` with axes named `freq`,`i`,`j` (+ extra sweep axes).
2. **Export**: params `(ds, group, TouchstoneExportOptions opts, IReadOnlyDictionary<string,int>
   pinnedIndexByAxis, bool allSweepFiles, string baseFilePathNoSuffix)`.
   - Identify axis dims: `freq`,`i`,`j` by name; the rest are sweep axes. `nPorts = i-axis length`.
   - Build the set of pinned-index combinations: single (`pinnedIndexByAxis`) or, if `allSweepFiles`,
     the Cartesian product over every sweep axis's indices.
   - For each combination: slice the S cube to `[freq,i,j]` (pin sweep axes to their index, keep
     `freq`/`i`/`j`) using `DataCube.Slice(args)` with `Range.All` for kept axes and the int index for
     pinned — mirror `DataSet.ParameterTrace`/`NodeTrace` arg-building. Extract per-freq
     `Mat<Complex>` (`[freq,i,j]` row-major). Read source per-port Z0 from the `Z0` cube
     (`ComplexValues`, length `nPorts`; if absent, `Z0Array(50)`).
   - Renorm to uniform real: `toZ0 = RFNetwork.Z0Array(new Complex(opts.Z0Ohms,0), nPorts)`;
     `mats[fi] = RFNetwork.SToS(srcMat[fi], srcPerPortZ0, toZ0)`. (Uniform-real==target → no-op.)
   - `var snp = new SNP(freqs, mats, MatrixType.S, opts.MatrixFormat, new Complex(opts.Z0Ohms,0));`
   - Filename: single → `{baseFilePathNoSuffix}.s{nPorts}p`; all-sweep →
     `{baseFilePathNoSuffix}__{axis}={val}{unit}…​.s{nPorts}p` (sanitize: drop `/\\:*?"<>|` and quotes,
     spaces→`_`; format val InvariantCulture; prefer `Axis.Labels[idx]` when present).
   - **Collision check first**: build all target paths; if two combinations yield the same filename
     return `Status=NameCollision` with the clashing names (write nothing).
   - `precision = $"{char.ToUpperInvariant(opts.DigitFormat)}{opts.Digits}"` (e.g. `"F12"`).
   - Write each: `TouchstoneIO.WriteFile(snp, p, formatOverride: opts.MatrixFormat,
     touchstone11Compatible: true, precision: precision)`. **Never** pass
     `touchstone11Compatible:false` (it emits a spurious complex-Z0 comment).
   - Return `Ok` + written paths + `SourceZ0Kind` + `Renormalized` (= kind != UniformReal || Z0≠src).
   Existing-file overwrite is handled by the UI (picker for single; batch confirm for all-sweep) —
   `TouchstoneExporter` just writes.

### A7. RfCore gate tests (`RfCore.Tests` or the Export test project)
- `MatWriter` grouped: build a 2-group DataSet (e.g. `HB1`+`measurements`), write `.mat`, reopen with
  PureHDF, assert `/dataset/HB1/<cube>` and `/dataset/groups` exist and `format_version == 2`.
- `TsvWriter`: rank-3 complex cube → header + correct row count (= ∏ axis lengths) + a spot-checked row.
- `DataSetSubset.SelectGroups`: keeps only named groups.
- `TouchstoneExporter`: (i) uniform-real source, single slice → file parses back via
  `TouchstoneIO.ReadFile` to matching S; (ii) non-uniform Z0 → `Renormalized==true` and round-trip S at
  the user Z0 matches a hand `RFNetwork.SToS`; (iii) all-sweep over a 2-pt sweep → 2 files; (iv) forced
  name collision → `NameCollision`, no files written.

---

## Part B — UI (Avalonia)

### B1. `DataExporterViewModel` (new, `src/Ui/.../ViewModels/` near the Data Display VMs)
State: `ExportFormat`/Touchstone selector enum; `ObservableCollection` of datasource items
(`<schematic>` + abs path); selected datasource; loaded `DataSet`; an `ObservableCollection` of
**include rows** (`{ GroupName, IsChecked }`) built from `ds.Groups` (default all true) + a
`MeasurementsAvailable`/`IncludeMeasurements` pair; Touchstone options (`Z0=50`, `Digits=12`,
`DigitFormat='f'`, `MatrixFormat=MA`); Touchstone slicing rows (per extra S-axis: name, unit,
value options, selected index) + `SaveAllSweepFiles`; computed `Z0Notice`/`ShowZ0Notice`;
`CanExport`. Ctor takes `resultsRoot` (string?) + optional preselect `<schematic>`.
- Enumerate: list dirs under `resultsRoot` containing `run.npy`, newest-first; on selection load via
  `DataSetImporter.Import`.
- Format = Touchstone → switch include list to single-select, filter to groups whose
  `CubesIn(group)` contains `"S"`; hide measurements; populate slicing rows from
  `TouchstoneExporter` inspect; recompute Z0 notice from `ClassifyZ0`.
- Pure export methods the code-behind calls after the picker: `ExportDataSet(path)` (npy/mat/tsv →
  `DataSetSubset.SelectGroups` + `DataSetExporter.Export`), `ExportTouchstone(basePathNoSuffix)` →
  `TouchstoneExporter.Export(...)` returning the result (so code-behind can report collisions/paths).
- Suggested filename base = selected `<schematic>`; extension chosen by format (`.npy`/`.mat`/`.txt`;
  Touchstone uses `.s{N}p`, N from the S cube's `i` axis length).

### B2. `src/Ui/Views/Dialogs/DataExporterDialog.axaml(.cs)` (new)
Copy the `AnalysisEditorDialog` pattern: `Window` subclass, ctor `(DataExporterViewModel vm)` sets
DataContext + seeds controls + subscribes `vm.PropertyChanged`; static
`public static async Task ShowAsync(Window? owner, DataExporterViewModel vm)` with the
null-owner→first-active/visible-window fallback (copy verbatim from `AnalysisEditorDialog.ShowAsync`).
Layout per design §3 (card `Border`, `TextBlock.label`, compact `ComboBox`, `seg-btn` for format/
digit-format/matrix-format, an Avalonia `ListBox` with a `CheckBox` item template for includes,
`CrfWarningBrush` selectable Z0 notice). **Picker in code-behind** (Avalonia `StorageProvider`, like
`DataDisplayView.axaml.cs`):
- Export click → if invalid, no-op; else:
  - npy/mat/tsv or single Touchstone: `SaveFilePickerAsync` with format filter +
    `SuggestedFileName = vm.SuggestedFileName`; call `vm.ExportDataSet(path)` /
    `vm.ExportTouchstone(pathWithoutExt)`.
  - all-sweep Touchstone: `OpenFolderPickerAsync` to choose the output **directory**; the base name
    is auto-set to `<schematic>` → call `vm.ExportTouchstone(Path.Combine(dir, schematic))`;
    on `NameCollision` show the clashing names via `ShowErrorAsync`; if any returned path already
    existed, pre-check and batch-confirm (reuse the SaveChanges/OK dialog) before writing.
  - Surface failures via the existing `ShowErrorAsync` helper; `Close()` on success.

### B3. Data Display toolbar button — `DataDisplayView.axaml` + `DisplayWindowViewModel`
Add an `ExportDataCommand` (`[RelayCommand]`) to `DisplayWindowViewModel` that builds a
`DataExporterViewModel` (results root from `GetResultsRootAction?.Invoke()`, preselect the current
datasource's schematic when it's a results run) and calls `DataExporterDialog.ShowAsync(owner, vm)`.
Wire the owner/StorageProvider the same way the existing save/open commands are injected in
`DataDisplayView.axaml.cs OnLoaded` (add a `SetExportDataAction`-style seam if needed, mirroring
`SetSaveDataDisplayAsAction`). Add a toolbar `Button` in the `StackPanel.Toolbar` (after the
"Load Dataset…" button / datasource combobox) with `mi:MaterialIcon Kind="Export"` (or
`DatabaseExportOutline`) and tooltip "Export Data".

### B4. File → Export… — `WorkspaceWindow.axaml` (+ `.axaml.cs`) + `WorkspaceViewModel`
Add an `ExportDataCommand` on `WorkspaceViewModel` that builds a `DataExporterViewModel`
(`resultsRoot = GetResultsRoot()` = `<workspaceRoot>/results` when a workspace is open, else disabled)
and shows the dialog via `DataExporterDialog.ShowAsync(owner, vm)`. Add an `<MenuItem Header="Export…">`
to the in-window File `Menu` and a matching `NativeMenuItem` (macOS), next to the existing
"New Data Display"/"Open Data Display…"/"Open Symbol…" items (search those headers to find both menus).

### B5. UI gate tests (`tests/Ui.Tests`, framework-free)
`DataExporterViewModel` is a plain VM (no Avalonia host) — test: datasource enumeration finds a
`run.npy` dir; include rows default all-checked; Touchstone mode filters to S-bearing groups + builds
slicing rows for extra axes; `Z0Notice` shows for a non-uniform-Z0 fixture; `SuggestedFileName`
extension tracks the format. (Do not instantiate `WorkspaceViewModel` — Avalonia host required.)

---

## Notes / gotchas
- Capture nullable-property reads into locals before use (TreatWarningsAsErrors).
- The grouped S cube axes are named `freq`,`i`,`j` (1-based port values on `i`/`j`); slicing for
  Touchstone keeps those three and pins the rest — reuse the arg-building pattern in
  `DataSet.ParameterTrace`.
- Touchstone precision is a .NET format string (`"F12"`), not a digit count alone.
- Always `touchstone11Compatible: true`.
- `measurements` group name = `DataSet.MeasurementsGroup`; default group = `DataSet.DefaultGroup`.
- After landing, add a newest-first changelog entry to `src/Ui/CLAUDE.md` (brief name, files, tests,
  build result) per house style.
