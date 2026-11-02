# Sonnet Brief 4b — unified "Show all" toggle + bare measurement names in the picker

Two small Data Display picker refinements:

1. **Collapse** the two per-trace toggles ("Show all nodes" + "Show all branches") into one generic
   **"Show all"** that drives both the node-axis pin filter and the branch-current list filter.
2. **Emit bare names** for measurements- and default-group cubes in the signal picker, so selecting a
   measurement yields `PDC` (not `measurements.PDC`) — matching what the user types and upholding the
   rule that picker-only use always produces a valid expression. Analysis cubes stay qualified
   (`HB1.V`, `DC1.I:Ids`) because a bare `V` would resolve to the wrong group.

Prereq: bare-name resolution (Brief 4a + `DataSet.BareResolve` measurements support) is already on disk.
Read first: `ViewModels/TraceRowViewModel.cs` (the `_showAllNodes` / `_showAllBranches` members,
`RebuildSignals`, `RebuildAxisRolesCore`, `OnSelectedSignalChanged`, `RefreshDescription`, `CommitSpec`)
and `Views/DataDisplay/PlotInspectorView.axaml` (the two existing checkboxes). Build 0W/0E; tests green.

## Part 1 — merge the toggles (`TraceRowViewModel.cs`)

### 1a. Replace the two observable toggles with one
Delete `_showAllNodes` + `OnShowAllNodesChanged` and `_showAllBranches` + `OnShowAllBranchesChanged`, and add:
```csharp
// Unified "Show all" — reveals unlabeled nodes (axis-pin filter) AND device-port currents
// (branch-list filter). Default false ⇒ both filters ON (user-labeled nodes + IProbe currents only).
[ObservableProperty]
private bool _showAll;

partial void OnShowAllChanged(bool value)
{
    if (_rebuildingAxisRoles) return;   // default is set during an axis rebuild — avoid re-entry
    RebuildSignals();                   // re-filters the branch list AND (at its tail) calls
                                        // RebuildAxisRoles → re-applies the node-axis filter
}
```
(`RebuildSignals` already ends by calling `RebuildAxisRoles`, so one call refreshes both filters.)

### 1b. Keep the two visibility predicates; add a unified one
`ShowAllNodesToggleVisible` and `ShowAllBranchesToggleVisible` don't depend on toggle *state* — leave them
as-is. Add:
```csharp
/// <summary>True when the unified "Show all" toggle is relevant — the cube has a filterable node axis
/// OR a source has device-port currents hidden behind the IProbe filter.</summary>
public bool ShowAllToggleVisible => ShowAllNodesToggleVisible || ShowAllBranchesToggleVisible;
```

### 1c. Point the filter reads at `ShowAll`
- In `RebuildSignals`, the branch filter `if (!ShowAllBranches && probeSet is not null …)` → `if (!ShowAll && probeSet is not null …)`.
- In `RebuildAxisRolesCore`:
  - `if (labeledSet is null && !ShowAllNodes) ShowAllNodes = true;` → `if (labeledSet is null && !ShowAll) ShowAll = true;`
  - `bool showAll = ShowAllNodes;` → `bool showAll = ShowAll;`
  - The line `OnPropertyChanged(nameof(ShowAllNodesToggleVisible));` (raised when `_hasNodeAxis` flips) →
    also raise `OnPropertyChanged(nameof(ShowAllToggleVisible));` (keep or replace the node one; the
    bound control uses the unified property).

### 1d. Update the change-notification raises
Everywhere the old toggle-visibility properties are re-raised — in `OnSelectedSignalChanged`,
`RefreshDescription`, and `CommitSpec` (each currently raises `ShowAllNodesToggleVisible` and/or
`ShowAllBranchesToggleVisible`) — raise `nameof(ShowAllToggleVisible)` instead (or in addition). The
single bound control reads `ShowAllToggleVisible`.

## Part 2 — bare emission for measurements/default (`RebuildSignals`)

In the cube-signal loop, the qualified-name line:
```csharp
string qualified = group == DataSet.DefaultGroup ? bareName : $"{group}.{bareName}";
```
becomes:
```csharp
// Default- and measurements-group cubes are bare-resolvable (DataSet.BareResolve) — emit their bare
// name so the picker yields `PDC`/`V`, matching typed input. Analysis cubes must stay qualified
// (bare `V` would resolve to the default/measurements group, not the analysis).
string qualified =
    (group == DataSet.DefaultGroup || group == DataSet.MeasurementsGroup)
        ? bareName
        : $"{group}.{bareName}";
```
This flows through unchanged to `CubeName`, the `Label` (`$"{filePrefix}{qualified}"`), the selection
match (`s.CubeName == _trace.CubeName`), `OnSelectedSignalChanged` (`_trace.CubeName = value.CubeName`),
and `TrySetCubeData` (`ds[t.CubeName]` resolves bare via `BareResolve`). No other edits needed.

Back-compat note (alpha, fine): a previously-saved plot storing `measurements.PDC` still resolves via the
qualified path — it just may not pre-select in the combo until re-picked. No breakage; do not add a
migration shim.

## Part 3 — AXAML (`PlotInspectorView.axaml`)
Find the two checkboxes added by the IProbe/scalars work:
- `Content="Show all nodes"`, `IsChecked="{Binding ShowAllNodes}"`, `IsVisible="{Binding ShowAllNodesToggleVisible}"`
- `Content="Show all branches"`, `IsChecked="{Binding ShowAllBranches}"`, `IsVisible="{Binding ShowAllBranchesToggleVisible}"`

Replace both with a single checkbox (keep the same styling/placement as the old "Show all nodes" one):
```xml
<CheckBox Content="Show all"
          IsChecked="{Binding ShowAll}"
          IsVisible="{Binding ShowAllToggleVisible}"/>
```

## Tests — `tests/Ui.Tests`
1. **Measurements_EmitBare:** a grouped source with a `measurements` cube `PDC` → `AvailableSignals`
   contains an item whose `CubeName == "PDC"` (not `"measurements.PDC"`); its `Label` has no
   `measurements.` prefix.
2. **Analysis_StaysQualified:** an `HB1` group cube `V` → the item's `CubeName == "HB1.V"`.
3. **ShowAll_RevealsBranchesAndNodes:** with a source that has both `__ProbeBranches` (hidden device-port
   current) and `__LabeledNodes` (unlabeled node), `ShowAll=false` hides both; `ShowAll=true` reveals the
   device-port current in `AvailableSignals` AND the unlabeled node in the selected cube's `AxisRoles`
   node pin options.
4. **ShowAllToggleVisible_Or:** true when either a node axis or a hideable device-port current is present;
   false for a probe-only scalar source with no node axis.
5. **PickedMeasurement_Resolves:** selecting the bare `PDC` item binds and renders on a Table (no
   expression error), confirming bare `CubeName` resolves end-to-end.

## Gate (manual)
Open a Data Display on a single-tone HB run that has an IProbe, labeled nodes, and a `PDC` measurement.
The trace card shows one **"Show all"** checkbox (not two). Unchecked: the current list shows only the
IProbe current and the node pin shows only labeled nodes; checked: device-port currents and all nodes
appear. The measurement picks as `PDC` (the spec box reads `PDC`, not `measurements.PDC`).

## On completion
Note in `src/Ui/DataDisplay/CLAUDE.md`: the per-trace node/branch visibility is one unified `ShowAll`
toggle; the picker emits bare names for default- and measurements-group cubes (analysis cubes stay
qualified). Grouped (Analysis→quantity) combo presentation is tracked separately.
