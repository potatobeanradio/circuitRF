# Brief EMB-5 — The MCP server under long work: jobs, estimates first, and nothing blocks the rest

**Series:** EMB-0 … EMB-5 · **Tag:** `R-emb5-<n>`
**Area:** `src/Cli/Serve/McpServer.cs` (`Enqueue`, `WorkerLoop`, `Cancel`), `src/Cli/Serve/ToolCatalog.cs`,
`src/Cli/Serve/PathRoot.cs`, `src/Cli/CliDiagnostics.cs` (`serve.path.outside-root`), `src/Cli/RunHost.cs`,
`docs/design/cli.md`
**Depends on:** EMB-4 (the estimates R-emb5-2 surfaces).

---

## 0. The gap

An agent driving board EM through the MCP server today:

- **One worker thread serves every tool** (`McpServer` starts one `circuitrf-serve` thread running
  `WorkerLoop`). An EM `check` that took 16+ minutes blocked every later call, including a sub-second
  `check` of a `.cnl`. The agent fell back to the shell for the rest of the session.
- **Cancelling did not stop the work.** `notifications/cancelled` cancels the call's token, but `check`
  passed no `RunControl` to `EmRunService.Preflight`, so the worker kept meshing. (Fixed as F2 in `36ae6a845`;
  this brief makes cancellation a property every long verb is tested for.)
- **No estimate before committing.** `explain` already computes the planar factor time per point and
  Palace's element count and memory, but nothing puts them in front of the agent at the moment it decides
  to call `run`. Twice an agent committed to a run whose real cost was 4× or more the number it could have
  read.
- **The `create` tool documented three views; the CLI takes four** (`3d`). Fixed as F4 in `36ae6a845`.
- **Paths outside the server root are refused** (`serve.path.outside-root`) — correct — but the refusal does
  not say where the agent can write.

## 1. Requirements

**R-emb5-1 — Read-only verbs run concurrently; long verbs run as jobs.** Tools that write nothing
(`find`, `check`, `explain`, `read`, `reference`, `solver`, `render`, `plot`) run on a small pool, not behind
the single worker. `run` (and `check` of an EM setup) may be started as a **job**: `run … async=true` returns
at once with a job id; `job action=status|cancel|result id=…` reports progress (stage, percent, elapsed,
estimate remaining), cancels, and returns the same result document the synchronous call would. Without
`async`, behaviour is unchanged. **Why:** an agent can keep reading and checking while a 20-minute EM run
proceeds, and can stop it.

**R-emb5-2 — An estimate before the run, from the same functions `explain` uses.** `run analysis=em
dryRun=true` (and `circuitrf em --dry-run`) returns, without meshing: the kernel, unknowns, memory, time per
point and total (planar: `SurfaceMesher.EstimatedFactorTime`; Palace: `EstimatePalace` after EMB-4's
correction), the verdict against this machine's memory, and every refusal `check` would raise. A real run's
first progress notification repeats the estimate, so a client that did not ask still sees it. Each estimate
carries a confidence note when the geometry is imported (EMB-4 R-emb4-1).

**R-emb5-3 — Cancellation is tested per verb.** Every verb the server runs that can take more than a few
seconds (`check` of a `.cem`/`.c3d`, `explain` of a 3D setup, every `run` analysis) observes
`RunHost.Control`; a test cancels each mid-stage and asserts the worker is free within one stage boundary.

**R-emb5-4 — Stage-level progress names what is slow.** Progress notifications carry the stage name and its
own size (*"Gmsh: high-order optimisation, 330,000 nodes"*, *"near-field factor, 41 % of 23,734 columns"*),
from EMB-2 R-emb2-6 and EMB-4 R-emb4-5, so an agent can cancel and change approach rather than wait.

**R-emb5-5 — The root refusal says where to write.** `serve.path.outside-root` adds the server root and,
when one exists, a writable scratch folder under it the server will accept (e.g. `<root>/.circuitrf-scratch`
created on demand), so an agent with its own temp directory knows where to put a render or a result.

