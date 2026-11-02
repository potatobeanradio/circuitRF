# Brief YA-9 — Data Display II: pass/fail families, envelopes, scatter, contributions, linked trial selection

**Series:** `brief-yield-0-overview.md` (D9) · **Tag:** `R-ya9-<m>` · **Depends on:** YA-8
**Area:** `src/Render/DataDisplay/Models/Trace.cs` (family fields), `Renderers/PlotRenderer.cs`,
`DataDisplayConfig.cs`, `src/Ui/DataDisplay/ViewModels/` (`PlotInspectorViewModel`, the trace card, the family
rows), `src/Ui/DataDisplay/DataDisplayDocumentViewModel.cs`, `src/Cli/PlotVerb.cs`, `docs/design/family-curves.md`,
`plot-versus.md`, `data-display.md`

---

## 1. Goal

Seeing *which* trials fail and *why*: every trial's response coloured by pass/fail with the nominal on top; an envelope
that reads at a glance where 1,000 curves cannot; scatter plots coloured the same way; a contribution chart; and one
selected trial highlighted everywhere at once, with the actions a designer takes next.

## 2. Requirements

**R-ya9-1 — Colour a family by a category.** A family trace (`FamilyAxisName` = `trial`, or any family axis) gains
**Colour by**: a per-member cube on the family axis (`pass` by default on a yield source; any `goal:<g>:pass`; or
`corner`). Pass members draw in the trace's colour at reduced opacity, fail members in the theme's fail colour, did-not-
evaluate members not at all (listed in the legend count). Legend: `S21 — 471 pass · 29 fail`. Persisted in the
`.cdd`; absent writes identical bytes. **Draw order:** pass, then fail, then the nominal — so failures are never
buried under passes.

**R-ya9-2 — The nominal.** On a yield source, the nominal group's curve draws over the family in the trace's full
colour and width (toggle **Show nominal**, default on).

**R-ya9-3 — Envelopes.** A family gains **Envelope**: `off` | `min–max` | `percentile p` (e.g. P1–P99) | `mean ± kσ`,
drawn as a shaded band behind (or instead of — **Curves: on/off**) the members, with the mean or median as a line.
Computed per x point from YA-4's `_over` functions on the family axis — one implementation. With `Curves: off`, a
10,000-trial family draws as one band and stays interactive. On a Smith or Polar plot an envelope is refused — a
pointwise band of complex values is not a region — with the reason in the menu item's tooltip.

**R-ya9-4 — Scatter.** Plot Versus with both sides on the `trial` axis (`goal:S21:worst vs stat:R1.R`) draws as
points (line off, markers on — existing `TraceProperties`) and takes the same **Colour by**. The trace card's
Statistics menu (YA-8) gains **Scatter vs ▸** <each `stat:` key and each other scalar>, writing the Versus spec and
the style. An optional least-squares line with its R² in the legend.

**R-ya9-5 — Contribution Pareto.** A bar chart (YA-8's bars over a categorical x axis of contributor names, sorted
by share, with a cumulative line on a secondary axis) of YA-4's `Contributions` for a chosen goal or measure. Because
contributions are never computed unasked, the chart is created by an explicit action (trace card ▸ Statistics ▸
**Contributions**, or the panel) that computes them once and stores them in the source as a cube
(`contrib:<goal>` over a labelled `contributor` axis) so the `.cdd` redraws without recomputing.

**R-ya9-6 — Linked trial selection.** Clicking a family member, a scatter point, or a histogram bar selects
trial(s) in that **source** (a bar selects every trial in its bin). The selection is shared by every plot in every
Data Display bound to that source: the selected members/points draw highlighted and the rest dim; a status chip
shows `Trial 417` (or `23 trials`). Esc clears it. Selection is session state, never persisted. The trace's context
menu, and the Yield panel's trial table (YA-10), offer on a single selected trial: **Send trial to Tuning** (loads its
values into the Tuning sliders via `TuningPanelViewModel.LoadValues`, which TO-10 already uses for Send to Tuning),
**Re-run trial** (YA-4 R-ya4-7 — shows its full result as a snapshot ghost, tuning D9), **Copy values**, and **Save
as corner…** (YA-6).

**R-ya9-7 — Headless.** `plot --trace` accepts `colorby=pass|<cube>`, `envelope=minmax|p:1|sigma:3`, `curves=0`,
`nominal=0|1`; selection is interactive only. Gate: SVG byte parity CLI vs in-process for a pass/fail family with a
P1–P99 envelope.

## 3. Gates (minimal; run only these classes)
- `FamilyColourByTests` — a 10-member family with 3 fails draws 7 + 3 in two colours, fails after passes, nominal last.
- `EnvelopeTests` — min–max band equals the pointwise min and max; percentile band equals `pctl_over`; Smith refuses.
- `ScatterTests` — the menu writes the Versus spec; colours follow `pass`; the fit line's R² equals an independent
  computation.
- `TrialSelectionTests` — selecting a point in one plot highlights the same trial in a second display bound to the
  same source; a bar selects its bin's trials; Send to Tuning loads that trial's values.
- `TrialPlotParityTests` — R-ya9-7.
