# Brief C — Project Tree: open `.csym`, and fix Content-tab names

**Scope:** two related bugs about opening documents from the Project Tree and how their tabs are titled. Small, surgical. UI-layer only.

1. Double-clicking a `.csym` file in the Project Tree does NOT open it in the Symbol Editor.
2. Content-tab titles should include the file extension (so a `.csch` and a `.csym` named after the same cell are distinguishable). Also a bug: double-clicking a `.csch` titles the tab with the **cell name**, not the file name.

---

## Read first (real names)

- `src/Ui/Views/ProjectTree/ProjectTreeView.axaml.cs` — `OnTreeDoubleTapped`: `if (tool.SelectedItem is ProjectTreeNodeViewModel node) node.ActivateCommand.Execute(null);` (`tool` is `ProjectTreeTool`).
- `src/Ui/ViewModels/ProjectTree/ProjectTreeItemViewModel.cs` — class is `ProjectTreeNodeViewModel`. `ActivateCommand = new RelayCommand(() => _actions?.OpenNode(this));`
- `src/Ui/ViewModels/WorkspaceViewModel.cs` — implements `ITreeActions`. KEY methods:
  - `OpenNode(ProjectTreeNodeViewModel node)`: switches on `node.Kind`. For `NodeKind.ViewFile` it reads `Path.GetExtension(node.AbsolutePath).ToLowerInvariant()` and branches: `.csym → OpenOrActivateSymbol`, `.csch → OpenOrActivateSchematic`, else no-op. For `NodeKind.Cell → OpenOrActivateCellPlaceholder`. **default → no-op.**
  - `OpenOrActivateSymbol(absolutePath)`: builds `SymbolEditorDocument(Path.GetFileNameWithoutExtension(absolutePath), vm, absolutePath)`.
  - `OpenOrActivateSchematic(absolutePath)`: `title = string.IsNullOrWhiteSpace(cellName) ? Path.GetFileNameWithoutExtension(absolutePath) : cellName;` ← **this is the tab-name bug: cellName wins.** `cellName` comes from `SchematicPersistence.LoadFromFile`.
