# circuitRF

Lightweight cross-platform RF circuit simulator (DC, S-parameters, harmonic balance,
loadpull/sourcepull). **NOT a SPICE simulator.** See `docs/PRD.md` for scope, the five
hero circuits, and non-goals. This file is standing project memory — keep it current.

## Stack
- .NET 10 (LTS), C# 14
- Avalonia 12 (UI), SkiaSharp (canvas rendering), CommunityToolkit.MVVM (MVVM)
- CSparse.NET (sparse complex LU for large MNA), NumFlat (dense linear algebra)
- Consumes `RfCore` (Touchstone I/O, network params, the `DataSet`/`DataCube` result types,
  interpolation, renormalization, plotting). **`RfCore` was merged into this repository via
  `git subtree` on 2026-07-29** (brief-housekeeping-tearoff-palette-repo.md §6 — splotRF, the
  other consumer of the standalone RfCore repo, is being retired; two repos confused new
  contributors) — it now lives at `RfCore/` in this repo's own root (history preserved, not
  squashed) and is referenced via `ProjectReference` (`../../RfCore/src/RfCore.csproj` from a
  `src/*`/`tests/*` project). It is *not* under `src/` — same architectural boundary as before,
  just no longer a separate clone.

## Build / test / run
- Build:   `dotnet build`
- Test:    `dotnet test`
- Run CLI: `dotnet run --project src/Cli -- <args>`

### `dotnet test` is fast by default (brief-test-default-fast.md, 2026-07-28)

**Plain `dotnet test`, with no flags, is the routine gate — it is fast by construction, not by
convention.** Repo-root `circuitrf.runsettings` (`TestCaseFilter: Category!=Benchmark`) is wired in via
`Directory.Build.props`'s `RunSettingsFilePath`, so every invocation — `dotnet test` at the root,
`dotnet test tests/Ui.Tests`, an IDE test run, CI — inherits the exclusion automatically. There is
nothing to type and nothing to forget. This supersedes the prior two-tag, filter-must-be-typed schemes
from brief-benchmark-gate-split.md and brief-test-suite-fast-loop.md: `Category=Nightly` is retired
and `Category=Slow` is gone as a category (its former members are either untagged, having been
measured under the threshold, or folded into `Benchmark`).

- **Repo root, no flags: 5,169 tests in ~30 s.** Per-project (`dotnet test tests/Ui.Tests`) is
  likewise fast on its own (~11-12 s). This is what "build+test green" means in every brief from here
  on, unless that brief's own text says otherwise.
- **`RfCore.Tests` IS in `circuitrf.slnx` and IS covered by a plain `dotnet test`** (2026-07-30; it was
  not, until then — an older note in `src/Ui/CLAUDE.md` says otherwise and is marked superseded). 281
  routine tests, ~4 s. Its proprietary loadpull fixtures are git-ignored, so on a fresh clone 56 of them
  report **Skipped with a reason** via `FixtureFact`/`FixtureTheory` rather than failing — do not
  "repair" those skips by committing lab data.
