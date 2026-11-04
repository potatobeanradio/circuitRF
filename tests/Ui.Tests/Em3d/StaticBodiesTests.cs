using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-dfr31-1 — an electrostatic setup on a board with no nets: connected bodies, names a person
//  types, floating metal. Synthetic geometry only. Gates 1, 2 and 4 and the setup round trip need
//  nothing installed; gate 3's Palace run skips without Palace (its closed-form half is
//  Engine.Tests' StaticKronTests).
// ══════════════════════════════════════════════════════════════════════════════════════════════

[Collection(Em3dProcessCountersCollection.Name)]
public sealed class StaticBodiesTests(ITestOutputHelper output) : IDisposable
{
    private const double Eps0 = 8.8541878128e-12;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-bodies-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ── 1. The bottom pad alone takes its whole piece of metal ──────────────────────────────────

    [Fact]
    public void Gate1_TheBottomPadAlone_TakesPadBarrelInnerPadAndLine_AndThePlanesAndStitchingAreOneBody()
    {
        var (setup, source) = Board(gndNets: false);
        var g = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(g.Ok, g.Refusal);
        var p = g.Problem!;

        string pad = PieceAt(p, "Bottom Copper (1 oz)", 10, 0);
        setup.Terminals3D = [new("T", "", null, [pad])];
        g = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(g.Ok, g.Refusal);
        p = g.Problem!;

        var t = Assert.Single(p.Terminals, x => !x.Floating);
        Assert.Equal(new[] { PieceAt(p, "Top Copper (1 oz)", 10, 0), PieceAt(p, "Inner 2", 10, 0), pad, "via/1" }.Order(),
                     t.Objects.Order());

        // The rest is one body: both planes and the four stitching vias, floating (the default) as one more solve.
        var planes = Assert.Single(p.Terminals, x => x.Floating);
        Assert.Equal(new[] { PieceAt(p, "Inner 1 (Ground Plane)", 2, 0), PieceAt(p, "Bottom Copper (1 oz)", 2, 0),
                             "via/2", "via/3", "via/4", "via/5" }.Order(), planes.Objects.Order());
        Assert.Empty(p.FloatingConductors());
        Assert.Null(PalaceConfigWriter.StaticRefusal(p));
        Assert.Contains(g.Notes, n => n.StartsWith("1 floating piece(s) of metal add 1 solve(s)", StringComparison.Ordinal));
    }

    // ── 2. Ground mode is the hand-assigned GND net, to the attribute ───────────────────────────

    /// <summary>
    /// Other metal: Ground on the net-less board against GND given by hand to the planes and stitching vias with
    /// Ground3D: GND (the round-26 path). The two problems lower to the SAME Palace configuration — every attribute in the
    /// same terminal or ground — over the same geometry, so their capacitance is the same number, not merely within 0.5 %.
    /// </summary>
    [Fact]
    public void Gate2_GroundMode_LowersExactlyAsHandAssignedGndNetsWithGround3D()
    {
        var (byMode, src1) = Board(gndNets: false);
        byMode.UnassignedConductors3D = EmUnassignedConductors.Ground;
        var (byNet, src2) = Board(gndNets: true);
        byNet.Ground3D = "GND";
        foreach (var (s, src) in new[] { (byMode, src1), (byNet, src2) })
        {
            var first = Em3dGenerator.Generate(s, src, src.Technology!);
            s.Terminals3D = [new("T", "", null, [PieceAt(first.Problem!, "Bottom Copper (1 oz)", 10, 0)])];
        }
        var a = Em3dGenerator.Generate(byMode, src1, src1.Technology!);
        var b = Em3dGenerator.Generate(byNet, src2, src2.Technology!);
        Assert.True(a.Ok, a.Refusal);
        Assert.True(b.Ok, b.Refusal);
        Assert.Contains(a.Notes, n => n.Contains("are ground (UnassignedConductors3D: Ground)", StringComparison.Ordinal));
        Assert.DoesNotContain(b.Problem!.Terminals, t => t.Floating);

        Assert.Equal(a.Problem!.Solids.Select(s => (s.Role, Em3dProblem.Bounds(s.Primitive))),
                     b.Problem!.Solids.Select(s => (s.Role, Em3dProblem.Bounds(s.Primitive))));
        Assert.Equal(a.Problem.GroundObjects.Count, b.Problem.GroundObjects.Count);
        string Config(Em3dProblem p)
        {
            var low = GmshGeoWriter.Write(p, PalaceSettings.Default);
            Assert.True(low.Ok, low.Refusal);
            var cfg = PalaceConfigWriter.Write(p, low.Groups, PalaceSettings.Default);
            Assert.True(cfg.Ok, cfg.Refusal);
            return cfg.Json!;
        }
        Assert.Equal(Config(b.Problem), Config(a.Problem));
    }

