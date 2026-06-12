# Brief A — Symbol Editor Grippers (resize-handle position + 4-corner snapping)

**Scope:** Symbol Editor only. Two changes + one removal. No schematic-editor changes.
**Architectural firewall:** all files here are UI-layer (`src/Ui`). Do not add Avalonia/Skia types to framework-free models. `SymbolEditorOverlay` and `SymbolGeometry` are framework-free — keep them so (plain numbers, no SKColor/SKPath).

---

## Read first (real names — read these before writing anything)

- `src/Ui/Renderers/SymbolEditorRenderer.cs` — `Draw(...)`, `DrawSelectionOverlay(...)`. The gripper is drawn from `overlay.ResizeHandle` (a single `(double X, double Y)?`) as a filled square at the **bottom-right**. This file was recently made bitmap-live-aware; do not regress that.
- `src/Ui/Schematic/SymbolEditorOverlay.cs` — the `SymbolEditorOverlay` record. Currently has ONE handle: `public (double X, double Y)? ResizeHandle { get; init; }`.
- `src/Ui/ViewModels/SymbolEditorViewModel.cs` — owns resize-gripper state and overlay rebuild. Find: the resize hit-test, the resize-drag handler, the `_isResizing` / resize-shift fields, and `RebuildOverlay()` where `ResizeHandle` is populated. (Grep for `ResizeHandle`, `Resize`, `gripper`, `HitTestGripper`.)
- `src/Ui/Controls/SymbolEditorCanvas.cs` — pointer routing into the VM (`OnPointerPressed/Moved/Released → _viewModel.OnPointer*`). It already calls `InvalidateVisual()` on every pointer move and syncs `_overlay` on `Overlay` PropertyChanged. You should not need to change wiring here, but read it to confirm.
- `src/Ui/Schematic/SymbolGeometry.cs` — `BboxOf(prim)`, `ScaleBy(prim, sx, sy, anchorX, anchorY)` (or current signature — confirm), `TranslateBy`. These are the framework-free geometry ops the resize uses.

Read the resize path end-to-end **before** touching it. The gripper bug and the new gripper share that path.

---

## Spine (do-not-violate)

1. The gripper(s) must render at the **live** (in-progress) bbox during a resize, not the pre-resize bbox.
2. Aspect-locked resize behavior for bitmaps must be preserved exactly (bitmaps already force aspect lock; do not change that).
3. Every resize is one undoable command (already true — do not split it).
4. No double-application of the offset: the live-resized primitive is the single source of the live bbox.

---

## Layer 1 — FIX: gripper renders in the wrong position during live resize

**Symptom:** while dragging the bottom-right gripper to resize a primitive (bitmap especially), the gripper handle does not track the corner — it lags at the pre-resize position.

**Root cause to confirm by reading:** `RebuildOverlay()` computes `ResizeHandle` from the **committed** primitive's bbox (`SymbolGeometry.BboxOf(prim)` on the un-scaled primitive). During a live resize the committed primitive hasn't changed yet (the scaled clone lives in `InProgressPrimitive` / a `resizeLivePrim`), so the handle is computed from stale geometry. Meanwhile the live image IS drawn at the scaled size (via the in-progress preview), so the handle visibly separates from the corner.

**Fix:** during an active resize, compute the handle position from the **live-scaled** primitive (the same scaled clone that drives the in-progress preview), not the committed one. Concretely:
- In the VM, when a resize is in progress, the overlay's `ResizeHandle` must be derived from `BboxOf(resizeLivePrim)` (the in-progress scaled clone), i.e. the bottom-right corner of the live bbox.
- At rest (no resize), keep computing it from the committed primitive's bbox as today.

**Gate 1:** Grab the bottom-right gripper of a bitmap and drag. The handle square stays pinned to the bottom-right corner of the image for the entire drag (not just at release). Repeat for a non-bitmap primitive (e.g. a Rect). Aspect lock for the bitmap is unchanged.

---

## Layer 2 — ADD: second gripper at the TOP-LEFT corner (4-side grid snapping)

**Goal:** add a second resize gripper at the **top-left** corner of the selected primitive that works exactly like the bottom-right one, but anchored at the opposite corner. With both, the user can snap a primitive to the grid on all four sides (bottom-right gripper grid-snaps the right+bottom edges; top-left gripper grid-snaps the left+top edges).

