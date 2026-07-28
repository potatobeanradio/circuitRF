# circuitRF

Lightweight cross-platform RF circuit simulator (DC, S-parameters, harmonic balance,
loadpull/sourcepull). **NOT a SPICE simulator.** See `docs/PRD.md` for scope, the five
hero circuits, and non-goals. This file is standing project memory — keep it current.

## Stack
- .NET 10 (LTS), C# 14
- Avalonia 12 (UI), SkiaSharp (canvas rendering), CommunityToolkit.MVVM (MVVM)
- CSparse.NET (sparse complex LU for large MNA), NumFlat (dense linear algebra)
- Consumes an `RfCore` library shared with splotRF (Touchstone I/O, network params, the
  `DataSet`/`DataCube` result types, interpolation, renormalization, plotting). **RfCore is an
  external sibling project**, cloned side-by-side and referenced via `ProjectReference`
  (`../RfCore/RfCore.csproj`) — it is *not* under `src/`.

## Build / test / run
- Build:   `dotnet build`
- Test:    `dotnet test`
- Run CLI: `dotnet run --project src/Cli -- <args>`

### Fast test loop — and what "gate item 1" means now (brief-benchmark-gate-split.md, 2026-07-27)

**The routine gate for every brief's item 1 is `dotnet test --filter "Category!=Benchmark"` — not the
full unfiltered suite.** This is a deliberate, explicit policy change, not a quiet filter: the full
suite (3382 tests) took ~5 minutes almost entirely because of the 500,000-shape `LayoutPerf` TIMED
sweeps, which cost real time on every commit for a diagnostic almost nobody needed routinely
(R-L2a-3: counters are the gate, wall-clock is the diagnostic). `Category!=Benchmark` runs everything
else — **3382 tests in ~32 s** — and is what "build+test green" means from here on, in every brief,
unless that brief's own text says otherwise (a brief can still ask for the full unfiltered suite
explicitly when it means to).

- **`Category=Benchmark`** — opt-in TIMED measurement sweeps only (median/p95 across profiles ×
  render paths × pan/zoom/full-extent, warmed up): `LayoutPerformanceBaselineTests.Baseline_500k` +
  `R8bCrossoverExperiment`, `LayoutLodMergeCacheBenchmarkTests.{LodOnly,Final}_FullExtent_500k` +
  `PathCache_500k_MemoryStaysUnderCap_TimeAndMemoryReported`,
  `LayoutSpatialIndexPerfTests.BulkLoad_500k_BuildTimeRecorded`. Run explicitly:
  `dotnet test --filter "Category=Benchmark"` (~5.5 min). **Required** when touching rendering, the
  spatial index, the path/instance caches, or LOD, and at any performance-phase boundary — named
  explicitly so this is a rule, not a judgement call.
- **500k's COUNTER coverage stays in the routine gate**, at negligible cost (~5 s total) — this is the
  part that actually catches an algorithmic regression (an accidental O(n)/O(n²) scan that bypasses
  the spatial index): `LayoutSpatialIndexPerfTests.Gated500k_CullingCountersStayCorrect` (one shared
  500k layout PER PROFILE, reused across a full-extent AND a zoomed-in assertion — no timing, no
  warm-up sweep). Verified to actually catch a regression, not just assumed: temporarily disabling the
  spatial-index culling query in `LayoutRenderer.Draw` turns this test red immediately.
