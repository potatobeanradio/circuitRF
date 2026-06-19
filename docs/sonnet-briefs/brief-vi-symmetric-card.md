# Sonnet Brief U1 — V/I-symmetric trace card (branch filter, eye on pin row, spec reverse-sync, empty state)

Completes the V/I-symmetric card on top of the unified `I` cube (E1, landed) and the group→item cascade
(landed). Four parts, independent enough to stage. Data Display only. Build 0W/0E
(`TreatWarningsAsErrors=true`); tests green.

Read first: `ViewModels/TraceRowViewModel.cs` (`RebuildAxisRolesCore`, `RebuildSignals`, `CommitSpec`,
`OnSelectedSignalChanged`), `ViewModels/AxisRoleRowViewModel.cs`, `ViewModels/TraceDataItem.cs`,
`Views/DataDisplay/PlotInspectorView.axaml` (identity row + axis-role `ItemsControl` template).

Context (verified): the unified `I` cube has a `branch` axis with `Labels` = branch names;
`__ProbeBranches` lists the IProbe subset — the exact mirror of `V`/`node`/`__LabeledNodes`. The
axis-role editor already renders one generic X/Fam/Pin row per axis with a label-filtered pin combo, so
the branch row needs no new template — only the filter wiring.

## Part A — filter the `branch` axis like the `node` axis (`RebuildAxisRolesCore`)

Today the method reads `__LabeledNodes` and filters only `axis.Name == "node"`. Generalize to whichever
label axis the cube has (`node` **or** `branch`) and its provenance cube.

Replace the `__LabeledNodes` read + `hasNode` detection block with:
```csharp
// Which axis (if any) is the filterable label axis, and its provenance side-cube.
string? filterAxisName = null, provenanceCube = null;
foreach (var ax in cube.Axes)
{
    if (ax.Name == "node")   { filterAxisName = "node";   provenanceCube = "__LabeledNodes";  break; }
    if (ax.Name == "branch") { filterAxisName = "branch"; provenanceCube = "__ProbeBranches"; break; }
}

HashSet<string>? labeledSet = null;
if (provenanceCube is not null)
{
    string sib = SiblingCubeName(_trace.CubeName, provenanceCube);
    if (ds.Contains(sib))
    {
        labeledSet = new HashSet<string>(StringComparer.Ordinal);
        var lblCube   = ds[sib];
        var labelAxis = lblCube.Axes.FirstOrDefault(a => a.Labels is not null);   // "label" or "probe"
        if (labelAxis?.Labels is { } lbls) foreach (var l in lbls) labeledSet.Add(l);
    }
}

bool hasFilterAxis = filterAxisName is not null;
if (_hasNodeAxis != hasFilterAxis)            // _hasNodeAxis now means "has a filterable label axis"
{
    _hasNodeAxis = hasFilterAxis;
    OnPropertyChanged(nameof(ShowAllNodesToggleVisible));
    OnPropertyChanged(nameof(ShowAllToggleVisible));
}

if (labeledSet is null && !ShowAll) ShowAll = true;   // no provenance ⇒ show all (unchanged)
bool showAll = ShowAll;
```
Then in the per-axis loop, change the filter guard from `axis.Name == "node"` to
`axis.Name == filterAxisName`:
```csharp
if (axis.Name == filterAxisName && !showAll && labeledSet is not null) { /* filtered (unchanged) */ }
else { /* unfiltered (unchanged) */ }
```
Everything else in the method (filtered/unfiltered option building, `optionsAreLabels: true`, the X
fallback) is unchanged. Now a `V` cube filters its node pins by `__LabeledNodes` and an `I` cube filters
its branch pins by `__ProbeBranches`, identically.

Cleanup: the Brief-2 `ShowAllBranchesToggleVisible` scanned for separate `I:*` cubes, which no longer
exist — it now always returns false. Simplify `ShowAllToggleVisible` to `IsCubeBoundTrace && _hasNodeAxis`
and delete `ShowAllBranchesToggleVisible` and its `OnPropertyChanged` raises.

## Part B — eye on the node/branch pin row (not the identity row)

### B1. `AxisRoleRowViewModel`
Add an init flag marking the filterable label row:
```csharp
public bool IsFilterableLabelAxis { get; }
```
Set it in the constructor from a new parameter `bool isFilterableLabelAxis = false`. In
`RebuildAxisRolesCore`, pass `isFilterableLabelAxis: axis.Name == filterAxisName` in BOTH
`new AxisRoleRowViewModel(...)` calls (filtered and unfiltered).

### B2. AXAML — remove the identity-row eye, add it to the pin row
- **Delete** the identity-row eye `<Button Grid.Column="2" … ToggleShowAllCommand …>` and collapse the
  identity row to four columns. Change the identity `Grid.ColumnDefinitions` from
  `* | * | Auto | Auto | 26` to `* | * | Auto | 26`, and renumber the matrix button to `Grid.Column="2"`
  and the →R button to `Grid.Column="3"`.