    // ── 3. A floating plate between two plates, through Palace ──────────────────────────────────

    /// <summary>
    /// Plates A, M and B stacked over each other with PMC sides, so each gap is a uniform field: M floating, the reduced
    /// C between A and B is the two gaps' capacitors in series. Within the 1 % the static tests' plate gate uses.
    /// </summary>
    [PalaceFact]
    public void Gate3_AFloatingPlateBetweenTwoPlates_GivesTheTwoGapsInSeries()
    {
        const double Um = 1e-6;
        double w = 1000 * Um, t = 20 * Um, d1 = 100 * Um, d2 = 150 * Um, er = 2, top = 600 * Um;
        double zA = 100 * Um, zM = zA + t + d1, zB = zM + t + d2;
        // A PEC floor under A, as the static tests' plate gate has: it couples to A alone, so A–B is still the series pair.
        var faces = new Em3dFaces(Em3dBoundaryKind.Pmc, Em3dBoundaryKind.Pmc, Em3dBoundaryKind.Pmc, Em3dBoundaryKind.Pmc,
                                  Em3dBoundaryKind.Pec, Em3dBoundaryKind.Pmc);
        Em3dSolid Plate(string n, double z) => new(n, "metal", Em3dRole.Conductor, new Em3dBox(new(0, 0, z), new(w, w, z + t)), 2);
        Em3dSolid Fill(string n, double z0, double z1) => new(n, "fill", Em3dRole.Dielectric, new Em3dBox(new(0, 0, z0), new(w, w, z1)), 1);
        var problem = new Em3dProblem(
            [
                Fill("below", 0, zA), Fill("gap1", zA + t, zM), Fill("gap2", zM + t, zB), Fill("above", zB + t, top),
                Plate("A", zA), Plate("M", zM), Plate("B", zB),
            ],
            [], [new Em3dMaterial("fill", er, null, 0, 1, 0), new Em3dMaterial("metal", 1, null, 0, 1, 5.8e7)], [],
            new Em3dAirBox(new(0, 0, 0), new(w, w, top), faces), new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 20)
        {
            Type = Em3dProblemType.Electrostatic,
            Terminals = [new Em3dTerminal("A", ["A"]), new Em3dTerminal("B", ["B"]), new Em3dTerminal("M", ["M"]) { Floating = true }],
        };
        Assert.Null(PalaceConfigWriter.StaticRefusal(problem));

        var settings = PalaceSettings.Default with { AdaptiveMaxIterations = 0, MaxElementWavelengths = 0.15, EdgeRefinement = 1 };
        var low = GmshGeoWriter.Write(problem, settings);
        Assert.True(low.Ok, low.Refusal);
        var cfg = PalaceConfigWriter.Write(problem, low.Groups, settings);
        Assert.True(cfg.Ok, cfg.Refusal);
        string dir = Path.Combine(_root, "float");
        var mesh = PalaceRun.Mesh(dir, low, SolverDiscovery.Gmsh.Find(out _)!.Path, null, default);
        Assert.True(mesh.Ok, mesh.Message);
        string palace = SolverDiscovery.ReadinessFor(Em3dSolverId.Palace).Single(r => r.Tool == SolverTool.Palace).Installation!.Path;
        var tracker = new PalaceStageTracker(0, settings.SweepAdaptiveTol, null, true);
        var solved = PalaceRun.Solve(dir, cfg.Json!, palace, 1, null, default, out _, tracker);
        Assert.True(solved.Ok, solved.Message);
        var full = PalaceRun.ReadTerminalMatrix(Path.Combine(dir, PalaceConfigWriter.OutputDirectory, PalaceRun.CapacitanceFile),
                                                "C", "(F)", [1, 2, 3], out string? error);
        Assert.True(full is not null, error);

        var c = Em3dStaticResult.EliminateFloating(full!, [false, false, true]);
        double c1 = Eps0 * er * w * w / d1, c2 = Eps0 * er * w * w / d2, series = c1 * c2 / (c1 + c2);
        output.WriteLine($"C_AB = {-c[0, 1]:G6} F, series closed form {series:G6} F, {100 * (-c[0, 1] / series - 1):F3} %");
        Assert.InRange(-c[0, 1] / series, 0.99, 1.01);
        Assert.Equal(c[0, 1], c[1, 0], 1e-6 * c[0, 0]);
    }

