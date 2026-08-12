using Avalonia.Input;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// Three owner reports about microstrip components in the LAYOUT editor, 2026-08-11:
///
/// <list type="number">
/// <item>a microstrip dropped from the palette ignored the technology's own line width (the schematic
///       editor has always honoured it);</item>
/// <item>MTee's W1/W2/W3 rows blanked out or errored in the PCell parameter editor, and editing them
///       grew spurious Z1/Z2 rows;</item>
/// <item>the gripper glyphs drew over the snap glyph during a grip drag, hiding the one mark that says
///       which feature is being snapped to.</item>
/// </list>
/// </summary>
public sealed class MicrostripLayoutIssuesTests : IDisposable
{
    private readonly string _root;

    public MicrostripLayoutIssuesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-ustrip-layout-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    /// <summary>The shipped RO4350B 20 mil PCB technology — the same worked example
    /// <c>MicrostripNiceDefaultsTests</c> pins the schematic side against.</summary>
    private static Technology Ro4350B20Mil() =>
        ShippedTechnologies.Load(ShippedTechnologies.All.First(e =>
            e.Id.Contains("20mil", StringComparison.OrdinalIgnoreCase)));

    private const double MetresPerMil = 25.4e-6;

    private LayoutEditorViewModel MakeVm(Technology? tech = null, long snapDbu = 1000)
        => new(new LayoutView { DbuPerMicron = 1000, SnapDbu = snapDbu },
               Path.Combine(_root, "Doc", "layout", "main.clay"))
           { ActiveTool = LayoutEditorViewModel.Tool.Select, Technology = tech };

