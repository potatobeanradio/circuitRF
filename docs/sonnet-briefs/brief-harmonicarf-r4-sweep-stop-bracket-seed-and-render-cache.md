# Brief — harmonicaRF Round 4: the sweep's missing stop, the bracket's wrong crossing, the seed policy, and the render cache

**Read first:** `src/Harmonica/RESOLVED.md` **§3 and §4** and `src/Ui/RESOLVED.md` **§1.4** — this brief
is the direct follow-on to both and does not restate their findings. Then these files in full:
`src/Harmonica/PinSearch.cs`, `src/Harmonica/CircuitModel.cs` (`HarmonicaSettings` only),
`src/Harmonica/ContourGrid.cs`, `src/Ui/Harmonica/HarmonicaPanelRenderer.cs`,
`src/Ui/Harmonica/HarmonicaSolver.cs`, and `HarmonicaViewModel.RequestFrame` / `OptionsFor` /
`SetMarkerVswr` / the L1-marker drag path (`src/Ui/Harmonica/HarmonicaViewModel.cs`). For §2 only:
`src/Engine/Loadpull/LoadpullEngine.cs`.

**The shipped default document is the fixture for every measurement in this brief**, exactly as R3B
used it. Report your own measured figures against it in the completion note. Note that
`HarmonicaSettings.PinMaxDbm` now defaults to **50** (R-h9r2-18 raised it from 30), which matters
directly to §1 and §5 — R3B's own "46-solve sweep" figure was taken at a **34 dBm** ceiling and is not
comparable to what the shipped default does today.

**Order matters and is not negotiable: §1 first, then §5.** §1 removes roughly the top twenty rungs of
every tier-A sweep, and those rungs are the deepest in compression and therefore the hardest to
converge. Any drag-frame measurement taken before §1 lands is measuring work that should not exist,
and §5's conclusions drawn from it would be wrong.

---

## 0. Scope, decided by the owner

**In scope:**

- §1 — the power sweep must stop at the compression target. **New; the owner found it during R3's
  review.**
- §2 — a real nonlinear DC seed for the general `LoadpullEngine`. **This item only.**
- §3 — the `PinSearch.Run` bracket defect that RESOLVED.md §4 found and flagged for follow-up.
- §4 — the cached render backdrop, **harmonicaRF only**.
- §5 — the drag-size FPS asymmetry, and the seed policy behind it.

**Explicitly out of scope, decided:**

- **The rest of §3's seed work is NOT adopted into the general engines.** Only fix #1 (the real DC
  seed) crosses over. Fix #2 (nearest-in-Pin neighbour seeding) is a no-op there — `LoadpullEngine`
  walks a uniform ladder and never jumps. Fix #4 (separate stop reasons) is already present as
  `stopReason` / the `StopCode` cube. Fix #3 (retry from DC) is deliberately declined; see §7.
- **No parallelisation of `LoadpullEngine` or `LoadpullPursuitEngine`.** Do not add it, do not
  refactor toward it, do not make `PursuitContext` cloneable "for later".
- **No DataDisplay changes.** The backdrop cache is harmonicaRF's alone this pass. Do not touch
  `PlotRenderer` or `AxesRenderer` — the standing rule holds without exception here.

---

## 1. The explicit power sweep does not stop at the compression target

> **owner:** *"When I set the Pin driveup stop level to 50 dBm, the power sweep actually GOES to
> Pin=50 dBm and the DUT is compressed by ~20 dB. All power sweeps must stop once the
> compression level is higher than the P-xdB setting. How did this get missed?"*

### 1.1 — It was not missed. It was decided, and the decision is being reversed here

Root-caused by reading; do not re-derive it. `PinSearch.Sweep`'s ladder loop carries this comment
verbatim:

```
// Every point is solved regardless of where compression crosses — R-h9r2-19 forbids stopping
// early, unlike Run()'s own bracket-and-secant. The FIRST crossing is still recorded AS the
// ladder is walked (R-h9r2-17a's own "first one"), from the incremental gMax above.
```

So the behaviour is R-h9r2-19's explicit rule, not an oversight — and RESOLVED.md §3 names that same
rule as "the guardrail" that R3B was forbidden to touch. What made it visible now is
**R-h9r2-18's default move from 30 to 50 dBm**, which the owner asked for. At a 30 dBm ceiling the
overdrive past P3dB was a few dB and looked unremarkable; at 50 dBm it is ~20 dB and is obviously
wrong. Two individually-reasonable decisions composed into a bad one.

