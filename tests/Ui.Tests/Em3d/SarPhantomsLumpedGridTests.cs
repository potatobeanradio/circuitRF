// ================================================================
//  SarPhantomsLumpedGridTests.cs — the gates of brief-sar-3: the ellipsoid, the layered phantom, the lumped RLC element,
//  explicit grid lines, and a setup without SAR writing exactly what it wrote before. Writer output only: nothing here
//  runs openEMS. The two that compare against SAR-1's reference XMLs read them from testdata/sar/openems-ref, which is
//  kept on the developer machine only; there they use the file, elsewhere the values transcribed from it.
// ================================================================

using System.Globalization;
using System.Xml.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

/// <summary>Shared fixtures: a workspace whose technology holds copper and the openEMS head phantom's three tissues, and a
/// small dipole (two copper arms along z, a lumped port in the gap) for a phantom to sit beside.</summary>
internal static class SarFixtures
{
    public const long Mm = 1_000_000;          // DBU at 1000 DBU per µm

    public static readonly string[] HeadTissues =
        ["openEMS head phantom skin (298 MHz)", "openEMS head phantom skull (298 MHz)", "openEMS head phantom brain (298 MHz)"];

    public static (string Path, string Cws) Workspace(string root)
    {
        string ws = Path.Combine(root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials =
            [
                new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 },
                new TechMaterial { Name = HeadTissues[0], Epsr = 38.9, Sigma20 = 0.84 },
                new TechMaterial { Name = HeadTissues[1], Epsr = 11.4, Sigma20 = 0.29 },
                new TechMaterial { Name = HeadTissues[2], Epsr = 52.7, Sigma20 = 0.95 },
            ],
        });
        string cws = Path.Combine(ws, ".cws");
        WorkspacePersistence.SaveToFile(cws, new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        return (Path.Combine(dir, "Cell.c3d"), cws);
    }

    /// <summary>Two 1 × 1 × 20 mm copper arms on z either side of a 2 mm gap at the origin, with a lumped port across it on the
    /// XZ plane.</summary>
    public static C3dDocument Dipole() => new()
    {
        Objects =
        [
            new C3dBox { Name = "armLo", Material = "Copper", Min = new(-Mm / 2, -Mm / 2, -21 * Mm), Size = new(Mm, Mm, 20 * Mm) },
            new C3dBox { Name = "armHi", Material = "Copper", Min = new(-Mm / 2, -Mm / 2, Mm), Size = new(Mm, Mm, 20 * Mm) },
        ],
        Ports = [new C3dPort { Number = 1, Name = "P1", Plane = C3dPlane.XZ, Rect = new C3dRect { Min = new(-Mm / 2, -Mm), Size = new(Mm, 2 * Mm) } }],
    };

    public static EmSetup Setup() => new()
    {
        Name = "S", Solver3D = Em3dSolverId.OpenEms,
        Frequency = new FrequencySpec("0.5", "1.5", 11, SweepKind.Linear, "GHz", "GHz"),
    };

    public static (Em3dProblem Problem, CsxcadLowering Lowering) Lower(C3dDocument doc, string path, string cws, EmSetup? setup = null)
    {
        C3dPersistence.SaveToFile(path, doc);
        setup ??= Setup();
        var g = C3dProblemAssembly.Assemble(setup, doc, path, cws, new C3dElaborator());
        Assert.True(g.Ok, g.Refusal);
        var gs = CemOpenEms.ResolveGrid(setup.OpenEms, path);
        var grid = FdtdGrid.Build(g.Problem!, gs, long.MaxValue);
        Assert.Null(grid.Refusal);
        var low = CsxcadWriter.Write(g.Problem!, grid, gs, CemOpenEms.ResolveRun(setup.OpenEms));
        Assert.True(low.Ok, low.Refusal);
        return (g.Problem!, low);
    }

    public static string? Reference(string relative)
    {
        string file = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "sar", "openems-ref", relative);
        return File.Exists(file) ? file : null;
    }

    public static double D(string? s) => double.Parse(s!, NumberStyles.Float, CultureInfo.InvariantCulture);
}

