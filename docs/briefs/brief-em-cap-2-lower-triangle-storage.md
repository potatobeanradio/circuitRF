# Brief CAP-2 — store only the lower triangle of the dense planar matrix

Series: `brief-em-cap-0-overview.md`. **Changes no answer — the gate is bit-identical S-parameters.**
Halves the matrix term of `PlanarSystem.ResidentBytes` from 16·N² to ~8·N².

## The gap

Z is complex-symmetric by construction: `PlanarFill` computes m ≤ n and mirrors (R-fil-2).
`SymmetricFactorization` (P7) is an in-place, blocked, right-looking LDLᵀ that **reads and writes
only the lower triangle**; its own header says the upper triangle is never read. Yet the matrix is a
full `Mat<Complex>` (NumFlat, column-major, stride N), so half of the largest allocation in the
solver holds a mirror nobody uses.

## What to do

1. **A new storage type for the lower triangle in BLOCK-COLUMN layout**: for each block of `nb`
   columns starting at k (nb = `SymmetricFactorization.DefaultBlockSize`, 64), one contiguous
   column-major panel of rows k…N−1 × nb columns. Cost ≈ N²/2 + N·nb/2 entries. Chosen because it is
   exactly the shape the factorisation already walks: the panel factor touches one panel, and the
   trailing update writes column j while reading the current panel — both stay contiguous over i,
   which is what keeps P7's speed. **Do not use the classic row/column packed triangle** — its column
   starts are not aligned to blocks and the trailing update loses its cache behaviour.
2. **The fills write into it directly**, never building a full matrix first: `PlanarFill.Fill`,
   `FillMultiLevel`, `PlanarFillMerged.FillMerged` (the PM-2 TᵀZT path, which already works in
   bounded panels — `MergedPanelBudgetBytes`), and anything that adds to Z after the fill (surface
   impedance, conductor loss, port loading — grep for writes to `Matrix`). Stop mirroring.
3. **`SymmetricFactorization` factors the new type.** Same loops, same order of operations, same
   parallel decomposition over trailing columns — so the arithmetic is identical and the result is
   bit-identical. Keep its stability instruments (`GrowthFactor`, `SmallestPivotRatio`).
4. **Readers of Z before factoring** (`PlanarSystem.Matrix`, diagnostics, tests, `Residual`,
   `TrackFactorizationResidual`'s copy): give the type an element accessor `(i,j)` that answers
   either triangle by symmetry, and an explicit `ToDense()` for small-N callers. `Matrix` may become
   that `ToDense()` — expensive and named so. Callers that only need a mat-vec get a symmetric
   mat-vec on the packed type.
5. **The NumFlat LU fallback** (`PlanarFillSettings.UseSymmetricFactorization = false`) expands to
   dense for itself and keeps its own (higher) cost in `ResidentBytes`; it is a reference path, not a
   capacity path.
6. **Accounting.** `PlanarSystem.MatrixBytes` becomes the block-column count × 16, and
   `ResidentBytes`, `CeilingFor`, the refusals and the panel all follow from it (one function, as P1
   arranged). `PlanarMemoryAccountingTests` must still hold the accounting to the allocation.

## Gates

- **Bit-identical** S-parameters against today's path on the §10.7 hero, a multi-level via mesh, a
  PM-2 merged mesh, and a de-embedded run. If the summation order had to change somewhere, say
  where and why, and the gate becomes `|ΔS| ≤ 1e-12` for that path only.
- **Allocation:** measured bytes of the stored matrix within 1 % of N²/2·16 + N·nb/2·16.
- **Speed (scratch harness, recorded, not a test):** factor time at N = 11,210 within +10 % of the
  recorded 37 s on the same machine; fill time not worse. If it is worse, find why before shipping —
  the layout was chosen so it should not be.
- **Peak working set** of one frequency point at N ≈ 15,000 measured and recorded against the new
  `ResidentBytes`.

Targeted tests: `tests/Engine.Tests/Mom/PlanarP7SymmetricFactorTests.cs`,
`PlanarMemoryAccountingTests.cs`, `PlanarFillTests.cs`, `PlanarFillCostTests.cs`, plus the hero and
de-embedding test classes the comparison runs through. Run only those.

Record in `src/Engine/Mom/RESOLVED.md`: the layout chosen and why, the measured bytes, the factor
time before/after, and the list of matrix writers that had to change.
