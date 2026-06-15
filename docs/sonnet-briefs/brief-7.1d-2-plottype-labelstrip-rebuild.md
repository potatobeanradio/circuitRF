# Sonnet Brief — Phase 7.1d-2 (follow-up): rebuild label strips on structural inspector changes

**Design:** `docs/design/data-display.md` §2.8 / 7.1d-2. Bug: switching PlotType (e.g. Smith → Table) in the
PlotInspector leaves the old Y-axis label strips on screen — a Table should have none. Files:
`DataDisplay/ViewModels/PlotInspectorViewModel.cs` and `DataDisplay/ViewModels/PlotContainerViewModel.cs`.

## Root cause (confirmed)
The previous fix made appearance changes (color/line/marker) re-render the **existing** strips via an
`AppearanceRevision` bump on `PlotNeedsRedraw`. But a PlotType change is **structural** — the strip *set* changes
(Smith/Polar have strips; Rect/Table have none) — so the strips must be **rebuilt**, not repainted, and a bump
can't remove them. `PlotInspectorViewModel.OnPlotTypeChanged` does `_plot.SetPlotType(value)` then fires only
`PlotNeedsRedraw`; the container forwards/bumps but never calls `UpdateLabelStrips()`. (Zoom "fixes" it only
because some later pass eventually rebuilds.) `PlotContainerViewModel.UpdateLabelStrips()` already does the right
thing — it `Clear()`s both collections and re-adds strips **only** when `plot.PlotType.IsComplex()`, so for Table
it clears and adds nothing. It just isn't being invoked on an inspector-driven structural change.

The same gap affects other structural edits that change the strip set: **add/remove trace** (count) and the
**→R secondary-axis toggle** (a trace moving left↔right should move its strip to the other side). Fix all of
them with one signal.

## Fix — a dedicated structural event (keep appearance changes on the cheap revision path)
1. **`PlotInspectorViewModel.cs`** — add an event next to `PlotNeedsRedraw`:
```csharp
public event EventHandler? PlotStructureChanged;
```
   Raise it (in addition to the existing `PlotNeedsRedraw?.Invoke(...)`) from each mutator that changes the strip
   set: `OnPlotTypeChanged`, `AddTrace`, `RemoveTrace`, `OnTraceSecondaryAxisChanged`, and `OnLibraryChanged`
   (which adds/removes/restores traces). A one-liner `PlotStructureChanged?.Invoke(this, EventArgs.Empty);`
   right where each already calls `PlotNeedsRedraw?.Invoke(...)`. **Do not** raise it from the appearance/text
   handlers (color/line/marker, `RebuildAndNotify`) — those stay on the revision-bump path so high-frequency
   slider drags don't trigger collection rebuilds/flicker.

2. **`PlotContainerViewModel.cs`** — in the constructor, right after the existing `Inspector.PlotNeedsRedraw += …`
   subscription, add:
```csharp
Inspector.PlotStructureChanged += (s, e) => UpdateLabelStrips();
```
   `UpdateLabelStrips()` (default `widthAndThemeOnly:false`) rebuilds `LeftLabelStrips`/`RightLabelStrips` from
   the current plot — clearing them for Rect/Table, repopulating for Smith/Polar, and re-deriving left/right
   membership so the →R toggle moves a strip to the correct side. These are all discrete user actions (not
   high-frequency), so a rebuild here is fine — no flicker concern.

Leave the `PlotNeedsRedraw` handler (curve repaint + `AppearanceRevision++`) exactly as it is — it still covers
color/description live refresh.

## Gate (verify in the running app)
1. Smith → **Table** via the Properties-dock inspector: the left/right Y-axis label strips **disappear**
   immediately (no zoom). Table → Smith: strips reappear. Smith → Rect: external strips clear (Rect uses in-canvas
   margin labels).
2. Add/Remove a trace: strip count updates live. Toggling **→R** (secondary axis) moves that trace's strip to the
   opposite side live.
3. Appearance changes (color/line width/marker) still update live with no flicker during slider drags (the prior
   fix is unregressed). Builds green; both inspector surfaces (flyout + Properties dock) behave identically.

## On completion
Note this under 7.1d-2 in `src/Ui/CLAUDE.md` (label strips now rebuild on structural inspector changes).
Next: **7.1d-3** (marker editor polish).
