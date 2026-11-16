# Brief — harmonic balance of a control-current loop from a cold start

**Tag:** `R-hbcl-<n>` · **Area:** `src/Engine/HarmonicBalance/HbNewton.cs` (the control-current Jacobian `J_cc`, `RunDevicePass`'s
two-pass `_c_ref`), `src/Engine/HarmonicBalance/HbLinearExtractor.cs` (`ExtractDC`, `ForcedDc`, `ControlSensitivityRow`,
`SolveFullNetwork`), `src/Engine/HarmonicBalance/HbEngine.cs` (the DC seed), `src/Engine/RESOLVED.md`.

## The gap

An SDD that reads another part's current (`C[n]`) inside a feedback loop solves in DC and does not solve in harmonic balance
started from that DC point. The case is the *Symbolically Defined Device (SDD)* example's bias loop: an SDD at node `vg` that
senses the drain supply's current through an IProbe and holds `vg` at `VGS0 − Vspan·tanh(Kloop·(Idd − Iset)/Vspan)`, feeding
a GaN FET's gate through a 100 nH choke. DC converges at every loop gain. Harmonic balance at 2 GHz and 10 dBm drive:

| Loop gain `Kloop` (V/A) | Cold start (from the DC seed) | Warm start (from the solution at `Kloop` = 1) |
|---|---|---|
| 0 | converges | — |
| 50 | does not converge in 100 iterations | — |
| 200 | does not converge (‖F‖ 4.4 to 20 over a drive sweep) | converges: Pout 21.15 dBm, Idd 62.9 mA, Vgs −3.29 V |

A 10 kΩ bleed resistor from `vg` to ground (so the gate group has a DC path in the linear network) changes nothing. The
non-converged points report numbers (Pout 12.6 dBm with the gate at −3.08 V) that a reader cannot tell from a solution except
by the `Converged` cube. The loop on a low-pass weight (`I[1,2]` with `H[2] = 1/(1 + j2πf·1 µs)`, so it acts at DC only) fails
the same way as the loop on `w = 0`.

What is already established (`src/Engine/RESOLVED.md`, the SDD example's entry):

- Two DC defects on this circuit were fixed first: HB decoupled `vg` and the gate (an inductor between two interface nodes; now
  `RankDeficientAt`, and `ForcedDc` when the pair has no DC path but gmin), and DC lacked a weighted term's `JacCtrl`. With both
  fixed, HB's DC harmonic matches the DC analysis at `Kloop` = 0, and the warm-started solution at 200 is physical.
- So a solution exists and Newton reaches it from nearby; from the DC seed it does not. The candidates, in the order to check:
  1. **`J_cc` at the DC harmonic.** `ControlSensitivityRow(ω = 0, branch)` back-solves on the DC factorization `ExtractDC` built.
     When `ForcedDc` is engaged, Y_NN(0) comes from the held-node system while `ControlSensitivityRow` and `SolveFullNetwork` still
     read the regularized factorization whose Z_NN(0) was singular: the control current's sensitivity and the back-solved
     `_c_ref` may be inconsistent with the Y the residual uses. The bleed-resistor run (no `ForcedDc`) also fails, so this alone
     may not be the cause.
  2. **The one-step `_c_ref` seed** (harmonic-balance CLAUDE.md, the J_cc section): `_c_ref` lags by an iterate, and the note
     records only superlinear convergence for strong coupling (β ≈ 0.8). This loop's gain is 16 at `Kloop` = 200: the lag may be
     outside what the scheme converges from.
  3. **The DC seed itself**: HB starts from the DC point, which is the loop's own solution, so the seed is good at DC; check
     whether HB's first Newton step moves the DC harmonic away from it (the RF drive raises the average drain current, which the
     loop then answers through a gain of 16).
- **The FD oracle**, `HbNewton.CompareJacobianNumerical` with `cc` supports the control-current Jacobian (≤ 1e-5 on the existing gate
  tests). Running it on this circuit at the DC seed and at the warm solution says directly whether `J_cc` is right here.

## Requirements

**R-hbcl-1 — Find the cause.** Run the FD oracle on the bias-loop circuit (its netlist is the SDD example's bench 6 with an HB
analysis; `tests/Engine.Tests/SddWeightedControlDcTests.cs` has a smaller loop of the same shape) at the DC seed and at the
warm-started solution. Record which Jacobian block disagrees, or that none does, in `src/Engine/RESOLVED.md`.

