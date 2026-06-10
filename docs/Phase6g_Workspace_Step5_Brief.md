# Phase 6g — Workspace Step 5: New-Cell entry points + the Cell Reference Model (Claude Code / Sonnet)

Two things: **(A)** add node-free ways to create a cell (a **File → New Cell** menu item + a New-Cell
affordance on an **empty/rootless** Project Tree) so a fresh workspace is usable — fixing the chicken-and-egg
where New Cell only existed on an existing tree node; and **(B)** the **cell reference model** — the
architectural payoff: a placed schematic component resolves its glyph/pins through
`instance → relative path → .ccell → primary .csym` instead of the static `SymbolKind → BuiltInSymbols` path,
which **unblocks the deferred symbol-editor live-update and cell-driven open** (4c-later). Read
`workspace-and-project-tree.md` §4/§4.2 and `symbol-editor.md` §6 first. **(A) is small and lands first;
(B) is the large, careful part — sub-gate it tightly.** Report and stop between every layer. Firewall green.

> Read first: `docs/design/workspace-and-project-tree.md` §4 (cell reference model), §4.2 (the three
> missing-symbol states — keep distinct), §2 (primacy); `docs/design/symbol-editor.md` §6 (Option A positional
> + the committed Option B migration), §1 (ownership: cell owns primary). Context code:
> `src/Ui/ViewModels/WorkspaceViewModel.cs` (`ITreeActions.NewCellAsync(parentNode)` — generalize for the
> node-free case; `NewWorkspace` — today resets dock, does NOT create a folder; `CurrentWorkspacePath`;
> `_factory.ProjectTreeTool`), `src/Ui/Views/WorkspaceWindow.axaml` (the File menu — add New Cell, enabled
> only when a workspace is loaded), `src/Ui/Schematic/CellFolder.cs` (`CreateCellFolder`, `ResolvePrimary`,
> `SubFolderPath`, `ViewType`), `src/Ui/Schematic/CellPersistence.cs` (`CcellFile`), `src/Ui/Schematic/
> EditableSchematic.cs` (`EditableComponent.Symbol`/`SymbolKind`/`PortCount`/`GetPortWorldCoord`;
> `SymbolPortDefs.For`; `ComputeGlyphBb` reads `BuiltInSymbols.Primitives`), `src/Ui/Schematic/
> BuiltInSymbols.cs` (`Primitives(kind)` — the static path being supplemented), `src/Ui/Renderers/
> SchematicRenderer.cs` (`DrawSymbol`, `DrawSymbolLines` — the glyph render to feed from a resolved `.csym`),
> `src/Ui/Schematic/SymbolPersistence.cs` (`LoadFromFile`). Design docs win on any conflict.

## The spine (do not violate)
- **(B) is additive, not a rip-out.** Built-in components still resolve via `SymbolKind → BuiltInSymbols`
  (the standard library is code today). The **cell reference** is a *new* resolution path for components that
  reference a cell by relative path. A component resolves its glyph by: **if it has a cell reference → resolve
  via the cell chain; else → the existing `SymbolKind` path.** Don't break the built-in path.
- **Resolution chain (§4):** `instance.CellRef (relative path) → cell folder → .ccell (CellFolder.
  ResolvePrimary for Symbol) → primary .csym → SymbolPersistence.LoadFromFile → primitives + pins`. Reuse
  `CellFolder.ResolvePrimary` (the single primacy source) and `SymbolPersistence` — do NOT re-implement.
- **The three missing-symbol states stay distinct (§4.2):** (1) no symbol at first placement → prompt
  (existing); (2) **broken cell reference** (path unresolved) → **"Not Found" glyph**; (3) **primary
  contradiction** (cell resolves but `.ccell` names a missing `.csym`) → **plain-rectangle stand-in** + the
  tree's System.Warning. Three paths, not one.
- **Live update = the payoff:** when a cell's primary symbol changes (Make Primary, or an edit saved to the
  primary `.csym`), schematics showing instances of that cell **re-render**. Pins re-resolve from the new
  `.csym`; wires that no longer meet a pin show unconnected (positional model; no auto-rewire — Option B is
  still deferred, `symbol-editor.md` §6). **Risky op, surfaced not blocked.**
- **Firewall:** resolution/caching logic is framework-free (model layer); only the renderer (Skia) draws.
- **Scope fence (step 5):** entry points (A) + the cell reference resolution/render/live-update (B). NO
  cell-parameter editor (step 6), NO `.cws` Known-Files/view-state (step 7), NO Option B auto-rewire.

