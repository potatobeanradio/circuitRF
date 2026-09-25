using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Cli;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Theming;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.WBond;
using RfCore.Export;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;
using Point3 = CircuitRF.Engine.Em3d.Point3;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-5 — see a 3D setup before solving it: `render` sections and the isometric outline,
//  and `explain` on a 3D setup. §4's eight gates. No solver anywhere, and gate 6 holds that no
//  mesher or solver process is started to answer either verb (a version probe is, since brief 6).
//
//  Party to the typeface collection: gate 3 compares rendered TEXT bytes against a fresh CLI
//  process, which has no typeface override to read (SkiaFontsTypefaceCollection's membership rule).
// ══════════════════════════════════════════════════════════════════════════════════════════════

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class Em3dRenderExplainTests(ITestOutputHelper output) : IDisposable
{
    private const double Um = 1e-6;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-em3d5-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ── 1. Sections through the reference microstrip ───────────────────────────────────────────

    [Fact]
    public void Gate1_MidSubstrateIsOneSubstrateFill_AndMidCopperIsTheStripWithBothPorts()
    {
        var problem = Microstrip();

        // RO4350 spans 35-543 µm, the strip 543-578 µm (brief 3 gate 1).
        var mid = Em3dSectionScene.Build(problem, new Em3dView(Em3dViewKind.SectionZ, 289 * Um));
        Assert.Equal(["RO4350"], mid.Objects);

        var copper = Em3dSectionScene.Build(problem, new Em3dView(Em3dViewKind.SectionZ, 560.5 * Um));
        Assert.Contains("Top Copper (1 oz)/1", copper.Objects);
        Assert.DoesNotContain("RO4350", copper.Objects);
        Assert.Equal([1, 2], copper.Ports.Select(p => p.Number));

        // The strip's cut is its outline: 10 mm long, 1.1 mm wide.
        var strip = copper.Regions.Single(r => r.Object == "Top Copper (1 oz)/1");
        Assert.Equal(10e-3, strip.Rings[0].Max(q => q.U) - strip.Rings[0].Min(q => q.U), 12);
        Assert.Equal(1.1e-3, strip.Rings[0].Max(q => q.V) - strip.Rings[0].Min(q => q.V), 12);
    }

    // ── 2. The interface convention ─────────────────────────────────────────────────────────────

    /// <summary>
    /// "z = the top of the substrate", typed as a caller types it, is the copper and not the
    /// substrate: present when bottom ≤ z &lt; top, and the plane snapped onto the boundary first, so
    /// whichever way the last bit of 543um's conversion fell it cannot flicker.
    /// </summary>
    [Fact]
    public void Gate2_ZExactlyAtTheSubstrateTop_DrawsTheCopperAndNotTheSubstrate()
    {
        var problem = Microstrip();
        var (view, refusal) = RenderEm3d.ParseSection("z=543um");
        Assert.Null(refusal);

        var scene = Em3dSectionScene.Build(problem, view);
        Assert.Contains("Top Copper (1 oz)/1", scene.Objects);
        Assert.DoesNotContain("RO4350", scene.Objects);

        var cu = Assert.IsType<Em3dExtrudedPolygon>(problem.Solids.Single(s => s.Name == "Top Copper (1 oz)/1").Primitive);
        Assert.Equal(cu.ZBottom, scene.At);   // snapped, bit for bit
    }

    // ── 3. CLI == in-process, byte for byte ─────────────────────────────────────────────────────

    /// <summary>The verb as a PROCESS against CircuitRF.Render called here on the same documents: a
    /// section (the via cut vertically, which exercises the analytic cylinder cut) and the isometric
    /// outline (which exercises the tessellation), each as SVG and PDF.</summary>

    [Fact]
    public void Gate3_TheVerbAsAProcess_WritesTheBytesTheRendererWrites_SectionAndIso_SvgAndPdf()
    {
        var ws = CaseBWorkspace();
        var src = InProcess(ws.Cem3d);
        var theme = ThemeResolver.Resolve(ThemeResolver.DefaultThemeName, ws.Root);
        var style = new Em3dRenderStyle(
            Em3dSectionRenderer.ObjectColours(src.Problem, src.Origins, src.Tech, theme, ColorVariant.Light),
            theme, ColorVariant.Light, DocumentExtents.DefaultMargin, Transparent: false);

        foreach (var (args, view) in new (string[] Args, Em3dView View)[]
                 {
                     (["--section", "xz@y=0um"], new Em3dView(Em3dViewKind.SectionY, 0)),
                     (["--iso"], Em3dView.Iso),
                 })
        {
            var scene = Em3dSectionScene.Build(src.Problem, view);
            foreach (string ext in new[] { "svg", "pdf" })
            {
                string outPath = Path.Combine(_root, $"{view.Plane}.{ext}");
                var (exit, stdout, stderr) = RunCli(["render", ws.Cem3d, "-o", outPath, .. args]);
                Assert.True(exit == 0, stderr + stdout);

                byte[] inProcess = ext == "svg"
                    ? Svg(1600, 1200, c => Em3dSectionRenderer.Draw(c, 1600, 1200, scene, style))
                    : Pdf(1600, 1200, c => Em3dSectionRenderer.Draw(c, 1600, 1200, scene, style));
                byte[] fromCli = File.ReadAllBytes(outPath);
                output.WriteLine($"{view.Plane}.{ext}: {fromCli.Length} vs {inProcess.Length} bytes");
                if (ext == "pdf")
                    Assert.True(fromCli.AsSpan().SequenceEqual(inProcess), $"{view.Plane}.pdf differs from the in-process render");
                else
                    // RenderCliVerbTests' one named exclusion, and only it: Skia numbers an SVG's
                    // clipPath ids from a counter it never resets, so two processes that have drawn
                    // different amounts before disagree about the ids of identical clips.
                    Assert.Equal(StripSkiaIds(Encoding.UTF8.GetString(fromCli)), StripSkiaIds(Encoding.UTF8.GetString(inProcess)));
            }
        }
    }

    // ── 4. Refusals ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_ABareNumber_TwoViews_AndAPlanarCem_AreRefusals_ThePlanarOneNamingItsLayout()
    {
        var ws = CaseBWorkspace();
        string outPath = Path.Combine(_root, "never.svg");

        Assert.Equal("render.em3d.unit-required", RefusalCode("render", ws.Cem3d, "-o", outPath, "--section", "z=35"));
        Assert.Equal("render.em3d.multiple-views",
                     RefusalCode("render", ws.Cem3d, "-o", outPath, "--section", "z=35um", "--iso"));

        var (code, message) = Refusal("render", ws.CemPlanar, "-o", outPath, "--iso");
        Assert.Equal("render.em3d.planar", code);
        Assert.Contains(Path.Combine("via", "layout", "via.clay"), message);
        Assert.False(File.Exists(outPath));
    }

    // ── 5. explain lists the via transition, and its --json round-trips ─────────────────────────

    [Fact]
    public void Gate5_ExplainListsEverySolidMaterialPortAndTheAirBox_AndItsJsonRoundTrips()
    {
        var ws = CaseBWorkspace();
        var src = InProcess(ws.Cem3d);

        var (exit, stdout, stderr) = RunCli("explain", ws.Cem3d, "--json");
        Assert.True(exit == 0, stderr + stdout);
        var element = JsonNode.Parse(stdout)!["result"]!["explain"]!["em3d"]!;

        var options = ResultDocumentWriter.Options;
        var report = element.Deserialize<ExplainEm3dJson>(options)!;
        Assert.True(JsonNode.DeepEquals(element, JsonSerializer.SerializeToNode(report, options)),
                    "the em3d block does not survive a System.Text.Json round trip");

        Assert.Equal(src.Problem.Solids.Select(s => s.Name).Concat(src.Problem.Sheets.Select(s => s.Name)).Order(),
                     report.Solids.Select(s => s.Name).Order());
        Assert.Contains(report.Solids, s => s.Name == "via/1/fill" && s.Role == "air" && s.Primitive == "cylinder");
        Assert.Equal(src.Problem.Materials.Select(m => m.Name), report.Materials.Select(m => m.Name));
        Assert.All(report.Materials, m => Assert.NotEqual("unrecorded", m.From));
        Assert.Equal(src.Problem.Ports.Select(p => p.Number), report.Ports.Select(p => p.Number));
        Assert.Equal(["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"], report.AirBox!.Faces.Select(f => f.Face));
        Assert.Equal(("m", 1.0), (report.LengthUnit, report.LengthScale));

        // Round vias are §4.3 row 4's. Palace's size is an estimate from the Palace section's mesh
        // sizes (brief-em3d-7); openEMS's is exact — the grid generator's own grid (brief-em3d-8
        // R-em3d8-5c), with the feature that set its smallest cell named.
        Assert.Contains(report.Guidance, g => g.Row == 4);
        var palaceSize = report.Size.Single(z => z.Backend == "palace");
        Assert.Equal("estimate", palaceSize.Kind);
        Assert.True(palaceSize.Elements > 0 && palaceSize.Unknowns > palaceSize.Elements);
        var openEmsSize = report.Size.Single(z => z.Backend == "openems");
        Assert.Equal("exact", openEmsSize.Kind);
        var grid = FdtdGrid.Build(src.Problem, OpenEmsGridSettings.Default);
        Assert.Equal(grid.Cells, openEmsSize.Elements);
        Assert.Equal([grid.X.Lines.Count, grid.Y.Lines.Count, grid.Z.Lines.Count], openEmsSize.CellsPerAxis);
        Assert.Equal(grid.TimeStepEstimateS, openEmsSize.TimeStepS);
        Assert.NotEmpty(openEmsSize.SmallestCellFeatures!);
        // brief-em3d-9 R-em3d9-3a: one openEMS run per port, said before anything runs.
        Assert.Contains($"openEMS runs once per port: {src.Problem.Ports.Count} runs", openEmsSize.Note);

        // The Palace estimate the row will carry counts meshed regions only: a conductor is a hole.
        var estimate = Em3dSizeEstimate.Palace(src.Problem, _ => 100 * Um, 2);
        Assert.DoesNotContain(estimate.Regions, r => src.Problem.Solids.Single(s => s.Name == r.Solid).Role == Em3dRole.Conductor);
        Assert.NotNull(estimate.MemoryBytes);

        // …and it counts the BACKGROUND too — what no solid claims, which the mesher fills as air. Case B's
        // floor is absorbing, so that is the whole region below the stack.
        var withBackground = Em3dSizeEstimate.Palace(src.Problem, _ => 100 * Um, 2, backgroundEdgeM: 100 * Um);
        var background = Assert.Single(withBackground.Regions, r => r.Solid == "background");
        Assert.True(background.VolumeM3 > 0);
        Assert.True(withBackground.Tetrahedra > estimate.Tetrahedra);
    }

    // ── 6. Zero processes ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_ExplainAndRenderStartNoProcess()
    {
        // Narrowed by brief-em3d-6 R-em3d6-4c from "no process" to "no SOLVE process": explain's solver
        // section asks each program its version (and Palace for a dry run) through the same launcher,
        // which is a probe and is allowed. A mesher or a solver is still never started by either verb.
        var ws = CaseBWorkspace();
        long before = Em3dProcessLauncher.SolvesStarted;
        Assert.Equal(0, InProcessCli("explain", ws.Cem3d));
        Assert.Equal(0, InProcessCli("render", ws.Cem3d, "-o", Path.Combine(_root, "p.png"), "--size", "200x150", "--iso"));
        // A delta, not zero: brief-em3d-7's gates start Gmsh in this test process, and share this
        // class's collection so none can run in between.
        Assert.Equal(before, Em3dProcessLauncher.SolvesStarted);
    }

    // ── 7. Mitred sweep joints ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-em3d5-1b on kernel W's own loop: every joint ring IS the problem's ring, bit for bit, and lies
    /// on the bisector plane of the two segments it joins — so neighbouring prisms share that face with
    /// no gap and no overlap — and the whole sweep is a closed mesh: every edge has exactly two faces.
    /// </summary>
    [Fact]
    public void Gate7_MitredSweepJointsShareBitwiseEqualVertices_OnTheBisector_AndTheMeshIsClosed()
    {
        var result = CaseAWires();
        var solid = result.Problem!.Solids.Single(s => s.Name == "wire/G1/1");
        var sweep = Assert.IsType<Em3dSweep>(solid.Primitive);
        var mesh = Em3dTessellation.Of(solid);
        Assert.All(mesh.Triangles, t => Assert.Equal("wire/G1/1", t.Solid));

        int n = sweep.Rings[0].Count;
        for (int k = 0; k < sweep.Rings.Count; k++)
            for (int j = 0; j < n; j++)
                Assert.Equal(sweep.Rings[k][j], mesh.Vertices[k * n + j]);   // record equality: bitwise

        for (int k = 1; k + 1 < sweep.Path.Count; k++)
        {
            var a = Unit(Sub(sweep.Path[k], sweep.Path[k - 1]));
            var b = Unit(Sub(sweep.Path[k + 1], sweep.Path[k]));
            var bisector = Unit(new Point3(a.X + b.X, a.Y + b.Y, a.Z + b.Z));
            foreach (var q in sweep.Rings[k])
                Assert.True(Math.Abs(Dot(Sub(q, sweep.Path[k]), bisector)) < 1e-12 * sweep.Diameter,
                            $"joint {k} is not on its bisector plane");
        }

        var edges = new Dictionary<(int, int), int>();
        foreach (var t in mesh.Triangles)
            foreach (var (p, q) in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
            {
                var key = p < q ? (p, q) : (q, p);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        Assert.All(edges.Values, count => Assert.Equal(2, count));
    }

    // ── 8. Wire rows ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_WireRowsShowBothLoopHeights_AndWhichLevelSetTheFootLength()
    {
        var result = CaseAWires();
        var setup = new EmSetup { Name = "caseA", Solver3D = Em3dSolver.Palace };
        var report = ExplainEm3d.Build(new Em3dSetupSource("/nowhere/caseA.cem", setup,
                                                           new EmSetupResolution(null, null, null, []), result, null));

        var built = result.Wires.Single();
        var row = Assert.Single(report.Wires);
        Assert.Equal(built.AssemblyLoopHeightM, row.AssemblyLoopHeightM);
        Assert.Equal(built.WBondLoopHeightM, row.WBondLoopHeightM);
        Assert.Equal(150 * Um, row.WBondLoopHeightM, 1e-12);
        Assert.NotEqual(row.AssemblyLoopHeightM, row.WBondLoopHeightM);
        Assert.Equal("built-in", row.FootLengthFrom);     // case A states no foot, and no .wasm resolves
        Assert.Equal(built.Process.FootLength.Nm * 1e-9, row.FootLengthM);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private static Em3dProblem Microstrip()
    {
        var (setup, source) = Em3dGeneratorTests.Microstrip();
        var result = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(result.Ok, result.Refusal);
        return result.Problem!;
    }

    private static Em3dGenerationResult CaseAWires()
    {
        var design = WBondIo.ReadFile(Path.Combine(RepoRoot(), "testdata", "em3d", "f0", "A-bondwire", "kernelw", "case.wBond"));
        var result = Em3dWireTests.Generate(design);
        Assert.True(result.Ok, result.Refusal);
        return result;
    }

    private sealed record Workspace(string Root, string Cem3d, string CemPlanar);

    /// <summary>F0's case B workspace, copied, with its signal via made a plated barrel with a 25 µm
    /// wall (brief 3 gate 3) and a 3D setup beside the planar one.</summary>
    private Workspace CaseBWorkspace()
    {
        string from = Path.Combine(RepoRoot(), "testdata", "em3d", "f0", "B-via", "planar", "ws");
        string ws = Path.Combine(_root, "ws");
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(ws, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
        }

        string techPath = Path.Combine(ws, "tech", "f0-case-b.ctech");
        var tech = TechPersistence.LoadFromFile(techPath);
        var via = tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Via);
        via.Fill = ViaFillKind.Plated;
        via.WallThicknessDbu = 25_000;
        TechPersistence.SaveToFile(techPath, tech);

        string cem = Path.Combine(ws, "via", "em", "via3d.cem");
        EmSetupPersistence.SaveToFile(cem, new EmSetup
        {
            Name = "via3d", LayoutRef = "via/layout/via.clay",
            Frequency = new FrequencySpec("0.1", "20", 200, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolver.Palace,
        });
        return new Workspace(ws, cem, Path.Combine(ws, "via", "em", "via.cem"));
    }

    /// <summary>The problem generated in this process straight from the documents — the reader, the
    /// resolver and the generator, not the verb.</summary>
    private static (Em3dProblem Problem, IReadOnlyDictionary<string, Em3dObjectOrigin> Origins, Technology Tech)
        InProcess(string cem)
    {
        var setup = EmSetupPersistence.LoadFromFile(cem);
        var resolution = EmSetupResolver.Resolve(cem, setup.LayoutRef,
                                                 CircuitRF.Design.Workspace.WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(cem)),
                                                 new TechnologyCache());
        var tech = resolution.Source!.Technology!;
        var result = Em3dGenerator.Generate(setup, resolution.Source, tech);
        Assert.True(result.Ok, result.Refusal);
        return (result.Problem!, result.Origins, tech);
    }

    private static string StripSkiaIds(string svg)
        => System.Text.RegularExpressions.Regex.Replace(svg, @"\b(cl|img|gr|fp)_[0-9a-z]+\b", "$1_N");

    private static byte[] Svg(int w, int h, Action<SKCanvas> draw)
    {
        using var stream = new SKDynamicMemoryWStream();
        using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, w, h), stream)) draw(canvas);
        return Encoding.UTF8.GetBytes(SvgFontNormalizer.RepairPositionLists(
            Encoding.UTF8.GetString(stream.DetachAsData().ToArray())));
    }

    private static byte[] Pdf(int w, int h, Action<SKCanvas> draw)
    {
        using var stream = new SKDynamicMemoryWStream();
        using (var doc = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Creator = "circuitRF" }))
        {
            draw(doc.BeginPage(w, h));
            doc.EndPage();
            doc.Close();
        }
        return stream.DetachAsData().ToArray();
    }

    // ── driving the verb ────────────────────────────────────────────────────────────────────────

    private string RefusalCode(params string[] args) => Refusal(args).Code;

    /// <summary>The first error the verb reported, run as a process with --json, and that it exited 1.</summary>
    private (string Code, string Message) Refusal(params string[] args)
    {
        var (exit, stdout, stderr) = RunCli([.. args, "--json"]);
        output.WriteLine(stdout + stderr);
        Assert.Equal(1, exit);
        var error = JsonNode.Parse(stdout)!["diagnostics"]!.AsArray()
                            .First(d => (string?)d!["severity"] == "error")!;
        return ((string)error["id"]!, (string)error["message"]!);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static int InProcessCli(params string[] args)
    {
        JsonRun.Reset();
        return CliEntry.Run(args);
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(Em3dRenderExplainTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "circuitrf.slnx"))) return dir;
        throw new InvalidOperationException("repo root not found");
    }

    private static Point3 Sub(Point3 a, Point3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static double Dot(Point3 a, Point3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static Point3 Unit(Point3 a) { double l = Math.Sqrt(Dot(a, a)); return new(a.X / l, a.Y / l, a.Z / l); }
}
