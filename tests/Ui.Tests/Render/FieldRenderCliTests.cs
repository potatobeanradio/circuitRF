// brief-em3d-84 §7 — `circuitrf render <c3d> --field <plot>` and `--list-fields`, the verb run as a PROCESS on the committed
// cavity fixture (testdata/em3d/fields/cavity, TE101 at 8.88 GHz), re-listed where a gate needs a driven run exactly as
// brief 83's FieldPlotTests re-list it:
//
//    1  a ClipPlane plot draws the triangle count and colour range the 3D view builds for the same plot
//    2  a PNG pixel, inverted through the map and the range, reads the field's value; at a wall E is tangential to, the bottom
//    3  a run re-listed with one more frequency below the plot's renders the same PNG bytes (the solution is picked by value)
//    4  a frequency the run did not save refuses with brief 83's sentence, and writes nothing
//    5  a stale run draws, and says so
//    6  each refusal has its own diagnostic, exits 1 and writes nothing
//    7  src/Ui keeps no copy of the resolution, and src/Cli slices and ranges nothing of its own (source scans)
//    8  --list-fields lists every plot, a missing one with its sentence, and exits 0
//    9  --phase draws the instant asked for, and the legend says which
//   10  a thinned vector slice draws fewer pieces covering the same area; --no-thin and PNG draw every triangle
//   11  brief-em3d-89 — a Surfaces plot of |E| on the cavity's region, seen from the front, draws that region's boundary with
//       the view's range, and a pixel on the front wall reads the field there
//   12  brief-em3d-100 — a plot's drive is printed with no flag, as the line the 3D view's legend prints for it
//   13  brief-dfr34-3 — a section through two points cuts the field on that oblique plane, and the CLI draws the triangles and
//       range the 3D view builds for it
//   14  brief-dfr34-3 — an axis plot written before reads and writes back byte for byte, and its plane is the axis plane
//   15  brief-dfr34-3 — Section Through… makes the plot, sets the clip plane to its plane and faces it; one undo takes all three

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.ThreeD;
using CircuitRF.Ui.Tests.Viewer3D;
using CircuitRF.Ui.ThreeD;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Render;

