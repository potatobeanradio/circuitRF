# Sonnet Brief — Phase 7.1d-2: Plot Inspector in the Properties dock (dual surface)

**Design:** `docs/design/data-display.md` §2.8 / 7.1d-2. **Scope: host the SAME `PlotInspectorView` in the
Properties dock as a fourth context, mirroring `IsSymbolEditorActive`/`IsCellActive`.** When a Data Display
document is active and a single plot is selected, the Properties dock shows that plot's inspector; edits there
redraw the plot live and mirror the in-document flyout (same VM instance). No inspector redesign (it's owner-
approved); no `PlotControl` / persistence / MarkerEditor (7.1d-3) changes.

## Context — the existing pattern (reuse exactly)
`PropertiesTool` (`src/Ui/ViewModels/Dock/PropertiesTool.cs`) has three mutually-exclusive contexts, each with
an `IsXActive` flag, a VM, and a `SetActiveX(...)` setter called by `WorkspaceViewModel` on active-document
change; `IsSchematicContextActive => !IsSymbolEditorActive && !IsCellActive` is the fallback.
`PropertiesView.axaml` overlays `Panel`s gated by those flags. `WorkspaceViewModel.OnDocumentDockPropertyChanged`
routes the active dockable to `SetActiveSymbolEditor` / `SetActiveCell` / `SetActiveSchematic`.
The Data Display document VM exposes `doc.ViewModel.Window` (`DisplayWindowViewModel`) whose **`ActiveInspector`**
(`PlotInspectorViewModel?` = the single-selected plot's inspector) **raises `PropertyChanged`** when the plot
selection changes (it does `OnPropertyChanged(nameof(ActiveInspector))` on tab/selection change).

## 1. `PropertiesTool` — add the Data Display context
- Add `[ObservableProperty] private bool _isDataDisplayActive;`
- Add `[ObservableProperty] private PlotInspectorViewModel? _plotInspectorVm;`
  (`using CircuitRF.Ui.DataDisplay.ViewModels;`)
- Update `IsSchematicContextActive => !IsSymbolEditorActive && !IsCellActive && !IsDataDisplayActive;`
  and add `partial void OnIsDataDisplayActiveChanged(bool _) => OnPropertyChanged(nameof(IsSchematicContextActive));`
- Add the setter, mirroring the others (clears the other contexts):
```csharp
public void SetActiveDataDisplay(PlotInspectorViewModel? vm)
{
    IsCellActive         = false;
    IsSymbolEditorActive = false;
    IsDataDisplayActive  = vm is not null;
    CellEditorVm         = null;
    PlotInspectorVm      = vm;
    EditorVm.SetContext(null);
    SymbolInspectorVm.SetContext(null);
    HeaderText = vm is not null ? "Plot" : "Properties";
}
```
Also have the other three setters (`SetActiveSchematic/Symbol/Cell`) set `IsDataDisplayActive = false` and
`PlotInspectorVm = null` so switching away clears the plot context.

## 2. `PropertiesView.axaml` — host the inspector
Add a branch alongside the existing ones (and the inspector is 430-wide and fixed, so wrap it so a narrow dock
doesn't clip it):
```xml
<Panel IsVisible="{Binding IsDataDisplayActive}">
    <ScrollViewer HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Auto">
        <ddv:PlotInspectorView DataContext="{Binding PlotInspectorVm}"/>
    </ScrollViewer>
</Panel>
```
Add `xmlns:ddv="using:CircuitRF.Ui.Views.DataDisplay"`. **Keep `PlotInspectorView`'s `Width="430"` unchanged**
(don't touch the owner-approved flyout layout) — the horizontal `ScrollViewer` lets a narrow Properties dock
scroll, and the dock is user-resizable to ~430. (Trade-off: when the dock is wider than 430 there's blank
space to the right; acceptable. A width-flexible inspector is a later refinement — out of scope.)

## 3. `WorkspaceViewModel` — route + subscribe (the in-document-selection wiring)
Unlike symbol/cell/schematic (which only change on document activation), the plot inspector changes with the
**in-document plot selection**, so subscribe to the active Data Display window's `ActiveInspector`.
- Add fields: `private DisplayWindowViewModel? _subscribedDisplayWindow;` and a
  `System.ComponentModel.PropertyChangedEventHandler? _displayInspectorHandler;`
- Add a router:
```csharp
private void RouteDataDisplayProperties(DataDisplayDocument? dd)
{
    if (_subscribedDisplayWindow is not null && _displayInspectorHandler is not null)
        _subscribedDisplayWindow.PropertyChanged -= _displayInspectorHandler;
    _subscribedDisplayWindow = null;

    if (dd is null) { _factory.PropertiesTool?.SetActiveDataDisplay(null); return; }

    var window = dd.ViewModel.Window;
    _subscribedDisplayWindow = window;
    _displayInspectorHandler ??= (_, e) =>
    {
        if (e.PropertyName is nameof(DisplayWindowViewModel.ActiveInspector))
            _factory.PropertiesTool?.SetActiveDataDisplay(_subscribedDisplayWindow?.ActiveInspector);
    };
    window.PropertyChanged += _displayInspectorHandler;
    _factory.PropertiesTool?.SetActiveDataDisplay(window.ActiveInspector);
}
```
- In `OnDocumentDockPropertyChanged`, branch on `DataDisplayDocument` **before** the symbol/cell/schematic
  routing, and call `RouteDataDisplayProperties(null)` on every non-DataDisplay path so it unsubscribes + clears:
```csharp
if (activeDockable is DataDisplayDocument ddDoc)
{
    RouteDataDisplayProperties(ddDoc);
}
else
{
    RouteDataDisplayProperties(null);
    // …existing SetActiveSymbolEditor / SetActiveCell / SetActiveSchematic routing…
}
```
- **Guard tree-selection clobber:** in `OnProjectTreeSelectionChanged`, the non-cell branch calls
  `SetActiveCell(null)` unless a cell document is active — extend that guard so it also does **not** clear the
  inspector when `_factory.DocumentDock?.ActiveDockable is DataDisplayDocument` (otherwise clicking a tree node
  while a Data Display is active would wipe the plot inspector).

## Behavior notes
- Both surfaces bind the **same** `PlotInspectorViewModel` (`window.ActiveInspector`), so an edit in the dock
  updates the VM → redraws the plot → and the flyout (if open) shows the same values. That's the dual-surface goal.
- No plot selected (ActiveInspector null) while a Data Display is active → `IsDataDisplayActive=false` →
  fallback placeholder ("Select object to inspect…"); header "Properties". Acceptable.
- The inspector's "Close" button still appears in the dock (it deselects/closes the flyout). Hiding it in the
  dock surface is a minor later polish — **out of scope** here.

## Gate (acceptance)
1. Builds green. Selecting a single plot in an active Data Display shows **its** `PlotInspectorView` in the
   Properties dock (header "Plot"); selecting a different plot swaps the inspector; deselecting shows the
   placeholder.
2. Editing in the dock inspector (line/symbol/color/axes/etc.) **redraws the plot live** and the in-document
   flyout reflects the same change (same VM) — and vice-versa.
3. Switching the active tab to a schematic / symbol / cell document restores those Properties contexts; clicking
   a tree node while a Data Display is active does **not** wipe the plot inspector.
4. No regression to schematic/symbol/cell Properties behavior; a narrow Properties dock scrolls rather than
   clipping the 430-wide inspector.

## On completion
Tick the 7.1d-2 bullet in `docs/design/data-display.md`; note "Phase 7.1d-2 — COMPLETE" in `src/Ui/CLAUDE.md`;
report build + a screenshot of a plot's inspector in the Properties dock with the flyout open showing the same
state. Next: **7.1d-3** (marker editor polish).
