# circuitRF — Project Requirements Document (PRD)

**Status:** Approved — v1.3 baseline · **Owner:** (you) · **Date:** 2026-08-04
**Scope of this document:** defines *what* circuitRF v1 must do and how we'll know it's done. It does **not** specify the data model or algorithms (those live in `docs/design/`).

> **v1.2 → v1.3 (2026-08-04):** added **3D wirebond EM** as a fourth MoM kernel (§5, §8, §9), and **narrowed the §2 non-goal** from "no 3D full-wave EM" to *no FEM, no volumetric meshing, no arbitrary 3D geometry* — the previous wording would have excluded a thin-wire solver that requires none of those things. **Layout remains 2D**: a wirebond is a parametric *component* whose layout view is its 2D projection, so no 3D shape type enters the layout database and no volume mesher is written. Design detail in [`design/mom-wirebond-kernel.md`](design/mom-wirebond-kernel.md); phases **LW1/LW2** in the development plan. No change to the five heroes, the engine, or any other scope.
>
> **v1.1 → v1.2:** added **Hero 1B** — a ~10,000-component mechanically-generated linear network as a **performance/scale anchor** (validates the §14 10k-component / <10 s NFR), distinct from the correctness heroes; targeted at Phase 2 (§4). Recorded the engine-wide **magnitude (= peak) phasor convention** and the **RF-power-source available-power formulation** (both resolved in `linear-engine.md` rev 3). No scope change to the correctness heroes.
>
> **v1.0 → v1.1:** result model clarified during data-model design — a run returns a **`DataSet`** of named **`DataCube`s** (each a single-kind `Real` or `Complex` array), replacing "one DataCube" language (§11, §13); **`.npy` exports the whole `DataSet` as one packed structured array** (§11). No scope change.
>
> **v0.3 → v1.0:** approved as the v1 scope baseline; no content changes from draft v0.3.
>
> **Changelog v0.2 → v0.3:** added **cell parameters** / hierarchical parameter passing (§7, §8, §13); made **conditionals a v1 requirement** in the expression language (§7); resolved the Hero 2/4 reference-model match (identical SDD FET on both sides, §4); set frequency/tone defaults (2 GHz center; two-tone Δf = 10 MHz; IM2–IM5) and added **Hero 5 (two-tone IM)** (§4–5); `.npy` complex cubes export as a **single structured array** (§11); closed most open items (§17).
>
> Items still marked **[PROPOSED]** are defaults to confirm later.

---

## 1. Vision

circuitRF is a lightweight, cross-platform Electronic Design Automation (EDA) circuit simulator for RF design. It studies the frequency response and nonlinear behavior of RF circuits — from simple topologies (10–20 components) to hierarchical multiport designs (10,000+ components) — using DC, S-parameter, and harmonic balance analyses. It targets RF practitioners and their managers, academic researchers, and capable hobbyists who today either can't justify a $25k/seat industrial tool or find those tools too heavy for quick investigation.

circuitRF's distinguishing promises are: (1) it is an **RF simulator, not a SPICE simulator** — the analyses and workflow are built around the RF/microwave problem; (2) it makes **loadpull and sourcepull simulation as easy as possible**; (3) it is **lightweight, low-cost, and human-readable** in its file formats; (4) it is **easy** — measured as a low number of clicks/inputs to a useful result — while still exposing the full configuration advanced users expect; and (5) it closes the loop from **schematic to physical layout to electromagnetic simulation** in one tool, for both PCB and MMIC work, without a separate layout package or EM licence.

## 2. Non-goals (explicitly out of scope for v1)

circuitRF v1 is deliberately bounded. The following are **not** in v1:

