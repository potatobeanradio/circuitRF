# Brief 1 — the warning tier: Pass / Warning / Fail

**Series:** [Impedance review](brief-impedance-0-overview.md) · **Tag:** `R-imp1-n` · **Phase:** model change
**Area:** `src/Design/Layout/Em/TraceImpedanceAnalysis.cs` (`TraceImpedanceOptions`, `TraceIssue`,
`TraceVerdict`, `TraceImpedanceReport`, `Assemble`), `src/Render/Renderers/TraceImpedanceReportDocument.cs`,
`src/Cli/Impedance.cs`, `src/Cli/Serve/ToolCatalog.cs` (the `impedance` tool),
`src/Ui/Views/Dialogs/TraceImpedanceAnalysisDialog.axaml(.cs)`,
`src/Ui/Layout/LayoutEditorViewModel.TraceImpedance.cs`
**Depends on:** nothing · **Blocks:** 2, 3, 5
**Rule:** the boards, their vendors and the reporter must not be named anywhere in the repo.

---

## 0. What is wrong today

`TraceIssue.Fails` is `=> true`, and `Assemble` sets `Verdict = issues.Count > 0 ? Fail : Pass`. Every
finding of every kind fails its trace, by the same amount. Board A's RF trace at 55.4 Ω against a
55.0 Ω edge and a 122 Ω signal trace are both FAIL; so is a trace whose reference legitimately steps
to another layer, and so is a trace one of whose cuts the kernel could not solve.

A reviewer needs to see the difference between "look at this" and "this is wrong".

## 1. `R-imp1-1` — a warning band, entered as a second percentage

- **`R-imp1-1a`** `TraceImpedanceOptions.WarningPercent` (double, default **20**). The warning band is
  target ± `WarningPercent`. Refused unless `TolerancePercent < WarningPercent < 100`, with a sentence
  naming both numbers. `TraceImpedanceReport` carries it, with `WarnLowOhms`/`WarnHighOhms` beside
  `LowOhms`/`HighOhms`.
- **`R-imp1-1b`** An out-of-tolerance stretch (today's one `OutOfTolerance` issue per run of flagged
  stations — keep that grouping) is a **Warning** when every station in it lies inside the warning band,
  and a **Fail** when any station lies outside it. Its text names the band it broke: *"… outside
  50 Ω ± 10 %, inside ± 20 %"* or *"… outside 50 Ω ± 20 %"*.
- **`R-imp1-1c`** Board A's case, synthesised: a trace at 55.4 Ω against 50 ± 10 % / ± 20 % is a
  Warning, and the trace's verdict is Warning.

## 2. `R-imp1-2` — an electrically short excursion is a warning (optional frequency)

A stretch outside the warning band over a small fraction of a wavelength (a neck-down into a pad, a
width step at a launch) is not what fails a line at the frequency it carries. That judgement needs a
frequency, so it is opt-in.

- **`R-imp1-2a`** `TraceImpedanceOptions.MaxFrequencyHz` (double?, default null). When set, an
  out-of-tolerance stretch that would be a Fail under R-imp1-1b is a **Warning** when its electrical
  length is below **λ/20** at `MaxFrequencyHz`, λ from each station's own ε_eff
  (Σ Length·√ε_eff over the stretch, against c/(20·f)). The finding's text says so, with the numbers:
  *"… 0.31 mm, 0.018 λ at 6 GHz — electrically short"*.
- **`R-imp1-2b`** When null the rule is off and the report says nothing about it. When set, the
  summary page's "How it was measured" paragraph states the λ/20 rule and the frequency once.
- **`R-imp1-2c`** It applies to `OutOfTolerance` only. A broken return is not made acceptable by being
  short.

## 3. `R-imp1-3` — a severity for every finding kind

- **`R-imp1-3a`** `TraceIssue` gains `Severity` (`IssueSeverity { Warning, Fail }`); `Fails` becomes
  `Severity == IssueSeverity.Fail`. Each kind:

  | Kind | Severity |
  |---|---|
  | `OutOfTolerance` | R-imp1-1b / R-imp1-2a |
  | `ReturnBroken` | **Fail** |
  | `NoReference` | **Fail** |
  | `PartialReference` | Warning |
  | `ReferenceStep` | Warning — often a designed transition; the reviewer decides |
  | `Unsolved` | Warning — part of the trace was not checked, which is not the same as wrong |

  Put this table in a comment at `Assemble`, once, with the reason per row.
