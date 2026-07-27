# Sonnet Brief — Phase L2a: the performance harness and baseline

**Design:** `docs/design/layout-view.md` §5.1 (the budget), §5.4 R12 (the harness), §2.3 R8b (the merge tier
whose threshold must come from data), §5.3 (the four things that matter). **Consumes** all of Phase L1.

**L2 is three briefs.** This is **L2a: measurement only — no optimization whatsoever.** Then **L2b** (R-tree
spatial index, viewport culling, accelerated hit-test and marquee preview) and **L2c** (per-shape path
caching, LOD, the R8b merge tier with its threshold set from L2a's data).

## Why the harness comes first

1. §5.4 R12 requires it in CI regardless, so it is not extra work — only reordered work.
2. **The R8b merge threshold is specified as a number derived from measurement**, not a guess (§2.3, §5.1).
   L2c cannot be done correctly without L2a's output.
3. This project has now had three defects caused by an assumption about scale being wrong — the default
   viewport zoom, the sub-pixel grid pitch, and the 5 µm label height. Optimizing before measuring is how a
   fourth one gets built in. **Measure first, and be prepared for the bottleneck to be somewhere other than
   where the design doc predicts.**

The deliverable is a baseline: real numbers, recorded, that L2b and L2c are then judged against.

---

## 1. Synthetic layout generator

Test-only code (`tests/Ui.Tests/...`), never shipped.

```csharp
public static LayoutView Generate(int shapeCount, int layerCount, int seed, GeneratorProfile profile);
```

**R-L2a-1. Deterministic** — same seed, byte-identical layout. Every measurement must be reproducible, and a
flaky generator makes a flaky gate.

**R-L2a-2. Clustered, not uniformly random.** This is the methodology point that decides whether the numbers
mean anything. A uniform random scatter is the *worst* case for a spatial index and is nothing like a real
layout, which is dense regions separated by empty space. Generate a mix: a handful of dense clusters, some
mid-density areas, and large empty stretches. Otherwise L2b's R-tree will be measured against a distribution
it will never see and its benefit will be understated.

**Profiles**, because different content stresses different code paths:
- `Manhattan` — rects and orthogonal polygons only. The PCB-ish baseline.
- `CurveHeavy` — circles, rounded rects, arc-bearing curves and paths. Adaptive curve tessellation is a
  per-frame cost the Manhattan profile never pays, and L1a's R12 already calls for this variant.
- `Mixed` — realistic blend, including labels, a polygon-with-holes population, and a few bitmaps.

Sizes: **1k / 50k / 500k** shapes across **200 layers**, matching §5.1.

## 2. Instrumentation — count work, don't just time it

`LayoutRenderer.Draw` already returns a `LayoutRenderResult`. **Extend it with counters** rather than adding
conditional compilation or a side channel:

- shapes examined, shapes drawn (i.e. survived culling)
- `SKPath` objects constructed this frame
- draw calls issued
- layers visited
- bytes allocated during the frame

**R-L2a-3. Counters are the CI gate; wall-clock is the local diagnostic.** Counters are deterministic and
machine-independent; frame times on a shared CI runner are neither. A CI assertion like *"at 500k shapes
zoomed into 1% of the extent, shapes **drawn** is O(visible) not O(total)"* is precise, stable, and actually
catches the regression that matters. An assertion of *"< 16.6 ms"* will flap on a loaded runner and get
disabled within a month, which is worse than no gate at all.

Wall-clock still gets measured and **reported**, but gated loosely (say 3× headroom) so it catches
catastrophes without flapping.

## 3. What to measure

Frame-time and counters for each combination of {1k, 50k, 500k} × {Manhattan, CurveHeavy, Mixed} ×
{darkening path, merged path}:

- **Pan** — a fixed sweep across the design at a fixed zoom.
- **Zoom** — a fixed zoom sweep from full-extent to deep-in, at a fixed centre.
- **Full-extent static** — the pathological "everything visible" frame.

Plus the interactive costs that are not frame time but are felt as lag:

- **Hit-test** — `LayoutHitTest.HitStack` at a point, dense and sparse regions.
- **Marquee preview** — L1i's `ComputeMarqueeSelection`, which currently scans every shape on **every pointer
  move**. At 500k this is likely to be among the worst offenders and it will not show up in a frame-time
  measurement at all.
- **Load** — `LayoutPersistence.LoadFromFile` parse time and `.clay` file size at each shape count, which
  also finally puts a number on §4's deferred gzip question.

Measure **both** the per-shape-fill (darkening) and merged rendering paths at every size — §5.1 says so, and
the R8b threshold is exactly the crossover between them.

## 4. Methodology

**R-L2a-4. Warm up, then report median and p95 — never the mean.** JIT and first-frame path construction
make the first iterations meaningless, and a single GC pause turns a mean into noise that reads like signal.
Discard warm-up iterations explicitly.

**R-L2a-5. Headless, off-screen, no Avalonia window** — render into an `SKSurface` exactly as L1a's pixel
tests do. That is what makes this CI-able at all.

**R-L2a-6. No new dependencies.** Plain xUnit plus `Stopwatch` for the gate. If a richer local profiling
harness is wanted later, propose it separately — the root `CLAUDE.md` says ask before adding packages, and a
benchmarking framework is not needed to answer the questions in §3.

**Allocation per frame** deserves its own assertion: the steady-state pan loop should approach zero
allocation. Per-frame garbage is the usual cause of "smooth except for a periodic hitch", and it is invisible
to a median frame time.

## 5. Report the baseline

**R-L2a-7. The completion note contains the actual numbers**, as a table, for every combination in §3. This
is the phase's real deliverable — L2b and L2c are judged against it, and without it they have nothing to
prove.

Also record explicitly:
- **Where the time actually goes** at 500k. The design doc predicts path construction and per-shape draw
  calls; if profiling says otherwise — parse time, flattening, allocation, the marquee scan — **say so
  plainly**, because that changes what L2b and L2c should do.
- **The R8b crossover**: the shape count at which the merged path becomes faster than per-shape fills. That
  number is L2c's input.
- Whether §5.1's targets are already met at 1k and 50k. They may well be, in which case L2b and L2c can be
  scoped to the 500k case rather than optimizing something that is already fast.

## 6. Scope guardrails

- **No optimization.** No R-tree, no caching, no LOD, no merge tier, no culling changes, no allocation
  removal. If a fix looks irresistible, write it down in the completion note for L2b/L2c instead.
- The only production-code change permitted is **adding counters to `LayoutRenderResult`** and whatever
  minimal plumbing populates them. That must not itself cost measurable time — increment plain fields, no
  logging, no string formatting, no dictionaries.
- No changes to `LayoutRenderer`'s drawing behaviour, `LayoutHitTest`, `LayoutFlattener`, or any view model.
- Don't touch `src/Core`, `src/Engine`, `RfCore`, the schematic or the symbol editor.

## 7. Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. **Determinism (R-L2a-1)** — the same seed produces a byte-identical `LayoutView` across runs and across
   a serialize/reload cycle.
3. **Distribution (R-L2a-2)** — the generator's output is measurably clustered: assert that a viewport
   covering 1% of the extent contains substantially more or fewer than 1% of the shapes depending on where
   it is placed. A uniform generator fails this.
4. **All three profiles generate and render** without exception at all three sizes, including the
   polygon-with-holes and bitmap populations in `Mixed`.
5. **Counters are correct** — on a hand-built 10-shape layout with 3 shapes off-screen, `shapes examined`
   and `shapes drawn` are exactly as expected, and `draw calls` matches the documented per-layer accounting.
6. **Counters cost nothing** — frame time with counters populated is within noise of a build with them
   stubbed out.
7. **Harness runs in CI** within a sensible time budget. If 500k is too slow for every CI run, gate 1k and
   50k per-commit and mark 500k for a nightly or on-demand run — but **still record it in the baseline**.
8. **The baseline table exists** and is committed (R-L2a-7).
9. **Loose wall-clock gates only** (R-L2a-3) — any absolute timing assertion carries at least 3× headroom
   and is documented as a catastrophe detector, not a target.

## 8. On completion

1. Add a "Phase L2a — COMPLETE" entry at the top of `src/Ui/CLAUDE.md` containing **the baseline table
   itself**, plus: where the time actually goes at 500k, the measured R8b crossover point, whether §5.1's
   1k and 50k targets are already met, the marquee-preview and hit-test numbers, and `.clay` parse time and
   file size at each scale.
2. List, without fixing them, every optimization opportunity the measurements revealed — that list is the
   input to L2b and L2c, and it should be ordered by measured cost rather than by the design doc's
   predictions.
3. Report back before L2b (R-tree, culling, accelerated hit-test and marquee) is briefed.
