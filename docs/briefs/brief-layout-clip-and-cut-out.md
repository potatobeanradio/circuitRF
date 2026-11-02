# Brief — Clip and Cut Out: the per-operand booleans the layout editor is missing

**Status: SHIPPED 2026-09-09.** Every requirement built and gated. Findings in `src/Ui/RESOLVED.md`.
Investigation was done against `main` @ `c6c518fe`; the measurements in §1 and the defect in §8 are
from that investigation.

**Reported (owner, 2026-09-09):** on a board layout, the ask was to keep only the Top Copper artwork
inside a region of interest, ahead of an EM simulation. The gesture used was the obvious one — draw a
Rect over the region on the same layer, select everything on Top Copper, run **Intersect** — and the
layer came back empty.

**Intersect was right.** What is missing is a different operation, which this brief adds.

---

## 1. Why Intersect emptied the layer, and why that is correct

`LayoutBooleans.Intersect` is **n-ary**: `A ∩ B ∩ C ∩ …` over the whole selection, folded pairwise in
`Combine` (`src/Design/Layout/LayoutBooleans.cs:31`). It is not "clip each operand against one of
them". The reported layer held:

| Top Copper, one cell | count |
|---|---|
| Polygons | 67 |
| Rect drawn for the region of interest | 1 |
| Shape pairs whose **bounding boxes are disjoint** | 2185 of 2278 |

Relative to that Rect the 67 polygons sort as **65 fully outside · 1 fully inside · 1 straddling** (the
board-wide pour). So the fold goes empty at the first disjoint pair and never reaches the Rect. The
empty result is the correct answer to the question that was asked.

**Intersect stays exactly as it is.** Two overlapping shapes reduced to their common region is a real
operation people want, it is what every other tool's AND does, and nothing below changes its
semantics, its enablement, or its name.

Two things the current behaviour does get wrong, both small:

- The only feedback is `_messageSink?.Info("Intersect produced no geometry.")`
  (`src/Ui/Layout/LayoutEditorViewModel.Booleans.cs:434`) — `Info` severity, and it does not say
  *why*. §5.4 replaces it with a sentence that names the cause.
- §7 is a live unhandled exception on this exact gesture, found while investigating.

## 2. The operation that is missing

**Per-operand clipping.** One shape acts as a **stencil**; every selected shape is intersected with it
*independently*, and each keeps its own identity. N operands in, N results out (0..N shapes each), not
one combined region.

This is not expressible with what ships today. `Difference` has the same n-ary shape as `Intersect`
(`A − B − C − …`), so "select everything and subtract a frame" subtracts the operands from each other.
The only current route is to select the stencil plus **one** shape and run Intersect, repeatedly.

## 3. Naming

**`Clip`** and **`Cut Out`**.

- **Clip** — keep the part of each selected shape that lies **inside** the stencil.
- **Cut Out** — keep the part that lies **outside** it.

*Clip* is the term this operation carries across graphics (a clipping path), vector-geometry libraries
and GIS overlay analysis, where its complement is likewise a first-class named operation. It reads
correctly to someone who has never used circuitRF, and it does not collide with `Intersect`, which is
the different thing §1 describes.

The menu headers name the stencil (§5.2), so they read `Clip to Rect` / `Cut Out with Rect` rather
than bare verbs — the one place in this block where a bare verb would hide the thing the user most
needs to see.

**Do not call either of them `Slice`.** See §8.

## 4. Scope: the selection, and only the selection

**R-clip-0. The operands are the selected shapes. Unselected geometry is never touched, on any
layer.** This is the same contract every other boolean in this editor honours and there is no reason
for these two to differ. A layout-wide "clip everything to a boundary" is a different feature (a tool,
not a selection operation) and is not in this brief.

What *is* dropped is the **same-layer enablement constraint**.
`Intersect`/`Difference`/`XOR` require `SelectionHasSameLayerPair` because combining two shapes across
layers has no meaning. Clip combines nothing: the stencil is a stencil, and each operand is clipped on
its own and keeps its own layer. So the operands may sit on any mix of layers, and selecting the copper
on two layers plus running Clip once is one operation instead of two. **That is a statement about which
selections are legal, not about how far the operation reaches.**

## 5. Which shape is the stencil

This is the question the whole design turns on, and the answer is a gesture, not a rule.

### 5.1 The stencil is the shape under the right-click

**R-clip-1. `Clip` and `Cut Out` use the shape under the right-click that opened the context menu as
the stencil. The selection is the operand set.**

Two facts make this exact, both verified in the current code:

