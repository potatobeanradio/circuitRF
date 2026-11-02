# Brief — harmonicaRF R9C: the loadpull holes, the MXP/MXE lie, and the coarse launch frame

**Read first, in this order:**
`src/Harmonica/PinSearch.cs` — **all of it**, but especially `Run`'s bracket at `:252–330`, its
`FirstCrossing` at `:334–355`, the refinement loop at `:361–413` (whose own doc comment already names
the limitation this brief hits), and `Sweep` at `:499–640`;
`src/Harmonica/ContourGrid.cs:20–41` (`GridPoint`, `IsHole`, `Metric`), `:176–258` (`Build`),
`:310–435` (`BuildParallel`), `:488–500` (`NearestByVswr`);
`src/Ui/Harmonica/HarmonicaSolver.cs:85–100` (`Options`' Rings/Spokes defaults and the comment that
justifies them), `:289–341` (the grid build and the two `SolveAtOptimum` calls), `:535–551`
(`SolveAtOptimum` — **the three lines at `:544–547` are §2**), `:617–674` (the operating-point column,
which is the reference implementation for §2.3), `:790–870` (`AddMxColumn`);
`src/Ui/Harmonica/HarmonicaViewModel.cs:1127–1150` (`RequestFrame`), `:1247–1290`
(`RequestScheduledFrame`, `OptionsFor`);
`src/Ui/Views/Harmonica/HarmonicaView.axaml.cs:164–174` (`EnsureFirstSolve`);
`src/Harmonica/FrameScheduler.cs:100–120` (`FullRings`/`CoarseRings`), `:175–180` (`NextPlan`);
`src/Harmonica/CLAUDE.md`'s standing warning about `PinSearch.Run`'s bracket — **it is the bug this
brief closes, and it has been recorded and unfixed since R3B.**

**Do NOT update any `CLAUDE.md`.** `src/Ui/RESOLVED.md` and `src/Harmonica/RESOLVED.md` only.
**No screenshot verification** — every claim below is a measurement, and the gate is more measurements.

Tag new comments `R9C §n`.

---

## 0. The investigation, first — everything below follows from it

Two owner reports, one root cause. The measurements were taken headlessly on the shipped default DUT
(SDD, Vgs −3.05, Vds 48, f₀ 2 GHz, K = 3, PinStart −10, PinMax 34, CompressionDb 3), Z0 = 80, S1 = 50 Ω,
L2/L3 near-short, over the frame path's own full-quality 3 × 12 ring grid. Debug build, single-threaded.

### 0.1 Report 1 — "the contours and MXE change when I move L1 to the MXE point"

They do not change **because of L1**. `ContourGrid.Build` overwrites the swept band per point
(`working.Set(side, tuneHarmonic, z)`) and `StateKey` deliberately excludes it, so the grid is provably
independent of L1's own value. What changes is the GRID SIZE:

`HarmonicaView.EnsureFirstSolve` calls `RequestFrame()` with **no options**, and
`HarmonicaSolver.Options`' defaults are the ladder's COARSE rung — its own comment says so: *"Defaults
are the coarse ring set of §6.8, so the first frame after opening a document is fast rather than
correct-and-slow."* Every frame after that goes through `RequestScheduledFrame` → `NextPlan(false)` →
`FrameQuality.Full` → `FullRings × FullSpokes`. Measured:

| grid | points | holes | DE argmax Z | Pout argmax Z |
|---|---|---|---|---|
| launch, 2 × 12 | 25 | **4** | 122.579 − j0.805 | 78.749 − j0.957 |
| every later frame, 3 × 12 | 37 | **1** | **132.319 − j1.786** | 78.795 + j0.464 |

The owner's second reported value, `132.319 − j1.786`, is reproduced to all six digits. So the answer
that "changed" was a 25-point answer being replaced by a 37-point one, and the launch picture also
carries four holes instead of one.

### 0.2 Report 2 — "MXE Pout 26.72 dBm while P-3dB Pout is 39.28 dBm"

At ZL1 = 132.319 − j1.786, the two compression finders in this codebase disagree completely:

```
PinSearch.Run   (the contour grid + MXP/MXE) : HOLE — NonConvergence, 7 solves
PinSearch.Sweep (the panel + the strip)      : compresses at Pin 23.104 dBm, Pout 39.276 dBm, DE 69.90%
ladder gain: -10:14.14  …  11:15.72  …  19:19.17 (peak)  …  23:16.27  24:15.35
```

`Run`'s doubling stride probes −10, −7, −1, 11, then jumps to 34 dBm — straight past the entire
compression region — and a 23 dB jump breaks the HB warm start, so that probe does not converge and
`Run` returns `NonConvergence` for the whole point (`PinSearch.cs:310–312`).

