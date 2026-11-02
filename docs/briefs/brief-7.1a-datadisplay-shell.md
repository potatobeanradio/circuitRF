# Sonnet Brief — Phase 7.1a: Data Display document shell + empty canvas

**Design:** `docs/design/data-display.md` §3 → "7.1a". Read that block first. **Scope is 7.1a ONLY: get a
Data Display to exist as a first-class, tear-off Dock document with an empty placeholder canvas.** No plot
model, no renderers, no inspector, no `.cdd` persistence, no data — those are 7.1b–7.1e. This step de-risks
the Dock integration before the heavy port.

## Goal
A **New Data Display** command opens a `DataDisplayDocument` tab in the center DocumentDock; it tears off
into its own window and re-docks like Schematic/Symbol tabs; it closes cleanly. The body is a placeholder
"empty canvas" view. That's it.

## Verified substrate (consume — already exists)
- `CircuitRfDockFactory.OpenDocument(Document doc)` (`src/Ui/ViewModels/Dock/CircuitRfDockFactory.cs`) adds
  a document to the center dock and activates/focuses it. Tear-off is already wired
  (`DefaultHostWindowLocator = () => new HostWindow()`).
- `App.axaml` `Application.DataTemplates` maps each document type to its view (e.g.
  `SchematicDocument → SchematicView`, `SymbolEditorDocument → SymbolEditorView`). Add one entry for the
  new document.
- `SymbolEditorDocument` (`src/Ui/Schematic/SymbolEditorDocument.cs`) is the **clone template**:
  `Document` subclass with `FilePath?` / `IsScratch` / `IsDirty` (mirrors the VM via `PropertyChanged`) /
  `Materialize(path)`. Mirror this surface; **do not** implement `IUndoableDocument` yet (no undo in 7.1a).
- `WorkspaceViewModel` already has the **New Symbol** path described in `scratch-and-save-lifecycle.md` §8.1
  (`NewScratchSymbol()`, `_scratchSymbols` list, `NextScratchSymbolTitle()`, opens via
  `_factory.OpenDocument`), and the close/quit/save-all participation (`HasAnyDirtyWork`,
  `ConfirmCloseDockable`/`CloseDockableConfirm`, `SaveAllDocuments`, `PromptSaveBeforeClose`). **Mirror the
  New Symbol path** for Data Displays.
- `Material.Icons.Avalonia` is already referenced and `<mi:MaterialIconStyles/>` is loaded in `App.axaml`.

## Code changes

### 1. New folder `src/Ui/DataDisplay/` (namespace `CircuitRF.Ui.DataDisplay`)
Keep Data Display code in its own area (not under `Schematic/`). The ported splotRF models/renderers
(7.1b+) will also land here.

#### `DataDisplayDocument.cs` — `sealed class DataDisplayDocument : Document`
Clone `SymbolEditorDocument`'s shape, minus undo:
- `public DataDisplayViewModel ViewModel { get; }`
- `public string? FilePath { get; private set; }` (null = scratch — a `.cdd` path later); `public bool IsScratch => FilePath is null;`
- `public bool IsDirty` (private set; on change set `Title = IsDirty ? $"• {_baseTitle}" : _baseTitle`).
- Constructor `(string title, DataDisplayViewModel vm, string? filePath = null)`: set `Id`/`Title`/
  `_baseTitle`/`FilePath`/`ViewModel`; subscribe to `vm.PropertyChanged` to reflect `vm.IsDirty` (the VM is
  the source of truth, exactly as `SymbolEditorDocument` does).
- `internal void Materialize(string filePath)` (sets `FilePath`, clears dirty) — present but unused until
  7.1e; fine to leave as a thin stub mirroring `SymbolEditorDocument.Materialize`.

