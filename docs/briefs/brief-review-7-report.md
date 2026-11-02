# Brief 7 — the review report: a PDF a reviewer can circulate

**Series:** [Layout Review](brief-review-0-overview.md) · **Tag:** `R-rev7-n` · **Phase:** report
**Area:** new `src/Render/Renderers/LayoutReviewReportDocument.cs`, `src/Cli/Review.cs` (`-o`), the Review panel's
Export PDF; shares page furniture and marker clustering with `TraceImpedanceReportDocument.cs`
**Depends on:** 6 · **Blocks:** nothing

---

## 0. What this brief builds

A review ends in something a reviewer sends to the designer. This is that document, from the one result every other
surface reads, drawn by the renderers the application draws with (`src/Render`), and on brief-impedance-9's lesson from
the start: **a report a large board turns into hundreds of pages is not a review**, so there is one style and it is
the summary.

## 1. Requirements

**R-rev7-1 — Page 1: the summary.** The layout's name, technology, date, and one row per check: title, the thresholds it
ran with (marking those that differ from the default), its finding count, its accepted count, or *not run* and why. A
clean check reads "No findings", never a blank cell.

**R-rev7-2 — Per check with findings, per layer: one map page.** The board outline and that layer's copper in its
technology colour, faded; every finding's marker on top, numbered; markers that would overlap drawn as one counted badge
(brief-impedance-9's clustering, shared, not copied). A check whose findings span two layers (`stacked-traces`) is drawn
on the upper layer with the lower one's copper outlined.

**R-rev7-3 — Per check: one findings page.** The first 20 findings in the result's order, each its number (matching the
map), location in the display unit and sentence, then "… and N more" with the count. Accepted findings follow, each with
its reason. Nothing beyond 20 is listed; the full list is `circuitrf review --all` or `--json`, and the page says so.

**R-rev7-4 — Page count known before writing.** Laid out without writing, on brief-impedance-9's terms, so the panel can
show "Export PDF (14 pages)". The gate: board C's report with every check enabled is **at most ~2 pages per check with
findings, plus one**, whatever its finding counts.

**R-rev7-5 — Two ways out, one document.** `circuitrf review <path> -o report.pdf` and the panel's Export PDF call the
same `LayoutReviewReportDocument.Pdf` with the same result. The CLI's `-o` writes nothing else; a cancelled run writes the
checks that finished and says on page 1 that it was cancelled.

## 2. Tests (`tests/Ui.Tests/Review/ReviewReportTests.cs`)

1. A seeded result with two checks with findings, one clean, one *not run* → the laid-out page count is 1 + 2 + 2, and
   page 1's text carries all four rows (read the PDF's text, not its pixels).
2. 500 seeded findings in one check → still two pages for that check, "… and 480 more" on the findings page.
3. The verb run as a PROCESS writes a PDF byte-identical to the in-process call, every byte but the creation timestamp,
   which the PDF carries by design (the `em` and `render` gates' pattern).

Run only that class.

## 3. By hand on board C (completion note)

Every check enabled: page count, the time to lay out and write, and the PDF attached to the completion note for the
owner.

## 4. Docs

`docs/user/src/reference/cli.md`: `-o` on the `review` section. `layout-editor.md`: Export PDF in the Layout Review
section.
