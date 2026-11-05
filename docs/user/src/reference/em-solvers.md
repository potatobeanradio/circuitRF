---
title: EM Solvers: MoM, FEM and FDTD
slug: reference/em-solvers.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > EM solvers
lede: circuitRF can solve a structure three ways — its own planar method of moments (MoM), the finite element method (FEM) of Palace or EMerge, and openEMS's finite-difference time-domain method (FDTD). What each one is, why there are several, which to use for what, and how to get the most speed from EMerge.
keywords: EM solver, electromagnetic solver, FEM, finite element, finite element method, FDTD, finite-difference time-domain, finite difference time domain, MoM, method of moments, planar, 2.5D, 3D, full-wave, full wave, Palace, openEMS, EMerge, Gmsh, mesh, tetrahedra, Yee grid, time step, CFL, eigenmode, cavity resonance, curved geometry, fillet, staircase, staircasing, fidelity, curved elements, connector, electrostatic, magnetostatic, capacitance matrix, inductance matrix, which solver, compare solvers, speed, memory, open source, sparse solver, GPU, cores, threads, adaptive sweep, command line tools
---

circuitRF can solve an electromagnetic problem in three different ways, and each is a different
method, not a faster or slower version of the same one:

- **MoM**, the **method of moments**: circuitRF's own planar solver, built into the application.
- **FEM**, the **finite element method**: **Palace** or **EMerge**, two separate open-source programs
  that circuitRF runs.
- **FDTD**, the **finite-difference time-domain** method: **openEMS**, another separate open-source
  program that circuitRF runs.