    private LayoutEditorViewModel Place(
        string generatorId, IReadOnlyDictionary<string, PCellValue> parameters,
        Technology? tech = null, long snapDbu = 1000)
    {
        var vm = MakeVm(tech, snapDbu);
        string cellDir = GeneratedCellStore.GetOrCreate(
            _root, generatorId, parameters, null, null, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0,
        });
        vm.SelectInstance(0);
        return vm;
    }

    private static IReadOnlyDictionary<string, PCellValue> ParametersOf(LayoutEditorViewModel vm)
    {
        var res = CellLayoutResolver.Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir);
        Assert.Equal(CellLayoutState.Resolved, res.State);
        return res.View!.PCellOrigin!.Parameters;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  1 — the palette drop honours the technology's own line width
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The report: an MLIN dropped into a layout kept the registry's fixed 2.9 mm baseline, while the
    /// same MLIN placed on a schematic in the same workspace read 42 mil. Both entry points now route
    /// the registry defaults through the ONE rewrite (<c>MicrostripSubstrateInjection</c>), so the
    /// synthesised 50 Ω width and its rounding are shared rather than reimplemented.
    /// </summary>
    [Fact]
    public void PaletteDropDefaults_OnRo4350B20Mil_Give42MilWidth_NotTheMmBaseline()
    {
        var resolved = SchematicToLayoutGenerator.ResolveDefaultParameters(
            SymbolKind.Mlin, 0, Ro4350B20Mil());

        double w = resolved["W"].AsReal();
        Assert.Equal(42.0 * MetresPerMil, w, 12);              // 42 mil, in SI metres

        // Non-vacuous: the old value really is different, and by a wide margin (2.9 mm ≈ 114.17 mil).
        Assert.False(Math.Abs(w - 0.0029) < 1e-9, "the fixed 2.9 mm baseline must be gone");
    }

    /// <summary>Lengths get the technology's own round number too — 400 mil, not 10 mm converted.</summary>
    [Fact]
    public void PaletteDropDefaults_LengthIsARoundNumberInTheTechnologysOwnUnit()
    {
        var resolved = SchematicToLayoutGenerator.ResolveDefaultParameters(
            SymbolKind.Mlin, 0, Ro4350B20Mil());

        Assert.Equal(400.0 * MetresPerMil, resolved["L"].AsReal(), 12);
    }

    /// <summary>
    /// Every width-entry microstrip is covered, not just MLIN — MTee's three arms and MCross's four
    /// all synthesise to the same 50 Ω line. (MTaper's W2 is deliberately the 100 Ω narrow end, so it
    /// is asserted as strictly narrower rather than equal.)
    /// </summary>
    [Theory]
    [InlineData(SymbolKind.MTee,   new[] { "W1", "W2", "W3" })]
    [InlineData(SymbolKind.MCross, new[] { "W1", "W2", "W3", "W4" })]
    [InlineData(SymbolKind.MBend,  new[] { "W" })]
    public void PaletteDropDefaults_EveryWidthParameter_IsTheFiftyOhmLine(SymbolKind kind, string[] widths)
    {
        var resolved = SchematicToLayoutGenerator.ResolveDefaultParameters(kind, 0, Ro4350B20Mil());
        foreach (string name in widths)
            Assert.Equal(42.0 * MetresPerMil, resolved[name].AsReal(), 12);
    }

    [Fact]
    public void PaletteDropDefaults_MTaper_KeepsItsNarrowEnd()
    {
        var resolved = SchematicToLayoutGenerator.ResolveDefaultParameters(
            SymbolKind.Mtaper, 0, Ro4350B20Mil());

        Assert.Equal(42.0 * MetresPerMil, resolved["W1"].AsReal(), 12);
        Assert.True(resolved["W2"].AsReal() < resolved["W1"].AsReal(),
                    "W2 is the 100 Ω narrow end and must stay narrower than the 50 Ω W1");
    }

    /// <summary>
    /// No technology resolves — the millimetre baseline stands exactly as it did before this change.
    /// This is what keeps every pre-existing two-argument caller (and every test using one) unaffected.
    /// </summary>
    [Fact]
    public void PaletteDropDefaults_WithNoTechnology_KeepTheMmBaselineUnchanged()
    {
        var withNull = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0, null);
        var twoArg   = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);

        Assert.Equal(0.0029, withNull["W"].AsReal(), 12);
        Assert.Equal(withNull["W"].AsReal(), twoArg["W"].AsReal(), 12);
        Assert.Equal(withNull["L"].AsReal(), twoArg["L"].AsReal(), 12);
    }

    /// <summary>
    /// The two editors agree by construction now, but assert it directly — this is the property the
    /// owner actually reported, and it is the one a future divergence would break.
    /// </summary>
    [Fact]
    public void PaletteDrop_AndSchematicPlacement_AgreeOnTheSameWidth()
    {
        var tech = Ro4350B20Mil();

        // The schematic path, exactly as SchematicViewModel.CommitPlacement performs it.
        var schematicParams = ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 0)
            .Select(dp => new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            })
            .ToList();
        MicrostripSubstrateInjection.ApplyTechnologyDefaults(schematicParams, tech, SymbolKind.Mlin);
        var schematicW = Assert.Single(schematicParams, p => p.Name == "W");
        double schematicMetres =
            double.Parse(schematicW.Expression, System.Globalization.CultureInfo.InvariantCulture)
            * MetresPerMil;

        double layoutMetres =
            SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0, tech)["W"].AsReal();

        Assert.Equal("mil", schematicW.Unit);
        Assert.Equal(schematicMetres, layoutMetres, 12);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  2 — MTee's W1/W2/W3 are its own, not MKlopf's entry-mode pseudo-names
    // ═════════════════════════════════════════════════════════════════════════

    private static PCellParamRowViewModel Row(LayoutShapePropertiesViewModel props, string name)
    {
        Assert.NotNull(props.PCellParamRows);
        for (int i = 0; i < props.PCellParamRows!.Count; i++)
            if (props.PCellParamRows[i].Name == name) return props.PCellParamRows[i];
        Assert.Fail($"no parameter row named '{name}' (rows: {DescribeRows(props)})");
        return null!;
    }

    private static string DescribeRows(LayoutShapePropertiesViewModel props)
    {
        var names = new List<string>();
        for (int i = 0; i < props.PCellParamRows!.Count; i++) names.Add(props.PCellParamRows[i].Name);
        return string.Join(", ", names);
    }

    private (LayoutEditorViewModel Vm, LayoutShapePropertiesViewModel Props) PlaceMTee(Technology? tech = null)
    {
        var vm = Place("MTEE", SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.MTee, 0, tech), tech);
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        vm.SelectInstance(0);
        return (vm, props);
    }

    /// <summary>
    /// The headline symptom, and the one that needs NO technology to reproduce: MKlopf's entry-mode
    /// branch used to fire on the NAME alone, and its first act is to demand a resolved substrate —
    /// so on a technology-less document an MTee's W1/W2 blanked out with an error while W3 (which
    /// never matched the name test) rendered correctly.
    /// </summary>
    [Fact]
    public void MTee_WithNoTechnology_ShowsAllThreeWidths_NoBlanksNoErrors()
    {
        var (_, props) = PlaceMTee();

        foreach (string name in new[] { "W1", "W2", "W3" })
        {
            var row = Row(props, name);
            Assert.Null(row.Error);
            Assert.False(string.IsNullOrWhiteSpace(row.ValueText), $"{name} rendered blank");
        }
    }

    /// <summary>
    /// The three arms are independent, so the three rows must read independently too. With the name
    /// hijack in place W1 and W2 both came from a synthesized 50 Ω conversion of an absent Z1/Z2
    /// (defaulting to 50 Ω/50 Ω) and were therefore EQUAL to each other and unrelated to the cell.
    /// </summary>
    [Fact]
    public void MTee_EachWidthRow_ReadsItsOwnParameter()
    {
        var tech = Ro4350B20Mil();
        var parameters = new Dictionary<string, PCellValue>(
            SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.MTee, 0, tech))
        {
            ["W1"] = PCellValue.Real(0.001),
            ["W2"] = PCellValue.Real(0.002),
            ["W3"] = PCellValue.Real(0.003),
        };
        var vm = Place("MTEE", parameters, tech);
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        vm.SelectInstance(0);

        string w1 = Row(props, "W1").ValueText, w2 = Row(props, "W2").ValueText, w3 = Row(props, "W3").ValueText;
        Assert.NotEqual(w1, w2);
        Assert.NotEqual(w2, w3);
    }

    /// <summary>
    /// The write half. Editing W1 used to run MKlopf's width→impedance conversion and MERGE Z1/Z2
    /// into the cell — MTeePCell ignores them, so the edit appeared to do nothing, and the two
    /// orphans then surfaced as new rows on the next refresh ("it adds Z1/Z2 fields").
    /// </summary>
    [Fact]
    public void MTee_EditingW1_WritesW1_AndNeverMintsZParameters()
    {
        var (vm, props) = PlaceMTee(Ro4350B20Mil());
        double before = ParametersOf(vm)["W1"].AsReal();

        var row = Row(props, "W1");
        row.Commit("77");

        var after = ParametersOf(vm);
        Assert.NotEqual(before, after["W1"].AsReal());
        Assert.DoesNotContain("Z1", after.Keys);
        Assert.DoesNotContain("Z2", after.Keys);

        // W2/W3 are untouched — the edit is to one arm, not a paired conversion.
        Assert.Equal(before, after["W2"].AsReal(), 12);
        Assert.Equal(before, after["W3"].AsReal(), 12);
    }

    /// <summary>The rows a user sees are exactly the generator's own — no Z1/Z2/F3db anywhere.</summary>
    [Theory]
    [InlineData(SymbolKind.MTee,   "MTEE")]
    [InlineData(SymbolKind.MCross, "MCROSS")]
    [InlineData(SymbolKind.Mtaper, "MTAPER")]
    public void WidthEntryMicrostrips_ShowNoMklopfPseudoRows(SymbolKind kind, string generatorId)
    {
        var tech = Ro4350B20Mil();
        var vm = Place(generatorId, SchematicToLayoutGenerator.ResolveDefaultParameters(kind, 0, tech), tech);
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        vm.SelectInstance(0);

        Assert.NotNull(props.PCellParamRows);
        Assert.False(props.IsMklopfTarget);
        string rows = DescribeRows(props);
        Assert.DoesNotContain("Z1", rows);
        Assert.DoesNotContain("Z2", rows);
        Assert.DoesNotContain("F3db", rows);
    }

    /// <summary>
    /// The control that keeps the fix honest: MKlopf's own entry-mode toggle must still work. Without
    /// this, the three new IsMklopfTarget guards could have been "fixed" by deleting the branch.
    /// </summary>
    [Fact]
    public void Mklopf_EntryModeToggle_StillSwapsZ1Z2ForW1W2()
    {
        var tech = Ro4350B20Mil();
        var vm = Place("MKLOPF", SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mklopf, 0, tech), tech);
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        vm.SelectInstance(0);

        Assert.True(props.IsMklopfTarget);
        Assert.Contains("Z1", DescribeRows(props));

        Assert.True(props.MklopfEntryModeAvailable, "the shipped PCB technology must resolve a substrate");
        props.ToggleMklopfImpedanceEntryCommand.Execute(null);

        string rows = DescribeRows(props);
        Assert.Contains("W1", rows);
        Assert.DoesNotContain("Z1", rows);
        var w1 = Row(props, "W1");
        Assert.Null(w1.Error);
        Assert.False(string.IsNullOrWhiteSpace(w1.ValueText));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  3 — grippers yield to the snap glyph during a grip drag
    // ═════════════════════════════════════════════════════════════════════════

    private static PCellHandleMarker Grip(LayoutEditorViewModel vm, string label, double dx, double dy)
        => Assert.Single(vm.Overlay.PCellHandles, h =>
               h.Label == label && Math.Abs(h.AxisDx - dx) < 1e-6 && Math.Abs(h.AxisDy - dy) < 1e-6);

    /// <summary>An MLIN plus a separate rect whose corner is something to snap to.</summary>
    private LayoutEditorViewModel PlaceMlinWithSnapTarget(long targetX, long targetY)
    {
        var vm = Place("MLIN", SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0),
                       snapDbu: 10_000);
        vm.Model.Shapes.Add(new RectShape
        {
            Layer = new LayerKey(1, 0),
            X1 = targetX, Y1 = targetY, X2 = targetX + 500_000, Y2 = targetY + 500_000,
        });
        return vm;
    }

    [Fact]
    public void GripDrag_NearASnapTarget_HidesTheGrippers_LeavingOnlyTheSnapGlyph()
    {
        const long targetX = 3_333_000, targetY = 0;
        var vm = PlaceMlinWithSnapTarget(targetX, targetY);

        var grip = Grip(vm, "L", 1, 0);
        Assert.NotEmpty(vm.Overlay.PCellHandles);   // non-vacuous: they ARE drawn before the drag

        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 200_000, snapTolDbu: 50_000);
        vm.OnPointerMoved(targetX - 2_000, targetY, leftDown: true, KeyModifiers.None,
                          hitTolDbu: 200_000, snapTolDbu: 50_000);

        Assert.NotNull(vm.Overlay.SnapMarker);
        Assert.Empty(vm.Overlay.PCellHandles);

        vm.OnPointerReleased(targetX - 2_000, targetY, KeyModifiers.None);
        Assert.NotEmpty(vm.Overlay.PCellHandles);   // back on release
    }

    /// <summary>
    /// Outside the snap threshold there is no snap glyph, so the grippers must keep rendering — the
    /// second half of the owner's own wording, and the guard against "hide them for the whole drag".
    /// </summary>
    [Fact]
    public void GripDrag_WithNothingInRange_StillShowsTheGrippers()
    {
        var vm = PlaceMlinWithSnapTarget(3_333_000, 0);

        var grip = Grip(vm, "L", 1, 0);
        vm.OnPointerPressed(grip.X, grip.Y, KeyModifiers.None, hitTolDbu: 200_000, snapTolDbu: 50_000);
        // Far from the rect, and far from the MLIN's own excluded geometry.
        vm.OnPointerMoved(grip.X + 40_000, 8_000_000, leftDown: true, KeyModifiers.None,
                          hitTolDbu: 200_000, snapTolDbu: 50_000);

        Assert.Null(vm.Overlay.SnapMarker);
        Assert.NotEmpty(vm.Overlay.PCellHandles);
    }

    /// <summary>
    /// Merely hovering near a feature with a PCell selected — no drag — must not blank the grips.
    /// The suppression is scoped to an active grip drag, not to "a snap marker exists".
    /// </summary>
    [Fact]
    public void HoveringNearASnapTarget_WithNoDrag_KeepsTheGrippers()
    {
        const long targetX = 3_333_000, targetY = 0;
        var vm = PlaceMlinWithSnapTarget(targetX, targetY);

        vm.OnPointerMoved(targetX - 2_000, targetY, leftDown: false, KeyModifiers.None,
                          hitTolDbu: 200_000, snapTolDbu: 50_000);

        Assert.NotNull(vm.Overlay.SnapMarker);
        Assert.NotEmpty(vm.Overlay.PCellHandles);
    }
}
