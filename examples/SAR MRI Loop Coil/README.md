# SAR: MRI Loop Coil

A 7 T (298 MHz) surface coil beside a head phantom, designed the way coil designers work: **every capacitor gap is a
port**. openEMS runs once, keeps each port's fields, and from then on the schematic tunes, matches and optimizes the
coil while B1 and SAR follow every value with no new EM run. circuitRF's SAR is for research and engineering use; it is
not a compliance tool.

The coil and the phantom are openEMS's own `MRI_Loop_Coil` tutorial, rebuilt from 3D-view objects. circuitRF reproduces
that tutorial's numbers on the tutorial's own grid; this example runs on circuitRF's own, coarser grid, so it solves in
about a minute and a half (see *What was traded for speed*, below).

## The two cells

**Loop Coil** (its 3D view). An 80 mm (z) by 60 mm (y) loop of 5 mm strips at x = −130 mm, drawn as eight PEC
sheets, with four gaps a third of a strip wide. **Each gap is a 50 Ω lumped port**: the feed at the bottom is port 1,
the left, right and top gaps are ports 2, 3 and 4. Beside it is the head phantom: three nested ellipsoids (skin, skull,
brain; semi-axes 100, 80 and 100 mm, shells 9 mm), solved as 2.5 mm voxels, in the tissue values openEMS's tutorial
uses at 298 MHz (each with its density, which SAR needs).

Its one setup, **CoSim**: openEMS, 98–498 MHz in 41 points, first-order absorbing (Mur) faces, an end criterion of
−40 dB, and a **Sar** group at 298 MHz — 10 g averaged SAR by cubes, B1 with B0 along +z, and **Keep per-port fields**
on. Two field plots are saved: B1+ and SAR on the axial plane, both drawn from the schematic's fields analysis `F1`.

**Coil Bench** (a test bench). The coil's saved run as one **EM Field Data** block, `EM1`, on `results/Loop Coil CoSim.openems.cemr`, with its
four pins named after the run's ports, and around it:

| Part | What it is |
|---|---|
| `Cleft`, `Cright`, `Ctop` | the three gap capacitors, all set by the variable `Cgap` |
| `Cf` | the capacitor across the feed gap, in series with the loop like the other three |
| `Cm` | the series match from the 50 Ω source to the feed |
| `P1` | the 50 Ω source |

and two analyses: `SP1`, S-parameters over 280–316 MHz, and `F1`, a **fields** analysis at 298 MHz that recombines the
four ports' fields with the waves this circuit delivers into them. `F1` is normalised **per watt available** from the
source (`norm=available`), so a poor match shows as weak B1, as it would on the scanner.

## Run it

1. **Simulate the Loop Coil's 3D view** (setup `CoSim`). It needs openEMS; the first run offers to install it. Four
   openEMS runs, one per port, about 95 s in all, and a result record and a field set of 87 MB written beside the `.s4p` in `results/`.
2. **Simulate the Coil Bench.** `EM1` reads the saved result, so editing the setup later changes nothing it uses until the setup runs
   again; its **Run settings…** shows what differs. `SP1` gives S11 and `F1` gives B1 and SAR. The Messages pane's **Open in 3D view**
   shows them on the coil.
3. **Optimize.** The Optimizer panel already holds the variables, the goals and an algorithm (simplex, seed 1). Press
   **Run**.

As drawn, with the tutorial's 5.4 pF in the gaps, a 33 pF feed capacitor and a 4.7 pF match, the coil is far off
resonance on this grid: 0.3 % of the available power is accepted and the mean |B1+| in the brain is 0.010 µT per √W.
The optimizer meets all three goals in 21 iterations and 40 simulations, about 4 s:

| | As drawn | Best |
|---|---|---|
| `Cgap` | 5.4 pF | 7.450 pF |
| `Cf.C` | 33 pF | 14.10 pF |
| `Cm.C` | 4.7 pF | 5.061 pF |
| `Match`: S11 at 298 MHz, ≤ −20 dB | −0.01 dB | −20.5 dB |
| `B1`: mean \|B1+\| over the brain, ≥ 0.2 µT/√W | 0.010 | 0.203 |
| `SAR10g`: psSAR10g, ≤ 2 W/kg per W | 0.005 | 1.80 |

