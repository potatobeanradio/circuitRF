# Brief YA-8 — Data Display I: bars, histograms, CDF and quantile, yield sensitivity, spec lines, statistics table

**Series:** `brief-yield-0-overview.md` (D9) · **Tag:** `R-ya8-<m>` · **Depends on:** YA-4
**Area:** `src/Render/DataDisplay/Models/Trace.cs`, `Misc.cs` (`TraceProperties`), `DataDisplayConfig.cs`
(`TracePropertiesConfig` — the `.cdd` format), `Renderers/PlotRenderer.cs`, `Renderers/AxesRenderer.cs`,
`Renderers/TableRenderer.cs`, `src/Ui/DataDisplay/ViewModels/` (the trace card), `src/Cli/PlotVerb.cs`,
`src/Cli/RenderDataDisplay.cs`, `docs/design/data-display.md`, `trace-card.md`, `docs/user/src/reference/` (Data
Display page)

---

## 1. Goal

The statistical pictures a yield user reads first — the histogram against its spec, the CDF and quantile plot that
expose a tail, the yield sensitivity histogram that says where to move a nominal, and the statistics table — drawn
by the Data Display, written into a `.cdd`, and drawn identically headlessly.

## 2. Design rule

**The numbers come from YA-4's expression functions; the Data Display adds drawing, not statistics.** A histogram is
a trace whose expression is `histogram(goal:S21:worst, 30)` over the `bin` axis that function creates — so the CLI,
`plot`, a `measure` line and the GUI all compute it with the same code. What this phase adds is how such a trace is
**drawn** and how a user **gets one without typing it**. No new `PlotType`: these are Rect plots (and a Table), so
markers, export, Plot Versus and the trace card keep working.

## 3. Requirements

**R-ya8-1 — Bars draw style.** `TraceProperties` gains a draw style `Line|Bars|Step` (`Line` default; persisted in
`TracePropertiesConfig` with the `.cdd` rules — a display that uses none writes identical bytes). Bars use the
`width` companion the `histogram` function returns (or the x spacing when absent), the trace's colour at the fill
opacity the colour theme uses for fills, an outline in the line colour. A family of bars (a histogram per corner)
draws side-by-side within each bin. Autoscale includes zero on the y axis for bars.

**R-ya8-2 — The trace card's Statistics menu.** On any trace whose cube has a `trial` axis (or any axis, chosen in
the menu), a **Statistics** submenu rewrites the trace as: **Histogram** (bins: auto by Freedman–Diaconis, editable;
count or percent), **CDF**, **Normal quantile** (sample quantiles against Φ⁻¹ — a straight line is Gaussian), and,
when the source has `pass`, **Yield sensitivity vs ▸** <each `stat:` key> (bars: yield % per bin; a light second
series: trials per bin). Each choice writes an ordinary expression into the trace and sets the draw style — the card
shows the expression, so the user can see and edit what was done. "Back to curves" restores the original expression.

**R-ya8-3 — Spec lines.** A plot drawing a yield goal's value shows that goal's limits: on a histogram/CDF of
`goal:<g>:worst`, a vertical line per limit (a sloped limit draws its tighter end, labelled); on a curve family over
the goal's range axis, the limit line across the goal's range (sloped where sloped). Drawn in the theme's limit colour,
dashed, labelled with the goal name and limit. Toggled per plot (**Spec lines**, default on when the source is a yield
result). The goal-to-trace link reuses `TraceGoalReader`/`TraceToGoal` in reverse — one place knows how a goal and a
trace correspond.

**R-ya8-4 — Normal fit overlay.** On a histogram, an optional fitted normal curve (mean and σ of the data, scaled to
the bin heights), off by default, toggled from the trace card.

**R-ya8-5 — The statistics table.** A Table plot preset: rows are the yield goals' `worst` values and the bench's
scalar measures; columns mean, σ, min, max, median, P1, P99, skew, kurtosis, Cpk (where a goal gives limits),
σ-to-limit, yield and its interval. Built from YA-4's functions, so the same numbers as the CLI's statistics table.

**R-ya8-6 — Headless parity.** `plot --trace` accepts `style=bars|step` and `stat=histogram|cdf|quantile|yieldsens`
(each rewriting the trace exactly as the menu does), and `--spec-lines`. `render --data` draws a `.cdd` holding any
of these. Gate: the CLI and the in-process composer produce **byte-identical** SVG for a histogram with spec lines.

**R-ya8-7 — Live.** While a run publishes in memory (YA-4 R-ya4-6), histograms and the table update per coalesced
frame; bin edges are held fixed after the first frame that has ≥ 30 trials (so bars grow rather than jump), and
re-derived when the run finishes.

## 4. Gates (minimal; run only these classes)
- `BarsRenderTests` — a 5-bin histogram renders 5 bars at the expected pixel rectangles; a 2-member family renders
  side-by-side; a display without bars writes the same `.cdd` bytes as before.
- `StatisticsMenuTests` — each menu entry writes the expected expression and style, and Back to curves restores the
  original exactly.
- `SpecLineTests` — a `ge 14` goal draws one vertical line at 14 on its histogram; a sloped goal over freq draws a
  sloped line across its range on the family.
- `YieldSensitivityTests` — on a divider whose yield rises with R1, the yield-sensitivity bars increase monotonically.
- `HistogramPlotParityTests` — R-ya8-6's byte identity.