- In the axis-role row `DataTemplate`, add a trailing column for the eye. Change the row Grid
  `ColumnDefinitions="70,Auto,Auto,Auto,*"` to `"70,Auto,Auto,Auto,*,Auto"`, and after the pin
  `ComboBox` (Col 4) add:
  ```xml
  <!-- Eye: reveal unlabeled nodes / device-port branches. Tied to the pin selector. -->
  <Button Grid.Column="5"
          Classes="seg-btn"
          Classes.active="{Binding $parent[ItemsControl].DataContext.ShowAll}"
          Command="{Binding $parent[ItemsControl].DataContext.ToggleShowAllCommand}"
          IsVisible="{Binding IsFilterableLabelAxis}"
          Width="26" Padding="3,2" Margin="3,0,0,0"
          ToolTip.Tip="Show all nodes / branch currents">
      <mi:MaterialIcon Kind="Eye" Width="14" Height="14"/>
  </Button>
  ```
  (`IsFilterableLabelAxis` already implies the pin combo is shown — that row is always Pin or X/Fam; when
  the row is X/Fam the pin combo hides and the eye sitting beside it is harmless. If you want them to hide
  together exactly, also bind `IsVisible` to a multi-condition with `ShowPinPicker`; a simple converter or
  leaving it on the row is acceptable since the eye only acts on the label axis.)

The `$parent[ItemsControl]` ancestor binding reaches the `TraceRowViewModel` (the `ItemsControl`'s
DataContext) for `ShowAll` / `ToggleShowAllCommand`.

## Part C — spec edit reverse-syncs every combo (`CommitSpec`)

Today `CommitSpec` parses the text, sets `CubeName/Slice/Transform`, calls `RebuildAndNotify()` (which
syncs the transform combo + flags via `RefreshDescription`), then `RebuildAxisRoles()` — but it never
re-syncs the **group/item** combos, so editing the text to a different cube leaves those stale.

On a **valid** single-cube parse, call `RebuildSignals()` instead of `RebuildAxisRoles()` — it re-derives
`match` from the new `_trace.CubeName`, re-selects `SelectedGroup`/`SelectedSignal` under the existing
`_suppressDataCallback` guard, and calls `RebuildAxisRoles()` at its tail. On an **invalid**/multi-cube
expression, keep the best-effort path (clear stale rows; leave the combos as the user left them):
```csharp
_parent.RebuildAndNotify();
if (_trace.CubeName is not null)   // valid single-cube spec
    RebuildSignals();              // re-syncs group + item combos AND axis-role rows to the new cube
else
    RebuildAxisRoles();            // best-effort: clear stale rows; combos unchanged
OnPropertyChanged(nameof(IsCubeBoundTrace));
OnPropertyChanged(nameof(ShowAllToggleVisible));
```
`RebuildSignals()` is safe here: `CommitSpec` runs from the spec box's LostFocus/Enter, not from a combo
selection callback, and the `_suppressDataCallback` guard prevents the re-selection from re-entering
`OnSelectedSignalChanged`. (This is the same revert-bug concern noted on `RefreshDescription`; it does not
apply to the spec-box entry point.)

## Part D — analysis groups always offer V and I; absent ⇒ empty-state

### D1. `TraceDataItem`
```csharp
public bool IsAbsent { get; init; }   // a V/I placeholder for an analysis group whose cube is missing
```

### D2. `RebuildSignals` — synthesize the missing V/I per analysis group
After the cube-build loops and **before** building `AvailableGroups`, ensure each analysis group has both
a `V` and an `I` item. An analysis group is one whose V/I item is qualified (`CubeName` contains `.`):
```csharp
foreach (var grpName in _allSignals.Select(s => s.Group).Distinct().ToList())
{
    var items = _allSignals.Where(s => s.Group == grpName).ToList();
    var vItem = items.FirstOrDefault(s => s.IsCubeBound && s.Label == "V" && (s.CubeName?.Contains('.') ?? false));
    var iItem = items.FirstOrDefault(s => s.IsCubeBound && s.Label == "I" && (s.CubeName?.Contains('.') ?? false));
    if (vItem is null && iItem is null) continue;          // not an analysis group
    var sample = (vItem ?? iItem)!;
    string prefix = sample.CubeName![..(sample.CubeName.IndexOf('.') + 1)];   // e.g. "HB1."
    if (vItem is null)
        _allSignals.Add(new TraceDataItem(sample.Entry, prefix + "V", Array.Empty<AxisSlice>(), "V")
                        { Group = grpName, IsAbsent = true });
    if (iItem is null)
        _allSignals.Add(new TraceDataItem(sample.Entry, prefix + "I", Array.Empty<AxisSlice>(), "I")
                        { Group = grpName, IsAbsent = true });
}
```
(`_allSignals` is cleared and rebuilt each call, so no duplication. `match` resolves an absent item by
`CubeName` like any other, so a trace previously bound to an absent `HB1.I` re-selects it.)

