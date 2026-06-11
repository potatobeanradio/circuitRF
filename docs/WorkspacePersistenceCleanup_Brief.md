# circuitRF — Workspace/File Persistence & Dock cleanup (Claude Code / Sonnet)

Eight items, sub-gated by independence and risk. Items 1–5 are surgical and low-risk; items 6–8 are the deep
Dock ones (Reset Layout, workspace doc-restore, tear-off) — instrument-first, sub-gate hard, report+STOP
between each. Do the easy five first and land them before touching Dock internals. Firewall green.

## Read first (real types, verified)
- `ViewModels/WorkspaceViewModel.cs` — `NewScratchSchematic`, `ResetLayout`, `WriteWorkspaceFile`,
  `OpenWorkspace`/`OpenRecentWorkspace`, `SaveLooseToWorkspace`, `SaveLoosePlainFile`, `SaveAllDocuments`,
  `_openDocsByPath`, `_scratchDocs`, `OnDocumentDockPropertyChanged`, `ResolveOwner`.
- `ViewModels/Dock/CircuitRfDockFactory.cs` — `CreateLayout`, `CreateDefaultLayout`, `DocumentDock`,
  `OpenDocument`, the tool properties.
- `Schematic/WorkspacePersistence.cs` — `CwsFile` (`DockLayout`, `KnownFiles`, `LibraryRefs`,
  `ColorSchemeName`, `TreeViewState`), `CurrentFormatVersion`.
- `Views/Dialogs/SavePlanDialog.axaml(.cs)` — the Save-All dialog (item 2).
- `Views/Dialogs/InputNameDialog.axaml(.cs)` — name dialog (item 3).
- `SchematicDocument` (IsScratch/IsDirty/Materialize), `SymbolEditorDocument`, `CellParameterEditorDocument`.

## LAYER 1 — `.csch.csch` duplicate extension [item 1]
In `SaveLooseToWorkspace` AND `SaveLoosePlainFile`, the picker sets both `SuggestedFileName = doc.Id + ".csch"`
and `DefaultExtension = "csch"` → Avalonia appends the default ext → `name.csch.csch`. Fix: set
`SuggestedFileName = doc.Id` (no extension) in both; keep `DefaultExtension = "csch"`. Check `SaveWorkspaceAs`
(`SuggestedFileName="untitled"` + `DefaultExtension="cws"`) is fine (it is — no double ext) and any other
`SaveFilePickerAsync` callers (Symbol `SaveSymbolAsAsync` uses `GetFileNameWithoutExtension` — already correct).
**Gate:** Save-As a scratch schematic → picker proposes `name.csch`, not `name.csch.csch`. Report.

## LAYER 2 — Save-All dialog: centering + column widths (HIG) [item 2]
In `SavePlanDialog.axaml`: (a) text content currently centered — per HIG, left-align text/path cells
(centering is for headers/numerics only); confirm and fix any centered text columns to `Left`. (b) Widen the
columns that clip (workspace directory / path) — use `*`/proportional widths or a min-width so paths are
readable; let the path column take remaining space. Keep the dialog resizable if it isn't. No logic changes —
layout only.
**Gate:** Save-All dialog shows readable, left-aligned paths; the workspace-dir column isn't clipped at the
default dialog size. Report.

## LAYER 3 — Name field focus on create dialogs [item 3]
`InputNameDialog` (used by New Cell / New Cell in Workspace / New Symbol / New Schematic) must focus its name
`TextBox` when shown. In `InputNameDialog.axaml.cs`, focus the TextBox on `Opened` (or `OnLoaded`):
`nameTextBox.Focus(); nameTextBox.SelectAll();`. (Avalonia: set focus in the `Opened` handler, not the
constructor — the visual tree isn't ready earlier.)
**Gate:** open each of the four create dialogs → the name field has focus, ready to type. Report.

## LAYER 4 — new documents start clean (not dirty) [item 4]
`NewScratchSchematic` creates `SchematicDocument` dirty-from-birth (comment says so). A brand-new empty doc
must NOT be dirty; it becomes dirty on the first meaningful edit (component placed, wire drawn, analysis
configured, parameter changed). 
1. New scratch/symbol/schematic/data-display docs start `IsDirty = false` (but still scratch / unsaved). The
   close-prompt should NOT fire for a never-touched new doc (`ConfirmCloseDockable` already returns true when
   `!IsScratch && !IsDirty` — make scratch-but-clean also closeable without prompt, OR keep scratch prompting
   only when dirty: align with "user did something meaningful").
2. Wire dirtiness to real mutations: subscribe to the doc's undo stack (`UndoRedo` — first command pushed →
   dirty) and/or the edit model's change event; `ComponentPlaced` already fires. The undo-stack "can undo
   became true" edge is the cleanest single signal — when the stack goes non-empty, mark dirty.
