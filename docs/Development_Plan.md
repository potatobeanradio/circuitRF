# circuitRF — Development Plan & AI Workflow Strategy

**Status:** Phases 0–7 complete; the **layout editor (Phase 9)** is substantially complete and
**electromagnetic simulation (Phase 10)** is next · **Date:** 2026-07-29
*Companion to `docs/PRD.md`. The PRD defines scope and acceptance; this plan defines the roadmap, the AI workflow, and the engineering strategy. Where the two overlap, the PRD wins.*

> **Update note (2026-06-24):** circuitRF is now a working application, not a plan. Phases 0–7 are
> substantially built and validated: the three-layer model + expression engine + `.cnl`/JSON I/O
> (Phase 1); complex sparse MNA + S-parameters + Touchstone via the extracted `RfCore` sibling
> (Phase 2); nonlinear DC + device models + the SDD with automatic differentiation (Phase 3);
> single-tone and two-tone harmonic balance with continuation (Phase 4); parametric sweeps + the
> `DataSet`/`DataCube` model + loadpull/sourcepull + `.mat`/`.npy`/Touchstone/`.spl`/`.lpcwave`
> export (Phase 5); the Avalonia 12 schematic editor, symbol editor, library palette, workspace/
> project tree, and hierarchy navigation (Phase 6); and the `DataCube`-native Data Display with
> Smith/polar/rect/table plots, markers, the loadpull surface/contour engine, **end-to-end loadpull
> contour plotting** (simulated and measured), and **interactive markers that operate on the contour
> surface** (Phase 7).
> **What's left for v1:** Phase-8 hardening — installers, docs, and CI. **Deferred to v2:**
> the Layout editor, a sparse block Jacobian for HB at scale, the Verilog-A/OSDI backend
> (→ ASM-HEMT), and **noise analysis** (see §11). The earlier refinements still hold: the result
> model is a **`DataSet`** of single-kind **`DataCube`s**; **`RfCore` is an external sibling**
> (`ProjectReference`); the device base is **`ComponentModel`**; analyses **and measurements**
> attach to a **`TestBench`**; `.npy` exports the whole DataSet as one packed structured array.
> Per-subsystem detail lives in the nested `CLAUDE.md` files and `docs/design/`.

---

## 0. The three decisions that shape everything

**1. Share a core library with splotRF.** circuitRF and splotRF are the same animal: .NET 10, Avalonia 12, SkiaSharp, NumFlat, CommunityToolkit.MVVM, MIT, cross-platform, AI-assisted. splotRF already solves Touchstone I/O, S/Z/Y math, Smith/polar/rectangular plotting, renormalization, de-embedding, markers, and PDF/SVG export. **Decision taken:** circuitRF and splotRF share an `RfCore` library (network-parameter types, Touchstone I/O, the `DataSet`/`DataCube` result types, the plotting controls); circuitRF owns the result-model contract and splotRF is upgraded to consume it. **RfCore is an external sibling project** — cloned side-by-side and referenced via `ProjectReference` (`../RfCore/RfCore.csproj`), not under `src/` — and must be extracted from splotRF (needed from Phase 2; Phase 1 does not depend on it). This eliminates ~a phase of work and makes measured-vs-simulated overlays trivial.

**2. Build the engine first, CLI-driven, validated numerically before the GUI.** The simulator's correctness is the entire value of circuitRF. Define circuits in the human-readable format / netlist, run them headless from the CLI, and check against references (analytic, other simulators' exports, measured) before building the schematic editor on top. The CLI you wanted anyway is the engine's test harness from day one.

**3. Let the five hero circuits bound scope.** The approved heroes (PRD §4) are the acceptance anchors; every feature is gated against "does a hero need it for v1?":
- **Hero 1** — S-parameters of a 4-port RLC matching network with an embedded Touchstone block, vs a 4-port reference.
- **Hero 2** — single-tone HB power sweep of a single-FET PA (RLC + mutual-inductance extrinsic network); Pout/gain/efficiency/PAE.
- **Hero 3** — fundamental-impedance loadpull of the Hero-2 PA, contours on a Smith chart.
- **Hero 4** — single-tone HB of a 2-stage PA (input MN → FET → interstage MN → FET → output MN); the strongest test of linear/nonlinear partitioning across two devices.
- **Hero 5** — two-tone intermodulation (IM2–IM5) on the Hero-2 PA; forces two-tone mixing order ≥ 5.

