# Brief — RF4 (= L2d): the raster tier exists, and a flat board cannot reach it

**Series:** `brief-rasterfill-0-overview.md` §1c and §2. **This is L2d**, the tiled raster cache that
`docs/design/layout-view.md` §5.3 deferred and that `brief-L2c-lod-merge-and-caching.md` gate 10 said
to recommend only with a measured shortfall as evidence.

**Read brief 3 first and decide between them.** Brief 3 deletes this work at the source for imported
raster fill; this brief caches it. They are not alternatives for every case — this one is the only
thing that helps a board already sitting in a workspace, and the only thing that helps geometry that
is genuinely complex rather than merely painted — but if brief 3 lands first, re-measure before
building this. **§5.3 says "do not build these speculatively", and this brief opens by discharging
that condition, not by ignoring it.**

---

## 0. The evidence the deferral was waiting for

Board A (overview §1a), fitted to the board, all layers visible:

| | 1600×1000 | 3200×2000 (2× DPI) |
|---|---|---|
| pan | **282 ms (3.5 FPS)** | **459 ms (2.2 FPS)** |
| one zoom step in | 135 ms | **812 ms (1.2 FPS)** |

Against §5.1's targets this is a ~50k-shape document that should pan at 60 fps and does not, by
17–27×. And the frame is not merely slow, it is *structurally* slow — every tier that could help is
already engaged:

- `PathsConstructed` is **0** on every repaint and pan frame, at every zoom. There is no path building
  left to remove.
- A 29,274-shape layer already issues **4 draw calls**.
- Outlines are already off; forcing them off changes nothing.
- 77% of the frame is Skia rasterizing painted area (overview §1c), which is invariant to all of the
  above.

**And a fourth tier for exactly this already exists — this document cannot reach it.** The instance
raster tier (`DefaultInstanceRasterMaxDevicePixels`, counted as `InstanceRastersBuilt`) rasterizes a
cell once at device scale and blits it per placement, and it was built because the other three tiers
key on a chunk's largest primitive being a few device pixels and so structurally cannot fit PCB
content. It is keyed on **placements**. Board A has **zero instances**, so `InstanceRastersBuilt` is 0
on every frame it will ever draw.

That is the gap this brief closes: **the same idea, applied to top-level geometry.** Not a fourth tier
— the fourth tier, given the input it was always the right answer for.

## 1. Requirements

**R-rf4-1. Committed layer geometry rasterizes into viewport-aligned tiles; a pan blits them.** The
tile grid is anchored in *document* space and quantized like `ComputeOrigin` already quantizes the
path-space anchor, so a pan moves across tile boundaries without re-rasterizing anything already held.
A pan at fixed zoom must build **zero** tiles in steady state — that is the whole feature, and gate 1.

**R-rf4-2. Tiles hold committed layer geometry and nothing else.** Not the grid, not the rulers, not
the selection, not handles, not the marquee, not the drag ghost, not port glyphs. Everything that is
per-frame chrome keeps being drawn per frame over the blit. The existing separation makes this
tractable: `DrawLayer` already batches committed geometry and `DrawPortGlyphs` already runs as a
top pass.

**R-rf4-3. A drag bypasses the cache, exactly as every other cache here does.** `LayoutPathCache`
bypasses on `dragOverrides`, the L2b index is not churned by drags, and a drag selection is always
small. A tile containing a dragged shape is drawn live for the duration of the gesture and re-rastered
on commit. Getting this wrong reproduces the ghost-left-behind defect `LayoutPathCache`'s own comment
documents, one level up and harder to see.

**R-rf4-4. Zoom invalidates, and the gesture must not therefore stutter.** Tiles are rasterized at a
device scale; a zoom changes it. Two acceptable answers, and the brief does not pick for you: rasterize
per zoom *octave* and scale the blit within it (cheap, slightly soft mid-octave — and note that brief 2
establishes exactly this octave-bucketing vocabulary), or draw live during the gesture and re-tile on
settle. **Measure both.** What is not acceptable is re-rasterizing every tile every frame of a pinch,
which is worse than no cache at all.

**R-rf4-5. Bounded memory, disposed explicitly.** A tile is a native Skia surface. Cap total tile
memory, evict LRU, and dispose on eviction rather than relying on a finalizer — the rule
`LayoutPathCache` already states in its R-L2c-4 note. **Report the cap and the steady-state figure**;
the owner asked directly during the instance-raster work whether that problem was memory, and the
answer there was no. Do not let this one become the change that makes it yes.