- **A right-click does not disturb the selection.** `LayoutCanvas.OnPointerPressed`'s right-button
  branch records `ContextMenuTarget = (wx, wy)` and returns (`src/Ui/Controls/LayoutCanvas.cs:626`);
  it never calls `_viewModel.OnPointerPressed`, so `ApplyClickSelection` never runs. Whatever was
  selected before the right-click is still selected when the menu opens.
- **The menu already knows what is under the click.** `BuildContextMenuItems(wx, wy)` receives the
  world point and already hit-tests it — `FindEdgeForContextMenu`, `FindVertexForContextMenu`,
  `FindRulerForContextMenu` (`LayoutCanvas.cs:979`). Items whose meaning depends on the right-clicked
  object are an established pattern in this menu, not a new one.

So the gesture is: **select what you want clipped, right-click the tool shape, choose Clip.** The
designation is explicit, costs no extra click, and needs no state, no mode and no ordering convention.

**The stencil need not be selected**, and if it happens to be (a `Select All` sweeps it up), it is
excluded from the operand set rather than clipped against itself.

**R-clip-2. The stencil is NOT consumed.** It is a tool, not an operand — it is not part of what the
operation replaces, so it survives, exactly as an unselected shape would. This is a deliberate
departure from the other booleans, and it follows from R-clip-1 rather than contradicting it: those
consume their operands, and the stencil is not one. It is also what makes the common case work —
clip Top Copper, then select Bottom Copper and clip again with the same rect. Deleting it afterwards
is one keystroke; recreating it is not.

### 5.2 The rule must be visible

**R-clip-3. The menu header names the stencil, and so does Messages** — a mis-aimed right-click is
then obvious before the click, not after:

- Header: `Clip to Rect` · `Cut Out with Rect`. Tooltip: `Clips the 67 selected shapes to Rect · Top Copper`.
- Success: `Clip: 67 shapes clipped to Rect · Top Copper — 65 removed, 1 changed, 1 unchanged.`
- Nothing survived: `Clip: no selected shape overlapped Rect · Top Copper — all 67 were removed.`

This is `ReportOperandOutcome`'s existing pattern, which already names Difference's first operand by
kind and layer (`LayoutEditorViewModel.Booleans.cs:425`).

### 5.3 Considered and rejected

- **Last shape in document order** (highest index in `Model.Shapes`). Deterministic under every
  gesture and it lands on the right shape for the motivating workflow — a stencil is drawn *for* this
  operation, so it is the newest shape. But it is invisible: nothing on screen says which shape is
  special, and it silently picks the wrong one for a stencil drawn earlier. R-clip-1 is the same
  answer made visible and made explicit.
- **Last shape selected.** There is no such thing for most selections. `SetSelection` preserves the
  order it is handed (`LayoutEditorViewModel.cs:957`) and a Ctrl+click genuinely appends, so click
  order is meaningful for a click-built selection — but a marquee hands it spatial-index query order
  and `Select All` hands it ascending index order (`SelectableShapeIndices`,
  `LayoutEditorViewModel.cs:1047`). A rule that is click order sometimes and tree order otherwise is
  a rule nobody can predict.
- **First selected, matching `Difference`.** Same problem, and `Difference`'s convention is not being
  extended: it is a fold whose first operand is its minuend, which matches the `A − B` reading. Clip
  is not a fold.

### 5.4 While the same trip is open, fix Intersect's own report

**R-clip-4.** Replace `"Intersect produced no geometry."` with a sentence that states the cause and
points at the new command — the empty result in §1 is legitimate and must stop reading like a failure:

> `Intersect: the selected shapes have no region in common (Intersect is the region shared by ALL of them). To keep the part of each shape inside one region, right-click that region and use Clip.`

Same for XOR and Difference where they legitimately produce nothing, minus the Clip pointer.

## 6. Geometry

One function with a `ClipType`, in `src/Design/Layout/LayoutBooleans.cs` — pure geometry,
framework-free, no undo, no selection, no Messages, exactly like everything else in that file.

```csharp
public static LayoutClipResult Clip  (IReadOnlyList<LayoutShape> operands, LayoutShape stencil, Technology? tech);
public static LayoutClipResult CutOut(IReadOnlyList<LayoutShape> operands, LayoutShape stencil, Technology? tech);

public readonly record struct LayoutClipResult(
    IReadOnlyList<LayoutShape> Shapes,   // in operand order; an operand may contribute 0..N
    int OperandsRemoved,                 // went empty
    int OperandsChanged,                 // geometry differs
    int OperandsUntouched,               // passed through byte-identical (R-clip-6)
    bool AnyCurvedOperand);              // a CLIPPED operand needed flattening
```

