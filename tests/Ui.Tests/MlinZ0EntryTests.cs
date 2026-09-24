using System.Globalization;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// MLIN's Z0 field, schematic and layout: it shows the static Z0 of the stored W, and a typed Z0
/// synthesises W and writes it — W stays the one stored parameter (ParameterEditorViewModel.MlinImpedance.cs).
/// </summary>
public sealed class MlinZ0EntryTests : IDisposable
{
    private readonly string _root;

    public MlinZ0EntryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-mlin-z0-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static (SchematicViewModel Vm, EditableComponent Comp, ParameterEditorViewModel Editor) MakeMlin(string wExpr)
    {
        // No workspace: the factory's own fallback substrate (FR-4, H 1.6 mm, T 35 µm, εr 4.4) is what
        // both the field and a run use.
        var model = new SchematicEditModel { SchematicDirectory = null };
        var comp = new EditableComponent { Symbol = SymbolKind.Mlin, InstanceName = "TL1" };
        comp.Parameters.Add(new EditableParameter { Name = "W", Expression = wExpr, Unit = "mm", ShowOnSchematic = true });
        comp.Parameters.Add(new EditableParameter { Name = "L", Expression = "10", Unit = "mm", ShowOnSchematic = true });
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);
        return (vm, comp, editor);
    }

    [Fact]
    public void Schematic_TypedZ0_WritesTheWidthThatGivesIt_AndReadsBack_AndUndoes()
    {
        var (vm, comp, editor) = MakeMlin("1");
        Assert.True(editor.IsMlinTarget);

        // Focus leaving the field untouched is not an edit: the shown Z0 is rounded, and converting it
        // back used to write a slightly different W on every focus loss (round-7 field report).
        editor.CommitMlinZ0Command.Execute(null);
        Assert.False(vm.UndoRedo.CanUndo);
        Assert.Equal("1", comp.Parameters.Single(p => p.Name == "W").Expression);

        editor.MlinZ0Text = "50";
        editor.CommitMlinZ0Command.Execute(null);

        double w = double.Parse(comp.Parameters.Single(p => p.Name == "W").Expression, CultureInfo.InvariantCulture) * 1e-3;

        // Independent closed form (Wheeler/Hammerstad synthesis, zero thickness, W/h < 2 branch):
        // 50 Ω on 1.6 mm εr 4.4 is ≈ 3.06 mm. 35 µm of copper narrows it a few percent.
        double er = ComponentModelFactory.DefaultSubstrateEpsR, h = ComponentModelFactory.DefaultSubstrateHMeters;
        double a = 50.0 / 60 * Math.Sqrt((er + 1) / 2) + (er - 1) / (er + 1) * (0.23 + 0.11 / er);
        double wOverH = 8 * Math.Exp(a) / (Math.Exp(2 * a) - 2);
        Assert.InRange(w / (wOverH * h), 0.93, 1.0);

        // The field re-reads the written W as the Z0 that was typed.
        Assert.Equal("50", editor.MlinZ0Text);
        Assert.Equal(50.0, HammerstadJensen.Compute(w, h, ComponentModelFactory.DefaultSubstrateTMeters, er,
            new MicrostripValidityReporter("t")).Z0, precision: 2);

        // One undoable edit.
        vm.UndoRedo.Undo();
        Assert.Equal("1", comp.Parameters.Single(p => p.Name == "W").Expression);
    }

    [Fact]
    public void Schematic_WidthFromAnExpression_ShowsNoZ0_AndIsNeverOverwritten()
    {
        var (_, comp, editor) = MakeMlin("wline");

        Assert.Equal("", editor.MlinZ0Text);
        Assert.True(editor.HasMlinImpedanceNote);

        editor.MlinZ0Text = "50";
        editor.CommitMlinZ0Command.Execute(null);
        Assert.Equal("wline", comp.Parameters.Single(p => p.Name == "W").Expression);
    }

    [Fact]
    public void Layout_Z0Row_FollowsW_AndATypedZ0_SetsW()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        string clayPath = Path.Combine(_root, "Doc", "layout", "main.clay");
        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath)
            { ActiveTool = LayoutEditorViewModel.Tool.Select, Technology = tech };
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);

        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", defaults, null, null, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance
            { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0 });
        vm.OnPointerPressed(0, 0, Avalonia.Input.KeyModifiers.None);
        Assert.True(props.IsMlinTarget);

        PCellParamRowViewModel Row(string name)
        {
            var names = new List<string>();
            for (int i = 0; i < props.PCellParamRows!.Count; i++) names.Add(props.PCellParamRows[i].Name);
            Assert.Equal(names.IndexOf("W") + 1, names.IndexOf(LayoutShapePropertiesViewModel.MlinZ0Row));
            return props.PCellParamRows[names.IndexOf(LayoutShapePropertiesViewModel.MlinZ0Row)];
        }

        // Clicking the canvas commits every field on focus loss; an untouched one must change nothing.
        Row("Z0").Commit(Row("Z0").ValueText);
        Assert.False(vm.UndoRedo.CanUndo);

        Row("Z0").Commit("75");
        Assert.Null(Row("Z0").Error);

        var (substrate, _, _) = SubstrateResolver.ResolveElectrical(tech, PCellLayerSelection.Default);
        double expected = HammerstadJensen.SynthesizeWidth(75, substrate!.HeightMeters, substrate.ThicknessMeters,
            substrate.RelativePermittivity, new MicrostripValidityReporter("t"));
        var origin = CellLayoutResolver.Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir).View!.PCellOrigin!;
        Assert.Equal(expected, origin.Parameters.Real("W"), precision: 9);
        Assert.False(origin.Parameters.ContainsKey("Z0"));
        Assert.StartsWith("75", Row("Z0").ValueText);
    }

    /// <summary>
    /// <b>The Component Properties window belongs to the instance it was opened for</b> (round-7 field
    /// report): it followed the selection, so a click on the canvas emptied it and re-selecting rebuilt
    /// and resized it. Pinned, a click elsewhere changes nothing in it, and an edit still lands on its
    /// instance.
    /// </summary>
    [Fact]
    public void Layout_PinnedDialog_IgnoresTheCanvasSelection_AndEditsItsOwnInstance()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        string clayPath = Path.Combine(_root, "Doc", "layout", "main.clay");
        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath)
            { ActiveTool = LayoutEditorViewModel.Tool.Select, Technology = tech };
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", defaults, null, null, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance
            { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0 });

        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        props.PinToInstance(0);
        var rows = props.PCellParamRows;
        Assert.NotNull(rows);

        vm.OnPointerPressed(50_000_000, 50_000_000, Avalonia.Input.KeyModifiers.None);   // click empty canvas
        Assert.Empty(vm.SelectedInstanceIndices);
        Assert.True(props.IsInstanceContext);
        Assert.Same(rows, props.PCellParamRows);                                          // not rebuilt

        var z0 = props.PCellParamRows!.First(r => r.Name == LayoutShapePropertiesViewModel.MlinZ0Row);
        z0.Commit("75");
        var origin = CellLayoutResolver.Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir).View!.PCellOrigin!;
        Assert.NotEqual(defaults.Real("W"), origin.Parameters.Real("W"));
    }
}
