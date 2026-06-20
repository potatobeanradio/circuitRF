# Analysis Authoring — Step 6: wire authored analyses into extraction + run (the finale) (Claude Code / Sonnet)

The finale that closes the 6e gap: carry the schematic's **authored analyses + measurements** through
extraction into the netlist, so **Run executes them** and the "no analysis" message recedes. Builds on
steps 1–5 (model, persistence, list, form, reuse) + 6e steps 1–5 (extractor, `CnlWriter`, Run). **This brief
is step 6.** Loadpull/pursuit authoring stays deferred (note carried). Read `analysis-authoring.md` §6 first.
Sub-gated; **report and stop between every layer.** Firewall green.

> Read first: `docs/design/analysis-authoring.md` §6 (extraction/run wiring), §2.1 (SP multi-segment → flat
> freq array). Context code: `src/Ui/Schematic/NetExtractor.cs` (`Extract(model, name)` → `ExtractionResult`
> — currently builds `tb` with **only `Instances`**; the gap: it never copies `model.Analyses`/`model.
> Measurements` into `tb.Analyses`/`tb.Measurements`), `src/Ui/Schematic/EditableSchematic.cs`
> (`SchematicEditModel.Analyses`/`Measurements` — step 2), `src/Core/Design/TestBench.cs` (`Analyses`/
> `Measurements` lists), `src/Core/Netlist/CnlWriter.cs` (emits typed analyses + `measure` — confirm DC/SP/HB
> + SP multi-segment), `src/Core/Netlist/CnlReader.cs` (round-trip target), `src/Ui/Schematic/
> SchematicRunService.cs` (`RunNetlist(path)` → `RunResult`; dispatches `TestBench.Analyses`; already has
> `RunStatus.NoAnalysis`), `src/Ui/ViewModels/WorkspaceViewModel.cs` (`RunAnalysis` → `WriteNetlist` →
> `RunNetlist`; `AnalysisRowViewModel.Enabled`), `src/Core/Design/Analysis.cs` (`SParameterAnalysis.Sweeps`,
> the `Enabled` flag if on the model). Design docs win on any conflict.

## The spine (do not violate)
- **Extraction carries authored analyses + measurements.** `NetExtractor.Extract` must copy the schematic
  model's `Analyses` + `Measurements` into the emitted `TestBench` — the one missing wire. No new engine code.
