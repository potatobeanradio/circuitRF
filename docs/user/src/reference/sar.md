---
title: SAR and B1
slug: reference/sar.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > SAR and B1
lede: Specific absorption rate and the MRI transmit field from a 3D EM run on openEMS — setting it up, what the numbers are normalised to, the B1 convention, and reading the results.
keywords: SAR, specific absorption rate, psSAR, psSAR1g, psSAR10g, peak spatial-average SAR, local SAR, whole-body SAR, SARwb, absorbed power, accepted power, per watt, B1, B1+, B1-, B1 plus, B1 minus, MRI, transmit field, RF coil, B0, SAR efficiency, cube averaging, 62704, sar_calc, openEMS, raw SAR dump, maximum intensity projection, MIP, peak cube, tissue, phantom, Sar group, CellConstantMaterial, research use
---

A 3D view's openEMS setup can compute, at one or more frequencies, the **specific absorption rate** (SAR) in every
lossy tissue it holds, the **peak spatial-average SAR** over cubes of 1 g, 10 g or any mass you name, the power the
tissue absorbs, and for MRI work the **transmit field B1+** and its counter-rotating part B1−. They come out as field
plots on the 3D model, readouts in the setup editor, and scalars in the run's `.npy`.

<div class="callout note">
<span class="label">Research and engineering use</span>
<p>circuitRF's SAR is for research and engineering. It is not a compliance tool and no result claims compliance with
any standard. The averaging follows the cube-averaging procedure of IEC/IEEE 62704-1 as openEMS implements it, and
agrees with openEMS's own <code>sar_calc</code> on the same data: the same peak, the same cell, every averaged cell
within 1e-6. The standard itself was not consulted.</p>
</div>

SAR needs **openEMS** (see [3D EM](em-3d.html)). Palace does not write the per-cell data SAR is computed from, and a
setup that names Palace alone is refused.

## What the numbers are {#what}

- **Local SAR** in a cell is σ|E|²/(2ρ), in W/kg: the cell's conductivity, the peak amplitude of its electric field, and
  its density. Air has no density and no SAR.
- **psSAR** of a mass *m* is the largest average of local SAR over a cube holding *m* of tissue. A cube centred on each
  tissue cell is grown until it holds the mass; it counts only if less than 10 % of it is air and every one of its six
  faces touches tissue. A cell that is the centre of no such cube takes the largest average of a valid cube it lies
  wholly inside, and a cell inside none takes a cube grown outward from one of its own faces. This is why a cube at a
  surface is not simply clipped: a phantom's skin is where the peak usually is.
