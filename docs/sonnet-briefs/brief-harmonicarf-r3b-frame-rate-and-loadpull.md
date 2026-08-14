# Brief — harmonicaRF Round 3B: the frame rate, the loadpull holes, and parallel solves

**Read first:** `docs/design/harmonicarf.md` **§6.3–6.8**, then `src/Harmonica/CLAUDE.md` in full
(especially the R2B entry and H0–H3's measured cost table), then these five files:
`src/Harmonica/PinSearch.cs`, `src/Harmonica/ContourGrid.cs`, `src/Harmonica/SolvePool.cs`,
`src/Ui/Harmonica/HarmonicaSolver.cs`, and `HarmonicaViewModel.RequestFrame` /
`RequestScheduledFrame` / `OptionsFor` / `DragGridPoint` (`src/Ui/Harmonica/HarmonicaViewModel.cs`
~690–1020).

**Three of the five sections below are already root-caused** — §1 by direct profiling (its numbers are
measured, and it **overturns an earlier draft of this same brief**; read §1.1's correction), §2 and
part of §5 by reading. Reproduce §1's figures, do not re-derive them. §3 is a genuine investigation and
its first deliverable is a measurement, not a fix. §4 is new work.

**§1 changes this brief's scope: it lands in `src/Core`, not in harmonicaRF.** The bottleneck is the
SDD expression evaluator, which every SDD consumer in the repo shares — the HB engine, the DC engine,
loadpull, and the hero goldens. §6 and §7 carry the extra guardrails and gates that forces.

**The shipped default document is the fixture for every measurement in this brief.**
`HarmonicaViewModel.DefaultModel()` — Hero 2's GaN HEMT as an SDD, `K = 3`, f₀ = 2 GHz, Vds = 48 V,
Vgs = −3.05 V, `PinStartDbm = -10`, `PinMaxDbm = 34`, `CompressionDb = 3`, and `PinStepDbm` at its
`HarmonicaSettings` default of **1.0**. Every number quoted below is for that document. Report your
own measured figures against it in the completion note.

---

## 1. The frame rate: the power sweep stays live, and the bottleneck is the SDD evaluator

> **owner:** *"There is something seriously wrong with the FPS when I drag an L1 marker to update the
> loadline (when the Fundamental load plane is used). It's way too slow for the simple DUT we are
> using. Frame rate should be >60 for the default DUT we are shipping. Note the MXP/MXE data does not
> need to be updated during this drag."*
>
> *"The whole power-sweep curve does need to be live, with live rendering. I also want the higher frame
> rate. I have built prototype software of this same feature and had no issues achieving a high frame
> rate. We must be doing something wrong. 11 FPS is pathetic. Investigate."*

**The owner is right, and an earlier draft of this section was wrong.** It proposed carrying the
power-sweep curve forward during a drag to cut the solve count. That is a workaround for a defect, it
takes away a feature the owner wants, and it would have left the real problem in place. **The power
sweep stays fully live on every drag frame. Do not reduce the amount of work. Make the work fast.**

### 1.1 — What was measured

Profiled directly on the shipped default document (`HarmonicaContext.Create(DefaultModel())`, Release
build, warmed, best of many). **These numbers are measured, not estimated — reproduce them before you
change anything, and put your own in the completion note.**

```
InterfaceCount = 2, K = 3, gridN = 16          <- 8 complex unknowns. A 16×16 real Jacobian.
Interface.Close only ............  0.0012 ms   <- negligible
PinSearch.Measure ...............  0.0022 ms   <- negligible
warm ctx.Solve ..................  0.46 ms     (1 Newton iteration)
cold ctx.Solve ..................  0.96 ms
PinSearch.Sweep(-10..34 @1dB) ...  31.5 ms     46 solves => 0.68 ms/solve
HarmonicaDataSet.Build ..........  1.10 ms     per published frame
IntrinsicPlane.Loadline(64) .....  0.60 ms     per published frame
one dut.Evaluate ................  9.36 us     <-- everything above is this, and nothing else
```

**Correction to an earlier draft of this brief, and to how the cost has been quoted before:** it cited
"~2 ms per solve" from `HarmonicaGridDragCostTests`. That figure is real but belongs to a *different*
fixture — K = 5 **with an Rd/Rs/Ls package**, a materially bigger circuit. On the shipped default a
warm solve is **0.46 ms**, and the whole 46-rung tier-A sweep is **31.5 ms**, not 92 ms. The count was
never the problem.

### 1.2 — Where the 9.36 µs goes, and why it is ~50–100× too slow

`SddModel.Evaluate` calls `SddEvaluator.EvalDual` once per port equation — a **tree-walk of the parsed
AST in forward-mode dual arithmetic.** Measured separately:

```
node counts — trivial 1, small 3, big 80
EvalDual trivial ("_v1") ..........  0.447 us   (1 node)
EvalDual small  ("_v1/50") ........  0.621 us   (3 nodes)
EvalDual big    (the drain eqn) ...  8.705 us   (80 nodes)
the same formula hand-written in C#:  0.0022 us
```

0.447 + 8.705 ≈ 9.15 µs, against the 9.36 µs measured for `dut.Evaluate`. **The device evaluation is
100% expression evaluation. There is nothing else in it.** Two separable costs:

**(a) ~0.45 µs of pure per-call setup, paid even for a one-node expression.** `SddEvaluator.EvalDual`
does this on **every single call**:
```csharp
var bindings = new Dictionary<string, Dual>(StringComparer.Ordinal);
foreach (var kv in parameters) bindings[kv.Key] = Dual.Param(kv.Value, n);
for (int i = 0; i < n; i++) bindings[$"_v{i + 1}"] = Dual.Seed(portVoltages[i], n, i);
```
A dictionary allocated and populated, with **string interpolation to build `"_v1"`, `"_v2"` per
call** — and then every `RefExpr` in the tree does a string-hashed lookup into it. For the gate
equation `_v1/50` that setup is 72% of the whole cost.

**(b) ~105 ns per AST node** ((8.705 − 0.447) / 79). For nodes that are an add or a multiply that is
absurd. The likely contributors, in the order worth checking:
- **`Dual` is ~144 bytes and is copied by value through every operation.** `DualGrad` is
  `[InlineArray(Dual.MaxN)] double` with `MaxN = 16`, so the struct carries a 128-byte gradient
  **regardless of the actual N — which here is 2.** Every `Add`/`Mul`/`Div`/`Pow` takes two by value
  and returns a third.
- the string-keyed dictionary lookup at every `RefExpr`;
- the `switch` over AST node types with local functions/closures per call, and the static
  `AdWarnings.CurrentModel` write per call.

For calibration: the hand-written C# form of the same formula (two `exp`, two `log`, two `tanh`, ~30
arithmetic ops) measured 0.0022 µs — **treat that as an over-optimistic floor**, because the loop body
is loop-invariant and the JIT may have hoisted it. Even a deliberately pessimistic floor — say three
libm calls at ~15 ns plus 30 arithmetic ops — is on the order of **100 ns for the whole 80-node
expression**, against 8,705 ns today. The headroom is one to two orders of magnitude and is not
subtle.

### 1.3 — What to do

**Fix the evaluator. Everything else in this brief gets faster for free**, because every hot path in
harmonicaRF is the same function: the HB Newton residual and Jacobian (gridN = 16 evaluations per
iteration), `IntrinsicPlane.Loadline` (0.597 ms = exactly 64 × 9.3 µs), `GatePortSelfSpectra`,
`HarmonicaDataSet.Build`, and every one of the ~280 solves in a contour grid.

In increasing order of effort, and **measure after each so the payoff of each is known separately**:

1. **Resolve names to slots once, not per call.** Bind each `RefExpr` to an index when the AST is
   cached on `SddModel`, and pass values as a `ReadOnlySpan<double>`/array indexed by that slot.
   Kills the per-call dictionary, the `$"_v{i+1}"` interpolation and every string hash in the walk.
   Cheap, local, and worth ~0.45 µs on **every** call — which on the small equations is most of it.
2. **Stop carrying 16 gradient slots when N is 2.** Either make the width generic/specialised, or
   restructure the walk so the gradient lives in one stack-allocated buffer written in place rather
   than in a 144-byte value copied through every node.
3. **Compile the AST.** Either to a delegate (`System.Linq.Expressions`) or — better for startup, AOT
   and predictability — to a flat instruction array executed by a small stack machine over
   `Span<double>`. This is what turns ~105 ns/node into single-digit ns/node, and it is where the bulk
   of the remaining gap lives.
4. **Give `SddModel` a real batch path.** `ComponentModel.PrefersBatchEvaluate` / `EvaluateBatch`
   already exists and is already used by external devices — and `IntrinsicPlane.GatePortSelfSpectra`
   already prefers it when offered, so it would benefit with no call-site change. Evaluating all
   gridN = 16 time points in one call amortises whatever setup survives step 1 and opens the door to
   vectorising.

**Stop and report after step 1 and step 2 with measured figures before starting step 3.** If the first
two get a warm solve to ~50 µs, the whole tier-A sweep lands near 2–3 ms and the owner's requirement is
already met with the power sweep fully live — at which point step 3 is optional and should be proposed
rather than assumed.

### 1.4 — Then account for the rest of the frame, because it does not yet add up

The solve side of a drag frame today is roughly 31.5 (sweep) + 1.1 (dataset) + 0.6 (loadline) ≈
**33 ms ≈ 30 fps**, and the owner observes **~11 fps**. **Roughly two thirds of the frame is
unaccounted for by the solve, and that gap has not been measured.** Find it before declaring the frame
rate fixed. Candidates, all of which you can instrument through the existing `FrameTiming` record
(TierA/GridSolve/Fit/Raster/Render) plus your own probes:

- **the canvas draw** (`HarmonicaCanvas` / `HarmonicaPanelRenderer` — `LastRenderMs` is already
  plumbed to the scheduler; read it);
- **the readout strip rebuild on every published frame** — `ReadoutStripView.SetItems` clears and
  rebuilds every column's controls on each frame, which is dozens of Avalonia controls per frame
  (`SetInputs` already has an in-place path for exactly this reason; `SetItems` does not);
- **pool and dispatcher overhead per frame** — `SolvePool.Submit` allocates a `CancellationTokenSource`
  and a `Task` per pointer move, and the completion marshals to the UI thread;
- **pointer-event coalescing** — if every `PointerMoved` submits a frame, a fast drag can generate far
  more requests than frames. Latest-wins drops them, but the drop is not free and the *measured* fps
  is then bounded by how the events are batched, not by the solve.

**Report the full per-stage breakdown of one mid-drag frame, before and after.** A frame-rate claim
without it is not evidence.

### 1.5 — What is still allowed to be dropped during a drag

The owner named one thing explicitly: **MXP/MXE need not update during an L1 drag** (they already do
not — `SolveAtOptimum` is gated on `FrameQuality.Full`, and a drag forces `SkipContours`). Leave that
as it is. **The power sweep, the loadline, the glyphs and the operating-point readouts all stay
live.** If after §1.3 and §1.4 the target is still not met, come back and say so with numbers — do not
quietly start dropping content again.

---

## 2. Dragging a grid point costs 46 HB solves for a result that cannot have changed

> **owner:** *"The FPS is seriously way too slow when dragging a grid point on a Smith Chart. There
> should be no harmonic balance calculations that occur during the drag; the drag is simply moving a
> glyph on the screen and should render way above 60 FPS. It should probably be >120 FPS. Perhaps
> it's a similar bug to the L1 marker being slow."*

**The owner's guess is right and it is the same 46-solve tier-A sweep** (31.5 ms measured, §1.1).
**This section stands on its own and is not made obsolete by §1** — a gesture that changes no circuit
state should cost no solves at any evaluator speed; §1 only changes how badly it hurts.
`HarmonicaViewModel.DragGridPoint` (~line 991) ends in:

```csharp
var plan = Scheduler.NextPlan(dragging: true);
return RequestFrame(OptionsFor(plan, dragging: true), gridPointOverride: (index, gamma));
```

`OptionsFor(..., dragging: true)` sets `SkipContours = true`, so no grid is swept — **but tier A still
runs the whole ladder**, at terminations that a grid-point drag does not touch at all. R-h9r2-4 chose
Option 1 (splice the moved point into the carried `GridPoints` list, display only) precisely so this
gesture would be cheap, and then routed it through the full frame pump anyway.

**A mid-drag grid-point frame must cost ZERO HB solves.** The dragged Γ is a display-only edit to the
already-published `SmithPanelData.GridPoints`; nothing in the frame's physics depends on it until
release. Move it off the solve pool entirely: mutate the published frame's grid-point list on the UI
thread, raise `RedrawRequested`, done. The existing `ApplyGridPointOverride` already builds exactly
that spliced list — it is being handed to a solve that had no reason to run.

On release (`dragging: false`), `DragGridPoint` already installs the new `CustomGrid` and requests a
real frame. Leave that path alone.

Precedent for "a gesture that redraws without re-solving" is already in this file:
`SetMarkerVswr`/`ToggleMarkerVswrEnabled` do exactly that, with the reasoning written out. Follow that
shape.

**Gate it on a counter, not on a stopwatch:** N pointer-move events during a grid-point drag must
leave `SolvePool.StartedCount` and `HarmonicaSolver.LastSolveCount` unchanged, and the glyph must
still move.

---

## 3. The loadpull grid gives up far too early — investigate before you fix

> **owner:** *"There is something fundamentally wrong with the way the loadpull contours are being
> generated. With the default DUT and configuration, the contour isolines do not close above
> ZL1 > 200 Ω — the grid glyphs render as holes, implying that section of the Smith chart grid does
> not converge in harmonic balance. However, if I move the L1 marker to ZL1 = 200 Ω, I see a good
> drive-up with convergence and proper compression. So… what is the loadpull algorithm (or the
> drive-up algorithm for the loadpull) doing that makes it so bad? For this default DUT and
> configuration, a convergence plane should be greater than gamma 0.8. My number-one suspect is the
> drive-up algorithm. Second suspect is the warm start. […] Note that for harmonicaRF the DC solve can
> be performed as soon as the DUT is loaded into memory and can always be reused throughout all
> harmonica calculations. A drive-up that does not seed with a warm start based on a different grid
> point should at the very least be using the DC solve as its warm start."*

**The owner's own evidence is the sharpest tool here**: at ZL1 = 200 Ω (Γ ≈ 0.6 real) the tier-A path
converges and compresses cleanly, while the grid reports a hole at the same load. **Tier A and the
grid run different drive-ups.** Tier A is `PinSearch.Sweep`; the grid is `PinSearch.Run`. Whatever the
difference is, it is between those two functions and their seeding — not in the device, the
terminations or the HB solver.

### 3.1 — Measure first, and report the histogram

**Deliverable one is a diagnostic, not a fix.** Nothing in the current code distinguishes the three
ways a point becomes a hole. `GridPoint.IsHole` is `!Result.Compressed`, and `PinStopReason` carries
`PinMax` vs `NonConvergence` but is never surfaced past `PinSearchResult`.

Add enough instrumentation to answer, for the shipped default document on a 5 × 12 ring grid at
`MaxGamma = 0.8` (and again at 0.9):

- per Γ point: `PinStopReason`, solve count, and **which stage failed** — the tickle, the `PinStart`
  solve, a bracket step (at what Pin), or a secant step (at what Pin);
- the totals: converged / `PinMax` / `NonConvergence`;
- the same grid with the warm start and the Pin hint **disabled** (cold every point);
- for three failing Γ points, `PinSearch.Sweep` run at that same termination — does it converge, and
  where does it compress?

Put this in `tests/Harmonica.Tests` as a `Category=Benchmark` test that *reports* (this repo's
convention for a measurement) rather than asserting a threshold you have not yet earned. **Print the
table into the completion note.** If the failures are overwhelmingly `PinMax` rather than
`NonConvergence` the rest of this section is the wrong tree and you must say so.

### 3.2 — The specific mechanism to test first

`ContourGrid.Build` records, for each converged neighbour:

```csharp
converged.Add((gamma, result.Steps[^1].Point.V, result.AtCompression?.PavlDbm));
```

`Steps[^1]` is the **last solved step** — i.e. a spectrum at or near the compression drive, often
25–34 dBm. `PinSearch.Run` then opens at the *tickle*:

```csharp
Complex[,]? seed = warmStart;         // the neighbour's ~30 dBm compressed spectrum
...
var tickle = Solve(s.TickleDbm);      // −50 dBm
if (tickle is null)
    return new PinSearchResult(PinStopReason.NonConvergence, solves) { Steps = steps };
```

**A −50 dBm solve seeded with a heavily compressed large-signal spectrum from a different load is an
80-dB drive-level mismatch, and one failed Newton there turns the whole Γ point into a hole.** The
owner's hypothesis was "the neighbouring grid point is too far in Γ"; the drive-level mismatch is
worse than that and is structural rather than geometric. Note that tier A does not have this problem —
`Sweep` keys its prior spectra **by Pin level** (`priorLevelSpectra`) and falls back to the in-ladder
predecessor. `Run` has no equivalent.

Second, related: the bracket's first probe jumps straight to a neighbour's compression Pin
(`pin = Math.Clamp(hint, pinLo + 0.25, PinMaxDbm)`) — potentially +35 dB in one step — seeded with the
`PinStart` (−10 dBm) solution.

Third, a contagion effect worth checking in the histogram: a hole contributes nothing usable to
`converged`, so its neighbours have to reach further for a seed, and one failure along a ring can walk
outward. That would show up as a contiguous arc of holes rather than scattered ones — which is exactly
what "the isolines do not close above 200 Ω" looks like.

### 3.3 — The fixes, in the order the measurement should justify

1. **A real DC operating point as the cold seed — the owner asked for this by name, and today's cold
   seed is not one.** `HarmonicaContext.SeedFromDc` solves *the linear network's* DC point **with the
   devices absent** (`V = −Y(0)⁻¹·I_src(0)`, harmonics zero); its own comment says so. Compute the
   true nonlinear DC solution once per (`StructuralKey`, bias) — it is available the moment the DUT is
   loaded, exactly as the owner says — cache it on `HarmonicaContext`, and use it as the seed whenever
   there is no warm start. Invalidate on rebuild and on `SetBias`. This is cheap, it is correct, and
   it makes every subsequent recommendation testable against a sane baseline.

2. **Never seed a solve at one drive level from a converged spectrum at a very different one.** The
   minimal version: `ContourGrid` keeps the neighbour's whole ladder rather than only `Steps[^1]`, and
   `Run` picks the neighbour step whose Pin is nearest the level being solved — the same rule `Sweep`
   already implements. The cheaper version: seed the tickle and the `PinStart` solve from the DC point
   (item 1) and use the neighbour's spectrum only from the bracket onward. **Measure both**; report
   converged-count and solves-per-point for each.

3. **Retry cold before declaring a hole.** A failed `Solve` inside `Run` currently returns
   `NonConvergence` immediately. One retry from the DC seed costs one solve on the paths that are
   already failing and nothing on the paths that are not. Count the retries so the cost is visible.

4. **Report `PinMax` and `NonConvergence` differently.** Once the histogram exists, the two are not
   the same story: `PinMax` means "this load does not compress by 3 dB below 34 dBm", which is a real
   physical answer; `NonConvergence` means the solver gave up. Today both render as an identical
   hollow dot. At minimum make the distinction reachable — a tooltip, the status strip, a counter in
   the completion note. Do not silently reclassify a `NonConvergence` as converged.

**Do not touch `PinSearch.Sweep`'s ladder semantics** (R-h9r2-17/18/19 are the owner's own explicit
specification) and **do not touch `LoadpullEngine`** — every Hero 3/3B golden walks its ladder and it
is out of scope here.

