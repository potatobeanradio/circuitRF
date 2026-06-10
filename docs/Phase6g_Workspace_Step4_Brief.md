# Phase 6g — Workspace Step 4: Project Tree context menus + double-click actions (Claude Code / Sonnet)

Make the Project Tree **interactive**: right-click **context menus** (New Cell; New Schematic/Symbol/Layout;
Edit Parameters; Make Primary; Reveal in Finder/Explorer) and **double-click** actions (open schematic →
content tab; open symbol → Symbol Editor; cell → cell-parameter editor [deferred stub for now]; unviewable →
no-op). **This brief is ONLY step 4.** The **cell reference model** (instance→cell→primary resolution + live
update) is **step 5**; the **cell-parameter editor** is **step 6** — so "double-click a cell / Edit
Parameters" opens a **deferred placeholder** here, not the real editor. Read `workspace-and-project-tree.md`
§3.4/§3.5 first. Sub-gated; **report and stop between every layer.** Firewall green; creation actions
undoable where they mutate (filesystem creates are their own commit — see below).

> Read first: `docs/design/workspace-and-project-tree.md` §3.4 (context menus), §3.5 (double-click). Context
> code: `src/Ui/ViewModels/ProjectTree/ProjectTreeItemViewModel.cs` (`ProjectTreeNodeViewModel` — `Kind`,
> `AbsolutePath`, `RelativePath`, `Name`, `Children`), `src/Ui/ViewModels/Dock/ProjectTreeTool.cs`
> (`SelectedItem`, `RootItems`, `SetWorkspace`, `Refresh`, `FilterState`), `src/Ui/Schematic/CellFolder.cs`
> (`CreateCellFolder`, `SubFolderPath`, `ViewExtension`, `ViewType`), `src/Ui/Schematic/CellPersistence.cs`
> (`CcellFile` — to write `Make Primary` + read for the cell editor stub), `src/Ui/Schematic/NameValidator.cs`,
> `src/Ui/ViewModels/WorkspaceViewModel.cs` (`OpenSymbolFile` — the `.csym`→editor pattern to reuse;
> `SchematicDocument`/`FromRenderModel` — the schematic-open pattern; `_factory.OpenDocument`; how the tool is
> reached via `_factory.ProjectTreeTool`). Design docs win on any conflict.

## The spine (do not violate)
- **Wire actions against `ProjectTreeNode`/`ProjectTreeNodeViewModel`** (the real model) — there is no old
  `ProjectTreeItemKind` to switch on (deleted in step 3). Action availability keys off `NodeKind`.
- **Open/activate, don't duplicate:** opening a schematic/symbol that's already open **activates** the
  existing tab/window rather than opening a second (track by absolute path).
- **Filesystem creates re-scan the tree:** New Cell / New Schematic / New Symbol create files/folders on disk,
  then **trigger `ProjectTreeTool.Refresh()`** so the new node appears (filesystem-is-truth — the tree reflects
  disk, §intro). Validate names via `NameValidator` (reject invalid with a message).
- **Defer what later steps own:** double-click a **cell** and **Edit Parameters** open a **deferred
  placeholder** (the cell-parameter editor is step 6); **layout** actions are greyed (v2); unviewable file
  types (`.cdd`, etc.) are no-ops (no viewer yet). Do NOT build the cell-parameter editor or the cell
  reference model here.
- **Scope fence (step 4):** context menus + double-click wiring. NO cell-parameter editor, NO cell reference
  model / live update, NO layout editor.

---

## LAYER 1 — double-click actions

Wire the tree's double-click (the view raises it; route to a `WorkspaceViewModel` handler given the
double-clicked `ProjectTreeNodeViewModel`), per §3.5:
- **`ViewFile` of a schematic (`.csch`)** → open it in a workspace **content tab** (or activate if already
  open). Reuse the `SchematicDocument` open pattern. *(For now, loading a real `.csch` from disk into an edit
  model may reuse the existing schematic load path; if `.csch`-from-cell loading isn't wired yet, open the
  schematic document for that file path — wire the actual `.csch` deserialize if it exists, else a clear
  "not yet loadable" message. State which.)*
- **`ViewFile` of a symbol (`.csym`)** → open in the **Symbol Editor** (tab or window), or activate if open.
  **Reuse `OpenSymbolFile`'s body** (load `.csym` → `EditableSymbol` → `SymbolEditorDocument`), pointed at the
  node's `AbsolutePath`, `UserEditable=true` (a user `.csym`).
- **`Cell` node** → open the **cell-parameter editor** — **deferred placeholder** for now (step 6): open a
  stub tab / show a Message "Cell parameter editor: coming in step 6" (do not build the editor).
- **`ViewFile` of a layout (`.clay`)** → deferred (v2) no-op.
- **`DataDisplayFile` / `ColorThemeFile` / `OtherFile`** → attempt-to-open is a **no-op** for types with no
  viewer yet (no crash, no error — optionally a quiet Message).
- **Open/activate dedup:** track open documents by absolute path; a second double-click activates the
  existing tab/window.

