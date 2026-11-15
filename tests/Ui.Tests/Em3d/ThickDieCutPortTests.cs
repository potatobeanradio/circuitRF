// brief-icf-6 — 3D on a thick die: where a cut pin's sheet lands, and the air box. A synthetic fixture: the shipped silicon RFIC
// technology with its substrate made 750 µm thick (the undrawn backside the floor), a 40 × 20 µm cell cut out of a 10 µm line.
// Assembled problems only, nothing solved.

using System.Text.Json.Nodes;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

public sealed class ThickDieCutPortTests(ThickDieCutPortTests.Fixture fx, ITestOutputHelper output) : IClassFixture<ThickDieCutPortTests.Fixture>
{
    private const int Dbu = 1000;
    private static readonly LayerKey M1 = new(1, 0), M2 = new(2, 0), M3 = new(3, 0);
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    // ── Gate 1 (R-icf6-1): a stated Return lands every sheet on it; without it, the walk lands them as before ───────────────

    [Fact]
    public void AStatedReturn_LandsEverySheetOnIt_AndWithoutIt_TheWalkDecides()
    {
        // Pins on Metal2 with plates of Metal3 above and Metal1 below each face: the walk takes the nearer, Metal1 (0.85 µm down
        // against 1 µm up); Return: Metal3 sends both up, and one pin's own Return overrides the entry's walk for that pin alone.
        var walked = Landings(fx.Stacked(), new EmComponentPort("X1"));
        Assert.All(walked, l => Assert.StartsWith("down to 'Metal1'", l.Landing));
        var stated = Landings(fx.Stacked(), new EmComponentPort("X1") { Return = "Metal3" });
        Assert.All(stated, l => Assert.StartsWith("up to 'Metal3'", l.Landing));
        var perPin = Landings(fx.Stacked(), new EmComponentPort("X1").WithPins(new Dictionary<string, EmPinOptions> { ["B"] = new() { Return = "Metal3" } }));
        Assert.StartsWith("down to 'Metal1'", perPin.Single(l => l.Pin == "A").Landing);
        Assert.StartsWith("up to 'Metal3'", perPin.Single(l => l.Pin == "B").Landing);

        // The .cem keys: the entry's Return and a pin's, read back as written.
        var setup = fx.Setup(new EmComponentPort("X1") { Return = "Metal3" }.WithPins(new Dictionary<string, EmPinOptions> { ["B"] = new() { Return = "Metal1" } }));
        var back = EmSetupPersistence.Deserialize(EmSetupPersistence.Serialize(setup));
        Assert.Equal(setup.ComponentPorts, back.ComponentPorts);
        Assert.Equal("Metal1", back.ComponentPorts[0].ReturnOf("B"));
        Assert.Equal("Metal3", back.ComponentPorts[0].ReturnOf("A"));
    }

    // ── Gate 2 (R-icf6-1): a Return that does not cover a face is refused, naming the pin and what it found ─────────────────

    [Fact]
    public void AReturnThatDoesNotCoverAFace_IsRefused_NamingThePin()
    {
        var g = Problem(fx.Die(), new EmComponentPort("X1") { Return = "Metal1" });
        var cut = g.ComponentPorts!.Ports.Where(p => p.Cutout).ToList();
        foreach (var p in cut) output.WriteLine(C3dComponentPorts.Describe(p));
        Assert.StartsWith("down to 'Metal1'", cut.Single(p => p.Pin == "A").Landing);
        var b = cut.Single(p => p.Pin == "B");
        Assert.False(b.Ok);
        Assert.StartsWith("X1.B: ", b.Refusal, StringComparison.Ordinal);
        Assert.Contains("finds no metal of 'Metal1' (as its cut entry's Return states)", b.Refusal, StringComparison.Ordinal);
        Assert.Contains("'Metal1' (no metal under the line's end)", b.Refusal, StringComparison.Ordinal);
    }

    /// <summary>R-icf6-1 — Planar has no sheet to land: a stated Return changes nothing there and is said once.</summary>
    [Fact]
    public void Planar_IgnoresReturn_AndSaysSoOnce()
    {
        var planar = fx.Setup(new EmComponentPort("X1") { Return = "Metal1" }.WithPins(new Dictionary<string, EmPinOptions> { ["B"] = new() { Return = "Metal2" } }));
        planar.Solver3D = Em3dSolverChoice.None;
        planar.AnalysisKind = EmAnalysisKind.Planar;
        var build = EmPlanarComponentPorts.Build(planar, fx.Source(fx.Die()))!;
        Assert.Single(build.Notes, n => n.StartsWith("The cut entries' Return is not used on Planar", StringComparison.Ordinal));
        Assert.Equal(2, build.Ports.Count(p => p.Cutout && p.Ok));
    }

