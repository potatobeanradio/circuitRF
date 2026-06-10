# Phase 6g — Workspace Step 6: the cell-parameter editor (Claude Code / Sonnet)

Replace the cell double-click / Edit Parameters **placeholder** (step 4's `StubDocument`) with a real
**cell-parameter editor**: edits the cell's **declared parameter interface** in its `.ccell` —
**add / remove / rename** parameter rows + defaults — undoable, HIG-compliant, no color. It is the *sibling*
of the instance-parameter editor (`ParameterEditorViewModel`) but a **different purpose** (defines the
interface, not instance values). Read `workspace-and-project-tree.md` §7 and `parameter-editor.md` first.
Sub-gated; **report and stop between every layer.** Firewall green; every edit undoable.

> Read first: `docs/design/workspace-and-project-tree.md` §7 (cell-parameter editor — the key UI delta:
> add/remove/rename rows; rename is the consequential edit), `docs/design/parameter-editor.md` (the instance
> editor's HIG/columns/unit-ComboBox conventions to mirror). Context code:
> `src/Ui/ViewModels/ParameterEditorViewModel.cs` + `ParameterRowViewModel.cs` (the instance editor to mirror
> — fixed-list, value editing; the cell editor is editable-list, interface editing), `src/Ui/Schematic/
> CellPersistence.cs` (`CcellFile` + `CcellParameter` — Name/DefaultExpression/Unit/Dimension/ShowOnSchematic
> — what the cell editor reads/writes), `src/Ui/ViewModels/WorkspaceViewModel.cs`
> (`OpenOrActivateCellPlaceholder` — the `StubDocument` to replace; `_openDocsByPath`; per-document undo /
> `IUndoableDocument`), `src/Ui/Schematic/EditableSchematic.cs` (`EditableParameter`, `UnitDimension` — the
> dimension→unit ComboBox source), `src/Ui/Views/Dialogs/ParameterEditorDialog.axaml` + the Properties view
> (host patterns). Design docs win on any conflict.

## The spine
- **Different purpose from the instance editor:** the instance editor edits one instance's **values** (fixed
  row set); the cell editor edits the cell's **interface** in `.ccell` — **add / remove / rename** parameter
  rows + their defaults. The add/remove/rename capability is the whole delta.
- **Undoable as a cell document.** The cell editor is an editable document with its **own** `UndoRedoStack`
  (`IUndoableDocument`, per the per-document-undo rule); add/remove/rename/default-edit are commands that
  notify in both Execute and Undo and write the `.ccell`.
- **HIG + no color** (§7 / `parameter-editor.md`): header (cell name) · scrollable editable row list (Name ·
  Default · Unit · Dimension · Show-on-schematic, shared-size columns matching the instance editor) · an "＋
  Add Parameter" affordance + per-row remove · footer. Reuse the dimension→unit ComboBox; no RGB.
- **Rename is consequential (§7):** renaming a parameter changes the interface; surface a warning that
  placed instances referencing the old name fall back to the default / show unset. v1 **allows + surfaces**;
  no auto-migration.
- **Scope fence (step 6):** the cell-parameter editor only. No instance-value migration, no `.cws` work
  (step 7).

---

## LAYER 1 — `.ccell`-backed editable model + commands (framework-free)

1. A cell-editor edit model wrapping a loaded `CcellFile` (its `CcellParameter` list + the cell folder path),
   exposing the mutable parameter list. Loaded from / saved to `.ccell` via `CellPersistence`.
2. **Commands** (notify both directions, write `.ccell` on apply): `AddCellParameterCommand`,
   `RemoveCellParameterCommand`, `RenameCellParameterCommand`, `SetCellParameterDefaultCommand` (+ unit/
   dimension/show edits — one command or a small set, your call). Each mutates the model + persists.
3. A round-trip test: add/rename/remove/default-edit → `.ccell` reflects it; undo reverts both model and file.

**Layer 1 gate:** the model + commands compile, framework-free; a headless test does add/rename/remove/default
with undo, and the `.ccell` round-trips. Report.

---

## LAYER 2 — `CellParameterEditorViewModel` + its document

1. A VM mirroring `ParameterEditorViewModel`'s shape (header, row collection, empty/non-empty) but for the
   **interface**: rows are add/remove/rename-able; each row has Name (**editable**, unlike the instance
   editor), Default expression, Unit (dimension-keyed ComboBox), Dimension, Show-on-schematic. Plus an
   **Add Parameter** command and per-row **Remove**.
2. An editable **document** wrapper implementing `IUndoableDocument` with its **own** `UndoRedoStack` (so cell
   edits undo independently, per the per-document rule). Commands route to this stack.
3. **Rename warning:** when a row's Name changes, surface the consequence (a non-blocking note/inline hint
   that instances using the old name will fall back) — §7. Validate names (`NameValidator` or the param-name
   rules).