`HarmonicaSolver.SolveAtOptimum` then reports the wreckage as an answer:

```csharp
int idx = sweep.AtCompression is null ? sweep.Steps.Count - 1 : IndexOfNearestPin(...);
var step = idx >= 0 && idx < sweep.Steps.Count ? sweep.Steps[idx] : null;
return seed with { Solved = step, Published = published };
```

On a failed search, `AtCompression` is null, so `idx` is the LAST SURVIVING PROBE. That probe is
Pin = 11 dBm at gain 15.72 dB, and **11 + 15.72 = 26.72 dBm — the owner's number, exactly.** The MXE
column was showing the figures of merit at an arbitrary low-drive bracket probe of a search that
failed, labelled as the optimum.

### 0.3 Report 3 (the separate owner report) — "a hole that converges fine when I drag L1 there"

Same cause. The one hole on the 37-point grid is Γ = 0.8 (Z = 720 Ω): `Run` → `NonConvergence` (9
solves, 2 retries); `Sweep` at the identical termination → compresses at 12.74 dBm, Pout 32.05 dBm.
**1 of 1 holes converges under the uniform ladder.** The owner is right: it should never have been a
hole.

### 0.4 What it costs to fix, measured over the whole 37-point grid

| per-point search | solves | holes | wall (Debug) |
|---|---|---|---|
| `PinSearch.Run` (shipped) | 222 | 1 | 116 ms |
| ladder @ 1 dB | 1370 | **0** | 480 ms |
| ladder @ 2 dB | 736 | **0** | 248 ms |
| ladder @ 4 dB | 420 | 2 | 169 ms |

1 dB and 2 dB agree to **0.03 dB in Pin and 0.002 dB in Pout**. Anything coarser re-introduces
non-convergence — the per-step warm start is what makes a ladder robust, so *any* large Pin jump is the
hazard, whether it comes from a doubling stride or from a coarse step.

### 0.5 One thing the owner expected that is already true

> "the user expects the RBF kernel and parameter settings (as shown in the harmonicaRF settings
> dialog) to be respected when calculating the interpolated MXP and MXE metrics"

They already are. `ContourGrid.Fit` factorizes with `ContourKernel`/`ContourSmooth`/`ContourEpsilon`,
re-read from `ctx.Model.Settings` at the start of every `Build`, and `InterpolatedArgmax` refines on
that same fit — the identical surface the iso-lines are drawn from. **Do not touch the RBF path.** The
MXP/MXE *position* is interpolated from a settings-respecting surface; the MXP/MXE *numbers* are not
interpolated at all and never were — they are one real solve at that Γ (R-h9b-16, deliberate, and the
reasoning there is still right). The defect was that the solve failed and was reported anyway.

---

## 1. Owner rulings — implement these, do not re-open them

1. **The per-point compression search becomes `PinSearch.Sweep` at a 2 dB ladder step**, for contour
   grid points AND for MXP/MXE's own solve. Chosen over repairing `Run`'s bracket, and over fixing
   MXP/MXE alone.
2. **The launch frame is solved at full quality**, like every other frame.
3. Reporting a failed search as an answer is fixed regardless (§2).

---

## 2. `SolveAtOptimum` — never report a search that failed

### 2.1 The refusal

```csharp
// R9C §2 — a search that did NOT reach the compression target has no optimum to report. This used to
// fall back to `sweep.Steps[^1]` — the last surviving probe of a FAILED search — and hand it to
// AddMxColumn as though it were the compression point: on the shipped default at ZL1 = 132.3 Ω that
// probe was Pin 11 dBm at 15.72 dB gain, published as "MXE Pout 26.72 dBm" while the strip's own
// P-3dB read 39.28 dBm at the same termination. A stated "no optimum" is not a worse answer than a
// wrong one; it is the only honest one.
if (result.SweepCompression is not { } reading || result.AtCompression is not { } spectrumStep)
    return seed with { Solved = null, Published = null, UnsolvedReason = ... };
```

Add `string? UnsolvedReason` to `SmithPanelData.SmithOptimum` and populate it from the search's own
`PinStopReason` — `PinMax` ("this termination did not reach P-x dB before PinMax") and
`NonConvergence` ("the drive-up at this optimum did not converge") are different stories and R3B §3.3
already insisted they stay distinguishable.