**`PinMaxDbm` is one number serving two roles** (R-h9r2-18, deliberate): the explicit sweep's `Stop`
*and* `PinSearch.Run`'s hard bracket ceiling. That pairing is not changed by this brief and its
rationale still holds — the panel must not sweep further than the grid searches.

**The other two drive-ups already stop, which is the inconsistency to close:**

| drive-up | behaviour at the compression target |
|---|---|
| `LoadpullEngine.RunOneTermination` | stops: `if (compression >= p.Compression + 0.1) break;` |
| `PinSearch.Run` | stops: the secant lands on the target |
| `PinSearch.Sweep` | **runs to `Stop` regardless** |

`Sweep` is the outlier, and the owner's expectation matches the established convention in the other
two.

### 1.2 — What to change, and the one number the owner must pick

Add an **overdrive margin** to `HarmonicaSettings` — call it `SweepOverdriveDb` — and stop the tier-A
ladder at the first rung whose compression reaches `CompressionDb + SweepOverdriveDb`.

**Do not hardcode the margin at zero.** The owner's instruction is that the sweep must stop once
compression exceeds the P-xdB setting, and a margin of 0 satisfies that literally. But there is a real
cost to 0 that the owner should decide against knowingly: **PAE typically peaks a few dB past P3dB**,
and a sweep truncated exactly at the target throws away the saturation region a PA designer usually
wants to see on that panel. Implement the setting, default it to **the owner's stated choice**, and
say in the completion note what the panel looks like at 0, 2, and 3 dB so the choice is made against
pictures rather than in the abstract. Until the owner rules, default to **0** — his instruction as
written.

**Rules the change must respect:**

- **A sweep that never compresses still runs to `Stop`.** The `cross < 0` path is correct today and
  must stay: if the device does not reach the target anywhere in the range, the user has to see the
  whole range to know that. Only a sweep that *does* cross stops early.
- **The crossing bracket is unaffected.** The early stop happens strictly after the crossing pair has
  been recorded, so `SweepCompression`'s interpolation and `ExactCompressionSolve`'s extra solve both
  behave exactly as they do now. Prove this with a test, don't assume it.
- **The panel's x-axis must not breathe during a drag.** With an early stop the last solved Pin moves
  as the termination moves, so an auto-fitted x-axis will jitter every frame — which will look worse
  than the problem being fixed. Keep the axis fixed to `[PinStartDbm, PinMaxDbm]` and let the curve
  end short of the right edge.
- **`MaxSweepPoints` validation still applies to the full requested range**, not to the truncated one.
  A user asking for a range that would exceed 1001 points must still be refused by name before any
  solving starts.

### 1.3 — R-h9r2-19 is being revised, so say so where it lives

R-h9r2-19's "never stop early" rule is superseded for the compression case only. Update its statement
wherever it is recorded (`src/Harmonica/CLAUDE.md` and the design doc), including the *reason* — the
rule was right when `PinMaxDbm` was 30 and became wrong when it moved to 50. A future reader who finds
only the new rule will otherwise re-derive the old one.

### 1.4 — Gate

`PinSearch.Sweep` on the shipped default, `PinMaxDbm = 50`, `CompressionDb = 3`,
`SweepOverdriveDb = 0`: the last solved rung's compression is the first to reach ≥ 3 dB, the solve
count falls from its current value to that rung's index, and `SweepCompression` reports the same
Pin/Pout/Gain/DE/PAE it reports today to within 1e-9. **Report the before and after solve counts** —
that delta is the input to §5.

---

## 2. A real nonlinear DC seed for the general loadpull engine

Scope: **fix #1 of RESOLVED.md §3, and nothing else.**

### 2.1 — Establish whether there is anything to fix, before fixing it

`LoadpullEngine.Run`'s first grid point calls `RunOneTermination` with `warmStart = null`, which
reaches `HbEngine.RunSinglePoint(ctx.HbParams, null, ctx.SolveSettings)`. **Read what `HbEngine` does
with a null seed and report it in the completion note before changing anything.** Three outcomes, three
different answers:

