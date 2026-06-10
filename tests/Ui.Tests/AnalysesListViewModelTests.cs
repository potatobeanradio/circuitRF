using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Commands.Analysis;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;
using static CircuitRF.Ui.ViewModels.AnalysisEditorViewModel;

namespace CircuitRF.Ui.Tests;

// ── Layer 1 gate: AnalysesListViewModel + AnalysisRowViewModel ────────────────

/// <summary>
/// Verifies that <see cref="AnalysesListViewModel"/> reflects the active schematic's analyses
/// list, that all mutations (Remove/Duplicate/reorder/Enable) propagate to the model, that
/// Duplicate resolves name collisions, and that switching the active schematic rebinds.
/// </summary>
public sealed class AnalysesListViewModelTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SchematicViewModel MakeVm(params Analysis[] analyses)
    {
        var model = new SchematicEditModel();
        foreach (var a in analyses) model.Analyses.Add(a);
        return new SchematicViewModel(model, messageSink: null);
    }

    private static AnalysesListViewModel BindVm(SchematicViewModel schVm)
    {
        var vm = new AnalysesListViewModel();
        vm.SetActiveSchematic(schVm);
        return vm;
    }

    // ── Reflection: list mirrors the model ────────────────────────────────────

    [Fact]
    public void Rows_ReflectModelAnalyses()
    {
        var schVm = MakeVm(new DcAnalysis("DC1"), new DcAnalysis("DC2"));
        var vm    = BindVm(schVm);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("DC1", vm.Rows[0].Name);
        Assert.Equal("DC2", vm.Rows[1].Name);
    }

    [Fact]
    public void TypeLabel_CorrectPerType()
    {
        var sp  = new SParameterAnalysis("SP1", new FrequencySpec("1e9", "10e9", "1e8"));
        var hb  = new HarmonicBalanceAnalysis("HB1");
        var dc  = new DcAnalysis("DC1");
        var schVm = MakeVm(dc, sp, hb);
        var vm    = BindVm(schVm);

        Assert.Equal("DC", vm.Rows[0].TypeLabel);
        Assert.Equal("SP", vm.Rows[1].TypeLabel);
        Assert.Equal("HB", vm.Rows[2].TypeLabel);
    }

    [Fact]
    public void Summary_NonEmpty_ForEachType()
    {
        var sp  = new SParameterAnalysis("SP1", new FrequencySpec("1e9", "10e9", "1e8"));
        var hb  = new HarmonicBalanceAnalysis("HB1") { ToneExpr = "2e9", MaxHarmonicExpr = "7" };
        var dc  = new DcAnalysis("DC1");
        var schVm = MakeVm(dc, sp, hb);
        var vm    = BindVm(schVm);

        Assert.NotEmpty(vm.Rows[0].Summary);   // DC
        Assert.NotEmpty(vm.Rows[1].Summary);   // SP
        Assert.NotEmpty(vm.Rows[2].Summary);   // HB
    }

    // ── Enable toggle ─────────────────────────────────────────────────────────

    [Fact]
    public void Enable_Toggle_MutatesModelAndMarksCanUndo()
    {
        var a     = new DcAnalysis("DC1");
        var schVm = MakeVm(a);
        var vm    = BindVm(schVm);

        Assert.True(a.Enabled);

        vm.Rows[0].Enabled = false;

        Assert.False(a.Enabled);
        Assert.True(schVm.UndoRedo.CanUndo);
    }

    [Fact]
    public void Enable_Toggle_Undoable()
    {
        var a     = new DcAnalysis("DC1");
        var schVm = MakeVm(a);
        var vm    = BindVm(schVm);

        vm.Rows[0].Enabled = false;
        Assert.False(a.Enabled);

        schVm.UndoRedo.Undo();
        Assert.True(a.Enabled);
    }

    // ── Remove ────────────────────────────────────────────────────────────────

    [Fact]
    public void Remove_DeletesFromModelAndRows()
    {
        var schVm = MakeVm(new DcAnalysis("DC1"), new DcAnalysis("DC2"));
        var vm    = BindVm(schVm);
        vm.SelectedRow = vm.Rows[0];

        vm.RemoveCommand.Execute(null);

        Assert.Single(vm.Rows);
        Assert.Equal("DC2", vm.Rows[0].Name);
        Assert.Single(schVm.EditModel.Analyses);
    }

    [Fact]
    public void Remove_IsUndoable()
    {
        var a1    = new DcAnalysis("DC1");
        var schVm = MakeVm(a1, new DcAnalysis("DC2"));
        var vm    = BindVm(schVm);
        vm.SelectedRow = vm.Rows[0];

        vm.RemoveCommand.Execute(null);
        schVm.UndoRedo.Undo();

        Assert.Equal(2, schVm.EditModel.Analyses.Count);
        Assert.Equal("DC1", schVm.EditModel.Analyses[0].Name);
    }

    // ── Duplicate ─────────────────────────────────────────────────────────────

    [Fact]
    public void Duplicate_ClonesAfterSource()
    {
        var schVm = MakeVm(new DcAnalysis("DC1"), new DcAnalysis("DC2"));
        var vm    = BindVm(schVm);
        vm.SelectedRow = vm.Rows[0];   // DC1

        vm.DuplicateCommand.Execute(null);

        Assert.Equal(3, vm.Rows.Count);
        Assert.Equal("DC1",      vm.Rows[0].Name);
        Assert.Equal("DC1 copy", vm.Rows[1].Name);
        Assert.Equal("DC2",      vm.Rows[2].Name);
    }

    [Fact]
    public void Duplicate_ResolvesCopyNameCollision()
    {
        var schVm = MakeVm(new DcAnalysis("DC1"), new DcAnalysis("DC1 copy"));
        var vm    = BindVm(schVm);
        vm.SelectedRow = vm.Rows[0];

        vm.DuplicateCommand.Execute(null);

        Assert.Equal("DC1 copy 2", vm.Rows[1].Name);
    }

    [Fact]
    public void Duplicate_PreservesEnabled()
    {
        var a     = new DcAnalysis("DC1") { Enabled = false };
        var schVm = MakeVm(a);
        var vm    = BindVm(schVm);
        vm.SelectedRow = vm.Rows[0];

        vm.DuplicateCommand.Execute(null);

        Assert.False(vm.Rows[1].Analysis.Enabled);
    }

    [Fact]
    public void Duplicate_IsUndoable()
    {
        var schVm = MakeVm(new DcAnalysis("DC1"));
        var vm    = BindVm(schVm);
        vm.SelectedRow = vm.Rows[0];

        vm.DuplicateCommand.Execute(null);
        Assert.Equal(2, schVm.EditModel.Analyses.Count);

        schVm.UndoRedo.Undo();
        Assert.Single(schVm.EditModel.Analyses);
    }

    // ── Reorder ───────────────────────────────────────────────────────────────

    [Fact]
    public void MoveUp_SwapsWithPredecessor()
    {
        var schVm = MakeVm(new DcAnalysis("DC1"), new DcAnalysis("DC2"), new DcAnalysis("DC3"));
        var vm    = BindVm(schVm);
        vm.SelectedRow = vm.Rows[1];   // DC2

        vm.MoveUpCommand.Execute(null);

        Assert.Equal("DC2", schVm.EditModel.Analyses[0].Name);
        Assert.Equal("DC1", schVm.EditModel.Analyses[1].Name);
    }

    [Fact]
    public void MoveDown_SwapsWithSuccessor()
    {
        var schVm = MakeVm(new DcAnalysis("DC1"), new DcAnalysis("DC2"), new DcAnalysis("DC3"));
        var vm    = BindVm(schVm);
        vm.SelectedRow = vm.Rows[1];   // DC2

        vm.MoveDownCommand.Execute(null);

        Assert.Equal("DC3", schVm.EditModel.Analyses[1].Name);
        Assert.Equal("DC2", schVm.EditModel.Analyses[2].Name);
    }

    [Fact]
    public void MoveUp_NotAvailableAtTop()
    {
        var schVm = MakeVm(new DcAnalysis("DC1"), new DcAnalysis("DC2"));
        var vm    = BindVm(schVm);
        vm.SelectedRow = vm.Rows[0];

        Assert.False(vm.MoveUpCommand.CanExecute(null));
    }

    [Fact]
    public void MoveDown_NotAvailableAtBottom()
    {
        var schVm = MakeVm(new DcAnalysis("DC1"), new DcAnalysis("DC2"));
        var vm    = BindVm(schVm);
        vm.SelectedRow = vm.Rows[1];

        Assert.False(vm.MoveDownCommand.CanExecute(null));
    }

    // ── Active-schematic rebind ───────────────────────────────────────────────

    [Fact]
    public void SwitchActiveSchematic_Rebinds()
    {
        var schVm1 = MakeVm(new DcAnalysis("DC1"));
        var schVm2 = MakeVm(new SParameterAnalysis("SP1", new FrequencySpec("1e9", "10e9", 101)));
        var vm     = BindVm(schVm1);

        Assert.Equal("DC", vm.Rows[0].TypeLabel);

        vm.SetActiveSchematic(schVm2);

        Assert.Equal("SP", vm.Rows[0].TypeLabel);
    }

    [Fact]
    public void NullActiveSchematic_ShowsNoActiveSchematic()
    {
        var vm = new AnalysesListViewModel();
        vm.SetActiveSchematic(null);

        Assert.True(vm.NoActiveSchematic);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void EmptyList_IsEmptyTrue()
    {
        var schVm = MakeVm();
        var vm    = BindVm(schVm);

        Assert.False(vm.NoActiveSchematic);
        Assert.True(vm.IsEmpty);
    }

    // ── Model change: adding to the model outside the VM rebinds the rows ─────

    [Fact]
    public void ModelChange_ExternalAdd_RebuildsRows()
    {
        var schVm = MakeVm();
        var vm    = BindVm(schVm);
        Assert.Empty(vm.Rows);

        schVm.EditModel.Analyses.Add(new DcAnalysis("DC1"));
        schVm.EditModel.NotifyChanged();

        Assert.Single(vm.Rows);
    }

    // ── DuplicateAnalysisCommand.ResolveName (unit) ───────────────────────────

    [Fact]
    public void ResolveName_FirstCopy()
    {
        var analyses = new List<Analysis> { new DcAnalysis("DC1") };
        Assert.Equal("DC1 copy", DuplicateAnalysisCommand.ResolveName(analyses, "DC1"));
    }

    [Fact]
    public void ResolveName_SecondCopy()
    {
        var analyses = new List<Analysis> { new DcAnalysis("DC1"), new DcAnalysis("DC1 copy") };
        Assert.Equal("DC1 copy 2", DuplicateAnalysisCommand.ResolveName(analyses, "DC1"));
    }

    // ── Enabled round-trips via serialization ─────────────────────────────────

    [Fact]
    public void Enabled_False_RoundTripsViaSerialization()
    {
        var a = new DcAnalysis("DC1") { Enabled = false };
        var dto = Schematic.AnalysisSerialization.ToDto(a);
        Assert.False(dto.Enabled);

        var restored = Schematic.AnalysisSerialization.FromDto(dto);
        Assert.NotNull(restored);
        Assert.False(restored!.Enabled);
    }
}

