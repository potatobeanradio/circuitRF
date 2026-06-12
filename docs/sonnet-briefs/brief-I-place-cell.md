# Brief I — Place a Cell in the Schematic (drag-drop from Project Tree)

**Scope:** let the user drag a **cell** from the Project Tree and drop it into a schematic to
instantiate it as a component (CellRef), named `X1/X2/…`, with default parameters, rendered by the
cell's primary symbol. Interacting with the placed cell is identical to a palette component, plus a
(deferred) "Push Into" menu. **The model/render/connectivity/resolution layer already supports
CellRef components — do NOT rebuild it.** This brief is the placement UX on top of it.

**Prereq:** Briefs F/G landed (Pin/Term, cell ports). **Out of scope here:** the auto-generate-symbol
prompt for symbol-less cells — that's **Brief J**; this brief handles the case where the cell HAS a
resolvable symbol, and for a symbol-less cell falls back to the existing placeholder render (Brief J
adds the prompt).

**Firewall:** UI layer; `CellSymbolResolver`/`CellFolder`/`EditableSchematic` are framework-free —
keep them so.

---

## What already exists (READ — do not rebuild)

- `src/Ui/Schematic/EditableSchematic.cs`:
  - `EditableComponent.CellRef` (`string?`, relative path from the schematic's dir to the cell
    folder). When non-null, the **cell-reference render path** is used.
  - `ToRenderComponent(isPointConnected, cellRefResolution)` already renders a CellRef component:
    Resolved → resolved symbol's pins + primitives; NotFound/PrimaryMissing → placeholder glyph, no
    pins. **Ports render with connection state (red square unconnected) already.**
  - `SchematicEditModel.SchematicDirectory` (base dir for resolving CellRef); `BuildRenderModel`
    → `ResolveAllCellRefs()` resolves every CellRef via `CellSymbolResolver`; connectivity pass
    already uses resolved cell pins.
  - `NextAvailableName(existing, prefix)` — instance naming (use prefix `"X"`).
- `src/Ui/Schematic/CellSymbolResolver.cs` — `Resolve(cellRef, baseDir)` → `Resolved` /
  `NotFound` / `PrimaryMissing`; `Invalidate(cellAbsDir)` / `InvalidateAll()`.
