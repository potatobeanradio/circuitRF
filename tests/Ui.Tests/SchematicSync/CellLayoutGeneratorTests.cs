using CircuitRF.Core.Design;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Mom;
using Xunit;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// A library cell whose <c>.ccell</c> names the PCell that draws it (<c>LayoutGenerator</c>). The design is the schematic-driven
/// layout set's C1 (three MLINs between two ports) with the middle line made an instance X2 of a library cell, Line: ports a, b and
/// sub, a resistor and a capacitor inside, drawn by the built-in MLIN, a mapped to pin 1, b to pin 2 and sub to no pin (a bulk
/// terminal the generator does not draw). Synthetic throughout, and no Python.
/// </summary>
public sealed class CellLayoutGeneratorTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-cellgen-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    private const string Map = """[ { "Port": 1, "Name": "a", "LayoutPin": "1" }, { "Port": 2, "Name": "b", "LayoutPin": "2" }, { "Port": 3, "Name": "sub", "LayoutPin": "" } ]""";

    /// <summary>Update Layout places X2 as MLIN at the instance's own values — W an expression of the schematic's VAR, L stated
    /// with its unit — joined to its neighbours; LVS pairs it with the generated instance and is clean, saying once that sub is
    /// not drawn.</summary>
    [Fact]
    public void ACellDrawnByAGenerator_IsPlacedAsIt_AndLvsPairsIt()
    {
        var p = Design("placed", "MLIN", Map);
        var (plan, layout) = Run(p);

        Assert.Empty(plan.NoLayoutWarnings);
        var x2 = Assert.Single(layout.Instances, i => i.SchematicId == "X2");
        var origin = PCellOriginOf(p, x2);
        Assert.Equal("MLIN", origin.GeneratorId);
        Assert.Equal(1.1e-3, origin.Parameters["W"].AsReal(), 12);   // w = 1.1 mm, from the VAR
        Assert.Equal(3e-3, origin.Parameters["L"].AsReal(), 12);
        Assert.Contains(plan.Lines, l => l.Text.Contains("Joined 2 of 2 connections", StringComparison.Ordinal));

        var lvs = LvsRun.Run(p.CellDir);
        Clean(lvs);
        Assert.Contains(lvs.Comparison.Devices, d => d.SchematicName == "X2");
        var notDrawn = Assert.Single(lvs.Findings, f => f.Id == "lvs.terminal.not-drawn");
        Assert.Equal(3, notDrawn.Diagnostic.Arguments["port"]);
    }

    /// <summary>A cell with its own layout view as well (its document for its own EM run) is still placed as the generator, and
    /// LVS pairs it as one leaf device without descending into the cell. Without the field the same cell is placed as its own
    /// layout and LVS descends into it, as before.</summary>
    [Fact]
    public void TheGeneratorWins_OverTheCellsOwnLayout_AndLvsDoesNotDescend()
    {
        var p = Design("own", "MLIN", Map, ownLayout: true);
        var (plan, layout) = Run(p);
        Assert.Empty(plan.NoLayoutWarnings);
        Assert.Equal("MLIN", PCellOriginOf(p, Assert.Single(layout.Instances, i => i.SchematicId == "X2")).GeneratorId);
        var lvs = LvsRun.Run(p.CellDir);
        Assert.Empty(lvs.Cells);
        Clean(lvs);

        var before = Design("own-no-field", null, Map, ownLayout: true);
        var (_, laid) = Run(before);
        Assert.EndsWith("Line", Assert.Single(laid.Instances, i => i.SchematicId == "X2").CellRef.TrimEnd('/'), StringComparison.Ordinal);
        Assert.NotEmpty(LvsRun.Run(before.CellDir).Cells);
    }

    /// <summary>A cell without the field is untouched: a schematic-only cell is still skipped with the same sentence, and LVS still
    /// reports its instance as a part the artwork lacks.</summary>
    [Fact]
    public void ACellWithoutTheField_BehavesAsBefore()
    {
        var p = Design("plain", null, Map);
        var (plan, layout) = Run(p);
        Assert.Contains("X2 (", Assert.Single(plan.NoLayoutWarnings), StringComparison.Ordinal);
        Assert.EndsWith("referenced cell has no layout view — skipped.", plan.NoLayoutWarnings[0], StringComparison.Ordinal);
        Assert.DoesNotContain(layout.Instances, i => i.SchematicId == "X2");
        var lvs = LvsRun.Run(p.CellDir);
        Assert.Contains(lvs.Findings, f => f.Id == "lvs.device.unmatched-schematic" && f.Objects.Contains("X2"));
        Assert.Contains(lvs.Findings, f => f.Id == "lvs.net.floating-copper");   // the ground pour, with or without X2's line
        Assert.Equal(EmPartKind.NoLayout, EmPartKinds.Of(X2(p), p.SchematicDir));
        Assert.Empty(TerminalMap.ValidateCell(Path.Combine(p.Workspace, "Line")));
    }

    /// <summary>An unknown generator is one plain warning naming the cell and the generator, and <c>check</c> says so; a map naming
    /// a pin the generator does not draw is an error in <c>check</c> and an open terminal in LVS.</summary>
    [Fact]
    public void AnUnknownGenerator_AndAWrongPin_AreSaid()
    {
        var unknown = Design("unknown", "NO_SUCH_CELL", Map);
        var (plan, _) = Run(unknown);
        string warning = Assert.Single(plan.NoLayoutWarnings);
        Assert.Contains("\"Line\"", warning, StringComparison.Ordinal);
        Assert.Contains("\"NO_SUCH_CELL\"", warning, StringComparison.Ordinal);
        Assert.Contains("import the kit that provides it", warning, StringComparison.Ordinal);
        Assert.Contains(TerminalMap.ValidateCell(Path.Combine(unknown.Workspace, "Line")),
                        d => d.Id == "check.terminals.generator-not-registered");

        var wrong = Design("wrong", "MLIN", Map.Replace("\"LayoutPin\": \"2\"", "\"LayoutPin\": \"P2\"", StringComparison.Ordinal));
        var check = TerminalMap.ValidateCell(Path.Combine(wrong.Workspace, "Line"));
        var bad = Assert.Single(check, d => d.Id == "check.terminals.generator-pin-unknown");
        Assert.Equal("P2", bad.Arguments["pin"]);
        Run(wrong);
        Assert.Contains(LvsRun.Run(wrong.CellDir).Findings, f => f.Severity == DiagnosticSeverity.Error && f.Render().Contains("X2", StringComparison.Ordinal));
    }

    /// <summary>LVS and EM read the cell as its generator's part: OfSchematic names MLIN's kind, and an EM cell's <c>"*"</c> cuts
    /// the placed instance out by its SchematicId, as it does a kit part's.</summary>
    [Fact]
    public void TheCellIsItsGeneratorsPart_ToLvsAndEm()
    {
        var p = Design("kinds", "MLIN", Map);
        var type = DeviceTypes.OfSchematic(X2(p), p.SchematicDir);
        Assert.Equal(DeviceKind.TransmissionLine, type.Kind);
        Assert.Null(type.CellDir);
        Assert.Equal(EmPartKind.Part, EmPartKinds.Of(X2(p), p.SchematicDir));

        Run(p);
        var created = EmSetupCreate.Create(p.Workspace, "C1", EmSetupCreate.LayoutRefOf(p.Workspace, p.ClayPath), s =>
        {
            s.AnalysisKind = EmAnalysisKind.Planar;
            s.Frequency = new FrequencySpec("2", "2", 1, SweepKind.Linear, "GHz", "GHz");
            s.ComponentPorts = [new EmComponentPort("*") { PortAt = EmComponentPortAt.Cell }];
        });
        var layout = LayoutPersistence.LoadFromFile(p.ClayPath);
        var (tech, _) = TechnologyResolver.ResolveForDocument(layout.TechRef, p.ClayPath, Path.Combine(p.Workspace, ".cws"), new TechnologyCache());
        var build = EmPlanarComponentPorts.Build(EmSetupPersistence.LoadFromFile(created.Path!),
                                                 new EmLayoutSource(p.ClayPath, layout, tech.Tech, layout.DbuPerMicron))!;
        var cut = build.Ports.Where(q => q.Cutout && q.Refdes == "X2").ToList();
        Assert.NotEmpty(cut);
        Assert.All(cut, q => Assert.Equal("X2", q.SchematicId));
    }

    /// <summary>With no terminal map the cell reads as a kit part reads: by the generated cell's own map, port for port, its
    /// extra port left uncompared and unsaid — the behaviour the declared map's "not drawn" note makes visible.</summary>
    [Fact]
    public void WithNoMap_ThePlacementReadsAsAKitPartDoes()
    {
        var p = Design("nomap", "MLIN", "null");
        Run(p);
        var lvs = LvsRun.Run(p.CellDir);
        Clean(lvs);
        Assert.DoesNotContain(lvs.Findings, f => f.Id == "lvs.terminal.not-drawn");
    }

    // ── Parameters: the instance's, else the cell's default, else the generator's; a declared count arrives as an Int ──────────

    private const string KitGenerator = "cellgen_test_coil";

    [Fact]
    public void Parameters_InstanceThenCellThenGenerator_AndACountStaysAnInt()
    {
        var seen = new List<IReadOnlyDictionary<string, PCellValue>>();
        var resolver = new CoilResolver(seen);
        PCellRegistry.AddResolver(resolver);
        try
        {
            string ws = Path.Combine(_scratch, "params");
            Directory.CreateDirectory(ws);
            LibraryCell(ws, "Coil", KitGenerator,
                """[ { "Port": 1, "Name": "la", "LayoutPin": "LA" }, { "Port": 2, "Name": "lb", "LayoutPin": "LB" } ]""",
                [new CcellParameter { Name = "turns", DefaultExpression = "3" },
                 new CcellParameter { Name = "s", DefaultExpression = "5", Unit = "um" },
                 new CcellParameter { Name = "w", DefaultExpression = "2*s" }],
                [("la", -200f, 0f), ("lb", 200f, 0f)]);
            string here = Path.Combine(ws, "Top", "schematic");
            Directory.CreateDirectory(here);

            var model = new SchematicEditModel { SchematicDirectory = here };
            var vars = new EditableComponent { InstanceName = "VAR1", Symbol = SymbolKind.Var };
            vars.Parameters.Add(new EditableParameter { Name = "n", Expression = "3" });
            model.Components.Add(vars);
            var l1 = new EditableComponent { InstanceName = "L1", Symbol = SymbolKind.Generic, CellRef = "../../Coil" };
            model.Components.Add(l1);

            void Plan()
                => SchematicLayoutPlanner.Plan(model, new LayoutView(), here, ws, Path.Combine(ws, "Top", "layout"), null, null,
                                               DiskCellResolver.Instance);

            Plan();   // nothing stated: the cell's defaults, w through s in the cell's scope; gap the generator's own
            var first = Assert.Single(seen);
            Assert.Equal(PCellValue.Int(3), first["turns"]);
            Assert.Equal(1e-5, first["w"].AsReal(), 15);
            Assert.Equal(PCellValue.Real(2e-6), first["gap"]);
            Assert.False(first.ContainsKey("s"));   // not the generator's parameter

            seen.Clear();
            l1.Parameters.Add(new EditableParameter { Name = "turns", Expression = "n+1" });
            l1.Parameters.Add(new EditableParameter { Name = "s", Expression = "7", Unit = "um" });
            Plan();   // the instance's own values, an expression of the schematic's VAR; w follows the instance's s
            var second = Assert.Single(seen);
            Assert.Equal(PCellValue.Int(4), second["turns"]);
            Assert.Equal(1.4e-5, second["w"].AsReal(), 15);
        }
        finally { PCellRegistry.RemoveResolver(resolver); }
    }

    /// <summary>A placed cell carries its schematic's variables as empty rows the instance may fill. An empty row is unstated,
    /// not "no value set": it takes the cell's own variable of that name, evaluated where the cell's other variables are
    /// bound (gap = 2*g0), else the generator's default (w), and the part is placed. A cell variable the generator does not
    /// declare and the instance has no row for is not handed over.</summary>
    [Fact]
    public void AnEmptyRow_IsUnstated_AndTakesTheCellsVariable()
    {
        var seen = new List<IReadOnlyDictionary<string, PCellValue>>();
        var resolver = new CoilResolver(seen);
        PCellRegistry.AddResolver(resolver);
        try
        {
            string ws = Path.Combine(_scratch, "rows");
            Directory.CreateDirectory(ws);
            LibraryCell(ws, "Dev", KitGenerator,
                """[ { "Port": 1, "Name": "la", "LayoutPin": "LA" }, { "Port": 2, "Name": "lb", "LayoutPin": "LB" } ]""",
                [], [("la", -200f, 0f), ("lb", 200f, 0f)],
                vars: [("turns", "5"), ("g0", "1.5e-6"), ("gap", "2*g0"), ("extra", "7")]);
            string here = Path.Combine(ws, "Top", "schematic");
            Directory.CreateDirectory(here);

            var model = new SchematicEditModel { SchematicDirectory = here };
            var x1 = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = "../../Dev" };
            foreach (string row in new[] { "turns", "gap", "w" }) x1.Parameters.Add(new EditableParameter { Name = row, Expression = "" });
            model.Components.Add(x1);

            var plan = SchematicLayoutPlanner.Plan(model, new LayoutView(), here, ws, Path.Combine(ws, "Top", "layout"), null, null,
                                                   DiskCellResolver.Instance);

            Assert.Empty(plan.NoLayoutWarnings);
            var passed = Assert.Single(seen);
            Assert.Equal(PCellValue.Int(5), passed["turns"]);
            Assert.Equal(3e-6, passed["gap"].AsReal(), 15);
            Assert.Equal(PCellValue.Real(4e-6), passed["w"]);
            Assert.False(passed.ContainsKey("g0"));
            Assert.False(passed.ContainsKey("extra"));
        }
        finally { PCellRegistry.RemoveResolver(resolver); }
    }

    /// <summary>A kit generator declaring a count (turns, Int), a width and a gap, and drawing two pins, LA and LB.</summary>
    private sealed class CoilResolver(List<IReadOnlyDictionary<string, PCellValue>> seen) : IPCellGeneratorResolver
    {
        private static readonly Dictionary<string, PCellValue> Declared = new(StringComparer.Ordinal)
        {
            ["turns"] = PCellValue.Int(2),
            ["w"]     = PCellValue.Real(4e-6),
            ["gap"]   = PCellValue.Real(2e-6),
        };

        public IReadOnlyCollection<string> KnownGeneratorIds => [KitGenerator];
        public string Describe() => "test resolver";
        public string? ContentKeyFor(string generatorId) => generatorId == KitGenerator ? "v1" : null;
        public IReadOnlyDictionary<string, PCellValue>? DeclaredDefaults(string generatorId) => generatorId == KitGenerator ? Declared : null;

        public PCellGenerator? Resolve(string generatorId)
            => generatorId != KitGenerator ? null : (parameters, _, _) =>
            {
                seen.Add(new Dictionary<string, PCellValue>(parameters, StringComparer.Ordinal));
                return new PCellResult(
                    [new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }],
                    [new PCellPin("LA", 0, 50, new LayerKey(1, 0), 100, 180.0), new PCellPin("LB", 100, 50, new LayerKey(1, 0), 100, 0.0)]);
            };
    }

    // ── The fixture ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>C1 with TL2 replaced by X2, an instance of the library cell Line (beside the design in the workspace; its own
    /// defaults W = 1 mm, L = 5 mm) at W = w (a VAR, 1.1 mm) and L = 3 mm, its sub pin on ground. <paramref name="generator"/> null writes no LayoutGenerator.</summary>
    private SdlChecker.Prepared Design(string tag, string? generator, string terminals, bool ownLayout = false)
    {
        var p = SdlChecker.Prepare("C1-through-line", Path.Combine(_scratch, tag));
        string line = LibraryCell(p.Workspace, "Line", generator, terminals,
            [new CcellParameter { Name = "W", DefaultExpression = "1", Unit = "mm" }, new CcellParameter { Name = "L", DefaultExpression = "5", Unit = "mm" }],
            [("a", -200f, 0f), ("b", 200f, 0f), ("sub", 0f, 200f)]);
        if (ownLayout) OwnLayout(line, p.TechPath);

        var model = p.Model;
        model.Components.RemoveAll(c => c.InstanceName == "TL2");
        var x2 = new EditableComponent { InstanceName = "X2", Symbol = SymbolKind.Generic, CellRef = "../../Line", X = 600, Y = 0 };
        x2.Parameters.Add(new EditableParameter { Name = "W", Expression = "w" });
        x2.Parameters.Add(new EditableParameter { Name = "L", Expression = "3", Unit = "mm" });
        model.Components.Add(x2);
        model.Components.Add(new EditableComponent { InstanceName = "GND9", Symbol = SymbolKind.Ground, X = 600, Y = 200 });
        var vars = new EditableComponent { InstanceName = "VAR1", Symbol = SymbolKind.Var, X = 0, Y = 1000 };
        vars.Parameters.Add(new EditableParameter { Name = "w", Expression = "1.1", Unit = "mm" });
        model.Components.Add(vars);
        model.SchematicDirectory = p.SchematicDir;
        SchematicPersistence.SaveToFile(p.SchematicPath, model);
        return p;
    }

    /// <summary>No error, and no warning but the floating ground pour Update Layout draws under C1's lines, which no part's pin
    /// reaches (C1 alone reads the same).</summary>
    private static void Clean(LvsRunResult lvs)
        => Assert.DoesNotContain(lvs.Findings, f => f.Severity >= DiagnosticSeverity.Warning && f.Id != "lvs.net.floating-copper");

    private static EditableComponent X2(SdlChecker.Prepared p) => p.Model.Components.Single(c => c.InstanceName == "X2");

    /// <summary>Planned with the disk's cells, applied to the empty layout and saved, as Update Layout from Schematic does.</summary>
    private static (SchematicLayoutPlan Plan, LayoutView Layout) Run(SdlChecker.Prepared p)
    {
        var plan = SchematicLayoutPlanner.Plan(p.Model, p.Target, p.SchematicDir, p.Workspace, p.LayoutDir, p.Tech, p.TechPath,
                                               DiskCellResolver.Instance, targetLayoutPath: p.ClayPath);
        var layout = p.Applied(plan);
        LayoutPersistence.SaveToFile(p.ClayPath, layout);
        return (plan, layout);
    }

    private static PCellOrigin PCellOriginOf(SdlChecker.Prepared p, LayoutInstance inst)
    {
        var res = CellLayoutResolver.Resolve(inst.CellRef, p.LayoutDir);
        Assert.Equal(CellLayoutState.Resolved, res.State);
        return Assert.IsType<PCellOrigin>(res.View!.PCellOrigin);
    }

    /// <summary>A library cell: a symbol with <paramref name="pins"/> in port order, a schematic with a Pin per port (a resistor
    /// between the first two and a capacitor from the second to the third, when there are three), and a <c>.ccell</c> written as
    /// JSON so the terminal map is read exactly as a hand-written file is.</summary>
    private static string LibraryCell(string ws, string name, string? generator, string terminals, List<CcellParameter> parameters,
                                      (string Name, float X, float Y)[] pins, (string Name, string Expression)[]? vars = null)
    {
        string dir = CellFolder.CreateCellFolder(ws, name);
        var ccell = new CcellFile { NumPorts = pins.Length, Parameters = parameters, LayoutGenerator = generator };
        string json = CellPersistence.Serialize(ccell);
        json = json.TrimEnd().TrimEnd('}') + $",\n  \"Terminals\": {terminals}\n}}";
        File.WriteAllText(Path.Combine(dir, CellFolder.CcellFileName), json);

        string symbolDir = CellFolder.SubFolderPath(dir, ViewType.Symbol);
        Directory.CreateDirectory(symbolDir);
        string pinJson = string.Join(",\n", pins.Select((pin, k) =>
            FormattableString.Invariant($$"""{ "LocalX": {{pin.X}}, "LocalY": {{pin.Y}}, "PortIndex": {{k}}, "Name": "{{pin.Name}}" }""")));
        File.WriteAllText(Path.Combine(symbolDir, name + ".csym"), $$"""
            {
              "FormatVersion": 6, "GridSize": 100, "PortCount": {{pins.Length}},
              "Primitives": [ { "$type": "Rect", "ColorRole": "SymbolLine", "StrokeTier": "Normal", "Filled": false, "Cx": 0.0, "Cy": 0.0, "W": 400.0, "H": 200.0 } ],
              "Pins": [ {{pinJson}} ]
            }
            """);

        var sch = new SchematicEditModel();
        if (pins.Length == 3)
        {
            sch.Components.Add(PinAt(1, pins[0].Name, -100, -400));        // port at (0, -400)
            sch.Components.Add(PinAt(2, pins[1].Name, -1100, 200));        // port at (-1000, 200)
            sch.Components.Add(PinAt(3, pins[2].Name, -100, 800));         // port at (0, 800)
            sch.Components.Add(Part("R1", SymbolKind.Resistor, 0, 0, "R", "50", "Ohm"));      // (0,-200)-(0,200)
            sch.Components.Add(Part("C1", SymbolKind.Capacitor, 0, 400, "C", "1", "pF"));     // (0,200)-(0,600)
            sch.Wires.Add(Wire((0, -400), (0, -200)));
            sch.Wires.Add(Wire((-1000, 200), (0, 200)));
            sch.Wires.Add(Wire((0, 600), (0, 800)));
        }
        else
        {
            sch.Components.Add(PinAt(1, pins[0].Name, -100, -400));
            sch.Components.Add(PinAt(2, pins[1].Name, -100, 400));
            sch.Components.Add(Part("R1", SymbolKind.Resistor, 0, 0, "R", "1", "Ohm"));
            sch.Wires.Add(Wire((0, -400), (0, -200)));
            sch.Wires.Add(Wire((0, 200), (0, 400)));
        }
        if (vars is { Length: > 0 })
        {
            var block = new EditableComponent { InstanceName = "VAR1", Symbol = SymbolKind.Var, X = 2000, Y = 0 };
            foreach (var (n, e) in vars) block.Parameters.Add(new EditableParameter { Name = n, Expression = e });
            sch.Components.Add(block);
        }
        string schematicDir = CellFolder.SubFolderPath(dir, ViewType.Schematic);
        Directory.CreateDirectory(schematicDir);
        SchematicPersistence.SaveToFile(Path.Combine(schematicDir, name + ".csch"), sch);
        return dir;
    }

    /// <summary>The cell's own layout: a strip with pins 1 and 2, standing for the drawing its own EM run uses.</summary>
    private static void OwnLayout(string cellDir, string techPath)
    {
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var tech = TechPersistence.LoadFromFile(techPath);
        var layer = Conductors.Of(tech).First().DrawingLayers.First();
        var view = new LayoutView { TechRef = Path.GetRelativePath(layoutDir, techPath).Replace('\\', '/') };
        view.Shapes.Add(new RectShape { Layer = layer, X1 = 0, Y1 = -500_000, X2 = 3_000_000, Y2 = 500_000 });
        view.Pins.Add(new LayoutPin { Name = "1", X = 0, Y = 0, Layer = layer, WidthDbu = 1_000_000, OutwardDeg = 180 });
        view.Pins.Add(new LayoutPin { Name = "2", X = 3_000_000, Y = 0, Layer = layer, WidthDbu = 1_000_000, OutwardDeg = 0 });
        CellCreate.WriteLayoutView(cellDir, Path.GetFileName(cellDir), view);
    }

    private static EditableComponent PinAt(int num, string name, double x, double y)
    {
        var c = new EditableComponent { Symbol = SymbolKind.Pin, X = x, Y = y };
        c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        c.Parameters.Add(new EditableParameter { Name = "Name", Expression = name });
        return c;
    }

    private static EditableComponent Part(string name, SymbolKind kind, double x, double y, string param, string expr, string unit)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = kind, X = x, Y = y };
        c.Parameters.Add(new EditableParameter { Name = param, Expression = expr, Unit = unit });
        return c;
    }

    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }
}
