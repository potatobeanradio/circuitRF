# Sonnet Brief — Trace-card layout + 4 fixes (Z0 S-only, FreqUnit refresh, column reorder, header dbl-click)

Five trace-card / Table follow-ups. Pairs with `brief-trace-expressions.md` (the expression feature); this brief
is the **layout + bug** half. Files: `PlotInspectorView.axaml`, `TraceRowViewModel.cs`,
`PlotInspectorViewModel.cs`, the Table view code-behind (`DataDisplayView`/table host).

---
## 1 — BUG: Z0 shown for HB (cube) traces
The Z0 row's outer `StackPanel Orientation="Horizontal"` **always renders** — only the inner box `StackPanel` is
gated by `ShowZ0Control`. So the `"Z0"` label and `Ω` show even for cube/HB traces (where `ShowZ0Control` is
false). Fix: gate the **whole Z0 row** on a scattering/S-param trace.
- Add `public bool ShowZ0Row => IsScatteringTrace;` to `TraceRowViewModel` (S-param, non-derived, non-cube;
  `IsScatteringTrace` already encodes this). Raise `OnPropertyChanged(nameof(ShowZ0Row))` everywhere the other
  Z0 props are raised (`OnSelectedSignalChanged`, `RefreshDescription`, `RebuildSignals`).
