# Brief — RF1: a flat document pays 12 ms a frame to be told it has no instances

**Series:** `brief-rasterfill-0-overview.md` §1d. Read that first for the measurement this is sized by.
**Scope:** `LayoutRenderer.Draw`'s second spatial query, and the `LayoutSpatialIndex` overload behind it.
**Not in scope:** anything about how shapes are drawn. This brief never reaches `DrawLayer`.

---

## 0. The finding

`LayoutRenderer.Draw` queries the spatial index **twice** per frame:

```
LayoutRenderer.cs:528   var candidates         = view.SpatialIndex.QueryIntersecting(view.Shapes, viewportRect);
LayoutRenderer.cs:659   var instanceCandidates = view.SpatialIndex.QueryIntersecting(
                            view.Shapes, view.Instances, InstanceBboxFor, CellLayoutResolver.Generation, viewportRect);
LayoutRenderer.cs:661   counters.InstancesExamined = instanceCandidates.Count(e => e.Kind == SpatialEntryKind.Instance);
```

The second overload (`LayoutSpatialIndex.cs:187`) returns **every entry in the rect, shapes included**,
into a `List` that is never pre-sized, and then sorts the lot through a comparison delegate
(`LayoutSpatialIndex.cs:232`).

Measured on board A (52,230 shapes, **zero instances**), board fit, 1600×1000:

| | per call | entries | allocation |
|---|---|---|---|
| shape-only query (`:165`) | 2.66 ms | 51,378 | — |
| **combined query (`:187`)** | **12.42 ms** | 51,378 — all of them shapes | **1,026 KB** |
| the `.Count(…)` over its result | 0.16 ms | → **0** | — |

A frame with **every layer hidden**, drawing nothing and issuing no draw calls, still costs **19.1 ms
and allocates 4,346 KB**. Most of that is this call.

**This is not about raster-fill boards.** It is proportional to the candidate count, so it costs any
large flat document — and "flat" is the normal shape of an imported board, which never has instances
at all.

## 1. Why the second query exists, and what of that is real

Three things are genuinely riding on it, and a fix that breaks any of them is worse than the cost:

1. **It draws the instances.** `instanceCandidates` is passed to `DrawInstances` (`:697`).
2. **It is what puts instance entries into the index.** `Draw`'s own comment says the position of this
   call above the layer loop is load-bearing: `CanAffordOutlines` reads `SpatialIndex.Extent`, and on
   the first frame of a schematic-generated layout — which has *no* top-level shapes — asking before
   the instances were indexed returned an empty extent and produced a one-frame flicker.
3. **It maintains the instance side against staleness** — `_syncedInstanceCount`, `_instancesDirty`
   and `_syncedResolutionVersion` (`:216-217`). An instance list that has just become *empty* is
   still stale, and still needs a refresh to evict the entries it used to own.

What is **not** real is the shape side. The shape-only query at `:528` already rebuilds the shape
index (`:168-170`) before this call runs, so the combined overload's shape traversal, its shape
entries, and its sort over them are pure duplication on the render path.

## 2. Requirements

**R-rf1-1. A document with nothing to index on the instance side, and nothing indexed there already,
skips the combined query entirely.** Both halves of that sentence are required. `view.Instances.Count
== 0` alone is *not* a sufficient guard: a document whose last instance was just deleted has an empty
list and a dirty index, and skipping there would leave stale instance entries to be drawn and to widen
`Extent`. The guard is "the live list is empty **and** the index agrees it is already synced to empty"
— which is exactly the staleness question `:216-217` already asks. Expose it as a cheap, locked
predicate on `LayoutSpatialIndex` (for example `InstanceSideIsEmptyAndClean(instances, resolutionVersion)`)
rather than re-deriving the condition in the renderer, so the two can never drift.

**R-rf1-2. When the query is skipped, the frame behaves exactly as it does today.**
`InstancesExamined` is 0 (it already is), `DrawInstances` receives an empty sequence, and
`CanAffordOutlines` reads the same `Extent` — which it does, because with nothing on the instance side
the extent is the shape extent either way. Prove this rather than assert it: R-rf1-6.

**R-rf1-3. The combined query pre-sizes its result list.** `new List<LayoutSpatialEntry>()` grown by
doubling to 51,378 entries is ~17 reallocations, several of them on the Large Object Heap, every
frame. The node walk can count first, or the list can be seeded from `_syncedCount + _syncedInstanceCount`.
This is worth doing **independently of R-rf1-1**, because a document that genuinely has instances still
pays it.

