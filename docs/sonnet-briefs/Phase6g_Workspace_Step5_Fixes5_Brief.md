# Phase 6g — Step 5 fix: New Workspace HIG polish + tracked location + Recent Workspaces + Open as folder (Claude Code / Sonnet)

A cluster of related workspace-open polish: **(1)** fix the `NewWorkspaceDialog` HIG issues (button label
centering); **(2)** prepopulate the Workspace name with the next available `Untitled-Workspace-N` for the
current Location; **(3)** track the last-used Location (in-memory, updated by New + Open) and default the
dialog to it; **(4)** add a **Recent Workspaces** feature (persisted, last 10, File → Open Recent submenu +
Clear Recent); **(5)** change File → **Open** to **"Open Workspace…"** using a **folder** picker (not file),
and update Recent after any open. Sub-gated; report between layers. Firewall green; don't regress create/open.

> Context code: `src/Ui/Views/Dialogs/NewWorkspaceDialog.axaml(.cs)` (the dialog to polish + prepopulate),
> `src/Ui/Views/Dialogs/InputNameDialog.axaml` (HIG reference — buttons `Width="80"`, centered content),
> `src/Ui/ViewModels/WorkspaceViewModel.cs` (`NewWorkspace`, `OpenWorkspace` — change to folder picker;
> `CurrentWorkspacePath`; `ResolveOwner`), `src/Ui/Theming/AppPreferences.cs` (`AppPreferences` +
> `AppPreferencesIo.Load/Save` — **the persistence home**; already persists `preferences.json` between
> launches and is loaded at startup), `src/Ui/Schematic/NameValidator.cs`, `src/Ui/Schematic/
> WorkspacePersistence.cs`, `src/Ui/Views/WorkspaceWindow.axaml` (File menu — NativeMenu + in-window Menu; the
> "Open…" item to rename + the new "Open Recent" submenu). Design authority: `workspace-and-project-tree.md`
> §1.1.

## The spine
- **Tracked location is in-memory only** (does NOT persist between launches); **Recent Workspaces persists**
  (last 10, in `AppPreferences`).
- **Open Workspace = folder picker** (the workspace IS a folder); selecting a folder that contains a `.cws`
  opens it; a folder without a `.cws` is rejected (not a workspace).
- **Recent updates after every successful open OR create.**
- **Scope fence:** these five items. No step-6/7 work.

---

## LAYER 1 — NewWorkspaceDialog HIG polish + tracked location + name prepopulation

1. **Button HIG:** fix Cancel/OK so the **label text is centered** within the button (mirror `InputNameDialog`'s
   button style — `Width="80"`, default content centering; remove whatever caused left/odd alignment).
   Ensure consistent spacing/padding and that OK is `IsDefault`, Cancel is `IsCancel`/Esc.
2. **Tracked location (in-memory):** add a workspace-location field on `WorkspaceViewModel` (e.g.
   `string _lastWorkspaceParentDir`), initialized to the current sensible default (Documents). The
   `NewWorkspaceDialog` defaults its **Location** to this. **Not persisted** between launches.
3. **Name prepopulation:** when the dialog appears, prefill the **Workspace name** with the next available
   **`Untitled-Workspace-N`** where N is the lowest positive integer such that
   `<Location>/Untitled-Workspace-N` does **not** exist (scan the current Location). Recompute the suggestion
   when the user changes the Location via "Choose…" (so the default name stays collision-free for the chosen
   parent). Use a small helper (it mirrors `SchematicEditModel.NextAvailableName`'s "lowest free integer"
   logic, but over folder existence at the Location).
4. The name remains user-editable + live-validated (`NameValidator`), OK still gated on valid + non-colliding
   (existing behavior).

**Layer 1 gate:** the dialog opens with centered button labels, Location defaulted to the tracked dir, and the
name prefilled to the next free `Untitled-Workspace-N` for that Location; changing Location recomputes the
suggestion; OK still gated. Report.

---

## LAYER 2 — tracked location updates on New + Open; Open becomes a folder picker

1. **New Workspace:** after a successful create, set `_lastWorkspaceParentDir` to the **parent** of the new
   workspace folder (so the next New defaults there).
