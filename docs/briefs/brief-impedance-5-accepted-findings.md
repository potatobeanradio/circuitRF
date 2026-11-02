# Brief 5 — accepted findings: a known deviation, with its reason, on the record

**Series:** [Impedance review](brief-impedance-0-overview.md) · **Tag:** `R-imp5-n` · **Phase:** feature, format change
**Area:** new `src/Design/Layout/Em/TraceImpedanceAcceptance.cs`, `TraceImpedanceAnalysis.cs`
(`TraceRun`, `TraceIssue`, the verdict), `src/Design/Layout/LayoutModel.cs` (`LayoutView`),
`LayoutPersistence.cs`, the Impedance panel (brief 3), `src/Render/Renderers/TraceImpedanceReportDocument.cs`,
`src/Cli/Impedance.cs`, `src/Cli/Serve/ToolCatalog.cs`
**Depends on:** 1, 3 · **Blocks:** nothing
**Rule:** the boards, their vendors and the reporter must not be named anywhere in the repo.

---

## 0. Why

Scope (briefs 2, 4) removes traces that were never meant to be controlled. What is left can still hold
a finding the designer knows about and has decided is fine: board A's connector trace at 55.4 Ω
against a 55.0 Ω edge; a reference that steps to Inner 2 under a via fence on purpose; a ground
clearance under an RF pad that the launch was designed around. Today the only way to get such a board
to a clean report is to widen the tolerance for EVERY trace, which hides real faults.

DRC solved the same problem with waivers (`DrcWaiver`, `LayoutView.DrcWaivers`, layout-view.md
§9A.1: *waiving must be per-violation, persisted, and visible, or people stop running DRC*). Do the
same here, and call it **Accept** — the finding stays true; the designer has accepted it.

## 1. `R-imp5-1` — what an acceptance is, and what it matches

- **`R-imp5-1a`** `TraceImpedanceAcceptance` { `Key`, `Kind`, `Reason`, `AcceptedUtc`, `LayerName`,
  `Summary` (the finding's text at the time, so one that no longer matches can still be recognised and
  removed — `DrcWaiver.RuleName`'s role), and for `OutOfTolerance` the **worst Z0** accepted }.
  Stored in `LayoutView.ImpedanceAcceptances`, beside `DrcWaivers`, omitted when empty, not undoable
  (brief 2's R-imp2-3b, same reason).
- **`R-imp5-1b`** **The key names the TRACE and the KIND, not the stretch.** Trace ids (T1…) renumber
  with the scope and the stretch endpoints move whenever the tolerance does, so neither can key it.
  Key = layer name + the trace's two end points, order-normalised and quantised to 1 µm + the finding
  kind. It therefore stops matching when the trace is moved or re-routed — which is right: a moved
  trace has not been reviewed. Write the key function once, beside the type, and say this in its
  comment.
- **`R-imp5-1c`** **An acceptance does not cover a WORSE finding.** For `OutOfTolerance`, it holds
  only while the trace's worst excursion from target is no larger than the accepted worst Z0's; a
  later run that is worse shows the finding again, un-accepted, with *"accepted at 55.5 Ω, now
  58.1 Ω"*. Other kinds match by key alone.
- **`R-imp5-1d`** **A reason is required.** Unlike a DRC waiver's (which may be empty), this one is
  the sentence a reader of a passing report relies on. Refuse an empty reason in the panel and on
  the CLI.

## 2. `R-imp5-2` — what an accepted finding does to the verdict

- **`R-imp5-2a`** `TraceIssue.Accepted` (the acceptance, or null). A trace's verdict is computed from
  its UN-accepted findings only (brief 1's rule otherwise unchanged). `TraceRun.AcceptedCount`.
- **`R-imp5-2b`** Accepted findings are never removed from the report. Every reader shows them marked
  **ACCEPTED** with the reason.
- **`R-imp5-2c`** `TraceImpedanceReport`: `AcceptedCount`, and `StaleAcceptances` — acceptances that
  matched no finding in this run (listed, never deleted automatically). An acceptance whose trace is
  OUT OF SCOPE in this run is neither stale nor applied: it is not reported at all.

## 3. `R-imp5-3` — the panel

- **`R-imp5-3a`** On a finding row (brief 3's R-imp3-2c): **Accept…** asks for the reason (required)
  and applies at once — the counts and the row's verdict update without a re-run. **Un-accept** on an
  accepted row. Multi-select accepts several findings with one reason.
- **`R-imp5-3b`** The filter (R-imp3-2b) gains **Accepted**; the default "Warnings and failures" hides
  accepted findings, and the header shows the accepted count beside Pass.
- **`R-imp5-3c`** Stale acceptances are listed under their own heading with their saved `Summary` and a
  remove button.

## 4. `R-imp5-4` — the PDF and the CLI: a report that shows success honestly

- **`R-imp5-4a`** PDF summary: a passing review leads with the verdict — *"PASS — 3 traces reviewed,
  3 pass (1 with an accepted finding), 0 warnings, 0 failures"* — followed by the scope (brief 2). An
  **Accepted findings** section lists each one: trace, finding text, reason, date. On the map an
  accepted finding's marker is drawn grey with a check, not removed.
- **`R-imp5-4b`** CLI: acceptances apply by default (the `.clay` is the record); `--ignore-accepted`
  reports as if there were none. Exit code counts un-accepted findings only (brief 1's R-imp1-4d).
  `--accept <trace-id> --reason "<text>"` is NOT added: a CLI run's trace ids are this run's, and an
  acceptance is a decision made reading the finding. Headless callers edit the `.clay`, whose key is
  documented in the file-format reference. `--json`: each issue's `"accepted": { "reason", "date" }`
  and `staleAcceptances`.

## 5. Tests (minimal — one per claim)

1. Accepting board A's case (a trace at 55.4 Ω, warning) makes the trace PASS with `AcceptedCount = 1`;
   moving the trace by 10 µm makes the acceptance stale and the trace WARN again.
2. Changing the tolerance does not un-match the acceptance (the key is the trace, not the stretch).
3. The same trace made worse (narrower, higher Z0) shows the finding again with both numbers in its
   text.
4. An acceptance round-trips through the `.clay`; a `.clay` with none is byte-identical to today's.
5. The verb exits 0 on a layout whose only failure is accepted, and 1 with `--ignore-accepted`.

## 6. Scope

- No author field (DRC waivers have none; a name in a file that is committed is a privacy question
  this brief does not settle).
- No accept-all-of-a-kind or accept-by-width shortcut: each acceptance is a decision about one
  finding.

## 7. On completion

Findings in `src/Design/RESOLVED.md` and `src/Ui/RESOLVED.md`. Update the user reference (the panel's
Accept, the PDF's accepted section, `--ignore-accepted`) and the `.clay` format section with
`ImpedanceReview` and `ImpedanceAcceptances`.
