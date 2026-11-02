# Brief: Data Display fixes — keyboard routing, copy/paste, save-rename, toolbar plot buttons

Stack/rules: .NET 10, Avalonia 12, `TreatWarningsAsErrors=true` (capture nullable-property reads into
locals). UI-only change set. Build must end **0W/0E**; add gate tests; report total test count. After
landing, add a newest-first changelog entry to `src/Ui/CLAUDE.md`.

Six items: three bugs + three toolbar changes. All root causes below are verified on disk.

---

## Bug 1 — Ctrl+S on a focused Data Display saves the workspace `.cws`, not the `.cdd`

**Root cause.** `WorkspaceWindow.axaml` binds `Window.KeyBinding Gesture="Ctrl+S"/"Meta+S"` →
`SaveAllDocumentsCommand`. Window key bindings are processed before visual-tree routing (see the
"Keyboard shortcut routing" section in `src/Ui/CLAUDE.md`), so the focused Data Display never sees the
key. The Data Display's own `SaveDataDisplayCommand` (toolbar) is never reached, and the scratch `.cdd`
is not written.

**Fix — make the active Data Display's `.cdd` save on Ctrl+S, consistent with how Schematic/Symbol docs
save their own file.** First determine how Schematic/Symbol Ctrl+S currently saves the active doc's
file: read `WorkspaceViewModel.SaveAllDocumentsCommand` (and `SaveSingleDocument` / `SaveDataDisplayDoc`,
already present from the close/quit work). Then make the **same mechanism** persist the active/dirty
`DataDisplayDocument`:
- If `SaveAllDocumentsCommand` already saves the active schematic/symbol's file in place (and scratch via
  picker), extend it to also persist dirty `DataDisplayDocument`s via the existing
  `SaveDataDisplayDoc(dd, owner)` (materialized → save in place to `dd.FilePath`; scratch → `.cdd`
  save-as picker). That makes Ctrl+S save the `.cdd` exactly as it saves a `.csch`.
- If instead Schematic/Symbol claim Ctrl+S through a focus-gated **tunnel** handler in their view
  (mirroring the documented `AddHandler(KeyDownEvent, …, RoutingStrategies.Tunnel, handledEventsToo:true)`
  pattern), add the equivalent to `DataDisplayView` (see Bug 2 — the same tunnel handler will host the
  clipboard keys), routing Ctrl/Meta+S → `ViewModel.Window.SaveDataDisplayCommand`, gated on
  `IsKeyboardFocusWithin`.

Pick whichever path matches the schematic behavior so the two stay consistent; do not invent a third.
The acceptance test: with a Data Display focused, Ctrl+S writes/updates its `.cdd` (prompting for a path
when scratch) and does **not** silently save only the workspace.

---

## Bug 2 — Copy/Paste does not work in the Data Display

Three independent defects, all required:

**2a. `PerformCopy` is a stub.** `DisplayWindowViewModel.PerformCopy(bool selectedOnly)` is
`// TODO 7.x: stub` and writes nothing to the clipboard. Implement it:
```csharp
private async Task PerformCopy(bool selectedOnly)
{
    var display = DataDisplay;
    if (display is null || _setClipboardTextAction is null) return;

    var containers = (selectedOnly
        ? display.Plots.Where(p => p.IsSelected)
        : display.Plots).ToList();
    if (containers.Count == 0) return;

    var config = new DataDisplayConfig
    {
        FormatVersion = DataDisplayConfig.CurrentFormatVersion,
        Plots = containers
            .Select(c => DataDisplayViewModel.BuildPlotContainerConfig(c, configDir: "", display.Library))
            .ToList(),
    };
    string json = JsonSerializer.Serialize(config, DataDisplayViewModel.JsonOpts);
    await _setClipboardTextAction(json);
    await CheckPasteStateAsync();   // refresh CanPaste right after copy
}
```
`BuildPlotContainerConfig` is `internal static` and is the same authoritative snapshot used by Save, so
it emits cube-bound traces and logical SourceRefs correctly. (Note `TryParseDataDisplayConfig` already
gates on `config.Plots.Count > 0`, so write into `Plots`, not `Tabs`.)

