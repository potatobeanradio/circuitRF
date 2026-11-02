# Brief: gennet — Generate Netlist command (Simulate menu)

**Goal.** Add a **Generate Netlist** command to the Simulate menu that writes `netlist.cnl` for
the active schematic and opens it in the OS default editor. No analysis is run. The command is
enabled only when a schematic document is active (greyed for Symbol Editor / cell editor / Welcome
/ no document), works for empty schematics, and is **not** undoable.

This brief is independent of the hierarchical-extraction work — it uses the existing
`WriteNetlist`. Once the extraction + CnlWriter briefs land, the same command produces a
hierarchical netlist with no further change here.

Authority: `docs/design/hierarchical-net-extraction.md` §6.

## Files

- `src/Ui/ViewModels/WorkspaceViewModel.cs` — new command + CanExecute + enablement refresh +
  `OpenPathExternal` helper.
- `src/Ui/Views/WorkspaceWindow.axaml` — menu items (macOS `NativeMenu` + in-window `Menu`).

## Step 1 — `GenerateNetlistCommand` in WorkspaceViewModel

Add to the **Simulate commands** region (next to `RunAnalysis`/`StopAnalysis`). Model it on
`RunAnalysis` but **stop after writing the file** and add an explicit CanExecute.

```csharp
/// <summary>
/// Extracts the active schematic and writes netlist.cnl (no analysis is run), then opens it
/// in the OS default editor. Enabled only when a schematic document is active. Not undoable.
/// </summary>
[RelayCommand(CanExecute = nameof(CanGenerateNetlist))]
private void GenerateNetlist()
{
    if (_factory.DocumentDock?.ActiveDockable is not SchematicDocument activeDoc)
        return; // CanExecute guards this; defensive.

    var testBenchName = activeDoc.Id;

    string netlistPath;
    try
    {
        IReadOnlyList<string> conflicts;
        // ActiveViewModel = the cell currently being viewed (base schematic, or a pushed-in
        // sub-cell). WYSIWYG: generate a netlist for what the user is looking at.
        (netlistPath, conflicts) = WriteNetlist(activeDoc.ActiveViewModel.EditModel, testBenchName);
        foreach (var conflict in conflicts)
            Messages.Warning($"Extraction: {conflict}");
        Messages.Success($"Netlist written: {netlistPath}", netlistPath);
    }
    catch (Exception ex)
    {
        Messages.Error($"Netlist write failed: {ex.Message}");
        return;
    }

    // Open in the OS default editor (no analysis).
    try { OpenPathExternal(netlistPath); }
    catch (Exception ex) { Messages.Warning($"Could not open netlist externally: {ex.Message}"); }
}

private bool CanGenerateNetlist()
    => _factory.DocumentDock?.ActiveDockable is SchematicDocument;
```

Notes:
- Plain `[RelayCommand]` (synchronous, no undo involvement) → **not undoable** by construction.
- `WriteNetlist` already writes `netlist.cnl` to the workspace root (or the recovery session dir
  when no workspace is open) and is robust for an **empty** model (empty `TestBench` ⇒ header-only
  `.cnl`). No empty-schematic special-casing needed.
- Confirm `SchematicDocument.ActiveViewModel` exists (it does — hier2 nav stack) and is a
  `SchematicViewModel` exposing `.EditModel`. If for any reason you prefer to match `RunAnalysis`
  exactly, `activeDoc.ViewModel.EditModel` (the base) also works — but `ActiveViewModel` is the
  intended WYSIWYG target per the design doc.

## Step 2 — Refresh enablement on active-document change

`CanGenerateNetlist` depends on which dockable is active, so the command must re-query whenever
the active document changes. The central hook is `OnDocumentDockPropertyChanged` (fires on
`ActiveDockable` changes — it already retargets Properties/Analyses/undo). Add one line there:

```csharp
// near the end of OnDocumentDockPropertyChanged, alongside the other active-doc reactions
GenerateNetlistCommand.NotifyCanExecuteChanged();
```

