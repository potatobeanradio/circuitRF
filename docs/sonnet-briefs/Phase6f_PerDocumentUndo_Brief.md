# Phase 6f — Per-document Undo stacks + Symbol Editor menu handles (fix) (Claude Code / Sonnet)

Two related fixes: (1) make **Undo/Redo per-document** so undoing in an active symbol editor cannot undo a
schematic edit (and vice versa, and two schematics don't cross-undo); (2) add the **menu/toolbar handles** to
open the Symbol Editor (the commands exist but nothing invokes them). Do this **now** while only the schematic
+ one symbol editor exist — it is far cheaper than after the data display and multi-document editing land.
Sub-gated; **report and stop between every layer.** Firewall green; existing undo behavior within a single
document must not regress.

> Read first: `docs/design/symbol-editor.md` §5 (editor hosting), `src/Ui/CLAUDE.md`. Context code:
> `src/Ui/ViewModels/WorkspaceViewModel.cs` (the **single shared `UndoRedo` stack** — the bug; the `Undo`/
> `Redo` `[RelayCommand]`s bound to it; `OpenSymbolEditorDocked`/`OpenSymbolEditorWindow` — the unwired
> commands; `OnDocumentDockPropertyChanged` — the existing **active-document** hook, already used for the
> Properties panel), `src/Ui/ViewModels/Dock/CircuitRfDockFactory.cs` (`DocumentDock.ActiveDockable`,
> `OpenDocument`), `src/Ui/ViewModels/SchematicViewModel.cs` (ctor takes `UndoRedoStack`),
> `src/Ui/ViewModels/SymbolEditorViewModel.cs` (ctor takes `UndoRedoStack`), `src/Ui/Schematic/
> SchematicDocument.cs` + `SymbolEditorDocument.cs` (the per-tab document wrappers), `src/Ui/Commands/
> UndoRedoStack.cs` (`CanUndo`/`CanRedo`/`PropertyChanged`), `src/Ui/Views/WorkspaceWindow.axaml` (the menu —
> where the Symbol Editor handles go). Design docs win on any conflict.

## The core principle (do not violate)
**Undo/Redo is per-document, not global.** Each editable document (schematic, symbol, and later data display)
owns its **own** `UndoRedoStack`. The workspace's Undo/Redo command acts on the **active document's** stack —
never a shared one. An undo while a symbol editor is active touches only that symbol's stack; switching tabs
switches which stack Undo targets. Two schematics in two tabs have two independent stacks.

> Why now: today `WorkspaceViewModel` owns ONE `UndoRedo` and passes it to *every* `SchematicViewModel` and
> `SymbolEditorViewModel`, so all documents share one history (undo in a symbol reverts a schematic edit).
> Fixing this with two document types is small; after the data display + multi-doc editing it is a rewrite.

---

## LAYER 1 — each document owns its stack

1. **Stop sharing the workspace stack.** Give each editable document VM its **own** `UndoRedoStack`:
   - `SchematicViewModel`: create its own `UndoRedoStack` (either internally, or the document wrapper creates
     one and passes it in — pick the cleaner; the VM must end up with a per-instance stack, not the workspace
     one). Update the call site in `WorkspaceViewModel.OpenTreeItem` (`new SchematicViewModel(editModel, …)`)
     so it no longer passes the workspace `UndoRedo`.
   - `SymbolEditorViewModel`: same — its own stack; update `OpenSymbolEditorDocked`/`OpenSymbolEditorWindow`
     so they no longer pass the workspace `UndoRedo`.
2. **Expose the stack on the document** so the workspace can find the active one: each editable document VM
   (or its `Document` wrapper — `SchematicDocument`, `SymbolEditorDocument`) exposes its `UndoRedoStack` via a
   small interface, e.g. `interface IUndoableDocument { UndoRedoStack UndoRedo { get; } }`. Implement it on
   both document wrappers (and it's the contract the future data-display document will implement too).
3. **Remove the workspace-owned `UndoRedo`** as the thing commands act on (it can stay as a field only if
   something still needs it; prefer removing it entirely — the workspace no longer owns undo history).

**Layer 1 gate:** schematic and symbol VMs each have an independent `UndoRedoStack`; both documents expose it
via `IUndoableDocument`; no document is constructed with a shared stack. Builds. Report.

---

## LAYER 2 — route Undo/Redo to the ACTIVE document

1. **Track the active undoable document.** Extend the existing `OnDocumentDockPropertyChanged` hook (it
   already fires on `ActiveDockable` change and feeds the Properties panel): when the active dockable is an
   `IUndoableDocument`, record it as the **current undo target**; otherwise the target is null (Undo/Redo
   disabled). (Tear-off Symbol Editor windows: see Layer 3 — a focused tear-off window is the active target
   while focused.)
2. **Point the workspace Undo/Redo commands at the active target's stack:**
   - `Undo()` → `_activeUndoTarget?.UndoRedo.Undo()`; `CanUndo()` → `_activeUndoTarget?.UndoRedo.CanUndo ?? false`.
     Same for Redo.
   - **Re-subscribe `PropertyChanged`** on stack swap: when the active target changes, unsubscribe the old
     stack's `PropertyChanged` and subscribe the new one, then call `UndoCommand.NotifyCanExecuteChanged()` /
     `RedoCommand.NotifyCanExecuteChanged()` (the existing pattern — but now the *subscription target* moves
     with the active document, instead of being the one fixed stack).
3. **Reset on workspace new/open:** `NewWorkspace` etc. clear the active target (no global `UndoRedo.Reset()`
   to lean on anymore — per-document stacks die with their documents).

**Layer 2 gate:** with a schematic and a symbol editor both open as tabs: edit each, switch tabs, Undo — Undo
reverts only the **active** tab's last edit; the other tab's history is untouched. Undo/Redo enable/disable
reflects the **active** document's stack depth. Two schematics in two tabs: independent. Report (the
cross-undo test explicitly).

---

## LAYER 3 — tear-off Symbol Editor window owns its undo

A torn-off `SymbolEditorWindow` is a separate window; when it is focused it is the active undo target, and its
Undo/Redo (keyboard + any in-window buttons) act on **its** document's stack — never the main workspace's
active tab.
- The tear-off window already holds a `SymbolEditorDocument` (with its own stack from Layer 1). Ensure the
  window routes its **own** Undo/Redo (keyboard shortcuts and the 4a toolbar Undo/Redo buttons) to that
  document's stack directly — the tear-off window does not depend on the workspace's active-tab tracking.
- The 4a in-editor Undo/Redo buttons (docked or tear-off) bind to **that editor's** stack `CanUndo`/`CanRedo`.

**Layer 3 gate:** open a symbol editor as a tear-off window; edit it; Undo in the tear-off reverts only its
edits; the main workspace's schematic Undo is unaffected and vice versa. Report.

---

## LAYER 4 — add the Symbol Editor menu/toolbar handles

The `OpenSymbolEditorDocked`/`OpenSymbolEditorWindow` commands exist but nothing invokes them — add the UI
entry points so the editor is reachable.
- Add menu items (e.g. under a suitable menu in `WorkspaceWindow.axaml` — "Window" or "Tools", or "File →
  New" group) bound to the two commands: **"Open Symbol Editor"** (docked) and **"Open Symbol Editor
  (Window)"** (tear-off). Place them sensibly; match the existing menu style.
- (These open on a built-in symbol for now — real cell-driven opening is a later step; this is just the
  reachable handle the owner is missing.)

**Layer 4 gate:** the Symbol Editor is openable from the menu both docked and as a tear-off window; both open
and render the symbol; the per-document undo from Layers 1–3 holds. Report.

---

## Acceptance
1. Each editable document (schematic, symbol) owns an **independent** `UndoRedoStack` via `IUndoableDocument`;
   no shared workspace stack drives document undo.
2. Workspace Undo/Redo acts on the **active** document's stack; switching tabs switches the target; Undo/Redo
   enable-state reflects the active document. **Cross-document undo is impossible** — undoing in a symbol
   never reverts a schematic, two schematics don't cross-undo (verified).
3. A tear-off Symbol Editor window routes Undo/Redo to its own stack, independent of the workspace.
4. The Symbol Editor is reachable from the menu (docked + tear-off).
5. `dotnet build`/`dotnet test` green; firewall green; **single-document undo behavior unchanged** (the
   schematic's own undo/redo still works exactly as before — same commands, same notify-both-directions);
   nothing else regresses.

## Guardrails
- **Per-document stacks, routed to the active document** — never a global stack driving multiple documents.
  `IUndoableDocument` is the contract the data display will also implement later.
- **Don't regress single-document undo** — the schematic's own undo/redo (and the recent notify-both-
  directions fix) must behave identically; this changes *which* stack Undo targets, not how a stack works.
- **Reuse the existing active-dockable hook** (`OnDocumentDockPropertyChanged`) — don't invent a parallel
  active-tracking mechanism; it already feeds the Properties panel, now it also feeds undo routing.
- Sub-gate the four layers; report and stop between each; don't run the full suite into the output limit.
- Update `symbol-editor.md` (note per-document undo) and `src/Ui/CLAUDE.md` (the per-document-undo rule +
  `IUndoableDocument` contract + active-document routing) so future documents (data display) follow it.

*Exit: Undo/Redo is per-document and targets the active document's own stack — symbol, schematic, and future
data-display histories are fully independent — and the Symbol Editor is reachable from the menu, docked or
torn off.*