- **Not a SPICE simulator.** Transient analysis is deferred; v1 ships DC, S-parameters, and harmonic balance only.
- **Full Verilog-A is deferred to v2.** v1 ships built-in nonlinear models plus the Symbolically-Defined Device (§6). The **ASM-HEMT** GaN model rides on the v2 Verilog-A/OSDI backend (§6.1) — not in v1.
- **Layout is 2D. EM is 2.5D planar plus 3D wirebonds — and no FEM.** The layout view is fully implemented in v1 (§8, §9) with a **2.5D method-of-moments** solver for planar geometry and a **thin-wire MoM kernel for bond wires** (§5). Out of scope: **FEM, volumetric meshing, and arbitrary 3D geometry** — the user cannot draw a 3D solid, the layout database stores no 3D shape type, and nothing meshes a volume. A wirebond is admitted as a **parametric component** whose layout view is its 2D projection and whose 3D path is generated from named parameters (loop height, profile, diameter), which is why it needs none of the excluded machinery.
- **No layout auto-router.** Schematic→layout places components; the user routes. Obstacle-aware auto-routing applies to schematic wiring only.
- **A third-party cell database is not the storage layer.** v1 uses circuitRF's own human-readable native format. An optional third-party *cell import/export bridge* may come later; full support is out of scope.
- **No co-simulation, no system/behavioral-level modeling (e.g., X-parameter generation), no optimization/yield engine** in v1.

## 3. Users & jobs-to-be-done

- **RF engineer / practitioner** — designs and verifies an RF circuit (PA, mixer, filter, oscillator, matching network), then compares simulation against lab measurements. Needs trustworthy S-parameters and HB, and frictionless measured-vs-simulated overlays.
- **Engineering manager** — reviews a design and its verification results; values clarity of results and low tool cost.
- **Academic researcher** — investigates new topologies and unexplained nonlinear behavior; needs the SDD, two-tone HB, swept variables/parameters, internal solver settings, and export of results to MATLAB/Octave/Python.
- **Hobbyist (e.g., ham operator)** — investigates and troubleshoots an RF design; needs the "easy" path to work with almost no configuration.

## 4. Hero circuits & acceptance criteria

These circuits define "done" for the v1 engine; every proposed feature is gated against "does a hero need it for v1?" Each hero ships with a reference in `testdata/` and a regression test. **References are externally generated** — produced independently of circuitRF, then committed as fixed data — with the **identical SDD FET definition on both sides**, so an HB comparison tests circuitRF's HB math rather than a different transistor. This is what constrains the SDD language: its equations must be expressible in whatever equation-defined-device form the reference is authored in. **All heroes use a 2 GHz center frequency** unless noted.

### Hero 1 — Linear / S-parameters
**Circuit:** a 4-port RLC matching network that *embeds a Touchstone (SNP) block*, compared against a 4-port Touchstone reference.
**Exercises:** complex sparse MNA, R/L/C stamps, SNP block with frequency interpolation, 4-port S-parameter extraction, renormalization, Touchstone I/O via the shared core.
**Acceptance:** across the full sweep, `max |S_sim(i,j,f) − S_ref(i,j,f)| < 1e-6` (linear magnitude) for all 16 S-parameters. CLI: `circuitrf sparam hero1.cnl --freq <sweep> -o out.s4p`.

### Hero 1B — Linear scale / performance anchor
**Circuit:** a large, mechanically-generated linear network (~10,000 R/L/C components — e.g. a long ladder or mesh) over a few-hundred-point frequency sweep. **Owner-generated** by a script, not hand-drawn.
**Why it exists:** none of the correctness heroes stress *scale* — they are small circuits chosen to pin numerical accuracy. The §14 NFR (10k components, few-hundred frequencies, < 10 s) has nothing else testing it, so a scale problem could hide until a user hits it. Hero 1B is the **performance/scale anchor**, deliberately separate from the correctness heroes.
**Exercises:** the symbolic-once / numeric-per-frequency sparse pipeline, AMD ordering, factor-once/multi-RHS extraction, and the Group-2 branch-unknown count at scale.
**Acceptance:** solves within the §14 budget (**< 10 s** for ~10k components × few-hundred frequencies on a typical laptop) **and** passes an internal-consistency check (e.g. reciprocity of the extracted S-matrix, or agreement with a coarser independent solve). **Acceptance is performance + consistency, NOT a `1e-6` reference match** — Hero 1B has no external golden reference; it is not a correctness anchor. Targeted at **Phase 2**, alongside Hero 1.

