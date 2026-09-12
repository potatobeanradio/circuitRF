# Brief — a raster-fill Gerber board renders at 2 FPS: the series

**Owner report, 2026-09-12.** Layout rendering had been tuned and was performing well, then a
different imported board panned and zoomed at a visibly low frame rate. This series is the
investigation's answer and the four pieces of work it produced.

This file is the **overview only**. It is not implementable. Each numbered brief beside it is.

---

## 0. The short answer

**The renderer's existing tiers are all working. The geometry is the problem.**

The board is a Gerber import whose copper pours are stored as *raster fill*: instead of a filled
polygon, each pour is tens of thousands of abutting one-mil-wide scanline strokes. Two layers of it
carry **41,420 such strokes totalling 321 metres of centreline**, and every frame Skia stroke-to-fills
and scan-converts the whole thing.

Every measurement below confirms the L2c machinery is doing its job and cannot do more:

- `PathsConstructed` is **0** on every repaint and pan frame, at every zoom — the path cache serves
  the entire working set (`LayoutCanvas.PathCacheCapacityFor` sizes it to the document, so the 50,000
  default that a previous import blew past is not in play here).
- The hairline-elision tier collapses a 29,274-shape layer into **4 draw calls**.
- Forcing outlines off changes nothing, because `CanAffordOutlines` has already turned them off.

What is left is **rasterization**, and rasterization is proportional to painted area. No batching,
caching or culling change touches it. That is brief 4 (a tiled raster cache) or, better and cheaper,
brief 3 (do not create 41,420 strokes in the first place).

Alongside it the investigation found **two genuine defects** that are not about this board at all —
they cost every large flat document — and those are briefs 1 and 2.

---

## 1. What was measured, 2026-09-12

A scratch console harness in **Release**, driving the real `LayoutRenderer.Draw` against a CPU
`SKSurface` at 1600×1000, with a persistent `LayoutPathCache` sized exactly as `LayoutCanvas` sizes
it. Not a `Category=Benchmark` test — per the repo's standing practice, measurement of this kind uses
a scratch harness, because `dotnet test` builds Debug and would invert the managed-vs-native split.

Three imported boards, named here **A**, **B** and **C** rather than by product, per the repo's
no-commercial-vendor-references rule. All are ordinary Gerber/PCB imports supplied by the owner.

### 1a. Board A — the reported one

| | |
|---|---|
| Shapes | **52,230** (0 instances, 11 layers) |
| Breakdown | 50,617 `Path`, 634 `Circle`, 521 `Rect`, 295 `Via`, 161 `Poly`, 2 `Label` |
| Paths 25,400 DBU (1 mil) wide | **46,823** |
| Path vertex count | median **2**, max 16 — they are line *segments*, not outlines |
| Layer "bottom copper" | 29,274 shapes, 28,921 paths, **142,029 mm** of centreline |
| Layer "gnd" | 12,826 shapes, 12,499 paths, **179,382 mm** of centreline |
| Board itself | 49.4 × 52.6 mm |
| Document extent | **161.8 × 189.3 mm** — the fab and drill-chart layers inflate it ~12× in area |

The scanline pitch is 22,860 DBU against a 25,400 DBU stroke width, i.e. abutting with 10% overlap.
That is a painted raster, stored as vector strokes.

### 1b. Frame cost, fitted to the board (the view the work is actually done at, all layers visible)

| | 1600×1000 | 3200×2000 (2× DPI, i.e. a Retina panel) |
|---|---|---|
| repaint | 288 ms | 441 ms |
| **pan** | **282 ms (3.5 FPS)** | **459 ms (2.2 FPS)** |
| one zoom step in (2×) | 135 ms | **812 ms (1.2 FPS)** |

**The 2× DPI column is the one that matches the report.** Device pixels are what the fill costs, and
at 2× the hairline-elision tier also disengages an octave earlier — which is why zooming *in* makes
it worse before culling eventually wins.

### 1c. Where the time goes (board fit, 1600×1000, each layer measured alone at the *same* viewport)

| Source | Cost above floor | Share of the 283 ms frame |
|---|---|---|
| "gnd" pour | ~118 ms | 42% |
| "bottom copper" pour | ~99 ms | 35% |
| Fixed per-frame floor (§1d) | 18.9 ms | 7% |
| Every other layer combined | ~25 ms | 9% |

**Cost tracks painted area, not shape count.** "gnd" costs *more* than "bottom copper" while having
57% fewer paths, because it has 26% more centreline length. Any reasoning about this board that
starts from shape count is wrong.

> A trap worth recording: the first pass at this table measured each layer at *its own* zoom-to-fit,
> which silently compared layers at different zooms. Fixing the viewport across the sweep is what made
> the numbers add up to the all-layers frame.

### 1d. The fixed floor — 19 ms and 4.3 MB per frame, with nothing drawn

With **every layer hidden**, zero shapes drawn and zero draw calls issued, a frame at board fit still
costs **19.1 ms and allocates 4,346 KB**. It is superlinear in candidate count:

