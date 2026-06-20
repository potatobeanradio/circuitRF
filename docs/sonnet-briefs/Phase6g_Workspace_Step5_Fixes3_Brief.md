# Phase 6g — Step 5 fix: New Workspace uses a folder picker (Claude Code / Sonnet)

Replace the `.cws` **file** picker in New Workspace with a **folder picker**, so the user navigates to (and can
create) the workspace folder in one native dialog — the picked folder **is** the workspace folder, its leaf
name **is** the workspace name, and `.cws` is written inside it. This removes the `.cws`-extension validation /
file-overwrite-prompt mismatch (the OS picker was checking for a file `name.cws` while we actually create a
*folder* `name`, so it nagged about the wrong thing and could miss a real folder collision). One layer. Report
when done. Firewall green; don't regress Open/Save.

> Context code: `src/Ui/ViewModels/WorkspaceViewModel.cs` (`NewWorkspace(Window?)` — currently uses
> `window.StorageProvider.SaveFilePickerAsync(... DefaultExtension="cws", FileTypeChoices=[".cws"] ...)` then
> `workspaceDir = Path.GetDirectoryName(cwsPath)`; `ResolveOwner` helper; the post-create wiring:
> `CurrentWorkspacePath`, `_factory.ProjectTreeTool?.SetActions/SetWorkspace`), `src/Ui/Schematic/
> WorkspacePersistence.cs` (`SaveToFile`, `CwsFile`), `src/Ui/Schematic/NameValidator.cs`,
> `src/Ui/Schematic/CellFolder.cs` (`CcellFileName` = ".ccell" — and the `.cws` is likewise literally ".cws").
> Avalonia API: `IStorageProvider.OpenFolderPickerAsync(FolderPickerOpenOptions)`. Design authority:
> `workspace-and-project-tree.md` §1.1.

## The change — folder picker, picked folder IS the workspace

Rewrite `NewWorkspace` to use **`OpenFolderPickerAsync`** instead of `SaveFilePickerAsync`:
1. `var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title =
   "New Workspace — choose or create the workspace folder", AllowMultiple = false });` — the native folder
   picker lets the user **navigate into an existing empty folder OR create a new folder and select it**, in
   one dialog (this satisfies "pick parent + name in the same picker": creating a new folder names it and
   selects it in one go).
2. If `folders.Count == 0` → cancelled, return.
3. The selected folder is the **workspace folder**: `workspaceDir = folders[0].Path.LocalPath`. The
   **workspace name** is its leaf: `name = Path.GetFileName(workspaceDir.TrimEnd(Path.DirectorySeparatorChar,
   Path.AltDirectorySeparatorChar))`.
4. **Validate the name** via `NameValidator.Validate(name)` (the folder leaf must be cross-platform-safe). If
   invalid → clear Message, return (the user picked/created a folder with an illegal name).
5. **Collision guard (the real one now):** the `.cws` path is `Path.Combine(workspaceDir, ".cws")`. If a
   `.cws` **already exists** in the chosen folder → this folder is **already a workspace**; reject with a
   clear Message ("'<name>' is already a circuitRF workspace — use Open instead.") and return. (Choosing a
   non-empty non-workspace folder is allowed — the user may be making a workspace in an existing folder; only
   an existing `.cws` blocks.)
6. Create the workspace: `Directory.CreateDirectory(workspaceDir)` (no-op if it exists — the picker already
   made/selected it), `WorkspacePersistence.SaveToFile(cwsPath, new CwsFile())`.
7. Post-create wiring (unchanged from today): `SetActiveUndoTarget(null)`, `_openDocsByPath.Clear()`,
   `CurrentWorkspacePath = cwsPath`, reset the Dock layout, re-`SetActions` + `SetWorkspace(workspaceDir)` on
   the (possibly fresh) `ProjectTreeTool`, success Message using `name` (the folder leaf — **not** the file
   stem, which is empty for ".cws").

**Notes:**
- **No `.cws` extension/filter anywhere** — there is no file picker now, so no extension to fight and no
  file-overwrite prompt.
- The workspace **name comes from the folder leaf** everywhere (header, window title, success message) —
  consistent with the prior fix (the file is literally `.cws`, so its stem is empty; never derive the name
  from the file).
- If the chosen folder is **non-empty but has no `.cws`**, allow it (just write `.cws` in); only an existing
  `.cws` is the block.

**Gate:** New Workspace opens a **folder** picker; creating a new folder "MyWorkspace" in it and selecting it
yields `…/MyWorkspace/.cws`, the tree roots at `…/MyWorkspace/`, the header shows "MyWorkspace", and File →
New Cell enables; selecting a folder that already contains a `.cws` is rejected with a clear message; Open and
Save still work. Report.

## Acceptance
1. New Workspace uses a native **folder** picker (navigate + create-folder in one dialog); the picked folder
   is the workspace folder and its leaf name is the workspace name; `.cws` is written inside it; no `.cws`
   file-extension/overwrite prompt.
2. Name validated (`NameValidator`); an existing-`.cws` folder is rejected (already a workspace); non-workspace
   folders (empty or not) are accepted.
3. Post-create state correct: `CurrentWorkspacePath` set, tree rooted at the workspace folder, header = folder
   name, New Cell enabled. Open/Save unregressed.
4. `dotnet build`/`dotnet test` green; firewall green; nothing else regresses.

## Guardrails
- **Folder picker, not file picker** — `OpenFolderPickerAsync`; the folder IS the workspace, its leaf IS the
  name; `.cws` literally `.cws` inside it.
- **Name from the folder leaf**, validated; never from the (empty) `.cws` stem.
- **Collision guard = existing `.cws` in the chosen folder** (already-a-workspace), not a file-name check.
- Don't regress Open/Save or the post-create wiring.
- One layer; report when done.
- Update `src/Ui/CLAUDE.md` if useful (New Workspace = folder picker; name from folder leaf).

*Exit: New Workspace presents one native folder picker where the user creates/chooses the workspace folder
directly — no `.cws`-extension confusion, no risk of the OS overwrite prompt fighting our folder-based
collision check.*
