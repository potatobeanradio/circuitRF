# Sonnet Brief — Phase 7.1d-3a: restyle MarkerEditorView to the inspector idiom

**Design:** `docs/design/data-display.md` §2.8 / 7.1d-3. Restyle the marker editor flyout
(`Views/DataDisplay/MarkerEditorView.axaml`) so it reads as a sibling of the now-approved
`PlotInspectorView` / `AnalysisEditorDialog`. **Visual restyle only — no VM logic changes, no behavior
changes** (frequency commit-on-Enter, gating flags, bindings all stay). Marker-undo work is a **separate**
brief (7.1d-3b). Files: `Views/DataDisplay/MarkerEditorView.axaml` (+ `.axaml.cs` only if needed).

## Idiom reference (match these, already in the codebase)
- **Approved:** `Views/DataDisplay/PlotInspectorView.axaml` — outer card
  `Border Background={DynamicResource SystemChromeMediumLowColor} CornerRadius=8 Padding=10`; opacity-tiered
  labels `TextBlock.label` (FontSize 10, Opacity 0.6, VCenter); compact `ComboBox` base style (FontSize 10,
  Height 22, MinHeight 0, Padding 4,1); segmented toggle `Button.seg-btn` with `.active` accent applied on
  `/template/ ContentPresenter` (accent bg + white foreground, Light1/Dark1 hover/press); `CrfWarningBrush`
  for errors. Don't hardcode a font — inherit the app/dialog default (same as the inspector).
- Don't modify `PlotInspectorView.axaml` (owner-approved). **Copy** the handful of styles it needs into this
  view's `<UserControl.Styles>`: `TextBlock.label`, the base compact `ComboBox` style, the compact
  `NumericUpDown` style, and the `seg-btn` family (idle + `.active`/`:checked` accent on
  `/template/ ContentPresenter`, plus the grey-icon rule). (A shared StyleInclude dictionary used by both
  views is the DRY option, but only if it leaves the inspector pixel-identical — otherwise duplicate; do
  **not** risk the approved inspector.)

## Current view (what to restyle)
`MarkerEditorView.axaml` is a `UserControl Width=240 Padding=12` → `StackPanel Spacing=8` of raw controls:
Name `TextBox`; Frequency `TextBox` (commit-on-Enter via `OnFreqTextBoxKeyDown`); read-only
`SelectableTextBlock` data/Z0 lines + a `MultiLines` `ItemsControl`; a Format/Precision/Digits row
(`ComboBox`/`ComboBox`/`NumericUpDown`); a Normalize-Impedance `CheckBox` + Display-Size `ComboBox` row; and
a Rect-only Multi-marker / Δ Delta `CheckBox` pair. VM bindings (keep all): `Name`, `FreqDisplayText` +
`FreqUnitLabel`, `OwnDataLine`/`OwnZ0Line`/`HasMultiLines`/`MultiLines`, `MatrixFormat`+`ShowFormatSelector`,
`FormatString`, `Digits`, `Style`, `UseNormalized`, `IsMulti`/`IsDelta`/`ShowMultiDeltaControls`.

## Restyle (control-by-control — preserve every binding)
1. **Outer card.** Wrap the content in the inspector's outer card (`SystemChromeMediumLowColor`, CornerRadius
   8, Padding 10). Keep a compact fixed flyout width (~250; the marker editor is flyout-only, not docked).
   `Spacing=8` between sections.
2. **Labels.** Every field caption uses `Classes="label"` (drop the inline `FontSize=11 Opacity=0.6`).
3. **Name / Frequency.** Compact `TextBox`es (Height ~22, FontSize 10–11) under `label` captions. Keep
   `x:Name="FreqTextBox"` + `KeyDown="OnFreqTextBoxKeyDown"` and the `FreqUnitLabel` StringFormat caption.
4. **Readout block (OwnDataLine / OwnZ0Line / MultiLines).** Keep the `SelectableTextBlock`s and the
   `MultiLines` `ItemsControl`, but place them in a subtle rounded readout row — a
   `Border Background={DynamicResource SystemChromeLowColor} BorderBrush={DynamicResource CrfTileBorderBrush}
   BorderThickness=1 CornerRadius=6 Padding=8,6` (the `traceCard` look) — FontSize 11, secondary lines ~0.55
   opacity. This visually separates the live data readout from the editable fields.
5. **Format / Precision / Digits.** Keep the one-row grid; restyle the two `ComboBox`es with the compact base
   style and the `NumericUpDown` with the compact style. Keep `ShowFormatSelector` collapsing the Format
   column + its spacer on Rect.
6. **Display Size + Normalize Impedance.** Replace the Normalize `CheckBox` with a `seg-btn`-styled
   `ToggleButton` (`IsChecked="{Binding UseNormalized}"`, label "Norm Z" or an icon) — `.active`/`:checked`
   accent matches the inspector and fixes checkbox text-width misalignment. Display Size: keep as a compact
   `ComboBox` bound to `Style` (a segmented S/M/L toggle is optional polish but needs VM per-value flags —
   **out of scope**; combo is fine).
7. **Multi-marker / Δ Delta (Rect-only).** Replace the two `CheckBox`es with two `seg-btn`-styled
   `ToggleButton`s (`IsChecked` two-way to `IsMulti` / `IsDelta`; labels "Multi" and "Δ"). Keep the group
   gated by `ShowMultiDeltaControls`. (`OnIsMultiChanged` already triggers the plot redraw — unchanged.)

## Gate (verify in the running app)
1. The marker editor flyout reads as a sibling of the plot inspector (outer card, opacity-tiered labels,
   compact combos, segmented `.active`/`:checked` toggles, rounded readout row); inherits the app font.
2. Every field still edits the marker live and correctly: Name, Frequency (Enter commits + snaps), Format
   (Smith/Polar only), Precision, Digits, Display Size, Normalize, Multi/Δ (Rect only); the data readout +
   multi-trace rows update on change; `ShowFormatSelector`/`ShowMultiDeltaControls` gating intact.
3. Design-time preview (`MarkerEditorViewModel.DesignInstance`) renders. Builds green; no VM changes.

## On completion
Note the restyle under 7.1d-3 in `src/Ui/CLAUDE.md`; screenshot the restyled flyout next to the plot
inspector. The marker-undo tidy is the companion brief (7.1d-3b).
