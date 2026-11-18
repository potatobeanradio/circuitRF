# PWL-2 — Measure before building: the wall port on Palace, by hand

Series overview: `brief-pwl-0-overview.md`. Needs brief 1's `LineCalibrator`.

## Why

Four things decide whether the wall port is accurate, and none of them can be settled by argument:

1. **How big the gap is.** The calibration removes the gap, so the answer should not depend on it; how small it may be
   before the mesh or the source misbehaves, and how large before it stops being a lumped source, is a measurement.
2. **Whether the port region must be meshed identically** in the structure and in its standards. The planar solver
   gets identity for free from its tensor grid and its gate depends on it (`PlanarCalibration.cs` D4). A tetrahedral
   mesh differs from one structure to the next unless something forces it not to.
3. **Where C′ comes from**, for Z_c = γ/(jωC′) (overview D5).
4. **How long the uniform end run must be** before the two walls' error boxes stop seeing each other.

This brief answers them on Palace with hand-built problems and writes the answers down. Nothing ships from it except
the fixtures, the findings and the amendments to brief 3.

## The fixtures

All are `Em3dProblem`s built in a scratch harness (not in the repo), each with a PEC face at both ends of the line and
a lumped port in a gap of length `g` at each end, from the face (−) to the line (+), in the line's plane. The
standards for each are the same cross-section between two PEC faces at the separations `LineCalibrator`'s plan asks
for. Solve at **1, 5 and 10 GHz** unless a question needs more.

| Fixture | Cross-section | Oracle |
|---|---|---|
| **S** | 50 Ω stripline, zero-thickness strip, PTFE fill (εr 2.1), PEC top and bottom faces, PMC sides | exact closed form (Z_c and γ) |
| **M** | 50 Ω microstrip, 0.508 mm laminate (εr 2.2, tanδ 0.0009), 18 µm copper, absorbing top and sides | classic closed-form microstrip model with dispersion |
| **C** | edge-coupled microstrip pair on M's laminate, 0.3 mm gap | classic closed-form coupled-microstrip model |
| **Si** | 10 µm line on 5 µm oxide over 200 µm silicon of 10 Ω·cm, ground under the silicon | none; compared with a wave port on the same face |

**S** and **C** are the gate fixtures of the overview (gates 3 and 4) and are committed under `testdata/pwl/` as
`.c3d` files, so briefs 3, 5 and 6 solve exactly the same structures.

## Questions and what to measure

- **Q1 — Gap.** On S and M, `g` = w/40, w/20, w/10, w/5 (w the strip width) and h/10 (h the substrate height). Report
  the calibrated |ΔS| against the oracle for each. Choose the default `g` as the middle of the flat region, as a
  fraction of whichever of w and h binds, and a floor (cells across the gap) below which the run refuses.
- **Q2 — Mesh identity.** On S, M and C, three ways: (a) structure and standards meshed independently with the same
  settings; (b) an **extruded port block**: the cross-section meshed once as a surface and extruded along the line for
  the end-run length in the structure and in every standard, so the block's tetrahedra are the same in all of them;
  (c) as (b) with prisms instead of tetrahedra, if Palace accepts them. Report |ΔS| against the oracle and the
  **third-line residual** (a line length the calibration did not use, de-embedded, should be a matched line in Z_c).
  If (a) is within the oracle tolerance, the series uses (a) and says the measurement is why.
- **Q3 — C′.** Three sources, each turned into Z_c with the measured γ:
  (a) a 2D electrostatic solve of the wall's cross-section with the quasi-static kernel (`QuasiStaticKernel`,
  kernel A), which takes arbitrary 2D conductors and dielectrics; a conducting layer enters as a complex permittivity
  ε − jσ/ω, so C′ is recomputed per frequency where any layer conducts;
  (b) Palace's electrostatic solve of two standards, the capacitance differenced over their separation so the end
  effects cancel (the planar solver's D7);
  (c) the oracle's C′, on S and M only, as the reference.
  Report Z_c's error for (a) and (b) on S, M and C, and what each costs. **Whichever is chosen must give the same C′
  to every solver**, so a source that only one solver can provide needs a stated reason to be preferred.
  Kernel A's quasi-static Z_c is never the ORACLE in this series (it would make the comparison circular); where it is
  chosen as the source, the oracles above stay closed forms.
- **Q4 — End run.** On M and C, the end run at 1, 2, 3, 5 and 8 substrate heights. Report the third-line residual.
  Choose the default in substrate heights of the cross-section's tallest dielectric under the line.
- **Q5 — Silicon.** On Si, the calibrated wall port and a wave port on the same face at 10 frequency points from 1 to
  40 GHz. Record γ and Z_c from each and whether either jumps between neighbouring points. No oracle and no gate; this
  is the case the series exists for, and its numbers go into brief 7's README.
- **Q6 — Cost.** For each fixture, the standards' solve time and memory against the structure's own, and how many
  standards the plan asked for across 1–10 GHz and 1–40 GHz.

## Deliverables

- `src/Design/Em3d/RESOLVED.md` §PWL-2: every table above, the decision for each question, and the tolerance for
  gates 3 and 4 (the measured worst |ΔS| on S and C at the chosen settings, with a stated margin).
- Brief 3 amended in place where a decision changes it (its §Decisions from brief 2 lists what to fill in).
- `testdata/pwl/` with fixtures S and C.

**Stop and report** rather than continue if no combination of Q1–Q4 brings S within 1e-2 in |ΔS| of its exact closed
form at 10 GHz: the series would then need a different construction, and that is the owner's decision.

## Rules

Every Palace run under a memory watchdog; no run over a few minutes; three frequencies except Q5. The harness stays in
the session's scratch directory, not in `tools/` and not in the Benchmark tier.