1. It already computes a real nonlinear DC operating point → **nothing to do; say so and stop.**
2. It uses a device-absent linear DC point (the defect §3 named in `SeedFromDc`) → replace it with the
   real one, as §3 did for `HarmonicaContext`.
3. It seeds zero and relies on `DriveStepping`/`DcBiasStepping` continuation to recover → measure
   whether the continuation actually triggers on the hero fixtures. If it never fires, the cold start
   is already cheap and the change buys nothing; if it fires, the DC seed should remove it.

### 2.2 — If it lands

Mirror §3's shape rather than inventing a new one: a real nonlinear DC solve against the loadpull
netlist, **cached keyed to (structure, bias)**, invalidated when bias changes, gated by a counter so
the compute count stays visible, and **falling back to the existing behaviour when the DC solve fails
even under continuation** — never worse than what it replaces.

### 2.3 — Golden risk, named up front

This changes the seed for the first grid point of every loadpull run, and seeds propagate through
`FindNearestSeed` to every point after it. **Hero 3 / 3B goldens may move.** If they do, the movement
must be explained — magnitude, which quantity, whether the new answer is closer to the ladder's own
ground truth — and the owner must approve before anything is re-baselined. A silently re-baselined
golden is a failed brief.

---

## 3. `PinSearch.Run`'s bracket can converge to the wrong compression crossing

RESOLVED.md §4 found this, reproduced it against the untouched original bracket code, and flagged it
for a follow-up. This is that follow-up. **Reproduce §4's measurement first: `Run()` reports 28.4 dBm
where `Sweep()` reports 27.2 dBm at the same termination.** If that no longer reproduces, stop and say
so — everything below assumes it does.

### 3.1 — The definitional question is already settled; do not reopen it

It is tempting to treat "which crossing is the right one" as an open question, because on a device with
gain expansion the compression curve can cross the target more than once. **It is not open.**
`PinSearch.Sweep`'s own `CompressionAt` comment already fixes the rule, and fixes it with a measurement
behind it:

> gMax is the RUNNING maximum … updated as the ladder is walked, in ORDER, never recomputed globally
> after the fact. This is what "first crossing, lowest Pin" (R-h9r2-17a) actually means … measured
> directly: doing it the other way moved this fixture's own compression point from single digits of
> dBm to 27 dBm.

So the answer is: **first crossing, measured against a running gMax taken in ascending Pin order.**
`Run` already uses the same running-max shape. **`Run`'s defect is purely one of sampling** — the
doubling stride (3, 6, 12, 24 dB…) can step clean over the first crossing and never sample it, so the
rule is right and the evidence fed to it is incomplete. Fixing the sampling is the whole job.

### 3.2 — The trap that will bite a naive fix

The obvious repair is: once the bracket finds a rung with `c >= target`, probe back into
`(pinLo, pinHi)` to check for an earlier crossing. **Done naively this is wrong**, because `gMax` is a
running maximum updated in *probe* order, and by the time you probe the interior you have already
probed `pinHi` and folded its gain into `gMax`. The interior points would then be measured against a
peak from further up the ladder — precisely the mistake Sweep's comment says moved the answer from
single digits to 27 dBm.

**So make the crossing determination a pure function of the samples, not of probe order.** Keep every
probe's `(PavlDbm, GainDb)`. To decide where the first crossing is, sort the samples by Pin, run the
ascending running-max over *that* order, and take the first bracketing pair. This is exactly Sweep's
rule applied to whatever samples happen to exist, which is also what makes `Run` and `Sweep` provably
consistent rather than accidentally so. Refine — bisect the pre-crossing interval — until the first
crossing is located to within the existing `CompressionToleranceDb`, then hand that interval to the
secant.

### 3.3 — The cost budget is the constraint, and it is tight

The doubling stride exists for one reason: R-hrf-7's measured **~4.6 solves/point**, against the
ladder's ~30. That is what makes a 61-point grid affordable. The fix must not surrender it.

- **Bound the refinement.** Only refine when the sample history actually shows a reason to — a
  non-monotone gain sequence, or a bracket interval wider than some threshold. A monotone,
  narrow bracket needs no extra probe at all.
- **Cap the extra probes** at a small fixed number and count them (a counter on `PinSearchResult`,
  the way `Retries` is counted — visible, never silent).
