# Brief P6 — AIM must not rebuild its geometry at every frequency

**Problem.** `PlanarSolveContext.SolveAt` constructs a new `PlanarAimOperator` per frequency
(`src/Engine/Mom/PlanarSolve.cs:149`). Its constructor rebuilds the support boxes, the stencil
projections, the near set, the mirror index — all frequency-independent — and, decisively, the near
fill (`PlanarAim.cs:419–429`) calls `PlanarEntryFill.At`, which runs the clustered-panel `PairCores`
singular quadrature (900 outer points × closed forms, per half pair) from scratch. The dense path
does that ONCE per mesh (D6, `CoreFillCount == 1`). This is the structural half of HISTORY §12's
"the entry count falls 10×, the fill time does not": AIM's per-frequency build at N = 3,731 is 25 s
against the dense path's 26.7 s fill + LU — but the dense path's singular cores are amortised over
the sweep and AIM's are paid at every point.

Read first: `PlanarAim.cs` (constructor, `NearSet`, `MirrorIndex`, `PlanarEntryFill`); `PlanarSolve.cs`
(`PlanarSolveContext`); `PlanarFill.cs` D6 header; HISTORY §12. Do P4 first (the near-field cores
this caches are P4's primitives; P5 optional but compounding).

## Milestones

1. **Split `PlanarAimOperator` into a per-mesh `PlanarAimGeometry` and a per-frequency operator.**
   The geometry holds: grid origin/pitch/extent, `_stencils` (real coefficients — they are real
   today, stored as `Complex`; store `double`), `_rowPtr`/`_colIdx`, the mirror index, and the near
   pairs' frequency-independent cores (P4's primitives, one record per near CELL pair, indexed by a
   sparse cell-pair CSR). Build it once in `PlanarSolveContext`'s constructor when `Settings.Aim` is
   set — i.e. exactly where `BuildGeometryOnlyCores` runs today.
2. **Per frequency** the operator builds only: the two grid kernel tables and their FFT hats, the
   remainder tables (already shared), the near remainders + assembly, the AIM correction, and the
   `SparseLU`. Gate: the near entries are **bit-identical** to today's `entry.At(i, j)` where P4
   left them bit-identical, else P4's 1e-12; `AimAcceleratorTests` and `AimAccuracyTests` green
   unchanged.
3. **Counter gate**: over a 5-point sweep, the near-field singular cores are computed exactly once
   (instrument as `CoreFillCount` is; assert 1).
4. **Measure** per-frequency AIM build time split (grid kernel, near remainder, correction, LU) at
   N = 552, 3,731 and 12,000 before/after, alone. Record in `HISTORY.md` beside §12's table and
   re-state the time crossover.
5. **Memory:** the geometry adds the near cores (P4: 7 × 2 doubles × near cell pairs ≈ 112 B ×
   ~200/row × m) — quantify it in the same table; it replaces recomputation, not the 16·N² it
   avoids.

## Must NOT

- Change the projection order, pitch, near radius, tolerance or the preconditioner. P8 owns the
  radius.
- Touch the multi-level refusal. P12.

## Gates

Bit-identity/1e-12 on the near entries; the once-per-sweep counter; both AIM test classes; the
`HISTORY.md` split table; `RESOLVED.md` write-up; correct `CLAUDE.md` §8's AIM paragraph in place
where its "the fill time falls far more slowly" explanation is now only half true, and add the
`> Built at P6` note to `docs/design/mom-engine.md` §10.7.