This is how a long component/feature list stays compatible with "lightweight."

---

## 1. Stack validation (confirmed)

- **Avalonia 12 — confirmed**, and proven by splotRF on Windows/macOS/Linux (incl. ARM64). Its rendering work (deferred composition, dirty-rect tracking) suits a schematic canvas. Caveat: render the canvas yourself with virtualization + a spatial index — do not use one control per component.
- **.NET 10 (LTS) — confirmed**, and splotRF already targets it. Budget a little time for packaging-tool lag (splotRF used `fpm` for Linux because `dotnet-deb` trailed .NET 10).
- **Sparse matrices — required.** Use **CSparse.NET** (managed complex sparse LU) for large MNA; keep native KLU/SuiteSparse as a profiled, optional future optimization. NumFlat (dense) is fine for small/per-harmonic blocks.
- **Third-party cell database — deferred.** Native human-readable format for v1; a third-party database only as an optional later cell import/export bridge.

## 2. PRD — done

The PRD is written and **approved** (`docs/PRD.md`, v1.0 baseline). It is the source of truth for scope, the five heroes + acceptance criteria, components, file formats, the "easy" click budgets, NFR targets, and licensing. Remaining `[PROPOSED]`/open items live in PRD §17 (power-sweep range pending the FET model; Hero-5 IM tolerances; NFR numbers).

## 3. Deep-dive areas to resolve before each phase

Each gets a design note in `docs/design/` before its phase starts.

### 3.1 Harmonic balance — the crown jewel and top risk
Partition (linear freq-domain subnetwork + nonlinear time-domain devices via FFT); frequency-domain KCL residual; the conversion-matrix Jacobian (conductive `Γ·diag(dg/dv)·Γ⁻¹` + charge `jΩ·Γ·diag(dq/dv)·Γ⁻¹`); single- vs two-tone frequency management (two-tone needs diamond truncation, **mixing order ≥ 5** for Hero 5, a separately-tested frequency-index map, and an APFT/frequency-mapping transform); convergence via power/source-step continuation from day one. Design on paper (Opus) → `docs/design/harmonic-balance.md`. Local conventions captured in `src/Engine/HarmonicBalance/CLAUDE.md`.

### 3.2 Data model architecture — **done** (`docs/design/data-model.md`)
Three-layer separation — **design** (editable, hierarchical, serialized) → **elaboration** (flatten, resolve params/sweeps, number nodes) → **numeric** (matrices, vectors, the `DataSet`/`DataCube` result model). The **`ComponentModel`** base (passive + active; "Device" reserved for active parts) with `Stamp`/`Evaluate` is what makes new components easy. The Swift was reviewed as reference (not transliterated). The `Cell` vs `TestBench` split and the analysis/measurement model live here too.

### 3.3 Multi-dimensional complex solution data — the `DataSet`/`DataCube` (done)
A run returns a **`DataSet`**: a named collection of **`DataCube`s**, each a labeled, unit-bearing, N-D array with a single `DataKind` (`Real` or `Complex`), flat-backed with strides, sliceable/reducible. Complex spectra and real scalar measurements coexist in one DataSet while each array stays one honest kind. S-parameters and Touchstone are special cases — which is why it's the splotRF seam and the source for `.mat`/`.npy` export (`.npy` = the whole DataSet as one packed structured array). Backing store designed to be swappable to chunked/memory-mapped later. Conventions in `src/Core/Data/CLAUDE.md`.

### 3.4 Verilog-A & ASM-HEMT — deferred to v2
v1 ships built-in diode/FET/BJT + the SDD; **full Verilog-A is v2.** **ASM-HEMT** (CMC/Si2 standard GaN model, distributed as Verilog-A) is supported in **v2 via an OSDI/OpenVAF backend — not a native hand-port** (a port is a major effort + a maintenance burden tracking CMC releases). **v1 requirement:** the device interface must already express a thermal/self-heating node, collapsible internal nodes, gate current, and charge-based capacitances, so ASM-HEMT slots in later without redesign.

