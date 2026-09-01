using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The SpiceModel's Parameters dialog: the two questions it asks, the combo that answers the second
/// one, and the rows the answer brings with it.
///
/// <para><b>The panel is where a refusal has to arrive.</b> Everything the extractor can say about a
/// file, this says at the moment the file is chosen — the difference between a minute and a
/// measurement built on a part that never resolved. Both read the same peek, so they cannot say
/// different things.</para>
/// </summary>
public class SpiceModelParameterPanelTests : IDisposable
{
    private readonly string _root;

    public SpiceModelParameterPanelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-smpanel-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        SpiceModelPeek.InvalidateAll();
    }

    public void Dispose()
    {
        SpiceModelPeek.InvalidateAll();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Write(string name, string text)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private const string TwoSubcktsAndACard = """
        .model D1N4148 D (IS=2.52n RS=0.568 N=1.752)
        .subckt inner a b w=1u
        R1 a b 50
        .ends
        .subckt outer p1 p2 mult=2
        X1 p1 p2 inner
        .ends
        """;

    private (EditableComponent Comp, ParameterEditorViewModel Editor) Open(string file, string name = "")
    {
        var model = new SchematicEditModel { SchematicDirectory = _root };
        var comp = new EditableComponent { Symbol = SymbolKind.SpiceModel, InstanceName = "X1" };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.SpiceModel, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        comp.Parameters.First(p => p.Name == "File").Expression = file;
        comp.Parameters.First(p => p.Name == "Name").Expression = name;
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);
        return (comp, editor);
    }

    [Fact]
    public void TheNameComboListsEveryDefinitionTheFileProvides()
    {
        Write("kit.lib", TwoSubcktsAndACard);
        var (_, editor) = Open("kit.lib");

        Assert.True(editor.IsSpiceModel);
        Assert.Equal(3, editor.SpiceModelNameOptions.Count);
        foreach (var wanted in new[] { "outer", "inner", "D1N4148" })
            Assert.Contains(editor.SpiceModelNameOptions, o => o.StartsWith(wanted, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WithNoNameSet_TheComboShowsTheHighestLevelDefinitionAsSelected()
    {
        Write("kit.lib", TwoSubcktsAndACard);
        var (_, editor) = Open("kit.lib");

        Assert.True(editor.SpiceModelNameIndex >= 0);
        Assert.StartsWith("outer", editor.SpiceModelNameOptions[editor.SpiceModelNameIndex],
                          StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChoosingADefinition_WritesTheNameAndBringsInItsOwnParameterRows()
    {
        Write("kit.lib", TwoSubcktsAndACard);
        var (comp, editor) = Open("kit.lib", "outer");

        Assert.Contains(comp.Parameters, p => p.Name == "mult" && p.Expression == "2");
        Assert.DoesNotContain(comp.Parameters, p => p.Name == "w");

        int innerIdx = editor.SpiceModelNameOptions
            .Select((o, i) => (o, i))
            .First(x => x.o.StartsWith("inner", StringComparison.OrdinalIgnoreCase)).i;
        editor.SpiceModelNameIndex = innerIdx;

        Assert.Equal("inner", comp.Parameters.First(p => p.Name == "Name").Expression);

        // The previous definition's row is GONE and this one's is here. A subcircuit's parameters
        // belong to the definition that declares them; leaving the old list behind would show a
        // user rows that are then dropped at extraction.
        Assert.Contains(comp.Parameters, p => p.Name == "w");
        Assert.DoesNotContain(comp.Parameters, p => p.Name == "mult");
    }

    [Fact]
    public void PinsAndPitch_AreOfferedForASubcircuitAndHiddenForACard()
    {
        Write("kit.lib", TwoSubcktsAndACard);

        Assert.True(Open("kit.lib", "outer").Editor.SpiceModelShowPinLayout);

        // A card draws as the device, whose terminals are where that device's terminals are. Two
        // combos that visibly do nothing are worse than two that are absent.
        Assert.False(Open("kit.lib", "D1N4148").Editor.SpiceModelShowPinLayout);
    }

    [Fact]
    public void PanelParameters_NeverAppearAsGenericRows()
    {
        Write("kit.lib", TwoSubcktsAndACard);
        var (_, editor) = Open("kit.lib", "outer");

        foreach (var name in new[] { "File", "Name", "PinConfig", "Pitch" })
            Assert.DoesNotContain(editor.Rows, r => r.Name == name);

        Assert.Contains(editor.Rows, r => r.Name == "mult");
    }

    /// <summary>
    /// The panel is a readout of the FILE, so it follows an undo the way it follows an edit.
    ///
    /// <para>This is the one case the editor's own stale-binding rebuild cannot catch: a
    /// card-backed instance has no generic rows at all, so an empty row set matches an empty row
    /// set and nothing rebuilds. Without the panel refresh on that path it would go on describing
    /// the file that was chosen before.</para>
    /// </summary>
    [Fact]
    public void UndoingAFileChange_PutsThePanelBack()
    {
        Write("kit.lib", TwoSubcktsAndACard);
        Write("d.model", ".model D1N4148 D (IS=2.52n)\n");

        var model = new SchematicEditModel { SchematicDirectory = _root };
        var comp = new EditableComponent { Symbol = SymbolKind.SpiceModel, InstanceName = "X1" };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.SpiceModel, 0))
            comp.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression });
        comp.Parameters.First(p => p.Name == "File").Expression = "d.model";
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);
        Assert.Single(editor.SpiceModelNameOptions);          // the one card

        vm.Execute(new CircuitRF.Ui.Commands.Schematic.SetParametersCommand(
            model, comp,
            comp.Parameters.Select(p =>
            {
                var c = p.Clone();
                if (c.Name == "File") c.Expression = "kit.lib";
                return c;
            })));

        Assert.Equal(3, editor.SpiceModelNameOptions.Count);   // the new file's three definitions

        vm.UndoRedo.Undo();
        Assert.Single(editor.SpiceModelNameOptions);
    }

    [Fact]
    public void ARefusal_IsShownWhenTheFileIsChosen_NotOnlyAtRun()
    {
        Write("odd.model", ".model SOMETHING NPNX (IS=1e-16)\n");
        var (_, editor) = Open("odd.model");

        Assert.True(editor.HasSpiceModelStatus);
        Assert.True(editor.SpiceModelStatusIsProblem);
    }

    [Fact]
    public void AWorkingChoice_IsDescribed_AndNamesItsPins()
    {
        Write("kit.lib", TwoSubcktsAndACard);
        var (_, editor) = Open("kit.lib", "outer");

        Assert.False(editor.SpiceModelStatusIsProblem);
        Assert.Contains("p1", editor.SpiceModelStatus);
        Assert.Contains("p2", editor.SpiceModelStatus);
    }

    [Fact]
    public void NoFileYet_SaysSoRatherThanShowingAnEmptyPanel()
    {
        var (_, editor) = Open("");
        Assert.True(editor.SpiceModelStatusIsProblem);
        Assert.Contains("No file chosen", editor.SpiceModelStatus);
    }
}