**2b. Clipboard actions are never injected.** `DataDisplayView.axaml.cs OnLoaded` wires the library's
`CopyToClipboardFunc` but never calls `SetGetClipboardTextAction` / a text setter, so `PerformCopy`'s
setter and `Paste`'s `_getClipboardTextAction` are both null → copy no-ops and `CanPaste` is always
false. Add a simple **text** setter seam on `DisplayWindowViewModel` (the existing
`_setClipboardDataAction : Func<DataTransfer,Task>` is unused — replace it with
`_setClipboardTextAction : Func<string,Task>` + `SetSetClipboardTextAction(...)`), and wire both in
`OnLoaded`:
```csharp
win.SetSetClipboardTextAction(async text =>
{
    var cb = TopLevel.GetTopLevel(this)?.Clipboard;
    if (cb is not null) await cb.SetTextAsync(text);
});
win.SetGetClipboardTextAction(async () =>
{
    var cb = TopLevel.GetTopLevel(this)?.Clipboard;
    return cb is null ? null : await cb.GetTextAsync();
});
```
Also call `win.CheckPasteStateAsync()` from `OnAttachedToVisualTree` (next to the existing
`RefreshAvailableDataSources()` call) so Paste enables when the clipboard already holds plot JSON.

**2c. `PasteFromConfigAsync` is a stale network-only fork.** It iterates `config.Plots`, treats
`tc.SourcePath` as a filesystem path (`Path.IsPathRooted(tc.SourcePath)`), and builds only
SNP/network traces — it has **no cube-bound branch**. But `BuildTraceConfig` now emits a *logical*
SourceRef in `SourcePath`, and the correct per-container loader is `LoadPlotContainerConfigAsync`
(cube-aware + ref-aware: resolves the SourceRef via `Library.ResolveAbs`, lazy-loads files, handles
`CubeName`/`Slice`/`Expression`). So pasting a results/cube plot today drops its traces and
mis-resolves sources.

Rewrite `PasteFromConfigAsync` to **reuse** the loader instead of duplicating trace construction:
- For each `pc` in `config.Plots`: bump `pc.Left/pc.Top` by `PasteOffset` and bump each trace's marker
  `InfoBoxX/InfoBoxY` by `PasteOffset`, then call the shared `LoadPlotContainerConfigAsync(pc,
  configDir: "")`. Refactor that method to **return the added `PlotContainerViewModel`** (it currently
  ends with `_plots.Add(container); RebuildMarkerInfoBoxesForContainer(container);` — return
  `container`), so paste can collect the pasted set. (Save/Load callers ignore the return.)
