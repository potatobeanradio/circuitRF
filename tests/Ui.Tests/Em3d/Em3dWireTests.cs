using System.Text.RegularExpressions;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Assembly;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.WBond;
using Xunit;
using Point3 = CircuitRF.Engine.Em3d.Point3;
using WPoint3 = CircuitRF.WBond.Point3;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-4 — bond wires in the 3D problem: cross-section, feet, balls, loop height. §7's ten
//  gates, plus R-em3d4-2c's sweep through vertical. No solver anywhere. The geometry is F0's case A
//  (testdata/em3d/f0/README.md): 100 µm substrate on a PEC ground, zero-thickness 100 µm pads at
//  x = ±500 µm, and kernel W's own case.wBond — a 1 mil gold wire whose axis ends 12.7 µm above
//  the pads and whose wBond loop height is 150 µm exactly.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class Em3dWireTests
{
    private const double Um = 1e-6;
    private const double D = 25.4 * Um;
    private const double PadTop = 100 * Um;

    // ── 1. The perimeter-matched hexagon ────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_TheHexagon_MatchesTheNotesTable_AndItsPerimeterIsPiD()
    {
        var s = Em3dWireSection.Hexagon(D);
        Rel(Math.PI * D / 6, s.Side);
        Rel(Math.Sqrt(3) * Math.PI / 6 * D, s.Height);
        Rel(Math.PI / 3 * D, s.Width);
        Rel(Math.PI * D, s.Perimeter);

        // The outline a sweep is built from has the same perimeter, measured edge by edge.
        var ring = Em3dWireSection.Outline(Em3dSection.Hexagon, D);
        double perimeter = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            perimeter += Math.Sqrt(Math.Pow(ring[i].Across - ring[j].Across, 2) + Math.Pow(ring[i].Up - ring[j].Up, 2));
        Rel(Math.PI * D, perimeter);
    }

    // ── 2, 3, 6. Case A: level bottom face, foot on pad bitwise, assembly vs wBond loop height ─

    [Fact]
    public void Gate2_3_6_CaseA_BottomFaceLevel_FootOnPadExactly_AndTheTwoLoopHeights()
    {
        var design = WBondIo.ReadFile(Path.Combine(RepoRoot(), "testdata", "em3d", "f0", "A-bondwire", "kernelw", "case.wBond"));
        var result = Generate(design);
        Assert.True(result.Ok, result.Refusal);
        Assert.Empty(result.Problem!.Validate());

        var sweep = Sweep(result, "wire/G1/1");
        Assert.Equal(Em3dSection.Hexagon, sweep.Section);

        // Gate 2 — at every section the two lowest corners are adjacent and at one height.
        foreach (var ring in sweep.Rings)
        {
            var low = Enumerable.Range(0, ring.Count).OrderBy(k => ring[k].Z).Take(2).Order().ToArray();
            Assert.True(low[1] - low[0] == 1 || (low[0] == 0 && low[1] == ring.Count - 1));
            Assert.Equal(ring[low[0]].Z, ring[low[1]].Z, 1e-12 * D);
        }

        // Gate 3 — both feet: the first two and last two rings' bottom corners ARE the pad sheet's z.
        var pads = result.Problem!.Sheets;
        Assert.Equal(2, pads.Count);
        foreach (int i in new[] { 0, 1, sweep.Rings.Count - 2, sweep.Rings.Count - 1 })
        {
            var ring = sweep.Rings[i];
            double padZ = pads.Single(p => Math.Sign(p.Outline[0].X) == Math.Sign(ring[0].X)).Z;
            Assert.Equal(2, ring.Count(q => q.Z == padZ));
        }

        // Gate 6 — from first principles: the apex axis is at 262.7 µm, its top surface half a section
        // height above; the pads' tops are 100 µm. wBond's own number is 150 µm (max − min of the axis).
        double h = Math.Sqrt(3) * Math.PI / 6 * D;
        var report = Assert.Single(result.Wires);
        Assert.Equal(150 * Um, report.WBondLoopHeightM, 12);
        Assert.Equal(262.7 * Um + h / 2 - PadTop, report.AssemblyLoopHeightM, 12);
        // …so they differ by one section height plus how far the file's end point sat above the foot's
        // axis (12.7 µm, a round wire's half-height, against the hexagon's h/2) — the foot's own part.
        Assert.Equal(h + (12.7 * Um - h / 2), report.AssemblyLoopHeightM - report.WBondLoopHeightM, 12);
        Assert.Contains(result.Notes, n => n.Contains("moved in z", StringComparison.Ordinal));
    }

    // ── 4. Foot direction, both ends of an asymmetric wire (and the wire's own foot length) ─────

    [Fact]
    public void Gate4_FeetPointOutwardAlongTheirEndSegments_AndAnOverhangIsAWarning()
    {
        var design = OneWire(
            new WPoint3(-475_000, 0, 112_700), new WPoint3(-400_000, 30_000, 250_000),
            new WPoint3(300_000, 0, 250_000), new WPoint3(475_000, 0, 112_700));
        design.Arrays[0].FootLengthNm = 40_000;              // the array's — wins over the process default
        var result = Generate(design);
        Assert.True(result.Ok, result.Refusal);
        var path = Sweep(result, "wire/G1/1").Path;

        // Start: away from the loop along P0 − P1 in plan, 40 µm.
        var startDir = Norm(-75, -30);
        Assert.Equal(40 * Um, Plan(path[0], path[1]).Len, 12);
        Assert.Equal(startDir.X, Plan(path[1], path[0]).Dx, 12);
        Assert.Equal(startDir.Y, Plan(path[1], path[0]).Dy, 12);
        Assert.Equal(-475 * Um, path[1].X, 15);
        Assert.Equal(0.0, path[1].Y, 15);

        // End: along P3 − P2, +x.
        Assert.Equal(1.0, Plan(path[^2], path[^1]).Dx, 12);
        Assert.Equal(475 * Um, path[^2].X, 15);
        Assert.Equal(0.0, path[^2].Y, 15);

        // A wire's own foot length beats its array's; 200 µm from x = 475 overhangs the pad by 125 µm.
        design.Arrays[0].Wires[0].FootLengthNm = 200_000;
        var over = Generate(design);
        Assert.True(over.Ok, over.Refusal);
        Assert.Equal(WireBondValueSource.Wire, over.Wires[0].Process.FootLength.Source);
        Assert.Equal(125 * Um, over.Wires[0].End.OverhangM, 12);
        Assert.Contains(over.Warnings, w => w.Contains("wire/G1/1", StringComparison.Ordinal) &&
                                            w.Contains("overhangs", StringComparison.Ordinal) && w.Contains("125"));
    }

    // ── 5. A ball end: a neck when the path does not arrive vertically, none when it does ──────

    [Fact]
    public void Gate5_ABallEnd_InsertsANeckOnlyForANonVerticalArrival_AndMeetsTheBallFaceToFace()
    {
        foreach (bool vertical in new[] { false, true })
        {
            var design = OneWire(
                new WPoint3(-475_000, 0, 112_700),
                vertical ? new WPoint3(-475_000, 0, 250_000) : new WPoint3(-325_000, 0, 262_700),
                new WPoint3(325_000, 0, 262_700), new WPoint3(475_000, 0, 112_700));
            design.Arrays[0].Wires[0].StartBond = BondStyle.Ball;
            var result = Generate(design);
            Assert.True(result.Ok, result.Refusal);
            Assert.Empty(result.Problem!.Validate());

            var report = result.Wires[0];
            Assert.Equal(!vertical, report.Start.Neck);
            Assert.False(report.End.Neck);

            var ball = Assert.IsType<Em3dTruncatedSphere>(result.Problem!.Solids.Single(s => s.Name == "wire/G1/1/ball/start").Primitive);
            double padZ = result.Problem!.Sheets.Single(p => p.Outline[0].X < 0).Z;
            Assert.Equal(padZ, ball.ZMin);                            // the ball sits ON the pad, bitwise
            Assert.Equal(2.5 * D / 2, ball.Radius, 15);             // built-in 2.5 d, unverified
            var sweep = Sweep(result, "wire/G1/1");
            Assert.All(sweep.Rings[0], q => Assert.Equal(ball.ZMax, q.Z));   // the wire's end face IS the ball's top
            if (!vertical)
                Assert.Equal((sweep.Path[0].X, sweep.Path[0].Y), (sweep.Path[1].X, sweep.Path[1].Y));
        }
    }

    // ── R-em3d4-2c. Through vertical: no zero-area and no flipped section ──────────────────────

    [Fact]
    public void ASweepThroughVertical_HasNoZeroAreaOrFlippedSection()
    {
        var design = OneWire(
            new WPoint3(-475_000, 0, 112_700), new WPoint3(-375_000, 0, 212_700), new WPoint3(-370_000, 0, 312_700),
            new WPoint3(-370_000, 0, 412_700), new WPoint3(-375_000, 0, 512_700), new WPoint3(-200_000, 0, 600_000),
            new WPoint3(475_000, 0, 112_700));
        var result = Generate(design);
        Assert.True(result.Ok, result.Refusal);
        var rings = Sweep(result, "wire/G1/1").Rings;

        double area = 3 * Math.Sqrt(3) / 2 * Math.Pow(Math.PI * D / 6, 2);
        Point3? previousEdge = null;
        foreach (var ring in rings)
        {
            Assert.True(NewellArea(ring) > 0.5 * area);           // mitres only ever enlarge a section
            var edge = Sub(ring[1], ring[0]);                     // the bottom face's across-edge
            if (previousEdge is { } pe) Assert.True(Dot(pe, edge) > 0, "the section flipped between neighbours");
            previousEdge = edge;
        }
    }

    // ── 7. Kernel W unchanged ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_KernelW_AssemblesIdenticalInputs_WhateverThe3DFieldsSay_AndNothingInWBondReadsThem()
    {
        var files = RepoWBonds();
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            var plain = WBondIo.ReadFile(file);
            var dressed = WBondIo.ReadFile(file);
            foreach (var array in dressed.Arrays)
            {
                array.FootLengthNm = 123_000;
                foreach (var w in array.Wires)
                {
                    w.CrossSection = WireCrossSection.Round;
                    (w.StartBond, w.EndBond, w.FootLengthNm) = (BondStyle.Ball, BondStyle.Ball, 77_000);
                }
            }
            if (plain.WireCount == 0) continue;
            var a = WireMesh.Build(plain);
            var b = WireMesh.Build(dressed);
            Assert.Equal(a.Filaments, b.Filaments);
            Assert.Equal(a.Images, b.Images);
            Assert.Equal(plain.AllWires().Select(w => (plain.MaterialFor(w), w.RadiusMetres)),
                         dressed.AllWires().Select(w => (dressed.MaterialFor(w), w.RadiusMetres)));
        }

        // R-em3d4-1c — outside the model and its reader, nothing in src/WBond reads a 3D field.
        var reads = new Regex(@"\.(CrossSection|StartBond|EndBond|FootLengthNm)\b");
        foreach (string src in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "WBond"), "*.cs", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(src);
            if (name is "WBondDesign.cs" or "WBondIo.cs" || src.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            Assert.False(reads.IsMatch(StripComments(File.ReadAllText(src))), $"{name} reads a 3D-only wire field");
        }
    }

    // ── 8. Round trip, and the reference page ───────────────────────────────────────────────────

    [Fact]
    public void Gate8_EveryWBondRoundTripsWithNoNewKey_The3DFieldsRoundTrip_AndTheReferencePageNamesThem()
    {
        foreach (string file in RepoWBonds())
        {
            string written = WBondIo.Write(WBondIo.ReadFile(file));
            Assert.Equal(written, WBondIo.Write(WBondIo.Read(written)));
            foreach (string key in new[] { "CrossSection", "StartBond", "EndBond", "FootLengthNm" })
                Assert.DoesNotContain($"\"{key}\"", written, StringComparison.Ordinal);
        }

        var design = OneWire(new WPoint3(0, 0, 0), new WPoint3(100_000, 0, 0));
        design.Arrays[0].FootLengthNm = 60_000;
        var w = design.Arrays[0].Wires[0];
        (w.CrossSection, w.StartBond, w.FootLengthNm) = (WireCrossSection.Round, BondStyle.Ball, 50_000);
        string once = WBondIo.Write(design);
        Assert.Contains("\"CrossSection\": \"Round\"", once, StringComparison.Ordinal);
        Assert.Equal(once, WBondIo.Write(WBondIo.Read(once)));
        var back = WBondIo.Read(once);
        Assert.Equal((WireCrossSection.Round, BondStyle.Ball, (BondStyle?)null, 50_000L, 60_000L),
                     (back.Arrays[0].Wires[0].CrossSection!.Value, back.Arrays[0].Wires[0].StartBond!.Value,
                      back.Arrays[0].Wires[0].EndBond, back.Arrays[0].Wires[0].FootLengthNm!.Value,
                      back.Arrays[0].FootLengthNm!.Value));

        string page = CircuitRF.Cli.DocumentSchema.Render(CircuitRF.Cli.DocumentSchema.Find("wbond")!);
        foreach (string word in new[] { "CrossSection", "StartBond", "EndBond", "FootLengthNm", "Hexagon", "Round", "Wedge", "Ball" })
            Assert.Contains(word, page, StringComparison.Ordinal);
    }

    // ── 9. An unknown metal is a refusal, not gold; a metal defined twice differently warns ──────

    [Fact]
    public void Gate9_AnUnknownMetalIsARefusal_AndATechnologyMetalWinsWithAWarning()
    {
        var design = OneWire(new WPoint3(-475_000, 0, 112_700), new WPoint3(475_000, 0, 112_700));
        design.Arrays[0].Wires[0].Material = "Unobtainium";
        var refused = Generate(design);
        Assert.False(refused.Ok);
        Assert.Contains("'Unobtainium'", refused.Refusal!, StringComparison.Ordinal);
        Assert.Contains("wire/G1/1", refused.Refusal!, StringComparison.Ordinal);
        Assert.Contains("Gold", refused.Refusal!, StringComparison.Ordinal);      // the known metals are listed

        design.Arrays[0].Wires[0].Material = "Gold";
        var tech = CaseATech();
        tech.Materials.Add(new TechMaterial { Name = "Gold", Sigma20 = 4.5e7, Alpha20 = 0.0034 });
        var both = Generate(design, tech);
        Assert.True(both.Ok, both.Refusal);
        Assert.Contains(both.Warnings, w => w.Contains("'Gold'", StringComparison.Ordinal) && w.Contains("technology's values"));
        Assert.Equal(4.5e7, both.Problem!.Materials.Single(m => m.Name == "Gold").SigmaSm, 1);   // at 20 °C
    }

    // ── 10. The built-in defaults are named on every run, and not when a .wasm states them ──────

    [Fact]
    public void Gate10_TheBuiltInFootLengthIsNamed_UntilTheAssemblyRulesStateOne()
    {
        var design = WBondIo.ReadFile(Path.Combine(RepoRoot(), "testdata", "em3d", "f0", "A-bondwire", "kernelw", "case.wBond"));
        var guessed = Generate(design);
        Assert.Contains(guessed.Notes, n => n.Contains("built-in starting value", StringComparison.Ordinal) &&
                                            n.Contains("DefaultFootLengthNm", StringComparison.Ordinal));
        Assert.Equal(WireBondValueSource.BuiltIn, guessed.Wires[0].Process.FootLength.Source);
        Assert.Equal(50_800, guessed.Wires[0].Process.FootLength.Nm);

        string dir = Directory.CreateTempSubdirectory("em3d-wires-").FullName;
        try
        {
            WasmPersistence.SaveToFile(Path.Combine(dir, "house.wasm"),
                                       new WasmFile { Name = "House", DefaultFootLengthNm = 30_000 });
            design.AssemblyRef = "house.wasm";
            var stated = Generate(design, wires: new Em3dWireSource(design,
                new WireBondWorkspace(Path.Combine(dir, "case.wBond"), null), null));
            Assert.True(stated.Ok, stated.Refusal);
            Assert.DoesNotContain(stated.Notes, n => n.Contains("built-in starting value", StringComparison.Ordinal));
            Assert.Equal(new WireBondValue(30_000, WireBondValueSource.AssemblyRules), stated.Wires[0].Process.FootLength);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    internal static Em3dGenerationResult Generate(WBondDesign design, Technology? tech = null,
                                                 Em3dWireSource? wires = null)
    {
        tech ??= CaseATech();
        const string clay = """
            {
              "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Um", "SnapDbu": 1000,
              "Shapes": [
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "X1": -550000, "Y1": -50000, "X2": -450000, "Y2": 50000 },
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "X1": 450000, "Y1": -50000, "X2": 550000, "Y2": 50000 },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X": -550000, "Y": 0, "Text": "1", "Height": 20000, "IsPort": true, "PortDirection": "R0" },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X": 550000, "Y": 0, "Text": "2", "Height": 20000, "IsPort": true, "PortDirection": "R180" }
              ],
              "Instances": []
            }
            """;
        var view = LayoutPersistence.Deserialize(clay);
        var setup = new EmSetup
        {
            Name = "caseA", LayoutRef = "caseA/layout/caseA.clay",
            Frequency = new FrequencySpec("1", "40", 40, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolver.Palace, OperatingTempC = 20,
        };
        return Em3dGenerator.Generate(setup, new EmLayoutSource("/nowhere/caseA.clay", view, tech, view.DbuPerMicron),
                                      tech, wires ?? new Em3dWireSource(design, WireBondWorkspace.None, null));
    }

    /// <summary>F0 case A's stack: zero-thickness pads on 100 µm of εr 9.8, on an undrawn ground.</summary>
    private static Technology CaseATech() => new()
    {
        Name = "F0 case A",
        DefaultFlattenTolDbu = 100,
        Layers = [new LayerDef { Key = new LayerKey(1, 0), Name = "Pad" }],
        Stackup = new Stackup
        {
            Layers =
            [
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Pads", ThicknessDbu = 0, SigmaSm = 4.1e7,
                                   DrawingLayers = [new LayerKey(1, 0)] },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Substrate", ThicknessDbu = 100_000, Epsr = 9.8 },
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Ground", ThicknessDbu = 0, SigmaSm = 5.8e7,
                                   IsGroundReference = true },
            ],
        },
    };

    private static WBondDesign OneWire(params WPoint3[] points)
    {
        var design = new WBondDesign { OperatingTempC = 20 };
        var wire = new Wire { DiameterNm = 25_400 };
        wire.Points.AddRange(points);
        design.Arrays.Add(new WireArray { Name = "G1", Wires = { wire } });
        return design;
    }

    private static Em3dSweep Sweep(Em3dGenerationResult r, string name)
        => Assert.IsType<Em3dSweep>(r.Problem!.Solids.Single(s => s.Name == name).Primitive);

    private static (double Dx, double Dy, double Len) Plan(Point3 from, Point3 to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y, len = Math.Sqrt(dx * dx + dy * dy);
        return (dx / len, dy / len, len);
    }

    private static (double X, double Y) Norm(double x, double y) { double l = Math.Sqrt(x * x + y * y); return (x / l, y / l); }

    private static Point3 Sub(Point3 a, Point3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static double Dot(Point3 a, Point3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static double NewellArea(IReadOnlyList<Point3> ring)
    {
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            nx += (ring[j].Y - ring[i].Y) * (ring[j].Z + ring[i].Z);
            ny += (ring[j].Z - ring[i].Z) * (ring[j].X + ring[i].X);
            nz += (ring[j].X - ring[i].X) * (ring[j].Y + ring[i].Y);
        }
        return 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
    }

    private static void Rel(double expected, double actual)
        => Assert.True(Math.Abs(actual - expected) <= 1e-12 * Math.Abs(expected), $"{actual} vs {expected}");

    private static string StripComments(string src)
        => Regex.Replace(src, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);

    private static List<string> RepoWBonds()
        => [.. new[] { "examples", "testdata" }
               .SelectMany(d => Directory.EnumerateFiles(Path.Combine(RepoRoot(), d), "*.wBond", SearchOption.AllDirectories))
               .Order(StringComparer.Ordinal)];

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "circuitrf.slnx"))) return dir;
        throw new InvalidOperationException("repo root not found");
    }
}
