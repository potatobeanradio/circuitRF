# UI (Avalonia) — local conventions

Standing instructions for `src/Ui`. Read with the root `CLAUDE.md`, the interaction spec
`docs/design/ui-design.md`, and the architecture/firewall note `docs/design/ui-architecture.md`. The UI is
how people drive the engine; it must never become the source of truth for simulation.

## Scratch documents + New Schematic (Phase 6h Step 1 — done)

**Scratch = in-memory, no path, dirty, tree-invisible.** A scratch `SchematicDocument` is a normal
`SchematicViewModel`/`SchematicDocument` with `FilePath = null`. It is NOT in `_openDocsByPath` (which is
keyed by absolute path) and is NOT shown in the project tree (the tree reflects disk only).

### SchematicDocument scratch identity
- `FilePath: string?` — null for scratch; set to the on-disk `.csch` path for materialized docs.
  Step 2 (materialize-ancestors) will set this once at save time.
- `IsScratch => FilePath is null` — computed flag.
- `IsDirty: bool` — scratch starts `true` and stays true in step 1 (no save path yet).
  On-disk docs start `false` and flip to `true` on the first undoable edit (`CanUndo` flips).
- Tab title = `"• " + baseTitle` when `IsDirty`; plain title when clean.

### WorkspaceViewModel scratch tracking
- `_scratchDocs: List<SchematicDocument>` — open scratch docs. NOT `_openDocsByPath`.
  **NOTE (step 1):** entries are not removed when a tab is closed (no Dock close callback yet).
  The close-prompt and cleanup come in step 3. `_scratchDocs.Clear()` is called in `NewWorkspace`
  (which resets the Dock layout, so tabs are gone). `OpenWorkspace` does NOT clear it (tabs survive).
- `RebuildOpenSchematics()` iterates both `_openDocsByPath.Values` and `_scratchDocs` so scratch
  schematics re-resolve cell-ref symbols after a symbol save or Make-Primary.

### NewScratchSchematicCommand (⇧⌘N / Ctrl+Shift+N)
- Parameterless `[RelayCommand]`, always enabled — **no workspace required**.
- Creates `SchematicEditModel` → `SchematicViewModel` → `SchematicDocument(title, vm)` (null path).
- Title = next free `Untitled-Schematic-N` (lowest N not already in `_scratchDocs` or `_openDocsByPath`).
- Adds doc to `_scratchDocs`, opens via `_factory.OpenDocument(doc)`.
- Bound to File → New Schematic in the macOS NativeMenu (`Meta+Shift+N`) and in-window Menu
  (`Ctrl+Shift+N`). **New Cell (workspace-required) has no keyboard shortcut** — it was displaced.

### Launch state
At startup, the constructor auto-opens one `Untitled-Schematic-1` scratch tab so the app lands directly
on an editable canvas. The welcome message is "circuitRF ready." (not "Create a New Schematic to get
started" — the schematic is already open).

### Materialize + SavePlanDialog (Phase 6h Step 2 — done)

**`SchematicDocument.Materialize(string filePath)`** — `internal` method that sets `FilePath` and clears
`IsDirty`. The one-way scratch→materialized transition. Also used to clear dirty on re-save of materialized
docs. `FilePath` now has `private set` (was readonly in step 1).

**`SavePlan` / `SavePlanBuilder`** (`src/Ui/Schematic/SavePlan.cs`) — framework-free plan model and builder.
`SavePlanBuilder(currentWorkspacePath, workspaceParentDir, scratchDocs).Build(mode, overrides)` produces
`SavePlan { WorkspaceStep?, IReadOnlyList<CellStep>, IReadOnlyList<SaveStep> }`. De-duplicates cell steps
by name. `SchematicHasAnalyses` returns false (TODO 6e hook for analysis→TestBench detection). `SaveMode`:
`EachOwnCell` (default) / `AllInOneCell`.

**`SavePlanExecutor.ExecuteFileOps(SavePlan, existingWorkspaceDir?)`** (`SavePlanExecutor.cs`) — framework-free
static method; creates workspace/.cws, cell folders/.ccell (sets IsTestBench), writes .csch files, sets
PrimarySchematic in .ccell, calls `Materialize` on each doc. Returns list of all files written.

**`WorkspaceViewModel.ExecuteSavePlan(SavePlan)`** — calls `SavePlanExecutor.ExecuteFileOps`, updates
`CurrentWorkspacePath` + `_lastWorkspaceParentDir` (if new workspace), moves docs from `_scratchDocs` to
`_openDocsByPath`, re-wires project tree, calls `Refresh()`, reports all written paths via `Messages.Success`.

**`WorkspaceViewModel.SaveAllDocumentsCommand`** (`[RelayCommand] async Task SaveAllDocuments(Window? owner)`)
— the new ⌘S/Ctrl+S handler:
1. Dirty scratch docs → `SavePlanBuilder.Build` → `SavePlanDialog.ShowDialog<SavePlan?>` → on confirm, `ExecuteSavePlan`
2. Dirty materialized docs → `SchematicPersistence.SaveToFile` + `Materialize` directly
3. `WriteWorkspaceFile` if workspace exists
- Returns "Nothing to save" info if nothing is dirty.

**`SavePlanDialog`** (`src/Ui/Views/Dialogs/SavePlanDialog.axaml(.cs)`) — HIG plan dialog:
- Title "Save your work" / subtitle "circuitRF will create the following and save your documents."
- Mode toggle (`EachOwnCellRadio` / `AllInOneCellRadio` + `SharedCellNameBox`) visible when cells will be created
- Plan table (`PlanRowsPanel` StackPanel): workspace rows (FolderOutline icon), cell rows (Folder icon + TestBench
  badge), save rows (FileOutline icon + primary badge). Rows built programmatically in code-behind.
- Inline `NameValidator` errors per editable row (OrangeRed text below each row).
- **Save All** (`SaveAllButton`, `IsDefault=True`, `HorizontalContentAlignment=Center`) / **Cancel** (`IsCancel=True`)
- Returns confirmed `SavePlan` or null on cancel via `ShowDialog<SavePlan?>`.

**⌘S/Ctrl+S routing:** `WorkspaceWindow.axaml` binds both `NativeMenuItem` and `KeyBinding` for ⌘S/Ctrl+S
to `SaveAllDocumentsCommand`. `SaveWorkspaceAsCommand` remains bound to ⌘⇧S/Ctrl+Shift+S. Menu item now
reads "Save All" (macOS NativeMenu + in-window Menu).

**Scope fence (step 2):** into-cell Save-All only. Close/quit prompts, autosave, and loose/plain-file tiers
are step 3.

### Three-tier save + close/quit prompts + autosave/recovery (Phase 6h Step 3 — done)

**Tier 2 — loose Known File (`SaveLooseSchematicCommand`, bound to File → "Save Schematic As…"):**
`SaveLooseToWorkspace(doc, owner)` shows a file picker → writes `.csch` → atomically updates `.cws`
(`WorkspacePersistence.SaveToFileAtomic`) adding the path to `CwsFile.KnownFiles` → scratch→materialized
transition (`_scratchDocs.Remove`, `_recovery.ClearDoc`, `doc.Materialize`, `_openDocsByPath[fp] = doc`).

**Tier 3 — no workspace (`SaveLooseNoWorkspace`):** `SaveChangesDialog` with "Create Workspace…" /
"Save as File" / Cancel. "Create Workspace…" routes to the full plan dialog (same as ⌘S). "Save as
File" → `SaveLoosePlainFile` (file picker, plain `.csch`, no workspace registration,
`_recovery.ClearDoc`).

**Close/quit prompts:**
- **Tab close** — `CircuitRfDockFactory.CloseDockableConfirm: Func<IDockable, Task<bool>>?` wired in
  constructor; `CloseDockable` is `async void` override: awaits hook, returns without calling base on
  cancel. `ConfirmCloseDockable` shows `SaveChangesDialog` per dirty `SchematicDocument`.
- **Window close / quit** — `WorkspaceWindow.OnClosing` async override: `e.Cancel = true`, await
  `PromptSaveBeforeClose`, then `_vm.OnCleanExit(); _closingConfirmed = true; Close()`.
