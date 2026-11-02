# Brief — Tools ▸ Examples opens in THIS window when the window has no workspace

**Tag:** `R-ex1-n` · **Series:** stands alone. Written with the 3D editor round 8 briefs (em3d 90–95), from the same owner
report.
**Area:** `src/Ui/ViewModels/WorkspaceViewModel.Examples.cs`, `tests/Ui.Tests/Examples/`, `docs/user/src/new-user-guide/index.md`
**Depends on:** — · **Blocks:** —

## What is wrong

`WorkspaceViewModel.OpenExample` always ends with `App.OpenWorkspaceInNewWindow(result.CwsPath)`. Its doc comment gives
the reason: someone reaching for an example usually has a design open, and replacing it would be unhelpful. That reason
only applies when a workspace **is** open. In a window showing no workspace (the start shell, or after Close Workspace),
the example opens in a second window and leaves the first one empty.

## What to build

### `R-ex1-1` — the rule

After a successful copy:

- **This window has a workspace open** (`CurrentWorkspacePath is not null`): open the copy in a new window, as today.
- **This window has none**: open the copy **in this window**, through the same funnel Open Recent uses
  (`SwitchToWorkspaceReporting(result.CwsPath)`). Do not call `OpenRecentWorkspace` itself. Its missing-path pruning,
  dirty prompt and EM-in-flight confirmation are for replacing a workspace, and here nothing is being replaced.
  - Skip `ConfirmConcurrentOpenAsync`: the copy was created a moment ago, so no other process can have it open.
  - The copy's `.cws` is pushed to Open Recent, as any opened workspace is.

Rewrite the class's doc comment (the "It opens in a NEW WINDOW" paragraph) so it states both halves and the reason for
each. Keep the rest.

### `R-ex1-2` — the macOS menu bar

The native menu's Examples rows (`WorkspaceWindow.axaml.cs`, `_vm.OpenExampleCommand`) run the same command against the
**key window's** view model. Check that "this window" means the window the user chose the menu from. It should not mean
the first window or a stale `_vm`. If the native menu is bound to one view model for the app's lifetime, fix that here,
because the rule above is only right if it reads the right window.

## Gate

- A view-model test with no workspace open: `OpenExample` (folder picker stubbed to a temp directory) leaves
  `CurrentWorkspacePath` equal to the copy's `.cws`, and **no** `App.OpenWorkspaceInNewWindow` call. Inject the new-window
  opener as a seam if there is none; `App` is static.
- The same test with a workspace open: the current workspace is unchanged, and the new-window opener was called once with
  the copy's path.
- Run only `dotnet test tests/Ui.Tests --no-build --filter "FullyQualifiedName~ExampleWorkspaces"`.

## Docs

`docs/user/src/new-user-guide/index.md` (and any other page that says examples "open in a new window"): state the
two-case rule in one sentence. Edit the doc sources only. The owner regenerates `docs/user` at the end of the series.

## On completion

Record findings in `src/Ui/RESOLVED.md` (never CLAUDE.md). Do not commit unless the owner asks.
