# Sonnet Brief — Phase 7.1f: Data Display ↔ workspace & Project Tree integration

**Design:** `docs/design/data-display.md` (add a 7.1f bullet) · `docs/design/project-file-formats.md` (`.cdd`/`.cws`).
**This is NOT 7.1d-2** (that's the Properties-dock inspector surface). Scope: make `.cdd` documents first-class
workspace citizens — an Open menu item, `.cws` open-doc persistence, Project-Tree discovery, and double-click
open. Most plumbing already exists; this is additive. No `PlotControl` / inspector changes.

## Context — what already exists (reuse, don't rebuild)
`WorkspaceViewModel` (`src/Ui/ViewModels/WorkspaceViewModel.cs`):
- `OpenDataDisplayFromFileAsync(string path, Stream stream)` — opens a `.cdd` as a **new deduped Content-pane
  tab**: checks `_openDocsByPath`, creates `DataDisplayDocument(title, vm, filePath: absPath)`, injects
  `SetOpenFileAsNewDisplayAction`, `OpenDocument`, `await Window.LoadAllAsync(absPath, stream)` (which does the
  `format_version` reject), then `Materialize`. **Reuse this.**
- `NewDataDisplay()` — scratch display (tracked in `_scratchDataDisplays`, no path; never persisted).
- `WriteWorkspaceFile(...)` persists open docs to `CwsFile.OpenDocuments` (a `List<CwsOpenDocument>` with a
  free-string `Kind`: currently `"schematic"/"symbol"/"cell"`) + `ActiveDocumentPath`; `RestoreOpenDocuments`
  re-opens them. **`DataDisplayDocument` is not yet handled in either.**
- `OpenNode(node)` dispatches double-click; `DataDisplayFile` currently falls into the `default` no-op.
`WorkspaceScanner.Scan` already classifies `.cdd → NodeKind.DataDisplayFile` **inside user sub-folders**
(`BuildFileNode`), but the **root `Scan` loop enumerates only sub-directories — not loose files at the
workspace root**, so a root-level `.cdd` doesn't appear. `DataDisplayDocument` has `FilePath` / `IsScratch` /
`Materialize`. `CwsOpenDocument.Kind` is a free string (no model change needed).

## 1. App-level "Open Data Display…" command + menu item
- Add `[RelayCommand] private async Task OpenDataDisplayFile(Window? owner)` to `WorkspaceViewModel`, mirroring
  the existing `OpenSymbolFile`: resolve owner, `StorageProvider.OpenFilePickerAsync` (single, filter
  `circuitRF Data Display *.cdd` + All Files); then
  `await using var s = await result[0].OpenReadAsync(); await OpenDataDisplayFromFileAsync(result[0].Path.LocalPath, s);`.
  Wrap in try/catch and surface `InvalidDataException` (version mismatch) / IO errors via `Messages.Error`.
- Add the menu item in `Views/WorkspaceWindow.axaml`: an **"Open Data Display…"** `NativeMenuItem` in the
  **File** submenu bound to `OpenDataDisplayFileCommand`, placed next to the existing "Open Symbol…" /
  "New Data Display" items. If those items also have in-window `MenuItem` equivalents (Windows/Linux menu),
  add it there too. (No NativeMenu code-behind change needed — it's a static, always-enabled item.)

## 2. `.cws` persists + restores open `.cdd` documents
In `WorkspaceViewModel.WriteWorkspaceFile`:
- **Open-docs loop** — add a branch alongside schematic/symbol/cell:
  `else if (dockable is DataDisplayDocument dd && dd.FilePath is not null) { docPath = dd.FilePath; kind = "datadisplay"; }`.
- **Active-document block** — add the matching `DataDisplayDocument` case so a `.cdd` tab can be the restored
  active tab.
In `RestoreOpenDocuments` switch — add:
  `case "datadisplay" when File.Exists(absPath): OpenOrActivateDataDisplay(absPath); break;`.
Add a **path-based open helper** `OpenOrActivateDataDisplay(string absPath)` shared by restore (§2) and
double-click (§4): dedup via `_openDocsByPath` (activate if open); else create the doc + inject
`SetOpenFileAsNewDisplayAction`, `OpenDocument`, register in `_openDocsByPath`, then load via
`Window.LoadAllAsync(absPath)` (**null stream → reads the file itself**) and `Materialize`. Cleanest: refactor
`OpenDataDisplayFromFileAsync` to delegate to this helper (stream vs path the only difference). The load is
async; in the sync restore path use an async local that awaits (or fire-and-forget with a try/catch that routes
errors to `Messages`) so a bad/old `.cdd` surfaces its version error and doesn't break workspace open.
- Scratch displays (`_scratchDataDisplays`, no path) are **never** persisted — already correct.
- No `.cws` `format_version` bump (adding a `Kind` value is additive within v2).

## 3. Project Tree shows `.cdd` files anywhere in the workspace
In `WorkspaceScanner.Scan`, after building the cell/user-folder sub-dir children, **also enumerate loose files
at the workspace root** via the existing `BuildFileNode`, **excluding the `.cws` file** (`CwsFileName`). This
surfaces root-level `.cdd` (→ `DataDisplayFile`) and other files; sub-folder `.cdd` already appears. Keep the
alphabetical ordering convention. (This also surfaces root-level `netlist.cnl` etc. as `OtherFile` — the tree's
existing category filters, incl. the `WorkspaceFileSystem` / `DataDisplays` toggles in `ProjectTreeFilterState`
/ `CwsTreeViewState`, gate visibility; acceptable. If the owner dislikes root clutter, the filter handles it.)
Verify `DataDisplayFile` nodes honor the **DataDisplays** filter toggle (likely already wired — just confirm).

## 4. Double-click a `.cdd` in the tree → open in the Content pane
In `WorkspaceViewModel.OpenNode`, add:
```
case NodeKind.DataDisplayFile:
    OpenOrActivateDataDisplay(node.AbsolutePath);
    return;
```
So a tree `.cdd` opens (or activates) just like `.csch`/`.csym` do via `OpenOrActivateSchematic`/`Symbol`.

## Out of scope (note, don't implement)
- Dirty-close prompt for Data Displays (`HasAnyDirtyWork` / `PromptSaveBeforeClose` don't yet include
  `DataDisplayDocument`) — a separate follow-on; leave as-is here.
- The Data Display's own in-document Save/Open Display toolbar (already shipped in 7.1e).

## Gate (acceptance)
1. **File → Open Data Display…** opens a `.cdd` into a Content-pane tab (deduped; re-opening activates the
   existing tab); a version-mismatch `.cdd` shows a clear error.
2. Saving a `.cdd` inside the workspace folder (root **or** a sub-folder) makes it appear in the Project Tree
   **without** being a Known File; **double-clicking it opens it in the Content pane** like `.csch`/`.csym`.
3. With ≥1 `.cdd` tab open, **Save Workspace** then reopen the workspace → the same Data Display tabs reopen
   (correct tab order + active tab), each restoring its viewport (tab/zoom/pan via 7.1e).
4. Builds green; no regression to schematic/symbol/cell open-doc persistence or tree behavior.

## On completion
Add a **7.1f** bullet to `docs/design/data-display.md` (Data Display workspace/tree integration — Open menu,
`.cws` open-doc persistence, tree discovery, double-click open). Note "Phase 7.1f — COMPLETE" in
`src/Ui/CLAUDE.md`; report build + a screenshot of a `.cdd` in the tree and an opened-from-tree tab. After this,
back to **7.1d-2** (Properties-dock inspector) per the plan order.
