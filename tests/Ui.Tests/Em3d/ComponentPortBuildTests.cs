using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// brief-emb-1 §2 — component ports built on a part's own pads (R-emb1-3), verified before meshing (R-emb1-4), gap ports per
// signal pad against the part's ground pad (R-emb1-5) and a connector's shell bridged (R-emb1-6). One small board on the
// shipped two-layer RO4350B stack (Top Copper over 508 µm to a Bottom Copper plane), three parts placed on it:
//   C1 — two 400 × 500 µm pads 600 µm apart; a 300 µm trace with a ROUND cap reaches 150 µm into the gap from pad 1, and a
//        280 µm round pour (a zone's arc) reaches 80 µm into it from pad 2. Neither face is straight.
//   U1 — three pads in a row, IN / GND / OUT, 300 µm gaps.
//   J1 — a launch: signal pad 1, a GND pad on top and a SHELL pad on the bottom plane right under it.
public sealed class ComponentPortBuildTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private const int Dbu = 1000;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bottom = new(2, 0);
    private readonly string _root = Directory.CreateTempSubdirectory("emb1-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static long Um(double v) => (long)Math.Round(v * Dbu);
    private const double M = 1e-6;   // µm in metres

    /// <summary>A slanted copper edge crossing the face a fraction of a DBU from the sheet's corner would leave a sliver of the sheet inside
    /// the metal, which no mesh element has for a face. The copper takes the corner exactly: it is a vertex of the outline, with no
    /// other vertex beside it.</summary>
    [Fact]
    public void ASlantedEdgeCrossingTheFaceBesideTheSheetsCorner_TakesTheCorner()
    {
        // A 45° edge reaching x = 4900 µm at the bottom of C1's shared width, y = 4750 µm, and falling away above it.
        var slant = new PolygonShape { Layer = Top, Xy = [Um(4000), Um(4650), Um(5000), Um(4650), Um(4700), Um(4950), Um(4000), Um(4950)] };
        var (g, _) = Assemble(["C1"], [slant]);
        Assert.True(g.Ok, g.Refusal);
        var build = g.ComponentPorts!;
        var c1 = Assert.Single(build.Ports);
        Assert.Equal("verified", c1.Verdict);
        var port = c1.Port!;
        var corner = new Point2(port.Rect.Min.U * 1e-9, port.Rect.Min.V * 1e-9);
        Assert.InRange(corner.X, 4899 * M, 4900 * M);

        var low = c1.Landings.Single(l => l.Box.X0 < 5000 * M);
        var outline = ((Em3dExtrudedPolygon)build.Elaboration.Solids.Single(s => s.Name == low.Conductor).Primitive).Outline;
        var beside = outline.Where(q => Math.Abs(q.X - corner.X) < 1e-7 && Math.Abs(q.Y - corner.Y) < 1e-7).ToList();
        var only = Assert.Single(beside);
        Assert.Equal(corner.X, only.X, 12);
        Assert.Equal(corner.Y, only.Y, 12);
    }

    /// <summary>The gate: the sheet's ends lie on the landing faces within 1 DBU, no sample of the sheet is copper, the conductor
    /// count is unchanged, and each landing overlaps its pad across the port's whole width.</summary>
    [Fact]
    public void AGapBoundedByACapAndAnArc_GetsLandingsAndAVerifiedSheet()
    {
        var (g, drawnConductors) = Assemble(["C1"]);
        Assert.True(g.Ok, g.Refusal);
        var built = g.ComponentPorts!;
        var c1 = Assert.Single(built.Ports);
        Assert.Equal("verified", c1.Verdict);
        Assert.True(c1.AlongX);

        // Both faces needed a landing: the cap's tip at 4850 µm and the arc's at ~5220 µm.
        Assert.Equal(2, c1.Landings.Count);
        var port = c1.Port!;
        double x0 = port.Rect.Min.U * 1e-9, x1 = (port.Rect.Min.U + port.Rect.Size.U) * 1e-9;
        double y0 = port.Rect.Min.V * 1e-9, y1 = (port.Rect.Min.V + port.Rect.Size.V) * 1e-9;
        var low = c1.Landings.Single(l => l.Box.X0 < 5000 * M);
        var high = c1.Landings.Single(l => l.Box.X0 > 5000 * M);
        Assert.InRange(Math.Abs(low.Box.X1 - x0), 0, 1e-9);
        Assert.InRange(Math.Abs(high.Box.X0 - x1), 0, 1e-9);
        Assert.InRange(low.Box.X1, 4849 * M, 4850.1 * M);   // the cap's tessellated tip
        Assert.InRange(high.Box.X0, 5215 * M, 5225 * M);
        // Across the port's whole width, reaching back past the pad's own face (4700 / 5300 µm) into solid copper.
        Assert.All(c1.Landings, l => { Assert.Equal(y0, l.Box.Y0, 12); Assert.Equal(y1, l.Box.Y1, 12); });
        Assert.True(low.Box.X0 < 4700 * M && high.Box.X1 > 5300 * M);
        Assert.Equal(500 * M, y1 - y0, 9);

        // No conductor added or lost, and no copper inside the sheet: sampled here on its own 2 µm grid.
        var problem = g.Problem!;
        Assert.Equal(drawnConductors, problem.Solids.Count(s => s.Role == Em3dRole.Conductor));
        double z = port.Offset * 1e-9;
        for (double x = x0 + 2e-9; x < x1; x += 2e-6)
            for (double y = y0 + 2e-9; y < y1; y += 2e-6)
                Assert.DoesNotContain(problem.Solids, s => s.Role == Em3dRole.Conductor && C3dThermal.Inside(s.Primitive, new Point3(x, y, z), -0.5e-9));

        // The run's port is that sheet, between the two pads' conductors, both ends stated.
        var em = Assert.Single(problem.Ports);
        Assert.Equal(port.Positive, em.PositiveObject);
        Assert.Equal(port.Negative, em.NegativeObject);
        Assert.NotEqual(em.PositiveObject, em.NegativeObject);
        Assert.Equal(1, Math.Abs(em.Direction.X), 9);
    }

    /// <summary>A landing that would touch copper of another conductor is refused, naming the part.</summary>
    [Fact]
    public void ALandingThatWouldBridgeTwoConductors_IsRefusedByRefdes()
    {
        var (g, _) = Assemble(["C1"], [new RectShape { Layer = Top, X1 = Um(4750), Y1 = Um(5250), X2 = Um(4900), Y2 = Um(5400) }]);
        Assert.False(g.Ok);
        Assert.StartsWith("C1:", g.Refusal, StringComparison.Ordinal);
        Assert.Contains("bridging two conductors", g.Refusal, StringComparison.Ordinal);
    }

    /// <summary>A scan line through a vertex the ring only touches (both its edges on one side) reads no copper boundary there:
    /// inside copper it does not split the run in two, and an ordinary crossing is unchanged. The split, or a zero-length run
    /// in front of the face, was what refused a real board's capacitor whose ground pad sits in a pour.</summary>
    [Fact]
    public void AScanLineThatOnlyTouchesAVertex_ReadsNoBoundaryThere()
    {
        // A 10 × 10 plate with a diamond hole whose lowest vertex (5, 5) the line y = 5 only touches.
        var plate = new Em3dExtrudedPolygon([new(0, 0), new(10, 0), new(10, 10), new(0, 10)],
                                            [[new(5, 5), new(6, 7), new(5, 9), new(4, 7)]], 0, 1);
        Assert.Equal([(0.0, 10.0)], C3dComponentPorts.Intervals(plate, 5, alongX: true));
        Assert.Equal([(0.0, 4.0), (6.0, 10.0)], C3dComponentPorts.Intervals(plate, 7, alongX: true));
    }

    /// <summary>Copper of pad 2's conductor reaching most of the way across the gap, as a ground pour round the other pad
    /// does, puts the gap where the two conductors leave off rather than refusing it as copper across the pads' middle.</summary>
    [Fact]
    public void ACopperFillReachingPastThePadsMiddle_PutsTheGapWhereTheCopperLeavesOff()
    {
        var (g, _) = Assemble(["C1"], [new RectShape { Layer = Top, X1 = Um(4900), Y1 = Um(4700), X2 = Um(5400), Y2 = Um(5300) }]);
        Assert.True(g.Ok, g.Refusal);
        var c1 = Assert.Single(g.ComponentPorts!.Ports);
        Assert.Equal("verified", c1.Verdict);
        double x0 = c1.Port!.Rect.Min.U * 1e-9, x1 = (c1.Port.Rect.Min.U + c1.Port.Rect.Size.U) * 1e-9;
        Assert.InRange(x0, 4849 * M, 4850.1 * M);   // the trace cap's tip
        Assert.Equal(4900 * M, x1, 9);              // the fill's edge
    }

    /// <summary>A part whose port cannot be built is left out with a warning, and the others are built and numbered without a
    /// gap: one part's copper never costs every other part its port.</summary>
    [Fact]
    public void APartThatCannotBeBuilt_IsLeftOut_AndTheOthersAreBuilt()
    {
        var (g, _) = Assemble(["C1", "U1"], [new RectShape { Layer = Top, X1 = Um(4750), Y1 = Um(5250), X2 = Um(4900), Y2 = Um(5400) }]);
        Assert.True(g.Ok, g.Refusal);
        var build = g.ComponentPorts!;
        Assert.Contains("bridging two conductors", Assert.Single(build.Ports, p => p.Refdes == "C1").Refusal);
        Assert.Equal([1, 2], build.Ports.Where(p => p.Ok).Select(p => p.Port!.Number));
        Assert.Equal(["U1.IN", "U1.OUT"], build.Ports.Where(p => p.Ok).Select(p => p.Port!.Name));
        Assert.Equal([1, 2], g.Problem!.Ports.Select(p => p.Number));
        Assert.Contains(g.Warnings, w => w.Contains("C1 is left out of this run", StringComparison.Ordinal));
    }

    /// <summary>The Touchstone header says which numbered port is which component port: the problem's ports carry the names
    /// they have in the 3D view, and every 3D writer's header starts with one <c>Port[N] = name</c> line each.</summary>
    [Fact]
    public void TheTouchstoneHeader_NamesEachComponentPortByItsNumber()
    {
        var (g, _) = Assemble(["U1"]);
        Assert.True(g.Ok, g.Refusal);
        Assert.Equal(["Port[1] = U1.IN", "Port[2] = U1.OUT"], CircuitRF.Design.Em3d.Em3dPortMap.NameLines(g.Problem!.Ports));
        Assert.Equal(new string?[] { "U1.IN", "U1.OUT" },
                     RfCore.TouchstonePortNames.Parse(CircuitRF.Design.Em3d.Em3dPortMap.NameLines(g.Problem.Ports), 2)!);
    }

    /// <summary>The verification's sampler answers every sample exactly as C3dThermal.Inside does — inside, outside, on an edge,
    /// in a hole, either sign of tolerance — so making it fast moved no verdict.</summary>
    [Fact]
    public void TheSheetSampler_AnswersAsInsideDoes()
    {
        var plate = new Em3dExtrudedPolygon(
            [new(0, 0), new(10e-6, 0), new(10e-6, 4e-6), new(6e-6, 4e-6), new(6e-6, 10e-6), new(0, 10e-6)],
            [[new(1e-6, 1e-6), new(3e-6, 1e-6), new(2e-6, 3e-6)]], 0, 1e-6);
        double[] vs = [.. Enumerable.Range(0, 41).Select(j => -0.5e-6 + 11e-6 * j / 40)];
        foreach (double tol in new[] { -0.5e-9, 0.5e-9, -0.3e-6 })
        {
            var sampler = new C3dComponentPorts.SheetSampler(plate, vs, -0.5e-6, 10.5e-6, tol);
            for (int i = 0; i <= 44; i++)
                for (int j = 0; j < vs.Length; j++)
                {
                    var q = new Point3(-0.5e-6 + 11e-6 * i / 44, vs[j], 0.5e-6);
                    Assert.Equal(C3dThermal.Inside(plate, q, tol), sampler.Inside(q, j));
                }
        }
    }

    /// <summary>The 3D editor builds the active setup's component ports with its scene: drawn as port sheets, and listed in the
    /// Object Tree under Component ports — ticking "Every two-terminal part" shows them, with no run.</summary>
    [Fact]
    public void TheEditor_DrawsAndListsTheSetupsComponentPorts()
    {
        var (_, cws, c3d) = Written(["C1"]);
        var (vm, settle) = OpenEditor(c3d, cws);
        try
        {
            var (part, result) = Assert.Single(vm.ComponentPortResults);
            Assert.Equal("C1", part.Refdes);
            var port = Assert.IsType<Em3dPort>(result?.Resolved);
            Assert.NotNull(vm.SceneObject(port.Name));
            var group = Assert.Single(vm.Tree.OfType<CircuitRF.Ui.ThreeD.C3dTreeGroup>(), g => g.Role == CircuitRF.Ui.ThreeD.C3dTreeGroupRole.ComponentPorts);
            Assert.Equal("C1", Assert.Single(group.Items).Label);
        }
        finally { vm.Dispose(); }
    }

    /// <summary>The 3D view draws the copper the run solves: each conductor a landing extends is drawn reaching the landing's face,
    /// both corners of it. Drawn from the elaboration before its landings, the sheet met a round cap and a zone's arc at their
    /// tips while the solver met a straight face.</summary>
    [Fact]
    public void TheEditor_DrawsTheLandingsTheRunSolves()
    {
        var (_, cws, c3d) = Written(["C1"]);
        var (vm, settle) = OpenEditor(c3d, cws);
        try
        {
            var (part, _) = Assert.Single(vm.ComponentPortResults);
            Assert.Equal(2, part.Landings.Count);
            var scene = vm.Viewer.Scene;
            foreach (var l in part.Landings)
            {
                var conductor = vm.SceneObject(l.Conductor);
                Assert.NotNull(conductor);
                var drawn = scene.Vertices.Skip(conductor.FirstVertex).Take(conductor.VertexCount)
                                 .Select(v => (X: v.X + scene.Origin.X, Y: v.Y + scene.Origin.Y)).ToList();
                double face = l.Box.X0 < 5000 * M ? l.Box.X1 : l.Box.X0;
                foreach (double y in new[] { l.Box.Y0, l.Box.Y1 })
                    Assert.Contains(drawn, v => Math.Abs(v.X - face) < 0.05 * M && Math.Abs(v.Y - y) < 0.05 * M);
            }
        }
        finally { vm.Dispose(); }
    }

    /// <summary>A component port's width, typed in the Inspector, is the setup's override for that part beside "*" — and the port
    /// built next is that wide, still centred on the pads.</summary>
    [Fact]
    public void TheInspector_SetsAComponentPortsWidth_BesideEveryPart()
    {
        var (_, cws, c3d) = Written(["*"]);
        var (vm, settle) = OpenEditor(c3d, cws);
        try
        {
            var before = Assert.Single(vm.ComponentPortResults, c => c.Part.Refdes == "C1").Part;
            Assert.Equal(500 * M, before.WidthM, 9);
            var row = vm.Tree.OfType<CircuitRF.Ui.ThreeD.C3dTreeGroup>().Single(g => g.Role == CircuitRF.Ui.ThreeD.C3dTreeGroupRole.ComponentPorts)
                        .Items.Single(i => i.Label == "C1");
            vm.SelectedTreeItem = row;
            vm.Properties.Reload();
            Assert.True(vm.Properties.IsComponentPortPage);
            vm.Properties.ComponentWidth = "400um";
            vm.Properties.CommitComponentPort();
            Assert.Equal("", vm.Properties.Error);
            settle();

            Assert.Equal([EmSetup.AllComponents, "C1"], vm.ActiveSetup!.ComponentPorts.Select(c => c.Refdes));
            Assert.Equal(400, vm.ActiveSetup.ComponentPorts[1].WidthUm);
            var after = Assert.Single(vm.ComponentPortResults, c => c.Part.Refdes == "C1").Part;
            Assert.Equal(400 * M, after.WidthM, 9);
            double Centre(C3dComponentPort p) => (p.Port!.Rect.Min.V + p.Port.Rect.Size.V / 2.0) * 1e-9;
            Assert.Equal(Centre(before), Centre(after), 9);
        }
        finally { vm.Dispose(); }
    }

    /// <summary>A drawn port's width typed in the Inspector resizes it about its centre; another plane keeps its centre in space and
    /// its width and height.</summary>
    [Fact]
    public void TheInspector_ResizesADrawnPort_AndTurnsItOntoAnotherPlane()
    {
        var (_, cws, c3d) = Written([]);
        var doc = C3dPersistence.LoadFromFile(c3d);
        long D(double um) => (long)Math.Round(um * doc.DbuPerMicron);
        doc.Ports.Add(new C3dPort { Number = 7, Name = "P7", Plane = C3dPlane.XY, Offset = D(543),
                                    Rect = new C3dRect { Min = new C3dPoint2(D(4900), D(4750)), Size = new C3dPoint2(D(200), D(500)) } });
        C3dPersistence.SaveToFile(c3d, doc);
        var (vm, settle) = OpenEditor(c3d, cws);
        try
        {
            vm.SelectedTreeItem = vm.Tree.OfType<CircuitRF.Ui.ThreeD.C3dTreeGroup>().Single(g => g.Role == CircuitRF.Ui.ThreeD.C3dTreeGroupRole.Ports)
                                    .Items.Single(i => i.Name.EndsWith("/7", StringComparison.Ordinal));
            vm.Properties.Reload();
            Assert.True(vm.Properties.IsPortPage);
            vm.Properties.PortWidth = "300um";
            vm.Properties.CommitPort();
            Assert.Equal("", vm.Properties.Error);
            var port = vm.Document.Ports.Single(p => p.Number == 7);
            Assert.Equal((D(4850), D(300)), (port.Rect.Min.U, port.Rect.Size.U));      // about its centre, x = 5000 µm

            vm.Properties.PortPlane = "XZ";
            Assert.Equal(C3dPlane.XZ, port.Plane);
            Assert.Equal((D(5000), D(543), D(5000)), (port.Rect.Min.U + port.Rect.Size.U / 2, port.Rect.Min.V + port.Rect.Size.V / 2, port.Offset));
            Assert.Equal((D(300), D(500)), (port.Rect.Size.U, port.Rect.Size.V));
        }
        finally { vm.Dispose(); }
    }

    /// <summary>A 3D editor on <paramref name="c3d"/> with its first scene adopted, and how to wait for the next.</summary>
    private static (CircuitRF.Ui.ThreeD.C3dEditorViewModel Vm, Action Settle) OpenEditor(string c3d, string cws)
    {
        var posted = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var fake = new CircuitRF.Ui.Tests.ThreeD.PatchRecordingBackend();
        var vm = new CircuitRF.Ui.ThreeD.C3dEditorViewModel(c3d, C3dPersistence.LoadFromFile(c3d), () => fake, () => cws, posted.Enqueue);
        void Settle() => Assert.True(SpinWait.SpinUntil(() =>
        {
            while (posted.TryDequeue(out var a)) a();
            return vm.AdoptedGeneration == vm.Viewer.Source.Requested;
        }, TimeSpan.FromSeconds(60)), "the scene never settled");
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(640, 480);
        vm.Viewer.Session.EnsureBackend();
        vm.Start();
        Settle();
        return (vm, Settle);
    }

    /// <summary>A three-pad part gets one gap port per signal pad, each against its own ground pad, numbered in list order.</summary>
    [Fact]
    public void AThreePadPart_GetsTwoPorts_EachAgainstItsGroundPad()
    {
        var (g, _) = Assemble(["U1"]);
        Assert.True(g.Ok, g.Refusal);
        var ports = g.ComponentPorts!.Ports;
        Assert.Equal(["U1.IN", "U1.OUT"], ports.Select(p => p.Port!.Name));
        Assert.Equal([1, 2], ports.Select(p => p.Port!.Number));
        Assert.All(ports, p => Assert.Equal("GND", p.ReferencePin));
        Assert.Single(ports.Select(p => p.Port!.Negative).Distinct());
        Assert.All(ports, p => Assert.Empty(p.Landings));
    }

    /// <summary>A launch whose ground pads lie over each other on top and bottom copper gets the shell's block through the
    /// substrate, and its port's negative end is that bridged ground.</summary>
    [Fact]
    public void AConnector_GetsItsShellBridged_AndItsPortReturnsThroughIt()
    {
        var (g, _) = Assemble(["J1"]);
        Assert.True(g.Ok, g.Refusal);
        var j1 = Assert.Single(g.ComponentPorts!.Ports);
        Assert.True(j1.Connector);
        Assert.Equal("GND", j1.ReferencePin);
        var bridge = Assert.Single(g.ComponentPorts.Bridges);
        Assert.Equal(j1.Port!.Negative, bridge.Top);
        var block = g.Problem!.Solids.Single(s => s.Name == bridge.Name);
        Assert.Equal(Em3dRole.Conductor, block.Role);
        var ep = Assert.IsType<Em3dExtrudedPolygon>(block.Primitive);
        Assert.True(ep.ZBottom < 0.1e-3 && ep.ZTop > 0.5e-3, $"{ep.ZBottom} … {ep.ZTop}");
    }

    /// <summary>R-emb1-8 — <c>explain --component-ports</c> lists each port's refdes, conductors, gap, width, landings and verdict
    /// without meshing, its list overriding the setup's; <c>check</c> reports the same ports (and a part left out, by refdes) —
    /// both through the CLI as a process.</summary>
    [Fact]
    public void ExplainAndCheck_ReportTheComponentPorts_ThroughTheCli()
    {
        var (_, _, c3d) = Written(["C1"]);
        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "circuitRF.slnx"))) repo = Path.GetDirectoryName(repo)!;

        var (exit, stdout, stderr) = CliProcess.Run(repo, [], "explain", c3d, "--component-ports", "C1,U1");
        output.WriteLine(stdout);
        Assert.True(exit == 0, stderr + stdout);
        Assert.Contains("C1: port 1, pad 1 on 'U1/", stdout);
        Assert.Contains("landing ", stdout);
        Assert.Contains("U1.IN: port 2", stdout);
        Assert.Contains("verified", stdout);

        (exit, stdout, stderr) = CliProcess.Run(repo, [], "check", c3d);
        output.WriteLine(stdout + stderr);
        Assert.True(exit == 0, stderr + stdout);
        Assert.Contains("setup 'board': C1: port 1", stdout + stderr);

        // A part that cannot be built is left out with a warning and the rest are built, so check passes and says so.
        var (_, _, bad) = Written(["C1", "Q9"]);
        (exit, stdout, stderr) = CliProcess.Run(repo, [], "check", bad);
        Assert.True(exit == 0, stderr + stdout);
        Assert.Contains("Q9 is no part placed on this 3D view's layout", stdout + stderr);
        Assert.Contains("Q9 is left out of this run", stdout + stderr);
        Assert.Contains("setup 'board': C1: port 1", stdout + stderr);
    }

    /// <summary>
    /// §2's one small Palace run: a 0.4 mm line on the 508 µm board broken by a 0.4 mm gap, R1's two pads either side of it, a
    /// drawn port at each end of the line and R1's component port across the gap. The component port must see the two
    /// terminated half-lines (|S33| well short of 1: a sheet short of its copper reads ≈ 1, an open circuit), and the
    /// co-simulation netlist the run writes — R1 a 50 Ω placeholder from its port's node to ground — must elaborate and pass
    /// about two thirds of the wave between the drawn ports (50 Ω in series between two 50 Ω loads: |S21| = 2/3).
    /// </summary>
    [PalaceFact]
    [Trait("Category", "Benchmark")]
    public void APalaceRun_SeesTheGapThroughTheComponentPort_AndItsCoSimulationRuns()
    {
        string ws = Path.Combine(_root, "palace");
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz"));
        string cws = Path.Combine(ws, ".cws");
        WorkspacePersistence.SaveToFile(cws, new CwsFile { DefaultTechRef = "tech.ctech" });
        Footprint(ws, "Res", [("1", -400, 0, 400, 400, Top), ("2", 400, 0, 400, 400, Top)]);
        string cell = CellFolder.CreateCellFolder(ws, "Board");
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = Bottom, X1 = 0, Y1 = Um(-1000), X2 = Um(4200), Y2 = Um(1000) });
        view.Shapes.Add(new RectShape { Layer = Top, X1 = 0, Y1 = Um(-200), X2 = Um(1700), Y2 = Um(200) });
        view.Shapes.Add(new RectShape { Layer = Top, X1 = Um(2500), Y1 = Um(-200), X2 = Um(4200), Y2 = Um(200) });
        foreach (var (text, x, dir) in new[] { ("1", 0.0, LayoutRotation.R0), ("2", 4200.0, LayoutRotation.R180) })
            view.Shapes.Add(new LabelShape
            {
                Layer = Top, PortLayer = Top, X = Um(x), Y = 0, Text = text, Height = Um(200), IsPort = true,
                PortKind = PlanarPortKind.Edge, PortDirection = dir,
            });
        view.Instances.Add(Place("Res", "R1", 2100, 0));
        CellCreate.WriteLayoutView(cell, "Board", view);
        string c3d = Path.Combine(CellFolder.SubFolderPath(cell, ViewType.ThreeD), "Board" + C3dPersistence.Extension);
        var doc = C3dHierarchy.NewFromLayout(cell, c3d, cws).Document!;
        Assert.Equal(2, doc.Ports.Count);
        var face = new EmAirBoxFace(1000, null);
        doc.Setups.Clear();
        doc.Setups.Add(EmSetupPersistence.ToEmbedded(new EmSetup
        {
            Name = "board", Solver3D = Em3dSolverId.Palace,
            Frequency = new FrequencySpec("2", "2", 1, SweepKind.Linear, "GHz", "GHz"),
            AirBox = new EmAirBox(face, face, face, face, null, face),
            ComponentPorts = [new EmComponentPort("R1")],
        }));
        CellCreate.WriteThreeDView(cell, "Board", doc);

        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "circuitRF.slnx"))) repo = Path.GetDirectoryName(repo)!;
        var (exit, stdout, stderr) = CliProcess.Run(repo, [], "em", c3d);
        output.WriteLine(stdout + stderr);
        Assert.True(exit == 0, stderr);
        string snp = Directory.GetFiles(Path.Combine(ws, "results"), "*.s3p", SearchOption.AllDirectories).Single();
        var board = RfCore.TouchstoneIO.ReadFile(snp);
        double s33 = board[0][2, 2].Magnitude;
        output.WriteLine($"|S33| = {s33:F3}");
        Assert.InRange(s33, 0, 0.9);

        string cosim = Path.Combine(ws, "Board board_cosim.cnl");
        Assert.True(File.Exists(cosim), stdout);
        string outSnp = Path.Combine(ws, "cosim.s2p");
        (exit, stdout, stderr) = CliProcess.Run(repo, [], "sparam", cosim, "-o", outSnp);
        Assert.True(exit == 0, stderr + stdout);
        double s21 = RfCore.TouchstoneIO.ReadFile(outSnp)[0][1, 0].Magnitude;
        output.WriteLine($"co-simulation |S21| = {s21:F3}");
        Assert.InRange(s21, 0.5, 0.8);
    }

    // ── the fixture ────────────────────────────────────────────────────────────────────────────

    private (string Cell, string Cws, string C3d) Written(string[] parts)
    {
        var (cell, c3dPath, cws) = Board([]);
        var doc = C3dHierarchy.NewFromLayout(cell, c3dPath, cws).Document!;
        doc.Setups.Add(EmSetupPersistence.ToEmbedded(new EmSetup
        {
            Name = "board", Solver3D = Em3dSolverId.Palace,
            Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
            ComponentPorts = [.. parts.Select(p => new EmComponentPort(p))],
        }));
        CellCreate.WriteThreeDView(cell, "Board", doc);
        return (cell, cws, c3dPath);
    }

    private (CircuitRF.Design.Layout.Em3d.Em3dGenerationResult Result, int DrawnConductors) Assemble(
        string[] parts, IEnumerable<LayoutShape>? extra = null)
    {
        var (cell, c3dPath, cws) = Board(extra ?? []);
        var made = C3dHierarchy.NewFromLayout(cell, c3dPath, cws);
        var doc = Assert.IsType<C3dDocument>(made.Document);
        var setup = new EmSetup
        {
            Name = "board", Solver3D = Em3dSolverId.Palace,
            Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
            ComponentPorts = [.. parts.Select(p => new EmComponentPort(p))],
        };
        doc.Setups.Add(EmSetupPersistence.ToEmbedded(setup));
        CellCreate.WriteThreeDView(cell, "Board", doc);
        doc = C3dPersistence.LoadFromFile(c3dPath);
        var embedded = C3dSetups.Select(doc, null).Setup!;
        Assert.Equal(parts, embedded.ComponentPorts.Select(p => p.Refdes));
        var run = C3dSetups.ForRun(embedded, c3dPath);
        var drawn = new C3dElaborator().Elaborate(doc, c3dPath, cws, new C3dElaborationOptions(3e9) { SolidVias = true });
        return (C3dProblemAssembly.Assemble(run, doc, c3dPath, cws), drawn.Solids.Count(s => s.Role == Em3dRole.Conductor));
    }

    private (string Cell, string C3d, string Cws) Board(IEnumerable<LayoutShape> extra)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz"));
        string cws = Path.Combine(ws, ".cws");
        WorkspacePersistence.SaveToFile(cws, new CwsFile { DefaultTechRef = "tech.ctech" });

        Footprint(ws, "Cap", [("1", -500, 0, 400, 500, Top), ("2", 500, 0, 400, 500, Top)]);
        Footprint(ws, "Amp", [("IN", -600, 0, 300, 400, Top), ("GND", 0, 0, 300, 400, Top), ("OUT", 600, 0, 300, 400, Top)]);
        Footprint(ws, "Sma", [("1", 0, 0, 300, 300, Top), ("GND", 0, 700, 600, 600, Top), ("SHELL", 0, 700, 600, 600, Bottom)]);

        string dir = CellFolder.CreateCellFolder(ws, "Board");
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(new RectShape { Layer = Bottom, X1 = 0, Y1 = 0, X2 = Um(10000), Y2 = Um(10000) });
        // C1's gap: a round-capped trace into pad 1, a round pour over pad 2.
        view.Shapes.Add(new PathShape { Layer = Top, Xy = [Um(3000), Um(5000), Um(4700), Um(5000)], Width = Um(300), End = PathEndStyle.Round });
        view.Shapes.Add(new CircleShape { Layer = Top, Cx = Um(5500), Cy = Um(5000), R = Um(280) });
        view.Shapes.AddRange(extra);
        view.Instances.Add(Place("Cap", "C1", 5000, 5000));
        view.Instances.Add(Place("Amp", "U1", 5000, 8000));
        view.Instances.Add(Place("Sma", "J1", 2000, 2000));
        CellCreate.WriteLayoutView(dir, "Board", view);
        return (dir, Path.Combine(CellFolder.SubFolderPath(dir, ViewType.ThreeD), "Board" + C3dPersistence.Extension), cws);
    }

    private static LayoutInstance Place(string cell, string refdes, double xUm, double yUm) => new()
    {
        CellRef = "../../" + cell, X = Um(xUm), Y = Um(yUm), Mag = 1.0, RefDes = refdes,
    };

    private static void Footprint(string ws, string name, (string Pin, double X, double Y, double W, double H, LayerKey Layer)[] pads)
    {
        string cellDir = CellFolder.CreateCellFolder(ws, name);
        var cell = new LayoutView { DbuPerMicron = Dbu };
        foreach (var (pin, x, y, w, h, layer) in pads)
        {
            cell.Pins.Add(new LayoutPin { Name = pin, X = Um(x), Y = Um(y), WidthDbu = Um(Math.Min(w, h)), Layer = layer });
            cell.Shapes.Add(new RectShape { Layer = layer, Pin = pin, X1 = Um(x - w / 2), Y1 = Um(y - h / 2), X2 = Um(x + w / 2), Y2 = Um(y + h / 2) });
        }
        CellCreate.WriteLayoutView(cellDir, name, cell);
    }
}
