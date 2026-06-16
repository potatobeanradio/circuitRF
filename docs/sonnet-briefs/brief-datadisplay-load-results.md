# Sonnet Brief — Data Display: load HB/results `.npy` from `results/<schematic>/` (unblocks 7.3 testing)

**Goal.** Give the Data Display a fast way to load a schematic's run results (`.npy` under
`results/<schematicKey>/`) as data sources, plus a way to pick a **different `<schematicKey>` directory**.
This unblocks testing 7.3 (cube traces / families) with real HB sweep data. (S-param testing worked via the
Touchstone path; HB cubes have had no easy entry point.)

**Good news — the plumbing already works.** `DataSourceLibraryViewModel.LoadFileAsync(path)` already loads
`.npy` fully (→ `DataSetImporter` → DataSet), and the file picker in `DataDisplayView.axaml.cs:DoOpenFileAsync`
already lists `*.npy`. So today you *can* load HB results by hand-navigating to
`results/<schematicKey>/foo.npy`. This brief adds a **results-aware affordance** so the user doesn't hand-navigate.

## What to build — a "Load Run Results…" picker scoped to `results/`
Add a command + small UI on the Data Display that:
1. Resolves the **workspace results root** = `<workspaceRoot>/results/` (see "Workspace root" below).
2. Lets the user pick a **`<schematicKey>` subdirectory** under `results/` (these are the per-schematic result
   folders written by `RunResultsWriter`, each containing `<analysisName>.npy` + a `.source` marker file).
3. Loads **all `.npy` files** in the chosen subdirectory as data sources (skip the `.source` marker), via the
   existing `DataSourceLibrary.LoadFileAsync(path)` per file (dedupe is already handled there).

**Two acceptable UIs — pick the simpler that fits the existing chrome:**
- **(A) Folder picker (smallest):** a toolbar/menu item "Load Run Results…" → `StorageProvider`
  `OpenFolderPickerAsync` with `SuggestedStartLocation` = the `results/` root → enumerate `*.npy` in the chosen
  folder → `LoadFileAsync` each. This gives "pick a different `<schematic>` directory" for free (the user can
  browse to any `results/<key>/`). Do this first; it's the least code.
- **(B) Results browser flyout (nicer, optional follow-up):** a small list of `<schematicKey>` subdirs under
  `results/` (each row = the folder name + analysis count); selecting one loads its `.npy`s. Only build this if
  (A) feels insufficient — note it as a follow-up rather than gold-plating now.

Implement **(A)** in this brief.

## Workspace root — how the Data Display learns it
The Data Display is file-addressed and doesn't currently know the workspace root. `RunResultsWriter` writes to
`<baseDir>/results/...` where `baseDir` is the workspace root (and for scratch, a recovery-session dir). Wire the
root in the same place other display→workspace context is injected (`DataDisplayView.OnLoaded` sets the
`Window`'s action callbacks; `WorkspaceViewModel` already injects `OpenFileAsNewDisplayAction` etc.):
- Add a `Func<string?>? GetResultsRootAction` (or reuse an existing workspace-context getter) on
  `DisplayWindowViewModel`, set by `WorkspaceViewModel` to return `<CurrentWorkspacePath>/results` (null when no
  workspace / scratch). Read `WorkspaceViewModel.CurrentWorkspacePath` (already used by the save pipeline).
- In `DoLoadRunResultsAsync` (new, in `DataDisplayView.axaml.cs`), use that root as
  `SuggestedStartLocation` (via `StorageProvider.TryGetFolderFromPathAsync`); if null, fall back to the user's
  home / no suggestion. The folder picker still lets the user navigate anywhere (so "a different schematic dir"
  works even across workspaces).

## Wiring (mirror the existing `DoOpenFileAsync` pattern)
- `DisplayWindowViewModel`: add `LoadRunResultsCommand` + `SetLoadRunResultsAction(Func<Task>)` +
  `GetResultsRootAction` (parallel to `OpenFileCommand`/`SetOpenFileAction`).
- `DataDisplayView.OnLoaded`: `win.SetLoadRunResultsAction(DoLoadRunResultsAsync);`
- `DataDisplayView.DoLoadRunResultsAsync`: folder picker (suggested start = results root) → enumerate
  `Directory.GetFiles(folder, "*.npy")` → `foreach LoadFileAsync(path)`. Guard: no files → a brief Message
  ("No .npy results in <folder>").
- `WorkspaceViewModel`: where it injects the other display actions for a newly-opened Data Display
  (the `OpenOrActivateDataDisplayCoreAsync` / `RefreshOpenDataDisplaysAsync` area), set
  `window.GetResultsRootAction = () => CurrentWorkspacePath is { } w ? Path.Combine(w, "results") : null;`
- Toolbar/menu: add a "Load Run Results…" button next to the existing Load Data / Open Display buttons in the
  Data Display chrome (`DataDisplayView.axaml`), bound to `LoadRunResultsCommand`. Material icon e.g.
  `DatabaseArrowDown` or `FolderDownload`. Keep it subtle, matching the existing toolbar idiom.

## Tests
- **Headless (`DataSourceLibraryViewModel`):** point `LoadFileAsync` at a temp `results/<key>/x.npy` written via
  `DataSetExporter` → entry loads, `Data` cube present, not broken. (Confirms the load path; the picker itself
  is view code.)
- Manual gate below covers the picker.

## Gate
Build 0W/0E. Manual: run an HB sweep that writes `results/<key>/<analysis>.npy`; in a Data Display click "Load
Run Results…", which opens at the workspace `results/` folder; pick the schematic's folder → its `.npy`(s)
appear as data sources; a cube trace (7.3a axis-role picker) can then plot from them. Picking a *different*
schematic's `results/<otherKey>/` folder loads that one instead.

## On completion
Note in `src/Ui/CLAUDE.md`: Data Display can load a schematic's run results via "Load Run Results…" (folder
picker scoped to `<workspaceRoot>/results/`, injected as `GetResultsRootAction`); it loads every `.npy` in the
chosen `results/<schematicKey>/` folder through the existing `LoadFileAsync`. A richer results-browser flyout
(list of schematic-key folders) is a noted follow-up.
