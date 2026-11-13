// brief-icp-3 — a cell instance cut out of a 3D problem: on a 3D .cem (built through its in-memory 3D view) and on a .c3d placing the
// layout, by ICP-2's cut, with a drawn edge port's sheet at each cut face. A 1 mm line on the shipped 20 mil PCB technology broken by a
// 2 mm two-pin cell (Seg, pins A and B), over a drawn ground plane on Bottom Copper (a 3D view solves the copper that is drawn).
// Nothing is meshed or solved: every gate reads the assembled problem.

using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Em3d;
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

public sealed class CellCutout3DTests(CellCutout3DTests.Fixture fx, ITestOutputHelper output) : IClassFixture<CellCutout3DTests.Fixture>
{
    private const int Dbu = 1000;
    private static readonly LayerKey Top = new(1, 0), Bottom = new(2, 0);
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    /// <summary>Gate 1 — the assembled problem of a cut has no metal where the instance was (Pin mode) or inside its box (Cell mode),
    /// and one lumped sheet per connected pin at the cut face, as wide as the face.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheCut_LeavesTheInstanceOut_AndPutsASheetAtEachFace(bool cellMode)
    {
        var source = fx.Source("g1" + (cellMode ? "c" : "p"), fx.Line());
        var g = Em3dSetupProblem.Of(fx.Setup([Entry("X1", cellMode)]), source, fx.Tech);
        Assert.True(g.Ok, g.Refusal);
        var p = g.Problem!;

        // Pin mode: the segment's metal is gone, the lines stay to the faces. Cell mode: the box (200 µm round the cell) is empty too.
        double a = cellMode ? 4800 : 5000, b = cellMode ? 7200 : 7000;
        Assert.False(MetalAt(p, 6000), "the instance's own metal is left out");
        Assert.True(MetalAt(p, a - 50) && MetalAt(p, b + 50), "the lines up to the cut faces stay");
        if (cellMode) Assert.False(MetalAt(p, 4900) || MetalAt(p, 7100), "everything inside the box is left out");

        var names = Names(g);
        var cut = p.Ports.Where(x => names.ContainsKey(x.Number)).OrderBy(x => x.Number).ToList();
        Assert.Equal(["X1.A", "X1.B"], cut.Select(x => names[x.Number]));
        Assert.Equal([3, 4], cut.Select(x => x.Number));
        Assert.Equal(4, p.Ports.Count);
        foreach (var (port, face) in cut.Zip(new[] { a, b }))
        {
            output.WriteLine($"{port.Number} {names[port.Number]} {port.Kind} {port.Min} {port.Max} {port.PositiveObject} {port.NegativeObject}");
            Assert.Equal(Em3dPortKind.Lumped, port.Kind);
            Assert.Equal(face * 1e-6, port.Min.X, 9);
            Assert.Equal(face * 1e-6, port.Max.X, 9);                           // upright, at the face
            Assert.Equal(1000e-6, port.Max.Y - port.Min.Y, 9);                  // as wide as the face
            Assert.Equal(508e-6, port.Max.Z - port.Min.Z, 9);                   // from the line down to its return
        }
        Assert.All(g.ComponentPorts!.Ports, c => Assert.True(c.Cutout && c.Ok));
        Assert.Null(g.CoSimPorts);                                               // no part across a cut port until ICP-5
    }

