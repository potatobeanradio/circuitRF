# Brief 5 — drag: reorder a layer, re-span a via, and place it laterally

**Series:** [stackup rendering](brief-stackup-render-0-overview.md) · **Tag:** `R-stk5-n`
**Area:** `src/Ui/Controls/StackupCanvas.cs`, `src/Ui/Layout/TechEditorViewModel.cs`, `src/Design/Layout/TechModel.cs`
**Depends on:** brief 2 · **Independent of:** 3, 4, 6, 7 (though it reads much better after 3)

---

## 0. What this brief delivers

Three drags, on one surface:

| Grab | Drag | Means |
|---|---|---|
| A conductor or dielectric band | up / down | **Reorder** it in z |
| A via barrel | up / down | **Move its span** — both ends together |
| A via barrel | left / right | **Slide it laterally** — cosmetic, and persisted |
| A via's top or bottom **gripper** | up / down | **Change that end** of the span |

Plus the one model change the series makes: an additive nullable field for that lateral position.

---

## 1. Reordering a band

### R-stk5-1 — the drag means exactly what the ↑ / ↓ buttons mean

The card already carries ↑ and ↓ bound to `TechEditorViewModel.MoveStackupLayer(row, ±1)`, and that
method already owns two rules that are easy to get wrong and were decided for good reasons:

- **A via row is refused outright.** Its position in the list is not z and never was — swapping it
  changed the reading order and no physical fact.
- **A band swaps with the next non-via band**, stepping over any via entries lying between them in the
  list. A via's index carries no meaning, so leaving it untouched is correct.

The drag must produce **the same sequence of `MoveStackupLayer` calls** a user would get by clicking
the buttons. Do not write a new reorder.

### R-stk5-2 — one drag, one undo entry

Dragging the top copper of a nine-entry stack to the bottom crosses four bands. Four `MoveStackupLayer`
calls would push **four** undo entries and one `Ctrl-Z` would put it back one place — which reads as a
broken undo.

So: the drag shows a **ghost** while the pointer is down and commits **once on release**:

```csharp
string before = SnapshotJson();
// apply the whole move in place against Working.Stackup.Layers, honouring R-stk5-1's rules
CommitEdit(before, $"Move {name} to position {n}");
```

Add `TechEditorViewModel.MoveStackupLayerTo(StackupLayerRowViewModel row, int targetBandIndex)` for
this, next to `MoveStackupLayer`, sharing its via rules — one `CommitEdit`, one snapshot, one entry.
Express `MoveStackupLayer(row, ±1)` in terms of it rather than leaving two copies of the via-skipping
walk.

### R-stk5-3 — what the drag looks like

