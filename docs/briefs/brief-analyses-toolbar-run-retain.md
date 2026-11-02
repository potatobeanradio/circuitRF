# Sonnet Brief — Analyses inspector: toolbar row, Run button, retain last schematic

Three UX changes to the non-modal Analyses panel. Build 0W/0E (`TreatWarningsAsErrors=true`); tests green.
Land after `brief-analyses-copy-paste-chains` + `brief-sweep-card-reorder` (no conflict, but they touch the same VM).

## #4 + #5 — `src/Ui/Views/Analyses/AnalysesListView.axaml`
Give the schematic name its own row, move the button toolbar to the row below it, and add a Run icon button to
the left of the name. Replace the outer `RowDefinitions` and the first two row blocks.

Change the outer grid:
```xml
    <Grid RowDefinitions="Auto,Auto,*,Auto">
```
Then **replace the entire existing `<!-- Toolbar -->` Grid (Grid.Row="0")** with these two rows. Row 0 = Run +
name; Row 1 = the toolbar (same buttons as today, just relocated and with the HeaderLabel removed):
```xml
        <!-- Schematic name + Run -->
        <Grid Grid.Row="0" ColumnDefinitions="Auto,*" Margin="4,4,4,1">
            <Button Grid.Column="0"
                    Command="{Binding RunCommand}"
                    ToolTip.Tip="Run analysis"
                    Padding="3,2" Background="Transparent" BorderThickness="0" Margin="0,0,5,0">
                <mi:MaterialIcon Kind="Play" Width="15" Height="15"
                                 Foreground="{DynamicResource SystemBaseMediumColor}"/>
            </Button>
            <TextBlock Grid.Column="1"
                       Text="{Binding HeaderLabel}"
                       FontWeight="SemiBold" FontSize="12"
                       TextTrimming="CharacterEllipsis"
                       VerticalAlignment="Center"/>
        </Grid>

        <!-- Toolbar -->
        <Grid Grid.Row="1" ColumnDefinitions="Auto,*,Auto" Margin="4,0,4,2">
            <StackPanel Grid.Column="0" Orientation="Horizontal" Spacing="0">

                <Button Command="{Binding AddCommand}" CommandParameter="{Binding $parent[Window]}"
                        ToolTip.Tip="Add analysis" Padding="3,2" Background="Transparent" BorderThickness="0" Margin="0,0,1,0">
                    <mi:MaterialIcon Kind="Plus" Width="14" Height="14" Foreground="{DynamicResource SystemBaseMediumColor}"/>
                </Button>
                <Button Command="{Binding EditCommand}" CommandParameter="{Binding $parent[Window]}"
                        ToolTip.Tip="Edit selected analysis" Padding="3,2" Background="Transparent" BorderThickness="0" Margin="0,0,1,0">
                    <mi:MaterialIcon Kind="Pencil" Width="14" Height="14" Foreground="{DynamicResource SystemBaseMediumColor}"/>
                </Button>
                <Button Command="{Binding DuplicateCommand}"
                        ToolTip.Tip="Duplicate selected analysis" Padding="3,2" Background="Transparent" BorderThickness="0" Margin="0,0,1,0">
                    <mi:MaterialIcon Kind="ContentCopy" Width="14" Height="14" Foreground="{DynamicResource SystemBaseMediumColor}"/>
                </Button>
                <Button Command="{Binding RemoveCommand}"
                        ToolTip.Tip="Remove selected analysis" Padding="3,2" Background="Transparent" BorderThickness="0" Margin="0,0,2,0">
                    <mi:MaterialIcon Kind="Delete" Width="14" Height="14" Foreground="{DynamicResource SystemBaseMediumColor}"/>
                </Button>
                <Button Command="{Binding MoveUpCommand}"
                        ToolTip.Tip="Move up (inner / earlier)" Padding="3,2" Background="Transparent" BorderThickness="0" Margin="0,0,1,0">
                    <mi:MaterialIcon Kind="ChevronUp" Width="14" Height="14" Foreground="{DynamicResource SystemBaseMediumColor}"/>
                </Button>
                <Button Command="{Binding MoveDownCommand}"
                        ToolTip.Tip="Move down (outer / later)" Padding="3,2" Background="Transparent" BorderThickness="0" Margin="0,0,4,0">
                    <mi:MaterialIcon Kind="ChevronDown" Width="14" Height="14" Foreground="{DynamicResource SystemBaseMediumColor}"/>
                </Button>

                <Border Width="1" Height="12" Margin="0,0,4,0" VerticalAlignment="Center"
                        Background="{DynamicResource SystemBaseLowColor}"/>

                <Button Command="{Binding CopyCommand}" CommandParameter="{Binding $parent[Window]}"
                        ToolTip.Tip="Copy selected analyses (⌘C / Ctrl+C)" Padding="3,2" Background="Transparent" BorderThickness="0" Margin="0,0,1,0">
                    <mi:MaterialIcon Kind="ContentCopy" Width="14" Height="14" Foreground="{DynamicResource SystemBaseMediumColor}"/>
                </Button>
                <Button Command="{Binding CopyAllCommand}" CommandParameter="{Binding $parent[Window]}"
                        ToolTip.Tip="Copy all analyses" Padding="3,2" Background="Transparent" BorderThickness="0" Margin="0,0,1,0">
                    <mi:MaterialIcon Kind="SelectAll" Width="14" Height="14" Foreground="{DynamicResource SystemBaseMediumColor}"/>
                </Button>
                <Button Command="{Binding PasteCommand}" CommandParameter="{Binding $parent[Window]}"
                        ToolTip.Tip="Paste analyses (⌘V / Ctrl+V)" Padding="3,2" Background="Transparent" BorderThickness="0" Margin="0,0,4,0">
                    <mi:MaterialIcon Kind="ContentPaste" Width="14" Height="14" Foreground="{DynamicResource SystemBaseMediumColor}"/>
                </Button>

                <Border Width="1" Height="12" Margin="0,0,4,0" VerticalAlignment="Center"
                        Background="{DynamicResource SystemBaseLowColor}"/>

                <Button Command="{Binding InsertFromTemplateCommand}" CommandParameter="{Binding $parent[Window]}"
                        ToolTip.Tip="Insert from Template…" Padding="3,2" Background="Transparent" BorderThickness="0" Margin="0,0,1,0">
                    <mi:MaterialIcon Kind="FileImportOutline" Width="14" Height="14" Foreground="{DynamicResource SystemBaseMediumColor}"/>
                </Button>
                <Button Command="{Binding SaveAsTemplateCommand}" CommandParameter="{Binding $parent[Window]}"
                        ToolTip.Tip="Save as Template…" Padding="3,2" Background="Transparent" BorderThickness="0">
                    <mi:MaterialIcon Kind="BookmarkPlusOutline" Width="14" Height="14" Foreground="{DynamicResource SystemBaseMediumColor}"/>
                </Button>
            </StackPanel>

            <Button Grid.Column="2" Click="OnHelp"
                    ToolTip.Tip="Help" Padding="3,2" Background="Transparent" BorderThickness="0">
                <mi:MaterialIcon Kind="HelpCircleOutline" Width="14" Height="14" Foreground="{DynamicResource SystemBaseMediumColor}"/>
            </Button>
        </Grid>
```
Then bump the two remaining rows down by one: the **List area** Grid `Grid.Row="1"` → `Grid.Row="2"`, and the
**Footer hint** TextBlock `Grid.Row="2"` → `Grid.Row="3"`. (The `OnHelp` handler is unchanged.)

