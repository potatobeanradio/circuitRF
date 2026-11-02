# Brief: Data Display Table improvements — resize, sort glyph, wheel-zoom, rank≥2 render, copy, node-index

Stack/rules: .NET 10, Avalonia 12, `TreatWarningsAsErrors=true` (capture nullable-property reads into
locals). UI-only. Build must end **0W/0E**; add gate tests; report total test count. Newest-first
changelog entry in `src/Ui/CLAUDE.md` after landing. Adding **defaulted** fields to `TraceConfig` is
safe under the alpha no-migration rule — no `.cdd` format_version bump.

Primary file: `src/Ui/DataDisplay/Renderers/TableRenderer.cs` (verified). Interactive handlers
(column-resize drag, pointer-wheel, "Copy Table Data") live in
`src/Ui/DataDisplay/Controls/PlotControl.cs` (65 KB) — locate by grepping `ColumnWidth`,
`ResizeHandle`/`TableHitKind.ResizeHandle`, `OnPointerWheel`/`PointerWheelChanged`, and
`BuildCopyGrid`/"Copy Table Data".

Six items; land incrementally.

---

## Item 1 — Column-resize drag is wrong when there are multiple X-axis columns

**Root cause (verified).** Every `XAxis` column draws its width from a **single** shared value
`plot.ColumnWidth` (`TableRenderer.BuildLayout`: `XAxis → Max(MinColumnWidth, plot.ColumnWidth)`;
`TotalColumnWidth` and `CalcFitWidth` likewise). With ≥2 distinct X groups, resizing one XAxis column
writes `plot.ColumnWidth` and resizes **all** XAxis columns at once; worse, because earlier XAxis columns
widen mid-drag, the dragged column's left edge (`ColX[resizeCol]`) shifts under the cursor, so the
`newWidth = mouseX − ColX[resizeCol]` math diverges → the "incorrect calculation" the user sees.

**Fix — per-X-group width stored on the anchoring trace.**
- Add `double XColumnWidth { get; set; } = 0;` to `Trace` (0 = "fall back to plot.ColumnWidth"). Persist
  as a defaulted `TraceConfig.XColumnWidth` (round-trip in `BuildTraceConfig` / `LoadPlotContainerConfigAsync`,
  default 0).
- In `TableRenderer`, every XAxis width read becomes: `var anchor = plot.Traces[col.FirstTraceIndex];
  double w = anchor.XColumnWidth > 0 ? anchor.XColumnWidth : plot.ColumnWidth;` then
  `Max(MinColumnWidth, w)`. Apply in `BuildLayout`, `TotalColumnWidth`, and the `CalcFitWidth` write path.
- In `PlotControl`'s resize-drag handler: when the dragged column is `XAxis`, write the new **logical**
  width to `plot.Traces[col.FirstTraceIndex].XColumnWidth` (not `plot.ColumnWidth`); `TraceValue` columns
  keep writing `trace.ColumnWidth` as today. Auto-fit (double-click) on an XAxis column writes the anchor's
  `XColumnWidth` too.

Each X group is now independently sized, and each column's width no longer depends on the others, so the
drag delta is stable. (Backward-compat: existing `.cdd` files have `XColumnWidth=0` → fall back to
`plot.ColumnWidth`, unchanged single-X behavior.)

**Test.** Two cube traces with different X axes (e.g. one `Pin` sweep, one `node`) → two XAxis columns;
resizing the second XAxis column changes only it; the first is unaffected and the new width equals the
drag target (within rounding).

---

## Item 2 — Sort-direction glyph bleeds into the adjacent header when the X column is narrow

**Root cause (verified).** In `DrawHeaderRow`, the arrow X is
`triCx = ColX[c] + ScaledPaddingX + measuredW + spaceW*1.5 + triSize*0.5`, where `measuredW` is clamped to
`ColW[c] − 2·padding` — but the trailing `+ spaceW*1.5 + triSize*0.5` pushes the arrow **past**
`ColX[c] + ColW[c]` once the column is narrow, so it overdraws the next header.

**Fix.** Clamp the arrow center to stay inside the column:
```csharp
float maxCx = layout.ColX[c] + layout.ColW[c] - triSize * 0.5f - CellBorderWidth;
triCx = Math.Min(triCx, maxCx);
```
(Optionally skip the arrow entirely when `ColW[c]` can't fit text+arrow, but the clamp alone fixes the
bleed.) The arrow stays within its column at all widths.

**Test.** Render a Table with an XAxis column width at `MinColumnWidth`; assert the computed `triCx +
triSize*0.5 ≤ ColX[c] + ColW[c]`.

---

## Item 3 — Mouse wheel zooms the Data Display when the Table doesn't need scrolling

**Behavior.** Scrolling the Table has priority; if the Table's rows all fit (no scroll needed), the wheel
zooms the Data Display instead.

**Fix.**
- Add a public helper to `TableRenderer`:
  ```csharp
  public static bool CanScroll(Plot plot, (double W, double H) canvasSize, float zoomLevel = 1f)
  {
      var layout = BuildLayout(plot, canvasSize, zoomLevel);
      return layout.RowCount > layout.VisibleRowCount;
  }
  ```
- In `PlotControl`'s pointer-wheel handler, in the `PlotType.Table` branch: if
  `!TableRenderer.CanScroll(plot, canvasSize, zoom)`, **do not** scroll and leave `e.Handled = false` so
  the event bubbles to the parent `PlotCanvasView`, which already zooms the Data Display on wheel. When it
  can scroll, scroll as today and set `e.Handled = true`.
- Confirm the wheel actually propagates to the zoom handler when left unhandled (the canvas-level
  scroll-zoom in `PlotCanvasView`). If `PlotControl` swallows wheel unconditionally for tables today, this
  is the one-line gate that fixes it.

