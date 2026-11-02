# Sonnet Brief — L3a follow-ups: picker filtering, marquee selection, instance properties, cell drag-drop

Four owner items after L3a. Two of them (§1 and §4) share the same cycle-filter, and §3 is a display bug
against a model that is already correct.

---

## 1. The Place Instance picker must exclude cells that would create a cycle

Currently the picker lists the parent cell itself, which can only ever be refused by R-L3a-2's edit-time
check. Offering a choice whose sole outcome is an error message is a bad affordance.

**R-fix-1. Exclude the parent cell only. Everything else is offered, attempted, and refused with the cycle
message.**

Self-reference is obvious enough that a user will not wonder why it is missing — keep it out. A deeper cycle
is **not** obvious: if **A** instantiates **B**, silently omitting **A** from the picker while editing **B**
leaves the user hunting for a cell that appears to have vanished, with nothing on screen to explain it.
**Prefer a visible, explanatory error over an invisible absence.** So list those cells, let the user pick
one, and have R-L3a-2's edit-time check refuse it with the path named (`B → A → B`). The user learns *why*,
which a missing row can never teach them.

Same principle for a cell with **no layout view**: list it, but **disabled with the reason shown inline**
(R13a's spirit — visible and explained rather than absent), since instancing it would only ever resolve to a
permanent placeholder.

This makes R-L3a-2's refusal the primary user-facing mechanism rather than a last-resort guard, which suits
it — it already names the offending path.

## 2. Marquee must select instances, and selection must be allowed to mix

Marquee selects no instances at all today, arrayed or otherwise.

L3a's stated scope decision — instances get their own selection state, mutually exclusive with shapes — is
**correct for some operations and wrong for others**, and the split is worth drawing explicitly:

| Operation | Instances participate? |
|---|---|
| Marquee, click, Shift/Ctrl modifiers | **Yes** |
| Move, nudge, delete, cut / copy / paste, duplicate | **Yes, in a mixed selection** |
| Vertex / edge / bulge / control-point handles (L1d) | No — an instance has no vertices |
| Boolean ops, offset, flatten, repair (L1e) | No — an instance is not geometry |
| Scale-mode bbox handles (L1h) | No in this pass — L3a named numeric `Mag` only, and that stands |

**R-fix-2. A selection may contain both shapes and instances.** Mutual exclusivity is what makes the
reported bug unfixable in place: a marquee dragged over a region containing both would otherwise have to
silently pick one. For the shape-only operations above, a mixed selection **disables them with a reason**
(R13a) rather than partially applying — *"Boolean operations apply to shapes only; 2 instances selected."*

**R-fix-3. Marquee reuses the existing predicate, extended to instance bboxes.** L1i's one-predicate rule
stands: `ComputeMarqueeSelection` gains instance candidates but the enclose/crossing test, the
visibility/selectability filter, and the Shift/Ctrl combination against the base selection are unchanged.
Instance candidates come from the **same** L2b tree — R-L3a-4 already put them there as discriminated
entries, so this is a filter change, not a second query.

**The live preview must cover instances too** (L1i): an instance entering the marquee highlights before
release, and un-highlights on Ctrl-drag, exactly as shapes do.

**Arrays are one object.** A marquee touching any placement of a 50×50 array selects the array as a unit —
there is no such thing as selecting placement 37. Make sure the bbox used is the whole array extent, which
R-L3a-4 already stores.

## 3. Instances have no Layer and no Net — the panel is wrong, the model is right

`LayoutInstance` (`LayoutModel.cs` 222) carries `CellRef`, `X`, `Y`, `Rot`, `MirrorX`, `Mag`, `Rows`, `Cols`,
`PitchX`, `PitchY`, `SchematicId` — and **no `Layer`, no `Net`**. Nothing to remove from the model. The panel
shows them anyway:

- `LayoutShapePropertiesViewModel.cs:183` — `public bool ShowNet => !ShowBitmap;` is true for an instance.
- The Layer row is not gated at all.

**R-fix-4. Gate both rows on shape context.** `IsInstanceContext` already exists (line 591); make `ShowLayer`
and `ShowNet` require a shape selection, so an instance shows only its own section.

Record the reasoning in the code comment, because "should an instance have a layer?" will be asked again:
an instance paints on whatever layers its **sub-cell** uses, so it has no layer of its own; GDSII `SREF`
carries no layer either; and nets attach to conductor geometry and pins, not to a placement. **Nothing is
lost by removing them** — hiding layer M1 already hides M1 geometry *inside* instances, which is what a user
expects, and the sub-cell's port labels are what will carry nets when L5 lands.

## 4. Drag a cell from the project tree onto a layout

The schematic already does this and the payload is shared, so most of the work exists.

**What is already there:** `CellDragPayload` with `TryParse` off `DataFormat.Text`, and the project tree as a
drag source — that is how the schematic drop works today.

**What is not:** `LayoutCanvas` has **no `DragDrop` wiring whatsoever** — no `SetAllowDrop`, no handlers.
`SchematicCanvas` registers three pairs (palette, cell, image-file) at lines 233–240.

**R-fix-5. Mirror `SchematicCanvas.OnCellDragOver` / `OnCellDrop`**, with two substitutions:

- **Resolve through `CellLayoutResolver`** instead of `CellSymbolResolver`, and show a **real geometry
  ghost** when it resolves — the sub-cell's cached per-layer paths under the placement transform, which
  R-L3a-3 already builds. Fall back to a labelled bounding box when it does not, matching R-L3a-1.
- **Snap through the layout's `SnapDbu`**, not the schematic grid.

**R-fix-6. Drop routes through the same command path as the Instance tool.** One placement path, not two —
otherwise the cycle check, the undo entry shape, and the array defaults drift between them. The
**cycle check on drop is doing real work**, not duplicating a filter: §1 deliberately leaves every
non-parent cycle to be caught and explained at placement time, and drag-drop is simply a second way to reach
that same moment.

Set `DragEffects = None` in `DragOver` **only for the parent cell itself** — the one case obvious enough that
a "no" cursor needs no explanation. For any other cycle-forming payload, **accept the drop and refuse it with
the cycle message** (R-fix-1's principle): a silent "no" cursor tells the user nothing about why, and
"drag-and-drop is broken" is exactly the conclusion they would draw.

Register the handler so it **coexists with image-file drop** if that has landed or lands later — follow
`SchematicCanvas`'s pattern of separate handler pairs per payload kind rather than one handler that
branches.

---

## 5. Scope guardrails

- No scale-by-drag-handle for instances — L3a named numeric `Mag` only and that stands.
- No push-in/pop-out, edit-in-place, or `CellUsageScanner` work (L3b). No flatten or group-into-cell (L3c).
- No changes to `LayoutInstance`'s fields, to `.clay`, or to the L2b index structure.
- Do not weaken R-L3a-2 — §1 adds a filter, it does not replace the guard.
- Don't touch `src/Core`, `src/Engine`, `RfCore`, the schematic or symbol editors.

## 6. Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. **Picker (R-fix-1)** — the parent cell is absent; with `A` instantiating `B`, editing `B` **does** list
   `A`, and choosing it is refused with `B → A → B` named. A deeper `A → B → C` chain likewise lists `A`
   when editing `C` and refuses with the full path. A cell with no layout view is listed but disabled, with
   its reason visible.
3. **The guard still holds** — a cycle attempted through a path that bypasses the picker is still refused
   with the path named.
4. **Marquee selects instances (R-fix-3)** — a marquee over a lone instance selects it; over an array,
   selects the array as a unit from any placement; over a mixed region, selects both shapes and instances.
5. **Preview parity** — instances highlight live during the drag and un-highlight under Ctrl, and the
   preview at release equals the committed selection (L1i's invariant, now over both kinds).
6. **Mixed-selection operations** — move, nudge, delete, cut/copy/paste and duplicate all apply to shapes and
   instances together as **one** undo entry; booleans, flatten, repair and vertex handles are **disabled with
   a reason** naming the instance count.
7. **Properties (R-fix-4)** — an instance selection shows no Layer and no Net row; a shape selection still
   shows both; a bitmap still shows Layer but not Net.
8. **Drag-drop (R-fix-5/6)** — dragging a cell from the tree onto a layout shows a resolved-geometry ghost,
   snaps, and places on drop as one undo entry; an unresolvable cell shows the fallback ghost; dragging the
   **parent** cell shows a "no" cursor; dragging a **deeper** cycle-forming cell is accepted by `DragOver`
   and refused on drop with the path named.
9. **One placement path** — assert the drop and the Instance tool produce an identical `LayoutInstance` for
   the same cell and point.
10. **Schematic drag-drop is unaffected** — its existing tests pass unchanged.

## 7. On completion

Add an "L3a follow-ups" entry at the top of `src/Ui/CLAUDE.md` recording: that the picker excludes **only**
the parent cell, and that deeper cycles are deliberately left to R-L3a-2's refusal message so the user sees
**why** rather than finding a cell mysteriously absent — a stated UX principle, not an oversight;
**R-fix-2's mixed selection** and the table of which operations do and do not accept instances, superseding
L3a's blanket mutual exclusivity; that instances never had `Layer`/`Net` in the model and the panel was
simply ungated, plus **why** those concepts do not apply; and that layout cell drop reuses `CellDragPayload`
and the Instance tool's single placement path.