### Hero 2 — Single-tone harmonic balance, single-FET PA
**Circuit:** a FET PA whose extrinsic network is a *linear RLC network including mutual inductance*, driven single-tone at 2 GHz, swept over available input power, reporting **Pout, gain, drain efficiency, PAE**.
**Exercises:** nonlinear FET (SDD) + DC operating point, the mutual-inductance (0-port) component, single-tone HB (conversion-matrix Jacobian), power-step continuation, FOM extraction.
**Acceptance:** converges at **every** point of a power sweep from small-signal into ≥3 dB compression at **H = 7 harmonics** [PROPOSED, configurable]. Pout and gain within **±0.01 dB**; drain efficiency and PAE within **±0.1 percentage points (absolute)**. *(Power-sweep range is TBD pending the FET model — §17.)*

### Hero 3 — Fundamental-impedance loadpull
**Circuit:** the Hero-2 PA, with load Γ_L swept over a Smith-chart grid; Pout and PAE as **contours on a Smith chart**.
**Exercises:** generic sweep over (real, imag) of Γ, the DataCube, loadpull-as-experiment, contour extraction, Smith-chart rendering via splotRF.
**Acceptance:** **≥100 Γ points within |Γ| ≤ 0.9** [PROPOSED] converge at **≥95%** (previous-point continuation); Pout/PAE contours match the reference within Hero-2 tolerances. Runs from CLI and renders in the Data Display.

### Hero 4 — Single-tone harmonic balance, 2-stage PA
**Circuit:** input MN → stage-1 FET → interstage MN → stage-2 FET → output MN, driven single-tone at 2 GHz, swept over input power, same FOMs as Hero 2.
**Why it exists:** the strongest test of HB **linear/nonlinear partitioning** — the engine must place *both* FETs in the nonlinear partition, characterize the surrounding linear subnetwork (input + interstage + output + bias) as one multiport linear block interfacing all nonlinear-facing nodes, and transfer signal between stages through the linear interstage network at every harmonic. Also exercises hierarchy flattening (five sub-cells), the multi-device block-structured Jacobian, and cascaded compression — none of which a single-FET hero can surface.
**Acceptance:** **same as Hero 2** (every point into ≥3 dB compression at H = 7; Pout/gain ±0.01 dB; efficiency/PAE ±0.1 pp absolute).

### Hero 5 — Two-tone intermodulation
**Circuit:** the Hero-2 PA (same SDD FET, same extrinsic network), driven **two-tone**: f₁ = 1.995 GHz, f₂ = 2.005 GHz (2 GHz center, **Δf = 10 MHz**), reporting **IM2, IM3, IM4, IM5** products.
**Exercises:** two-tone HB — the `{k₁f₁ + k₂f₂}` spectrum, the truncation/index map, the almost-periodic transform, and the baseband/harmonic-zone mixing products. Capturing the close-in fifth-order products (3f₁−2f₂ = 1.975 GHz, 3f₂−2f₁ = 2.025 GHz) requires a **two-tone mixing order ≥ 5**; capturing the even-order baseband product (f₂−f₁ = 10 MHz) validates the engine's handling of baseband terms — directly relevant to the source/load baseband-termination effects this tool targets. Close-in third-order products sit at 2f₁−f₂ = 1.985 GHz and 2f₂−f₁ = 2.015 GHz.
**Acceptance [PROPOSED]:** at the chosen drive level(s), HB converges and product levels (in dBc relative to the carriers) match the reference within: **IM3 ±0.5 dBc**, **IM2/IM4/IM5 ±1.0 dBc** (high-order, low-level products are numerically delicate). Included **only if** an external IM reference is obtainable; otherwise treated as a circuitRF self-consistency target.

