# Brief P4 — per-cell-pair moment caching in the vector block

**Problem.** `CLAUDE.md` §10.7 (design note) named this as "the cheaper first move" at L8c and it was
never taken. `BuildDirectionCores` (`src/Engine/Mom/PlanarFill.cs:593`) calls `PairCores` four times
per same-direction basis pair — once per (half, half) — and `AddDirectionBlock` / `PlanarEntryFill.At`
call `PairRemainder` four times per pair at every frequency. Adjacent rooftops share cells, so the
same CELL pair is integrated up to four times with different ramp weights.

**The four are linearly dependent.** For a cell of extent Δ along the flow direction, the two ramps a
cell can carry are `w_A = (u − u₀)/A` and `w_B = (u₁ − u)/A = Δ/A − w_A`. So every (half, half)
integral over a cell pair is a linear combination of four cell-pair primitives per kernel:
`⟨1,1⟩`, `⟨ξ,1⟩`, `⟨1,ξ′⟩`, `⟨ξ,ξ′⟩` — where `⟨·,·⟩` is the outer-quadrature × closed-form-inner
integral already computed, with `ξ` the outer node's ramp coordinate (free at the outer node) and
`ξ′` the inner moment (`InverseMomentU`/`V`, `LogMomentU`/`V`, already closed form). One outer pass
per cell pair yields all of them for both flow directions: 7 numbers × (Inverse, Log[, Radius]).

Read first: `PlanarFill.cs` — `BuildDirectionCores`, `PairCores`, `RampHalves`, `WeightMoment`,
`PairRemainder`, `PlanarEntryFill`; `RectangleIntegrals` (the moment closed forms); the D6 header.

## Milestones

1. **Derive and write down the linear map** (in the file header, as L8c did) from the four
   primitives to the four (half, half) combinations, for X and for Y flow, including the sign
   convention `Sigma`/`Edge` in `CellWeight`. Get this right on paper before code: a sign slip here
   is a smooth, plausible, wrong inductance.
2. **Cores.** Replace the per-basis-pair `VX0/VXLog` build with a per-CELL-pair build of the
   primitives (same packed cell-pair index as `S0`), and assemble the basis-pair core at use.
   Storage: 7 × 2 doubles per cell pair against today's `2 + 3 + 3` per cell pair — roughly the same
   bytes (+ P2's `VXArea` removal nets it below today). Quadrature calls fall from ~4.5 m² to ~0.5 m².
   Gate: the assembled `Fill` matrix on the hero and on the 60 mm taper agrees with today's to
   **1e-12 relative per entry** (associativity changes; bit-identity is not available) and every
   existing `PlanarFillTests`/`PlanarFillOracleTests` gate passes unchanged.
3. **Remainder.** Do the same for `PairRemainder`: one outer×inner pass per cell pair per frequency
   accumulating the same 7 weighted sums of `rem(ρ)`. Cache per (cell pair) within one fill. The
   per-frequency vector remainder work falls ~4×. Gate: same 1e-12 on the assembled matrix.
4. **`PlanarEntryFill`** (AIM's near field) takes the same primitives through its `_pCache`
   mechanism, widened from scalar `P` to the 7-primitive record. Gate: `AimAcceleratorTests`
   green; near fill wall clock before/after at N = 3,731 in `HISTORY.md`.
5. **Cut cells** (`Strips != null`): the ramp is affine in both coordinates, so the four-primitive
   reduction does not hold. Fall back to today's per-half path for any pair with a cut cell. Gate:
   the conformal fixtures bit-identical.

## Must NOT

- Change any quadrature rule, panel clustering, or the closed forms.
- Touch the multi-level ẑ blocks (they are pulse × pulse already and share `S0`).

## Gates

The 1e-12 matrix agreement on three fixtures; all existing fill oracles; `HISTORY.md` table of core
build time and per-frequency fill time before/after on the three series fixtures; `RESOLVED.md`
write-up; `docs/design/mom-engine.md` §10.7's "cheaper first move" paragraph gains its `> Built at P4`
note with the measured factor.