- **Whole-region SAR** (`SARwb`) is the region's absorbed power over its tissue mass.
- **Absorbed power** (`Pabs`) is ½σ|E|² summed over the tissue's volume.
- **B1+ and B1−**: [the B1 convention](#b1), below.

## Setting it up {#setup}

In the EM Setup editor, an openEMS setup has a **SAR and B1** group. Tick it, then:

| Control | Key | Default | |
|---|---|---|---|
| **At (GHz)** | `FrequenciesGHz` | required | each inside the sweep |
| **Masses (g)** | `MassesGrams` | 1, 10 | any positive mass; each gives `psSAR<m>g` |
| **Averaging** | `Method` | Cube | *Cube* is the procedure above; *Simple* accepts every centred cube that reaches the mass, with no air or face rule |
| **Region** | `Region` | all tissue | the objects or phantoms every number is reported over; a phantom's name stands for all its layers |
| **B1** | `B1` | on below 500 MHz | |
| **B0 along** | `B0Axis` | +z | the static field's direction |
| **B1 mean over** | `B1Region` | the first phantom's innermost layer | where the mean \|B1+\| is taken |
| **Cross-check with sar_calc** | `CrossCheck` | off | also runs openEMS's `sar_calc` on the same data and shows its peak beside circuitRF's |
| **Keep per-port fields** | `PerPortFields` | off | keeps each port's fields for [co-simulation](cosimulation.html) |

In the `.c3d` or `.cem` this is the setup's `Sar` section; `circuitrf reference em-setup` lists every key.

**Which cells are tissue.** A solid is tissue when it is a dielectric with a conductivity above zero and its material
states a **density**. A lossy material with no density in the region is refused, naming it: openEMS would write it as
air and every SAR tool would then ignore it. Several of circuitRF's shipped tissues state no density, because no free
source gives one; add one, with its source, to your own copy (see [Materials](materials.html#tissue)). A board whose loss
is a loss tangent has a conductivity of zero and is never tissue.

**One frequency per tissue evaluation.** A body tissue's εr and σ change with frequency, and an openEMS run sees each
material as one constant pair over its whole band. circuitRF evaluates every frequency-dependent material once, at the
**first** SAR frequency, so the SAR and B1 a run reports are valid at that frequency only. Two SAR frequencies with a
frequency-dependent tissue are refused; make a second setup for the second frequency, or use a
[band sweep](phone-sar.html#bands), which makes one setup per band for you.

**What the group switches on.** *Cell-constant material* (each FDTD cell takes the material at its centre), which cube
averaging needs. It is slightly less accurate at a material's face, and the run says so. Unticking it in the openEMS
options overrides the group.

**Disk.** Each SAR frequency and driven port writes a raw dump of the tissue's box, 36 bytes a cell, plus about 276 bytes a
cell for B1's magnetic field while the run lasts. The run's notes and `circuitrf explain` estimate it before the run,
and it is a warning above 4 GB.

**Refused before any run:** a SAR frequency outside the sweep; a setup with no lossy dielectric; a region that names no
object; a non-driven problem; on a cylindrical grid, an averaging mass or the cross-check (a cube is a Cartesian grid's,
so a cylindrical grid gives local SAR and B1 only).

## Units and normalisation {#units}

The run's excitation is arbitrary and tiny, so every SAR, power and B1 number circuitRF shows is **per 1 W accepted** at
the driven port: SAR divided by the accepted power, B1 divided by its square root.

| Quantity | Unit | Per |
|---|---|---|
| local SAR, psSAR, SARwb | W/kg | W accepted |
| Pabs, Prad | W | W accepted (a fraction) |
| B1+, B1−, mean \|B1+\| | µT (T in the `.npy`) | √W accepted |
| SAR efficiency | µT/√(W/kg) | — (mean \|B1+\| over √psSAR10g, the same at any power) |
| Pacc | W | **as solved**, the one value that is, so the solved scale can be recovered |

The accepted power is ½Re(V I*) at the port at the SAR frequency, the same rule openEMS's own SAR tutorials use. A field
plot can be drawn at another power: its **Drive power** in the Inspector takes one (`250 mW`, `30 dBm`), referred to
the accepted or to the incident wave, and SAR scales with the power, B1 with its square root.

## The B1 convention {#b1}

With the phasor convention b(t) = Re{**B** e^{jωt}}, **B** = μ0**H**, and B0 along +z, the transverse field splits into two
circularly polarised parts:

<pre><code>B1+ = (Bx + j·By) / 2        B1− = (Bx − j·By) / 2</code></pre>

|B1+| is the part rotating counter-clockwise seen from +z (from +x towards +y), |B1−| the part rotating clockwise. This is
exactly what openEMS's own MRI tutorials compute. Which of the two is the transmit field depends on the sign convention
for precession, and the literature uses both; circuitRF takes openEMS's definition as it stands. **B0 along −z swaps the
two.** For B0 along ±x or ±y the transverse pair is taken cyclically, (By, Bz) and (Bz, Bx).

The mean |B1+| is volume-weighted over **B1 mean over**'s objects.

## Reading the results {#results}

- **The setup editor's readouts**, after a run, per SAR frequency and driven port: psSAR per mass with the peak cube's
  centre and tissue, peak local SAR, whole-region SAR, absorbed, accepted (1 W, and the value as solved), radiated and the
  power budget when the setup has a radiation pattern, mass, mean |B1+| and the efficiency, and `sar_calc`'s peak with the
  cross-check on. `circuitrf em` prints the same, one line per port.
- **Field plots.** Each run offers its SAR and B1 as two solutions of their own, labelled *(SAR)* and *(B1)*, at the SAR
  frequency and port. The quantities are `SAR` (local), `SAR1g`, `SAR10g` and so on per mass, `B1p` and `B1m`. SAR is drawn
  over tissue only, so air is transparent; B1 is drawn over the whole box, because B1 is wanted in the air inside a coil.
  - **Maximum-intensity projection** (a SAR clip plane's *Maximum-intensity projection* box, or `render --mip x|y|z`): each line through the volume
    along the plane's axis carries its largest value, so one plane shows the hottest tissue wherever it lies.
  - **The peak marker**: the peak cube as a wireframe in the 3D view, and on a section its square, solid where the plane
    cuts it and dashed, with its distance, where it does not. Local SAR has no cube and is marked with a cross.
- **The `.npy`** gains a `sar` group, each cube over the SAR frequencies and the driven ports: `psSAR1g`, `psSAR10g` (per
  mass), `SARwb`, `Pabs`, `Pacc`, `Mass`, and with B1 `B1pMean` and `SarEff`; with a radiation pattern also `Prad` and
  `PowerBudget`, (absorbed + radiated) / accepted, which should lie a little under 1.
- **The volumes** (local SAR, each mass's average, B1+, B1−) are kept as float32 `.npy` arrays in the run folder, beside
  openEMS's raw dump, with their cell centres. Their names are in the run's `sar<j>.json` record.

Headless: `circuitrf em view.c3d --setup <name>` runs it, `circuitrf explain view.c3d` reports the group, the tissue each
object is, ρ, εr and σ as the solver will see them and the disk estimate, and `circuitrf render view.c3d --field <plot> -o
out.png` draws a SAR or B1 plot. MCP's `run analysis=em` and `render` do the same.

## How it was checked {#checked}

circuitRF rebuilt openEMS's own SAR and MRI tutorials from 3D-view objects and ran them on openEMS 0.37.0:

| Case | Compared | Result |
|---|---|---|
| Dipole beside a three-layer head, 1 GHz, on the tutorial's grid | psSAR10g per W, whole-phantom SAR, power budget | +0.09 %, +0.02 %, 97.99 % (the tutorial's 97.93 %) |
| The same, on circuitRF's own grid | psSAR10g per W | −2.45 % |
| 7 T loop coil beside a head phantom, on the tutorial's grid | \|B1+\| at the centre, peak local SAR on two planes, S11 minimum | −0.34 %, −0.64 %, the same frequency to 0.01 MHz |
| The raw data of the dipole, averaged by circuitRF and by `sar_calc` | 10 g peak, every cell | identical peak and cell; every cell within 7.8e-7 |

openEMS stops a run when its energy has decayed, checked on wall-clock time, so two runs of the same problem can stop at
different steps; that alone moved the dipole's psSAR10g by 0.24 % between runs of the tutorial itself.

## Related {#related}

- [Co-simulation](cosimulation.html): parts as ports, and B1, SAR and the radiation pattern from a schematic with no new EM run.
- [Tissues and phantoms](phantoms.html): the materials and the bodies SAR is computed in.
- [Voxel and mesh import](anatomy-import.html): your own segmented anatomy.
- [Phone SAR](phone-sar.html): placement against a phantom, band sweeps and reference values.