public sealed class C3dEllipsoidTests
{
    /// <summary>R-sar3-1: the kernel builds an ellipsoid turned off the world's axes as a closed solid of volume 4/3·π·abc, to
    /// the kernel's tolerance: the worker's volume is OCCT's fixed-order integration, 4.4e-4 low on this B-spline surface
    /// (measured: adaptive integration at 1e-9 gives the analytic volume to 1e-6, so the surface itself is exact).</summary>
    [KernelFact]
    public void TheKernelsEllipsoid_HasTheAnalyticVolume()
    {
        using var kernel = KernelForTests.New();
        var e = new C3dEllipsoid
        {
            Name = "egg", Centre = new C3dPoint3(10_000, -5_000, 2_000), SemiAxes = new C3dPoint3(80_000, 60_000, 30_000),
            Placement = new C3dPlacement { Rotate = [new C3dRotation { Axis = C3dAxis.Y, Deg = 30 }] },
        };
        var build = kernel.Build(GeometryKernelTree.From(e, 1000));
        Assert.True(build.Valid);
        double expected = 4.0 / 3 * Math.PI * 80 * 60 * 30;
        Assert.True(Math.Abs(build.VolumeUm3 - expected) <= 1e-3 * expected, $"{build.VolumeUm3} vs {expected}");
    }

    /// <summary>R-sar3-1: on the world's axes it is written as openEMS's own Dipole_SAR writes its ellipsoids — a unit sphere
    /// under Scale3 then Translate; turned off them, as the polyhedron of its tessellation.</summary>
    [Fact]
    public void CsxcadWritesAScaledSphere_AndAPolyhedronWhenTurned()
    {
        const double mm = 1e-3;
        Em3dProblem Problem(Em3dEllipsoid egg)
        {
            var a = Em3dBoundaryKind.Absorbing;
            var port = new Em3dPort(1, "port/1", "armHi", "armLo", new(0, -0.5 * mm, -1 * mm), new(0, 0.5 * mm, 1 * mm),
                                    new(0, 0, 1), 50, new Em3dReferencePlane(new(0, 0, 0), new(1, 0, 0), 0));
            return new Em3dProblem(
                [new("armLo", "Cu", Em3dRole.Conductor, new Em3dBox(new(-0.5 * mm, -0.5 * mm, -21 * mm), new(0.5 * mm, 0.5 * mm, -1 * mm)), 1),
                 new("armHi", "Cu", Em3dRole.Conductor, new Em3dBox(new(-0.5 * mm, -0.5 * mm, 1 * mm), new(0.5 * mm, 0.5 * mm, 21 * mm)), 2),
                 new("skin", "skin", Em3dRole.Dielectric, egg, 3)],
                [], [new Em3dMaterial("Cu", 1, null, 0, 1, 5.8e7), new Em3dMaterial("skin", 50, null, 0, 1, 0.65)], [port],
                new Em3dAirBox(new(-150 * mm, -150 * mm, -150 * mm), new(150 * mm, 150 * mm, 150 * mm), new Em3dFaces(a, a, a, a, a, a)),
                new Em3dFrequency(0.5e9, 1.5e9, 11, Em3dSweepKind.Linear), 20);
        }
        string Write(Em3dEllipsoid egg)
        {
            var p = Problem(egg);
            var grid = FdtdGrid.Build(p, OpenEmsGridSettings.Default, long.MaxValue);
            var low = CsxcadWriter.Write(p, grid, OpenEmsGridSettings.Default, OpenEmsRunSettings.Default);
            Assert.True(low.Ok, low.Refusal);
            return XDocument.Parse(low.Model!).Descendants("Material").Single(m => m.Attribute("Name")!.Value == "skin")
                            .Element("Primitives")!.Elements().Single().ToString(SaveOptions.DisableFormatting);
        }

        var aligned = Write(Em3dEllipsoid.Aligned(new(100 * mm, 0, 0), 80 * mm, 100 * mm, 100 * mm));
        Assert.Equal("<Sphere Priority=\"3\" Radius=\"1\"><Transformation><Scale3 Argument=\"0.08,0.1,0.1\" />" +
                     "<Translate Argument=\"0.1,0,0\" /></Transformation><Center X=\"0\" Y=\"0\" Z=\"0\" /></Sphere>", aligned);

        double c = Math.Cos(Math.PI / 6), s = Math.Sin(Math.PI / 6);
        var turned = Write(new Em3dEllipsoid(new(100 * mm, 0, 0), 80 * mm, 100 * mm, 60 * mm, new(c, 0, -s), new(0, 1, 0)));
        Assert.StartsWith("<Polyhedron", turned, StringComparison.Ordinal);
    }
}