**R-emb5-6 — `create` and the CLI agree, held by a test.** A test compares each MCP tool option's documented
value list with the CLI parser's accepted values for the same flag (views, technologies, analysis kinds), so
F4's kind of drift fails a build.

**R-emb5-7 — A duration is read the way its own refusal describes it.** `run analysis=optimize time=10min`
is refused with "expects seconds, or a number and s, ms, min or h, got '10min'" — the spelling the sentence
offers. `src/Cli/Optimize.cs` (~line 125) turns a bare number into "N s" and passes anything else to
`TuningValidator.TimeLimitSeconds`, which evidently wants a space before the unit. Accept `10min`, `10 min`,
`90s`, `1.5h` alike everywhere a wall-clock limit is taken (the optimize line's `timelimit=`, the CLI's
`--time`, the MCP `time` argument), through one parser, and quote an accepted example in the refusal.

**R-emb5-8 — Every long result has a small form.** `result=summary` is refused for `optimize`
("'result' does not apply to optimize", `serve.args.not-for-mode`), so an agent asking a fit for its best
values receives every iteration's detail. Give `optimize`, `yield`, `montecarlo`, `corners`, `center` and `doe`
the same `summary` the S-parameter runs have: the outcome, the best values as text to write back, each goal's
margin and where it binds — and nothing per iteration unless `showIterations` asks. `import library` refuses `result=summary` too, and returns every indexed part (tens of thousands
for one vendor folder); its summary is the count per library and quantity, and the tolerance note.

**R-emb5-9 — A refusal never floods the caller.** `recognize` on a picture it could not calibrate (an
instrument screenshot whose grid carries no per-line labels, only start and stop beneath it, with a noisy
trace) refused correctly but returned ~150,000 characters: 206 trace fragments, each with its own record.
Collapse the trace list in any refused or unassigned reading to a count plus the few longest traces, with the
full list behind an explicit argument, and lead the result with the refusal's sentence and the flag that
answers it (`xAxis`/`yAxis`, or `manualBox` + `xRange` + `yRange`). Apply the same bound to every tool whose
result can grow with its input. `explain --tunables` is one: on a design holding a 15-port SnP it lists 240 offset rows
(`BRD.SdB_i_j`, `BRD.Sdeg_i_j`) before the four vendor parts asked about, though its own per-block summary
at the end already says the same; fold each SnP block's entries to that summary line unless the block is named.

**R-emb5-10 — A followed curve is a trace, not a gridline.** `recognize follow=<name>=<px>,<py>` on a click a
few pixels from a coloured trace locked onto the black 0 dB gridline beneath it and returned a flat line
(`src/Design/Imaging/Plots/ManualCurves.cs`, the follow path). Prefer a coloured, non-grid run near the click
over the plot's own grid and frame colours; report the colour followed and, when another candidate was
within reach, name it, so the caller can click again knowingly rather than discover it in the overlay.

**R-emb5-11 — A curve read by hand can be written as S-parameters.** In manual mode `trace` is refused
("--trace belongs to the automatic reading; a plot read by hand takes --curve …,is=S21:db") and `follow`
accepts no `is=`, so a followed S11 can only come out as CSV. Let `follow` take the same `,is=<quantity>` (and
`,axis=`) suffix `curve` does, so a hand-followed curve reaches a Touchstone like a hand-drawn one.

**R-emb5-12 — The schematic reference expands every type it names.** `reference schematic` lists
`CschTuning.Variables [TunableEntry]`, `Goals [OptimizationGoal]` and `Optimizer [OptimizerSettings]` but never
expands those three (nor `TuningPreset`, `StatisticsSettings`, `CornerDefinition`, `CenteringSettings`,
`DoeSettings`, `StatCorrelation`), while it expands every other type down to its enums. An agent writing a
tuning block into a `.csch` had to read `src/Core/Design/TuningSetup.cs` to learn that a goal's comparison is
`Type` (`Le`/`Ge`/`Eq`/`In`/`Out`) with `Limit`, and that a vendor-part search is a `TunableEntry` with `Parts`. Guessing cost two refusals more: `Distribution` is
`Gauss`/`Unif`/`LogNorm`/`Discrete` (not `Gaussian`), and `Spread.Sigmas` is a JSON string (`"3"`, not `3`).
Generate them from the same types the reader deserialises into, with one example of a part search and one goal,
and say that the `.cnl` `tune`/`goal`/`optimize` lines (`reference tuning`, `goals`, `optimizers`) are the same
model. The same result also carries the whole field list twice, as `text` and as `schema` (~20k characters
each); return one unless the other is asked for.

