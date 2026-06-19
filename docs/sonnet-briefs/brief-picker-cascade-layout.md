# Sonnet Brief 4c — cascading signal picker, relocated transform combo, eye toggle button

Restructures the trace-card picker per the owner's layout:

1. **Cascading combos.** Replace the single signal combo with two: a **group** combo (`Measurements`,
   `HB1`, `DC1`, `S-Parameters`, …) and, to its right, an **item** combo listing that group's quantities.
   Changing the group repopulates the item combo.
2. **Move the transform combo** (dB20/dB10/dB/Mag/…) out of the identity row onto the **spec/expression
   row**, left of the spec TextBox; shrink the TextBox to compensate.
3. **Eye toggle button.** Replace the "Show all" checkbox with a small no-text **seg-btn** carrying the
   Material `Eye` icon, placed to the **right of the item combo** in the identity row; active when
   `ShowAll` is on.

UX invariant (must hold): using only the picker always produces a valid trace expression. Data Display
only. Build 0W/0E (`TreatWarningsAsErrors=true`); tests green.

Read first: `ViewModels/TraceRowViewModel.cs` (`RebuildSignals`, `OnSelectedSignalChanged`,
`_suppressDataCallback`), `ViewModels/TraceDataItem.cs`, and the trace-card `DataTemplate` in
`Views/DataDisplay/PlotInspectorView.axaml` (identity-row Grid, the transform combo, the "Show all"
checkbox, the spec TextBox). The `seg-btn` style and `mi:` (Material.Icons.Avalonia) namespace already
exist in that AXAML.

---

## Part A — `TraceDataItem`: carry a group

Add an init-only group tag (display header the item belongs to). No ctor signature changes:
```csharp
/// <summary>Picker group header this item belongs to (e.g. "HB1", "Measurements", "S-Parameters").</summary>
public string Group { get; init; } = "";
```

## Part B — `TraceRowViewModel`: the cascade

