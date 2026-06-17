# Sonnet Brief — PlotProperties / PlotInspector: 6 changes

Six independent changes to the Data Display plot UI. Files are under `src/Ui/DataDisplay`. Build 0W/0E
(TreatWarningsAsErrors) after each part. The "PlotProperties" panel the user refers to IS the
**PlotInspector** (`PlotInspectorView.axaml` + `PlotInspectorViewModel` + `TraceRowViewModel`), shown
from the plot's context-menu flyout and the Properties pane.

Parts: A = color comboboxes → IconSelectButton; B = live aspect ratio on plot-type change; C = trace
card doesn't refresh HB→S-param; D = HB voltage/current net-name notation; E = drop ..Converged/..Residual
from the data-source combo; F = Table width tracks trace count.

---

## PART A — Replace color ComboBoxes with IconSelectButton

In `PlotInspectorView.axaml` the trace card has color **ComboBox**es bound to
`vm:PlotInspectorViewModel.ColorItems` with `SelectedIndex="{Binding LineColorIndex}"` and
`SelectedIndex="{Binding MarkerColorIndex}"`. Replace EVERY color ComboBox in this view with a
`ctl:IconSelectButton` that shows a rounded color swatch (looks like a button at idle) and **no
selection highlight**.

> The user expects **3** color comboboxes total. Read `PlotInspectorView.axaml` and convert ALL of them
> (the two I can see are Line color and Marker color in the line/symbol rows; if a third exists — e.g. a
> Table or fill color — convert it the same way). Report how many you found and converted.

### A1. `IconSelectButton` uses `SelectedItem` (an object), not `SelectedIndex`
The existing color combos bind an `int` index (`LineColorIndex`/`MarkerColorIndex`). `IconSelectButton`
exposes `SelectedItem`. Add `ColorItem`-typed wrapper properties to `TraceRowViewModel` that bridge
index ↔ item, leaving the existing `int` properties and their `_trace.Properties.*ColorIndex` writes
intact:
```csharp
// In TraceRowViewModel, next to LineColorIndex:
public ColorItem? SelectedLineColor
{
    get => PlotInspectorViewModel.ColorItems.FirstOrDefault(c => c.Index == LineColorIndex)
           ?? PlotInspectorViewModel.ColorItems[0];
    set { if (value is not null && value.Index != LineColorIndex) LineColorIndex = value.Index; }
}
// raise change notification when the index changes so the swatch updates:
partial void OnLineColorIndexChanged(int value)   // EXISTING method — add the OnPropertyChanged line
{
    _trace.Properties.LineColorStorage = null;
    _trace.Properties.LineColorIndex   = value;
    _parent.Notify();
    OnPropertyChanged(nameof(SelectedLineColor));   // ← add
}
```
Do the same for `SelectedMarkerColor` ↔ `MarkerColorIndex` (add `OnPropertyChanged(nameof(SelectedMarkerColor))`
to the existing `OnMarkerColorIndexChanged`). If a third color exists, add the matching wrapper.

### A2. The control in XAML
Replace each color `<ComboBox …>` block with:
```xml
<ctl:IconSelectButton Grid.Column="2"
                      HorizontalAlignment="Stretch"
                      ItemsSource="{x:Static vm:PlotInspectorViewModel.ColorItems}"
                      SelectedItem="{Binding SelectedLineColor, Mode=TwoWay}"
                      IsEnabled="{Binding LineEnabled}"
                      Highlight="False"
                      HighlightSelected="False"
                      ToolTip.Tip="Line color">
    <ctl:IconSelectButton.ItemTemplate>
        <DataTemplate x:DataType="vm:ColorItem">
            <!-- Rounded color square; fills the button/popup row, looks like a swatch button -->
            <Border Background="{Binding Brush}"
                    CornerRadius="3"
                    MinWidth="20" Height="12"
                    HorizontalAlignment="Stretch"
                    ToolTip.Tip="{Binding Name}"/>
        </DataTemplate>
    </ctl:IconSelectButton.ItemTemplate>
</ctl:IconSelectButton>
```
For the marker row, bind `SelectedMarkerColor` + `IsEnabled="{Binding MarkerEnabled}"` +
`ToolTip.Tip="Marker color"`. Keep the same `Grid.Column`/width the ComboBox occupied (col 2, the 95-px
YAxis column) so the row layout is unchanged.