`AddMxColumn`'s no-optimum branch (`HarmonicaSolver.cs:806–822`) then names the third case. Its current
tooltip lists two ("every grid point is a hole, or this frame is mid-drag"); it must now read the
optimum's own `UnsolvedReason` when there is one, and keep the two existing sentences otherwise.
**R7C §1.4's rule is untouched:** the branch still emits the SAME ten rows, so the chunk's shape never
changes and the column cannot churn at frame rate.

### 2.2 The solve becomes the same one the panel makes

```csharp
var result = PinSearch.Sweep(ctx, t, s.PinStartDbm, s.PinMaxDbm, s.PinStepDbm);
```

— the document's own ladder settings, i.e. **literally the call the tier-A drive-up already makes**.
MXP/MXE and the strip's operating-point column then agree by construction rather than by coincidence:
one function, one definition of "P-x dB", one running-`gMax` rule. Measured cost: ~38 solves each,
two per full-quality frame, against `Run`'s 11.

`SolveAtOptimum` stays gated on `opt.Quality == FrameQuality.Full` — a degraded rung still publishes the
glyph's POSITION and no numbers, exactly as now.

### 2.3 Read the right half of the result — this is the trap

`PinSearchResult` has two compression-shaped fields and they mean different things. The rule is already
written down at `HarmonicaSolver.cs:623–634` for the operating-point column, and **the MX column must
now follow the identical rule**:

- **scalars at compression** (Pout, Gain, DE, PAE, Pdc) come from `SweepCompression` — the interpolated
  (or, with `ExactCompressionSolve` on, the one-real-solve) reading AT the target;
- **the spectrum** (and therefore `HarmonicaDataSet.Build`, Zin, AM/PM, and `Foms.GpDb`) comes from
  `AtCompression`, the nearest solved ladder point.

Reading `AtCompression`'s own scalars instead would round every MX figure to the nearest whole ladder
step — precisely the error interpolation exists to remove, and a silent 1 dB error in Pout.

Carry both onto `SmithOptimum`: keep `Solved` (the `PinStep`, for the spectrum and Gp) and add
`CompressionReadout? SolvedCompression`. `AddMxColumn` reads `SolvedCompression` for Pout/Eff/PAE/Gain
with a `?? Solved`-based fallback, exactly the `sc?.X ?? at.X` shape the operating-point column uses —
so a future `Run`-based caller still works.

### 2.4 Gate

`tests/Ui.Tests/Harmonica/HarmonicaOptimumSolveTests.cs`:

- at a termination where the ladder compresses, the MX column's Pout equals the strip's
  operating-point Pout **when the two are at the same Γ** — assert to 0.01 dB, and construct the case
  by setting L1 to the frame's own reported optimum Γ. This is the owner's exact test, made a gate.
- a `SmithOptimum` whose search did not compress yields `Solved == null`, a non-null `UnsolvedReason`,
  and an MX column of ten rows all reading `"—"`.
- the MX column's row COUNT is identical in the solved and unsolved branches (R7C §1.4).

---

## 3. The contour grid's per-point search becomes a ladder

### 3.1 The setting

`src/Harmonica/CircuitModel.cs`, beside the other sweep knobs:

```csharp
/// <summary>
/// R9C §3 — the Pin ladder step, in dB, that each CONTOUR GRID point's drive-up walks. Separate from
/// <see cref="PinStepDbm"/> (the power-sweep PANEL's own step, default 1 dB) because the grid pays it
/// once per Γ point: measured on the shipped default's 37-point grid, 1 dB is 1370 solves and 2 dB is
/// 736, and the two agree to 0.03 dB in Pin and 0.002 dB in Pout.
///
/// <para><b>Do not raise this past 3 dB.</b> Measured, not assumed: at 4 dB the same grid grew 2
/// holes and at 6 dB more — a large Pin jump breaks the HB warm start, which is the identical
/// mechanism that made PinSearch.Run's doubling stride fail (§0.2). Clamped on read for that reason.</para>
/// </summary>
public double ContourLadderStepDbm { get; init; } = 2.0;
```

Clamp to `[0.5, 3.0]` at the one place `ContourGrid` reads it, and persist it in `CharmIo` beside
`PinStepDbm` (write always, read `s?.ContourLadderStepDbm ?? defaults.…` — the same absent-means-default
rule every other setting there uses). Do not surface it in a dialog in this brief.

### 3.2 `ContourGrid.Build`

Replace

```csharp
var (seed, hint, neighborSteps) = NearestByVswr(converged, gamma);
var result = PinSearch.Run(ctx, working, seed, hint, neighborSteps: neighborSteps);
```

with a ladder call whose warm start is the neighbour's whole ladder, **keyed by level**:

