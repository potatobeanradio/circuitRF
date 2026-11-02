# Sonnet Brief — HB-P3: a Newton that does not fail at 16 dBm, and warm starts for every tone count

**Design:** `docs/design/harmonic-balance.md` §11 (continuation — the `DriveStepping` knob and the
step-backoff it promises), §11.1 (the single-tone warm start, as built), §12.2 (the convergence
test), §16 item 4 (damping policy — "settle empirically"; this brief settles it). **Code:**
`src/Engine/HarmonicBalance/HbNewton.cs` (`Solve`, `ApplyUpdate`), `HbNewton2D.cs`, `HbNewtonNd.cs`,
`HbEngine.cs` (`Run`, `RunTwoTone`, `RunMultiTone`, `Resolve`), `src/Engine/ParametricSweepEngine.cs`
(`RunInner`, the seed chain), `src/Engine/Loadpull/DriveLadder.cs` (the drive ladder loadpull already
has — read it; do not duplicate it).

**One sentence:** the HB Newton is undamped and the `DriveStepping` directive is parsed and then
ignored, so a cold solve of the shipped Hero 2 at 16 or 20 dBm runs to `MaxIter=100` and returns
non-converged; add a backtracking line search on ‖F‖ to all three Newton loops, make `DriveStepping`
do what the design and the `.cnl` grammar say it does, and extend the previous-point warm start —
which is what makes the single-tone sweep survive today — to the two-tone and n-tone paths, which
cold-start every point.

**Why (HB performance review, 2026-08-29).** Measured, Release build:

| `hero2_convergence.cnl`, cold `RunSinglePoint` (DC seed, λ=1) | iterations | converged |
|---|---|---|
| Pavl = 0 dBm | 3 | yes |
| Pavl = 10 dBm | 4 | yes |
| **Pavl = 16 dBm** | **100** | **no** |
| **Pavl = 20 dBm** | **100** | **no** |

The 21-point Pin sweep costs 1.5 ms/point warm and **7.6 ms/point cold** — the cold figure is the
high-Pin points burning 100 iterations each, not a slower solve. The same failure is one `type=hb`
directive away from any user: a single point at a compressed drive, or a two-tone sweep (every point
cold, `RunTwoTone` takes no seed), or a 3+-tone sweep (same). `HbEngine.Resolve` reads
`DriveStepping=` into `HbAnalysisParams.DriveStepping` (`HbEngine.cs:197–202`) and nothing
downstream reads it: the knob the design reserved "for Phase-4 bring-up" was never wired.

**Structural facts.**

1. **The Jacobian is exact and the fixed point is right; the failure is the step, not the
   direction.** `CompareJacobianNumerical` gates `BuildJ` at 1e-5 on this very fixture at Pin=18 "near
   failing" (Hero2Tests). Warm from 1 dB below, the 16 dBm point converges in 2–3 iterations. Full
   Newton steps from the DC seed overshoot into a region where the SDD's `tanh`/exp terms saturate and
   the iterate wanders. A line search that accepts a step only if ‖F‖ decreases (Armijo on ‖F‖², with
   halving) is the standard remedy and costs one `EvaluateNonlinear` + `BuildF` per trial (~150 µs
   single-tone, ~4 ms two-tone) — far cheaper than an extra Newton iteration (which also builds and
   factors J) and incomparably cheaper than 97 wasted ones.
2. **`Lambda` (B2) is a *fixed* damping factor** applied every iteration. It stays as the user's
   override: when `Lambda < 1` the line search starts from `λ = Lambda` instead of 1. Default
   behaviour (`Lambda = 1`) becomes "full step, backtrack only if ‖F‖ does not decrease" — a
   converging solve takes **exactly the same steps as today**, so goldens do not move.
3. **The loadpull engine solved this for itself with `DriveLadder`** (a uniform-step Pin ramp with
   warm start along the rung, `src/Engine/Loadpull/DriveLadder.cs`), after its own version of this
   failure (the 23 dB bracket jump; see the loadpull RESOLVED notes). `HbEngine.Run`'s
   `DriveStepping` fallback should **reuse `DriveLadder`**, not re-implement a ramp: on a cold
   non-convergence with `IfNecessary`, ladder the tone source's drive from a small-signal level up to
   the requested one in fixed dB steps, warm-starting each rung, and report the rung count in the
   convergence trace. `Always` ramps unconditionally; `Never` reports the failure as today.
