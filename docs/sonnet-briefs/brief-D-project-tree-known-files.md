# Brief D — Project Tree: Known Files (drag-drop receive + context menu)

**Scope:** add the ability to drop a file or directory onto the Project Tree (workspace tab) to register it as a **Known File** (a workspace shortcut persisted in `.cws`), with broken-reference rendering and a per-node context menu. Reuse the bitmap-drop path knowledge. UI-layer only; `.cws` model already has the field.

This is the largest of the project-tree briefs — do it in its own session.

---

## What already exists (read first — do NOT rebuild these)

- `src/Ui/Schematic/ProjectTreeNode.cs` — `NodeKind.KnownFile` and `NodeKind.KnownFilesGroup` already defined. `ProjectTreeNode.WarningReason` already drives broken-reference rendering (the tree renders `System.Warning` color + italics and uses the string as tooltip; "Known File path not found" is a listed cause).
- `src/Ui/ViewModels/ProjectTree/ProjectTreeItemViewModel.cs` (`ProjectTreeNodeViewModel`) — already has: `IsWarning` (→ italic + warning color), `IconKind` cases for `KnownFile`/`KnownFilesGroup`, `CanReveal` (includes `KnownFile`), `RevealCommand`, platform-correct `RevealLabel`. Filter wiring: `NodeKind.KnownFile`/`KnownFilesGroup → f.KnownFiles` already in `IsVisibleUnderFilter`.
- `src/Ui/ViewModels/ProjectTree/ITreeActions.cs` — the action interface WorkspaceViewModel implements. You will ADD methods here.
- `src/Ui/ViewModels/WorkspaceViewModel.cs` — implements `ITreeActions`. `Reveal(node)` is ALREADY implemented (macOS `open -R` / Windows `explorer /select,` / Linux `xdg-open`). The `.cws` Known-Files list is already used: `SaveLooseToWorkspace` adds to `cws.KnownFiles` and `WriteWorkspaceFile` persists `TreeViewState.KnownFiles` (the filter toggle, not the list). The KnownFiles *list* lives on `CwsFile.KnownFiles` (a `List<string>` of paths).
- `src/Ui/Schematic/WorkspacePersistence.cs` + `WorkspaceModel.cs` + `WorkspaceScanner.cs` — `CwsFile.KnownFiles` (List<string>); `WorkspaceScanner.Scan` builds the tree. **Confirm whether the scanner already emits `KnownFile`/`KnownFilesGroup` nodes from `cws.KnownFiles`** — if it does, you only need the broken-ref `WarningReason` + DnD + context menu. If it does NOT yet build those nodes, add that to the scanner (Layer 2).
- Bitmap-drop reference (reuse the type-switch): `src/Ui/Controls/SchematicCanvas.cs` `TryExtractImagePath(DragEventArgs)` — macOS (Avalonia.Native) returns a **single** `IStorageItem` under `DataFormat.File`; other backends return `IEnumerable<IStorageItem>`; also handle a bare `string`. Copy that defensive type-switch pattern for the tree drop (but accept ANY file or directory, not just images).
- `src/Ui/Views/ProjectTree/ProjectTreeView.axaml` (+ `.axaml.cs`) — the tree view. You'll add `DragDrop` handlers (mirror `SchematicCanvas`'s `DragDrop.SetAllowDrop(this,true)` + `AddHandler(DragDrop.DragOverEvent/DropEvent, …)`).

---

## Spine (do-not-violate)

1. **Known Files are paths in `.cws`** (`CwsFile.KnownFiles`), never copies of file content. "Copy to Workspace" is the only action that copies bytes.
2. **Alpha persistence policy:** `.cws` format_version is written and reject-on-mismatch; never migrate. Don't add fields you don't need.
3. The tree is **rebuilt from scratch** by `WorkspaceScanner.Scan` on every refresh — Known-File nodes are transient/derived from `cws.KnownFiles`; do not store node state.
4. A dropped path that is OUTSIDE the workspace is still a valid Known File (a shortcut). A path that doesn't exist on disk is a **broken** Known File (warning render + Fix option).
5. Reuse `Reveal` (already implemented) for the reveal action. Don't reimplement OS reveal.

