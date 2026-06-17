# Sonnet Brief — Flyout PlotInspector edits not reflected in the Properties-pane inspector

## The bug
Editing a plot's trace card in the **flyout** PlotInspector (right-click plot → Plot Properties…, or
double-tap) and closing it does NOT update the **Properties pane** inspector for that plot. The plot
itself redraws correctly; only the Properties-pane fields stay stale.

## Root cause — two VM instances over one Plot
The Properties pane binds to the container's **owned** inspector:
`DataDisplayViewModel.ActiveInspector => SingleSelectedPlot?.Inspector` → `PropertiesTool.PlotInspectorVm`.
That is the single `PlotInspectorViewModel` created in `DataDisplayViewModel.AddPlot` /
`LoadPlotContainerConfigAsync` and held by `PlotContainerViewModel.Inspector`.

But the flyout makes a **brand-new** instance every time it opens
(`PlotControl.ShowPlotInspector`):
```csharp
_inspectorVm = new PlotInspectorViewModel(_plot, () => _inspectorFlyout?.Hide(), _library);
```
Both VMs share the same `Plot` model, so flyout edits reach the model (and redraws fire), but the flyout
VM's `Traces` row-VMs and observable properties are a *separate* projection. The container's inspector
(= the Properties-pane VM) is never told to re-read the model, so its fields don't change.

## Fix — flyout reuses the container's inspector (one VM per plot)
Make the flyout reuse `PlotContainerViewModel.Inspector` instead of constructing a new VM. Then there is
exactly one `PlotInspectorViewModel` per plot, bound by both the flyout view and the Properties-pane view
simultaneously — edits in either surface raise `PropertyChanged`/collection-changed and both update live
(and on close the pane is already current).

`PlotControl` already has a handle to the container via `ContainerProvider`
(`= () => DataContext as PlotContainerViewModel`, set in `PlotContainerView.OnDataContextChanged`), so it
can fetch the shared inspector with `ContainerProvider?.Invoke()?.Inspector`.

### Step 1 — make the inspector's close action settable
`PlotInspectorViewModel` currently stores a readonly `_closeAction` and binds `CloseCommand` to it. The
container builds the inspector with a no-op close (`() => { }`); the flyout needs the Close button to hide
the flyout while it's open. Make the close action mutable and invoke it indirectly.

In `PlotInspectorViewModel`:
```csharp
// was: private readonly Action _closeAction;
private Action _closeAction;
```
Constructor stays the same (assigns `_closeAction = closeAction;`), but build the command to call through
the field so a later reassignment takes effect:
```csharp
// was: CloseCommand = new RelayCommand(_closeAction);
CloseCommand = new RelayCommand(() => _closeAction());
```
Add a setter:
```csharp
/// <summary>Sets the action invoked by CloseCommand. The flyout points this at flyout.Hide()
/// while open and restores the no-op on close, so the single shared inspector can be driven by
/// both the flyout and the Properties pane.</summary>
public void SetCloseAction(Action closeAction) => _closeAction = closeAction;
```
(Keep the existing `_library`/`_plot` fields as they are.)

