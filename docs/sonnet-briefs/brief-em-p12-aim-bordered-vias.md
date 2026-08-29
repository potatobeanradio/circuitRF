# Brief P12 — vias and multi-level under AIM, as a bordered system

**Problem.** `PlanarAimOperator.Build` refuses any mesh with a ẑ basis, and `PlanarSolveContext.SolveAt`
refuses the general kernel under `Aim` — so every real PCB with a ground via is capped at the dense
5,000. The refusal's reason ("a projection with a derivative in it") is true of projecting the ẑ
bases; it is not necessary to project them.

**The shape.** Order the unknowns horizontal-first (R-via-5 already does): `Z = [Z_hh Z_hz; Z_zh Z_zz]`.
`N_z` is the via footprint count — tens to a few hundred. Then:

- `Z_hh` is the same operator AIM accelerates today, per (level, level) pairing. All levels share
  one tensor grid, so each pairing is one more grid kernel table + FFT hat pair (`G_A` and `G_q` at
  `(z_a, z_b)` from `PlanarKernelSet.Get`) — `L(L+1)/2` of them for L levels, a handful — and the
  scatter/gather is per level.
- `Z_hz`, `Z_zh` and `Z_zz` are filled DENSELY by today's `FillMultiLevel` arms (`MixedEntry`,
  `SingularPrismPart`, `CellPairPotential`): `N_h × N_z` entries, cheap when `N_z ≪ N_h`.
- The product is `y_h = AIM(x_h) + Z_hz x_z`, `y_z = Z_zh x_h + Z_zz x_z`. The preconditioner is the
  near-field LU with the dense border folded in (the border is sparse in the sense that matters:
  `N_z` rows) — or block-eliminate `Z_zz` (small, dense LU) and precondition the Schur complement.

Read first: `PlanarFill.FillMultiLevel` (after P3); `PlanarAim.cs` (after P6 and P8);
`PlanarKernelSet`; `CLAUDE.md` §3.3–3.4 and §7's refusals; `ViaBasisTests`, `VerticalCurrentTests`,
`MultiLevelPortTests` (the dense oracles this must match).

## Milestones

1. **Multi-level without vias first.** Per-level-pairing grid kernels; near set across levels
   (same in-plane criteria; a cross-level pair has no 1/ρ but still its ln ρ — `CLAUDE.md` §3.5); the
   exact near entries from `FillMultiLevel`'s horizontal arm. Gate: `|ΔI|` against the dense
   multi-level solve at the same tolerance `AimAccuracyTests` uses (8.7e-7 at the shipped defaults),
   on `MultiLevelPortTests`' fixtures.
2. **The border.** Dense `Z_hz`/`Z_zz` from the existing arms; the product; the preconditioner.
   Gate: `|ΔI|` against the dense via solve on `ViaPhysicsTests`' two-level fixture and the
   ground-attachment fixture (`InternalPortTests`), same tolerance; de-embedded S to 1e-6 absolute.
3. **`G_A^zz`'s validated range** (`VerticalRangeVerdict`) still governs — it is about the kernel,
   not the solver; assert the refusal still fires.
4. **Ceiling.** Grow a two-level via-bearing fixture by LENGTH (the healthy construction) to the
   12,000 ceiling and past; record iterations, near/row, resident bytes. Recommend whether
   `AcceleratedUnknownCeiling` applies to via meshes — **owner's decision**; write the sentence that
   changes (`SurfaceMesher.GuardCeiling`'s `accelerated` argument and `PlanarSolveContext`'s
   `levels is null` condition are the two places).

## Must NOT

- Project the ẑ bases or the mixed derivative kernel onto the grid. If `N_z` ever makes the dense
  border the cost, that is a later brief with its own measurement.
- Change any via physics (`ViaZIntegral`, `SingularPrismPart`, `MixedEntry`).

## Gates

The two `|ΔI|` gates; the S gate; the range-verdict refusal; the ladder in `HISTORY.md`;
`RESOLVED.md` write-up; `CLAUDE.md` §8's "Multi-level/via meshes are refused by name" corrected in
place; `docs/design/mom-engine.md` §10.7 gains the `> Built at P12` note.
