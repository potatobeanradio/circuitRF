# Phase 6g — Workspace Step 3: Project Tree VIEW (display + filter + refresh) (Claude Code / Sonnet)

The first **visible** workspace piece: a Project Tree **view** bound to the step-2 model
(`ProjectTreeNode` from `WorkspaceScanner.Scan`). Render the tree with disclosure, **bold primaries**,
**System.Warning + italics** for broken refs (with reason tooltips), the **category-toggle filter**, and
**manual + on-focus refresh**. **This brief is ONLY step 3 — display, filter, refresh.** Context menus
(New Cell / Make Primary / Reveal in Finder) and double-click actions (open schematic/symbol/cell-editor) are
**step 4** — do NOT build them here. Read `workspace-and-project-tree.md` §3.1/§3.2/§3.3 first. Sub-gated;
**report and stop between every layer.** Firewall green.

> Read first: `docs/design/workspace-and-project-tree.md` §3.1 (structure shown), §3.2 (visual states),
> §3.3 (the category-toggle filter). Context code (from step 2): `src/Ui/Schematic/ProjectTreeNode.cs`
> (`NodeKind`, `IsPrimary`, `IsTestBench`, `WarningReason`, `Children`, `RelativePath` — **the model the view
> binds to**), `src/Ui/Schematic/WorkspaceScanner.cs` (`Scan`), `src/Ui/Schematic/WorkspaceModel.cs`
> (`Rescan` — the refresh entry). Code to **replace**: `src/Ui/ViewModels/Dock/ProjectTreeTool.cs` (the 6b
> **stub** with hardcoded demo libraries + the 4-kind `ProjectTreeItemKind` enum) and `src/Ui/ViewModels/
> ProjectTree/ProjectTreeItemViewModel.cs` (the stub item VM) and their view; `src/Ui/ViewModels/
> WorkspaceViewModel.cs` (`OpenTreeItem`, `_factory.ProjectTreeTool`, `CurrentWorkspacePath`). Color roles:
> `src/Ui/Theming/` + `SchematicRenderTheme` (`System.Warning` role). Design docs win on any conflict.

## The spine (do not violate)
- **Bind to the real model.** The tree view's item VM wraps a `ProjectTreeNode` (or the tool builds a thin
  item-VM tree from the node tree). **Delete the 6b stub** (hardcoded demo libraries, the 4-kind
  `ProjectTreeItemKind`) — the tree now comes from `WorkspaceScanner.Scan` of the current workspace folder.
- **Display + filter + refresh only.** No actions (open/new/make-primary/reveal) — those are step 4. The
  item VM may have placeholder command stubs, but wire no behavior.
- **Visual states are driven by the model's data** (`IsPrimary`, `IsTestBench`, `WarningReason`) — the view
  does not recompute primacy or re-detect broken refs; it renders what the model already decided (step 2).
- **Refresh = re-scan.** Manual (a Refresh affordance) + on-focus (re-scan when the tree/workspace regains
  focus) — call `WorkspaceModel.Rescan` / `WorkspaceScanner.Scan` and rebind. No `FileSystemWatcher`.
- **Scope fence (step 3):** the tree renders, filters, and refreshes. NO context menus, NO double-click
  actions, NO cell-reference model, NO editor.

---

## LAYER 1 — replace the stub tool/item-VM with model-bound VMs

1. **Item VM:** a `ProjectTreeNodeViewModel` (in `ViewModels/ProjectTree/`) wrapping a `ProjectTreeNode`:
   exposes `Name`, `Kind`, `IsPrimary`, `IsTestBench`, `WarningReason`, `RelativePath` (for tooltip),
   `IsExpanded`, and a `Children` `ObservableCollection<ProjectTreeNodeViewModel>` built from the node's
   children. Computed display properties the view binds to: `IsWarning` (= `WarningReason is not null`),
   `FontWeight`/`IsBold` (= `IsPrimary`), `IsItalic` (= `IsWarning`).
2. **Tool:** rewrite `ProjectTreeTool` to hold the **root** `ProjectTreeNodeViewModel` (or an
   `ObservableCollection` of top-level VMs) built from a `WorkspaceScanner.Scan(currentWorkspaceDir)`, plus a
   `SetWorkspace(rootDir)` / `Refresh()` that re-scans and rebuilds the VM tree. **Delete** the hardcoded
   demo-library constructor and the `ProjectTreeItemKind` enum (the real `NodeKind` replaces it).
3. **Wire the workspace dir:** `WorkspaceViewModel` already tracks `CurrentWorkspacePath`; point the tool at
   the workspace **root directory** (the folder containing `.cws`). When no workspace is open, the tree is
   empty (or a quiet "No workspace open" placeholder).

**Layer 1 gate:** the tool builds a `ProjectTreeNodeViewModel` tree from `WorkspaceScanner.Scan` of a real
folder; the old stub (demo libraries, `ProjectTreeItemKind`) is gone; building compiles. A headless test maps
a scanned `ProjectTreeNode` tree → VM tree preserving kind/flags. Report.

---

## LAYER 2 — the tree view: disclosure + bold primaries + warning styling

The Project Tree view (`Views/ProjectTree/…`, replacing the stub view) — a `TreeView` bound to the tool's
root VM(s):
- **Disclosure** triangles for nodes with children; **no expander for empty** nodes (the model already omits
  empty view sub-folders — §3.1).