---

## Layer 1 — Drag-drop RECEIVE on the Project Tree

In `ProjectTreeView.axaml.cs`:
1. `DragDrop.SetAllowDrop(this, true)` and `AddHandler(DragDrop.DragOverEvent, OnFileDragOver)` + `AddHandler(DragDrop.DropEvent, OnFileDrop)` (constructor).
2. `OnFileDragOver`: if the drag contains at least one file/dir path (use the bitmap-style `TryExtractDroppedPath` you port from `SchematicCanvas.TryExtractImagePath`, but **accept any extension AND directories**), set `e.DragEffects = Copy; e.Handled = true;` else `None`.
3. `OnFileDrop`: extract the path(s); for each, call a new tree action to register it as a Known File. Resolve the `ProjectTreeTool`/`ITreeActions` from `DataContext` (the view's DataContext is `ProjectTreeTool`; actions are reachable via the same `ITreeActions` the node VMs use — see how `SetActions(this)` is wired in WorkspaceViewModel; you may need `ProjectTreeTool` to expose an `AddKnownFile(path)` that forwards to `ITreeActions`, OR add the registration directly on `ITreeActions` and call it from the tool). Keep it consistent with the existing actions plumbing.

**`TryExtractDroppedPath` rules:** `item.TryGetRaw(DataFormat.File)` → switch: `IStorageItem single → single.Path?.LocalPath`; `IEnumerable<IStorageItem> files → files.Select(...LocalPath)`; `string s → s`. Accept the path if `File.Exists(path) || Directory.Exists(path)` (a directory IS allowed — it's a folder shortcut). Return all valid paths.

**Privacy/safety:** only register paths the **user dropped** (this is direct user action, fine). Do not auto-open or execute anything on drop — just register the reference.

**Gate 1:** Drag a file from Finder onto the Project Tree → it appears under a "Known Files" group as a Known File node. Drag a folder → it appears as a Known File (folder icon). Dropping a non-file (text, etc.) is ignored.

---

## Layer 2 — Build Known-File nodes from `.cws` (if not already)

If `WorkspaceScanner.Scan` does not yet emit nodes for `cws.KnownFiles`:
- Add a synthetic `KnownFilesGroup` node (only when `cws.KnownFiles` is non-empty) whose children are one `KnownFile` node per path.
- For each Known File path, set `WarningReason = "Known File path not found"` (or similar) when `!File.Exists(path) && !Directory.Exists(path)` → drives the existing italic + warning-color render and the Fix affordance.
- Node `Name` = `Path.GetFileName(path)` (for a directory, the folder name); `AbsolutePath` = the stored path.
- **New action plumbing:** the drop registers via `ITreeActions`. Add to `ITreeActions` (and implement in `WorkspaceViewModel`):
  - `void AddKnownFile(string path)` — loads `.cws` (preserve existing list; dedup case-insensitive like `SaveLooseToWorkspace` does), adds the path to `cws.KnownFiles`, `WorkspacePersistence.SaveToFileAtomic`, then `_factory.ProjectTreeTool?.Refresh()`.

**Gate 2:** Restart/reopen the workspace → Known Files persist (they're in `.cws`). A Known File whose target was deleted renders in the warning color/italics with a tooltip.

---

## Layer 3 — Per-Known-File context menu

Add a context menu shown only on `KnownFile` nodes. Mirror how existing context-menu items are declared in `ProjectTreeView.axaml` and gated by `ProjectTreeNodeViewModel` boolean visibility props (e.g. `IsViewFile`, `IsCell`, `CanReveal`). Add an `IsKnownFile => Kind == NodeKind.KnownFile` helper and bind menu-item visibility to it. Commands wire to NEW `ITreeActions` methods.

Menu items (in this order):
1. **"Open External…"** → `OpenExternal(node)`: ask the OS to open the file/dir with its default handler.
   - macOS: `Process.Start(new ProcessStartInfo("open", new[]{ path }){ UseShellExecute=false })`
   - Windows: `Process.Start(new ProcessStartInfo(path){ UseShellExecute=true })`
   - Linux: `Process.Start(new ProcessStartInfo("xdg-open", path){ UseShellExecute=false })`
   - Disabled when the path is broken (doesn't exist).
2. **"Copy to Workspace"** → `CopyToWorkspace(node)`: **disabled + greyed out when the path IS already within the workspace** (a `CanCopyToWorkspace` predicate: enabled only when the path is OUTSIDE `workspaceDir`). When enabled: copy the file to the workspace root (`File.Copy(src, Path.Combine(workspaceDir, Path.GetFileName(src)))`, handle name collision — append ` (1)` etc. or refuse with a message), then **update the Known File reference** in `.cws` to the new in-workspace path (replace the old entry), save `.cws`, refresh. For a **directory** Known File: either disable Copy-to-Workspace for directories in v1 (state this) or do a recursive copy — recommend **disable for directories in v1** to keep scope tight; note it.
   - Predicate: a path is "within the workspace" if `Path.GetRelativePath(workspaceDir, path)` does not start with `..` and isn't rooted elsewhere. Use a robust check.
3. **"Remove Reference"** → `RemoveKnownFile(node)`: remove the path from `cws.KnownFiles`, save `.cws`, refresh. **Does NOT delete the file on disk.** (Make this unmistakable — only the reference is removed.)
4. **Reveal** ("Reveal in Finder"/"Reveal in Explorer"/"Reveal in File Manager") → reuse the EXISTING `RevealCommand` / `Reveal(node)` and `RevealLabel` (already implemented; `KnownFile` is already in `CanReveal`). Just include the menu item.

Add the four `ITreeActions` members (`OpenExternal`, `CopyToWorkspace`, `RemoveKnownFile`, and reuse `Reveal`) and their commands + `CanExecute` predicates on `ProjectTreeNodeViewModel` (mirroring how `RevealCommand`/`MakePrimaryCommand` are constructed with a can-execute that checks `_actions is not null && <visibility>`).

**Broken-reference Fix affordance:** the user wants broken Known Files to offer a "fix". Minimum viable: a broken Known File shows the warning render (Layer 2) and its context menu still offers **Remove Reference** and (if you want) a **"Fix…"** item that opens a file picker to re-point the reference to a new path (update `cws.KnownFiles` entry, save, refresh). If "Fix…" is more than trivial, ship Remove + warning render in v1 and note Fix as a follow-up — but state which you did.

**Gate 3:** Right-click a Known File → menu shows Open External…, Copy to Workspace (greyed when already in-workspace), Remove Reference, Reveal. "Remove Reference" removes the node but leaves the file on disk. "Copy to Workspace" on an external file copies it in and re-points the reference. Reveal highlights it in the OS file manager. A broken Known File still offers Remove (and Fix, if implemented).

---

## Acceptance

- Drop a file OR directory onto the tree → registered as a Known File in `.cws`, appears under a Known Files group, persists across reopen. ✅
- Broken Known Files render with warning color/italics + tooltip. ✅
- Per-node context menu: Open External… (disabled if broken), Copy to Workspace (disabled if already in workspace; directory handling stated), Remove Reference (reference only, file untouched), Reveal (reuses existing). ✅
- All `.cws` writes go through `WorkspacePersistence.SaveToFileAtomic` and preserve the rest of the file. ✅

## Guardrails

- Reuse `Reveal` and the bitmap-drop type-switch; don't reinvent.
- Never copy file bytes except in "Copy to Workspace". Never delete a file (Remove Reference is reference-only).
- Preserve unrelated `.cws` contents on every write (load → mutate KnownFiles → atomic save), exactly like `SaveLooseToWorkspace`.
- Keep `ProjectTreeNode`/`WorkspaceModel` framework-free.

## Scope fence (do NOT do here)

- No grippers (A), clipboard (B), tab-name/open-`.csym` fixes (C), or cell/properties (E).
- Don't implement directory-recursive Copy-to-Workspace unless trivial; disable for dirs and note it.

## Exit / report

State: whether the scanner already built KnownFile nodes or you added it; the new `ITreeActions` members; the in-workspace predicate; directory handling for Copy-to-Workspace; whether you shipped "Fix…"; and confirmation you ran the 3 gates mentally.