**R-emb5-13 — A vendor part can be placed from a `.cnl`.** `find parts` returns a part's instance parameters
(`File=vendorlib://<library>/…`, `Name=…`), but only a `.csch` component (`Symbol: "SpiceModel"`) accepts them:
the `.cnl` reader resolves no `vendorlib://` reference, so a netlist author must either hand-copy the
subcircuit the extractor inlines or switch to a schematic. Give `.cnl` the same placement (`SpiceModel:C1 p5 0
File="vendorlib://…" Name=…`, resolved through the workspace's `LibraryRefs` exactly as the schematic's is), so
`tune C1.Part parts="…"` works from a netlist, and say in `find`'s result which document kinds accept the
parameters it returns.

**R-emb5-14 — A `.cdd` written headlessly opens with its traces.** `plot --write-cdd` writes each trace's
`SourcePath` relative to the `.cdd`'s own folder, and `render` reads it back the same way; the application resolves
a relative source against the workspace's `results/` folder (`DataSourceLibraryViewModel.ResolveAbs`). A `.cdd`
written anywhere but `results/` itself (here `results/agent/`) therefore opens with every plot empty and no error,
and `tools/UiSnapshot` hides it by defaulting `--results` to the `.cdd`'s folder. Pick one rule for both readers.
Resolving against the document's own folder is the natural choice, with the results root as the fallback for a
`.cdd` saved by an older build. A `.sNp` source should also reach the source list without a hand edit of `.cws`
`KnownFiles`.

## 2. Gates

- `ServeProtocolAdapterTests`: a slow fake verb on the worker does not delay a concurrent `find`; a job's
  status/cancel/result round trip; `result` equals the synchronous call's document.
- R-emb5-3's per-verb cancellation test on small fixtures (a planar setup large enough to mesh for a second;
  a 3D setup cancelled during lowering).
- `dryRun` on a planar and a 3D fixture returns the same numbers `explain` prints.
- The root-refusal sentence names the root and the scratch folder.
- R-emb5-6's catalog-vs-parser test.
- R-emb5-7: `10min`, `10 min`, `600`, `600s` and `0.5h` all set the same limit on the CLI, the MCP and a
  `timelimit=` line; a refusal quotes an accepted spelling.
- R-emb5-8: `optimize result=summary` returns the best values and goal margins and no per-iteration record.
- R-emb5-9: a refused plot reading of a fixture with hundreds of fragments returns under a fixed size, with the
  refusal first.
- R-emb5-10: on a fixture with a coloured trace a few pixels from a black gridline, a click between them follows
  the trace, and the result names the gridline as the other candidate.
- R-emb5-11: `follow=S11=<px>,<py>,is=S11:db` with `output=x.s1p` writes a one-port Touchstone.
- R-emb5-12: every type named in `reference schematic` has its own field table; a test walks the named types.
- R-emb5-14: a `.cdd` written by `plot --write-cdd` into a subfolder of `results/` loads its traces in the
  Data Display view model resolved as the application resolves it.
- R-emb5-13: a `.cnl` placing a `vendorlib://` part elaborates to the same netlist the `.csch` placing it does;
  `tune C1.Part parts=…` on that `.cnl` passes `check`.

## 3. Not in scope

- Remote/multi-client servers, authentication, or persisting jobs across a server restart.
- Changing what any verb computes; this brief changes only how the server schedules, reports and stops it.

