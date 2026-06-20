# Phase 6f — Symbol Editor 4b: drawing tools + stroke/font property controls (Claude Code / Sonnet)

Add the **drawing tools** to the Symbol Editor (the 4a shell handles select/move/delete/undo): place each
vector primitive — line, polyline, rect, rounded-rect, circle, ellipse, arc, triangle/polygon, quad/cubic
curve, sine, half-wave, text — plus the **stroke-width** and **font (size + style)** property controls. **This
brief is ONLY 4b.** **Pins** (placement + port mapping) and the **live schematic update** are **4c** — do NOT
build them here. **Bitmap** import is a later sub-step too. Read `symbol-editor.md` §5 (toolbar/canvas) + §7.1
(sine/half-wave) + §7.3 (fonts) first. Build on the 4a stack exactly. Sub-gated; **report and stop between
every layer.** Firewall green; every mutation undoable.

> Read first: `docs/design/symbol-editor.md` §2.1 (primitive list), §5 (toolbar + property controls), §7.1
> (sine/half-wave smart-paths), §7.3 (fonts), §2.3 (color = role, no RGB). Context code (all from 4a):
> `src/Ui/ViewModels/SymbolEditorViewModel.cs` (the `Tool` enum — **add the drawing tools**; `OnPointerPressed/
> Moved/Released` in symbol-local coords; `Execute(IUiCommand)`; `_selection`; `SnapToP`; `RebuildOverlay`),
> `src/Ui/Schematic/SymbolEditorOverlay.cs` (overlay — add an in-progress-primitive preview field),
> `src/Ui/Commands/Symbol/MoveSymbolPrimitivesCommand.cs` + `DeleteSymbolPrimitivesCommand.cs` (the command
> pattern to mirror — both notify in Execute+Undo), `src/Ui/Schematic/SymbolModel.cs` (the primitive types +
> `SymbolColorRole`/`SymbolStrokeTier`/`SymbolFontStyle`/`SineAxis`/`SymbolTextAlign` + `EditableSymbol`),
> `src/Ui/Schematic/SymbolGeometry.cs` (`BboxOf`/`HitTest`/`TranslateBy` — extend if a new primitive needs
> it), `src/Ui/Renderers/SchematicRenderer.cs` (`DrawSymbol` — already renders all vector primitives incl
> Sine; reuse for the in-progress preview), `src/Ui/Controls/SymbolEditorCanvas.cs` + `Views/Content/
> SymbolEditorView.axaml(.cs)` (the canvas + toolbar to add tool buttons/controls to). Design docs win on
> any conflict.

## The spine (do not violate)
- **Mirror the 4a pattern:** new tools are `Tool` enum members; placement is driven by the existing
  `OnPointerPressed/Moved/Released` (symbol-local coords); each placement commits via `Execute(new
  PlaceSymbolPrimitiveCommand(...))` on the editor's own stack (notifying in Execute+Undo, like the move/
  delete commands). No new pattern.
- **Reuse `DrawSymbol` for the in-progress preview** — the primitive being drawn renders through the same
  `DrawSymbol`/overlay path, not a bespoke preview renderer. (One symbol-rendering source, the standing rule.)
- **Art snaps to the fine grid `p` (=5 local units)** via the existing `SnapToP` — every placement point
  snaps to `p`. (Pins are 4c; no `P` snapping here.)
- **Color is a role, never literal** (`SymbolColorRole`, default `SymbolLine`) — NO RGB picker. Stroke width
  (`SymbolStrokeTier` Normal/Thin, or the width field the model uses) and font size/style ARE editable.
- **Scope fence (4b):** drawing tools + stroke/font controls only. NO pins, NO live schematic update, NO
  bitmap, NO locked-symbol gate, NO `.csym` load/save UI. Those are 4c / later.

---

## LAYER 1 — `PlaceSymbolPrimitiveCommand` + the tool enum

1. **Command** (`Commands/Symbol/PlaceSymbolPrimitiveCommand.cs`, mirror Move/Delete): holds the
   `EditableSymbol` + the finished `SymbolPrimitive`; `Execute()` appends it to `Primitives` + `NotifyChanged()`;
   `Undo()` removes it + `NotifyChanged()`. (Append to end = topmost Z, consistent with hit-test-from-end.)
2. **Tool enum:** extend `SymbolEditorViewModel.Tool` with: `Line, Polyline, Rect, RoundedRect, Circle,
   Ellipse, Arc, Triangle, QuadCurve, CubicCurve, Sine, HalfWave, Text` (keep `Select`). Add an
   `[ObservableProperty]`-backed "current draw style" the tools read: `CurrentColorRole` (default
   `SymbolLine`), `CurrentStrokeTier`, `CurrentFontSize`, `CurrentFontStyle` — the property controls (Layer 4)
   set these; new primitives are created with them.

**Layer 1 gate:** command + enum + current-style properties compile; a unit test executes
`PlaceSymbolPrimitiveCommand` (append + undo-remove, both notify). Report.

---

## LAYER 2 — placement interaction (the drawing gestures)

Extend the VM pointer handlers so that when `ActiveTool != Select`, pressing/dragging **draws** instead of
selecting. Two gesture families:
- **Two-point drag** (Line, Rect, RoundedRect, Circle, Ellipse, Arc, Sine, HalfWave): press = first point
  (snapped to `p`), drag updates the second point (live preview), release commits the finished primitive via
  `PlaceSymbolPrimitiveCommand`. Map the two points to each primitive's fields (e.g. Rect: the two corners →
  Cx/Cy/W/H; Circle: center + radius from the drag distance; Arc/Sine: bounding span → params with sensible
  defaults for the artistic bits — cycles=1 for Sine, etc., tunable later).
