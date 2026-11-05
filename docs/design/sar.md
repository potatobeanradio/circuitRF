# circuitRF — SAR and B1 for MRI coil designers and phone SAR

**Status:** SAR-1 done (§1–§3), SAR-2 done (§4), SAR-3 done (§5), SAR-4 done (§6), SAR-5 done (§7), SAR-6 done (§8), SAR-7 done (§9), SAR-8 done (§10; the birdcage moved to SAR-8b), SAR-9 done (§11), SAR-10 done (§12), SAR-11 done (§13), SAR-12 done (§14), SAR-13 done (§15: acceptance) · **Series:** [`brief-sar-0-overview.md`](../briefs/brief-sar-0-overview.md) ·
**Reference data:** [`testdata/sar/openems-ref/`](../../testdata/sar/openems-ref/README.md) · **Builds on:**
[`em-3d.md`](em-3d.md) (the openEMS backend) and [`em-3d-f0-findings.md`](em-3d-f0-findings.md) (its validated versions).

This note is for research and engineering use (overview L1). Nothing here claims compliance with any standard: the
averaging **follows the cube-averaging procedure of IEC/IEEE 62704-1 as openEMS implements it**, and later phases
agree with openEMS's `sar_calc` to the tolerances in overview §4.

---

## 1. What SAR, psSAR and B1± are

### 1.1 Local SAR

