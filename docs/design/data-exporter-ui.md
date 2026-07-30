# Data Exporter (GUI) — design

Status: design (awaiting implementation). Owns the **GUI** layer that lets a user export a
run's simulation data to `.npy`, `.mat`, tab-delimited text, or Touchstone. It sits **on top of**
the RfCore writer/format spec in `data-export.md` (the `.mat`/`.npy` programmatic API) and reuses
`TouchstoneIO` + `RFNetwork` for Touchstone. Read with: `data-export.md` (writer API/format),
`results-dataset-layout.md` (the grouped `run.npy` shape this consumes), `ui-design.md` +
`src/Ui/CLAUDE.md` (HIG), and `src/RfCore/Export/CLAUDE.md`.

## 1. Scope

A modal **Data Exporter** window. The user picks a **format**, a **datasource** (a
`results/<schematic>/run.npy`), and **which analyses + measurements** to include, then exports via
the **system file picker**. Out of scope: the linear-network payload (`IncludeLinearNetwork`) and
linear-interior eval (`LinearEvalMode`) — the UI always exports with `ExportOptions.Default`
(`IncludeLinearNetwork = false`, `LinearEvalMode = EvaluateNone`). No `ILinearNetworkPayload` is
passed.

Two entry points, both open the same modal:
- **"Export Data" button** on the Data Display toolbar (`DataDisplayView.axaml`).
- **File → Export…** menu item (in-window `Menu` + macOS `NativeMenu` in `WorkspaceWindow.axaml`).

## 2. The datasource

Source = one grouped `run.npy` (per `results-dataset-layout.md`): an ordered set of **analysis
groups** (`HB1`, `SP1`, `DC1`, …) plus an optional **`measurements`** group (`DataSet.MeasurementsGroup`).

- **Enumeration.** Scan `<workspaceRoot>/results/<schematic>/run.npy`; list each `<schematic>` whose
  `run.npy` exists, newest first. The combobox shows `<schematic>`. The results root comes from the
  Data Display's `GetResultsRootAction` (toolbar entry) or `WorkspaceViewModel.GetResultsRoot()`
  (`<workspaceRoot>/results`, File-menu entry).
- **Load.** On selection, load the grouped DataSet via `RfCore.Export.DataSetImporter.Import(path)`.
  `ds.Groups` yields analysis groups (and `"measurements"` when present).
- **Preselection.** From the toolbar, preselect the schematic backing the Data Display's current
  datasource when it is a results run; otherwise the newest. From File→Export, the newest (or none).

## 3. Window layout (HIG)

A `Window` subclass `DataExporterDialog` in `src/Ui/Views/Dialogs/`, following the
`AnalysisEditorDialog` idiom (DataContext = VM, `PropertyChanged` → control sync, OK/Cancel
`Close(result)`, static `ShowAsync(owner, vm)` with the null-owner→first-visible-window fallback).
Card look and compact controls per HIG: outer `Border` (`SystemChromeMediumLowColor`, CornerRadius 8,
Padding 10), `TextBlock.label` (FontSize 10, Opacity 0.6) section labels, compact base `ComboBox`
(FontSize 10, Height 22), `ToggleButton.seg-btn` for the format selector, `CrfTileBorderBrush`
card borders, `CrfWarningBrush` for the gentle Z0 notice.

Sections, top to bottom:
1. **Format** — segmented buttons (`seg-btn`): `.npy` · `.mat` · `Tab-delimited` · `Touchstone`.
2. **Datasource** — `<schematic>` combobox (§2).
3. **Include** — an Avalonia **`ListBox`** of the datasource's analysis groups, each a checkable row
   (`SelectionMode="Multiple"` via `CheckBox` item template). A separate **"Measurements"** check row
   appears when the `measurements` group exists. **Default: all analyses + measurements checked.**
   - *Touchstone mode override:* the list becomes **single-select** and filtered to **S-bearing
     analyses only** (groups whose cubes include an `"S"` cube). Measurements row hidden. (See §5.)
4. **Touchstone options** (visible only in Touchstone mode) — Reference impedance **Z0** (NumericUpDown,
   **real**, default **50**), **digits** (default **12**), **digit format** f/g/e (`seg-btn`, default
   **f**), **matrix format** MA/RI/dB (`seg-btn`, default **MA**).
5. **Touchstone slicing** (visible only in Touchstone mode, only when the selected S cube has axes
   beyond `freq`/`i`/`j`) — one **down-select combobox per extra (sweep) axis**, plus a **"Save all
   sweep combinations to separate files"** check (§5.3).
6. **Gentle Z0 notice** (visible only in Touchstone mode when the selected S analysis is not
   uniform-real) — a selectable `CrfWarningBrush` text block (§5.4).
