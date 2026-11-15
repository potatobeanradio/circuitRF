# Dividers and Couplers

RF dividers and couplers placed as single parts, each stated by its specifications and drawn from the same plan its
circuit model simulates. One cell per component. Today the workspace holds the **Wilkinson divider**: an MWPD at 2.4 GHz
on a 20 mil RO4350B board, laid out by Update Layout from Schematic and solved on the Planar MoM solver, EMerge and
Palace against its circuit model.

![The board: port 1's line on the left, the divider's ring, the 0402 isolation resistor across the ring's gap, and the two output lines bending away to ports 2 and 3.](pictures/wilkinson-layout.png)

## The cell

| Cell | Views | What it is |
|---|---|---|
| **Wilkinson** | schematic, layout, EM setup, 3D view | The test bench and the board: a 5 mm input line, the MWPD, a 100 Ω resistor with an 0402 land pattern on R2-R3, and each output bending away through a 90° MCURVE and a 5 mm line to its port |

## How it was built

1. **Schematic.** `WPD1` is an MWPD with `Z0` 50 Ω, `F` 2.4 GHz and `Gap` 1.35 mm, the 0402's pin span. Nothing else is
   typed: select it, and its parameters show what the board makes of those specs — a 1.05768 mm port width, 0.548 mm arms
   of 70.7 Ω, a quarter wave of 18.903 mm and a ring radius of 5.743 mm. `R1` is 100 Ω, 2·Z0, with `Footprint`
   `smt:0402`, dropped on the symbol's R2-R3 pins. The lines use the VAR `W50`, set to the readout's port width exactly,
   so they meet the divider's pins with no step in the copper.
2. **Why the outputs bend away.** The divider's two output pins are only Gap + W apart, 2.4 mm centre to centre. The
   Planar solver calibrates each port on an isolated line and refuses a port with another port's metal closer than five
   substrate heights, so each output turns 90° away and runs 5 mm before its port. A board would route them apart anyway.
3. **Design ▸ Update Layout from Schematic.** It places the ring, lays the 0402 across the ring's gap, chains the lines
   on, puts the three ports at the line ends and pours the ground plane on Bottom Copper. LVS matches
   (`circuitrf lvs Wilkinson`).
4. **EM setups.** `Wilkinson/em/Wilkinson Planar.cem` is the Planar setup. **Design ▸ New 3D View from Layout** made the
   3D view, which has two setups, **Palace** and **EMerge**. All three name `R1` as a component port.

## Run it

| Step | What | Time here |
|---|---|---|
| 1 | Simulate the schematic | under a second |
| 2 | Simulate the EM setup **Wilkinson Planar** | about 4½ min |
| 3 | Open the 3D view, run setup **EMerge** | about 1 min |
| 4 | Run setup **Palace** in the same view | about 2 min |

Times are from a shared Linux server, 8 threads for Planar and EMerge, and 8 Palace processes on one compute node. The
first 3D run offers to install its solver. The three Touchstone files the runs write are in `results/`, so the comparison
below can be read without running anything.

## The resistor and the EM runs

**Each EM run has the resistor as a component port.** The ring joins R2 to R3 the long way round, so the 0402's two
pads lie on one piece of copper, and the port across them closes a loop. At RF that is an ordinary port. Each run warns
that the port's 0 Hz row would read the ring's copper, close to a short; this sweep has no 0 Hz point. Each result is a
four-port: ports 1 to 3, and `R1` as port 4. The co-simulation netlist each run writes (`<setup>_cosim.cnl`) closes
port 4 with the 100 Ω. The EM numbers below are that closed three-port, against the circuit model with its 100 Ω.

Driven from port 1, the resistor carries no current: the two arms are mirror images, so R2 and R3 sit at the same
voltage, and |S11|, |S21| and |S31| hardly depend on it. The isolation, |S23|, is what the resistor sets.

## What you will see

![|S21|, |S11| and |S23| against frequency: the circuit model as a line, the three EM solvers as markers, each with R1's port closed by 100 Ω.](pictures/wilkinson-results.png)

At 2.4 GHz, each EM result with port 4 closed by 100 Ω:

| | Circuit model, 100 Ω | Planar MoM | EMerge | Palace |
|---|---|---|---|---|
| \|S11\| | −47.5 dB | −28.2 dB | −24.6 dB | −23.5 dB |
| \|S21\| | −3.149 dB | −3.113 dB | −3.130 dB | −3.163 dB |
| \|S31\| | −3.149 dB | −3.113 dB | −3.137 dB | −3.183 dB |
| \|S23\| | −36.4 dB | −29.2 dB | −25.1 dB | −18.6 dB |

Across 1.2 to 4.8 GHz, eleven points from F/2 to 2F:

| | Planar MoM | EMerge | Palace |
|---|---|---|---|
| Largest difference in \|S21\| from the circuit model | 0.06 dB | 0.21 dB | 0.44 dB |
| Largest difference in \|S31\| from the circuit model | 0.06 dB | 0.30 dB | 0.54 dB |
| Worst \|S11\| in the band (circuit model −9.1 dB) | −8.9 dB | −9.9 dB | −7.8 dB |
| Deepest \|S11\| of the eleven points | 2.40 GHz | 2.09 GHz | 2.40 GHz |
| Deepest \|S23\| of the eleven points (circuit model −36.4 dB at 2.40 GHz) | −29.2 dB at 2.40 GHz | −27.5 dB at 2.09 GHz | −20.4 dB at 2.09 GHz |
| Largest difference in \|S23\| outside 2.0 to 2.8 GHz | 2.3 dB | 2.0 dB | 3.4 dB |

**All three solvers put |S21| within 0.04 dB of the model at 2.4 GHz.** The model's |S21| of −3.149 dB is the ideal
−3.01 dB plus about 0.14 dB of conductor and dielectric loss in the arms and the 10 mm of line. Planar follows the model
within 0.06 dB across the whole band. Its |S11| null is where the model's is, but 28 dB deep rather than 47 dB: a deep
null is a small difference of two large numbers, and a few hundredths of a dB of arm loss or junction reactance moves
it.

**The isolation is a null too, and the solvers find it less deep than the model.** Planar puts it at 2.4 GHz, 29 dB
down rather than 36. EMerge and Palace put it lower, at the 2.09 GHz point, and shallower. Away from the null all three
follow the model's |S23| within 2 to 3.4 dB. The null needs the resistor's path and the ring's to cancel exactly. In
EM the resistor sits on a port across its 250 µm gap, a strip and delta gap on Planar and a lumped sheet in 3D, and
that port's own reactance moves the cancellation.

**The 3D runs read further from the model away from 2.4 GHz**, Palace most. Both use coarse meshes (below), and in 3D
each port is a lumped sheet from the line down to the ground plane, 20 mil tall, whose own inductance stays in the
answer. Palace's |S21| and |S31| also differ from each other by up to 0.14 dB on a layout that is symmetric: its draft
mesh is not. Its |S11| is 3 to 7 dB worse than the model's from 1.2 to 3.6 GHz, the null aside. EMerge's four-port is not quite passive:
at 4 of the 11 points the largest singular value of S is above 1, at most 1.0037, and the run says so.

**The model warns, and it should.** The run's Messages say that MTEE's junction formulas are calibrated at εr 9.9 and
this board is 3.66, that the tee's branch is a little wider than their W/h range, and that the ring's bend radius,
10.5 widths, is past the 3 to 7 widths MCURVE's length rule is stated for. At 2.4 GHz none of this moves |S21| by more
than the solvers disagree with one another.

## Run times and meshes

| | Mesh | Unknowns | Wall time | Peak memory |
|---|---|---|---|---|
| Circuit model | — | — | under a second | — |
| Planar MoM | 2,944 cells | 5,290 | 4 min 24 s | 1.4 GB |
| EMerge | 11,053 tetrahedra | 80,114 | 49 s | 1.7 GB |
| Palace | 78,748 tetrahedra | 101,692 | 1 min 37 s | 2.7 GB |

The meshes are the same settings as before the resistor was a port; the port's strip (Planar) and sheet (3D) are new
geometry, so each solver meshed slightly differently.

## What was traded for speed

- **Planar: edge mesh off, and `"LocalGrid": false`.** The local grid sizes its cells from the sweep's top frequency, so
  with it on the mesh, and the answer, would change if the band changed.
- **EMerge: the Draft preset, no adaptive mesh passes, and its adaptive frequency sweep.**
- **Palace: the Draft preset and `AdaptiveMaxIterations` 0** — one solve on the initial mesh, no refinement — with its
  adaptive frequency sweep, built from 4 full solves.
- **The 3D air box is 5 mm on the sides and the top**, about ten substrate heights.
- **Eleven log-spaced points from 1.2 to 4.8 GHz**, F·2^(k/5) for k = −5 … 5, so 2.4 GHz is one of them.

## Things to try

- **Change `F` or `Z0`** and simulate: the ring is redrawn and re-solved from the new specs. Update Layout redraws the
  layout; re-run the EM setups to compare again.
- **Remove `R1`, or change it**, and watch |S23| and |S22| while |S21| and |S11| stay put.
