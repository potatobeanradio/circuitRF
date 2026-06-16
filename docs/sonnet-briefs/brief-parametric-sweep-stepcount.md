# Sonnet Brief — Parametric sweep: Start/Stop/Step and Start/Stop/NumPoints (not just a value list)

**Goal.** A parametric sweep must accept **Start/Stop/Step** and **Start/Stop/NumPoints** (lin or log), not only
an explicit comma list. Users sweep many points (e.g. Pin −30→0 in 0.25 dB steps = 121 pts); typing a list is
untenable.

**Most of the machinery already exists.** `SweepExpander.ExpandSweep(start, stop, stepOrCount, SweepAxisMode,
SweepKind)` already supports `StepSize`, `PointCount`, and `List` with Linear/Log. The S-param directive parser
(`TryParseSParamDirective`) already parses `start`/`stop`/`step`/`npts`/`log` into a `FrequencySpec`. The gaps:
1. The **`parametric_sweep` CNL directive** only parses `Values=v1,v2,…` (a discrete list).
2. **`ParametricSweepAnalysis`** stores a final `double[] SweepValues` — no spec form.
3. The **Sweep-4 UI** (analysis editor) — confirm it emits Start/Stop/Step/Count, not a list.

This brief threads a sweep **spec** through the directive, the model, and the engine so expansion happens at run
time (consistent with how `FrequencySpec.Expand` works for S-param).

## 1. CNL directive — accept start/stop/step | npts (+ log), keep Values= for back-compat
In `CnlReader.TryParseParametricSweepDirective` (`src/Core/Netlist/CnlReader.cs`), after reading `Var=` and
`Inner=`, support both forms:
- **Existing:** `Values=v1,v2,…` → explicit list (keep working).
- **New:** `Start=` `Stop=` (`Step=` | `Npts=`) [`log` | `log=true`]. Parse numbers with invariant culture.
  Mirror the S-param parser's mode/kind detection (`npts` → PointCount; else `step` → StepSize; bare `log` or
  `log=true` → `SweepKind.Log`). No frequency-unit handling needed (sweep vars are unitless doubles — Pin in dBm,
  Vbias in V, etc. — the value is taken as-is).
- Build the value array via `SweepExpander.ExpandSweep(start, stop, stepOrCount, mode, kind)` for the new form,
  or `SweepExpander.ExpandList(valuesStr)` for the list form. (SweepExpander is in `CircuitRF.Ui.Schematic` — see
  "Where ExpandSweep lives" below.)
- Validation: missing both `Values` and (`Start`+`Stop`) → throw a clear `Parametric sweep '<name>': needs
  Values= or Start=/Stop=`. Step ≤ 0 with StepSize → error; Npts < 1 → error.

