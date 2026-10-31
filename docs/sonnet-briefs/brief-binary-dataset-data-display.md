# Brief — binary simulation datasets (`.ds`) as a Data Display source

**Tag:** `R-bds-n` · **Phase:** Render seam, Data Display library, CLI
**Area:** `src/Render/DataDisplay/PlotSourceFile.cs`, `src/Render/DataDisplay/DataSourceView.cs`,
`src/Ui/DataDisplay/ViewModels/DataSourceEntryViewModel.cs` (`SourceKind`),
`src/Ui/DataDisplay/ViewModels/DataSourceLibraryViewModel.cs`, `src/Ui/Views/DataDisplay/DataDisplayView.axaml.cs`,
`src/Ui/Views/Properties/DatasetsListView.axaml.cs`, `src/Ui/ViewModels/WorkspaceViewModel.cs` (the open-file picker
and the extension switch near the `.spl` one), `src/Ui/Archive/WorkspaceArchiveScanner.cs`,
`src/Design/Revision/WorkspacePolicyFiles.cs`, `src/Cli/ReadBack.cs`, `src/Cli/JsonRun.cs`
**Depends on:** `BinaryDatasetReader` / `BinaryDatasetWriter` in `src/RfCore/Export` and the `.ds` export, as built
**Rule:** the format is called a **binary simulation dataset** (`.ds`) everywhere: UI text, docs, code, comments,
commit messages. No program, company or product is named in connection with it. Test files are written by
`BinaryDatasetWriter` from made-up numbers; no `.ds` file from outside the repo is ever committed.

---

## 0. What exists, what is missing

`BinaryDatasetReader` reads a `.ds` file into blocks: nested sweeps, each with independent variables (outermost
first) and dependent variables at every grid point. It refuses anything that is not a full grid, naming the
variable. `BinaryDatasetReader.ReadDataSet(path)` returns an ordinary `DataSet`:

- one **group per block**, named after the block's analysis (`SweepVds.SP1.SP` → `SP1`, `DC1.DC` → `DC1`; a
  one-segment name as itself), or the whole name with `_` for `.` where two blocks would collide;
- each dependent variable is a **cube on the block's sweep axes** (`SP1.VD.i` on `[Vds, Vgs]`), its `Unit` taken from
  the variable's quantity (`V`, `A`, `Hz`, `Ohm`) when the file names one;
- a complete element family `S[1,1]` … `S[n,n]` becomes **one cube `S`** with port axes `i`, `j` last (and `X[1]` …
  `X[n]` one cube `X` with a `port` axis), exactly the shape circuitRF's own S cubes have;
- text variables (port names) are not cubes; the sweep variables are axes, not cubes.

Model Composer already imports `.ds` (`ImportSettings.Ds`), with `Group.Variable` array names and a leading `-` in
the map to read a source current negated. **The Data Display cannot open a `.ds` at all**: its loaders dispatch on
`.npy`, Touchstone, `.spl` and `.lpcwave` only. This brief adds `.ds` as a fifth source kind that loads and plots
like a `.npy`.

## 1. `R-bds-1` — the Render seam (`PlotSourceFile.Load`)

`PlotSourceFile` is the one place a path becomes a `(DataSet, SNP)` for the GUI library, `circuitrf render --data`,
`circuitrf plot` and the Smith Chart. Add a `.ds` branch before the final fallback:

1. `BinaryDatasetReader.IsBinaryDataset(path)` (dispatch on the **content**, not only the extension, so a renamed
   file is still read; the extension check comes first for speed). Then `BinaryDatasetReader.ReadDataSet(path)`.
2. **`Z0` from `PortZ`.** In each group that has both an `S` cube and a `PortZ` cube (`[sweep…, freq, port]`), if
   every point has the same impedance per port, add a `Z0` cube `[port]` to that group through
   `DataSetBuilder.BuildZ0Cube`. If it varies, add none and return a note (§4). Without a `Z0`, virtual `Z`/`Y`
   cubes do not materialise, which is the first silent failure `PlotSourceFile`'s own header warns about.
3. Then `DataSourceView.MaterializeNetworkParamCubes(data)` and `DataSourceView.NetworkViewOf(data)`, exactly as the
   `.npy` branch does. A swept S cube has no network view by design (`NetworkMetrics.IsNetworkShaped`); that is
   correct here too and needs no special case.

**Check before writing code:** a `.ds` S cube's axes are `[Vds, Vgs, freq, i, j]`, with sweep axes outermost. Confirm
that a parametric-sweep `.npy` S cube from circuitRF has its sweep axes in the same position relative to `freq, i,
j`, and that the trace card's pinning of non-plotted axes works on it. If circuitRF puts sweep axes elsewhere,
reorder in the reader's `ToDataSet` so the two agree, not in the Data Display.