- `src/Ui/Schematic/CellFolder.cs` — `ResolvePrimary(cellDir, ViewType.Symbol)` five-branch:
  `SoleFile` (one .csym → used regardless of .ccell ✓ user's fallback), `NamedPresent`,
  `MissingNamedPrimary`, `NoPrimary`, `NoView` (no .csym at all → Brief J's auto-gen case).
- `src/Ui/Commands/Schematic/PlaceComponentCommand.cs` — generic; adds/removes an
  `EditableComponent`. No SymbolKind special-casing → works for cells as-is.
- `src/Ui/Controls/SchematicCanvas.cs` — palette DnD: `OnPaletteDragOver`/`OnPaletteDrop` parse
  `PaletteDragPayload` (prefixed text) and call **`_editContext.CommitPlacement(kind, portCount,
  rotation, wx, wy)`**; a placement **ghost** is shown via `Overlay.Ghost = new PlacementGhost(...)`.
  Image-file handlers run AFTER palette (palette pre-marks `e.Handled`).
- `src/Ui/Schematic/PaletteDragPayload.cs` — the prefixed-text payload pattern to mirror
  (`circuitrf-palette:Kind:PortCount`, `Serialize`/`TryParse`). **Native pasteboard requires a
  prefixed TEXT payload — do not use an in-process object format (macOS NSPasteboard crash).**
- `src/Ui/Views/ProjectTree/ProjectTreeView.axaml.cs` — has drop-RECEIVE (Known Files) and
  double-tap-open, but **no drag SOURCE** yet. `ProjectTreeNodeViewModel` (`Kind`, `AbsolutePath`);
  a cell node is `NodeKind.Cell`.
- `SchematicViewModel.CommitPlacement(SymbolKind, int portCount, SymbolRotation, double wx, double
  wy)` — **the method to mirror**: it builds an `EditableComponent` (seeds `DefaultParameters`, sets
  `InstanceName` via `NextAvailableName`, flags) and runs `PlaceComponentCommand`. (Grep it.)
- `src/Ui/Schematic/CellPersistence.cs` — `CcellFile.Parameters` (`List<CcellParameter>` Name +
  default expr + unit) → the cell's default parameters to seed onto the instance.

---

## Spine (do-not-violate)

1. A placed cell is a normal `EditableComponent` with `CellRef` set — so it inherits ALL component
   interactions (select/move/F5/keystroke-move/copy-paste/inline-edit/double-click param editor)
   for free. Do not fork a parallel "cell object" type.
2. `CellRef` is the **relative path** from the schematic's directory to the cell folder
   (`Path.GetRelativePath(SchematicDirectory, cellAbsDir)`), matching the existing resolver contract.
3. Instance name prefix is **`X`** (`X1, X2, …` via `NextAvailableName(existing, "X")`).
4. Default parameters come from the cell's `.ccell` (`CcellFile.Parameters`), not the registry.
5. The native drag payload is **prefixed text** (mirror `PaletteDragPayload`).
6. Placement is one undoable `PlaceComponentCommand`.

---

## Layer 1 — Cell drag SOURCE on the Project Tree

In `ProjectTreeView.axaml.cs`, start a system drag when the user drags a **cell** node:
- Add `PointerPressed`/`PointerMoved` (or use Avalonia's drag threshold) handlers; when the pressed
  item is a `ProjectTreeNodeViewModel` with `Kind == NodeKind.Cell`, begin a drag once past the
  threshold: build a `DataTransfer`/`DataObject` containing `DataFormat.Text` = a new
  `CellDragPayload(cellAbsPath).Serialize()`, and call `DragDrop.DoDragDrop(triggerEvent, data,
  DragDropEffects.Copy)`.
- Only cells are draggable — schematic/symbol/known-file nodes are NOT (the user drags a **cell**,
  not a `.csch`). Guard on `Kind == Cell`.
- Don't break the existing double-tap-open or drop-receive handlers.

Add `src/Ui/Schematic/CellDragPayload.cs` mirroring `PaletteDragPayload`:
`circuitrf-cell:<absolute-cell-folder-path>`, with `Serialize()` and
`TryParse(string?, out CellDragPayload)` (prefix-guarded; reject foreign text).

**Gate 1:** Dragging a cell node produces a drag with the prefixed-text payload; dragging a
schematic/symbol/known-file node does not start a cell drag.

---

## Layer 2 — Schematic drop target accepts cells (ghost + drop)

In `SchematicCanvas.cs`, add cell-payload handling **alongside** the palette handlers (register
before the image-file handlers so a cell drop is consumed first):
- `OnCellDragOver`: if a `DataFormat.Text` item `CellDragPayload.TryParse`s, set
  `DragEffects = Copy`, `e.Handled = true`, and show a placement **ghost** at the snapped cursor.
  The existing `PlacementGhost` is `SymbolKind`-based; for a cell, show a generic box ghost (add a
  cell-aware ghost variant, or reuse a neutral box ghost sized to the resolved symbol bbox if
  cheap). A simple snapped-box ghost is acceptable for v1 — state what you did.
- `OnCellDrop`: parse the payload, compute `wx,wy`, and call a new VM entry point
  **`_editContext.CommitCellPlacement(cellAbsPath, wx, wy, rotation)`** (Layer 3). Clear the ghost.
- Ensure the cell handlers and palette handlers don't double-fire (palette parses
  `circuitrf-palette:`, cell parses `circuitrf-cell:` — mutually exclusive prefixes).

**Gate 2:** Dragging a cell over a schematic shows a ghost following the cursor; dropping calls
`CommitCellPlacement`. Palette drops and image drops still work.

---

## Layer 3 — `CommitCellPlacement` (build the cell instance)

Add `SchematicViewModel.CommitCellPlacement(string cellAbsDir, double wx, double wy, SymbolRotation
rotation)`, mirroring `CommitPlacement`:
1. **Require a saved schematic:** if `EditModel.SchematicDirectory is null`, the relative CellRef
   can't be computed and the resolver can't run → show a message ("Save the schematic before
   placing a cell") and abort. (State if you instead choose to allow & store absolute — not
   recommended; the resolver is relative-based.)
2. Compute `cellRef = Path.GetRelativePath(EditModel.SchematicDirectory, cellAbsDir)`.
3. **Resolve the symbol now:** `CellSymbolResolver.Resolve(cellRef, SchematicDirectory)`.
   - `Resolved` or any state with a symbol → proceed.
   - `PrimaryMissing` **because NoView** (no .csym at all) → **Brief J** handles the auto-gen prompt;
     for THIS brief, proceed and let the existing placeholder glyph render (note the seam clearly so
     Brief J slots in here).
   - `PrimaryMissing` because `NoPrimary`/`MissingNamedPrimary` (symbols exist but ambiguous/
     contradicted) → place with the placeholder and show a warning toast ("`{cell}` has no primary
     symbol selected" / "named primary missing") — do NOT auto-generate (symbols already exist).
4. Build the `EditableComponent`:
   - `CellRef = cellRef`; `Symbol =` a neutral placeholder kind (e.g. `SymbolKind.Generic`) — unused
     for rendering when CellRef resolves, but required by the non-null field.
   - `InstanceName = NextAvailableName(EditModel.Components, "X")`.
   - `X,Y =` snapped `wx,wy`; `Rotation = rotation`.
   - **Seed parameters** from the cell's `.ccell`: load `CcellFile` via `CellPersistence.LoadFromFile
     (Path.Combine(cellAbsDir, CellFolder.CcellFileName))`, and for each `CcellParameter` add an
     `EditableParameter { Name, Expression = default, Unit, ShowOnSchematic = true, Dimension }`.
   - Label flags: `ShowTypeLabel = true` (cell name — Layer 4), `ShowInstanceName = true`.
5. Run `PlaceComponentCommand(EditModel, comp, onChanged)` through the document's undo stack.

**Gate 3:** Dropping a cell that has a primary symbol places an `X{n}` instance at the cursor, drawn
with the cell's symbol and pins (red squares when unconnected), with the cell's default parameters.
Undo removes it; redo restores it. Selecting/moving/F5/copy-paste/inline-edit/double-click-param
all behave exactly like a palette component.

---

## Layer 4 — Cell name as the type label

`ToRenderComponent` currently sets the type label to `ComponentTypeRegistry.DisplayName(Symbol)` —
wrong for a cell (Symbol is a placeholder). Make the type label the **cell name** when `CellRef`
is set:
- In the labels block, when `CellRef is not null`, use the cell folder name
  (`Path.GetFileName(CellRef.TrimEnd('/','\\'))`) as `labels[0]` instead of `DisplayName(Symbol)`.
  Derive it (single source of truth = CellRef) — do NOT add a separate persisted CellName that could
  drift. (If a non-path-derived display is ever needed, revisit; v1 derives.)
- The inline label editor, F5 move, and double-click param editor already operate on the labels/
  parameters list, so they work unchanged once `labels[0]` is the cell name.

**Gate 4:** A placed cell shows its **cell name** as the type label (not "Generic"); the instance
name `X{n}` shows below it; shown parameters render as usual and are inline-editable.

---

## Layer 5 — "Push Into" context-menu item (deferred operation)

A cell instance's context menu gets a **"Push Into"** item (the only thing distinguishing a cell
from a library component). The operation is **deferred** — wire the menu item but make it a no-op
(disabled, or enabled-but-shows "coming soon"). Find the schematic component context menu (grep the
schematic view/`WorkspaceWindow.axaml` / the menu built around `ContextMenuTargetId`), and show
"Push Into" only when the right-clicked component has `CellRef != null` (bind to an `IsCell`
predicate on the component/VM). Do not implement push-in navigation.

**Gate 5:** Right-clicking a placed cell shows "Push Into" (disabled/no-op); right-clicking a
library component does not show it. All other context-menu actions are unchanged.

---

## Layer 6 — Re-render when the cell changes (verify/extend E2 path)

When the cell's **primary symbol** changes (Make-Primary, .csym save) or its **parameters** change,
open schematics that reference the cell must re-render. E2 already wired primary/port changes to
`CellSymbolResolver.Invalidate` + "rebuild open schematics". Verify this path also rebuilds
schematics that contain **CellRef components** (it should, since `BuildRenderModel` re-resolves), and
extend it if a gap exists:
- On `.csym` save in the Symbol Editor → `CellSymbolResolver.InvalidateAll()` (or `Invalidate(cellDir)`)
  → every open schematic VM rebuilds its render model.
- On `.ccell` primary/param change → same.
- Confirm a placed cell visibly updates (new symbol art / new pins) without reopening the schematic.

**Gate 6:** With a schematic open showing a placed cell, changing the cell's primary symbol (or
editing its symbol and saving) updates the placed instance live.

---

## Acceptance
- Drag a cell from the tree → drop in a schematic → `X{n}` instance with the cell's symbol, pins,
  and default params. ✅
- Interactions identical to a palette component; "Push Into" present (deferred) only for cells. ✅
- Cell name is the type label; pins show connection state. ✅
- Placement is undoable; placing into an unsaved schematic is handled (prompt to save). ✅
- Changing the cell's primary symbol/params re-renders open instances. ✅
- (Symbol-less cell uses the placeholder for now; Brief J adds the auto-gen prompt at the marked seam.) ✅

## Guardrails
- Reuse `EditableComponent`/`PlaceComponentCommand`/`CommitPlacement` patterns — no parallel cell type.
- `CellRef` is relative; prefixed-text drag payload only.
- Derive the cell label from `CellRef` (no drift-prone duplicate field).
- Don't implement Push-Into; don't implement auto-gen (Brief J).
- Keep `CellSymbolResolver`/`CellFolder`/`EditableSchematic` framework-free. Minimal diff; list files.

## Scope fence (NOT here)
- Auto-generate symbol prompt/generator (Brief J). Push-Into navigation (future).
- No new placement mechanisms beyond drag-drop.

## Exit / report
State: the `CellDragPayload` format; the tree drag-source trigger; `CommitCellPlacement` signature +
how it seeds params and resolves the symbol; how the cell label is derived; where "Push Into" is
gated; and how re-render on cell change is ensured. Confirm the 6 gates run mentally and note the
exact seam where Brief J's auto-gen prompt will slot in.
