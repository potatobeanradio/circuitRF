# Brief 5 — impedance in the review: the saved Impedance review's verdicts as findings

**Series:** [Layout Review](brief-review-0-overview.md) · **Tag:** `R-rev5-n` · **Phase:** checks
**Area:** `src/Design/Layout/Review/Checks/ImpedanceCheck.cs` (new); reads `src/Design/Layout/Em/TraceImpedanceAnalysis.cs`,
`TraceImpedanceAcceptance.cs`, `TraceImpedanceScope.cs` without changing them
**Depends on:** 2 · **Blocks:** 6

---

## 0. Why this is a reading, not a check

"RF tracks routed as 50 Ω" is the Impedance review's question, and the Impedance series already answers it: trace
finding, cross-sections, return-path findings, a scope saying which traces are RF, accepted findings, a report. The hard
part of the question, *which traces are RF*, is the Impedance review's saved scope (brief-impedance-2/4), and the
designer has already answered it there. So the review does not grow an impedance rule. It runs the Impedance analysis
the layout already describes and lists its verdicts beside the other checks, so one review covers the whole list.

## 1. Requirements

**R-rev5-1 — Run the saved review, scoped.** The check builds `TraceImpedanceOptions` from the layout's saved
`ImpedanceReview` (target, tolerance, warning band, layers, frequency, scope), exactly as `circuitrf impedance` does by
default, and calls `TraceImpedanceAnalysis.Analyze` on the run's shared `LayoutSource`. Factor the options-from-saved-
review step out of `src/Cli/Impedance.cs` into `src/Design` if it lives only in the CLI, so the review and the verb build
the same options from one function.

**R-rev5-2 — No saved scope means not run.** A layout with no `ImpedanceReview`, or one whose scope is empty, makes the
check *not run* with the sentence "No impedance scope is saved on this layout. Choose the RF traces in the Impedance
panel." **The review never runs the whole board**: on board C a whole-board impedance run is 5 min 22 s (brief-impedance-8),
and a reviewer who wants that runs `circuitrf impedance --no-scope` or the Impedance panel, where the cost is stated.

**R-rev5-3 — One finding per trace that does not pass.** A trace whose verdict is Fail, Warning or Unsolved is one
finding (Warning severity in the review, whatever its impedance verdict: the review never errors), marker = that
trace's issue stretches, key = `TraceImpedanceAcceptance.TraceKeyOf`. Sentence: the trace's verdict, its worst Z0
against target ± tolerance, and its issue kinds by name, in the Impedance report's own words (call its text helpers,
`OhmsText`, `VerdictText`, `ShortType`; do not re-word them). A passing trace is not a finding. The check's row also
states the scope in words (`DescribeScope`) and how many traces passed, so a clean result reads as "12 RF traces, all
within 50 Ω ± 10 %" rather than as nothing.

**R-rev5-4 — Impedance acceptances are the Impedance review's.** `TraceImpedanceAcceptance.Apply` is applied first: a
finding the designer accepted in the Impedance panel is ACCEPTED here with its reason. Accepting an impedance finding
from the Review panel (brief 6) writes a `TraceImpedanceAcceptance`, not a `ReviewAcceptance`, so the two panels can
never disagree about it. A trace is accepted when every one of its issues is.

**R-rev5-5 — Settings belong to the Impedance review.** This check has no thresholds of its own in `ReviewSettings`; its
target, bands and scope are the Impedance review's. `--list-checks` and the panel show them read-only, with where to
change them. Only the enable flag is the review's.

**R-rev5-6 — Progress.** The analysis's own layer-by-layer progress is forwarded as this check's progress; cancelling
the review cancels it and keeps the layers that finished, as the Impedance panel does.

## 2. Tests (`tests/Ui.Tests/Review/ImpedanceCheckTests.cs`)

1. A small layout with a saved scope covering one 50 Ω microstrip and one 70 Ω trace → one finding, on the 70 Ω trace,
   whose sentence carries the Impedance report's worst-Z0 text verbatim.
2. The same layout with no saved review → *not run*, the sentence above, and no solve performed (a COUNTER of
   cross-sections solved is zero; no timing).
3. An impedance acceptance on the 70 Ω trace's only issue → the review reports it ACCEPTED with the reason.
4. The review's impedance finding and `circuitrf impedance`'s failing trace for the same layout have the same key.

Run only that class.

## 3. By hand on board C (completion note)

With board C's saved Impedance review (Top Copper, a lasso region; see its `.clay`): the check's time, its findings, and
that they match the Impedance panel's FAIL/WARN traces for the same scope one for one.