### 3.4 — The gate

**A 5 × 12 ring grid at `MaxGamma = 0.8` on the shipped default document has no holes**, or every
remaining hole is a `PinMax` with a stated physical reason. The owner's expectation is "a convergence
plane greater than gamma 0.8", so also report the converged fraction at 0.85 and 0.9 rather than
tuning to exactly 0.8. Quote before/after counts.

---

## 4. Parallelise the grid solve

> **owner:** *"Can we use parallel compute to speed up the loadpull portion of the simulation? Perhaps
> batch as 4 or 8 points per core so they can share a warm start within their batch group."*

**Yes, and the cores are already idle.** `SolvePool` sizes itself at `ProcessorCount − 2` workers but
runs **one frame at a time** (latest-wins), so during a grid build exactly one worker is busy and
`ContourGrid.Build` walks its Γ list in a single `foreach`. On an 8-core machine that is 6 idle
workers for the ~550 ms that dominates a full frame (H0–H3 measured the split as SOLVE 0.804 s / FIT
2.87 ms / EXTRACT 67.9 ms — **the solves are 92% of it**).

Constraints, all of them load-bearing:

- **`HarmonicaContext` is not thread-safe** — `src/Harmonica/CLAUDE.md`'s own rule, and `SetBias`
  mutates the netlist in place. Each parallel batch needs its **own** context. Creating one is
  milliseconds (elaboration) and happens once per structural change, so a small pool of contexts held
  alongside the grid is the right shape. `SolveWorker.EnsureContext` is the existing pattern for
  "reuse unless the structural key moved" — follow it rather than inventing a second rule.
