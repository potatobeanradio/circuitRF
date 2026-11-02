# Brief series — SAR and B1 for MRI coil designers and phone SAR, on openEMS (SAR-1 … SAR-13)

**Status:** briefs written 2026-10-17, nothing implemented · **Decisions:** O1–O6 settled by the owner 2026-10-17 (§6) · **Branch:** `sar` — **local only, never pushed** (a
pre-push hook refuses it); it is released by the owner, when it has been tested significantly, by a merge into `main`
**Requirement tag:** `R-sar<n>-<m>` (phase n, requirement m).
**Design note:** `docs/design/sar.md` — new; SAR-1 opens it with the openEMS reference set, every later phase adds a
section.
**Builds on:** the 3D EM series (`docs/design/em-3d.md`), above all the openEMS backend (`CsxcadWriter`, `OpenEmsRun`,
`FdtdGrid`), the `.c3d` document and its field plots (`C3dFieldPlots`, brief-em3d-100's drive power), the technology
materials and `.cmat` libraries (em-3d.md §4.1a), the tuning and optimizer series (TO), and the yield series (YA).

---

## 0. What the owner asked for (paraphrased)

openEMS v0.37.0 reworked its SAR calculation to follow IEC/IEEE 62704-1. The owner wants circuitRF to make SAR easy
to set up, simulate, plot and analyse, and to be useful to the MRI RF research and development community. **First
audiences: MRI RF coil designers, and phone SAR.** **openEMS first**, and the first proof is that circuitRF reproduces
openEMS's own published SAR and MRI examples. This is a **research and engineering** project. **Nothing is bought**:
no licence, no anatomical model and no standard.

## 1. Why circuitRF, in one paragraph

An MRI transmit coil is a resonant conductor with tens of tuning, matching and decoupling capacitors, next to lossy
tissue. Designing one is a circuit problem and a field problem at the same time. Today that work is done with an EM
solver plus hand-written scripts that recombine its port fields with a circuit solution (the "co-simulation" method).
circuitRF already has the schematic, S-parameters, the optimizer, yield, the 3D setup editor and openEMS. **What it
lacks is lossy-tissue materials, phantoms, SAR and B1 as results, and the recombination of port fields by a circuit
solution (SAR-9).** That last part is the differentiator. It also carries over to phased arrays, antennas with matching
networks and lumped-tuned resonators.

---

## 2. What openEMS v0.37.0 gives us (surveyed 2026-10-17)

- **SAR is post-processing, not an engine feature.** A raw SAR dump (`DumpType 29`, HDF5) carries the E field, cell
  volume, conductivity and density per cell. A separate GPL executable, `sar_calc`, averages it
  (`--method IEEE_62704 | IEEE_C95_3 | SIMPLE`, `--mass <g>`, `--autorange <dB>`, `--numThreads`). Python's
  `SAR_Calculation` and Octave's `CalcSAR.m` wrap the same C++ code. Dump types 20/21/22 (local, 1 g, 10 g) also exist.
- **SAR needs `CellConstantMaterial=1`.** The tutorials say so: the cell-averaged material model must be off for
  averaging per 62704-1. circuitRF's writer does not set it today.
- **Examples:** `Dipole_SAR` (Python and Octave), `MRI_Loop_Coil` (Python and Octave), `MRI_LP_Birdcage` (Octave
  only), and analytic SAR unit tests. Bundled fallback phantoms: three nested ellipsoids (head, 298 MHz) and a layered
  cylinder (body, 128 MHz), at 2.5 mm voxels, as `DiscMaterial` HDF5 files.
- **Other things in v0.37.0 that matter here:** series and parallel lumped RLC elements, frequency-domain dump
  oversampling, `--exact-endcriteria`, graceful Ctrl-C. The MPI engine is removed.
- **circuitRF's validated openEMS is 0.37.0-rc3** (`SolverDiscovery`). SAR-1 moves it to 0.37.0 final.
- **circuitRF reads openEMS fields as VTK**, but it already ships a managed, MIT HDF5 library: **PureHDF 2.1.2**,
  in `src/RfCore` for the `.mat` export (`MatWriter.cs`). The raw SAR dump and `DiscMaterial` voxel files are HDF5
  only, so SAR-4 reuses PureHDF to read and write them. (`OpenEmsFarField.cs`'s header says there is no reader; that is
  stale, and SAR-4 corrects it.)

---

## 3. The phases

| Phase | Brief | Delivers | Depends on |
|---|---|---|---|
| SAR-1 | `brief-sar-1-openems-reference-set.md` | openEMS 0.37.0 final validated; openEMS's own examples run unmodified and their results committed as reference data; design note opened | — |
| SAR-2 | `brief-sar-2-tissue-materials.md` | Frequency-dependent tissue materials (Gabriel 1996 Cole-Cole) and phantom liquids, evaluated at the analysis frequency | — |
| SAR-3 | `brief-sar-3-phantoms-lumped-elements-setup.md` | Ellipsoids and layered parametric phantoms in `.c3d`; a lumped RLC element; the FDTD setup keys replication needs (cell-constant material, explicit grid lines) | SAR-2 |
| SAR-4 | `brief-sar-4-hdf5-via-purehdf.md` | openEMS's HDF5 read and `DiscMaterial` written, through the PureHDF circuitRF already ships | — |
| SAR-5 | `brief-sar-5-sar-engine.md` | circuitRF's own SAR: local, 1 g / 10 g averaged, whole-body, absorbed power, the power budget; `sar_calc` as a cross-check | SAR-4 |
| SAR-6 | `brief-sar-6-sar-and-b1-results.md` | SAR and B1± as run results: field plots, peak markers, readouts, `.npy`, CLI and MCP | SAR-3, SAR-5 |
| SAR-7 | `brief-sar-7-replicate-dipole-sar.md` | `Dipole_SAR` rebuilt in circuitRF, matching SAR-1's reference (the phone-SAR gate) | SAR-6 |
| SAR-8 | `brief-sar-8-replicate-mri-loop-coil.md` | `MRI_Loop_Coil` rebuilt in circuitRF, matching SAR-1's reference (the coil gate) | SAR-6 |
| SAR-8b | `brief-sar-8b-replicate-birdcage.md` | `MRI_LP_Birdcage` on circuitRF's cylindrical grid: lumped elements, ports, given lines and SAR there; the quadrature feed by superposition (moved out of SAR-8, owner 2026-10-18) | SAR-8 |
| SAR-9 | `brief-sar-9-port-field-cosimulation.md` | Capacitors as ports, per-port fields, the circuit's solution recombining them into B1 and SAR | SAR-8 |
| SAR-10 | `brief-sar-10-field-goals-tuning-yield.md` | B1 and SAR metrics as tuning, optimizer and yield goals; multi-channel drive (RF shimming) | SAR-9, TO, YA |
| SAR-11 | `brief-sar-11-voxel-and-mesh-import.md` | Segmented voxel models (NIfTI, raw) and per-tissue surface meshes into `.c3d` | SAR-3, SAR-4 |
| SAR-12 | `brief-sar-12-phone-sar-workflow.md` | Phone SAR: flat and head phantoms, device placement, 1 g and 10 g against published limits, normalised to rated power | SAR-7 |
| SAR-13 | `brief-sar-13-acceptance-examples-docs.md` | The numbers against §4, two example workspaces, user pages, design note, RESOLVED.md | all |

SAR-2 and SAR-4 have no dependencies and can run in parallel with SAR-1. **SAR-7 and SAR-8 are the series' gate.** If
they cannot reproduce openEMS's own numbers, nothing after them proceeds until the cause is found and written down.

---

## 4. Acceptance — what "good" means

Settled by the owner as proposed (O4, 2026-10-17). Widening any of them needs the owner.

- **The SAR engine (SAR-5)**, on openEMS's own raw dump, against `sar_calc` on the same file: local SAR equal to
  1e-5 relative; the 10 g and 1 g peak spatial-average SAR within **0.5 %**, and every averaged cell within **1 %**
  on ≥ 99 % of cells. On analytic cases (a uniform block; a layered slab under a plane wave), exact to the stated
  tolerance.
- **`Dipole_SAR` (SAR-7), on the reference's own grid lines:** |S11| within 0.01 (linear) across the band; Zin at
  f0 within 1 %; accepted power within 1 %; psSAR10g per watt within **2 %**; whole-phantom SAR within 2 %; the power
  budget (radiated + absorbed) / accepted between 95 % and 100 %, as the tutorial prints it.
- **`Dipole_SAR` on circuitRF's own grid:** psSAR10g per watt within **5 %** of the reference, and the difference
  explained in the design note.
- **`MRI_Loop_Coil` (SAR-8), on the reference's grid:** S11 minimum frequency within 0.5 MHz and depth within 1 dB;
  B1+ per √W at the phantom centre within 3 %; peak local SAR per watt within 5 %; the B1+ and SAR planes correlated
  ≥ 0.99 with the reference's.
- **Co-simulation (SAR-9):** the loop coil with its three capacitors replaced by ports and put back by the schematic
  reproduces SAR-8's lumped-capacitor run: B1+ within 2 %, psSAR10g within 3 %. **Recombination after a capacitor
  change needs no EM run**, and that is held by a counter (no openEMS process started), not a timing test.
- **No regression:** every existing openEMS gate (`testdata/em3d/f0` and the em3d test classes) stays green after the
  move to 0.37.0 final.

---

## 5. Decisions locked by this brief

- **L1 — Research and engineering use.** No page, report or comment claims compliance with any standard. The wording
  is "follows the cube-averaging procedure of IEC/IEEE 62704-1 as openEMS implements it, and agrees with openEMS's
  `sar_calc` to the tolerance in §4". Where a regulatory limit is shown (SAR-12), it is labelled as a reference value.
- **L2 — Nothing bought, nothing restricted committed.** No purchased licence, model or standard. A free model under
  a non-commercial or no-derivatives licence may be used **on a developer's machine only**, git-ignored, and is never
  shipped, converted for distribution or named in the repo. Without the standard's own benchmark data, the SAR engine
  is checked against analytic cases and against `sar_calc`.
- **L3 — No GPL code copied.** openEMS and its tools are run as external processes, under em-3d.md §5.3's contract.
  Their scripts, C++ and phantom `.h5` files are read to learn from and never copied. circuitRF's phantoms are built
  from their own published geometry (nested ellipsoids and cylinders are physical facts, not code).
- **L4 — References are externally generated.** SAR-1 runs openEMS's examples **unmodified** and commits what they
  produce as fixed data, under the repository's rule for references (CLAUDE.md §Validation expectations).
- **L5 — openEMS first, solver-neutral model.** The SAR engine works on a Cartesian grid of E, σ and ρ, and B1 on H.
  Nothing in it knows which solver wrote them, so a later Palace series feeds the same engine after resampling.
- **L6 — One SAR frequency per material evaluation.** Tissue properties are evaluated at the setup's SAR frequency
  (SAR-2). An openEMS run is broadband, but its tissues are constant, so its SAR and B1 are valid at that frequency
  only, and the result says so. Broadband tissue (Debye fits) is not in this series.
- **L7 — Units.** SAR in W/kg; by default normalised to **1 W accepted** at the driven port, as both openEMS examples
  normalise; B1 in µT, and per √W accepted. The brief-em3d-100 drive-power setting rescales both.
- **L8 — The B1 convention is openEMS's.** With the e^{jωt} phasor and B0 along +z, B1+ = (Bx + jBy)/2 and
  B1− = (Bx − jBy)/2, exactly as `MRI_Loop_Coil` computes them. The B0 axis is a setup key (default +z); a B0 along −z
  swaps the two. The design note derives it once.
- **L9 — No vendor or product names** in any brief, page, test or comment (CLAUDE.md).

## 6. Owner decisions (settled 2026-10-17)

All six settled by the owner on 2026-10-17; each phase applies its own.

- **O1 — HDF5 (SAR-4). Settled 2026-10-17: reuse PureHDF 2.1.2**, which `src/RfCore` already references for the
  `.mat` export. No HDF5 code of our own, no new dependency.
- **O2 — Tissue data (SAR-2).** Ship tissue properties computed from the **published Gabriel 1996 parametric model**,
  implemented ourselves, plus an importer for a tissue table the user has downloaded themselves. The widely used
  online tissue database's redistribution terms are unverified, so its data is not shipped.
- **O3 — `sar_calc` alongside ours (SAR-5).** circuitRF's own engine produces the number. When openEMS's `sar_calc`
  is installed, a setup key runs it on the same dump, and the result reports both and their difference.
- **O4 — The tolerances in §4.** Accepted as proposed.
- **O5 — The birdcage (SAR-1, SAR-8).** `MRI_LP_Birdcage` is Octave-only. **Octave is installed on the developer
  machine** (dev-only, never a circuitRF dependency) to generate its reference, and the birdcage is in the reference
  set.
- **O6 — The co-simulation's home (SAR-9).** **A schematic** places the `.c3d`'s S-parameter block, and the per-port
  fields are carried beside it, so a schematic analysis recombines them. There is no coil network inside the `.c3d`.

## 7. Risks, said up front

- **HDF5 (SAR-4).** Small now that PureHDF is reused. The remaining risk is that CSXCAD reads `DiscMaterial` files
  more strictly than a generic HDF5 reader does, which is why CSXCAD itself is that phase's gate.
- **Field size (SAR-9, SAR-11).** A whole body at 2 mm is about 34 M cells. One port's E and H at single precision is
  about 1.6 GB, and a multi-port coil multiplies that. Fields are stored per port, cropped to the lossy region, as
  float32, read memory-mapped, never as one in-memory `DataCube`.
- **Agreement on the grid.** circuitRF's grid generator and openEMS's `SmoothMeshLines` place different lines. That
  is why SAR-7 and SAR-8 gate on the reference's own grid lines first, and on circuitRF's own grid second.
- **High-Q ringing.** A lumped-capacitor coil rings for a long time in FDTD. SAR-8 runs it as the tutorial does; SAR-9's
  ports-instead-of-capacitors removes the problem by construction.
- **Audience.** A specialist community; the owner has chosen it deliberately.

## 8. Not in this series

Palace and FEM SAR (IEC/IEEE 62704-4); Pennes bioheat and thermal dose (the thermal solver can take SAR as a source
later); parallel-transmit Q-matrices and virtual observation points beyond SAR-10's channel weights; implant lead
heating; absorbed power density above 6 GHz; anatomical models that can be posed or morphed; broadband dispersive
tissue in FDTD; any compliance report.
