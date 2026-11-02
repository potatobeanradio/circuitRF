---
name: project-brief-7.4h-1-contour-ux-card-menu
description: Phase 7.4h-1: ContourData new fields + epsilon forwarding + VM observables + AXAML card UX + Import Data menu; 9 gate tests; 2063 total — completed 2026-06-20
metadata:
  type: project
---

Phase 7.4h-1 — Contour UX round 1 — COMPLETE.

4 slices delivered:

**7.4h-1a — model + engine epsilon (§1, §3)**
- `ContourData.cs`: Added 8 new fields — `DisplayMxp`, `DisplayMxe`, `DisplayGridPoints`, `GridPointColor` (SKColors.Black), `LabelForeground` (SKColors.White), `InterpKernel` (Multiquadric), `Smoothing` (1e-3), `Epsilon` (null)
- `ContourFillSelection { None, Topography, Heatmap }` enum added to ContourData.cs
- `DataDisplayConfig.cs` `ContourTraceConfig`: Same 8 new fields (colors serialized as `uint` ARGB; `InterpKernel` with `[JsonStringEnumConverter]`; `Epsilon` as `double?`)
- `DataDisplayViewModel.cs`: Both load and save blocks updated (SKColor ↔ uint ARGB conversion via `new SKColor(uint)` / `(uint)cd.Color`)
- `LoadpullSurface.Fit()`: Added `double? epsilon = null` parameter; `FitKey` record extended with `Epsilon`; forwarded to `new Rbf2D(..., epsilon)` and `new LoadpullFit(..., epsilon)`

**7.4h-1b — VM new observables + commands (§2)**
- `TraceRowViewModel.cs`: Added usings for `SkiaSharp`, `System.Threading.Tasks`, `Avalonia.Controls`, `CircuitRF.Ui.Theming`, `CircuitRF.Ui.Views.Dialogs`
- 9 new `[ObservableProperty]` fields: `_contourDisplayMxp`, `_contourDisplayMxe`, `_contourDisplayGridPoints`, `_contourGridPointColor`, `_contourLabelBackground`, `_contourLabelForeground`, `_contourInterpKernel`, `_contourSmoothing`, `_contourEpsilon`
- Display-only handlers → `_parent.Notify()` (no re-fit)
- Engine-param handlers (`InterpKernel`, `Smoothing`, `Epsilon`) → `RebuildContour()`
- `SelectedContourFill` derived property: getter from ShowFill+SelectedFillKind; setter drives both; `OnPropertyChanged(nameof(SelectedContourFill))` raised from `OnContourShowFillChanged` and `OnContourSelectedFillKindChanged`
- `AllRbfKernels` static list
- `ContourFillOptions` static list
- Commands: `ToggleMxpCommand`, `ToggleMxeCommand`, `ToggleDisplayGridPtsCommand`, `PickGridPointColorCommand`, `PickLabelBgColorCommand`, `PickLabelFgColorCommand`
- Color pick async methods using `ColorPickerDialog` with `Rgba` ↔ `SKColor` conversion
- `RebuildContour()` now forwards `kernel: cd.InterpKernel, smooth: cd.Smoothing, epsilon: cd.Epsilon`
- Constructor initializes all new fields from `cd`

**7.4h-1c — AXAML card edits (§4-7)**
- Identity row `<Grid>` gets `IsVisible="{Binding IsStandardTrace}"` (hidden for contour traces)
- Range NUDs: `Width="60/55/60"` on Start/Step/Stop; constraint value NUD: `Width="60"`
- Show-toggles (Lines, Fill, Labels) and Fill-kind panel REMOVED from main body; moved into Options
- Options StackPanel rebuilt:
  - Labels / Lines toggles
  - Fill ISB (`SelectedContourFill`, None/Topography/Heatmap, items from `ContourFillOptions`)
  - MXP / MXE toggle buttons
  - Grid pts show button + color swatch (Opens `PickGridPointColorCommand`)
  - Label Bg + Label Text color swatches
  - Kernel combo (`AllRbfKernels`)
  - Smooth NUD + Epsilon NUD
  - Label spacing NUD (existing, kept)
  - Colormap combo (existing, kept)
- `SkColorToAvaloniaColorConverter` created in `Converters/` for swatch background bindings
- Converter registered as `{StaticResource SKC}` in `PlotInspectorView.axaml` Resources

**7.4h-1d — Import Data menu (§8)**
- `WorkspaceViewModel.cs`: `[RelayCommand] async Task ImportData(Window? owner)` — multi-select file picker (.spl, .lpcwave, .npy, Touchstone); `AddKnownFile()` per path; refresh all DataDisplay libs
- `WorkspaceWindow.axaml`: NativeMenu and in-window Menu both get "Import Data…" after "Add Library…" with `DatabaseImportOutline` icon

**Gate tests: 9 new (7 UI + 2 RfCore)**
- T13 — ContourTraceConfig new fields round-trip JSON
- T14 — Old .cdd missing new fields loads with defaults (alpha-safe)
- T15 — SelectedContourFill getter/setter drives ShowFill + SelectedFillKind
- T16 — Display toggle (MXP) does not clear ContourGrid (no re-fit)
- T17 — ContourData new fields have expected defaults
- RfCore: `Fit_DifferentEpsilonProducesDistinctCacheEntry`, `Fit_SameEpsilonReturnsCachedInstance`

**Why:** `[[project-brief-7.4e-contour-trace-card]]`
**How to apply:** Follow the same pattern for 7.4h-2 (contour plot renderer enhancements).