- **`ContourGrid` itself rewrites `_points` and the RBF factorization on every `Build`** — the
  parallel region must write into a pre-sized array indexed by Γ position and assemble `_points` in
  the original order afterwards, so the point ORDER is independent of completion order.
- **Batches must be spatially coherent and deterministically assigned.** The owner's 4–8 points per
  batch is the right granularity: partition by ring/arc so a batch's members are Γ-neighbours and can
  share a warm start and a Pin hint within the batch, exactly as the serial loop does today. **Fixed
  partition, no work stealing** — a run must be reproducible.
- **Cross-batch seeding is where determinism dies.** Today every point can seed from any earlier
  converged point. If batches share seeds across threads the answer depends on timing. **Keep seeding
  strictly within a batch** (plus, optionally, one deterministic seed handed to each batch up front —
  e.g. the DC point from §3.3, or the result of one serial "leader" point solved before the fan-out).
  State which you chose.
- **Cancellation is per point already** (`ct.ThrowIfCancellationRequested()` between Γ points). Keep
  that granularity inside each batch; a superseded frame must still abandon within one point's cost.
- **`onProgress` is called from several threads** once this lands. Make the counter atomic and the
  callback's contract explicit, or route progress through a single reporter.

**Correctness gate: the parallel grid's per-point metrics must agree with the serial grid's.** They
will not be bit-identical — a different seed reaches a different point inside the same convergence
tolerance — so compare with a stated tolerance (Pout in dB, DE/PAE in points) and **report the actual
worst-case deviation.** If it is larger than the compression tolerance can explain, something is wrong
and you must say so rather than widening the tolerance. A point that is a hole serially and converged
in parallel (or vice versa) is a **failure of this gate**, not a bonus — report it either way.