public sealed class C3dPhantomTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar3-phantom-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>R-sar3-2: an Ellipsoid3 phantom is three concentric ellipsoids in its three tissues, the innermost written with
    /// the highest priority — and Flatten gives the same three solids as ordinary objects.</summary>
    [Fact]
    public void AnEllipsoid3_WritesThreeNestedSolids_InnermostFirst_AndFlattenGivesTheSame()
    {
        var (path, cws) = SarFixtures.Workspace(_root);
        var doc = SarFixtures.Dipole();
        doc.Objects.Add(C3dPhantom.New(C3dPhantomShape.Ellipsoid3, "head", new C3dPoint3(120 * SarFixtures.Mm, 0, 0), 1000));
        var (problem, low) = SarFixtures.Lower(doc, path, cws);

        var layers = problem.Solids.Where(s => s.Name.StartsWith("head/", StringComparison.Ordinal)).ToList();
        Assert.Equal(["head/skin", "head/skull", "head/brain"], layers.Select(s => s.Name));
        Assert.Equal(SarFixtures.HeadTissues, layers.Select(s => s.Material));
        Assert.Equal([0.1, 0.091, 0.082], layers.Select(s => Math.Round(((Em3dEllipsoid)s.Primitive).A, 9)));
        var xml = XDocument.Parse(low.Model!);
        int Priority(string name) => int.Parse(xml.Descendants("Material").Single(m => m.Attribute("Name")!.Value == name)
                                                  .Descendants("Sphere").Single().Attribute("Priority")!.Value, CultureInfo.InvariantCulture);
        Assert.True(Priority("head/skin") < Priority("head/skull") && Priority("head/skull") < Priority("head/brain"));

        var flat = C3dPersistence.Deserialize(C3dPersistence.Serialize(doc));
        var (made, refusal) = C3dPhantoms.Flatten(flat, flat.Objects.FindIndex(o => o is C3dPhantom));
        Assert.Null(refusal);
        Assert.Equal(["head_skin", "head_skull", "head_brain"], made.Select(o => o.Name));
        Assert.All(made, o => Assert.Equal("head", o.Group));
        var (flatProblem, _) = SarFixtures.Lower(flat, path, cws);
        var flatLayers = flatProblem.Solids.Where(s => s.Name.StartsWith("head_", StringComparison.Ordinal)).ToList();
        Assert.Equal(layers.Select(s => (s.Material, s.Role, s.Primitive, s.Order)), flatLayers.Select(s => (s.Material, s.Role, s.Primitive, s.Order)));
    }
}

