# Phase 6g — Step 5 fix: custom New Workspace dialog (not a system picker) (Claude Code / Sonnet)

Replace the system folder picker in New Workspace with a **custom modal dialog** (mirroring `InputNameDialog`)
so the user creates a **Workspace** — not a "folder" — and **always** gets a fresh workspace folder. The
system folder picker is demoted to picking only the **parent location**; the workspace folder itself is
**created by us**, named in our dialog, with a hard "must not already exist" guard. This removes the bad UX
where picking an existing folder dropped a `.cws` into it. Read `InputNameDialog.axaml(.cs)` first — the
dialog is that pattern plus a parent-location row. Sub-gated; report between layers. Firewall green; don't
regress Open/Save.

> Context code: `src/Ui/Views/Dialogs/InputNameDialog.axaml(.cs)` (the **exact pattern to mirror**: a custom
> `Window`, `ShowDialog<T?>` returning the value or null, live `NameValidator` feedback, OK/Cancel + Enter/Esc),
> `src/Ui/ViewModels/WorkspaceViewModel.cs` (`NewWorkspace(Window?)` — currently `OpenFolderPickerAsync`;
> `ResolveOwner`; post-create wiring: `CurrentWorkspacePath`, `_factory.ProjectTreeTool?.SetActions/
> SetWorkspace`, success Message), `src/Ui/Schematic/NameValidator.cs`, `src/Ui/Schematic/WorkspacePersistence.cs`
> (`SaveToFile`, `CwsFile`). Avalonia: `IStorageProvider.OpenFolderPickerAsync` is used **only** for the
> parent-location "Choose…" button inside the dialog. Design authority: `workspace-and-project-tree.md` §1.1.

## The principle
- **The user creates a "Workspace," not a "folder."** All language in the dialog says Workspace. We create
  the workspace folder ourselves — the user never navigates a folder picker to *create* it.
- **A New Workspace is ALWAYS a new folder.** The target folder must **not already exist** — reject if it
  does (no dropping `.cws` into an existing folder, no overwrite).
- **The system folder picker picks only the PARENT location** (an existing folder is fine there — that's just
  "where should my new workspace live"). The workspace folder = `parent/<name>/`, created by us.
- **Scope fence:** just the New Workspace dialog + the create logic. No other work.

---

## LAYER 1 — the `NewWorkspaceDialog` (custom modal, mirrors InputNameDialog)

Create `src/Ui/Views/Dialogs/NewWorkspaceDialog.axaml(.cs)`, modeled on `InputNameDialog`:
- **Title:** "New Workspace".
- **Fields:**
  - **Workspace name** — a `TextBox` (the workspace/folder name), live-validated via `NameValidator` (show the
    reason inline, like InputNameDialog's `ValidationMessage`).
  - **Location** — a read-only `TextBox`/label showing the chosen **parent directory**, plus a **"Choose…"**
    button that opens `OpenFolderPickerAsync` (title e.g. "Choose where to create the workspace") to set the
    parent. Default the parent to a sensible location (e.g. the user's Documents, or the last-used dir if
    easily available — else Documents).
  - **(Optional, nice) a live preview line:** "Will create: `<parent>/<name>/`" so the user sees the real
    target.
- **Result:** `ShowDialog<NewWorkspaceResult?>` returning `{ ParentDir, Name }` (or a small record), or
  **null** on cancel — exactly InputNameDialog's return-or-null contract.
- **OK enabled only when** a parent is chosen AND the name passes `NameValidator` AND `parent/<name>/` does
  **not** already exist (show "A workspace folder named '<name>' already exists here." inline when it does).
  Enter commits, Esc cancels (mirror InputNameDialog's key handling).

**Layer 1 gate:** the dialog opens, "Choose…" sets the parent via the folder picker, the name validates live,
OK is gated on valid+non-colliding, and it returns `{parent, name}` or null. Report.

---

## LAYER 2 — `NewWorkspace` uses the dialog + creates the folder

Rewrite `NewWorkspace` to use the dialog instead of the folder picker:
1. `var owner = ResolveOwner(ownerParam); if (owner is null) return;`
2. `var result = await new NewWorkspaceDialog(defaultParentDir).ShowDialog<NewWorkspaceResult?>(owner);`
   `if (result is null) return;` (cancelled).
3. Compute `workspaceDir = Path.Combine(result.ParentDir, result.Name)`. **Re-check the guard** (defense in
   depth, in case of a race): if `Directory.Exists(workspaceDir)` → Message + return.
4. `Directory.CreateDirectory(workspaceDir)`; write the workspace file `Path.Combine(workspaceDir, ".cws")`
   via `WorkspacePersistence.SaveToFile(cwsPath, new CwsFile())`.
5. Post-create wiring (unchanged): `SetActiveUndoTarget(null)`, `_openDocsByPath.Clear()`,
   `CurrentWorkspacePath = cwsPath`, reset Dock layout, re-`SetActions` + `SetWorkspace(workspaceDir)` on the
   tree, success Message using `result.Name` (the folder leaf — **not** the empty `.cws` stem).
6. The workspace **name is the folder leaf** everywhere (header, window title, message) — consistent with the
   prior fixes.

**Layer 2 gate:** File → New Workspace opens the custom dialog (not a system folder picker); choosing a parent
+ typing "MyWorkspace" creates `…/MyWorkspace/.cws`, roots the tree there, header shows "MyWorkspace", New Cell
enables; an existing target folder is rejected (dialog won't enable OK, and the create-time re-check guards
the race); Open/Save still work; the system picker is only ever used for the *parent* and never drops a `.cws`
into a pre-existing chosen folder. Report.

## Acceptance
1. New Workspace presents a **custom "New Workspace" dialog** (Workspace-named fields), not a raw system
   folder picker; the system folder picker is used only behind the "Choose…" button to pick the **parent**.
2. The workspace folder `parent/<name>/` is **created by us** and must **not pre-exist** (gated in the dialog
   + re-checked at create); `.cws` written inside it; name validated via `NameValidator`.
3. Post-create state correct (CurrentWorkspacePath, tree rooted at the workspace folder, header = folder name,
   New Cell enabled); name derived from the folder leaf; Open/Save unregressed.
4. `dotnet build`/`dotnet test` green; firewall green; nothing else regresses.

## Guardrails
- **Custom dialog, Workspace language** — the user creates a Workspace; we create the folder. No "pick an
  existing folder and we'll drop a `.cws` in it" path.
- **Always a new folder** — `parent/<name>/` must not exist (gate OK + re-check at create); never overwrite.
- **System folder picker = parent only**, behind "Choose…"; an existing parent is fine.
- **Mirror `InputNameDialog`** — same return-or-null `ShowDialog` contract, same live-validation style; don't
  invent a new dialog pattern.
- **Name from the folder leaf**, validated; never from the (empty) `.cws` stem.
- Don't regress Open/Save or the post-create wiring.
- Sub-gate the two layers; report and stop between each.
- Update `src/Ui/CLAUDE.md` if useful (New Workspace = custom dialog; folder picker only for parent).

*Exit: New Workspace is a custom dialog where the user names a Workspace and chooses where it lives; circuitRF
creates the (guaranteed-new) workspace folder and its `.cws` — no system-picker confusion, no risk of polluting
an existing folder.*
