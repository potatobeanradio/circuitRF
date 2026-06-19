# Brief: Project Tree UX improvements (9 items)

Stack/rules: .NET 10, C# 14, Avalonia 12, CommunityToolkit.Mvvm, Material.Icons.Avalonia.
`src/Ui/CircuitRF.Ui.csproj` has **TreatWarningsAsErrors=true** — capture nullable-property reads into
locals, and NEVER put raw `<`/`>` in XML doc comments. Build must end **0W/0E**; add gate tests where
noted; report total test count. Newest-first changelog entry in `src/Ui/CLAUDE.md` after landing.
Adding **defaulted/nullable** fields is safe under the alpha no-migration rule — no format_version bump.

Land each item independently; they're mostly orthogonal. Items 4, 8, 9 are small (AXAML + a tiny
code-behind/VM hook); 1, 2, 3, 5 are medium; 6, 7 are the substantial ones.

Key files (all verified on disk unless marked "locate"):
- `src/Ui/Views/ProjectTree/ProjectTreeView.axaml` + `.axaml.cs` — tree view + code-behind.
- `src/Ui/ViewModels/ProjectTree/ProjectTreeItemViewModel.cs` — class `ProjectTreeNodeViewModel`
  (node VM: commands, `IsSaveable`, `IsCell`, `RevealLabel`, `CanReveal`, etc.).
- `src/Ui/ViewModels/ProjectTree/ITreeActions.cs` — callback interface `WorkspaceViewModel` implements.
- `src/Ui/ViewModels/Dock/ProjectTreeTool.cs` — the view's DataContext (`WorkspaceName`, `HasWorkspace`,
  `SelectedItem`, `_workspaceModel`, `SetWorkspace`, `ClearWorkspace`, `Refresh`, `SetActions`).
- `src/Ui/ViewModels/Dock/PropertiesTool.cs` — Properties pane (four mutually-exclusive contexts).
- `src/Ui/Schematic/CellFolder.cs` — cell layout + primacy (`ResolvePrimary`, `CreateCellFolder`,
  `ViewExtension`, `SubFolderPath`, `ViewType`, `PrimaryState`); `.ccell` via `CellPersistence` /
  `CcellFile{PrimarySchematic,PrimarySymbol,PrimaryLayout}`; `NameValidator.Validate(name)`.
- `src/Ui/Views/WorkspaceWindow.axaml` — File menu (NativeMenu + in-window Menu; already has Open
  Recent `RecentMenuItems`/`HasRecentWorkspaces` and a Clear-Recent command).
- `WorkspaceViewModel.cs` (large, locate by name) — implements `ITreeActions`; owns recent-workspaces
  infra, the save-dirty flow, document open/close, and the `PropertiesTool` reference.
- Locate: `CellUsageScanner` (has `CountReferencingCells`), `InputNameDialog`, the Properties view
  `PropertiesView.axaml`, and the app launch-startup routine + the recent-workspaces / launch-startup
  AppSettings members.

---

## Item 8 (do first — trivial) — Separators around "Edit Parameters"

In `ProjectTreeView.axaml`, the cell `MenuItem Header="Edit Parameters"` (Command=ActivateCommand,
IsVisible=IsCell) gets a separator above **and** below, each gated to the cell menu:
```xml
<Separator IsVisible="{Binding IsCell}"/>
<MenuItem Header="Edit Parameters" Command="{Binding ActivateCommand}" IsVisible="{Binding IsCell}">
  <MenuItem.Icon><mi:MaterialIcon Kind="Pencil" Width="14" Height="14"/></MenuItem.Icon>
</MenuItem>
<Separator IsVisible="{Binding IsCell}"/>
```

---

## Item 9 — Reorder the cell context menu

Target cell-menu order, top → bottom: **Open Schematic**, **Open Cell**, Open Symbol, … (creation
actions, Edit Parameters per item 8) …, **Reveal** (moved down), Remove Cell.

