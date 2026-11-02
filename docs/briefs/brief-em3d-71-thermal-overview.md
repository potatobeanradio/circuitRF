# Brief — 3D thermal, fifth series: native thermal FEM on the 3D model (F3)

**Status:** Briefed, not built · **Date:** 2026-09-27 · **D1–D4 proposed, owner to confirm** (§3) ·
**Design note:** [`docs/design/em-3d.md`](../design/em-3d.md) rev 7 (Draft) — phase **F3**, §4.1a, §6.6, §8.5, §9
**Previous series:** [`brief-em3d-0-overview.md`](brief-em3d-0-overview.md) (1–10),
[`brief-em3d-20-overview.md`](brief-em3d-20-overview.md) (20–31),
[`brief-em3d-40-overview.md`](brief-em3d-40-overview.md) (40–53) and
[`brief-em3d-60-overview.md`](brief-em3d-60-overview.md) (60–70)
**Area:** `src/Thermal/` (new: the numeric solver, no UI), `tests/Thermal.Tests/` (new),
`src/Design/Thermal/` (new: lowering, run service), `src/Design/ThreeD/` (heat sources, probes, mesh
regions, the thermal setup), `src/Design/Layout/TechModel.cs` + `resources/technologies/generic-materials.cmat`
(thermal properties, interfaces), `src/Design/Em3d/GmshGeoWriter.cs` (a thermal lowering, mesh regions),
`src/Engine/Em3d/` (the mesh reader moves here), `src/Render/Scene3D/` (temperature, wire colouring),
`src/Ui/ThreeD/` (drawing sources/probes/regions, the Setups dialog's thermal page, *Plot Temperature*),
`src/WBond/` (per-wire current share, read only), `src/Cli/` (`em`, `check`, `explain`), `examples/`,
`testdata/thermal/`, `tests/`
**Requirement tag:** `R-em3d<n>-<m>` as before. This series is numbered **71–81**.

---

## 0. The short answer

The user builds **one** 3D model and solves it for EM, for heat, or both: the geometry, the materials and
the ports are the same; a **thermal setup** adds different excitations. When this series is done:

- **Power in, temperature out (scenario 1).** A die on a flange on a board with plated vias on a heatsink.
  The user draws a **heat source** over the FET's fingers, gives it a dissipated power, fixes the heatsink
  face's temperature, places **probes** on the die, the flange/board interface and the vias, and runs. The
  result is the temperature at every probe, over any swept variable, and derived quantities such as
  **Rth = ΔT / P** written as setup **measures**.
- **Channel versus surface (scenario 2).** One finger (or a whole device cut by symmetry planes) with the
  heat placed in a narrow strip at the drain-side gate edge, the GaN/SiC thermal boundary resistance as an
  **interface**, and a **spot probe** that averages the top surface over an IR microscope's spot. The
  channel-to-surface offset is a measure; the gradient is **visible** — on faces, on clip planes and along a
  picked line — for the user's own Rth arithmetic. The per-finger **Rth matrix**, **Z_th(jω)** and a
  radar **pulse-train peak temperature** are brief 80.
- **Current in, wire temperature out (scenario 3).** A bond-wire array carries **DC plus harmonic
  currents**, entered at an EM port as **peak or RMS** per harmonic, swept. Wires are **1D conduction
  elements** of their true 3D length and true round area; the result is the temperature **along every
  wire**, drawn **on the 3D wire**, and the hottest point of each. The same run can take its currents from a
  **harmonic-balance power sweep** of the schematic that instances this 3D view (brief 79).
- **Conductive balance.** Electrical conductivity σ(T) and thermal conductivity k(T) are solved **together
  with** the temperature by Newton's method — the owner's name for this is *conductive balance*; the
  user-facing name of the analysis is **electrothermal**. Each dependence has an **off switch**, so the
  user can see how much it contributes. Above the current at which no steady state exists (thermal
  runaway — the fusing limit), the result **says so**; it is an answer, not a failed run.
- **Plot Temperature.** After a thermal run, right-click a face ▸ *Plot Temperature*, or show it on every
  face, on a clip plane, as a hover readout, and as a line plot between two picked points. The colour
  range is the **true** minimum and maximum — the hot spot is the answer, and must never be clipped away.
- **Mesh regions.** A box drawn in the 3D view with a target element size; the fine mesh goes where the
  user puts it (the fingers) and the coarse mesh everywhere else (the heatsink). Palace setups honour them
  too.

### The rule this series adds

> **One model, one mesh pipeline, one set of materials.** A thermal run reads the same `.c3d`, the same
> technology records and the same Gmsh lowering as a Palace run; it adds excitations, it never adds a
> second copy of the geometry or of a material. A thermal object is invisible to the EM solvers, and an EM
> object means the same thing in a thermal run that it means in an EM run.

---

## 1. Things that are not obvious, resolved here once

### 1a. Gmsh is required for thermal (owner, 2026-09-27)

em-3d.md §7.6 said thermal "needs no install anywhere", while §9 put it on the Gmsh mesh pipeline, and
Gmsh is user-installed (§7.1). **The owner accepted Gmsh as a thermal requirement.** The install
assistant's Gmsh recipes already exist (`src/Design/Em3d/Install/recipes/gmsh-*`), the discovery already
finds it, and a thermal run with no Gmsh is the same refusal a Palace run gets, naming the same remedy.
The solver itself is managed code (§1f) and needs nothing else. em-3d.md §7.6 item 2 is corrected by
`R-em3d71-2`.

Not chosen, recorded so they are not re-proposed: a tetrahedral mesher inside the shipped geometry worker
(possible later — same licence class as OpenCASCADE; it is the route to "no install" if Gmsh ever proves a
barrier), and a finite-volume solve on circuitRF's own FDTD grid (no mesher, but it staircases every wire
and curve and wastes cells across the 10⁵ span of scales §9.2 describes).