- In the XAML, wrap the **entire** Z0 `StackPanel` (the one containing the `"Z0"` label, the "Multiple Port
  Normalization" label, and the inner `ShowZ0Control` box) with `IsVisible="{Binding ShowZ0Row}"`. Now HB/cube
  traces show no Z0 anything.

---
## 2 — BUG: harmonic units don't refresh when the plot Freq unit changes
`OnFreqUnitChanged` (`PlotInspectorViewModel`) only sets `_plot.FreqUnits` and redraws. The harmonic axis-role
combo entries and the Table column-0 (which the cluster brief made freq-unit-aware) are built once and never
rebuilt, so changing GHz→MHz doesn't update them.
- In `OnFreqUnitChanged`, after setting `_plot.FreqUnits`: re-resolve cube data and rebuild the axis-role rows so
  freq-scaled labels refresh. Call `RebuildAndNotify()` (which re-runs `TrySetCubeData` + `RefreshDescription`)
  AND have each `TraceRowViewModel` rebuild its `AxisRoles` (the pin-combo `opts` are freq-scaled). Add a
  `internal void OnFreqUnitChanged()` on `TraceRowViewModel` that calls `RebuildAxisRoles()` +
  `OnPropertyChanged` for any freq-dependent labels, and have the parent loop call it for every row.
- Ensure the Table redraws (it already does via `PlotNeedsRedraw`). Verify the Table column-0 header/cells pick
  up the new unit (they read `plot.FreqUnits` at draw time, so a redraw suffices once the data is rebuilt).

---
## 3 + 4 — CHANGE: reorder identity-row columns + unify the Y-axis combo
Current identity row: `34 (MatrixType ISB) | * (signal) | 95 (YAxis combo OR cube-transform combo, overlaid) | 26 (→R)`.
Target:
- **Col 0 = data source (signal) combo** — always first.
- **Col 1 = the transform / Y-axis-type combo** — **one unified combo for ALL traces**, using the cube-transform
  style list (`None, dB20, dB10, dB, Mag, Phase, Real, Imag, Conj`). This replaces the *separate* network YAxis
  combo (`AvailableYAxes`/`ShowYAxisCombo`) and cube-transform combo (`AllCubeTransforms`/`IsCubeBoundTrace`) with
  a single combo bound to one property.
- **Col 2 = Matrix Type (S/Z/Y)** — **only when the source is S-parameter** (`ShowMatrixTypeCombo`); collapsed
  otherwise.
- **Col 3 = →R** secondary-axis button (unchanged, Rect only).

### Unify the Y-axis combo (the load-bearing change)
Today network traces use `DependentVarFormat` (`YAxis`) and cube traces use `CubeTransform` (`Transform`). Unify
on **`CubeTransform`** as the single user-facing transform for both:
- Add a single `SelectedTransformItem` (CubeTransformItem) on `TraceRowViewModel`, bound by the one combo.
- For **cube** traces: it sets `_trace.Transform` (as today).
- For **network/S-param** traces: map `CubeTransform → DependentVarFormat` and set `_trace.YAxis`:
  `None→Complex, Mag→Mag, dB20→Db, Phase→Phase, Real→Real, Imag→Imaginary`. The cube-only members
  (`dB10`, `dB`, `Conj`) are **disabled** (shown greyed) for network traces — reuse the existing
  `enabled`-flag idiom (`YAxisItem` had it; give `CubeTransformItem` an `Enabled` flag or wrap in a per-row
  filtered list). On Smith/Polar, force/limit to `None` (complex) as the YAxis combo did.
- Remove `ShowYAxisCombo`/`AvailableYAxes`/`SelectedYAxis` usage from the XAML (keep the props if other code
  references them, but the card binds the unified combo). The combo is shown for Rect/Table (the old
  `IsRectOrTablePlot` gate); Smith/Polar hide it or lock to complex as before.

### XAML column rewrite (identity row)
Replace the identity-row `ColumnDefinitions` and the three combos with:
```
ColumnDefinitions: * (MinW75, signal) | 1000* (MinW50 MaxW95, transform) | Auto (matrix, S-param only) | 26 (→R)
```
- **Col 0:** the existing signal `ComboBox` (`AvailableSignals`/`SelectedSignal`), moved from col 1 → col 0.
- **Col 1:** ONE `ComboBox` bound to `SelectedTransformItem` / `AllCubeTransforms` (with per-item enable),
  `IsVisible` for Rect/Table. Remove the two overlaid combos.
- **Col 2:** the MatrixType `IconSelectButton`, `IsVisible="{Binding ShowMatrixTypeCombo}"`, `Width="Auto"`/34
  (collapses when hidden — use `IsVisible` so the `Auto` column takes zero width when collapsed). Moved from
  col 0 → col 2.
- **Col 3:** the →R button (unchanged).
- **Important — the line/symbol rows below** use the same `34 | * | 95 | 26` column template to align with the
  identity row. Since col 0 is no longer the fixed 34-wide matrix slot, re-check alignment: either keep the
  line/symbol rows' existing template (their ISB stays in a 34 col) and accept that the identity row no longer
  has a leading fixed col, OR shift the line/symbol ISBs to match. Simplest: give the identity row a leading
  `Auto` matrix col on the RIGHT (col 2) and let cols 0/1 be signal/transform; the line/symbol rows keep their
  own layout (their controls don't need to align with signal/transform). Verify visually that nothing overlaps;
  adjust MinWidth/MaxWidth so the signal combo stays readable.

---
## 5 — BUG: double-click on a Table trace column header opens the Plot Properties flyout
Should open the **inline trace editor** (the expression text box) instead. The Table renderer's `HitTest`
already returns `TableHitKind.TraceHeader` with the `HitTrace`. Find the Table view's pointer handler (the
code-behind hosting the Skia table — `DataDisplayView`/the table control's `OnPointerPressed`/double-tap) and:
- On a double-click whose `HitTest` is `TableHitKind.TraceHeader`, **do not** open the Plot Properties flyout.
  Instead open the inline editor seeded with the trace's expression/shorthand (`trace.Expression ??
  trace.CubeShorthand`), commit → parse via `TraceExpression`/`CubeTraceSpecParser` (per the expression brief),
  same invalid-handling. (If the inline-in-table editor isn't built yet, route the double-click to focus the
  trace card's spec textbox for that trace as the interim — but the intended target is an in-place editor over
  the header cell.)
- Ensure other double-click targets (data cells, freq/X header) keep their current behavior; only
  `TraceHeader` changes.
- If the flyout currently opens from a generic "double-click anywhere on the table → properties" handler, narrow
  it: header double-click → inline edit; elsewhere → existing behavior (or nothing).

---
## Tests
- **ShowZ0Row:** false for a cube/HB trace, true for an S-param trace; XAML row hidden when false (VM-level
  assert on the bool).
- **UnifiedTransform_NetworkMap:** setting the unified combo to `dB20` on a network trace sets `YAxis=Db`;
  `Mag→Mag`; `None→Complex`. Cube-only members disabled for network traces.
- **FreqUnitChange_RebuildsAxisRoles:** changing `FreqUnit` rebuilds a harmonic axis-role row's pin options to
  the new unit (e.g. `2 GHz`→`2000 MHz`).
- Header-double-click routing is manual (view code).

## Gate
Build 0W/0E. Manual: an HB/cube trace card shows **no** Z0 row; columns read **signal | transform | (matrix only
for S-param) | →R**; one transform combo (None/dB20/Mag/…) drives both network and cube traces; switching the
plot Freq unit updates the harmonic labels live; double-clicking a Table trace header opens the inline editor,
not the Plot Properties flyout.

## On completion
Note in `src/Ui/CLAUDE.md`: trace-card identity row is signal | unified-transform | matrix(S-param only) | →R;
one `CubeTransform`-style combo drives both network (mapped to `DependentVarFormat`) and cube traces; the Z0 row
is gated entirely on S-param traces; `OnFreqUnitChanged` rebuilds harmonic axis labels; Table trace-header
double-click opens the inline trace editor instead of the Plot Properties flyout.
