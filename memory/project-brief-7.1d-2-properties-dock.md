---
name: project-brief-7.1d-2-properties-dock
description: Brief 7.1d-2: PlotInspectorView in Properties dock as 4th context — completed 2026-06-14
metadata:
  type: project
---

Phase 7.1d-2 — COMPLETE (2026-06-14)

PlotInspectorView hosted in the Properties dock as a fourth mutually-exclusive context alongside
schematic/symbol/cell inspectors.

**Why:** dual-surface goal — same PlotInspectorViewModel instance appears in both the in-document
flyout and the Properties dock, so edits in either host redraw the plot and both views stay in sync.

**Files changed:**
- `src/Ui/ViewModels/Dock/PropertiesTool.cs`: added `IsDataDisplayActive` + `PlotInspectorVm`
  observable properties; updated `IsSchematicContextActive` to guard all four contexts;
  added `SetActiveDataDisplay(PlotInspectorViewModel?)` setter; all other setters clear
  `IsDataDisplayActive = false` and `PlotInspectorVm = null`.
- `src/Ui/Views/Properties/PropertiesView.axaml`: added `xmlns:ddv` namespace;
  added `Panel IsVisible="{Binding IsDataDisplayActive}"` wrapping `ScrollViewer` +
  `ddv:PlotInspectorView DataContext="{Binding PlotInspectorVm}"`.
- `src/Ui/ViewModels/WorkspaceViewModel.cs`: added `_subscribedDisplayWindow` /
  `_displayInspectorHandler` fields; added `RouteDataDisplayProperties(DataDisplayDocument?)`
  which subscribes to `DisplayWindowViewModel.ActiveInspector` PropertyChanged; updated
  `OnDocumentDockPropertyChanged` to branch on `DataDisplayDocument` first; updated
  `OnProjectTreeSelectionChanged` guard to also skip when `ActiveDockable is DataDisplayDocument`.

**How to apply:** Next is 7.1d-3 (marker editor polish). [[project-brief-7.1d-1-polish]]
