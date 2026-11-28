# Brief PCD-11 — Authoring on the canvas: a seeded port, a drop anywhere, a tee on a joint, drawn metal

**Series:** [PCell Designer](brief-pcd-0-overview-and-contract.md) · **Tag:** `R-pcd11-n`
**Area:** new `src/Design/PCellDesigner/PCellAuthoring.cs`; `PCellDesign.Create` and `PinDrop` (PCD-5's and PCD-4's,
for this); `src/Ui/PCellDesigner/`, `src/Ui/Views/PCellDesigner/`; `docs/user/src/reference/pcell-designer.md`
**Depends on:** PCD-6 (the window), PCD-2 (offsets), PCD-3 (`MSHAPE`), PCD-4 (`PinDrop`, `CellNetlistMerge`).

---

## 0. What this adds

Until now a cell was authored on the canvas only by dropping a part on a free pin, and a new cell had no pin at all.
This brief closes the four gaps: a new cell starts with a port, a part can be dropped anywhere, a drop on a joint
makes the tee, and copper can be drawn corner by corner. Every result is still a function of the parameters (S1).

## 1. Requirements

- **`R-pcd11-1` — a seeded port.** `PCellDesign.Create` writes Port 1 and its ground. The first drop on an empty canvas
  joins the part to that port. **+ Port** (and a Port dropped from the Library on a pin) adds the next port, grounded,
  on a free pin.
- **`R-pcd11-2` — a drop on empty canvas.** The part is placed by an offset from the nearest pin: that pin is the
  anchor, `Dx` and `Dy` the drop point in the anchor's frame as literals in the cell's length unit, `Angle` 0. One undo
  step; the status names the anchor.
- **`R-pcd11-3` — a drop on a joint.** When the pin is joined to one other part, `PinDrop` inserts an `MTEE` whose
  widths follow the parts (`W1`, `W2` the two old parts' `W`, `W3` the new part's), the old parts on its through pins and
  the new part on its branch. When one of the two is already an `MTEE`, it becomes an `MCROSS` with the new part on its
  fourth pin. A joint where a part has no width (a port, a pad) is refused with a sentence. One undo step.
- **`R-pcd11-4` — the `MSHAPE` draw tool.** Click the corners; double-click or Enter finishes, Esc cancels. Pin 1 is
  the nearest pin when a corner is within reach (joined to it by net), else the first corner, placed by offset from the
  nearest pin. The corners are written relative to pin 1, in the anchor pin's frame, in the cell's length unit; pin 1's
  width is the anchor part's `W` when it has one; `Model` is `"open"`.

## 2. Decisions

- **D1 — Text, not a second editor.** The tee and the port go through the netlist and `CellNetlistMerge`, as a pin drop
  does: the extraction writes each new line with its substrate, and the merge redraws the wires.
- **D2 — The cell's length unit** is the unit of the first length parameter in the table, else the layout's display
  unit.
- **D3 — Literals in one database unit.** A drop's offset and a drawn corner are rounded to the database unit and
  written in six significant figures, so the drop lands where it was made.

## 3. Tests

`tests/Ui.Tests/PCellDesigner/Pcd11*.cs`, one per requirement, headless through the view model.

## 4. Possible enhancements

- **A drop that snaps to a grid.** The literal is where the pointer was; a grid in the cell's unit would give round
  numbers, at the cost of a grid setting for a window that has none.
- **Arcs and holes while drawing.** `MSHAPE` has neither yet (PCD-3); the draw tool would offer them once it does.