    /// <summary>Gate 2 — a .c3d placing the layout (New 3D View's document) and the 3D .cem on it assemble the same solids and sheets
    /// for the same entry; and in a view placing it twice, the entries start with the layout instance's name and each placement's
    /// sheets go where it is placed.</summary>
    [Fact]
    public void ACemAndAThreeDView_CutTheSame_AndEntriesNameTheLayoutInstance()
    {
        var source = fx.Source("g2", fx.Line());
        var setup = fx.Setup([Entry("X1", false)]);
        var cem = Em3dSetupProblem.Of(setup, source, fx.Tech);
        Assert.True(cem.Ok, cem.Refusal);

        string cellDir = Path.GetDirectoryName(Path.GetDirectoryName(source.AbsolutePath))!;
        string c3d = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.ThreeD), Path.GetFileName(cellDir) + ".c3d");
        var made = C3dHierarchy.NewFromLayout(cellDir, c3d, fx.Cws);
        Assert.NotNull(made.Document);
        var view = C3dProblemAssembly.Assemble(setup, made.Document!, c3d, fx.Cws, fromCem: true);
        Assert.True(view.Ok, view.Refusal);
        Assert.Equal(Ports(cem.Problem!), Ports(view.Problem!));
        Assert.Equal(Solids(cem.Problem!), Solids(view.Problem!));

        // Two placements, the second 20 mm up: "U1/X1" and "U2/X1" name one instance in each.
        var second = C3dPersistence.Deserialize(C3dPersistence.Serialize(made.Document!));
        var u2 = C3dPersistence.Deserialize(C3dPersistence.Serialize(second)).Instances[0];
        u2.Name = "U2";
        u2.Placement = new C3dPlacement { Origin = new C3dPoint3(0, Um(20000), 0) };
        second.Instances.Add(u2);
        var both = C3dProblemAssembly.Assemble(fx.Setup([Entry("U1/X1", false), Entry("U2/X1", false)]), second, c3d, fx.Cws, fromCem: true);
        Assert.True(both.Ok, both.Refusal);
        var bothNames = Names(both);
        var sheets = both.Problem!.Ports.Where(x => bothNames.ContainsKey(x.Number)).ToDictionary(x => bothNames[x.Number]);
        Assert.Equal(["U1/X1.A", "U1/X1.B", "U2/X1.A", "U2/X1.B"], sheets.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(sheets["U1/X1.A"].Min.Y + 20e-3, sheets["U2/X1.A"].Min.Y, 9);
        Assert.Equal(sheets["U1/X1.A"].Min.X, sheets["U2/X1.A"].Min.X, 9);
    }

    /// <summary>Gate 3 — an R-icp2-3 refusal reads identically from a Planar and a 3D setup: the parent's metal overlapping the cell
    /// beyond pin A.</summary>
    [Fact]
    public void ARefusal_ReadsTheSame_OnPlanarAndOn3D()
    {
        var line = fx.Line();
        line.Shapes.Add(R(Top, 5000, -200, 5300, 200));
        var source = fx.Source("g3", line);
        var planar = fx.Setup([Entry("X1", false)]);
        planar.Solver3D = Em3dSolverChoice.None;
        planar.AnalysisKind = EmAnalysisKind.Planar;
        var built = EmPlanarComponentPorts.Build(planar, source)!;
        string sentence = Assert.Single(built.Warnings, w => w.Contains("X1.A", StringComparison.Ordinal));
        output.WriteLine(sentence);
        Assert.Contains("Cell mode", sentence, StringComparison.Ordinal);

        var g = Em3dSetupProblem.Of(fx.Setup([Entry("X1", false)]), source, fx.Tech);
        Assert.True(g.Ok, g.Refusal);
        Assert.Contains(sentence, g.ComponentPorts!.Warnings);
        Assert.Equal(["X1.B"], Names(g).Values);
        Assert.Equal(3, g.Problem!.Ports.Count);
    }

    /// <summary>Gate 4 — a 3D view with no cut assembles byte-identically, before and after a cut setup was assembled on the same
    /// elaborator (the cut lowers its layout into a cache entry of its own).</summary>
    [Fact]
    public void AViewWithNoCut_AssemblesByteIdentically()
    {
        var source = fx.Source("g4", fx.Line());
        string cellDir = Path.GetDirectoryName(Path.GetDirectoryName(source.AbsolutePath))!;
        string c3d = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.ThreeD), Path.GetFileName(cellDir) + ".c3d");
        var doc = C3dHierarchy.NewFromLayout(cellDir, c3d, fx.Cws).Document!;
        var plain = fx.Setup([]);
        string Geo(Em3dGenerationResult r) => GmshGeoWriter.Write(r.Problem!, PalaceSettings.Resolve(plain.Palace)).Geo!;

        var fresh = C3dProblemAssembly.Assemble(plain, doc, c3d, fx.Cws, new C3dElaborator(), fromCem: true);
        var elaborator = new C3dElaborator();
        var before = C3dProblemAssembly.Assemble(plain, doc, c3d, fx.Cws, elaborator, fromCem: true);
        Assert.True(C3dProblemAssembly.Assemble(fx.Setup([Entry("X1", true)]), doc, c3d, fx.Cws, elaborator, fromCem: true).Ok);
        var after = C3dProblemAssembly.Assemble(plain, doc, c3d, fx.Cws, elaborator, fromCem: true);
        Assert.True(fresh.Ok, fresh.Refusal);
        Assert.Equal(Geo(fresh), Geo(before));
        Assert.Equal(Geo(before), Geo(after));
        Assert.Equal(Ports(before.Problem!), Ports(after.Problem!));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The built component ports' names by number: what the Touchstone and the record name each port.</summary>
    private static Dictionary<int, string> Names(Em3dGenerationResult g)
        => g.ComponentPorts!.Ports.Where(c => c.Ok).ToDictionary(c => c.Port!.Number, c => c.Port!.Name);

    private static string Ports(Em3dProblem p) => string.Join("\n", p.Ports.Select(x => $"{x.Number} {x.Name} {x.Kind} {x.Min} {x.Max} {x.Z0}"));

    private static string Solids(Em3dProblem p)
        => string.Join("\n", p.Solids.Select(s => s.Primitive is Em3dExtrudedPolygon ep
            ? $"{s.Role} {ep.ZBottom:R} {ep.ZTop:R} {string.Join(";", ep.Outline.Select(q => $"{q.X:R},{q.Y:R}"))}"
            : $"{s.Role} {s.Primitive.GetType().Name}").Order(StringComparer.Ordinal));

    /// <summary>Whether a conductor on Top Copper holds the line's centre at x (µm).</summary>
    private static bool MetalAt(Em3dProblem p, double xUm)
        => p.Solids.Any(s => s.Role == Em3dRole.Conductor && s.Primitive is Em3dExtrudedPolygon ep && ep.ZBottom > 100e-6
                             && C3dThermal.Inside(ep, new Point3(xUm * 1e-6, 0, (ep.ZBottom + ep.ZTop) / 2), 0));

    private static EmComponentPort Entry(string path, bool cellMode) => new(path)
    {
        PortAt = cellMode ? EmComponentPortAt.Cell : EmComponentPortAt.Pin,
        KeepOutUm = cellMode ? new EmKeepOut(-200, -1500, 2200, 1500) : null,
    };

    private static RectShape R(LayerKey l, double x0, double y0, double x1, double y1)
        => new() { Layer = l, X1 = Um(x0), Y1 = Um(y0), X2 = Um(x1), Y2 = Um(y1) };

    private static void Pin(LayoutView v, string name, double x, double y, double outward, double width)
        => v.Pins.Add(new LayoutPin { Name = name, X = Um(x), Y = Um(y), WidthDbu = Um(width), OutwardDeg = outward, Layer = Top });

    private static LabelShape Edge(double x, LayoutRotation dir, string text) => new()
    {
        Layer = Top, PortLayer = Top, X = Um(x), Y = 0, Text = text, Height = Um(200), IsPort = true, PortKind = PlanarPortKind.Edge,
        PortDirection = dir,
    };

    public sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("icp3-").FullName;
        public Technology Tech { get; } = ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz");
        public string Cws => Path.Combine(_root, ".cws");
        private int _n;

        public Fixture()
        {
            TechPersistence.SaveToFile(Path.Combine(_root, "tech.ctech"), Tech);
            WorkspacePersistence.SaveToFile(Cws, new CwsFile { DefaultTechRef = "tech.ctech" });
            // Seg: a 2 mm × 1 mm series element on Top Copper, pins A (left face) and B (right face).
            string dir = CellFolder.CreateCellFolder(_root, "Seg");
            var seg = new LayoutView { DbuPerMicron = Dbu };
            Pin(seg, "A", 0, 0, 180, 1000);
            Pin(seg, "B", 2000, 0, 0, 1000);
            seg.Shapes.Add(R(Top, 0, -500, 2000, 500));
            CellCreate.WriteLayoutView(dir, "Seg", seg);
        }

        /// <summary>A 12 mm line of 1 mm, Seg bridging 5 to 7 mm as X1, edge ports at the far ends, a ground plane under all of it.</summary>
        public LayoutView Line()
        {
            var v = new LayoutView { DbuPerMicron = Dbu };
            v.Shapes.Add(R(Top, 0, -500, 5000, 500));
            v.Shapes.Add(R(Top, 7000, -500, 12000, 500));
            v.Shapes.Add(R(Bottom, 0, -3000, 12000, 3000));
            v.Shapes.Add(Edge(0, LayoutRotation.R0, "1"));
            v.Shapes.Add(Edge(12000, LayoutRotation.R180, "2"));
            v.Instances.Add(new LayoutInstance { CellRef = "../../Seg", X = Um(5000), Y = 0, Mag = 1.0, SchematicId = "X1" });
            return v;
        }

        public EmLayoutSource Source(string cell, LayoutView view)
        {
            string name = $"{cell}{Interlocked.Increment(ref _n)}";
            string dir = CellFolder.CreateCellFolder(_root, name);
            CellCreate.WriteLayoutView(dir, name, view);
            string clay = Directory.GetFiles(dir, "*.clay", SearchOption.AllDirectories).Single();
            return new EmLayoutSource(clay, LayoutPersistence.LoadFromFile(clay), Tech, Dbu);
        }

        public EmSetup Setup(IReadOnlyList<EmComponentPort> entries) => new()
        {
            Name = "cut", LayoutRef = "line.clay", Solver3D = Em3dSolverId.Palace,
            Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
            ComponentPorts = [.. entries],
        };

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