7. **Buttons** — Cancel · **Export** (the system picker opens on Export; see §6).

## 4. Format routing

`.npy`, `.mat`, tab-delimited all take the **selected subset** of the grouped DataSet; Touchstone is
its own path.

- **Subset.** Build a new `DataSet` containing only the checked groups (+ `measurements` when checked)
  via a small RfCore helper `DataSetSubset.SelectGroups(ds, groups)` (copies cubes with
  `AddToGroup(group, name, cube)` — cubes are immutable, shallow copy is fine).
- **`.npy`** → `DataSetExporter.Export(subset, path, ExportFormat.Npy)`.
- **`.mat`** → `DataSetExporter.Export(subset, path, ExportFormat.Mat)` — **after** the MatWriter
  rewrite (§7).
- **Tab-delimited** → `DataSetExporter.Export(subset, path, ExportFormat.Tsv)` (new enum value) →
  new `TsvWriter` (§6.1). Routing through `DataSetExporter` keeps the size-estimate behaviour; the
  linear-eval branch is inert (`EvaluateNone`).
- **Touchstone** → new `TouchstoneExporter` (§5), not `DataSetExporter` (different I/O shape:
  single analysis, slicing, renorm, possibly many files).

## 5. Touchstone

### 5.1 Analysis constraint
Touchstone writes one network. The user must select exactly **one S-bearing analysis** — a group
whose cubes contain `"S"` (the grouped S cube is `[<sweep…>, freq, i, j]`, axes named `freq`,`i`,`j`
per `DataSetBuilder.FromSnp`). The ListBox enforces single-select and filters to S-bearing groups in
Touchstone mode; if the datasource has none, the Export button is disabled with a short hint.

### 5.2 Slicing to [freq, i, j]
Touchstone needs a 3-axis `[freq, i, j]` matrix series. The selected S cube's axes are partitioned:
`freq`, `i`, `j` are the network axes; **every other axis is a sweep axis** to be pinned (down-select)
or iterated (all-files). For each sweep axis present a combobox of its values (use `Axis.Labels` when
present, else formatted `Axis.Values` + unit). Default each to index 0.

### 5.3 All-sweep multi-file
When "Save all sweep combinations…" is checked: the down-select combos disable; the exporter iterates
the **Cartesian product** of all sweep-axis values, slices each to `[freq, i, j]`, and writes **one
file per combination**. Filename suffix encodes the pinned values:
`<base>__<axis>=<val><unit>[__<axis>=<val><unit>…].sNp` (sanitized: strip path separators/quotes,
spaces→`_`). **Collision check before writing any file:** build the full target-path set; if two
combinations map to the same filename (value-formatting collision) → abort with a message listing the
clashes; if any target already exists on disk → confirm overwrite once for the batch (no silent
overwrite). The extension is `.s<N>p` for N ports.

### 5.4 Port-normalization (uniform real Z0 only)
Touchstone supports a single **real** Z0. The selected S analysis carries a `"Z0"` cube (per-port
complex reference impedances); classify it with `DataSetBuilder.ClassifyZ0` → `Z0Kind`.
- `UniformReal` and equal to the user's Z0 → write directly (renorm is a numerical no-op).
- `UniformComplex` / `NonUniform` / `UniformReal≠userZ0` → **renormalize to the user's uniform real
  Z0** and show the gentle notice. Renorm per frequency at the matrix level:
  `toZ0 = RFNetwork.Z0Array(new Complex(userZ0,0), nPorts)`,
  `mats[fi] = RFNetwork.SToS(sourceMat[fi], sourcePerPortZ0, toZ0)`, then
  `new SNP(freqs, mats, MatrixType.S, userFormat, new Complex(userZ0,0))`.
- **Gentle notice text** (when not uniform-real): *"This analysis uses per-port or complex reference
  impedances. Touchstone supports only a single real reference impedance, so the data will be
  renormalized to {Z0} Ω on export."* (Z0 substituted live.)

### 5.5 Write
`TouchstoneIO.WriteFile(snp, path, formatOverride: userFormat, touchstone11Compatible: true,
precision: $"{f|g|e}{digits}")`. `touchstone11Compatible: true` guarantees a clean
`# <unit> S <fmt> R <Z0>` option line and (since we pre-renorm to `snp.Z0` real) performs no second
renorm. **Do not** use `touchstone11Compatible: false` — it always emits a spurious
"complex Z0" comment block. `precision` is a .NET format string: f→`"F12"`, g→`"G12"`, e→`"E12"`.
`MatrixFormat` enum: `MA`/`RI`/`DB`.

