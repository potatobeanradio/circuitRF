# circuitRF — Symbol Editor rotate: about CENTER, 4× = identity (Claude Code / Sonnet)

The Symbol Editor rotate has a real bug: selecting primitives + pins at arbitrary positions and pressing **R**
four times does NOT return them to the start. Two compounding causes, both in
`Commands/Symbol/RotateSelectionCommand.cs` + the R-key handler. Fix: rotate about the selection's **center**
(consistent with the schematic editor's "rotate in place" feel), with a **stable** center so N×90° composes to
identity, and **no per-step pin rounding**. Firewall green; undo stays exact.

## Why it's broken today (confirmed on disk)
1. **Moving anchor.** The R handler builds a NEW `RotateSelectionCommand` per press, and the ctor recomputes
   the anchor as `(min-X, max-Y)` of the CURRENT bbox via `ComputeAnchor`. After each 90° turn the bbox sits
   in a new place, so each of the four presses rotates about a DIFFERENT point. Four 90° rotations about four
   different centers = net rotation 0° but a net **translation** → the shape returns to its original
   orientation but lands elsewhere. (Breaks identity for any non-square selection.)
2. **Per-step pin snap rounding.** `Execute` rotates each pin then `Snap()`s it to PinGrid=100 every step. If
   the selection isn't grid-aligned, each step rounds, and four roundings accumulate instead of cancelling →
   pins drift even with a fixed center.

## Target behavior
- Rotate the selected primitives **and** pins 90° CW about the **center of the selection's combined bounding
  box** (mirrors the schematic editor's in-place rotation).
- Pressing R four times returns every item EXACTLY to its starting position and orientation (identity).
- Pins remain on the connection grid P=100 at rest.
- Undo of a single R is exact (already is — keep the snapshot/closure restore).

## The fix

### Part A — stable center, captured once per rotation "session"
The center must NOT be recomputed from the post-rotation bbox each press. Two acceptable designs — pick the
simpler that fits the command model:

**Preferred — rotate about the bbox center, computed from the CURRENT geometry but using the center (not a
corner), AND keep it stable across a 4-press cycle.** The bbox CENTER is rotation-invariant under rotation
about itself: rotating the selection 90° about its own center leaves the bbox center fixed (for the combined
content the center maps to itself), so recomputing the center each press yields the SAME point every time —
unlike the corner, which moves. So switching the anchor from `(min-X, max-Y)` to the bbox **center**
`((min-X+max-X)/2, (min-Y+max-Y)/2)` makes each independent command rotate about the same fixed point, and 4×
composes to identity for the primitives automatically. 

Verify this invariant holds for YOUR bbox computation: after a 90° rotation about the center, recomputing
`ComputeBb` over the rotated primitives must give a bbox with the SAME center (it will, since 90° rotation of
a point set about a point maps the axis-aligned bbox to one with the same center). If for any primitive type
the bbox isn't symmetric about the rotation (e.g. arc sweep), capture the center once and reuse — see below.

**Fallback if recomputation isn't perfectly stable** (e.g. arc bbox asymmetry): capture the center ONCE at the
start of a rotation cycle and reuse it for subsequent presses. Implement by having the VM hold the rotation
center: on the first R press for a given selection, compute and store `_rotateCenterX/Y`; reuse it on
subsequent presses; invalidate (clear) it whenever the selection changes, a non-rotate mutation occurs, or the
tool changes. Pass the captured center into the command ctor instead of letting it compute one.

Change `ComputeAnchor` → `ComputeCenter` returning `((minX+maxX)/2, (minY+maxY)/2)` of the combined prims+pins
bbox, and rotate about that center. Update `RotateBy90About(p, cx, cy)` calls accordingly (the helper already
rotates about an arbitrary point — pass the center).

### Part B — pins: rotate exactly, don't round mid-cycle
Pins must stay on P=100 at rest, but per-step `Snap()` is what makes 4× drift. Fix so a full cycle is exact:
- If the **selection is grid-aligned** (center on the grid and pins on P), rotation about the center maps P
  positions to P positions with no rounding needed — `Snap()` becomes a no-op and 4× is exact. In that common
  case the bug disappears.
- If the selection is **not** grid-aligned, do NOT round each step (that's the drift). Instead rotate the pin
  about the center with full precision and snap ONLY for display/commit such that a 4-cycle still returns to
  the original. Simplest robust approach: rotate from the pin's **current** precise position each step WITHOUT
  Snap inside Execute; keep pins on P by snapping the pin's resting position only when the selection is
  grid-aligned. If you must snap a non-aligned pin, accept that pins land on P (intentional) but ensure the
  command's Undo restores the exact pre-rotation value (it already snapshots `_pinOldX/Y`), so the user can
  always Undo rather than rely on 4×R. Document this edge in the design note.
- Net rule: **remove the unconditional `Snap()` from the per-step pin rotation.** Either snap only when the
  result is meant to land on P (aligned case → no-op anyway), or rotate precisely and let Undo be the exact
  inverse. The 4×R=identity guarantee holds for the aligned case (the normal authoring case); the design note
  states non-grid-aligned selections may round to P on rotate (use Undo for exact reversal).

### Part C — keep undo exact (no change needed beyond Part B)
The existing snapshot closures (`_primRestores`) and `_pinOldX/_pinOldY` already restore exact pre-Execute
state. Leave them. Just ensure Execute's pin math no longer rounds in a way that can't be undone (it can be —
Undo writes the captured originals — so Undo stays exact regardless).

## Verify (manual)
1. Place 3–4 primitives of mixed types + 2 pins at arbitrary (grid-aligned) positions. Select all. Press R 4× →
   every item is back EXACTLY where it started, same orientation. 
2. Press R once → everything rotates 90° CW about the selection's visual center (in-place feel, matches
   schematic). 
3. Pins remain on the P grid after each rotation. 
4. Single R then Undo → exact restore. 
5. Repeat with a single primitive (center = its own bbox center) → R 4× identity. 
6. (Edge) A non-grid-aligned selection: R 4× returns prims to start; pins may snap to P — Undo reverses
   exactly. Confirm no runaway drift (no accumulating translation).

## Guardrails
- Rotate about the **bbox CENTER**, not a corner — this both matches the schematic editor and makes the center
  rotation-invariant so 4× composes to identity.
- **Remove the per-step unconditional pin `Snap()`** that causes cumulative drift; keep pins on P for the
  normal grid-aligned case; rely on exact Undo for the non-aligned edge.
- Don't recompute a MOVING anchor; the center is stable (or capture-once per cycle if any bbox asymmetry makes
  recomputation unstable).
- Keep undo exact (existing snapshots).
- One undoable `RotateSelectionCommand` per press; `NotifyChanged` in Execute and Undo (it does).
- Firewall green; `dotnet build`/`dotnet test` green; no regression to pin select/move/delete or primitive
  drag/resize.
- Update `docs/design/symbol-editor.md`: rotation is 90° CW about the selection bbox CENTER (matches schematic
  in-place rotation); N×90° composes to identity; grid-aligned selections keep pins on P with no drift;
  non-grid-aligned selections may snap pins to P on rotate (Undo reverses exactly).

*Exit: R rotates the selection 90° CW about its center (in-place, like the schematic editor); four presses
return primitives and pins exactly to start for grid-aligned selections; pins stay on P; undo remains exact.*
