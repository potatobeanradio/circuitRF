# circuitRF Architecture

How circuitRF is put together: the layers, the engines, the framework firewall and the
source tree. Back to the [README](README.md).

## Layers

circuitRF is built in **strictly one-directional layers** — dependencies point **up the stack only**, and
nothing below the UI knows the UI exists. This is what keeps the simulator (the actual value of the
product) independent of any GUI framework. Full detail:
[`docs/design/ui-architecture.md`](docs/design/ui-architecture.md).

```
  src/RfCore              shared result/network library: Touchstone I/O, S/Z/Y math,
        ▲                  the DataSet/DataCube result model, loadpull readers/writers
        │
  src/Core      Design + Elaboration: cells, instances, nets, parameters, the expression
        ▲        engine; flatten + resolve → an "elaborated netlist".  No UI, no numerics.
        │
  src/Engine    Numeric layer: sparse MNA, DC, S-parameters, harmonic balance, loadpull,
        ▲        and the planar method-of-moments EM kernel.  Consumes the elaborated
        │        netlist, produces a DataSet.  No UI.
        │
  src/Design    Design-layer DOCUMENTS: the .clay layout model, the .ctech technology and
        ▲        stackup, the .ccell cell folder, the .csch/.csym schematic and symbol models
        │        with net extraction, the .cem EM setup and its extractors, the interchange
        │        readers/writers, the DRC and LVS engines, and the functions that CREATE a
        │        workspace, a cell and an imported part.  No UI: it draws nothing and docks
        │        nothing; the EDITORS all stay in src/Ui.
        │
  src/Render    The Skia RENDERERS: schematic, symbol, layout and bondwire, their themes and
        ▲        caches, the colour-theme model and .ccolor reader, and the overlay descriptions
        │        of a frame's transient chrome.  SkiaSharp only — pixels out, and nothing in.
        │        Referenced by BOTH src/Ui and src/Cli, so there is exactly one renderer.
        │
  src/Ui        Presentation: Avalonia 12 + SkiaSharp. Schematic/symbol/layout editors,
                 Data Display, workspace. Depends on everything above. Nothing depends on it.

  src/Diagnostics  The coded-diagnostic leaf: an id, typed arguments and an English template.
                 Referenced by every layer that authors user-facing text, including RfCore and
                 WBond, which have no common ancestor.  No UI.

  src/Harmonica  harmonicaRF's framework-free half — interactive harmonic loadpull on one
  src/WBond      wBond's framework-free half — bondwire geometry + its own 3D MoM kernel
                 Both also ship as standalone apps: src/Ui with a different Main().

  src/Cli       Headless driver — depends on Core/Engine/RfCore/Design, NOT on src/Ui. Proof
                 the engines are fully usable with no GUI; the engines' primary test harness.
                 Verbs: sparam, dc, hb, lp, lpp, em, elab, netlist, check, explain, lvs,
                 rail, smith, convert, new, import, render, plot, find, read, reference,
                 history, serve.  See docs/user/reference/cli.html.
```

### The three layers (design → elaboration → numeric)

1. **Design layer** (`src/Core`) — what you edit: **cells** (each with Symbol / Schematic / Layout views),
   instances, nets, **parameters** (hierarchical, with overrides), global variables, and a **TestBench**
   (the thing you simulate — top cell + analyses + measurements). Serialized to **human-readable** files
   (`.cnl` netlist, JSON).
2. **Elaboration layer** (`src/Core`) — flattens the hierarchy, resolves every parameter and expression
   **top-down** (with mandatory **cycle detection**), and numbers the nodes → an *elaborated netlist*.
   This is the single thing the engine consumes, whether it came from a hand-written `.cnl` or from the
   schematic editor's **net extractor**.
3. **Numeric layer** (`src/Engine`) — matrices, unknown vectors, and analyses. It never sees a domain
   object or an unresolved expression. Every run returns a **`DataSet`**: a named collection of
   **`DataCube`s**, each a labeled, unit-bearing, N-D array of a single kind (Real **or** Complex).

