using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Layout.PCells.Wire;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A PCell parameter is edited with the control its GENERATOR declared it needs — a checkbox for a
/// flag, a dropdown for an enumeration, selectable text for something the generator derives — rather
/// than the one free-text box every parameter used to get regardless of what it was.
///
/// <para><b>Why this is a correctness matter and not a polish one.</b> The box that hurts is the one
/// over a DERIVED value. A MIM cap's C is a function of its w and l: the cell never reads it, so
/// typing into it changes nothing, and the number sitting there stops matching the artwork the moment
/// w or l moves. Rendered identically to the width beside it, it reads as an input, it invites an
/// edit, and it silently ignores one.</para>
/// </summary>
public sealed class PCellParameterEditorKindTests : IDisposable
{
    private readonly string _root;

    public PCellParameterEditorKindTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-pcell-editorkind-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        PCellRegistry.ClearResolvers();
        CellLayoutResolver.InvalidateUnder(_root);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private const string GenId = "TESTKIT.cap";

    /// <summary>A declaration in the shape a vendor kit's capacitor cell produces: everything a
    /// string, a couple of enumerations, a real flag, and a capacitance that is an output.</summary>
    private static readonly PCellParameterInfo[] Declared =
    [
        new("w",         PCellValueKind.String, PCellValue.Text("6.99u"), "Width"),
        new("l",         PCellValueKind.String, PCellValue.Text("6.99u"), "Length"),
        new("C",         PCellValueKind.String, PCellValue.Text("74.6f"), null, Computed: true),
        new("model",     PCellValueKind.String, PCellValue.Text("mimcap"), "Model name"),
        new("shield",  PCellValueKind.String, PCellValue.Text("Yes"), "Shield ring",
            [PCellValue.Text("Yes"), PCellValue.Text("No")]),
        new("guard", PCellValueKind.String, PCellValue.Text("Yes"), "Guard ring",
            [PCellValue.Text("Yes"), PCellValue.Text("No"), PCellValue.Text("Sides"), PCellValue.Text("Ends")]),
        new("shape",     PCellValueKind.String, PCellValue.Text("octagon"), null,
            [PCellValue.Text("octagon"), PCellValue.Text("square")]),
        new("calculate", PCellValueKind.Bool,   PCellValue.Bool(true), "Calculate as,ad,ps,pd"),
        new("ng",        PCellValueKind.Int,    PCellValue.Int(1), "Number of Gates"),
    ];

    /// <summary>Stands in for a running kit worker: it answers the same two questions
    /// <see cref="PCellWorkerProvider"/> does — what do you declare, and here is your geometry — so
    /// the rows are built through the real registry lookup rather than around it.</summary>
    private sealed class FakeKit : IPCellGeneratorResolver
    {
        public PCellGenerator? Resolve(string generatorId) => generatorId == GenId
            ? (parameters, tech, layers) => new PCellResult(
                  [new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 }],
                  [],
                  ComputedParameters: ["C"])
            : null;

        public IReadOnlyCollection<string> KnownGeneratorIds => [GenId];
        public string Describe() => "fake kit";
        public string? ContentKeyFor(string generatorId) => generatorId == GenId ? "v1" : null;

        public IReadOnlyList<PCellParameterInfo>? DeclaredParameters(string generatorId)
            => generatorId == GenId ? Declared : null;

