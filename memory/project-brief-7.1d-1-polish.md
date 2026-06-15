---
name: project-brief-7.1d-1-polish
description: Brief 7.1d-1 polish R1+R2+R3+R4 — inspector colors, IconSelectButton, column alignment, cleanups — completed 2026-06-14
metadata:
  type: project
---

Phase 7.1d-1 polish R1 — COMPLETE 2026-06-14.
Five visual polish items applied to `PlotInspectorView.axaml` and new `PlotTypeGlyphControl.cs`:
1. Base `ComboBox` style (Selector="ComboBox") makes all combos uniformly compact (Height 22, FontSize 10, Padding 4,1). `ComboBox.compact` kept as no-op.
2. MatrixType/LineType/MarkerType combos widened to 52 to eliminate left/right clipping.
3. Line toggle previews current dash pattern; Symbol toggle shows selected marker icon.
4. New `PlotTypeGlyphControl.cs` in Controls/ — Avalonia Control (not SkiaSharp) drawing Smith/Polar grid glyphs.
5. Both Line and Symbol rows use `ColumnDefinitions="Auto,30,*,46,52"` for column-for-column alignment.

Phase 7.1d-1 polish R2 — COMPLETE 2026-06-14.
(A) Inspector toolbar-matched colors: `Button.seg-btn` → default icons `SystemBaseMediumColor` via targeted `mi|MaterialIcon` + `ctl|PlotTypeGlyphControl` styles; active state via `/template/ ContentPresenter` (Background=SystemAccentColor, Foreground=White, hover=Light1, pressed=Dark1); white icon overrides on `.seg-btn.active`.
(B) `ComboBox.icon-pick` style: Width=28, Padding=2, no chevron (`/template/ PathIcon` + `/template/ Path` IsVisible=False), grey/transparent background; popup items centered.
(C) VM additions: `LineModeItem`/`SymbolModeItem` in ComboItems.cs; `PlotInspectorViewModel.LineModes` + `SymbolModes` static lists; `TraceRowViewModel.SelectedLineMode`/`SelectedSymbolMode` computed properties with cross-notifications.
(D) Trace card: line + symbol rows → `ColumnDefinitions="Auto,30,*,Auto"` (4 cols); separate enable toggles + style/shape combos removed (2 fewer combos per card); MatrixType uses icon-pick Width=30.
(E) Color combos Width=52 with 34px swatch border.

Build 0W/0E; 1206 tests pass. icon-pick used ComboBox restyle (`/template/ PathIcon` selector for chevron).

Phase 7.1d-1 polish R3 — COMPLETE 2026-06-14.
R2 `ComboBox.icon-pick` approach rejected by owner; replaced with genuine `IconSelectButton` custom `TemplatedControl`.
(A) New `src/Ui/DataDisplay/Controls/IconSelectButton.cs` — StyledProperties: `ItemsSource`, `SelectedItem` (TwoWay), `ItemTemplate`, `Highlight`. `OnApplyTemplate` finds PART_Button/PART_Popup/PART_ListBox; highlight adds/removes `active` class on PART_Button to reuse existing `seg-btn`/`seg-btn.active` styles.
(B) ControlTheme inline in `PlotInspectorView.axaml` `UserControl.Resources` — `Popup.Placement="Bottom"` (not PlacementMode; Avalonia 12 API), `IsLightDismissEnabled=True`, ListBox with custom ListBoxItem ControlTheme.
(C) Added `Button.seg-btn Canvas Line` (grey) + `Button.seg-btn.active Canvas Line` (White) stroke styles.
(D) All three trace rows share `ColumnDefinitions="28,*,95,26"` — slider right edge aligns with signal combo; color combos stretch to 95px column.

Build 0W/0E; tests pass.

**Why:** R2 icon-pick was a ComboBox restyle (chevron hidden via selectors); R3 is a genuine lightweight button control as requested.
**How to apply:** VM unchanged from R2; only the control class + AXAML needed updating.

Phase 7.1d-1 polish R4 — COMPLETE 2026-06-14.
Six owner-review fixes to `IconSelectButton.cs`, `PlotInspectorView.axaml`, `App.axaml`:
1. **Line-glyph bug**: `CrfIconBrush` (SolidColorBrush wrapping SystemBaseMediumColor) added to `App.axaml` so it resolves in popup visual trees. `Line.Stroke` in DataTemplate set directly to `{DynamicResource CrfIconBrush}`. Removed defunct `Button.seg-btn Canvas Line` styles (Color ≠ IBrush; silently failed).
2. **HighlightSelected**: new styled property (bool, default true) on `IconSelectButton`. `ApplyHighlight()` gates `active` class on `Highlight && HighlightSelected`. `ApplyHighlightSelected()` adds/removes `flat-select` class on PART_ListBox. Popup Border carries `<Style Selector="ListBox.flat-select ListBoxItem:selected">` (transparent bg, hover stays). All three trace-card ISBs set `HighlightSelected="False"`.
3. **Right padding**: col 0 `28→34` in all three rows; ISB `Margin="0,0,6,0"`.
4. **Slider height**: `Height 35→20`, `TranslateTransform Y="-7.5"` removed from both sliders; rows now uniform height.
5. **Card outline**: `Border.traceCard` style adds `BorderBrush=CrfTileBorderBrush`, `BorderThickness=1`, `CornerRadius 4→6`.
6. **Trash button**: traceCard child is now `Grid ColumnDefinitions="*,Auto"` — content StackPanel in col 0, `Button.removeTrace` (TrashCanOutline 14px, transparent, `CrfIconBrush` fg, red pointerover) in col 1 `VerticalAlignment=Top`; old `×` button removed from Z0 row.

Build 0W/0E; 1206 tests pass.
