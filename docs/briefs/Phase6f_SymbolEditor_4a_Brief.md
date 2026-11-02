# Phase 6f — Symbol Editor 4a: scaffold + canvas + select/move/delete + undo (Claude Code / Sonnet)

Stand up the Symbol Editor: a window (dockable + tear-off) hosting a Skia canvas that renders a `Symbol`
(primitive model from steps 1–2) and lets the user **select, move, and delete** primitives, undoably. **This
brief is ONLY 4a** — the scaffold and basic editing. The **drawing tools** (line/rect/circle/arc/etc.),
**pins**, **live schematic update**, **fonts/stroke controls**, **bitmap**, and the **locked-symbol** gate are
**later sub-steps (4b/4c)** — do NOT build them here. Read `symbol-editor.md` §5 (editor UI) first. Mirror the
existing schematic stack. Sub-gated; **report and stop between every layer.** Firewall green; every edit
undoable.

> Read first: `docs/design/symbol-editor.md` §5 (editor UI), §2 (the model you're editing). Context code to
> **mirror** (the schematic stack is the template — the symbol editor is a smaller sibling):
> `src/Ui/Controls/SchematicCanvas.cs` (the Skia `Control`: DirectProperties, pan/zoom, pointer→VM
> delegation, `ICustomDrawOperation`+`ISkiaSharpApiLease`, `WorldToScreen`/`ScreenToWorld`),
> `src/Ui/ViewModels/SchematicViewModel.cs` (tool/selection/drag state, `OnPointerPressed/Moved/Released`,
> `Execute`, `RenderModel`/`Overlay`), `src/Ui/Renderers/SchematicRenderer.cs` (`DrawSymbol` — reuse it),
> `src/Ui/Schematic/SymbolModel.cs` (`Symbol`, `SymbolPrimitive` + subtypes, `BuiltInSymbols.Primitives`),
> `src/Ui/Schematic/SymbolGeometry.cs` (primitive bbox helper from step 1 — extend for hit-testing),
> `src/Ui/Commands/UndoRedoStack.cs` + `Commands/Schematic/*` (command pattern — every mutation notifies in
> both Execute and Undo), `src/Ui/Views/Content/SchematicView.axaml(.cs)` (canvas hosting + input wiring
> pattern), `src/Ui/ViewModels/Dock/CircuitRfDockFactory.cs` + `WorkspaceWindow` (how documents/tools dock).
> Design docs win on any conflict.

## The spine (do not violate)
- **Mirror the schematic stack, don't reinvent:** a `SymbolEditorCanvas` (Skia `Control`, like
  `SchematicCanvas`) + a `SymbolEditorViewModel` (tool/selection/edit state, like `SchematicViewModel`) + a
  `SymbolEditorView` (hosts the canvas, wires input) + commands on the **same** `UndoRedoStack`. Reuse
  `DrawSymbol` for rendering — do NOT write a second symbol renderer.
- **Edit a working copy `Symbol`** (a mutable editable form of the primitive list). 4a edits an **in-memory**
  symbol; loading/saving `.csym` and wiring to a real cell come in 4b/4c. For 4a, open the editor on a
  **built-in symbol's primitives** (`BuiltInSymbols.Primitives(kind)`) copied into the editable form, so
  there's real content to select/move/delete.
- **Every mutation is an undoable command** on the shared `UndoRedoStack`, notifying in both Execute and Undo
  (the standing rule).
- **Grid:** body-art primitives snap to the **fine authoring grid `p`** (`SnapToAuthorGrid`); pins snap to `P`
  — but **pins are 4b**, so 4a only moves art on `p`.
- **Scope fence (4a):** NO drawing tools, NO pin tool/mapping, NO live schematic update, NO font/stroke
  property panel, NO bitmap, NO locked-symbol gate, NO `.csym` load/save. Just: open → render → select/move/
  delete → undo, in a dockable/tear-off window.

---

## LAYER 1 — the editable symbol model + a hit-test/bbox helper

1. **Editable symbol form:** a mutable working copy of `Symbol` the editor mutates (e.g.
   `EditableSymbol { List<SymbolPrimitive> Primitives; List<SymbolPin> Pins; }` — pins carried but untouched
   in 4a), with a `ToSymbol()`/`FromSymbol(Symbol)` round-trip. (The `SymbolPrimitive` subtypes are mutable
   classes already, so they can be edited in place; the editable form just holds a mutable list.)
2. **Hit-test + bbox per primitive** (extend `SymbolGeometry`): `BboxOf(SymbolPrimitive)` and
   `HitTest(SymbolPrimitive, localX, localY, tol)` for each primitive type (line/polyline near-segment,
   rect/circle/etc. near-edge or inside-if-filled). Used for click-selection and the selection outline.

**Layer 1 gate:** editable form round-trips a `Symbol` losslessly; bbox + hit-test return sane results for
each primitive type (unit test). Framework-free (no Skia/Avalonia in the model/geometry). Report.

---

## LAYER 2 — `SymbolEditorViewModel` (tool/selection/drag state)

Mirror `SchematicViewModel`, much smaller. Holds the `EditableSymbol`, a **selection** (set of selected
primitive indices/refs), the active **tool** (4a tools: **Select** only — drawing tools are 4b), drag state,
and `Execute(IUiCommand)` on the shared `UndoRedoStack`. Pointer handlers:
- `OnPointerPressed(localX, localY, mods)` — Select tool: hit-test topmost primitive; set/toggle selection
  (shift-add); begin a move-drag if pressing on a selected primitive; rubber-band if on empty space.