**R-rf4-6. Invalidation rides existing notifications, not a new path.** `LayoutChangeInfo` already
drives `LayoutPathCache.Apply` and the spatial index; layer visibility, technology and theme changes
already invalidate elsewhere. A second notification path is a second thing to get out of step. A tile
is invalidated by document extent: which tiles a changed shape touches is a spatial-index query, which
is already there.

**R-rf4-7. Correctness is pixel-identity, not similarity.** With the tier disabled the output must be
bit-identical to today's. With it enabled the composed frame must match the un-tiled frame — including
across tile seams, which is where antialiasing at a tile edge will differ if tiles are rasterized with
a clip instead of with the neighbouring geometry drawn and cropped. **Seam correctness is the hard part
of this brief**; budget for it accordingly.

**R-rf4-8. A negative threshold disables the tier outright.** Every other tier in `LayoutRenderOptions`
carries this contract, and it is how the export path pins exact vector geometry and how gate 7 gets its
reference. `render --detail full` must continue to produce exactly what it produces now — a tiled
raster has no place in an SVG or PDF.

## 2. What not to do

- **Do not tile the overlays.** R-rf4-2. A stale selection rectangle inside a cached tile is the kind
  of defect that reads as the editor malfunctioning.
- **Do not clip-and-rasterize per tile without accounting for the seam.** R-rf4-7.
- **Do not build this before re-measuring after briefs 1, 2 and 3.** Brief 3 may move board A out of
  the range where this pays for itself, and the threshold at which the tier should engage has to be set
  from the post-brief-3 numbers, not these.
- **Do not extend the instance raster tier by generalising its key.** It is keyed on a placement's
  resolved cell and its cache invalidates for free because a changed file yields a new `LayoutView`.
  Top-level geometry has no such key and needs viewport tiles with explicit invalidation. Sharing the
  blit machinery is fine; sharing the cache is not.
- **Do not use this to avoid brief 3.** Caching 41,420 strokes still leaves EM, DRC and export looking
  at 41,420 strokes.

## 3. Gates

**There is a standing conflict here that needs the owner's call before the gates are written.**
`brief-L2c-lod-merge-and-caching.md`'s completion note says re-enabling routine 500k **timing**
coverage is part of L2d's own gate. The standing instruction since is that no *new* timing tests are
added, because they measure the machine and flake, and that structural properties are asserted with
counters instead. **The recommendation in this brief is: satisfy L2c's intent with counters routinely,
and leave timing in the existing `Category=Benchmark` tier** — the 500k timing sweep already lives
there and needs no new test to be run. Confirm before proceeding.

1. **A pan builds no tiles.** Steady-state pan at fixed zoom: `TilesBuilt == 0`, `TilesBlitted ==` the
   number on screen. One counter assertion, and it is the feature.
2. **A zoom builds a bounded number.** Per R-rf4-4's chosen answer — per octave, or once on settle.
   Assert the count, not the clock.
3. **Pixel identity, tier disabled.** Bit-identical to today's renderer.
4. **Pixel identity, tier enabled.** The composed frame matches the un-tiled frame, at several pan
   offsets chosen so that geometry lands *across* seams. R-rf4-7.
5. **Overlays are never cached.** Move a selection with the document unchanged: tiles are reused
   (`TilesBuilt == 0`) and the selection still tracks.
6. **A drag is live.** A shape being dragged renders at its dragged position every frame. R-rf4-3.
7. **Export is untouched.** `render --detail full` output is byte-identical, which R-rf4-8 guarantees
   structurally. The existing `RenderCliVerbTests` byte-for-byte comparison already covers this.
8. **Memory stays under the cap** and is reported. R-rf4-5.
9. **Invalidation is exact.** Editing one shape re-rasters only the tiles it touches — a counter, and
   the assertion that catches an over-broad invalidation quietly turning the cache off.

## 4. Measuring it

A Release scratch harness, as in the overview §1 — not a new benchmark test. Report tile counts
alongside frame time, and report steady-state tile memory next to the cap.

## 5. On completion

1. Record in **`src/Render/RESOLVED.md`**: the before/after against overview §1b, the R-rf4-4 zoom
   decision **and the measurement that chose it**, the seam approach from R-rf4-7, and the tile memory
   cap with its steady-state figure.
2. State plainly whether §5.1's targets are now met, per scenario, including any still unmet and what
   it would take — the same honesty `brief-L2c-lod-merge-and-caching.md` gate 10 asked for.
3. Note that `docs/design/layout-view.md` §5.3's "deferred until measured" is now discharged, and what
   measured it.
4. **Do not write any of this to a `CLAUDE.md`.**