        public IReadOnlyDictionary<string, PCellValue>? DeclaredDefaults(string generatorId)
            => generatorId == GenId
                ? Declared.Where(d => d.Default is not null).ToDictionary(d => d.Name, d => d.Default!.Value)
                : null;
    }

    private (LayoutEditorViewModel Vm, LayoutShapePropertiesViewModel Props) SelectOnePlacedCap()
    {
        PCellRegistry.ClearResolvers();
        PCellRegistry.AddResolver(new FakeKit());

        string clayPath = Path.Combine(_root, "Doc", "layout", "main.clay");
        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath)
            { ActiveTool = LayoutEditorViewModel.Tool.Select };
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);

        var parameters = Declared.Where(d => d.Default is not null)
                                 .ToDictionary(d => d.Name, d => d.Default!.Value);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, GenId, parameters, null, null, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0,
        });
        vm.OnPointerPressed(0, 0, Avalonia.Input.KeyModifiers.None);
        return (vm, props);
    }

    private static PCellParamRowViewModel Row(LayoutShapePropertiesViewModel props, string name)
    {
        var rows = props.PCellParamRows!;
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Name == name) return rows[i];
        throw new Xunit.Sdk.XunitException($"No parameter row named '{name}'.");
    }

    [Fact]
    public void EachParameterGetsTheEditorItsGeneratorDeclared()
    {
        var (_, props) = SelectOnePlacedCap();

        // Nothing declared beyond a default: the free-text box, exactly as before any of this.
        Assert.Equal(PCellParamEditor.Text, Row(props, "w").Editor);
        Assert.Equal(PCellParamEditor.Text, Row(props, "model").Editor);
        Assert.Equal(PCellParamEditor.Text, Row(props, "ng").Editor);

        // A real Bool, and a two-valued Yes/No enumeration, are the same control.
        Assert.Equal(PCellParamEditor.Check, Row(props, "calculate").Editor);
        Assert.Equal(PCellParamEditor.Check, Row(props, "shield").Editor);

        Assert.Equal(PCellParamEditor.Choice, Row(props, "guard").Editor);

        // TWO choices, and still a dropdown: "octagon"/"square" is not a yes/no pair, and a checkbox
        // for it would have an unchecked state with no name. The count is not the test.
        Assert.Equal(PCellParamEditor.Choice, Row(props, "shape").Editor);

        Assert.Equal(PCellParamEditor.Computed, Row(props, "C").Editor);
    }

    [Fact]
    public void ADerivedParameterIsNotEditable_EvenThroughTheCommitPathDirectly()
    {
        var (vm, props) = SelectOnePlacedCap();
        var c = Row(props, "C");
        string beforeRef = vm.Model.Instances[0].CellRef;

        c.Commit("999f");

        // Not merely disabled in the view: refused at the commit. A write would have produced a new
        // content hash, a new generated cell, and a value the generator overwrites on its next run —
        // an edit that appears to take and changes nothing.
        Assert.Equal(beforeRef, vm.Model.Instances[0].CellRef);
        Assert.Equal("74.6f", Row(props, "C").ValueText);
    }

    [Fact]
    public void TickingACheckboxWritesTheKitsOwnWord_NotTrue()
    {
        var (vm, props) = SelectOnePlacedCap();

        Row(props, "shield").IsChecked = false;

        var origin = ResolveOrigin(vm);
        Assert.Equal(PCellValueKind.String, origin.Parameters["shield"].Kind);
        Assert.Equal("No", origin.Parameters["shield"].AsText());

        // A parameter the kit really did declare as a flag still gets a Bool — the vocabulary
        // follows the declaration, not the control.
        Row(props, "calculate").IsChecked = false;
        Assert.Equal(PCellValueKind.Bool, ResolveOrigin(vm).Parameters["calculate"].Kind);
    }

    [Fact]
    public void ChoosingFromADropdownCommitsTheChoice()
    {
        var (vm, props) = SelectOnePlacedCap();

        Row(props, "guard").SelectedChoice = "Ends";

        Assert.Equal("Ends", ResolveOrigin(vm).Parameters["guard"].AsText());
    }

    [Fact]
    public void AValueTheGeneratorDoesNotList_IsOfferedRatherThanCorrected()
    {
        PCellRegistry.ClearResolvers();
        PCellRegistry.AddResolver(new FakeKit());

        string clayPath = Path.Combine(_root, "Doc2", "layout", "main.clay");
        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath)
            { ActiveTool = LayoutEditorViewModel.Tool.Select };
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);

        // What an older design, or a hand-edited file, can perfectly well hold.
        var parameters = Declared.Where(d => d.Default is not null)
                                 .ToDictionary(d => d.Name, d => d.Default!.Value);
        parameters["guard"] = PCellValue.Text("Partial");

        string cellDir = GeneratedCellStore.GetOrCreate(_root, GenId, parameters, null, null, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0,
        });
        vm.OnPointerPressed(0, 0, Avalonia.Input.KeyModifiers.None);

        var row = Row(props, "guard");

        // Shown, and selected. Snapping it to the nearest listed choice would change artwork nobody
        // asked to change; rendering an empty box would make the first click do that silently.
        Assert.Contains("Partial", row.Choices);
        Assert.Equal("Partial", row.SelectedChoice);
        Assert.Equal("Partial", ResolveOrigin(vm).Parameters["guard"].AsText());
    }

    [Fact]
    public void TheKitsOwnLabelIsShown_AndTheNameStaysReachable()
    {
        var (_, props) = SelectOnePlacedCap();

        var ng = Row(props, "ng");
        Assert.Equal("Number of Gates", ng.Label);
        Assert.Equal("ng", ng.Name);            // still the identifier everything commits by
        Assert.Contains("ng", ng.Tip);          // and still visible, on hover

        // No label declared: the name IS the label, and there is nothing to disambiguate on hover.
        var shape = Row(props, "shape");
        Assert.Equal("shape", shape.Label);
    }

    /// <summary>
    /// A generated cell folder is reused on a plain EXISTENCE check — nothing regenerates on a hit —
    /// so which parameters are derived has to travel in the cell's own file. Held only in memory it
    /// would be known for the cell just placed and unknown for every cell already on disk, and the
    /// same MIM cap would offer an edit box for C in one session and not the next.
    /// </summary>
    [Fact]
    public void WhichParametersAreDerived_SurvivesReopening()
    {
        var (vm, _) = SelectOnePlacedCap();
        string cellRef = vm.Model.Instances[0].CellRef;

        // Straight off disk, with every in-memory cache dropped — the cold-open path.
        CellLayoutResolver.InvalidateUnder(_root);
        var reopened = CellLayoutResolver.Resolve(cellRef, vm.InstanceBaseDir);

        Assert.Equal(CellLayoutState.Resolved, reopened.State);
        var origin = reopened.View!.PCellOrigin!;
        Assert.True(origin.IsComputed("C"));
        Assert.False(origin.IsComputed("w"));
    }

    private PCellOrigin ResolveOrigin(LayoutEditorViewModel vm)
    {
        var res = CellLayoutResolver.Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir);
        Assert.Equal(CellLayoutState.Resolved, res.State);
        Assert.NotNull(res.View!.PCellOrigin);
        return res.View.PCellOrigin!;
    }
}