- **Only ENABLED analyses run.** The list's per-analysis Enabled flag must gate what reaches the engine.
  Cleanest: **extraction emits only enabled analyses** (disabled ones simply aren't in the netlist), so the
  netlist *is* what runs. (Confirm where `Enabled` lives — model vs. row VM — and carry it to the model so
  extraction can read it; if it currently lives only on the row VM, add it to the `Analysis`/model so it
  persists and extracts.)
- **SP multi-segment → one flat freq array** (§2.1) — confirm `CnlWriter` emits the multi-segment sweep in a
  form `CnlReader` parses back to the same points, OR the analysis resolves its `Sweeps` to a flat sorted/
  deduped array at emit (the step-1 expand). The run must see the union of all segment points.
- **Round-trip stays green** — extract → `.cnl` → `CnlReader` must reproduce the analyses (DC/SP/HB);
  the 6e oracle + the existing tests stay green.
- **Reuse the existing run chain** — `RunNetlist` already dispatches + handles `NoAnalysis`/`EngineError`/
  `Success`; once analyses flow through, a drawn schematic with an analysis runs. No new engine code.
- **Scope fence (step 6):** extraction-carries-analyses + enabled-gating + CnlWriter/round-trip confirmation +
  measurements emit. NO loadpull authoring, NO new engine code, NO results visualization (Phase 7).

---

## LAYER 1 — extraction carries analyses + measurements (+ enabled gating)

1. In `NetExtractor.Extract`, after building `tb.Instances`, **copy the model's analyses + measurements**:
   `tb.Analyses` ← `model.Analyses` (the **enabled** ones — see #2), `tb.Measurements` ← `model.Measurements`.
   (These are the framework-free `Analysis`/`Measurement` records from step 1/2 — no transformation needed
   beyond the enabled filter + SP-segment resolution if done here.)
2. **Enabled gating:** ensure the **Enabled** flag is on the persisted model (the `Analysis` record or a
   parallel structure the `.csch` round-trips), not only on the row VM. Extraction includes **only enabled**
   analyses. (If `Enabled` currently lives only on `AnalysisRowViewModel`, promote it to the model so it
   persists in `.csch` and is visible to extraction — a small step-2-style addition; confirm + state.)
3. **SP multi-segment:** decide where the segment union happens — either `CnlWriter` emits multiple sweep
   directives `CnlReader` re-parses, or extraction resolves `Sweeps` to a flat sorted/deduped array (step-1
   expand) before emit. Pick the path that keeps the round-trip exact and the engine fed one freq array.

**Layer 1 gate:** headless test — a model with 2 analyses (1 enabled, 1 disabled) + measurements extracts to a
`TestBench` carrying **only the enabled** analysis + the measurements; an SP analysis with 2 segments yields
the expected unioned freq points. Report.

---

## LAYER 2 — CnlWriter round-trip (DC/SP/HB) + the Run executes them

1. **Confirm/extend `CnlWriter`** emits the v1 authored types — DC, S-parameter (multi-segment), HB — in the
   grammar `CnlReader` parses back. A round-trip test: `TestBench` (with each type) → `CnlWriter` → text →
   `CnlReader` → equivalent `TestBench` (analyses preserved, SP points preserved, measurements preserved).
2. **Run executes them:** with analyses now in the extracted netlist, `RunAnalysis` → `WriteNetlist` →
   `RunNetlist` runs the enabled analyses through the engine to `DataSet`(s). The **`NoAnalysis`** message now
   appears only when the schematic genuinely has no enabled analysis. Verify the active schematic's authored
   SP/DC/HB analysis actually runs (a DataSet comes back; reported via Messages; held for Phase 7).
3. **Measurements:** confirm `measure …` directives emit + round-trip (v1 minimal — name/expr/unit); they ride
   along into the `TestBench` (engine consumption of measurements may be Phase-7-facing — at minimum they
   round-trip and don't break the run).

**Layer 2 gate:** a drawn schematic with one authored enabled SP analysis → Run extracts + writes a netlist
containing that analysis → engine runs → DataSet returned + success Message (no "no analysis"); disabling the
analysis → Run reports "no analysis"; the CnlWriter↔CnlReader round-trip test (DC/SP/HB + measurements) passes;
the 6e oracle + existing tests stay green. Report.

## Acceptance (step 6)
1. `NetExtractor.Extract` carries the schematic's **enabled** authored analyses + measurements into the
   emitted `TestBench`; Enabled is persisted on the model (round-trips `.csch`) and gates extraction.
2. `CnlWriter` emits DC/SP(multi-segment)/HB + `measure` lines that `CnlReader` round-trips equivalently;
   SP segments resolve to the correct unioned freq array.
3. `RunAnalysis` runs the enabled authored analyses to DataSet(s) (reused engine chain, no new engine code);
   "no analysis" appears only when genuinely none enabled; DataSets held for Phase 7.
4. `dotnet build`/`dotnet test` green (incl. the 6e oracle + analysis round-trip); firewall green; **no
   loadpull authoring, no new engine code, no results visualization** (Phase 7 / deferred); nothing else
   regresses.

## Guardrails
- **Carry analyses in extraction** — the one missing wire; copy `model.Analyses`/`Measurements` → `tb`.
- **Only enabled analyses run** — Enabled on the persisted model, gating extraction.
- **SP multi-segment → one flat freq array**, round-trip-exact.
- **Reuse the run chain** — no new engine code; `RunNetlist` already dispatches + handles status.
- **Round-trip + oracle stay green** — extract → `.cnl` → `CnlReader` reproduces analyses.
- **Scope fence:** wiring + round-trip + measurements emit only — no loadpull authoring, no engine code, no
  Phase-7 visualization.
- Sub-gate the two layers; report and stop between each.
- Update `analysis-authoring.md` §7 status (step 6 done — the 6e "no analysis" gap closed) and `src/Ui/
  CLAUDE.md` (extraction carries enabled analyses; Run executes authored analyses).

*Exit: a drawn schematic with an authored, enabled analysis simulates end-to-end — extraction carries it,
the netlist contains it, the engine runs it, a DataSet comes back — closing the "no analysis" gap from 6e
step 5. Only measurements depth + loadpull authoring (deferred) and Phase 7 visualization remain.*
