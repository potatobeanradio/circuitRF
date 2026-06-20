# Phase 6e — Step 5: the in-app Run (extract → engine → DataSet) (Claude Code / Sonnet)

The payoff: the **Run command** takes the active TestBench schematic, extracts + writes `netlist.cnl`
(steps 1–4), feeds it through the **existing engine chain** (`CnlReader → Elaborator → engine`) to a
**`DataSet`**, and surfaces the result. A drawn schematic finally simulates end-to-end. **This brief is
step 5.** Results **visualization is Phase 7** — step 5's job is "the run happens and produces a DataSet,"
reported via Messages, not plotted. Read `net-extraction-and-run.md` §5 first. Sub-gated; report between
layers. Firewall green.

> Read first: `docs/design/net-extraction-and-run.md` §5 (Run wiring, engine reuse, results-are-Phase-7,
> error surfacing). Context: `src/Cli/Program.cs` (`RunSparam` — the **exact** `CnlReader.ReadFile →
> new Elaborator(lib).Elaborate(tb) → SParameterEngine.Run(nl, freqs) → DataSet` pattern to mirror), `src/Ui/
> ViewModels/WorkspaceViewModel.cs` (`RunAnalysis`/`StopAnalysis` stubs; step-4 `WriteNetlist`; the active
> document; `Messages`), `src/Core/Netlist/CnlReader.cs`, `src/Core/Elaboration/Elaborator.cs`, `src/Engine/`
> (`SParameterEngine`, `NonlinearDcEngine`, `HbEngine`, `LoadpullEngine`, `AnalysisSettings`), `src/Core/
> Design/TestBench.cs` (`Analyses`/`RawDirectives` — what analysis to run), `src/Core/Design/Analysis.cs`.
> Design docs win on any conflict.

## The spine
- **Reuse the engine chain — no new engine code.** Mirror the CLI: `CnlReader` the `netlist.cnl` (or use the
  step-1 `TestBench` directly) → `Elaborator.Elaborate` → the engine the testbench's analysis declares →
  `DataSet`. Route by analysis type (S-param/DC/HB/loadpull) exactly as the CLI/engines expect.
- **Run from `netlist.cnl`** (steps 1–4 already write it) so the run matches the inspectable artifact; a small
  internal-consistency win, and it's what the CLI does. (Re-reading the just-written file is fine.)
- **Results → Phase 7.** v1 surfaces success/convergence/warnings via Messages and **holds** the `DataSet`
  (so Phase 7 can bind it); it does NOT plot. The `netlist.cnl` path is a clickable Message link.
- **Errors are clear, never silent** — no analysis, unconnected required pins, singular matrix,
  non-convergence → a Message pointing at the cause.
- **Analysis source (the deferred A-vs-B seam):** analyses live on the `TestBench` (`Analyses`/
  `RawDirectives`). The schematic has **no analysis-authoring UI yet** (deferred). So step 5 runs **whatever
  analyses the extracted testbench carries** — which for a hand-authored hero `.cnl` opened/run is real, but
  for a freshly-drawn schematic is likely **none**. Handle "no analysis" gracefully (a clear Message: "No
  analysis defined — add one to run", per the deferred authoring). Do NOT build analysis authoring here.
- **Scope fence (step 5):** Run → engine → DataSet + reporting. NO results visualization (Phase 7), NO
  analysis-authoring UI, NO new engine code.

---

## LAYER 1 — the run service (headless-ish: netlist → DataSet)

A run helper (`src/Ui` is fine — it orchestrates; the engine is Core/Engine):
1. `RunTestBench(SchematicEditModel model | netlist path, …)`: extract+write `netlist.cnl` (step-4 helper) →
   `CnlReader.ReadFile` → `new Elaborator(lib).Elaborate(tb)` → dispatch to the engine for each declared
   analysis → collect `DataSet`(s).
2. **Analysis dispatch:** read the testbench's `Analyses` (typed: HB/loadpull/parametric) and `RawDirectives`;
   route each to its engine with its `AnalysisSettings` (mirror how the CLI builds settings — e.g. the freq
   sweep for S-param). If no analysis is present → return a clear "no analysis" outcome (not an exception).
3. Return a small result (DataSet(s) + status + messages); catch engine exceptions
   (`SingularMatrixException`, `NonlinearDcNotConvergedException`, etc.) into status, not crashes.

**Layer 1 gate:** headless test — a hero `.cnl` (or a model that extracts to one) with an S-param analysis runs
to a `DataSet` via the service; a netlist with no analysis returns the "no analysis" status; an engine error is
captured as status, not an unhandled throw. Report.

---

## LAYER 2 — wire the Run command + Stop + reporting

1. **`RunAnalysis`** (the stub) → call the run service on the **active TestBench schematic**; post Messages:
   the `netlist.cnl` path (clickable), per-analysis success/convergence info, any extraction conflicts
   (step 4), and engine warnings/errors. Hold the resulting `DataSet`(s) on the VM for Phase 7.
2. **No-analysis / not-a-testbench:** a clear Message ("No analysis defined…") rather than a failure; don't
   block — the schematic still extracted and wrote `netlist.cnl`.
3. **`StopAnalysis`:** wire if the engines support cancellation; else keep the stub honest ("nothing running"
   / best-effort). State which.
4. **Scratch run:** works with no workspace (netlist in the scratch dir, step 4); same reporting.

**Layer 2 gate:** Run on a drawn schematic that carries an analysis (or a hero opened into one) extracts →
runs → reports success + the `netlist.cnl` link + a DataSet held; Run on an analysis-less schematic reports
"no analysis" cleanly; an engine error surfaces as a clear Message. Report.

## Acceptance (step 5)
1. The Run command extracts the active schematic, writes `netlist.cnl`, runs it through the existing engine
   chain (no new engine code) to a `DataSet`, routing by the testbench's declared analysis.
2. Results are surfaced via Messages (status, convergence, warnings, clickable `netlist.cnl`) and the DataSet
   is held for Phase 7; **no plotting**.
3. Errors (no analysis, singular matrix, non-convergence, unconnected pins) surface as clear Messages, never
   silent crashes; scratch runs work with no workspace.
4. `dotnet build`/`dotnet test` green; firewall green; **no results visualization, no analysis-authoring UI,
   no new engine code** (Phase 7 / deferred); nothing else regresses.

## Guardrails
- **Reuse the CLI's engine chain** — `CnlReader → Elaborator → engine → DataSet`; no new engine code.
- **Results are Phase 7** — surface via Messages + hold the DataSet; don't plot.
- **Graceful "no analysis"** — the deferred authoring means most fresh schematics have none; report, don't
  fail.
- **Capture engine exceptions** into status/Messages, not crashes.
- **Scope fence:** Run → DataSet + reporting only.
- Sub-gate the two layers; report between each.
- Update `net-extraction-and-run.md` §6 status (step 5 done) and `src/Ui/CLAUDE.md` (Run reuses the engine
  chain; results held for Phase 7; analysis-authoring still deferred).

*Exit: a drawn schematic simulates end-to-end — Run extracts it, runs the engine, and reports a DataSet —
closing the core 6e loop; results visualization and analysis authoring are the remaining frontiers (Phase 7 /
the deferred A-vs-B decision).*
