# Sonnet Brief — Phase L3a: instances, resolution, arrays, and instance caching

**Design:** `docs/design/layout-view.md` §7 (hierarchy), §5.3 item 4 (instance path caching), §3.1
(`Instance` primitive), §2.4 (primacy resolution). **Consumes all of L1 and L2.**

**L3 is three briefs.** This is **L3a: make instances exist, resolve, render fast, and be selectable.**
Then **L3b** (push-in/pop-out navigation, edit-in-place, hierarchy save, `CellUsageScanner`) and **L3c**
(flatten one level / all levels, group-into-cell).

**On L2's one unmet target.** L2c closed everything except the full-extent frame (3.5–4.6× over at 50k,
13–15× at 500k) and correctly identified a tiled raster cache (L2d) as the only remaining lever. **L3 goes
first, deliberately**, for three reasons worth recording: §5.1's 500k row is the *pathological/imported*
case whose stated qualitative requirement — degrade via LOD, never freeze the UI — L2c now meets; hierarchy
is *how real designs reach those shape counts*, and §5.3 item 4 attacks exactly that population; and a tile
cache designed against a flat shape model would likely need rework once instances change what dirties a tile.
**Re-measure after L3** — 500k shapes reached via arrays is a very different cost profile from 500k unique
shapes, and L2d should be scoped against that number, not today's.

---

## 1. What already exists

`LayoutInstance` shipped in L0a and has never been used: `CellRef`, `X`, `Y`, `Rot`, `MirrorX`, `Mag`,
`Rows`, `Cols`, `PitchX`, `PitchY`, `SchematicId`. `LayoutView.Instances` exists. L1a explicitly skipped
rendering it and left a `// L3` marker. So the model is done — this phase makes it live.

Reuse, do not reinvent: `CellFolder.ResolvePrimary(cellDir, ViewType.Layout)` for primacy;
`CellSymbolResolver` as the structural template for `CellLayoutResolver`, including its three-state handling
(resolved / not-found / stale-and-reloading); `TechnologyCache`'s explicit-invalidation shape for the
sub-cell cache; and `MissingNamedPrimary`'s precedent for surfacing a broken reference as a warning that does
not block editing.

## 2. Resolution

**R-L3a-1. A missing or broken sub-cell never blocks editing and never renders as nothing.** Resolve through
`CellLayoutResolver`; on failure, render a labelled placeholder outline at the instance's stored extent (or a
default box if none is known), report once per distinct missing `CellRef` per load — not once per placement —
and keep the instance fully selectable and movable so the user can fix or delete it. An instance that
silently vanishes is the worst outcome; §2.4 already set this precedent for technologies.

Cache resolved sub-cells by path with explicit invalidation, mirroring `TechnologyCache`. **No
`FileSystemWatcher`** — L0c ruled that out deliberately and the reasoning is unchanged.

## 3. Cycles and depth — enforce at three points, not one

§7 says cycle detection must be enforced at edit time, not discovered at render time. That is necessary and
not sufficient.

**R-L3a-2. Cycles are rejected at edit time, detected at load time, and bounded at render time.** A `.clay`
can arrive from outside the editor — hand-edited, produced by a script, or imported in L4 — so the editor's
own guard cannot be the only one. Specifically:

- **Edit time**: adding an instance walks the reference graph and refuses a cycle with a message naming the
  path (`A → B → A`).
- **Load time**: resolving a hierarchy detects a cycle and marks the offending instance broken (§2, R-L3a-1)
  rather than throwing.
- **Render time**: a hard recursion depth cap (suggest 32) that stops descent and draws a placeholder. This
  is the backstop that turns a pathological file into a visible artifact instead of a stack overflow.

A depth cap is also needed for legitimate deep hierarchy, independent of cycles.

## 4. Rendering — this is where the phase earns its keep

**R-L3a-3. A sub-cell's geometry is built once and drawn once per placement under a matrix.** §5.3 item 4.
A 50×50 via array must cost **one** path build and 2,500 matrix draws, not 2,500 builds. Without this,
arrays are unusable and the phase has failed regardless of what else works.

**This composes cleanly with L2c's cache because of a decision already made there.** R-L2c-3 caches paths in
**shape-local** space specifically so they survive a change of frame origin. The same property makes them
reusable across placements: cache the sub-cell's per-layer paths in **cell-local** space, then concatenate
the instance transform per placement. Had L2c cached in path space, none of this would work — worth noting
in the completion write-up, because it is the second time that decision has paid.

**Transform**: translate + R0/R90/R180/R270 + mirror-X + magnification, composed into one `SKMatrix` per
placement and concatenated with the array offset. Hairline strokes stay hairlines under any matrix
(`StrokeWidth = 0`), so §2.3's contract survives magnification unchanged.

**Culling and LOD apply to instances too**: an off-screen placement is skipped by the L2b query, and a
placement whose whole extent falls below the LOD threshold draws as a single bounding-box mark rather than
descending into the sub-cell at all. §5.3's "instances below a size threshold render as their bounding box
outline only" is exactly this, and it is what keeps a deep hierarchy affordable at full extent.