---

## EMB-5 part A — completion note (R-emb5-1 … R-emb5-6)

### R-emb5-1 — read-only verbs on a pool, long verbs as jobs — DONE
- `McpServer` now has two lanes: a pool of three threads (`McpServer.PoolSize`) for the tools that write nothing a design
  reads back — `find`, `check`, `explain`, `read`, `reference`, `solver`, `render`, `plot` (`ServeJobs.ReadOnlyTools`) — and
  for `resources/read`; every other tool keeps the one serial worker. Three, not one per core, because a board's `.cem`
  `check` holds a whole mesh.
- Jobs (`src/Cli/Serve/ServeJobs.cs`): `run … async=true` and `check … async=true` return a job document at once
  (`result.job.id`). New tool `job` with `action=status|cancel|result` (and `status` with no id lists all jobs). Status
  reports state (`queued/running/cancelling/cancelled/done`), the stage (the same message a progress notification
  carries), the run's own counters, `percent`, `elapsedSeconds` and `remainingSeconds` (elapsed × left/done, only where the
  run counts its work), the estimate (for an em run) and the exit code. `result` returns the stored tool result — built by
  the same function (`McpServer.BuildResult`) the synchronous call uses, so it is the same document. Jobs answer on the
  reader thread, so polling never waits behind a run. Jobs are not persisted (§3).
- `async` is an adapter argument (`ToolCatalog.AsyncArgument`, in `ToolSpec.Adapter` of `run` and `check`), exempted by
  name in `EveryAdvertisedArgument_IsAFlagTheVerbActuallyReads` like `attachImage`/`overlay`.
- **Concurrency — what was checked.** The verbs' process-wide state is now per async flow:
  - `JsonRun`: all ~50 static fields became properties over one `State`; `JsonRun.Isolate()` gives a call its own
    (`AsyncLocal`, so a verb's own `Parallel.For`/tasks see the call's state). CLI processes use the process-wide state
    exactly as before. Every server call (`RunVerb`, `RefusalDocument`) runs isolated.
  - `JsonRun.TakeFlags` no longer calls `Console.SetOut(TextWriter.Null)` when a sink is installed (serve already
    silences `Console.Out` once at startup) — the one global every call used to touch.
  - `RunHost.Control/Observer`, `CliEntry.KitFolders/HostWorkspace`, `GeneratedCells._trusted/Python` → `AsyncLocal`.
    `RunHost.Install` now restores the previous host (nests).
  - `CliEntry._kitsAdded` (HashSet) and the worker-log hook flag: taken under a lock.
  - A scan of every static collection mutated after init across `src/Cli`, `Design`, `Core`, `Render`, `Engine`,
    `RfCore`: all locked/concurrent except `BuiltInSymbols`' ten glyph caches (filled by `check` and `render`) → now
    `ConcurrentDictionary`. `TechnologyCache` is a new instance per call everywhere `src/Cli` makes one.
    `PlotDocumentScope`, `AdWarnings`, `EmStreamed` are thread-static already. Not changed because no pooled verb writes
    them: `ScratchFolder.Chosen` (em only, serial lane), `History._note`, `LayoutConvert._mintedTechPath`,
    `Rail._generated`, test-only hooks. Recorded in `src/Cli/RESOLVED.md`.

### R-emb5-2 — the estimate before the run — DONE
- `circuitrf em <file> --dry-run [--setup] [--device] [--memory-limit]` (`src/Cli/EmDryRun.cs`) runs `Check.Run` then
  `Explain.Run` on the same file in one invocation: `result.check` (every refusal check raises; planar mesh figures) and
  `result.explain` (3D size rows, Palace's estimate after EMB-4's correction with its imported-outline sentence, memory
  limits). Nothing is solved or written. One info diagnostic `em.dry-run.estimate` restates the numbers in a line.
  `--component`, `--bands`, `--solver` with `--dry-run` are refused (`cli.em.dry-run-needs-setup`).