### 1b. What is meshed: solids, never air

A thermal run meshes **every solid** in the elaborated problem and **no air**. Air has no conduction worth
solving here, and natural convection is not a conduction problem. Every exposed face is **insulated**
(adiabatic) unless the setup gives it a condition:

| Condition | Where | Value |
|---|---|---|
| **Fixed temperature** | a named face (`heatsink/zmin`) | °C, an expression |
| **Convection** | a named face, or *all exposed faces* | h in W/(m²·K) and the ambient °C |
| **Interface resistance** | between two touching solids | m²·K/W (§1e) |
| Radiation | — | **deferred** (§4) |

At least one fixed-temperature or convection face is required; a problem with neither has no steady
state, and is refused before meshing with that sentence.

Insulated-by-default is also what makes **symmetry** free: a device cut in half along a mirror plane is
exact with the cut face insulated. Brief 76 adds the declaration that lets the viewer and the reported
powers speak for the whole device.

### 1c. Excitations: geometry in the document, values in the setup

The same model carries several thermal setups (and EM setups beside them). So:

- **The document** holds the **places**: `HeatSources` (a sheet on a plane — a rectangle or a polygon — or a
  named solid, for a volumetric source), `Probes`, and `MeshRegions`. They are lists beside `Ports`, like
  `Ports`, not `C3dObject`s, because none of them is material: an EM lowering skips them, the precedence
  rules never see them, and a sheet drawn as a heat source can never be mistaken for a PEC sheet.
- **The setup** holds the **values**: each source's power (W, or W/m² / W/m³), each current terminal's
  currents, each boundary's temperature or h, the sweep. Every value is an **expression** in the
  document's variables (brief 51's machinery), so a power, a current or a heatsink temperature sweeps like
  any other variable.
- **Current terminals are the EM ports.** A port already names a positive and a negative conductor
  (`C3dPort.Positive`/`Negative`). A thermal setup's current excitation says *"port 1 carries this"*: the
  current enters the positive conductor across the port and leaves the negative one. This is also what
  makes the HB link (brief 79) a lookup rather than a mapping dialog — the schematic instance of this 3D
  view's S-parameter result has one pin per port, numbered as the ports are.

