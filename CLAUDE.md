# circuitRF

Lightweight cross-platform RF circuit simulator (DC, S-parameters, harmonic balance,
loadpull/sourcepull). **NOT a SPICE simulator.** See `docs/PRD.md` for scope, the five
hero circuits, and non-goals. This file is standing project memory — keep it current.

## Searching this repo

**Use `grep` (or ripgrep) directly whenever possible instead of spawning a search agent.** This
repo's structure is well-known and its files are plain text — a targeted `grep -n` finds a
symbol, class, or XAML control faster and far cheaper than delegating a "find X" task to an
agent. Reach for an agent only when the search genuinely needs multi-step reasoning across many
unrelated locations, not for straightforward lookups.

## Resolving Issues
If significant findings were found during bug fixes or changes, write to the relevant RESOLVED.md 
files never CLAUDE.md. This helps keep the CLAUDE.md files small.

Never quote the owner or users in git commit comments, RESOLVED.md files or anywhere in the code.
Instead, briefly paraphrase owner/user messages. Pre-existing quotes are ok.

## Stack
- .NET 10 (LTS), C# 14
- Avalonia 12 (UI), SkiaSharp (canvas rendering), CommunityToolkit.MVVM (MVVM)
- CSparse.NET (sparse complex LU for large MNA), NumFlat (dense linear algebra)
- `RfCore` (Touchstone I/O, network params, the `DataSet`/`DataCube` result types, interpolation,
  renormalization, plotting) is **a first-class project of this repository, exactly like `Core`,
  `Engine`, and `Ui`** — `src/RfCore/` with its tests at `tests/RfCore.Tests/`, both listed in
  `circuitrf.slnx`, referenced via ordinary `ProjectReference`.

  **It is NOT a subtree, and there is nothing left to "un-subtree" (2026-07-30).** It arrived via
  `git subtree add` on 2026-07-29 purely to preserve history
  (brief-housekeeping-tearoff-palette-repo.md §6 — splotRF, the other consumer of the standalone
  RfCore repo, was being retired). **"Being a subtree" is not a persistent state in git**: there is
  no `.gitmodules`, no config, no live link to anything. RfCore's 24 original commits are a
  permanent *second parent* of merge `0bd04db`, and `git blame` on any file under `src/RfCore/`
  still resolves to its original author and date. The only residue is a three-line
  `git-subtree-dir/-mainline/-split` trailer in that one old commit message, which is inert unless
  someone runs `git subtree pull` — **so don't.** Treat `src/RfCore/` as ordinary first-party code.

  *Known git wrinkle, not a history loss:* `git log --follow <path>` does not cross the merge (a
  documented `--follow`-vs-merges limitation). `git blame` does. To read the pre-merge history
  directly: `git log 0bd04db^2 -- src/Data/DataCube.cs` (the *old* path, on the pre-merge parent).

  **The architectural boundary is unchanged and does not depend on directory placement** — it is
  enforced by assembly-reference checks in `tests/Firewall.Tests`, which is why moving RfCore under
  `src/` cost nothing. RfCore still references no UI framework, and nothing in it may.