[Collection(Viewer3DCollection.Name)]
public sealed class FieldRenderCliTests(ITestOutputHelper output) : IDisposable
{
    private const long Um = 1000;                                            // DBU per micron
    private const double A = 22.86e-3, B = 10.16e-3, D = 25e-3;              // the cavity, metres
    private const double Margin = 0.05;
    private const int W = 800, H = 600;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-em84-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();
    private readonly List<C3dEditorViewModel> _open = [];

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static string Cavity => Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "fields", "cavity");

    // ── 1. the same slice as the 3D view ────────────────────────────────────────────────────

    [Fact]
    public void Gate1_AClipPlanePlot_DrawsTheTrianglesAndRangeThe3DViewBuilds()
    {
        string c3d = Workspace(Eigenmode(), [MidZ("Field1")]);
        Run(c3d);
        var field = Field(RenderJson(c3d, "cut.png", "--field", "Field1"));

        var vm = Open(c3d);
        Until(() => vm.Viewer.Plot?.Name == "Field1" && vm.Viewer.FieldScale is not null && vm.Viewer.FieldGeometry.Vertices.Length > 0,
              "the 3D view never drew Field1");
        Thread.Sleep(150);
        while (_posted.TryDequeue(out var a)) a();
        output.WriteLine($"CLI {field.GetProperty("triangles").GetInt32()} triangles; view {vm.Viewer.FieldGeometry.Vertices.Length / 3}");
        Assert.Equal(vm.Viewer.FieldGeometry.Vertices.Length / 3, field.GetProperty("triangles").GetInt32());
        Assert.Equal(vm.Viewer.FieldScale!.Lo, field.GetProperty("range").GetProperty("lo").GetDouble());
        Assert.Equal(vm.Viewer.FieldScale!.Hi, field.GetProperty("range").GetProperty("hi").GetDouble());
    }

    // ── 2. value, not colour ────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_ThePngPixel_ReadsTheFieldsValue_AndIsTheMapsBottomAtAWallETangentTo()
    {
        string c3d = Workspace(Eigenmode(), [MidZ("Field1")]);
        Run(c3d);
        string png = Path.Combine(_root, "cut.png");
        var field = Field(RenderJson(c3d, png, "--field", "Field1", "--no-legend"));
        double lo = field.GetProperty("range").GetProperty("lo").GetDouble(), hi = field.GetProperty("range").GetProperty("hi").GetDouble();
        using var bmp = SKBitmap.Decode(png);

        var (page, _) = Page(c3d);
        double Read(double x, double y)
        {
            var p = page.Map(new Uv(x, y));
            return lo + Invert(ColorMap3D.Viridis, bmp.GetPixel((int)Math.Round(p.X), (int)Math.Round(p.Y))) * (hi - lo);
        }
        // At the centre and a quarter across, what the field data says (FieldSampler: the element's own shape functions).
        foreach (double x in (double[])[A / 2, A / 4])
        {
            double want = Math.Min(Sample(new FieldQuantity(Info("E"), false, FieldMode.Peak), x, B / 2, D / 2, 0), hi);
            double got = Read(x, B / 2);
            output.WriteLine($"x = {x * 1e3:G4} mm: pixel reads {got:G5}, the field {want:G5} (range {lo:G4} … {hi:G4})");
            Assert.True(Math.Abs(got - want) <= 0.02 * hi, $"at x = {x}: read {got}, the field is {want}");
        }
        // At x = 0 E (along y) is tangential to the wall: the first pixel inside it reads the data there, in the bottom of the
        // map. Not exactly its bottom colour — Palace's node-averaged output holds 88 V/m on this wall (3.4% of the top).
        var edge = page.Map(new Uv(0, B / 2));
        var inside = bmp.GetPixel((int)Math.Round(edge.X) + 1, (int)Math.Round(edge.Y));
        double wall = lo + Invert(ColorMap3D.Viridis, inside) * (hi - lo);
        double wallField = Sample(new FieldQuantity(Info("E"), false, FieldMode.Peak), 1 / page.Scale, B / 2, D / 2, 0);
        output.WriteLine($"one pixel inside the wall: reads {wall:G4}, the field {wallField:G4}");
        Assert.True(Math.Abs(wall - wallField) <= 0.02 * hi && wall <= 0.05 * hi, $"at the wall the pixel reads {wall}, the field {wallField}");
    }

    // ── 3. by value ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_ARunReListedWithOneMoreFrequencyBelow_RendersTheSameBytes()
    {
        string c3d = Workspace(Driven(), [MidZ("Field1", new C3dFieldSolution { GHz = 10 })]);
        DrivenRun(c3d, [(2, 1), (10, 3)]);
        string first = Path.Combine(_root, "a.png"), second = Path.Combine(_root, "b.png");
        Assert.Equal(0, Cli("render", c3d, "-o", first, "--field", "Field1").Exit);
        DrivenRun(c3d, [(2, 1), (6, 2), (10, 3)]);                               // index 1 is 6 GHz now; 10 GHz is the same step
        Assert.Equal(0, Cli("render", c3d, "-o", second, "--field", "Field1").Exit);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    // ── 4. missing data refuses ─────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_AFrequencyTheRunDidNotSave_RefusesWithBrief83sSentence_AndWritesNothing()
    {
        string c3d = Workspace(Driven(), [MidZ("Field1", new C3dFieldSolution { GHz = 6 })]);
        DrivenRun(c3d, 2, 10);
        string png = Path.Combine(_root, "cut.png");
        var (exit, _, stderr) = Cli("render", c3d, "-o", png, "--field", "Field1");
        output.WriteLine(stderr);
        Assert.Equal(1, exit);
        Assert.Contains("The run saved 2, 10 GHz; this plot shows 6 GHz. Pick a saved frequency, or add 6 to Save fields at and run again.", stderr);
        Assert.False(File.Exists(png));
    }

    // ── 5. stale draws, and says so ─────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_AStaleRun_DrawsAndSaysSo()
    {
        string c3d = Workspace(Eigenmode(), [MidZ("Field1")]);
        string dir = Run(c3d);
        var doc = C3dPersistence.LoadFromFile(c3d);
        ((C3dBox)doc.Objects[0]).Size = new C3dPoint3(22000 * Um, 10160 * Um, 25000 * Um);     // the model moved on
        C3dPersistence.SaveToFile(c3d, doc);

        string json = RenderJson(c3d, "cut.png", "--field", "Field1", out string stderr);
        Assert.True(Field(json).GetProperty("stale").GetBoolean());
        Assert.Equal(dir, Field(json).GetProperty("run").GetString());
        Assert.Contains("note: field plot 'Field1': the model has changed since the run", stderr);
    }

    /// <summary>brief-em3d-87 — the model unchanged and its technology edited: the note names the technology.</summary>
    [Fact]
    public void Gate5b_ARunWhoseTechnologyChanged_NamesIt()
    {
        string c3d = Workspace(Eigenmode(), [MidZ("Field1")]);
        Run(c3d);
        string tech = Path.Combine(_root, "ws", "tech.ctech");
        var t = TechPersistence.LoadFromFile(tech);
        t.Materials![0].Epsr = 1.0006;
        TechPersistence.SaveToFile(tech, t);

        RenderJson(c3d, "cut.png", "--field", "Field1", out string stderr);
        Assert.Contains("note: field plot 'Field1': 'tech.ctech' has changed since the run", stderr);
    }

    // ── 6. refusals ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_EachRefusal_HasItsOwnDiagnostic_ExitsOne_AndWritesNothing()
    {
        var surfaces = MidZ("Surf");
        surfaces.On = C3dFieldPlotOn.Surfaces;
        string c3d = Workspace(Eigenmode(), [MidZ("Field1"), surfaces]);
        Run(c3d);
        string clay = Path.Combine(_root, "board.clay"), cem = Path.Combine(_root, "setup.cem");
        File.WriteAllText(clay, "");
        File.WriteAllText(cem, "");
        string png = Path.Combine(_root, "never.png");
        (string Id, string[] Args)[] cases =
        [
            ("render.field.unknown",             ["render", c3d, "-o", png, "--field", "Field9"]),
            ("render.field.view-disagrees",      ["render", c3d, "-o", png, "--field", "Field1", "--section", "z=5mm"]),
            // brief-em3d-89 — a Surfaces plot draws, and asks for what the 3D view has and a file does not
            ("render.field.direction-required",  ["render", c3d, "-o", png, "--field", "Surf"]),
            ("render.field.not-a-section",       ["render", c3d, "-o", png, "--field", "Surf", "--section", "z=5mm"]),
            ("render.field.view-disagrees",      ["render", c3d, "-o", png, "--field", "Field1", "--view-dir", "top"]),
            ("render.field.view-dir-malformed",  ["render", c3d, "-o", png, "--field", "Surf", "--view-dir", "up"]),
            ("render.field.surface-png-only",    ["render", c3d, "-o", Path.Combine(_root, "never.svg"), "--field", "Surf", "--iso"]),
            ("render.field.region-required",     ["render", c3d, "-o", png, "--field", "Surf", "--iso"]),
            ("render.field.region-unknown",      ["render", c3d, "-o", png, "--field", "Surf", "--iso", "--region", "lid"]),
            ("render.field.scale-bar-oblique",   ["render", c3d, "-o", png, "--field", "Surf", "--iso", "--scale-bar"]),
            ("render.field.option-not-applicable", ["render", c3d, "-o", png, "--field", "Surf", "--iso", "--region", "cavity", "--no-mirror"]),
            ("render.field.phase-not-animated",  ["render", c3d, "-o", png, "--field", "Field1", "--phase", "90"]),
            ("render.field.not-3d-view",         ["render", clay, "-o", png, "--field", "Field1"]),
            ("render.field.on-cem",              ["render", cem, "-o", png, "--field", "Field1"]),
        ];
        foreach (var (id, args) in cases)
        {
            var (exit, stdout, stderr) = Cli([.. args, "--json"]);
            output.WriteLine($"{id}: {stderr.Trim()}");
            Assert.Equal(1, exit);
            Assert.Matches($"\"id\":\\s*\"{Regex.Escape(id)}\"", stdout);
            Assert.False(File.Exists(png), id + " wrote a file");
        }
        Assert.False(File.Exists(Path.Combine(_root, "never.svg")));
        Assert.Contains("Its field plots: Field1, Surf.", Cli("render", c3d, "-o", png, "--field", "Field9").StdErr);
    }

    // ── 7. one copy ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_UiDefinesNoResolution_AndCliSlicesAndRangesNothingOfItsOwn()
    {
        string repo = PalaceBackendTests.RepoRoot();
        foreach (string f in Directory.EnumerateFiles(Path.Combine(repo, "src", "Ui"), "*.cs", SearchOption.AllDirectories))
        {
            string code = StripComments(File.ReadAllText(f));
            foreach (string name in (string[])["SolutionKey", "PickSolution", "PlotProblem", "RunDirectories"])
                Assert.False(Regex.IsMatch(code, $@"\b(static|public|private|internal)\b[^;={{}}()]*\b{name}\s*\("),
                             $"{Path.GetRelativePath(repo, f)} defines {name}");
            // brief-em3d-89 — what a Surfaces or Faces plot draws is built by FieldSurfacePlot alone, for the window and `render`
            foreach (string call in (string[])["FieldSurfaces.Exterior(", "FieldSurfaces.RegionBoundary(", "FieldSurfaces.Boundary(", "new FieldFacePainter("])
                Assert.False(code.Contains(call, StringComparison.Ordinal), $"{Path.GetRelativePath(repo, f)} builds a field surface itself: {call}");
        }
        foreach (string f in Directory.EnumerateFiles(Path.Combine(repo, "src", "Cli"), "*.cs", SearchOption.AllDirectories))
        {
            string code = StripComments(File.ReadAllText(f));
            Assert.DoesNotContain("FieldSlicer", code);
            Assert.DoesNotContain("FieldColorScale.", code);
            Assert.DoesNotContain("FieldMeshTets", code);
            Assert.DoesNotContain("FieldSurfaces.", code);
            Assert.DoesNotContain("FieldFacePainter", code);
        }
    }

    // ── 8. --list-fields ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_ListFields_ListsBothPlots_TheMissingOneWithItsSentence()
    {
        string c3d = Workspace(Eigenmode(), [MidZ("Field1"), MidZ("Mode7", new C3dFieldSolution { Mode = 7 })]);
        Run(c3d);
        var (exit, stdout, stderr) = Cli("render", c3d, "--list-fields", "--json");
        Assert.True(exit == 0, stderr);
        var plots = JsonDocument.Parse(stdout).RootElement.GetProperty("result").GetProperty("fieldPlots").EnumerateArray().ToList();
        Assert.Equal(["Field1", "Mode7"], plots.Select(p => p.GetProperty("name").GetString()));
        Assert.True(plots[0].GetProperty("dataPresent").GetBoolean());
        Assert.False(plots[1].GetProperty("dataPresent").GetBoolean());
        Assert.StartsWith("The run saved modes 1, 2, 3; this plot shows mode 7.", plots[1].GetProperty("problem").GetString());
        Assert.Contains("data: missing — The run saved modes", Cli("render", c3d, "--list-fields").StdOut);
    }

    // ── 9. phase ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate9_Phase_DrawsTheInstantAsked_AndTheLegendSaysWhich()
    {
        var plot = MidZ("Wave");
        plot.Mode = nameof(FieldMode.Instantaneous);
        string c3d = Workspace(Eigenmode(), [plot]);
        Run(c3d);
        string at0 = Path.Combine(_root, "p0.png"), at90 = Path.Combine(_root, "p90.png");
        Field(RenderJson(c3d, at0, "--field", "Wave", "--phase", "0", "--no-legend"));
        var field = Field(RenderJson(c3d, at90, "--field", "Wave", "--phase", "90", "--no-legend"));
        Assert.NotEqual(File.ReadAllBytes(at0), File.ReadAllBytes(at90));
        Assert.Equal(90, field.GetProperty("phase").GetDouble());

        double lo = field.GetProperty("range").GetProperty("lo").GetDouble(), hi = field.GetProperty("range").GetProperty("hi").GetDouble();
        using var bmp = SKBitmap.Decode(at90);
        var p = Page(c3d).Page.Map(new Uv(A / 2, B / 2));
        double got = lo + Invert(ColorMap3D.Viridis, bmp.GetPixel((int)Math.Round(p.X), (int)Math.Round(p.Y))) * (hi - lo);
        double want = Math.Clamp(Sample(new FieldQuantity(Info("E"), false, FieldMode.Instantaneous), A / 2, B / 2, D / 2, Math.PI / 2), lo, hi);
        output.WriteLine($"φ = 90°: pixel reads {got:G5}, Evaluate(ch, π/2) {want:G5} (range {lo:G4} … {hi:G4})");
        Assert.True(Math.Abs(got - want) <= 0.02 * hi, $"read {got}, the field at 90° is {want}");

        string svg = Path.Combine(_root, "p90.svg");
        Assert.Equal(0, Cli("render", c3d, "-o", svg, "--field", "Wave", "--phase", "90").Exit);
        Assert.Contains("φ = 90°", File.ReadAllText(svg));
    }

    // ── 10. thinning ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate10_AThinnedSlice_DrawsFewerPiecesOverTheSameArea_AndNoThinOrPngDrawsEveryTriangle()
    {
        string c3d = Workspace(Eigenmode(), [MidZ("Field1")]);
        Run(c3d);
        var env = new Dictionary<string, string> { ["CRF_FIELD_THIN_LIMIT"] = "0" };
        string svg = Path.Combine(_root, "thin.svg");
        var thin = Field(RenderJson(c3d, svg, ["--field", "Field1"], env, out _));
        int triangles = thin.GetProperty("triangles").GetInt32(), drawn = thin.GetProperty("trianglesDrawn").GetInt32();
        int paths = Regex.Matches(File.ReadAllText(svg), "<path").Count;
        output.WriteLine($"{triangles} triangles, drawn as {drawn}; {paths} <path> elements; {new FileInfo(svg).Length:N0} bytes");
        Assert.True(drawn < triangles);
        Assert.True(paths < triangles);

        string full = Path.Combine(_root, "full.svg");
        var noThin = Field(RenderJson(c3d, full, ["--field", "Field1", "--no-thin"], env, out _));
        Assert.Equal(triangles, noThin.GetProperty("trianglesDrawn").GetInt32());
        output.WriteLine($"un-thinned: {new FileInfo(full).Length:N0} bytes");
        Assert.Equal(triangles, Field(RenderJson(c3d, Path.Combine(_root, "all.png"), ["--field", "Field1"], env, out _))
                                     .GetProperty("trianglesDrawn").GetInt32());

        // The same area: the pieces' loops against the triangles', in process on the same cut.
        var run = FieldRun.OpenPalace(Cavity, GmshGeoWriter.LengthUnitM)!;
        var step = FieldStep.Open(run.Solutions[0].VolumePvtu!, run.ToMetres);
        var q = FieldQuantity.Offered(step.Arrays, []).First(x => x.Array.Name == "E" && x.Mode == FieldMode.Peak);
        var origin = (A / 2, B / 2, D / 2);
        var cut = FieldSection.Cut(q, step, origin, new ClipPlane3D { Enabled = true, Axis = ClipAxis3D.Z, Offset = 0 }, false, 99)!;
        var layer = Em3dSectionField.Build(cut, 0, raster: false, thinAbove: 0, []);
        double pieces = layer.Pieces.Sum(p => p.Area), tris = 0;
        for (int k = 0; k < layer.Triangles; k++)
            tris += Math.Abs(Em3dSectionField.SignedArea([layer.Vertices[3 * k], layer.Vertices[3 * k + 1], layer.Vertices[3 * k + 2]]));
        output.WriteLine($"area: pieces {pieces:G12} m², triangles {tris:G12} m², {layer.Pieces.Count} pieces");
        Assert.True(Math.Abs(pieces - tris) <= 1e-9 * tris, $"pieces cover {pieces}, the triangles {tris}");
    }

    // ── 11. a Surfaces plot (brief-em3d-89) ─────────────────────────────────────────────────

    [Fact]
    public void Gate11_ASurfacesPlot_FromTheFront_DrawsTheRegionsBoundaryWithTheViewsRange_AndAWallPixelReadsTheField()
    {
        var surf = MidZ("Surf");
        surf.On = C3dFieldPlotOn.Surfaces;
        string c3d = Workspace(Eigenmode(), [surf]);
        Run(c3d);
        string png = Path.Combine(_root, "front.png");
        var root = JsonDocument.Parse(RenderJson(c3d, png, "--field", "Surf", "--view-dir", "front", "--region", "cavity", "--no-legend"))
                               .RootElement.GetProperty("result").GetProperty("render");
        var field = root.GetProperty("em3d").GetProperty("field");
        Assert.Equal("projection", root.GetProperty("em3d").GetProperty("view").GetString());

        // the region's boundary and its range, as FieldSurfacePlot builds them for the 3D view
        var run = FieldRun.OpenPalace(Cavity, GmshGeoWriter.LengthUnitM)!;
        var step = FieldStep.Open(run.Solutions[0].VolumePvtu!, run.ToMetres);
        var q = FieldQuantity.Offered(step.Arrays, []).First(x => x.Array.Name == "E" && x.Mode == FieldMode.Peak);
        var region = FieldGroups.Read(Cavity).Where(g => g.Name == "cavity" && g.Dimension == 3).Select(g => g.Attribute).ToHashSet();
        var boundary = FieldSurfaces.RegionBoundary(step.Mesh, step.Load("E")!, region, (A / 2, B / 2, D / 2));
        var scale = FieldColorScale.Auto(q, [boundary], false, 99);
        output.WriteLine($"CLI {field.GetProperty("triangles").GetInt32()} triangles, {field.GetProperty("range").GetProperty("lo").GetDouble():G6} … " +
                         $"{field.GetProperty("range").GetProperty("hi").GetDouble():G6}; the region's boundary {boundary.TriangleCount}, {scale.Lo:G6} … {scale.Hi:G6}");
        Assert.Equal(boundary.TriangleCount, field.GetProperty("triangles").GetInt32());
        Assert.Equal(scale.Lo, field.GetProperty("range").GetProperty("lo").GetDouble(), 1e-9 * scale.Hi);
        Assert.Equal(scale.Hi, field.GetProperty("range").GetProperty("hi").GetDouble(), 1e-9 * scale.Hi);

        // from the front the picture is (x, z), framed on the box: the y = 0 wall's centre reads |E| there (normal to it, at its peak)
        var frame = new Em3dScene(new Em3dView(Em3dViewKind.Projection, 0), 0, 0, [], [], [], [], new Uv(0, 0), new Uv(A, D), [], []);
        var page = Em3dSectionRenderer.Layout(W, H, frame, Margin);
        using var bmp = SKBitmap.Decode(png);
        var p = page.Map(new Uv(A / 2, D / 2));
        double got = scale.Lo + Invert(ColorMap3D.Viridis, bmp.GetPixel((int)p.X, (int)p.Y)) * (scale.Hi - scale.Lo);
        double want = Math.Min(Sample(q, A / 2, 1e-6, D / 2, 0), scale.Hi);
        output.WriteLine($"front wall's centre: pixel {got:G5}, the field {want:G5}");
        Assert.True(Math.Abs(got - want) <= 0.03 * scale.Hi, $"read {got}, the field is {want}");
    }

    // ── the fixture ─────────────────────────────────────────────────────────────────────────

    // ── 12. brief-em3d-100 — the plot's drive ───────────────────────────────────────────────

    /// <summary>brief-em3d-100 gate 8 — a plot at 10 W: `render --field` prints the drive line, with no flag, exactly as the 3D
    /// view's legend (FieldPlotResolver.LegendLines) prints it for the same plot in-process.</summary>
    [Fact]
    public void Gate12_ThePlotsDrive_IsTheLegendLine_TheViewPrints()
    {
        var plot = MidZ("Hot", new C3dFieldSolution { GHz = 10 });
        plot.DrivePowerW = 10;
        string c3d = Workspace(Driven(), [plot]);
        DrivenRun(c3d, 10);
        string svg = Path.Combine(_root, "hot.svg");
        var r = Cli("render", c3d, "-o", svg, "--field", "Hot");
        Assert.True(r.Exit == 0, r.StdErr);

        var vm = Open(c3d);
        Until(() => vm.Viewer.LayerNamed("Hot") is { Builds: > 0, Building: false, Scale: not null }, "the plot was never drawn");
        string line = Assert.Single(vm.Viewer.FieldLegendLines(), l => l.StartsWith("Drive: ", StringComparison.Ordinal));
        Assert.Equal("Drive: 10 W incident (available), peak", line);
        Assert.Contains(line, File.ReadAllText(svg));
    }

    // ── 13–15. brief-dfr34-3 — a section through any two points ─────────────────────────────

    /// <summary>A section across the cavity's plan diagonal-ish: from a corner region to the far side, at no axis angle.</summary>
    private static C3dFieldPlot Diagonal(string name) => new()
    {
        Name = name, Solution = new C3dFieldSolution { Mode = 1 }, Quantity = "E", Mode = nameof(FieldMode.Peak), On = C3dFieldPlotOn.ClipPlane,
        Through = new C3dFieldPlotThrough { From = new C3dPoint2(2000 * Um, 1000 * Um), To = new C3dPoint2(20000 * Um, 9000 * Um) },
    };

    [Fact]
    public void Gate13_ASectionThroughTwoPoints_CutsOnThatPlane_AndTheCliDrawsWhatTheViewBuilds()
    {
        string c3d = Workspace(Eigenmode(), [Diagonal("Cut")]);
        Run(c3d);
        string png = Path.Combine(_root, "cut.png");
        var field = Field(RenderJson(c3d, png, "--field", "Cut"));
        Assert.Equal("ClipPlane", field.GetProperty("on").GetString());

        var vm = Open(c3d);
        Until(() => vm.Viewer.Plot?.Name == "Cut" && vm.Viewer.FieldScale is not null && vm.Viewer.FieldGeometry.Vertices.Length > 0,
              "the 3D view never drew Cut");
        Thread.Sleep(150);
        while (_posted.TryDequeue(out var a)) a();
        // every vertex the view drew is on the plane through the two points, which is at neither axis angle
        var line = FieldSectionLine.Of(vm.Document.FieldPlots[0], vm.Document.DbuPerMicron)!.Value;
        var (nx, ny) = line.Normal;
        Assert.True(Math.Abs(nx) > 0.3 && Math.Abs(ny) > 0.3, $"the normal ({nx}, {ny}) is on an axis");
        var origin = vm.Viewer.Scene.Origin;
        double worst = vm.Viewer.FieldGeometry.Vertices.Max(v => Math.Abs(line.Across(v.X + origin.X, v.Y + origin.Y)));
        output.WriteLine($"CLI {field.GetProperty("triangles").GetInt32()} triangles; view {vm.Viewer.FieldGeometry.Vertices.Length / 3}; " +
                         $"farthest vertex off the plane {worst:G3} m");
        Assert.True(worst < 1e-6, $"a vertex is {worst} m off the plane");
        Assert.Equal(vm.Viewer.FieldGeometry.Vertices.Length / 3, field.GetProperty("triangles").GetInt32());
        Assert.Equal(vm.Viewer.FieldScale!.Lo, field.GetProperty("range").GetProperty("lo").GetDouble());
        Assert.Equal(vm.Viewer.FieldScale!.Hi, field.GetProperty("range").GetProperty("hi").GetDouble());
        Assert.True(new FileInfo(png).Length > 0);
    }

    [Fact]
    public void Gate14_AnAxisPlotWrittenBefore_ReadsAndWritesBackByteForByte_OnItsAxisPlane()
    {
        string before = C3dPersistence.SerializeFieldPlots([MidZ("Field1")]);
        Assert.DoesNotContain("Through", before);
        var read = C3dPersistence.DeserializeFieldPlots(before);
        Assert.Null(read[0].Through);
        Assert.Equal(before, C3dPersistence.SerializeFieldPlots(read));
        var plane = FieldPlotResolver.ScenePlane(read[0], (int)Um, (1e-3, 2e-3, 3e-3));
        Assert.Equal(ClipAxis3D.Z, plane.Axis);
        Assert.Equal((float)(12.5e-3 - 3e-3), plane.Offset);
        // and a section's two points survive the same round trip
        string section = C3dPersistence.SerializeFieldPlots([Diagonal("Cut")]);
        var back = C3dPersistence.DeserializeFieldPlots(section)[0].Through!;
        Assert.Equal((new C3dPoint2(2000 * Um, 1000 * Um), new C3dPoint2(20000 * Um, 9000 * Um)), (back.From, back.To));
    }

    [Fact]
    public void Gate15_SectionThrough_MakesThePlot_ClipsOnItsPlane_FacesIt_AndOneUndoTakesAllThreeBack()
    {
        string c3d = Workspace(Eigenmode(), [MidZ("Field1")]);
        var vm = Open(c3d);
        var clipBefore = vm.Viewer.CurrentClip;
        var cameraBefore = vm.Viewer.CurrentCamera;
        int plots = vm.Document.FieldPlots.Count;

        Assert.Null(vm.CommitSectionThrough(new C3dPoint3(2000 * Um, 1000 * Um, 0), new C3dPoint3(20000 * Um, 9000 * Um, 10000 * Um)));
        var made = vm.Document.FieldPlots[^1];
        Assert.Equal(plots + 1, vm.Document.FieldPlots.Count);
        Assert.NotNull(made.Through);
        var clip = vm.Viewer.View.Clip;
        Assert.True(clip.Enabled);
        Assert.Equal(ClipAxis3D.Section, clip.Axis);
        // the clip plane is the plot's plane, and the side it keeps is the one away from the camera
        var line = FieldSectionLine.Of(made, vm.Document.DbuPerMicron)!.Value;
        var plotPlane = line.ScenePlane(vm.Viewer.Scene.Origin);
        Assert.True(System.Numerics.Vector3.Distance(clip.Unflipped, plotPlane.Unflipped) < 1e-5f);
        Assert.Equal(plotPlane.Offset, clip.Offset, 1e-6f);
        var cam = vm.Viewer.View.Camera;
        Assert.Equal(0, cam.Pitch, 1e-6f);
        Assert.True(System.Numerics.Vector3.Dot(cam.Back, clip.Unflipped) < -0.999f, "the camera does not face the plane");
        Assert.False(clip.Keeps(cam.Eye), "the side toward the camera was kept");

        vm.UndoRedo.Undo();
        while (_posted.TryDequeue(out var a)) a();
        Assert.Equal(plots, vm.Document.FieldPlots.Count);
        Assert.Equal(clipBefore, vm.Viewer.CurrentClip);
        Assert.Equal(cameraBefore.Yaw, vm.Viewer.View.Camera.Yaw);
        Assert.Equal(cameraBefore.Pitch, vm.Viewer.View.Camera.Pitch);
        Assert.Equal(cameraBefore.Distance, vm.Viewer.View.Camera.Distance);
        Assert.Equal(cameraBefore.Target, vm.Viewer.View.Camera.Target);
    }

    private static C3dFieldPlot MidZ(string name, C3dFieldSolution? solution = null) => new()
    {
        Name = name, Solution = solution ?? new C3dFieldSolution { Mode = 1 }, Quantity = "E", Mode = nameof(FieldMode.Peak),
        On = C3dFieldPlotOn.ClipPlane, Axis = C3dAxis.Z, Offset = 12500 * Um,
    };

    private static EmSetup Eigenmode()
    {
        var none = new EmAirBoxFace(0, null);
        return new EmSetup
        {
            Name = "modes", Solver3D = Em3dSolverId.Palace, Problem3D = Em3dProblemType.Eigenmode,
            AirBox = new EmAirBox(none, none, none, none, none, none), Eigenmode = new EmEigenmode3D(1, 5),
            Frequency = new CircuitRF.Core.Design.FrequencySpec("5", "15", 3, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz"),
        };
    }

    private static EmSetup Driven()
    {
        var none = new EmAirBoxFace(0, null);
        return new EmSetup
        {
            Name = "drive", Solver3D = Em3dSolverId.Palace, Problem3D = Em3dProblemType.Driven,
            AirBox = new EmAirBox(none, none, none, none, none, none),
            Frequency = new CircuitRF.Core.Design.FrequencySpec("2", "18", 9, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz"),
        };
    }

    /// <summary>A workspace holding the cavity as a .c3d with <paramref name="setup"/> embedded and <paramref name="plots"/>.</summary>
    private string Workspace(EmSetup setup, List<C3dFieldPlot> plots)
    {
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Air", Epsr = 1 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cavity", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cavity.c3d");
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            Objects = [new C3dBox { Name = "cavity", Material = "Air", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(22860 * Um, 10160 * Um, 25000 * Um) }],
            Setups = [EmSetupPersistence.ToEmbedded(setup)],
            FieldPlots = plots,
        });
        return path;
    }

    private string RunDir(string c3d)
    {
        var doc = C3dPersistence.LoadFromFile(c3d);
        var setup = C3dSetups.ForRun(C3dSetups.Read(doc).Single().Setup!, c3d);
        return Em3dRunService.RunDirectory(Path.Combine(_root, "ws", "results"), setup, Em3dSolverId.Palace);
    }

    /// <summary>The cavity fixture as the setup's finished run, keeping the document it solved as the 3D editor's Simulate does.</summary>
    private string Run(string c3d)
    {
        string dir = RunDir(c3d);
        if (!Directory.Exists(dir)) CopyDirectory(Cavity, dir);
        // brief-em3d-87 — the document and every file its elaboration read, as the run service keeps them
        var doc = C3dPersistence.LoadFromFile(c3d);
        C3dRunInputs.Take(doc, c3d, C3dElaborator.ElaborateOnce(doc, c3d, null).FilesRead).KeepIn(dir);
        return dir;
    }

    /// <summary>A driven run saving <paramref name="ghz"/>: the cavity's first steps re-listed as those frequencies (brief 83's gate 5).</summary>
    private void DrivenRun(string c3d, params double[] ghz) => DrivenRun(c3d, [.. ghz.Select((g, i) => (g, i + 1))]);

    /// <summary>A driven run saving each frequency from the cavity's step (Cycle) named beside it.</summary>
    private void DrivenRun(string c3d, (double GHz, int Cycle)[] steps)
    {
        string dir = RunDir(c3d);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        CopyDirectory(Cavity, dir);
        string pv = Path.Combine(dir, "postpro", "paraview");
        foreach (string part in (string[])["", "_boundary"])
        {
            string from = Path.Combine(pv, "eigenmode" + part), to = Path.Combine(pv, "driven" + part);
            Directory.Move(from, to);
            File.Delete(Path.Combine(to, "eigenmode" + part + ".pvd"));
            File.WriteAllText(Path.Combine(to, "driven" + part + ".pvd"),
                "<?xml version=\"1.0\"?>\n<VTKFile type=\"Collection\" version=\"2.2\" byte_order=\"LittleEndian\">\n<Collection>\n" +
                string.Concat(steps.Select(x => $"<DataSet timestep=\"{x.GHz.ToString(System.Globalization.CultureInfo.InvariantCulture)}\" group=\"\" part=\"0\" " +
                                                $"file=\"Cycle00000{x.Cycle}/data.pvtu\" name=\"mesh\"/>\n")) +
                "</Collection>\n</VTKFile>\n");
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string dst = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }
    }

    /// <summary>The page the verb lays the section out on: the same scene, from the same problem, at the same size and margin.</summary>
    private (Em3dPageLayout Page, Em3dScene Scene) Page(string c3d)
    {
        var doc = C3dPersistence.LoadFromFile(c3d);
        var setup = C3dSetups.ForRun(C3dSetups.Read(doc).Single().Setup!, c3d);
        var g = C3dProblemAssembly.Assemble(setup, doc, c3d, Path.Combine(_root, "ws", ".cws"));
        Assert.True(g.Problem is not null, g.Refusal);
        var scene = Em3dSectionScene.Build(g.Problem!, new Em3dView(Em3dViewKind.SectionZ, D / 2));
        return (Em3dSectionRenderer.Layout(W, H, scene, Margin), scene);
    }

    /// <summary>Where <paramref name="c"/> falls on <paramref name="map"/>, 0..1: the nearest of 4,096 samples.</summary>
    private static double Invert(ColorMap3D map, SKColor c)
    {
        double best = double.MaxValue, at = 0;
        for (int i = 0; i <= 4096; i++)
        {
            var (r, g, b) = map.Sample(i / 4096f);
            double d = (r - c.Red) * (r - c.Red) + (g - c.Green) * (g - c.Green) + (b - c.Blue) * (b - c.Blue);
            if (d < best) { best = d; at = i / 4096.0; }
        }
        return at;
    }

    private static FieldArrayInfo Info(string name)
    {
        var run = FieldRun.OpenPalace(Cavity, GmshGeoWriter.LengthUnitM)!;
        return FieldStep.Open(run.Solutions[0].VolumePvtu!, run.ToMetres).Info(name)!;
    }

    /// <summary><paramref name="q"/> of mode 1 at (x, y, z) world metres, from the field data.</summary>
    private static double Sample(FieldQuantity q, double x, double y, double z, double phase)
    {
        var run = FieldRun.OpenPalace(Cavity, GmshGeoWriter.LengthUnitM)!;
        var step = FieldStep.Open(run.Solutions[0].VolumePvtu!, run.ToMetres);
        var a = step.Load(q.Array.Name)!;
        Span<double> ch = stackalloc double[a.Info.Channels];
        double k = 1 / run.ToMetres;
        Assert.True(new FieldSampler(step.Mesh).Sample(a, x * k, y * k, z * k, ch), "the point is outside the mesh");
        return q.Evaluate(ch, phase);
    }

    // ── the verb, and the view it is compared with ──────────────────────────────────────────

    private C3dEditorViewModel Open(string path)
    {
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(_root, "ws", ".cws"), _posted.Enqueue)
        {
            ResultsRootProvider = () => Path.Combine(_root, "ws", "results"),
        };
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(400, 300);
        vm.Start();
        Until(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested && vm.Viewer.Scene.Objects.Length > 0, "the scene never settled");
        return vm;
    }

    private void Until(Func<bool> done, string what)
        => Assert.True(SpinWait.SpinUntil(() => { while (_posted.TryDequeue(out var a)) a(); return done(); }, TimeSpan.FromSeconds(30)), what);

    private static JsonElement Field(string json)
        => JsonDocument.Parse(json).RootElement.GetProperty("result").GetProperty("render").GetProperty("em3d").GetProperty("field").Clone();

    private string RenderJson(string c3d, string output, params string[] extra) => RenderJson(c3d, output, extra, null, out _);

    private string RenderJson(string c3d, string output, string a, string b, out string stderr) => RenderJson(c3d, output, [a, b], null, out stderr);

    private string RenderJson(string c3d, string output, string[] extra, IReadOnlyDictionary<string, string>? env, out string stderr)
    {
        string path = Path.IsPathRooted(output) ? output : Path.Combine(_root, output);
        var (exit, stdout, err) = Cli([.. new[] { "render", c3d, "-o", path, "--size", $"{W}x{H}", "--margin", Margin.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                                       .. extra, "--json"], env);
        stderr = err;
        Assert.True(exit == 0, err + stdout);
        return stdout;
    }

    private static (int Exit, string StdOut, string StdErr) Cli(params string[] args) => Cli(args, null);

    private static (int Exit, string StdOut, string StdErr) Cli(string[] args, IReadOnlyDictionary<string, string>? env)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = PalaceBackendTests.RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = typeof(FieldRenderCliTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }

    private static string StripComments(string code)
        => Regex.Replace(code, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
}