**Where ExpandSweep lives:** `SweepExpander` currently sits in `src/Ui/Schematic` (UI assembly). The CNL reader is
in `Core`, which must not depend on UI. **Move `SweepExpander` (and `SweepAxisMode`) to `Core`** —
`src/Core/Design/SweepExpander.cs`, namespace `CircuitRF.Core.Design` — it's pure math (no Avalonia) and belongs
beside `FrequencySpec`. Update the few existing references (the Sweep-4 UI). This keeps the architectural firewall
intact (Core has no UI dependency) and lets both the CNL reader and the UI share one expander. Confirm
`SweepKind` already lives in Core (it's used by `FrequencySpec`) — reuse it.

## 2. ParametricSweepAnalysis — carry the spec, expand lazily (or eager-store, but record the form)
Today `ParametricSweepAnalysis(name, sweepVarName, double[] sweepValues, inner)` stores the final array. Two
options — **pick (A) unless the .cnl round-trip needs the spec preserved verbatim:**
- **(A) Eager-expand, keep array (smallest):** the directive parser expands to `double[]` at read time (as the
  list form already does) and constructs the existing `ParametricSweepAnalysis`. The engine is unchanged
  (`ParametricSweepEngine.Run` already iterates `SweepValues`). **Downside:** the `.cnl` writer can only re-emit a
  `Values=` list (loses the compact Start/Stop/Step form on round-trip).
- **(B) Store the spec (nicer round-trip):** add an optional spec to `ParametricSweepAnalysis` — e.g.
  `SweepSpec? Spec` with `{ double Start, Stop, StepOrCount; SweepAxisMode Mode; SweepKind Kind }` plus the
  expanded `SweepValues` (computed once). The writer re-emits Start/Stop/Step when `Spec` is present, else
  `Values=`. The engine still reads `SweepValues` (unchanged).

**Recommend (B)** — it preserves the user's compact intent across save/reload and keeps the editor's fields
faithful. Keep `SweepValues` populated (engine + StackSweepAxis already depend on it), and make `Spec` additive
(nullable; null ⇒ came from an explicit list). Constructors: keep the existing `(name, var, double[], inner)` and
add `(name, var, SweepSpec, inner)` that expands into `SweepValues`.

## 3. CNL writer — emit the compact form when available
`src/Core/Netlist/CnlWriter.cs`: when writing a `ParametricSweepAnalysis`, if `Spec` is present emit
`Start=… Stop=… (Step=… | Npts=…) [log]`; else emit `Values=v1,v2,…`. Round-trip test below.

## 4. Sweep-4 analysis editor UI — ensure it offers Step/Count, writes the spec
Find the analysis editor (`src/Ui/ViewModels/AnalysisEditorViewModel.cs`,
`src/Ui/Views/Dialogs/AnalysisEditorDialog.axaml`). The Sweep-4 brief specified Start/Stop/Step | Count | List +
Lin/Log rows; **verify that exists**. If the editor currently only produces a `Values=` list (which would explain
why the netlist shows a discrete list), wire it to:
- a **Mode** segmented control (`Step` / `Points` / `List`) + **Lin/Log** toggle (reuse the `FreqSpecMode` idiom
  from the S-param editor),
- the relevant fields, a live count/preview ("121 points: −30, −29.75, … 0"), and inline `CrfWarningBrush` on
  invalid ranges,
- on commit, build a `SweepSpec` (option B) — not a pre-expanded list — so the model and `.cnl` keep the compact
  form. (If the editor already does Step/Count but expanded to a list, switch it to store the spec.)

If the editor already supports this fully and only the **CNL path** was list-only, then this step is just
confirming the editor stores/round-trips the spec; note that and move on.

## Tests
- **CnlReader_StartStopStep:** `analysis SW type=parametric_sweep Var=Pin Start=-30 Stop=0 Step=0.25 Inner=HB1`
  → `SweepValues.Length == 121`, first −30, last 0.
- **CnlReader_StartStopNpts:** `… Start=-20 Stop=10 Npts=7 …` → 7 points, linspace.
- **CnlReader_Log:** `… Start=1 Stop=1000 Npts=4 log …` → {1,10,100,1000}.
- **CnlReader_ValuesStillWorks:** `… Values=-3.0,-3.2 …` → 2 points (regression).
- **Roundtrip_Spec (option B):** read a Start/Stop/Step directive → write → read → identical `SweepValues` and
  (if Spec stored) identical compact form.
- **ExpandSweep_MoveToCore:** the moved `SweepExpander` unit tests still pass from the Core namespace.

## Gate
Build 0W/0E; tests green. Manual: in the analysis editor, define a Pin sweep as Start −30 / Stop 0 / Step 0.25 →
run → `HB1.npy` has a 121-point Pin axis; save/reload the schematic keeps the compact Start/Stop/Step (not a
121-number list). A Points-count and a Log sweep also work.

## On completion
Note in `src/Core/.../CLAUDE.md` + `src/Ui/CLAUDE.md`: parametric sweeps accept Start/Stop/Step and
Start/Stop/Npts (lin/log) in addition to an explicit Values= list; `SweepExpander` moved to Core (shared by the
CNL reader and the analysis editor); `ParametricSweepAnalysis` carries an optional `SweepSpec` so the compact form
round-trips through `.cnl`.
