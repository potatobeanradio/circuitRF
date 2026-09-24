# circuitRF — 3D full-wave EM (FEM and FDTD) and thermal (design draft)

**Status:** Draft — rev 3, scoping only · **Date:** 2026-09-24 (rev 0: 2026-09-23, as `fem-3d.md`) ·
**Target:** **v2 at the earliest; parts of it are v3** (PRD §2, §17 v1.4). Nothing here is v1 scope and
nothing in v1 depends on it.

> **rev 0 → rev 1 (2026-09-24):** a second full-wave backend, **openEMS (FDTD)**, beside Palace (FEM),
> **chosen per setup by the user** over one solver-neutral 3D problem (§3, §4); the file renamed from
> `fem-3d.md` because it is no longer only about FEM. **Palace on Windows** worked through in detail
> (§7) — the goal is that no Windows user is shut out of an FEM solve. A **licence finding** that
> affects every Palace binary circuitRF would distribute, on any platform: ParMETIS (§2).
>
> **rev 1 → rev 2 (2026-09-24):** **circuitRF distributes no solver and no mesher** — Palace, openEMS
> and Gmsh are installed by the user and found the way the Verilog-A compiler is (§7.1). That removes
> the ParMETIS question for circuitRF, the GPL source-offer obligations, and the circuitRF-built
> Windows Palace; §7 is rewritten around what a user-installed Palace costs on Windows and how that
> cost is kept small.
>
> **rev 2 → rev 3 (2026-09-24):** user-facing text **names the solvers**, and circuitRF **offers to
> download and install them** from upstream (§7.2) — reporting a failed install verbatim, with remedies
> only for failures circuitRF's own installs have already met. **What it installs it can also
> uninstall**, so a user who no longer needs 3D EM gets the disk back — and **uninstalling circuitRF
> removes them too**, with a warning wherever the platform allows one.

> **This is a dated survey of a fast-moving landscape — re-survey before building anything.**
> Every tool choice below reflects what the open-source ecosystem offered on the dates given, and is
> expected to be revisited months later, when the answers may differ. What was actually checked:
>
> | Tool | State surveyed | Re-check before relying on it |
> |---|---|---|
> | Palace | **v0.18.1** (released 2026-09-21; v0.16.1 Apr, v0.17.0 Jun, v0.18.0 Sep 2026), Apache-2.0, 23 contributors, source read directly on 2026-09-23 and again on 2026-09-24 for §7 | native Windows build? PML? thermal? config compatibility across versions? prebuilt binaries? still PETSc only through SLEPc (§7.4)? |
> | ParMETIS | Pulled in by Palace's superbuild whenever any sparse direct solver is enabled; its licence read directly on 2026-09-24 — **not open source for commercial use** (§2) | licence changed? Palace's superbuild able to omit it? |
> | openEMS | GPLv3; CSXCAD (its geometry library) LGPLv3; latest stable **v0.0.36**, with **v0.37.0 release candidates**; both ship **64-bit Windows builds** (2026-09-24; release dates not confirmed) | release cadence? MPI or GPU engine? STL/PLY import (§6.5)? |
> | Gmsh | GPL; used by every Palace example — **version not checked** | licence unchanged? a permissively licensed mesher now good enough to replace it? |
> | OpenCASCADE | LGPL-2.1 + exception — **version not checked** | any maintained C#/.NET binding (would remove the need for a native worker, §6.2)? |
> | Netgen / TetGen | LGPL-2.1 / AGPL, from general knowledge — not re-verified | — |
> | Avalonia GPU hosting | **12.0.3**: composition GPU interop (IOSurface/Metal, D3D11/Vulkan, DMA-BUF/Vulkan) and `OpenGlControlBase`, from Avalonia's own docs — not prototyped | still there? a built-in 3D surface? |
> | GPU API behind it | Metal/D3D/Vulkan direct, or WebGPU via wgpu (Silk.NET bindings) — general knowledge, not prototyped | maturity of .NET WebGPU bindings |
>
> Also worth a fresh search at revisit time: **other open-source 3D FEM electromagnetic solvers**
> (one with native Windows support or a permissive mesher would change §5 and §7), and
> **open-source thermal FEM solvers** (one that is embeddable would change §9's "write it" call).
> The *architecture* — solvers as external processes, circuitRF owning the document, the resolved
> expressions and the results — is meant to survive a change of tool; the tool names are not.

Companion to [`mom-engine.md`](mom-engine.md) (the 2.5D planar MoM arc) and
[`mom-wirebond-kernel.md`](mom-wirebond-kernel.md) (kernel W). Those remain circuitRF's EM solvers for
the geometry they fit. This document scopes what they cannot reach: **arbitrary 3D geometry solved
full-wave** — packages, lids and cavities, connectors and board transitions, stepped grounds beyond
what kernel W's conductor meshing covers — by the **finite-element method** or **finite-difference
time-domain**, at the user's choice, and, on the same geometry, **3D steady and frequency-domain
thermal** analysis from the channel under a FET's field plate down through die, attach, package,
board vias and heatsink.

---

## 0. The one-paragraph version

circuitRF writes no full-wave 3D solver. It drives two open-source ones as **external processes** —
the same arrangement as circuitRF's device workers, and for the same reasons: **Palace** (Apache-2.0,
MPI-parallel 3D **FEM**) and **openEMS** (GPLv3, 3D **FDTD**). Both are fed from **one solver-neutral
3D problem** that circuitRF owns — named solids, materials, ports and boundaries, every dimension a
resolved number — and **the user picks the solver per setup**, or runs both on the same geometry as a
cross-check. The problem is lowered two ways: for Palace through **Gmsh** (GPL, run as a program) with
its built-in **OpenCASCADE** kernel (LGPL-2.1 + exception) to a tetrahedral mesh; for openEMS to
CSXCAD XML and a rectilinear grid that circuitRF generates itself. OpenCASCADE stays circuitRF's
solid-geometry engine for the editable modeller and the viewer. circuitRF owns what it already owns
everywhere else: the human-readable design document, the expression engine that resolves every
dimension, the run service that turns results into Touchstone files and `DataSet`s, and the viewer.
**circuitRF distributes none of these programs**: the user installs them and circuitRF finds, checks
and drives them, as it already does the Verilog-A compiler. **No Windows user is shut out of an FEM
solve**: circuitRF drives a Palace installed in the user's own Linux subsystem, and openEMS runs
natively (§7). **Thermal is the one FEM circuitRF writes itself**, in C#, because steady heat
conduction is a scalar, symmetric-positive-definite problem an ordinary preconditioned
conjugate-gradient solver handles — and because its most valuable
output is not a picture but a **thermal network for the FET thermal node**, which only circuitRF can
consume.

---

## 1. What already exists, and where 3D starts

