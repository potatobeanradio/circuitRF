# Brief N — Wire-drag connection-preservation regression tests

Small, self-contained: add **two** headless tests that lock in the two wire-drag fixes from this
session. No production code changes. The harness already exists — reuse it; do NOT build a new one.

## The invariant under test
**A component/cell port that was connected before a drag must still be connected after it** — and a
drag/merge must never silently fuse two wires in a way that drops a port off the net.

## What already exists (read these first; match their style exactly)
- `tests/Ui.Tests/DragInvariantOracleTests.cs` — whole-wire/component drags via
  `vm.SimulateDragCommit(dx, dy)`. Connectivity asserted with
  `model.BuildRenderModel()` → `render.Components.First(c => c.Id == …).Ports[i].State ==
  PortConnectionState.Connected`. Helper `MakeResistor(cx, cy)` → port0 (index 0) at `(cx, cy-200)`,
  port1 (index 1) at `(cx, cy+200)`; `Near(a,b)` = within 1.0. **Add test 1 here.**
- `tests/Ui.Tests/SegmentDragKeepsConnectionTests.cs` — segment drags via pointer events:
  `MakeVm(GridSnap=false)`, `DragSegment(vm, pressX, pressY, toX, toY)` (press/move/release),
  `HasDotAt(vm, x, y)` over `vm.RenderModel!.ConnectionDots`. **Add test 2 here** (add a `MakeResistor`
  identical to the one in `DragInvariantOracleTests`, or a small local equivalent).
- `tests/Ui.Tests/TJunctionStemFollowTests.cs` — already proves a *legitimate* stem (no component pin)
  still follows a dragged through-segment. **No new test needed; it must stay green** (our fix only
  skips stems whose junction is on a port, so these are unaffected). Just run it.

There is **no** segment-drag oracle method and you do **not** need one — drive segment drags through
`OnPointerPressed/Moved/Released` exactly as the two files above do.

## Test 1 — merge must not bury a port (whole-wire drag)
Add to `DragInvariantOracleTests.cs`. Two wires meet at a component pin; dragging one off must not
merge them into a single wire that drops the pin.

Setup:
- `R` via `MakeResistor(0, -200)` → its port1 (index 1, bottom) is at `P = (0, 0)`.
- `Wh` = wire `[(0,0), (400,0)]` (from `P` rightward).
- `Wv` = wire `[(0,0), (0,400)]` (from `P` downward).
- Select `Wv`; `vm.SimulateDragCommit(dx: 200, dy: 0)`.

Assert (post-fix):
- `R`'s port1 state is `Connected`.
- Both wires still exist (`model.Wires.Count == 2`) — the merge was suppressed.
- `Wh` is unchanged: still ends at `(0,0)` and `(400,0)`.

(Pre-fix this failed: `Wv`+`Wh` merged, normalize dropped the `(0,0)` joint, port went Unconnected.)

## Test 2 — a stem anchored at a pin must not be dragged off it (segment drag)
Add to `SegmentDragKeepsConnectionTests.cs`. This is the inductor/cell scenario, reduced to the
geometry that triggers it: a wire whose junction sits on a stationary component pin must stay put when
an adjacent segment is dragged.

Setup (build the post-first-drag L-state directly — deterministic, no need to replay the first drag):
- `RL` via `MakeResistor(0, -200)` → port1 (bottom) at `PL = (0, 0)`.
- `RR` via `MakeResistor(400, -200)` → port1 (bottom) at `PR = (400, 0)`.
- `Wh` = wire `[(0,0), (400,0)]` = `PL → PR`.
- `Wv` = wire `[(0,0), (400,0), (400,400)]` — the L: top at `PL`, **corner at `PR`**, dropping down.
  (Its segment index 1 is the vertical drop at `x=400`.)
- Drag `Wv`'s vertical drop segment leftward: `DragSegment(vm, 400, 200, 300, 200)`.

Assert (post-fix):
- `RR`'s port1 (the pin at `PR`) is `Connected` — i.e. `HasDotAt(vm, 400, 0)` OR the render port state
  is `Connected` (use whichever the file's style favors).
- `Wh` is unchanged: `model.FindWire(Wh.Id)!.Points` still starts `(0,0)` and ends `(400,0)` — it did
  NOT get dragged off `PR`.

(Pre-fix this failed: `Wh` was treated as a stem of `Wv`'s moving corner and dragged to `(300,0)`,
detaching `RR`.)

## Acceptance
- Both new tests pass on the current tree (they encode already-fixed behavior). ✅
- `TJunctionStemFollowTests` and `SegmentDragKeepsConnectionTests` still pass unchanged. ✅
- Full `Ui.Tests` suite stays green. ✅

## Guardrails
- No production code changes. No new test framework, base class, or oracle hook.
- Reuse existing helpers (`MakeResistor`, `DragSegment`, `HasDotAt`, `Near`, `PortConnectionState`).
- Keep each test to one scenario with clear assert messages, matching the surrounding files' style.
- Don't assert on exact jog geometry of the *dragged* wire (that's allowed to vary) — assert only the
  invariant: the previously-connected pin stays connected and the *stationary* wire is unchanged.

## Exit / report
List the two test names added and which file each went in. Confirm both pass and that
`TJunctionStemFollowTests` + `SegmentDragKeepsConnectionTests` remain green.
