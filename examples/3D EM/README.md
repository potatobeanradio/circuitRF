# 3D EM

Three structures that circuitRF's planar solver cannot represent, or can only compare against, each
set up for **Palace**, the 3D finite-element solver circuitRF runs. Each one runs in minutes on a 16 GB
laptop. The guide to 3D EM in circuitRF is the **3D EM** page of **Help ▸ circuitRF Documentation**,
which walks through this same workspace.

## First: Simulate will ask you to install Palace

Palace and its mesher, Gmsh, are separate programs, and circuitRF does not include them. **Opening this
workspace needs neither.** The first time you press **Simulate** on a 3D setup without them, the run
is refused with **Install Palace …**. That is the expected first step, not a fault. Palace is built from
source, which took 52 minutes on the reference machine below. Gmsh installs in under a minute.

**You can look at every model before installing anything:** open a setup in a cell's `em` folder and
press **Show 3D**.

## What is here

| Cell | What it shows |
|---|---|
| **Bond wire** | One 1 mil gold wire between two pads, solved in 3D, beside the wire kernel's answer for the same wire |
| **Via through a plane** | A line that changes sides of its ground plane through a via. The planar solver refuses it; Palace answers it |
| **Package** | A package with its lid on: its capacitance and inductance matrices, and the lid's resonances |

## The numbers, and where they come from

Every time and memory figure below was **measured once**, on an Apple M4 with 10 cores and 16 GB, running
macOS 27.0, Palace 0.18.1 on 10 processes, and Gmsh 4.15.2. A time is the wall clock for the whole run,
Gmsh and Palace together. A memory figure is Palace's own peak over all its processes. Your machine will
differ in time, and very slightly in the last digit of an answer.

The key numbers are also in `expected-numbers.json`, beside this file, with the tolerance each is held
to. circuitRF's own tests re-run every setup against that file, so this page and the solver cannot
quietly disagree.

**`check` on this workspace** reports one warning per layout: its port labels carry no area for the
design rules to check. That is expected. The one error, on the planar via setup, is there on purpose
(see below).

## Bond wire

A 1 mil (25.4 µm) gold wire with a hexagonal section and a wedge foot at each end. It loops 150 µm high
between two 100 µm pads on 100 µm of alumina, inside a closed metal box 3 × 2 × 1.1 mm. A port sheet runs
from each pad's outer edge down to the ground.

`Bond wire/em/Bond wire 3D.cem`, Palace at the **Standard** preset, 1–40 GHz:

- Took **179 s**, with a peak of **3.1 GB**.
- |S21| is **−0.765 dB** at 10 GHz.
- The series inductance is **906 pH** at 1 GHz: the π-model's series term, −Im(1/Y21)/ω.

**What the other presets give.** *Draft* takes 5 s but reads 813 pH and −0.514 dB. The starting mesh
around a 25 µm wire is exactly what refinement exists to fix, so on a wire Draft is not enough.
*Accurate* took 8.5 min at 7.0 GB and reads 908 pH and
−0.772 dB: within 2 pH and 0.01 dB of Standard, for nearly three times the time and more than twice
the memory. Standard is the setting for a wire.

**The same wire through kernel W.** `Bond wire/layout/Bond wire.wBond` holds the wire, and wBond's wire
kernel solves it: open it and **Export Touchstone …** with the *Distributed* model. It reads **704 pH**
and **−0.680 dB**. The 202 pH between the two answers is **where the terminals are**, not a disagreement
between the solvers. Kernel W's terminals are the wire's own ends. Palace's ports are at the pads' outer
edges, so its answer also contains the pad between each port and the wire, and the 100 µm drop from the pad
to ground.

## Via through a plane

A 400 µm microstrip on top of a four-layer board, a via through a 900 µm clearance in the middle ground
plane, and an inverted microstrip underneath leaving the other way. The laminate has εr 3.66 and tanδ
0.004, and the metal is copper. Both lines return through the same plane, one from above and one from
below.

**First, see the planar solver refuse it.** Open `Via through a plane/em/Via planar.cem` and press
Simulate. It is refused, with this message:

*This EM setup names 'Ground Plane' as its return plane, but its top surface is at 270 µm, which is NOT below the lowest analysis level 'Bottom Copper' at 0 µm. A return plane must lie BENEATH the conductor it feeds, or there is no dielectric slab between them to solve on. Name a conductor below 0 µm, or restrict this setup's analysis levels to conductors above 'Ground Plane'.*