The ContextMenu is shared across all node kinds via per-kind `IsVisible`. Make these moves:
- Move the `Open Schematic` and `Open Symbol` `MenuItem`s (both IsVisible=IsCell) to the **top** of the
  `<ContextMenu>`, before the Save item. Because they're IsCell-only, this only changes the cell menu.
- Add a new **"Open Cell"** `MenuItem` directly **below** "Open Schematic" (above "Open Symbol"),
  IsVisible=IsCell. **OPEN QUESTION for the owner — confirm the command:** the cell's double-click
  (`ActivateCommand → ITreeActions.OpenNode`) is currently what "Edit Parameters" is also bound to, so
  "Open Cell" = `ActivateCommand` would duplicate "Edit Parameters". Most likely intent: "Open Cell" =
  `ActivateCommand` (open the cell document), and "Edit Parameters" stays as the labelled duplicate, OR
  "Open Cell" maps to a not-yet-existing "open cell overview" action. **Wire "Open Cell" to
  `ActivateCommand` for now and flag in the PR for confirmation.** Icon: `IntegratedCircuitChip`.
- Move the `Reveal` `MenuItem` (Header=RevealLabel, Command=RevealCommand, IsVisible=CanReveal) **down**
  to just above the Remove group (`Remove Data Display` / `Remove` / `Remove Cell`). Reveal is shared by
  many kinds; moving it down is acceptable for all of them (Reveal-near-bottom reads fine everywhere) —
  note this global effect in the PR.

No VM changes; pure AXAML reordering. Verify each non-cell kind's menu still reads sensibly.

---

## Item 4 — "Save Cell" stays in the menu after saving

**Root cause (verified).** `ProjectTreeNodeViewModel.IsSaveable => _actions?.IsNodeDirty(this)` is a plain
getter with no change notification, and the per-item `ContextMenu` in `ProjectTreeView.axaml` has **no
`Opening` handler**. So `IsVisible="{Binding IsSaveable}"` is evaluated once at template realization and
never refreshed — saving clears dirty but the menu still shows "Save Cell".

**Fix.**
- `ProjectTreeNodeViewModel`: add
  ```csharp
  /// <summary>Re-evaluate open-time-dynamic menu visibility (called from ContextMenu.Opening).</summary>
  public void RefreshDynamicMenuState()
  {
      OnPropertyChanged(nameof(IsSaveable));
      OnPropertyChanged(nameof(SaveHeader));
  }
  ```
