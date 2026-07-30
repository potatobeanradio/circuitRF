using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-followups-2.md Item 1 (R-L5g-1): the layout Properties Inspector's
/// PCell parameter list gains MKlopf's Z1/Z2 ⇄ W1/W2 and L ⇄ F3db entry-mode toggles — mirroring
/// <c>ParameterEditorViewModel</c>'s own schematic-side toggle pair, but resolving substrate via
/// <see cref="SubstrateResolver.ResolveElectrical"/> (the same resolution the PCell generator itself
/// uses) rather than <c>MicrostripSubstrateInjection.ResolveWorkspaceTechnology</c> (the schematic's
/// own ancestor-.cws walk) — the brief's own explicit instruction.
/// </summary>
public sealed class MklopfLayoutEntryModeTests : IDisposable
{
    private readonly string _root;

    public MklopfLayoutEntryModeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-mklopf-layout-entrymode-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (LayoutEditorViewModel Vm, LayoutShapePropertiesViewModel Props, LayoutInstance Inst) SetupMklopf(
        string cellName, Technology? technology)
    {
        string clayPath = Path.Combine(_root, cellName, "layout", "main.clay");
        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath)
        {
            ActiveTool = LayoutEditorViewModel.Tool.Select,
            Technology = technology,
        };
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);

        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mklopf, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MKLOPF", defaults, null, null, PCellLayerSelection.Default);
        var inst = new LayoutInstance { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 3_000_000, Y = 0, Mag = 1.0 };
        vm.Model.Instances.Add(inst);

        vm.OnPointerPressed(3_000_000, 0, Avalonia.Input.KeyModifiers.None);
        return (vm, props, inst);
    }

    // ── IsMklopfTarget / availability ────────────────────────────────────────────────────────

    [Fact]
    public void SelectingMklopfInstance_SetsIsMklopfTarget_True()
    {
        var (_, props, _) = SetupMklopf("Doc", StarterTechnologies.Pcb2Layer());
        Assert.True(props.IsMklopfTarget);
    }

    [Fact]
    public void SelectingNonMklopfInstance_IsMklopfTarget_False()
    {
        string clayPath = Path.Combine(_root, "NonMk", "layout", "main.clay");
        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath)
            { ActiveTool = LayoutEditorViewModel.Tool.Select };
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);

        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", defaults, null, null, PCellLayerSelection.Default);
        var inst = new LayoutInstance { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0 };
        vm.Model.Instances.Add(inst);
        vm.OnPointerPressed(0, 0, Avalonia.Input.KeyModifiers.None);

        Assert.False(props.IsMklopfTarget);
    }

    [Fact]
    public void NoTechnologyResolved_MklopfEntryModeAvailable_False_ToggleCannotExecute()
    {
        var (_, props, _) = SetupMklopf("Doc", technology: null);

        Assert.True(props.IsMklopfTarget);
        Assert.False(props.MklopfEntryModeAvailable);
        Assert.False(props.ToggleMklopfImpedanceEntryCommand.CanExecute(null));
        Assert.False(props.ToggleMklopfLengthEntryCommand.CanExecute(null));
    }

    [Fact]
    public void NoTechnologyResolved_ToggleTooltips_StateWhy()
    {
        var (_, props, _) = SetupMklopf("Doc", technology: null);

        Assert.NotNull(props.MklopfEntryModeDisabledReason);
        Assert.Equal(props.MklopfEntryModeDisabledReason, props.MklopfImpedanceToggleTip);
        Assert.Equal(props.MklopfEntryModeDisabledReason, props.MklopfLengthToggleTip);
    }

    // ── R-L5h-8: a technology that resolves AFTER the instance is already selected (the async
    // orphan-technology-prompt path, workspace-and-project-tree.md §5A.2/R33, or any later live
    // .ctech change) must still enable the toggles — not just a technology present at selection
    // time. This is the actual root cause behind "the toggles are always disabled": OnVmPropertyChanged
    // used to re-raise AvailableLayers on a Technology change but never re-evaluate
    // MklopfEntryModeAvailable, so a selection made before Technology resolved stayed stuck disabled
    // forever, even after Technology became valid. ─────────────────────────────────────────────────

    [Fact]
    public void TechnologyResolvingAfterSelection_EnablesTheToggle_WithoutReselecting()
    {
        var (vm, props, _) = SetupMklopf("Doc", technology: null);
        Assert.False(props.MklopfEntryModeAvailable);
        Assert.False(props.ToggleMklopfImpedanceEntryCommand.CanExecute(null));

        // The SAME live-update path ApplyTechResolution/the orphan-tech prompt uses — no re-selection.
        vm.Technology = StarterTechnologies.Pcb2Layer();

        Assert.True(props.MklopfEntryModeAvailable);
        Assert.True(props.ToggleMklopfImpedanceEntryCommand.CanExecute(null));
        Assert.True(props.ToggleMklopfLengthEntryCommand.CanExecute(null));
        Assert.Null(props.MklopfEntryModeDisabledReason);
    }

    [Fact]
    public void TechnologyResolvingAfterSelection_ThePCellParamRowsPicksUpConvertedValues()
    {
        var (vm, props, _) = SetupMklopf("Doc", technology: null);
        vm.Technology = StarterTechnologies.Pcb2Layer();
        Assert.True(props.MklopfEntryModeAvailable);

        // The toggle itself now genuinely works post-hoc — not just the CanExecute flag.
        props.ToggleMklopfImpedanceEntryCommand.Execute(null);
        Assert.True(props.MklopfUsesWidthEntry);
        var names = new List<string>();
        for (int i = 0; i < props.PCellParamRows!.Count; i++) names.Add(props.PCellParamRows[i].Name);
        Assert.Contains("W1", names);
    }

    [Fact]
    public void WithTechnology_MklopfEntryModeAvailable_True_ToggleCanExecute()
    {
        var (_, props, _) = SetupMklopf("Doc", StarterTechnologies.Pcb2Layer());

        Assert.True(props.MklopfEntryModeAvailable);
        Assert.True(props.ToggleMklopfImpedanceEntryCommand.CanExecute(null));
        Assert.True(props.ToggleMklopfLengthEntryCommand.CanExecute(null));
    }

    // ── Read path: toggling shows converted W1/W2/F3db values ───────────────────────────────

    [Fact]
    public void ToggleImpedanceEntry_ShowsWidthRows_WithConvertedValues()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var (vm, props, inst) = SetupMklopf("Doc", tech);

        props.ToggleMklopfImpedanceEntryCommand.Execute(null);

        Assert.True(props.MklopfUsesWidthEntry);
        Assert.NotNull(props.PCellParamRows);
        var names = new List<string>();
        for (int i = 0; i < props.PCellParamRows!.Count; i++) names.Add(props.PCellParamRows[i].Name);
        Assert.Contains("W1", names);
        Assert.Contains("W2", names);
        Assert.DoesNotContain("Z1", names);
        Assert.DoesNotContain("Z2", names);

        // Cross-check against the SAME conversion the generator itself uses.
        var (substrate, _, _) = SubstrateResolver.ResolveElectrical(tech, PCellLayerSelection.Default);
        Assert.NotNull(substrate);
        var reporter = new MicrostripValidityReporter("(test)");
        var (expectedW1, expectedW2) = MicrostripKlopfEntryConversion.ImpedanceToWidth(
            50.0, 100.0, substrate!.HeightMeters, substrate.ThicknessMeters, substrate.RelativePermittivity, reporter);

        var w1Row = props.PCellParamRows[names.IndexOf("W1")];
        var w2Row = props.PCellParamRows[names.IndexOf("W2")];
        Assert.Null(w1Row.Error);
        Assert.Null(w2Row.Error);

        long expectedW1Dbu = PCellUnits.MetresToDbu(expectedW1, vm.Model.DbuPerMicron);
        long expectedW2Dbu = PCellUnits.MetresToDbu(expectedW2, vm.Model.DbuPerMicron);
        string expectedW1Text = LayoutUnits.Format(expectedW1Dbu, vm.DisplayUnit, vm.Model.DbuPerMicron);
        string expectedW2Text = LayoutUnits.Format(expectedW2Dbu, vm.DisplayUnit, vm.Model.DbuPerMicron);
        Assert.Equal(expectedW1Text, w1Row.ValueText);
        Assert.Equal(expectedW2Text, w2Row.ValueText);
    }

    [Fact]
    public void ToggleLengthEntry_ShowsF3dbRow_WithConvertedValue()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var (vm, props, _) = SetupMklopf("Doc", tech);

        props.ToggleMklopfLengthEntryCommand.Execute(null);

        Assert.True(props.MklopfUsesF3dbEntry);
        var names = new List<string>();
        for (int i = 0; i < props.PCellParamRows!.Count; i++) names.Add(props.PCellParamRows[i].Name);
        Assert.Contains("F3db", names);
        Assert.DoesNotContain("L", names);

        var (substrate, _, _) = SubstrateResolver.ResolveElectrical(tech, PCellLayerSelection.Default);
        var reporter = new MicrostripValidityReporter("(test)");
        double expectedF3db = MicrostripKlopfEntryConversion.LengthToF3db(
            50.0, 100.0, 0.05, 0.02, substrate!.HeightMeters, substrate.ThicknessMeters, substrate.RelativePermittivity, reporter);

        var f3dbRow = props.PCellParamRows[names.IndexOf("F3db")];
        Assert.Null(f3dbRow.Error);
        string expectedText = SchematicToLayoutGenerator.Fmt(SchematicToLayoutGenerator.ToDisplayValue("GHz", expectedF3db));
        Assert.Equal($"{expectedText} GHz", f3dbRow.ValueText);
    }

    [Fact]
    public void ToggleTwice_ReturnsToCanonicalZ1Z2Rows()
    {
        var (_, props, _) = SetupMklopf("Doc", StarterTechnologies.Pcb2Layer());

        props.ToggleMklopfImpedanceEntryCommand.Execute(null);
        Assert.True(props.MklopfUsesWidthEntry);
        props.ToggleMklopfImpedanceEntryCommand.Execute(null);
        Assert.False(props.MklopfUsesWidthEntry);

        var names = new List<string>();
        for (int i = 0; i < props.PCellParamRows!.Count; i++) names.Add(props.PCellParamRows[i].Name);
        Assert.Contains("Z1", names);
        Assert.Contains("Z2", names);
    }

    // ── Write path: committing a converted value round-trips back to canonical Z1/Z2/L ──────

    [Fact]
    public void CommitW1Value_ConvertsBackToZ1Z2_ForksNewCell_SiblingOtherWidthPreserved()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var (vm, props, inst) = SetupMklopf("Doc", tech);
        string originalCellRef = inst.CellRef;

        props.ToggleMklopfImpedanceEntryCommand.Execute(null);
        var names = new List<string>();
        for (int i = 0; i < props.PCellParamRows!.Count; i++) names.Add(props.PCellParamRows[i].Name);
        var w1Row = props.PCellParamRows[names.IndexOf("W1")];
        var w2Row = props.PCellParamRows[names.IndexOf("W2")];
        string w2TextBefore = w2Row.ValueText;

        // Compute the current W1/W2 pair (same conversion the row already displays) and re-derive Z1/Z2
        // after nudging W1 up by 10% — this is what CommitPCellParamField must reproduce internally.
        var (substrate, _, _) = SubstrateResolver.ResolveElectrical(tech, PCellLayerSelection.Default);
        var reporter = new MicrostripValidityReporter("(test)");
        var (w1Cur, w2Cur) = MicrostripKlopfEntryConversion.ImpedanceToWidth(
            50.0, 100.0, substrate!.HeightMeters, substrate.ThicknessMeters, substrate.RelativePermittivity, reporter);
        double w1New = w1Cur * 1.10;
        var (expectedZ1, expectedZ2) = MicrostripKlopfEntryConversion.WidthToImpedance(
            w1New, w2Cur, substrate.HeightMeters, substrate.ThicknessMeters, substrate.RelativePermittivity, reporter);

        long w1NewDbu = PCellUnits.MetresToDbu(w1New, vm.Model.DbuPerMicron);
        string w1NewText = LayoutUnits.Format(w1NewDbu, vm.DisplayUnit, vm.Model.DbuPerMicron);
        w1Row.Commit(w1NewText);
        Assert.Null(w1Row.Error);

        // Instance forked to a new generated cell (content-addressed on the new canonical Z1/Z2) —
        // ReplaceInstanceCommand swaps in a new LayoutInstance object at the same index, so the LIVE
        // instance must be re-read from the model rather than the pre-commit `inst` reference.
        var liveInst = vm.Model.Instances[0];
        Assert.NotEqual(originalCellRef, liveInst.CellRef);

        var res = CellLayoutResolver.Resolve(liveInst.CellRef, vm.InstanceBaseDir);
        Assert.Equal(CellLayoutState.Resolved, res.State);
        var newOrigin = res.View!.PCellOrigin!;
        Assert.Equal(expectedZ1, newOrigin.Parameters["Z1"], precision: 3);
        Assert.Equal(expectedZ2, newOrigin.Parameters["Z2"], precision: 3);

        // W2 (the sibling, un-typed field) reads the SAME value after the commit as before it — a
        // fresh row lookup, since a fork rebuilds the whole PCellParamRows collection (a new CellRef
        // is a new `_pcellParamGeneratedCellDir`, R-L1j-6's own rebuild-on-structural-change rule) so
        // `w2Row` itself is now a stale, no-longer-realized object.
        var namesAfter = new List<string>();
        for (int i = 0; i < props.PCellParamRows!.Count; i++) namesAfter.Add(props.PCellParamRows[i].Name);
        var w2RowAfter = props.PCellParamRows[namesAfter.IndexOf("W2")];
        Assert.Equal(w2TextBefore, w2RowAfter.ValueText);
    }

    [Fact]
    public void CommitF3dbValue_ConvertsBackToL()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var (vm, props, inst) = SetupMklopf("Doc", tech);

        props.ToggleMklopfLengthEntryCommand.Execute(null);
        var names = new List<string>();
        for (int i = 0; i < props.PCellParamRows!.Count; i++) names.Add(props.PCellParamRows[i].Name);
        var f3dbRow = props.PCellParamRows[names.IndexOf("F3db")];

        var (substrate, _, _) = SubstrateResolver.ResolveElectrical(tech, PCellLayerSelection.Default);
        var reporter = new MicrostripValidityReporter("(test)");
        double f3dbNew = MicrostripKlopfEntryConversion.LengthToF3db(
            50.0, 100.0, 0.05, 0.02, substrate!.HeightMeters, substrate.ThicknessMeters, substrate.RelativePermittivity, reporter) * 1.20;
        double expectedL = MicrostripKlopfEntryConversion.F3dbToLength(
            50.0, 100.0, 0.05, f3dbNew, substrate.HeightMeters, substrate.ThicknessMeters, substrate.RelativePermittivity, reporter);

        string f3dbText = $"{SchematicToLayoutGenerator.Fmt(SchematicToLayoutGenerator.ToDisplayValue("GHz", f3dbNew))} GHz";
        f3dbRow.Commit(f3dbText);
        Assert.Null(f3dbRow.Error);

        var liveInst = vm.Model.Instances[0]; // ReplaceInstanceCommand swaps the instance object
        var res = CellLayoutResolver.Resolve(liveInst.CellRef, vm.InstanceBaseDir);
        Assert.Equal(CellLayoutState.Resolved, res.State);
        var newOrigin = res.View!.PCellOrigin!;
        Assert.Equal(expectedL, newOrigin.Parameters["L"], precision: 3);
    }

    // ── Reset-on-new-selection ────────────────────────────────────────────────────────────────

    [Fact]
    public void SwitchingToADifferentMklopfInstance_ResetsEntryModeToCanonical()
    {
        string clayPath = Path.Combine(_root, "TwoMk", "layout", "main.clay");
        var tech = StarterTechnologies.Pcb2Layer();
        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath)
            { ActiveTool = LayoutEditorViewModel.Tool.Select, Technology = tech };
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);

        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mklopf, 0);
        string cellDirA = GeneratedCellStore.GetOrCreate(_root, "MKLOPF", defaults, null, null, PCellLayerSelection.Default);
        var instA = new LayoutInstance { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDirA), X = 0, Y = 0, Mag = 1.0 };
        vm.Model.Instances.Add(instA);

        var defaultsB = new Dictionary<string, double>(defaults) { ["Z1"] = 25.0 };
        string cellDirB = GeneratedCellStore.GetOrCreate(_root, "MKLOPF", defaultsB, null, null, PCellLayerSelection.Default);
        var instB = new LayoutInstance { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDirB), X = 20_000_000, Y = 0, Mag = 1.0 };
        vm.Model.Instances.Add(instB);

        vm.OnPointerPressed(0, 0, Avalonia.Input.KeyModifiers.None); // select instA
        props.ToggleMklopfImpedanceEntryCommand.Execute(null);
        Assert.True(props.MklopfUsesWidthEntry);

        vm.OnPointerPressed(20_000_000, 0, Avalonia.Input.KeyModifiers.None); // select instB
        Assert.False(props.MklopfUsesWidthEntry);
    }
}
