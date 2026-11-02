# Phase 6g — Step 5 fixes: New Workspace prompt + workspace name in Project Tree header (Claude Code / Sonnet)

Two fixes on the step-5 entry points: **(1)** New Workspace does not actually prompt (so no workspace is
created, so File → New Cell stays permanently disabled) — **instrument first, then fix**; **(2)** the Project
Tree dock header should show the **current workspace name** instead of the static "Project Tree" text (next to
the Refresh button). Sub-gated; report between layers. Firewall green; don't regress the working open/save
pickers.

> Context code: `src/Ui/ViewModels/WorkspaceViewModel.cs` (`NewWorkspace(Window? owner)` — async RelayCommand
> using `SaveFilePickerAsync`, creates the folder + `.cws`, sets `CurrentWorkspacePath`;
> `NewCellInWorkspaceCommand` with `CanExecute = CanNewCellInWorkspace` = `CurrentWorkspacePath is not null`;
> `OnCurrentWorkspacePathChanged` calls `NewCellInWorkspaceCommand.NotifyCanExecuteChanged()` + `tree.
> SetWorkspace`), `src/Ui/Views/WorkspaceWindow.axaml` (all `NewWorkspaceCommand` bindings **already** pass
> `CommandParameter="{Binding $parent[Window]}"` — NativeMenu, in-window Menu, toolbar, keybindings), `src/Ui/
> ViewModels/Dock/ProjectTreeTool.cs` (`Title = "Project Tree"` set once in ctor; `SetWorkspace(rootDir)` /
> `ClearWorkspace()`), the Project Tree view (the header with the Refresh button + the title text).

## The spine
- **Instrument before fixing (1).** The XAML owner-passing is already correct, so the cause is NOT the obvious
  "owner is null from the menu." Find the *real* cause at runtime before changing logic — don't guess-patch.
- **Don't regress** the working Open/Save/SaveAs pickers (they use the same `SaveFilePickerAsync`/owner
  pattern and work — so the picker mechanism itself is fine; the difference is specific to `NewWorkspace`).
- **Scope fence:** just these two fixes. No other step-6/7 work.

---

## LAYER 1 — instrument the New Workspace path (diagnose, do NOT fix yet)

`NewWorkspace(Window? owner)` is reported to not prompt. The owner *is* passed by XAML, and Open/Save (same
pattern) work — so the cause is subtler. Add temporary instrumentation and **report findings**; do not change
behavior yet:
1. Log at the top of `NewWorkspace`: whether it's **entered at all** (does the menu/keybinding actually invoke
   it?), and whether `owner` is null.
2. Log the **result** of `SaveFilePickerAsync` (null vs. a path) and whether the `try` block throws (log the
   exception — a silent `catch` may be swallowing a `Directory.CreateDirectory`/`SaveToFile` failure).
3. Check the **command signature/dispatch:** `NewWorkspace` is an **async `Task` RelayCommand taking a
   `Window?`** — confirm the generated `NewWorkspaceCommand` is an `IAsyncRelayCommand` **with a parameter**,
   and that the NativeMenu (macOS) actually passes the parameter and awaits it. A common failure: an
   `async void`/parameter-mismatch, or the macOS **NativeMenu not surfacing `SaveFilePickerAsync`** the way
   the in-window menu does. **Test both menus** (macOS NativeMenu vs. the in-window Menu) and the toolbar
   button — report which actually open the picker and which don't.
4. Report: is the picker opening and the user cancelling? opening and the create throwing? or never opening
   (command not firing / owner null / wrong overload)?

**Layer 1 gate:** a written report of what actually happens on each invocation path (NativeMenu / in-window
menu / toolbar / Ctrl+N), pinpointing where the chain breaks. **Stop and report — no fix yet.**

---

## LAYER 2 — fix New Workspace per the Layer-1 finding

Apply the minimal fix the diagnosis points to. Likely candidates (confirm against L1, don't apply blind):
- If the **macOS NativeMenu** doesn't drive the async picker correctly → ensure the command is properly
  async-dispatched, or route New Workspace through the same mechanism the working Save/SaveAs use.
- If a **silent `catch`** is swallowing a create failure → surface the error (Message) and fix the cause.
- If **owner resolution** fails specifically here → use the same owner the working pickers use (e.g.
  `GetMainWindow()` as a fallback when the parameter is null, so it works from keybinding too).
- Whatever the cause: after New Workspace succeeds, **`CurrentWorkspacePath` is set**, which must fire
  `OnCurrentWorkspacePathChanged` →`NewCellInWorkspaceCommand.NotifyCanExecuteChanged()` (so File → New Cell
  **enables**) and `tree.SetWorkspace`.

**Layer 2 gate:** New Workspace prompts for name/location (all invocation paths that should work), creates the
folder + `.cws`, loads it, and **File → New Cell becomes enabled**; then New Cell creates a cell that appears
in the tree. The Open/Save pickers still work. Report.

---

## LAYER 3 — workspace name in the Project Tree dock header

Replace the static "Project Tree" / "Project" text next to the Refresh button with the **current workspace
name**:
1. In `ProjectTreeTool`, set `Title` to the **workspace name** (the root folder name) in `SetWorkspace(rootDir)`
   (e.g. `Title = Path.GetFileName(rootDir.TrimEnd(separators))`), and reset it to a sensible default
   ("Project Tree" or "No Workspace") in `ClearWorkspace()`. Raise `OnPropertyChanged(nameof(Title))` if Dock's
   `Tool.Title` doesn't notify automatically (verify — if Dock's title binding isn't observing changes, expose
   a separate observable `HeaderText` property the view binds to instead).
2. If the dock **tab/header** text comes from Dock's own `Title` binding, that may suffice; if the **in-view
   header row** (the one with the Refresh button) shows its own label, bind that label to the workspace name
   (the tool's `Title`/`HeaderText`).
3. Tooltip/overflow: long names should elide, not stretch the panel.

**Layer 3 gate:** opening/creating a workspace shows its name next to the Refresh button; closing/clearing
resets to the default; switching workspaces updates it. Report.

---

## Acceptance
1. New Workspace prompts (name/location), creates the workspace, loads it; **File → New Cell enables** and
   creates a cell that appears in the tree (the chicken-and-egg is fully resolved); Open/Save unregressed.
2. The Project Tree header shows the current workspace name (next to Refresh), resetting on clear.
3. `dotnet build`/`dotnet test` green; firewall green; nothing else regresses.

## Guardrails
- **Instrument first (L1), fix per the finding (L2)** — the XAML owner-passing is already correct, so don't
  re-patch that; find the real cause.
- **Don't regress Open/Save** (same picker pattern, currently working).
- **Header reflects the workspace** — set on `SetWorkspace`, reset on `ClearWorkspace`; ensure the binding
  observes the change.
- Sub-gate the three layers; report and stop between each.
- Update `src/Ui/CLAUDE.md` if the fix reveals a NativeMenu/async-command gotcha worth recording.

*Exit: New Workspace actually creates a workspace (unblocking New Cell), and the Project Tree header names the
current workspace — the owner can create a workspace and add cells end to end.*