2. **Open Workspace = folder picker.** Rename the command/menu to **"Open Workspace…"** and change
   `OpenWorkspace` from `OpenFilePickerAsync` to **`OpenFolderPickerAsync`** (title "Open Workspace"):
   - The selected folder is the workspace folder; the workspace file is `Path.Combine(folder, ".cws")`.
   - If that `.cws` **exists** → open it (load color scheme as today, set `CurrentWorkspacePath`, tree roots
     there). If it does **not** exist → reject with a clear Message ("That folder is not a circuitRF
     workspace (no .cws found).").
   - After a successful open, set `_lastWorkspaceParentDir` to the **parent** of the opened workspace folder.
3. Update the File menu text in **both** the macOS NativeMenu and the in-window Menu: "Open…" → "Open
   Workspace…".

**Layer 2 gate:** Open Workspace presents a folder picker; choosing a workspace folder opens it; choosing a
non-workspace folder is rejected with a clear message; after New or Open, the tracked location is the parent
of that workspace (verified by the next New Workspace dialog defaulting there). Report.

---

## LAYER 3 — Recent Workspaces (persisted) + File → Open Recent submenu

1. **Persistence:** add `List<string> RecentWorkspaces` to `AppPreferences` (the `.cws` paths, most-recent
   first), serialized in `preferences.json` (it already persists between launches and loads at startup).
   Provide `AppPreferences` load/save use in `WorkspaceViewModel` (load on construct; save on change).
2. **Update rule:** after any successful **open or create**, push that workspace's `.cws` path to the front
   of the list, de-duplicate (case-insensitive), **cap at 10**, and save. (Store the `.cws` path; the
   workspace name for display is the **parent folder leaf**.)
3. **Menu:** add **File → Open Recent** as a submenu (both NativeMenu + in-window Menu), populated from
   `RecentWorkspaces`:
   - One item per recent workspace, labeled with the **workspace name** (folder leaf), opening that workspace
     on click (reuse the Open path: validate the `.cws` still exists; if a recent entry's `.cws` is **missing**,
     either grey it or show a Message and offer to remove it — pick the simpler: remove-on-failed-open with a
     Message).
   - A **separator**, then **"Clear Recent"** which empties the list and saves.
   - When the list is empty, Open Recent is empty/disabled.
   - The submenu must **rebuild when the list changes** (after open/create/clear) — bind it to an observable
     collection the VM exposes, or rebuild the menu items on change.

**Layer 3 gate:** opening/creating workspaces adds them to Open Recent (most-recent first, max 10, no dups);
clicking a recent entry opens it; a recent entry whose `.cws` is gone is handled gracefully (removed + Message);
Clear Recent empties it; the list **persists across app restart**; the submenu updates live. Report.

## Acceptance
1. `NewWorkspaceDialog`: centered button labels (HIG), Location defaulted to the tracked dir, name prefilled
   to the next free `Untitled-Workspace-N` for that Location (recomputed on Location change).
2. Tracked location (in-memory, non-persistent) updates to the parent of the workspace after New **and** Open,
   and seeds the next New Workspace dialog.
3. Open Workspace uses a **folder** picker, opens a folder containing `.cws`, rejects one without; menu reads
   "Open Workspace…".
4. Recent Workspaces: persisted (last 10, MRU, de-duped), File → Open Recent submenu of names + separator +
   Clear Recent; updated after every open/create; missing entries handled; persists across restart.
5. `dotnet build`/`dotnet test` green; firewall green; nothing else regresses.

## Guardrails
- **Tracked location = in-memory only** (never persisted); **Recent = persisted** in `AppPreferences`.
- **Open Workspace = folder picker**; a workspace folder must contain `.cws` (reject otherwise); name from the
  folder leaf (the `.cws` stem is empty).
- **Recent: MRU, de-dup (case-insensitive), cap 10, save on change, rebuild menu on change**; handle a recent
  entry whose `.cws` no longer exists.
- **Mirror `InputNameDialog`** button styling for the HIG fix; don't restyle unrelated dialogs.
- Don't regress New Workspace create, the folder-collision guard, or the tree/header wiring.
- Sub-gate the three layers; report and stop between each.
- Update `src/Ui/CLAUDE.md` (tracked location in-memory; Recent Workspaces in AppPreferences; Open = folder
  picker).

*Exit: New Workspace opens with HIG-correct buttons and a smart default name at a remembered location; Open
Workspace is a folder picker; and a persisted Recent Workspaces list (File → Open Recent + Clear Recent) tracks
the last 10, updated on every open and create.*