**R-clip-5. Each operand keeps its own `Layer` and its own `Net`.** The stencil contributes neither.
This is a deliberate departure from `Combine`'s `NetsDiffered` rule (`LayoutBooleans.cs:117`), which
clears the net when operands disagree — correct there, because a union of two nets genuinely has no
single net. Clipping a 40-net copper layer must not silently strip 40 nets.

**R-clip-6. An operand the stencil does not touch is passed through UNCHANGED — the same object, not a
polygonized copy.** Test the operand's bbox against the stencil's bbox first: no overlap means, for
`Clip`, contribute nothing, and for `Cut Out`, contribute the original shape. Without this, `Clip`
quietly converts every Circle, RoundedRect and Curve on the layer into a `PolygonShape` — a
destructive, invisible flatten of artwork the user never asked to touch. It is also what makes the
operation fast: on the §1 board it skips 65 of 67 operands with no Clipper2 call at all.

Containment is a second, cheaper-than-clipping case: an operand whose bbox lies wholly inside the
stencil's bbox is unchanged by `Clip` and removed by `Cut Out` — but only for a stencil that is convex
and hole-free, which a `RectShape` is and an arbitrary `PolygonShape` is not. Restrict it to a Rect
stencil or skip it; the bbox-disjoint test above is the one that carries the win and it is exact for
both operations and every stencil.

**R-clip-7. One undo entry**, a single `Commands.Layout.ReplaceShapesCommand` over the whole selection,
built and executed exactly once — the rule stated at the top of
`src/Ui/Layout/LayoutEditorViewModel.Booleans.cs` and shared with every method in it. Results are
inserted at the lowest removed index and become the new selection, as `CommitReplace` already does.
The stencil is neither removed nor re-added (R-clip-2), so it is untouched by the undo entry.

## 7. UI

`LayoutCanvas.AddBooleanAndFlattenMenuItems` (`src/Ui/Controls/LayoutCanvas.cs:1273`), in the boolean
block directly after `XOR`, no separator — they are booleans and belong with the others. That method
takes no coordinates today and gains the `(wx, wy)` its caller already has.

Availability is a new `ClipAvailability(wx, wy)`, not `BooleanOpAvailability` (which requires a
same-layer pair — §4 does not):

| Condition | Result |
|---|---|
| any instance in the selection | disabled — `"Clip applies to shapes only; N instances selected."` (`ShapeOnlyBlockReason`, unchanged) |
| no geometric shape selected | disabled — `"Select the shapes to clip, then right-click the shape to clip them to"` |
| right-click did not land on a geometric shape | disabled — `"Right-click the shape to clip to"` |
| the stencil is the only selected shape | disabled — `"Select the shapes to clip — the right-clicked shape is the stencil, not an operand"` |
| otherwise | enabled |

Every disabled case names its remedy, per R13a in `docs/design/layout-view.md` — and here the reason
text is also how a user learns the gesture, which is why none of these may be silent or hidden.

Bitmaps, labels and vias are excluded from the operand set and can never be the stencil (§8).

No CLI verb. `docs/design/cli.md` is explicit that there are deliberately no per-primitive edit verbs;
once a document exists the way to change it is to write it.

## 8. A live crash found on the way — fix it in this brief

**Selecting a `LabelShape` or a `ViaShape` together with any geometry on the same layer and running
Union / Intersect / Difference / XOR throws `ArgumentOutOfRangeException` out of a context-menu
click.** Measured 2026-09-09, both kinds:

```
selected=2  canBool=True
ArgumentOutOfRangeException :: LayoutFlattener.Flatten supports Rect/Polygon/RoundedRect/Circle/Curve
only — Path is a centerline …, and Via/Label are not filled-region primitives. (Parameter 'shape')
```

`GeometricSelectedIndices` (`LayoutEditorViewModel.Booleans.cs:38`) filters `BitmapShape` and nothing
else, so a label or a via reaches `LayoutClipper.ToClipperPaths` → `LayoutFlattener.Flatten`, whose
switch throws on both. The flattener's own message says plainly that neither is a filled region — the
filter simply never learned about them.

This is directly on the reported gesture's path: *select everything on a copper layer, run a boolean*.
The reported cell happens to carry no label or via on that layer, which is the only reason the report
was an empty layer rather than a crash dialog.