**Layer 1 gate:** double-clicking a `.csym` opens/activates it in the Symbol Editor; a `.csch` opens/activates
a schematic tab (or a clear message if `.csch` load isn't wired); a cell shows the deferred cell-editor
placeholder; unviewable types no-op. Report which open paths are real vs. messaged.

---

## LAYER 2 — context menu: open/reveal/make-primary (view-file + general)

Add a context menu (right-click) on tree nodes, items shown per `NodeKind` (§3.4):
- **On a view file (`.csym`/`.csch`/`.clay`):**
  - **Make Primary** → write the node's filename into the cell's `.ccell` (the matching
    `PrimarySchematic`/`PrimarySymbol`/`PrimaryLayout` field) via `CellPersistence`, then `Refresh()` (the
    bold-primary re-resolves through `CellFolder.ResolvePrimary`). *(This is a filesystem `.ccell` edit; it is
    its own commit — not on a document undo stack. A confirmation Message is enough.)*
  - **Reveal in Finder/Explorer** → open the OS file manager pointing at the file (`AbsolutePath`). Use the
    platform reveal (macOS `open -R`, Windows `explorer /select,`, Linux `xdg-open` the parent) — a small
    cross-platform helper.
- **Reveal in Finder/Explorer** is available on file nodes generally (and folder nodes → open the folder).

**Layer 2 gate:** Make Primary on a non-primary `.csym` updates `.ccell` and the tree re-renders it bold (and
clears/keeps the contradiction warning correctly); Reveal opens the OS file manager at the file on this
platform. Report.

---

## LAYER 3 — context menu: creation actions (New Cell / New Schematic / New Symbol / New Layout)

Per §3.4 (note the placement rule: **New Cell on container nodes; New views on the cell node**):
- **On the workspace root or a library node:** **New Cell** → prompt for a name (validate via
  `NameValidator`), call `CellFolder.CreateCellFolder(containerDir, name)` (creates the folder + subfolders +
  initial `.ccell`), then `Refresh()`. The new cell appears.
- **On a cell node:** **New Schematic**, **New Symbol**, **New Layout**:
  - **New Schematic** → create a new empty `.csch` in the cell's `schematic/` (prompt for a view filename,
    validate), then open it for authoring (a schematic content tab) and `Refresh()`. *(If empty-`.csch`
    creation/serialize isn't available yet, create the file via the schematic persistence path if it exists,
    else open a new empty schematic document targeting that path and message that save will write it — state
    which.)*
  - **New Symbol** → create a new empty `.csym` in `symbol/` (prompt + validate), open it in the Symbol Editor
    (`EditableSymbol` fresh, `CurrentSymbolPath` = the new path, `UserEditable=true`), then `Refresh()`.
  - **New Layout** → **disabled/greyed** (v2).
- Name prompts reject invalid names (NameValidator) with a clear message; duplicate filenames are rejected or
  disambiguated.

**Layer 3 gate:** New Cell on the workspace/library node creates the folder structure and the cell appears in
the tree; New Symbol on a cell creates a `.csym` and opens it in the editor; New Schematic creates/opens a
schematic; New Layout is greyed. All via `NameValidator` + `Refresh()`. Report.

---

## Acceptance (step 4)
1. Double-click: `.csym` → Symbol Editor (open/activate); `.csch` → schematic tab (open/activate, or clear
   message if load unavailable); cell → deferred cell-editor placeholder; layout/unviewable → no-op.
2. Context menu (per `NodeKind`): **Make Primary** (writes `.ccell`, re-resolves bold + warning), **Reveal in
   Finder/Explorer** (cross-platform), **New Cell** (on workspace/library), **New Schematic/Symbol** (on cell;
   open the new view), **New Layout** greyed.
3. All creation/primary actions validate names via `NameValidator` and **`Refresh()`** the tree
   (filesystem-is-truth); open/activate dedups by path.
4. `dotnet build`/`dotnet test` green; firewall green; **no cell-parameter editor, no cell reference model /
   live update, no layout editor** (steps 5/6); nothing else regresses.

## Guardrails
- **Key off `NodeKind`**, the real model — no resurrected `ProjectTreeItemKind`.
- **Open/activate, never duplicate** — track by absolute path.
- **Creates re-scan** — filesystem-is-truth; after any create/Make-Primary, `Refresh()` so the tree matches
  disk. `Make Primary` is a `.ccell` edit (its own commit), not a document-undo operation.
- **Validate names** via `NameValidator`; reject invalid with a clear message.
- **Defer step-5/6 work:** cell double-click + Edit Parameters = placeholder (cell editor is step 6); no cell
  reference model / live update; layout greyed.
- Sub-gate the three layers; report and stop between each; don't run the full suite into the output limit.
- Update `workspace-and-project-tree.md` §8 status (step 4 done) and `src/Ui/CLAUDE.md` (tree actions:
  open/activate by path, creates re-scan, Make Primary writes `.ccell`).

*Exit: the Project Tree is interactive — open schematics/symbols by double-click, create cells and views and
make-primary via context menu, reveal in the OS file manager — with the cell reference model (step 5) and the
cell-parameter editor (step 6) still to come.*
