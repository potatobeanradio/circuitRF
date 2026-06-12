# Brief E — Cell Parameter Editor + Properties Inspector (header by selection type)

**Scope:** two related pieces.
1. A simple editable view of a **cell's** properties (primary schematic, primary symbol, and port count) — opened from the Cell's "Edit Parameters" context menu, shown as a content document and/or in the Properties Inspector.
2. The Properties Inspector **header text** must change with the selection: cell → "Cell", component → "Component", symbol-editor primitive → "Symbol".

UI-layer only. Read the schema notes carefully — there is a real design decision about port count.

---

## Read first (real names)

- `src/Ui/Schematic/CellPersistence.cs` — **`CcellFile`** on-disk model. Fields: `List<CcellParameter> Parameters`, `string? PrimarySchematic`, `string? PrimarySymbol`, `string? PrimaryLayout`, `bool IsTestBench`, `int FormatVersion`. **There is NO port/pin count field on the cell.** (This drives the Layer 1 decision below.)
- `src/Ui/Schematic/CellParameterEditModel.cs` — `CellParameterEditModel(string ccellPath, CcellFile file)`. Exposes `CcellPath`, `Parameters` (read), `MutableParameters` (commands only), `Save()` (writes `.ccell`), `NotifyChanged()`/`Changed`. Today it only wraps the parameter list.
- `src/Ui/Schematic/CellParameterEditorDocument.cs` — `CellParameterEditorDocument : Document, IUndoableDocument`. Wraps `CellParameterEditorViewModel ViewModel` and `UndoRedo => ViewModel.UndoRedo`.
- **`CellParameterEditorViewModel`** — FIND IT (grep `class CellParameterEditorViewModel`; likely `src/Ui/ViewModels/...`). Read its current shape (rows, undo stack, how it binds to `CellParameterEditModel`).
- `src/Ui/Schematic/CellFolder.cs` — `SubFolderPath(cellDir, ViewType.Schematic/Symbol)`, `ViewExtension`, `SchematicSubFolder`/`SymbolSubFolder`, `ResolvePrimary(...)`. Use these to enumerate the cell's schematic/symbol files.
- `src/Ui/ViewModels/WorkspaceViewModel.cs` — `OpenOrActivateCellPlaceholder(absolutePath, cellName)` builds `CellParameterEditModel(ccellPath, file)` + `CellParameterEditorViewModel(cellName, editModel)` + `CellParameterEditorDocument(cellName, vm)`. This is the "Edit Parameters" open path. Also `OnDocumentDockPropertyChanged` routes active docs to `PropertiesTool.SetActiveSymbolEditor` / `SetActiveSchematic`.
- `src/Ui/ViewModels/Dock/PropertiesTool.cs` — `Tool` with `Title="Properties"`, `EditorVm` (`ParameterEditorViewModel`, schematic), `SymbolInspectorVm` (`SymbolPrimitiveInspectorViewModel`), `IsSymbolEditorActive`. Methods `SetActiveSchematic(vm)` / `SetActiveSymbolEditor(vm)`. **The header is the `Title`.**
- `src/Ui/Views/Properties/PropertiesView.axaml(.cs)` — the Properties pane view. Read how `Title`/header is shown and how it switches sub-views on `IsSymbolEditorActive`.
- `src/Ui/Views/ParameterEditor/*` — the schematic parameter-editor views (reference for building the cell view in the same style).
- The symbol that determines a cell's electrical interface: `Symbol.PortCount` (from `SymbolPersistence.LoadFromFile`). For a cell, the "number of ports" is the primary symbol's `PortCount`. Components use `PortCount` (= electrical N); symbols also expose `Pins` (= N+1 physical, includes reference). See `SymbolPortDefs` / `EditableComponent.PortCount`.

---

## Terminology decision (answer the user's "ports or pins?")

Use **"Ports"** at the cell/user level. Rationale grounded in the codebase: the electrical interface count is `PortCount` (N) everywhere (`EditableComponent.PortCount`, `ZPort`, `SymbolPortDefs.For(kind, portCount)`). "Pins" in this codebase are the **symbol's physical connection points** (N+1, including the reference) — an internal symbol-geometry detail, not the cell's user-facing interface. So the cell editor says **Ports** (N).

---

