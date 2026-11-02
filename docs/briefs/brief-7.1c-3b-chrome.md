# Sonnet Brief — Phase 7.1c-3b: chrome (tabs + toolbar + library + Load Touchstone + docked inspector)

**Design:** `docs/design/data-display.md` §3 → "7.1c" → "7.1c-3b". Read that. **Scope: wrap the 3a canvas in
the real document chrome — tab strip, an in-document toolbar, the SNP library panel, Load Touchstone, and the
faithful docked inspector panel — and remove the 3a demo seed.** This completes 7.1c. Faithful port +
chrome assembly + re-enabling one file dialog. **Do not redesign; no inspector restyle (7.1d), no `.cdd`
(7.1e), no DataSet (7.2).**

## Reconciliation decisions (locked for this slice)
- **splotRF's app menu is dropped.** Its `NativeMenu` + in-window `Menu` do not port — circuitRF's workspace
  owns the app menu. The Data Display commands surface via an **in-document toolbar** + the document view's
  `KeyBindings` (like the Schematic document).
- **Load Touchstone ships now** (so plots can be authored from real files). **Save/Open Display (`.cdd`) stays
  7.1e** — leave those actions un-injected.
- **Docked inspector panel = faithful splotRF** (right column, gated on `IsInspectorOpen` + `HasSingleSelection`).
  The §2.8 Properties-dock unification is 7.1d.

## The port

### 1. `TabHeaderView` → `src/Ui/Views/DataDisplay/TabHeaderView.axaml(.cs)`
Copy splotRF `src/Views/TabHeaderView.axaml(.cs)` → namespace `CircuitRF.Ui.Views.DataDisplay`, `x:DataType`
`TabViewModel`. Keep the label↔TextBox rename swap (DoubleTapped → `IsEditingName`; Enter/Esc/LostFocus
confirm; code-behind focus+SelectAll) and the `Close` button (`CloseTabCommand`). Pure retarget.

### 2. `SnpLibraryView` → `src/Ui/Views/DataDisplay/SnpLibraryView.axaml(.cs)`
Copy splotRF `src/Views/SnpLibraryView.axaml(.cs)` → namespace `CircuitRF.Ui.Views.DataDisplay`, `x:DataType`
`SnpLibraryViewModel`. Keep: header, empty-drop hint (incl. the `OnPlatform` Linux/Ctrl+L text), the
`ItemsControl` over `Entries` with the `SnpEntryViewModel` template (DisplayName + `.broken` style + Refresh
+ Remove icon buttons + context menu Refresh / Reveal / Copy Path / Copy Path Relative), and the
**`DragDrop.AllowDrop` drop handler** in the code-behind (adds dropped `.snp` files to the library). The
`SnpEntryViewModel` commands were ported in 7.1c-1 — confirm `RevealInExplorerCommand` (platform reveal),
`CopyPath*`, `Refresh`, `Remove` compile and work; retarget any path/clipboard calls to circuitRF.
**Add a small import button** to the library header (Material `FileImportOutline`) bound to
`Window.OpenFileCommand` for discoverability (Ctrl/Meta+L still works via the document KeyBindings).

### 3. Build the document chrome in `DataDisplayView.axaml` (bound to `DataDisplayDocument`)
Replace the 3a body (bare `PlotCanvasView` + temp Add-Plot button) with:
- **Root `KeyBindings`** (cross-platform Ctrl **and** Meta variants) for the Data-Display commands only —
  `AddPlotCommand` (Ctrl+Shift+A), `NewTabCommand` (Ctrl+T), `RemovePlotCommand` (Delete / Ctrl+Shift+D),
  `ZoomInCommand` (Ctrl+OemPlus), `ZoomOutCommand` (Ctrl+OemMinus), `ActualSizeCommand` (Ctrl+D1),
  `FitAllCommand` (Ctrl+D0), `UndoCommand` (Ctrl+Z), `RedoCommand` (Ctrl+Shift+Z / Ctrl+Y),
  `OpenFileCommand` (Ctrl+L). **Drop** the app-level ones (New/Open/Save/Close/Quit/Settings). All bound
  `{Binding ViewModel.Window.<Cmd>}`.
- An **in-document toolbar** — a `StackPanel Classes="Toolbar"` (reuse the Schematic document's toolbar
  style: `StackPanel.Toolbar mi|MaterialIcon` from App.axaml) with Material-icon buttons:
  Add Plot (`ChartLine`) · New Tab (`TabPlus`) · sep · Zoom In (`MagnifyPlusOutline`) · Zoom Out
  (`MagnifyMinusOutline`) · Actual Size (`Magnify`) · Fit All (`FitToPageOutline`) · sep · Undo (`Undo`) ·
  Redo (`Redo`), each bound to `ViewModel.Window.<Cmd>` with a `ToolTip.Tip`.