```csharp
// R9C §3.2 — the VSWR-nearest converged neighbour's ladder becomes this point's per-LEVEL warm start.
// PinSearch.Sweep already takes exactly this shape (priorLevelSpectra, keyed by the rounded Pin level)
// for R-h9r2-19's frame-to-frame lever, so nothing new is invented here — the neighbour simply plays
// the role the previous frame plays there. It is strictly better than Run's single `seed`: every rung
// starts from a converged solution at the SAME drive level rather than from one 20+ dB away.
var result = PinSearch.Sweep(ctx, working, s.PinStartDbm, s.PinMaxDbm, ladderStepDbm,
                             warmStart: null, priorLevelSpectra: LevelSpectraOf(neighborSteps));
```

where `LevelSpectraOf` is `steps.ToDictionary(st => Math.Round(st.PavlDbm, 6), st => st.Point.V)`,
built once per neighbour rather than per point if the profile asks for it. `NearestByVswr`'s `PinHint`
return is now unused by this call site — **leave the method's shape alone** (`BuildParallel` and any
future `Run` caller still want it) and discard the hint at the call site with a one-line comment saying
a ladder has no bracket to hint.

`Sweep`'s own early stop (`CompressionDb + SweepOverdriveDb`) is what keeps the cost at the measured
736 rather than a full run to PinMax on every point; do not disable it.

### 3.3 `GridPoint.Metric` must read `SweepCompression` — or every contour value is quantised

`ContourGrid.cs:29–40` reads `Result.AtCompression`. With a ladder that is the nearest solved rung, so
every contour value would round to the 2 dB grid. Change it to prefer the interpolated reading and fall
back exactly as `HarmonicaSolver` already does:

```csharp
public double Metric(GridMetric metric)
{
    // R9C §3.3 — SweepCompression first: it is the reading AT the compression target, while
    // AtCompression is only the nearest solved ladder rung. Falling back to the step keeps a Run()
    // result (whose secant already lands on target, and whose SweepCompression is null) unchanged.
    var sc = Result.SweepCompression;
    var at = Result.AtCompression;
    if (sc is null && at is null) return double.NaN;
    return metric switch
    {
        GridMetric.PoutDbm         => sc?.PoutDbm  ?? (at!.PoutW > 0 ? 10 * Math.Log10(at.PoutW) + 30 : double.NaN),
        GridMetric.DrainEfficiency => (sc?.De  ?? at!.De)  * 100.0,
        GridMetric.Pae             => (sc?.Pae ?? at!.Pae) * 100.0,
        _ => double.NaN,
    };
}
```

`IsHole` stays `!Result.Compressed`; `Sweep` sets `AtCompression` whenever it compresses, so the
predicate is unchanged in meaning.

### 3.4 `BuildParallel` gets the identical treatment

It is not on the frame path today, but two per-point searches that disagree is exactly the drift these
briefs exist to stop — and `ContourGridParallelTests`' serial-vs-parallel hole-set gate is what would
catch it late and expensively. Change its body the same way (the deterministic leader, the angle
batching, the per-batch context pool and the "seeding is strictly within a batch" rule are all
unaffected — only the per-point call changes).

Its own recorded worst-case serial-vs-parallel PAE deviation (2.69 pts, caused by two hinted `Run`
searches converging confidently to two different wrong answers — see the refinement loop's own doc
comment at `PinSearch.cs:369–385`) should shrink or vanish, because a uniform ladder does not depend on
which neighbour seeded it. **Measure it and report the new figure; do not assume it improved.**

### 3.5 What this does NOT change

- `PinSearch.Run` stays in the tree, unmodified, and keeps its known bracket limitation. It is still
  reachable and still documented. Do not delete it and do not "fix" it here — that was explicitly the
  road not taken.
- `src/Harmonica/CLAUDE.md`'s standing warning about `Run` therefore stays true. **Do not edit it** (no
  `CLAUDE.md` edits in this brief); note in `src/Harmonica/RESOLVED.md` that the frame path no longer
  calls `Run` for grid points, so the warning now applies only to direct callers.
- The RBF fit, the raster, the support mask, `InterpolatedArgmax`, the hole disc and the hull clip are
  all untouched (§0.5).

---

## 4. The launch frame is solved at full quality

Two edits, and both are needed — either alone leaves a "fast but wrong" path reachable.

**(a)** `HarmonicaView.EnsureFirstSolve` (`:169–174`):

