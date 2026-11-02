# Brief: polish-saveall-cws — Save All always persists the .cws

**Goal.** "Save All" (File menu / ⌘S → `SaveAllDocuments`) must **always** write the workspace `.cws`
when a workspace is open — dirty or not — capturing the set of documents open *at that moment*. If no
documents are open, the `.cws` records that, so reopening the workspace opens no tabs.

Authority: laundry-list "Save All" bug + user decision. Size: **S**.

## Root cause

`WorkspaceViewModel.SaveAllDocuments` writes the `.cws` (`WriteWorkspaceFile`) only on the paths that
actually save a dirty document. When nothing is dirty it hits an early
`Messages.Info("Nothing to save."); return;` **before** any `.cws` write. Repro: close all tabs →
Save All → nothing dirty → early return → `.cws` never updated → its `OpenDocuments` still lists the
old tabs → reopening restores them. `WriteWorkspaceFile` already captures the *current* open docs
from `_factory.DocumentDock.VisibleDockables` (and writes `OpenDocuments = null` when none are open),
so the only fix needed is to **guarantee it runs on every Save All**.

## Fix — guarantee a .cws write on every exit

Wrap the body of `SaveAllDocuments` so the workspace is persisted on every code path, and remove the
now-redundant in-body `WriteWorkspaceFile` calls.

```csharp
[RelayCommand]
private async Task SaveAllDocuments(Window? owner)
{
    var window = ResolveOwner(owner);
    if (window is null) return;

    try
    {
        // … existing body UNCHANGED (SingleDoc scope, symbol SingleDoc scope, AllDocs scope,
        //    scratch plan, materialized writes, orphaned sessions, scratch/materialized symbols) …
        // Leave the "Nothing to save." Messages.Info(...) lines and their `return;`s in place —
        // the finally below still runs on those returns.
    }
    finally
    {
        // Save All always refreshes the .cws (open-doc snapshot + tree state) when a workspace
        // is open — even when nothing was dirty, and even when no documents are open (null list).
        if (CurrentWorkspacePath is not null)
            WriteWorkspaceFile(CurrentWorkspacePath);
    }
}
```

Then **remove** the existing in-body `if (CurrentWorkspacePath is not null) WriteWorkspaceFile(CurrentWorkspacePath);`
calls inside the method (the SingleDoc-schematic path and the end of the AllDocs path) so the `.cws`
isn't written twice per invocation. The `finally` is now the single writer.

Why `finally` (not just appending at the end): the method has several early `return;`s (nothing dirty,
cancelled dialogs). `finally` runs on all of them, so every Save All persists the workspace with one
line and no per-return edits.

### Behavioural details to preserve / verify

- **No workspace open** (`CurrentWorkspacePath is null`): `finally` no-ops — correct (tier-3 / no
  workspace). A scratch save that *creates* a workspace sets `CurrentWorkspacePath` during the body, so
  `finally` then writes the new `.cws` — correct.
- **All tabs closed:** `WriteWorkspaceFile` sets `OpenDocuments = null` (its loop already skips
  dockables without a path / the welcome stub), so reopen shows the welcome stub with no document tabs
  — exactly the requested behaviour.
- **Messages:** `WriteWorkspaceFile` (non-silent) posts its normal `Saved: <.cws path>` message, so
  every Save All logs the workspace write (consistent with the "log every file write" item). The
  "Nothing to save." info message still appears when no documents were dirty; that's fine alongside
  the `.cws` save log.
- A cancelled save dialog mid-flow still triggers the `finally` `.cws` write — harmless (it records the
  current, unchanged open-doc state).

## Acceptance

- Repro from the report: open workspace, close all tabs, **Save All**, open a different workspace from
  Open Recent, reopen the original → **no document tabs** are restored (and no stale tabs).
- Open two schematics, **Save All** (nothing dirty), reopen workspace → those two tabs restore.
- Saving with dirty docs still saves them *and* updates the `.cws` exactly once.
- No double `Saved:` message for the `.cws` per Save All.

## Out of scope

- The toolbar "Save Workspace" button (`SaveWorkspaceCommand`) and "Save Schematic As" — separate items.
