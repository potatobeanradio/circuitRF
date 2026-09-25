using System.Globalization;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-3 — the solver-neutral 3D problem, generated from a layout and a technology. §7's
//  gates 1-7 and 9 (gate 8, Validate's count, is tests/Engine.Tests/Em3d/Em3dProblemTests.cs).
//  No solver is involved anywhere.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class Em3dGeneratorTests
{
    private const double Um = 1e-6;

    // ── 1. The reference page's microstrip ──────────────────────────────────────────────────────

    [Fact]
    public void Gate1_TheReferenceMicrostrip_OneSubstrateOneAirOneConductorTwoPorts_OnAPecFloor()
    {
        var (setup, source) = Microstrip();
        var result = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(result.Ok, result.Refusal);
        var p = result.Problem!;
        Assert.Empty(p.Validate());

        // One substrate, one air, one conductor — and 35 µm of copper is 50 skin depths at 10 GHz,
        // so a solid, not a sheet (R-em3d3-5f).
        Assert.Equal(["RO4350", "air", "Top Copper (1 oz)/1"], p.Solids.Select(s => s.Name));
        Assert.Equal([Em3dRole.Dielectric, Em3dRole.Air, Em3dRole.Conductor], p.Solids.Select(s => s.Role));
        Assert.Equal([1, 2, 3], p.Solids.Select(s => s.Order));
        Assert.Empty(p.Sheets);

        // z to the DBU-to-metre conversion: ground copper 0-35, RO4350 35-543, top copper 543-578 µm.
        var sub = Assert.IsType<Em3dBox>(p.Solids[0].Primitive);
        Assert.Equal(35 * Um, sub.Min.Z, 15);
        Assert.Equal(543 * Um, sub.Max.Z, 15);
        var cu = Assert.IsType<Em3dExtrudedPolygon>(p.Solids[2].Primitive);
        Assert.Equal(543 * Um, cu.ZBottom, 15);
        Assert.Equal(578 * Um, cu.ZTop, 15);
        var air = Assert.IsType<Em3dBox>(p.Solids[1].Primitive);
        Assert.Equal(543 * Um, air.Min.Z, 15);

        // The floor is the undrawn ground plane's top, and it is PEC; every other face absorbs.
        Assert.Equal(Em3dBoundaryKind.Pec, p.Boundary.Faces.ZMin);
        Assert.Equal(Em3dBoundaryKind.Absorbing, p.Boundary.Faces.ZMax);
        Assert.Equal(35 * Um, p.Boundary.Min.Z, 15);

        // Two ports, numbered as the planar setup numbers the same layout (R-em3d3-2d): the planar
        // extractor's own ports, compared by number and position.
        var planar = PlanarExtractor.Extract(source.View.Shapes, source.Technology!, source.DbuPerMicron, 10e9,
                                             setup.ToExtractionSettings());
        var planarPorts = EmPortExtraction.Extract(source.View.Shapes, planar.Problem!, source.DbuPerMicron,
                                                   setup.ResolvePortZ0).Ports;
        Assert.Equal(planarPorts.Select(q => (q.Number, q.Location.X)), p.Ports.Select(q => (q.Number, q.Min.X)));

        var p1 = p.Ports[0];
        Assert.Equal("port/1", p1.Name);
        Assert.Equal("Top Copper (1 oz)/1", p1.PositiveObject);
        Assert.Equal("airbox/zmin", p1.NegativeObject);
        Near(new Point3(0, -550 * Um, 35 * Um), p1.Min);
        Near(new Point3(0, 550 * Um, 543 * Um), p1.Max);
        Assert.Equal(new Point3(0, 0, 1), p1.Direction);
        Assert.Equal(0.0, p1.ReferencePlane.ShiftM);
        Assert.Equal(new Point3(1, 0, 0), p1.ReferencePlane.Normal);
        Assert.Equal(new Point3(-1, 0, 0), p.Ports[1].ReferencePlane.Normal);

        Assert.Equal(20.0, p.OperatingTempC);
        Assert.Equal(new Em3dFrequency(1e9, 10e9, 4, Em3dSweepKind.Linear), p.Frequency);
    }

    // ── 2. Determinism ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_GeneratingTwiceGivesTheSameProblem_ByteForByteInCanonicalText()
    {
        var (setup, source) = CaseB(plated: true);
        string a = Canonical(Em3dGenerator.Generate(setup, source, source.Technology!).Problem!);
        var (setup2, source2) = CaseB(plated: true);
        string b = Canonical(Em3dGenerator.Generate(setup2, source2, source2.Technology!).Problem!);
        Assert.Equal(a, b);
    }

    // ── 3. F0's case B: a plated via is a tube, and the antipad is a hole ──────────────────────

    [Fact]
    public void Gate3_CaseB_APlatedViaIsTubePlusFill_AndTheGroundPlaneKeepsItsAntipad()
    {
        var (setup, source) = CaseB(plated: true);
        var result = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(result.Ok, result.Refusal);
        var p = result.Problem!;
        Assert.Empty(p.Validate());

        var plating = p.Solids.Single(s => s.Name == "via/1");
        var fill    = p.Solids.Single(s => s.Name == "via/1/fill");
        var barrel  = Assert.IsType<Em3dCylinder>(plating.Primitive);
        var bore    = Assert.IsType<Em3dCylinder>(fill.Primitive);
        Assert.Equal(Em3dRole.Conductor, plating.Role);
        Assert.Equal(Em3dRole.Air, fill.Role);
        Assert.Equal(150 * Um, barrel.Radius, 15);
        Assert.Equal(125 * Um, bore.Radius, 15);               // 25 µm wall
        Assert.Equal(0.0, barrel.AxisStart.Z, 15);              // bottom copper's bottom …
        Assert.Equal(505 * Um, barrel.AxisEnd.Z, 15);           // … to top copper's top
        Assert.Equal(plating.Order + 1, fill.Order);            // the fill wins: §1d subtracts
        Assert.True(plating.Order > p.Solids.Where(s => !s.Name.StartsWith("via/", StringComparison.Ordinal))
                                             .Max(s => s.Order));

        var plane = p.Solids.Single(s => s.Name == "Ground Plane/1");
        var slab  = Assert.IsType<Em3dExtrudedPolygon>(plane.Primitive);
        var hole  = Assert.Single(slab.Holes);
        Assert.Equal(450 * Um, hole.Max(q => q.X), 9);          // the 900 µm antipad
        Assert.Equal(235 * Um, slab.ZBottom, 15);
        Assert.Equal(270 * Um, slab.ZTop, 15);

        // The antipad is filled by the dielectric the inner plane's band is given to (the one above).
        var diel1 = Assert.IsType<Em3dBox>(p.Solids.Single(s => s.Name == "Dielectric 1").Primitive);
        Assert.Equal(235 * Um, diel1.Min.Z, 15);
        Assert.True(p.Solids.Single(s => s.Name == "Dielectric 1").Order < plane.Order);

        // A drawn plane is geometry, not a floor; the ports end on it, one from each side.
        Assert.Equal(Em3dBoundaryKind.Absorbing, p.Boundary.Faces.ZMin);
        Assert.Equal(["Ground Plane/1", "Ground Plane/1"], p.Ports.Select(q => q.NegativeObject));
        Assert.Equal([new Point3(0, 0, 1), new Point3(0, 0, -1)], p.Ports.Select(q => q.Direction));
        Assert.Equal(270 * Um, p.Ports[0].Min.Z, 15);
        Assert.Equal(470 * Um, p.Ports[0].Max.Z, 15);
        Assert.Equal(35 * Um, p.Ports[1].Min.Z, 15);
        Assert.Equal(235 * Um, p.Ports[1].Max.Z, 15);
    }

    // ── 4. An overmold ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_AnOvermoldCoversTheLateralExtent_AboveDielectricsBelowConductors()
    {
        var (setup, source) = Microstrip();
        var tech = TechPersistence.Deserialize(TechPersistence.Serialize(source.Technology!));
        tech.Materials.Add(new TechMaterial { Name = "Mould compound", Epsr = 3.9, TanD = 0.005 });
        tech.Bodies.Add(new TechBody
        {
            Name = "Overmold", Material = "Mould compound", SitsOn = "RO4350", ThicknessDbu = 500_000,
        });

        var p = Em3dGenerator.Generate(setup, source with { Technology = tech }, tech).Problem!;
        Assert.Empty(p.Validate());

        var mould = p.Solids.Single(s => s.Name == "Overmold");
        var bodyBox = Assert.IsType<Em3dBox>(mould.Primitive);
        Assert.Equal(p.Boundary.Min.X, bodyBox.Min.X);
        Assert.Equal(p.Boundary.Max.Y, bodyBox.Max.Y);
        Assert.Equal(543 * Um, bodyBox.Min.Z, 15);
        Assert.Equal(1043 * Um, bodyBox.Max.Z, 15);
        Assert.Equal(Em3dRole.Dielectric, mould.Role);

        int dielectric = p.Solids.Single(s => s.Name == "RO4350").Order;
        int conductor  = p.Solids.Single(s => s.Role == Em3dRole.Conductor).Order;
        Assert.InRange(mould.Order, dielectric + 1, conductor - 1);
        Assert.Contains(p.Materials, m => m.Name == "Mould compound" && m.Epsr == 3.9);
    }

    // ── 5. Auto never resolves to 3D ───────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_AutoNeverResolvesTo3D_OverEveryCemInTheRepo()
    {
        var cems = RepoCems();
        Assert.True(cems.Count >= 10, $"only {cems.Count} .cem files found");
        foreach (string cem in cems)
        {
            var setup = EmSetupPersistence.LoadFromFile(cem);
            Assert.False(setup.Is3D, cem);
            var choice = EmKernelRegistry.Choose(setup.AnalysisKind, EmExtractorVerdict.Yes, EmExtractorVerdict.Yes);
            Assert.Contains(choice.Kind, new[] { EmAnalysisKind.CrossSection, EmAnalysisKind.Planar });
        }
        // By construction: no registered kernel, and no analysis kind, is a 3D solver.
        Assert.Equal([EmAnalysisKind.CrossSection, EmAnalysisKind.Planar], EmKernelRegistry.Kernels.Select(k => k.Kind));
        Assert.Equal(["CrossSection", "Planar", "Auto"], Enum.GetNames<EmAnalysisKind>());
    }

    // ── 6. Round trip ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_EveryCemInTheRepo_RoundTripsWithNoNewKey_AndA3DSetupRoundTrips()
    {
        // Writer to writer, as brief 2's gate 1 does for .ctech: not every .cem in the repo is in this
        // writer's exact spelling (ten state "ResonanceSearch": false, which the writer omits), so the
        // bytes on disk were never the fixed point. What the 3D fields could change is what the writer
        // EMITS for a planar file, and the only way is a key the file did not state.
        foreach (string cem in RepoCems())
        {
            string written = EmSetupPersistence.Serialize(EmSetupPersistence.Deserialize(File.ReadAllText(cem)));
            Assert.Equal(written, EmSetupPersistence.Serialize(EmSetupPersistence.Deserialize(written)));
            foreach (string key in new[] { "\"Solver3D\"", "\"OperatingTempC\"", "\"AirBox\"", "\"Palace\"", "\"OpenEms\"" })
                Assert.DoesNotContain(key, written, StringComparison.Ordinal);
        }

        var (setup, _) = Microstrip();
        setup.Solver3D       = Em3dSolver.Palace;
        setup.OperatingTempC = 85;
        setup.AirBox         = new EmAirBox(ZMax: new EmAirBoxFace(2000, Em3dBoundaryKind.Pec));
        setup.Palace         = new CemPalace();
        string once = EmSetupPersistence.Serialize(setup);
        Assert.Equal(once, EmSetupPersistence.Serialize(EmSetupPersistence.Deserialize(once)));
        var back = EmSetupPersistence.Deserialize(once);
        Assert.Equal(Em3dSolver.Palace, back.Solver3D);
        Assert.Equal(85, back.OperatingTempC);
        Assert.Equal(new EmAirBoxFace(2000, Em3dBoundaryKind.Pec), back.AirBox!.ZMax);
        Assert.NotNull(back.Palace);
        Assert.Null(back.OpenEms);
    }

    // ── 7. A refused port ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_APortThePlanarExtractorRefuses_IsRefusedWithThePlanarSentence()
    {
        var (setup, source) = Microstrip();
        var port2 = source.View.Shapes.OfType<LabelShape>().Single(l => l.Text == "2");
        port2.X = 12_000_000;                           // 2 mm past the end of the line

        var planar = PlanarExtractor.Extract(source.View.Shapes, source.Technology!, source.DbuPerMicron, 10e9,
                                             setup.ToExtractionSettings());
        string planarRefusal = EmPortExtraction.Extract(source.View.Shapes, planar.Problem!, source.DbuPerMicron,
                                                        setup.ResolvePortZ0, source.View.DisplayUnit).Refusal!;

        var result = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.False(result.Ok);
        Assert.Equal(planarRefusal, result.Refusal);
    }

    // ── 9. The reference page ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate9_TheEmSetupReferencePage_DescribesTheNewFields()
    {
        string page = CircuitRF.Cli.DocumentSchema.Render(CircuitRF.Cli.DocumentSchema.Find("em-setup")!);
        foreach (string word in new[] { "Solver3D", "AirBox", "OperatingTempC", "CemAirBoxFace", "PaddingUm",
                                        "Em3dSolver", "OpenEms", "Em3dBoundaryKind", "Absorbing" })
            Assert.Contains(word, page, StringComparison.Ordinal);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>`circuitrf reference layout`'s own example, on the technology it names, with the
    /// em-setup page's own frequency plan and Solver3D set.</summary>
    internal static (EmSetup, EmLayoutSource) Microstrip()
    {
        const string clay = """
            {
              "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Mm", "SnapDbu": 10000,
              "Shapes": [
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X1": 0, "Y1": -550000, "X2": 10000000, "Y2": 550000 },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X": 0, "Y": 0, "Text": "1", "Height": 400000, "IsPort": true, "PortDirection": "R0" },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X": 10000000, "Y": 0, "Text": "2", "Height": 400000, "IsPort": true, "PortDirection": "R180" }
              ],
              "Instances": []
            }
            """;
        var view = LayoutPersistence.Deserialize(clay);
        var tech = ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz");
        var setup = new EmSetup
        {
            Name = "thru", LayoutRef = "thru/layout/thru.clay",
            Frequency = new FrequencySpec("1", "10", 4, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolver.Palace,
        };
        return (setup, new EmLayoutSource("/nowhere/thru.clay", view, tech, view.DbuPerMicron));
    }

    /// <summary>F0's case B (testdata/em3d/f0/B-via/planar/ws) — its own .clay and .ctech, with the
    /// signal via made a plated barrel with a 25 µm wall.</summary>
    internal static (EmSetup, EmLayoutSource) CaseB(bool plated)
    {
        string ws = Path.Combine(RepoRoot(), "testdata", "em3d", "f0", "B-via", "planar", "ws");
        string clay = Path.Combine(ws, "via", "layout", "via.clay");
        var view = LayoutPersistence.LoadFromFile(clay);
        var tech = TechPersistence.Deserialize(File.ReadAllText(Path.Combine(ws, "tech", "f0-case-b.ctech")));
        if (plated)
        {
            var via = tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Via);
            via.Fill = ViaFillKind.Plated;
            via.WallThicknessDbu = 25_000;
        }
        var setup = new EmSetup
        {
            Name = "via", LayoutRef = "via/layout/via.clay",
            Frequency = new FrequencySpec("0.1", "20", 200, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolver.Palace,
        };
        return (setup, new EmLayoutSource(clay, view, tech, view.DbuPerMicron));
    }

    /// <summary>The problem as text, every double in round-trip form — what gate 2 compares.</summary>
    private static string Canonical(Em3dProblem p)
    {
        var sb = new StringBuilder();
        string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        string P3(Point3 q) => $"({R(q.X)},{R(q.Y)},{R(q.Z)})";
        string Ring(IReadOnlyList<Point2> r) => string.Join(" ", r.Select(q => $"{R(q.X)},{R(q.Y)}"));

        foreach (var s in p.Solids)
        {
            sb.Append($"solid {s.Name} {s.Material} {s.Role} {s.Order} ");
            sb.AppendLine(s.Primitive switch
            {
                Em3dExtrudedPolygon e => $"extrude {R(e.ZBottom)} {R(e.ZTop)} [{Ring(e.Outline)}] " +
                                         string.Join("", e.Holes.Select(h => $"[{Ring(h)}]")),
                Em3dBox b => $"box {P3(b.Min)} {P3(b.Max)}",
                Em3dCylinder c => $"cylinder {P3(c.AxisStart)} {P3(c.AxisEnd)} {R(c.Radius)}",
                var other => other.ToString(),
            });
        }
        foreach (var s in p.Sheets)
            sb.AppendLine($"sheet {s.Name} {s.Material} {s.Order} {R(s.Z)} {R(s.ThicknessM)} [{Ring(s.Outline)}]");
        foreach (var m in p.Materials)
            sb.AppendLine($"material {m.Name} {R(m.Epsr)} {R(m.TanD)} {R(m.Mur)} {R(m.SigmaSm)}");
        foreach (var q in p.Ports)
            sb.AppendLine($"port {q.Number} {q.Name} {q.PositiveObject} {q.NegativeObject} {P3(q.Min)} {P3(q.Max)} " +
                          $"{P3(q.Direction)} {R(q.Z0.Real)} {R(q.Z0.Imaginary)} {P3(q.ReferencePlane.Origin)}");
        sb.AppendLine($"box {P3(p.Boundary.Min)} {P3(p.Boundary.Max)} {p.Boundary.Faces}");
        sb.AppendLine($"freq {p.Frequency} {R(p.OperatingTempC)}");
        return sb.ToString();
    }

    private static void Near(Point3 expected, Point3 actual)
    {
        Assert.Equal(expected.X, actual.X, 15);
        Assert.Equal(expected.Y, actual.Y, 15);
        Assert.Equal(expected.Z, actual.Z, 15);
    }

    private static List<string> RepoCems()
        => [.. new[] { "examples", "testdata" }
               .SelectMany(d => Directory.EnumerateFiles(Path.Combine(RepoRoot(), d), "*.cem", SearchOption.AllDirectories))
               .Order(StringComparer.Ordinal)];

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "circuitrf.slnx"))) return dir;
        throw new InvalidOperationException("repo root not found");
    }
}
