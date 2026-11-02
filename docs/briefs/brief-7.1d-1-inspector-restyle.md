# Sonnet Brief — Phase 7.1d-1: restyle the Plot Inspector (owner visual spec)

**Design:** `docs/design/data-display.md` §2.8 + §3 → "7.1d-1". **Scope: restyle `PlotInspectorView.axaml` to
the circuitRF AnalysisEditor idiom, per the owner's explicit visual spec below.** Keep **all existing trace
bindings/commands/behavior** — every edit must still redraw live. The only VM/converter additions allowed are
the scoped presentation helpers in §A (per-type plot flags + set-commands; a line-style glyph converter).
Idiom reference: `src/Ui/Views/Analyses/SpBodyView.axaml` (segmented `.active` toggles, opacity-tiered labels,
filled rounded rows) + `src/Ui/Views/Dialogs/AnalysisEditorDialog.axaml`.

## A. Scoped VM / converter additions (the only non-view changes)
In `PlotInspectorViewModel`:
- Add bool getters `IsRectPlot`/`IsSmithPlot`/`IsPolarPlot` (compare `_plot.PlotType`); `IsTablePlot` exists.
- In `OnPlotTypeChanged`, also `OnPropertyChanged` for `IsRectPlot`/`IsSmithPlot`/`IsPolarPlot`/`IsTablePlot`
  (so the segmented highlight follows the active type).
- Add four parameterless commands `SetPlotTypeRectCommand`/`…SmithCommand`/`…PolarCommand`/`…TableCommand`,
  each just `PlotType = PlotType.<X>;` (the existing `[ObservableProperty] PlotType` setter already rebuilds +
  redraws). Mirror SpBodyView's `SetModeStepCommand` pattern.
- Add a converter `LineTypeToDashArrayConverter` (`Converters/`) → maps `LineType` to an
  `AvaloniaList<double>` dash pattern (Solid → empty, Dashed → `4,2`, Dotted → `1,2`, etc.) for the line-sample
  glyph in §E. (If a `LineType`→sample is cleaner another way, fine — keep it presentation-only.)

No other VM changes. Do **not** touch trace-model behavior.

## B. Top of inspector — segmented plot-type header + relocated Freq/Trace
Replace the current `Type:` combo + the plot-level grid with **two rows**:
- **Row 1 (centered header): a segmented plot-type selector.** Four `Button.seg-btn` toggles (copy the
  `seg-btn` + `.seg-btn.active` styles from SpBodyView), centered (`HorizontalAlignment="Center"`), each a
  **glyph** with `Classes.active` bound to the matching `IsRectPlot`/`IsSmithPlot`/`IsPolarPlot`/`IsTablePlot`
  and `Command` = the matching `SetPlotType…Command`. This row **is** the inspector header (drop the separate
  "Plot Properties" title text, or keep a tiny title above it — owner's call; prefer the segmented row as the
  header). Glyphs:
  - **Rect** → Material `ChartLine`. **Table** → Material `Table`.
  - **Smith / Polar** have no good Material glyph — draw a small (~14×14) custom glyph in the button: Smith =
    an `Ellipse` + a horizontal diameter `Line` + one inner arc `Path`; Polar = two concentric `Ellipse`s + a
    vertical & horizontal `Line` cross. Keep them monochrome (inherit `Foreground`) so `.active` recolors them.
    (If a custom glyph proves fiddly, fall back to Material `Web` for Polar and `ChartArc` for Smith and flag
    for owner review.)
  - Add `ToolTip.Tip` = the type name on each.