```csharp
// R9C §4 — the first frame goes through the SAME scheduled path every later frame does. It used to
// call RequestFrame() bare, which takes Options' own defaults — the ladder's COARSE rung — so the
// launch picture was a 25-point grid while every frame after it was 37: measured, that moved the DE
// optimum from Z = 122.579 − j0.805 to Z = 132.319 − j1.786 and carried 4 holes instead of 1, which
// is what the owner saw as "the contours change when I move L1". The saving was ~65 solves on a
// grid that measured 451 ms whole, in Debug.
_doc.ViewModel.Harmonica.RequestScheduledFrame(dragging: false);
```

**(b)** `HarmonicaSolver.Options`' `Rings`/`Spokes` defaults become `FrameScheduler.FullRings` /
`FullSpokes`, and the comment above them — *"Defaults are the coarse ring set of §6.8, so the first
frame after opening a document is fast rather than correct-and-slow"* — is rewritten to say the
opposite and why. A bare `new Options()` is used by tests and could be used by a future caller; leaving
it silently coarse re-arms the same trap under a different name.

Check `tests/Ui.Tests/Harmonica/HarmonicaSolvePoolTests.cs` and `HarmonicaFrameTierCostTests` for
assertions that depend on the old default point count and update them deliberately rather than by
whatever makes them pass.

---

## 5. Cost: re-measure, do not extrapolate

A full-quality frame's grid solve roughly triples. That is the price the owner accepted, and the reason
it is affordable is that **a drag never solves a grid at all** (`OptionsFor`: `SkipContours = dragging
|| plan.SkipContours`) — so this lands on the on-release frame and on explicit Solve Now, not on the
interactive path.

Two things must actually be checked rather than reasoned about:

1. **`FrameScheduler` sees a bigger full-frame cost** through `RecordFrame`, and may degrade the
   quality rung sooner. Run `tests/Harmonica.Tests/FrameSchedulerTests.cs` and reason about whether any
   rung threshold now trips on a frame that is legitimately more expensive. If a threshold needs
   moving, move it with a measurement beside it, in this brief's own RESOLVED.md entry.
2. **The recorded cost figures go stale.** `HarmonicaGridDragCostTests` (Category=Benchmark) records
   "full rebuild 272 HB solves / 547.8 ms; one dragged point 3 solves / 3.3 ms with 60 points reused".
   The first half changes; the single-point-reuse half should not (R-h7-12's reuse is keyed on Γ and is
   search-independent). Re-run it, update the recorded numbers in the test's own comment, and report
   both halves.

---

## 6. Gate

**New tests, in `tests/Harmonica.Tests`:**

- `ContourGridTests` — on the shipped default fixture at 3 × 12, **`HoleCount == 0`**. This is the
  owner's hole report as a gate. State in the test's comment that Γ = 0.8 (Z = 720 Ω) was the point
  that used to fail and that `Sweep` compresses there at 12.74 dBm / 32.05 dBm.
- A **direct A/B**: at Z = 720 Ω, Z = 132.319 − j1.786 and Z = 96.331 − j0.152, the grid's own per-point
  result and `PinSearch.Sweep(…, PinStepDbm)` agree on Pout at compression to within a stated tolerance
  (the 1 dB-vs-2 dB measurement above says 0.05 dB is comfortable; pin whatever you measure, with the
  number in the assertion message).
- `Metric` reads the interpolated reading: a `GridPoint` built from a ladder result whose
  `SweepCompression.PoutDbm` differs from its `AtCompression`'s own Pout returns the former.
- `ContourGridParallelTests` — the existing serial-vs-parallel gate must still pass; report the new
  worst-case deviation (§3.4).

**New tests, in `tests/Ui.Tests/Harmonica`:** §2.4's three, plus one for §4 asserting the first
scheduled frame's plan is `FrameQuality.Full` with `FullRings`/`FullSpokes`.

**Then:**

```
dotnet build
dotnet test tests/Harmonica.Tests --no-build
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
dotnet test tests/Engine.Tests --no-build
```

`Engine.Tests` is in the list because `PinSearch` reads `LoadpullEngine.ComputeFoms` and this brief
changes which steps are measured; it is ~3.5 minutes and it is worth it once, here.

Benchmarks that must be re-run and whose recorded numbers must be updated (opt in with
`--settings circuitrf.benchmark.runsettings`): `HarmonicaGridDragCostTests`,
`HarmonicaFrameTierCostTests`, `ContourGridParallelTests`, `LoadpullHoleDiagnosticTests` — that last
one's whole subject is the hole set, and its own §3.1 diagnosis (holes come from the bracket's first
hint-driven jump) is confirmed and closed by this brief.

Write the outcome to `src/Ui/RESOLVED.md` and `src/Harmonica/RESOLVED.md`, including §0's measurement
tables verbatim — they are the evidence, and a later reader must not have to re-derive them.
**No `CLAUDE.md` edits.**
