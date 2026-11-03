using CircuitRF.Ui.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-followups.md §5/R-L5f-8/R-L5f-9, gates 8/9: selecting a PCell instance
/// lists its parameters, editable, with lazy row materialization (the L1j assertion); editing one of
/// two instances sharing a generated cell forks a new cell and leaves the other unchanged.
/// </summary>
public sealed class PCellPropertiesInspectorParameterListTests : IDisposable
{
    private readonly string _root;

    public PCellPropertiesInspectorParameterListTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-pcell-paramlist-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (LayoutEditorViewModel Vm, LayoutShapePropertiesViewModel Props) Setup(string cellName)
    {
        string clayPath = Path.Combine(_root, cellName, "layout", "main.clay");
        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath)
            { ActiveTool = LayoutEditorViewModel.Tool.Select };
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        return (vm, props);
    }

    [Fact]
    public void PCellInstance_ListsParameters_Gate8()
    {
        var (vm, props) = Setup("Doc");
        var defaults = SchematicLayoutPlanner.ResolveDefaultParameters(SymbolKind.Mklopf, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MKLOPF", defaults, null, null, PCellLayerSelection.Default);
        var inst = new LayoutInstance { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 3_000_000, Y = 0, Mag = 1.0 };
        vm.Model.Instances.Add(inst);

        vm.OnPointerPressed(3_000_000, 0, Avalonia.Input.KeyModifiers.None);

        Assert.True(props.ShowPCellParameterList);
        Assert.NotNull(props.PCellParamRows);
        Assert.Equal(defaults.Count, props.PCellParamRows!.Count);

        // Realizing exactly one row (index 0) materializes exactly one — the L1j virtualization proof.
        var firstRow = props.PCellParamRows[0];
        Assert.Equal(1, props.PCellParamRows.MaterializedCount);
        Assert.False(string.IsNullOrEmpty(firstRow.ValueText));
    }

    [Fact]
    public void EditingParameter_ForksNewCell_LeavesSiblingUnchanged_Gate9()
    {
        var (vm, props) = Setup("Doc2");
        var defaults = SchematicLayoutPlanner.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", defaults, null, null, PCellLayerSelection.Default);
        string cellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir);

        var inst1 = new LayoutInstance { CellRef = cellRef, X = 0, Y = 0, Mag = 1.0 };
        var inst2 = new LayoutInstance { CellRef = cellRef, X = 20_000_000, Y = 0, Mag = 1.0 };
        vm.Model.Instances.Add(inst1);
        vm.Model.Instances.Add(inst2);

        vm.OnPointerPressed(5_000_000, 0, Avalonia.Input.KeyModifiers.None); // mid-MLIN1, selects inst1
        Assert.True(props.ShowPCellParameterList);

        // Find the "W" row and commit a new value through the same path the view's LostFocus handler uses.
        int wIndex = -1;
        for (int i = 0; i < props.PCellParamRows!.Count; i++)
            if (props.PCellParamRows[i].Name == "W") { wIndex = i; break; }
        Assert.True(wIndex >= 0);
        var wRow = props.PCellParamRows[wIndex];

        wRow.Commit("9.5"); // mm, layout display unit is Um by default... use whatever's currently shown format

        // inst2 (never selected/edited) keeps pointing at the ORIGINAL cell.
        Assert.Equal(cellRef, vm.Model.Instances[1].CellRef);
        // inst1 now points somewhere else.
        Assert.NotEqual(cellRef, vm.Model.Instances[0].CellRef);
    }

    /// <summary>
    /// Contract version 2: a parameter that is not a number must not be shown as one. Before the
    /// widening the inspector could only read a double, so a String parameter would have rendered a
    /// confident <c>0</c> — the failure this test exists to keep closed is silent, not loud.
    /// </summary>
    [Fact]
    public void ANonNumericParameter_ShowsItsOwnText_NotAZero()
    {
        var (vm, props) = Setup("Doc3");
        var parameters = new Dictionary<string, PCellValue>(
            SchematicLayoutPlanner.ResolveDefaultParameters(SymbolKind.Mlin, 0))
        {
            ["Fingers"] = PCellValue.Int(4),
            ["Model"]   = PCellValue.Text("nch_lvt"),
        };
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", parameters, null, null, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance
            { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0 });

        vm.OnPointerPressed(5_000_000, 0, Avalonia.Input.KeyModifiers.None);

        Assert.Equal("4",       RowNamed(props, "Fingers").ValueText);
        Assert.Equal("nch_lvt", RowNamed(props, "Model").ValueText);
    }

    /// <summary>
    /// An edit re-enters the kind the parameter already has. Typing a number into an Int parameter
    /// must not quietly turn it into a Real: the kind is part of the content hash that decides which
    /// generated cell the instance resolves to, so a coerced kind is a different cell.
    /// </summary>
    [Fact]
    public void EditingAParameter_KeepsItsKind_RatherThanCoercingItToWhateverWasTyped()
    {
        var (vm, props) = Setup("Doc4");
        var parameters = new Dictionary<string, PCellValue>(
            SchematicLayoutPlanner.ResolveDefaultParameters(SymbolKind.Mlin, 0))
            { ["Fingers"] = PCellValue.Int(4) };
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", parameters, null, null, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance
            { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0 });

        vm.OnPointerPressed(5_000_000, 0, Avalonia.Input.KeyModifiers.None);
        RowNamed(props, "Fingers").Commit("6");

        var edited = CellLayoutResolver
            .Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir).View!.PCellOrigin!.Parameters["Fingers"];
        Assert.Equal(PCellValue.Int(6), edited);
        Assert.Equal(PCellValueKind.Int, edited.Kind);

        // And a value that is not a whole number is refused by name rather than truncated.
        var row = RowNamed(props, "Fingers");
        row.Commit("6.5");
        Assert.False(string.IsNullOrEmpty(row.Error));
    }

    // ── A script-backed cell's units (owner report, 2026-09-15) ──────────────

    /// <summary>
    /// <b>A script-backed length parameter is shown in the LAYOUT's display unit, like every other
    /// dimension in the panel — not as a bare SI number.</b>
    ///
    /// <para>OWNER REPORT: a 10 µm turn width read as <c>0.00001</c> in a layout whose every other
    /// length was in µm. The unit the whole read/write path is keyed on came only from
    /// <c>ComponentTypeRegistry.DefaultParameters</c>, which is keyed by <c>SymbolKind</c> — and a
    /// kit's generator has no SymbolKind at all, so the unit was always "". The dimension was on the
    /// wire the whole time; nothing carried it as far as the row.</para>
    /// </summary>
    [Fact]
    public void AScriptBackedLengthParameter_ReadsInTheLayoutsDisplayUnit()
    {
        var (vm, props) = Setup("Doc5");
        vm.Model.DisplayUnit = LayoutUnit.Um;

        PCellRegistry.ClearResolvers();
        PCellRegistry.AddResolver(new DeclaringResolver());
        try
        {
            var parameters = new Dictionary<string, PCellValue>
            {
                ["Width"] = PCellValue.Real(10e-6),   // 10 µm, stored in SI like every other length
                ["Turns"] = PCellValue.Int(3),
            };
            string cellDir = GeneratedCellStore.GetOrCreate(
                _root, DeclaringResolver.Id, parameters, null, null, PCellLayerSelection.Default);
            vm.Model.Instances.Add(new LayoutInstance
                { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0 });

            vm.OnPointerPressed(5_000, 0, Avalonia.Input.KeyModifiers.None);

            var width = RowNamed(props, "Width");
            Assert.Equal("mm", width.Unit);      // the registry's own spelling for "a length"
            Assert.Equal("10", width.ValueText); // …rendered through the layout's µm, not 1E-05

            // A count is not a length and must not be scaled into one.
            Assert.Equal("", RowNamed(props, "Turns").Unit);
            Assert.Equal("3", RowNamed(props, "Turns").ValueText);
        }
        finally { PCellRegistry.ClearResolvers(); }
    }

    /// <summary>
    /// And it is TYPED in that unit too. The same empty unit made the field uneditable in any
    /// sensible one: with nothing to strip, "12 um" does not parse at all, and a bare "12" commits
    /// twelve METRES — an edit that looks like it worked and moves the artwork by six orders of
    /// magnitude.
    /// </summary>
    [Fact]
    public void AScriptBackedLengthParameter_IsTypedInThatUnitToo()
    {
        var (vm, props) = Setup("Doc6");
        vm.Model.DisplayUnit = LayoutUnit.Um;

        PCellRegistry.ClearResolvers();
        PCellRegistry.AddResolver(new DeclaringResolver());
        try
        {
            var parameters = new Dictionary<string, PCellValue> { ["Width"] = PCellValue.Real(10e-6) };
            string cellDir = GeneratedCellStore.GetOrCreate(
                _root, DeclaringResolver.Id, parameters, null, null, PCellLayerSelection.Default);
            vm.Model.Instances.Add(new LayoutInstance
                { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0 });

            vm.OnPointerPressed(5_000, 0, Avalonia.Input.KeyModifiers.None);
            RowNamed(props, "Width").Commit("12");

            var edited = CellLayoutResolver
                .Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir).View!.PCellOrigin!.Parameters["Width"];
            Assert.Equal(12e-6, edited.AsReal(), 12);
        }
        finally { PCellRegistry.ClearResolvers(); }
    }

    // ── The MMIC passives' µm rows (owner report, 2026-10-07) ────────────────

    /// <summary>
    /// <b>A bare number typed into a µm row is micrometres.</b> OWNER REPORT: a spiral's Din changed
    /// from 100 to 200 read back as 200000000 µm and drew a 200 m coil. The parser looked the row's
    /// "µm" glyph up in the ASCII-only unit table, found no scale and stored 200 METRES; the display
    /// normalized and divided by 1e-6. Every length on all four MMIC parts is a µm row.
    /// </summary>
    [Theory]
    [InlineData(SymbolKind.MimCap,    "MIMCAP")]
    [InlineData(SymbolKind.Spiral,    "SPIRAL")]
    [InlineData(SymbolKind.Tfr,       "TFR")]
    [InlineData(SymbolKind.Airbridge, "AIRBRIDGE")]
    public void AnMmicPartsMicronRow_TakesABareNumberAsMicrons(SymbolKind kind, string generatorId)
    {
        var (vm, props) = Setup("Mmic" + generatorId);
        var defaults = SchematicLayoutPlanner.ResolveDefaultParameters(kind, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, generatorId, defaults, null, null, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance
            { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0 });
        vm.SelectInstance(0);

        var micronRows = ComponentTypeRegistry.DefaultParameters(kind, 0).Where(p => p.Unit == "µm").Select(p => p.Name).ToList();
        Assert.NotEmpty(micronRows);
        foreach (var name in micronRows)
        {
            RowNamed(props, name).Commit("200");

            var edited = CellLayoutResolver
                .Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir).View!.PCellOrigin!.Parameters[name];
            Assert.Equal(200e-6, edited.AsReal(), 12);
            Assert.Equal("200 µm", RowNamed(props, name).ValueText);
        }

        // The ASCII and Greek-mu spellings strip against the same row.
        RowNamed(props, micronRows[0]).Commit("150 um");
        RowNamed(props, micronRows[0]).Commit("120 μm");
        Assert.Equal("120 µm", RowNamed(props, micronRows[0]).ValueText);
    }

    /// <summary>A TFR's R, a MIMCAP's C and a spiral's L are listed after the geometry as computed rows
    /// — a readout, not an input — and follow an edit to it.</summary>
    [Theory]
    [InlineData(SymbolKind.Tfr,       "TFR",     "R")]
    [InlineData(SymbolKind.MimCap,    "MIMCAP",  "C")]
    [InlineData(SymbolKind.Spiral,    "SPIRAL",  "L")]
    [InlineData(SymbolKind.OctSpiral, "OSPIRAL", "L")]
    public void AnMmicReadoutRow_IsComputedAndFollowsTheGeometry(SymbolKind kind, string generatorId, string readout)
    {
        var (vm, props) = Setup("Readout" + generatorId);
        var defaults = SchematicLayoutPlanner.ResolveDefaultParameters(kind, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, generatorId, defaults, null, null, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance
            { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0 });
        vm.SelectInstance(0);

        var names = Enumerable.Range(0, props.PCellParamRows!.Count).Select(i => props.PCellParamRows[i].Name).ToList();
        string follows = MmicPassiveInjection.ReadoutFollows(kind);
        Assert.Equal(names.IndexOf(follows) + 1, names.IndexOf(readout));
        var row = RowNamed(props, readout);
        Assert.True(row.IsComputed);
        string before = row.ValueText;
        Assert.StartsWith("≈ ", before);

        RowNamed(props, follows).Commit("80");
        Assert.NotEqual(before, RowNamed(props, readout).ValueText);
        if (kind == SymbolKind.Tfr)   // no technology: the standalone 50 Ω/sq film
            Assert.Equal("≈ 400 Ω (8 sq × 50 Ω/sq)", RowNamed(props, readout).ValueText);
        if (readout == "L")
            Assert.EndsWith(" nH (estimated)", RowNamed(props, readout).ValueText);
    }

    /// <summary>A resolver standing in for a kit: it declares the DIMENSIONS a real one declares on
    /// the wire, and draws one rectangle whose width is the parameter, so the geometry moves with the
    /// value the way a real cell's does.</summary>
    private sealed class DeclaringResolver : IPCellGeneratorResolver
    {
        public const string Id = "TEST_KIT_CELL";

        public PCellGenerator? Resolve(string generatorId) => generatorId == Id
            ? (parameters, _, _) =>
              {
                  long half = PCellUnits.MetresToDbu(parameters.Real("Width", 10e-6), 1000) / 2;
                  return new PCellResult([new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = -half, X2 = 100_000, Y2 = half }], []);
              }
            : null;

        public IReadOnlyCollection<string> KnownGeneratorIds => [Id];
        public string Describe() => "the declaring test resolver";
        public string? ContentKeyFor(string generatorId) => generatorId == Id ? "v1" : null;

        public IReadOnlyDictionary<string, PCellValue>? DeclaredDefaults(string generatorId)
            => generatorId == Id
                ? new Dictionary<string, PCellValue> { ["Width"] = PCellValue.Real(10e-6), ["Turns"] = PCellValue.Int(3) }
                : null;

        public IReadOnlyList<PCellParameterInfo>? DeclaredParameters(string generatorId)
            => generatorId == Id
                ? [new PCellParameterInfo("Width", PCellValueKind.Real, PCellValue.Real(10e-6),
                                          Dimension: PCellDimension.Length),
                   new PCellParameterInfo("Turns", PCellValueKind.Int, PCellValue.Int(3))]
                : null;
    }

    private static PCellParamRowViewModel RowNamed(LayoutShapePropertiesViewModel props, string name)
    {
        Assert.NotNull(props.PCellParamRows);
        for (int i = 0; i < props.PCellParamRows!.Count; i++)
            if (props.PCellParamRows[i].Name == name) return props.PCellParamRows[i];
        throw new Xunit.Sdk.XunitException($"No PCell parameter row named '{name}'.");
    }
}
