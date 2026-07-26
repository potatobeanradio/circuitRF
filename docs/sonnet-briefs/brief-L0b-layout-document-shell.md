# Sonnet Brief — Phase L0b: layout document + editor shell

**Design:** `docs/design/layout-view.md` §0 (reuse map), §1.3 (display units are free), §4 (`.clay`),
§11 (phase L0). **Consumes L0a** — `src/Ui/Layout/` already holds the model, units, technology and
persistence; see the "Phase L0a — COMPLETE" note at the top of `src/Ui/CLAUDE.md`.

**Scope is L0b ONLY: make a layout view exist as a first-class, tear-off Dock document with a placeholder
canvas, a working `.clay` save/open lifecycle, and project-tree integration.** No rendering of geometry, no
tools, no undo, no technology resolution — those are L0c (`.ctech` editor + tech plumbing) and L1 (drawing).
This mirrors what 7.1a did for the Data Display: de-risk the document/dock/workspace integration on its own,
before anything heavy lands on top of it.

## Goal

**File → New Layout** opens a `LayoutDocument` tab that tears off and re-docks like a Schematic/Symbol tab;
it saves to `.clay` and reopens; a `.clay` inside a cell's `layout/` folder opens by double-click from the
project tree; and the document participates correctly in dirty tracking, Save All, close/quit prompts, and
`.cws` session restore. The body is a placeholder panel showing the layout's metadata — not its geometry.

## Verified substrate (consume — already exists)

- **L0a**: `LayoutView`, `LayoutPersistence.SaveToFile/LoadFromFile`, `LayoutUnits.Format/TryParse`,
  `LayoutGeometry.BboxOf`. All framework-free. **Do not modify L0a files** except where this brief says so.
- **`WorkspaceScanner` already lists `.clay` files.** `BuildCellNode` iterates `Enum.GetValues<ViewType>()`,
  so a `.clay` under `layout/` is *already* emitted as a `NodeKind.ViewFile` under a `NodeKind.CellViewFolder`,
  with primacy resolved through `CellFolder.ResolvePrimary(cellDir, ViewType.Layout)`. **No scanner change and
  no new `NodeKind` is needed** — verify this first, then wire only the *open* path and the context menu.
- **`SymbolEditorDocument`** (`src/Ui/Schematic/SymbolEditorDocument.cs`) is the clone template: `Document`
  subclass with `FilePath?` / `IsScratch` / `IsDirty` (mirrored from the VM via `PropertyChanged`) /
  `Materialize(path)` / `OnSavedAs(path, name)`.
