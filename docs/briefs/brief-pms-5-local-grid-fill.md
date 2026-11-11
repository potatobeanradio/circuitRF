# PMS-5 — a locally refined grid: the fill

Series: `brief-pms-0-overview.md`. Needs brief 4.

## Why

Brief 4 produces a mesh whose cells are blocks of different sizes, with rooftops that may cover only
a segment of a coarse cell's face. The fill was written for whole-face rooftops on a grid of
elementary cells.

Its accuracy work assumes that geometry throughout: the τ-keyed quadrature, singular extraction, the
closed-form rectangle cores, and the via z-integral. So do its cost work (P4/P5's translation-class
memo, CAP-3's counted cores) and CL1's Gram for conductor loss. This brief makes all of them accept a
block mesh, and proves the answer is the one the tensor grid gives.

## What to build

### R-pms5-1 — the pulse domains are rectangles, not cells

On a block mesh a divergence pulse lives on a **support rectangle**: a whole cell, or a strip of one
(D-pms4-4).

- The scalar block's cell-pair potentials become support-pair potentials over the distinct support
  rectangles. Many rooftops share a support, so deduplicate by identity.
- The vector block's half-supports are the same rectangles with a ramp.

Every integral in `RectangleIntegrals` and `SingularExtraction` already takes a rectangle. What
changes is which rectangles are paired. On a 1 × 1 block mesh the support set is exactly today's cell
set, in the same order.

### R-pms5-2 — the quadrature selector sees the real supports

τ (centroid separation over the larger diagonal) is computed from the supports, not from grid cells.

A strip of a coarse cell is long and thin. Check that the self and near rules (4 × 4 and 3 × 3
Chebyshev-clustered panels) hold their recorded accuracy on aspect ratios the tensor grid did not
produce:

- the fill's own accuracy gates are 5e-6 (`src/Engine/Mom/CLAUDE.md` §3.3);
- run them on the worst strip aspect fixture S produces.

If a rule falls short, panel the long side. Do not loosen a gate.

### R-pms5-3 — translation classes and counted cores

The pair classifier and the class-keyed core store (P5, `PlanarPairClasses`) key pairs by relative
geometry. Extend the key to support rectangles of several sizes.

- Same-size supports at the same integer offset on the candidate grid still share a class, so a
  coarse region with uniform blocks dedupes better than today's irregular grid did.
- `CountCores` / `DistinctBandKeys` count the same keys.
- Brief 1's bounds still apply.

### R-pms5-4 — the multi-level blocks and the vias

The ẑẑ and ẑx̂/ẑŷ blocks pair a vertical basis's footprint with supports on other levels. With one
shared tree (D-pms4-2), a vertical basis's footprint is a block on both levels. `ViaZIntegral` takes
the block rectangle. The ẑx̂ arm integrates against strips as well as cells.

### R-pms5-5 — conductor loss

CL1's Gram ⟨f_m, f_n⟩ and CL2's conductor-power quadratic form are over rooftop supports. Strips
overlap their parent cell's other strips, so the Gram gains off-diagonal entries between segments of
one coarse cell. Derive them in closed form; they are ramps over rectangles.

### R-pms5-6 — the solve accepts the mesh

Lift brief 4's refusal for the dense solve, the multi-level fill and the via fill. AIM, ports and
everything else stay refused until brief 6.

## Gates

1. **1 × 1 blocks are bit-identical.** With every block forced to 1 × 1 and `LocalGrid` on, the
   filled matrix equals today's entry for entry on every existing fill test fixture, single-level and
   multi-level.
2. **Accuracy against the tensor grid, on A1 and A2.** This is self-calibrated as in brief 3, gate 2.
   Solve each one with the tensor grid, with the tensor grid one refinement step finer, and with the
   local grid. Do this undeembedded at first: brief 6 brings the ports, so use `InternalDeltaGap`
   ports, which need no standards. Run 1, 5 and 10 GHz.
   - |S_local − S_tensor| must be no larger than the tensor grid's own refinement difference;
   - and below 1e-2 in any case.

   Report the three numbers per frequency.
3. **Charge and reciprocity.** On every block fixture:
   - Z is complex-symmetric to the bit (`UseSymmetricFactorization` depends on it);
   - the net charge of every basis is zero except ground attachments.
4. **Quadrature on strips.** The fill's own accuracy gates run on the worst strip aspect of fixture S
   and pass at their current thresholds.
5. **Cost, reported not asserted.** Fill time and counted cores per frequency on fixture S at 0.6 mm,
   tensor against local. A local mesh with fewer unknowns must not be slower to fill; if it is, find
   out why before closing.

## Out of scope

Ports and de-embedding, AIM, the far field, current density and the DC solve. All are brief 6.