## Build / test / run
- Build:   `dotnet build`
- Test:    `dotnet test`
- Run CLI: `dotnet run --project src/Cli -- <args>`
  Verbs: `sparam`, `dc`, **`hb`**, **`lp`**, **`lpp`**, **`em`**, `elab`. **The CLI has its own design doc —
  `docs/design/cli.md`** — covering the five-step anatomy of a run verb, the stdout/stderr split, and
  the rules below; read it before adding a verb. `hb`/`lp`/`lpp` run the netlist's harmonic-balance,
  loadpull and loadpull-pursuit analyses, and each runs the whole sweep when a `parametric_sweep`
  wraps it (naming the inner analysis is promoted to its wrapper, since running the inner alone
  silently drops the sweep axis — a freq-swept loadpull is exactly this shape). They evaluate the
  TestBench's `measure` lines exactly as the GUI does, so a `.cnl` that works headless works when
  opened. `--set var=expr` overrides a global before elaboration; **every other override replaces the
  DIRECTIVE in the TestBench**, because the sweep engine re-resolves it at each point and an override
  handed to one engine instance is discarded after the first. `-o out.{mat,npy,txt}` exports, and `lp`
  also writes `.spl`/`.lpcwave` — the loadpull interchange the Data Display reads back.
  **`em` takes a `.cem` and needs no other arguments** (2026-08-26). Both references it must follow
  are WALK-UPS, not flags: the layout is relative to the nearest ancestor `.cws` above the `.cem`
  (falling back to the `.cem`'s own directory), and the technology resolves against the LAYOUT's own
  parent workspace. It writes exactly where Simulate writes — `EmRunService.ResolveSnpPath` is
  predictable by design so a schematic's SnP reference survives a re-run — and `-o` moves the
  Touchstone only, never the `.npy` that carries the diagnostics group. A refusal stays a refusal:
  `Refused`/`NoLayout`/`EngineError` exit 1 with the run service's own sentence, `Cancelled` exits 130.
  The gate is `tests/Ui.Tests/Em/EmCliVerbTests.cs`, which compares the CLI's `.sNp` **byte for byte**
  against `EmRunService.Run`'s — every line but the provenance write-timestamp, which the file carries
  by design; the `.npy` matches with no exception.
- Package: **exactly one script per platform, and each builds everything that platform ships** —
  `packaging/windows/build-windows.ps1` (9 files: `.msi` x64/arm64/x86 in both install scopes, plus
  the `.zip` the updater fetches), `packaging/macos/build-macos.sh` (2 `.dmg`s, both architectures;
  wraps the existing `src/Ui/bundleFor*MacOS.sh`), `packaging/linux/build-linux.sh` (4 files: `.deb`
  and `.tar.gz` for x64/arm64; `fpm` is needed only for the `.deb`). All write to `dist/`. Flags
  narrow a run; **the no-argument form is the release form**, because 1.0.0-beta.2 shipped 7 of its
  15 artifacts when Windows defaulted to one architecture in one scope and Linux was two scripts —
  silently, since a missing update payload stops updates with no error. Held by
  `tests/Ui.Tests/PackagingScriptTests.cs`.
  **Each must run ON its own platform** (WiX is Windows-only, `codesign`/`hdiutil` macOS-only, and
  the Windows PE icon is only embedded when the publish happens on Windows). Step-by-step
  instructions live in `BUILDING.md`, which `README.md` links to; keep the two in step.
  App icons (`.icns`/`.ico`/`.png`) are **build products** rasterised from the committed brand SVGs
  by `dotnet run --project tools/IconGen`, which every packaging script runs first — no icon binary
  is ever committed.

  **Two packaging rules exist because breaking either fails silently** (both held by
  `tests/Ui.Tests/PackagingScriptTests.cs`, both learned from a real Windows build, 2026-08-18):
  - **Every `.ps1` under `packaging/` must be pure ASCII.** Windows PowerShell 5.1 reads a BOM-less
    `.ps1` as cp1252, so a UTF-8 emoji or box-drawing char decodes to bytes 0x93/0x94 — the curly
    quotes `“ ”`, which PowerShell honours as string delimiters. Nothing errors: the parser swallows
    everything to the next quote-class byte, PRINTS it instead of running it, and continues. One `📦`
    turned the whole `dotnet publish` block into a string literal (verified against the AST, lines
    48-54), and the first visible symptom was a `Get-ChildItem` "cannot find path …\publish\win-x64"
    from a *later* step. A BOM also fixes it and is the wrong fix — invisible, and it does not
    survive an editor round-trip anyone would notice.
  - **What ships is named after the APPLICATION, not the assembly**: `circuitRF(.exe)`,
    `harmonicaRF(.exe)`, `wBond(.exe)`. The assembly stays `CircuitRF.Ui` (RfCore's
    `InternalsVisibleTo` — WB40), and .NET names the published host after the assembly with no
    property to separate them, so `src/Ui/CircuitRF.Ui.csproj`'s `CrfRenameApphost` target renames it
    **after publish only** — a plain `dotnet build`/`dotnet run` is untouched. Five packaging files
    repeat that name as a literal and must be changed together (the `.wxs` + `build-windows.ps1`, the
    Debian `postinst` + `.desktop`, the three `bundleFor*MacOS.sh` + their `Info.plist`s).
- **The version number is written in exactly one place: the repo-root `VERSION` file** (one line,
  e.g. `0.9.0-beta.1`). `Directory.Build.props` reads it into every assembly's
  `Version`/`InformationalVersion` — which is what the About box renders via `src/Ui/AppVersion.cs`
  — and `packaging/version.{sh,ps1}` derive from it the installer file names, the MSI
  ProductVersion, the stamped `CFBundleShortVersionString`/`CFBundleVersion`, and dpkg's `~`
  spelling. Nothing is generated or rewritten; the version strings in `Assets/macOS/*.plist` are
  placeholders the bundle scripts overwrite. **Never hard-code a version anywhere else** — three
  copies had already drifted (About said 0.9.0, the plists 0.1.0, the assembly the 1.0.0 default),
  which is what `tests/Ui.Tests/VersionSingleSourceTests.cs` now holds shut.

### Run the test suite ONCE. Read the TRX for failures — never re-run to find out what broke

**Owner instruction, given three times (2026-08-25).** A full `dotnet test` is ~7 minutes of wall
clock. Running it a second time because the first run's console tail scrolled past the failure is
pure waste, and it is never necessary: **every project writes
`tests/<Project>.Tests/TestResults/last-run.trx` on every run**, and that file holds each test's
name, outcome, duration, failure message, stack trace and captured stdout.

- **`dotnet test`'s console tail only shows the LAST project to finish.** With seven test projects
  running concurrently, a failure in an early-finishing one (`Core.Tests` is 1 s; `Engine.Tests` is
  5+ min) is scrolled off. That is what makes the "just run it again" reflex so tempting — and the
  console is simply the wrong place to look.
- **Get the per-project verdicts in the same run** with `dotnet test 2>&1 | grep -E "^Passed!|^Failed!"`,
  which prints one line per project.
- **Get the failure detail from the TRX**, after the fact, for free:

  ```bash
  python3 - <<'EOF'
  import re, glob
  for f in glob.glob('tests/*/TestResults/last-run.trx'):
      x = open(f, encoding='utf-8', errors='replace').read()
      for m in re.finditer(r'testName="([^"]+)"[^>]*outcome="Failed"', x):
          print(f, m.group(1))
      for m in re.finditer(r'outcome="Failed".*?<Message>(.*?)</Message>', x, re.S):
          print(m.group(1)[:500])
  EOF
  ```

- The same file carries `<StdOut>`, so **a diagnostic `ITestOutputHelper` line is readable without
  re-running** — which is the cheap way to probe a failure, rather than adding a print and running
  the suite again.
- Corollary: **scope the run before starting it**, per the section below — `dotnet test tests/Ui.Tests`
  for layout/UI work is ~40 s against ~7 min for the full solution.

### `dotnet test` is fast by default (brief-test-default-fast.md, 2026-07-28)

**Plain `dotnet test`, with no flags, is the routine gate — it is fast by construction, not by
convention.** Repo-root `circuitrf.runsettings` (`TestCaseFilter: Category!=Benchmark`) is wired in via
`Directory.Build.props`'s `RunSettingsFilePath`, so every invocation — `dotnet test` at the root,
`dotnet test tests/Ui.Tests`, an IDE test run, CI — inherits the exclusion automatically. There is
nothing to type and nothing to forget. This supersedes the prior two-tag, filter-must-be-typed schemes
from brief-benchmark-gate-split.md and brief-test-suite-fast-loop.md: `Category=Nightly` is retired
and `Category=Slow` is gone as a category (its former members are either untagged, having been
measured under the threshold, or folded into `Benchmark`).

- **Repo root, no flags: 7,482 tests in ~4 min** (measured 2026-08-06). This is what "build+test
  green" means in every brief from here on, unless that brief's own text says otherwise.
  **`Engine.Tests` is ~3 min 24 s of that on its own** — measured alone, with `--no-build`, so it is
  the suite's own cost and not parallel contention. It grew there gradually across the L8/L9
  electromagnetic phases (65 s at L9b, ~2 min at the ground-via work) and no single test crosses the
  ~5 s `Category=Benchmark` threshold; ~1,000 tests averaging ~0.2 s each simply add up. **The
  earlier "5,169 tests in ~30 s" figure recorded here was stale by roughly an order of magnitude** —
  do not quote it. The other projects are still genuinely fast on their own: `Ui.Tests` ~27 s
  (5,075 tests), `Core.Tests` ~1 s, `RfCore.Tests` ~6 s, `Firewall.Tests` instant, so **scope the
  gate to the projects your change can reach** and keep the full-solution run for phase boundaries.
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
  Currently **122 test methods** repo-wide, counted rather than estimated (91 in `Engine.Tests`, 24 in
  `Ui.Tests`, 6 in `Harmonica.Tests`, 1 in `RfCore.Tests`); `brief-em-sweep-performance`'s own
  milestones account for much of the growth past the ~81 recorded below, and M5's accelerator adds the
  last 5 (`AimAccuracyTests`, 5.8 min) — the earlier count of 74 omitted `Harmonica.Tests`'
  own tier entirely; H6 added `InverseSolveCostTests` (3 methods, ~5 s) and
  `HarmonicaDragCostTests` (1 method, ~2 s), and H7 added `HarmonicaGridDragCostTests` (1 method,
  the 61-point grid measurement) and `HarmonicaTestbenchCliTests` (1 method, which launches the real
  `Cli hb` process) — **~5 s together**. The L8/L9 full-wave phases are where nearly all of them came from, because a
  single de-embedded full-wave point costs ~48 s one level and 71.9 s two (and **149.9 s** at the
  two-level-with-vias mesh L9's own phase gate runs on, N = 1,023), so none of those measurements can
  live in the routine tier. **L9's phase gate added 2** (`L9PhaseGateTests.Gate1` 5 m 28 s and
  `Gate2` 6 m 29 s, **11.97 min together, measured alone** — the via-carries-current comparison and the
  two-level degeneracy; their routine counterparts, the three `Gate3Wiring_…` tests, stay in the
  default gate at ~25 ms). **L9e added 7** (`ViaPhysicsTests.T3_1` 54 s — the ℓ/w
  error curve and its convergence sweeps; `AdaptiveSweepTests.T1_2` 16 s / `T4_1` and `T4_2` ~3-4 min
  each — the tolerance curve, the sweep-time measurement and D3's interpolant comparison;
  `PlanarBudgetTests.T4_3` 68 s / `T4_4` ~1 min / `T5_1` 6 s — the run-level memory arithmetic, its
  working-set cross-check and ACA's compression measurement), bringing the opt-in tier to roughly
  40 minutes in total. **The via z-integral follow-up added 3** (`ViaPhysicsTests.T3_1b` 24 s and
  `T3_1c` 1 m 37 s — the subdivision-invariance ladder and the n_z convergence table; `M1_1` 23 s —
  the cost measurement that decided the design), and **re-pointed `T3_1`** from measuring the midpoint
  rule's error to gating the fill's, at 16 s instead of 54 s. **Net ~+2 min.** The older, named ones are: the L8 phase gate
  (`L8PhaseGateTests.Gate1/Gate2/Gate3` × 2 starters, plus
  `EmAcceptanceBudgetTests.R18_WhatTheUserWaitsForAfterSimulate_AtTheShippingMesh`, ~8.5 min together
  — its routine counterpart `Gate3Wiring` stays in the default gate at 2.5 s); the
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
`src/RfCore` (Touchstone I/O, network params, `DataSet`/`DataCube`, `.npy` export), `src/Design`
(the design-layer DOCUMENT artifacts — the layout model and `.clay` reader, the technology model and
`.ctech` reader, the `.ccell` cell-folder format, the `.cem` EM setup and the extractors that turn
geometry + stackup into an `EmProblem`), `src/Ui` (Avalonia), `src/Cli` (headless driver + test
harness). `RfCore` is an ordinary first-party project alongside the rest — see §Stack for why it is
no longer at the repo root, and why that changed nothing architecturally.

**`src/Design` is not a second `src/Core`.** Core is the CIRCUIT design layer — cells as netlists,
parameters, expressions, elaboration; it knows nothing about DBU, stackups or drawing layers. Design
is the artwork side, carved out of `CircuitRF.Ui` in 2026-08 so `src/Cli` could run an EM setup
without pulling Avalonia across the firewall (`docs/sonnet-briefs/brief-cli-em-verb.md`). It draws
nothing, docks nothing and observes no canvas — the layout EDITOR, the DRC engine, the PCell
generators and the `.cem` editor all stayed in `src/Ui`. The namespaces that moved with it are
listed once in `src/Ui/GlobalUsings.cs` rather than in ~300 `using` lines.

`tools/` holds programs that are not part of the application. **A program in there that exists to be
tested against deliberately references no other project in this repo** — an independent
implementation of a contract, not a mirror of ours, since a second copy of our own code agreeing with
itself proves nothing:
- `tools/DeviceWorkerExample` — a reference **device worker**, the kind of separate process circuitRF
  runs to evaluate an externally-supplied device model. See its own `README.md` for the protocol and
  for how a kit declares its worker.
- `tools/senior-worker` — the worker circuitRF actually ships, for compiled vendor model libraries.
  One C source file, three products (a Linux executable; on Windows a DLL holding the callbacks plus
  a launcher stub, because a Windows model imports its host callbacks from a *named module*).
- `tools/fake-model-lib` — a test-only library mimicking that model ABI, so the worker can be driven
  end to end on a machine with no vendor kit on it. Not built by `dotnet build`.
- `tools/IconGen` — rasterises `src/Ui/Assets/artwork/*-app-icon.svg` into the `.icns`/`.ico`/`.png`
  containers the three operating systems read. Writes both containers itself (no `iconutil`, no
  ImageMagick), which is what makes packaging work identically on all three. Not in `circuitRF.slnx`,
  so a plain `dotnet build` neither builds it nor restores its `Svg.Skia` dependency.

**UI firewall:** `RfCore`, `src/Core`, `src/Engine`, `src/Design`, `src/Cli` must reference **no UI framework**
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
- The SDD's equations must stay expressible in an ordinary equation-defined-device form (hero references depend on it).

## How to add a component type
Derive from `ComponentModel` (the single base for passive **and** active parts — "Device" is
reserved for its RF meaning, an active part): declare ports + params, then `Stamp(...)` (linear
contribution — the model *contributes* stamps; the engine *owns* the matrix) and/or `Evaluate(...)`
(nonlinear: returns `i`, `q`, `dg`, `dc`). Register it in the component-model factory. Add a
golden-reference test. See `docs/design/data-model.md` §5.
**The base type must already accommodate the v2 ASM-HEMT/Verilog-A path:** a thermal/self-heating
node, collapsible internal nodes, terminal current, and charge-based capacitances (`q(v)` with
`dq/dv`). The external-device path exercises all four today (`ExternalDeviceModel`, `VerilogA`);
`FetModelBase` does not carry a thermal node of its own.


## Validation expectations
Numerical changes require a `testdata/` regression test within the tolerance in the PRD.
The five heroes are the acceptance anchors (S-params 1e-6; HB Pout/gain ±0.01 dB, eff/PAE ±0.1 pp;
loadpull contours; two-tone IM2–IM5). References are **externally generated** — produced
independently of circuitRF, then committed as fixed data — with the **identical SDD FET definition
on both sides**, so HB comparisons test our math, not a different transistor. CI runs the
suite on Windows, macOS, and Linux.

## Ask before
- Adding native (non-managed) dependencies (cross-platform risk).
- Anything marked out-of-scope for v1 in `docs/PRD.md` (transient, full Verilog-A/ASM-HEMT,
  a third-party cell database, layout view).

## Commit expectations
- Never commit unless given an explicit instruction by owner

## Commercial Vendor References
- Do not allow references to commercial vendors or their products to leak into the circuitRF repo - not even as a glossery of names to filter out.
- This includes the names of any specific PDK that does not come built into circuitRF
- The only exception to this rule is ".kicad_pcb" - that is a file name extension for a data format.
- Before any commit, always grep search for these names that could pollute the repo.  Remove them, and indicate what was removed via chat.

## Licensing
Core is **MIT**. Never ingest GPL code (some third-party simulators are GPL — learn from, never copy).
Keep a clean extension boundary so a future commercial **circuitRF+** can layer on without forking.

## Glossary
MNA, S-parameters, harmonic balance (HB), conversion matrix, loadpull/sourcepull, APFT, IMn,
DUT, Touchstone/SNP, SDD, OSDI/Verilog-A, `DataSet`/`DataCube`. Terms are defined where they
first appear in `docs/PRD.md` and the `docs/design/` notes.
