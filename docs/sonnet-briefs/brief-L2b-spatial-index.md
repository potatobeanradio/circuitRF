# Sonnet Brief — Phase L2b: spatial index, culling, and the marquee/hit-test fix

**Design:** `docs/design/layout-view.md` §5.2 R11 (spatial index), §5.1 (budget), §5.3 items 1 and 3.
**Consumes L2a's baseline** — read the baseline table at the top of `src/Ui/CLAUDE.md` before starting;
this brief is ordered by **measured** cost, not by the design doc's predictions.

**Scope is L2b ONLY: make work proportional to what is visible.** Per-shape path caching, LOD, and the
R8b merge tier are **L2c**.

---

## 0. What L2a measured, and what it changes

`ShapesExamined == ShapesDrawn == total shape count`, at every scale, every time. **There is no viewport
culling of any kind today**, and every consumer scans linearly. The consequences, ranked by measured cost:

| Rank | Cost | 50k | 500k |
|---|---|---|---|
| 1 | **Marquee preview**, per pointer move | 17–36 ms | **234–433 ms** |
| 2 | **Hit-test**, per single click | 10–32 ms | 100–330 ms |
| 3 | **Render** (full-extent / pan / zoom) | 42–251 ms | 647–2,554 ms |

**The marquee is the headline, and it is not close.** At 500k a single 100-move drag costs 23–43 *seconds*.
At 50k it is already 17–36 ms **per move** — past the frame budget on every move, meaning the marquee is
unusable well before the design doc's "dense MMIC cell" scenario is reached. It is a completely separate
O(N) scan with no relationship to the render pipeline, which is exactly why no frame-time measurement had
ever surfaced it.

**Set expectations honestly about what culling can and cannot fix.** Culling helps only when things are off
screen. It will do **almost nothing** for the 500k *full-extent* frame, where everything is visible by
definition — that number is LOD's job in L2c. L2b's render win shows up in pan and zoomed-in work, which is
where users actually spend their time.

---

## 1. The index

### R-L2b-1. One R-tree over all shapes, not one per layer

§5.2 suggests per-layer indices so hidden layers cost zero. **Deviate, and record why:** L2a's scenario is
200 layers. Per-layer means ~200 tree descents per query — ~2,200 node visits minimum even when the viewport
is empty — versus one descent into a single tree. It also multiplies incremental-maintenance work by 200 on
every edit.

Instead: **one R-tree whose entries carry `(bbox, shapeIndex)`**. Consumers filter the returned candidates by
layer visibility and selectability afterwards, which is trivial on an already-small result set. Hidden layers
then cost a predicate call per candidate rather than a scan.

**Bulk-load with STR (Sort-Tile-Recursive) packing** on build — O(n log n), and it produces far better node
quality than repeated insertion, which matters given L2a's deliberately clustered distribution.

### R-L2b-2. The index must never be stale, and there must be exactly one hook

A stale index means shapes that silently fail to render or cannot be selected — a confusing, hard-to-trace
failure. Every mutation path must maintain it: add, delete, replace, move, scale, paste, undo, redo,
technology retarget, resolution change, flatten, boolean ops.

`LayoutView.Changed` already exists and every command raises it via `NotifyChanged()`, so it is the natural
single hook — but it carries no payload, and a full 500k STR rebuild per edit is far too slow.

**Extend the change notification with a minimal payload** describing what happened (indices added, removed,
replaced) with an explicit "everything changed" case for load, retarget and resolution change. Incremental
insert/delete for the common path; full rebuild only for the bulk case. **One hook, not update calls
sprinkled through a dozen commands** — the sprinkled version is how one path gets missed.

Track insert/delete churn and **bulk-rebuild when quality degrades** past a threshold, since repeated
insertion degrades an R-tree over time.

**Drags do not churn the index.** Geometry is not mutated during a drag (pending state lives in
`Overlay.DragOverrides`), so no rebuild happens per pointer move — the index sees one update on release.
Verify this rather than assume it.

## 2. Consumer 1 — marquee preview (highest priority)

`LayoutEditorViewModel.ComputeMarqueeSelection` currently bbox-scans every shape per qualifying pointer move.

Query the R-tree with the marquee rect, then apply **the existing predicate unchanged**.

**R-L2b-3. The index changes which shapes are *considered*, never the decision.** L1i's whole design rests on
one predicate serving both preview and commit; if the indexed path and the commit path could disagree, the
highlight would lie about the outcome again. The enclose/crossing test, the visibility and selectability
filter, and the Shift/Ctrl combination against `_marqueeBaseSelection` all stay exactly as they are.

