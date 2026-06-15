# Sonnet Brief — Phase 7.1d-1 (polish Round 4): IconSelectButton cleanups

**Design:** `docs/design/data-display.md` §2.8 / 7.1d-1. Owner review of the landed `IconSelectButton` — six
small fixes in `PlotInspectorView.axaml`, `Controls/IconSelectButton.cs`, and `App.axaml`. Keep redraw-live
behavior. The control's look/behavior is otherwise approved — don't redesign it.

## 1. BUG — line-row glyphs don't render (matrix & symbol are fine)
**Root cause:** the line sample's stroke comes from `Stroke="{DynamicResource SystemBaseMediumColor}"`
(the `Button.seg-btn Canvas Line` style). `SystemBaseMediumColor` is a **Color** key; assigning a Color to the
`IBrush` `Stroke` property **silently fails** (the documented gotcha in App.axaml — it bites `BorderBrush`/
`Stroke`, not `Foreground`, which is why the MaterialIcon/TextBlock glyphs render and the `Line` doesn't).
**Fix:**
- Add an app-level brush in `App.axaml` `Application.Resources` so it resolves everywhere incl. popups:
  `<SolidColorBrush x:Key="CrfIconBrush" Color="{DynamicResource SystemBaseMediumColor}"/>`.
- In the line-sample `DataTemplate`, set the `Line` stroke **directly**:
  `<Line ... Stroke="{DynamicResource CrfIconBrush}"/>` (don't rely on the descendant style — the popup is a
  separate visual tree the inspector's `UserControl.Styles` don't reach).
- Remove the now-defunct `Button.seg-btn Canvas Line` (default + active) Color-stroke styles.
Verify the line glyph renders in **both** the idle button and the open popup, for Solid/Dashed/Off.

## 2. Make selection-highlight optional; turn it OFF for the three trace-card uses
Add a `HighlightSelected` styled property to `IconSelectButton` (`bool`, **default true**, so reuse elsewhere
keeps highlighting). It gates **both** highlight surfaces:
- **Button accent:** in `ApplyHighlight()`, add the `"active"` class only when `Highlight && HighlightSelected`.
- **Popup selected-row accent:** when `HighlightSelected` is false, add a class (e.g. `"flat-select"`) to
  `PART_ListBox`; in the ControlTheme add a style so `ListBox.flat-select ListBoxItem:selected` keeps a
  transparent background (hover stays). Recompute on property change + in `OnApplyTemplate`.
Then set **`HighlightSelected="False"`** on all three trace-card `IconSelectButton`s (Matrix, Line, Symbol).
State is conveyed by the glyph (faint Off vs solid/shape On), not an accent.

## 3. Right padding after each of the three IconSelectButtons
They're cramped against the next column. Widen the shared **col 0 from `28` → `34`** in all three rows
(identity / line / symbol — keep them identical for alignment) and keep the button left with a ~6 px right gap
(`Margin="0,0,6,0"`, left-aligned). Verify the three buttons still line up vertically and have clear space to
their right.

## 4. Tighten the line/symbol row height (slider is inflating it)
The global `Slider` style uses `Height="35"` + a `TranslateTransform Y="-7.5"` hack, which makes the line/
symbol rows visibly taller than the identity/Z0 rows. Reduce the `Slider` `Height` to ~`18–20`, **remove the
`TranslateTransform`**, set `VerticalAlignment="Center"`. Verify the thumb/track aren't clipped and that the
line row, symbol row, identity row, and Z0 row all read with the **same height and uniform gaps** (the
trace-body `StackPanel Spacing` may need a small nudge). Tune in the running app.

## 5. Give the trace card a defined outline
`Border.traceCard` reads flat against the panel. Add a subtle rounded outline and keep the fill:
`BorderBrush="{DynamicResource CrfTileBorderBrush}"` (existing app brush), `BorderThickness="1"`,
`CornerRadius="6"` (keep `Background` `SystemChromeLowColor`, or nudge if it helps it read as a card). Each
trace should look like a distinct rounded card.

## 6. Relocate the remove (×) button
It looks orphaned at the far right of the Z0 row. Move it to the **card's top-right corner** as a small,
subtle **trash icon** (`TrashCanOutline`, ~14 px, transparent background, no border, `CrfIconBrush` foreground,
red on `:pointerover`):
- Make the `traceCard` Border's child an outer `Grid ColumnDefinitions="*,Auto"`: the existing content
  `StackPanel` in col 0; the remove `Button` in col 1, `VerticalAlignment="Top"` (aligned with the identity
  row). This **reserves** a corner column (no overlap with the →R button).
- Remove the old `×` button from the Z0 row (drop its trailing column).
Keep `RemoveCommand` + the tooltip.

## Guardrails
- Only `IconSelectButton.cs` (the new property + class toggle), `PlotInspectorView.axaml`, and `App.axaml` (the
  one brush) change. No VM changes, no `PlotControl`, no Properties dock (7.1d-2).
- Every edit still redraws live.

## Gate (acceptance)
1. Builds green. Line glyphs render in the idle button and the popup (Solid/Dashed/Off).
2. None of the three trace-card selectors show an accent highlight (button or popup); `HighlightSelected`
   defaults true for future reuse.
3. Clear space to the right of each `IconSelectButton`; the three stay vertically aligned.
4. Line/symbol rows are the same height as the identity/Z0 rows with uniform spacing.
5. Each trace card has a subtle rounded outline; the remove control is a trash icon in the card's top-right.

## On completion
Note "Phase 7.1d-1 polish R4 — COMPLETE" in `src/Ui/CLAUDE.md`. Report build + a screenshot. After owner
sign-off on the look, next is **7.1d-2** (Properties-dock surface).
