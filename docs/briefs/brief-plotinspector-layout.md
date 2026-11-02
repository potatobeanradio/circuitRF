# Sonnet Brief — PlotInspector: combobox content-width quirk + Table "Highlight" relayout

Two localized visual fixes in `src/Ui/Views/DataDisplay/PlotInspectorView.axaml` (one strictly timeboxed).

## Fix A — ComboBox hidden-chevron content not using the freed space  (TIMEBOX: 5 MINUTES)
The base `ComboBox` style already hides the dropdown glyph and tries to reclaim its space:
```xml
<Style Selector="ComboBox /template/ PathIcon#DropDownGlyph">
    <Setter Property="IsVisible" Value="False"/><Setter Property="Width" Value="0"/><Setter Property="MinWidth" Value="0"/>
</Style>
<Style Selector="ComboBox /template/ ContentPresenter#PART_ContentPresenter">
    <Setter Property="Grid.ColumnSpan" Value="2"/>
</Style>
```
…but content still doesn't fill the freed area. **Spend at most 5 minutes.** Most likely cause in the Avalonia
12 Fluent ComboBox template: the toggle area `Grid` reserves a fixed/`Auto` second column for the glyph that
`ColumnSpan=2` overlaps but doesn't *collapse*, and/or `PART_ContentPresenter` carries right `Padding`/`Margin`.
Two quick things to try (stop at the first that works):
1. Add a right-padding/margin zero-out to the content presenter style:
   `<Setter Property="Margin" Value="0"/>` and confirm the ComboBox's own `Padding="4,1"` isn't asymmetric.
2. Verify the template part name is actually `PART_ContentPresenter` in Avalonia 12.0.3's Fluent ComboBox (it
   may be `ContentPresenter` without the `PART_` prefix, or `PART_ContentPresenterFrame`). If the selector is
   matching nothing, the `ColumnSpan=2` is silently a no-op — fixing the selector name is the whole fix.

If neither resolves it within the timebox, **revert any experimental change and leave the current behavior**;
add a one-line `<!-- TODO: chevron-space reclaim not resolved; Fluent template column reserves width -->` comment
and report "not fixed in timebox" on completion. Do not rabbit-hole.

## Fix B — Table plot: move "Highlight" combobox to the Z0 row, right-aligned, drop the label
Currently, for Table plots the row-highlight color picker lives in the number-format `Grid`
(`ColumnDefinitions="Auto,Auto,Auto,Auto,*,*"`) as the last two cells: a `Highlight` label (col 4) and a
`ComboBox` (col 5, bound `SelectedIndex={Binding LineColorIndex}`, `IsEnabled={Binding LineEnabled}`). Move it to
the **Z0 row** for a cleaner look.

The Z0 row Grid (inside the standard-trace body) is:
```xml
<Grid ColumnDefinitions="Auto,Auto,Auto,*">
    <TextBlock Grid.Column="0" Text="Z0" .../>
    <TextBox   Grid.Column="1" .../>          <!-- Z0String -->
    <TextBlock Grid.Column="2" Text="Ω" .../>
    <!-- Column 3 (*) currently empty -->
</Grid>
```
This Z0 row has **no `IsVisible` binding**, so it shows for all standard-trace plot types including Table — which
is exactly where we want the Table-only Highlight picker to sit.

**Changes:**
1. In the **number-format Grid**, delete the `Highlight` `TextBlock` (col 4) and its `ComboBox` (col 5). Change
   its `ColumnDefinitions` from `Auto,Auto,Auto,Auto,*,*` to `Auto,Auto,Auto,Auto` (Format label, Format combo,
   Digits label, Digits NUD). (Leave a trailing `*` only if needed for spacing — `Auto,Auto,Auto,Auto,*` is fine
   too; pick whichever keeps Digits from stretching.)
2. In the **Z0 row Grid**, add the Highlight ComboBox into column 3 (`*`), right-aligned, visible only for Table.
   Preserve the exact bindings it had (`SelectedIndex={Binding LineColorIndex, Mode=TwoWay}`,
   `IsEnabled={Binding LineEnabled}`, the `ColorItems` ItemsSource, and the swatch `ItemTemplate` with
   `Width="34" Height="10"`):
   ```xml
   <ComboBox Grid.Column="3"
             ItemsSource="{x:Static vm:PlotInspectorViewModel.ColorItems}"
             SelectedIndex="{Binding LineColorIndex, Mode=TwoWay}"
             IsEnabled="{Binding LineEnabled}"
             IsVisible="{Binding IsTablePlot}"
             HorizontalAlignment="Right"
             VerticalAlignment="Center"
             Padding="4,0">
       <ComboBox.ItemTemplate>
           <DataTemplate x:DataType="vm:ColorItem">
               <Border Width="34" Height="10" Background="{Binding Brush}" CornerRadius="2"
                       ToolTip.Tip="{Binding Name}"/>
           </DataTemplate>
       </ComboBox.ItemTemplate>
   </ComboBox>
   ```
   No "Highlight" label (dropped per spec). Keep its `ToolTip.Tip="Highlight color"` so the affordance is still
   discoverable.

The Z0 row is inside the `IsStandardTrace` StackPanel and the Z0 cells are always visible; the new combobox is
gated on `IsTablePlot`, so non-Table plots show just `Z0 [____] Ω` and Table plots show `Z0 [__] Ω … [swatch]`
right-aligned. Confirm `IsTablePlot` and `LineColorIndex`/`LineEnabled` exist on `TraceRowViewModel` (they're
already bound elsewhere in this template, so they do).

## Gate
Build 0W/0E. Table plot: Highlight swatch sits on the Z0 row, right-aligned, no "Highlight" text; number-format
row shows only Format + Digits. Other plot types unchanged. Fix A either resolved or cleanly reverted with the
TODO note (report which).