public sealed class LumpedElementCsxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar3-lumped-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>R-sar3-3: a 5.4 pF element across a gap in y, its value a VAR in pF, writes the attributes MRI_Loop_Coil's
    /// <c>caps_y</c> carries (Direction, Caps, C, LEtype; no R or L) with the same values, and adds no port.</summary>
    [Fact]
    public void A5p4PfElement_WritesTheLoopCoilsLumpedElementAttributes()
    {
        var (path, cws) = SarFixtures.Workspace(_root);
        long mm = SarFixtures.Mm;
        var doc = SarFixtures.Dipole();
        doc.Variables.Add(new C3dVariable { Name = "Ctune", Expression = "5.4", Unit = "pF" });
        // A second gap, in y, between two copper strips on the plane x = 30 mm.
        doc.Objects.Add(new C3dBox { Name = "stripLo", Material = "Copper", Min = new(30 * mm, -11 * mm, 30 * mm), Size = new(mm, 10 * mm, mm) });
        doc.Objects.Add(new C3dBox { Name = "stripHi", Material = "Copper", Min = new(30 * mm, mm, 30 * mm), Size = new(mm, 10 * mm, mm) });
        doc.LumpedElements.Add(new C3dLumpedElement
        {
            Name = "caps_y", Plane = C3dPlane.YZ, Offset = 30 * mm + mm / 2,
            Rect = new C3dRect { Min = new(-mm, 30 * mm), Size = new(2 * mm, mm) }, C = "Ctune", CUnit = "pF",
        });
        var (problem, low) = SarFixtures.Lower(doc, path, cws);
        Assert.Single(problem.Ports);
        Assert.Equal([1], low.Ports);

        var ours = XDocument.Parse(low.Model!).Descendants("LumpedElement").Single(e => e.Attribute("Name")!.Value == "caps_y");
        var expected = new Dictionary<string, double> { ["Direction"] = 1, ["Caps"] = 1, ["C"] = 5.4e-12, ["LEtype"] = 0 };
        if (SarFixtures.Reference("MRI_Loop_Coil/MRI_Loop_Coil.xml") is { } file)
        {
            var theirs = XDocument.Load(file).Descendants("LumpedElement").Single(e => e.Attribute("Name")!.Value == "caps_y");
            expected = theirs.Attributes().Where(a => a.Name.LocalName is not ("ID" or "Name"))
                             .ToDictionary(a => a.Name.LocalName, a => SarFixtures.D(a.Value));
        }
        var mine = ours.Attributes().Where(a => a.Name.LocalName is not ("ID" or "Name")).ToDictionary(a => a.Name.LocalName, a => SarFixtures.D(a.Value));
        Assert.Equal(expected.Keys.Order(), mine.Keys.Order());
        foreach (var (k, v) in expected) Assert.Equal(v, mine[k], v * 1e-12);
    }
}

public sealed class ExplicitGridTests
{
    private const double mm = 1e-3;

    /// <summary>A dipole-like problem: arms on z, a port sheet in the gap on the plane y = <paramref name="portY"/>.</summary>
    private static Em3dProblem Problem(double portY, double half = 0.1)
    {
        var a = Em3dBoundaryKind.Absorbing;
        var port = new Em3dPort(1, "port/1", "armHi", "armLo", new(-0.1 * mm, portY, -1.25 * mm), new(0.1 * mm, portY, 1.25 * mm),
                                new(0, 0, 1), 50, new Em3dReferencePlane(new(0, 0, 0), new(0, 1, 0), 0));
        return new Em3dProblem(
            [new("armLo", "Cu", Em3dRole.Conductor, new Em3dBox(new(-0.1 * mm, -0.1 * mm, -30 * mm), new(0.1 * mm, 0.1 * mm, -1.25 * mm)), 1),
             new("armHi", "Cu", Em3dRole.Conductor, new Em3dBox(new(-0.1 * mm, -0.1 * mm, 1.25 * mm), new(0.1 * mm, 0.1 * mm, 30 * mm)), 2)],
            [], [new Em3dMaterial("Cu", 1, null, 0, 1, 5.8e7)], [port],
            new Em3dAirBox(new(-half, -half, -half), new(half, half, half), new Em3dFaces(a, a, a, a, a, a)),
            new Em3dFrequency(0.5e9, 1.5e9, 11, Em3dSweepKind.Linear), 20);
    }

    /// <summary>R-sar3-5: OpenEms.GridLines naming SAR-1's Dipole_SAR XML gives exactly its RectilinearGrid — every line, scaled by
    /// its DeltaUnit, written back unchanged — and FdtdGrid places none of its own. Where that file is absent, a CSXCAD file of
    /// the same form written here stands in.</summary>
    [Fact]
    public void TheDipoleXmlsGrid_IsReproducedExactly()
    {
        string file = SarFixtures.Reference("Dipole_SAR/sar_dipole.xml") ?? StandIn();
        var section = new CemOpenEms { GridLines = new CemGridLines { File = file } };
        var settings = CemOpenEms.ResolveGrid(section);
        Assert.Empty(settings.Problems());

        var source = XDocument.Load(file).Descendants("RectilinearGrid").Single();
        double delta = SarFixtures.D(source.Attribute("DeltaUnit")!.Value);
        var problem = Problem(0, half: 0.1);
        var grid = FdtdGrid.Build(problem, settings, long.MaxValue);
        Assert.Null(grid.Refusal);
        foreach (var (tag, axis) in new[] { ("XLines", grid.X), ("YLines", grid.Y), ("ZLines", grid.Z) })
        {
            var want = source.Element(tag)!.Value.Split(',').Select(v => SarFixtures.D(v) * delta).ToList();
            Assert.Equal(want, axis.Lines);
        }
        var low = CsxcadWriter.Write(problem, grid, settings, OpenEmsRunSettings.Default);
        Assert.True(low.Ok, low.Refusal);
        var written = XDocument.Parse(low.Model!).Descendants("RectilinearGrid").Single();
        Assert.Equal(grid.X.Lines, written.Element("XLines")!.Value.Split(',').Select(SarFixtures.D));
        Assert.Contains(low.Notes, n => n.StartsWith("openEMS runs on the ", StringComparison.Ordinal));
    }