- **Icons per `NodeKind`** (cell / library / view-file / data-display / color-theme / folder / known-file) —
  use the existing Material.Icons set, sensible glyphs; keep it tasteful (frontend-design tokens).
- **Bold** for `IsPrimary` view files (§3.2).
- **System.Warning color + italics** for `IsWarning` nodes (broken library, broken Known File, missing-named-
  primary cell), with the **`WarningReason` as the tooltip** (§3.2). Use the theme's `System.Warning` role
  (not a literal color).
- **Tooltip** shows the node's `RelativePath` on file nodes (and the warning reason on warning nodes).
- A `TestBench` cell may get a subtle distinguishing affordance (e.g. a "runnable" glyph) — optional, light.

**Layer 2 gate:** the tree renders a real workspace folder: cells disclose their schematic/symbol/layout view
files, primaries are bold, a cell with a missing-named-primary shows System.Warning + italics + reason
tooltip, a broken library/Known File shows the same; empty view sub-folders show no expander. Report
(screenshot description).

---

## LAYER 3 — the category-toggle filter

Implement the §3.3 filter as a **set of independently-toggleable categories** (not a radio list):
**Cells · Libraries · TestBenches · Data Displays · Color Themes · Known Files · Workspace File-System**.
- A small filter affordance (a toolbar of toggles, a filter popup/menu, or a row of checkable chips above the
  tree — pick the cleanest for the dock region; match frontend-design).
- Filtering **hides nodes whose category is toggled off**, keeping ancestors needed to show visible
  descendants (a cell stays visible if any of its views is visible; a library stays if it has a visible cell).
  "TestBenches" filters cells to `IsTestBench`. "All categories on" = full tree.
- Map `NodeKind` → filter category (e.g. `ViewFile`/`CellViewFolder`/`Cell` → Cells; `Library`/
  `LibrariesGroup` → Libraries; `DataDisplayFile` → Data Displays; `ColorThemeFile` → Color Themes;
  `KnownFile`/`KnownFilesGroup` → Known Files; `UserFolder`/`OtherFile` → Workspace File-System; a `Cell` with
  `IsTestBench` also satisfies TestBenches).
- The active filter set persists in memory (and may be recorded to `.cws` tree-view-state later — step 7;
  not required here).

**Layer 3 gate:** toggling categories shows/hides the right nodes with ancestors preserved; "Only Cells",
"Cells + Libraries", "Only TestBench", and "All" all behave per §3.3. Report.

---

## LAYER 4 — refresh (manual + on-focus)

- **Manual:** a Refresh affordance (toolbar button / context on the root) → `tool.Refresh()` → re-scan +
  rebuild the VM tree, **preserving expand/filter state** where reasonable (e.g. keep expanded the nodes whose
  relative path is still present).
- **On-focus:** re-scan when the tree (or the workspace window) regains focus, so external Finder edits show
  up. Debounce/guard so it doesn't thrash.
- No `FileSystemWatcher` (deferred, §9) — document the intent in a comment.

**Layer 4 gate:** editing the workspace folder externally (add a cell, delete a primary file) and refreshing
(button or refocus) updates the tree — the new cell appears, the contradiction warning appears/clears.
Report.

---

## Acceptance (step 3)
1. The Project Tree binds to the real `WorkspaceScanner` model (the 6b stub with demo libraries +
   `ProjectTreeItemKind` is gone).
2. The tree renders disclosure, **bold primaries**, **System.Warning + italics + reason tooltips** for broken
   refs, relative-path tooltips, no expander for empty nodes — all driven by the model's data.
3. The category-toggle filter (§3.3) shows/hides nodes with ancestors preserved; the owner's enumerated cases
   are all reachable.
4. Manual + on-focus refresh re-scans and rebuilds, reflecting external filesystem changes.
5. `dotnet build`/`dotnet test` green; firewall green; **no context menus, no double-click actions, no
   cell-reference model, no editor** (steps 4+); nothing else regresses.

## Guardrails
- **Bind to the model; don't recompute** — `IsPrimary`/`IsTestBench`/`WarningReason` come from step 2; the
  view renders them, it doesn't re-derive primacy or re-detect broken refs.
- **Delete the 6b stub** — no hardcoded demo libraries, no `ProjectTreeItemKind`; the tree is the scanned
  workspace.
- **System.Warning via the theme role**, not a literal color (theming consistency).
- **Refresh = re-scan** (manual + on-focus), preserving expand/filter state where reasonable; no
  FileSystemWatcher.
- **Scope fence:** display + filter + refresh only — no context menus, double-click actions, reference model,
  or editor.
- Sub-gate the four layers; report and stop between each; don't run the full suite into the output limit.
- Update `workspace-and-project-tree.md` §8 status (step 3 done) and `src/Ui/CLAUDE.md` (the Project Tree
  binds to `WorkspaceScanner`; visual states from model data; category-toggle filter; manual+on-focus
  refresh).

*Exit: the Project Tree is a live view of the scanned workspace — disclosure, bold primaries, System.Warning
broken refs, the category filter, and manual+on-focus refresh — with actions (context menus, double-click)
still to come in step 4.*
