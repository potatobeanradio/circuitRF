# Brief — RF2: the widened-path cache key is not bucketed, so every zoom frame is a total miss

**Series:** `brief-rasterfill-0-overview.md` §1e.
**Scope:** one expression in `LayoutRenderer.DrawLayer`, and the cache entry it keys.
**Size:** small. It is in this series because it is measurable, not because it is large.

---

## 0. The finding

`LayoutRenderDetail.ToleranceDbu` buckets its answer **down to a power of two**, and says plainly why:

> a decimated contour is cached and must be rebuilt whenever the tolerance changes. Taken straight from
> the zoom, that is every frame of a zoom gesture — the rebuild would cost more than the saving.
> Bucketed, a zoom moves through an entire octave before anything is rebuilt.

The widening allowance two lines away in `DrawLayer` was never given the same treatment:

```
LayoutRenderer.cs:1263   long widenDbu = devicePxPerDbu > 0
                             ? (long)Math.Ceiling(GeometryStrokeDevicePixels / devicePxPerDbu) : 0;
```

`widenDbu` is the cache key for `LayoutPathCache.GetOrBuildWidened` (`Entry.WidenedAtDbu`). At board
fit on board A, `devicePxPerDbu ≈ 3.08e-5`, so `widenDbu ≈ 64,935` — and a **0.1%** zoom change moves
it by ~65. Different key, total miss, every hairline path on every visible layer rebuilt.

`GetOrBuildWidened`'s own doc comment states the intended behaviour:

> it changes with zoom and only with zoom. A pan is all hits; a zoom step rebuilds the working set once.

That is true of a discrete zoom *step*. A trackpad pinch or a smooth wheel zoom is a **new value every
frame**, so "once" is once per frame.

Measured, board A, board fit, 1600×1000 — frames that change zoom by 0.1%, i.e. a visible set that is
for all practical purposes identical:

| Zoom | repaint | 0.1%-per-frame zoom | paths rebuilt per frame |
|---|---|---|---|
| board fit | 288 ms | **336 ms (+17%)** | **192,680** |
| 2× | 135 ms | 162 ms (+20%) | 87,904 |
| 4× and beyond | — | no penalty | 0 — the hairline tier has disengaged |

192,680 is larger than the shape count because `BuildPathOutline` builds three `SKPath`s per
`PathShape`.

## 1. Requirements

**R-rf2-1. `widenDbu` is bucketed so that it is constant across a zoom octave.** Follow
`LayoutRenderDetail.ToleranceDbu` exactly — the same power-of-two bucketing, the same direction, the
same reasoning — rather than inventing a second scheme. Two tiers that quantize zoom differently are
two things to keep in step.

**R-rf2-2. The bucketing may only ever widen, never narrow.** This is the one asymmetry that matters
and it is *not* the same as the tolerance case. `detailDbu` bucketed **down** makes the decimation
finer than requested, which is safe because the error bound only tightens. `widenDbu` is a *visibility
substitution*: the widened fill stands in for a fill-plus-outline, and if the widening comes out
smaller than the pen it replaces, the shape is drawn **thinner than it should be** and hairline
geometry starts to disappear. So bucket to a power of two that is **≥** the raw value. State this in
the code comment, because the obvious symmetry with `ToleranceDbu` is a trap.

**R-rf2-3. The footprint claim in `GetOrBuildWidened` stays true, or its comment changes.** That
method documents the substitution as *exact*: "a `PathShape`'s fill IS its centreline stroked at
`Width`, so fill-then-outline and fill-at-`Width`-plus-the-pen cover the identical region." Bucketing
makes the widening **up to 2× the pen** rather than exactly it, so the footprint is no longer
identical — it is a bounded over-cover. Say so there, in those terms, and say what bounds it. A
comment that still claims exactness after this change is worse than no comment.

**R-rf2-4. The visual consequence is stated and measured, not assumed.** The tier only runs where a
path is under one device pixel wide, and the over-cover is at most one further device pixel, so the
change is expected to be imperceptible. **Confirm it with a differential render** rather than by
argument — and if it is perceptible at the octave boundary, halve the bucket (quarter-octaves) rather
than abandoning the bucketing.

**R-rf2-5. `GetOrBuildWidened`'s doc comment is corrected.** "a zoom step rebuilds the working set
once" was written about a discrete step and is what let this survive. Replace it with what is now
true: a zoom rebuilds once per octave, and a continuous gesture inside one octave rebuilds nothing.

## 2. What not to do

- **Do not bucket down.** R-rf2-2. The failure is hairline geometry thinning or vanishing — the exact
  class of defect the substitution tier exists to prevent.
- **Do not make the cache key tolerant instead** (accept an entry whose `WidenedAtDbu` is "close
  enough"). That reintroduces per-entry drift with no octave boundary to reason about, and two shapes
  drawn in the same frame could then carry different widenings.
- **Do not extend this to `Width` itself.** The stored width is model data; only the *allowance* is
  being quantized.
- **Do not fix this by enlarging the cache.** It is a 100% miss rate, not a capacity problem —
  `LayoutCanvas.PathCacheCapacityFor` already sizes the cache to the document.
- **Do not add a timing test.** §3.

## 3. Gates

1. **The key is stable across an octave.** Pure unit test on the bucketing function: every zoom in
   `[z, 2z)` yields one `widenDbu`, and the boundary yields the next. No rendering, no clock.
2. **A micro-zoom frame rebuilds nothing.** Render a hairline-heavy layout, then render it again at
   1.001× zoom: `PathsConstructed` must be **0** on the second frame. This is the brief in one
   assertion, and it is a counter, not a timing.
3. **An octave boundary rebuilds once, and only once.** Crossing it rebuilds the working set; the
   frame after it at a further 0.1% rebuilds nothing.
4. **The widening never shrinks.** Property test: for every zoom sampled across several decades,
   bucketed `widenDbu` ≥ raw `widenDbu`. R-rf2-2.
5. **Differential render at the boundary.** The frames either side of an octave boundary differ by no
   more than the documented bound — a pixel-coverage comparison, the same oracle
   `LayoutRenderDetail`'s own density gate uses. R-rf2-4.
6. **Pan is unaffected.** A pan at fixed zoom still reports `PathsConstructed == 0`, exactly as today.

## 4. On completion

1. Record in **`src/Render/RESOLVED.md`**: the measured before/after from §0, the bucketing direction
   and **why it is opposite to `ToleranceDbu`'s** (R-rf2-2 — this is the part worth writing down), and
   the outcome of the R-rf2-4 differential render.
2. Correct the two comments named in R-rf2-3 and R-rf2-5 in the same change. They are the reason this
   was not found earlier.
3. **Do not write any of this to a `CLAUDE.md`.**