// ── AnalysisEditorViewModel: NextFreeName + OnTypeChanged ─────────────────────

/// <summary>
/// Verifies that <see cref="AnalysisEditorViewModel.NextFreeName"/> returns the correct
/// typed prefix (DC/SP/HB/LP/LPP) and that switching the type picker auto-updates the Name
/// when the current name looks auto-generated.
/// </summary>
public sealed class AnalysisEditorViewModelNameTests
{
    private static SchematicEditModel EmptyModel() => new();

    private static SchematicEditModel ModelWith(params string[] names)
    {
        var m = new SchematicEditModel();
        foreach (var n in names) m.Analyses.Add(new DcAnalysis(n));
        return m;
    }

    // ── NextFreeName — correct prefix per type ────────────────────────────────

    [Theory]
    [InlineData(AnalysisKind.DC,  "DC1")]
    [InlineData(AnalysisKind.SP,  "SP1")]
    [InlineData(AnalysisKind.HB,  "HB1")]
    [InlineData(AnalysisKind.LP,  "LP1")]
    [InlineData(AnalysisKind.LPP, "LPP1")]
    public void NextFreeName_ReturnsCorrectPrefix(AnalysisKind kind, string expected)
        => Assert.Equal(expected, NextFreeName(kind, []));

    [Fact]
    public void NextFreeName_SkipsExisting()
    {
        Assert.Equal("DC2", NextFreeName(AnalysisKind.DC, ["DC1"]));
        Assert.Equal("SP3", NextFreeName(AnalysisKind.SP, ["SP1", "SP2"]));
        Assert.Equal("LPP2", NextFreeName(AnalysisKind.LPP, ["LPP1"]));
    }