#### `DataDisplayViewModel.cs` — `sealed partial class DataDisplayViewModel : ObservableObject`
Minimal for 7.1a (grows in 7.1b+):
- `[ObservableProperty] private bool _isDirty;` (stays false in 7.1a — nothing edits it yet).
- A title/display name property if convenient.
- A placeholder `public bool HasPlots => false;` (the view's empty-state binding). No plot collection yet.
- No commands required yet (Add Plot arrives with the plot model in 7.1b/c).

### 2. New view `src/Ui/Views/DataDisplay/DataDisplayView.axaml(.cs)` (namespace `CircuitRF.Ui.Views.DataDisplay`)
A placeholder `UserControl`, `x:DataType="dd:DataDisplayViewModel"`:
- A full-bleed canvas-colored surface (use circuitRF theme brushes — e.g. `SystemChromeLowColor` /
  `SystemRegionColor`, matching other document bodies).
- A centered empty-state hint: a Material icon (e.g. `ChartLine`/`ChartScatterPlot`) + muted text like
  "Empty data display — plot authoring coming soon" (this text is a 7.1a placeholder; the real
  add-plot affordance comes in 7.1b/c). Use `SystemBaseMediumColor` for the muted text, ~13px.
- No interaction logic. Keep the code-behind to the standard `InitializeComponent()`.

### 3. `App.axaml` — document→view mapping
Add, alongside the existing document DataTemplates:
```xml
<DataTemplate DataType="{x:Type dd:DataDisplayDocument}">
    <ddv:DataDisplayView/>
</DataTemplate>
```
with `xmlns:dd="using:CircuitRF.Ui.DataDisplay"` and `xmlns:ddv="using:CircuitRF.Ui.Views.DataDisplay"`.
Documents already use the cached (non-deferred) content template, so the view paints on the normal layout
pass — no extra work.

### 4. `WorkspaceViewModel` — New Data Display command + lifecycle participation
Mirror the **New Symbol** path:
- `NewDataDisplay()` (or a `NewDataDisplayCommand`): build a `DataDisplayViewModel`, wrap in a
  `DataDisplayDocument` with a scratch title from a `NextDataDisplayTitle()` helper (lowest free
  `"Untitled-Display-N"` across open displays — mirror `NextScratchSymbolTitle()`), track it in a new
  `_scratchDataDisplays` list (parallel to `_scratchSymbols`), and open via `_factory.OpenDocument(doc)`.
  Always enabled; no workspace required (scratch-first, like New Schematic/Symbol).
- **Close/quit participation:** ensure the close-confirm hook (`CloseDockableConfirm` /
  `ConfirmCloseDockable`) and `HasAnyDirtyWork` / `PromptSaveBeforeClose` **handle `DataDisplayDocument`
  without throwing**. In 7.1a a display is never dirty, so it closes freely — just make sure the type is
  recognized (or that unknown document types default to "clean, close freely") and removed from
  `_scratchDataDisplays` on close. Do **not** wire `.cdd` save here (7.1e).

### 5. Menu / command surface
Add a **New Data Display** item next to **New Schematic** / **New Symbol** (locate the menu in
`WorkspaceWindow.axaml` and the command wiring in `WorkspaceViewModel`). A sensible accelerator if one is
free (e.g. ⇧⌘D / Ctrl+Shift+D) — but only if unused; otherwise menu-only is fine.

### 6. Replace any existing DataDisplay *stub* path
If a "New Data Display" today creates a `StubDocument(StubKind.DataDisplay)` (rendered via
`StubContentView` + `StubKindIsDataDisplayConverter`), replace that with the real `DataDisplayDocument`.
Leave the `StubKind.DataDisplay` enum value and converter in place (harmless); just stop creating the stub
for this action.

## Scope guardrails (do NOT do in 7.1a)
- No `Plot`/`Trace`/`Axes`/`Marker` models, no renderers, no `PlotControl`, no canvas interaction.
- No Plot Inspector, no data sources, no Touchstone/`.npy` loading.
- No `.cdd` persistence, no Save/Open of displays, no `Materialize` wiring beyond the stub.
- No `IUndoableDocument` / undo integration.
- Don't touch the engines, RfCore, or 7.0 code.

## Gate (acceptance)
1. Builds green (`TreatWarningsAsErrors=true`).
2. **New Data Display** opens an `Untitled-Display-1` tab in the center dock showing the empty-canvas
   placeholder.
3. The tab **tears off** into its own window and **re-docks** (same behavior as a Schematic/Symbol tab).
4. The tab **closes cleanly** (no prompt, since it's not dirty; removed from tracking).
5. Opening several displays yields `Untitled-Display-1/2/3…` (unique titles).
6. Existing Schematic/Symbol/close-prompt behavior is unchanged.

## On completion
Add a one-line "Phase 7.1a — COMPLETE" note to `src/Ui/CLAUDE.md` (the new `DataDisplayDocument`/VM/View,
the New command, the DataTemplate). Report back before 7.1b (the plot model + renderers port) is briefed.
