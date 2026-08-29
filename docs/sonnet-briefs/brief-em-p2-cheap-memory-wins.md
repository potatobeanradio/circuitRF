# Brief P2 — four mechanical memory wins on the dense path

Four changes, each a few lines, each independently gated. None changes the arithmetic of any entry;
every gate is bit-identity on the assembled matrix.

Read first: `src/Engine/Mom/PlanarFill.cs` (`BuildDirectionCores`, `ScalarPotentialMatrix`,
`PlanarFillCores`), `PlanarDeembed.cs` (`StaticCapacitance`, `CapacitancePerMetre`),
`PlanarSolve.cs` (`PlanarSolveContext` ctor, `PlanarPortCalibrator` ctor and `PrepareAt`).

## Milestones

1. **Drop `VXArea`/`VYArea`.** `PlanarFill.cs:601` stores `cArea[p] = mMom * nMom` — an O(N²) array
   holding the outer product of a per-basis O(N) vector. Keep the per-basis moments on
   `PlanarFillCores` (one `double[]` per direction) and multiply at use (`AddDirectionBlock`,
   `HorizontalVectorEntry`, `PlanarEntryFill`). Update `CoreBytes`. Gate: `PlanarFill.Fill` bit-identical
   on the hero; `CoreBytes` falls by exactly `8·(nx(nx+1)/2 + ny(ny+1)/2)`.

2. **`StaticCapacitance` must not copy `P`.** `PlanarDeembed.cs:229` builds a second m×m matrix to
   divide by ε₀. Solve `P q = ε₀·1` instead (scale the right-hand side, or the result). Gate: the
   returned capacitance bit-identical or within 1e-14 relative (the scaling is one multiply per
   entry; say which).

3. **`CapacitancePerMetre` must reuse the standards' existing cores.** `PlanarDeembed.cs:220` rebuilds
   `PlanarFillCores` for meshes whose `PlanarSolveContext` already holds identical cores. Pass the
   context's cores in (the signature takes `PlanarStandard`; the calibrator has `_standards[i].Cores`).
   Gate: a counter — `PlanarSolveResult.CoreFillCount` is currently 1 + standards; it must not change,
   and a new assertion that `BuildCores` is invoked exactly once per mesh over a de-embedded sweep
   (instrument `PlanarFillCores` with an instance-independent build counter the way
   `PlanarSweepResult.CoreFillCount` is done — never a static).

4. **Standards' cores are built lazily.** `PlanarPortCalibrator`'s constructor
   (`PlanarSolve.cs:249`) builds a context — and therefore the O(m²) cores — for every standard of the
   band, though `NeededAt` fills only two per frequency. Build a standard's cores on its first
   `RawScatteringAt`. On a 1–20 GHz band with several separations, report in `HISTORY.md` how many
   standards were built before and after, and the resident delta. Gate: the sweep's published S is
   bit-identical; the standards that were never selected never build cores (counter).

   *Interaction with milestone 3:* `CapacitancePerMetre` uses `Standards[0]` and `Standards[^1]`.
   The longest standard may therefore be built only for the static solve. That is correct — it is
   what the static differencing needs — but note it in the write-up; P11 changes that solve.

## Must NOT

- Touch `PlanarAim.cs`, the LU, or any quadrature.
- Store `P` packed — that is a candidate (it halves a transient 4N² bytes) but it touches every
  `p[a, b]` reader in three fills; if you take it, it is a fifth milestone with its own bit-identity
  gate, and say so.

## Gates

Bit-identity per milestone as stated; the counters; `HISTORY.md` before/after resident bytes on the
three series fixtures; `RESOLVED.md` write-up.
