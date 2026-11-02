# Sonnet Brief — Sweep Fix 4/5: Edit Analysis UI — nested parametric sweeps

**Goal.** Give the analysis editor a way to **add/order nested parametric sweep axes** around an inner analysis,
replacing the deprecated HB-internal "Sweep" fields (Briefs 1/3). A user builds e.g. "HB1, swept over `Pavl`
(−20→10 dBm), nested inside a sweep over `Vbias`" → produces a `ParametricSweepAnalysis` (possibly nested)
wrapping the inner analysis. Variables come from VARs / globals (the VAR work makes any of them sweepable).

## Required reads
- `src/Ui/ViewModels/AnalysisEditorViewModel.cs`, `src/Ui/Views/Dialogs/AnalysisEditorDialog.axaml(.cs)` — the
  existing analysis editor (how analyses are listed/edited, how HB fields render, how it builds `Analysis`
  objects). Follow the §2.8 idiom already used here (opacity-tiered labels, rounded rows, segmented `.active`
  buttons, IBM Plex, `CrfWarningBrush` for invalid input).
- `src/Core/Design/Analysis.cs` — `ParametricSweepAnalysis(name, sweepVarName, double[] sweepValues,
  innerAnalysisName)`; `HarmonicBalanceAnalysis` with the now-deprecated `SweepVar*`.
- How available variable names are discoverable for the picker (VAR/global names): the schematic's
  variables/globals — reuse whatever the expression editors already use to list known names; if none, accept a
  free-text variable name (validated against known names with a soft warning).

## Model mapping (what the UI builds)
`ParametricSweepAnalysis` takes an **explicit `double[]` of values** + an `innerAnalysisName`. The UI captures
intent (start/stop/step or point-count, or an explicit list) and expands to `double[]` at build time (reuse the
`FrequencySpec`/sweep expansion helpers' linspace/logspace logic — extract a shared `ExpandSweep(start, stop,
step|count, lin|log)` if not already reusable; keep it headless/testable).

Nesting = chaining `ParametricSweepAnalysis` by name: outer.Inner = middle.Name; middle.Inner = HB.Name. The
editor must let the user add **0..N** sweep axes around one inner analysis and order them outer→inner.

## UI
On an analysis's editor, add a **"Parametric Sweeps"** section (collapsible), below the inner-analysis fields:
- A list of **sweep axis rows**, ordered outer→inner (drag-reorder or up/down buttons). Each row:
  - **Variable** — combo of known VAR/global names (+ free-text fallback) with a soft "unknown variable" warning.
  - **Mode** — segmented `Start/Stop/Step` vs `Start/Stop/Points` vs `List` (explicit comma values), matching the
    `FreqSpecMode` idiom.
  - the relevant expression fields (start/stop/step or count, or a values textbox), and **Lin/Log** toggle.
  - **unit** (optional, free text or a small unit combo) — carried so the produced sweep axis can be labeled with
    a unit (Brief 1 left HB's axis unit empty; the sweep axis the UI builds can supply it). If the
    `ParametricSweepAnalysis`/`Axis` can't carry a unit today, **flag it** — adding a unit to the sweep axis is a
    small, worthwhile follow-up (note it; don't block this brief).
  - a live **count/preview** ("21 points: −20, −19, … 10") and an inline `CrfWarningBrush` error for invalid
    ranges (step ≤ 0, stop < start, empty list).
  - add/remove row buttons.
- **Replace** the HB editor's old Sweep var/start/stop/step fields with this section (those directive fields are
  deprecated per Brief 3). If an existing analysis still has HB `SweepVar*` set (loaded from old `.cnl`),
  **migrate it into a sweep row** on open (pre-populate the section), so the user sees their old sweep as a
  parametric axis.

## Build/commit
When the editor commits, build the `Analysis` graph:
- the inner analysis (HB/DC/S-param) **without** any internal sweep, plus
- one `ParametricSweepAnalysis` per sweep row, chained by `InnerAnalysisName` outer→inner, with the **outermost**
  being the runnable/enabled analysis. Name them deterministically (e.g. `<inner>_sweep_<var>`).
- Persist through the existing analysis-save path (`.csch` analyses round-trip). Ensure the run dispatch
  (Brief 3) runs the outermost `ParametricSweepAnalysis` via `ParametricSweepEngine`.

## Tests
- **Headless (`ExpandSweep`):** start/stop/step, start/stop/count, list, lin/log → correct `double[]` (unit
  tests on the extracted helper).
- **Build_NestedChain:** two sweep rows (Vbias outer, Pavl inner) around an HB → produces two
  `ParametricSweepAnalysis` chained correctly (outer.Inner = inner-sweep.Name; inner-sweep.Inner = HB.Name);
  values match the expansions.
- **Migrate_OldHbSweep:** an HB analysis with legacy `SweepVar*` set opens with one pre-populated sweep row.
- Manual covers the editor UX.

## Gate
Build 0W/0E; tests green. Manual: open the analysis editor, add a sweep over a VAR variable (e.g. drive power),
nest a second sweep (bias), run → the data display shows cubes with both axes named after the variables
(self-describing); removing a sweep row reverts to single-point. The HB editor no longer shows the old Pin/Sweep
fields.

## On completion
Note in `src/Ui/CLAUDE.md`: the analysis editor builds nested `ParametricSweepAnalysis` axes (variable +
start/stop/step|count|list + lin/log + optional unit) around an inner analysis; legacy HB `SweepVar*` migrates
into a sweep row on open; run dispatch executes the outermost sweep via `ParametricSweepEngine`. Completes the
sweep-model consolidation (P1Tone is a separate brief).