### Step 2 — reuse the inspector in the flyout
In `PlotControl.ShowPlotInspector(int scrollToTraceIndex = -1)`, replace the `new PlotInspectorViewModel`
with the container's instance, and route close through `SetCloseAction`. Current head:
```csharp
private void ShowPlotInspector(int scrollToTraceIndex = -1)
{
    if (_plot is null) return;
    _inspectorFlyout?.Hide();

    _inspectorVm = new PlotInspectorViewModel(_plot, () => _inspectorFlyout?.Hide(), _library);
    _inspectorVm.PlotNeedsRedraw += OnInspectorPlotNeedsRedraw;

    var view = new PlotInspectorView { DataContext = _inspectorVm };
    _inspectorView = view;
    …
    _inspectorFlyout.Closed += (_, _) =>
    {
        if (_inspectorVm is not null)
            _inspectorVm.PlotNeedsRedraw -= OnInspectorPlotNeedsRedraw;
    };
    _inspectorFlyout.ShowAt(flyoutAnchor);
    …
}
```
Replace with:
```csharp
private void ShowPlotInspector(int scrollToTraceIndex = -1)
{
    if (_plot is null) return;
    _inspectorFlyout?.Hide();

    // Reuse the container's single inspector so flyout edits and the Properties-pane inspector
    // stay in sync (one VM per plot). Fall back to a fresh VM only if no container is wired
    // (e.g. PlotControl used outside PlotContainerView).
    _inspectorVm = ContainerProvider?.Invoke()?.Inspector
                   ?? new PlotInspectorViewModel(_plot, () => { }, _library);

    // Point the shared inspector's Close button at this flyout while it is open.
    _inspectorVm.SetCloseAction(() => _inspectorFlyout?.Hide());
    _inspectorVm.PlotNeedsRedraw += OnInspectorPlotNeedsRedraw;

    var view = new PlotInspectorView { DataContext = _inspectorVm };
    _inspectorView = view;

    var (flyoutAnchor, hOffset, vOffset) = ComputeStableAnchor(
        new Point(Bounds.Width, 0), PlacementMode.RightEdgeAlignedTop);

    _inspectorFlyout = new Flyout
    {
        Content              = view,
        Placement            = PlacementMode.RightEdgeAlignedTop,
        HorizontalOffset     = hOffset,
        VerticalOffset       = vOffset,
        ShowMode             = FlyoutShowMode.Standard,
        OverlayInputPassThroughElement = this,
    };

    _inspectorFlyout.Closed += (_, _) =>
    {
        if (_inspectorVm is not null)
        {
            _inspectorVm.PlotNeedsRedraw -= OnInspectorPlotNeedsRedraw;
            // Restore a no-op close so a stale flyout reference is never invoked by the
            // Properties-pane Close button (the pane has no Close button today, but be safe).
            _inspectorVm.SetCloseAction(() => { });
        }
    };

    _inspectorFlyout.ShowAt(flyoutAnchor);

    if (scrollToTraceIndex >= 0)
        view.ScrollToTrace(scrollToTraceIndex);
}
```

Notes / why this is safe:
- **Redraws:** `PlotContainerViewModel` already subscribes `Inspector.PlotNeedsRedraw` and
  `PlotContainerView` wires `vm.PlotNeedsRedraw → _plotControl.InvalidateVisual()`, so the plot redraws on
  inspector edits even without the flyout's own handler. Keeping `OnInspectorPlotNeedsRedraw` (which also
  raises `PlotChanged`) is fine; we only add/remove our own delegate, never the container's.
- **One VM, two views:** Avalonia allows a single VM bound to two `PlotInspectorView`s at once. The shared
  `Traces` ObservableCollection and observable props drive both. While both are visible, edits in the
  flyout update the pane live; on close the pane is already current — fixing the reported bug.
- **Two `PlotInspectorView` DataTemplates / `ControlTheme`:** the IconSelectButton `ControlTheme` lives in
  `PlotInspectorView.axaml`'s `UserControl.Resources`, so each view instance carries its own copy — no
  conflict from two live views.
- **`view.ScrollToTrace` / `FocusSpecTextBox`** operate on the view, unaffected by VM reuse.
- Do **not** dispose or clear the container inspector on flyout close — it lives for the container's
  lifetime and the Properties pane keeps using it.

## STOP-and-verify before building
- Confirm `PlotInspectorViewModel` has no other place that captured `_closeAction` by value (only
  `CloseCommand` should use it). After the change the field is non-readonly; ensure nothing else breaks.
- Confirm `PlotContainerViewModel.Inspector` is a public get (it is) and is the same instance fed to
  `PropertiesTool.SetActiveDataDisplay` via `ActiveInspector` (it is:
  `ActiveInspector => SingleSelectedPlot?.Inspector`).

## Gate / manual checks (build 0W/0E)
1. Select a plot so the Properties pane shows its inspector. Right-click the plot → Plot Properties…
   (flyout opens). In the flyout: change a trace's color, line style, transform, add a trace, change plot
   type. The Properties-pane inspector reflects every change **live** (and certainly after closing the
   flyout).
2. Flyout Close button still hides the flyout.
3. Reverse: edit in the Properties pane → the open flyout (if open) reflects it.
4. Add/remove trace in the flyout → pane's trace list count matches; no duplicate or orphaned rows.
5. Plot still redraws on every flyout edit (no regression).
6. Open/close the flyout repeatedly → no leak/exception; Close action always hides the current flyout and
   never throws after close.

If an existing test constructs the flyout VM directly, it still works (the fallback `new
PlotInspectorViewModel(...)` path is preserved when `ContainerProvider` is null).
