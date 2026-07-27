# UI (Avalonia) — local conventions

Phase L2c — LOD, the merge tier, and path caching (brief-L2c-lod-merge-and-caching.md, 2026-07-27) —
COMPLETE, the last L2 brief. **Close the full-extent frame cost** — the one thing L2b's culling
structurally could not touch, because nothing is off-screen to cull when everything is visible.
**Result: substantially closed, not fully closed** — said plainly below, with the measured shortfall
and a concrete recommendation, per the brief's own explicit request.

**§0's own framing turned out to be exactly right, confirmed by measurement, not assumed:** the merge
tier is a 2-20% win (L2a), draw-call count is not the dominant cost, and LOD is the lever that can
actually move the full-extent number. Built and measured LOD ALONE first, before writing one line of
the merge tier or the path cache (gate 2's explicit ordering) — see the LOD-only table below.

## R-L2c-1 — aggregate sub-pixel shapes, never drop them

A shape whose on-screen bbox falls under `LodPixelThreshold` (default 2 device px, `LayoutRenderer.
DefaultLodPixelThreshold`) contributes a MINIMAL RECT — its own bbox, clamped up to at least 1 device
pixel per side, centered on its own bbox center — to one batched-per-layer fill path, instead of
building its real geometry. **Dropping was the correctness regression this phase existed to avoid**: a
dense copper pour made of thousands of sub-pixel shapes must still read as a filled region at full
extent, not empty space — a speed-up that silently deletes geometry is not a speed-up, it is a bug that
happens to run fast. `DenseClusterOfSubPixelShapes_RendersAsFilledRegion_NotEmpty` (3,000 scattered
10-DBU rects) is the direct regression gate; `SubPixelShape_NeverDropped_EvenAlone` pins the
degenerate single-shape case.

**Consequence, stated in the code so it is never later "fixed" back into per-shape compositing:**
sub-pixel shapes stop darkening on same-layer overlap once aggregated — imperceptible at that zoom
(the same argument §2.3 R8b already makes for the merge tier), and correct by construction: they share
the exact same one-fill-per-layer path R8b's own aggregate uses.

## R-L2c-2 — one mechanism, two triggers (LOD and the merge tier are the same code path)

`LayoutRenderer.DrawLayer` builds exactly ONE `aggregate` `SKPath` per layer per frame. Two independent
per-shape/per-layer conditions feed it:
- **Sub-pixel** (R-L2c-1, above) — a PER-SHAPE decision, checked first, applies at ANY zoom regardless
  of layer density. A dense small-feature layer with only 50 shapes total still benefits the moment
  those shapes individually go sub-pixel, well below any shape-count threshold.
- **Layer shape count** (R8b) — a PER-LAYER decision: `candidateCount > MergeShapeCountThreshold`
  (default 2,000, `LayoutRenderer.DefaultMergeShapeCountThreshold`) or `ForceMergeTier` (the mechanism
  for R8b's "a preference forces merge permanently" — the render-option exists; wiring an actual
  Settings toggle to it is a small, explicitly deferred follow-up, not required to close the perf gap
  this phase targets). Shapes that merge this way still get their REAL geometry built (just added to
  the shared aggregate instead of drawn/composited alone) — merging is now a UX decision (R8a's
  overlap-darkening feedback), not a performance one; L2a already found merged fills cheaper than
  per-shape darkening at every density tested (500 to 100,000 shapes/layer, 1.02-1.21×, no crossover to
  discover). 2,000 is set where a genuinely dense layer's per-shape darkening feedback has already
  become visual noise rather than information, not at a performance cliff — there isn't one.

**Gate 6's proof is structural, not a pixel comparison** (a pixel comparison between a sub-pixel-sized
aggregate and a normal-sized one is thrown off by the FIXED-device-pixel stroke dominating the tiny
one — found this the hard way writing the test, fixed by comparing what actually matters):
`SubPixelTrigger_And_CountTrigger_BothCollapseDrawCallsToOLayers_SameStructuralSignature` shows BOTH
triggers collapse `DrawCalls` to exactly 2 (one aggregate fill, one batched stroke) for a single layer,
while the same shapes rendered individually (merge forced off) issue one draw call per shape — proving
one shared mechanism, not two implementations that happened to converge.

## R-L2c-3 — the path cache is keyed to SHAPE-LOCAL space, not path space (the trap)

`LayoutPathCache` (`src/Ui/Renderers/LayoutPathCache.cs` — Skia, lives in Renderers, never in the
framework-free `Layout/` folder) caches each shape's fill path relative to **its own DBU bbox
minimum**, reusing `LayoutRenderer.PathSpace` (now `internal`, was `private`) with the shape's own bbox
min as the origin instead of the per-frame viewport-anchored one L1a's R-L1a-1 uses. **The trap this
avoids, spelled out because it is exactly the kind of mistake that looks correct until you pan:** path
space's origin is quantized and re-picked near the viewport centre on almost every pan
(`ComputeOrigin`) — a path cached directly in path space would be wrong (or about to be invalidated)
after nearly every frame, doing net harm. A shape's own bbox min never moves unless the shape's
geometry itself changes, so the cached path survives ANY number of pans/zooms untouched; at draw time,
`canvas.Save(); canvas.Translate(ps.X(refX), ps.Y(refY)); canvas.DrawPath(localPath, fillPaint);
canvas.Restore();` reproduces the exact current-frame position — the same affine-in-DBU algebra that
makes path space itself work (`ps.X(dbu) = (dbu-origin)×dbuToUm`) guarantees `ps.X(dbu) == local.X(dbu)
+ ps.X(refX)` for ANY reference point, so the translate is exact, not approximate.

`CachedShape_PixelIdentical_AfterPanThatChangesPathSpaceOrigin` is the direct regression gate — it
explicitly ASSERTS (not assumes) that the pan it performs actually changes `ComputeOrigin`'s quantized
result, so the test cannot pass vacuously by accidentally panning too little.

**The cache serves ONLY the individual (per-shape darkening) tier — deliberately not the aggregate
tier**, per the brief's own explicit instruction ("per-shape matrices are incompatible with batching
into one path"): a sub-pixel or merge-triggered shape's geometry is built fresh into the shared
aggregate every frame regardless of whether a cache is configured; only shapes that reach the
individual tier (comfortably-sized, layer not merging) are cache candidates.

**Bounded, LRU (R-L2c-4).** `LayoutPathCache(capacity)` evicts the least-recently-drawn entry when over
capacity, disposing its native `SKPath` explicitly (never relying on the finalizer for Skia's
unmanaged buffers). Invalidation rides on L2b's `LayoutChangeInfo` — no second notification path:
`Full` clears everything, `RemovedTrailing`/`Updated` evict exactly the affected indices, `Appended`
needs no action (new indices have nothing cached yet). `LayoutCanvas` owns one cache per bound
document (fresh instance whenever `ViewModel` changes — cache entries are keyed by shape INDEX, which
is only meaningful within the ONE `LayoutView` it came from), invalidated from the SAME `Model.Changed`
subscription that already drives repaint. One-shot export renders (`LayoutClipboard`'s transient
views) and every existing test pass `PathCache = null` (the default) and get exactly L2b's behavior —
paths built fresh every call, zero risk to the ~2,300 pre-existing tests.

## The numbers

Same headless `SKSurface` harness, `SyntheticLayoutGenerator`, full-extent view (the ONLY scenario L2b's
culling could not help, by construction — everything is visible, so there is nothing to cull).

### LOD alone (gate 2's key decision point — merge tier and path cache both explicitly disabled)

| | 50,000 shapes | 500,000 shapes |
|---|---|---|
| Manhattan  | 62.0ms | 621.5ms |
| CurveHeavy | 71.9ms | 789.6ms |
| Mixed      | 69.9ms | 694.9ms |

Every profile: `PathsConstructed = 0` (no full geometry built anywhere — every candidate aggregated),
`DrawCalls` collapsed to ~400 (Manhattan/CurveHeavy: 200 layers × 2) or a few thousand (Mixed: labels
still draw individually, excluded from LOD/merge on purpose — text isn't compositing geometry).

### Final — all three items engaged (merge tier + path cache; cache is empty here, see below for why)

| | 50,000 shapes | 500,000 shapes |
|---|---|---|
| Manhattan  | 57.8ms | 656.3ms |
| CurveHeavy | 75.8ms | 757.3ms |
| Mixed      | 63.6ms | 700.9ms |

**The merge tier and path cache add nothing at full extent, measured directly, not assumed** — at full
extent every candidate is ALREADY sub-pixel (a 2-20µm shape against a 100mm×100mm design, viewed all at
once, is always far under 2 device px), so LOD aggregates everything before either mechanism gets a
turn: the "Final" cache's own `Count` is 0 in every full-extent run. This is expected, not a bug — §0
already predicted the merge tier and cache would not close THIS particular gap; they were built anyway
because they matter for other scenarios (below), and gate 2's own measurement is what proves it.

### Before (L2b) → after (L2c final), full-extent darkening path

| | 50k, L2b → L2c | 500k, L2b → L2c |
|---|---|---|
| Manhattan  | 75.9ms → 57.8ms (1.3×) | 737ms → 656ms (1.1×) |
| CurveHeavy | 237.6ms → 75.8ms (**3.1×**) | 2,554ms → 757ms (**3.4×**) |
| Mixed      | 99.3ms → 63.6ms (1.6×) | 1,103ms → 701ms (1.6×) |

**LOD did exactly what §0 predicted: it eliminated the curve-tessellation-specific cost.** CurveHeavy
went from the WORST profile (3× Manhattan, per L2a's own finding) to statistically indistinguishable
from Manhattan's remaining cost (757ms vs 656ms at 500k) — because once a shape is sub-pixel, its
ORIGINAL complexity (a `PathShape`'s 3-`SKPath` `BuildPathOutline` cost, an arc's tessellation) never
gets paid at all; only its bbox is ever computed. That is a real, structural win, not a measurement
artifact.

**It does not close the gap to §5.1's targets.** 16.6ms (50k) / 50ms (500k):

| | 50k over budget | 500k over budget |
|---|---|---|
| Manhattan  | 3.5× | 13.1× |
| CurveHeavy | 4.6× | 15.1× |
| Mixed      | 3.8× | 14.0× |

**The residual cost is now profile-INDEPENDENT — direct evidence of what it actually is.** Manhattan
(which never had a curve-tessellation cost to eliminate) is still 13× over budget at 500k, essentially
matching CurveHeavy's 15× after LOD closed CurveHeavy's own gap. With `PathsConstructed = 0` across the
board, the remaining ~620-790ms at 500k cannot be geometry construction — it is the cost of iterating
500,000 shapes to compute a bbox and add a minimal rect each, and (the larger piece, by elimination)
**rasterizing an aggregate path containing up to 500,000 individual rects, once, however batched the
draw call is.** Neither the merge tier nor the path cache can touch this: both are per-shape/per-frame
CPU-side optimizations, and this cost is GPU/rasterizer-side, paid once per frame regardless of how the
geometry was assembled.

### Path cache — a real, separate win, at a realistic (zoomed-in, not full-extent) scale

Full extent is not the scenario the cache targets — repeated frames at a shape-heavy but comfortably
zoomed-in view are (§3's own framing: "repeated frames, not the first one"). Zoomed into one of
`SyntheticLayoutGenerator`'s dense clusters (CurveHeavy, 500,000 total shapes, ~8,918 in view, each
individually above the LOD threshold): **40.5ms median** per frame after the cache warms, `hit=26,754
miss=8,918` across 3 measured frames — i.e. every candidate after the first frame is a cache hit,
exactly the "cold first frame, fast repeats" signature §3 promised. This is well within an interactive
feel (though still 2.4× over the strict 16.6ms budget for THIS specific dense-cluster view — a smaller,
more typical view would clear it).

**Memory, reported honestly, including the measurement's own limitation:** process working-set delta
for those ~8,918 cached entries was ≈94MB (≈11KB/entry). This is a WORKING-SET measurement
(`Process.WorkingSet64` before/after), not a clean per-`SKPath` allocation count — `GC.
GetAllocatedBytesForCurrentThread` (used elsewhere in this project's benchmarks) only captures MANAGED
bytes, and an `SKPath`'s real cost is native Skia point-buffer memory, which working-set delta captures
but also mixes in with JIT, one-time Skia/font initialization, and GC-heap noise from the same
measurement window. Treat 11KB/entry as an order-of-magnitude signal, not a precise number. At the
default capacity (50,000 entries), this bounds the cache to roughly the same order of magnitude as
"the whole L2a benchmark harness's own steady-state memory," which is the right ballpark for a
per-document cache; `LayoutCanvas` can override the capacity per document if a specific workflow needs
a different bound. Gate 9's bound itself held throughout (`cache.Count <= capacity`, always, by
construction — `EvictIfOverCapacity` runs after every insert).

## Is Phase L2 complete against §5.1? Stated plainly, not implied.

**No — full-extent at 50k and 500k is still over budget, by 3.5-4.6× and 13-15× respectively, even
after all three L2 phases.** That is the one target L2 does not meet. Everything else L2 set out to fix
is fixed:

- **1k (typical PCB cell): met**, trivially, throughout L1/L2.
- **Hit-test and marquee at any scale: solved (L2b)** — 1,000-7,000× faster hit-test, a realistic local
  marquee selection down from seconds to ~100-150ms per 100 moves at 50k-500k.
- **Pan/zoom at a realistic editing zoom (not full-extent): substantially improved (L2b culling + L2c
  LOD/cache together)** — the dense-cluster path-cache measurement above (40.5ms for ~8,918 visible
  shapes, cache warm) is the closest proxy this harness has to "actually panning around editing a dense
  cell," and it is close to, though not fully inside, budget.
- **Full-extent at 50k/500k ("dense MMIC cell" and "pathological full chip"): NOT met.** LOD delivered
  a real, measured 1.1-3.4× win and — critically — DOES degrade gracefully exactly as §5.3 item 3
  requires (density preserved, never an empty/frozen frame), but the residual per-frame rasterization
  cost of up to 500,000 shapes is not something any CPU-side per-shape mechanism (caching, batching,
  merging) can close further, because it is no longer CPU-side per-shape cost.

**What it would take: a tiled raster cache (L2d), not more per-shape optimization.** §5.3 already named
this as the deferred option "until measured" — it is now measured. The residual cost is "rasterize up
to 500,000 shapes' worth of pixels, once, every single frame, even when panning across an UNCHANGED
picture." A tiled cache (re-rasterize only the tiles a pan/edit actually dirties, reuse cached tile
bitmaps for everything else) is the direct answer to exactly that shape of problem, and is the
recommended next step if closing the full-extent number is still a priority — with the 13-15× shortfall
at 500k as the evidence behind that recommendation, not a guess.

## Scope discipline held

No changes to `src/Core`, `src/Engine`, `RfCore`, the schematic or symbol editors. No instance path
caching (instances do not exist until L3). `.clay` load time/file size untouched (L2a's own scope,
still explicitly out of L2b/L2c's rendering concern). No tiled raster cache started — named as the
recommended L2d, not built speculatively, per the brief's own "do not start it here."

Test files: `LayoutLodMergeTests.cs` (gates 3-6 — density preservation incl. the lone-shape degenerate
case, pixel-identical-when-not-engaged incl. R8a darkening staying intact, counter collapse for both
the all-sub-pixel and the few-normal-shapes cases, the structural one-mechanism proof),
`LayoutPathCacheTests.cs` (gates 7-8 — the pan-changes-origin regression gate with an explicit
non-vacuous-pan assertion, a multi-pan cache-stays-warm test, and one test per `LayoutChangeInfo` kind
incl. a full render-level round trip proving a geometry edit's cached result matches a fresh build),
`LayoutPerf/LayoutLodMergeCacheBenchmarkTests.cs` (gates 2, 9, 10 — LOD-only and final full-extent
timing at 50k/500k for all three profiles, the dense-cluster cache memory/timing measurement). 29 new
tests; 2340 Ui.Tests total (was 2320), all green, confirmed stable across 3 consecutive runs; full
solution green (Firewall 4/4, Core 388/388, Engine 461/462 — 1 pre-existing skip, matching the L2b
baseline exactly). Every one of the ~2,340 tests — including every L1/L2a/L2b pixel-oracle test written
before LOD/merge/caching existed — passed against the new code on the FIRST run, with zero test edits;
LOD is on by DEFAULT (unlike the path cache, which is opt-in via a non-null `PathCache`) precisely
because R-L2c-1 makes it a correctness-preserving optimization, and this is the evidence that holds.

**Next: L3 (hierarchy)** — instances, sub-cell references, the array (rows×cols) case, and whatever
that phase's own brief scopes for instance path caching (deferred here explicitly because instances
don't exist yet). If closing L2's one remaining gap (full-extent at scale) becomes a priority before
L3, that is the tiled-raster-cache brief (L2d) described above, with the measured shortfall already in
hand. Report back either way before the next brief lands.

Phase L2b — spatial index, culling, and the marquee/hit-test fix (brief-L2b-spatial-index.md,
2026-07-27) — COMPLETE: **make work proportional to what is visible.** One R-tree over every
`LayoutView.Shapes`, STR bulk-load on build, incremental insert/remove for the hot interactive paths,
and all three L2a-identified consumers (marquee, hit-test, render) now query it instead of scanning
linearly. Per-shape path caching, LOD, and the R8b merge tier remain L2c.

**R-L2b-1 — one tree, not one per layer, and why: L2a's own scenario is 200 layers.** A per-layer index
means ~200 tree descents per query (~2,200 node visits minimum even for an EMPTY viewport) and
multiplies incremental-maintenance work by 200 on every edit. `LayoutSpatialIndex`
(`src/Ui/Layout/LayoutSpatialIndex.cs`) holds one tree of `(bbox, shapeIndex)` entries across every
shape regardless of layer; every consumer filters the returned candidates by layer visibility/
selectability AFTERWARD — trivial on an already-small result set, and hidden layers now cost a
predicate call per candidate rather than a scan.

**Two independent correctness mechanisms, deliberately layered — this is the load-bearing design
decision of the whole phase.** `LayoutSpatialIndex.Apply(shapes, info)` is the proactive, incremental
maintenance hook (see below) — a PERFORMANCE optimization, not a correctness requirement.
`LayoutSpatialIndex.EnsureFresh(shapes)`, called at the start of EVERY query, is the actual guarantee:
a cheap O(1) check (`shapes.Count != _syncedCount`) that does a full STR rebuild whenever the index is
stale OR has never been built. **This is what let ~2,300 pre-existing Layout tests pass unchanged, on
the first run, with zero test edits** — the overwhelming majority build a `LayoutView` via direct
`Shapes.Add(...)` and never call `NotifyChanged` at all (confirmed by search before writing a line of
index code); the first query against such a view just does one lazy full rebuild and is correct from
then on for that instance. `Apply` could be deleted entirely and every query would still be correct —
only slower, paying a full rebuild on every single edit instead of an O(log n) incremental update.
**Documented limitation, not a theoretical one:** the count-based check does not catch a shape mutated
in place at a stable index without going through `NotifyChanged` — but every mutation path in this
codebase already calls it (that is how the canvas repaints at all), and a dedicated search across every
Renderer/HitTest/Marquee/Selection/Viewport test file before this phase confirmed no test does this
either.

**`LayoutChangeInfo`** (`src/Ui/Layout/LayoutChangeInfo.cs`) is the minimal payload R-L2b-2 asks for —
`Full` (rebuild), `Appended`/`RemovedTrailing` (a contiguous run at the TAIL — safe because nothing
after it exists to shift), `Updated` (in-place swap at stable indices — safe because `Shapes.Count`
never changes). `LayoutView.NotifyChanged(LayoutChangeInfo? info = null)` defaults `null` to `Full` —
**"one hook, not update calls sprinkled through a dozen commands" is literally true**: only 4 of the 7
command files needed ANY change (`AddShapeCommand` → `Appended`/`RemovedTrailing`, always safe because
under a LIFO undo/redo stack a command's own insert/remove can only ever run when nothing pushed after
it still exists — proven, not assumed, in that file's own doc comment; `MoveShapesCommand` → `Updated`,
refactored to store indices instead of shape references since geometry mutation never shifts anything;
`ReplaceShapeCommand` → `Updated`, a straight 1-index swap; `ReplaceShapesCommand` → `Appended`/
`RemovedTrailing` ONLY when its removed set is empty, i.e. paste/duplicate — any actual removal shifts
other shapes' indices in a way this command does not track precisely, so it falls back to the `Full`
default). `DeleteShapesCommand`, `SetShapeFieldCommand<T>`, `RetargetTechnologyCommand` needed **zero**
changes — they already call `NotifyChanged()` with no payload, which is `Full`, which is always correct
for them (deletion shifts arbitrary indices; a property-panel field edit is interactive-scale and
infrequent; retarget only rewrites `LayerKey`, which the index doesn't even key on). **Rebuild-on-
degradation (R-L2b-2):** `Apply` tracks churn since the last full rebuild and triggers one when churn
exceeds `max(2000, currentCount/4)` — repeated incremental insertion (a cheap "sort-the-wider-axis-and-
bisect" split, not Guttman's quadratic PickSeeds — correctness over optimality, since this policy is
the quality backstop) degrades node quality over time; this restores it periodically rather than
letting it degrade indefinitely. Confirmed by a dedicated churn test, not assumed.

**Drags do not churn the index — verified, not assumed (R-L2b-2's explicit instruction).** Geometry
during a live move/scale/handle-drag preview lives entirely in `Overlay.DragOverrides` (translated
clones); the real shape objects, and therefore the index, are untouched until commit. A dedicated test
drives a 100-move drag and asserts `LayoutSpatialIndex.FullRebuildCount`/`IncrementalApplyCount`
(internal, test-only counters) are UNCHANGED across all 100 moves, then exactly one incremental
`Apply` on release.

**A real correctness gap this uncovered, fixed before it could ship as a bug:** during a live drag, the
index still reflects each dragged shape's PRE-drag bbox for the WHOLE gesture (that's the point of the
paragraph above) — so culling `LayoutRenderer.Draw`'s candidate set from the index ALONE would wrongly
cull a shape dragged from off-screen INTO view, since the index has no idea it moved. Fixed by
force-including every `Overlay.DragOverrides` key in the render candidate set regardless of what the
index query returns — drag selections are always small (bounded by what the user selected), so this is
cheap, and a dragged shape whose LIVE position is actually off-screen is still correctly invisible (the
canvas clip rect discards it, exactly as it always would have). A dedicated regression test proves this
concretely: a shape starting off-screen, given a `DragOverrides` entry moving it on-screen, still
renders. Found by reasoning through the design before writing the render-culling code, not by a test
failure — but pinned by one anyway.

**Consumer 1 (marquee, highest priority per L2a's ranking) — `ComputeMarqueeSelection`.** Queries
`Model.SpatialIndex.QueryIntersecting(Model.Shapes, marqueeBb)`, then applies the EXISTING predicate
(enclose = full containment, crossing = intersection, both against the real `LayoutGeometry.BboxOf`)
completely unchanged on the candidates — R-L2b-3: "the index changes which shapes are considered, never
the decision." One intersect query correctly serves both marquee modes: containment implies
intersection, so an intersect query is always a safe superset.

**Consumer 2 (hit-test) — `LayoutHitTest.HitStack`.** Queries the point expanded by `tolDbu` (computed
per-call from the live viewport by the caller, exactly as before — never cached, never index-derived),
then runs the byte-for-byte identical per-shape test and ZOrder/area/index ordering on the candidates.

**Consumer 3 (render culling) — `LayoutRenderer.Draw`.** Queries the viewport's visible-world rect,
expanded by a device-pixel margin (`RenderCullMarginDevicePixels = 8`, converted to world units at the
CURRENT zoom via `vp.Zoom`) — the batched per-layer hairline stroke extends a pixel or two beyond the
exact fill bbox the tree stores, and antialiasing softens a little further; without this margin, a
shape sitting just outside the viewport but whose STROKE would still paint into it could be wrongly
culled, breaking gate 4's pixel-identical requirement. Candidates are bucketed by layer and drawn in
`ZOrder` exactly as before — the rendering CONTRACT (§2.3: layer order, per-shape fills, batched opaque
strokes, overlap darkening) is completely unchanged, only which shapes reach that pipeline. Bitmaps are
ALSO culled through the same one query (not a second one) — `DrawBitmapShapes` now iterates candidates
instead of every shape; R-bmp-2's "always paint first, regardless of ZOrder" rule is unaffected, since
that governs paint ORDER among whatever bitmaps are actually candidates.

**`ConservativeBboxOf`** (`LayoutSpatialIndex`, internal) is the ONE bbox notion stored in the tree —
identical to `LayoutGeometry.BboxOf` for every shape kind except `LabelShape`, where it is a generous,
rotation- and font-metrics-agnostic `(charCount+1)×height` square pad around the anchor point. This
matters because `LayoutGeometry.BboxOf`'s own label case is a ZERO-SIZE POINT (correct and unchanged
for the marquee predicate, which re-applies it verbatim on candidates), while `LayoutHitTest`'s
approximate footprint and the renderer's real-font-metrics footprint are two DIFFERENT, narrower
notions — the index only needs to be a safe UPPER bound of whichever one a consumer actually uses, and
over-inclusion is free (filtered downstream by each consumer's own exact test); a query rect padding
choice can never fix an UNDER-sized stored bbox, so the margin has to live in the stored value here,
not the query.

**Unknown-layer detection is now scoped to what is actually in view, a deliberate and stated trade-off.**
`LayoutRenderResult.UnknownLayers` used to scan every shape regardless of viewport; it now only sees
candidates from the culled query, so a layer that is entirely off-screen in a given frame will not
trigger its "unknown layer" warning until a frame actually brings it into view. This does not affect
gate 4 (pixels), and is arguably the more useful behavior (warn about what's rendering, not what
theoretically exists somewhere in a 500k-shape file) — flagged explicitly rather than left as a silent
side effect.

## The before/after table (R-L2a-7 convention, R-L2b gate 9)

Same headless `SKSurface` harness, same synthetic layouts (seeded identically), median/p95 over N
samples. "D" = darkening (per-shape) path — the one L2b's culling actually affects; "M" = the L2c
merge-tier stand-in, which is UNCHANGED by this phase (it never queries the index — a fixed L2a-era
number, included only for continuity with that table, not evidence of L2b's work).

### 50,000 shapes / 200 layers — before (L2a) → after (L2b)

| | full-extent D | pan D (median) | zoom D (median) | hit-test dense | marquee (100 moves, median / **min**) |
|---|---|---|---|---|---|
| Manhattan  | 75.9ms → 78.1ms (noise) | 49.2ms → **27.6ms** | 42.3ms → **8.5ms** | 10.5ms → **0.010ms** (≈1,050×) | 1,721ms → 1,772ms / 868ms → **103ms** |
| CurveHeavy | 237.6ms → 243.8ms (noise) | 198.2ms → **95.1ms** | 200.2ms → **26.3ms** | 31.8ms → **0.009ms** (≈3,530×) | 3,631ms → 3,674ms / 2,203ms → **148ms** |
| Mixed      | 99.3ms → 102.3ms (noise) | 76.1ms → **40.2ms** | 69.2ms → **12.3ms** | 24.0ms → **0.010ms** (≈2,400×) | 2,319ms → 2,315ms / 1,468ms → **129ms** |

### 500,000 shapes / 200 layers — before (L2a) → after (L2b)

| | full-extent D | pan D (median) | zoom D (median) | hit-test dense | marquee (100 moves, median / **min**) |
|---|---|---|---|---|---|
| Manhattan  | 737ms → 770ms (noise) | 195ms → **0.10ms** | 465ms → **87ms** | 107ms → **0.026ms** (≈4,120×) | 23,444ms → 23,540ms / 15,838ms → **8,041ms** |
| CurveHeavy | 2,554ms → 2,602ms (noise) | 1,201ms → **0.10ms** | 2,053ms → **236ms** | 327ms → **0.044ms** (≈7,430×) | 43,335ms → 44,065ms / 30,063ms → **8,441ms** |
| Mixed      | 1,103ms → 1,148ms (noise) | 249ms → **0.11ms** | 754ms → **122ms** | 262ms → **0.038ms** (≈6,900×) | 34,233ms → 34,201ms / 24,342ms → **8,257ms** |

Full-extent stays within measurement noise at every scale — gate 3, "culling must not cost anything
when nothing is off screen," holds. **Hit-test is the cleanest win** — a single point query, always
small regardless of design size, now 1,000-7,000× faster and effectively O(log n) as §0 predicted.
**Pan's median at 500k dropping to ~0.1ms is culling doing exactly its job** — most panned-to positions
in a 100mm design have almost nothing in view; the sweep's own p95/max stay high because one sweep step
IS the full-extent view, which correctly still costs what it always cost.

**Marquee is the one number that needs an honest caveat, not a spun one.** The benchmark's drag path
(inherited unchanged from L2a, for a fair before/after comparison) sweeps from one corner of the design
to the other — meaning the marquee rect ITSELF grows to cover nearly the whole design by the end of the
drag. A query against a rect that large legitimately returns nearly every shape, indexed or not — there
is no algorithmic trick that makes "how many shapes are inside this now-huge rectangle" cheaper than
examining that many shapes. The **min** column (the fastest, small-marquee-early-in-the-drag moves) DID
drop substantially — the per-move win is real and large for a realistic "select a nearby cluster"
gesture — but the median/max, dominated by the drag's own final large-rect moves, look barely changed.
This is not a shortcoming of the index; it is what "select nearly everything via one giant marquee"
correctly costs regardless of implementation, and the benchmark's own methodology (a full-diagonal
sweep) exercises exactly that worst case on every run. Real usage — selecting a local cluster — sees the
**min** column's win, not the median column's.

**R8b crossover experiment was NOT re-run** — it measures the darkening-vs-merged FILL cost at a fixed
(full-extent, single-layer) viewport, which culling does not touch; L2a's numbers for it stand unchanged.

**STR bulk-load, 500,000 shapes (Mixed profile):** median 470.8ms, p95 505.7ms (`LayoutSpatialIndex.
Apply` with `LayoutChangeInfo.Full`, i.e. from-scratch, cold). **A zoomed-in-1%-of-extent query at 500k**
returned as few as 11 candidates out of 500,000 (0.002%) in the specific region this harness's
histogram-based dense-cluster finder landed on — the load-bearing, structurally-guaranteed fact is
O(visible) not O(total) at every scale tested (gate 2's counter assertion, not a specific ratio), not
this one number, which depends entirely on where in a 100mm×100mm mostly-empty design you happen to
zoom.

## Which of §5.1's targets are now met

**1k: still yes, trivially** — unaffected either way (already far under budget).

**50k ("dense MMIC cell," target 60fps/16.6ms): still no for full-extent and worst-case zoom, but the
INTERACTIVE costs that actually gate usability are now solved.** Full-extent render is unchanged
(75-244ms, 5-15× over budget — L2c's job). But hit-test (10-32ms → ~0.01ms) and the common-case marquee
(min 868-2,203ms → 103-148ms, i.e. per-move cost well under a frame budget for a typical local
selection) are the two costs that were actually UNUSABLE before this phase — those are fixed now.

**500k ("pathological/full chip," target ≥20fps/50ms): still no for full-extent — expected, and said
plainly, not implied otherwise.** Full-extent is 628ms-2,602ms, 13-52× over the 50ms floor, with no LOD
built yet (L2c). But per L2a's own §0 warning ("culling helps only when things are off screen... will
do almost nothing for the 500k full-extent frame — that number is LOD's job"), that gap was EXPECTED
and is not this phase's to close. What L2b was actually responsible for — pan/zoom/hit-test/marquee at
scale — moved from "unusable" (a single click costing 100-330ms; a marquee move costing up to 433ms) to
either fully solved (hit-test) or substantially improved for the realistic case (marquee, pan).

## Test files

`LayoutSpatialIndex.cs`/`LayoutChangeInfo.cs` (production, `src/Ui/Layout/`) — the ONLY production
changes beyond the three consumer call sites and 4 command files listed above.
`LayoutSpatialIndexTests.cs` (19 tests — STR-build-vs-linear-scan correctness over a clustered fixture,
the lazy self-healing staleness check incl. a "never calls NotifyChanged" fixture matching this
project's own dominant test pattern, incremental insert-with-split, remove, the degradation-rebuild
trigger, `ConservativeBboxOf`'s safety margin per shape kind), `LayoutSpatialIndexFreshnessTests.cs`
(gate 7 — 11 tests, one per mutation kind driven through the REAL `LayoutEditorViewModel` public API —
add/delete/replace/move/nudge/scale/paste/flatten/boolean-union/technology-retarget, each checked at
three checkpoints: after execute, after undo, after redo — plus DBU resolution change exercised
directly against the model, since no VM seam calls `LayoutScaling.TryChangeResolution` yet),
`LayoutSpatialIndexDragTests.cs` (gate 8 — the 100-move-drag-touches-the-index-zero-times-then-once
instrumentation test, and the off-screen-to-on-screen live-drag render regression test),
`LayoutPerf/LayoutSpatialIndexPerfTests.cs` (gates 2/3/10 — zoomed-viewport counter assertions at 50k
and 500k via L2a's own `SyntheticLayoutGenerator`, full-extent-unchanged, STR bulk-load timing at 500k).
36 new tests; 2320 Ui.Tests total (was 2284), all green, confirmed stable across 3 consecutive runs;
full solution green (Firewall 4/4, Core 388/388, Engine 461/462 — 1 pre-existing skip, matching the
L2a baseline exactly). **Every one of the ~2,300 pre-existing Layout tests (rendering, hit-test,
selection, marquee) passed against the new indexed code path on the FIRST run with only one expected
update** (`Counters_HandBuiltTenShapeLayout_ExaminedAndDrawnMatchExpected`'s own L2a-era comment
explicitly predicted its 10/10 expected counters would become 7/7 once culling landed — updated to
match) — the strongest available evidence for gate 4's "pixel-identical output" requirement, since
those tests' expected pixels were written against the pre-index linear-scan renderer and never changed.

**Next: L2c** — per-shape path caching (the `// L2: cache with the shape path` comment already sitting
on `BuildPathOutline` since an earlier phase), LOD (sub-2px screen-bbox culling — the direct fix for
the 500k full-extent gap this phase deliberately left open), and the R8b merge tier at a threshold
L2a's own crossover data suggests can reasonably be lower than the design doc's ~20k starting guess.
Report back before that brief lands.

Phase L2a — the performance harness and baseline (brief-L2a-performance-harness.md, 2026-07-27) —
COMPLETE: **measurement only, no optimization** — a deterministic clustered synthetic-layout
generator, per-frame work counters, and the real baseline numbers L2b (R-tree/culling/accelerated
hit-test/marquee) and L2c (path caching/LOD/the R8b merge tier) are judged against.

**Production change is exactly the brief's guardrail and nothing more.** `LayoutRenderResult` gained
5 counter fields (`ShapesExamined`/`ShapesDrawn`/`PathsConstructed`/`DrawCalls`/`LayersVisited`, all
default 0 — the two pre-existing single-arg call sites needed no change) and a new internal
`LayoutFrameCounters` (plain `int` fields, no dictionaries/logging/string formatting) is threaded by
reference through `DrawLayer`/`DrawBitmapShapes`/`BuildShapePath`/`BuildPathOutline` to populate them.
Nothing else in `LayoutRenderer.cs` changed — no culling, no caching, no merge tier; a counter is
literally an `x++` at each call site. `BuildShapePath`/`BuildPathOutline`'s new `counters` parameter
defaults to `null` (guarded `if (counters is not null)`) so the two existing external callers
(`LayoutPathOutlineSeamTests.cs`, `BuildOutlinePathForSelection`'s default case, the L1b ghost) needed
zero changes.

**The generator (`tests/Ui.Tests/LayoutPerf/SyntheticLayoutGenerator.cs`) is test-only, per the brief —
it is not reachable from any production code.** `Generate(shapeCount, layerCount, seed, profile)` is a
pure function of its arguments: shape kind is decided by index modulo (never by RNG outcome, so the
kind schedule can never drift even if a future edit changes how much randomness one shape kind
consumes), position is drawn from a single seeded `Random` in a fixed order. **R-L2a-2 (clustered, not
uniform)** is 6 small dense clusters (55% of shapes), 4 larger mid-density regions (30%), and a sparse
background spanning the WHOLE 100mm×100mm extent (15%) — most of a generated layout is empty space by
construction, the opposite of what a uniform scatter would produce and exactly what an R-tree (L2b)
benefits from. Three profiles: **Manhattan** (Rect + a hand-built rectilinear "L" `PolygonShape`, no
diagonal or curved edge anywhere), **CurveHeavy** (Circle / RoundedRect / a closed `CurveShape` built
from 4 quarter-arcs, reusing the exact `LayoutRendererTests.ClosedCurve_OfFourQuarterArcs_
FillsLikeACircle` bulge constant / a 2-vertex OPEN `PathShape` with one Arc edge — CurveHeavy explicitly
covers both "arc-bearing curves AND paths," per the brief's own §1 wording), **Mixed** (all of the
above plus a `PolygonShape` with a genuinely contained, non-intersecting hole — passes
`LayoutClipper.EnsureValidHoles`'s cheap already-valid check with no Clipper2 re-derivation, so
determinism survives a save/load round trip — a handful of `BitmapShape`s whose `ImagePathRef`
deliberately never resolves (exercises `BitmapCache.DrawBrokenPlaceholder`, a lighter cost than a real
decode — noted, not silently assumed representative), and labels at 5% of the non-reserved population).
`GenerateTechnology(layerCount)` is the paired `Technology` — `layerCount` `LayerDef`s with distinct
HSV-wheel colors, no external dependency.

**The "merged path" comparison is a test-only stand-in, not a build of L2c's real merge tier** —
`tests/Ui.Tests/LayoutPerf/MergedPathBenchmarkRenderer.cs` collects every layer's shapes into ONE fill
path + one stroke path (2 draw calls per layer, exactly what §5.3's real merge tier will eventually
produce) instead of `LayoutRenderer`'s per-shape composited fills. It reuses `LayoutRenderer`'s own
internal `PathSpace`/`ComputeOrigin`/`BuildPathOutline` (all already `internal`, no new exposure) for
exact-geometry cases and duplicates the short Rect/Polygon/RoundedRect/Circle/Curve/Via switch rather
than widening `BuildShapePath` past `private` — the brief's guardrail permits counters and "whatever
minimal plumbing populates them," not a second production accessibility change for a benchmark-only
comparison.

**A real, pre-existing race, found by running the new suite three times in a row, not by inspection.**
`LayoutTextOutline.TestOverrideTypeface` (the seam that lets a headless test render a `LabelShape`
without touching unloadable `SkiaFonts.PlexRegular`) is ONE static field; xUnit runs test CLASSES in
parallel by default, and `LayoutLabelFixAndTextFlattenTests`/`LayoutLabelOwnerFollowUpFixesTests`
already set/cleared it independently in their own constructor/`Dispose` — a latent race that predates
this phase. Adding two more classes doing the same thing (both new benchmark files render Mixed-profile
labels) made the collision frequent enough to actually fail a run. Fixed with a shared xUnit collection,
**not** a blanket `DisableTestParallelization`: new `LayoutTextOutlineTypefaceCollection`
(`tests/Ui.Tests/LayoutTextOutlineTypefaceCollection.cs`) is a bare `[CollectionDefinition]` marker;
all four classes that touch the static field now carry `[Collection(...Name)]`, which serializes only
those four relative to each other — the rest of the ~2300-test suite still parallelizes normally.
Three consecutive full non-Nightly runs after the fix: 2284/2284, 2284/2284, 2284/2284.

## The baseline table (R-L2a-7)

All numbers: `AMD`-class CI-equivalent dev machine, headless `SKSurface`, warm-up discarded, **median /
p95** over N samples (never the mean — R-L2a-4). 200 layers throughout, per §5.1. Reproduce with
`dotnet test --filter FullyQualifiedName~LayoutPerf --logger "console;verbosity=detailed"`
(`--filter "Category!=Nightly"` to skip the 500k rows — they take ~6 minutes combined).

### 1,000 shapes / 200 layers

| Profile | Full-extent D / M (median, p95) | Pan D / M | Zoom D / M | Hit-test dense / sparse | Marquee (100 moves) | Load parse | `.clay` size |
|---|---|---|---|---|---|---|---|
| Manhattan  | 1.62/1.62 · 1.56/1.61 ms | 1.46/1.68 · 1.45/1.63 ms | 0.99/1.86 · 0.84/4.06 ms | 0.215/0.224 · 0.200/0.207 ms | 16.3/20.2 ms | 0.74/0.74 ms | 267,614 B (267.6 B/shape) |
| CurveHeavy | 5.03/5.08 · 4.63/4.79 ms | 4.76/5.15 · 4.50/4.90 ms | 3.59/4.92 · 3.41/4.66 ms | 0.628/0.655 · 0.626/0.828 ms | 43.1/49.8 ms | 1.51/2.03 ms | 437,871 B (437.9 B/shape) |
| Mixed      | 2.29/2.31 · 1.95/1.96 ms | 2.11/2.40 · 1.82/2.11 ms | 1.24/2.23 · 1.24/7.25 ms | 0.468/0.473 · 0.467/0.474 ms | 28.2/32.3 ms | 0.99/1.01 ms | 340,952 B (341.0 B/shape) |

### 50,000 shapes / 200 layers

| Profile | Full-extent D / M | Pan D / M | Zoom D / M | Hit-test dense / sparse | Marquee (100 moves) | Load parse | `.clay` size |
|---|---|---|---|---|---|---|---|
| Manhattan  | 75.9/80.9 · 60.3/60.9 ms | 49.2/66.1 · 45.4/57.8 ms | 42.3/72.9 · 42.2/62.2 ms | 10.5/10.8 · 9.7/10.0 ms | **1,720/1,721 ms** | 90.5 ms | 13,373,154 B (267.5 B/shape) |
| CurveHeavy | 237.6/237.7 · 209.0/210.2 ms | 198.2/238.8 · 183.0/217.5 ms | 200.2/250.8 · 189.3/231.9 ms | 31.8/32.7 · 31.4/31.9 ms | **3,631/3,631 ms** | 98.1 ms | 21,885,561 B (437.7 B/shape) |
| Mixed      | 99.3/100.8 · 78.9/79.9 ms | 76.1/101.0 · 61.0/79.1 ms | 69.2/104.4 · 56.6/87.4 ms | 24.0/24.4 · 23.4/23.8 ms | **2,319/2,319 ms** | 65.6 ms | 17,025,118 B (340.5 B/shape) |

### 500,000 shapes / 200 layers (Nightly trait — ran once for this table)

| Profile | Full-extent D / M | Pan D / M | Zoom D / M | Hit-test dense / sparse | Marquee (100 moves) | Load parse | `.clay` size |
|---|---|---|---|---|---|---|---|
| Manhattan  | 737/737 · 648/648 ms | 195/747 · 162/649 ms | 465/772 · 453/676 ms | 107/109 · 102/103 ms | **23,444/23,444 ms** | 448 ms | 133,730,154 B (267.5 B/shape) |
| CurveHeavy | 2,554/2,554 · 2,230/2,230 ms | 1,201/2,525 · 1,105/2,231 ms | 2,053/2,574 · 1,897/2,336 ms | 327/330 · 322/325 ms | **43,335/43,335 ms** | 873 ms | 218,853,638 B (437.7 B/shape) |
| Mixed      | 1,103/1,103 · 863/863 ms | 249/1,104 · 144/853 ms | 754/1,126 · 571/888 ms | 262/265 · 255/257 ms | **34,233/34,233 ms** | 811 ms | 189,734,523 B (379.5 B/shape) |

(D = darkening/per-shape path, M = merged-path stand-in; "median/p95" pairs, both in ms unless noted.)

### R8b crossover experiment — single layer, Manhattan rects, full-extent frame, darkening vs. merged

| Shapes on the one layer | Darkening median | Merged median | Ratio (D/M) |
|---|---|---|---|
| 500     | 0.78 ms   | 0.64 ms   | 1.21× |
| 2,000   | 3.27 ms   | 2.92 ms   | 1.12× |
| 5,000   | 8.32 ms   | 7.60 ms   | 1.09× |
| 10,000  | 16.9 ms   | 15.7 ms   | 1.08× |
| 20,000  | 30.6 ms   | 28.5 ms   | 1.08× |
| 50,000  | 78.8 ms   | 74.9 ms   | 1.05× |
| 100,000 | 166.1 ms  | 163.1 ms  | 1.02× |

### Counters (full-extent, darkening path) — direct evidence for "where the time goes"

| | 1k Manhattan | 1k CurveHeavy | 1k Mixed | 50k Manhattan | 50k CurveHeavy | 50k Mixed |
|---|---|---|---|---|---|---|
| ShapesExamined | 1,000 | 1,000 | 1,000 | 50,000 | 50,000 | 50,000 |
| ShapesDrawn    | 1,000 | 1,000 | 1,000 | 50,000 | 50,000 | 50,000 |
| PathsConstructed | 1,000 | 1,500 | 949 | 50,000 | 75,000 | 47,500 |
| DrawCalls      | 1,200 | 1,200 | 1,190 | 50,200 | 50,200 | 50,198 |
| LayersVisited  | 200 | 200 | 200 | 200 | 200 | 200 |

`ShapesExamined == ShapesDrawn == total shape count`, EVERY time, at EVERY scale — confirms directly
what the design doc predicted and this phase built no fix for: **there is no viewport culling of any
kind today.** `DrawCalls ≈ ShapesDrawn + LayersVisited` (one fill per shape, one batched stroke per
non-empty layer) — the "constant per-layer draw-call count" §5.3 promises only exists once L2c's merge
tier actually ships. `PathsConstructed` is where CurveHeavy's extra cost shows up structurally, not just
in wall-clock: 1.5 paths built per CurveHeavy shape (75,000 / 50,000) vs. 1.0 for Manhattan, because
`BuildPathOutline` allocates 3 `SKPath`s (centerline, stroke-to-fill outline, `Simplify` destination) per
`PathShape`, and exactly 1/4 of CurveHeavy's population is a `PathShape` — `0.25×3 + 0.75×1 = 1.5`,
matching the measured ratio exactly.

## Where the time actually goes at 500k

**Not where the design doc predicted first** (path construction / per-shape draw calls) — those ARE
real (full-extent frame time is 5-15× over the §5.1 pan/zoom budget already at 50k, 13-50× over at
500k, and CurveHeavy costs ~3× Manhattan per shape from tessellation + the 3-paths-per-`PathShape` cost
above) — but the single most catastrophic number in this whole table is **the marquee preview**, and it
is not close:

- At 500k, one marquee drag of 100 pointer moves costs **23.4 – 43.3 seconds** — 234-433 **milliseconds
  per pointer move**, worse than rendering an entire full-extent frame (647ms-2,554ms) roughly every
  4-11 moves. This is `LayoutEditorViewModel.ComputeMarqueeSelection` doing an O(shape count) bbox scan
  on every qualifying `OnPointerMoved`, exactly as the brief predicted — "it will not show up in a
  frame-time measurement at all" is confirmed literally: nothing above touches this cost, it is a
  completely separate O(N) linear scan with no relationship to the render pipeline.
- Even at 50k, marquee costs 1.7-3.6 **seconds** for 100 moves (17-36ms/move) — already past the 16.6ms
  frame budget on every single move, i.e. **the marquee preview is unusably laggy well before the
  design doc's "dense MMIC cell (~50k)" scenario is even reached.**
- Second worst: **hit-test**, 100-330ms for a SINGLE click at 500k (a linear scan, same root cause as
  marquee — no spatial index) — bad for one click, catastrophic for anything that calls it repeatedly.
- Third: full-extent/pan/zoom **render** cost itself — real, but an order of magnitude less painful
  than marquee/hit-test at every scale measured, and (per §5.1) the only one L2b's culling directly
  targets before L2c's caching/merge tier is even needed.
- Fourth: **load/parse time and `.clay` size** — 267-438 bytes/shape depending on profile, ~450ms-873ms
  parse at 500k. Not a rendering problem, but the 500k CurveHeavy file is ~219 MB uncompressed; §4's
  deferred gzip question now has real numbers behind it (worth a look whenever `.clay` I/O gets its own
  attention — out of scope for L2b/L2c, which are rendering/hit-test phases).

**Ranked by measured cost (not the design doc's prediction), highest first:**
1. **Marquee preview recompute** — by a wide margin the worst offender at every scale ≥ 50k; the single
   highest-priority target for L2b's "accelerated marquee."
2. **Hit-test** (a single query) — same root cause (no spatial index), same L2b fix (the R-tree), much
   less catastrophic per-call than marquee but still 100-330ms at 500k.
3. **Full-extent/pan/zoom render cost** — real and linear in total shape count (no culling exists), the
   direct target of L2b's viewport culling (bring `ShapesExamined`/`ShapesDrawn` down to O(visible)) and
   L2c's per-shape path caching + LOD (bring the per-shape constant down).
4. **`PathShape`/curve tessellation cost** (CurveHeavy ≈ 3× Manhattan per shape, confirmed structurally
   via `PathsConstructed`) — L2c's per-shape path cache is the direct fix (§5.3 item 2's `// L2: cache
   with the shape path` comment on `BuildPathOutline`, already left there in an earlier phase).
5. **R8b's merge tier** — a real but genuinely MODEST win (see below), not a cliff.
6. **`.clay` load/parse + file size at very large scale** — real, quantified, explicitly out of L2b/L2c's
   scope.

## Are §5.1's 1k/50k targets already met?

**1k: yes, easily** — every full-extent/pan/zoom frame is 0.8-5.1ms, far under the 16.6ms (60fps)
budget even at CurveHeavy's worst p95. Nothing to optimize at this scale.

**50k ("dense MMIC cell," target 60fps / 16.6ms): no** — full-extent/pan/zoom frames run
42-251ms, **5-15× over budget** depending on profile. This is squarely L2b/L2c's job to close.

**500k ("pathological/full chip," target ≥20fps / 50ms, degrade via LOD): no, and not expected to be** —
full-extent frames run 647ms-2,554ms, 13-51× over the 50ms floor, with no LOD or culling built yet
(L2a's own guardrail forbids building either). The marquee number above is the one that actually matters
most at this scale, though — a correct-but-slow render is tolerable if LOD keeps the picture legible;
an unresponsive marquee drag is not.

## The R8b crossover — the honest answer is "no crossover was found in the tested range"

The single-layer sweep above (500 to 100,000 shapes on one layer) shows merged **always** cheaper than
darkening, by a MARGIN THAT SHRINKS as density grows (1.21× at 500 shapes/layer down to 1.02× at
100,000) — the opposite of a "cliff crossover" shape. There is no shape count in the tested range where
darkening becomes cheaper than merging. Two implications for L2c, stated plainly rather than papered
over with an invented number:
- **Merging a layer's fills is never a net loss in raw draw cost**, at any density tested — the design
  doc's "start at ~20k, tune against the benchmark" framing implicitly assumes a break-even point to
  find; this data says the real lever is elsewhere (draw-CALL-count reduction, which matters more when
  many layers each carry a modest shape count — exactly the 200-layer scenario above, where merged wins
  15-25% even at only 1k-50k TOTAL shapes) rather than a per-layer fill-cost cliff.
- L2c can reasonably set the merge threshold **lower** than 20k with no measured downside in this data —
  the real cost of merging early is the UX one §2.3 R8a already named directly (same-layer overlap stops
  reading as darker), not a performance one. That trade-off is the design doc's own deliberate decision
  and out of scope to revisit here; this note only says the PERFORMANCE case for a low threshold is
  stronger than "start at ~20k" implies.

## Optimization opportunities for L2b/L2c — ordered by measured cost, not by the design doc's guesses

1. **Marquee preview** (`LayoutEditorViewModel.ComputeMarqueeSelection`) — O(N) bbox scan on every
   pointer move, unthrottled cost measured directly above; the L1i pixel-movement-skip guard already
   reduces recompute FREQUENCY but not per-recompute COST, and per-recompute cost is what dominates at
   scale. Needs the R-tree (viewport/marquee-rect query) L2b already plans.
2. **Hit-test** (`LayoutHitTest.HitStack`) — same fix, same root cause.
3. **Viewport culling** — `LayoutRenderer.Draw` examines and draws every shape every frame regardless of
   visibility (confirmed structurally by the counters, not assumed); L2b's R-tree brings
   `ShapesExamined`/`ShapesDrawn` down to O(visible), which is where most of the render-cost gap to
   §5.1's targets should close.
4. **Per-shape path caching + `PathShape`'s 3-paths-per-shape cost** — L2c; `BuildPathOutline`'s own
   `// L2: cache with the shape path` comment (already in the code from an earlier phase) is exactly
   this, now with real numbers (1.5× paths for CurveHeavy) behind it.
5. **LOD (screen-bbox-under-2px culling)** — §5.3 item 3, not directly measured by this harness (no
   sub-pixel-shape population was generated), but implied by the 500k full-extent numbers: at deep
   zoom-out, the overwhelming majority of 500k shapes' on-screen footprint is sub-pixel.
6. **R8b merge tier** — real, modest (2-21%), no crossover cliff found; lower priority than the above
   four, and the threshold decision has more room than the design doc assumed (see above).
7. **`.clay` gzip** (§4, explicitly out of scope for L2b/L2c) — 500k CurveHeavy is ~219MB uncompressed;
   worth a future look at save/load time and disk footprint, not a rendering concern.

Test files: `SyntheticLayoutGenerator.cs`/`MergedPathBenchmarkRenderer.cs`/`BenchmarkHarness.cs` (the
harness itself, all test-only, `tests/Ui.Tests/LayoutPerf/`), `LayoutPerfHarnessGateTests.cs` (gates
2-6: determinism incl. a same-seed/different-seed pairing and a serialize-reload round trip, the
1%-of-extent clustering histogram, generate+render at 1k/50k for all 3 profiles with no exception incl.
the Mixed hole/bitmap/label populations, 3 hand-built counters-correctness fixtures, a structural
plain-`int`-fields proof for `LayoutFrameCounters` standing in for gate 6's "cost nothing" per R-L2a-3's
no-conditional-compilation constraint), `LayoutPerformanceBaselineTests.cs` (the real deliverable — full
baseline table above, `[Trait("Category","Nightly")]` on every 500k-scale case per gate 7, loose
3×-headroom wall-clock catastrophe assertions per gate 9, the R8b crossover experiment).
`LayoutTextOutlineTypefaceCollection.cs` (new, `tests/Ui.Tests/`) fixes the pre-existing
`TestOverrideTypeface` parallel-test race described above — applied to both this phase's new classes AND
the two pre-existing label test classes that already had the same latent bug. 46 new tests (28 gate +
18 baseline/crossover theories); 2284 Ui.Tests total (was 2256), all green, run 3× consecutively to
confirm the race fix holds; full solution green (Firewall 4/4, Core 388/388, Engine 461/462 — 1
pre-existing skip, matching the layout-label-owner-follow-up baseline exactly). **Not interactively
verified** (no visual driver in this environment, matching every prior Layout Editor phase) — the
numbers above come directly from the headless `SKSurface` harness, which is the whole point of this
phase; there is nothing to verify interactively that the numbers don't already show more precisely.

**Next: L2b** — the R-tree spatial index (§5.2 R11, per-layer), viewport culling, and accelerated
hit-test/marquee — in that priority order, per the ranked list above. Report back before L2c
(per-shape path caching, LOD, the R8b merge tier at a threshold this phase's data suggests can be lower
than ~20k) is briefed.

Layout labels — owner follow-up round, 5 reports (2026-07-27) — COMPLETE: after the label-fix brief
below shipped, five more owner reports landed in one pass.

**(1) A committed label could still "disappear" the instant Enter was pressed.** The R-lbl-2 ghost boost
(below) only ever applied to the temporary IN-PROGRESS ghost — the COMMITTED shape used the raw
technology/fallback height unboosted, which can still render sub-pixel at a zoomed-out view even though
it's perfectly sensible at the technology's own typical zoom. `LayoutRenderer.EffectiveGhostLabelHeightDbu`
was generalized and renamed `EffectiveVisibleLabelHeightDbu` (its `dbuToUm` parameter dropped — it
cancels out of the on-screen-pixel computation entirely, so the caller now only needs the zoom directly,
device px per DBU), and `LayoutEditorViewModel.CommitLabel` now applies the SAME visibility floor to the
real committed `Height`, using the zoom captured when typing started. Never retroactive — an existing
label is never touched.

**(2) The selection highlight box was wrong for rotated labels.** Two independent bugs in the SAME
hand-derived approximate-footprint formula, duplicated in `LayoutHitTest.LabelHitBbox` (hit-testing,
must stay framework-free) and `LayoutRenderer.BuildOutlinePathForSelection` (the renderer, which has
Skia): R90/R270 had the local "far corner" landing on the WRONG SIDE of the anchor entirely (verified
against `DrawLabelText`'s real transform via a from-scratch rotation-matrix derivation, not by guessing)
— fixed in both places. Separately, R0/R180 were positionally correct but loosely FIT (a fixed
0.62-of-height-per-character estimate, same for "W" and "i"). Since the renderer already has SkiaSharp,
`BuildOutlinePathForSelection`'s LabelShape case was replaced entirely with a new
`LayoutRenderer.MeasureLabelWorldBbox` — real font metrics via `SKFont.MeasureText` (cheap: one
measurement call, not a full glyph-outline extraction like `LayoutTextOutline` uses for flattening),
all four corners transformed through the exact same rotation table `DrawLabelText` uses, so it is
correct for every rotation by construction and can never drift from what's actually rendered.
`LayoutHitTest.LabelHitBbox` keeps the cheap approximation (framework-free constraint is real) but got
the same rotation-sign fix. Proven with a REAL pixel-oracle test (not just a formula check) by also
threading `LayoutTextOutline`'s `TestOverrideTypeface`/`ResolveTypeface` seam into `DrawLabelText` itself
— see item (4) below for what that seam is.

**(3) Flatten dialog button text not centered — reported for the 3rd time.** Every prior fix was
per-dialog (`ScaleDialog`, `OffsetDialog`, `LayerMappingDialog`, …, each individually gained
`HorizontalContentAlignment="Center"` on its buttons). `FlattenToPolygonDialog.axaml`'s buttons were
simply never patched. Fixed ONCE, globally, in `App.axaml`'s existing `Style Selector="Button"` — added
`HorizontalContentAlignment="Center"` there instead of a fourth per-dialog patch. **This class of bug is
now structurally impossible to reintroduce — do not go back to fixing individual dialog buttons.** Every
existing per-dialog explicit override becomes redundant-but-harmless (same value); new dialogs need no
button-centering code at all.

**(4) `LabelShape` gained a `Style` property** (`LabelFontStyle { Regular, Bold, Italic, Condensed }`,
additive, no `.clay` `FormatVersion` bump, defaults Regular) — mirrors the symbol editor's
`SymbolFontStyle` PATTERN, deliberately not the type (`LayoutModel.cs`'s own header: "Layout borrows
patterns from Schematic, not types"). The style-to-typeface mapping is now centralized in ONE place,
`LayoutTextOutline.ResolveTypeface(LabelFontStyle)` (mirrors `SchematicRenderer`'s `TextPrimitive.
FontStyle` mapping exactly, including Condensed → the Light weight, matching that precedent, not a
typo) — `LayoutRenderer.DrawLabelText` AND `LayoutTextOutline.BuildGlyphContours` both call it now,
which is a STRONGER "same font" guarantee than the original brief's "both reference the same constant"
(there is only one place a style→typeface mapping could ever drift, period). `ResolveTypeface` still
checks `TestOverrideTypeface` first (now also threaded into `DrawLabelText`, not just the flatten path),
which is what makes a genuine pixel-oracle test of the SELECTION BOX FIX above possible. Editable in the
Properties Inspector (new Style combo in the Label section, immediate-commit, same pattern as Rotation).

**(5) Properties Inspector: label position (X/Y) is now editable.** A label's X/Y is a plain anchor
point, not a min-corner-and-size pair like Rect — so unlike `ApplyRectResize`/`ApplyRectPosition`'s two
different semantics, editing it is a single straight translate (`CommitLabelXText`/`CommitLabelYText`,
staged text, `LayoutUnits` parsing, `ApplyToEach` for multi-selection-as-one-undo-entry — the same shape
every other typed dimension field in this panel already uses).

Test file: `LayoutLabelOwnerFollowUpFixesTests.cs` — the corrected R90/R270 hit-box quadrants (and R0/R180
unaffected), `MeasureLabelWorldBbox`'s quadrant-per-rotation and real-vs-fixed-width metrics, a genuine
pixel-oracle proving the R90 selection fix with actual rendered glyph pixels (not just a formula), the
global Button style + a documentation-by-test that `FlattenToPolygonDialog`'s buttons carry no redundant
per-button override, `LabelShape.Style` defaulting/cloning/persistence round-trip and Properties-Inspector
commit-as-one-undo-entry, and label X/Y position commit/error/multi-selection-undo. Plus 6 new tests in
`LayoutLabelFixAndTextFlattenTests.cs` (`CommitLabel_*`/`EffectiveVisibleLabelHeightDbu_*`) for item (1),
folded into that file since they're a direct continuation of its own gates. 2256 Ui.Tests total, all
green; full solution green (Firewall 4/4, Core 388/388, Engine 461/462 — 1 pre-existing skip, matching
the layout-label-fix-and-text-flatten baseline exactly). **Not interactively verified** (no visual driver
in this environment) — the R90 selection-box fix in particular rests on a real pixel-oracle rendering
test (stronger evidence than most items in this file get), but please still confirm on your end: a new
label is visible immediately after pressing Enter even when zoomed out; the selection box hugs a rotated
label correctly at all four rotations; the Flatten dialog's buttons read centered; a label's Style combo
in the Properties Inspector visibly changes its rendered weight/slant; and a label's X/Y fields move it
when edited.

Layout labels: invisible on PCB, and Flatten-to-Polygon for text (brief-layout-label-fix-and-text-
flatten.md, 2026-07-27) — COMPLETE: owner report "placing a label with the toolbar button appears to do
nothing," plus a request for text-to-polygon. **The label pipeline (tool, typing buffer, ghost caret,
Enter/Escape/Backspace, `CommitLabel`, `DrawLabelText`) was never broken** — the label WAS created, just
too small to see. Root cause: `_labelHeightDbu = 5_000` was a hardcoded absolute size (5 µm at 1000
DBU/µm) chosen at one scale in a database whose scales span six orders of magnitude — **the exact same
failure mode as the L1a default-zoom bug** (see that entry further down this file): on the PCB starter
technology (1 mil snap, ~20 mm default viewport), a 5 µm label is ~1/4000 of the view width, comfortably
sub-pixel; `DrawLabelText` even clamps its font size to `Math.Max(0.001f, …)`, so it drew something
infinitesimal rather than visibly nothing, which is what made this look like a dead button rather than a
sizing bug.

**R-lbl-1 — the default label height now comes from the technology, seeded once at construction, never
re-seeded.** `Technology.DefaultLabelHeightDbu` (new, additive, no `.ctech` `FormatVersion` bump — 0 =
unset) is seeded per starter tech (**40 mil** for PCB 2-Layer, **5 µm** for MMIC GaAs, matching each
process's own drafting convention) and applied in `LayoutEditorViewModel.ApplyTechResolution`, guarded by
a one-shot `_labelHeightSeededFromTech` flag — exactly like `DisplayUnit`/`SnapDbu` are seeded once at
construction and never re-seeded by a later technology change (retarget, live `.ctech` edit), so a user's
own typed label-height edit is never silently discarded. **Why technology-provided here, and deliberately
NOT viewport-relative like the bitmap brief's R-bmp-4** — say so explicitly, because the two look
inconsistent otherwise: a bitmap is a reference image the user resizes immediately, so "sensible on
screen right now" is correct; a label height is a *drafting convention of the process/board*, wanted
consistent across a design and across sessions — deriving it from the current zoom would make the same
typed command produce different persistent geometry depending on how far the user happened to be zoomed
in. Different defaults for different reasons, on purpose. No technology resolved → falls back to the
original hardcoded 5 µm, now correctly scoped as "no-technology fallback" rather than "the default."

**R-lbl-2 — typing feedback.** The Label tool's status hint reuses the EXISTING toolbar `DrawReadoutText`
readout (was hardcoded to `""` while typing) rather than inventing a second status property — "Typing
label — Enter to commit, Esc to cancel," clearing the instant `CommitLabel`/`CancelDrawOp` flips
`_isTypingLabel` false. A ghost that would render below `LayoutRenderer.MinGhostLabelDevicePixels` (8px)
is boosted to exactly that floor for DISPLAY ONLY (the committed shape's real `Height` is never touched)
— the boost math is deliberately split into a pure function, `LayoutRenderer.EffectiveGhostLabelHeightDbu`,
specifically so it is headlessly testable (see the SkiaFonts constraint below); the hint also notes when
the label is smaller than the zoom captured at typing-start can show. **R-lbl-3** — `LayoutCanvas`'s Space
branch now checks `_viewModel?.IsTypingLabel != true` before arming the pan modifier: Space itself already
reached the label buffer correctly via Avalonia's separate `TextInput` event (verified, not assumed — the
`KeyDown` handler never set `e.Handled`), but `_spaceHeld` stayed true through the rest of the gesture,
so a subsequent left-drag panned instead of doing nothing.

**Text-to-polygon flattening (R-lbl-4/R-lbl-6) extends the EXISTING "Flatten to Polygon…" command —
no second menu entry.** `HasCurvedGeometryAt`'s predicate widened to include any non-port `LabelShape`
with text; the dialog's message now names curves and labels separately ("3 curves and 1 label will
become polygon(s)") and calls out port-label skips by name rather than folding them into "has no
curvature." **R-lbl-5 — a port label (`IsPort`) is a terminal marker (§9/§10.6) and is NEVER flattened,
even when explicitly selected** — excluded from `HasCurvedGeometryAt` (so a sole-port-label selection
shows Flatten disabled with a reason) and defended a second time inside `FlattenLabel` itself (returns
empty) in case a future caller ever bypasses that filter; a mixed selection flattens everything else and
reports the skip via Messages, never silently dropping the label with no trace.

**The pipeline is split at the Skia boundary, matching this codebase's framework-free convention for
`src/Ui/Layout/`:** `src/Ui/Renderers/LayoutTextOutline.cs` (Skia — the ONE place this feature touches
`SkiaSharp`) extracts one `CurveShape` per glyph CONTOUR from `SKFont.GetTextPath` (the SAME
`SkiaFonts.PlexRegular` typeface `LayoutRenderer.DrawLabelText` renders with — WYSIWYG, and deterministic
across machines since the font is bundled), mirroring `DrawLabelText`'s transform EXACTLY (same
Y-down-path-space rotation-sign table) rather than re-deriving it, with each TrueType quadratic segment
degree-elevated to an exact Cubic edge (`LayoutEdge` has no Quad kind; elevation, unlike flattening, loses
nothing). `src/Ui/Layout/LayoutTextFlatten.cs` (framework-free) takes those contours, flattens each via
the SAME shared `LayoutFlattener` every other curved primitive uses (no second flattener), and — this is
the part worth over-explaining — **resolves nesting (which contour is an outer boundary vs. a hole) via
Clipper2's `Union`/`PolyTree64` (`LayoutClipper.FromClipperTree`, already the single nesting-resolution
point every other multi-contour boolean result in this codebase uses), NEVER by inferring nesting from
contour winding by hand** — glyph fill rules vary by font, and hand-winding-inference is a classic source
of filled-in letters (a hole silently vanishing). This is a real end-to-end exercise of §3.1a holes: `O`
→ 1 polygon with 1 hole, `8` → 1 polygon with 2 holes, `i` → 2 SEPARATE polygons (dot + stem, neither
nested in the other) — without hole support every rounded letter would come out as a solid blob. One
`ReplaceShapesCommand` covers the WHOLE selection per Flatten operation (not one per label) — a label
expanding to N polygons is simply N `added` entries against its ONE `removed` entry, which
`ReplaceShapesCommand`'s existing N-removed→M-added contract already handles with no changes; undo
restores the exact original `LabelShape` instance at its original index.

**`SkiaFonts.PlexRegular` cannot load in this project's headless xunit tests at all** (confirmed
empirically, not assumed: `Avalonia.Platform.AssetLoader` throws `InvalidOperationException` with no live
Avalonia app host — the same constraint `LayoutRulerRendererTests.cs` already documented for tick-label
text). `LayoutTextOutline.BuildGlyphContours` gained an `internal TestOverrideTypeface` (via
`InternalsVisibleTo`, production code never sets it) substituting `SKTypeface.Default` — the ONE typeface
guaranteed loadable without the asset system — so the REAL algorithm (glyph extraction, quad-to-cubic
elevation, DBU transform, `LayoutFlattener`, Clipper2 nesting) runs genuinely end-to-end in tests, through
the actual VM entry points (`FlattenSelectionToPolygon`/`FlattenAllCurves`), not a mock. Verified by hand
against a scratch console probe before writing test assertions: `SKTypeface.Default` resolves to
Helvetica on macOS and produces the exact same glyph topology (O=1 hole, 8=2 holes, i=2 disjoint parts)
any reasonable Latin sans-serif does — a safe, portable bet for these specific glyphs. Gate 11 ("same
font") and the ghost's pixel-level visibility (R-lbl-2's second half) genuinely cannot be exercised at
all here — pinned instead by a source-text-scan (both `LayoutRenderer.cs` and `LayoutTextOutline.cs`
reference `SkiaFonts.PlexRegular` verbatim) and by testing `EffectiveGhostLabelHeightDbu` as the pure
arithmetic it was deliberately split out to be, respectively — matching this codebase's established
answer to "the thing under test needs a live Avalonia app host this project's tests don't have."

Test file: `LayoutLabelFixAndTextFlattenTests.cs` — the PCB-technology headline regression (a placed
label renders ≥ 4 device px at the default viewport; a direct negative-control proving the OLD 5000 DBU
constant would have been sub-pixel there), both starter techs seed `CurrentLabelHeightDbu` from
`DefaultLabelHeightDbu` and a later technology change never re-seeds it, no-technology fallback to 5 µm,
`.ctech` round-trip of the new field plus a hand-stripped-field file still loading, the typing hint
appearing/clearing on commit/Escape and noting/omitting the too-small-for-zoom case, the ghost min-size
boost as pure arithmetic, Space appending to the label buffer while typing (VM-level, since `LayoutCanvas`
is a `Control` and cannot be constructed headlessly) plus a source-scan pin of the guard clause itself,
the O/8/i hole-count gates and a raw-glyph-vs-flattened-polygon bounding-box cross-check, the same-font
source-scan, port-label-alone-disabled and mixed-selection-skip-and-report, the Rect/Circle/label
enablement matrix, and flatten-a-label undo restoring the original `LabelShape` at its original index
byte-identically. 28 new tests; 2227 Ui.Tests total, all green; full solution green (Firewall 4/4, Core
388/388, Engine 461/462 — 1 pre-existing skip, matching the layout-bitmaps-and-insert-button baseline
exactly). **Not interactively verified** (no visual driver in this environment) — please confirm placing
a label on a PCB-technology layout is now visibly readable at the default zoom, that typing shows the
status hint, that Space in label text still types a space without arming pan, and that flattening a
label (Right-click → Flatten to Polygon… with a Label selected) produces geometry matching the rendered
glyph.

Layout bitmaps + Insert Bitmap (brief-layout-bitmaps-and-insert-button.md, 2026-07-27) — COMPLETE: the
Layout Editor gained a `BitmapShape : LayoutShape` (image path reference, X/Y/W/H DBU placement rect,
Opacity, Locked) — a min-corner-and-size shape, unlike every other primitive's own coordinate shape —
plus an "Insert Bitmap" toolbar button in **both** the symbol editor and the Layout Editor.

**`src/Ui/Renderers/BitmapCache.cs` (new, R-bmp-1) is the single shared decode cache**, extracted from
`SchematicRenderer`'s former private `ConcurrentDictionary<string, SKBitmap?>` — `Load`/`Invalidate`/
`TryGetPixelSize`/`DrawBrokenPlaceholder`, used by both the schematic/symbol renderer and the new
layout renderer. `SchematicRenderer.InvalidateBitmapCache`/`TryGetBitmapPixelSize` are now one-line
forwarders (`=> BitmapCache.Invalidate(path)` / `=> BitmapCache.TryGetPixelSize(path)`) — every existing
caller (`SchematicViewModel`, `SymbolEditorViewModel`) needed zero changes.

**R-bmp-2 — a bitmap's Layer governs visibility/selectability only, NEVER paint order; bitmaps always
render first, beneath every layer, regardless of the layer's own ZOrder.** This is the one deliberate
exception to every other shape's "Layer determines both visibility and z-order" rule, and is enforced in
exactly one place: `LayoutRenderer.Draw` excludes `BitmapShape` from the ZOrder-sorted `byLayer` grouping
entirely and calls a new `DrawBitmapShapes` unconditionally right after the path-space matrix is set up,
before the per-layer loop runs. `DrawBitmapShapes` still respects the bitmap's OWN layer's `Visible` flag
(and reports unknown layers the same way `Draw`'s main loop does) — "always behind" is about paint
ORDER, not visibility. A missing/undecodable file draws `BitmapCache.DrawBrokenPlaceholder` (a dashed
warning-colored box + X) instead of throwing or silently vanishing. Both the selection-outline builder
(`BuildOutlinePathForSelection`) and the ghost/paste-preview builder (`DrawGhostShape`) gained their own
`BitmapShape` case for the same reason — `BuildShapePath` (the shared per-shape path builder every other
draw call funnels through) deliberately has NO `BitmapShape` case, since a bitmap isn't geometry, so
these two callers needed a small dedicated rect-path branch (`BuildBitmapPlacementRectPath`) instead.

**R-bmp-3 — a bitmap is NOT geometry.** `LayoutEditorViewModel.Booleans.cs`'s `GeometricSelectedIndices`
(`ValidSelectedIndices.Where(i => Model.Shapes[i] is not BitmapShape)`) is the ONE place this is
enforced — Union/Intersect/Difference/XOR/Offset all gather their operands through it, so a bitmap in a
mixed selection is silently excluded (never throws, never partially applies) rather than needing a
defensive case added to `LayoutFlattener`/`LayoutClipper`, which would still throw if ever handed a
`BitmapShape` (their switches have no matching arm) — the exclusion happens upstream, at operand
gathering, not downstream, at the geometry engine. `LayoutFlattener.HasCurvedGeometry`/
`LayoutSelfIntersection.Test`/`LayoutClipper.EnsureValidHoles`/`LayoutHandles.Build`/
`LayoutArcPromotion.PromoteArcsToCubics` all needed NO change — each already has a safe default/`_`
arm that handles an unrecognized shape type correctly (false/no-op/passthrough), confirmed by direct
code reading, not assumed. **Forward-only comments** (Flatten/Export/DRC/Mesh don't exist yet) live on
`BitmapShape`'s own doc comment in `LayoutModel.cs`: excluded from Flatten-to-Polygon, future GDSII/DXF/
Gerber export, DRC, and MoM meshing — but full participation in bbox/hit-test/select/move/scale/
clipboard/undo (all confirmed working via the generic, shape-type-agnostic machinery in `LayoutGeometry`/
`LayoutHitTest`/`LayoutCoordinateWalk`/`LayoutFragment`, extended with one `BitmapShape` case each), and
IS rendered into the clipboard's PDF/SVG/EMF graphic export (`LayoutClipboard`'s renderers call
`LayoutRenderer.Draw` directly on the exported selection — the always-behind bitmap pass runs
unconditionally, so this needed no clipboard-side change at all).

**R-bmp-4 — sizing is viewport-relative, never a fixed DBU constant** (`LayoutEditorViewModel.Bitmaps.cs`'s
`ComputeBitmapPlacementSize`): a newly-placed bitmap's long edge spans ~25% of the CURRENT viewport
width in DBU (`LayoutCanvas.ViewportWidthDbu()`, computed fresh per placement from `VisibleMaxX -
VisibleMinX` — never cached, since DBU are nanometres and a stale width would be meaningless after any
zoom/pan), preserving the source image's pixel aspect ratio via `BitmapCache.TryGetPixelSize`. An
undecodable file falls back to a 4:3 box (still 25%-of-viewport sized) plus a one-time Messages warning
— insertion never silently fails. `PlaceBitmap` is the ONE construction+insertion point, shared by drag-
drop (`DropBitmap`, top-left lands at the drop point — same convention the symbol editor's `DropBitmap`
already established) and the Insert Bitmap button (`InsertBitmapAtViewportCenter`, centres the placed
rect on the viewport centre instead) — no second placement path to keep in sync.

**The three bitmap models — `BitmapPrimitive` (symbol editor), `SchematicBitmap` (schematic canvas
objects), `BitmapShape` (layout) — remain deliberately separate, per the brief's explicit guardrail.**
Different coordinate systems (symbol-editor local units vs. layout DBU), different hosts, no payoff to
unifying them; `LayoutGeometry.Clone`'s `BitmapShape` arm and the other two models' equivalent clone
code are near-identical by convention, not by shared type.

**Locked (a NEW enforcement, not ported from anywhere)** — `BitmapPrimitive.Locked`/`SchematicBitmap`-
adjacent precedent turned out to be persisted-but-never-enforced in this codebase (confirmed by grep: no
caller ever reads `bmp.Locked`), so layout's `BitmapShape.Locked` needed real enforcement built from
scratch: "blocks move and scale, never selection." `LayoutEditorViewModel.Bitmaps.cs`'s
`MovableSelectedIndices` (`indices.Where(... && !IsLockedBitmap(...))`) is the one filter every move
path (`CommitMoveDrag`, `NudgeSelection`, the live drag-preview loop in `RebuildOverlay`) and every scale
path (`ApplyScale`, `TryBeginScaleDrag`) routes through — a locked bitmap stays in the selection set (its
outline still renders, `ShowScaleHandles` still shows handles for a lone selected one) but is excluded
from whatever a move/scale operation actually acts on; a LONE locked-bitmap scale drag has nothing left
to move after filtering, so `TryBeginScaleDrag` returns false and the drag never begins.

**Properties panel** (`LayoutShapePropertiesViewModel`/`LayoutShapePropertiesView.axaml`) — Path (staged
text + a Browse… button, file picker in code-behind per the UI firewall) / Width / Height / Opacity
(0-100%, staged text, no `LayoutUnits` dimension since it isn't a length) / Locked (tri-state checkbox,
null = differs across a multi-bitmap selection, committed immediately like a combo). `ShowNet` (`=>
!ShowBitmap`) hides the Net row for an all-bitmap selection — meaningless for an image.

**Insert Bitmap toolbar button (R-bmp-5)** — Layout: `LayoutEditorView.axaml`'s toolbar, opens a
`StorageProvider` file picker in code-behind, calls `LayoutCanvas.InsertBitmapAtViewportCenter(path)`.
Symbol editor: same button shape, routes through the EXISTING `SymbolEditorViewModel.DropBitmap` via a
new `SymbolEditorCanvas.InsertBitmapAtViewportCenter(path)` (viewport-centre world coords, no new
placement logic) — `IsEnabled="{Binding ViewModel.IsEditable}"` matches every other tool button in that
toolbar. The Layout Editor has no document-level "locked" concept to gate against (confirmed by grep —
nothing else in `LayoutEditorViewModel` reads/writes such a flag), so its button has nothing to disable
against and is always enabled; inventing one was out of scope.

Test file: `LayoutBitmapTests.cs` — persistence round-trip (`$type: "Bitmap"`, byte-identical), Bbox/
Clone/TranslateBy/coordinate-walk geometry, hit-test select/deselect, R-bmp-3's Union-skips-bitmap and
Offset-disabled-with-reason, the R-bmp-2 pixel oracle (a bitmap on a HIGH-ZOrder layer still paints
BENEATH a rect on a LOW-ZOrder layer), broken-path placeholder renders with no exception, `BitmapCache`'s
load-once/invalidate/missing-file/pixel-size behavior, R-bmp-4 sizing (aspect-preserving quarter-viewport-
width, undecodable-file fallback + Messages warning), the right-click Resolve Path…/Refresh Cache VM
entry points, Locked blocking move (multi-selection: others move, the locked bitmap doesn't, still
selected) and scale (lone locked selection: handles show, drag never commits; mixed selection: the
locked bitmap is skipped), and `LayoutFragment` round-trip + translate (the clipboard's pure half). 25
new tests; 2199 Ui.Tests total, all green; full solution green (Firewall 4/4, Core 388/388, Engine
461/462 — 1 pre-existing skip, matching the datadisplay-fix-context-menu-stacking baseline exactly).
**Not interactively verified** (no visual driver in this environment, matching every prior phase) — the
toolbar button placement, drag-and-drop, and the properties-panel Browse… file picker cannot be
exercised headlessly for the same reason every prior phase's dialog/menu/DnD code couldn't be;
correctness rests on the VM-level gate tests above plus direct code reading of the generic (shape-type-
agnostic) machinery each new `BitmapShape` case was added to.

Data Display fix (context menu stacking) — brief-datadisplay-fix-context-menu-stacking.md, 2026-07-27 —
COMPLETE: same class of defect as the layout-canvas context-menu-stacking fix, in `src/Ui/DataDisplay/`.
A convergence job, not a design job — this codebase already contained three correct implementations of
the two patterns needed, two of them in the very file being fixed.

**Audit — four right-click menus in `PlotControl.cs`, three broken:** the **main plot menu** (`_contextMenu
??= BuildContextMenu(); _contextMenu.Open(this);`) was already correct (**Pattern A** — static content,
cache the built instance, never `new`-ed again). `MarkerInfoBoxView.axaml.cs`'s **marker-box menu**
(`var menu = new ContextMenu(); menu.Opening += (_, _) => RebuildContextMenu(menu); ContextMenu = menu;`,
constructed once in `OnDataContextChanged`) was already correct too (**Pattern B** — dynamic content,
one instance, rebuilt on `Opening`, Avalonia owns open/close). The **marker context menu**
(`ShowMarkerContextMenu`), **trace-header menu** (`ShowTraceHeaderContextMenu`), and **table menu**
(was `BuildTableContextMenu`) each constructed a fresh `ContextMenu` and called `.Open(this)` on every
right-click, never tracking or closing the previous one — the identical bug the layout-canvas fix
addressed, just three sites instead of one.

**Fix — converge onto the two patterns already proven correct in these same two files, invent nothing:**
new `_markerContextMenu`/`_traceHeaderContextMenu`/`_tableContextMenu` fields, each `??= new
ContextMenu()` (constructed at most once), repopulated on every click (`menu.Items.Clear()` then
fresh `MenuItem`s — the same discipline `PopulateMarkerMenu` already had), then `menu.Close(); menu.Open(this);`
(`Close()` first so a second right-click while the menu is still up replaces rather than re-opens —
Avalonia's own `Control.ContextMenu`+`Opening` auto-open mechanism doesn't fit here: which of FOUR
different menus opens is decided by hand in `OnPointerReleased` from per-click state
(`_rightClickedMarker`/`_rightClickedTrace`/`_plot?.PlotType`), not by Avalonia's own right-click
gesture recognition on a single declared menu, so the brief's own documented fallback — explicit
`Close()`/`Open()` on a reused instance — is the correct choice here, not the layout fix's `Opening`-
attached-to-XAML approach). **The table menu needed MORE than the brief's own suggested one-line
Pattern-A fix**: `BuildTableContextMenu()` takes no parameters, which the brief flagged as suggestive of
click-independent content — but it actually reads `_rightClickedDataTrace`/`_rightClickedDataFreq` (mutable
fields, not parameters) to decide "Add Marker"'s enabled state AND which cell a click captures, so a
naively-cached build would go stale after the FIRST right-click, silently adding markers at the wrong
cell on every subsequent one. Converted to `PopulateTableContextMenu(ContextMenu menu)` (Pattern B, like
the other two) instead of the brief's suggested `_tableContextMenu ??= BuildTableContextMenu()` — the
brief explicitly asked to "check first," and checking found the click-dependency it flagged as unlikely.

**`MarkerInfoBoxView.PopulateMarkerMenu` needed no change** — already `menu.Items.Clear()` first, always
builds fresh `MenuItem`s, so it was already safe to call repeatedly on the same instance; that's exactly
why Pattern B already worked in the info box and why `ShowMarkerContextMenu` could adopt it unchanged.

**Wider audit (guardrail):** grepped the rest of `src/Ui` for `new ContextMenu`/`.Open(` on a menu —
nothing outside these two files does either; no further sites to report.

Test file: `DataDisplayContextMenuStackingTests.cs` — `PlotControl` is a `Control` subclass and this
project's tests must not call any Avalonia runtime API, so (matching the layout fix's own precedent)
this is a source-text-scan test: exactly one unguarded `new ContextMenu()` remains in `PlotControl.cs`
(`BuildContextMenu()`'s own internal construction, itself only ever called from behind `_contextMenu
??=`), the three previously-broken menus are each guarded and clear `Items` before repopulating and
`Close()` before `Open()`, and `MarkerInfoBoxView.axaml.cs` is unchanged (still exactly one `new
ContextMenu()`, still the `Opening`-rebuild pattern, `PopulateMarkerMenu` still clears first). 5 new
tests; 2174 Ui.Tests total, all green; full solution green (Firewall 4/4, Core 388/388, Engine 461/462 —
1 pre-existing skip, matching the L1j baseline exactly). **Not interactively verified** (no visual
driver in this environment) — please confirm ten consecutive right-clicks on a marker, a trace header,
and the plot table each leave exactly one menu open, and that right-clicking marker A then marker B
shows B's options, not A's stale ones.

L1j post-ship fix — vertex list couldn't scroll (2026-07-27) — COMPLETE: owner report, same day as
L1j shipped. Root cause: the vertex list used a bare `ItemsControl`, on the (wrong) assumption that its
default Fluent-theme template already wires up a `ScrollViewer` the way `ListBox`'s does. It doesn't —
scrolling was silently inert, no error, no visible symptom in the empty/short-list case that got
exercised during development. **Fix:** switched to `ListBox` (still with `ListBox.DataTemplates` for
the `RingHeaderRow`/`VertexRowViewModel` polymorphic dispatch, unchanged) with an EXPLICIT
`ScrollViewer.VerticalScrollBarVisibility="Auto"` (+ `HorizontalScrollBarVisibility="Disabled"`) attached
property — this is not a new pattern: `SymbolPrimitiveInspectorView.axaml`'s own "virtualized coord
list" (`PolylineCoords`, the closest sibling feature in this codebase — an editable, bounded-height row
list in a properties panel) already established exactly this `ListBox` + explicit attached-property
shape, and this fix now matches it instead of diverging. **Rule for any future bounded/virtualized row
list in this codebase: use `ListBox`, not a bare `ItemsControl`, and always set
`ScrollViewer.VerticalScrollBarVisibility` explicitly — do not rely on the theme's default.** No VM
change — `LazyIndexedList`/`RebuildOrRefreshVertexRows`/virtualization semantics (R-L1j-5/R-L1j-6) are
unaffected; this was AXAML-only. Full solution green (Firewall 4/4, Core 388/388, Ui.Tests 2169/2169 —
unchanged count, no new tests since this is a pure control-choice fix with no new VM-observable
behavior; the underlying `LazyIndexedList` gate-11 test already covers the data side). **Not
interactively re-verified** (no visual driver in this environment) — please confirm the vertex list now
scrolls with the mouse wheel/scrollbar for a polygon with more vertices than fit in the panel.

Phase L1j — richer, live Properties Inspector (brief-L1j-properties-inspector.md, 2026-07-27) —
COMPLETE: the Layout Editor's shape-properties panel now updates live during a drag, gained Rect/
RoundedRect width/height/position fields, an editable virtualized vertex list (with holes), and
visible-invalid-state validation uniformly across every field, old and new.

**R-L1j-1 — the inspector reads EFFECTIVE (drag-override-aware) geometry, which is what makes every
field live and why the refresh plumbing needed no change at all.** `LayoutEditorViewModel.
EffectiveShapeAt(int)`/`EffectiveSelectedShapes()` (new) return the live `Overlay.DragOverrides` preview
clone for an index when a drag is in flight, otherwise the committed `Model.Shapes[i]`. `_selected`
(`LayoutShapePropertiesViewModel`) is now built from `EffectiveSelectedShapes()` instead of
`Model.Shapes[i]` directly — that one change is the entire liveness fix; `RefreshFromVm`'s existing
triggers (`_vm.Model.Changed` and `_vm.PropertyChanged` on `Overlay`, both already subscribed since L1c)
already fire on every pointer move of a drag, exactly as the brief's own diagnosis predicted ("the
trigger is not the problem").

**R-L1j-2 — read-only mid-drag, because `_selected` holds throwaway preview clones then.**
`IsEditingEnabled` (`false` whenever `Overlay.DragOverrides.Count > 0`) gates the whole editable region
via one `IsEnabled` binding on the fields' `StackPanel` and the vertex list's `ItemsControl` — fields
stay visible (live) but not typeable. Belt-and-braces: every Commit* method independently re-checks
`DragBlocksEdits()` at the moment it runs, so a commit is refused even if something bypassed the view's
binding (the direct regression gate for this).

**R-L1j-3 — a refresh never clobbers the field currently under the caret.** `SetFocusedField(string?)`
is called by the view's new GotFocus/LostFocus handlers (Tag-keyed, see below); every text write
`RefreshFromVm` makes goes through `SetTextIfNotFocused(fieldKey, value, getter, setter)`, which skips
the one field matching `_focusedField`. LostFocus clears the focus key **before** calling the field's
own Commit* method, so that field's own just-committed reformat is NOT skipped (only a refresh
triggered by something else, while still focused, is protected) — Escape (`RevertField`/`VertexRowViewModel.
Revert`) is the one deliberate bypass, forcing exactly that field back to canonical and clearing its error.

**Width/Height/Position (§2), R-L1j-4 — size and position are two DIFFERENT semantics, not one.**
`ShowRectSize` gates a new block (above the RoundedRect corner-radius field) for any selection that is
all `Rect`/`RoundedRect`. Editing **Width/Height** (`ApplyRectResize`) keeps the minimum corner (X1,Y1)
FIXED and moves the far edge (X2/Y2) — matches how the shape was drawn; the tooltip states this
directly. Editing **X/Y** (`ApplyRectPosition`, the "also add position" addition — kept, not struck)
instead TRANSLATES the shape, preserving width/height. A `RoundedRect`'s `CornerRadius` is clamped to
half the (possibly new) shorter side by `ApplyRectResize` and reported via the new
`LayoutEditorViewModel.ReportWarning` — otherwise shrinking the width below `2×CornerRadius` would
silently produce a geometrically invalid shape. Both helpers chain per-shape
`SetShapeFieldCommand<long>`s (X1/Y1/X2/Y2, and the clamp, when it fires) into one `CompositeCommand`
per multi-selection edit — no `ReplaceShapeCommand`/cloning needed here, since Rect/RoundedRect are
plain-field shapes and `_selected[i]` is the live model reference whenever editing is enabled (R-L1j-2
guarantees no drag is in flight at commit time).

**The vertex list (§3) — last in the panel, shown for exactly one `Polygon`/`Curve`/`Path`.**
Columns: `#` (read-only), `X`/`Y` (editable, `LayoutUnits` fields), `Edge` (read-only — `Line`/`Arc`/
`Cubic`, blank for an open `Path`'s last vertex, which has no outgoing edge; edge CONVERSION stays on
L1d's canvas context menu, never here). Holes (§3.1a) are grouped under their own ring header — "Outer
(12)", "Hole 1 (8)", outer first, via `LayoutShapeEditing.HolesOf`/`SetHoleVertex` (new, mirroring the
existing `XyOf`/`SetVertex` for the outer ring — a hole vertex is always Line-kind, since a hole is
always Clipper2-polygonal, never its own edge list). **Committing a vertex edit is exactly one
`ReplaceShapeCommand`** (`LayoutShapePropertiesViewModel.CommitVertexField`) — the SAME command L1d's
own canvas vertex-drag commits, so undo restores the exact original shape instance at its original
index and the Polygon→Curve promotion rule needs no special-casing here.

**R-L1j-5/R-L1j-6 — the two virtualization traps, both silent, both invisible until a huge imported
polygon freezes the UI.** (5) The vertex list is its OWN bounded region (`Grid RowDefinitions="Auto,*"`,
the fields' `ScrollViewer` capped at `MaxHeight="340"`), never nested inside the fields' `ScrollViewer` —
an unbounded-height `ItemsControl` measures and realizes every item, degrading virtualization to
nothing with zero visible symptom until someone selects a 20,000-vertex polygon. Avalonia's `ItemsControl`
already wraps its content in its own `ScrollViewer` + `VirtualizingStackPanel` by default (no explicit
wrapper `ScrollViewer` was added — that would be the exact same trap one level up). (6) `LazyIndexedList<T>`
(new, `src/Ui/ViewModels/`) is an index-addressed `IReadOnlyList<T>` that constructs (and caches) a row
on FIRST access only — `Count` and "what kind of row is at index i" are computed from ring VERTEX
COUNTS (`BuildRingPlan`, O(numRings)), never by walking every vertex. `RebuildOrRefreshVertexRows`
rebuilds `VertexRows` (a brand-new `LazyIndexedList`) only when the ring STRUCTURE changes (ring/vertex
counts, e.g. insert/remove-vertex or a Polygon→Curve promotion elsewhere); for an unchanged structure —
including every pointer move of a live canvas vertex-drag — it instead calls `RefreshFromShape()` on
only the ALREADY-REALIZED rows (`LazyIndexedList.MaterializedIndices`), so a drag never rebuilds the
collection (would thrash scroll position/focus) and a 20,000-vertex polygon materializes only the ~30
rows actually on screen. `LazyIndexedList.MaterializedCount` is the gate-11 test hook proving this holds.

**Validation (§4) — one visible-invalid-state idiom, reused everywhere, not invented per field.**
Every dimension field parses through `LayoutUnits.TryParse`; on invalid/out-of-range input the Commit*
method sets that field's `XxxError` string (a specific reason — `"Width must be greater than 0"`, not a
generic rejection) and returns WITHOUT touching the field's Text or calling `RefreshFromVm` — the user's
typed text is never silently discarded. `Classes.error="{Binding HasXxxError}"` (a `TextBox.error` style,
`CrfWarningBrush` border — the same brush `TechEditorView`'s validation banner already uses) plus
`ToolTip.Tip="{Binding XxxError}"` is the visible cue, applied uniformly to every field, old and new —
no bespoke per-field error `TextBlock`, matching "reuse the existing idiom." Escape reverts explicitly
(`RevertField`/row `Revert`); LostFocus naturally reverts too once focus has genuinely left (R-L1j-3's
guard no longer protects a field once `_focusedField` is cleared). Multi-selection blank-on-differing/
apply-to-all-as-one-undo-entry was already `ApplyToEach`'s behavior and needed no change.

**View wiring is Tag-keyed, mirroring `TechEditorView`'s `CommitField`/`OnComboSelectionChanged`
dispatcher** — three generic handlers (`OnFieldGotFocus`/`OnFieldLostFocus`/`OnFieldKeyDown`, reading
`TextBox.Tag` as the field key) cover every STATIC field in the panel instead of one handler pair per
field; three more (`OnVertexFieldGotFocus`/`...LostFocus`/`...KeyDown`, reading `Tag` = `"X"`/`"Y"` plus
`DataContext` as the specific `VertexRowViewModel`) cover every vertex row. `LayoutShapePropertiesViewModel.
CommitField(key, text)`/`RevertField(key)` are new generic entry points that dispatch to the SAME
granular `Commit*Text` methods (`CommitCornerRadiusText`, `CommitRectWidthText`, …) that already existed
(or were added) with their original public names/signatures — existing tests calling those directly are
untouched.

Test files: `LayoutShapePropertiesLivenessTests.cs` (gates 2-7, 13 — a `Rect` corner/`Circle` radius/
`RoundedRect` corner-radius handle drag all update their text fields on EVERY pointer move before
release; a whole-shape move drag updates X/Y continuously; undo refreshes the panel; a commit attempted
mid-drag is refused and the model is unchanged, fields re-enable on release; a focused field survives a
refresh triggered by an unrelated model change and only reformats once focus genuinely leaves; Width/
Height keep the min corner fixed while a RoundedRect's corner radius clamps-and-reports; every
`LayoutUnits` input form is accepted; invalid text shows the error state without mutating the model or
discarding what was typed; Escape reverts; a differing multi-selection Rect-width edit applies to all as
one undo entry), `LayoutVertexListTests.cs` (gates 8-12 — visibility restricted to exactly one Polygon/
Curve/Path; a row edit is one `ReplaceShapeCommand` that undoes/redoes at the original index with the
list reflecting it; an invalid vertex commit pushes no command; a hole polygon shows both ring headers
with correct counts and a hole vertex edits correctly; a 20,000-vertex polygon selects near-instantly
with `MaterializedCount == 0` until rows are actually accessed, then stays in the tens; a live canvas
vertex-drag never replaces the `VertexRows` collection instance while updating the realized row's text).
24 new tests; 2169 Ui.Tests total, all green; full solution green (Firewall 4/4, Core 388/388, Engine
461/462 — 1 pre-existing skip, matching the L1-fix-context-menu baseline exactly). **Not interactively
verified** (no visual driver in this environment, matching every prior Layout Editor phase) — the
`ItemsControl` virtualization itself (container realization, actual scroll behavior) cannot be driven
headlessly (this project's test suite must not call any Avalonia runtime API at all — a stricter bar
than the usual "no `Window` subclass" note); correctness rests on `LazyIndexedList`'s own gate-11 test
(proving the DATA STRUCTURE never eagerly materializes) plus direct code reading of the `ItemsControl`
placement (its own default template already provides `ScrollViewer` + `VirtualizingStackPanel` — no
custom wrapper was added that could defeat it).

L1 fix (context menu stacking) — LayoutCanvas built a new ContextMenu per right-click
(brief-L1-fix-context-menu-stacking.md, 2026-07-27) — COMPLETE: owner report — right-clicking the layout
canvas opened a menu, but every subsequent right-click opened ANOTHER one underneath it without
dismissing the first, so N right-clicks left N stacked popups (peeled off one dismissal at a time).
Root cause: `LayoutCanvas.ShowShapeContextMenu` ended with `var menu = new ContextMenu { ItemsSource =
items }; menu.Open(this);` — a brand-new instance, manually opened, never tracked, never closed. This
is the second bug in a row caused by the layout editor diverging from an established house convention
(the Scale dialog's `TextChanged` commit, fixed the same session, was the first) — the fix converges on
the pattern rather than patching the divergent one.

**The reference pattern, copied exactly:** `SymbolEditorCanvas`/`SchematicCanvas` already do this
correctly — ONE `ContextMenu` declared once in the hosting view's XAML (`<ctrl:SymbolEditorCanvas.
ContextMenu><ContextMenu Opening="..."/></...>`), and the canvas's `OnPointerPressed` right-click branch
only RECORDS what was under the click into a field; Avalonia opens the declared menu itself and raises
`Opening`, where the handler rebuilds `ItemsSource` fresh (or cancels). `LayoutCanvas` now follows the
identical shape: **`LayoutEditorView.axaml`** declares the single `<ContextMenu Opening=
"OnLayoutContextMenuOpening"/>` on `LayoutCanvasCtrl`; **`LayoutCanvas.OnPointerPressed`**'s right-click
branch (non-Ctrl) now only sets `ContextMenuTarget = (wx, wy)` — no `e.Handled = true` here, mirroring
the reference exactly, since marking the pointer-pressed event handled risks suppressing Avalonia's own
right-click-opens-ContextMenu gesture recognition (a separate mechanism from ours); the Ctrl+right-click
branch (routes to ordinary press handling per the macOS one-button-mouse convention) explicitly clears
`ContextMenuTarget = null` before returning, so the `Opening` handler cancels via `e.Cancel = true`
rather than "just not calling Show" as before. **`LayoutCanvas.BuildContextMenuItems(wx, wy)`**
(`internal`, replacing the old `private void ShowShapeContextMenu`) is the exact same item-building logic
(edge-conversion items, Delete Vertex, `AddBooleanAndFlattenMenuItems`'s selection-scoped booleans/
offset/scale/repair/flatten) — unchanged in content, only its ending changed: it now `return`s the
`List<object>` instead of constructing-and-opening a `ContextMenu` itself. **`LayoutEditorView.axaml.cs`
's `OnLayoutContextMenuOpening`** is the thin glue: `LayoutCanvasCtrl.ConsumeContextMenuTarget()` (an
atomic get-and-clear, so a stale target can never leak into a later, unrelated opening) returns null →
cancel; otherwise `BuildContextMenuItems` runs fresh and its result becomes `ItemsSource`. **Fresh
`MenuItem`s every opening, never reused item instances with re-subscribed `Click`** — that would fire an
action N times on the Nth opening, a worse bug than the one being fixed; `BuildContextMenuItems`
already built fresh items every call before this fix (nothing to change there), so this property was
free, not something newly engineered.

**§4 audit — the other canvases:** `SchematicCanvas` was checked and is NOT affected — it already uses
the correct framework-owned pattern (`ContextMenuTargetId`, recorded-only on right-click, matching
`SymbolEditorCanvas`'s `BitmapContextPrimIdx`). `MarkerInfoBoxView.axaml.cs` (Data Display) is also
correct — a single `ContextMenu` instance assigned once in `OnDataContextChanged`, rebuilt via its own
`Opening` handler. **`PlotControl.cs` (Data Display's plot canvas) DOES have this bug, in THREE of its
four context-menu paths**: `ShowMarkerContextMenu`, `ShowTraceHeaderContextMenu`, and the Table
right-click path via `BuildTableContextMenu()` all construct `new ContextMenu()` fresh and call
`.Open(this)` on every invocation with nothing tracked or closed — the identical stacking bug this brief
fixed for layout. (The plain-canvas right-click path in the same file, `_contextMenu ??=
BuildContextMenu()`, is already correctly cached and reused — so the file is inconsistent internally,
not uniformly wrong.) **Not fixed in this pass per the brief's explicit guardrail** ("layout canvas
context menu only... do not fix [another canvas] even if it has the same pattern... it gets its own
brief") — flagging here so a follow-up brief can target `PlotControl.cs` specifically; the fix shape is
already proven (mirror `MarkerInfoBoxView.axaml.cs`'s own correct sibling code in the same file family).

Test file: `LayoutContextMenuStackingTests.cs` — `LayoutCanvas` is a `Control` subclass and this
project's test suite must not call any Avalonia runtime API (`tests/Ui.Tests/CircuitRF.Ui.Tests.csproj`'s
own header comment), so the menu-building/event-wiring behavior itself cannot be driven directly —
matching every prior Layout Editor phase's note that context-menu construction "cannot be unit-tested
headlessly." Per the brief's own fallback (gate 2's second sentence): 3 structural/source-level tests
assert the invariant that makes stacking impossible by construction — `LayoutCanvas.cs` contains no `new
ContextMenu` and no `.Open(this)` call anywhere, and `LayoutEditorView.axaml` declares exactly one
`<ContextMenu>` element, wired to `OnLayoutContextMenuOpening`. 3 new tests; 2145 Ui.Tests total, all
green; full solution green (Firewall 4/4, Core 388/388, Engine 461/462 — 1 pre-existing skip, matching
the L1i baseline exactly). Gates 3–7 (rebuild-per-opening content correctness, click-fires-exactly-once,
Ctrl-suppression, enablement preservation) rest on code reading and the unchanged-by-this-fix VM-level
enablement tests (`LayoutEnablementTests.cs`) — not on a new behavioral test, for the same "cannot
construct a Control headlessly" reason every prior phase's menu/dialog code has cited. **Not
interactively verified** (no visual driver in this environment) — please confirm ten consecutive
right-clicks now leave exactly one menu open.

Phase L1i — live marquee selection feedback (brief-L1i-live-marquee-selection.md, 2026-07-27) —
COMPLETE: one owner report — while dragging a selection marquee, nothing highlighted until release, so
the user couldn't see what they were about to select. Root cause: `LayoutEditorViewModel.CommitMarquee`
(the hit computation) was called only from `HandleSelectRelease`; the move handler just redrew the
rectangle.

**R-L1i-1 — one hit computation, two callers, by construction, not by discipline.** `CommitMarquee`'s
old body (bbox scan + enclose/crossing test + Shift/Ctrl combination against `_marqueeBaseSelection`)
is now `ComputeMarqueeSelection(curX, curY)` — called from the move handler on every qualifying pointer
move AND from `CommitMarquee` (now just `SetSelection(ComputeMarqueeSelection(...))`) at release. This
is the point of the refactor: if the preview computed hits differently from the commit, the highlight
would lie about the outcome — with one shared function, that divergence is structurally impossible, not
just untested. `RebuildOverlay` computes ONE "effective highlight" — `_marqueePreview` while
`_selectDragKind == Marquee`, `_selectedIndices` otherwise — so there is one render path, not a
committed one and a preview one; the highlight uses the same accent as a settled selection.

**R-L1i-2 — the preview NEVER writes into `_selectedIndices`.** `ComputeMarqueeSelection` mutates and
returns a separate reused scratch buffer, `_marqueePreview` — the modifier combination is computed
*against* `_marqueeBaseSelection` (the pre-drag selection snapshot), so a preview that wrote into the
real selection would corrupt the very base it's derived from on the next pointer move. Escape
(`CancelMarqueeIfActive`, called from `CancelDrawOp` before `_selectDragKind` resets) and an off-canvas
button-release both clear `_marqueePreview` and restore `SelectionStatusText` from the (untouched)
`_selectedIndices` — never a special "was it a marquee?" branch, since that field was simply never
written during the drag.

**R-L1i-3 — the preview IS the prospective final selection, not the raw hits.**
`ComputeMarqueeSelection` already folds in Shift (add) and Ctrl (toggle) against the base selection, so
highlighting its result directly means a Ctrl-drag crossing an already-selected shape visibly
**un-highlights** it mid-drag — the owner's "unhighlight" request falls out of this rule with no
separate code path.

**R-L1i-4 — solid = enclose, dashed = crossing** (`LayoutRenderer.DrawMarquee`, keyed off
`LayoutMarquee.IsLeftToRight`) — the standard CAD affordance, now load-bearing rather than decorative
since the highlight set can visibly change when a drag crosses back over the press point.

**Perf — skip the recompute below one device pixel of movement**, a genuinely realistic gate (not just
a micro-optimization): pointer-move events far outnumber meaningful rectangle changes.
`LayoutEditorViewModel.OnPointerMoved` gained a `pixelDbu` parameter (world-space size of one device
pixel at the current zoom, computed fresh per call by `LayoutCanvas.OnePixelDbu()` — deliberately NOT
derived from the existing several-pixel `HitTolDbu()`, a different concern); the move handler compares
the new corner against `_marqueeLastComputedCorner` (the corner used for the last actual compute, not
the raw pointer position) and only calls `ComputeMarqueeSelection` when it moved by at least that much.
`pixelDbu` defaults to 0 (always recompute) so every pre-existing call site/test that doesn't pass it is
unaffected. `MarqueeRecomputeCount` (internal, `InternalsVisibleTo`) is test-only instrumentation
proving the skip actually skips (gate 9) — there was no other way to observe "did the expensive path
run" without it. `_marqueeHitsScratch`/`_marqueePreview` are both persistent, cleared-and-refilled
buffers, never reallocated per pointer move.

Test file: `LayoutMarqueePreviewTests.cs` — gate 2 (live highlight before release, un-highlight on
leaving), gate 3 (preview-at-release-moment == committed selection, for all three modifier states,
asserted by direct comparison rather than a reimplementation), gate 4 (plain/Shift/Ctrl preview
semantics, including the Ctrl un-highlight), gate 5 (50 intermediate moves under Shift match one move
to the same endpoint — the direct regression for "does the preview corrupt the base"), gate 6 (a
direction flip mid-drag changes both the rectangle style and the highlight set, and the commit matches
the FINAL direction), gate 7 (Escape restores the rendered highlight, not just the model), gate 8
(hidden/non-selectable layers never preview), gate 9 (`MarqueeRecomputeCount` proves sub-pixel moves are
skipped and a crossing move recomputes), gate 10 (a full gesture through `LayoutViewport.ScreenToWorldX/
Y` on both starter technologies at a realistic default viewport). 15 new tests; 2142 Ui.Tests total, all
green; full solution green (Firewall 4/4, Core 388/388, Engine 461/462 — 1 pre-existing skip, matching
the L1h-fix baseline exactly). **Not interactively verified** (no visual driver in this environment,
matching every prior Layout Editor phase) — correctness rests on the gate tests above, which drive the
exact same public `OnPointerPressed`/`OnPointerMoved`/`OnPointerReleased` state machine the canvas calls
and read the live highlight through `Overlay.SelectedIndices`, the same property the renderer consumes.

L1h fix (Scale dialog) — the untestable shim held the bug, not the linker (2026-07-27) — COMPLETE: third
report on "typing a Width/Height into the Scale dialog gets silently corrupted." Two prior rounds fixed
the wrong layer — **`ScaleFieldLinker`'s exact-factor math was already correct through both of them.**
`ScaleDialog.axaml.cs` is a `Window` subclass, the one layer that cannot be constructed in this project's
headless test suite — so it was the only place a policy bug could hide and survive two "fixed and tested"
rounds. The actual defect: every box committed on **`TextChanged`** (per keystroke). A programmatic write
to one box (echoing a just-typed value into another, both rounded to 4 decimals for display) could itself
raise a live commit if its delivery landed after the `_updating` reentrancy guard had already been reset —
`OnHeightChanged` would then re-derive `FactorY` from that ROUNDED height text, and because Uniform mode
cross-assigns, silently overwrite the EXACT `FactorX` the user's own edit had just set. The fix (per
`docs/sonnet-briefs/brief-L1h-fix-scale-dialog-width.md`) is to **remove the policy from the shim, not
patch it again**: `ScaleFieldLinker` (`src/Ui/Views/Dialogs/ScaleFieldLinker.cs`) now owns "which field is
authoritative and must never be written back" directly, and every rule that makes the bug structurally
impossible lives there, where it's testable.

**`ScaleField` enum + `Edit(field, text)`/`DisplayFor(field)` replace the old `TrySet*Text` methods.**
`Edit` records the committed field as `AuthoritativeField`; `DisplayFor` returns `null` for that field —
never a string to write back — so `ScaleDialog.axaml.cs` has no `skip*` flags left to pass correctly or
forget: it loops all four boxes and assigns `DisplayFor(field)` only where non-null. **R-fix-1 — commit is
LostFocus + Enter, never TextChanged**, matching every other typed dimension field in this editor
(`LayoutShapePropertiesView.axaml.cs`'s own header comment states the same convention) — per-keystroke
commit was wrong on its own terms regardless of the reentrancy bug (typing "400" would have momentarily
committed a width of "4", then "40", rewriting the other fields through nonsense on the way).

**R-fix-5 — a genuinely realistic second trigger, closed by treating a same-text commit as a pure no-op.**
Even with LostFocus/Enter, tabbing THROUGH an unedited field still fires its LostFocus with whatever the
last refresh wrote in its box — `DisplayFor(Height)`'s own rounded text, not anything the user typed.
`Edit` now compares the incoming text against the field's OWN current display (`RawDisplayFor`, the
authoritative-gating-free internal getter `DisplayFor` wraps) **before** doing anything else; an exact
match is a no-op — returns `true`, but recomputes nothing and leaves `AuthoritativeField` untouched. This
is what actually makes "Uniform mode must not let a derived field overwrite the authoritative one" hold:
without it, tabbing through Height after typing Width would still silently corrupt `FactorX`, LostFocus
convention or not.

**Test file (`ScaleFieldLinkerTests.cs`, rewritten):** now exercises the public `Edit`/`DisplayFor` API
(the old `TrySetWidthText` etc. are `private`, only reachable through `Edit`) — the headline typed-width
survival case, idempotent-refresh (100 re-reads never drift `FactorX`), the "no round-trip through display
text" invariant stated directly (`DisplayFor` returns null for the authoritative field), and the R-fix-5
regression itself: type Width, Tab through Height untouched, assert `Edit` accepts it as a no-op and
`FactorX` is bit-identical to before. 12 Ui.Tests (was 11 — one gate replaced, no count regression). Full
solution green (Firewall 4/4, Core 388/388, Ui.Tests 2127/2127, Engine 461/462 — 1 pre-existing skip).
**Still not interactively verified** (no visual driver in this environment) — gate 6 of the brief (type
400, Tab away, confirm the box still reads 400, click Scale, confirm the resulting shape measures 400) is
manual and was not run; correctness rests on the linker-level tests above plus the shim being reduced to
a loop with no decision left to get wrong.

L1h post-ship fix — Scale dialog button centering + Width/Height not respected (2026-07-27) — COMPLETE:
two owner reports after trying L1h. **(1) Cancel/Scale button text not centered:** `HorizontalContentAlignment="Center"`
added to both buttons in `ScaleDialog.axaml` — trivial. **(2) Typing a Width/Height was silently corrupted**
(e.g. typing `400` redisplayed as `400.9001`) — reported twice; the first fix attempt (tracking `_factorX`/
`_factorY` as private fields on `ScaleDialog.axaml.cs` read at commit) built clean and passed the full suite,
but the owner reported the bug persisting. Root cause, confirmed by re-reading rather than re-guessing: nothing
in the shipped code re-parsed the rounded `"0.####"` Factor display string — the most likely explanation for
"still broken" is that the owner was re-testing an already-running (unrebuilt) compiled desktop app, since this
project has no interactive GUI driver available to confirm directly. Rather than re-assert the same fix, the
live field-linking logic was extracted into a new pure, Avalonia-free class, **`ScaleFieldLinker`**
(`src/Ui/Views/Dialogs/ScaleFieldLinker.cs`) — `ScaleDialog.axaml.cs` is now a thin shim that only wires TextBox
events to it. `FactorX`/`FactorY` are the sole source of truth for a commit (full-precision `double`);
`FactorText`/`FactorYText` (`"0.####"`) exist ONLY for redisplay and are never re-parsed. `TrySetWidthText`/
`TrySetHeightText` derive the exact factor via `(double)parsedDbu / origDbu` directly from the parsed DBU value,
never through any rounded intermediate. This is the same class of fix the rest of L1h already established
(pull the logic that needs a real test into a framework-free class, since a `Window` subclass cannot be
constructed headlessly here) — `ScaleFieldLinkerTests.cs` (11 tests) pins the exact reported scenario with a
deliberately unfriendly fixture (`3,728,000 / 1,000,000` DBU) where `400/3728` needs more precision than the
4-decimal display can hold, proving the round-trip is exact and that redisplaying the rounded factor repeatedly
never drifts the typed width. 2126 Ui.Tests total (11 new), all green; full solution green (Firewall 4/4, Core
388/388, Engine 461/462 — 1 pre-existing skip). **If width/height still looks wrong after this fix, fully quit
and relaunch the compiled app** — this is a desktop executable, not a hot-reloading dev server; re-testing an
already-running instance would not pick up either fix.

Phase L1h — Scale, and fixing the shape context menu (brief-L1h-scale-and-context-menu.md, 2026-07-27)
— COMPLETE: two owner-reported items. The context menu's three confusing commands (`Merge`, `Flatten
to Polygon`, `Flatten to Polygon…`) are gone/collapsed, every selection-scoped command now says why
it's disabled instead of silently vanishing, and the layout editor gained **Scale** — a numeric
dialog and bbox-handle mouse drags sharing one set of semantics.

**Why `Merge` was removed rather than fixed, and `Union` is now per-layer (R-L1h-1):** L1e's `Merge`
("union restricted to shapes sharing a layer, applied per layer") and `Union` ("all selected shapes
merged") were IDENTICAL for the overwhelmingly common single-layer selection — two commands differing
only in a subtlety nobody reads a tooltip for. `ApplyUnion()` now does exactly what `ApplyMerge()`
used to (`LayoutBooleans.Merge` — unchanged, already existed, already correctly per-layer-grouped),
and `Merge`/`CanMergeSelection`/`MergeSelectionCommand` are deleted outright rather than kept as a
second, redundant answer. Intersect/Difference/XOR are **not** grouped per layer (the brief's own
R-L1h-1 and gate 2 only cover Union) — they still fold across the whole selection exactly as L1e built
them — but now share Union's enablement rule ("≥2 selected shapes share a layer"), since combining
across layers with no shared context was never a meaningful boolean input; cross-layer combination,
when genuinely intended, is Move-to-Layer then Union — two explicit steps, never one silent one.

**The one-Flatten-entry decision (R-L1h-2):** the no-dialog "Flatten to Polygon" menu item is gone;
"Flatten to Polygon…" is the ONLY surviving entry and it always prompts — flattening is irreversible
except by undo and its resolution is the whole point of the operation, so the tolerance is shown and
confirmed, never inferred. `FlattenSelectionToPolygon(null)` is now a documented no-op (kept only
because `null` is also the harmless "nothing selected" shape every other `Apply*` method already
uses) — the removed call site (`FlattenSelectionToPolygon(null)` from the menu) is simply gone.
**R-L1h-2a — the dialog never writes its chosen tolerance back onto any shape**: every shape it
touches stops being curved (a flattened circle is a `PolygonShape`, which has no `FlattenTolDbu` field
at all), so there is structurally nowhere for a written-back value to land. **R-L1h-2b — the tolerance
LIVES in the properties panel**, which is exactly what the dialog pre-fills from
(`LayoutFlattener.OwnTolDbu`, a new sibling of `ResolveTolDbu` that reports which of its two branches
— the shape's own value vs. the technology default — actually won, without duplicating the resolver)
— the two surfaces agree by reading the same source, never by writing to each other. §1.3.0's real
prerequisite gap: `CircleShape`/`RoundedRectShape` had NO `FlattenTolDbu` field at all before this
phase, contradicting §3.2 R9b ("every curved primitive carries a flatten tolerance") — both gained the
field (additive, nullable, **no `FormatVersion` bump** — the same `Holes` precedent), and
`LayoutShapePropertiesViewModel`'s gating predicate/getter/setter and `LayoutFlattener.ResolveTolDbu`
were all widened from `CurveShape or PathShape` to include both.

**R-L1h-3 is now a standing rule for every future selection-scoped command in this editor: a command
is either disabled with a stated reason, or it does something — never a silent no-op.** New shared
type `LayoutCommandAvailability` (`bool CanExecute, string? DisabledReason`) — every selection-gated
command (`BooleanOpAvailability`, `OffsetAvailability`, `ScaleAvailability`, `FlattenAvailability`,
`RepairAvailability`, `DeleteVertexAvailability`, `CutCopyDeleteDuplicateAvailability`, the static
`PasteAvailability(bool)`) answers through this one type, so the menu, a future toolbar, and any
keyboard binding can never disagree. `LayoutCanvas.AddBooleanAndFlattenMenuItems` now ALWAYS adds
every item (never conditionally omits one) and sets `IsEnabled`/`ToolTip.Tip` from the availability —
disable, don't hide, so menu position stays stable and a missing item never reads as a bug. The
companion half: a command that is legitimately enabled but achieves nothing — Union of two same-layer
shapes that don't overlap — reports through Messages (`"Union: selected shapes did not overlap —
nothing was combined."`) rather than appearing to silently succeed; detected as `added.Count >=
operands.Count` per layer group, since Clipper2 does not annihilate operands that never touch (the
existing `result.Shapes.Count == 0` check, still correct for Intersect/XOR's genuinely-empty-result
case, does NOT catch this — a disjoint Union still produces one output shape per input). **Scope note:**
`Cut`/`Copy`/`Delete`/`Duplicate`/`Paste`/`Paste in Place` gained availability properties (for
headless testability against the brief's audit table) but were deliberately NOT added as new context-
menu items — they were never the owner's reported problem (keyboard-only, unreported, working), and
`Paste`'s availability needs an async clipboard peek this phase didn't wire into the menu. `Align`/
`Distribute`/a dedicated `Move to Layer`/`Set Net` command do not exist anywhere in this codebase —
the brief's §1.5 table lists them for completeness of the pattern, not as things this phase built;
building them is future scope, not a gap in this phase.

**R-L1h-4/R-L1h-5 — Scale's mouse handles and the three handle-mode rows:** corner handles scale
uniformly (factor = drag distance from anchor ÷ original distance); side handles stretch ONE axis
only (the handle's own axis; the other factor stays 1.0) — no modifier decides which, so non-uniform
scaling is a deliberate act (drag a side) rather than an accidental one (Shift-something). Anchor is
the OPPOSITE corner/side (`LayoutScaleHandles.Opposite`, `(Index + 2) % 4` in either handle vocabulary
— corners and sides each reuse `LayoutHandles`' own existing 0..3 conventions so "opposite" is one
formula); **Alt** anchors the selection's bbox centre instead. Handle modes, driven by a NEW property
`ShowScaleHandles` (`_selectedIndices.Count >= 2 || (Count == 1 && ScaleModeActive)`): one shape shows
L1d's vertex/edge/bulge/control-point handles, unchanged; two or more shapes show bbox scale handles
(new — a multi-selection previously had NO handles at all); one shape with **Scale mode** toggled on
(new context-menu item, `ToggleScaleModeCommand`) TEMPORARILY replaces L1d's handles with bbox scale
handles, and **Escape (with no drag in progress) exits Scale mode and restores L1d's handles without
touching the selection** — a new, deliberately separate branch in `OnKeyDown`'s Escape handling, ahead
of the general "cancel active op" check, since toggling a mode off is not the same kind of action as
cancelling a drag. The mouse-drag state machine (`_scaleDragKind`/`_scaleDragIndices`/
`_scaleDragOriginals` in `LayoutEditorViewModel.Scale.cs`) is a THIRD state machine living alongside
`_selectDragKind` (Move/Marquee) and `_handleDragKind` (L1d reshape) — checked first in
`HandleSelectPress`/`Move`/`Release`, exactly like those two already were, and reusing
`Overlay.DragOverrides` for the live preview (already N-shape-capable from L1c's move-drag). Typed
override mid-drag (`CommitTypedScale`) mirrors L1b's typed Rect W/H commit: text that parses as a bare
number is a factor, text that parses as a dimension is the resulting size along the dragged axis —
either commits the drag immediately at that exact value, discarding wherever the pointer currently is.

**R-L1h-6 — one shared coordinate walk, not three hand-maintained copies.** New
`LayoutCoordinateWalk.Transform(shape, LayoutCoordinateTransform)` (`LayoutCoordinateTransform` = `{X,
Y, Magnitude}`, three `Func<long,long>`, with `.Uniform(f)` for the degenerate case where all three
coincide) is now the ONE place that walks the full field list: outer vertices, hole rings, cubic
control points, circle radius, rounded-rect corner radius, path width, via pad/drill, label
position/height, `FlattenTolDbu` — bulge is NEVER in this list, deliberately (dimensionless, would
silently change curvature). Before this phase, `LayoutScaling.ScaleShape` (DBU resolution change) and
`LayoutFragment.RescaleShape` (paste rescale) were two independently hand-maintained copies of this
EXACT switch — proven to already be genuinely at risk of drift, not a hypothetical: both had
independently omitted `CircleShape`/`RoundedRectShape.FlattenTolDbu` identically, simply because
neither field existed yet. Both now call `LayoutCoordinateWalk.Transform` instead of their own copy;
Scale (`LayoutEditorViewModel.Scale.cs`'s `BuildScaledShapes`) is the third caller and was built
against the shared walk from day one. `Magnitude` exists because a uniform transform is the case where
`X`/`Y`/`Magnitude` all coincide, but a non-uniform Scale needs `X`/`Y` to differ — for length fields
with no single axis of their own (radius, corner radius, width, pad/drill, label height, flatten
tolerance), there is no EXACT answer under a non-uniform transform, so Scale supplies the isotropic-
equivalent factor `sqrt(fx*fy)` rather than leaving them untouched. `LayoutScaling`'s read-only
coarsening pre-scan (`ScanShape`) is a deliberately separate, untouched code path (it needs per-field
offender LABELS the mutate-only `Transform` signature has no room for) — it gained the same two new
`FlattenTolDbu` checks by hand, since it is the one place in this codebase that is allowed to still be
a hand-maintained parallel list (it always was, even before this phase — the mutate/scan duality is
`LayoutScaling`'s own internal concern, not the cross-file drift R-L1h-6 targets).

**R-L1h-7 — non-uniform scale promotes arcs to cubics, because cubics are closed under affine
transforms and circular arcs are not.** A circular arc scaled non-uniformly is an ellipse, which the
edge-list model cannot represent (`Line`/`Arc`/`Cubic` only) — new `LayoutArcPromotion.PromoteArcsToCubics`
converts every `Arc` edge (and a `CircleShape`'s implicit one) to `Cubic` edges FIRST, exactly the same
"promote to the more general representation because the simpler one cannot express the result" move as
L1d's Polygon→Curve rule (R-L1d-3). A `CircleShape` becomes a `CurveShape` with exactly 4 cubic
quadrants (the standard circle-as-4-Béziers construction, `kappa = 0.5522847…`, accurate to a small
fraction of a percent of radius — the same convention vector tools use for the same reason); an
existing `Arc` edge inside a `Curve`/`Path` is split into ≤90°-sweep cubic segments (more than one
cubic when the arc's sweep exceeds 90°, since one cubic per segment is only accurate to that range) via
the standard control-point formula `d = (4/3)tan(Δθ/4)·r` along each endpoint's tangent — general
enough to cover both the whole-circle promotion and any arbitrary Arc edge, not just literal circles.
**Only under a NON-uniform scale** — under a uniform one, arcs stay arcs and a `Circle` stays a
`Circle` (checked once, `Math.Abs(factorX - factorY) < 1e-9`, in `BuildScaledShapes`, shared by both
the numeric dialog and the mouse-drag commit so the two can never disagree). The conversion is reported
once per operation through Messages (`"Scale: circular arc(s) were converted to cubic curves to scale
non-uniformly."`), never silently.

**Trap 2 — rounding, and scaling is deliberately NOT exactly reversible.** Every scaled coordinate
rounds to the nearest DBU (`MidpointRounding.AwayFromZero`, matching `LayoutFragment.Rescale`'s existing
convention) and is **never** snapped to the snap grid — off-grid vertices are legitimate (§1.5 R5), and
snapping a scaled result would deform it, the same mistake R-L1c-3 already avoids for Move. This means
scaling by 3 then by 1/3 may leave a shape a DBU or two off from where it started — a direct, unavoidable
consequence of integer coordinate storage, not a bug to chase. **Guards**: a factor of 0 or negative is
rejected with a Messages error and the model is left untouched (mirroring is a separate, out-of-scope
operation); a scale that would shrink any selected shape below 1 DBU in its defining dimension
(`WouldCollapse` — bbox width/height, circle radius, path width, via pad/drill, label height) is
rejected WHOLESALE (no partial application) and named in the error.

**Numeric vs. mouse: one semantics, shared by construction, not by discipline.** Both
`LayoutEditorViewModel.ApplyScale` (the `ScaleDialog`'s numeric path) and the mouse-drag commit
(`CommitScaleDrag`) call the SAME private `BuildScaledShapes`/`WouldCollapse` — there is no separate
`ScaleShapesCommand` type; both paths build the scaled shape list and hand it to the existing
`ReplaceShapesCommand` (N→N, already exactly the right shape for "possibly-type-changing replace,
restore at original index" — the identical precedent L1d's Polygon→Curve promotion already established),
so "share one set of semantics" (§2.1) is enforced by the two callers literally invoking the same method,
not by keeping two implementations in sync by hand.

Test files: `LayoutEnablementTests.cs` (the full R-L1h-3 audit — every table row's enabled/disabled
CanExecute plus non-empty reason strings), `LayoutScaleTests.cs` (the numeric path: exact factor math,
all 3 representative anchors, the full R-L1h-6 field-list fixture with a hole/cubic/path-width/via/label
and bulge-unchanged, the shared-traversal-is-actually-shared comparison against both
`LayoutScaling.TryChangeResolution` and `LayoutFragment.Rescale` at matching ratios, non-uniform arc
promotion with a flattened-outline-vs-analytic-ellipse check, rounding/no-snap, both guards, one undo
entry), `LayoutScaleGesturesTests.cs` (mouse: corner/side drag, Alt-anchors-centre, typed factor AND
typed size override mid-drag, Escape-pushes-nothing, all three handle-mode rows including Escape-exits-
Scale-mode, and one drag driven through actual screen-pixel `LayoutViewport` conversion per the standing
screen→world rule), plus additions to `LayoutBooleanOperationsViewModelTests.cs` (Union-groups-by-layer
with one undo entry, the disjoint-shapes Messages note, `FlattenSelectionToPolygon(null)` is now a no-op,
`OwnTolDbu`, `PreviewFlattenVertexCounts`), `LayoutShapePropertiesViewModelTests.cs` (the tolerance field
reachable on all four curved types, a mixed-four-type selection applying as one undo entry, the inherited-
value placeholder text), and `LayoutPersistenceTests.cs` (the two new `FlattenTolDbu` fields round-trip
with no `FormatVersion` change, and a hand-authored pre-L1h `.clay` JSON — missing the fields entirely —
still loads). 66 new tests; 2115 Ui.Tests total, all green; full solution green (Firewall 4/4, Core
388/388, Engine 461/462 — 1 pre-existing skip, matching the L1g baseline exactly). **Not interactively
verified** (no visual driver in this environment, matching every prior Layout Editor phase) — the
context-menu construction, `ScaleDialog`'s live factor/size linking, and the bbox-handle rendering
cannot be unit-tested headlessly for the same reason every prior phase's dialog/menu/render code
couldn't be; correctness rests on the VM-level gate tests above, which drive the exact same public
methods (`ApplyScale`, the `OnPointerPressed`/`Moved`/`Released` gesture state machine, and the
`LayoutCommandAvailability` properties) the dialog/menu/renderer call.

Phase L1g — technology retargeting (brief-L1g-technology-retarget.md, 2026-07-27) — COMPLETE: **Phase
L1 is now complete, including L1g** (L1f's own "Phase L1 is now complete" below was written one brief
early). Two owner-reported gaps closed with one shared component: there is now a UI to change a
layout's technology, and cross-technology paste can no longer silently mangle geometry.

**Why `GetMissingLayers` was the wrong question.** It asked *"which layer keys are absent from the
destination technology?"* — but both starter technologies use the identical `(1,0)`..`(8,0)` key
range with completely different meanings (PCB's `(7,0)` is Drill; MMIC GaAs's `(7,0)` is Substrate).
So pasting a PCB selection into an MMIC layout found **nothing missing** — no dialog, no warning,
every shape silently adopted a semantically unrelated layer (Drill geometry rendered as Substrate,
Soldermask as Via). This is not a violation of §2.1's "identity is `(Layer, Datatype)`" rule — that
rule is correct *within* one technology. **Across** two technologies the numeric key carries no
meaning at all, because each process numbers its own layers independently; names are what survive the
crossing. The right question is *"which layers need the user's confirmation?"*, answered by the new
`LayoutLayerMapping.Propose` (`src/Ui/Layout/LayoutLayerMapping.cs`, framework-free) — shared verbatim
by both cross-technology paste (replacing L1f's `GetMissingLayers` trigger, now deleted — nothing else
called it) and the new technology-retargeting command, so the answer can never drift between the two
callers (R-L1g-1).

**Matching order is name-before-number, and here is the exact reason it has to be:** `Propose` checks
1) same key + same name (case-/whitespace-insensitive) → `SameKeySameName`, 2) exact name match on a
*different* key → `ExactName`, 3) same key + different name → `SameKeyDifferentName` (the Drill→
Substrate trap itself — proposed for display, but **never pre-selected**), 4) otherwise →
`NoMatch`. Checking name-match-at-a-different-key (step 2) *before* falling back to same-key (step 3)
is what makes a confident rename beat a low-confidence numeric coincidence — pinned by
`LayoutLayerMappingTests.Propose_ExactNameMatch_BeatsACompetingSameKeyNumericMatch`. `(Layer,
Datatype)` still identifies a layer *within* one technology (§2.1, unchanged); it identifies nothing
at all *across* two, which is exactly why name has to be tried first.

**R-L1g-2 is the single rule that keeps ordinary same-tech paste frictionless while making
cross-tech paste safe:** confirmation (the shared `LayerMappingDialog`, one table row per source
layer, sorted by shape count descending, replacing L1f's per-key `LayerReconciliationDialog` loop —
now deleted) is required whenever **any** row is `SameKeyDifferentName` or `NoMatch`
(`LayoutLayerMapping.RequiresConfirmation`); if every row is `SameKeySameName` or `ExactName`
(the same-technology case, or a confidently-renamed layer), the mapping applies silently. One rule,
checked once, shared by both callers — never two independent "should I ask?" decisions that could
answer differently.

**`RetargetTechnologyCommand`** (`src/Ui/Commands/Layout/`) is the one undo entry §4 requires:
rewrites `LayoutView.TechRef`, every affected shape's `LayerKey` (via a captured `(Index, Before,
After)` list — geometry itself is never touched, so it stays byte-identical), and — only when the
user checks the opt-in box — `DisplayUnit`/`SnapDbu`. Undo restores all of it together from one
before/after `LayoutEditorViewModel.RetargetState` snapshot; a half-undone retarget would be worse
than no undo at all. **`DbuPerMicron` is deliberately never touched by retargeting** — it is not even
a field on `Technology`, and resolution is a property of the layout, not of the process; a different
resolution remains `LayoutScaling.TryChangeResolution`, a separate, guarded operation. **Unit adoption
is opt-in, default OFF** — silently overwriting a user's working `DisplayUnit`/`SnapDbu` mid-retarget
is exactly the kind of helpfulness that erodes trust; the `ChangeTechnologyDialog` checkbox must be
checked explicitly, and even then only `DisplayUnit`/`SnapDbu` move, never `DbuPerMicron`.

**Entry point (Gap 1 — there was previously no UI to change a layout's technology at all):** a
"Technology: *PCB 2-Layer* ▾" button in the metadata bar (`LayoutEditorView.axaml`, replacing the
plain readout `TextBlock`) is the ONE entry point. The brief's literal "a Layout-menu item" was tried
as a second entry point on the shape context menu (this codebase has no top-level "Layout" menu to
hang a real menu-bar item from) and then **removed on owner review**: retargeting is a whole-layout
operation, not a per-shape one, and a right-click on a shape is the wrong place to reach for it when a
single, always-visible metadata-bar affordance already covers the case — do not re-add it there.
`ChangeTechnologyDialog` offers **(Workspace default)**
(writes `TechRef = null`, always present and selectable, not just reachable by never having chosen —
L0c's convention), every `.ctech` in the workspace's `tech/` folder, and **Browse…** for one outside
the workspace; `LayoutEditorViewModel.WorkspaceTechDir`/`ResolveWorkspaceDefaultTech` are wired once
per document by `WorkspaceViewModel.WireRetargetSeam` (all three `new LayoutEditorViewModel(...)` call
sites) so the picker never depends on `WorkspaceViewModel` directly.

**Add to technology reuses the exact live-tech seam paste already established** — `RetargetTechnologyCommand`
never writes a `.ctech` file; `LayoutEditorViewModel.ApplyAddToTechnologyChoices` clones the
just-applied destination `Technology` and fires the same `RequestAddLayerToTechnology` event
`WorkspaceViewModel.OnLayoutRequestAddLayerToTechnology` already subscribes, landing in
`TechnologyCache.SetLive` — undoable in the tech editor, dirty until saved there.

**Report what happened (§5):** both a retarget and a cross-tech paste post one Messages summary via
`LayoutEditorViewModel.ReportMessage` — e.g. *"Retargeted to MMIC GaAs · 1,204 shapes · Top
Copper→Metal1 (name), Drill→(unknown)"* — built by `LayoutLayerMapping.SummarizeMapping`, the single
formatter shared by both callers so the wording can't drift between them either.

Test files: `LayoutLayerMappingTests.cs` (the Drill/Soldermask match-kind coverage, all four
`LayerMatchKind`s, case-/whitespace-insensitive name matching, the name-beats-numeric-coincidence
priority test, shape-count sort order, `RequiresConfirmation`, `BuildChoices`), `LayoutRetargetTests.cs`
(the round-trip byte-identical-geometry/nets/holes gate, `(Workspace default)` re-resolution, opt-in
unit adoption with `DbuPerMicron` unchanged either way, one-undo-entry via `LayoutPersistence.Serialize`
equality, Add-to-technology's live-override seam, Keep-as-unknown, nothing-ever-dropped across every
choice combination, and the Messages summary), plus new/renamed cases in `LayoutClipboardViewModelTests.cs`
(`Paste_PcbDrillAndSoldermask_IntoMmic_RequiresConfirmation_NeverSilentlyRewritesOntoUnrelatedLayer` —
the Gap 2 regression test, which fails against the old `GetMissingLayers`-driven trigger — and
`Paste_SameTechnologyBothSides_RaisesNoConfirmation_AndRewritesNothing` for gate 3) and
`LayoutFragmentTests.cs` (the three obsolete `GetMissingLayers` tests removed; `ApplyReconciliation`'s
own three-branch coverage is unchanged by this phase). 24 new tests; 2066 Ui.Tests total, all green;
full solution green (Firewall 4/4, Core 388/388, Engine 461/462 — 1 pre-existing skip, matching the
L1f baseline exactly). **Not interactively verified** (no visual driver in this environment, matching
every prior Layout Editor phase) — the metadata-bar button, context-menu item, and both new dialogs
(`LayerMappingDialog`, `ChangeTechnologyDialog`) cannot be unit-tested headlessly for the same reason
every prior phase's dialog/menu code couldn't be; correctness rests on the VM-level gate tests above,
which drive the exact same public methods (`RetargetTo`, `ProposeFragmentLayerMapping`,
`ApplyFragmentReconciliation`) the dialogs' code-behind calls.

**Phase L1 (layout draw & edit — primitives, snap, selection, handles, booleans/offsets, clipboard,
and technology retargeting) is now complete.**

Phase L1f — cross-cell cut/copy/paste (brief-L1f-clipboard.md, 2026-07-26) — COMPLETE: **Phase L1 is
now complete.** A layout selection can be cut/copied/pasted/duplicated within one document, across
cells, and across a different circuitRF workspace/technology/process — with layers, nets, and holes
intact, correctly rescaled across DBU resolutions, warning rather than silently mangling, and pasting
into Office/iWork as a real vector graphic.

**`LayoutFragment.cs` (pure, framework-free) / `LayoutClipboard.cs` (Avalonia) — the split that makes
this phase testable.** `LayoutFragment` decides what a paste MEANS: building a fragment from a
selection, rescaling it across DBU resolutions, reconciling layers against a destination technology,
and translating shapes for placement — none of it touches `IClipboard`, Skia, or any Avalonia type.
`LayoutClipboard` is thin glue on top: JSON text + PDF/SVG/PNG rendering + `IClipboard` traffic,
mirroring `SchematicClipboard` end to end (same Windows-bypass-Avalonia branch, same
`ClipboardFormats`/`WindowsClipboard` reuse). This is why the hard parts — rescale math, layer
reconciliation, the marker guard — have 40+ direct unit tests, while the clipboard I/O itself has
**zero**, exactly like `SchematicClipboard` before it: `IClipboard`/`Avalonia.Media.Imaging.Bitmap`
both require a live Avalonia platform (`IPlatformRenderInterface`) a headless xunit run doesn't have —
confirmed by deliberately calling `new Bitmap(stream)` in a test and observing the exact
`InvalidOperationException` this environment throws for it, not by assumption. `LayoutClipboard`'s
three private renderers are `internal` (not `private`) specifically so tests can call them directly,
mirroring `LayoutRenderer.PathSpace`'s existing "internal so a test can drive it" precedent.

**R-L1f-1 — the payload is self-describing.** `LayoutFragment.Payload` carries its source
`DbuPerMicron` and the `LayerDef`s the selection actually uses (never the whole technology) — nothing
else in the fragment depends on ambient state, which is the entire reason a fragment survives a paste
into a different technology, a different resolution, and a different running circuitRF instance for
free (the payload rides the system clipboard as plain marker-guarded JSON text — the marker is
`"circuitrf/layout-clipboard-v1"`, checked before anything is parsed; `Instances` are deliberately NOT
carried — nothing can create one until L3 hierarchy).

**Paste warns-and-proceeds on a lossy rescale; `LayoutScaling.TryChangeResolution` refuses — and
that's correct, not an inconsistency to fix.** `LayoutFragment.Rescale` scales every fragment
coordinate (including holes and Cubic control points, the same easy-to-miss list `LayoutScaling`
already documented) by the exact ratio between source and destination `DbuPerMicron`; where the ratio
is non-integer or a specific coordinate doesn't divide evenly, it rounds, names the affected shape in
a returned warning list, and returns the shapes anyway. `TryChangeResolution` mutates an EXISTING
design in place, so a silent snap would be a real, hard-to-notice loss — refusing is right there.
`Rescale` only ever ADDS brand-new geometry the user can undo in one keystroke — proceeding-with-a-
warning is right here. Same underlying math (mirrors `LayoutScaling`'s per-shape coordinate walk),
opposite failure policy, on purpose.

**Layer reconciliation (R-L1f-3), three branches, Keep-as-unknown the default:** a layer the
destination technology already defines needs no choice at all — the shape keeps its `LayerKey` and
the renderer resolves it against the DESTINATION's own `LayerDef` (never re-added, never overridden by
the fragment's copy). An absent layer gets exactly one prompt per distinct key (`LayerReconciliationDialog`,
never once per shape), with an "Apply to all remaining" checkbox the caller (`LayoutEditorView`'s
paste orchestration) honors for every later absent key in the same paste: **Keep as unknown**
(default, non-destructive, renders via `FallbackPalette`), **Map to an existing layer** (rewrites the
shape's `LayerKey`), or **Add to the technology** (leaves the key alone; the fragment's own `LayerDef`
is installed into a live, unsaved clone of the technology via a new `LayoutEditorViewModel.
RequestAddLayerToTechnology` event — `WorkspaceViewModel.OnLayoutRequestAddLayerToTechnology` subscribes
at every `new LayoutEditorViewModel(...)` call site and forwards straight to `TechnologyCache.SetLive`,
exactly mirroring the L1-fix `TechEditorViewModel.TechLiveChanged`/`OnTechLiveChanged` seam — nothing is
ever written to the `.ctech` file directly; the user still decides whether to persist it, and it is
undoable in the tech editor). Cancelling any reconciliation prompt abandons the WHOLE paste rather than
partially reconciling it. Nothing is ever dropped in any branch — `LayoutFragment.ApplyReconciliation`
always returns exactly as many shapes as it was given.

**Duplicate (Ctrl/Cmd+D) deliberately bypasses the system clipboard entirely.** It clones the
selection and places it offset by one snap step (`LayoutEditorViewModel.OneSnapStepDbu`, the same
"snap step, or 1 DBU with snapping off" helper nudge already uses) as one undo entry — never touching
`IClipboard`, because clobbering the user's clipboard as a side effect of Duplicate is a small betrayal
people notice. Cut is Copy (writes to the clipboard) then `DeleteSelection()` (the existing L1c
command) as one undo entry — `CutSelectionAfterCopy` is nothing more than that one call. Paste and
Paste in Place both land via the same empty-removed-set `ReplaceShapesCommand` call
(`InsertPastedShapes`) L1e's boolean/offset/flatten operations already established for N→M
replacements — appended topmost within their layers, one undo entry, and the newly placed/duplicated
shapes become the selection.

**Paste's live ghost is a new `LayoutOverlay.PastePreview` (a shape LIST, not the single-shape
`InProgressPrimitive` the L1b draw ghost uses)**, rendered by a renamed-but-otherwise-identical
`LayoutRenderer.DrawGhostShape` (was `DrawGhost`) called once per preview shape — no second ghost-
drawing code path. `LayoutEditorViewModel`'s paste-placement state
(`_pastePlacementShapes`/`_pastePlacementAnchorX/Y`/`_pasteCursorX/Y`) is checked FIRST in
`OnPointerPressed`/`OnPointerMoved`/`OnKeyDown` — ahead of the Select-tool dispatch and ahead of every
drawing tool — exactly like `_isTypingLabel` already is, so an armed paste ghost owns every gesture
regardless of whatever tool happens to be active underneath it. A click commits at the current snapped
cursor (`LayoutSnapping.SnapPoint`, the same primitive drag/draw already use); Escape cancels and
pushes no command; the ghost renders at the fragment's own anchor (zero delta) until the first pointer
move arrives. Paste in Place skips all of this — it has no ghost, lands at the fragment's original
(rescaled/reconciled) coordinates immediately.

**`WindowsClipboard` and `ClipboardFormats` were reused completely unchanged** (§6.1 of the brief) —
`LayoutClipboard.CopyAsync` calls `WindowsClipboard.SetClipboard(hwnd, pdf, svg, json, bitmap, pageW,
pageH)` with its own rendered bytes and strings, exactly like `SchematicClipboard` does; nothing in
`WindowsClipboard.cs` needed to change, and `ClipboardFormats.PdfNativeMacFormat`/`SvgNativeFormat`
(already `internal` in the same assembly/namespace) are referenced directly with zero duplication of
the UTI strings.

**Export-mode render flags (R-L1f-5/R-L1f-6).** `LayoutRenderOptions` gained one new field,
`TransparentBackground` — when true, `LayoutRenderer.Draw` skips its background-fill `DrawRect`
entirely (the destination surface must already arrive transparent: `LayoutClipboard`'s bitmap path
calls `SKBitmap.Erase(SKColors.Transparent)` before rendering; a fresh PDF page or SVG canvas starts
blank on its own). Combined with the export call sites always passing `ShowGrid = false` and
`Overlay = null`, this suppresses background/grid/selection-outlines/handles/marquee/ghost with no
other renderer change — every one of those was already conditional on `opts.ShowGrid`/`opts.Overlay?.X`
before this phase. **Hairline strokes already carried a real, non-zero world-space width before this
phase** (`LayoutRenderer.DrawLayer`'s `GeometryStrokeDevicePixels`-derived `StrokeWidth`, an earlier
owner-feedback fix, not a literal Skia `StrokeWidth = 0` hairline) — R-L1f-6 needed no renderer change
at all, only an export call site that passes a non-degenerate `LayoutViewport`, which
`LayoutClipboard`'s bbox-derived viewport always is. **Export renders the SELECTION, not the current
view (R-L1f-4)** — `LayoutClipboard`'s render helpers take only `shapes`/`tech`/`theme`/`transparent`,
never a viewport parameter; the export `LayoutViewport` is always freshly computed from
`LayoutGeometry.BboxOf` over the exported shapes, so the same selection renders identically regardless
of whatever the user's on-screen pan/zoom happen to be — provable by construction (no on-screen
viewport can reach these methods) and pinned by a test that varies shape order into the same bbox and
asserts identical output.

**R-L1f-7 (EMF alpha) — not independently re-verified in this environment, and said so rather than
assumed.** The EMF pipeline (SVG→GDI+ metafile via `Svg.NET`, inside `WindowsClipboard.SetClipboard`)
is byte-for-byte the same untouched code `SchematicClipboard` already uses — this phase changes
nothing about it, only supplies layout's own rendered SVG bytes into the same call. Whatever alpha
fidelity that pipeline has (or lacks) applies identically here; verifying it means pasting overlapping
same-layer shapes into PowerPoint on Windows and zooming in, which no interactive Windows environment
is available to do from this session. Flagging rather than silently assuming correctness, per the
brief's explicit instruction.

**Cross-editor safety** — a Layout paste of a symbol-clipboard-shaped JSON blob (or arbitrary text, or
truncated JSON) is a clean no-op with no exception: `LayoutFragment.TryDeserialize` checks
`Payload.Marker == "circuitrf/layout-clipboard-v1"` before trusting anything else in the blob, and
`System.Text.Json` silently ignores JSON properties `Payload` doesn't declare rather than throwing, so
a schematic/symbol-clipboard payload (an entirely different JSON shape) simply deserializes into an
all-default `Payload` whose `Marker` is null and fails the check. The reverse direction (a layout
payload pasted into the symbol editor) needed no new code — `SymbolClipboard`/`SchematicClipboard` are
untouched per the brief's guardrail, and their own paste paths already reject a JSON shape they don't
recognize the same way.

Test files: `LayoutFragmentTests.cs` (gates 2/4/5/6/7 — round-trip fidelity across every shape kind
incl. a polygon-with-hole/arc-bearing Curve/curved Path/net-carrying shapes, the marker guard against
arbitrary text/truncated JSON/a symbol-clipboard-shaped payload, exact-integer-ratio rescale, the
reverse-direction non-dividing-coordinate warn-and-proceed case, Cubic-control-point and hole
rescaling, all three reconciliation branches, and the shape-count-never-changes invariant across every
reconciliation choice), `LayoutClipboardViewModelTests.cs` (gates 3/6/8/9/10/11 — Cut as one undo
entry, Duplicate's one-snap-step offset and undo, Paste in Place at original coordinates, paste
appended topmost, the screen-coordinate-driven ghost-anchor-lands-on-snapped-cursor gate on both
starter technologies via `LayoutViewport.ScreenToWorldX/Y`, Escape-during-ghost pushing no command,
cross-technology paste defaulting to Keep-as-unknown with nothing dropped, cross-DBU-resolution paste
rescaling before reconciliation, and the live-technology-override event firing an independent clone),
`LayoutClipboardExportTests.cs` (gates 13/14 — page dimensions as a pure function of the selection
bbox regardless of shape order, and a pixel-oracle proving export mode paints no background/grid/
overlay while the shape outline still covers a real, non-zero-width area). 43 new tests; 2042 Ui.Tests
total, all green; full solution green (Firewall 4/4, Core 388/388, Engine 461/462 — 1 pre-existing
skip, matching the L1e baseline exactly). **Not interactively verified** (no visual driver in this
environment, matching every prior Layout Editor phase) — gate 12 (graphic paste into PowerPoint/Word/
Keynote/Pages, verified by hand) and gate 15 (Windows SVG-failure robustness, exercising
`WindowsClipboard.SetClipboard` directly) are both explicitly hands-on/Windows-only per the brief and
were not run; correctness rests on the gate tests above plus direct code reading of the untouched
`WindowsClipboard`/`ClipboardFormats` reuse.

**Phase L1 (layout draw & edit — primitives, snap, selection, handles, booleans/offsets, and the
clipboard) is now complete. Next: L2 — performance**: the R-tree spatial index, per-shape path
caching, the §2.3 R8b merge tier with its threshold set from data, LOD culling, and the CI benchmark
harness at 1k / 50k / 500k shapes measuring both the darkening and merged paths.

Phase L1e — Clipper2 geometry operations and Flatten to Polygon (brief-L1e-clipper-operations.md,
2026-07-26) — COMPLETE: a layout shape can now be **booleaned** (Union/Intersect/Difference/XOR),
**merged** per layer, **offset** (grow/shrink), **self-intersection repaired**, and **flattened to a
polygon** — all via Clipper2. `Polygon`/`Curve` gained explicit holes. Cross-cell clipboard is
explicitly **not** in this phase (L1f).

**The hole decision (§0), and why it survives:** `PolygonShape`/`CurveShape` gained
`List<long[]>? Holes` — inner rings, same flat-vertex-list convention as `Xy`, null/absent meaning "no
holes" (purely additive, **no `.clay` `FormatVersion` bump**). This was forced, not chosen for
elegance: subtracting a via pad from a ground pour — *the* most common PCB layout operation — produces
a polygon with a hole, and keyholing it in the database (cutting a zero-width slit to the outer ring)
would be **lossy and irreversible** — the hole stops being a distinct entity, later booleans behave
differently, and L6's mesher would be asked to mesh a degenerate zero-width channel. Three of the four
consumers already handled multi-contour geometry natively (`SKPath` under one fill rule, point-in-
polygon hit-testing is the same ray cast run once more, Clipper2's own `PolyTree64` output *is* this
structure) — only the flattener's return shape changed, and its signature (`IReadOnlyList<long[]>`)
was already built to allow it. **Only GDSII genuinely cannot express a hole** — that is a format
limitation resolved at the export boundary (§8), exactly like curve flattening; the database keeps its
holes regardless of what any one writer can carry. **Forward requirement for L4, since there is no
GDSII writer file yet to carry the comment directly: the GDSII writer must keyhole on export** (cut a
zero-width slit from each inner ring to the outer, emitting one self-touching contour — standard
practice, what every GDSII writer does) and state this in the export dialog's per-format fidelity note
next to curve flattening; re-importing that GDSII yields a keyholed polygon, not the original, which is
inherent to the format. DXF and Gerber both express holes natively and need no such treatment.

**R-L1e-0 (§3.1a R10b): a hole must lie inside its outer ring and intersect neither that ring nor
another hole.** Clipper2's own `PolyTree64` output (every boolean/offset/repair below) satisfies this
by construction — normal editing never produces an invalid hole. `LayoutClipper.EnsureValidHoles`
enforces it on the one OTHER construction path that exists today, a hand-edited `.clay`
(`LayoutPersistence.FromFileModel` calls it for every loaded shape; a future paste (L1f) or import (L4)
should call it too). **Deliberately cheap-no-op-first, not "always re-derive":** it runs a pure
containment/intersection check first (point-in-polygon + segment-intersection, no Clipper2 call) and
only re-derives the shape via a Clipper2 `Union` when that check actually fails — re-deriving
unconditionally would have been simpler to write, but a `Union` pass can reorder vertices/holes even on
an already-valid shape, which would have silently broken exact round-trip equality (gate 2) for the
overwhelming common case (every shape a boolean op below actually produces).

**DBU integers feed Clipper2 with zero scaling — the payoff for the integer-database decision.**
`LayoutClipper.ToClipperPaths`/`FromClipperTree` (`src/Ui/Layout/LayoutClipper.cs`) is the **one**
conversion point §6.1 of the design doc requires — booleans, offsets, DRC (L5b), the mesher (L6), and
export (L4) all funnel through it, so the flattening tolerance is never chosen twice with two different
answers. Clipper2's `Path64`/`Point64` are `long`-based, exactly §1.1's storage type: no "scale to a
working integer grid" step (which every other clipping library needs and we don't), no float
conversion, no precision loss anywhere in the pipeline. **`FillRule.NonZero` everywhere, stated once**
(`LayoutClipper.Rule`) — it is what makes self-intersection repair produce the outer region rather than
a checkerboard. `PathShape` gets its geometry outline here too, via `InflatePaths` on the flattened
centerline at `Width/2`, with `Flush→Butt`/`Round→Round`/`Square`&`Extended→Square` (both extend by the
same half-width amount — see the L1a `PathEndStyle` note in this file for why they currently render
identically).

**R-L1e-1 — this is NOT the display outline, and the two stay separate, on purpose.**
`LayoutRenderer.BuildPathOutline` (the L1-fix-era Skia-stroker-plus-`Simplify` path) is **completely
untouched** by this phase — Skia tessellates a curved trace adaptively at the current zoom, while
Clipper2 works on flattened (polygonal) geometry, so a curved trace's Clipper2 outline is correctly
polygonal for booleans/DRC/export and would be visibly faceted if it were ever used for display. Two
outlines, two purposes, never unify them; `LayoutClipper.ToClipperPaths`'s doc comment says so directly
now, alongside the pre-existing warning on `BuildPathOutline` itself. Gate 10's seam test
(`LayoutPathOutlineSeamTests.cs`) still passes untouched, proving nothing here disturbed it.

**Boolean layer/net attribute rules (§3), implemented once in `LayoutBooleans.Combine` and reused by
every op:** result **layer** = the first operand's layer (for Difference, "first-selected" = the shape
being subtracted from — selection ORDER matters, and `LayoutEditorViewModel.ApplyBoolean` deliberately
passes operands in `_selectedIndices`' own click order, never re-sorted by index, or Difference would
silently subtract the wrong direction). Result **net** = the shared net if every operand agrees,
else **cleared and reported** via Messages — never picked arbitrarily. Curved operands are flattened by
`ToClipperPaths`; the "warn once per session" rule (§3.2 R9e) is session (open-document) state on the
VM (`_warnedCurvedOperandThisSession`), not a pure-function concern — `LayoutBooleans` itself is
stateless and just reports `AnyCurvedOperand`/`NetsDiffered` back to the VM in its result record.

**One `LayoutBooleans` fold serves Union/Intersect/Difference/XOR for any operand count.**
`Combine(ClipType, operands, tech)` pairwise-folds `A op B op C …` in operand order — this generalizes
correctly to N operands for every op the brief lists: Union/Intersection/XOR are associative, and
Difference folded left-to-right is exactly "first minus the rest." No separate N-ary special-casing was
needed. `Merge` groups operands by layer and calls `Combine(Union, …)` once per group. `Offset` and
`Repair` are their own direct `InflatePaths`/`Union` calls (not folds — they operate on one shape).

**`ReplaceShapesCommand` (`src/Ui/Commands/Layout/`) generalizes L1d's `ReplaceShapeCommand` from 1→1
to N→M**, for every op in this phase (a boolean/merge/offset/repair/flatten can turn K selected shapes
into 0..N results). Same rule as L1b's single-shape restore, extended: insert added shapes at the
**lowest removed index**; Undo removes them from there and reinserts every original at its own original
index, ascending. `LayoutEditorViewModel.CommitReplace` is the one place every L1e operation builds this
command and re-selects the newly-added shapes — one undo entry per operation, always (gate 13).

**All the actual operation logic lives in three new framework-free files under `src/Ui/Layout/`**
(`LayoutClipper.cs`, `LayoutBooleans.cs`, `LayoutFlattenToPolygon.cs`) — `LayoutEditorViewModel.
Booleans.cs` (a second partial-class file, kept separate from the already-1600-line main VM file) is
pure selection/undo/Messages plumbing on top, mirroring how the rest of this VM is organized.
`LayoutFlattenToPolygon` deliberately does **not** go through Clipper2 at all — flattening a curve into
its polygon export is a direct `LayoutFlattener` consumer with nothing to union or re-derive winding
for. A `PathShape` with curved centerline edges flattens those edges to `Line` and **stays a
`PathShape`** (width/end style intact) — turning a trace into a filled polygon outline is a different,
lossy operation users flattening a trace do not expect.

**Context menu wiring** (`LayoutCanvas.ShowShapeContextMenu`, renamed from `ShowEdgeConversionMenu`):
Union/Intersect/Difference/XOR (≥2 selected), **Offset…** (≥1 selected), Merge (≥1 selected), Repair
Self-Intersection (single selection, only when `LayoutSelfIntersection.Test` is currently true),
Flatten to Polygon / Flatten to Polygon… (only when the selection has curved geometry) — all
selection-scoped, so they show regardless of exactly what (if anything) is directly under the
right-click point, unlike the edge-conversion/delete-vertex items above them which stay
click-target-scoped. `FlattenToPolygonDialog` (`src/Ui/Views/Dialogs/`) is the "…" tolerance prompt
with a live vertex-count preview, computed against the first curved shape in the selection and then
applied uniformly to the whole selection. `OffsetDialog` (same folder, same shape) prompts for the
dimension (unit-suffixed, negative allowed — validation only rejects unparseable text, never a sign)
and pre-fills with the VM's last-used `OffsetText` so repeated offsets in one session don't require
re-typing the same distance; on Apply, `LayoutCanvas.ShowOffsetDialogAsync` calls
`CommitOffsetText`/`ApplyOffsetToSelection` — the same staged-field pattern every other typed dimension
field on this VM already uses (`CornerRadiusText`, `PathWidthText`, …), reused rather than adding a
second parse path.

**Empty results are legal and structural, not a special case anywhere** — `LayoutBooleans.Combine`/
`Offset` just produce zero `LayoutShape`s (e.g. disjoint Intersect, or an over-shrunk Offset), and
`ReplaceShapesCommand` removes the operand(s) and inserts nothing; the VM reports it via Messages but
never throws and never leaves the originals in place.

Test files: `LayoutClipperTests.cs` (ToClipperPaths for every shape kind incl. holes and the Path
outline via `InflatePaths`, `FromClipperTree`'s hole/island tree walk, `EnsureValidHoles`'s no-op-when-
valid vs. repair-when-invalid paths), `LayoutBooleansTests.cs` (gate 4's canonical rect-minus-circle,
gate 5's overlapping/disjoint/fully-contained/empty-intersection cases for every op, gate 6's split-in-
two, gate 7's net propagation + layer attribution + selection-order-for-Difference, Merge's per-layer
grouping, gate 8's offset grow/shrink/annihilate, gate 9's determinism both repeated and through a
serialize/reload round-trip, gate 12's bowtie repair), `LayoutHolesTests.cs` (gate 2's round-trip
byte-equality using a REAL Clipper2-produced hole shape — hand-authored geometry would risk the
`EnsureValidHoles` re-derivation path and defeat the point of the test — gate 3's bbox/hit-test/
scaling/translate/clone coverage, and the pixel-oracle donut render), `LayoutBooleanOperationsViewModelTests.cs`
(gate 13's one-undo-entry-restores-both-operands-at-original-indices, gate 6 at the VM/undo level, gate
7's Messages warning + "warn once per session" across two ops, gate 8 via the staged `OffsetText`
field, gate 11's circle→polygon/preview-count-matches/multi-selection-skips-non-curved/curved-Path-
stays-a-Path/`FlattenAllCurves`-layer-filter, gate 12 via the VM). 54 new tests; 1999 Ui.Tests total,
all green; full solution green (Firewall 4/4, Core 388/388, Engine 461/462 — 1 pre-existing skip,
matching the L1d baseline exactly). **Not interactively verified** (no visual driver in this
environment, matching every prior Layout Editor phase) — the context-menu construction itself
(`ShowShapeContextMenu`'s `MenuItem`/`ContextMenu` wiring, `FlattenToPolygonDialog`'s AXAML) cannot be
unit-tested headlessly for the same reason every prior phase's menu/dialog code couldn't be; correctness
rests on the VM-level gate tests above, which drive the exact same public methods
(`ApplyUnion`/`ApplyDifference`/`RepairSelfIntersection`/`FlattenSelectionToPolygon`/…) the menu items
call. **Next: L1f** (cross-cell cut/copy/paste — `.clay` fragment format, DBU rescale on paste, layer
reconciliation against the destination technology — the last brief of Phase L1).

L1d post-ship fix round 3 — Rect/RoundedRect edge-drag, and a "Delete Vertex" context menu item
(2026-07-26) — COMPLETE: two owner follow-ups after round 2. (1) "Drag edge midpoint / edge line"
was, by the brief's own §2 handle table, deliberately Polygon/Curve/Path-only — but the owner wanted
it on `Rect`/`RoundedRect` too, a reasonable ask (widening one side of a box is a completely ordinary
rectangle-editing gesture). (2) The owner still couldn't discover vertex-picking at all ("I don't
even know how to select an individual vertex") — clicking a vertex handle with no drag silently picks
it for the NEXT Delete keypress, with zero visual feedback that anything happened; asked for an
explicit "Delete Vertex" context-menu item instead.

**(1) Rect/RoundedRect edge-drag.** `Rect`/`RoundedRect` have no vertex list (`X1/Y1/X2/Y2` fields
only), so this could not reuse `TranslateEdgeEndpoints`/`FindEdgeLineHit`'s vertex-list machinery
as-is — it needed a parallel, axis-aligned-specific path, added everywhere that machinery is
consulted: **Handles** — `LayoutHandles.BuildAxisAlignedEdgeMidpoints` adds 4 `EdgeMidpoint` handles
(same edge-index convention as the corners: 0=bottom, 1=right, 2=top, 3=left) to both `Rect` and
`RoundedRect`; a `RoundedRect`'s corner rounding only shortens the ENDS of each straight run
symmetrically, so the midpoint position is unaffected by it and needs no special-casing. **Hit-test**
— `LayoutShapeEditing.FindEdgeLineHit` now dispatches `Rect`/`RoundedRect` to a new
`FindAxisAlignedEdgeLineHit` (tests the 4 corner-to-corner segments directly — no `Xy` array to walk)
instead of its early `!IsVertexListShape → null` return, so a plain click ANYWHERE along a Rect's edge
line (not just the exact midpoint handle) begins the drag, matching the Polygon/Curve/Path behavior
exactly. **Geometry builder** — `LayoutShapeEditing.TranslateRectEdge`/`TranslateRoundedRectEdge`:
since a rectangle's 4 edges are each defined by a SINGLE field, "translate this edge perpendicular to
itself" is just `Y1 += delta` / `X2 += delta` / `Y2 += delta` / `X1 += delta` per edge index — no
vector projection needed, the axis is fixed by which edge it is (unlike the general vertex-list case,
where the perpendicular direction must be derived from the edge's own vector). **VM wiring** — a new
`HandleDragKind.RectEdge` (parallel to the existing `RectCorner`, which similarly re-maps the generic
`Vertex`/`EdgeMidpoint` handle kinds for these two shape types in `BeginHandleDrag`); a new
`ComputeRectEdgePerpendicularOffset` projects the drag delta onto whichever single axis that edge
index owns (Y for edges 0/2, X for edges 1/3) and snaps it, mirroring `ComputeEdgePerpendicularOffset`'s
"snap the perpendicular offset, not each endpoint" rule; `FinalizeHandleDragShape` normalizes
`RectEdge` results at commit exactly like `RectCorner` (an edge dragged past its opposite edge is a
well-defined "inside-out" rect mid-drag, corrected only once, at release). **Ctrl+click-insert
deliberately still excludes Rect/RoundedRect** — extending `FindEdgeLineHit` to those shapes meant the
existing Ctrl-branch in `TryHandleSelectPressOnHandles` would otherwise reach `InsertVertexOnEdge`,
which calls `XyOf` and would throw for a shape with no vertex list; the Ctrl-insert branch now guards
on `LayoutShapeEditing.IsVertexListShape(shape)` first (there is genuinely nothing to insert into — a
`Rect` cannot gain a 5th point without becoming a different shape kind entirely, which is out of scope
here and not requested).

**(2) "Delete Vertex" context menu item.** A new `LayoutEditorViewModel.FindVertexForContextMenu(wx,
wy, tolDbu)` mirrors `FindEdgeForContextMenu` exactly (single-selection only, reuses the SAME
`LayoutHandles.Build` + `LayoutHandleHitTest.HitTest` a left-click-drag would use, so "the vertex you
can right-click to delete" is always the same one dragging would grab) but filters to
`LayoutHandleKind.Vertex` hits and requires `IsVertexListShape` — a `Rect`/`RoundedRect` corner is a
resize handle, not a removable vertex, exactly the same distinction round 2's picked-vertex-bookkeeping
fix made. `LayoutEditorViewModel.DeleteVertex` changed from `private` to `public` (no behavior change)
so the canvas can call it directly. `LayoutCanvas.ShowEdgeConversionMenu` (right-click handler) now
independently probes for BOTH an edge hit (existing Convert-to-… items) and a vertex hit (new "Delete
Vertex" item), adding a `Separator` between them only when both are present — right-clicking a
Polygon/Curve/Path vertex (which is always also "near" one of its two adjacent edges, so both hits
normally fire together) shows Convert-to-… / separator / Delete Vertex in one menu, exactly the layout
requested. The already-correct "blocked below 3/2 vertices" no-op (§3) is unchanged — the menu item is
always offered when a vertex is found; clicking it while blocked harmlessly does nothing, identical to
the existing keyboard-Delete behavior (no new blocking UI was requested).

Test files: `LayoutHandlesTests.cs` (Rect/RoundedRect handle-count tests updated for the 4 new
edge-midpoint handles each), `LayoutShapeEditingTests.cs` (`TranslateRectEdge`/`TranslateRoundedRectEdge`
per-edge-index field mapping, `FindEdgeLineHit` on both Rect and RoundedRect for all 4 sides plus the
dead-center-miss case), `LayoutHandleGesturesTests.cs` (full-VM-gesture Rect/RoundedRect edge-midpoint
drag, a plain click away from the midpoint still beginning the drag, inside-out-past-the-opposite-edge
normalizing at commit, `FindVertexForContextMenu` finding a Polygon vertex vs. correctly refusing a
Rect corner, and `DeleteVertex` via the context-menu lookup path both succeeding and correctly staying
blocked at the 3-vertex minimum). 19 new tests; 1945 Ui.Tests total, all green; full solution green
(Firewall 4/4, Core 388/388, Engine 461/462 — 1 pre-existing skip). **Not interactively verified** (no
visual driver in this environment) — the context-menu wiring in particular (`ContextMenu`/`MenuItem`/
`Separator` construction in `LayoutCanvas.cs`) cannot be unit-tested headlessly (any `Control` requires
the Avalonia runtime); correctness rests on the VM-level `FindVertexForContextMenu`/`DeleteVertex` gate
tests above plus direct code reading of the menu-building logic.

L1d post-ship fix round 2 — macOS Ctrl-click-as-right-click, and picked-vertex bookkeeping on
Rect/RoundedRect/Circle (2026-07-26) — COMPLETE: owner report that Ctrl/Cmd+click-insert *still*
didn't work after the round-1 fix below, plus "Delete on a selected vertex" appearing broken and
undiscoverable. Two independent bugs, both in gesture wiring the round-1 fix didn't touch.

**(1) macOS reports Control+left-click as a SECONDARY (right) button press at the OS/Avalonia level**
— the classic one-button-mouse "Control-click = right-click" convention, inherited from AppKit.
`LayoutCanvas.OnPointerPressed`'s `props.IsRightButtonPressed` branch ran unconditionally BEFORE the
left-click branch that carries the round-1 Ctrl-insert fix — so on macOS, holding Control and clicking
never reached that logic at all; it always opened the edge-conversion context menu instead, no matter
how the VM-level priority ordering was fixed. This explains why the round-1 fix (verified correct at
the VM level, with a passing regression test) produced no visible change for the owner — the bug was
one layer up, in the canvas's button-type dispatch, never exercised by a VM-level test (this class of
bug is exactly why `LayoutCanvas.cs` — any `Control` subclass — cannot be unit-tested headlessly; see
"Testing without the Avalonia runtime" below). **Fix:** the right-button branch now checks
`KeyModifiers.Control` first; if held, it forwards to the ordinary press path (which already handles
Ctrl/Cmd+click-insert correctly) instead of opening the context menu. A genuine right-click with no
Control held is unaffected. **Not unit-tested** (requires the Avalonia runtime's button/modifier
translation, which this environment cannot construct) — reasoned from the documented AppKit convention
and the exact code shape; Cmd (Meta)+click does NOT trigger this OS-level button substitution — only
literal Control does — so Cmd+click was already working correctly via the existing left-click path and
needed no change.

**(2) `_pickedVertexIndex` (the "Delete on a selected vertex" bookkeeping, §3) was set for ANY
Vertex-kind handle click, including a `Rect`/`RoundedRect` corner** — which reports as a Vertex-kind
`LayoutHandle` but maps to `HandleDragKind.RectCorner` (a resize), not a removable vertex.
`LayoutShapeEditing.RemoveVertex` correctly refuses a non-vertex-list shape (returns null), but
`OnKeyDown`'s Delete branch called `DeleteVertex(...)` and returned UNCONDITIONALLY, whether or not it
actually did anything — so clicking a Rect's corner (a completely ordinary thing to do while selecting
it) silently broke the Delete key for that shape: neither a vertex was removed (correctly — Rect has
none) NOR did the whole shape get deleted (the bug — Delete just did nothing). **Fix:**
`_pickedVertexIndex` is now only set when the handle is Vertex-kind AND
`LayoutShapeEditing.IsVertexListShape(shape)` — false for Rect/RoundedRect/Circle, so Delete on those
shapes now falls through to `DeleteSelection()` exactly as it did before any handle was ever clicked.
The already-correct "blocked below 3/2 vertices" behavior for a true vertex-list shape (Polygon/Curve/
Path) is unchanged — that case must stay a no-op, not escalate to deleting the whole shape.

**Clarifying, not a bug: "Drag edge midpoint / edge line" and "Ctrl/Cmd+click an edge" per §2's handle
table only exist on `Polygon`/`Curve`/`Path` — a `Rect`/`RoundedRect`/`Circle` has ONLY corner/radius
handles (no edge-midpoint, no edge-line insert), because those shapes have no vertex list to insert
into.** A new regression test drives a plain (non-Ctrl) click a quarter of the way along a long
Polygon edge — deliberately clear of the midpoint handle's own tolerance window — and confirms
`LayoutShapeEditing.FindEdgeLineHit`'s fallback still begins the perpendicular edge-drag correctly;
the underlying VM/geometry logic for both edge gestures was already correct on the shapes that support
them. If edge-drag/insert still seem unavailable, first confirm the shape under test is a Polygon
(or Curve/Path), not a Rect — this is the most likely remaining explanation. Regression tests:
`LayoutHandleGesturesTests.ClickRectCornerThenDelete_FallsThroughToDeletingTheWholeShape` and
`..._PlainClickOnEdgeLine_AwayFromTheMidpointHandle_StillBeginsTheEdgeDrag`. 1926 Ui.Tests total, all
green; full solution green (Firewall 4/4, Core 388/388, Engine 461/462 — 1 pre-existing skip).

L1d post-ship fix — Ctrl/Cmd+click-insert was shadowed by the EdgeMidpoint handle (2026-07-26) —
COMPLETE: owner report ("Ctrl/Cmd+click an edge doesn't seem to work"). Root cause:
`TryHandleSelectPressOnHandles` tested `LayoutHandleHitTest.HitTest` (the handle-priority test)
BEFORE checking `ctrl` at all — and every straight edge already carries an `EdgeMidpoint` drag-handle
sitting exactly at that edge's midpoint, the single most natural spot to click "the edge." A
Ctrl+click landing on (or within tolerance of) that handle hit the handle branch first, called
`BeginHandleDrag` unconditionally, and returned — the `ctrl` check for insert-a-vertex lived further
down and was never reached. Programmatic gesture tests at the time all clicked deliberately clear of
any handle (to isolate the insert path), so this shadowing went uncaught until interactive use.
**Fix:** `TryHandleSelectPressOnHandles` now checks `ctrl` FIRST — if held, `FindEdgeLineHit` runs
immediately and inserts on a hit, before the handle hit-test ever runs; only when Ctrl is held but no
edge is under the click does it fall through to the normal handle test. Ctrl+click now means "insert
a vertex" unconditionally, regardless of what handle happens to occupy the same pixel. Regression test:
`LayoutHandleGesturesTests.CtrlClickExactlyOnTheEdgeMidpointHandle_StillInsertsAVertex_NotAnEdgeDrag`
clicks at the exact edge-midpoint coordinate with Ctrl held and asserts an insert (not a drag) occurs.
1924 Ui.Tests total, all green.

Phase L1d — vertex/edge/bulge/control-point editing handles (brief-L1d-shape-editing-handles.md,
2026-07-26) — COMPLETE: a layout shape can now be **reshaped**, not just moved/deleted — drag a
vertex, drag an edge, drag a bulge or a cubic control point, insert/remove a vertex, resize a
Circle's radius or a RoundedRect's corner radius or a Rect's corner, and convert an edge
Line↔Arc↔Cubic via a right-click menu. Clipper2 booleans/offsets, Flatten-to-Polygon, and
self-intersection *repair* are explicitly **not** in this phase (L1e).

**`ReplaceShapeCommand` (`src/Ui/Commands/Layout/`) is the SINGLE command for every geometry-reshape
edit** — vertex move, edge move, insert, remove, bulge/control-point change, radius/corner-radius
resize, Rect corner resize, AND edge-kind conversion. Not a family of per-operation commands, because
the promotion rule (below) **changes the shape's runtime type** (`PolygonShape` → `CurveShape`) — a
command that mutates a shape in place cannot express a type change, while one that swaps the whole
instance at a fixed index expresses every edit (type-preserving or not) uniformly. Undo is trivially
the reverse swap at the same index. All the actual geometry math lives in `LayoutShapeEditing.cs`
(`src/Ui/Layout/`), whose builders are **immutable-style by rule**: given a shape, build and return a
brand-new one (`LayoutGeometry.Clone` + a targeted field change) — never mutate the shape the renderer
or the model may currently be reading. `LayoutEditorViewModel` is the only caller that ever swaps a
built shape into `LayoutView.Shapes`, and only via `ReplaceShapeCommand`.

**Three different snapping rules, deliberately different and living together in
`LayoutEditorViewModel.BuildHandleDragPreview` so they read as one considered system rather than an
inconsistency:**
1. **Whole-shape move** (L1c, unchanged) snaps the **delta** — one rigid translation applied to every
   vertex, so an off-grid shape's internal geometry survives a move exactly.
2. **Vertex drag** snaps the **resulting position** — the user is placing a single point and no other
   vertex is affected, so snapping this one point can't mangle anything else.
3. **Edge drag** snaps the **perpendicular offset** (a scalar projected onto the edge's unit normal),
   then applies the identical snapped delta to both endpoints — like a move, this must snap the delta
   and not each endpoint independently, or a 45° edge could silently become some other angle.
Bulge, cubic-control, radius, and corner-radius drags snap their own scalar result (bulge is
deliberately **not** grid-snapped — it's geometric/unbounded, not a coordinate; radius and
corner-radius ARE snapped, being lengths on the same grid as everything else).

**Hit priority (R-L1d-2), `LayoutHandleHitTest.cs`: CubicControl > Bulge > Vertex/Radius/CornerRadius >
EdgeMidpoint**, nearest-within-tier as the tiebreak. This matters because a Cubic edge's control-point
handle can sit very close to a vertex handle, and a straight edge's midpoint handle always exists
alongside any vertex/bulge handle on the same edge — without an explicit order the "wrong" handle
would win arbitrarily depending on pixel rounding.

**Handle grab radii are PIXEL quantities, computed fresh on every hit-test query from the CURRENT
zoom — never cached, never derived from `SnapDbu`.** This is the exact class of bug the L1-fix-clear-
and-default-zoom entry (below) already burned the project on once for the drawing tools; L1d repeats
the same discipline for handles. `LayoutHandleHitTest.HitTest` takes a `tolDbu` the caller computes
per-call (`pixels / zoom`), same as L1c's selection hit-testing.

**The promotion rule (R-L1d-3):** `PolygonShape` carries no edge list (every edge is implicitly Line);
converting one of its edges to Arc/Cubic via the right-click menu (`LayoutShapeEditing.ConvertEdge`)
replaces it with an equivalent `CurveShape` — same `Layer`/`Net`/`Xy`, now with an edge list — swapped
in at the **same index** by `ReplaceShapeCommand`, so Undo restores the exact original `PolygonShape`
instance (not an equivalent copy). A `CurveShape`/`PathShape` (already carrying an edge list) just
gains the converted edge in place, no type change. Reverse demotion (converting every edge back to
Line) is deliberately NOT automatic.

**Insert vertex** (Ctrl/Cmd+click an edge): a Line edge inserts at the **snapped** click point (an
ordinary new vertex); an Arc or Cubic edge instead splits at the **exact parameter** nearest the
click, deliberately **unsnapped** — both resulting sub-edges share the source arc's center/radius (or
are an exact de Casteljau split of the source cubic), so the shape is visually unchanged. Forcing that
point onto the grid would pull it off the original curve. **Remove vertex** is blocked below 3
vertices for a closed shape (Polygon/Curve) or below 2 for an open Path; removing a middle vertex
merges its two adjacent edges into one straight Line (curvature is not preserved through a removed
vertex — there's no principled way to merge two arbitrary curved edges into one equivalent curve); an
open Path's true endpoint removal just drops the one adjacent edge instead of merging.

**Self-intersection is flagged, never blocked or repaired** (`LayoutSelfIntersection.cs`, an O(n²)
segment-pair sweep with adjacency-skip, deliberately excluding the wraparound-adjacent pair for closed
shapes): `LayoutEditorViewModel.WarnIfSelfIntersecting` runs after every committed handle-drag edit and
posts a `Messages.Warning` if the resulting shape self-intersects — the edit is kept either way.
Clipper2-based repair is explicitly out of scope (L1e).

**One gesture, one undo entry, always.** A handle drag calls `BuildHandleDragPreview` on every
`OnPointerMoved` tick (updating only `Overlay.DragOverrides` for the live preview — never mutating
`Model.Shapes` mid-drag) and executes exactly one `ReplaceShapeCommand` at `OnPointerRelease`, no
matter how many intermediate move events fired. **Escape mid-drag restores the original shape and
pushes no command** — `_handleDragOriginal` (the pre-drag shape) is only ever consumed by the eventual
`ReplaceShapeCommand.Execute`; `CancelDrawOp`'s `ResetHandleDragState()` simply discards the live
preview and drops back to the untouched model, mirroring the same Escape contract every other
Layout/Symbol editor gesture already follows.

Test files: `LayoutHandlesTests.cs` (gate 2's data — `LayoutHandles.Build` produces the right handle
set per shape kind: Rect/RoundedRect corners, Circle radius, Polygon vertex+edge-midpoint, Curve
Arc→Bulge/Cubic→two-control-points, Path's N−1 edges, Via/Label have none), `LayoutHandleRenderingTests.cs`
(gate 2's pixel proof — a single selection's edge-midpoint handle paints just outside the shape, a
multi-selection shows none; probes an edge midpoint rather than a corner specifically to avoid the
selection outline's own miter-join overshoot at a vertex, which is easily mistaken for a handle),
`LayoutHandleHitTestTests.cs` (gate 3 — the R-L1d-2 priority order with deliberately overlapping
handles, plus the nearest-within-tier tiebreak), `LayoutShapeEditingTests.cs` (direct unit tests of
every `LayoutShapeEditing` builder — SetVertex/TranslateEdgeEndpoints/SetBulge/SetCubicControl/
SetRadius/SetCornerRadius/ResizeRectCorner, RemoveVertex's three edge-list-surgery cases, InsertVertexOnEdge
for Line/Arc/Cubic edges including the arc-split center/radius/on-circle exactness gate, ConvertEdge's
promotion rule and the plain-Path no-type-change case), `LayoutHandleGesturesTests.cs` (gates 4–12,
driven through `OnPointerPressed/Moved/Released` exactly as the canvas would: grab-radius tolerance
derived from zoom on both starter technologies' snap scales, vertex-drag-snaps-position vs.
move-drag-still-snaps-delta as an explicit regression pairing, 45°-edge-drag-preserves-direction,
Ctrl+click insert and click-vertex-then-Delete/blocked-at-minimum removal, bulge sign-flip-past-chord
and bulge=0-renders-straight, the promotion rule through the VM's `ConvertEdge`/`FindEdgeForContextMenu`
public API with `Assert.Same` proving Undo restores the exact original instance, 50-intermediate-
pointer-moves collapsing to one undo entry with byte-identical `LayoutPersistence.Serialize` round-trip
through Undo/Redo, Escape mid-drag leaving the model provably untouched, and a self-intersecting
vertex-drag posting a Warning while keeping the edit). 65 new tests; 1923 Ui.Tests total, all green;
full solution green (Firewall 4/4, Core 388/388, Engine 461/462 — 1 pre-existing skip, matching the L1c
baseline exactly). **Not interactively verified** (no visual driver in this environment, matching every
prior Layout Editor phase) — correctness rests on the gate test suite above, including the pixel-oracle
handle-rendering tests and the full-VM-gesture tests that drive the actual press/move/release state
machine rather than only the underlying geometry builders. **Next: L1e** (Clipper2 booleans/offsets,
Flatten-to-Polygon, self-intersection repair, cross-cell clipboard).

Escape-key bug fix — `LayoutEditorView` never wired the activation-focus seam (2026-07-26) — COMPLETE:
owner report ("I press Esc but nothing happens") root-caused to two compounding gaps. **(1)**
`WorkspaceWindow.axaml`'s `<KeyBinding Gesture="Escape" Command="{Binding DisarmPlacementCommand}"/>`
is processed by Avalonia before visual-tree routing begins and always marks the event `Handled`,
silently pre-empting `LayoutCanvas`'s plain bubble-phase `KeyDown +=` handler before it ever runs.
**(2)** `LayoutEditorView` never wired the `ActivationFocusRequested`/`ConsumeActivationFocus` seam
that `LayoutDocument` already exposes (unlike `SchematicView`/`SymbolEditorView`), so the canvas likely
never had keyboard focus at all after a tab switch — see this file's "Keyboard shortcut routing" and
"Editor view grabs keyboard focus on tab activation" sections below, which already document the
authoritative pattern. **Fix:** `LayoutEditorView.axaml.cs` now mirrors `SymbolEditorView.axaml.cs`
exactly — a tunnel `KeyDownEvent` handler (`handledEventsToo: true`, gated on
`LayoutCanvasCtrl.IsKeyboardFocusWithin`) forwards Escape to `vm.OnKeyDown`, and a
`DataContextChanged` handler subscribes to `ActivationFocusRequested`/calls `ConsumeActivationFocus()`
to focus the canvas on tab activation, deferred via `Dispatcher.UIThread.Post(Background)`. No new
tests — this is a straight application of an already-documented, already-tested pattern to a view that
had missed it; the fix was confirmed working by the owner interactively.

L1 fix (path seams + live tech) — brief-L1-fix-path-seams-and-live-tech.md, 2026-07-26 — COMPLETE:
two independent owner-reported items.

**(1) `Path` rendered internal seam lines at every bend.** Root cause: `LayoutRenderer.BuildPathOutline`
built the trace outline via `strokeForFill.GetFillPath(centerline, outline)`, and `GetFillPath` does
**not** produce one merged contour — Skia's stroker emits one contour per segment plus a wedge per
join, all overlapping at every bend. That is invisible when **filling** (`DrawLayer`'s
`canvas.DrawPath(shapePath, fillPaint)` uses the default Winding rule, which composites the overlaps
exactly once) and very visible when hairline-**stroking** the same path (`DrawLayer`'s batched outline
stroke traces every contour edge in the path, including the internal boundaries where segment quads
and join wedges abut — those internal boundaries are the seam artifacts a bent trace showed at each
vertex). **Fix:** `outline.Simplify(simplified)` before returning, using the simplified path for BOTH
fill and stroke (never keep two versions); falls back to the unsimplified path only if `Simplify`
itself fails (degenerate input), so a trace is never silently dropped. **This is deliberately a
SEPARATE outline from L1e's Clipper2 geometry offset, and must stay that way** — Clipper2 works on
flattened (polygonal) geometry, so a curved trace's Clipper2 outline is correctly polygonal for
booleans/DRC/Gerber export but WRONG for display, which needs the adaptive, zoom-correct curve
tessellation §3.2 R9c specifies. Two outlines, two purposes: display (here, Skia stroker + Simplify,
curves stay curves) vs. geometry (L1e, Clipper2 offset on the flattened centerline, exact and
integer) — do not "unify" them later; the doc comment on `BuildPathOutline` now carries this warning
directly so it survives. `PathSpace` and `BuildPathOutline` were changed from `private` to `internal`
so `LayoutPathOutlineSeamTests.cs` can drive them directly and pixel-verify the fix precisely, rather
than only through a full `Technology`/viewport setup. **`// L2: cache with the shape path`** is left
at the call site — `Simplify` is an `SkPathOps` call, meaningfully more expensive than plain path
construction; fine at L1 scale (paths rebuild every frame anyway) but it must ride along with L2's
per-shape path cache rather than recompute every frame for thousands of traces. Test file:
`LayoutPathOutlineSeamTests.cs` — a 3-vertex 90°-bend `PathShape`, low fill opacity so the opaque
stroke color is unambiguous, scanned along the horizontal segment's own centerline (which runs
straight through the joint with no cap) asserting **exactly 2** stroke-color pixel runs (the two true
silhouette edges, nothing in between); a bounds-preservation check against a hand-built raw
`GetFillPath` result for the same geometry; and a 3-case degenerate-input theory (identical points,
zero width, both) asserting no throw and a non-null (never-dropped) outline.

**(2) Technology edits now apply live, and closing dirty prompts.** Editing a `.ctech` — color,
`Visible`, `Selectable`, anything — reflects in every open layout **immediately**, without Save;
Save is now purely "write to disk, then let the live copy be superseded by the just-saved on-disk
value." **`TechnologyCache` gained a second dictionary, `_live`** (absolute path → `Technology`),
checked by `Get` BEFORE the file-backed `_cache` — deliberately a separate dictionary rather than a
value stored inside `_cache`, because `ClearLive` (discard-without-saving) must be able to drop the
override and fall back to the last known on-disk value WITHOUT forcing a disk re-read of a file that
was never touched. `SetLive(path, tech)` installs an override and fires `TechnologyChanged` (existing
consumers need zero changes — they already react to that event); `ClearLive(path)` removes only the
live entry (no-op, no event, if none was installed); `HasLiveOverride(path)` gates the reload-confirm
guard below; `Invalidate(path)` now drops BOTH the live override and the plain cache entry (a save, an
external edit, or "Reload Technology" makes either kind of cached value stale); `InvalidateAll` covers
both dictionaries too. **`TechEditorViewModel` gained `event Action<string, Technology>? TechLiveChanged`,
fired from exactly one place: `ApplySnapshot`.** That method is already the single choke point for
both a fresh commit (`CommitEdit` pushes a `TechSnapshotCommand` whose `Execute()` calls back into
`ApplySnapshot`) and undo/redo (`TechSnapshotCommand.Undo` also calls it) — so firing there once covers
every case the brief's event table lists, with no duplicate call in `CommitEdit` itself. **R-fix-1:
the fired value is always an independent clone — `TechPersistence.Deserialize(json)` on the SAME
`json` string that was just used to rebuild `Working`, never `Working` itself.** Two concrete failure
modes if that rule is skipped: `Working` keeps mutating in place before the next commit, so a consumer
holding it directly would observe half-applied edits; and undo/redo **replaces** the `Working`
reference wholesale, so a consumer holding the old instance would silently stop receiving updates
after the first undo. Reusing the already-in-hand `json` (rather than re-serializing `Working`) is the
"one extra deserialize per committed edit" the brief notes — cheap on a small `Technology` object.
**`WorkspaceViewModel` wiring:** `vm.TechLiveChanged += OnTechLiveChanged` alongside the existing
`TechSaved`/`SaveError` subscriptions in `OpenOrActivateTech`. **Coalesce, don't throttle** —
`OnTechLiveChanged` stashes the latest clone per path in `_pendingTechLive` (a dictionary, so a burst
of commits in one gesture — e.g. a multi-selection apply — collapses to the LATEST value per path) and
schedules at most one `Dispatcher.UIThread.Post` per burst; `FlushPendingTechLive` drains the
dictionary and calls `_techCache.SetLive` once per path, so the canvas repaints once per burst, not
once per keystroke-equivalent commit. `ResetTechCache` (called on every workspace-lifetime reset)
also clears `_pendingTechLive`/`_techLiveFlushScheduled` as a belt-and-suspenders guard against a
reset landing in the same dispatcher tick as a pending flush. **The L0c invariant still holds and now
matters more:** `ApplyTechResolution` (unchanged) never re-seeds `DisplayUnit`/`SnapDbu` — with
updates now streaming continuously instead of only at Save, a regression here would silently fight
the user mid-edit; a dedicated test streams five live edits and asserts both stay untouched.
**Discard/save/reload semantics**, all driven through the SAME `SetLive`/`ClearLive`/`Invalidate`
primitives: Save → `OnTechSaved` calls `_techCache.Invalidate(path)` (unchanged code — `Invalidate` now
clears the live override too, so no separate `ClearLive` call was needed there); Close → Don't Save →
`ConfirmCloseDockable`'s `TechDocument` branch now calls `_techCache.ClearLive(techDoc.FilePath)`
before returning (open layouts revert to on-disk); Close → Cancel → nothing changes, override stays;
`PromptSaveBeforeClose`'s bulk Don't-Save branch (quit / workspace-switch with multiple dirty docs)
also clears every dirty tech doc's override; `OnDockableClosed` unconditionally calls `ClearLive` for
ANY closing `TechDocument` as a safety net for a path that skips the confirm hook (a no-op for a
clean/already-saved document). **Close-prompt participation itself was already fully wired** — a code
read of `ConfirmCloseDockable`/`HasAnyDirtyWork`/`PromptSaveBeforeClose`/`SaveAllDocuments` found
`TechDocument` already present in every one of them (Save/Don't Save/Cancel, on quit and on workspace
switch) before this fix — nothing there was actually missing; only the live-override lifecycle needed
adding on top. **"Reload Technology" now guards against silently discarding unsaved edits:**
`ITreeActions.ReloadTechnology` → `ReloadTechnologyAsync` (an `AsyncRelayCommand` now, was a plain
`RelayCommand`); when `_techCache.HasLiveOverride(path)` is true it shows a 2-button
`SaveChangesDialog` ("Discard unsaved changes to '{name}'?", `saveLabel: "Discard"`,
`dontSaveLabel: null`) before calling `Invalidate` — Cancel returns without touching the cache, so the
override survives. Test files: `TechnologyCacheTests.cs` gained 6 tests for `SetLive`/`ClearLive`/
`HasLiveOverride`/`Invalidate`-clears-both; `LayoutLiveTechnologyTests.cs` composes `TechnologyCache` +
`TechEditorViewModel` + `LayoutEditorViewModel` exactly as `WorkspaceViewModel` wires them (mirroring
the existing L0d gate-3 "simulated seam" test) for live color/Visible/Selectable propagation, the
deep-clone-and-undo behavior, discard-reverts, save-clears-the-override, and units-never-re-seeded;
plus two small "simulate the production switch" tests each (mirroring this codebase's established
pattern for `WorkspaceViewModel`-only logic that needs the Avalonia runtime to construct for real) for
the close-prompt Cancel/Don't-Save branches and the reload-guard Cancel/Discard branches. 23 new tests
across both fixes; 1858 Ui.Tests total, all green; full solution green (Firewall 4/4, Core 388/388,
Engine 461/462 — 1 pre-existing skip, matching the L1c baseline exactly). **Not interactively verified**
(no visual driver in this environment, matching every prior Layout Editor phase) — correctness rests on
the pixel-oracle seam test and the composed-real-types live-propagation tests above.



Phase L1c — flattener, hit-testing, selection, move, delete, properties (brief-L1c-selection-and-properties,
2026-07-26) — COMPLETE: a layout shape can now be **selected, cycled through when stacked, dragged, deleted,
nudged, and edited** in a properties panel. Vertex/edge/bulge/control-point handles, Clipper2 booleans, and
the clipboard are explicitly **not** in this phase (L1d/L1e). **Why the flattener (`LayoutFlattener.cs`)
lands here rather than with L1e's booleans:** §6.1 of the design doc calls for **one**
`ToClipperPaths(shape, tolerance)`-equivalent helper shared by booleans, offsets, DRC, the mesher, hit-test,
and export, so the flattening tolerance is never chosen twice with two different answers — hit-testing a
`Curve`/`Circle`/`RoundedRect` is the *first* consumer of that shared helper, so it is built now and L1e's
booleans will simply wrap it for Clipper2 rather than inventing their own. `LayoutFlattener.Flatten(shape,
tolDbu)` returns closed rings for `Rect`/`Polygon` (pass-through, no allocation churn beyond the one array a
`Rect` needs) / `RoundedRect` (4 lines + 4 sagitta-bounded quarter arcs) / `Circle` (sagitta-bounded full
revolution) / `Curve` (edge-list walk: `Line` verbatim, `Arc` via `LayoutArc.FromBulge` + sagitta subdivision,
`Cubic` via recursive de Casteljau split against a chord-distance flatness test); `Path`'s centerline uses
the same edge-walking code through an internal `FlattenOpenEdgeList`, since turning a centerline+width into
a closed *outline* is an offset operation that belongs to L1e's Clipper2 work, not this phase — hit-testing a
`Path` uses distance-to-centerline instead (see below), so nothing here needs the outline. **The sagitta
formula is `2·acos(1 − s/r)` maximum sweep per segment**, clamped from below by a fixed `MinSweepPerSegment`
(2π/4096) so a pathologically large radius can't demand an unbounded segment count — this is the "clamped to
something sane for very large r" the brief calls for. **R-L1c-1 (determinism) is structural, not tested-in**:
every computation is a fixed sequence of double-precision arithmetic over the shape's own integer fields in a
fixed loop order (no dictionaries, no parallelism, nothing machine- or process-dependent), and the closed-ring
implicit-closure convention (never repeat vertex 0 at the end) is enforced by explicitly stripping the
duplicate closing point after both `Curve` and `RoundedRect` assembly. Pinned by
`LayoutFlattenerTests.Flatten_SameShapeAndTolerance_100Times_ByteIdentical` and
`..._AfterSerializeDeserializeRoundTrip_ByteIdentical`.

**`LayoutHitTest.HitStack(view, tech, x, y, tolDbu)`** — framework-free, no spatial index (L2 adds the R-tree;
this signature deliberately doesn't presuppose one). **Ordering, exactly per §6.2 R13**: `ZOrder` descending,
then **ascending bbox area** (reusing `LayoutGeometry.BboxOf` as the size proxy for every shape kind, rather
than an exact per-type area formula — a small shape on a large one on the same layer is reachable, which is
the case that actually matters), then ascending list index as the deterministic tie-break. Filled shapes
(`Rect`/`Polygon`/`RoundedRect`/`Circle`/`Curve`) are tested via `LayoutFlattener.Flatten` at
`min(shape/tech tolerance, click tolerance)` — never coarser than the click tolerance itself, so the polygon
approximation can't hide a hit near a curved edge — then point-in-polygon (ray cast) OR distance-to-edge ≤
tolDbu. `Path` uses distance-to-flattened-centerline ≤ `Width/2 + tolDbu`. `Via` is a direct circle-distance
check (pad radius); `Label` uses an approximate text footprint (character-count × height × a fixed aspect
ratio — framework-free, no font metrics at this layer) duplicated in `LayoutRenderer`'s selection-outline
builder for the same reason (both need it, neither can reach Skia's real text metrics without becoming
Avalonia-coupled). Skips shapes on a layer whose resolved `LayerDef` is `Visible == false` or
`Selectable == false`; unknown layers resolve through `FallbackPalette` (always selectable).

**R-L1c-2 (overlap cycling)** — `LayoutEditorViewModel` caches `(ClickX, ClickY, Stack, Index)` on every
Select-tool press. The next press advances `Index` modulo the stack length **only if** the click is within the
tolerance of the cached point (or Alt is held, which bypasses the distance check entirely — a deliberate
"next candidate regardless of exact pixel" escape hatch) **and** the cache hasn't been invalidated. Three
independent invalidation paths, all required: (1) pointer movement beyond the tolerance threshold, checked in
`HandleSelectMove` before the `leftDown` early-return so a **hover-only** move (no button down) still
invalidates it; (2) **any** model mutation — the constructor's existing `Model.Changed` subscription (already
there for L1b's dirty-tracking) now also nulls the cache and strips any now-out-of-range selected indices,
since every command (`AddShapeCommand`, `MoveShapesCommand`, `DeleteShapesCommand`, `SetShapeFieldCommand<T>`,
undo, redo) calls `NotifyChanged()` — one subscription point covers all of them; (3) a selection change from
anywhere else (marquee commit, Select All, Deselect All, Delete) explicitly nulls it. The status readout
(`LayoutEditorViewModel.SelectionStatusText`) shows `"Rect · M2 · 2 of 5"` only when the current single
selection came from a cache with `Stack.Count > 1` — this is the "without it, cycling reads as a glitch"
requirement; a plain single selection outside a stack shows just `"Rect · M2"`, and a multi-selection shows
`"N selected"`.

**R-L1c-3 — move snaps the delta, not the vertices — is the test this phase is most likely to get wrong
silently.** `MoveShapesCommand` (`src/Ui/Commands/Layout/`) receives an already-snapped `(Dx, Dy)` and adds it
to every vertex of every selected shape via `LayoutGeometry.TranslateBy` — including Cubic edges' `C1X/C1Y/
C2X/C2Y` control points, which are absolute DBU coordinates, not relative offsets, and are easy to forget.
**The wrong version — rounding each moved vertex independently onto the snap grid — looks completely correct
in every test that only uses on-grid fixtures**, because on-grid vertices round to themselves. It only shows
its bug on off-grid geometry (imported GDSII, a 45° diagonal, a flattened arc), where it silently re-snaps and
destroys the shape's internal relationships — exactly what §1.5 R5 forbids. `LayoutEditorViewModel`'s
move-drag computes the delta via `LayoutSnapping.SnapValue(pointerDelta, SnapDbu, altSuspends)` — the *same*
snap primitive L1b already uses for point-snapping, applied to a delta instead of a coordinate, which is
mathematically identical and needed no new snapping code. Pinned by
`LayoutMoveDeleteNudgeTests.Move_OffGridPolygon_EveryVertexMovesByExactlyTheSameSnappedDelta` — a
deliberately off-grid polygon whose vertices must all shift by the exact same integer delta.

**Live move preview never mutates the model mid-drag.** Mirroring the schematic editor's rule ("during an
active drag, do not call BuildRenderModel() per tick — update the overlay only, commit once at drag-end"),
`LayoutOverlay` gained `DragOverrides: IReadOnlyDictionary<int, LayoutShape>` — shape index → a translated
**clone** (`LayoutGeometry.Clone`, deep including edge lists) rendered in place of the real shape.
`LayoutRenderer.DrawLayer` now groups shapes by layer as `(int Index, LayoutShape Shape)` pairs specifically
so it can substitute the override at render time; the real `Model.Shapes[i]` is untouched until
`OnPointerReleased` computes the final total delta and executes exactly one `MoveShapesCommand`. Arrow-key
nudge (one `SnapDbu` step, ten with Shift) goes straight to `MoveShapesCommand` with no drag/preview phase —
each keypress is its own undo entry.

**Selection rendering** — `LayoutOverlay.SelectedIndices` (an accent outline batched into one stroked path,
drawn above every layer, **never touching fill** — the layer color stays the information the user is
reading, per the brief) and `LayoutOverlay.Marquee` (`LayoutMarquee`, a *non-normalized* rect — direction is
meaningful: left-to-right encloses, right-to-left crosses — rendered with the same filled+opaque-edge look
§2.3 gives ordinary geometry, just in the new `ColorRole.LayoutSelection` accent instead of a layer color).
Both are new theme role additions (`ColorTheme.cs`/`LayoutRenderTheme.cs`), light/dark pairs supplied.

**A plain click on a shape already inside a multi-selection preserves the whole selection —
`ApplyClickSelection`'s non-modifier branch does NOT unconditionally replace with `[hitIndex]`.** The naive
version (replace on every plain click, full stop) looks completely correct for single-shape selection and
for cycling, and only breaks the one case that actually matters: Shift-select {A, B}, then plain-click+drag
starting *inside* A intending to drag the pair — the naive version collapses the selection to `{A}` at press
time, before the drag even starts, silently defeating "drag from inside any selected shape translates the
whole selection." The fix: replace-with-hit only when the hit is NOT already part of a `Count > 1` selection;
a click on an unselected shape, or the sole member of an existing single-shape selection (needed so cycling
still replaces-with-itself each step), still replaces normally. Pinned by
`LayoutMoveDeleteNudgeTests.Move_DragFromInsideAnyMultiSelectedShape_TranslatesTheWholeSelection`.

**Marquee** (`LayoutEditorViewModel.HandleSelectPress/Move/Release`, `SelectDragKind.Marquee`) starts only
when a Select-tool press hits **empty space** — a press on a shape always means click-select (+ arms a
move-drag), never a marquee, by construction. Enclose (left-to-right: press.X ≤ release.X) requires the
shape's bbox fully inside the marquee bbox; crossing (right-to-left) requires `Bbox.Intersects`. Shift/Ctrl
captured **at press** (`_marqueeAdd`/`_marqueeToggle`) and applied against a snapshot of the pre-drag
selection at commit — add unions, toggle XORs, plain replaces. A marquee also respects the same
Visible/Selectable layer gate `HitStack` uses (`LayoutEditorViewModel.ResolveLayerDef`, a small local mirror
of the same resolution order — `Technology.Layers` then `FallbackPalette`).

**Properties panel** (`LayoutShapePropertiesViewModel` + `LayoutShapePropertiesView`, wired as `PropertiesTool`'s
fifth mutually-exclusive context via `SetActiveLayout`) — common Layer (swatch+name combo, reusing
`LayoutEditorViewModel.AvailableLayers` directly) and Net (free text); type-specific groups (`RoundedRect`
corner radius, `Circle` radius, `Path` width+end-style, `Label` text/height/rotation, `Curve`/`Path` flatten
tolerance — blank means inherit) show **only when every selected shape is that one type**; a mixed-type
multi-selection shows just the common fields. A shared value displays normally; a **differing** value across
a homogeneous-type selection displays blank, and committing a new value applies it to every shape that has
that field **as one undo entry** — `LayoutShapePropertiesViewModel.ApplyToEach<T>` folds one
`SetShapeFieldCommand<T>` per actually-changing shape into a `CompositeCommand` chain (the same
`CompositeCommand` the schematic editor already uses for multi-step commits; it is binary, so N shapes fold
as N-1 nested pairs). Text/dimension fields are staged (commit on LostFocus/Enter, mirroring
`LayoutEditorView`'s own toolbar fields exactly — invalid text reverts to the canonical-formatted current
value and never throws); combo selections (Layer, End style, Rotation) commit immediately. Dimension parsing
goes through `LayoutUnits.TryParse`/`Format`, per §1 R6.

**Known gap, deliberate:** no literal "Select All / Deselect All" Edit-menu entry — `src/Ui/CLAUDE.md`'s
existing standing rule ("there is intentionally no window-level Ctrl+A binding... each editor owns Ctrl+A
when focused") took precedence over the brief's literal wording. `LayoutEditorViewModel.SelectAllCommand`/
`DeselectAllCommand` exist and are fully wired (Ctrl/Cmd+A routes to Select All inside `OnKeyDown` when the
Select tool is active); a toolbar/menu affordance for them is a small follow-up if the owner wants one.

Test files: `LayoutFlattenerTests.cs` (gate 2 + R-L1c-1 — circle tolerance/monotonic vertex count,
Rect/Polygon pass-through, RoundedRect corner geometry, cubic subdivision terminates, 100× + serialize
round-trip determinism, `ResolveTolDbu` precedence), `LayoutHitTestTests.cs` (gates 3/4/5 — stacking order,
per-primitive accuracy including an arc-bearing `Curve` and `RoundedRect`, `Path` centerline distance,
hidden/non-selectable layer exclusion, fallback-layer selectability), `LayoutSelectionTests.cs` (gates 6/7/
12/13/14 — five-deep cycling incl. wraparound and the status readout, threshold-based and mutation-based
cache invalidation, click-on-empty clears, marquee enclose/crossing/Shift/Ctrl, undo-past-a-delete never
leaves a stale index, screen-pixel hit-testing through `LayoutViewport.ScreenToWorldX/Y` on **both** starter
technologies, and a dedicated proof that the hit tolerance differs by orders of magnitude between a very low
and a very high zoom rather than being cached or derived from `SnapDbu`), `LayoutMoveDeleteNudgeTests.cs`
(gates 8/9/10 — the off-grid-preserving move, Alt-suspends-snap, arrow-key nudge incl. Shift×10, one-undo-
entry-per-nudge, multi-selection delete+undo byte-identical serialization), `LayoutShapePropertiesViewModelTests.cs`
(gate 11 — multi-selection net/radius edits as one undo entry, blank-on-differing-values, `2.9mm`/`nm` parse
and garbage-revert, blank flatten-tolerance means inherit), and `LayoutSelectionRenderingTests.cs` (pixel
oracles in `LayoutRendererTests.cs`'s own style: a selected shape's fill is untouched while its accent
outline appears; the marquee renders its filled accent rect; a `DragOverrides` entry renders the shape at
its translated position and NOT its original one). 52 new tests; 1835 Ui.Tests total, all green; full
solution green (Firewall 4/4, Core 388/388, Engine 461/462 — 1 pre-existing skip, matching the L1b baseline
exactly). **Not interactively verified** (no visual driver in this environment, matching every prior
Layout Editor phase) — correctness rests on the gate test suite above, which — per the brief's explicit
"Read first" lesson from the L1 fix round — deliberately includes screen-pixel-driven tests through
`LayoutViewport.ScreenToWorldX/Y` rather than only world-coordinate tests, specifically because world-
coordinate tests structurally cannot catch a screen-to-world tolerance bug. **Next: L1d** (vertex/edge/bulge/
control-point handles, insert/remove vertex, edge conversion Line↔Arc↔Cubic, and the Polygon→Curve promotion
rule this file's header has been describing since L1b).



L1 fix — unclipped `SKCanvas.Clear` and the nonsense default zoom (brief-L1-fix-clear-and-default-zoom,
2026-07-26) — COMPLETE: the real root causes of the two items the L1b post-ship entry below left
unresolved, found by reading the code rather than by rendering. **(1) The toolbar, both rulers, and the
metadata bar were wiped by the canvas — "invisible until hover, invisible again on exit."** Avalonia
hands an `ICustomDrawOperation` the WHOLE render-surface canvas; `ICustomDrawOperation.Bounds` is used
for invalidation/hit-testing only, it does **not** clip Skia. `LayoutRenderer.Draw` and
`LayoutRulerRenderer.Draw` both called `canvas.Clear(...)` with no clip in force, which fills the
*entire current clip region* — the whole window — wiping every sibling control already painted that
frame (`LayoutEditorView.axaml` paints `ToolbarBorder` → metadata `Border` → `HRuler` → `VRuler` →
`LayoutCanvas` → placeholder, in that order, so the canvas's `Clear` destroyed all four controls
painted before it). `SymbolEditorRenderer.Draw` calls `canvas.Clear` too, and gets away with it for
exactly one reason: `SymbolEditorView.axaml` sets `ClipToBounds="True"` on `SymbolEditorCanvas`, which
makes Avalonia push a clip before rendering the control, constraining `Clear` to that control's own
rectangle — `LayoutCanvas`/`LayoutRulerControl` did not have it. That single attribute was the entire
difference between the two editors, never the `WrapPanel`↔`ScrollViewer` swap and never a
Grid-vs-DockPanel choice (both ruled out during the investigation; neither was ever the cause). It also
explains the exact hover signature reported: hovering a toolbar button invalidates only that button's
small rect, which the canvas does not intersect, so the button paints normally and the toolbar
"appears"; any pointer move over the canvas calls `InvalidateVisual()`, forcing a full repaint in which
the unclipped `Clear` wipes the toolbar again. **Fix, both applied:** `LayoutRenderer.Draw` and
`LayoutRulerRenderer.Draw` now `canvas.Save()`/`canvas.ClipRect(...)` before drawing anything (a cached,
`[ThreadStatic]` `SKPaint` fills the background via `DrawRect` instead of `Clear`, restored in
`finally`); `LayoutEditorView.axaml` also gained `ClipToBounds="True"` on `LayoutCanvas` and both
`LayoutRulerControl`s, matching `SymbolEditorView.axaml`. Do both — the renderer fix is the one that
does not depend on a caller remembering an attribute; the AXAML fix is correct anyway. Regression gate:
`LayoutRendererTests.Draw_NeverPaintsOutsideTheViewportRect` and
`LayoutRulerRendererTests.Draw_NeverPaintsOutsideTheStripRect` render into an `SKSurface`
**larger** than the viewport, pre-fill it with a sentinel color, and assert every pixel outside the
viewport rect is untouched — this is the contract directly, no compositor and nothing to distrust. **A
correction about the investigation itself:** the headless test harness used throughout was **not**
defective — an earlier session concluded it was, because a minimal probe (a plain toolbar next to any
custom-draw control, including `SymbolEditorCanvas`) reproduced "toolbar absent" 100% of the time
including for a control with no reported real bug. That was a true positive: `SymbolEditorCanvas`
*does* carry the same latent unclipped `Clear`, protected only by the caller-side `ClipToBounds`
attribute — the harness was correctly showing that the known-good editor has the same bug when that one
attribute is missing from its host. **Do not discard the headless harness over this again; restore and
reuse it.** **(2) The default zoom made the first shape impossible to draw** — not a placeholder bug;
the `IsEmpty`/`PropertyChanged` wiring from the L1b entry below is correct and stays. `LayoutViewport`'s
`Zoom` is **device pixels per DBU** (confirmed by `LayoutCanvas.Zoom1To1`); `LayoutViewport.Default`
hardcoded `zoom = 1.0`, and at the default `DbuPerMicron = 1000` (1 DBU = 1 nm) that is **1 screen pixel
per nanometre** — a brand-new empty layout showed a window roughly 1.5 µm wide. The PCB starter
technology's `DefaultSnapDbu` is 1 mil (25,400 DBU), so the entire visible canvas was ~6% of one snap
step: every pointer position in the whole window snapped to the same grid coordinate, `BuildTwoPointShape`
saw a zero-width/zero-height/zero-radius result on every drag, and returned null every time — `IsEmpty`
never had a chance to go false. **World-coordinate unit tests structurally cannot catch this class of
bug** — every existing gesture test called `OnPointerPressed(wx, wy, …)` with world coordinates
directly, bypassing `ScreenToWorld` and the default viewport entirely; the bug lives exactly in the gap
those tests skip over, which is why the new regression tests route through `LayoutViewport.ScreenToWorldX/Y`.
**Fix:** `LayoutViewport.Default` now takes the layout's own `SnapDbu`/`DbuPerMicron` and frames ~200 snap
steps across the viewport width (clamped to `[MinZoom, MaxZoom]`, with a plain micron-scale fallback span
when `SnapDbu <= 0`) instead of a fixed `zoom = 1.0` — physically meaningful and immediately drawable for
both starter technologies. `LayoutCanvas.OnLayoutUpdated`'s initial-fit guard no longer requires
`Shapes`/`Instances` to already be non-empty — it now fits exactly once per bound ViewModel as soon as
`Bounds` is valid, empty or not, so an empty layout gets the new sane default instead of the raw
`zoom = 1.0` field default. The auto-fit-on-first-shape hack added in L1b's `LayoutCanvas.OnModelChanged`
is removed — it existed only to compensate for the broken default zoom, and once the default is sane it
actively hurts (every first shape would yank the viewport to frame that one shape alone).
`OnModelChanged` is back to a plain `InvalidateVisual()`. Separately, a real drag can still legitimately
collapse to zero after snapping even when the raw (unsnapped) pointer movement was non-zero — `Rect`/
`RoundedRect`/`Circle` now expand the affected axis (or radius) to one snap step instead of silently
returning null in that case, via `TryExpandDegenerateAxis`/raw press-vs-current tracking
(`_drawP1RawX/Y`/`_drawP2RawX/Y`, never persisted — only ever compared against, to distinguish "the
pointer moved and the grid ate it" from "the pointer never moved on this axis," which still correctly
yields no shape). Regression gates: `LayoutL1FixTests.DefaultViewport_TwoScreenPointsApart_MapToDistinctSnappedWorldCells`
(both starter techs — this is the test that would have caught the bug), `..._GridIsVisible_AtOrAboveTheEightPixelThreshold`,
`EndToEnd_ScreenCoordinates_PcbTech_DrawsRectShape_AndClearsIsEmpty` (drives the tool state machine with
screen points converted through `ScreenToWorld`, not world coordinates), `SubSnapStepDrag_NonDegenerateRaw_YieldsOneSnapStepRect_NotNull`,
and `ZeroLengthClick_NoRawMovement_StillYieldsNoShape` (the negative case). 9 new tests; 1783 Ui.Tests
total, all green; full solution green (Firewall 4/4, Core 388/388, Engine 461/462 — 1 pre-existing
skip). **Not interactively re-verified on a real machine** (no visual driver in this sandboxed
environment) — but unlike every prior round on this bug, both root causes here were found by reading
the code, are mechanically certain (not "sometimes" or compositor-timing-dependent), and are covered by
tests that pin the actual contract (never paint outside the viewport rect; the default viewport is
provably drawable) rather than relying on a screenshot. Please confirm on your end that the toolbar
now renders on first paint and stays visible, and that drawing a shape into a brand-new layout shows up
immediately.

L1b post-ship fixes (2026-07-26) — COMPLETE: three owner-reported issues after trying the Layout
Editor for the first time. **(1) Drawing a shape appeared to do nothing / the canvas stayed on the
"Empty layout" placeholder:** two independent bugs, both now fixed. **(1a)** `IsEmpty`/`ShapeCountText`/
`InstanceCountText`/`ExtentText` on `LayoutEditorViewModel` are all computed from `Model.Shapes`, but
nothing ever raised `PropertyChanged` for them when the model mutated — `LayoutView.Changed` fires (so
`LayoutCanvas` itself repaints correctly), but the *view's* `IsEmpty`-bound placeholder `Border` (drawn
on top of the canvas per the L1a XAML) never rehid itself, so a successfully-drawn shape rendered
underneath a placeholder that never went away. Fixed: the `LayoutEditorViewModel` constructor now
subscribes to `Model.Changed` and re-raises `PropertyChanged` for all four. **Rule: any VM property
computed from `Model.Shapes`/`Model.Instances` must be re-raised from that same `Model.Changed`
subscription — it will not update on its own.** **(1b)** `LayoutCanvas`'s initial zoom-to-fit only ever
ran from `OnLayoutUpdated`, whose guard requires `Shapes`/`Instances` to already be non-empty — so a
layout that *started* empty never cleared `_needsInitialFit`, and drawing the very first shape left the
view at its default pan/zoom (screen-pixel-scale around the origin), which for typical DBU-scale
geometry could put the new shape completely out of the visible viewport even though it was correctly in
the model and correctly rendered. Fixed: `LayoutCanvas.OnModelChanged` (the `Model.Changed` handler)
now also performs the initial-fit check, so the first shape drawn into an empty layout auto-fits the
view exactly like loading a non-empty `.clay` already did. **(2) The toolbar appeared invisible until
the pointer moved over it, and went invisible again on pointer-exit:** the real root cause (an
unclipped `SKCanvas.Clear` in `LayoutRenderer`/`LayoutRulerRenderer` wiping the whole render surface —
`ICustomDrawOperation.Bounds` does not clip Skia) is **fixed**; see the "L1 fix" entry at the top of
this file for the full story, including a correction of two theories floated here originally (the
`WrapPanel`↔`ScrollViewer` swap and a Grid-vs-DockPanel choice) — neither was ever the cause, and the
headless test harness used to investigate this was not defective either.
**(3) Project Tree "New Layout" was hardcoded `IsEnabled="False"`:** a leftover stub from Phase 6g
(2026-06-19-era), written before the Layout Editor existed at all ("New Layout → IsEnabled=False,
greyed, v2" — see the 6g history further down this file) and never revisited once L0-L1b landed.
`ITreeActions.NewLayoutAsync` + `ProjectTreeNodeViewModel.NewLayoutCommand` + `WorkspaceViewModel.
NewLayoutAsync` now mirror `NewSchematicAsync`/`NewSymbolAsync` exactly (prompt for a name, validate,
write an empty `.clay` via `LayoutPersistence.SaveToFile`, seed `DisplayUnit`/`SnapDbu` from the
resolved workspace-default technology exactly like the File-menu `NewLayout` command, open a materialized
`LayoutDocument`, `Messages.Success`). `Open Layout`'s enablement (`CanOpenLayout`, resolved via
`CellFolder.ResolvePrimary(..., ViewType.Layout)`) was already correct and already covered by
`ProjectTreeNodeViewModelTests` (`Cell_SoleLayout_CanOpenLayoutTrue` et al.) — nothing to fix there.
2 new tests in `LayoutDrawingToolsTests.cs` (`DrawingAShape_RaisesPropertyChanged_ForIsEmptyAndMetadataBarCounts`,
`UndoingTheOnlyShape_RaisesPropertyChanged_IsEmptyBecomesTrueAgain`) cover fix (1a) directly; item (2)'s
real fix and its own regression tests are in the "L1 fix" entry at the top of this file. 1774 Ui.Tests
total at the time; see the L1 fix entry above for the current total. **Not interactively re-verified**
(no visual driver in this environment) — reasoned from the binding/layout mechanics above, exactly as
flagged for every prior Layout Editor phase.

Phase L1b — drawing tools, snap, and undo (brief-L1b-drawing-tools, 2026-07-26) — COMPLETE: a layout
can now be *drawn* — pick a layer, pick a tool, draw a shape, watch it land in the layout's color with
a live dimension readout, Ctrl+Z removes it. No selection/hit-testing/handles/move/delete-by-picking/
clipboard/booleans — those are L1c/L1d. **Fine-grained commands, not snapshot undo — a deliberate
departure from L0d's `.ctech` editor.** `TechEditorViewModel` snapshots the whole `Technology` per
edit because a technology is tens of layers and a handful of stackup entries — cheap to clone every
time. A layout can hold 10³–10⁶ shapes (§5.1 of the design doc), so cloning the whole model per edit is
exactly what that budget forbids; `LayoutEditorViewModel` instead owns its own `UndoRedoStack` and
every drawn shape is one `AddShapeCommand` (`src/Ui/Commands/Layout/`), the house `IUiCommand` pattern.
Only one command exists in L1b, but the plumbing is built for the dozen more L1c/L1d add on top (that's
why `AddShapeCommand` exists as a real command rather than an inline lambda). **Restore-at-original-
index, not append.** Z-order within a layer is list order (§2.3), so `AddShapeCommand` captures the
shape's index on its *first* `Execute` and every subsequent Undo/Redo re-inserts at that exact
position (`_view.Shapes.Insert(_index, _shape)`) — this is what makes "draw A, B, C; undo C; undo B;
redo B" put B back at index 1, never appended past C's old slot; an Undo that quietly re-appended would
be a rendering-order bug that only surfaces much later as a silent z-order swap. **`LayoutView` gained
a `Changed` event + `NotifyChanged()`** (mirrors `EditableSymbol.Changed`) so `LayoutCanvas` can repaint
on mutation — `LayoutCanvas.ViewModel`'s setter subscribes to `Model.Changed` once (the VM's `Model`
reference itself never changes post-construction, so no re-subscription logic is needed). **Dirty
tracking combines two independent sources**: `_prefsDirty` (a DisplayUnit/SnapDbu edit — still
deliberately carries NO undo entry, per §1.3/§1.5) OR'd with `_undoRedo.IsModified` (a geometry edit);
`MarkSaved()` clears both together and is what `LayoutDocument.Materialize`/`OnSavedAs` now call instead
of setting `IsDirty` directly (mirrors `SymbolEditorDocument.Materialize` calling
`ViewModel.UndoRedo.MarkSaved()`). **The current-layer combo is session state, deliberately NOT
persisted in `.clay`** — `LayoutEditorViewModel.AvailableLayers`/`CurrentLayerKey` are populated from
`Technology.Layers` (ordered by ZOrder) or a small fixed fallback set (`1/0`…`4/0` via
`FallbackPalette`) when there is no technology, repopulated on every `ApplyTechResolution` (keeps the
current selection if its key survives, else falls back to the first layer — never throws, gate 11
covers the removed-current-layer case explicitly). **There is deliberately no `Curve` drawing tool.**
`LayoutEditorViewModel.Tool` is `Select, Rect, RoundedRect, Circle, Polygon, Path, Label` — no `Curve`,
because the interaction that actually creates a curved edge (drag a segment's midpoint to set its
bulge) is the *same* interaction as L1c's bulge handle; building it once there and reusing it at draw
time beats two implementations that drift. **The promotion rule** (documented in `LayoutModel.cs`'s
header now, implemented in L1c): a `PolygonShape` whose edge is converted to an Arc/Cubic via that
future bulge-handle gesture is replaced by an equivalent `CurveShape` carrying the same `Xy` plus the
new edge list — Polygon is the "all edges are Line" special case of Curve, not a separate lineage;
`PathShape` already carries an edge list from L0a and just gains the curved edge in place. **Snap +
angle mode** (`LayoutSnapping.cs`, framework-free): `SnapValue`/`SnapPoint` snap to `LayoutView.SnapDbu`
(Alt suspends per-point, `SnapDbu &lt;= 0` means none); `ConstrainAndSnap` constrains a candidate vertex
relative to the previous one per `AngleMode` (Manhattan/Deg45/AnyAngle) and snaps a **single scalar
distance along the already-chosen direction**, never X/Y independently — this guarantees "never emit an
off-mode segment" *by construction* (both axis components are always exact multiples of the same
snapped magnitude — equal for a 45° diagonal, one of them exactly zero for an axis direction) rather
than needing the brief's suggested independent-snap-then-fallback-check. Angle mode never applies to
`Circle`/`RoundedRect`. **Live readout + typed entry (§1 R6):** `LayoutEditorViewModel.DrawReadoutText`
updates every pointer move (W×H / radius / running-segment-and-total, via `LayoutUnits.Format`); the
toolbar's corner-radius/path-width+end-style/label-height fields are staged text committed through
`LayoutUnits.TryParse` — invalid text reverts to the last good value and never throws (gate 8).
**Typed Rect commit** (gate 9, the §10.10 "click start, click end, type W=2.9mm" step): `CommitDrawWidthText`/
`CommitDrawHeightText` stage overrides on a live Rect drag without finalizing it;
`CommitTypedRect` (wired to Enter in either field) finalizes at exactly the staged W/H — anchored at
the original press point, extended in +X/+Y, regardless of where the drag currently sits — as one
undo entry, same as a normal release. **`LayoutCanvas` now takes a single `ViewModel` DirectProperty**
(not separate `Model`/`Technology` properties, as in L1a) so it can dispatch left-press/move/release
and keyboard/text input directly to the VM's tool state machine, mirroring `SymbolEditorCanvas`; middle-
drag pan and wheel zoom are checked *before* any VM dispatch, so both keep working mid-gesture (panning
mid-polygon is normal). Crosshair cursor for every drawing tool, arrow for `Select` (inert in L1b — a
registered tool that does nothing, same as the brief specifies). **The in-progress ghost** renders via
`LayoutRenderer`'s new `LayoutRenderOptions.Overlay` — reuses `BuildShapePath` (no second geometry
path), drawn above every committed layer in the ghost's own resolved layer color with a faint fill and
a dashed hairline outline; never contributes to `LayoutRenderResult.UnknownLayers` (an uncommitted
shape's layer choice isn't a gap to warn about). Test file: `LayoutDrawingToolsTests.cs` (gates 2–12:
every tool produces the right primitive on the current layer; 12-vertex polygon is one undo entry;
draw-A-B-C/undo/redo restores B at its original index; snap incl. Alt-suspend and `SnapDbu=0`; Manhattan/
Deg45/AnyAngle segment constraints; snap/angle-mode changes leave serialized `Shapes` byte-identical;
typed entry parse+revert; typed Rect commit regardless of pointer position; Escape leaves the model
untouched and clears the overlay; Backspace drops exactly one vertex; fallback-layer usability and the
removed-current-layer fallback; dirty-tracks-undo-and-clears-on-undo-to-saved; save+reload round-trips
every drawn shape) — 27 new tests; 1772 Ui.Tests total, all green; full solution green (Firewall 4/4,
Core 388/388, Engine 461/462 — 1 pre-existing skip, matching the L1a baseline exactly). **Not
interactively verified** (no visual driver in this environment, matching every L0/L1a phase's
precedent) — correctness here rests on the headless VM-level gesture tests above, which drive the tool
state machine with synthetic pointer/key events exactly as the canvas would call it.
**Next: L1c** (selection with overlap cycling, vertex/edge/bulge/control-point editing, move, delete,
edge conversion — the Curve-promotion rule's real implementation — and the properties panel with layer
and net).

Phase L1a — the layout canvas: rendering, pan/zoom, grid, rulers (brief-L1a-layout-canvas, 2026-07-26)
— COMPLETE: a layout is now *visible* — geometry draws in the resolved technology's layer colors over
a decimated grid, framed by rulers, with fluid Y-up pan/zoom. No editing at all (no tools, no
selection, no hit-testing) — that is L1b/L1c/L1d. **`LayoutRenderer`** (`src/Ui/Renderers/LayoutRenderer.cs`)
is the new Skia entry point (`Draw(canvas, LayoutView?, Technology?, LayoutViewport, LayoutRenderOptions) ->
LayoutRenderResult`); `SchematicRenderer.DrawSymbol` is **not** reused (§0 of the design doc: one
`SKPath` per primitive per frame is right for ~20 symbol primitives and wrong for 10³–10⁶ layout shapes).
**The path-space coordinate convention (R-L1a-1/2) — the thing this phase had to get right first:**
`SKPath` is float32 (24-bit mantissa, ~16.7M distinct values); a 300mm board at 1nm resolution is
~3×10⁸ DBU, so feeding raw DBU into a path quantizes badly far from the origin — and the artefacts
only appear when someone zooms in far from (0,0), the worst possible time to discover it. The fix
(`LayoutRenderer.PathSpace`, private struct): every vertex is mapped to `(dbu - origin) * dbuToUm`,
where `origin` is a per-frame anchor near the viewport centre, quantized to a power-of-two step
(`ComputeOrigin`) so it changes only roughly once per screen's worth of panning — magnitudes are then
bounded by the *visible extent* in micrometres, not by absolute position, so a sub-micron feature
300mm from the origin still renders at full precision (gate 9, `SmallFeature_AtLargeCoordinate_
RendersCorrectSize_NoQuantization`). Pan/zoom are then a single positive-scale `SKMatrix`
(`SKMatrix.CreateScaleTranslation`) applied to the whole path-space geometry once per frame — panning
never rebuilds a single path. **Y is flipped once, at path-space construction** (layout's own
coordinate system is Y-up/physical/GDSII convention, unlike the schematic/symbol canvases' Y-down
screen sense — see `LayoutViewport`), which is exactly why arc math is the one place this file has a
sharp edge: **arc parameters (center/radius/start-angle/sweep) must always be derived from the
original DBU (Y-up) endpoints via `LayoutArc.FromBulge`, never re-derived from the already-flipped
path-space floats** — a flip is a reflection (determinant -1) that reverses an arc's sweep sense, so
re-deriving from flipped points with the same signed bulge silently fits a *different* arc through the
same two endpoints (same sweep magnitude, wrong center) rather than the mirrored version of the
original one. The fix is a single negation of the world-computed start angle and sweep when converting
to Skia's `ArcTo(SKRect, startDeg, sweepDeg, forceMoveTo)` convention (`AppendEdge`). This bug is
genuinely silent — it still draws *a* curve, just the wrong one — which is why it has a dedicated
regression test (`ClosedCurve_OfFourQuarterArcs_FillsLikeACircle`, four 90° arcs that must fill like a
circle, not some other bulgy shape) rather than relying on the softer "everything draws" gate alone; it
was caught by that test during development, not by inspection. **The compositing contract (§2.3 R8a) —
per-shape fill / batched hairline stroke:** fills are drawn individually per shape at the layer's
`FillOpacity` (so same-layer overlap composites darker — the owner's decision, `OverlappingSameLayer
ShapesUnitLayer_CompositeDarkerThanSingleCoverage`), while every shape's outline is accumulated into one
`SKPath` per layer and stroked once with `StrokeWidth = 0` — Skia's hairline special case, exactly 1
device pixel at any zoom regardless of the CTM (`OutlineStroke_SamePixelThickness_At1xAnd100xZoom`).
One `SKPaint` per layer per role (fill/stroke), reused across every shape on that layer; the R8b merge
tier (an LOD fallback above ~20k shapes) is explicitly **not** built yet — L1a always draws per-shape
fills. **Curves render natively, no flattener**: `Line`→`LineTo`, `Arc`→`ArcTo`, `Cubic`→`CubicTo`,
`Circle`→`AddCircle`, `RoundedRect`→`AddRoundRect` — Skia tessellates adaptively at the current
transform, which **is** §3.2 R9c's "rendering flattens adaptively at screen resolution," for free.
`PathShape` (a trace) builds its centerline via the same edge-list path builder, then
`SKPaint.GetFillPath` (stroke-to-fill, with the mapped cap: `Flush`→`Butt`, `Round`→`Round`,
`Square`→`Square`, `Extended`→`Butt` with the centerline pre-extended by `width/2` in DBU space before
any transform) produces the real outline, which is then filled/stroked like any other shape.
**Layer resolution + the fallback palette:** `LayerDef` if the resolved `Technology` defines the key,
else `FallbackPalette.For(key)` — gap-filling only the missing layer, never the whole technology.
Unknown keys are collected during the frame (never posted from inside the render loop — `LayoutRenderer`
never touches `IMessageSink`) and returned via `LayoutRenderResult.UnknownLayers`; `LayoutCanvas` raises
`FrameUnknownLayers` after each paint, `LayoutEditorView` forwards it to
`LayoutEditorViewModel.ReportUnknownLayers`, which dedupes against a per-document `HashSet<LayerKey>` so
a layer with thousands of shapes warns exactly once per load, not once per shape (this is the "not yet
wired" seam L0c deliberately left open, now wired). **`LayoutCanvas`** (`src/Ui/Controls/LayoutCanvas.cs`)
clones `SymbolEditorCanvas`'s shape (viewport state owned by the canvas, mirrored out via
`ViewportChanged`/`CursorWorldChanged` for readouts) but is Y-up throughout; middle-mouse pans always,
Space+left-drag is the alternative; wheel zoom is cursor-anchored (`LayoutViewport.WithZoomAnchoredAt`,
gate 5); `ZoomToFit`/`ZoomIn`/`ZoomOut`/`Zoom1To1` are plain public methods called directly from toolbar
`Button.Click` handlers in code-behind, exactly like `SymbolEditorCanvas.ZoomToFit()` — no VM commands
own the viewport. **Zoom 1:1 is defined as 1 device pixel per one tick of the document's display unit**
(1 px = 1 mil on a PCB layout, 1 px = 1 µm on an MMIC layout) via `LayoutUnits.ToDbu(1, DisplayUnit,
DbuPerMicron)` — a stable, physically-meaningful "actual size," not an arbitrary 1:1 DBU ratio. Left
mouse is a no-op with a clearly-marked seam comment for L1b's tool dispatch. Initial fit-on-first-render
only when the layout is non-empty; the L0b centered placeholder ("Empty layout — drawing tools arrive in
L1b.") stays for an empty one (`LayoutEditorViewModel.IsEmpty`). **The grid** (`LayoutRenderer.DrawGrid`)
is computed entirely in **screen space** (never touches the path-space float32 path — R-L1a-3's "never
draws sub-pixel" and R-L1a-1's quantization concern are two different problems with two different fixes):
`LayoutGridMath.ComputeGridPitch` (framework-free, `src/Ui/Layout/`) decimates the snap pitch through the
1/2/5×10ⁿ sequence until the on-screen dot spacing clears an 8px floor, or returns null (grid disappears
rather than degenerating) if it never can. Major dots every 5 minor steps (`MajorGridStepCount`).
**Rulers** (`LayoutRulerRenderer` + `LayoutRulerControl`) reuse the same 1/2/5×10ⁿ chooser
(`LayoutGridMath.ComputeRulerTickStepDbu`) in the document's `DisplayUnit` so labels never collide, plus
a cursor position indicator on each ruler and an X/Y readout in the metadata bar
(`LayoutEditorViewModel.CursorXText`/`CursorYText`, `SetCursorWorld`) — switching the display-unit combo
relabels both (already proven not to touch geometry by L0b's `DisplayUnitChange_IsSerializationNoOp`
test). Test files: `LayoutRendererTests.cs` (gates 2/3/4/9 — every shape type, R8a darkening, circle
area within 2%, hairline thickness invariant under 100× zoom, fill opacity, fallback palette + gap-fill
+ once-per-layer warning, the arc-handedness regression, large-coordinate fidelity),
`LayoutGridMathTests.cs` (gate 7 — grid decimation never sub-pixel across a wide zoom sweep, disappears
rather than degenerating; gate 8 — ruler tick step never collides), `LayoutViewportTests.cs` (gates 5/6 —
zoom-anchors-at-cursor, Zoom Fit on tiny and ~300mm fixtures), `LayoutEditorViewModelL1aTests.cs`
(`IsEmpty`, cursor-readout formatting, unknown-layer warn-once across many frames/shapes, and gate 10 —
"the L0 loop closes": re-resolving a `Technology` with an edited layer color changes the rendered pixel).
35 new tests; 1745 Ui.Tests total, all green; full solution green (Firewall 4/4, Core 388/388,
Engine 461/462 — 1 pre-existing skip, matching the L0 baseline exactly). **Not interactively verified**
(no visual driver in this environment, matching every L0 phase's precedent) — correctness here rests on
the pixel-oracle test suite above, which is deliberately stronger than usual for exactly that reason.
**Next: L1b** (drawing tools — Rect/RoundedRect/Circle/Polygon/Curve/Path/Label/Port, snap and angle
mode, live dimension readout during draw/drag, and fine-grained `IUiCommand` undo).

L0d post-ship fixes (2026-07-26) — COMPLETE: four owner-reported issues in the `.ctech` editor.
**(1) Color swatches always rendered grey:** `Border.Background="{Binding SwatchColor}"` does NOT
auto-convert a bound `Avalonia.Media.Color` to a `Brush` — that implicit conversion only applies to
XAML-literal `{DynamicResource System*Color}` lookups, not regular property bindings (a sibling gotcha
to the `BorderBrush` Color-vs-Brush note further down this file). Fix: wrap it explicitly —
`<Border.Background><SolidColorBrush Color="{Binding SwatchColor}"/></Border.Background>` — matching
the pre-existing pattern in `SettingsView.axaml`'s role swatches. **Rule: never bind a `Color`-typed VM
property directly to `Background`/`BorderBrush`/`Fill`/`Stroke` — always route it through an explicit
`SolidColorBrush` element.** **(2) Header/control misalignment (Layers tab, and DRC Rules
proactively):** the header row and each item row were two independently-sized `Grid`s with per-cell
`Margin="3,0,0,0"` ad hoc spacing that drifted out of sync — not actually a centering problem, a
column-identity problem. Fixed by giving header and row `Grid`s in `TechEditorView.axaml` **identical**
`ColumnDefinitions` (same `SharedSizeGroup` names under the existing `Grid.IsSharedSizeScope="True"`)
and a shared `ColumnSpacing` instead of per-cell margins, plus `HorizontalAlignment="Center"` on the
non-Name header labels. **When adding any header+row grid pair, declare the column list once and
copy it verbatim to both Grids — do not hand-tune matching pixel widths per cell.** **(3) Purpose column
grew with the window instead of Name:** the `*` (star) column was column 8 (Purpose); swapped so column
0 (Name) is `*` and Purpose is a fixed `Width="140"` `TextBox` in an `Auto` column. **(4) Stackup
drawing-layer cardinality — was multi-select for all three kinds, is now kind-dependent per
docs/design/layout-view.md §10.4:** a **conductor** is explicically "bound to one or more drawing
layers" (multi-select stays correct — e.g. a plane repeated across several drawn layer numbers); a
**via** is "bound to a drawing layer" (singular) and a **dielectric** slab likewise corresponds to at
most one outline/extent layer. `StackupLayerRowViewModel.AllowMultipleDrawingLayers` (`Kind ==
Conductor`) gates this: `SetDrawingLayerChecked` now clears any prior selection before adding when
`!AllowMultipleDrawingLayers`, so checking a new box for a via/dielectric un-checks the old one instead
of adding to it. The drawing-layers section, previously hidden entirely for `Dielectric`
(`IsVisible="{Binding !IsDielectric}"`), is now shown for all three kinds — the model already supported
a dielectric's drawing layer (`StarterTechnologies.MmicGaAs`'s GaAs stackup entry seeds one) but the UI
never exposed it. Label text is `DrawingLayersLabel` ("Drawing layers:" vs "Drawing layer:", singular
for non-conductor). 3 new tests in `TechEditorDocumentTests.cs` (conductor multi-check stays checked;
via/dielectric second-check-replaces-first, via `[Theory]` over both kinds); 1709 Ui.Tests total, all green. Not interactively verified
(no visual driver in this environment) — reasoned from the binding/layout mechanics above, not observed.

**Stackup polish (same day):** a subtle units-reminder label now sits right of the Thickness field —
`StackupLayerRowViewModel.ThicknessUnitSuffix` reads `LayoutUnits.Suffix(Working.DefaultDisplayUnit)`.
`LayoutUnits.Suffix(LayoutUnit)` is a new public helper (`src/Ui/Layout/LayoutUnits.cs`) — the single
source for this mapping; `LayoutEditorViewModel`'s previously-private duplicate now delegates to it
(DRY cleanup, not a behavior change). Thickness and σ (S/m) were briefly tried on one shared horizontal
row for a conductor; reverted to σ on its own row below Thickness (owner call after seeing it), but
**with its TextBox still visually aligned under the Thickness TextBox** — both rows' label
`TextBlock`s ("Thickness:" / "σ (S/m):", different lengths) now share the same fixed `Width="70"`, so
both TextBoxes start at the same x despite the label-text-length mismatch, AND the two TextBoxes
themselves both carry a matching fixed `Width` (settled at `60`, after `90`/`100` mismatched then `100`/`100`) so the fields
read as a matched pair, not just co-aligned on the left edge. **When two stacked label+field rows need
their fields to line up and a full shared-size grid is overkill, give the labels a matching fixed
`Width`, and give the fields a matching fixed `Width` too — aligning left edges alone still looks
mismatched if the boxes themselves are different sizes.**
1 new test (`ThicknessUnitSuffix_ReflectsTechnologyDefaultDisplayUnit`); 1710 Ui.Tests total, all green.

Phase L0d — the `.ctech` editor document (brief-L0d-ctech-editor, 2026-07-26) — COMPLETE: **Phase L0 is
complete.** Double-clicking a `.ctech` now opens a dockable, tear-off-capable editor — layer table, stackup,
DRC rules, live validation — and saving fires L0c's change seam so every open layout picks up the edit
immediately. **`TechEditorViewModel`** (`src/Ui/Layout/TechEditorViewModel.cs`) owns the working `Technology`
plus three `ObservableCollection` projections (`Layers`/`StackupLayers`/`DrcRules`) and re-runs
`TechValidation.Validate` after every committed edit (cheap, never throws — no reason to defer it).
**Undo is coarse-grained whole-`Technology` snapshots, a deliberate departure from the schematic/symbol
editors' fine-grained `IUiCommand`s.** Those editors mutate large geometry documents where cloning per edit
would be far too expensive, so they record deltas; a `Technology` is tens of layers, a handful of stackup
entries, and a few rules — small enough that `TechPersistence.Serialize`/`Deserialize` (already the
exhaustively-tested `.ctech` round-trip) doubles as an exact, trivial deep clone. `CommitEdit(beforeJson,
description)` snapshots after the mutation already happened in place, no-ops if nothing changed, and pushes
one `TechSnapshotCommand` (`src/Ui/Layout/TechSnapshotCommand.cs`) per **committed** edit (a field commit, an
add, a remove, a reorder) — never per keystroke. Both `Execute`/`Undo` replace `Working` wholesale via
`ApplySnapshot`, which orphans every row view model bound to the old instance (expected — mirrors
`CellParameterRowViewModel`'s "orphaned by RebuildRows" convention) and rebuilds all three collections.
`IsDirty` follows `UndoRedo.IsModified` exactly like `SymbolEditorViewModel` (`MarkSaved()` on save), so undo
back to the saved baseline clears the dirty bullet for free. **`.ctech` has no scratch state** — unlike a
layout, a technology is always workspace-scoped configuration backed by a file from the moment it exists;
`TechDocument.FilePath` (`src/Ui/Layout/TechDocument.cs`) is a plain non-nullable `string`, so there is no
`IsScratch`/materialize/offer-dialog path to build or test. `TechDocument : Document, IUndoableDocument,
IActivatableDocument` mirrors `SymbolEditorDocument`'s shape so Ctrl/Cmd+Z routes correctly via the existing
`SetActiveUndoTarget(activeDockable as IUndoableDocument)` seam — no new undo-routing code was needed.
**Save → `TechnologyCache.Invalidate(path)` is what drives L0c's live-refresh seam** — `TechEditorViewModel.
Save()` writes via `TechPersistence.SaveToFile` (`AtomicFile`) then fires `TechSaved(path)`;
`WorkspaceViewModel.OnTechSaved` is the sole handler and its body is exactly `_techCache.Invalidate(path);
Messages.Success(...)` — that one call is what makes every open layout resolved against this path
re-resolve, per L0c's already-working seam (nothing new needed on the refresh side). **Saving with
validation issues present is intentionally allowed** — `SaveCommand` never checks `HasValidationIssues`;
§2.4's rule is that a bad technology warns and still works, and refusing to save a work-in-progress would be
worse than the problem. Layer table: `LayerRowViewModel` — Name/Layer/Datatype/FillOpacity/ZOrder/Purpose are
staged `string` fields committed via code-behind LostFocus/Enter (never bind `TextBox.Text` straight to an
`int`/`double` VM property — kept the string-staged convention from `CellParameterRowViewModel` throughout
for that reason); Visible/Selectable commit immediately via partial `On*Changed`; color swatch reuses
`ColorPickerDialog` (converted to/from `Rgba` in the VM, per the UI firewall) with a `SwatchColor` computed
`Avalonia.Media.Color` property for `Border.Background` binding. **New rows get the next free `(Layer,
Datatype)`** (`NextFreeLayerKey` — lowest layer number strictly greater than every existing one, datatype 0 —
provably collision-free) **, never a duplicate.** **Sorting is display-only** — nothing in this phase sorts
the grid at all (no header-click sort was built), so `Technology.Layers` list order is changed only by the
↑/↓ buttons, which also swap the two rows' `ZOrder` values so the numeric field stays meaningful for anyone
who *does* eyeball the list order. Stackup: `StackupLayerRowViewModel` shows only the fields its `Kind` uses
(εr/tanδ/µr for Dielectric, σ for Conductor/Via) via `IsDielectric`/`IsConductor`/`IsVia` visibility switches;
thickness is a physical dimension parsed/formatted through `LayoutUnits.TryParse`/`Format` in the
technology's `DefaultDisplayUnit` (never a hand-rolled parser); drawing-layer selection is a closed multi-
select (`DrawingLayerCheckItem` per current layer-table entry) — impossible in the UI to reference a layer
that doesn't exist, though the model-level stale reference is deliberately *not* auto-cleaned when a
referenced layer is deleted (`TechValidation`'s existing "unknown drawing layer" check surfaces it instead —
see the gate test). No stackup diagram (that's L6). DRC rules: plain grid, Layer picked from a closed
`LayerOptionItem` combo (a display-label wrapper around `LayerKey`, since a record struct's default
`ToString()` isn't fit for display), Value is the same `LayoutUnits` dimension convention as stackup
thickness; nothing executes these rules until L5b. **`TechEditorView.axaml`** (`src/Ui/Views/Layout/`) is a
`TabControl` (Layers / Stackup / DRC Rules) under a header (name + Undo/Redo/Save) and a validation banner
(`IsVisible={HasValidationIssues}`, never blocks Save); code-behind is two generic dispatchers
(`CommitField`/`OnComboSelectionChanged`) keyed by `Control.Tag` + the DataContext's row-VM type, instead of
one handler per field across three different row VMs. `TechEditorWindow.axaml(.cs)` mirrors
`SymbolEditorWindow` (Ctrl/Cmd+Z/Y `KeyBindings`, since this document does have undo) — note the *generic*
drag-a-tab-out tear-off always uses `CrfHostWindow` regardless (Gate 2's tear-off/re-dock requirement came
free from `TechDocument` implementing `IActivatableDocument`+`IUndoableDocument` and being opened via
`_factory.OpenDocument`); `TechEditorWindow` exists for parity with `LayoutEditorWindow`/`SymbolEditorWindow`
as a per-document-type window class, not because the generic tear-off needs it. **Workspace wiring**:
`WorkspaceViewModel.OpenOrActivateTech` mirrors `OpenOrActivateLayout` exactly (dedupe via `ActivateIfOpen`);
a `.ctech` that fails to load (corrupt JSON, newer `FormatVersion`) is caught and reported via
`Messages.Error` — **no blank document is opened**, matching the pre-existing catch shape `OpenOrActivateLayout`
already had. `OpenNode`'s previously-documented no-op `TechFile` fall-through now has a real arm. **"New
Technology…"** (File menu + tree context menu on Workspace/Library nodes, gated on a workspace being open) —
`NewTechnologyDialog` (name + PCB/MMIC/Empty starter radios + "Set as workspace default" checkbox, mirrors
`NewWorkspaceDialog`'s live-validation idiom) → writes `tech/<name>.ctech` (the *name* is the file stem too —
technology names are filesystem path components like cell names, so plain `NameValidator` is enough without a
separate slug step), optionally sets `CwsFile.DefaultTechRef` + invalidates + `RefreshAllOpenLayoutTech()`,
then opens it. `StarterTechnologies.Empty()` added (bare Technology, no layers/stackup/rules) alongside the
existing `Pcb2Layer()`/`MmicGaAs()`. **Dirty/close/Save-All/`.cws` — full participation**, every seam mirrors
Layout's shape but simplified for the no-scratch case (no offer-dialog, always a direct write):
`HasAnyDirtyWork`, `ConfirmCloseDockable`, `SaveAllDocuments` (both `SingleDoc` and `AllDocs` scopes),
`PromptSaveBeforeClose`, `WriteWorkspaceFile`/`RestoreOpenDocuments` (`kind="tech"`), and
`ITreeActions.IsNodeDirty`/`SaveNodeAsync`/`ProjectTreeNodeViewModel.SaveHeader` (`NodeKind.TechFile` arm,
"Save Technology") all gained a `TechDocument` case. **Tree dirty dot**: `ProjectTreeTool.SetTechFileDirty`
(mirrors `SetCellDirty`, but a technology has no owning cell — the node updated is the `.ctech` file node
itself) is called from a new `WorkspaceViewModel.HookTechFileDirty`, subscribed in `OpenOrActivateTech`
exactly like `HookLayoutCellDirty`; subscribes to the **VM's** `PropertyChanged` for `IsDirty` (not the
document's — `TechDocument.IsDirty`, like `LayoutDocument`/`SymbolEditorDocument`, is a hand-rolled property
that never raises its own `PropertyChanged`, only mutates `Title`). `IsOpenableFile` now includes `NodeKind.TechFile` (a
pre-existing test asserting the pre-L0d "not openable yet" state was updated, not left behind, per gate 2's
intent). Test file: `TechEditorDocumentTests.cs` (dirty mirroring/save/round-trip-clears-dirty; undo/redo of
a color edit, layer add, layer reorder, stackup reorder, and a DRC-rule edit; undo-past-first-edit no-op;
round-trip of one edit per section surviving save+reload with nothing else moved; live validation
surfacing+clearing a duplicate `(Layer,Datatype)`; save-with-issues-present still writes; deleting a
referenced layer surfaces the validation message without touching the stackup's stale reference; `1.6mm` /
`35u` / `100 um` all parse to the correct DBU and redisplay in the default unit; and a "simulated seam" test
that composes `TechnologyCache` + `TechEditorViewModel` + `LayoutEditorViewModel` exactly as
`WorkspaceViewModel` wires them — since `WorkspaceViewModel` itself can't be instantiated headlessly — to
prove gate 3, the L0 gate line, end to end: editing a layer color and saving invalidates the cache and the
open layout's `Technology` becomes a new instance with the edited value) — 18 new tests; 1706 Ui.Tests
total, all green; full solution green (Firewall 4/4, Core 388/388, Engine 461/462 — 1 pre-existing skip,
matching the L0c baseline exactly). Interactive verification was limited to confirming the app boots
cleanly with the new menu items and DataTemplate wired (no XAML binding exceptions on launch) — no visual
driver is available for this desktop app in this environment, matching the L0b/L0c precedent; the
color-picker command and the three dialogs (New Technology…, the tab-switch/tear-off chrome) were not
interactively exercised. **Phase L0 (layout model, document shell, technology plumbing, and the `.ctech`
editor) is now complete. Next: L1 — draw & edit** (the primitive tools, the §3.2 edge-list flattener and
Flatten-to-Polygon, Clipper2 booleans and offsets, selection with overlap cycling, vertex/edge/bulge editing,
clipboard, and fine-grained undo).

Phase L0c — technology plumbing, resolution, and the fallback palette (brief-L0c-technology-plumbing, 2026-07-26) — COMPLETE: Technologies now *reach* a layout — the `tech/` folder, a workspace default, a resolver with a cache and a change seam, a generated fallback palette, and a New Workspace technology choice. **No `.ctech` editor yet** (that's L0d). **`TechnologyResolver`** (`src/Ui/Layout/TechnologyResolver.cs`, framework-free, no `IMessageSink`) — `Resolve(techRef, clayDir, workspaceRootDir, workspaceDefaultTechRef, cache) → TechResolution { Tech, ResolvedPath, Source, Diagnostics }`. Resolution order, exactly: (1) a non-null `LayoutView.TechRef` resolves relative to the `.clay` file's own directory; (2) otherwise the workspace default (`CwsFile.DefaultTechRef`) resolves relative to the workspace root; (3) otherwise `Tech = null`, `Source = None`. **`TechRef = null` means "use the workspace default" — not an error, the normal case.** A `.clay` only stores a `TechRef` when it deliberately deviates from the default; this is why Save-As and cell moves never have to rewrite a relative path. Every failure (missing file, corrupt JSON, newer `FormatVersion`) is non-fatal — `Tech = null` plus one diagnostic, layout still opens/edits; a resolved technology that fails `TechValidation` still resolves, with its problems appended to `Diagnostics`. The resolver never posts — `WorkspaceViewModel.ResolveTechFor` posts every diagnostic via `Messages.Warning`. **`TechnologyCache`** (`src/Ui/Layout/TechnologyCache.cs`) — `Get(absPath)` loads-once-caches-by-path (`OrdinalIgnoreCase`), `Invalidate(absPath)`/`InvalidateAll()`, `event Action<string>? TechnologyChanged`. **Deliberately no `FileSystemWatcher`** (cross-platform watchers need debouncing, behave differently per OS, and fire during our own atomic writes) — invalidation is explicit only: the tree's "Reload Technology" command, and (in L0d) the `.ctech` editor on save. **`FallbackPalette`** (`src/Ui/Layout/FallbackPalette.cs`) — `LayerDef For(LayerKey)`, deterministic via a fixed FNV-1a hash of `(Layer, Datatype)` (never `HashCode.Combine`, which is randomized per process) mapped to a hue at fixed S=0.55/V=0.85; `Name = "L{Layer}/{Datatype}"`, `FillOpacity = 0.35`, `ZOrder = Layer*1000+Datatype`. Golden-value tests pin exact `Rgba` bytes. Two callers: no technology at all (every layer from the palette) and a resolved technology missing one layer (gap-fill only, warn once per unknown layer per load — not yet wired since nothing renders layers until L1/L4). **`.cws`**: `CwsFile.DefaultTechRef` (`string?`, relative to workspace root, `JsonIgnore(WhenWritingNull)`, no `FormatVersion` bump); `CwsTreeViewState.TechFiles` (`bool`, default true). **Project tree**: `NodeKind.TechFile` added; `WorkspaceScanner.BuildFileNode` classifies `.ctech` (the `tech/` folder itself needed no scanner change — it already appears as a `NodeKind.UserFolder` like any non-cell root subfolder). `ProjectTreeNodeViewModel` gained `IsTechFile`, `IsWorkspaceDefaultTech` (resolved live through `ITreeActions.IsWorkspaceDefaultTech`, refreshed by `RefreshDynamicMenuState` on menu-open same as `IsSaveable`), `SetAsWorkspaceDefaultCommand`/`ReloadTechnologyCommand`, `LayersOutline` icon, and `NodeKind.TechFile` added to `CanReveal`. **No `.ctech` double-click editor** — `OpenNode`'s switch has no `TechFile` case, so it falls through to the existing no-op `default` arm; this is intentional (L0d opens the editor), not an oversight — do NOT wire it to a text editor or generic viewer. Two context-menu actions exist without an editor: **"Set as Workspace Default"** writes `DefaultTechRef`, invalidates the cache entry, and calls a new `WorkspaceViewModel.RefreshAllOpenLayoutTech()` (re-resolves every open layout regardless of prior path — the path-matched `OnTechnologyChanged` seam alone would miss a document moving from the old default to the new one). **"Reload Technology"** just calls `TechnologyCache.Invalidate(path)`. A small `CheckCircleOutline` accent icon in the tree row's dirty-bullet column (mutually exclusive with the bullet — Cell vs. TechFile) shows the current default. **`WorkspaceViewModel`** owns one `TechnologyCache` for the lifetime of a workspace: `ResetTechCache()` replaces the instance and re-subscribes `TechnologyChanged → OnTechnologyChanged`, called once from the constructor and again from `NewWorkspace`/`SwitchToWorkspace`/`ResetToBlankShell` (mirrors how `_scratchLayouts` etc. are reset) — a fresh instance rather than just clearing, so no stale subscription from the previous workspace can leak in. `ResolveTechFor(string? techRef, string? clayPath)` reads `CwsFile.DefaultTechRef` fresh off disk (mirrors `WriteWorkspaceFile`'s own re-load-before-merge pattern), calls the resolver, posts diagnostics, returns the result. `OnTechnologyChanged(path)` (the live-refresh seam) re-resolves and calls `LayoutEditorViewModel.ApplyTechResolution` on every open `LayoutDocument` (scratch + path-keyed) whose `ResolvedTechPath` matches; in L0c the only visible effect is the metadata-bar readout — L1/L2 hook the renderer to this same event. **`NewLayoutCommand`** now resolves the workspace default *before* constructing the model and seeds `DisplayUnit`/`SnapDbu` from `tech.DefaultDisplayUnit`/`DefaultSnapDbu` (falling back to L0b's hardcoded `Um`/1000 with no technology); `TechRef` stays null per the convention. `OpenOrActivateLayout` resolves against `model.TechRef` + the file's own path immediately after load. **`LayoutEditorViewModel`** gained `Technology?` (`[ObservableProperty]`), `TechNameText`/`LayerCountText`/`TechSummaryText` ("PCB 2-Layer · 8 layers" / "No technology · fallback colors"), and `ApplyTechResolution(TechResolution)` — deliberately does NOT touch `DisplayUnit`/`SnapDbu` (those are the document's own state once open; re-seeding them on a changed technology would silently discard a user's choice). `LayoutEditorView.axaml` metadata bar gained a "Technology:" pair showing `TechSummaryText`. **New Workspace dialog** (`NewWorkspaceDialog.axaml(.cs)`) gained a Technology `RadioButton` row (PCB (2-layer FR-4) / MMIC (GaAs) / None, PCB checked by default); `NewWorkspaceResult` gained a `NewWorkspaceTechChoice Technology` field. `WorkspaceViewModel.NewWorkspace` — PCB/MMIC create `tech/` + write the starter via `TechPersistence.SaveToFile` (`pcb-2layer.ctech` / `mmic-gaas.ctech`) and set `CwsFile.DefaultTechRef`; **None creates neither the folder nor the reference** — a fully valid workspace that resolves to the fallback palette. Test files: `TechnologyResolverTests.cs` (resolution order, all non-fatal failure modes, validation-failure-still-resolves), `TechnologyCacheTests.cs` (load-once, case-insensitive key, `Invalidate` forces reload, `TechnologyChanged` fires with the path, `InvalidateAll`), `FallbackPaletteTests.cs` (golden `Rgba` values, determinism, non-color fields), plus additions to `WorkspaceScannerTests.cs` (`.ctech` classification, `tech/` folder, `.cws` `DefaultTechRef`/`TechFiles` round-trip incl. absent-on-older-file), `ProjectTreeNodeViewModelTests.cs` (`IsTechFile`, icon, `CanReveal`, no-actions defaults, `TechFiles` filter), and `LayoutEditorDocumentTests.cs` (readout defaults, `ApplyTechResolution` leaves `DisplayUnit`/`SnapDbu` untouched) — 41 new tests; 1688 Ui.Tests total, all green; full solution green (Firewall 4/4, Core 388/388, Engine 461/462 — 1 pre-existing skip). Interactive verification (New Workspace → PCB/MMIC/None → New Layout → metadata bar reads correctly; Set as Workspace Default / Reload Technology from the tree) was **not** performed — no visual driver is available for this desktop app in this environment; build+test is the verification we have, matching the L0b precedent. **Next: L0d** (`.ctech` editor document — layer table, stackup list, DRC-rule grid, live validation surfacing, and firing the change seam on save).

Phase L0b — layout document + editor shell (brief-L0b-layout-document-shell, 2026-07-26) — COMPLETE: A layout view now exists as a first-class, tear-off Dock document — no geometry rendering yet (that's L1), no `.ctech` resolution (L0c). **`LayoutDocument`** (`src/Ui/Layout/LayoutDocument.cs`) clones `SymbolEditorDocument`'s shape minus undo (`Document, IActivatableDocument`; `FilePath`/`IsScratch`/`IsDirty` mirrored from the VM via `PropertyChanged`; `Materialize`/`OnSavedAs` set both `FilePath` and `ViewModel.CurrentLayoutPath` and clear `ViewModel.IsDirty` directly — Layout has no undo stack to organically clear it). **`LayoutEditorViewModel`** (`src/Ui/Layout/LayoutEditorViewModel.cs`) is deliberately thin: wraps a `LayoutView`; `DisplayUnit`/`SnapDbu` write through to the model and set `IsDirty=true` on change but go on **no undo stack** (§1.3/§1.5 — a unit/snap change is a view-preference edit, not a geometry mutation); read-only metadata properties `ResolutionText`/`SnapText`/`ShapeCountText`/`InstanceCountText`/`ExtentText` (bbox union via `LayoutGeometry.BboxOf`, "—" when empty) refresh on `DisplayUnit` change; `SaveLayoutCommand`/`SaveLayoutAsCommand` (`IAsyncRelayCommand<Window?>`) mirror `SymbolEditorViewModel`'s save commands exactly (`PerformSave` → `LayoutPersistence.SaveToFile`, clears dirty, fires `LayoutSaved`, or fires `SaveError` on failure — never crashes). **`LayoutEditorView`** (`src/Ui/Views/Layout/LayoutEditorView.axaml`, namespace `CircuitRF.Ui.Views.Layout`) is a DockPanel: a static placeholder canvas (Material icon + "Layout canvas — drawing tools arrive in L1.") over a bottom metadata bar (resolution / display-unit ComboBox / snap / shape+instance counts / extent) — no `SKPath`, no pan/zoom, no tools. **`LayoutEditorWindow`** (`src/Ui/Views/LayoutEditorWindow.axaml`) is the tear-off host, mirroring `SymbolEditorWindow` (no `KeyBindings` block — nothing to undo). `App.axaml` maps `LayoutDocument → LayoutEditorView` via `xmlns:lay`/`xmlns:layv`. **Workspace lifecycle** (`WorkspaceViewModel`) mirrors the scratch-symbol path throughout: `_scratchLayouts` list (cleared alongside `_scratchSymbols` in `NewWorkspace`/`SwitchToWorkspace`/`ResetToBlankShell`); `NewLayoutCommand` (File → New Layout, always enabled, `NextScratchLayoutTitle` → `Untitled-Layout-N`); `OpenLayoutFileCommand` (File → Open Layout…) + `OpenOrActivateLayout` (dedup via `_openDocsByPath`); `SaveSingleLayoutDocument`/`SaveMaterializedLayoutDoc`/`SaveScratchLayout`/`SaveScratchLayoutToCell`/`SaveScratchLayoutAsFile` clone the symbol save-target-offer flow exactly (`CellFolder.SubFolderPath(cellDir, ViewType.Layout)`); `HasAnyDirtyWork`/`PromptSaveBeforeClose`/`ConfirmCloseDockable`/`OnDockableClosed`/`SaveAllDocuments` all gained `LayoutDocument` branches; `WriteWorkspaceFile`/`RestoreOpenDocuments` gained `kind="layout"`. **Remove/Rename Cell needed zero new code** — both already force-close via a generic `_openDocsByPath` scan keyed by path-under-cell-dir (`IsPathOrUnder`), so once `OpenOrActivateLayout` registers into that dictionary, a `LayoutDocument` under a removed/renamed cell is force-closed automatically. **Project tree:** `OpenNode`'s `.clay` case (previously a documented deferred no-op) now dispatches to `OpenOrActivateLayout`; `ITreeActions.OpenCellLayout` + `OpenCellPrimary` generalized to a 3-way switch; `ProjectTreeNodeViewModel` gained `CanOpenLayout`/`OpenLayoutCommand` (mirroring `CanOpenSchematic`/`OpenSchematicCommand`) and `.clay` was added to `IsOpenableFile`/`IsRemovableFile`/`SaveHeader` ("Save Layout"); `IsCellDirty`/`IsNodeDirty`/`SaveNodeAsync`/`SaveCellViewsAsync` all gained a `LayoutDocument` arm alongside the existing `SymbolEditorDocument` one, via a new `HookLayoutCellDirty`/`RefreshCellDirtyForLayout` pair mirroring `HookSymbolCellDirty`/`RefreshCellDirtyForSymbol`. **`WorkspaceScanner` needed NO change** — `BuildCellNode` already iterates `Enum.GetValues<ViewType>()`, so a `.clay` under `<cell>/layout/` was already emitted as a `NodeKind.ViewFile` under a `NodeKind.CellViewFolder` with primacy resolved through the existing `CellFolder.ResolvePrimary(cellDir, ViewType.Layout)` — **do not go looking for (or add) a layout-specific `NodeKind`.** Menu surface: File → New Layout / Open Layout… added next to Open Symbol in both the in-window `Menu` and the macOS `NativeMenu` (menu-only, no accelerator — none was free). Test files: `LayoutEditorDocumentTests.cs` (dirty mirroring, display-unit-is-serialization-no-op, save/round-trip, metadata bar), `WorkspaceScannerTests.cs` (`Scan_Cell_SoleLayout_MarkedPrimary`), `ProjectTreeNodeViewModelTests.cs` (`.clay` added to the `IsOpenableFile`/`IsRemovableFile` theories) + new `ProjectTreeNodeViewModelLayoutTests` (`CanOpenLayout`) — 16 new tests; 1647 Ui.Tests total, all green. Manually verified the app boots cleanly with the new menu items and DataTemplate wired (no XAML binding exceptions on launch); full interactive tear-off/drag/save-dialog verification was not done (no visual driver available for this desktop app in this environment). **Next: L0c** (`.ctech` editor document, `tech/` folder + `.cws` default technology, technology resolution with the missing-tech fallback, layer-color live-refresh seam).

Phase L0a — layout model, units, technology, persistence (brief-L0a-layout-model-persistence, 2026-07-26) — COMPLETE: New framework-free area `src/Ui/Layout/` (namespace `CircuitRF.Ui.Layout`), mirroring how `DataDisplay/` was carved out of `Schematic/` — no Avalonia, no SkiaSharp, no reference to any `Schematic` type (patterns borrowed, not types). **Units (`LayoutUnits.cs`):** all layout coordinates are `long` DBU; `DbuPerMicron` defaults to 1000 (1 DBU = 1 nm); `ToDbu`/`FromDbu` compute in `decimal` (never `double`) so `1 mil = 25400 DBU`, `1 mm = 1_000_000 DBU`, `1 inch = 25_400_000 DBU` are exact; `TryParse` accepts a bare number or a unit-suffixed string (`nm`/`u`|`um`|`µm`/`mm`/`mil`/`in`|`inch`, case-insensitive, `1e3nm` etc.); `Format` trims trailing zeros. **Model (`LayoutModel.cs`):** `LayerKey(Layer,Datatype)`; one edge-list vocabulary (`LayoutEdge`: Line/Arc-by-bulge/Cubic) serves both `CurveShape` (closed) and `PathShape` (open centerline+width+end-style); `RectShape`/`PolygonShape`/`RoundedRectShape`/`CircleShape`/`ViaShape`/`LabelShape` round out the shape set, every shape carrying `LayerKey` + nullable `Net` (unpopulated until L5); `LayoutInstance` (translate/rotate/mirror/mag + optional rows×cols array = GDSII AREF); `LayoutView` is the container (DbuPerMicron/DisplayUnit/SnapDbu/AngleMode/TechRef/Shapes/Instances). **Geometry (`LayoutGeometry.cs`):** `Bbox` (Union/Contains/Intersects) + `BboxOf(shape)` — exact for Rect/Poly/RRect/Circle, exact arc extremes (via `LayoutArc.ArcExtremes`, bulge↔center/radius/angle conversion) for Arc edges, conservative convex-hull-of-control-points for Cubic edges, Path grows by Width/2 plus an end-style-aware cap (Flush/Round/Square/Extended). Bounding boxes only — the flattener and `ToClipperPaths` are L1. **Technology (`TechModel.cs`, `StarterTechnologies.cs`):** `LayerDef` (reuses the existing framework-free `CircuitRF.Ui.Theming.Rgba` — layer colors are literal, not a `ColorRole`), `Stackup`/`StackupLayer`, `DrcRule`; two starter techs (`Pcb2Layer()`, `MmicGaAs()`) match docs/design/layout-view.md §2.4's table exactly and both pass `TechValidation.Validate` clean. **Persistence (`LayoutPersistence.cs` / `TechPersistence.cs`):** `.clay`/`.ctech` format_version 1, cloning `SymbolPersistence`'s conventions (`System.Text.Json`, `WriteIndented`, `PropertyNameCaseInsensitive`, `JsonStringEnumConverter`, PascalCase, no naming policy, `$type` polymorphic discriminator on `LayoutShape`, `Id` never persisted, reject-on-newer-format-version, written through `AtomicFile`). `LoadFromFile` sniffs the gzip magic bytes (`0x1F 0x8B`) and transparently decompresses — writers stay plain-JSON in v1, so a future gzip writer is a write-side-only change with no format bump (shared via internal `GzipTextFile`). A shorter-than-expected `Edges` list is padded with `Line` on load. **`TechValidation.cs`:** never throws; flags duplicate `LayerKey`, a `DrawingLayers` entry naming an unknown layer, non-positive conductor σ, sub-unity dielectric εr, an unknown-layer DRC rule, and non-positive stackup thickness. **`LayoutScaling.cs`:** `TryChangeResolution` — refinement (integer multiply) always succeeds; coarsening (integer divide) pre-scans every coordinate (shape geometry, path width, radii, corner radii, `FlattenTolDbu`, Cubic control points, instance position/pitch, `SnapDbu`) without mutating and only commits when the whole design divides exactly, else returns a bounded (~20 + count) named-offender list with the layout completely unmutated; non-integer ratios rejected. Test files: `LayoutUnitsTests.cs`, `LayoutModelTests` (covered inline via persistence/geometry tests), `LayoutPersistenceTests.cs`, `TechPersistenceTests.cs`, `LayoutGeometryTests.cs`, `LayoutScalingTests.cs` — 62 new tests, all headless (no Avalonia runtime). Scope fence held: no rendering, no document/editor/view-model, no project-tree/`.cws` integration, no flattener/Clipper2, no GDSII/DXF/Gerber, no DRC execution — see `docs/sonnet-briefs/brief-L0a-layout-model-persistence.md`. **Next: L0b** (layout document + editor shell, `.ctech` editor, `tech/` folder + project-tree node).

HB spectrum stage 2 — harmonic axis carries orders; Trace reconstructs frequency (brief-hb-spectrum-2-order-axis, 2026-06-23) — COMPLETE: After the engine change (stage 2, Part A), the single-tone `harmonic` axis stores integer orders (unit `""`), never Hz values. The owner (`PlotInspectorViewModel`) resolves the per-X fundamental from `ToneFreqs` via `GetToneFreqsCube` + `ResolveFundamentalByX` and injects it via `Trace.SetSpectrumFundamentals(f0ByX)` immediately before `SetCubeData`/`SetFamilyData`. The Trace reconstructs harmonic frequency for: geometry (`BuildCubePath` + `SetFamilyData` X positions = `order × f0 × freqScale`), marker readouts (`BuildCubeMarkerBoxLines` emits `harmonic={order}` + optional `freq=… GHz`), stem info (`GetStemFreqString` uses `_f0ByX`), and the X-axis label (`Plot.XLabel` treats a harmonic-named axis as frequency). `HarmonicOrderOf` removed (order is now the axis value directly). The harmonic axis is matched by **name**, not by being a frequency unit. 5 UI gate tests (`HbSpectrumStage2Tests.cs`). T3/T5.B in `MarkerSweepFreqLabelTests.cs` updated to use integer orders + `SetSpectrumFundamentals`. **Follow-ups:** two-tone `mixIndex` → same pattern; physical-frequency column in Table.

Marker readout: freq-var sweep axis uses its own name (brief-marker-sweep-freq-label, 2026-06-23) — COMPLETE: The `freq=…`/`harmonic=…` display in `Trace.BuildCubeMarkerBoxLines` is now specific to the **HB harmonic axis** (matched by `HarmonicAxisName == "harmonic"`). Any other frequency-unit axis — notably a parametric sweep over a frequency variable like `RFfreq` — is labelled with its own axis/variable name and shows no `harmonic=` row. 5 new tests in `MarkerSweepFreqLabelTests.cs`. Build 0W/0E; 1403 Ui.Tests pass.

Marker + X-axis per-swept-variable units (brief-sweep-axis-marker-units Part C, 2026-06-22) — COMPLETE: Marker info boxes and X-axis labels now show units for all sweep-axis types. The family `else` branch in `Trace.BuildCubeMarkerBoxLines` appends `FamilyAxisUnit` (e.g. `Vgs=1 V`). `PlotInspectorViewModel` already passes `fAxis.Unit` to `SetFamilyData` — the fix is upstream: `ParametricSweepEngine` now tags the axis with `Units.BaseUnit(origVar.Unit)`, which `fAxis.Unit` picks up. The frequency family branch (→ `freq=2 GHz`) and the X-axis non-frequency branch (→ `{name}={val} {unit}`) were already correct. Gate tests: `SweepAxisMarkerUnitTests.cs` (T1 freq-X, T2 non-freq-X, T3 family). Build 0W/0E; 1398 Ui.Tests pass.

Parametric-sweep range units — UI layer (brief-sweep-range-units, 2026-06-22) — COMPLETE: `SweepAxisRowViewModel` now applies a unit multiplier so sweeping a GHz frequency VAR over `1 … 5` (unit inherited or explicit) materializes `[1e9 … 5e9]`. `EffectiveUnit` = `Unit` if user set one; else the swept VAR's declared unit (`GetVarUnit` scans `model.Components` for `SymbolKind.Var` parameters). `BuildValues` and `BuildSpec` multiply Start/Stop by `Units.Scale(EffectiveUnit) ?? 1.0`; Step is scaled only in StepSize mode; PointCount count is not scaled. `BuildSpec` stores **coefficients** (unscaled) + `EffectiveUnit` on `SweepSpec` — Part A re-applies the scale at PSA construction. `FromPsa` restores `Unit = spec.Unit`. Note: var-unit-wins does NOT apply here — the chosen field/inherited unit always governs (unlike the freq-preview helper). `AnalysisSerialization` adds `PsaUnit` (string?) to `CschAnalysis`; `ToDto` writes it when non-empty; `FromDto` passes `dto.PsaUnit ?? ""` to the `SweepSpec` ctor. Absent `PsaUnit` → base (back-compat). 5 gate tests: 2 in `SweepRowUnitTests.cs` (T6: defaultsUnitFromVar + override; T7: round-trip) + 1 in `AnalysisSerializationTests.cs` (T8: ToDto→FromDto). Build 0W/0E; 2156 total tests pass.

Analysis-editor frequency preview (brief-analysis-freq-preview-units, 2026-06-22) — COMPLETE: Analysis-editor frequency previews now mirror the engine's var-unit-wins rule via `AnalysisPreviewHelper.ComputeFreqPreview(coeff, fieldUnit, model)`. If the coefficient expression references a variable that declares its own frequency unit (found via `LookupParamUnit` scanning `model.Components`), that variable's unit overrides the field-unit dropdown; otherwise the field unit applies. `DesignScope.Build` still resolves the raw numeric value (units stripped — the existing limitation), and `FreqUnit.Multiplier` applies the winning unit. `FreqUnitHelper.ToHzExpr` retired — zero remaining callers (deleted in Part D). Non-frequency parameter previews unchanged (raw numeric, units deferred). 7 gate tests in `FreqExprUnitTests.cs` (Tests 1–7 of the preview brief). Build 0W/0E; 2156 total tests pass.

SDD weighting editor (brief-sdd-weighting-editor, 2026-06-19) — COMPLETE (Option A — minimal): `ParameterRowViewModel.CommitName` now validates SDD equation names inline when `_ownerSymbol is SymbolKind.Sdd or SymbolKind.FetSdd`. Accepts `I[p]` (p≥1), `I[p,w]` (p≥1, w≥0), `Q[p]` (p≥1), `H[w]` (w≥2); rejects H[0]/H[1] ("built-in"), malformed H[x]/H[] ("integer weight ≥ 2"), and everything else ("Not a valid SDD equation name"). **`TryValidateSddName(string, out string)`** is `internal static` for direct unit testing (no Avalonia runtime needed). **`NameWatermark`** property emits `"I[p,w] · Q[p] · H[w]"` for SDD/FetSdd owners and `""` for all others; bound to the name TextBox via `PlaceholderText` in `ParameterEditorView.axaml`. Regexes duplicated from `ComponentModelFactory` private fields (comment points back). No Core/Engine changes. 26 gate tests in `SddEquationNameValidationTests.cs`. Build 0W/0E; 1953 total tests pass.

Project Tree UX (brief-projecttree-ux, 2026-06-19) — COMPLETE: 9 independent UX items. **Item 1 (recent workspaces):** `ProjectTreeTool.RecentEntry` sealed record; `RecentWorkspaces` ObservableCollection + `HasRecentWorkspaces`; `RefreshRecent()` (called from `SetActions`/`ClearWorkspace`); `OpenRecentCommand` + `ClearRecentCommand`; `GetRecentWorkspaces()` on `ITreeActions`/`WorkspaceViewModel` (skips missing dirs); AXAML shows recent-workspaces panel in place of "No workspace open." with link-button style (`Button.pt-link`, accent foreground, Hand cursor). **Item 2 (Open Recent cascade on workspace-name context menu):** workspace-name TextBlock has its own ContextMenu with Open Workspace / Open Recent submenu / Close Workspace / Reveal items. **Item 3 (workspace-name context menu):** added via `<TextBlock.ContextMenu>` in AXAML (see Item 2). **Item 4 (dynamic menu state):** `<ContextMenu Opening="OnNodeContextMenuOpening">` calls `vm.RefreshDynamicMenuState()` to re-fire INPC for `IsSaveable`/`SaveHeader` so stale CanExecute never shows. **Item 5 (file-info Properties panel):** `FileInfoInspectorViewModel` (Name, SizeText, ModifiedText); `PropertiesTool` gains 5th context `IsFileInfoActive`/`FileInfoVm`; PropertiesView shows a 2-row grid (Size / Modified); `WorkspaceViewModel.OnTreeSelectionChanged` sets file-info context for KnownFile leaf + OtherFile nodes, clears it for other node types. **Item 6 (Duplicate Cell):** `DuplicateCellCommand` (AsyncRelayCommand, CanExecute=IsCell) in node VM; `DuplicateCellAsync` in `WorkspaceViewModel` — prompts name, validates, copies folder, renames primary schematic+symbol to `<newName>.csch/.csym` (skips if target name collides with non-primary), updates `.ccell`, refreshes tree. **Item 7 (Rename Cell):** `RenameCellCommand`; `RenameCellAsync` — `RenameCellDialog` (name TextBox + "Rename primary files to match" checkbox, default checked), validates (NameValidator + no-other-cell collision + no target-file-collision), force-closes open docs, moves directory, calls `CellUsageScanner.RewriteCellReferences`, renames primaries when checkbox is on, updates `.ccell`, refreshes tree. `CellUsageScanner.RewriteCellReferences` (new) parses JSON with `JsonNode`, matches last path segment of `"CellRef"` (PascalCase), rewrites and re-writes file. **Item 8 (Open Cell command):** "Open Cell" context menu item between "Open Schematic" and "Open Symbol" opens the primary schematic. **Item 9 (Close Workspace):** `CloseWorkspaceCommand` (CanExecute = `CurrentWorkspacePath is not null`); `ResetToBlankShell()` — force-closes all docs, clears registries, resets layout, re-wires tools; menu entry in File menu + NativeMenu. 14 gate tests: 6 in `ProjectTreeUxTests.cs` (Items 5/6), 4 in `CellUsageScannerTests.cs` (Item 7 rewrite). Build 0W/0E; 1887 total tests pass.

Table improvements (brief-table-improvements, 2026-06-19) — COMPLETE: 6 independent items. **Item 1 (per-group X-column resize):** `Trace.XColumnWidth` (0 = fall back to `plot.ColumnWidth`) added; `TableRenderer.BuildLayout` and `TotalColumnWidth` read `anchor.XColumnWidth > 0 ? anchor.XColumnWidth : plot.ColumnWidth` for each XAxis column; `PlotControl` resize-start, drag-move, and double-tap auto-fit all write `XColumnWidth` on the anchor trace instead of `plot.ColumnWidth`; round-tripped in `TraceConfig.XColumnWidth` + `BuildTraceConfig`/`LoadPlotContainerConfigAsync`. **Item 2 (sort-arrow bleed):** `DrawHeaderRow` clamps `triCx = Math.Min(triCx, colX + colW - triSize*0.5 - CellBorderWidth)` so the arrow centre stays inside the column. **Item 3 (wheel-zoom vs. scroll):** `TableRenderer.CanScroll(plot, canvasSize, zoomLevel)` added; `OnPointerWheel` returns without setting `e.Handled` when `CanScroll` is false, so the event bubbles to the parent for zoom. **Item 4 (family / rank-2 trace):** `FamilyCurve` gains `RawComplex`/`RawReal`; `SetFamilyData` stores the raw arrays; `TableColumn` gains `FamilyCurveIndex` (−1 = normal) and `IsNodeAxis`; `BuildColumns` branches on `trace.IsFamily` to emit one TraceValue column per curve (header: `baseShorthand @ Vgs=…`) and an explicit `trace.IsCubeBound-but-no-CubeXValues` branch for blank/invalid traces (no fall-through to legacy Data branch); `FormatFamilyCellAt` + `Trace.FormatFamilyCell` handle cell formatting; `DrawHeaderRow` uses `col.Header` directly for family columns. **Item 5 (Copy Table Data):** comes for free from Item 4 — `BuildCopyGrid` calls `BuildColumns` + `FormatColumnCell`. **Item 6 (node axis integer):** `TableColumn.IsNodeAxis` set when `axisName == "node"` (OrdinalIgnoreCase); `FormatColumnCell` XAxis branch returns `((long)Math.Round(xVal)).ToString(InvariantCulture)` before the `IsFreqUnit` check. 11 gate tests in `TableImprovementsTests` (Items 1–6 in `TableCubeTraceTests.cs`). Build 0W/0E; 1876 total tests pass.

Schematic housecleaning (brief-schematic-housecleaning, 2026-06-19) — COMPLETE: 6 independent items. **Item 1 (paste Num dedup):** `SchematicPasteCommand.ResolveNums` (new) runs after `ResolveNames`; builds the used-Num set from existing Term/P1Tone in the model, then for each pasted Term/P1Tone whose `Num` collides, assigns the lowest free positive integer (live set updated between batch-pasted components so intra-batch collisions are also prevented). **Item 3 (Save As title):** `SchematicDocument.OnSavedAs(filePath, cellName)` (new) sets `FilePath`, `_baseTitle`, `Id`, calls `UpdateTitle()` — unlike `Materialize` it may be called repeatedly. `WorkspaceViewModel.SaveLooseSchematic` now targets the active materialized doc when no dirty scratch doc exists; `SaveLooseToWorkspace`/`SaveLoosePlainFile` both branch on `IsScratch` to call `Materialize` vs `OnSavedAs` and update `_openDocsByPath` (remove old path, add new). **Item 4 (toolbar glyphs + Pin):** Wire button is now a `<Path Data="M 1,6 L 15,10"/>` (matches Symbol Editor line glyph); Ground and Term buttons use `<ctrl:PaletteGlyphControl>` to render the actual library symbol; new Pin button (`PlacePinBtn`) added after Term with `OnPlacePin` handler that arms `SymbolKind.Pin` placement. **Item 6 (SNP label position):** `LabelBaseYFor`/`LabelRowGeometry` gain optional `double? glyphHalfH` parameter; the SNP branch uses it when provided instead of hardcoded `SnpBodyRect(Standard, Loose)`. Updated at all callsites: `SchematicRenderer.DrawLabels` → `c.GlyphBbMaxY - c.Y`; `SchematicModelBuilder.BuildComponent` → `gMaxY - cy`; `EditableSchematic.ToRenderComponent` → `glyphMaxY - Y`; `SchematicHitTest.TestComponentLabels` → `comp.ComputeGlyphBb().MaxY - comp.Y` (SnP only); `SchematicView.axaml.cs ComputeComponentLabelScreen` → `GlyphHalfH` on `ComponentLabelAnchor`. 17 gate tests in `OhmLowercaseTests.cs` (Core), `P1ToneLintTests.cs` (Core), `SchematicHousecleaningTests.cs` (Ui). Build 0W/0E; 1220 Ui.Tests pass.

Data Display fixes (brief-datadisplay-fixes, 2026-06-19) — COMPLETE: 3 bugs + 3 toolbar enhancements. **Bug 1 (Ctrl+S):** `SaveAllDocuments` now checks for an active `DataDisplayDocument` first and dispatches to `SaveDataDisplayDoc(activeDisplay, window)`, consistent with schematic/symbol Ctrl+S behavior; the data display's `UserControl.KeyBindings` also binds `Ctrl/Meta+S → SaveDataDisplayCommand` for focus-local routing. **Bug 2 (Copy/Paste):** 2a — `PerformCopy` now serializes live plots via `BuildPlotContainerConfig` into a `DataDisplayConfig.Plots` JSON and writes it via the `_setClipboardTextAction` delegate; 2b — `_setClipboardDataAction` replaced by `_setClipboardTextAction : Func<string,Task>` + `SetSetClipboardTextAction`; both text clipboard delegates wired in `DataDisplayView.axaml.cs OnLoaded`; `CheckPasteStateAsync()` called from `OnAttachedToVisualTree`; 2c — `LoadPlotContainerConfigAsync` now returns `PlotContainerViewModel`; `PasteFromConfigAsync` rewritten to call the loader (handles cube-bound traces, resolves SourceRefs, dedupes marker names) instead of duplicating stale network-only logic; 2d — `Ctrl/Meta+C/X/V` added to `DataDisplayView.axaml` KeyBindings; `InvokeClipboardAsync` in `WorkspaceViewModel` routes Cut/Copy/Paste to the active `DataDisplayDocument` via new public `InvokeCutAsync`/`InvokeCopyAsync`/`InvokePasteAsync` methods. **Bug 3 (tab title):** `DisplayWindowViewModel.SaveAllAsync` raises `ConfigPathSaved : Action<string>` after `CaptureBaseline`; `DataDisplayDocument` ctor subscribes to `vm.Window.ConfigPathSaved += OnSavedToPath`; `OnSavedToPath` updates `FilePath`, `_baseTitle`, `Id`, `Title`; dead `Materialize` stub now delegates to `OnSavedToPath`. **Enhancement 4:** `AddPlot()` now uses `PlotType.Rect`; `AddSmithPlotCommand`, `AddPolarPlotCommand`, `AddTablePlotCommand` added. **Enhancement 5:** `LoadRunResultsCommand` toolbar button removed. **Enhancement 6:** Datasource ComboBox moved to first toolbar position with separator; final order: `[Datasource combo] | [Rect][Smith][Polar][Table] | [NewTab] | [Zoom×4] | [Undo][Redo] | [Save][Open] | [Export]`. 11 gate tests in `DataDisplayFixesTests.cs` (T1–T11). Build 0W/0E; 1205 Ui.Tests pass.

Data Exporter (brief-data-exporter, 2026-06-19) — COMPLETE: Modal `DataExporterDialog` (File → Export… + toolbar button on Data Display) exports `results/<schematic>/run.npy` to `.npy`, `.mat`, tab-delimited `.txt`, or Touchstone. **VM** (`DataExporterViewModel`, `src/Ui/DataDisplay/ViewModels/`): enumerates `results/*/run.npy` into `AvailableSchematicNames`; optional `preselectSchematic` selects the active run; mode-sensitive `IncludeRows` (groups for npy/mat/tsv, only groups with an `S` cube for Touchstone); `MeasurementsAvailable`/`IncludeMeasurements` gate the `measurements` group; `SweepSliceRows` and `AllSweepCheck` for Touchstone slice selection; `Z0Ohms`, `Digits`, `DigitFormat`, `MatrixFormat` (uses `RfCore.MatrixFormat`); `CanExport` guards the Export button; `ExportDataSet(path)` dispatches via `DataSetSubset.SelectGroups`→`DataSetExporter.Export`; `ExportTouchstone(baseNoSuffix)` dispatches via `TouchstoneExporter.Export`. `SuggestedFileName` returns `<schematic>.<ext>`. **Dialog** (`DataExporterDialog.axaml/.cs`, `src/Ui/Views/Dialogs/`): format segmented-buttons wired in code-behind (no data-bindings for ToggleButton state); `StorageProvider` file-picking lives entirely in code-behind per UI firewall; `ShowAsync(Window? owner, …)` walks `ApplicationLifetime.Windows` for a fallback owner. **DisplayWindowViewModel**: `SetExportDataAction(Func<Task>)` + `[RelayCommand] ExportData()` action-seam. **DataDisplayView**: `DoExportDataAsync()` infers preselect schematic from `SelectedDataSourceAbs`; export button after the datasource combo. **WorkspaceViewModel**: `[RelayCommand(CanExecute=CanExportData)] ExportData()` + `CanExportData()` gates on `GetResultsRoot() != null`; `ExportDataCommand.NotifyCanExecuteChanged()` called in `OnCurrentWorkspacePathChanged`. **WorkspaceWindow.axaml**: File → Export… NativeMenuItem + in-window MenuItem with `DatabaseExportOutline` icon. 10 gate tests in `DataExporterViewModelTests.cs` (no-root, enumeration, preselect, default mode, Touchstone-no-S, npy/mat/tsv write, snp write, suggested filename). Build 0W/0E.

Current-picker branch filter (brief-unify-i-cube-engine, 2026-06-18) — SUPERSEDED by unified I cube: The old per-branch `I:*` cube filter is gone. The engine now emits a single `I` cube with a labeled `branch` axis; `__ProbeBranches` marks the IProbe subset. `TraceRowViewModel.ShowAllBranchesToggleVisible` always returns `false`; `ShowAll` is driven solely by `ShowAllNodesToggleVisible`. `CurrentPickerBranchFilterTests.cs` deleted (tested superseded behavior). See `src/Engine/HarmonicBalance/CLAUDE.md` §C2.

MEAS component (brief-meas-component, 2026-06-18) — COMPLETE: `SymbolKind.Meas` is an **annotation component** (no ports, no instance emission) whose `name = expression` parameter rows route to `TestBench.Measurements` at the **top testbench level only** — MEAS inside a sub-cell raises a conflict and is ignored. It reuses the VAR multi-line text editor (`VarEditorViewModel` + `VarEditorDialog`); `VarEditorViewModel.SetTarget` now stores `_compKind` (VAR vs MEAS) and exposes `PanelTitle`, `DialogTitle`, `AddRowLabel` to differentiate labels. `GenerateUniqueName` produces `Meas{n}` for MEAS. The glyph is a port-less box with two `=` bars (`BuildMeas()` in `BuiltInSymbols.cs`). Registry: `[SymbolKind.Meas] = new("MEAS","MEAS", …, IsCommon:true)`, `EngineReference` sentinel `"MEAS"`, `DefaultParameters` → `[]`, `TryParseCode("MEAS")` → `SymbolKind.Meas`, `UserParamTemplate` → `Meas{0}`. `NetExtractor.ExtractModel` was extended: skip MEAS in the instance loop, collect MEAS rows as `Measurement` objects (first-definition-wins on dup), return as 4th tuple element; `Extract` calls `tb.Measurements.AddRange(topMeas)` (replaces vestigial `model.Measurements` loop). `EmitCellInstance` destructures the new 4th element and warns on non-empty sub-cell MEAS. Measurements are evaluated post-run by `MeasurementEvaluator` into the run's **`measurements` group** (one grouped `run.npy`); referenced in the Data Display by **bare name** (analysis cubes stay qualified `Analysis.Cube`). `DataSet.MeasurementsGroup = "measurements"` enables bare resolution — see `src/Core/Data/CLAUDE.md`. 5 gate tests in `MeasComponentTests.cs` (no-instance, rows-become-measurements, duplicate-first-kept, inside-cell-ignored, csch-round-trip). Build 0W/0E.

Analyses toolbar run retain (brief-analyses-toolbar-run-retain, 2026-06-17) — COMPLETE: The Analyses panel toolbar is restructured into two rows: Row 0 = Run ▶ button + schematic name; Row 1 = button toolbar (same buttons, no HeaderLabel). The panel retains `_lastActiveSchematicDoc` in `WorkspaceViewModel` — only schematic docs update it; focusing a data display / symbol / cell tab no longer blanks the panel. `AnalysesListViewModel` gains `public event Action? RunRequested` and `[RelayCommand(CanExecute = nameof(HasActiveSchematic))] private void Run() => RunRequested?.Invoke();`; `RefreshCommandStates` calls `RunCommand.NotifyCanExecuteChanged()`. `WorkspaceViewModel.RunAnalysis` falls back to `_lastActiveSchematicDoc` when no schematic is the active dockable; body extracted to `RunSchematicDocAsync`. `WireAnalysesRun()` subscribes `OnAnalysesRunRequested` (fires `_ = RunSchematicDocAsync(doc)`) to `ListVm.RunRequested`; called after each `OnDocumentDockPropertyChanged` re-subscription (ctor, NewWorkspace, SwitchToWorkspace). `_lastActiveSchematicDoc = null` on workspace clear; `OnDockableClosed` blanks panel if the closed doc is the retained one. 4 gate tests in `AnalysesToolbarRunRetainTests.cs`. Build 0W/0E; 1678 total tests pass.

Sweep card reorder (brief-sweep-card-reorder, 2026-06-17) — COMPLETE: Up/Down on a sweep card reorders it within its chain (`ReorderSweepInChainCommand`, Up=inner/Down=outer); base selection still moves the whole chain (`MoveAnalysisChainCommand`). `ReorderSweepInChainCommand` locates the chain block, swaps the two adjacent sweeps in a local sequence, then relinks `InnerAnalysisName` bottom-up and writes back; snapshots old instances for Undo. `AnalysesListViewModel.MoveUp/MoveDown` branch on whether `SelectedRow.Analysis is ParametricSweepAnalysis`. `CanMoveUp` for a sweep returns true only when the slot above it is also a sweep (i.e. there is an inner sibling); `CanMoveDown` returns true only when the slot below is also a sweep. 12 new gate tests in `SweepCardReorderTests.cs`; existing `CanMoveUp_LoneSweepRow_ReturnsFalse` test updated to reflect new semantics. Build 0W/0E; 1674 total tests pass.

Analyses copy/paste chains (brief-analyses-copy-paste-chains, 2026-06-17) — COMPLETE: `CloneAnalysis` now handles `ParametricSweepAnalysis` (both Spec and Values forms) and takes an optional `newInnerName` parameter; callers can re-target the inner link. `ExpandSelectionToChains` (internal) on `AnalysesListViewModel` walks `sweepsByInner` outward from each selected base to include its entire sweep chain in model order; `Copy` calls it before serializing. `PasteAnalysesCommand` rewrites `ResolveNames` as a two-pass algorithm: Pass 1 computes collision-free names and builds an old→new `remap` dict; Pass 2 clones each analysis with both its new name and a remapped `InnerAnalysisName` (inner in paste set → remapped name; lone sweep → `retargetInner ?? original`). `Paste` in `AnalysesListViewModel` passes `retargetInner: SelectedRow?.Analysis.Name`. 8 gate tests in `AnalysesCopyPasteChainTests.cs`. Build 0W/0E; 1662 total tests pass.

Analyses list grouping (brief-analyses-list-grouping, 2026-06-17) — COMPLETE: The Analyses list renders `ParametricSweepAnalysis` members indented (20 px left margin) under their base simulation with a live `"N pts: a…b"` summary and `"SW"` type badge. `AnalysisRowViewModel` gains `IsSweep` (bool), `Name` returns `psa.SweepVarName` for sweeps (the internal `Analysis.Name` is unchanged), `TypeLabel` gains the `ParametricSweepAnalysis => "SW"` arm, `ComputeSummary` gains `FormatSweepSummary`+`FmtNum`. `BoolToIndentConverter` (new, `namespace CircuitRF.Ui.ViewModels`) maps `true → Thickness(20,0,0,0)`; `AnalysesListView.axaml` adds `<UserControl.Resources>` with it and binds `Border.Margin` via `IndentConv`. `MoveAnalysisChainCommand` (new, `src/Ui/Commands/Analysis/`) moves the whole chain (base + its contiguous sweeps) past the adjacent chain; `MoveRange` rotates the block into the target slot. `AnalysesListViewModel.MoveUp/Down` switch to `MoveAnalysisChainCommand`; `CanMoveUp/Down` recompute the block start/end before checking boundaries. 15 gate tests in `AnalysesListGroupingTests.cs`. Build 0W/0E; 1652 total tests pass.

Sweep revamp Stage 3 (brief-sweep-revamp-3-editor, 2026-06-17) — COMPLETE: The analysis editor is the unified model — base type + ordered `SweepAxes` with per-axis `Enabled` and ↑/↓ reorder. **Critical fix:** `BuildAnalyses` now writes the dialog's `Enabled` to the base and each row's `Enabled` to its own sweep (the old `!hasSweeps && Enabled` / `isLast && Enabled` hack produced dead chains under Stage 2's collapse logic and is gone). `SweepAxes[0]` = innermost = plot X axis; rows below are outer (slower) sweeps. `SweepAxisRowViewModel` gained `[ObservableProperty] bool Enabled = true`; `FromPsa` restores it from `psa.Enabled`. `AnalysisEditorViewModel` gained `MoveSweepAxisUpCommand`/`MoveSweepAxisDownCommand` (mirror `RemoveSweepAxisCommand`). Edit-restore: `_enabled = inner.Enabled` (was `sweepChain[^1].Enabled`). `SweepAxisRowView.axaml` Row 1 gains a `CheckBox` (Enabled, left) and `↑`/`↓`/`×` button group (right, `sw-icon` style); code-behind wires two new click handlers same as `OnRemoveClick`. `AnalysisEditorDialog.axaml` shows a one-line order hint above the axes. 8 new gate tests in `SweepRevamp3EditorTests.cs`; 1637 total tests pass. Build 0W/0E. **Parametric-sweep revamp (Stages 1–3) COMPLETE.**

Phase 7.3b (family role, 2026-06-17) — COMPLETE: A single `Trace` with a `FamilyIterate` axis renders **N curves** (`FamilyCurves`), one per value of the iterated axis. Two entry points: (1) **Picker** — axis-role editor now has a 3-state toggle (X / Pinned / Family); `AxisRoleRowViewModel.IsFamily` added, `OnAxisSetToFamily` demotion callback, `FlushSliceAndRebuild` emits `AxisRole.FamilyIterate`; (2) **Auto-recognition** — bare `Name` (no `[`) or `Name[:, :]` (2 kept axes) parses as family (convention: last-kept=X, earlier-kept=Family). `CubeTraceSpecParser` synthesizes all-`:` tokens for bare names and replaces the old `xCount!=1` error with family assignment (keptDims≤2) or error (>2). `PlotInspectorViewModel.TrySetCubeData` routes single-cube specs (CubeName+Slice both non-null) through the slice path even when `Expression` is set; multi-cube expressions use `TraceExpression`. Static `ResolveFamily` loops the family axis, slices each rank-1 curve, calls `Trace.SetFamilyData`. `Trace` gains: `AxisRole.FamilyIterate`, `MaxFamilyCurves=101` (hard cap), `FamilyCurve` nested class, `FamilyCurves` list, `FamilyAxisName`, `IsFamily`, `SetFamilyData`, `RectY` private helper, updated `PathBoundingRect` (spans all curves for autoscale). `TraceRenderer.Draw` short-circuits for `IsFamily`: one stepped-color path per curve, then `DrawFamilyLegend` (corner box, axis name title, swatch+label rows, capped at 12 with "(+N) more" tail). Persistence: `AxisRole.FamilyIterate` round-trips via numeric enum value in `.cdd`; `FamilyCurves`/`Points` are derived and never serialized. Markers on family traces are deferred. 7 new gate tests (Parser_TwoKept, Parser_Bare, Parser_ThreeKept_Errors, Family_RendersNCurves, Family_Cap101, Family_Autoscale, plus TwoXAxes test updated). Build 0W/0E; 1605 total tests pass. **Phase 7.3 COMPLETE.**

Inline editor fixes (brief-inline-editor-fixes, 2026-06-16) — COMPLETE: The inline edit box derives its position solely from `SchematicComponent.LabelRowGeometry` → `WorldToScreen` (same source as the renderer and hit-test), so tweaking label placement is a one-line change in `SchematicComponent`. The hand-rolled `cpy + zoom*120 + textSize + row*(textSize+2)` formula (which drifted progressively lower at low zoom due to a non-scaling `+2` per-row term) is gone; `LabelBaseYFor` (N-aware) also places SDD/ZPort param boxes correctly for free. `ComponentLabelAnchor` now carries `Symbol` and `PortCount`; prefix measured once at the renderer's reference size (70) so it is zoom-independent. **VAR/SDD parameters are name-editable inline**: the param text includes `"Name = Expr Unit"` (select-all on open), and `CommitInlineEdit` parses out the `=` split → `EditParameterCommand` now takes an optional `newName` (snapshot old name for full undo). **Other params select value-only**: `InlineEditSelLength = param.Expression.Length` when a unit is present. **Unit remap**: `ParseExpressionUnit(raw, param)` overload (internal, testable) splits a no-space trailing unit ("1Ω" → "1"+"Ω") by checking whether the run matches `param.Unit` (canonical casing) or is a recognized engine unit (not a bare SI prefix like "n"). `FocusAndSelectInlineEditBox` is a shared helper used by both the component-label path and the wire-label path so both honour the selection contract. 8 gate tests in `InlineEditorFixesTests.cs`. Build 0W/0E; 1507 total tests pass.

Component label hitbox (brief-component-label-hitbox, 2026-06-16) — COMPLETE: Label row geometry has a single source of truth — `SchematicComponent.LabelRowGeometry` (anchor + hit band) and `SchematicComponent.LabelBaseYFor` (N-aware base-Y for SDD/ZPort, reusing `SymbolPortDefs.SddBodyRect`). The renderer (`DrawLabels`), the hit-test (`TestComponentLabels`), and both FullBb builders (`ToRenderComponent`, `SchematicModelBuilder`) all derive from these, so the clickable zone always tracks the rendered text and SDD/ZPort labels always clear the port-count-grown body. **Do not reintroduce a parallel copy of the label-layout constants in the hit-test.** Previously `TestComponentLabels` had private stale constants (`LabelRowHeight=72`, `LabelStartOffY=134`) that drifted from the renderer, especially after user-moved offsets. Bug B: fixed `LabelBaseY=280` constant caused label overlap on SDD/ZPort for N≥4; `LabelBaseYFor` returns `Math.Max(LabelBaseY, SddBodyRect(N).HalfH + LabelWorldStep)`. 6 gate tests in `ComponentLabelHitboxTests.cs`. Build 0W/0E; 1498 total tests pass.

Symbol library overhaul (brief-symbol-library-overhaul, 2026-06-16) — COMPLETE: SDD/ZPort autogen symbol uses a port-count-aware rounded-rect body (grows in ±Y with N, `RRect` radius=12) with 2N ± pins whose Y coordinates are ALWAYS whole multiples of the connection grid (100). Root cause fixed: `portSpacing=300` caused half-grid pin Y via `(nLeft-1)*0.5 * 300` fractions (e.g. ±50, ±150); banker's rounding collapsed these to the same P-cell → false "connected" on empty schematic. Fix: `portSpacing=400` so every center is an even multiple of 200, ±100 pins land on odd multiples of 100. N=1 special-cased (+ left at (−200,0), − right at (+200,0)) for both Sdd and ZPort. Body edges at ±90, stubs from ±90 to ±200; port-number/polarity `TextPrimitive` labels placed inside body near each stem. ZPort "Z" mark (diagonal lines) removed. `BuiltInSymbols.Primitives(kind, portCount)` overload added; old `Primitives(kind)` calls `Primitives(kind, 2)` for backward compat. Pin symbol reoriented horizontal: 6-point hexagon body + stem to right, tip at (200,0); `SymbolPortDefs.For(Pin)` → `("1", 200, 0)`. VAR symbol carries centered `TextPrimitive("VAR")`. ToneSource, Vdc, P1Tone, Term each carry "+" and "−" `TextPrimitive` indicators left of their stems. `SchematicModelBuilder` Pin port updated from `(0,−200)` to `(200,0)`. 10 gate tests in `SymbolLibraryOverhaulTests.cs`. Build 0W/0E; 931 Ui.Tests pass.

Trace expressions (brief-trace-expressions, 2026-06-16) — COMPLETE: Cube traces accept full **element-wise expressions** over cube slices (`TraceExpression`), reusing the circuitRF scalar expression engine evaluated per X-sample with cube refs bound as placeholder variables (`__c0`, `__c1`, …). Examples: `mag(V[:, 0, 0]) + mag(V[:, 0, 1])`, `dB20(V[:, 0, 1]) - dB20(V[:, 0, 0])`, `conj(V[:, 0, 0])`. Transforms are function calls (`mag(...)`, `dB20(...)`, `conj(...)`) — `CubeShorthand` and `BuildPickerExpression()` now emit function-call syntax (e.g. `mag(V[:, 0])` not `mag V[:, 0]`). `Trace.Expression` (nullable string) supersedes `CubeName`/`Slice`/`Transform` for value production when set; `Trace.ExpressionError` carries parse/eval failure text (cleared on success). `IsCubeBound` now includes `Expression is not null`. Pipeline in `TraceExpression.TryEvaluate`: scan expression for `CubeName[...]` refs → slice each to rank-1 → validate same X length → substitute placeholders → `Parser.Parse` → evaluate per X-sample via `Evaluator.InjectResolved` → yield `(xVals, complexValues?, realValues?, xAxisName, xUnit)`. `PlotInspectorViewModel.TrySetCubeData` branches: `trace.Expression is not null` → `TraceExpression.TryEvaluate` path, else existing single-slice path. `CommitSpec` commits to `trace.Expression` and delegates validation to `TrySetCubeData`. `FlushSliceAndRebuild` and `ApplySelectedTransform` set `trace.Expression = BuildPickerExpression()` after picker edits. `SpecError` is a computed getter reading `trace.ExpressionError`. `dB20` added as alias for `dB` in `Evaluator.cs`. Smith/Polar + real-valued expression → gentle "needs complex" error. Invalid syntax / mismatched slice dimensions surface as the ` <invalid>` hint. Matrix math is out of scope (element-wise only). 9 gate tests in `TraceExpressionTests.cs`. Build 0W/0E; 921 Ui.Tests pass.

Node-picker labeled filter (brief-node-picker-labeled-filter, 2026-06-16) — COMPLETE: The cube `node` axis-role picker filters to **user-labeled nodes only** (filter ON by default). Provenance is threaded `NetExtractor` → `TestBench.LabeledNets` → `NodeMap.LabeledNames` → `__LabeledNodes` side cube in the HB DataSet (persisted in `.npy`). `TraceRowViewModel.RebuildAxisRoles` reads `__LabeledNodes` (if present) and filters the `node` axis `PinOptions` to labeled nodes. A parallel `PinOptionIndices[]` list maps display-row → true cube-axis index so `TruePinIndex` (= `PinOptionIndices?[PinIndex] ?? PinIndex`) always resolves the true cube index. `FlushSliceAndRebuild` uses `TruePinIndex` in the emitted `AxisSlice`. `ShowAllNodes` (per-trace observable) defaults to `false` (filter ON) when `__LabeledNodes` is present; defaults to `true` when the cube is absent (hand-written netlist → show-all so those files stay usable). A present-but-empty `__LabeledNodes` shows nothing. A "Show all nodes" toggle appears on trace cards with a node axis. `__`-prefixed cubes are metadata: `RebuildSignals` skips them so `__LabeledNodes` never appears as a selectable signal. 11 gate tests (T1–T11) in `NodePickerLabeledFilterTests.cs` (Ui.Tests) and `HbLabeledNodesCubeTests.cs` (Engine.Tests). Build 0W/0E; 1460 total tests pass.

Trace-card layout fixes (brief-table-cube-layout-fixes) — COMPLETE: Five trace-card / Table fixes. (#1) Z0 row gated entirely on S-param traces: `ShowZ0Row => IsScatteringTrace` on `TraceRowViewModel`; outer StackPanel uses `IsVisible="{Binding ShowZ0Row}"` so cube/HB traces show no Z0 label or Ω. (#2) `OnFreqUnitChanged` in `PlotInspectorViewModel` now calls `vm.OnFreqUnitChanged()` on each row (which calls `RebuildAxisRoles()`) then `RebuildAndNotify()` so harmonic pin labels rebuild in the new unit. (#3+4) Identity row reordered to **signal | unified-transform | matrix(S-only, Auto) | →R**; two overlaid combos (network `YAxis` and cube `CubeTransform`) replaced by one `SelectedTransformItem` combo bound to `TraceTransformItems` (returns `AllCubeTransforms` for cube, `AllTransformsForNetwork` for network traces). `CubeTransformItem` gains `Enabled` flag; `AllTransformsForNetwork` disables `dB10`, `dB`, `Conj` (cube-only). `SelectedTransformItem` maps via `YAxisToCubeTransform`/`CubeTransformToYAxis`; `SyncTransformItem()` resyncs silently from `RefreshDescription`. (#5) Table trace-header double-click routes to the inline spec TextBox via `PlotInspectorView.FocusSpecTextBox(idx)` (stores `_inspectorView` in `PlotControl`, posts focus at `Render` priority). 3 gate tests in `TraceCardLayoutTests.cs`. Build 0W/0E; 1449 total tests pass.

Vdc component (brief-vsource-vdc-fix) — COMPLETE: `SymbolKind.VoltageSource` removed; replaced by `SymbolKind.Vdc`. Registry entry: DisplayName `"Vdc"`, prefix `"V"`, `IsCommon: true`, SearchTerms `["Vdc","DC","bias","supply","voltage","V"]`; `EngineReference(Vdc)` → `"Vdc"`; `TryParseCode("V")` and `TryParseCode("VDC")` → `SymbolKind.Vdc`; `DefaultParameters(Vdc)` → `[Vdc=0 V]` (single param, `ShowOnSchematic: true`). `ToneSource` gains a hidden `Vdc=0 V` param (`ShowOnSchematic: false`) as the 3rd default. Glyph: `BuildVdc()` in `BuiltInSymbols.cs` — 4-primitive battery (top lead, long +bar, short −bar, bottom lead); old 6-primitive circle+±marks removed. Ground glyph changed from 2-primitive (stem + filled triangle) to 4-primitive (stem + 3 horizontal bars). `LibraryCatalog.AllItems` sort updated to `StringComparer.OrdinalIgnoreCase` (explicit). `SchematicModelBuilder`: `SymbolKind.VoltageSource` → `Vdc`; demo params updated. 8 gate tests: 4 Engine.Tests + 4 Ui.Tests. Build 0W/0E; 1419 total tests pass.

Table/trace-card cube UX cluster (brief-table-cube-ux-cluster) — COMPLETE: Four refinements for cube (HB-sweep) data. (#3) Sort-arrow gap widened: `triCx` uses `boldFont.MeasureText(" ") * 1.5f` instead of the hardcoded `+2f`; `CalcFitWidth` reservation updated to match. (#5) MatrixType (S/Z/Y) combo gated to S-parameter (network/SNP) sources only via `ShowMatrixTypeCombo => !IsCubeBound && Data is { } d && !d.IsEmpty`; notified from `OnSelectedSignalChanged`, `RebuildSignals`, and `RefreshDescription`. (#6) Harmonic axis renders in the plot's `FreqUnits` (display-only): `TableRenderer` detects `IsFreqUnit(CubeXUnit)` and scales both the column-0 header and data cells by `FreqUnits.Scale()`; non-frequency cube axes (Pin/dBm, bias/V) are unscaled. Axis-role pin options in `TraceRowViewModel.RebuildAxisRoles` also scale freq-unit axes via `_parent.FreqUnit`. (#4) Inline spec editor: `Trace.InvalidSpecText` (string?) stores user's raw text when invalid; `CubeShorthand` returns `"{text} <invalid>"` and `FormatCubeCell` returns `""` when set. `CubeTraceSpecParser.TryParse(text, ds, ...)` is the pure-static inverse of `Trace.CubeShorthand` (transform + cube name + per-axis token: `:`, quoted label, or integer index). `TraceRowViewModel` gains `SpecShorthand` (raw editable text, no `<invalid>` suffix), `SpecError`, `HasSpecError`, and `CommitSpec(text)` (parse + apply / set invalid). `PlotInspectorView.axaml` adds a TextBox + `SelectableTextBlock` hint (gentle, selectable) below the axis-role editor for cube traces; event handlers in code-behind (LostFocus / Enter). 6 new gate tests in `TableCubeTraceTests.cs` (`CubeTraceSpecParserTests` class). Build 0W/0E; tests green.

Table plot cube-bound traces (brief-table-cube-traces) — COMPLETE: The Table plot supports cube-bound traces. When all traces in a plot are cube-bound, column 0 becomes the trace's kept (X) axis (name + unit, no freq scaling); cells read cube values via `Trace.FormatCubeCell`; and trace column headers use `Trace.CubeShorthand` (DataCube `Name[pinned, …, :]` index form). Mixed cube+SNP plots fall back to frequency mode. `TableRenderer.GetSortedRowAxis` returns the union of all cube X values (sorted) and delegates to `GetSortedFrequencies` for legacy/mixed plots. `Trace` exposes `CubeXValues`/`CubeComplex`/`CubeReal`/`CubeXAxisName`/`CubeXUnit` read accessors (no recompute). Markers on cube traces remain unsupported. 11 gate tests in `TableCubeTraceTests.cs`. Build 0W/0E; 1391 total tests pass.

Sweep Start/Stop/Step|Npts (brief-parametric-sweep-stepcount) — COMPLETE: `SweepExpander`/`SweepAxisMode` moved to `CircuitRF.Core.Design` (Core firewall). `SweepSpec` redesigned to `{ Start, Stop, StepOrCount, Mode, Kind }` (no Variable). `ParametricSweepAnalysis` gains spec constructor (expands eagerly + stores `Spec` for round-trip). `SweepAxisRowViewModel` adds `BuildSpec() → SweepSpec?` (returns null for List mode) and `FromPsa` now restores `StartExpr/StopExpr/StepOrCountExpr/Mode/Kind` from `psa.Spec` when present (falls back to List). `AnalysisEditorViewModel.BuildAnalyses()` uses spec constructor for StepSize/PointCount axes so the `.cnl` writer emits compact `Start=/Stop=/Step=|Npts=` form. Build 0W/0E; 260 Core.Tests + 880 Ui.Tests pass.

Sweep results one-file (brief-sweep-results-one-file) — COMPLETE: A parametric-sweep tree writes a **single** results file named after its **root inner analysis** (`HB1.npy`, not `HB1_sweep_Pin.npy`). Analyses referenced as the `Inner` of any `ParametricSweepAnalysis` are not run or written standalone. Implementation in `SchematicRunService.RunNetlist`: (1) builds `innerOfSweep` set (all `InnerAnalysisName` values) before the dispatch loop; (2) adds `if (innerOfSweep.Contains(analysis.Name)) continue;` guard (name-membership based, independent of `Enabled`); (3) for sweeps, the result name comes from `RootInnerName(psa, tb)` (walks `InnerAnalysisName` down to the first non-sweep analysis, max 64 hops). `DeduplicateName` still guards if two sweep trees resolve to the same root name. 4 new gate tests (S1–S4): single-sweep one-result, nested-sweep one-result, standalone-still-runs regression, mixed-standalone-and-swept two-results. Build 0W/0E; 1380 total tests pass.

P1Tone source component (brief-sweep-5-p1tone-source) — COMPLETE: `SymbolKind.P1Tone` added to `SchematicModel.cs` enum. Registry entry: DisplayName `"P1Tone"`, prefix `"P"`, `IsCommon: true`, SearchTerms `["P1Tone","power","Pavl",...]`; `EngineReference(P1Tone)` → `"P1Tone"`; `TryParseCode("P1TONE")` → `SymbolKind.P1Tone`; `DefaultParameters(P1Tone)` → `[Pavl=0dBm, Z=50Ω, Freq=1GHz, Phase=0deg]`; `SymbolPortDefs.For(P1Tone)` uses default 2-pin (top/bottom). Glyph: `BuildP1Tone()` in `BuiltInSymbols.cs` (circle + sine + power-arrow chevron ↑). Core layer: `P1ToneModel` in `src/Core/Devices/P1ToneModel.cs`; `ComponentModelFactory` registers `"P1Tone"` in `_parameterizedTypes` and dispatches to `CreateP1ToneModel`; `Elaborator` mints `__p1tone_{path}_drv` and calls `ResolveP1ToneParameters`. HB layer: `HbEngine.Run`/`RunTwoTone` call `SetToneContext(fc, driveFreqHz)` on every `P1ToneModel` before extraction; commensurability checks include `P1ToneModel.FreqHz`. 7 gate tests in `P1ToneTests.cs`. Build 0W/0E; 1346 total tests pass.

Sweep Fix 4 (brief-sweep-4-edit-analysis-ui) — COMPLETE: Analysis editor now supports 0..N parametric sweep axes wrapping any inner analysis (DC/SP/HB). **Headless helper:** `SweepExpander` (`src/Ui/Schematic/SweepExpander.cs`) + `SweepAxisMode` enum (`StepSize`/`PointCount`/`List`) — static `ExpandSweep(start, stop, stepOrCount, mode, kind)` and `ExpandList(csv)`. **Row VM:** `SweepAxisRowViewModel` (`src/Ui/ViewModels/SweepAxisRowViewModel.cs`) — VarName (combo with `KnownVarNames` from VAR components + soft unknown-variable warning), Mode (seg-btns), per-mode fields, Lin/Log kind, live preview, `BuildValues() → double[]?`, `FromPsa` restore factory, `FromLegacyHbSweep` migration factory. **Row view:** `SweepAxisRowView.axaml(.cs)` — card-style with AutoCompleteBox for variable name, Mode seg-btns, Lin/Log, Start/Stop/Step|Count|List fields, Remove button (walks visual tree to find `AnalysisEditorViewModel.RemoveSweepAxisCommand`). **Analysis editor VM:** `AnalysisEditorViewModel` gains `ObservableCollection<SweepAxisRowViewModel> SweepAxes`, `SweepsExpanded`, `AddSweepAxisCommand`, `RemoveSweepAxisCommand`, `EditingChainNames`, `BuildAnalyses() → IReadOnlyList<Analysis>?` (replaces `BuildAnalysis()`). Chain: [inner (Enabled=false), sweep₁ (false), …, sweepₙ (Enabled=true)]; naming scheme `<innerName>_sweep_<varName>`. Legacy HB `SweepVar*` migrated into a StepSize row on `FromAnalysis`. Edit constructor handles `ParametricSweepAnalysis` by resolving to the innermost non-sweep analysis and loading the chain via `ResolveChain`. **Dialog:** `AnalysisEditorDialog.axaml` adds `x:DataType` to Window and a "Parametric Sweeps" Expander below the analysis body panels; AXAML uses `DataTemplate DataType="vm:SweepAxisRowViewModel"` → `SweepAxisRowView`. Code-behind updated: `ShowAsync` returns `IReadOnlyList<Analysis>?`; `OnOkClick` calls `BuildAnalyses()`. **HB body:** old `SweepEnabled`/`SweepVarName`/`SweepStart|Stop|StepExpr` fields + AXAML section removed. **New commands:** `AddAnalysesCommand` (adds list contiguously, undo removes all); `EditAnalysisChainCommand` (replaces old chain by names, undo restores at original index). **AnalysesListViewModel:** `Add` → `AddAnalysesCommand`; `Edit` collects `vm.EditingChainNames` → `EditAnalysisChainCommand`. **Serialization:** `CschAnalysis` gains `PsaVarName/PsaValues/PsaInnerName`; `AnalysisSerialization.ToDto/FromDto` handles `"sweep"` type tag → `ParametricSweepAnalysis` round-trip. **NetExtractor:** `Enabled` filter removed — ALL analyses flow into `tb.Analyses` (so `ParametricSweepEngine` can find inner analyses by name); comment explains the split. **SchematicRunService:** `if (!analysis.Enabled) continue;` guard added at dispatch loop — disabled chain members are never run directly. 21 new tests: `SweepExpanderTests` (9 tests covering all modes/kinds), `SweepBuilderTests` (4 tests: nested chain, no-axes single, legacy HB migration, outer-sweep edit load), `NetExtractorAnalysesTests` updated (5 existing tests adapted + 1 new chain test). Build 0W/0E; 1339 total tests pass.

VAR component UI (brief-var-component-ui) — COMPLETE: double-clicking a VAR component opens `VarEditorDialog` (instead of the generic `ParameterEditorDialog`). The editor has two modes — **Mode A (Text, default)**: a single multi-line `TextBox` where each line is `name = expression`; comments (`#`/`//`) and blank lines are skipped; a validation banner shows parse errors and duplicate names; "Apply" commits via `SetVarParametersCommand` (atomic, undoable). **Mode B (Rows)**: an `ItemsControl` of editable name/expression/unit rows with Add/Remove per-row, routing through `SetVarParamNameCommand`/`EditParameterCommand`/`Add-Remove VarParameterCommand`. Switching Text→Rows applies pending text; switching Rows→Text serializes current params back to text. All edits flow through `SchematicViewModel.Execute` (undo/redo + dirty dot). Parse/serialize logic is in `VarTextParser` (static, framework-free, testable): `ParseLines()` and `SerializeLines()`. VAR symbol is now a port-less box (no leads) in `BuiltInSymbols` (`BuildVar()`). `AllBuiltIns_HaveAtLeastOnePin` updated to skip `SymbolKind.Var`. 2 new gate tests in `VarComponentTests`: `ParseLines_RoundTrips` and `Duplicate_EmptyName_Flagged`. Build 0W/0E; 1315 total tests pass. **VAR component complete.**

VAR component (brief-var-component-core) — COMPLETE: `SymbolKind.Var` is a node-less, port-less component whose `EditableParameter` rows are routed by `NetExtractor.ExtractModel` into the enclosing frame's `Cell.Variables` (sub-cell) or `TestBench.GlobalVariables` (testbench top). VAR is **never emitted as an `ElaboratedComponent`** — it is skipped in the emission loop (alongside `Ground`/`Pin`) and `EngineReference(Var)` returns sentinel `"VAR"` (not a factory primitive). Per-cell isolation and HB sweepability fall out of the existing scope machinery (`Elaborator.BuildGlobalScope` / `BuildCellScope` already bind `Variables`). `ComponentTypeRegistry` entry: DisplayName `"VAR"`, prefix `"VAR"`, `IsCommon: true`, SearchTerms `["VAR","Variable","var","vars","parameter","sweep"]`; `TryParseCode("VAR")` → `SymbolKind.Var`; `DefaultParameters(Var)` → `[]`; `SymbolPortDefs.For(Var)` → `[]`. Duplicate variable names across VAR rows in the same frame emit a conflict and keep the first. `.cnl` representation: VAR-sourced variables emit naturally via the existing `name = expr [unit]` variable directive — no new `.cnl` syntax needed. 6 gate tests in `VarComponentTests.cs`. Build 0W/0E; 1313 total tests pass.

Load Run Results (brief-datadisplay-load-results) — COMPLETE: Data Display can load a schematic's run results via "Load Run Results…" (toolbar button, `FolderArrowDownOutline` icon). `DisplayWindowViewModel` gains `_loadRunResultsAction` + `SetLoadRunResultsAction` + `GetResultsRootAction` (public `Func<string?>?`) + `LoadRunResultsCommand`. `DataDisplayView.axaml.cs.OnLoaded` injects `DoLoadRunResultsAsync` via `SetLoadRunResultsAction`; `DoLoadRunResultsAsync` opens `StorageProvider.OpenFolderPickerAsync` with `SuggestedStartLocation` = `GetResultsRootAction?.Invoke()` (via `TryGetFolderFromPathAsync`), then calls `DataSourceLibrary.LoadFileAsync(path)` for every `*.npy` in the chosen folder (dedup is handled there); no `.npy` files → `ShowErrorAsync`. `WorkspaceViewModel` injects `GetResultsRootAction = GetResultsRoot` (private helper) in both `NewDataDisplay` and `OpenOrActivateDataDisplayCoreAsync`. A richer results-browser flyout (list of schematic-key folders) is a noted follow-up. Build 0W/0E; 1307 total tests pass.

**Scratch results discovery (2026-06-26):** `GetResultsRoot` now delegates to `RunResultsWriter.ResolveResultsRoot(CurrentWorkspacePath, _recovery.SessionDir)`, which MUST mirror `WriteNetlist`'s destination: `<workspaceRoot>/results` when a workspace is open, else `<recovery SessionDir>/results`. Previously it returned **null** with no workspace, so a scratch sim (no `.csch`/workspace) wrote results to `SessionDir/results/<scratchKey>/run.npy` but the Data Display's `ResultsRootProvider` got null → `RefreshAvailableDataSources` enumerated nothing → the scratch run never appeared in the source picker. The fix makes a no-save scratch sim plottable. The workspace flow is unchanged (still `<workspaceRoot>/results`); only the no-workspace case changed from null → scratch dir. Side effect: `CanExportData` (`GetResultsRoot() is not null`) is now always true — export is additionally available for scratch runs (harmless; the dialog enumerates whatever exists). Tests `RunResultsWriterTests.ResolveResultsRoot_{Workspace,NoWorkspace}_*`.

Phase 7.3a (axis-role assignment picker) — COMPLETE: cube-bound traces are now authored via a per-axis role editor (X / Pinned) over any-rank DataCube; the flat ≤2-D enumeration is replaced by one `AvailableSignals` item per cube (rank ≥1; "S"/"Z0" still skipped). New `AxisRoleRowViewModel` (per-axis row: `AxisName`, `Unit`, `IsX`, `PinIndex`, `PinOptions`, `IsRoleToggleable`); `ObservableCollection<AxisRoleRowViewModel> AxisRoles` on `TraceRowViewModel`, rebuilt in `RebuildSignals()` (at the end) and in `OnSelectedSignalChanged` (cube apply block). Auto-flip invariant: setting IsX on a row calls `OnAxisSetToX` which silently demotes other X rows via `SetIsXSilent`; `FlushSliceAndRebuild` writes the new `AxisSlice[]` back to `Trace.Slice` (by axis order, Role=KeepAsX/PinToIndex) and calls `_parent.RebuildAndNotify()`. No-X guard in both `RebuildAxisRoles` and `FlushSliceAndRebuild` (first axis is forced X if none has KeepAsX). Owner-side resolution in `TrySetCubeData` generalised to N-D: build `object[] args` by **name-matched** lookup (`foreach (var s in slice) if (s.AxisName == axName)`) instead of positional, with fallback (axis 0 kept when no KeepAsX entry found). `PlotInspectorView.axaml` gains `ItemsControl` bound to `AxisRoles` (inside `IsStandardTrace` StackPanel, `IsVisible=IsCubeBoundTrace`); each row shows axis label, X/Pin seg-btns, and pin-index ComboBox. `CubeTraceTests.CubeTrace_RankGE3_NotOffered` renamed/updated to `CubeTrace_RankGE3_Offered` (old assertion reversed). 5 new gate tests in `AxisRolePickerTests.cs`. Build 0W/0E; 811 Ui.Tests pass.

Phase 7.2f-2 (Z0 box default-locked + Override checkbox) — COMPLETE: The trace-card Z0 box is read-only by default, showing the source's port-1 uniform reference (`SourceZ0PerPort[0]`, or `Data.Z0` when no per-port vector). An "Override" checkbox to the right unlocks the box for uniform-renorm; unchecking reverts the box and `_trace.Z0` to the source port-1 value and triggers a recompute. Non-uniform sources (`Z0Kind.NonUniform`) replace the box+checkbox entirely with subtle grey "Multiple Port Normalization" text — no editing, no glyph. UniformComplex is treated as uniform (box shown with complex port-1 value; Override available). The per-trace `AlertCircleOutline` orange glyph (7.2e) is removed from the trace card; the one-time Messages warning on load is retained. VM: `TraceRowViewModel` gains `_sourceZ0Kind` (stashed by `ApplySourceZ0`, now instance method; static trace-only path renamed `StampSourceZ0OnTrace`), `SourceZ0IsNonUniform`, `IsScatteringTrace`, `IsMultiPortNormalization`, `ShowZ0Control`, `[ObservableProperty] Z0OverrideEnabled`, `SeedZ0FromSource()`, `_applyingSource` + `_seedingZ0` flags to suppress partial-method rebuilds during seeding. `IsZ0Editable` changed to `ShowZ0Control && Z0OverrideEnabled`. `Z0DisabledReason` retained for existing tests. 5 gate tests in `Z0OverrideTests.cs`. Build 0W/0E; 1296 total tests pass.

Phase 7.2f (per-port Z0 compute) — COMPLETE: scattering traces now compute against the source's true per-port Z0 vector (`SourceZ0PerPort`/`SourceZ0IsUnusual`, populated from the Z0 cube via 7.2e classification). `Trace.BuildMatrixPath` uses `RFNetwork.SToZ(mat, SourceZ0PerPort)`/`SToY(mat, SourceZ0PerPort)` when `SourceZ0IsUnusual` (not the scalar-collapse cheat); `GetMarkerImpedanceString` picks `SourceZ0PerPort[Row]` instead of the scalar `_z0`. `BuildDerivedPath` (stability, max-gain) renorms non-uniform sources to uniform-real via `SToS(m, SourceZ0PerPort, z0RealArray)` before calling `StabilityMu`/`MaxGain`. Uniform/Touchstone path unchanged. `StampSourceZ0OnTrace` (internal static) propagates both fields from the library entry to the trace during `PlotInspectorViewModel.RefreshSourceZ0`. 5 gate tests in `PerPortZ0ComputeTests.cs`. Build 0W/0E.

Phase 7.2e (non-uniform/complex Z0 indicator) — COMPLETE: Data Display surfaces an always-on per-trace badge (Material `AlertCircleOutline`, `CrfWarningBrush`, tooltip = per-port Z0 values formatted as `portN=<real>Ω` or `portN=<re>+<im>jΩ`) on scattering traces whose source `HasUnusualZ0` (i.e. `DataSetBuilder.ClassifyZ0(_data["Z0"])` returns `NonUniform` or `UniformComplex`, not `UniformReal`). `DataSourceEntryViewModel` gains `Z0Kind?`, `HasUnusualZ0`, `Z0PerPort` (computed by `ClassifyZ0FromData()`, called from both constructors and both `Refresh*` methods). `TraceRowViewModel` gains `ShowZ0Badge` (= non-cube-bound S-trace with `entry.HasUnusualZ0`) and `Z0BadgeTooltip` (per-port list + kind suffix); notified from `OnSelectedSignalChanged`, `RebuildSignals()`, and `RefreshDescription()`. `PlotInspectorView.axaml` Z0 row Grid extended to 5 columns (`Auto,Auto,Auto,*,Auto`) with `mi:MaterialIcon` in column 4. One-time Messages warning fires via the library→workspace event seam: `DataSourceLibraryViewModel.UnusualZ0Detected` event (guarded by `_warnedPaths HashSet`, cleared on `Remove`); `WorkspaceViewModel.WireDataDisplayLibraryEvents()` subscribes at document-creation time and posts via `Messages.Warning`. 3 gate tests in `Z0IndicatorTests.cs`. Build 0W/0E; 1286 total tests pass. Full per-port Z0-dependent compute (S→Y/Z, marker impedance, stability on non-uniform sources) remains the **7.2f** follow-on.

Phase 7.2c-c (DataSource rename) — COMPLETE: pure rename pass. `SnpLibraryViewModel` → `DataSourceLibraryViewModel` (`DataSourceLibraryViewModel.cs`); `SnpEntryViewModel` → `DataSourceEntryViewModel` (`DataSourceEntryViewModel.cs`); `DisplayWindowViewModel.SnpLibrary` → `DataSourceLibrary`; view files `SnpLibraryView.axaml(.cs)` → `DataSourceLibraryView.axaml(.cs)`. All consumers retyped: `TabViewModel`, `DataDisplayViewModel`, `PlotContainerViewModel`, `PlotInspectorViewModel` (`_library` field + `TrySetCubeData` param + `LibraryEntries` collection type), `TraceDataItem` (Entry type), `PlotControl.Library` DirectProperty, `WorkspaceViewModel.RefreshOpenDataDisplaysAsync`, AXAML `x:DataType` and `DataTemplate` references, `DataDisplayView.axaml(.cs)`. NAMING DEBT headers removed from both renamed files. No behavior change; no serialization change. Build 0W/0E; 1283 total tests pass. **Phase 7.2c COMPLETE.**

Phase 7.2c-b (minimal-label display-name policy) — COMPLETE: trace display names are computed at the plot level (`TraceLabeler.ComputeMinimalLabels`) from two separate identity components — source (`Path.GetFileNameWithoutExtension(SourcePath)`) and quantity (`ShortDescription` for network-bound; `CubeName(pinned=idx) transform` for cube-bound). Any component constant across the plot's traces is dropped; recomputed on add/remove. Label priority: `CustomLabel` (user override, theme color) › `AutoLabel` (computed policy, trace color) › legacy `ShowFilePrefix` fallback. New `AutoLabel` DirectProperty on `AxisLabelControl`; `LabelStripViewModel._autoLabel` observable property; `PlotContainerViewModel.UpdateLabelStrips()` calls `TraceLabeler` and stamps `AutoLabel` on each non-custom strip; `AxesRenderer.DrawTitleAndAxisLabels()` uses the same lookup for Rect Y-axis margin labels. `alwaysShowSource` reads `AppSettingsViewModel.Instance.AlwaysDisplayDataSourcePrefix`. Separator `·` (U+00B7). 5 gate tests in `MinimalLabelTests.cs`. Build 0W/0E; 793 Ui.Tests pass.

Phase 7.2c-a (cube-native trace path) — COMPLETE: `Trace` is now either **network-bound** (SNP/matrix/derived, unchanged) or **cube-bound** (`SourcePath`+`CubeName`+`Slice`+`Transform`, identity stored as separate fields). Three new types in `Trace.cs`: `CubeTransform` enum (`None dB20 dB10 dB Mag Phase Real Imag Conj`), `AxisRole` enum (`PinToIndex KeepAsX`), `AxisSlice` readonly record struct. `IsCubeBound => CubeName is not null` discriminates the two paths in `BuildPath`. Owner-injects-data pattern: `PlotInspectorViewModel.TrySetCubeData` (internal static) resolves the DataSet from the library by `SourcePath`, slices the DataCube, calls `trace.SetCubeData(xVals, complexValues?, realValues?, xAxisName, xUnit, plotType, freqUnit)` — `Trace` never holds a `DataSet` reference. Signal picker (TraceRowViewModel.RebuildSignals) enumerates ≤2-D cubes only (rank 1 → one signal; rank 2 → enumerate pinned axis per keep-axis; rank ≥ 3 → skip; "S"/"Z0" → skip). `PlotInspectorView.axaml`: CubeTransform ComboBox in col2 (`IsVisible=IsCubeBoundTrace`); YAxis ComboBox visibility bound to `ShowYAxisCombo = IsRectOrTablePlot && !IsCubeBoundTrace`. `.cdd` persistence via `TraceConfig`: new nullable `CubeName`/`CubeTransform`/`CubeSlice` fields, no format-version bump. Cube-bound traces loaded via `LoadPlotContainerConfigAsync` use a placeholder SNP and call `TrySetCubeData` immediately. Markers/derived remain network-only for now (all marker methods guard `if (IsCubeBound) return`). 4 gate tests in `CubeTraceTests.cs`. Build 0W/0E; 788 Ui.Tests pass.

Quit latch fix — COMPLETE (brief-quit-latch): `App._isShuttingDown` is released via `App.AbortQuit()` from `WorkspaceWindow.OnClosing` whenever a close/quit prompt is cancelled (user hit Cancel, or cancelled the save dialog). `AbortQuit() => _isShuttingDown = false` is a harmless no-op when called during a plain window-close cancel (latch was already false). A caught exception in `OnClosing` also calls `AbortQuit()` so an unexpected error in the save pipeline never wedges all future quits. Build 0W/0E.

Data Display tab dirty indicator — COMPLETE (brief-datadisplay-dirty-indicator): `DataDisplayDocumentViewModel.IsDirty` is now live. `DataDisplayViewModel.ContentChanged` fires via two channels: structural undo edits (`UndoRedo.StateChanged`) and inspector redraws (`PlotContainerViewModel.PlotNeedsRedraw`, hooked via `OnPlotsCollectionChanged` on `_plots.CollectionChanged`). `DisplayWindowViewModel.DirtyChanged` bubbles `ContentChanged` from the active tab; it also fires in `OnUndoRedoStateChanged` (tab add/remove) and after each `CaptureBaseline()` call (save and load, so the bullet clears on save). `DataDisplayDocumentViewModel` subscribes `Window.DirtyChanged` and recomputes `IsDirty = Window.HasUnsavedChanges()` — authoritative, ignores view-only state (selection, zoom, pan). 3 gate tests (`DataDisplay_DirtyBullet_On{StructuralEdit,ClearsOnSave,InspectorEdit}`). Build 0W/0E; 784 Ui.Tests pass.

Project Tree "Save" context item — COMPLETE (brief-tree-save-dirty): a "Save" `MenuItem` (first in the context menu) appears on Cell and ViewFile/DataDisplayFile nodes that are currently dirty; hidden when clean. Header is node-kind-specific ("Save Cell" / "Save Schematic" / "Save Symbol" / "Save Data Display"). `ITreeActions` gains `IsNodeDirty(node)` + `SaveNodeAsync(node)`. `WorkspaceViewModel` implements both: `IsCellDirty` is extracted from the old `RefreshCellDirty` body so both the cell-dirty indicator and `IsNodeDirty` share one aggregation; `IsNodeDirty` covers Cell (via `IsCellDirty`), .csch (registry dirty set), .csym (open symbol docs), and .cdd (via `HasUnsavedChanges()`); `SaveNodeAsync` dispatches to `SaveCellViewsAsync` (saves all dirty schematics + symbol editors under the cell dir, then calls `RefreshCellDirty`), `SaveSchematicByPath` (registry → `SchematicPersistence.SaveToFile` → `NotifySessionSaved`), `SaveSymbolByPathAsync` (delegates to existing `SaveMaterializedSymbolDoc`), or `SaveDataDisplayByPathAsync` (delegates to existing `SaveDataDisplayDoc`). `ProjectTreeNodeViewModel` gains `IsSaveable` (plain getter, re-evaluated at menu-open time), `SaveHeader` (kind-specific label), and `SaveCommand` (`AsyncRelayCommand`, no CanExecute guard — `IsVisible=IsSaveable` gates the item in AXAML). 5 new gate tests in `ProjectTreeSaveTests.cs`. Build 0W/0E; 1271 total tests pass.

Close/quit save pipeline — COMPLETE (brief-close-quit-save): two bugs fixed. **Bug #2 (crash):** `PromptSaveBeforeClose` crashed with `ArgumentOutOfRangeException` when only orphaned dirty sessions existed — the old final branch `dirtyMatSymbols[0].Id` had no guard. Fixed: `firstId` is now `string?` with a 7-branch nullable chain ending in `Path.GetFileNameWithoutExtension(orphanedPaths[0])` and a `null` fallback; the message uses `(total == 1 && firstId is not null)`. **Bug #1 (data loss):** dirty `.cdd` documents slipped through the close/quit pipeline. `DataDisplayDocumentViewModel.IsDirty` is never wired to live edits; the fix bypasses it entirely — `HasAnyDirtyWork()`, `PromptSaveBeforeClose`, and `ConfirmCloseDockable` all call `DisplayWindowViewModel.HasUnsavedChanges()` directly (a polled baseline-comparison, set by `SaveAllAsync` / `LoadAllAsync`). New `SaveDataDisplayDoc(dd, owner)` saves materialized docs in-place and scratch docs via a `.cdd` file picker (mirrors the schematic/symbol pattern). `OnClosing` in `WorkspaceWindow.axaml.cs` hardened with try/catch so a future exception in the prompt keeps the window open rather than crashing the app. 3 gate tests in `CloseQuitSaveTests.cs`. Build 0W/0E.

Data Display auto-refresh — COMPLETE (brief-datadisplay-autorefresh): after a successful run, `RunAnalysis` captures the paths returned by `RunResultsWriter.WriteResults` (return type changed from `void` to `IReadOnlyList<string>`) and calls `RefreshOpenDataDisplaysAsync(written)`. That helper iterates all open `DataDisplayDocument`s (both `_openDocsByPath` and `_scratchDataDisplays`) and calls `DataSourceLibraryViewModel.ReloadChangedAsync(changedPaths)` on each. `ReloadChangedAsync` (new) reloads only the entries whose `FilePath` matches one of the changed absolute paths — skipping missing files (no `FindMissingFileAsync` prompt), reusing `ReloadAsync` in-place so SNP/DataSet identity is preserved and `LibraryChanged` fires per entry triggering inspector rebuild + redraw. Brand-new `.npy` files not already in a display are NOT auto-added. Build 0W/0E; 2 new tests (`RunResultsWriter_ReturnsWrittenPaths`, `DataSourceLibraryViewModel_ReloadChanged_OnlyMatching`); 1261 total tests pass.

Tree Remove Cell — COMPLETE (brief-tree-remove-cell): `CellUsageScanner.CountReferencingCells(workspaceRootDir, targetCellDir)` scans the workspace for distinct cells whose schematics contain a `CellRef` resolving to the target (best-effort; skips unreadable schematics). `ITreeActions.RemoveCellAsync` implemented in `WorkspaceViewModel`: guards on `CurrentWorkspacePath`, counts referencing cells, shows a big warning dialog (usage count appended when > 0), force-closes all open tabs/sessions under the cell dir, calls `SystemTrash.TryMoveToTrash`, then refreshes the tree. `ProjectTreeNodeViewModel` gains `RemoveCellCommand` (`IAsyncRelayCommand`, CanExec: `IsCell`). Context menu bottom gains `<Separator IsVisible="{Binding IsCell}"/>` + `<MenuItem Header="Remove Cell" .../>`. Referencing cells are NOT auto-repaired — broken cell-refs already degrade gracefully (Not Found placeholder). Build 0W/0E; 4 new `CellUsageScannerTests` pass; 1255 total tests pass.

Tree Remove-to-Trash — COMPLETE (brief-tree-trash-and-file-remove): removals route through `SystemTrash.TryMoveToTrash` (OS Trash/Recycle Bin; recoverable; **never hard-delete on failure** — returns false + error). Windows uses `SHFileOperation` with `FOF_ALLOWUNDO` (P/Invoke; works for files and directories). macOS uses `osascript → Finder delete`. Linux uses `gio trash`. `ITreeActions` gains `RemoveDataDisplay`/`RemoveFile`; both delegate to the shared private `RemoveNodeToTrashAsync` (confirm dialog → close open tabs via `ForceCloseDockable` → trash → `ProjectTreeTool.Refresh`). `ForceCloseDockable` on `CircuitRfDockFactory` bypasses the dirty-save confirm hook (file is being deleted; saving would be wrong). `ProjectTreeNodeViewModel` gains `IsDataDisplayFile`, `IsRemovableFile` (OtherFile/UserFolder/`.csch`/`.csym` ViewFiles), `RemoveDataDisplayCommand`, `RemoveFileCommand`. `.npy`/results live under `OtherFile`/`UserFolder` node kinds (no dedicated NodeKind). Context menu items added to `ProjectTreeView.axaml`. macOS osascript requires Finder AppleScript authorization (entitlement); headless `dotnet test` returns -1743 — tests treat that as a pass (environment gap). Build 0W/0E; 32 new test assertions pass.

Schematic hierarchy save — COMPLETE (brief-schematic-hierarchy-save): single-document Save (`WorkspaceViewModel.SaveSingleDocument`, the funnel for both toolbar Save and ⌘S-single) now persists the base session **and** every dirty session in the document's nav stack. After writing `doc.ViewModel.EditModel` to `doc.FilePath`, the method iterates `doc.NavFrames`, skips the base (by `ReferenceEquals`), skips clean frames (`!session.UndoRedo.IsModified`), looks up each dirty session's path via `_registry.TryGetPath`, calls `SchematicPersistence.SaveToFile` per session, then `NotifySessionSaved` to clear the dirty flag and refresh the tree dot. Hierarchy edits live in pushed-in shared sessions, NOT in `doc.ViewModel.EditModel`; popped-out dirty sessions are still covered by the Save-All / close-prompt orphaned-session sweep. 3 new gate tests in `HierarchySaveTests.cs`. Build 0W/0E; 1227 tests pass.

Engine diagnostics channel — COMPLETE (Phase brief-engine-diagnostics-channel): `SchematicRunService` drains `nl.Warnings` (populated by `ElaboratedNetlist.AddWarning`/`AddWarningOnce` in `src/Engine`) into `RunResult.Warnings` after every dispatch, including on `EngineError`. `WorkspaceViewModel.RunAnalysis` posts each warning to the Messages pane at Warning level (`Messages.Warning(w)`). The engine never touches `IMessageSink` directly. Gated by `SchematicRunServiceTests.RunNetlist_FloatingNodeFromBuriedTerm_WarningsNonEmpty` (L1e) and `RunNetlist_CleanNetlist_WarningsEmpty` (L1f).

Net extraction pin geometry — COMPLETE: `NetExtractor` now uses `SchematicEditModel.PortDefsOf`/`PortWorldOf` (cell-ref-aware, the render model's single source of truth) for component port positions. Built-in `SymbolPortDefs` is the fallback for non-cell components and cell-reference instances where `SchematicDirectory` is not set (backward-compat). For resolved cell-refs, `NetExtractor.BuildCellRefResolutions` pre-builds a `Dictionary<compId, CellSymbolResolution>` via `CellSymbolResolver.Resolve` and passes it to `GetEffectivePortDefs`, which replaces every old `SymbolPortDefs.For + GetPortWorldCoord` callsite (Layer-1 seeding, short-disable union, `AssignNetNames` auto-scan, `EmitCellInstance` binding, `EmitInstance` terminals). `EmitCellInstance` binding guard now compares against the **resolved pin count** (not the always-2 `SymbolPortDefs` length). 4 new hierarchy tests in `NetExtractorHierarchyTests.cs` gate the fix. Build 0W/0E; 1220 tests pass.

Phase 7.2b (data-source library) — COMPLETE: Source library now loads `.npy` via `DataSetImporter` alongside Touchstone (via `TouchstoneIO` + `DataSetBuilder.FromSnp`). Each `SnpEntryViewModel` carries a `DataSet? Data` (unified payload) and `SNP? Snp` (S-param facet — non-null for Touchstone and `.npy`-with-S; null for cube-only `.npy`). `.npy`-with-S: `DataSetBuilder.ToSnp(data)` exposes an `SNP` for the existing picker; `.npy`-without-S: `Snp = null` (not pickable until 7.2c). `SourceKind {Touchstone, Npy}` enum routes `LoadFileAsync`, `ReloadAsync`, `RestoreBrokenEntry`, `AddBrokenEntry`. `IsBroken => _snp?.IsEmpty ?? false` (null Snp is NOT broken). Command properties use `{ get; private set; } = null!` (assigned in `InitCommands`). File-picker updated to "Data Files" (Touchstone + .npy). `SnpLibraryView.axaml.cs` drop handler uses `e.IsBroken` + `e.FilePath`. Naming debt: `Snp{Library,Entry}ViewModel` rename to `DataSource*` deferred to 7.2c. Build 0W/0E; 1211 tests pass. S-param gate met (`.npy`-with-S pickable + plottable via existing SNP machinery). **Next: 7.2c** (cube-native trace path for non-S cubes + identity components + minimal labels + class rename).

Pre-7.2 cleanup (Skia Plex glyph fallback) — COMPLETE: Data Display renderers now use a per-glyph DejaVu fallback for any code point IBM Plex Sans lacks. New `DataDisplay/Renderers/RendererText.cs` adds `DrawLeftTextWithFallback` + `MeasureTextWithFallback` (splits text into Plex/DejaVu runs via `SKTypeface.GetGlyph`). `TableRenderer` uses these for all trace-data cells (where `∠` U+2220 appears in MA/DB format) and builds matching DejaVu fonts in `Draw()` and `CalcFitWidth()`. Table sort-direction arrow (▲/▼) is now an `SKPath` drawn just right of the freq header text (`DrawSortArrow`) — the glyph characters are gone from both `DrawHeaderRow` and `CalcFitWidth`. `MarkerRenderer.DrawInfoBox` uses `DrawLeftTextWithFallback` per line; `MeasureInfoBox` uses `MeasureTextWithFallback` so info-box sizing accounts for real `∠` width. IBM Plex remains the primary font for all other text. Build 0W/0E; 1206 tests pass. **Next: 7.2** (DataSet as trace data source).

Phase 7.1d-3b (stale-marker guard) — COMPLETE: `MarkerEditorViewModel` gains `private bool MarkerIsLive => _parent is not null && _parent.Trace.Markers.Contains(_marker)`; all nine model-mutating paths (`OnNameChanged`, `OnMatrixFormatChanged`, `OnStyleChanged`, `OnDigitsChanged`, `OnUseNormalizedChanged`, `OnFormatStringChanged`, `OnIsMultiChanged`, `OnIsDeltaChanged`, `CommitFrequency`) start with `if (!MarkerIsLive) return;`. Edits to a detached marker (Ctrl+Z removed it) are silently dropped; after Ctrl+Shift+Z redo the same instance is live again and edits resume. Read-only display properties unchanged. Build 0W/0E. **7.1d-3 COMPLETE. Next: 7.2** (DataSet as trace data source).

Phase 7.1d-3a (MarkerEditorView restyle) — COMPLETE: `MarkerEditorView.axaml` restyled to match the `PlotInspectorView` idiom. Outer card (`SystemChromeMediumLowColor`, CornerRadius=8, Padding=10); all labels use `TextBlock.label` (FontSize 10, Opacity 0.6); `UserControl.Styles` block copies the compact `TextBox`/`ComboBox`/`NumericUpDown` styles and the `ToggleButton.seg-btn` family (idle + `:checked` accent on `/template/ ContentPresenter`, Light1/Dark1 hover/press). Data readout block wrapped in a `Border`(`SystemChromeLowColor`, `CrfTileBorderBrush`, CornerRadius=6, Padding=8,6) — card look separating read-only values from editable fields; secondary lines at 0.55 opacity. Normalize `CheckBox` → `ToggleButton.seg-btn` ("Norm Z"); Multi/Δ `CheckBox`es → two `ToggleButton.seg-btn`s ("Multi" / "Δ"). Width=240→250. All VM bindings preserved; code-behind unchanged. Build 0W/0E.

Phase 7.1d-2 (plottype + label strip rebuild) — COMPLETE: `PlotInspectorViewModel` gains `PlotStructureChanged` event; raised from `OnPlotTypeChanged`, `AddTrace`, `RemoveTrace`, `OnTraceSecondaryAxisChanged`, and `OnLibraryChanged` (not from appearance/text handlers). `PlotContainerViewModel` constructor subscribes `Inspector.PlotStructureChanged += (s, e) => UpdateLabelStrips()` immediately after the `PlotNeedsRedraw` subscription. Switching PlotType (e.g. Smith → Table) now clears label strips immediately; add/remove trace and →R toggle update strip count/side live. Appearance changes (color/line/slider drags) remain on the revision-bump path with no flicker. Build 0W/0E. **Next: 7.1d-3** (marker editor polish).

Phase 7.1d-2 (combo shrink + label redraw) — COMPLETE: Two PlotInspector follow-ups. (A) All three trace-card rows (identity/line/symbol) now use explicit `<Grid.ColumnDefinitions>` with priority-shrink star sizing: col1 = `Width="*" MinWidth="54"` (signal combo / NUD+slider), col2 = `Width="1000*" MinWidth="40" MaxWidth="95"` (dB/Mag/Phase / color combos). At wide widths col2 pins to 95 and col1 takes the rest; as the inspector narrows, col1 shrinks to its 54 floor first, then col2 releases and shrinks. Signal combo gains `MinWidth="20"`; both sliders gain `MinWidth="20"`. (B) Live label-strip redraw fix: `LabelStripViewModel` gains `[ObservableProperty] int _appearanceRevision`; `AxisLabelControl` gains an `AppearanceRevision` direct property whose setter calls `InvalidateVisual()`; both `AxisLabelControl` instances in `PlotContainerView.axaml` bind `AppearanceRevision="{Binding AppearanceRevision}"`; `PlotContainerViewModel` constructor now bumps `st.AppearanceRevision++` on every `PlotNeedsRedraw` for all left and right strips. Build 0W/0E. **Next: 7.1d-3** (marker editor polish).

Phase 7.1d-2 (width-flex follow-up) — COMPLETE: PlotInspectorView is now width-flexible. (1) `PlotInspectorView.axaml`: `Width="430"` → `MaxWidth="430"` (control stretches to fill its host, capped at 430). (2) `DataDisplayView.axaml` inner `Border`: added `Width="430"` so the flyout/docked inspector renders at exactly 430 as before. (3) `PropertiesView.axaml` `ScrollViewer`: `HorizontalScrollBarVisibility="Auto"` → `Disabled` so the viewport constrains the inspector to the dock width rather than giving it unbounded measure room. Build 0W/0E. **Next: 7.1d-3** (marker editor polish).

Phase 7.1d-2 — COMPLETE: PlotInspectorView hosted in the Properties dock as a fourth context. (1) `PropertiesTool` gains `IsDataDisplayActive` + `PlotInspectorVm` (`[ObservableProperty]`) and `SetActiveDataDisplay(PlotInspectorViewModel?)` — clears all other contexts, sets header "Plot"/"Properties". `IsSchematicContextActive` now guards against all three non-schematic contexts. (2) `PropertiesView.axaml` adds `xmlns:ddv` + a `Panel IsVisible="{Binding IsDataDisplayActive}"` wrapping a `ScrollViewer` (horizontal auto) containing `ddv:PlotInspectorView DataContext="{Binding PlotInspectorVm}"`. (3) `WorkspaceViewModel` adds `_subscribedDisplayWindow`/`_displayInspectorHandler` fields and `RouteDataDisplayProperties(DataDisplayDocument?)` — subscribes to `DisplayWindowViewModel.PropertyChanged`, tracks `ActiveInspector`, calls `SetActiveDataDisplay`; unsubscribes on every non-DataDisplay activation path. `OnDocumentDockPropertyChanged` branches on `DataDisplayDocument` first; all other branches call `RouteDataDisplayProperties(null)` to unsubscribe. `OnProjectTreeSelectionChanged` guards also skip clobbering when `ActiveDockable is DataDisplayDocument`. Build 0W/0E; 1206 tests pass. **Next: 7.1d-3** (marker editor polish).

Phase 7.1f — COMPLETE: Data Display workspace/tree integration. (1) `OpenDataDisplayFileCommand` — file picker opens `.cdd` into a Content-pane tab (deduped); `NativeMenuItem` + in-window `MenuItem` added to File menu next to "Open Symbol…" / "New Data Display". (2) `WriteWorkspaceFile` persists open `DataDisplayDocument`s as `kind="datadisplay"` + covers active-doc path; `RestoreOpenDocuments` adds `"datadisplay"` case via fire-and-forget `OpenOrActivateDataDisplay`. (3) `WorkspaceScanner.Scan` enumerates loose files at workspace root via `BuildFileNode` (`.cws` excluded); root-level `.cdd` → `NodeKind.DataDisplayFile`. (4) `OpenNode` `DataDisplayFile` case opens via `OpenOrActivateDataDisplay`. Refactored `OpenDataDisplayFromFileAsync` delegates to `OpenOrActivateDataDisplayCoreAsync` (stream-or-path). Build 0W/0E; 1206 tests pass.

Phase 7.1e — COMPLETE: `.cdd` layout persistence. (1) `DataDisplayConfig.CurrentFormatVersion = 1` const + `FormatVersion` property (default 1 so clipboard JSON passes); `SaveAllAsync` writes `FormatVersion = CurrentFormatVersion`; `LoadAllAsync` throws `InvalidDataException` on mismatch — no partial load. (2) `DataDisplayView.axaml.cs OnLoaded` injects `SetSaveDataDisplayAsAction`/`SetOpenDataDisplayAction`/`SetGetWindowGeometryAction`; `DoSaveDisplayAsAsync` uses `StorageProvider.SaveFilePickerAsync` (`.cdd` filter); `DoOpenDisplayAsync` uses `OpenFilePickerAsync` + `await using var stream = file.OpenReadAsync()` (macOS security-scoped); errors surfaced via `ShowErrorAsync` (reuses `SaveChangesDialog` with OK-only). (3) Toolbar: `ContentSaveOutline` → `SaveDataDisplayCommand`, `FolderOpenOutline` → `OpenDataDisplayCommand`, preceded by a separator. `Ctrl/Cmd+S`/`O` not clobbered (global workspace shortcuts untouched). Build 0W/0E; 1206 tests pass.

Phase 7.1d-1 polish R5 — COMPLETE: Single slider-thumb fix in `PlotInspectorView.axaml`. Removed `Height="20"` from the `Slider` style so Avalonia's Fluent template keeps its natural thumb-centered height; replaced with `Margin="2,-7"` (negative vertical trims the layout footprint so line/symbol rows stay tight). Added `ClipToBounds="False"` to the nested col-1 `Grid ColumnDefinitions="30,*"` in both the line and symbol rows so the thumb can't be clipped if it slightly overhangs. Build 0W/0E. **7.1d-1 inspector look is now closed out; next is 7.1d-2** (Properties-dock surface).

Phase 7.1d-1 polish R4 — COMPLETE: Six fixes to `IconSelectButton` / `PlotInspectorView` / `App.axaml`. (1) Line-glyph bug: `CrfIconBrush` (`SolidColorBrush Color=SystemBaseMediumColor`) added to `App.axaml Application.Resources`; `Line Stroke` in the line-ISB `DataTemplate` bound directly to `{DynamicResource CrfIconBrush}` (not the defunct `Button.seg-btn Canvas Line` style which couldn't reach popup visual trees); both defunct canvas-line styles removed. (2) `HighlightSelected` styled property added to `IconSelectButton` (bool, default true); `ApplyHighlight()` now gates `active` class on `Highlight && HighlightSelected`; `ApplyHighlightSelected()` adds/removes `flat-select` class on `PART_ListBox`; `flat-select ListBoxItem:selected` style in popup `Border.Styles` keeps transparent background; all three trace-card ISBs set `HighlightSelected="False"`. (3) Col 0 widened `28→34` in all three rows; ISB margins `0,0,3,0→0,0,6,0` for clear right gap. (4) Slider style `Height 35→20`; both inline `TranslateTransform Y="-7.5"` removed; row heights now uniform. (5) `Border.traceCard` style gains `BorderBrush=CrfTileBorderBrush`, `BorderThickness=1`, `CornerRadius 4→6`. (6) Trash button (`TrashCanOutline`, 14px, `Classes="removeTrace"`) in card's `Grid.Column=1` `VerticalAlignment=Top`; `Button.removeTrace` style: transparent bg, no border, `CrfIconBrush` foreground, red on `:pointerover`; old `×` button removed from Z0 row. Build 0W/0E; 1206 tests pass. Next: **7.1d-2** (Properties-dock surface).

Phase 7.1d-1 polish R3 — COMPLETE: (A) `ComboBox.icon-pick` styling removed entirely. New `IconSelectButton` custom `TemplatedControl` (`src/Ui/DataDisplay/Controls/IconSelectButton.cs`) — StyledProperties: `ItemsSource`, `SelectedItem` (TwoWay default), `ItemTemplate`, `Highlight`; `OnApplyTemplate` finds PART_Button + PART_Popup + PART_ListBox; button click toggles popup; list selection sets `SelectedItem` + closes popup; `Highlight` adds/removes `active` class on PART_Button so the existing `seg-btn`/`seg-btn.active` idiom handles all visual states. ControlTheme defined in `PlotInspectorView.axaml` `UserControl.Resources` (Width=28, Height=22; Popup `Placement="Bottom"`, `IsLightDismissEnabled=True`; ListBox with inline `ListBoxItem` ControlTheme for hover/selected; Avalonia 12: `Popup.Placement` not `PlacementMode`). (B) Added `Button.seg-btn Canvas Line` style (grey) + `Button.seg-btn.active Canvas Line` style (White) so line-glyph strokes flip with accent state. (C) All three trace-card rows now share `ColumnDefinitions="28,*,95,26"` — identity row: matrix ISB(28) · signal combo(*) · YAxis(95) · →R(26); line/symbol rows: ISB(28) · nested `Grid(30,*)` with NUD+slider · color combo(95, HorizontalAlignment=Stretch) · blank(26). Slider right edge aligns with signal combo above. Color swatches use `Height="10"` (no fixed Width) + `HorizontalAlignment="Stretch"` to fill the 95-px column. Slider `Margin="2,0"`. Build 0W/0E; 1206 tests pass. Next: **7.1d-2** (Properties-dock surface).

Phase 7.1d-1 polish R2 — COMPLETE: (A) Inspector icon buttons match toolbar idiom — `Button.seg-btn` default icons use `SystemBaseMediumColor` via targeted `mi|MaterialIcon` and `ctl|PlotTypeGlyphControl` styles; active state uses accent via `/template/ ContentPresenter` (Background=SystemAccentColor, Foreground=White, hover=Light1, pressed=Dark1); white icon overrides on `.seg-btn.active` ensure glyphs and custom PlotTypeGlyphControl strokes flip to White. Smith/Polar PlotTypeGlyphControl Stroke no longer bound to `$parent[Button].Foreground`; driven by styles instead. (B) `ComboBox.icon-pick` style: Width=28, Padding=2, no chevron (`/template/ PathIcon IsVisible=False` + `/template/ Path IsVisible=False`), grey/transparent background; popup items centered. (C) VM — `LineModeItem` and `SymbolModeItem` classes added to `ComboItems.cs`; `PlotInspectorViewModel.LineModes` (Off + all `LineType`s) and `SymbolModes` (Off, Circle, Square) static lists; `TraceRowViewModel.SelectedLineMode` and `SelectedSymbolMode` computed properties (get=derive from LineEnabled/LineType/MarkerEnabled/SelectedMarkerTypeItem, set=drive them); `OnLineEnabledChanged`/`OnLineTypeChanged`/`OnMarkerEnabledChanged`/`OnSelectedMarkerTypeItemChanged` all call `OnPropertyChanged(nameof(SelectedLineMode/SelectedSymbolMode))`. (D) Trace card — line row and symbol row now `ColumnDefinitions="Auto,30,*,Auto"` (4 cols): icon-pick + NUD + slider + color-combo Width=52; separate enable toggles and style/shape combos removed (2 fewer combos per card). MatrixType identity-row combo also uses `Classes="icon-pick"` at Width=30. (E) Color combos Width=52 with 34px swatch. Build 0W/0E; 1206 tests pass. icon-pick used ComboBox restyle approach; verify chevron hiding in the running app. Next: **7.1d-2** (Properties-dock surface).

Phase 7.1d-1 polish — COMPLETE: (1) Base `ComboBox` style (no class) with FontSize 10, MinHeight 0, Padding 4,1, Height 22, VerticalContentAlignment Center — all combos now uniformly compact; `ComboBox.compact` kept as no-op. (2) MatrixType Width 52, LineType Width 52, MarkerType Width 52 — no left/right clipping. (3) Line-toggle now shows live dash-pattern preview via `Canvas/Line` with `StrokeDashArray` bound to `LineType` through `LTD` converter; Symbol-toggle shows selected marker icon via `mi:MaterialIcon Kind="{Binding SelectedMarkerTypeItem.Icon}"`. (4) New `src/Ui/DataDisplay/Controls/PlotTypeGlyphControl.cs` — Avalonia `Control` with `Kind` (Smith/Polar) + `Stroke` styled properties; Polar draws 2 concentric circles + H/V axes; Smith draws outer circle + real axis + R=1 circle + X=±1 arc circles clipped to unit circle. Smith/Polar header buttons now use `PlotTypeGlyphControl` (Rect=ChartLine, Table=TableLarge unchanged). (5) Both Line and Symbol rows use `ColumnDefinitions="Auto,30,*,46,52"` — sliders are same length, columns align; Slider `Margin` reduced to `4,0`; colour combos Width=46, style/marker combos Width=52, `HorizontalAlignment="Right"` dropped. Build 0W/0E; 1206 tests pass. Owner to hand-tweak `PlotTypeGlyphControl.cs` geometry. Next: **7.1d-2** (Properties-dock surface).

Phase 7.1d-1 — COMPLETE (pass 2): Segmented plot-type header (4 `Button.seg-btn` glyphs: `ChartLine`/`ChartArc`/`ChartDonut`/`TableLarge`, centered, `Classes.active` bound to `IsRectPlot`/`IsSmithPlot`/`IsPolarPlot`/`IsTablePlot`, commands `SetPlotType*Command`); `+ Trace` now left-aligned in Row 2 alongside Freq + Font-Size controls; `ToggleButton.trace-toggle` style (`:checked` for active look) replaces Line/Symbol checkboxes with equal-size glyph toggles (`VectorPolyline`/`ChartScatterPlot`); `ComboBox.compact` style (FontSize 10, reduced MinHeight/Padding) applied to MatrixType/YAxis/LineType/Format combos; MatrixType ItemTemplate → letter on `Border` (SystemBaseLowColor, CornerRadius=3, 18×16); LineType ItemTemplate → `Line` glyph with `StrokeDashArray` from `LineTypeToDashArrayConverter` (new `Converters/LineTypeToDashArrayConverter.cs`); MarkerType combo shrunk to Width=40; `PlotInspectorViewModel` gains `IsRectPlot`/`IsSmithPlot`/`IsPolarPlot` bool getters + `OnPlotTypeChanged` notifies all four + four `SetPlotType*Command` relay commands; build 0W/0E; 1206 tests pass. Smith/Polar icons are `ChartArc`/`ChartDonut` fallbacks — flagged for owner review. Next: **7.1d-2** (Properties-dock surface).

Phase 7.1c COMPLETE — splotRF Data Display engine ported (canvas + containers + tabs + toolbar + SNP library + Load Touchstone + flyout/docked inspector), splotRF-styled. Summary of 7.1c-3b additions: `TabHeaderView.axaml(.cs)` (double-click rename, close button); `SnpLibraryView.axaml(.cs)` (header with import button, drop hint, entry list, drag-drop, context menu); `SnpLibraryViewModel` gains `ImportCommand` + `System.Windows.Input` using; `DataDisplayView.axaml` replaced with full chrome — `UserControl.KeyBindings` (Ctrl+Meta for Add Plot/New Tab/Remove/Zoom In-Out/Actual Size/Fit All/Undo/Redo/Load Touchstone), in-document `StackPanel.Toolbar` (ChartLine · TabPlus · sep · MagnifyPlusOutline · MagnifyMinusOutline · Magnify · FitToPageOutline · sep · Undo · Redo), `Grid 180,4,*` with `SnpLibraryView` + `GridSplitter` + `TabControl(TabStripPlacement=Bottom)` hosting `TabHeaderView` + `PlotCanvasView` per tab + docked `PlotInspectorView` gated on `IsInspectorOpen`/`HasSingleSelection`; `DataDisplayView.axaml.cs` injects `SetOpenFileAction`/`SetGetCanvasSizeAction` + wires `SnpLibrary.ImportCommand`, `CopyToClipboardFunc`, `FindMissingFileAsync` on `Loaded`; `DataDisplayDocumentViewModel` stripped to `Window + IsDirty` (demo seed removed); build 0W/0E; 1206 tests pass. Next: **7.1d** (inspector restyle to the §2.8 merge + dual surface + marker polish).

Phase 7.1c-3a — COMPLETE: real canvas + containers + provider wiring (single tab); `PlotContainerView` ported (`src/Ui/Views/DataDisplay/PlotContainerView.axaml(.cs)`) with full move/resize/select code-behind and all four provider wires (`NextMarkerIndexProvider`/`FindMarkerInfoBoxVmProvider`/`ContainerProvider`/`SelectedMarkersProvider`); splotRF `DataDisplayView` ported and renamed `PlotCanvasView` (avoids collision with document-level `DataDisplayView`), with middle-pan/drag-select/scroll-zoom/background-deselect code-behind; `DataDisplayDocumentViewModel` now wraps `DisplayWindowViewModel` as `Window` property; 7.1b `CurrentPlot`/`HasPlots`/`InsertDemoPlotCommand` harness removed; `SeedDemoPlot()` seeds one Rect S21-dB plot into the first tab (TEMP 3a); document `DataDisplayView` hosts `PlotCanvasView` bound to `ViewModel.Window.ActiveTab` + temp "Add Plot" button; build green 0W/0E; 1206 tests pass.

Phase 7.1c-2 — COMPLETE: 7.1b render-only `PlotControl` replaced with splotRF's full interactive version (pan left-drag, zoom Ctrl+scroll, context menu, flyouts); `AxisLabelControl`, `DragSelectOverlay`, `DoubleToDecimalConverter` ported; five flyout/overlay views ported (`PlotInspectorView`, `AxesLimitsView`, `AxesLabelsFlyout`, `MarkerEditorView`, `MarkerInfoBoxView`); `PlotExporter` ported with `"circuitRF.pdf"/"circuitRF.svg"` app formats; harness updated with `ContentGrid`, `EnablePanning="True"`, `DoubleTapped→HandleDoubleTapAt`; canvas.Clear bug fixed (only clears when `_plot is null`); build green 0W/0E.

Phase 7.1c-1 — COMPLETE: splotRF view-model stack faithfully ported to `src/Ui/DataDisplay/ViewModels/` (namespace `CircuitRF.Ui.DataDisplay.ViewModels`); 21 VM files created; 3 model files added (`AppSettings.cs`, `DataDisplayConfig.cs`, `UndoRedo.cs`); `DataDisplayDocumentViewModel` rename complete; `RfCore.csproj` extended with `InternalsVisibleTo CircuitRF.Ui` for `SNP.CreateBroken`/`RefreshFrom`; `DisplayWindowViewModel.PerformCopy` stubbed (`// TODO 7.x`); build green 0W/0E; smoke tests pass.

Phase 7.1b — COMPLETE: splotRF plot model (`Misc`, `Axes`, `Marker`, `Plot`, `Trace`) + Skia renderers (`RenderTheme`, `PlotRenderer`, `AxesRenderer`, `TraceRenderer_MarkerRenderer`, `TableRenderer`) ported to `src/Ui/DataDisplay/`; font seam retargeted to IBM Plex (`SkiaFonts.PlexRegular`/`PlexBold`); color seam picks `RenderTheme.Light`/`Dark` from `ActualThemeVariant`; render-only `PlotControl` in `src/Ui/DataDisplay/Controls/`; demo `InsertDemoPlot` harness seeds a synthetic S21-in-dB Rect plot; build green.

Phase 7.1a — COMPLETE: `DataDisplayDocument`/`DataDisplayViewModel` (`src/Ui/DataDisplay/`), `DataDisplayView` (`src/Ui/Views/DataDisplay/`), `NewDataDisplayCommand` on `WorkspaceViewModel`, DataTemplate in `App.axaml`. New Data Display opens an `Untitled-Display-N` tab with an empty placeholder canvas; tears off and re-docks; closes cleanly; Ctrl+Shift+D / ⌘⇧D shortcut wired.

Standing instructions for `src/Ui`. Read with the root `CLAUDE.md`, the interaction spec
`docs/design/ui-design.md`, and the architecture/firewall note `docs/design/ui-architecture.md`. The UI is
how people drive the engine; it must never become the source of truth for simulation.

---

## Testing without the Avalonia runtime

Unit tests in `tests/Ui.Tests/` must be framework-free (no Avalonia app host, no UI thread). Here is what can and cannot be instantiated.

**Constructable without Avalonia:**
- `SchematicViewModel`, `SchematicEditModel`, `SchematicDocument`, `SymbolEditorDocument`, `SchematicSessionRegistry` — all pure C#, no Avalonia dependency.
- `DisplayWindowViewModel`, `DataDisplayDocumentViewModel`, `DataDisplayDocument` — confirmed by `DataDisplayVmSmokeTest.cs`. `DisplayWindowViewModel.SaveAllAsync(path)` is plain async disk I/O and works in tests; it also sets the dirty baseline via `CaptureBaseline()`. `HasUnsavedChanges()` is synchronous.
- `SchematicPersistence.SaveToFile` / `LoadFromFile` — disk I/O only.

**Requires Avalonia runtime (cannot be directly unit-tested):**
- `WorkspaceViewModel` — constructor calls `new CircuitRfDockFactory()`, `CreateLayout()`, `InitLayout()`, and posts to `Dispatcher.UIThread`. Never instantiate in tests.
- `SaveChangesDialog` and any `Window` subclass — require the Avalonia app host.
- Any `WorkspaceViewModel` method that calls `dlg.ShowDialog(owner)`.

**Pattern for testing `WorkspaceViewModel` logic:** use the "simulate" pattern — write a private static helper in the test class that replicates the relevant production logic using real types (empty lists are fine). See `HierarchySaveTests.SimulateSingleDocSave` and `CloseQuitSaveTests.PromptSaveBeforeClose_OrphanedOnly_NoCrash` for examples.

**`DataDisplayDocumentViewModel.IsDirty` is NOT wired to live edits** — nothing propagates `DisplayWindowViewModel.HasUnsavedChanges()` into it. The close/quit pipeline (and any test checking dirty state) must call `docVm.Window.HasUnsavedChanges()` directly. A brand-new `DisplayWindowViewModel` returns `false` from `HasUnsavedChanges()` until `SaveAllAsync` or `LoadAllAsync` has been called once to establish a baseline.

---

## Keyboard shortcut routing — focus-independent tunnel handler (SchematicView + SymbolEditorView)

**The problem:** Toolbar `Button` clicks steal keyboard focus from the canvas. `Window.KeyBindings`
(e.g. `<KeyBinding Gesture="Escape" Command="{Binding DisarmPlacementCommand}"/>`) are processed
**before** visual-tree routing begins and always mark `e.Handled = true`. A plain `protected override
OnKeyDown` on the `UserControl` is registered without `handledEventsToo`, so it is silently skipped after
the Window KeyBinding runs. A `KeyDown +=` handler on the canvas is also skipped (canvas is not in the
bubble path from a sibling toolbar button).

**The fix — one authoritative tunnel handler per editor view:**
```csharp
// In the UserControl constructor:
this.AddHandler(
    InputElement.KeyDownEvent,
    OnViewKeyDownTunnel,
    RoutingStrategies.Tunnel,
    handledEventsToo: true);
```

- `RoutingStrategies.Tunnel` fires **before** the focused element processes the key, so the View claims
  Esc/S/W/F/Z first and marks them handled — the canvas's bubble handler then naturally skips them (no
  double-processing).
- `handledEventsToo: true` fires even when `e.Handled` is already `true` (the Window KeyBinding pre-mark).
- Gate with `IsKeyboardFocusWithin` so the handler is a no-op when focus is on a different panel
  (Properties, Project Tree, etc.).
- Gate with `InlineEditBox.IsKeyboardFocusWithin` (schematic) so the inline TextBox keeps its own
  Esc/Enter behaviour.

**Schematic editor** (`SchematicView`): owns Esc (→ SetSelectTool or Selection.Clear), S, W, Z, F.
**Symbol editor** (`SymbolEditorView`): owns Esc (delegates to `vm.OnKeyDown` which handles text/pin/general
modes), S (→ SetActiveToolCommand "Select"), F (→ ZoomToFit).

**Do NOT add a `protected override OnKeyDown` on these views** — it is a dead path after a toolbar click
and causes double-handling if the tunnel handler is also present. The tunnel handler IS the single
authoritative path; the canvas's `KeyDown` handler remains for canvas-specific keys (Ctrl+C/X/V, F5,
Delete, R, nudge).

### Select All (Ctrl/Cmd+A) is per-editor and focus-gated — NO window-level binding (2026-06-25)
There is intentionally **no** window-level Ctrl+A binding and **no** Edit→Select All menu (both removed —
the menu's command was a dead no-op and its `InputGesture="Ctrl+A"` risked hijacking Ctrl+A in a docked
panel's text box). Each editor owns Ctrl/Cmd+A, fired only when that editor has keyboard focus, checking
`(Control | Meta)` so Cmd works on macOS:
- **Schematic** — `SchematicCanvas.OnKeyDown` → `vm.OnKeyDown` → `Key.A when ctrl` → `SelectAll()` (components +
  wires + canvas objects). Focus-gated because the canvas only receives keys when focused.
- **Symbol** — `SymbolEditorView` tunnel handler: `ctrl && Key.A && !IsTypingText` → `vm.SelectAll()` (all
  `EditableSymbol.Primitives`). Gated by `IsKeyboardFocusWithin`; suppressed while typing a text primitive.
- **Data Display** — `DataDisplayView.axaml` `Ctrl+A`/`Meta+A` KeyBindings → `Window.SelectAllCommand` →
  `DataDisplayViewModel.SelectAll()` (everything selectable: all plot containers **and** all marker info
  boxes). A focused `TextBox` inside the view consumes Ctrl+A first (select-all-text), so the binding doesn't
  hijack it; the Properties inspector lives in a separate dock, so its text boxes are unaffected.
Tests: `SymbolEditorViewModelTests.SelectAll_SelectsEveryPrimitive`, `DataDisplaySelectAllTests`. Ui 1539.

### Editor view grabs keyboard focus on tab activation (2026-06-25)
Bug: after switching Content tabs, shortcuts (Select All, nudges) didn't work until the user clicked the
canvas — the activated view had no keyboard focus. Fix via `IActivatableDocument` (`src/Ui/Commands/`):
`{ event ActivationFocusRequested; RequestActivationFocus(); ConsumeActivationFocus(); }`, implemented by
`SchematicDocument`/`SymbolEditorDocument`/`DataDisplayDocument` (sets a pending flag + raises the event).
`WorkspaceViewModel.OnDocumentDockPropertyChanged` (the canonical tab-switch hook — views stay realized, so
`OnAttachedToVisualTree` does NOT reliably re-fire on tab-switch) calls `RequestActivationFocus()` on the new
`activeDockable`. Each editor view, in its `DataContextChanged`, subscribes to `ActivationFocusRequested`
(focus when already bound) **and** checks `ConsumeActivationFocus()` (focus when it binds AFTER the request —
first open, view built on the next layout pass). Focus is deferred via `Dispatcher.Post(Background)` and
targets the canvas (`SchematicCanvasCtrl`/`SymbolEditorCanvasCtrl`) or — for the data display — the
`DataDisplayView` itself (`Focusable=true`, so its `UserControl.KeyBindings` fire). Contract test
`ActivationFocusTests`.

---

## Library Palette — catalog metadata + LibraryCatalog projection (Step 1 — done, updated for multi-category)

**`ComponentTypeRegistry`** (Avalonia-free, `src/Ui/Schematic/ComponentTypeRegistry.cs`) carries
**Palette metadata** on every `ComponentTypeInfo` entry:
- **`ComponentCategory`** enum — `Lumped`, `TransmissionLine`, `Microstrip`, `Sources`, `DataFiles`,
  `Terminals`, `Other`. All 11 built-ins are populated. `All`/`Common`/`RecentlyUsed` are virtual
  categories (filters in `LibraryCatalog`), not enum values.
- **`SearchTerms: IReadOnlyList<string>?`** — display name, type code, and aliases.
- **`IsCommon: bool`** — curated Common subset. True for R/L/C/V/VTone/Ground/Port.
- **`ExtraCategories: IReadOnlyList<ComponentCategory>?`** — additional categories a component belongs to.
  A component with `ExtraCategories = [TransmissionLine]` appears under both its primary category AND
  `TransmissionLine` in `ByCategory` filtering. `AllItems` still lists it once, sorted by the primary
  `Category`. Null means single-category (most built-ins). ZPort declares
  `ExtraCategories: [TransmissionLine]` as the mechanism demonstration.

**`LibraryCatalog`** (`src/Ui/Schematic/LibraryCatalog.cs`) — framework-free, headless; the single source
the Palette VM binds to:
- **`PaletteItem`** record — `{ Kind, PortCount, DisplayName, Category, SearchTerms, IsCommon, ExtraCategories }`.
  Bind to this, not `SymbolKind` directly (keeps v2 re-key catalog-internal, not a Palette rewrite).
- **`AllItems`** — stable ordered projection from registry (by primary category rank then display name).
  Multi-category items appear **once** under their primary category sort key.
- **`ByCategory(category)`** — **set-containment filter**: returns items where `Category == category` OR
  `ExtraCategories.Contains(category)`. A multi-category component appears under every category it lists.
- **`Common`** — virtual: items where `IsCommon = true`.
- **`RecentlyUsed(mru)`** — virtual: caller supplies `IReadOnlyList<SymbolKind>` (MRU list); returns items
  in that order, unknown kinds skipped.
- **`Search(query, category?)`** — case-insensitive substring over `DisplayName` + `SearchTerms` + category
  name; composes with an optional real-category filter (which uses the same set-containment).

**Developer-contribution point:** multi-category = set `ExtraCategories` in the registry entry. `ByCategory`
picks it up automatically — no Palette code changes.

Gate: 65 new tests; all 1042 tests green; firewall green.

---

## Dock 12.0.0.2 — ToolControl / DeferredContentControl tab-switch fix (FIXED app-wide)

**Root cause (historical):** `ToolControl` in Dock.Avalonia 12.0.0.2 uses `DeferredContentControl+ControlRecyclingDataTemplate` internally. On tab switch, `DeferredContentControl` retains the existing realized view and only updates DataContext. Views with `x:DataType` compiled bindings silently no-op on the wrong DataContext type → stale fallback content (e.g. "No workspace open").

**Fix (in `App.axaml`):** `CrfToolControlCachedContentTemplate` — the tool analog of `DockDocumentControlCachedContentTemplate`. Applied via `<Style Selector="dockCtrl|ToolControl"><Setter Property="Template">`. The template exactly mirrors the package ControlTheme chrome (ToolTabStrip at `DockPanel.Dock="Bottom"`; Border with `DockSurfacePanelBrush`/`DockBorderSubtleBrush`/`BorderThickness="1 0 1 0"`; PART names preserved; DockableControl wrapper) but replaces `DeferredContentControl` with a plain `ContentControl`. Avalonia re-resolves the App DataTemplate on each `Content` change, realizing the correct view for the new active dockable type.

**Both left ToolDocks are now tabbed:** `projectTreeDock` (Project Tree + Library Palette) and `propertiesDock` (Properties + Analyses). Tab switching works correctly for all pairs.

**On Dock version upgrades:** re-extract the ToolControl ControlTheme from the package source (Controls/ToolControl.axaml) and update `CrfToolControlCachedContentTemplate` in `App.axaml` to mirror the new chrome — change only `PART_ContentPresenter` (keep `DeferredContentControl → ContentControl`).

**If you add a new tool:** tabbed ToolDocks work correctly — place the new tool in whichever ToolDock makes sense for the UX. No per-tool isolation required.

---

## Dock 12.0.0.2 — tool tear-off window close crashes FactoryBase.CloseDockable (FIXED via CrfHostWindow)

**Symptom:** Tearing a **tool** panel (Properties, Analyses, Project Tree, Library/Palette) out into its own
floating window and then closing that window — either via the window's OS close box, or by closing the tool
tabs one-by-one down to empty — crashed the app with an unrecoverable `NullReferenceException` thrown from
`Dock.Model.FactoryBase.CloseDockable`. Document tear-off windows never had the problem.

**Root cause (instrumented, not guessed):** closing a tool float-out cascades closes — each child tool first
(these succeed), then the now-empty container `ToolDock`. By that final close the floating window's `RootDock`
is already stripped bare: `VisibleDockables.Count == 0`, `ActiveDockable`/`FocusedDockable == null`, and
`Window`/`Windows == null`. `FactoryBase.CloseDockable`'s window-management/collapse path dereferences one of
those nulls → NRE. The throw is **inside the library** (we don't control that code), and the exact null moves
depending on whether the close arrives as a separate empty-dock call (OS close box) or from inside the
last-tool collapse (closing tabs). Temp instrumentation in `CircuitRfDockFactory.CloseDockable` dumped the
full owner/root/window chain and confirmed the bare-floating-root state; it has since been removed.

**What did NOT work (and why it's not in the tree):** guarding `CloseDockable` to detach empty docks before
`base`, and wrapping `base.CloseDockable` in `try/catch (NullReferenceException)` + manual teardown. Both just
relocated the NRE (next time it surfaced directly in our own cleanup, posted via `Task.ThrowAsync` on the
dispatcher). Chasing a moving null through library teardown we can't see was the wrong layer. `CloseDockable`
is back to its clean original (confirm hook + `base.CloseDockable`).

**Fix (`src/Ui/ViewModels/Dock/CrfHostWindow.cs`):** a `HostWindow` subclass that overrides `OnClosing`. If
the floated layout contains any `ITool` (walks `Window?.Layout` recursively, all derefs null-guarded), it sets
`e.Cancel = true` — the tool float window's close box is **inert**; the user re-docks the panel by dragging its
tab back. Document float windows have no `ITool` in their layout, so they fall through to `base.OnClosing` and
close normally. Both host-window construction paths build it: `CircuitRfDockFactory.DefaultHostWindowLocator`
and `WorkspaceWindow.MainDockControl.HostWindowFactory` (the belt-and-suspenders pair) both `=> new
CrfHostWindow()`. This prevents the crashing entry point rather than patching the library's teardown.

**On Dock version upgrades:** re-test the tool tear-off close path (it may be fixed upstream — if so, this
workaround can be dropped and the close box restored). Verify `HostWindow.OnClosing(WindowClosingEventArgs)`,
`HostWindow.Window` (`IDockWindow`) and `IDockWindow.Layout` still exist with those names; if the floated-root
accessor was renamed, update the one line in `CrfHostWindow.FloatsAnyTool()`.

---

## Library Palette — glyph tile + inert list (Step 2 — done)

**`PaletteGlyphControl`** (`src/Ui/Controls/PaletteGlyphControl.cs`) — Skia `Control` using the
`ICustomDrawOperation` + `ISkiaSharpApiLeaseFeature` pattern (mirror of `SymbolEditorCanvas`):
- Takes `Kind: SymbolKind` (styled property; `AffectsRender`).
- Calls `BuiltInSymbols.Primitives(kind).Primitives` for geometry.
- Computes glyph bbox via `SymbolGeometry.ComputeBb`; derives zoom+pan so the glyph fits centered
  with 12% padding (same math as `SymbolEditorCanvas.ZoomToFitInternal`).
- Calls `SchematicRenderer.DrawSymbol(canvas, prims, compX:0, compY:0, R0, mirrorX:false, panX, panY, zoom, theme)`
  — the exact same glyph-only call the symbol editor uses. **No second renderer.**
- Transparent background (the hosting tile supplies the tile background).
- Subscribes to `ThemeService.ThemeChanged` for reactive redraws; uses `SchematicRenderTheme.FromTheme`.

**GOTCHA — never `canvas.Clear(SKColors.Transparent)` in a transparent-overlay custom-draw op (Windows
desktop punch-through, 2026-06-25).** `SKCanvas.Clear` uses **Src** blend mode: it REPLACES the leased
region with fully-transparent pixels, erasing the tile background Avalonia already composited behind the
control. On macOS the opaque window backing masks it; on Windows the cleared pixels punch through to the
desktop (the Library Palette rendered see-through). A glyph-only overlay must draw on TOP of the existing
composited content — do not clear at all. (`PlotControl`'s `Clear(SKColors.Transparent)` is safe: it only
fires in the null-plot branch and sits over the opaque parent DataDisplay canvas, never window chrome.)

**`PaletteTile`** (`src/Ui/Controls/PaletteTile.axaml(.cs)`) — `UserControl` (DataContext = `PaletteItem`):
- Layout: `StackPanel` → square `Button` (60×60) containing a 50×50 `PaletteGlyphControl` + `TextBlock` caption.
- `IsArmed: bool` styled property exists; **step 4 drives it** (nothing drives it now).
- Tooltip: `StackPanel` with `DisplayName` (semibold) + `Category` line.
- Caption: `DisplayName` at 10pt, centered, `TextTrimming="CharacterEllipsis"`, max 68px wide.

**`PaletteTool`** (`src/Ui/ViewModels/Dock/PaletteTool.cs`) — `Tool` with `Items = LibraryCatalog.AllItems`.
Tabbed with `ProjectTreeTool` in `projectTreeDock` in `CircuitRfDockFactory` (left column, upper dock).
Title = "Library". Tab switching works correctly via `CrfToolControlCachedContentTemplate` — see Dock fix above.

**`PaletteToolView`** (`src/Ui/Views/Palette/PaletteToolView.axaml(.cs)`) — `ScrollViewer` → `ItemsControl`
with `WrapPanel`, `DataTemplate DataType="PaletteItem"` → `PaletteTile`. Inert; step 3 replaces with
column-driven grid + category header.

**Spine invariants honored:**
- Glyph-only: `DrawSymbol` draws primitives; no pin pass, no label pass called separately.
- `DrawSymbol` reused directly — no second renderer.
- Auto-scale + center via `SymbolGeometry.ComputeBb` + padding math.
- Theme-driven colors via `SchematicRenderTheme`; no literal colors in any tile code.
- `IsArmed` exists but nothing drives it; no placement wired.

Gate: all 1037 tests green; firewall green (no SKColor in framework-free models; Skia only in `src/Ui`).

---

## Library Palette — responsive grid + header (Step 3 — done)

**`PaletteTool`** extended with filter/search state and computed `DisplayedItems`:
- **`PaletteCategoryEntry`** / **`PaletteCategoryKind`** (in `PaletteTool.cs`) — category selector for
  the ComboBox; covers virtual (All/Common/Recently Used) and real `ComponentCategory` values.
- **`Categories: IReadOnlyList<PaletteCategoryEntry>`** — stable ordered list for the header ComboBox:
  All · Common · Recently Used · (real categories that have ≥1 item, in catalog sort order).
  `TransmissionLine` → "Transmission Line"; `DataFiles` → "Data Files" in display names.
- **`SelectedCategory`** / **`SearchQuery`** — `[ObservableProperty]`; drive all computed properties.
- **`DisplayedItems: IReadOnlyList<PaletteItem>`** — computed on demand via `LibraryCatalog`:
  - Real category → `LibraryCatalog.Search(query, category)` (search composes within the category)
  - Virtual + no query → `AllItems` / `Common` / `RecentlyUsed(emptyMru)` respectively
  - Virtual + query → `LibraryCatalog.Search(query)` across All (search overrides virtual filter)
  - MRU is `Array.Empty<SymbolKind>()` for step 3; persistence is step 4.
- **`HasNoItems`** / **`HasSearchQuery`** — boolean flags updated via partial callbacks; drive AXAML visibility.
- **`ClearSearchCommand`** — sets `SearchQuery = ""`.

**`PaletteToolView`** (`src/Ui/Views/Palette/PaletteToolView.axaml`) — rewritten:
- **Header** (row 0): `StackPanel` with category `ComboBox` (full-width, `SelectedItem` two-way) + search
  `TextBox` (`PlaceholderText="Search…"`, padded for overlaid icons) + magnifier icon overlay (left,
  non-hit-test) + clear `Button` overlay (right, `IsVisible="{Binding HasSearchQuery}"`).
- **Content** (row 1): `Grid` — "No matching components." `TextBlock` (visible when `HasNoItems`) +
  `ScrollViewer`(`HorizontalScrollBarVisibility="Disabled"`) → `ItemsControl` → `WrapPanel` bound to
  `DisplayedItems` (hidden when empty).

**Width-driven column count — one rule for dock + tear-off:**
`columns = max(1, floor(availableWidth / 74))` is implicit in `WrapPanel` with fixed-width tiles (68px +
6px margin). `HorizontalScrollBarVisibility="Disabled"` ensures the scroll viewer's viewport width is the
`WrapPanel`'s measure constraint. Dock default (~160px) → ~2 columns; torn-off + widened → more. No
docked-vs-floating special-case.

Gate: all 1037 tests green; firewall green.

---

## Library Palette — placement state machine (Step 4 — done)

**App-level armed state:**
- **`PendingPlacement`** (`src/Ui/Schematic/PendingPlacement.cs`) — `sealed record (SymbolKind Kind, int PortCount, SymbolRotation Rotation = R0)`. Null on the service means nothing is armed.
- **`PlacementService`** (`src/Ui/Schematic/PlacementService.cs`) — framework-free `ObservableObject`. `Pending: PendingPlacement?`. `Toggle(kind, portCount)` (arm/disarm/switch), `Disarm()`, `Rotate(clockwise)`. Owned by `WorkspaceViewModel.PlacementService`.

**Tile arming (L1):**
- **`PaletteTileVm`** (in `PaletteTool.cs`) — per-tile `ObservableObject` wrapper: `Item: PaletteItem`, `[ObservableProperty] bool IsArmed`, `ICommand ArmCommand` (calls `PlacementService.Toggle`).
- **`PaletteTool.SetPlacementService`** — subscribes to `Pending` changes; calls `UpdateArmedState()` which stamps `IsArmed` on all current tile VMs. `DisplayedItems` now returns `IReadOnlyList<PaletteTileVm>`.
- **`PaletteTile.axaml`** — `x:DataType="PaletteTileVm"`, `Button.Command="{Binding ArmCommand}"`, `Classes.armed="{Binding #TileRoot.IsArmed}"` → accent background style; bindings updated to `Item.Kind/DisplayName/Category`.
- **`PaletteToolView.axaml`** — DataTemplate `DataType` → `PaletteTileVm`; `IsArmed="{Binding IsArmed}"` on tile.
- **`WorkspaceViewModel`** — `public PlacementService PlacementService { get; } = new()`, injected into `PaletteTool` (+ after each `CreateDefaultLayout()` call), `[RelayCommand] DisarmPlacement()`.
- **`WorkspaceWindow.axaml`** — `<KeyBinding Gesture="Escape" Command="{Binding DisarmPlacementCommand}"/>`.

**Ghost-follow + rotate (L2):**
- **`SchematicViewModel.SetPlacementService`** — subscribes to `Pending`. When `Pending` non-null: activates `Tool.Place` with correct symbol/rotation/portCount on THIS canvas (also clears conflicting drag/wire state). When same kind but rotation changes: patches `Overlay.Ghost` in-place (preserves X/Y). When `Pending` null: calls `SetSelectTool()`. Called at all 4 document-creation sites in `WorkspaceViewModel`.
- **R/Shift-R rotation** — when `ActiveTool == Tool.Place` and `_placementService` is set, routes through `PlacementService.Rotate()` so ALL open canvases update simultaneously. Falls back to direct `RotateSelection` for keyboard-initiated placement (P key).
- **Cursor** — `Tool.Place` now maps to `StandardCursorType.Cross` in `SchematicCanvas.UpdateCursor`.
- **Esc** — canvas `OnKeyDown` passes Esc to VM → `SetSelectTool()` locally; `SetSelectTool()` also calls `PlacementService.Disarm()` (if `ActiveTool` was `Tool.Place`) → `Pending = null` → all canvases exit Place mode + tile un-highlights. **Critical gotcha:** the ARM state lives in `PlacementService.Pending`, NOT in the VM's `ActiveTool` enum. Setting `ActiveTool = Select` alone leaves `Pending` non-null — the tile stays highlighted and other canvases stay armed. Always clear via `Disarm()`. The feedback-loop guard: `Disarm()` is called *after* `ActiveTool` is set to `Select`, so `OnSvcPropertyChanged(Pending=null)` sees `ActiveTool != Tool.Place` and does not re-enter. `Disarm()` when `Pending` is already null is a no-op (CommunityToolkit.MVVM `SetProperty` no-change guard).

**Commit + stay-armed + MRU (L3):**
- **Stay-armed** — `HandlePlacePress` does not call `SetSelectTool()` after commit; `Tool.Place` persists; ghost continues from cursor.
- **`SchematicViewModel.ComponentPlaced`** event — `Action<SymbolKind>`, fired in `HandlePlacePress` after each commit.
- **`_placementPortCount`** — stored on VM; set by service path; used in `HandlePlacePress` for variadic types (Sdd/ZPort) so the palette-specified PortCount is honoured.
- **`AppPreferences.RecentlyPlaced: List<string>?`** — SymbolKind as string, MRU cap 12, saved in `preferences.json`.
- **`WorkspaceViewModel.OnComponentPlaced`** → `PushMruPlaced` — dedup+front+cap; calls `PaletteTool.SetMru(_recentlyPlaced)` live and saves preferences.
- **Recently-Used category** — `PaletteTool.SetMru` sets `_mruList`; `ComputeRawItems` uses it for `RecentlyUsed` category; live-updated on each commit.
- **Connectivity on commit** — reuses existing on-`P` union via `BuildRenderModel` after `PlaceComponentCommand.Execute`. No second connectivity path.

**Scope fence:** arm/ghost/rotate/commit/connect/stay-armed/MRU only. Drag-and-drop is step 5.

Gate: all 1037 tests green; firewall green.

---

## Library Palette — system drag-and-drop (Step 5 — done)

**DnD and click-arm converge on ONE commit:**
- **`SchematicViewModel.CommitPlacement(kind, portCount, rotation, worldX, worldY, mirrorX=false)`** —
  extracted shared commit: places `EditableComponent` (auto-name + defaults), runs the on-`P` connectivity
  union (`BuildRenderModel` after `Execute`), one undoable `PlaceComponentCommand`. Both click-arm
  (`HandlePlacePress`) and DnD drop call it — no duplicated commit logic.
- **`SchematicViewModel.CurrentPlacementRotation`** — public property exposing `_placementRot` so the
  canvas drop handler can use the last-used rotation for the drop.

**Drag payload:**
- **`PaletteDragPayload(SymbolKind Kind, int PortCount)`** (`src/Ui/Schematic/PaletteDragPayload.cs`) —
  `sealed record` carrying the catalog item. Holds:
  `static readonly DataFormat<PaletteDragPayload> Format = DataFormat.CreateInProcessFormat<PaletteDragPayload>("circuitrf/palette-item")`
  (Avalonia 12 in-process typed DnD format). Two instances created with the same identifier string compare
  equal (`DataFormat` equality is identifier-based), so source and sink can independently reference it.

**Canvas drop target (Layer 1):**
- `DragDrop.SetAllowDrop(this, true)` + `AddHandler(DragDrop.DragOverEvent, ...)` + `AddHandler(DragDrop.DropEvent, ...)` in
  `SchematicCanvas` constructor.
- `OnPaletteDragOver`: `e.DataTransfer.Formats.Contains(PaletteDragPayload.Format)` → `DragDropEffects.Copy`;
  all other formats → `DragDropEffects.None` (foreign drags silently ignored).
- `OnPaletteDrop`: reads payload via `foreach (var item in e.DataTransfer.Items) { item.TryGetRaw(Format) }`;
  calls `CommitPlacement` at the snapped drop world point with `_editContext.CurrentPlacementRotation`.

**Avalonia 12.0.3 DnD API (changed from older Avalonia — reference this on any DnD work):**
- `DragEventArgs.DataTransfer: IDataTransfer` (NOT `e.Data`; `IDataObject` was removed)
- `IDataTransfer.Formats: IReadOnlyList<DataFormat>` / `IDataTransfer.Items: IReadOnlyList<IDataTransferItem>`
- `IDataTransferItem.TryGetRaw(DataFormat) → object?`
- `DataFormat.CreateInProcessFormat<T>(string identifier)` — in-process typed format (no serialization)
- `DataTransfer` (concrete) / `DataTransferItem` (concrete): `item.Set(DataFormat<T>, T)` then `transfer.Add(item)`
- `DragDrop.DoDragDropAsync(PointerPressedEventArgs, IDataTransfer, DragDropEffects)` (NOT `DoDragDrop`)

**Tile drag source (Layer 2):**
- **`PaletteTile.axaml.cs`** — `PointerPressed` stores the event args; `PointerMoved` detects 5 px
  threshold (Euclidean), clears stored args, builds `DataTransferItem.Set(Format, PaletteDragPayload)` +
  `DataTransfer.Add(item)`, calls `await DragDrop.DoDragDropAsync(pressArgs, transfer, Copy)`.
  `PointerReleased` clears stored args. `DataContext` is `PaletteTileVm`; payload is `vm.Item.Kind` +
  `vm.Item.PortCount`.

**Invariants:**
- Last-used rotation for drop — raw OS drag can't rotate mid-drag; `CurrentPlacementRotation` is the single source.
- Click-arm unaffected — DnD is purely additive; the step-4 arm/ghost/rotate/Esc path is unchanged.
- Foreign drags silently rejected (`DragDropEffects.None` in `DragOver`; payload null-check in `Drop`).
- Drop works on any open schematic (all canvases are registered drop targets independently).

Gate: all 1037 tests green; firewall green.

---

## Library Palette — ghost pins + DnD ghost + grid polish (Polish step — done)

**Ghost shows pins (L1):**
- **`PlacementGhost`** (`src/Ui/Schematic/SchematicOverlay.cs`) gains `int PortCount = 2` — carries the
  port count for variadic devices (ZPort/Sdd) so the renderer knows how many pins to draw.
- Both `PlacementGhost` construction sites in `SchematicViewModel` pass `_placementPortCount` (already tracked
  since step 4).
- **`SchematicRenderer.DrawOverlay`**: after `DrawSymbol` for the ghost body, iterates
  `SymbolPortDefs.For(ghost.Symbol, ghost.PortCount)` and draws a small solid square at each port via the same
  `LocalToPixel` transform. Uses `PortBoxHalf` for size, `theme.GhostBody` color, no path effect (body is
  dashed; pins are solid). Rotation moves pins correctly (the same `LocalToPixel` math that `DrawPortMarkers`
  uses). Tiles remain glyph-only; pin squares are on the schematic ghost only.

**DnD ghost follows cursor (L2):**
- **`SchematicCanvas.OnPaletteDragOver`** now extracts the payload (`TryGetRaw`) on every drag-over event and
  sets `_editContext.Overlay = overlay with { Ghost = new PlacementGhost(sx, sy, kind, rotation, mirrorX=false, portCount) }`,
  snapped to `EditModel.SnapToGrid`. Ghost is invalidated each tick → ghost (with pins) follows the cursor.
- **`DragLeaveEvent` handler** (`OnPaletteDragLeave`) added — clears `overlay.Ghost` when drag exits canvas.
- **`OnPaletteDrop`** clears the ghost at the top before processing.

**Grid tightening + subtle border (L3, superseded by Fix v2 below):**
Tile `StackPanel` margin tweak to `2 3 2 3` — 1 px change, imperceptible. `Button` gains `BorderThickness=1` /
`BorderBrush=DockBorderSubtleBrush` / `CornerRadius=3`.

Gate: all 1042 tests green; firewall green.

---

## Library Palette — DnD root-cause fix + real grid tightening (Fix v2 — done)

**Root cause (Button-eats-drag):** `Button` captures the pointer on `PointerPressed` and handles the
press→move gesture for its own click mechanic. The tile's drag-source handlers lived on the outer `UserControl`,
which never received an owned press→threshold gesture because the `Button` already owned the pointer. Result:
`DoDragDropAsync` was never called, no `DragOver` fired, no ghost appeared, no drop landed.

**Fix — single pointer owner:**
- **`PaletteTile.axaml`** — `Button` replaced by a plain `Border` (`x:Name="TileGlyph"`). The `Border` is not
  a button control and does not capture the pointer. `Classes.armed` moves to the `Border`. Styles use
  `Border#TileGlyph` selectors: `SystemBaseLowColor` 1 px border (unarmed), `:pointerover` tint
  (`SystemChromeMediumColor`), `.armed` accent background + border, `.armed:pointerover` light accent.
  No `Command` attribute — arm is handled in code-behind.
- **`PaletteTile.axaml.cs`** — three handlers on the `UserControl` (events bubble from the `Border`):
  - `PointerPressed`: record `_pressArgs`, set `_dragOccurred = false`. No capture.
  - `PointerMoved`: detect 5 px threshold; set `_dragOccurred = true`, clear `_pressArgs`, call
    `DoDragDropAsync(savedArgs, transfer, Copy)`.
  - `PointerReleased`: if `_pressArgs` is still set (no drag) → call `vm.ArmCommand.Execute(null)` (arm toggle).
  All `Console.Error` `[DnD]` instrumentation removed.
- **`SchematicCanvas.cs`** — all `Console.Error.WriteLine` DnD logs removed from `OnPaletteDragOver` and
  `OnPaletteDrop`. Canvas-side drop handling was already correct.

**Real grid tightening:**
- `StackPanel Width="60"` (was 68), `Margin="1 2 1 2"` (was 2 3 2 3) → slot = 62 px (was ~74 px; visibly tighter).
- `Border` 52×52 px (was `Button` 60×60), glyph 44×44 (was 50×50). Column rule: `floor(availableWidth / 62)`.
- Border brush `SystemBaseLowColor` (guaranteed system resource, resolves in all Avalonia themes; renders visibly).

**Key gotcha (do not reintroduce):** never wrap the tile glyph in a `Button` or any pointer-capturing control.
`Button` (and `ToggleButton`) capture the pointer on press — this consumes the press→move gesture before
`DoDragDropAsync` can be called, silently killing the drag. Use a plain `Border` or `Panel` for the clickable
area and handle arm + drag entirely in pointer handlers on the `UserControl`.

Gate: all 1042 tests green; firewall green.

---

## Library Palette — macOS DnD crash fix + visible border (DnD crash fix — done)

**Root cause (macOS NSPasteboard crash):** `DataFormat.CreateInProcessFormat<T>(...)` is an in-process-only
format. On macOS, a real system drag goes through NSPasteboard — but an in-process format writes nothing to
it. AppKit detects a drag image with 0 pasteboard items and throws an uncaught NSException → app terminates.
The crash trace: `NSDraggingSession … 'There are 0 items on the pasteboard, but 1 drag images'`.

**Fix — text pasteboard format (mirrors SchematicClipboard):**
- **`PaletteDragPayload.cs`** — the `Format = DataFormat.CreateInProcessFormat<...>` field is **removed**.
  Instead the record adds `Serialize() → "circuitrf-palette:{Kind}:{PortCount}"` and
  `static bool TryParse(string?, out PaletteDragPayload)` that accepts **only** strings with the
  `circuitrf-palette:` prefix (foreign-text guard — random text drags are ignored silently).
- **`PaletteTile.OnTilePointerMoved`** — `transferItem.Set(DataFormat.Text, payload.Serialize())` instead
  of the in-process format. Everything else unchanged.
- **`SchematicCanvas.OnPaletteDragOver` / `OnPaletteDrop`** — reads `TryGetRaw(DataFormat.Text)`, calls
  `TryParse`; strings without the prefix → `DragDropEffects.None` / ignored.

**Tile border visibility fix:** `SystemBaseLowColor` (~12% opacity) replaced with `SystemBaseMediumLowColor`
(~38%) in `PaletteTile.axaml` → tile borders are now visibly readable in light and dark without fighting
the armed accent.

**Critical rule — palette DnD must use a platform pasteboard format:**
`DataFormat.Text` (or a `DataFormat.CreateBytesPlatformFormat` bytes format) writes to the native pasteboard.
`DataFormat.CreateInProcessFormat<T>` does NOT — it crashes macOS system DnD. The working pattern for DnD
payloads is a prefix-guarded serialized string on `DataFormat.Text`, exactly like `SchematicClipboard`.

Gate: all 1042 tests green; firewall green.

---

## Library Palette — tile border fix + T-junction drag-follow (BorderAndDragFollow — done)

### Color-vs-Brush on BorderBrush (GOTCHA — do NOT reintroduce)
`BorderBrush` requires a **Brush** object. `{DynamicResource System*Color}` keys (including
`SystemBaseLowColor`, `SystemBaseMediumLowColor`, etc.) resolve to a `Color` struct, NOT a
`SolidColorBrush`. Avalonia's `Background` property has an implicit `Color`→`Brush` conversion;
`BorderBrush` does **not** — the assignment silently produces no border regardless of which `*Color` key
is used, no error is raised. **Rule:** always use a `SolidColorBrush` application resource for
`BorderBrush`. `CrfTileBorderBrush` (`#55808080`, 33% opacity neutral gray, defined in `App.axaml`) is
the palette tile border resource. Do NOT reference `System*Color` keys on `BorderBrush` anywhere.

### Pin-on-wire-body drag-follow (T-junction follow — done)
When a component pin is placed on a wire's mid-span (T-junction), both the live drag path and the commit
path now re-route that wire so the connection survives the move:
- **Detection:** `PointOnSegmentInterior` + `ConnectTolerance` — the same single connectivity predicate
  used by `BuildRenderModel.IsConnected`. No second predicate.
- **Re-route:** `RouteBodyFollow(orig, nx, ny)` in `SchematicViewModel` — routes `orig[0] → P' → orig[^1]`
  via two `OrthogonalRoute` legs stitched at the new port position, then `SimplifyWirePoints`. Mirrors
  `RouteStem` (the wire-segment stem-follow re-route); do NOT invent a parallel re-router.
- **Undo:** folded into the same `MoveCommand` (`followWireSnaps`) so one Undo restores the component and
  every followed wire. Mirrors the existing endpoint-follow.
- **Perf:** O(N × S); only checks `_dragUnselectedWirePoints` (snapshotted at drag-start). No O(N²) scan.
- `BuildPortMoves` was fixed to pass `cs.Component.PortCount` to `SymbolPortDefs.For` (was using default=2,
  which silently gave wrong ports for variadic Sdd/ZPort components).

See `docs/design/placement-connectivity-and-drag-follow.md` for the full design note.

Gate: all 1038 tests green; firewall green.

---

## Pin-on-pin connectivity detection (PinOnPinConnectivity — done)

**Root cause:** `BuildRenderModel.IsConnected` was checking only `WirePointHash` (wire vertices). Two
component ports coincident with no wire both reported Unconnected; no junction dot appeared.

**Fix — single connectivity source extended to ports:**
- **`IsConnected`** now uses `conPointCounts >= 2`: the tested port contributes 1 to its P-cell's count;
  `cnt >= 2` means something else (another port OR a wire vertex) is also there → connected.
  The wire-body fallback scan (`PointOnSegment`) is unchanged for the rare port-on-wire-body case.
- **Port-coincidence dot pass** added at the end of `ComputeConnectivityGeometry`: after the wire
  auto-dot loop, iterates all component ports and emits a junction dot at any P-cell where
  `conPointCounts >= 2` OR `PointOnSegmentInterior` (port on wire body). Skips cells already covered
  by a wire auto-dot (no double-dots). Uses the same `segList`/`segIndex` and `QuantKey` already built
  in that pass — O(N), no new data structures.

**"Exclude self" invariant:** `conPointCounts[key] >= 2` is the correct threshold because every port
contributes exactly 1 to its own P-cell. A lone port has count = 1, so `>= 2` requires at least one
other endpoint. Do not use `> 0` here — that would mark a lone port as connected to itself.

**No double-dots:** `autoDotKeys.Contains(key)` skip in the port-coincidence pass ensures a P-cell
already covered by a wire T-junction or corner auto-dot never gets a second dot.

**Oracle (permanent):** `PinOnPinConnectivityTests.cs` — three headless assertions:
- `PinOnPin_BothPortsConnected_ExactlyOneDot`: two coincident ports → Connected + one dot.
- `PinOnWireVertex_PortConnected_Control`: port on wire vertex → Connected (was already correct).
- `LonePort_StaysUnconnected_NoDot`: lone port → Unconnected / no dot (anti-over-connect guard).

Gate: all 1041 tests green; firewall green.

---

## Drag invariant — auto-wire on pin-on-pin separation (DragInvariant Layer 3 — done)

The governing invariant ("a connection, once made, survives any drag") now covers all four cases:

**Case 2 (pin-on-pin → auto-wire):** When a component drag separates two pins that were in direct contact
(no wire between them), an auto-wire is created so the connection becomes a wired contact rather than
breaking.

**Implementation in `SchematicViewModel`:**
- **`PinOnPinContact` record struct** — snapshot of a pin-on-pin contact at drag start: `(StationaryX,
  StationaryY, MovingCompId, MovingPortIndex)`.
- **`_dragPinOnPinContacts: List<PinOnPinContact>?`** — cleared in `ClearDragState`; populated in
  `SnapshotDragStartPositions`.
- **Snapshot (`SnapshotDragStartPositions`):** after building `_dragUnselectedWirePoints`, iterates all
  moving component ports; skips ports already on a wire (Case 1 — handled by follow-wires); records each
  coincident pair with an unselected port. O(moving ports × wires × unselected ports).
- **Live preview (`UpdateDragOverlay`):** for each contact whose moving port has separated from the
  stationary pin, inserts a synthetic route keyed `"pop-preview-N"` into `wireOverrides` — the renderer
  draws it as a live preview wire throughout the drag. `wireOverrides ??= new()` handles the component-
  drag-only case (no wire drag in progress).
- **Commit (`CommitDragAsCommand`):** for each separated contact, builds an `EditableWire` via
  `WireGeometry.OrthogonalRoute(stationaryPin → movingPinEndPos)` and wraps it in a `PlaceWireCommand`.
  Auto-wires are chained onto the `MoveCommand` via `new CompositeCommand(finalCmd, wc)`. One Undo
  removes every auto-wire AND restores the component to its pre-drag position.
- **No-wire if still coincident:** both the preview and commit skip contacts where the drag kept the
  pins touching (drag that lands them on the same P-cell forms no wire).

**Key invariants:**
- Reuses `WireGeometry.OrthogonalRoute` (the same routing primitive as Case 1 follow-wires).
- Reuses `CoincidentPoints`/`ConnectTolerance` — no second connectivity predicate.
- `autoWireCmds` and `mergeCmd` are mutually exclusive (`mergeCmd` requires `compSnaps.Count == 0`).
- Does not drag the stationary component — only a wire is formed (no rigid coupling).

**Oracle (permanent):** `DragInvariantOracleTests.cs` — all four cases green:
- `Case1a_ComponentDrag_WireEndpointFollowsPin_StaysConnected`: endpoint follow.
- `Case1b_ComponentDrag_TJunctionBodyFollowsPin_StaysConnected`: T-junction body follow.
- `Case2_ComponentDrag_PinOnPinSeparates_AutoWireConnectsBothPins`: auto-wire on separation.
- `Case3_WireDrag_ConnectedEndpointStaysPinnedToComponentPin_StaysConnected`: wire drag pin pinned.

Design doc: `docs/design/placement-connectivity-and-drag-follow.md` (rev 5).

Gate: all 588 tests green; firewall green.

---

## Drag invariant — shared-point rule (DragFollowSharedPoint — done)

**Case 4 (shared-point disambiguation):** When a moving pin starts coincident with BOTH a stationary
component pin AND a wire endpoint (three things at one point), the stationary connection wins —
the wire endpoint must NOT follow the moving pin. A new auto-wire forms to keep the moving component connected.

**Root cause (two faults):**
1. `UpdateConnectedWireEndpointsLive` and the follow block in `CommitDragAsCommand` matched a wire
   endpoint to the moving port's ORIGINAL position via `CoincidentPoints(orig[k], ox, oy)`. A wire
   ending at the shared point coincided with the moving pin's start position, causing the endpoint to
   follow the moving component off the stationary pin.
2. `SnapshotDragStartPositions` skipped pin-on-pin recording when the moving port was "already on a
   wire" (`onWire` guard). This suppressed the compensating auto-wire even though the wire was not
   actually going to follow (fault 1 mis-attributed the follow).

**Fix in `SchematicViewModel`:**
- **`IsPointHeldByStationaryPin(x, y)`** — new private helper; returns true if any UNSELECTED component
  port coincides with (x, y) within `ConnectTolerance`. Handles selected/unselected correctly: dragging
  a component that owns the wire endpoint is unselected-free at that point, so the follow still works.
- **`UpdateConnectedWireEndpointsLive`** — added `&& !IsPointHeldByStationaryPin(orig[k].X, orig[k].Y)`
  guard to both endpoint-follow checks. Wire endpoint stays pinned when a stationary pin holds the same point.
- **`CommitDragAsCommand` follow block** — same `IsPointHeldByStationaryPin` guard on both endpoints.
- **`SnapshotDragStartPositions`** `onWire` skip — changed from `if (onWire) continue` to
  `if (onWire && !IsPointHeldByStationaryPin(wx, wy)) continue`. The pin-on-pin contact is now recorded
  (and the auto-wire formed) even when a wire endpoint is present, if a stationary pin also holds the point.

**Key invariant:** a wire endpoint held by a stationary (unselected) pin is treated as pinned, exactly
like the wire-drag `IsWireEndpointConnectedToUnselected` pinning rule. A moving pin merely starting
coincident there does not override a stationary connection.

**Preserved cases:**
- Case 1a (genuine endpoint follow): no stationary pin at the endpoint → `IsPointHeldByStationaryPin`
  returns false → follow proceeds unchanged.
- Case 2 (pin-on-pin, no wire at shared point): `onWire = false` → pin-on-pin recording unchanged.
- Case 3 (wire drag): unchanged — the stationary-pin guard is in the component-drag paths only.

**Oracle (permanent):** `DragInvariantOracleTests.cs` `Case4_SharedPoint_WireStaysOnStationaryPin_AutoWireConnectsMovingComponent`:
C1–C2 wire + C3 pin-on-pin on C1-bottom → drag C3 away → wire endpoint stays at C1-bottom, new
auto-wire (0,200)→(0,400) forms, C1/C2/C3 all Connected.

Design doc: `docs/design/placement-connectivity-and-drag-follow.md` (rev 6).

Gate: all 589 tests green; firewall green.

---

## Extraction carries enabled analyses + run executes them (Phase 6e Step 6 — done)

**`NetExtractor.Extract`** now carries the schematic's authored analyses + measurements into the emitted
`TestBench`: layer 4 copies `model.Analyses` (enabled filter: `Analysis.Enabled`) + `model.Measurements`
into `tb.Analyses`/`tb.Measurements`. The `Enabled` flag lives on the `Analysis` base class and is persisted
in `.csch` (`CschAnalysis.Enabled`), so it round-trips and gates extraction automatically.

**SP multi-segment → one flat freq array:** `CnlWriter` emits one `analysis Name type=sparam` line per
segment (same analysis name). `CnlReader` now merges consecutive segments with the same name into a single
`SParameterAnalysis` with all sweeps. At engine time, `SParameterAnalysis.Expand()` unions all segment
points into one sorted/deduped `double[]`.

**CnlReader additions (round-trip-exact for all v1 typed analyses):**
- `TryParseDcDirective`: `analysis Name type=dc` → typed `DcAnalysis` (was falling through to raw directive).
- Multi-segment SP merge: consecutive `analysis N type=sparam` lines with same name collapsed into one.
- `TryParseMeasurementLine`: extracts trailing unit token — `measure Name = expr unit` round-trips unit.
  `IsMeasurementUnit` detects bare-word units (`dB`, `V`, `%`, `dBm`, …) without false-positive on expressions.

**Run flow (no new engine code):** `RunAnalysis` → `WriteNetlist` (now includes analyses) → `RunNetlist`
dispatches typed analyses. "No analysis" message appears only when all analyses are disabled or none exist.

Gate: 8 new tests (5 `NetExtractorAnalysesTests` + 3 `CnlWriterTests`); all 977 tests green; firewall green.

---

## Analysis reuse: copy/paste + .canl templates (Phase 6e Step 5 — done)

**One serializer (§5.4):** clipboard + `.canl` + `.csch` all use `AnalysisSerialization.Serialize/Deserialize`
(for clipboard/`.canl`) and `ToDto/FromDto` (for `.csch`). Never write a second encoder.

**Copy/paste:** `AnalysesListViewModel.CopyCommand` / `CopyAllCommand` / `PasteCommand` — multi-select supported;
`PasteAnalysesCommand` appends with intra-paste collision resolution (`{name} copy`, `{name} copy 2`, …),
undoable; §5.1 unresolved-ref surfacing via `AnalysisPreviewHelper` (≈ unknown: f0).

**Templates (`.canl`):** `AnalysisSerialization.SerializeCanl/DeserializeCanl` + `CanlFile` DTO wrap the same
analysis DTOs with `Name` + optional `Description`. `TemplateManager` (framework-free, `src/Ui/Schematic/`) loads
the resolution chain workspace→user (`LocalApplicationData/circuitRF/templates/`), saves atomically, checks
existence, deletes.

**Save as Template dialog** (`src/Ui/Views/Dialogs/SaveAsTemplateDialog.axaml`): name (validated via
`NameValidator`) + description + read-only preview list of analyses to be saved + collision guard (overwrite confirm
via `SaveChangesDialog`). Saves to workspace templates dir when a workspace is open, user templates dir otherwise.
Reports path via `_schematicVm.MessageSink?.Success(path, path)`.

**Insert from Template dialog** (`src/Ui/Views/Dialogs/InsertFromTemplateDialog.axaml`): lists all `.canl` from
the resolution chain; selected template shows preview; Delete button (minimal Manage). On Insert, appends via
`PasteAnalysesCommand` (same collision resolution + §5.1 surfacing).

**Workspace dir tracking:** `WorkspaceViewModel.OnCurrentWorkspacePathChanged` → `AnalysesTool.SetWorkspaceDir(dir)`
→ `AnalysesListViewModel.SetWorkspaceDir` — workspace dir flows into template commands so they target the workspace
templates dir when a workspace is open.

**`SaveChangesDialog`** now supports `dontSaveLabel: null` to show only 2 buttons (Save + Cancel).

**TextBox vertical centering (HIG):** global `<Style Selector="TextBox">` in `App.axaml` sets
`VerticalContentAlignment="Center"` — applies to all TextBoxes app-wide.

**Double-click to edit:** `AnalysesListView.axaml.cs :: OnRowDoubleTapped` calls `vm.EditCommand.ExecuteAsync(window)`.

Gate: 956 tests green (5 new `.canl` round-trip tests in `AnalysisSerializationTests`); firewall green.

---

## Analysis Add/Edit dialog — Layer 1 (Phase 6e Step 4 — Layer 1 done)

**`src/Ui/Views/Dialogs/AnalysisEditorDialog.axaml(.cs)`** — HIG Add/Edit dialog. Returns
`Analysis?` via `ShowDialog<Analysis?>`. Static `ShowAsync(owner, vm, isEdit)` factory handles
null-owner fallback (same `ResolveOwner` pattern). Code-behind driven (no `x:DataType`).

**Layout:** title | type picker (WrapPanel RadioButtons: DC · S-Parameter · Harmonic Balance;
Load Pull · LP Pursuit greyed + "coming soon" tooltip) | Name TextBox + inline validation |
Enabled CheckBox | swappable body panel | Cancel / OK (IsDefault, centered labels, gated on
CanCommit).

**Body panels (IsVisible by type):**
- `DcBodyPanel` — "Operating point — no additional configuration required." (DC is the novice path)
- `SpBodyPanel` — Layer 1 placeholder ("Default: 1–10 GHz, 101 pts"); Layer 2 replaces with segment sub-list
- `HbBodyPanel` — Layer 1 placeholder ("Default: f₀ = 1 GHz, 7 harmonics"); Layer 3 replaces with full form

**`AnalysisEditorViewModel`** (`src/Ui/ViewModels/AnalysisEditorViewModel.cs`) — staging VM:
- `AnalysisKind` enum (DC/SP/HB), `Type`, `Name`, `Enabled`, `NameError`, `CanCommit`
- `SpBody: SpBodyViewModel` / `HbBody: HbBodyViewModel` — per-type body VMs
- `ComputePreview(string expression)` — delegates to `AnalysisPreviewHelper` (§4.3, no fork)
- `BuildAnalysis()` — builds the staged `Analysis` on OK; null on validation failure
- `NextFreeName(kind, existing)` — generates "DC1"/"SP1"/"HB1", lowest free

**`AnalysisPreviewHelper`** (`src/Ui/ViewModels/AnalysisPreviewHelper.cs`) — static helper
reusing `DesignScope.Build + new Evaluator().Eval`, swallow-errors → empty, bare-number/blank
gates. Shared across all analysis-editor expression fields (SP segment Start/Stop in L2, HB
Tone/MaxHarm in L3).

**`SpBodyViewModel`** / **`HbBodyViewModel`** (`src/Ui/ViewModels/`) — sealed partial
`ObservableObject` stubs; `BuildSweeps()` / `BuildAnalysis()` return sensible defaults in L1.
L2 adds segments collection + commands to `SpBodyViewModel`. L3 adds all HB fields to
`HbBodyViewModel`. Both have `FromAnalysis` factory for the edit path.

**`AddAnalysisCommand`** / **`EditAnalysisCommand`** (`src/Ui/Commands/Analysis/`) — undoable
mutations. Add appends at count; Undo removes by reference. Edit stores old/new + index; Undo
restores original.

**Wiring:** `AnalysesListViewModel.Add(Window?)` / `Edit(Window?)` are now `async Task` commands.
`AnalysesListView.axaml` passes `CommandParameter="{Binding $parent[Window]}"` on all Add/Edit
buttons (toolbar + empty-state). `SetupAnalysesDialog` continues to work (same VM, modal host).

Gate: `dotnet build` / `dotnet test` green, all 951 tests pass, firewall green.

---

## Analyses panel + modal (Phase 6e Step 3 — done)

**`src/Ui/ViewModels/AnalysisRowViewModel.cs`** — wraps one `Analysis`; exposes `Enabled` (routes through
`EnableAnalysisCommand`), `Name`, `TypeLabel` ("DC"/"SP"/"HB"), `Summary` (one-liner with SI-suffixed
frequency; raw expression string for non-literal values).

**`src/Ui/ViewModels/AnalysesListViewModel.cs`** — `ObservableCollection<AnalysisRowViewModel>` for the
active schematic. `SetActiveSchematic(vm?)` rebinds on tab switch. Commands: Add/Edit (placeholder
no-ops — step 4 builds the real form), Remove, Duplicate (name-collision resolved: "{name} copy", then
"{name} copy 2", …), MoveUp, MoveDown. All mutations route through `SchematicViewModel.Execute` → undo
stack → marks document dirty. `NoActiveSchematic` / `IsEmpty` flags drive the two empty states.

**`src/Ui/ViewModels/Dock/AnalysesTool.cs`** — Dock `Tool` wrapping `AnalysesListViewModel`; Id = "Analyses".
Placed in the lower-left `propertiesDock` alongside `PropertiesTool` in `CircuitRfDockFactory`.

**Commands** (`src/Ui/Commands/Analysis/`):
- `EnableAnalysisCommand` — toggles `Analysis.Enabled`; undoable.
- `RemoveAnalysisCommand` — removes + records insertion index for undo re-insert.
- `DuplicateAnalysisCommand` — switch-expression clone for DC/SP/HB; `ResolveName` resolves collisions.
- `MoveAnalysisCommand` — swaps adjacent items; Execute/Undo swap in opposite directions.

**Views** (`src/Ui/Views/Analyses/`):
- `AnalysesListView.axaml` — toolbar + three-state body (no-schematic / empty-list / rows); rows show
  Enabled checkbox + TypeLabel badge + Name + Summary. Footer "Analyses run in listed order." when non-empty.
- `AnalysesToolView.axaml` — thin dock wrapper: `AnalysesListView DataContext="{Binding ListVm}"`.

**Dialog** (`src/Ui/Views/Dialogs/SetupAnalysesDialog.axaml`):
- Modal host for the **same** `AnalysesListViewModel` the dock uses (one VM, two hosts).
- Opened via `WorkspaceViewModel.SetupAnalysesCommand` (`Window? owner` → `ResolveOwner`).
- Bound in Simulate menu: NativeMenuItem + in-window MenuItem, both with "Setup Analyses…" label.

**Active-schematic tracking**: `WorkspaceViewModel.OnDocumentDockPropertyChanged` calls
`_factory.AnalysesTool?.SetActiveSchematic(activeVm)` after the PropertiesTool call — same pattern.

**`Analysis.Enabled`** added to `Core.Design.Analysis` base class (`bool Enabled { get; set; } = true`).
Persisted in `CschAnalysis.Enabled`; existing files without the field default to `true` on load.

Gate: 22 tests in `tests/Ui.Tests/AnalysesListViewModelTests.cs`; all 951 tests green; firewall green.

---

## Analysis persistence + shared encoder (Phase 6e Step 2 — done)

**`src/Ui/Schematic/AnalysisSerialization.cs`** — the **one encoder** (§5.4) for `Analysis` +
`Measurement` lists.  Three destinations reuse it; never write a second encoder:
- **`.csch`** (now): `CschFile.Analyses` + `CschFile.Measurements` populated via `AnalysisSerialization.ToDto/FromDto`.
- **Clipboard** (step 5): `AnalysisSerialization.Serialize(analyses, measurements) → json`.
- **`.canl` templates** (step 5): same `Serialize/Deserialize`.

**DTOs** (in `AnalysisSerialization.cs`): `CschAnalysis` (flat, type-discriminated by `Type: "dc"/"sp"/"hb"`),
`CschFrequencySpec`, `CschMeasurement`.  Enum-as-string, WhenWritingNull, Id never persisted.
Unknown type tags are silently skipped on load (forward-compat for loadpull/pursuit).

**`SchematicEditModel`** now carries `Analyses: List<Analysis>` + `Measurements: List<Measurement>`.
`CschFile` gains `Analyses: List<CschAnalysis>?` + `Measurements: List<CschMeasurement>?` (null =
omitted in file; absent on read = empty — old files load cleanly).

**`SavePlanBuilder.SchematicHasAnalyses`** returns `model.Analyses.Count > 0` (was `false`), so a
schematic carrying analyses sets `IsTestBench = true` on its cell step.

Gate: 19 new tests in `tests/Ui.Tests/AnalysisSerializationTests.cs`; all 929 tests green; firewall green.

---

## Run service + RunAnalysis wiring (Phase 6e Step 5 — done)

`src/Ui/Schematic/SchematicRunService.cs` — headless `static RunNetlist(path) → RunResult`.
Mirrors the CLI engine chain exactly: `CnlReader.ReadFile → new Elaborator(lib).Elaborate(tb)` →
dispatch each declared analysis → collect `DataSet`s. Never throws — all engine exceptions are
captured into `RunStatus.EngineError`.

**Analysis dispatch:**
- Typed: `SParameterAnalysis` (freq array from `FrequencySpec`), `HarmonicBalanceAnalysis`
  (`HbEngine.Resolve` + `new HbEngine(nl, tb).Run(p).DataSet`), `LoadpullAnalysis`
  (`LoadpullEngine.Resolve` + `Run`), `LoadpullPursuitAnalysis` (`LoadpullPursuitEngine.Resolve` +
  `new LoadpullPursuitEngine(lpEngine).Run`), `ParametricSweepAnalysis` (`ParametricSweepEngine.Run`).
  `DcAnalysis`: deferred (noted in message).
- Raw `type=sparam` directives from `RawDirectives`: parsed for `start/stop/step` with optional
  frequency-unit tokens (`GHz`, `MHz`, `kHz`, `Hz`); dispatched to `SParameterEngine.Run`.

**`WorkspaceViewModel.RunAnalysis`** (now `async Task`):
1. `WriteNetlist` → posts clickable path + extraction warnings.
2. `await Task.Run(() => SchematicRunService.RunNetlist(path))` — engine on background thread.
3. Posts `Messages.Success` / `Messages.Info` (NoAnalysis) / `Messages.Error` (EngineError).
4. Holds `_lastRunDataSets: IReadOnlyList<DataSet>` for Phase 7 (not plotted here).

**`StopAnalysis`**: informational stub — engines have no `CancellationToken` in v1; run completes.

**Scope fence:** Run → DataSet + reporting only. No results visualisation (Phase 7), no
analysis-authoring UI, no new engine code.

Gate: 4 tests in `tests/Ui.Tests/SchematicRunServiceTests.cs`; all 884 tests green.

---

## Net extractor (Phase 6e Step 1 — done)

`src/Ui/Schematic/NetExtractor.cs` — headless, framework-free `SchematicEditModel → TestBench` pass.

**Key invariants:**
- **Reuses `ComputeConnectivityGeometry`** (now `internal`) as the single source of connectivity: wire
  vertex hash, auto-dot T-junctions (`AutoDotKeys`), and dot-gated crossing predicate (`IsCrossingAtDot`).
  The extractor consumes these outputs; it does NOT re-implement T-junction or crossing logic.
- **Connection = exact on-`P` equality** — union-find is keyed by integer P-cells
  `(long)Math.Round(x/GridSize)`, not floating-point tolerance.
- **Same-name label union (§2.1.6):** labels with the same name union all their nets, even across
  physically-disjoint wires. `FindLabelNetKey` uses vertex-exact first, then `PointOnSegment` with
  `GridSize/2` tolerance for mid-segment labels.
- **Net-name priority: ground → Pin → label → auto.** A Pin component owns its net's identity — a
  net carrying a Pin is always named after the Pin, even if a user net label is also present on that
  net. The label is silently overridden (no conflict emitted); conflicts are only reported for
  label-vs-label collisions. Ground always wins over both. This ensures `Cell.Ports` (built from Pin
  names) matches the net names seen by the elaborator's port-binding step.  Implemented in
  `AssignNetNames` (`NetExtractor.cs`): ground → label loop (label-vs-label conflict detection) →
  Pin block (overrides label names; warns and skips if net is "0") → auto-name loop.
- **Terminal order is the contract:** `NetBindings[k]` = net at terminal k (symbol order). Walk
  `SymbolPortDefs.For(Symbol, PortCount)` in order. Never transpose; FetSdd is [gate, drain, source].
- **ZPort N-or-N+1 rule:** signal pins → `NetBindings`; "ref" pin → `RefNetBinding` (null if "0").
- **SDD 2N-pin rule:** the SDD schematic symbol exposes **2N pins as differential ± pairs** (pin
  order `1+,1−,2+,2−,…`), separate from ZPort's N+1 generator. Pin order is the NetExtractor
  contract matching the engine's `_v(p) = V(net[2p]) − V(net[2p+1])`. `EditableComponent.PortCount`
  for SDD remains N (signal ports); pin count is 2N. `FromRenderModel` derives SDD N as `pins/2`
  (ZPort stays `pins−1`).
- **Port special case:** schematic shows 1 pin; emits `NetBindings = [sigNet, "0"]`.
- **`ComponentTypeRegistry.EngineReference`** (new): maps SymbolKind → engine type string — differs
  from `DisplayName` for FetSdd ("FET"→"SDD"), ZPort ("Z"→"Z_Port"), ToneSource ("VTone"→"V_1Tone").
- **Ground skipped** (not emitted as instance); Open/Short honored.
- **Units glyph→ASCII normalization** applied at `EmitInstance` via `UnitNormalizer.ToEngineUnit`:
  editor glyphs (Ω, µ) are converted to ASCII engine spellings (Ohm, u) when building
  `ParameterAssignment.Unit` — the single crossing point. Editor glyphs and the engine `Units`
  table are both **unchanged**; only the emitted unit string is normalized.

Gate tests: `tests/Ui.Tests/NetExtractorLayer{1,2,3}Tests.cs` (19 tests, all green).

## Units glyph→ASCII normalization (Phase 6e Step 3 — done)

`src/Core/Expressions/UnitNormalizer.cs` — framework-free (no Avalonia, no Skia); lives in `src/Core`
so it is reachable from both `src/Core` and `src/Ui`.

**Rule:** convert at the boundary, once. The editor thinks in glyphs; the engine `Units` table is
ASCII-keyed (`Ohm`, `u`). `UnitNormalizer.ToEngineUnit(editorUnit)` is the one place the conversion
happens — called from `NetExtractor.EmitInstance` when building `ParameterAssignment` overrides.

**Substitutions (compose with any SI prefix):**
- `Ω` (U+03A9) → `Ohm`: `kΩ→kOhm`, `MΩ→MOhm`, `GΩ→GOhm`, `mΩ→mOhm`
- `µ` (U+00B5 MICRO SIGN) → `u`: `µH→uH`, `µF→uF`, `µV→uV`, `µA→uA`, `µW→uW`, `µm→um`
- `μ` (U+03BC GREEK MU) → `u`: defensive, handles alternate keyboard/font input
- Already-ASCII units (`nH`, `pF`, `Hz`, `deg`, `mil`, …) pass through unchanged
- `"None"` / empty → `""` (no unit emitted)
- Table-uncovered units (`dBm`, `V`, `A`, `W`, `kV`, `cm`, `mOhm`) emit as-is without crashing

Gate: 30 tests in `tests/Core.Tests/Expressions/UnitNormalizerTests.cs`, all green.

## Extraction oracle (Phase 6e Step 2 — done)

`src/Core/Netlist/CnlWriter.cs` — framework-free `TestBench → .cnl` text (inverse of `CnlReader`).
`tests/Ui.Tests/ExtractionOracleTests.cs` — 3 oracle tests:
- **L2 topology:** `NetExtractor.Extract` → `TestBench_extracted` topology ≡ hand-authored TestBench
  (partition-set comparison, name-agnostic). Transposition test FAILS the oracle (proves it has teeth).
- **L3 DataSet:** both extracted + authored run through `Elaborator + SParameterEngine`; DataSets match
  within 1e-9 tolerance.

The oracle is the **permanent correctness gate** for all future extraction changes.

## netlist.cnl write (Phase 6e Step 4 — done)

`WorkspaceViewModel.WriteNetlist(SchematicEditModel, string testBenchName)` — private helper;
framework-free except for `Directory.CreateDirectory` + `File.Move`.

**Destination rule:**
- Workspace open (`CurrentWorkspacePath != null`) → `Path.GetDirectoryName(CurrentWorkspacePath)/netlist.cnl`
  (workspace root directory).
- No workspace (scratch) → `RecoveryManager.SessionDir/netlist.cnl` (scratch-session dir, created lazily).

**Write flow:** `NetExtractor.Extract(model, name)` → `CnlWriter.Write(tb, header)` with provenance
header `; netlist.cnl — generated from TestBench "<name>" at <ISO-8601 UTC>` → atomic write
(temp path + `File.Move(..., overwrite: true)`).

**`RunAnalysis` command** (Step 4 wiring): resolves the active `SchematicDocument`, calls
`WriteNetlist`, posts `Messages.Success(path, path)` (clickable link) for the written path,
and posts `Messages.Warning` for each extraction conflict. **No engine run** — step 5 adds that.

Scope fence: one `netlist.cnl` overwritten each run; generated artifact, not saved-project state;
`.csch` is the source of truth.

---

## Scratch documents + New Schematic (Phase 6h Step 1 — done)

**Scratch = in-memory, no path, dirty, tree-invisible.** A scratch `SchematicDocument` is a normal
`SchematicViewModel`/`SchematicDocument` with `FilePath = null`. It is NOT in `_openDocsByPath` (which is
keyed by absolute path) and is NOT shown in the project tree (the tree reflects disk only).

### SchematicDocument scratch identity
- `FilePath: string?` — null for scratch; set to the on-disk `.csch` path for materialized docs.
  Step 2 (materialize-ancestors) will set this once at save time.
- `IsScratch => FilePath is null` — computed flag.
- `IsDirty: bool` — scratch starts `true` and stays true in step 1 (no save path yet).
  On-disk docs start `false` and flip to `true` on the first undoable edit (`CanUndo` flips).
- Tab title = `"• " + baseTitle` when `IsDirty`; plain title when clean.

### WorkspaceViewModel scratch tracking
- `_scratchDocs: List<SchematicDocument>` — open scratch docs. NOT `_openDocsByPath`.
  **NOTE (step 1):** entries are not removed when a tab is closed (no Dock close callback yet).
  The close-prompt and cleanup come in step 3. `_scratchDocs.Clear()` is called in `NewWorkspace`
  (which resets the Dock layout, so tabs are gone). `OpenWorkspace` does NOT clear it (tabs survive).
- `RebuildOpenSchematics()` iterates both `_openDocsByPath.Values` and `_scratchDocs` so scratch
  schematics re-resolve cell-ref symbols after a symbol save or Make-Primary.

### NewScratchSchematicCommand (⇧⌘N / Ctrl+Shift+N)
- Parameterless `[RelayCommand]`, always enabled — **no workspace required**.
- Creates `SchematicEditModel` → `SchematicViewModel` → `SchematicDocument(title, vm)` (null path).
- Title = next free `Untitled-Schematic-N` (lowest N not already in `_scratchDocs` or `_openDocsByPath`).
- Adds doc to `_scratchDocs`, opens via `_factory.OpenDocument(doc)`.
- Bound to File → New Schematic in the macOS NativeMenu (`Meta+Shift+N`) and in-window Menu
  (`Ctrl+Shift+N`). **New Cell (workspace-required) has no keyboard shortcut** — it was displaced.

### Launch state and action ownership

The `WorkspaceViewModel` constructor does **not** open any document. `CreateLayout()` already installs a
Welcome stub in the DocumentDock, so the app always lands on Welcome by default.

`ExecuteLaunchActionAsync(LaunchAction)` is called once at Background priority after the first window is
shown (from `App.axaml.cs ApplyLaunchSettings`). It is the **sole owner** of the initial document:

| Action         | Behavior                                                                                  |
|----------------|-------------------------------------------------------------------------------------------|
| `Welcome`      | Leave the Welcome stub; add nothing. **This is the default.**                             |
| `NewSchematic` | `RemoveWelcomeStub()` then `NewScratchSchematic()`.                                       |
| `NewWorkspace` | Show New Workspace dialog; on success, `RemoveWelcomeStub()` (from `CreateDefaultLayout`). If cancelled, Welcome stays. |
| `OpenWorkspace`| Show folder picker; on success, `RemoveWelcomeStub()` (no-op if `RestoreOpenDocuments` already removed it). If cancelled, Welcome stays. |
| `NewSymbol`    | Fall back to Welcome + `Messages.Info` (no blank-symbol path without a cell).             |
| `NewDataDisplay`| Fall back to Welcome + `Messages.Info` (not yet implemented).                            |

**Enum order:** `Welcome` is first (value 0 = default). `AppPreferences.LaunchAction` defaults to
`Welcome` in both `App.axaml.cs` and `SettingsView.LoadGeneralPrefs`.

Command-line file args still override (the `startupPaths.Length > 0` gate in `App.axaml.cs` skips
`ApplyLaunchSettings` entirely).

**macOS startup path:** On macOS (no file args), `firstWindow.Show()` and `ApplyLaunchSettings` are
called inline in `OnFrameworkInitializationCompleted` — NOT deferred to a `ShowFirstWindowIfNeeded`
helper (which has been removed; its guard on `_desktop.Windows` was always false because `firstWindow`
is added to `_desktop.Windows` by assignment to `MainWindow`, before `Show()`). The `_launchHandled`
flag (`bool`, default false) makes a startup Finder file-open take precedence: `OnActivated` sets it
true before the Background-priority `ApplyLaunchSettings` post runs, so the launch action is skipped.

### Materialize + SavePlanDialog (Phase 6h Step 2 — done)

**`SchematicDocument.Materialize(string filePath)`** — `internal` method that sets `FilePath` and clears
`IsDirty`. The one-way scratch→materialized transition. Also used to clear dirty on re-save of materialized
docs. `FilePath` now has `private set` (was readonly in step 1).

**`SavePlan` / `SavePlanBuilder`** (`src/Ui/Schematic/SavePlan.cs`) — framework-free plan model and builder.
`SavePlanBuilder(currentWorkspacePath, workspaceParentDir, scratchDocs).Build(mode, overrides)` produces
`SavePlan { WorkspaceStep?, IReadOnlyList<CellStep>, IReadOnlyList<SaveStep> }`. De-duplicates cell steps
by name. `SchematicHasAnalyses` returns false (TODO 6e hook for analysis→TestBench detection). `SaveMode`:
`EachOwnCell` (default) / `AllInOneCell`.

**`SavePlanExecutor.ExecuteFileOps(SavePlan, existingWorkspaceDir?)`** (`SavePlanExecutor.cs`) — framework-free
static method; creates workspace/.cws, cell folders/.ccell (sets IsTestBench), writes .csch files, sets
PrimarySchematic in .ccell, calls `Materialize` on each doc. Returns list of all files written.

**`WorkspaceViewModel.ExecuteSavePlan(SavePlan)`** — calls `SavePlanExecutor.ExecuteFileOps`, updates
`CurrentWorkspacePath` + `_lastWorkspaceParentDir` (if new workspace), moves docs from `_scratchDocs` to
`_openDocsByPath`, re-wires project tree, calls `Refresh()`, reports all written paths via `Messages.Success`.

**`WorkspaceViewModel.SaveAllDocumentsCommand`** (`[RelayCommand] async Task SaveAllDocuments(Window? owner)`)
— the new ⌘S/Ctrl+S handler:
1. Dirty scratch docs → `SavePlanBuilder.Build` → `SavePlanDialog.ShowDialog<SavePlan?>` → on confirm, `ExecuteSavePlan`
2. Dirty materialized docs → `SchematicPersistence.SaveToFile` + `Materialize` directly
3. `WriteWorkspaceFile` if workspace exists
- Returns "Nothing to save" info if nothing is dirty.

**`SavePlanDialog`** (`src/Ui/Views/Dialogs/SavePlanDialog.axaml(.cs)`) — HIG plan dialog:
- Title "Save your work" / subtitle "circuitRF will create the following and save your documents."
- Mode toggle (`EachOwnCellRadio` / `AllInOneCellRadio` + `SharedCellNameBox`) visible when cells will be created
- Plan table (`PlanRowsPanel` StackPanel): workspace rows (FolderOutline icon), cell rows (Folder icon + TestBench
  badge), save rows (FileOutline icon + primary badge). Rows built programmatically in code-behind.
- Inline `NameValidator` errors per editable row (OrangeRed text below each row).
- **Save All** (`SaveAllButton`, `IsDefault=True`, `HorizontalContentAlignment=Center`) / **Cancel** (`IsCancel=True`)
- Returns confirmed `SavePlan` or null on cancel via `ShowDialog<SavePlan?>`.

**⌘S/Ctrl+S routing:** `WorkspaceWindow.axaml` binds both `NativeMenuItem` and `KeyBinding` for ⌘S/Ctrl+S
to `SaveAllDocumentsCommand`. `SaveWorkspaceAsCommand` remains bound to ⌘⇧S/Ctrl+Shift+S. Menu item now
reads "Save All" (macOS NativeMenu + in-window Menu).

**Scope fence (step 2):** into-cell Save-All only. Close/quit prompts, autosave, and loose/plain-file tiers
are step 3.

### Three-tier save + close/quit prompts + autosave/recovery (Phase 6h Step 3 — done)

**Tier 2 — loose Known File (`SaveLooseSchematicCommand`, bound to File → "Save Schematic As…"):**
`SaveLooseToWorkspace(doc, owner)` shows a file picker → writes `.csch` → atomically updates `.cws`
(`WorkspacePersistence.SaveToFileAtomic`) adding the path to `CwsFile.KnownFiles` → scratch→materialized
transition (`_scratchDocs.Remove`, `_recovery.ClearDoc`, `doc.Materialize`, `_openDocsByPath[fp] = doc`).

**Tier 3 — no workspace (`SaveLooseNoWorkspace`):** `SaveChangesDialog` with "Create Workspace…" /
"Save as File" / Cancel. "Create Workspace…" routes to the full plan dialog (same as ⌘S). "Save as
File" → `SaveLoosePlainFile` (file picker, plain `.csch`, no workspace registration,
`_recovery.ClearDoc`).

**Close/quit prompts:**
- **Tab close** — `CircuitRfDockFactory.CloseDockableConfirm: Func<IDockable, Task<bool>>?` wired in
  constructor; `CloseDockable` is `async void` override: awaits hook, returns without calling base on
  cancel. `ConfirmCloseDockable` shows `SaveChangesDialog` per dirty `SchematicDocument`.
- **Window close / quit** — `WorkspaceWindow.OnClosing` async override: `e.Cancel = true`, await
  `PromptSaveBeforeClose`, then `_vm.OnCleanExit(); _closingConfirmed = true; Close()`.
- **NewWorkspace / OpenWorkspace / OpenRecentWorkspace** — `HasAnyDirtyWork()` guard; on dirty,
  `PromptSaveBeforeClose` (Save All / Don't Save / Cancel); Cancel aborts the navigation.
- **`PromptSaveBeforeClose`** — collects dirty scratch + dirty materialized; single dialog message;
  Save → plan dialog for scratch + direct write for materialized; DontSave → proceed; Cancel → false.

**`SaveChangesDialog`** (`src/Ui/Views/Dialogs/SaveChangesDialog.axaml(.cs)`) — now configurable:
constructor accepts `message`, `saveLabel`, `dontSaveLabel`, `cancelLabel`; `Close(Result)` on each
button so both `ShowDialog` and `ShowDialog<T>` return correctly. `SizeToContent="WidthAndHeight"`.

**Autosave/recovery — `RecoveryManager`** (`src/Ui/Schematic/RecoveryManager.cs`, framework-free):
- **Session dir:** `LocalApplicationData/circuitRF/recovery/<12-char-hex-guid>/` (created lazily).
- **`AutoSave(doc)`** — atomic `.csch` write (temp + `File.Move(..., overwrite: true)`); silently
  swallows I/O errors (autosave must never interrupt editing).
- **`ClearDoc(doc)`** — removes one recovery file when a doc is cleanly saved/materialized. Prunes
  empty session dir.
- **`ClearSession()`** — removes entire session dir on clean exit.
- **`FindPriorSessions(currentSessionDir)`** / **`LoadSession(sessionDir)`** / **`DeletePriorSession`**
  — discovery + deserialize for restore offer.

**Wiring in `WorkspaceViewModel`:**
- `RecoveryManager _recovery` initialized in constructor.
- `StartAutosaveTimer()` — `DispatcherTimer` (30 s interval) → `AutoSaveAll()`.
- `AutoSaveAll()` — iterates `_scratchDocs.Where(IsDirty)`, calls `_recovery.AutoSave`.
- `CheckForRecovery()` (async void) — deferred via `Dispatcher.UIThread.Post(..., Background)`;
  finds prior sessions; shows restore dialog; on accept: opens recovered docs as new scratch tabs;
  on decline: calls `DeletePriorSession`.
- `OnCleanExit()` — stops timer, calls `_recovery.ClearSession()`. Called before confirming quit.
- `_recovery.ClearDoc` at every materialization point: `ExecuteSavePlan` (per save step),
  `SaveLooseToWorkspace`, `SaveLoosePlainFile`.
- `OnDockableClosed` (subscribed to `_factory.DockableClosed`) — removes closed docs from
  `_scratchDocs` and `_openDocsByPath`.

**Scratch-only invariant:** autosave never touches materialized docs. Once a doc is materialized
(removed from `_scratchDocs`), no recovery file is created or offered for it.

---

## Scratch symbols + New Symbol on launch (ScratchSymbol — done)

**New Symbol on launch** (On-launch = New Symbol) now opens a scratch symbol immediately — no workspace or
cell required. The lifecycle mirrors scratch schematics at the document level.

### SymbolEditorDocument scratch identity
- `FilePath: string?` — null for scratch; set to the on-disk `.csym` path for materialized docs.
- `IsScratch => FilePath is null` — computed.
- `IsDirty: bool` — the VM's `IsDirty` (`[ObservableProperty]`) is the **single source of truth**;
  `SymbolEditorDocument` subscribes to `ViewModel.PropertyChanged` for `IsDirty` changes and mirrors it.
  **Do NOT double-track** — only the document subscribes to the VM, not the reverse.
- Tab title = `"• " + baseTitle` when `IsDirty`; plain title when clean.
- `Materialize(string filePath)` — sets `FilePath`, sets `ViewModel.CurrentSymbolPath`, sets `ViewModel.IsDirty = false`.
  IsDirty on the document clears via the PropertyChanged subscription.

### WorkspaceViewModel scratch symbol tracking
- `_scratchSymbols: List<SymbolEditorDocument>` — open scratch symbol docs. Not in `_openDocsByPath`.
- `NewScratchSymbol()` — creates `EditableSymbol { UserEditable = true }` → `SymbolEditorViewModel` →
  `SymbolEditorDocument(title, vm)` (null FilePath), wires `vm.SymbolSaved += OnSymbolSaved`,
  adds to `_scratchSymbols`, opens via `_factory.OpenDocument`.
- `NextScratchSymbolTitle()` — lowest free `"Untitled-Symbol-N"` across `_scratchSymbols` + open symbol docs.
- `OnDockableClosed`: removes from `_scratchSymbols` (mirrors `_scratchDocs.Remove` for schematics).
- Both workspace-reset paths (`NewWorkspace`, `OpenWorkspace`) call `_scratchSymbols.Clear()`.

### Launch action
`ExecuteLaunchActionAsync(NewSymbol)` → `_factory.RemoveWelcomeStub(); NewScratchSymbol();`
(was: fall back to Welcome + info message).

### Save-target offer dialog (⌘S with scratch symbol active)
`SaveAllDocuments` SingleDoc branch routes `SymbolEditorDocument` through `SaveSingleSymbolDocument`:
- **Scratch** → `SaveScratchSymbol(doc, window)` shows `SaveChangesDialog` with:
  - **"Save to Cell…"** (workspace open): `InputNameDialog` for cell name → `CellFolder.CreateCellFolder` +
    `SubFolderPath(ViewType.Symbol)` → `SymbolPersistence.SaveToFile` → `doc.Materialize` → move from
    `_scratchSymbols` to `_openDocsByPath` → `OnSymbolSaved` (cache invalidation) → tree refresh.
    Cell name = symbol filename (e.g., cell "MyFET" → `MyFET/symbol/MyFET.csym`).
    No workspace: routes to "Save as File" branch instead.
  - **"Save as File"** (orphan): delegates to `vm.SaveSymbolAsCommand.ExecuteAsync(window)` (file picker +
    `PerformSave`), then calls `doc.Materialize(pathAfter)` + moves to `_openDocsByPath`.
    No workspace registration, no tree entry — bare .csym.
  - **Cancel**: no-op.
- **Materialized** → `vm.SaveSymbolCommand.ExecuteAsync(window)` (existing path, already works).

### Full dirty-work coverage (Layer 5)
- `HasAnyDirtyWork()`: includes `_scratchSymbols.Any(IsDirty)` and materialized symbol docs.
- `ConfirmCloseDockable`: added branch for dirty `SymbolEditorDocument` (same Save/Don't Save/Cancel
  pattern as schematics; Save → `SaveSingleSymbolDocument`; returns `!symDoc.IsDirty` so Cancel in
  the save-target dialog also cancels the close).
- `SaveAllDocuments` AllDocs scope: iterates `dirtyScratchSymbols` (per-doc offer dialog) and
  `dirtyMaterializedSymbols` (direct VM save).
- `PromptSaveBeforeClose`: includes dirty scratch + materialized symbol docs in total count and save path.

### Recovery / autosave
**Deferred for v1.** Scratch symbols are lost on crash in v1. `AutoSaveAll` / `CheckForRecovery` cover
only `_scratchDocs`. Extending to `_scratchSymbols` is a straightforward follow-up.

### v2 deferred items
- Full `SavePlan`/cell-wizard for symbols (AllInOneCell mode, TestBench detection, plan dialog).
- "Save to Cell" when no workspace: currently routes to "Save as File"; v2 should offer workspace creation.

---

## New Workspace dialog + Open Workspace + Recent Workspaces (Phase 6g Step 5 fix 5)

**File → New Workspace uses `NewWorkspaceDialog`** (`src/Ui/Views/Dialogs/NewWorkspaceDialog.axaml(.cs)`),
NOT a raw system folder picker. The user names a *Workspace*; circuitRF creates the folder.

Key rules:
- The system folder picker (`OpenFolderPickerAsync`) is used **only** behind the "Choose…" button to
  select the **parent location** — an existing folder is fine for that.
- The workspace folder = `parent/<name>/` — always created by us, never pre-existing (dialog gates OK
  on this + `NewWorkspace` re-checks at create time as a race guard).
- `NewWorkspaceDialog` returns `NewWorkspaceResult { ParentDir, Name }` via `ShowDialog<NewWorkspaceResult?>`,
  or null on cancel — mirrors `InputNameDialog`'s return-or-null contract.
- Name validated live via `NameValidator`; workspace name comes from the folder leaf, never the `.cws` stem.
- On open, the name field is **prefilled** with the next free `Untitled-Workspace-N` for the current Location.
  When the user changes Location via "Choose…" without having manually edited the name, the suggestion is
  recomputed for the new location. Suppression flag `_settingSuggested` prevents `OnNameChanged` from
  marking the programmatic fill as a user edit.

### Tracked Location (in-memory, not persisted)
`WorkspaceViewModel._lastWorkspaceParentDir` (initialized to Documents): seeds the Location field in
`NewWorkspaceDialog` and the `SuggestedStartLocation` of `OpenFolderPickerAsync`. Updated after every
successful New or Open to the parent of the workspace folder. **Never persisted.**

### Open Workspace = folder picker
`OpenWorkspace` uses `OpenFolderPickerAsync` (not file picker). The selected folder IS the workspace
folder; `.cws` = `Path.Combine(folder, ".cws")`. If `.cws` does not exist, the open is rejected with a
clear error message. Menu item reads "Open Workspace…" in both NativeMenu and in-window Menu.

### Recent Workspaces (persisted in AppPreferences)
- `AppPreferences.RecentWorkspaces: List<string>?` — the `.cws` paths, MRU order, capped at 10.
  Serialized as `recent_workspaces` in `preferences.json`. Null when empty (omitted from JSON).
- `WorkspaceViewModel.PushRecent(cwsPath)` — dedup (case-insensitive), insert at front, cap 10, save,
  rebuild menu items. Called after every successful `NewWorkspace` and `OpenWorkspace`.
- `WorkspaceViewModel.RecentMenuItems: ObservableCollection<Control>` — holds `MenuItem` + `Separator`
  instances rebuilt by `RebuildRecentMenuItems()`. Bound to the in-window "Open Recent" submenu via
  `ItemsSource`. `HasRecentWorkspaces` (bool property, notified on change) drives `IsEnabled`.
- `WorkspaceViewModel.RecentWorkspacesChanged: event Action?` — fired after every push/clear so
  `WorkspaceWindow.axaml.cs` can rebuild the NativeMenu.
- **NativeMenu rebuild**: `WorkspaceWindow.axaml.cs` subscribes to `RecentWorkspacesChanged` in
  `OnDataContextChanged`. `EnsureNativeRecentMenuWired()` (called once from `OnOpened`) inserts the
  "Open Recent" `NativeMenuItem` programmatically after "Open Workspace…" and populates it.
  `RebuildNativeRecentMenu()` clears and repopulates `NativeMenuItem.Menu.Items` on every change.
- **Missing entry**: if a recent workspace's `.cws` no longer exists, `OpenRecentWorkspace` removes it
  from the list, saves, rebuilds menus, and shows an error.
- `ClearRecentWorkspaces` command empties the list, saves, and rebuilds both menus.

## macOS / command gotchas (Phase 6g Step 5 fixes)

### `$parent[Window]` is null on macOS for NativeMenu and KeyBinding
`{Binding $parent[Window]}` resolves to `null` for `NativeMenuItem.CommandParameter` and
`KeyBinding.CommandParameter` on macOS — neither lives in the Avalonia visual tree where the ancestor
walk can reach a `Window`. The standard fix is a `ResolveOwner(Window? parameter)` helper in the ViewModel:

```csharp
private Window? ResolveOwner(Window? parameter) =>
    parameter
    ?? (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
       ?.Windows.FirstOrDefault(w => ReferenceEquals(w.DataContext, this));
```

`desktop.MainWindow` is also null in this app (`App.axaml.cs` calls `window.Show()` but never assigns
`desktop.MainWindow`), so `.MainWindow` is the wrong fallback. Walking `ApplicationLifetime.Windows`
keyed by `ReferenceEquals(w.DataContext, this)` finds the exact host window and works correctly in
multi-window scenarios. Apply `ResolveOwner` to every command that takes `Window?` and opens a picker.

### `CreateDefaultLayout()` replaces all factory tools
`CircuitRfDockFactory.CreateDefaultLayout()` internally calls `CreateLayout()` which assigns new
instances to `ProjectTreeTool`, `PropertiesTool`, etc. Any command that resets the layout (currently
only `NewWorkspace`) must re-wire those tools **after** `Layout = newLayout`:

```csharp
Layout = newLayout;
_factory.ProjectTreeTool?.SetActions(this);
_factory.ProjectTreeTool?.SetWorkspace(workspaceDir);
```

`SetActions` before `SetWorkspace` because `SetWorkspace` → `RebuildVmTree` uses `_actions`.

### Dock `Tool.Title` PropertyChanged is not reliably picked up by Avalonia compiled bindings
Setting `Title` on a Dock `Tool` (base class) calls `SetProperty` which fires `PropertyChanged` via
the Dock library's `ObservableObject`. However, Avalonia compiled bindings (`x:DataType`) on the
tool's view do not reliably pick up this event in practice. Expose a separate `[ObservableProperty]`
for any observable header text the view needs to bind to:

```csharp
[ObservableProperty] private string _workspaceName = "No workspace";
```

`ProjectTreeTool.Title` is **static "Project"** — set once in the constructor, never updated per workspace.
The in-view header `TextBlock.Text` binds to `WorkspaceName` (set in `SetWorkspace`, reset to "No workspace"
in `ClearWorkspace`). Do NOT update `Title` per workspace — the dock-tab label is intentionally always "Project".

### `.cws` is a dotfile — derive the workspace name from the FOLDER, not the file stem
A circuitRF workspace is a **named folder containing a `.cws` file**: `…/<name>/.cws`.
`Path.GetFileNameWithoutExtension(".cws")` in .NET returns `".cws"` (dotfiles have no extension), NOT the
workspace name. Always derive the workspace name from the **folder name**:

```csharp
var dir  = Path.GetDirectoryName(cwsPath);       // …/<name>
var name = Path.GetFileName(dir);                 // <name>
```

Apply this everywhere the workspace name must be displayed (window title, `WorkspaceName`, messages).

---

## Cell reference model + live update (Phase 6g Step 5 — done)

### Entry points (L1)
`NewWorkspace` creates a real `.cws` + workspace folder on disk (was only resetting dock state).
`File → New Cell` menu item (`NewCellInWorkspaceCommand` on `WorkspaceViewModel`, greyed via
`CanExecute = CurrentWorkspacePath is not null`; `NotifyCanExecuteChanged` in `OnCurrentWorkspacePathChanged`).
Tree-header **New Cell** button in `ProjectTreeView` (`IsVisible="{Binding HasWorkspace}"`).
`ITreeActions.NewCellInWorkspaceAsync()` is the shared implementation: prompts with `InputNameDialog`,
validates with `NameValidator`, calls `CellFolder.CreateCellFolder(workspaceDir, name)` + `Refresh()`.

### Cell-ref data model (L2)
**`EditableComponent.CellRef: string?`** — relative path from the schematic directory to the referenced
cell folder.  Null for built-in components.  Round-tripped through `CschComponent.CellRef` (nullable,
`WhenWritingNull`; omitted from file when null).

**`SchematicEditModel.SchematicDirectory: string?`** — absolute directory of the containing `.csch` file.
Set by `SchematicPersistence.FromFileModel` from the directory argument passed by `LoadFromFile`.
Used as the base for resolving `CellRef` relative paths.

**`CellSymbolResolver`** (`src/Ui/Schematic/CellSymbolResolver.cs`) — framework-free static resolver.
`Resolve(cellRef, baseDir) → CellSymbolResolution { State: CellSymbolState, Symbol? }`.
`CellSymbolState` enum: `Resolved / NotFound / PrimaryMissing` (kept distinct — do NOT collapse).
Cache keyed by `(cellAbsDir, primaryFilename, symFileMtime)`; invalidated by `Invalidate(cellAbsDir)`
or `InvalidateAll()`.  Resolution chain: relative path → `Directory.Exists` → `CellFolder.ResolvePrimary`
(single primacy source) → `SymbolPersistence.LoadFromFile`.

### Three-state rendering (L3)
**`SchematicComponent`** gained `CellRefState: CellSymbolState?` and
`CellRefPrimitives: IReadOnlyList<SymbolPrimitive>?` (both null for built-ins).

**`BuildRenderModel`** pre-resolves all `CellRef` values via `ResolveAllCellRefs()` before the
connectivity pass.  Resolved symbol pins supply port world-coords (not `SymbolPortDefs`).
`ToRenderComponent(isConnected, cellRefResolution?)` uses resolved pins for ports and resolved
primitives for the glyph BB.

**`SchematicRenderer`** dispatches on `CellRefState` before the built-in draw path:
- `Resolved` → `DrawSymbol(c.CellRefPrimitives, ...)` — same path as built-ins, no `DrawVariadicPortLeads`
- `NotFound` → `DrawCellRefNotFoundGlyph` — warning fill+stroke box, "Not Found" centred label
- `PrimaryMissing` → `DrawCellRefPrimaryMissingGlyph` — plain stroke rectangle stand-in
- `null` (built-in) → existing `BuiltInSymbols.Primitives` + `DrawVariadicPortLeads` path, unchanged

### Live update (L4)
**`SymbolEditorViewModel.SymbolSaved: event Action<string>?`** fires from `PerformSave` with the
absolute `.csym` path.  Both Save and Save-As go through `PerformSave`.

**`SchematicViewModel.TriggerRebuild()`** calls `EditModel.NotifyChanged()` — reuses the same
`Changed → RebuildRenderModel` pipeline used by all mutation commands.

**`WorkspaceViewModel`** wiring:
- `OnSymbolSaved(savedSymPath)` — derives `cellDir` (two `GetDirectoryName` calls up), calls
  `CellSymbolResolver.Invalidate(cellDir)`, then `RebuildOpenSchematics()`.
- `RebuildOpenSchematics()` — iterates `_openDocsByPath.Values`, calls `TriggerRebuild()` on every
  `SchematicDocument`.
- `MakePrimary` — after writing a new primary symbol (`subFolderName == CellFolder.SymbolSubFolder`),
  calls `Invalidate(cellDir)` + `RebuildOpenSchematics()`.
- `OpenOrActivateSymbol` and `NewSymbolAsync` both subscribe `vm.SymbolSaved += OnSymbolSaved` when
  the `SymbolEditorViewModel` is created.

**Dangling wires on pin-count change:** wires to ports that no longer exist in the new symbol show
as unconnected (dangling). No auto-rewire (Option B still deferred).

---

## Project Tree interactions (Phase 6g Step 4 — done)

**ITreeActions** (`src/Ui/ViewModels/ProjectTree/ITreeActions.cs`) — callback interface implemented by
WorkspaceViewModel, injected into `ProjectTreeNodeViewModel` via `ProjectTreeTool.SetActions(ITreeActions)`.
Every tree-node command delegates to this interface so all open/create/reveal operations live in WorkspaceViewModel.

**Commands on ProjectTreeNodeViewModel** — `ActivateCommand` (double-click open/activate),
`MakePrimaryCommand` (view files), `RevealCommand` (all file/folder nodes), `NewCellCommand`
(workspace/library nodes), `NewSymbolCommand` / `NewSchematicCommand` (cell nodes).  Context-menu
`IsVisible` driven by `IsViewFile`, `IsCell`, `IsWorkspaceOrLibrary`, `CanReveal`.

**Open/activate dedup** — `WorkspaceViewModel._openDocsByPath` (`Dictionary<string, IDockable>`,
OrdinalIgnoreCase) tracks open docs by absolute path. `ActivateIfOpen(absPath)` checks before opening;
activates the existing tab if found. Users can close a tab and reopen from the tree without issue.

**Double-click open paths:**
- `.csym` → `SymbolPersistence.LoadFromFile` + `EditableSymbol.FromSymbol` + `SymbolEditorDocument` (real)
- `.csch` → `SchematicPersistence.LoadFromFile` + `SchematicViewModel` + `SchematicDocument` (real)
- cell node → `CellParameterEditorDocument` (real, step 6 — see below)
- `.clay`, other view-file types, data displays, color themes → no-op

**Make Primary** — reads `.ccell` from `../..` of the view file path, sets the correct `PrimarySchematic` /
`PrimarySymbol` / `PrimaryLayout` field (discriminated by sub-folder name), writes back, calls `Refresh()`.
When the changed view is a symbol, also calls `CellSymbolResolver.Invalidate(cellDir)` + `RebuildOpenSchematics()`
so open schematics re-render with the new primary (Step 5 live-update).

**Reveal** — `Process.Start`: macOS `open -R <path>`, Windows `explorer /select,"<path>"`,
Linux `xdg-open <parent-dir>`.  Platform detected via `RuntimeInformation.IsOSPlatform`.

**Creation actions** — `InputNameDialog` (`src/Ui/Views/Dialogs/`) prompts for name, validated with
`NameValidator`.  On confirm:
- New Cell → `CellFolder.CreateCellFolder(parentDir, name)` + `Refresh()`
- New Symbol → write empty `.csym` via `SymbolPersistence.SaveToFile` + open `SymbolEditorDocument` with
  fresh `EditableSymbol { UserEditable=true, CurrentSymbolPath=path }` + `Refresh()`
- New Schematic → write empty `.csch` via `SchematicPersistence.SaveToFile` + open `SchematicDocument` with
  new `SchematicViewModel(emptyModel)` + `Refresh()`
- New Layout → `IsEnabled=False` (greyed, v2)

`RevealLabel` on the VM is platform-aware ("Reveal in Finder" / "Reveal in Explorer" / "Reveal in File Manager").

## Cell-parameter editor (Phase 6g Step 6 — done)

**Purpose:** edits the cell's **declared parameter interface** in its `.ccell` — add / remove / rename rows +
defaults (Name / Default / Unit / Dimension / ShowOnSchematic). NOT instance values. The delta vs. the instance
editor (`ParameterEditorViewModel`): rows are add/remove/rename-able; Name is editable.

### Edit model (framework-free)
**`CellParameterEditModel`** (`src/Ui/Schematic/CellParameterEditModel.cs`) — wraps a `CcellFile` + `.ccell`
path. `IReadOnlyList<CcellParameter> Parameters` (read view); `internal List<CcellParameter> MutableParameters`
(command access); `Save()` writes `.ccell`; `NotifyChanged()` fires `Changed` event so the VM rebuilds rows.

### Commands (framework-free, `src/Ui/Commands/Cell/`)
- **`AddCellParameterCommand`** — appends; Undo removes by reference.
- **`RemoveCellParameterCommand`** — records insertion index; Undo re-inserts at saved index.
- **`SetCellParameterCommand`** — stores full old/new snapshot for Name, Default, Unit, Dimension, Show.
  Covers rename, default edit, unit/dimension/show changes. Both Execute and Undo persist + notify.

### ViewModel (`src/Ui/ViewModels/CellParameterEditorViewModel.cs`)
Owns its own `UndoRedoStack` (per the per-document-undo rule). Subscribes to `_editModel.Changed`;
`RebuildRows()` clears + recreates `ObservableCollection<CellParameterRowViewModel>` on every mutation or undo.
`AddParameterCommand` generates a unique `ParamN` name. `UndoCommand`/`RedoCommand` delegate to own stack.

### Row VM (`src/Ui/ViewModels/CellParameterRowViewModel.cs`)
Staged Name (editable), Default, Unit, Dimension, ShowOnSchematic. Commit methods called from code-behind
(LostFocus/Enter for TextBoxes; SelectionChanged for ComboBoxes). `partial void OnStagedNameChanged`:
shows `RenameWarning` while name diverges from model. `partial void OnShowOnSchematicChanged`: auto-commits.
`CommitName` validates `[A-Za-z_][A-Za-z0-9_]*`; reverts on invalid. `CommitDimension` resets unit to first
valid option for the new dimension. `AllDimensions` = `Enum.GetValues<UnitDimension>()` (static array).

### Document (`src/Ui/Schematic/CellParameterEditorDocument.cs`)
`Document + IUndoableDocument` — `UndoRedo => ViewModel.UndoRedo`. Keyed by cell folder path in
`WorkspaceViewModel._openDocsByPath`. Workspace undo routing routes to its stack while active.

### View (`src/Ui/Views/Content/CellParameterEditorView.axaml(.cs)`)
`x:DataType="CellParameterEditorDocument"`. Layout: header (cell name + "Parameters" label), column-header
row, scrollable `ItemsControl` (rows), footer (Add Parameter button). `Grid.IsSharedSizeScope="True"` on the
outer container aligns header columns with row columns (`CpName`, `CpUnit`, `CpDim`, `CpShow`, `CpRemove`).
Code-behind: `_suppressUnitCommit` + `_suppressDimCommit` flags prevent re-entrant SelectionChanged commits.
DataTemplate registered in `App.axaml`.

### Wiring
`WorkspaceViewModel.OpenOrActivateCellPlaceholder` (step 4 stub) replaced: loads `.ccell` via
`CellPersistence`, creates `CellParameterEditModel` + `CellParameterEditorViewModel` + `CellParameterEditorDocument`,
opens via `_factory.OpenDocument`, keyed by cell folder path in `_openDocsByPath`. Dedup/activate already open.

**Scope fence:** cell-parameter editor only — no instance-value migration, no `.cws` work (step 7).

## .cws lifecycle (Phase 6g Step 7 — done)

**Atomic writes everywhere:** All `.cws` writes use `WorkspacePersistence.SaveToFileAtomic` (temp-rename).
Callers: `NewWorkspace`, `WriteWorkspaceFile`, `SavePlanExecutor.ExecuteFileOps`, `SaveLooseToWorkspace`.
`SaveToFile` (non-atomic) remains only in test helpers.

**Corruption-tolerant open:** `TryLoadCws(string cwsPath)` — loads `.cws`, logs `Messages.Warning` on any
exception, returns `new CwsFile()`. Both `OpenWorkspace` and `OpenRecentWorkspace` call it. Tree content
is always populated by `WorkspaceScanner` (filesystem is truth, scanner's own `TryLoadCws` handles corruption).
"No `.cws` → not a workspace" gate (file-exists check) retained; a corrupt-but-present `.cws` degrades to
defaults, never rejects the open.

**Real `WriteWorkspaceFile`:** Loads existing `.cws` to preserve `KnownFiles` + `LibraryRefs` (authoritative
on disk), updates `ColorSchemeName` + `TreeViewState`, writes atomically. `DockLayout` stays null in v1
(`Dock.Serializer` not referenced). `silent` param suppresses success message on debounce/exit flush.

**Debounced autosave:** `ScheduleCwsSave()` resets a 3-second `DispatcherTimer`; on tick, writes silently.
`SubscribeToFilterState()` hooks `ProjectTreeFilterState.PropertyChanged → ScheduleCwsSave()`, tracking the
current tool instance across `CreateDefaultLayout()` replacements (called in constructor, `NewWorkspace`,
`ResetLayout`, `ExecuteSavePlan`). Pan/zoom never touches a `.cws` field — no write there.
`OnCleanExit` stops timers, flushes `.cws` synchronously, then clears recovery cache.

**Tree view-state in `.cws` (`CwsTreeViewState`):** 7 bool filter flags; `Ordering` string (v1 = null,
reserved for future ordering UI). Written by `WriteWorkspaceFile`; restored on open via `ApplyTreeViewState`
(unsubscribes debounce handler during restore to avoid spurious write). `TreeViewState` is
`JsonIgnore(WhenWritingNull)` — null written as nothing; missing on read → `null` → defaults applied.

**`MemberFiles` removed:** Deleted from `CwsFile`. Old `.cws` files with `member_files` still load (System.Text.Json
ignores unknown fields by default with `PropertyNameCaseInsensitive = true`).

## Project Tree VIEW (Phase 6g Step 3 — done)

Three new files in `src/Ui/ViewModels/ProjectTree/`:
- **`ProjectTreeFilterState`** (`ProjectTreeFilterState.cs`) — `ObservableObject` with 7 independently
  togglable bool properties (`Cells`, `Libraries`, `TestBenches`, `DataDisplays`, `ColorThemes`,
  `KnownFiles`, `WorkspaceFileSystem`); `IsAllOn` and `SetAll(bool)` helpers.
- **`ProjectTreeNodeViewModel`** (`ProjectTreeItemViewModel.cs` — old stub replaced) — wraps a
  `ProjectTreeNode`; exposes `IconKind` (`MaterialIconKind` switch on `Kind` + `IsTestBench`),
  `IsWarning`, `IsBold`, `IsItalic`, `IsExpanded` (two-way, for refresh-state preservation),
  `Children` (all), `FilteredChildren` (category-filtered, reactive).  Bottom-up `ApplyFilter()`
  preserves ancestors when a descendant's category is on.  `MissingNamedPrimary` → `IsWarning = true`.
- **`ProjectTreeTool`** (rewritten, deletes 6b stub) — `FilterState`, `RootItems`, `HasWorkspace`,
  `SetWorkspace(dir)` / `ClearWorkspace()`, `[RelayCommand] Refresh()` (re-scans + preserves expand
  state), `RebuildVmTree(expandedPaths)`.

Views:
- **`ProjectTreeView.axaml`** (rewritten) — toolbar (Refresh button + Filter flyout with 7 checkboxes),
  no-workspace placeholder, `TreeView` with `TreeDataTemplate ItemsSource="{Binding FilteredChildren}"`;
  styles: `TreeViewItem` `IsExpanded TwoWay`, `.pt-bold` / `.pt-italic` / `.pt-warning`
  (`.pt-warning` uses `{DynamicResource CrfWarningBrush}`); per-kind Material icon + name `TextBlock`
  with conditional classes; tooltip = WarningReason (if warning) + RelativePath.
- **`ProjectTreeView.axaml.cs`** (rewritten) — on-focus refresh via `Window.Activated`, debounced with
  `_refreshPending` bool + `Dispatcher.UIThread.Post` at `Background` priority.

`WorkspaceViewModel` updated: `SetWorkspace(dir)` / `ClearWorkspace()` wired to `CurrentWorkspacePath`
change; `OpenTreeItem` and its coupling to the 6b stub removed.

`CrfWarningBrush` (`SolidColorBrush`) declared in `App.axaml`; updated from `ThemeService.Active`
`ColorRole.SystemWarning` in `App.axaml.cs` `UpdateCrfWarningBrush()`, subscribed to `ThemeChanged`.

10 new VM tests in `ProjectTreeNodeViewModelTests.cs`; 372 total green.

## Workspace scan + project tree model (Phase 6g Step 2 — done)

Three framework-free files in `src/Ui/Schematic/` (no Avalonia / Skia):
- **`ProjectTreeNode`** / **`NodeKind`** (`ProjectTreeNode.cs`) — the in-memory tree node.
  `NodeKind` covers all §3.3 filter categories: `Workspace`, `Cell`, `Library`, `LibrariesGroup`,
  `CellViewFolder`, `ViewFile`, `UserFolder`, `DataDisplayFile`, `ColorThemeFile`, `OtherFile`,
  `KnownFile`, `KnownFilesGroup`.  Per-node flags: `IsPrimary`, `IsTestBench`, `WarningReason`
  (non-null = System.Warning tooltip text).  Children are mutable only via `internal AddChild`
  (called only by the scanner).  The tree is transient — rebuilt on every refresh.
- **`WorkspaceScanner`** (`WorkspaceScanner.cs`) — `static Scan(rootDir)` walks the workspace folder
  into that model.  Filesystem is truth (no membership list consulted).  Delegates primacy entirely
  to `CellFolder.ResolvePrimary` (never re-derived).  Empty view sub-folders produce no node.
  Stable ordering: alphabetical (OrdinalIgnoreCase) within every level.  Tolerates missing/corrupt
  `.cws`.  Warning states recorded as DATA (MissingNamedPrimary, broken library paths, broken Known
  Files) so step 3 renders System.Warning + tooltip without the scanner caring about rendering.
- **`WorkspaceModel`** (`WorkspaceModel.cs`) — thin wrapper: `WorkspaceRootDir`, `RootNode`,
  `Rescan()`.  The step-3 view binds to this and calls `Rescan()` on focus/Refresh.
  No `FileSystemWatcher` (deferred per §9).

`CwsFile.KnownFiles` (`List<string>`) added to `WorkspacePersistence.cs` — additive v1 field;
old `.cws` files load with an empty list.  `LibraryRefs` now accepts folder paths or `.clib` file
paths (scanner takes the parent dir for `.clib`).  `MemberFiles` is retained for round-trip but
is ignored by the scanner (membership is the filesystem, not a manifest).

41 new tests in `WorkspaceScannerTests.cs` (L1 node model, L2 scanner, L3 refresh); 362 total green.

## Workspace cell-level building blocks (Phase 6g Step 1 — done)

Three framework-free files in `src/Ui/Schematic/` (no Avalonia / Skia):
- **`NameValidator`** — cross-platform-safe name validation (§1.4 charset; `IsValid`/`Validate`).
- **`CellPersistence`** / **`CcellFile`** / **`CcellParameter`** — `.ccell` read/write mirroring `SymbolPersistence`
  conventions (System.Text.Json, enum-as-string, format_version reject, `Id` never persisted).
  `CcellParameter` mirrors `EditableParameter`'s shape but holds the **default** expression (the cell's
  declared interface, not an instance override).
- **`CellFolder`** — `CreateCellFolder(parentDir, cellName)` creates `cellName/schematic/symbol/layout/` + initial
  `.ccell`.  **`ResolvePrimary(cellFolder, viewType)`** is the single source of primacy truth implementing
  the five-branch rule (workspace-and-project-tree.md §2): SoleFile → NamedPresent → MissingNamedPrimary →
  NoPrimary → NoView.  The **MissingNamedPrimary** contradiction is kept distinct (drives System.Warning).
  The filesystem is truth; no membership list is maintained here.

## Symbol Editor (Phase 6f steps 4a + 4b — editing shell + drawing tools)

The Symbol Editor is a **smaller sibling of the schematic stack**. Mirror its patterns; do not reinvent.

### Stack structure
- `EditableSymbol` (`src/Ui/Schematic/EditableSymbol.cs`) — mutable working copy of a `Symbol`. Commands
  hold a reference and call `NotifyChanged()` after mutation.  Framework-free.
- `SymbolGeometry` (`src/Ui/Schematic/SymbolGeometry.cs`) — `BboxOf(prim)`, `HitTest(prim, x, y, tol)`,
  `TranslateBy(prim, dx, dy)`, `ComputeBb(list)`. Framework-free.
- `SymbolEditorViewModel` (`src/Ui/ViewModels/SymbolEditorViewModel.cs`) — all 15 tools (Select + 14
  drawing tools), selection, move-drag, rubber-band, gesture state, current style (`ColorRole`,
  `StrokeTier`, `FontSize`, `FontStyle`), `Execute(IUiCommand)` on the VM's **own** `UndoRedoStack`.
  `SetActiveToolCommand` and `SetCurrentStrokeTierCommand` are `RelayCommand<string>` (parse enum from
  CommandParameter) so every toolbar button needs zero code-behind.
  `FontStyleOptions` is a public static array exposed as ComboBox `ItemsSource`.
- `Commands/Symbol/PlaceSymbolPrimitiveCommand` — appends primitive (topmost Z); Undo removes it. Both
  directions call `NotifyChanged()`. Mirrors Move/Delete commands.
- `Commands/Symbol/MoveSymbolPrimitivesCommand` + `DeleteSymbolPrimitivesCommand` — both directions call
  `EditableSymbol.NotifyChanged()`.
- `SymbolEditorCanvas` (`src/Ui/Controls/SymbolEditorCanvas.cs`) — Skia control with pan/zoom,
  delegates pointer/keyboard/TextInput to VM, cursor = Cross for drawing tools / Default for Select.
- `SymbolEditorRenderer` (`src/Ui/Renderers/SymbolEditorRenderer.cs`) — draws fine-grid (`p=5`),
  calls `SchematicRenderer.DrawSymbol` (reused, not duplicated), selection bboxes, rubber-band;
  renders `InProgressPrimitive` from overlay as dashed ghost via `DrawSymbol(overridePaint: ghostPaint)`.
- `SymbolEditorOverlay` — carries `InProgressPrimitive` (nullable), selection, rubber-band, drag offset.
- `SchematicRenderer.DrawSymbol` — now renders `TextPrimitive` for real (IBM Plex Sans, align, style).
- `EnumEqualsToBoolConverter` (`src/Ui/Converters/`) — `(enum, string param) → bool`; drives
  `Classes.ToolActive` binding on toolbar buttons.
- `SymbolEditorDocument` / `SymbolEditorView` / `SymbolEditorWindow` — dockable document + tear-off window.
  Both host the same `SymbolEditorView`; only the chrome differs.

### Drawing gestures
- **Two-point drag** (click + drag to release): Line, Rect, RoundedRect, Circle, Ellipse, Arc, Sine, HalfWave.
- **Multi-point click** (click per point; Enter or double-click to commit):
  Polyline (≥2 pts), Polygon (≥3 pts, auto-closes), Triangle (exactly 3, auto-commits),
  QuadCurve (exactly 3, auto-commits), CubicCurve (exactly 4, auto-commits).
- **Text**: click anchor → type → Enter commits; Backspace erases; Escape cancels; live cursor shows `|`.
  Uses Avalonia `TextInput` event (IME-safe), not raw `KeyDown`.
- **Escape** during any gesture: cancel (nothing placed). **Escape** with nothing in progress: clear selection.
- All snapped to `p = 5` local units via `SnapToP`.

### Pins (4c — done)
- **Two separate snap grids:** art snaps to `p = 5` (`SnapToP` in the VM); pins snap to `P = 100`
  (`SnapToConnectionGrid`, `PinGrid = 100.0`). Never use `SnapToP` for a pin; never use `SnapToConnectionGrid`
  for art.
- **Pin tool owns pin interaction.** The Select tool only touches primitives. Pin select/move/remap all
  live in `PinToolPress` / `PinToolMove` / `PinToolRelease` paths.
- **Unmapped port = open circuit, never an error.** Surface informally via `DrawUnmappedPortPanel` (soft-
  yellow overlay); do not block editing or flag as an error.
- **PortCount** is the number of ports the symbol maps pins to. Persisted in `.csym`. A `.csym` with
  `PortCount = 0` infers `PortCount = pins.Count` for backward compatibility (old files).
- **Locked gate:** `EditableSymbol.UserEditable = false` → `SymbolEditorViewModel.IsLocked = true` →
  all `Execute()` calls are no-ops; Pin / drawing tools disabled in toolbar; cross cursor reverts to arrow;
  "Read-only" shown in metadata bar. Built-in symbols opened via View menu are always locked.

### .csym I/O (4c — done)
- **Save/Save-As:** `SaveSymbolCommand`/`SaveSymbolAsCommand` on the VM; delegate to
  `SymbolPersistence.SaveToFile`. Both are `IAsyncRelayCommand<Window?>`.
- **Open:** File → "Open Symbol…" in the Workspace window → `WorkspaceViewModel.OpenSymbolFileCommand` →
  `SymbolPersistence.LoadFromFile` → `EditableSymbol.FromSymbol(symbol)` with `UserEditable = true`.
- **Built-in symbols opened from the View menu** are loaded with `UserEditable = false`.
- `.csym` format: JSON with `format_version`; reject-on-mismatch; alpha policy (no migration in v1).

### Deferred (do NOT build without discussion)
- Live schematic update — requires cell model / project-tree / workspace design (later phase).
- Cell-driven open — same dependency.
- Rewiring `SymbolKind → BuiltInSymbols` — same dependency.
- If a task seems to need the cell model, STOP and report (it's the project-tree design).

### Key invariants
- **`DrawSymbol` is shared** — `SchematicRenderer.DrawSymbol` is `internal static`; the editor calls it
  directly. Do NOT write a second symbol renderer.
- **All mutations undoable** on the document's own `UndoRedoStack`; `NotifyChanged()` in both Execute and Undo.
- **Art snaps to `p = 5` local units** (`SnapToP` in the VM). Pins snap to `P = 100` (`SnapToConnectionGrid`).
- **Color is a role** (`SymbolColorRole`), never literal RGB. No color picker in the editor.

### Opening the editor
`WorkspaceViewModel.OpenSymbolEditorDockedCommand` (View menu) opens on Resistor (docked).
`WorkspaceViewModel.OpenSymbolEditorWindowCommand` opens on Inductor (tear-off window).

---

## Symbol orientation (Phase 6f step 3 — standard library art)

**2-terminal symbols are VERTICAL** (R, L, C, V, Tone, Port/Term, GND): local pin 1 at `(0,-200)` (top),
pin 2 at `(0,+200)` (bottom). FET, ZPort, Sdd, Generic stay **horizontal** (ports left/right).

Schematic layout code consequence: place 2-terminal passives at `SymbolRotation.R90` in a horizontal signal
path (pin 1 right, pin 2 left at R90) — same `cx ± 200` wire coordinates as before. Bias-path vertical
components (at R0) need no rotation. See `docs/design/standard-library-symbols.md` for geometry.

---

## Color theming (Phase 6 — three-layer separation)

`src/Ui/Theming/` holds the **framework-free L1 theme model** (no SKColor, no Avalonia):
- `ColorRole` — string role constants (`Schematic.Background`, `System.Warning`, …); add a constant here to introduce a new role.
- `Rgba` — plain `record struct`, serializable via System.Text.Json.
- `ColorVariant` — `{ Light, Dark }` enum; independent of Avalonia's `ThemeVariant`.
- `ColorTheme` — role → RGBA maps for both variants; `Resolve(role, variant)` falls back to `BuiltIn` for absent roles. `ColorTheme.BuiltIn` is the single source of truth for default colors.
- `ColorThemeIo` — `.ccolor` read/write (System.Text.Json, `format_version` reject-on-mismatch, no Id persisted, keys sorted for stable diffs).

**Firewall note:** `src/Ui/Theming/` carries no Avalonia or SkiaSharp types and could migrate to `src/Core` without changes if another assembly ever needs it. Keep it that way.

**L2 projection** (`SchematicRenderTheme.FromTheme`): translates L1 roles to SKColor tokens for the renderer. L3 (not yet built): active-theme preference, workspace tracking, resolution order, Settings UI.

**Rule-of-role:** adding a new themable color = new `ColorRole` constant (L1) + read it in the relevant `*RenderTheme` token struct (L2). L1 and L3 never change for new colors.

## Phase 6d schematic editor — key rules (from 6d-fix)

### Per-segment wire drag convention (Phase 6d wire editing)
Wire segments are **independently selectable and draggable**. A direct click on a wire segment (not near an endpoint) returns `HitKind.WireSegment` with `SubIndex = segmentIndex`, and highlights only that segment. The drag convention is **perpendicular-only**:
- A **horizontal** segment can be dragged **vertically** only (dx zeroed out).
- A **vertical** segment can be dragged **horizontally** only (dy zeroed out).
- Dragging along the segment's own axis is not offered (delta constrained in `HandleSegmentDragLive`, not as a fixup after).

This preserves orthogonality automatically — moving a segment perpendicular to itself only lengthens/shortens its adjacent neighbors.

**Rubber-band: a segment move NEVER breaks a connection.** The dragged segment always translates by the perpendicular delta; whatever is connected is held in place by adding jog segments (`OrthogonalRoute`) rather than detaching:
- An **outer endpoint connected to anything** is held connected — but *how* depends on the drag axis (`ShouldPinDraggedEndpoint`):
  - a **port** or a coincident **wire vertex/corner** (a fixed point) is *pinned*: held in place with a jog bridging it to the moved segment;
  - an endpoint on a wire **body** is pinned only if that body is **perpendicular** to the drag (it would move off). If the body is **parallel** to the drag, the endpoint **slides along it** (no jog) — e.g. a horizontal wire joining two vertical wires slides down them as one straight segment, staying exactly 2 junction dots (a jog there would run along the verticals and spawn bogus extra dots).
- **Sliding is clamped** (`ComputeSlideClamp`): the perpendicular delta is bounded so a sliding endpoint can never run off the end of the wire it rides — the connection is never lost. Ranges from all sliding endpoints (and every wire each touches) are intersected, so the drag **stops at the shorter wire's end** when two parallel wires differ in length.

### Collinear overlap simplification
Two **collinear** wires (same line) whose spans overlap or abut are redundant and merge into their **union** (`WireGeometry.TryMergeCollinearOverlap`, tried in `TryBuildMergeCommand` **before** the endpoint-coincidence merge — the endpoint merge builds a back-tracking path that `NormalizePoints` would collapse, dropping part of the span when two collinear wires share an endpoint; guarded by `OverlapMergeBuriesT` so a merge can't bury a T-junction with a third wire). A connector wire dragged until both its ends coincide collapses to a zero-length wire; `DotRevalidationCommand` (the central post-edit cleanup wrapping every `Execute`) removes any wire that normalizes to &lt; 2 distinct points — undoably — so no stray zero-length wire (and its bogus junction dot) is left behind. A junction **dot** is only drawn where incident segments form a real *branch* — the auto-dot rule requires incident segments spanning **both axes** (a horizontal AND a vertical one), so 3+ *collinear* segments meeting (overlapping wires) draws no dot. Connectivity (`autoDotKeys`, endpoint-connected state) still counts any incident ≥ 3, so a collinear-overlap endpoint reads connected (no false red dot) even though no junction dot shows.
- When **both** outer ends are pinned (a single connected segment), jogs are added at **both** ends so the wire **bows out** — it is no longer frozen.
- Wires connected **on** the dragged segment follow it by the same delta so their junction stays attached: a **stem** T-ed onto its interior, a wire joined at one of its **moving vertices**, and a **user crossing-dot** on its interior (the dot slides along the stationary crossed wire). All folded into the one undoable `MoveCommand` (incl. `DotMoveSnapshot`), so a single Undo restores the wire, its followers, and the dots together. If a drag carries the wire entirely off a crossing, the now-invalid dot is removed by re-validation at commit (per the §5.1 dot invariant).

Each segment drag commits as a single `MoveCommand` with a `WireMoveSnapshot` (old points → new points), undoable. Live preview uses `SchematicOverlay.WireDragPoints` — no `BuildRenderModel()` per tick.

**Selection model**: `_selectedSegment: (string WireId, int SegmentIndex)?` in `SchematicViewModel` tracks the segment selection separately from `SchematicSelection` (which holds whole-object IDs). `SchematicOverlay.SelectedWireSegment` carries it to the renderer. Rubber-band selection still selects whole wires (`HitKind.Wire` from `TestRect`), not individual segments.

### Esc-key contract (both schematic and symbol editors)
**Rule:** Esc cancels whatever is in progress and returns to the Select tool. With nothing in progress (idle in Select), Esc clears the selection. Same semantics in both editors.

**Schematic (`SchematicViewModel.OnKeyDown` + `SchematicView.axaml.cs.OnKeyDown`):**
- `HasActiveOperation` is true when any non-Select tool is active, or a drag/rubber-band/segment-drag/inline-edit is in progress in Select mode.
- `if (HasActiveOperation) SetSelectTool(); else Selection.Clear();`
- `SetSelectTool()` sets `ActiveTool = Select`, calls `CancelCurrentOp()` (clears ghost/wire/drag state), then calls `_placementService?.Disarm()` if the previous tool was `Tool.Place`.
- **ARM-lives-in-PlacementService gotcha:** `ActiveTool = Select` alone does NOT disarm an ARMed palette item. The arm state lives in `PlacementService.Pending`. Always call `Disarm()` when leaving `Tool.Place` — `SetSelectTool()` does this automatically since the fix.
- Inline-edit Esc: `OnInlineEditKeyDown` calls `CancelInlineEdit()` + `DismissInlineEditBox()` + `SetSelectTool()`.

**Symbol editor (`SymbolEditorViewModel.OnKeyDown`):**
- Text-typing Esc → `CancelOp(); ActiveTool = Select`.
- Pin Esc → `ActiveTool = Select` (triggers `OnActiveToolChanged` which resets pin state).
- Any other in-progress op Esc → `CancelOp(); ActiveTool = Select`.
- Idle Esc → `ClearSelection()`.
- No `PlacementService` in the symbol editor — no Disarm needed.

### Wire draw-mode cursor and finish gestures (Phase 6d)
- Wire tool cursor: `StandardCursorType.Cross` (reverts to Default when tool changes away from Wire).
- **Enter** or **double-click** finishes the in-progress wire (keeps what was drawn) and returns to Select tool.
- **Esc** discards the in-progress wire (via `CancelCurrentOp`) and returns to Select.
- `< 2` distinct points: discarded, nothing placed.

### Incremental render rule (Item 1 / perf)
During an **active drag or nudge**, do NOT call `BuildRenderModel()` per tick. Update
`SchematicOverlay.ComponentDragPositions` / `WireDragPoints` only (O(k) for k moved objects).
`BuildRenderModel()` is deferred to drag-END. The connectivity pass inside `BuildRenderModel()`
is O(N) via spatial hash — never revert to the O(N²) linear scan.

### Display name registry (Item 8)
`ComponentTypeRegistry` in `src/Ui/Schematic/ComponentTypeRegistry.cs` maps `SymbolKind` →
`(DisplayName, InstancePrefix)`. **Always** read `ComponentTypeRegistry.DisplayName(kind)` for
the on-schematic type label — **never** call `kind.ToString()` or hard-code abbreviations in the
renderer. When the component model gains a richer type system, re-key the registry off that type.

### Id not persisted (Item 2)
`Id` on `EditableComponent`, `EditableWire`, `EditableNetLabel`, `EditableDot`, `EditableCanvasObject`
is **runtime identity only** — it must NOT appear in any persisted file (`.csch`, `.cws`, `.csym`,
`.cdd`). Fresh Ids are auto-generated on import. Tests compare content, never Ids.

### Move-Labels op (Item 14)
**F5** or the right-click "Move Labels" context menu entry enters Move-Labels mode.
- Phase `Picking` (nothing selected): next click picks which component to move.
- Phase `WaitFirstClick` (components already selected): next click sets the drag reference point.
- Phase `Moving`: mouse moves show live preview via `SchematicOverlay.LabelDragOffsets`; second click commits as a `MoveLabelsCommand`. **Esc always returns to Select.**
- Label offsets persist in `.csch` as `LabelOffsets: [[dx,dy],…]` on each component (omitted when all zero).
- `SchematicOverlay.LabelDragOffsets` carries `{compId → (DX,DY)}` during the drag. The renderer applies the delta on top of any existing per-label offset stored in `SchematicComponent.LabelOffsets`.

### Clipboard (Item 15)
`SchematicClipboard.CopyAsync()` places four formats on the clipboard simultaneously via Avalonia's `DataTransfer` API (richest first):
- `PdfNativeMacFormat` (`com.adobe.pdf` UTI, macOS) / `PdfNativeWinFormat` (`application/pdf`, Windows) — PDF bytes via `SKDocument.CreatePdf()`; recognised by Keynote, Preview, Pages, etc.
- `SvgNativeFormat` (`public.svg-image` UTI) — SVG bytes for macOS/Linux vector apps (Illustrator, Inkscape). Omitted on Windows (no well-known SVG clipboard format; EMF would be the Windows vector path — needs System.Drawing.Imaging + Svg.NET, follow splotRF's `WindowsClipboard.cs`).
- `DataFormat.Bitmap` — Avalonia `Bitmap` (PNG-backed raster; universal fallback for Keynote, Pages, Word, etc.).
- `DataFormat.Text` — JSON text (primary for Paste; cross-session portable). Always present even if rich formats fail.
- **Paste** reads `DataFormat.Text` (JSON) and wraps in `SchematicPasteCommand` (undoable).
- **Ctrl/Cmd+C / +X / +V** in the canvas raise events on `SchematicCanvas` (`ClipboardCopyRequested` / `ClipboardCutRequested` / `ClipboardPasteRequested`) handled async in `SchematicView.axaml.cs`.
- The JSON payload carries **`GridSize`** (= `P_src`). On paste, `SchematicPasteCommand` compares it to the destination `GridSize`; cross-grid content is snapped to `P_dst` and a warning is posted (see Grid & Connectivity below).

### Grid & connectivity (Phase 6 — standing rules)

**Authority:** `docs/design/grid-and-connectivity.md`. This section is a quick-reference; the design doc is the source of truth.

**Two grids, two jobs — never conflated:**
- **Connection grid `P`** (`GridSize`, default 100): every pin-in-world, wire endpoint, wire bend, junction dot lands on it **exactly** (integer multiple — equality not tolerance). Connection = coordinate equality, not proximity.
- **Authoring grid `p = P/k`** (`AuthorGridSize`, default `k=20` → `p=5`): label offsets, net-label positions, canvas objects. Use `SnapToAuthorGrid` for these. **Never** use `SnapToAuthorGrid` for electrical connection points.

**On-grid invariant (R7):** after any edit, every pin world-coordinate, wire vertex, and junction dot is an exact `P` multiple. `OnGridInvariantTests` guards this. Do not introduce any edit path that bypasses `SnapToGrid`.

**`SnapToGrid` vs `SnapToAuthorGrid`:**
- `EditModel.SnapToGrid(v)` → snaps to `P` — use for all electrical points (component origin placement, wire endpoints, wire bends, segment drag).
- `EditModel.SnapToAuthorGrid(v)` → snaps to `p` — use for label drag (`ComputeLabelDelta`), canvas-object drag. **Never** use this for wires or component origins.

**`ConnectTolerance = 0.5`** (float-dust guard only): connectivity is established at *input* by snapping to `P`, not by tolerance afterward. Do not raise `ConnectTolerance` or use it to bridge real gaps.

**Net labels are NOT on any grid:** a net label's position carries no electrical meaning. `EditableNetLabel.X/Y` may be any value. Do not snap net-label positions to `P` or assert them in R7.

**Cross-grid paste (§5):** `SchematicPasteCommand` accepts `sourceGridSize` and `messageSink`. When `P_src ≠ P_dst`, it snaps component origins and wire vertices to `P_dst` (using `Math.Round(v/P)*P`), canvas objects to `p_dst`, posts a `Warning` to `IMessageSink`, and validates R7 post-snap — all in the constructor so Execute/Undo/Redo are clean. `CopyAsync` embeds `model.GridSize` in the JSON; `PasteAsync` returns it; `SchematicView.axaml.cs` threads both through to the command.

## Phase 6b shell conventions (locked in — do not deviate without discussion)

### Dock library
- **Dock.Avalonia 12.0.0.2** + **Dock.Model.Mvvm 12.0.0.2** + **Dock.Avalonia.Themes.Fluent 12.0.0.2** —
  all three are required. Theme is loaded via `StyleInclude Source="avares://Dock.Avalonia.Themes.Fluent/DockFluentTheme.axaml"`.

#### Dock color system — how it works and how to override it
The theme's accent colors are defined in `avares://Dock.Avalonia.Themes.Fluent/Accents/Fluent.axaml`
(source: `src/Dock.Avalonia.Themes.Fluent/Accents/Fluent.axaml` in the wieslawsoltes/Dock repo).
Two tiers of resources exist:

**Tier 1 — primary accent family (hardcoded VS blue):**
```
DockApplicationAccentBrushLow      #007ACC  ← active tab background, splitter drag
DockApplicationAccentBrushMed      #1C97EA  ← hover tab background, splitter hover
DockApplicationAccentBrushHigh     #52B0EF  ← close-button hover
DockApplicationAccentForegroundBrush #F0F0F0 ← text on active (accent) tabs
DockApplicationAccentBrushIndicator #007ACC ← dock target indicators
```

**Tier 2 — StaticResource aliases** (resolved at theme load time, pointing into Tier 1):
```
DockTabActiveBackgroundBrush  → DockApplicationAccentBrushLow
DockTabActiveIndicatorBrush   → DockApplicationAccentBrushLow
DockTabHoverBackgroundBrush   → DockApplicationAccentBrushMed
DockTabCloseHoverBackgroundBrush → DockApplicationAccentBrushHigh
DockSplitterHoverBrush        → DockApplicationAccentBrushMed
DockSplitterDragBrush         → DockApplicationAccentBrushLow
DockSurfaceHeaderActiveBrush  → DockThemeAccentBrush → {DynamicResource SystemAccentColor} ✓
```

**Key rule:** `Application.Resources` wins over `StyleInclude` resources for `{DynamicResource}` lookups.
Overriding a Tier-1 key in `Application.Resources` fixes places that reference it directly.
BUT StaticResource aliases (Tier 2) are resolved at load time — their VALUE is baked in as the
original brush object. To fix those, you must ALSO override the Tier-2 alias keys directly in
`Application.Resources`. Both tiers are overridden in `App.axaml`'s `Application.Resources` block.

**Note:** `DockSurfaceHeaderActiveBrush` (tool panel active title bar) already chains to
`SystemAccentColor` via `DockThemeAccentBrush`, so it was correct without any override.

**What controls which UI element:**
- Active document tab strip item background: `DockTabActiveBackgroundBrush`
- Separator line between tab strip and content: `DockTabActiveIndicatorBrush`
- Tool panel active title bar (Project/Properties/Messages): `DockSurfaceHeaderActiveBrush`
- Splitter bar on hover/drag: `DockSplitterHoverBrush` / `DockSplitterDragBrush`
- **`CircuitRfDockFactory`** extends `Factory`; owns the layout tree. It exposes `MessagesTool?`,
  `ProjectTreeTool?`, `DocumentDock?`. Use `factory.OpenDocument(stub)` to add tabs in 6c+.
- Dock Tool/Document subclasses live in `src/Ui/ViewModels/Dock/`. Their views are wired via
  **`DataTemplate`** in `App.axaml` (NOT ViewLocator — Dock resolves its own templates from the
  `Application.DataTemplates` list).
- `SetFocusedDockable(IDock, IDockable)` requires the parent `IDock` container, not the tool itself.
  When programmatically activating a tool, use `SetActiveDockable(tool)` only unless you hold a
  reference to the container.
- **GOTCHA — document bodies must use the CACHED (non-deferred) content template.** Dock's default
  `DocumentControl` template (`DockDocumentControlSingleContentTemplate`, Fluent theme) wraps each
  document body in a **`DeferredContentControl`** that realizes content on a *background-priority
  dispatcher timeline*. The DataTemplate resolves correctly and the right view IS built — but its
  first **paint is deferred** and (in our app) does not flush until the next layout pass, so a
  newly-activated document stays unpainted (the previous tab's stale view lingers) until something
  forces a relayout (e.g. toggling a panel). The `IDeferredContentPresentation { DeferContentPresentation
  => false }` opt-out did NOT help (its readiness gate fails at the instant content swaps). **Fix
  (in `App.axaml`):** override `DocumentControl.Template` to Dock's other built-in template,
  `DockDocumentControlCachedContentTemplate`, which hosts each dockable in a plain `ContentControl`
  (no `DeferredContentControl`, no timeline) and paints on the normal layout pass:
  `<Style Selector="dockCtrl|DocumentControl"><Setter Property="Template"
  Value="{DynamicResource DockDocumentControlCachedContentTemplate}"/></Style>`. Trade-off: all open
  document bodies stay realized (cached) instead of lazily built — negligible at our tab counts, and
  tab switching becomes instant. Document views still resolve via the `App.axaml` DataTemplates.

### Command / undo-redo infrastructure — per-document stacks, focused-window routing

**The rule (do not violate):** Undo/Redo is per-document, resolved by the focused window.
- Each editable document VM (`SchematicViewModel`, `SymbolEditorViewModel`) owns its **own**
  `UndoRedoStack` (created internally, exposed as `public UndoRedoStack UndoRedo`).
- Each document wrapper (`SchematicDocument`, `SymbolEditorDocument`) implements `IUndoableDocument`
  (in `src/Ui/Commands/IUndoableDocument.cs`) and forwards `UndoRedo` to its VM.
- **Focused-window rule:** every window's Undo targets the document it is showing, never another.
  - **Main `WorkspaceWindow`:** `WorkspaceViewModel` tracks `_factory.DocumentDock.ActiveDockable`
    via `OnDocumentDockPropertyChanged`; its `Undo`/`Redo` commands route to that document's stack.
    Switching tabs re-subscribes `PropertyChanged` on the new stack so enable-state updates correctly.
  - **Tear-off windows** (`SymbolEditorWindow`): `Window.KeyBindings` bind to the document's own
    `ViewModel.UndoCommand`/`RedoCommand` directly — fully independent of `WorkspaceViewModel`.
  - **Dock-floated dockables** (user drags a tab into its own floating window): `WorkspaceViewModel.
    TryWireHostWindowsUndo` detects the new `HostWindow` via `ApplicationLifetime.Windows` scan
    (deferred one frame) and injects matching `KeyBindings` pointing at the floated document's stack.
    The host window's `Closed` event cleans up the subscription and removes it from `_wiredHostWindows`.
- **One keystroke owner:** do NOT handle undo/redo keys both at the canvas level and at the window
  level — choose one. The window `KeyBindings` are the authoritative path; canvas `OnKeyDown`
  handlers must **not** call `_undoRedo.Undo()` directly (and must set `e.Handled = true` for any
  keys they DO consume, so they don't bubble to the window binding and fire a second time).
- **Cross-document undo is impossible:** undoing in a symbol can never revert a schematic edit.
- **The Parameter Editor has NO independent stack.** It commits through `_schematicVm.Execute(...)`,
  so parameter edits live in the owning schematic's history and are undoable from that schematic.
  `ParameterEditorViewModel.UndoCommand`/`RedoCommand` delegate to `_schematicVm.UndoRedo` — they
  are only an affordance so the user can undo a parameter edit while the dialog is focused; they do
  not own a separate stack. Never give an inspector/properties panel its own undo stack.
- All user mutations still route through `IUiCommand` → the document's own `UndoRedoStack.Execute(cmd)`.
  Do not wire mutations that bypass the stack (except global file ops: New/Open/Save).
- Future document types (data display etc.) implement `IUndoableDocument` to participate automatically.
  Their windows follow the same focused-window rule: tear-off → own `KeyBindings`; Dock-floated →
  `TryWireHostWindowsUndo` picks them up automatically.

### Messages / IMessageSink
- **`IMessageSink`** lives in `src/Ui/Messages/` — not in Core/Engine (firewall respected).
  `MessagesTool` implements it. Obtain via `WorkspaceViewModel.Messages`.
- Always post from any thread: `MessagesTool.Post()` dispatches to the UI thread internally.
- Level semantics: Info = neutral status; Success = operation completed; Warning = degraded or
  unexpected but non-fatal; Error = operation failed, user must act.
- Icon + color both carry the level (never color alone — accessibility requirement from ui-design.md §2).

### Toolbar
- Avalonia 12 has **no built-in `ToolBar` control**. Use `Border` + `StackPanel Orientation="Horizontal"`
  with `Background="Transparent" BorderThickness="0"` buttons and `Border Width="1"` separators.

### Icons (Material.Icons.Avalonia 3.0.2)
- Always verify enum names against the `Material.Icons` 3.0.2 DLL before using them —
  many intuitively-named icons don't exist (e.g. `Chip`, `PanelLeftOpen`, `Undo`, `PlusBox`).
  Valid substitutes used in 6b: `IntegratedCircuitChip`, `PageLayoutSidebarLeft`, `UndoVariant`/`RedoVariant`, `FilePlus`.
- Context menus on `TreeView` items: place `<Grid.ContextMenu>` inside the `TreeDataTemplate` Grid
  so the DataContext is the item VM, not the tool VM.

### Fonts
Two font families are embedded in `Assets/Fonts/` and registered as `Application.Resources` in `App.axaml`:

| Resource key   | Family          | Files                                      | License                  |
|----------------|-----------------|--------------------------------------------|--------------------------|
| `DejaVuSans`   | DejaVu Sans     | `Assets/Fonts/DejaVuSans*.ttf`             | Bitstream Vera Fonts     |
| `IBMPlexSans`  | IBM Plex Sans   | `Assets/Fonts/IBM_Plex_Sans/static/*.ttf`  | SIL Open Font License 1.1 |

**IBM Plex Sans is static-only** (no variable fonts) because SkiaSharp does not support variable fonts.

**Avalonia controls** reference them via `{DynamicResource IBMPlexSans}` / `{DynamicResource DejaVuSans}`.

**SkiaSharp renderers** must use `SkiaFonts` (`src/Ui/Renderers/SkiaFonts.cs`) — lazy-loaded
`SKTypeface` instances sourced from the same embedded assets via `AssetLoader.Open()`. **Default
to `IBMPlexSans` for all renderer text** (labels, tick marks, annotations, tables). Fall back to
`DejaVuSans` only when broader Unicode coverage is needed (e.g. non-Latin axis labels).

```csharp
// Preferred — clean, modern, designed for screen
var tf = SkiaFonts.PlexRegular;    // Regular
var tf = SkiaFonts.PlexBold;       // Bold
var tf = SkiaFonts.PlexSemiBold;   // SemiBold
var tf = SkiaFonts.PlexItalic;     // Italic
var tf = SkiaFonts.PlexLight;      // Light

// Fallback — wide Unicode range
var tf = SkiaFonts.DejaVuRegular;
var tf = SkiaFonts.DejaVuBold;
```

Add additional weights by copying the `Lazy<SKTypeface>` pattern in `SkiaFonts.cs` — every static
file in `Assets/Fonts/IBM_Plex_Sans/static/` is embedded and loadable. Never call
`SKTypeface.Default` or `SKTypeface.FromFamilyName(...)` in renderers; that pulls from the host OS
and produces inconsistent cross-platform output.

**Firewall (load-bearing):** all UI-framework code lives here in `src/Ui`. `RfCore`/`Core`/`Engine`/`Cli`
reference **no Avalonia** — an enforced CI check fails the build otherwise (`ui-architecture.md` §3). Keep
Skia *rendering* separable from Avalonia *control hosting* so a re-skin keeps the renderers. The display
layer is circuitRF's own, `DataCube`-native (C1); splotRF is reference material, not a dependency.

## Framework & patterns
- **Avalonia 12**, single codebase for Windows/macOS/Linux. Mirror splotRF's structure, controls,
  and packaging recipes wherever possible — it's our proven sibling app (same stack, MIT).
- **MVVM via CommunityToolkit.MVVM.** Views are thin; logic lives in view models. No simulation
  logic in code-behind.
- **The GUI never simulates the design layer directly.** It always builds/edits the design layer,
  then asks the engine to elaborate and run. Results come back as a **`DataSet`** (named
  single-kind `DataCube`s).


### System Specific Differences Between Window, Linux, macOS
- macOS uses ⌘ symbol instead of Ctrl on Windows and Linux. Ensure this is respected in menus and tooltips text.
- The System file manager in macOS is called the "Finder", while Windows has an "Explorer". Respect this convention in menus and tooltip text when referring to the System File Manager.

## Schematic canvas — the performance-critical control (6c pattern, locked in)

### Render pipeline (do not change)
- **Control.Render → ICustomDrawOperation → ISkiaSharpApiLease → SchematicRenderer.Draw**
  - `SchematicCanvas : Control` — Avalonia host; owns pan/zoom state, pointer handling, DirectProperty bindings
  - `SchematicDrawOperation : ICustomDrawOperation` — captures a snapshot of viewport state; leases the Skia canvas
  - `SchematicRenderer` (static class) — pure Skia; no Avalonia types; re-skinnable
  - **NOT SKCanvasView** — that path is not composited correctly in Avalonia 11+
- See `splotRF/src/Controls/PlotControl.cs` for the proven pattern this is adapted from.

### World ↔ pixel transform
- `panX, panY` = world coords at the top-left corner of the canvas (in world units)
- `zoom` = pixels per world unit
- `px = (wx - panX) * zoom`; `wy = py / zoom + panY`
- Scroll-zoom: record world point under cursor *before* zoom change, adjust pan to keep it fixed after.
- Symbol local coords: 100 units = 1 grid square; standard component width = 300 units (body + leads)

### Performance
- **Viewport virtualization**: `SchematicSpatialIndex` (uniform grid, cell size 1500 world units, `Dictionary<(int,int),List<int>>`)
  — `QueryViewport(vpMinX,Y,vpMaxX,Y)` returns conservative candidate sets for components and wires.
  Never draw everything; the index prunes invisible items before the render loop.
- **LOD (level of detail)**: based on `compPixW = zoom × 300`
  - `< 6px`  → single filled rect (`LodRect` colour); skip all else
  - `< 22px` → body symbol lines only; skip port markers and text
  - `≥ 22px` → full: symbol + port markers (red box for unconnected) + labels
- **Grid**: adaptive — fine grid drawn when spacing ≥ 4px × 3; coarse-only (×10) when spacing ≥ 4px; skipped below 4px.
  Cap at 600 lines per axis.
- **FPS**: `static volatile long SchematicRenderer.LastFrameTicks` written by the renderer each frame.
  `SchematicView.axaml.cs` reads it every 333 ms via `DispatcherTimer` to update the toolbar readout.
  The renderer also draws an overlay in the top-right corner of the canvas (enabled by `ShowFps=True`).

### DirectProperty pattern
```csharp
public static readonly DirectProperty<SchematicCanvas, SchematicModel?> ModelProperty =
    AvaloniaProperty.RegisterDirect<SchematicCanvas, SchematicModel?>(
        nameof(Model), o => o.Model, (o, v) => o.Model = v);
```
Model setter rebuilds `SchematicSpatialIndex`, sets `_needsInitialFit = true`, calls `InvalidateVisual()`.

### Initial fit
`LayoutUpdated` event fires when `Bounds` is valid. A `_needsInitialFit` flag triggers `ZoomToFitInternal`
exactly once. The handler unsubscribes itself. Do NOT call `InvalidateVisual()` inside `Render()`.

### Adding a new canvas type (6d Data Display, etc.)
1. Create `MyRenderer` (static, no Avalonia — parallel to `SchematicRenderer`)
2. Create `MyCanvas : Control` with a `DirectProperty<MyCanvas, MyModel?>` and an `ICustomDrawOperation`
   nested class that leases Skia and calls `MyRenderer.Draw`
3. Create `MyView.axaml` with toolbar + `MyCanvas`; wire buttons in `MyView.axaml.cs`
4. Add `DataTemplate DataType="{x:Type ...}"` in `App.axaml`

- **Do NOT render components as individual styled controls.** A 10,000-component schematic will die
  that way. Render the canvas yourself via a custom control (`DrawingContext`, dropping to
  **SkiaSharp** for hot paths), with **viewport virtualization** and a **spatial index** (e.g.
  quadtree/grid) for hit-testing and pan/zoom.
- Lean on Avalonia 12's rendering work (deferred composition, dirty-rect tracking) but verify with
  a large stress schematic in `testdata/`, not a toy one.

## Wiring & auto-routing
- Obstacle-aware auto-routing must avoid drawing wires over placed symbols. Start with **orthogonal
  A\*** over a coarse grid using the spatial index for obstacles; refine later. Keep the router
  separable and testable headless.

## Editing model
- **Undo/redo via the command pattern** across all editors (schematic, symbol). Every mutation is a
  reversible command; nothing mutates model state directly.
- **Copy/paste via the system clipboard**, all or a selectable subset of a schematic, to/from other
  schematics.
- **Hierarchy navigation:** push into a sub-cell's schematic, edit, pop back. Editing a cell affects
  every instance; instance-level parameter overrides (root `CLAUDE.md` → expressions) stay per-instance.

## Data Display
The data-display layer is **circuitRF's own**, built fresh and **`DataCube`-native** (a trace is a slice of
a cube), living under `src/Ui` (see `docs/design/ui-design.md` / `ui-architecture.md`). It is **NOT** taken
from splotRF as a dependency and is not in RfCore. splotRF is **reference material only** — mine its proven
techniques (the three-coordinate-space transform pipeline, Smith/polar/rectangular rendering math, the
placeable-plot canvas, plot/trace/table rendering, MarkerInfoBox, autoscale-with-marker-preservation,
tick snapping, pan/zoom/hit-test) and **re-implement them cleanly against `DataCube`** — do not reimplement
from scratch ignoring splotRF's solved problems, and do not depend on splotRF's code. Keep it
UI-framework-light (clean Skia-render vs thin Avalonia-host split) so it stays re-skinnable and could be
lifted into a shared lib later if ever needed (not now). Support measured-vs-simulated overlay (a lab
Touchstone over a simulated result cube from the `DataSet`).

## "Easy" budgets (testable)
Honor the PRD §12 click budgets (e.g. placed FET → running HB power sweep in ≤ 8 actions). Advanced
settings must remain reachable but must not clutter the default path.

# circuitRF UI Coding Context

These are design-quality standards; the architectural rules above are non-negotiable structural constraints.

## Abstract

circuitRF UI development should prioritize accessibility, clarity, consistency, and professional presentation. Interfaces should follow modern Human Interface Guidelines where practical, using system fonts, semantic styling, adaptive layouts, intuitive interactions, and restrained motion. UI text and workflows should target RF engineers, researchers, technical managers, and advanced hobbyists. The overall goal is to create a polished, trustworthy, efficient engineering application suitable for advanced technical workflows.

---

# 1. Core Design Principles

* Prioritize usability, clarity, readability, and consistency.
* Follow Apple Human Interface Guidelines where practical, but treat them as preferred guidance rather than strict rules.
* Favor clean, minimal, engineering-oriented interfaces.
* Avoid visual clutter and unnecessary animation.
* Design for desktop workflows and technically sophisticated users.

---

# 2. Accessibility

## Text and Typography

* Default font size: 12–13 pt.
* Minimum font size: 9–10 pt.
* Support scaling text up to at least 200%.
* Avoid ultra-light font weights.
* Prefer:

  * Regular
  * Medium
  * Semibold
  * Bold

## Contrast

Use WCAG AA contrast guidance:

* Normal text under 18 pt: minimum 4.5:1 contrast.
* Large text (18+ pt): minimum 3:1 contrast.
* Bold text: minimum 3:1 contrast.

## Visual Communication

* Never rely on color alone to convey meaning.
* Use icons, shapes, labels, or patterns alongside color.
* Support color-blind accessibility.

## Layout Accessibility

* Ensure layouts adapt cleanly to large font sizes.
* Reduce truncation wherever possible.
* Prefer stacked layouts when horizontal space becomes constrained.

---

# 3. Colors

* Use system colors whenever possible.
* Avoid hardcoded colors.
* Support light and dark modes automatically.
* Use colors consistently across the application.
* Do not reuse the same color for conflicting meanings.

---

# 4. Icons and Symbols

## UI Icons

* Use Material.Icons.Avalonia for normal UI icons.
* Maintain consistency in:

  * stroke weight
  * scale
  * detail level
  * perspective

## Cell Symbols

Cell symbols are NOT UI icons.

They serve two purposes:

1. Visual identification of RF/electrical components.
2. Port connection geometry for schematic editing.

Do NOT use Material.Icons.Avalonia for schematic cell symbols.

---

# 5. Layout and Visual Hierarchy

## General Layout

* Group related content visually.
* Use spacing and alignment consistently.
* Prioritize important information near the top-left (LTR layouts).
* Avoid overcrowding controls.
* Ensure controls remain visually distinct from content.

## Content Flow

* Use progressive disclosure for advanced or hidden information.
* Avoid overwhelming the user with excessive detail at once.

## Window and Screen Usage

* Extend backgrounds fully to edges.
* Scrollable regions should fill available space.
* Respect safe areas and window chrome.
* Avoid placing critical controls at the bottom edge of windows.

---

# 6. Motion and Animation

* Use animation only when it improves clarity.
* Keep animations subtle, brief, and purposeful.
* Avoid decorative or excessive motion.
* Use motion to:

  * reinforce navigation
  * indicate scrolling
  * communicate state transitions

---

# 7. Localization and RTL Support

* Support localization using ResX.
* Consider right-to-left layout support where practical.
* Flip directional icons in RTL layouts.
* Do NOT flip icons representing real-world objects.

---

# 8. Typography and Semantic Styles

## Fonts

* Avalonia controls use the system UI font by default — do not override it globally.
* For custom Skia renderers, use **IBM Plex Sans** (`SkiaFonts.PlexRegular` etc.) as the default.
  Fall back to **DejaVu Sans** (`SkiaFonts.DejaVuRegular` etc.) only for wide Unicode coverage.
* Never call `SKTypeface.Default` or `SKTypeface.FromFamilyName(...)` in renderers.
* Create centralized semantic Avalonia styles for any explicit font overrides.
* Avoid excessive typeface variation.

## Hierarchy

Use typography consistently to indicate hierarchy:

* weight
* size
* spacing
* leading

## Leading

* Loose leading improves readability in large text blocks.
* Tight leading may be used in compact lists.
* Avoid tight leading for 3+ line text blocks.

---

# 9. UI Writing Style

## Tone

UI language should be:

* professional
* concise
* direct
* trustworthy
* technically appropriate

Target audience:

* RF engineers
* researchers
* technical managers
* advanced hobbyists

## Writing Rules

* Prefer active voice.
* Use clear verbs for actions.
* Avoid overly clever or playful wording.
* Use consistent terminology.
* Prefer concise labels.
* Avoid unnecessary possessive pronouns.
* Avoid vague “we” language in errors.

## Error Messages

* Make errors actionable and specific.
* Clearly explain:

  * what failed
  * why
  * how to fix it

---

# 10. Data Entry

* Dynamically validate fields while editing.
* Give immediate feedback on invalid input.
* Use numeric formatters for numeric fields.
* Do not request data that can be inferred automatically.
* Never prepopulate password fields.
* Disable Continue/Next actions until required data is valid.

---

# 11. Drag and Drop

## Behavior

* Support drag-and-drop broadly where practical.
* Support both move and copy semantics correctly.
* Support undo for drag operations whenever possible.

## Visual Feedback

Provide:

* drag previews
* insertion indicators
* valid/invalid destination feedback
* progress indicators for long transfers

## UX Expectations

* Keep dragged items selected after drop.
* Support multi-item drag where appropriate.
* Preserve styling when dropping rich text/content.

---

# 12. User Feedback

Feedback should communicate:

* status
* progress
* success/failure
* warnings
* recoverable problems

## Best Practices

* Keep feedback contextual and near related UI.
* Use passive feedback for status updates.
* Use interruptive alerts only for serious or destructive actions.
* Ensure all feedback mechanisms are accessible.

---

# 13. Engineering Expectations for AI UI Code Generation

When generating UI code for circuitRF:

* Prefer Avalonia UI conventions and semantic styles.
* Reuse centralized styling resources.
* Use adaptive/responsive layouts.
* Maintain accessibility standards by default.
* Favor readability and clarity over visual novelty.
* Avoid hardcoded dimensions/colors unless required.
* Use consistent spacing and alignment.
* Keep interfaces efficient for technical workflows.
* Ensure keyboard accessibility where practical.
* Minimize unnecessary dependencies and visual complexity.
* Treat schematic symbols separately from standard UI iconography.

---

# 14. Parameter Editor (Phase 6 / ParameterEditorView)

## One view, two hosts
`ParameterEditorView` (`UserControl`) + `ParameterEditorViewModel` are built once and hosted two ways:
1. **Embedded inspector** — in the Properties region (`PropertiesView`), bound to the active schematic's
   selection via `ParameterEditorViewModel.SetContext(vm)`. Activated by `PropertiesTool.SetActiveSchematic`.
2. **Dialog** — opened on component double-click (`SchematicView.axaml.cs :: OnComponentDoubleTapped`),
   configured via `ParameterEditorViewModel.SetTargetDirect(vm, comp, showClose: true)`. Uses `ParameterEditorDialog` Window.

Neither host decision (coexistence mode, modality) touches `ParameterEditorView` internals — both are
single swappable flags.

## Chosen defaults (owner experiments — change these single points)
- **Coexistence = content-switch** (parameter editor shown when one non-Ground component is selected,
  palette placeholder otherwise). Flag location: `PropertiesView.axaml` `<!-- COEXISTENCE_FLAG -->` comment.
  To switch to stack: replace the outer `Grid` with a `StackPanel` and remove the `IsVisible` toggles.
- **Modality = non-modal** (lets the user see schematic labels update live as they edit). Flag location:
  `SchematicView.axaml.cs :: OnComponentDoubleTapped` `const bool isModal = false;`.

## Units keyed by dimension
Unit ComboBox options come from `ComponentTypeRegistry.UnitOptions(UnitDimension)`, not per-`SymbolKind`.
Each `EditableParameter` carries a `Dimension` field (seeded at placement, type-change, and `FromRenderModel`).
`NumPorts` is never shown as a row. The single Ground/null guard is in `ParameterEditorViewModel.SetTarget`.

## Active-schematic wiring
`WorkspaceViewModel` subscribes to `DocumentDock.PropertyChanged` and calls
`CircuitRfDockFactory.PropertiesTool.SetActiveSchematic(vm)` when `ActiveDockable` changes.
`PropertiesTool` delegates to `EditorVm.SetContext(vm)`.

## Command stack discipline
All edits (expression, unit, ShowOnSchematic, instance name, label visibility) go through `SchematicViewModel.Execute`,
which wraps in `DotRevalidationCommand`. No-change guard in every commit method. `SetParameterVisibilityCommand`
(new in Phase 6) notifies in both Execute and Undo, consistent with all other mutation commands.

---

## Phase 7.0 deliverable — COMPLETE

**Per-run `.npy` results writer** — writes each run's `DataSet`s to disk so a future Data Display UI can
address them without re-running the simulation.

**New files:**
- `src/Ui/Schematic/RunResultsWriter.cs` — framework-free static class (no Avalonia); `SchematicKey`,
  `OwnerIdentity`, `WriteResults`.
- `tests/Ui.Tests/RunResultsWriterTests.cs` — 9 tests covering key derivation (4 cases) and `WriteResults`
  (5 cases: happy path, stale-clear, collision warning, same-owner re-run, empty skip).
- `tests/Engine.Tests/Export/NpyRoundTripAllAnalysesTests.cs` — 7 round-trip tests (S-param Hero 1 × 3,
  Loadpull Hero 3 × 4) ensuring `DataSetExporter → DataSetImporter` is lossless for every analysis type.

**Naming rule (LOCKED):** `<baseDir>/results/<schematicKey>/<analysisName>.npy`
- Cell-homed sole view: `<cellName>` (e.g. `Amp`)
- Cell-homed multi-view: `<cellName>.<viewStem>` (e.g. `Amp.tb2`)
- Loose file: file stem
- Scratch: `Sanitize(scratchId)`

**Collision guard:** `.source` marker file in the results dir; warns and skips if owned by a different cell.
The marker stores the owner identity **relative to `baseDir`** (the workspace root) when the owner lives
inside the workspace (`RunResultsWriter.NormalizeOwnerIdentity`), so **moving the whole workspace anywhere on
disk is NOT a collision** — baseDir, `results/`, and the cells move together and the relative path is stable.
`OwnerIdentity` still computes an absolute path; `WriteRun` relativizes it before storing/comparing. Migration:
a legacy ABSOLUTE marker that mismatches an inside-workspace (relative) owner is treated as a moved workspace
(`SameOwner` adopts + rewrites the marker to the relative form), not a collision. Genuinely different owners
(two cells with the same key, or an outside-workspace owner that keeps an absolute identity) still warn. Tests:
`WriteRun_WorkspaceMoved_AdoptsResultsWithoutCollision`, `WriteRun_DifferentInWorkspaceOwners_StillCollide`,
`WriteRun_DifferentOwner_PostsWarningWritesNothing`. **Do NOT revert the marker to an absolute path.**

**Within-run dedup:** `_2`, `_3`, … suffix appended when multiple analyses share a name in one run.

**`RunResult` change:** `IReadOnlyList<DataSet>?` replaced by `IReadOnlyList<AnalysisResult>?` (record
`AnalysisResult(string Name, DataSet Data)`). `RunResult.DataSets` convenience property preserves existing callers.

**`WorkspaceViewModel` hook:** calls `RunResultsWriter.WriteResults` in the `RunStatus.Success` branch of
`RunAnalysis`, using `baseDir = Path.GetDirectoryName(netlistPath)`.

Gate: Firewall 4/4 · Core 254/254 · Ui 721/721 · Engine 225/225 — all green.