> **Validation-methodology note (resolved).** Because the *same SDD FET equations* are used on both sides, the tight Hero-2/4 tolerances (±0.01 dB / ±0.1 pp) test circuitRF's HB engine, not two different transistor models. Requirement this creates: the SDD's v1 expression language (§7) must be expressible such that its equations can be transcribed into other tools' equation-defined devices — straightforward for algebraic `i = f(v)` models, which is what the heroes use.

## 5. Simulation scope (functional requirements)

- **DC analysis** — linear and nonlinear (Newton-Raphson with gmin / source stepping); prerequisite to HB.
- **S-parameter analysis** — linear, via MNA over a frequency sweep; complex; multiport; renormalization to per-port (optionally complex) reference impedance.
- **Harmonic balance** — multidimensional Newton with a conversion-matrix Jacobian; linear/nonlinear partition; nonlinear devices evaluated in time domain and transformed via FFT.
  - **Single-tone** — required.
  - **Two-tone** — required. The truncation scheme must reach **mixing order ≥ 5** and retain baseband and harmonic-zone products, to support the Hero-5 IM2–IM5 validation.
  - **Continuation** — power/source stepping required for convergence at drive.
- **Parametric sweeps** — a generic sweep wraps *any* analysis over one or more parameters (Pin_available, a DC voltage, or any user variable / cell parameter per §7), producing complex, multi-dimensional results.
- **Loadpull / sourcepull** — a first-class experiment: sweep source/load Γ (fundamental, and **harmonic loadpull** where harmonic terminations are varied), report FOMs as Smith-chart contours. Headline differentiator; dedicated UX attention.
- **Electromagnetic analysis (method of moments)** — analyse **layout** geometry against its technology **stackup** and return S-parameters that are consumable anywhere a Touchstone block is, closing the schematic → layout → EM loop inside one tool. Ports attach to layout **pins**. Two kernel families, sharing one solver interface, one stackup model, one port model, one mesh viewer and one results path:
  - **Planar (2.5D)** — staged: quasi-static per-unit-length for uniform cross-sections, then full-wave over a single dielectric with a ground plane, then the general layered stack with **vias and z-directed current**. Closed-form microstrip (Hammerstad-Jensen) is the validation oracle for the first stage. Design detail in [`design/mom-engine.md`](design/mom-engine.md).
  - **3D wirebonds (thin-wire)** — ball- and wedge-bond loop profiles in arrays up to **200 wires**, capturing the **mutual coupling between every wire**, over the packaging-realistic range: 0.5–1.25 mil wire radius, 5–50 mil loop height, 5–300 mil pitch. Staged the same way and for the same reason: quasi-static PEEC first (frequency-independent matrices, so a sweep is effectively free), then retarded full-wave. Wires-only over a ground plane first; meshed surfaces — landing pads, **overmold**, and **stepped/discontinuous ground** — second. Closed-form partial inductance (Neumann/Grover) and a right-angle-corner image solution are the validation oracles; owner-generated 3D FEM data is the regression anchor. Design detail in [`design/mom-wirebond-kernel.md`](design/mom-wirebond-kernel.md), summarised in `mom-engine.md` §10.11.

  This kernel exists because die-to-board bondwire inductance is a first-order design parameter in exactly the power-amplifier and module work the hero circuits (§4) represent, and because it is the geometry where MoM most clearly beats FEM: unknowns scale with wire count rather than with the volume of air between the wires.

## 6. Components (functional requirements)

Each component declares a variable number of typed parameters (with units). Adding a new component type must be straightforward — a single device interface (ports + linear `Stamp` and/or nonlinear `Evaluate`) plus factory registration.

**Linear (native):** resistor (2), inductor (2), capacitor (2), mutual inductance (0-port coupling), ideal transmission line TLIN (2), impedance block / arbitrary Z (2), SNP / Touchstone block (n), user-defined linear model in frequency domain (n), Port for hierarchy (2), Term for S-parameters (2), DC voltage source (2), AC voltage source (2), AC current source (2), RF power source = AC source with internal impedance (2), current probe (2).