- The **main content** `Grid ColumnDefinitions="180,4,*"`:
  - `<v:SnpLibraryView Grid.Column="0" DataContext="{Binding ViewModel.Window.SnpLibrary}"/>`
  - `<GridSplitter Grid.Column="1" .../>`
  - right `Grid Grid.Column="2" ColumnDefinitions="*,Auto"`:
    - `TabControl` (`TabStripPlacement="Bottom"`, `ItemsSource="{Binding ViewModel.Window.Tabs}"`,
      `SelectedItem="{Binding ViewModel.Window.ActiveTab, Mode=TwoWay}"`, `ItemTemplate`=`TabHeaderView`,
      `ContentTemplate`=`PlotCanvasView`). Copy splotRF's `TabControl.Styles` (TabItem MinHeight/Padding).
    - docked inspector `Border Grid.Column="1" IsVisible="{Binding ViewModel.Window.IsInspectorOpen}"` →
      inner `Border IsVisible="{Binding ViewModel.Window.HasSingleSelection}"` →
      `<v:PlotInspectorView DataContext="{Binding ViewModel.Window.ActiveInspector}"/>`.

### 4. Document view code-behind (`DataDisplayView.axaml.cs`) — inject the dialog actions
The window VM uses injected actions for anything needing a `TopLevel`. In `OnLoaded` (or once the visual
tree is up), inject:
- **`Window.SetOpenFileAction(...)`** — port splotRF's **Load Touchstone** picker from
  `DisplayWindow.axaml.cs` (its `SetOpenFileAction` body / `OpenFile` dialog): `StorageProvider`
  `OpenFilePickerAsync` filtered to Touchstone (`.s*p`), then add each picked file to `Window.SnpLibrary`
  (same call the splotRF original uses). Resolve `TopLevel.GetTopLevel(this)`.
- **`Window.SetGetCanvasSizeAction(() => Window.ActiveTab?.GetCanvasSizeFunc?.Invoke() ?? (800, 600))`** —
  `FitAll` needs this.
- **Do NOT** inject `SetOpenDataDisplayAction` / `SetSaveDataDisplayAsAction` (those are `.cdd`, 7.1e) or
  the clipboard actions (plot Cut/Copy/Paste, later). Leave them unset — their commands no-op safely.

### 5. Remove the 3a demo seed
Delete `SeedDemoPlot()` (and its call) from `DataDisplayDocumentViewModel`, plus the leftover synthetic-SNP
`using`s. Authoring now flows: **Load Touchstone → SNP Library → Add Plot** (splotRF's `AddPlot` builds a
plot from the first non-empty library SNP when the canvas is empty). The document VM keeps just `Window` +
`IsDirty`.

### 6. App.axaml
`DataDisplayDocument → DataDisplayView` mapping unchanged.

## Scope guardrails
- No `.cdd` save/open (7.1e); no DataSet source (7.2); no inspector restyle / Properties-dock unify (7.1d).
- Don't port splotRF's `Window`/menus/About/Settings/Quit. No new app-menu wiring.
- Plot Cut/Copy/Paste clipboard wiring is out of scope (leave the VM's stubs as-is).

## Gate (acceptance)
1. Builds green; no `splotRF` namespace remains; no demo seed.
2. **Tabs:** New Tab adds a tab; double-click a tab header renames it; close removes it (min one stays).
3. **Library + Load Touchstone:** the import button / Ctrl+L opens a Touchstone picker; a chosen `.sNp`
   appears in the SNP Library (drag-drop also works on macOS); broken/missing entries show red-italic.
4. **Authoring:** with a library file loaded, **Add Plot** creates a plot from it; plots can be authored
   across multiple tabs; each tab keeps independent plots/zoom.
5. **Toolbar + shortcuts:** Add Plot / New Tab / Zoom In-Out / Actual Size / Fit All / Undo / Redo work from
   both the toolbar and keyboard; the docked inspector appears when `IsInspectorOpen` and a single plot is
   selected.
6. Tear-off / re-dock / close still work; no schematic regression.

## On completion
Update `src/Ui/CLAUDE.md`: "Phase 7.1c COMPLETE — splotRF Data Display engine ported (canvas + containers +
tabs + toolbar + SNP library + Load Touchstone + flyout/docked inspector), splotRF-styled." Report the build
result + a screenshot of a 2-tab display with a loaded Touchstone plotted. Next: **7.1d** (inspector restyle
to the §2.8 merge + dual surface + marker polish).