### 1d. The wire is a 1D element — true length, true area, drawn in 3D (owner, 2026-09-27)

A bond wire 25 µm across in a model centimetres wide is the worst thing a tetrahedral mesher meets, and its
radial temperature difference is negligible (its Biot number is tiny). So in a thermal run **a wire is a
chain of 1D conduction elements**, and:

- **Its length is the true 3D arc length** of its resolved centreline (`C3dWires.Resolve` — the same rings
  every solver and the viewer already use), including a ball end's vertical neck, **not** a plan-view
  length. The owner made this a condition of accepting 1D wires; brief 77 gates it.
- **Its area is πd²/4**, whatever its EM cross-section. The perimeter-matched hexagon (em-3d.md §6.6) has
  9.3 % less area, and a wire's temperature rise between fixed ends scales as 1/A², so meshing the hexagon
  would read a wire **about 21 % hot**. The hexagon is right for RF current and wrong for heat; the thermal
  lowering does not use it.
- **Its feet are bonded**: a wedge foot is a contact patch (foot length × wire width) on its pad's top face,
  embedded in the pad's mesh; a ball end contacts over the ball's footprint. The free span starts at the
  heel.
- **In an overmold** the wire loses heat sideways into the compound along its length (brief 77); in an air
  cavity it does not (convection on a wire is an opt-in h).
- **It is drawn in 3D.** The result carries T(s) along every wire, and the viewer colours the displayed
  wire solid by arc length — the wire the user sees is the wire that was solved, only its colour comes
  from the 1D solution (brief 75 draws it; brief 77 produces it).
- **Both sources of wires work**: a `.wBond`'s arrays reached through a layout instance, and wires drawn in
  the `.c3d` (brief 50). Both already resolve through the same function, so there is one wire model, not two.

### 1e. Interfaces: a material pair in the technology, an override in the document

A thermal boundary resistance — GaN on SiC, a die attach, a solder or thermal-interface layer, a voided
bond — is a **surface condition, not a meshed layer** (em-3d.md §9.2). Meshing a 1 µm attach under a
5 mm flange is exactly the sliver that breaks a mesher, and the resistance is what the user knows, not the
layer's thickness and conductivity.

- **The technology gains `ThermalInterfaces`**: a list of `{ MaterialA, MaterialB, ResistanceM2KW, Source }`
  records. GaN/SiC is a property of that material pair wherever the two touch, so it is stated once.
- **A `.c3d` may override one contact**: a named object pair and a resistance (a die's attach, a voided
  region). An override wins over the material pair for that contact only.
- The solver **duplicates the nodes** on the interface and joins the two sides with interface elements of
  conductance 1/R″ (brief 76).

### 1f. The solver: managed, second-order tetrahedra, direct or multigrid

Steady conduction is ∇·(k∇T) = −q: scalar, symmetric positive definite for constant k. Gmsh already writes
**second-order curved tetrahedra** for Palace; the thermal solver uses the same element order.

- **Below a crossover** (a few hundred thousand unknowns; brief 72 measures where) a sparse **Cholesky**
  factorisation (CSparse, already a dependency) — exact and robust.
- **Above it**, **conjugate gradients with a smoothed-aggregation algebraic multigrid (AMG)**
  preconditioner, written in managed code. Conductivity contrasts such as copper at ~400 W/(m·K) against a
  mould compound under 1 make simpler preconditioners crawl; AMG is the one that is robust to them.
- **Nonlinear** (k(T), σ(T), Joule heat): Newton's method, each step one of the above (the electrothermal
  Jacobian is not symmetric — brief 77 uses BiCGStab with a block AMG preconditioner above the crossover,
  sparse LU below).
- **Frequency domain** (Z_th(jω), brief 80): (K + jωC)T = q is complex symmetric; sparse complex LU below
  the crossover, COCG with the AMG of K above.