- **The ScratchSymbol path** in `WorkspaceViewModel` (see `src/Ui/CLAUDE.md` §"Scratch symbols + New Symbol
  on launch" and `docs/design/scratch-and-save-lifecycle.md`): `NewScratchSymbol()`, `_scratchSymbols`,
  `NextScratchSymbolTitle()`, the save-target offer dialog, the orphaned-session sweep in
  `PromptSaveBeforeClose`, and `HasAnyDirtyWork`. **Mirror it.**
- **`WorkspacePersistence` / `.cws` open-document restore** already handles `kind="schematic" | "symbol" |
  "datadisplay"` (see `src/Ui/CLAUDE.md` §"Phase 7.1f"). Add `"layout"` the same way.
- `App.axaml` `Application.DataTemplates` maps document type → view. Add one entry.
- `CircuitRfDockFactory.OpenDocument` / `ForceCloseDockable` already do docking and force-close.

## Code changes

### 1. `src/Ui/Layout/LayoutDocument.cs` — `sealed class LayoutDocument : Document`

Clone `SymbolEditorDocument`'s shape, minus undo:

- `public LayoutEditorViewModel ViewModel { get; }`
- `public string? FilePath { get; private set; }`; `public bool IsScratch => FilePath is null;`
- `public bool IsDirty { get; private set; }` — on change, `Title = IsDirty ? $"• {_baseTitle}" : _baseTitle`.
- Constructor `(string title, LayoutEditorViewModel vm, string? filePath = null)`; subscribe to
  `vm.PropertyChanged` and mirror `vm.IsDirty` (the VM is the source of truth).
- `internal void Materialize(string filePath)` and `internal void OnSavedAs(string filePath, string cellName)`
  — same split as `SchematicDocument` (`Materialize` once for a scratch doc; `OnSavedAs` repeatable).

**Do NOT implement `IUndoableDocument`.** There is nothing to undo yet; undo arrives with L1's tools.

### 2. `src/Ui/Layout/LayoutEditorViewModel.cs` — `sealed partial class : ObservableObject`

Deliberately thin — it grows enormously in L1.

- `public LayoutView Model { get; }` (the L0a container).
- `[ObservableProperty] private bool _isDirty;`
- `[ObservableProperty] private LayoutUnit _displayUnit;` and `[ObservableProperty] private long _snapDbu;`
  — both write through to `Model` and set `IsDirty = true`.
- Read-only display properties for the metadata bar (see §3): `ResolutionText`, `SnapText`, `ShapeCountText`,
  `InstanceCountText`, `ExtentText`. Recompute on `DisplayUnit` change so the whole bar re-renders in the new
  unit. `ExtentText` uses `LayoutGeometry.BboxOf` unioned over `Model.Shapes` (`"—"` when empty).
- `SaveLayoutCommand` / `SaveLayoutAsCommand` as `IAsyncRelayCommand<Window?>`, delegating to
  `LayoutPersistence.SaveToFile` — mirroring `SymbolEditorViewModel`'s save commands exactly.

**On the two editable fields.** Display unit and snap grid are *document preferences*, not geometry
mutations — §1.3 R3 says a unit change "needs no undo entry beyond a view-preference change", and §1.5 R5
says a snap change never touches existing geometry. So they **dirty the document** (they are persisted in
`.clay`) but do **not** go on an undo stack. They exist in L0b for a specific reason: they are the smallest
real edit available, so they make the dirty/save/restore machinery testable, and the display-unit combo is
the live demonstration of §1.3 — geometry numbers re-render, geometry bytes do not move.

### 3. `src/Ui/Views/Layout/LayoutEditorView.axaml(.cs)` (namespace `CircuitRF.Ui.Views.Layout`)

A placeholder `UserControl`, `x:DataType="lay:LayoutEditorViewModel"`:

- A full-bleed canvas-colored surface using circuitRF theme brushes (match other document bodies —
  `SystemChromeLowColor` / `SystemRegionColor`). **No geometry is drawn.** A centered muted empty-state hint
  (Material icon + ~13px `SystemBaseMediumColor` text) reading something like
  *"Layout canvas — drawing tools arrive in L1."*
- A **metadata bar** along the bottom (thin, `SystemChromeMediumLowColor`, ~11px labels in the
  `TextBlock.label` idiom used elsewhere) showing: resolution (`1 DBU = 1 nm`), a **Display unit ComboBox**
  bound to `DisplayUnit`, a **snap** entry showing `SnapText`, shape count, instance count, and extent.
  The bar is what makes this phase demonstrable and testable; keep it plain.
- Standard `InitializeComponent()` code-behind only; `StorageProvider` file-picking stays in code-behind per
  the UI firewall, exactly as `SymbolEditorView` does it.

Add the tear-off host if Symbol has a dedicated one (`SymbolEditorWindow`) — mirror whatever that pattern is
so the same view hosts both docked and torn-off, with only the chrome differing.

### 4. `App.axaml` — document → view mapping

```xml
<DataTemplate DataType="{x:Type lay:LayoutDocument}">
    <layv:LayoutEditorView/>
</DataTemplate>
```
with `xmlns:lay="using:CircuitRF.Ui.Layout"` and `xmlns:layv="using:CircuitRF.Ui.Views.Layout"`.

### 5. `WorkspaceViewModel` — lifecycle

Mirror the New Symbol path throughout:

- **`NewLayoutCommand`** — build a `LayoutView` (defaults: `DbuPerMicron = 1000`, `DisplayUnit = Um`,
  `SnapDbu = 1000`, `AngleMode = AnyAngle`, `TechRef = null`), wrap in a `LayoutEditorViewModel` and a
  `LayoutDocument` titled from `NextScratchLayoutTitle()` (lowest free `"Untitled-Layout-N"`), track in
  `_scratchLayouts`, open via `_factory.OpenDocument`. Scratch-first: **no workspace required.**
  `TechRef` stays null in L0b — L0c stamps the workspace default technology here.
- **Save / Save As** — reuse the symbol save-target offer dialog pattern: a scratch layout saved with a
  workspace open offers the cell's `layout/` sub-folder; otherwise a plain file picker. On success call
  `Materialize` (scratch) or `OnSavedAs` (already materialized), and update `_openDocsByPath`.
- **`OpenLayoutFileCommand`** — File → "Open Layout…", `.clay` filter, dedupe against `_openDocsByPath`,
  `await using` the stream (macOS security-scoped), errors via the existing `ShowErrorAsync`.
- **Ctrl/Cmd+S** — `SaveAllDocuments` must recognise an active `LayoutDocument` and dispatch to it, the same
  way it already branches for `DataDisplayDocument`.
- **Close / quit** — `HasAnyDirtyWork`, `PromptSaveBeforeClose`, `ConfirmCloseDockable` must all handle
  `LayoutDocument` without throwing, prompt when dirty, and remove it from `_scratchLayouts` /
  `_openDocsByPath` on close.
- **Workspace teardown** — `ResetToBlankShell` and the Remove/Rename Cell paths must `ForceCloseDockable` any
  open layout under the affected cell directory, exactly as they do for `.csch`/`.csym`.

### 6. Project tree — open, save, dirty

The scanner already produces the nodes. Wire the actions:

- `OpenNode` — a `ViewFile` node whose extension is `.clay` opens via the same
  open-or-activate path as `.csch`/`.csym`.
- **Cell context menu** — add "Open Layout" next to the existing "Open Schematic" / "Open Symbol" items,
  enabled when `CellFolder.ResolvePrimary(cellDir, ViewType.Layout)` yields a primary.
- `ITreeActions.IsNodeDirty` / `SaveNodeAsync` — extend both to cover `.clay` (open layout documents),
  so the "Save" context item and the cell dirty dot behave as they do for the other two views.
- `SaveCellViewsAsync` — include dirty layouts when saving a whole cell.

### 7. `.cws` session restore

`WriteWorkspaceFile` persists open `LayoutDocument`s as `kind="layout"` with their path;
`RestoreOpenDocuments` adds the `"layout"` case. Mirror the `"datadisplay"` case added in 7.1f.

### 8. Menu surface

**File → New Layout** and **File → Open Layout…**, placed next to the New/Open Symbol items, in both the
in-window `MenuItem` menu and the macOS `NativeMenuItem` menu. An accelerator only if one is genuinely free —
otherwise menu-only. (Watch the macOS `$parent[Window]` gotcha documented in `src/Ui/CLAUDE.md`.)

## Scope guardrails (do NOT do in L0b)

- **No geometry rendering.** No Skia control, no `SKPath`, no spatial index, no pan/zoom, no LOD.
  The canvas is a static placeholder.
- No tools, no selection, no hit-testing, no vertex editing, no clipboard, no `IUndoableDocument`/undo (L1).
- No `.ctech` editor, no `tech/` folder scanning, no `.cws` default-tech field, no technology resolution or
  missing-tech fallback (L0c). `TechRef` is written and read but never followed.
- No hierarchy, no instances placed, no push-in/pop-out, no session registry (L3).
- No Clipper2, no GDSII/DXF/Gerber, no DRC, no EM.
- Don't touch `src/Core`, `src/Engine`, `RfCore`, or any existing Schematic **model** file. Editing
  `WorkspaceViewModel` / `WorkspaceScanner` call sites / `App.axaml` / the menus is expected.

## Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. **New Layout** opens an `Untitled-Layout-1` tab; a second opens `Untitled-Layout-2`.
3. The tab **tears off** into its own window and **re-docks**, matching Schematic/Symbol behaviour.
4. **Save As** writes a `.clay`; `LayoutPersistence.LoadFromFile` on the written file returns a `LayoutView`
   equal to the document's model; the tab title loses its dirty dot and gains the file's name.
5. **Reopen round-trip** — save a layout, close the tab, reopen it from the file picker: `DisplayUnit`,
   `SnapDbu`, `DbuPerMicron` and `AngleMode` all survive.
6. **Tree integration** — a `.clay` placed in `<cell>/layout/` appears under the cell's layout view folder
   (assert the scanner already does this, don't re-implement it), and double-clicking it opens the document.
   "Open Layout" on the cell context menu opens the primary.
7. **Metadata bar is correct.** Loading a fixture with known content shows the right shape and instance
   counts and the right extent. Switching the display-unit combo from µm to mil **re-renders every number in
   mils and marks the document dirty**; saving and reloading shows the geometry is byte-for-byte unchanged.
   This is §1.3 R3 demonstrated in the UI — make it an explicit test, not just a manual check.
8. **Dirty participation** — a dirty layout blocks a silent close, appears in the Save All sweep, shows the
   project-tree dirty dot and the "Save" context item, and prompts on quit.
9. **`.cws` restore** — open layouts reopen on the next workspace load.
10. **Remove Cell** on a cell with an open layout force-closes that tab without a prompt.

## On completion

1. Add a "Phase L0b — COMPLETE" entry at the top of `src/Ui/CLAUDE.md` in the established style: the new
   `LayoutDocument` / `LayoutEditorViewModel` / `LayoutEditorView`, the New/Open/Save commands, the tree and
   `.cws` wiring, the metadata bar, and the test file names. Note explicitly that the scanner needed no change
   and why — future-you will otherwise go looking for a layout-specific `NodeKind`.
2. Report back before L0c (`.ctech` editor document, `tech/` folder + `.cws` default technology, technology
   resolution with the missing-tech fallback, and the layer-color live-refresh seam) is briefed.