- `ProjectTreeView.axaml`: on the `<ContextMenu>` add `Opening="OnNodeContextMenuOpening"`.
- `ProjectTreeView.axaml.cs`:
  ```csharp
  private void OnNodeContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
  {
      if (sender is ContextMenu { DataContext: ProjectTreeNodeViewModel vm })
          vm.RefreshDynamicMenuState();
  }
  ```
  (If the ContextMenu's DataContext is null at open time, fall back to
  `((sender as Control)?.Parent as Control)?.DataContext` / the placement target's DataContext.)

The dirty bullet (`IsDirty`, has INPC, set via `ProjectTreeTool.SetCellDirty`) already updates after save;
this only fixes the stale menu item.

**Test.** Hard to unit-test the popup; verify by hand (dirty cell → Save Cell → re-open menu → item gone).

---

## Item 3 — "Reveal in Finder" on the workspace title

Right-clicking the top `WorkspaceName` `TextBlock` (DataContext = `ProjectTreeTool`) should show a
single "Reveal in Finder/Explorer" item.

- `ProjectTreeTool`: add a platform-correct label and a reveal command that delegates to the workspace
  root node (which is `RootItems[0]`, Kind=Workspace, CanReveal=true):
  ```csharp
  public string RevealLabel =>
      RuntimeInformation.IsOSPlatform(OSPlatform.OSX)      ? "Reveal in Finder"
      : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Reveal in Explorer"
      : "Reveal in File Manager";

  [RelayCommand(CanExecute = nameof(HasWorkspace))]
  private void RevealWorkspace()
  {
      if (RootItems.Count > 0) RootItems[0].RevealCommand.Execute(null);
  }
  ```
  (Add `using System.Runtime.InteropServices;`. If `HasWorkspace` isn't already an
  `[ObservableProperty]`/notifying member, just drop `CanExecute` — the menu sits on the title which is
  only meaningful with a workspace.)
- `ProjectTreeView.axaml`: add a context menu to the title TextBlock:
  ```xml
  <TextBlock Grid.Column="0" Text="{Binding WorkspaceName}" ... >
    <TextBlock.ContextMenu>
      <ContextMenu IsVisible="{Binding HasWorkspace}">
        <MenuItem Header="{Binding RevealLabel}" Command="{Binding RevealWorkspaceCommand}">
          <MenuItem.Icon><mi:MaterialIcon Kind="FolderSearchOutline" Width="14" Height="14"/></MenuItem.Icon>
        </MenuItem>
      </ContextMenu>
    </TextBlock.ContextMenu>
  </TextBlock>
  ```

---

## Item 1 — Recent-workspaces list in the no-workspace state

Replace the `"No workspace open."` `TextBlock` (gated `!HasWorkspace`) with a recent list (name-only
links) + a "Clear Recent" button. The recent list already exists in app settings / `WorkspaceViewModel`
(it backs the File → Open Recent submenu's `RecentMenuItems` and a Clear-Recent command).

- `ITreeActions`: add recent-workspace access so `ProjectTreeTool` doesn't reach into settings directly:
  ```csharp
  IReadOnlyList<(string Name, string Path)> GetRecentWorkspaces();
  void OpenWorkspacePath(string path);
  void ClearRecentWorkspaces();
  ```
  Implement in `WorkspaceViewModel` over the existing recent-workspaces store. `Name` =
  `Path.GetFileName(path.TrimEnd(sep))`; skip entries whose folder no longer exists (or mark them, your
  call — simplest: skip).
- `ProjectTreeTool`: expose a bindable collection + commands:
  ```csharp
  public ObservableCollection<RecentEntry> RecentWorkspaces { get; } = new();
  public bool HasRecentWorkspaces => RecentWorkspaces.Count > 0;
  public sealed record RecentEntry(string Name, string Path);

  private void RefreshRecent()
  {
      RecentWorkspaces.Clear();
      if (_actions is not null)
          foreach (var (n, p) in _actions.GetRecentWorkspaces())
              RecentWorkspaces.Add(new RecentEntry(n, p));
      OnPropertyChanged(nameof(HasRecentWorkspaces));
  }

  [RelayCommand] private void OpenRecent(string path) => _actions?.OpenWorkspacePath(path);
  [RelayCommand] private void ClearRecent() { _actions?.ClearRecentWorkspaces(); RefreshRecent(); }
  ```
  Call `RefreshRecent()` from `SetActions(...)` and from `ClearWorkspace()` (so closing a workspace
  shows an up-to-date list).
- `ProjectTreeView.axaml`: replace the placeholder TextBlock with:
  ```xml
  <StackPanel IsVisible="{Binding !HasWorkspace}" Margin="8,6" Spacing="4">
    <TextBlock Text="Recent Workspaces" FontWeight="SemiBold" FontSize="11"
               Foreground="{DynamicResource SystemBaseMediumColor}"
               IsVisible="{Binding HasRecentWorkspaces}"/>
    <ItemsControl ItemsSource="{Binding RecentWorkspaces}">
      <ItemsControl.ItemTemplate>
        <DataTemplate x:DataType="vm:ProjectTreeTool+RecentEntry">
          <Button Classes="link" Padding="0" Margin="0,1"
                  Background="Transparent" BorderThickness="0" HorizontalAlignment="Left"
                  Content="{Binding Name}"
                  ToolTip.Tip="{Binding Path}"
                  Command="{Binding $parent[ItemsControl].((vm:ProjectTreeTool)DataContext).OpenRecentCommand}"
                  CommandParameter="{Binding Path}"/>
        </DataTemplate>
      </ItemsControl.ItemTemplate>
    </ItemsControl>
    <TextBlock Text="No recent workspaces." FontSize="11"
               Foreground="{DynamicResource SystemBaseMediumColor}"
               IsVisible="{Binding !HasRecentWorkspaces}"/>
    <Button Content="Clear Recent" FontSize="11" Margin="0,4,0,0"
            Command="{Binding ClearRecentCommand}"
            IsVisible="{Binding HasRecentWorkspaces}"/>
  </StackPanel>
  ```
  Style the link button (accent foreground, underline-on-hover) via a `Button.link` style in the
  control's `<UserControl.Styles>` (or reuse an existing link style if one exists). Confirm the
  `x:DataType` for the nested record (`ProjectTreeTool+RecentEntry`) compiles; if the compiled-binding
  path to `OpenRecentCommand` is awkward, give each `RecentEntry` its own `IRelayCommand` instead.

---

## Item 2 — File menu "Close Workspace"

Add a "Close Workspace" item to the File menu (both the macOS `NativeMenu` block and the in-window
`Menu` in `WorkspaceWindow.axaml`), bound to a new `CloseWorkspaceCommand` on `WorkspaceViewModel`,
enabled only when a workspace is open. Place it sensibly (e.g., after the Open Workspace/Recent group).

`CloseWorkspaceCommand` (in `WorkspaceViewModel`):
1. Prompt to save any dirty open documents — **reuse the existing save-dirty flow** (the same one used on
   app exit / "Save All" prompting). If the user cancels, abort the close.
2. Force-close all open Content/Tool documents for the workspace (reuse the existing close/`ForceClose`
   path), reset `PropertiesTool` to the placeholder (its setters already clear contexts when passed
   null), and call `ProjectTreeTool.ClearWorkspace()`.
3. Revert the window to the **Launch Startup** state — i.e., the same no-workspace state a fresh launch
   produces under the launch-startup setting. **Locate the app launch/startup routine** (App startup or a
   `WorkspaceViewModel` init path) and reuse it for the no-workspace case rather than duplicating layout
   reset. If launch-startup is configured to auto-open the last workspace, closing still lands on the
   blank/no-workspace shell (Close = explicit user intent), so call the blank-shell branch directly.

Keep this DRY: factor the "reset to no-workspace shell" into one method called by both startup and close.

---

## Item 5 — Properties inspector shows file info for known files / .npy

When the user selects a Known File (leaf, not a directory) or an `.npy`/results leaf (NodeKind.OtherFile)
in the tree, the Properties pane shows **name (no path), size, last-modified** — all selectable.

- New VM `FileInfoInspectorViewModel` (e.g. `src/Ui/ViewModels/FileInfoInspectorViewModel.cs`):
  properties `Name`, `SizeText`, `ModifiedText` (strings). Build from a `System.IO.FileInfo`:
  - `Name = fi.Name`
  - `SizeText` = human-readable (`B`/`KB`/`MB`/`GB`, e.g. `12.3 MB`); for a directory, skip/size "—".
  - `ModifiedText = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm")` (or a friendly local format).
- `PropertiesTool`: add a fifth context, mutually exclusive with the others:
  ```csharp
  [ObservableProperty] private bool _isFileInfoActive;
  [ObservableProperty] private FileInfoInspectorViewModel? _fileInfoVm;
  partial void OnIsFileInfoActiveChanged(bool v) => OnPropertyChanged(nameof(IsSchematicContextActive));

  public void SetActiveFileInfo(FileInfoInspectorViewModel? vm)
  {
      IsCellActive = false; IsSymbolEditorActive = false; IsDataDisplayActive = false;
      CellEditorVm = null; PlotInspectorVm = null;
      EditorVm.SetContext(null); SymbolInspectorVm.SetContext(null);
      IsFileInfoActive = vm is not null; FileInfoVm = vm;
      HeaderText = vm is not null ? "File" : "Properties";
  }
  ```
  Update `IsSchematicContextActive` to also exclude `IsFileInfoActive`, and add
  `IsFileInfoActive = false; FileInfoVm = null;` to the other four `SetActive*` methods so activating a
  document clears the file-info context.
- `PropertiesView.axaml` (locate): add a section visible when `IsFileInfoActive`, using
  **`SelectableTextBlock`** for every value so text is selectable:
  ```xml
  <StackPanel IsVisible="{Binding IsFileInfoActive}" Margin="8" Spacing="6">
    <SelectableTextBlock Text="{Binding FileInfoVm.Name}" FontWeight="SemiBold"/>
    <Grid ColumnDefinitions="Auto,*" RowDefinitions="Auto,Auto" >
      <TextBlock Grid.Row="0" Grid.Column="0" Text="Size" Margin="0,0,8,2"
                 Foreground="{DynamicResource SystemBaseMediumColor}"/>
      <SelectableTextBlock Grid.Row="0" Grid.Column="1" Text="{Binding FileInfoVm.SizeText}"/>
      <TextBlock Grid.Row="1" Grid.Column="0" Text="Modified" Margin="0,0,8,0"
                 Foreground="{DynamicResource SystemBaseMediumColor}"/>
      <SelectableTextBlock Grid.Row="1" Grid.Column="1" Text="{Binding FileInfoVm.ModifiedText}"/>
    </Grid>
  </StackPanel>
  ```
- Routing: `ProjectTreeTool` has `[ObservableProperty] SelectedItem`. Add a partial handler that calls a
  new `ITreeActions` hook:
  ```csharp
  partial void OnSelectedItemChanged(ProjectTreeNodeViewModel? value)
      => _actions?.OnTreeSelectionChanged(value);
  ```
  `ITreeActions`: add `void OnTreeSelectionChanged(ProjectTreeNodeViewModel? node);`. `WorkspaceViewModel`
  implements it: if `node` is a leaf file worth inspecting —
  `node.Kind == NodeKind.OtherFile`, or `node.Kind == NodeKind.KnownFile && File.Exists(node.AbsolutePath)`
  (not a directory) — build a `FileInfoInspectorViewModel` from `new FileInfo(node.AbsolutePath)` and call
  `PropertiesTool.SetActiveFileInfo(vm)`. For other node kinds, **do nothing** (leave the current
  document-driven context). (Optionally also handle `.npy` that classifies as OtherFile — it already does
  per the node VM's `IsRemovableFile` note.)

---

## Item 6 — "Duplicate" cell context-menu item

Duplicate a cell within the workspace under a new name; in the copy, rename the **primary** schematic and
symbol files to the new cell name (other views keep their filenames), unless that would collide.

- `ITreeActions`: `Task DuplicateCellAsync(ProjectTreeNodeViewModel cellNode);`
- `ProjectTreeNodeViewModel`: `DuplicateCellCommand` (AsyncRelayCommand, CanExecute = `IsCell`), delegate
  to `_actions?.DuplicateCellAsync(this)`. Add a `MenuItem Header="Duplicate…"` IsVisible=IsCell (near the
  cell creation group; icon `ContentDuplicate`).
- `WorkspaceViewModel.DuplicateCellAsync`:
  1. Prompt for the new cell name via `InputNameDialog` (prefill e.g. `oldName_copy`).
  2. Validate: `NameValidator.Validate(newName)` is null, and `Path.Combine(parentDir, newName)` does not
     already exist (no cell/folder collision). On failure, show the reason and abort.
  3. Recursively copy the cell folder `oldDir` → `newDir` (all sub-folders + `.ccell` + every view file).
  4. For each of Schematic and Symbol: resolve the **copied** cell's primary via
     `CellFolder.ResolvePrimary(newDir, ViewType.X)`. If it resolved to a concrete file
     (`SoleFile`/`NamedPresent`), compute target name `newName + CellFolder.ViewExtension(X)`. Rename the
     primary file to the target **and** update the copied `.ccell`'s `PrimarySchematic`/`PrimarySymbol`
     to the new filename — **unless** a different (non-primary) file of that exact target name already
     exists in the view sub-folder, in which case **skip the rename** for that view (leave the primary
     filename and `.ccell` entry untouched). Other (non-primary) view files always keep their names.
  5. Persist the `.ccell` (`CellPersistence.SaveToFile`) and `Refresh()` the tree.

  Use `CellFolder.SubFolderPath` + `ViewExtension`; do the copy with a small recursive helper (the project
  is framework-free here — plain `System.IO`).

**Test.** Duplicate a cell whose primary schematic is `foo.csch` → new cell `bar` has
`bar/schematic/bar.csch` and `.ccell.PrimarySchematic == "bar.csch"`; a non-primary `extra.csch` keeps its
name; if `bar/schematic/bar.csch` would collide with a pre-existing `bar.csch`, the primary keeps its
original name.

---

## Item 7 — "Rename" cell context-menu item

Rename a cell, update every cell that references it, and (optionally, via a checkbox defaulting ON) rename
its primary schematic + symbol to match.

- `ITreeActions`: `Task RenameCellAsync(ProjectTreeNodeViewModel cellNode);`
- `ProjectTreeNodeViewModel`: `RenameCellCommand` (AsyncRelayCommand, CanExecute = `IsCell`) +
  `MenuItem Header="Rename…"` IsVisible=IsCell (icon `Rename`/`FormTextbox`).
- Dialog: needs a name field **and** a checkbox "Rename primary schematic & symbol to match" (default
  checked). Extend `InputNameDialog` with an optional checkbox (label + default + result bool), or add a
  small `RenameCellDialog`. Returns `(string? newName, bool renamePrimaries)`.
- `WorkspaceViewModel.RenameCellAsync`:
  1. Prompt; if cancelled, abort.
  2. Validate `NameValidator.Validate(newName)` and that no other cell/folder named `newName` exists in
     the workspace (collision check). Abort with reason on failure.
  3. If the cell has dirty open documents, run the save-dirty prompt first (or require save); then
     **close** any open documents/sessions for this cell (renaming a folder out from under open editors
     will break them — reuse the force-close path, then reopen is the user's choice).
  4. Rename the folder `oldDir` → `Path.Combine(parentDir, newName)` (`Directory.Move`).
  5. **Update references:** find all cells that reference the old cell name and rewrite them to `newName`.
     `CellUsageScanner` already finds referencing cells (`CountReferencingCells`); extend it (or add a
     sibling) to enumerate the referencing `.csch` files and the in-file reference tokens, and rewrite
     each occurrence old→new. Do this transactionally where practical; if a rewrite fails, surface an
     error (the folder is already moved — log clearly; full rollback is out of scope for alpha, but report
     accurately). Confirm how a cell reference is stored in a `.csch` (the cell-name string in the
     instanced-component record) before rewriting — match exactly, not substring.
  6. If `renamePrimaries`: for Schematic and Symbol, resolve the primary in the **renamed** folder; if
     concrete, rename it to `newName + ext` and update `.ccell` — **but** if a non-primary file of that
     target name already exists in the sub-folder, **refuse that view's rename** (skip it, keep the
     original primary filename + `.ccell` entry) and tell the user which view was skipped.
  7. Persist `.ccell` and `Refresh()`.

**Test.** Rename cell `amp` → `lna` with a referencing testbench: the testbench's instanced reference now
reads `lna`; `lna/schematic/lna.csch` exists when the checkbox is on; renaming is refused (with a message)
if `lna/schematic/lna.csch` already exists as a non-primary file; a name colliding with another cell is
rejected up front.

---

## Notes / sequencing
- Items 8, 9, 4 are quick wins — land first.
- Items 1 & 2 share the recent-workspaces + launch-startup infrastructure in `WorkspaceViewModel` /
  AppSettings — read those once and do both together.
- Items 6 & 7 share cell-folder file ops, `.ccell` rewriting, and `InputNameDialog`/collision checks — do
  them together; Item 7's reference rewrite is the one genuinely new mechanism (extend `CellUsageScanner`).
- New `ITreeActions` members this brief adds: `GetRecentWorkspaces`, `OpenWorkspacePath`,
  `ClearRecentWorkspaces`, `OnTreeSelectionChanged`, `DuplicateCellAsync`, `RenameCellAsync`. Implement all
  in `WorkspaceViewModel`.
- **Confirm with the owner:** the "Open Cell" command mapping (Item 9).
