# Phase 6g — Workspace Step 2: filesystem scan → Project Tree MODEL (Claude Code / Sonnet)

Build the **scan that turns a workspace folder into an in-memory tree model** — still **no view**. Walk the
workspace directory into a tree of typed nodes (workspace → cells / libraries / user-folders → view files /
other files), resolving each cell's primacy via `CellFolder.ResolvePrimary`, classifying every node, and
recording broken-reference states **as data** (so the view in step 3 can render them). **This brief is ONLY
step 2** — the model. No tree UI, no context menus, no double-click, no cell-reference model, no editor.
Framework-free, headless-testable. Read `workspace-and-project-tree.md` §1/§2/§3.1/§3.2 first. Sub-gated;
**report and stop between every layer.** Firewall green.

> Read first: `docs/design/workspace-and-project-tree.md` §1 (on-disk structure), §2 (primacy),
> §3.1 (structure shown), §3.2 (visual states — but here as model *data*, not rendering). Context code (from
> step 1): `src/Ui/Schematic/CellFolder.cs` (`ResolvePrimary`, `PrimaryResolution`, `PrimaryState`,
> `ViewType`, sub-folder/extension helpers — **reuse, don't reimplement primacy**), `src/Ui/Schematic/
> CellPersistence.cs` (`CcellFile` — IsTestBench, parameters), `src/Ui/Schematic/WorkspacePersistence.cs` +
> `CwsFile` (the `.cws` — referenced libraries, Known Files), `src/Ui/Schematic/NameValidator.cs`. Design docs
> win on any conflict.

## The spine (do not violate)
- **The filesystem is truth.** The scan reads the folder structure; it does not consult a membership list.
  Membership = what's on disk.
- **Model only, framework-free.** The tree-node model + scanner live in `src/Ui/Schematic/` (or a new
  `src/Ui/Workspace/` framework-free folder), carry no Avalonia/Skia, and are unit-testable headless by
  pointing the scanner at a temp directory tree.
- **Reuse step 1.** Cell primacy comes from `CellFolder.ResolvePrimary` — do NOT re-derive it. `.ccell` is
  read via `CellPersistence`; `.cws` via `WorkspacePersistence`.
- **Broken/warning states are DATA, not rendering.** The model records *why* a node is a warning (broken
  library path, broken Known File, missing-named-primary contradiction) so step 3 renders System.Warning; the
  scanner does not render anything.
- **Scope fence (step 2):** scan → model. NO view, NO context menus, NO double-click, NO filtering UI (the
  model carries enough category info to filter later, but the toggle UI is step 3), NO cell-reference model,
  NO editor.

---

## LAYER 1 — the tree-node model

Define a framework-free node model (records/classes) representing the scanned workspace. A single
`ProjectTreeNode` with a `Kind` discriminator is fine, or a small type hierarchy — pick the cleaner; it must
capture:
- **Node kind** (the category, used by the step-3 filter): `Workspace`, `Cell`, `Library`, `CellViewFolder`
  (the schematic/symbol/layout sub-folder grouping), `ViewFile` (a `.csch`/`.csym`/`.clay`), `UserFolder`
  (arbitrary workspace folder), `DataDisplayFile` (`.cdd`), `ColorThemeFile` (`.ccolor`), `OtherFile`,
  `KnownFile`, `KnownFilesGroup`, `LibrariesGroup` — enough to express the §3.3 filter categories (Cells,
  Libraries, TestBenches, Data Displays, Color Themes, Known Files, Workspace File-System).
- **Display name**, **absolute path**, and **relative path** (relative to the workspace root — for tooltips).
- **Children** (ordered).
- **Per-node state flags** the view needs: `IsPrimary` (a view file that resolved primary), `IsTestBench` (a
  cell whose `.ccell` says so), and a **`WarningState`** (none / reason) capturing the broken-reference cases
  with a human-readable reason string:
  - library path unresolved,
  - Known File path unresolved,
  - cell with a **MissingNamedPrimary** contradiction (carry which view type + missing filename for the
    tooltip).
- (`Id`/runtime identity not persisted anywhere — this is a transient model rebuilt by each scan.)

**Layer 1 gate:** the node model compiles, framework-free; a unit test constructs a small tree by hand and
reads back kind/name/paths/flags. Report.

---

## LAYER 2 — the workspace scanner

A `WorkspaceScanner` (framework-free) with `ProjectTreeNode Scan(string workspaceRootDir)` that walks the
structure per `workspace-and-project-tree.md` §1/§3.1:
1. **Workspace root** → the root node (name = root folder name). Read the `.cws` (via `WorkspacePersistence`)
   for referenced libraries + Known Files (used in steps 4–5 below). A missing/corrupt `.cws` is tolerated
   (scan the folder anyway; the tree still shows cells).
2. **Cells:** each immediate sub-folder containing a `.ccell` is a **Cell** node. Under it, a
   **CellViewFolder** node per non-empty view sub-folder (`schematic/`/`symbol/`/`layout/`), each containing
   **ViewFile** children. **Empty view sub-folders produce no node** (§3.1 — no empty expanders). Use
   `CellFolder.ResolvePrimary(cellFolder, viewType)` to mark the primary ViewFile (`IsPrimary`) and to detect
   the **MissingNamedPrimary** contradiction → set the Cell node's `WarningState` with a reason like
   "Primary symbol reference broken: amp.csym not found." Read `IsTestBench` from the `.ccell`.
3. **Arbitrary user folders / files:** sub-folders that are **not** cells (no `.ccell`) are **UserFolder**
   nodes; scan them recursively, classifying files by extension — `.cdd` → DataDisplayFile, `.ccolor` →
   ColorThemeFile, else OtherFile. (This realizes "the tree surfaces these by extension," §1.1/§3.1.)
4. **Referenced libraries:** for each library path in the `.cws`, resolve it; if it resolves, scan it as a
   **Library** node (a folder of cell folders — same cell logic as #2, recursively). If it **does not
   resolve**, emit a Library node with `WarningState` = "library path unresolved: <path>".
5. **Known Files:** a **KnownFilesGroup** with a **KnownFile** child per `.cws` Known File path; each that
   does not resolve gets `WarningState` = "file not found: <path>".

**Determinism:** stable ordering (e.g. alphabetical within each level) so scans are reproducible and testable.
(User-customizable ordering is a step-3 view concern; the model's default order is just stable.)

**Layer 2 gate:** point `Scan` at a hand-built temp workspace tree (cells with 1 / multiple / missing-primary
views, an empty view sub-folder, a user folder with a `.cdd` and `.ccolor`, a `.cws` with one resolvable and
one broken library + one broken Known File) → assert the returned tree has the right node kinds, the right
primary marks, `IsTestBench`, and the right `WarningState` reasons. Report.

---

## LAYER 3 — refresh contract (manual + on-focus, model side)

The scan is the whole refresh mechanism for v1: **re-running `Scan` rebuilds the model.** Provide a clean
entry the step-3 view will call on focus / on a Refresh command — no `FileSystemWatcher` (deferred, §9).
- Ensure `Scan` is **idempotent and cheap enough** to re-run on focus (it's a directory walk; fine for v1).
- If useful, a tiny `WorkspaceModel` wrapper holding the root node + the workspace root path + a `Rescan()`
  that returns a fresh tree — so the view binds to one object and calls `Rescan()`. (Optional; the static
  `Scan` may be enough.)

**Layer 3 gate:** re-running the scan after a temp-tree change (add a cell, delete a primary file) reflects
the change in the new model (the contradiction appears/disappears appropriately). Report.

---

## Acceptance (step 2)
1. A framework-free tree-node model captures kind (covering all §3.3 filter categories), name, absolute +
   relative paths, ordered children, and per-node flags (`IsPrimary`, `IsTestBench`, `WarningState` + reason).
2. `WorkspaceScanner.Scan` walks a workspace folder into that model: cells (via `.ccell`), view folders/files
   with primacy from `CellFolder.ResolvePrimary` (including the MissingNamedPrimary contradiction as a warning
   reason), arbitrary user folders/files by extension, referenced libraries (broken → warning), and Known
   Files (broken → warning). Empty view sub-folders produce no node. Stable ordering.
3. Re-running the scan reflects filesystem changes (the refresh contract).
4. `dotnet build`/`dotnet test` green; firewall green (all framework-free); **no tree view, no context menus,
   no double-click, no filter UI, no cell-reference model, no editor** (steps 3+); nothing else regresses.

## Guardrails
- **Filesystem is truth; reuse step 1** — primacy from `CellFolder.ResolvePrimary`, `.ccell` via
  `CellPersistence`, `.cws` via `WorkspacePersistence`. Don't re-derive primacy or invent a membership list.
- **Warning states are DATA + reasons**, not rendering — the MissingNamedPrimary contradiction, broken library
  paths, and broken Known Files are recorded so step 3 can show System.Warning; keep MissingNamedPrimary
  distinct from "no primary" (it already is, in `PrimaryState`).
- **Model only, framework-free, headless-testable** — point the scanner at a temp dir; no Avalonia/Skia.
- **Scope fence:** scan → model. No view, menus, double-click, filter UI, reference model, or editor.
- Sub-gate the three layers; report and stop between each; don't run the full suite into the output limit.
- Update `workspace-and-project-tree.md` §8 status (step 2 done) and note in `src/Ui/CLAUDE.md` that the
  workspace scan + tree-node model exist and are the (framework-free) source the Project Tree view binds to.

*Exit: a framework-free scanner turns a workspace folder into a typed, ordered tree model — cells, libraries,
user files, with primacy and broken-reference states recorded as data — ready for the Project Tree view
(step 3) to bind and render, with no GUI yet.*
