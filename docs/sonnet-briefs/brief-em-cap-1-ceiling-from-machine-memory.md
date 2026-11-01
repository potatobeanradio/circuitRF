# Brief CAP-1 — the dense planar ceiling comes from the machine's memory

Series: `brief-em-cap-0-overview.md`. **Changes no answer.** It changes which runs are allowed to
start, and what the user is told before one starts.

## The gap

`SurfaceMesher.UnknownCeiling = 10_000` is a constant. A 64 GB workstation and an 8 GB laptop are
refused at the same N, and a user who wants more has to know to type a memory limit into the setup
(`PlanarMeshSettings.MemoryLimitBytes`). The mechanism to do better already exists and is only wired
to the opt-in path:

- `SurfaceMesher.CeilingFor(accelerated, memoryLimitBytes, cellCount)` — with a limit, binary-searches
  the largest N whose `PlanarSystem.ResidentBytes` fits it.
- `PlanarMemoryCheck` (`src/Design/Layout/Em/PlanarMemoryCheck.cs`) — warns past 75 % of physical
  memory, asks past 100 %, and honours the machine-wide limit in Settings ▸ Solvers.
- `EmRunService` already knows the physical memory and passes it to the check.

## What to do

1. **When a setup states no memory limit, the dense ceiling is derived from the machine:** the
   machine-wide limit (Settings ▸ Solvers) if one is set, otherwise physical memory, through the
   existing `CeilingFor`. A setup's own `MemoryLimitBytes` still wins over both.
2. **The engine stays a pure function of its settings.** `src/Engine` must not read the machine's
   memory. The run service (`src/Design`) resolves the limit and hands it down in
   `PlanarMeshSettings`/`PlanarFillSettings` exactly as an explicit limit is handed down today. A test
   or a direct `PlanarKernel.Solve` call with no limit keeps `UnknownCeiling` as the fallback, so
   engine tests stay machine-independent.
3. **Every reader of the ceiling asks the same function.** Today's call sites: the mesher's verdict,
   `PlanarSystem.GuardCeiling`, `PlanarFill.BuildCores*`, `PlanarDeembed`'s two thresholds (lines
   ~912–963), and the panel (`EmSetupEditorViewModel` ~921 and ~1916). The pre-solve report and the
   run must not disagree — P12 recorded that they once did.
4. **The CLI `em` verb and the MCP `run analysis=em` get the same derivation** — they go through
   `EmRunService`, so this should be free; prove it with one test.
5. **Say the per-point time before a large run starts.** Add an estimated time per frequency point to
   the existing budget sentence (`PlanarMeshReport.Budget`) when N is past the old 10,000:
   `factor ≈ c·N³` with `c` taken from the recorded measurement (37 s at N = 11,210, Release,
   10 cores) and labelled **estimated** — one figure, no explanatory paragraph. Scale by
   `Environment.ProcessorCount` only if a measurement on this machine supports it; otherwise do not.
6. **De-embedding standards.** Their matrices are smaller than the DUT's, but confirm the peak of a
   de-embedded point stays within `ResidentBytes` of the DUT plus the standards' own, and that the
   check's "before de-embedding adds its calibration standards" wording is still true. Measure on the
   §10.7 hero; record the number.

## Gates

- A run at N between 10,000 and the machine-derived ceiling **starts** (one frequency point, scratch
  harness, N ≈ 15,000 is enough), and its measured peak working set is within 10 % of
  `ResidentBytes` — record both.
- A run above the machine-derived ceiling is refused with the existing typed refusal
  (`PlanarMeshRefusedException`), quoting the machine's memory rather than a constant.
- Explicit `MemoryLimitBytes` still overrides; Settings ▸ Solvers limit still overrides physical.
- Existing tests that assert the 10,000 refusal keep passing because they run with no resolved
  limit; any that went through `EmRunService` and assumed 10,000 get an explicit limit, not a new
  expected number derived from the test machine.

Targeted tests: `tests/Engine.Tests/Mom/PlanarMemoryLimitTests.cs`, `PlanarBudgetTests.cs`,
`EmDeembedCeilingTests.cs`; `tests/Ui.Tests/Em/PlanarMemoryLimitUiTests.cs`,
`MeshBudgetAndCeilingsTests.cs`, `EmCeilingRefusalTests.cs`, `EmRunServiceTests.cs`. Add one test
per claim above; run only these classes.

UI: if the panel's ceiling wording changes, render it with `tools/UiSnapshot` and look at it.
