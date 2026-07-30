# Sonnet Brief — Snap combobox blanking, grid/geometry snap consistency, glyph size

Three small items against the landed geometry-snap work.

Gate command is plain `dotnet test`.

---

## 1. Bug: the grid-snap combobox is sometimes blank

Reported in two situations: opening an existing `.clay` (the owner suspects a sub-1-mil snap value), and
creating a new workspace then a new layout.

### 1.1 Cause

An Avalonia `ComboBox` renders **blank** when its bound selection matches **no item in the list**. The ladder
built per R-snp-2 is a set of technology-derived rungs; a document whose `SnapDbu` is not one of them has
nothing to select.

That covers both reports, and they are probably two different causes of the same symptom:

- **Existing `.clay` with an off-ladder value** — e.g. 0.5 mil when the ladder is 1 / 5 / 10 / 25 / 50 mil. The
  owner's suspicion is almost certainly right.
- **New workspace + new layout** — more likely a **binding-order** problem: the combobox populates before the
  technology resolves, so the ladder is empty at bind time and never repopulates. A fresh layout seeded from
  `DefaultSnapDbu` (25400 = 1 mil in the shipped RO4350B technology) *should* match a rung, which is what makes
  ordering the likelier explanation here.

### 1.2 Fix

**R-cmb-1. The ladder always contains the document's current `SnapDbu`.** If it is not a standard rung, insert
it in sorted position and display it through `LayoutUnits.Format` like any other entry. That makes blank
**structurally impossible** rather than fixing the two reported paths and leaving a third.

Do not silently round the document's value onto the nearest rung — that would change geometry behaviour to fix
a display bug.

**R-cmb-2. Rebuild the ladder when the technology resolves, and when it is retargeted.** The list is derived
from `DefaultSnapDbu` and the display unit; both can arrive or change after the control is first bound.

**R-cmb-3. Rebuild when the display unit changes**, since the entries are formatted in it — 1 mil and 25.4 µm
are the same rung with two labels, and the combobox must relabel rather than blank.

Find where the ladder is constructed (it is not in `LayoutSnapping.cs`, which holds only the L1b snap math —
look in the layout metadata-bar view and its view-model) and **report the file**, so the next reader knows.

## 2. Bug: grid snap ignored during a move-drag when geometry snap is on

Edge drags already behave correctly; move-drags do not. The owner's proposal — make move behave like edge — is
right, and it is what R-snpf-3 already specified.

**R-cmb-4. In every drag: a geometry candidate in range wins; otherwise grid snap applies.** Geometry snap
overriding grid snap means *when it has something to offer*, not *whenever the mode is enabled*. A move-drag
that free-floats because no feature happens to be nearby is the bug.

**R-cmb-5. The two snaps act on different quantities, and that must be deliberate, not accidental.**

| | What is snapped |
|---|---|
| **Geometry snap** | an **absolute position** — the grab point lands exactly on the target feature |
| **Grid snap on a move** | the **delta**, not the position (R-L1c-3) — so a shape keeps its off-grid relationship to itself |

So within one gesture: candidate in range → the grab point is placed absolutely; no candidate → the delta is
grid-snapped. **Do not "unify" these by making the move snap its position** — R-L1c-3 exists because snapping a
moved shape's position quantizes its internal geometry, which is a regression the L1c work specifically avoided.

Edge drags snap a perpendicular offset, which is already a delta, so they need no change.

## 3. Change: geometry-snap glyphs 10% larger

**R-cmb-6. Increase the marker size by a further 10%.** It is screen-space sized (R-snp-4), so this is one
constant in `src/Ui/Renderers/LayoutRenderer.Snap.cs` — change it there and nowhere else. If the value has been
duplicated per glyph type, consolidate it while you are there.

## 4. Guardrails

- Do not round a document's `SnapDbu` to fit the ladder (R-cmb-1).
- Do not make move-drags snap position instead of delta (R-cmb-5).
- Do not scale glyphs with zoom — they stay screen-space.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 5. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Never blank (R-cmb-1)** — a `.clay` with `SnapDbu` at 0.5 mil opens with that value **shown and
   selected**; the ladder contains it in sorted position. Assert the selection is non-null for an off-ladder
   value, which is the actual defect.
3. **New workspace, new layout (R-cmb-2)** — creating a workspace then a layout shows a populated combobox with
   the seeded value selected. Assert after the technology resolves *and* immediately on open.
4. **Retarget and unit change (R-cmb-2/3)** — retargeting the technology repopulates the ladder with the
   selection preserved; switching display unit relabels every entry without blanking.
5. **Move-drag grid snap (R-cmb-4)** — with geometry snap **on** and **no** candidate in range, a move-drag
   snaps to the grid. With a candidate in range, it lands on the feature. Both asserted in the same test, since
   the bug is the transition between them.
6. **Delta not position (R-cmb-5)** — a shape whose vertices are off-grid, moved with grid snap active and no
   candidate, keeps its internal vertex spacing exactly; only its offset changes.
7. **Edge drags unchanged** — existing edge-drag snap tests still pass untouched.
8. **Glyph size (R-cmb-6)** — the rendered marker is 10% larger than before at a fixed zoom, and unchanged
   across zoom levels.

## 6. On completion

Record in `src/Ui/CLAUDE.md`: **which file builds the snap ladder**, and that it must always contain the
document's current value so the combobox cannot blank; that the **new-workspace case was a binding-order
problem** if that is what it proves to be; and **R-cmb-5** — that geometry snap places an absolute position
while grid snap on a move snaps the delta, so the two are deliberately different quantities within one gesture.
