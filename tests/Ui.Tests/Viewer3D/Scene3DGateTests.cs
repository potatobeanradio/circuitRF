// brief-em3d-28 §6 — the scene-model gates, headless: no GPU, no window. Counters, not timings.
//
//   2  batches: one draw per (object, material), independent of triangle count
//   4  superseded generations: two quick edits, the first generation's scene is never handed on
//   7  the .msh reader: node, tetrahedron and boundary counts equal Gmsh's own
//   8  grid lines: the drawn set equals FdtdGrid's, and the smallest-cell label its minimum spacing
//   9  picking: a CPU ray through a pixel hits what the (software) ID pass names
// plus the camera's standard views, which the toolbar's buttons set.

using System.Numerics;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Viewer3D;

[Collection(Viewer3DCollection.Name)]
public sealed class Scene3DGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-viewer3d-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    internal static Em3dGenerationResult CaseA(string dir)
    {
        var (setup, source) = PalaceProgressTests.CaseA(dir);
        var r = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(r.Ok, r.Refusal);
        return r;
    }

    internal static (Em3dGenerationResult, EmSetup) Via()
    {
        var (setup, source) = Em3dGeneratorTests.CaseB(plated: true);
        setup.AirBox = PalaceBackendTests.Padded(1500);
        var r = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(r.Ok, r.Refusal);
        return (r, setup);
    }

    // ── 2. batches ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_EveryObjectIsOneDraw_ForCaseA_AndTheViaTransition()
    {
        var (via, _) = Via();
        foreach (var g in new[] { CaseA(_root), via })
        {
            var scene = Scene3DBuilder.Build(g.Problem!, 1, g.Origins);
            var pairs = scene.Batches.Select(b => (b.ObjectId, b.MaterialSlot)).ToList();
            Assert.Equal(pairs.Count, pairs.Distinct().Count());
            Assert.Equal(scene.Batches.Length, scene.Batches.Select(b => b.ObjectId).Distinct().Count());
            // Every solid and sheet is in the scene, each as one batch.
            Assert.Equal(g.Problem!.Solids.Count + g.Problem.Sheets.Count + g.Problem.Ports.Count + 6, scene.Batches.Length);

            var view = new Viewer3DViewState { Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, 1.6f) };
            view.Adopt(scene, null);
            Array.Fill(view.Visible, true);
            var plan = new Scene3DFramePlan();
            plan.Plan(scene, view, 800, 500, false, false, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
            int triangleDraws = Enumerable.Range(0, plan.DrawCount).Count(i => plan.Draws[i].Pipeline is Scene3DPipeline.Opaque or Scene3DPipeline.Translucent);
            Assert.Equal(scene.Batches.Length, triangleDraws);
        }
    }

    [Fact]
    public void Gate2_TheDrawCountDoesNotDependOnTheTriangleCount()
    {
        int Draws(int outlineVertices)
        {
            var ring = Enumerable.Range(0, outlineVertices)
                                 .Select(k => new Point2(1e-3 * Math.Cos(2 * Math.PI * k / outlineVertices), 1e-3 * Math.Sin(2 * Math.PI * k / outlineVertices)))
                                 .ToList();
            var scene = Scene3DBuilder.Build(Disc(ring), 1);
            return scene.Batches.Length;
        }
        var few = Scene3DBuilder.Build(Disc([new(0, 0), new(1e-3, 0), new(0, 1e-3)]), 1);
        var many = Scene3DBuilder.Build(Disc([.. Enumerable.Range(0, 4000).Select(k => new Point2(1e-3 * Math.Cos(k * 2 * Math.PI / 4000), 1e-3 * Math.Sin(k * 2 * Math.PI / 4000)))]), 1);
        Assert.True(many.TriangleCount > 100 * few.TriangleCount);
        Assert.Equal(few.Batches.Length, many.Batches.Length);
        Assert.Equal(Draws(8), Draws(800));
    }

    /// <summary>A conductor disc (any ring) on a substrate, in an air box.</summary>
    internal static Em3dProblem Disc(IReadOnlyList<Point2> ring)
    {
        var lo = new Point3(-3e-3, -3e-3, 0); var hi = new Point3(3e-3, 3e-3, 2e-3);
        var pec = Em3dBoundaryKind.Pec;
        return new Em3dProblem(
            [new Em3dSolid("sub", "fr4", Em3dRole.Dielectric, new Em3dBox(lo, new Point3(hi.X, hi.Y, 0.5e-3)), 1),
             new Em3dSolid("air", "air", Em3dRole.Air, new Em3dBox(new Point3(lo.X, lo.Y, 0.5e-3), hi), 2),
             new Em3dSolid("disc", "cu", Em3dRole.Conductor, new Em3dExtrudedPolygon(ring, [], 0.5e-3, 0.535e-3), 3)],
            [],
            [new Em3dMaterial("fr4", 4.4, null, 0.02, 1, 0), new Em3dMaterial("air", 1, null, 0, 1, 0),
             new Em3dMaterial("cu", 1, null, 0, 1, 5.8e7)],
            [],
            new Em3dAirBox(lo, hi, new Em3dFaces(pec, pec, pec, pec, pec, pec)),
            new Em3dFrequency(1e9, 10e9, 10, Em3dSweepKind.Linear), 20);
    }

    // ── 4. superseded generations ───────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_TwoQuickEdits_TheFirstGenerationsSceneIsNeverHandedOn()
    {
        using var firstMayFinish = new ManualResetEventSlim(false);
        var handed = new List<long>();
        using var source = new Scene3DSource((gen, _, _) =>
        {
            if (gen == 1) firstMayFinish.Wait(TimeSpan.FromSeconds(10));    // ignores the token: dropped by NUMBER
            return Scene3DModel.Empty(gen);
        });
        using var done = new CountdownEvent(1);
        source.SceneReady += s => { lock (handed) handed.Add(s.Generation); done.Signal(); };

        source.Request();                          // edit 1: its build is still running …
        source.Request();                          // … when edit 2 arrives
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        firstMayFinish.Set();                      // now let generation 1 finish, late
        SpinWait.SpinUntil(() => source.Discarded == 1, TimeSpan.FromSeconds(10));

        Assert.Equal([2L], handed);
        Assert.Equal(1, source.Handed);
        Assert.Equal(1, source.Discarded);
        Assert.Equal(2, source.Current.Generation);
    }

    // ── 7. the .msh reader ──────────────────────────────────────────────────────────────────

    private static string Testdata(params string[] parts) => Path.Combine([PalaceBackendTests.RepoRoot(), "testdata", .. parts]);

    /// <summary>The counts Gmsh itself printed reading the mesh back (count.geo's Printf lines).</summary>
    private static (int Nodes, int Triangles, int Tets) GmshCounts(string log)
    {
        int Get(string key) => int.Parse(File.ReadLines(log).Single(l => l.StartsWith(key + " ", StringComparison.Ordinal))[(key.Length + 1)..]);
        return (Get("nodes"), Get("triangles"), Get("tetrahedra"));
    }

    [Theory]
    [InlineData("small-binary.msh")]
    [InlineData("small-ascii.msh")]
    public void Gate7_TheReadersCounts_AreGmshsOwn(string file)
    {
        var mesh = MshReader.Read(Testdata("em3d", "viewer", "small-mesh", file));
        var (nodes, tris, tets) = GmshCounts(Testdata("em3d", "viewer", "small-mesh", "gmsh-counts.log"));
        Assert.Equal((nodes, tris, tets), (mesh.NodeCount, mesh.TriangleCount, mesh.TetCount));
        Assert.Equal(file.Contains("binary"), mesh.Binary);
        Assert.Equal(["air", "outer_pec", "substrate", "via", "via_wall"], mesh.PhysicalNames.Values.Order());
    }

    [Fact]
    public void Gate7_F0CaseA_GivesGmshsCounts_AndPalacesElementCount()
    {
        // The 15.6 MB mesh is not committed; tools/Viewer3dSpike/README §1 regenerates it.
        string msh = Environment.GetEnvironmentVariable("CRF_F0_CASE_A_MSH")
                     ?? Path.Combine(PalaceBackendTests.RepoRoot(), "tools", "Viewer3dSpike", "data", "case.msh");
        if (!File.Exists(msh)) return;
        var mesh = MshReader.Read(msh);
        var (nodes, tris, tets) = GmshCounts(Testdata("em3d", "f0", "A-bondwire", "palace-round", "gmsh-counts.log"));
        Assert.Equal((nodes, tris, tets), (mesh.NodeCount, mesh.TriangleCount, mesh.TetCount));
        Assert.Equal(146_769, mesh.TetCount);   // palace.log's element total for this run (brief 21's summary)
    }

    [Fact]
    public void Gate7_AMalformedFile_RefusesWithTheLineNumber()
    {
        string text = File.ReadAllText(Testdata("em3d", "viewer", "small-mesh", "small-ascii.msh"));
        int at = text.IndexOf("$Elements", StringComparison.Ordinal);
        int line = text[..at].Count(c => c == '\n') + 1;      // the $Elements line; its count follows
        string broken = text[..at] + "$Elements\n12x\n" + text[(text.IndexOf('\n', text.IndexOf('\n', at) + 1) + 1)..];
        using var s = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(broken));
        var e = Assert.Throws<InvalidDataException>(() => MshReader.Read(s, "broken.msh"));
        Assert.Contains($"broken.msh, line {line + 1}", e.Message);
        Assert.Contains("'12x'", e.Message);
    }

    // ── 8. grid lines ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_TheDrawnLinesAreFdtdGrids_AndTheSmallestCellLabelIsItsMinimumSpacing()
    {
        var (g, setup) = Via();
        var grid = FdtdGrid.Build(g.Problem!, CemOpenEms.ResolveGrid(setup.OpenEms));
        var scene = Scene3DBuilder.Build(g.Problem!, 1, g.Origins);
        double zMid = g.Problem!.Solids.First(s => s.Role == Em3dRole.Dielectric) is { Primitive: Em3dBox sb }
            ? (sb.Min.Z + sb.Max.Z) / 2 : 0;
        var clip = new ClipPlane3D { Enabled = true, Axis = ClipAxis3D.Z, Offset = (float)(zMid - scene.Origin.Z) };
        var drawing = FdtdGridOverlay.Build(grid, scene, clip);

        // On the clip plane: lines along y sit at every x line, lines along x at every y line.
        var onPlane = Segments(drawing.Lines, scene).Where(s => Math.Abs(s.A.Z - zMid) < 1e-9 && Math.Abs(s.B.Z - zMid) < 1e-9).ToList();
        var xs = onPlane.Where(s => s.A.X == s.B.X && s.A.Y != s.B.Y).Select(s => s.A.X).ToList();
        var ys = onPlane.Where(s => s.A.Y == s.B.Y && s.A.X != s.B.X).Select(s => s.A.Y).ToList();
        AssertSameLines(grid.X.Lines, xs);
        AssertSameLines(grid.Y.Lines, ys);

        // On conductor surfaces: every segment lies in some grid plane.
        var metal = Segments(drawing.Lines, scene).Except(onPlane).ToList();
        Assert.NotEmpty(metal);
        foreach (var (a, b) in metal)
            Assert.True(OnAGridPlane(a, b, grid.X.Lines, p => p.X) || OnAGridPlane(a, b, grid.Y.Lines, p => p.Y) ||
                        OnAGridPlane(a, b, grid.Z.Lines, p => p.Z), $"segment {a} → {b} lies in no grid plane");

        foreach (var label in drawing.Labels)
            Assert.Equal(FdtdGridOverlay.MinSpacing(grid.Axis(label.Axis).Lines), label.SmallestCellM, 15);
        Assert.Equal(3, drawing.Labels.Count);
    }

    private static List<(Vector3d A, Vector3d B)> Segments(Scene3DVertex[] v, Scene3DModel scene)
    {
        var s = new List<(Vector3d, Vector3d)>();
        for (int i = 0; i + 1 < v.Length; i += 2)
            s.Add((W(scene, v[i]), W(scene, v[i + 1])));
        return s;
    }

    private readonly record struct Vector3d(double X, double Y, double Z);

    private static Vector3d W(Scene3DModel s, Scene3DVertex v) { var (x, y, z) = s.ToWorld(new Vector3(v.X, v.Y, v.Z)); return new(x, y, z); }

    /// <summary>The drawn coordinates (float, scene-local) round to the grid's own lines, one to one.</summary>
    private static void AssertSameLines(IReadOnlyList<double> grid, List<double> drawn)
    {
        Assert.Equal(grid.Count, drawn.Count);
        var sorted = drawn.Order().ToList();
        double span = grid[^1] - grid[0];
        for (int i = 0; i < grid.Count; i++) Assert.True(Math.Abs(grid[i] - sorted[i]) <= 1e-6 * span, $"line {i}: {sorted[i]} vs {grid[i]}");
    }

    private static bool OnAGridPlane(Vector3d a, Vector3d b, IReadOnlyList<double> lines, Func<Vector3d, double> c)
    {
        double tol = 1e-6 * (lines[^1] - lines[0]);
        return lines.Any(l => Math.Abs(c(a) - l) <= tol && Math.Abs(c(b) - l) <= tol);
    }

    // ── 9. picking ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate9_ACpuRayThroughAPixel_HitsWhatTheIdPassNames()
    {
        var g = CaseA(_root);
        var scene = Scene3DBuilder.Build(g.Problem!, 1, g.Origins);
        var view = new Viewer3DViewState();
        view.Adopt(scene, null);
        var cam = Camera3D.Fit(scene.ContentMin, scene.ContentMax, 1.6f);
        const int w = 160, h = 100;
        int hits = 0, wire = 0;
        for (int py = 1; py < h; py += 2)
            for (int px = 1; px < w; px += 2)
            {
                uint gpu = Scene3DPicking.IdAtPixel(scene, cam, px, py, w, h, view.Visible);
                var (cpu, _) = Scene3DPicking.Pick(scene, cam, px, py, w, h, view.Visible);
                Assert.Equal(gpu, cpu);
                if (cpu != 0) hits++;
                if (scene.Object(cpu)?.Kind == Scene3DKind.Wire) wire++;
            }
        Assert.True(hits > 20, $"only {hits} pixels hit anything");
        Assert.True(wire > 0, "no pixel found the bond wire");
    }

    // ── the camera's standard views ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(StandardView3D.Top, 1, 0, 0, 0, 1, 0)]
    [InlineData(StandardView3D.Bottom, 1, 0, 0, 0, -1, 0)]
    [InlineData(StandardView3D.Front, 1, 0, 0, 0, 0, 1)]
    [InlineData(StandardView3D.Back, -1, 0, 0, 0, 0, 1)]
    [InlineData(StandardView3D.Right, 0, 1, 0, 0, 0, 1)]
    [InlineData(StandardView3D.Left, 0, -1, 0, 0, 0, 1)]
    public void StandardViews_PutTheAxesWhereTheToolbarSays(StandardView3D v, float rx, float ry, float rz, float ux, float uy, float uz)
    {
        var c = new Camera3D { Distance = 1, FovY = Camera3D.DefaultFovY, SceneRadius = 0.5f };
        c.SetStandardView(v);
        Assert.True(Vector3.Distance(new Vector3(rx, ry, rz), c.Right) < 1e-6, $"{v}: right is {c.Right}");
        Assert.True(Vector3.Distance(new Vector3(ux, uy, uz), c.Up) < 1e-6, $"{v}: up is {c.Up}");
    }

    [Fact]
    public void Orthographic_KeepsTheScaleAtTheTarget_AndTheRayHitsWhatIsUnderThePixel()
    {
        var c = Camera3D.Fit(new Vector3(-1), new Vector3(1), 1.5f);
        foreach (var proj in new[] { Projection3D.Perspective, Projection3D.Orthographic })
        {
            c.Projection = proj;
            // A point on the focal plane under a pixel projects back to that pixel.
            var p = c.PointOnFocalPlane(100, 40, 300, 200);
            var (x, y, visible) = c.Project(p, 300, 200);
            Assert.True(visible);
            Assert.Equal(100f, x, 2);
            var (o, d) = c.Ray(99.5f, 39.5f, 300, 200);
            float t = Vector3.Dot(c.Target - o, c.Forward) / Vector3.Dot(d, c.Forward);
            Assert.True(Vector3.Distance(o + d * t, p) < 1e-4f, $"{proj}: ray misses the focal point");
        }
    }
}