They are chosen in one place, the **Solver** group of an EM setup: *Planar* (MoM), **FEM 3D (Palace)**,
**FDTD 3D (openEMS)**, **FEM 3D (EMerge)**, or **Compare…**, which runs two or more of them on one model.
The setup, the layout and the result are the same kind of document whichever solves it. This page
explains the three methods, the three programs that solve in 3D, and which problems each one is good
at and bad at. How to set each one up is in [EM Setup](em-setup.html#solver-3d),
[The MoM engine](mom-engine.html) and [3D EM](em-3d.html).

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#short">The short answer</a></li>
<li><a href="#methods">Three ways to discretise Maxwell's equations</a>
  <ul><li><a href="#mom">MoM: unknowns on the metal only</a></li>
      <li><a href="#fem">FEM: a tetrahedral mesh of the volume</a></li>
      <li><a href="#fdtd">FDTD: a grid, stepped through time</a></li></ul></li>
<li><a href="#programs">Palace, openEMS and EMerge: the three programs</a></li>
<li><a href="#compare">Side by side</a></li>
<li><a href="#speed-memory">Is it speed? Is it memory?</a></li>
<li><a href="#emerge-speed">Getting the most speed from EMerge</a></li>
<li><a href="#choosing">Choosing, by problem</a></li>
<li><a href="#curved">What each solver sees of curved geometry</a></li>
<li><a href="#why-three">Why not just one?</a></li>
</ol>
</nav>

## The short answer {#short}

**Use the planar MoM solver whenever the structure is planar metal on a layered board or die.** That
covers most layout work. It is the fastest of the three and needs nothing installed. Go to 3D only for
what it cannot represent.

**Use FEM (Palace)** for curved or small metal in a 3D volume: bond wires, round vias, packages and
lids. Use it too when you need something only FEM gives you: a cavity's resonances (eigenmode), or a
capacitance or inductance matrix (electrostatic and magnetostatic).

**Use FEM (EMerge)** for the same kind of problem when you want an FEM answer quickly, or a second
FEM answer beside Palace's. It installs in about two minutes with nothing to build, and on the shipped
cavity its driven sweep takes seconds. It solves driven and eigenmode problems with lumped ports, and
driven problems with wave ports: one conductor on the face (a microstrip end, a coax), or several
terminals on one face, a stripline pair included. A hollow waveguide needs Palace, and so do
capacitance and inductance matrices.

**Use FDTD (openEMS)** for Manhattan metal on a stackup over a wide band, and for radiators whose air
box you want to keep small. It needs far less memory than FEM.

**Compare** (*Compare…*) when an answer matters and you want a second opinion. FEM and FDTD share no
numerics, so when they agree, each has probably converged. Palace and EMerge share the method but no
code, so their agreement checks the solving, and a third answer says which of two that disagree to
doubt.

## Three ways to discretise Maxwell's equations {#methods}

Every EM solver replaces the continuous field with a finite number of unknowns and solves for those.
The three methods differ in **where the unknowns are** and **whether they solve one frequency at a time
or step through time**. Almost every practical difference between them follows from those two choices.

<svg viewBox="0 0 650 205" role="img" aria-label="The same microstrip cross-section discretised three ways: MoM cells on the strip only; FEM triangles filling a box, finer near the strip; FDTD a graded rectilinear grid" style="width:100%;height:auto">
  <rect x="15" y="125" width="190" height="45" fill="var(--surface)" stroke="none"/>
  <line x1="15" y1="170" x2="205" y2="170" stroke="var(--text)" stroke-width="3"/>
  <rect x="90" y="121" width="40" height="4" fill="var(--accent)"/>
  <path d="M95 121V125 M100 121V125 M105 121V125 M110 121V125 M115 121V125 M120 121V125 M125 121V125" stroke="var(--surface)" stroke-width="0.8"/>
  <text x="110" y="80" text-anchor="middle" font-size="11" fill="var(--muted)" font-family="IBM Plex Sans">no mesh in the</text>
  <text x="110" y="94" text-anchor="middle" font-size="11" fill="var(--muted)" font-family="IBM Plex Sans">air or the substrate</text>
  <text x="110" y="24" text-anchor="middle" font-size="12.5" font-weight="600" fill="var(--text)" font-family="IBM Plex Sans">MoM — cells on the metal only</text>
  <text x="110" y="192" text-anchor="middle" font-size="11" fill="var(--muted)" font-family="IBM Plex Sans">layers are infinite; ground is exact</text>
  <rect x="230" y="40" width="180" height="130" fill="none" stroke="var(--border)" stroke-width="1.2"/>
  <rect x="230" y="125" width="180" height="45" fill="var(--surface)" stroke="none"/>
  <path d="M230 40L251.0 62.0 M251.0 62.0L230 86.0 M230 86.0L251.0 102.4 M251.0 102.4L230 112.6 M230 112.6L251.0 119 M251.0 119L230 123 M230 123L251.0 127 M251.0 127L230 133.4 M230 133.4L251.0 143.6 M251.0 143.6L230 160.0 M230 160.0L251.0 170 M275.0 40L251.0 62.0 M251.0 62.0L275.0 86.0 M275.0 86.0L251.0 102.4 M251.0 102.4L275.0 112.6 M275.0 112.6L251.0 119 M251.0 119L275.0 123 M275.0 123L251.0 127 M251.0 127L275.0 133.4 M275.0 133.4L251.0 143.6 M251.0 143.6L275.0 160.0 M275.0 160.0L251.0 170 M275.0 40L290.4 62.0 M290.4 62.0L275.0 86.0 M275.0 86.0L290.4 102.4 M290.4 102.4L275.0 112.6 M275.0 112.6L290.4 119 M290.4 119L275.0 123 M275.0 123L290.4 127 M290.4 127L275.0 133.4 M275.0 133.4L290.4 143.6 M290.4 143.6L275.0 160.0 M275.0 160.0L290.4 170 M300 40L290.4 62.0 M290.4 62.0L300 86.0 M300 86.0L290.4 102.4 M290.4 102.4L300 112.6 M300 112.6L290.4 119 M290.4 119L300 123 M300 123L290.4 127 M290.4 127L300 133.4 M300 133.4L290.4 143.6 M290.4 143.6L300 160.0 M300 160.0L290.4 170 M300 40L306 62.0 M306 62.0L300 86.0 M300 86.0L306 102.4 M306 102.4L300 112.6 M300 112.6L306 119 M306 119L300 123 M300 123L306 127 M306 127L300 133.4 M300 133.4L306 143.6 M306 143.6L300 160.0 M300 160.0L306 170 M312 40L306 62.0 M306 62.0L312 86.0 M312 86.0L306 102.4 M306 102.4L312 112.6 M312 112.6L306 119 M306 119L312 123 M312 123L306 127 M306 127L312 133.4 M312 133.4L306 143.6 M306 143.6L312 160.0 M312 160.0L306 170 M312 40L318 62.0 M318 62.0L312 86.0 M312 86.0L318 102.4 M318 102.4L312 112.6 M312 112.6L318 119 M318 119L312 123 M312 123L318 127 M318 127L312 133.4 M312 133.4L318 143.6 M318 143.6L312 160.0 M312 160.0L318 170 M324 40L318 62.0 M318 62.0L324 86.0 M324 86.0L318 102.4 M318 102.4L324 112.6 M324 112.6L318 119 M318 119L324 123 M324 123L318 127 M318 127L324 133.4 M324 133.4L318 143.6 M318 143.6L324 160.0 M324 160.0L318 170 M324 40L330 62.0 M330 62.0L324 86.0 M324 86.0L330 102.4 M330 102.4L324 112.6 M324 112.6L330 119 M330 119L324 123 M324 123L330 127 M330 127L324 133.4 M324 133.4L330 143.6 M330 143.6L324 160.0 M324 160.0L330 170 M336 40L330 62.0 M330 62.0L336 86.0 M336 86.0L330 102.4 M330 102.4L336 112.6 M336 112.6L330 119 M330 119L336 123 M336 123L330 127 M330 127L336 133.4 M336 133.4L330 143.6 M330 143.6L336 160.0 M336 160.0L330 170 M336 40L349.6 62.0 M349.6 62.0L336 86.0 M336 86.0L349.6 102.4 M349.6 102.4L336 112.6 M336 112.6L349.6 119 M349.6 119L336 123 M336 123L349.6 127 M349.6 127L336 133.4 M336 133.4L349.6 143.6 M349.6 143.6L336 160.0 M336 160.0L349.6 170 M365.0 40L349.6 62.0 M349.6 62.0L365.0 86.0 M365.0 86.0L349.6 102.4 M349.6 102.4L365.0 112.6 M365.0 112.6L349.6 119 M349.6 119L365.0 123 M365.0 123L349.6 127 M349.6 127L365.0 133.4 M365.0 133.4L349.6 143.6 M349.6 143.6L365.0 160.0 M365.0 160.0L349.6 170 M365.0 40L389.0 62.0 M389.0 62.0L365.0 86.0 M365.0 86.0L389.0 102.4 M389.0 102.4L365.0 112.6 M365.0 112.6L389.0 119 M389.0 119L365.0 123 M365.0 123L389.0 127 M389.0 127L365.0 133.4 M365.0 133.4L389.0 143.6 M389.0 143.6L365.0 160.0 M365.0 160.0L389.0 170 M410 40L389.0 62.0 M389.0 62.0L410 86.0 M410 86.0L389.0 102.4 M389.0 102.4L410 112.6 M410 112.6L389.0 119 M389.0 119L410 123 M410 123L389.0 127 M389.0 127L410 133.4 M410 133.4L389.0 143.6 M389.0 143.6L410 160.0 M410 160.0L389.0 170 M251.0 40V170 M275.0 40V170 M290.4 40V170 M300 40V170 M306 40V170 M312 40V170 M318 40V170 M324 40V170 M330 40V170 M336 40V170 M349.6 40V170 M365.0 40V170 M389.0 40V170 M230 62.0H410 M230 86.0H410 M230 102.4H410 M230 112.6H410 M230 119H410 M230 123H410 M230 127H410 M230 133.4H410 M230 143.6H410 M230 160.0H410" fill="none" stroke="var(--muted)" stroke-width="0.5"/>
  <line x1="230" y1="170" x2="410" y2="170" stroke="var(--text)" stroke-width="3"/>
  <rect x="300" y="121" width="40" height="4" fill="var(--accent)"/>
  <text x="320" y="24" text-anchor="middle" font-size="12.5" font-weight="600" fill="var(--text)" font-family="IBM Plex Sans">FEM — elements fill the volume</text>
  <text x="320" y="192" text-anchor="middle" font-size="11" fill="var(--muted)" font-family="IBM Plex Sans">smaller elements where the field changes</text>
  <rect x="440" y="40" width="180" height="130" fill="none" stroke="var(--border)" stroke-width="1.2"/>
  <rect x="440" y="125" width="180" height="45" fill="var(--surface)" stroke="none"/>
  <path d="M461.0 40V170 M485.0 40V170 M500.4 40V170 M510 40V170 M516 40V170 M522 40V170 M528 40V170 M534 40V170 M540 40V170 M546 40V170 M559.6 40V170 M575.0 40V170 M599.0 40V170 M440 62.0H620 M440 86.0H620 M440 102.4H620 M440 112.6H620 M440 119H620 M440 123H620 M440 127H620 M440 133.4H620 M440 143.6H620 M440 160.0H620" fill="none" stroke="var(--muted)" stroke-width="0.5"/>
  <line x1="440" y1="170" x2="620" y2="170" stroke="var(--text)" stroke-width="3"/>
  <rect x="510" y="121" width="40" height="4" fill="var(--accent)"/>
  <text x="530" y="24" text-anchor="middle" font-size="12.5" font-weight="600" fill="var(--text)" font-family="IBM Plex Sans">FDTD — a rectilinear grid</text>
  <text x="530" y="192" text-anchor="middle" font-size="11" fill="var(--muted)" font-family="IBM Plex Sans">time steps set by the smallest cell</text>
</svg>

### MoM: unknowns on the metal only {#mom}

The **method of moments** puts its unknowns on the **conductors only**: the current on each small cell
of metal. The air, the substrate and the ground plane are not meshed at all. They are built into the
**Green's function**, the formula for the field one piece of current makes everywhere else. circuitRF's
Green's function is for a **stack of laterally infinite layers over an infinite ground plane**, which is
what makes the method planar (often called 2.5D).

- **Every cell talks to every other cell**, so the matrix is **dense**: N unknowns cost N² numbers.
  Small N is the whole point: a microstrip bend needs a few hundred unknowns, where a volume mesh of
  the same bend needs hundreds of thousands.
- It solves **one frequency at a time**, and circuitRF's adaptive sampling picks which frequencies to
  solve.
- **Radiation into open space and the infinite substrate are exact**, with no box and no absorbing
  wall to place.
- It **cannot** represent anything the layer stack cannot: a board edge, a lid, a cavity wall, a wire
  arcing through air, a signal via passing through a reference plane, or a dielectric that stops
  somewhere. [The MoM engine ▸ Cannot](mom-engine.html#can-cannot) is the full list. circuitRF refuses
  such a structure by name rather than solving the wrong one.

### FEM: a tetrahedral mesh of the volume {#fem}

The **finite element method** fills the whole solved region — dielectrics, air, the space inside a
package — with **tetrahedra**, and solves for the field on each one. Metal is a boundary of that volume.
circuitRF builds the 3D model, **Gmsh** meshes it, and **Palace** or **EMerge** solves it.

- **The mesh follows the geometry.** Tetrahedra fit a round wire, a slanted face or a curved via
  barrel. There is no staircase.
- **Each element talks only to its neighbours**, so the matrix is **sparse**. It is also far larger
  than a MoM matrix: hundreds of thousands to a million or more unknowns is normal.
- **Adaptive refinement.** After a solve, Palace estimates where the error is and subdivides only
  those elements, then solves again. Small elements end up where the answer needs them — around a
  wire, along a strip's edge — without anyone placing them by hand. EMerge can refine its mesh the same
  way (its *Accurate* preset does); its other presets solve a finer starting mesh instead.
- It works **in the frequency domain**. A driven run's adaptive sweep solves a few frequencies in full
  and builds the rest of the band from them.
- **The same mesh answers other questions.** An **eigenmode** solve finds a structure's resonant
  frequencies and Q directly (Palace and EMerge). **Electrostatic** and **magnetostatic** solves give
  capacitance and inductance matrices (Palace). Neither of the other two methods offers these in
  circuitRF.
- The volume must end somewhere, so the model sits in an **air box** whose faces are metal walls,
  symmetry planes or absorbing boundaries. Palace's and EMerge's absorbing boundaries are first order,
  so an open structure needs the box kept well away from it.

### FDTD: a grid, stepped through time {#fdtd}

The **finite-difference time-domain** method divides the region into a **rectilinear grid** of
rectangular cells and **steps the fields forward in time**: an electric-field update, then a
magnetic-field update, over and over. circuitRF builds the model and writes the grid itself; **openEMS**
runs the time stepping.

- **One run covers a whole band.** A short pulse contains every frequency in the sweep. After it has
  died away, the port voltages and currents are transformed into S-parameters at every frequency at
  once.
- **Memory is small.** A cell stores six field values and a few coefficients. There is no matrix to
  hold and nothing to factor.
- **The smallest cell sets the time step.** The **CFL condition** (Courant–Friedrichs–Lewy) says a wave
  may not cross a cell in one time step, so one fine feature shrinks the time step of the entire model.
  A 25 µm wire in a 3 mm package means a small time step everywhere, and many more steps to cover the
  same time.
- **Metal aligned with the grid costs nothing; anything else is a staircase.** A diagonal edge or a
  round wire is approximated by grid cells, and matching a curved surface accurately takes a fine grid.
- **It has to wait for the energy to leave.** A run ends when the signals have died away. A lossy or
  well-matched structure rings down quickly. A **high-Q** cavity rings for a very long time, which is
  why a cavity's resonances are FEM's job, not FDTD's.
- **Absorbing boundaries are good.** openEMS's PML (perfectly matched layer) soaks up an outgoing wave
  over a few cells, so a radiator's air box can stay small.
- openEMS drives **one port per run**, so an N-port setup is N runs, one after another.
- **The solvers build a wave port differently.** Palace and EMerge compute the line's mode on the air
  box's face and project the field onto it. openEMS computes no mode: it feeds the line from a short
  extension behind the face and measures its voltage and current with probes around the reference plane.
  [Wave ports](em-setup.html#wave-ports).

## Palace, openEMS and EMerge: the three programs {#programs}

circuitRF **ships none of the 3D solvers and contains no code from any of them.** It runs them as
separate programs you install. The install assistant can do that for you (see
[3D EM ▸ Getting the solvers](em-3d.html#getting)), and so can `circuitrf solver install palace`
(or `openems`, `emerge`). Opening, editing and looking at a 3D model needs none of them. Only
**Simulate** does.

| | Palace | openEMS | EMerge |
|---|---|---|---|
| Method | FEM, frequency domain | FDTD | FEM, frequency domain |
| Source | [Palace on GitHub](https://github.com/awslabs/palace) | [openEMS on GitHub](https://github.com/thliebig/openEMS), and its site [openems.de](https://openems.de) | [EMerge on GitHub](https://github.com/FennisRobert/EMerge), published as a Python package |
| Who maintains it | Its development team, in the open on GitHub | Its author and contributors | Its author and contributors, in the open on GitHub |
| Licence | Apache 2.0 | GPL v3 | GPL v2 or later |
| Mesh | Tetrahedra, made by [Gmsh](https://gmsh.info) from circuitRF's model | A rectilinear grid, written by circuitRF | Tetrahedra, made by EMerge's own copy of Gmsh from the same model |
| Parallel | Across processes (MPI): one per physical core by default | Across the cores of one machine | Its sparse solver's threads, on the cores of one machine; a GPU on an arm64 Mac. An eigenmode solve uses one core |
| What circuitRF runs on it | Driven S-parameters, eigenmode, electrostatic, magnetostatic; lumped and wave ports, including two terminals on one face when their modes travel at different speeds (a microstrip pair, not a stripline); radiation pattern | Driven S-parameters; lumped and wave ports, including several terminals on one face ([the 3D Wave Ports example](../new-user-guide/index.html#wave-ports-3d)); radiation pattern | Driven S-parameters and eigenmode; lumped and wave ports, including several terminals on one face (a stripline pair too); radiation pattern |
| 0 Hz point | circuitRF conduction solve ([the DC point](em-setup.html#solver-3d)) | circuitRF conduction solve ([the DC point](em-setup.html#solver-3d)) | circuitRF conduction solve ([the DC point](em-setup.html#solver-3d)) |
| Install, as measured on one Apple M4 | Built from source: 52 min, 1.9 GB | 8 min, 0.27 GB | Ready-built packages: about 2 min, 1.0 GB, with a Python of its own |
| On Windows | Inside your Windows Subsystem for Linux (WSL 2). Palace's GitHub page covers Linux and macOS only; see [Installing Palace on Windows](palace-windows.html) | Native | Native, with an x64 Python, which an arm64 Windows computer runs under its x64 emulation. Not yet measured |

**Palace** is a parallel finite-element code for full-wave 3D electromagnetics. It also has a
time-domain solver, which circuitRF does not use. **openEMS** is an FDTD solver. It is written for use
with its own geometry library, and circuitRF writes that library's model file directly. **EMerge** is a
finite-element solver for the time-harmonic wave equation, written in Python on compiled numerical
libraries. It has no program of its own to start, so circuitRF writes the model as a data file and
starts a small driver script of its own in EMerge's Python, which builds the model through EMerge,
solves it and writes the answer back as plain arrays. **Gmsh** is the open-source mesh generator that
turns circuitRF's 3D model into Palace's tetrahedra, and EMerge's.

What circuitRF adds around every program is the same: it **builds the 3D model** from your layout, its
technology and bond wires (or from a drawn [3D view](drawing-in-3d.html)), checks that the run fits in
memory, runs the solver, follows its progress from its log, and reads the answer back as an ordinary
result. [EM Setup](em-setup.html#solver-3d) has every setting.

<div class="callout note">
<span class="label">What FDTD cannot say that FEM can</span>
<p>Two material limits of openEMS appear in every openEMS run's notes. A loss tangent is exact at one
frequency only, because openEMS holds a material's conductivity constant. And solid metal is a perfect
conductor, because a grid cannot resolve a skin depth; thin metal drawn as a sheet keeps its loss. On a
lossy substrate these are the largest expected differences between the two solvers. See
<a href="em-setup.html#openems-run">EM Setup ▸ Running openEMS</a>.</p>
</div>

## Side by side {#compare}

| | **MoM** (planar, built in) | **FEM** (Palace) | **FEM** (EMerge) | **FDTD** (openEMS) |
|---|---|---|---|---|
| Unknowns live on | The metal surfaces | Every tetrahedron of the volume | Every tetrahedron of the volume | Every grid cell of the volume |
| Matrix | Dense, N² | Sparse, very large | Sparse, very large | None: explicit time stepping |
| Frequency | One frequency per solve; adaptive sampling | Frequency domain; adaptive sweep | Frequency domain; adaptive sweep | Time domain: one pulse covers the band |
| Geometry it fits | Planar metal and vias in a layered stack | Anything: curved, slanted, small metal in a large volume | The same as Palace | Manhattan metal; curves are staircased |
| What sets the cost | The metal area, and how finely it is cut | The volume, the highest frequency, and how much refinement the small features need | The volume, the highest frequency, and the element size along metal edges | The number of cells × the number of time steps; the smallest cell sets the time step |
| Memory | 84 MB at 2,000 unknowns, growing as N² | The largest: gigabytes | Gigabytes: 1.6 GB at 54,000 unknowns, about 24 kB more per unknown | Small: tens to hundreds of megabytes |
| Open space, radiation | Exact, with no box | An air box with first-order absorbing faces, kept well away | The same as Palace | An air box with PML, which can stay close |
| Enclosures, lids, cavities | Not representable | Yes, and eigenmodes find their resonances | Yes, and eigenmodes find their resonances | Yes, but a high-Q cavity rings for a long time |
| Capacitance and inductance matrices | No | Yes (electrostatic, magnetostatic) | No | No |
| Ports | Edge ports (de-embedded), internal delta-gap ports and internal ports ([the three types](mom-engine.html#ports)) | Lumped and wave ports, up to two terminals on one face ([its rules](em-setup.html#wave-port-terminals-palace)) | Lumped and wave ports, up to nine terminals on one face ([its rules](em-setup.html#wave-port-terminals-emerge)) | Lumped and wave ports, any number of terminals on one face ([its rules](em-setup.html#wave-port-terminals)) |
| Metal loss | Yes | Yes (conductivity on every metal surface, [corrected for thin metal](em-setup.html#thin-metal-3d)) | Yes (a surface impedance on every metal surface, [corrected for thin metal](em-setup.html#thin-metal-3d)) | Sheets only; solid metal is lossless |
| Dielectric loss | Yes, tanδ at every frequency | Yes, tanδ at every frequency | Yes, tanδ at every frequency | Exact at the band centre only |
| Installed | Always | You install it | You install it | You install it |

## Is it speed? Is it memory? {#speed-memory}

**Both, and which one binds depends on the problem.** These are the figures circuitRF has measured.
Each is one machine's measurement — an Apple M4 with 10 cores and 16 GB — and yours will differ.

**MoM is fastest where it applies.** At 2,000 unknowns a frequency point solves in under a second and
holds 84 MB. At 5,000 it holds 527 MB, and that is the dense solver's ceiling: a larger mesh is refused
before it runs. The optional accelerated solve raises the ceiling to 12,000 on a single metal level
with no vias, using about four times less memory. Its cost grows fastest with **metal area and mesh
density**, not with the size of the box around the metal, because there is no box. See
[The MoM engine ▸ What makes a run infeasible](mom-engine.html#budget).

**FEM uses the most memory, and the memory is usually what limits it.** The shipped example's runs:

| Palace run (from [3D EM ▸ Walking through the example](em-3d.html#example)) | Wall time | Peak memory |
|---|---|---|
| A package's capacitance matrix, Standard | 8 s | 1.1 GB |
| A package's inductance matrix, Standard | 22 s | 1.8 GB |
| A via through a ground plane, 0.1–20 GHz, Draft | 62 s | 4.8 GB |
| One bond wire, Standard | 179 s | 3.1 GB |
| A package lid's resonances, eigenmode | 166 s | 1.7 GB |
| The same via, Standard | 35 min | 9.3 GB |

While the solvers were being chosen, hand-built Palace models found the practical limit of a 16 GB
machine at about **1.2 million second-order unknowns**. circuitRF checks a run's memory twice before it
solves: once from the model's volumes, and again from the element count Gmsh reports.

**FDTD uses far less memory than FEM, and the time step is usually what limits it.** In the same
solver-choice measurements, hand-built openEMS models of one bond wire reached Palace's answer to
within 0.1 dB at 10 GHz with **400,000 cells in 13 s and 67 MB**, where the Palace run of the same wire
took 108.5 s and 7.7 GB. That grid had cells of **half the wire's radius** around the wire, and at that
size the wire set the time step for the whole model. The same wire on a coarser grid read 26 % too much
inductance. A package with many wires is therefore FDTD's worst case: every wire needs a fine grid
around it, and the finest cell anywhere slows every step.

**EMerge's driven sweeps are fast, and its memory is set by its sparse solver.** The 3D Eigenmode example's
runs (8–12 GHz, 801 points) and its calibration, on the same machine:

| EMerge run (from [the 3D Eigenmode example](em-setup.html#eigenmodes)) | Wall time | Peak memory |
|---|---|---|
| A cavity, driven, Draft: 53,614 unknowns, 10 full solves | 3 s | 1.6 GB |
| The same cavity, driven, Standard: 109,566 unknowns | 6 s | 3.1 GB |
| The same cavity, driven, Accurate: three refinement passes to 415,100 unknowns | 34 s | 6.9 GB |
| The same cavity's resonances, eigenmode, Draft: 109,708 unknowns, one core | 3 min 21 s | 2.7 GB |

Palace took 34 s and 4.3 GB for that driven sweep, and 3 min 42 s and 3.7 GB for the eigenmode. The two
answers are compared in the example's README.

So the honest summary is:

- **Small metal in a large volume** (wires, a die in a package): FEM's adaptive mesh spends elements
  only where the field changes. FDTD's grid refines along whole lines and planes through the model, and
  its time step falls with the finest cell.
- **Large, simple, Manhattan structures over a wide band**: FDTD's one pulse and small memory win, and
  there is no matrix to factor.
- **Planar structures**: MoM wins both, often by orders of magnitude, because it does not mesh the
  volume at all.
- **Narrow resonances**: FEM, by eigenmode, finds a resonance directly. FDTD has to wait for it to ring
  down, and MoM cannot represent the enclosure.

## Getting the most speed from EMerge {#emerge-speed}

Almost all of an EMerge run's time is spent factorising sparse matrices, so **the sparse solver it uses
decides its speed**. circuitRF's install gives every computer the fastest one it can have, and adds a
faster one by itself when your computer gains what that one needs. Nothing about the answer changes:
on the same mesh, every solver below gave the same S-parameters to within 1e-10 dB.

**What the install gives you, and what makes it faster:**

| Computer | What the install gives | What makes it faster |
|---|---|---|
| macOS 15 or later, arm64 | EMerge's macOS sparse solver, the fastest on the CPU | For the GPU: **the command line tools** (`xcode-select --install`) and a Rust toolchain. circuitRF then builds the GPU solver itself ([below](#emerge-gpu)) |
| macOS 13 or 14, arm64 | A multi-threaded sparse solver | **macOS 15**: EMerge's macOS sparse solver comes only for macOS 15 or later. Reinstall EMerge after updating. The GPU solver is available as above |
| macOS, x64; Linux, x64; Windows | A multi-threaded sparse solver that EMerge's own dependencies bring | Nothing to add. Not yet measured on these computers |
| Linux, arm64 | The same no-compiler multi-threaded solver as an arm64 Mac | Nothing to add. Expected from the arm64 Mac's measurement; not yet measured on Linux |

Measured on an Apple M4 (10 cores, 16 GB) with the 3D Eigenmode example's cavity, driven, an adaptive
sweep over 8–12 GHz, every core in use:

| Sparse solver | 9,835 tetrahedra | 19,426 tetrahedra | Peak memory at 9,835 |
|---|---|---|---|
| EMerge's single-threaded default, used when nothing faster is installed | 48.6 s | 235.6 s | 2.87 GB |
| The multi-threaded solver of macOS 13 and 14 | 4.0 s | 11.2 s | 2.18 GB |
| EMerge's macOS sparse solver | **3.0 s** | **9.2 s** | 1.85 GB |

**Which one you have** is in **Settings ▸ Solvers**: the EMerge row says *Fast sparse solver*, or
*Baseline sparse solver, one core* with what to install, and *GPU sparse solver installed* when there
is one. `circuitrf solver list` prints the same. Each run's cost line names the solver it used.

**Cores.** EMerge's sparse solver uses the setup's *Cores* setting as its thread count, and every
physical core when that is *Automatic*. On one core the run above took 2.0× (EMerge's macOS solver) to 4.1×
(the other multi-threaded solver) as long as on ten. circuitRF does not split a sweep across several EMerge processes: on
the cavity, two processes of five threads took 13.2 s against one of ten's 10.0 s, and each process
holds its own copy of the matrices.

**An eigenmode run uses one core**, whatever is installed: EMerge 3.0's eigensolver runs on one. The
cavity's resonances took 3 min 21 s where its driven sweep took 3 s. Keep `Count` to the modes you need,
and `TargetGHz` near them ([Running EMerge](em-setup.html#emerge-run) says why).

**The adaptive sweep** solves the full problem at five equally spaced frequencies, fits every requested
point from them, and adds the frequency where the fit is least certain until its estimated error on |S|
is below the tolerance. The cavity's 801 points took 10 full solves. Turned off (`AdaptiveSweep` false),
every point is a full solve of its own — 801 for that sweep. Turn it off when the run warns that the
sweep reached its most solves (`MaxSamples`) without converging, or when the sweep has only a few
points, where solving each one costs no more than fitting them. A field saved for the 3D view is always a
full solve of its own at that frequency.

**Accurate refines the mesh itself, and that is where its memory goes**: on the cavity its three passes took
the mesh to 60,503 tetrahedra, and on the 3D Connector's launch they passed 7 GB and the run was stopped by its
memory limit. Standard solves the starting mesh only.

**Memory** grows with the unknowns: about 134 MB plus 24 kB per unknown with a multi-threaded solver,
and 440 MB plus 34 kB per unknown with the single-threaded default. At element order 2 a mesh has about
7.2 unknowns per tetrahedron. circuitRF checks the mesh EMerge made against this computer's memory
before it solves; it cannot estimate the size before meshing, because EMerge sizes its mesh along the
edges of every metal face. Element order 1 has about a fifth of the unknowns, but it put the cavity's
notch 25–30 MHz low, which is why every preset uses order 2.

### EMerge on a GPU {#emerge-gpu}

On an arm64 macOS computer, EMerge can factorise its matrices on the GPU (see
[Settings ▸ Solvers](settings.html#em3d) for how it is installed). **circuitRF uses it automatically
when it is faster**: for a driven problem of 40,000 unknowns or more. Below that the CPU's fast sparse
solver is as quick or quicker. Measured on the shipped Cavity, driven, with an adaptive sweep:

| Unknowns | CPU (fast sparse solver) | GPU |
|---|---|---|
| 29,726 | 0.8 s | 0.8 s |
| 67,106 | 2.3 s | 1.7 s |
| 142,612 | 6.1 s | 3.7 s |
| 303,888 | 17.0 s | 9.2 s |

Both converged in the same number of full solves, and their S-parameters agreed to within 2e-6. The GPU
works in single precision, and circuitRF turns on the GPU solver's own double-precision correction; without
it, the same sweeps either needed more solves or never converged. **A sweep on the GPU is watched**: when its
estimated error has not reached a new low in 8 solves, circuitRF stops it and solves the sweep again on the CPU,
on the same mesh, and says so in the run's Notes. The 3D Wave Ports *Coupled Microstrip* (336,748 unknowns) is
such a case: on the GPU its sweep was no better after 22 solves than after 14, and on the CPU it converged in 9,
at 1.9 s per solve against the GPU's 7.6 s. A sweep on the GPU that stops at its limit without converging is
also solved again on the CPU.

**Every solve on the GPU is checked**, whatever the sweep's length and whether Device is Automatic or GPU: when the
GPU solver reports its factorisation unusable, or its answer misses the equations by more than 1e-3 (‖Ax − b‖/‖b‖), the
whole sweep is solved on the CPU, on the same mesh, and the Notes say so. Saved fields follow the same rule. If the CPU
cannot finish, the run is refused and writes no result. This is commonest at low frequencies: on a 12,000-tetrahedron
line the GPU's S-parameters were up to 0.6 from the CPU's at 1 GHz, and within 2e-4 from 12 GHz up.

**Eigenmode runs do not use the GPU**: EMerge's eigensolver runs on the CPU whatever is installed.
To choose the device yourself, set **Device** in Settings ▸ Solvers to **CPU** or **GPU**, or pass
`--device cpu` or `--device gpu` to `circuitrf em`. On other computers there is no GPU solver for EMerge,
and every run solves on the CPU.

## Choosing, by problem {#choosing}

| Problem | Use | Why |
|---|---|---|
| Lines, bends, couplers, filters, spirals, MIM capacitors on a board or die | **MoM** | Planar metal in a layered stack is exactly what it models, in seconds |
| Vias stitching metal to ground | **MoM** | Vias are part of the planar model |
| A patch antenna on a large ground | **MoM**, or FDTD for a finite board | MoM's ground is infinite and exact; a real board's finite ground needs 3D — see [Antennas](antennas.html) |
| A signal via **through** a reference plane | **FEM** or **FDTD** | MoM refuses it: its return plane must lie below every conductor |
| Bond wires over one flat ground | **wBond**, then **FEM** | [wBond](wbond.html#fidelity)'s equations and kernel are far faster; curved, thin metal is FEM's strength, and FEM is their independent check |
| Bond wires over a ground step or split, in a cavity, under a lid | **FEM** | wBond's ground is one flat plane; FEM solves the ground you drew |
| A die in a package, leads, lids, connectors | **FEM** | Small features in an enclosed volume |
| "Is there a cavity resonance in my band?" | **FEM, eigenmode** (Palace or EMerge) | Finds each mode's frequency and Q directly ([the 3D Eigenmode example](em-setup.html#eigenmodes)); the two programs put the example's cavity mode within 0.01 % of each other |
| A package's capacitance or inductance matrix | **FEM, electrostatic or magnetostatic** | Only Palace computes them |
| A broadband Manhattan board structure: a transition, a launch, a stripline | **FDTD**, or FEM | One pulse covers the band, at little memory |
| A quick FEM answer, or a second FEM answer beside Palace's | **FEM (EMerge)** | Installs in minutes, and a driven sweep of a small model takes seconds; it shares no code with Palace |
| A radiator that is not planar | **FDTD** or FEM | FDTD's PML keeps the box small; both give a pattern — [Antennas ▸ From a 3D solver](antennas.html#3d) |
| Any 3D answer you are about to rely on | **Compare…**, Palace with openEMS or EMerge, or all three | Independent solvers; agreement means each has converged |

When a 3D solver is chosen, the setup panel says which method, FEM or FDTD, suits the model's geometry. That is
advice, not a restriction.

**Heat is a fourth solve, and it is none of these.** A thermal setup on the same 3D model is solved by circuitRF's own
steady-conduction solver — no Palace, openEMS or EMerge — but **it needs Gmsh**, the mesher Palace uses, to mesh the solids. See
[Thermal](thermal.html).

<div class="callout note">
<span class="label">Agreement is a cross-check, not a reference</span>
<p><b>Compare…</b> runs two or more solvers on one model and writes each one's difference from the
reference. Every solver reads the same model that circuitRF built, so a mistake in building it would
appear in every answer. It checks
the solving, not the model. See <a href="em-setup.html#compare">EM Setup ▸ Compare</a>.</p>
</div>

## What each solver sees of curved geometry {#curved}

A bore, a pin, a fillet, a chamfer at an angle: a 3D model is full of surfaces that are not flat and not aligned
with an axis. The 3D solvers see them differently, and the difference follows from their methods.

- **Palace meshes the exact surface.** Gmsh reads the geometry kernel's own shape, not a faceted copy of it, and
  circuitRF asks for **second-order (curved) elements** — each tetrahedron's edges bend to lie on the surface — sized
  to the curvature: about twelve elements per full turn. What limits it
  is the **initial mesh**: a small radius forces small elements, and small elements cost unknowns. So Palace's note
  about a curved feature says how small its elements there will be and that the feature *will dominate the mesh* —
  and its setup's **Enabled** box on the feature is how to find out whether it matters.
- **EMerge meshes the same exact surface** with its own copy of Gmsh, sized by the curvature as EMerge
  asks Gmsh to, so its tetrahedra follow a bore or a fillet as Palace's do. Its elements are
  straight-sided: element order 2 is the order of the field inside each one, not a curved edge, so a
  curve is followed by flat facets that shrink with the elements.
- **openEMS staircases to its grid.** Its grid is rectilinear, so a curved surface is represented by the cells it
  fills: a bore becomes a staircase, and a feature smaller than about **one cell** is not represented at all — a
  fillet of 100 µm on a grid of 200 µm cells is solved as a sharp edge. Refining the grid converges a staircase; it
  cannot make the grid follow the surface. Its note per kernel object names the worst feature: *will not represent*
  (the cell is wider than the radius), *staircases … with about N cells* (a warning below four), or that refining
  the grid converges it.
- **openEMS on a cylindrical grid follows a round surface coaxial with its axis.** A 3D view's setup can ask for a grid
  of circles about one axis ([A cylindrical grid](em-setup.html#openems-cylindrical)); a pin and a bore centred on it
  then lie on the grid's own circles. The coax that runs 1 to 2.4 % slow on the rectilinear grid measured its phase
  velocity exact there, and its impedance within 0.4 Ω. Only surfaces round about that one axis gain: anything else on
  such a grid is staircased as before.

**Where the notes appear.** Under each setup in *Simulate ▸ Setup Analyses…* (the 3D editor's Setups panel), in
`circuitrf check` at their own severity — they never change its exit code — and in the run's messages. None of them
stops a run.

{{ui: em3d-connector-setups}}

**What to change.** A feature openEMS will not represent needs a **finer grid near it**, or it needs **Palace**. In the
setup's openEMS section, more cells per wavelength refines the grid everywhere, and a smaller grading ratio keeps the
cells small further from a part's own edges. The grid beside a small part is set by that grading, not only by the
wavelength: the connector below still had a 200 µm cell at its pin's tip at 30 cells per wavelength, and 143 µm at
the default 20 with a grading of 1.15. A feature Palace says dominates its mesh can be
switched off with its **Enabled** box to see what it costs and what it changes.

### Measured: a connector pin's fillet {#curved-measured}

The **3D Connector** example (*File ▸ New ▸ Examples ▸ 3D Connector*) asks the question with numbers. A 0.4 mm pin in a
1.34 mm PTFE bore — 50.06 Ω by the coaxial closed form — launches onto a 62 mil line on 20 mil of laminate — 49.85 Ω
by Hammerstad and Jensen's — and the pin's tip is rounded by a 0.1 mm fillet. Those two impedances are closed-form
references; everything below is the solvers' output. Each setup ran twice, the fillet enabled and disabled, over 2–18
GHz, on an Apple M4 with 10 cores and 16 GB:

| Run | Time | Size | \|S11\| at 10 GHz | \|S11\| at 18 GHz |
|---|---|---|---|---|
| Palace (Draft), fillet | 1 min 10 s | 4.0 GB | −16.71 dB | −40.75 dB |
| Palace (Draft), no fillet | 1 min 9 s | 4.3 GB | −16.69 dB | −40.90 dB |
| Palace, element order 2, fillet | 10 min 43 s | 10.1 GB | −18.55 dB | −31.95 dB |
| Palace, element order 2, no fillet | 8 min 32 s | 10.7 GB | −18.53 dB | −32.20 dB |
| openEMS (default grid), fillet | 1 min 20 s | 360,360 cells | −17.25 dB | −19.03 dB |
| openEMS (default grid), no fillet | 1 min 18 s | 326,040 cells | −17.71 dB | −19.75 dB |
| openEMS, 30 cells per wavelength, fillet | 1 min 21 s | 355,971 cells | −17.50 dB | −18.65 dB |

- **Palace sees the fillet** — it meshes it with elements of about 52 µm — and finds it changes |S11| by at most 0.25
  dB, at 18 GHz where the match is 32 dB down, with the same sign at both element orders, and |S21| by under 0.001 dB.
  At this band a 0.1 mm rounding on a 0.4 mm pin is **electrically negligible**.
- **openEMS does not see it**: the cell at the tip is 200 µm, twice the radius, and its warning says the edge is solved
  as sharp. Its two runs differ by 0.72 dB at 18 GHz because the fillet's faces add grid lines of their own (360,360
  cells against 326,040) — the grid moved, not the geometry: nothing of a fillet half a cell across is in the model to
  make a difference. That is the honest answer to *does openEMS respect a fillet?*: not one under a cell.
- **Neither |S11| is converged at the settings a first run uses**: element order alone moves Palace's by up to 8.8 dB
  at the deepest null. |S21| is steadier: Palace −0.246 dB (Draft) and −0.207 dB (order 2) at 10 GHz, −0.518 dB and
  −0.503 dB at 18 GHz; openEMS −0.133 dB and −0.457 dB. The difference between the two solvers there is named by the run
  itself: openEMS writes the metals as perfect conductors, since an FDTD grid does not resolve a skin depth, so its
  answer has no conductor loss.

The example's README has every setting behind these numbers, and `expected-numbers.json` beside it records every
frequency of the shipped runs, which circuitRF's own regression test holds.

## Why not just one? {#why-three}

Because **no one method is best at everything**, and the problems an RF designer meets span all three.

- **Why MoM, when a 3D solver can do everything a planar one can?** Because on planar structures —
  most of layout work — it is seconds against minutes and megabytes against gigabytes, and it needs
  nothing installed. A 3D solver has to mesh the air and the substrate that MoM gets for free from its
  Green's function. MoM is the default; 3D is for what it cannot represent.
- **Why FEM?** For geometry that is neither planar nor Manhattan, where adaptive refinement puts
  elements only where they are needed. And for the questions only it answers in circuitRF: resonances,
  and capacitance and inductance matrices.
- **Why FDTD as well?** Because on the structures it suits — broadband and Manhattan — it is fast and
  uses very little memory. It is also the independent check on FEM. The two share no numerics, so
  their agreement means something that re-running one solver on a finer mesh cannot.
- **Why two FEM programs?** Because Palace and EMerge share the method and no code: each builds its own
  mesh, assembles its own matrices, models its own lumped port and fits its own sweep. A third
  independent answer is what makes **Compare** useful: when two of three agree, the third is the one to
  look at, where two that disagree say only that one of them is wrong. EMerge also installs in minutes
  from ready-built packages, where Palace builds from source for most of an hour, and its driven sweep of
  a small model takes seconds.

The three also check each other. circuitRF's solvers were validated this way: wBond's wire kernel
against Palace, the planar patch against a 3D run of the same patch, and Palace, openEMS and EMerge against
each other and against closed forms. [3D EM](em-3d.html) and [Antennas](antennas.html#3d) give the numbers.