### 3.5 Loadpull/sourcepull UX — the differentiator
A first-class "experiment": pick the DUT port, a Smith-chart grid of Γ (fundamental and optionally harmonic), and the FOMs; run HB per point into the DataCube; plot contours via the shared Smith chart. Spend UX effort here.

### 3.6 SDD + automatic differentiation
The SDD needs an expression parser and exact `di/dv` via forward-mode AD (or symbolic), so users write `i = f(v)` and get correct Jacobian stamps. Build it before any Verilog-A work. Note the heroes use the SDD FET (the same equations are transcribed into other tools for the golden references), so the SDD language must stay transcribable into other tools' equation-defined devices.

### 3.7 Variables, expressions & cell parameters *(new)*
One expression engine serves global variables, **cell parameters** (hierarchical, parameterized-subcircuit passing), and the SDD. Elaboration resolves parameters **top-down** (instance overrides bound in parent scope; cell evaluates its values and sub-cell passes in its scope) with **cycle detection** across variables, defaults, and overrides. v1 language: refs; `+ - * / ^ ( )`; functions (`tan`,`tanh`,…); **conditionals** (`< <= > >= == !=`, `&& || !`, `if(cond,then,else)`); user-defined expression functions. → `docs/design/expressions.md`.

## 4. Roadmap — phases, exit criteria

| Phase | Goal | Key deliverables | Exit criteria |
|---|---|---|---|
| **0. Discovery & design** ✅ *(complete)* | Decide before building | PRD ✅; repo + skeleton ✅; six `CLAUDE.md` files ✅; all five design notes ✅ (`data-model`, `expressions`, `linear-engine`, `measurements`, `harmonic-balance`) | PRD approved ✅; all design notes exist & approved ✅; ready for Phase 1 |
| **1. Core model + files + CLI + expression engine** | The spine | Three-layer model; native schematic/symbol/library format; netlist + JSON reader; **expression engine + variables + cell parameters + cycle detection**; elaboration/flattening with top-down parameter resolution; CLI that dumps the elaborated netlist | A hierarchical, parameterized circuit round-trips file → model → elaboration → printed netlist; cycles are detected and reported |
| **2. Linear engine: MNA, DC(linear), S-parameters** | First trustworthy numbers | Complex sparse MNA (CSparse.NET); per-frequency S-parameter extraction + renormalization; SNP block w/ interpolation; Touchstone I/O via `RfCore` | **Hero 1** matches the 4-port reference to `< 1e-6` from the CLI |
| **3. Nonlinear DC + device models + SDD/AD** | Nonlinear foundation | Newton DC w/ gmin/source stepping; diode → FET → BJT stamps; SDD with automatic differentiation (uses the §3.7 engine) | Hero-PA DC operating point converges & matches reference; an SDD nonlinearity solves correctly |
| **4. Harmonic balance** | The crown jewel | Single-tone HB (conversion-matrix Jacobian, power-step continuation); multi-device partition; two-tone (diamond truncation, mixing order ≥ 5, separable index map) | **Hero 2** power sweep (Pout/gain ±0.01 dB, eff/PAE ±0.1 pp); **Hero 4** 2-stage partition; **Hero 5** two-tone IM2–IM5 |
| **5. Sweep + DataSet + loadpull + export** | Differentiator | Generic parametric sweep; the `DataSet`/`DataCube` result model; loadpull/sourcepull experiment (incl. harmonic loadpull, Γ-grid or Z-grid); `.mat`/`.npy` export (`.npy` = whole DataSet as one packed structured array) | **Hero 3** fundamental loadpull contours from the CLI; exports load in MATLAB/Octave/NumPy |
| **6. GUI: schematic + symbol editors** | How people drive it | Avalonia 12 virtualized canvas + spatial index; place/move/wire + obstacle-aware auto-routing; hierarchy push/pop; system-clipboard copy/paste; symbol editor; library browser; variable/parameter/sweep setup; undo/redo | A user builds, parameterizes, edits, and runs the hero PAs in the GUI |
| **7. Data Display** | Results & measured-vs-sim | `DataCube`-native plotter; plot + table views; measured-vs-simulated overlay; loadpull contour rendering | Simulation results and a lab Touchstone overlay on one Smith chart |
| **8. Hardening & optional extensions** | Polish + future doors | Packaging (Win/macOS/Linux); docs; regression suite in CI; *optional:* Verilog-A/OSDI backend (→ **ASM-HEMT**), third-party cell bridge | Installers on 3 OSes; regression suite green; deferred items have clean plug-in points |
| **9. Layout editor** ✅ *(substantially complete)* | Physical design, in the same tool | Integer-DBU geometry model + `.clay`/`.ctech` formats (L0); drawing, selection, booleans, scale, labels, bitmaps (L1); spatial index, LOD, path caching (L2); hierarchy — instances, arrays, navigation, flatten, group-into-cell (L3); **GDSII / DXF / Gerber+Excellon** interchange (L4); the **PCell** contract + substrate-aware **microstrip family** (L5a); **schematic↔layout generation** (L5) | A PCB and an MMIC design draw, edit and export; exported GDSII/DXF/Gerber open correctly in independent third-party viewers; schematic→layout and layout→schematic are idempotent and report what they change |
| **10. Electromagnetic simulation (2.5D MoM)** ▶ *(next)* | Close the schematic → layout → EM loop | Substrate stackup + mesher (L6); **quasi-static per-unit-length** kernel for uniform cross-sections (L7); **full-wave, single dielectric + ground plane** (L8); **general layered stack, N dielectrics, vias and z-directed current** (L9). Ports attach to layout pins; results return as S-parameters consumable anywhere a Touchstone block is | L7 agrees with closed-form microstrip (Hammerstad-Jensen) within **±2%** on Z₀ and εeff over the published validity range; L8/L9 agree with reference/measured data for a coupled-line and a via-bearing structure |