## 6. File picker and write
The system picker lives in the dialog **code-behind** (Avalonia `StorageProvider`, as in
`DataDisplayView.axaml.cs`). On **Export**:
- Single file (npy/mat/tsv, and single-file Touchstone): `SaveFilePickerAsync` with a format-typed
  filter and **suggested filename = `<schematic>.<ext>`** (`.npy`/`.mat`/`.txt`/`.s<N>p`).
- All-sweep Touchstone: `OpenFolderPickerAsync` chooses the output **directory**; the base name is
  auto-set to `<schematic>` and per-file names are derived (§5.3) in that directory.
The code-behind passes the chosen path(s) to the VM's export method (pure, RfCore calls), surfaces
success/failure via the existing `ShowErrorAsync` helper, and `Close()`s on success.

### 6.1 Tab-delimited scheme
Excel-import friendly. **Long format, one section per cube.** For each cube in each selected group:
a section header line `# <Group>.<Cube>` (group omitted for default group), then a tab-delimited
**column-header row** — one column per axis (`<axisName>[<unit>]`), then `value` (real cubes) or
`re`,`im` (complex cubes) — then one row per cube element (Cartesian product of axis indices) listing
each axis's **value** (label when present) and the datum. Sections separated by a blank line. This
writes **all** data for any rank and reads naturally for the common `freq`-vs-value case. Extension
`.txt` (Excel opens tab-delimited `.txt` directly).

## 7. MatWriter rewrite (stale → grouped)
`src/RfCore/Export/MatWriter.cs` currently iterates `ds.Cubes` (**default group only**) → it writes
**nothing** for a grouped `run.npy`. Rewrite to mirror `NpyWriter`'s grouped traversal:
- Walk `ds.Groups` → `ds.CubesIn(group)`; emit each cube as an HDF5 dataset under a per-group
  subgroup: `/dataset/<group>/<cube>` (default group → `/dataset/<cube>` as today).
- Axis JSON under `/dataset/__axes__/<group>/<cube>/axes.json` (reuse `BuildAxesJson`).
- Add a `/dataset/groups` string dataset (ordered group names) and bump `FormatVersion` → **2**.
- Keep `MakeShapedArray`, the `ComplexEntry` compound type, and `BuildLinearNetworkGroup` unchanged
  (linear-network stays off for UI exports; no MatReader exists, so there is no reader to keep in sync).

## 8. New / changed RfCore code (firewall-safe; no Avalonia)
- `ExportFormat.cs` — add `Tsv`.
- `DataSetExporter.cs` — add `case ExportFormat.Tsv: TsvWriter.Write(path, workingDs, opts); break;`.
- `TsvWriter.cs` (new) — §6.1.
- `TouchstoneExporter.cs` (new) — §5 logic: given the grouped DataSet, the chosen analysis group, the
  Touchstone options, and either a pinned-index map or the all-files flag, slices the S cube, reads
  the group's `Z0` cube, renorms via `RFNetwork`, builds `SNP`s, and writes via `TouchstoneIO`.
  Returns the written paths (and a typed collision/again result). Unit-testable.
- `DataSetSubset.cs` (new) — `SelectGroups(DataSet, IEnumerable<string>) → DataSet`.

## 9. UI files
- `src/Ui/Views/Dialogs/DataExporterDialog.axaml(.cs)` (new).
- `src/Ui/.../DataExporterViewModel.cs` (new) — format/datasource/selection state, the analyses+
  measurements list, Touchstone options + slicing state, validation (`CanExport`), and the pure
  export methods that call the RfCore writers.
- `DataDisplayView.axaml` — add an "Export Data" toolbar button (icon `Export` /
  `DatabaseExportOutline`) → new `ExportDataCommand` on `DisplayWindowViewModel`.
- `WorkspaceWindow.axaml` (+ `.axaml.cs`) — add "Export…" to the in-window File `Menu` and the macOS
  `NativeMenu`, next to "New Data Display" / "Open Data Display…" / "Open Symbol…", → command on
  `WorkspaceViewModel`.

## 10. Open decisions (defaults chosen; redirect if needed)
| # | Question | Default implemented |
|---|----------|---------------------|
| 1 | Gentle non-uniform-Z0 wording (§5.4) | The sentence in §5.4. |
| 2 | Tab-delimited >2-axis scheme | Long format + per-cube sections (§6.1). |
| 3 | Tab-delimited extension | `.txt`. |
| 4 | Tab/Touchstone routing | Tsv via `DataSetExporter` (`ExportFormat.Tsv`); Touchstone via standalone `TouchstoneExporter`. |
| 5 | All-sweep filename suffix | `<base>__<axis>=<val><unit>…` (§5.3). |
| 6 | Existing-file collision (all-sweep) | One batch overwrite-confirm; abort on intra-batch name clashes. |
| 7 | All-sweep picker UX | Folder picker; base name auto-set to `<schematic>` (§6). |
