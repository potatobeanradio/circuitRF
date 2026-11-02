# Brief DFR26-4 — The layout ratsnest follows a part through copy, paste and rotate

**Series:** designer feedback round 26 (2026-10-24), see DFR26-1 for the list. · **Tag:** `R-dfr26-4-<n>`
**Area:** `src/Ui/Layout/LayoutEditorViewModel.Ratsnest.cs`, `src/Design/Layout/Extraction/Ratsnest.cs`,
`src/Ui/Layout/LayoutEditorViewModel.Clipboard.cs` (copy payload ~55, paste placement ~173-210),
`src/Ui/Layout/SchematicToLayoutGenerator.cs` + `src/Ui/ViewModels/WorkspaceViewModel.SchematicToLayout.cs`,
`src/Ui/Controls/LayoutCanvas.cs` (rat hit-testing). Findings go in `src/Ui/RESOLVED.md`.
**Tests:** new `tests/Ui.Tests/Layout/RatsnestRound26Tests.cs`, plus the existing ratsnest and schematic-to-layout
classes.

---

## 0. The gap

The designer imported their schematic (a `.dsn`), updated the cropped ten-layer layout from it, and placed the two
series capacitors. What they saw:

1. **No rats at first** for the parts that came from the imported schematic, and the layout's reference designators
   did not match the schematic's. Running Update Layout from Schematic a second time gave the right designators.
2. **Copy then paste of a placed part resets its designator, and its rats are gone.** A plain move keeps both. Their
   screenshot shows the pasted pair of 0402s at the new place with no rats and the old rat still drawn at the original
   position.
3. **Rotating a part disconnects its rats.**
4. **Rats get stuck** when parts are moved around, and **a rat that has come loose cannot be selected.**

The ratsnest is drawn from "the schematic's nets by placement" matched to the layout's pins (see the header of
`LayoutEditorViewModel.Ratsnest.cs`). Everything above is that match breaking: a pasted part is a new placement whose
designator no longer names a schematic part, and a rotated part's pins are not re-read.

## 1. Reproduction

Build it synthetically first: a two-part schematic (R and C in series between two ports), Update Layout from Schematic,
then in the layout (a) copy-paste the C, (b) rotate the R by 90°, (c) drag both, (d) undo each. Record which step loses
which rat. Then confirm on the designer's workspace the owner holds (an imported `.dsn` schematic): what made the first
update come out with designators that did not match (a second update fixed them, so it is order-dependent — find the
order).

## 2. What to do

### R-dfr26-4-1 — Copy, paste and duplicate keep the link to the schematic part, unless that would make two

- **Cut then paste** (a move by clipboard) keeps the designator and the schematic link — it is the same part.
- **Copy then paste** while the original is still placed makes a second placement of one schematic part. Keep the
  original linked; give the copy the next free designator and say, in one status line, that it is not in the
  schematic ("C210 copied as C210_1 — not in the schematic, so it has no rats"). A pasted copy must never silently take
  over the original's rats.
- **Copy then paste after the original was deleted** keeps the designator and the link.

### R-dfr26-4-2 — Rotate, mirror and flip re-read the pins

Every transform that moves pins invalidates the ratsnest pin cache exactly as a move does (the move path offsets the
dragged parts' pins; a rotation must re-read them). A rotated part's rats end on its rotated pins.

### R-dfr26-4-3 — No stale rats

After any edit settles, every rat drawn is from the current pin positions; a rat whose part is gone is not drawn.
Find the "stuck" case from §1 and pin it with a test. A loose rat being unselectable is then moot; if rats are meant to
be selectable at all (check the overlay's hit test), they are selectable everywhere.

### R-dfr26-4-4 — First update gives the schematic's designators

Fix whatever made the first Update Layout from Schematic of an imported schematic come out with different
designators, and add the case to the schematic-to-layout tests.

## 3. Tests

One per claim: cut-paste keeps rats; copy-paste gives the copy a new designator with no rats and leaves the original's
rats; rotate keeps rats on the rotated pins; undo of each restores the rats; first update of a schematic whose parts
were imported matches designators. Run the new class and the existing `*Ratsnest*` and `*SchematicToLayout*` classes
only.

## 4. Text

`docs/user/src/reference/layout-editor.md`, the ratsnest paragraph: what copy, paste and rotate do to rats. Regenerate
that page only.

## 5. Out of scope

Back-annotating a layout-only part into the schematic.