## #5 — `AnalysesListViewModel`: Run command + event
The panel doesn't own the run pipeline (netlist write, engine, results, display reload lives in
WorkspaceViewModel), so the button raises an event the workspace handles.
```csharp
    /// <summary>Raised when the Run button is pressed; WorkspaceViewModel runs the retained schematic.</summary>
    public event Action? RunRequested;

    [RelayCommand(CanExecute = nameof(HasActiveSchematic))]
    private void Run() => RunRequested?.Invoke();
```
Wherever the other schematic-gated commands get refreshed (the spot that calls
`AddCommand.NotifyCanExecuteChanged()` etc. after `SetActiveSchematic`/RebuildRows), add
`RunCommand.NotifyCanExecuteChanged();` so the button enables/disables with the active schematic.

## #6 — `WorkspaceViewModel`: retain last schematic + run it
The Analyses panel currently blanks because `OnDocumentDockPropertyChanged` passes `null` to
`SetActiveSchematic` for non-schematic docs. Track the last schematic document and only update on a schematic.

1. Field (near `_activeUndoTarget`):
```csharp
    // Last schematic document made active — kept so the Analyses panel + Run button survive focusing a
    // data display / symbol / cell tab. Cleared when this doc is closed or the workspace changes.
    private SchematicDocument? _lastActiveSchematicDoc;
```