    // ── Gate 3 (R-icf6-2): check warns on a sheet falling ~755 µm, and not on one landing a few µm down ───────────────────────

    [Fact]
    public void Check_WarnsOnASheetFallingToTheFloor_NotOnOneLandingNearby()
    {
        string cem = fx.Cem("warn", fx.Die(), new EmComponentPort("X1"));
        var (_, stdout, stderr) = CliProcess.Run(RepoRoot(), [], "check", cem, "--json");
        output.WriteLine(stderr);
        var diagnostics = JsonNode.Parse(stdout)!["diagnostics"]!.AsArray()
            .Select(d => (Id: d!["id"]!.GetValue<string>(), Message: d["message"]!.GetValue<string>())).ToList();
        foreach (var d in diagnostics.Where(d => d.Message.Contains("X1.", StringComparison.Ordinal))) output.WriteLine($"{d.Id}: {d.Message}");
        var falls = Assert.Single(diagnostics, d => d.Id == "check.cem.component-port-warning" && d.Message.Contains("falls", StringComparison.Ordinal));
        Assert.Contains("X1.B (port ", falls.Message, StringComparison.Ordinal);
        Assert.Matches(@"falls 75\d(\.\d+)? µm to the floor \('Backside Metal'\)", falls.Message);
        Assert.Contains("State the cut entry's Return", falls.Message, StringComparison.Ordinal);
        // Each pin's landing and height, in its port's line: A on Metal1 a few µm down, B on the floor.
        Assert.Contains(diagnostics, d => d.Id == "check.cem.component-port" && d.Message.Contains("X1.A", StringComparison.Ordinal)
                                          && System.Text.RegularExpressions.Regex.IsMatch(d.Message, @"landing down to 'Metal1', [0-4](\.\d+)? µm"));
        Assert.Contains(diagnostics, d => d.Id == "check.cem.component-port" && d.Message.Contains("X1.B", StringComparison.Ordinal)
                                          && d.Message.Contains("landing down to the floor, 'Backside Metal', 75", StringComparison.Ordinal));
    }

    // ── Gate 4 (R-icf6-3): a sheet to the floor pads the sides from the column and runs the substrate to the box ─────────────

    [Fact]
    public void ASheetToTheFloor_PadsFromTheColumn_AndTheSubstrateReachesTheBox()
    {
        var g = Problem(fx.Die(), new EmComponentPort("X1"));
        var p = g.Problem!;
        double column = Assert.IsType<double>(p.PaddingColumnM);
        output.WriteLine($"column {column * 1e6:F2} µm; box x {p.Boundary.Min.X * 1e6:F1} … {p.Boundary.Max.X * 1e6:F1} µm");
        foreach (var n in g.Notes) output.WriteLine(n);
        Assert.InRange(column, 750e-6, 760e-6);
        var (lo, hi) = p.PaddedContent!.Value;
        foreach (double pad in new[] { lo.X - p.Boundary.Min.X, p.Boundary.Max.X - hi.X, lo.Y - p.Boundary.Min.Y, p.Boundary.Max.Y - hi.Y })
            Assert.InRange(pad, 0.9 * Em3dGenerator.DefaultPaddingColumn * column, 1.1 * Em3dGenerator.DefaultPaddingColumn * column);
        Assert.Contains(g.Notes, n => n.StartsWith("A port sheet reaches the undrawn ground floor", StringComparison.Ordinal));
        var silicon = p.Solids.Single(s => g.Origins.TryGetValue(s.Name, out var o) && o.StackupEntry == "Silicon");
        var b = Em3dProblem.Bounds(silicon.Primitive);
        Assert.Equal((p.Boundary.Min.X, p.Boundary.Min.Y, p.Boundary.Max.X, p.Boundary.Max.Y), (b.X0, b.Y0, b.X1, b.Y1));
    }