- `OnPointerMoved(...)` — update drag (live offset, snapped to `p`) or rubber-band rect.
- `OnPointerReleased(...)` — commit the move as a **`MoveSymbolPrimitivesCommand`** (records old/new positions
  for the moved primitives; Execute/Undo both apply and notify), or finalize rubber-band selection.
- `OnKeyDown(...)` — Delete/Backspace → **`DeleteSymbolPrimitivesCommand`**; Ctrl+Z/Y → undo/redo;
  Escape → clear selection.
- Exposes a **render snapshot** (`Symbol` + overlay info: selected indices) the canvas reads, and fires
  `Changed`/PropertyChanged so the canvas invalidates (mirror `RenderModel`/`Overlay` + `SyncFromVm`).

**Commands (new, in `Commands/Symbol/`):** `MoveSymbolPrimitivesCommand`, `DeleteSymbolPrimitivesCommand` —
each holds the `EditableSymbol`, records the affected primitives + before/after, and calls the model's
`NotifyChanged()` in **both** Execute and Undo.

**Layer 2 gate:** VM compiles; a headless test drives press/move/release → a move command lands on the stack
and undo reverts it; delete + undo works; selection toggles. Report.

---

## LAYER 3 — `SymbolEditorCanvas` (Skia control) + selection overlay

Mirror `SchematicCanvas` (smaller — no LOD/spatial-index needed at symbol scale):
- Skia `Control` with pan/zoom (reuse the same wheel-zoom + middle-drag-pan code), `WorldToScreen`/
  `ScreenToWorld` (here "world" = symbol-local coords), `ICustomDrawOperation` + `ISkiaSharpApiLease`.
- **Render:** call `SchematicRenderer.DrawSymbol(canvas, vm.Symbol, 0, 0, R0, false, pan, zoom, theme)` (the
  symbol is drawn at local origin; the editor's pan/zoom is the view transform). Draw a **grid** (show the
  fine `p` grid, like the schematic grid). Draw a **selection overlay**: an outline around each selected
  primitive's bbox + the rubber-band rect (reuse the schematic selection-box visual style/roles).
- **Input:** delegate pointer/keyboard to the VM (mirror `SchematicCanvas`'s press/move/release/key
  delegation), `InvalidateVisual` on VM `Changed`.

**Layer 3 gate:** the canvas renders a built-in symbol via `DrawSymbol`, pan/zoom works, selecting a primitive
draws an outline, rubber-band selects, drag moves (snapped to `p`), delete removes — all undoable, redrawing
live. Report (screenshot description).

---

## LAYER 4 — host it: dockable + tear-off window + a toolbar stub

- **`SymbolEditorView`** (`UserControl`) hosting the `SymbolEditorCanvas` + a **toolbar stub** (just the
  Select tool + Undo/Redo buttons for now; the drawing-tool buttons are 4b — leave space/placeholders).
- **Hosting:** make it openable two ways (per §5): as a **dockable document/tool** in the workspace
  (mirror how schematic documents dock via `CircuitRfDockFactory`) AND as a **tear-off `Window`**. A simple
  command/menu entry "Open Symbol Editor (demo)" that opens it on a chosen built-in symbol is fine for 4a
  (real cell wiring is 4c). The **same `SymbolEditorView`** is hosted both ways — only the chrome differs.
- Undo/Redo buttons bind to the shared `UndoRedoStack.CanUndo/CanRedo` (mirror the WorkspaceVM wiring that was
  fixed earlier — `NotifyCanExecuteChanged` on stack `PropertyChanged`).

**Layer 4 gate:** the Symbol Editor opens both docked and as a tear-off window on a built-in symbol; select/
move/delete/undo work in both; Undo/Redo buttons enable/disable correctly. Report.

---

## Acceptance (4a)
1. A Symbol Editor opens (dockable + tear-off) on a built-in symbol and renders it via the shared `DrawSymbol`.
2. Select (click + shift-add + rubber-band), move (drag, snapped to fine grid `p`), and delete primitives —
   all **undoable** on the shared stack (Execute+Undo both notify; canvas redraws live).
3. Pan/zoom and the fine grid display work; selection outlines render.
4. `dotnet build`/`dotnet test` green; firewall green (model/geometry framework-free; Skia only in canvas/
   renderer); **no drawing tools, no pins, no live schematic update, no `.csym` I/O, no fonts/bitmap/lock** —
   those are 4b/4c; nothing in prior phases regresses.

## Guardrails
- **Mirror the schematic stack; reuse `DrawSymbol`** — no second symbol renderer, no bespoke pattern.
- **Every mutation undoable** on the shared `UndoRedoStack`, notifying in both Execute and Undo.
- **Art snaps to `p`** (fine grid); pins are untouched in 4a (no `P` snapping of pins yet — that's 4b).
- **Scope fence:** 4a is open→render→select/move/delete→undo, docked + tear-off. Drawing tools, pins, live
  update, fonts, bitmap, locked-symbol gate, `.csym` I/O are explicitly later — do NOT build them.
- Sub-gate the four layers; report and stop between each; don't run the full suite into the output limit.
- Update `symbol-editor.md` §11 status (4a done) and `src/Ui/CLAUDE.md` (the symbol editor mirrors the
  schematic stack; shared `UndoRedoStack`; reuses `DrawSymbol`).

*Exit: a working Symbol Editor window (docked + tear-off) that renders a symbol through the shared primitive
renderer and supports undoable select/move/delete of its primitives — the editing shell that the drawing
tools (4b) and pins + live update (4c) plug into.*