    // ── 4. What a person types ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_ANetAUniqueSuffixAndAnExactPathEachResolve_AndAnAmbiguousSuffixListsTheCandidates()
    {
        string[] conductors = ["U1/Top Copper (1 oz)/RF_LINE", "U1/Bottom Copper (1 oz)/TESTPT", "U1/Bottom Copper (1 oz)/1",
                               "U1/Inner 1 (Ground Plane)/1", "via/1"];
        var nets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["U1/Top Copper (1 oz)/RF_LINE"] = ["RF"], ["via/1"] = ["RF"],
        };
        var names = new Em3dConductorNames(conductors, nets);

        Assert.Equal(["U1/Bottom Copper (1 oz)/1"], names.Resolve("U1/Bottom Copper (1 oz)/1").Objects);
        Assert.Equal(["U1/Top Copper (1 oz)/RF_LINE", "via/1"], names.Resolve("RF").Objects);
        var suffix = names.Resolve("TESTPT");
        Assert.Equal("suffix", suffix.How);
        Assert.Equal(["U1/Bottom Copper (1 oz)/TESTPT"], suffix.Objects);

        var ambiguous = names.Resolve("1");
        Assert.False(ambiguous.Ok);
        Assert.Equal(3, ambiguous.Ambiguous.Count);
        string text = Em3dConductorNames.AmbiguousText("1", ambiguous.Ambiguous);
        foreach (string c in ambiguous.Ambiguous) Assert.Contains($"'{c}'", text);

        var none = names.Resolve("TESTP");
        Assert.False(none.Ok);
        Assert.Contains("U1/Bottom Copper (1 oz)/TESTPT", none.Near);
        Assert.True(none.Near.Count <= Em3dConductorNames.NearNames);

