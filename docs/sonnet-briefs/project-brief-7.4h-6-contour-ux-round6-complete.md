# Phase 7.4h-6: Contour UX Round 6 — Complete

**Date:** 2026-06-21  
**Gate tests added:** 12 (T40–T51 in ContourTraceCardTests; 2 in LoadpullSurfaceTests)  
**Total tests:** 1775 (1363 Ui.Tests + 412 Engine.Tests)  
**Build:** 0W/0E

---

## Slice 6a — Serious Bugs

### §2: MXP/MXE kernel thread (LoadpullSurface.cs)
`MaxPower`, `MaxEfficiency`, and `GetMxx` now accept `RbfKernel kernel`, `double smooth`, `double? epsilon` params. `RecommendedBox` now forwards `fit.Kernel/Smooth/Epsilon` to its two internal `GetMxx` calls. Previously MXP/MXE always used defaults regardless of user's InterpolationKernel/Smoothing/Epsilon settings.

### §3: Copy/paste drops ContourData (ContourData.cs + Trace.cs)
Added `Clone()` on `ContourData` — copies all 27 authoring/style fields, leaves Grid/Scatter/Levels/MxpCoord/MxeCoord null. Added `ContourData = src.ContourData?.Clone()` in Trace copy constructor so paste produces an independent ContourData that re-fits on its own.

---

## Slice 6b — Defaults

### §4: ColorMap default → Bone
`ContourData.ColorMap` defaults to `ContourColorMap.Bone` (was Hot). `AddContourTrace` inherits the last contour trace's ColorMap (or Bone if first).

### §6: LabelForeground/Background defaults
`LabelForeground` → `SKColors.Black` (was White). `LabelBackground` → `SKColors.White` (was semi-transparent black). Black text on white background label boxes.

### §9: LabelSpacing default → 30.0
`ContourData.LabelSpacing` defaults to 30.0 (was 1.0). `TraceRowViewModel._contourLabelSpacing` field updated to match. NUD maximum raised from 10 → 1000 in AXAML.

### §13: DrawLabels set at add-time
`ContourData.DrawLabels` no longer has an initializer (defaults false). `AddContourTrace` sets `DrawLabels = (plane == SurfacePlane.Z)` — true for Rect plots (meaningful contour labeling), false for Smith/Polar (too cluttered).

---

## Slice 6c — Render Fixes

### §1: Smith fill ragged edge → Γ-disk clip
`DrawTopoMapFill` takes new `SurfacePlane plane = SurfacePlane.Gamma` param. When `plane == Gamma`, adds a canvas Save + `ClipPath(unit circle * 1.02f, antialias: true)` before the SaveLayer pass, with an extra Restore at the end. Marching-squares skips NaN cells at the Γ-boundary — clipping gives a clean circular edge. `PlotRenderer` passes `contourPlane` derived from `plot.PlotType`.

### §5: Label box thin black stroke
`DrawIsoLines` adds a `bgStroke` paint (`StrokeWidth=0.75f`) applied after the fill rect on each label box. Color: `new SKColor(0,0,0, (byte)(120*fadeF))`.

### §10: Remove zoom division from DrawOptimumMarker
`DrawOptimumMarker` no longer divides marker radius/stroke/font by `zoomLevel`. Fixed sizes: `r=7f, sw=1.5f, fs=9f` (canvas-px, zoom-independent). Previously zooming in shrank markers to invisibility.

---

## Slice 6d — Card Polish (AXAML)

### §7: Const-metric ComboBox with disabled items
Bound to `ConstraintMetricOptions` (ObservableCollection of `ConstraintMetricItem(Name, IsEnabled)`), `SelectedItem=SelectedConstraintMetricItem`. `ItemContainerTheme` sets `IsEnabled={ReflectionBinding IsEnabled}`. `ItemTemplate` shows `{Binding Name}`. The currently-selected metric (primary metric) is disabled in the list to prevent self-constraint.

### §8: ConstantMetric always populated
`RebuildConstraintMetricOptions()` auto-selects the first enabled item when switching to ConstantMetric. `OnContourConstraintKindChanged` calls it to ensure constraint metric is never empty/same as primary.

### §9: LabelSpacing NUD max → 1000
`Maximum="10"` → `Maximum="1000"` in PlotInspectorView.axaml.

### §11: Widen colormap/fill selectors
Grid ColumnDefinitions changed from `"60,*"` to `"*,Auto"`. Colormap ComboBox: removed `Width="60"`, added `HorizontalAlignment="Stretch"`. Fill IconSelectButton: added `Width="80"`.

### §12: Merge Grid pts label+toggle
Replaced `TextBlock Text="Grid pts"` + `Button Classes="seg-btn" Text="Show"` with a single `Button Classes="seg-btn" Text="Grid"`. ColumnDefinitions changed from `"50,Auto,Auto,Auto"` to `"Auto,Auto,Auto"` (dropped the label column).

### §16: Narrow constraint box + units TextBlock
Wrapped ConstraintValue NumericUpDown in `StackPanel Orientation="Horizontal" Spacing="2"`. NUD width: 60 → 46. Added `TextBlock Text="{Binding ConstraintUnits}" Classes="label"` beside it.

---

## New VM Members (TraceRowViewModel.cs)

```csharp
public record ConstraintMetricItem(string Name, bool IsEnabled);
public ObservableCollection<ConstraintMetricItem> ConstraintMetricOptions { get; } = new();
[ObservableProperty] private ConstraintMetricItem? _selectedConstraintMetricItem;
partial void OnSelectedConstraintMetricItemChanged(ConstraintMetricItem? value) { ... }
private void RebuildConstraintMetricOptions() { ... }
public string ConstraintUnits => ContourConstraintKind == ConstraintKind.Compression
    ? "dB" : ConstraintMetricUnit(ContourConstraintMetric);
private static string ConstraintMetricUnit(string? metric) => metric switch { ... };
```

---

## Gate Tests Added

**ContourTraceCardTests.cs (T40–T51):**
- T40: `ContourData_Clone_CopiesStyleAndLeavesComputedNull`
- T41: `Trace_CopyCtor_ClonesContourData_NotSameReference`
- T42: `ContourData_Default_ColorMap_IsBone`
- T43: `ContourData_Default_LabelForeground_IsBlack`
- T44: `ContourData_Default_LabelBackground_IsWhite`
- T45: `ContourData_Default_LabelSpacing_Is30`
- T46: `AddContourTrace_SmithPlot_DrawLabels_False`
- T47: `AddContourTrace_RectPlot_DrawLabels_True`
- T48: `AddContourTrace_SecondTrace_InheritsColorMap`
- T49: `TraceRowVm_ConstraintUnits_Compression_IsDb`
- T50: `TraceRowVm_ConstraintUnits_ConstantPout_IsDBm`
- T51: `TraceRowVm_ConstraintUnits_ConstantDE_IsPct`

**LoadpullSurfaceTests.cs:**
- `MaxPower_DifferentKernels_ProduceDifferentInterpolatedPeak`
- `MaxEfficiency_DifferentKernels_ProduceDifferentInterpolatedPeak`

**T17 updated:** `ContourData_NewFields_HaveExpectedDefaults` — assertion for `LabelForeground` updated from White → Black to match §6 change.
