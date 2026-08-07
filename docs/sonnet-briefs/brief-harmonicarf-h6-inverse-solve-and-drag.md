# Brief — harmonicaRF H6: the drag gesture, the inverse solve, and reachability

**Read first:** `docs/design/harmonicarf.md` (especially **§4.5**, **§6.6**, **§6.8**, **§7.2**),
then `src/Harmonica/CLAUDE.md` and `src/Ui/CLAUDE.md`'s H4–H5 entries. H0–H3 built the headless
engine; H4–H5 built the document, the four panels, the solve pool and the frame scheduler. **Every
piece of machinery this phase needs to run a live drag already exists and is gated — and nothing
calls it, because there is no pointer gesture.** That is the first thing H6 fixes.

---

## 0. What already exists, and what genuinely does not

**Do not rebuild any of this. If something below seems missing, it is a lookup you have not found —
ask before writing a second one.**

| you need | it is here | notes |
|---|---|---|
| solve one frame at stated quality | `HarmonicaSolver.Solve(ctx, terms, markers, opts, grid, ct)` | takes a pooled grid and a token |
| run it off the UI thread, latest-wins | `SolvePool<T>` (`src/Harmonica/SolvePool.cs`) | `cores − 2` workers, each with its own ctx + grid |
| decide the frame's quality | `FrameScheduler` (`src/Harmonica/FrameScheduler.cs`) | fed a clock; `NextPlan(dragging)` / `RecordFrame(plan, timing)` |
| the document's own wiring of both | `HarmonicaViewModel.RequestScheduledFrame(bool dragging)`, `.RecordFrameCost(...)`, `.PublishFrame(...)` | the VIEW publishes, on the UI thread |
| Γ → canvas for a harmonicaRF Smith panel | `HarmonicaPanelRenderer.GammaToCanvas(gamma, size)` | **never** `PlotRenderer.BuildTransforms` — see §5 |
| where each panel landed last frame | `HarmonicaCanvas`'s per-panel rects (`CharmLayout` fractions × canvas size) | already recorded so pointer handling can resolve a hit |
| a marker's extrinsic Γ and its intrinsic Γ | `HarmonicaMarker.Gamma` / `.GammaIntrinsic` | mutable, and the SAME instance is on both Smith panels |
| the intrinsic value itself | the `Gamma_intr` cube in `HarmonicaDataSet.Build` | §4.5's definition lives there; H6 **reads** it, never re-derives it |
| set a marker from the UI | `HarmonicaViewModel.SetMarkerGamma` / `SetMarkerImpedance` | |
| the out-of-circle display scale | `IntrinsicGlyphScale` | R-h45-4; already gated by a pixel oracle |

**Genuinely does not exist yet, and is this phase's work:** any pointer interaction on a harmonicaRF
panel at all; the inverse solve; reachability shading; the hatched outline for an out-of-circle
extrinsic solution.

---

## 1. Scope

Three things, in this order. **M1 is independently useful and is a legitimate stopping point** — it
turns the whole H4–H5 scheduler/pool stack from "built and gated" into "visibly working", and if the
inverse solve proves harder than expected that is still a real deliverable.