**Layer 2 gate:** the VM loads a `.ccell`, lists its params as editable rows, supports add/remove/rename/
default with undo on its own stack; rename shows the consequence note. Report.

---

## LAYER 3 — host it: replace the placeholder; open on cell double-click / Edit Parameters

1. Replace `OpenOrActivateCellPlaceholder` (the `StubDocument`) so double-clicking a **Cell** node — and the
   **Edit Parameters** context item — opens the **cell-parameter editor** on that cell's `.ccell` (dedup by
   the cell's path via `_openDocsByPath`; activate if already open).
2. Host it like other editable documents (a content tab; the dialog form optional) so per-document undo
   routing (the active-document hook) picks up its stack — Undo/Redo while it's active hit **its** stack.
3. **Save:** edits persist to `.ccell` (on each command, or on an explicit Save — match the instance editor's
   immediacy; `.ccell` is small). After a save that changes the param set, a tree `Refresh()` isn't required
   (params don't change tree structure) — but do it if cheap/consistent.

**Layer 3 gate:** double-click a cell (or Edit Parameters) opens the cell-parameter editor on its `.ccell`;
add/rename/remove/default edits persist to `.ccell` and are undoable via the active-document Undo; reopening
the cell shows the edited interface; the step-4 placeholder is gone. Report.

## Acceptance (step 6)
1. A cell-parameter editor edits the cell's declared interface in `.ccell` — add / remove / rename rows +
   defaults (Name/Default/Unit/Dimension/Show) — reusing the instance editor's column/unit-ComboBox HIG, no
   color.
2. Edits are undoable on the cell document's **own** stack (`IUndoableDocument`), persisted to `.ccell`;
   rename surfaces its consequence (no auto-migration).
3. Cell double-click + Edit Parameters open it (dedup/activate); the step-4 `StubDocument` placeholder is
   replaced.
4. `dotnet build`/`dotnet test` green; firewall green (model/commands framework-free); **no instance-value
   migration, no `.cws` work** (step 7); nothing else regresses.

## Guardrails
- **Interface, not values** — add/remove/rename rows is the delta vs. the instance editor; mirror its HIG/
  columns/unit ComboBox, no color.
- **Own undo stack** (`IUndoableDocument`); commands notify both directions and write `.ccell`.
- **Rename is surfaced, not auto-migrated** (§7).
- Reuse `CcellFile`/`CellPersistence` and the instance-editor patterns; don't duplicate or fork.
- **Scope fence:** cell-parameter editor only — no instance migration, no `.cws`.
- Sub-gate the three layers; report and stop between each.
- Update `workspace-and-project-tree.md` §8 status (step 6 done) and `src/Ui/CLAUDE.md`.

*Exit: double-clicking a cell opens a real cell-parameter editor that edits the cell's `.ccell` interface
(add/remove/rename/defaults), undoably — replacing the placeholder and leaving only `.cws` refinement (step 7).*
