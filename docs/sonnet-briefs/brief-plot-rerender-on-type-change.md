# Sonnet Brief — Plot re-renders fully on plot-type change (Table→Smith), not just after a zoom

**Bug:** Changing a plot's type in the PlotInspector (e.g. Table → Smith) doesn't re-render the plot's
**selection outline highlight** or its **size** until the user zooms the data display. Zooming "fixes" it.

**Root cause (confirmed).** `PlotInspectorViewModel.OnPlotTypeChanged` fires two events:
`PlotNeedsRedraw` (→ the view calls `InvalidateVisual` on its `PlotControl`) and `PlotStructureChanged`. In
`PlotContainerViewModel` the structure handler is:
```csharp
Inspector.PlotStructureChanged += (s, e) => UpdateLabelStrips();
```
But the container's **screen-layout properties** — `ViewWidth`, `ViewHeight`, `ViewTop`, `ViewLeft`,
`ViewTotalWidth`, `ViewContainerLeft`, `LabelStripViewWidth`, `LabelStripMargin` — and `IsSquareAspect` all
depend on the plot type (complex Smith/Polar vs Rect/Table change aspect, the `Top/BottomLabelExtraLogical`
canvas padding, and square-vs-free sizing). On a plot-type change, nothing re-raises the full `View*` set:
- `OnWidthChanged`/`OnHeightChanged` re-raise them only when `Width`/`Height` actually change (a resize), and
- `NotifyViewProperties()` re-raises the full set only on zoom/offset change — **which is exactly why zooming
  fixes it.**

`UpdateLabelStrips()` raises a *partial* subset (`ViewHeight`, `ViewTop`, `LabelStripMargin`, `ViewTotalWidth`,
`ViewContainerLeft`) but not `ViewWidth`, `IsSquareAspect`, etc., so the container bounds / selection outline
stay stale until the next zoom.

## Fix — `src/Ui/DataDisplay/ViewModels/PlotContainerViewModel.cs`
Make the plot-type/structure change re-notify the full view layout immediately. In the constructor, change the
`Inspector.PlotStructureChanged` subscription to:
```csharp
Inspector.PlotStructureChanged += (s, e) =>
{
    UpdateLabelStrips();                       // rebuild strips for the new type (Table has none, etc.)
    OnPropertyChanged(nameof(IsSquareAspect)); // aspect flips Table/Rect ↔ Smith/Polar
    NotifyViewProperties();                    // re-raise ALL View* size props now — not on next zoom
};
```
`NotifyViewProperties()` already raises `ViewLeft/ViewTop/ViewWidth/ViewHeight/ViewTotalWidth/
ViewContainerLeft/LabelStripViewWidth/LabelStripMargin/ZoomLevel` and refreshes strips in place — it's the same
call the zoom path uses, so this makes a type change behave like the zoom that currently "fixes" the layout.
`PlotNeedsRedraw` continues to fire separately (→ `PlotControl.InvalidateVisual`) so the Skia plot content
redraws too.

**Verify the selection outline:** if the outline highlight is container chrome bound to the `View*` bounds (most
likely), `NotifyViewProperties()` refreshes it. If instead it's drawn inside `PlotControl`'s render, the existing
`PlotNeedsRedraw → InvalidateVisual` already covers it. Confirm one of those two paths refreshes the outline; if
neither does (e.g. a separate adorner that observes neither), also invalidate that adorner on the structure
change. Do **not** introduce a new full-canvas relayout pass — the `NotifyViewProperties` call is the minimal fix.

Don't touch `PlotInspectorViewModel` (it already fires the right events) or the zoom path.

## Test (`tests/Ui.Tests` or the DataDisplay VM test project — headless, no rendering needed)
**`PlotTypeChange_RaisesViewLayoutNotifications`**: construct a `PlotContainerViewModel` with a Table plot;
subscribe to its `PropertyChanged`; set the inspector's `PlotType` to `Smith` (via
`SetPlotTypeSmithCommand`/`PlotType` setter). Assert `PropertyChanged` was raised for `ViewWidth`, `ViewHeight`,
and `IsSquareAspect` (i.e. the full layout refreshed without a zoom). A regression guard that the type change no
longer depends on a subsequent `NotifyViewProperties`/zoom.

## Gate
Build 0W/0E; test green. Manually: select a plot, switch Table→Smith in the inspector → the plot resizes to the
square Smith layout and the selection outline redraws immediately, with no zoom needed. Smith→Table and the other
transitions likewise update at once.
