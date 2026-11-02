# Brief MIM-3 — thin dielectric layers: measure before trusting

**Problem.** A MIM capacitor puts two meshed conductor levels 0.05–0.5 µm apart across a dielectric
layer thinner than anything the full-wave kernel has ever been measured on. `LayerStack.CanRepresent`
accepts any positive thickness, so nothing REFUSES the structure — which is exactly the dangerous
configuration: a complete, plausible answer with no evidence behind it. This brief is
measurement-first: produce the error-vs-separation ladder, and end with either a validated-range
statement or a named, bounded fix.

**Why suspicion is warranted, specifically** (all on the record in `src/Engine/Mom/CLAUDE.md`):

- §L8c: DCIM's fitted complex images stop being "smooth on the mesh's own scale" once an image sits
  closer to the metal plane than a cell is wide — and the observed failure mode was a quadrature
  that was 5% wrong while *converging gently enough to look converged at every step*. Plate-scale
  separations (~0.1 µm) against micron-scale cells reproduce that geometry deliberately.
- §3.5: cross-level entries have no 1/ρ but still carry logarithms; "different levels ⇒ smooth ⇒
  plain quadrature" is a recorded trap.
- The numerical-∂z rule: a step must be bounded by the distance to the nearest interface — interfaces
  are now 0.05 µm apart.
- `Dcim.FitAtHeights` and `ValidatedRhoOverLambdaAtHeights` were validated at interlayer-scale
  height pairs; nothing has sampled them at near-coincident heights, and the L9 two-level degeneracy
  gate ran at ordinary interconnect spacing.

Read first: `src/Engine/Mom/CLAUDE.md` §L8a/§L8c and §3.5; `Dcim.cs` (`FitAtHeights`,
`WithinValidatedRange`); `SommerfeldIntegral.EvaluateLayered` (the shares-no-approximation oracle);
`PlanarFill.FillMultiLevel`; `docs/design/mom-engine.md` §10.9's oracle-ladder discipline —
this brief is one more rung of that ladder, one tier down in z.

## Milestones — each a table in `HISTORY.md` before the next is started

1. **Kernel tier.** Spatial-domain Green's function error at height pairs (z, z′) with
   |z − z′| ∈ {0.05, 0.1, 0.2, 0.5, 1} µm, against direct Sommerfeld integration, on a stack with a
   thin high-εr layer between the two heights. The §L8a presentation: error as a fraction of the
   free-space kernel at the same ρ, over the validated ρ span.
2. **Fill tier.** Assembled cross-level matrix blocks on a two-plate mesh vs the same entries at
   forced-high quadrature order, across the same separation ladder × a cell-size ladder
   (cell/separation from ~1 to ~50). This is where §L8c's converged-looking-but-wrong mode would
   reappear; the ladder's shape — not any single number — is the evidence.
3. **Physics tier.** Extracted low-frequency capacitance of a square parallel-plate pair vs the
   closed form ε₀εᵣA/d with a stated fringing correction, and the C ∝ 1/d law across the ladder;
   plus reciprocity/passivity at RF and mesh-refinement monotonicity (§10.9's self-consistency
   set). Kernel A on the equivalent cross-section is a free second opinion for the per-unit-length
   case — use it.
4. **The verdict, written down.** Either: the shipped defaults hold to a stated tolerance over a
   stated separation range (then `Dcim.WithinValidatedRange`-style NOTE wording, never a refusal,
   reports outside it); or a bounded quadrature/fit change with milestones 1–3 rerun after it. If
   the fix is not cheap, stop and report — the ladder itself is this brief's deliverable.

## Must NOT

- Ship any tuning that milestone 2's ladder did not justify, and never loosen an existing gate
  (`AimAccuracyTests`' 8.7e-7, the L9 gates) to accommodate thin layers.
- Add timing tests, or routine tests above the cost rules — the ladders run in a scratch harness
  (RELEASE, alone); anything kept as a test that exceeds ~5 s carries `Category=Benchmark`.
- Change the one-level or interconnect-scale paths' arithmetic unless a measured defect demands
  it — and then bit-identity is replaced by a stated tolerance with the old arithmetic kept as a
  named reference during the change (the P4 pattern).

## Gates

The three ladders in `src/Engine/Mom/HISTORY.md`; the verdict in `src/Engine/Mom/RESOLVED.md`; a
small routine regression pinning whichever tier proved most fragile (a counter or a fixed-input
matrix-entry comparison, not wall clock); stale sentences in `docs/design/mom-engine.md` §10.12
corrected in place. `dotnet test tests/Engine.Tests` green.
