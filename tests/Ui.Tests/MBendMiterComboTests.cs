using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner-reported: "MBend seems to have no way to change the miter mode. (At least it's not
/// obvious.) Is there a way to use a combobox to select None, Fifty or Optimal? Make Optimal the
/// default." MBend's "Miter" parameter was a raw 0/1/2 number box with no indication it was really
/// a closed set of named modes — <see cref="ComponentTypeRegistry.EnumParamOptions"/> +
/// <see cref="ParameterRowViewModel"/>'s enum-combo support fixes that generically (reusable by any
/// future component with the same shape of parameter), and the default now seeds Optimal.
/// </summary>
public class MBendMiterComboTests
{
    private static (SchematicViewModel Vm, EditableComponent Comp, ParameterEditorViewModel Editor) MakeMBend(
        string miterExpr = "0")
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { Symbol = SymbolKind.MBend, InstanceName = "B1", X = 0, Y = 0 };
        comp.Parameters.Add(new EditableParameter { Name = "W", Expression = "2.9", Unit = "mm", ShowOnSchematic = true });
        comp.Parameters.Add(new EditableParameter { Name = "Angle", Expression = "90", Unit = "deg", ShowOnSchematic = true });
        comp.Parameters.Add(new EditableParameter { Name = "Miter", Expression = miterExpr, Unit = "", ShowOnSchematic = true });
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);
        return (vm, comp, editor);
    }

    private static ParameterRowViewModel MiterRow(ParameterEditorViewModel editor)
        => editor.Rows.Single(r => r.Name == "Miter");

    // ── Default value ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultParameters_MBend_Miter_DefaultsToOptimal()
    {
        var dp = ComponentTypeRegistry.DefaultParameters(SymbolKind.MBend, 0).First(p => p.Name == "Miter");
        Assert.Equal("2", dp.Expression); // 2 = Optimal, per MicrostripBendMiter's own enum order
    }

    // ── The registry mechanism itself ───────────────────────────────────────────────────────────

    [Fact]
    public void EnumParamOptions_MBendMiter_ReturnsNoneFiftyOptimal_InEnumOrder()
    {
        var options = ComponentTypeRegistry.EnumParamOptions(SymbolKind.MBend, "Miter");
        Assert.NotNull(options);
        Assert.Equal(new[] { "None", "Fifty", "Optimal" }, options);
    }

    [Fact]
    public void EnumParamOptions_UnrelatedParameterOrComponent_ReturnsNull()
    {
        Assert.Null(ComponentTypeRegistry.EnumParamOptions(SymbolKind.MBend, "W"));
        Assert.Null(ComponentTypeRegistry.EnumParamOptions(SymbolKind.Resistor, "R"));
    }

    // ── The row VM: shows a combo, not a text box, for Miter ────────────────────────────────────

    [Fact]
    public void MiterRow_IsAnEnumParam_WithTheRightOptionsAndIndex()
    {
        var (_, _, editor) = MakeMBend(miterExpr: "1");
        var row = MiterRow(editor);

        Assert.True(row.IsEnumParam);
        Assert.NotNull(row.EnumOptions);
        Assert.Equal(3, row.EnumOptions!.Count);
        Assert.Equal(1, row.SelectedEnumIndex); // "1" = Fifty
    }

    [Fact]
    public void OtherRows_AreNotEnumParams()
    {
        var (_, _, editor) = MakeMBend();
        var wRow = editor.Rows.Single(r => r.Name == "W");
        var angleRow = editor.Rows.Single(r => r.Name == "Angle");

        Assert.False(wRow.IsEnumParam);
        Assert.False(angleRow.IsEnumParam);
        Assert.Null(wRow.EnumOptions);
    }

    [Fact]
    public void FreshlyPlacedMBend_DefaultRow_SelectsOptimal()
    {
        var (_, _, editor) = MakeMBend(miterExpr: "2"); // matches the new DefaultParameters value
        var row = MiterRow(editor);
        Assert.Equal(2, row.SelectedEnumIndex);
    }

    // ── Selecting a combo option commits to the model, undoably ─────────────────────────────────

    [Fact]
    public void SelectingFifty_CommitsExpressionEqualsOne()
    {
        var (_, comp, editor) = MakeMBend(miterExpr: "0");
        var row = MiterRow(editor);

        row.SelectedEnumIndex = 1; // Fifty

        Assert.Equal("1", comp.Parameters.Single(p => p.Name == "Miter").Expression);
    }

    [Fact]
    public void SelectingOptimal_CommitsExpressionEqualsTwo()
    {
        var (_, comp, editor) = MakeMBend(miterExpr: "0");
        var row = MiterRow(editor);

        row.SelectedEnumIndex = 2; // Optimal

        Assert.Equal("2", comp.Parameters.Single(p => p.Name == "Miter").Expression);
    }

    [Fact]
    public void SelectingSameOption_IsANoOp_PushesNoUndoEntry()
    {
        var (vm, comp, editor) = MakeMBend(miterExpr: "0");
        var row = MiterRow(editor);
        bool couldUndoBefore = vm.UndoRedo.CanUndo;

        row.SelectedEnumIndex = 0; // already None — no-op

        Assert.Equal("0", comp.Parameters.Single(p => p.Name == "Miter").Expression);
        Assert.Equal(couldUndoBefore, vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void SelectingAnOption_IsUndoable()
    {
        var (vm, comp, editor) = MakeMBend(miterExpr: "0");
        var row = MiterRow(editor);

        row.SelectedEnumIndex = 2; // -> Optimal
        Assert.Equal("2", comp.Parameters.Single(p => p.Name == "Miter").Expression);

        vm.UndoRedo.Undo();
        Assert.Equal("0", comp.Parameters.Single(p => p.Name == "Miter").Expression);
    }

    [Fact]
    public void Undo_RefreshesTheRowsSelectedIndex()
    {
        var (vm, _, editor) = MakeMBend(miterExpr: "0");
        var row = MiterRow(editor);

        row.SelectedEnumIndex = 2;
        vm.UndoRedo.Undo();

        // Undo fires EditModel.Changed -> OnModelChanged -> RefreshFromModel on existing rows
        // (the parameter NAME set is unchanged, so no full rebuild is needed here).
        Assert.Equal(0, MiterRow(editor).SelectedEnumIndex);
    }

    [Fact]
    public void OutOfRangeOrUnparsableExpression_FallsBackToFirstOption_NeverThrows()
    {
        var (_, _, editor) = MakeMBend(miterExpr: "99");
        Assert.Equal(0, MiterRow(editor).SelectedEnumIndex);

        var (_, _, editor2) = MakeMBend(miterExpr: "not-a-number");
        Assert.Equal(0, MiterRow(editor2).SelectedEnumIndex);
    }

    // ── EnumIndexReadout: the subtle read-only numeric readout next to the combo ────────────────
    // Owner's own follow-up: keep the schematic label/inline text-edit showing the raw index (it
    // can only ever be typed/edited as a number there, no combo on the canvas), and instead add a
    // read-only readout in the Parameter Editor that makes the combo's underlying number visible.

    [Theory]
    [InlineData("0", "0")]
    [InlineData("1", "1")]
    [InlineData("2", "2")]
    public void EnumIndexReadout_ShowsTheUnderlyingNumber_MatchingTheSelectedOption(string miterExpr, string expectedReadout)
    {
        var (_, _, editor) = MakeMBend(miterExpr);
        Assert.Equal(expectedReadout, MiterRow(editor).EnumIndexReadout);
    }

    [Fact]
    public void EnumIndexReadout_IsEmpty_ForNonEnumParameters()
    {
        var (_, _, editor) = MakeMBend();
        var wRow = editor.Rows.Single(r => r.Name == "W");
        Assert.Equal("", wRow.EnumIndexReadout);
    }

    [Fact]
    public void EnumIndexReadout_UpdatesLiveWhenTheComboSelectionChanges()
    {
        var (_, _, editor) = MakeMBend(miterExpr: "0");
        var row = MiterRow(editor);
        Assert.Equal("0", row.EnumIndexReadout);

        row.SelectedEnumIndex = 2; // Optimal

        Assert.Equal("2", row.EnumIndexReadout);
    }

    [Fact]
    public void EnumIndexReadout_UpdatesOnUndo()
    {
        var (vm, _, editor) = MakeMBend(miterExpr: "0");
        var row = MiterRow(editor);

        row.SelectedEnumIndex = 1;
        Assert.Equal("1", row.EnumIndexReadout);

        vm.UndoRedo.Undo();

        Assert.Equal("0", row.EnumIndexReadout);
    }

    // ── The on-schematic label deliberately still shows the raw numeric index ──────────────────
    // Reverted per owner's explicit preference: the label's inline text-edit only accepts a raw
    // number (no combo on the canvas), so it must stay consistent with what can actually be typed
    // back into it.

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    public void OnSchematicLabel_StillShowsTheRawNumericIndex_NotTheNamedOption(string miterExpr)
    {
        var comp = new EditableComponent { Symbol = SymbolKind.MBend, InstanceName = "B1", X = 0, Y = 0 };
        comp.Parameters.Add(new EditableParameter { Name = "Miter", Expression = miterExpr, Unit = "", ShowOnSchematic = true });

        var rendered = comp.ToRenderComponent();

        Assert.Contains($"Miter = {miterExpr}", rendered.Labels);
    }
}