---

## LAYER 1 — New-Cell entry points (A): File menu + empty-tree affordance

Fix the chicken-and-egg: a freshly-created/empty workspace has no tree node to right-click.
1. **`NewWorkspace` creates a real workspace folder.** Today it only resets the dock. Make
   `New Workspace` prompt for a location/name (a folder + a `.cws` inside), create it on disk, set
   `CurrentWorkspacePath`, and point the tree at it (the existing `OnCurrentWorkspacePathChanged` →
   `SetWorkspace` path). A new workspace shows an empty-but-valid tree root. *(If a full
   save-location prompt is heavy, minimally: create the workspace folder + empty `.cws` and load it, so the
   tree has a root to add cells under.)*
2. **File → New Cell** menu item (`WorkspaceWindow.axaml`, both the macOS NativeMenu and the in-window Menu) →
   a `NewCellInWorkspaceCommand` that creates a cell **in the current workspace root** (reusing the
   `NewCellAsync` logic with the workspace root dir as parent). **Enabled only when a workspace is loaded;
   disabled/greyed otherwise** (bind `CanExecute` to "workspace loaded" — `CurrentWorkspacePath is not null` /
   the tool's `HasWorkspace`).
3. **Empty-tree affordance:** when the tree has a workspace root but no cells (or to make New Cell reachable
   without an existing node), ensure New Cell is reachable — e.g. a context-menu item on the **workspace root
   node** (which always exists once a workspace is loaded), and/or a small "New Cell" button in the tree
   header. Generalize `NewCellAsync` so it can take the **workspace root** as the parent (not only a clicked
   library/cell node).

**Layer 1 gate:** with no workspace, File → New Cell is greyed; New Workspace creates a loadable workspace
(empty tree root); File → New Cell (and the root-node/header affordance) then creates a cell that appears in
the tree. The owner can now create a first cell from empty. Report.

---

## LAYER 2 — the cell-reference data + resolver (B, framework-free)

1. **Component cell reference:** add an optional **`CellRef`** (relative path string) to `EditableComponent`
   (and its `.csch` persistence — nullable, `WhenWritingNull`; absent = a built-in `SymbolKind` component, the
   current case). A component is either *built-in* (`SymbolKind`, no `CellRef`) or *cell-referencing*
   (`CellRef` set).
2. **Resolver** (framework-free, e.g. `CellSymbolResolver`): given a `CellRef` + the workspace/`.csch` base
   dir, resolve the chain → a result that is one of: **Resolved**(Symbol primitives+pins from the primary
   `.csym`), **NotFound**(the path didn't resolve — state 2), or **PrimaryMissing**(cell resolved but its
   `.ccell` names an absent primary `.csym` — state 3). Reuse `CellFolder.ResolvePrimary(cellDir, ViewType.
   Symbol)` + `SymbolPersistence.LoadFromFile`. Map `PrimaryState.MissingNamedPrimary`/`NoView` appropriately
   to the three-state result.
3. **Caching + invalidation:** cache resolved symbols by (cellDir, primary filename, file mtime) so repeated
   renders don't re-read disk every frame; invalidate on Make-Primary / on a save to the primary `.csym` /
   on tree Refresh. Keep it simple and correct over clever.

**Layer 2 gate:** a headless test: build a temp cell (with a primary `.csym`), point a `CellRef` at it →
resolver returns Resolved with the right primitives/pins; delete the cell folder → NotFound; name a missing
primary in `.ccell` → PrimaryMissing. Report.

---

## LAYER 3 — render a cell-referencing instance (the three states on canvas)

In the schematic render path (`EditableComponent.ToRenderComponent` / `ComputeGlyphBb` / the renderer):
- **Resolved:** draw the resolved `.csym` primitives via `DrawSymbol` (the same generic path the built-ins
  use), and derive the instance's **pins/ports from the resolved `.csym` pins** (not `SymbolPortDefs`) so
  connectivity uses the cell's actual pins.
- **NotFound (state 2):** draw a distinct **"Not Found" glyph** (e.g. a box with a "?" / "Not Found" label in
  `System.Warning`), no pins (or zero ports) — the user must re-point.
- **PrimaryMissing (state 3):** draw the **plain-rectangle stand-in** (the existing `System.Warning` rectangle
  behavior), distinct from "Not Found".
- Built-in (`SymbolKind`, no `CellRef`) instances are unchanged (the existing path).
- Keep glyph-BB / spatial-index correct for each case (reuse `ComputeGlyphBb`, fed by the resolved primitives
  for Resolved; sensible default boxes for NotFound/PrimaryMissing).

**Layer 3 gate:** a schematic with a cell-referencing instance renders the cell's primary symbol; breaking the
reference shows "Not Found"; a primary-contradiction shows the rectangle; built-in components are visually
unchanged (parity). Report.

---

## LAYER 4 — live update on primary-symbol change (the payoff)

When the cell's primary symbol changes, instances re-render:
- **Make Primary** (step 4's action) and **saving the primary `.csym`** in the Symbol Editor → invalidate the
  resolver cache for that cell and **trigger a re-render** of any open schematic showing instances of it.
  (Mechanism: the schematic's render snapshot rebuilds — the same NotifyChanged→rebuild path edits already
  use — for affected instances; a simple "invalidate + rebuild open schematics" is fine for v1.)
- **Pin re-resolution + the risk (surfaced, not blocked):** the new primary `.csym` may have different pins;
  pins re-resolve from it, and wires that no longer meet a pin render **unconnected** (positional model). Show
  this honestly (dangling-wire indicators); do NOT auto-rewire (Option B deferred). Optionally a one-line
  Message noting the symbol changed and connections should be verified.
- **Cell-driven open (the other 4c-later item):** double-clicking a **cell-referencing instance** (or a
  suitable affordance) can now **open that cell's primary symbol** in the Symbol Editor — wire this if cheap
  (it reuses the resolver + the existing symbol-open path); else state it's deferred.

**Layer 4 gate:** edit a cell's primary symbol (Make Primary to a different `.csym`, or edit+save the primary)
→ an open schematic with an instance of that cell re-renders to the new symbol; pin-count changes show
dangling wires (not a crash, not auto-rewired). Report.

---

## Acceptance (step 5)
1. **(A)** New Workspace creates a loadable workspace; File → New Cell (greyed when no workspace) and a
   root-node/header affordance create a cell from an empty tree — the owner can author from scratch.
2. **(B)** A component may carry a `CellRef` (relative path), persisted in `.csch`; the resolver returns
   Resolved / NotFound / PrimaryMissing via `CellFolder.ResolvePrimary` + `SymbolPersistence`.
3. Cell-referencing instances render: Resolved → the primary `.csym` (pins from it); NotFound → "Not Found"
   glyph; PrimaryMissing → plain rectangle. Built-ins unchanged (parity).
4. Changing a cell's primary symbol live-re-renders open schematics; differing pin counts show unconnected
   wires (no auto-rewire); no crash.
5. `dotnet build`/`dotnet test` green; firewall green (resolver framework-free; Skia only in renderer); **no
   cell-parameter editor, no `.cws` view-state, no Option B auto-rewire** (steps 6/7 / deferred); built-in
   resolution and all prior phases unregressed.

## Guardrails
- **(B) is additive** — built-in `SymbolKind` resolution stays; cell-ref is a new path. Don't rip out
  `BuiltInSymbols`.
- **Reuse `CellFolder.ResolvePrimary` + `SymbolPersistence`** — one primacy source, one `.csym` loader; no
  re-implementation.
- **Three missing-symbol states stay distinct (§4.2)** — Resolved / NotFound / PrimaryMissing render
  differently; do not collapse NotFound and PrimaryMissing into one rectangle.
- **Live-update is surfaced, not blocked** — pin-count changes show dangling wires; no auto-rewire (Option B
  deferred, `symbol-editor.md` §6).
- **Cache + invalidate** resolved symbols; don't re-read disk every frame, but always reflect a primary change.
- **Scope fence:** entry points + cell reference model only. No cell-parameter editor, no `.cws` view-state.
- Sub-gate the four layers; report and stop between each; don't run the full suite into the output limit.
- Update `workspace-and-project-tree.md` §8 (step 5 done; live-update + cell-open delivered), `symbol-editor.md`
  §11 (4c-later live-update now delivered by workspace step 5), and `src/Ui/CLAUDE.md` (cell-ref resolution
  path, three-state rendering, live-update invalidation).

*Exit: a workspace can be created and populated with cells from empty (File → New Cell), and schematic
components can reference cells by relative path — resolving to the cell's primary symbol, showing distinct
glyphs for the broken/contradiction states, and live-re-rendering when the primary symbol changes — finally
delivering the symbol-editor live-update deferred back in 4c.*
