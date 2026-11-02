# Sonnet Brief — Phase L2c: LOD, the merge tier, and path caching

**Design:** `docs/design/layout-view.md` §5.1 (budget), §5.3 items 2 and 3, §2.3 R8a/R8b.
**Consumes L2a's baseline and L2b's before/after table** — both at the top of `src/Ui/CLAUDE.md`. Read them
first; this brief is sized by what those measurements left unsolved.

**Scope is L2c: close the full-extent frame cost.** This is the last L2 brief.

---

## 0. Exactly one problem remains, and two of the three obvious levers won't fix it

L2b solved every interactive cost: hit-test is 1,000–7,000× faster, pan at 500k dropped to ~0.1 ms median,
and a realistic local marquee went from seconds to ~130 ms per 100 moves. What culling could not touch is the
frame where **everything is visible**, because nothing is off screen to cull:

| | full-extent, after L2b | §5.1 target | Over budget |
|---|---|---|---|
| 50k | 78–244 ms | 16.6 ms | **5–15×** |
| 500k | 628–2,602 ms | 50 ms | **13–52×** |

Now the uncomfortable part, from L2a's own numbers:

- **The merge tier is a 2–20% win, not a fix.** The single-layer sweep found merged fills only 1.02–1.21×
  cheaper than per-shape, and the 200-layer profiles showed 15–25%. Useful. Nowhere near 13–52×.
- **Draw-call count is not the dominant cost.** Merging collapses ~50,200 draw calls to ~400 at 50k and buys
  only ~20%. So the time is in **rasterization and path construction per shape**, not in call overhead.
- **Path caching helps repeated frames, not the first one.** It directly targets the measured
  CurveHeavy ≈ 3× Manhattan cost (`BuildPathOutline` builds 3 `SKPath`s per `PathShape`), but a cold
  full-extent frame still has to build everything once.

**LOD is the lever that can actually close the gap**, because at full extent the overwhelming majority of
shapes are smaller than a pixel. Build the LOD tier first, re-measure, and only then decide how much of the
rest is still needed. That is the same measure-then-optimize discipline L2a and L2b were run under, applied
inside a single phase.

---

## 1. LOD — do this first, and measure before continuing

§5.3 item 3 says to "drop anything whose screen bbox is under ~2 px." **Dropping is wrong for the case that
matters**: a dense copper pour made of thousands of sub-pixel shapes would render as empty space, which is a
correctness regression disguised as a speed-up.

**R-L2c-1. Sub-pixel shapes are aggregated, not dropped.** A shape whose screen bbox falls below the
threshold contributes a minimal rect (its bbox, clamped to ~1 px) to **one batched path per layer**, filled
once. No per-shape `SKPath` construction, no per-shape composite, no per-shape draw call. The picture keeps
its density; the work collapses.

Two consequences worth stating up front:

- **Sub-pixel shapes stop darkening on overlap.** At a zoom where a shape is under a pixel this is
  imperceptible, which is precisely the argument §2.3 R8b already makes for the merge tier. Say so in the
  code comment so it is not later "fixed" back into per-shape compositing.
- **This is the same mechanism as the merge tier.** LOD aggregation and R8b merging both mean "one batched
  fill per layer instead of N composited ones." Implement one mechanism with two triggers (below-pixel-size,
  or above-shape-count) rather than two code paths that drift.

**Threshold**: start at ~2 device pixels, expose it, and tune from measurement rather than taste.

**Gate this before building anything else in this brief** — re-run the harness after LOD alone and record the
numbers. If full-extent lands inside budget at both 50k and 500k, §§2–3 may be unnecessary or much smaller
than planned, and that is a good outcome to discover before writing them.

## 2. The merge tier (R8b)

Now a UX decision rather than a performance one — L2a found merged always cheaper, so there is no break-even
point to discover and §2.3 R8b has been updated to say so.

**R-L2c-2. One mechanism, two triggers.** The batched-per-layer fill built for §1 is the merge tier. It
engages when a layer's visible shapes exceed a threshold **or** when they are sub-pixel. A user preference
forces it on permanently, per R8b.

Set the count threshold from §1's measurements. L2a's data supports going **lower than the design doc's
original "~20k" guess**, since merging is never a performance loss — the only cost is the R8a darkening
behaviour the user chose, so the threshold should sit where overlap-darkening genuinely stops being
perceptible, not where a performance cliff was assumed to be.

**Strokes already batch** and are unchanged.