- **`Category=Benchmark`** is the *only* opt-in tag. Applied mechanically wherever a test's measured
  wall-clock exceeds ~5 s — and, since 2026-07-30, also to a test that is *fast but wall-clock-sensitive*
  and therefore cannot survive the parallel-start burst of a full-solution run (`RfCore.Tests`'
  `Rbf2DPerfTests`, 4 methods: millisecond-fast, but a ~0.3 ms operation reads ~10 ms per sample under
  full-suite load, so even a best-of-20 gate flaked). **Do not untag those on the grounds that they run
  quickly** — they are tagged for the purpose the mechanism serves, not the letter of the ~5 s rule.
  Currently 23 tests repo-wide (16 in `Ui.Tests`, 3 in `Engine.Tests`, 4 in `RfCore.Tests`): the
  500,000-shape `LayoutPerf` TIMED sweeps
  (`LayoutPerformanceBaselineTests.Baseline_500k`/`Baseline_50k` + `R8bCrossoverExperiment`,
  `LayoutLodMergeCacheBenchmarkTests.{LodOnly,Final}_FullExtent_500k` +
  `PathCache_500k_MemoryStaysUnderCap_TimeAndMemoryReported`,
  `LayoutSpatialIndexPerfTests.BulkLoad_500k_BuildTimeRecorded`,
  `LayoutInstanceArrayPerfTests`'s 500k case) plus the handful of `Engine.Tests` loadpull/pursuit
  methods whose individual runtime crosses the threshold (most loadpull/pursuit tests do not and stay
  untagged and routine).
- **Opt in with `dotnet test --settings circuitrf.benchmark.runsettings`, not `--filter`.** This
  SDK's VSTest version ANDs a command-line `--filter` with the project's own `TestCaseFilter` rather
  than overriding it, so `--filter "Category=Benchmark"` resolves to the impossible AND of
  `Category!=Benchmark` and `Category=Benchmark` and silently matches nothing — verified directly, not
  assumed. Passing `--settings` on the command line does override the project-level
  `RunSettingsFilePath` cleanly, so `circuitrf.benchmark.runsettings` (`TestCaseFilter:
  Category=Benchmark`) is the actual one-liner opt-in path. Run it (~5 min) when touching rendering,
  the spatial index, the path/instance caches, or LOD, and at any performance-phase boundary.
- **500k's COUNTER coverage stays in the default gate**, at negligible cost (~5 s total) — this is the
  part that actually catches an algorithmic regression (an accidental O(n)/O(n²) scan that bypasses
  the spatial index): `LayoutSpatialIndexPerfTests.Gated500k_CullingCountersStayCorrect` (one shared
  500k layout PER PROFILE, reused across a full-extent AND a zoomed-in assertion — no timing, no
  warm-up sweep). Verified to actually catch a regression, not just assumed: temporarily disabling the
  spatial-index culling query in `LayoutRenderer.Draw` turns this test red immediately.
- **Tagging a new slow test:** measure it (a TRX run reports per-test duration); if it is at or above
  ~5 s, add `[Trait("Category", "Benchmark")]`. Below that, leave it untagged — it belongs in the
  default gate. A `[Theory]`'s `InlineData` cases can't be tagged individually, so a mixed-cost Theory
  (e.g. `LayoutPerformanceBaselineTests`'s former combined `Baseline`) should be split into separate
  `[Theory]` methods by cost tier so only the slow tier carries the tag.

**Deferred, on purpose, and it must stay visible rather than quietly becoming permanent:** §5.1's 500k
**timing** target is unmet and lives only in `Category=Benchmark` now. L2c's own measured shortfall
(13-15× over the 50 ms floor at full extent) is the reason — closing it needs the tiled raster cache
(L2d), not more per-shape optimization (see L2c's own completion note above). **Re-enabling routine
500k timing coverage is part of L2d's own gate**, when that phase lands; until then,
`Category=Benchmark` via `--settings circuitrf.benchmark.runsettings` is how anyone actively working on
performance checks it.

**Layout/UI work** — the only projects layout work can plausibly touch or break (every layout brief since
L0a carries the guardrail "don't touch `src/Core`, `src/Engine`, `RfCore`"):
```
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```
Run as two commands — this SDK's `dotnet test` rejects more than one explicit project path in a single
invocation (`MSB1008: Only one project can be specified`).

**The full unfiltered suite still exists** (bypass the default filter with an empty override
`--settings` file, or `--filter "Category=Benchmark|Category!=Benchmark"`) — reach for it only at
genuine phase boundaries, or whenever the complete picture (including the 500k timing sweep) is
actually wanted. It is not what routine `dotnet test` runs, and does not need to be.

Moving `Benchmark` tests to a separate runner outside `dotnet test` discovery entirely (so they
wouldn't even need an opt-in filter) was considered and not done — restructuring the ~19 tagged methods
across 3 files into a standalone project/entry point is more than the brief's "stop and report if not
cheap" threshold, and the `--settings` opt-in already satisfies the brief's gates without it.

Add `--no-build` after the first build of a session.

## Architecture — three layers, kept separate
1. **Design layer** (`src/Core`): Cells (Symbol/Schematic/Layout views), instances, nets,
   parameters, libraries — editable, serialized, human-readable. Layout view is a v1 placeholder.
2. **Elaboration layer** (`src/Core`): flatten hierarchy, resolve parameters/sweeps top-down,
   number nodes → an *elaborated netlist*. This is what the engine consumes.
3. **Numeric layer** (`src/Engine`): matrices, unknown vectors, the `DataSet`/`DataCube` result
   model. No UI, no domain types.

Source map: `src/Core` (layers 1–2 + the expression engine), `src/Engine` (layer 3 + analyses),
`src/Ui` (Avalonia), `src/Cli` (headless driver + test harness). `RfCore` lives at this repo's
own `RfCore/` root (merged via `git subtree`, §6 above) — still referenced via `ProjectReference`,
still architecturally outside `src/*`, just no longer a separate clone.

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