3. Same principle for `SymbolEditorViewModel` (it already only sets `IsDirty` in `Execute()` — verify a freshly
   created symbol opens clean; `NewSymbolAsync` opens an empty editable — confirm IsDirty=false at open).
**Gate:** create a new schematic → it's not dirty, closing it doesn't prompt; place a component → now dirty,
closing prompts; same for a new symbol. Report.

## LAYER 5 — File menu: "Save All" vs "Save" by active panel (macOS NativeMenu) [item 5]
When the **Project Tree** panel is active → the File-menu item reads **"Save All"** and saves all open docs
(current `SaveAllDocuments`). When a **document/content tab** or a **torn-off document window** is active →
it reads **"Save"** and saves ONLY that active document.
1. Track active context: you already get `OnDocumentDockPropertyChanged` (ActiveDockable) and can tell when a
   document tab is focused vs a tool. Add an observable like `ActiveSaveScope` (enum AllDocs | SingleDoc) +
   `SaveMenuHeader` ("Save All" / "Save") the menu binds to. Drive it from which dockable/panel is active
   (tree/tool focused → AllDocs; a Document active → SingleDoc).
2. The Save command branches on scope: AllDocs → `SaveAllDocuments`; SingleDoc → save the active document only
   (reuse `SaveSingleDocument` for schematics; add symbol/cell save paths as needed).
3. **macOS NativeMenu:** the in-window `Menu` and the macOS `NativeMenu` are separate; update BOTH headers.
   Bind the in-window MenuItem `Header`/`Command` to the observable; for NativeMenu, the code-behind that
   builds it (same place RecentWorkspaces is rebuilt via `RecentWorkspacesChanged`) must update the
   NativeMenuItem header when scope changes — add a `SaveScopeChanged` event mirroring the recent-menu pattern,
   and rebuild/relabel the NativeMenuItem. Test on macOS specifically — `$parent[Window]` is null there, so
   use `ResolveOwner(null)`.
**Gate (macOS + in-window):** focus the Project Tree → menu says "Save All", saves everything; focus a
document tab → "Save", saves only it; focus a torn-off doc window → "Save", saves only it. Report on macOS.

## LAYER 6 — Reset Layout must preserve documents/tabs/selection [item 6 — BUG]
`ResetLayout` calls `CreateDefaultLayout()` → `CreateLayout()`, building a BRAND-NEW tree (fresh `welcome`
stub, new tool instances) and replacing `Layout` — discarding all open documents, the active tab, and
selection. Reset should ONLY restore dock geometry (panel positions/proportions/splitters), keeping the
existing document dockables, active tab, and in-document selection intact.
- **Approach:** instead of rebuilding from scratch, reset the *proportional* structure while RE-HOSTING the
  existing `DocumentDock` (with its current `VisibleDockables` + `ActiveDockable`) and the existing tool
  instances into the default arrangement. I.e. build the default proportional skeleton but insert the
  CURRENT `_factory.DocumentDock` and current tools, not new ones. Do not new-up `welcome`, the tools, or the
  documents.
- If Dock makes in-place re-hosting hard, the alternative is to capture the current document list + active +
  per-doc selection, rebuild, then re-add the SAME document instances and restore active/selection. Prefer
  re-hosting the existing instances (no document re-creation) so selection state is inherently preserved.
**Gate:** open 3 docs, select something in one, switch active tab, drag a panel, then Reset Layout → panels
return to default arrangement BUT all 3 docs remain, the active tab is unchanged, and the selection is intact.
Report.

## LAYER 7 — persist + restore open documents per workspace [item 7 — BUG, deep]
Opening a workspace shows none of the documents that were open when it was saved, and switching workspaces
must show ONLY that workspace's docs. Root cause: nothing persists the open-document set — `CwsFile.DockLayout`
is never written, and there is no open-docs list. 
- **Design (simplest robust): persist an explicit open-documents list in `.cws`, not the raw Dock layout.**
  Add to `CwsFile` an `OpenDocuments` list: each entry = { relative-or-absolute path, doc kind
  (schematic/symbol/cell), tab order index }, plus an `ActiveDocumentPath`. Bump `CurrentFormatVersion`
  (alpha: reject-on-mismatch, no migration — per standing rules).
