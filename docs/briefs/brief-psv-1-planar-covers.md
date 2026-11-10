# PSV-1 — dielectrics above the top analysis level enter the planar medium

Series: `brief-psv-0-overview.md`. Independent of briefs 2 and 3.

## Why

`PlanarExtractor.BuildMediumStack` builds the medium from the ground plane up to the topmost analysis
level's sheet and then terminates it in an open air half-space at exactly that height. Any dielectric
the technology declares above that level is discarded: passivation over an MMIC's top metal, solder
mask, a conformal coating, a radome, a gain-raising superstrate. The run says so (the "ANT-12 §1a"
warning). The answer is the one for an uncovered structure, and nothing in it looks different. ANT-12
measured this on the shipped 5.8 GHz patch example: adding a 0.5 mm, εr 3.0 cover gave bit-identical
S-parameters at all 11 frequencies.

The warning itself records that this is an extraction limit, not a physics limit: a uniform
superstrate is a layered medium. The general kernel already solves levels that have dielectric above
them, since every buried level of a multi-level stack is one. `PlanarFarField` already has a
"buried or covered level" path, and `CrossSectionExtractor` already carries dielectric above the metal.
The planar extractor is the one path that stops short.

## What to build

### R-psv1-1 — the medium continues above the top level

In `BuildMediumStack`, keep cutting above `topOfInterest`: every dielectric band whose top is above the
topmost level's sheet contributes a region, up to the top of the highest dielectric. The stack then
terminates in air above that, as it does today.

- The region directly above the top sheet is filled by the same rule as every other level. The
  conductor band's own thickness is absorbed into the paired neighbour (`SheetAt`, R-em-4a, MIM-6).
  Read the existing comment block in `BuildMediumStack`; nothing new is decided there.
- **Merging is unchanged and must reach the termination.** Adjacent same-material slabs merge. A cover
  slab of εr 1, tanδ 0, µr 1 at the top of the stack must merge INTO the air termination rather than
  add an interface. That way an explicit "Air" stackup entry above the top metal is byte-identical to
  having none (gate 1).
- **The flipped stack.** When the levels are on the board's underside (`mirroredStack`, ~line 626), a
  solder mask or coating *below* the lowest level is the same case mirrored. Handle both, or, if the
  mirrored path makes this materially harder, refuse nothing, keep today's behaviour there, and say so
  in the completion note.
- `Stackup.Top == Ground` (a lid) is out of scope. Whatever the planar extractor does with it today, it
  keeps doing.

### R-psv1-2 — the warning becomes a note

Remove the ANT-12 §1a warning. In its place, add one INFO note naming the covers that are now in the
medium and their total thickness. It is not a warning, because nothing is wrong. Keep the wording
plain; the user wrote the stackup, so do not explain to them what a cover is.

Update the user doc paragraph in `docs/user/src/reference/mom-engine.md` (the "A dielectric that stops
somewhere" bullet, ~line 159) and the matching text in `antennas.md`. A cover is now in the solve; what
remains true is that it is laterally infinite. **Do not write change history** ("used to", "now").
State what is true.

### R-psv1-3 — audit what assumed the top level is the top of the stack

A thicker stack and a top level that is no longer on the top interface change the input to several
places. Read each one and decide whether it still holds; fix it or add a refusal with a sentence. List
the verdict for each in the completion note:

| Where | What to check |
|---|---|
| `PlanarProblem.RequiresGeneralKernel` / the one-slab path | A covered single-substrate line now has two regions and goes to the general kernel. Correct, but measure what it costs on the patch example (fill time per frequency, before vs after) and report it. Do not add a timing test. |
| `Dcim` (`ReferenceHeightM = Stack.TopZ`, `LowestFittableFrequency(stackHeightM)`), `PlanarSolve.cs:120` and `:2161` | Heights are measured from `TopZ`, which is now above the top level. Check the fit's validated range and the low-frequency floor still mean what they say. |
| `SpectralGreens` (~858, `kh = k0·TopZ`), `SommerfeldIntegral.cs:513`, `LayeredMedium.cs:637` | Sampling-path and pole-search scaling with the total height |
| Surface-wave pole search | A superstrate adds TM/TE modes. Confirm the search finds them; a missed pole is a silently wrong far field and coupling |
| `PlanarFarField` | The top level becomes "cross-region" (`atTop` false). That path exists (ANT-11's grazing rule names it). Confirm it is exercised and gated |
| `InteriorStaticGreens`, `ShallowImageCore`, `PlanarQuasiStaticLine`, the L8d static C_pul route | Each assumes something about where the level sits relative to the top termination |
| `PlanarPortCalibrator` / `PlanarCalibration` | Its line standards are solved in the same medium. Check the structural medium comparison (MIM-4) still matches a covered line to its standards |
| The analytic alternatives (R-msh-8a) | An analytic microstrip formula offered for a covered line is wrong. Suppress it, or state the cover in the offer |

## Gates

1. **Byte identity.** Every starter technology and every shipped example: no dielectric above the top
   level, so the medium, the `.snp` and the provenance hash are unchanged. Add one test that puts an
   explicit εr-1 "Air" entry above a starter's top metal and asserts a bit-identical medium stack.
2. **Covered line vs cross-section.** A 50 Ω microstrip on the RO4350B 20 mil starter, with a 25 µm,
   εr 3.5 cover added in the test: the planar kernel's Z₀ and ε_eff agree with
   `CrossSectionExtractor`'s to the tolerance the existing uncovered planar-vs-cross-section comparison
   uses. Find that tolerance, cite it in the test, and do not loosen it. Also assert that the cover
   moved ε_eff by more than that tolerance, so the gate cannot pass on an uncovered answer.
3. **ANT-12's own measurement, reversed.** The patch example with the 0.5 mm εr 3.0 cover: the
   S-parameters are no longer identical to the uncovered run, and the resonance moves DOWN. One short
   3D cross-check (EMerge or Palace, coarse, the resonance frequency only, 3-5 points around it):
   planar and 3D agree on the direction and on the size of the shift to within the planar-vs-3D patch
   agreement already recorded for the uncovered case. If that check needs more than a few minutes of
   solve time, stop and report rather than running it.
4. **An MMIC with passivation.** The MMIC GaAs starter plus a 1 µm, εr 7 nitride over Metal2: the run
   solves, the note names the layer, and a Metal2 line's Z₀ falls by a plausible amount (report the
   number).

## Out of scope

- A cover that stops laterally. That is brief 2's note, and otherwise not representable in 2.5D.
- A lid (`Stackup.Top == Ground`).
