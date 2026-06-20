# Phase 4a Follow-up — Fix DC-in-Newton; Self-Generate Hero 2 Regression Golden (Claude Code / Sonnet)

**Context:** Phase 4a's HB engine converges, but (a) it disagrees with the external golden data, which
the owner has decided NOT to trust (painful to produce, unverifiable internals, a physically suspect
fundamental), and (b) a review of `HbNewton.cs` suggests a real bug in how the DC component is handled.
Fix the bug first, then self-generate trustworthy regression golden data from circuitRF. **Keep the
diagnostics-over-grinding discipline from the Phase-4a brief.**

## Task 1 — FIX: the DC (k=0) component must be solved IN the full HB Newton, not frozen
**Suspected bug:** `HbNewton.cs` appears to reuse the nonlinear-DC solve's k=0 solution as a FIXED DC
component throughout the full HB solve, updating only the k≥1 harmonics in the Newton iteration. This
is physically wrong.

**Correct behavior:** the nonlinear-DC solve provides only the **initial guess** for the k=0 component
(and the bias seed). The full HB Newton then solves **all harmonics INCLUDING k=0 simultaneously**, as
coupled unknowns. The DC component is a full participant in the error function `F(V)` and in the
conversion-matrix Jacobian, coupling to the harmonics through the mixing terms.

**Why (physics — do not skip):** nonlinear devices mix harmonics down to DC (even-order products:
`cos²(ωt) = ½ + ½cos2ωt` — the `½` is a drive-dependent DC shift). So the converged DC operating point
**changes with RF drive level** (self-biasing / bias shift — the same mechanism as a rectifier, and why
a class-AB PA's drain current rises with drive). Freezing DC:
- suppresses self-biasing (wrong for any device with even-order nonlinearity),
- forces the down-mixing energy into a wrong solution, contaminating the harmonics too (not a localized
  error),
- makes Pdc/PAE wrong (they read the DC component, measurements §5) — fatal for a PA simulator.
This is even more important for multi-tone (Phase 4c).

**This matches the design** — harmonic-balance.md §10 (nonlinear DC is the *initial guess*) and §4 (the
error function and conversion-matrix Jacobian include the k=0 row and the DC↔harmonic coupling blocks,
per the Maas reference). The implementation deviated; bring it in line with the note. Confirm the
conversion-matrix Jacobian includes the DC row/column and its coupling to the harmonics.

**Sanity check after the fix:** the converged DC drain current should now **move with Pin** (rise with
drive for this class-AB bias) instead of staying pinned at the zero-drive value. Report the DC drain
voltage/current vs Pin so the owner can confirm it shifts.

## Task 2 — Self-generate the Hero 2 regression golden data (AFTER Task 1)
Once Task 1 is fixed and Hero 2 re-converges with the physics sanity checks holding:
- Run `hero2.cnl` over **Pin = −20 to 0 dBm in 1 dB steps (21 points)**, MaxHarm=4.
- Export the converged **node voltages AND branch currents** at `n_drain` and `n_gate`, per harmonic
  (DC + 4 harmonics), per power point, as CSV (real & imag) — same column style as the existing files
  (`hbfrequency; Pave; r <node>.Vb; i <node>.Vb`, plus current columns). Place in `testdata/Hero2/`.
- **Label these files clearly as SELF-GENERATED REGRESSION references** (a header comment or a README in
  the folder): they are circuitRF's own frozen output for catching future regressions — NOT
  independently validated cross-checks. A future independent validation (hand-computed single-tone case,
  or another tool) is still owed. Do not present them as proof of correctness.
- The OLD external-reference files (`hero2_golden_reference_n_*.csv`) are NOT trusted — either delete them or
  clearly mark them deprecated/superseded so they're never used as the gate.

## Task 3 — Wire the regression golden into the test suite
- Add a Hero 2 regression test that runs `hero2.cnl` and compares against the self-generated golden
  (voltages and currents), with the owner's tolerance rule: components with magnitude **< 1e-5 (real or
  imag) are numerical noise → pass-by-default**; compare only signal-bearing bins. This test runs in CI
  going forward — it's the tripwire that flags if a future phase breaks the HB engine.
- Preserve the physics anchors as explicit assertions where cheap: DC `n_gate = −3.05 V` and (zero-drive)
  DC `n_drain` near 48 V; gate harmonics above the fundamental ≈ 0 (linear input); and now, DC drain
  current shifts with Pin (Task 1).

## Acceptance
1. The DC component is solved within the full HB Newton (all harmonics incl. k=0 coupled); DC drain
   current demonstrably moves with Pin. (Task 1 — report the trend.)
2. Self-generated regression golden exists in `testdata/Hero2/`, labeled as such, V and I, −20..0 dBm.
3. A CI regression test compares against it with the <1e-5-is-noise rule; physics anchors asserted.
4. `dotnet build`/`dotnet test` green; Phases 1–3 and the rest of 4a still pass.

## Guardrails
- **Fix Task 1 BEFORE generating golden** — golden data generated with the DC bug would freeze the bug
  into the regression reference.
- Diagnostics over deep convergence grinding (Phase-4a brief discipline). If the DC-in-Newton fix
  introduces convergence trouble, report the convergence trace and symptom rather than launching a large
  investigation — small fixes OK, large re-architecture → flag for the owner.
- The conversion-matrix Jacobian's DC coupling is per harmonic-balance.md §4 / the Maas reference — use
  the note, don't improvise the DC block.
- Update `src/Engine/HarmonicBalance/CLAUDE.md` to record that DC is a full Newton participant (initial
  guess from nonlinear-DC, then solved coupled).