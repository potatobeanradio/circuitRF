# Brief: polish-cell-open-menu — "Open Schematic" / "Open Symbol" on cell context menu

**Goal.** Add two items to a **cell** node's Project Tree context menu, directly below the "Reveal in
Finder" item: **Open Schematic** and **Open Symbol**. They open the cell's *primary* `.csch` / `.csym`
in a Content-panel tab (or activate the existing tab if already open). Each is greyed out + disabled
when the cell has no resolvable primary view of that type.

Authority: laundry-list addendum. Size: **S–M**.

## Files

- `src/Ui/ViewModels/ProjectTree/ITreeActions.cs` — two new callbacks.
- `src/Ui/ViewModels/ProjectTree/ProjectTreeItemViewModel.cs` — two commands + enable flags on
  `ProjectTreeNodeViewModel`.
- `src/Ui/ViewModels/WorkspaceViewModel.cs` — implement the callbacks.
- `src/Ui/Views/ProjectTree/ProjectTreeView.axaml` — two menu items.

## Step 1 — ITreeActions callbacks

```csharp
/// <summary>Open (or activate) the cell's primary schematic in a Content tab.</summary>
void OpenCellSchematic(ProjectTreeNodeViewModel cellNode);

/// <summary>Open (or activate) the cell's primary symbol in a Content tab.</summary>
void OpenCellSymbol(ProjectTreeNodeViewModel cellNode);
```

## Step 2 — node VM: enable flags + commands

In `ProjectTreeNodeViewModel`, a Cell node's `AbsolutePath` is the cell folder. Compute "has a primary"
once at construction using the authoritative `CellFolder.ResolvePrimary` (same primacy rule used by
Push-In / Make-Primary). Enabled iff a unique primary exists (`SoleFile` or `NamedPresent`); disabled
for `NoView` (none), `NoPrimary` (multiple, none chosen), and `MissingNamedPrimary` (broken).

Add read-only fields + props:

```csharp
public bool CanOpenSchematic { get; }
public bool CanOpenSymbol    { get; }
```

Set them in the constructor (only meaningful for cells):

```csharp
if (Kind == NodeKind.Cell)
{
    CanOpenSchematic = CellFolder.ResolvePrimary(AbsolutePath, ViewType.Schematic).State
        is PrimaryState.SoleFile or PrimaryState.NamedPresent;
    CanOpenSymbol = CellFolder.ResolvePrimary(AbsolutePath, ViewType.Symbol).State
        is PrimaryState.SoleFile or PrimaryState.NamedPresent;
}
```

(`CellFolder`/`ViewType`/`PrimaryState` are in `CircuitRF.Ui.Schematic`, already imported in this file.
This is a couple of `Directory.GetFiles` per cell node at tree-build time — acceptable; the scanner
already walks these folders. If you'd rather avoid the disk hit, derive it from existing scanned
children — a primary `.csch`/`.csym` descendant `ViewFile` with `IsPrimary == true` — but the
`ResolvePrimary` call is simplest and authoritative.)

Add the commands (alongside the other command properties + their wiring in the ctor):

```csharp
public IRelayCommand OpenSchematicCommand { get; }
public IRelayCommand OpenSymbolCommand    { get; }

// … in the ctor, with the other `new RelayCommand(...)` wirings:
OpenSchematicCommand = new RelayCommand(
    () => _actions?.OpenCellSchematic(this),
    () => _actions is not null && IsCell && CanOpenSchematic);

OpenSymbolCommand = new RelayCommand(
    () => _actions?.OpenCellSymbol(this),
    () => _actions is not null && IsCell && CanOpenSymbol);
```

A `RelayCommand` with `CanExecute == false` makes the bound `MenuItem` disabled + greyed automatically
— that's the requested behaviour.

## Step 3 — WorkspaceViewModel implementation

Reuse the existing dedup-and-activate openers (`OpenOrActivateSchematic` / `OpenOrActivateSymbol`),
which already activate an existing tab when the file is open (`ActivateIfOpen` →
`_factory.SetActiveDockable`).

```csharp
public void OpenCellSchematic(ProjectTreeNodeViewModel cellNode) => OpenCellPrimary(cellNode, ViewType.Schematic);
public void OpenCellSymbol(ProjectTreeNodeViewModel cellNode)    => OpenCellPrimary(cellNode, ViewType.Symbol);

private void OpenCellPrimary(ProjectTreeNodeViewModel cellNode, ViewType viewType)
{
    var cellDir = cellNode.AbsolutePath;
    var pr      = CellFolder.ResolvePrimary(cellDir, viewType);
    if (pr.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent) || pr.ResolvedName is null)
    {
        var what = viewType == ViewType.Schematic ? "schematic" : "symbol";
        Messages.Info($"Cell '{Path.GetFileName(cellDir)}' has no primary {what}.");
        return;
    }
    var path = Path.Combine(CellFolder.SubFolderPath(cellDir, viewType), pr.ResolvedName);
    if (viewType == ViewType.Schematic) OpenOrActivateSchematic(path);
    else                                OpenOrActivateSymbol(path);
}
```

Notes:
- `OpenOrActivateSchematic`/`OpenOrActivateSymbol` already: open in a Content tab, dedupe by absolute
  path, and activate the existing tab via `SetActiveDockable` when already open.
- **Torn-away (floated) tab:** `ActivateIfOpen` calls `_factory.SetActiveDockable(existing)`, which
  activates the dockable within its host window. If the document was floated into a separate OS window,
  verify that this brings that window forward; if Dock doesn't raise the OS window, that's a small
  follow-up (find the host `Window` for the dockable and `Activate()` it) — note the result, don't
  over-engineer it in this brief.
- The disk re-resolve here is harmless and keeps the action self-contained even if the node flag is
  stale.

## Step 4 — menu items

In `ProjectTreeView.axaml`, insert the two items immediately **after** the shared Reveal item and
**before** the cell `<Separator>` (so for a cell the menu reads: Reveal in Finder → Open Schematic →
Open Symbol → ─── → New Schematic …):

```xml
<!-- All revealable nodes -->
<MenuItem Header="{Binding RevealLabel}"
          Command="{Binding RevealCommand}"
          IsVisible="{Binding CanReveal}"/>

<!-- Cell: open primary views (greyed when no primary of that type exists) -->
<MenuItem Header="Open Schematic"
          Command="{Binding OpenSchematicCommand}"
          IsVisible="{Binding IsCell}"/>
<MenuItem Header="Open Symbol"
          Command="{Binding OpenSymbolCommand}"
          IsVisible="{Binding IsCell}"/>

<!-- Separator before creation actions -->
<Separator IsVisible="{Binding IsWorkspaceOrLibrary}"/>
<Separator IsVisible="{Binding IsCell}"/>
```

`IsVisible="{Binding IsCell}"` shows them only on cell nodes; the command's `CanExecute` greys them out
when there's no primary of that type.

## Acceptance

- Right-click a cell with a primary schematic and symbol → **Open Schematic** and **Open Symbol** are
  enabled and open/activate the right tabs.
- A cell with only a schematic (no symbol, or multiple symbols with none primary) → **Open Symbol** is
  greyed/disabled; **Open Schematic** works (and vice-versa).
- A cell with neither → both greyed/disabled.
- Invoking when the tab is already open activates it rather than opening a duplicate.
- The two items appear only on cell nodes, directly below Reveal in Finder.