**R-hbcl-2 — Converge from cold.** Harmonic balance of a control-current loop converges from its DC seed wherever DC does, at
loop gains to at least 1,000 V/A on the bias loop, at 0 to 30 dBm drive, without the user sweeping the gain. If the fix is a
continuation (ramping the control coupling the way drive stepping ramps the drive), it engages only when the plain Newton fails
(`IfNecessary`), says so in the run's notes, and leaves every circuit that converges today bit-identical.

**R-hbcl-3 — A failed point is not a result.** Until R-hbcl-2 holds everywhere, a non-converged HB point inside a sweep is
reported as such in the run's notes with its residual (it already is) and its cubes are NaN in the measurements a display reads,
rather than the last iterate's numbers. Check this against what the loadpull engine does for its non-converged points, and do
the same.

**R-hbcl-4 — The example.** The SDD example's bench 6 gains an HB drive sweep (10 to 30 dBm at the bench-5 MXP load) with the
loop open and closed, and its README's "What the bench leaves out" paragraph is replaced by what that sweep shows (the loop
pushing the gate negative as drive raises the average current). The README's line telling the user to sweep `Kloop` up from 0
is removed.

## Gates

1. **The FD oracle** agrees with the analytic Jacobian on the bias-loop circuit within the existing tolerance (1e-5 relative),
   at the DC seed and at the solution.
2. **Cold start.** The bias loop at `Kloop` = 50, 200 and 1,000, Pin 10, 20 and 30 dBm, converges from cold, and equals the
   warm-started solution to the HB tolerance.
3. **Nothing else moves.** The HB, DC, SDD, loadpull and hero tests (1,007 at the time of writing, filter
   `HarmonicBalance|Sdd|NonlinearDc|Loadpull|Hero|Dc`) pass unchanged; `SddControlCurrentHbJacobianTests` still holds its 1e-5.
4. **R-hbcl-3**: a sweep with a forced non-convergence (MaxIter=1) reports NaN in its measurements at that point.

## Not here

A slow (sampled or held) bias controller model as a component; transient analysis of the loop's settling.

## Completion note (2026-11-16)

Built as above.

- **R-hbcl-1, the cause:** no Jacobian block disagrees. The FD oracle's relative metric reported 4e-2 at the seed and 2.6e-2 at
  the solution in the bias node's DC row, and that figure grows as 1/ε: roundoff in a row carrying `ForcedDc`'s ~1e6 S. Scaled by
  its own row (`MaxRowScaledError`, new) the Jacobian agrees to 4e-7 and 5e-7, and reads 1.0 with `J_cc` off. The cause was the
  line search judging ‖F‖ in amperes: the stiff bias row (1 mΩ times the loop gain) vetoed every step from the DC seed.
- **R-hbcl-2:** a control-current solve whose line search crawls (5 iterations running at λ ≤ 1/32) is solved again from the same
  start judging either norm, with a run note; if that fails, the usual solve runs whole as before. Cold starts at Kloop 50, 200
  and 1,000 and 10, 20 and 30 dBm converge in 10 to 21 iterations and equal the warm-started solution to 2.1e-5 V. Circuits
  without control currents are untouched; a control-current circuit that converges today changes only if its line search crawls
  for 5 iterations running (the bias loop at 30 dBm did: 45 to 80 iterations before, 17 to 21 now, the same answer). It is not a
  continuation, so nothing ramps.
- **R-hbcl-3:** an unconverged HB point in a sweep keeps `Converged` 0 and `Residual` and reads NaN in every other cube, so its
  measurements are NaN; a run note says so.
- **R-hbcl-4:** bench 6 gains `HB1`, `DRIVE` (Pin 10 to 30 dBm) and `LOOPHB` (Kloop 0, 200), its 50 Ω load replaced by a tuner
  presenting bench 5's maximum-power load (`SddExampleAuthoring.Bench6` changed with it); the README's paragraph now tables the
  sweep and the line telling the user to sweep `Kloop` up from 0 is gone. 22 points, 1.2 s.
- **Gates:** `Engine.Tests/HarmonicBalance/HbControlLoopColdStartTests` (5), `Ui.Tests/Examples/SddExampleTests` (8, one new);
  gate 3's filter, 1,013 tests, passes after one change: `HbLinearNodeTests.T5_Sweep_FullNodes` read numbers at a point refused as
  a vacuous tolerance, which R-hbcl-3 makes NaN on purpose; it now checks the converged point. Sweep, measurement, transient and
  oscillator Engine tests (262), and the Harmonica and Ui HB classes that read `Converged` (112), pass. `Firewall.Tests` fails two
  user-facing-text checks on transient, envelope and expression files this brief did not touch.
- Docs: simulations (Parametric Sweep: an unconverged HB point), sdd (control-current loops), the example page; findings in
  `src/Engine/RESOLVED.md`.