- `check`'s planar `em` block gained the numbers its sentences already quoted: `memoryBytes`, `factorSecondsPerPoint`,
  `points`, `factorSecondsTotal`, `memory` (`fits`/`past-limit`). Sources: `PlanarMeshReport.RunResidentBytes` (or the
  near-field estimate's `ResidentBytes` when the accelerated solve runs); `SurfaceMesher.EstimatedFactorSeconds` (new
  numeric twin of `EstimatedFactorTime`, same formula) or `PlanarNearFieldEstimate.FactorSeconds`.
  `EmPreflightResult.NearField` (new) carries the estimate Preflight already made — no second computation.
- MCP: `run analysis=em dryRun=true`. And an em run's FIRST progress notification is `estimate: …` — the server runs the
  dry run on the same argv first, only when someone listens (progress token or job). Cost: what `check` of the setup
  costs (planar extract+mesh, or 3D lowering), which the run repeats.
- **Finding / not done:** Palace has no time estimate anywhere (`explain` prints none); none was invented, so a 3D
  estimate states elements, unknowns and memory but no time. The planar time is the factor alone (fill not included),
  and the line says so.

### R-emb5-3 — cancellation per verb — DONE
- Run verbs' catch-alls (`sparam`, `dc`, `hb`, `lp`/`lpp`, `fields`, em bands) mapped a cancellation to
  `cli.run.failed` (exit 1): now `when (ex is not OperationCanceledException)`, so the server answers 130.
- `explain` of a `.c3d` caught the cancellation as `explain.unreadable`: fixed.
- The 3D lowering takes no token, so `Em3dSetupSource` now has named stage boundaries (check token + `BeginStage`), as do
  `ExplainEm3d.Build` and `check` of a `.c3d` around its elaboration.
- Test cancels at the first progress observation (synchronously, from the observer — deterministic) and asserts 130,
  empty outputs, and that the same lane answers the next call: check `.cem` (planar), check `.c3d`, explain `.c3d`,
  run em, run sparam. Optimize/yield/env already mapped cancellation to 130 and were not re-tested here.

### R-emb5-4 — stage-level progress — DONE
`McpServer.ProgressMessage`: stage, then `— <done> of <total> <unit>` when the stage has its own size, then the live
`StageDetail` (Gmsh's node count from EMB-2, the near-field factor's columns from EMB-4). Invariant-culture digits. Used
by notifications and job status. No Gmsh/Palace code edited.

### R-emb5-5 — root refusal says where to write — DONE
`serve.path.outside-root` now names the root, says relative paths are taken from it, and names
`<root>/.circuitrf-scratch` (`PathRoot.Scratch()`, created the moment a refusal names it).

### R-emb5-6 — catalog vs parser — DONE
`ToolCatalogAgreesWithTheParserTests`: `create cell views` (documented words == `Authoring.ViewWords`, each accepted by the
real verb through the server, a misspelling refused; the description now also documents `none`, which the parser took
silently), `create workspace lengthUnit` (== `Authoring.LengthUnitWords`, each accepted), technologies
(`reference technologies` ids == the ids `new workspace --tech` accepts, read from its own refusal), `device` on
`run em` and `explain` (== `EmergeDevice` values, each parsed), and `run`'s analysis kinds (every mode a dispatched CLI
verb, every `yield` noun but `trial` a mode). `Authoring.ParseViews` and the length-unit parse now read those tables.

### Docs
`docs/design/cli.md` §8.10 (`--dry-run`), §11.5 (scratch folder), §11.6 rewritten (two lanes, jobs, progress text, the
estimate notification). The server `instructions` gained one paragraph (dryRun, async/job, reading tools answered during
a run). No `docs/user` page edited (the brief names none).

### Tests (Ui.Tests, targeted)
- New: `tests/Ui.Tests/Cli/ServeLongWorkTests.cs` (13 cases; in-process server over anonymous pipes with a `Runner` seam
  — a latch, not a stopwatch) and `tests/Ui.Tests/Cli/ToolCatalogAgreesWithTheParserTests.cs` (6 cases).