Phases 0–5 are the "engine half" (disproportionate *thinking*); 6–8 the "product half" (disproportionate
*typing*) — which maps onto the model split below. Phases 9–10 repeat that shape at smaller scale: the layout
editor is mostly product work, the MoM kernel is mostly engine work.

## 5. AI workflow: Opus vs Sonnet, Chat vs Code

**Principle:** Opus where depth/ambiguity/math/architecture/gnarly-debugging dominate; Sonnet where the spec is clear and the work is execution and volume. Loop: design & review with Opus → produce within those rails with Sonnet → bring hard failures back to Opus. **Chat** for design/brainstorm/PRD/`CLAUDE.md`/Swift-review; **Code** for in-repo implementation, builds, and tests.

| Phase | Primary model | Where |
|---|---|---|
| 0. Design/PRD ✅ | Opus | Chat |
| 1. Core model + files + CLI + expressions | Opus (design) → Sonnet (impl) | Chat → Code |
| 2. Linear/S-param | Opus (MNA/S-param method) → Sonnet (stamps, Touchstone, tests) | Chat → Code |
| 3. DC + devices + SDD/AD | Opus (AD + convergence) → Sonnet (per-device stamps + tests) | Chat/Code → Code |
| 4. Harmonic balance | Opus, heavily (solver core); Sonnet (plumbing/tests) | Chat (derive) → Code |
| 5. Sweep + DataCube + loadpull + export | Opus (cube + sweep design) → Sonnet (impl + export) | Chat → Code |
| 6. GUI | Opus (canvas/virtualization/routing) → Sonnet (views/VMs/controls) | Chat → Code |
| 7. splotRF integration | Sonnet (Opus only for shared-core refactors) | Code |
| 8. Hardening + extensions | Sonnet (packaging/docs/CI); Opus (Verilog-A/OSDI or third-party interop design) | Code; Chat for interop |

## 6. Per-phase starter prompts (openers)

