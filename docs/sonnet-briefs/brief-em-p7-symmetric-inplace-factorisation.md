# Brief P7 — an in-place, blocked, parallel complex-symmetric factorisation

**Problem.** Per frequency at N = 4,933 the LU is **42.8 s** against a 21.8 s fill (design note
§10.7's own table; the "fill, not LU" ratio there folds in the once-per-sweep core build). NumFlat's
`LuDecompositionComplex` is single-threaded (measured CPU/wall = 1.00 on 10 cores), unblocked, and
stores `L` and `U` as two separate full matrices while `PlanarSystem` keeps `Matrix` — three N×N
complex arrays resident at once (P1 has the exact number). The fill parallelises 5.4×; the LU does
not parallelise at all, so near the ceiling a sweep is ~2/3 factorisation on one core.

**Z is complex-symmetric by construction** (R-fil-2 mirrors bit for bit). An LDLᵀ without pivoting
halves the flops and the storage; a blocked right-looking form parallelises over the trailing
update; done in place, no second copy exists. MoM matrices are strongly diagonally dominant in the
self terms and unpivoted complex-symmetric factorisation is standard practice for them — but it is
not guaranteed stable, so the gate is a residual check, not faith.

Read first: `PlanarSystem.cs`; `PlanarExcitation.Solve` (P back-substitutions per factorisation);
`ChargeSolver.cs` (the LU idiom this replaces); P1's memory table.

## Milestones

1. **Write `SymmetricFactorization`** in `src/Engine/Mom/` (managed C#, no package): packed or
   full-lower in-place LDLᵀ, blocked (block size ~64), trailing update via `Parallel.For` over
   column blocks, respecting `PlanarFillSettings.MaxDegreeOfParallelism`/`Budget` exactly as
   `ForRows` does (one cap). Forward/backward substitution for one and for P right-hand sides.
   Residual `‖Zx − b‖/‖b‖` computed after every solve and exposed.
2. **Gate on the dense reference**: on the hero, the 80 mm line and the taper, `x` against NumFlat's
   LU solution to 1e-10 relative, and residual ≤ 1e-12. Then on the worst-conditioned fixtures the
   repo has — the FR-4 20 GHz remainder-stressed case (`PlanarFillTests` Tier 6) and the low-frequency
   guard's neighbourhood (`Dcim.CanFitAtFrequency`'s floor): record the residual. **If any fixture's
   residual exceeds 1e-8, the brief stops and reports; a pivoted alternative (Bunch–Kaufman) is the
   follow-up, not a quiet fallback.**
3. **Wire it into `PlanarSystem`** behind the existing `IPlanarOperator`; `Matrix` is consumed by the
   factorisation (document that `Matrix` is no longer readable after `Lu`, and fix the two tests that
   read it, if any, by taking a copy in the TEST). Keep NumFlat's path reachable as a setting for the
   oracle comparison, exactly as `UseRadialTable = false` is kept.
4. **Bit-identity is not available** (a different factorisation). Gate every published S on the
   three series fixtures to **1e-9 absolute** against the NumFlat path, de-embedded, over a 5-point
   sweep; `PlanarSolveTests` and `NetworkPropertyTests` green.
5. **Measure** factor time at caps 1/2/4/10 and resident peak at N = 552 / 1,980 / 4,933; record
   with P1's table. Re-ask R17: with resident ≈ 8·N² (packed) + cores, what N fits 1 GB? Record the
   number; **moving `UnknownCeiling` is a separate owner decision** — write the sentence that would
   change and stop.

## Must NOT

- Add a native BLAS/LAPACK dependency (root `CLAUDE.md`: ask first).
- Change the fill or the AIM path.

## Gates

Milestones 2 and 4's tolerances; the timing/memory table in `HISTORY.md`; `RESOLVED.md` write-up;
correct `docs/design/mom-engine.md` §10.7's "cost is the fill, not the LU" paragraph in place (it is
false per frequency today) with the `> Built at P7` note.