- Changed: `ServeProtocolAdapterTests` — `job` in the advertised list and skipped like `batch`; `async` exempted by name;
  and `run/sensitivity` added to the positional table (a pre-existing gap from SENS-1 that failed the gate before it
  reached any EMB-5 argument).
- Run: `ServeProtocolAdapterTests` + `EmRunSeverityCliTests` + both new classes: 62 tests; first run 2 failures (the two
  `ServeProtocolAdapterTests` expectations above), fixed and re-run: pass. Final: all pass.
- Not run: `Firewall.Tests` (no project reference added).

### Deviations from the brief's gate text
The scheduling/job gates live in a new class `ServeLongWorkTests` rather than inside `ServeProtocolAdapterTests` —
that file launches a server process per test and is shared with part B; the in-process harness is what makes the
latch-based ordering assertion possible.

### Expected conflicts with part B
`src/Cli/Serve/ToolCatalog.cs` (async adapter on run/check, `dryRun` on em, `views` description, the `AsyncArgument`
block before `AttachImageArgument`), `src/Cli/Serve/McpServer.cs` (substantial: Enqueue/Invoke/BuildResult/ToolResult/
RunVerb/Report/WorkerLoop; one paragraph in `instructions` after the 3D EM paragraph), `docs/design/cli.md` (§8.10 new,
§11.5, §11.6), `src/Cli/CliDiagnostics.cs` (new diagnostics next to `EmFreqWithoutComponent` and `ServeCancelled`;
`ServePathOutsideRoot` gained a parameter), `src/Cli/CliEntry.cs` (`RunEm` flags; catch filters), `src/Cli/Optimize.cs`
not touched. If part B adds a `.cnl`/`find` field or a `summary` result path, `JsonRun`'s new `State` class must get any
new static field as a property (the transform pattern is in the file).

---

# EMB-5 part B: completion note (R-emb5-7 … R-emb5-14)

## Per requirement

**R-emb5-7: durations.** `TuningValidator.TimeLimitSeconds` is now a regex reader. It takes a number and an optional `s`/`ms`/`min`/`h`, with or without a space. `600`, `600s`, `10min`, `10 min`, `0.5h` and `90 s` all parse. The optimize line's `timelimit=`, `opt --time`, `yield … --time` and the MCP `time` argument all use this one reader; the MCP argument maps to `--time`. Every refusal now quotes `TuningValidator.TimeLimitSpelling`: "seconds, or a number and s, ms, min or h, e.g. 600, 90s, 10min, 10 min or 1.5h". That covers `tuning.optimize.timelimit`, `OptFlagValue` and `YieldFlagValue`.

**R-emb5-8: small form of long results.**
- `--result summary` now works on a report that has no DataSet. The new `src/Cli/ReportSummaries.cs` is applied in `JsonRun.BuildPayload`:
  - optimize: drops each goal's per-corner table; the binding corner stays.
  - yield / montecarlo: drops statistics, the worst-trial lists, contribution fits and the per-reason trial numbers.
  - corners: drops the corner × goal rows; `worst` stays.
  - center: drops the iteration history.
  - doe: keeps only the active effects.
- `ResultPayload.Summarized`, a new nullable field in RfCore, names what was left out.
- The catalog's `Result` row is added to the optimize, montecarlo, yield, corners, center and doe modes, and to `import library`. Before this, `result=summary` was refused as `serve.args.not-for-mode`.
- `import library --result summary` collapses the per-file tolerance notes into one count note. The library JSON was already counts only.
- Decision: optimize's default document was already "final result only". In summary form, `perIteration` stays only when `showIterations` asked for it.