Note that **crossing mode** (right-to-left) needs candidates whose bbox *intersects* the marquee, and
**enclose mode** needs those *contained* — an intersect query serves both, with containment tested on the
candidates.

## 3. Consumer 2 — hit-test

`LayoutHitTest.HitStack` takes DBU and a tolerance. Query the tree with the point expanded by `tolDbu`, then
run the **existing** per-shape tests and the existing ordering (ZOrder descending → ascending area → list
index) on the candidates.

The tolerance is still computed per query from the live viewport (`pixels / zoom`) — the index does not
change that, and it must not become cached or index-derived.

## 4. Consumer 3 — render culling

`LayoutRenderer.Draw` currently walks every shape on every layer.

Query the tree with the viewport rect, **bucket the candidates by layer**, then draw layers in `ZOrder`
order exactly as now. The layer iteration order is the rendering contract (§2.3) and must not change.

- Hidden layers: skip their bucket entirely.
- Empty buckets: no draw calls, and `LayersVisited` should reflect that.
- The ghost overlay, selection outlines, handles and the marquee are **not** in the index — they are
  transient and drawn separately. Leave them alone.

**Do not add the sub-2px LOD drop here.** That is L2c, and mixing it in makes it impossible to attribute the
measured win.

## 5. Scope guardrails

- **No per-shape path caching, no LOD, no merge tier, no allocation rework** — all L2c. If the temptation
  arises, note it for L2c instead.
- No changes to the rendering contract: layer order, per-shape fills, batched opaque hairline strokes, and
  overlap darkening all stay exactly as they are.
- No changes to `LayoutFlattener`, `LayoutClipper`, or any command's semantics.
- No `.clay` format or load-path changes — L2a quantified the 219 MB / 873 ms case and it is explicitly out
  of scope here.
- Don't touch `src/Core`, `src/Engine`, `RfCore`, the schematic or the symbol editor.

## 6. Gate (acceptance)

Counters are the gate; wall-clock is reported (L2a's R-L2a-3 stands).

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses — in
   particular every L1 selection, marquee, hit-test and rendering test still passes unchanged.
2. **Culling works (counter assertion)** — at 500k zoomed into ~1% of the extent, `ShapesExamined` and
   `ShapesDrawn` are **O(visible), not 500,000**. This is the assertion L2a's counters were built for.
3. **Full-extent is unchanged** — at full extent `ShapesDrawn` is still ~total, and frame time is within
   noise of the L2a baseline. Culling must not *cost* anything when nothing is off screen.
4. **Identical output** — for a fixed viewport, the rendered bitmap is pixel-identical to the pre-index
   renderer across all three profiles. Culling changes speed, never pixels.
5. **Marquee correctness (R-L2b-3)** — for a suite of drags across all three modifier states and both
   directions, the indexed preview set is **identical** to the pre-index result. Then the L1i invariant
   again: preview at release equals the committed selection.
6. **Hit-test correctness** — `HitStack` returns the identical ordered list as the pre-index implementation
   for points inside shapes, near edges, in dense stacks, and in empty space. Ordering ties still break
   reproducibly by list index.
7. **Index freshness (R-L2b-2)** — after each of add, delete, replace, move, scale, paste, undo, redo,
   flatten, boolean op, technology retarget and resolution change, a full linear scan and an index query
   return the **same** shape set. Run this as a table-driven test over every mutation; one missed path is
   the whole risk of this phase.
8. **Drags do not rebuild** — assert the index is rebuilt/updated zero times across a 100-move drag, and
   once on release.
9. **Measured wins, recorded** — re-run L2a's harness and report the new marquee, hit-test, pan and zoom
   numbers beside the baseline. Marquee per-move at 500k is the headline; state it plainly whether or not
   it hits a target.
10. **Bulk-load quality** — build time for 500k is recorded, and a query over the clustered distribution
    returns O(visible) candidates rather than a large fraction of the tree.

## 7. On completion

1. Add a "Phase L2b — COMPLETE" entry at the top of `src/Ui/CLAUDE.md` with **the before/after table**
   (marquee per move, hit-test per click, pan, zoom, full-extent, at 50k and 500k), plus: the
   **single-tree-not-per-layer decision and its reasoning**, the change-payload hook and which mutation
   paths feed it, the rebuild-on-degradation policy, and confirmation that culling left rendered output
   pixel-identical.
2. State plainly which of §5.1's targets are now met and which still are not — the 500k full-extent frame
   is expected to remain over budget until L2c's LOD lands, and saying so is more useful than implying
   otherwise.
3. Report back before L2c (per-shape path caching, LOD, the R8b merge tier) is briefed.