4. **The warm-start seed is a `Complex[N, K+1]`; the two-tone and n-tone seeds are `Complex[N, M]`.**
   `ParametricSweepEngine.RunInner` returns the single-tone `InterfaceV` through an `out` parameter
   and chains it along the innermost axis only, resetting on non-convergence and on a dimension
   change. `RunTwoTone`/`RunMultiTone` build their initial guess from `NonlinearDcEngine.Run` +
   `InitialGuess2D` every call. Accepting a seed of the right shape is the same five lines
   `Run` has (`HbEngine.cs:315–332`); `HbRunResult.InterfaceV` already exists and is null for those
   paths today. The sweep engine's chain is shape-agnostic once the dimension check compares against
   `M` instead of `K+1`.
5. **A warm start must never propagate a wrong branch.** Design §11.1's rule — reset the chain on any
   non-converged point — is kept for all tone counts. With the line search in, "non-converged" should
   become rare; the reset is the belt to the line search's braces.
6. **Tolerance is absolute on ‖F‖ in amperes** (design §12.2). The line search compares ‖F‖ before
   and after a step; it does not need, and must not introduce, a relative criterion. §12.2's
   "relative" and "normalize-to-drive" alternatives stay unbuilt.

**Sequencing.** M1 line search in all three Newton loops (fixes the failure). M2 `DriveStepping`
via `DriveLadder` (the documented fallback, for the cases M1 does not catch). M3 two-tone / n-tone
warm start through the sweep engine.

---

## 1. M1 — backtracking line search

In `HbNewton.Solve`, after `dV` is obtained:

```
f0 = ‖F‖   (already computed for the convergence test)
λ  = lambda                         // the user's fixed factor, default 1
for trial = 0 .. MaxBacktracks-1:
    V_trial = V + λ·dV
    F_trial = BuildF(EvaluateNonlinear(V_trial …))
    if ‖F_trial‖ ≤ (1 − c·λ)·f0  with c = 1e-4:   accept: V = V_trial; carry (iNl, qNl, G, C,
                                                    buckets) forward so the NEXT iteration does not
                                                    re-evaluate at the same V
    else λ /= 2
if no trial accepted: take the smallest λ (do not stall) and record a "stalled" flag in the trace
```

- `MaxBacktracks` = 8 (λ down to 1/256), a constant with a comment; not a user setting.
- **Reuse the accepted evaluation.** Today's loop evaluates at the top of every iteration; with the
  line search the accepted trial's spectra are exactly that evaluation. Restructure so
  `EvaluateNonlinear` runs once per accepted step plus once per rejected trial — never twice at one
  `V`. Test 4 counts this.
- `HbConvergenceTrace.IterRecord` gains `Lambda` and `Backtracks` so the trace (and
  `HbConsoleDiagnostics`) shows what happened.
- **Identical transcription into `HbNewton2D.Solve` and `HbNewtonNd.Solve`.** Three copies of one
  15-line loop is acceptable here (the three loops are already deliberately parallel and the
  goldens on each are frozen); a shared helper is fine if it takes delegates for evaluate/BuildF and
  does not change the byte-identity of the converging case.
- The control-current path (`cc != null`, the two-pass `_c_ref`) is unaffected: the trial evaluation
  goes through the same `EvaluateNonlinear` with `iNlLast` as its seed.

## 2. M2 — `DriveStepping` does something

In `HbEngine.Run` (single-tone; the two-tone and n-tone twins after M3, since a ramp is a warm-start
chain):

- Locate the tone source(s) that set the drive at `p.ToneHz` (`P1ToneModel`/`ToneSourceModel`;
  `SetSourceDrive` is how the loadpull engine mutates them — use the same door).
- `IfNecessary`: run the cold solve; if it does not converge, ramp with `DriveLadder` from
  `min(requested − 20 dB, small-signal)` to the requested drive in the ladder's step (loadpull's
  uniform 2 dB was measured to give zero holes — start there), warm-starting each rung from the
  previous, and take the final rung's `V` as the point's answer. Every rung goes in the
  `HbConvergenceTrace` as a `StepRecord` (the trace already has a step notion — that is what it was
  built for).
- `Always`: ramp without trying cold. `Never`: today's behaviour.
- Restore the source's drive to the requested value in a `finally` — a thrown exception mid-ramp must
  not leave the netlist at a rung.
- Under `ParametricSweepEngine` a warm-started point that fails also falls to the ramp (the chain then
  resets per fact 5).

## 3. M3 — warm start for two-tone and n-tone

- `HbEngine.Run(p, warmStart)` passes `warmStart` into `RunTwoTone(p, warmStart)` and
  `RunMultiTone(p, warmStart)`; each uses it as the Newton guess and **skips the DC seed** when its
  shape is `[N, M]` for its own `M`, exactly mirroring the single-tone branch. Both populate
  `HbRunResult.InterfaceV` and `Converged`.