**R-emb5-9: bounded refusals.**
- A plot reading that made no Touchstone or named no trace (refused, or every trace unassigned) lists only the 5 longest traces. It adds `tracesRead`, the full count, plus one leading finding saying so. Findings about the omitted traces are dropped.
- `--all-traces` (MCP `allTraces`) lists them all.
- `RecognizePlotsJson.Refusal` is now the first property, so a refusal leads.
- An uncalibrated axis's refusal flag now reads `--y-axis (or read the plot by hand: --manual-box with --x-range and --y-range)`.
- `explain --tunables`:
  - Each SnP block's offset rows fold into its summary line: `ExplainSnpJson.Folded`, and the printed line shows `(folded: --snp-entries SNP1 lists them)`.
  - The new `--snp-entries SNP3,…|all` flag (MCP `snpEntries`) unfolds them.
  - Checked by hand on a 2-port SnP: folded by default, 8 rows when named. No test was added for this part; the brief's gate names only the plot reading.

**R-emb5-10: Find curve prefers a coloured trace.**
- `ManualCurves.InkAt` prefers coloured ink (chroma ≥ 40, `ColouredChroma`) over grey or black ink, whatever the contrast. A black curve is still followed when no coloured ink is in reach.
- `ManualFollowResult.Other` returns the grey or black ink that was passed over, with its distance.
- `recognize` reports each follow as one of two new diagnostics, both registered in `CliStructuredOutputTests.ExpectedIds`:
  - `recognize.plot.followed` (info): names the colour followed and any ink passed over.
  - `recognize.plot.follow-failed` (warning): replaces the old plain stderr line.
- The GUI's Find curve gets the colour preference automatically. It does not yet show `Other`.

**R-emb5-11: `follow` takes `is=` and `axis=`.**
- `--follow name=px,py[,axis=…][,is=…]` uses the same suffix parser as `--curve`; the parser was extracted into `PlotFlags.Suffixes`.
- When a `--curve` of the same name states the scale or quantity, it decides; otherwise the follow's suffix does.
- The manual-conflict refusal for `--trace` now names `--follow …,is=S21:db` as well. The usage text, the catalog row and the cli.md table are updated.

**R-emb5-12: the schematic reference.**
- `DocumentSchema.Format` gains `Descend`, a list of extra assemblies the walk expands. `schematic` adds `src/Core`'s assembly, so `TunableEntry`, `OptimizationGoal`, `OptimizerSettings`, `TuningPreset`, `StatisticsSettings`, `CornerDefinition`, `CenteringSettings`, `DoeSettings`, `StatCorrelation`, `StatSpread`, `GoalRange` and their enums each get a table.
- The preamble now has a ninth "not obvious" bullet. It says the tuning block is the same model as the `.cnl` tune/goal/optimize lines, gives the enum spellings and says Spread fields are strings. It includes a working `Tuning` example with a part search, a toleranced resistor and one goal; the test deserialises it with the reader's own options.
- `reference <format> --json` now returns the field tables once:
  - Default: the page text with its tables.
  - `--tables text|schema` (MCP `tables`): with `schema`, the tables come back as `reference.schema` and the text holds the preamble only.
  - A bad value is refused as the new `reference.args.tables`.
- `GeneratedReferenceTests.TheGeneratedFieldListIsTheReadersOwnType` now passes `--tables schema`. `ReferenceCliVerbTests`' anchor "Eight things" became "Nine things".

**R-emb5-13: vendor part from a `.cnl`.**
- `NetExtractor.EmitSpiceModelInstance` is split. The emission is now `EmitSpiceModel`, which takes the nets as a callback; the schematic wrapper computes nets from pins exactly as before.
- New `NetExtractor.ResolveNetlistSpiceModels(lib, tb, dir)` resolves each `SpiceModel:` line (token `NetlistSpiceModelToken`) through the same `EmitSpiceModel`. That means the same `VendorPartSource.Resolve` lookup through the `.cws`, the same block read and the same cells. It replaces the line with the emitted instance.
- Top-level parts are returned as tunables through the schematic's own `PartTunablesOf`. Parts placed inside `.cnl` cells are emitted but not offered.
- It is called from `CnlTechnologyBinding.Bind`, so every `.cnl` reader gets it. `Bind` now returns the parts, and `ReadFile` has an `out parts` overload. `PreparedCircuit.FromFile`/`FromText`, `check`, `explain --tunables` and `CircuitSource.Read` pass the parts into the catalog.
- A line that cannot be emitted throws, carrying the schematic's sentence.
- `find --parts` now returns `placedBy`: the document kinds that accept the parameters and the `.cnl` spelling.