**Nonlinear (native):** diode (2), FET (3), BJT (3), Symbolically-Defined Device — user equation `i = f(v)` with derivatives via automatic/symbolic differentiation (n), Verilog-A — user-supplied plain-text source, read by circuitRF (n).

**v1 component scope decisions:**
- **Verilog-A deferred to v2** beyond file-reading groundwork. v1 nonlinear coverage = built-in diode/FET/BJT + SDD. The device interface must let an OSDI/OpenVAF backend plug in later without redesign.
- The **SDD ships in v1**, built on the §7 expression engine + automatic differentiation.

### 6.1 ASM-HEMT — deferred to v2, via the Verilog-A/OSDI backend (decision)
ASM-HEMT (Advanced SPICE Model for HEMTs) is the CMC/Si2 **industry-standard, surface-potential-based GaN HEMT compact model**, distributed as **Verilog-A/Verilog-AMS source** (the same source commercial EDA tools compile; currently ~v101.5.0, revised periodically). It is a heavy model — Schrödinger-Poisson core, self-heating, access-region resistances, field plates, gate current, noise, many parameters.

**Decision:** support ASM-HEMT in **v2** by running its standard Verilog-A through the **OSDI/OpenVAF backend** — *not* as a native C# hand-port. The canonical, maintainable path is to run its Verilog-A (which is the v2 backend anyway, and yields MVSG/BSIM/etc. too); a native port is a major effort plus a recurring burden tracking CMC releases, counter to "lightweight." Pulling it into v1 means pulling the whole Verilog-A backend into v1. Nothing in v1 is blocked: built-in FET + SDD cover all heroes.

**v1 architectural requirement:** design the device interface (and OSDI binding) so ASM-HEMT slots in later without redesign — it must already express a **thermal/self-heating node**, **collapsible internal nodes** (access regions, field plates), **gate (terminal) current**, and **charge-based capacitances** (`q(v)` with `dq/dv` Jacobian contributions), alongside the conductive `i(v)`/`di/dv` path.

## 7. Variables, expressions & cell parameters (functional requirement)

circuitRF has one expression engine, used for **global variables**, **cell parameters**, and **SDD device equations**.

**Global variables.** User-defined, usable anywhere a parameter value is expected, and **sweepable** (a sweep axis can be a variable — feeding §5 sweeps and the §13 DataCube). A variable may be a constant or an **expression referencing other variables**.

**Cell parameters.** A cell may declare named parameters, each with a default (which may itself be an expression). When the cell is instanced as a sub-cell (component) in a parent, the parent may **override any parameter** with a value or expression evaluated in the *parent's* scope. Within the cell, its parameters are in scope for (a) expressions on the cell's own component values and (b) values **passed down to its own sub-cell instances**. This is hierarchical (parameterized-subcircuit) parameter passing.

**Circular-reference safety.** circuitRF **detects and rejects cycles** across the whole dependency graph — global variables, cell-parameter defaults, and instance-level overrides — and reports the offending cycle.

**v1 expression language (basic; designed to grow):**
- variable / parameter references;
- arithmetic and grouping: `+  -  *  /  ^  ( )`;
- a standard function set: `tan()`, `tanh()`, and the usual elementary/trig/exp/log functions;
- **conditionals (v1)** — comparison operators (`<  <=  >  >=  ==  !=`), boolean operators (`&&  ||  !`), and a conditional form `if(cond, then, else)` (ternary `cond ? then : else` equivalent);
- **user-defined functions** that are themselves expressions, taking an arbitrary number of parameters.

The exact grammar and the parameter-resolution order live in `docs/design/expressions.md` and `docs/design/data-model.md`. The parser/evaluator is built to extend (vectors, units-in-expressions, etc.) without breaking v1 files.

## 8. Cells, views, and libraries

User content is organized as **cells**: a collection of electrically connected components with optional ports to interface to other cells. A cell **declares parameters** (§7) and has three view types:

