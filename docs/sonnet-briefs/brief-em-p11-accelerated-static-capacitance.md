# Brief P11 — the calibration standards' static capacitance without a dense m×m solve

**Problem.** `PlanarDeembed.StaticCapacitance` solves `P q = ε₀·1` on each of two calibration
standards with a dense m×m complex LU (`src/Engine/Mom/PlanarDeembed.cs:213–236`). It is the one
step that stays dense when the DUT is accelerated, and it refuses wide-port runs whose DUT would have
succeeded (`brief-em-deembed-ceiling-closeout.md`; the owner's taper's wide-port standard meshed at
N = 6,466). `P` is exactly the scalar-block operator with the static kernel
`PlanarKernelTerms.StaticScalar` — the same cell-pulse potential AIM already projects for the charge.

Read first: `PlanarDeembed.cs`; `PlanarAim.cs` after P6 (the per-mesh geometry, the charge stencil);
`PlanarFill.ScalarPotentialMatrix`; `PlanarStaticLimitTests` (the ω → 0 capacitance oracle);
`EmDeembedCeilingTests`.

## Milestones

1. **A cell-pulse AIM operator**: reuse P6's `PlanarAimGeometry` restricted to the charge
   projection over CELLS (a pulse per cell rather than the ± pair per basis — the stencil is the
   same `Moments` call with `sign = +1` on one cell), one grid kernel (`StaticScalar`), the near set
   over cells, exact near entries from the scalar primitives, sparse LU preconditioner, GMRES.
   Tolerance: the capacitance must be right to 1e-6 relative — `C_pul` differences two totals that
   agree to several digits, so the solve tolerance is tighter than the DUT's; measure what GMRES
   tolerance delivers 1e-6 on the differenced result and set it from the measurement.
2. **Gate against the dense solve**: `CapacitancePerMetre` on the hero's standards to 1e-6 relative,
   and every `PlanarStaticLimitTests` capacitance oracle through the accelerated path.
3. **Wire it**: when the run is accelerated (`Settings.Aim != null`), the static solve is accelerated
   too; the dense path stays bit-identical (setting null → today's code). Remove the setup-time
   refusal in `PlanarSolve.Run` (`PlanarSolve.cs` ~lines 285–313) for accelerated runs and re-word
   it for dense ones; `EmDeembedCeilingTests` asserts the sentence — update it to assert the new
   one.
4. **Re-run the owner's taper** (`brief-em-aim-ceiling.md` §0) accelerated and de-embedded, and
   record what the user now gets.

## Must NOT

- Change `CapacitancePerMetre`'s differencing or the γ-and-C route to Z_c.
- Use kernel A's 2-D C_pul instead: it was considered and its ≤1.3% error renormalises the
  published S; the 3-D differencing is kept on purpose.

## Gates

1e-6 agreement with the dense static solve; the static oracles; the taper re-run in `HISTORY.md`;
`RESOLVED.md` write-up; `CLAUDE.md` §8's "always-DENSE m×m cell system" sentence corrected in place;
`docs/design/mom-engine.md` §10.7's ceiling note gains its `> Built at P11` sentence.
