# Sonnet Brief — Phase 7.1d-2 (follow-up): combo shrink-after-floor + live label-strip redraw

**Design:** `docs/design/data-display.md` §2.8 / 7.1d-2. Two independent PlotInspector items from owner feedback:
**(A)** let the color combos + the dB/Mag/Phase combo shrink too, but only *after* the data-source combo and
sliders hit a floor; **(B)** a live-redraw bug — a trace color change repaints the curve but not the y-axis
label strip until a zoom. Files: (A) `Views/DataDisplay/PlotInspectorView.axaml` only; (B)
`DataDisplay/ViewModels/LabelStripViewModel.cs`, `DataDisplay/Controls/AxisLabelControl.cs`,
`Views/DataDisplay/PlotContainerView.axaml`, `DataDisplay/ViewModels/PlotContainerViewModel.cs`.

---

## Part A — fixed-width combos shrink after the `*` columns reach a floor

Today each of the three trace rows uses `ColumnDefinitions="34,*,95,26"`: col0 = matrix/line/symbol
IconSelectButton (34), col1 = `*` (signal combo on the identity row; nested NUD(30)+slider on the line/symbol
rows), col2 = **fixed 95** (dB/Mag/Phase combo on the identity row; color combo on line/symbol), col3 = 26
(→R toggle / spacer). The `*` already shrinks; the 95 columns never do. Goal: the 95 columns hold at 95 while
col1 shrinks to its floor, then shrink (col1 stays floored). Grid star sizing gives this "priority shrink" via a
**heavy-weight star capped by MaxWidth**: the capped column pins at 95 and donates surplus to the light star
(col1); once col1 hits its MinWidth, the capped column releases width and shrinks.

For **all three** rows (identity, line, symbol) replace the inline `ColumnDefinitions="34,*,95,26"` with the
**identical** explicit definitions (identical across rows = columns stay aligned):
```xml
<Grid.ColumnDefinitions>
    <ColumnDefinition Width="34"/>
    <ColumnDefinition Width="*"     MinWidth="54"/>          <!-- signal combo / NUD+slider -->
    <ColumnDefinition Width="1000*" MinWidth="40" MaxWidth="95"/> <!-- dB/Mag/Phase / color -->
    <ColumnDefinition Width="26"/>
</Grid.ColumnDefinitions>
```
Then, per the owner's request, add `MinWidth="20"` to the inner flexible elements:
- identity-row **signal `ComboBox`** (Grid.Column 1): add `MinWidth="20"`.
- line-row and symbol-row **`Slider`** (the nested `Grid.Column 1` slider): add `MinWidth="20"`.

The col2 combos (dB/Mag/Phase + color) are already `HorizontalAlignment="Stretch"`, so they fill col2 and
shrink with it — no change needed there.

**Honest note on the floor:** the data-source combo can't literally floor at 20, because the line/symbol rows
carry a 30 px numeric width box (NUD) beside the slider and all three rows must stay column-aligned — so the
shared `*` floor is ~54 (30 NUD + ~20 slider + margin). The slider itself gets the requested 20 px min. The
numbers **54 / 40 / 95 are in-app tunable** — verify the priority behavior visually and adjust. Also verify the
**wide (430) layout is unchanged** (at max width col2 pins to 95 and col1 takes the rest, exactly as today), the
Freq row (Row 2) and the Z0 / table-format rows are untouched, and nothing clips at the practical narrow width.
If Avalonia's star clamp does **not** yield the pin-then-shrink behavior, say so — the fallback is acceptable
proportional shrink, but flag it rather than leaving it broken.

---

## Part B — trace color/appearance change must repaint the y-axis label strip live

**Root cause (confirmed):** `AxisLabelControl.Render` reads `_trace.Properties.LineColor` directly, and the
control only re-renders when one of its bound props (`Trace`, `PlotTheme`, `CustomLabel`, `ShowFilePrefix`)
changes. A color edit mutates the **same `Trace`** in place → the inspector fires `PlotNeedsRedraw` →
`PlotContainerViewModel` **forwards it only** (repaints the Skia curve) → the strip control is never poked → it
keeps the stale color. Zoom fixes it because `NotifyViewProperties()` → `UpdateLabelStrips(widthAndThemeOnly:true)`
changes `StripWidth`/`Theme`, which raise and trigger `InvalidateVisual`. (This is surface-independent — it just
*appeared* dock-specific because interacting near the flyout incidentally invalidates the strips.) Re-pushing the
**same** `Theme`/`StripWidth` won't help (no change = no notification), so use a revision counter that always
changes.

1. **`LabelStripViewModel.cs`** — add `[ObservableProperty] private int _appearanceRevision;`.
2. **`AxisLabelControl.cs`** — add an `AppearanceRevision` direct property whose setter invalidates:
```csharp
public static readonly DirectProperty<AxisLabelControl, int> AppearanceRevisionProperty =
    AvaloniaProperty.RegisterDirect<AxisLabelControl, int>(
        nameof(AppearanceRevision), o => o.AppearanceRevision, (o, v) => o.AppearanceRevision = v);
private int _appearanceRevision;
public int AppearanceRevision
{
    get => _appearanceRevision;
    set { SetAndRaise(AppearanceRevisionProperty, ref _appearanceRevision, value); InvalidateVisual(); }
}
```
3. **`PlotContainerView.axaml`** — in **both** `AxisLabelControl` templates (left `Grid.Column=0` and right
   `Grid.Column=2`) add the binding: `AppearanceRevision="{Binding AppearanceRevision}"`.
4. **`PlotContainerViewModel.cs`** — in the constructor, change the inspector subscription so each redraw also
   bumps the strips (the `Trace` color/description is already updated by the time `PlotNeedsRedraw` fires, so the
   re-render reads the new value):
```csharp
Inspector.PlotNeedsRedraw += (s, e) =>
{
    PlotNeedsRedraw?.Invoke(this, e);
    foreach (var st in LeftLabelStrips)  st.AppearanceRevision++;
    foreach (var st in RightLabelStrips) st.AppearanceRevision++;
};
```
This re-renders the **existing** strip controls (cheap int + `InvalidateVisual`; no collection rebuild, no
flicker — safe even during slider drags). It fixes color **and** description/format staleness, on both the
flyout and the Properties-dock surface.

**Out of scope (note, don't fix here):** structural strip changes — trace add/remove and the →R axis-move (a
trace moving left↔right doesn't move its strip to the other side until a rebuild) — still rely on the existing
`UpdateLabelStrips()` / `PlotChanged` path. If the →R strip-side staleness is also visible, that's a separate
small follow-up (have `OnTraceSecondaryAxisChanged` trigger a strip rebuild), not part of this fix.

---

## Gate (verify in the running app)
1. **Part A:** narrowing the Properties dock shrinks the signal combo + sliders first; once they reach their
   floor the dB/Mag/Phase and color combos begin to shrink (and stop at their min). All three rows stay
   column-aligned at every width; the 430-wide layout is unchanged.
2. **Part B:** change a trace's color on a Smith chart from the **Properties-dock** inspector → the left y-axis
   label recolors **instantly**, no zoom needed; same from the flyout; changing the signal/format updates the
   strip text live too. No flicker when dragging the line-width/marker-size sliders.
3. Builds green; dual-surface sync from 7.1d-2 still works; no regression to Rect/Smith/Polar/Table.

## On completion
Note both follow-ups under 7.1d-2 in `src/Ui/CLAUDE.md`; screenshot the narrowed inspector (showing the color
combo shrunk) and a Smith chart whose left label recolored live. Next: **7.1d-3** (marker editor polish).
