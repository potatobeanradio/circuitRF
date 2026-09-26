---
title: 3D EM
slug: reference/em-3d.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > 3D EM
lede: What circuitRF's 3D solvers are for, how to get them, how to set one up and read the answer, and a worked example you can open.
keywords: 3D EM, full-wave, finite element, FEM, FDTD, Palace, openEMS, Gmsh, bond wire, via, package, capacitance matrix, inductance matrix, eigenmode, cavity resonance, lid resonance, wave port, air box, mesh, fields, 3D view, install
---

circuitRF can hand a layout to a 3D solver: **Palace**, a finite-element (FEM) solver, or **openEMS**, a
finite-difference time-domain (FDTD) solver. It builds the 3D model itself, from the layout, its
technology and its bond wires. It meshes the model with **Gmsh**, runs the solver, and reads the answer
back as an ordinary result. The same `.cem` setup document drives it. You choose a 3D solver in that
setup's **Solver** group, and nothing else about the workflow changes.

This page is the guide. The setup panel's 3D controls, and every `.cem` field they write, are described
control by control in [EM Setup ▸ Solver — planar or 3D](em-setup.html#solver-3d). This page links into
that one rather than repeating it.

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#when">When to use 3D, and when not</a></li>
<li><a href="#getting">Getting the solvers</a></li>
<li><a href="#setting-up">Setting one up</a></li>
<li><a href="#running">Watching it run, and reading the answer</a></li>
<li><a href="#view">The 3D view</a></li>
<li><a href="#example">Walking through the example</a></li>
</ol>
</nav>

## When to use 3D, and when not {#when}

**The planar solvers stay the default wherever they fit.** circuitRF's planar method-of-moments solver
(see [the MoM engine](mom-engine.html)) and the bond-wire kernel in [wBond](wbond.html) put their
unknowns on the metal only. A 3D solver meshes the volume, including the air around the structure. A
3D run therefore costs minutes and gigabytes where a planar run costs seconds. A 3D solver is also a
separate program you install. Use 3D for what the planar solvers **cannot represent**, and as an
independent check on them. Do not use it as a replacement.

| Structure | Use |
|---|---|
| Planar metal on a layered board or die, and vias that stitch to ground | The planar solver. A 3D run is an independent cross-check only |
| A signal via **through** a reference plane: a line on each side of the same plane | **3D.** The planar solver refuses it: its return plane must lie beneath every conductor it solves |
| Bond-wire arrays | wBond's kernel. A 3D run is the independent reference it is validated against |
| Leadframes, lids, cavities, stepped metal, connectors, package-to-board transitions | **3D.** Nothing planar represents them |
| A package's capacitance and inductance matrices | **3D (Palace).** Electrostatic and magnetostatic solves |
| Resonances of a cavity or a lid: "is there a mode inside my band?" | **3D (Palace).** An eigenmode solve finds them directly |
| A radiator that is not planar | **3D.** See [Antennas ▸ From a 3D solver](antennas.html#3d) |

**Which 3D solver.** Palace (FEM) and openEMS (FDTD) suit different geometry. The setup panel says which
fits your model when you choose one. This is advice, not a restriction.

- **FEM (Palace)** fits curved and diagonal metal: bond wires, round vias. Its tetrahedra follow a
  surface, and its adaptive refinement puts small elements only where the error is. It is the only one
  of the two that computes capacitance and inductance matrices, and it finds a cavity's resonances
  directly. FDTD would have to ring a high-Q cavity down for a long time.
- **FDTD (openEMS)** fits Manhattan geometry on a stackup, because metal aligned with its grid costs
  nothing. One pulse covers a whole band. Its absorbing boundaries let the air box around a radiator
  stay small. One fine feature sets its cell size, and through that its time step, so a single thin
  wire in a large model is expensive.

Running **both** on one setup (**FEM & FDTD - Compare**) gives two answers from methods that share nothing
numerically, and their agreement is strong evidence that each has converged. It is a cross-check, not a
reference: both solvers read the same generated model, so a mistake in building that model would appear
in both.

## Getting the solvers {#getting}

circuitRF ships no solver and no mesher. Opening a workspace, editing a 3D setup and looking at its model
need none of them. **Simulate** is the first step that does. On a machine without them it is refused,
naming what is missing, and the message offers **Install Palace …** (or Gmsh, or openEMS). **Settings ▸
3D EM** has the same **Install …** buttons, and `circuitrf solver install palace --yes` does the same from
a terminal. [EM Setup ▸ Letting circuitRF install the 3D solvers](em-setup.html#install-assistant) is the
full description: what is shown before anything downloads, where each program goes, and what happens
when an install fails.

**What it costs**, as measured. Each figure is one machine's measurement, and yours will differ:

| Install | Measured |
|---|---|
| Palace, macOS on Apple silicon | 52 min, 1.9 GB, on an Apple M4 (10 cores, 16 GB). Built from source |
| Palace, Linux arm64 | 29 min, 2.1 GB: three clean installs of Ubuntu 24.04 in a container on an Apple M4 (8 GB, 4 build jobs) |
| Palace, Windows (in the Linux subsystem) | Not yet measured. Reported upstream as most of a day |
| Gmsh, macOS | Under a minute, 0.13 GB |
| openEMS, macOS | 8 min, 0.27 GB, built by openEMS's own script |

Palace is always built from source, because no ready-built Palace exists for macOS or for the Linux
subsystem. The build is long but runs in the background, with its progress in the Messages panel, and
**Cancel** stops it at any point.

<div class="callout note">
<span class="label">Palace's licence note</span>
<p>A default Palace build includes ParMETIS, whose licence allows commercial use for evaluation only. If
you build Palace, you accept those terms. circuitRF distributes no copy of Palace, so it passes on none.</p>
</div>

**On Windows**, Palace runs inside your own Windows Subsystem for Linux distribution, and circuitRF treats
it like a Palace on this computer. Gmsh and openEMS run natively. What you need first, and what each
missing piece's message says, is in
[EM Setup ▸ Palace on Windows](em-setup.html#palace-windows).

**By hand.** If you prefer to build the solvers yourself, circuitRF finds them on `PATH`, in a Spack
install tree, in a conda environment, or where you name them in **Settings ▸ 3D EM**. It runs only the
versions it has validated. The exact validated versions and the three build problems met on macOS are in
[EM Setup ▸ Installing the 3D solvers by hand](em-setup.html#install-3d-solvers).

**Removing them.** **Uninstall …** in **Settings ▸ 3D EM**, or `circuitrf solver remove palace --yes`.
circuitRF removes only what it installed, and never touches your documents or results. See
[EM Setup ▸ Removing the 3D solvers](em-setup.html#uninstall-solvers).

## Setting one up {#setting-up}

A 3D setup is an ordinary EM setup whose **Solver** is not *Planar*. Create one as any other (**New ▸ EM
Setup** on a layout, see [EM Setup](em-setup.html#creating)), then choose the solver. Every control
writes a field of the `.cem`, so a setup can equally be written as text. The fields below link to their
full description.

- **Solver** (`Solver3D`): `Palace`, `OpenEms`, or `Both` to run the two and compare them.
  [More](em-setup.html#solver-3d).
- **Problem** (`Problem3D`, Palace only): *Driven* for S-parameters (the default), *Electrostatic* for a
  capacitance matrix, *Magnetostatic* for an inductance matrix, *Eigenmode* for resonant frequencies and
  Q. [Package RLC](em-setup.html#package-rlc), [Eigenmodes](em-setup.html#eigenmodes).
- **Ports.** A layout's port labels become ports. In 3D a port is **lumped** by default: a sheet from the
  line down to its return, with the port's Z₀ across it. A **wave port** (`Ports3D`, Palace only) is fed
  by the line's own mode on the air box's face instead, and has no sheet parasitic.
  [Wave ports](em-setup.html#wave-ports).
- **Terminals** (`Terminals3D`, static solves): the matrix's rows and columns, each named by a net.
  Every conductor on that net belongs to the terminal. A magnetostatic terminal also names the port its
  current enters by.
- **The air box** (`AirBox`): how far each face of the solved region lies beyond the geometry, and what
  the face does to the field. It can be *Absorbing* (open space), *Pec* (a metal wall), *Pmc* or
  *Symmetry*. By default the floor sits on the lowest ground plane and the other five faces are
  absorbing, a fraction of a wavelength out. A closed box of `Pec` faces is a shielded enclosure, and it
  is how the example models a package lid.
- **Quality** (`Quality` in the `Palace` section): *Draft*, *Standard* (the default) or *Accurate*.
  These set the element order, the refinement passes and the sweep tolerance together, and any one of
  those can be overridden on its own. The measured cost of each preset is in
  [EM Setup ▸ Solver](em-setup.html#solver-3d). **Which one is enough depends on the structure, not on a
  rule**: the example below uses both Draft and Standard, and says why for each.
- **Fields** (`SaveFieldsGHz`): which frequencies' fields are saved for the 3D view. By default only the
  sweep's centre is saved, because fields are large.
  [Fields for the 3D view](em-setup.html#palace-fields).

**Look before you solve.** **Show 3D** on the setup's panel opens the 3D view on the model circuitRF
built. It works before any solver is installed. `circuitrf explain` on the `.cem` prints the same model as
text: every solid, its material and its extent, the ports, the air box and its faces, and an estimate of
the problem's size. `circuitrf render x.cem --section xz@y=0um -o cut.svg` draws a section through it.

## Watching it run, and reading the answer {#running}

**Before anything runs**, circuitRF checks whether the run fits in this machine's memory. The first check
uses the model's volumes. A second check, once Gmsh has reported how many tetrahedra it made, uses the
real count. On a model with small metal the second is the one that counts, because refinement around the
metal is nearly the whole mesh. Past 75 % of memory the run carries a warning naming the estimate and
what would shrink it. Past 150 %, Simulate asks before going on.

**While it runs**, the progress row names the solver's own stages, read from its own log: *Meshing
(Gmsh)*; *Solving: refinement pass k of N* with the unknown count; *Sweep: sampling*, with the sweep's
error converging on its tolerance; *Reading results*. The memory the solver is using is beside it. A 3D
run cannot stop early and keep a partial result, so the button reads **Cancel**.

**When it finishes**, one line states what the run cost: the wall time, the solver's own peak memory,
the tetrahedra before and after refinement, the unknowns, and the preset. The same line is recorded in
the result's header.

**Where the answer lands.** A result is named after its solver, so a 3D run never replaces a planar one or
the other solver's:

| Problem | Result |
|---|---|
| Driven | `results/<setup>.palace.sNp` and `<setup>.palace_em.npy`, opened in the Data Display like any S-parameters |
| Electrostatic | `results/<setup>.palace_es.npy`: `C` (Maxwell) and `C_mutual` (the capacitors you would draw). Printed by `circuitrf em` |
| Magnetostatic | `results/<setup>.palace_ms.npy`: `L` and `L_mutual`. Printed by `circuitrf em` |
| Eigenmode | `results/<setup>.palace_eig.npy`: each mode's `f`, `Q`, and where its energy is. Printed by `circuitrf em` and shown on the panel |
| openEMS | `results/<setup>.openems.sNp` |
| Both | the two above, and `results/<setup>.compare_em.npy` holding their difference |

`results/<setup>.palace/` keeps everything the run made: the Gmsh script, the mesh, the Palace
configuration, both programs' logs, and the saved fields. The run can be repeated by hand from it. An
unchanged model reuses its mesh.

## The 3D view {#view}

**Show 3D** on a setup's panel opens the 3D view beside it, drawn by the graphics card. It shows the
model circuitRF built, and after a run it also shows what the solver made.

- **Model.** Every solid, coloured by its layer, with the dielectrics, the air and the air box's faces
  each switchable. The faces are coloured by boundary kind: grey metal, blue absorbing, orange PMC,
  violet symmetry. The **object tree** lists every solid, and a click in the view picks one and names it.
- **Camera.** Drag to orbit; right-drag, middle-drag or Shift-drag to pan; scroll to zoom. **F** fits the model. **1** is
  isometric, **2**–**7** are the six orthographic views, and **P** and **O** switch between perspective
  and orthographic.
- **Clip plane** (**C**). A plane along an axis or the view direction, dragged through the model. It
  shows inside a package, under a lid, or through a via's clearance.
- **Mesh.** After a Palace run, the mesh Gmsh made: the boundary triangles, and the tetrahedra the clip
  plane cuts.
- **Grid.** For an openEMS setup, the FDTD grid on the clip plane and where it meets the metal.
- **Fields.** After a run that saved them: the field at each saved frequency, eigenmode or terminal,
  drawn on the clip plane or on the surfaces. The quantities offered are only those in the solver's
  files: the electric field |E|, the surface current J_s on the conductors, and for a static solve the
  potential. A **dB** scale and a **range** percentile keep one singular edge from washing out the
  picture.
- **Animation.** The play button sweeps the phase through one cycle and draws the instantaneous field
  Re{E·e^jφ}. That is the standing wave in a cavity, or the current running along a wire.
- **Pictures.** **Export picture …** saves a PNG of the view at a multiple of the window's size, with or
  without the legend and caption. Right-click in the view for **Copy Picture**.

**What the mesh tells you about trusting the answer.** Look at where the small elements are. After
refinement they gather where Palace's own error estimate said the answer needed them: along the edges of
strips, around a wire, in a port sheet. If the refined region is on the metal you care about, the
refinement did its work. If a feature you care about sits among large elements, the answer at that
feature is the coarse mesh's, and a *Standard* run (with refinement) is the one to trust. Palace also
saves its **error indicator** per element, which the view offers as a field: it shows the error
directly, where the mesh shows it only by implication. On an openEMS setup, the grid is the whole
accuracy story. A wire much thinner than the cells around it has too much inductance, and the run's
notes say so.

<div class="callout note">
<span class="label">Pictures of the 3D view</span>
<p>The 3D view draws on the graphics card, so its pictures cannot be made by the documentation's
headless generator. The four below are to be exported from the example with <b>Export picture …</b> at 2×,
with the legend on.</p>
</div>

<!-- FIGURE PLACEHOLDER em3d-view-bond-wire-model — 1600 x 1000 PNG, Export picture at 2x, legend on:
     Bond wire/em/Bond wire 3D.cem after a run, isometric (1), dielectrics and air hidden, mesh ON.
     Caption: "The bond wire after a Standard run: the wire, its feet and the pads, with the mesh Palace
     refined around them." -->

<!-- FIGURE PLACEHOLDER em3d-view-bond-wire-current — 1600 x 1000 PNG, Export picture at 2x, legend on:
     the same view, mesh off, Field = the saved 20.5 GHz frequency, J_s on surfaces, dB on.
     Caption: "Surface current J_s on the bond wire and its pads at 20.5 GHz, in dB." -->

<!-- FIGURE PLACEHOLDER em3d-view-via-field — 1600 x 1000 PNG, Export picture at 2x, legend on:
     Via through a plane/em/Via 3D.cem after a run, front view (3), clip plane C on y (normal along y) at
     0, Field = 10.05 GHz, |E| on the clip plane, dB on.
     Caption: "|E| at 10.05 GHz in a vertical cut along both lines and through the via, in dB." -->

<!-- FIGURE PLACEHOLDER em3d-view-package-mode — 1600 x 1000 PNG, Export picture at 2x, legend on:
     Package/em/Package lid modes.cem after a run, top view (2), clip plane on z just below the lid,
     Field = mode 3, |E| on the clip plane.
     Caption: "|E| of the lid's first cavity mode in a plane just below the lid." -->

## Walking through the example {#example}

**Tools ▸ Examples ▸ 3D EM** copies a workspace with three cells to a folder you choose and opens it.
Each cell is something a planar solver cannot do, or can do only as a comparison. Every setup runs in
minutes on a 16 GB laptop. Its `README.md` opens first and carries the same numbers as this section. The
numbers also live in `expected-numbers.json` beside it, which circuitRF's own tests re-run against, so the
two cannot drift apart.

**Every time and memory figure below was measured once**, on an Apple M4 with 10 cores and 16 GB,
running macOS 27.0, Palace 0.18.1 on 10 processes, and Gmsh 4.15.2. Times are wall clock for the whole
run. Memory is Palace's own peak over all its processes.

**Without Palace, start anyway.** Open any setup and press **Show 3D** to see the model. Press
**Simulate**, and the refusal offering **Install Palace …** is the expected first step.

### Bond wire {#example-bond-wire}

A 1 mil (25.4 µm) gold wire with a hexagonal section and a wedge foot at each end. It loops 150 µm high
between two 100 µm pads on 100 µm of alumina, inside a closed metal box 3 × 2 × 1.1 mm. A port sheet runs
from each pad's outer edge down to the ground.

{{ui: em3d-bond-wire-section}}

`Bond wire 3D.cem` runs Palace at **Standard**. It takes **179 s**, with a peak of **3.1 GB**, and gives
|S21| = **−0.765 dB** at 10 GHz and a series inductance of **906 pH** at 1 GHz. (The inductance is the
π-model's series term, −Im(1/Y21)/ω, from the S-parameters.)

- **Draft is not enough here.** It takes 5 s, but reads **813 pH** and **−0.514 dB**. The starting mesh
  around a 25 µm wire is exactly what refinement exists to fix. A wire is small metal in a big box, so
  it is the case where the preset matters most.
- **Accurate** (three refinement passes and tighter tolerances) took 8.5 min at 7.0 GB and reads 908 pH
  and −0.772 dB: within 2 pH and 0.01 dB of Standard, for nearly three times the time.
- **Kernel W**, the wire kernel in wBond, reads **704 pH** and **−0.680 dB** from the same `.wBond`
  (open it and **Export Touchstone …** with the *Distributed* model). The 202 pH between the two answers
  is **where the terminals are**, not a disagreement between the solvers. Kernel W's terminals are the
  wire's own ends. Palace's ports are at the pads' outer edges, so its answer also contains the pad between
  each port and the wire, and the 100 µm drop from the pad to ground.

### Via through a plane {#example-via}

A 400 µm microstrip on top of a four-layer board, a via through a 900 µm clearance in the middle ground
plane, and an inverted microstrip underneath leaving the other way. The laminate has εr 3.66 and tanδ
0.004, and the metal is copper. Both lines return through the same plane, one from above and one from
below.

{{ui: em3d-via-section}}

**First, the planar solver.** `Via planar.cem` is the same structure for the planar solver, and it is
refused:

> This EM setup names 'Ground Plane' as its return plane, but its top surface is at 270 µm, which is NOT below the lowest analysis level 'Bottom Copper' at 0 µm. A return plane must lie BENEATH the conductor it feeds, or there is no dielectric slab between them to solve on. Name a conductor below 0 µm, or restrict this setup's analysis levels to conductors above 'Ground Plane'.

`circuitrf check` reports the same sentence as an error on that setup. It is the only error in the
workspace, and it is there on purpose.

**Then Palace.** `Via 3D.cem` runs at **Draft** over 0.1–20 GHz, with 1.5 mm of air on every side of the
board and absorbing walls. It takes **62 s**, with a peak of **4.8 GB**, and gives |S21| = **−0.228 dB**
at 10 GHz and **−0.430 dB** at 20 GHz.

- **What Draft trades away is phase.** Against an independent hand-built Palace model of the same via
  (element order 2, 155,883 tetrahedra), Draft's |S21| is within 0.1 dB, but its phase is **12° off at
  10 GHz and 23° off at 20 GHz**.
- **Element order 2**, with Draft's other values (`"ElementOrder": 2` in the `Palace` section), takes
  **6.5 min** at **9.2 GB** and lands within 0.02 dB and 5° of that model.
- **Standard** took **35 min** at 9.3 GB on this structure, when the presets were measured. **Accurate**
  has never finished on it: it was stopped after 23 minutes in its third solve.

### Package {#example-package}

A ceramic package with its lid on. The 8 × 8 mm cavity has a 254 µm alumina base on a metal floor. Two
200 µm gold leads stop 100 µm short of the side walls, and a 1 mil bond wire runs from each lead to a die
pad inside. The lid and seal ring are the closed metal box around it: side walls 100 µm beyond the leads,
and a lid 700 µm above the wires, 1.13 mm above the floor. The die itself is not in the model.

{{ui: em3d-package-section}}

- **Capacitance** (`Package C.cem`, electrostatic, **Standard**): **8 s**, **1.1 GB**. Each lead with its
  wire and die pad has **586 fF** to ground, and the two leads couple through **0.74 fF**.
- **Inductance** (`Package L.cem`, magnetostatic, **Standard**): **22 s**, **1.8 GB**. Each path has
  **1.98 nH**, and the mutual inductance is **0.032 nH**. It runs on `Package shorted.clay`, a copy of
  the layout with a via from each die pad to the floor. Shorting the far end gives each terminal's
  current a loop, which is the ordinary way to read a series inductance. This is **external**
  inductance, the RF value: conductors carry current on their surfaces only.
- **Draft is not enough for either.** It runs in 2–3 s but reads **831 fF** (42 % high) and **1.66 nH**
  (16 % low). A static solve has no frequency to size its elements by, so its first mesh is coarse, and
  refinement is what finds the field around the metal.
- **The lid's modes** (`Package lid modes.cem`, eigenmode, the three modes above 5 GHz, at **Draft with
  element order 2**): **166 s**, **1.7 GB**. **Mode 3, at 23.65 GHz, is the lid's first cavity mode**,
  with 94 % of its energy in the air under the lid and a Q of **1,662**. That Q counts the gold's loss
  and the two ports' 50 Ω as loads. The estimate for a cavity much thinner than a wavelength, with the
  base and the air in series under the lid, puts the empty cavity at 23.7 GHz. **Modes 1 and 2, at
  14.3 GHz, are not the lid**: they are the leads ringing as lines into their ports, with a Q below 1.
  For a band that reaches 20 GHz, the lid is clear, and a cavity 20 % larger each way would bring its
  mode to about 19.7 GHz, inside the band.
- **The eigenmode preset.** *Draft* takes 13 s and finds the lid mode at 23.39 GHz, but reads its Q as
  176, ten times too low: order 1 cannot resolve the loss. Element order 2 fixes that without a
  refinement pass. *Standard* adds refinement passes to Palace's nonlinear eigenvalue solve, and was
  stopped after 28 minutes in its last pass.

**What the example does not show.** Every setup is Palace's. The via and the bond wire can also be run
with **FEM & FDTD - Compare**, to see how far openEMS lands from Palace on the same model. That is a
cross-check, not a reference.
