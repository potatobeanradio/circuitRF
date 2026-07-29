# circuitRF

**A lightweight, cross-platform RF circuit simulator — for the RF community, by the RF community.**

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Platforms](https://img.shields.io/badge/platforms-Windows%20%7C%20macOS%20%7C%20Linux-blue.svg)](#getting-started)
[![UI: Avalonia](https://img.shields.io/badge/UI-Avalonia%2012-7B68EE.svg)](https://avaloniaui.net/)

circuitRF is an EDA tool for developing RF circuits.  It can analyze the frequency response and nonlinear behavior of RF circuits — from a handful of components to hierarchical, multi-port designs with thousands of components — using **DC**, **S-parameter**, and **harmonic-balance** analyses, plus first-class **loadpull / sourcepull**. The analyses and the workflow are built around the RF/microwave problem, the file formats are human-readable, and the headline goal is to make loadpull as easy as a few clicks. circuitRF also supports layout for PCB and MMIC design and includes export capabilities to Gerber, DXF and GSDII formats.

circuitRF is for RF practitioners or researchers who can't justify the cost of traditional tools (or find those tools too heavy for a quick investigation): **power-amplifier, LNA, and mixer designers; RF EDA and device-modeling engineers; academic researchers; and capable hobbyists.** It is written in **C# / .NET 10**, with an **Avalonia 12** GUI rendered through **SkiaSharp**, and it was built largely **AI-assisted** (see
[AI-assisted development](#ai-assisted-development)).

> **Status:** v1 *alpha*. The engine (S-parameters, nonlinear DC, single/two-tone harmonic balance,
> parametric sweeps, loadpull) runs the acceptance circuits from both the CLI and the GUI; the Avalonia
> schematic/symbol editors and the Data Display — including end-to-end loadpull simulation,contour plotting
> and interactive contour markers — are in place. What's left is packaging and hardening
> ([Roadmap & status](#roadmap--status)). Expect rough edges, and please file issues.

---

## Screenshots

### Schematic editor
![circuitRF schematic editor](docs/images/schematic-editor.png)
<!-- IMAGE TO CREATE: docs/images/schematic-editor.png
     The schematic editor showing a single-FET power amplifier (Hero 2): an SDD FET with gate/drain bias
     (Vdc sources), input and output matching networks (R/L/C), a P1Tone RF source on the left, a Term on
     the output. Left: the Library Palette with component glyph tiles. Right/bottom: the Analyses panel
     with an HB power sweep set up. Show a couple of net labels and the green junction dots so the wiring
     reads clearly. -->
*Build hierarchical RF circuits on a virtualized canvas: drag from the palette, wire, label nets, set
parameters and sweeps, and Run.*

### Symbol editor
![circuitRF symbol editor](docs/images/symbol-editor.png)
<!-- IMAGE TO CREATE: docs/images/symbol-editor.png
     The symbol editor with a custom cell symbol in progress — e.g. a two-port amplifier block: a body
     rectangle, a few drawing primitives (lines/arc/text label), and two pins snapped to the connection
     grid with their port numbers shown. Left toolbar: the drawing tools (line, rect, circle, arc, text,
     pin). Show the fine authoring grid. -->
*Draw the glyph for any cell and place its connection pins — the same renderer the schematic uses.*

### Data Display — loadpull contours
![circuitRF loadpull contours on a Smith chart](docs/images/data-display-loadpull-contour.png)
<!-- IMAGE TO CREATE: docs/images/data-display-loadpull-contour.png
     The Data Display showing a loadpull result on a Smith chart: Pout (dBm) and PAE (%) contours over the
     load-Γ plane, with the MXP (max power) and MXE (max efficiency) markers called out, and a couple of
     interactive markers reading off impedance/value. A trace inspector card on the right shows the
     metric/colormap selection. Optionally a second rectangular plot (power sweep) docked alongside. -->
*Plot S-parameters, spectra, power sweeps, and loadpull contours; overlay measured Touchstone/`.spl`/
`.lpcwave` data on simulated results.*

---

## Contributors are welcome — *especially RF domain experts*

circuitRF is meant to be **community-driven, by and for the RF engineering community.** We value **RF
domain knowledge as much as software experience.** If you design power amplifiers, LNAs, or mixers; build
RF EDA tooling; do device modeling; or develop transistor technology (GaN-on-SiC, GaN-on-Si, LDMOS, …),
**you are exactly who this project needs** — and circuitRF is a great place to use AI to build the
simulation features *you* want.

You do **not** need to be a professional software developer. If you've scripted in MATLAB or Python, you
have enough to start. Pair yourself with [Claude Code](https://www.anthropic.com/claude-code) (or your
AI assistant of choice) and let it do the heavy lifting on the C#.

**The recommended first contribution:** browse the [component library](#adding-a-standard-library-component)
for a part you wish were there — a diode, a BJT, a microstrip line, an ideal transformer, a coupler — and
add it. circuitRF ships a step-by-step skill for exactly this:

> **[`docs/skills/adding-a-library-component.md`](docs/skills/adding-a-library-component.md)** — hand this
> file to Claude Code, tell it which component you want, and it will walk the whole procedure (palette,
> symbol, ports, the engine stamp, the factory, and a regression test).

It's the fastest way to learn the codebase, it's genuinely useful to other users, and it's the kind of
contribution where *your* RF expertise — not your C# fluency — is the scarce ingredient. See
[Contributing](#contributing) for the full picture.

---

## Architecture

circuitRF is built in **strictly one-directional layers** — dependencies point **up the stack only**, and
nothing below the UI knows the UI exists. This is what keeps the simulator (the actual value of the
product) independent of any GUI framework. Full detail:
[`docs/design/ui-architecture.md`](docs/design/ui-architecture.md).

```
  RfCore (sibling repo)   shared result/network library: Touchstone I/O, S/Z/Y math,
        ▲                  the DataSet/DataCube result model, loadpull readers/writers
        │
  src/Core      Design + Elaboration: cells, instances, nets, parameters, the expression
        ▲        engine; flatten + resolve → an "elaborated netlist".  No UI, no numerics.
        │
  src/Engine    Numeric layer: sparse MNA, DC, S-parameters, harmonic balance, loadpull.
        ▲        Consumes the elaborated netlist, produces a DataSet.  No UI.
        │
  src/Ui        Presentation: Avalonia 12 + SkiaSharp. Schematic/symbol editors, Data
                 Display, workspace. Depends on Core + Engine + RfCore. Nothing depends on it.

  src/Cli       Headless driver — depends on Core/Engine/RfCore, NOT on src/Ui. Proof the
                 engine is fully usable with no GUI; the engine's primary test harness.
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

### How rendering works — SkiaSharp and Avalonia

The GUI is **Avalonia 12** (the cross-platform .NET UI framework — same window/menu/dock machinery on all
three OSes). But circuitRF does **not** render schematics or plots as Avalonia controls — a 10,000-component
schematic would die under one control per component. Instead, both the schematic canvas and the Data
Display draw themselves with **SkiaSharp** (a fast 2D graphics library) through a custom control, with
**viewport virtualization** and a **spatial index** for hit-testing and pan/zoom.

The split is deliberate: a **pure renderer** (`SchematicRenderer`, the plot renderers — Skia only, no
Avalonia types) draws a model + transform onto a surface, and a thin **Avalonia control** hosts that
surface and pumps input events. The rendering investment lives in the renderer; the Avalonia control is
just a host.

### The framework firewall (any UI could replace Avalonia)

Avalonia may someday be replaced by something better. The circuitRF *engines* must be skinnable by a new
UI with as little trouble as possible — so **`RfCore`, `src/Core`, `src/Engine`, and `src/Cli` reference no
UI framework at all** (no Avalonia). This is **not** a hope; it's an **enforced invariant** — a CI test
loads each non-UI assembly and fails the build if it references `Avalonia*`.

The entire engine↔UI contract is two shapes: **design model down, `DataSet` up.** A replacement UI
re-implements only the *presentation* of those two shapes; the engine, elaboration, analyses, result model,
net extraction, and file formats are untouched. (SkiaSharp is allowed below the UI — it's a graphics
library, not a UI framework — but in practice the renderers live with the display layer.) That's the whole
point of the firewall: **the simulator survives the UI.** Detail in
[`docs/design/ui-architecture.md`](docs/design/ui-architecture.md).

---

## Source layout

```
circuitRF/
├─ src/
│  ├─ Core/            Design + elaboration layers, and the expression engine (no UI, no numerics)
│  │  ├─ Design/         cells, instances, TestBench, analyses, measurements
│  │  ├─ Elaboration/    flatten hierarchy, resolve parameters/sweeps, number nodes
│  │  ├─ Devices/        ComponentModel base + built-in models (R/L/C, FET, SDD, TLIN, …)
│  │  ├─ Expressions/    tokenizer, Pratt parser, evaluator, automatic differentiation
│  │  ├─ Netlist/        .cnl reader/writer
│  │  └─ Data/           DataSet/DataCube result model (mirrors RfCore)
│  ├─ Engine/          Numeric layer — consumes the elaborated netlist, returns a DataSet (no UI)
│  │  ├─ HarmonicBalance/  HB residual, conversion-matrix Jacobian, single/two-tone, continuation
│  │  └─ Loadpull/         loadpull + pursuit engines, .gam terminations
│  ├─ Ui/              Avalonia 12 + SkiaSharp — the only place UI-framework code lives
│  │  ├─ Schematic/      net extractor, editable model, library palette, placement
│  │  ├─ Renderers/      pure SkiaSharp renderers (schematic, symbols) — no Avalonia types
│  │  ├─ Controls/       Avalonia custom controls hosting Skia surfaces + input
│  │  ├─ DataDisplay/    DataCube-native plots (Smith/polar/rect/table), loadpull surface, contours
│  │  ├─ ViewModels/  Views/  Commands/  Theming/  …   the MVVM shell
│  └─ Cli/             Headless driver + the engine's test harness (no UI)
├─ docs/
│  ├─ PRD.md             what v1 must do + the five "hero" acceptance circuits
│  ├─ Development_Plan.md  the roadmap, status, and AI-workflow strategy
│  ├─ design/            per-subsystem design notes (the "why")  ← start here to go deep
│  └─ skills/            step-by-step procedures (e.g. adding-a-library-component.md)
├─ testdata/           golden references + regression fixtures
└─ CLAUDE.md           standing project memory (architecture, invariants) — root + nested per subsystem
```

`RfCore` is an **external sibling repository**, cloned *next to* circuitRF and referenced via
`ProjectReference` (`../RfCore/RfCore.csproj`) — it is shared with [splotRF](https://github.com/potatobeanradio/splotRF)
and is **not** under `src/`. See [Getting started](#getting-started).

---

## Getting started

You'll develop on **Windows**, **macOS**, or **Linux**. The steps are nearly identical on all three; where
they differ, it's called out. If you're new to .NET, just follow along — the commands are copy-paste.

### 1. Install the tools

| Tool | Why | Get it |
|---|---|---|
| **.NET 10 SDK** | builds and runs circuitRF | <https://dotnet.microsoft.com/download/dotnet/10.0> |
| **Git** | clone the repos | <https://git-scm.com/downloads> |
| **Visual Studio Code** | edit + debug (lightweight, cross-platform) | <https://code.visualstudio.com/> |
| VS Code **C# Dev Kit** extension | C# editing/IntelliSense/debug in VS Code | <https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csdevkit> |

> Prefer a full IDE? **Visual Studio 2022** (Windows) or **JetBrains Rider** (all OSes) work too — open
> the `src/` projects directly. VS Code is the lightest path and what most contributors use.

Verify the SDK is installed:
```bash
dotnet --version      # should print 10.x.x
```

### 2. Clone circuitRF *and* RfCore side by side

circuitRF references `RfCore` as a sibling folder, so clone **both into the same parent directory**:

```bash
# pick a working folder, e.g. ~/code
cd ~/code

git clone https://github.com/potatobeanradio/circuitRF.git
git clone https://github.com/potatobeanradio/RfCore.git

# you should now have:  ~/code/circuitRF   and   ~/code/RfCore   (siblings)
```

### 3. Build and test

```bash
cd ~/code/circuitRF

dotnet build      # restores packages + compiles everything
dotnet test       # runs the regression suite (engine math, file round-trips, UI logic)
```

A green `dotnet test` means your environment is good. (On Windows use the same commands in PowerShell or
the terminal; on macOS/Linux use any shell.)

**Loadpull/contour test fixtures are not included.** A handful of tests under `Engine.Tests` and
`Ui.Tests` read real lab-measured GaN FET `.spl`/`.lpcwave` files from `testdata/spl_test_data/` and
`testdata/lpwave_test_data/`. That data is proprietary and is not committed to the repo (see
`.gitignore`). On a fresh clone those tests report as **Skipped**, with a reason naming the missing
path — they never fail. Contact the repo owner if you need the files for full coverage.

---

## Running circuitRF

### Launch the GUI

```bash
cd ~/code/circuitRF
dotnet run --project src/Ui
```

This opens the desktop app: build a schematic, set up analyses, hit **Run**, and view results in the Data
Display. New to it? Start a scratch schematic (**File → New Schematic**), drag a few parts from the
**Library Palette**, wire them, and explore.

### Run headless from the command line

The engine is fully drivable without the GUI — this is how it's tested, and how you'd script a batch. The
CLI takes a `.cnl` netlist (a human-readable text circuit description):

```bash
# S-parameters: sweep 1–3 GHz in 50 MHz steps, write a Touchstone file
dotnet run --project src/Cli -- sparam mycircuit.cnl --freq 1GHz:3GHz:50MHz -o mycircuit.s2p

# Dump the elaborated netlist (flattened + parameters resolved) — great for debugging
dotnet run --project src/Cli -- elab mycircuit.cnl

# Help
dotnet run --project src/Cli
```

Frequencies accept `1GHz`, `100MHz`, or bare Hz (`1e9`). Harmonic-balance and loadpull runs are currently
driven from the GUI's **Run** button (which uses the same headless run path); a dedicated CLI verb for them
is on the roadmap.

### Run a netlist through an engine (in code)

The whole pipeline is three calls — read → elaborate → run — which is exactly what the CLI does:

```csharp
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine;

var (lib, testbench) = CnlReader.ReadFile("mycircuit.cnl");
var netlist          = new Elaborator(lib).Elaborate(testbench);
var dataset          = SParameterEngine.Run(netlist, freqsHz);   // → a DataSet of DataCubes
```

---

## Adding a standard library component

This is the **recommended first contribution** — and the most valuable thing an RF expert can do. circuitRF
ships ~20 built-in parts (R, L, C, Vdc, RF tone source, Ground, Term, Pin, current probe, FET-SDD, SDD,
Z-port, SnP/Touchstone, nonlinear C, mutual inductance, ideal transmission line, tuners, …). There are many
useful parts **not** yet in the library — a **diode**, a **BJT**, microstrip elements (**MLIN/MSTEP/MBEND**),
an **ideal transformer**, **coupled lines**, a **circulator/isolator**, lumped **attenuator pads**, and more.

Adding one is a well-trodden path:

1. Read the skill: **[`docs/skills/adding-a-library-component.md`](docs/skills/adding-a-library-component.md)**.
   It covers both archetypes — a **device** (has ports, stamps into the engine; worked example: the ideal
   transmission line) and an **annotation** (no ports, e.g. VAR/MEAS) — and lists every file to touch.
2. The component registry is a single hub — `ComponentTypeRegistry` (`src/Ui/Schematic/ComponentTypeRegistry.cs`);
   the palette is generated from it, so you never edit palette UI code.
3. A new device subclasses **`ComponentModel`** (one base for passive *and* active parts), declares its
   ports + parameters, and implements `Stamp(...)` (linear contribution) and/or `Evaluate(...)`
   (nonlinear `i`, `q`, and their derivatives). Register it in the model factory and add a golden-reference
   test.
4. The companion device walkthrough is
   [`docs/sonnet-briefs/palette-contributor-guide.md`](docs/sonnet-briefs/palette-contributor-guide.md).

The honest pitch: hand the skill file and your component's equations to Claude Code, and it will do most of
the C#. Your job is the RF physics — the stamp, the model equations, the reference to check against.

---

## Roadmap & status

circuitRF is **v1 alpha**. The engine and editors work; some features are still landing. The five "hero"
circuits in [`docs/PRD.md`](docs/PRD.md) (a 4-port S-parameter network, a single-FET PA HB power sweep, a
loadpull, a 2-stage PA, and a two-tone IM case) are the acceptance anchors, validated against other
simulators' references.

**Done:** S-parameters; nonlinear DC + diode/FET/BJT + the SDD with automatic differentiation; single- and
two-tone harmonic balance with continuation; parametric sweeps; the `DataSet`/`DataCube` result model;
loadpull/sourcepull + pursuit; `.mat` / `.npy` / Touchstone / `.spl` / `.lpcwave` export; the Avalonia
schematic + symbol editors, library palette, workspace/project tree, hierarchy navigation, and undo/redo;
the `DataCube`-native Data Display with Smith/polar/rect/table plots; **end-to-end loadpull contour
plotting** (engine → RBF surface fit → contour render, for simulated *and* measured data); and
**interactive markers**, including markers that read and drag on the contour surface.

What's left for the v1 release is **hardening (Phase 8)** — installers for Windows/macOS/Linux (mirroring
splotRF's recipes), broader docs, and keeping the `testdata/` regression suite green in CI on all three OSes.

**Deferred to v2:**
- **Layout editor.** The cell carries the *concept* of a Layout view; it is a placeholder in v1 (no 2D/3D
  layout, no EM).
- **Sparse block Jacobian for HB at scale.** v1 uses a dense per-block Jacobian; a sparse solve is the path
  to very large nonlinear problems.
- **Verilog-A / OSDI backend → ASM-HEMT.** v1 ships built-in models + the SDD; the device interface is
  already designed to accept an OSDI/OpenVAF backend without redesign.
- **Noise analysis — an open green field.** circuitRF v1 has **no** noise figure, phase noise, or
  noise-parameter (Fmin, Γopt, Rn) extraction. This isn't a technical wall — the linear engine already
  builds what a noise pass needs, and the `.spl`/`.lpcwave` importers already parse noise columns — it's
  simply **unbuilt**, left open on purpose for a contributor (an LNA designer, a device-modeling expert) who
  wants to own it. A solid noise pass is a strong candidate for a **major v2 feature**. If that's you, dig in.

Full roadmap and current status: [`docs/Development_Plan.md`](docs/Development_Plan.md).

---

## Contributing

**Contributions are welcome and encouraged.** circuitRF is community-driven, by and for the RF community,
and **RF domain knowledge counts as much as software experience.** You don't need to be a career
programmer — MATLAB/Python scripting experience plus an AI assistant is plenty.

**Good first contributions:**
- **Add a missing library component** (see [above](#adding-a-standard-library-component)) — the
  highest-leverage starting point, and a great use of AI.
- Build a circuit in the schematic editor and **report what's confusing or broken** — alpha feedback is
  gold.
- Improve a design note in `docs/design/`, or a `CLAUDE.md`, where the docs lag the code.
- Pick up a **roadmap** item (the noise green field is wide open).

**The ground rules:**
- The architecture is layered and the **UI firewall is enforced** — keep Avalonia out of
  `RfCore`/`Core`/`Engine`/`Cli` (a CI test will catch you). Renderers stay Skia-only.
- **Every numerical change needs a `testdata/` regression test** within the tolerance the PRD states.
- The core is **MIT** — never ingest GPL code.
- Each subsystem has a `CLAUDE.md` with its local conventions; read the relevant one before diving in.

Open an issue to discuss anything substantial before a large PR, so we can point you at the right design
note (and save you rework).

---

## AI-assisted development

circuitRF was built largely with AI assistance (primarily [Claude](https://www.anthropic.com/claude) /
[Claude Code](https://www.anthropic.com/claude-code)), and **AI-assisted contributions are first-class
here.** The codebase is structured for it: spatial `CLAUDE.md` memory files capture the invariants and
local conventions of each subsystem, `docs/design/` holds the reasoning behind each part, and
`docs/skills/` holds step-by-step procedures you can hand directly to an AI agent.

This is the deliberate bet of the project: **an RF expert with an AI assistant can build the simulation
features they need.** If that describes you, you're in the right place.

---

## License

circuitRF's core is released under the **[MIT License](LICENSE)**. (No GPL/copyleft code is ingested; a
future commercial superset, if any, layers on through a clean extension boundary without forking the core.)

---

## Acknowledgments

- **[Avalonia](https://avaloniaui.net/)** (cross-platform UI), **[SkiaSharp](https://github.com/mono/SkiaSharp)**
  (2D rendering), **[CSparse.NET](https://github.com/wo80/CSparse.NET)** (sparse complex LU),
  **NumFlat** (dense linear algebra), **[Clipper2](https://github.com/AngusJohnson/Clipper2)**
  (integer-coordinate polygon clipping and offsetting, used by the layout editor — Boost Software
  License), and **[CommunityToolkit.MVVM](https://github.com/CommunityToolkit/dotnet)**.
```