- After loading each container, dedup marker names against the pre-paste set (keep the existing
  `usedMarkerNames` "append `_2`, `_3`…" logic, applied to the pasted container's trace markers).
- Select only the pasted containers + their InfoBoxes (keep the existing tail of the method).
- Return the pasted containers (the `PasteCommand` undo wrapper in `DisplayWindowViewModel.Paste`
  already consumes the list).

This makes copy→paste round-trip cube/results plots and Touchstone plots identically to save→load.

**2d. Keyboard + Edit-menu routing for Cut/Copy/Paste.** Ctrl+C/X/V are intentionally **not** window
key bindings (`WorkspaceWindow.axaml` comment), and the Edit-menu Cut/Copy/Paste bind to
`WorkspaceViewModel.Cut/Copy/PasteCommand`. So the Data Display currently receives no clipboard keys and
the menu doesn't target it. Do both:
- **Keyboard:** add Ctrl/Meta + C/X/V to `DataDisplayView.axaml` `UserControl.KeyBindings` →
  `ViewModel.Window.CopyCommand` / `CutCommand` / `PasteCommand`. (These don't collide with any window
  binding, so plain KeyBindings suffice — they fire on bubble from the focused canvas. If a tunnel
  handler was added for Bug 1, host C/X/V there instead for consistency.)
- **Edit menu:** make `WorkspaceViewModel.Cut/Copy/Paste/SelectAllCommand` dispatch to the active
  `DataDisplayDocument`'s `DisplayWindowViewModel` (`CutCommand`/`CopyCommand`/`PasteCommand`) when a
  Data Display is the active dockable — mirroring however these already route to the active schematic.
  (Read the existing command bodies; extend the active-doc switch.)

Make `Paste`/`PasteFromConfigAsync` robust if `CanPaste` is stale: it already re-parses and no-ops on
invalid JSON, so the menu item staying enabled is harmless.

---

## Bug 3 — First save of a New Data Display doesn't update the Content-pane tab title

**Root cause.** `DisplayWindowViewModel.SaveAllAsync` sets the VM's `WindowTitle` and
`_currentConfigPath`, but nothing updates the host `DataDisplayDocument` (the Dock `Document` whose
`Title` drives the tab). `DataDisplayDocument.Materialize(filePath)` is a stub that sets `FilePath` and
clears dirty but **never updates `_baseTitle`/`Title`**, and it isn't called by the in-document save
path anyway. So a scratch doc keeps its `Untitled-Display-N` tab after first save-as.

**Fix.** Funnel through `SaveAllAsync` (the single save funnel for toolbar + workspace paths):
- Add an event on `DisplayWindowViewModel`, e.g. `public event Action<string>? ConfigPathSaved;`, raised
  at the end of `SaveAllAsync(path,…)` after `CaptureBaseline()` with the absolute `path`.
- In `DataDisplayDocument` ctor, subscribe: `vm.Window.ConfigPathSaved += OnSavedToPath;` where
  ```csharp
  internal void OnSavedToPath(string path)
  {
      FilePath   = path;
      _baseTitle = System.IO.Path.GetFileNameWithoutExtension(path);
      Id         = path;                                   // so dedup/reopen keys on the real path
      Title      = _isDirty ? $"• {_baseTitle}" : _baseTitle;
  }
  ```
  (Remove or fold the dead `Materialize` stub into this.)
- In `WorkspaceViewModel`, ensure the scratch→materialized transition is recorded so reopening the file
  activates this doc instead of duplicating: when a scratch `DataDisplayDocument` is first saved, move it
  from `_scratchDataDisplays` into `_openDocsByPath` keyed by the new path. Hook this off the same
  `ConfigPathSaved` event (subscribe where the document is created) or off `SaveDataDisplayDoc`. Mirror
  the existing schematic/symbol scratch-materialization bookkeeping.

Acceptance: New Data Display → edit → Save → tab shows the file's base name (with the `•` dirty bullet
cleared); reopening the saved `.cdd` activates the same tab rather than opening a duplicate.

---

## Enhancement 4 — "Add Plot" → "Add Rect Plot" + 3 new plot-type buttons

**Current.** Toolbar `AddPlotCommand` → `DataDisplay?.AddPlot()`, and `DataDisplayViewModel.AddPlot`
defaults `plotType = PlotType.Smith` — so the button makes a **Smith** plot. An overload
`AddPlot(PlotType, FreqUnit, …)` already exists and creates an empty plot of the given type.

**Changes in `DisplayWindowViewModel`:**
- Change the existing command body to Rect: `private void AddPlot() => DataDisplay?.AddPlot(PlotType.Rect);`
  (keeps the `Ctrl+Shift+A` binding; now adds a Rect plot).
- Add three commands:
  ```csharp
  [RelayCommand] private void AddSmithPlot() => DataDisplay?.AddPlot(PlotType.Smith);
  [RelayCommand] private void AddPolarPlot() => DataDisplay?.AddPlot(PlotType.Polar);
  [RelayCommand] private void AddTablePlot() => DataDisplay?.AddPlot(PlotType.Table);
  ```
  (`PlotType` is already in scope via `using CircuitRF.Ui.DataDisplay;`.)

**Changes in `DataDisplayView.axaml`** (toolbar): the "Add Rect Plot" button keeps `Kind="ChartLine"`;
the three new buttons reuse the same glyphs as the PlotInspector plot-type selector. Add the controls
namespace `xmlns:ctl="using:CircuitRF.Ui.DataDisplay.Controls"`. Button bodies, matching the existing
`<Button Padding="6,3">` + 16×16 glyph toolbar style:
- Add Rect Plot: `<mi:MaterialIcon Kind="ChartLine" Width="16" Height="16"/>` → `AddPlotCommand`,
  tooltip "Add Rect Plot (Ctrl+Shift+A)".