    /// <summary>R-sar3-5: a port whose plane lies between the given lines is refused, naming the lines either side.</summary>
    [Fact]
    public void APortOffTheLines_IsRefused()
    {
        double[] lines = [-100, -50, -10, -2, -1.25, -0.5, 0, 0.5, 1.25, 2, 10, 50, 100];
        var section = new CemOpenEms { GridLines = new CemGridLines { Unit = "mm", X = [.. lines], Y = [.. lines], Z = [.. lines] } };
        var settings = CemOpenEms.ResolveGrid(section);
        Assert.Null(FdtdGrid.Build(Problem(0), settings, long.MaxValue).Refusal);

        var refused = FdtdGrid.Build(Problem(0.25 * mm), settings, long.MaxValue).Refusal;
        Assert.NotNull(refused);
        Assert.Contains("Port 1's sheet lies at y = 250 µm, between the lines at 0 µm and 500 µm", refused, StringComparison.Ordinal);
    }

    private static string StandIn()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-sar3-grid-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "grid.xml");
        string L(double from, double to, int n) => string.Join(",", Enumerable.Range(0, n).Select(i => (from + (to - from) * i / (n - 1)).ToString("R", CultureInfo.InvariantCulture)));
        File.WriteAllText(f, $"<?xml version=\"1.0\"?>\n<ContinuousStructure CoordSystem=\"0\">\n    <RectilinearGrid DeltaUnit=\"0.001\" CoordSystem=\"0\">\n" +
                             $"        <XLines Qty=\"41\">{L(-100, 100, 41)}</XLines>\n        <YLines Qty=\"41\">{L(-100, 100, 41)}</YLines>\n" +
                             $"        <ZLines Qty=\"81\">{L(-100, 100, 81)}</ZLines>\n    </RectilinearGrid>\n</ContinuousStructure>\n");
        return f;
    }
}

public sealed class SarSetupUnchangedTests
{
    /// <summary>R-sar3-4: a setup that requests no SAR writes byte for byte the openEMS golden committed before this phase (the
    /// microstrip of brief-em3d-9's gate 4), and CellConstantMaterial on changes exactly the &lt;FDTD&gt; element's attribute.</summary>
    [Fact]
    public void ASetupWithoutSar_WritesTheCommittedGolden()
    {
        var (setup, source) = Em3dGeneratorTests.Microstrip();
        setup.Solver3D = Em3dSolverId.OpenEms;
        setup.AirBox = GoldenAirBoxes.For("fixture microstrip");       // the golden's own box, never the default
        var r = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(r.Ok, r.Refusal);
        var gs = CemOpenEms.ResolveGrid(setup.OpenEms);
        var grid = FdtdGrid.Build(r.Problem!, gs, long.MaxValue);
        var off = CsxcadWriter.Write(r.Problem!, grid, gs, CemOpenEms.ResolveRun(setup.OpenEms, requestsSar: false));
        string golden = File.ReadAllText(Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "openems-goldens", "microstrip", CsxcadWriter.ModelFile));
        Assert.Equal(golden, off.Model);

        var on = CsxcadWriter.Write(r.Problem!, grid, gs, CemOpenEms.ResolveRun(new CemOpenEms { CellConstantMaterial = true }));
        Assert.Equal(golden.Replace("TimeStepMethod=\"3\">", "TimeStepMethod=\"3\" CellConstantMaterial=\"1\">", StringComparison.Ordinal), on.Model);
    }
}
