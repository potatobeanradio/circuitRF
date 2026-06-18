# Sonnet Brief — Analyses list: group sweeps under their sim (indent + summary + move-with-root)

> Polishes the Analyses list in the Properties/Setup view (`AnalysesListView` + `AnalysisRowViewModel` +
> `AnalysesListViewModel`). Three changes: (1) sweep rows show a real summary instead of "?", (2) sweep rows
> are indented (incl. their Enable checkbox) so they read as grouped under their base sim, ordered inner→outer
> (already the model order), and (3) Up/Down reorder moves a whole chain (base + its sweeps) as a unit.
> Build 0W/0E (`TreatWarningsAsErrors=true`); tests green.

Context already true: a chain is stored contiguously in `model.Analyses` as `[base, innermost_sweep, …,
outermost_sweep]` (BuildAnalyses order), so sweeps already sit directly under their base, inner→outer. The list
rebuilds on every `EditModel.Changed`, so summaries refresh automatically after an edit — no INotify needed.

## Part A — `AnalysisRowViewModel.cs`: sweep label, name, summary, indent flag
```csharp
    public bool IsSweep => Analysis is ParametricSweepAnalysis;

    // Sweeps display the swept variable as the name (the underlying Analysis.Name stays the
    // auto-generated "DC1_sweep_Vgs", used by commands).
    public string Name => Analysis is ParametricSweepAnalysis psa ? psa.SweepVarName : Analysis.Name;

    public string TypeLabel => Analysis switch
    {
        DcAnalysis               => "DC",
        SParameterAnalysis       => "SP",
        HarmonicBalanceAnalysis  => "HB",
        ParametricSweepAnalysis  => "SW",
        _                        => "?",
    };
```
Add the sweep arm to `ComputeSummary` and a formatter:
```csharp
    private static string ComputeSummary(Core.Design.Analysis a) => a switch
    {
        DcAnalysis                 => "Operating point",
        SParameterAnalysis sp      => FormatSpSummary(sp),
        HarmonicBalanceAnalysis hb => FormatHbSummary(hb),
        ParametricSweepAnalysis ps => FormatSweepSummary(ps),
        _                          => "",
    };

    private static string FormatSweepSummary(ParametricSweepAnalysis psa)
    {
        var v = psa.SweepValues;
        if (v.Length == 0) return "(empty)";
        if (v.Length == 1) return $"1 pt: {FmtNum(v[0])}";
        return $"{v.Length} pts: {FmtNum(v[0])}…{FmtNum(v[^1])}";
    }

    private static string FmtNum(double v) =>
        v.ToString(System.Math.Abs(v) >= 1e6 || (System.Math.Abs(v) > 0 && System.Math.Abs(v) < 0.01)
            ? "G4" : "G6", CultureInfo.InvariantCulture);
```
(`CultureInfo` is already imported.)

## Part B — `AnalysesListView.axaml`: indent sweep rows (whole row, incl. checkbox)
The row template's outer `Border` currently has `Padding="4,3"`. Bind its left margin off `IsSweep` so the entire
row — checkbox included — shifts right for sweeps. Add a bool→Thickness converter (or reuse one if present).
Add to the view's resources:
```xml
    <UserControl.Resources>
        <vm:BoolToIndentConverter x:Key="IndentConv"/>
    </UserControl.Resources>
```
…and on the row Border:
```xml
                        <Border Padding="4,3"
                                Background="Transparent"
                                Margin="{Binding IsSweep, Converter={StaticResource IndentConv}}">
```
Add the converter (small file, e.g. `src/Ui/DataDisplay/Converters/` or `src/Ui/Converters/`):
```csharp
using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace CircuitRF.Ui.ViewModels;   // match the vm: xmlns used in the view, or add the right xmlns

public sealed class BoolToIndentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? new Thickness(20, 0, 0, 0) : new Thickness(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```
(Put it wherever keeps the `xmlns:vm` reference valid; if simpler, drop it next to existing converters and add
that xmlns to the view. A converter is cleaner than a second template, but if you prefer, a `DataTrigger`/style
selector on `IsSweep` setting `Margin` is equally fine.)

## Part C — move a whole chain as a unit
Replace the single-analysis move with a chain-aware move. A "chain block" is a base analysis followed by its
contiguous `ParametricSweepAnalysis` rows.