**R-emb5-14: `.cdd` sources.**
- Rule chosen for both readers: resolve a relative source beside the `.cdd` first, then the workspace's `results/`.
- The CLI's `CddSources.LocateFile` already did this.
- GUI: `DataSourceLibraryViewModel` gains `DocumentDirectory`, set by `DisplayWindowViewModel.LoadAllAsync` and `SaveAllAsync`; `ResolveAbs` looks there first.
- `plot --write-cdd` now writes every source reference relative to the `.cdd`'s folder (`PlotVerb.Relocated`). It used to write the bare file name, which is only right beside the result. A `.cdd` written beside its result is unchanged, so the byte-identity gate still holds.
- A Touchstone the display holds now appears in the source list without a `.cws` KnownFiles entry.
- `tools/UiSnapshot`'s `--results` default is now the `.cdd`'s workspace `results/` (null outside a workspace) instead of the `.cdd`'s own folder.

## Tests
New file `tests/Ui.Tests/Cli/AgentExperienceTests.cs`:
- `DurationSpellingTests`: 7 theory cases, plus 1 fact covering the CLI, the line, the MCP `time` argument and MCP `result=summary`.
- `RefusedReadingSizeTests`: 300 generated dashes read as 48 traces. The bounded document is 8.3k chars; with `--all-traces` it is 39k. The test asserts a bound of 20k.
- `FollowedCurveTests` (2).
- `SchematicReferenceTests` (2).
- `NetlistVendorPartTests` (2): `.cnl` and `.csch` elaborate to the same part components; `check` passes with `tune C1.Part parts="C0G"`; import-library summary. Uses the synthetic `VendorLibraryFixture`.
- `HeadlessCddTests` (1).

Run: my classes plus affected existing ones (`GeneratedReferenceTests`, `ReferenceCliVerbTests`, `CliStructuredOutputTests`, `ManualDigitise*`, `TouchstoneAssemblyTests`, `OptCliVerbTests`, `OptMcpTests`, `VendorPartOptimizeTests`, `VendorPlacedPartTests`, `VendorLibraryCliVerbTests`, `SpiceModelComponentTests`, `SpiceModelVersusImportedCellTests`, `Z0ComplexPortMarkerTests`, `MissingVerbsCliTests`, `SnpEntryTunableTests`, and two catalog tests in `ServeProtocolAdapterTests`).
- Result: 261 tests, 260 passed, 1 failed.
- The failure was `ServeProtocolAdapterTests.EveryAdvertisedArgument_IsAFlagTheVerbActuallyReads`. It was already red at HEAD: SENS-1 added the `run/sensitivity` mode without adding it to the test's positionals table. I added that row, and the test now passes.

## UiSnapshot
`emb-5b-cdd-results-agent.png` (in `<SCRATCH>`): a `.cdd` written by `plot --write-cdd` into `<ws>/results/agent/`, rendered with the new default results root. Both traces draw.

## Notes
- No new mutable static field in `JsonRun`. I added `JsonRun.SummaryOnly`, a read-only property over the existing `_summaryOnly`, and the const `LibrarySummarized`. `BuildPayload`'s new block reads `_summaryOnly`, `Optimize`, `Yield`, `Corners`, `Center` and `Doe`. These need mapping onto part A's per-call `State`.
- No timing figure is recorded anywhere.
- DocGen was not run: no `docs/user/src` page was edited.
- Shared files: `ToolCatalog.cs` (the `Result` description text, `Result` added to six run modes and `import library`, the follow description, `allTraces`, `snpEntries`, reference `tables`), `docs/design/cli.md` (localized paragraphs), `CliDiagnostics.cs` (3 new factories), `ServeProtocolAdapterTests.cs` (one positionals row). `McpServer.cs` was not touched.
