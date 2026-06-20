# Phase 6f — Floating/tear-off window Undo routing (verify + fix) (Claude Code / Sonnet)

The per-document-undo fix landed and works **for tabs in the main window's document dock**, but undo in
**floating / tear-off windows is unhandled**. Three cases: (a) the bespoke Symbol Editor tear-off window
(`SymbolEditorWindow`) is a bare shell with no Undo/Redo of its own; (b) a schematic (or any document) **floated
out of the dock by drag** — AvaloniaUI Dock can reparent a tab into its own window — may no longer be the main
dock's `ActiveDockable`, so the main-window Undo could target the wrong document or nothing; (c) the
**Parameter Editor** (embedded inspector AND its separate-window/dialog form) edits a schematic's parameters
through a `SchematicViewModel` reference — its edits must land on **that schematic's** stack, never another
schematic's, even when shown as a separate window while a different schematic is active. Verify and fix all
three so **every window's Undo acts on the document that window is showing.** Sub-gated; **report and stop
between layers.** Firewall green; do not regress the working main-window tab routing.

> Note on the Parameter Editor (case c): it must **NOT** get its own undo stack. It is an *inspector* that
> commits through `_schematicVm.Execute(...)` onto its target component's **owning schematic's** stack — that
> is exactly what keeps its edits in the right history. Giving it an independent stack would *create* a
> cross-document hazard. The fix here is to **verify the editor's `_schematicVm` stays bound to the target
> component's owning schematic** (not the merely-active one), especially as a separate window.