    /// <summary>Gate 4's other half — with no sheet to the floor the box is the one the old call builds (no column passed), the slabs
    /// stop at the artwork and the problem carries no column or padded content: the code path is today's.</summary>
    [Fact]
    public void NoSheetToTheFloor_KeepsTodaysBox()
    {
        var g = Problem(fx.Die(metal1UnderBoth: true), new EmComponentPort("X1") { Return = "Metal1" });
        var p = g.Problem!;
        Assert.All(g.ComponentPorts!.Ports.Where(c => c.Cutout), c => Assert.StartsWith("down to 'Metal1'", c.Landing));
        Assert.Null(p.PaddingColumnM);
        Assert.Null(p.PaddedContent);
        Assert.DoesNotContain(g.Notes, n => n.Contains("port column", StringComparison.Ordinal) || n.Contains("run to the air box's sides", StringComparison.Ordinal));
        var silicon = p.Solids.Single(s => g.Origins.TryGetValue(s.Name, out var o) && o.StackupEntry == "Silicon");
        var b = Em3dProblem.Bounds(silicon.Primitive);
        var (lo, hi) = Em3dGenerator.ContentBounds(p);
        Assert.Equal((lo.X, lo.Y, hi.X, hi.Y), (b.X0, b.Y0, b.X1, b.Y1));
        var old = Em3dGenerator.PaddedAirBox(fx.Setup(new EmComponentPort("X1")), (lo.X, lo.Y, hi.X, hi.Y, lo.Z, hi.Z), 10e9, p.Boundary.Min.Z,
                                             [], [], out _, p.PaddingHeightM, p.PaddingHeightBelowM);
        Assert.Equal(old, p.Boundary with { Material = null });
    }

    // ── The field test's re-run on main ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A stated Return whose metal stops short of the face by more than a tenth of its width is refused, as a solve refuses
    /// it. It used to be moved in up to three widths (the .c3d's rule), silently: on the field test a capacitor pin's sheet slid
    /// 4 µm along its line onto a via stack of its own net. Automatic is unchanged.</summary>
    [Fact]
    public void AStatedReturnShortOfTheFace_IsRefused_NotMovedAlongTheLine()
    {
        var v = fx.Die();
        v.Shapes.Add(R(M1, 55, -20, 80, 20));                  // under B's line, 15 µm (1.5 widths) in from its face
        var cut = Problem(v, new EmComponentPort("X1") { Return = "Metal1" }).ComponentPorts!.Ports.Where(p => p.Cutout).ToList();
        foreach (var p in cut) output.WriteLine(C3dComponentPorts.Describe(p));
        Assert.StartsWith("down to 'Metal1'", cut.Single(p => p.Pin == "A").Landing);
        var b = cut.Single(p => p.Pin == "B");
        Assert.False(b.Ok);
        Assert.Matches(@"its metal stops 15(\.0+)? µm short of the port's end of the line", b.Refusal);
        Assert.StartsWith("down to the floor", Landings(v, new EmComponentPort("X1")).Single(l => l.Pin == "B").Landing);
    }