One **expression engine** (tokenize → Pratt-parse → AST → evaluate; never string substitution) serves
global variables, cell parameters, the SDD's device equations, and measurements
([`docs/design/expressions.md`](docs/design/expressions.md)).

### The engines (`src/Engine`)

- **Linear / S-parameters** — complex **sparse MNA** (CSparse.NET) over a frequency sweep, with
  renormalization and Touchstone (`.sNp`) blocks with interpolation.
  ([`docs/design/linear-engine.md`](docs/design/linear-engine.md))
- **Nonlinear DC** — Newton–Raphson with gmin/source stepping; diode, FET, BJT, and the **SDD**
  (Symbolically-Defined Device: you write `i = f(v)` and exact Jacobians come from **forward-mode
  automatic differentiation**). ([`docs/design/nonlinear-dc.md`](docs/design/nonlinear-dc.md),
  [`docs/design/sdd.md`](docs/design/sdd.md))
- **Harmonic balance** — multidimensional Newton with a conversion-matrix Jacobian, a clean
  linear/nonlinear partition, **single- and two-tone** (diamond truncation, mixing order ≥ 5), and
  power-step continuation for convergence at drive.
  ([`docs/design/harmonic-balance.md`](docs/design/harmonic-balance.md))
- **Loadpull / sourcepull** — the headline differentiator: sweep source/load Γ over a Smith-chart grid,
  run HB per point, and report FOMs (Pout, gain, efficiency, PAE) as contours. Includes a **pursuit**
  engine and a post-processor that derives the display metrics measured files carry.
  ([`docs/design/loadpull.md`](docs/design/loadpull.md),
  [`docs/design/loadpull-contours.md`](docs/design/loadpull-contours.md))
- **Electromagnetic (`src/Engine/Mom`)** — two kernels behind one registry: a **quasi-static
  cross-section** solver for uniform lines (Z₀, ε_eff, loss, RLGC) and a **full-wave planar
  method-of-moments** solver over a layered Green's function, with meshing, ports, de-embedding,
  adaptive frequency sampling and an AIM accelerator. Fed by `src/Design`'s extractors, driven by the
  GUI's EM Setup panel *or* by `circuitrf em`. ([`docs/design/mom-engine.md`](docs/design/mom-engine.md))

### How rendering works — SkiaSharp and Avalonia

The GUI is **Avalonia 12** (the cross-platform .NET UI framework — the same window, menu and dock
machinery on all three OSes). But circuitRF does **not** render schematics, layouts or plots as
Avalonia controls — a 10,000-component schematic would die under one control per component. Each
canvas draws itself with **SkiaSharp** through a custom control, with **viewport virtualization**
and a **spatial index** for hit-testing and pan/zoom. The split is deliberate: a **pure renderer**
(Skia only, no Avalonia types) draws a model + transform onto a surface, and a thin Avalonia control
hosts that surface and pumps input events. The rendering investment lives in the renderer.

### The framework firewall

The circuitRF *engines* must be skinnable by any new UI with as little trouble as possible — so
**`RfCore`, `src/Core`, `src/Engine`, `src/Design`, `src/Render`, `src/Cli`, `src/Diagnostics`,
`src/Harmonica` and `src/WBond` reference no UI framework at all** (no Avalonia). This is **not** a
hope; it's an **enforced invariant** — [`tests/Firewall.Tests`](tests/Firewall.Tests) loads each of
those nine assemblies and fails the build if any references `Avalonia*`.

That firewall is why `circuitrf em`, `circuitrf lvs` and `circuitrf render` work from a terminal at
all: the EM run service, the DRC and LVS engines and the renderers all sit below the line, so the
headless answer and the on-screen one come from one implementation instead of two that drift.

