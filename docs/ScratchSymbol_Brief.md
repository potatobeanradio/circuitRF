# circuitRF — Scratch symbols: New-Symbol-on-launch mirrors scratch schematics (Claude Code / Sonnet)

Change New-Symbol-on-launch from a "requires a workspace/cell" message to a real **scratch symbol** lifecycle
mirroring scratch schematics: an in-memory editable symbol the user is prompted to save — either INTO A CELL or
as an ORPHAN .csym. Sub-gated; report+STOP between layers. Firewall green; build/test green each layer.

## Scope decision (read first)
The symbol editor VM ALREADY has most of the machinery: `IsDirty`, `CurrentSymbolPath`, a working "Save As…"
that writes an orphan .csym via the file picker (`SaveSymbolAsAsync` → `PerformSave`), and the `SymbolSaved`
event. So **orphan .csym save already works.** Two real gaps:
1. `SymbolEditorDocument` has NO scratch/dirty/Materialize machinery (unlike `SchematicDocument`): no tab
   bullet, no close-prompt, no Save-All participation.
2. There is no "save into a NEW CELL" path (create the cell folder + symbol/ subfolder, write the .csym there).

We mirror the schematic lifecycle at the DOCUMENT level (scratch tracking + dirty bullet + close-prompt +
Save-All), but for the save-target choice we reuse the simple two-option OFFER DIALOG pattern (like
`SaveLooseNoWorkspace`) — NOT the full multi-step SavePlan/cell-wizard. Offer: **"Save to Cell…"** vs **"Save
as File"** (orphan, already works). Full new-cell-wizard parity is a deliberate v2 deferral.

## Read first (verified on disk)
- Schematic/SchematicDocument.cs — the scratch template: `FilePath`/`IsScratch`/`IsDirty` (title bullet),
  dirty-on-first-undo wiring, `Materialize(path)`. MIRROR this in SymbolEditorDocument.
- Schematic/SymbolEditorDocument.cs — currently bare (Id/Title/ViewModel/UndoRedo). Add scratch tracking.
- ViewModels/SymbolEditorViewModel.cs — has IsDirty, CurrentSymbolPath, SaveSymbolAsync/SaveSymbolAsAsync,
  PerformSave, SymbolSaved. Reuse.
- ViewModels/WorkspaceViewModel.cs —
  - `ExecuteLaunchActionAsync` NewSymbol case (currently a Messages.Info fallback) — replace with scratch-symbol
    creation.
  - `NewScratchSchematic()` + `NextScratchSchematicTitle()` — template for `NewScratchSymbol()` +
    `NextScratchSymbolTitle()`.
  - `_scratchDocs` (typed `List<SchematicDocument>`) — needs a sibling for scratch symbols, OR generalize (see
    L1 note).
  - `NewSymbolAsync(cellNode)` — the EXISTING cell-based New Symbol (writes empty .csym into a cell's symbol/
    subfolder, opens editor). The "Save to Cell…" target reuses this folder logic.
  - `ConfirmCloseDockable`, `SaveAllDocuments`, `PromptSaveBeforeClose`, `HasAnyDirtyWork` — schematic-typed;
    extend to symbols.
  - `SaveLooseNoWorkspace` — the two-option offer-dialog PATTERN to mirror for "Save to Cell / Save as File".
- CellFolder.cs — `CreateCellFolder`, `SubFolderPath(cellDir, ViewType.Symbol)`, `ViewExtension` — cell
  creation + symbol subfolder path (reused by Save-to-Cell).

## LAYER 1 — SymbolEditorDocument gets the scratch lifecycle
Mirror SchematicDocument's scratch identity on `SymbolEditorDocument`:
- Add `string? FilePath` (private set), `bool IsScratch => FilePath is null`, `bool IsDirty` (private set, with
  title-bullet: `Title = IsDirty ? "• " + _baseTitle : _baseTitle`), `_baseTitle`.