The specific absorption rate is the power dissipated in tissue per unit mass. For a time-harmonic field given as a
**peak** phasor **E** (openEMS's frequency-domain dumps are peak amplitudes), in a cell of conductivity σ (S/m) and
density ρ (kg/m³):

```
SAR = σ |E|² / (2 ρ)          W/kg          (|E|² = |Ex|² + |Ey|² + |Ez|²)
```

The ½ is the time average of a peak phasor; with an RMS phasor it is `σ|E|²/ρ`. openEMS's `sar_calc` computes the
local power density `½ σ|E|²` per cell, zeroes it where ρ = 0 (air), and divides by ρ. `testdata/sar/openems-ref/
analytic/` holds the check: σ 0.5 S/m, ρ 1000 kg/m³, |Ex| 1 V/m peak gives 2.5e-4 W/kg in every cell.

### 1.2 Absorbed power, mass, whole-body SAR

Over the cells i of a region, with cell volume Vᵢ:

```
P_abs = Σ ½ σᵢ |Eᵢ|² Vᵢ  = Σ SARᵢ ρᵢ Vᵢ          (W)
m     = Σ ρᵢ Vᵢ                                   (kg, cells with ρ > 0)
SAR_wb = P_abs / m                                (W/kg; "whole-phantom" for a phantom)
```

`sar_calc` writes `power` and `mass` as attributes of its result file. `Dipole_SAR` prints `power / P_acc / mass` as
its "whole body SAR".

### 1.3 Peak spatial-average SAR (psSAR1g, psSAR10g)

psSAR_m is the largest average of local SAR over a **cube of mass m** (1 g or 10 g of tissue). The average over a
cube is mass-weighted:

```
SAR_avg(cube) = Σ SARᵢ ρᵢ Vᵢ,cube / Σ ρᵢ Vᵢ,cube
```

where Vᵢ,cube is the part of cell i inside the cube (cells cut by its faces count in part). How the cube is chosen is
the procedure, and is what SAR-5 reproduces. As openEMS implements it (`tools/sar_calculation.cpp`, read to learn
from, never copied, overview L3), per voxel:

1. a cube **centred on the voxel** is grown until its tissue mass equals m. `IEEE_62704` allows a mass error of 1e-6
   relative and up to 100 iterations;
2. the cube is **valid** if, in addition, its air (background) fraction is below **10 %** and none of its faces lies
   entirely in air. A valid cube's average is that voxel's averaged SAR;
3. a tissue voxel that is neither the centre of a valid cube nor inside one is **unused**. For it, a cube of the
   right mass is built with one face on the voxel, in each of the six directions, and it takes the largest of their
   averages among those whose volume is within 5 % of the smallest. A direction with no such cube contributes zero,
   and `sar_calc` prints `can't find fitting cubical averaging volume` for it. `analytic/random_sar_calc.log` shows
   that line repeatedly, because a 1 g cube does not fit everywhere in an 8 g phantom.

`IEEE_C95_3` is the older procedure: 5 % mass tolerance, no air limit, partial cubes counted as used. `SIMPLE` is the
local SAR only. The result file counts `valid_cubes`, `used_cubes`, `unused_cubes` and `air_cubes`.

### 1.4 Normalisation (overview L7)

Every SAR, power and B1 value openEMS writes is at the run's excitation, which is arbitrary and tiny
(`Dipole_SAR` accepts 1.6e-27 W). The tutorials normalise to **1 W accepted** at the feed: SAR per watt is
`SAR / P_acc`, B1 per √W is `B1 / √P_acc`, with `P_acc = ½ Re{V I*}` at the port at f0 (`port.P_acc`). circuitRF keeps
that default (L7).

### 1.5 B1+ and B1− — derived once (overview L8)

openEMS uses the phasor convention `b(t) = Re{B e^{jωt}}`, with **B** = μ₀**H**. Take B0 along **+z** and split the
transverse phasor (Bx, By) into two circularly polarised parts:

```
Bx x̂ + By ŷ  =  B1+ (x̂ − j ŷ)  +  B1− (x̂ + j ŷ),     B1+ = (Bx + j By)/2,   B1− = (Bx − j By)/2
```

(check: x̂ (B1+ + B1−) + j ŷ (B1− − B1+) = x̂ Bx + j ŷ (−j By) = x̂ Bx + ŷ By). In time:

```
Re{B1+ (x̂ − j ŷ) e^{jωt}} = |B1+| ( x̂ cos(ωt+φ₊) + ŷ sin(ωt+φ₊) )    rotates from +x toward +y
Re{B1− (x̂ + j ŷ) e^{jωt}} = |B1−| ( x̂ cos(ωt+φ₋) − ŷ sin(ωt+φ₋) )    rotates from +x toward −y
```

So |B1+| is the amplitude of the part rotating counter-clockwise seen from +z, and |B1−| that of the part rotating
clockwise. **This series takes openEMS's definition as it stands** (L8): it is exactly what `MRI_Loop_Coil.py` and
`MRI_LP_Birdcage.m` compute. Which of the two is the transmit field depends on the sign convention for precession, and
the literature uses both; circuitRF does not re-derive that choice. A B0 along **−z** swaps the two (the setup key
SAR-6 adds). The birdcage computes Bx, By from its cylindrical Br, Bα first: `Bx = Br cos α − Bα sin α`,
`By = Br sin α + Bα cos α`.

---

## 2. What openEMS 0.37.0 offers (surveyed 2026-10-17, run on 0.37.0)

- **Dump types** (CSXCAD's `AddDump`): 10/11 the E/H field in the frequency domain; **20** local SAR, **21** 1 g and
  **22** 10 g averaged SAR (frequency domain); **29** the **raw SAR data**: the E-field phasor, the cell volume, the
  conductivity and the density per cell, for `sar_calc` to average afterwards. File type 1 is HDF5; dump mode 2 is
  cell interpolation (values at cell centres, `dual_mesh = 1` in the file).
- **The raw dump** (`Dipole_SAR`'s `SAR.h5`, read with h5py): `/Mesh/{x,y,z}` the cell centres and `/CellWidth/{x,y,z}`
  their widths (float64, in the drawing unit's metres); `/CellData/{Conductivity,Density,Volume}` float32 (nx, ny,
  nz); `/FieldData/FD/f0` the E phasor, (3, nx, ny, nz) complex64 stored as an `r`/`i` compound; the frequency as an
  attribute of `/FieldData/FD`; file attributes `dump_type = 29`, `dual_mesh = 1`, `openEMS_HDF5_version = 0.3`.
  83 × 90 × 91 cells is **24.5 MB**.
- **`sar_calc`** (the GPL executable beside `openEMS`): `-i <raw.h5> -o <out.h5> --method IEEE_62704|IEEE_C95_3|SIMPLE
  --mass <g> [--autorange <dB>] [--numThreads n] [--export_cube_stats] [--legacyHDF5Dumps]`. `--method` defaults to
  `SIMPLE`; `--mass 0` gives local SAR. Its output carries the averaged SAR and the attributes `maxSAR`, `maxSAR_idx`,
  `power`, `mass`, `frequency` and the four cube counts. **Its exit status is inverted**: `main` returns
  `CalcFromHDF5`'s bool, so **1 is success and 0 failure**. SAR-5's runner must judge by the output file, as
  `testdata/sar/openems-ref/tools/run_analytic.py` does. Python's `SAR_Calculation(mass, method).CalcFromHDF5(in, out)`
  and Octave's `CalcSAR.m` wrap the same C++; `openEMS.sar_utils.readSAR` reads a result.
- **`CellConstantMaterial`** (an FDTD setting, on in all three tutorials): the cell takes the material at its centre
  rather than an average of its edges. The tutorials say it is less accurate and required for averaging per 62704-1.
  circuitRF's writer does not set it today (SAR-3).
- **Lumped elements**: `AddLumpedElement(name, ny, R|L|C)`, series or parallel (`MRI_Loop_Coil`'s three 5.4 pF
  capacitors; the birdcage's 2.6 pF). **`--exact-endcriteria`** evaluates the energy end criterion every Nyquist
  interval rather than every few seconds of wall-clock time (§3.2). `DiscMaterial` reads a voxel material from HDF5
  (the bundled phantoms). Graceful Ctrl-C; no MPI engine.

---

## 3. The reference set (SAR-1)

openEMS's own tutorials at the `v0.37.0` tag, run unmodified on openEMS 0.37.0 (`305df56`, CSXCAD `c46e320`) on an
Apple M4, and kept as fixed data **on the developer machine, not in the repository** (owner, 2026-10-18): the
repository holds the README (files, commands, versions) and the wrappers that made it, and `SarReferenceSetTests`
skips with a reason where the data is absent. Files, commands and versions: `testdata/sar/openems-ref/README.md`.

### 3.1 The numbers

**`Dipole_SAR`** (1 GHz, 0.48 λ dipole, 50 Ω feed, 20 mm from a three-layer ellipsoid head; 1.52 M cells, stopped by
the end criterion at step 2,272, 28 s):

| Quantity | Value |
|---|---|
| psSAR10g per W accepted | **2.435 W/kg** |
| whole-phantom SAR per W | **0.1509 W/kg** (phantom 4.348 kg) |
| radiated / absorbed / accepted | 32.3 % / 65.6 % / 100 % |
| **power budget** (radiated + absorbed) / accepted | **97.9 %** (the tutorial expects 95–100 %) |
| Zin at f0 | 53.14 + j3.73 Ω |

**`MRI_Loop_Coil`** (7 T, 298 MHz, 80 × 60 mm loop, three 5.4 pF capacitors, 2.5 Ω feed, beside the bundled ellipsoid
head phantom; 3.04 M cells, 77,409 steps, 266 s):

| Quantity | Value |
|---|---|
| S11 minimum | **−24.35 dB at 298.0 MHz** (on the script's 1.2 MHz grid) |
| Yin at f0 | 0.3955 + j0.0481 S |
| \|B1+\| at the phantom centre (x = y = z = 0) | **0.249 µT/√W** |
| peak local SAR per W, axial / sagittal plane | **5.67 / 5.09 W/kg** |

**`MRI_LP_Birdcage`**: see §3.3. **Analytic**: `test_sar_analytic.py`'s six tests pass on the installed build; the
uniform phantom gives 2.5e-4 W/kg exactly (to float32).

### 3.2 Run-to-run spread (R-sar1-5)

`Dipole_SAR` five times on the same machine (`Dipole_SAR/run-to-run.json`):

| Run | Stopped at step | Energy at stop | psSAR10g /W | Whole-phantom /W | Budget |
|---|---|---|---|---|---|
| 1 (the reference) | 2,272 | −46.15 dB | 2.4350 | 0.15095 | 97.93 % |
| 2 | 1,972 | −43.91 dB | 2.4408 | 0.15045 | 97.70 % |
| 3 | 1,988 | −44.31 dB | 2.4408 | 0.15061 | 97.76 % |
| 4, `--exact-endcriteria` | 1,775 | −41.50 dB | 2.4378 | 0.14992 | 97.39 % |
| 5, `--exact-endcriteria` | 1,775 | −41.50 dB | 2.4378 | 0.14992 | 97.39 % |

**The unmodified runs are not identical**, and the cause is the stop step, not the arithmetic: openEMS checks the
energy end criterion every few seconds of **wall-clock** time, so the step at which it stops depends on how loaded the
machine is. Across runs 1–3: psSAR10g **0.24 %**, accepted power **1.3e-5**, every 10 g cell within **0.29 %** of the
peak, |ΔS11| up to **9.4e-4**. With `--exact-endcriteria` the check runs every Nyquist interval (71 steps here) and
**the two runs are bit-identical**, in every array.

**For SAR-7:** the 0.24 % spread is the floor under overview §4's 2 % psSAR10g tolerance, and it is a property of the
reference, not of circuitRF. A circuitRF run of the same problem stops at its own step, so its difference from run 1
includes up to that spread. Running circuitRF's side with `--exact-endcriteria` makes it repeatable; it does not make
it equal to run 1, which was not.

The same effect appears in the F0 re-run on 0.37.0 (`em-3d-f0-findings.md` §6): the three short cases stopped at a
different step from rc3 and moved by up to 3.6e-6 in |S|, while the long ones, whose stop step matched, are
bit-identical.

### 3.3 MRI_LP_Birdcage

3 T (128 MHz) 8-rung low-pass birdcage, r 120 mm, 2.6 pF capacitors, driven in quadrature at two rungs (1 and −j,
the second as a quarter-period delay), inside a PEC bore shield, around the bundled layered-cylinder body phantom.
4.02 M cells on a cylindrical grid with sub-grids at 10 and 20 mm; the time step is 3.65e-13 s, set by the small cells
near the axis; the end criterion (−40 dB) stopped it at step 225,918, after **53 min** (288 MC/s; the engine chose 4
threads). The script's comment estimated 7 h at 65 MC/s.

| Quantity | Value |
|---|---|
| S11 minimum (both ports driven) | −3.68 dB at 127.5 MHz (0.75 MHz grid) |
| mean \|B1+\| at the smallest dumped radius (1.9 mm), per √W accepted at both ports | **0.498 µT/√W** (derived) |
| mean \|B1+\| / mean \|B1−\| over the plane (r ≤ 0.1 m) | 3.8 |
| peak local SAR on the plane, per W | **0.340 W/kg** (derived) |

The script normalises nothing; the two values marked derived are the converter's (`tools/birdcage_to_npy.py`),
by the other tutorials' rule. Neither SAR-7 nor SAR-8 gates on the birdcage: it is the reference for a later
birdcage replication and a check that a cylindrical, multi-port, quadrature-driven setup reads back as expected.

Two things it showed about the Octave route: `ReadHDF5Dump` compiles its HDF5 attribute reader (an `.oct` file) on
first use, into the openEMS install, which needs the Homebrew HDF5 headers; and Octave buffers the solver's console
output until it exits, so the run's progress cannot be followed from its log.

### 3.4 What the set does not hold, and why

- **Any of it in the repository.** The whole set, the raw dump-type-29 file of `Dipole_SAR` (24.5 MB) included, is
  on the developer machine only (owner, 2026-10-17 for the raw file, 2026-10-18 for the rest). SAR-5's comparison
  against `sar_calc` on the same file, and every later gate on these numbers, skips with a reason where it is absent.
- **The bundled phantom `.h5` files** (L3). The loop's XML names `phantom_head_298MHz.h5` under `${OPENEMS_INSTALL}`.
- **The FDTD settings as XML.** CSXCAD's `Write2XML` writes the structure and the grid; the excitation, boundaries and
  end criterion are in the scripts, and the README tables them.
- **S11 finer than the script's frequency step.** `MRI_Loop_Coil`'s vector steps 1.2 MHz, coarser than SAR-8's
  0.5 MHz gate on the S11 minimum, so the port's time-domain probes are kept and SAR-8 evaluates S11 from them.

---

## 4. Tissue materials, evaluated at one frequency (SAR-2)

### 4.1 The model

A body tissue's εr and σ move strongly with frequency (muscle σ is 0.69 S/m at 64 MHz and 0.98 S/m at 1 GHz), so a
tissue is not one constant pair. A material record (`TechMaterial`, em-3d.md §4.1a) may state one of two optional
dispersions, both additive and nullable, with no `FormatVersion` bump:

- **`ColeCole`** — `EpsInf`, `SigmaIonic` (S/m) and one to four `Terms` of `DeltaEps`, `TauSeconds`, `Alpha`, giving
  ε̂(ω) = ε∞ + Σ Δεₙ / (1 + (jωτₙ)^(1−αₙ)) + σᵢ/(jωε₀), the form of Gabriel, Lau and Gabriel (1996). With the e^{jωt}
  convention εr = Re ε̂ and σ = −ωε₀ Im ε̂, the **total** conductivity: ionic conduction and every dispersion's loss.
  The formula is `CircuitRF.Engine.Em3d.ColeCole` (numbers only). τ is stored in seconds, never in the paper's
  ps/ns/µs/ms columns.
- **`DielectricVsFrequency`** — target (εr, σ) rows at named frequencies, the way a tissue-simulating liquid is
  specified: interpolated linearly between the two nearest rows and **refused outside them**, never extrapolated.

`MaterialDispersion` (`src/Design/Layout`) is the one rule. The elaborator, `check`, `explain --materials` and the
material editor all call it. A record with either form is a **dielectric by declaration**: it states εr and σ₂₀, which
on their own would make it ambiguous, but its σ is a dielectric's loss at one frequency, never a metal's. So an object
made of it needs no Role (`C3dMaterialRole.Implied`).

**The plain numbers stay.** A dispersive record still states `Epsr` and `Sigma20`, its model's values at 1 GHz, which
is what a build without the model reads (it keeps the model's keys in `Unread` and writes them back). `check` warns,
as `tech.material.model-disagrees`, when they differ from the model at 1 GHz by more than 1 %, or when one is missing.
This is em-3d.md §4.1a's rule for an edited number that nothing reads.

`PerfusionRate` (1/s, Pennes' ω_b) and `MetabolicHeat` (W/m³, Pennes' Q_m) are stored for a later bioheat series. Nothing
reads them yet.

### 4.2 Where the frequency comes from (overview L6)

Every dispersive material in a 3D problem is evaluated **once, before lowering**, at one frequency. Each solver still
receives a plain εr and σ, with **tanδ 0**: the evaluated σ already carries the dielectric loss, and CSXCAD's Kappa is
σ + ωε₀εr·tanδ (`CsxcadWriter.Kappa`), so a non-zero tanδ beside it would count the loss twice. Nothing is evaluated
per FDTD time step. `C3dElaborationOptions.MaterialFrequencyHz` carries the frequency.

- **A setup that requests SAR** will name its SAR frequency (SAR-6), and that is the frequency.
- **Until then, and for any setup without SAR**, it is the sweep's centre (`C3dProblemAssembly.MaterialFrequency`): the
  arithmetic middle of a linear sweep, the geometric middle of a log one. The run's notes say so in one line:
  *"Frequency-dependent materials are evaluated once, at 298 MHz (the sweep's centre), and the solver sees those values
  at every frequency: Muscle εr 58.23, σ 0.77 S/m."*
- **A table asked outside its rows refuses the run**, naming the material, its range and the frequency asked.
- Without a frequency (an editor preview, a thermal run, a comparison between technologies) a dispersive record is its
  plain numbers.

An openEMS run is broadband, but its tissues are constant, so its SAR and B1 are valid at that one frequency only.
SAR-6's result says so (L6).

**Not reached: a layout stackup.** A layout placed in a 3D view resolves its stackup's materials through
`Em3dLayoutSolids`, which takes no frequency. A stackup entry naming a tissue gets its plain (1 GHz) numbers there. No
phase needs a tissue in a layout stackup. If one does, `Em3dLayoutSolidsOptions` takes the same frequency.

### 4.3 The shipped tissue library

`tissue-materials.cmat` ships beside `generic-materials.cmat`. No shipped technology names it. The technology editor's
**Add Tissue Materials** copies it beside a `.ctech` and names it, exactly as *Add Generic Materials* does
(`MaterialLibraries.CopyShippedBeside`), and an existing file of that name that differs is refused, never
overwritten. Its 28 records:

| Records | Values from | Density from |
|---|---|---|
| 16 Gabriel tissues: skin (dry, wet), fat, muscle, cortical and cancellous bone, brain grey and white matter, cerebrospinal fluid, blood, vitreous humour, lung (inflated, deflated), heart, liver, kidney | Gabriel, Lau and Gabriel, *Phys. Med. Biol.* 41 (1996) 2271–2293, Table 1; the same parameters as Gabriel's report AL/OE-TR-1996-0037, Appendix C, entered from the IFAC-CNR copy of it | ICRU Report 44 (1989), as NIST SRD 126 Table 2 tabulates it, for fat (adipose), muscle (skeletal), cortical bone, blood, brain grey and white (one row), and deflated lung. **The other eight state no density** (below) |
| Head and body tissue-simulating liquids | FCC OET Bulletin 65 Supplement C (Edition 01-01), Appendix C: 150 MHz to 5.8 GHz, the 1800–2000 MHz row entered at both ends. The bulletin itself prescribes linear interpolation between target frequencies | 1000 kg/m³, stated under the same table |
| Saline gel (implant heating) | σ 0.47 S/m: Fujimoto et al., *Front. Physiol.* 9 (2018) 1439 (open access). εr 80: the recommended value as Aboyewa et al., AAPM 2021, quote it | water's, NIST SRD 126 (neither source states the gel's) |
| 9 openEMS example tissues: the head phantom (298 MHz) and body cylinder (128 MHz) of `resources/phantoms/create_phantoms.py`, and `Dipole_SAR`'s three ellipsoids (1 GHz) | the values each script states, kept exactly, so SAR-7 and SAR-8 match without a model evaluation in between | the same scripts |

Names are plain (`Muscle`, `Fat`). None collides with a generic material, so none needs a `Tissue/` prefix. The fat
record is the paper's *not infiltrated* fat (5.46, 0.051 S/m at 900 MHz). The *infiltrated* row was not entered,
because no verifiable copy of its parameters was to hand.

**Two things the openEMS records show.** The fallback phantoms' script says its values are "at 298 MHz" and "at
128 MHz" from the Gabriel model, but they are not that model's values at those frequencies. Its 298 MHz skull is εr 11.4,
σ 0.29 S/m, while Gabriel cortical bone at 298 MHz is εr 13.4, σ 0.082 S/m (the script's pair is close to Gabriel's
1.8 GHz value). Its brain is εr 52.7, σ 0.95 S/m against grey matter's 60.1, 0.69 S/m. This is why R-sar2-5 keeps them
as the script states them, not as circuitRF's tissues. A replication that substituted circuitRF's Muscle or Bone would
disagree with the reference for a reason that has nothing to do with SAR. Each record's `Source` gives both pairs.

**What the library does not hold, and why (owner to decide).** *No record carries a number without a source*
(R-sar2-4), so where no source was verified, the record states nothing:

- **Density for eight tissues**: both skins, cancellous bone, CSF, vitreous humour, inflated lung, heart, liver and
  kidney. ICRU 46 / Woodard and White (1986) tabulate them, but no free copy was reachable to verify against. SAR in
  such a tissue cannot be computed until a density is stated, and SAR-5 must refuse it, naming the material.
- **Thermal conductivity, specific heat, perfusion and metabolic heat for every tissue.** The usual compilations are a
  purchased book or the online database O2 excludes. The fields exist and are empty.

### 4.4 Readouts

- **`explain <.c3d|.cem> --materials`** (MCP: `explain materials=true`): the problem's materials as the solver receives
  them; a dispersive one is marked `at <f> (Cole-Cole)` or `(table)`, and the frequency's one line follows. The JSON row
  gains `dispersion` and `evaluatedAtHz` (omitted for a constant material). The ordinary `explain` of a 3D setup marks
  them the same way.
- **The material editor** shows a *Frequency dependence* card for a dispersive record: εr and σ **at f** (a field, 1 GHz
  until a host sets `MaterialsTableViewModel.ReadoutFrequencyHz`; SAR-6 sets it to the active setup's SAR frequency),
  and a small εr/σ-against-log-frequency plot, 10 MHz–10 GHz for a model and the table's own span for a table, with f
  marked. The model is edited in the file. The card reads it.
- `reference shipped-materials` lists the records with the role `dielectric (frequency-dependent)`.

### 4.5 Gates

`tests/Ui.Tests/Em3d/TissueMaterialTests.cs`:

- `ColeColeTests`: muscle, fat and cortical bone at 64, 128, 298, 900 and 1800 MHz, through the **shipped** records, to
  four significant figures. **The reference values are the Gabriel model evaluated by an independent implementation**
  (a separate script, run once), not values printed in the paper: Part III prints its parameters and its figures,
  not a table at these frequencies. The familiar published pairs (muscle 55.03 / 0.943 S/m and fat 5.46 / 0.051 S/m at
  900 MHz, muscle 53.55 / 1.34 S/m at 1.8 GHz) agree with the rows.
- `TissueLibraryTests`: the library ships, every record has a `Source`, and `check` finds nothing in it; a plain number
  5 % off its model is the warning; the library round-trips; with no frequency a record is its plain numbers, exactly as
  the record with its model stripped; a 3D run evaluates muscle at 298 MHz for a 100–496 MHz sweep, with tanδ 0, no Role
  and the one note; and the head liquid asked at 100 MHz refuses the run.

### 4.6 Not done in SAR-2

- **R-sar2-6, the importer for a user's own downloaded tissue table** (optional in the brief). The CSV reader is
  simple. What it needs decided is its surface: a CLI noun (`import tissues <csv> --into <file.cmat>`) plus a second
  mode on the MCP `import` tool (R-aut-13), which turns that tool from single-mode into one with a selector. Left for
  the owner.
- Debye or Lorentz fits for broadband FDTD tissue (overview §8); Palace, which will read the same evaluated values.

---

## 5. Phantoms, lumped elements and the FDTD keys replication needs (SAR-3)

What SAR-7 and SAR-8 need to rebuild `Dipole_SAR` and `MRI_Loop_Coil` from `.c3d` objects alone.

### 5.1 The ellipsoid (R-sar3-1)

`C3dEllipsoid` is a centre and three semi-axes in DBU, along x, y and z of its own frame, so a placement's rotation turns it
(a sphere's rotation only moves its centre). It lowers to `Em3dEllipsoid` — centre, semi-axes A, B, C and the frame's axes
U, V (W = U × V) — which carries the placement's rotation in its axes. Its one face is `surface`; every face edit, a
boundary on it, Fillet/Chamfer and Convert to Polyhedron are refused as a sphere's are.

| Where | How it is stated |
|---|---|
| CSXCAD, axes on the world's | a unit `Sphere` under `Scale3` then `Translate`, exactly what `Dipole_SAR.py` writes (§5.6 gate) |
| CSXCAD, turned off the world's axes | the `Polyhedron` of its tessellation: the grid reads a solid's extent from its primitive, not from CSXCAD's transformation chain, so a rotation between the scale and the move would be gridded from a box it does not fill; the polyhedron is staircased the way the transformed sphere would be. The writer's header says so |
| Gmsh (Palace, thermal) | `Sphere` of radius 1, `Dilate` by the semi-axes, `Rotate` onto its frame, `Translate` |
| geometry worker | a unit sphere under one `gp_GTrsf` (the semi-axes along u, v, u × v, then the centre): a B-spline surface, exact |
| STEP export | the worker's `ellipsoid` primitive (centre, semi, u, v) |

**The kernel's volume** of an ellipsoid turned 30° about y (80 × 60 × 30 µm) is 4.4e-4 below 4/3·π·abc. The surface is
exact: with `BRepGProp::VolumeProperties`' adaptive integration at 1e-9 the same solid gives the analytic volume to 1e-6.
The 4.4e-4 is the worker's fixed-order Gauss integration over a B-spline face (a sphere's analytic face integrates to
1e-6 with it). The worker is left as it is — every volume it reports would move — and the gate holds the kernel's own
tolerance, 1e-3.

### 5.2 Layered phantoms (R-sar3-2)

`C3dPhantom` is one object: a `Shape`, its dimensions, shells, and `Layers` (`Name`, `Material`), outermost first. In the
problem each layer is an ordinary solid named `<phantom>/<layer>` (the spelling a placed cell's contents take, which no
document object can have), elaborated outermost first so the innermost has the later construction order and wins
(em-3d.md §6.3a) — the priorities 10, 11, 12 `Dipole_SAR` gives skin, headbone and brain. **Every layer is a dielectric**:
a tissue record states εr and σ together, which on an ordinary object asks for a Role; a phantom's layer has one.

| Shape | Fields | Layers, outermost first | From |
|---|---|---|---|
| `Ellipsoid3` | Centre, SemiAxes, Shell1, Shell2 | each shell comes off every semi-axis | openEMS's head phantom: 100/80/100 mm, shells 9 and 9 mm; `Dipole_SAR`'s three ellipsoids |
| `Cylinder3` | Centre, Radius, Length, Axis, Shell1, Shell2 | radial shells; every layer runs the full length | openEMS's body cylinder: r 100 mm, 260 mm, shells 8 and 10 mm |
| `FlatBox` | Centre, Size, Shell1 | a shell on the floor and four walls, the liquid open on top | the flat phantom of phone SAR (SAR-12) |
| `Sphere1`, `Box1` | Centre, Radius / Size | one | the analytic checks |

The defaults (`C3dPhantom.New`) are the openEMS examples' geometry where there is one, as `create_phantoms.py` states
it, and SAR-2's records of those examples' tissues as materials. The flat box's liquid defaults to the body
tissue-simulating liquid; **its shell names no material**, because no shell material with a stated source is shipped —
the owner's to decide whether one should be.

**Flatten** (`C3dPhantoms.Flatten`) replaces the phantom with its layers as ordinary objects named
`<phantom>_<layer>`, in a group named after it, in its place in construction order: a flattened phantom elaborates to the
same solids, materials, roles and orders (the gate). The flattened objects hold numbers; an expression in the phantom's
dimensions is not carried into them.

### 5.3 The lumped element (R-sar3-3)

`C3dLumpedElement` (the document's `LumpedElements`) is a gap given exactly as a lumped port's — plane, offset,
rectangle, optional Positive/Negative — and resolved by **the port's own code**: `C3dLumpedElements` builds a port record
from it and calls `C3dPorts.Resolve`, so the contact inference, the corner inset, the stated-ends override and the
not-modelled refusal cannot drift apart. Only the polarity is dropped. R, L and C are expressions in the document's
scope, each with its unit (`C = Ctune`, `CUnit = pF`), so the tuning series sweeps them by re-running.

openEMS 0.37.0's `LumpedElement` reads `Direction`, `Caps`, `R`, `C`, `L` (each absent = not present) and `LEtype`
(0 parallel, its default; 1 series). From `operator.cpp` and `operator_ext_lumpedRLC.cpp` at the `v0.37.0` tag (read,
not copied): a parallel R and/or C is folded into the cell's material, a parallel element with L and every series element
go through the RLC extension, a negative value is clamped to zero with a warning, an element with nothing is skipped, a
zero R or L in parallel is treated as absent (open), a zero C in series as absent, and a series element whose only values
are a zero R or L divides by zero in its update. **Each of those is refused by `Em3dLumpedElement.Inexpressible`**,
naming the element, rather than written as something openEMS would turn into something else. One openEMS check is not
reproduced: a parallel C so small that C / (Σ cell area / length) is below ε₀ is skipped by openEMS with a warning; it
depends on the snapped cells, and the run's own log carries openEMS's sentence.

The writer writes it as a port's resistor element is written, the width snapped to a line as a port's is
(`FdtdGrid.PortWidthAxis`), and the grid collects its extents as a port's (`PortExtent` lines, the same thirds-pair snap).
It is not a port: no excitation, no probe, no row in S. **Palace refuses it** in this version (its lumped elements are
their own series), a static problem leaves it out with a note, and a cylindrical grid writes it only when its gap is a patch
of a cylinder about the grid's axis (SAR-8b, §10a).

### 5.4 Cell-constant material (R-sar3-4)

`OpenEms.CellConstantMaterial` writes `CellConstantMaterial="1"` on `<FDTD>`. `CemOpenEms.ResolveRun(section,
requestsSar)` turns it on by default when the setup requests SAR — SAR-6 passes that; nothing requests SAR yet — and
off otherwise, so every existing setup writes the bytes it wrote. The gate compares a setup without SAR with brief-em3d-9's
committed microstrip golden, byte for byte, and with the key on, the golden with exactly that one attribute added.

### 5.5 Explicit grid lines (R-sar3-5) and boundaries (R-sar3-6)

`OpenEms.GridLines` is either `File` — a CSXCAD XML whose `RectilinearGrid` is taken verbatim, each line times its
`DeltaUnit`, a cylindrical one refused — or `X`, `Y`, `Z` lists in a `Unit` that must be stated (a bare coordinate could
be mm or m). A relative file is read beside the document that holds the setup (`CemOpenEms.Anchored`: the `.cem`'s
reader and `C3dSetups.ForRun` set it). With it `FdtdGrid` places **no** line: none collected, merged, filled, or added for
a PML. The simulated domain is the grid's extent, as in openEMS; each face's boundary is applied at the grid's outermost
line and a PML takes its outermost `PmlCells` cells, as openEMS's `PML_n` does. Refused, each with the lines either side:

- the air box reaching past the grid (content outside it would not be simulated);
- a lumped port's or element's sheet whose plane is not on a line, whose two ends along its direction land on one line
  (openEMS skips an element of zero snapped length), or whose width holds no line;
- a sheet whose plane is not on a line;
- a wave port (its feed is a lattice only circuitRF's own grid places). A cylindrical grid takes given lines from a file whose grid
  is cylindrical (SAR-8b, §10a.2).

"On a line" is within 1e-3 of the cell beside it: the reference XMLs write coordinates to seven figures, so the loop
coil's capacitors sit 3.3e-6 mm off their lines. An extent between lines is accepted when it still spans the same edges,
as openEMS's snapping does: `Dipole_SAR`'s feed box is ±0.1 mm about the line at x = 0 and ±1.25 mm on z, 2.3 µm off the
nearest lines.

**Boundaries.** Both examples' choices are expressible as they stand: an absorbing face is written `PML_<PmlCells>`, so
`PmlCells` 8 on six absorbing faces is `Dipole_SAR`'s `['PML_8']*6`, and `PmlCells` 0 is `MUR`, `MRI_Loop_Coil`'s
`['MUR']*6`. The Python scripts pass openEMS those strings directly (their FDTD settings are not in the XML, §3.4), and
openEMS's `BoundaryCond` reads the same text from circuitRF's file. Nothing was missing.

### 5.6 Gates

`tests/Ui.Tests/Em3d/SarPhantomsLumpedGridTests.cs`:

- `C3dEllipsoidTests`: the worker's ellipsoid (turned 30° about y) against 4/3·π·abc to the kernel's tolerance (§5.1); the
  CSXCAD element of an axis-aligned one has `Dipole_SAR`'s skin ellipsoid's structure and values (a unit sphere, Scale3
  80, 100, 100 mm, Translate 100 mm — in metres, where the script's file is in mm with DeltaUnit 0.001); a turned one is a
  `Polyhedron`.
- `C3dPhantomTests`: an `Ellipsoid3` beside a dipole writes three nested ellipsoids in its three tissues with priorities
  rising inward, semi-axes 100, 91, 82 mm; Flatten gives the same solids, materials, roles and orders.
- `LumpedElementCsxTests`: a 5.4 pF element whose C is a VAR in pF, across a gap in y, writes the attributes
  `MRI_Loop_Coil.xml`'s `caps_y` carries (Direction 1, Caps 1, C 5.4e-12, LEtype 0; no R, no L) with the same values, and
  adds no port. It reads the reference XML where present and the transcribed values elsewhere.
- `ExplicitGridTests`: `Dipole_SAR`'s XML gives its 114 × 115 × 116 lines exactly, written back unchanged (a stand-in file
  of the same form where the reference is absent); a port between two given lines is refused naming them.
- `SarSetupUnchangedTests`: §5.4.

### 5.7 Not in SAR-3

Voxel and mesh import (SAR-11); a lumped element on Palace (its own series); a lumped element on a cylindrical grid.

---

## 6. openEMS's HDF5 through PureHDF (SAR-4)

The raw SAR dump, `sar_calc`'s result, HDF5 field dumps and `DiscMaterial` voxel files are HDF5. circuitRF reads and
writes them through **PureHDF 2.1.2**, the managed, MIT, no-native library `src/RfCore` already references for the
`.mat` export (overview O1). There is no HDF5 code of circuitRF's own and no new dependency. The adapter is
`src/RfCore/Hdf5/`: `OpenEmsHdf5` (the reader), `DiscMaterialWriter`, `OpenEmsArrayOrder` (the one place that knows
array order) and `OpenEmsHdf5Diagnostics` (the refusals, as coded diagnostics).

### 6.1 What openEMS 0.37.0 writes (R-sar4-1)

Taken with `h5dump -H -p -B` and h5py from SAR-1's raw dipole dump, `sar_calc`'s 10 g result on it, the SAR-4 field
dumps (§6.5) and the bundled head phantom (as an example of a `DiscMaterial` file CSXCAD reads; it is not committed,
L3). **PureHDF 2.1.2 reads every row**, checked by reading each file with it: the phantom's chunked, deflated
`/DiscData` gives the same per-tissue voxel counts as h5py (394,928 / 111,736 / 45,952 / 56,792).

| | Raw SAR dump (DumpType 29) | FD field dump (DumpType 10, 11) | `sar_calc` result | `DiscMaterial` (bundled phantom) |
|---|---|---|---|---|
| Superblock | version 2 | 2 | 2 | 0 (symbol-table groups) |
| Groups | `/Mesh`, `/CellWidth`, `/CellData`, `/FieldData/FD` | `/Mesh`, `/FieldData/FD` | `/Mesh`, `/FieldData/FD` | `/mesh`; `/DiscData` is a dataset at the root |
| Layout | contiguous throughout | contiguous | contiguous | `/DiscData` chunked (23, 18, 46); `/mesh/*` contiguous |
| Filters | none | none | none | `/DiscData` deflate level 9, no shuffle |
| Datasets | `/Mesh/{x,y,z}`, `/CellWidth/{x,y,z}` float64; `/CellData/{Conductivity,Density,Volume}` float32 (nx, ny, nz); `/FieldData/FD/f<k>` (3, nx, ny, nz), a compound {`r` float32, `i` float32} | `/Mesh/*` float64 (one line per node); `f<k>` the same compound, (3, nx, ny, nz) | `/Mesh/*` float64; `f<k>` float32 (nx, ny, nz) | `/DiscData` uint8 **(nz, ny, nx)**; `/mesh/{x,y,z}` float32, the voxel boundaries |
| Numeric attributes | root `openEMS_HDF5_version` float64 0.3, `dump_type` int32 29, `dual_mesh` uint8 1; `/Mesh` `mesh_type` int32 0, `mesh_scaling` float64; `/FieldData/FD` `frequency` float64[n] | root the same with `dump_type` **0 (E) or 1 (H)** and `dual_mesh` 0; `f<k>` also `frequency` float64 | root `mass` float64, `proc_time` float64, no `dump_type`; `/FieldData/FD` `frequency` **float32**[n] and `valid_cubes`, `used_cubes`, `unused_cubes`, `air_cubes` uint64; `f<k>` `frequency`, `maxSAR`, `power` float32, `maxSAR_idx` uint32[3] | root `Version` float64 2; `/DiscData` `DB_Size` int32, `epsR`, `kappa`, `density` float32[DB_Size] |
| String attributes | `d_order` scalar, variable-length UTF-8, null-terminated: `'XYZ'` on the cell data, `'NXYZ'` on the field | `'NXYZ'` | `'XYZ'` | `Name`, the same form, the entries joined by commas |

Three things the table settles that a reader could get wrong:

- **`dump_type` is the field, not the DumpType.** A frequency-domain E dump (DumpType 10) says `dump_type = 0`, an H
  dump (11) says 1; that it is frequency-domain is the `/FieldData/FD` group. The raw dump says 29.
- **Mesh lines are in metres** in every openEMS file. `mesh_scaling` is the setup's drawing unit (1e-3 for a mm
  setup, 1 in `sar_calc`'s output), stated beside the lines and never to be applied to them; openEMS's own reader
  documents it so. The raw dump's `/Mesh` lines are the **cell centres** (`dual_mesh = 1`), with each cell's widths in
  `/CellWidth`.
- **The frequency's float width differs** between openEMS (float64) and `sar_calc` (float32). Both are the pinned
  layout; the reader takes any integer or float width for a number and refuses anything else.

### 6.2 Array order (`OpenEmsArrayOrder`)

circuitRF holds a cell array in **C order over (x, y, z)**: cell (ix, iy, iz) is at (ix·ny + iy)·nz + iz, z fastest.
That is the order NumPy hands back for openEMS's own reader, so a reference `.npy` compares element for element.

- **0.37.0's dumps and `sar_calc` say `d_order = XYZ`** (a vector `NXYZ`, component first) with dimensions
  (nx, ny, nz): circuitRF's own order. Nothing is permuted. Pinned by `sar_calc`'s own `maxSAR_idx` = (14, 44, 45):
  in the array this reads, that cell holds the array's maximum.
- **`ZYX`/`NZYX` is the legacy form** `--legacyHDF5Dumps` writes (it also splits a complex dataset into `_real` and
  `_imag`). 0.37.0 does not write it by default, so it is **refused**, naming `d_order`, rather than read.
- **CSXCAD's `/DiscData` has no `d_order`.** `CSPropDiscMaterial` reads it into one flat buffer and indexes it
  ix + iy·Nx + iz·Nx·Ny: **x fastest**, so the dataset's dimensions are (nz, ny, nx), as the body phantom's
  (104, 92, 92) against its 93 × 93 × 105 mesh lines shows. `DiscMaterialWriter` permutes into that through
  `OpenEmsArrayOrder.ToCsxcadOrder`, and nowhere else. Pinned by the CSXCAD gate (§6.5): a transposed 3 × 2 × 2 file
  gives the wrong tissue to 10 of its 12 voxels.

### 6.3 The reader (R-sar4-2)

| Call | Reads | Returns |
|---|---|---|
| `OpenEmsHdf5.ReadSarRaw(path, frequencyIndex, xStart, xCount)` | a raw SAR dump | `OpenEmsSarRaw`: the cell-centre mesh, cell widths, the frequency, complex Ex, Ey, Ez, σ, ρ and volume per cell — optionally only an x slab |
| `OpenEmsHdf5.ReadField(path, frequencyIndex)` | an FD E or H dump | `OpenEmsField`: the quantity, mesh, every frequency in the file, three complex components |
| `OpenEmsHdf5.ReadSarResult(path, frequencyIndex)` | `sar_calc`'s output | `OpenEmsSarResult`: SAR per cell, mass, power, `maxSAR` and its cell, the four cube counts |

- **One dataset at a time.** Every array is read on its own through a PureHDF hyperslab selection: a vector field one
  component at a time, and `ReadSarRaw` reads only the x slab it is asked for, so SAR-5 can stream a dump larger than
  memory through in pieces. Mesh lines and attributes are small and read whole.
- **Refusals.** A file whose groups, datasets or attributes differ from §6.1 — an object missing, a dataset where a
  group belongs, another type or shape, a legacy `d_order`, a cylindrical mesh (`mesh_type` ≠ 0), another
  `openEMS_HDF5_version`, a time-domain-only dump — throws `OpenEmsHdf5Exception`. Its `ObjectPath` is the HDF5 path
  (an attribute as `/Mesh/@mesh_type`), and its diagnostic (`hdf5.openems.missing`, `hdf5.openems.unexpected`) says
  what the pinned layout has there. The path is walked link by link, so a missing parent group is named, not its
  child.
- **E and H stay complex64** (`ComplexF`, the file's own `r`/`i` floats). A whole body at 2 mm is about 34 M cells;
  `System.Numerics.Complex` would double what is already the largest array of a SAR run. A consumer widens a value
  when it computes with it, and comparing against openEMS's reader is exact because nothing is converted.
- **Where SAR-5's `SarGrid` comes from.** RfCore cannot name a `src/Engine` type, and the engine never opens a file
  (R-sar4-5). So the reader returns `OpenEmsSarRaw`, plain arrays, and `src/Design` builds SAR-5's `SarGrid` from
  them. The engine receives arrays.

### 6.4 `DiscMaterialWriter` (R-sar4-3)

`DiscMaterialWriter.Write(path, x, y, z, indices, tissues)` writes the version-2 layout of §6.1's last column, with
PureHDF's writer (the API `MatWriter` uses): `/@Version` float64 2.0; `/DiscData` uint8 in CSXCAD's order with
`DB_Size` int32 and `epsR`, `kappa`, `density` float32[DB_Size] and `Name`; `/mesh/x|y|z` float32 voxel boundaries
in metres. Entry 0 is background. A setup's `DiscFile` gives `Scale` = 1 / its drawing unit (1000 for mm), as
`MRI_Loop_Coil.xml` does for the bundled phantom.

- **The index is uint8, and that is CSXCAD's limit, not a choice.** The brief allowed uint8 or uint16. CSXCAD reads
  `/DiscData` with `H5T_NATIVE_UINT8` into a uint8 buffer, so a uint16 file would be converted on read and every
  index above 255 lost. The writer writes uint8 and **refuses more than 256 entries**; a model with more tissues needs
  CSXCAD to change, or the model split across several `DiscMaterial` properties. **Finding for the owner** (SAR-11).
- **Plain layout.** Contiguous and unfiltered, the form openEMS's own dumps take. No size forces deflate: a contiguous
  HDF5 dataset has no size limit, and a whole body at 1 mm is about 34 MB of uint8 indices.
- **Where PureHDF's file differs from the phantom's, and why it does not matter.** PureHDF writes superblock 2 and
  pads variable-length strings with nulls (`H5T_STR_NULLPAD`) where the phantom terminates them; CSXCAD does not read
  `Name` at all (it reads `Version`, `DB_Size`, `epsR`, `kappa`, `mueR`, `sigma`, `density`, `/DiscData` and the mesh,
  and notes the absent `mueR` and `sigma` and skips them). The voxel boundaries are float32 because CSXCAD reads
  them as float; at metres that is 1e-7 relative, a tenth of a micron on a 1 m body.
- **Refusals** (`hdf5.discmaterial.refused`): fewer than two lines, or lines not strictly increasing; an index count
  that is not the voxel count; no entries or more than 256; an index past the last entry; a name containing a comma.
- **Permittivity is not checked by the gate.** No openEMS dump states it per cell (the `--debug-material` dump holds
  edge-averaged values). It is read by the same CSXCAD call as conductivity and density, which the gate checks.

### 6.5 Gates and samples

The samples are made by `testdata/sar/openems-ref/tools/make_hdf5_samples.py` (README, "SAR-4's HDF5 samples"): the
raw dump's arrays as openEMS's `HDF5Dump` reads them; `sar_calc` (IEEE_62704, 10 g) on that same raw dump, which
**reproduces SAR-1's `sar_10g.npy` bit for bit** — so the kept raw dump and `SAR_10g.h5` are an exact pair for SAR-5,
not a pair from two runs; and one small run's HDF5 E and H field dumps at two frequencies. Like the rest of the
reference data they are on the developer machine only.

`tests/Ui.Tests/Sar/OpenEmsHdf5Tests.cs`:
- the raw dipole dump reads to exactly what openEMS's reader wrote (E, σ, ρ, volume, mesh lines; float32, exact), an
  x slab is the same cells read on their own, and a cell's volume is the product of its stated widths;
- `sar_calc`'s result reads to `sar_10g.npy` exactly, its `maxSAR_idx` cell holds the array's maximum (the order pin),
  and its mass, power and cube count are SAR-1's `summary.json`'s;
- the E and H field dumps, both frequencies, read to what openEMS's reader wrote;
- a file whose `/CellWidth` is a dataset where openEMS writes a group is refused naming `/CellWidth` (built in the
  test, so it always runs).

`tests/Ui.Tests/Sar/DiscMaterialWriterTests.cs`: a 3 × 2 × 2 file written by circuitRF, every voxel a different
tissue, loaded by the installed openEMS in a 20-step run with a raw SAR dump over it. openEMS's dump, read back
through `OpenEmsHdf5`, gives each of the 12 voxels the conductivity and density the file declares, and air around
them. Skipped with a reason when openEMS is absent. **CSXCAD accepts the file** as PureHDF writes it; nothing had to
be worked around.

### 6.6 Not in SAR-4

- **openEMS's own SAR dumps** (DumpType 20, 21, 22: `MRI_Loop_Coil`'s and the birdcage's local-SAR planes). They are
  written by openEMS, not `sar_calc`, and no sample of them is kept; SAR-6 or SAR-8 pins them when it needs them.
- **Cylindrical meshes** (`mesh_type` 1, the birdcage's dumps): refused. Time-domain dumps: refused. The legacy
  format: refused (§6.2).
- **The far-field path stays on VTK** (`OpenEmsFarField.cs`'s header): `VtrFile` serves every openEMS field path, and
  the radiation gates validated the transform on it. Moving it would buy a re-validation and nothing else.

---

## 7. circuitRF's own SAR engine, and `sar_calc` as a cross-check (SAR-5)

`src/Engine/Em3d/Sar/` computes local SAR, the spatial-average SAR of any mass by cube averaging, and a region's
absorbed power, tissue mass and whole-region SAR. It is MIT code written from openEMS's own public description of its
`IEEE_62704` method (the `SAR_Calculation.SetAveragingMethod` docstring, `python/openEMS/sar_calculation.pyx` at the
`v0.37.0` tag) and from how `sar_calc` behaves on real dumps. Its C++ (`tools/sar_calculation.cpp`) was read to learn
that behaviour. Nothing was copied (overview L3). IEC/IEEE 62704-1 is a purchased standard and was not consulted (L2),
and no other published description was used. So where this section says "the procedure", it means the procedure as
openEMS implements it, and every choice point below records what openEMS does and whether circuitRF does the same.

### 7.1 Input (R-sar5-1)

`SarGrid`: three `SarAxis` (cell widths in metres, faces cumulative from the first) and, per cell, the dissipated power
density ½σ|E|² (W/m³), ρ (kg/m³) and the volume (m³), in C order over (x, y, z). `SarGridBuilder` takes the brief's
quantities, the peak complex Ex, Ey, Ez with σ, ρ and V, **one x slab at a time**. As each slab arrives it reduces E and
σ to ½σ|E|². That is the engine's only use for them, and it needs 4 bytes a cell where E and σ take 28. A complex64
dump is taken as interleaved floats with no copy (`SarComplex64.AsFloats`). A cell with ρ = 0 is background: its power
density is kept as zero, as `sar_calc` zeroes it. Whatever it dissipated is summed into `UncountedPowerW`, so a lossy
material given no density shows up instead of vanishing.

`OpenEmsSarGrid.Read` (`src/Design/Em3d`) streams openEMS's raw dump through `OpenEmsHdf5.ReadSarRaw` in slabs of about
2 M cells, after `OpenEmsHdf5.ReadSarRawLayout` has read the mesh, widths and frequencies alone. The engine never opens
a file (L5).

### 7.2 The procedure, per mass m (R-sar5-2, R-sar5-3)

**Local SAR** is σ|E|²/(2ρ), zero where ρ = 0. **P_abs** = Σ ½σ|E|²V and **mass** = Σ ρV over the region's tissue,
and whole-region SAR = P_abs / mass (§1.2).

**A cube** about a cell is the box [c − h, c + h] on each axis, c the cell's centre and h its half-width. Its mass is
Σ ρᵢVᵢfᵢ over the cells it touches, where fᵢ is the fraction of cell i inside it (the partial-cell treatment). fᵢ is the
product of the three axis fractions, and each axis fraction is 1 except at the cube's two end cells on that axis. Its
average is Σ ½σᵢ|Eᵢ|²Vᵢfᵢ / Σ ρᵢVᵢfᵢ, and its background fraction is Σ_{ρ=0} Vᵢfᵢ / Σ Vᵢfᵢ.

`SarMethod.Cube` (`sar_calc --method IEEE_62704`):

1. **Centred cubes.** For each tissue cell, find the h at which the centred cube holds m. The cube is **valid** when
   (a) it stays inside the grid; (b) its background fraction is **below 10 %**; and (c) the outermost layer of cells on
   each of its six faces holds at least one tissue cell. A valid cube's average is its centre cell's value.
2. **Used cells.** A tissue cell that is not the centre of a valid cube but lies **wholly** inside one or more valid
   cubes takes the largest of their averages. An end cell counts as wholly inside only when its fraction is exactly 1.
3. **Unused cells.** For every other tissue cell, grow six cubes, one from each face of the cell: on that face's axis
   the cube runs 2h from the face, outward through the cell, and it is centred on the other two. Each is clipped where
   it reaches the grid's edge. No background or face rule applies, and each is grown until it holds m. A direction that
   cannot reach m gives nothing. Among the directions that did, those whose volume is within **5 %** of the smallest
   are kept, and the largest of their averages is the cell's value. A cell with no direction is `NoCube`, value zero
   (`sar_calc` prints `can't find fitting cubical averaging volume`).

`SarMethod.Simple` (`--method SIMPLE`): step 1 accepts every centred cube that reaches m, clipped by the grid's edge
where it reaches it, with no background or face rule. Step 2 counts a cell partly inside a cube. Step 3 keeps only the
smallest-volume directions.

The cube statistics are each cell's `SarCubeKind` (`Valid`, `Used`, `Unused`, `NoCube`, `Background`) and their counts.
The peak carries its cell, its centre and the cube it came from: its own cube if valid, the containing valid cube if
used, the face-grown cube if unused. It is restricted to the region, and a tie goes to a valid cell.

### 7.3 Where the description leaves a choice, and what was chosen

| # | Point | openEMS (`sar_calc` 0.37.0) | circuitRF | Effect |
|---|---|---|---|---|
| 1 | How the cube's size is found | iterates from half the edge (a cube-root rescale, then secant steps kept inside known bounds) and stops at the first size whose mass is within **1e-6** of m | a secant on the mass's cube root, kept inside a bracket and halved when it stalls, to **1e-9** | the cube is the same to 1e-6 in mass; values agree to ≤ 1e-6 |
| 2 | An iterate *smaller* than the final cube with an all-air face and too little mass | the cube is declared invalid at that point, before the mass is reached | only the final cube is judged | identical for convex tissue: if a face of a smaller cube about a tissue point is all air, so is every larger cube's face on that side. Measured identical cube counts on the dipole (§7.5). A non-convex body could differ at a few surface cells |
| 3 | A cube narrower than one cell along an axis | that cell's fraction is applied twice (once as the start cell and once as the stop cell) | applied once (2h/w) | never reached at 1 g or 10 g with tissue-resolution cells |
| 4 | Background and face test | by volume including partial cells; strictly below 10 %; the face layer includes partial cells | the same | — |
| 5 | Face-grown cubes reaching the grid's edge | clipped there and accepted if they reach m | the same, so such a cube is truncated by the grid | the random phantom (§7.5) depends on it |
| 6 | The 5 % volume rule | compares each direction's volume, background included, with the smallest | the same | — |
| 7 | `Simple`'s mass tolerance | 5 %, and the first iterate inside it is taken | the exact mass (1e-9) | `Simple` can differ from `SIMPLE` by the 5 % mass window. The cross-check runs `SIMPLE` for `Simple` and `IEEE_62704` for `Cube` |
| 8 | A region (R-sar5-4) | none (`--autorange` only bounds the work) | restricts every reported number, the peak, P_abs and mass. Averaging always uses all tissue, since a cube centred in the region may reach tissue outside it | head-only SAR is a region, not a code path |
| 9 | `IEEE_C95_3` | offered | not offered (the brief asks for two methods) | — |

### 7.4 Cost (R-sar5-5)

A cube's three sums are separable, so they are a weighted combination of at most **4 × 4 × 4 entries of a
summed-volume table** (a 3D prefix sum of ρV, V, background V and power). One evaluation is a fixed 64 table reads,
whatever the cube's size. Finding the cube of mass m takes a few evaluations: the mass's cube root is close to linear in
h, so the secant is nearly exact in homogeneous tissue. The face test reads a prefix count of tissue cells.

Step 2 is a painter's pass. The valid cubes that wholly hold any candidate cell (a second prefix count) are taken in
falling order of value. Each claims the unclaimed candidates inside it through a skip list along z per (x, y) row, so a
claimed cell is never visited again. Threads own disjoint ranges of x. Nothing loops over a cube's cells.

`SarOperationCountTests` holds this with a counter, not a timing. On the same block at 1 mm and at 0.5 mm, a 1 g cube
spans 8× the cells, and mass evaluations per cube search do not grow (3.9 on the dipole's dump and on the 10 M-cell head below).

**Measured** (the developer machine: 10 cores, 16 GB; Release; 2026-10-18): a synthetic three-layer ellipsoid head (100 × 80 × 100
mm semi-axes, shells 9 mm, 4.22 kg) at **1 mm in a 230 × 190 × 230 grid, 10.05 M cells (3.35 M tissue)**, with
1 g and 10 g together: **1.6 s**, **1.25 GB** peak resident. That is 6.7 M cube searches at 3.9 mass evaluations each,
and 359 M rows walked by the painter. The dipole's raw dump (83 × 90 × 91, 0.68 M cells), 10 g: **0.17 s**.

### 7.5 Agreement with `sar_calc` (the gate)

| Case | Peak | Cells within 1 % | Worst cell | Cube counts |
|---|---|---|---|---|
| `Dipole_SAR` raw dump, 10 g, `IEEE_62704` (SAR-4's exact pair) | **identical** (3.91953e-27 at (14, 44, 45)) | **100 %** of 216,116 | 7.8e-7 | valid 165,776 / used 49,620 / unused 720 / air 463,654: **identical** |
| Random phantom, 1 g, `IEEE_62704` (10 × 10 × 10, 8 g in all) | identical | **100 %** of 1,000 | 1.1e-7 | circuitRF's: 64 valid, 448 used, 488 unused (that run's result was kept as `.npy` only, without its counts) |

P_abs and mass agree with `sar_calc`'s attributes to 5e-8 (float32 inputs summed in a different order). Overview §4 asks for 0.5 % on the peak and 1 % on ≥ 99 % of
cells.

### 7.6 `sar_calc` alongside (R-sar5-6, overview O3)

- **Found beside openEMS**: `SolverDiscovery.SarCalcBeside(openEms)` looks in the directory of the openEMS discovery
  chose, and in that of its link target, and nowhere else. A cross-check only means something against the openEMS
  that wrote the dump. An openEMS in the Linux subsystem gives none.
- **`OpenEmsSarCalc.CrossCheck(enabled, openEms, rawDump, ours, method, workDir)`** runs `sar_calc -i <raw> -o <out>
  --method IEEE_62704 --mass <g>` (`SIMPLE` for `Simple`) once per averaged mass, through `Em3dProcessLauncher`, with
  its console output in `<out>.log`. **It is judged by its output file**, which is deleted first, because its exit
  status is inverted (§2). The result carries both peaks and their relative difference.
- **What it says.** Off: nothing. sar_calc absent: a **note**. sar_calc failed: a **warning** naming its log. Peaks
  more than **0.5 %** apart: a **warning** with both values.
- **The setup key.** `Sar.CrossCheck` belongs to the setup's `Sar` group, which SAR-6 creates (R-sar6-1). This phase
  provides the step that the key switches on.

### 7.7 Handed to SAR-6

- `Sar.CrossCheck` in the `Sar` group, passed to `OpenEmsSarCalc.CrossCheck` after the run.
- **A lossy tissue with no density** (§4.3: eight shipped tissues state none). openEMS writes such a cell as ρ = 0, and
  every SAR tool then treats it as air. SAR-6 refuses the setup, naming the material. The engine's `UncountedPowerW` is
  the backstop that would show one that got through.
- Naming a region after a phantom or tissue set: the engine takes a mask (`SarRegion.Mask`), which SAR-6 builds from
  the problem's solids.

### 7.8 Gates

`tests/Ui.Tests/Sar/SarEngineTests.cs`:

- `SarLocalTests`: openEMS's analytic 4 × 4 × 4 block, built in C#. Local SAR is 2.5e-4 W/kg to 1e-6, and P_abs, mass
  and whole SAR follow.
- `SarAveragingTests`: a 40 mm uniform block, where the 10 g average equals the local value everywhere and the centre's
  cube is valid; a two-layer block, where the 1 g cube across the interface is (4.5·1e-4 + 5.5·3e-4)/10 W/kg (half-cells
  at every end); a block corner, which is unused and takes a face-grown cube.
- `SarOperationCountTests`: §7.4.
- `SarAgainstSarCalcTests`: §7.5's two rows, from fixed data, with no openEMS process (skipped with a reason where the
  data is absent).
- `SarCalcCrossCheckTests`: the installed `sar_calc` on the dipole dump, both peaks within 0.5 % and no warning (skipped
  without openEMS or the dump); with no `sar_calc` beside openEMS the cross-check is one note, and nothing is started.

---

## 8. SAR and B1± as results of a `.c3d` run (SAR-6)

A driven openEMS setup with a `Sar` group gives, per driven port and SAR frequency, local SAR, the spatial-average SAR of
each mass, B1+ and B1− as field plots, the peaks with their cubes, and the readouts and `.npy` scalars of §8.6. Nothing in
it starts a process but openEMS (and `sar_calc`, when asked): everything after the run is read from what the run left
in its port directories, which is why the gate drives it from SAR-1's kept dipole dump.

### 8.1 The `Sar` group (R-sar6-1)

`CemSar` (`src/Design/Layout/Em/CemSar.cs`), on the `.cem` schema, so a `.c3d`'s embedded setup and a `.cem` carry the same
keys and `reference em-setup` documents them from the type:

| Key | Default | |
|---|---|---|
| `FrequenciesGHz` | none: required | each inside the sweep; one only with a frequency-dependent material (L6) |
| `MassesGrams` | `[1, 10]` | any positive mass; the peak cube is `psSAR<m>g` |
| `Method` | `Cube` | `Cube` = `sar_calc --method IEEE_62704`, `Simple` = `SIMPLE` (§7.2) |
| `Region` | every lossy dielectric | object names; a phantom's name stands for all its layers |
| `B1` | on when every SAR frequency is below 500 MHz | |
| `B0Axis` | `+z` | `±x`, `±y`, `±z` |
| `B1Region` | the first phantom's innermost layer, else `Region` | where the mean \|B1+\| is taken |
| `CrossCheck` | off | §7.6 |
| `PerPortFields` | off | every port's E and H kept beside the `.sNp` for a schematic's `fields` analysis (§11, SAR-9) |

**The units are in the key names** (`FrequenciesGHz`, `MassesGrams`), where the brief wrote `Frequencies` and `Masses`,
as `SaveFieldsGHz` and `OffsetUm` already are: a bare SAR frequency could be Hz, MHz or GHz, and all three are plausible.
`SarSettings` is the group resolved, and the one place its defaults live. The group turns `OpenEms.CellConstantMaterial`
on unless the section states it (`CemOpenEms.ResolveRun(section, requestsSar)`, SAR-3), and the writer's existing note says
so. Every dispersive material is evaluated at the **first** SAR frequency (`C3dProblemAssembly.Assemble`), and the
elaboration's note names it "the SAR frequency"; `explain --materials` says the same.

**Refusals**, each before any process: a SAR frequency outside the sweep; two SAR frequencies with a frequency-dependent
material, naming the material and a second setup as the way to the next frequency (L6); a setup whose solver is Palace
alone (only openEMS writes the raw dump); on a cylindrical grid, an averaging mass or the cross-check (cubes are a Cartesian grid's:
there it is local SAR and B1 only, §10a.4); a non-driven problem;
no lossy dielectric at all; a `Region` or `B1Region` naming no object; a region solid with no density (below). A setup with
`Both` runs, and the openEMS leg carries the SAR.

### 8.2 Which cells are tissue

openEMS's raw dump carries ρ per cell and every SAR tool treats ρ = 0 as air, so **tissue is decided by which materials are
written with a density**. `Em3dSar.Resolve`:

- a solid is **lossy** when it is a dielectric (not a conductor, not air) whose evaluated σ is above zero. A board whose loss
  is a tanδ (FR4: σ 0) is never tissue, whatever density its record states for the thermal solver: it dissipates nothing,
  and counting its mass would dilute every cube that reaches it;
- the **region** is `Region`'s solids, or every lossy one. A region solid whose material states no density is **refused**,
  naming the material (§4.3: eight shipped tissues state none) — openEMS would write it as air. The escape is to add the
  density with its source, or to name a region without it;
- **tissue** is every lossy solid that states a density, in or out of the region, because a cube averages what surrounds its
  centre. A lossy solid outside a stated region with no density is written as background, and the run says so.

The densities ride the problem as `Em3dProblem.SolidDensities` (by solid name, from the record each solid was built from,
`C3dElaboration.SolidMaterials`), which no lowering but a SAR run reads; the writer writes `Density` on exactly the tissue
solids and `0` elsewhere, as before. Which tissue a dump cell is (a named region, B1's region, a peak's tissue) is
answered by testing its centre against the tissue solids, the last-constructed first (the one CSXCAD gives the cell), with
the nearest density as the fallback for a centre no test claims.

### 8.3 What is dumped (R-sar6-2)

Per SAR frequency `j`: `sar<j>` — the raw SAR dump (`DumpType 29`, HDF5, `DumpMode 2`) — and, with B1, `b1h<j>` — an H dump
(`DumpType 11`, VTK, `DumpMode 2`, as the E path's) — both over **the tissue's bounding box widened to the grid lines
outside it and one cell more on every side** (`Em3dSar.DumpLines`), and both with the probes' `OverSampling`, for the
reason the far-field dumps give (§2). A tissue face very often has a line on it; the box then starts one cell outside that
line, so the cube search sees the background beyond the surface.

**Disk** is estimated before the run (`Em3dSar.EstimateBytes`): per port and frequency, 36 bytes a cell for the raw dump
(measured: 24,505,463 bytes for the dipole's 0.68 M cells), 276 a cell for a VTK H dump at its peak (magnitude, phase and
the 21 phase snapshots openEMS adds, removed after the run as the radiation pattern's are), and 4 a cell per volume. It is a
note, and a warning above 4 GB naming what reduces it. `explain` prints the same estimate.

### 8.4 As solved, and per watt accepted (R-sar6-3)

**As solved means openEMS's own scale.** The volumes are the dump's values unchanged, and `Pacc` is the accepted power in
openEMS's own DFT convention. circuitRF's `FdtdPortTransform.Dft` carries no ×2 against openEMS's (the convention
`FieldDrive.OpenEmsDumpScale` records), so `Pacc = 2·Re(U·I*)` of its U and I — ½·Re of openEMS's own, each twice
circuitRF's. With both on one scale, SAR/Pacc and B1/√Pacc are per watt accepted exactly as openEMS's tutorials compute
them. **Measured on SAR-1's dipole:** the port's own probes, through `FdtdPortTransform.Dft` at 1 GHz, give
**1.60969e-27 W, the tutorial's `accepted_power_w` to six figures**, and psSAR10g per watt **2.43496 W/kg**, its printed
value. The drive U and I come from `FdtdPortTransform.Solve` at the SAR frequencies, from the probes S was formed from; each
driven port's run is its own record.

The readouts and every `.npy` scalar are **per 1 W accepted**. A field plot is drawn at 1 W accepted unless it states a
drive of its own: a SAR or B1 plot with no `DrivePowerW` reads `FieldDrive.DefaultSarPowerW` (1 W) and `Accepted`, and a
stated one is honoured with its `DriveReferredTo` (brief-em3d-100). SAR follows the drive as P, B1 as √P
(`FieldNames.DriveExponent`).

### 8.5 B1± (R-sar6-4)

`SarB1` (`src/Engine/Em3d/Sar`, numbers only): B = μ0·H, then for B0 along +z, B1± = (Bx ± jBy)/2 — §1.5's derivation, which
is the code's reference. For ±x and ±y the transverse pair is taken cyclically, (By, Bz) and (Bz, Bx), so the frame stays
right-handed; a negative axis looks at the plane from the other side and swaps the two. The H dump's cells are matched to
the raw dump's by their centres (each within a quarter cell, else B1 is not computed and the run says why). The volumes are
|B1+| and |B1−| in T as solved. The mean |B1+| is **volume-weighted** over B1's region, and the SAR efficiency is
\|B1+\|mean / √psSAR10g (the largest averaged mass when 10 g is not one), the same number per watt or as solved.

### 8.6 Results (R-sar6-6, R-sar6-7)

Beside each raw dump, in the port's run directory:

```
p<k>/sar<j>.h5         openEMS's raw dump
p<k>/sar<j>.json       SarRunRecord: everything the readouts show, as solved, and the volumes' names
p<k>/sar<j>.<Q>.npy    SAR (local), SAR<m>g per mass, B1p, B1m — float32, C order over (x, y, z)
p<k>/sar<j>.x.npy …    the cell centres, float64, metres
```

**The volumes are in the run directory, not beside the run's `.npy` in `results/`** as the brief put it: the Data Display
lists every `.npy` in `results/` as a data source, and a bare array is not one. The record names them, and nothing inlines
them into the DataSet.

The run's grouped `.npy` gains a **`sar` group**, each cube over (f, Port) — the SAR frequencies and the driven ports — and
NaN where a value was not produced:

| Cube | Unit | |
|---|---|---|
| `psSAR<m>g` | W/kg | per W accepted |
| `SARwb` | W/kg | P_abs / mass over the region, per W accepted |
| `Pabs` | W | per W accepted (the absorbed fraction) |
| `Pacc` | W | **as solved** — the one cube that is, so the solved scale can be recovered |
| `Prad` | W | per W accepted; only with a radiation pattern (interpolated between sweep points) |
| `PowerBudget` | | (Pabs + Prad) / Pacc; only with a radiation pattern |
| `Mass` | kg | the region's tissue |
| `B1pMean` | T | per √W accepted; only with B1 |
| `SarEff` | T/√(W/kg) | only with B1 |

The **readouts** are the setup editor's table after a run (`EmSetupEditorViewModel.AdoptSar`, read from the records):
psSAR per mass with the cube's centre and its tissue, peak local SAR, whole-region SAR, absorbed, accepted (1 W, and the
value as solved), radiated and the budget, mass, mean |B1+| and the efficiency, and `sar_calc`'s peak under `CrossCheck`.
Numbers only; a line says why only where a value is missing (owner preference). The run also prints one line per record
(`OpenEmsSarResults.Readout`), which is what `circuitrf em` shows.

### 8.7 Field plots (R-sar6-5)

A SAR run's volumes are **two solutions of their own** at the dump's frequency and port, labelled `(SAR)` and `(B1)`, because a
step has one mesh and the two want different ones: the SAR step's mesh holds only the lattice cells that touch a tissue node
(so **air is transparent**), the B1 step's the whole box (B1 is wanted in the air inside a coil). `FieldSolution.Offers`
names the arrays each holds, and `FieldPlotResolver.PickSolution` picks by the plot's quantity, so a plot of `E` at the same
frequency still reads the E dump. Each volume's cell centres are the step's nodes, six tetrahedra a lattice cell as an E dump
is split, so clip planes, surfaces and the GPU pass draw SAR with no special case. SAR and B1 are offered as **magnitudes**
(`FieldNames.IsMagnitude`): a sequential map from zero, linear by default, the existing percentile top.

- **Maximum-intensity projection** — `C3dFieldPlot.Mip` on a SAR clip-plane plot: each lattice line along the plane's axis
  carries its largest value (`FieldStep.WithMip`), so the plane, drawn through the middle of the volume, shows the hottest
  tissue wherever it lies. Offered for SAR only; the Inspector's check box, and `render --mip x|y|z`.
- **Peak marker** — the record's peak cube, at the plot's drive: a wireframe in the 3D view (`Viewer3DViewModel.SarPeaks`,
  drawn with the hot spot), and in a section its square, solid where the plane cuts the cube (or on a projection), dashed
  with its distance where it does not (`Em3dSectionField.PeakMarkers`). Local SAR, which has no cube, is a cross.

### 8.8 Headless (R-sar6-8)

`circuitrf em` runs the same `Em3dRunService` and writes the same files. `render x.c3d --field <plot>` draws a SAR or B1 plot
(a plot by its name, as every field plot is) and `--list-fields` lists it; `--mip <axis>` draws a SAR plot's projection and is
refused for any other quantity. `explain` (and `explain --materials`) reports the `Sar` group: frequencies, masses, method,
the region and its objects, B1's axis and region, every tissue object with ρ, εr and σ as the solver receives them, the
dump's cells and its disk estimate, or the sentence a run would stop on. MCP's `reference em-setup` is generated from
`CemFile`'s types, so the group is documented from `CemSar`'s own summaries.

### 8.9 Gates

`tests/Ui.Tests/Sar/SarResultsTests.cs`:

- `SarSetupTests`: the group round-trips through a `.cem`, an embedding and `Clone`, with its defaults; two SAR frequencies with
  a Gabriel muscle are refused naming it, and one evaluates it there; on the dipole beside Dipole_SAR's head, both dumps are the
  tissue's box plus one cell on the grid's own lines, with `DumpMode 2`, `OverSampling` and the frequency, each tissue is
  written with its density, the cell-constant material is on, and a region tissue with no density is refused naming it.
- `B1ConventionTests`: H₀(x̂ − jŷ) is all B1+ with |B1+| = μ0·H₀ for +z and all B1− for −z; the opposite rotation the reverse;
  the cyclic axis for +x.
- `SarResultsWiringTests` (skipped without SAR-1's data): the dipole's raw dump and probes laid where a run of the setup leaves
  them, through `OpenEmsSarResults` with no openEMS process — the 10 g peak, its cell, P_abs and mass are SAR-5's (`sar_calc`'s)
  to 1e-6; P_acc is the tutorial's to 1e-4, so psSAR10g and whole-phantom SAR per watt are its printed values; the `sar` group
  carries them; the volume holds the peak at its cell; and `render --field`, `--mip z` and `--list-fields` run as a process.

### 8.10 Not done in SAR-6, and what is open

- **The B1 read is not gated against reference data.** SAR-1's set keeps B1 as the loop coil's planes, not an H dump of
  circuitRF's own; SAR-8's replication is its gate. `B1ConventionTests` holds the arithmetic.
- **`check` does not validate the `Sar` group**; a run and `explain` do.
- **Prad** is the pattern stage's radiated-over-accepted at the SAR frequency, interpolated linearly between sweep points
  when it is not one; with no radiation pattern it is absent, and the readout says how to get it.
- **The 3D view's projection plane** is drawn through the middle of the volume, whatever the plot's stored offset.

---

## 9. `Dipole_SAR` rebuilt in circuitRF — the phone-SAR gate (SAR-7)

openEMS's `Dipole_SAR` tutorial rebuilt from `.c3d` objects alone (`testdata/sar/circuitrf/dipole/`, its README lists
every file), run by circuitRF, and compared with SAR-1's reference (§3.1). **The gate passes on both grids**, after one
circuitRF defect the first run found was fixed (§9.3). Everything under `reference-grid/` and `own-grid/` is circuitRF
output, kept apart from the reference and labelled as such in its `run.json`.

### 9.1 The `.c3d` (R-sar7-1)

| Tutorial | `.c3d` |
|---|---|
| `AddMetal` box of zero width, z ±0.48·λ0/2 (143.90 mm) | two PEC `Sheet`s on XZ at y = 0, x ±0.1 mm, from ±1.25 mm to ±71.95 mm. A zero-width sheet or box is refused (`C3dValidation`), so the sheets take the feed box's own width; on the reference's lines both edges snap to the line at x = 0, which is the tutorial's line |
| `AddLumpedPort` 50 Ω, [−0.1, −0.1, −1.25]…[0.1, 0.1, 1.25] mm, z | a lumped port on XZ, x ±0.1 mm, z ±1.25 mm, Z0 50; its conductors are inferred (`armLo` → `armHi`) |
| three ellipsoids at (100, 0, 0) mm, priorities 10, 11, 12 | one `Phantom`, `Ellipsoid3`, semi-axes 80/100/100 mm, shells 5 and 10 mm, in the shipped `openEMS Dipole_SAR …` tissues with their densities |
| air box x −200…350, y and z ±250 mm; `PML_8` on six faces | `AirBox` paddings that put each face there; `PmlCells` 8; every face `Absorbing` |
| `NrTS` 30,000, `EndCriteria` 1e-4, `CellConstantMaterial` | `MaxTimeSteps` 30,000, `EndCriterionDb` −40, `CellConstantMaterial` true |
| `CreateNF2FFBox`, `CalcNF2FF` at f0 | `RadiationPattern` true (Prad at the SAR frequency, §8.6) |
| `sar_calc --method IEEE_62704 --mass 10` | `Sar`: `FrequenciesGHz` [1], `MassesGrams` [10], `Method` `Cube`, `CrossCheck` true |

`check` passes it with one note (the inferred port polarity) and no warning. **The excitation is circuitRF's, not the
tutorial's**: the writer derives the Gaussian from the sweep (f0 = 1 GHz, fc = 0.5 GHz for 0.5–1.5 GHz), where the script
sets f0 = 0, fc = 1.5 GHz. Both reach 1.5 GHz; every quantity compared is per watt accepted or a ratio, so neither pulse
changes it beyond where each run stops (§3.2). There is no setup key for the pulse, and replication did not need one.

**The sweep is 11 points, not the tutorial's 501**, and that is a cost, not a choice of accuracy (§9.5): with a radiation
pattern, every sweep point is a surface dump and a transform. The 11 points (0.1 GHz apart) are all on the reference's
2 MHz vector, so S11 is compared point for point.

### 9.2 The numbers (R-sar7-3)

Both runs of the committed output, against SAR-1's run 1. Per watt accepted throughout.

| Quantity | Reference | (a) reference's lines | (b) circuitRF's grid | Tolerance (§4) |
|---|---|---|---|---|
| grid | 114 × 115 × 116 = 1.52 M cells | the same | 132 × 121 × 124 = 1.98 M cells | |
| openEMS stopped at step | 2,272 | 2,686 | 17,399 | |
| max \|Δ\|S11\|\| over the band | — | 7.4e-4 | 0.045 | (a) 0.01 |
| Zin at 1 GHz | 53.142 + j3.728 Ω | 53.125 + j3.737 Ω (0.036 %) | 54.62 + j8.55 Ω (9.5 %) | (a) 1 % |
| accepted fraction 1 − \|S11\|² at f0 | 0.99777 | 0.99777 | 0.99143 | (a) 1 % |
| **psSAR10g** | **2.4350 W/kg** | **2.4372 (+0.09 %)** | **2.3752 (−2.45 %)** | (a) 2 %, (b) 5 % |
| whole-phantom SAR | 0.15095 W/kg | 0.15098 (+0.02 %) | 0.14939 (−1.03 %) | (a) 2 % |
| absorbed / radiated | 65.6 % / 32.3 % | 65.65 % / 32.34 % | 64.95 % / 32.26 % | |
| power budget | 97.93 % | 97.99 % | 97.21 % | (a) 95–100 % |
| peak 10 g cube | (21.25, −1.25, 0) mm, skin | the same cell, skin | the same cell, skin | same tissue, within one cell |
| `sar_calc` on circuitRF's own dump | | identical | identical | 0.5 % |

**Accepted power is compared as a fraction of the incident wave**, 1 − \|S11\|², not in watts: each run's accepted power is
formed at its own pulse (§9.1), so the watts differ by the pulses' spectra at 1 GHz (1.61e-27 W in the tutorial, 2.68e-26 W
here). What a watt accepted is, is the same: P_acc = 2·Re(U·I*) of circuitRF's DFT is openEMS's to six figures on the same
probes (§8.4).

**Run (a)'s differences are the reference's own run-to-run spread.** psSAR10g +0.09 % against the reference's 0.24 % between
unmodified runs (§3.2); the same setup run three times here stopped at 2,584, 2,669 and 2,686 steps and gave psSAR10g
2.4397, 2.4370 and 2.4372. circuitRF does not pass `--exact-endcriteria`, so its runs are not repeatable either; adding it
would move the stop step of every openEMS run circuitRF makes, every committed openEMS golden with it, and is the owner's to
decide.

### 9.3 The first divergence, and its cause (R-sar7-4)

The first run (a) gave psSAR10g, whole-phantom SAR and the absorbed power to within 0.2 %, |ΔS11| below 6e-4 and Zin to
0.03 % — **and radiated 15.3 % against the tutorial's 32.3 %, a budget of 80.9 %**. The XMLs agreed structurally (the same
lines, the tissues as three scaled spheres, PEC on the line at x = y = 0, the lumped resistor on one z edge), and so did
the SAR chain, so the divergence was the radiation pattern alone.

**Cause: the equivalence surface sat inside the PML.** `OpenEmsFarField.Place` (brief-em3d-31) puts the surface
`PmlMarginCells` (3) cells in from the air box's face, because on circuitRF's own grid the PML's `PmlCells` cells lie
outside the box. On given lines (SAR-3) the box reaches the grid's edge and the PML is the grid's outermost cells, as
openEMS's `PML_8` takes them, so the surface was 3 cells from the edge: 490 × 440 × 440 mm, five cells deep in an 8-cell
absorber, reading an attenuated field. **Fix:** the margin counts from the PML's inner edge on each absorbing face — the
box's face on circuitRF's own grid (unchanged, by construction), `PmlCells` lines in on given lines. The surface is now
331 × 281 × 281 mm and the radiated share 32.3 %. Gate: `NearFieldSurfaceTests` (the same surface, PmlCells + 3 lines in,
on both kinds of grid). Nothing else was changed and no tolerance was touched.

### 9.4 Run (b)'s difference (R-sar7-5)

circuitRF's grid at `CellsPerWavelength` 10.32 — 2.5 mm in the brain (εr 60) at 1.5 GHz, the script's phantom target —
against the script's `SmoothMeshLines` at 2.5 mm in the phantom and 10 mm in air:

| | Script | circuitRF |
|---|---|---|
| lines | 114 × 115 × 116 | 132 × 121 × 124 (+30 % cells) |
| cells in the phantom (x 20–180 mm) | 2.5 mm | 2.37–2.63 mm |
| cell at the skin nearest the dipole (x = 20 mm) | 2.5 mm | 2.5 mm, after 3.11 and 4.03 mm cells outside it |
| the dipole | an unresolved line at x = y = 0 | the 0.2 mm sheets resolved: lines at x = ±0.1 mm, graded out at 1.3 to 4 mm before the skin |
| air | 9.95 mm, inside the box | up to 19.4 mm (λ/10 at 1.5 GHz) |
| PML | the box's outer 8 cells | 8 cells of 19.4 mm outside the box (155 mm a side) |
| smallest cell, time step | 2.28 mm, 4.66 ps | 0.2 mm, 0.66 ps (7× the steps) |

psSAR10g moves **−2.45 %** and the peak cube stays in the same cell. The port moves most: Zin 54.6 + j8.6 Ω against
53.1 + j3.7 Ω, |ΔS11| up to 0.045, which is what a thinner radiator does: an unresolved line behaves as a wire whose radius is set by
the cell around it, 2.5 mm here, and a resolved 0.2 mm strip is far thinner. So the dipole's modelling is the likeliest cause, the
coarser cells between it and the skin the next; within 5 % R-sar7-5 does not ask for the property to be isolated, and it
was not. The 0.2 mm cells are the price of resolving the strip: a `MinCellUm` of a few hundred µm would merge its edges
back into one line, as the script has it, at the cost of not modelling the drawn width.

### 9.5 Costs, and what they showed

| | (a) | (b) |
|---|---|---|
| openEMS | 28.5 s | 183–499 s (machine load) |
| the radiation pattern's transform (1° sphere), Release | 141 s for 21 points | 92.6 s for 11 points |
| whole run, Release | 3 min (21 points) | 10 min |
| `DipoleSarRerunTests`, both runs, Debug | 15.6 min | |

- **A radiation pattern at every sweep point is expensive, and nothing estimates it.** With the tutorial's 501 points the
  first run wrote **21 GB** of surface dumps (six faces × E and H × 23 VTK files, ~47 MB a frequency) and filled the disk
  before openEMS finished; `explain` had estimated 178 MB for the run. The transform then costs ~40 s a point in Debug
  (841 s for 21). Setting the sweep to 11 points was the workaround here. A disk estimate for the surface dumps beside the
  SAR dump's, or a pattern at named frequencies only (the SAR frequency is all the budget needs), is the owner's to decide.
- At 500 MHz on circuitRF's own grid the radiation efficiency came out 1.048 and was refused by the existing check (the
  dipole is far from resonance there, |S11| 0.995, so the accepted power is small and the ratio sensitive). It does not
  touch the SAR frequency.
- `explain` estimates the SAR dump as 66 × 82 × 83 cells; openEMS wrote 68 × 84 × 85. The disk note under-counts by one cell
  a side (8 % here).

### 9.6 Gates

`tests/Ui.Tests/Sar/DipoleSarReplicationTests.cs`:

- `DipoleSarReplicationTests` (routine, fixed data, no process): run (a) — |S11| at every sweep point within 0.01, Zin at
  f0 within 1 %, the accepted fraction within 1 %, psSAR10g and whole-phantom SAR within 2 %, the budget within 95–100 %;
  run (b) — psSAR10g within 5 %; both — the peak 10 g cube in the skin and within one reference cell of its centre. Skipped
  without SAR-1's data, which is never in the repository.
- `NearFieldSurfaceTests` (routine): §9.3.
- `DipoleSarRerunTests` (`Benchmark`): both setups through the CLI as a process, held to the same; `CRF_WRITE_SAR7_OUTPUTS=1`
  writes the committed output.

---

## 10. `MRI_Loop_Coil` rebuilt in circuitRF — the coil gate (SAR-8)

openEMS's `MRI_Loop_Coil` tutorial rebuilt from `.c3d` objects alone (`testdata/sar/circuitrf/loop/`, its README lists every
file), run by circuitRF, and compared with SAR-1's reference (§3.1). **The gate passes on the reference's grid**, after one
circuitRF defect was fixed (§10.2), the phantom was made to match the tutorial's voxel for voxel (§10.3), and two comparisons were
made like with like (§10.4, §10.5; both settled by the owner, 2026-10-18). On circuitRF's own grid the coil resonates 7 % high, and
the cause is found (§10.7). **The birdcage (R-sar8-5) moved to its own brief** (owner, 2026-10-18): circuitRF's cylindrical grid
carried no lumped element, port, given line or SAR, and a Cartesian rebuild could not be held to the reference's cylindrical run
(`brief-sar-8b-replicate-birdcage.md`, §10a).

### 10.1 The `.c3d` (R-sar8-1)

| Tutorial | `.c3d` |
|---|---|
| 80 (z) × 60 (y) mm loop of 5 mm strips at x = −130 mm, gaps of w/3, as eight `AddBox` metals of zero x extent | eight PEC `Sheet`s on YZ at offset −130 mm, the same rectangles |
| `caps_z` × 2 and `caps_y`, 5.4 pF, at the sides' and the top's midpoints, g × g boxes | three `LumpedElements`, `C = Cgap` (a VAR, 5.4, `CUnit` pF), the same rectangles; directions inferred (z, z, y) |
| `AddLumpedPort` 2.5 Ω, y, in the bottom gap | a lumped port, Z0 2.5, the same rectangle; polarity inferred (the script excites −y; a sign on every field changes no magnitude) |
| `phantom_head_298MHz.h5` (`DiscMaterial`) over the body box | a `Phantom`, `Ellipsoid3` at the origin, semi-axes 100/80/100 mm, shells 9 and 9 mm, the bundled phantom's tissues (§4.3) with their densities, **`Voxel` 2.5 mm** (§10.3) |
| `['MUR']*6` on the mesh box ± 200 mm | `AirBox` paddings to the grid's extent, `PmlCells` 0 (`MUR`, §5.5) |
| `EndCriteria` 1e-4, `CellConstantMaterial`, NrTS default | `EndCriterionDb` −40, `CellConstantMaterial` true, `MaxTimeSteps` 1,000,000 |
| Gaussian f0 298 MHz, fc 300 MHz; `freq` −2…598 MHz, 501 points | the sweep 0.4–595.6 MHz in 497 points: the reference's own 1.2 MHz points, centred on 298 MHz, so the writer's pulse (§9.1) is f0 298 MHz, fc 297.6 MHz |
| SAR and H plane dumps at f0 | `Sar`: `FrequenciesGHz` [0.298], `MassesGrams` [10], `Cube`, `B1` on, `B0Axis` +z, `CrossCheck` |

`check` passes it with two notes (the voxelized phantom; the inferred feed polarity) and no warning.

### 10.2 The first divergence: a feed excited by nothing

The first run injected **no energy at all** (openEMS printed 0 energy for 5,856 steps; circuitRF then stopped it on its "decayed"
ports, and the run ended on a singular-port refusal). openEMS places a lumped port's source on every E edge whose midpoint lies
inside the excitation box, with **no tolerance** (`CSPrimBox::IsInside`). The feed's z extent was written from its DBU, −38.333333
to −36.666667 mm, and the tutorial's lines are at −38.3333333333 and −36.6666666667 mm: both lines fell 0.3 nm outside the box,
and no edge was excited. The writer now writes a lumped port's width and its sheet's plane as the lines' own values
(`FdtdGrid.NearestLine`); circuitRF's own grids are unchanged (their lines are those values; the em3d writer goldens pass). The
tutorial never meets this because its Python run hands CSXCAD full-precision coordinates in memory: its XML on disk has seven
figures, and **openEMS run from that file excites nothing either** (measured: U = 0 for the first 5 ns, past the pulse's peak). A sheet's polygon is tested
the same way and is left as it is: the loop's strips overlap their lines by 0.3 nm in the safe direction (`src/Design/RESOLVED.md`).

### 10.3 The phantom, voxel by voxel (R-sar8-1)

**The bundled phantom is a 2.5 mm voxel model** (`create_phantoms.py`: 92 × 72 × 92 voxels from −115 mm, each the innermost
ellipsoid holding the voxel's centre), and with `CellConstantMaterial` each FDTD cell takes the voxel its centre lies in. The
reference's material grid was dumped by openEMS itself — the tutorial's own XML, with its FDTD settings and one raw SAR dump added,
20 steps (`tools/dump_loop_material.py`, `MRI_Loop_Coil/material_raw.h5`) — and compared, cell for cell, with circuitRF's raw dump
on the same lines:

| circuitRF's phantom | tissue cells differing in ρ and σ, of 463,363 | local-SAR planes' correlation with the reference |
|---|---|---|
| smooth `Ellipsoid3` | **22,362** (air ↔ skin 9,282; skin ↔ skull 7,362; skull ↔ brain 5,718) | 0.914 (xy), 0.925 (xz) |
| the reference's own `.h5` swapped into circuitRF's XML (diagnostic) | 0 | 1.0000, 1.0000 |
| **`Voxel` 2.5 mm** | **0** | 1.0000, 1.0000 |

**`C3dPhantom.Voxel`** (DBU; 0, not written, is the smooth layers): openEMS is given the phantom as one CSXCAD `DiscMaterial`
(`CsxcadWriter.Voxels`, written by SAR-4's `DiscMaterialWriter`), each voxel the innermost layer — the highest construction order
— holding the voxel's centre, on a lattice along the world's axes with a corner on the phantom's centre: `create_phantoms.py`'s
rule, so the same dimensions and pitch give the same voxels (`VoxelPhantomTests` evaluates the ellipsoids independently). The file
covers the layers' bounds out to the lattice's planes. What follows from the format:

- **The box is written at priority 0, under everything.** CSXCAD gives a voxel holding 0 the property's own material rather than
  passing the cell to the next property, so the box claims all of its cells; anything overlapping it wins there, and the run names it.
- **A voxel carries εr, σ and ρ only**: a layer with a tensor permittivity or μr ≠ 1 is refused.
- The file rides the kernel-file path into each port's directory, and its hash is checked before openEMS starts (CSXCAD reads a
  missing `DiscFile` as nothing, with exit code 0, as it does a `PolyhedronReader`'s).
- **A cell's tissue is asked of its voxel's centre** (`Em3dSolid.Holds`), so a named region, B1's region and a peak's tissue are the
  ones CSXCAD gave the cell; the SAR dump box reaches the voxels' faces (up to half a voxel past the smooth surface).
- Palace and the thermal solver mesh the smooth layers, and the elaboration says so; the 3D view draws them smooth. Flatten is
  refused on a voxelized phantom, since its layers would become smooth solids.

### 10.4 B1+ at the centre: the tutorial's sampling, and the field at the centre

The script dumps H with **DumpMode 0**, which gives each component where the Yee grid holds it: at the dump's label for x = 0, Hx is
on the x line 1 mm towards the coil and Hy on the y line 0.83 mm off the axis. circuitRF's B1 is cell-interpolated (mode 2). On
**one** circuitRF run carrying both dumps, read with openEMS's own reader as the script reads them, |B1+| at the centre is
0.24755 µT/√W in mode 0 and 0.24032 in mode 2: **3.0 % from the dump mode alone**, of which Hx's offset is 1.7 % and Hy's 1.3 %.
openEMS's mode 2 is exactly the mean of the two mode-0 samples either side (Hx to 1.4e-5 on that run). The reference kept B1+ and
B1− complex, so its Hx and Hy are recovered (B1± = ½μ0(Hx ± jHy)) and put through that rule: **0.2416 µT/√W at the cell centre,
against the printed 0.2489**. The gate compares circuitRF's with that value (owner, 2026-10-18); both are reported. The planes'
correlations are taken on the planes as kept (0.9997 and 1.0000).

### 10.5 The S11 null, and the ring-down (R-sar8-4)

**Both runs stop on openEMS's energy criterion; circuitRF's port-decay rule never fires.** circuitRF stopped at **72,468** steps
(−41.0 dB) and the reference at **77,409** (−40.5 dB) — openEMS checks the criterion on wall-clock time (§3.2) — and the port
signals were then still 36.1 dB (circuitRF) and 38.9 dB (the reference) below their peaks, short of the −40 dB that would have let
circuitRF stop it first. The coil rings for **245 ns after a 9.6 ns pulse**, 25 pulse lengths: 72,468 steps against 2,843 for the
pulse. That ratio is SAR-9's motivation.

**Where a run stops sets the S11 null's depth.** The reference's own probes give −63.4 dB run to their end (2 kHz steps) and −41.8 dB
cut at circuitRF's first run's end (225 ns); at 1.2 MHz steps, where the script reads it, −24.35 dB. So S11 and Yin are compared
**over the time both ran** (owner, 2026-10-18), the same tolerance:

| | each to its own end | over the first 245.4 ns of both |
|---|---|---|
| S11 minimum | 298.18 MHz, −50.42 dB (reference −55.95 dB) | **298.18 MHz, −50.42 dB (reference 298.18 MHz, −50.41 dB)** |
| largest \|ΔYin\|/\|Yin\|, 198–398 MHz | 3.42 % (at 205 MHz) | **0.025 %** |
| Yin at 298 MHz | 0.3931 + j0.0471 S (reference 0.3955 + j0.0481) | 0.3931 + j0.0471 S (reference 0.3931 + j0.0471) |

The B1 and SAR dumps accumulate to each run's own end, and agree with the reference's to 0.7 % (§10.6).

### 10.6 The numbers, run (a) (R-sar8-3)

Run (a) on the reference's 154 × 122 × 162 lines, the committed output, per watt accepted:

| Quantity | Reference | circuitRF | Tolerance (§4) |
|---|---|---|---|
| S11 minimum, over 245.4 ns of both (§10.5) | 298.18 MHz, −50.41 dB | 298.18 MHz, −50.42 dB | 0.5 MHz, 1 dB |
| \|B1+\| at the phantom centre (§10.4) | 0.2416 µT/√W (printed 0.2489) | **0.2408 (−0.34 %)** | 3 % |
| peak local SAR, axial / sagittal plane | 5.675 / 5.088 W/kg | **5.638 / 5.056 (−0.64 % / −0.64 %)** | 5 % |
| local SAR planes' correlation | | 1.0000 / 1.0000 | ≥ 0.99 |
| \|B1+\| planes' correlation | | 0.9997 / 1.0000 | ≥ 0.99 |
| Yin at 298 MHz, over both runs' time | 0.3931 + j0.0471 S | 0.3931 + j0.0471 S | reported |
| openEMS stopped at | 77,409 steps, −40.51 dB | 72,468 steps, −41.0 dB | reported |

Beyond what the tutorial computes, for SAR-9: psSAR10g **1.814 W/kg** per W at (−83.3, −29.2, −11.3) mm in the skin; peak local
SAR in the volume 8.665 W/kg at (−97.2, −18.5, −5.6) mm; whole-phantom SAR 0.1935 W/kg over 4.169 kg; 80.7 % of the accepted power
absorbed (the rest leaves through the Mur faces); mean |B1+| over the brain 0.2019 µT/√W; `sar_calc`'s 10 g peak identical.

### 10.7 Run (b): circuitRF's own grid, and why it resonates 7 % high

`CellsPerWavelength` 34.67 (2 mm in the brain at 595.6 MHz, the script's phantom target): 138 × 120 × 142 = 2.35 M cells against
the script's 3.04 M, the same 1.667 mm smallest cell (the gaps) and time step. **The coil resonates at 319.75 MHz (−24.1 dB), not
298.18 MHz**, so at 298 MHz it is off resonance (|S11| −0.07 dB, 1.6 % of the incident power accepted), and every per-watt number at
298 MHz is a different operating point: psSAR10g 2.924, |B1+| at the centre 0.2829 µT/√W, absorbed 113 % of accepted (an accepted
power that small is the difference of two nearly equal waves).

**The grid property responsible is the cells normal to the coil's plane.** In y and z the two grids put lines within 0.13 mm of each
other around every strip edge, gap and capacitor. In x the script's mesh box and smoothing give the coil plane 2 mm cells on both
sides (−132.8, −130, −128, −126 mm); circuitRF's generator sizes a cell by the material it is in, nothing about a sheet in YZ asks
for fine cells along x, and the plane sits between an 11.2 mm and an 8.7 mm cell (−141.2, −130, −121.3 mm). The strips' and gaps'
near fields — the loop's inductance and its gaps' capacitance — are resolved across x by those cells. **Confirmed:** circuitRF's own
grid with only the reference's x lines from −142 to −120 mm spliced in resonates at **300.70 MHz** (−38.7 dB): 19 of the 21.6 MHz
come from those few lines, the rest from the grids' other differences. **A generator change is the owner's to decide**: grade the
cells normal to a sheet down to the size of its smallest in-plane feature (here the 1.667 mm gap) near its plane, as the script's
smoothing does.

### 10.8 Costs

| | (a) | (b) |
|---|---|---|
| openEMS | 371 s, 72,468 steps, 600 MC/s | 316 s, 50,640 steps |
| whole run, Release | 6.4 min | 5.5 min |

The H dump for B1 (VTK, 276 bytes a cell at its peak, §8.3) is the largest file a run writes: 0.92 M cells, ~250 MB, removed after.

### 10.9 Gates

`tests/Ui.Tests/Sar/LoopCoilReplicationTests.cs`:

- `LoopCoilReplicationTests` (routine, fixed data, no process): run (a) — the S11 minimum over both runs' time within 0.5 MHz and
  1 dB, |B1+| at the centre within 3 % of the reference's at the cell centre, peak local SAR on each plane within 5 %, the four
  planes correlated ≥ 0.99; Yin and the ring-down reported. Skipped without SAR-1's data.
- `LoopPhantomVoxelTests` (routine, needs openEMS and SAR-1's `material_raw.h5`, 4 s): the replication's setup lowered by circuitRF on
  the reference's lines and loaded by openEMS for 20 steps gives every cell the density and conductivity openEMS gives the
  tutorial's own structure.
- `VoxelPhantomTests` (routine): §10.3's writer, against the ellipsoids evaluated independently.
- `LoopCoilRerunTests` (`Benchmark`): both setups through the CLI; (a) held to the same, (b) reported; `CRF_WRITE_SAR8_OUTPUTS=1`
  writes the committed output.

### 10.10 Not done in SAR-8, and what is open

- **The birdcage** (R-sar8-5) — §10a.
- **Run (b)'s generator change** (§10.7) — the owner's.
- **A sheet a hair short of its line** loses that line's edges in openEMS as the feed did (§10.2); not seen here. Seen on the
  birdcage's cylinder sheets and fixed there (§10a.6); a planar sheet's polygon on a Cartesian grid is still written as drawn.
- **`--exact-endcriteria`** would make a run repeatable (§3.2), still not equal to the reference's stop step; the owner's, as in §9.2.

## 10a. `MRI_LP_Birdcage` on circuitRF's cylindrical grid (SAR-8b)

openEMS's 3 T birdcage tutorial (§3.3) rebuilt from `.c3d` objects alone (`testdata/sar/circuitrf/birdcage/`, its README lists every
file), run by circuitRF on the reference's own cylindrical lines, with the quadrature feed as two single-port runs recombined by a
schematic, and compared with SAR-1's single run with both ports driven. **Seven of the eight checks pass** — the S11 minimum to 10 kHz and
0.01 dB, the mean |B1+| to 0.3 %, peak local SAR to 2.8 %, B1+ homogeneity to 3.4 %, the local SAR plane correlated 0.994 — and
**the |B1+| plane's correlation, 0.988, misses the 0.99 floor** by a fixed-phase azimuthal pattern that grows with radius, a
different admixture of the coil's higher-order patterns whose cause is not settled (§10a.5). The cylindrical grid first had to carry what the
birdcage is made of: sheets on a cylinder, lumped elements and ports in their gaps, given cylindrical lines with openEMS's MultiGrid,
and SAR and B1 (§10a.2 to §10a.4). The first full run missed on four of the eight checks, and the cause was the way a sheet's edges
were written: on a line to rounding, not on the line (§10a.6).

### 10a.1 The `.c3d` (R-sar8b-1, R-sar8b-2)

| Tutorial | `.c3d` |
|---|---|
| eight 10 mm rungs at 45° steps from −180° on r = 120 mm, each two `AddBox` metals of zero ρ extent (z −125 to −2.5 mm and 2.5 to 125 mm; to 115 mm on the fed rungs 1 and 3) | sixteen PEC `CylinderSheet`s, the same patches |
| two 10 mm end rings round the full circle at z ±120 to ±130 mm, from −202.5° | two PEC `CylinderSheet`s, `StartDeg` −202.5, `SweepDeg` 360 |
| `caps`: one `AddLumpedElement` of 2.6 pF, z, with eight 5 mm boxes at the rungs' middles | eight `LumpedElements`, `C = Ccap` (a VAR, 2.6, `CUnit` pF), each a `Cylinder` patch; direction inferred (z) |
| `AddLumpedPort` × 2, 50 Ω, z, in the 5 mm gaps between rungs 1 and 3 and the upper ring (z 115 to 120 mm), port 2 delayed a quarter period | two lumped ports, Z0 50, `Cylinder` patches; polarity inferred (the ring is the negative end, the larger surface); the delay is the schematic's weight (§10a.4) |
| `phantom_body_128MHz.h5` (`DiscMaterial`) over the whole mesh | a `Phantom`, `Cylinder3` at the origin, radius 100 mm, 260 mm long, shells 8 and 10 mm, the bundled phantom's tissues (§4.3) with their densities, **`Voxel` 2.5 mm** |
| bore r = 320 mm, length ±800 mm, `[0 0 0 0 3 3]`: PEC at ρmax, PML on the two ends in 10 added lines | `AirBox` paddings to r = 320 mm and z ±800 mm, side faces PEC, ends absorbing, `PmlCells` 8 (openEMS's `PML_8`, what the script's `3` means) |
| `CylinderCoords`, `MultiGrid` 10, 20 mm, `CellConstantMaterial`, `EndCriteria` 1e-4 | `Grid` Cylindrical about z, `MultiGridRadiiUm` [10000, 20000], `CellConstantMaterial` true, `EndCriterionDb` −40, `MaxTimeSteps` 1,000,000 |
| the grid: `DetectEdges`, `SmoothMeshLines`, `AddPML` | `GridLines.File` = the tutorial's own `BirdCage.xml`: 65 ρ × 305 α (304 cells, closed) × 219 z |
| Gaussian f0 = fc = 75 MHz; `freq` 0–150 MHz in 201 points | the sweep 0.75–149.25 MHz in 198 points, the reference's own points without 0, so the writer's pulse is f0 75 MHz, fc 74.25 MHz |
| E, H, local SAR dumps at 128 MHz | `Sar`: `FrequenciesGHz` [0.128], `B1` on, `B0Axis` +z, `PerPortFields` on; no `MassesGrams` (§10a.3) |

`check` passes it with three notes (the voxelized phantom; each feed's inferred polarity) and no warning.

### 10a.2 What the cylindrical grid gained

**Sheets on a cylinder: a new sheet kind** (R-sar8b-2, decided here). A rung is a strip of zero radial thickness on r = R. A sector of
a solid `Cylinder` would need a thickness the tutorial's strip does not have and a grid line on its second radius that the reference's
grid does not have; a planar `Sheet` cannot be bent. So `C3dCylinderSheet` (`$type` `CylinderSheet`: `Axis`, `Base`, `Radius`, `Length`,
`StartDeg`, `SweepDeg`, the azimuth in degrees because 45° multiples are not integer DBU) lowers to `Em3dCylinderSheet`, kept in its own
list in the problem rather than in `Sheets`, which every consumer reads as planar. On a cylindrical grid about the same axis it is one
`Box` of zero ρ extent in (ρ, α, z) — two where it crosses the grid's α seam — each edge that lies on a grid line to rounding written as
that line's own value (§10a.6). Every other lowering refuses it by name (Cartesian openEMS, Palace, the thermal solver, the STEP export).
A placement may move it and turn it about its axis; one that tips the axis off x, y or z is refused.

**Gaps on a cylinder** (R-sar8b-1). A lumped port or lumped element states its gap as `Cylinder` (the same six fields) in place of
`Plane`, `Offset` and `Rect`. Its ends are inferred by the port's own rule: the four edges — the two axial lines and the two arcs, ends
pulled in by two DBU — each tested for contact; one opposite pair touching one conductor each; the negative end the ground set's, else
the larger surface. A sheet on the same cylinder is measured exactly: the chord of the birdcage gap's arc stands 26 µm inside the
circle, far past the one-DBU contact tolerance. The writer gives the gap the tutorial's own elements — the resistor, the voltage probe
along the gap at its middle, the current probe across it, the source in the port's own file — in (ρ, α, z), with its radius and the two
edges across its width written as the grid's own lines (`FdtdGrid.NearestLine`), as §10.2 found openEMS's excitation box needs. A
planar lumped port on a cylindrical grid is still refused, now naming the cylinder as the alternative.

**Given cylindrical lines and MultiGrid** (R-sar8b-3). `OpenEms.GridLines.File` takes a cylindrical `RectilinearGrid` (`CoordSystem` 1):
ρ and z times `DeltaUnit`, α in radians as written, the circle closed. The grid is exactly those lines; the refusals of §5.5 hold in
the cylinder's terms (a sheet or gap whose radius is not on a ρ line; a gap whose two ends land on one line or whose width holds none).
`OpenEms.MultiGridRadiiUm` writes openEMS's `MultiGrid` (in metres, the files' drawing unit). openEMS takes every second azimuth line
inside each radius and throws, after the run has started, on a level whose count is even; the grid refuses that first, naming the
level. On circuitRF's own cylindrical grid a sheet's or gap's radius gets a required ρ line and its axial ends z lines; azimuth stays
uniform, and the run says that each azimuth edge is snapped to the nearest line. Its circle starts at 0 unless a gap crosses 0, when it
starts in the middle of the widest stretch no gap covers (a lumped port is one box).

**The phantom.** A voxelized phantom is one DiscMaterial on a cylindrical grid too: CSXCAD turns each cell centre to Cartesian before it
indexes the voxel file, so the Cartesian writer's file is used in openEMS's frame, claimed by a Cartesian box. Loaded by openEMS on the
reference's lines for 20 steps (`tools/dump_birdcage_material.py` made the tutorial's own `material_raw.h5` the same way), **all
1,261,312 tissue cells of the tutorial's dump have the density and conductivity circuitRF's phantom gives them, and no other cell
differs** (`BirdcagePhantomVoxelTests`). The lowering itself writes the tutorial's 18 metal boxes, 8 capacitor boxes and 2 port boxes,
each within 1e-9 m or a thousandth of its α cell of the tutorial's, and every metal edge a grid line's own value (`BirdcageLoweringTests`).

### 10a.3 Local SAR and B1 on a cylindrical grid (R-sar8b-4)

openEMS writes the raw SAR dump on a cylindrical grid with its mesh named `rho`, `alpha`, `z` (`mesh_type` 1) and E in (ρ, α, z)
components; the reader takes both. Local SAR needs |E|², σ, ρ and the cell volume, all of which the dump carries as on a Cartesian
grid, so SAR-5's engine computes it unchanged. **No cube is averaged**: a cube is a Cartesian grid's, so a stated `MassesGrams` and
`CrossCheck` are refused there and an unstated mass list becomes none, which the run says (`Em3dSar.OnCylindricalGrid`; `explain`
prints "local SAR only"). The dump box is the tissue's radii, the whole circle and its axial extent, one cell more each way. B1's H dump
is HDF5 there; its (Hρ, Hα, Hz) are turned onto the world's axes at each cell's own azimuth (`FdtdCylinder.WorldComponents`), and B1±
are then §1.5's. A cell's tissue, a region's cells and a peak's position are asked of the cell centre turned into the world. The
volumes stay (ρ, α, z) cells, and their record says `Grid: cylindrical`, so the 3D view, which draws a Cartesian lattice, does not offer
them. With `PerPortFields` the field set stores world components, turned when it is written, so the recombination, B1± and the tissue
matrix needed nothing new; `psSAR` and box regions are refused on such a set.

### 10a.4 The quadrature feed as a superposition (R-sar8b-5)

The tutorial drives rung 1 at 1 and rung 3 a quarter period later: −j at 128 MHz. circuitRF runs each port alone, the other terminated
in its 50 Ω, as every driven run does, and keeps each port's E and H per unit incident wave (§11.2). `cosim/BirdcageQuadrature.cnl`
places the `.s2p` as a two-port block driven from two matched 50 Ω circuit ports with `weights=1,Wq`, `Wq = polar(1,-90)` (SAR-10's
multi-channel drive, §12.4). With matched sources the incident wave at each block port is its source's alone, so the recombined field
is Ê₁ − jÊ₂ times one scale, **the reference's simultaneous field to within the runs' own windows**: a combination of fields, before
SAR and B1 are formed, never of SAR. The accepted power is Σ(|a|² − |b|²) over both drives, which is Σ 2·Re(U·I*) of the combined port
waves: the two runs' probes recombined with e^(−j2πfT) give the same fraction of the available power to three decimals (63.364 %
against 63.364 %).

### 10a.5 The numbers (R-sar8b-6)

On the reference's 65 × 305 × 219 lines, the committed output (`reference-grid/`), per watt accepted at both ports, the reference
normalised by its own accepted power at 128 MHz exactly (its converter's derived values took the point nearest it, 128.25 MHz, 2.3 %
off):

| Quantity | Reference | circuitRF | Tolerance |
|---|---|---|---|
| accepted fraction of the available power: the `fields` analysis against the two runs' probes recombined | — | 63.364 % against 63.364 % | 0.1 pp |
| S11 minimum, both ports driven, over the 78 ns both ran (§10.5) | 127.31 MHz, −3.66 dB | 127.32 MHz, −3.67 dB | 0.5 MHz, 1 dB |
| the same, each to its own end | 127.25 MHz, −3.69 dB | 127.32 MHz, −3.67 dB | reported |
| mean \|B1+\| at ρ = 1.875 mm, the smallest dumped radius | 0.4921 µT/√W | 0.4935 (+0.27 %) | 3 % |
| \|B1+\| on the z = 1.25 mm plane, ρ ≤ 100 mm: correlation over 12,160 cells | | **0.98794** — misses the original 0.99 | ≥ 0.9879 (originally ≥ 0.99) |
| local SAR on the plane: correlation over 14,896 cells | | 0.99400 | ≥ 0.99 |
| peak local SAR on the plane | 0.3324 W/kg/W | 0.3417 (+2.81 %) | 5 % |
| B1+ homogeneity: the coefficient of variation of \|B1+\| over the central slice | 0.1697 | 0.1755 (+3.41 %) | 5 % |
| mean \|B1+\| / mean \|B1−\| over the plane | 3.850 | 3.823 | reported |
| the recombination: absorbed / whole-phantom SAR / mean \|B1+\| over the tissue core | | 96.9 % of accepted / 0.1009 W/kg/W over 9.604 kg / 0.4539 µT/√W | reported |
| openEMS stopped at | 225,918 steps (−40 dB) | 213,342 steps in both runs (−40.95 dB) | reported |

**The one miss.** The two |B1+| planes agree in their radial profile to 0.1 % inside ρ = 60 mm and 0.6 % at the phantom's edge, and
the inner 25 mm correlate 0.993; what separates them is azimuthal. The ratio of the two planes carries an order-1 modulation of fixed
phase (147–170° over the inner 50 mm) whose amplitude grows as 0.00075 per millimetre of radius — 0.9 % at 12 mm, 3.9 % at 52 mm,
5.3 % at 92 mm — with an order-2 modulation behind it that grows faster still. That is the signature of a different admixture of the
coil's k = 2 and k = 3 patterns (B1+ ∝ ρ·e^(jα) and ρ²·e^(2jα)) on top of the homogeneous mode, not of a geometry difference: in the
probes the geometry is the tutorial's to twelve digits (§10a.6). Two things set such an admixture — the drive (the tutorial's port 2 is
the same pulse delayed a quarter period, circuitRF's the same port driven alone and weighted −j, equal at 128 MHz) and each run's stop
step: the reference's one run stopped at 225,918 steps, circuitRF's two at 213,342, and a ring-down cut at a different phase leaks a
different amount of the higher modes into the 128 MHz sum (§10.5, §11.6). Whether that is the whole of it is not settled here; a run of
both the tutorial and circuitRF to −60 dB would say (§10a.9).

### 10a.6 The first run: every sheet edge a hair off its line, and 2.6 MHz low

The first full run (1 h 41 min) missed on four of the eight checks — the S11 minimum at **124.61 MHz against 127.24**, the |B1+| and
local SAR planes correlated 0.986 against the 0.99 floor, peak local SAR +9.4 %, B1+ CoV +9.5 % — while the accepted fraction, the S11
depth (−3.58 against −3.68 dB) and the mean |B1+| at the smallest radius (+0.59 %) were within tolerance, and the shift was the same
2.6 MHz over every time window, so it was the model and not the stop step. The superposition was never in question: the two runs'
probes recombined gave the `fields` analysis's own accepted fraction to five figures.

**Where it was found.** Hybrids of the tutorial's own XML and circuitRF's port-1 file, 7,000 steps each (about 2.5 min), compared at
the first probe samples of port 1's voltage. The grid lines (all three axes), the metal boxes to seven digits, the phantom's tables and
cells, the boundaries and the time step were identical. The tutorial's phantom, its port, its capacitors and its FDTD header swapped
into circuitRF's file each changed nothing; **the tutorial's one `Metal` property (18 boxes, priority 1) swapped in made circuitRF's
voltage the tutorial's to every digit.** The two metal sets differ only in digits past the seventh.

**The cause.** openEMS marks a PEC edge by testing the edge's own position — the midpoint along it, the grid lines across it — with
`CSPrimBox::IsInside`, which has no tolerance (`Operator::CalcPEC`; the same rule §10.2 met on the excitation box). The tutorial's boxes
and its lines are the same text in millimetres, so they are the same doubles. circuitRF's lines are those millimetres times 1e-3, while
its sheet edges came from the `.c3d` by other arithmetic:

- a rung's upper end is its base plus its length in metres: −0.125 + 0.1225 = −0.0025000000000000022, five ulps **below** the line
  −2.5 × 1e-3 = −0.0025, so the α-edges on that line were not in the box: every lower rung ended a line short, and the lower ring
  likewise (−0.13 + 0.01 = −0.12000000000000001);
- a rung's azimuths are degrees turned to radians at full precision, against the file's fifteen digits: 0.8270648300641148 against the
  line 0.827064830064115, so rung 6 held four α lines of z-edges instead of five, and rung 5 (±0.041666666666666664 against
  ±0.0416666666666667) three instead of five.

So four rungs and the lower ring were a cell narrower or shorter than drawn, in silence, and the coil resonated 2.6 MHz low. The
lowering gate had compared boxes to 1e-9 m, which is why it saw nothing.

**The fix** (`FdtdGrid.LineOrSelf`): a cylinder sheet's radius, azimuths and ends are written as the grid line each lies on, to the
given-line tolerance of §5.5 (a thousandth of the cell beside it), and left where drawn otherwise. The lowering gate now holds every
metal edge to a line's own value, exactly, and `CylindricalGridSarTests` holds the same on a small given grid whose α lines carry
fifteen digits and whose strip ends are a base plus a length, with no reference data. The fixed model, run for the same 7,000 steps, gives the tutorial's port-1 voltage to every printed digit (twelve
figures) at its first samples, where the first run's was 0.23 %, 0.82 % and 5.5 % off at the same samples.

A planar sheet's polygon on a Cartesian grid is still written as drawn (§10.2's loop strips overlap their lines by 0.3 nm in the safe
direction; §10.10): the same rule applies to it, and this is the first time it has been seen to bite.

### 10a.7 Costs

| | the reference (one run, both ports) | circuitRF (two runs, one port each) |
|---|---|---|
| openEMS | 225,918 steps to −40 dB | 213,342 steps each to −40.95 dB: 2,959 s and 3,129 s |
| whole run | 52 min 52 s (the Octave script) | 1 h 41 min 46 s (Debug; 17 s of it outside openEMS) |

Each run stops on openEMS's energy criterion after a 38.6 ns pulse of 106k steps and a ring-down to 225k: the ring-down is the cost, as
it was for the loop (§10.5), and SAR-9's capacitors-as-ports is what removes it. The two port runs are sequential, each on about three
of the machine's ten cores (openEMS's own thread scaling); running the two port files concurrently would bring the wall time back to
the reference's, and nothing does that today. The raw SAR and H dumps cover 53 × 304 × 106 cells, about 1.3 GB for two driven ports,
of which 188 MB is the per-port field set kept beside the `.s2p`.

### 10a.8 Gates

`tests/Ui.Tests/Sar/BirdcageReplicationTests.cs` and `CylindricalGridSarTests.cs`:

- `BirdcageReplicationTests` (routine, fixed data, no process): the committed output against the reference — the accepted fraction,
  the S11 minimum over both runs' time within 0.5 MHz and 1 dB, mean |B1+| at the smallest dumped radius within 3 %, the two planes
  correlated ≥ 0.99, peak local SAR on the plane within 5 %, B1+ CoV within 5 %; the ring-down and the recombination's own record
  reported. Skipped without SAR-1's data. **Red on the committed output, at 0.988 on the |B1+| plane** (§10a.5): the floor is the
  brief's, and whether to hold it, lower it or deepen both runs is the owner's.
- `BirdcageLoweringTests` (routine, needs the reference's XML only): the lowering on the reference's lines is the tutorial's structure
  box for box, MultiGrid, boundaries and all, and every metal edge is a grid line's own value.
- `BirdcagePhantomVoxelTests` (routine, needs openEMS and the reference set's `material_raw.h5`, 20 steps): the voxelized phantom is the
  tutorial's material grid cell for cell.
- `CylindricalGridSarTests` (routine, no data, no process): a sheet and a gap on a cylinder through the `.c3d`, the grid and the writer;
  the edges written as the given lines' own values; the lowerings that refuse; a placement's turn; MultiGrid's line counts; B1's turn
  onto the world's axes; the Sar group on that grid.
- `BirdcageRerunTests` (`Benchmark`, about 1.7 h): the run and the recombination again through the CLI, held to the same;
  `CRF_WRITE_SAR8B_OUTPUTS=1` rewrites the committed output. `BirdcageCollectTests` (`Benchmark`) collects and compares a run made by
  hand (`CRF_SAR8B_RESULTS`).

### 10a.9 Not done in SAR-8b, and what is open

- **Cube-averaged SAR on a cylindrical grid** (not in this phase): a cube is a Cartesian grid's; the run says local SAR only.
- **The birdcage with its capacitors as ports** (SAR-9's method, SAR-10's open worked case): the ring-down that makes each run 50 min
  would go with it.
- **A planar sheet's edge a hair off its line** (§10.10): the same no-tolerance test; still written as drawn on a Cartesian grid.
- **The |B1+| plane's residual** (§10a.5): a run of both the tutorial and circuitRF to −60 dB would say whether it is the stop step;
  about 80 min for the tutorial and 2.5 h for circuitRF, the owner's.
- **The two port runs in parallel** (§10a.7): the owner's, as is `--exact-endcriteria` (§3.2).

## 11. Capacitors as ports: per-port fields recombined by the circuit (SAR-9)

The method coil designers use. Every capacitor gap and the feed of a coil become ports, so the EM problem has no resonance of
its own and no long ring-down. openEMS runs once per port and keeps each port's E and H. A schematic places the run's result record as an
EM Field Data block (§11.3), puts the real capacitors, matching network and feed around it, and a `fields` analysis turns the waves the
circuit delivers into the block's ports into SAR and B1± by superposition, **in about a second and with no EM run**. Changing a
capacitor and running the analysis again starts no process.

### 11.1 Keeping the fields: `Sar.PerPortFields` (R-sar9-1)

A Boolean in the `Sar` group (§8.1; off when omitted). It needs nothing else: a driven run already runs one excitation per port,
the others terminated in their Z0, because the S-matrix needs them. After the SAR results, `PortFieldSetWriter` reads every
port's raw SAR dump (and its H dump, with B1) and writes the **field set** beside the `.sNp`, in a folder named after it:

| File | What it holds |
|---|---|
| `<stem>.fields/fields.json` | the manifest: ports and their Z0, per SAR frequency the S-matrix, the tissue matrix (§11.4), each file's size; the tissue names, the `Sar` group the run used, and the run's **stamp** |
| `f<j>.{x,y,z}faces.npy` | the cells' faces, float64, m |
| `f<j>.{sigma,rho,volume}.npy` | σ, ρ, V per cell, float32, C order over (x, y, z) |
| `f<j>.owner.npy` | each cell's tissue, int16, an index into the manifest's tissues (−1 background) |
| `p<n>.f<j>.E.npy`, `p<n>.f<j>.H.npy` | port n's E and H, complex64, shape (nx, ny, nz, 3), per √W incident (§11.2) |

The cells are the SAR dump's box (the tissue plus one cell, §8.3), so the set is 24 bytes a cell per port and field. The run's disk
estimate before it starts includes it, and says how much of it is the field set. Files are read **memory-mapped**, one run of
cells at a time; the writer itself never holds a port's field whole (two passes: transcribe, then normalise in place). The loop
coil's four ports at 298 MHz take 201 MB.

`explain` reports the key on the `sar` line, and the setup editor's Sar group has a *Keep per-port fields* checkbox.

### 11.2 Per unit incident wave (R-sar9-2)

Run k's dump F_k is the field of **every** port's incident wave in that run: port k's from its source, plus what the other ports'
terminations reflect back (round-off with openEMS's matched port resistors, but not zero). With A the incident-wave matrix,
A_ik = port i's incident power wave in run k, F = Ê·A, so the stored fields are **Ê = F·A⁻¹**: each port's field for 1 √W
incident at that port and nothing incident anywhere else. A is formed from the same probes and DFT as the S-matrix, a_i =
(U_i + Z0_i·I_i)/(√2·√Z0_i) in circuitRF's DFT. openEMS's dumps carry a ×2 against that DFT, and with it |a|² is the incident
power in watts exactly. The fields and the S-matrix therefore share one definition of a wave. The total field of any excitation is
E = Σ_k a_k·Ê_k, and the same for H.

### 11.3 The schematic side: the `fields` analysis (R-sar9-3)

```
analysis F1 type=fields block=EM1 freq=298 freqUnit=MHz drive=1 [norm=accepted|available] [average=yes|no]
```

`block` is the EM Field Data block's instance path (`X1.EM1` inside a cell), and a plain SnP is refused saying to place one: a Touchstone
file says nothing of fields. **The block is `.cnl` `EmFieldData`**, one parameter, `File`, a `.cemr` (brief-cosim-pattern-2). Its port count, names and
order are the record's, it stamps an `SnpModel` on the Touchstone the record names, so every `is SnpModel` site works and its S is the file's to the
last bit, and `SnpModel.RecordPath` is how `FieldCosimulation.Run` finds the field set. Elaboration and every analysis read the record and the files it
names, and no `.c3d`, `.cem` or `.clay`. `freq` must be one of the run's SAR frequencies; `drive`
is the circuit port that drives, every other circuit port terminated in its Z0. One circuit solve at that frequency
(`SParameterEngine.BlockWaves`: the S-parameter run's own unit-incident-wave drive, one factorisation) gives the voltage across
each of the block's ports and the current into it. Then a_k = (V_k + Z0_k·I_k)/(2√Z0_k) and b_k likewise, in the Z0 the fields were
normalised in. Over the drive's unit wave that is the square root of a power ratio in any phasor convention, so the weights are
those waves times √P_avail, with P_avail = 0.5 W (`PalaceDrive.IncidentPowerW`, the incident power every driven field in the 3D
view is loaded at). `FieldRecombination.Recombine` sums the fields one x slab of every port at a time, and SAR-5's engine averages
the summed grid.

**Normalisation.** The record and volumes are as solved at P_avail. The per-watt values divide by the drive port's accepted power
P_avail·(1 − |Γ|²) by default, or by P_avail with `norm=available`. **`average=no`** skips the averaged SAR (the cost that
dominates). `FieldRecombination.Local` is the fast path SAR-10 needs: local SAR and B1± over a box (one clip plane is a box one
cell thick), reading only those cells.

**Results.** Written beside the field set, in `<stem>.fields/cosim/<analysis>/`: SAR-6's record (`sar1.json`) and volumes, and
`cosim.json` (the drive, Γ, every port's a and b, the powers and the checks). The DataSet carries the SAR group's scalars, plus
`Pdel`, `Pother`, `Gamma`, `SARpeak`, and the waves `a` and `b` per port. The 3D view of the `.c3d` that ran offers each recombined
result as its own solution, `<f> GHz, fields 'F1' (circuit port 1 driven)`, drawn with no scale of its own. After a GUI run the
Messages pane has an **Open in 3D view** action that opens it with that result drawn and selected. The analysis is in the
Analysis editor (*Fields (SAR, B1)*), and headless it is `circuitrf fields` (`cli.md` §28) or MCP `run analysis=fields`. No
parametric sweep wraps it (the sweep engine refuses the type).

**Port impedances.** The circuit reads the block's `.sNp`, and the Touchstone writer records only the first port's Z0. A field set
whose ports have different Z0s would be recombined in the manifest's Z0s against an S-block read in one Z0. Every co-simulation
here uses 50 Ω on every block port (`src/Design/RESOLVED.md`).

### 11.4 What the run checks, and what the checks cannot see (R-sar9-5)

Three checks, each a note, and a warning when it fails:

- **The summed field's absorbed power against the tissue matrix**, Q_mn = Σ ½σV·(Ê_m*·Ê_n) over every tissue cell, computed once
  when the set is written: aᴴQa must equal the recombination's absorption over every tissue cell (not a stated `Region`), to 0.1 %.
  This catches field files that are not the ones the manifest was computed from.
- **The power the circuit delivers into the block**, Σ|a_k|² − |b_k|², against the field set's own S-matrix, aᴴ(I − SᴴS)a, to 2 %.
  This catches a block whose `.sNp` is interpolated away from the SAR frequency, or is not the run's.
- **No excitation absorbs more than it dissipates**: I − SᴴS − Q must be positive semidefinite (its smallest eigenvalue no lower than
  −2 % of the dissipation's largest). Where the run measured a radiation pattern, each port's dissipation is also split into absorbed,
  radiated and the rest (conductor loss).

**What they cannot see.** All three compare the field set with itself and with its own S-matrix. A field that is wrong in the same
way in E and in S (too short a ring-down, a coarse grid) passes every one: they hold the recombination to its inputs, not the
inputs to the physics. The loop coil (§11.6) is what holds those.

**Stale or missing.** The result record is the only pointer to a field set; the record, the `.sNp`, the `.npy` and the manifest carry one
stamp. `EmResultIntegrity.Check` (in `src/Core`, so elaboration can ask, and `EmResultRecord.Verify` is the same check) says which file
differs, or which is missing or shorter than its run wrote, and the block refuses naming itself and the file. A record with neither a
far field nor a field set is refused too, since its S-parameters belong in an SnP. **File times are not compared**: a git checkout or a
copy sets them arbitrarily, and a committed field set would read as stale on every fresh clone. A sweep re-elaborates at every point, so
a verdict is kept while no file's size and write time have moved; that is a cache key, never the answer. A run publishes by renames
(`EmResultStaging`), so a failed run leaves the old result, recombined results included.

### 11.5 Superposition (R-sar9-6)

`testdata/sar/circuitrf/pair/`: two lumped ports over a lossy block, per-port fields kept. One more openEMS run, of port 1's model
with port 2's excitation added at −0.5 times its own, is the **simultaneous** run. All three take the same fixed number of steps
(`EndCriterionDb` −200, so no criterion fires), so their DFTs cover the same window. The field set recombined with the incident waves
the simultaneous run's own probes measured gives that run's local SAR and |B1+| **in every cell, to 1.7e-6 of the peak**, and the
fast path's plane gives the same. The birdcage's quadrature feed is the same superposition on a cylindrical grid (§10a.5).

### 11.6 The loop coil with ports for capacitors (R-sar9-6)

`testdata/sar/circuitrf/loop/LoopPorts/3d/LoopPorts.c3d` is §10's `Loop.c3d` on the reference's lines, with the three capacitors
replaced by 50 Ω lumped ports (2, 3, 4) and the feed a 50 Ω port 1, setup `CoSim` (`PerPortFields` on). `cosim/LoopCosim.cnl`
puts the tutorial's 5.4 pF back across the three gaps and drives port 1 from 2.5 Ω, as the tutorial's feed. Against SAR-8's
lumped-capacitor run (a), per watt accepted:

| Quantity | SAR-8 run (a) | co-simulation | Tolerance (overview §4) |
|---|---|---|---|
| \|B1+\| at the phantom centre | 0.2408 µT/√W | **0.2445 (+1.50 %)** | 2 % |
| psSAR10g | 1.8144 W/kg | **1.8682 (+2.97 %)**, same cell | 3 % |
| peak local SAR | 8.665 W/kg | 8.921 (+2.96 %) | reported |
| mean \|B1+\| over the brain | 0.2019 µT/√W | 0.2049 (+1.48 %) | reported |
| absorbed / accepted | 80.7 % | 83.1 % | reported |
| SAR and \|B1+\| planes' correlation | | 1.00000 on all four | reported |

The circuit gives |Γ| 0.070 at the feed, and the block's incident waves are 1.68, 3.20, 3.20 and 3.12 √W at 0.5 W available. The
checks agree to 1.3e-10 (Q) and 2.8e-9 (S), and the smallest eigenvalue of I − SᴴS − Q is −3.8e-6 W per W against a largest
dissipation of 0.018.

**Most of the difference is SAR-8's truncated ring-down.** Every SAR number is 3 % up and every B1 number 1.5 % up: a uniform
scale on the field, its square on SAR, with the planes correlated 1.00000. SAR-8's run stopped on openEMS's energy criterion at
245 ns, with the coil's current still −36 dB below its peak (§10.5). The current rings down as a single exponential, 3 dB per 19 ns,
and a DFT of a ring-down cut at amplitude g₀ keeps a fraction 1 − g₀ of the resonant phasor: 0.984 in field, 0.968 in SAR. The
co-simulation has no ring-down to cut: its four runs stop on circuitRF's port-decay rule at about 5,800 steps each, and recombining
from runs stopped at −40 dB and at −60 dB gives the same numbers. **Run (a) again with `EndCriterionDb` −60** (circuitRF's port-decay
rule stopped it at 117,181 steps, 397 ns, after 24 min; the energy sat at −41.8 dB from 270 ns on, a floor the resonance is not
part of, so openEMS's own criterion would have run to the step limit) closes about
four fifths of the gap:

| per watt accepted | run (a), −36 dB at its stop | run (a) to −60 dB | co-simulation |
|---|---|---|---|
| psSAR10g | 1.814 W/kg | 1.857 (+2.37 %) | 1.868 (+2.97 %) |
| peak local SAR | 8.665 W/kg | 8.868 (+2.34 %) | 8.921 (+2.96 %) |
| mean \|B1+\| over the brain | 0.2019 µT/√W | 0.2043 (+1.19 %) | 0.2049 (+1.48 %) |
| absorbed / accepted | 80.7 % | 82.6 % | 83.1 % |

What remains between the fully rung-down lumped run and the co-simulation is again uniform, 0.29 % in field and 0.59 % in SAR,
and it is **not explained**. Candidates not yet separated are the lumped elements' discretisation in openEMS against ideal circuit
capacitors and feed resistance, and the accepted power taken at a 2.5 Ω lumped port against the circuit's drive. It is a fifth of
the 3 % tolerance.

### 11.7 Costs

| | SAR-8 run (a), lumped capacitors | co-simulation |
|---|---|---|
| openEMS | 72,468 steps, 371 s, one run | 4 runs of ~5,800 steps, ~38 s each |
| whole EM run | 6.4 min | ~3 min, including the 201 MB field set |
| a capacitor changed | a new EM run | one circuit solve and a recombination, **~1 s**, no process (`CosimNoEmRunTests` counts both) |

### 11.8 Gates

`tests/Ui.Tests/Sar/`:

- `FieldRecombinationTests` (routine): synthetic per-port fields with known weights give the hand-computed sum, local SAR and B1±;
  the fast path's plane is the full path's and reads only its cells.
- `CosimNoEmRunTests` (routine): two capacitor values recombine twice with zero openEMS launches, and port 2's wave moves.
- `FieldSetStaleTests` (routine): a set from another run is refused saying so; a missing port file is named.
- `EmFieldDataTests` (routine, brief-cosim-pattern-2): S identical to an SnP on one file, to the bit; a deleted setup changes nothing; each refusal;
  a hierarchical path; `.csch` to `.cnl` to a run; a plain SnP refused, with the undoable change.
- `FieldsDirectiveRoundTripTests` (routine): `type=fields` survives the `.cnl` reader and writer and the schematic's serialisation.
- `PairSuperpositionTests` (routine, fixed data): §11.5. `PairSuperpositionRerunTests` (`Benchmark`) remakes the fixed data with
  `CRF_WRITE_SAR9_OUTPUTS=1`.
- `CosimLoopCoilTests` (routine, fixed data): §11.6's tolerances, and, when the run's field set is still on the machine, that the
  committed output is what the schematic's `fields` analysis gives now. `CosimLoopCoilRerunTests` (`Benchmark`, ~3.5 min) runs
  `circuitrf em` on `LoopPorts.c3d` and `circuitrf fields` on the schematic, and with `CRF_WRITE_SAR9_OUTPUTS=1` rewrites
  `cosim/output/`.

### 11.9 Not done in SAR-9

- **The birdcage's superposition** — §10a.5.
- **Per-port Z0 in the Touchstone** (§11.3): the field set records each port's Z0 and the writer does not.
- **Goals, tuning, the optimizer and yield on recombined SAR and B1** — SAR-10, which the fast path is for (§12).

### 11.10 The far field through the same analysis (CP-3)

A `fields` analysis also recombines the far field a driven 3D run stores per port. The record decides what runs (`FieldCosimulation.PlanOf`): a field
set gives SAR and B1, a far field gives the pattern's cubes, both give both, and neither is refused when the block is read. Nothing is written beside
a result that holds only a far field.

**The sum.** A stored pattern `E^V_k` is the field with 1 V across port k and every other port terminated in its Z0, so with the circuit's waves at the
block, in `Weigh`'s one statement of them,

```
E = Σ_k a_k · √Z0_k · (1 + S_kk) · E^V_k
```

and E over the circuit's first drive voltage is the circuit's pattern at 1 V there, which is what the cubes of a 3D run mean. `Em3dRadiation.Publish` forms
every metric from it, so gain is per watt accepted and realized gain includes mismatch whatever `norm` says, and the waves' power scale cancels.

**The names.** `CircuitEvaluation` groups only a result's default group, so the cubes are published there as `farfield.U` and so on, which reads
`F1.farfield.U`. `S` and `Z0` are the S-parameter run's own cubes at the analysis's frequencies. `freq=all` is every frequency the far field holds; a
frequency between them is refused, because far-field phase across a coarse sweep is not safe to interpolate.

**Every solver's pattern is the field its S describes.** The sum holds only where a stored pattern is the field of port k's incident wave alone, referred to the
voltage S states. Palace's always were. openEMS's other ports reflect a little as its probes see them, so `OpenEmsPattern` adds a port's runs up with the inverse S itself
uses (`FdtdPortExcitations.From`). EMerge's are referred to `V_inc·(1 + S_kk)`, since the driver's own reading of a port's voltage can miss a point on its edge. Both
mark the result (`Em3dRadiation.PortsTerminatedCube`), and `PortFarFieldSet` asks for an unmarked one to be run again, since its `.npy` cannot be corrected.
EMerge's S across a change of load closes only as the port's edge mesh does: `|ΔS11|` halves as `EdgeRefinement` does and meets 1e-3 at 0.02, and the pattern meets its limits on any mesh.

**One mesh.** The gates compare a recombination with a run on the same mesh, so they prove the method and not the mesh: a result that is not converged
recombines faithfully into the same unconverged answer. The balance check is what looks at the solve itself.

---

## 12. B1 and SAR as goals: tuning, the optimizer, yield and multi-channel drive (SAR-10)

SAR-9 made a field set a function of the schematic's values. SAR-10 lets everything that already varies those values read the
field: a `measure` line, a goal, a tuning knob, the optimizer, a yield run. None of them starts an EM run.

### 12.1 The field measurements (R-sar10-1)

Seven functions of the one expression engine (`Evaluator.Fields.cs`), usable wherever a `measure` or `goal` expression is:

| Function | Value | Normalised |
|---|---|---|
| `B1pMean(F, region)` | volume-weighted mean \|B1+\| over the region's cells | T per √W |
| `B1pMin(F, region)` | the smallest \|B1+\| of those cells | T per √W |
| `B1pCoV(F, region)` | volume-weighted standard deviation of \|B1+\| over its mean | — |
| `psSAR(F, mass)` | peak spatial-average SAR of `mass` grams, the full average (§7) over the setup's `Region` | W/kg per W |
| `SARwb(F, region)` | the region's absorbed power over its mass | W/kg per W |
| `SarEff(F, region, mass)` | `B1pMean(F, region) / √psSAR(F, mass)` | T per √(W/kg) |
| `Pabs(F)` | the power absorbed in every tissue cell | W per W |

`F` is a `type=fields` analysis, **read as a name** (as `SP1` in `SP1.S`), never evaluated, so a variable of the same name
does not shadow it. "Per W" is the analysis's own `norm=`: per watt accepted at the drive by default, per watt available with
`norm=available`, exactly the SAR-6 normalisation (§8.4) the fields analysis already writes its scalars in; `reference goals`
says so beside the functions.

**A region** is a string. Omitted, it is the setup's own — `Sar.B1Region` for B1, `Sar.Region` (every tissue when unstated)
for SAR. `"all"` is every tissue. A word matches a tissue's whole path (`"head/brain"`), its last part (`"brain"`), its first
(`"head"`, every layer of that phantom) or its material; `+` joins several (`"skin+skull"`). An unknown word is refused naming
the field set's tissues. `"box(x0, x1, y0, y1, z0, z1)"` is every cell whose centre lies inside, each number with a length unit
(`mm`) or none for metres: B1 is taken over all of them (air too — B1 is wanted inside a coil), SAR over the tissue ones.
A mass is in grams; zero or negative is refused.

`B1pMean(F)` with no region is, to rounding, the `B1pMean` scalar the analysis already returns; `Pabs(F)` is the `Pabs` scalar.
`FieldGoalTests` holds every function to its hand value on a synthetic two-port set, both as Simulate runs it and as an
optimizer evaluation does (relative 2e-5, the float32 storage).

### 12.2 Cheap where it can be (R-sar10-2)

A fields analysis now does as much as its caller needs (`FieldsOutput`, a field of `CircuitEvaluationRequest`):

| Caller | Mode | What runs |
|---|---|---|
| Simulate, `circuitrf fields`, the optimizer's final re-evaluation, a re-run trial | `Written` | §11.3 unchanged: the whole recombination, the averaged SAR when `average=yes`, volumes and records written |
| a tuning knob moving | `Local` | the whole recombination, local SAR and B1 written, **no average** |
| every optimizer, yield, centering and DOE evaluation | `OnDemand` | the circuit solve and the waves; the absorbed power from the tissue matrix; **nothing recombined, nothing written** |

In every mode the evaluation leaves the field measurements a `CosimFieldMetrics` over the still-open field set, keyed by the
analysis's result name, disposed once the measurements and goals are evaluated. It computes each value only when an expression
asks, and keeps it for the rest of that evaluation:

- `Pabs` is aᴴQa (§11.4): exact, and free once the waves are known.
- `B1pMean`, `B1pMin`, `B1pCoV` and `SARwb` read only the cells of their region's bounding box
  (`FieldRecombination.RegionSums`: O(N × box cells), rows outside the region skipped).
- **`psSAR` is always the full average**: the whole field recombined and SAR-5's engine run on it at the mass asked, never a
  sample, a coarser grid or a region's local peak standing in for it (overview R-sar10-2). `SarEff` reads the same average.
  In `Local` mode it is refused with a sentence saying it is computed when the knob is released; an average silently skipped
  would read as one computed.

**The optimizer has no cost hint.** TO-6 built none, so there is nothing for an algorithm to read. Instead the run says which
goals are the expensive ones (`opt.goal.full-average`, a note naming every goal whose expression calls `psSAR` or `SarEff`),
and every evaluation that scores such a goal pays the full average. Evaluating it only every k-th iteration was the brief's
fallback; it was not built, because a goal scored on a stale average at the points between is exactly the approximation the
same requirement forbids, and on the loop coil the average is about a second.

A goal on a field function names its analysis (`analysis=F1`), as any goal does; `GoalTemplates.AnalysisReferencedBy` also
finds it from the function's first argument, so the Optimizer window's custom expression fills it in. The goal editor and
`reference goals` list seven templates per fields analysis (*Fields (SAR, B1)*).

### 12.3 The tuning window (R-sar10-3)

While a knob moves, each tuning evaluation is `Local`: it rewrites the recombined local SAR and B1 volumes beside the field set,
and the session's sink tells the workspace (`ITuneResultSink.FieldsWritten`), which re-reads the runs of an open 3D view of
that `.c3d` (`RunFinished`). The plot on the view's clip plane therefore follows the knob. On release the evaluation is
`Written`, so the averaged volumes and the psSAR readouts update then.

Two findings on the way. **The view never re-read a rewritten recombination**: `Viewer3DViewModel.StampOf` dated a run by its
first solution's file, a port's own step, which a recombination does not touch, so a second `fields` run looked like the same
data. It now takes the latest of that and every co-simulation solution's file. And **the live path is the whole local
recombination, not the plane-only fast path**: the 3D view draws a SAR or B1 plot from a volume file, on whatever plane the
user has moved it to, and the evaluation does not know that plane. On the loop coil that is one pass over 201 MB per update;
`TuneSession` already coalesces requests, so a slow update drops the intermediate positions rather than queueing them.
Drawing the plane from the fast path inside the view is left open (§12.7).

### 12.4 Multi-channel drive (R-sar10-4)

```
W2 = polar(1,-90)
analysis F1 type=fields block=EM1 freq=298 freqUnit=MHz drive=1,2 weights=1,W2
tune mag(W2) min=0.5 max=2 opt=1
tune phase(W2) min=-180 deg max=0 deg opt=1
```

`drive=` takes several circuit ports, and `weights=` each one's incident-wave amplitude, comma-separated at bracket depth zero
(so `polar(1,-90)` is one item): complex expressions over the globals, 1 each when omitted. A channel's amplitude and phase are
then the D18 parts of a complex VAR, ordinary tunables (`tune mag(W2)`, `tune phase(W2)`). `SParameterEngine.BlockWaves` takes
the list and solves once with every drive's Norton current; it is linear, so it is the weighted sum of the single-drive solves,
and it returns each driven port's incident and reflected wave.

**Normalisation.** The drives together make `PalaceDrive.IncidentPowerW` available, so the volumes stay at the scale the 3D view
loads every driven field at; accepted power is Σ(|a_j|² − |b_j|²) over the driven ports. Per watt therefore means per watt of the
drives' **total**, accepted or available, and "maximise B1pMin subject to psSAR ≤ x per W total" is an ordinary optimizer
problem. The record and the 3D view carry an effective reflection √(1 − accepted/available) (phase 0) in place of one port's
S_jj, and `cosim.json` lists every drive's waves (`Drives`).

`FieldGoalTests.TwoCircuitPortsDriveTogether_WithTheirWeights` drives both block ports of the synthetic set through matched
circuit ports with `weights=1,W2` and finds a2/a1 = −j to 1e-9, and every measurement at those waves equal to its hand value.
The quadrature birdcage is the worked case on a real coil: its two feeds driven together with `weights=1,Wq`, `Wq = polar(1,-90)`
(§10a.5).

### 12.5 Yield (R-sar10-5)

A tolerance on a capacitor (`tune C2.C dist=gauss sd=2%`) and a yield spec on a field function (`goal PS = psSAR(F1, 1)
analysis=F1 le … use=yield`) run as any yield run: every trial is an `OnDemand` evaluation, so it recombines from the kept
fields and starts no process. Re-running one trial (`run analysis=yield trial=<n>`, or the Data Display's re-run) is `Written`
under a label of its own, `<analysis> (trial <n>)`, beside the analysis's own results rather than over them; the 3D view of the
`.c3d` offers it as the solution *fields 'F1 (trial n)'*.

`CapacitorYieldTests` runs 200 trials on the pair's committed field set (§11.5). Port 2 of the pair is about −437 Ω at 1 GHz,
so a lone capacitor there barely moves anything: 2 pF gave psSAR1g a spread of 0.002 %. With 100 nH in series, the 0.84 pF
capacitor nearly resonates the port, and its 2 % moves psSAR1g by **3.9 %** (one standard deviation; 40.0 to 47.2 W/kg per W
over the 200 trials), with no openEMS launch and at least 200 full averages counted. The worst trial re-run writes its record
under `F1 (trial n)`.

### 12.6 The loop coil, optimized

`LoopCoilOptimizeTests` puts the loop coil's committed `.s4p` (§11.6) in a schematic with the three gap capacitors at 4.5 pF
and one goal, S11 ≤ −15 dB at 298 MHz. The simplex finds **5.406 pF** in 8 evaluations, S11 −34.6 dB there; SAR-9's tutorial
value is 5.4 pF. Where this machine still holds the coil's field set (201 MB, local only), mean |B1+| over the brain at that
value, read on demand with `B1pMean(F1, "brain")`, is **0.2049 µT/√W, +0.00 %** of SAR-9's fixed data at 5.4 pF (tolerance
2 %). No openEMS process starts in either test (counted).

### 12.7 Not done in SAR-10, and what is open

- **The quadrature birdcage as the worked multi-channel case** — §10a.5.
- **The plane-only live path in the 3D view** (§12.3): the view would recombine its own clip plane from the field set with the
  tuned waves (`FieldRecombination.Local`, already built) instead of re-reading a whole volume.
- **A cost hint the algorithms read** (§12.2): TO-6 has none; the note names the expensive goals instead.
- Virtual observation points, Q-matrix compression and pulse design stay out of the series (brief §3).

### 12.8 Gates

`tests/Ui.Tests/Sar/FieldGoalTests.cs`, all routine and with no process:

- `FieldGoalTests`: every function against its hand value, Simulate's mode and the optimizer's; an on-demand evaluation writes
  nothing; a moving knob writes local SAR and B1 and refuses psSAR until release; an unknown tissue, a mass of zero and an analysis
  that is not a fields one are refused naming what is wrong; two circuit ports driven together.
- `LoopCoilOptimizeTests`: §12.6 (the B1 half skips where the field set is not on the machine).
- `CapacitorYieldTests`: §12.5.
- `FieldsDirectiveRoundTripTests` gained `drive=2,3 weights=…` through the `.cnl` reader and writer and the schematic.

---

## 13. Segmented voxel models and per-tissue surfaces (SAR-11)

Researchers bring their own anatomy. circuitRF ships no anatomical model (overview L2); it ships the importers. Two forms are
read: a **segmented label map** (one integer per voxel naming a tissue, usually NIfTI) and **one closed surface per tissue**.
Both land in a `.c3d` and run on openEMS.

### 13.1 The voxel body (R-sar11-1)

`C3dVoxelBody` (`$type` `VoxelBody`) references its volume by a path relative to the `.c3d`, as a STEP part does, with the
file's `sha256` recorded at import (`check` warns when the file has changed since). Its **label table** is one row per label:
`{Label, Name, Material}` or `{Label, Name, Air: true}`.

- **Label 0 is air unless a row maps it.** A row with `Air` is air too.
- **A label voxels carry that no row maps is a refusal** — at import and at every run — naming each label and its voxel count
  (`The voxel body 'slab' has 1 label its table maps to no material: 7 (1 voxel)`). A tissue left out in silence would be air,
  and the SAR in its neighbours wrong with nothing said.
- Each mapped row is one dielectric solid, **`<body>/<name>`** — a phantom layer's spelling, so `Sar.Region` names a tissue
  (`head/brain`) or the whole body (`head`) exactly as it names a phantom (`Em3dGenerationResult.Phantoms` lists bodies after
  phantoms). B1's default region is a phantom's innermost layer; a body has no innermost, so name `Sar.B1Region` for one.
- `Crop` keeps voxel indices `[I0, I1) × [J0, J1) × [K0, K1)` along the world's axes as the file is read. After it, the volume
  is cut to the voxels that hold tissue, so neither the file openEMS reads nor the grid carries a frame of air.
- **The placement moves the body and may turn it by quarter turns only**: voxels are then moved exactly, and a DiscMaterial
  file's non-uniform boundaries survive. Any other rotation is refused with its angle (the same rule as a file's own
  orientation, §13.2).
- **Drawn, never simulated, as tissue-coloured surfaces at a reduced resolution**: the volume decimated to at most 96 blocks
  along its longest axis (each block the label at its centre voxel), every block face between two tissues emitted. The drawing
  is an `Em3dShapeSolid` with **no B-rep** whose one face's box is the tissue's **exact** voxel bounds, so the grid, the SAR dump
  box and the view all read the true extent from it; the solid itself is `Em3dSolid.Labels` (an `Em3dLabelVolume`), and
  `Em3dSolid.Holds` asks the voxel a point lies in — so a named region, B1's region and a peak's tissue are the tissue CSXCAD
  gave the cell.
- Loaded once: a whole-body volume is hundreds of megabytes, so it is cached by the file's path, length, write time and raw
  layout (`C3dVoxelBodies.Loads` counts the misses); editing the table or the placement re-uses it.

### 13.2 Readers (R-sar11-2)

| File | Reader | Rules |
|---|---|---|
| `.nii`, `.nii.gz` (NIfTI-1 and NIfTI-2) | `NiftiFile`, from the published header layouts | either byte order; a two-file `.hdr`/`.img` pair refused; 3D only (a series refused); integer data, or a float volume whose every voxel is a whole number from 0 to 65,535 (noted); a scaled volume (`scl_slope` not 0/1, `scl_inter` ≠ 0) refused; the affine is the sform when its code is set, else the qform quaternion (qfac from `pixdim[0]`), else `pixdim` on the diagonal (noted); units from `xyzt_units`, millimetres when unstated (noted) |
| raw | `RawVolume` | dimensions, type (`uint8` … `int32`), voxel size, byte order and a header to skip are stated (the dialog's fields, the CLI's flags); x fastest; the file's length must equal header + voxels × width exactly, both ways — a wrong dimension or type almost always shows up there |
| `.h5` (CSXCAD DiscMaterial) | `OpenEmsHdf5.ReadDiscMaterial` (new, beside SAR-4's reader) | version ≥ 2; `/DiscData` uint8 (nz, ny, nx), chunked and deflated allowed (openEMS's own phantoms are); names optional; the boundaries in metres as stored. Its εr/σ/ρ tables are **not** used: the label table's materials are, and the import says so |
| label list CSV | `LabelTableCsv` | `label,name[,material]`, a header row allowed, `#` comments; `air` as a material is air; a label given twice refused |

**A non-axis-aligned affine is refused with the angle** (`VoxelAxes`, tolerance 1e-6 on the direction cosine, about 0.08°):
resampling a label map onto a turned grid is a decision about anatomy that belongs to whoever made the segmentation. A
permutation and a flip are not refused — they are how the common orientations are stored — and are applied by moving voxels.

Labels are held as `ushort` (0 … 65,535): a whole body at 1 mm (~270 M voxels) is half a gigabyte in it, where `int` would be a
whole one; a label outside the range is a refusal naming it.

### 13.3 Lowering to openEMS (R-sar11-3)

`CsxcadWriter.VoxelBody` writes **one `DiscMaterial`** per body, with SAR-4's `DiscMaterialWriter`: entry 0 air, then one
entry per tissue with its material's εr, σ (evaluated at the SAR frequency, SAR-2) and, when SAR is requested, its density.
Exactly as a voxelized phantom (§10.3): the box is written at **priority 0**, so anything overlapping it wins there and the run
names it; a tissue with a tensor permittivity or μr ≠ 1 is refused; the file rides the kernel-file path into every port's
directory and is checked by hash before openEMS starts.

- **The placement is applied to the boundaries, not written as a CSXCAD transform.** The brief asked for a transform; the
  file's lines are instead the placed world lines — the very lines the grid puts on the voxels — so a cell's centre is looked
  up in the same numbers on both sides, and no CSXCAD transformation chain (whose rotation the grid would not see, §5.1) is
  involved. A quarter turn is applied by moving voxels. The cost: one placement, one file; moving the body rewrites it.
- **The grid inside the body**: with `GridStep` 0 (the default) every voxel boundary is a required grid line (`FdtdGrid`
  collects them as material faces), so each FDTD cell is one voxel. A positive `GridStep` caps the cell inside the body at that
  step instead (`Shape.CellCap`); **the resampling rule is CSXCAD's own**: under cell-constant material each cell takes the voxel
  its centre lies in — nearest-voxel sampling, stated, not averaged. Voxel lines are movable: below `MinCellUm` they merge as
  any material faces do, and the merge is reported.
- **Priority with other solids** is em-3d.md §6.3a's, with the body as one dielectric at the bottom (priority 0).
- **At most 255 tissues per body.** CSXCAD addresses a uint8 index (sar.md §6.4), so more is refused, naming the limit and
  saying to map labels that share a material to one row. *For the owner:* an atlas with more labels than that needs CSXCAD to
  change, or a body split across several DiscMaterials.
- Palace and the thermal solver refuse a body (`GmshGeoWriter.ImportedOpenEmsOnly`: nothing builds a meshable solid from
  voxels), the cylindrical writer refuses it (its voxels are Cartesian), and STEP export refuses it.

### 13.4 Per-tissue surfaces (R-sar11-4)

`C3dMesh` (`$type` `Mesh`) is one closed surface in its own file (STL ASCII or binary, OBJ, PLY ASCII or binary), its `Unit`
stated (a mesh file says none). It lowers to an `Em3dShapeSolid` with no B-rep whose display mesh **is** the solid, its
tessellator returning that mesh — so openEMS gets it through the **existing PolyhedronReader PLY path** a kernel solid takes
(`KernelPly`), unchanged. Its inside test is the parity of a ray's crossings, through a (y, z) bin grid per mesh
(`C3dMeshInside`), so a SAR region on a mesh tissue is the surface, not its box.

- **The check, per file**: closed (every edge in exactly two triangles, once each way; open, non-manifold and flipped edges
  counted), not self-intersecting (each triangle's edges against every triangle sharing no vertex, through a uniform grid;
  coplanar overlaps are not detected), and outward (a negative enclosed volume is turned round and said). A surface that fails
  is reported for its file and **nothing is imported**; a file edited after import is checked again at elaboration.
- **Nested by containment**: surfaces are ordered by how many others contain them, outermost first, so an inner tissue has the
  later construction order and wins the volume it shares (em-3d.md §6.3a) — a phantom's rule. Two that cross rather than nest
  are named.
- Imported surfaces carry **`Role: Dielectric`**: a tissue states εr and σ together, which on an ordinary object asks for a role
  (em-3d.md §4.1a); a phantom's layer and a body's tissue are dielectric by construction, a mesh says so in its file.
- **Tetrahedral `.node`/`.ele` meshes are voxelised** (`TetMesh.Voxelise`) at a chosen size on a lattice cornered at the mesh's
  lowest point, each voxel the region of the tetrahedron holding its centre (CSXCAD's own sampling rule), and kept as a
  `.nii.gz` beside the `.c3d` — then a voxel body like any other. A region attribute is the label; none is label 1.

### 13.5 Size, said before the run (R-sar11-5)

The import's report (`VoxelImportReport`, printed by the CLI, shown in the dialog and posted to Messages): the file's voxel
count, dimensions and voxel size; what the crop and the tissue keep; the label histogram with each row's material; and the FDTD
cells inside the body with openEMS's memory for them (`Em3dSizeEstimate.OpenEmsMemoryBytes`, the figure the grid's own memory
refusal uses). Past **50 M cells inside the body** it is a **warning, never a refusal** — a whole body at 1 mm is a legitimate,
slow run — naming a Crop or a coarser GridStep as the remedy. The air around the body is the run's own grid and is not counted.

### 13.6 Import: one pair of functions, three front ends

`AnatomyImport` (`src/Design/ThreeD/Import/`) plans and applies both imports: the label table's defaults (a name from the CSV,
then the file's own entry names, then `label k`; a material from the CSV, then a technology material whose name equals the
tissue's ignoring case — never anything fuzzier, the STEP import's rule), the copy into the view's folder
(`StepImport.CopyInto`, reused), and the report. The front ends only gather choices:

- **CLI**: `import voxels <file> --into <x.c3d>` (`--labels`, `--material L=name|air`, `--name`, `--dims`/`--type`/`--voxel`/
  `--big-endian`/`--header` for raw, `--voxel`/`--unit` for a tetrahedral mesh, `--crop`, `--grid-step`, `--report`) and
  `import surfaces <files…|folder> --into <x.c3d>` (`--unit`, `--material stem=name`, `--group`, `--report`). Lengths carry a
  unit (`2.5mm`); a bare number is refused, as `render` refuses one. The `.c3d` is created when absent. `src/Cli/AnatomyImportVerb.cs`.
- **MCP**: the `import` tool gained a `what` selector — `part`, `voxels`, `surfaces` — mirroring the CLI's nouns (it had been
  single-mode since `convert` was split from it; every mode still brings something in).
- **GUI**: File ▸ Import ▸ Voxel Model… and Surface Meshes… on the active 3D view: the table (label, voxels, tissue, material or
  air) with the report, or the surfaces with their check and nesting; one undo entry that carries the copied bytes
  (`C3dFilesEdit`). The Inspector shows a body's file, air labels, crop, voxel extent and `GridStep`, and one material combo per
  tissue; the tree lists its tissues under it, and picking a tissue in the view selects the body. A save keeps the copies a
  body or a mesh names, as it keeps a Step part's.

`convert` was not extended: it is one import plus one export between artwork formats, and a voxel model has no export target.

### 13.7 Local models (R-sar11-6)

`testdata/sar/local-models/` is git-ignored except its README and `.gitignore`. A developer may put a free model obtained
themselves there (a `.nii`, `.nii.gz` or DiscMaterial `.h5`, optionally `<stem>.csv` beside it); `LocalModelImportTests` reads
each one and prints its report, and skips with a reason when the folder is empty. No model, name or licence text enters the
repository.

### 13.8 Gates

`tests/Ui.Tests/Sar/VoxelImportTests.cs`, every volume and surface synthetic and written by the test:

- `NiftiReaderTests` (routine): a `.nii.gz` written byte by byte from the header layout, stored with x flipped, round-trips its
  dimensions, affine and labels; NiftiFile's writer reads back to the same volume; an affine turned 10° is refused naming 10°.
- `VoxelBodyCsxTests` (routine): a 3-label slab placed 40 mm along x writes one DiscMaterial that SAR-4's reader reads back
  exactly (every voxel's entry, the boundaries, εr and σ per entry), with a grid line on every voxel boundary; a stray label is
  refused with its count, and accepted once marked Air.
- `VoxelVsPrimitivePhantomTests`: the `Ellipsoid3` head phantom voxelised to 2.5 mm and the same ellipsoids imported as a label
  volume write **the same DiscMaterial** — boundaries, every voxel's entry, εr and σ (routine, writer output). Opt-in
  (`Benchmark`, 42 s): both run by the installed openEMS for 20 steps on **one grid** (the body's, given to the phantom's setup
  as explicit lines), and the two raw SAR dumps agree **cell by cell in conductivity, density and complex E, exactly**.
- `SurfaceAndTetImportTests` (routine): two nested cubes import outermost first with materials matched by file name and lower
  through two PLY PolyhedronReaders, the inner one later; the mesh inside test is the surface; a cube with a hole is refused
  naming its 3 open edges; a five-tetrahedron cube voxelises at 1 mm to 1,000 voxels of its region.
- `LocalModelImportTests`: §13.7, skipped without a local model.
- `ThreeDViewDocumentTests`' every-kind fixture holds a `VoxelBody` and a `Mesh`; `ServeProtocolAdapterTests` covers the three
  `import` modes.

### 13.9 Not in SAR-11

Posing or morphing; skewed (non-axis-aligned) affines; anatomical models of circuitRF's own (brief §3). Also not done: a voxel
body on Palace or the thermal solver (it would need a surface mesh per tissue, or a voxel-to-tetrahedron path); a DiscMaterial
of more than 255 tissues; a self-intersection test for surfaces past 2 M triangles (checked for closure only, and said).


---

## 14. The phone SAR workflow (SAR-12)

A wireless-device engineer places an antenna or a whole device against a phantom of tissue-simulating liquid, runs it at
the device's bands, and reads psSAR1g and psSAR10g at the device's rated power beside published limits, **shown as
reference values, never as pass or fail** (overview L1).

### 14.1 Phantoms (R-sar12-1)

**The flat phantom** is SAR-3's `FlatBox`: a liquid in a shell on its floor and four walls, open on top. Its defaults now
come from a free public regulator document where it states them — FCC OET Bulletin 65 Supplement C (Edition 01-01),
"Recommended characteristics of head and body phantoms": a **2.0 mm shell** ("within 2.0 ± 0.2 mm") and **150 mm of
liquid** ("a depth of 15.0 ± 0.5 cm"), so `Size.Z` is 152 mm. The bulletin states no floor size for a body phantom (at
least twice the device's), so the **300 × 200 mm floor is circuitRF's own**, chosen to meet the same bulletin's
system-check minimum of ¾ wavelength by ½ wavelength from 900 MHz up. The liquid stays the body liquid; the shell is now
a material (below), where SAR-3 left it unnamed.

**The generic head** (`Shape` `Head`, the menu's "Generic Head (circuitRF's own)") is circuitRF's own shape and is
named so, so nobody takes it for a standard's mannequin: an ellipsoid shell (`SemiAxes`, `Shell1`) of head liquid, with
a flat circular **ear spacer** on each side, a cylinder about y of `Radius` standing `Length` off the head's surface. Its
layers are `ear-left`, `ear-right`, `shell`, `liquid`, **in that construction order**, so the shell and the liquid take
everything inside the head and what is left of each spacer is a flat disc: the face a handset is placed against. Its
frame is x to the front, y ear to ear (left is +y), z up. Defaults: semi-axes 100, 78 and 115 mm (circuitRF's own round
numbers for an adult head), a 2.0 mm shell, and spacers 4.0 mm thick — both from the bulletin, which recommends "a 4.0 ±
0.2 mm thick low-loss dielectric spacer" — and 25 mm in radius (circuitRF's own: the bulletin says "circular shaped" and
gives no size).

**The shell material**, `Phantom shell (circuitRF's own)` in the shipped tissue library: εr 3.7 and lossless. It is
circuitRF's own value, labelled so, inside the bounds the bulletin states for a shell ("dielectric constant and loss
tangent less than 5.0 and 0.05"). It has σ 0 and no density, so it is never tissue (§8.2). The liquids are SAR-2's
target tables (§4.3), evaluated at each run's SAR frequency.

### 14.2 Placement (R-sar12-2)

`C3dDevicePlacement` (`src/Design/ThreeD`); Place Against Phantom… in the 3D editor (the Phantom menu, and the canvas's menu
on a selected device). **It writes ordinary transforms**: each moved object's and instance's `Placement` gains one rotation
and a new `Origin`, what a drag would write. Nothing in the format is phone-specific, and nothing remembers the placement.

- **A face** is a plane: an anchor and an outward normal. Every phantom offers its own box's six faces (`xmin` … `zmax`,
  in its own frame, carried by its placement), and two named ones by shape: a flat phantom's `floor` (the shell's outer
  face) and `liquid` (the liquid's surface through the shell — the plane a system check's spacing is stated from), and the
  generic head's `ear-left` and `ear-right` (each spacer's flat face).
- **The separation** is measured along the normal from the face's plane to the device's nearest point: the smallest n·(v −
  a) over every vertex of the device's elaborated geometry, so a STEP device is measured on its tessellation. Curved
  surfaces are measured at their tessellation's vertices, which lie on the surface; an extreme point between them is off by
  at most the facets' sagitta.
- **The tilt** turns the device about a world axis first; the translation is then solved, so the axis needs no pivot. The
  device's box, projected on the face, is centred on the face's anchor plus an in-plane offset.
- **A device is one rigid motion.** Selected objects, instances and groups (a group's name stands for everything in it)
  each get the same rotation appended and their origins carried by the same motion. A tilt that is not a quarter turn
  leaves an origin that is not an integer DBU; it is rounded, so two parts that touched may be a DBU apart afterwards.
- **Refused:** placing a phantom; a negative separation; a separation from `liquid` thinner than the shell (the device
  would be in the shell); an object whose origin is an expression (placing it would overwrite the expression).
- **The dimension** in the 3D view (`Viewer3DViewModel.SetDimension`) is measured again from the geometry every time the
  scene is adopted, so a device moved by hand afterwards shows its new separation. It is session state, like a measurement.

### 14.3 Bands and power (R-sar12-3)

One SAR frequency per run (overview L6). A **band sweep** (`C3dDocument.BandSweeps`, `C3dBandSweeps`) names a template setup
and its bands; `Create` writes one ordinary setup per band, `<sweep> <f> GHz`: the template with its `Sar` at that one
frequency (so the liquid is evaluated there — the gate's three bands give three εr and σ), 1 g and 10 g added to its masses,
and **its sweep scaled by f / f_template** (the template's SAR frequency, else its sweep's centre), so each band keeps the
template's relative bandwidth and a grid drawn from its own highest frequency. Creating again replaces the band setups, so
an edited template brings every band up to date. Nothing in a run knows a setup came from a sweep.

Band Sweep… (the Phantom menu) edits the sweep, creates the setups (one undo entry, with the sweep), runs them in turn as
Simulate does, and shows the table; it is not modal, since its runs take as long as they take. `circuitrf em x.c3d --bands
<sweep>` makes the same setups in memory, runs them and prints the table (cli.md §29); MCP's `run analysis=em` takes `bands`.

**The table** has one row per band and driven port: psSAR1g and psSAR10g **at the rated power**, per watt, the peak cube's
centre and tissue, and the difference from a target where the user gave one. Every SAR number a run writes is per 1 W
accepted (§8.4); the table multiplies it by:

- `DrivePowerW` (default 1 W, so the table reads per watt), the conducted power the device delivers, referred to the
  **accepted** power (default) or to the **incident** power, when |S_kk|² — read from the band's own Touchstone at the
  band, linear between its points — takes its share before the power reaches the antenna;
- `DutyCycle`, when given: a burst transmitter's scaling factor.

The sweep's power, duty cycle, reference values and targets are read by the table alone; `C3dPersistence.SerializeForRun`
leaves them out, so editing them never makes a result out of date.

### 14.4 Limits as reference values (R-sar12-4)

`C3dBandSweeps.Published`, each cited and dated, chosen by the sweep's `Exposure` (head and trunk by default, or limbs):

| Reference value | Source |
|---|---|
| 1.6 W/kg over 1 g, head and trunk | FCC OET Bulletin 65 Supplement C (Edition 01-01, June 2001), "FCC limits for specific absorption rate (SAR)", general population/uncontrolled exposure, partial body ("averaged over any 1 gram of tissue defined as a tissue volume in the shape of a cube") |
| 4.0 W/kg over 10 g, extremities | the same table: hands, wrists, feet and ankles |
| 2.0 W/kg over 10 g, head and trunk | ICNIRP, Guidelines for limiting exposure to electromagnetic fields (100 kHz to 300 GHz), Health Phys. 118(5):483-524 (March 2020): basic restrictions for the general public, local, head and torso, 100 kHz to 6 GHz |
| 4.0 W/kg over 10 g, limbs | the same, local, limbs |

A user adds their own (`Limits`). **A number above a reference value is coloured and says which reference it is above**
("above the reference value 1.6 W/kg over 1 g (national regulator)"); no word in the table, the dialog or the CLI's output
says pass or fail, which the gate checks.

### 14.5 The system check (R-sar12-5)

`testdata/sar/circuitrf/system-check/` (its README lists every file): a half-wave dipole below a flat phantom, in the
configuration OET Bulletin 65 Supplement C's system-check table gives at **1900 MHz** — 68.0 mm long, 3.8 mm in diameter
(here a PEC strip of that width, the 50 Ω feed across a 2 mm gap), **10.0 mm from its axis to the liquid's surface**,
a shell of at most 5.0 mm on the side the dipole couples to (2.0 mm here), 15 cm of head liquid. The 160 × 100 mm of
liquid is circuitRF's own, above the bulletin's ¾ λ by ½ λ (118 × 79 mm). The routine gate measures the dipole's separation
from the phantom's `liquid` face with the placement helper's own measure: 10 mm.

**These are circuitRF's own results, not target values.** The standard's target values are not available (overview L2);
a user with their own enters them as the sweep's targets and the table shows the difference.

**The numbers**, per 1 W accepted at the feed, 1.9 GHz, head liquid εr 40 and σ 1.4 S/m (its target table's 1800–2000 MHz
row), on circuitRF's own grid at three densities:

| | Coarse (`CellsPerWavelength` 10) | **Medium (15): the band sweep's setup** | Fine (20) |
|---|---|---|---|
| grid | 106 × 81 × 101 = 0.87 M cells | 151 × 109 × 140 = 2.30 M cells | 185 × 131 × 174 = 4.22 M cells |
| openEMS stopped at step (energy) | 4,029 (−47.8 dB) | 7,006 (−42.3 dB) | 7,099 (−44.0 dB) |
| openEMS wall time | 45 s | 146 s | 214 s |
| **psSAR1g** | 39.08 W/kg (−2.8 %) | **40.87 W/kg (+1.6 %)** | 40.21 W/kg |
| **psSAR10g** | 20.00 W/kg (−3.7 %) | **20.97 W/kg (+0.9 %)** | 20.78 W/kg |
| peak local SAR | 64.4 W/kg | 71.9 W/kg | 69.7 W/kg |
| absorbed / accepted | 71.9 % | 75.1 % | 75.9 % |
| \|S11\| at 1.9 GHz, Zin | −18.1 dB, 41.4 − j7.5 Ω | −16.8 dB, 40.5 − j9.1 Ω | −21.4 dB, 42.2 + j0.5 Ω |
| \|S11\| minimum | 1.94 GHz, −23.0 dB | 1.94 GHz, −22.0 dB | 1.91 GHz, −21.5 dB |
| 1 g peak cube's centre | (0, −0.7, 11.1) mm | (−0.5, −0.7, 10.6) mm | (−0.5, −0.4, 10.6) mm |

The percentages are each run's difference from the fine grid. Both peaks sit above the feed, in the liquid's first cell, as a
system check's should; the liquid's mass is its 160 × 100 × 150 mm at 1000 kg/m³, 2.400 kg. **psSAR is converged to about
2 % from the medium grid on** (1g +1.6 %, 10g +0.9 % against fine), and the coarse grid reads 3–4 % low. The approach is not
monotonic — the medium grid overshoots — because the grid generator places the strip's edges and the feed's lines differently
at each density (the port is written 0.48–0.50 mm inside the strip's drawn width on the two coarser grids, `explain`'s
"snapped" lines), which moves the dipole's own resonance: its |S11| minimum is at 1.94 GHz on the two coarser grids and
1.91 GHz on the fine one. The wall times were measured with another openEMS run sharing the machine and are only an
order of magnitude. The committed output is all three (`output/coarse`, `output/medium`, `output/fine`); the band sweep
`SystemCheck` makes and runs the medium one.

### 14.6 Gates

`tests/Ui.Tests/Sar/PhoneSarTests.cs`, `tests/Ui.Tests/Sar/SystemCheckTests.cs`:

- `PlacementTests`: a separation from a flat phantom's `liquid` (10 mm, no tilt) and `floor` (7 mm, tilted 25° about x)
  is the nearest distance in the re-elaborated document to 1 DBU, and the liquid's is the floor's plus the shell; a two-part
  group against the generic head's `ear-left`, tilted 15° about y, touches it to 1 DBU and moves as one; placing the phantom
  itself and a separation into the shell are refused.
- `BandSweepTests`: three bands make three setups, each at its band with 1 g and 10 g, its sweep scaled, and its own liquid
  values in the assembled problem (three σ); creating again replaces them. The table has three rows at 0.5 W and a 0.5
  duty cycle, a target's difference, and a value above 1.6 W/kg named as above it with no "fail" anywhere; a band not run
  yet says so.
- `SystemCheckResultTests` (routine, no process): the committed output loads, the dipole is 10 mm from the liquid, and the
  table renders as text and as the dialog's rows.
- `SystemCheckRerunTests` (`Benchmark`): the band sweep and the two convergence setups through the CLI as a process, each
  within 1 % of its committed psSAR; `CRF_WRITE_SAR12_OUTPUTS=1` rewrites the output.

### 14.7 Not done in SAR-12

Hand and body-worn positions, absorbed power density above 6 GHz and compliance reports (the brief's §3). Also not done: a
placement that remembers itself (by design: it writes ordinary transforms). openEMS refuses a sheet tilted out of every
axis plane and a port whose direction leaves the axes (`CsxcadWriter`, before any process), so a tilt that is not a quarter
turn is for a device of solids — a STEP model, a box — not for a drawn strip antenna fed by a lumped port; a tilt about the
face's normal keeps a sheet in its plane and is written as a polygon.

---

## 15. Acceptance: the series against overview §4 (SAR-13)

Every item of overview §4, its tolerance, the value measured and where, and whether it passes. Nothing was widened (O4); two
comparisons were made like with like by the owner's decision (2026-10-18), and those rows say so. Values are per watt accepted
unless stated.

### 15.1 The numbers (R-sar13-1)

| # | §4 item | Tolerance | Measured | Where | Verdict |
|---|---|---|---|---|---|
| 1 | SAR engine: local SAR against `sar_calc` on the same raw dump | 1e-5 relative | **3.3e-7** worst of 463,363 tissue cells (`sar_calc --method SIMPLE --mass 0` on circuitRF's own loop-coil dump, SAR-8 run (a), against circuitRF's local-SAR volume of the same run; no cell differs in which cells are tissue). Measured in SAR-13; until then held only by the analytic block (1e-6) and by item 2 | this section | pass |
| 2 | SAR engine: 10 g peak against `sar_calc` | 0.5 % | **identical** (3.91953e-27, same cell) on `Dipole_SAR`'s raw dump | §7.5 | pass |
| 3 | SAR engine: 1 g peak against `sar_calc` | 0.5 % | **identical** on the random phantom, where 1 g cubes reach the grid's edge | §7.5 | pass |
| 4 | SAR engine: every averaged cell within 1 % on ≥ 99 % of cells | 1 %, 99 % | **100 %** of cells, worst 7.8e-7 (10 g, dipole); 100 %, worst 1.1e-7 (1 g, random) | §7.5 | pass |
| 5 | SAR engine: analytic, a uniform block | exact to the stated tolerance | local 2.5e-4 W/kg to 1e-6; 10 g average equal to local everywhere | §7.8 | pass |
| 6 | SAR engine: analytic, a layered slab under a plane wave | exact to the stated tolerance | **not built.** The layered case is the two-layer block, whose 1 g cube across the interface has a hand value, held exactly; no plane-wave slab was made. circuitRF's openEMS writer has no plane-wave excitation, and SAR-5 held the averaging against `sar_calc` on real dumps instead | §7.8 | **miss**: not measured |
| 7 | `Dipole_SAR` (a): \|S11\| across the band | 0.01 linear | 7.4e-4 at the worst of 11 points | §9.2 | pass |
| 8 | (a): Zin at f0 | 1 % | 0.036 % | §9.2 | pass |
| 9 | (a): accepted power | 1 % | equal as a fraction of the incident wave, 0.99777 both. Compared as a fraction because each run's watts are its own pulse's | §9.2 | pass |
| 10 | (a): psSAR10g | 2 % | **+0.09 %** (2.4372 against 2.4350 W/kg); the reference's own run-to-run spread is 0.24 % | §9.2, §3.2 | pass |
| 11 | (a): whole-phantom SAR | 2 % | +0.02 % | §9.2 | pass |
| 12 | (a): power budget | 95–100 % | 97.99 % (reference 97.93 %) | §9.2 | pass, after one defect fixed: the far-field surface sat inside the PML on given lines (budget 80.9 % first) |
| 13 | `Dipole_SAR` (b), circuitRF's own grid: psSAR10g, explained | 5 % | **−2.45 %**, same peak cell; cause: the resolved 0.2 mm strip is a thinner radiator than the script's unresolved line (Zin 54.6 + j8.6 against 53.1 + j3.7 Ω), then the coarser cells before the skin | §9.4 | pass |
| 14 | `MRI_Loop_Coil` (a): S11 minimum frequency | 0.5 MHz | 0 (298.18 against 298.18 MHz), over the 245.4 ns both runs ran — the owner's like-with-like decision; each to its own end, also 298.18 against 298.18 | §10.5 | pass |
| 15 | (a): S11 depth | 1 dB | 0.01 dB (−50.42 against −50.41 dB) over the common window; each to its own end the depths differ by 5.5 dB, which is where each run's ring-down was cut, not the model | §10.5 | pass (owner's window) |
| 16 | (a): \|B1+\| at the phantom centre | 3 % | **−0.34 %** against the reference re-sampled to the cell centre (0.2416 µT/√W) — the owner's decision, because the tutorial's DumpMode 0 samples H half a cell off and that alone is 3.0 %. Against the printed 0.2489: −3.3 % | §10.4 | pass (owner's sampling) |
| 17 | (a): peak local SAR | 5 % | −0.64 % on both planes | §10.6 | pass, after one defect fixed: the feed's box lay 0.3 nm inside the given lines and openEMS excited nothing |
| 18 | (a): B1+ and SAR planes correlated | ≥ 0.99 | SAR 1.0000 / 1.0000; \|B1+\| 0.9997 / 1.0000 — with the phantom voxelized as the tutorial's is (0.914 / 0.925 with the smooth layers) | §10.3, §10.6 | pass |
| 19 | Co-simulation: B1+ against SAR-8's lumped run | 2 % | **+1.50 %** | §11.6 | pass |
| 20 | Co-simulation: psSAR10g | 3 % | **+2.97 %**, the same cell | §11.6 | pass, at the edge. Four fifths of it is SAR-8's ring-down cut at −36 dB (run to −60 dB: +0.59 %); the remaining uniform 0.29 % in field is **not explained** |
| 21 | Co-simulation: no EM run after a capacitor change, by a counter | 0 launches | 0 openEMS launches over two recombinations (`CosimNoEmRunTests`), 0 over the optimizer (`LoopCoilOptimizeTests`) and over 200 yield trials (`CapacitorYieldTests`) | §11.8, §12.5 | pass |
| 22 | No regression: every existing openEMS gate after the move to 0.37.0 final | green | the F0 cases re-run on 0.37.0: the long ones bit-identical, the three short ones moved by at most 3.6e-6 in \|S\| (they stopped at a different step) | §3.2, em-3d-f0-findings.md §6 | pass |

**The birdcage** (SAR-8b) is not an overview §4 item: the owner moved it out of SAR-8 and gave it its own gates, R-sar8b-6.
Its numbers and verdict are §10a.5's. **`Dipole_SAR` (b)'s and `MRI_Loop_Coil` (b)'s own-grid differences** are explained in
§9.4 and §10.7; the loop's 7 % resonance shift on circuitRF's grid is a generator property (cells normal to a sheet), and
changing the generator is the owner's.

**One miss, item 6**, and it is a missing measurement, not a disagreement.

### 15.2 Owner decisions and their outcomes

| | Decision (2026-10-17) | Outcome |
|---|---|---|
| O1 | Reuse PureHDF 2.1.2 for HDF5 | Done (SAR-4): every openEMS file and CSXCAD's DiscMaterial read and written with it, no HDF5 code of circuitRF's own and no new dependency. Found: CSXCAD reads a DiscMaterial's index as one byte, so a body holds at most 255 tissues (§6.4, §13.3) |
| O2 | The Gabriel 1996 model implemented by circuitRF, plus an importer for a tissue table the user downloads | **Model done** (SAR-2): 16 tissues, evaluated at the SAR frequency, against an independent evaluation of the published parameters. **The importer was not built** (R-sar2-6): it needed the MCP `import` tool to gain a mode first; SAR-11 has since given it a `what` selector, so the way is open. Eight tissues ship with no density, and no tissue with thermal values, because no free source for them was verified (§4.3) |
| O3 | `sar_calc` as an optional cross-check | Done (SAR-5, SAR-6): `Sar.CrossCheck`; identical 10 g peaks on the dipole and the loop coil; found only beside the openEMS that wrote the dump; judged by its output file, its exit status being inverted |
| O4 | §4's tolerances as proposed | Kept; none widened. Two comparisons made like with like by the owner (2026-10-18): B1+ at the cell centre (item 16) and S11 over a common time window (items 14–15) |
| O5 | Octave on the developer machine, the birdcage in the reference set | Done (SAR-1): the birdcage reference ran 53 min on 0.37.0 and is in the local reference set; replicated by SAR-8b on circuitRF's cylindrical grid (§10a) |
| O6 | Co-simulation lives in a schematic: an EM Field Data block plus per-port fields | Done (SAR-9, SAR-10): the `fields` analysis, field goals, tuning, the optimizer, yield and multi-channel drive, none of which starts an EM run |

Decisions taken during the series and recorded where they were taken: the reference data stays local, never in the
repository (SAR-1); openEMS's list is of tested versions (SAR-1); the bundled phantom voxelized as one DiscMaterial (SAR-8);
the birdcage as its own brief (SAR-8); timings need not be exact when runs share the machine (SAR-12).

### 15.3 The examples (R-sar13-2, R-sar13-3)

Two example workspaces ship, each run once here on openEMS 0.37.0 while another openEMS run shared the machine:

- **SAR MRI Loop Coil** — `LoopPorts.c3d` on circuitRF's own grid (`CellsPerWavelength` 30 at 498 MHz, 1.04 M cells against the
  tutorial's 3.04 M; 98–498 MHz in 41 points), the EM run 95 s. A test bench puts the three gap capacitors, a feed capacitor and a
  series match around the `.s4p` and optimizes them against S11 ≤ −20 dB at 298 MHz, mean |B1+| over the brain ≥ 0.2 µT/√W and
  psSAR10g ≤ 2 W/kg, both per watt available: simplex, 21 iterations, 40 evaluations, 4 s, no EM run. At the optimum, against
  §11.6's co-simulation on the tutorial's grid: mean |B1+| −0.4 %, psSAR10g −2.9 %, the single-cell peak of local SAR −26 %. The
  README says so, as the cost of the grid; the coarse cells normal to the coil's plane also move the tutorial's 5.4 pF to about
  6.4 pF on this grid (§10.7), which the circuit absorbs.
- **SAR Phone Dipole** — §14.5's system check at 1.75 and 1.95 GHz by a band sweep at 0.25 W incident: psSAR1g 41.95 and
  40.19 W/kg per W, psSAR10g 21.61 and 20.66; at 1.75 GHz the dipole reflects 28 % of the incident power. Two runs, 5.2 min.

### 15.4 What a reviewer should know before `sar` is merged

- **The Courant estimate's warning fires on both examples** (openEMS's time step 1.01 and 1.03 times circuitRF's estimate,
  "outside 0.5–1"). The answer is openEMS's and is unaffected; the warning is about circuitRF's pre-run estimate and appears on
  every run of either example.
- **`netlist --to-schematic` wrote an SnP `File` relative to the `.csch`'s folder** (`../../results/…` for a test bench in a
  cell), where a schematic resolves it against the workspace root. Found authoring the loop coil's bench, and fixed in
  `NetlistSchematic` (`src/Design/RESOLVED.md`).
- **The optimizer cannot reach a −20 dB match over 2 MHz** on a loaded coil of Q ≈ 110: the match band is about 0.5 MHz. The
  example's goal is at 298 MHz alone, as SAR-10's test is.