- **Report the new solves/point** against 4.6 in the completion note. If it exceeds ~6, stop and bring
  the number to the owner rather than shipping it.

Do not redesign the stride itself. Capping the maximum stride is an acceptable *additional* lever if
measurement supports it, but the back-check above is the targeted fix and should be tried alone first.

### 3.4 — Gate

On the shipped default at the termination §4 identified: `Run()` and `Sweep()` agree on the compression
Pin to within `CompressionToleranceDb`. The 61-point grid's worst-case converged-point deviation
against the serial ladder drops from **2.69 pts PAE** to within the same tolerance as the other
metrics. Solves/point reported. `ContourGrid`'s hole set does not grow.

---

## 4. Cache the render backdrop — harmonicaRF only

> **owner:** *"I propose we speed this up by caching a backdrop (without the marker glyphs), so that
> when user drags marker, only the marker has to redrawn."*

### 4.1 — Why this is worth more than RESOLVED.md §1.4's own follow-up note estimated

§1.4 considered caching and declined it, on the grounds that the frozen contours are only **1.0 ms @1x
/ 1.4 ms @2x** of the render. That was pricing contour caching. **This is not that.** During an
L1-marker drag the static set on a Smith panel is: the chrome (circles, grid lines, title band), the
37 grid-point dots, *and* the 30 frozen polylines. What moves is the marker glyph. So the cacheable
share of SmithPower's **7.01 ms @2x** and SmithEfficiency's **6.76 ms @2x** is most of each, not 1.4 ms
across both.

### 4.2 — Use a rasterised surface, not `SKPicture`, and §1.4 already tells you why

§1.4's per-panel table shows the two Smith panels scaling **~2.9× from 1x to 2x** while the loadline
scales ~1.3× and the power sweep ~1.7×. Draw-call count is scale-invariant, so a ~2.9× scaling with
~4× the pixels says the cost is **rasterisation-bound, not draw-call-bound.**

That rules out `SKPictureRecorder`: replaying a picture re-executes the same draw commands and saves
the geometry/layout work, not the antialiased path rasterisation — which is the part that is actually
expensive. **Render the static layer once into an offscreen `SKSurface`, snapshot it as an `SKImage`,
and blit that.** Confirm the diagnosis rather than trusting it: if a `SKPicture` prototype is cheap to
try, measure both and report both.

**State the ceiling honestly in the completion note.** 800×600 @2x is ~7.7 MB of RGBA; a CPU blit of
that is order 1–2 ms, near-free if the canvas is GPU-backed. So expect the two Smith panels to go from
~13.8 ms to roughly **3–4 ms @2x** — the render roughly halved, not eliminated. If the measured result
is far better than that, find out why before believing it.

### 4.3 — Two layers, not one

A single "everything but the marker" backdrop thrashes during a **grid-point** drag, where the 37 dots
move every frame: you would pay the re-render *plus* the blit on top of a normal draw. Split it:

- **Layer A (static):** Smith chrome + frozen contour polylines. Survives both gesture types.
- **Layer B (semi-static):** the grid-point dots. Invalidated by a grid-point drag, untouched by a
  marker drag.
- **Live (uncached):** markers, glyphs, VSWR overlay — drawn directly every frame.

Two cached layers means two blits. **Do not add a third**; past two, the blit cost starts eating the
saving. R3B §2 already made a mid-drag grid-point frame cost zero HB solves, so that gesture can afford
Layer B's rebuild.

### 4.4 — The invalidation key is the actual work

The realistic failure mode here is stale pixels from a missed key, and it will be harder to spot than
either of the render bugs `src/Ui/RESOLVED.md` already records. Key **at minimum** on: panel rect,
device pixel scale, the Γ→canvas transform (including `TitleBandHeight`, which both render and
hit-test already share), theme/palette, the grid-point set, the contour set, which iso-lines are
enabled, and the title text. **Write the key as one named function** with the field list in one place,
so a future field addition has exactly one site to update.

### 4.5 — Gate

Per-panel render times at 1x and 2x, before and after, in §1.4's own table format so the two are
directly comparable. Plus a **correctness gate**: a frame rendered with the cache on and one with it
off must be pixel-identical for a static scene, and the cache must be shown to invalidate on each key
field individually — a test per field, not one test that changes everything at once.

