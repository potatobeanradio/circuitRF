# Sonnet Brief — Quit does nothing after a previously-cancelled quit (macOS, all OSes)

**Bug:** Selecting Quit (File menu or ⌘Q) sometimes does nothing — the app neither quits nor shows a prompt.

**Root cause (confirmed).** `App.Quit()` (`src/Ui/App.axaml.cs`) guards re-entry with a `_isShuttingDown` latch
that is **never reset when a quit is aborted**:
```csharp
internal void Quit()
{
    if (_isShuttingDown) return;     // ← stays true forever once a quit is cancelled
    _isShuttingDown = true;
    _bgMenuWindow?.Hide();
    if (_desktop is null) { Environment.Exit(0); return; }
    var windows = _desktop.Windows.OfType<WorkspaceWindow>().ToList();
    if (windows.Count == 0) { Environment.Exit(0); return; }
    foreach (var w in windows) w.Close();          // → WorkspaceWindow.OnClosing
}
```
`WorkspaceWindow.OnClosing` (`src/Ui/Views/WorkspaceWindow.axaml.cs`) cancels the close and prompts:
```csharp
e.Cancel = true;
bool proceed = await _vm.PromptSaveBeforeClose(this, "closing");
if (proceed) { _vm.OnCleanExit(); _closingConfirmed = true; Close(); }
// proceed == false (user hit Cancel, or cancelled the save-as/save-plan dialog):
//   window stays open, but App._isShuttingDown is still true.
```
So after any cancelled quit (Cancel in the save prompt, or cancelling the save-as/SavePlan dialog), `_isShuttingDown`
remains `true`. Every later `Quit()` returns immediately at the guard → "nothing happens." Both the `QuitApplication`
command and the macOS `ShutdownRequested → Quit()` path are affected. The clean-quit path (no dirty work) still
works, which is why it's intermittent ("sometimes").

`Quit()` can't observe the cancel synchronously — `w.Close()` returns before `OnClosing`'s `await` resolves — so
the window must tell `App` when it aborts.

## Fix
**`App.axaml.cs`** — add a method to release the latch:
```csharp
/// <summary>Called by a WorkspaceWindow when a close/quit prompt is cancelled, so a later Quit works.</summary>
internal void AbortQuit() => _isShuttingDown = false;
```

**`WorkspaceWindow.axaml.cs`** — in `OnClosing`, when the prompt does not proceed, clear the latch:
```csharp
e.Cancel = true; // block the close until the prompt resolves

bool proceed = await _vm.PromptSaveBeforeClose(this, "closing");
if (proceed)
{
    _vm.OnCleanExit();
    _closingConfirmed = true;
    Close();
}
else
{
    // User cancelled (or cancelled the save dialog): the window stays open.
    // Release any in-progress app quit so a subsequent Quit isn't silently swallowed.
    (App.Current as App)?.AbortQuit();
}
```
This is safe for a plain window-close cancel too (Cmd+W / red button with no quit in progress): `_isShuttingDown`
was already `false`, so `AbortQuit()` is a harmless no-op there.

**Do not** touch `_bgMenuWindow` in `AbortQuit` — on abort a WorkspaceWindow is still open, so the background
menu window correctly stays hidden (its visibility is managed by `NotifyWindowCountChanged`, which behaves
normally again once `_isShuttingDown` is cleared).

## Why this is the whole fix
- Successful quit (Save or Don't Save → `proceed == true`): `_closingConfirmed = true; Close()` → `OnClosing`
  early-returns → window closes → `OnClosed` → `NotifyWindowCountChanged` sees `_isShuttingDown && !anyOpen` →
  `Environment.Exit(0)`. Unchanged.
- Clean quit (no dirty work): `OnClosing` returns without cancelling → window closes → same exit path. Unchanged.
- Cancelled quit: now releases the latch → next Quit works. Fixed.

## Tests
This is hard to unit-test (window lifecycle + async OnClosing). Add a tiny seam test if practical:
**`AbortQuit_ResetsShuttingDown`** — call a test hook that sets `_isShuttingDown` (or call `Quit()` with a
stubbed desktop), then `AbortQuit()`, and assert a subsequent `Quit()` proceeds past the guard. If exposing that
state is too invasive for the test project, rely on the manual gate below and skip the unit test.

## Gate (manual, macOS)
1. Make a dirty edit. File → Quit → in the save prompt click **Cancel**. The app stays open (expected).
2. File → Quit **again** → the save prompt appears again (previously: nothing happened). Choosing Save or Don't
   Save now quits.
3. Repeat with ⌘Q instead of the menu — same behavior.
4. Clean state (nothing dirty) → Quit exits immediately. Cancelling the save-as/SavePlan dialog (scratch doc)
   then re-quitting also works.

## On completion
Note in `src/Ui/CLAUDE.md`: `App._isShuttingDown` is released via `App.AbortQuit()` from `WorkspaceWindow.OnClosing`
whenever a close/quit prompt is cancelled, so a cancelled quit no longer wedges all future quits.
