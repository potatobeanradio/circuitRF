using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// brief-dfr26-7 — the TESTPAD component on the shipped ten-layer board: its artwork, the cut-outs it declares (poured
/// and drawn), the EM port it carries at its centre, its stackup resolution, and the impedance tool's report of a pad
/// hanging off a line.
/// </summary>
public sealed class TestPadRound26Tests : IDisposable
{
    private const string TenLayer = "pcb-10layer_FR-4_1.7mm_1oz";
    private const string Top = "Top Copper (1 oz)", Bottom = "Bottom Copper (1 oz)";
    private static readonly LayerKey TopKey = new(1, 0), BottomKey = new(2, 0), Inner1Key = new(10, 0),
                                     Inner7Key = new(20, 0), Inner8Key = new(21, 0), ThroughDrill = new(7, 0);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-dfr26-7-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly Technology _tech = ShippedTechnologies.Load(TenLayer);

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static EditableComponent Component(SymbolKind kind, string name, params (string Name, string Value)[] set)
    {
        var comp = new EditableComponent { InstanceName = name, Symbol = kind };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 0))
            comp.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, Dimension = dp.Dimension });
        foreach (var (n, v) in set) comp.Parameters.Single(p => p.Name == n).Expression = v;
        return comp;
    }

    /// <summary>Update Layout from a schematic of <paramref name="parts"/>: the generated layout, its folder and path.</summary>
    private (LayoutView View, string LayoutDir, string Clay) LaidOut(params EditableComponent[] parts)
    {
        string cellDir = CellFolder.CreateCellFolder(_root, "Board" + Guid.NewGuid().ToString("N")[..6]);
        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        foreach (var p in parts) model.Components.Add(p);
        var view = new LayoutView();
        SchematicToLayoutGenerator.Run(model, view, schematicDir, _root, layoutDir, _tech, null, null).Command!.Execute();
        return (view, layoutDir, Path.Combine(layoutDir, "Board.clay"));
    }

    private PCellResult Generate(params (string Key, PCellValue Value)[] set)
        => TestPadPCell.Generate(set.ToDictionary(s => s.Key, s => s.Value), _tech, PCellLayerSelection.Default);

    // ── Test 1: artwork ──────────────────────────────────────────────────────────────────────────

    /// <summary>Designer round 27: the pad's two conductor rows are pickers of the stackup's conductors, so a name that
    /// misses (a "Top Copper" for "Top Copper (1 oz)") cannot be typed; and every row whose own default is empty can be
    /// emptied again in the dialog, since empty there means "follow the technology".</summary>
    [Fact]
    public void ConductorRowsArePickers_AndEmptyDefaultRowsCanBeCleared()
    {
        Assert.Equal(ComponentTypeRegistry.LayerChoiceKind.Signal,
                     ComponentTypeRegistry.LayerChoiceKindFor(SymbolKind.TestPad, "PadLayer"));
        Assert.Equal(ComponentTypeRegistry.LayerChoiceKind.Signal,
                     ComponentTypeRegistry.LayerChoiceKindFor(SymbolKind.TestPad, "ViaFrom"));
        Assert.Null(ComponentTypeRegistry.LayerChoiceKindFor(SymbolKind.TestPad, "ClearLayers"));

        foreach (string row in new[] { "ClearLayers", "ClearD", "Drill", "FeedW", "InnerPadD", "AntipadD", "PortNum" })
            Assert.True(ComponentTypeRegistry.HasEmptyDefault(SymbolKind.TestPad, row), row);
        Assert.False(ComponentTypeRegistry.HasEmptyDefault(SymbolKind.TestPad, "D"));
    }

    [Fact]
    public void ViaFed_DrawsThePadOnPadLayer_TheDrillOnTheThroughSpan_AndNothingBetween()
    {
        var r = Generate(("PadLayer", Bottom), ("ViaFrom", Top), ("D", 1.5e-3));

        var pad = Assert.Single(r.Shapes.OfType<CircleShape>(), c => c.Layer == BottomKey);
        Assert.Equal(750_000, pad.R);
        var drill = Assert.Single(r.Shapes.OfType<ViaShape>());
        Assert.Equal(ThroughDrill, drill.Layer);
        Assert.Equal(300_000, drill.DrillSize);
        Assert.Single(r.Shapes.OfType<CircleShape>(), c => c.Layer == TopKey);
        Assert.Equal([TopKey, BottomKey, ThroughDrill], r.Shapes.Select(s => s.Layer).Distinct().OrderBy(k => k.Layer));

        Assert.Equal(TopKey, r.Pins.Single(p => p.Name == "1").Layer);
        var pin2 = r.Pins.Single(p => p.Name == "2");
        Assert.Equal((BottomKey, 0L, 0L), (pin2.Layer, pin2.X, pin2.Y));
    }

    [Fact]
    public void SideFed_DrawsAStubOnTheNamedSide_WithPinOneAtItsEnd()
    {
        var r = Generate(("PadLayer", Bottom), ("Feed", "E"), ("D", 1.5e-3), ("FeedW", 0.2e-3));

        Assert.Empty(r.Shapes.OfType<ViaShape>());
        var stub = Assert.Single(r.Shapes.OfType<RectShape>());
        Assert.Equal(BottomKey, stub.Layer);
        Assert.Equal((0L, 950_000L), (stub.X1, stub.X2));          // centre out past the 0.75 mm rim by one width
        Assert.Equal((-100_000L, 100_000L), (stub.Y1, stub.Y2));
        var pin1 = r.Pins.Single(p => p.Name == "1");
        Assert.Equal((950_000L, 0L, 0.0), (pin1.X, pin1.Y, pin1.OutwardDirectionDeg));
    }

    // ── Test 2: the pour's cut-outs ──────────────────────────────────────────────────────────────

    [Fact]
    public void GroundPour_CutsClearD_OutOfEachPlaneInClearLayers_AndOnlyTheViaAntipadElsewhere()
    {
        var (view, layoutDir, _) = LaidOut(
            Component(SymbolKind.Mlin, "ML1", ("SignalLayer", Top)),                                 // returns through Inner 1
            Component(SymbolKind.Mlin, "ML2", ("SignalLayer", Bottom), ("GroundReference", "Inner 8")),
            Component(SymbolKind.TestPad, "TP1", ("PadLayer", Bottom), ("ViaFrom", Top),
                      ("ClearLayers", "Inner 8"), ("ClearD", "2.5")));
        var tp = view.Instances.Single(i => i.SchematicId == "TP1");
        foreach (var ml in view.Instances.Where(i => i.SchematicId!.StartsWith("ML")))
        {
            var bb = CellHierarchy.InstanceBbox(ml, layoutDir);
            ml.X -= (bb.MinX + bb.MaxX) / 2; ml.Y -= (bb.MinY + bb.MaxY) / 2;   // both lines centred on the pad
        }
        tp.X = 0; tp.Y = 0;

        var pours = GroundPourPlanner.Plan(view, _tech, layoutDir);
        long HoleWidth(string plane)
        {
            var poly = Assert.IsType<PolygonShape>(Assert.Single(pours.Single(p => p.Ground.Name == plane).Shapes));
            var hole = Assert.Single(poly.Holes!);
            return hole.Where((_, i) => i % 2 == 0).Max() - hole.Where((_, i) => i % 2 == 0).Min();
        }
        Assert.InRange(HoleWidth("Inner 8"), 2_499_000, 2_520_000);   // ClearD
        Assert.InRange(HoleWidth("Inner 1"), 899_000, 910_000);       // the via's own pad + 0.3 mm, not ClearD
    }

    // ── Test 3: Cut Clearances into Planes ───────────────────────────────────────────────────────

    [Fact]
    public void CutClearances_CutsACircleFromEachListedDrawnPlane_AsOneUndoEntry()
    {
        var (view, _, clay) = LaidOut(Component(SymbolKind.TestPad, "TP1", ("PadLayer", Bottom), ("ViaFrom", Top),
                                                ("ClearLayers", "Inner 8, Inner 7"), ("ClearD", "2")));
        var tp = view.Instances.Single();
        tp.X = 0; tp.Y = 0;
        foreach (var layer in new[] { Inner8Key, Inner7Key, Inner1Key })
            view.Shapes.Add(new RectShape { Layer = layer, X1 = -5_000_000, Y1 = -5_000_000, X2 = 5_000_000, Y2 = 5_000_000 });

        var vm = new LayoutEditorViewModel(view, clay) { Technology = _tech };
        vm.SelectInstance(0);
        Assert.True(vm.SelectionIsOneTestPad);
        vm.CutTestPadClearances();

        PolygonShape On(LayerKey k) => Assert.IsType<PolygonShape>(Assert.Single(view.Shapes, s => s.Layer == k));
        foreach (var k in new[] { Inner8Key, Inner7Key })
        {
            var hole = Assert.Single(On(k).Holes!);
            Assert.InRange(hole.Where((_, i) => i % 2 == 0).Max() - hole.Where((_, i) => i % 2 == 0).Min(), 1_980_000, 2_010_000);
        }
        Assert.IsType<RectShape>(Assert.Single(view.Shapes, s => s.Layer == Inner1Key));   // not listed: untouched

        Assert.Equal("Undo \"Cut Clearances into Planes\"", vm.UndoRedo.UndoDescription);
        vm.UndoRedo.Undo();
        Assert.Equal(3, view.Shapes.OfType<RectShape>().Count());
        Assert.False(vm.UndoRedo.CanUndo);
    }

    // ── Test 4: the component-declared EM port ───────────────────────────────────────────────────

    [Fact]
    public void CentrePort_IsSeenAtThePadCentreOnPadLayer_OpenIsNot_AndADuplicateNumberIsRefused()
    {
        var (view, _, clay) = LaidOut(
            Component(SymbolKind.TestPad, "TP1", ("PadLayer", Bottom), ("Centre", "Port"), ("PortNum", "3")),
            Component(SymbolKind.TestPad, "TP2", ("PadLayer", Bottom)));
        var tp1 = view.Instances.Single(i => i.SchematicId == "TP1");
        (tp1.X, tp1.Y, tp1.Rot) = (5_000_000, 2_000_000, LayoutRotation.R90);
        var source = new EmLayoutSource(clay, view, _tech, view.DbuPerMicron);

        var found = EmComponentPorts.Collect(source);
        Assert.Null(found.Refusal);
        var port = Assert.Single(found.Shapes.OfType<LabelShape>());                        // TP2's Open centre adds nothing
        Assert.Equal((5_000_000L, 2_000_000L, BottomKey), (port.X, port.Y, port.Layer));
        Assert.Equal(PlanarPortKind.Internal, port.PortKind);
        Assert.Equal(3, Assert.Single(EmPortExtraction.NumberPorts(found.Shapes)).Number);

        view.Shapes.Add(new LabelShape { Layer = TopKey, X = 0, Y = 0, Text = "P3", IsPort = true });
        var refused = EmComponentPorts.Collect(source).Refusal;
        Assert.NotNull(refused);
        Assert.Contains("'P3'", refused);
        Assert.Contains("'TP1'", refused);
    }

    // ── Test 5 (stackup half): which plane the pad faces ─────────────────────────────────────────

    [Fact]
    public void ThePadFacesTheNearestUnclearedConductor_AndClearingItMovesHToTheNext()
    {
        TestPadFace Face(params string[] clear)
            => Assert.Single(SubstrateResolver.ResolveTestPad(_tech, Bottom, Top, viaFed: true, clear).Pad!.Faces);

        var near = Face();
        Assert.Equal(("Inner 8", 71e-6), (near.Plane.Name, Math.Round(near.HeightMeters, 9)));
        var next = Face("Inner 8");
        Assert.Equal(("Inner 7", 140e-6), (next.Plane.Name, Math.Round(next.HeightMeters, 9)));
        Assert.Equal(["Inner 8"], next.Passed);
    }

    // ── Test 6: the impedance tool reports what the model computes ───────────────────────────────

    [Fact]
    public void ImpedanceTool_OnALineWithAPadOffIt_ReportsTheModelsCAndL()
    {
        // A 50 Ω-ish Top Copper line into a through via, a 1.5 mm pad on Bottom Copper; the planes are undrawn.
        var shapes = new List<LayoutShape>
        {
            new RectShape { Layer = TopKey, X1 = 0, Y1 = -65_000, X2 = 20_000_000, Y2 = 65_000 },
            new CircleShape { Layer = TopKey, Cx = 20_000_000, Cy = 0, R = 300_000 },
            new ViaShape { Layer = ThroughDrill, X = 20_000_000, Y = 0, PadSize = 600_000, DrillSize = 300_000 },
            new CircleShape { Layer = BottomKey, Cx = 20_000_000, Cy = 0, R = 750_000 },
        };
        var report = TraceImpedanceAnalysis.Analyze(shapes, _tech, LayoutUnits.DefaultDbuPerMicron,
                                                    new TraceImpedanceOptions { Layers = [TopKey] });
        Assert.True(report.Ok, report.Refusal);
        var shunt = Assert.Single(report.Layers.SelectMany(l => l.Traces).SelectMany(t => t.PadShunts));

        var tp = Component(SymbolKind.TestPad, "TP1", ("PadLayer", Bottom), ("ViaFrom", Top));
        var values = TestPadInjection.Build(_tech, tp.Parameters).Overrides
            .ToDictionary(o => o.Name, o => new CircuitRF.Core.Expressions.Value(double.Parse(o.Expression, System.Globalization.CultureInfo.InvariantCulture)));
        values["D"] = new CircuitRF.Core.Expressions.Value(1.5e-3);
        var model = Assert.IsType<TestPadModel>(ComponentModelFactory.TryCreate("TESTPAD", values));

        Assert.Equal(["Inner 8"], shunt.ReferencePlanes);
        Assert.Equal(model.Geometry.PadCapacitance, shunt.Capacitance, 1e-18);
        Assert.Equal(model.Geometry.ViaInductance, shunt.ViaInductance!.Value, 1e-15);
        Assert.Equal(model.Geometry.BarrelCapacitance, shunt.BarrelCapacitance, 1e-18);
    }

    // ── brief-dfr26-8: the via's unused inner pads ───────────────────────────────────────────────

    private static readonly LayerKey[] InnerKeys = [new(10, 0), new(11, 0), new(12, 0), new(13, 0), new(18, 0), new(19, 0), new(20, 0), new(21, 0)];

    /// <summary>The model a schematic TESTPAD on the ten-layer board extracts to, with <paramref name="set"/> typed.</summary>
    private TestPadModel ExtractedModel(params (string Name, string Value)[] set)
    {
        var tp = Component(SymbolKind.TestPad, "TP1", [("PadLayer", Bottom), ("ViaFrom", Top), .. set]);
        var values = TestPadInjection.Build(_tech, tp.Parameters).Overrides
            .ToDictionary(o => o.Name, o => o.Expression.StartsWith('"')
                ? new CircuitRF.Core.Expressions.Value(o.Expression.Trim('"'))
                : new CircuitRF.Core.Expressions.Value(double.Parse(o.Expression, System.Globalization.CultureInfo.InvariantCulture)));
        values["D"] = new CircuitRF.Core.Expressions.Value(1.5e-3);
        foreach (var (n, v) in set)
            if (n is "Drill" or "InnerPadD" or "AntipadD" or "ClearD")
                values[n] = new CircuitRF.Core.Expressions.Value(double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) * 1e-3);
        return Assert.IsType<TestPadModel>(ComponentModelFactory.TryCreate("TESTPAD", values));
    }

    /// <summary>
    /// R-dfr26-8-1's gate, against an axisymmetric finite-difference solve of the same via (fixed reference data,
    /// computed outside circuitRF): the stub as built 1.446 pF, of which the eight 0.6 mm pads in 0.94 mm antipads are
    /// 0.192 pF. R-dfr28-5-4 adds the round-26 board itself: converged Palace puts the same eight pads at 0.218 pF, the
    /// test pad 1.4 mm off the via on its trace. The brief's Palace figures (3.36 and 1.61 pF, and 1.6-2.0 pF refined)
    /// had the via barrel on the ground net — src/Core/RESOLVED.md says why.
    /// </summary>
    [Fact]
    public void UnusedInnerPads_OnTheTenLayerBoard_AddWhatTheFieldSolveDoes()
    {
        var with = ExtractedModel(("Drill", "0.3"), ("InnerPadD", "0.6"), ("AntipadD", "0.94")).Geometry;
        var without = ExtractedModel(("Drill", "0.3"), ("AntipadD", "0.94")).Geometry;

        Assert.Equal(8, with.Pads.Count);
        Assert.Empty(without.Pads);
        Assert.InRange(with.StubCapacitance, 0.75 * 1.446e-12, 1.25 * 1.446e-12);
        Assert.InRange(with.StubCapacitance - without.StubCapacitance, 0.75 * 0.192e-12, 1.25 * 0.192e-12);
        Assert.InRange(with.StubCapacitance - without.StubCapacitance, 0.75 * 0.218e-12, 1.25 * 0.218e-12);
    }

    // ── brief-dfr27-1 R-dfr27-1-1: the pad's capacitance depends on ClearD ───────────────────────

    /// <summary>
    /// R-dfr27-1-1's gate on the shipped board: D 1.5 mm on Bottom, ClearLayers = Inner 8. The capacitance never rises
    /// as the hole grows, a ClearD of 0 is the uncleared pad, and at D + 0.5 mm it is within 25 % of an axisymmetric
    /// finite-volume solve of the pad alone over Inner 8 with that hole and a solid Inner 7: 0.575 pF (fixed reference
    /// data, computed outside circuitRF; 1.118 pF with no hole).
    /// </summary>
    [Fact]
    public void PadCapacitance_FallsWithClearD_FromTheUnclearedPad_ToTheFieldSolve()
    {
        double Pad(double clearMm) => ExtractedModel(("ClearLayers", "Inner 8"),
            ("ClearD", clearMm.ToString(System.Globalization.CultureInfo.InvariantCulture))).Geometry.PadCapacitance;

        double[] c = [.. Enumerable.Range(0, 46).Select(k => Pad(k * 0.1))];
        for (int k = 1; k < c.Length; k++) Assert.True(c[k] <= c[k - 1], $"rises at ClearD {k * 0.1:0.0} mm");
        Assert.Equal(ExtractedModel().Geometry.PadCapacitance, c[0], c[0] * 0.01);
        Assert.InRange(c[20], 0.75 * 0.575e-12, 1.25 * 0.575e-12);
    }

    // ── brief-dfr27-1 R-dfr27-1-2/3: the parameter dialog ────────────────────────────────────────

    /// <summary>A TESTPAD in a schematic of a workspace on the ten-layer board, open in the parameter dialog.</summary>
    private (SchematicViewModel Vm, EditableComponent Comp, ParameterEditorViewModel Editor) InDialog(params (string Name, string Value)[] set)
    {
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        TechPersistence.SaveToFile(Path.Combine(_root, "tech", "t.ctech"), _tech);
        WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "tech/t.ctech" });
        string schematicDir = Path.Combine(_root, "Pad", "schematic");
        Directory.CreateDirectory(schematicDir);

        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        var comp = Component(SymbolKind.TestPad, "TP1", set);
        model.Components.Add(comp);
        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);
        return (vm, comp, editor);
    }

    [Fact]
    public void ClearLayers_IsAChecklistOfTheConductors_WritingTheSameText()
    {
        var (_, comp, editor) = InDialog(("ClearLayers", "Inner 8, Gone"));
        var row = editor.Rows.Single(r => r.Name == "ClearLayers");
        string Text() => comp.Parameters.Single(p => p.Name == "ClearLayers").Expression;

        Assert.True(row.IsLayerChecklistParam);
        Assert.False(row.ShowExpressionTextBox);
        Assert.Equal(11, row.LayerCheckItems.Count);   // ten conductors, then the name that is not one
        Assert.Equal(["Inner 8", "Gone"], row.LayerCheckItems.Where(i => i.IsChecked).Select(i => i.Name));
        Assert.True(row.LayerCheckItems.Single(i => i.Name == "Gone").IsMissing);
        Assert.Contains("\"Gone\" is not a conductor", row.LayerChecklistMissingWarning);

        row.LayerCheckItems.Single(i => i.Name == "Inner 7").IsChecked = true;
        Assert.Equal("Inner 7, Inner 8, Gone", Text());   // stackup order, then the unknown name

        while (row.LayerCheckItems.FirstOrDefault(i => i.IsChecked) is { } on) on.IsChecked = false;
        Assert.Equal("", Text());
        Assert.False(row.HasLayerChecklistMissingWarning);
    }

    [Fact]
    public void RowsTheFeedDoesNotUse_AreGreyed_WithTheReason_AndKeepTheirValues()
    {
        var (vm, comp, editor) = InDialog(("Drill", "0.25"), ("FeedW", "0.4"));
        ParameterRowViewModel Row(string name) => editor.Rows.Single(r => r.Name == name);
        string[] viaRows = ["ViaFrom", "Drill", "InnerPadD", "AntipadD"];

        Assert.False(Row("FeedW").ExpressionEnabled);
        Assert.Contains("Feed = Via", Row("FeedW").DisabledReason);
        Assert.All(viaRows, n => Assert.True(Row(n).ExpressionEnabled));

        vm.Execute(new EditParameterCommand(vm.EditModel, comp.Parameters.Single(p => p.Name == "Feed"), "E", ""));
        Assert.True(Row("FeedW").ExpressionEnabled);
        Assert.All(viaRows, n => Assert.False(Row(n).ExpressionEnabled));
        Assert.StartsWith("Not used with Feed = E", Row("ViaFrom").NameTooltip);
        Assert.Equal("0.25", comp.Parameters.Single(p => p.Name == "Drill").Expression);
    }

    [Fact]
    public void InnerPadD_DrawsAPadOnEachPassedConductor_ButNotOnAClearedOne()
    {
        var r = Generate(("PadLayer", Bottom), ("ViaFrom", Top), ("InnerPadD", 0.6e-3), ("ClearLayers", "Inner 8"));
        var inner = r.Shapes.OfType<CircleShape>().Where(c => InnerKeys.Contains(c.Layer)).ToList();
        Assert.Equal(InnerKeys[..7], inner.Select(c => c.Layer));
        Assert.All(inner, c => Assert.Equal(300_000, c.R));
        Assert.DoesNotContain(Generate(("PadLayer", Bottom), ("ViaFrom", Top)).Shapes.OfType<CircleShape>(), c => InnerKeys.Contains(c.Layer));
    }

    [Fact]
    public void ImpedanceTool_ReportsEachUnusedLand_AndTheyAddWhatTheModelSays()
    {
        // Test 6's line and pad, with every inner conductor drawn as a plane round a 0.94 mm antipad holding a 0.6 mm land.
        var shapes = new List<LayoutShape>
        {
            new RectShape { Layer = TopKey, X1 = 0, Y1 = -65_000, X2 = 20_000_000, Y2 = 65_000 },
            new CircleShape { Layer = TopKey, Cx = 20_000_000, Cy = 0, R = 300_000 },
            new ViaShape { Layer = ThroughDrill, X = 20_000_000, Y = 0, PadSize = 600_000, DrillSize = 300_000 },
            new CircleShape { Layer = BottomKey, Cx = 20_000_000, Cy = 0, R = 750_000 },
        };
        long[] hole = [.. Enumerable.Range(0, 64).SelectMany(k => new[]
        {
            20_000_000 + (long)Math.Round(470_000 * Math.Cos(-k * Math.PI / 32)), (long)Math.Round(470_000 * Math.Sin(-k * Math.PI / 32)),
        })];
        foreach (var k in InnerKeys)
        {
            shapes.Add(new PolygonShape { Layer = k, Xy = [15_000_000, -5_000_000, 25_000_000, -5_000_000, 25_000_000, 5_000_000, 15_000_000, 5_000_000], Holes = [hole] });
            shapes.Add(new CircleShape { Layer = k, Cx = 20_000_000, Cy = 0, R = 300_000 });
        }
        var report = TraceImpedanceAnalysis.Analyze(shapes, _tech, LayoutUnits.DefaultDbuPerMicron,
                                                    new TraceImpedanceOptions { Layers = [TopKey] });
        Assert.True(report.Ok, report.Refusal);
        var shunt = Assert.Single(report.Layers.SelectMany(l => l.Traces).SelectMany(t => t.PadShunts));

        Assert.Equal(8, shunt.UnusedPads!.Count);
        Assert.Contains("CAM option", shunt.Text);
        double model = ExtractedModel(("Drill", "0.3"), ("InnerPadD", "0.6"), ("AntipadD", "0.94")).Geometry.InnerPadCapacitance;
        Assert.InRange(shunt.UnusedPadCapacitance, 0.9 * model, 1.1 * model);   // the land and antipad are measured, to 5-10 µm
    }

    // ── brief-dfr26-8 R3/R4: the Palace size estimate and an edge port on the air box ─────────────

    /// <summary>A 4 mm square of the ten-layer board: every inner conductor a plane round a 0.94 mm antipad holding a
    /// 0.6 mm land, a through via, a line from the board edge on Top and a 1.5 mm pad on Bottom; an edge port where the
    /// line meets the edge, with no air-box padding at the sides.</summary>
    private (EmSetup Setup, CircuitRF.Engine.Em3d.Em3dProblem Problem) Fixture(string cem)
    {
        var (setup, g) = Generated(cem);
        Assert.True(g.Ok, g.Refusal);
        return (setup, g.Problem!);
    }

    /// <summary><see cref="Fixture"/>'s generation, refusal and all; <paramref name="viaNet"/> is the via's own net.</summary>
    private (EmSetup Setup, CircuitRF.Design.Layout.Em3d.Em3dGenerationResult Result) Generated(string cem, string viaNet = "SIG",
                                                                                                Action<LayoutView>? edit = null)
    {
        const long c = 2_000_000;
        long[] hole = [.. Enumerable.Range(0, 64).SelectMany(k => new[]
        {
            c + (long)Math.Round(470_000 * Math.Cos(-k * Math.PI / 32)), c + (long)Math.Round(470_000 * Math.Sin(-k * Math.PI / 32)),
        })];
        var view = new LayoutView();
        view.Shapes.Add(new RectShape { Layer = TopKey, X1 = 0, Y1 = c - 65_000, X2 = c, Y2 = c + 65_000, Net = "SIG" });
        view.Shapes.Add(new CircleShape { Layer = TopKey, Cx = c, Cy = c, R = 300_000, Net = "SIG" });
        view.Shapes.Add(new CircleShape { Layer = BottomKey, Cx = c, Cy = c, R = 750_000, Net = "SIG" });
        foreach (var k in InnerKeys)
        {
            view.Shapes.Add(new PolygonShape { Layer = k, Xy = [0, 0, 4_000_000, 0, 4_000_000, 4_000_000, 0, 4_000_000], Holes = [hole], Net = "GND" });
            view.Shapes.Add(new CircleShape { Layer = k, Cx = c, Cy = c, R = 300_000, Net = "SIG" });
        }
        view.Shapes.Add(new ViaShape { Layer = ThroughDrill, X = c, Y = c, PadSize = 600_000, DrillSize = 300_000, LandingLayer = TopKey, Net = viaNet });
        view.Shapes.Add(new LabelShape { Layer = TopKey, X = 0, Y = c, Text = "P1", Height = 100_000, IsPort = true,
                                         PortDirection = LayoutRotation.R0, PortLayer = TopKey, PortKind = PlanarPortKind.Edge });
        edit?.Invoke(view);
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8] + ".cem");
        File.WriteAllText(path, cem);
        var setup = EmSetupPersistence.LoadFromFile(path);
        return (setup, CircuitRF.Design.Layout.Em3d.Em3dGenerator.Generate(setup, new EmLayoutSource("fixture.clay", view, _tech, view.DbuPerMicron), _tech));
    }

    /// <summary>R-dfr26-8-3 — an electrostatic estimate is in Palace's scalar unknowns, sized as the script sizes a static
    /// mesh. Pinned to one run of this fixture (Gmsh 4.15.2, Palace 0.18.1, Standard): 81,052 tetrahedra and 125,324
    /// unknowns at order 2, 1.8 GB; priced as a driven problem's it read several times that.</summary>
    [Fact]
    public void PalaceEstimate_OfAStaticMultilayerFixture_IsWithinTwiceGmshAndPalace()
    {
        var (setup, p) = Fixture("""
            { "FormatVersion": 1, "Name": "es", "LayoutRef": "fixture.clay", "Solver3D": "Palace", "Problem3D": "Electrostatic",
              "Terminals3D": [ { "Name": "SIG", "Net": "SIG" } ], "Ground3D": "GND",
              "Palace": { "Quality": "Standard", "AdaptiveMaxIterations": 0 } }
            """);
        var est = CircuitRF.Design.Em3d.Em3dRunService.EstimatePalace(p, CircuitRF.Design.Layout.Em.PalaceSettings.Resolve(setup.Palace))!;
        Assert.InRange(est.Unknowns / 125_324.0, 0.5, 2.0);
        Assert.InRange(est.Tetrahedra / 81_052.0, 0.5, 2.0);
    }

    /// <summary>R-dfr26-8-4 — an edge port at a board edge the air box meets with no padding lies IN the box face; it is
    /// claimed before the faces, or the face takes its sheet and the run is refused ("selected nothing in the mesh").
    /// This fixture was refused so before, and solved after. Since brief DFR28-3 the generator first pads an ABSORBING face
    /// clear of the sheet (in it, the port shares its power with the absorber), so the port lies inside the box; a PEC face
    /// is never moved, and a port in one is still claimed before the faces.</summary>
    [Fact]
    public void ALumpedPortOnAnAirBoxFace_IsClaimedBeforeTheFace()
    {
        string Cem(string xmin) => $$"""
            { "FormatVersion": 1, "Name": "drv", "LayoutRef": "fixture.clay", "Solver3D": "Palace",
              "Frequency": { "StartExpr": "1", "StopExpr": "3", "StepExpr": "", "NumPoints": 3, "Mode": "PointCount", "Kind": "Linear",
                             "StartUnit": "GHz", "StopUnit": "GHz", "StepUnit": "Hz" },
              "Palace": { "Quality": "Draft" },
              "AirBox": { "XMin": { "PaddingUm": 0, "Boundary": "{{xmin}}" }, "XMax": { "PaddingUm": 0, "Boundary": "Absorbing" },
                          "YMin": { "PaddingUm": 0, "Boundary": "Absorbing" }, "YMax": { "PaddingUm": 0, "Boundary": "Absorbing" },
                          "ZMin": { "PaddingUm": 4000, "Boundary": "Absorbing" }, "ZMax": { "PaddingUm": 4000, "Boundary": "Absorbing" } } }
            """;
        static double EdgeX(CircuitRF.Engine.Em3d.Em3dProblem p) => Assert.Single(p.Ports, q => Math.Abs(q.Min.X - q.Max.X) < 1e-12).Min.X;

        var (setup, absorbing) = Generated(Cem("Absorbing"));
        Assert.True(absorbing.Ok, absorbing.Refusal);
        var settings = CircuitRF.Design.Layout.Em.PalaceSettings.Resolve(setup.Palace);
        Assert.True(absorbing.Problem!.Boundary.Min.X < EdgeX(absorbing.Problem), "the absorbing xmin face is padded clear of the port");
        Assert.Contains(absorbing.Notes, n => n.Contains("xmin face", StringComparison.Ordinal) && n.Contains("padded", StringComparison.Ordinal));
        Assert.Contains("q0[] = ", CircuitRF.Design.Em3d.GmshGeoWriter.Write(absorbing.Problem, settings).Geo!);

        var p = Fixture(Cem("Pec")).Problem;
        Assert.Equal(EdgeX(p), p.Boundary.Min.X, 12);
        string geo = CircuitRF.Design.Em3d.GmshGeoWriter.Write(p, settings).Geo!;
        Assert.True(geo.IndexOf("q0[] = ", StringComparison.Ordinal) is var port and >= 0
                    && port < geo.IndexOf("// air box", StringComparison.Ordinal), "the port is claimed after the air-box faces");
    }

    // ── brief-dfr28-5: a via on one net passing another's copper ─────────────────────────────────

    /// <summary>R-dfr28-5 — the TP149 study's fault: the via drawn on GND inside the terminal's lands and pads.
    /// brief-dfr34-4 D1 (owner, 2026-11-04) — every piece of copper it passes is SIG, so the via is built as SIG: a static solve
    /// gives it that net and says so, naming the drill shape, with the layout edit that makes it so; a driven solve builds too.</summary>
    [Fact]
    public void AViaOnGround_InsideTheTerminalsCopper_TakesTheCoppersNetInAStaticSolve_AndOffersTheLayoutEdit()
    {
        LayoutView? view = null;
        var (_, es) = Generated("""
            { "FormatVersion": 1, "Name": "es", "LayoutRef": "fixture.clay", "Solver3D": "Palace", "Problem3D": "Electrostatic",
              "Terminals3D": [ { "Name": "SIG", "Net": "SIG" } ], "Ground3D": "GND" }
            """, viaNet: "GND", edit: v => view = v);
        Assert.True(es.Ok, es.Refusal);
        string note = Assert.Single(es.Notes, n => n.Contains("is on net 'GND', and every piece of copper it passes is on net 'SIG'", StringComparison.Ordinal));
        Assert.Contains("The via at (2, 2) mm (drill shape 1 on layer", note);
        Assert.Contains(es.Problem!.Terminals.Single().Objects, o => o == "via/1");
        var fix = es.ViaNetFixes[note];
        Assert.Equal(("GND", "SIG"), (fix.FromNet, fix.ToNet));
        Assert.Same(view!.Shapes.OfType<ViaShape>().Single(), fix.Find(view.Shapes));

        var (_, driven) = Generated("""
            { "FormatVersion": 1, "Name": "drv", "LayoutRef": "fixture.clay", "Solver3D": "Palace",
              "Frequency": { "StartExpr": "1", "StopExpr": "3", "StepExpr": "", "NumPoints": 3, "Mode": "PointCount", "Kind": "Linear",
                             "StartUnit": "GHz", "StopUnit": "GHz", "StepUnit": "Hz" } }
            """, viaNet: "GND");
        Assert.True(driven.Ok, driven.Refusal);
    }

    /// <summary>brief-dfr34-4 R-dfr34-4-4 — a via whose copper is on two nets joins them, so a static solve still refuses it, and
    /// names the drill shape by its layer and place as well as the copper of each net.</summary>
    [Fact]
    public void AViaPassingCopperOfTwoNets_IsRefused_NamingTheDrillShape()
    {
        var (_, es) = Generated("""
            { "FormatVersion": 1, "Name": "es", "LayoutRef": "fixture.clay", "Solver3D": "Palace", "Problem3D": "Electrostatic",
              "Terminals3D": [ { "Name": "SIG", "Net": "SIG" } ], "Ground3D": "GND" }
            """, viaNet: "GND", edit: v => v.Shapes.OfType<CircleShape>().First(c => InnerKeys.Contains(c.Layer)).Net = "OTHER");
        Assert.False(es.Ok);
        Assert.Contains("'via/1': the via at (2, 2) mm (drill shape 1 on layer", es.Refusal);
        Assert.Contains("net 'SIG'", es.Refusal);
        Assert.Contains("net 'OTHER'", es.Refusal);
    }
}
