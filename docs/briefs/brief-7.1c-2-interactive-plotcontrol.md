# Sonnet Brief — Phase 7.1c-2: the interactive PlotControl + its flyout/overlay views

**Design:** `docs/design/data-display.md` §3 → "7.1c" → "7.1c-2". Read that. **Scope: replace the 7.1b
render-only `PlotControl` with splotRF's full interactive `PlotControl`, plus the views/controls it
directly references, so one plot pans/zooms and the inspector flyout works.** Still **no canvas /
containers / tabs** (7.1c-3), **no inspector restyle** (7.1d), **no DataSet** (7.2). Faithful copy-port:
rename namespaces, keep behavior. **Do not redesign.**

## Why these files travel together
splotRF's `PlotControl` is a monolith: its context menu, double-tap, and pointer handlers **hard-reference
five views** — `PlotInspectorView` (`.ScrollToTrace`), `AxesLimitsView`, `AxesLabelsFlyout`,
`MarkerEditorView`, and `MarkerInfoBoxView` (`.PopulateMarkerMenu` static) — plus `PlotExporter`
(Export/Copy). They must all compile together. Their VMs were already ported in 7.1c-1.

## The port

### 1. `PlotControl` → `src/Ui/DataDisplay/Controls/PlotControl.cs` (replace the 7.1b file)
Copy splotRF `src/Controls/PlotControl.cs` over the 7.1b render-only one. Rename namespace →
`CircuitRF.Ui.DataDisplay.Controls`; retarget usings: `splotRF.ViewModels` → `CircuitRF.Ui.DataDisplay.ViewModels`,
`splotRF.Views` → `CircuitRF.Ui.Views.DataDisplay`, models/renderers/`RenderTheme` via `CircuitRF.Ui.DataDisplay`.
- **This auto-fixes the 7.1b `canvas.Clear()` bug** — splotRF's `PlotDrawOperation` only clears when the
  plot is null; for a live plot it does **not** clear (so stacked plots composite correctly). Keep that.
- Keep the `Plot` / `PlotTheme` / `EnablePanning` / `ZoomFactor` / `Library` properties, the provider hooks
  (`NextMarkerIndexProvider`/`FindMarkerInfoBoxVmProvider`/`ContainerProvider`/`SelectedMarkersProvider`),
  and all pointer/wheel/context-menu/flyout/table logic **as-is**. Fonts already resolve to IBM Plex via the
  7.1b renderers; Material icons are available.

### 2. Controls → `src/Ui/DataDisplay/Controls/`
Port `AxisLabelControl.cs` and `DragSelectOverlay.cs` (namespace `CircuitRF.Ui.DataDisplay.Controls`). Both
are small, self-contained Skia/overlay controls (used by the canvas/container in 7.1c-3, but port now to
complete the Controls folder; they compile standalone).

### 3. Flyout/overlay views → `src/Ui/Views/DataDisplay/`, namespace `CircuitRF.Ui.Views.DataDisplay`
Port these splotRF `src/Views/` `.axaml` + `.axaml.cs` (rename `x:Class`/namespaces; retarget the
`xmlns:vm`→`CircuitRF.Ui.DataDisplay.ViewModels`, `xmlns:m`/models→`CircuitRF.Ui.DataDisplay`,
`xmlns:cc`/controls→`CircuitRF.Ui.DataDisplay.Controls`, `xmlns:cv`/converters→ the ported converters ns):
- `PlotInspectorView` (preserve its public `ScrollToTrace(int)` — `PlotControl.ShowPlotInspector` calls it).
  **Port splotRF-styled as-is; the circuitRF restyle is 7.1d.**
- `AxesLimitsView`, `AxesLabelsFlyout`.
- `MarkerEditorView`, `MarkerInfoBoxView` (preserve the `MarkerInfoBoxView.PopulateMarkerMenu(...)` static —
  `PlotControl.ShowMarkerContextMenu` calls it).
Port the **converters** these views use (splotRF `src/Converters/`, e.g. `DoubleToDecimalConverter`,
`PrecisionFormatConverter`) → `src/Ui/DataDisplay/Converters/`, namespace `CircuitRF.Ui.DataDisplay.Converters`
(or reuse an equivalent circuitRF converter if one already exists — your call, note which).

### 4. `PlotExporter` (Export / Copy menu)
`PlotControl`'s `OnMenuExport`/`OnMenuCopyPlot` call `PlotExporter.ExportAsync` / `CopyPlotToClipboardAsync`.
Port `PlotExporter` (find it in splotRF `src/` — likely a Services/root file) → `CircuitRF.Ui.DataDisplay`.
**If it carries file-dialog / window deps that don't port cleanly, stub just those two `PlotControl`
handlers** (`OnMenuExport`/`OnMenuCopyPlot`) with a `// TODO 7.x` no-op — they're **not** in the 7.1c-2 gate.
Prefer porting; stub only if it would balloon this slice.

### 5. Harness — make the single plot interactive
Extend the 7.1b harness in `DataDisplayView` + `DataDisplayDocumentViewModel`:
- The hosted `PlotControl` is now the interactive one bound to `CurrentPlot`; set **`EnablePanning="True"`**
  (no container to handle moves in the harness) and a sensible `ZoomFactor`.
- Wire **`DoubleTapped`** on the `PlotControl` (in `DataDisplayView` code-behind) →
  `plotControl.HandleDoubleTapAt(e.GetPosition(plotControl))` so double-click opens the inspector flyout.
  (In the real engine `PlotContainerView` does this; the harness does it directly.)
- `Library` may stay null (the inspector opens; the data picker is just empty — fine for the gate). Provider
  hooks stay unset; splotRF's null-guards keep marker paths from crashing.
- Keep the `TEMP 7.1b/7.1c` markers; 7.1c-3 replaces this harness with the real canvas.

## Scope guardrails (do NOT do in 7.1c-2)
- No `DataDisplayView` (splotRF canvas) / `PlotContainerView` / `DisplayWindow` chrome / tabs (7.1c-3).
- No inspector **restyle** (7.1d) — `PlotInspectorView` ships splotRF-styled.
- No marker **runtime wiring** (container providers, canvas info-box overlay) — that's 7.1c-3; marker code
  ships but is exercised then.
- No DataSet source (7.2), no `.cdd` (7.1e). Everything stays in `src/Ui`.

## Gate (acceptance)
1. Builds green (`TreatWarningsAsErrors`); no `splotRF` namespace remains in ported files.
2. On the harness: the single plot **pans** (left-drag) and **zooms** (Ctrl+scroll); **double-click opens
   the (splotRF-styled) Plot Inspector flyout**, and changing plot type / a trace's line color/width
   **redraws immediately**.
3. Insert two stacked demo plots (or resize one over another) → they **composite without wiping** each other
   (the `canvas.Clear` regression is gone).
4. No schematic/symbol regression; the 7.1b font/watermark behavior is unchanged.

## On completion
One-line "Phase 7.1c-2 — COMPLETE" note to `src/Ui/CLAUDE.md` (interactive `PlotControl` + 5 flyout/overlay
views + converters + `PlotExporter` (or stub) ported; `canvas.Clear` fixed). Report the build result, any
stubs, and whether `PlotExporter` ported or was stubbed. Next: **7.1c-3** (canvas + containers + tabs, wired
into the document).