**No EM run starts during any of it.** Each of the 40 simulations solves the circuit, recombines the stored fields and,
for `SAR10g`, averages the recombined SAR over 10 g cubes from scratch. The run's notes name that goal as the expensive
one.

The three gap capacitors and the two match capacitors are also on the **Tuning** panel. Drag `Cgap` with the 3D view's
B1+ plot open and the plot follows the slider; the averaged SAR is recomputed when you let go.

## The numbers at the optimum

Per watt accepted at the feed, against the same coil solved with ports for capacitors on the tutorial's own grid lines
(circuitRF's replication of the tutorial, `docs/design/sar.md` §11.6):

| | This example (circuitRF's grid) | The tutorial's grid | Difference |
|---|---|---|---|
| grid | 103 × 92 × 110 = 1.04 M cells | 154 × 122 × 162 = 3.04 M cells | |
| mean \|B1+\| over the brain | 0.2040 µT/√W | 0.2049 µT/√W | −0.4 % |
| psSAR10g | 1.814 W/kg | 1.868 W/kg | −2.9 % |
| peak local SAR (one cell) | 6.63 W/kg | 8.92 W/kg | −26 % |
| absorbed / accepted | 80.8 % | 83.1 % | |
| EM run | 95 s | about 3 min | |

The 10 g peak sits in the skin, level with the coil's side, at (−80.6, +33.1, −11.6) mm; on the tutorial's grid it is the
mirror-image point at y = −29.2 mm. The loop is nearly symmetric about y = 0, so the two sides' peaks are close, and which
side wins moves with the grid.

## What was traded for speed

- **The grid.** circuitRF places its own lines (`CellsPerWavelength` 30 at the sweep's 498 MHz) instead of the
  tutorial's 3.04 M-cell grid, a third of the cells. B1 and the 10 g average are within 3 %. The single-cell peak of local
  SAR is not: it is set by the few cells at the skin nearest the strips, and it reads 26 % low here. Read local SAR's
  peak from a finer grid, or from the averaged values.
- **The coil's tuning moved.** The cells normal to the coil's plane are coarse on this grid, which lowers the loop's
  inductance (`docs/design/sar.md` §10.7). With the tutorial's own 2.5 Ω feed this grid resonates the loop at about
  6.4 pF, not 5.4 pF. The circuit absorbs that: the optimizer finds the values for this grid, which is the point of
  keeping the capacitors out of the EM run.
- **The sweep.** 98–498 MHz in 41 points (10 MHz apart), where the tutorial's runs 0.4–595.6 MHz in 497 points. With every
  gap a port the block has no resonance of its own, so 10 MHz is enough between points, and 298 MHz is one of them: the
  match goal and `F1` read the block where it was solved.

To run on the tutorial's own lines, `OpenEms.GridLines` in the setup takes a grid from a file (the EM Setup reference,
"Running on given grid lines").

## Things to try

- Change the phantom's tissues to circuitRF's own frequency-dependent ones (the technology editor's **Add Tissue
  Materials**, then Muscle, Bone (cortical) and Brain (grey matter); Skin (wet) states no density, so SAR refuses it).
  Re-run the 3D view: the fields belong to the tissues they were solved with.
- See how uneven a surface coil's field is: `measure Flat = B1pCoV(F1, "brain")`, the spread of |B1+| over the brain
  divided by its mean, reads about 1.07 at the optimum.
- Put a tolerance on the gap capacitors (`dist=gauss sd=2%` on `Cgap`'s tuning row), add a yield spec
  (`goal SAR10gY = psSAR(F1, 10) analysis=F1 le 2 use=yield`) and run a yield analysis: every trial recombines the
  stored fields, and none starts openEMS.