    // ── Type-switch auto-name update ──────────────────────────────────────────

    [Fact]
    public void TypeSwitch_UpdatesAutoName_DC_to_HB()
    {
        var vm = new AnalysisEditorViewModel(EmptyModel(), AnalysisKind.DC);
        Assert.Equal("DC1", vm.Name);
        vm.Type = AnalysisKind.HB;
        Assert.Equal("HB1", vm.Name);
    }

    [Fact]
    public void TypeSwitch_UpdatesAutoName_DC_to_SP()
    {
        var vm = new AnalysisEditorViewModel(EmptyModel(), AnalysisKind.DC);
        vm.Type = AnalysisKind.SP;
        Assert.Equal("SP1", vm.Name);
    }

    [Fact]
    public void TypeSwitch_SkipsExistingNames()
    {
        var vm = new AnalysisEditorViewModel(ModelWith("HB1"), AnalysisKind.DC);
        vm.Type = AnalysisKind.HB;
        Assert.Equal("HB2", vm.Name); // HB1 already exists
    }

    [Fact]
    public void TypeSwitch_DoesNotOverrideCustomName()
    {
        var vm  = new AnalysisEditorViewModel(EmptyModel(), AnalysisKind.DC);
        vm.Name = "MyAmplifierDC";  // custom — letters only, no trailing digits
        vm.Type = AnalysisKind.HB;
        Assert.Equal("MyAmplifierDC", vm.Name); // preserved
    }