### C1. New command `src/Ui/Commands/Analysis/MoveAnalysisChainCommand.cs`
```csharp
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Analysis;

/// <summary>Moves the whole chain (base + its contiguous parametric sweeps) containing
/// <paramref name="member"/> up or down past the adjacent chain. Undo reverses it.</summary>
internal sealed class MoveAnalysisChainCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly Core.Design.Analysis _member;
    private readonly bool _moveUp;

    public string Description => _moveUp ? "Move analysis up" : "Move analysis down";

    public MoveAnalysisChainCommand(SchematicEditModel model, Core.Design.Analysis member, bool moveUp)
    { _model = model; _member = member; _moveUp = moveUp; }

    public void Execute() => Move(_moveUp);
    public void Undo()    => Move(!_moveUp);

    private void Move(bool up)
    {
        var list = _model.Analyses;
        int idx = list.IndexOf(_member);
        if (idx < 0) return;

        // Block containing _member: walk back to its base, forward over its sweeps.
        int start = idx;
        while (start > 0 && list[start] is ParametricSweepAnalysis) start--;
        int end = start;
        while (end + 1 < list.Count && list[end + 1] is ParametricSweepAnalysis) end++;

        if (up)
        {
            if (start == 0) return;
            int prevEnd = start - 1, prevStart = prevEnd;
            while (prevStart > 0 && list[prevStart] is ParametricSweepAnalysis) prevStart--;
            MoveRange(list, start, end, prevStart);            // block → before previous block
        }
        else
        {
            if (end + 1 >= list.Count) return;
            int nextStart = end + 1, nextEnd = nextStart;
            while (nextEnd + 1 < list.Count && list[nextEnd + 1] is ParametricSweepAnalysis) nextEnd++;
            MoveRange(list, nextStart, nextEnd, start);        // next block → before this block
        }
        _model.NotifyChanged();
    }

    private static void MoveRange(IList<Core.Design.Analysis> list, int from, int to, int insertAt)
    {
        var block = new List<Core.Design.Analysis>();
        for (int i = from; i <= to; i++) block.Add(list[i]);
        for (int i = to; i >= from; i--) list.RemoveAt(i);
        // insertAt was computed before removal; it is always < from here, so it is unaffected.
        for (int i = 0; i < block.Count; i++) list.Insert(insertAt + i, block[i]);
    }
}
```

### C2. `AnalysesListViewModel.cs` — use it; chain-aware CanExecute
Swap the two move commands to the chain command:
```csharp
    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp()
    {
        if (SelectedRow is null || _schematicVm is null) return;
        _schematicVm.Execute(new MoveAnalysisChainCommand(_schematicVm.EditModel, SelectedRow.Analysis, moveUp: true));
    }

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown()
    {
        if (SelectedRow is null || _schematicVm is null) return;
        _schematicVm.Execute(new MoveAnalysisChainCommand(_schematicVm.EditModel, SelectedRow.Analysis, moveUp: false));
    }
```
Make the guards chain-aware (there is a block before/after the selected row's block):
```csharp
    private bool CanMoveUp()
    {
        if (SelectedRow is null || _schematicVm is null) return false;
        var list = _schematicVm.EditModel.Analyses;
        int idx = list.IndexOf(SelectedRow.Analysis);
        if (idx < 0) return false;
        int start = idx;
        while (start > 0 && list[start] is ParametricSweepAnalysis) start--;
        return start > 0;                       // a block exists above
    }

    private bool CanMoveDown()
    {
        if (SelectedRow is null || _schematicVm is null) return false;
        var list = _schematicVm.EditModel.Analyses;
        int idx = list.IndexOf(SelectedRow.Analysis);
        if (idx < 0) return false;
        int end = idx;
        while (end > 0 && list[end] is ParametricSweepAnalysis &&
               (end - 1 < 0 || list[end - 1] is ParametricSweepAnalysis)) end--; // normalize into block
        // simpler: recompute block end from the base
        int start = idx;
        while (start > 0 && list[start] is ParametricSweepAnalysis) start--;
        int blockEnd = start;
        while (blockEnd + 1 < list.Count && list[blockEnd + 1] is ParametricSweepAnalysis) blockEnd++;
        return blockEnd + 1 < list.Count;       // a block exists below
    }
```
(Tidy the `CanMoveDown` to just the `start`/`blockEnd` computation — drop the stray first loop.) The old
`MoveAnalysisCommand.cs` can stay or be deleted; it's no longer referenced.

## Tests
- **AnalysisRowViewModel:** a `ParametricSweepAnalysis("DC1_sweep_Vgs","Vgs", spec, "DC1")` with 41 values
  0…120 → `TypeLabel=="SW"`, `Name=="Vgs"`, `Summary` starts `41 pts:` and contains `0` and `120`; `IsSweep`.
- **MoveAnalysisChainCommand:** model `[DC1, DC1_sweep_Vgs, SP1]` → MoveDown on `DC1` (or its sweep) → order
  becomes `[SP1, DC1, DC1_sweep_Vgs]` (the chain moved together); Undo restores. MoveUp on `SP1`'s row when it
  follows the chain → symmetric.
- **CanMove guards:** first block can't move up; last block can't move down.

## Gate (manual)
A DC with two sweeps shows three rows: `DC1` then indented `Vgs`/`Vds` rows with a "SW" badge and a
"41 pts: 0…120"-style summary (updates after editing the sweep). The checkbox sits inside the indent. Select the
DC (or a sweep) and press Up/Down — the whole group moves together; sweeps never detach from their base.

## On completion
Note in the nearest CLAUDE.md: the Analyses list renders parametric-sweep members indented under their base
with a live "N pts: a…b" summary, and Up/Down moves the whole chain as a unit (`MoveAnalysisChainCommand`).
