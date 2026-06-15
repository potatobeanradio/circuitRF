# Sonnet Brief — Phase 7.1d-1 (polish Round 2): toolbar-matched colors + merged icon-select

**Design:** `docs/design/data-display.md` §2.8 / 7.1d-1. **Scope: (A) make all Plot Inspector icon buttons
match the circuitRF toolbar look, and (B) introduce ONE reusable compact icon-select element that merges
on/off + style into a tiny combobox-like button, reused for Line, Symbol, and Matrix-Type — removing two
combos per trace card.** Keep all redraw-live behavior. Working items to NOT regress: Smith/Polar glyphs,
line/symbol toggle glyphs, thin combos, MatrixType S/Y/Z letters, plot-type header layout.

## A. Match the toolbar icon idiom (colors)
Reference (copy the pattern): `Views/Content/SchematicView.axaml` `Button.ToolActive` styles + App.axaml
`StackPanel.Toolbar mi|MaterialIcon`. The rules:
- **Default icon foreground = `SystemBaseMediumColor`** (toolbar grey), not `SystemBaseHighColor`. Add a style
  so MaterialIcons (and the custom glyphs) inside inspector buttons use it.
- **Default background = transparent** (buttons read against the panel like toolbar buttons). Keep the
  segmented plot-type group's subtle look, but drop the filled/`SystemBaseHigh` active treatment.
- **Active / selected = accent**, applied on the **inner `/template/ ContentPresenter` Background** (the Fluent
  theme sets hover/pressed there, so targeting `Button.Background` won't stick):
  - `ContentPresenter` Background `SystemAccentColor`, Foreground **White**;
  - `:pointerover` → `SystemAccentColorLight1`; `:pressed` → `SystemAccentColorDark1`.
  Mirror `Button.ToolActive` exactly. Apply this to the plot-type `seg-btn.active`, and to the selected/edit
  state of the new icon-select (§B), so the whole inspector matches the Schematic/Symbol toolbars.
- The custom glyphs already bind `Stroke="{Binding $parent[Button].Foreground}"`, so White-on-active works
  once Foreground flips to White. Verify Smith/Polar/line-sample glyphs turn white when active.

(If the owner later wants the literal toolbar-grey *behind* the buttons, the header/rows can be backed with
`SystemChromeLowColor`; default to transparent for now.)

## B. New reusable element: compact icon-select (`icon-pick`)
A tiny combobox-like picker: shows the current option's glyph at button width; on click a small popup lists the
options (each glyph row the same width as the button); selecting one sets the value and closes. **Preferred
implementation: restyle `ComboBox` via `Classes="icon-pick"`** (reuses ComboBox popup/selection/keyboard):
- Hide the dropdown chevron (the Fluent ComboBox template part — confirm its name in the Avalonia 12 Fluent
  ComboBox theme, e.g. a `PathIcon`/`Path` named `DropDownGlyph`; set `IsVisible=False` or width 0 via
  `Selector="ComboBox.icon-pick /template/ …"`).
- Button-sized: `Width` ~28, minimal `Padding`, the tight height from the base `ComboBox` style; grey/
  transparent background + `SystemBaseMediumColor` foreground (§A); **selected state uses the accent idiom**.
- Popup `ComboBoxItem`s: small padding, **stretch to the control width**, each rendering the item's glyph
  centered; selected/hover item uses the accent highlight.
- This **fixes the earlier chevron-driven clipping** (no chevron) and frees space in the row.

If ComboBox template-part styling proves too fiddly to get clean, fall back to a minimal custom control
(`ToggleButton` + `Popup` of glyph buttons) — but try the ComboBox restyle first.

## C. VM: merge on/off + style into option lists (TraceRowViewModel)
Add presentation option-lists that fold the enable toggle into the style/shape choice:
- **Line:** a small `LineModeItem { bool IsOff; LineType Type; }`. `IReadOnlyList<LineModeItem> LineModes` =
  `[Off] + AllLineTypes`. `LineModeItem? SelectedLineMode` (get/set):
  - get → the `IsOff` item when `!LineEnabled`, else the item whose `Type == LineType`.
  - set → `IsOff` ⇒ `LineEnabled = false`; else `LineEnabled = true; LineType = value.Type`. (Reuse the existing
    `LineEnabled`/`LineType` observable setters so the trace updates + redraws; raise `OnPropertyChanged` for
    `SelectedLineMode`.)
- **Symbol:** `SymbolModeItem { bool IsOff; MarkerType Shape; }`. `SymbolModes = [Off, Circle, Square]`.
  `SelectedSymbolMode` get/set maps to `MarkerEnabled` + the marker shape (drive the existing
  `SelectedMarkerTypeItem`/`MarkerType` path). Off ⇒ `MarkerEnabled=false`.
- **Matrix:** reuse the existing `MatrixType` + `AllMatrixTypes` (no Off) — just rendered through `icon-pick`.
- The width/size NUDs, sliders, and color combos keep `IsEnabled="{Binding LineEnabled}"` /
  `{Binding MarkerEnabled}` (still valid — the merged setter drives those).

## D. Rewire the trace card (remove 2 combos, denser)
- **Line row** → `[icon-pick LineModes] [width NUD] [slider] [color combo]`. Remove the separate line on/off
  toggle (col 0) **and** the line-style combo (col 4); the `icon-pick` in col 0 now carries both. Item template
  = the line-sample glyph (reuse `LTD` dash converter); the `Off` item = a faint/low-opacity line (owner can
  tweak).
- **Symbol row** → `[icon-pick SymbolModes] [size NUD] [slider] [color combo]`. Remove the symbol toggle +
  marker-shape combo. Item template = the marker `MaterialIcon` (`Icon` of the shape); `Off` = faint marker.
- **Matrix** (identity row col 0): swap the lettered-box ComboBox for the same `icon-pick` (item template =
  the S/Y/Z lettered box you already have).
- Keep both rows' columns **identical and fixed** so they stay aligned (Round-1 rule): e.g.
  `ColumnDefinitions="Auto,30,*,Auto"` with the color combo a fixed width in both rows.

## E. Color combo renders fully
With the style/marker combos gone, the color combo has room — give it adequate width and the base ComboBox
height; **verify its left/right outline is no longer clipped** in the running app.

## Guardrails
- Only the additions in §C touch VMs. No `PlotControl`, Properties dock (7.1d-2), MarkerEditor (7.1d-3), or
  DataSet (7.2) changes.
- Every edit still redraws live; Off/Solid/Dash and Off/Circle/Square correctly enable/disable + restyle.

## Gate (acceptance)
1. Builds green. All inspector icon buttons match the toolbar: grey (`SystemBaseMediumColor`) icons, accent
   selection with White icon (hover Light1 / pressed Dark1).
2. The `icon-pick` element works for Line (Off/Solid/Dash), Symbol (Off/Circle/Square), and Matrix (S/Y/Z):
   compact button → popup of full-width glyph rows → selection applies + closes; no chevron clipping.
3. Each trace card now has **two fewer combos**; line/symbol rows are `[icon-pick][NUD][slider][color]`, aligned
   column-for-column; the color combo outline renders fully.
4. Selecting Off disables the line/symbol (NUD/slider/color dim); selecting a style re-enables + restyles, live.

## On completion
Note "Phase 7.1d-1 polish R2 — COMPLETE" in `src/Ui/CLAUDE.md`. Report build + a screenshot. If the `icon-pick`
needed the custom-control fallback, say so. Next (after owner sign-off on the look): **7.1d-2** (Properties dock).
