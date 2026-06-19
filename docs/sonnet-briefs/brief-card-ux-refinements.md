# Sonnet Brief F2 — trace-card UX refinements (V/I icon-select, drop trivial `[:]`, first-add mag())

Three small, independent Data-Display refinements. Build 0W/0E; tests green. (A fourth item — renaming the
"Pin" button — is pending a terminology decision and is NOT in this brief.)

Read first: `Views/DataDisplay/PlotInspectorView.axaml` (identity-row item combo),
`ViewModels/TraceRowViewModel.cs` (`FilterSignalsToGroup`/`AvailableSignals`),
`Models/Trace.cs` (`BuildPickerExpression`), `ViewModels/PlotInspectorViewModel.cs` (`BuildSeedCubeTrace`),
`Controls/IconSelectButton.cs`.

## 1. V/I item selector → `IconSelectButton` (easier to hit than a 2-char combo)
For analysis groups the item list is exactly `V`/`I`; those single characters are awkward combo targets.
Show an `IconSelectButton` for that case, keep the `ComboBox` for groups with longer/variable item lists
(Measurements, S-Parameters) so their disabled-item handling is preserved.

### 1a. VM flag (`TraceRowViewModel`)
```csharp
/// <summary>True when the item list is the analysis V/I pair (render as an IconSelectButton).</summary>
public bool IsViSelector =>
    AvailableSignals.Count > 0 &&
    AvailableSignals.All(s => s.IsCubeBound && (s.Label == "V" || s.Label == "I"));
```
Raise `OnPropertyChanged(nameof(IsViSelector))` at the end of `FilterSignalsToGroup` (where
`AvailableSignals` is repopulated) so it tracks group changes.

### 1b. AXAML (identity row, Col 1)
Replace the single item `ComboBox` with an `IconSelectButton` + the existing `ComboBox`, both in
`Grid.Column="1"`, visibility-toggled:
```xml
<!-- Analysis V/I → compact icon-select -->
<ctl:IconSelectButton Grid.Column="1"
                      IsVisible="{Binding IsViSelector}"
                      ItemsSource="{Binding AvailableSignals}"
                      SelectedItem="{Binding SelectedSignal, Mode=TwoWay}"
                      Highlight="False" HighlightSelected="False"
                      Margin="0,0,3,0" MinWidth="40"
                      ToolTip.Tip="Quantity (V / I)">
    <ctl:IconSelectButton.ItemTemplate>
        <DataTemplate x:DataType="vm:TraceDataItem">
            <TextBlock Text="{Binding Label}" FontSize="{StaticResource FontSize}"
                       HorizontalAlignment="Center"/>
        </DataTemplate>
    </ctl:IconSelectButton.ItemTemplate>
</ctl:IconSelectButton>

<!-- Measurements / S-Parameters → keep the ComboBox (existing markup, add IsVisible) -->
<ComboBox Grid.Column="1"
          IsVisible="{Binding !IsViSelector}"
          ItemsSource="{Binding AvailableSignals}"
          SelectedItem="{Binding SelectedSignal, Mode=TwoWay}"
          … (keep existing ItemContainerTheme + ItemTemplate) … />
```
(`Highlight="False" HighlightSelected="False"` = no accent highlight per the request; the button still
shows the current item via the template, and the popup lists `V`/`I` as larger click targets.)

## 2. Drop a trivial `[:]` — show `PDC`, not `PDC[:]` (`Trace.BuildPickerExpression`)
When the picker-authored slice is a single whole-axis X, emit the bare cube name. Right after the existing
rank-0 early return (`if (Slice.Length == 0) …`), add:
```csharp
// A single whole-axis X (e.g. "PDC[:]") reads better bare.
if (Slice.Length == 1 && Slice[0].Role == AxisRole.KeepAsX && !Slice[0].IsNarrowedRange)
    return Transform == CubeTransform.None
        ? CubeName
        : $"{TransformFunctionName(Transform)}({CubeName})";
```
This affects only **picker-authored** expressions. A user who explicitly types `PDC[:]` keeps it:
`CommitSpec` stores the typed text verbatim and does not call `BuildPickerExpression`, so the `[:]`
survives until the user re-picks via the combos. (Confirm `AxisSlice.IsNarrowedRange` and
`TransformFunctionName` exist — both are already used elsewhere in `Trace`.)

## 3. First-add on a Rect plot defaults a complex cube to `mag()` (`BuildSeedCubeTrace`)
So clicking "Add trace" on a Rect plot always shows a curve instead of an `<invalid>` (complex-on-Rect).
Seed-time only — do not re-check on later edits. In `BuildSeedCubeTrace`, just before the seed trace's
`Expression = …BuildPickerExpression()` line (the normal rank≥1 path), add:
```csharp
// First-add nicety: a complex cube on a Rect plot would render <invalid>; default to mag() so the user
// sees something. Seed-time only — never re-applied on later edits.
if (_plot.PlotType == PlotType.Rect && cube.DataKind == DataKind.Complex)
    seedTrace.Transform = CubeTransform.Mag;
```
(Use the actual seed-trace variable name in that method. `cube` is the seeded cube already resolved there.
Smith/Polar/Table are unaffected.)

## Tests
1. **ViSelector_FlagOnAnalysisGroup:** an `HB1` group (items V/I) → `IsViSelector == true`; a Measurements
   group (PDC, Gain) → false.
2. **PickerExpr_DropsTrivialColon:** a trace whose slice is a single whole-X axis →
   `BuildPickerExpression()` returns `"PDC"` (and `"mag(PDC)"` with a transform), not `"PDC[:]"`.
3. **PickerExpr_KeepsRange:** a narrowed-range X (`PDC[1:4]`) is NOT collapsed.
4. **UserTypedColon_Respected:** `CommitSpec("PDC[:]")` leaves `SpecShorthand == "PDC[:]"` (not collapsed).
5. **FirstAdd_RectComplex_Mag:** seeding a complex cube on `PlotType.Rect` → seed `Transform == Mag`
   (Expression `mag(...)`); on `PlotType.Smith` → `Transform == None`.

## Gate (manual)
Analysis group shows a compact `V/I` icon-select (click → V or I) instead of a cramped combo. Pick a
rank-1 measurement → the spec box reads `Gain`, not `Gain[:]`; typing `Gain[:]` keeps `Gain[:]`. On a Rect
plot, "Add trace" on a complex signal shows `mag(...)` plotting immediately rather than `<invalid>`.

## On completion
Note in `src/Ui/DataDisplay/CLAUDE.md`: analysis V/I uses an `IconSelectButton`; picker expressions drop a
trivial `[:]`; first-add on Rect defaults complex cubes to `mag()`.
