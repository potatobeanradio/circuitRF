# Brief 9 — a report a large board can be reviewed from

**Series:** [Impedance review](brief-impedance-0-overview.md) · **Tag:** `R-imp9-n` · **Phase:** report
**Area:** `src/Render/Renderers/TraceImpedanceReportDocument.cs` (the one composition), `src/Render/Renderers/MapLabelPlacer.cs`,
`src/Cli/Impedance.cs` (`-o report.pdf`), `src/Ui/Layout/Impedance/ImpedancePanelViewModel.cs` and its view (Export PDF),
`src/Ui/Layout/LayoutEditorViewModel.TraceImpedance.cs` (`ExportTraceImpedancePdfAsync`)
**Depends on:** 8 (board C must run in minutes to be worth summarising) · **Blocks:** nothing
**Rule:** the board, its vendor and the reporter must not be named anywhere in the repo. It is "board C" (brief 8).

---

## 0. What the report does on a large board (2026-10-27)

The report is one composition (`TraceImpedanceReportDocument.Pdf`): a summary page, then per layer ONE map page, the
table of every trace, the numbered findings, and the junction table, continued over as many pages as they need.
That is right for a scoped review of tens of traces. On board C, whole board, no scope:

- **Top Copper alone is 114 pages** (489 traces, 388 findings, 573 junctions — 5.8 MB). The whole board has 1,800
  traces, **8,326 findings** and 1,257 junctions: thousands of pages. Nobody reads it.
- The findings repeat. Top's 388 come in **11 shapes** — "'Inner 1' is missing below the trace for … µm", "Z0 a–b Ω
  over … µm outside 50 Ω ± 10 %", "'Inner 1' covers only n % of the width below the trace", and so on — each written out
  once per occurrence with its own coordinates. Inner 7 has 3,445 findings on 276 traces.
- The map is one A4 page per layer and `MapLabelPlacer` never drops a label: it takes the least-bad spot. Top's map
  is readable only by zooming the PDF, and one trace near the board's corner carries ~70 overlapping numbered
  markers. Bottom (881 traces) is worse.
- The board was imported from Gerber: its copper carries no net names, so a grouping cannot rely on nets.

## 1. `R-imp9-1` — a Summary style beside the Full one

`TraceImpedanceReportDocument.Pdf(report, style)` with `style` Full (today's report, byte-identical) or Summary. Every
number on a Summary page comes from the same `TraceImpedanceReport`, nothing is recomputed, and the summary page
(what was analysed, against what, the verdict per layer) is the same page in both styles.

Per layer, Summary has:

1. **The map** — the Z0 colouring of every trace as now. Ids only on traces that FAIL. Markers clustered: where
   markers would overlap, ONE badge with the count and the worst severity's fill replaces them. A trace with many
   findings gets one marker carrying its count, not one per finding.
2. **Findings grouped** — one row per (finding kind × severity × width class), with the count, the traces it hits
   (ids, truncated to what fits a line with "+ n more"), and the worst value (largest Z0 excursion, longest missing
   reference, smallest coverage). Width class is the survey's own (`--survey`'s classes), so the grouping matches what
   the user already sees there.
3. **The worst findings in full** — the 20 worst on the layer (fails first, then by excursion), written exactly as in
   the Full report, so the numbers a reviewer quotes are on the page.
4. **Counts, not tables** — passing traces and junctions are counted in a line, not listed.

Accepted findings (brief 5) are shown in Summary as in Full: an accepted finding is grouped with its kind but counted
separately as accepted, and the stale acceptances are listed. A report must never pass by hiding findings.

**Gate for length.** Summary of board C (all ten layers, whole board) is at most ~2 pages per layer with traces plus the
summary page — about 20 pages — with every finding counted somewhere.

## 2. `R-imp9-2` — where the user chooses it

- **CLI:** `--report full|summary` beside `-o report.pdf`. `--json` is unaffected (it always carries everything).
- **Impedance panel, Export PDF:** the save step offers the style, with the page count each would come to (count it
  from the report, do not render twice to find out).

**Owner decision D1 — the default.** (a) recommended: Full, except that when Full would exceed **50 pages** the panel
pre-selects Summary and says why ("Full: 1,240 pages"); the CLI defaults to Full unless `--report summary` (a script
that exported Full yesterday gets Full today). (b) Full always, Summary only when chosen. (c) Summary whenever the
review has no scope (the whole board).

**Owner decision D2 — the grouping key.** (a) recommended: kind × severity × width class, as above. (b) kind ×
severity only (shorter, loses which traces are the controlled ones). Net is not offered: imported boards carry none.

## 3. `R-imp9-3` — the dense map in the Full report too

Clustering markers (R-imp9-1 item 1) is worth having in Full as well, where a cluster is unreadable: a badge expands
to its numbers in the findings list that follows. Change Full only if the badge is used solely where markers would
otherwise overlap, so every scoped report of a few traces stays byte-identical (gate 2).

## 4. Gates

1. Board C, whole board: the Summary PDF's page count (≤ ~20) and that its grouped counts sum to the report's
   findings per layer. Run by hand and reported; board C is not committed and no test names it.
2. Full is byte-identical to today's for the existing report tests (the panel's and the CLI's byte-identity gates)
   — the Summary is new, it changes nothing that exists.
3. A synthetic board with many repeated findings (a few dozen traces, the same return-path gap under each): Summary
   groups them into one row per kind × width class with the right count; the worst-20 list holds the worst.
4. Markers: on a synthetic board with 50 findings in one small area, the Summary map draws one badge with "50", not
   50 markers; on a board with three separated findings it draws three markers.
5. CLI `--report summary` and the panel's Summary export are byte-identical for one report (the existing pattern for
   the Full report).

## 5. Tests (minimal)

One per gate 2–5. Run only the report/impedance classes (`--filter FullyQualifiedName~Impedance` and the report
document's class) — never the whole of `Ui.Tests`.

## 6. Not in this brief

How fast the analysis runs (brief 8). The Impedance panel's own results list (it already filters and expands per
trace). Grouping by net. A Summary for the `rail` verb's report.

## 7. On completion

`src/Render/RESOLVED.md` (what the Summary carries, the grouping, the page count on board C), never any `CLAUDE.md`.
Update the impedance user doc's export section and `docs/design/cli.md`'s `impedance` options for `--report`.