1. **Symbol** — the glyph rendered when the cell is instanced; defines port x/y positions.
2. **Schematic** — the electrical representation: positions of sub-cell instances and their interconnections.
3. **Layout** — the physical (2D) representation: geometry on a technology-defined layer stack, hierarchy with instances and arrays, and **parametric cells** whose artwork is generated from the cell's parameters rather than drawn. Implemented in v1; drawing 3D geometry is out of scope (§2). A layout resolves a **technology** (`.ctech`) supplying its layer table and substrate stackup, which is also what the EM solver (§5) and the substrate-aware microstrip components (§6) read.

   A **wirebond** is the one object with a physical extent the 2D canvas cannot show. It is modelled as a **parametric component instance**, not a shape: its parameters carry the endpoints, loop height, profile and wire diameter, its layout view is the **2D projection** plus an annotation, and its 3D path is generated for the EM solver (§5) on demand. The layout database therefore gains no 3D shape type, and "layout is 2D" (§2) holds without qualification.

Cells live in **Libraries**; circuitRF can reference many libraries simultaneously. Hierarchy is first-class: push into a sub-cell's schematic, edit, pop back. Sub-cell instances may receive parameter overrides from the parent (§7).

## 9. User interface (functional requirements)

- **Cross-platform** single codebase on Windows, macOS, Linux (Avalonia 12), mirroring splotRF.
- **Schematic editor** — drag symbols from a Library view; click-drag to move; a **wiring tool**; **obstacle-aware auto-routing** that avoids drawing wires over placed symbols.
- **Hierarchy navigation** — push into / pop out of sub-cell schematics.
- **Clipboard** — copy/paste all or selectable portions to/from other schematics via the **system clipboard**.
- **Symbol editor** — edit a custom cell's symbol.
- **Layout editor** — draw and edit physical geometry on the technology's layer stack: drawing tools with curves and holes, boolean operations, scale, a **technology/stackup editor**, hierarchy with instances and arrays, push-in/pop-out, flatten and group-into-cell, and **GDSII / DXF / Gerber+Excellon** import and export.
- **Wirebond placement** — place a parametric bond wire between two pads (ball or wedge profile, loop height, diameter), shown on the canvas as its 2D projection with an annotation. A **CSV wirebond table importer** covers the packaging workflow, because hand-placing a 200-wire array is not a workflow anyone will use.
- **Schematic ↔ layout generation** — an explicit command in each direction: place or update layout instances from the schematic, or update the schematic from layout edits. Never automatic; each run reports what it changed and is a single undo.
- **Undo/redo** — across all editors.
- **Data Display** — native `DataCube`-driven plots and tables (Smith, polar, rectangular, table); **measured-vs-simulated overlay**.
- **Variable / parameter / sweep setup** — define variables and cell parameters, set instance overrides, and choose sweep axes (§7).
- **Advanced settings** — all solver/analysis settings present and quickly findable, without cluttering the "easy" path.

## 10. Command-line interface (functional requirement)

A CLI accepts an input netlist and/or JSON circuit description plus an output file. It is also the **engine's primary test harness**: the engine must be fully drivable and validated headless, before and independently of the GUI.

## 11. File formats & data export (functional requirements)

- **Human-readable wherever possible**, diff-friendly, simple — a product principle.
- **Native format** for cells (symbol / schematic / library), including cell parameter declarations and instance overrides.
- **Netlist** input and **JSON** circuit description input.
- **Touchstone (.sNp)** read/write (shared core).
- **Simulation-output format** for results, consumable by the shared plotter (§13).
- **Result binary export** — export results to **MATLAB/Octave `.mat`** and **NumPy `.npy`**. A run's results are a **`DataSet`** of named **`DataCube`s** (each `Real` or `Complex`); preserve element kind, axis labels, and units where the format allows: `.mat` carries each named cube as a named variable/struct natively; **`.npy` exports the whole `DataSet` as a single packed structured (record) array** whose fields carry the named cubes plus axis metadata (per `docs/design/data-export.md`).
- A change to any file format or to the result-data contract is a reviewed decision (affects splotRF interop, §13).

