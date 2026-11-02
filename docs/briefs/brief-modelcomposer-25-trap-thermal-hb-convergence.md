# Brief MC-25 — Harmonic balance stalls when a trap node and the thermal node are both present

**Series:** `brief-modelcomposer-0-overview.md` · **Tag:** `R-mc25-<m>` · **Depends on:** MC-9, MC-10, MC-21 (the switches
are the diagnostic) · **Read with:** `src/Engine/HarmonicBalance/CLAUDE.md`, `brief-hb-p3-convergence-line-search-and-multitone-warm-start.md`,
`docs/design/model-composer.md` §24 (the measurements below), `src/Core/RESOLVED.md` (MC-22 entry)
**Area:** `src/Engine/HarmonicBalance/` (`HbNewton`, `HbDriveRamp`, `HbEngine`), `src/Core/Devices/Composed/`
(`ComposedCoreModel`, `Traps/`), `src/Engine/ParametricSweepEngine.cs`; `tests/Ui.Tests/Examples/ExampleWorkspaceLoadsTests.cs`
**Tests:** `tests/Engine.Tests`, `tests/Ui.Tests` (targeted classes only)

---

## 1. Goal

The shipped Model Composer example's power-sweep bench (`examples/Model Composer/GanPowerSweep`, an 8-finger instance of
`synthetic-gan/gan_4x125.cmodel`, 2 GHz, Pin −4 to 22 dBm) **does not converge at Pin 14 and 16 to 21 dBm**, with no
instance switch set. Each failed rung burns 100 Newton iterations and ~600 line-search halvings, then the drive-ramp
fallback adds ~58 more steps: about 13 s per rung, against 0.01–0.05 s for a point that converges. Nobody noticed,
because `TheGanBenches_Run` reads `AnalysisConvergence.Converged`, which a parametric sweep never sets to false.

Make the bench converge at every rung to 22 dBm, quickly, and make the example's gate able to see it when it does not.

## 2. What is already known (measured during MC-22, 2026-10-20)

One cold HB point at Pin 18 dBm on that bench, `HbEngine.Run`, default settings:

| Instance | Iterations (first step) | Backtracks | Result |
|---|---|---|---|
| as fitted (drain-lag trap node `phi2` + thermal node `t`) | 100 | 596 | **fails**, then 58 ramp steps, 12.7 s |
| `DrainLag=false` (no trap node) | 16 | 7 | converges, 0.02 s |
| `SelfHeating=false` (trap node kept) | 23 | 22 | converges, 0.04 s |
| `DrainLag=false SelfHeating=false` | 14 | 6 | converges, 0.05 s |
| `Charge=false` | 80 | 274 | converges, 0.36 s |

- **The stall needs both slow state nodes.** Either one alone converges close to quadratically.
- **The device Jacobian is right.** Over 4,000 random points (vgs −6 to 1.5 V, vds −1 to 26 V, `phi2` 0 to 20 V, ΔT 0
  to 60 K), `dg`/`dc` match a central difference of `i`/`q` wherever the difference agrees at two step sizes (1e-6 and
  1e-8 relative). The only residual mismatches are ~0.7 % in `dg[0,1]` near vds = 26 V, far outside the data. The large
  mismatches seen on a regular grid were all kinks of the guided extrapolation at polygon vertices: real
  discontinuities in the gradient, allowed by MC-2, and not where these waveforms go.
- **The waveform does not reach anything exotic.** At 15–18 dBm the intrinsic vgs peaks at +0.86 V (the currents' hull
  reaches +0.848 V) and vds stays between 0.1 and 14 V, in the converging cases too.
- **Converging up to 14 dBm cold is not the same as converging in the sweep.** Pin 14 converges cold in 16 iterations
  but failed in the warm-started sweep.
- At the stall ‖F‖ sits at 1e-3 to 1.7e3 "A", with the step fraction down to 1/256. A residual that large points at a
  state-node row, not a circuit node.

**Hypotheses, not verified:**
1. Row scaling. ‖F‖₂ mixes amps at circuit nodes with the trap node's KCL (its `i = rate·…` in the trap model's units,
   on a 1 mF node) and the thermal port's `−P` (watts as amps, into `Rth ∥ Cth`). One badly scaled row dominates the
   norm, and the backtracking line search (HB-P3) cannot find a decrease in it.