- **Multi-point click** (Polyline, Polygon/Triangle, QuadCurve, CubicCurve): click adds points; a defined
  end gesture finishes (double-click, or Enter, or Escape-cancels) — mirror the schematic wire's multi-click
  finish (`FinishCurrentWire` analog). QuadCurve = 3 points, CubicCurve = 4 points, Triangle = 3 then auto-
  close; Polyline/Polygon = N points until finish.
- **Text:** click places a text primitive at the point; the content is entered via a small inline text box
  (mirror the schematic inline-edit box pattern) or a simple prompt — keep it minimal; commit the
  `TextPrimitive` with `CurrentFontSize`/`CurrentFontStyle`/`SymbolText` role.
- **After a successful placement, return to the Select tool** (or stay in the tool for repeat placement — pick
  the schematic's convention; match it). Escape cancels an in-progress placement (reuse `CancelOp`).
- **In-progress preview:** add a field to `SymbolEditorOverlay` (e.g. `InProgressPrimitive` or a point list +
  tool) so the canvas can render the primitive being drawn live via `DrawSymbol`. Update it on
  `OnPointerMoved`/click; clear on commit/cancel.

**Layer 2 gate:** with each tool selected, the gesture draws the primitive with a live preview and commits it
undoably (snapped to `p`); Escape cancels mid-draw; multi-point tools finish on the chosen gesture. Report
which tools were exercised.

---

## LAYER 3 — canvas: render the in-progress preview + tool cursors

In `SymbolEditorCanvas`:
- Render the overlay's in-progress primitive via `DrawSymbol` (a dashed/ghost paint like the schematic wire
  preview / ghost — reuse the overlay-preview visual style).
- Set the cursor per active tool (crosshair for drawing tools, default for Select) — mirror
  `SchematicCanvas.UpdateCursor`.
- Delegate the draw gestures to the VM handlers (already wired in 4a for press/move/release — just ensure the
  non-Select tools flow through).

**Layer 3 gate:** drawing any primitive shows a live ghost preview that becomes the committed primitive on
release/finish; cursor reflects the tool. Report.

---

## LAYER 4 — toolbar: tool buttons + stroke/font property controls

In `SymbolEditorView.axaml` (the 4a toolbar stub):
- **Tool buttons:** one per drawing tool (+ the existing Select), bound to set `ActiveTool` (a toggle/radio
  group so the active tool is visible). Group sensibly (shapes, curves, text). Match the schematic toolbar's
  styling.
- **Stroke control:** a small selector for `CurrentStrokeTier` (Normal/Thin) — or a width field if the model
  uses a numeric width. Applies to newly-drawn primitives (and, optionally, to the current selection via a
  `SetSymbolPrimitiveStyleCommand` — include that if cheap, else defer; note which).
- **Font controls:** `CurrentFontSize` (numeric) and `CurrentFontStyle` (Regular/Bold/Italic/Condensed combo)
  for the Text tool. Note in a tooltip that **Condensed depends on the bundled font face** and falls back
  gracefully (§7.3) — and confirm `DrawSymbol`'s Text rendering honors the style (if Text was stubbed in step
  1, implement minimal Text rendering now: the Text tool needs it).
- **No color control** (§2.3) — confirm none is present.

**Layer 4 gate:** the toolbar drives tool selection and the stroke/font style of newly-drawn primitives; Text
renders with the chosen size/style; no color control exists. All placements undoable. Report.

---

## Acceptance (4b)
1. Every vector primitive (line, polyline, rect, rounded-rect, circle, ellipse, arc, triangle/polygon, quad/
   cubic curve, sine, half-wave, text) can be drawn on the canvas with a live preview and committed undoably,
   snapped to the fine grid `p`.
2. Stroke tier/width and font size+style are settable and apply to newly-drawn primitives; Text renders with
   the chosen style (Condensed falls back gracefully); **no RGB color control** (role-based only).
3. The 4a select/move/delete/undo still works; switching tools works; Escape cancels an in-progress draw.
4. `dotnet build`/`dotnet test` green; firewall green (VM/model/geometry framework-free; Skia only in canvas/
   renderer); **no pins, no live schematic update, no bitmap, no locked gate, no `.csym` I/O** — those are 4c/
   later; nothing in prior phases regresses.

## Guardrails
- **Mirror the 4a command/tool pattern; reuse `DrawSymbol`** for the in-progress preview — no second renderer,
  no new architecture.
- **Every placement undoable** on the editor's own stack (Execute+Undo both notify).
- **Art snaps to `p`** (=5); pins are 4c (no `P` snapping here).
- **Color is a role, never literal** — no RGB picker; stroke + font are editable.
- **Scope fence:** drawing tools + stroke/font controls only. No pins, live-update, bitmap, locked gate, or
  `.csym` UI.
- Sub-gate the four layers; report and stop between each; don't run the full suite into the output limit.
- Update `symbol-editor.md` §11 status (4b done) and `src/Ui/CLAUDE.md` (the symbol-editor drawing tools +
  current-style model) to match.

*Exit: the Symbol Editor can draw the full vector-primitive vocabulary with live preview, undoably, snapped to
the fine grid, with stroke and font controls (role-based color) — leaving pins + the live schematic update for
4c.*