| Candidates examined | Floor |
|---|---|
| 51,378 | 19.1 ms |
| 24,580 | 9.0 ms |
| 9,927 | 0.7 ms |

Timed directly, the two spatial queries a frame makes:

| | per call | entries returned | allocation |
|---|---|---|---|
| `QueryIntersecting(shapes, rect)` | 2.66 ms | 51,378 | — |
| `QueryIntersecting(shapes, instances, …)` | **12.42 ms** | 51,378 (all of them shapes) | **1,026 KB** |
| `.Count(e => e.Kind == Instance)` over it | 0.16 ms | → **0** | — |

**Board A has no instances.** 12.4 ms of every frame is a second full R-tree traversal, an unsized
`List` grown by doubling, and a 51,378-element `Sort` with a comparison delegate, whose entire product
is the number zero. That is **brief 1**.

### 1e. Zooming pays a second time

`widenDbu` (`LayoutRenderer.cs:1263`) is `ceil(strokePx / devicePxPerDbu)`, recomputed raw. It is the
cache key for `LayoutPathCache.GetOrBuildWidened`, so *any* change in zoom is a 100% miss.
`detailDbu` immediately beside it is deliberately bucketed to a power of two for exactly this reason.

A **0.1%** zoom change — well inside one frame of a trackpad pinch — rebuilds:

| Zoom | repaint | 0.1%-per-frame zoom | paths rebuilt |
|---|---|---|---|
| board fit | 288 ms | **336 ms** | **192,680** |
| 2× | 135 ms | 162 ms | 87,904 |
| 4× and beyond | — | no penalty | 0 (tier disengaged) |

That is **brief 2**.

### 1f. Is this board special? No — its *class* is

| Board | Shapes | File | pan @ board fit |
|---|---|---|---|
| **A** | 52,230 (raster fill) | 14.7 MB | **282 ms** |
| **B** | 33,283 (raster fill) | 8.5 MB | **109 ms** |
| **C** | 2,603 (dense polygons) | **27 MB** | **13.5 ms** |

Board C is nearly twice the file size of A and renders 21× faster. **Scanline count is the variable;
file size and vertex count are not.** C's dense polygons are exactly what `LayoutRenderDetail`'s
vertex-decimation tier was built for, and it handles them.

---

## 2. The four briefs, in the order they are worth doing

| | Brief | Buys | Risk | Touches |
|---|---|---|---|---|
| 1 | `brief-rasterfill-1-instance-query-on-flat-documents.md` | ~12 ms/frame on **every** large flat document | Low | `src/Render`, `src/Design` |
| 2 | `brief-rasterfill-2-widen-key-bucketing.md` | ~17–20% while zooming, wherever the hairline tier runs | Low | `src/Render` |
| 3 | `brief-rasterfill-3-coalesce-raster-fill-on-import.md` | The 77%, at the source — and helps EM, DRC and export too | Medium | `src/Design/Layout/Interchange` |
| 4 | `brief-rasterfill-4-tiled-raster-cache.md` | The 77%, at the renderer — for boards already imported | High | `src/Render` |

**1 and 2 are small, independent and safe**; do them first and re-measure, because they change the
baseline the other two are judged against.

**3 before 4.** Brief 3 deletes the work; brief 4 caches it. A pour that arrives as one polygon costs
nothing to draw and nothing to cache, and it is also the shape every *other* consumer wants —
`PlanarExtractor`, DRC and the exporters all currently see 41,420 strokes where the board has one
pour. Brief 4 is still worth doing (it is L2d, already carried as deliberately-deferred in the repo's
own notes, and it is the only thing that helps a board already in a workspace) but it is the larger
and riskier of the two, and brief 3 may make its threshold much rarer.

## 3. What this series does not claim

- **The harness rasterizes on CPU.** The application composites through Avalonia, which is GPU-backed
  on macOS via `UsePlatformDetect`. Absolute milliseconds may differ there. The attribution, the
  counter evidence and both defects are backend-independent, and the 2×-DPI measurement suggests the
  real figure is worse than the 1× column, not better.
- **Nothing here is a regression.** The L2b/L2c work is functioning as designed and measurably so; this
  is geometry that no tier in it was built to meet.
- **No fix is proposed for the document extent** being 12× the board area. It costs nothing directly
  (culling handles it) and it is what the source files say.

## 4. Reading order

`docs/sonnet-briefs/brief-L2c-lod-merge-and-caching.md` (the tiers being measured, and L2d's own
deferral) · `docs/sonnet-briefs/brief-L2b-spatial-index.md` (the index both queries hit) ·
`src/Render/Renderers/LayoutRenderer.cs` (`Draw`'s candidate walk, `DrawLayer`'s tier ladder) ·
`src/Render/Renderers/LayoutRenderDetail.cs` (`ToleranceDbu` — the bucketing precedent brief 2
follows) · `src/Render/Renderers/LayoutPathCache.cs` · `src/Design/Layout/LayoutSpatialIndex.cs`
(both `QueryIntersecting` overloads) · `src/Design/Layout/Interchange/GerberReader.Regions.cs`
(`:276`, "a stroke stays a `PathShape`").