It lives in a **new project, `src/Thermal`** (numeric layer, below the firewall, no UI), with
**`tests/Thermal.Tests`** — closing em-3d.md open question 4. `Engine.Tests` already costs ~3.5 minutes;
thermal's own test loop should cost seconds. `src/Thermal` knows nothing about `.c3d`, Gmsh or materials by
name: it takes a mesh, per-element conductivity functions, sources and boundary conditions, and returns
nodal temperatures. The lowering and orchestration live in `src/Design/Thermal` (the `Em3dRunService`
pattern).

### 1g. Conductive balance: Newton over σ(T) and k(T), with switches (owner, 2026-09-27)

For the metals a bond wire is made of, **resistivity rises nearly linearly with temperature**
(≈0.34–0.39 %/K), and **thermal conductivity falls more slowly** — gold falls from about 312 W/(m·K) at
125 °C to about 262 W/(m·K) at 927 °C (owner's figures). Both enter the balance:

- **Unknowns**: temperature everywhere, and electric potential on the conductors and wires (for the DC
  part). **Residuals**: ∇·(σ(T)∇φ) = 0 on conductors; ∇·(k(T)∇T) + σ(T)|∇φ|² + q_RF(T) + q_src = 0.
- **Newton** on the coupled system, with the full Jacobian (dσ/dT, dk/dT, dq_RF/dT and the Joule term's
  dependence on φ). A damped step when the residual does not fall. **Converged is stated** as the
  residual and the last temperature update, in the run's notes.
- **Switches**: `SigmaOfT` and `KOfT`, both on by default. Off means the 20 °C value (σ) or the nominal
  value (k) is used everywhere, so the user can run twice and see each one's share.
- **Thermal runaway is a result.** Between fixed-temperature ends, a wire with ρ = ρ₀[1+α(T−T₀)] and
  constant k has the exact solution θ(x) = θ_b·cos(βx)/cos(βL/2), θ = T − T₀ + 1/α,
  β² = I²ρ₀α/(kA²): **no steady state exists once βL/2 ≥ π/2**. Newton's Jacobian goes singular as the
  current approaches that limit. A current sweep continues from the last converged point (warm start, the
  HB drive-ladder pattern); where a step cannot converge, the run **brackets the limit by bisection** and
  reports *"no steady state above ≈ X A (thermal runaway) — the last converged point is Y A"*. Points
  above it carry no temperature, and say why.
- A **fixed-point (Picard) path** is kept as the reference the Newton path is tested against, not as a
  user option.

### 1h. RF current in wires: skin effect per harmonic, inductive current share

- **Heat per unit length** in a wire is Σₙ ½|Iₙ|²·R′_ac(fₙ, T) with Iₙ the **peak** phasor of harmonic n
  (and I₀² R′_dc for DC). **R′_ac comes from wBond's exact Bessel solution** (`src/WBond/InternalImpedance.cs`),
  evaluated at the wire's solved temperature — the temperature wBond itself has always had to assume
  (85 °C, `Materials.DefaultOperatingTempC`).
- **Harmonics are entered separately**, each as **Peak or RMS** (stored explicitly — never inferred).
  A single total RMS is correct only at DC: R_ac > R_dc, so treating RF current as DC **understates** the
  heat, which is the unsafe direction. The UI shows the DC-equivalent RMS as a readout, never as the input.
- **Phase does not matter**: harmonics are orthogonal and add in power.
- **The share between wires.** At DC the conduction solve decides it (exact, including σ(T) — a hot wire
  takes less current). At RF it is decided by the **inductance matrix**, not by resistance: wBond's
  `ArrayReduction` already computes the per-wire share of an array's current, frequency-independent in the
  inductive regime. At RF, σ(T) moves the share by very little and moves the heat a lot — which is why
  **conductive balance runs inside the thermal solver**, with no EM re-solve per iteration. An outer EM
  loop is not built; the showcase measures what it would have changed.
- **Scope**: RF heat is applied to **wires** in this series; DC Joule heat is applied to **every**
  conductor. RF surface loss in pads, leads and flanges is deferred (§4).

### 1i. Measures, and temperatures in °C

A thermal setup has **measures** (brief 74): named expressions over probe results, evaluated by the one
expression engine — `Rth_jc = (Tmax(die_top) - Tavg(flange_bottom)) / Pdiss`, `dT_ch_surf =
Tmax(channel) - Tavg(ir_spot)`. **Degrees Celsius** at every boundary a user or a file touches
(`src/Core/Devices/Temperature.cs`'s rule); kelvin only inside the solver.

### 1j. Materials must carry what the solver reads

`generic-materials.cmat` carries **no thermal conductivity at all** today, and lacks the materials these
scenarios are made of. Brief 73 adds, each with a cited `Source`: k (and k(T) where it matters), density
and specific heat for every existing record, and new records for silicon carbide, gallium nitride, CVD
diamond, a gold–tin solder, a sintered-silver die attach, copper–tungsten and copper–molybdenum flange
alloys, a generic mould compound (with its glass-transition temperature as a note) and a generic thermal
interface material. The four bond-wire metals — gold, copper, aluminium and silver — get ρ(T) and k(T) tables to below
their melting points; gold's is checked against the owner's two figures. **When a material
states both a table and a coefficient (σ(T) or k(T)), the table wins** (owner, 2026-09-27); `check` warns
when they disagree at 20 °C.

### 1k. Nothing visible can be seen from an agent's session

As in every 3D series: gates are counters and headless checks, never timings and never pixels; every brief
with a visible surface ends with an **owner check list** for the **Debug** build; every completion note
says pixels were not seen. The solver is fully testable headlessly, and most gates live there.

---

## 2. The briefs

| # | Brief | Delivers | Depends on |
|---|---|---|---|
| 72 | [references and the solver spike](brief-em3d-72-thermal-references-and-spike.md) | externally generated reference data (slab, composite slab, spreading resistance, wire with ρ(T) and k(T), wire in a cylinder of mould, Z_th of a slab, the Shah paper's cases); Gmsh embedded-curve and embedded-surface checks; the direct/AMG crossover measured in a scratch harness; findings note | — |
| 73 | [materials and the document](brief-em3d-73-thermal-materials-and-document.md) | thermal properties in `generic-materials.cmat` and new materials; table-wins resolution; `ThermalInterfaces`; `.c3d` `HeatSources`, `Probes`, `MeshRegions`, interface overrides; the thermal setup schema; `check`/`explain`; reference pages | 72 |
| 74 | [the solver and the run](brief-em3d-74-thermal-solver-and-run.md) | `src/Thermal` (P2 tets, assembly, Cholesky / PCG-AMG, Newton on k(T)); the thermal Gmsh lowering; mesh regions in Palace too; the run service, `DataSet`, `.pvd`/`.vtu`, measures, sweeps, a mesh-convergence check; `em` runs a thermal setup | 73 |
| 75 | [the editor and Plot Temperature](brief-em3d-75-thermal-editor-and-plot.md) | draw heat sources, probes, mesh regions; face ▸ thermal boundary; the Setups dialog's thermal page; *Plot Temperature* on a face, all faces, a clip plane; hover readout; line plot; hot-spot marker; range fixed across a sweep; wires coloured by T(s) | 74 |
| 76 | [interfaces, via fields, submodels, symmetry](brief-em3d-76-thermal-interfaces-vias-submodels.md) | interface elements; a via field as an anisotropic effective block; two-step (package, then die) solves; symmetry planes | 74 |
| 77 | [electrothermal DC, 1D wires, conductive balance](brief-em3d-77-electrothermal-wires-conductive-balance.md) | DC current at ports; conduction solve on conductors; 1D wires (true length, πd²/4, bonded feet, overmold coupling); Newton over σ(T) and k(T) with switches; runaway bracketing; wire T(s) in the result | 74 (75 to draw it) |
| 78 | [RF harmonic currents](brief-em3d-78-thermal-rf-harmonic-currents.md) | harmonic currents at ports, Peak/RMS; R′_ac(f, T) from wBond's Bessel solution; per-wire share from `ArrayReduction` for `.wBond` arrays and `.c3d` wires | 77 |
| 79 | [from harmonic balance](brief-em3d-79-thermal-from-harmonic-balance.md) | a thermal setup takes its port currents from an HB sweep of the schematic instancing this view; wire temperature per power step with Pout carried; probe limits (mould Tg) flagged | 78 |
| 80 | [Rth matrix, Z_th(jω) and radar pulses](brief-em3d-80-thermal-rth-zth-pulse.md) | the N×N Rth matrix across sources; Z_th(jω); a Foster fit; the periodic pulse-train peak temperature; the Foster network written as a `.cnl` subcircuit (not attached — owner: later) | 74 (76 for symmetry) |
| 81 | [the showcase](brief-em3d-81-thermal-showcase.md) | three example workspaces (die→heatsink, channel vs surface, output wire array with an HB sweep); user pages; the owner walk-through | all |

**Tracks:**
- **Core:** 72 → 73 → 74. Nothing else starts before 74.
- **Surface:** 75 (after 74).
- **Package physics:** 76 (after 74, parallel with 75 and 77).
- **Wires:** 77 → 78 → 79.
- **Dynamics:** 80 (after 74).
- 81 comes last.

**Smallest demonstrable cut:** 72, 73, 74, 75 — scenario 1 and a simple scenario 2 with power sources,
fixed-temperature faces, probes and *Plot Temperature*. Then 77 (scenario 3 at DC), 78 (at RF), 79 (from
HB).

---

## 2A. Traceability — the owner's request, and where each part is built

| Request (paraphrased) | Where |
|---|---|
| Reuse the 3D view; one model for EM, thermal, or both — only the excitations differ | §1c, 73 |
| Scenario 1: power on a sheet over the fingers, fixed heatsink temperature, monitor vias, package interface and die; Rth die→package; de-embed the package/board interface temperature | 73, 74, 75, 76 (interfaces, via fields) |
| Scenario 2: channel temperature under a field plate vs the IR-visible surface; the offset; one finger or many | 73 (spot probe), 76 (TBR, symmetry, submodel), 75 (gradient views), 80 (Rth matrix) |
| Visualise the gradient to do Rth by hand | 75 (faces, clip planes, hover, line plot) |
| Rth matrix and a pulse RC network, if easy | 80 |
| Scenario 3: current into the bond pad, sweep it, monitor wire temperatures | 77, 78 |
| …better: link the schematic, HB power sweep, harmonic currents into thermal, wire temperature per power level | 79 |
| Couple electrical and thermal conductivity; Newton; "conductive balance" | §1g, 77 |
| Include k(T) (gold 312 → 262 W/(m·K), 125 → 927 °C); able to switch it off | §1g, 73, 77 |
| Validate against the Shah wire-bond fusing paper, or a similar equation | 72, 77 |
| Mesh boxes with different densities | 73, 74, 75 |
| Other excitations? Power, current (magnitude or RMS), harmonics | §1b, §1c, §1h, 73, 78 |
| Plot Temperature on a face once a thermal solution exists | 75 |
| Wires as 1D if the length is the 3D distance and the temperature is drawn along the 3D wire | §1d, 75, 77 |
| `.wBond` wires, and ideally `.c3d` wires | §1d, 78 |
| Radar pulses | 80 |
| A series of briefs | this document |

---

## 3. Decisions

**Made by the owner (2026-09-27):** Gmsh is required for thermal (§1a); wires are 1D with the true 3D
length and are drawn in 3D (§1d); k(T) is part of conductive balance and can be switched off (§1g); the
σ(T)/k(T) table wins over the coefficient (§1j); only the channel-to-surface offset and a visible gradient
are needed for scenario 2, with the Rth matrix and a pulse network if cheap (80); `.wBond` first, `.c3d`
wires too (78); closing the loop back into HB (wire R(T)) and the FET thermal node is **later** (§4);
pulses for radar are in scope (80); the analysis is called **electrothermal** and its Newton loop
**conductive balance**.

**Proposed — the owner confirms:**

| # | Decision | Brief's default | Blocks |
|---|---|---|---|
| D1 | Where a thermal setup lives | **In the `.c3d`'s `Setups`**, one name space with the EM setups, marked `Problem3D: Thermal`; it has a `Thermal` section and no `Solver3D` (the solver is circuitRF's). A `.cem` does not take a thermal setup | 73 |
| D2 | The run verb | **`em` runs a thermal setup** (`circuitrf em x.c3d --setup Hot`), as it runs any embedded setup; no new verb. em-3d.md open question 1 leans this way already | 74 |
| D3 | Where the solver lives | **`src/Thermal` + `tests/Thermal.Tests`** (§1f); em-3d.md open question 4 closes | 74 |
| D4 | Interface resistances | **A material-pair table in the technology, a per-contact override in the `.c3d`** (§1e) | 73, 76 |
| D5 | What is meshed | **Solids only**; exposed faces insulated by default (§1b) | 74 |
| D6 | RF heat | **Wires only** in this series; DC heat in every conductor (§1h) | 78 |
| D7 | The per-wire RF share | **Inductive, from `ArrayReduction`**, one share for every harmonic; DC share from the conduction solve (§1h) | 78 |
| D8 | Harmonic current entry | **Per harmonic, Peak or RMS per entry**, stored explicitly; the total RMS is a readout, never an input (§1h) | 78 |
| D9 | Temperature colour range | **True min/max** by default, never a percentile; *fixed across a sweep* is a toggle (75) | 75 |
| D10 | The Foster network | **Written as a `.cnl` subcircuit with a thermal-node pin**, not attached to any device (§4) | 80 |
| D11 | Probe limits | A probe may carry a **limit** (°C); the result flags the first sweep point where it is crossed (a mould compound's glass transition, a wire's rating) | 73, 79 |

---

## 4. What is deferred, and why

- **Closing the loop into the circuit** (owner: later): wire R(T) back into HB, and the Rth/Foster network
  attached to a FET's thermal node so DC/HB/loadpull see self-heating. Brief 80 writes the network; nothing
  attaches it.
- **An outer EM loop** in conductive balance (§1h): σ(T) barely moves an inductive current share. Brief 81
  measures the size of what it would change before anyone builds it.
- **RF surface loss in pads, leads and flanges** (§1h, D6): would come from Palace's surface-loss density.
- **Radiation** boundaries: small at these temperatures, nonlinear, and not asked for.
- **Transient**: out of scope (PRD). Pulses are answered in the frequency domain (80).
- **Adaptive mesh refinement** driven by an error estimate: brief 74's convergence check (re-mesh finer and
  report how far each probe moved) is the honest first step; an estimator-driven Gmsh background field is a
  later brief if the check proves too manual.
- **A tetrahedral mesher shipped in the geometry worker** (§1a).

---

## 5. Where the code goes

```
src/Thermal/                          NEW (74): numeric, no UI, references nothing above src/Engine
    ThermalMesh.cs                    nodes, P2 tets, tagged surfaces, embedded 1D wire chains
    ThermalAssembly.cs                stiffness, mass (80), sources, Robin/Dirichlet terms, interface elements (76)
    Solvers/                          Cholesky (CSparse), SmoothedAggregationAmg, Pcg, BiCgStab, Cocg (80)
    Nonlinear/                        Newton over k(T); conductive balance (77); Picard reference
    Electrothermal/                   conduction on conductors, 1D wire elements, Joule terms (77, 78)
    Frequency/                        Z_th(jω), Foster fit, pulse train (80)
tests/Thermal.Tests/                  NEW
src/Engine/Em3d/MshReader.cs          MOVED from src/Render/Scene3D (74) — one reader, used by both
src/Design/Thermal/                   NEW: ThermalLowering (C3d → mesh + tags), ThermalRunService, ThermalResult (74+)
src/Design/ThreeD/C3dDocument.cs      HeatSources, Probes, MeshRegions, ContactResistances (73)
src/Design/Layout/TechModel.cs        ThermalInterfaces (73)
src/Design/Layout/Em/EmSetupModel.cs  Problem3D.Thermal + the Thermal section (73)
src/Design/Em3d/GmshGeoWriter.cs      thermal mode; MeshRegions as Box fields for every setup (74)
src/Design/resources/technologies/    generic-materials.cmat: thermal properties, new materials (73)
src/Render/Scene3D/Fields/            temperature quantity, min/max range, wire colouring by arc length (75)
src/Ui/ThreeD/                        draw source / probe / region; boundary menu; Setups thermal page; Plot Temperature (75)
src/Cli/                              em runs thermal; check/explain report thermal setups (73, 74)
testdata/thermal/                     externally generated references + the scripts that made them (72)
examples/                             three thermal examples (81)
```

---

## 6. The series' own gate

**`R-em3d71-1`** Every brief in §2 exists and every link resolves.

**`R-em3d71-2`** When the owner confirms D1–D4, `em-3d.md` goes to **rev 8**: §7.6 item 2 says thermal
needs **Gmsh** (and nothing else); §9 gains §1b–§1h's decisions (solids only, excitations, 1D wires,
interfaces, conductive balance, RF heat); §10's F3 row points at this series; open question 4 closes (D3).
CLAUDE.md is **not** edited.