        // Through the generator, the same refusal names every candidate, for a terminal and for the ground alike.
        var (setup, source) = Board(gndNets: false);
        setup.Terminals3D = [new("T", "", null, ["1"])];
        var g = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.False(g.Ok);
        Assert.Contains("Terminal 'T' names '1' ends", g.Refusal);
        Assert.Contains("'Inner 2/1'", g.Refusal);
        setup.Terminals3D = [new("T", "", null, ["Inner 2/1"])];
        setup.Ground3D = "1";
        Assert.Contains("ground (Ground3D) is '1' ends", Em3dGenerator.Generate(setup, source, source.Technology!).Refusal);
    }

    // ── 5. Floating metal: refused when asked, bounded, and stored ──────────────────────────────

    [Fact]
    public void OtherMetal_RefuseNamesTheBodies_AndTheChoiceRoundTripsWithFloatWritingNothing()
    {
        var (setup, source) = Board(gndNets: false);
        setup.Terminals3D = [new("T", "", null, ["Inner 2/1"])];
        setup.UnassignedConductors3D = EmUnassignedConductors.Refuse;
        var g = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.False(g.Ok);
        Assert.Contains("1 piece(s) of metal are in no terminal", g.Refusal);
        Assert.Contains("'body:", g.Refusal);
        Assert.Contains("(6 objects)", g.Refusal);

        // Two terminals naming one piece of metal are refused with both names.
        setup.UnassignedConductors3D = EmUnassignedConductors.Float;
        setup.Terminals3D = [new("T", "", null, ["Inner 2/1"]), new("U", "", null, ["via/1"])];
        Assert.Contains("Terminals 'T' and 'U' are one piece of metal", Em3dGenerator.Generate(setup, source, source.Technology!).Refusal);

        Assert.DoesNotContain("UnassignedConductors3D", EmSetupPersistence.Serialize(new EmSetup { Name = "s" }));
        foreach (var v in Enum.GetValues<EmUnassignedConductors>())
        {
            var s = new EmSetup { Name = "s", UnassignedConductors3D = v };
            Assert.Equal(v, EmSetupPersistence.Deserialize(EmSetupPersistence.Serialize(s)).UnassignedConductors3D);
            Assert.Equal(v, s.Clone().UnassignedConductors3D);
        }
    }

    // ── the board ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A 4-layer board, 20 × 10 mm, nothing a ground reference: a line on Top Copper to a via at (10, 0) that lands on a
    /// pad on Inner 2 and ends in a test pad on Bottom Copper; Inner 1 and Bottom Copper are planes with a clearance round
    /// the via, stitched by four vias. No nets, unless <paramref name="gndNets"/> gives the planes and stitching vias GND.
    /// </summary>
    internal static (EmSetup, EmLayoutSource) Board(bool gndNets)
    {
        string net = gndNets ? """, "Net": "GND" """ : "";
        static long D(double mm) => (long)Math.Round(mm * 1e6);
        string Rect(int layer, double x1, double y1, double x2, double y2, string extra = "")
            => $$"""{ "$type": "Rect", "Layer": { "Layer": {{layer}}, "Datatype": 0 }{{extra}}, "X1": {{D(x1)}}, "Y1": {{D(y1)}}, "X2": {{D(x2)}}, "Y2": {{D(y2)}} }""";
        string Via(double x, double y, string extra = "")
            => $$"""{ "$type": "Via", "Layer": { "Layer": 9, "Datatype": 0 }{{extra}}, "X": {{D(x)}}, "Y": {{D(y)}}, "PadSize": {{D(0.6)}}, "DrillSize": {{D(0.3)}} }""";
        var shapes = new List<string>
        {
            Rect(1, 2, -0.4, 10.4, 0.4),                  // the line, ending over the via
            Via(10, 0),                                   // via/1
            Rect(3, 9.5, -0.5, 10.5, 0.5),                // the inner landing pad
            Rect(4, 9, -1, 11, 1),                        // the test pad
            // Inner 1: a plane with a 1.6 mm clearance round the via
            Rect(2, 0, -5, 9.2, 5, net), Rect(2, 10.8, -5, 20, 5, net), Rect(2, 9.2, 0.8, 10.8, 5, net), Rect(2, 9.2, -5, 10.8, -0.8, net),
            // Bottom Copper: a plane with a 4 mm clearance round the test pad
            Rect(4, 0, -5, 8, 5, net), Rect(4, 12, -5, 20, 5, net), Rect(4, 8, 2, 12, 5, net), Rect(4, 8, -5, 12, -2, net),
            Via(4, 3.5, net), Via(16, 3.5, net), Via(4, -3.5, net), Via(16, -3.5, net),
        };
        string clay = $$"""
            { "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Mm", "SnapDbu": 10000,
              "Shapes": [ {{string.Join(",\n", shapes)}} ], "Instances": [] }
            """;
        var view = LayoutPersistence.Deserialize(clay);
        var tech = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        foreach (var l in tech.Stackup.Layers) l.IsGroundReference = false;
        var setup = new EmSetup
        {
            Name = "board", LayoutRef = "board/layout/board.clay",
            Solver3D = Em3dSolverId.Palace,
            Problem3D = Em3dProblemType.Electrostatic,
            Terminals3D = [new("T", "", null, ["Inner 2/1"])],
        };
        return (setup, new EmLayoutSource("/nowhere/board.clay", view, tech, view.DbuPerMicron));
    }

    /// <summary>The conductor on <paramref name="layer"/> whose footprint holds (x, y) mm.</summary>
    internal static string PieceAt(Em3dProblem p, string layer, double xMm, double yMm)
    {
        double x = xMm * 1e-3, y = yMm * 1e-3;
        return p.Solids.Where(s => s.Role == Em3dRole.Conductor && s.Name.StartsWith(layer + "/", StringComparison.Ordinal))
                       .Concat(p.Sheets.Select(sh => new Em3dSolid(sh.Name, sh.Material, Em3dRole.Conductor,
                                                                    new Em3dExtrudedPolygon(sh.Outline, sh.Holes, sh.Z, sh.Z), 0))
                                       .Where(s => s.Name.StartsWith(layer + "/", StringComparison.Ordinal)))
                       .Single(s => s.Primitive is Em3dExtrudedPolygon e && Inside(e.Outline, x, y) && !e.Holes.Any(h => Inside(h, x, y))).Name;
    }

    private static bool Inside(IReadOnlyList<Point2> ring, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            if ((ring[i].Y > y) != (ring[j].Y > y) &&
                x < (ring[j].X - ring[i].X) * (y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X)
                inside = !inside;
        return inside;
    }
}

