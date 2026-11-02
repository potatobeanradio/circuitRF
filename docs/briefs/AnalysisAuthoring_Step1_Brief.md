# Analysis Authoring — Step 1: model additions (headless) (Claude Code / Sonnet)

The first, self-contained slice of analysis authoring: small **framework-free model additions** to
`src/Core/Design/Analysis.cs` so an S-parameter analysis can carry **multiple frequency-sweep segments**, each
specified **Start/Stop/Step OR Start/Stop/N-points**, with **expression-string** fields (so `stop = 2*f0`
works). **This brief is ONLY step 1** — the model + unit tests. **No `.csch` persistence, no UI, no
clipboard/templates, no extraction/run wiring** — those are steps 2+. Read `analysis-authoring.md` §2 first.
Sub-gated; **report and stop between every layer.** Firewall green.

> Read first: `docs/design/analysis-authoring.md` §2 (the model additions — §2.1 sweep-segment list, §2.2
> points-mode, §2.3 expression fields). Context code: `src/Core/Design/Analysis.cs` (the **target** —
> `Analysis` base, `SParameterAnalysis(string, FrequencySpec)`, `FrequencySpec(start, stop, step, kind)`,
> `SweepKind{Linear,Log}`, `HarmonicBalanceAnalysis`/`DcAnalysis` (unchanged), `SweepSpec`), `src/Core/
> Netlist/CnlReader.cs` (how it currently constructs `SParameterAnalysis`/`FrequencySpec` — see what breaks
> when the shape changes; update the reader's construction to the new shape), `src/Engine/SParameterEngine.cs`
> + `src/Cli/Program.cs` `RunSparam`/`BuildFreqArray` (how a freq array is built today — the engine consumes a
> flat array; the multi-segment union must produce one). Design docs win on any conflict.

## The spine (do not violate)
- **Framework-free, headless.** All changes are in `src/Core/Design` (+ wherever the reader/engine construct
  these) — no Avalonia/Skia. Unit-tested with no GUI.
- **Additive + minimal.** Extend `SParameterAnalysis`/`FrequencySpec`; do NOT redesign the `Analysis`
  hierarchy. DC and HB are unchanged.
- **Store what the user typed** (`parameter-editor.md` principle): persist the *intent* (step-mode vs.
  points-mode; the expression strings), not just a derived number — the round-trip (step 2) must reproduce
  what the user entered.
- **Don't break existing callers.** `CnlReader` constructs `SParameterAnalysis`/`FrequencySpec` today, and
  the engine/CLI build a freq array; update them to the new shape so the build + existing tests stay green.
- **Scope fence (step 1):** model + reader/engine construction + unit tests. NO `.csch` persistence (step 2),
  NO serialization-for-clipboard/template (step 2), NO UI (steps 3–4), NO reuse (step 5).

---

## LAYER 1 — `FrequencySpec`: points-mode + expression strings (§2.2/§2.3)

Extend `FrequencySpec` so a segment is specifiable two ways and carries expressions:
1. **Expression strings:** the start/stop/step values become **expression strings** (e.g. `"1e9"`, `"2*f0"`)
   resolved against globals at engine time — OR keep the resolved doubles and add parallel `…Expr` strings;
   **pick the cleaner and state it.** The design (§2.3) wants `stop = 2*f0` to work, so the authored value
   must survive as an expression. (Simplest: `StartExpr`/`StopExpr`/`StepExpr` strings + a resolver step;
   the existing expression `Evaluator` resolves them.)
2. **Mode (§2.2):** add a `FreqSpecMode { StepSize, PointCount }` discriminator (or `int? NumPoints`):
   - `StepSize` → start/stop/step (current behavior).
   - `PointCount` → start/stop + **N points**; the step is **derived** (`(stop-start)/(N-1)` linear;
     log-spaced when `Kind == Log`). N ≥ 2 (N=1 = the start point only — define the edge).
3. **`SweepKind` (Linear/Log)** already exists — keep it per-segment.
4. A **resolve+expand** helper: given resolved globals, a `FrequencySpec` → its concrete `double[]` frequency
   points (honoring mode + kind). This is what the engine consumes.

**Layer 1 gate:** unit tests — a `StepSize` spec expands to the expected points; a `PointCount` spec expands to
N points with the right (derived) spacing (linear and log); expression-string start/stop/step resolve against a
supplied global (`f0`); N=2 and N edge cases behave. Report.

---

## LAYER 2 — `SParameterAnalysis`: a list of sweep segments (§2.1)

Change `SParameterAnalysis` from a single `FrequencySpec` to **a list of segments**:
1. Replace the single `Freq` with **`IReadOnlyList<FrequencySpec> Sweeps`** (≥1). Keep a convenience
   constructor/factory for the common single-segment case so call sites stay simple.
2. A **resolve+expand** for the whole analysis: union each segment's expanded points (§Layer 1 helper) into
   **one sorted, de-duplicated `double[]`** — the flat frequency array the engine/CLI already expect
   (`SParameterEngine`/`BuildFreqArray`). (Confirm the engine wants a flat sorted array; produce exactly
   that.)
3. **Update `CnlReader`** to construct the new shape (a single authored `.cnl` sweep → one segment), and the
   **engine/CLI** call sites to consume the unioned array. Keep existing S-param tests green (a single sweep
   behaves exactly as before).

**Layer 2 gate:** unit tests — a single-segment S-param analysis expands to the same points as today (no
regression); a two-segment analysis (e.g. 1–2 GHz step 100 MHz + 5–6 GHz step 50 MHz) unions to the sorted
deduped union; `CnlReader` builds the new shape and existing `.cnl` S-param fixtures still parse + run. Report.

## Acceptance (step 1)
1. `FrequencySpec` supports **Start/Stop/Step** and **Start/Stop/N-points** (derived step, linear + log) and
   carries **expression-string** start/stop/step resolved against globals; a resolve+expand helper yields the
   concrete points.
2. `SParameterAnalysis` carries **`IReadOnlyList<FrequencySpec> Sweeps` (≥1)**; a whole-analysis expand unions
   segments into one sorted/deduped flat freq array the engine consumes.
3. `CnlReader` + engine/CLI updated to the new shape; **existing S-param parse/run tests stay green** (single
   sweep unchanged in behavior).
4. `dotnet build`/`dotnet test` green; firewall green (all in `src/Core`, framework-free); **no `.csch`
   persistence, no serialization-for-clipboard/template, no UI, no reuse** (steps 2+); nothing else regresses.

## Guardrails
- **Additive + minimal** — extend `SParameterAnalysis`/`FrequencySpec`; don't redesign the hierarchy; DC/HB
  untouched.
- **Store what the user typed** — mode (step vs. points) + expression strings persist the intent; the derived
  step is computed, not stored as the source of truth.
- **Don't break callers** — update `CnlReader`/engine/CLI to the new shape; single-segment behavior identical
  to today (regression-free).
- **Engine consumes a flat sorted/deduped array** — the multi-segment union produces exactly that.
- **Scope fence:** model + construction + tests only.
- Sub-gate the two layers; report and stop between each; don't run the full suite into the output limit.
- Update `analysis-authoring.md` §7 status (step 1 done) and `src/Core/*/CLAUDE.md` if the `FrequencySpec`
  shape change is worth recording.

*Exit: the analysis model supports multi-segment S-parameter sweeps specified by step or point-count, with
expression-valued fields — the framework-free foundation the `.csch` persistence + shared serialization
(step 2) and the authoring UI (steps 3–4) build on.*