    [Fact]
    public void TypeSwitch_DoesNotOverrideCustomName_WithUnderscore()
    {
        var vm  = new AnalysisEditorViewModel(EmptyModel(), AnalysisKind.DC);
        vm.Name = "PA_sweep";
        vm.Type = AnalysisKind.SP;
        Assert.Equal("PA_sweep", vm.Name);
    }

    [Fact]
    public void TypeSwitch_MultipleChanges_TrackCorrectly()
    {
        var vm = new AnalysisEditorViewModel(EmptyModel(), AnalysisKind.DC);
        vm.Type = AnalysisKind.SP;
        Assert.Equal("SP1", vm.Name);
        vm.Type = AnalysisKind.HB;
        Assert.Equal("HB1", vm.Name);
        vm.Type = AnalysisKind.DC;
        Assert.Equal("DC1", vm.Name);
    }

    [Fact]
    public void EditExisting_TypeSwitch_UpdatesAutoName()
    {
        var sp = new SParameterAnalysis("SP1", new[] { new FrequencySpec("1e9", "10e9", "1e9") });
        var vm = new AnalysisEditorViewModel(EmptyModel(), sp);
        Assert.Equal("SP1", vm.Name);
        // SP1 is an auto-generated name pattern → switching type should update it
        vm.Type = AnalysisKind.HB;
        Assert.Equal("HB1", vm.Name);
    }
}