**R-clip-8. `GeometricSelectedIndices` excludes `LabelShape` and `ViaShape` as well as `BitmapShape`,
and the exclusion is stated once**, as a positive test for what the flattener accepts rather than a
growing deny-list — a new non-region shape kind must not be able to reintroduce this. A via wanted as
artwork has `Convert to Via`'s inverse; a label has `Flatten to Polygon`, the supported route from
text to a region, which already exists.

Gate: a shape-kind-exhaustive test that puts **every** `LayoutShape` subclass into a boolean operand
set and asserts no throw. That is the test that would have caught this one and will catch the next.

## 9. `Slice` is a third operation, and it is not in this brief

`Slice` appears in the edit-operations list in **both** `docs/design/layout-view.md:665` and
`docs/user/src/reference/layout-editor.md:121`. There is **no such command anywhere in `src/Ui` or
`src/Design`** — no menu item, no view-model method, no implementation. It has never been specified
either: it is a bare word in a bullet list.

In layout work `Slice` means a *knife cut*: a line or polyline that divides the shapes it crosses into
separate pieces, with no region removed and total area conserved. That is genuinely different from
both operations here — a different input (a line, not a region) and a different output (more shapes,
same area). Spending the name on "remove what is inside a region" would make a real knife cut
unnameable later.

**R-clip-9.** Replace `Slice` in those two lists with `Clip` and `Cut Out`. A knife-cut `Slice` is a
reasonable future item and should be briefed on its own when it is wanted; until then the docs must
not promise a command that does not exist.

## 10. Gates

`tests/Ui.Tests/LayoutBooleanOperationsViewModelTests.cs` for the VM half (selection wiring, one undo
entry, restore-at-original-index, Messages text — its existing subject), plus geometry-level tests
beside the other `src/Design` ones.

1. **The reported case.** 67 disjoint polygons + a Rect on one layer; the polygons selected, the Rect
   right-clicked, `Clip` leaves the 1 inside shape, the clipped part of the 1 straddler, the Rect, and
   nothing else. Intersect on the same selection still returns empty — §1's semantics are unchanged.
2. **R-clip-0** — geometry that is NOT selected is byte-identical afterwards, including geometry on the
   stencil's own layer that the stencil overlaps. This is the test for the question this design turns
   on, and it belongs first.
3. **R-clip-1** — a right-click on the stencil leaves `SelectedIndices` exactly as it was; the stencil
   is excluded from the operand set when it is itself selected; a right-click on empty space disables
   the command with its stated reason rather than guessing.
4. **R-clip-2** — the stencil is still in the model afterwards, byte-identical, and the same stencil
   clips a second, disjoint selection on another layer in a second operation.
5. **R-clip-5** — operands on four different nets, clipped: every result carries its own operand's net,
   and no `NetsDiffered` warning is posted.
6. **R-clip-6** — a Circle and a RoundedRect wholly outside the stencil survive `Cut Out` as *the same
   objects*, still `CircleShape`/`RoundedRectShape`, byte-identical through
   `LayoutPersistence.Serialize`. This is the test that catches a silent flatten.
7. **§4** — one stencil, operands on three layers, one operation: each result on its operand's own
   layer; `ClipAvailability` is enabled for a selection with no same-layer pair at all.
8. **R-clip-7** — one undo entry restores every operand at its original index, byte-identical.
9. **R-clip-3** — the menu header names the stencil by kind; Messages reports the three counts; the
   all-removed case posts its own sentence.
10. **R-clip-8** — every `LayoutShape` subclass in a boolean operand set, no throw (§8).
11. **R-clip-9** — a docs test, or a grep in the existing docs gate, asserting `Slice` no longer
    appears as a shipped command in either list.

`Clip` and `Cut Out` are complements: for any operand and stencil, `Clip` ∪ `Cut Out` reconstructs the
operand and their intersection is empty. Worth one property-style test over a handful of fixed
operand/stencil pairs — it catches a fill-rule or hole-nesting mistake in one assertion.

## 11. Out of scope

- A knife-cut `Slice` (§9).
- A layout-wide clip-to-boundary that ignores the selection (§4) — a different feature, and a tool
  rather than a selection operation.
- Any change to `Intersect`, `Union`, `Difference` or `XOR` semantics or enablement — R-clip-4 changes
  only the wording of what they report, and R-clip-8 only which shape kinds may enter an operand set.
- A CLI verb (§7).
- Clipping placed instances. An instance is not geometry, and `ShapeOnlyBlockReason` already disables
  the whole command when one is selected — `Flatten Hierarchy` first is the existing route.