- **NewWorkspace / OpenWorkspace / OpenRecentWorkspace** — `HasAnyDirtyWork()` guard; on dirty,
  `PromptSaveBeforeClose` (Save All / Don't Save / Cancel); Cancel aborts the navigation.
- **`PromptSaveBeforeClose`** — collects dirty scratch + dirty materialized; single dialog message;
  Save → plan dialog for scratch + direct write for materialized; DontSave → proceed; Cancel → false.

**`SaveChangesDialog`** (`src/Ui/Views/Dialogs/SaveChangesDialog.axaml(.cs)`) — now configurable:
constructor accepts `message`, `saveLabel`, `dontSaveLabel`, `cancelLabel`; `Close(Result)` on each
button so both `ShowDialog` and `ShowDialog<T>` return correctly. `SizeToContent="WidthAndHeight"`.

**Autosave/recovery — `RecoveryManager`** (`src/Ui/Schematic/RecoveryManager.cs`, framework-free):
- **Session dir:** `LocalApplicationData/circuitRF/recovery/<12-char-hex-guid>/` (created lazily).
- **`AutoSave(doc)`** — atomic `.csch` write (temp + `File.Move(..., overwrite: true)`); silently
  swallows I/O errors (autosave must never interrupt editing).
- **`ClearDoc(doc)`** — removes one recovery file when a doc is cleanly saved/materialized. Prunes
  empty session dir.
- **`ClearSession()`** — removes entire session dir on clean exit.
- **`FindPriorSessions(currentSessionDir)`** / **`LoadSession(sessionDir)`** / **`DeletePriorSession`**
  — discovery + deserialize for restore offer.

**Wiring in `WorkspaceViewModel`:**
- `RecoveryManager _recovery` initialized in constructor.
- `StartAutosaveTimer()` — `DispatcherTimer` (30 s interval) → `AutoSaveAll()`.
- `AutoSaveAll()` — iterates `_scratchDocs.Where(IsDirty)`, calls `_recovery.AutoSave`.
- `CheckForRecovery()` (async void) — deferred via `Dispatcher.UIThread.Post(..., Background)`;
  finds prior sessions; shows restore dialog; on accept: opens recovered docs as new scratch tabs;
  on decline: calls `DeletePriorSession`.
- `OnCleanExit()` — stops timer, calls `_recovery.ClearSession()`. Called before confirming quit.
- `_recovery.ClearDoc` at every materialization point: `ExecuteSavePlan` (per save step),
  `SaveLooseToWorkspace`, `SaveLoosePlainFile`.
- `OnDockableClosed` (subscribed to `_factory.DockableClosed`) — removes closed docs from
  `_scratchDocs` and `_openDocsByPath`.

**Scratch-only invariant:** autosave never touches materialized docs. Once a doc is materialized
(removed from `_scratchDocs`), no recovery file is created or offered for it.

---

## New Workspace dialog + Open Workspace + Recent Workspaces (Phase 6g Step 5 fix 5)

**File → New Workspace uses `NewWorkspaceDialog`** (`src/Ui/Views/Dialogs/NewWorkspaceDialog.axaml(.cs)`),
NOT a raw system folder picker. The user names a *Workspace*; circuitRF creates the folder.

Key rules:
- The system folder picker (`OpenFolderPickerAsync`) is used **only** behind the "Choose…" button to
  select the **parent location** — an existing folder is fine for that.
- The workspace folder = `parent/<name>/` — always created by us, never pre-existing (dialog gates OK
  on this + `NewWorkspace` re-checks at create time as a race guard).
- `NewWorkspaceDialog` returns `NewWorkspaceResult { ParentDir, Name }` via `ShowDialog<NewWorkspaceResult?>`,
  or null on cancel — mirrors `InputNameDialog`'s return-or-null contract.
- Name validated live via `NameValidator`; workspace name comes from the folder leaf, never the `.cws` stem.
- On open, the name field is **prefilled** with the next free `Untitled-Workspace-N` for the current Location.
  When the user changes Location via "Choose…" without having manually edited the name, the suggestion is
  recomputed for the new location. Suppression flag `_settingSuggested` prevents `OnNameChanged` from
  marking the programmatic fill as a user edit.

### Tracked Location (in-memory, not persisted)
`WorkspaceViewModel._lastWorkspaceParentDir` (initialized to Documents): seeds the Location field in
`NewWorkspaceDialog` and the `SuggestedStartLocation` of `OpenFolderPickerAsync`. Updated after every
successful New or Open to the parent of the workspace folder. **Never persisted.**

### Open Workspace = folder picker
`OpenWorkspace` uses `OpenFolderPickerAsync` (not file picker). The selected folder IS the workspace
folder; `.cws` = `Path.Combine(folder, ".cws")`. If `.cws` does not exist, the open is rejected with a
clear error message. Menu item reads "Open Workspace…" in both NativeMenu and in-window Menu.

### Recent Workspaces (persisted in AppPreferences)
- `AppPreferences.RecentWorkspaces: List<string>?` — the `.cws` paths, MRU order, capped at 10.
  Serialized as `recent_workspaces` in `preferences.json`. Null when empty (omitted from JSON).
- `WorkspaceViewModel.PushRecent(cwsPath)` — dedup (case-insensitive), insert at front, cap 10, save,
  rebuild menu items. Called after every successful `NewWorkspace` and `OpenWorkspace`.
- `WorkspaceViewModel.RecentMenuItems: ObservableCollection<Control>` — holds `MenuItem` + `Separator`
  instances rebuilt by `RebuildRecentMenuItems()`. Bound to the in-window "Open Recent" submenu via
  `ItemsSource`. `HasRecentWorkspaces` (bool property, notified on change) drives `IsEnabled`.
- `WorkspaceViewModel.RecentWorkspacesChanged: event Action?` — fired after every push/clear so
  `WorkspaceWindow.axaml.cs` can rebuild the NativeMenu.
- **NativeMenu rebuild**: `WorkspaceWindow.axaml.cs` subscribes to `RecentWorkspacesChanged` in
  `OnDataContextChanged`. `EnsureNativeRecentMenuWired()` (called once from `OnOpened`) inserts the
  "Open Recent" `NativeMenuItem` programmatically after "Open Workspace…" and populates it.
  `RebuildNativeRecentMenu()` clears and repopulates `NativeMenuItem.Menu.Items` on every change.
- **Missing entry**: if a recent workspace's `.cws` no longer exists, `OpenRecentWorkspace` removes it
  from the list, saves, rebuilds menus, and shows an error.
- `ClearRecentWorkspaces` command empties the list, saves, and rebuilds both menus.

## macOS / command gotchas (Phase 6g Step 5 fixes)

### `$parent[Window]` is null on macOS for NativeMenu and KeyBinding
`{Binding $parent[Window]}` resolves to `null` for `NativeMenuItem.CommandParameter` and
`KeyBinding.CommandParameter` on macOS — neither lives in the Avalonia visual tree where the ancestor
walk can reach a `Window`. The standard fix is a `ResolveOwner(Window? parameter)` helper in the ViewModel:

```csharp
private Window? ResolveOwner(Window? parameter) =>
    parameter
    ?? (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
       ?.Windows.FirstOrDefault(w => ReferenceEquals(w.DataContext, this));
```

`desktop.MainWindow` is also null in this app (`App.axaml.cs` calls `window.Show()` but never assigns
`desktop.MainWindow`), so `.MainWindow` is the wrong fallback. Walking `ApplicationLifetime.Windows`
keyed by `ReferenceEquals(w.DataContext, this)` finds the exact host window and works correctly in
multi-window scenarios. Apply `ResolveOwner` to every command that takes `Window?` and opens a picker.

### `CreateDefaultLayout()` replaces all factory tools
`CircuitRfDockFactory.CreateDefaultLayout()` internally calls `CreateLayout()` which assigns new
instances to `ProjectTreeTool`, `PropertiesTool`, etc. Any command that resets the layout (currently
only `NewWorkspace`) must re-wire those tools **after** `Layout = newLayout`:

```csharp
Layout = newLayout;
_factory.ProjectTreeTool?.SetActions(this);
_factory.ProjectTreeTool?.SetWorkspace(workspaceDir);
```

`SetActions` before `SetWorkspace` because `SetWorkspace` → `RebuildVmTree` uses `_actions`.

### Dock `Tool.Title` PropertyChanged is not reliably picked up by Avalonia compiled bindings
Setting `Title` on a Dock `Tool` (base class) calls `SetProperty` which fires `PropertyChanged` via
the Dock library's `ObservableObject`. However, Avalonia compiled bindings (`x:DataType`) on the
tool's view do not reliably pick up this event in practice. Expose a separate `[ObservableProperty]`
for any observable header text the view needs to bind to:

```csharp
[ObservableProperty] private string _workspaceName = "No workspace";
```

`ProjectTreeTool.Title` is **static "Project"** — set once in the constructor, never updated per workspace.
The in-view header `TextBlock.Text` binds to `WorkspaceName` (set in `SetWorkspace`, reset to "No workspace"
in `ClearWorkspace`). Do NOT update `Title` per workspace — the dock-tab label is intentionally always "Project".

### `.cws` is a dotfile — derive the workspace name from the FOLDER, not the file stem
A circuitRF workspace is a **named folder containing a `.cws` file**: `…/<name>/.cws`.
`Path.GetFileNameWithoutExtension(".cws")` in .NET returns `".cws"` (dotfiles have no extension), NOT the
workspace name. Always derive the workspace name from the **folder name**:

```csharp
var dir  = Path.GetDirectoryName(cwsPath);       // …/<name>
var name = Path.GetFileName(dir);                 // <name>
```

Apply this everywhere the workspace name must be displayed (window title, `WorkspaceName`, messages).

---

## Cell reference model + live update (Phase 6g Step 5 — done)

### Entry points (L1)
`NewWorkspace` creates a real `.cws` + workspace folder on disk (was only resetting dock state).
`File → New Cell` menu item (`NewCellInWorkspaceCommand` on `WorkspaceViewModel`, greyed via
`CanExecute = CurrentWorkspacePath is not null`; `NotifyCanExecuteChanged` in `OnCurrentWorkspacePathChanged`).
Tree-header **New Cell** button in `ProjectTreeView` (`IsVisible="{Binding HasWorkspace}"`).
`ITreeActions.NewCellInWorkspaceAsync()` is the shared implementation: prompts with `InputNameDialog`,
validates with `NameValidator`, calls `CellFolder.CreateCellFolder(workspaceDir, name)` + `Refresh()`.

### Cell-ref data model (L2)
**`EditableComponent.CellRef: string?`** — relative path from the schematic directory to the referenced
cell folder.  Null for built-in components.  Round-tripped through `CschComponent.CellRef` (nullable,
`WhenWritingNull`; omitted from file when null).

**`SchematicEditModel.SchematicDirectory: string?`** — absolute directory of the containing `.csch` file.
Set by `SchematicPersistence.FromFileModel` from the directory argument passed by `LoadFromFile`.
Used as the base for resolving `CellRef` relative paths.

**`CellSymbolResolver`** (`src/Ui/Schematic/CellSymbolResolver.cs`) — framework-free static resolver.
`Resolve(cellRef, baseDir) → CellSymbolResolution { State: CellSymbolState, Symbol? }`.
`CellSymbolState` enum: `Resolved / NotFound / PrimaryMissing` (kept distinct — do NOT collapse).
Cache keyed by `(cellAbsDir, primaryFilename, symFileMtime)`; invalidated by `Invalidate(cellAbsDir)`
or `InvalidateAll()`.  Resolution chain: relative path → `Directory.Exists` → `CellFolder.ResolvePrimary`
(single primacy source) → `SymbolPersistence.LoadFromFile`.

### Three-state rendering (L3)
**`SchematicComponent`** gained `CellRefState: CellSymbolState?` and
`CellRefPrimitives: IReadOnlyList<SymbolPrimitive>?` (both null for built-ins).

**`BuildRenderModel`** pre-resolves all `CellRef` values via `ResolveAllCellRefs()` before the
connectivity pass.  Resolved symbol pins supply port world-coords (not `SymbolPortDefs`).
`ToRenderComponent(isConnected, cellRefResolution?)` uses resolved pins for ports and resolved
primitives for the glyph BB.

**`SchematicRenderer`** dispatches on `CellRefState` before the built-in draw path:
- `Resolved` → `DrawSymbol(c.CellRefPrimitives, ...)` — same path as built-ins, no `DrawVariadicPortLeads`
- `NotFound` → `DrawCellRefNotFoundGlyph` — warning fill+stroke box, "Not Found" centred label
- `PrimaryMissing` → `DrawCellRefPrimaryMissingGlyph` — plain stroke rectangle stand-in
- `null` (built-in) → existing `BuiltInSymbols.Primitives` + `DrawVariadicPortLeads` path, unchanged

### Live update (L4)
**`SymbolEditorViewModel.SymbolSaved: event Action<string>?`** fires from `PerformSave` with the
absolute `.csym` path.  Both Save and Save-As go through `PerformSave`.

**`SchematicViewModel.TriggerRebuild()`** calls `EditModel.NotifyChanged()` — reuses the same
`Changed → RebuildRenderModel` pipeline used by all mutation commands.

**`WorkspaceViewModel`** wiring:
- `OnSymbolSaved(savedSymPath)` — derives `cellDir` (two `GetDirectoryName` calls up), calls
  `CellSymbolResolver.Invalidate(cellDir)`, then `RebuildOpenSchematics()`.
- `RebuildOpenSchematics()` — iterates `_openDocsByPath.Values`, calls `TriggerRebuild()` on every
  `SchematicDocument`.
- `MakePrimary` — after writing a new primary symbol (`subFolderName == CellFolder.SymbolSubFolder`),
  calls `Invalidate(cellDir)` + `RebuildOpenSchematics()`.
- `OpenOrActivateSymbol` and `NewSymbolAsync` both subscribe `vm.SymbolSaved += OnSymbolSaved` when
  the `SymbolEditorViewModel` is created.

**Dangling wires on pin-count change:** wires to ports that no longer exist in the new symbol show
as unconnected (dangling). No auto-rewire (Option B still deferred).

---

## Project Tree interactions (Phase 6g Step 4 — done)

**ITreeActions** (`src/Ui/ViewModels/ProjectTree/ITreeActions.cs`) — callback interface implemented by
WorkspaceViewModel, injected into `ProjectTreeNodeViewModel` via `ProjectTreeTool.SetActions(ITreeActions)`.
Every tree-node command delegates to this interface so all open/create/reveal operations live in WorkspaceViewModel.

**Commands on ProjectTreeNodeViewModel** — `ActivateCommand` (double-click open/activate),
`MakePrimaryCommand` (view files), `RevealCommand` (all file/folder nodes), `NewCellCommand`
(workspace/library nodes), `NewSymbolCommand` / `NewSchematicCommand` (cell nodes).  Context-menu
`IsVisible` driven by `IsViewFile`, `IsCell`, `IsWorkspaceOrLibrary`, `CanReveal`.

**Open/activate dedup** — `WorkspaceViewModel._openDocsByPath` (`Dictionary<string, IDockable>`,
OrdinalIgnoreCase) tracks open docs by absolute path. `ActivateIfOpen(absPath)` checks before opening;
activates the existing tab if found. Users can close a tab and reopen from the tree without issue.

**Double-click open paths:**
- `.csym` → `SymbolPersistence.LoadFromFile` + `EditableSymbol.FromSymbol` + `SymbolEditorDocument` (real)
- `.csch` → `SchematicPersistence.LoadFromFile` + `SchematicViewModel` + `SchematicDocument` (real)
- cell node → `CellParameterEditorDocument` (real, step 6 — see below)
- `.clay`, other view-file types, data displays, color themes → no-op

**Make Primary** — reads `.ccell` from `../..` of the view file path, sets the correct `PrimarySchematic` /
`PrimarySymbol` / `PrimaryLayout` field (discriminated by sub-folder name), writes back, calls `Refresh()`.
When the changed view is a symbol, also calls `CellSymbolResolver.Invalidate(cellDir)` + `RebuildOpenSchematics()`
so open schematics re-render with the new primary (Step 5 live-update).

**Reveal** — `Process.Start`: macOS `open -R <path>`, Windows `explorer /select,"<path>"`,
Linux `xdg-open <parent-dir>`.  Platform detected via `RuntimeInformation.IsOSPlatform`.

**Creation actions** — `InputNameDialog` (`src/Ui/Views/Dialogs/`) prompts for name, validated with
`NameValidator`.  On confirm:
- New Cell → `CellFolder.CreateCellFolder(parentDir, name)` + `Refresh()`
- New Symbol → write empty `.csym` via `SymbolPersistence.SaveToFile` + open `SymbolEditorDocument` with
  fresh `EditableSymbol { UserEditable=true, CurrentSymbolPath=path }` + `Refresh()`
- New Schematic → write empty `.csch` via `SchematicPersistence.SaveToFile` + open `SchematicDocument` with
  new `SchematicViewModel(emptyModel)` + `Refresh()`
- New Layout → `IsEnabled=False` (greyed, v2)

`RevealLabel` on the VM is platform-aware ("Reveal in Finder" / "Reveal in Explorer" / "Reveal in File Manager").

## Cell-parameter editor (Phase 6g Step 6 — done)

**Purpose:** edits the cell's **declared parameter interface** in its `.ccell` — add / remove / rename rows +
defaults (Name / Default / Unit / Dimension / ShowOnSchematic). NOT instance values. The delta vs. the instance
editor (`ParameterEditorViewModel`): rows are add/remove/rename-able; Name is editable.

### Edit model (framework-free)
**`CellParameterEditModel`** (`src/Ui/Schematic/CellParameterEditModel.cs`) — wraps a `CcellFile` + `.ccell`
path. `IReadOnlyList<CcellParameter> Parameters` (read view); `internal List<CcellParameter> MutableParameters`
(command access); `Save()` writes `.ccell`; `NotifyChanged()` fires `Changed` event so the VM rebuilds rows.

### Commands (framework-free, `src/Ui/Commands/Cell/`)
- **`AddCellParameterCommand`** — appends; Undo removes by reference.
- **`RemoveCellParameterCommand`** — records insertion index; Undo re-inserts at saved index.
- **`SetCellParameterCommand`** — stores full old/new snapshot for Name, Default, Unit, Dimension, Show.
  Covers rename, default edit, unit/dimension/show changes. Both Execute and Undo persist + notify.

### ViewModel (`src/Ui/ViewModels/CellParameterEditorViewModel.cs`)
Owns its own `UndoRedoStack` (per the per-document-undo rule). Subscribes to `_editModel.Changed`;
`RebuildRows()` clears + recreates `ObservableCollection<CellParameterRowViewModel>` on every mutation or undo.
`AddParameterCommand` generates a unique `ParamN` name. `UndoCommand`/`RedoCommand` delegate to own stack.

### Row VM (`src/Ui/ViewModels/CellParameterRowViewModel.cs`)
Staged Name (editable), Default, Unit, Dimension, ShowOnSchematic. Commit methods called from code-behind
(LostFocus/Enter for TextBoxes; SelectionChanged for ComboBoxes). `partial void OnStagedNameChanged`:
shows `RenameWarning` while name diverges from model. `partial void OnShowOnSchematicChanged`: auto-commits.
`CommitName` validates `[A-Za-z_][A-Za-z0-9_]*`; reverts on invalid. `CommitDimension` resets unit to first
valid option for the new dimension. `AllDimensions` = `Enum.GetValues<UnitDimension>()` (static array).

### Document (`src/Ui/Schematic/CellParameterEditorDocument.cs`)
`Document + IUndoableDocument` — `UndoRedo => ViewModel.UndoRedo`. Keyed by cell folder path in
`WorkspaceViewModel._openDocsByPath`. Workspace undo routing routes to its stack while active.

### View (`src/Ui/Views/Content/CellParameterEditorView.axaml(.cs)`)
`x:DataType="CellParameterEditorDocument"`. Layout: header (cell name + "Parameters" label), column-header
row, scrollable `ItemsControl` (rows), footer (Add Parameter button). `Grid.IsSharedSizeScope="True"` on the
outer container aligns header columns with row columns (`CpName`, `CpUnit`, `CpDim`, `CpShow`, `CpRemove`).
Code-behind: `_suppressUnitCommit` + `_suppressDimCommit` flags prevent re-entrant SelectionChanged commits.
DataTemplate registered in `App.axaml`.

### Wiring
`WorkspaceViewModel.OpenOrActivateCellPlaceholder` (step 4 stub) replaced: loads `.ccell` via
`CellPersistence`, creates `CellParameterEditModel` + `CellParameterEditorViewModel` + `CellParameterEditorDocument`,
opens via `_factory.OpenDocument`, keyed by cell folder path in `_openDocsByPath`. Dedup/activate already open.

**Scope fence:** cell-parameter editor only — no instance-value migration, no `.cws` work (step 7).

## .cws lifecycle (Phase 6g Step 7 — done)

**Atomic writes everywhere:** All `.cws` writes use `WorkspacePersistence.SaveToFileAtomic` (temp-rename).
Callers: `NewWorkspace`, `WriteWorkspaceFile`, `SavePlanExecutor.ExecuteFileOps`, `SaveLooseToWorkspace`.
`SaveToFile` (non-atomic) remains only in test helpers.

**Corruption-tolerant open:** `TryLoadCws(string cwsPath)` — loads `.cws`, logs `Messages.Warning` on any
exception, returns `new CwsFile()`. Both `OpenWorkspace` and `OpenRecentWorkspace` call it. Tree content
is always populated by `WorkspaceScanner` (filesystem is truth, scanner's own `TryLoadCws` handles corruption).
"No `.cws` → not a workspace" gate (file-exists check) retained; a corrupt-but-present `.cws` degrades to
defaults, never rejects the open.

**Real `WriteWorkspaceFile`:** Loads existing `.cws` to preserve `KnownFiles` + `LibraryRefs` (authoritative
on disk), updates `ColorSchemeName` + `TreeViewState`, writes atomically. `DockLayout` stays null in v1
(`Dock.Serializer` not referenced). `silent` param suppresses success message on debounce/exit flush.

**Debounced autosave:** `ScheduleCwsSave()` resets a 3-second `DispatcherTimer`; on tick, writes silently.
`SubscribeToFilterState()` hooks `ProjectTreeFilterState.PropertyChanged → ScheduleCwsSave()`, tracking the
current tool instance across `CreateDefaultLayout()` replacements (called in constructor, `NewWorkspace`,
`ResetLayout`, `ExecuteSavePlan`). Pan/zoom never touches a `.cws` field — no write there.
`OnCleanExit` stops timers, flushes `.cws` synchronously, then clears recovery cache.

**Tree view-state in `.cws` (`CwsTreeViewState`):** 7 bool filter flags; `Ordering` string (v1 = null,
reserved for future ordering UI). Written by `WriteWorkspaceFile`; restored on open via `ApplyTreeViewState`
(unsubscribes debounce handler during restore to avoid spurious write). `TreeViewState` is
`JsonIgnore(WhenWritingNull)` — null written as nothing; missing on read → `null` → defaults applied.

**`MemberFiles` removed:** Deleted from `CwsFile`. Old `.cws` files with `member_files` still load (System.Text.Json
ignores unknown fields by default with `PropertyNameCaseInsensitive = true`).

## Project Tree VIEW (Phase 6g Step 3 — done)

Three new files in `src/Ui/ViewModels/ProjectTree/`:
- **`ProjectTreeFilterState`** (`ProjectTreeFilterState.cs`) — `ObservableObject` with 7 independently
  togglable bool properties (`Cells`, `Libraries`, `TestBenches`, `DataDisplays`, `ColorThemes`,
  `KnownFiles`, `WorkspaceFileSystem`); `IsAllOn` and `SetAll(bool)` helpers.
- **`ProjectTreeNodeViewModel`** (`ProjectTreeItemViewModel.cs` — old stub replaced) — wraps a
  `ProjectTreeNode`; exposes `IconKind` (`MaterialIconKind` switch on `Kind` + `IsTestBench`),
  `IsWarning`, `IsBold`, `IsItalic`, `IsExpanded` (two-way, for refresh-state preservation),
  `Children` (all), `FilteredChildren` (category-filtered, reactive).  Bottom-up `ApplyFilter()`
  preserves ancestors when a descendant's category is on.  `MissingNamedPrimary` → `IsWarning = true`.
- **`ProjectTreeTool`** (rewritten, deletes 6b stub) — `FilterState`, `RootItems`, `HasWorkspace`,
  `SetWorkspace(dir)` / `ClearWorkspace()`, `[RelayCommand] Refresh()` (re-scans + preserves expand
  state), `RebuildVmTree(expandedPaths)`.

Views:
- **`ProjectTreeView.axaml`** (rewritten) — toolbar (Refresh button + Filter flyout with 7 checkboxes),
  no-workspace placeholder, `TreeView` with `TreeDataTemplate ItemsSource="{Binding FilteredChildren}"`;
  styles: `TreeViewItem` `IsExpanded TwoWay`, `.pt-bold` / `.pt-italic` / `.pt-warning`
  (`.pt-warning` uses `{DynamicResource CrfWarningBrush}`); per-kind Material icon + name `TextBlock`
  with conditional classes; tooltip = WarningReason (if warning) + RelativePath.
- **`ProjectTreeView.axaml.cs`** (rewritten) — on-focus refresh via `Window.Activated`, debounced with
  `_refreshPending` bool + `Dispatcher.UIThread.Post` at `Background` priority.

`WorkspaceViewModel` updated: `SetWorkspace(dir)` / `ClearWorkspace()` wired to `CurrentWorkspacePath`
change; `OpenTreeItem` and its coupling to the 6b stub removed.

`CrfWarningBrush` (`SolidColorBrush`) declared in `App.axaml`; updated from `ThemeService.Active`
`ColorRole.SystemWarning` in `App.axaml.cs` `UpdateCrfWarningBrush()`, subscribed to `ThemeChanged`.

10 new VM tests in `ProjectTreeNodeViewModelTests.cs`; 372 total green.

## Workspace scan + project tree model (Phase 6g Step 2 — done)

Three framework-free files in `src/Ui/Schematic/` (no Avalonia / Skia):
- **`ProjectTreeNode`** / **`NodeKind`** (`ProjectTreeNode.cs`) — the in-memory tree node.
  `NodeKind` covers all §3.3 filter categories: `Workspace`, `Cell`, `Library`, `LibrariesGroup`,
  `CellViewFolder`, `ViewFile`, `UserFolder`, `DataDisplayFile`, `ColorThemeFile`, `OtherFile`,
  `KnownFile`, `KnownFilesGroup`.  Per-node flags: `IsPrimary`, `IsTestBench`, `WarningReason`
  (non-null = System.Warning tooltip text).  Children are mutable only via `internal AddChild`
  (called only by the scanner).  The tree is transient — rebuilt on every refresh.
- **`WorkspaceScanner`** (`WorkspaceScanner.cs`) — `static Scan(rootDir)` walks the workspace folder
  into that model.  Filesystem is truth (no membership list consulted).  Delegates primacy entirely
  to `CellFolder.ResolvePrimary` (never re-derived).  Empty view sub-folders produce no node.
  Stable ordering: alphabetical (OrdinalIgnoreCase) within every level.  Tolerates missing/corrupt
  `.cws`.  Warning states recorded as DATA (MissingNamedPrimary, broken library paths, broken Known
  Files) so step 3 renders System.Warning + tooltip without the scanner caring about rendering.
- **`WorkspaceModel`** (`WorkspaceModel.cs`) — thin wrapper: `WorkspaceRootDir`, `RootNode`,
  `Rescan()`.  The step-3 view binds to this and calls `Rescan()` on focus/Refresh.
  No `FileSystemWatcher` (deferred per §9).

`CwsFile.KnownFiles` (`List<string>`) added to `WorkspacePersistence.cs` — additive v1 field;
old `.cws` files load with an empty list.  `LibraryRefs` now accepts folder paths or `.clib` file
paths (scanner takes the parent dir for `.clib`).  `MemberFiles` is retained for round-trip but
is ignored by the scanner (membership is the filesystem, not a manifest).

41 new tests in `WorkspaceScannerTests.cs` (L1 node model, L2 scanner, L3 refresh); 362 total green.

## Workspace cell-level building blocks (Phase 6g Step 1 — done)

Three framework-free files in `src/Ui/Schematic/` (no Avalonia / Skia):
- **`NameValidator`** — cross-platform-safe name validation (§1.4 charset; `IsValid`/`Validate`).
- **`CellPersistence`** / **`CcellFile`** / **`CcellParameter`** — `.ccell` read/write mirroring `SymbolPersistence`
  conventions (System.Text.Json, enum-as-string, format_version reject, `Id` never persisted).
  `CcellParameter` mirrors `EditableParameter`'s shape but holds the **default** expression (the cell's
  declared interface, not an instance override).
- **`CellFolder`** — `CreateCellFolder(parentDir, cellName)` creates `cellName/schematic/symbol/layout/` + initial
  `.ccell`.  **`ResolvePrimary(cellFolder, viewType)`** is the single source of primacy truth implementing
  the five-branch rule (workspace-and-project-tree.md §2): SoleFile → NamedPresent → MissingNamedPrimary →
  NoPrimary → NoView.  The **MissingNamedPrimary** contradiction is kept distinct (drives System.Warning).
  The filesystem is truth; no membership list is maintained here.

## Symbol Editor (Phase 6f steps 4a + 4b — editing shell + drawing tools)

The Symbol Editor is a **smaller sibling of the schematic stack**. Mirror its patterns; do not reinvent.

### Stack structure
- `EditableSymbol` (`src/Ui/Schematic/EditableSymbol.cs`) — mutable working copy of a `Symbol`. Commands
  hold a reference and call `NotifyChanged()` after mutation.  Framework-free.
- `SymbolGeometry` (`src/Ui/Schematic/SymbolGeometry.cs`) — `BboxOf(prim)`, `HitTest(prim, x, y, tol)`,
  `TranslateBy(prim, dx, dy)`, `ComputeBb(list)`. Framework-free.
- `SymbolEditorViewModel` (`src/Ui/ViewModels/SymbolEditorViewModel.cs`) — all 15 tools (Select + 14
  drawing tools), selection, move-drag, rubber-band, gesture state, current style (`ColorRole`,
  `StrokeTier`, `FontSize`, `FontStyle`), `Execute(IUiCommand)` on the VM's **own** `UndoRedoStack`.
  `SetActiveToolCommand` and `SetCurrentStrokeTierCommand` are `RelayCommand<string>` (parse enum from
  CommandParameter) so every toolbar button needs zero code-behind.
  `FontStyleOptions` is a public static array exposed as ComboBox `ItemsSource`.
- `Commands/Symbol/PlaceSymbolPrimitiveCommand` — appends primitive (topmost Z); Undo removes it. Both
  directions call `NotifyChanged()`. Mirrors Move/Delete commands.
- `Commands/Symbol/MoveSymbolPrimitivesCommand` + `DeleteSymbolPrimitivesCommand` — both directions call
  `EditableSymbol.NotifyChanged()`.
- `SymbolEditorCanvas` (`src/Ui/Controls/SymbolEditorCanvas.cs`) — Skia control with pan/zoom,
  delegates pointer/keyboard/TextInput to VM, cursor = Cross for drawing tools / Default for Select.
- `SymbolEditorRenderer` (`src/Ui/Renderers/SymbolEditorRenderer.cs`) — draws fine-grid (`p=5`),
  calls `SchematicRenderer.DrawSymbol` (reused, not duplicated), selection bboxes, rubber-band;
  renders `InProgressPrimitive` from overlay as dashed ghost via `DrawSymbol(overridePaint: ghostPaint)`.
- `SymbolEditorOverlay` — carries `InProgressPrimitive` (nullable), selection, rubber-band, drag offset.
- `SchematicRenderer.DrawSymbol` — now renders `TextPrimitive` for real (IBM Plex Sans, align, style).
- `EnumEqualsToBoolConverter` (`src/Ui/Converters/`) — `(enum, string param) → bool`; drives
  `Classes.ToolActive` binding on toolbar buttons.
- `SymbolEditorDocument` / `SymbolEditorView` / `SymbolEditorWindow` — dockable document + tear-off window.
  Both host the same `SymbolEditorView`; only the chrome differs.

### Drawing gestures
- **Two-point drag** (click + drag to release): Line, Rect, RoundedRect, Circle, Ellipse, Arc, Sine, HalfWave.
- **Multi-point click** (click per point; Enter or double-click to commit):
  Polyline (≥2 pts), Polygon (≥3 pts, auto-closes), Triangle (exactly 3, auto-commits),
  QuadCurve (exactly 3, auto-commits), CubicCurve (exactly 4, auto-commits).
- **Text**: click anchor → type → Enter commits; Backspace erases; Escape cancels; live cursor shows `|`.
  Uses Avalonia `TextInput` event (IME-safe), not raw `KeyDown`.
- **Escape** during any gesture: cancel (nothing placed). **Escape** with nothing in progress: clear selection.
- All snapped to `p = 5` local units via `SnapToP`.

### Pins (4c — done)
- **Two separate snap grids:** art snaps to `p = 5` (`SnapToP` in the VM); pins snap to `P = 100`
  (`SnapToConnectionGrid`, `PinGrid = 100.0`). Never use `SnapToP` for a pin; never use `SnapToConnectionGrid`
  for art.
- **Pin tool owns pin interaction.** The Select tool only touches primitives. Pin select/move/remap all
  live in `PinToolPress` / `PinToolMove` / `PinToolRelease` paths.
- **Unmapped port = open circuit, never an error.** Surface informally via `DrawUnmappedPortPanel` (soft-
  yellow overlay); do not block editing or flag as an error.
- **PortCount** is the number of ports the symbol maps pins to. Persisted in `.csym`. A `.csym` with
  `PortCount = 0` infers `PortCount = pins.Count` for backward compatibility (old files).
- **Locked gate:** `EditableSymbol.UserEditable = false` → `SymbolEditorViewModel.IsLocked = true` →
  all `Execute()` calls are no-ops; Pin / drawing tools disabled in toolbar; cross cursor reverts to arrow;
  "Read-only" shown in metadata bar. Built-in symbols opened via View menu are always locked.

### .csym I/O (4c — done)
- **Save/Save-As:** `SaveSymbolCommand`/`SaveSymbolAsCommand` on the VM; delegate to
  `SymbolPersistence.SaveToFile`. Both are `IAsyncRelayCommand<Window?>`.
- **Open:** File → "Open Symbol…" in the Workspace window → `WorkspaceViewModel.OpenSymbolFileCommand` →
  `SymbolPersistence.LoadFromFile` → `EditableSymbol.FromSymbol(symbol)` with `UserEditable = true`.
- **Built-in symbols opened from the View menu** are loaded with `UserEditable = false`.
- `.csym` format: JSON with `format_version`; reject-on-mismatch; alpha policy (no migration in v1).

### Deferred (do NOT build without discussion)
- Live schematic update — requires cell model / project-tree / workspace design (later phase).
- Cell-driven open — same dependency.
- Rewiring `SymbolKind → BuiltInSymbols` — same dependency.
- If a task seems to need the cell model, STOP and report (it's the project-tree design).

### Key invariants
- **`DrawSymbol` is shared** — `SchematicRenderer.DrawSymbol` is `internal static`; the editor calls it
  directly. Do NOT write a second symbol renderer.
- **All mutations undoable** on the document's own `UndoRedoStack`; `NotifyChanged()` in both Execute and Undo.
- **Art snaps to `p = 5` local units** (`SnapToP` in the VM). Pins snap to `P = 100` (`SnapToConnectionGrid`).
- **Color is a role** (`SymbolColorRole`), never literal RGB. No color picker in the editor.

### Opening the editor
`WorkspaceViewModel.OpenSymbolEditorDockedCommand` (View menu) opens on Resistor (docked).
`WorkspaceViewModel.OpenSymbolEditorWindowCommand` opens on Inductor (tear-off window).

---

## Symbol orientation (Phase 6f step 3 — standard library art)

**2-terminal symbols are VERTICAL** (R, L, C, V, Tone, Port/Term, GND): local pin 1 at `(0,-200)` (top),
pin 2 at `(0,+200)` (bottom). FET, ZPort, Sdd, Generic stay **horizontal** (ports left/right).

Schematic layout code consequence: place 2-terminal passives at `SymbolRotation.R90` in a horizontal signal
path (pin 1 right, pin 2 left at R90) — same `cx ± 200` wire coordinates as before. Bias-path vertical
components (at R0) need no rotation. See `docs/design/standard-library-symbols.md` for geometry.

---

## Color theming (Phase 6 — three-layer separation)

`src/Ui/Theming/` holds the **framework-free L1 theme model** (no SKColor, no Avalonia):
- `ColorRole` — string role constants (`Schematic.Background`, `System.Warning`, …); add a constant here to introduce a new role.
- `Rgba` — plain `record struct`, serializable via System.Text.Json.
- `ColorVariant` — `{ Light, Dark }` enum; independent of Avalonia's `ThemeVariant`.
- `ColorTheme` — role → RGBA maps for both variants; `Resolve(role, variant)` falls back to `BuiltIn` for absent roles. `ColorTheme.BuiltIn` is the single source of truth for default colors.
- `ColorThemeIo` — `.ccolor` read/write (System.Text.Json, `format_version` reject-on-mismatch, no Id persisted, keys sorted for stable diffs).

**Firewall note:** `src/Ui/Theming/` carries no Avalonia or SkiaSharp types and could migrate to `src/Core` without changes if another assembly ever needs it. Keep it that way.

**L2 projection** (`SchematicRenderTheme.FromTheme`): translates L1 roles to SKColor tokens for the renderer. L3 (not yet built): active-theme preference, workspace tracking, resolution order, Settings UI.

**Rule-of-role:** adding a new themable color = new `ColorRole` constant (L1) + read it in the relevant `*RenderTheme` token struct (L2). L1 and L3 never change for new colors.

## Phase 6d schematic editor — key rules (from 6d-fix)

### Per-segment wire drag convention (Phase 6d wire editing)
Wire segments are **independently selectable and draggable**. A direct click on a wire segment (not near an endpoint) returns `HitKind.WireSegment` with `SubIndex = segmentIndex`, and highlights only that segment. The drag convention is **perpendicular-only**:
- A **horizontal** segment can be dragged **vertically** only (dx zeroed out).
- A **vertical** segment can be dragged **horizontally** only (dy zeroed out).
- Dragging along the segment's own axis is not offered (delta constrained in `HandleSegmentDragLive`, not as a fixup after).

This preserves orthogonality automatically — moving a segment perpendicular to itself only lengthens/shortens its adjacent neighbors.

**Rubber-band: a segment move NEVER breaks a connection.** The dragged segment always translates by the perpendicular delta; whatever is connected is held in place by adding jog segments (`OrthogonalRoute`) rather than detaching:
- An **outer endpoint connected to anything** is held connected — but *how* depends on the drag axis (`ShouldPinDraggedEndpoint`):
  - a **port** or a coincident **wire vertex/corner** (a fixed point) is *pinned*: held in place with a jog bridging it to the moved segment;
  - an endpoint on a wire **body** is pinned only if that body is **perpendicular** to the drag (it would move off). If the body is **parallel** to the drag, the endpoint **slides along it** (no jog) — e.g. a horizontal wire joining two vertical wires slides down them as one straight segment, staying exactly 2 junction dots (a jog there would run along the verticals and spawn bogus extra dots).
- **Sliding is clamped** (`ComputeSlideClamp`): the perpendicular delta is bounded so a sliding endpoint can never run off the end of the wire it rides — the connection is never lost. Ranges from all sliding endpoints (and every wire each touches) are intersected, so the drag **stops at the shorter wire's end** when two parallel wires differ in length.

### Collinear overlap simplification
Two **collinear** wires (same line) whose spans overlap or abut are redundant and merge into their **union** (`WireGeometry.TryMergeCollinearOverlap`, tried in `TryBuildMergeCommand` **before** the endpoint-coincidence merge — the endpoint merge builds a back-tracking path that `NormalizePoints` would collapse, dropping part of the span when two collinear wires share an endpoint; guarded by `OverlapMergeBuriesT` so a merge can't bury a T-junction with a third wire). A connector wire dragged until both its ends coincide collapses to a zero-length wire; `DotRevalidationCommand` (the central post-edit cleanup wrapping every `Execute`) removes any wire that normalizes to &lt; 2 distinct points — undoably — so no stray zero-length wire (and its bogus junction dot) is left behind. A junction **dot** is only drawn where incident segments form a real *branch* — the auto-dot rule requires incident segments spanning **both axes** (a horizontal AND a vertical one), so 3+ *collinear* segments meeting (overlapping wires) draws no dot. Connectivity (`autoDotKeys`, endpoint-connected state) still counts any incident ≥ 3, so a collinear-overlap endpoint reads connected (no false red dot) even though no junction dot shows.
- When **both** outer ends are pinned (a single connected segment), jogs are added at **both** ends so the wire **bows out** — it is no longer frozen.
- Wires connected **on** the dragged segment follow it by the same delta so their junction stays attached: a **stem** T-ed onto its interior, a wire joined at one of its **moving vertices**, and a **user crossing-dot** on its interior (the dot slides along the stationary crossed wire). All folded into the one undoable `MoveCommand` (incl. `DotMoveSnapshot`), so a single Undo restores the wire, its followers, and the dots together. If a drag carries the wire entirely off a crossing, the now-invalid dot is removed by re-validation at commit (per the §5.1 dot invariant).

Each segment drag commits as a single `MoveCommand` with a `WireMoveSnapshot` (old points → new points), undoable. Live preview uses `SchematicOverlay.WireDragPoints` — no `BuildRenderModel()` per tick.

**Selection model**: `_selectedSegment: (string WireId, int SegmentIndex)?` in `SchematicViewModel` tracks the segment selection separately from `SchematicSelection` (which holds whole-object IDs). `SchematicOverlay.SelectedWireSegment` carries it to the renderer. Rubber-band selection still selects whole wires (`HitKind.Wire` from `TestRect`), not individual segments.

### Wire draw-mode cursor and finish gestures (Phase 6d)
- Wire tool cursor: `StandardCursorType.Cross` (reverts to Default when tool changes away from Wire).
- **Enter** or **double-click** finishes the in-progress wire (keeps what was drawn) and returns to Select tool.
- **Esc** discards the in-progress wire (via `CancelCurrentOp`) and returns to Select.
- `< 2` distinct points: discarded, nothing placed.

### Incremental render rule (Item 1 / perf)
During an **active drag or nudge**, do NOT call `BuildRenderModel()` per tick. Update
`SchematicOverlay.ComponentDragPositions` / `WireDragPoints` only (O(k) for k moved objects).
`BuildRenderModel()` is deferred to drag-END. The connectivity pass inside `BuildRenderModel()`
is O(N) via spatial hash — never revert to the O(N²) linear scan.

### Display name registry (Item 8)
`ComponentTypeRegistry` in `src/Ui/Schematic/ComponentTypeRegistry.cs` maps `SymbolKind` →
`(DisplayName, InstancePrefix)`. **Always** read `ComponentTypeRegistry.DisplayName(kind)` for
the on-schematic type label — **never** call `kind.ToString()` or hard-code abbreviations in the
renderer. When the component model gains a richer type system, re-key the registry off that type.

### Id not persisted (Item 2)
`Id` on `EditableComponent`, `EditableWire`, `EditableNetLabel`, `EditableDot`, `EditableCanvasObject`
is **runtime identity only** — it must NOT appear in any persisted file (`.csch`, `.cws`, `.csym`,
`.cdd`). Fresh Ids are auto-generated on import. Tests compare content, never Ids.

### Move-Labels op (Item 14)
**F5** or the right-click "Move Labels" context menu entry enters Move-Labels mode.
- Phase `Picking` (nothing selected): next click picks which component to move.
- Phase `WaitFirstClick` (components already selected): next click sets the drag reference point.
- Phase `Moving`: mouse moves show live preview via `SchematicOverlay.LabelDragOffsets`; second click commits as a `MoveLabelsCommand`. **Esc always returns to Select.**
- Label offsets persist in `.csch` as `LabelOffsets: [[dx,dy],…]` on each component (omitted when all zero).
- `SchematicOverlay.LabelDragOffsets` carries `{compId → (DX,DY)}` during the drag. The renderer applies the delta on top of any existing per-label offset stored in `SchematicComponent.LabelOffsets`.

### Clipboard (Item 15)
`SchematicClipboard.CopyAsync()` places four formats on the clipboard simultaneously via Avalonia's `DataTransfer` API (richest first):
- `PdfNativeMacFormat` (`com.adobe.pdf` UTI, macOS) / `PdfNativeWinFormat` (`application/pdf`, Windows) — PDF bytes via `SKDocument.CreatePdf()`; recognised by Keynote, Preview, Pages, etc.
- `SvgNativeFormat` (`public.svg-image` UTI) — SVG bytes for macOS/Linux vector apps (Illustrator, Inkscape). Omitted on Windows (no well-known SVG clipboard format; EMF would be the Windows vector path — needs System.Drawing.Imaging + Svg.NET, follow splotRF's `WindowsClipboard.cs`).
- `DataFormat.Bitmap` — Avalonia `Bitmap` (PNG-backed raster; universal fallback for Keynote, Pages, Word, etc.).
- `DataFormat.Text` — JSON text (primary for Paste; cross-session portable). Always present even if rich formats fail.
- **Paste** reads `DataFormat.Text` (JSON) and wraps in `SchematicPasteCommand` (undoable).
- **Ctrl/Cmd+C / +X / +V** in the canvas raise events on `SchematicCanvas` (`ClipboardCopyRequested` / `ClipboardCutRequested` / `ClipboardPasteRequested`) handled async in `SchematicView.axaml.cs`.
- The JSON payload carries **`GridSize`** (= `P_src`). On paste, `SchematicPasteCommand` compares it to the destination `GridSize`; cross-grid content is snapped to `P_dst` and a warning is posted (see Grid & Connectivity below).

### Grid & connectivity (Phase 6 — standing rules)

**Authority:** `docs/design/grid-and-connectivity.md`. This section is a quick-reference; the design doc is the source of truth.

**Two grids, two jobs — never conflated:**
- **Connection grid `P`** (`GridSize`, default 100): every pin-in-world, wire endpoint, wire bend, junction dot lands on it **exactly** (integer multiple — equality not tolerance). Connection = coordinate equality, not proximity.
- **Authoring grid `p = P/k`** (`AuthorGridSize`, default `k=20` → `p=5`): label offsets, net-label positions, canvas objects. Use `SnapToAuthorGrid` for these. **Never** use `SnapToAuthorGrid` for electrical connection points.

**On-grid invariant (R7):** after any edit, every pin world-coordinate, wire vertex, and junction dot is an exact `P` multiple. `OnGridInvariantTests` guards this. Do not introduce any edit path that bypasses `SnapToGrid`.

**`SnapToGrid` vs `SnapToAuthorGrid`:**
- `EditModel.SnapToGrid(v)` → snaps to `P` — use for all electrical points (component origin placement, wire endpoints, wire bends, segment drag).
- `EditModel.SnapToAuthorGrid(v)` → snaps to `p` — use for label drag (`ComputeLabelDelta`), canvas-object drag. **Never** use this for wires or component origins.

**`ConnectTolerance = 0.5`** (float-dust guard only): connectivity is established at *input* by snapping to `P`, not by tolerance afterward. Do not raise `ConnectTolerance` or use it to bridge real gaps.

**Net labels are NOT on any grid:** a net label's position carries no electrical meaning. `EditableNetLabel.X/Y` may be any value. Do not snap net-label positions to `P` or assert them in R7.

**Cross-grid paste (§5):** `SchematicPasteCommand` accepts `sourceGridSize` and `messageSink`. When `P_src ≠ P_dst`, it snaps component origins and wire vertices to `P_dst` (using `Math.Round(v/P)*P`), canvas objects to `p_dst`, posts a `Warning` to `IMessageSink`, and validates R7 post-snap — all in the constructor so Execute/Undo/Redo are clean. `CopyAsync` embeds `model.GridSize` in the JSON; `PasteAsync` returns it; `SchematicView.axaml.cs` threads both through to the command.

## Phase 6b shell conventions (locked in — do not deviate without discussion)

### Dock library
- **Dock.Avalonia 12.0.0.2** + **Dock.Model.Mvvm 12.0.0.2** + **Dock.Avalonia.Themes.Fluent 12.0.0.2** —
  all three are required. Theme is loaded via `StyleInclude Source="avares://Dock.Avalonia.Themes.Fluent/DockFluentTheme.axaml"`.

#### Dock color system — how it works and how to override it
The theme's accent colors are defined in `avares://Dock.Avalonia.Themes.Fluent/Accents/Fluent.axaml`
(source: `src/Dock.Avalonia.Themes.Fluent/Accents/Fluent.axaml` in the wieslawsoltes/Dock repo).
Two tiers of resources exist:

**Tier 1 — primary accent family (hardcoded VS blue):**
```
DockApplicationAccentBrushLow      #007ACC  ← active tab background, splitter drag
DockApplicationAccentBrushMed      #1C97EA  ← hover tab background, splitter hover
DockApplicationAccentBrushHigh     #52B0EF  ← close-button hover
DockApplicationAccentForegroundBrush #F0F0F0 ← text on active (accent) tabs
DockApplicationAccentBrushIndicator #007ACC ← dock target indicators
```

**Tier 2 — StaticResource aliases** (resolved at theme load time, pointing into Tier 1):
```
DockTabActiveBackgroundBrush  → DockApplicationAccentBrushLow
DockTabActiveIndicatorBrush   → DockApplicationAccentBrushLow
DockTabHoverBackgroundBrush   → DockApplicationAccentBrushMed
DockTabCloseHoverBackgroundBrush → DockApplicationAccentBrushHigh
DockSplitterHoverBrush        → DockApplicationAccentBrushMed
DockSplitterDragBrush         → DockApplicationAccentBrushLow
DockSurfaceHeaderActiveBrush  → DockThemeAccentBrush → {DynamicResource SystemAccentColor} ✓
```

**Key rule:** `Application.Resources` wins over `StyleInclude` resources for `{DynamicResource}` lookups.
Overriding a Tier-1 key in `Application.Resources` fixes places that reference it directly.
BUT StaticResource aliases (Tier 2) are resolved at load time — their VALUE is baked in as the
original brush object. To fix those, you must ALSO override the Tier-2 alias keys directly in
`Application.Resources`. Both tiers are overridden in `App.axaml`'s `Application.Resources` block.

**Note:** `DockSurfaceHeaderActiveBrush` (tool panel active title bar) already chains to
`SystemAccentColor` via `DockThemeAccentBrush`, so it was correct without any override.

**What controls which UI element:**
- Active document tab strip item background: `DockTabActiveBackgroundBrush`
- Separator line between tab strip and content: `DockTabActiveIndicatorBrush`
- Tool panel active title bar (Project/Properties/Messages): `DockSurfaceHeaderActiveBrush`
- Splitter bar on hover/drag: `DockSplitterHoverBrush` / `DockSplitterDragBrush`
- **`CircuitRfDockFactory`** extends `Factory`; owns the layout tree. It exposes `MessagesTool?`,
  `ProjectTreeTool?`, `DocumentDock?`. Use `factory.OpenDocument(stub)` to add tabs in 6c+.
- Dock Tool/Document subclasses live in `src/Ui/ViewModels/Dock/`. Their views are wired via
  **`DataTemplate`** in `App.axaml` (NOT ViewLocator — Dock resolves its own templates from the
  `Application.DataTemplates` list).
- `SetFocusedDockable(IDock, IDockable)` requires the parent `IDock` container, not the tool itself.
  When programmatically activating a tool, use `SetActiveDockable(tool)` only unless you hold a
  reference to the container.
- **GOTCHA — document bodies must use the CACHED (non-deferred) content template.** Dock's default
  `DocumentControl` template (`DockDocumentControlSingleContentTemplate`, Fluent theme) wraps each
  document body in a **`DeferredContentControl`** that realizes content on a *background-priority
  dispatcher timeline*. The DataTemplate resolves correctly and the right view IS built — but its
  first **paint is deferred** and (in our app) does not flush until the next layout pass, so a
  newly-activated document stays unpainted (the previous tab's stale view lingers) until something
  forces a relayout (e.g. toggling a panel). The `IDeferredContentPresentation { DeferContentPresentation
  => false }` opt-out did NOT help (its readiness gate fails at the instant content swaps). **Fix
  (in `App.axaml`):** override `DocumentControl.Template` to Dock's other built-in template,
  `DockDocumentControlCachedContentTemplate`, which hosts each dockable in a plain `ContentControl`
  (no `DeferredContentControl`, no timeline) and paints on the normal layout pass:
  `<Style Selector="dockCtrl|DocumentControl"><Setter Property="Template"
  Value="{DynamicResource DockDocumentControlCachedContentTemplate}"/></Style>`. Trade-off: all open
  document bodies stay realized (cached) instead of lazily built — negligible at our tab counts, and
  tab switching becomes instant. Document views still resolve via the `App.axaml` DataTemplates.

### Command / undo-redo infrastructure — per-document stacks, focused-window routing

**The rule (do not violate):** Undo/Redo is per-document, resolved by the focused window.
- Each editable document VM (`SchematicViewModel`, `SymbolEditorViewModel`) owns its **own**
  `UndoRedoStack` (created internally, exposed as `public UndoRedoStack UndoRedo`).
- Each document wrapper (`SchematicDocument`, `SymbolEditorDocument`) implements `IUndoableDocument`
  (in `src/Ui/Commands/IUndoableDocument.cs`) and forwards `UndoRedo` to its VM.
- **Focused-window rule:** every window's Undo targets the document it is showing, never another.
  - **Main `WorkspaceWindow`:** `WorkspaceViewModel` tracks `_factory.DocumentDock.ActiveDockable`
    via `OnDocumentDockPropertyChanged`; its `Undo`/`Redo` commands route to that document's stack.
    Switching tabs re-subscribes `PropertyChanged` on the new stack so enable-state updates correctly.
  - **Tear-off windows** (`SymbolEditorWindow`): `Window.KeyBindings` bind to the document's own
    `ViewModel.UndoCommand`/`RedoCommand` directly — fully independent of `WorkspaceViewModel`.
  - **Dock-floated dockables** (user drags a tab into its own floating window): `WorkspaceViewModel.
    TryWireHostWindowsUndo` detects the new `HostWindow` via `ApplicationLifetime.Windows` scan
    (deferred one frame) and injects matching `KeyBindings` pointing at the floated document's stack.
    The host window's `Closed` event cleans up the subscription and removes it from `_wiredHostWindows`.
- **One keystroke owner:** do NOT handle undo/redo keys both at the canvas level and at the window
  level — choose one. The window `KeyBindings` are the authoritative path; canvas `OnKeyDown`
  handlers must **not** call `_undoRedo.Undo()` directly (and must set `e.Handled = true` for any
  keys they DO consume, so they don't bubble to the window binding and fire a second time).
- **Cross-document undo is impossible:** undoing in a symbol can never revert a schematic edit.
- **The Parameter Editor has NO independent stack.** It commits through `_schematicVm.Execute(...)`,
  so parameter edits live in the owning schematic's history and are undoable from that schematic.
  `ParameterEditorViewModel.UndoCommand`/`RedoCommand` delegate to `_schematicVm.UndoRedo` — they
  are only an affordance so the user can undo a parameter edit while the dialog is focused; they do
  not own a separate stack. Never give an inspector/properties panel its own undo stack.
- All user mutations still route through `IUiCommand` → the document's own `UndoRedoStack.Execute(cmd)`.
  Do not wire mutations that bypass the stack (except global file ops: New/Open/Save).
- Future document types (data display etc.) implement `IUndoableDocument` to participate automatically.
  Their windows follow the same focused-window rule: tear-off → own `KeyBindings`; Dock-floated →
  `TryWireHostWindowsUndo` picks them up automatically.

### Messages / IMessageSink
- **`IMessageSink`** lives in `src/Ui/Messages/` — not in Core/Engine (firewall respected).
  `MessagesTool` implements it. Obtain via `WorkspaceViewModel.Messages`.
- Always post from any thread: `MessagesTool.Post()` dispatches to the UI thread internally.
- Level semantics: Info = neutral status; Success = operation completed; Warning = degraded or
  unexpected but non-fatal; Error = operation failed, user must act.
- Icon + color both carry the level (never color alone — accessibility requirement from ui-design.md §2).

### Toolbar
- Avalonia 12 has **no built-in `ToolBar` control**. Use `Border` + `StackPanel Orientation="Horizontal"`
  with `Background="Transparent" BorderThickness="0"` buttons and `Border Width="1"` separators.

### Icons (Material.Icons.Avalonia 3.0.2)
- Always verify enum names against the `Material.Icons` 3.0.2 DLL before using them —
  many intuitively-named icons don't exist (e.g. `Chip`, `PanelLeftOpen`, `Undo`, `PlusBox`).
  Valid substitutes used in 6b: `IntegratedCircuitChip`, `PageLayoutSidebarLeft`, `UndoVariant`/`RedoVariant`, `FilePlus`.
- Context menus on `TreeView` items: place `<Grid.ContextMenu>` inside the `TreeDataTemplate` Grid
  so the DataContext is the item VM, not the tool VM.

### Fonts
Two font families are embedded in `Assets/Fonts/` and registered as `Application.Resources` in `App.axaml`:

| Resource key   | Family          | Files                                      | License                  |
|----------------|-----------------|--------------------------------------------|--------------------------|
| `DejaVuSans`   | DejaVu Sans     | `Assets/Fonts/DejaVuSans*.ttf`             | Bitstream Vera Fonts     |
| `IBMPlexSans`  | IBM Plex Sans   | `Assets/Fonts/IBM_Plex_Sans/static/*.ttf`  | SIL Open Font License 1.1 |

**IBM Plex Sans is static-only** (no variable fonts) because SkiaSharp does not support variable fonts.

**Avalonia controls** reference them via `{DynamicResource IBMPlexSans}` / `{DynamicResource DejaVuSans}`.

**SkiaSharp renderers** must use `SkiaFonts` (`src/Ui/Renderers/SkiaFonts.cs`) — lazy-loaded
`SKTypeface` instances sourced from the same embedded assets via `AssetLoader.Open()`. **Default
to `IBMPlexSans` for all renderer text** (labels, tick marks, annotations, tables). Fall back to
`DejaVuSans` only when broader Unicode coverage is needed (e.g. non-Latin axis labels).

```csharp
// Preferred — clean, modern, designed for screen
var tf = SkiaFonts.PlexRegular;    // Regular
var tf = SkiaFonts.PlexBold;       // Bold
var tf = SkiaFonts.PlexSemiBold;   // SemiBold
var tf = SkiaFonts.PlexItalic;     // Italic
var tf = SkiaFonts.PlexLight;      // Light

// Fallback — wide Unicode range
var tf = SkiaFonts.DejaVuRegular;
var tf = SkiaFonts.DejaVuBold;
```

Add additional weights by copying the `Lazy<SKTypeface>` pattern in `SkiaFonts.cs` — every static
file in `Assets/Fonts/IBM_Plex_Sans/static/` is embedded and loadable. Never call
`SKTypeface.Default` or `SKTypeface.FromFamilyName(...)` in renderers; that pulls from the host OS
and produces inconsistent cross-platform output.

**Firewall (load-bearing):** all UI-framework code lives here in `src/Ui`. `RfCore`/`Core`/`Engine`/`Cli`
reference **no Avalonia** — an enforced CI check fails the build otherwise (`ui-architecture.md` §3). Keep
Skia *rendering* separable from Avalonia *control hosting* so a re-skin keeps the renderers. The display
layer is circuitRF's own, `DataCube`-native (C1); splotRF is reference material, not a dependency.

## Framework & patterns
- **Avalonia 12**, single codebase for Windows/macOS/Linux. Mirror splotRF's structure, controls,
  and packaging recipes wherever possible — it's our proven sibling app (same stack, MIT).
- **MVVM via CommunityToolkit.MVVM.** Views are thin; logic lives in view models. No simulation
  logic in code-behind.
- **The GUI never simulates the design layer directly.** It always builds/edits the design layer,
  then asks the engine to elaborate and run. Results come back as a **`DataSet`** (named
  single-kind `DataCube`s).


### System Specific Differences Between Window, Linux, macOS
- macOS uses ⌘ symbol instead of Ctrl on Windows and Linux. Ensure this is respected in menus and tooltips text.
- The System file manager in macOS is called the "Finder", while Windows has an "Explorer". Respect this convention in menus and tooltip text when referring to the System File Manager.

## Schematic canvas — the performance-critical control (6c pattern, locked in)

### Render pipeline (do not change)
- **Control.Render → ICustomDrawOperation → ISkiaSharpApiLease → SchematicRenderer.Draw**
  - `SchematicCanvas : Control` — Avalonia host; owns pan/zoom state, pointer handling, DirectProperty bindings
  - `SchematicDrawOperation : ICustomDrawOperation` — captures a snapshot of viewport state; leases the Skia canvas
  - `SchematicRenderer` (static class) — pure Skia; no Avalonia types; re-skinnable
  - **NOT SKCanvasView** — that path is not composited correctly in Avalonia 11+
- See `splotRF/src/Controls/PlotControl.cs` for the proven pattern this is adapted from.

### World ↔ pixel transform
- `panX, panY` = world coords at the top-left corner of the canvas (in world units)
- `zoom` = pixels per world unit
- `px = (wx - panX) * zoom`; `wy = py / zoom + panY`
- Scroll-zoom: record world point under cursor *before* zoom change, adjust pan to keep it fixed after.
- Symbol local coords: 100 units = 1 grid square; standard component width = 300 units (body + leads)

### Performance
- **Viewport virtualization**: `SchematicSpatialIndex` (uniform grid, cell size 1500 world units, `Dictionary<(int,int),List<int>>`)
  — `QueryViewport(vpMinX,Y,vpMaxX,Y)` returns conservative candidate sets for components and wires.
  Never draw everything; the index prunes invisible items before the render loop.
- **LOD (level of detail)**: based on `compPixW = zoom × 300`
  - `< 6px`  → single filled rect (`LodRect` colour); skip all else
  - `< 22px` → body symbol lines only; skip port markers and text
  - `≥ 22px` → full: symbol + port markers (red box for unconnected) + labels
- **Grid**: adaptive — fine grid drawn when spacing ≥ 4px × 3; coarse-only (×10) when spacing ≥ 4px; skipped below 4px.
  Cap at 600 lines per axis.
- **FPS**: `static volatile long SchematicRenderer.LastFrameTicks` written by the renderer each frame.
  `SchematicView.axaml.cs` reads it every 333 ms via `DispatcherTimer` to update the toolbar readout.
  The renderer also draws an overlay in the top-right corner of the canvas (enabled by `ShowFps=True`).

### DirectProperty pattern
```csharp
public static readonly DirectProperty<SchematicCanvas, SchematicModel?> ModelProperty =
    AvaloniaProperty.RegisterDirect<SchematicCanvas, SchematicModel?>(
        nameof(Model), o => o.Model, (o, v) => o.Model = v);
```
Model setter rebuilds `SchematicSpatialIndex`, sets `_needsInitialFit = true`, calls `InvalidateVisual()`.

### Initial fit
`LayoutUpdated` event fires when `Bounds` is valid. A `_needsInitialFit` flag triggers `ZoomToFitInternal`
exactly once. The handler unsubscribes itself. Do NOT call `InvalidateVisual()` inside `Render()`.

### Adding a new canvas type (6d Data Display, etc.)
1. Create `MyRenderer` (static, no Avalonia — parallel to `SchematicRenderer`)
2. Create `MyCanvas : Control` with a `DirectProperty<MyCanvas, MyModel?>` and an `ICustomDrawOperation`
   nested class that leases Skia and calls `MyRenderer.Draw`
3. Create `MyView.axaml` with toolbar + `MyCanvas`; wire buttons in `MyView.axaml.cs`
4. Add `DataTemplate DataType="{x:Type ...}"` in `App.axaml`

- **Do NOT render components as individual styled controls.** A 10,000-component schematic will die
  that way. Render the canvas yourself via a custom control (`DrawingContext`, dropping to
  **SkiaSharp** for hot paths), with **viewport virtualization** and a **spatial index** (e.g.
  quadtree/grid) for hit-testing and pan/zoom.
- Lean on Avalonia 12's rendering work (deferred composition, dirty-rect tracking) but verify with
  a large stress schematic in `testdata/`, not a toy one.

## Wiring & auto-routing
- Obstacle-aware auto-routing must avoid drawing wires over placed symbols. Start with **orthogonal
  A\*** over a coarse grid using the spatial index for obstacles; refine later. Keep the router
  separable and testable headless.

## Editing model
- **Undo/redo via the command pattern** across all editors (schematic, symbol). Every mutation is a
  reversible command; nothing mutates model state directly.
- **Copy/paste via the system clipboard**, all or a selectable subset of a schematic, to/from other
  schematics.
- **Hierarchy navigation:** push into a sub-cell's schematic, edit, pop back. Editing a cell affects
  every instance; instance-level parameter overrides (root `CLAUDE.md` → expressions) stay per-instance.

## Data Display
The data-display layer is **circuitRF's own**, built fresh and **`DataCube`-native** (a trace is a slice of
a cube), living under `src/Ui` (see `docs/design/ui-design.md` / `ui-architecture.md`). It is **NOT** taken
from splotRF as a dependency and is not in RfCore. splotRF is **reference material only** — mine its proven
techniques (the three-coordinate-space transform pipeline, Smith/polar/rectangular rendering math, the
placeable-plot canvas, plot/trace/table rendering, MarkerInfoBox, autoscale-with-marker-preservation,
tick snapping, pan/zoom/hit-test) and **re-implement them cleanly against `DataCube`** — do not reimplement
from scratch ignoring splotRF's solved problems, and do not depend on splotRF's code. Keep it
UI-framework-light (clean Skia-render vs thin Avalonia-host split) so it stays re-skinnable and could be
lifted into a shared lib later if ever needed (not now). Support measured-vs-simulated overlay (a lab
Touchstone over a simulated result cube from the `DataSet`).

## "Easy" budgets (testable)
Honor the PRD §12 click budgets (e.g. placed FET → running HB power sweep in ≤ 8 actions). Advanced
settings must remain reachable but must not clutter the default path.

# circuitRF UI Coding Context

These are design-quality standards; the architectural rules above are non-negotiable structural constraints.

## Abstract

circuitRF UI development should prioritize accessibility, clarity, consistency, and professional presentation. Interfaces should follow modern Human Interface Guidelines where practical, using system fonts, semantic styling, adaptive layouts, intuitive interactions, and restrained motion. UI text and workflows should target RF engineers, researchers, technical managers, and advanced hobbyists. The overall goal is to create a polished, trustworthy, efficient engineering application suitable for advanced technical workflows.

---

# 1. Core Design Principles

* Prioritize usability, clarity, readability, and consistency.
* Follow Apple Human Interface Guidelines where practical, but treat them as preferred guidance rather than strict rules.
* Favor clean, minimal, engineering-oriented interfaces.
* Avoid visual clutter and unnecessary animation.
* Design for desktop workflows and technically sophisticated users.

---

# 2. Accessibility

## Text and Typography

* Default font size: 12–13 pt.
* Minimum font size: 9–10 pt.
* Support scaling text up to at least 200%.
* Avoid ultra-light font weights.
* Prefer:

  * Regular
  * Medium
  * Semibold
  * Bold

## Contrast

Use WCAG AA contrast guidance:

* Normal text under 18 pt: minimum 4.5:1 contrast.
* Large text (18+ pt): minimum 3:1 contrast.
* Bold text: minimum 3:1 contrast.

## Visual Communication

* Never rely on color alone to convey meaning.
* Use icons, shapes, labels, or patterns alongside color.
* Support color-blind accessibility.

## Layout Accessibility

* Ensure layouts adapt cleanly to large font sizes.
* Reduce truncation wherever possible.
* Prefer stacked layouts when horizontal space becomes constrained.

---

# 3. Colors

* Use system colors whenever possible.
* Avoid hardcoded colors.
* Support light and dark modes automatically.
* Use colors consistently across the application.
* Do not reuse the same color for conflicting meanings.

---

# 4. Icons and Symbols

## UI Icons

* Use Material.Icons.Avalonia for normal UI icons.
* Maintain consistency in:

  * stroke weight
  * scale
  * detail level
  * perspective

## Cell Symbols

Cell symbols are NOT UI icons.

They serve two purposes:

1. Visual identification of RF/electrical components.
2. Port connection geometry for schematic editing.

Do NOT use Material.Icons.Avalonia for schematic cell symbols.

---

# 5. Layout and Visual Hierarchy

## General Layout

* Group related content visually.
* Use spacing and alignment consistently.
* Prioritize important information near the top-left (LTR layouts).
* Avoid overcrowding controls.
* Ensure controls remain visually distinct from content.

## Content Flow

* Use progressive disclosure for advanced or hidden information.
* Avoid overwhelming the user with excessive detail at once.

## Window and Screen Usage

* Extend backgrounds fully to edges.
* Scrollable regions should fill available space.
* Respect safe areas and window chrome.
* Avoid placing critical controls at the bottom edge of windows.

---

# 6. Motion and Animation

* Use animation only when it improves clarity.
* Keep animations subtle, brief, and purposeful.
* Avoid decorative or excessive motion.
* Use motion to:

  * reinforce navigation
  * indicate scrolling
  * communicate state transitions

---

# 7. Localization and RTL Support

* Support localization using ResX.
* Consider right-to-left layout support where practical.
* Flip directional icons in RTL layouts.
* Do NOT flip icons representing real-world objects.

---

# 8. Typography and Semantic Styles

## Fonts

* Avalonia controls use the system UI font by default — do not override it globally.
* For custom Skia renderers, use **IBM Plex Sans** (`SkiaFonts.PlexRegular` etc.) as the default.
  Fall back to **DejaVu Sans** (`SkiaFonts.DejaVuRegular` etc.) only for wide Unicode coverage.
* Never call `SKTypeface.Default` or `SKTypeface.FromFamilyName(...)` in renderers.
* Create centralized semantic Avalonia styles for any explicit font overrides.
* Avoid excessive typeface variation.

## Hierarchy

Use typography consistently to indicate hierarchy:

* weight
* size
* spacing
* leading

## Leading

* Loose leading improves readability in large text blocks.
* Tight leading may be used in compact lists.
* Avoid tight leading for 3+ line text blocks.

---

# 9. UI Writing Style

## Tone

UI language should be:

* professional
* concise
* direct
* trustworthy
* technically appropriate

Target audience:

* RF engineers
* researchers
* technical managers
* advanced hobbyists

## Writing Rules

* Prefer active voice.
* Use clear verbs for actions.
* Avoid overly clever or playful wording.
* Use consistent terminology.
* Prefer concise labels.
* Avoid unnecessary possessive pronouns.
* Avoid vague “we” language in errors.

## Error Messages

* Make errors actionable and specific.
* Clearly explain:

  * what failed
  * why
  * how to fix it

---

# 10. Data Entry

* Dynamically validate fields while editing.
* Give immediate feedback on invalid input.
* Use numeric formatters for numeric fields.
* Do not request data that can be inferred automatically.
* Never prepopulate password fields.
* Disable Continue/Next actions until required data is valid.

---

# 11. Drag and Drop

## Behavior

* Support drag-and-drop broadly where practical.
* Support both move and copy semantics correctly.
* Support undo for drag operations whenever possible.

## Visual Feedback

Provide:

* drag previews
* insertion indicators
* valid/invalid destination feedback
* progress indicators for long transfers

## UX Expectations

* Keep dragged items selected after drop.
* Support multi-item drag where appropriate.
* Preserve styling when dropping rich text/content.

---

# 12. User Feedback

Feedback should communicate:

* status
* progress
* success/failure
* warnings
* recoverable problems

## Best Practices

* Keep feedback contextual and near related UI.
* Use passive feedback for status updates.
* Use interruptive alerts only for serious or destructive actions.
* Ensure all feedback mechanisms are accessible.

---

# 13. Engineering Expectations for AI UI Code Generation

When generating UI code for circuitRF:

* Prefer Avalonia UI conventions and semantic styles.
* Reuse centralized styling resources.
* Use adaptive/responsive layouts.
* Maintain accessibility standards by default.
* Favor readability and clarity over visual novelty.
* Avoid hardcoded dimensions/colors unless required.
* Use consistent spacing and alignment.
* Keep interfaces efficient for technical workflows.
* Ensure keyboard accessibility where practical.
* Minimize unnecessary dependencies and visual complexity.
* Treat schematic symbols separately from standard UI iconography.

---

# 14. Parameter Editor (Phase 6 / ParameterEditorView)

## One view, two hosts
`ParameterEditorView` (`UserControl`) + `ParameterEditorViewModel` are built once and hosted two ways:
1. **Embedded inspector** — in the Properties region (`PropertiesView`), bound to the active schematic's
   selection via `ParameterEditorViewModel.SetContext(vm)`. Activated by `PropertiesTool.SetActiveSchematic`.
2. **Dialog** — opened on component double-click (`SchematicView.axaml.cs :: OnComponentDoubleTapped`),
   configured via `ParameterEditorViewModel.SetTargetDirect(vm, comp, showClose: true)`. Uses `ParameterEditorDialog` Window.

Neither host decision (coexistence mode, modality) touches `ParameterEditorView` internals — both are
single swappable flags.

## Chosen defaults (owner experiments — change these single points)
- **Coexistence = content-switch** (parameter editor shown when one non-Ground component is selected,
  palette placeholder otherwise). Flag location: `PropertiesView.axaml` `<!-- COEXISTENCE_FLAG -->` comment.
  To switch to stack: replace the outer `Grid` with a `StackPanel` and remove the `IsVisible` toggles.
- **Modality = non-modal** (lets the user see schematic labels update live as they edit). Flag location:
  `SchematicView.axaml.cs :: OnComponentDoubleTapped` `const bool isModal = false;`.

## Units keyed by dimension
Unit ComboBox options come from `ComponentTypeRegistry.UnitOptions(UnitDimension)`, not per-`SymbolKind`.
Each `EditableParameter` carries a `Dimension` field (seeded at placement, type-change, and `FromRenderModel`).
`NumPorts` is never shown as a row. The single Ground/null guard is in `ParameterEditorViewModel.SetTarget`.

## Active-schematic wiring
`WorkspaceViewModel` subscribes to `DocumentDock.PropertyChanged` and calls
`CircuitRfDockFactory.PropertiesTool.SetActiveSchematic(vm)` when `ActiveDockable` changes.
`PropertiesTool` delegates to `EditorVm.SetContext(vm)`.

## Command stack discipline
All edits (expression, unit, ShowOnSchematic, instance name, label visibility) go through `SchematicViewModel.Execute`,
which wraps in `DotRevalidationCommand`. No-change guard in every commit method. `SetParameterVisibilityCommand`
(new in Phase 6) notifies in both Execute and Undo, consistent with all other mutation commands.