Notes:
- `Highlight="False"` (no active-state accent on the idle button) and `HighlightSelected="False"` (the
  `flat-select` style → selected popup row keeps transparent background) together satisfy "no selection
  highlighting." The idle button shows the current color as a rounded square — exactly the requested
  look.
- `IconSelectButton`'s default `ControlTheme` already lives in this view's `UserControl.Resources`
  (PART_Button/PART_Popup/PART_ListBox), so the swatch renders as a button at idle and a vertical popup
  of swatches on click — same machinery the Line/Symbol/Matrix ISBs already use here.

**Part A check:** each former color combo is now a rounded color swatch button; clicking opens a vertical
list of color swatches; picking one updates the trace color live and shows no blue selection highlight;
disabled state (line/marker off) greys it.

---

## PART B — Live aspect-ratio update when switching to Smith/Polar

**Symptom:** changing plot type to Smith/Polar from Rect/Table (via the inspector's segmented header)
does not live-update the plot's container aspect ratio. The axes circle looks right, but the **selection
outline** and the **axes-label vertical position** are wrong until something else resizes the plot.

**Cause:** `PlotContainerViewModel` already handles `Inspector.PlotStructureChanged` by raising
`IsSquareAspect` + `NotifyViewProperties()`. But `NotifyViewProperties` recomputes the *view* props from
the existing `Width`/`Height`, which are still the old Rect dimensions (non-square). `ResizeTo` enforces
the square only during a drag-resize, never on a plot-type switch. So the container box stays non-square
→ the selection outline (drawn around `Width×Height`) is wrong, and the label strip vertical layout
(`TopLabelExtraLogical`/`BottomLabelExtraLogical`, both functions of `Width`, and the strip `Height`)
is computed against a stale, non-square `Height`.

**Fix:** when the structure changes, coerce the container box to the aspect the new plot type requires,
BEFORE `NotifyViewProperties()`. In `PlotContainerViewModel`'s `Inspector.PlotStructureChanged` handler:
```csharp
Inspector.PlotStructureChanged += (s, e) =>
{
    CoerceAspectForPlotType();           // ← new: square-up Width/Height for Smith/Polar; table width in Part F
    UpdateLabelStrips();
    OnPropertyChanged(nameof(IsSquareAspect));
    NotifyViewProperties();
};
```
Add the helper:
```csharp
/// <summary>
/// Re-shapes the container box to match the current plot type after a live plot-type switch:
///   • Smith/Polar  → square graph area (Width == Height), preserving the larger dimension so the
///     plot doesn't shrink; selection outline + label strips then lay out correctly.
///   • Table        → width set to the table's natural total column width (Part F).
///   • Rect         → left as-is (free aspect).
/// Logical (pre-zoom) coordinates, same space ResizeTo() uses.
/// </summary>
private void CoerceAspectForPlotType()
{
    if (IsSquareAspect)
    {
        double size = Math.Max(200, Math.Max(Width, Height));
        if (Width != size || Height != size) { Width = size; Height = size; }
    }
    // Table width handled in Part F (call SyncTableWidth() here).
}
```
`Width`/`Height` setters already fan out the right `OnPropertyChanged`s (see `OnWidthChanged`/
`OnHeightChanged`), so the selection outline and label strips refresh immediately.

> Keep `ResizeTo`'s square enforcement as-is (drag path). This only adds the type-switch path.

**Part B check:** with a plot selected, switch Rect→Smith (or Polar) in the inspector: the container
immediately becomes square, the selection outline hugs the square, and the axes labels sit at the
correct vertical position — no second resize needed. Smith→Rect leaves a free aspect.

---

## PART C — Trace card doesn't switch to S-param fields when source changes HB→S-param

**Symptom:** changing a trace's data source from an HB cube to an S-parameter source leaves the card
showing cube fields (spec editor / axis-role rows) and not the S-param fields (Matrix-type button, Z0
row).

**Cause:** `TraceRowViewModel.OnSelectedSignalChanged` updates the trace and raises `OnPropertyChanged`
for the Z0/matrix flags, but NOT for the cube/network *discriminator* properties the card visibility
binds to: `IsCubeBoundTrace`, `IsStandardTrace`, `ShowYAxisCombo`, `ShowAllNodesToggleVisible`,
`TraceTransformItems`, and the spec-editor props. So the `IsVisible="{Binding IsCubeBoundTrace}"` panels
don't toggle.

**Fix:** at the end of `OnSelectedSignalChanged` (after `_parent.RebuildAndNotify();`), refresh all the
discriminating flags. The cleanest is to call the existing `RefreshDescription()` (it already raises
most of them) PLUS the cube flag it omits:
```csharp
_parent.RebuildAndNotify();

// Source kind may have flipped (cube ↔ network) — refresh every card-visibility discriminator
// so the right fields show without reopening the inspector.
OnPropertyChanged(nameof(IsCubeBoundTrace));
OnPropertyChanged(nameof(ShowAllNodesToggleVisible));
RefreshDescription();   // raises ShowMatrixTypeCombo, ShowZ0Row/Control, ShowYAxisCombo, TraceTransformItems, Spec*, etc.
```
(`RefreshDescription` already raises `IsCubeBoundTrace`? — it does NOT; it raises `ShowYAxisCombo` which
depends on it but not the flag itself. Add the explicit `IsCubeBoundTrace` raise as shown. Verify
`IsStandardTrace` is constant `true` so it needs no raise.)

**Part C check:** a cube/HB trace switched to an S-param source immediately shows the Matrix-type button
+ Z0 row and hides the cube spec editor / axis-role rows; and the reverse (S-param → HB) shows the cube
editor and hides the S-param fields. No inspector reopen needed.

---

## PART D — HB voltage/current: use net-NAME notation, not node index

**Change:** when the user picks an HB voltage or current cube in the trace card, the authored slice /
shorthand should reference the node by its **net name** (e.g. `V[:, "Vout2", 2]`) rather than the bare
node index. Users rarely know the node index.

Where this is produced: a cube-bound trace's default slice pins the `node` axis to an index, and the
shorthand/expression is built by `Trace.BuildPickerExpression()` / `Trace.CubeShorthand` from the
`Slice` (`AxisSlice` carries an `Index`). The node axis has `Labels` (the net names) — see
`RebuildAxisRolesCore` which already reads `axis.Labels` for the node axis.

**STOP-and-report first:** read `src/Ui/DataDisplay/Models/Trace.cs` (`BuildPickerExpression`,
`CubeShorthand`) and `src/Ui/DataDisplay/SliceTokenParser.cs` to confirm:
- how the node axis index is currently emitted into the shorthand (numeric index vs. label), and
- whether the shorthand/parser already supports a quoted net-name token like `"Vout2"` in the node slot
  (the example `V[:, "Vout2", 2]` implies the parser accepts a quoted label in place of an index).

Then implement: when building the picker shorthand/expression for a cube that has a `node` axis with
`Labels`, emit the **quoted label** (`"{netName}"`) for the node slot instead of the integer index,
using `axis.Labels[index]`. Keep the numeric path as a fallback when the axis has no labels (hand-written
netlists). Ensure `SliceTokenParser` round-trips a quoted net-name token back to the correct node index
(match by label), so a user-typed `V[:, "Vout2", 2]` resolves. Report whether the parser already handles
quoted labels or needs the match-by-label path added.

**Part D check:** select an HB `V` (or `I`) cube on a node-bearing source → the spec field shows
`V[:, "Vout2", k]` (quoted net name), not `V[:, 7, k]`; editing the quoted name to another valid net
re-resolves correctly; a label with no name still falls back to the index form.

---

## PART E — Don't list ..Converged / ..Residual in the data-source combo

**Change:** the data-source (signal) ComboBox must not offer solver-diagnostic cubes whose names end in
`Converged` or `Residual` (or contain the `..`-style diagnostic marker). Advanced users can still plot
them by typing the expression manually in the spec field.

Where: `TraceRowViewModel.RebuildSignals()`, the cube loop already skips `S`, `Z0`, `__`-prefixed, and
node-indexed current cubes. Add a skip for converged/residual diagnostics:
```csharp
foreach (var (cubeName, cube) in ds.Cubes)
{
    if (cubeName is "S" or "Z0" || cubeName.StartsWith("__", StringComparison.Ordinal)) continue;
    // Solver diagnostics — not offered in the picker; advanced users type them in the spec field.
    if (cubeName.EndsWith("Converged", StringComparison.Ordinal) ||
        cubeName.EndsWith("Residual",  StringComparison.Ordinal)) continue;
    // … existing node-indexed-current skip and the rest …
}
```
> Confirm the actual cube names on disk (read the HB engine's cube emission if unsure). If the names are
> dotted (e.g. `HB..Converged`), `EndsWith("Converged")` still catches them. Match what the engine emits;
> report the exact names you guarded against.

**Part E check:** load an HB DataSet → the source combo no longer lists the Converged/Residual entries;
typing the cube name in the spec field still plots it.

---

## PART F — Table width grows/shrinks with trace count

**Change:** adding a trace to a **Table** plot must grow the table's width to exactly accommodate the new
column; removing a trace must shrink it to exactly fit the remaining columns.

**Cause:** the Table's container `Width` is independent of its column count. On add/remove,
`PlotInspectorViewModel` fires `PlotStructureChanged`, but `PlotContainerViewModel` never recomputes
`Width` from the table's natural total column width.

**Fix:** compute the table's natural width = freq column width + Σ per-trace `ColumnWidth`, and set the
container `Width` to it on every structure change (and once on load) for Table plots. Put it in the same
`CoerceAspectForPlotType()` added in Part B:
```csharp
private void CoerceAspectForPlotType()
{
    if (IsSquareAspect)
    {
        double size = Math.Max(200, Math.Max(Width, Height));
        if (Width != size || Height != size) { Width = size; Height = size; }
    }
    else if (PlotVM.Plot.PlotType == PlotType.Table)
    {
        SyncTableWidth();
    }
}

/// <summary>
/// Sets the container Width to the table's natural total column width so the table box exactly
/// fits its columns (freq column + one per trace value column). Height is left to the user/drag.
/// </summary>
private void SyncTableWidth()
{
    var plot = PlotVM.Plot;
    if (plot.PlotType != PlotType.Table) return;
    var cols = CircuitRF.Ui.Renderers.TableRenderer.BuildColumns(plot);   // freq col + per-trace cols
    double total = 0;
    foreach (var c in cols)
        total += c.Kind == CircuitRF.Ui.Renderers.TableColKind.TraceValue
            ? plot.Traces[c.FirstTraceIndex].ColumnWidth
            : plot.ColumnWidth;     // freq / non-trace columns use the plot-level width
    double newW = Math.Max(200, total);
    if (Math.Abs(Width - newW) > 0.5) Width = newW;
}
```
> Read `src/Ui/DataDisplay/Renderers/TableRenderer.cs` (`BuildColumns`, `TableColKind`,
> `MinColumnWidth`, and any existing total-width helper) to use the EXACT column set/width the renderer
> draws — the box width must match the rendered table to the pixel. If `TableRenderer` already exposes a
> "total width" helper, call that instead of re-summing. Report which you used.
>
> Also call `SyncTableWidth()` once when the container is first built for a Table plot (e.g. at the end
> of the constructor, or in the first `UpdateLabelStrips`), so an initially-Table plot starts at the
> right width. Per-column manual resize (the drag handle in `PlotControl`) already sets
> `Trace.ColumnWidth`/`Plot.ColumnWidth`; after such a drag the container should also re-fit — confirm
> whether the drag-end path (`PlotChanged`) should call `SyncTableWidth()` too, and wire it if so
> (likely yes, so the box tracks a column-width drag).

**Part F check:** on a Table plot, "+ Trace" widens the table box by exactly the new column's width;
removing a trace narrows it to exactly fit; resizing a column re-fits the box; switching a plot to Table
sizes the box to its columns.

---

## Gate
Build 0W/0E. Tests green. Manual checks per part above. Summary verifications:
- Color swatches replace all color combos (report the count — expected 3) with no selection highlight.
- Rect→Smith/Polar squares the container live (outline + labels correct).
- HB→S-param (and back) flips the trace card's fields without reopening.
- HB V/I shorthand shows quoted net names; parser round-trips them.
- Converged/Residual cubes absent from the source combo; still typeable.
- Table box width exactly fits its columns on add/remove/resize/type-switch.

**STOP-and-report checkpoints:** Part A color-combo count; Part D Trace.cs/SliceTokenParser net-name
emission + parser support; Part E exact diagnostic cube names; Part F TableRenderer width helper + whether
the column-drag end should re-fit.