- Constructor: accept optional `string? filePath = null`; start clean (`IsDirty = false`); wire dirty-on-first-
  edit from `ViewModel.UndoRedo.CanUndo` going true (same pattern as SchematicDocument). ALSO mirror the VM's
  own `IsDirty` (the VM sets IsDirty on Execute) — pick ONE source of truth: prefer the document subscribing to
  the VM's `IsDirty` PropertyChanged (the VM already has `[ObservableProperty] _isDirty`), so the tab bullet and
  the VM dirty stay in lock-step. (Don't double-track; the VM is the source, the document reflects it.)
- Add `internal void Materialize(string filePath)` → sets FilePath, clears IsDirty (and clears the VM's
  IsDirty + sets `ViewModel.CurrentSymbolPath`).
**Gate:** open a symbol editor, make an edit → tab shows "• title"; (no save path yet) — visual only. Report.

## LAYER 2 — Scratch-symbol tracking in WorkspaceViewModel
Mirror scratch-schematic tracking for symbols.
- Add a scratch list for symbols. SIMPLEST: `private readonly List<SymbolEditorDocument> _scratchSymbols = [];`
  (parallel to `_scratchDocs`). (Generalizing `_scratchDocs` to a common interface is cleaner long-term but
  higher-risk; for v1 a parallel list is fine and localized.)
- `NewScratchSymbol()` (mirror `NewScratchSchematic`): make an empty editable symbol
  (`new EditableSymbol { UserEditable = true }`), a `SymbolEditorViewModel`, a `SymbolEditorDocument` with a
  scratch title (`NextScratchSymbolTitle()` → "Untitled-Symbol-N"), wire `vm.SymbolSaved += OnSymbolSaved`,
  add to `_scratchSymbols`, `_factory.OpenDocument(doc)`.
- `NextScratchSymbolTitle()` mirrors `NextScratchSchematicTitle()` over `_scratchSymbols` + open symbol docs.
- `OnDockableClosed`: also remove from `_scratchSymbols` (mirror the `_scratchDocs.Remove` line).
**Gate:** call NewScratchSymbol (temporarily from a menu/test) → a blank symbol editor tab opens, dirty-tracked,
not in the tree. Report.

## LAYER 3 — Launch action: NewSymbol creates a scratch symbol
Replace the NewSymbol fallback in `ExecuteLaunchActionAsync`:
```
case LaunchAction.NewSymbol:
    _factory.RemoveWelcomeStub();
    NewScratchSymbol();
    break;
```
**Gate (macOS):** set On-launch = New Symbol, relaunch → a blank scratch symbol editor opens, no Welcome tab,
dirty-trackable. Report.

## LAYER 4 — Save a scratch symbol: "Save to Cell…" or "Save as File" (orphan)
When the user saves a SCRATCH symbol (⌘S with the symbol tab active, or the editor's Save button), branch like
`SaveLooseSchematic`:
- If `CurrentSymbolPath` is already set (materialized) → `PerformSave` to that path (already works).
- If scratch → show the two-option offer dialog (mirror `SaveLooseNoWorkspace`'s SaveChangesDialog):
  - **"Save to Cell…"** → pick or create a cell, then write the .csym into that cell's `symbol/` subfolder:
    1. If a workspace is open: prompt for a cell name (InputNameDialog, like `NewCellInWorkspaceAsync`); create
       the cell folder via `CellFolder.CreateCellFolder(workspaceDir, name)` if it doesn't exist (or let the
       user target an existing cell — v1 can be "type a new cell name under the workspace root").
       Compute `symbolDir = CellFolder.SubFolderPath(cellDir, ViewType.Symbol)`, then
       `filePath = Path.Combine(symbolDir, symbolName + ".csym")`. Write via the VM's PerformSave path (or
       `SymbolPersistence.SaveToFile`), then `doc.Materialize(filePath)`, move the doc out of `_scratchSymbols`,
       `_openDocsByPath[filePath] = doc`, refresh the tree.
    2. If NO workspace is open: tell the user a cell needs a workspace; offer to create a workspace first
       (reuse the New Workspace flow) OR fall back to "Save as File". Keep this branch SIMPLE — if no workspace,
       you may just route to "Save as File" with a note. (Mirror how SaveLooseNoWorkspace handles the
       no-workspace case; don't rebuild the full SavePlan.)
  - **"Save as File"** → the EXISTING orphan path: `SaveSymbolAsAsync(owner)` → file picker → `PerformSave` →
    then `doc.Materialize(result path)` and move out of `_scratchSymbols`. (Orphan .csym, no cell, no tree
    registration — mirrors `SaveLoosePlainFile`.)
  - **Cancel** → no-op.
- Wire the symbol tab into ⌘S: `SaveAllDocuments`'s SingleDoc branch currently only handles
  `SchematicDocument`. Extend it so when the active dockable is a `SymbolEditorDocument`, it routes to this
  symbol-save flow (scratch → offer dialog; materialized → PerformSave).
**Gate:** new scratch symbol → draw something → ⌘S → offer dialog → "Save to Cell…" creates the cell +
symbol/<name>.csym and the tab de-dirties (bullet gone); a second scratch symbol → ⌘S → "Save as File" writes
an orphan .csym; Cancel leaves it dirty. Tree shows the cell-saved symbol. Report.

## LAYER 5 — Close-prompt + Save-All + recovery parity for scratch symbols
Extend the dirty-work plumbing so scratch symbols aren't silently lost:
- `HasAnyDirtyWork()`: also check `_scratchSymbols.Any(d => d.IsDirty)` and open materialized symbol docs that
  are dirty.
- `ConfirmCloseDockable`: currently only prompts for dirty `SchematicDocument`. Add a branch for a dirty
  `SymbolEditorDocument` → SaveChangesDialog → on Save route to the Layer-4 symbol-save flow (return false if
  the save is cancelled so the close cancels too).
- `SaveAllDocuments` (AllDocs scope) + `PromptSaveBeforeClose`: include dirty scratch symbols (route each
  through the Layer-4 save). Keep it straightforward — for AllDocs, a dirty scratch symbol can go through the
  same offer dialog per doc, or (simpler v1) just the orphan "Save as File" picker per symbol; pick one and
  state it.
- Recovery (autosave) parity for scratch symbols is OPTIONAL for v1 — if cheap, mirror `AutoSaveAll`/
  `CheckForRecovery` for symbols; otherwise note it as deferred (scratch symbols are lost on crash in v1, same
  caveat schematics had before recovery). State which you did.
**Gate:** create a dirty scratch symbol, try to close the tab → Save/Don't Save/Cancel prompt; ⌘S with the tree
active (AllDocs) saves dirty symbols too; quitting with a dirty scratch symbol prompts. Report; state the
recovery decision.

## Acceptance
New-Symbol-on-launch opens a scratch symbol (no message); scratch symbols are dirty-tracked (tab bullet); saving
a scratch symbol offers "Save to Cell…" (creates cell + symbol/<name>.csym) or "Save as File" (orphan .csym);
close/quit/Save-All prompt for dirty scratch symbols. Mirrors the scratch-schematic lifecycle. Firewall green;
build/test green; no regression to scratch schematics, the existing cell-based New Symbol, or symbol save/undo.

## Guardrails
- Reuse the existing symbol-save machinery (CurrentSymbolPath/PerformSave/SaveSymbolAsAsync/SymbolSaved) — don't
  duplicate it. The VM is the source of truth for IsDirty; the document reflects it.
- Mirror the scratch-SCHEMATIC patterns (NewScratchSchematic, SaveLooseNoWorkspace, ConfirmCloseDockable) rather
  than inventing new flows.
- Use the SIMPLE two-option offer dialog for the save target — do NOT rebuild the full SavePlan/cell-wizard for
  symbols (deferred to v2; note it in docs).
- "Save to Cell" writes into the cell's symbol/ subfolder via CellFolder.SubFolderPath(..., ViewType.Symbol);
  orphan "Save as File" writes a bare .csym with no tree registration.
- Don't regress the launch-action macOS fix or the existing cell-context New Symbol.
- Sub-gate; report+STOP between layers.
- Update docs/design/scratch-and-save-lifecycle.md (scratch symbols section: lifecycle + Save-to-Cell/orphan;
  note full cell-wizard parity deferred to v2) and src/Ui/CLAUDE.md.

*Exit: New Symbol on launch creates a scratch symbol the user can save into a cell or as an orphan .csym,
dirty-tracked with close/quit prompts — mirroring the scratch-schematic lifecycle.*
