# Sonnet Brief — Schematic hierarchy save data-loss bug (HIGH PRIORITY)

**Bug (data loss).** Open a workspace with a schematic tab → Push In one level into a sub-cell → edit →
click the Save toolbar button → Messages says "Saved" → close & reopen workspace → **the edit is gone.**

**Root cause (confirmed).** The toolbar Save (`SchematicView.axaml.cs` → `OnSaveCsch`) calls
`IHierarchyHost.SaveSchematicDocumentAsync(doc)` → `WorkspaceViewModel.SaveSingleDocument(doc, owner)`. For a
materialized doc that method does:
```csharp
SchematicPersistence.SaveToFile(doc.FilePath, doc.ViewModel.EditModel, doc.Id);
```
It writes **`doc.ViewModel.EditModel`** — the *base* frame — to `doc.FilePath`. But after Push In, the user's
edit went into **`doc.ActiveViewModel.EditModel`**, a *different* shared registry session backed by a
*different* `.csch` (the sub-cell). The base session is unchanged, so Save writes the (unchanged) base, reports
"Saved", and never persists the edited sub-cell session. On reopen the sub-cell `.csch` still has the old
content → the edit is lost.

The pushed-in session isn't rescued elsewhere either: the orphaned-dirty-session sweep runs only in
`SaveAllDocuments`'s AllDocs branch and in `PromptSaveBeforeClose`, **not** in the single-doc toolbar path.

**Relevant facts (already verified):**
- `SchematicDocument.ViewModel` = base session (registered at `doc.FilePath`); `ActiveViewModel` = top nav
  frame; `NavFrames` = `IReadOnlyList<(SchematicViewModel Session, string Label)>` (index 0 = base).
- Pushed-in sub-cell sessions are shared registry sessions created via `GetOrCreateSession(path)` in
  `PushIntoCell`; each has its own `.csch` path retrievable via `_registry.TryGetPath(vm, out path)`
  (already used in `PopOutOf`).
- `SchematicViewModel.UndoRedo.IsModified` is the per-session dirty flag; `NotifySessionSaved(absPath)`
  (calls `_registry.MarkSaved` + `UpdateCellDirtyForSession`) clears it and refreshes the tree indicator.
- `doc.IsDirty` follows the ACTIVE frame, so while pushed-in-and-edited it is true → `SaveSchematicDocumentAsync`'s
  `if (!doc.IsDirty) return;` guard passes correctly; the bug is purely the wrong write target.

## Fix — `WorkspaceViewModel.SaveSingleDocument` (the materialized branch)
After writing the base session, **also flush every dirty session currently in the document's nav stack** to its
own registry path. Concretely, in the `doc.FilePath is not null` branch, keep the base write, then add:

```csharp
// Persist every dirty pushed-in sub-cell session in this doc's nav stack to its own .csch.
// (Hierarchy edits live in the active frame's shared session, NOT doc.ViewModel.EditModel.)
foreach (var (session, _) in doc.NavFrames)
{
    if (ReferenceEquals(session, doc.ViewModel)) continue;   // base handled above
    if (!session.UndoRedo.IsModified) continue;              // clean frame — skip
    if (!_registry.TryGetPath(session, out var subPath) || subPath is null) continue;
    try
    {
        var subCellName = Path.GetFileNameWithoutExtension(subPath);
        SchematicPersistence.SaveToFile(subPath, session.EditModel, subCellName);
        NotifySessionSaved(subPath);                         // clears dirty + refreshes tree dot
        Messages.Success("Saved", subPath);
    }
    catch (Exception ex)
    {
        Messages.Error($"Failed to save '{subPath}': {ex.Message}");
    }
}
```

Keep the existing base write + `doc.Materialize(doc.FilePath)` + `NotifySessionSaved(doc.FilePath)` as they are
(the base write is a harmless no-op when the base is clean). Because `SaveSingleDocument` is the single funnel for
both single-doc entry points (`SaveAllDocuments` SingleDoc branch **and** `SaveSchematicDocumentAsync`/toolbar),
fixing it here covers the toolbar Save and ⌘S-single.

**Scope notes / non-goals.** This persists dirty frames *currently in the nav stack*. A sub-session you've
already popped out of becomes an orphaned dirty session — that case is already handled by the Save-All and
close-prompt sweeps; do not duplicate that here. Don't touch the scratch branch (push-in from a scratch parent
isn't a supported flow). Don't change `SaveAllDocuments` or `PromptSaveBeforeClose`.

## Tests (`tests/Ui.Tests`, mirror existing hierarchy/save tests)
1. **`PushedIn_Edit_SingleSave_PersistsSubCell`** (the reported bug): build a 2-cell hierarchy on disk (parent
   with a cell-instance → child `.csch`); open parent as a `SchematicDocument`; `PushIntoCell`; make an undoable
   edit on `doc.ActiveViewModel`; call the single-doc save path; reload the **child** `.csch` from disk and assert
   the edit is present. Assert the child session's `UndoRedo.IsModified` is false afterward.
2. **`BaseEdit_SingleSave_Unchanged`** (regression): edit at base (no push-in), save, reload base `.csch`, assert
   edit present — unchanged behavior.
3. **`CleanFrames_NotRewritten`**: with a dirty base but clean pushed-in frame, assert only the base is written
   (no spurious child write) — guards the `IsModified` skip.

## Gate
The reported scenario round-trips: push in, edit, Save, close, reopen → edit present. Build 0W/0E; existing
save/hierarchy tests stay green.

## On completion
Note in `src/Ui/CLAUDE.md`: single-document Save persists the base session **and** every dirty session in the
document's nav stack (hierarchy edits live in pushed-in shared sessions, not `doc.ViewModel.EditModel`);
popped-out dirty sessions are still covered by the Save-All / close-prompt orphaned-session sweep.