- `src/Ui/Schematic/ProjectTreeNode.cs` — `NodeKind` enum. A `.csym`/`.csch`/`.clay` inside a cell view sub-folder is built as `NodeKind.ViewFile` by `WorkspaceScanner.Scan`. **Verify this is actually the kind assigned to a `.csym` node** (Bug #1 hinges on it).
- `src/Ui/Schematic/WorkspaceScanner.cs` — where `ProjectTreeNode`s (and their `NodeKind`) are produced. Grep for `.csym`, `ViewFile`, `CellViewFolder`. Confirm a `.csym` file becomes a `ViewFile` node with `AbsolutePath` ending in `.csym`.
- `src/Ui/Schematic/SchematicDocument.cs` and `src/Ui/Schematic/SymbolEditorDocument.cs` — how `Title`/`Id` are set (constructor takes a title string). The tab shows `Title`.

---

## Bug 1 — `.csym` double-click does not open the Symbol Editor

`OpenNode` already branches `.csym → OpenOrActivateSymbol`, so the dispatch logic is correct **if** the node reaches `OpenNode` as a `ViewFile` whose `AbsolutePath` ends in `.csym`. The break is therefore upstream. **Diagnose in this order — do not "fix" blindly:**

1. **Confirm the NodeKind.** In `WorkspaceScanner.Scan`, verify a `.csym` file under `cell/symbol/` is emitted as `NodeKind.ViewFile` (not `OtherFile`, not skipped). If `.csym` is being classified as something `OpenNode`'s `switch` doesn't handle (→ falls to `default:` no-op), THAT is the bug — fix the scanner classification so `.csym` is a `ViewFile`.
2. **Confirm double-tap routing.** Add a temporary check (or reason via reading): does `OnTreeDoubleTapped` fire for a `.csym` row and is `tool.SelectedItem` the right node? If the row's VM is a `ProjectTreeNodeViewModel` and `ActivateCommand` runs, it reaches `OpenNode`. If selection isn't set on double-tap (e.g. the tap selects then the VM is a different instance), fix the selection/routing.
3. **Confirm dedup isn't mis-hitting.** `OpenOrActivateSymbol` calls `ActivateIfOpen(absolutePath)` first (keyed in `_openDocsByPath` by absolute path, case-insensitive). If a stale/empty entry exists for that path, it would "activate" a non-existent/closed doc. Unlikely but cheap to rule out.

Most likely it's (1) — a scanner classification gap for `.csym`. Whatever you find, fix the actual cause; if it turns out `OpenNode` IS reached with the right ext and still fails, instrument `OpenOrActivateSymbol` (it has a try/catch that surfaces `Messages.Error($"Failed to open symbol: …")`) and read the message — a load/parse exception would be swallowed into that toast.

**Gate 1:** Double-click a `.csym` in the tree → it opens in the Symbol Editor as a content tab (or activates the existing tab if already open). Double-clicking the same file again activates the existing tab (no duplicate). A malformed `.csym` shows the existing error toast, not silent nothing.

---

## Bug 2 — Content-tab titles: include extension; use file name (not cell name)

**Desired:** every content tab opened from the tree is titled with the **file name including its extension** — e.g. `amp.csch`, `amp.csym` — so two views named after the same cell are visually distinct.

Changes in `WorkspaceViewModel`:

1. **`OpenOrActivateSchematic`**: replace the title logic. Currently `title = cellName-or-filename`. Change to the file name **with** extension: `var title = Path.GetFileName(absolutePath);` (yields `amp.csch`). Drop the `cellName` precedence entirely for the tab title. (The `cellName` is still used elsewhere for `SchematicPersistence.SaveToFile(..., cellName)` and as the schematic's `doc.Id` for save prompts — **do not** change `Id`/save semantics; only change the displayed `Title`. Check whether `SchematicDocument` uses the constructor's title arg for BOTH `Id` and `Title`; if so, and `Id` is used as a stable key for save prompts / `NextScratchSchematicTitle` dedup, keep `Id` as-is and set only `Title`. See note below.)
2. **`OpenOrActivateSymbol`**: change `Path.GetFileNameWithoutExtension(absolutePath)` → `Path.GetFileName(absolutePath)` (yields `amp.csym`).
3. **Consistency:** also update the other open/create paths that title tabs from a file so the convention is uniform when opened from the tree:
   - `OpenSymbolFile` (File-menu open) currently uses `Path.GetFileNameWithoutExtension(path)`.
   - `NewSymbolAsync` / `NewSchematicAsync` title the new tab with the bare `name` (no extension). For consistency, title new view tabs with `name + ext` too (use `CellFolder.ViewExtension(ViewType.Symbol/Schematic)` which gives `.csym`/`.csch`). Confirm this doesn't break `_openDocsByPath` (keyed by path, not title) or scratch-title dedup (scratch docs are "Untitled-…", separate).

**IMPORTANT — `Id` vs `Title` separation (read before editing):**
`SchematicDocument`/`SymbolEditorDocument` set both `Id` and `Title` from the constructor's title arg today. `Id` is used as a **stable identifier** in several places: save-prompt text (`Save '{doc.Id}'…`), `NextScratchSchematicTitle`/`NextScratchSymbolTitle` dedup, and `SaveSingleDocument` messages. The **tab UI shows `Title`**. To add the extension to the visible tab without disturbing identity-based logic:
- Preferred: set `Title` to the file-name-with-extension and leave `Id` as whatever it is today (so save prompts/dedup are unchanged). If the document constructor forces `Id == Title`, add a way to pass a distinct display title (e.g. an optional `displayTitle` ctor param or a settable `Title`), and only change the displayed `Title`.
- If `Id` and `Title` being equal is relied upon anywhere for *path/identity*, do NOT collapse them — keep identity stable, change display only.
Read `SchematicDocument.cs` / `SymbolEditorDocument.cs` and the `.cws` restore (`RestoreOpenDocuments` re-opens by path, not title — safe) to confirm titles aren't used as keys.

**Gate 2:** Open a `.csch` from the tree → tab reads `name.csch` (NOT the cell name). Open a `.csym` → tab reads `name.csym`. Open both a schematic and a symbol named after the same cell → the two tabs are visually distinct by extension. Save prompts still name the doc sensibly. `.cws` save/restore still re-opens the same tabs.

---

## Acceptance

- `.csym` double-click opens/activates the Symbol Editor. ✅
- Tabs show file name **with** extension for both `.csch` and `.csym`, opened from tree, File menu, and New. ✅
- `.csch` tab no longer shows the cell name. ✅
- Document `Id`/save-prompt/dedup/`.cws` restore semantics unchanged. ✅

## Guardrails

- Fix Bug 1's **actual cause** (likely scanner classification); don't paper over it in `OpenNode`.
- Change only the **displayed Title**, never the identity `Id` used by save prompts/dedup, unless you've confirmed `Id` isn't an identity key.
- Minimal diff. List every file touched.

## Scope fence (do NOT do here)

- No Known-Files drag-drop (Brief D). No cell-parameter editor (Brief E). No clipboard (B). No grippers (A).

## Exit / report

State: the real cause of Bug 1 (with the file/line you changed) and whether you needed instrumentation; the exact title expressions used in each open/create path; and whether `Id` and `Title` are now distinct (and why that's safe). Confirm you ran both gates mentally.
