# Brief — Find Instance: a component list for the focused schematic or layout

**Status:** NOT BUILT · **Date:** 2026-09-22
**Area:** `src/Ui/ViewModels/Dock/` (new tool), `src/Ui/Views/` (new view), `src/Ui/Docking/`,
`src/Ui/ViewModels/WorkspaceViewModel*.cs`, `src/Ui/Views/WorkspaceWindow.axaml`,
`src/Ui/Controls/SchematicCanvas.cs`, `src/Ui/Layout/LayoutEditorViewModel*.cs`
**Requirement tag:** `R-fi-<n>`

---

## 0. What the owner asked for

A dockable **Instance List** panel (title: *Instances*; menu command: **Design ▸ Find Instance…**)
that finds a placed component quickly in a large schematic or layout:

- It lists the component instances of the **focused document** — a `.csch` or a `.clay` — and its
  header says **which document** it is showing, the way the Analyses panel's header already does.
- It filters by **instance name** and by **component type**, and stays responsive while doing so.
- **Double-click** a row → the document **zooms to that instance and selects it**.
- **Design ▸ Find Instance…**, accelerator **Ctrl+F** (⌘F on macOS), brings the panel to the front
  for the focused document and **puts keyboard focus in its search box**, so the user types the
  name straight away. The menu item is **enabled only when a `.csch` or `.clay` is focused**, and
  greyed out otherwise.

---

## 1. Requirements

### The panel

- **R-fi-1** A new dock `Tool`, `InstancesTool`, with its own id in `DockPanelIds`, registered in
  `CircuitRfDockFactory` beside `LvsTool` (both of its switch arms, lines ~229 and ~711, and the two
  construction sites). It must survive a dock-layout save/restore like every other panel —
  `DockLayoutPersistenceTests` is the gate — and it is **not** added to any shipped Window Layout
  default: it appears when asked for.
- **R-fi-2** It follows the focused document exactly as the Analyses and DRC panels do. Hook it into
  the same places `AnalysesTool.SetActiveSchematic` (WorkspaceViewModel ~14883) and
  `DrcTool.SetActiveLayout` (~9045, ~12195, ~15028) are called, **including the calls that clear
  them**. One entry point, `SetActiveDocument(object? documentViewModel, string? displayName)`, taking
  either a `SchematicViewModel` or a `LayoutEditorViewModel`.
- **R-fi-3** The header reads the document's file name (`Amp.csch`, `Amp.clay`) — the
  `AnalysesListViewModel.HeaderLabel` pattern — and plain *Instances* with an empty-state line
  ("Open a schematic or layout to list its instances.") when neither is focused. **Showing one
  document's list while another is on screen is worse than showing none** (the LVS panel's rule).
- **R-fi-4** Columns: **Name**, **Type**, and a dimmed **Cell/Part** column where it adds something.
  - Schematic row: `EditableComponent.InstanceName`; type is
    `ComponentTypeRegistry.DisplayName(Symbol, PortCount)`, or for a cell reference the referenced
    cell's folder name, or for a kit part its part id.
  - Layout row: `LayoutInstance.DisplayRefDes` (never `RefDes` directly — its own comment says why),
    falling back to the cell name for an unnamed placement; type is the PCell generator id or the
    part kind where there is one, otherwise the referenced cell's name.
  - **Top level only.** Instances inside a placed cell are that cell's business; Push Into is how
    you get there. Say so in the empty-filter state, not in a tooltip nobody reads.
  - Excluded from a schematic's list: Ground, VAR, MEAS and other annotation symbols —
    `SchematicComponent.IsAnnotationSymbol` plus Ground — they are not what anyone is looking for,
    and dozens of grounds would bury the parts. **Decide this with the owner before building** if
    there is any doubt; it is one predicate.
- **R-fi-5** Two filters, combined with AND: a **text box** matching the instance name (a
  case-insensitive substring; a `*`/`?` wildcard is optional polish, not required), and a **type
  picker** populated from the types actually present in this document, with "All types" first.
  Rows sort by instance name with numbers compared as numbers — reuse
  `SchematicSortPlacement.NaturalNameComparer`, do not write a second one.

### Speed (the owner's word was "fast/responsive")

- **R-fi-6** The list is **virtualised** (`ListBox` with its default virtualising panel, or a
  `DataGrid`; not an `ItemsControl` in a `ScrollViewer`, which realises every row). Measure against a
  **10,000-instance layout** and a **2,000-component schematic** — build both in the test fixture,
  do not commit them.
- **R-fi-7** Rows are **rebuilt from the model, not tracked per edit**: on document switch, and on the
  model's own `Changed` event **debounced** (~150 ms) — never on every drag tick. A layout's
  `NotifyChanged(LayoutChangeInfo…)` already says whether instances changed; a shapes-only change
  must not rebuild the list. Nothing is done at all while the panel is hidden — rebuild when it
  becomes visible.
- **R-fi-8** Filtering runs over an in-memory array of row records (name, type, lower-cased name
  cached) — never re-reads the model per keystroke. Typing into the box must not allocate a new
  collection per character if avoidable; a `DataGridCollectionView`/filter predicate or a single
  replaced `List` is fine. Gate it with a **counter** (rows scanned per keystroke), not a timing
  test — see the repo's rule against new timing benchmarks.

