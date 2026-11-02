# Impedance review — series overview: a report that can show success

**Series:** Impedance review · **Tag:** `R-imp{n}-m` · **Briefs:** 1–5
**Area:** `src/Design/Layout/Em/TraceImpedanceAnalysis.cs`, `src/Render/Renderers/TraceImpedanceReportDocument.cs`,
`src/Cli/Impedance.cs`, `src/Ui/Layout/LayoutEditorViewModel.TraceImpedance.cs`,
`src/Ui/Views/Dialogs/TraceImpedanceAnalysisDialog.axaml(.cs)`, `src/Design/Layout/LayoutModel.cs`,
`LayoutPersistence.cs`
**Found by:** outside field report, 2026-09-26 — an experienced RF board designer ran Impedance Analysis
on two vendor reference boards (Gerber imports).
**Rule:** the boards, their vendors, the connector maker and the reporter must not be named anywhere in
the repo. The boards are "board A" (4 copper layers) and "board B" (8 copper layers) below.

---

## 0. What the field report showed

The analysis is accurate, and it is unusable as a review, for one reason: **it has no idea which
traces are meant to be controlled**, so it holds every trace on the board to 50 Ω.

| | Traces | Pass | Fail | Time |
|---|---|---|---|---|
| Board A, Top + Inner 1 (cancelled before Inner 2 / Bottom) | 41 | 0 | 41 | 1,078 s (1,098 solves) |
| Board B, Top + Inner 1 | 73 | 2 | 71 | 7.4 s |

- On board A, **36 of the 41 are 100 µm routing** — 76–122 Ω as GCPW on Top, ~61 Ω as stripline on
  Inner 1. They were never meant to be 50 Ω. The one RF trace, **457 µm to the coaxial connector,
  reads 55.4–55.5 Ω against a 55.0 Ω band edge** and is reported as FAIL alongside a 122 Ω signal
  trace, with nothing to tell them apart.
- On board B the RF trace (381 µm, 54.9–57.0 Ω) passes; the other 71 rows are 99–221 µm fan-out.
- On board A the run spent 18 minutes solving Inner 1's routing, which the designer then stopped.
- The PDF's map carries a numbered marker on every failing trace, so the RF path's label is lost in
  the clutter (the designer's screenshot of board B).

The designer's own words, paraphrased: without being told which traces are the RF path, the report
analyses many traces that do not need it; being able to mark traces as RF, from the schematic or in the
layout, would cut the analysis down; failing that, a lasso around the RF areas of the board.

The owner's reading: no engineer will circulate a report that is mostly FAIL, however accurate. The
review needs (a) a way to say what is under review, (b) a tier between pass and fail, and (c) a way
to accept a known finding with a reason — so that a correct board produces a report that shows it.

## 1. The briefs

| # | Brief | What it adds | Depends on |
|---|---|---|---|
| 1 | [Warning tier](brief-impedance-1-warning-tier.md) | Pass / Warning / Fail; a warning band; severities per finding kind | — |
| 2 | [Scope model, by width](brief-impedance-2-scope-by-width.md) | `TraceImpedanceScope` in the `.clay`; layer + width classes; CLI flags; scope before solving | 1 |
| 3 | [The Impedance panel](brief-impedance-3-impedance-panel.md) | the modal dialog becomes a dockable panel: settings, Run, results, cross-probe, Export PDF | 1, 2 |
| 4 | [Scope on the canvas](brief-impedance-4-scope-on-the-canvas.md) | regions (rectangle / lasso), picked copper, nets | 2, 3 |
| 5 | [Accepted findings](brief-impedance-5-accepted-findings.md) | accept a finding with a reason, persisted on the layout like a DRC waiver | 1, 3 |
| 6 | [Regions per layer](brief-impedance-6-regions-per-layer.md) | a lasso/rectangle region belongs to the layer it was drawn on; the "no capacitance" neck probe | 4 |
| 7 | [Pads and winding](brief-impedance-7-pads-and-winding.md) | a trace into a pad is found as a trace; polygon winding is normalised once, in `LayoutClipper` | 6 |
| 8 | [Large-board speed](brief-impedance-8-large-board-speed.md) | a whole 10-layer board in minutes, not 29 min; bounded memory; progress with an estimate | 7 |
| 9 | [Summary report](brief-impedance-9-summary-report.md) | a Summary report style beside Full: findings grouped, dense markers clustered, worst 20 in full | 8 |

Briefs 1 and 2 alone turn both boards' reports into "the RF trace passes (board B) / warns (board A)".
Brief 3 is enabling: the dialog is modal and ends in a PDF, so there is nowhere to draw a region or
accept a finding. Build in order.

## 2. Decisions already made (owner, 2026-09-26)

- **The warning band is a second percentage**, entered beside the tolerance (not a multiple of it).
- **Scope and accepted findings are saved in the `.clay`**, on the `LayoutView`, beside `DrcWaivers`
  and `LvsWaivers` and on their terms: a statement about this artwork, not about the technology.

## 3. Rules that hold across the series

- **Scope selects TRACES, never COPPER.** A cross-section needs the ground plane, the side grounds and
  every neighbour, which run far outside any region the user draws. Every trace still sees the whole
  board's copper; scope decides only which traces are cut, solved and reported.
- **Scope is applied before cutting and solving**, so it saves the solve time as well as the rows. The
  gate for that is a COUNTER (solves performed), never a wall-clock measurement.
- **What was not reviewed is said, not hidden.** The report states the scope in words and counts the
  traces outside it; it does not list them.
- **One analysis.** The panel, the PDF and `circuitrf impedance` read the same `TraceImpedanceReport`
  from the same `TraceImpedanceAnalysis.Analyze`; no brief adds a second classifier in `src/Ui`.
- **Every existing call with no scope and no warning band behaves as today** except for the severity
  split of brief 1, which is the point of the series.

## 4. Not in this series

- **RF marking from the schematic** (back-annotation). It needs nets on the layout that a Gerber import
  does not carry; brief 4's net selector is the landing point for it when it exists.
- **Frequency-dependent or lossy Z0.** The solve stays quasi-static and lossless.
- Any change to trace finding (`FindLayer`) or to the cross-section kernel.

## 5. On completion (every brief)

Findings in `src/Design/RESOLVED.md` (the UI half in `src/Ui/RESOLVED.md`), never in any `CLAUDE.md`.
Update `docs/user/src/reference/layout-editor.md` §Impedance Analysis and the `impedance` row and
section of `docs/user/src/reference/cli.md`. Do not run DocGen; the owner regenerates at the end of the
series.