| Geometry | Solver today | 3D full-wave's role |
|---|---|---|
| Planar metal on a layered stack, vias | Planar MoM (kernels A–C) | Independent reference only |
| Bond-wire arrays, overmold, stepped ground meshed as a conductor | Kernel W | Independent reference; the regression anchor kernel W already calls for |
| Leadframes, clips, stepped cavities, lids, connectors, board-to-package transitions | **None** — `mom-wirebond-kernel.md` lists "complex 3D metal" as where MoM's advantage erodes | **Primary solver** |
| Radiating structures — antennas on a board or package, far field | Planar MoM, for planar radiators only | **Primary solver** where the radiator is 3D; FDTD's PML fits (§4.3) |
| Heat flow die → heatsink | **None** | **Primary solver** (native C#, §9) |

MoM stays the default wherever it fits: its unknowns scale with conductor surface, FEM's and FDTD's
with the volume of everything including the air. 3D full-wave is added for the geometry MoM cannot
represent, not as a replacement.

---

## 2. Palace — what it is, measured against what circuitRF needs

Surveyed at **v0.18.1**, on 2026-09-23 — see the survey note at the top. ~78,000 lines of C++ of its
own, on a large numerical stack of open-source libraries: MFEM (discretization), hypre (the
auxiliary-space preconditioner for curl-curl problems), a parallel sparse direct solver (SuperLU_DIST,
MUMPS or STRUMPACK), SLEPc/PETSc or ARPACK (eigenproblems), libCEED, MPI. Nothing in that stack is
GPL — **but one piece is not open source for commercial use at all** (below).

**It has:**
- **Driven (frequency-domain)** with lumped ports, **wave ports** (a 2D port eigenproblem), Floquet
  ports, surface current sources; an **adaptive fast frequency sweep** (a rational reduced-order
  model — the answer to "how many frequency points").
- **Eigenmode** (cavity and package resonances), **electrostatic** (Maxwell capacitance matrix),
  **magnetostatic** (inductance matrix) — the last two are exactly package RLC extraction.
- **Adaptive mesh refinement** from a solution error estimate, for every problem type except
  time-domain driven. circuitRF therefore only has to produce a reasonable *initial* mesh.
- Boundaries: PEC, PMC, impedance, **finite conductivity** (a thin metal sheet need not be meshed
  through its thickness), first- and second-order absorbing, periodic; far-field extraction; lumped
  circuit synthesis from the fitted sweep.
- Input: one JSON configuration plus a mesh whose regions and surfaces carry integer **attributes**
  (Gmsh `.msh` among others). Output: CSV (`port-S.csv`, `port-V.csv`, `domain-E.csv`, …) and
  VTK XML field files.

**It lacks, and each is a design constraint here:**
- **No PML** — absorbing boundaries only. Radiating problems need a larger air box (or openEMS, §4.3).
- **No native Windows build** — its own documentation supports Windows only through a Linux
  subsystem, best effort, and publishes no binaries for any platform. §7.4 is how circuitRF meets it.
- **No mesher and no geometry** — every Palace example builds its mesh with Gmsh scripts (§6).
- **No thermal** (§9).
- **Configuration files are not compatible across versions**, by its own statement (§5.3).

**ParMETIS — a licence finding (2026-09-24).** Palace's superbuild builds ParMETIS whenever any
sparse direct solver (SuperLU_DIST, STRUMPACK or MUMPS) is enabled, and SuperLU_DIST is enabled by
default. ParMETIS's own licence permits free use by non-profit institutions and government agencies
for education and research; **anyone else may use it for evaluation only, and it may not be
redistributed without prior approval.** This does not touch a user who builds Palace themselves and
accepts those terms. It would touch **any Palace binary circuitRF itself distributed** — which is one
of the reasons circuitRF distributes none (§7.1). What remains is a sentence on the installation page,
so that a commercial user learns the terms before building rather than after.

---

## 3. openEMS — what it is, measured against what circuitRF needs

Surveyed on 2026-09-24. openEMS is a 3D **finite-difference time-domain** solver (the equivalent-circuit
FDTD formulation) in C++, GPLv3, with its geometry and material description in a companion library,
**CSXCAD** (LGPLv3). It is normally driven from Octave/Matlab or Python scripts that build a CSXCAD
model and write it out as **one XML file**, which the `openEMS` executable then runs.

**It has:**
- **Broadband results from one run.** A Gaussian pulse excites the structure; the port voltages and
  currents are recorded in time and transformed to the frequency domain, so a whole band comes out of
  a single simulation — FDTD's defining strength, and a different answer to "how many frequency
  points" from Palace's reduced-order sweep.
- **Absorbing boundaries: uniaxial PML and Mur** — the PML Palace lacks, and near-field-to-far-field
  transformation for radiation patterns.
- Lumped, microstrip-line and waveguide ports; a **conducting-sheet model** for thin lossy metal (the
  FDTD counterpart of Palace's finite-conductivity boundary); dispersive materials.
- **Cartesian and cylindrical grids, including multi-grid.**
- **Native 64-bit Windows builds**, published with its releases — the property Palace lacks.
- A SIMD, multithreaded engine. Output: port time series as text, field dumps as VTK or HDF5.

**It lacks, and each is a design constraint here:**
- **No mesher that circuitRF can use.** The mesh is a set of grid lines per axis, and in the usual
  workflow the user or a helper script in the scripting interface places them. circuitRF generates the
  grid itself (§6.5) — which is also why the FDTD path carries no GPL mesher at all.
- **Staircased geometry.** Every surface is snapped to the rectilinear grid. Manhattan board geometry
  aligned with the axes is exact; curves and diagonals (a bond wire's arc, a round via barrel, a taper)
  are approximated, and accuracy on them is bought with cells.
- **No adaptive refinement**; convergence is judged by the energy left in the domain (an end
  criterion such as −50 dB) and by re-running on a finer grid.
- **No MPI or GPU engine** is listed; one machine's cores are the ceiling.
- **No eigenmode, electrostatic or magnetostatic solver** — the problem types that make Palace a
  package RLC extractor are FEM-only (§4.3).
- **Project health.** openEMS is largely the work of one maintainer, with an irregular release cadence.
  In its favour, its XML format has been stable for years — less version churn than Palace's.

---

## 4. Two solvers, one problem

The user's choice between FEM and FDTD is a choice of **backend**, not a second design. Everything the
design means — the geometry, the materials, which faces are ports, where the air ends — is written
once; each solver sees a lowering of it.

### 4.1 The solver-neutral 3D problem

The pivot is **not OpenCASCADE's output**: openEMS cannot take a B-rep, and Palace never sees one
either (it sees Gmsh's mesh). The pivot is circuitRF's own **resolved 3D problem** — the 3D
counterpart of the `EmProblem` the planar extractors produce:

- **Named solids**, each a construction object (extruded polygon, box, cylinder, sphere, swept wire,
  boolean result — §6.3), carrying a **material** by name from the technology;
- **named ports** — a port sheet or a pair of named faces, with its reference impedance and, crucially,
  its **reference plane** (§4.4);
- **named boundaries** — the air box and what each of its faces is (absorbing, PEC, PMC, symmetry);
- the **frequency range** and the quantities wanted (S-parameters, fields on named surfaces, far field).

Every number in it is resolved (the rule the circuit side already lives by — **elaborate first**), and
nothing in it names a mesh entity (§6.4). Tier A's generator (§6.3) produces it; Tier B's editor
(§6.3) is a way of authoring it.

| Backend | Lowering | Mesh |
|---|---|---|
| **Palace (FEM)** | problem → `.geo` script → Gmsh with OpenCASCADE → tetrahedral `.msh` + Palace JSON | Gmsh, then Palace's adaptive refinement |
| **openEMS (FDTD)** | problem → CSXCAD XML, written as text | rectilinear grid lines generated by circuitRF (§6.5) |

### 4.2 What is shared and what is per solver

The setup document holds the neutral problem **plus one section per solver**, both kept, so switching
solver is picking a section and "run it in both" is one command, not a second setup.

| Shared (written once) | Palace section | openEMS section |
|---|---|---|
| Geometry, materials, ports, boundaries, frequency range, requested outputs | Initial mesh size fields and grading; adaptive-refinement tolerance and iteration cap; element order; sweep tolerance | Grid rules (maximum cell size in wavelengths, the thirds rule at metal edges, grading ratio); end criterion (energy decay in dB); maximum time steps |

A solver section left empty takes circuitRF's defaults for it, so a user who only ever runs one
solver never sees the other's settings.

### 4.3 When each is the right tool

This is the guidance the setup panel gives, not a restriction — a user may run either on anything.

| Problem | Better fit | Why |
|---|---|---|
| Board and connector transitions, Manhattan geometry on a stackup | **FDTD** | Aligned with the grid, so staircasing costs nothing; one run covers the band |
| Radiating structures, antennas, far field | **FDTD** | PML, and a small air box; Palace needs absorbing boundaries far away |
| Very broadband S-parameters | FDTD | One pulse, the whole band; Palace's adaptive sweep narrows this gap |
| Bond wires, curved and diagonal metal, round vias | **FEM** | Tetrahedra follow the surface; FDTD pays in cells for every curve |
| High-Q cavities, filters, package resonances | **FEM** | FDTD must ring down for a long time; Palace's eigenmode finds resonances directly |
| Package RLC extraction (capacitance, inductance matrices) | **FEM only** | Electrostatic and magnetostatic solvers; FDTD's time step is badly matched to near-DC physics |
| Fine features in a large volume | FEM | Adaptive refinement puts elements where the error is; one fine feature sets FDTD's cell size and, through the stability limit, its time step |

### 4.4 Running both — what agreement proves

Two discretizations that share nothing numerically — tetrahedra in frequency, a grid in time — agreeing
on one geometry is strong evidence that each has converged. Two limits keep that honest:

- **It is a cross-check, not a reference.** Both backends read the same generated problem, so a bug in
  the Tier A generator appears identically in both and agreement cannot catch it. §10's validation
  rule stands: references are externally generated.
- **Ports decide whether the comparison is fair.** A lumped port in FDTD and one in FEM carry different
  parasitics, and an S-parameter is only defined relative to a reference plane. The neutral port
  definition (§4.1) states its reference plane explicitly and each backend de-embeds to it; without
  that, a user sees a few tenths of a dB between the two solvers and distrusts both.

The comparison is a first-class result: running both produces the two Touchstone files and a
difference `DataSet` the Data Display can plot like any other.

### 4.5 Where results land

`EmRunService.ResolveSnpPath` is predictable so that a schematic's SnP reference survives a re-run.
With two backends on one setup, **the solver is part of the path** — otherwise the second run silently
overwrites the first — and a schematic's reference names which solver's result it uses. Re-running the
same solver still lands on the same path.

---

## 5. Why external processes, and not translations or libraries

### 5.1 Translating them to C# — rejected

Palace's 78,000 lines are the thin part. What makes it good is the stack beneath it: hypre's
auxiliary-space preconditioner, a parallel multifrontal direct solver, MFEM's high-order Nédélec
elements — millions of lines of C, C++ and Fortran with no .NET equivalent. CSparse.NET (single
threaded, no supernodes) cannot factor a 3D FEM system of useful size: 3D fill-in grows as
N^(4/3) in memory under the best ordering. A translation is years of work and a permanent fork of a
project that releases roughly every two months.

openEMS is smaller and FDTD is simpler, but it is **GPLv3**: a translation would be a derivative work
and could not enter an MIT codebase (PRD §15). Writing an independent FDTD engine from the literature
is possible in principle and is not proposed — openEMS's value is years of validated port, boundary
and material models, not the update loop.

The honest version of "write our own FEM in C#" is narrower, and is §9: yes for thermal, no for
full-wave.

### 5.2 Loading them as libraries — rejected

A library would share circuitRF's process with MPI, with solver `abort()`s, and with global state.
Both solvers are already programs — `mpirun -n N palace config.json`, `openEMS model.xml` — so the
worker *is* the solver executable and there is nothing to wrap. Process isolation is the property the
device workers are built on (`tools/DeviceWorkerExample/README.md`, "Why a worker is a separate
process"); it applies here unchanged, and it is also what makes a **remote** run (a Linux workstation,
a cluster) the same code path as a local one.

For openEMS the rule has a sharper edge: **its Python and Octave/Matlab interfaces are not used.**
They link the GPL engine and library into the calling process. circuitRF writes the CSXCAD XML as
text from the format, runs the executable, and reads the text and VTK/HDF5 files it writes — which
means circuitRF does the port post-processing itself (§5.3), the same arrangement §6.1 sets for Gmsh.

### 5.3 The contract circuitRF holds

- **A short list of validated versions of each solver.** The user installs the solver (§7.1), so
  circuitRF cannot choose the version — it can only refuse one it has not validated. It writes input
  for the version it finds, checks the executable's version before every run, and refuses an
  unvalidated one **naming the versions that are validated**, rather than letting a silently-misread
  key produce a plausible wrong answer. The list is kept short: every entry is a writer to keep and a
  validation set to re-run. Palace embeds a JSON schema for its
  configuration; circuitRF's writer is tested against that schema for the pinned version. openEMS has
  no schema; its writer is tested against the pinned version's own parse of the XML.
- **circuitRF computes openEMS's S-parameters itself.** The engine writes port voltage and current
  time series; the transform to the frequency domain, the incident/reflected split and the
  normalization to the port impedance are circuitRF code in the numeric layer — the work the scripting
  interface otherwise does, written independently. It is small, and it is the right owner: the same
  code sees every port's reference plane (§4.4).
- **Results land where the planar EM run's land**, with the solver in the path (§4.5). Palace's
  `port-S.csv` and circuitRF's openEMS transform both become a `.sNp` and a `DataSet` like any other
  run. Field files are kept for the viewer (§8).
- **A run is a CLI verb before it is a button** (the rule `docs/design/cli.md` states for every run
  verb): `em` on a setup whose solver is 3D, with the backend chosen by the setup and overridable on
  the command line — or a sibling verb, decided when F1 is briefed.

---

## 6. Meshing and geometry — Gmsh and OpenCASCADE for FEM, circuitRF's own grid for FDTD

### 6.1 Gmsh as an external GPL tool (decided)

Palace needs a conforming tetrahedral mesh with every region and surface tagged. The candidates are
Gmsh (GPL), Netgen (LGPL-2.1) and TetGen (AGPL — excluded); writing a robust 3D tetrahedral mesher is
out of the question. **Gmsh is used, and only as a separate program:**

- circuitRF **writes a `.geo` script as text** and runs the `gmsh` executable on it. circuitRF never
  links Gmsh, never loads its library or its in-process API (Python, C, C++, Julia — all of which link
  GPL code into the caller), and never copies its source. Communication is files and a process exit
  code, which keeps the MIT core outside the GPL's reach (PRD §15).
- **Physical groups** in the script become Palace's attributes. The mapping from circuitRF object to
  group number is circuitRF's, written alongside the mesh.
- **Initial mesh only.** Palace's adaptive refinement converges the answer; circuitRF's job is a
  mesh that is valid and not wasteful — graded near ports and fine features, coarse in air.
- **Thin metal is a sheet**, carrying a finite-conductivity or impedance boundary, wherever its
  thickness is small against both skin depth scale and the neighbouring dimensions. This is the
  single largest mesh saving available.
- **Gmsh is user-installed** and found like every other external program here (§7.1); circuitRF's
  installers carry no copy, so they carry no GPL source-offer obligation either. Gmsh publishes native
  builds for Windows, macOS and Linux, so it is not part of the Windows problem.

### 6.2 OpenCASCADE, as an external tool

OpenCASCADE Technology (OCCT) is a **library**, not a program — it ships no production command-line
tool (its Tcl test harness is not an interface to build on). It reaches circuitRF as an external tool
in two ways, one immediately and one later:

**Route A — through Gmsh (F1 onward, no new dependency).** Gmsh embeds OCCT as its
`SetFactory("OpenCASCADE")` geometry kernel. A `.geo` script therefore already has OCCT's solid
modelling: boxes, cylinders, spheres, cones, extrusion of arbitrary polygons, pipe sweeps along a
path (a bond wire), boolean union/difference/intersection, **fragments** (the operation that makes
touching solids share faces, which a conforming mesh requires), fillets and chamfers, and STEP
import/export. Choosing Gmsh means OCCT is already in the pipeline, out of process, at no additional
cost. Gmsh's fragment operation also reports which output entities came from which input — the
provenance §6.4 depends on.

The limitation is that Route A is **batch**: each edit re-runs `gmsh`. That is fine at mesh time and
for a read-only preview of generated geometry, and too slow and too coarse-grained for an interactive
modeler that must retessellate and hit-test on every drag.

**Route B — a geometry worker (F4, v3).** A small C++ program, `tools/geometry-worker`, links OCCT
and stays running for the life of a modelling session, spoken to over stdin/stdout the way the device
workers are:

- *build* — takes a **fully resolved** construction tree (numbers only; see §6.3) and returns, per
  face, a triangle tessellation tagged with the construction object it came from;
- *pick / measure* — hit-tests and distances against the exact solid, not the tessellation;
- *export* — writes a BREP or STEP file that Gmsh meshes (Route A's pipeline, unchanged), and a
  per-solid triangle surface for the FDTD lowering where CSXCAD's primitives cannot express a shape
  (§6.5).

OCCT's licence (LGPL-2.1 with an additional exception) allows linking it into this worker; OCCT is
shipped as shared libraries beside it, and the worker's own source is MIT. OCCT builds natively on
Windows, macOS and Linux, so — unlike Palace — the geometry worker can ship in every installer. It
is a **native dependency** in the CLAUDE.md sense and is agreed in principle (PRD §17 v1.4); the
build and packaging cost is measured when F4 is briefed, the way `tools/senior-worker`'s was.

**OCCT's role is the same whichever solver the user picks**: it is the modelling kernel of Tier B and
the source of the viewer's tessellation (§8). It is on Palace's meshing path and, for openEMS, only on
the path of shapes the neutral problem cannot state as primitives.

### 6.3 Where the geometry comes from

**Tier A — generated from what circuitRF already holds (v2).** The layout (`.clay`), the
technology's stackup (`.ctech`) and kernel W's wirebond parameters already describe most package and
board geometry, and the planar EM extractors already turn them into an `EmProblem`. The 3D problem
(§4.1) is generated from the same inputs, on demand, exactly as kernel W generates a wire's 3D path:
each layer's polygons extruded through its stackup thickness, vias as prisms or cylinders, wire paths
as circular sweeps, balls and bumps as spheres or truncated spheres, mould compound and lids as boxes.
**The user draws no solid.** Every dimension is already a resolved circuitRF value, so a parametric
sweep regenerates and remeshes per point with no new machinery. Tier A's vocabulary is almost exactly
CSXCAD's primitive set, so the FDTD lowering of a Tier A problem needs no OCCT at all.

**Tier B — a 3D view the user edits (v3).** A new cell view holding a **construction history**:
named primitives and operations (box, cylinder, polygon extrude, sweep, boolean, fragment), each
dimension an **expression** in circuitRF's one expression engine, scoped by the cell's parameters
like every other view. The file is human-readable, as every circuitRF document is; its format and
extension are decided with F4. The rule the circuit side already lives by applies unchanged —
**elaborate first**: circuitRF resolves every expression and hands the geometry kernel numbers only,
just as the numeric layer never sees an unresolved parameter.

### 6.4 Naming — the problem every history-based modeler has

When a dimension changes, a solid's faces are rebuilt and their indices change. A boundary condition
stored as "face 17" silently moves to a different face. **circuitRF never stores a face or volume
index.** Materials, ports and boundaries attach to **named construction objects** (a solid, or a
named sheet drawn for the purpose — a port sheet, a radiation box face), and the mapping to solver
entities is rebuilt on every run: to mesh groups from the kernel's provenance (Gmsh's fragment map in
Route A; OCCT's `Modified`/`Generated`/`IsDeleted` history in Route B), and to CSXCAD properties by
name, since each named object becomes one or more named CSXCAD primitives. A named object that no
longer yields any face is a refusal naming the object, never a guess.

### 6.5 The FDTD lowering and its grid

**Geometry.** Each named solid becomes CSXCAD primitives on a property named after it: extruded
polygons, boxes, cylinders, spheres, wires and curves cover Tier A. CSXCAD has **no booleans**; where
primitives overlap, the one with the higher **priority** wins the cell. A subtraction therefore
becomes a higher-priority solid of the surrounding material, and circuitRF assigns priorities from the
construction order so the FDTD result means what the construction tree means. Shapes no primitive can
state (fillets, general booleans from Tier B) are tessellated by OCCT (§6.2) and read by CSXCAD as a
polyhedron from a triangle file — CSXCAD's polyhedron import is believed to read STL and PLY; to
verify at F0.

**The grid is circuitRF's to write, and it is the real work of this backend.** FDTD's accuracy is
decided almost entirely by where the grid lines fall:

- lines **on every metal edge**, and the **thirds rule** at the edge of a strip (lines placed one
  third inside and two thirds outside the edge, where the field singularity lives);
- a **maximum cell size** of a fraction of the shortest wavelength in each material;
- **smooth grading** — neighbouring cells differ by no more than a fixed ratio;
- **alignment**: every port and every thin sheet exactly on a line.

It is a one-dimensional problem per axis — collect the required lines, then fill and smooth between
them — so it is ordinary managed code in the numeric layer, MIT, testable headlessly, and the reason
the FDTD path has no mesher dependency. The count of cells it produces, and the time step the
stability limit then allows, are reported before a run starts; a grid that would not fit in memory is
a refusal naming the feature that set the cell size.

---

## 7. Where the solvers come from, where they run — and Windows

### 7.1 circuitRF distributes no solver and no mesher

**Palace, openEMS and Gmsh are installed by the user, never shipped by circuitRF** — no binary in an
installer, no image, no first-use download, on any platform. This is the arrangement circuitRF already
has for the Verilog-A compiler (`VerilogACompilerDiscovery`, `src/Core/RESOLVED.md`), and it is
adopted for the same reasons, plus two of its own:

- **Licences stay the user's and upstream's, not circuitRF's.** Starting a separately installed
  program, handing it circuitRF's own files and reading what it writes is use, not derivation or
  distribution — the position `src/Core/RESOLVED.md` records for the compiler. circuitRF takes on no
  GPL source-offer obligation for openEMS or Gmsh, and **the ParMETIS restriction (§2) never reaches
  circuitRF**, because circuitRF redistributes nothing. `THIRD-PARTY-NOTICES.md` gains no entry.
- **No build farm.** No per-platform solver builds, no carried patches, no re-validation of a
  circuitRF-made binary at every Palace release — each of which the native Windows route would
  otherwise have required.
- **circuitRF works without them.** With none installed, every 3D run is a refusal and nothing else
  changes — schematic, planar EM, HB, loadpull and thermal (§9, managed code) are untouched.

What circuitRF does instead is make a user-installed solver **easy to point at and impossible to
misuse**:

- **Discovery, in the compiler's order:** a path the user names in Settings, then an environment
  variable, then `PATH`, then the handful of directories a GUI-launched process cannot see through
  `PATH` (the finding `src/Core/RESOLVED.md` records for the compiler: a Finder-launched app's `PATH`
  holds only the four system directories). **A named program that does not work is reported, never
  silently replaced** by one found elsewhere. The candidate command names live in one list per tool,
  the only place in circuitRF that names it.
- **Version check before every run** against the validated versions (§5.3). An unvalidated version is
  a refusal naming the versions that are validated — for Palace this matters, because its
  configuration changes meaning across versions.
- **A capability probe, not just a version string.** Palace built without an eigensolver cannot solve
  wave ports; circuitRF finds that out at setup time — from what the build reports about itself, or a
  seconds-long probe run, decided with F1 — and refuses a wave-port setup by saying so, rather than
  letting the solver fail minutes into a run. A user-built Palace can be configured in ways a shipped
  one never would be, so this check earns its place.
- **An install assistant** that downloads each program from its own upstream source and runs its
  installer on the user's request (§7.2) — and an installation page per platform for anyone who
  would rather do it by hand, both kept current with the validated versions. A Settings row per tool
  shows what was found, where, which version, and how.
- **The licence note is the user's to read, and circuitRF puts it in front of them.** Palace's default
  build includes ParMETIS, whose terms allow commercial use for evaluation only (§2). A user who
  installs Palace accepts those terms directly; the assistant's consent step and the installation page
  both say so in one sentence rather than leaving a commercial user to discover it.
- **The solvers are named.** Unlike the Verilog-A compiler, whose dialogs, refusals and doc pages
  name no product, circuitRF's user-facing text names Palace, openEMS and Gmsh (owner's call,
  2026-09-24): an assistant that installs three separate programs, and a page that tells a user how
  to install them by hand, cannot be written without their names. The candidate command names still
  live in one list per tool.

The alternative — a circuitRF-distributed Palace — was set aside (owner's call, 2026-09-24) for the
cost it carries: a ParMETIS-free rebuild on every platform, a native Windows port to maintain, and a
release per upstream release. The saving has a price, and it is paid in installation effort, mostly on
Windows (§7.4); the assistant (§7.2) is how circuitRF pays most of it on the user's behalf.

### 7.2 The install assistant

**circuitRF offers to install the solvers; it does not ship them.** Offered from the refusal a 3D run
gives when a solver is missing, and from the solver's Settings row: *Install Palace…*. With the user's
consent, circuitRF downloads the program **from its own upstream source** — openEMS's and Gmsh's
published archives, Palace through its package manager (Spack today; a binary package once one exists,
§7.4) — and runs the upstream installer or build. The bytes come from upstream, under upstream's
licence, onto the user's machine at the user's request: circuitRF still redistributes nothing (§7.1).

- **Consent names what will happen**: the program and version, where it comes from, where it will go,
  roughly how long it takes (for a from-source Palace build, hours — said up front), how much disk it
  needs, and the licence note (§7.1). Nothing is downloaded before the user says yes.
- **It installs per user**, under circuitRF's per-user state (`AppDataRoot`, the home the compiled
  Verilog-A models already have), never into a system directory and never needing administrator
  rights — except for the one step circuitRF cannot do for a Windows user, enabling the Linux
  subsystem (§7.4), which it explains and does not attempt. On Windows, Palace is installed into the
  user's own Linux subsystem distribution, inside its Linux filesystem.
- **A downloaded archive is verified** against the checksum upstream publishes, where it publishes one;
  where it does not, the installation says so rather than implying a check it did not make.
- **It runs in the background, with progress and cancellation**, on the same `RunControl` the EM runs
  use: a day-long build must not hold a window hostage. Cancelling leaves nothing half-installed where
  discovery would find it.
- **What it installed is what discovery finds.** On success circuitRF names the installed program in
  the tool's Settings row, runs the version and capability checks (§7.1) against it at once, and
  reports the result. An install is not called a success until those checks pass.
- **Recipes are data, per validated version.** The commands for each tool, version and platform live in
  one versioned recipe file, not in code, so adopting a new validated version (§5.3) is a recipe edit
  and a validation run.

**When an installer fails, circuitRF reports it — and does not try to be clever.** The report says
which step failed, gives the upstream tool's own output **verbatim** (the line that failed is the whole
value of a build error, the rule circuitRF already follows for Verilog-A compiler diagnostics), and
keeps the full log at a path it prints. It does not silently retry, switch recipes, or patch anything.
Where the failure matches one **circuitRF's own installs have already met**, the report adds that
case's known remedy — a missing build tool in a fresh Linux distribution, a build started on the
mounted Windows drive, a checksum failure from an unreliable dependency mirror (reported upstream as a
transient that clears on retry). That table of known failures is **learned, not guessed**: it is
seeded by installing each solver several times on each platform during F0 (§10), and grows from
failures users report. A failure it does not recognize is reported as unrecognized, with the log, and
nothing more.

**What the assistant installs, it can uninstall.** A user who no longer needs 3D EM gets the disk
back — and a from-source Palace build, with the package manager's downloaded sources and build cache
beside it, runs to gigabytes.

- **Only what circuitRF installed.** Every install writes a record of what it put where; uninstall
  removes exactly that and nothing else. A solver the user installed themselves — found on `PATH` or
  at a path named in Settings — has no Uninstall action at all, and circuitRF never deletes it. The
  Settings row says which kind each tool is: *installed by circuitRF* or *found at …*.
- **On Windows**, Palace was installed into a circuitRF-owned directory inside the user's Linux
  subsystem distribution; uninstall removes that directory. The distribution itself is the user's and
  is never removed, and neither are system packages the user installed on circuitRF's advice (the
  build tools of §7.4) — the confirmation says those remain and are the user's to remove.
- **The confirmation shows the space it will free**, measured, before anything is deleted. Removal
  is permanent; getting the tool back is a reinstall — for Palace, the same hours-long build — and the
  confirmation says so.
- **"Remove all 3D solvers"** is one action for the user who is done with 3D EM, beside the per-tool
  ones.
- **Refused while in use**: a solver that a run or an install is using is not removed from under it;
  the refusal names what is using it.
- **Documents are never touched.** Setups, meshes, Touchstone results and field files live in the
  user's workspaces, not in the per-user folder, and uninstalling a solver leaves every one of them.
  Afterwards a 3D run is a refusal offering *Install…* again, exactly as before the first install.
- **Superseded versions are not left behind silently.** When the assistant installs a newly validated
  version (§5.3), a circuitRF-installed older version is listed with its size and offered for removal,
  rather than accumulating unseen.

**Uninstalling circuitRF uninstalls the solvers it installed — with a warning, wherever the platform
lets circuitRF give one** (owner's call, 2026-09-24). The warning says the solvers will be removed and
that reinstalling circuitRF later means reinstalling them — for Palace, the hours-long build again.
Whether circuitRF can give that warning depends on how the user removes it, and circuitRF's packages
are removed four different ways:

| How circuitRF is removed | Can it warn? | Can it remove the solvers? |
|---|---|---|
| **Windows `.msi`**, from the system's Apps list | **Not reliably.** Windows runs an MSI uninstall with its reduced interface — a confirmation and a progress bar, not the package's own dialogs — and a silent uninstall by an administrator shows nothing at all. | **Partly.** A perMachine uninstall runs elevated as the system account, which cannot reach the user's per-user folder or their Linux subsystem, where Palace lives. |
| **macOS**, the app dragged to the Trash | **No.** No circuitRF code runs when an app is trashed. | **No.** |
| **Linux `.deb`**, through the package manager | **No.** Removal is non-interactive by Debian convention. | **No.** Its removal script runs as root, and Debian policy forbids a package from touching users' home directories. |
| **Linux `.tar.gz`**, `install.sh --uninstall` | **Yes** — circuitRF's own script, run by the user. | **Yes.** |

So the design is:

1. **An *Uninstall circuitRF…* command inside the application, on every platform** — the one route
   that can always warn, and that runs as the user, so it reaches every solver the assistant installed,
   Palace in the Linux subsystem included. It shows the warning, removes the solvers (exactly as
   *Remove all 3D solvers* does, refused while one is in use), then removes circuitRF itself: the MSI
   uninstall on Windows, the `.app` moved to the Trash on macOS, the tarball's own uninstall on Linux.
   A `.deb` install is removed by the package manager, so there the command removes the solvers and
   then tells the user the one command that removes the package.
2. **On Windows, the Apps-list uninstall is routed through that command.** The perUser layout already
   has a stub `circuitRF.exe` that never changes (`packaging/windows/circuitRF.wxs`); the uninstall
   entry Windows shows points at it, and the stub runs the MSI uninstall after the warning. This is
   the ordinary Windows pattern for an application that must ask something on uninstall, and it is a
   packaging change to design with the scripts; perMachine gets the same treatment through the
   installed executable.
3. **An upgrade never removes the solvers.** A perMachine install is updated by running a newer `.msi`
   — updates there are notify-only (`circuitRF.wxs`) — and Windows Installer's major upgrade works by
   uninstalling the previous version first. Anything attached to uninstall runs only on a real removal,
   never inside an upgrade; otherwise every update would delete a day-long Palace build. The same holds
   for the perUser updater's version swap and the macOS bundle exchange, which are not uninstalls and
   must never be treated as one. This is a gate, not a note: an upgrade that removes a solver is a bug.
4. **Where no warning is possible — the Trash on macOS, the package manager on Linux — the leftovers
   are made useful, not hunted down.** The solvers stay in the per-user folder with their install
   record; a later reinstall of circuitRF **finds them and uses them without reinstalling**, and their
   Settings rows still carry *Uninstall*. The installation page says so, and says that running
   *Uninstall circuitRF…* first is how to get the space back.
5. **An uninstall reaches only the account running it.** On a shared machine, solvers another user
   installed stay in that user's folder; the warning says so, and each user's own *Remove all 3D
   solvers* is how they go.

**A headless spelling** is part of the design, as it is for every capability here (`docs/design/cli.md`):
a build machine installs — and uninstalls — a solver the same way the GUI does, from the same recipe
and the same install record. Its shape is decided with F1.

### 7.3 Locations

| Location | Platforms | Notes |
|---|---|---|
| **Local, native** | Palace: Linux, macOS. openEMS and Gmsh: all three | The user's own install, found by §7.1's discovery. |
| **Local, the user's Linux subsystem** | **Windows**, for Palace | circuitRF drives a Palace the user installed inside it (§7.4). |
| **Local container** | Windows, macOS, Linux | An image the user builds or obtains (Spack generates container recipes); for build machines more than desktops. |
| **Remote host** (SSH) | any client | Mesh and configuration go out, CSV and fields come back. The route for problems a laptop cannot hold (§7.5). |

Every location runs the same input files, so a result does not depend on where it was computed.
**Automatic** is the default setting: it uses the first location that is available, in the table's
order, and when none is, the refusal names the one action that would make one available.

**openEMS and Gmsh pose no location problem** — both publish native builds for Windows, macOS and
Linux, so on every platform installing them is downloading an archive and, at most, naming it in
Settings.

### 7.4 Windows and Palace

This is the one place the rule costs something, and the design's job is to make that cost as small as
it can be without circuitRF distributing anything.

**What circuitRF does: drive the user's own Linux subsystem.** Palace's maintainers recommend the
Windows Subsystem for Linux to Windows users. circuitRF treats a Palace inside it as a first-class
location:

- it **finds** it — lists the user's distributions and runs §7.1's discovery inside each, so a user who
  installed Palace in the subsystem configures nothing on the Windows side;
- it **stages the run inside the Linux filesystem**, not on the Windows drive the subsystem mounts,
  where file access across the boundary is slow: mesh and configuration copied in, `port-S.csv` and
  the requested field files copied out, large field files only when the viewer asks for them;
- it **checks memory against the subsystem's**, not the machine's — the subsystem's virtual machine
  gets a fraction of host memory by default — and a refusal names the setting that raises it;
- compute inside the subsystem runs at close to native speed, so the cost is the file transfer the
  staging above keeps to inputs and results.

When the subsystem is missing, the refusal says which of its two preconditions is missing
(virtualization in firmware, or the Windows feature, which needs administrator rights once) and what
enables it. Some managed corporate machines forbid both; there, remote (§7.5) and openEMS (§4.3) are
what remain.

**What installing takes, today: a build from source.** No binary package of Palace exists for any
platform as of 2026-09-24. Palace's documented install is **Spack, which compiles the whole stack from
source** — reported in Palace's issue tracker as taking most of a day inside the Linux subsystem, and
in that report ending in a build failure. That is the real gap, and it is **not Windows-only**: a
macOS user installs the same way. The install assistant (§7.2) runs that build for the user, inside
their own distribution and its Linux filesystem, with the preconditions that trip people up checked
first (the build tools a fresh distribution lacks; building on the mounted Windows drive, which is
where the failed report was building). It shortens the user's part to one consent and a wait; it does
not shorten the build.

**What closes the gap without circuitRF distributing anything: upstream packages.** A community
package-manager recipe for Palace (conda-forge) is open and awaiting review, **linux-64 only**, at
v0.18.1. Once it lands, installing Palace inside the Linux subsystem is a binary package install —
minutes, not a day — and the installation page switches to it. It is the lever with the most reach,
so it is where circuitRF's effort on this problem goes: helping that recipe land, then extending it
to macOS and, eventually, a native Windows variant. Contributions upstream are not distribution by
circuitRF; the binaries are the package manager's, under its terms.

**A native Windows Palace is an upstream matter, and more feasible than it was.** In 2023 the stated
obstacle was PETSc, then used for all of Palace's complex-valued linear algebra. Reading the v0.18
source (2026-09-24), Palace has its own complex vector and operator types and compiles its PETSc
header only under `PALACE_WITH_SLEPC`: **PETSc is now reached only through SLEPc**, and a build with
ARPACK as the eigensolver leaves PETSc out entirely. A minimum configuration for what circuitRF runs —
driven S-parameters with lumped and wave ports, electrostatic, magnetostatic, eigenmode:

| Keep | Leave out | Why out |
|---|---|---|
| MFEM, hypre, libCEED, METIS, SuperLU_DIST, ARPACK-NG, BLAS/LAPACK, the header-only JSON, formatting and Eigen libraries | SLEPc and PETSc, STRUMPACK, MUMPS, libxsmm, MAGMA, SUNDIALS, GPU backends | SLEPc/PETSc: the historical Windows blocker, replaced by ARPACK. STRUMPACK/MUMPS: further direct solvers, and Fortran. libxsmm/MAGMA: optional accelerators. SUNDIALS: Palace's time-domain solver, which circuitRF does not use. |

Fortran is then needed for ARPACK-NG alone, and two open-source toolchains build native Windows
binaries with one (LLVM with Flang; MinGW-w64 with gfortran). Windows has a freely redistributable
MPI runtime; whether it covers every call the stack makes is untested. This is recorded as what a
Windows port **would need**, offered upstream as a finding — not as work circuitRF commits to, and not
as a binary circuitRF would ship if it built one.

### 7.5 Remote host

Mesh and configuration go out over SSH, CSV and fields come back. It is the route for problems a
laptop cannot hold, and **it is the same on every client operating system** — a Windows user with a
Linux workstation or a cluster has the full Palace, including multi-node MPI, with nothing installed
locally at all. circuitRF holds no credential for it, on the terms `history`'s network operations
already follow: it uses whatever SSH is configured with, and a prompt is a refusal, not a hang.

### 7.6 The Windows position, in order

1. **FDTD is native on Windows** — openEMS publishes Windows builds, so for the problems §4.3 gives it
   a Windows user has the same local solver as everyone else, installed by unpacking an archive.
2. **Thermal (§9) is managed code** and needs no install anywhere.
3. **FEM runs in the user's own Linux subsystem**, which circuitRF drives end to end (§7.4), with
   Palace installed there by the install assistant (§7.2) — a from-source build until an upstream
   package lands, run for the user rather than by them.
4. **Remote (§7.5)** gives the full Palace with no local install.
5. **A native Windows Palace** arrives only from upstream or a package manager (§7.4); circuitRF's
   part is contributing to it, and its discovery picks it up as a local native install the day it
   exists.

---

## 8. The 3D viewer — hosted in Avalonia, and fast

**The bar is interactive feel, not just correctness**: orbiting, hovering and selecting should feel as
immediate as the snappiest open-source 3D modelling tools (Blender is the reference the owner named).
Everything in this section follows from one rule: **the screen never waits on geometry.**

### 8.1 Avalonia hosts the viewport, not OpenCASCADE

OpenCASCADE computes shapes; circuitRF's own renderer draws them. OCCT's bundled visualization layer
is **not** used: it would put OCCT back in circuitRF's process (the geometry worker of §6.2 exists to
keep it out) and it owns a native window, which is the hosting option §8.3 rejects. The worker hands
back triangles tagged by construction object; the renderer never calls the kernel.

**Not SkiaSharp.** Skia is a 2D rasterizer with no depth buffer; its 4×4 matrix only puts 2D layers in
perspective. Software depth sorting draws a wireframe of a few hundred solids and fails at what the
viewer must show: intersecting translucent solids, and fields on 10⁵–10⁶ elements.

### 8.2 What makes a 3D tool feel fast, as requirements

1. **Geometry is GPU-resident.** Tessellations are uploaded once as vertex buffers; orbit, pan and
   zoom change one camera matrix and upload nothing. Draws are batched per object and material, never
   per face.
2. **Picking is done by the GPU.** Object and face IDs are drawn into an offscreen buffer and the pixel
   under the cursor is read back, so hover and selection cost the same at 10 objects as at 10,000.
   Hover highlighting is a shader state change, never a retessellation.
3. **A drag is a preview.** Moving, rotating or resizing transforms what is already drawn; the kernel
   rebuild happens once, on release. The rules the PCell parameter handles already follow
   (`pcell-parameter-handles.md`) govern a drag on a dimension bound to an expression.
4. **Heavy work is off the drawing path.** The viewport always draws the last *finished* geometry; a
   rebuild in flight never blanks or stalls it.
5. **Keyboard-first, no modal dialogs mid-gesture** — a value can be typed while dragging.

A mesh modeller works on triangles; a B-rep kernel's booleans and tessellation take tens of
milliseconds to seconds. The kernel cannot be made that fast, so the design makes sure **nothing ever
waits for it**.

### 8.3 How Avalonia hosts it

Checked against Avalonia 12.0.3 (the version this repo builds with) on 2026-09-23:

| Option | How | Verdict |
|---|---|---|
| **Composition GPU interop** | circuitRF's own GPU device renders on its own thread into a shared texture; the compositor imports it (`CompositionDrawingSurface` — IOSurface + Metal shared events on macOS, D3D11 or Vulkan handles on Windows, DMA-BUF or Vulkan on Linux) and only composites it | **Chosen direction** |
| `OpenGlControlBase` | Avalonia supplies an OpenGL context inside its own render pass | Simplest to prototype; OpenGL is frozen at 4.1 on macOS |
| `NativeControlHost` | A native child window with its own swapchain | **Rejected**: Avalonia content cannot draw over it, and Dock's floating panels re-parent windows, which a native child survives badly |
| Skia | 2D only | Rejected (§8.1) |

**Why composition interop, in this repo specifically.** circuitRF has already measured that **an
application has one compositor**: a 1,500 ms layout frame took the Project Tree, the Messages panel and
File ▸ Quit down with it, and the only cure was bounding the frame (`src/Ui/RESOLVED.md`, "The whole
UI crawled…"). With composition interop the compositor's share of a 3D frame is placing a finished
texture. A 3D frame still rendering, or a kernel still rebuilding, means the previous image stays up —
it cannot starve the rest of the window.

The GPU API behind the shared texture (Metal/D3D/Vulkan directly, or WebGPU over wgpu, which targets
all three) is decided on the F2 spike (§8.6).

### 8.4 Three loops at three speeds

1. **Frame loop** — GPU, at display refresh: camera, hover, gizmos, selection outline. Near-zero
   managed work per frame, which also keeps the Debug build (the one the owner runs) responsive.
2. **Input loop** — UI thread: turns pointer and key events into small messages to the frame loop and
   the kernel loop. It computes no geometry.
3. **Kernel loop** — the out-of-process geometry worker (§6.2), asynchronous:
   - every edit carries a **generation number**; a result for a superseded generation is discarded;
   - a **coarse tessellation first**, refined afterwards;
   - each construction-history step is **cached**, so an edit rebuilds only the steps after it.

Keeping OCCT out of process is therefore a responsiveness property as well as an isolation one: kernel
work physically cannot block the UI thread.

### 8.5 Split across the firewall

As `src/Render` is today: tessellation buffers, the scene model, colour maps, camera and picking
geometry live below the UI firewall (no Avalonia); only the GPU device and the composition surface
live in `src/Ui`. That keeps a headless `render` of a 3D view possible later without a second renderer.

**What it shows, in order:** the generated geometry (Tier A preview), the mesh — Palace's tetrahedra
or openEMS's grid lines, which is where an FDTD user sees whether the grid landed on the metal edges —
then fields read from either solver's output (Palace's VTK XML, openEMS's VTK or HDF5 dumps) — |E|,
surface current, and for thermal, temperature — on boundary surfaces and clip planes (never the
volume's millions of elements directly). With both solvers run on one setup, the same surface can show
either field or their difference.

### 8.6 Proving it before building it

- **Spike first (opens F2):** one Avalonia pane on composition interop that orbits a million triangles
  and GPU-picks under the cursor, on all three operating systems, before any viewer code is written.
- **Gates are counters, not timings** (timing tests measure the machine and flake): *an orbit uploads
  zero bytes*, *a hover makes zero kernel calls*, *a drag makes zero kernel calls until release*,
  *a superseded generation's result is never drawn*.
- **Feel is judged by hand.** A headless session can build and test the viewer but cannot see it;
  "does it feel fast" is an owner check on real hardware, recorded as such.

---

## 9. Thermal — native C# FEM

### 9.1 Why this one is written, not borrowed

Neither Palace nor openEMS solves heat flow. Steady conduction, ∇·(k∇T) = −q, is the easiest problem
FEM has: one unknown per node, a symmetric positive-definite matrix, solved by conjugate gradients
with an algebraic multigrid or incomplete-Cholesky preconditioner. Linear or quadratic tetrahedra on
the **same Gmsh mesh pipeline** (§6) are enough. It needs no MPI, no curl elements and no external
solver, so it can be ordinary managed code in the numeric layer and run on every platform circuitRF
ships on — Windows included, with no route to choose.

### 9.2 The physics that decides channel temperature

- **Scale span of ~10⁵** — a sub-micron heat source under a field plate, a centimetre-scale heatsink.
  Handled by graded meshing, and where that is not enough by sub-modelling: solve the package
  coarsely, then the die region finely with the coarse solution as its boundary.
- **Temperature-dependent conductivity** — the substrate materials of RF FETs lose conductivity as
  they heat. The Kirchhoff transform linearises this exactly for a single material; with several,
  Picard iteration on k(T).
- **Where the heat is** — dissipation concentrates at the drain-side edge of the gate, not uniformly
  over the channel; the source geometry is an input the FET cell supplies, not a guess.
- **Interfaces** — thermal boundary resistance between epitaxial layers and substrate, die attach, and
  voids are surface conditions, not meshed layers.
- **Boundaries** — fixed temperature, convection (a heat-transfer coefficient), insulated.

### 9.3 What it produces that only circuitRF can use

The FET models already expose a **thermal node**, and the network behind it is the host's to build
(the models ignore their own lumped thermal parameters). Thermal FEM builds it:

- an **N×N thermal-resistance matrix** across the fingers of a multi-finger device, including mutual
  heating;
- the **thermal impedance Z_th(jω)**, from the frequency-domain heat equation
  ∇·(k∇T) − jωρc·T = −q — which stays inside circuitRF's no-transient scope;
- fitted to a **Foster or Cauer RC network** attached to the thermal node, so DC, harmonic balance
  and loadpull see the package's real self-heating.

That coupling is the reason thermal belongs in circuitRF rather than in a separate thermal tool.

---

## 10. Phases

| Phase | Content | Version |
|---|---|---|
| **F0** | Spike, no product code: Palace, openEMS and Gmsh installed by hand; hand-written models of one bond-wire-over-ground case and one via transition, **run through both solvers**; compared against kernel W, planar MoM and each other. Measures install cost, laptop run time and memory, agreement, **FDTD's cost on the curved bond wire** (the case §4.3 expects it to struggle with), **installing each solver several times on each platform** — how long it takes, every failure met and what fixed it, which seeds the install assistant's recipes and its table of known failures (§7.2), and whether CSXCAD reads a triangle file (§6.5) — and produces **externally generated reference data** for the existing MoM kernels, useful even if nothing further is built. | any time |
| **F1** | The solver-neutral 3D problem (§4.1) and the Palace backend, CLI first: Tier A → problem → `.geo` → Gmsh → Palace → `.sNp` + `DataSet`. Driven S-parameters, plus electrostatic/magnetostatic for package RLC. Discovery, version and capability checks (§7.1); the install assistant, its uninstall, and their CLI spelling (§7.2); the solver-location setting (§7.3), including driving the user's own Linux subsystem on Windows (§7.4); the installation page. | v2 |
| **F1b** | The openEMS backend on the same problem: CSXCAD writer, the grid generator (§6.5), circuitRF's own port post-processing (§5.3); run-both and the difference `DataSet` (§4.4); solver in the result path (§4.5). | v2 |
| **FU** | Upstream, not product code: help the community Palace package recipe land (linux-64), then extend it to macOS; offer §7.4's Windows findings to Palace. Whatever lands, §7.1's discovery picks up with no circuitRF change. | alongside F1 |
| **F2** | Read-only 3D viewer: geometry, mesh or grid, fields from either solver — opened by the hosting spike of §8.6. | v2 |
| **F3** | Native thermal FEM on the F1 mesh pipeline; thermal-resistance matrix and Z_th(jω) fitted to a network on the FET thermal node. | v2 or v3 |
| **F4** | Editable 3D view (Tier B), construction history with expressions, `tools/geometry-worker` (OCCT, Route B), feeding both backends. | v3 |

Whether F1 or F1b ships first is F0's call, not a prior decision: F1 carries Palace's broader problem
set, F1b a solver every Windows user runs natively.

### Validation

Per CLAUDE.md, references are externally generated. For F1 and F1b: closed forms (coaxial and
rectangular waveguide impedance and cutoff, cavity eigenfrequencies, parallel-plate capacitance) and
F0's hand-built runs of each solver, re-run for every version added to the validated list (§5.3).
For F3: the one-dimensional slab and the analytical spreading-resistance
solutions for a rectangular source on a layered substrate. FEM-vs-FDTD and FEM-vs-MoM agreement on
shared geometry are cross-checks, not references — all of them are circuitRF-driven (§4.4).

---

## 11. Decisions

**Made (2026-09-23):**
- 3D full-wave EM is a **v2-or-later** capability; the PRD's v1 non-goal stands for v1 (PRD §2, §17 v1.4).
- Full-wave solvers are **external processes** — not translated, not linked.
- **Gmsh is used as an external GPL program only** (§6.1).
- **OpenCASCADE is the geometry kernel** — through Gmsh first, through a native geometry worker when
  the editable modeler lands (§6.2).
- Thermal FEM is **native C#** (§9).
- The 3D viewer is **hosted in Avalonia through composition GPU interop**, drawn by circuitRF's own
  renderer, never by OCCT's viewer; the kernel is never on the drawing path (§8).

**Made (2026-09-24):**
- **Two full-wave backends, chosen by the user per setup**: Palace (FEM) and openEMS (FDTD), over
  **one solver-neutral 3D problem** circuitRF owns (§4). Running both on one setup is supported.
- **openEMS is used as an external GPL program only** — its XML written as text, its executable run,
  its scripting interfaces never used (§5.2).
- **The FDTD grid is generated by circuitRF**, in managed code (§6.5).
- **circuitRF distributes no solver and no mesher** — Palace, openEMS and Gmsh are user-installed and
  found as the Verilog-A compiler is, on every platform (§7.1).
- **User-facing text names the solvers**, unlike the Verilog-A compiler's (§7.1).
- **circuitRF offers to download and install the solvers from upstream**, and reports a failed install
  verbatim, with remedies only for failures its own installs have met (§7.2).
- **What circuitRF installed, circuitRF can uninstall** — only that, never a user's own install, and
  never a document (§7.2).
- **Uninstalling circuitRF removes the solvers it installed, with a warning** — through an in-app
  *Uninstall circuitRF…* command on every platform, which the Windows Apps-list entry is routed
  through; never during an upgrade; and where the platform allows no warning, leftovers are reused by
  a reinstall rather than hunted down (§7.2).
- **Windows users are not shut out of FEM**: circuitRF drives a Palace in the user's own Linux
  subsystem; openEMS runs natively; remote runs need nothing local (§7.4–§7.6).

**Open:**
1. The run verb's shape — `em` with a 3D setup, or a sibling verb (§5.3).
2. The GPU API behind the composition surface — native per platform, or WebGPU (§8.3), decided on the F2 spike.
3. The Tier B document's format and extension (§6.3).
4. Where the thermal solver lives in the source tree — `src/Engine` or a project of its own.
5. Whether F1 (Palace) or F1b (openEMS) ships first (§10).
6. The install assistant's CLI spelling, install and uninstall (§7.2).

## 12. Risks

- **Palace version churn** — configuration incompatible across versions; mitigated by a short
  validated-version list and a schema test (§5.3), at the cost of a writer and a validation run per
  version adopted. Because users install Palace themselves, circuitRF adopts new versions on its own
  schedule and refuses the rest by name — a user who upgraded early is told which version to use.
- **Installation burden** — until an upstream binary package exists, installing Palace means building
  it from source on every platform, a process reported to take most of a day and sometimes to fail
  (§7.4). The install assistant (§7.2) runs it for the user but cannot make it shorter or make it
  succeed; a user whose build fails and who gives up never runs an FEM solve at all. Mitigated by
  the known-failures table, by working upstream (FU) for a binary package, and by openEMS, thermal
  and remote runs, which are unaffected.
- **Windows machines that forbid virtualization** have no local Palace; remote (§7.5) and openEMS
  (§4.3) are what remain there, and a native Windows Palace depends on upstream (§7.4).
- **User-built configurations** — a user's Palace may be built without a feature circuitRF needs (an
  eigensolver, a direct solver); the capability check (§7.1) turns that into a refusal at setup time
  instead of a failure mid-run.
- **openEMS's maintenance** rests largely on one person; its stable XML limits the damage of a
  slowdown, and the neutral problem (§4.1) means losing it costs a backend, not the design.
- **FDTD grid quality** — a poor grid gives a plausible wrong answer; the grid generator's rules
  (§6.5) are tested against closed forms, and the viewer shows the grid (§8.5).
- **Cross-solver disagreement read as a bug in circuitRF** — mitigated by explicit port reference
  planes (§4.4) and by saying in the difference view which solver §4.3 expects to be more accurate for
  the geometry at hand.
- **Mesh robustness on real designs** — extruded layouts produce slivers, near-coincident faces and
  tiny gaps that a CAD-drawn model does not. A geometry clean-up pass (snap, heal, merge) before
  fragmenting is likely required and is the least predictable part of F1. The FDTD grid has the
  mirror-image problem: two nearly coincident edges demand two nearly coincident lines, and one tiny
  cell sets the time step for the whole run.
- **Problem size on a laptop** — a package model with an air box reaches millions of unknowns quickly;
  the remote location (§7.5) is how that is answered, and the refusal when a local run will not fit must
  name that remedy.
- **The GPL boundary** — enforced the way the UI firewall is: a test that fails if any circuitRF
  assembly references a Gmsh or openEMS library, and a scan that no Gmsh or openEMS source enters the
  tree.