### Double-click: zoom to it and select it

- **R-fi-9** **Layout:** `LayoutEditorViewModel.SelectInstance(index)` then
  `RequestZoomToRegion(bbox)`, where the bbox is `CellHierarchy.InstanceBbox(inst, baseDir)` grown by
  a margin so the part is not edge to edge. The document is activated first if it lost focus (the
  user may have clicked another tab — resolve the target from the view model the row came from, as
  `UpdateLayoutForWBond` does, never "the active document").
- **R-fi-10** **Schematic:** the schematic has **no view-model-level zoom request today** — only
  `SchematicCanvas.ZoomToFit` and the `RevealWorldRect` that Sort Placement added (pan-only unless
  the rect does not fit). Add a request on `SchematicDocument` shaped like
  `RequestZoomToFit`/`ZoomToFitRequested`, carrying a world rect, which `SchematicView` routes to the
  canvas. Zoom target is the component's `FullBb` (glyph plus labels) from the render model, grown by
  a margin, **zoomed in to a readable scale** — find means *show me it*, so this zooms, unlike
  Sort Placement's reveal. Select with `Selection` set to that one id.
- **R-fi-11** Enter in the list does what double-click does. A single click only highlights the row —
  it does **not** move the camera (arrowing down a list must not fling the canvas about).

### Menu, accelerator, focus

- **R-fi-12** **Design ▸ Find Instance…** in BOTH menus in `WorkspaceWindow.axaml` — the in-window
  `MenuItem` (with an icon, `InputGesture="Ctrl+F"`) and the macOS `NativeMenuItem`
  (`Gesture="Meta+F"`) — plus the two `KeyBinding`s (Ctrl+F and Meta+F). **Ctrl+F is currently free in
  the workspace window** (audited 2026-09-22: harmonicaRF uses it, but in its own window; the
  schematic view's bare `F` is Zoom to Fit and returns early for any modifier; the layout canvas
  checks `!ctrl` for `F`). Re-audit before claiming it.
- **R-fi-13** `FindInstanceCommand` with `CanExecute = IsSchematicDocumentActive ||
  IsLayoutDocumentActive`, re-notified from the same two places the Update Layout/Schematic
  commands' `NotifyCanExecuteChanged` already is (WorkspaceViewModel ~15170, ~15461). A greyed-out
  menu item is the requirement; do not hide it.
- **R-fi-14** The command shows the panel (docking it if closed, un-hiding it if auto-hidden,
  bringing its tab to front if tabbed behind another) **and focuses the search box with its text
  selected**, so typing replaces the last search. Use the `IActivatableTool`/`ActivationFocusRelay`
  mechanism — a panel can be activated before its view exists, and the pending flag is what makes
  the first Ctrl+F of a session work. **Do not** steal focus into the box on ordinary document
  switches; only the command does that.
- **R-fi-15** Escape in the search box returns focus to the document canvas.
- **R-fi-16** The bare letter keys the canvases bind (F, Z, W, Q, …) must not fire while the user is
  typing into the search box. The panel is outside the canvas's key route, so this should hold by
  construction — but check the window-level bindings, and add the panel's text box to whatever
  "is typing in a field" guard exists if one catches it.

### Not in scope

- Searching inside placed sub-cells, across documents, or across the workspace.
- Instances of the symbol editor's `.csym`, Data Display, or EM setup.
- Renaming or editing from the list.

---

## 2. Traps worth knowing up front

- **Selection identity differs per editor.** A schematic selects by `EditableComponent.Id` (stable
  GUID fragment); a layout selects instances by **index** into `LayoutView.Instances`, which shifts
  on any delete. A row must therefore hold the *object* (`LayoutInstance`) and look its index up at
  double-click time, never cache an index.
- **Stale rows.** If the model changed and the debounce has not fired yet, the double-clicked row may
  point at an instance that is gone — look it up, and if it is gone, rebuild and say nothing.
- **The layout bbox of a broken reference is empty.** Zoom to the instance's origin with a default
  span rather than doing nothing — the user still wants to find it (it is often *why* they are
  looking).
- **A layout reveal vs zoom.** `RequestRevealRegion` pans only; `RequestZoomToRegion` frames. Find
  wants the latter.

---

## 3. Tests (minimal — one per claim)

- Filter by name and by type, AND-combined, natural order (headless, on the view model).
- Rebuild is debounced and skipped for a shapes-only layout change (counter).
- Double-click on a layout row selects that instance and issues a zoom request whose region contains
  its bbox; same for a schematic row with the new schematic request.
- `FindInstanceCommand.CanExecute` is false with a Data Display focused and true with a `.clay`.
- The panel follows document switches and clears to the empty state when the last document closes.

GUI pixels cannot be verified from the agent's shell — say so in the completion note rather than
implying they were seen.

## 4. On completion

Findings go in `src/Ui/RESOLVED.md` (or a new `RESOLVED.md` beside the new view). **Never** in any
`CLAUDE.md`. Update `docs/user/reference/schematic-editor.html` and `layout-editor.html` with the
panel and the shortcut.