- **`R-imp1-3b`** `TraceVerdict` gains `Warning`. A trace is **Fail** if any finding fails, else
  **Warning** if any finding warns, else **Pass**; **Unsolved** stays what it is today (no station
  solved). If `TraceVerdict` is serialized by NAME anywhere, adding a member is safe; if by NUMBER,
  append it rather than insert it.
- **`R-imp1-3c`** `TraceImpedanceReport.WarningCount` beside `PassCount`/`FailCount`.

## 4. `R-imp1-4` — every reader shows three tiers

- **`R-imp1-4a`** PDF (`TraceImpedanceReportDocument`): the summary tiles become Traces / Pass /
  Warning / Fail; the by-layer table gains a Warning column; the per-trace table's verdict column reads
  PASS / WARN / FAIL with an amber ink for WARN. On the map, a warning marker is drawn **hollow**
  (outlined, number inside) and a failing one filled, so the kind colours (orange = Z0, purple = return
  path) keep their meaning. The colour bar marks the warning band edges as a second, lighter pair of
  ticks. Findings list each finding with its severity.
- **`R-imp1-4b`** CLI text: `WARN` in the verdict column, `!` for a failing finding and `?` for a
  warning one, and the closing line *"N pass, W warning, F fail"*. `--json`: `warningCount`, each
  issue's `"severity": "warning" | "fail"`, the verdict string `"warning"`.
- **`R-imp1-4c`** CLI flags `--warn <percent>` and `--max-freq <frequency>` (a unit is required on the
  frequency — `6GHz` — a bare number is refused, the rule every CLI frequency follows). Add both to
  the `impedance` tool in `ToolCatalog`.
- **`R-imp1-4d`** **Exit codes follow `check`'s convention**: warnings are always reported and exit 0;
  any Fail, or any trace Unsolved, exits 1 (as today). `--severity warning` makes a warning exit 1 too.
  Update the tool description ("Exit 0 when every trace passes…").
- **`R-imp1-4e`** The dialog gains a **Warning ±** field beside Tolerance and an optional **Highest
  frequency** field (blank = off), and its band line reads *"Pass 45.0–55.0 Ω · Warning 40.0–60.0 Ω"*.
  The Messages line says pass / warning / fail, and is a Warning message only when something FAILED or
  the run was cancelled.

## 5. Tests (minimal — one per claim; extend `tests/Ui.Tests/Em/TraceImpedanceAnalysisTests.cs`)

1. A microstrip whose Z0 is ~1 % above the pass band is Warning, and its finding's severity is Warning;
   the same trace with the warning band set below its Z0 is Fail. (One `[Theory]`, two rows.)
2. A short narrow neck in an otherwise in-band trace: Fail with no frequency, Warning with a frequency
   at which it is below λ/20, Fail again at a frequency where it is not. (Three rows.)
3. The existing slot-in-the-plane fixture still FAILS (a broken return is never softened), and a
   reference step alone makes the trace Warning.
4. The verb: a layout whose only finding is a warning exits 0 and prints WARN; with `--severity
   warning` it exits 1. Fold into `TheVerb_WritesTheReport_AndExitsByTheVerdicts` if that is cheaper.
5. `WarningPercent <= TolerancePercent` is refused.

Run only `TraceImpedanceAnalysisTests` (`--filter FullyQualifiedName~TraceImpedanceAnalysis`).

## 6. Scope

- No change to trace finding, cutting or the kernel: every Z0 number is the same before and after.
- No per-kind user configuration of severity. The table in R-imp1-3a is the product's position.

## 7. On completion

Findings in `src/Design/RESOLVED.md`. Update `docs/user/src/reference/layout-editor.md`
§Impedance Analysis (the three tiers, the two new fields, the λ/20 rule) and `docs/user/src/reference/cli.md`
(the flags and the exit codes).