- **Write:** in `WriteWorkspaceFile` / `SaveAllDocuments`, enumerate the DocumentDock's documents in tab order
  (path-keyed via `_openDocsByPath`; skip unsaved scratch docs — only persist docs with a real path), record
  the active one. (Torn-off documents that live in float windows must be included — enumerate floated
  HostWindows too, like `TryWireHostWindowsUndo` does, so a torn-off doc is restored.)
- **Restore:** on `OpenWorkspace`/`OpenRecentWorkspace`, AFTER setting `CurrentWorkspacePath`: clear the
  current DocumentDock (remove all open docs — this is the "second workspace shows none of the first"
  requirement), then for each persisted entry re-open via the existing `OpenOrActivateSchematic` /
  `OpenOrActivateSymbol` / cell path, in order, and finally `SetActiveDockable` the saved active one. Rebuild
  `_openDocsByPath` as you go (it's already rebuilt-on-open by design).
- **Switching guarantee:** opening workspace B must remove workspace A's docs from the view entirely (tabs and
  any float windows). Closing/replacing the document set on open handles tabs; for float windows, close
  Dock-created HostWindows belonging to the prior workspace.
- v1 scope: restore tab order + active tab. Per-document view state (zoom/scroll) is out of scope unless
  trivial. Torn-off restoration: re-open as a normal tab is acceptable for v1 IF re-tearing isn't ready —
  but the brief's target is to restore torn-off docs to a float; gate it and if float-restore is too costly,
  fall back to restoring them as tabs and SAY SO.
**Gate:** workspace A with 3 docs (one active, one torn off) → Save All → open workspace B (A's docs all gone,
only B's shown) → reopen A (all 3 restored in order, correct active tab; torn-off restored per the chosen v1
behavior). Report exactly what torn-off restoration does.

## LAYER 8 — tear-off broken [item 8 — BUG, possibly large; INSTRUMENT FIRST]
Dock documents reportedly cannot be torn off (and maybe not re-attached). Given this project's Dock history,
do NOT guess.
1. **Instrument/diagnose first:** determine whether tear-off is (a) disabled by config (DocumentDock
   `CanFloat`/`CanDrag` or the Factory's float settings), (b) failing because no HostWindow
   locator/`CreateWindowFrom` is set up, or (c) failing at drag-detection. Log the relevant Dock factory
   hooks and report which. Check whether `Factory` overrides for window creation (HostWindow, `CreateLayout`
   for windows, `IDockWindow`) exist — Dock.Avalonia needs a window-locator wired to float.
2. Report findings BEFORE fixing. Then wire the minimal Dock float support: ensure documents are floatable
   (`CanFloat = true`), the Factory provides a HostWindow/window template, and re-docking is allowed.
3. Coordinate with Layer 7: torn-off docs must be enumerable for persistence and closeable on workspace
   switch.
**Gate (after diagnosis report):** a document tab can be dragged out to its own window and dragged back into
the dock. Report the diagnosis first, then the fix.

## Acceptance
Items 1–5 fixed and gated; Reset Layout preserves docs/active/selection; workspaces persist+restore their open
documents (and switching workspaces shows only the active workspace's docs); tear-off diagnosed and (if
feasible in scope) working. `.cws` format_version bumped for the OpenDocuments addition. `dotnet
build`/`dotnet test` green; firewall green; no regression to existing save/recovery/undo-routing.

## Guardrails
- Land items 1–5 (surgical) and gate each before touching Dock internals (6–8).
- **Item 8 is instrument-first** — diagnose and REPORT before changing Dock float config.
- Alpha persistence rules: `format_version` written + rejected-on-mismatch, never migrated; `Id` never
  persisted; only persist documents with a real path (skip unsaved scratch).
- Reset Layout: re-host EXISTING document/tool instances — never re-create documents (that's the bug).
- macOS: NativeMenu and in-window menu are separate — update both; `$parent[Window]` is null on macOS, use
  `ResolveOwner(null)`.
- Sub-gate; report+STOP between layers; don't batch 6/7/8.
- Update `docs/design/workspace-and-project-tree.md` + `project-file-formats.md` (the new `.cws` OpenDocuments
  field + version bump) + `src/Ui/CLAUDE.md` (Reset Layout re-hosts; workspace persists open-docs list).

*Exit: clean save-extension and dialogs; new docs aren't dirty until touched; the File menu reflects
Save-All-vs-Save by context on macOS; Reset Layout only rearranges; workspaces restore exactly the documents
(and active tab) the user left, showing only the active workspace's docs; tear-off diagnosed and addressed.*