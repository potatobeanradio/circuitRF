---
title: 3D EM
slug: reference/em-3d.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > 3D EM
lede: What circuitRF's 3D solvers are for, how to get them, how to set one up and read the answer, and a worked example you can open.
keywords: 3D EM, full-wave, finite element, FEM, FDTD, Palace, openEMS, EMerge, Gmsh, bond wire, via, package, capacitance matrix, inductance matrix, eigenmode, cavity resonance, lid resonance, wave port, air box, mesh, fields, 3D view, install
---

circuitRF can hand a layout to a 3D solver: **Palace** or **EMerge**, two finite-element (FEM) solvers,
or **openEMS**, a finite-difference time-domain (FDTD) solver. It builds the 3D model itself, from the
layout, its technology and its bond wires. It meshes the model with **Gmsh**, runs the solver, and reads the answer
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
<li><a href="#solved">Is this solved?</a></li>
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
| Bond-wire arrays over one flat ground | wBond's equations or its kernel. A 3D run is the independent reference they are validated against |
| Bond wires over a stepped or split ground, in a cavity, or under a lid | **3D.** wBond's ground is one flat plane — see [wBond ▸ Equations, the MoM kernel, or FEM and FDTD](wbond.html#fidelity) |
| Leadframes, lids, cavities, stepped metal, connectors, package-to-board transitions | **3D.** Nothing planar represents them |
| A package's capacitance and inductance matrices | **3D (Palace).** Electrostatic and magnetostatic solves |
| Resonances of a cavity or a lid: "is there a mode inside my band?" | **3D (Palace or EMerge).** An eigenmode solve finds them directly |
| A radiator that is not planar | **3D.** See [Antennas ▸ From a 3D solver](antennas.html#3d) |

**Which 3D solver.** FEM (Palace, EMerge) and FDTD (openEMS) suit different geometry. The setup panel says which
fits your model when you choose one. This is advice, not a restriction. The full comparison of FEM,
FDTD and the planar MoM solver — cost, memory, and which problems suit each — is
[EM Solvers: MoM, FEM and FDTD](em-solvers.html).

- **FEM (Palace)** fits curved and diagonal metal: bond wires, round vias. Its tetrahedra follow a
  surface, and its adaptive refinement puts small elements only where the error is. It is the only one
  of the two that computes capacitance and inductance matrices, and it finds a cavity's resonances
  directly. FDTD would have to ring a high-Q cavity down for a long time.
- **FEM (EMerge)** fits the same geometry as Palace and finds resonances too. It installs in about two
  minutes with nothing to build and solves a small driven model in seconds, with lumped or wave ports. It
  is a second FEM answer that shares no code with Palace's.
- **FDTD (openEMS)** fits Manhattan geometry on a stackup, because metal aligned with its grid costs
  nothing. One pulse covers a whole band. Its absorbing boundaries let the air box around a radiator
  stay small. One fine feature sets its cell size, and through that its time step, so a single thin
  wire in a large model is expensive.

Running two or more on one setup (**Compare…**) gives answers from solvers that share no code, and FEM
and FDTD share nothing numerically either; their agreement is strong evidence that each has converged.
It is a cross-check, not a reference: every solver reads the same generated model, so a mistake in
building that model would appear in all of them.

## Getting the solvers {#getting}

circuitRF ships no solver and no mesher. Opening a workspace, editing a 3D setup and looking at its model
need none of them. **Simulate** is the first step that does. On a machine without them it is refused,
naming what is missing, and the message offers **Install Palace …** (or Gmsh, openEMS or EMerge). **Settings ▸
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
| EMerge, macOS on Apple silicon | About 2 min, 1.0 GB, from ready-built packages, with a Python of its own; its faster sparse solver adds a few seconds when the command line tools are installed ([Getting the most speed from EMerge](em-solvers.html#emerge-speed)) |

Palace is always built from source, because no ready-built Palace exists for macOS or for the Linux
subsystem. The build is long but runs in the background, with its progress in the Messages panel, and
**Cancel** stops it at any point.

<div class="callout note">
<span class="label">Palace's licence note</span>
<p>A default Palace build includes ParMETIS, whose licence allows commercial use for evaluation only. If
you build Palace, you accept those terms. circuitRF distributes no copy of Palace, so it passes on none.</p>
</div>

**On Windows**, Palace runs inside your own Windows Subsystem for Linux distribution, and circuitRF treats
it like a Palace on this computer. Gmsh, openEMS and EMerge run natively.
[Installing Palace on Windows](palace-windows.html) walks through it step by step, and
[EM Setup ▸ Palace on Windows](em-setup.html#palace-windows) has what each missing piece's message says.

**By hand.** If you prefer to build the solvers yourself, circuitRF finds them on `PATH`, in a Spack
install tree, in a conda environment, or where you name them in **Settings ▸ Solvers**. An EMerge of your
own is never taken from `PATH`: name its Python, or its virtual environment's folder, in that row or in
`CIRCUITRF_EMERGE`. It runs only the Palace versions it has validated; any Gmsh, openEMS or EMerge runs,
with a warning when it is not a version circuitRF has tested. The exact versions and the three build problems met on macOS are in
[EM Setup ▸ Installing the 3D solvers by hand](em-setup.html#install-3d-solvers).

**Removing them.** **Uninstall …** in **Settings ▸ Solvers**, or `circuitrf solver remove palace --yes`.
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
  Q. [Package RLC](em-setup.html#package-rlc), [Eigenmodes](em-setup.html#eigenmodes). When an
  electrostatic solve is the right way to get a capacitance, and how to put the answer in a schematic, is
  [AN-03 Extracting capacitance](../app-notes/an03-extracting-capacitance.html).
- **Ports.** A layout's port labels become ports. In 3D a port is **lumped** by default: a sheet from the
  line's end to its return, with the port's Z₀ across it. The return is found in the layout: the nearer of
  the first plane with metal under the end below the line and the first above it, a ground-reference layer
  preferred, and a port can state another in the port table's **Return** column
  ([Return](em-setup.html#port-return)). A **wave port** (`Ports3D`) is fed and measured
  in the line's own field at the air box's face instead, and has no sheet parasitic. Both solvers build
  one, except that openEMS cannot feed a hollow waveguide and Palace cannot give a port with several
  terminals. [Wave ports](em-setup.html#wave-ports).

  <a id="port-width"></a>**How wide a lumped port's sheet is.** As wide as the port's bar in the layout, which the
  port's **Use edge width** and **Width** set ([Ports](layout-editor.html#ports)): the whole conductor edge with the
  box ticked; otherwise the Width you state, or 2 × H (H, the dielectric height from the port's conductor to its
  return) centred on the port, cut short where the edge ends. The run's notes say which, and why. A sheet across a
  whole edge can reach metal the port was never near, such as a via on a pour's edge; when that refuses the run,
  the refusal names **Use edge width** as the way to narrow it.

  **A port on a curved edge.** A port on a round pad's arc has a flat sheet against a curved outline, which leaves
  slivers between the two that the mesher has to resolve. The run cuts the pad flat across the sheet's width, at
  the sheet, and its notes say how much metal that removed.

  An **internal** port, placed inside a pad rather than at a line's end, is a **pad-to-plane port**: a
  vertical lumped sheet from the pad to the nearest ground-reference plane, above or below, that has
  metal across the whole sheet. A plane with an opening under the pad is passed through, so a cut-out
  under a test pad moves the port's return to the next plane down, which is what the cut-out does to
  the real pad. The sheet is centred on the label and is as wide as the pad is across it. When a via
  stands at the label (a test point's through via), the sheet stands beside the barrel instead, on the
  side away from the trace that feeds the pad, and moves out to where the plane's clearance round the
  via ends. A port whose **Return** states a layer lands on that layer only. Metal of
  another layer across the sheet's path is refused, naming that layer. A delta-gap port (a cut across a
  line) is planar only.

  <a id="component-ports"></a>**Ports a component carries.** A port label inside a placed part is that
  part's terminal and never a port of the board — except a port the component declares as an **EM port**.
  A [TESTPAD](components.html#testpad) with `Centre = Port` is one: its pad centre becomes an internal
  port numbered `PortNum`, wherever the part is placed, turned or mirrored, and in 3D it is the pad-to-plane
  port above. A planar setup takes it as an internal port to ground. Its number is checked against the
  board's own port labels and every other component's: a number used twice is refused, naming both.

  <a id="part-ports"></a>**A port across each part's own pads** (`ComponentPorts`). To co-simulate a board with the
  parts placed on it, each part needs a port of the board's N-port at its own pads. A 3D setup of a
  [3D view](drawing-in-3d.html) that places the board's layout names them by reference designator — `["C1", "C2", "L2"]`,
  or `"*"` for every placed two-terminal part — in its **Component ports** list (tick the parts; **Check** shows what
  would be built) or with `em <view.c3d> --component-ports "C1,C2"` for one run. Each is a lumped **gap** port between
  the part's two pads: its ends at the closest copper facing the gap across the port's width — the pad, a trace's
  round cap, a zone's arc — and where that copper is not one straight face, a **landing** rectangle extends it to one,
  reaching back into solid copper across the whole width. So a port between pads that end in rounded or scalloped
  copper, as imported artwork often does, meets that copper along both of its edges rather than at a few points.
  Landings are part of the solved problem and are drawn in the 3D view as part of the copper they extend, so the
  view shows what is solved; the layout is not changed. Before anything is meshed, in `check` as in the run, each port is verified: no landing touches other
  copper (it would join two nets), each end of the sheet touches its own pad's copper and nothing else, its sides touch
  nothing, no copper lies inside it, and its two pads are on different nets. A part that fails is left out of the run
  with a warning naming it and what was found, and the other parts' ports are built; the run refuses only when no port
  is left. Port numbers follow the 3D view's own ports, in list order, without a gap; an entry may state its own `Z0`,
  its `Reference` (one of the part's pins) to say which pad is the negative end, and its `WidthUm`, the port's width
  across the gap in micrometres, centred where the pads put it (omitted: the width the two pads share). An entry beside
  `"*"` is that part's override. The `.sNp` a run writes starts its header with one line per port saying which number is
  which port, a component port by its part (`! Port[3] = C1`, `! Port[4] = U1.IN`). The ports are drawn in the 3D view and listed under **Component ports** in the Object
  Tree as soon as the setup names them, a part left out with why; selecting one shows its Z0 and width in the
  Properties Inspector, which writes them into the part's entry. Its place and orientation come from the pads, so they
  follow the layout. A part with more pads, such as an
  amplifier, gets one gap port per signal pad against its own ground pad (a pad on the ground net, or named GND); a
  part whose ground pads on top and bottom copper lie over each other is a coaxial launch, and its shell is modelled
  by a copper block through the substrate joining the two. `explain <view.c3d> --component-ports` lists each port's
  pads, conductors, gap, width, landings and verdict without meshing. A finished driven run also writes
  `<setup>_cosim.cnl` beside where the `_sim` cell goes: the board's S-parameter block, a `Port` for each of the 3D
  view's own ports and each connector, and each part's model from the board's schematic from its port's node to
  ground — a part with no model there is a placeholder value, named in a comment and by `check`. A 3D view of a
  cell's layout is made by **Design ▸ New 3D View from Layout**, or `new view3d <cell> --from-layout`.

  **Why a gap port, and not a pad-to-plane port at each pad.** A part between two pad-to-plane ports carries its
  current, as far as the EM is concerned, down one port's sheet, along the plane and up the other's: a loop that is
  not on the board. A gap port puts the part where it is, across its own pads, and the co-simulation connects it from
  that port's node to ground — never between two port nodes. A hand-drawn pair of pad-to-plane ports is a different,
  approximate model; its error grows with the pads' spacing against the substrate's height.

  <a id="port-numbers"></a>**Port numbers run 1 to N.** A Touchstone file numbers its ports by position, not by
  name, so a layout whose ports are 1, 3 and 4 would write P3 as the file's port 2, and a schematic that connects the
  file connects it by position. A run, planar or 3D, is therefore refused while the numbers skip one, and the refusal
  names what is missing (`Port numbers skip 2: the 3 ports are 1, 3, 4 …`); `check` and `explain` say the same.
  Renumber the ports 1 to N. A port outside the setup's solve region is refused too, by name.
- **Terminals** (`Terminals3D`, static solves): the matrix's rows and columns, each named by a net, an
  object's path or the end of one. An electrostatic terminal takes the whole piece of metal it names; a
  magnetostatic terminal also names the port its current enters by. Metal in no terminal floats, is
  ground or refuses the run, as **Other metal** (`UnassignedConductors3D`) says.
  [Static problems](em-setup.html#package-rlc).
- **The air box** (`AirBox`): how far each face of the solved region lies beyond the geometry, and what
  the face does to the field. It can be *Absorbing* (open space), *PEC* (a metal wall), *PMC* or
  *Symmetry*. A face's padding is a share of the geometry's extent along that face's own axis: 0 % touches
  the geometry, and 100 % on +Z puts one board-thickness of air above it.
  By default, on a driven setup, the top is padded by 100 % of the geometry's height, the four sides by 20 H, where H is
  the dielectric height under the top metal, and the floor by 10 H, where H is the dielectric height over the bottom
  metal. Each is rounded to the nearest 5 % of its axis. No default is more than an eighth of the longest wavelength or the structure's largest extent. On
  a 1.6 mm board whose top dielectric is 0.2 mm that is about 4 mm of air on the sides and 1.6 mm on top; state a
  larger +Z padding when the field above the board needs more room. The floor sits on the lowest ground plane, as a
  PEC face, when that plane is undrawn, or when it is drawn under all the other metal. A drawn bottom conductor that
  covers 95 % of the footprint is that floor even when the technology does not mark it as ground, and the notes say so. A hole in that plane is allowed
  when nothing of another net lies in or over it, so the drill holes of ground vias do not keep the floor off. A hole
  with a signal via or other metal of another net in it does, because a floor there would short that metal to
  ground. When a hole keeps the floor off, the notes say where it is. A setup that asks for a radiation pattern keeps every face absorbing, padded by a
  quarter wavelength at its **pattern frequency** (`PatternFrequencyGHz`, the sweep's lowest frequency when none is
  set). That is never more than the structure's largest extent, and never less than an eighth of a wavelength at the
  sweep's lowest frequency. A static or eigenmode setup keeps the wavelength rule. A closed box of PEC faces is a shielded
  enclosure, and it is how the example models a package lid. Each padding box in a 3D view's Properties panel
  shows the padding as a length, and each face row gives that length with the rule that set it (*30.6 mm — λ/4 at
  2.45 GHz*, *0.69 mm — 10 × H*). Type a length, or a number ending in % for a share of the extent. The tooltip and
  `circuitrf explain` give the rule in full
  (*default: 4.572 mm, 20 × H, H = 203.2 µm of dielectric under the top metal, to the nearest 5 %*).
- **Robust meshing** (`RobustMeshing`): removes outline points that sit within a fraction of a micrometre of
  their neighbours' line, which imported artwork often has and which can stop the mesher. On unless the setup says
  `false`; ports are inferred on the outline it leaves, by `check` and by the run alike. See
  [EM Setup ▸ Robust meshing](em-setup.html#robust-meshing).
- **Vias** are solid copper barrels in every EM problem, whatever their stackup entry's **Fill** says: a plated
  barrel's air core changes nothing an RF answer can see and makes the mesher build inverted elements. A thermal
  solve keeps the wall, which is where the heat runs. `circuitrf explain` names each entry that was built solid.
- **Mesh order.** A layout's geometry, whose only curved surfaces are its drilled barrels, is meshed with
  straight-sided second-order elements; on an imported board that took Gmsh 10 to 30 s where curving them ran for
  more than a quarter of an hour. Genuinely curved geometry keeps curved elements, for at most
  `MeshCurvingMinutes` (5 by default) before the mesh is made straight-sided, and the run says when it was.
- **Quality** (`Quality` in the `Palace` section): *Draft*, *Standard* (the default) or *Accurate*.
  These set the element order, the refinement passes and the sweep tolerance together, and any one of
  those can be overridden on its own. The measured cost of each preset is in
  [EM Setup ▸ Solver](em-setup.html#solver-3d). **Which one is enough depends on the structure, not on a
  rule**: the example below uses both Draft and Standard, and says why for each.
- **Fields** (`SaveFieldsGHz`): which frequencies' fields are saved for the 3D view. By default only the
  sweep's centre is saved, because fields are large.
  [Fields for the 3D view](em-setup.html#palace-fields).

**Leaving something out.** In a [3D view](drawing-in-3d.html#model), untick an object's, a placed cell's or a
port's **Model** to solve without it while keeping it drawn. A run leaves it out entirely — the air box too is sized
without it — and its notes name what was left out. A port that is off is **absent**, not terminated: no excitation,
no sheet, no lumped element, so the gap it spanned is open. The result's ports are the modelled ones **renumbered
1…N in their order** (P2 off: P1, P3, P4 become 1, 2, 3), and the `.sNp` header carries one
`circuitRF-EM 3D port map:` line per port, and the `.npy` a `DocumentPort` array, so the file says which is which. A
reference to something that is off — a modelled port's conductor, a wire's pad, a face boundary — is refused naming
both, never dropped; every port off is refused (*there is nothing to excite*).

**Look before you solve.** **Show 3D** on the setup's panel opens the 3D view on the model circuitRF
built. It works before any solver is installed, and a port that cannot be built does not empty it: the
board is drawn without that port, and the reason the setup would not run is shown across the top of the
view. `circuitrf explain` on the `.cem` prints the same model as
text: every solid, its material and its extent, the ports, the air box and its faces, and an estimate of
the problem's size. A `.c3d` that embeds several setups takes `--setup <name>` to say which one. `circuitrf render x.cem --section xz@y=0um -o cut.svg` draws a section through it.

## Watching it run, and reading the answer {#running}

**Before anything runs**, circuitRF checks whether the run fits in this machine's memory. The first check
uses the model's volumes and the refined shell round each conductor and port. A second check, once Gmsh has
reported how many tetrahedra it made, uses the real count. On a bond wire or another small curved conductor
the second is the one that counts, because the refinement its curvature asks for is nearly the whole mesh. Past 75 % of memory the run carries a warning naming the estimate and
what would shrink it. Past 150 %, Simulate asks before going on.

**While it runs**, the progress row names the solver's own stages, read from its own log: *Meshing
(Gmsh)* with Gmsh's own stage (curves, surfaces, 3D, the second-order elements, and *high-order optimisation* with
the node count it works on), and `gmsh.log` in the run folder fills as Gmsh runs; *Solving: refinement pass k of N* with the unknown count; *Adaptive sweep*, with the model change
— how much the frequency model still changes when a new frequency is solved — and the tolerance it is done below; *Reading results*. The memory the solver is using is beside it. A 3D
run cannot stop early and keep a partial result, so the button reads **Cancel**.

**When it finishes**, one line states what the run cost: the wall time, the solver's own peak memory,
the tetrahedra before and after refinement, the unknowns, and the preset. The same line is recorded in
the result's header.

**A driven Palace run also states the power each port loses**: driving that port with the others matched,
1 − Σᵢ|Sᵢⱼ|², at the sweep's lowest and highest frequency. This is power that comes out of no port: it went into
the dielectric and the metal, or out through an absorbing face. A lossless circuit model cannot show it, and |S21|
hides it behind mismatch. A port that loses more than three times the median port, and more than 1 %, is named.
If the loss is nearly the same at both ends of the band, it is unlikely to be dielectric loss or radiation, which
both grow with frequency. Look at what that port's field reaches.

**Where the answer lands.** A result is named after its solver, so a 3D run never replaces a planar one or
the other solver's:

| Problem | Result |
|---|---|
| Driven | `results/<setup>.palace.sNp` and `<setup>.palace_em.npy`, opened in the Data Display like any S-parameters |
| Electrostatic | `results/<setup>.palace_es.npy`: `C` (Maxwell) and `C_mutual` (the capacitors you would draw). Printed by `circuitrf em` |
| Magnetostatic | `results/<setup>.palace_ms.npy`: `L` and `L_mutual`. Printed by `circuitrf em` |
| Eigenmode | `results/<setup>.palace_eig.npy`: each mode's `f`, `Q`, and where its energy is. Printed by `circuitrf em` and shown on the panel |
| openEMS | `results/<setup>.openems.sNp` and `<setup>.openems_em.npy` |
| EMerge | `results/<setup>.emerge.sNp` and `<setup>.emerge_em.npy`; an eigenmode run `<setup>.emerge_eig.npy` |
| Compare | each solver's own result, as above, and `results/<setup>.compare_em.npy` holding each one's difference from the reference |

For a setup embedded in a `.c3d`, `<setup>` is the file's stem and the setup's name with a space between: the
setup `Driven` in `board.c3d` writes `results/board Driven.palace.s15p` for fifteen ports. `-o` moves the
Touchstone only. `run analysis=em` lists every file it wrote in its outputs, the Touchstone first, so the path
never has to be put together by hand.

`results/<setup>.palace/` keeps everything the run made: the Gmsh script, the mesh, the Palace
configuration, both programs' logs, and the saved fields. The run can be repeated by hand from it. An
unchanged model reuses its mesh. `results/<setup>.emerge/` is EMerge's: the model as data
(`problem.json`), the driver script that reads it, EMerge's log, the arrays it wrote and the saved fields,
in the layout a Palace run's fields use, so every field plot draws either. A static or eigenmode setup keeps
its own folder beside the driven one: `results/<setup>.palace_es/`, `_ms/` or `_eig/`.

**A port with two terminals on Palace** ([On Palace](em-setup.html#wave-port-terminals-palace)) also keeps
what its terminal S was converted from, per frequency, in the `palace` group of `<setup>.palace_em.npy`:
`modal_S` and `modal_V` (Palace's modal S and the voltage along each terminal's path, port by excitation),
`modal_Zpv` and `modal_kn` (each mode's impedance and wavenumber), and `modal_G` and `modal_K` (the fitted
overlap of a face's modes and its Robin correction). A disputed result can be re-derived from them without
running Palace again.

## Is this solved? {#solved}

A 3D run takes minutes to hours, so a 3D view (`.c3d`) says, for each of its setups, whether a run's result still
matches the model. You can tell without opening the fields, and the answer is still there after you close and reopen
the document or the workspace.

**What "solved" means.** A result exists, and nothing the run read has changed since. That covers the 3D view itself
(unsaved edits included) and every file the run was solved from: placed layouts and their sub-cells, nested 3D views,
the technology, and its material libraries. A circuit-driven thermal run also counts the schematic it ran. A run keeps
a copy of the model it solved beside its result, and the check compares contents, not an edit count.

**What makes a result out of date.** An edit to anything that setup's solver reads: geometry, materials, variables,
an object's **Model** switch (*Model: kept in the drawing, left out of the solve* in
[Drawing in 3D](drawing-in-3d.html#model)), and the setup itself. **Each setup is judged on its own**:

- **Another setup never counts.** Adding, editing or removing one setup leaves every other setup's result as it was.
  The exception is a thermal submodel, whose result is cut from its *From* setup's, so editing that setup counts for it.
- **An EM result** also counts ports, face boundaries and the air box's material. Heat sources, probes, contact
  resistances, effective blocks, symmetry planes and the wire ground plane are thermal only and never count. Mesh
  regions count for Palace but not for openEMS, which builds its own grid.
- **A thermal result** also counts heat sources, probes and the rest of the thermal places, and only the ports its
  currents are driven through (every port, when a current comes from a circuit). Adding an EM port, or editing one no
  thermal current uses, never counts. Nor do face boundaries or the air box's material.

**What never counts, for any setup:** hiding or showing an object, its transparency, the field plots, choosing the
active setup, the display unit and the snap grid. Those are how the model is drawn, not what is solved. **Undoing back
to the solved model makes the result current again.**

**The marks.** Each method has its own shape: a **diamond** for FEM (Palace or EMerge), a **triangle** for openEMS
(FDTD) and a **circle** for a thermal result; the mark's tooltip names the program. Each method shows at most one mark:

- **Filled, solid:** the active setup has a result of this solver, and it matches the model.
- **Hollow:** the active setup has a result of this solver, and the model has changed since. This wins even when
  another setup's result is current.
- **Faded, filled:** the active setup has no result of this solver, but another setup does, and it matches the model.
- **No mark:** nothing of this solver matches the model. An out-of-date result of a setup you are not using shows no
  mark. The setup list says it in words.
- **A star after a mark (`*`):** that result is partial. The run was cancelled, interrupted (circuitRF closed while it
  ran), or did not converge, such as openEMS stopping at its step limit. A partial result can still be current.

A Compare setup counts for each solver it runs. Shape and fill carry the meaning, so the marks read the same without
colour. Hover over a mark for its words: the setup, when it was solved and how long it took, or what has changed since.

{{ui: em3d-solve-badges}}

**Where it appears.**

- **The setup list** (*Simulate ▸ Setup Analyses…*, and the Analyses panel while a 3D view is active). Each setup's card
  has a status line: *Solved 14:32 (18 min)*, *Solved 14:32 (18 min), not converged*, *Out of date: 'Board.clay' has
  changed*, *Cancelled 14:32: no complete result*, *Not run*. A Compare setup has a line per solver. This is where to
  decide whether to run.
- **The document tab**, after the name. The unsaved-changes dot stays in front of the name.
- **The workspace tree**, after a `.c3d`'s name. An open document shows what is in the editor, unsaved edits included.
  A closed one shows what its file says.
- **A torn-off window's title**, when its active document is a 3D view, as words: `Connector.c3d — solved (FEM,
  thermal)`. Only filled, solid marks are named, with *partial* where it applies. The main window's title names the
  workspace and does not change.

The checks run in the background and never delay opening a document or a workspace. A mark appears once its check
finishes, a moment after the document is on screen. After an edit, the check waits for half a second of quiet. It
runs again when any run finishes and when a file a 3D view is solved from is saved.

**Before a re-run.** When the setup you run already has a complete, current result, Simulate asks first: *Palace result
for 'EM1' is current (solved 14:32, took 18 min). Run again?* A Both setup names both results. A partial or out-of-date
result runs with no question, because there is no complete result to lose.

**From the command line.** `circuitrf explain <view.c3d>` has a **Solved** section: one line per setup and solver with
its state, whether it is partial, when it was solved, how long it took and what has changed. `--json` carries it too.
`circuitrf find` lists each 3D view's setups with their state. `circuitrf em` never asks before a re-run; it notes on
stderr that the result was already current and runs. See [The command line](cli.html).

## The 3D view {#view}

**Show 3D** on a setup's panel opens the 3D view beside it, drawn by the graphics card. It shows the
model circuitRF built, and after a run it also shows what the solver made. It is the
[3D editor](drawing-in-3d.html)'s own view with nothing to edit — the same toolbar, tree, keys and menus, without
the drawing tools, the setups, Simulate, the unit and the snap step — and its status lines under the view (the
setup's refusals and notes, the mesh, the field) are always shown.

- **Model.** Every solid, coloured by its layer, with the dielectrics, the air and the air box's faces
  each switchable. The faces are coloured by boundary kind: grey metal, blue absorbing, orange PMC,
  violet symmetry. The **object tree** lists every solid, and a click in the view picks one, names it and
  brings its row into sight; a row's tick hides that solid in this view only. The tree is headed **Objects**
  and lists the objects **by material** (the
  default — objects with no material come first, in their own group) or **by type** (boxes, sheets,
  cylinders, …), chosen in its header. By material a row does not repeat the material its group is headed
  by; an object's construction order is its name's tooltip. The **air box** appears — drawn and listed — once
  there is a solid or a sheet to size it: with no setup yet it is the box a new setup would solve in. By
  material it is listed under the material that fills it, first under Boxes by type, and says which setup it
  belongs to. It is filled with **Air** unless you choose otherwise: select it and pick its **Material** in the
  Properties panel (**Vacuum** for free space, or any material of the technology). Its tick hides it — faces
  and outline — and it stays hidden until you show it again. The filter at the header's right hides tree rows by type or by
  material, and its icon changes while it hides anything; it never hides an object in the view.
- **Camera.** Drag to orbit; right-drag, middle-drag, Alt-drag or Shift-drag to pan; scroll or pinch to zoom. With no
  wheel and no middle button, the keyboard does all three: **Ctrl/Cmd+=** and **Ctrl/Cmd+−** zoom, the **arrow keys**
  pan and **Shift+arrow** orbits 15°. **Home**, **Ctrl/Cmd+0** and *View ▸ Zoom to Fit* fit what is shown, keeping the
  direction you are looking from. **1** is isometric, **2**–**7** are the six orthographic views, and **P** switches between
  perspective and orthographic. Double-click the axis indicator for the same views: an axis looks straight
  down it — **Z** the top view, **Y** the front, **X** the right — and again turns to the opposite side; anywhere
  else inside its ring is isometric. While you orbit or pan nothing is highlighted and nothing snaps.
  These are the 3D editor's keys too: every 3D pane uses the same ones.
- **Selecting.** **O**, **F**, **E** and **V** choose what a click selects: a whole **object**, one **face**, one
  **edge**, or one **vertex** (the toolbar has a button for each). What is under the cursor is highlighted. A click
  selects it; Shift-click adds to or removes from the selection; **Esc** clears it. A selected face's area
  and normal, an edge's name, kind and length, or a vertex's coordinates, are shown under the view, in the
  layout's unit. **B** steps to the
  next thing *behind* the selection along the line of sight through the cursor, and **Shift+B** steps back
  toward you — the status line says where you are (`Face zmin · "trace" · 2 of 5`). A face selected
  behind others is drawn through what is in front of it. Right-click opens the menu for what is selected:
  *Hide*, *Isolate*, *Show All*, and *Select Owning Object*. Nothing here changes the setup.
- **The 3D editor.** A 3D view (`.c3d`) opens in this same pane with drawing added: boxes, cylinders, sheets,
  faces pushed and pulled, placed cells, bond wires, ports, setups and the air box, all with these keys. It is
  circuitRF's own and needs no solver. [The 3D Editor](drawing-in-3d.html) is its chapter, and describes every
  control.
- **Measure** (**M**, the ruler button, *3D ▸ Measure*; in the editor and in this view). Click two points,
  snapped as drawing is; after the first a line follows the cursor. A card in the corner of the view gives
  both points' x, y and z, their differences (the second minus the first) and the distance, in the
  document's unit — changing the unit re-spells them. Every number can be selected and copied; hover one for
  its copy button, or **Copy All** for the whole card as a table that pastes into a spreadsheet. A copied
  value carries its unit (`12.5mil`) and pastes back into any dimension box as exactly the same length.
  **≈** marks a point that is not exactly on the database-unit grid. A third click starts a new
  measurement; **Esc** ends it. Measuring changes nothing and adds no undo step.
- **Clip plane** (**C**). A plane along an axis or the view direction, dragged through the model. It
  shows inside a package, under a lid, or through a via's clearance.
- **Mesh.** After a Palace run, the mesh Gmsh made: the boundary triangles, and the tetrahedra the clip
  plane cuts.
- **Grid.** For an openEMS setup, the FDTD grid on the clip plane and where it meets the metal. A cylindrical
  grid is drawn as it is: on a clip plane across its axis, a circle at each radius and a spoke at each angle; on a
  plane along it, the radii as lines along the axis; on the metal, where its circles, spokes and planes cross it.
- **Field plots.** After a run that saved them, a field is drawn by a **field plot**: a row of the Object
  Tree's **Field Plots** group, saved with the 3D view like any object, so a plot is still there when the
  file is closed and opened again. Add one with the **+** on the group's header (or right-click the group,
  *New Field Plot…*); it is drawn at once and its fields open in the Properties Inspector:
  - **Setup** — the setup whose run it reads. A new plot keeps the setup that was active when it was made,
    so switching setups never changes what an existing plot shows; *Active* follows whichever is active.
  - **Solution** — what the run saved: a frequency (driven), a mode (eigenmode), a terminal (static), a
    sweep point (thermal). The plot keeps the **value**, not its place in the list, so a later run that saves
    one more frequency never moves it to another. On Palace, a port with two terminals is solved one **mode**
    of its face at a time, not one terminal, so its choices read *5 GHz, Left mode 2 driven*: the field of the
    face's second mode launched, which is not terminal 2 driven alone. An openEMS run drives each terminal, and
    its choices name the port. The list ends with *Other frequency… (needs a re-run)*,
    which adds a frequency to the setup's *Save fields at* and moves the plot to it. Until the setup has run,
    the list holds one greyed row saying what will appear, such as *Terminals of the electrostatic setup,
    once it has run*.
  - **Quantity** — only those in the solver's files are offered: the electric field |E|, the surface
    current J_s on the conductors, and for a static solve the potential. Until the setup has run, the
    quantities its run will offer are listed greyed.
  - **On** — a **clip plane** (the plot's own axis and position, so two plots can cut in two places),
    **surfaces** (the solid selected in the tree, or the conductors for J_s), or **faces** (below). Beside
    a clip plane's position is a slider across the geometry: the cut is drawn as you drag it and kept, as one
    undo step, when you let go. The field is saved for the whole volume, so the plane can be moved at any
    time, before or after Simulate, without running again. While the plot is selected the plane is drawn
    in the 3D view as a translucent rectangle across the air box, labelled with its axis and position, so
    it can be placed before anything is solved; *Always show the plane* keeps it drawn when the plot is not
    selected. **Through…** puts the plane through an object: press it, then click the object (a pad, a
    via) in the 3D view, and the plane moves to the object's centre on the plot's axis. Esc cancels. The
    plane is the plot's own. The toolbar's section plane neither moves with it nor hides it, so cut the
    geometry wherever helps you see.
    **Section Through…** (the Field Plots menu, or the 3D view's menu once a run has fields) makes a plot on a
    plane no axis gives: click two points (an object's centre, a vertex and an edge snap, so a pad's centre
    and a via's axis are one click each) and the plot is cut on the vertical plane through both, across the
    whole height. The toolbar's clip plane is set to the same plane (its axis reads **Section**), keeping the
    side away from you, and the view turns to face it square on. One undo takes all three back. In
    Properties the plot shows its two points; **Use an Axis** returns it to an axis plane.
  - **dB** and a **range** percentile, which keep one singular edge from washing out the picture. A field
    strength in dB reads in **dBµV/m** (H and surface current in **dBµA/m**), the EMC convention; any other
    quantity reads in dB re 1 of its unit. A power or energy density (the Poynting vector, the energy
    densities) is 10·log₁₀ of the value, and a field strength is 20·log₁₀.
  - **Drive** — for a driven solution, two more rows set the drive the field is
    [shown at](#field-drive). *Drive power* is the power, in W, mW or dBm, set per plot and saved with it.
    *Referred to* chooses **Incident**, the power a source matched to the port's Z₀ makes available, or
    **Accepted**, the power that enters the port after reflection, with every other port terminated in its
    own Z₀. Neither row is shown for an eigenmode, static or thermal solution.

  **Up to four plots are drawn at once**: tick them in the tree, so two slices of one quantity at two
  positions, or two quantities on crossing planes, are on screen together. Ticking a fifth is refused, and
  the status line names the four drawn so you can untick one. A new plot made while four are drawn is
  added hidden; *Show all* ticks the first four in the list and says which stay hidden. Plots of the same
  quantity at the same solution, in the same dB and range percentile, share **one colour range**, taken
  over all of their triangles together, so their colours compare. Each such group has **one legend**,
  titled with every plot in it. The legends stack down the right of the view, all one width, and a legend
  that would run past the bottom is left off with "+N more" under the last one. Where two plots cover the
  same place, the one selected in the tree is drawn on top, and the value under the cursor is the front
  plot's, named. Each change is one undo step. Adding, editing or hiding a plot never marks a result stale.
  A plot whose data is missing — no run of its setup yet, a run that did not save its frequency, a
  quantity the run no longer offers — stays in the tree with a warning mark and draws nothing; its
  tooltip, and the Inspector, say which, and name what the run did save. It is never moved to the nearest
  frequency for you. In a setup's own 3D view (*Show 3D View* from a `.cem`) plots last for the session:
  that view has no document to keep them in.

  <a id="field-drive"></a>**What drive a field is shown at.** Each driven solution is the field of one port
  driven, with every other port terminated in its own Z₀. The solve is linear, so the drive only rescales
  what is shown: field strengths by the square root of the power ratio, power and energy densities by the
  ratio. Changing it never needs a re-run and never marks a result stale. The default is the power the
  solver's own field is at, **0.5 W incident**: Palace drives each port at unit power on peak phasors, which
  is 0.5 W time-averaged. Magnitudes are **peak**; the RMS value is 1/√2 of the peak. Under an automatic
  range the colours do not change with the drive, but the legend's numbers and the value under the cursor
  do, and the legend states the drive, the port and its Z₀. *Accepted* needs the run's record of the port's
  reflection at the plot's frequency; where the run kept none, the plot is refused and the Inspector says
  why, and *Incident* still works. openEMS fields are shown at the same drive as Palace's, so the two
  solvers' plots of one structure compare directly. An openEMS run that kept no record of its port draws
  in relative values, without a unit, and its legend says so. Driving several ports at once is not
  offered.

  **Without a window**, a `.c3d`'s clip-plane plot is drawn as the section it cuts:
  `circuitrf render cavity.c3d -o cut.png --field Field1` (add `--phase 90` for an instantaneous
  quantity), and `circuitrf render cavity.c3d --list-fields` lists the plots and whether each one's data
  is there. A surfaces or faces plot is drawn from a direction you name, since the window's camera is not
  saved with it: `--iso`, or `--view-dir top` (`front`, `right` … or `x,y,z`), as a `.png`. It reads the
  same run, solution and range the 3D view does, and refuses a plot whose data is missing with the same
  sentence. See [`render`](cli.md#render-field).
- **One face.** In the 3D editor, right-click a face, *Plot Field*, to add that face to the plot being drawn
  when it is a faces plot — the top of one trace, one face of a substrate — or to start a new faces plot with
  it; again to take it off, and several faces accumulate. A conductor has no inside to read, so its face
  shows the field in the material next to it (or J_s, when that is the quantity chosen). A **sheet** asks
  *Top side* or *Bottom side*: the field's normal part jumps across a sheet that carries charge, so the two
  sides are two different pictures. The quantity is never changed for you — a face that cannot show the
  chosen one says why under the view. On an openEMS run the face is read half a grid cell off the metal,
  where openEMS records the field.
- **Animation.** With the plot drawn selected, the play button at the foot of the Inspector sweeps the phase
  through one cycle and draws the instantaneous field Re{E·e^jφ}. That is the standing wave in a cavity, or
  the current running along a wire. The phase and its speed belong to the view, not the plot: they are not
  saved. There is one phase for every plot drawn, so two animated plots move in step, and moving φ under
  one plot moves the others. For two quantities at one frequency that shows their true relative phase.
  Two plots at **different** frequencies each run one cycle of their own per loop, which compares them
  phase for phase rather than in real time; their legends say so.
- **Pictures.** **Export picture …** saves a PNG of the view at a multiple of the window's size, with or
  without the legends (the same stack as the view's) and caption. Right-click in the view — on anything or nothing — for **Copy**, which puts
  the view on the clipboard at four times the window's size (the 3D editor's canvas offers it too).

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
with the legend on. <code>circuitrf render --field</code> draws a <code>.c3d</code>'s clip-plane plot
headlessly, as a section rather than the view's perspective; none of these examples carries such a plot
yet, and each placeholder says what its headless spelling would need.</p>
</div>

<!-- FIGURE PLACEHOLDER em3d-view-bond-wire-model — 1600 x 1000 PNG, Export picture at 2x, legend on:
     Bond wire/em/Bond wire 3D.cem after a run, isometric (1), dielectrics and air hidden, mesh ON.
     Caption: "The bond wire after a Standard run: the wire, its feet and the pads, with the mesh Palace
     refined around them."
     Headless: none — a perspective model with its mesh is the 3D view's own picture, not a field plot. -->

<!-- FIGURE PLACEHOLDER em3d-view-bond-wire-current — 1600 x 1000 PNG, Export picture at 2x, legend on:
     the same view, mesh off, a field plot at the saved 20.5 GHz frequency, J_s on surfaces, dB on.
     Caption: "Surface current J_s on the bond wire and its pads at 20.5 GHz, in dB."
     Headless: not yet — J_s on surfaces is a Surfaces plot, which `render --field` refuses until the iso
     picture's brief; and a .cem holds no field plots, so the wire would have to be drawn in a .c3d first. -->

<!-- FIGURE PLACEHOLDER em3d-view-via-field — 1600 x 1000 PNG, Export picture at 2x, legend on:
     Via through a plane/em/Via 3D.cem after a run, front view (3), clip plane C on y (normal along y) at
     0, a field plot at 10.05 GHz, |E| on the clip plane, dB on.
     Caption: "|E| at 10.05 GHz in a vertical cut along both lines and through the via, in dB."
     Headless, once the via is drawn in a .c3d carrying this plot (a .cem holds none) as a ClipPlane plot on y
     at 0, 10.05 GHz, |E|, dB on, named ViaCut:
       circuitrf render "Via through a plane/3d/Via.c3d" -o em3d-view-via-field.png --size 1600x1000 --field ViaCut -->

<!-- FIGURE PLACEHOLDER em3d-view-package-mode — 1600 x 1000 PNG, Export picture at 2x, legend on:
     Package/em/Package lid modes.cem after a run, top view (2), clip plane on z just below the lid,
     a field plot at mode 3, |E| on the clip plane.
     Caption: "|E| of the lid's first cavity mode in a plane just below the lid."
     Headless, once the package's .c3d carries this plot (a .cem holds none) as a ClipPlane plot on z just below
     the lid, mode 3, |E|, named LidMode:
       circuitrf render "Package/3d/Package.c3d" -o em3d-view-package-mode.png --size 1600x1000 --field LidMode -->

### How far the dielectric reaches {#dielectrics}

A stackup gives each dielectric a thickness and a material but no outline, so circuitRF decides how far
it reaches sideways. The answer depends on the solver, and for the 3D solvers it is part of what gets
solved.

- **Planar (MoM) setups.** The planar solver treats every dielectric as **laterally infinite**: a board
  that goes on forever under the metal. **Show 3D** on a planar setup has to draw the substrate some
  size, so it draws each dielectric to the **board outline**, or, if the layout has none, to the **box
  round what the layout draws**. That shape is **for viewing only**. The planar result is the same
  whatever the picture shows.
- **3D setups (a `.cem` run in Palace or openEMS).** The dielectric reaches the air box, so a board with
  no drawn edge reaches the absorbing boundary as though it went on forever. A layout that draws a board
  outline is cut to that outline instead.
- **A layout placed in a 3D view (a `.c3d`).** There is no air box around the layout, so every dielectric
  is **finite**, and its sideways shape **is part of the problem** Palace and openEMS solve. Every slab is
  cut to the layout's **board outline** (closed shapes on the technology's outline layer: the layer
  exported as `Edge.Cuts`, or a Gerber profile), or, with no outline, to **one rectangle round everything
  the layout draws**, the same for every slab. Nothing reaches past the artwork, and a placed layout
  brings no metal it does not draw: a ground plane that a planar setup takes from the stackup must be
  drawn. The Messages note for the run says which bound the slabs took.

**Draw a board outline whenever the board is bigger than its copper**, for example when a trace's
fringing field runs through substrate beside it, for radiation from the board edge, or for coupling
across it. How a placed layout comes into a 3D view is in
[Drawing in 3D ▸ A placed layout's dielectric](drawing-in-3d.html#layout-dielectric).

## Walking through the example {#example}

**File ▸ New ▸ Examples ▸ 3D EM** copies a workspace with three cells to a folder you choose and opens it.
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

A 1 mil (25.4 µm) gold wire with a hexagonal section and a wedge foot at each end, between two 100 µm pads
on 100 µm of alumina, inside a closed metal box 3 × 2 × 1.1 mm. Its loop is not symmetric: it rises steeply
from the left pad to 290 µm above the pads about a third of the way across, then falls in a long slope to
the right pad. A port sheet runs
from each pad's outer edge down to the ground.

{{ui: em3d-bond-wire-section}}

`Bond wire 3D.cem` runs Palace at **Standard**. (Measured on the wire's earlier, symmetric 150 µm loop; the Palace figures in this section have not been re-run for the loop above. Kernel W's have.) It takes **179 s**, with a peak of **3.1 GB**, and gives
|S21| = **−0.765 dB** at 10 GHz and a series inductance of **906 pH** at 1 GHz. (The inductance is the
π-model's series term, −Im(1/Y21)/ω, from the S-parameters.)

- **Draft is not enough here.** It takes 5 s, but reads **813 pH** and **−0.514 dB**. The starting mesh
  around a 25 µm wire is exactly what refinement exists to fix. A wire is small metal in a big box, so
  it is the case where the preset matters most.
- **Accurate** (three refinement passes and tighter tolerances) took 8.5 min at 7.0 GB and reads 908 pH
  and −0.772 dB: within 2 pH and 0.01 dB of Standard, for nearly three times the time.
- **Kernel W**, the wire kernel in wBond, reads **768 pH** and **−0.800 dB** from the same `.wBond`
  (open it and **Export Touchstone …** with the *Distributed* model). The difference between the two answers
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
