# Phase 6g — Step 5 follow-up fixes: workspace folder-wrapping + tree name de-duplication (Claude Code / Sonnet)

Two small fixes from testing: **(1)** New Workspace writes the `.cws` into the *existing parent* folder instead
of creating a workspace folder to contain it — fix so a workspace is always a **named folder containing
`.cws`**; **(2)** the workspace name shows in **three** places (dock panel title, in-view header, tree root) —
de-duplicate per the owner's decision. Sub-gated; report between layers. Firewall green; don't regress
Open/Save.

> Context code: `src/Ui/ViewModels/WorkspaceViewModel.cs` (`NewWorkspace(Window?)` — currently
> `workspaceDir = Path.GetDirectoryName(cwsPath)`, i.e. the existing parent; the `ResolveOwner` helper now
> works), `src/Ui/Schematic/WorkspacePersistence.cs` (`SaveToFile`, `CwsFile`), `src/Ui/Schematic/
> NameValidator.cs`, `src/Ui/ViewModels/Dock/ProjectTreeTool.cs` (`Title`, `SetWorkspace`/`ClearWorkspace`),
> the Project Tree view (header row with Refresh button; the `RootItems` `TreeView`), `src/Ui/Schematic/
> WorkspaceScanner.cs` / `WorkspaceModel.cs` (the root node the tree binds to). Design authority:
> `workspace-and-project-tree.md` §1.1 (workspace = folder; `.cws` literally `.cws` inside it).

## The spine
- **A workspace IS a folder** whose name is the workspace name, with the `.cws` file inside it (§1.1). New
  Workspace must always produce that shape.
- **Don't clobber:** guard against name/folder collisions; reject with a clear message rather than overwrite.
- **One name, one place per role:** panel title = static "Project"; header = workspace name; tree root =
  omitted. Don't show the workspace name three times.
- **Scope fence:** just these two fixes. No other step-6/7 work.

---

## LAYER 1 — New Workspace creates the containing folder

Fix `NewWorkspace` so the chosen name produces a **folder** that contains the `.cws`:
1. From the picker result, take the **stem** (filename without extension) as the **workspace name** — e.g.
   picker returns `/foo/MyWorkspace.cws` → name = `MyWorkspace`, parent = `/foo`. (If the picker returns a
   name with no `.cws`, treat the whole leaf as the name.)
2. **Validate the name** via `NameValidator` (reject invalid with a Message — the folder name must be
   cross-platform-safe, §1.4).
3. Target = `parent/<name>/` (the **workspace folder**); the workspace file = `parent/<name>/.cws`
   (literally `.cws`, no stem — §1.1).
4. **Collision guard:** if `parent/<name>/` already exists (or a file of that name exists), **reject** with a
   clear Message ("A folder named '<name>' already exists here.") — do not overwrite or merge.
5. Create `parent/<name>/`, write `.cws` inside it, then set `CurrentWorkspacePath` to that `.cws` path and
   point the tree at the **workspace folder** (`parent/<name>/`) via `SetWorkspace` (the existing
   `OnCurrentWorkspacePathChanged` path — `Path.GetDirectoryName(cwsPath)` now correctly yields the workspace
   folder, since `.cws` lives inside it).
6. **Consistency check:** `CurrentWorkspacePath`'s directory must be the workspace folder (the named folder),
   so `tree.SetWorkspace(dir)` scans the right root, and the window title / header derive the right name.
   *(Note: with the file literally named `.cws`, `Path.GetFileNameWithoutExtension` yields empty — derive the
   workspace name from the **folder name** `Path.GetFileName(workspaceDir)`, not the file stem, wherever the
   name is shown. Fix the window-title derivation too if it currently uses the file stem.)*

**Layer 1 gate:** New Workspace with name "MyWorkspace" creates `…/MyWorkspace/.cws` (a folder containing the
file); the tree roots at `…/MyWorkspace/`; a second New Workspace with the same name+location is rejected with
a clear message; Open/Save still work. Report.

---

## LAYER 2 — de-duplicate the workspace name in the tree (panel title / header / root)

The workspace name currently can appear in three places. Set them per the owner's decision:
1. **Dock panel title** (`ProjectTreeTool.Title`): static **"Project"** (not the workspace name, not "Project
   Tree"). Set once; do not update it per workspace.
2. **In-view header** (the row next to the Refresh button): the **workspace name** (the workspace folder
   name). Bind this label to a tool property (e.g. an observable `WorkspaceName` / `HeaderText` on
   `ProjectTreeTool`) set in `SetWorkspace(rootDir)` = `Path.GetFileName(rootDir.TrimEnd(dir-separators))`,
   reset to something neutral (e.g. "No workspace") in `ClearWorkspace()`. Ensure the binding observes the
   change (raise `OnPropertyChanged`). Elide long names.
3. **Tree root node OMITTED:** the tree should start at the workspace's **children** (cells / libraries /
   user-folders / known-files), **not** show the `Workspace` root node as the top row (the header already
   names the workspace). Achieve this in the **view-model/binding layer**, not by changing the scan:
   bind the `TreeView`'s `ItemsSource` to the **root node's children** (e.g. `RootItems[0].FilteredChildren`,
   or have the tool expose a `TopLevelItems` = the root's children) rather than to the root node itself. Keep
   the scan/model unchanged (the `Workspace` root node still exists in the model; it's just not rendered as a
   row). Filtering/refresh/expand-state must still work on the now-top-level children.

**Layer 2 gate:** the panel title reads "Project"; the header reads the workspace name; the tree's top rows
are the workspace's cells/folders (no redundant workspace-name root row); filter, refresh, and expand-state
still work; clearing the workspace resets the header. Report.

---

## Acceptance
1. New Workspace always creates a **named folder containing `.cws`** (`…/<name>/.cws`); name validated;
   collisions rejected with a clear message; tree roots at the workspace folder; Open/Save unregressed.
2. Workspace name appears **once**: panel title = "Project"; header = workspace name; tree root row omitted
   (tree starts at the workspace's children). Filter/refresh/expand still work.
3. `dotnet build`/`dotnet test` green; firewall green; nothing else regresses.

## Guardrails
- **Workspace = folder containing `.cws`** (§1.1); derive the workspace name from the **folder name** (the
  file is literally `.cws`, so the file stem is empty — don't use it for the name).
- **Collision guard** on New Workspace — reject, never overwrite.
- **Name shown once per role:** title "Project" / header = name / root omitted; omit the root in the
  view-binding layer, leave the scan/model intact.
- Don't regress Open/Save or filter/refresh/expand-state.
- Sub-gate the two layers; report and stop between each.
- Update `src/Ui/CLAUDE.md` if the `.cws`-name-from-folder gotcha (file stem is empty) is worth recording.

*Exit: New Workspace produces a proper workspace folder containing `.cws`, and the workspace name appears
exactly once (header), with a static "Project" panel title and no redundant tree-root row.*