## Layer 1 design decision — where does a cell's port count live?

**`.ccell` has no port-count field.** A cell's port count is really the **primary symbol's `PortCount`** (the symbol declares the interface). So "edit the number of ports" has two possible meanings; pick ONE and state it:

- **(RECOMMENDED for v1 — single source of truth, smallest schema change):** The port count is **owned by the primary symbol**, and the Cell editor **displays it read-only** (resolved via `ResolvePrimary(cellDir, ViewType.Symbol)` → load that `.csym` → `symbol.PortCount`). Editing the count is done in the Symbol Editor (which already authors ports). This honors the standing "single source of truth" rule and avoids a `.ccell`/`.csym` drift bug. The Cell editor's editable fields are then **Primary Schematic** and **Primary Symbol** (combo boxes); Ports is shown read-only with a hint ("set in the primary symbol").
- (Alternative, only if the user insists on editing count here): add an authoritative `int PortCount` to `CcellFile` and have the editor write it. **This creates a drift risk** (`.ccell` count vs `.csym` `PortCount`) that the alpha "enforce at input / single source of truth" rule warns against. If chosen, you MUST also define how it reconciles with the primary symbol (e.g. writing the cell count rewrites the primary symbol's port count). This is more work and more risk.

**Default to the RECOMMENDED option** unless told otherwise. Implement editable Primary Schematic + Primary Symbol combos now; show Ports read-only. Leave a clearly-commented seam for making Ports editable later. State your choice in the report.

---

## Layer 2 — Cell editor: primary schematic + primary symbol combo boxes

Extend `CellParameterEditModel` + `CellParameterEditorViewModel` + the editor view so the cell's primary schematic and primary symbol are editable via combo boxes.

1. **Enumerate options:** from the cell folder (derive `cellDir` from `CcellPath` via `Path.GetDirectoryName`), list files in `CellFolder.SubFolderPath(cellDir, ViewType.Schematic)` matching `*.csch`, and in `.../Symbol` matching `*.csym`. Each combo lists those filenames; the selected item maps to `CcellFile.PrimarySchematic` / `PrimarySymbol` (stored as the bare filename, e.g. `amp.csch`, matching `MakePrimary` convention). Include a "(none)" option (null).
2. **Expose on the edit model:** add `PrimarySchematic`/`PrimarySymbol` get/set that read/write the underlying `CcellFile` and call `Save()` + `NotifyChanged()` — ideally through an **undoable command** so it routes the document's `UndoRedo` (mirror the existing parameter-edit commands in this editor; the document is `IUndoableDocument`). If a full command is heavy, at minimum persist on change and keep behavior consistent; prefer the command path to match the rest of the app's "every mutation is undoable" rule.
3. **Resolve Ports (read-only):** when a primary symbol is set, load it (`SymbolPersistence.LoadFromFile(Path.Combine(symbolSubDir, PrimarySymbol))`) and show `symbol.PortCount`. If no primary symbol, show "—".
4. **Make-Primary parity:** setting the primary symbol here should behave like `WorkspaceViewModel.MakePrimary` does for symbols — after writing `.ccell`, the cell-ref resolver cache must be invalidated and open schematics rebuilt so cell-reference components re-render. Reuse that path: either call the existing invalidation (`CellSymbolResolver.Invalidate(cellDir)` + rebuild open schematics) or raise an event the WorkspaceViewModel handles. Do NOT silently skip this — a stale resolver is a real bug. (Read `MakePrimary` + `OnSymbolSaved` + `RebuildOpenSchematics` for the exact calls.)
5. **View:** build the editor view in the style of `src/Ui/Views/ParameterEditor/*`: two combo boxes (Primary Schematic, Primary Symbol), a read-only Ports field, and the existing parameter list. It can live in the content-tab document (as today) — the user accepts either a modal or the Properties Inspector; the existing content-document is the least disruptive, so **keep the content-tab document** and ALSO surface cell properties in the Inspector per Layer 3.

**Gate 2:** Cell context-menu → Edit Parameters opens the cell editor. Primary Schematic and Primary Symbol combos list the cell's `.csch`/`.csym` files; changing them persists to `.ccell` and updates the tree's primary markers (Refresh) and re-resolves cell-ref components in open schematics. Ports shows the primary symbol's `PortCount` (read-only).

---

## Layer 3 — Properties Inspector: show cell properties + header by selection type

The Properties Inspector currently shows the schematic parameter editor (`EditorVm`) or the symbol primitive inspector (`SymbolInspectorVm`), switched by `IsSymbolEditorActive`. Add a **cell** context and make the **header (`PropertiesTool.Title`)** reflect what's shown.

1. **Header text:** change `PropertiesTool.Title` based on the active context:
   - cell selected/active → `"Cell"`
   - component selected (schematic) → `"Component"`
   - symbol-editor primitive selected → `"Symbol"`
   - (fallback when nothing specific is selected → keep `"Properties"`.)
   Implement by setting `Title` in the `SetActive*` methods (and add a `SetActiveCell`). Since `Title` is a Dock `Tool` property, confirm it updates the visible header (the `PropertiesView`/dock binds the tool title). If the pane header doesn't bind `Title`, add a bound header `TextBlock` to `PropertiesView.axaml` driven by an observable (`HeaderText`) on `PropertiesTool`.

   Nuance the user specified: "Inspector shows the cell properties when the user clicks a cell; if they then click a component, it shows the component's properties (current behavior)." So selection drives the header dynamically — clicking a component while a cell is active flips header to "Component". Wire the header to whatever context was most recently activated.

2. **Cell context in the Inspector:** add `SetActiveCell(CellParameterEditorViewModel? vm)` (or a lighter cell-properties VM) to `PropertiesTool`, set `Title="Cell"`, and clear the other two contexts (mirror how `SetActiveSchematic`/`SetActiveSymbolEditor` clear each other). Add an `IsCellActive` flag and a cell sub-view (can reuse the Layer 2 combos/read-only-ports view, or a compact version). Route it: in `WorkspaceViewModel.OnDocumentDockPropertyChanged`, when the active dockable is a `CellParameterEditorDocument`, call `PropertiesTool.SetActiveCell(cpd.ViewModel)`; for component selection within a schematic, the existing `SetActiveSchematic` path already shows component properties — set `Title="Component"` when a component is the active selection (and `"Cell"` only when a cell doc/selection is active). For the symbol editor, set `Title="Symbol"`.

   Keep it simple: the three `SetActive*` methods each set the correct `Title` and toggle the correct `IsXActive` flag; `PropertiesView.axaml` switches sub-views on those flags and shows the header.

**Gate 3:** Click a cell (open its editor / select it) → Inspector header reads "Cell" and shows cell properties. Click a component in a schematic → header reads "Component", shows component properties. Open the Symbol Editor and select a primitive → header reads "Symbol". With nothing selected → header reads "Properties".

---

## Acceptance

- Cell "Edit Parameters" opens an editor with editable Primary Schematic + Primary Symbol combos and a read-only Ports count (from the primary symbol). ✅
- Changing primaries persists to `.ccell`, refreshes tree primacy, and re-resolves cell-ref components (no stale resolver). ✅
- Terminology is "Ports" (N). ✅
- Properties Inspector header is "Cell" / "Component" / "Symbol" by selection, "Properties" when nothing specific. ✅
- Undo routes through the cell document's stack for cell edits. ✅

## Guardrails

- Default to **port count owned by the primary symbol** (read-only in the cell editor). Do NOT add a `CcellFile.PortCount` unless you explicitly choose the alternative and implement the reconciliation — and say so loudly in the report.
- `.ccell` writes go through `CellPersistence.SaveToFile`; honor format_version reject-on-mismatch (don't migrate).
- Reuse the existing Make-Primary invalidation/rebuild path; never leave the cell-ref resolver stale.
- Keep `CcellFile`/`CellParameterEditModel`/`CellFolder` framework-free.
- Minimal diff; list files touched.

## Scope fence (do NOT do here)

- No grippers (A), clipboard (B), open-`.csym`/tab-names (C), Known Files (D).
- Don't build a general cell-rename or layout editor; only ports(read-only)+primary combos+inspector header.

## Exit / report

State: which port-count option you chose (and why); whether cell edits are undoable commands; how you reused the Make-Primary invalidation; exactly how the header `Title` updates (binding path); and confirmation you ran all 3 gates mentally. Flag every assumption.