/// <summary>
/// brief-dfr31-1 gate 5, the view model's half (this project has no Avalonia platform; the view itself is drawn by
/// tools/UiSnapshot): the picker lists the problem's bodies, choosing one writes the terminal, the ground picker offers
/// "Every body not in a terminal", and a pick in the 3D view fills the armed row.
/// </summary>
public sealed class StaticBodyPickerTests
{
    [Fact]
    public void ThePickerListsTheBodies_ChoosingOneWritesTheTerminal_AndTheGroundOffersEveryBodyNotInATerminal()
    {
        var (setup, source) = StaticBodiesTests.Board(gndNets: false);
        var g = Em3dGenerator.Generate(setup, source, source.Technology!);
        var bodies = Em3dBodyList.Of(Em3dLayoutSolids.From(source, source.Technology!, null,
            new Em3dLayoutSolidsOptions(20e9, 25, Instance: false) { RegionSetup = setup }));
        Assert.Equal(2, bodies.Count);

        var vm = new CircuitRF.Ui.Layout.Em.EmSetupEditorViewModel("/nowhere/board.cem", setup, embedded: true);
        string? committed = null;
        vm.EmbeddedCommit = (_, after, _) => committed = after;
        vm.SetBodies(bodies);

        Assert.Equal(bodies.Select(b => b.Token), vm.BodyPicks.Select(p => p.Token));
        Assert.Equal(CircuitRF.Ui.Layout.Em.EmSetupEditorViewModel.EveryBodyLabel, vm.GroundPicks[0].Label);
        Assert.Null(vm.GroundPicks[0].Body);
        var signal = vm.BodyPicks.Single(p => p.Body!.Members.Contains("via/1"));
        Assert.Contains("4 objects", signal.Detail);
        Assert.Contains("Top Copper (1 oz)", signal.Detail);

        // Choosing a row writes its token, and the run resolves that token to the same body.
        vm.TerminalRows[0].Net = signal.Token;
        vm.CommitTerminals();
        Assert.Equal([signal.Token], vm.Working.Terminals3D[0].Objects);
        var run = Em3dGenerator.Generate(vm.Working, source, source.Technology!);
        Assert.True(run.Ok, run.Refusal);
        Assert.Equal(signal.Body!.Members.Order(), run.Problem!.Terminals.Single(t => !t.Floating).Objects.Order());

        // "Every body not in a terminal" clears the ground box and grounds the rest.
        vm.PickGround(vm.GroundPicks[0]);
        Assert.Equal("", vm.Working.Ground3D);
        Assert.Equal(EmUnassignedConductors.Ground, vm.Working.UnassignedConductors3D);
        Assert.NotNull(committed);

        // A pick in the 3D view fills the armed row with the picked object's body, then disarms.
        vm.TerminalRows[0].Net = "";
        vm.PickTerminalCommand.Execute(vm.TerminalRows[0]);
        Assert.True(vm.TerminalRows[0].IsPicking);
        IReadOnlyList<string>? highlighted = null;
        vm.HighlightRequested += m => highlighted = m;
        Assert.True(vm.PickConductor(StaticBodiesTests.PieceAt(g.Problem!, "Bottom Copper (1 oz)", 10, 0)));
        Assert.False(vm.TerminalRows[0].IsPicking);
        Assert.Equal(signal.Token, vm.TerminalRows[0].Net);
        Assert.Equal(signal.Body.Members.Order(), highlighted!.Order());
    }
}