- **Phase 1 (Opus→Sonnet):** design the human-readable cell format (symbol/schematic/library) incl. parameter declarations + instance overrides; then implement reader/writer, the expression engine (with conditionals + user functions), and top-down parameter resolution with cycle detection; round-trip tests on `testdata/`.
- **Phase 2 (Opus→Sonnet):** implement complex sparse MNA (CSparse.NET) + S-parameter extraction/renormalization + SNP-block interpolation; CLI `circuitrf sparam`; regression vs the Hero-1 `.s4p` to `1e-6`.
- **Phase 3 (Opus→Code):** forward-mode AD for the SDD so Jacobian stamps are exact; Newton DC with gmin/source stepping; diode then FET/BJT; validate the PA bias point.
- **Phase 4 (Opus, Chat first):** derive single-tone HB (residual, conversion-matrix Jacobian, power-step continuation); then two-tone with diamond truncation + a separable, testable index map; validate Hero 2, then Hero 4 (multi-device), then Hero 5 (IM2–IM5).
- **Phase 6 (Opus, Chat):** design the Avalonia 12 canvas — viewport virtualization, spatial index for hit-testing + obstacle-aware orthogonal-A* routing, command-pattern undo/redo.

## 7. CLAUDE.md — done

`CLAUDE.md` files are **spatial, not temporal**: one at the repo root plus nested ones per subsystem, updated as phases progress. **Written and in place (six):**
- `CLAUDE.md` (root) — stack, build/test/run, three-layer architecture, invariants, expression/cell-parameter rules, how-to-add-a-component (`ComponentModel`, incl. the v2 ASM-HEMT needs), validation against the five heroes, "ask before," licensing.
- `src/Core/CLAUDE.md` — design/elaboration/expression-engine conventions; `Cell` vs `TestBench`; kinded values; cycle detection; the Phase-1 deliverable.
- `src/Core/Data/CLAUDE.md` — the `DataSet`/`DataCube` result model + splotRF seam, single-kind cubes, named unit-bearing axes, swappable backing store, lockstep changes with splotRF.
- `src/Engine/CLAUDE.md` — `MnaSystem` + stamping API, fixed sign/direction conventions, one-assembly/three-uses, element grouping, DC-no-fudges, sparse-solve structure.
- `src/Engine/HarmonicBalance/CLAUDE.md` — FFT/sign conventions (incl. the MATLAB 1-based pseudocode caveat), partition + conversion-matrix Jacobian, single/two-tone, multi-device, continuation, DC-k=0 retention, linear-engine interface.
- `src/Ui/CLAUDE.md` — Avalonia/MVVM, virtualized canvas (never control-per-component), A* routing, command-pattern undo/redo, "GUI never simulates the design layer directly."

Update each at the end of the phase that touches its subsystem.

## 8. Risk register

| Risk | Mitigation |
|---|---|
| HB convergence & two-tone indexing (now with a hard order-≥5 requirement) | Paper design first (Opus); power-step continuation from day one; index map as a separate tested unit; single-tone fully before two-tone |
| Verilog-A / ASM-HEMT scope | Deferred to v2 behind the OSDI backend; v1 device interface pre-accommodates thermal/internal-node/gate-current/charge needs |
| Hierarchical parameter resolution & cycle detection | Correct top-down resolution in elaboration; cycle detection across vars/defaults/overrides; tested in Phase 1 |
| Native libs vs cross-platform | Stay managed (CSparse.NET, NumFlat) for v1; native interop only as a profiled option |
| Schematic-canvas perf & auto-routing | Custom virtualized canvas + spatial index; orthogonal A* to start |
| Scope creep vs "lightweight" | The five heroes + PRD §2 non-goals are the gate |
| Validation / golden references | Owner-generated from other simulators with the **identical SDD FET**; `testdata/` regression suite in CI from Phase 2 |
| Solo-dev + AI architectural drift | `CLAUDE.md` invariants; Opus subsystem-boundary reviews; engine-first |
| Swift → C# semantics | Design the C# model deliberately (don't transliterate); review the Swift before Phase 1 |
| Big-sweep memory | `DataSet`/`DataCube` backing store swappable to chunked/memory-mapped; deep measurement reach noted for a future prune-to-referenced-nodes pass |
| Licensing | MIT core; never ingest GPL from third-party simulators; comply with ASM-HEMT's license at v2; reuse your own MIT splotRF freely |
| Packaging-tool lag on .NET 10 | Budget Phase 8 time; reuse splotRF's `wix`/`fpm`/macOS recipes |

## 9. Status & immediate next steps

