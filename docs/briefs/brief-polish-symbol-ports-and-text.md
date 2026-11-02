# Brief: polish-symbol-ports-and-text (B17) — Ports indicator refresh + text backspace tofu

Two independent Symbol Editor fixes.

Size: **S**. Files: `src/Ui/ViewModels/SymbolEditorViewModel.cs`,
`src/Ui/ViewModels/WorkspaceViewModel.cs`.

---

## Fix A — Ports indicator refreshes from the `.ccell` on tab activation

**Symptom.** A symbol editor tab shows "Ports: N" from the owning cell's `.ccell` `NumPorts`, read once
when the tab opens. If the user changes the cell's port count in the cell parameter editor while the
symbol tab is already open, the symbol tab keeps the stale count (and its unmapped-port warnings are
stale too).

**Root cause.** `SymbolEditorViewModel.PortsLabel`/`ExternalPortCount` read
`EditableSymbol.ExternalPortCount`, which `WorkspaceViewModel.OpenOrActivateSymbol` sets exactly once at
open via `TryCellPortCount`. Nothing re-reads it later. (`EditableSymbol.ExternalPortCount` is a plain
`int?`, not serialized — it's cell authority, not symbol data — so updating it is side-effect-free and
must not dirty the doc.)

**Fix.** Re-read the `.ccell` `NumPorts` and push it into the VM whenever the symbol tab becomes active.

### 1. `SymbolEditorViewModel` — add a setter that notifies

Add near the `ExternalPortCount` / `PortsLabel` members:

```csharp
    /// <summary>
    /// Updates the cell-declared external port count — e.g. after the owning cell's .ccell NumPorts
    /// changed in the cell editor while this tab was inactive. Refreshes the Ports label and the
    /// unmapped-port overlay. No-op when unchanged. Does NOT dirty the document (ExternalPortCount is
    /// cell authority, not symbol data, and is not serialized).
    /// </summary>
    public void SetExternalPortCount(int? count)
    {
        if (EditableSymbol.ExternalPortCount == count) return;
        EditableSymbol.ExternalPortCount = count;
        OnPropertyChanged(nameof(ExternalPortCount));
        OnPropertyChanged(nameof(PortsLabel));
        RebuildOverlay();   // unmapped-port warnings depend on ExternalPortCount
    }
```

### 2. `WorkspaceViewModel` — drop the unused param and refresh on activation

`TryCellPortCount`'s `Symbol symbol` parameter is unused. Simplify it:

```csharp
    private static int? TryCellPortCount(string csymPath)   // was: (string csymPath, Symbol symbol)
    {
        var symbolDir = Path.GetDirectoryName(csymPath);
        if (symbolDir is null) return null;
        var cellDir   = Path.GetDirectoryName(symbolDir);
        if (cellDir is null) return null;
        var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        if (!File.Exists(ccellPath)) return null;
        try   { return CellPersistence.LoadFromFile(ccellPath).NumPorts; }
        catch { return null; }
    }
```

Update its one existing call in `OpenOrActivateSymbol`:

```csharp
            editable.ExternalPortCount = TryCellPortCount(absolutePath);   // was: (absolutePath, symbol)
```

In `OnDocumentDockPropertyChanged`, the active-document branch already does:

```csharp
        if (activeDockable is SymbolEditorDocument symDoc)
        {
            _factory.PropertiesTool?.SetActiveSymbolEditor(symDoc.ViewModel);
        }
```

Add the refresh inside that block, after `SetActiveSymbolEditor`:

```csharp
            // Ports indicator may be stale if the owning cell's .ccell NumPorts changed in the cell
            // editor while this tab was inactive — re-read it on activation.
            if (symDoc.ViewModel.CurrentSymbolPath is { } sp)
                symDoc.ViewModel.SetExternalPortCount(TryCellPortCount(sp));
```

Orphan symbols (no `.ccell`) → `TryCellPortCount` returns null → label falls back to pin count; correct.

(Known limitation, acceptable: a torn-off `SymbolEditorWindow` isn't in the main DocumentDock, so this
activation hook doesn't fire for it. The docked-tab case — the normal one — is covered.)

---

## Fix B — typing a text primitive: backspace/delete inserts a rectangle (tofu) glyph

**Symptom.** While typing a Text primitive, pressing Backspace (or Delete) leaves a □ rectangle glyph in
the text instead of deleting cleanly.

**Root cause.** Deletion already works: `OnKeyDown` handles `Key.Back` while `_isTypingText`
(`_textBuffer = _textBuffer[..^1]`). But the canvas doesn't mark the key handled, so the platform also
raises a `TextInput` event whose text is a control char (U+0008 / U+007F on macOS). `OnTextInput` appends
it unconditionally, and the font has no glyph → tofu. So Backspace both deletes the last char *and*
appends a control char.

**Fix.** Strip control characters in `OnTextInput` (deletion stays in `OnKeyDown`). Replace the method:

```csharp
    public void OnTextInput(string text)
    {
        if (!_isTypingText || string.IsNullOrEmpty(text)) return;
        // Backspace/Delete/Enter/Tab arrive here as control chars on some platforms and would render
        // as tofu (□). Deletion is handled in OnKeyDown (Key.Back); keep only printable text here.
        string printable = new string(text.Where(c => !char.IsControl(c)).ToArray());
        if (printable.Length == 0) return;
        _textBuffer += printable;
        RebuildOverlay();
    }
```

(`System.Linq` is already available in this file via the project's implicit usings — `.Where`/`.Select`
are used throughout.)

---

## Verification (manual)

**Fix A**
1. Open a cell's symbol (e.g. a 2-port cell) → tab shows "Ports: 2".
2. Open that cell's parameter editor, change NumPorts to 3.
3. Click back to the symbol tab → it now shows "Ports: 3" and the unmapped-port warning reflects the
   new count. Lower it back to 2 and refocus → "Ports: 2".
4. An orphan `.csym` (not under a cell) still shows "Ports: <pin count>" and refreshes on activation
   without error.

**Fix B**
1. Text tool → click → type "Hello" → Backspace twice → buffer reads "Hel", no □ glyphs.
2. Type more, hold Backspace to clear → empties cleanly, never shows a rectangle. Enter commits, Escape
   cancels (unchanged).

## Acceptance

- Symbol-tab Ports label (and unmapped-port overlay) reflect the current `.ccell` NumPorts after the tab
  regains focus; no document dirtying from the refresh.
- Backspace/Delete while typing a text primitive deletes characters with no tofu glyph.
