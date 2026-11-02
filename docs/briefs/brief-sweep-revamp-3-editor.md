# Sonnet Brief — Sweep revamp, Stage 3: unified editor (per-axis Enabled, reorder, build fix)

> Third of three (see `docs/design/parametric-sweep-ux.md`; Stages 1–2 landed). The Add/Edit Analysis
> dialog (`AnalysisEditorViewModel`) is already the unified model: a base type + an ordered `SweepAxes`
> list. This stage adds **per-axis Enabled**, **Up/Down reorder** of sweep axes, and fixes a **critical
> regression** in how the chain's Enabled flags are built. Build 0W/0E (`TreatWarningsAsErrors=true`); tests green.

## ★ Critical fix first — the build produces a dead chain post-Stage-2
`AnalysisEditorViewModel.BuildAnalyses` currently sets the base analysis to `Enabled = !hasSweeps && Enabled`
and each sweep to `Enabled = isLast && Enabled` (only the outermost sweep enabled; base + inner sweeps forced
`false`). That was a harmless dispatch hack pre-Stage-2. **Now Stage 2 collapses disabled sweeps and treats a
disabled base as inert**, so every swept analysis built by the editor resolves to a disabled base → **nothing
runs**. Fix: the base carries the dialog's Enabled; each sweep carries its own row's Enabled.

