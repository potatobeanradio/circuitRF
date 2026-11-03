// ================================================================
//  HierarchyExampleAuthoring.cs — the Hierarchy example, BUILT with the functions the application's own commands call
//  rather than written as JSON. Skipped unless CRF_AUTHOR_HIERARCHY=1, because it WRITES examples/Hierarchy (the
//  workspace folder is replaced). It is kept so the workspace can be rebuilt after a format change, and so the steps the
//  README describes are the ones that produced the files.
//
//  The steps, in the order a user takes them:
//    New Workspace (the shipped default technology) ▸ New Cell for Pad, Board, Bench and Assembly ▸ draw each schematic
//    ▸ Update Layout from Schematic on Pad (places the three parts and their ground vias) ▸ arrange the parts, draw the
//    traces and the two pins ▸ Update Layout from Schematic again (the vias follow their pads) ▸ the same for Board,
//    whose layout places Pad's layout twice ▸ Place Cell Instance of Board's layout in Assembly's 3D view.
//
//  Update Layout from Schematic is SchematicToLayoutGenerator.Run — the call WorkspaceViewModel.RunLayoutUpdate makes —
//  with the resolver the headless paths use (DiskCellResolver), which the GUI's own resolver is gated equal to.
// ================================================================

using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class HierarchyExampleAuthoring(ITestOutputHelper output)
{
    /// <summary>DBU per mil at 1000 DBU/µm.</summary>
    internal const long Mil = 25_400;

    private static readonly LayerKey TopCopper = new(1, 0);
    private static readonly LayerKey Outline   = new(8, 0);

    public sealed class AuthorFactAttribute : FactAttribute
    {
        public AuthorFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CRF_AUTHOR_HIERARCHY") != "1")
                Skip = "re-builds examples/Hierarchy only with CRF_AUTHOR_HIERARCHY=1";
        }
    }

    [AuthorFact]
    public void BuildTheWorkspace()
    {
        string examples = Path.Combine(PalaceBackendTests.RepoRoot(), "examples");
        string ws = Path.Combine(examples, "Hierarchy");
        // README.md and examples.json are prose, written by hand; keep the README across a rebuild.
        string readme = Path.Combine(ws, "README.md");
        string? keptReadme = File.Exists(readme) ? File.ReadAllText(readme) : null;
        if (Directory.Exists(ws)) Directory.Delete(ws, true);

        // New Workspace, on the technology the dialog opens on.
        var created = WorkspaceCreate.Create(examples, "Hierarchy", ShippedTechnologies.DefaultId);
        if (keptReadme is not null) File.WriteAllText(readme, keptReadme);
        // The connector the 3D view places is copied from the 3D Connector example, and its alloy is a material of that
        // example's technology. Added FIRST: a generated part is keyed on its technology's content, so a technology
        // edited after the layouts are generated leaves every part reading as out of date.
        var tech = AddConnectorAlloy(examples, created.TechPath!);

        BuildPad(ws, tech);
        BuildBoard(ws, tech);
        BuildBench(ws);
        BuildBoard3D(ws, tech);
        BuildLaunch(ws, examples, tech);
        BuildAssembly(ws, tech);
    }

    // ── Pad: a pi attenuator whose value is a cell parameter ────────────────────────────────────

    /// <summary>The resistors of a matched pi pad of <c>dB</c> attenuation in 50 Ω, written as the formulas rather than
    /// numbers so that one cell serves every instance — a VAR block inside the cell, which makes them cell-local
    /// variables evaluated in the instance's own scope.</summary>
    internal static readonly (string Name, string Expr)[] PadVars =
    [
        ("Z0",  "50"),
        ("K",   "10^(dB/20)        ; voltage ratio of the attenuation"),
        ("Rsh", "Z0*(K+1)/(K-1)    ; shunt arms"),
        ("Rse", "Z0*(K^2-1)/(2*K)  ; series arm"),
    ];

    private void BuildPad(string ws, Technology tech)
    {
        var m = new SchematicEditModel();
        m.Components.Add(Pin("P1", 1, "IN",  -300, 0, SymbolRotation.R0));
        m.Components.Add(Pin("P2", 2, "OUT", 1300, 0, SymbolRotation.R180));
        m.Components.Add(Part(SymbolKind.Resistor, "R1", 0,    400, SymbolRotation.R0,   ("R", "Rsh")));
        m.Components.Add(Part(SymbolKind.Resistor, "R2", 500,  0,   SymbolRotation.R270, ("R", "Rse")));
        m.Components.Add(Part(SymbolKind.Resistor, "R3", 1000, 400, SymbolRotation.R0,   ("R", "Rsh")));
        var vars = new EditableComponent { InstanceName = "VAR1", Symbol = SymbolKind.Var, X = -300, Y = -800 };
        foreach (var (n, e) in PadVars) vars.Parameters.Add(new EditableParameter { Name = n, Expression = e });
        m.Components.Add(vars);
        m.Components.Add(Ground("GND1", 0, 600));
        m.Components.Add(Ground("GND2", 1000, 600));
        Wire(m, (-200, 0), (300, 0));
        Wire(m, (700, 0), (1200, 0));
        Wire(m, (0, 0), (0, 200));
        Wire(m, (1000, 0), (1000, 200));
        foreach (var c in m.Components.Where(c => c.Symbol == SymbolKind.Resistor))
            c.Parameters.Add(new EditableParameter { Name = "Footprint", Expression = "smt:0402", ShowOnSchematic = false });

        string cellDir = NewCell(ws, "Pad", m, tech, ports: 2,
            parameters: [new CcellParameter { Name = "dB", DefaultExpression = "3" }],
            // Its ground vias and pour meet Board's ground plane, which no pin declares: LVS reads it flattened.
            flattenForLvs: true);

        // Update Layout from Schematic: R1, R2, R3 placed on a grid, a ground via at R1's and R3's grounded pads.
        string clay = LayoutPath(cellDir);
        var view = LayoutPersistence.LoadFromFile(clay);
        UpdateLayout(m, view, cellDir, ws, tech);

        // The designer's half: arrange the parts along a 200 mil run, draw the traces, place the pins.
        // R2 centred on the run; R1 and R3 standing on the line with their grounded ends below it.
        Place(view, cellDir, tech, "R2", LayoutRotation.R0,   pin: "1", at: (0, 0));
        long r2Span = PinAt(view, cellDir, tech, "R2", "2").X - PinAt(view, cellDir, tech, "R2", "1").X;
        Place(view, cellDir, tech, "R2", LayoutRotation.R0,   pin: "1", at: (100 * Mil - r2Span / 2, 0));
        Place(view, cellDir, tech, "R1", LayoutRotation.R270, pin: "1", at: (40 * Mil, 0));
        Place(view, cellDir, tech, "R3", LayoutRotation.R270, pin: "1", at: (160 * Mil, 0));
        // A designator's automatic place is above its part's body, which R270 turns toward +x: right of R3, clear of
        // everything, but right of R1 is on R2's pad. R1's is moved to the left of its footprint instead — the
        // offset dragging it there stores, the automatic one mirrored through the 0402 body's centre line.
        var r1 = view.Instances.Single(i => i.SchematicId == "R1");
        var land = CellLayoutResolver.Resolve(r1.CellRef, Path.Combine(cellDir, "layout")).View;
        var (adx, ady) = FootprintLabel.AutoOffset(land, LandPatternLayers.Resolve(tech, PCellLayerSelection.Default, []),
                                                   FootprintLabel.DefaultHeightDbu(view.DbuPerMicron));
        (r1.LabelDx, r1.LabelDy) = (adx, -ady);
        var r2a = PinAt(view, cellDir, tech, "R2", "1");
        var r2b = PinAt(view, cellDir, tech, "R2", "2");
        view.Shapes.Add(Trace(20 * Mil, (0, 0), r2a));
        view.Shapes.Add(Trace(20 * Mil, r2b, (200 * Mil, 0)));
        view.Pins.Add(new LayoutPin { Name = "IN",  X = 0,         Y = 0, WidthDbu = 20 * Mil, OutwardDeg = 180, Layer = TopCopper });
        view.Pins.Add(new LayoutPin { Name = "OUT", X = 200 * Mil, Y = 0, WidthDbu = 20 * Mil, OutwardDeg = 0,   Layer = TopCopper });

        // Update Layout again: the parts stay where they were put, and the ground vias move to their pads.
        UpdateLayout(m, view, cellDir, ws, tech);
        AssertNothingChanged(m, view, cellDir, ws, tech);
        LayoutPersistence.SaveToFile(clay, view);
    }

    // ── Board: two Pads between three 50 Ω lines ────────────────────────────────────────────────

    /// <summary>50 Ω on the default technology's 20 mil laminate (εr 3.66), by Hammerstad and Jensen.</summary>
    internal const double LineW = 44, LineL = 200;

    private void BuildBoard(string ws, Technology tech)
    {
        var m = new SchematicEditModel { SchematicDirectory = Path.Combine(ws, "Board", "schematic") };
        m.Components.Add(Pin("P1", 1, "IN",  -1500, 0, SymbolRotation.R0));
        m.Components.Add(Line("TL1", -1000));
        m.Components.Add(Cell(m, "X1", "../../Pad", -400, 0, ("dB", "3")));
        m.Components.Add(Line("TL2", 200));
        m.Components.Add(Cell(m, "X2", "../../Pad", 800, 0, ("dB", "6")));
        m.Components.Add(Line("TL3", 1400));
        m.Components.Add(Pin("P2", 2, "OUT", 1900, 0, SymbolRotation.R180));
        Wire(m, (-1400, 0), (-1200, 0));
        Wire(m, (-800, 0), (-600, 0));
        Wire(m, (-200, 0), (0, 0));
        Wire(m, (400, 0), (600, 0));
        Wire(m, (1000, 0), (1200, 0));
        Wire(m, (1600, 0), (1800, 0));

        string cellDir = NewCell(ws, "Board", m, tech, ports: 2);

        // Update Layout from Schematic: three MLIN PCells, and Pad's own layout placed twice.
        string clay = LayoutPath(cellDir);
        var view = LayoutPersistence.LoadFromFile(clay);
        UpdateLayout(m, view, cellDir, ws, tech);

        // Arranged end to end, each part's input on the previous one's output, after a short launch at each board
        // edge: the board's pins sit on drawn copper, not on a line's own artwork, which is a device's body.
        long launch = 20 * Mil, w = (long)(LineW * Mil);
        long x = launch;
        foreach (var (id, inPin, outPin) in new[] { ("TL1", "1", "2"), ("X1", "IN", "OUT"), ("TL2", "1", "2"),
                                                     ("X2", "IN", "OUT"), ("TL3", "1", "2") })
        {
            Place(view, cellDir, tech, id, LayoutRotation.R0, pin: inPin, at: (x, 0));
            x = PinAt(view, cellDir, tech, id, outPin).X;
        }
        view.Shapes.Add(Trace(w, (0, 0), (launch, 0)));
        view.Shapes.Add(Trace(w, (x, 0), (x + launch, 0)));
        x += launch;
        output.WriteLine($"Board run: {x / (double)Mil} mil");
        view.Pins.Add(new LayoutPin { Name = "IN",  X = launch / 2,     Y = 0, WidthDbu = w, OutwardDeg = 180, Layer = TopCopper });
        view.Pins.Add(new LayoutPin { Name = "OUT", X = x - launch / 2, Y = 0, WidthDbu = w, OutwardDeg = 0,   Layer = TopCopper });
        view.Shapes.Add(new RectShape { X1 = 0, Y1 = -200 * Mil, X2 = x, Y2 = 200 * Mil, Layer = Outline });

        // A module is not a part: X1 and X2 put nothing on the silkscreen (their resistors do), so their designators
        // are switched off, per instance, as the layout editor's Show Designator does.
        foreach (var inst in view.Instances.Where(i => i.SchematicId is "X1" or "X2")) inst.ShowRefDes = false;

        UpdateLayout(m, view, cellDir, ws, tech);
        AssertNothingChanged(m, view, cellDir, ws, tech);
        LayoutPersistence.SaveToFile(clay, view);
    }

    // ── Bench: the test bench that simulates Board ─────────────────────────────────────────────

    private static void BuildBench(string ws)
    {
        var m = new SchematicEditModel();
        m.SchematicDirectory = Path.Combine(ws, "Bench", "schematic");
        m.Components.Add(Cell(m, "X1", "../../Board", 0, 0));
        m.Components.Add(Part(SymbolKind.TermG, "P1", -600, 400, SymbolRotation.R0, ("Num", "1")));
        m.Components.Add(Part(SymbolKind.TermG, "P2", 600, 400, SymbolRotation.R0, ("Num", "2")));
        Wire(m, (-600, 200), (-600, 0), (-200, 0));
        Wire(m, (600, 200), (600, 0), (200, 0));
        m.Analyses.Add(new SParameterAnalysis("SP1",
            new FrequencySpec("0.1", "6", 60, SweepKind.Linear, "GHz", "GHz")));
        var meas = new EditableComponent { InstanceName = "Meas1", Symbol = SymbolKind.Meas, X = -200, Y = 1400 };
        meas.Parameters.Add(new EditableParameter { Name = "S21_dB", Expression = "dB(SP1.S(2,1))   ; -9 dB: the 3 dB pad and the 6 dB pad" });
        meas.Parameters.Add(new EditableParameter { Name = "S11_dB", Expression = "dB(SP1.S(1,1))" });
        m.Components.Add(meas);

        var made = CellCreate.Create(ws, "Bench", CellViews.Schematic, m);
        var ccellPath = Path.Combine(made.CellDir, ".ccell");
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.IsTestBench = true;
        CellPersistence.SaveToFile(ccellPath, ccell);
    }

    // ── The 3D hierarchy: Board's own 3D view, a Launch cell, and the Assembly that places both ──────

    /// <summary>The board's outline, from its layout: 1040 × 400 mil.</summary>
    private const long BoardRun = 1040 * Mil, BoardHalf = 200 * Mil, CarrierT = 60 * Mil;

    /// <summary>The 3D Connector example's own connector — copied, not redrawn: <c>examples/3D Connector/Launch</c>.</summary>
    private const string ConnectorExample = "3D Connector";

    /// <summary>Height of the ground plane's top above z = 0, the bottom of the stackup (where a layout placed at z = 0
    /// puts it): the stackup's layers from the ground reference down. The 3D Connector's launch is drawn with its z = 0 on
    /// that face, so this is where it is placed.</summary>
    private static long GroundTop(Technology tech)
    {
        var layers = tech.Stackup!.Layers.Where(l => l.Kind != StackupKind.Via).ToList();
        int ground = layers.FindIndex(l => l.IsGroundReference);
        return layers.Skip(ground).Sum(l => l.ThicknessDbu);
    }

    /// <summary>Board ▸ New 3D View: the board as built — its OWN layout placed as L1 (a cell's 3D view may place its
    /// layout; the views differ) on an aluminium carrier drawn here.</summary>
    private static void BuildBoard3D(string ws, Technology tech)
    {
        string cellDir = Path.Combine(ws, "Board");
        var doc = CellCreate.NewThreeDView(cellDir, tech);
        doc.Instances.Add(new C3dInstance
        {
            Name = "L1", CellRef = "../../Board", View = C3dInstanceView.Layout,
            Placement = new C3dPlacement { Origin = new C3dPoint3(0, 0, 0) },
        });
        doc.Objects.Add(new C3dBox
        {
            Name = "carrier", Material = "Aluminium",
            Min = new C3dPoint3(0, -BoardHalf, -CarrierT), Size = new C3dPoint3(BoardRun, 2 * BoardHalf, CarrierT),
        });
        CellCreate.WriteThreeDView(cellDir, "Board", doc);
    }

    /// <summary>
    /// The coaxial launch, COPIED from the 3D Connector example rather than drawn again: the housing with its bore
    /// subtracted and kept as PTFE fill, the flange imported from STEP and united with it, and the centre pin with its
    /// filleted tip — insulated from the housing by the PTFE. That example's board instance, ports and setups stay
    /// behind; this cell is the connector alone (its connector alloy: <see cref="AddConnectorAlloy"/>). Its frame: the
    /// flange's face on x = 0, the ground plane's top on z = 0.
    /// </summary>
    private static void BuildLaunch(string ws, string examples, Technology tech)
    {
        string source = Path.Combine(examples, ConnectorExample);
        var launch = C3dPersistence.LoadFromFile(Path.Combine(source, "Launch", "3d", "Launch.c3d"));
        launch.Instances.Clear();
        launch.Ports.Clear();
        launch.Setups.Clear();
        var made = CellCreate.Create(ws, "Launch", CellViews.ThreeD, tech: tech);
        C3dPersistence.SaveToFile(made.ThreeDPath!, launch);
        File.Copy(Path.Combine(source, "Launch", "3d", "flange.step"), Path.Combine(Path.GetDirectoryName(made.ThreeDPath!)!, "flange.step"));
    }

    /// <summary>The 3D Connector technology's connector alloy, added to this workspace's technology.</summary>
    private static Technology AddConnectorAlloy(string examples, string techPath)
    {
        var tech = TechPersistence.LoadFromFile(techPath);
        tech.Materials.Add(TechPersistence.LoadFromFile(Path.Combine(examples, ConnectorExample, "tech", "board-and-connector.ctech"))
                                          .Materials.Single(m => m.Name == "Connector alloy"));
        TechPersistence.SaveToFile(techPath, tech);
        return TechPersistence.LoadFromFile(techPath);
    }

    /// <summary>The top of the 3D hierarchy: Board's 3D VIEW placed as B1 (push into it and it is edited here, in
    /// context), and Launch placed twice — J2 turned 180° to face the other edge.</summary>
    private static void BuildAssembly(string ws, Technology tech)
    {
        long groundTop = GroundTop(tech);
        var made = CellCreate.Create(ws, "Assembly", CellViews.ThreeD, tech: tech);
        var doc = C3dPersistence.LoadFromFile(made.ThreeDPath!);
        doc.Instances.Add(new C3dInstance
        {
            Name = "B1", CellRef = "../../Board", View = C3dInstanceView.ThreeD,
            Placement = new C3dPlacement { Origin = new C3dPoint3(0, 0, 0) },
        });
        doc.Instances.Add(new C3dInstance
        {
            Name = "J1", CellRef = "../../Launch", View = C3dInstanceView.ThreeD,
            Placement = new C3dPlacement { Origin = new C3dPoint3(0, 0, groundTop) },
        });
        doc.Instances.Add(new C3dInstance
        {
            Name = "J2", CellRef = "../../Launch", View = C3dInstanceView.ThreeD,
            Placement = new C3dPlacement
            {
                Origin = new C3dPoint3(BoardRun, 0, groundTop), Rotate = [new C3dRotation { Axis = C3dAxis.Z, Deg = 180 }],
            },
        });
        C3dPersistence.SaveToFile(made.ThreeDPath!, doc);
    }

    // ── The steps, as the commands take them ──────────────────────────────────────────────────

    private string NewCell(string ws, string name, SchematicEditModel m, Technology tech, int ports,
                           List<CcellParameter>? parameters = null, bool flattenForLvs = false)
    {
        var made = CellCreate.Create(ws, name, CellViews.Schematic | CellViews.Symbol | CellViews.Layout, m, tech);
        // The symbol the schematic editor generates for a cell that has none.
        SymbolPersistence.SaveToFile(made.SymbolPath!, AutoSymbolGenerator.Generate(name, ports));

        var ccellPath = Path.Combine(made.CellDir, ".ccell");
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.NumPorts = ports;
        ccell.Parameters = parameters ?? [];
        ccell.FlattenForLvs = flattenForLvs;
        // Which layout pin is which port, stated rather than left to a positional guess (LVS reads it).
        ccell.Terminals = [new CcellTerminal { Port = 1, Name = "IN", LayoutPin = ["IN"] },
                           new CcellTerminal { Port = 2, Name = "OUT", LayoutPin = ["OUT"] }];
        CellPersistence.SaveToFile(ccellPath, ccell);
        return made.CellDir;
    }

    private static string LayoutPath(string cellDir) =>
        Path.Combine(cellDir, "layout", Path.GetFileName(cellDir) + ".clay");

    private SchematicToLayoutGenerator.GenerationResult Run(SchematicEditModel m, LayoutView view, string cellDir,
                                                            string ws, Technology tech)
    {
        string schematicDir = Path.Combine(cellDir, "schematic");
        m.SchematicDirectory = schematicDir;
        return SchematicToLayoutGenerator.Run(m, view, schematicDir, ws, Path.Combine(cellDir, "layout"),
                                              tech, Path.Combine(ws, "tech", ShippedTechnologies.DefaultId + ".ctech"),
                                              DiskCellResolver.Instance);
    }

    private void UpdateLayout(SchematicEditModel m, LayoutView view, string cellDir, string ws, Technology tech)
    {
        var r = Run(m, view, cellDir, ws, tech);
        foreach (var w in r.NoLayoutWarnings) output.WriteLine($"{Path.GetFileName(cellDir)} no-layout: {w}");
        foreach (var l in r.Lines) output.WriteLine($"{Path.GetFileName(cellDir)} {l.InstanceName}: {l.Text}");
        foreach (var l in SchematicLayoutPlanner.GroundArtworkReport(r.GroundArtwork, view, LayoutUnit.Mil))
            output.WriteLine($"{Path.GetFileName(cellDir)} ground: {l.Text}");
        Assert.Empty(r.NoLayoutWarnings);
        r.Command?.Execute();
    }

    private void AssertNothingChanged(SchematicEditModel m, LayoutView view, string cellDir, string ws, Technology tech)
    {
        var r = Run(m, view, cellDir, ws, tech);
        Assert.True(r.Command is null, $"{Path.GetFileName(cellDir)}: a third Update Layout still changed something: " +
                                       string.Join(" | ", r.Lines.Select(l => $"{l.InstanceName}: {l.Text}")));
    }

    /// <summary>Moves the instance linked to <paramref name="id"/> so its <paramref name="pin"/> lands on
    /// <paramref name="at"/> — what dragging a part by its pin onto a point does.</summary>
    private static void Place(LayoutView view, string cellDir, Technology tech, string id, LayoutRotation rot,
                              string pin, (long X, long Y) at)
    {
        var inst = view.Instances.Single(i => i.SchematicId == id);
        inst.Rot = rot;
        inst.X = 0; inst.Y = 0;
        var p = PinAt(view, cellDir, tech, id, pin);
        inst.X = at.X - p.X;
        inst.Y = at.Y - p.Y;
    }

    private static (long X, long Y) PinAt(LayoutView view, string cellDir, Technology tech, string id, string pin)
    {
        var inst = view.Instances.Single(i => i.SchematicId == id);
        var cell = CellLayoutResolver.Resolve(inst.CellRef, Path.Combine(cellDir, "layout"));
        Assert.Equal(CellLayoutState.Resolved, cell.State);
        var pins = CellPins.Resolve(cell.View!, tech);
        var local = pins.FirstOrDefault(p => p.Name == pin)
                    ?? throw new InvalidOperationException(
                        $"{id} has no pin '{pin}' — it has {string.Join(", ", pins.Select(p => p.Name))}");
        return LayoutInstanceTransform.TransformPoint(local.X, local.Y, inst, 0, 0);
    }

    private static PathShape Trace(long width, (long X, long Y) a, (long X, long Y) b) => new()
    {
        Xy = [a.X, a.Y, b.X, b.Y], Width = width, Layer = TopCopper,
    };

    // ── Schematic parts ───────────────────────────────────────────────────────────────────────

    private static EditableComponent Part(SymbolKind kind, string name, double x, double y, SymbolRotation rot,
                                          params (string Name, string Expr)[] overrides)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = kind, X = x, Y = y, Rotation = rot };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 0))
        {
            var o = overrides.FirstOrDefault(o => o.Name == dp.Name);
            c.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = o.Name is null ? dp.Expression : o.Expr,
                Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        }
        return c;
    }

    private static EditableComponent Pin(string name, int num, string label, double x, double y, SymbolRotation rot) =>
        Part(SymbolKind.Pin, name, x, y, rot, ("Num", num.ToString()), ("Name", label));

    private static EditableComponent Ground(string name, double x, double y) =>
        new() { InstanceName = name, Symbol = SymbolKind.Ground, X = x, Y = y, ShowTypeLabel = false, ShowInstanceName = false };

    private static EditableComponent Line(string name, double x)
    {
        var c = Part(SymbolKind.Mlin, name, x, 0, SymbolRotation.R0);
        foreach (var p in c.Parameters)
        {
            if (p.Name == "W") { p.Expression = LineW.ToString(); p.Unit = "mil"; }
            if (p.Name == "L") { p.Expression = LineL.ToString(); p.Unit = "mil"; }
        }
        return c;
    }

    /// <summary>Place Cell: the instance records the interface it was placed against, and its parameters are seeded
    /// from the cell's <c>.ccell</c> — as <c>SchematicViewModel</c>'s placement does — then overridden.</summary>
    private static EditableComponent Cell(SchematicEditModel m, string name, string cellRef, double x, double y,
                                          params (string Name, string Expr)[] overrides)
    {
        var c = new EditableComponent
        {
            InstanceName = name, Symbol = SymbolKind.Generic, X = x, Y = y, CellRef = cellRef,
            CellInterfaceHash = PlacedCellRef.HashFor(cellRef, m.SchematicDirectory),
        };
        var ccell = CellPersistence.LoadFromFile(Path.Combine(m.SchematicDirectory!, cellRef, ".ccell"));
        foreach (var cp in ccell.Parameters)
            c.Parameters.Add(new EditableParameter
            {
                Name = cp.Name, Unit = cp.Unit, Dimension = cp.Dimension, ShowOnSchematic = cp.ShowOnSchematic,
                Expression = overrides.FirstOrDefault(o => o.Name == cp.Name).Expr ?? cp.DefaultExpression,
            });
        return c;
    }

    private static void Wire(SchematicEditModel m, params (double X, double Y)[] points)
    {
        var w = new EditableWire();
        w.Points.AddRange(points);
        m.Wires.Add(w);
    }
}