**Performance gate:** report wall-clock for a 61-point grid, serial vs parallel, on the shipped
default document, with the machine's core count stated. `Category=Benchmark`, best-of-N, in the
non-parallel `HarmonicaBenchmarks` collection — H0–H3's own note records that six timing classes
contending with each other once *inverted* a comparison, so this is not optional.

**Do this after §3.** Parallelising a drive-up that fails on a third of the grid just gets to the
wrong answer faster, and §3's fixes change the seeding rules this section has to respect.

---

## 5. Is K really 3?

> **owner:** *"The default configuration says K=3 (harmonic order) in the display. However, when I look
> at the loadpull line shape, I suspect that many more harmonics are being used to calculate the time
> domain. I suspect that K is actually not 3 and is much higher than is displaying."*

**The displayed K is honest — `HarmonicaInputs` reads `model.Settings.HarmonicCount` and
`DefaultModel()` sets 3 — but the loadline the owner is looking at genuinely does contain harmonics
above K, and there is a specific reason.** `IntrinsicPlane.Loadline`:

```csharp
vds[t] = pv[drainPort] - (...);                        // band-limited to K, from ResampleSpectrum
ids[t] = dut.Evaluate(new PortVoltages(pv)).I[drainPort];   // NOT band-limited — instantaneous
```