This greys the menu item out for the Symbol Editor, cell parameter editor, Welcome stub, and when
no document is active — and re-enables it when a schematic tab becomes active. (Avalonia binds
`MenuItem.IsEnabled`/`NativeMenuItem.IsEnabled` to the command's `CanExecute`; the macOS native
item follows the same pattern the rest of the menu already relies on.)

## Step 3 — `OpenPathExternal` helper (factor from `OpenExternal`)

There is an existing `OpenExternal(ProjectTreeNodeViewModel node)` that opens `node.AbsolutePath`
with the OS default handler. Factor its OS-open logic into a path-based helper and have the node
overload delegate:

```csharp
/// <summary>Opens <paramref name="path"/> with the OS default application.</summary>
private static void OpenPathExternal(string path)
{
    if (OperatingSystem.IsMacOS())
        Process.Start("open", $"\"{path}\"");
    else if (OperatingSystem.IsWindows())
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    else // Linux / other
        Process.Start("xdg-open", $"\"{path}\"");
}
```

- Match the **existing** `OpenExternal` implementation exactly (same launcher per OS, same
  quoting/`ProcessStartInfo`). If `OpenExternal` already has this body, just extract it verbatim
  into `OpenPathExternal(string)` and change `OpenExternal(node)` to call
  `OpenPathExternal(node.AbsolutePath)`. `System.Diagnostics` and `System.Runtime.InteropServices`
  are already imported.
- Keep the existing `OpenExternal(node)` public/interface signature unchanged (other callers rely
  on it).

## Step 4 — Menu items

### macOS `NativeMenu` (Simulate section)

Insert **between** "Setup Analyses…" and the existing separator:

```xml
<NativeMenuItem Header="Setup Analyses…" Command="{Binding SetupAnalysesCommand}" CommandParameter="{Binding $parent[Window]}"/>
<NativeMenuItem Header="Generate Netlist" Command="{Binding GenerateNetlistCommand}"/>
<NativeMenuItemSeparator/>
<NativeMenuItem Header="Run"  Command="{Binding RunAnalysisCommand}"  Gesture="Meta+R"/>
<NativeMenuItem Header="Stop" Command="{Binding StopAnalysisCommand}" Gesture="Meta+OemPeriod"/>
```

### In-window `Menu` (Simulate section)

Insert after "Setup _Analyses…" and before the existing `<Separator/>`:

```xml
<MenuItem Header="Setup _Analyses…"
          Command="{Binding SetupAnalysesCommand}"
          CommandParameter="{Binding $parent[Window]}">
    <MenuItem.Icon><mi:MaterialIcon Kind="FormatListBulleted" Width="16" Height="16"/></MenuItem.Icon>
</MenuItem>
<MenuItem Header="_Generate Netlist"
          Command="{Binding GenerateNetlistCommand}">
    <MenuItem.Icon><mi:MaterialIcon Kind="FileExportOutline" Width="16" Height="16"/></MenuItem.Icon>
</MenuItem>
<Separator/>
<MenuItem Header="_Run" ... />   <!-- unchanged -->
```

No key gesture and no toolbar button (menu-only) for v1.

## Acceptance / manual test

- Open a workspace, open or create a schematic, place a couple of components.
  **Simulate → Generate Netlist** writes `netlist.cnl` to the workspace root, posts a clickable
  success message, and opens the file in the default editor. No analysis runs.
- An **empty** schematic still generates a header-only `netlist.cnl` and opens it.
- Switch to a Symbol Editor tab (or the cell parameter editor, or have no document) → the
  **Generate Netlist** menu item is **greyed out**. Switch back to a schematic → it re-enables.
- The command never appears in Undo history (it's not undoable).
- No regression to **Run** or **Setup Analyses**.

## Out of scope

- Hierarchical emission (cell `define` blocks) — arrives via brief-cnl-cells + brief-hier-extract
  + brief-run-wire; this command needs no change when they land.
