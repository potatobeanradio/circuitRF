# Brief 6 — the network strip, the sliders, and the mirror button

**Series:** [Smith Chart](brief-smith-0-overview.md) · **Tag:** `R-smith6-n` · **Phase:** P1
**Area:** `src/Ui/Smith/`, `src/Ui/Views/Smith/` · **Depends on:** 4 · **Blocks:** 7
**Design note:** [`smith-chart.md`](../design/smith-chart.md) §5.5, §5.6, §3.3

---

## 0. What this brief delivers

The bottom pane: the cascade drawn as a real schematic, element add/insert/delete/reorder, the selected
element's sliders, and the mirror button. No clipboard (brief 7) and no chart (brief 5).

---

## 1. `R-smith6-1` — the drawing is a PROJECTION, and the renderer is the schematic editor's

`SchematicRenderer.Draw` — the renderer the schematic editor draws every frame with, below the firewall
since RND-1. This brief builds a `SchematicModel` from the element list and hands it over. **It does not
touch the renderer.**

`src/Ui/Match/MatchSchematicModel.cs` is the worked example and carries two findings that are this brief's
too:

- **one ground per COLUMN**, under its lowest shunt symbol — not one per shunt element, which put two
  grounds on one lead and none on another;
- **spine wires in the GAPS between series bodies**, not port-to-port, because a built-in glyph carries
  its own leads and a port-to-port line lays a second wire across every series body.

It is a **projection, not an editable schematic**: there is no selection model, no wire tool and no free
placement, because the topology is a list. Series elements take `MatchSchematicModel.SeriesRotation`
(`R270`); shunt elements sit at `R0` below the spine.

### `R-smith6-2` — the strip's operations

- **Selection** by click, on the symbol or its label. The selected element's sliders appear beneath.
- **Add ▾** appends at the end (nearest the load). **Insert ▾** places before the selected element. Both
  carry a menu of the whole vocabulary, each entry naming series or shunt. The same menu is mirrored under
  **Insert** in the shell menu bar (brief 4 `R-smith4-9`) — one command, two surfaces.
- **Delete** removes the selected element; the chain closes up.
- **Reorder** by drag along the strip, or by the `⇅` buttons.
- **Enabled** is a checkbox per element. A disabled element contributes nothing, draws no trajectory, and
  **keeps its values and its place**. It costs one boolean and it is the difference between trying
  something and losing it. Draw it dimmed, on the schematic's own `DisableState` convention.
- **Instance names** auto-assign per type (`L1`, `L2`, `C1`, `TL1`, …), are editable, are unique, and are
  what brief 7 carries into a real schematic.
- **Zoom to Fit is the default** on any change that alters the element count; the strip scrolls
  horizontally otherwise.

### `R-smith6-3` — a new element's defaults

`ComponentTypeRegistry` already declares defaults for every one of these types — use them, do not invent a
second set. The two that need a decision beyond the registry:

- a **TLIN's `F_ref` defaults to the design frequency at the moment it is placed**, and thereafter does
  **not** follow it (brief 2 `R-smith2-3`). The status strip says so **once**, the first time a design
  frequency change leaves a TLIN's `F_ref` behind;
- an **`S1P`/`S2P` opens a file picker on placement**, and an element placed with no file is refused
  rather than created — a file-less file element has nothing to evaluate.

---

## 2. `R-smith6-4` — the sliders

One row per settable parameter of the selected element — one for an L, three for an SRLC, two for a TLIN,
and **none at all for an `S1P` or `S2P`**, whose value is a file. Selecting one of those shows its file
reference, its port count and its frequency span, and nothing to drag.

Each row is a label, a **compact** slider (the negative-margin trick, so the row does not grow tall) and an
**`InlineEditText`** showing the value with its unit — brief 4 `R-smith4-5`, and *every* means the slider
range endpoints and the instance name too.

- **Logarithmic** over a range centred on the current value, one decade either side by default, for R, L,
  C and a TLIN's Z₀. **Linear** for an electrical length and for a Z1P's real and imaginary parts.
- The range is shown at the ends and is editable (right-click ▸ *Set range…*). **Typing a value outside
  the range re-centres it rather than clamping** — a typed number is an instruction, not a gesture.
