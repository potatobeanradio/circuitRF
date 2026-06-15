# Sonnet Brief — Phase 7.1d-1 (polish): inspector visual fixes from owner review

**Design:** `docs/design/data-display.md` §2.8 / 7.1d-1. **Scope: targeted visual fixes to
`PlotInspectorView.axaml` + one new glyph control, per owner review of the landed 7.1d-1.** Keep ALL bindings/
commands/behavior. This is tuning — several items need **visual verification in the running app** (combo
chrome, row alignment); iterate until they look right. Working items to NOT regress: centered plot-type
header, Rect/Table icons, MatrixType S/Y/Z boxes, the Line/Symbol toggle-button mechanism, the tight
dB/Mag/Phase combo.

## 1. Make EVERY combo as thin (short) as the dB/Mag/Phase one
The only tight combo is the one carrying `Classes="compact"`; the rest use the Fluent default height and read
too tall. **Add a base `ComboBox` style** (no class, hits all combos) in `UserControl.Styles` with the compact
metrics, so all combos match:
```xml
<Style Selector="ComboBox">
    <Setter Property="FontSize"  Value="10"/>
    <Setter Property="MinHeight" Value="0"/>
    <Setter Property="Padding"   Value="4,1"/>
    <Setter Property="VerticalContentAlignment" Value="Center"/>
</Style>
```
If the Fluent template still imposes height, also set an explicit `Height` (~22) on this base style. Drop the
now-redundant `.compact` class (or keep it as a no-op). **Verify** Freq, signal, the three colour combos,
MarkerType, Highlight all become as short as the dB/Mag/Phase combo.

## 2. Fix the clipped MatrixType / LineType / MarkerType combos
Their left/right edges (rounded box + dropdown chevron) are cut off — the fixed `Width` (38/44/40) is too
small to hold `[pad][glyph][chevron][pad]`. **Widen** each until the full chrome renders (start ~MatrixType 52,
LineType 58, MarkerType 50) **or** shrink the dropdown chevron in the compact template. Verify in-app that the
box and glyph render fully with no horizontal clipping. (The parent grids already have `ClipToBounds="False"`.)

## 3. Line / Symbol toggle glyphs → dynamic + smaller
Make each enable-toggle **preview the current selection**, and shrink the glyph:
- **Line toggle** (col 0 of the line row): replace the static `VectorPolyline` with a small drawn line sample
  reflecting `LineType` — a `Canvas`/`Line` (~14×8) with `StrokeDashArray="{Binding LineType,
  Converter={StaticResource LTD}}"`, `Stroke="{Binding $parent[ToggleButton].Foreground}"`. So the button shows
  solid/dashed/dotted as selected.
- **Symbol toggle** (col 0 of the symbol row): replace the static `ChartScatterPlot` with
  `<mi:MaterialIcon Kind="{Binding SelectedMarkerTypeItem.Icon}" Width="10" Height="10"/>` so it shows the
  selected marker shape.
- Keep `IsChecked` bound to `LineEnabled` / `MarkerEnabled` (the glyph reflects selection regardless of
  enabled state). Reduce icon sizes overall so they sit cleanly in the 22×22 button.

## 4. Smith & Polar plot-type glyphs → new owner-tweakable control
The Material `ChartArc` (Smith) / `ChartDonut` (Polar) aren't right. **Create
`src/Ui/DataDisplay/Controls/PlotTypeGlyphControl.cs`** — an Avalonia `Control` that draws a simplified grid;
**all glyph geometry lives in this one file for the owner to tweak.**
- Styled properties: `Kind` (enum `Smith`/`Polar`, `AffectsRender`) and `Stroke` (`IBrush?`, `AffectsRender`,
  default to `SystemBaseHighColor` if null).
- `Render(DrawingContext)`: map unit coords `(u,v)∈[-1,1]` to the control rect (small inset). `Pen` from
  `Stroke`, thickness ~1.
  - **Polar:** 2 concentric circles (r=1.0, r=0.5) + horizontal axis (−1,0)→(1,0) + vertical axis (0,−1)→(0,1).
  - **Smith (reduced inner lines, no text):** push a unit-circle clip, then draw: outer circle (r=1, centre 0,0);
    real axis (−1,0)→(1,0); one constant-R circle R=1 → centre (0.5,0) r=0.5; two constant-X arcs X=±1 →
    circles centre (1,±1) r=1 (the unit clip leaves just the two symmetric arcs). Pop clip. (Math mirrors
    `AxesRenderer.DrawSmithGrid`: R-circle radius `1/(1+R)`, centre `(1−radius,0)`; X-circle centre `(1,±1/X)`
    radius `1/X`. Keep it sparse — do **not** port the full circle set.)
- In `PlotInspectorView.axaml` add `xmlns:ctl="using:CircuitRF.Ui.DataDisplay.Controls"` and replace the Smith
  and Polar `MaterialIcon`s in the header with
  `<ctl:PlotTypeGlyphControl Kind="Smith"/Polar" Width="16" Height="16"
   Stroke="{Binding $parent[Button].Foreground}"/>` (so `.active` recolors them). Keep Rect=`ChartLine`,
  Table=`TableLarge` as-is.

## 5. Align the Line and Symbol rows (and the two sliders)
The rows drift because their colour/glyph columns are `Auto` (different natural widths) so the `*` slider
column differs — the symbol slider collapses to a sliver. **Give both rows identical fixed columns** so every
item in the line row sits above its symbol-row counterpart:
- Set BOTH grids to e.g. `ColumnDefinitions="Auto,30,*,46,52"` (toggle · width/size NUD · slider · colour · 
  style/marker), with the **colour combo `Width="46"`** and the **style/marker combo `Width="52"`** in **both**
  rows (drop the `HorizontalAlignment="Right"` so they fill their fixed columns). Now col 2 (`*`) is equal →
  the two sliders are the **same length** and aligned.
- Reduce the global `Slider` side `Margin` (currently `10,0,10,0`) to ~`4,0` so the track uses the column width
  and both rows match.
- The colour combos for line and symbol now align horizontally; verify the size/width NUDs, sliders, colour
  combos, and style/marker combos line up column-for-column between the two rows.

## Guardrails
- No VM changes needed (all bindings exist: `LineType`, `SelectedMarkerTypeItem.Icon`, `LineEnabled`,
  `MarkerEnabled`, colour indices). Only the new `PlotTypeGlyphControl` is added.
- Don't touch `PlotControl`, the Properties dock (7.1d-2), or MarkerEditor (7.1d-3).

## Gate (acceptance)
1. Builds green. Every combo is as short as the dB/Mag/Phase combo; MatrixType/LineType/MarkerType render with
   no left/right clipping.
2. Line/Symbol toggles show the **current** line style / marker shape and are smaller.
3. Smith & Polar header buttons show recognizable custom grid glyphs (recolor when active); geometry lives in
   `PlotTypeGlyphControl.cs`.
4. The line and symbol rows align column-for-column; both sliders are the same length. Every edit still redraws
   live.

## On completion
Note "Phase 7.1d-1 polish — COMPLETE" in `src/Ui/CLAUDE.md`. Report build + a fresh screenshot for owner
review. The owner will hand-tweak `PlotTypeGlyphControl.cs` after.