**Behavior of the top-left gripper:** it is the mirror of the bottom-right gripper. The bottom-right gripper holds the **top-left** corner fixed and moves the bottom-right corner. The top-left gripper holds the **bottom-right** corner fixed and moves the top-left corner. Same aspect-lock rules, same snapping, same single-command commit. (Schematic-editor analogue for reference only: `SchematicViewModel.HandleObjResizeDrag` computes a fixed anchor as `orig - size/2`; the symbol path uses `BboxOf` corners — keep the symbol path's own convention.)

**Required changes:**

1. **`SymbolEditorOverlay`** — add a second handle field next to `ResizeHandle`. Suggested:
   - rename is NOT required, but add `public (double X, double Y)? ResizeHandleTopLeft { get; init; }` and treat the existing `ResizeHandle` as the bottom-right handle. (Optionally also rename `ResizeHandle → ResizeHandleBottomRight` for clarity, but only if you update every reference in the renderer and VM in the same pass. If unsure, keep `ResizeHandle` as the BR handle and just add the new TL field — smaller blast radius.)

2. **`SymbolEditorRenderer.DrawSelectionOverlay`** — draw BOTH handles with the identical square-handle code. Factor the handle-drawing into a tiny local helper `void DrawGrip((double X,double Y) p){…}` and call it for each non-null handle. Do not duplicate the paint setup.

3. **`SymbolEditorViewModel`**:
   - **Populate both handles** in `RebuildOverlay()`. At rest: BR = bbox bottom-right, TL = bbox top-left, both from the committed primitive. During resize: both from the **live-scaled** primitive's bbox (Layer 1 rule), so whichever corner you're dragging tracks live and the opposite (anchor) corner also stays correct.
   - **Hit-test** both handles in the resize hit-test. The existing hit-test returns "is the press on the gripper" — extend it to report **which** corner (BR or TL) was hit. A small enum (`enum ResizeCorner { None, BottomRight, TopLeft }`) or a `bool` for which corner is fine — keep it local to the VM.
   - **Resize-drag math:** the existing handler anchors at the top-left corner and moves bottom-right. Add the mirrored case: when the TL handle is active, anchor at the **bottom-right** corner and move the top-left. Both go through the same `SymbolGeometry.ScaleBy` + snap + single-command commit. Reuse the existing aspect-lock logic (do not fork it).

**Gate 2:** Select a primitive. Two gripper squares appear — one at top-left, one at bottom-right. Dragging the bottom-right gripper resizes from BR (TL fixed); dragging the top-left gripper resizes from TL (BR fixed). Both track live (Layer 1). Both grid-snap. Resize is undoable as one step. Bitmaps stay aspect-locked from either corner.

---

## Layer 3 — REMOVE: the Text-primitive gripper

The Text primitive currently shows a gripper that does nothing. Remove it (we may re-add it later for text wrapping). In `DrawSelectionOverlay` / `RebuildOverlay`, ensure **no** resize handle is emitted when the single selected primitive is a `TextPrimitive`. Text keeps its existing dashed bbox-box highlight (the `prim is TextPrimitive` branch in `DrawSelectionOverlay`) — only the resize grip(s) are suppressed for Text.

Decide where to gate it: simplest is in the VM where `ResizeHandle`/`ResizeHandleTopLeft` are populated — skip populating them when the selected primitive is Text (and when nothing/zero or multiple primitives are selected, which is presumably already the case). Confirm the hit-test also returns "no gripper" for Text so a press on the old grip location does nothing special.

**Gate 3:** Select a Text primitive. No gripper squares appear. The Text selection box still shows. Pressing where the grip used to be does a normal select/drag, not a resize.

---

## Acceptance (all must hold together)

- BR gripper resizes from BR (TL anchored); TL gripper resizes from TL (BR anchored). ✅
- Both grippers track the live corner during the entire drag (not just at release). ✅
- Bitmap resize stays aspect-locked from either corner; vector primitives resize per existing rules. ✅
- Each resize is one undoable command. ✅
- Text primitive shows NO gripper. ✅
- Schematic editor unaffected; symbol-editor bitmap live drag/resize (recently fixed) still works. ✅

## Guardrails

- Do not change `SymbolGeometry.ScaleBy` semantics; if you think you need to, stop and explain why in your summary instead.
- Do not duplicate handle paint/setup code — one local helper, called per handle.
- Keep `SymbolEditorOverlay` framework-free (tuples of doubles only).
- Minimal diff: only the resize hit-test, the resize-drag handler, `RebuildOverlay`, the overlay record (one new field), and `DrawSelectionOverlay`. If you touched anything else, list it and justify.

## Scope fence (do NOT do here)

- No clipboard/export work (that's Brief B).
- No project-tree work (Briefs C/D).
- No cell/properties work (Brief E).
- Do not refactor the renderer's bitmap-live logic; it's correct.

## Exit / report

State: which files changed; whether you renamed `ResizeHandle` or added a sibling; the exact resize-drag math for the TL corner; and a one-line confirmation that you ran the 3 gates mentally against the final code. Flag anything you had to assume.