**Done (Phases 0–7):** the spine (three-layer model, expression engine + cell parameters +
cycle detection, `.cnl`/JSON I/O, elaboration); the linear engine (sparse complex MNA, S-parameters,
renormalization, SNP interpolation, Touchstone I/O via `RfCore`); nonlinear DC +
diode/FET/BJT + the SDD with forward-mode AD; single- and two-tone harmonic balance with power-step
continuation; the generic parametric sweep, the `DataSet`/`DataCube` result model, loadpull/sourcepull
(incl. the pursuit engine and the post-processor), and `.mat`/`.npy`/Touchstone/`.spl`/`.lpcwave` export;
the Avalonia 12 GUI (virtualized schematic canvas, symbol editor, library palette, workspace + project
tree, hierarchy push/pop, undo/redo); and the `DataCube`-native Data Display (Smith/polar/rect/table,
markers, the RBF loadpull surface + contour extractor/renderer). **End-to-end loadpull contour plotting**
(simulated and measured) and **interactive markers that read/drag on the contour surface** are both done.
The firewall check (no Avalonia below `src/Ui`) runs in CI.

**Done (Phase 9 — layout, substantially):** the integer-DBU geometry model and the `.clay` / `.ctech`
formats; the layout editor (drawing tools, curves and holes, selection and handles, boolean operations via
Clipper2, scale, labels with text-to-polygon, bitmaps, technology and stackup editing); performance work
(R-tree spatial index, LOD, path caching — measured, not guessed); hierarchy (instances, arrays,
push-in/pop-out navigation, flatten one-level/all-levels, group-into-cell); **GDSII, DXF and
Gerber+Excellon** interchange, with DXF import first-class; the **PCell** contract
(`docs/design/pcell-contract.md`) and the substrate-aware **microstrip family** — MLIN, MBEND, MTEE, MCROSS,
MTAPER and the **Klopfenstein taper** with a novel off-axis `Offset` parameter — specified in
`docs/design/microstrip-models.md` against primary literature; and **schematic↔layout generation** in both
directions.

**Next (Phase 10 — electromagnetic simulation):** the 2.5D method-of-moments arc (L6–L9) described in
`docs/design/layout-view.md` §10. Staged so each stage has a validation oracle: the quasi-static kernel is
checked against the same closed-form microstrip implementation the MLIN component uses, which is why that
implementation is deliberately **shared** rather than duplicated.

**Remaining for the v1 (alpha) release:**
1. **Hardening (Phase 8)** — installers for Windows/macOS/Linux, broader docs, and keeping the `testdata/`
   regression suite green in CI on all three OSes.
2. Resolve the remaining PRD §17 items as inputs arrive (FET model → power-sweep range; reference IM data
   → Hero-5 tolerances; a benchmark machine → NFR numbers).

## 10. Resolved decisions (was "open questions")

- Solo project; may add contributors later → light process, contributor-ready docs/structure.
- License: **MIT** core; no GPL ingestion; future commercial **circuitRF+** via a clean extension boundary.
- Five hero circuits locked (PRD §4).
- splotRF: **shared core** (external `RfCore` sibling, `ProjectReference`); circuitRF owns the `DataSet`/`DataCube` contract.
- PRD written **before** the Swift review (done); Swift review is the next step.

*Remaining genuinely-open items are tracked in PRD §17 (power-sweep range; Hero-5 IM tolerances; NFR numbers).*

## 11. Noise analysis — a deliberate green field (v2 candidate)

circuitRF v1 has **no noise analysis** — no noise figure, no phase noise, no noise-parameter (Fmin, Γopt,
Rn) extraction. This is not a technical wall: the linear engine already builds the MNA system noise analysis
needs, the SDD/device interface could carry noise-current contributions, and `.spl`/`.lpcwave` ingest already
parses noise columns. It is simply **unbuilt** — a clean, well-bounded feature left open on purpose for a
contributor (an LNA designer or a device-modeling expert) who wants to own it. A solid noise pass — small-
signal noise figure and noise parameters over frequency, then nonlinear/HB noise for mixers and oscillators —
is a strong candidate for a **major v2 addition**. If that's you, this is a great place to make a mark.
