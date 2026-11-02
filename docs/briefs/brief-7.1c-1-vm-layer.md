# Sonnet Brief — Phase 7.1c-1: port the splotRF Data Display VM layer

**Design:** `docs/design/data-display.md` §3 → "7.1c" (P1 faithful-first) → "7.1c-1 — VM layer". Read that.
**Scope: faithfully port splotRF's entire view-model stack into circuitRF so it compiles. NO views, NO new
controls, NO wire-up, NO UI behavior yet** — those are 7.1c-2 (controls) and 7.1c-3 (views + wire-up). This
is a copy-port: rename namespaces, retarget a few seams, resolve one name collision. **Do not redesign.**

## Why VM-only compiles on its own
The VM layer references **models + renderers** (ported in 7.1b: `Plot`/`Trace`/`Axes`/`Marker`/`Misc`,
`RenderTheme`, `PlotRenderer` constants) + `RfCore` + Avalonia + `Material.Icons` + `System.Text.Json` —
**not** `PlotControl` or any view. (`PlotContainerViewModel`'s `using splotRF.Controls` is unused except a
doc-comment cref — drop it.) So the stack builds without the controls/views.

## Files to port → `src/Ui/DataDisplay/ViewModels/`, namespace `CircuitRF.Ui.DataDisplay.ViewModels`
Copy each from splotRF `src/ViewModels/`, rename `namespace splotRF.ViewModels` → `CircuitRF.Ui.DataDisplay.ViewModels`,
and add `using CircuitRF.Ui.DataDisplay;` where models/renderers/`RenderTheme` are referenced:

`ViewModelBase.cs`, `PlotViewModel.cs`, `PlotContainerViewModel.cs`, `DataDisplayViewModel.cs`,
`TabViewModel.cs`, `DisplayWindowViewModel.cs`, `PlotInspectorViewModel.cs`, `TraceRowViewModel.cs`,
`SnpLibraryViewModel.cs`, `SnpEntryViewModel.cs`, `LabelStripViewModel.cs`, `AppSettingsViewModel.cs`,
`AxesLimitsViewModel.cs`, `AxesLabelsViewModel.cs`, `MarkerInfoBoxViewModel.cs`, `MarkerEditorViewModel.cs`,
`ColorItem.cs`, `ComboItems.cs`, `ComplexStringHelper.cs`, `TraceDataItem.cs`, `UndoCommands.cs`.

Also port any **supporting types these reference** that 7.1b deferred — port them to satisfy the compile:
- `DataDisplayConfig.cs` (splotRF `src/Models/`) → `src/Ui/DataDisplay/Models/`, namespace
  `CircuitRF.Ui.DataDisplay` (referenced by `DisplayWindowViewModel`/`DataDisplayViewModel` Save/Load).
- The **undo stack** type `UndoRedoManager` + `IUndoableCommand` (splotRF `src/Models/UndoRedo.cs` or
  wherever defined) → port alongside.
- If a VM references `PlotExporter` or any other helper not listed, port it too **or** stub the single
  call site with a `// TODO 7.x` and a no-op — whichever is smaller. Do not pull in views to satisfy it.

## Required adaptations (the only non-mechanical parts)

### A. Resolve the name collision (do this first)
splotRF's `DataDisplayViewModel` is the **per-tab canvas** VM. circuitRF's 7.1a `DataDisplayViewModel`
(`src/Ui/DataDisplay/DataDisplayViewModel.cs`, namespace `CircuitRF.Ui.DataDisplay`) is the **document**
VM (the temp demo harness). **Rename the 7.1a class `DataDisplayViewModel` → `DataDisplayDocumentViewModel`**
(it is the document VM — accurate). Update its referencers: `DataDisplayDocument.cs` (the `ViewModel`
property type + ctor), `Views/DataDisplay/DataDisplayView.axaml` (`x:DataType`) + its code-behind, and the
**New Data Display** command in `WorkspaceViewModel`. Keep the 7.1b demo harness working under the new name
(it's still `TEMP 7.1b`; 7.1c-3 removes it). After the rename, the ported splotRF `DataDisplayViewModel`
(canvas) lives in `…ViewModels` with no clash.

### B. AppSettings — keep values, don't touch splotRF's disk path
Port `AppSettingsViewModel` (the `.Instance` singleton + its properties: `RectAspectRatio`,
`EffectiveShowFilePrefix`, etc.). **Do not** read/write splotRF's settings file. If it persists to disk,
either retarget the path to circuitRF's app-data convention or initialize from in-memory defaults and leave
`// TODO 7.x: wire AppSettings persistence`. The Data Display only needs the *values* compiling now.

### C. Save/Load + window/menu/file-dialog code — keep the core, defer the entry points
`DisplayWindowViewModel`/`DataDisplayViewModel` carry `.splot` serialize/deserialize (`DataDisplayConfig`)
and possibly file-dialog / window-geometry / app-menu / About-window code.
- **Keep** the pure serialize/deserialize methods (they compile against `DataDisplayConfig` + `System.Text.Json`).
- **Defer** anything that opens a file dialog, references a top-level `Window`/`TopLevel`/`IStorageProvider`,
  persists window geometry, or news up a splotRF `View` (About/Acknowledgments/DisplayWindow): comment out
  the body or guard it behind a `// TODO 7.1c-3 / 7.1e` stub. None of that is needed to compile the VM layer,
  and it maps onto circuitRF's document/Dock/menu later. Do **not** port splotRF `Views`/`Windows` here.
- Leave the extension as-is for now (`.splot` string is fine); the `.cdd` switch is 7.1e.

### D. Material.Icons / RfCore / Avalonia
All available. `MaterialIconKind` etc. resolve via the existing `Material.Icons` dep. `SNP`/`MatrixType`/
`Complex` via `RfCore`.

## Scope guardrails (do NOT do in 7.1c-1)
- No views (`DataDisplayView`/`PlotContainerView`/`PlotInspectorView`/`DisplayWindow`/marker views/tab views).
- No changes to `PlotControl` (stays the 7.1b render-only control); no porting splotRF's interactive PlotControl.
- No wiring the ported VMs into `DataDisplayDocument`/the canvas, no tabs UI, no menu commands, no markers UI.
- No `.cdd` persistence wiring, no file dialogs.
- Don't modify 7.0/engine/RfCore. Everything stays in `src/Ui`.

## Gate (acceptance)
1. **Builds green** (`TreatWarningsAsErrors=true`) with the full VM stack + `DataDisplayConfig` + undo types
   present; no `splotRF` namespace remains in ported files; no unused-using/nullable warnings.
2. The 7.1a→`DataDisplayDocumentViewModel` rename is complete and the existing **New Data Display** +
   7.1b demo-plot harness still open and render (no regression).
3. A tiny smoke check compiles and runs: `new DisplayWindowViewModel()` has one default `TabViewModel`
   whose `DataDisplay` (canvas VM) exists; calling its **AddPlot** path adds one `PlotContainerViewModel`
   to `DataDisplay.Plots`. (A unit test in `Ui.Tests`, or a temporary asserted call — your choice; remove
   temporary scaffolding before finishing.)

## On completion
Add a one-line "Phase 7.1c-1 — COMPLETE" note to `src/Ui/CLAUDE.md` (VM layer ported to
`CircuitRF.Ui.DataDisplay.ViewModels`; 7.1a doc VM renamed to `DataDisplayDocumentViewModel`; Save/Load +
window/dialog entry points deferred to 7.1c-3/7.1e). Report the build result + any types you had to stub.
Next: **7.1c-2** (grow `PlotControl` to interactive + `AxisLabelControl`/`DragSelectOverlay`).
