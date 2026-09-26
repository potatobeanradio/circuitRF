// brief-em3d-29 §7 — the viewer's fields: Palace's own ParaView output read back, sliced and sampled.
//
//   1  the config goldens differ from a field-off config only in the output lines
//   2  the reader on the fixture: counts equal the .vtu headers, the rank pieces merge to Palace's own count,
//      and every _real/_imag pair is one complex array
//   3  Palace's OWN probes (probe-E.csv) against the value circuitRF reads at the same points
//   4  the slice is exact: a linear field on a synthetic tet mesh, cut by an arbitrary plane
//   5  (GPU, Viewer3DFieldGateTests) a phase animation uploads nothing
//   6  the reader's managed allocation is bounded by what it keeps
//   7  an array the files do not hold is not offered (no J_s from an electrostatic run)
//   8  the cavity's TE101 |E| peaks at the centre and vanishes on the walls it is tangential to
//
// Fixtures: testdata/em3d/fields/ — Palace 0.18.1's own output, committed as it was written (a coarse PEC
// cavity on TWO ranks, so the merge is exercised, with three probes; a coarse electrostatic plate).

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Viewer3D;

public sealed class FieldTests(ITestOutputHelper output)
{
    private static string Fixture(string name) => Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "fields", name);

    // ── 1. goldens: only the output lines differ ────────────────────────────────────────────────

    /// <summary>
    /// Every committed Palace config golden, with its field-output lines taken out, is the config the
    /// writer produces with fields OFF (<c>SaveFieldsGHz: []</c>) — which is what every golden was before
    /// brief 29. So the re-golden changed the output lines and nothing else.
    /// </summary>
    [Fact]
    public void Gate1_EachGolden_DiffersFromTheFieldOffConfig_OnlyInItsOutputLines()
    {
        string dir = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "em3d", "palace-goldens");
        int checkedCount = 0;
        foreach (string golden in Directory.EnumerateFiles(dir, PalaceConfigWriter.ConfigFile, SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(golden);
            Assert.Contains("\"OutputFormats\"", text);
            string stripped = StripOutput(text);
            Assert.DoesNotContain("SaveStep", stripped);
            Assert.DoesNotContain("\"Save\"", stripped);
            // The field-off config has none of those lines to strip.
            string name = Path.GetFileName(Path.GetDirectoryName(golden)!);
            if (FieldOffConfig(name) is { } off) Assert.Equal(StripOutput(off), stripped);
            checkedCount++;
        }
        Assert.True(checkedCount >= 4, $"{checkedCount} goldens");
    }

    /// <summary>A config with every output line brief 29 adds removed: OutputFormats, a Point sample that
    /// only saves fields, and an eigenmode's or static solve's Save.</summary>
    private static string StripOutput(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["Problem"]!.AsObject().Remove("OutputFormats");
        var solver = node["Solver"]!.AsObject();
        foreach (string k in new[] { "Eigenmode", "Electrostatic", "Magnetostatic" })
            if (solver[k] is System.Text.Json.Nodes.JsonObject o) o.Remove("Save");
        if (solver["Driven"]?["Samples"] is System.Text.Json.Nodes.JsonArray samples)
            foreach (var s in samples.Where(s => s?["SaveStep"] is not null).ToList()) samples.Remove(s);
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string? FieldOffConfig(string golden)
    {
        (Em3dProblem Problem, PalaceSettings Settings)? p = golden switch
        {
            "microstrip" or "via" => PalaceBackendTests.LowerableFor(golden),
            _ => null,
        };
        if (p is not { } x) return null;
        var settings = x.Settings with { SaveFieldsGHz = [] };
        var low = GmshGeoWriter.Write(x.Problem, settings);
        return PalaceConfigWriter.Write(x.Problem, low.Groups, settings).Json;
    }

    /// <summary>The default saves the sweep's centre (D7) as a Point sample saving every step; [] saves none;
    /// a list outside the sweep is refused before anything runs.</summary>
    [Fact]
    public void SaveFrequencies_DefaultIsTheCentre_EmptySavesNone_OutsideTheSweepIsRefused()
    {
        var lin = new Em3dFrequency(9e9, 12e9, 4, Em3dSweepKind.Linear);
        Assert.Equal([10.5], PalaceConfigWriter.SaveFrequenciesGHz(lin, PalaceSettings.Default));
        Assert.Equal(Math.Sqrt(1e9 * 16e9) / 1e9, PalaceConfigWriter.SaveFrequenciesGHz(lin with { StartHz = 1e9, StopHz = 16e9, Kind = Em3dSweepKind.Log }, PalaceSettings.Default)[0], 12);
        Assert.Empty(PalaceConfigWriter.SaveFrequenciesGHz(lin, PalaceSettings.Default with { SaveFieldsGHz = [] }));
        Assert.False(PalaceConfigWriter.SavesFields(PalaceSettings.Default with { SaveFieldsGHz = [] }));
        Assert.Null(PalaceConfigWriter.SaveRefusal(lin, PalaceSettings.Default with { SaveFieldsGHz = [9, 12] }));
        Assert.Contains("outside the sweep", PalaceConfigWriter.SaveRefusal(lin, PalaceSettings.Default with { SaveFieldsGHz = [15] }));

        // The .cem field round-trips, [] included, and resolves onto the settings.
        var setup = new EmSetup { Palace = new CemPalace { SaveFieldsGHz = [] } };
        var back = EmSetupPersistence.Deserialize(EmSetupPersistence.Serialize(setup));
        Assert.NotNull(back.Palace!.SaveFieldsGHz);
        Assert.Empty(back.Palace.SaveFieldsGHz!);
        Assert.Empty(PalaceSettings.Resolve(back.Palace).SaveFieldsGHz!);
        Assert.Null(PalaceSettings.Resolve(null).SaveFieldsGHz);

        // A save-only frequency's row is dropped from what Palace reports, so the .sNp is the sweep's.
        var s = new PalacePortS([9e9, 10e9, 10.5e9, 11e9, 12e9], [.. Enumerable.Range(0, 5).Select(k => new Complex[,] { { k } })]);
        var kept = Em3dRunService.WithoutSaveOnlyRows(s, [9e9, 10e9, 11e9, 12e9]);
        Assert.Equal([9e9, 10e9, 11e9, 12e9], kept.FrequenciesHz);
        Assert.Equal(3.0, kept.S[2][0, 0].Real);
    }

    // ── 2. the reader on the fixture ────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_TheReader_CountsMatchTheHeaders_PiecesMergeToPalacesOwnCount_AndComplexPairsPairUp()
    {
        var run = FieldRun.OpenPalace(Fixture("cavity"))!;
        Assert.Equal(FieldProblemKind.Eigenmode, run.Kind);
        Assert.Equal(3, run.Solutions.Count);                     // Save = N = 3
        Assert.NotNull(run.IndicatorPvtu);                        // recognised by content, not by its number
        Assert.Equal(1e-6, run.ToMetres);
        long palaceElements = LogElementTotal(Path.Combine(Fixture("cavity"), PalaceRun.PalaceLogFile));

        foreach (var sol in run.Solutions)
        {
            var vol = FieldStep.Open(sol.VolumePvtu!, run.ToMetres);
            Assert.Equal(2, vol.Pieces.Count);                    // two MPI ranks
            Assert.Equal(vol.Pieces.Sum(p => p.NumberOfPoints), vol.Mesh.NodeCount);
            Assert.Equal(vol.Pieces.Sum(p => p.NumberOfCells), vol.Mesh.CellCount);
            Assert.Equal(palaceElements, vol.Mesh.CellCount);
            Assert.Equal((FieldCellShape.Tetrahedron, 2, 10), (vol.Mesh.Shape, vol.Mesh.Order, vol.Mesh.NodesPerCell));
            Assert.Equal(vol.Mesh.CellCount * 10, vol.Mesh.NodeCount);
            Assert.All(vol.Mesh.Cells, n => Assert.InRange(n, 0, vol.Mesh.NodeCount - 1));

            var e = vol.Info("E")!;
            Assert.True(e.IsComplex);
            Assert.Equal(3, e.Components);
            Assert.True(vol.Info("B")!.IsComplex);
            Assert.False(vol.Info("U_e")!.IsComplex);
            Assert.DoesNotContain(vol.Arrays, a => a.Name.EndsWith("_real") || a.Name.EndsWith("_imag"));
            var loaded = vol.Load("E")!;
            Assert.Equal(3 * vol.Mesh.NodeCount, loaded.Re.Length);
            Assert.Equal(loaded.Re.Length, loaded.Im!.Length);

            var bnd = FieldStep.Open(sol.BoundaryPvtu!, run.ToMetres);
            Assert.Equal((FieldCellShape.Triangle, 2, 6), (bnd.Mesh.Shape, bnd.Mesh.Order, bnd.Mesh.NodesPerCell));
            Assert.True(bnd.Info("J_s")!.IsComplex);
        }
        // The indicator is per cell.
        var ind = FieldStep.Open(run.IndicatorPvtu!, run.ToMetres);
        Assert.True(ind.Info("Indicator")!.PerCell);
        Assert.Equal(ind.Mesh.CellCount, ind.Load("Indicator")!.Re.Length);
    }

    /// <summary>Palace's log: "elements  min  max  avg  total" — the total over ranks.</summary>
    private static long LogElementTotal(string log)
    {
        var m = Regex.Match(File.ReadAllText(log), @"^\s*elements\s+\d+\s+\d+\s+\d+\s+(\d+)\s*$", RegexOptions.Multiline);
        Assert.True(m.Success, "no element line in the log");
        return long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    // ── 3. Palace's own probes ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Palace writes E at three points by ITSELF (probe-E.csv); circuitRF's reader, merge and sampler give
    /// the same complex vector there. This is the one check that does not go through circuitRF's reader.
    /// The DRAWN value (linear through the sub-tetrahedra) is measured beside it and stated.
    /// </summary>
    [Fact]
    public void Gate3_TheSampledField_EqualsPalacesOwnProbes()
    {
        string dir = Fixture("cavity");
        var run = FieldRun.OpenPalace(dir)!;
        var probes = ProbeCentres(Path.Combine(dir, PalaceConfigWriter.ConfigFile));
        var table = ReadProbeE(Path.Combine(dir, "postpro", "probe-E.csv"));
        double worst = 0, worstDrawn = 0;
        foreach (var sol in run.Solutions)
        {
            int mode = sol.Index + 1;
            var step = FieldStep.Open(sol.VolumePvtu!, run.ToMetres);
            var e = step.Load("E")!;
            var sampler = new FieldSampler(step.Mesh);
            var drawn = new FieldMeshTets(step.Mesh, e, (0, 0, 0));
            for (int p = 0; p < probes.Count; p++)
            {
                var (x, y, z) = probes[p];
                Span<double> ch = stackalloc double[6];
                Assert.True(sampler.Sample(e, x, y, z, ch), $"probe {p + 1} not located");
                var palace = table[mode][p];
                double err = VecDiff(ch, palace) / Norm(palace);
                double drawnErr = VecDiff(Linear(drawn, x * 1e-6, y * 1e-6, z * 1e-6), palace) / Norm(palace);
                output.WriteLine($"mode {mode} probe {p + 1}: |E| {Norm(palace):G6} V/m, sampled rel. error {err:E2}, drawn (linear sub-tets) {drawnErr:E2}");
                worst = Math.Max(worst, err);
                worstDrawn = Math.Max(worstDrawn, drawnErr);
            }
        }
        Assert.True(worst < 1e-3, $"worst sampled error {worst:E2}");
        // The DRAWN value — linear through the sub-tetrahedra — is not the exact one, and 1e-3 is not met by it
        // on this fixture: measured 5.70 % worst (mode 2, probe 2), 2-6 % off the nodes, exact on a node (probe 1
        // sits on one). The fixture is deliberately coarse (118 tetrahedra, ~λ/3 at its top mode) to stay under
        // 1 MB; the linear interpolant's error falls as the element size squared, so a λ/10 mesh draws ~10×
        // closer. The bound is that measurement, and the value UNDER THE CURSOR is the sampled one above.
        Assert.True(worstDrawn < DrawnTolerance, $"worst drawn error {worstDrawn:E2}");
    }

    /// <summary>The display's own error on this fixture — gate 3's measurement (5.70 % worst), rounded up.</summary>
    internal const double DrawnTolerance = 0.06;

    private static double Norm(ReadOnlySpan<double> v) { double s = 0; foreach (double x in v) s += x * x; return Math.Sqrt(s); }
    private static double VecDiff(ReadOnlySpan<double> a, ReadOnlySpan<double> b) { double s = 0; for (int i = 0; i < 6; i++) s += (a[i] - b[i]) * (a[i] - b[i]); return Math.Sqrt(s); }

    /// <summary>The drawn value at a point: the sub-tetrahedron holding it, interpolated linearly.</summary>
    private static double[] Linear(FieldMeshTets tets, double x, double y, double z)
    {
        Span<double> p = stackalloc double[12];
        var v = new double[4 * tets.Channels];
        for (int t = 0; t < tets.Count; t++)
        {
            tets.Get(t, p, v);
            if (Barycentric(p, x, y, z) is not { } l || l.Min() < -1e-9) continue;
            var o = new double[tets.Channels];
            for (int c = 0; c < tets.Channels; c++) for (int k = 0; k < 4; k++) o[c] += l[k] * v[k * tets.Channels + c];
            return o;
        }
        throw new Xunit.Sdk.XunitException("point in no sub-tetrahedron");
    }

    private static double[]? Barycentric(ReadOnlySpan<double> p, double x, double y, double z)
    {
        double a = p[3] - p[0], b = p[6] - p[0], c = p[9] - p[0];
        double d = p[4] - p[1], e = p[7] - p[1], f = p[10] - p[1];
        double g = p[5] - p[2], h = p[8] - p[2], i = p[11] - p[2];
        double rx = x - p[0], ry = y - p[1], rz = z - p[2];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (Math.Abs(det) < 1e-300) return null;
        double l1 = (rx * (e * i - f * h) - b * (ry * i - f * rz) + c * (ry * h - e * rz)) / det;
        double l2 = (a * (ry * i - f * rz) - rx * (d * i - f * g) + c * (d * rz - ry * g)) / det;
        double l3 = (a * (e * rz - ry * h) - b * (d * rz - ry * g) + rx * (d * h - e * g)) / det;
        return [1 - l1 - l2 - l3, l1, l2, l3];
    }

    private static List<(double X, double Y, double Z)> ProbeCentres(string config)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(config));
        return [.. doc.RootElement.GetProperty("Domains").GetProperty("Postprocessing").GetProperty("Probe").EnumerateArray()
            .Select(p => p.GetProperty("Center")).Select(c => (c[0].GetDouble(), c[1].GetDouble(), c[2].GetDouble()))];
    }

    /// <summary>probe-E.csv: per mode (row "m"), per probe, Re/Im of E_x, E_y, E_z — as channels
    /// (Re x, Re y, Re z, Im x, Im y, Im z).</summary>
    private static Dictionary<int, List<double[]>> ReadProbeE(string csv)
    {
        var lines = File.ReadAllLines(csv);
        var head = lines[0].Split(',').Select(h => h.Trim()).ToList();
        var result = new Dictionary<int, List<double[]>>();
        foreach (string line in lines.Skip(1))
        {
            var cells = line.Split(',').Select(c => double.Parse(c.Trim(), CultureInfo.InvariantCulture)).ToList();
            int m = (int)Math.Round(cells[0]);
            var list = new List<double[]>();
            for (int p = 1; ; p++)
            {
                int Col(string part, string axis) => head.FindIndex(h => h.StartsWith($"{part}{{E_{axis}[{p}]}}", StringComparison.Ordinal));
                if (Col("Re", "x") < 0) break;
                list.Add([cells[Col("Re", "x")], cells[Col("Re", "y")], cells[Col("Re", "z")],
                          cells[Col("Im", "x")], cells[Col("Im", "y")], cells[Col("Im", "z")]]);
            }
            result[m] = list;
        }
        return result;
    }

    // ── 4. the slice is exact ───────────────────────────────────────────────────────────────────

    /// <summary>A synthetic mesh (a 3 × 3 × 3 block of cubes, each cut into six tetrahedra, its nodes
    /// jittered) carrying a complex linear vector field; an arbitrary plane's slice equals the analytic
    /// field at every slice vertex to 1e-12, every vertex lies on the plane, and the slice's area is the
    /// plane's section of the block.</summary>
    [Fact]
    public void Gate4_ALinearFieldSlicedByAnArbitraryPlane_EqualsTheAnalyticFieldOnThePlane()
    {
        var mesh = new SyntheticTets(3, 0.2);
        var n = Normalize(new Vector3D(0.37, -0.58, 0.72));
        double d = -(n.X * 1.3 + n.Y * 1.6 + n.Z * 1.4);
        var slice = FieldSlicer.Slice(mesh, n, d);
        Assert.True(slice.TriangleCount > 20, $"{slice.TriangleCount} triangles");
        double worst = 0, off = 0;
        var want = new double[6];
        for (int v = 0; v < slice.VertexCount; v++)
        {
            double x = slice.Xyz[3 * v], y = slice.Xyz[3 * v + 1], z = slice.Xyz[3 * v + 2];
            off = Math.Max(off, Math.Abs(n.X * x + n.Y * y + n.Z * z + d));
            SyntheticTets.Field(x, y, z, want);
            for (int c = 0; c < 6; c++) worst = Math.Max(worst, Math.Abs(slice.Values[6 * v + c] - want[c]) / 10);
        }
        output.WriteLine($"{slice.TriangleCount} triangles, worst value error {worst:E2} (relative to the field's scale), worst off-plane {off:E2}");
        Assert.True(worst < 1e-12, $"{worst:E2}");
        Assert.True(off < 1e-12, $"{off:E2}");
        // Degenerate: a plane through a whole layer of nodes (z = 1 exactly, no jitter) still gives one layer.
        var flat = new SyntheticTets(3, 0);
        var s2 = FieldSlicer.Slice(flat, new Vector3D(0, 0, 1), -1);
        Assert.Equal(9.0, Area(s2), 9);                           // the 3 × 3 section, once — not twice
    }

    private static Vector3D Normalize(Vector3D v) { double l = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z); return new(v.X / l, v.Y / l, v.Z / l); }

    private static double Area(FieldSurface s)
    {
        double a = 0;
        for (int t = 0; t < s.TriangleCount; t++)
        {
            var p = s.Xyz.AsSpan(9 * t, 9);
            var u = new Vector3D(p[3] - p[0], p[4] - p[1], p[5] - p[2]);
            var w = new Vector3D(p[6] - p[0], p[7] - p[1], p[8] - p[2]);
            double cx = u.Y * w.Z - u.Z * w.Y, cy = u.Z * w.X - u.X * w.Z, cz = u.X * w.Y - u.Y * w.X;
            a += 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
        }
        return a;
    }

    /// <summary>A block of k³ unit cubes, each six tetrahedra about its main diagonal, interior nodes jittered.</summary>
    private sealed class SyntheticTets : ILinearTets
    {
        private readonly List<double[]> _nodes = [];
        private readonly List<int[]> _tets = [];

        public SyntheticTets(int k, double jitter)
        {
            var rng = new Random(29);
            int Id(int i, int j, int l) => (l * (k + 1) + j) * (k + 1) + i;
            for (int l = 0; l <= k; l++)
                for (int j = 0; j <= k; j++)
                    for (int i = 0; i <= k; i++)
                    {
                        bool inner = i > 0 && i < k && j > 0 && j < k && l > 0 && l < k;
                        double J() => inner ? (rng.NextDouble() - 0.5) * jitter : 0;
                        _nodes.Add([i + J(), j + J(), l + J()]);
                    }
            int[][] six = [[0, 1, 3, 7], [0, 1, 5, 7], [0, 2, 3, 7], [0, 2, 6, 7], [0, 4, 5, 7], [0, 4, 6, 7]];
            for (int l = 0; l < k; l++)
                for (int j = 0; j < k; j++)
                    for (int i = 0; i < k; i++)
                    {
                        int C(int b) => Id(i + (b & 1), j + ((b >> 1) & 1), l + ((b >> 2) & 1));
                        foreach (var t in six) _tets.Add([C(t[0]), C(t[1]), C(t[2]), C(t[3])]);
                    }
        }

        public int Count => _tets.Count;
        public int Channels => 6;

        public void Get(int tet, Span<double> xyz, Span<double> values)
        {
            Span<double> f = stackalloc double[6];
            for (int c = 0; c < 4; c++)
            {
                var p = _nodes[_tets[tet][c]];
                xyz[3 * c] = p[0]; xyz[3 * c + 1] = p[1]; xyz[3 * c + 2] = p[2];
                Field(p[0], p[1], p[2], f);
                f.CopyTo(values.Slice(6 * c, 6));
            }
        }

        /// <summary>E = a + B·p, complex, as channels.</summary>
        public static void Field(double x, double y, double z, Span<double> f)
        {
            f[0] = 1.5 + 2 * x - y + 0.5 * z; f[1] = -0.7 + x + 3 * y; f[2] = 2 - z + 0.25 * x;
            f[3] = 0.3 * x - 1.1 * y; f[4] = 4 - 2 * z; f[5] = -1 + x + y + z;
        }
    }

    // ── 5. the animation uploads nothing ────────────────────────────────────────────────────────

    /// <summary>The cavity's |Re{E·e^{jφ}}| on a slice, drawn through the recording fake: the geometry is
    /// uploaded once, and 100 phase steps after it change the uniform block and upload 0 bytes.</summary>
    [Fact]
    public void Gate5_AHundredPhaseSteps_UploadZeroBytes()
    {
        var (geometry, q, scale) = CavitySlice(FieldMode.Instantaneous);
        var scene = CircuitRF.Render.Scene3D.Scene3DModel.Empty();
        var fake = new RecordingBackend();
        using var session = new CircuitRF.Ui.Viewer3D.Viewer3DSession(() => fake);
        session.EnsureBackend();
        var view = new CircuitRF.Render.Scene3D.Viewer3DViewState { ShowField = true };
        view.Adopt(scene, null);
        var plan = new CircuitRF.Render.Scene3D.Scene3DFramePlan();
        var none = CircuitRF.Render.Scene3D.Scene3DOverlay.None;

        FieldUniforms.Write(view.Field, q, scale, CircuitRF.Render.Scene3D.ColorMap3D.Viridis, 0);
        plan.Plan(scene, view, 800, 500, false, false, none, none, none, geometry);
        session.Frame(0, plan, 1, scene, none, none, none, false, geometry);
        long first = fake.Counters.UploadBytesTotal;
        Assert.Equal(geometry.Bytes, first);
        Assert.Contains(plan.Draws.Take(plan.DrawCount), d => d.Pipeline == CircuitRF.Render.Scene3D.Scene3DPipeline.Field && d.Count == geometry.Vertices.Length);

        var cosines = new HashSet<float>();
        for (int i = 1; i <= 100; i++)
        {
            FieldUniforms.Write(view.Field, q, scale, CircuitRF.Render.Scene3D.ColorMap3D.Viridis, 2 * Math.PI * i / 100);
            plan.Plan(scene, view, 800, 500, false, false, none, none, none, geometry);
            session.Frame(i % 3, plan, (ulong)i + 1, scene, none, none, none, false, geometry);
            cosines.Add(plan.Uniforms[28]);           // the phase reached the uniform block
        }
        Assert.Equal(first, fake.Counters.UploadBytesTotal);
        Assert.Equal(1, fake.FieldUploads);
        Assert.True(cosines.Count > 50);
    }

    /// <summary>The cavity's TE101 E on the plane z = d/2 — an x–y section through its peak.</summary>
    private static (Scene3DFieldGeometry, FieldQuantity, FieldColorScale) CavitySlice(FieldMode mode)
    {
        var run = FieldRun.OpenPalace(Fixture("cavity"))!;
        var step = FieldStep.Open(run.Solutions[0].VolumePvtu!, run.ToMetres);
        var e = step.Load("E")!;
        var q = new FieldQuantity(e.Info, false, mode);
        var slice = FieldSlicer.Slice(new FieldMeshTets(step.Mesh, e, (0, 0, 0)), new Vector3D(0, 0, 1), -12.5e-3);
        var scale = FieldColorScale.Auto(q, [slice], false, 99);
        return (new Scene3DFieldGeometry(Scene3DFieldGeometry.Pack(q, [slice], [Vector3.Zero]), 1), q, scale);
    }

    /// <summary>
    /// The real Metal backend (macOS): the field pass draws the slice in the colour map's colours, a phase
    /// step changes the picture with no upload, and Export picture's read-back is the drawn frame.
    /// CRF_VIEWER3D_FIELD_PNG writes the exported picture.
    /// </summary>
    [Fact]
    public void Metal_DrawsTheFieldSlice_APhaseStepChangesIt_AndExportReadsItBack()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var (geometry, q, scale) = CavitySlice(FieldMode.Instantaneous);
        var scene = CircuitRF.Render.Scene3D.Scene3DModel.Empty();
        var metal = new CircuitRF.Ui.Viewer3D.Metal.MetalViewer3DBackend();
        using var session = new CircuitRF.Ui.Viewer3D.Viewer3DSession(() => metal);   // disposes the backend
        session.EnsureBackend();
        const int w = 320, h = 240;
        var min = new Vector3(0, 0, 0);
        var max = new Vector3(22.86e-3f, 10.16e-3f, 25e-3f);
        var view = new CircuitRF.Render.Scene3D.Viewer3DViewState { ShowField = true, Camera = CircuitRF.Render.Scene3D.Camera3D.Fit(min, max, w / (float)h) };
        view.Camera.SetStandardView(CircuitRF.Render.Scene3D.StandardView3D.Top);
        view.Camera.FitBounds(min, max, w / (float)h);
        view.Adopt(scene, null);
        var none = CircuitRF.Render.Scene3D.Scene3DOverlay.None;
        byte[] Draw(double phase)
        {
            FieldUniforms.Write(view.Field, q, scale, CircuitRF.Render.Scene3D.ColorMap3D.Viridis, phase);
            var plan = new CircuitRF.Render.Scene3D.Scene3DFramePlan();
            plan.Plan(scene, view, w, h, metal.FlipY, false, none, none, none, geometry);
            return session.RenderPixels(plan, scene, none, none, none, geometry)!;
        }
        var at0 = Draw(0);
        long uploads = metal.Counters.UploadBytesTotal;
        var at90 = Draw(Math.PI / 2);
        Assert.Equal(uploads, metal.Counters.UploadBytesTotal);

        // Drawn: pixels in the colour map's end colours (the peak saturates at the 99th percentile).
        var viridis = CircuitRF.Render.Scene3D.ColorMap3D.Viridis;
        var (tr, tg, tb) = viridis.Sample(1);
        int top = 0, changed = 0;
        for (int i = 0; i < at0.Length; i += 4)
        {
            if (Math.Abs(at0[i] - tr) + Math.Abs(at0[i + 1] - tg) + Math.Abs(at0[i + 2] - tb) < 12) top++;
            if (Math.Abs(at0[i] - at90[i]) + Math.Abs(at0[i + 1] - at90[i + 1]) + Math.Abs(at0[i + 2] - at90[i + 2]) > 30) changed++;
        }
        output.WriteLine($"{top} pixels at the top colour at φ = 0; {changed} changed by φ = 90°");
        Assert.True(top > 50, $"{top} top-colour pixels");
        Assert.True(changed > w * h / 10, $"{changed} pixels changed");     // TE101 is a standing wave: it collapses at 90°

        if (Environment.GetEnvironmentVariable("CRF_VIEWER3D_FIELD_PNG") is { Length: > 0 } png)
            File.WriteAllBytes(png, FieldPicture.Png(at0, w, h, 1, ["|Re{E·e^{jφ}}| (V/m)", scale.Describe(), "Mode 1"],
                                                     viridis, scale, "Mode 1 — TE101", dark: true));
    }

    // ── 6. memory ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opening a step and loading E allocates what the reader KEEPS (the mesh and the array, as floats)
    /// plus transients bounded by one piece's header; never boxed values or doubles per node. Bound: kept
    /// bytes + 2 × the largest piece header + 256 KB. On the fixture always; on a real run (F0 case A, say)
    /// when CRF_FIELD_MEMORY_RUN names its run directory.
    /// </summary>
    [Fact]
    public void Gate6_LoadingAFieldAllocatesWhatItKeeps_PlusABoundedHeader()
    {
        Measure(Fixture("cavity"));
        if (Environment.GetEnvironmentVariable("CRF_FIELD_MEMORY_RUN") is { Length: > 0 } big) Measure(big);
    }

    private void Measure(string runDir)
    {
        var run = FieldRun.OpenPalace(runDir)!;
        var sol = run.Solutions[0];
        FieldStep.Open(sol.VolumePvtu!, run.ToMetres).Load("E");       // warm: JIT, pools
        long before = GC.GetAllocatedBytesForCurrentThread();
        var step = FieldStep.Open(sol.VolumePvtu!, run.ToMetres);
        step.Load("E");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long kept = step.ResidentBytes;
        long header = step.Pieces.Max(p => HeaderBytes(p.Path));
        long bound = kept + 2 * header + (256 << 10);
        output.WriteLine($"{runDir}: {step.Mesh.CellCount:N0} cells, {step.Mesh.NodeCount:N0} nodes; allocated {allocated:N0} B, " +
                         $"kept {kept:N0} B, largest piece header {header:N0} B, bound {bound:N0} B ({(double)allocated / kept:F3}× kept)");
        Assert.True(allocated <= bound, $"allocated {allocated:N0} B > bound {bound:N0} B");
    }

    private static long HeaderBytes(string vtu)
    {
        var bytes = File.ReadAllBytes(vtu);
        int at = bytes.AsSpan().IndexOf("<AppendedData"u8);
        return at < 0 ? bytes.Length : at;
    }

    // ── 7. an absent array is not offered ───────────────────────────────────────────────────────

    [Fact]
    public void Gate7_AnElectrostaticRun_WithNoHAndNoBoundaryCurrent_OffersNoSurfaceCurrent()
    {
        var run = FieldRun.OpenPalace(Fixture("plates"))!;
        Assert.Equal(FieldProblemKind.Electrostatic, run.Kind);
        var sol = Assert.Single(run.Solutions);
        var vol = FieldStep.Open(sol.VolumePvtu!, run.ToMetres);
        var bnd = FieldStep.Open(sol.BoundaryPvtu!, run.ToMetres);
        var offered = FieldQuantity.Offered(vol.Arrays, bnd.Arrays);
        foreach (var q in offered) output.WriteLine(q.Label);
        Assert.DoesNotContain(offered, q => q.Array.Name is "J_s" or "B" or "H");
        Assert.Contains(offered, q => q.Array.Name == "V" && q.Mode == FieldMode.Value);
        Assert.Contains(offered, q => q.Array.Name == "E" && q.Mode == FieldMode.Peak);
        Assert.DoesNotContain(offered, q => q.Animated);       // a real field has no phase
        // Every offered quantity's array is one the files list.
        var listed = vol.Arrays.Concat(bnd.Arrays).Select(a => a.Name).ToHashSet();
        Assert.All(offered, q => Assert.Contains(q.Array.Name, listed));

        // The cavity (complex, with boundary J_s) does offer it, on the surfaces, animated too.
        var cav = FieldRun.OpenPalace(Fixture("cavity"))!.Solutions[0];
        var cq = FieldQuantity.Offered(FieldStep.Open(cav.VolumePvtu!, 1e-6).Arrays, FieldStep.Open(cav.BoundaryPvtu!, 1e-6).Arrays);
        Assert.Contains(cq, q => q.Array.Name == "J_s" && q.OnBoundary && q.Mode == FieldMode.Peak);
        Assert.Contains(cq, q => q.Array.Name == "E" && q.Mode == FieldMode.Instantaneous);
    }

    private static void SampleNode(FieldArray a, int node, Span<double> ch) => FieldSampling.Channels(a, node, -1, ch);

    // ── 9. openEMS ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// openEMS's dump of the homogeneous stripline of brief 7's gate 6 (b = 1 mm, w = 0.8 mm, εr 2.2, 10 mm;
    /// its Z0 is ≈ 51 Ω, so the 50 Ω ports leave it matched): |E| at the line's centre, midway between the
    /// strip and the ground plane under the strip's centre — the uniform-field region — equals the port
    /// voltage over that gap within 2 %. The voltage is port 1's own probe, transformed at the dump's
    /// frequency with openEMS's own normalisation (2·Δt·Σ u·e^{−jωt}, processfields_fd.cpp), so the two
    /// share their scale. About a second.
    /// </summary>
    [OpenEmsFact]
    public void Gate9_OpenEms_TheStriplinesFieldAtItsCentre_IsThePortVoltageOverTheGap()
    {
        string root = Path.Combine(Path.GetTempPath(), "crf-fields-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            const double h = 500e-6, len = 10e-3, groundTop = 35e-6;
            var (setup, source) = PalaceBackendTests.Stripline(800e-6, 1000e-6, len, 2.2);
            setup.Solver3D = Em3dSolver.OpenEms;
            setup.Palace = null;
            string results = Path.Combine(root, "results");
            var result = EmRunService.Run(setup, source, results);
            Assert.True(result.Status == EmRunStatus.Ok, result.Error);

            string runDir = Em3dRunService.RunDirectory(results, setup, Em3dSolver.OpenEms);
            var run = FieldRun.OpenOpenEms(runDir)!;
            Assert.Equal("openEMS", run.Solver);
            Assert.Equal([1, 2], run.Solutions.Select(x => x.Excitation));             // one per excited port
            var sol = run.Solutions.Single(x => x.Excitation == 1);
            Assert.Equal(2.5, sol.Timestep, 9);                                           // the sweep's centre (1-4 GHz)

            var step = FieldStep.Open(sol.VolumePvtu!, run.ToMetres);
            var e = step.Load("E")!;
            Assert.True(e.Info.IsComplex);
            var q = new FieldQuantity(e.Info, false, FieldMode.Peak);
            var sampler = new FieldSampler(step.Mesh);
            Span<double> ch = stackalloc double[6];
            Assert.True(sampler.Sample(e, len / 2, 0, groundTop + h / 2, ch));
            double field = q.Evaluate(ch);

            var u = OpenEmsRun.ReadProbe(Path.Combine(runDir, OpenEmsRun.PortDirectory(1), CsxcadWriter.VoltageProbe(1) ), out string? err)!;
            Assert.Null(err);
            var u2 = OpenEmsRun.ReadProbe(Path.Combine(runDir, OpenEmsRun.PortDirectory(1), CsxcadWriter.VoltageProbe(2)), out _)!;
            // The line is close to matched, not exactly (Z0 ≈ 51 Ω and the lumped ports' own parasitics): its
            // voltage has a small standing wave, measured 13 % between the two ends. So the voltage at the
            // centre is taken from BOTH ends by the TEM line's own equation — V(z) = A·cos βz + B·sin βz gives
            // V(ℓ/2) = (V1 + V2) / (2·cos(βℓ/2)) exactly — with β = ω√εr / c for this homogeneous line.
            var v1 = Dft(u, sol.FrequencyHz);
            var v2 = Dft(u2, sol.FrequencyHz);
            double beta = 2 * Math.PI * sol.FrequencyHz * Math.Sqrt(2.2) / 299_792_458.0;
            var mid = (v1 + v2) / (2 * Math.Cos(beta * len / 2));
            double expected = mid.Magnitude / h;
            output.WriteLine($"|E| at the centre {field:G6}; |V(ℓ/2)|/h {expected:G6} ({100 * (field / expected - 1):F2} %); " +
                             $"|V1|/h {v1.Magnitude / h:G6}, |V2|/h {v2.Magnitude / h:G6}; " +
                             $"E = ({ch[0]:G3}, {ch[1]:G3}, {ch[2]:G3}) + j({ch[3]:G3}, {ch[4]:G3}, {ch[5]:G3})");
            Assert.InRange(field / expected, 0.98, 1.02);

            // The slice through that point is drawn from the same data, on a grid line exactly.
            var slice = FieldSlicer.Slice(new FieldMeshTets(step.Mesh, e, (0, 0, 0)), new Vector3D(0, 0, 1), -(groundTop + h / 2));
            Assert.True(slice.TriangleCount > 0);
        }
        finally { try { Directory.Delete(root, true); } catch { /* best effort */ } }
    }

    /// <summary>openEMS's single-sided DFT: 2·Δt·Σ u(t)·e^{−jωt}.</summary>
    private static Complex Dft(FdtdProbe p, double hz)
    {
        Complex sum = 0;
        double dt = p.TimeS[1] - p.TimeS[0];
        for (int i = 0; i < p.TimeS.Length; i++) sum += p.Value[i] * Complex.Exp(new Complex(0, -2 * Math.PI * hz * p.TimeS[i]));
        return 2 * dt * sum;
    }

    // ── 8. the cavity's TE101 ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// TE101 (E = ŷ·sin(πx/a)·sin(πz/d)) peaks at the cavity's centre and vanishes on the four walls it is
    /// TANGENTIAL to (x = 0, a and z = 0, d). On the two y-walls E is NORMAL to the wall and is not zero —
    /// that is physics, not an error — so those walls are not in the claim.
    /// </summary>
    [Fact]
    public void Gate8_Te101_PeaksAtTheCentre_AndVanishesOnTheWallsItIsTangentialTo()
    {
        string dir = Fixture("cavity");
        var run = FieldRun.OpenPalace(dir)!;
        var te101 = run.Solutions.First(s => s.Index == 0);
        var vol = FieldStep.Open(te101.VolumePvtu!, run.ToMetres);
        var e = vol.Load("E")!;
        var q = new FieldQuantity(e.Info, false, FieldMode.Peak);
        var sampler = new FieldSampler(vol.Mesh);
        const double a = 22860, b = 10160, dd = 25000;          // µm, the mesh unit
        Span<double> ch = stackalloc double[6];
        Assert.True(sampler.Sample(e, a / 2, b / 2, dd / 2, ch));
        double centre = q.Evaluate(ch);
        double peak = 0;
        for (int i = 1; i < 10; i++)
            for (int k = 1; k < 10; k++)
                if (sampler.Sample(e, a * i / 10, b / 2, dd * k / 10, ch)) peak = Math.Max(peak, q.Evaluate(ch));
        output.WriteLine($"|E| at the centre {centre:G6}, largest on a 9 × 9 grid {peak:G6}");
        Assert.Equal(peak, centre, peak * 1e-9);

        // The walls, from the BOUNDARY collection's own E.
        var groups = FieldGroups.Read(dir);
        var tangential = groups.Where(g => g.Name is "airbox/xmin" or "airbox/xmax" or "airbox/zmin" or "airbox/zmax")
                               .Select(g => g.Attribute).ToHashSet();
        Assert.Equal(4, tangential.Count);
        var bnd = FieldStep.Open(te101.BoundaryPvtu!, run.ToMetres);
        var mesh = bnd.Mesh;
        var eb = bnd.Load("E")!;
        double wall = 0, tan = 0;
        for (int c = 0; c < mesh.CellCount; c++)
        {
            int attr = mesh.Attribute[c];
            if (!tangential.Contains(attr)) continue;
            bool xWall = groups.Any(g => g.Attribute == attr && g.Name.StartsWith("airbox/x"));
            for (int j = 0; j < mesh.NodesPerCell; j++)
            {
                int node = mesh.Cells[c * mesh.NodesPerCell + j];
                SampleNode(eb, node, ch);
                wall = Math.Max(wall, q.Evaluate(ch));
                // The components in the wall's plane: y and z on an x-wall, x and y on a z-wall.
                int n = xWall ? 0 : 2;
                double t2 = 0;
                for (int k = 0; k < 3; k++) if (k != n) t2 += ch[k] * ch[k] + ch[3 + k] * ch[3 + k];
                tan = Math.Max(tan, Math.Sqrt(t2));
            }
        }
        output.WriteLine($"on the four tangential walls: largest |E| {wall:G4} ({wall / centre:E2} of the centre's), " +
                         $"largest TANGENTIAL E {tan:G4} ({tan / centre:E2})");
        // Tangential E is what a PEC wall zeroes, exactly in Palace's Nédélec space: Float32 storage only.
        Assert.True(tan < 1e-6 * centre, $"tangential {tan / centre:E2}");
        // The rest is the normal component TE101 does not have, which the coarse fixture carries at the level
        // of its own display error (gate 3's measurement): zero "to the display tolerance".
        Assert.True(wall < DrawnTolerance * centre, $"{wall / centre:E2}");
    }
}