2. Stiffness of the coupled DC balance. The CaptureEmission capture rate goes as `exp((vds − φ)/Vcap)` with
   `Vcap` = 0.05 V, continued by its tangent above e^20. So the trap's DC balance is set by the waveform's PEAK, and
   temperature feeds both that rate and `Id`. A full Newton step on `φ` and `ΔT` together overshoots by volts.
3. The warm start in a sweep carries the previous rung's `φ`/`ΔT` DC into a drive where they are far from equilibrium.

## 3. Requirements

**R-mc25-1 — Find the cause before fixing anything.** Add to the HB convergence trace (or a diagnostic run of it) the
row of `F` that carries the largest share of ‖F‖ at each iteration, by node name. Run Pin 18 dBm as fitted and record:
which rows dominate at the stall, whether `phi2` and `t` move by volts per step, and whether the line search is rejecting
on the norm of a row Newton barely changes. Confirm or rule out each hypothesis in §2 with that record, and write it
into `src/Engine/RESOLVED.md` before any fix lands.

**R-mc25-2 — Fix it in the engine or the model, wherever R-mc25-1 points.** Candidates, in order of preference:
- **Scale the residual the line search judges by**, per row: by each row's own Jacobian diagonal, or by a natural unit
  per state-node kind. This changes no converged answer, only how the step is accepted.
- **Limit the state-node update per Newton step**, as a junction voltage is limited. For example, cap `Δφ` at a few
  `Vcap` and `ΔT` at a few kelvin per step, decided by the node's kind and never by its name.
- **Warm-start a sweep's state nodes at the new drive's equilibrium**, not the last rung's.

Whatever is chosen, it must not change any converged result. Hold that with a gate: every point that converges today
gives the same answer to 1e-9 relative.

**R-mc25-3 — The bench converges.** The GanPowerSweep netlist (`ModelComposerExampleAuthoring.PowerSweepNetlist`)
converges at every rung from −4 to 22 dBm with no switch set, and with each of `LinearCgs`, `LinearCdg`, `LinearCds` and
`LinearCm`. MC-22's `BranchLinearChargesGanTests.ThePowerSweep_ConvergesAtEveryRung_ForEachSingleBranch` currently stops
at 13 dBm for this reason. Move it back to 22 dBm and delete the sentence in its comment that explains the 13.

**R-mc25-4 — A sweep's convergence is visible.** A parametric sweep whose inner HB failed at any point must say so where
callers look. Either `AnalysisConvergence.Converged` is false (or a per-point count is carried), or the run adds a
warning naming the points. `TheGanBenches_Run` then reads it and fails on a non-converged rung. Check every other caller
of `AnalysisConvergence` for an assumption that null means converged.

**R-mc25-5 — The sweep path's own cost.** With every rung converging (Pin −4 to 13 dBm), five sweeps of 18 rungs took
9 minutes in MC-22's gate, while a cold single point costs 0.01–0.2 s. Measure where a converging rung's time goes in
`CircuitEvaluation` → `ParametricSweepEngine` → `HbEngine`. Candidates: re-elaboration, the bias-point DC solve
(`BiasFreeze`), the warm start taking many more iterations than a cold start, measurement evaluation. Fix what is
avoidable; record the rest in `src/Engine/RESOLVED.md`.

## 4. Not in this phase
Changing the guided extrapolation's continuity at polygon vertices (MC-2 allows the gradient to jump there).
Re-fitting the example model.

## 5. Gates
- R-mc25-1's record, in `src/Engine/RESOLVED.md`.
- The bench as fitted converges at Pin 18 dBm cold in under 30 iterations, and at every rung −4 to 22 dBm in the sweep.
- The converged-answer-unchanged gate (R-mc25-2) on the existing HB composed-FET and hero gates that touch state nodes:
  run the classes, not the suite.
- `ExampleWorkspaceLoadsTests.TheGanBenches_Run` fails when a rung does not converge. Show it once against an
  injected failure.
- MC-22's power-sweep gate back to 22 dBm, passing, its runtime recorded.
- R-mc25-5: the per-rung time breakdown, before and after.
