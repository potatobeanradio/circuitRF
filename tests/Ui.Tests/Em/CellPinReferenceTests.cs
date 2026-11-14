// brief-icp-4 — each cut pin's reference chosen per pin, and pins tied to ground. Planar fixtures on the shipped MMIC technology
// (ICP-2's line and Seg, a two-pin Gs cell whose pins sit side by side on one face, a three-pin Tri cell); 3D fixtures on the
// shipped 20 mil PCB technology with a drawn ground plane (ICP-3's), no solve. At most one frequency per Planar solve.

using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public sealed class CellPinReferenceTests(CellPinReferenceTests.Fixture fx, ITestOutputHelper output) : IClassFixture<CellPinReferenceTests.Fixture>
{
    private const int Dbu = 1000;
    private static readonly LayerKey M1 = new(1, 0), BacksideVia = new(8, 0);
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    // ── Gate 1: on Planar, facing faces build LCP's strip and delta gap, equal to the same strip and gap drawn by hand ─────

    [Fact]
    public void FacingFaces_BuildAStripAndGap_EqualToTheHandDrawnOnes()
    {
        var built = fx.Run("ab-built", fx.SegLine(uncalibratedEnds: true), [new EmComponentPort("X1", Reference: "B")]);
        var twin = fx.Run("ab-twin", SegGapTwin(), []);
        Assert.True(built.Status == EmRunStatus.Ok, built.Error);
        Assert.True(twin.Status == EmRunStatus.Ok, twin.Error);
        var (a, n) = S(built);
        var (b, m) = S(twin);
        Assert.Equal(3, n);
        Assert.Equal(n, m);
        double worst = a.Zip(b, (x, y) => (x - y).Magnitude).Max();
        output.WriteLine($"max |ΔS| built vs hand-drawn strip and gap = {worst:E2}");
        Assert.True(worst < 1e-9, $"max |ΔS| {worst}");
        string snp = File.ReadAllText(built.SnpPath!);
        Assert.Contains("! Port[3] = X1.A", snp, StringComparison.Ordinal);
        Assert.DoesNotContain("X1.B", snp, StringComparison.Ordinal);
    }

    // ── Gate 2: faces that do not face each other are left out; with no Reference they build ───────────────────────────────

    [Fact]
    public void FacesThatDoNotFace_AreLeftOut_AndWithoutAReferenceTheyBuild()
    {
        var referenced = fx.Build(fx.GsLine(withCell: true), [new EmComponentPort("X1", Reference: "S")]);
        var g = Assert.Single(referenced.Ports, p => p.Name == "X1.G");
        Assert.False(g.Ok);
        output.WriteLine(g.Refusal);
        Assert.Contains("do not face each other across one axis on one layer", g.Refusal, StringComparison.Ordinal);
        Assert.Contains("With Reference Ground it builds", g.Refusal, StringComparison.Ordinal);

        var plain = fx.Build(fx.GsLine(withCell: true), [new EmComponentPort("X1")]);
        Assert.Equal(["X1.G", "X1.S"], plain.Ports.Where(p => p.Ok).Select(p => p.Name));
    }

    // ── Gate 3: 3D — facing faces make one gap sheet; faces that do not face are left out ──────────────────────────────────

    [Fact]
    public void In3D_FacingFacesMakeOneGapSheet_AndOthersAreLeftOut()
    {
        var g = Em3dSetupProblem.Of(fx.Setup3D([new EmComponentPort("X1", Reference: "B")]), fx.Source3D("facing", fx.PcbLine("Seg3")), fx.Pcb);
        Assert.True(g.Ok, g.Refusal);
        var cut = Assert.Single(g.ComponentPorts!.Ports, p => p.Cutout);
        Assert.True(cut.Ok, cut.Refusal);
        output.WriteLine(C3dComponentPorts.Describe(cut));
        Assert.Equal("B", cut.ReferencePin);
        var sheet = g.Problem!.Ports.Single(p => p.Number == cut.Port!.Number);
        Assert.Equal(sheet.Min.Z, sheet.Max.Z, 12);                             // flat, on the copper
        Assert.Equal(5e-3, sheet.Min.X, 9);
        Assert.Equal(7e-3, sheet.Max.X, 9);                                      // from A's face to B's, across the vacated cell
        Assert.Equal(1e-3, sheet.Max.Y - sheet.Min.Y, 9);
        Assert.Equal(3, g.Problem.Ports.Count);                                  // the two drawn ports and the one sheet; none at B

        var ell = Em3dSetupProblem.Of(fx.Setup3D([new EmComponentPort("X1", Reference: "B")]), fx.Source3D("ell", fx.PcbEll()), fx.Pcb);
        var left = Assert.Single(ell.ComponentPorts!.Ports, p => p.Cutout && p.Pin == "A");
        Assert.False(left.Ok);
        output.WriteLine(left.Refusal);
        Assert.Contains("do not face each other across one axis on one layer", left.Refusal, StringComparison.Ordinal);
    }

    // ── Gate 5: a Pins map; every pin on ground; a pin both referenced and ported ──────────────────────────────────────────

    [Fact]
    public void APinsMap_BuildsItsPorts_AndAPinBothReturnAndPortIsRefused()
    {
        var pins = new EmComponentPort("X1").WithPins(new Dictionary<string, EmPinOptions>
        {
            ["B"] = new(Reference: "E"), ["C"] = new(Reference: "E"), ["E"] = new(Port: false),
        });
        // The .cem carries both keys and reads them back as written; an entry naming neither writes neither.
        var saved = fx.Setup("rt", [pins, new EmComponentPort("X2") { Ground = "E, SUB" }, new EmComponentPort("X3")]);
        string json = EmSetupPersistence.Serialize(saved);
        Assert.Equal(saved.ComponentPorts, EmSetupPersistence.Deserialize(json).ComponentPorts);
        Assert.Contains("\"Pins\"", json, StringComparison.Ordinal);
        Assert.Equal(json, EmSetupPersistence.Serialize(EmSetupPersistence.Deserialize(json)));

        var two = fx.Build(fx.TriLine(), [pins]);
        Assert.Equal(["X1.B", "X1.C"], two.Ports.Where(p => p.Ok).Select(p => p.Name));
        Assert.All(two.Ports.Where(p => p.Ok), p => Assert.Equal("E", p.ReferencePin));
        Assert.Contains(two.Notes, n => n.StartsWith("X1.E is the return of X1.B and X1.C", StringComparison.Ordinal));

        var ground = fx.Build(fx.TriLine(), [new EmComponentPort("X1", Reference: "Ground")]);
        Assert.Equal(["X1.B", "X1.C", "X1.E"], ground.Ports.Where(p => p.Ok).Select(p => p.Name));
        Assert.All(ground.Ports.Where(p => p.Ok), p => Assert.Null(p.ReferencePin));

        var both = new EmComponentPort("X1").WithPins(new Dictionary<string, EmPinOptions> { ["B"] = new(Reference: "E"), ["E"] = new(Port: true) });
        var refused = Assert.Single(fx.Build(fx.TriLine(), [both]).Ports, p => !p.Ok);
        output.WriteLine(refused.Refusal);
        Assert.Contains("X1.E is the Reference of X1.B and is also given a port of its own", refused.Refusal, StringComparison.Ordinal);
    }

    // ── Gate 6: a grounded pin builds no port; its via is in the problem; S equals the via drawn by hand ───────────────────

    [Fact]
    public void AGroundedPin_CarriesAGroundVia_EqualToTheHandDrawnOne()
    {
        var entry = new EmComponentPort("X1") { Ground = "B" };
        var view = fx.SegLine(uncalibratedEnds: true);
        var source = fx.Source("gnd-geo", view);
        var geometry = EmGeometry.ForSetup(fx.Setup("gnd-geo", [entry]), source);
        var via = Assert.Single(geometry.Shapes.OfType<RectShape>(), r => r.Layer == BacksideVia);
        Assert.Equal((Um(700), Um(-36), Um(772), Um(36)), (via.X1, via.Y1, via.X2, via.Y2));

        var built = fx.Run("gnd-built", fx.SegLine(uncalibratedEnds: true), [entry]);
        var twin = fx.Run("gnd-twin", SegGroundTwin(), []);
        Assert.True(built.Status == EmRunStatus.Ok, built.Error);
        Assert.True(twin.Status == EmRunStatus.Ok, twin.Error);
        var (a, n) = S(built);
        var (b, m) = S(twin);
        Assert.Equal(3, n);
        Assert.Equal(n, m);
        double worst = a.Zip(b, (x, y) => (x - y).Magnitude).Max();
        output.WriteLine($"max |ΔS| built vs hand-drawn ground via = {worst:E2}");
        Assert.True(worst < 1e-9, $"max |ΔS| {worst}");
        Assert.DoesNotContain("X1.B", File.ReadAllText(built.SnpPath!), StringComparison.Ordinal);
    }

    // ── Gate 7: Common has no construction, and says so ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Common_IsRefusedWithItsSentence()
    {
        var refused = Assert.Single(fx.Build(fx.TriLine(), [new EmComponentPort("X1", Reference: "Common")]).Ports);
        output.WriteLine(refused.Refusal);
        Assert.False(refused.Ok);
        Assert.StartsWith("X1: Reference Common (one node shared by its ports) has no construction", refused.Refusal, StringComparison.Ordinal);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static RectShape R(LayerKey l, double x0, double y0, double x1, double y1)
        => new() { Layer = l, X1 = Um(Math.Min(x0, x1)), Y1 = Um(Math.Min(y0, y1)), X2 = Um(Math.Max(x0, x1)), Y2 = Um(Math.Max(y0, y1)) };

    private static LabelShape Edge(double x, double y, LayoutRotation dir, string text, bool uncalibrated, LayerKey? layer = null) => new()
    {
        Layer = layer ?? M1, PortLayer = layer ?? M1, X = Um(x), Y = Um(y), Text = text, Height = Um(20), IsPort = true,
        PortKind = PlanarPortKind.Edge, PortDirection = dir, PortUseEdgeWidth = uncalibrated ? true : null, PortUncalibrated = uncalibrated,
    };

    /// <summary>ICP-2's line with Seg deleted and, drawn by hand, a strip across the 200 µm it left with a delta gap at its middle
    /// pointing at A (the positive end), as LCP's construction draws one.</summary>
    private static LayoutView SegGapTwin()
    {
        var v = new LayoutView { DbuPerMicron = Dbu };
        v.Shapes.Add(R(M1, 0, -36, 500, 36));
        v.Shapes.Add(R(M1, 700, -36, 1200, 36));
        v.Shapes.Add(Edge(0, 0, LayoutRotation.R0, "1", true));
        v.Shapes.Add(Edge(1200, 0, LayoutRotation.R180, "2", true));
        v.Shapes.Add(R(M1, 500, -36, 700, 36));
        v.Shapes.Add(new LabelShape
        {
            Layer = M1, PortLayer = M1, X = Um(600), Y = 0, Text = "3", Height = Um(36), IsPort = true,
            PortKind = PlanarPortKind.InternalDeltaGap, PortDirection = LayoutRotation.R180,
        });
        return v;
    }

    /// <summary>ICP-2's line with Seg deleted, an Uncalibrated port at A's face and the ground via drawn by hand at B's.</summary>
    private static LayoutView SegGroundTwin()
    {
        var v = new LayoutView { DbuPerMicron = Dbu };
        v.Shapes.Add(R(M1, 0, -36, 500, 36));
        v.Shapes.Add(R(M1, 700, -36, 1200, 36));
        v.Shapes.Add(Edge(0, 0, LayoutRotation.R0, "1", true));
        v.Shapes.Add(Edge(1200, 0, LayoutRotation.R180, "2", true));
        v.Shapes.Add(Edge(500, 0, LayoutRotation.R180, "3", true));
        v.Shapes.Add(R(BacksideVia, 700, -36, 772, 36));
        return v;
    }

    private static (Complex[] S, int N) S(EmRunResult r)
    {
        var data = r.Data!;
        var cube = data.CubesIn(data.Groups.First(x => data.CubesIn(x).ContainsKey("S")))["S"];
        return (cube.ComplexValues, cube.Axes[1].Values.Length);
    }

    public sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("icp4-").FullName;
        private string Planar => Path.Combine(_root, "planar");
        private string ThreeD => Path.Combine(_root, "pcb");
        public Technology Tech { get; } = ShippedTechnologies.Load("mmic-GaAs_2LM_100um");
        public Technology Pcb { get; } = ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz");
        private int _n;

        public Fixture()
        {
            Directory.CreateDirectory(Planar);
            Directory.CreateDirectory(ThreeD);
            TechPersistence.SaveToFile(Path.Combine(Planar, "tech.ctech"), Tech);
            WorkspacePersistence.SaveToFile(Path.Combine(Planar, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
            TechPersistence.SaveToFile(Path.Combine(ThreeD, "tech.ctech"), Pcb);
            WorkspacePersistence.SaveToFile(Path.Combine(ThreeD, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
            // ICP-2's Seg: 200 × 72 µm, pins A (left face) and B (right face).
            Cell(Planar, "Seg", c => { Pin(c, "A", 0, 0, 180, 72, M1); Pin(c, "B", 200, 0, 0, 72, M1); c.Shapes.Add(R(M1, 0, -36, 200, 36)); });
            // Gs: G and S side by side on its left face (one station), 40 µm each, 80 µm apart.
            Cell(Planar, "Gs", c =>
            {
                Pin(c, "G", 0, 60, 180, 40, M1); Pin(c, "S", 0, -60, 180, 40, M1);
                c.Shapes.Add(R(M1, 0, 40, 80, 80)); c.Shapes.Add(R(M1, 0, -80, 80, -40));
            });
            // Tri: B and C on its left face, E across from both on its right face (200 µm away, 160 µm wide).
            Cell(Planar, "Tri", c =>
            {
                Pin(c, "B", 0, 50, 180, 40, M1); Pin(c, "C", 0, -50, 180, 40, M1); Pin(c, "E", 200, 0, 0, 160, M1);
                c.Shapes.Add(R(M1, 0, -80, 200, 80));
            });
            // 3D: Seg3 (2 mm × 1 mm, A and B facing across it) and Ell (A on the left face, B on the bottom one).
            var top = new LayerKey(1, 0);
            Cell(ThreeD, "Seg3", c => { Pin(c, "A", 0, 0, 180, 1000, top); Pin(c, "B", 2000, 0, 0, 1000, top); c.Shapes.Add(R(top, 0, -500, 2000, 500)); });
            Cell(ThreeD, "Ell", c => { Pin(c, "A", 0, 0, 180, 1000, top); Pin(c, "B", 1000, -500, 270, 1000, top); c.Shapes.Add(R(top, 0, -500, 2000, 500)); });
        }

        private static void Pin(LayoutView v, string name, double x, double y, double outward, double width, LayerKey layer)
            => v.Pins.Add(new LayoutPin { Name = name, X = Um(x), Y = Um(y), WidthDbu = Um(width), OutwardDeg = outward, Layer = layer });

        private static void Cell(string ws, string name, Action<LayoutView> draw)
        {
            string dir = CellFolder.CreateCellFolder(ws, name);
            var v = new LayoutView { DbuPerMicron = Dbu };
            draw(v);
            CellCreate.WriteLayoutView(dir, name, v);
        }

        private static LayoutInstance Place(string cell, double x, double y, string id)
            => new() { CellRef = "../../" + cell, X = Um(x), Y = Um(y), Mag = 1.0, SchematicId = id };

        /// <summary>ICP-2's line: 1200 µm of 72 µm, Seg bridging 500-700 µm as X1, edge ports at the far ends.</summary>
        public LayoutView SegLine(bool uncalibratedEnds = false)
        {
            var v = new LayoutView { DbuPerMicron = Dbu };
            v.Shapes.Add(R(M1, 0, -36, 500, 36));
            v.Shapes.Add(R(M1, 700, -36, 1200, 36));
            v.Shapes.Add(Edge(0, 0, LayoutRotation.R0, "1", uncalibratedEnds));
            v.Shapes.Add(Edge(1200, 0, LayoutRotation.R180, "2", uncalibratedEnds));
            v.Instances.Add(Place("Seg", 500, 0, "X1"));
            return v;
        }

        /// <summary>Two 40 µm lines, 400 µm long, meeting Gs's G and S at x = 0; Uncalibrated ports at their far ends.</summary>
        public LayoutView GsLine(bool withCell)
        {
            var v = new LayoutView { DbuPerMicron = Dbu };
            v.Shapes.Add(R(M1, -400, 40, 0, 80));
            v.Shapes.Add(R(M1, -400, -80, 0, -40));
            v.Shapes.Add(Edge(-400, 60, LayoutRotation.R0, "1", true));
            v.Shapes.Add(Edge(-400, -60, LayoutRotation.R0, "2", true));
            if (withCell) v.Instances.Add(Place("Gs", 0, 0, "X1"));
            return v;
        }

        /// <summary>Two 40 µm lines from the left meeting Tri's B and C, a 160 µm line from the right meeting E; ports at the far ends.</summary>
        public LayoutView TriLine()
        {
            var v = new LayoutView { DbuPerMicron = Dbu };
            v.Shapes.Add(R(M1, -400, 30, 0, 70));
            v.Shapes.Add(R(M1, -400, -70, 0, -30));
            v.Shapes.Add(R(M1, 200, -80, 600, 80));
            v.Shapes.Add(Edge(-400, 50, LayoutRotation.R0, "1", true));
            v.Shapes.Add(Edge(-400, -50, LayoutRotation.R0, "2", true));
            v.Shapes.Add(Edge(600, 0, LayoutRotation.R180, "3", true));
            v.Instances.Add(Place("Tri", 0, 0, "X1"));
            return v;
        }

        /// <summary>ICP-3's 3D line: 1 mm wide, the cell bridging 5-7 mm as X1, a ground plane under it.</summary>
        public LayoutView PcbLine(string cell)
        {
            var top = new LayerKey(1, 0); var bottom = new LayerKey(2, 0);
            var v = new LayoutView { DbuPerMicron = Dbu };
            v.Shapes.Add(R(top, 0, -500, 5000, 500));
            v.Shapes.Add(R(top, 7000, -500, 12000, 500));
            v.Shapes.Add(R(bottom, 0, -3000, 12000, 3000));
            v.Shapes.Add(Edge(0, 0, LayoutRotation.R0, "1", false, top));
            v.Shapes.Add(Edge(12000, 0, LayoutRotation.R180, "2", false, top));
            v.Instances.Add(Place(cell, 5000, 0, "X1"));
            return v;
        }

        /// <summary>Ell placed at 5 mm: a line from the left meets A, a line from below meets B.</summary>
        public LayoutView PcbEll()
        {
            var top = new LayerKey(1, 0); var bottom = new LayerKey(2, 0);
            var v = new LayoutView { DbuPerMicron = Dbu };
            v.Shapes.Add(R(top, 0, -500, 5000, 500));
            v.Shapes.Add(R(top, 5500, -5000, 6500, -500));
            v.Shapes.Add(R(bottom, 0, -6000, 9000, 3000));
            v.Shapes.Add(Edge(0, 0, LayoutRotation.R0, "1", false, top));
            var down = Edge(6000, 0, LayoutRotation.R90, "2", false, top);
            down.Y = Um(-5000);
            v.Shapes.Add(down);
            v.Instances.Add(Place("Ell", 5000, 0, "X1"));
            return v;
        }

        private EmLayoutSource SourceIn(string ws, string cell, LayoutView view, Technology tech)
        {
            string name = $"{cell}{Interlocked.Increment(ref _n)}";
            string dir = CellFolder.CreateCellFolder(ws, name);
            CellCreate.WriteLayoutView(dir, name, view);
            string clay = Directory.GetFiles(dir, "*.clay", SearchOption.AllDirectories).Single();
            return new EmLayoutSource(clay, LayoutPersistence.LoadFromFile(clay), tech, Dbu);
        }

        public EmLayoutSource Source(string cell, LayoutView view) => SourceIn(Planar, cell, view, Tech);
        public EmLayoutSource Source3D(string cell, LayoutView view) => SourceIn(ThreeD, cell, view, Pcb);

        public EmSetup Setup(string name, IReadOnlyList<EmComponentPort> entries) => new()
        {
            Name = name, LayoutRef = name + ".clay", AnalysisKind = EmAnalysisKind.Planar,
            PlanarMesh = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 10, EdgeMesh: false),
            Frequency = new FrequencySpec("10", "10", 1, SweepKind.Linear, "GHz", "GHz"),
            AdaptiveSampling = false,
            ComponentPorts = [.. entries],
        };

        public EmSetup Setup3D(IReadOnlyList<EmComponentPort> entries) => new()
        {
            Name = "cut", LayoutRef = "line.clay", Solver3D = Em3dSolverId.Palace,
            Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
            ComponentPorts = [.. entries],
        };

        public EmRunResult Run(string name, LayoutView view, IReadOnlyList<EmComponentPort> entries)
            => EmRunService.Run(Setup(name, entries), Source(name, view), Path.Combine(_root, "results-" + name));

        public EmPlanarComponentPortBuild Build(LayoutView view, IReadOnlyList<EmComponentPort> entries)
            => EmPlanarComponentPorts.Build(Setup("b", entries), Source("b", view))!;

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
