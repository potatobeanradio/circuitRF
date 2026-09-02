// ================================================================
//  SpiceModelNameLabelTests.cs — the SPICE component's Name on the sheet (2026-09-01)
//
//  Owner: the SPICE component's Name parameter is to be rendered on the schematic,
//  but not editable with the inline text editor — the Parameter dialog's Name combo
//  is the only place to set it — and the dialog is to carry the standard
//  "Show in schematic" checkbox for it.
//
//  Name has no generic parameter row (SpiceModelSymbolProvider.IsPanelParameter keeps
//  the panel's own five out of the row list), so it had no checkbox to carry the flag
//  and no reason for the inline editor to refuse it. The inline editor is the wrong
//  control for it regardless: it writes the typed string straight through
//  EditParameterCommand, so a typo leaves an instance naming a definition the file
//  does not hold, with the symbol still drawn for the old one.
// ================================================================

using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class SpiceModelNameLabelTests
{
    private const string Definition = "QN2222";

    private static (SchematicEditModel Model, EditableComponent Comp) Placed()
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent
        {
            InstanceName     = "X1",
            Symbol           = SymbolKind.SpiceModel,
            X = 0, Y = 0,
            ShowTypeLabel    = true,
            ShowInstanceName = true,
        };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.SpiceModel, 2))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });

        Param(comp, SpiceModelSymbolProvider.FileParameter).Expression = "models/bjt.lib";
        Param(comp, SpiceModelSymbolProvider.NameParameter).Expression = Definition;

        model.Components.Add(comp);
        return (model, comp);
    }

    private static EditableParameter Param(EditableComponent c, string name)
        => c.Parameters.First(p => p.Name == name);

    private static int IndexOf(EditableComponent c, string name)
        => c.Parameters.ToList().FindIndex(p => p.Name == name);

    // ── Rendered ─────────────────────────────────────────────────────────────

    /// <summary>Name draws its own label — the row the checkbox below toggles.</summary>
    [Fact]
    public void TheNameParameter_RendersOnTheSchematic()
    {
        var (model, comp) = Placed();
        var (render, _) = model.BuildRenderModel();

        Assert.Contains(comp.LabelParameters(), p => p.Name == SpiceModelSymbolProvider.NameParameter);
        Assert.Contains($"{SpiceModelSymbolProvider.NameParameter} = {Definition}",
                        render.Components[0].Labels);
    }

    /// <summary>Cleared, it draws nothing — and nothing else on the component moves out from under it.</summary>
    [Fact]
    public void WithTheFlagCleared_TheNameLabelIsGone()
    {
        var (model, comp) = Placed();
        Param(comp, SpiceModelSymbolProvider.NameParameter).ShowOnSchematic = false;

        var (render, _) = model.BuildRenderModel();

        Assert.DoesNotContain($"{SpiceModelSymbolProvider.NameParameter} = {Definition}",
                              render.Components[0].Labels);
        // The type label still names the definition — that is a different label and unaffected.
        Assert.Equal(Definition, render.Components[0].Labels[0]);
    }

    // ── Not inline-editable ──────────────────────────────────────────────────

    /// <summary>The label is clickable — this is what makes the refusal below a real refusal and
    /// not a hit test that missed.</summary>
    [Fact]
    public void TheNameLabel_IsHitAsAParameterLabel()
    {
        var (model, comp) = Placed();
        var (render, index) = model.BuildRenderModel();

        var hit = HitNameLabel(model, render, index, comp);

        Assert.Equal(SchematicHitTest.HitKind.ComponentParam, hit.Kind);
        Assert.Equal(IndexOf(comp, SpiceModelSymbolProvider.NameParameter), hit.SubIndex);
    }

    [Fact]
    public void DoubleClickingTheNameLabel_StartsNoInlineEdit()
    {
        var (model, comp) = Placed();
        var (render, index) = model.BuildRenderModel();
        var vm = new SchematicViewModel(model);

        vm.BeginInlineEditForHit(HitNameLabel(model, render, index, comp), 0, 0);

        Assert.False(vm.IsInlineEditing);
    }

    /// <summary>Every parameter the panel owns, for the same reason — each is answered by a picker,
    /// a browse button or a closed vocabulary, none of which free text can stand in for.</summary>
    [Theory]
    [InlineData("File")]
    [InlineData("Name")]
    [InlineData("Section")]
    [InlineData("PinConfig")]
    [InlineData("Pitch")]
    public void NoPanelParameter_IsInlineEditable(string paramName)
    {
        var (model, comp) = Placed();
        var vm = new SchematicViewModel(model);

        vm.BeginInlineEditForHit(
            new SchematicHitTest.HitResult(
                SchematicHitTest.HitKind.ComponentParam, comp.Id, IndexOf(comp, paramName)),
            0, 0);

        Assert.False(vm.IsInlineEditing);
    }

    /// <summary>The refusal is scoped: a subcircuit's OWN declared parameter is an ordinary value
    /// and stays editable in place, as it is on every other component.</summary>
    [Fact]
    public void ADeclaredSubcircuitParameter_IsStillInlineEditable()
    {
        var (model, comp) = Placed();
        comp.Parameters.Add(new EditableParameter { Name = "BF", Expression = "150", ShowOnSchematic = true });
        var vm = new SchematicViewModel(model);

        vm.BeginInlineEditForHit(
            new SchematicHitTest.HitResult(
                SchematicHitTest.HitKind.ComponentParam, comp.Id, IndexOf(comp, "BF")),
            0, 0);

        Assert.True(vm.IsInlineEditing);
    }

    // ── The dialog's checkbox ────────────────────────────────────────────────

    /// <summary>The panel's checkbox writes the SAME flag the generic rows' one does, through the
    /// same undoable command — not a second setting that could disagree with the sheet.</summary>
    [Fact]
    public void ThePanelCheckbox_TogglesTheNameParametersOwnFlag()
    {
        var (model, comp) = Placed();
        var vm  = new SchematicViewModel(model);
        var ped = new ParameterEditorViewModel();
        ped.SetTargetDirect(vm, comp, showClose: false);

        Assert.True(ped.SpiceModelShowNameOnSchematic);

        ped.SpiceModelShowNameOnSchematic = false;
        Assert.False(Param(comp, SpiceModelSymbolProvider.NameParameter).ShowOnSchematic);

        vm.UndoRedo.Undo();
        Assert.True(Param(comp, SpiceModelSymbolProvider.NameParameter).ShowOnSchematic);
    }

    /// <summary>Opening the dialog on an instance whose label is already off shows the box clear —
    /// a readout of the instance, not a default.</summary>
    [Fact]
    public void ThePanelCheckbox_ReadsBackTheInstancesOwnFlag()
    {
        var (model, comp) = Placed();
        Param(comp, SpiceModelSymbolProvider.NameParameter).ShowOnSchematic = false;

        var ped = new ParameterEditorViewModel();
        ped.SetTargetDirect(new SchematicViewModel(model), comp, showClose: false);

        Assert.False(ped.SpiceModelShowNameOnSchematic);
    }

    private static SchematicHitTest.HitResult HitNameLabel(
        SchematicEditModel model, SchematicModel render, SchematicSpatialIndex index,
        EditableComponent comp)
    {
        // Row 0 = type, row 1 = instance name, then the rendered parameters in order.
        var shown = comp.LabelParameters().Where(p => p.Expression.Length > 0).ToList();
        int row = 2 + shown.FindIndex(p => p.Name == SpiceModelSymbolProvider.NameParameter);

        var (baseX, _, bandTop, bandBot) = SchematicComponent.LabelRowGeometry(
            comp.X, comp.Y, row, 0, 0, comp.Symbol, comp.PortCount,
            render.Components[0].GlyphBbMaxY - comp.Y);

        return SchematicHitTest.Test(model, render, index,
                                     baseX + 5, (bandTop + bandBot) * 0.5, includeLabels: true);
    }
}