## 5. Spatial index, bbox, and hit-test

**R-L3a-4. Instances live in the same index as shapes, with a discriminated entry.** L2b built one tree of
`(bbox, shapeIndex)`; extend the entry to identify shape-vs-instance rather than adding a second tree — every
consumer already filters candidates afterwards, and one tree keeps L2b's `EnsureFresh` freshness guarantee
as the single correctness mechanism.

**Instance bbox** = the resolved sub-cell's bbox, transformed, expanded across the array extent. An
unresolved sub-cell has no real bbox — use the placeholder extent and re-index when it resolves. Note that
this makes the index depend on resolution state; `EnsureFresh` must account for a resolution change, not
just a shape-count change.

**R-L3a-5. Clicking an instance selects the instance, not its contents.** Editing a sub-cell's geometry is
push-in, which is L3b. Hit-test descends into the sub-cell only far enough to decide whether the point is on
actual geometry rather than empty space inside the bbox — otherwise every array becomes one giant click
target covering its whole extent. Selection, move, delete, copy/paste and scale then operate on the instance
as a unit, through the existing commands.

## 6. Placement UX

An **Instance** tool: pick a cell (a dialog listing the workspace's cells with a layout view), then place
with a live ghost following the cursor, snapped, click to place, Escape to cancel — the same gesture
vocabulary as L1f's paste placement. Reuse that path rather than writing a second one.

**Array** is a property of a placed instance, edited in the properties panel: rows, cols, pitch X/Y, with a
live count. Rows = cols = 1 is an ordinary instance, so there is no separate "array tool."

Properties panel additions: cell reference (with a **re-target** button), rotation, mirror, magnification,
array fields.

## 7. Scope guardrails

- **No push-in/pop-out navigation, no edit-in-place, no hierarchy-save behaviour** (L3b).
- **No flatten, no group-into-cell** (L3c).
- **No `CellUsageScanner` extension** (L3b) — so Remove/Rename Cell does not yet see `.clay` instance
  references. Note the gap in the completion write-up so L3b inherits it.
- No net propagation across hierarchy, no LVS, no schematic-to-layout (L5).
- No tiled raster cache (the deferred L2d).
- No `.clay` format change — `LayoutInstance` already persists.
- Leave forward comments where L4 will need them: **GDSII has SREF/AREF natively, so export preserves
  hierarchy**, while DXF uses INSERT/BLOCK and Gerber has no hierarchy at all and must flatten.
- Don't touch `src/Core`, `src/Engine`, `RfCore`, the schematic or symbol editors.

## 8. Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. **Placement and render** — an instance of a cell with known geometry renders identically to that geometry
   drawn directly, for each of the 8 rotation/mirror combinations, verified by off-screen pixel comparison.
3. **Magnification** — a 2× instance renders at twice the size with hairline strokes still exactly 1 px.
4. **Arrays (R-L3a-3)** — a 50×50 array renders 2,500 placements; assert via counters that
   `PathsConstructed` for the array is **O(sub-cell shapes), not O(2,500 × sub-cell shapes)**. This is the
   phase's headline assertion.
5. **Array timing** — record frame time for a 50×50 array of a 20-shape cell and compare against 50,000 flat
   shapes. The whole point is that they should not be comparable.
6. **Missing sub-cell (R-L3a-1)** — renders a labelled placeholder, warns **once per distinct `CellRef`**,
   stays selectable and movable, and the layout still saves and reloads.
7. **Cycles (R-L3a-2)** — adding a cycle at edit time is refused with the path named; a `.clay` crafted with
   a cycle loads with the instance marked broken and **does not throw or overflow**; a 40-deep chain stops at
   the depth cap with a placeholder.
8. **Culling and LOD** — an off-screen placement is not descended into; a placement below the LOD threshold
   draws as one bounding-box mark, asserted via counters.
9. **Index freshness (R-L3a-4)** — after add, move, delete, array-change, undo/redo, and a **sub-cell
   resolution change**, a linear scan and an index query return the same instance set.
10. **Hit-test (R-L3a-5)** — clicking sub-cell geometry selects the instance; clicking empty space *inside*
    the instance bbox does not.
11. **Clipboard** — copy/paste an instance within and across layouts; `CellRef` resolves correctly or is
    reported broken in the destination.

## 9. On completion

1. Add a "Phase L3a — COMPLETE" entry at the top of `src/Ui/CLAUDE.md`. Call out: **R-L3a-3** and the
   measured array numbers from gates 4–5; that **L2c's shape-local caching decision is what made instance
   caching compose** (second time it has paid); **R-L3a-2's three enforcement points** and why edit-time
   alone is insufficient; **R-L3a-4** (one index, discriminated entries, and that `EnsureFresh` now depends
   on resolution state as well as shape count); and the **`CellUsageScanner` gap** L3b must close.
2. Report back before L3b (navigation and edit-in-place) is briefed.