- **`Category=Nightly`** keeps its own, now-narrower meaning — only
  `LayoutInstanceArrayPerfTests`'s 500k case remains tagged Nightly (cheap, ~1 s); `Benchmark`
  supersedes it for every other 500k `LayoutPerf` timed case (consolidated to one tag there, per the
  brief's own instruction, rather than leaving two overlapping tags on the same tests).
- **`Category=Slow`** (brief-test-suite-fast-loop) — engine-simulation classes in `Engine.Tests` that
  actually run the solver: `Hero3LoadpullTests`, `FreqSweptLoadpullTests`, `LoadpullCnlWriterRunTests`,
  `Hero3BPursuitTests`, `PursuitSearchDiagnosticTests` (all `Loadpull/`), and `Hero1BTests` (`Linear/` —
  a ~10k-component import+elaborate+solve gate, same profile as the loadpull tests even though it's
  outside that folder).

**Deferred, on purpose, and it must stay visible rather than quietly becoming permanent:** §5.1's 500k
**timing** target is now unmet AND unmeasured on routine runs. L2c's own measured shortfall (13-15×
over the 50 ms floor at full extent) is the reason — closing it needs the tiled raster cache (L2d), not
more per-shape optimization (see L2c's own completion note above). **Re-enabling routine 500k timing
coverage is part of L2d's own gate**, when that phase lands; until then, `Category=Benchmark` is how
anyone actively working on performance checks it.

**Layout/UI work** — the only projects layout work can plausibly touch or break (every layout brief since
L0a carries the guardrail "don't touch `src/Core`, `src/Engine`, `RfCore`"):
```
dotnet test tests/Ui.Tests --filter "Category!=Benchmark" --no-build
dotnet test tests/Firewall.Tests --no-build
```
Run as two commands — this SDK's `dotnet test` rejects more than one explicit project path in a single
invocation (`MSB1008: Only one project can be specified`).

**Whole-repo fast loop** (non-layout work, or a broader check before a layout PR):
```
dotnet test --filter "Category!=Slow&Category!=Benchmark" --no-build
```

**The full unfiltered suite (`dotnet test`, no filter) still exists and still runs everything** — reach
for it at genuine phase boundaries, or whenever the complete picture (including the deferred 500k
timing sweep) is actually wanted. It is simply no longer what "gate item 1" means by default.

Add `--no-build` after the first build of a session.

## Architecture — three layers, kept separate
1. **Design layer** (`src/Core`): Cells (Symbol/Schematic/Layout views), instances, nets,
   parameters, libraries — editable, serialized, human-readable. Layout view is a v1 placeholder.
2. **Elaboration layer** (`src/Core`): flatten hierarchy, resolve parameters/sweeps top-down,
   number nodes → an *elaborated netlist*. This is what the engine consumes.
3. **Numeric layer** (`src/Engine`): matrices, unknown vectors, the `DataSet`/`DataCube` result
   model. No UI, no domain types.

Source map: `src/Core` (layers 1–2 + the expression engine), `src/Engine` (layer 3 + analyses),
`src/Ui` (Avalonia), `src/Cli` (headless driver + test harness). `RfCore` is a **sibling project
outside this tree**, referenced via `ProjectReference`.

**UI firewall:** `RfCore`, `src/Core`, `src/Engine`, `src/Cli` must reference **no UI framework**
(no Avalonia) — all UI-framework code lives in `src/Ui`, so circuitRF can be re-skinned by replacing
`src/Ui` only. This is an **enforced** invariant (a CI assembly-reference check fails the build if the
core references Avalonia). Contract across the boundary: design model down, `DataSet` up. See
`docs/design/ui-architecture.md`.

## Invariants — do not violate
- Node 0 is ground.
- All AC / HB signal quantities (voltages, currents, spectra) are `System.Numerics.Complex`
  (double precision). Resolved parameter *values* are kinded **Real or Complex** (not forced
  complex); result cubes are likewise single-kind (`DataKind` Real or Complex).
- **The GUI never simulates the design layer directly — always elaborate first.**
- Never break the linear/nonlinear partition abstraction in the HB engine.
- Every analysis run returns a **`DataSet`** (a named collection of single-kind `DataCube`s);
  nothing invents its own result type. Measurements are added to the DataSet as named cubes.
- The numeric layer sees only fully-resolved parameter values (no expressions, no unbound vars).
- **Analyses attach to a `TestBench`, never to a `Cell`. Measurements also attach to the
  `TestBench`** and reference circuit quantities by absolute downward path (`V(X1.drain)`).

## Expressions, variables & cell parameters
One expression engine (tokenize → Pratt-parse → AST → evaluate; **never string substitution**)
serves global variables, cell parameters, SDD device equations, and measurements. See
`docs/design/expressions.md`.
- Cell parameters pass **top-down**: an instance binds overrides in the parent scope; the cell
  evaluates its own component values and its sub-cell passes in its scope.
- **Cycle detection is mandatory** across variables, cell-parameter defaults, and overrides.
- v1 language: variable refs; `+ - * / ^ ( )`; standard functions (`tan`, `tanh`, …);
  **conditionals** (`< <= > >= == !=`, `&& || !`, `if(cond,then,else)`); user-defined expression
  functions with arbitrary parameters. Values are kinded Real/Complex/Bool. Built to extend
  without breaking v1 files.
- The SDD's equations must stay transcribable into other tools' equation-defined devices (hero references depend on it).

## How to add a component type
Derive from `ComponentModel` (the single base for passive **and** active parts — "Device" is
reserved for its RF meaning, an active part): declare ports + params, then `Stamp(...)` (linear
contribution — the model *contributes* stamps; the engine *owns* the matrix) and/or `Evaluate(...)`
(nonlinear: returns `i`, `q`, `dg`, `dc`). Register it in the component-model factory. Add a
golden-reference test. See `docs/design/data-model.md` §5.
**The base type must already accommodate the v2 ASM-HEMT/Verilog-A path:** a thermal/self-heating
node (the native `FetModel` supports 2/3/4 ports, the 4th thermal), collapsible internal nodes,
terminal current, and charge-based capacitances (`q(v)` with `dq/dv`). Design for these now even
though v1 ships only built-ins + SDD.

## Validation expectations
Numerical changes require a `testdata/` regression test within the tolerance in the PRD.
The five heroes are the acceptance anchors (S-params 1e-6; HB Pout/gain ±0.01 dB, eff/PAE ±0.1 pp;
loadpull contours; two-tone IM2–IM5). References are owner-generated from other simulators using
the **identical SDD FET** so HB comparisons test our math, not a different transistor. CI runs the
suite on Windows, macOS, and Linux.

## Ask before
- Adding native (non-managed) dependencies (cross-platform risk).
- Anything marked out-of-scope for v1 in `docs/PRD.md` (transient, full Verilog-A/ASM-HEMT,
  a third-party cell database, layout view).

## Licensing
Core is **MIT**. Never ingest GPL code (some third-party simulators are GPL — learn from, never copy).
Keep a clean extension boundary so a future commercial **circuitRF+** can layer on without forking.

## Glossary
MNA, S-parameters, harmonic balance (HB), conversion matrix, loadpull/sourcepull, APFT, IMn,
DUT, Touchstone/SNP, SDD, OSDI/Verilog-A, `DataSet`/`DataCube`. Terms are defined where they
first appear in `docs/PRD.md` and the `docs/design/` notes.