- `ParametricSweepEngine.RunInner`: the dimension check becomes "same rank and same lengths as the
  current run's `[N, M]`", so the chain works for any tone count. The innermost-axis-only rule and
  the reset-on-non-convergence rule are unchanged.
- `AnalysisSettings.HbSweepWarmStart` governs all tone counts; its doc comment loses the "two-tone
  unchanged (cold)" sentence.

## 4. Tests

`tests/Engine.Tests/HarmonicBalance/HbLineSearchTests.cs` (add):

1. **The 16/20 dBm cold points converge.** `hero2_convergence.cnl` with `Pavl_dbm` overridden to
   16 and 20: cold `RunSinglePoint` converges in ≤ 15 iterations; record the iteration count and the
   number of backtracks in the test output. The converged `V` equals the warm-chained sweep's value at
   that point to 1e-6 (same root, not a spurious one).
2. **A converging solve takes the same steps.** For every point of the Hero-2 warm sweep and the
   Hero-4/Hero-5 goldens, `Backtracks == 0` on every iteration and the interface `V` is
   **bit-identical** to the pre-brief engine (keep the pre-brief `Solve` in the test project as the
   oracle for this one test, or compare against the committed golden CSVs).
3. **Two-tone and n-tone line search.** Cold `RunTwoTone` on `hero5.cnl` with `Pavl_dbm` raised
   until the pre-brief loop fails (find the level in the test, record it): converges with the line
   search. Same for `hero5_3tone.cnl`.
4. **No double evaluation.** Counter on `EvaluateNonlinear`: over a converging solve with zero
   backtracks it equals `iterations` (the final convergence check's evaluation is the last accepted
   step's), never `2·iterations`.
5. **`Lambda < 1` is honoured** as the starting λ (trace shows `Lambda == p.Lambda` on accepted
   steps with no backtracks).

`tests/Engine.Tests/HarmonicBalance/HbDriveSteppingTests.cs` (add):

6. **`IfNecessary` ramps only on failure.** A fixture whose cold solve fails even with the line
   search (construct one: raise `HbMaxIter` low, or a stiffer termination — record what was needed);
   the trace shows rungs, the final `V` matches a manual `DriveLadder` warm chain to 1e-9, and the
   source drive is back at the requested value afterwards.
7. **`Always` ramps** on a fixture that would converge cold (rung count ≥ 2); **`Never`** reports
   non-convergence with the warning text unchanged from today.
8. **Exception mid-ramp restores the drive** (inject a throwing model at rung 2).

`tests/Engine.Tests/HarmonicBalance/HbPinSweepWarmStartBenchTests.cs` (extend):

9. **Two-tone Pin sweep warm vs cold**: DC solves 1 vs N, total Newton iterations warm < cold,
   interface spectrum agreement to 1e-6 per product (the two-tone analogue of the existing single-tone
   test, through `ParametricSweepEngine.Run`).
10. **n-tone (3 tones) Pin sweep**, same assertions.
11. **The chain resets on a non-converged point** for two-tone (force one with `MaxIter=1` at one
    point; the next point's DC-solve counter ticks).

## 5. Gates

```
dotnet build
dotnet test tests/Engine.Tests --no-build
dotnet test tests/Ui.Tests --no-build        # harmonicaRF's frame scheduler reads the convergence trace
dotnet test tests/Firewall.Tests --no-build
```

Run each ONCE; read the TRX. Grep the diff for vendor or product names before finishing. Every
existing Hero golden must stay byte-identical (test 2 is the guard).

## 6. On completion

Findings — the iteration/backtrack counts at 16 and 20 dBm, the two-tone drive level at which the
undamped loop first failed, the fixture that still needed the ramp after the line search, before/after
per-point time of the cold Hero-2 sweep and of a two-tone sweep — to **`src/Engine/RESOLVED.md`
§HB-P3**. Rewrite design §11's `DriveStepping` paragraph and §16 item 4 as *as built*; drop "Two-tone
unchanged (cold)" from §11.1 and from the `HbSweepWarmStart` doc comment. **Never to any
`CLAUDE.md`.** Do not commit; the owner commits.

## 7. Out of scope, deliberately

- Relative / drive-normalised convergence criteria (§12.2's alternatives) — unbuilt, still unbuilt.
- A tapered guard-harmonic profile (§12.1) — untouched.
- Changing `HbMaxIter`'s default, or the 1e-6 tolerance.
- Replacing `DriveLadder` or touching the loadpull/pursuit engines' own ladders beyond reusing the
  class.
- Any change to the Jacobian, the solve (HB-P1), the extractor (HB-P2) or device evaluation (HB-P4).