## Order convention (lock this)
`SweepAxes[0]` wraps the base first, so **`SweepAxes[0]` = innermost**. The dialog renders the list top→bottom =
index 0→N, so **top row = innermost = plot X axis** (matches the owner's "innermost = X" choice); rows below are
outer (slower) sweeps. Keep this build order; just label it.

## Part A — `SweepAxisRowViewModel.cs`: per-axis Enabled
Add an observable, default true:
```csharp
    // ── Enabled ───────────────────────────────────────────────────────────────
    /// <summary>When false, this axis collapses out of the result (its Start/Stop/Step is kept).</summary>
    [ObservableProperty] private bool _enabled = true;
```
In `FromPsa`, restore it (right after `vm.VarName = psa.SweepVarName;`):
```csharp
        vm.Enabled = psa.Enabled;
```
`FromLegacyHbSweep` leaves the default (true).

## Part B — `AnalysisEditorViewModel.cs`

### B1. Fix `BuildAnalyses` Enabled flags
Base analysis carries the dialog Enabled (not `!hasSweeps && Enabled`):
```csharp
        // Build the inner analysis. The base carries the dialog's Enabled flag; a disabled base makes
        // the whole chain inert (Stage 2). Each sweep axis carries its own row's Enabled below.
        Analysis? inner = Type switch
        {
            AnalysisKind.DC  => new DcAnalysis(name)            { Enabled = Enabled },
            AnalysisKind.SP  => BuildSp(name, Enabled),
            AnalysisKind.HB  => HbBody.BuildAnalysis(name,      Enabled),
            _                => null,
        };
```
Per-axis Enabled in the chain loop — delete the `isLast` line and set from the row:
```csharp
        for (int i = 0; i < SweepAxes.Count; i++)
        {
            var row      = SweepAxes[i];
            string varName = row.VarName.Trim();
            if (varName.Length == 0) return null;

            string sweepName = $"{name}_sweep_{varName}";
            ParametricSweepAnalysis psa;

            if (row.Mode == SweepAxisMode.List)
            {
                double[]? values = row.BuildValues();
                if (values is null || values.Length == 0) return null;
                psa = new ParametricSweepAnalysis(sweepName, varName, values, innerName);
            }
            else
            {
                var spec = row.BuildSpec();
                if (spec is null) return null;
                psa = new ParametricSweepAnalysis(sweepName, varName, spec, innerName);
                if (psa.SweepValues.Length == 0) return null;
            }

            psa.Enabled = row.Enabled;        // was: isLast && Enabled
            chain.Add(psa);
            innerName = sweepName;
        }
```
Keep the doc-comment on `BuildAnalyses` accurate (it currently claims "[inner (disabled), …]"): update to note the
base + each axis carry their own Enabled.

### B2. Fix the edit-restore base Enabled
In the "Edit existing" constructor, the dialog Enabled is the BASE flag now, not the outermost sweep's:
```csharp
        // Enabled now lives on the base analysis (each sweep axis has its own row Enabled).
        _enabled = inner.Enabled;
```
(Replaces `_enabled = sweepChain.Count > 0 ? sweepChain[^1].Enabled : inner.Enabled;`.)

### B3. Reorder commands (mirror `RemoveSweepAxis`)
```csharp
    [RelayCommand]
    private void MoveSweepAxisUp(SweepAxisRowViewModel row)
    {
        int i = SweepAxes.IndexOf(row);
        if (i > 0) SweepAxes.Move(i, i - 1);
    }

    [RelayCommand]
    private void MoveSweepAxisDown(SweepAxisRowViewModel row)
    {
        int i = SweepAxes.IndexOf(row);
        if (i >= 0 && i < SweepAxes.Count - 1) SweepAxes.Move(i, i + 1);
    }
```

## Part C — `SweepAxisRowView.axaml` + code-behind: checkbox + Up/Down
Add a shared button style in `UserControl.Styles` (mirrors the existing × look):
```xml
        <Style Selector="Button.sw-icon">
            <Setter Property="Background"      Value="Transparent"/>
            <Setter Property="BorderBrush"     Value="{DynamicResource SystemBaseMediumLowColor}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="CornerRadius"    Value="3"/>
            <Setter Property="Padding"         Value="6,2"/>
            <Setter Property="MinWidth"        Value="0"/>
            <Setter Property="FontSize"        Value="13"/>
            <Setter Property="Margin"          Value="0,0,2,0"/>
        </Style>
```
Replace Row 1's grid (the `ColumnDefinitions="*,Auto,Auto"` block) with an Enabled checkbox on the left and an
Up/Down/× button group on the right:
```xml
            <Grid ColumnDefinitions="Auto,*,Auto,Auto" ColumnSpacing="6">

                <CheckBox Grid.Column="0"
                          IsChecked="{Binding Enabled, Mode=TwoWay}"
                          VerticalAlignment="Bottom"
                          Margin="0,0,0,4"
                          ToolTip.Tip="Include this sweep axis (uncheck to drop this dimension from the result; Start/Stop/Step is kept)"/>

                <StackPanel Grid.Column="1" Spacing="2">
                    <TextBlock Text="Variable" FontSize="10" Opacity="0.6"/>
                    <AutoCompleteBox ItemsSource="{Binding KnownVarNames}"
                                     Text="{Binding VarName, Mode=TwoWay}"
                                     FontSize="11"
                                     FilterMode="Contains"
                                     MinimumPrefixLength="0"
                                     PlaceholderText="e.g. Pavl"/>
                    <TextBlock Text="{Binding VarNameError}"
                               FontSize="10"
                               Foreground="{DynamicResource CrfWarningBrush}"
                               IsVisible="{Binding HasVarNameError}"
                               TextWrapping="Wrap"/>
                </StackPanel>

                <StackPanel Grid.Column="2" Spacing="2" VerticalAlignment="Bottom">
                    <TextBlock Text="Spacing" FontSize="10" Opacity="0.6"/>
                    <StackPanel Orientation="Horizontal">
                        <Button Content="Lin" Classes="sw-btn" Classes.active="{Binding IsLinear}" Command="{Binding SetLinearCommand}"/>
                        <Button Content="Log" Classes="sw-btn" Classes.active="{Binding IsLog}"    Command="{Binding SetLogCommand}"/>
                    </StackPanel>
                </StackPanel>

                <StackPanel Grid.Column="3" Orientation="Horizontal" VerticalAlignment="Bottom">
                    <Button Content="↑" Classes="sw-icon" x:Name="MoveUpButton"
                            ToolTip.Tip="Move toward innermost (plot X axis)"/>
                    <Button Content="↓" Classes="sw-icon" x:Name="MoveDownButton"
                            ToolTip.Tip="Move toward outer (slower) sweep"/>
                    <Button Content="×" Classes="sw-icon" x:Name="RemoveButton"/>
                </StackPanel>
            </Grid>
```
In `SweepAxisRowView.axaml.cs`, wire the two new buttons exactly like `OnRemoveClick` (walk up to the
`AnalysisEditorViewModel`):
```csharp
        if (MoveUpButton   is Button up)   up.Click   += (_, _) => Invoke(vm => vm.MoveSweepAxisUpCommand);
        if (MoveDownButton is Button down) down.Click += (_, _) => Invoke(vm => vm.MoveSweepAxisDownCommand);
```
…or simply add `OnMoveUpClick`/`OnMoveDownClick` handlers copied from `OnRemoveClick`, calling
`editorVm.MoveSweepAxisUpCommand.Execute(row)` / `MoveSweepAxisDownCommand.Execute(row)`. Keep the existing
`OnRemoveClick`/`RemoveButton` wiring. Match the existing tree-walk pattern; do not add new DI.

## Part D — `AnalysisEditorDialog.axaml`: order hint
In the `Parametric Sweeps` Expander, above the `ItemsControl`, add a one-line hint:
```xml
                    <TextBlock Text="Top = innermost sweep = plot X axis. Rows below are outer (slower) sweeps."
                               FontSize="10" Opacity="0.6" TextWrapping="Wrap" Margin="0,0,0,2"/>
```

## Tests
- **AnalysisEditorViewModel.BuildAnalyses (Ui.Tests):** build a DC + 2 sweep axes (both rows Enabled) →
  assert the base `DcAnalysis.Enabled == true` AND both `ParametricSweepAnalysis.Enabled == true` (regression
  guard for the dead-chain bug). Disable the dialog Enabled → base `Enabled == false`. Disable one row →
  that psa `Enabled == false`, the other `true`.
- **Reorder:** add 3 axes, `MoveSweepAxisDownCommand.Execute(SweepAxes[0])` → order swapped; `BuildAnalyses`
  nesting reflects the new order (innermost = `SweepAxes[0]`).
- **Round-trip through run (Engine/Ui integration, optional but valuable):** build a 2-axis DC sweep via the
  editor VM → run via `SchematicRunService` → result cube has both sweep axes (confirms the fix end-to-end with
  Stage 2).

## Gate (manual)
Open Add Analysis → DC → add two sweep axes (Vgs, Vds) → both have an Enabled checkbox and ↑/↓/× buttons.
Reorder with ↑/↓ — the top row is the X axis. OK → run → 2-D family plots. Uncheck one axis's Enabled → run →
that dimension drops (the axis collapses), the other still sweeps, and its Start/Stop/Step is intact when you
reopen. Uncheck the dialog's base Enabled → run → nothing runs.

**One-time migration note for the owner:** swept analyses created before this fix carry the old Enabled pattern
(base + inner sweeps disabled). After Stage 2 they run nothing. Re-open each in the editor, make sure the base
Enabled and each axis's Enabled are checked, and click OK to rewrite the chain — or just delete and recreate.

## On completion
Note in the nearest CLAUDE.md: the analysis editor is the unified model — base type + ordered `SweepAxes` with
per-axis Enabled and ↑/↓ reorder; `SweepAxes[0]` is innermost (= plot X). `BuildAnalyses` writes the base's
Enabled from the dialog and each sweep's from its row (no more `isLast`/`!hasSweeps` hack). This completes the
parametric-sweep revamp (Stages 1–3).
