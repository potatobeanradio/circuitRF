# Sonnet Brief — Project Tree: hide .DS_Store / .source files + add "Open" context-menu item

Two small, independent Project Tree changes.

## Part A — Hide `.DS_Store` and `.source` files from the tree
**Where:** `src/Ui/Schematic/WorkspaceScanner.cs`.

`.DS_Store` (macOS) and `.source` files currently appear as `OtherFile` nodes (via `BuildFileNode`) in the
workspace root and in user folders. They should NOT be displayed in the tree — UNLESS the user has explicitly
added them as Known Files (opt-in), in which case they still appear under the Known Files group.

**Fix:** add a small predicate and skip hidden files in the two loose-file enumeration loops only:
```csharp
// A file the tree hides by default (still shown if the user adds it as a Known File).
private static bool IsHiddenTreeFile(string path)
{
    var name = Path.GetFileName(path);
    return string.Equals(name, ".DS_Store", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetExtension(path), ".source", StringComparison.OrdinalIgnoreCase);
}
```
Apply it by skipping in:
1. `Scan` — the loose-files-at-root loop (`foreach (string f in Directory.GetFiles(workspaceRootDir)…)`), and
2. `BuildUserFolderNode` — the file loop (`foreach (string f in Directory.GetFiles(dir)…)`).

Do **not** filter in `BuildKnownFileNode` (Known Files are explicit user opt-in) or in `BuildCellNode` (cells
only list view files by extension). Add the skip right after the existing `.cws` skip in `Scan`.

**Assumption to confirm with owner if it matters:** "named `.source`" is interpreted as *extension* `.source`
(covers `foo.source`); `.DS_Store` is matched by exact filename. If the intent was an exact filename `.source`
too, the extension check already covers a file literally named `.source` only if it has that extension — a file
named exactly `.source` has extension `.source`, so it's covered.

**Test (`tests/Ui.Tests`, headless scanner test):** scan a temp workspace containing `a.csch`, `.DS_Store`,
`x.source`, and a normal `notes.txt` → resulting tree has nodes for `a.csch` and `notes.txt` but none for
`.DS_Store`/`x.source`. Add a second case: same workspace with `.DS_Store` listed in `.cws` KnownFiles → it
appears under the Known Files group (opt-in still works).

## Part B — Add "Open" context-menu item for `.csch`, `.csym`, `.cdd`
Same effect as double-click: opens the file in the Content panel (or focuses its tab if already open / torn
off). Double-click already routes through `ProjectTreeNodeViewModel.ActivateCommand` → `ITreeActions.OpenNode`,
and `OpenNode` already handles `ViewFile` (.csch/.csym) and `DataDisplayFile` (.cdd). So this is just a new menu
item bound to the **existing** `ActivateCommand`, shown for those node kinds.

**VM** (`src/Ui/ViewModels/ProjectTree/ProjectTreeItemViewModel.cs`): add a visibility helper:
```csharp
/// <summary>True for openable leaf files (.csch / .csym / .cdd) — drives the "Open" context item.</summary>
public bool IsOpenableFile =>
    Kind == NodeKind.DataDisplayFile
    || (Kind == NodeKind.ViewFile && Path.GetExtension(AbsolutePath).ToLowerInvariant() is ".csch" or ".csym");
```
(No new command — reuse `ActivateCommand`, which already calls `_actions?.OpenNode(this)`.)

**View** (`src/Ui/Views/ProjectTree/ProjectTreeView.axaml`): add as the FIRST item in the `<ContextMenu>` (above
the Known-File block), so "Open" is at the top for files:
```xml
<MenuItem Header="Open"
          Command="{Binding ActivateCommand}"
          IsVisible="{Binding IsOpenableFile}"/>
```
(Icons for all menu items come in a separate brief — don't add icons here.)

**No test needed** for Part B (pure wiring to an already-tested command); a build check suffices.

## Gate
Build 0W/0E. Scanner test green. Manually: `.DS_Store`/`.source` no longer in tree; right-click a `.csch`/`.csym`/
`.cdd` shows "Open" which opens/focuses the file like double-click.
