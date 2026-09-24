# circuitRF — 3D FEM: full-wave EM and thermal (design draft)

**Status:** Draft — rev 0, scoping only · **Date:** 2026-09-23 · **Target:** **v2 at the earliest;
parts of it are v3** (PRD §2, §17 v1.4). Nothing here is v1 scope and nothing in v1 depends on it.

> **This is a dated survey of a fast-moving landscape — re-survey before building anything.**
> Every tool choice below reflects what the open-source ecosystem offered on **2026-09-23**, and is
> expected to be revisited months later, when the answers may differ. What was actually checked on
> that date:
>
> | Tool | State surveyed | Re-check before relying on it |
> |---|---|---|
> | Palace | **v0.18.1** (released 2026-09-21; v0.16.1 Apr, v0.17.0 Jun, v0.18.0 Sep 2026), Apache-2.0, 23 contributors, source read directly | native Windows build? PML? thermal? config compatibility across versions? prebuilt binaries? |
> | Gmsh | GPL; used by every Palace example — **version not checked** | licence unchanged? a permissively licensed mesher now good enough to replace it? |
> | OpenCASCADE | LGPL-2.1 + exception — **version not checked** | any maintained C#/.NET binding (would remove the need for a native worker, §4.2)? |
> | Netgen / TetGen | LGPL-2.1 / AGPL, from general knowledge — not re-verified | — |
> | Avalonia GPU hosting | **12.0.3**: composition GPU interop (IOSurface/Metal, D3D11/Vulkan, DMA-BUF/Vulkan) and `OpenGlControlBase`, from Avalonia's own docs — not prototyped | still there? a built-in 3D surface? |
> | GPU API behind it | Metal/D3D/Vulkan direct, or WebGPU via wgpu (Silk.NET bindings) — general knowledge, not prototyped | maturity of .NET WebGPU bindings |
>
> Also worth a fresh search at revisit time: **other open-source 3D FEM electromagnetic solvers**
> (one with native Windows support or a permissive mesher would change §3 and §5), and
> **open-source thermal FEM solvers** (one that is embeddable would change §7's "write it" call).
> The *architecture* — solvers as external processes, circuitRF owning the document, the resolved
> expressions and the results — is meant to survive a change of tool; the tool names are not.

Companion to [`mom-engine.md`](mom-engine.md) (the 2.5D planar MoM arc) and
[`mom-wirebond-kernel.md`](mom-wirebond-kernel.md) (kernel W). Those remain circuitRF's EM solvers for
the geometry they fit. This document scopes what they cannot reach: **arbitrary 3D geometry solved by
the finite-element method** — packages, lids and cavities, connectors and board transitions, stepped
grounds beyond what kernel W's conductor meshing covers — and, on the same geometry and mesh pipeline,
**3D steady and frequency-domain thermal** analysis from the channel under a FET's field plate down
through die, attach, package, board vias and heatsink.

---

## 0. The one-paragraph version

circuitRF does not write a full-wave FEM solver. It drives **Palace** (open source, Apache-2.0, an
MPI-parallel 3D FEM electromagnetics solver) as an **external process** — the same arrangement as
circuitRF's device workers, and for the same reasons. Palace brings no mesher and no geometry, so
**Gmsh** (GPL) runs as a second external process, and its built-in **OpenCASCADE** kernel
(LGPL-2.1 + exception) is circuitRF's solid-geometry engine from day one. circuitRF owns what it
already owns everywhere else: the human-readable design document, the expression engine that resolves
every dimension, the run service that turns results into Touchstone files and `DataSet`s, and the
viewer. **Thermal is the one FEM circuitRF does write itself**, in C#, because steady heat conduction
is a scalar, symmetric-positive-definite problem an ordinary preconditioned conjugate-gradient solver
handles — and because its most valuable output is not a picture but a **thermal network for the FET
thermal node**, which only circuitRF can consume.

---

## 1. What already exists, and where FEM starts

| Geometry | Solver today | FEM's role |
|---|---|---|
| Planar metal on a layered stack, vias | Planar MoM (kernels A–C) | Independent reference only |
| Bond-wire arrays, overmold, stepped ground meshed as a conductor | Kernel W | Independent reference; the regression anchor kernel W already calls for |
| Leadframes, clips, stepped cavities, lids, connectors, board-to-package transitions | **None** — `mom-wirebond-kernel.md` lists "complex 3D metal" as where MoM's advantage erodes | **Primary solver** |
| Heat flow die → heatsink | **None** | **Primary solver** (native C#, §7) |

MoM stays the default wherever it fits: its unknowns scale with conductor surface, FEM's with the
volume of everything including the air. FEM is added for the geometry MoM cannot represent, not as a
replacement.

---

## 2. Palace — what it is, measured against what circuitRF needs

Surveyed at **v0.18.1**, on 2026-09-23 — see the survey note at the top. ~78,000 lines of C++ of its own, on a large numerical stack
of open-source libraries: MFEM (discretization), hypre (the auxiliary-space preconditioner for curl-curl
problems), a parallel sparse direct solver (SuperLU_DIST, MUMPS or STRUMPACK), SLEPc/PETSc or ARPACK
(eigenproblems), libCEED, MPI. Nothing in that stack is GPL.

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
- **No PML** — absorbing boundaries only. Radiating problems need a larger air box.
- **No native Windows build** — its own documentation supports Windows only through a Linux
  subsystem, best effort, with no binaries (§5).
- **No mesher and no geometry** — every Palace example builds its mesh with Gmsh scripts (§4).
- **No thermal** (§7).
- **Configuration files are not compatible across versions**, by its own statement (§3.3).

---

## 3. Why an external process, and not a translation or a library

### 3.1 Translating it to C# — rejected

The 78,000 lines are the thin part. What makes Palace good is the stack beneath it: hypre's
auxiliary-space preconditioner, a parallel multifrontal direct solver, MFEM's high-order Nédélec
elements — millions of lines of C, C++ and Fortran with no .NET equivalent. CSparse.NET (single
threaded, no supernodes) cannot factor a 3D FEM system of useful size: 3D fill-in grows as
N^(4/3) in memory under the best ordering. A translation is years of work and a permanent fork of a
project that releases roughly every two months.

The honest version of "write our own FEM in C#" is narrower, and is §7: yes for thermal, no for
full-wave.

### 3.2 Loading it as a library — rejected

A library would share circuitRF's process with MPI, with solver `abort()`s, and with global state.
Palace is already a program — `mpirun -n N palace config.json` — so the worker *is* the Palace
executable and there is nothing to wrap. Process isolation is the property the device workers are
built on (`tools/DeviceWorkerExample/README.md`, "Why a worker is a separate process"); it applies
here unchanged, and it is also what makes a **remote** run (a Linux workstation, a cluster) the same
code path as a local one.

### 3.3 The contract circuitRF holds

- **One validated Palace version at a time.** circuitRF writes configuration for a named version,
  checks `palace --version` before a run, and refuses a mismatch by name rather than letting a
  silently-misread key produce a plausible wrong answer. Palace embeds a JSON schema for its
  configuration; circuitRF's writer is tested against that schema for the pinned version.
- **Results land where the planar EM run's land.** `port-S.csv` becomes a `.sNp` at the path
  `EmRunService.ResolveSnpPath` predicts, so a schematic's reference survives a re-run, and a
  `DataSet` is built from it like any other run. Field files are kept for the viewer (§6).
- **A run is a CLI verb before it is a button** (the rule `docs/design/cli.md` states for every run
  verb): `em` on a setup whose solver is FEM, or a sibling verb — decided when F1 is briefed.

---

## 4. Meshing and geometry — Gmsh, with OpenCASCADE inside it

### 4.1 Gmsh as an external GPL tool (decided)

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
- Whether Gmsh is **user-installed** (located like the PCell Python interpreter) or **bundled** as a
  separate executable in the installers is an open decision (§9). Bundling a GPL program beside an
  MIT one is permitted aggregation but carries source-offer obligations the installers must meet.

### 4.2 OpenCASCADE, as an external tool

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
provenance §4.4 depends on.

The limitation is that Route A is **batch**: each edit re-runs `gmsh`. That is fine at mesh time and
for a read-only preview of generated geometry, and too slow and too coarse-grained for an interactive
modeler that must retessellate and hit-test on every drag.

**Route B — a geometry worker (F4, v3).** A small C++ program, `tools/geometry-worker`, links OCCT
and stays running for the life of a modelling session, spoken to over stdin/stdout the way the device
workers are:

- *build* — takes a **fully resolved** construction tree (numbers only; see §4.3) and returns, per
  face, a triangle tessellation tagged with the construction object it came from;
- *pick / measure* — hit-tests and distances against the exact solid, not the tessellation;
- *export* — writes a BREP or STEP file that Gmsh meshes (Route A's pipeline, unchanged).

OCCT's licence (LGPL-2.1 with an additional exception) allows linking it into this worker; OCCT is
shipped as shared libraries beside it, and the worker's own source is MIT. OCCT builds natively on
Windows, macOS and Linux, so — unlike Palace — the geometry worker can ship in every installer. It
is a **native dependency** in the CLAUDE.md sense and is agreed in principle (PRD §17 v1.4); the
build and packaging cost is measured when F4 is briefed, the way `tools/senior-worker`'s was.

### 4.3 Where the geometry comes from

**Tier A — generated from what circuitRF already holds (v2).** The layout (`.clay`), the
technology's stackup (`.ctech`) and kernel W's wirebond parameters already describe most package and
board geometry, and the planar EM extractors already turn them into an `EmProblem`. The 3D model is
generated from the same inputs, on demand, exactly as kernel W generates a wire's 3D path: each
layer's polygons extruded through its stackup thickness, vias as prisms, wire paths as circular
sweeps, balls and bumps as spheres or truncated spheres, mould compound and lids as boxes. **The user
draws no solid.** Every dimension is already a resolved circuitRF value, so a parametric sweep
regenerates and remeshes per point with no new machinery.

**Tier B — a 3D view the user edits (v3).** A new cell view holding a **construction history**:
named primitives and operations (box, cylinder, polygon extrude, sweep, boolean, fragment), each
dimension an **expression** in circuitRF's one expression engine, scoped by the cell's parameters
like every other view. The file is human-readable, as every circuitRF document is; its format and
extension are decided with F4. The rule the circuit side already lives by applies unchanged —
**elaborate first**: circuitRF resolves every expression and hands the geometry kernel numbers only,
just as the numeric layer never sees an unresolved parameter.

### 4.4 Naming — the problem every history-based modeler has

When a dimension changes, a solid's faces are rebuilt and their indices change. A boundary condition
stored as "face 17" silently moves to a different face. **circuitRF never stores a face or volume
index.** Materials, ports and boundaries attach to **named construction objects** (a solid, or a
named sheet drawn for the purpose — a port sheet, a radiation box face), and the mapping to mesh
groups is rebuilt on every run from the kernel's provenance (Gmsh's fragment map in Route A; OCCT's
`Modified`/`Generated`/`IsDeleted` history in Route B). A named object that no longer yields any face
is a refusal naming the object, never a guess.

---

## 5. Where Palace runs

Palace is the one component circuitRF cannot ship on every platform, so "where does the solver live"
is a setting from the first phase, with one run path behind it:

| Location | Platforms | Notes |
|---|---|---|
| **Local, user-installed** | Linux, macOS | Located like the PCell Python interpreter; version checked (§3.3). |
| **Local container** | Windows, macOS, Linux | An image pinned to the validated version; the Windows route. Memory and CPU limits of the container host bound problem size — reported, not hidden. |
| **Remote host** (SSH) | any client | Mesh and configuration go out, CSV and fields come back. The route for problems a laptop cannot hold. |
| **Shipped by circuitRF** | Linux, macOS | Possible later; a native Windows build is not available. |

Every location runs the same files, so a result does not depend on where it was computed.

---

## 6. The 3D viewer — hosted in Avalonia, and fast

**The bar is interactive feel, not just correctness**: orbiting, hovering and selecting should feel as
immediate as the snappiest open-source 3D modelling tools (Blender is the reference the owner named).
Everything in this section follows from one rule: **the screen never waits on geometry.**

### 6.1 Avalonia hosts the viewport, not OpenCASCADE

OpenCASCADE computes shapes; circuitRF's own renderer draws them. OCCT's bundled visualization layer
is **not** used: it would put OCCT back in circuitRF's process (the geometry worker of §4.2 exists to
keep it out) and it owns a native window, which is the hosting option §6.3 rejects. The worker hands
back triangles tagged by construction object; the renderer never calls the kernel.

**Not SkiaSharp.** Skia is a 2D rasterizer with no depth buffer; its 4×4 matrix only puts 2D layers in
perspective. Software depth sorting draws a wireframe of a few hundred solids and fails at what the
viewer must show: intersecting translucent solids, and fields on 10⁵–10⁶ elements.

### 6.2 What makes a 3D tool feel fast, as requirements

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

### 6.3 How Avalonia hosts it

Checked against Avalonia 12.0.3 (the version this repo builds with) on 2026-09-23:

| Option | How | Verdict |
|---|---|---|
| **Composition GPU interop** | circuitRF's own GPU device renders on its own thread into a shared texture; the compositor imports it (`CompositionDrawingSurface` — IOSurface + Metal shared events on macOS, D3D11 or Vulkan handles on Windows, DMA-BUF or Vulkan on Linux) and only composites it | **Chosen direction** |
| `OpenGlControlBase` | Avalonia supplies an OpenGL context inside its own render pass | Simplest to prototype; OpenGL is frozen at 4.1 on macOS |
| `NativeControlHost` | A native child window with its own swapchain | **Rejected**: Avalonia content cannot draw over it, and Dock's floating panels re-parent windows, which a native child survives badly |
| Skia | 2D only | Rejected (§6.1) |

**Why composition interop, in this repo specifically.** circuitRF has already measured that **an
application has one compositor**: a 1,500 ms layout frame took the Project Tree, the Messages panel and
File ▸ Quit down with it, and the only cure was bounding the frame (`src/Ui/RESOLVED.md`, "The whole
UI crawled…"). With composition interop the compositor's share of a 3D frame is placing a finished
texture. A 3D frame still rendering, or a kernel still rebuilding, means the previous image stays up —
it cannot starve the rest of the window.

The GPU API behind the shared texture (Metal/D3D/Vulkan directly, or WebGPU over wgpu, which targets
all three) is decided on the F2 spike (§6.6).

### 6.4 Three loops at three speeds

1. **Frame loop** — GPU, at display refresh: camera, hover, gizmos, selection outline. Near-zero
   managed work per frame, which also keeps the Debug build (the one the owner runs) responsive.
2. **Input loop** — UI thread: turns pointer and key events into small messages to the frame loop and
   the kernel loop. It computes no geometry.
3. **Kernel loop** — the out-of-process geometry worker (§4.2), asynchronous:
   - every edit carries a **generation number**; a result for a superseded generation is discarded;
   - a **coarse tessellation first**, refined afterwards;
   - each construction-history step is **cached**, so an edit rebuilds only the steps after it.

Keeping OCCT out of process is therefore a responsiveness property as well as an isolation one: kernel
work physically cannot block the UI thread.

### 6.5 Split across the firewall

As `src/Render` is today: tessellation buffers, the scene model, colour maps, camera and picking
geometry live below the UI firewall (no Avalonia); only the GPU device and the composition surface
live in `src/Ui`. That keeps a headless `render` of a 3D view possible later without a second renderer.

**What it shows, in order:** the generated geometry (Tier A preview), the mesh, then fields read from
Palace's VTK XML output — |E|, surface current, and for thermal, temperature — on boundary surfaces
and clip planes (never the volume's millions of elements directly).

### 6.6 Proving it before building it

- **Spike first (opens F2):** one Avalonia pane on composition interop that orbits a million triangles
  and GPU-picks under the cursor, on all three operating systems, before any viewer code is written.
- **Gates are counters, not timings** (timing tests measure the machine and flake): *an orbit uploads
  zero bytes*, *a hover makes zero kernel calls*, *a drag makes zero kernel calls until release*,
  *a superseded generation's result is never drawn*.
- **Feel is judged by hand.** A headless session can build and test the viewer but cannot see it;
  "does it feel fast" is an owner check on real hardware, recorded as such.

---

## 7. Thermal — native C# FEM

### 7.1 Why this one is written, not borrowed

Palace does not solve heat flow. Steady conduction, ∇·(k∇T) = −q, is the easiest problem FEM has: one
unknown per node, a symmetric positive-definite matrix, solved by conjugate gradients with an algebraic
multigrid or incomplete-Cholesky preconditioner. Linear or quadratic tetrahedra on the **same Gmsh
mesh pipeline** (§4) are enough. It needs no MPI, no curl elements and no external solver, so it can be
ordinary managed code in the numeric layer and run on every platform circuitRF ships on.

### 7.2 The physics that decides channel temperature

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

### 7.3 What it produces that only circuitRF can use

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

## 8. Phases

| Phase | Content | Version |
|---|---|---|
| **F0** | Spike, no product code: Palace and Gmsh installed by hand; hand-written `.geo` models of one bond-wire-over-ground case and one via transition; compared against kernel W and planar MoM. Measures install cost, laptop run time and memory, and agreement — and produces **externally generated reference data** for the existing MoM kernels, useful even if nothing further is built. | any time |
| **F1** | FEM backend, CLI first: Tier A geometry → `.geo` → Gmsh → Palace → `.sNp` + `DataSet`. Driven S-parameters, plus electrostatic/magnetostatic for package RLC. Solver-location setting (§5). | v2 |
| **F2** | Read-only 3D viewer: geometry, mesh, fields — opened by the hosting spike of §6.6. | v2 |
| **F3** | Native thermal FEM on the F1 mesh pipeline; thermal-resistance matrix and Z_th(jω) fitted to a network on the FET thermal node. | v2 or v3 |
| **F4** | Editable 3D view (Tier B), construction history with expressions, `tools/geometry-worker` (OCCT, Route B). | v3 |

### Validation

Per CLAUDE.md, references are externally generated. For F1: closed forms (coaxial and rectangular
waveguide impedance and cutoff, cavity eigenfrequencies, parallel-plate capacitance) and F0's
hand-built Palace runs. For F3: the one-dimensional slab and the analytical spreading-resistance
solutions for a rectangular source on a layered substrate. FEM-vs-MoM agreement on shared geometry is
a cross-check, not a reference — both are circuitRF-driven.

---

## 9. Decisions

**Made (2026-09-23):**
- FEM is a **v2-or-later** capability; the PRD's v1 non-goal stands for v1 (PRD §2, §17 v1.4).
- Full-wave FEM is **Palace, as an external process** — not translated, not linked.
- **Gmsh is used as an external GPL program only** (§4.1).
- **OpenCASCADE is the geometry kernel** — through Gmsh first, through a native geometry worker when
  the editable modeler lands (§4.2).
- Thermal FEM is **native C#** (§7).
- The 3D viewer is **hosted in Avalonia through composition GPU interop**, drawn by circuitRF's own
  renderer, never by OCCT's viewer; the kernel is never on the drawing path (§6).

**Open:**
1. Gmsh user-installed or bundled (§4.1), and the same question for Palace on Linux/macOS (§5).
2. The run verb's shape — `em` with an FEM setup, or a sibling verb (§3.3).
3. The GPU API behind the composition surface — native per platform, or WebGPU (§6.3), decided on the F2 spike.
4. The Tier B document's format and extension (§4.3).
5. Where the thermal solver lives in the source tree — `src/Engine` or a project of its own.

## 10. Risks

- **Palace version churn** — configuration incompatible across versions; mitigated by pinning and a
  schema test (§3.3), at the cost of a deliberate upgrade step per release adopted.
- **Mesh robustness on real designs** — extruded layouts produce slivers, near-coincident faces and
  tiny gaps that a CAD-drawn model does not. A geometry clean-up pass (snap, heal, merge) before
  fragmenting is likely required and is the least predictable part of F1.
- **Problem size on a laptop** — a package model with an air box reaches millions of unknowns quickly;
  the remote location (§5) is how that is answered, and the refusal when a local run will not fit must
  name that remedy.
- **Windows** — only through a container or a remote host, for as long as Palace has no native build.
- **The GPL boundary** — enforced the way the UI firewall is: a test that fails if any circuitRF
  assembly references a Gmsh library, and a scan that no Gmsh source enters the tree.