## 3. Per-shape path caching

Targets the measured CurveHeavy ≈ 3× cost and repeated frames at moderate zoom.

**R-L2c-3. Cache paths in shape-local space, not path space.** This is the trap. L1a's R-L1a-1 builds paths
in a *per-frame viewport-anchored* origin — so a path cached at one origin is wrong after any pan, and a
naive cache would be invalidated on essentially every frame, doing net harm. Cache each shape's path relative
to **its own bbox minimum**, then draw it under a per-shape translate that maps local → path space. Path
construction is expensive; a matrix concat is not.

Note the interaction: per-shape matrices are incompatible with batching into one path, so the cache serves
the **per-shape (darkening) tier** and the batched tier serves §§1–2. Different tiers, different mechanisms —
that is fine, and it is why §1 comes first.

**R-L2c-4. The cache is bounded.** 500k cached `SKPath`s is a large amount of native memory, and an unbounded
cache can cost more than it saves. Cap it (LRU over the recently-drawn set), make the cap configurable, and
**measure memory as well as time** — report both. If the measured win does not justify the memory, say so and
leave it out; that is a legitimate outcome.

**Invalidation** rides on L2b's `LayoutChangeInfo` payload, which already distinguishes `Full` / `Appended` /
`RemovedTrailing` / `Updated`. Reuse it — do not invent a second notification path.

## 4. Deliberately NOT in scope

- **Tiled raster cache.** §5.3 lists it as deferred until measured. If §§1–3 close the gap it is unnecessary;
  if they do not, it becomes a properly-scoped L2d with real numbers behind it. Do not start it here.
- Instance path caching (§5.3 item 4) — instances do not exist until L3.
- `.clay` load time and file size — quantified by L2a, explicitly out of L2b/L2c's rendering scope.
- Any change to the rendering contract beyond what R-L2c-1 states about sub-pixel compositing.
- `src/Core`, `src/Engine`, `RfCore`, the schematic and symbol editors.

## 5. Gate (acceptance)

Counters remain the CI gate; wall-clock is reported (R-L2a-3).

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. **LOD measured alone, before §§2–3 exist** — full-extent numbers at 50k and 500k for all three profiles,
   recorded. This is the phase's key decision point.
3. **Density is preserved (R-L2c-1)** — a dense cluster of sub-pixel shapes rendered at full extent produces
   a filled region, not empty space. Off-screen pixel test comparing coverage against the pre-LOD render
   within a tolerance. **This is the regression that "drop below 2px" would have shipped.**
4. **LOD does not engage when zoomed in** — at a zoom where shapes are comfortably above threshold, output is
   **pixel-identical** to L2b's renderer and per-shape compositing (including overlap darkening) is intact.
5. **Counters reflect the tier** — with LOD engaged, `PathsConstructed` and `DrawCalls` collapse to
   O(layers), not O(shapes); with it disengaged they match L2b exactly.
6. **Merge tier thresholds** — engaging by shape count and by sub-pixel size both route through the *same*
   batched code path (assert one mechanism, not two).
7. **Path cache correctness (R-L2c-3)** — a cached shape renders pixel-identically after a pan that changes
   the frame origin. This is the test that fails if the cache is keyed to path space.
8. **Cache invalidation** — after each of `Full` / `Appended` / `RemovedTrailing` / `Updated`, cached paths
   match freshly-built ones for every affected shape.
9. **Cache is bounded (R-L2c-4)** — memory at 500k stays under the configured cap; both time *and* memory are
   reported.
10. **Final numbers against §5.1** — state plainly, per profile and scale, which targets are met and which
    are not. If 500k full-extent is still over the 50 ms floor after all three items, say so and recommend
    whether a tiled cache (L2d) is worth it, with the measured shortfall as evidence.

## 6. On completion

1. Add a "Phase L2c — COMPLETE" entry at the top of `src/Ui/CLAUDE.md` with the **LOD-only** table from
   gate 2, the **final** table after all three items, and memory figures for the path cache. Call out:
   **R-L2c-1** (aggregate, don't drop, and why dropping would have been a correctness regression);
   that **LOD and the merge tier are one mechanism with two triggers**; **R-L2c-3** (shape-local caching, and
   the path-space trap it avoids); and the chosen thresholds with the measurements behind them.
2. State whether **Phase L2 is complete** against §5.1, honestly — including any target still unmet and what
   it would take.
3. Report back before **L3 (hierarchy)** is briefed.