**R-rf1-4. The sort keeps its contract but not its delegate.** The order — ascending `Index`, ties
broken by `Kind` — is relied on by consumers and must not change. A `static` comparison delegate on a
51,378-element sort is ~800,000 indirect calls; an `IComparer<LayoutSpatialEntry>` struct, or a sort
key packed into a single integer, removes the indirection without touching the order. **Measure before
keeping this one** — if R-rf1-1 and R-rf1-3 already close the gap on documents that have no instances,
this only matters for documents that do, and it may not be worth the code.

**R-rf1-5. Nothing about hit-test or marquee changes.** Those consumers call the same combined overload
deliberately (its doc comment at `:179` says so) and are not on a per-frame path. Any guard added for
the renderer must not change what *they* see.

## 3. What not to do

- **Do not skip the query on `view.Instances.Count == 0` alone.** R-rf1-1. The failure is silent and
  only appears on the frame after a delete.
- **Do not move the call below the layer loop** to "only pay for it when needed". That is the exact
  ordering `Draw`'s comment says caused a one-frame flicker on open, and the reason
  `LayoutInstanceCoarseTierTests` could not get two identical renders.
- **Do not replace the combined query with the shape-only one plus a separate instance query.** That is
  two traversals where there is currently one, and it re-opens the staleness dance the combined
  overload's two-pass lock protocol exists to solve.
- **Do not cache the query result across frames.** The viewport changes every frame of a pan; that is
  the one thing this result is a function of.
- **Do not add a timing test.** §4.

## 4. Gates

Per the repo's standing rule, **none of these assert a millisecond figure** — a timing gate measures
the machine and flakes. Each asserts the structural property that produced the time.

1. **The query is not made.** On a `LayoutView` with shapes and no instances, a frame issues exactly
   **one** spatial query. Assert it with a call counter on the index (test-only, `internal`), not by
   timing. This is the whole brief in one assertion.
2. **A deleted last instance still evicts.** Place one instance, draw a frame, remove it, draw again:
   the second frame must not draw it and `Extent` must not include it. **This is the test R-rf1-1's
   naive guard fails**, so write it before writing the guard.
3. **A document with instances is untouched.** `InstancesExamined`, `InstancesDrawn` and the rendered
   output are identical to today's for a layout with placements, including an array.
4. **A schematic-generated layout still gets its extent on frame one.** Two successive renders of one
   viewport of a layout whose geometry is *all* in placed cells are pixel-identical — the flicker
   regression named in §3. `LayoutInstanceCoarseTierTests` already owns this shape of assertion.
5. **Allocation, as a counter not a clock.** `GC.GetAllocatedBytesForCurrentThread()` around a steady
   -state frame on a large flat document: assert it is below a generous fixed ceiling. Allocation is
   deterministic in a way wall-clock is not, so this one is safe to pin; set the ceiling from
   measurement with headroom, and state the measured value in the comment.
6. **Ordering contract.** The combined query's result stays sorted by `Index` then `Kind` after
   R-rf1-3/4. Property test over a randomised index.

## 5. Measuring it

A scratch harness, **Release**, not a `Category=Benchmark` test — see the overview §1. Drive
`LayoutRenderer.Draw` against a CPU `SKSurface` with a persistent `LayoutPathCache` sized as
`LayoutCanvas.PathCacheCapacityFor` sizes it, and report `GC.GetAllocatedBytesForCurrentThread()`
per frame alongside the wall clock. Hide every layer to isolate the floor from the drawing.

The before figures to beat are in §0 and the overview §1d.

## 6. On completion

1. Record the findings in **`src/Render/RESOLVED.md`** — the before/after floor and per-frame
   allocation, the guard's two-part condition and *why* the one-part version is wrong (R-rf1-1), and
   whether R-rf1-4 was kept or measured away. Create the file's section if it needs one.
2. If the index gained a test-only counter or a new predicate, note it in
   **`src/Design/RESOLVED.md`** beside the spatial index's own entries.
3. **Do not write any of this to a `CLAUDE.md`.** Findings go in the sibling `RESOLVED.md`.
4. Re-measure the overview §1b table and report it, since briefs 3 and 4 are sized against it.
