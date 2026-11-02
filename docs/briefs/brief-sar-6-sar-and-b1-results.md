# Brief SAR-6 — SAR and B1± as results of a `.c3d` run

**Series:** `brief-sar-0-overview.md` (L1, L6, L7, L8) · **Tag:** `R-sar6-<m>`
**Depends on:** SAR-3, SAR-5
**Area:** the `.c3d` setup model (`Em3dSetup` / its openEMS group), `CsxcadWriter.cs` (dumps), `OpenEmsRun.cs`,
`C3dFieldPlots.cs` and `FieldPlotResolver` (`src/Render`), the results writer (`src/Design/Results`), the setup
editor and 3D view (`src/Ui`), `src/Cli` (`em`, `render --field`, `--list-fields`), the MCP reference topics,
`docs/design/sar.md` §8

---

## 1. Goal

A user asks a `.c3d` setup for SAR and B1 at a frequency, presses Simulate, and gets:
- local, 1 g and 10 g SAR, and B1+ and B1−, as field plots;
- the peak 1 g and 10 g SAR with markers where they occur;
- whole-volume SAR, absorbed power and the power budget as readouts;
- all of it in the run's `.npy`, from the CLI and MCP exactly as from the GUI.

## 2. Requirements

**R-sar6-1 — The setup's `Sar` group.**
- `Frequencies` (one or more, each inside the sweep; L6 means each also evaluates the materials, so a setup with
  dispersive tissue and two SAR frequencies is **refused**, naming the second-run alternative);
- `Masses` (default `[1, 10]` g) and `Method` (`Cube` default, `Simple`);
- `Region` (default: every tissue cell);
- `B1` (bool, default on for a frequency below 500 MHz) and `B0Axis` (default `+z`);
- `CrossCheck` (SAR-5).

A setup with the group turns on `CellConstantMaterial` (SAR-3), and the run's notes say so.

**R-sar6-2 — What is dumped.** Per SAR frequency: the raw SAR dump (`DumpType 29`, HDF5, cell interpolation),
cropped to the bounding box of the lossy solids plus one cell. For B1: an H dump (`DumpType 11`) over the same box at
cell centres, as VTK, so it matches the existing E-field path. Both use frequency-domain oversampling (the existing
`OverSampling` reason). **Disk use is estimated before the run** in `Em3dMemory`'s style, and a run over the
existing size warning says so.

**R-sar6-3 — Normalisation (L7).** Every SAR and B1 result is stored as solved and shown normalised. The default is
**1 W accepted** at the driven port; any power is set through brief-em3d-100's `DrivePowerW` and `DriveReferredTo`. SAR
scales with power, B1 with its square root, and the readout names the normalisation. A setup with more than one port
uses the port the run drove.

**R-sar6-4 — B1± (L8).** From H at cell centres: B = μ0·H, then B1± = (Bx ± jBy)/2 for `B0Axis = +z`, with the
axes permuted for `±x` and `±y` and the sign swapped for a negative axis. The design note's derivation is the
comment's reference.

**R-sar6-5 — Field plots.** New quantities in `C3dFieldPlot.Quantity`: `SAR` (local), `SAR1g`, `SAR10g` (any mass in
`Masses`, named `SAR<m>g`), `B1p`, `B1m` (magnitude; µT, or µT/√W when normalised). Clip planes and surfaces work as
for E. A **maximum-intensity projection** along an axis is new and is offered for SAR quantities only. A **peak marker**
is drawn at the peak averaging cube, as its wireframe, with the value. The colour scale is linear in W/kg by default
(`Db` off) with the existing percentile top. Air cells are transparent.

**R-sar6-6 — Readouts.** The run's result panel shows, per SAR frequency:
- psSAR1g and psSAR10g, with location and the tissue the peak lies in;
- whole-region SAR, absorbed power and tissue mass;
- accepted power and, when a far-field box exists, radiated power;
- the power budget as a percentage of accepted power;
- for B1, the mean |B1+| in a region the user names (default: the phantom's innermost layer) and the SAR efficiency
  |B1+|mean/√psSAR10g;
- under `CrossCheck`, the `sar_calc` peak beside circuitRF's.

No explanatory text under the values; a note only when a value is missing (owner preference).

**R-sar6-7 — `.npy` and DataSet.** Scalars go in as cubes over the SAR-frequency axis in the run's grouped `.npy`
(`sar` group): `psSAR1g`, `psSAR10g`, `SARwb`, `Pabs`, `Pacc`, `Prad`, `B1pMean`, `SarEff`. Volumes stay in their own
files beside it (`<run>.sar10g.npy` …, float32, with mesh-line files), referenced by name, never inlined into the
DataSet. The Data Display can plot the scalars like any cube.

**R-sar6-8 — Headless.** `circuitrf em` produces the same files. `render x.c3d --field SAR10g` and `--list-fields`
include the new quantities, and `render` gains `--mip <axis>` for SAR. `explain` reports the `Sar` group, the evaluated
tissue values and the estimated dump size. MCP's `reference em-setup` documents every key the day it exists.

## 3. Not in this phase

Recombination of port fields (SAR-9); voxel phantoms (SAR-11).

## 4. Gates (minimal)

- `SarSetupTests`: the group round-trips; two SAR frequencies with dispersive tissue are refused; the dump boxes are
  cropped as specified.
- `B1ConventionTests`: a circularly polarised H constructed by hand gives |B1+| = |B| and B1− = 0 for +z, and the
  reverse for −z.
- `SarResultsWiringTests`: SAR-1's dipole raw dump, run through the result path (no openEMS process), gives the `.npy`
  scalars SAR-5 computed, and `render --field SAR10g` writes a PNG.