- Below a small movement threshold, the gesture is a click (brief 3's selection), not a drag. Without a
  threshold every click nudges the stack.
- While dragging: the dragged band draws as a translucent ghost at the pointer, and an **insertion line**
  draws between the two bands it would land between. The stack does not re-flow under the pointer —
  re-flowing while dragging moves the target out from under the user.
- The insertion line snaps to **non-via band boundaries only**, which is the same rule as `R-stk5-1`.
- `Esc` while dragging cancels: no move, no undo entry. (Third job for `Esc`; it takes precedence over
  brief 3's clear-selection and is below brief 4's revert-edit, which cannot be open at the same time.)
- Releasing outside the canvas cancels.

---

## 2. Re-spanning a via

A via's span is `SpanFromLayer` / `SpanToLayer`, two **conductor** `StackupLayer.Name` values. Every
drag here resolves to a new pair of conductor names and commits them exactly as the card's two `Spans`
combo boxes commit them.

### R-stk5-4 — dragging the barrel moves both ends

Dragging the barrel body up or down moves the span as a unit: both ends step to the next conductor in
that direction, **keeping the number of conductors spanned constant**. It stops at the ends of the
stack rather than clamping one end and stretching — a barrel that silently changes length when it hits
the top copper is a via the user did not ask for.

### R-stk5-5 — dragging a gripper moves one end

Brief 1 places `ViaGripTop` and `ViaGripBottom` hits with a hit rect larger than the drawn glyph, shown
only on hover or selection. Dragging one snaps that end to the nearest **conductor** band under the
pointer.

- The two ends may not cross, and may not coincide: a via spanning one conductor connects nothing.
  Clamp at "adjacent conductors" — the minimum meaningful span — rather than refusing at release, so the
  gesture never ends in a message.
- Snap to conductors only. A via terminating on a dielectric is not expressible in the model and never
  should become so.

### R-stk5-6 — one undo entry, on release

Exactly as `R-stk5-2`. The whole drag is one `CommitEdit` with a description naming the new span
(`"Span Via1 Top→Inner1"`), so the undo list reads as the changes a person made.

---

## 3. The lateral position: the model change

### R-stk5-7 — the field

The owner: *"User can also move via left or right to arrange the vias laterally — this is for visuals
only to create nice looking stackups and must be persisted in the `.ctech` file."*

On `StackupLayer`, in `src/Design/Layout/TechModel.cs`, in the via block beside `Fill`,
`WallThicknessDbu`, `Plated`, `SpanFromLayer` and `SpanToLayer`:

```csharp
/// <summary>
/// Where this via is DRAWN across the width of a stackup cross-section, as a fraction of the drawn
/// band from 0 (left edge) to 1 (right edge). Null means "wherever the drawing puts it" — the
/// automatic lane assignment, and what every technology written before this field means.
///
/// <para><b>This is a drawing position and nothing else.</b> A stackup is a cross-section of a
/// laterally infinite sandwich; a via entry is a KIND of connection between two named conductors,
/// not one hole at one place, and every via drawn on its drawing layer is an instance of it. Nothing
/// in the extraction, the solver or any export may read this field, and a test asserts that a
/// technology differing only in this value extracts identically (R-stk5-9).</para>
///
/// <para>A fraction rather than a coordinate because the drawn band's width is the pane's, and
/// changes when the pane is resized; a stored pixel offset would drift every time.</para>
///
/// <para>Additive, nullable, no <c>.ctech</c> <c>FormatVersion</c> bump — the
/// <see cref="SheetAt"/>/<see cref="PresentWithLayer"/>/<see cref="Fill"/> pattern. Meaningless
/// (ignored) on a non-Via entry.</para>
/// </summary>
public double? DrawLaneFraction { get; set; }
```

`.ctech` persistence is automatic: `TechPersistence` serialises `Stackup` directly with
`DefaultIgnoreCondition = WhenWritingNull`, so **no existing file changes by one byte** and no reader
change is needed. Assert that (`R-stk5-8`) rather than assuming it.

### R-stk5-8 — round-trip and no-churn tests

Extend `tests/Ui.Tests/TechPersistenceTests.cs`:

- A value round-trips exactly.
- A `.ctech` written from a technology with every `DrawLaneFraction` null is **byte-identical** to one
  written before the field existed. Pin it against a committed fixture string, not against a
  re-serialisation of the same object.
- Out-of-range values clamp to `[0, 1]` on read, so a hand-edited file cannot put a barrel off the page.

### R-stk5-9 — the cosmetic-stays-cosmetic gate

**The important test in this brief.** Take a shipped technology, clone it, set every via's
`DrawLaneFraction` to a different value, and assert the two produce an **identical** extraction —
compare the `EmProblem` (or, if that is awkward to construct in `Ui.Tests`, the `PlanarExtractor`'s via
bindings and stack) element for element.

This field is one careless read away from becoming geometry, and the failure mode if it ever is would
be an answer that changes when someone tidies up a picture. A comment does not prevent that. A test
does.

### R-stk5-10 — the drag

Horizontal drag on a via barrel writes `DrawLaneFraction`, one `CommitEdit` on release
(`"Move Via1 laterally"`). It clamps so the barrel stays inside the band column, and it snaps to a
small number of lanes by default with a modifier (`Alt`) for free positioning — "nice looking stackups"
means aligned ones, and a free-drag-only control makes alignment a matter of eyesight.

Horizontal and vertical drags on a barrel are **one gesture**: the dominant axis at the movement
threshold decides which, and it does not change mid-drag. A barrel that re-spans itself because the
user's hand drifted upward while sliding it sideways is the defect this rule exists to prevent.

---

## 4. Tests

`tests/Ui.Tests/Stackup/StackupDragTests.cs`, plus the `TechPersistenceTests` additions above.

| Tag | Test |
|---|---|
| `R-stk5-1` | A reorder drag over a stack containing via entries produces the same `Stackup.Layers` order as the equivalent ↑/↓ clicks. A drag on a via row never reorders anything. |
| `R-stk5-2` | Dragging a band across four others pushes **exactly one** undo entry, and one `Undo()` restores the original order completely. |
| `R-stk5-2` | `MoveStackupLayer(row, ±1)` and `MoveStackupLayerTo` agree for every single-step case on the shipped technologies. |
| `R-stk5-3` | A press-and-release below the movement threshold selects and does not reorder. `Esc` mid-drag leaves the model untouched and pushes nothing. |
| `R-stk5-4` | A barrel drag preserves the number of conductors spanned, and stops rather than stretching at the stack ends. |
| `R-stk5-5` | A gripper drag snaps to conductors only; the ends cannot cross or coincide. |
| `R-stk5-6` | Each via drag is one undo entry and the result is byte-identical to setting the same span through the card's `SelectedSpanFrom`/`SelectedSpanTo`. |
| `R-stk5-8` | Round-trip; null-field byte identity; clamp on read. |
| `R-stk5-9` | **The extraction is identical under every lane value.** |
| `R-stk5-10` | Dominant-axis lock: a mostly-horizontal drag never changes the span; a mostly-vertical one never changes the lane. |

---

## 5. Scope

- No new fields other than `DrawLaneFraction`. No `FormatVersion` bump.
- No drag for conductors' or dielectrics' *thickness* — that is a typed value (brief 4), and a
  drag-to-resize on a compressed axis (brief 1 §3) would write a number the user cannot predict from
  the gesture. If this is wanted later it needs its own brief and a decision about what a drag on a
  logarithmic band means.
- No menus, no clipboard.

**On completion:** record the model-field findings in `src/Design/RESOLVED.md` and the interaction
findings in `src/Ui/RESOLVED.md`. Never in a CLAUDE.md.