### 4.6 — One measurement to take while you are in here

§1.4 named `ReadoutStripView.LastSetItemsMs` as self-timing instrumentation that only an interactive
run can read, and computed that ~60–70 ms of the owner's observed ~90 ms sits in the strip rebuild and
the Avalonia round trip — i.e. **more than everything §4 addresses.** Read that number during the
interactive check and report it. **Do not fix it in this brief** — it is not in scope — but do not
leave the number unread either.

---

## 5. The drag-size FPS asymmetry, and the seed policy

> **owner:** *"small changes in the drag of the marker cause seemingly very fast FPS. But when I make
> large changes to the marker, the FPS appears to drop … I still think we're seeding bad. Remember,
> we're solving nonlinear circuits here which can have a strange fourier solution across the
> termination plane. I suspect it's just simply faster to always use DC as the initial condition."*
>
> *"My previous prototype software never had this issue and I was always sweeping using only the DC
> operating point as the initial condition."*

### 5.1 — The owner's reasoning is sound, and there is a specific mechanism that fits it exactly

The physical argument is right and it matters: HB solutions across the termination plane are **not a
smooth family**. Two nearby Γ can sit in different basins, so a spectrum carried from elsewhere on the
plane is not merely a slightly-wrong seed — it can be an actively misleading one, worse than starting
cold. This also weakens the "seed from the nearest already-solved Γ" idea that was floated in
discussion; treat that as **not recommended**, and do not build it.

**And there is a mechanism in the code that fits the small-vs-large asymmetry precisely.**
`PinSearch.Sweep` takes `priorLevelSpectra` — R-h9r2-19's lever 1 — the **previous frame's** converged
spectrum at each Pin level, keyed by level, tried **first at every rung**:

```csharp
Complex[,]? levelSeed = priorLevelSpectra is not null &&
                        priorLevelSpectra.TryGetValue(Math.Round(pavlDbm, 6), out var prior)
    ? prior : seed;
```

- **Small drag:** the prior frame's spectra are near-perfect at *every* rung. One Newton iteration
  each. Very fast — exactly what the owner sees.
- **Large drag:** the prior frame's spectra are wrong at *every* rung simultaneously, and each rung
  prefers that wrong seed over its own in-ladder predecessor. Slow — exactly what the owner sees.

This also explains why the prototype never showed it: with a DC-only initial condition there is no
cross-frame seed to go stale. **Lever 1 is the prime suspect, and "always use DC" is very close to
"turn lever 1 off".**

### 5.2 — Measure three policies, do not just implement one

§1 must land first. Then instrument per drag frame: **solve ms, Newton iterations per rung, and the
frame's |ΔΓ|.** Sweep the marker across a controlled range of jump sizes and measure:

- **Policy A — today.** `warmStart` + `priorLevelSpectra`.
- **Policy B — the owner's.** No cross-frame reuse at all: the ladder's first rung starts cold from the
  real DC seed (§3's `SeedFromRealDc`, already built and cached), every rung after it warm-starts from
  its own in-ladder predecessor. *Note what B does and does not mean:* it is not "solve every rung from
  DC" — a 40 dBm rung generally will not converge from DC directly. It is "the sweep starts from DC and
  chains up", which is what the prototype did.
- **Policy C — B, plus lever 1 re-enabled only when |ΔΓ| is below a threshold.** The hedge, if B costs
  too much on small drags.

**Decision rule:** if B's small-drag frame time is within noise of A's, **delete lever 1** and take B —
one policy, no threshold, no tuning constant, and it matches the owner's prior experience. Only fall
back to C if B measurably loses on small drags, and if it does, the threshold must be reported with the
measurement that chose it, not picked.

### 5.3 — The confound to control for, and the shape that discriminates

- **Large and hard are entangled.** A large drag is usually also a drag *outward*, and §3's own table
  shows the hole rate climbing with `MaxGamma` — those terminations are genuinely harder. **Include a
  long tangential drag at constant |Γ| ≈ 0.5 in the measurement.** If that stays fast, distance is not
  the variable and the seeding story is wrong.
- **Gradual vs cliff.** If frame time degrades smoothly with |ΔΓ|, it is extra Newton iterations. If it
  falls off a cliff past some jump size, a continuation fallback is firing (a bad-enough seed fails the
  direct solve and triggers a whole stepping ladder — 5–10 internal solves where one was expected).
  **Report which shape it is**; they have different fixes and the raw fps number cannot distinguish
  them.

### 5.4 — The no-op frame, independently

> **owner:** *"During drag, if the termination point doesn't change, then the point should not run
> (because that result is already rendered)."*

Correct and worth doing on its own merits, but **it is not the explanation for the asymmetry** — it
only helps the small-movement case, which is already fast. Do it anyway: quantise Γ to a tolerance
below what the display and the readout strip can resolve, and skip the solve entirely when a drag frame
lands within tolerance of the last solved termination. Same no-re-solve shape as `SetMarkerVswr` and
R3B §2's grid-point splice. **Gate it on a counter, not a stopwatch**, exactly as
`HarmonicaGridPointDragTests` does.

### 5.5 — Gate

Frame time vs |ΔΓ| for all three policies, at 1x and 2x, with §1 landed. A large-jump drag frame's
solve time within a stated factor of a small-jump one — state the factor achieved, do not assert a
target. Plus the no-op counter test. **§4 and §5 must be measured together at the end:** with the
render halved, the solve becomes the dominant *and variable* term, so the asymmetry will be more
visible after §4 than before it, not less.

---

## 6. Guardrails

1. **`PinSearch.Sweep`'s ladder semantics change in exactly one way** — §1's early stop. Inclusivity at
   both ends, the running-gMax rule, the interpolated `SweepCompression`, and `ExactCompressionSolve`
   are all untouched.
2. **`LoadpullEngine`'s ladder and stop logic are untouched.** §2 changes the seed and nothing else.
   `PursuitEngine`, `LoadpullPursuitEngine` and their caches are not touched at all.
3. **No parallelism anywhere in this brief.**
4. **`PlotRenderer` and `AxesRenderer` are not widened, not subclassed, and not touched.** The cache
   lives on the harmonicaRF side.
5. **No DataDisplay changes**, including "harmless" preparatory refactors toward sharing the cache.
6. **Every new counter is a counter, not a log line** — `Retries`' precedent. Extra bracket probes
   (§3), DC seed computes (§2), skipped no-op frames (§5) all get a visible count.
7. **Nothing is re-baselined without the owner.** §2 and §3 both can move goldens; movement gets
   explained and approved, never absorbed.

## 7. Noted, not scoped — do not act on these

Recorded so they are not rediscovered, and deliberately left undone:

- **`LoadpullEngine` aborts a whole drive-up on one failed solve.** `if (!sr.Converged) { stopReason =
  "NonConvergence"; break; }` — a single flaky rung at, say, 22 dBm truncates the ladder and the point
  never reaches compression. §3's fix #3 (one retry from the DC seed before giving up) would cover it,
  and it is worth more here than it was in harmonicaRF, where a failed solve costs one dot rather than
  a whole ladder. **The owner has declined it this pass.** Do not implement it.
- **`convergedV[gi] = lastConv.V` stores the most-compressed step**, which `FindNearestSeed` then hands
  the next grid point as its warm start — the ~80 dB mismatch RESOLVED.md §3.2 hypothesised. §3
  measured that the tickle survives it, but with `Tickle=off` that compressed spectrum seeds `PinStart`
  directly. Part of declined fix #2. **Not scoped.**
- **RESOLVED.md §5's loadline `Ids` question is still awaiting the owner's call** and is untouched here.

## 8. Completion note — what it must contain

- §1: before/after solve counts on the shipped default; what the panel looks like at 0, 2 and 3 dB of
  overdrive margin.
- §2: **what `HbEngine` actually does with a null seed** — stated plainly, even if the answer is
  "nothing to fix". Golden movement, if any, itemised.
- §3: reproduction of the 28.4 vs 27.2 dBm figure; solves/point after the fix against 4.6; the
  worst-case grid deviation after.
- §4: the per-panel render table at 1x and 2x, before and after, in §1.4's format; the measured
  `LastSetItemsMs`.
- §5: frame time vs |ΔΓ| for policies A/B/C; which policy was taken and why; gradual-or-cliff; the
  tangential-drag control result.
- Anything in this brief that turned out to be wrong. R3B's own §1.1 correction is the standard here —
  say so plainly and show the measurement that overturned it.