## 2. `R-bds-2` — the Data Display library (GUI)

- `SourceKind` gains `BinaryDataset`. Persist a `.ds` source in a `.cdd` the way a Touchstone, `.spl` or `.lpcwave`
  source is persisted (a reference to a file outside `results/`, document-relative where the others are), including
  the **broken-entry restore** path when the file is missing and later found.
- `DataSourceLibraryViewModel`: both loaders (path and `IStorageFile`) and the reload path load a `.ds` through
  `PlotSourceFile.Load`, **not a second reader call**, so the GUI and the CLI cannot disagree.
- File pickers: add `*.ds` to the "all data" filters and a row `Binary dataset (*.ds)` in
  `DataDisplayView.axaml.cs` (three pickers), `DatasetsListView.axaml.cs`, and `WorkspaceViewModel`'s open dialog
  (plus its extension switch next to `.spl`).
- The source list shows a `.ds` source by file name, with the same icon treatment `.npy` gets. No new controls and
  no explanatory text under the entry (owner rule: a readout says only what it is).
- `WorkspaceArchiveScanner`: classify a referenced `.ds` as a data file, like a `.spl`, so an archive asks about it.
  `WorkspacePolicyFiles.ResultPatterns`: add `*.ds`, since an export or a run's `-o` can now put one in
  `results/`.

## 3. `R-bds-3` — CLI

- `circuitrf read <file.ds>` (`ReadBack.cs`): list the groups and cubes with their axes and units, like `.npy`, and
  the text variables and refusal sentence when there are any. `JsonRun`'s extension → kind map gains
  `".ds" => "ds"`.
- `plot` and `render --data` need nothing beyond §1, because they load through `PlotSourceFile`. Gate that with one
  test (§6), and do not add a second path.
- The MCP `read` tool and the `reference` topics that list readable result formats gain `.ds` with one line each.

## 4. `R-bds-4` — notes, never silent

Anything the load does not do as plainly as for a `.npy` is said once, as a note on the source, in the library and
in `read`'s output:

- text variables that are not cubes (`SP1.PortName[1]`);
- a `PortZ` that varies, so no `Z0` was added;
- a refusal (not a grid, an unknown container version): the reader's own sentence, shown where a broken `.npy` shows
  its error.

## 5. Export — already built

Writing `.ds` exists and is not part of this brief: `BinaryDatasetWriter.FromDataSet` turns a `DataSet` into blocks
(one per group and set of shared sweep axes; trailing `i`, `j` port axes as `X[i,j]` elements; a `Z0 [port]` beside
an `S` as `PortZ[p]` at every point of S's sweep — which is why §1's PortZ → Z0 rule round-trips a circuitRF S
result), `ExportFormat.Ds` dispatches to it from `DataSetExporter`, the Export Data dialog has a `.ds` format button,
and the CLI run verbs' `-o out.ds` writes one. Whether another program opens a file circuitRF wrote is not known and
must not be claimed in UI or docs.

## 6. Tests (minimal: one per claim; every file written by `BinaryDatasetWriter`)

- `PlotSourceFile.Load` on a written `.ds`: the `DataSet` resolves `SP1.S` and `SP1.Z` (virtual), and a group with a
  uniform `PortZ` has a `Z0`; with a varying `PortZ`, no `Z0` and a note.
- An unswept 2-port `.ds` (one frequency sweep only) gets a network view, so a derived trace (Max Gain) resolves.
- A `.cdd` saved with a `.ds` source reopens it; with the file moved away it shows as broken, and restores when
  the file is opened again.
- `circuitrf read x.ds` lists groups, cubes, axes and units; `circuitrf plot x.ds --trace cube=S,i=2,j=1,y=db`
  writes an SVG byte-identical to the in-process `PlotSourceFile` → composer path.
- A circuitRF S result exported as `.ds` and loaded back through `PlotSourceFile` has its `Z0` again (§1 rule 2).

**Visual verification:** `tools/UiSnapshot` of the Data Display with a `.ds` source loaded and an `S21` trace in
dB over frequency with `Vds`/`Vgs` pinned. List the PNG in the completion note.

## 7. Decisions for the owner (proposed defaults in bold)

a. User docs: **add `.ds` as "binary simulation dataset" to `file-formats.md` and the Data Display page**, with one
   example `read`/`plot` command, or leave it undocumented.
b. Group naming: **the analysis name (`SP1`)**, or the whole block name with dots replaced.

## 8. Not in this brief

- Uneven (non-grid) sweeps: still refused by the reader.
- Any change to the Model Composer import, which already reads `.ds`.

## 9. On completion

Findings go in `src/Render/RESOLVED.md` (seam) and `src/Ui/RESOLVED.md` (library), never a `CLAUDE.md`. Before any
commit, grep the diff for program, company and product names and remove them.
