# Sonnet Brief — Phase 7.1d-1 (polish Round 3): custom IconSelectButton + slider width

**Design:** `docs/design/data-display.md` §2.8 / 7.1d-1. **The `Classes="icon-pick"` ComboBox from R2 is
rejected — remove it entirely.** Build a genuine lightweight **custom control** that looks and behaves like a
circuitRF toolbar button with a click-to-open popup of option buttons. Then fix the slider width. Keep
redraw-live behavior. Do not touch `PlotControl`, Properties dock (7.1d-2), MarkerEditor, or DataSet.

## A. The control: `IconSelectButton` (custom, lightweight)
New control `src/Ui/DataDisplay/Controls/IconSelectButton.cs` (+ a `ControlTheme` — inline default style or a
small `Themes/IconSelectButton.axaml` merged into the view's resources, your call). **It is NOT a ComboBox.**

**Look & behavior (the spec — match exactly):**
- **Idle state looks like a regular circuitRF toolbar button**: the control's root visual **is an actual
  `Button`** (`PART_Button`) so it inherits the toolbar button's slightly-rounded corners, grey background, and
  hover for free. It shows the **current selection's glyph** (via `ItemTemplate`) centered.
- **Highlight rule:** when `Highlight=false` (the "Off" selection) → plain button (grey bg,
  `SystemBaseMediumColor` foreground). When `Highlight=true` (any non-Off selection) → **accent highlight, just
  like an active toolbar button**: apply the `Button.ToolActive` idiom from `SchematicView.axaml` — inner
  `/template/ ContentPresenter` Background `SystemAccentColor`, Foreground **White**, `:pointerover`
  `SystemAccentColorLight1`, `:pressed` `SystemAccentColorDark1`. (Drive this off a `:highlight` pseudoclass set
  from the `Highlight` property, or bind directly.)
- **Click → options appear as vertical buttons:** clicking `PART_Button` opens a `Popup` (`PART_Popup`,
  `PlacementMode=Bottom`, `IsLightDismissEnabled=True`) containing a themed rounded `Border` → a vertical list
  of the options, **each rendered as a button the same width as the control**, showing that option's glyph (via
  `ItemTemplate`). (Cleanest: a `ListBox` bound to `ItemsSource` + `ItemTemplate`, `SelectedItem` two-way to the
  control's `SelectedItem`, with `ListBoxItem`s styled as full-width stacked buttons with hover/selected accent;
  or an `ItemsControl` of `Button`s — your choice.)
- **Pick → update + close + return to idle:** selecting an option sets `SelectedItem`, closes the popup, and the
  control returns to its idle button look reflecting the new selection.

**Properties (StyledProperty):**
- `IEnumerable? ItemsSource`
- `object? SelectedItem` (TwoWay by default)
- `IDataTemplate? ItemTemplate` — renders one option's glyph (supplied per call-site)
- `bool Highlight` — drives the accent look

Keep it small and self-contained. The glyph visuals live at the **call sites** (below), not in the control.

## B. VM option lists (TraceRowViewModel) — merge enable + style/shape
- **Line:** `LineModeItem { bool IsOff; LineType Type; }`; `IReadOnlyList<LineModeItem> LineModes = [Off] +
  AllLineTypes`; `LineModeItem? SelectedLineMode` get/set mapping to `LineEnabled`/`LineType` (Off ⇒
  `LineEnabled=false`; else `LineEnabled=true; LineType=Type`). Reuse the existing observable setters so the
  trace redraws.
- **Symbol:** `SymbolModeItem { bool IsOff; MarkerType Shape; }`; `SymbolModes = [Off, Circle, Square]`;
  `SelectedSymbolMode` ↔ `MarkerEnabled` + marker shape.
- **Matrix:** reuse `MatrixType` + `AllMatrixTypes` (no Off).

## C. Use it at the three sites (per-site ItemTemplate)
- **Line** (line row col 0): `ItemsSource=LineModes`, `SelectedItem=SelectedLineMode`,
  `Highlight={Binding LineEnabled}`. ItemTemplate = the line-sample glyph (reuse `LTD` dash converter,
  `Stroke="{Binding $parent[Button].Foreground}"`); the `IsOff` item renders faint (Opacity ~0.35).
- **Symbol** (symbol row col 0): `ItemsSource=SymbolModes`, `SelectedItem=SelectedSymbolMode`,
  `Highlight={Binding MarkerEnabled}`. ItemTemplate = the marker `MaterialIcon`; `IsOff` faint.
- **Matrix** (identity row col 0): `ItemsSource=AllMatrixTypes`, `SelectedItem=MatrixType`,
  `Highlight="True"` (always set — no Off). ItemTemplate = the existing S/Y/Z lettered box.
- Remove the now-dead line-style and marker-shape combos (two fewer combos per card) and the old toggle
  buttons. This is the same densification as R2 — just with the new control.

## D. Slider width — align with the data-source combo above
The slider currently runs too wide. **The slider's right end must align with the right end of the data-source
(signal) combo in the identity row above it.** Make all three trace-card rows share identical columns so they
line up:
- Give the identity row and both line/symbol rows the **same** `ColumnDefinitions="28,*,95,26"`
  (icon-pick **fixed 28** · content `*` · 95 · trailing 26) — fixed cols equal ⇒ the `*` columns are equal ⇒
  things align across rows.
  - **Identity:** matrix `IconSelectButton`(28) · signal combo(`*`) · YAxis(95) · →R(26).
  - **Line:** line `IconSelectButton`(28) · **nested `Grid ColumnDefinitions="30,*"`** = width NUD(30)+slider(`*`)
    in col 1 · color combo(95, col 2) · empty(26). The slider sits in the nested `*`, so its right end == the
    signal combo's right end above. ✓
  - **Symbol:** same as line with size NUD + marker color.
- Drop the slider's wide side `Margin` so the track fills its cell. The colour combos now live under the YAxis
  column (95) and render fully (no clipping). **Verify alignment in the running app** and nudge the fixed widths
  if the chrome needs it.

## E. Plot-type header buttons
Keep the centered segmented plot-type buttons, but make sure they use the **same toolbar idiom** (grey idle /
accent-active via `ToolActive`, `SystemBaseMediumColor` icons) so the whole inspector is consistent. The
Smith/Polar/Rect/Table glyphs themselves are already good — don't change them.

## Guardrails
- Remove all R2 `icon-pick` ComboBox styling. Only `TraceRowViewModel` gains the option lists (§B); the new
  control is the only new file.
- Every edit still redraws live; Off disables the line/symbol (NUD/slider/color dim), a style re-enables + 
  restyles.

## Gate (acceptance)
1. Builds green. The line/symbol/matrix selectors are **buttons** in idle state — visually indistinguishable
   from circuitRF toolbar buttons (rounded corners, grey, `SystemBaseMediumColor` icon); **not** combobox-like.
2. Non-Off selection shows the **accent highlight** (white glyph), exactly like an active toolbar button; Off is
   a plain button.
3. Clicking opens a vertical popup of full-width option buttons; picking one updates the selection, closes the
   popup, and returns to the idle button look — live redraw.
4. Each card has two fewer combos; the **slider's right end aligns with the data-source combo above**; color
   combos render fully under the YAxis column.

## On completion
Note "Phase 7.1d-1 polish R3 — COMPLETE" in `src/Ui/CLAUDE.md`. Report build + a screenshot showing idle
buttons, an open popup, and a highlighted (non-Off) selection. The owner reviews the look before 7.1d-2.
