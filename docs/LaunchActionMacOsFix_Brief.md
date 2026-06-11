# circuitRF — Launch action not applied on macOS (Claude Code / Sonnet)

The VM side is correct (constructor no longer force-opens a scratch schematic; `ExecuteLaunchActionAsync`
handles all six actions). The bug is in **App.axaml.cs startup: on macOS, `ApplyLaunchSettings` is never
called.** Fix the macOS launch path. Verify on macOS (the user's platform). Firewall green; build/test green.

## Root cause (confirmed by reading App.axaml.cs)
`OnFrameworkInitializationCompleted` creates `firstWindow` and sets `desktop.MainWindow = firstWindow`. Then:
- **Windows/Linux (no file args):** `firstWindow.Show(); ApplyLaunchSettings(launchVm);` — works.
- **macOS:** posts `ShowFirstWindowIfNeeded` at Background priority and does NOT call `ApplyLaunchSettings`
  inline.

`ShowFirstWindowIfNeeded` only does anything when NO `WorkspaceWindow` exists:
```
if (_desktop?.Windows.OfType<WorkspaceWindow>().Any() != true) { … create+show a NEW window …; ApplyLaunchSettings(vm); }
```
But `firstWindow` was ALREADY created in `OnFrameworkInitializationCompleted` (and assigned to
`desktop.MainWindow`), so it is already present in `_desktop.Windows`. The guard is therefore **false**, the
method does nothing, and `ApplyLaunchSettings` is **never invoked on macOS**. The window the user sees is
`firstWindow`, shown via the macOS activation path (`OnActivated`), whose VM never had the launch action
applied. → launch setting ignored on macOS.

## The fix — apply launch settings to the window that actually shows on macOS
The cleanest fix: on macOS, show `firstWindow` and apply its launch settings on the SAME window, instead of
deferring to a method that bails because the window already exists.

In `OnFrameworkInitializationCompleted`, the macOS no-file-args branch should mirror the Windows/Linux branch
but via the macOS show path. Concretely:

1. **Replace** the macOS branch
   ```
   else if (OperatingSystem.IsMacOS())
   {
       Avalonia.Threading.Dispatcher.UIThread.Post(
           ShowFirstWindowIfNeeded,
           Avalonia.Threading.DispatcherPriority.Background);
   }
   ```
   with one that shows `firstWindow` and applies launch settings to ITS vm:
   ```
   else if (OperatingSystem.IsMacOS())
   {
       firstWindow.Show();
       var launchVm = (WorkspaceViewModel)firstWindow.DataContext!;
       Avalonia.Threading.Dispatcher.UIThread.Post(
           () => ApplyLaunchSettings(launchVm),
           Avalonia.Threading.DispatcherPriority.Background);
   }
   ```
   (If there's a reason `firstWindow` must NOT be shown immediately on macOS — e.g. waiting for a possible
   Apple-Event file open — see the alternative below. But the user launches by clicking the app icon, NOT via
   a file, so showing `firstWindow` + applying launch settings is correct for the normal case.)

2. **Guard against double-apply / double-show.** Ensure `ApplyLaunchSettings` runs exactly once for the shown
   window. If `OnActivated` (Apple-Event file open) can also fire, gate the launch-action application so file
   activation still takes precedence:
   - Add a `private bool _launchHandled;` field. Set it true the first time launch settings are applied OR a
     file is opened via `OnActivated`. In the macOS branch, only `ApplyLaunchSettings` if `!_launchHandled`
     (and set it). In `OnActivated`'s file path, set `_launchHandled = true` so a file open suppresses the
     launch action (consistent with the "command-line file args override" rule).
   - This preserves the existing intent: a file passed/opened at startup overrides the launch action.

3. **`ShowFirstWindowIfNeeded`** may now be redundant for the normal path. Keep it ONLY if it's still needed
   as an Apple-Event fallback (it's posted nowhere else after this change). If it becomes dead code, remove it;
   if it's still referenced by the activation fallback, leave it but ensure it doesn't double-apply
   (respect `_launchHandled`).

## Verify (ON macOS — the user's platform)
- Set On-launch = **Welcome**, relaunch (click app icon, no file): only the Welcome screen shows.
- Set **NewSchematic**, relaunch: one scratch schematic, NO Welcome tab.
- Set **NewWorkspace**, relaunch: the New Workspace dialog appears (Welcome remains if cancelled).
- Set **OpenWorkspace**, relaunch: the folder picker appears.
- Set **NewSymbol** / **NewDataDisplay**: the documented fallback message appears (or the action if
  implemented).
- Launch by double-clicking a workspace/file in Finder (Apple Event): the file opens and the launch action is
  SKIPPED (`_launchHandled` gate).
- Confirm the launch PANE (Palette/Project Tree) is also applied on macOS now (ApplyLaunchSettings sets both —
  this was also being skipped).

## Guardrails
- The fix is in App.axaml.cs startup wiring only — the VM (`ExecuteLaunchActionAsync`/`ApplyLaunchPane`) is
  correct, don't change it.
- `ApplyLaunchSettings` must run exactly once for the shown window on macOS; a startup file open suppresses the
  launch action (`_launchHandled`).
- Don't regress Windows/Linux (their branch already works — leave it).
- Don't regress the macOS no-window menu state (`BuildBgMenuWindow`) or the Apple-Event file path beyond adding
  the `_launchHandled` gate.
- Build/test green; firewall green.
- Note in src/Ui/CLAUDE.md: on macOS the first window is shown + launch settings applied inline in
  OnFrameworkInitializationCompleted; `_launchHandled` makes a startup file open take precedence.

*Exit: on macOS, launching the app applies the saved On-launch action and pane (Welcome by default), exactly
as on Windows/Linux; opening a file at startup still overrides the launch action.*
