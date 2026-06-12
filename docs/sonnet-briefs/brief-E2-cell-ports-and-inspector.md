# Brief E2 — Cell ports (editable, cell-owned) + Properties Inspector cell view

**Scope:** finish the cell editor / Properties Inspector work. Four changes. The primary-persistence bug is **already fixed by Opus** — do not reintroduce it (see Guardrails).

This supersedes Brief E's "port count owned by the symbol, read-only" recommendation. **Decision reversed: the CELL owns the port count.** Rationale below.

---

## Definitions (lock these)

- **Pin** = a pin object placed in a symbol (symbol-level, geometric).
- **Port** = the cell's electrical terminal — the interface the cell exposes to the outside world (cell-level, abstract). A cell with N ports needs a symbol whose pins realize those N ports.
- **The cell is the single source of truth for the port COUNT.** The primary symbol conforms; a mismatch surfaces via the symbol editor's existing unmapped-port panel (`EditableSymbol.ExternalPortCount`). This matches the existing design — the symbol editor already consumes an *external* port count.

---

## Read first (real names)

- `src/Ui/Schematic/CellPersistence.cs` — `CcellFile`. Currently NO port field. You will ADD `int NumPorts`.
- `src/Ui/Schematic/CellParameterEditModel.cs` — wraps `CcellFile`. Has `PrimarySchematic`/`PrimarySymbol` getters + internal `SetPrimarySchematic/Symbol(value)` (each does `_file.X = v; Save(); NotifyChanged();`). `CellDir`, `Save()`, `NotifyChanged()`, `Changed`, `PrimarySymbolChanged`. You will add `NumPorts` + `SetNumPorts(int)` + a `PortCountChanged` notification.
- `src/Ui/Commands/Cell/SetCellPrimaryCommand.cs` — the undoable-command pattern to mirror for a new `SetCellPortCountCommand`.
- `src/Ui/ViewModels/CellParameterEditorViewModel.cs` — **Opus just edited this.** It now has `BuildAvailableFileLists()` (called ONCE in ctor) and `SyncPrimarySelectionsFromModel()` (called on every `_editModel.Changed`, syncs selection + port count WITHOUT reassigning the combo `ItemsSource`). `PortCount` is currently a read-only `string` ("—"). You will make it an editable integer (`NumPorts`) with validation.
- `src/Ui/Views/Content/CellParameterEditorView.axaml` — the content-tab cell editor. Already has: header, a Primary Schematic/Symbol/Ports info `Border`, an Add-Parameter footer, and a **scrollable params `ItemsControl` inside a `ScrollViewer`** (the pattern the inspector should reuse). The "Ports" row is a read-only `TextBlock` bound to `ViewModel.PortCount` — change to editable + relabel.
- `src/Ui/ViewModels/Dock/PropertiesTool.cs` — `Tool` (`Title`/header). Has `SetActiveSchematic` / `SetActiveSymbolEditor`, `IsSymbolEditorActive`. The header-by-selection work from Brief E landed here (Title becomes "Component"/"Symbol"). You will add a **Cell** context: `SetActiveCell(...)`, `IsCellActive`, `Title="Cell"`.
- `src/Ui/Views/Properties/PropertiesView.axaml(.cs)` — the inspector view; switches sub-views on the `IsXActive` flags and shows the header. You will add a cell sub-view hosting the cell editor body with a **scrollable** params list.
- `src/Ui/ViewModels/Dock/ProjectTreeTool.cs` — `[ObservableProperty] ProjectTreeNodeViewModel? SelectedItem` (observable). The TreeView binds selection to it.
- `src/Ui/ViewModels/WorkspaceViewModel.cs` — owns `_factory.ProjectTreeTool` and `_factory.PropertiesTool`. `OnDocumentDockPropertyChanged` routes active-document → inspector. `OpenOrActivateCellPlaceholder(absolutePath, cellName)` builds the cell VM/doc. `TryCellPortCount(csymPath, symbol)` currently returns `symbol.PortCount` as the symbol's `ExternalPortCount` — **this must change to the cell's `NumPorts`** (Layer 1).

---

## Spine (do-not-violate)

