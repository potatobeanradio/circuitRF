using CircuitRF.Core.Design;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// brief-via-component.md §6 gates 3 and 4 — the VIA resolved from a real stackup, extracted into the
/// netlist a run consumes, and laid out beside two MLINs.
/// </summary>
public sealed class ViaComponentTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-via-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private const string FourLayer = "pcb-4layer_FR-4_62mil_1oz";
    private const string Top = "Top Copper (1 oz)", Inner1 = "Inner 1 (Ground Plane)", Inner2 = "Inner 2", Bottom = "Bottom Copper (1 oz)";

    private static EditableComponent Component(SymbolKind kind, string name, params (string Name, string Value)[] set)
    {
        var comp = new EditableComponent { InstanceName = name, Symbol = kind };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 0))
            comp.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, Dimension = dp.Dimension });
        foreach (var (n, v) in set) comp.Parameters.Single(p => p.Name == n).Expression = v;
        return comp;
    }

    /// <summary>Gate 3. On the shipped 4-layer FR-4, Top to Inner 2: the barrel is the mid-plane to
    /// mid-plane distance, it passes exactly Inner 1, and the through drill leaves a stub to Bottom —
    /// every number read back from the stackup itself.</summary>
    [Fact]
    public void TopToInner2_OnTheShippedFourLayerBoard_ResolvesFromTheStackup()
    {
        var tech = ShippedTechnologies.Load(FourLayer);
        var layers = tech.Stackup.Layers;
        double M(string name) => layers.Single(l => l.Name == name).ThicknessDbu * 1e-9;
        StackupLayer D(string name) => layers.Single(l => l.Name == name);

        var (span, failure, _) = SubstrateResolver.ResolveViaSpan(tech, Top, Inner2, toGround: false);
        Assert.Null(failure);
        Assert.NotNull(span);

        double expectedH = M(Top) / 2 + M("Prepreg (top)") + M(Inner1) + M("Core") + M(Inner2) / 2;
        Assert.Equal(expectedH, span!.LengthMeters, 1e-12);

        var plane = Assert.Single(span.Planes);
        Assert.Equal(Inner1, plane.Name);
        Assert.Equal((M("Prepreg (top)") + M("Core")) / 2, plane.ThicknessMeters, 1e-12);
        Assert.Equal(D("Core").Epsr, plane.RelativePermittivity, 1e-12);   // both dielectrics are 4.4

        Assert.Equal("Plated Through-Hole", span.ViaEntry!.Name);
        var stub = span.StubBeyondTo!;
        Assert.Equal(Bottom, stub.EndConductorName);
        Assert.Equal(M(Inner2) / 2 + M("Prepreg (bottom)") + M(Bottom) / 2, stub.LengthMeters, 1e-12);
        // The drill ends in Bottom, which is a ground plane: the stub passes it with the half of the
        // prepreg above it.
        Assert.Equal(Bottom, Assert.Single(stub.Planes).Name);
        Assert.Null(span.StubBeyondFrom);

        Assert.Equal(D("Plated Through-Hole").WallThicknessDbu!.Value * 1e-9, span.PlatingMeters, 1e-15);
        Assert.Equal(tech.DefaultViaDrillDbu * 1e-9, span.DrillMeters!.Value, 1e-15);
        Assert.Equal(4.4, span.MaxRelativePermittivity, 1e-12);
    }

    [Fact]
    public void ADefaultVia_GoesToTheFarthestLayerThatIsNotAPlane_AndAViaToGround_ToTheNearestPlane()
    {
        var tech = ShippedTechnologies.Load(FourLayer);
        var (via, _, _) = SubstrateResolver.ResolveViaSpan(tech, null, null, toGround: false);
        Assert.Equal((Top, Inner2), (via!.From.Name, via.To.Name));

        var (gnd, _, _) = SubstrateResolver.ResolveViaSpan(tech, null, null, toGround: true);
        Assert.Equal((Top, Inner1), (gnd!.From.Name, gnd.To.Name));
        // The blind Top-to-Inner-1 drill is the shorter span that covers it, so there is no stub.
        Assert.Equal("Ground Via (L1-L2)", gnd.ViaEntry!.Name);
        Assert.Null(gnd.StubBeyondTo);
        Assert.Empty(gnd.Planes);
    }

    /// <summary>Designer feedback round 34 — an imported board's spanless "Drill (non-plated)" entry, ahead of the plated
    /// one in the stackup, is never the drill a via is made on: its holes join nothing.</summary>
    [Fact]
    public void ANonPlatedEntry_IsNeverTheViasDrill()
    {
        var tech = ShippedTechnologies.Load(FourLayer);
        var layers = tech.Stackup.Layers;
        int first = layers.FindIndex(l => l.Kind == StackupKind.Via);
        layers.Insert(first, new StackupLayer { Name = "Drill (non-plated)", Kind = StackupKind.Via, Plated = false });

        var (span, failure, _) = SubstrateResolver.ResolveViaSpan(tech, Top, Bottom, toGround: false);
        Assert.Null(failure);
        Assert.NotEqual("Drill (non-plated)", span!.ViaEntry!.Name);
    }

    /// <summary>What a run consumes: the layer names gone, nothing empty written, the resolved barrel
    /// injected, the defaults the technology does not state named — and VIAGND's far end is ground.</summary>
    [Fact]
    public void Extraction_InjectsTheStackup_AndNamesTheDefaults()
    {
        string cellDir = CellFolder.CreateCellFolder(_root, "Via");
        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        var tech = ShippedTechnologies.Load(FourLayer);

        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(Component(SymbolKind.Via, "VIA1", ("FromLayer", Top), ("ToLayer", Inner2)));
        var injection = ViaSubstrateInjection.Build(tech, SymbolKind.Via, model.Components[0].Parameters);

        var names = injection.Overrides.Select(o => o.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { "H", "Sigma", "Planes", "Tp1", "Erp1", "Hstub", "StubPlanes", "Drill", "Pad", "Plating" }, names);
        Assert.DoesNotContain("Antipad", names);                       // the model's own default: pad + 0.3 mm

        var gnd = Component(SymbolKind.ViaGnd, "VIAG1");
        var gi = ViaSubstrateInjection.Build(tech, SymbolKind.ViaGnd, gnd.Parameters);
        Assert.Contains(gi.Overrides, o => o.Name == "Tpad");
        Assert.DoesNotContain(gi.Overrides, o => o.Name == "Hstub");

        // A technology that states no drill still has that default NAMED.
        tech.DefaultViaDrillDbu = 0;
        var bare = ViaSubstrateInjection.Build(tech, SymbolKind.Via, Component(SymbolKind.Via, "VIA2").Parameters);
        Assert.Contains(bare.Messages, m => m.Contains("Drill") && m.Contains("default", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A via placed as it comes, on any technology circuitRF ships, says nothing: every
    /// dimension the technology can state, it states, and the antipad is circuitRF's own rule rather than
    /// something the technology failed to give — so the editor shows no warning and the run posts none.</summary>
    [Fact]
    public void PlacedWithDefaults_OnEveryShippedTechnology_NothingIsWarned()
    {
        foreach (var entry in ShippedTechnologies.All)
        foreach (var kind in new[] { SymbolKind.Via, SymbolKind.ViaGnd })
        {
            var tech = ShippedTechnologies.Load(entry);
            var comp = Component(kind, "V1");
            var injection = ViaSubstrateInjection.Build(tech, kind, comp.Parameters);
            Assert.True(injection.Messages.Count == 0, $"{entry.Id} {kind}: {string.Join(" | ", injection.Messages)}");
            Assert.NotNull(ViaSubstrateInjection.Evaluate(tech, kind, comp.Parameters, out string? note));
            Assert.True(note is null, $"{entry.Id} {kind}: {note}");
        }
    }

    /// <summary>
    /// Gate 4. A line on Top, a VIA, a line on Inner 2 returning through Inner 1 above it: laid out
    /// from the schematic and butted together, each side of the via is ONE net — Top line to A, B to the
    /// Inner 2 line — and the two are not shorted by the barrel. The pour offered for Inner 1 cuts the
    /// via's own antipad out of it.
    /// </summary>
    [Fact]
    public void MlinViaMlin_LaidOut_ConnectsEachSide_AndThePlaneTakesTheAntipad()
    {
        string cellDir = CellFolder.CreateCellFolder(_root, "Transition");
        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);
        var tech = ShippedTechnologies.Load(FourLayer);

        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(Component(SymbolKind.Mlin, "ML1", ("SignalLayer", Top)));
        model.Components.Add(Component(SymbolKind.Via, "VIA1", ("FromLayer", Top), ("ToLayer", Inner2), ("Antipad", "1.2")));
        model.Components.Add(Component(SymbolKind.Mlin, "ML2", ("SignalLayer", Inner2), ("GroundReference", Inner1)));
        var target = new LayoutView();
        var run = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, null, null);
        run.Command!.Execute();
        Assert.Equal(3, target.Instances.Count);

        LayoutInstance Inst(string id) => target.Instances.Single(i => i.SchematicId == id);
        var ml1 = Inst("ML1");
        var bb = CellHierarchy.InstanceBbox(ml1, layoutDir);
        ml1.X -= bb.MaxX;           // ML1's pin 2 onto the origin
        Inst("VIA1").X = 0; Inst("VIA1").Y = 0;
        Inst("ML2").X = 0; Inst("ML2").Y = 0;   // ML2's pin 1 onto the origin

        string clay = Path.Combine(layoutDir, "Transition.clay");
        LayoutPersistence.SaveToFile(clay, target);
        var netlist = LayoutRead.Read(target, clay, cellDir, tech);
        foreach (var d in netlist.Devices)
            output.WriteLine($"{d.Path} {d.Type.Kind}: {string.Join(", ", d.Terminals.Select(t => $"{t.Name}->{t.NetIndex}"))}");

        LvsDevice Dev(string id) => netlist.Devices.Single(d => d.Path.Contains(id));
        int Net(string id, string terminal) => Dev(id).Terminals.Single(t => t.Name == terminal).NetIndex;
        Assert.Equal(DeviceKind.Via, Dev("VIA1").Type.Kind);
        Assert.Equal(Net("ML1", "2"), Net("VIA1", "A"));
        Assert.Equal(Net("VIA1", "B"), Net("ML2", "1"));
        Assert.NotEqual(Net("VIA1", "A"), Net("VIA1", "B"));

        // Both lines return through Inner 1, so it is offered as one pour — with the via's own 1.2 mm
        // antipad cut where the barrel passes it.
        var pour = Assert.Single(GroundPourPlanner.Plan(target, tech, layoutDir), p => p.Ground.Name == Inner1);
        Assert.Equal(1, pour.ViaClearances);
        var poly = Assert.IsType<PolygonShape>(Assert.Single(pour.Shapes));
        var hole = Assert.Single(poly.Holes!);
        long minX = hole.Where((_, i) => i % 2 == 0).Min(), maxX = hole.Where((_, i) => i % 2 == 0).Max();
        Assert.InRange(maxX - minX, 1_199_000, 1_220_000);   // circumscribed 48-gon of a 1.2 mm disc
        Assert.InRange((minX + maxX) / 2, -2_000, 2_000);
    }

    /// <summary>The worked example the component's help page gives, on the shipped two-layer FR-4 board:
    /// what a VIAGND at the technology's own via computes, in place of a hand-added shunt capacitor. Held
    /// here so the page and the model cannot quietly disagree.</summary>
    [Fact]
    public void TheHelpPagesWorkedExample_IsWhatTheModelComputes()
    {
        var tech = ShippedTechnologies.Load("pcb-2layer_FR-4_70mil_1oz");
        var gnd = Component(SymbolKind.ViaGnd, "VIAG1");
        var e = ViaSubstrateInjection.Evaluate(tech, SymbolKind.ViaGnd, gnd.Parameters, out string? note);
        Assert.Null(note);
        Assert.Equal(0.650, e!.Inductance * 1e9, 3);        // "0.65 nH"
        Assert.Equal(0.88, e.Capacitance * 1e12, 2);        // "0.88 pF"
        Assert.Equal(1.4, e.DcResistance * 1e3, 1);         // "1.4 mΩ"
        Assert.Equal(3.9, e.ValidityFrequency / 1e9, 1);    // "valid to 3.9 GHz"
    }

    /// <summary>A placed VIA as the run sees it: extracted, written as a .cnl, read back and elaborated.
    /// Its rows start empty and its IncludeC as the word "true", and neither may stop the run.</summary>
    [Fact]
    public void APlacedVia_AtItsDefaults_ExtractsAndElaborates()
    {
        var model = new SchematicEditModel();
        model.Components.Add(Component(SymbolKind.Via, "VIA1"));
        model.Components.Add(Component(SymbolKind.ViaGnd, "VIAG1"));
        string cnl = SchematicCircuit.CnlTextOf(model, "tb");
        output.WriteLine(cnl);
        Assert.DoesNotContain("=\n", cnl.Replace("\r", ""));
        Assert.DoesNotContain("FromLayer", cnl);

        var (lib, tb) = new CircuitRF.Core.Netlist.CnlReader().Read(cnl);
        var nl = new CircuitRF.Core.Elaboration.Elaborator(lib).Elaborate(tb);
        var via = Assert.IsType<CircuitRF.Core.Devices.ViaModel>(nl.Components.Single(c => c.InstancePath.EndsWith("VIA1")).Model);
        Assert.True(via.Geometry.IncludeCapacitance);
        Assert.IsType<CircuitRF.Core.Devices.ViaGroundModel>(nl.Components.Single(c => c.InstancePath.EndsWith("VIAG1")).Model);
    }
}