**`R-em3d71-3`** Byte identity: every existing Palace and openEMS golden, every tessellation count and every
shipped example's numbers are unchanged by the whole series. A `.c3d` with no thermal content writes
byte-identical `.geo` and Palace JSON — **mesh regions change the mesh only of a document that has them**.

**`R-em3d71-4` The owner's walk-through (owner check).** In the Debug build, with no terminal at any step:
1. open the *Die to Heatsink* example; run its thermal setup; right-click the die's top face ▸ *Plot
   Temperature*; read Rth from the measures table;
2. switch the via field to its effective block (brief 76) and rerun; compare the die temperature;
3. open *Channel vs Surface*; plot temperature on a clip plane through the finger; draw a line from the
   channel to the surface and read the offset from the line plot and from the measure;
4. open *Output Wires*; run the DC current sweep; see each wire coloured along its length; read the
   hottest wire; turn `KOfT` off, rerun, and compare;
5. run the HB-linked setup; read wire temperature against Pin, with Pout beside it;
6. run the pulse setup (brief 80); read the peak temperature at 10 % duty.

The owner records anything that did not feel immediate.

---

## 7. Scope for the series

- **No change to any existing answer** (gate 3).
- **No new native dependency.** The solver is managed; Gmsh is the existing external program.
- **No per-primitive edit verbs.** Thermal content is authored headlessly by writing the `.c3d`; the
  reference page describes every field.
- **No timing tests.** Counters only; crossover and cost measurements go in findings notes, from scratch
  harnesses.
- **Commercial names stay out of the repository** — no commercial thermal, EM or CAD tool is named, in code,
  tests, examples or "alternatives considered". Published papers are cited by author and title.
- **Nothing quotes the owner** in the repository; requests are paraphrased.
- **On completion of each brief, findings go in the relevant `RESOLVED.md`**, never `CLAUDE.md`. Doc
  sources are edited; DocGen is not run per brief.
- **Tests are minimal and targeted**: one test per claim; `dotnet test tests/Thermal.Tests` is the routine
  gate for solver work; never the full suite per brief.
- **References are externally generated** (CLAUDE.md §Validation): nothing in `testdata/thermal/` is
  produced by circuitRF.