A planar solver's return plane is the boundary its field solution ends on, so it has to lie beneath
every conductor it solves. Here one line is under it. `check` reports the same sentence as an error on
this setup. It is the only error in the workspace, and it is there on purpose.

**Then Palace.** `Via through a plane/em/Via 3D.cem`, at the **Draft** preset, 0.1–20 GHz, with 1.5 mm of
air on every side of the board and absorbing walls:

- Took **62 s**, with a peak of **4.8 GB**.
- |S21| is **−0.228 dB** at 10 GHz and **−0.430 dB** at 20 GHz.

**What Draft trades away is phase.** Against an independent, hand-built Palace model of the same via
(element order 2, 155,883 tetrahedra), Draft's |S21| is within 0.1 dB, but its phase is 12° off at
10 GHz and 23° off at 20 GHz. The alternatives, each measured on this structure:

- **Element order 2**, keeping Draft's other values: write `"ElementOrder": 2` beside `"Quality"` in the
  setup's `Palace` section. It took 6.5 min at 9.2 GB, and landed within 0.02 dB and 5° of that model.
- **Standard** took 35 min at 9.3 GB, when the presets were measured.
- **Accurate** has never finished on this structure: it was stopped after 23 minutes in its third solve.

## Package

A ceramic package with its lid on. The 8 × 8 mm cavity has a 254 µm alumina base on a metal floor. Two
200 µm gold leads stop 100 µm short of the side walls, and a 1 mil bond wire runs from each lead to a die
pad inside. The lid and seal ring are the closed metal box around it: side walls 100 µm beyond the
leads, and a lid 700 µm above the wires, 1.13 mm above the floor. The die itself is not in the model.

**Capacitance.** `Package/em/Package C.cem`, electrostatic, **Standard**:

- Took **8 s**, with a peak of **1.1 GB**.
- Each lead, with its wire and die pad, has **586 fF** to ground.
- The two leads couple through **0.74 fF**.

**Inductance.** `Package/em/Package L.cem`, magnetostatic, **Standard**:

- Took **22 s**, with a peak of **1.8 GB**.
- Each lead, wire and die pad path has **1.98 nH**.
- The mutual inductance between the two is **0.032 nH**.

The inductance runs on `Package shorted.clay`, a copy of the layout with a via from each die pad to the
floor. Shorting the far end gives each terminal's current a loop, which is the ordinary way to read a
series inductance. It is **external** inductance, the RF value: a 3D run carries current on conductor
surfaces only, and the result's notes say how much the DC value would add.

**Draft is not enough for either.** It runs in 2–3 s but reads 831 fF (42 % high) and 1.66 nH (16 %
low). A static solve has no wavelength to size its elements by, so its first mesh is coarse, and
refinement is what finds the field around the metal.

**The lid's modes.** `Package/em/Package lid modes.cem`, eigenmode, the three modes above 5 GHz, at
**Draft with element order 2** (`"ElementOrder": 2` in its `Palace` section):

- Took **166 s**, with a peak of **1.7 GB**.
- **Mode 3, at 23.65 GHz, is the lid's first cavity mode.** 94 % of its energy is in the air under the
  lid. Its Q is **1,662**, which counts the gold's loss and the two ports' 50 Ω as loads.
- A closed form agrees. The estimate for a cavity much thinner than a wavelength puts the empty
  8 × 8 mm cavity, with the base and the air in series under the lid, at 23.7 GHz. The leads pull it
  down slightly.
- **Modes 1 and 2, at 14.3 GHz, are not the lid.** They are the two leads ringing as lines into their
  ports, with 92 % of their energy in the base. With a Q below 1 they are not sharp resonances: the
  ports' 50 Ω absorbs them.

For a design whose band reaches 20 GHz, the lid's mode is clear of the band. The mode's frequency
scales inversely with the cavity's size, so a cavity 20 % larger each way would bring it to about
19.7 GHz, inside the band.

**What the other settings give.** *Draft* takes 13 s and finds the lid mode at 23.39 GHz, but reads its
Q as 176, ten times too low: order 1 cannot resolve the loss. *Standard* adds refinement passes to the
nonlinear eigenvalue solve, and was stopped after 28 minutes in its last pass.

## What is deliberately not here

**Results.** Run the setups yourself. The answers depend on the solver version, and a committed result
would outlive its version.

**openEMS.** Every setup here is Palace's. An openEMS run of the via or the bond wire is a cross-check,
not a reference. Choose **FEM & FDTD - Compare** in a setup's Solver group to run both solvers and see how
far apart they are.
