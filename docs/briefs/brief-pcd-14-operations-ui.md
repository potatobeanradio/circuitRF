# Brief PCD-14 — The operations in the PCell Designer window

**Series:** [PCell Designer](brief-pcd-0-overview-and-contract.md) · **Tag:** `R-pcd14-n`
**Area:** `src/Ui/PCellDesigner/`, `src/Ui/Views/PCellDesigner/`, `tools/UiSnapshot/PCellDesignerSnapshot.cs`,
`docs/user/src/reference/pcell-designer.md`
**Depends on:** PCD-13 (the history and its engine: `CircuitRF.Design.PCellDesigner.Operations`, its §2 contract),
PCD-6 (the window), PCD-10 (the dimension overlay).

---

## 0. What this adds

PCD-13 gives a designed cell a history of geometric operations (Merge, Subtract, Intersect, Exclusive, Grow, Repeat)
kept in its `.csch`. This lane is how a designer makes and edits that history in the window: select operands on the
canvas, press an operation, and see the copper, the response and the dimensions follow.

## 1. Requirements

- **`R-pcd14-1` — selection.** Click selects a part or an `MSHAPE` on the canvas (Shift-click adds or removes); a layer
  picker adds `layer:<name>` operands. The selection is listed in the window, first selected first.
- **`R-pcd14-2` — the toolbar.** Merge, Subtract (the first selected minus the rest), Intersect, Exclusive, Grow/Shrink
  (an amount expression, optionally onto another layer) and Repeat (rows, columns and pitches as expressions). Each
  calls `PCellOperations.Add` and is one undo step; a refusal is the status line and nothing changes.
- **`R-pcd14-3` — the history panel.** One row per operation in order: its `Describe` line, a Suppressed check box,
  and its refusal when its step in `TraceOf(layout)` was not applied. Edit in place (the amount, the counts and pitches,
  the operands as text), move up and down, suppress, delete; each is `Edit`, `Move`, `Suppress` or `Delete` and one undo
  step. The canvas, the response and the dimensions re-plan as after any edit.
- **`R-pcd14-4` — Make tool.** On a selected `MSHAPE`: its `Role` becomes `"tool"` and its `Model` `"open"`, one undo
  step.
- **`R-pcd14-5` — model honesty.** When `PCellOperations.WithoutCircuitModel` names operations, the response line says
  the cell has no circuit model and names them (`op2 (Subtract)`), pointing to Run EM, as it does for an `MSHAPE`
  with `Model="none"`.

## 2. Decisions

- **D1 — The view model owns it,** as every gesture of PCD-6: selection, the toolbar's fields and the history rows are
  view-model state, each action a method a test calls; the window only maps clicks to them.
- **D2 — A click selects the placement under it.** The part is the instance whose box holds the point, the smallest
  first (a pad on a line selects the pad); a tool `MSHAPE` is found the same way from its outline's box.
- **D3 — The operand text** in the history row is the operand list joined by spaces (`TL2 SH2`, `layer:Mask`), so an
  operand can be retyped without a picker.

## 3. Tests

`tests/Ui.Tests/PCellDesigner/Pcd14*.cs`, one per gesture through the view model; those needing the engine's real
bodies are written last, once PCD-13's commits are in.

## 4. Possible enhancements

- **A preview of the selected row's result** (`PCellOperations.Preview`) drawn over the canvas, so a step can be seen
  on its own; the full re-plan already shows the end result.
- **Drag to reorder** rows; up and down buttons do the same in two clicks.