> Read first: `src/Ui/CLAUDE.md` (per-document-undo rule + `IUndoableDocument`). Context code:
> `src/Ui/ViewModels/WorkspaceViewModel.cs` (the **current** routing — `OnDocumentDockPropertyChanged` keys
> off `_factory.DocumentDock.ActiveDockable`; `SetActiveUndoTarget`; the `Undo`/`Redo` `[RelayCommand]`s),
> `src/Ui/Views/SymbolEditorWindow.axaml(.cs)` (the bare tear-off shell — **no Undo command/menu/keyboard**),
> `src/Ui/Schematic/SymbolEditorDocument.cs` + `SchematicDocument.cs` (both implement `IUndoableDocument`,
> exposing `UndoRedo`), `src/Ui/Commands/UndoRedoStack.cs`, `src/Ui/ViewModels/Dock/CircuitRfDockFactory.cs`
> (Dock floating behavior; `DocumentDock`), `src/Ui/Views/WorkspaceWindow.axaml(.cs)` (the main menu + how its
> Undo/Redo bind to `WorkspaceViewModel`); `src/Ui/ViewModels/ParameterEditorViewModel.cs` +
> `ParameterRowViewModel.cs` (commit via `_schematicVm.Execute(...)` — the `SchematicViewModel` reference
> that must stay bound to the target component's owning schematic), `src/Ui/Views/Dialogs/
> ParameterEditorDialog.axaml(.cs)` + `src/Ui/Views/Properties/PropertiesView.axaml(.cs)` (the two host
> contexts — embedded inspector and separate window/dialog). Design docs / `CLAUDE.md` win on any conflict.

## The principle (do not violate)
**Every window's Undo/Redo acts on the document that window is showing — its own `IUndoableDocument` stack.**
The main window's Undo targets its active dock tab (already works). A tear-off / floating window's Undo targets
**that window's** document, independent of the main window. No window can undo into a document it isn't showing.

> Current gap: undo routing exists only for `_factory.DocumentDock.ActiveDockable` (main-window tabs). The
> Symbol Editor tear-off (`window.Show()`, outside the dock) has no undo path at all; a Dock-floated tab's
> undo target is unverified.

---

## LAYER 1 — verify the two cases (instrument first, don't fix yet)

Before changing anything, establish the actual behavior so the fix targets the real gap (not a guessed one):
1. **Symbol Editor tear-off:** open it (the "Open Symbol Editor (Window)" handle), make an edit, press Ctrl+Z
   **with the tear-off window focused**. Report what happens (likely nothing — the window has no Undo binding).
   Also check: does the *main* window's Undo menu, clicked while the tear-off is focused, do anything to the
   tear-off? To a main-window tab?
2. **Dock-floated schematic:** open a schematic tab, then **drag it out of the dock into its own floating
   window** (Dock's built-in float). Make an edit in the floated schematic, and separately have a different
   document active in the main dock. Press Undo from the floated window and from the main window. Report which
   document each undoes — i.e. does `_factory.DocumentDock.ActiveDockable` still point at the floated doc, or
   at the remaining main-dock tab?

**Layer 1 gate:** a written report of the observed behavior for both cases (what undo does, and which stack it
hits). This determines the exact fix in Layers 2–3. Report and stop.

---

## LAYER 2 — tear-off / floating windows carry their own Undo

Make each floating window self-sufficient for undo (the §-Layer-3 intent of the prior brief, now actually
built):
1. **A small per-window undo host.** The window's DataContext is an `IUndoableDocument` (the
   `SymbolEditorDocument` / `SchematicDocument`); give the floating window its **own** Undo/Redo commands
   (a tiny window-level VM or commands on the window) that act on **`Document.UndoRedo`** directly — not on
   `WorkspaceViewModel`. Bind their `CanExecute` to that document's stack `CanUndo`/`CanRedo` (subscribe
   `PropertyChanged`, the established pattern).
2. **Keyboard + menu in the window:** wire **Ctrl+Z / Ctrl+Shift+Z (Cmd on macOS)** in the floating window to
   those commands, and (if the window has a menu/toolbar) Undo/Redo items too. The 4a in-editor toolbar
   Undo/Redo buttons (if present in the tear-off) bind here.
3. The floating window's undo is **fully independent** of the main window's active-tab routing — it never
   reads `WorkspaceViewModel`'s `_activeUndoTarget`.

**Layer 2 gate:** with a Symbol Editor torn off: edit it, Ctrl+Z in that window reverts only its edits; the
main window's schematic is untouched; and the main window's own Undo (on its active tab) is unaffected by the
tear-off. Report.

---

## LAYER 3 — handle the Dock-floated tab (per Layer 1 findings)

Based on Layer 1's report of how a Dock-floated schematic behaves:
- **If Dock floating keeps the document discoverable as the active dockable of *some* dock** (the floated
  window has its own dock host), route the main-window Undo to **the focused window's** active document, not
  hard-coded to `_factory.DocumentDock`. I.e. resolve the undo target from the **currently focused window's**
  document dock / focused dockable, so a focused floated window's Undo targets its own content.
- **If a floated document is simply lost to the main `DocumentDock.ActiveDockable` tracking,** apply the
  Layer-2 approach to floated windows too: the float host window carries its own Undo acting on the focused
  document's stack.
- **Whichever:** the rule is "Undo follows the focused window's shown document." Don't leave a path where the
  main-window Undo silently targets a hidden/other tab while a floated window is focused.

Keep the working main-window tab routing intact (when the main window is focused, its active tab is the
target).

**Layer 3 gate:** float a schematic out, edit it, and verify Undo (from that floated window, and from the main
window when it's focused) targets the correct document in every focus combination; no cross-document undo.
Report the focus matrix tested.

---

## LAYER 4 — Parameter Editor commits to its target's owning schematic (verify, do NOT add a stack)

The Parameter Editor edits parameters through `_schematicVm.Execute(...)`, so its edits already land on the
target schematic's per-document stack — **provided `_schematicVm` is the owning schematic, not the active
one.** This layer **verifies** that and fixes only if it drifts. **Do NOT give the Parameter Editor its own
undo stack** — that would route parameter edits outside the schematic's history and *create* the cross-schematic
bug. The correct design is: parameter edits are schematic edits, undone via the schematic's own undo.

1. **Embedded inspector (Properties region):** confirm that when it tracks the active schematic and the user
   switches to a different schematic, the editor rebinds `_schematicVm` to the **newly active** schematic
   (and clears/retargets its rows). It should never hold component A (from schematic 1) while committing
   through schematic 2's VM. (It binds to selection in the active schematic, so this is likely already
   correct — verify the rebind on active-document change.)
2. **Separate-window / dialog form:** this is the real risk. A Parameter Editor dialog/window opened on a
   component of schematic 1 must keep committing to **schematic 1's** `SchematicViewModel` (and thus
   schematic 1's stack) for its whole lifetime — even if the user switches the main window to schematic 2.
   Verify the dialog captures and holds its **target schematic's** VM at open time and does not re-resolve to
   the active schematic. Edits in that dialog must be undoable via schematic 1 (its owning schematic), and
   must not appear in schematic 2's history.
3. **Undo reachability:** confirm a parameter edit made in the dialog is undoable — via schematic 1's undo
   (its tab/window). If the dialog is a separate focused window with no undo affordance and schematic 1 isn't
   the focused target, ensure undo of that edit is still reachable (either the dialog exposes undo on its
   owning schematic's stack, or focus returns to schematic 1). Per Layer 1, instrument and report what
   actually happens before deciding the minimal fix.

**Layer 4 gate:** open a Parameter Editor (dialog) on a component of schematic 1; switch the main window to
schematic 2; edit a parameter in the dialog → the edit lands on schematic 1, is undoable via schematic 1, and
does **not** touch schematic 2. The embedded inspector rebinds correctly on active-schematic change. The
Parameter Editor has **no** independent stack. Report.

---

## Acceptance
1. Symbol Editor tear-off window: its Undo/Redo (keyboard + any in-window button) acts on its own document's
   stack; independent of the main window.
2. A Dock-floated document: Undo follows the focused window's shown document; the main-window Undo targets its
   own active tab when it is focused. No window undoes into a document it isn't showing.
3. The Parameter Editor (embedded + separate window) commits to its **target component's owning schematic**
   stack and is undone via that schematic; it has **no** independent stack; a dialog opened on schematic 1
   never writes into schematic 2 even when 2 is active.
4. The previously-working case (multiple tabs in the main window, switch + undo) is **unchanged**.
5. `dotnet build`/`dotnet test` green; firewall green; nothing else regresses.

## Guardrails
- **Every window's Undo acts on the document it shows** — main window → its active tab; floating/tear-off
  window → its own document. Resolve by **focused window**, never a single hard-coded dock.
- **The Parameter Editor has NO stack of its own** — it commits through its target's owning `SchematicViewModel`
  (`_schematicVm.Execute`), so its edits live in that schematic's history. The fix is to keep `_schematicVm`
  bound to the target component's owning schematic (not the active one), especially as a separate window —
  NOT to give it an independent stack.
- **Instrument first (Layer 1)** — report the real behavior of both cases before fixing, so the fix matches
  reality (Dock's float behavior is not assumed).
- **Don't regress main-window tab routing** (the working per-document path) or single-document undo.
- Reuse `IUndoableDocument` + the existing stack-`PropertyChanged` notify pattern; don't invent a parallel
  undo mechanism.
- Sub-gate the three layers; report and stop between each; don't run the full suite into the output limit.
- Update `src/Ui/CLAUDE.md` (undo follows the focused window's shown document; floating windows carry their
  own undo) so the data-display window follows the same rule later.

*Exit: Undo/Redo is correct in every window — the main window targets its active tab, a tear-off or floated
window targets its own document — with no window able to undo into a document it isn't showing.*
