# PSV-3 — a planar via that crosses a dielectric interface

Series: `brief-psv-0-overview.md`. Independent of briefs 1 and 2.

## Why

`PlanarKernel.EveryViaLiesInOneMediumRegion` refuses any via whose span crosses an interface of the
medium. Its own comment names the reason and the fix. `ViaZIntegral` integrates the via's two extracted
asymptotes over its length in closed form (`PrismCore`). That works because their coefficients are the
source region's own Fresnel coefficients, which do not move with height. A via over two regions has two
sets of coefficients, and height pairs that straddle the interface have none. "Handling it properly
means splitting each via at the interfaces it crosses and treating the sub-span pairs separately, which
is real work and is named as not built."

The refusal's advice is to put a conductor level on the interface so the via becomes two stacked vias.
A process with no metal at that interface then has to be edited to suit the solver. Common real cases:
a substrate via through a die-attach or bonding layer; a PCB via through a core and a prepreg of
different εr; a thick top via through two inter-metal dielectrics; the MMIC MIM stack, whose
Metal1-Metal2 post crosses the MIM dielectric band. MIM-7 had to ship a separate technology for that
last one because this refusal took out every airbridge post.

## Read first

- `src/Engine/Mom/ViaZIntegral.cs`: the file header's split into the singular half (closed form,
  `PrismCore`) and the bounded half (Gauss in z on the terms).
- `PlanarKernel.cs` ~168-225: the refusal and its comment.
- `docs/design/mom-engine.md`: how a vertical current's scalar potential is formed (search "vertical",
  "bound charge", "polarisation charge"). The user doc states the kernel carries polarisation charge on
  interfaces explicitly (`docs/user/src/reference/mom-engine.md` ~218).

## The approach risk, stated before any code

**The split in z is the easy part. The question that decides whether this is a week or a month is the
scalar potential of a vertical current that crosses an interface.** Where ε jumps, a uniform vertical
current leaves a polarisation charge on the interface proportional to the jump in 1/ε. A
mixed-potential formulation must either carry that charge as its own term or show that its existing
kernel for vertical sources already accounts for it. Answer this from the code and the design doc
**first**:

- If the existing vertical-source kernel is already written so the interface charge is carried, by the
  cascade's own cross-region terms or by the explicit bound charge the user doc mentions, the work is
  the z-split below. Proceed.
- If it is not, the via's charge at the interface is missing, and a split-only fix would give a
  plausible wrong answer: exactly what the refusal exists to prevent. **Stop and report** with what
  adding the term would take. Do not build the split alone.

## What to build (if the risk check passes)

### R-psv3-1 — split each via at the interfaces it crosses

A via `[lo, hi]` crossing interfaces `z₁ < … < z_k` becomes sub-spans `[lo, z₁], [z₁, z₂], …,
[z_k, hi]`, each in one region. The basis function is unchanged: one uniform vertical current over the
whole via. **Only the z-integral is split.** No new unknowns, no new basis, no junction condition: the
current is continuous through an interface by construction.

For a basis pair (via i, via j), the z-integral is the sum over sub-span pairs (a, b):
- **a and b in the same region:** today's treatment exactly. `PrismCore` closed form for the extracted
  asymptotes, Gauss on the bounded remainder.
- **a and b in different regions:** no extraction is needed, since cross-region terms are bounded at
  ρ = 0 (the refusal's own comment says so). The whole kernel takes a Gauss rule in z on each sub-span.
  Choose n_z per sub-span by the same rule `ViaZIntegral` uses for the bounded half. Where a sub-span
  is very thin next to its neighbour (a 1 µm film beside a 100 µm via), the node count follows the
  sub-span's own length, not the via's.
- The same applies to a via's pairings with horizontal (sheet) basis functions. Find every caller of
  `ViaZIntegral` (`PlanarFill.cs`, `ShallowImageCore.cs`) and make each sum over sub-spans.

### R-psv3-2 — the refusal goes, and one stays

Remove `EveryViaLiesInOneMediumRegion`. Keep a refusal only for what still cannot be done, with a
sentence that says what. For example, a sub-span thinner than the z-rule can resolve against its
partner's distance; measure whether that case exists before adding a refusal for it.

The ground-attachment via (`ToGround`) takes the same path. Its lower end is the plane.

### R-psv3-3 — the user-facing text

Remove the stacked-via advice from every message and doc that gives it (`grep -rn "two stacked vias"`,
`grep -rn "crosses a dielectric interface"`). Update `docs/design/mom-engine.md` ~1658 and
`docs/user/src/reference/mom-engine.md`. State what is true, not what changed.

## Gates

1. **Byte identity.** Every via that lies in one region (every starter technology, every shipped
   example, `ViaPhysicsTests`' fixtures) gives a bit-identical matrix. The split of a one-region span is
   the span itself, so the code path must be identical, not merely close.
2. **The split agrees with itself.** A via in ONE region, artificially split at a fake interface of
   identical material on both sides (bypass the merge in the test), gives the same terminal inductance
   as the unsplit via, to the closed form's own convergence tolerance (`ViaPhysicsTests.T3_1b` has the
   ladder).
3. **Against 3D.** One fixture: a via through two dielectrics of clearly different εr (e.g. 3.0 and
   10.0, equal thickness) between two pads, with a short line on each. Planar vs a short 3D run (Palace
   or EMerge, one to three frequencies): |ΔS| in the agreement band the existing via-vs-3D comparison
   records for a one-region via. Also report the inductance a single-region approximation would have
   given (via in the lower εr only), so the size of the correction is on record.
4. **The MIM technology's post.** The airbridge post that MIM-7 found refused on
   `mmic-GaAs_2LM_100um_MIM.ctech` now solves. Report its inductance next to the plain starter's.
5. **Stop rule.** If the risk check above fails, the gate is the report, not the code.

## Out of scope

- Non-uniform current along a via (a via a sizeable fraction of a wavelength tall). It is a separate
  limit with its own existing refusal (R-via-6).
- Horizontal metal at the interface. That is an ordinary level.