1. **The drag gesture** (§6.8's own consumer). Dragging a marker on the EXTRINSIC Smith chart moves
   its termination live: pointer-down picks the marker, pointer-move calls
   `RequestScheduledFrame(dragging: true)`, pointer-up calls `RequestScheduledFrame(dragging: false)`.
   No inverse solve involved — the extrinsic Γ *is* the thing being set.
2. **The inverse solve** (§6.6). Dragging an INTRINSIC glyph. Full FD Jacobian on drag-start, rank-1
   Broyden per frame, automatic FD refresh when the residual stops decreasing.
3. **Reachability shading** (§6.6). The intrinsic map is not onto; the reachable region is shaded
   during an intrinsic drag.

---

## 2. M1 — the drag gesture

### R-h6-1 — one hit-test, resolved through `GammaToCanvas`

`HarmonicaPanelRenderer.AnnulusHeadroom` scales the whole Smith panel about its centre so the
compressed out-of-circle annulus fits. **A hit-test that inverts `PlotRenderer`'s own transform will
be off by that factor** — visibly, at the rim, which is exactly where markers sit. Add the inverse
(`CanvasToGamma`) next to `GammaToCanvas` in the same file, derived from the same `k`, and hit-test
through it. One transform pair, one place.

### R-h6-2 — the grab radius is in DEVICE PIXELS, computed per event

A marker's grab radius must be a constant number of screen pixels at every panel size, never a
constant in Γ. This repo has burned itself on a cached tolerance twice (`src/Ui/CLAUDE.md`'s L1c and
L1-fix entries); do not cache it and do not derive it from anything in Γ space.

### R-h6-3 — the drag never writes to the model mid-gesture beyond the marker itself

`HarmonicaMarker` is mutable and is the same instance on both charts (R-h45-3) — moving it *is* the
live preview. Do not clone the `TerminationSet` per pointer move into the document; `RequestFrame`
already snapshots what it needs so a worker never races the UI thread.

### R-h6-4 — the frame loop is the scheduler's, not the gesture's

The gesture calls `RequestScheduledFrame(dragging)` and **must not** pick rings, spokes or raster
itself. It also has to feed the loop: measure the frame and call `RecordFrameCost` with a real
`FrameTiming`, or the ladder can never degrade and D4's status message can never fire. **Time the
stages separately** (D6) — `HarmonicaSolver` already reports `LastSolveCount`; the raster/fit split
needs a stopwatch around the calls in `BuildSmith`.

### R-h6-5 — the status strip shows `FrameScheduler.StatusMessage`, always

D4's whole point is that a model which cannot hold the target is *told about*, never silently
stuttered at. The message exists and nothing displays it yet.

### Gate for M1

- A synthetic pointer sequence (down on a marker, 40 moves, up) moves the marker to the release point
  and leaves exactly one published frame at `FrameQuality.Full` — the snap.
- The same sequence with an over-budget `FrameTiming` walks the ladder down and still snaps on release.
- A pointer-down 200 px from any marker grabs nothing.
- The grab radius is the same number of pixels on a 300 px panel and a 900 px one.
- `StatusMessage` reaches the strip when tier A alone misses the target.

---

## 3. M2 — the inverse solve (§6.6)

### R-h6-6 — all marked harmonics solve SIMULTANEOUSLY

Owner-confirmed in §6.6, and it is the reason the phenomenon is worth a tool: the harmonics are
coupled. Unknowns are Re/Im of every *marked* extrinsic termination; equations are "every marked
band's intrinsic Γ equals its target", the dragged glyph supplying a new target and every other glyph
its present value. Square: 8 × 8 for four markers. **Do not solve one band at a time and iterate** —
that is a different (and wrong) problem.

### R-h6-7 — the residual differs by SIDE, and this is the trap

A load-side target is §4.5.1's ratio; a source-side target is §4.5.3's conversion-matrix **diagonal**
— and note that §4.5.3(a)'s closed form was corrected on 2026-08-06 (`Z_seen = (Zs + Z_Ls)/(1 +
gm·Z_Ls)`, sign fixed). Both are functions of the converged state, so the outer Newton is unchanged,
but open item 8 warns the source-side Jacobian is less well-conditioned. **Be prepared to give the
source side its own FD-refresh cadence and say so if you do.**

### R-h6-8 — FD on drag-start, Broyden per frame, FD refresh on stall

§6.6's own budget: 8 perturbation solves + residual ≈ 9 ms at start, then 1–2 solves ≈ 2 ms/frame.
Rebuilding the FD Jacobian every frame is ~30–40 ms and caps the drag at ~25 fps. **Measure both and
report them** — the numbers in the design note are estimates, and this is the phase that turns them
into measurements.

### R-h6-9 — a failed solve moves NOTHING

If the solve does not converge inside its iteration budget, the glyph does not move and the previous
extrinsic set is retained. **No partial application.** A glyph that lands somewhere the solver did
not actually reach is worse than one that sticks.

### R-h6-10 — an out-of-circle extrinsic solution is ALLOWED and FLAGGED

Drawn with a hatched outline, never clamped. An active source termination is a legitimate thing to
discover and hiding it would mislead. Note this is the *extrinsic* marker; `IntrinsicGlyphScale`
already handles the *intrinsic* out-of-circle case.

### R-h6-11 — the operating point is the power-sweep cursor's Pin

Intrinsic impedance is drive-dependent, so the equation is only well-posed at a stated drive. The
cursor is user-placeable and needs a **snap-to-compression** mode so "set the load at compression" is
expressible. **Re-converge at compression is DEFAULT OFF** — ~10× the cost and ill-conditioned where
the gain curve is flat.

### Gate for M2

- **The oracle is a round trip through the FORWARD path, not another inverse-solve run:** drag an
  intrinsic glyph to a target, take the extrinsic set the solve produced, run it forward through
  `HarmonicaSolver` and read `Gamma_intr` — it must land on the target within a stated tolerance.
  A solver agreeing with itself proves nothing.
- Four marked harmonics produce an 8 × 8 system and all four glyphs land on their targets.
- A deliberately unreachable target leaves the glyph and the extrinsic set **exactly** where they were.
- Broyden and full-FD-every-frame reach the same answer; report the cost of each.
- Cost measured and reported: FD-at-start, per-frame Broyden, and the FD-refresh rate on a real drag.

---

## 4. M3 — reachability shading

### R-h6-12 — shade the reachable region, do not let the glyph stick silently

Sampled coarsely, cached, refreshed on structural change. **Open item 4 says: if it proves expensive
it becomes opt-in rather than automatic.** Measure first, then decide, and record which you chose and
why.

### Gate for M3

- A model with a lossy embedding shows a genuinely smaller reachable region than one without —
  compared against the forward path, not asserted.
- The shading is cached: N frames of a drag compute it once.
- Its cost is reported at the shipping sampling density.

---

## 5. Standing constraints (violating any of these is a bug, not a style choice)

- **Never `PlotRenderer.BuildTransforms` on a harmonicaRF Smith panel.** `GammaToCanvas` /
  `CanvasToGamma` only — the annulus headroom is not in the raw transform.
- **The UI thread never solves.** Everything goes through `SolvePool`; the view publishes.
- **Tier A never degrades.** `FramePlan.IncludesTierA` is true on every rung and must stay that way.
- **harmonicaRF never fills contours** — owner ruling, not a default. Do not add a fill path, a
  setting, or a benchmark for one.
- **`src/Harmonica` references no Avalonia.** `tests/Firewall.Tests` enforces it.
- **No new physics.** §4.5's definitions, `Gamma_intr`, `PinSearch` and `ContourGrid` all exist. H6
  adds an *inverse* over the existing forward path.
- **Do not touch `src/Engine`, `src/Core` or `src/RfCore`.** If you think you need to, stop and report.

---

## 6. Cost discipline

Any test at or above ~5 s carries `[Trait("Category","Benchmark")]`, lives in a non-parallel
collection, takes a **best-of-N minimum** (not a mean or a median — this repo has been bitten three
times), and every reported number is measured **alone**. The inverse-solve cost tests will land there;
the correctness tests must not.

---

## 7. Gate command

```
dotnet test tests/Ui.Tests
dotnet test tests/Harmonica.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

Baseline going in: Ui 5,165 · Harmonica 78 · Firewall 5.

---

## 8. Report back

1. **The M1 gesture numbers** — measured frame time during a real drag, and which ladder rung it
   settled on. Tier 9 says only freeze-and-snap holds 30 fps on the shipping model; confirm or
   contradict that from an actual gesture.
2. **The inverse-solve costs** — FD-at-start, per-frame Broyden, FD-refresh rate — against §6.6's own
   estimates of 9 ms / 2 ms.
3. **Whether the source-side Jacobian genuinely needed its own cadence** (open item 8).
4. **Reachability shading's cost**, and whether you made it automatic or opt-in (open item 4).
5. **Anything the design note got wrong.** H0–H3 found a sign error in §4.5.3(a); H4–H5 found the
   viewport-margin defect and that a colour probe cannot separate an iso-line from chart chrome. Say
   so plainly rather than working around it quietly.