The entire engine↔UI contract is two shapes: **design model down, `DataSet` up.** A replacement UI
re-implements only the *presentation* of those two; the engines, elaboration, analyses, result model
and file formats are untouched. That's the whole point of the firewall: **the simulator survives the
UI.** Detail in [`docs/design/ui-architecture.md`](docs/design/ui-architecture.md).

---

## Source layout

```
circuitRF/
├─ src/
│  ├─ RfCore/       Shared RF result/network library (no UI) — Touchstone I/O, S/Z/Y math and
│  │                renormalization, interpolation, the DataSet/DataCube result model, the
│  │                .npy/.mat/TSV exporters, loadpull surfaces, contours and FOM dialects
│  ├─ Core/         Design + elaboration, and the expression engine (no UI, no numerics) —
│  │                cells, instances, TestBench, analyses, measurements; flatten and resolve
│  │                top-down; the ComponentModel family including the SDD and the
│  │                substrate-aware microstrip models; the .cnl reader/writer
│  ├─ Engine/       Numeric layer (no UI) — sparse MNA, DC, S-parameters, parametric sweeps,
│  │                harmonic balance, loadpull + pursuit, and the two MoM EM kernels
│  │                (quasi-static cross-section, and full-wave planar with AIM)
│  ├─ Design/       Design-layer DOCUMENTS, and the code that reads, writes, validates and
│  │                CREATES them (no UI: it draws nothing and docks nothing). Referenced by
│  │                BOTH src/Ui and src/Cli, so there is exactly one of each:
│  │                  Layout/     .clay model, integer-DBU geometry, booleans, spatial index,
│  │                              .ctech technology + stackup, footprints, the DRC and LVS
│  │                              engines, the GDSII / DXF / Gerber+Excellon / .kicad_pcb
│  │                              readers AND writers, and Em/ — the .cem setup, its
│  │                              extractors and EmRunService
│  │                  Schematic/  .csch model, persistence and NetExtractor (EDITOR: src/Ui)
│  │                  Symbol/  Cells/  Workspace/  RailRf/  Matching/  Revision/  Results/
│  ├─ Render/       The Skia RENDERERS, below the firewall — schematic, symbol, layout and
│  │                bondwire, their themes, caches and LOD tiers, the .ccolor colour-theme
│  │                model, and the hit-test / handle / snap / overlay geometry shared with the
│  │                editors. Called by the GUI *and* by the CLI, so there is one renderer.
│  ├─ Ui/          Avalonia 12 + SkiaSharp — the only place UI-framework code lives: the
│  │                schematic, symbol, layout, .cem and .ctech EDITORS, the PCell generators,
│  │                the Data Display, the harmonicaRF / wBond / railRF views, the docs
│  │                factory's capture side, the updater, and the MVVM shell
│  ├─ Diagnostics/ The coded-diagnostic leaf: an id, typed arguments, an English template
│  ├─ Harmonica/   harmonicaRF's framework-free half — interactive harmonic loadpull (no UI)
│  ├─ WBond/       wBond's framework-free half — bondwire geometry + its own 3D MoM (no UI)
│  └─ Cli/         Headless driver + the engines' test harness (no UI) — docs/design/cli.md
├─ tools/          Programs that are not part of the application (none in circuitRF.slnx):
│                  DocGen (the user-docs factory), IconGen, the device workers (C), the
│                  Python PCell host, the release signer, the macOS build VM
├─ packaging/      One script per platform, each building everything that platform ships
├─ docs/           PRD.md, Development_Plan.md, design/ (the "why" — start here to go deep),
│                  skills/, sonnet-briefs/, and user/ (the shipped documentation — GENERATED;
│                  sources in docs/user/src/)
├─ examples/       The workspaces Tools ▸ Examples opens
├─ testdata/       Golden references + regression fixtures (the five heroes live here)
├─ tests/          Core, Engine, Ui, RfCore, Harmonica, WBond and Firewall test projects
├─ VERSION         The ONE place the version number is written
└─ CLAUDE.md       Standing project memory (architecture, invariants) — root + per subsystem
```