## 12. "Easy," made measurable (functional requirement)

"Easy" = click/keystroke budgets for top tasks, so it is testable. **[PROPOSED]:**

- Placed FET (with bias + extrinsic network) → **running single-tone HB power sweep**: ≤ 8 actions.
- Converged HB setup → **running fundamental loadpull (default grid)**: ≤ 5 actions.
- Simulation result → **measured-vs-simulated overlay** on a Smith chart: ≤ 4 actions.

Advanced users must always reach every underlying setting; "easy" is the default, not a ceiling.

## 13. Architecture & integration requirements

- **Three-layer separation** (design → elaboration → numeric); the GUI never simulates the design layer directly. (`docs/design/data-model.md`, after the Swift review.)
- **Parameter resolution in elaboration.** Flattening resolves parameters **top-down** through the hierarchy: each instance binds its overrides in the parent scope, the cell evaluates its component values and sub-cell passes in its own scope, with cycle detection per §7. The numeric layer sees only fully-resolved values.
- **Shared core with splotRF.** A shared `RfCore` library: network-parameter types, Touchstone I/O, the `DataSet`/`DataCube` result types, and the Smith/polar/rectangular/table plotting controls. **circuitRF owns the result-model contract; splotRF is upgraded to consume it.** No RF math, Touchstone parsing, or plotting is reimplemented in circuitRF.
- **One result model — `DataSet` of `DataCube`s.** Every analysis run returns a `DataSet` holding named `DataCube`s; each cube is a labeled, unit-bearing, N-dimensional array with a single `DataKind` (`Real` or `Complex`). Complex spectra and scalar measurements (Pin, efficiency, PAE) coexist in one DataSet while each array stays one honest kind. S-parameter results, Touchstone files, and the `.mat`/`.npy` exports (§11) are all views of this model — the splotRF integration seam.
- **Sparse, managed linear algebra.** Large MNA uses managed sparse complex LU (CSparse.NET); native KLU/SuiteSparse stays a profiled, optional future optimization, never a v1 dependency.
- **Extensible device model** keeping the OSDI/Verilog-A (and ASM-HEMT) door open, including the §6.1 thermal/internal-node/gate-current/charge requirements.
- **One expression engine** (§7) shared by global variables, cell parameters, and the SDD.

## 14. Non-functional requirements **[all PROPOSED — confirm]**

| Concern | Target (v1) |
|---|---|
| Platforms | Windows 11, macOS (current), Ubuntu 24.04+, incl. ARM64 — mirroring splotRF |
| S-parameter scale | 10,000-component netlist, few-hundred frequency points, in **< 10 s** on a typical laptop |
| Single-tone HB | Hero-2 PA point converges in **< 3 s** per power/bias point at H = 7 |
| Loadpull throughput | ≥100-point fundamental loadpull (Hero 3) in **< 2 min** with continuation |
| Memory | A typical loadpull DataCube fits in RAM (~tens of MB); backing store designed to swap to chunked/memory-mapped for large sweeps |
| Numerical type | All AC/HB quantities are `System.Numerics.Complex` (double precision) |
| Validation | Every numerical change has a `testdata/` regression test within a stated tolerance; CI runs the suite on all three OSes |

## 15. Licensing & commercialization strategy

- **circuitRF core is MIT.** No GPL/copyleft code ingested (some third-party simulators are GPL — learn from, never copy). For ASM-HEMT (§6.1): comply with the model's CMC/author license when the v2 backend lands; don't vendor model source into the MIT core in a conflicting way.
- **circuitRF+ (future, commercial).** MIT permits a closed-source superset. **Architectural requirement:** a clean extension/plug-in boundary in v1 so commercial features layer on **without forking the core**; keep proprietary-only assumptions out of the open core.
- **Solo now, contributors later.** Light process, but architecture, `CLAUDE.md` files, and `docs/` maintained as if contributors will arrive.

## 16. Risks (pointers; full register in the development plan)

