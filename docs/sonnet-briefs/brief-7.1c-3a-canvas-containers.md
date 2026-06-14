# Sonnet Brief — Phase 7.1c-3a: the real canvas + containers, wired into the document

**Design:** `docs/design/data-display.md` §3 → "7.1c" → "7.1c-3a". Read that. **Scope: replace the 7.1b/7.1c-2
single-`PlotControl` harness with splotRF's real per-tab canvas of draggable/resizable plot containers,
wired into the Data Display document — single tab, no tab strip / toolbar / library yet** (those are 3b).
Faithful copy-port + one VM reconciliation + a temporary demo-plot seed. **Do not redesign.**

## What ships in 3a
A Data Display document shows a **canvas** where you add / move / resize / select / delete plots; each plot
renders via the interactive `PlotControl` (7.1c-2); pan (middle-drag) / scroll-zoom / drag-select work;
markers add/move and show info boxes; the inspector flyout (double-tap) edits live.

## The port

### 1. `PlotContainerView` → `src/Ui/Views/DataDisplay/PlotContainerView.axaml(.cs)`
Copy splotRF `src/Views/PlotContainerView.axaml(.cs)` → namespace `CircuitRF.Ui.Views.DataDisplay`; retarget
xmlns (`vm`→`CircuitRF.Ui.DataDisplay.ViewModels`, `cc`→`CircuitRF.Ui.DataDisplay.Controls`). The code-behind
already **wires the container providers** in `OnDataContextChanged`
(`NextMarkerIndexProvider`/`FindMarkerInfoBoxVmProvider`/`ContainerProvider`/`SelectedMarkersProvider`) and
the move/resize/select handlers — keep all of it. **One rename to fix:** it walks the visual tree for
`DataDisplayView` (in `OnResizeHandleDoubleTapped`) — change that to **`PlotCanvasView`** (see §2).

### 2. splotRF per-tab canvas → **rename to `PlotCanvasView`** → `src/Ui/Views/DataDisplay/PlotCanvasView.axaml(.cs)`
Copy splotRF `src/Views/DataDisplayView.axaml(.cs)` but **rename the type `DataDisplayView` → `PlotCanvasView`**
(`x:Class`, ctor, code-behind class) to avoid colliding with circuitRF's document-level `DataDisplayView`
(§4). Namespace `CircuitRF.Ui.Views.DataDisplay`; retarget xmlns (`v`→`CircuitRF.Ui.Views.DataDisplay`,
`vm`→`CircuitRF.Ui.DataDisplay.ViewModels`, `controls`→`CircuitRF.Ui.DataDisplay.Controls`). `x:DataType`
stays `TabViewModel`; it hosts the `ItemsControl`+`Canvas` of `PlotContainerView`s, the `MarkerInfoBoxView`
overlay (ported 7.1c-2), and `DragSelectOverlay`. Keep the interaction code-behind verbatim (middle-pan /
drag-select / scroll-zoom / background-deselect; `GetCanvasSizeFunc` registration).

### 3. VM reconciliation — document VM owns the window VM
In `DataDisplayDocumentViewModel` (`src/Ui/DataDisplay/`):
- Add `public DisplayWindowViewModel Window { get; }` (construct it). **Wrap, don't merge** — leave the
  ported `DisplayWindowViewModel` intact.
- Ensure `Window` has **one tab with `ActiveTab` set** after construction (if the ported
  `DisplayWindowViewModel` ctor doesn't already create an initial `TabViewModel` + set `ActiveTab`, add that).
- **Remove the 7.1b demo harness** (`CurrentPlot` / `HasPlots` / `InsertDemoPlot[Command]`).
- Keep/forward `IsDirty` (from `Window`'s unsaved tracking if present, else leave the existing field).

### 4. Rewrite circuitRF's document `DataDisplayView` to host the active canvas
`src/Ui/Views/DataDisplay/DataDisplayView.axaml(.cs)` (the document chrome, bound to `DataDisplayDocument`):
- Replace the single-`PlotControl` + demo button body with the **active tab's** canvas:
  `<v:PlotCanvasView DataContext="{Binding ViewModel.Window.ActiveTab}"/>` filling the area (with
  `xmlns:v="using:CircuitRF.Ui.Views.DataDisplay"`).
- Add a **temporary "Add Plot" button** bound to `ViewModel.Window.AddPlotCommand` (no toolbar until 3b) so
  the canvas can be populated for the gate. Mark `<!-- TEMP 3a — replaced by the toolbar in 3b -->`.
- Remove the `DoubleTapped`/`HandleDoubleTapAt` harness wiring (the container view owns that now).
- Keep `x:DataType="dd:DataDisplayDocument"`.

### 5. Temporary demo-plot seed (no library until 3b)
Without the SnpLibrary, `AddPlot` has no data to start from. Seed **one demo plot** into the first tab's
canvas so the canvas/markers/inspector are exercisable:
- Reuse the 7.1b synthetic builder (2-port SNP + Rect S21-dB trace). Wrap it as a `PlotContainerViewModel`
  (with its `PlotViewModel` + `PlotInspectorViewModel`) and add it to the tab's `DataDisplayViewModel.Plots`
  via the canvas VM's existing add-plot path, with a sensible starting `Left/Top/Width/Height`.
- Put this behind a temp `SeedDemoPlot()` called once on document construction (or the temp Add-Plot button
  can call it the first time). Mark `// TEMP 3a — removed when SnpLibrary + Load Touchstone land in 3b`.
- After the seed, `AddPlot` clones the last plot (splotRF behavior), so further plots work.

### 6. App.axaml
The `DataDisplayDocument → DataDisplayView` DataTemplate is unchanged (only the view's content changed).

## Scope guardrails (do NOT do in 3a)
- No tab strip / `TabControl` / `TabHeaderView` (3b). One tab only.
- No in-document toolbar, no `KeyBindings`, no `SnpLibraryView` panel, no Load Touchstone (3b).
- No docked inspector panel (3b) — the flyout from 7.1c-2 is how you reach the inspector in 3a.
- No `.cdd` (7.1e), no DataSet (7.2), no inspector restyle (7.1d). Everything stays in `src/Ui`.

## Gate (acceptance)
1. Builds green (`TreatWarningsAsErrors`); no `splotRF` namespace remains; no `DataDisplayView`/`PlotCanvasView`
   name confusion.
2. New Data Display opens with one canvas + the seeded demo plot. **Add Plot** adds another; plots can be
   **moved, resized, selected (incl. drag-select rubber-band), and deleted**.
3. Canvas **middle-drag pans**, **scroll zooms**; a plot's **double-click opens the inspector flyout** and
   edits redraw live; changing the plot's type to **Smith/Polar/Table** via the inspector renders correctly.
4. **Markers**: double-click/right-click a trace adds a marker; it shows an **info box** on the canvas and
   can be dragged. (Provider wiring via `PlotContainerView` is now live.)
5. Tear-off / re-dock / close still work; no schematic regression.

## On completion
One-line "Phase 7.1c-3a — COMPLETE" note to `src/Ui/CLAUDE.md` (real canvas + containers + provider wiring;
`PlotCanvasView` rename; document VM wraps `DisplayWindowViewModel`; harness removed; demo seed TEMP). Report
the build result + a description/screenshot of the canvas. Next: **7.1c-3b** (tabs + toolbar + SnpLibrary +
Load Touchstone + docked inspector).