### B1. New members
```csharp
// Full unfiltered signal set (rebuilt by RebuildSignals); AvailableSignals is the slice for SelectedGroup.
private readonly List<TraceDataItem> _allSignals = new();

/// <summary>Group headers for the left picker combo (distinct, in build order).</summary>
public ObservableCollection<string> AvailableGroups { get; } = new();

[ObservableProperty]
private string? _selectedGroup;

partial void OnSelectedGroupChanged(string? value)
{
    if (_suppressDataCallback) return;          // set during RebuildSignals — don't re-apply
    _suppressDataCallback = true;
    FilterSignalsToGroup(value);
    _suppressDataCallback = false;
    SelectedSignal = AvailableSignals.FirstOrDefault();   // applies the first item of the new group
}

private void FilterSignalsToGroup(string? group)
{
    AvailableSignals.Clear();
    if (group is null) return;
    foreach (var s in _allSignals)
        if (s.Group == group) AvailableSignals.Add(s);
}

public IRelayCommand ToggleShowAllCommand { get; }   // assign in ctor: new RelayCommand(() => ShowAll = !ShowAll)
```
(`AvailableSignals` stays the `ObservableCollection<TraceDataItem>` the item combo binds to, but it now
holds only the selected group's items.)

### B2. Build groups into `_allSignals`, not straight into `AvailableSignals`
In `RebuildSignals`, change the two build loops to append to `_allSignals` (cleared at the top) and to
**set `Group`** + a within-group **Label**:

- At top: `_allSignals.Clear(); AvailableGroups.Clear();` (keep `AvailableSignals.Clear()` too).
- **Network loop:** compute the group once per entry and tag items; use the bare element/derived label
  (the group now carries the source):
  ```csharp
  string netGroup = (singleSource ? "" : $"{System.IO.Path.GetFileNameWithoutExtension(entry.DisplayName)}..")
                  + "S-Parameters";
  ```
  Every `new TraceDataItem(entry, MatrixType, r, c, /*omitFilePrefix:*/ true, …)` and the derived-param
  items become `… { Group = netGroup }`, and pass `omitFilePrefix: true` so the Label is just
  `S(1,1)` / `Load Stability µ`. Add them to `_allSignals` (not `AvailableSignals`).
- **Cube loop:** group display name + bare quantity label:
  ```csharp
  string groupDisplay = group == DataSet.DefaultGroup     ? "Signals"
                      : group == DataSet.MeasurementsGroup ? "Measurements"
                      :                                      group;        // "HB1", "DC1", "SP1"
  string cubeGroup = filePrefix + groupDisplay;
  …
  _allSignals.Add(new TraceDataItem(entry, qualified, defaultSlice, /*label:*/ bareName, isEnabled)
                  { Group = cubeGroup });
  ```
  (`qualified` is still the bare-or-qualified CubeName from Brief 4b — unchanged. Only the *Label*
  becomes the bare quantity, since the group header carries the analysis/source.)

### B3. Populate `AvailableGroups` then select group → item (suppressed)
Replace the existing "Select the item matching the current trace state" tail. Keep the existing `match`
computation, but source it from `_allSignals` instead of `AvailableSignals` (same predicates). Then:
```csharp
foreach (var s in _allSignals)
    if (!AvailableGroups.Contains(s.Group)) AvailableGroups.Add(s.Group);

_suppressDataCallback = true;
SelectedGroup = match?.Group ?? AvailableGroups.FirstOrDefault();
FilterSignalsToGroup(SelectedGroup);
SelectedSignal = match ?? AvailableSignals.FirstOrDefault();
_suppressDataCallback = false;
```
Leave everything after (the `ShowZ0*`/`ShowMatrixTypeCombo` raises + `ApplySourceZ0` + `RebuildAxisRoles`)
as-is. The `OnSelectedSignalChanged` early-returns under suppression, so no trace mutation happens during
rebuild (current behavior preserved).

### B4. Constructor
Assign `ToggleShowAllCommand = new RelayCommand(() => ShowAll = !ShowAll);` near the other command
assignments. (`OnShowAllChanged` from Brief 4b already triggers `RebuildSignals`, which preserves the
current group/item via `match`.)

## Part C — AXAML identity row (`PlotInspectorView.axaml`)

Replace the identity-row `Grid` (the one with `ColumnDefinitions` `*`,`1000*`,`Auto`,`26` holding the
signal combo, transform combo, matrix button, →R) with five columns: group, item, eye, matrix, →R.
**The transform combo moves out of this row (to Part D).**

```xml
<Grid ClipToBounds="False">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"  MinWidth="60"/>   <!-- group -->
        <ColumnDefinition Width="*"  MinWidth="60"/>   <!-- item  -->
        <ColumnDefinition Width="Auto"/>               <!-- eye   -->
        <ColumnDefinition Width="Auto"/>               <!-- matrix (S-only) -->
        <ColumnDefinition Width="26"/>                 <!-- →R (Rect) -->
    </Grid.ColumnDefinitions>

    <!-- Col 0: group header -->
    <ComboBox Grid.Column="0"
              ItemsSource="{Binding AvailableGroups}"
              SelectedItem="{Binding SelectedGroup, Mode=TwoWay}"
              PlaceholderText="(load a file…)"
              Margin="0,0,3,0" MinWidth="20"
              HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch">
        <ComboBox.ItemTemplate>
            <DataTemplate>
                <TextBlock Text="{Binding}" FontSize="{StaticResource FontSize}"
                           TextTrimming="CharacterEllipsis" ToolTip.Tip="{Binding}"/>
            </DataTemplate>
        </ComboBox.ItemTemplate>
    </ComboBox>

    <!-- Col 1: item within the group (keep the existing item template + IsEnabled container theme) -->
    <ComboBox Grid.Column="1"
              ItemsSource="{Binding AvailableSignals}"
              SelectedItem="{Binding SelectedSignal, Mode=TwoWay}"
              Margin="0,0,3,0" MinWidth="20"
              HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch">
        <ComboBox.ItemContainerTheme>
            <ControlTheme TargetType="ComboBoxItem">
                <Setter Property="IsEnabled" Value="{ReflectionBinding IsEnabled}"/>
            </ControlTheme>
        </ComboBox.ItemContainerTheme>
        <ComboBox.ItemTemplate>
            <DataTemplate x:DataType="vm:TraceDataItem">
                <TextBlock Text="{Binding Label}"
                           Classes.brokenSource="{Binding IsBroken}"
                           FontSize="{StaticResource FontSize}"
                           TextTrimming="CharacterEllipsis"
                           ToolTip.Tip="{Binding Label}"/>
            </DataTemplate>
        </ComboBox.ItemTemplate>
    </ComboBox>

    <!-- Col 2: eye toggle (unified Show-all) — seg-btn toolbar style, active when ShowAll -->
    <Button Grid.Column="2"
            Classes="seg-btn"
            Classes.active="{Binding ShowAll}"
            Command="{Binding ToggleShowAllCommand}"
            IsVisible="{Binding ShowAllToggleVisible}"
            Width="26" Padding="3,2" Margin="0,0,3,0"
            ToolTip.Tip="Show all nodes / device-port branch currents">
        <mi:MaterialIcon Kind="Eye" Width="14" Height="14"/>
    </Button>

    <!-- Col 3: matrix type (S-parameter sources only) — unchanged content, now Grid.Column=3 -->
    <ctl:IconSelectButton Grid.Column="3" … (move the existing matrix IconSelectButton here verbatim) />

    <!-- Col 4: →R secondary-axis (Rect only) — unchanged content, now Grid.Column=4 -->
    <Button Grid.Column="4" … (move the existing →R button here verbatim) />
</Grid>
```
Delete the old "Show all" `CheckBox` from the trace body (it's replaced by the eye button).

## Part D — AXAML: transform combo onto the spec row

Replace the standalone spec `TextBox` (`IsVisible="{Binding IsCubeBoundTrace}" Text="{Binding SpecShorthand…}"`)
with a 2-column row holding the relocated transform combo + a narrower TextBox:
```xml
<Grid ColumnDefinitions="Auto,*" Margin="0,2,0,0">
    <!-- Transform combo (Rect/Table; both cube and network) — moved from the identity row -->
    <ComboBox Grid.Column="0"
              IsVisible="{Binding IsRectOrTablePlot}"
              Width="90" Margin="0,0,3,0"
              ItemsSource="{Binding TraceTransformItems}"
              SelectedItem="{Binding SelectedTransformItem, Mode=TwoWay}">
        <ComboBox.ItemContainerTheme>
            <ControlTheme TargetType="ComboBoxItem">
                <Setter Property="IsEnabled" Value="{ReflectionBinding Enabled}"/>
            </ControlTheme>
        </ComboBox.ItemContainerTheme>
        <ComboBox.ItemTemplate>
            <DataTemplate x:DataType="vm:CubeTransformItem">
                <TextBlock Text="{Binding Label}" FontSize="{StaticResource FontSize}"/>
            </DataTemplate>
        </ComboBox.ItemTemplate>
    </ComboBox>

    <!-- Spec editor (cube-bound). Auto column above collapses when the combo is hidden (Smith/Polar),
         so the TextBox reclaims full width. -->
    <TextBox Grid.Column="1"
             IsVisible="{Binding IsCubeBoundTrace}"
             Text="{Binding SpecShorthand, Mode=OneWay}"
             FontSize="{StaticResource FontSize}"
             Padding="4,2"
             LostFocus="OnSpecEditLostFocus"
             KeyDown="OnSpecEditKeyDown"
             ToolTip.Tip="Type a cube shorthand, e.g. dB20 V[0, 1, :]"/>
</Grid>
```
Keep the `SpecError` `SelectableTextBlock` immediately below, unchanged. For a network trace
(`IsCubeBoundTrace=false`) on Rect/Table, only the transform combo shows here — so network traces keep
their transform selector after the move.

## Behavior notes
- Changing the **group** selects that group's first item and applies it (cascade). Selecting an **item**
  applies it as before. Both keep the trace valid (UX invariant).
- The **eye** drives the same unified `ShowAll` from Brief 4b: it reveals device-port currents in the
  item combo AND unlabeled nodes in the axis-role pin lists. Hidden when nothing is filterable
  (`ShowAllToggleVisible`).
- Selection state survives `RebuildSignals` via the existing `match` (now resolved against `_allSignals`),
  which also seeds `SelectedGroup`.

## Tests — `tests/Ui.Tests`
1. **Groups_Built:** an HB run (groups `HB1` + `measurements`) → `AvailableGroups` == `["HB1","Measurements"]`;
   selecting `HB1` populates `AvailableSignals` with `V`/`I:Ids` (Labels are bare quantities); selecting
   `Measurements` repopulates with `PDC` etc.
2. **GroupChange_AppliesFirstItem:** setting `SelectedGroup="Measurements"` sets `SelectedSignal` to that
   group's first item and binds the trace to it (no expression error).
3. **NetworkGroup:** a single `.s2p` source → group `S-Parameters`, items `S(1,1)…` + derived; item
   Labels omit the file prefix.
4. **Rebuild_PreservesSelection:** with a cube trace bound to `HB1.V`, `RebuildSignals` re-selects group
   `HB1` and item `V` (no spurious trace mutation — `OnSelectedSignalChanged` suppressed during rebuild).
5. **EyeToggle:** `ToggleShowAllCommand` flips `ShowAll`; `ShowAllToggleVisible` gates the button as in 4b.
6. **TransformOnNetwork:** a network trace on Rect still exposes the transform combo (now on the spec row).

## Gate (manual)
Open a Data Display on a single-tone HB run with an IProbe + a `PDC` measurement, plus a loaded `.s2p`.
The trace card shows two combos: a group picker (`HB1` / `Measurements` / `S-Parameters`) and an item
picker that updates when the group changes. The transform combo sits on the spec-box row; the spec box is
narrower and widens on Smith/Polar. An eye button sits right of the item combo, highlighting when active
and revealing device-port currents + unlabeled nodes. Picking any group→item yields a valid expression in
the spec box.

## On completion
Note in `src/Ui/DataDisplay/CLAUDE.md`: the trace-card signal picker is a group→item cascade
(`AvailableGroups`/`SelectedGroup` → `AvailableSignals`/`SelectedSignal`); the transform combo lives on the
spec row; the unified `ShowAll` is an eye toggle button (`seg-btn`) right of the item combo.