The **voltage** axis is the truncated Fourier series evaluated at `LoadlineSamples` (64) points, so it
carries harmonics 0…K and nothing more. The **current** axis is the device law evaluated pointwise at
those same 64 instants — the device's full nonlinear response, including every harmonic the HB solve
truncates away at K. So the drawn locus is "the exact device current at a band-limited voltage", which
has more spectral content than the solve does, and on a strongly-driven device it *looks* like a
higher-K loadline.

**Do this, in order:**

1. **Confirm it by measurement.** Take the shipped default at its compression point, DFT the drawn
   `ids` array, and report the magnitude of bins 4…N relative to the fundamental. Do the same for
   `vds` (which should be identically zero above 3, to round-off). Put the numbers in the completion
   note — this is the evidence the owner asked for.
2. **Audit the rest of the K path while you are there** and confirm there is no second, higher order
   in play: `HarmonicaContext.ReExtract` / `Solve` (`HarmonicCount` → `InterfaceNetwork` and
   `HbFft.GridSize(K, FftOverSample)`), `HarmonicaDataSet`, `TerminationSet.HarmonicCount`,
   `HarmonicaNetlist`. State what you found. `FftOverSample` (default 1) enlarges the FFT **time
   grid**, not the number of retained harmonics — say so explicitly if that is what you find, because
   it is the obvious thing to mistake for a second K.