2. In `OnDocumentDockPropertyChanged`, **replace** the existing Analyses block:
```csharp
        // Analyses panel — tracks only schematics.
        var schematicVm = activeDockable is SchematicDocument sd ? sd.ViewModel : null;
        string? schName = activeDockable is SchematicDocument sdName && sdName.FilePath is { } fp
            ? System.IO.Path.GetFileName(fp) : null;
        _factory.AnalysesTool?.SetActiveSchematic(schematicVm, schName);
```
with retain-on-schematic-only:
```csharp
        // Analyses panel — retain the last schematic so focusing a data display / symbol / cell tab
        // does NOT blank it. Only update when a schematic document becomes active.
        if (activeDockable is SchematicDocument sd)
        {
            _lastActiveSchematicDoc = sd;
            string? schName = sd.FilePath is { } fp ? System.IO.Path.GetFileName(fp) : sd.Id;
            _factory.AnalysesTool?.SetActiveSchematic(sd.ViewModel, schName);
        }
```

3. Refactor `RunAnalysis` so the body can run any schematic doc, and fall back to the retained one:
```csharp
    [RelayCommand]
    private async Task RunAnalysis()
    {
        var doc = (_factory.DocumentDock?.ActiveDockable as SchematicDocument) ?? _lastActiveSchematicDoc;
        if (doc is null) { Messages.Warning("Run: no schematic is active."); return; }
        await RunSchematicDocAsync(doc);
    }

    private async Task RunSchematicDocAsync(SchematicDocument activeDoc)
    {
        // …existing RunAnalysis body verbatim, starting at `var testBenchName = activeDoc.Id;`…
    }
```
(Move everything after the old null-check into `RunSchematicDocAsync`; the body already uses `activeDoc`.)

4. Run-request wiring. The AnalysesTool + its ListVm are **new instances** after every `CreateDefaultLayout`, so
re-wire alongside the existing `OnDocumentDockPropertyChanged` re-subscriptions:
```csharp
    private void WireAnalysesRun()
    {
        if (_factory.AnalysesTool?.ListVm is { } listVm)
        {
            listVm.RunRequested -= OnAnalysesRunRequested;
            listVm.RunRequested += OnAnalysesRunRequested;
        }
    }

    private void OnAnalysesRunRequested()
    {
        var doc = (_factory.DocumentDock?.ActiveDockable as SchematicDocument) ?? _lastActiveSchematicDoc;
        if (doc is null) { Messages.Warning("Run: no schematic available."); return; }
        _ = RunSchematicDocAsync(doc);
    }
```
Call `WireAnalysesRun();` in **three** spots — immediately after each
`newNpc.PropertyChanged += OnDocumentDockPropertyChanged;` (or the ctor's `npc.PropertyChanged += …`):
the **constructor**, **NewWorkspace**, and **SwitchToWorkspace**.

5. Reset on workspace change: in the clear blocks of **NewWorkspace** and **SwitchToWorkspace** (where
`_openDocsByPath.Clear(); … _registry.Clear();` run), add `_lastActiveSchematicDoc = null;`.

6. Clear on close: in `OnDockableClosed`, after the existing cleanup, add:
```csharp
        if (ReferenceEquals(dockable, _lastActiveSchematicDoc))
        {
            _lastActiveSchematicDoc = null;
            _factory.AnalysesTool?.SetActiveSchematic(null);
        }
```
(If the retained schematic tab is closed the panel blanks — acceptable; the schematic is gone.)

## Tests
- `AnalysesListViewModel`: `RunCommand.CanExecute` is false with no schematic, true after `SetActiveSchematic(vm)`;
  invoking it fires `RunRequested`.
- (No engine test — run pipeline already covered.)

## Gate (manual)
1. Open a schematic, add analyses. Toolbar sits on its own row; the full schematic name shows on the row above.
2. Open a data display tab and focus it — the Analyses panel keeps showing the schematic's analyses (no "Open a
   schematic…" message). Edit an analysis, press the Run ▶ button → engine runs, data display refreshes, all
   without switching tabs.
3. Focus a `.csym` / cell tab — panel still retained. Close the schematic tab → panel blanks.

## On completion
Note in the nearest CLAUDE.md: the Analyses panel retains `_lastActiveSchematicDoc` (only schematic docs update
it; cleared on close/workspace-change); the panel's Run ▶ button raises `AnalysesListViewModel.RunRequested`,
wired by `WireAnalysesRun` to `RunSchematicDocAsync` on the retained doc; toolbar moved to its own row under the
schematic name.