    /// <summary>Every sentence the generator makes about a cut port reaches the run under the pin's name: the possessive ones
    /// ("Port 2's return passes through an opening …", "Port 2's sheet is … inside its line's end") were dropped by a match on
    /// "Port 2 ".</summary>
    [Fact]
    public void TheGeneratorsNotesAboutACutPort_ReachTheRun_UnderItsPin()
    {
        var v = fx.Die();
        // An opening in Metal1 round B's face, metal all round it and none under it: the walk passes through it to the floor.
        v.Shapes.Add(new PolygonShape
        {
            Layer = M1, Xy = [Um(20), Um(-25), Um(60), Um(-25), Um(60), Um(25), Um(20), Um(25)],
            Holes = [[Um(30), Um(-15), Um(50), Um(-15), Um(50), Um(15), Um(30), Um(15)]],
        });
        var g = Problem(v, new EmComponentPort("X1"));
        foreach (var n in g.Notes.Where(n => n.StartsWith("X1.", StringComparison.Ordinal))) output.WriteLine(n);
        Assert.Contains(g.Notes, n => n.StartsWith("X1.B (port 2): its return passes through an opening in 'Metal1' round its line's end",
                                                   StringComparison.Ordinal));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private Em3dGenerationResult Problem(LayoutView view, EmComponentPort entry)
    {
        var g = Em3dSetupProblem.Of(fx.Setup(entry), fx.Source(view), fx.Tech);
        Assert.True(g.Ok, g.Refusal);
        return g;
    }

    private List<(string Pin, string Landing)> Landings(LayoutView view, EmComponentPort entry)
    {
        var cut = Problem(view, entry).ComponentPorts!.Ports.Where(p => p.Cutout).ToList();
        foreach (var p in cut) output.WriteLine(C3dComponentPorts.Describe(p));
        Assert.Equal(2, cut.Count);
        Assert.All(cut, p => Assert.True(p.Ok, p.Refusal));
        return [.. cut.Select(p => (p.Pin!, p.Landing!))];
    }

    private static RectShape R(LayerKey l, double x0, double y0, double x1, double y1)
        => new() { Layer = l, X1 = Um(Math.Min(x0, x1)), Y1 = Um(Math.Min(y0, y1)), X2 = Um(Math.Max(x0, x1)), Y2 = Um(Math.Max(y0, y1)) };

    private static string RepoRoot()
    {
        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "circuitRF.slnx"))) repo = Path.GetDirectoryName(repo)!;
        return repo;
    }

    public sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("icf6-").FullName;
        public Technology Tech { get; }
        private int _n;

        public Fixture()
        {
            Tech = ShippedTechnologies.Load("rfic-Si_3LM_TFR_100um");
            Tech.Stackup.Layers.Single(l => l.Name == "Silicon").ThicknessDbu = Um(750);
            TechPersistence.SaveToFile(Path.Combine(_root, "tech.ctech"), Tech);
            WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
            // A 40 × 20 µm cell, pin A on its left face and B on its right, 10 µm wide: on Metal3 (Q3) and on Metal2 (Q2).
            foreach (var (name, layer) in new[] { ("Q3", M3), ("Q2", M2) })
                Cell(name, v =>
                {
                    v.Pins.Add(new LayoutPin { Name = "A", X = 0, Y = 0, WidthDbu = Um(10), OutwardDeg = 180, Layer = layer });
                    v.Pins.Add(new LayoutPin { Name = "B", X = Um(40), Y = 0, WidthDbu = Um(10), OutwardDeg = 0, Layer = layer });
                    v.Shapes.Add(R(layer, 0, -10, 40, 10));
                });
        }

        private void Cell(string name, Action<LayoutView> draw)
        {
            string dir = CellFolder.CreateCellFolder(_root, name);
            var v = new LayoutView { DbuPerMicron = Dbu };
            draw(v);
            CellCreate.WriteLayoutView(dir, name, v);
        }

        private static LayoutView Line(string cell, LayerKey layer)
        {
            var v = new LayoutView { DbuPerMicron = Dbu };
            v.Shapes.Add(R(layer, -100, -5, 0, 5));
            v.Shapes.Add(R(layer, 40, -5, 140, 5));
            v.Instances.Add(new LayoutInstance { CellRef = "../../" + cell, X = 0, Y = 0, Mag = 1.0, SchematicId = "X1" });
            return v;
        }

        /// <summary>The field test's shape: a Metal3 line through Q3, local Metal1 under A's face only (both, when asked).</summary>
        public LayoutView Die(bool metal1UnderBoth = false)
        {
            var v = Line("Q3", M3);
            v.Shapes.Add(R(M1, -20, -20, 10, 20));
            if (metal1UnderBoth) v.Shapes.Add(R(M1, 30, -20, 60, 20));
            return v;
        }

        /// <summary>A Metal2 line through Q2, with a Metal3 plate over and a Metal1 plate under each face.</summary>
        public LayoutView Stacked()
        {
            var v = Line("Q2", M2);
            foreach (var layer in new[] { M1, M3 })
            {
                v.Shapes.Add(R(layer, -20, -20, 10, 20));
                v.Shapes.Add(R(layer, 30, -20, 60, 20));
            }
            return v;
        }

        public EmLayoutSource Source(LayoutView view)
        {
            string name = $"L{Interlocked.Increment(ref _n)}";
            string dir = CellFolder.CreateCellFolder(_root, name);
            CellCreate.WriteLayoutView(dir, name, view);
            string clay = Directory.GetFiles(dir, "*.clay", SearchOption.AllDirectories).Single();
            return new EmLayoutSource(clay, LayoutPersistence.LoadFromFile(clay), Tech, Dbu);
        }

        public EmSetup Setup(EmComponentPort entry) => new()
        {
            Name = "cut", LayoutRef = "line.clay", Solver3D = Em3dSolverId.Palace,
            Frequency = new FrequencySpec("10", "40", 4, SweepKind.Linear, "GHz", "GHz"),
            ComponentPorts = [entry],
        };

        /// <summary>A .cem beside the workspace's root naming a new layout of <paramref name="view"/>, for the CLI.</summary>
        public string Cem(string name, LayoutView view, EmComponentPort entry)
        {
            var source = Source(view);
            var setup = Setup(entry);
            setup.Name = name;
            setup.LayoutRef = Path.GetRelativePath(_root, source.AbsolutePath).Replace('\\', '/');
            string path = Path.Combine(_root, name + ".cem");
            EmSetupPersistence.SaveToFile(path, setup);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