### D3. `TraceRowViewModel` — empty-state surface
```csharp
public bool   ShowEmptyQuantity   => SelectedSignal?.IsAbsent == true;
public string EmptyQuantityMessage =>
    SelectedSignal?.IsAbsent == true
        ? (SelectedSignal.Label == "I" ? "No branch currents" : "No node voltages")
        : "";
```
Raise both `nameof(ShowEmptyQuantity)` and `nameof(EmptyQuantityMessage)` wherever `SelectedSignal`
changes — in `OnSelectedSignalChanged` and in `RefreshDescription`.

In `OnSelectedSignalChanged`, short-circuit an absent pick so it doesn't try to bind/plot a missing cube
(place at the top, after the null/suppress guards):
```csharp
if (value?.IsAbsent == true)
{
    _trace.CubeName = value.CubeName;     // keep identity so the combo stays on this item
    _trace.Slice    = Array.Empty<AxisSlice>();
    AxisRoles.Clear();
    _parent.RebuildAndNotify();           // TrySetCubeData finds the cube absent ⇒ no points (graceful)
    OnPropertyChanged(nameof(ShowEmptyQuantity));
    OnPropertyChanged(nameof(EmptyQuantityMessage));
    return;
}
```
(Confirm `PlotInspectorViewModel.TrySetCubeData` clears points without throwing when
`!ds.Contains(t.CubeName)`; if it doesn't already, add that guard.)

### D4. AXAML — show the message in place of the axis editor
Gate the axis-role `ItemsControl` and show the message when empty:
```xml
<ItemsControl IsVisible="{Binding IsCubeBoundTrace}"      <!-- add: AND not ShowEmptyQuantity -->
              ItemsSource="{Binding AxisRoles}"> … </ItemsControl>
<TextBlock Classes="label"
           IsVisible="{Binding ShowEmptyQuantity}"
           Text="{Binding EmptyQuantityMessage}"
           Margin="0,2,0,0"/>
```
For the "AND not ShowEmptyQuantity" condition, expose a combined `bool ShowAxisRoles =>
IsCubeBoundTrace && !ShowEmptyQuantity;` on the VM and bind the `ItemsControl` `IsVisible` to it (raise it
alongside the others). Optionally also hide the spec row + line/symbol rows when `ShowEmptyQuantity`
(an absent quantity has nothing to style) — bind their `IsVisible` to `ShowAxisRoles` as well if it reads
cleaner.

## Tests — `tests/Ui.Tests`
1. **BranchPins_FilteredByProbeBranches:** `I` cube with probe branch `Ids` + device-port `M1:d`, plus
   `__ProbeBranches=[Ids]`; `ShowAll=false` → the branch row's `PinOptions` = `[Ids]`; `ShowAll=true` →
   includes `M1:d`. (Mirror of the existing node-filter test.)
2. **EyeRow_OnBranchAxis:** the branch (and node) `AxisRoleRowViewModel` has `IsFilterableLabelAxis=true`;
   the harmonic row has it false.
3. **SpecEdit_ResyncsCombos:** a trace on `HB1.V`; `CommitSpec("HB1.I[:, 1]")` (valid) → `SelectedGroup`
   stays `HB1`, `SelectedSignal.Label == "I"`, and the axis rows are the branch/harmonic of `I`.
4. **SpecEdit_Invalid_BestEffort:** `CommitSpec("mag(HB1.V) + bogus(")` → no throw; `SpecError` set;
   group/item combos unchanged; axis rows cleared.
5. **AnalysisGroup_OffersBothVI:** an HB run with **no** IProbe → group `HB1` item list contains both
   `V` and an `IsAbsent` `I`; selecting `I` sets `ShowEmptyQuantity=true`,
   `EmptyQuantityMessage == "No branch currents"`, and the trace plots nothing (no throw).
6. **V_NodeFilter_Unchanged:** the node-axis filter + node pin behavior is unchanged (regression).

## Gate (manual)
Single-tone HB with IProbe `Ids` + an SDD device + labeled nodes. Pick group `HB1`, item `I`: the branch
row lists `Ids`; the eye (right of the branch selector) reveals `M1:d`. Switch item to `V`: node row with
the same eye behavior. Edit the spec box to `HB1.I[:, 1]` and commit → the group/item combos and axis rows
follow. On a run with no IProbe, item `I` shows "No branch currents".

## On completion
Note in `src/Ui/DataDisplay/CLAUDE.md`: V and I are fully symmetric — one `V`/`node` and one `I`/`branch`
cube, each with a label-filtered pin row and a shared eye (`ShowAll`) sitting on that row; the spec box
reverse-syncs all combos on a valid edit (best-effort otherwise); analysis groups always offer V and I
with a "No node voltages"/"No branch currents" empty state. Supersedes the Brief-2 separate-cube branch
filter (now removed) and the identity-row eye.