- **Dragging is live**: every step re-evaluates and redraws, and **one drag is one undo entry** on brief 5
  `R-smith5-8`'s terms and for its reason. The Match Designer's slider write-back defect is the
  regression; the rule is that a control's write-back is not an edit.

### `R-smith6-5` — the active parameter

**The last-touched slider becomes the element's `ActiveParameter`** — which is what brief 5's gripper
drags. The active row is **marked**, so the connection between "the slider I just used" and "the handle on
the chart" is visible rather than remembered.

Defaults per the note's §3.3 table: `L` for an SRLC, `C` for a PRLC, `E` for a TLIN or stub, `Im` for a
Z1P, the single parameter otherwise.

---

## 3. `R-smith6-6` — the mirror button

Owner instruction. **One button on the strip's toolbar flips the drawing so the generator is on the right
and the cascade grows leftward.**

It is the schematic editor's own control taken verbatim, not re-drawn:

```xml
<Button Classes="SelectionBtn" Padding="6,3" ToolTip.Tip="Mirror Network  (M)">
  <mi:MaterialIcon Kind="FlipHorizontal" Width="16" Height="16"/>
</Button>
```

the same glyph the schematic and layout toolbars already carry for the same idea, so it needs no learning.

**It mirrors the drawing and nothing else.** Three halves, each with a wrong version:

- **The topology does not change.** Element 0 is still the one nearest the generator, the list order is
  untouched, and brief 2's recurrence runs exactly as before. The flag reaches only this projection, where
  it negates the direction the x-cursor advances.
- **The symbols mirror too, not just their positions.** Each projected component gets `MirrorX = true`,
  which `SchematicGeometry.LocalToWorld` already honours **for pin coordinates as well as the glyph**.
  This is not cosmetic: an `S2P`'s port-1 marking means something, and a symbol that kept its handedness
  while its position reflected would draw port 1 on the *load* side of a part whose file says otherwise.
- **The chart does not mirror, and there is no button offering to.** The Γ plane's orientation is fixed by
  physics — inductive above the real axis, capacitive below — and a mirrored Smith chart is a wrong one.

It is a **view setting**: it lives in the document's `View.MirrorNetwork`, it marks the document dirty, and
it is **one undo entry**, on the Data Display's own precedent that a persisted view change is undoable
(`PushAxesWindowChange`). A user who flips it by accident should be able to take it back with the key they
already use.

**Brief 7 depends on this flag twice** — the copy follows the mirror, and the paste's geometric end-rule
reads it — so expose it as a property, not as a private field on the view.

---

## 4. The gate

`tests/Ui.Tests/Smith/SmithNetworkStripTests.cs` — one test per claim:

1. **The projection is a real schematic**: a four-element cascade produces the expected component kinds,
   one ground per shunt column, and spine wires in the gaps — not across the series bodies.
2. **Mirroring is view-only** (`R-smith6-6`): flip, and assert **no element value, no node impedance and no
   chart point changed**; and that every component's `MirrorX` flipped and its pin world coordinates
   reflected — the symbols really turned round with their positions.
3. **Mirror is one undo entry** and a single Undo restores it.
4. **Insert places before the selection; Add appends**; delete closes the chain; reorder preserves values.
5. **A disabled element keeps its values and its place**, and is absent from the evaluator's node array
   (brief 2 `R-smith2-8`, asserted from this side).
6. **The active parameter follows the last-touched slider**, and a gripper drag then changes that
   parameter and no other.
7. **A slider drag is one undo entry**, *n* drags *n* undos (`R-smith6-4`).
8. **An `S1P`/`S2P` selection shows no slider rows** — the specification's "nothing for SNP".
9. **A TLIN placed at 2 GHz carries `F_ref = 2 GHz`**, and changing the design frequency afterwards leaves
   it alone and says so once.

---

## 5. What this brief must NOT do

- **No clipboard.** Brief 7, both directions.
- **No editable schematic.** No wire tool, no free placement, no selection model beyond "which element is
  selected". The topology is a list.
- **No change to `SchematicRenderer`.** If it needs one, that is a finding to record, not a change to make
  here.
- **No mirroring of the chart, and no button offering to.**
- **No second set of component defaults.** `ComponentTypeRegistry` has them.

---

## 6. On completion

Findings to `src/Ui/RESOLVED.md`. **Never a `CLAUDE.md`.**
