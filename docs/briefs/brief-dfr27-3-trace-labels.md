# Brief DFR27-3 — Data Display: a trace's own label

**Series:** designer feedback round 27 (2026-10-25). · **Tag:** `R-dfr27-3-<n>`
**Area:** the `.cdd` trace model (`src/Render/DataDisplay/Models/Trace.cs`, `DataDisplayConfig`, `PlotConfigLoader`),
`LegendRenderer`, `AxesRenderer.DrawTitleAndAxisLabels`, the marker box lines (`Trace.BuildMarkerBoxLines`,
`GetMarkerValString`, `GetMultiMarkerLine`), the trace row UI (`src/Ui/DataDisplay/ViewModels/TraceRowViewModel.cs`),
and the CLI's `plot --trace` spec (`src/Cli/PlotVerb.cs`). Findings go in `src/Render/RESOLVED.md`.
**Tests:** `tests/Ui.Tests` (render + round trip), the CLI plot tests.

---

## 0. What the designer asked for

One bench simulated three variants side by side (as built, test point cut, fix), so every trace came from the same
source and the legend, the Y-axis titles and the marker boxes all read `TP149_compare..SP1.S(2,1) dB20`,
`…S(4,3)…`, `…S(6,5)…`. What a reader needs is what each curve IS. The designer wants to type a label per trace.

A trace label is free text with no list of right answers, so a text box is the right control here.

## 1. What to do

### R-dfr27-3-1 — `Label` on a trace, stored in the `.cdd`

An optional string. Empty or absent means today's generated text exactly, so every existing `.cdd` draws unchanged
and writes back unchanged (no new key written when empty).

### R-dfr27-3-2 — Where it shows

When set, it replaces the generated name in the legend, the per-trace Y-axis title, the marker info box and
multi-marker lines (keeping `=value` after it), the table plot's column header, and exported data's column name.
The quantity is NOT appended: the label is the whole name. The source alias (`SourceAliases`) is unchanged and still
applies to traces without a label.

### R-dfr27-3-3 — Where it is edited

A `Label` text box in the trace's row, beside the trace's other settings, watermarked with the generated name so a
designer sees what they are replacing. Committed on Enter or focus loss, one undo step. The legend entry's context
menu offers **Rename…** to the same field.

### R-dfr27-3-4 — Headless

`plot --trace cube=S,i=2,j=1,y=db20,label="As built"` sets it, and `--write-cdd` writes it, so the CLI and the
GUI produce the same document.

Gate: a `.cdd` with three labelled traces round-trips byte for byte; its rendered legend, axis titles and marker box
show the labels; the same `.cdd` with the labels removed renders pixel-identical to today's.