**Test.** `CanScroll` returns false when `RowCount ≤ VisibleRowCount`, true otherwise (BuildLayout-backed).
Interactive zoom-vs-scroll is verified by hand.

---

## Item 4 — Render rank≥2 (family / parametric) cube traces in the Table without crashing

**Goal (per user): simple, crash-proof, shows all the data — "loop the data" like `TsvWriter`.**

Today `BuildColumns` only models a rank-1 cube X axis (`trace.CubeXValues`) plus the scalar anchor case.
A **family** cube trace (`trace.IsFamily`, rank-2: one kept X axis + one `FamilyIterate` axis → N
`FamilyCurves`) isn't represented, so the Table shows wrong/blank values; rank≥3 specs are already
`InvalidSpecText` (blank) at the picker.

**Fix — expand a family trace into one value column per family curve (loops the family dimension into
columns; Excel-friendly on copy).**
- Add `int FamilyCurveIndex = -1;` to `TableColumn` (-1 = ordinary value column).
- In `BuildColumns`, branch cube traces explicitly so they **never fall through** to the network
  `trace.Data` branch:
  - scalar (`CubeIsScalar`) → current blank-header anchor + one value column (unchanged).
  - family (`trace.IsFamily` and `FamilyCurves.Count > 0`) → emit the shared X column from the family's
    common X axis (use the curves' shared X values), then loop `FamilyCurves` (cap at
    `Trace.MaxFamilyCurves`) emitting one `TableColumn{ Kind = TraceValue, FirstTraceIndex = ti,
    FamilyCurveIndex = k, Header = baseShorthand + " @ " + familyLabel(k) }`. `familyLabel(k)` =
    the curve's family-axis value/label (read from the `FamilyCurve`; e.g. `FamilyAxisName=value`).
  - rank-1 (`CubeXValues` non-null, not family) → current behavior.
  - invalid / null `CubeXValues` (`InvalidSpecText != null`, or rank≥3) → emit an X column with **empty**
    `XValues` and a value column that formats to `""`. Never index a null array; never fall to the
    `trace.Data` Freq branch.
- `FormatColumnCell` TraceValue branch: if `col.FamilyCurveIndex >= 0`, return the family curve's
  formatted value at the row's X — add a `Trace` accessor mirroring `FormatCubeCell`, e.g.
  `FormatFamilyCell(int curveIndex, int xIndex, string fmt, int digits)` (look up the row's X in the
  curve's shared X, read that curve's complex/real value, apply the trace transform + format). For an
  out-of-range / missing sample return `""` (not a throw).
- Hit-test (`HitTest`) and marker logic already key on `FirstTraceIndex`; family value columns map to the
  same trace, which is fine (markers on family traces are unsupported → no marker rows).

This is intentionally a fallback presentation: N columns titled `expr @ Pin=0dBm`, `expr @ Pin=5dBm`, …
It shows everything, never crashes, and copies cleanly (Item 5). Don't over-polish.

Confirm on disk: the `FamilyCurve` shape (its family value/label + per-point values + shared X), and that
`MaxFamilyCurves` caps curve count (it does, =101).

**Test.** A Table plot with a family cube trace (N curves) → `BuildColumns` yields 1 X column + N value
columns; `FormatColumnCell` returns each curve's value per row; a rank≥3 / invalid cube trace yields blank
cells and does not throw.

---

## Item 5 — "Copy Table Data" produces meaningful tab-delimited output for higher-order columns

`BuildCopyGrid` already builds headers + rows from `BuildColumns` + `FormatColumnCell`. With Item 4's
family-as-columns change, copy **automatically** emits one tab-separated column per family curve with a
descriptive header (`expr @ Pin=0dBm` …) — pasting into Excel yields real columns. No change to the copy
command itself; just verify:
- The "Copy Table Data" command (in `PlotControl`) calls `TableRenderer.BuildCopyGrid(...)` and joins
  `headers` + `rows` with `\t` / `\n`.
- Headers with separators (`∠`, `@`) survive as plain text (they do — tab/newline are the only
  delimiters).
- Blank cells for short/invalid columns stay `""` (already handled).

**Test.** `BuildCopyGrid` on a family-trace Table returns a header row with N family columns and data rows
whose family cells are populated; column/row counts match the on-screen plan.

---

## Item 6 — Render a "node" X axis as integer values

When the Table's X axis is a node-index axis, show integers (no decimals) to signal it's an index.

**Fix.**
- Add `bool IsNodeAxis;` to `TableColumn`; in `BuildColumns` set it `true` when the cube X axis name is the
  node axis (confirm the exact string — the node-picker work uses axis name `"node"`; set
  `IsNodeAxis = string.Equals(axisName, "node", StringComparison.OrdinalIgnoreCase)`).
- In `FormatColumnCell`, XAxis branch: if `col.IsNodeAxis`, return
  `((long)Math.Round(xVal)).ToString(System.Globalization.CultureInfo.InvariantCulture)` instead of the
  `FormatString`/digits path. (Node axes are never freq-scaled, so this precedes the `IsFreqUnit` check.)

**Test.** A cube trace whose X axis is `node` → X-column cells render as `0,1,2,…` (no decimal point),
while a non-node numeric axis still uses the configured format.

---

## Notes
- Items 2 and 6 are pure `TableRenderer` one-liners; Item 4 is the substantive one; Items 1 and 3 touch
  `PlotControl` handlers (grep to locate).
- `Trace.XColumnWidth` and `TableColumn.FamilyCurveIndex`/`IsNodeAxis` are additive; no enum or
  format_version churn.
- Capture nullable-property reads into locals (TreatWarningsAsErrors).