Dominant risks: **HB convergence and two-tone frequency indexing** (now with a hard mixing-order-≥5 requirement from Hero 5 — design on paper first); **Verilog-A/ASM-HEMT scope** (deferred to v2; v1 device interface pre-accommodates its needs); **hierarchical parameter resolution and cycle detection** (correctness during elaboration); **schematic-canvas performance/auto-routing** (custom virtualized canvas, not control-per-component); **scope creep vs "lightweight"** (the five heroes + §2 non-goals are the gate); and, in the EM phase, the **layered-medium Green's function / DCIM** (the one genuinely uncertain schedule — note that the wirebond kernel deliberately does **not** depend on it). Swift→C# translation, large-sweep memory, and the wirebond-specific risks are tracked in the plan's risk register.

## 17. Decisions resolved & remaining open items

**Resolved (v1.0 baseline):**
- Hero 1 → `1e-6`. Hero 2/4 → Pout/gain ±0.01 dB, efficiency/PAE ±0.1 pp. Hero 4 added; Hero 5 (two-tone IM) added.
- Hero 2/4 reference-model match → **identical SDD FET definition on both sides** (the SDD language must be expressible in the equation-defined-device form the reference is authored in).
- Tone defaults → **2 GHz center; two-tone f₁ = 1.995 / f₂ = 2.005 GHz, Δf = 10 MHz; check IM2–IM5**; two-tone truncation **mixing order ≥ 5**.
- IM check → included **if** an external IM reference is obtainable; else a self-consistency target.
- Expression language → variables, `+ - * / ^ ( )`, function set incl. `tan`/`tanh`, **conditionals (v1)**, user-defined expression functions; grows later.
- **Cell parameters** with hierarchical passing → in v1 (§7, §8, §13).
- ASM-HEMT → v2 via Verilog-A/OSDI (§6.1).
- `.npy` multi-axis result → **whole `DataSet` exported as one packed structured array** (§11).

**Resolved (v1.3, 2026-08-04):**
- **3D wirebond EM → in scope**, as a fourth MoM kernel behind the existing solver interface (§5). Thin-wire, quasi-static first then retarded; wires-only first then meshed surfaces.
- **The §2 "no 3D EM" non-goal → narrowed** to *no FEM, no volumetric meshing, no arbitrary 3D geometry*. The old wording excluded a solver that needs none of them.
- **How a 3D wire lives in a 2D layout → a parametric component with a 2D-projection view** (§8). No 3D shape type in the layout database; "layout is 2D" survives intact.
- **Overmold → modelled**, via bound charge on the encapsulant surfaces with a homogeneous-fill fast mode. Not via volume meshing.
- **Discontinuous (stepped) ground → modelled**, by meshing the ground as a conductor. Explicitly *not* something the layered-medium kernel provides.

**Remaining open items:**
1. **Hero 2/4/5 power-sweep range** — TBD pending the chosen SDD FET model (small-signal start, compression depth, and the drive level(s) used for the Hero-5 IM check).
2. Hero-5 IM tolerances (§4) are **[PROPOSED]** — confirm dBc bounds once reference IM data exists.
3. NFR numbers in §14 remain **[PROPOSED]**.
4. **Wirebond kernel priority relative to the full-wave planar kernel** — the wirebond phases depend on the quasi-static planar phase and on nothing beyond it, so they carry no layered-Green's-function schedule risk and could run first. A roadmap sequencing call, not a scope question; it blocks neither design.

---

*Implementation status (2026-06-24): Phases 1–7 are substantially complete — the engine (MNA/S-parameters, nonlinear DC, single/two-tone HB, sweeps, loadpull) runs the heroes from the CLI and the GUI, and the Avalonia editors + `DataCube`-native Data Display are in place, including end-to-end loadpull contour plotting (simulated and measured) and interactive markers that operate on the contour surface. Remaining for the v1 (alpha) release: Phase-8 packaging/hardening. Noise analysis is a deliberate green-field deferral (see `docs/Development_Plan.md` §11). Roadmap and current status live in `docs/Development_Plan.md`.*