- **Row 2: actions + freq, using the freed width.** **`+ Trace` button on the LEFT** (left-aligned; style like
  SpBodyView's `+ Segment`, keep `AddTraceCommand`). On the **right**: `Freq:` label (opacity-tiered) + the
  freq-unit combo (`AllFreqUnits`/`FreqUnit`), and the table **Font Size** NUD (keep `IsVisible=IsTablePlot`).

Then the `Separator` + the trace-card `ScrollViewer` as today.

## C. Denser trace cards + compact combos
- Re-skin `Border.traceCard` to the filled rounded-row idiom: `Background="{DynamicResource SystemChromeLowColor}"`,
  `CornerRadius="4"`, tighter `Padding`/`Margin` (match SpBodyView). Drop the outlined border look.
- Add a **`ComboBox.compact` style** (FontSize ~9–10, reduced `MinHeight`/`Padding`, no spinner chrome) and
  apply it to the trace-card combos so they stop reading large. The base `FontSize` resource is `10`; bring the
  card combos in line with that or smaller.
- Field labels inside cards (Z0, Format, Digits, Highlight) → opacity-tier (`Opacity≈0.6`, FontSize 10).

## D. Glyph combos — MatrixType, LineType, MarkerType (smaller)
- **MatrixType** (`AllMatrixTypes`, currently text): render each item as a **letter on a small rounded box** —
  a `Border` (subtle `SystemBaseLowColor` bg, `CornerRadius=3`, ~18×16) containing a centered `TextBlock`
  showing the type token (`{Binding}` → "S"/"Y"/"Z"; multi-char tokens just show their text). Shrink the combo
  to fit the box.
- **LineType** (`AllLineTypes`, currently "Solid"/"Dashed" text): render each item as a **drawn line sample** —
  a thin `Rectangle`/`Line` (~26×1.5) with `StrokeDashArray` from `LineTypeToDashArrayConverter`. Narrow the
  combo to the sample width.
- **MarkerType** (`AllMarkerTypes`, already `MaterialIcon` glyphs): just **shrink** — icon ~10×10 and reduce the
  combo `Width` (currently 68) to ~40.

## E. Line / Symbol → equal-size glyph toggle buttons
Replace the `Line` and `Symbol` **checkboxes** (their differing text widths cause the misalignment) with two
**equal-size glyph toggle buttons** that turn the line / symbol on and off:
- Use `ToggleButton`s styled like `seg-btn` but keyed on `:checked` for the `.active` look, `IsChecked` bound
  `{Binding LineEnabled, Mode=TwoWay}` and `{Binding MarkerEnabled, Mode=TwoWay}` respectively.
- Glyphs: Line → Material `VectorPolyline` (or `ChartLine`); Symbol → Material `ChartScatterPlot` (or `Circle`).
  `ToolTip.Tip` "Show line" / "Show symbol".
- They sit where the checkboxes were, at the head of the line-row and symbol-row; the width/colour/style and
  size/colour/shape controls that follow keep their existing bindings + `IsEnabled="{Binding LineEnabled}"` /
  `{Binding MarkerEnabled}`. Equal-size buttons fix the alignment.

## Guardrails
- Keep every binding/command/converter for the controls you re-skin; no behavior change beyond §A.
- No Properties dock (7.1d-2), no MarkerEditor restyle (7.1d-3), no DataSet (7.2). Don't touch `PlotControl`.
- The flyout stays reachable exactly as today.

## Gate (acceptance)
1. Builds green. Inspector reads as an AnalysisEditor sibling: segmented glyph plot-type **header** (centered,
   highlights the active type), `+ Trace` on the left, freq/font on the right.
2. Plot-type buttons switch type and **highlight** the active one; every trace edit still redraws live.
3. Trace cards are visibly **denser**; MatrixType shows lettered boxes, LineType shows line samples, MarkerType
   shows small shape glyphs; Line/Symbol are **equal-size glyph toggles** that enable/disable their rows.
4. All four plot types show the correct controls; Smith/Polar glyphs are recognizable (or flagged for review).

## On completion
"Phase 7.1d-1 — COMPLETE" to `src/Ui/CLAUDE.md`. Report build result + a screenshot of the restyled flyout
(ideally one Rect and one Table plot) for owner review. Next: **7.1d-2** (Properties-dock surface).