- Add Smith Plot: `<ctl:PlotTypeGlyphControl Kind="Smith" Width="16" Height="16"
  Stroke="{DynamicResource SystemControlForegroundBaseHighBrush}"/>` → `AddSmithPlotCommand`.
- Add Polar Plot: `<ctl:PlotTypeGlyphControl Kind="Polar" Width="16" Height="16"
  Stroke="{DynamicResource SystemControlForegroundBaseHighBrush}"/>` → `AddPolarPlotCommand`.
- Add Table: `<mi:MaterialIcon Kind="TableLarge" Width="16" Height="16"/>` → `AddTablePlotCommand`.

(`PlotTypeGlyphControl` has `Kind ∈ {Smith, Polar}` and a `Stroke` brush; set Stroke to match the
adjacent MaterialIcon color — verify it reads at the same weight as the other toolbar icons and adjust
the brush if needed.) Place the four Add buttons contiguously (Rect, Smith, Polar, Table).

All four create an **empty** plot of the given type (no traces) — `AddPlot(PlotType.X)` already does this.

---

## Enhancement 5 — Remove the "Load Dataset…" toolbar button

Delete the `LoadRunResultsCommand` button (`FolderArrowDownOutline`, tooltip "Load Dataset…") from the
toolbar in `DataDisplayView.axaml`; the datasource combobox supersedes it. Leave
`LoadRunResultsCommand` / `DoLoadRunResultsAsync` in place (harmless) or remove them too — your call;
removing the button is the requirement. Clean up any now-redundant adjacent `<Border .../>` separator.

---

## Enhancement 6 — Move the datasource combobox to the left-most toolbar position + spacer

In `DataDisplayView.axaml`, move the datasource `<ComboBox …AvailableDataSources…>` to be the **first**
child of the toolbar `StackPanel` (before the Add Rect Plot button). Immediately after it, add the
standard vertical separator already used in this toolbar:
```xml
<Border Width="1" VerticalAlignment="Stretch" Margin="2,3"
        Background="{DynamicResource SystemControlForegroundBaseLowBrush}"/>
```
Resulting left-to-right order: **[Datasource combo] | [Add Rect][Add Smith][Add Polar][Add Table] |
[New Tab] | [Zoom In][Zoom Out][Actual][Fit] | [Undo][Redo] | [Save][Open] | [Export]**. Keep the combo's
existing bindings (`AvailableDataSources`, `SelectedDataSourceItem`, tooltip) unchanged.

---

## Tests (framework-free `tests/Ui.Tests`)
- **Copy/paste round-trip:** build a `DisplayWindowViewModel`, add a plot, copy (capture the JSON via a
  fake text-setter), clear, `PasteFromConfigAsync(parsedConfig)` → one container added, offset applied,
  marker names deduped. Add a cube-bound-trace fixture to prove cube traces survive paste (the
  regression this fixes).
- **Save-rename:** subscribe to `ConfigPathSaved`, call `SaveAllAsync(tmpPath)`, assert the event fires
  with the path; construct a `DataDisplayDocument`, call `OnSavedToPath`, assert `Title` == base name and
  `FilePath`/`Id` updated. (`DataDisplayDocument`/`DisplayWindowViewModel` are constructable without an
  Avalonia host — see `src/Ui/CLAUDE.md`.)
- **AddPlot type:** `Window.AddPlotCommand.Execute(null)` → active plot `PlotType == Rect`;
  `AddSmithPlotCommand` → `Smith`; etc. (each adds one empty plot).
- Do **not** instantiate `WorkspaceViewModel` (needs the Avalonia host); test its Cut/Copy/Paste routing
  via the "simulate" pattern if needed.

## Notes / gotchas
- Capture nullable-property reads into locals (TreatWarningsAsErrors).
- `PerformCopy`/paste serialize with `DataDisplayViewModel.JsonOpts` and the v1 `Plots` list (not `Tabs`)
  — that's what `TryParseDataDisplayConfig` and `PasteFromConfigAsync` consume.
- Refactoring `LoadPlotContainerConfigAsync` to return the container must not change Save/Load behavior
  (callers ignore the return).
- The Bug-1 fix must stay consistent with Schematic/Symbol Ctrl+S — confirm their mechanism first.