1. The cell owns `NumPorts`. `.ccell` is the single source of truth for the count. The symbol's `ExternalPortCount` is fed FROM the cell.
2. Every cell mutation (primary, port count, parameter) is an undoable command that calls `Save()` + `NotifyChanged()` in BOTH Execute and Undo (mirror `SetCellPrimaryCommand`).
3. Alpha persistence policy: `.ccell` `FormatVersion` is written + reject-on-mismatch, NEVER migrated. Adding `NumPorts` to the record is fine (new files get it; the format is still v1 since we don't bump unless you must — KEEP `CurrentFormatVersion = 1`, new field defaults to 0 so existing alpha files still parse).
4. Do NOT reintroduce the combo `ItemsSource` churn that Opus just removed (Guardrails).
5. The inspector and the content document should host the SAME cell-editor body (factor it; don't duplicate the params UI).

---

## Layer 1 — Cell owns `NumPorts` (schema + model + command), feeds the symbol

1. **`CcellFile`** (CellPersistence.cs): add `public int NumPorts { get; set; }` (default 0). Keep `CurrentFormatVersion = 1` (new field defaults cleanly; existing files omit it → 0). Place it near `IsTestBench`.
2. **`CellParameterEditModel`**: add `public int NumPorts => _file.NumPorts;` and `internal void SetNumPorts(int value) { _file.NumPorts = value; Save(); NotifyChanged(); PortCountChanged?.Invoke(CellDir); }`. Add `public event Action<string>? PortCountChanged;` (mirror `PrimarySymbolChanged`).
3. **`SetCellPortCountCommand`** (`src/Ui/Commands/Cell/`): mirror `SetCellPrimaryCommand` — capture old/new int, Execute → `SetNumPorts(new)`, Undo → `SetNumPorts(old)`, `Description = $"Set number of ports to {n}"`.
4. **`WorkspaceViewModel.TryCellPortCount`**: it currently returns `symbol.PortCount` when the `.csym` is under a cell. Change it to return the **cell's `NumPorts`** (read the sibling `.ccell` via `CellPersistence.LoadFromFile`, return `ccell.NumPorts`) so the symbol editor's `ExternalPortCount` reflects the cell, not the symbol. Keep the "orphan symbol → null" behavior (no cell → no external authority). Also subscribe to the cell VM's `PortCountChanged` (where the cell editor is constructed) to invalidate the cell-symbol resolver + rebuild open schematics, same as `OnSymbolSaved`/`PrimarySymbolChanged` does (a NumPorts change alters the cell interface that cell-ref components depend on).

**Gate 1:** Set a cell's NumPorts in `.ccell` (via the editor in Layer 2) → opening that cell's primary symbol shows the unmapped-port panel sized to the cell's NumPorts (not the symbol's own pin count). NumPorts persists across reopen.

---

## Layer 2 — Make "Number of Ports" editable + validated in the cell editor

In `CellParameterEditorViewModel`:
1. Replace the read-only `string PortCount` with an editable integer path:
   - `[ObservableProperty] private int _numPorts;` plus a string-backed input if you want inline validation, OR keep an `int` and validate in the change callback.
   - In `SyncPrimarySelectionsFromModel()` (the method Opus added), set `NumPorts = _editModel.NumPorts;` under the existing `_suppressPrimaryChangeEvents` guard (so programmatic sync doesn't fire the command). Extend the guard to also cover the NumPorts callback (rename the flag if helpful, e.g. `_suppressModelSyncEvents`, but keep its existing role intact).
   - `partial void OnNumPortsChanged(int value)`: if suppressed, return; **validate** (integer ≥ 0; clamp to a sane max, e.g. 0–64); if the validated value differs from `_editModel.NumPorts`, `UndoRedo.Execute(new SetCellPortCountCommand(_editModel, value))`. If invalid, revert the VM property to `_editModel.NumPorts` (graceful — no exception, no persisted bad value).
2. **Drop the symbol-derived port logic** (`UpdatePortCount` reading the primary `.csym`). Ports now come from the cell. Remove or repurpose `UpdatePortCount` to just `NumPorts = _editModel.NumPorts`. (Optionally show the primary symbol's actual pin count as a SECONDARY read-only hint like "symbol has 2 pins" to flag mismatches — nice-to-have, state if you add it.)

In `CellParameterEditorView.axaml`:
3. Relabel the row `"Ports"` → **`"Number of Ports"`**.
4. Replace the read-only `TextBlock` with an editable integer input. Prefer a `NumericUpDown` (built-in validation, integer increments, `Minimum=0`, `Maximum=64`) bound `Value="{Binding ViewModel.NumPorts}"`. If `NumericUpDown` styling is awkward, a `TextBox` with the change-callback validation above is acceptable — state which you used.

**Gate 2:** The cell editor shows "Number of Ports" with an editable integer control. Typing a valid integer persists to `.ccell` (survives reopen) and is undoable (Cmd+Z reverts it). Entering a negative/garbage value is rejected gracefully (reverts to last valid; nothing bad written). Changing it triggers the resolver invalidation (open schematics with a ref to this cell re-render).

---

## Layer 3 — Properties Inspector shows cell properties on TREE CLICK

The inspector currently reacts only to the active DOCUMENT (`OnDocumentDockPropertyChanged`). Add: clicking (selecting) a **cell** node in the Project Tree shows that cell's properties in the inspector.

1. **`PropertiesTool`**: add a cell context mirroring the others:
   - `[ObservableProperty] private CellParameterEditorViewModel? _cellVm;`
   - `[ObservableProperty] private bool _isCellActive;`
   - `public void SetActiveCell(CellParameterEditorViewModel? vm) { IsCellActive = vm is not null; CellVm = vm; if (vm is not null) { IsSymbolEditorActive = false; EditorVm.SetContext(null); SymbolInspectorVm.SetContext(null); Title = "Cell"; } }`
   - Ensure `SetActiveSchematic`/`SetActiveSymbolEditor` also clear the cell context (`IsCellActive=false; CellVm=null`) and set their own `Title` ("Component" for a schematic with a selected component / "Symbol" for the symbol editor), consistent with the Brief E header work. Last selection wins.
2. **Route tree selection** in `WorkspaceViewModel`: subscribe to `_factory.ProjectTreeTool.PropertyChanged`; when `e.PropertyName == nameof(ProjectTreeTool.SelectedItem)`, read the selected node; if `node.Kind == NodeKind.Cell`, build a cell VM from its `.ccell` (same construction as `OpenOrActivateCellPlaceholder`: `CellPersistence.LoadFromFile(ccellPath)` → `CellParameterEditModel` → `CellParameterEditorViewModel`) and call `_factory.PropertiesTool.SetActiveCell(vm)`. Subscribe that VM's `PortCountChanged`/`PrimarySymbolChanged` to the resolver-invalidation path (Layer 1). If the selected node is NOT a cell, leave the inspector as-is (don't forcibly clear — the active document still governs).
   - Wire the subscription where the tool is (re)created/wired (next to `_factory.ProjectTreeTool?.SetActions(this)` and the `OnDocumentDockPropertyChanged` subscription, including the re-wire in `SwitchToWorkspace`/`NewWorkspace`/`CreateDefaultLayout` paths).
3. **Undo routing (v1):** inspector cell edits persist immediately via their commands (each Saves). Global Cmd+Z routing to a tree-selected cell's VM is **deferred** for v1 unless trivial — state what you did. (Do NOT break document-based undo routing.)

**Gate 3:** Single-click a cell in the Project Tree → inspector header reads "Cell" and shows that cell's primaries + Number of Ports + parameters, editable, persisting. Click a component in a schematic → header flips to "Component" (existing behavior). Open the symbol editor + select a primitive → "Symbol".

---

## Layer 4 — Inspector params in a scroll view (reuse the content body)

The inspector's cell view must list parameters in a `ScrollViewer` like the content panel does.

1. **Factor the cell-editor body** (the Primary Schematic/Symbol + Number-of-Ports block, the Add-Parameter control, and the scrollable params `ItemsControl`) out of `CellParameterEditorView.axaml` into a reusable `UserControl` (e.g. `CellParameterBodyView`) bound to a `CellParameterEditorViewModel`. The content-tab `CellParameterEditorView` hosts it (keeping its header); the inspector's cell sub-view hosts the SAME control bound to `PropertiesTool.CellVm`.
2. Keep the params `ScrollViewer` (`VerticalScrollBarVisibility="Auto"`) exactly as in the content view so the inspector scrolls identically. Mind the inspector's narrower width — let columns compress (the shared-size groups already handle this); the params list should scroll vertically, not overflow.

**Gate 4:** In a narrow inspector, a cell with many parameters scrolls vertically within the inspector; the primaries/ports block stays put above the scroll region. The content-tab editor is visually unchanged (still uses the shared body).

---

## Acceptance

- `.ccell` has `NumPorts`; the cell owns it; it persists; existing alpha `.ccell` files still load (default 0). ✅
- "Number of Ports" is editable + validated (int ≥ 0, graceful reject) + undoable; feeds the symbol editor's `ExternalPortCount` (unmapped-port panel reflects the cell). ✅
- Clicking a cell in the Project Tree shows its properties in the inspector; header reads "Cell"; component/symbol selection still flips the header. ✅
- Inspector params scroll like the content panel; content + inspector share one body view. ✅
- The primary-persistence fix Opus made is intact (set primaries → edit params → reopen → primaries retained). ✅

## Guardrails

- **Do NOT reintroduce the persistence bug.** `BuildAvailableFileLists()` runs ONCE (ctor); per-change handlers must NOT reassign `AvailableSchematics`/`AvailableSymbols`. If you touch that VM, keep the build-once / sync-selection-only split. After your changes, mentally run: set primaries → add a parameter → confirm primaries are NOT wiped.
- Every cell mutation is an undoable command with Save+Notify in Execute AND Undo.
- Keep `.ccell` `FormatVersion = 1`; do not migrate; new field defaults to 0.
- Reuse the resolver-invalidation/rebuild path for NumPorts + primary changes; never leave the cell-ref resolver stale.
- Keep `CcellFile`/`CellParameterEditModel`/`CellFolder` framework-free.
- One shared body view — do not duplicate the params UI between content and inspector.
- Minimal diff; list every file touched.

## Scope fence (do NOT do here)

- No grippers (A), clipboard (B), tab-name/open (C), Known Files (D).
- No global-undo routing for inspector-hosted cells unless trivial (state it).
- Don't build symbol-pin auto-reconciliation; the unmapped-port panel already surfaces mismatch.

## Exit / report

State: the `CcellFile.NumPorts` default + that old files still load; whether you used `NumericUpDown` or validated `TextBox`; how `TryCellPortCount` now reads the cell; the exact tree-selection subscription site(s) (including re-wire paths); whether you shipped global-undo for inspector cells; the name of the factored shared body view; and a one-line confirmation you re-ran the persistence repro mentally against the final code.