3. **Then stop and report.** Whether the displayed current should be truncated to K bins (making the
   loadline consistent with the solve) or left as the true device response (physically what the device
   does at that voltage) is the owner's call, not yours. Present both with the measured harmonic
   content and ask. **Do not change it unilaterally** — the loadline is a headline visual and either
   answer is defensible.

---

## 6. Scope guardrails

- **No menu, chrome, title or readout-layout work** — those are **R3A** and **R3C**.
- **Do not change `PinSearch.Sweep`'s ladder semantics.** Inclusive at both ends, every point a real
  solve, running `gMax`, first-crossing compression: all four are R-h9r2-17/17a/18/19's own explicit
  specification and the last one has a measured reason (computing `gMax` globally moved a real
  fixture's compression point from single digits to 27 dBm).
- **Do not touch `LoadpullEngine`, `PursuitEngine` or any hero golden.**
- **Do not weaken `ContourGrid._reusableAgainst`.**
- **No static mutable state** — `src/Harmonica/CLAUDE.md`'s rule, and §4 makes it sharper, not softer.
- **The `.charm` format does not change.** Nothing here needs a new persisted setting; if you conclude
  one is unavoidable, say why before adding it.
- **`RfCore` untouched. `src/Engine` untouched.**
- **`src/Core` IS in scope, but ONLY for §1.3's evaluator work** — `SddEvaluator`, `Dual`, and
  `SddModel`'s caching of its parsed ASTs. Everything else in `src/Core` stays shut, and in particular:
  - **the expression LANGUAGE does not change.** No new syntax, no new functions, no change to
    parsing, precedence, or what an SDD equation may say. This is an execution-strategy change and
    nothing else. `docs/design/expressions.md` should not need an edit; if you think it does, stop and
    ask.
  - **the answers do not change.** `SddEvaluator` is shared by the HB engine, the DC engine, loadpull
    and the SDD-based hero references. Preserve floating-point operation order so results stay
    **bit-identical**, and gate that (§7). A "faster but slightly different" evaluator is a refusal:
    it would move every hero golden at once and nobody would be able to tell an improvement from a
    regression.
  - **`AdWarnings` domain-error reporting keeps working.** The clamps and warnings in `Dual`
    (`ExpCap`, `LogFloor`) are behaviour, not decoration — an SDD that today reports a domain warning
    must still report it.

---

## 7. Gates

1. **Build + `dotnet test` green** — `tests/Core.Tests`, `tests/Engine.Tests`, `tests/Harmonica.Tests`
   and `tests/Ui.Tests` while working, **full solution at the end, and this time that is not a
   formality**: §1 changes code every SDD in the repo runs through. Any new timing test carries
   `[Trait("Category", "Benchmark")]` and joins the `HarmonicaBenchmarks` collection.
2. **§1 — the evaluator produces BIT-IDENTICAL results.** A corpus test evaluates a set of ASTs (at
   minimum: the shipped default's two equations, every SDD equation appearing in `testdata/`, and a
   handful of hand-written ones exercising `^`, conditionals, every supported function, and the
   `ExpCap`/`LogFloor` clamp paths) through the old and the new path at many operating points, and
   asserts exact `double` equality on the value **and every gradient slot**. Not a tolerance — equality.
   Build this **first**, before changing anything, so it is a real before/after and not a
   rationalisation.
3. **§1 — every hero golden still passes at its stated PRD tolerance**, and Hero 2/3/3B (the SDD-based
   ones) are named explicitly in the completion note with their measured deviations.
4. **§1 — the measured speedup is reported per step** (§1.3 items 1, 2, and 3 if reached), as
   µs/`dut.Evaluate` and ms per warm `ctx.Solve` on the shipped default, against the baselines in
   §1.1.
5. **§1 — an L1 marker drag keeps the power sweep FULLY LIVE** — every rung solved, every frame, no
   carry-forward — and the measured frame time and fps are reported against the owner's >60 target.
   If the target is not met, say so with the per-stage breakdown from §1.4 and what is left.
6. **§1.4 — a full per-stage breakdown of one mid-drag frame is reported**, before and after, covering
   solve, render, strip rebuild and pool overhead. The ~22 ms currently unaccounted for is named.
7. **§2 — a grid-point drag costs zero HB solves per frame**, counter-gated, with the glyph still
   tracking the pointer.
8. **§3 — the hole histogram is reported**, before and after, for `MaxGamma` 0.8 / 0.85 / 0.9, with the
   `PinMax`-vs-`NonConvergence` split and the failing stage named.
9. **§3 — no `NonConvergence` holes remain on the shipped default at `MaxGamma = 0.8`**, or each
   remaining one is explained.
10. **§3 — the DC seed is a real nonlinear DC solve**, cached per structural-key-plus-bias, gated by a
    compute counter (not a clock), and invalidated on rebuild and on bias change.
11. **§4 — the parallel grid agrees with the serial grid** within a stated tolerance, with the measured
    worst-case deviation and the serial/parallel wall-clock both reported, and the hole SET identical.
12. **§4 — point order is independent of completion order**, and a superseded frame still cancels
    within one Γ point's cost.
13. **§5 — the harmonic-content measurement is reported and the question is put back to the owner**,
    with no unilateral change to the loadline.

**Interactive verification is required** for the two drags — no visual driver exists here, matching
every prior harmonicaRF phase. List the exact gestures in the completion note under "please confirm on
your end", and include: drag L1 across the chart and watch the loadline track it; drag a grid point;
drag L1 out past Γ 0.8 and confirm the contours close.

---

## 8. Write-up — READ THIS BEFORE YOU FINISH

**Do NOT append a phase write-up to `src/Harmonica/CLAUDE.md` or `src/Ui/CLAUDE.md`.** That is how
`src/Ui/CLAUDE.md` reached 21,417 lines and had to be archived, and `src/Harmonica/CLAUDE.md` is well
down the same road.

Instead: **create `src/Harmonica/RESOLVED.md`** and put this brief's detail there, following the shape
of the existing `src/Ui/DataDisplay/RESOLVED.md` — a title, a short note about why the file exists,
then one `##` section per completed brief. Anything that belongs to the `src/Ui` half (the frame-pump
changes, the drag routing) goes in `src/Ui/RESOLVED.md`, which brief R3A creates; if R3A has not
landed, create it the same way.

**Use it sparingly. Only truly important findings.** For this brief, the candidates are:

- **the SDD evaluator profile (§1.1/§1.2)** — that `dut.Evaluate` IS the whole cost of a solve, the
  ~0.45 µs per-call dictionary/string setup, the ~105 ns per AST node, and the 128-byte gradient
  carried for N = 2. Record the before/after µs. This is the single most valuable thing in the brief
  and it belongs in `src/Core/RESOLVED.md`, because it is not a harmonicaRF fact;
- **the correction that "~2 ms per solve" was a different fixture** (K = 5 with a package), so nobody
  re-derives a frame budget from it;
- the measured per-frame solve counts before and after (§1, §2);
- whatever §1.4 turns out to be — the two thirds of the frame that the solve does not explain;
- **the drive-level warm-start mismatch in `PinSearch.Run` (§3.2)**, if the measurement confirms it —
  this is the kind of thing that costs a day to rediscover;
- what the cold seed actually was before §3.3 item 1 (the linear network with the devices absent), so
  nobody assumes "seeded from DC" ever meant a real DC solve;
- the parallel-grid determinism rule from §4 (seed only within a batch) and the measured agreement;
- the loadline's un-band-limited current axis (§5), whatever the owner decides afterwards.

Everything else — file-by-file narration, what you renamed, which test you added — goes in the
completion note you hand back, not into a checked-in file. If a `CLAUDE.md` needs anything at all it
is a **one-line** standing rule with a pointer to `RESOLVED.md`.
