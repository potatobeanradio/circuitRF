# Brief 31 — the radiation pattern, from a 3D run

**Series:** [3D EM, second series](brief-em3d-20-overview.md) · **Tag:** `R-em3d31-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §3 (radiating structures: FDTD is the primary
solver, "far-field extraction" in §4) and §4.3 (FDTD's PML)
**Area:** `src/Design/Em3d/CsxcadWriter.cs`, `src/Design/Em3d/OpenEmsRun.cs`,
`src/Design/Em3d/PalaceConfigWriter.cs`, `src/Design/Em3d/Em3dRunService.cs`,
`src/Engine/Mom/PlanarFarField.cs` (the metrics stage, factored out — not the transform),
`src/Ui/Layout/Em/EmSetupEditorViewModel*.cs`, `src/Ui/Views/Layout/EmSetupEditorView.axaml`
**Depends on:** 9 (openEMS backend), 7/21 (Palace backend) · **Blocks:** —

---

## 0. Why this is a brief and not a checkbox rewire

The EM Setup panel's *Radiation pattern* group (ANT-12) sets `EmSetup.RadiationPattern` and
`ReferenceInputPowerDbm`, and the **planar** run turns them into the `farfield` group of its result
(`PlanarFarField.Group`): E_θ, E_φ, radiation intensity over the upper hemisphere on a 1° grid, then
directivity, the two gains, radiation efficiency, the power budget, per-plane beamwidth and the
polarization cubes. The Data Display plots that group as polar cuts or a 3D surface.

**Nothing on the 3D side reads either field.** `Em3dRunService` never looks at `RadiationPattern`,
neither backend asks its solver for a far field, and the transform the planar path uses cannot be
reused: it is a closed-form spectral transform of MoM rooftop currents over a layered medium
(R-ant-1), and a 3D run has no rooftop currents. So the group is **hidden** while a 3D solver is
chosen (the EM Setup cleanup that accompanied this brief). This brief puts it back, driving the same
`farfield` cubes from each 3D solver so the Data Display, the metrics and the CLI need nothing new.

---

## 1. `R-em3d31-1` — factor the metrics away from the transform

`PlanarFarField.Compute` does two things: (a) the MoM-specific transform to E_θ/E_φ on a
(θ, φ) grid, and (b) everything after — intensity, directivity, gains, efficiency, budget,
beamwidth, polarization, and the cube layout of the `farfield` group.

- Split (b) into a solver-agnostic stage that takes **a (θ, φ) grid of complex E_θ, E_φ at 1 m
  (or with its r normalisation stated), the frequency, the accepted power and the incident
  (available) power**, and emits the `farfield` group exactly as today.
- The planar path calls it with its own transform's output. **The planar result must be
  byte-identical before and after** — gate on a planar antenna fixture's `.npy`, compared cube by
  cube, before any 3D code is written (dump it FIRST, then refactor).
- Accepted power and available power come from the S-parameters the same run already produced
  (|a|²(1 − |S11|²) for a one-port; the multi-port rule the planar path already uses). The 3D stage
  must use the same rule, not re-derive it.

## 2. `R-em3d31-2` — openEMS: an NF2FF box

FDTD is the design note's primary solver for radiators; do this backend first.

- `CsxcadWriter` adds, when `RadiationPattern` is set, **frequency-domain E and H dump boxes** on a
  closed surface inside the PML — at least a few cells (state the number, and why) from every PML
  face and enclosing every conductor. A setup whose air box is too tight for that is a
  **refusal naming the air-box margin to raise**, never a silently truncated surface.
- A PEC floor (a ground plane that IS the box's zmin face) means the surface is open at the bottom
  and the pattern is the **upper hemisphere** — the same domain the planar path reports. Say so in a
  note. A structure with PML on every face gets the full sphere; the metrics stage must then
  integrate over the full sphere, and the cube's θ axis says which.
- Only the **driven port's run** is needed for the pattern of that excitation (openEMS runs once per
  port, R-em3d9-3a). Match the planar path's excitation convention (which port, which reference
  power) and state it in the result's provenance.
- The transform itself: either openEMS's own `nf2ff` program (check what the installed build ships
  and how its output is read — if it is HDF5, reading it is a **native dependency, which needs the
  owner's approval first**), or a C# surface-equivalence transform over the dumped tangential fields
  (the Love/Schelkunoff integral is elementary on a rectangular box). Measure both on the gate
  below and recommend one; do not add a native reader to find out.

## 3. `R-em3d31-3` — Palace: spike first

Palace's absorbing boundary is first order, so a far field from it needs the box far away (§4.3's
table says so). **Whether the installed Palace can post-process a far field at all has to be read
from its own configuration schema, not assumed.** Spike: run the installed Palace's dry run on a
configuration carrying the far-field request, and record what it accepts and what it writes.

- If it supports one: map `RadiationPattern` onto that request in `PalaceConfigWriter`, read what it
  writes, and feed §1's stage.
- If it does not: Palace reports the pattern **refused with a sentence naming FDTD 3D (openEMS)**
  as the solver that produces one, and the checkbox is disabled with that reason when Palace is the
  chosen solver. *FEM & FDTD - Compare* then produces the pattern from its openEMS half only, and
  says so.

## 4. `R-em3d31-4` — the panel

- Show the *Radiation pattern* group again for the 3D solvers that honour it
  (`ShowCircuitRfSolverControls` is what hides it today; give the group its own
  `ShowRadiationPattern` rather than widening that one).
- `RadiationPatternDisabledReason` gains the 3D reasons: Palace when §3 found no support, an air
  box too small for §2's surface, a static or eigenmode problem (nothing radiates in a driven sense).
- The CLI `em` verb picks it up from the `.cem` with no flag, as ANT-12's does.

## 5. Gates

- **Oracle, not self-agreement:** a half-wave dipole (or a short dipole) in free space — directivity
  1.64 (2.15 dBi) / 1.5 (1.76 dBi) — solved by openEMS through §2, within a stated tolerance.
- **Cross-solver:** one patch antenna from the shipped examples, planar vs FDTD 3D: broadside
  directivity and E-/H-plane beamwidths agree within a stated tolerance, measured and recorded.
- The planar byte-identity gate of §1.
- No new timing benchmark; the solver runs belong in `Category=Benchmark` if over ~5 s, with a
  routine counterpart that checks the **written** CSXCAD/Palace configuration (the dump box, its
  margin, the refusal) without running a solver.

## 6. Not in this brief

- Near-field plots (brief 29 covers field viewing).
- Arrays / pattern synthesis.
- A far field for static or eigenmode problems.
