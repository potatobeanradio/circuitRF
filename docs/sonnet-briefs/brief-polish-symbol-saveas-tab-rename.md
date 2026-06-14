# Brief: polish-symbol-saveas-tab-rename (B16) — Symbol Editor "Save As" renames the tab

**Goal.** After "Save As" (or any path change) in the Symbol Editor, the document tab title should
update to the new file name. Today it keeps the name it was opened/created with.

Size: **S**. File: `src/Ui/Schematic/SymbolEditorDocument.cs` only.

## Context — the other two B16 items are already done (no change)

Verified in `WorkspaceViewModel.cs`; do **not** add code for these:

1. **Tree open errors on failure** — `OpenOrActivateSchematic`, `OpenOrActivateSymbol`, and
   `OpenOrActivateCellPlaceholder` already wrap their bodies in `try/catch` and post
   `Messages.Error($"Failed to open …: {ex.Message}")`. `OpenOrActivateCellPlaceholder` also errors
   when the `.ccell` is missing. The only silent paths are intentional deferred no-ops (`.clay`
   view files, folder/data-display/colour-theme nodes) — not failures.
2. **Orphaned `.csym` openable** — `OpenOrActivateSymbol` opens *any* `.csym` as editable; when the
   file isn't under a cell, `TryCellPortCount` returns null so `ExternalPortCount` is null (orphan
   mode, port count = pin count). A `.csym` `ViewFile` tree node routes here via `OpenNode`.

(If the owner has a specific symptom for either — e.g. a particular node that opens silently or an orphan
that won't open — capture it separately; nothing in the current code path drops those on the floor.)

## Root cause (the real bug)

`SymbolEditorDocument` builds its tab title from `_baseTitle`, which is `readonly` and set once in the
constructor. The constructor subscribes to the VM's `PropertyChanged` for `IsDirty` only:

```csharp
ViewModel.PropertyChanged += (_, e) =>
{
    if (e.PropertyName is nameof(SymbolEditorViewModel.IsDirty))
        IsDirty = ViewModel.IsDirty;
};
```

`Materialize(path)` and every Save-As path (`SaveScratchSymbolAsFile`, and the materialized toolbar
"Save As…" → `VM.SaveSymbolAsCommand`) set `ViewModel.CurrentSymbolPath` to the new path, but nothing
feeds that back into `_baseTitle`/`Title`. So the tab keeps its old name.

`CurrentSymbolPath` is an `[ObservableProperty]` on the VM (verified), so it raises `PropertyChanged` —
the document can react to it.

## Fix

In `src/Ui/Schematic/SymbolEditorDocument.cs`:

1. Make `_baseTitle` settable:

```csharp
private string _baseTitle;        // was: private readonly string _baseTitle;
```

2. Extend the constructor's `PropertyChanged` subscription to also follow `CurrentSymbolPath`:

```csharp
ViewModel.PropertyChanged += (_, e) =>
{
    if (e.PropertyName is nameof(SymbolEditorViewModel.IsDirty))
        IsDirty = ViewModel.IsDirty;
    else if (e.PropertyName is nameof(SymbolEditorViewModel.CurrentSymbolPath))
        SyncTitleToPath();
};
```

3. Add the helper (place near `Materialize` / the title logic):

```csharp
// Keeps the tab title in lock-step with the on-disk file name after a Save As or materialize.
// CurrentSymbolPath is the single source of truth for the file name; preserve the • dirty prefix.
private void SyncTitleToPath()
{
    if (ViewModel.CurrentSymbolPath is not { } path) return;
    _baseTitle = System.IO.Path.GetFileName(path);
    Title = _isDirty ? $"• {_baseTitle}" : _baseTitle;
}
```

That's it. The existing `IsDirty` setter already rebuilds `Title` from `_baseTitle`, so the two stay
consistent regardless of the order in which `IsDirty` and `CurrentSymbolPath` fire during a save.

### Notes / scope boundaries

- **No initial double-fire:** every creation site sets `CurrentSymbolPath` on the VM *before*
  constructing the `SymbolEditorDocument` (which is where the subscription is wired), and the ctor sets
  the correct initial `Title`. `SyncTitleToPath` only runs on *later* path changes (Save As / materialize).
- **`Id` is intentionally left unchanged.** It's used for scratch-title dedup, Dock identity, and the
  close-prompt text. Renaming the tab (`Title`) is the reported bug; changing `Id` after open risks
  Dock layout-persistence keying and is out of scope. (The close-prompt may show the original name —
  acceptable for now; raise a follow-up if the owner wants `Id` to track too.)
- Don't touch `SymbolEditorViewModel` or any save method — the fix is entirely in the document.

## Verification (manual)

1. Open or create a symbol (e.g. `Untitled-Symbol-1`), edit it, **Save As…** to `foo.csym`
   → the tab reads `foo.csym` (no bullet) immediately.
2. Edit again → tab shows `• foo.csym`; **Save As…** to `bar.csym` → tab shows `bar.csym` (no bullet).
3. Scratch symbol → first ⌘S → "Save as File" → pick `baz.csym` → tab renames to `baz.csym`.
4. Reopen a saved `.csym` from the tree → tab shows its file name (unchanged behaviour).

## Acceptance

- A Symbol Editor tab's title tracks the file name after Save As / first save / materialize.
- Dirty bullet (`•`) is preserved/cleared correctly across the rename.
- No change to open-on-failure error reporting or orphan-symbol opening (already working).
